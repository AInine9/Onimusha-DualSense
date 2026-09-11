-- Onimusha DualSense bridge 1.1.2. Sound-derived haptics and adaptive triggers.
local path = 'onimusha_dualsense_bridge.json'
local enabled, frames, sequence, event_id = true, 0, 0, 0
local candidate, checking, events, errors = -1, false, {}, {}
local session = tostring(os.time()) .. ':' .. tostring(os.clock())
local counters = {adaptive=0, bow=0, spawner=0}
local original_feedback, native_feedback = nil, nil
local control, suppressed = nil, false
local control_read_interval, last_control_read = 3, -1000
local gameplay_allowed, ui_allowed = false, false
local defense_kind, defense_state = 'none', 0
local function classify_defense(bits)
    -- app.CharacterDef.ACTION_STATE_BIT: PARRIED is deliberately excluded.
    if (bits & 536870912)~=0 then return 'dodge' end
    if (bits & 134217728)~=0 then return 'deflect' end
    if (bits & 268435456)~=0 then return 'parry' end
    if (bits & (16777216 | 33554432 | 67108864))~=0 then return 'guard' end
    return 'none'
end
local player_entity, player_character, previous_player, previous_state = nil, nil, nil, nil
local special_kind, special_frame = 'none', -1000
local function special_context()
    if not player_entity or not player_character then return 'none' end
    local bits=player_character:call('getActionState')
    if (bits & 524288)==0 then special_kind='none'; special_frame=-1000; return 'none' end
    -- Do not invoke app.cPlayerCharacterEntity.isDeadHeatAction here. The
    -- live game raised an access violation from that native call. Dead-heat
    -- state is marked by the observed impact callback below instead.
    if special_kind=='deadheat' and frames-special_frame<=600 then return 'deadheat' end
    if special_kind=='issen' and frames-special_frame<=600 then return 'issen' end
    return 'none'
end
local extra_counts, extra_hooks, extra_last = {}, {}, {}
local attack_params, hit_ids, combat_frame = {}, {}, -1000
local last_dump_event, last_dump_frame = -1, -1000
-- Keep this below the 0.75 s companion freshness timeout even around 10 FPS.
local heartbeat_interval = 5
local last_dump_enabled, last_dump_paused, last_dump_gameplay, last_dump_ui = nil, nil, nil, nil
local last_dump_suppressed, last_dump_trigger, last_dump_defense = nil, nil, nil
local sound_pending, player_sound_object = {}, nil
local contact_frame, defense_frame, sound_step_frame, sound_side = -1000, -1000, -1000, false
local pre_suppress_until = -1
local native_bow, bow_until, bow_owner = false, -1, nil
local bow_counts = {starts=0,combat_releases=0,other_releases=0,blocked_steps=0,blocked_other=0}
local bow_last_reason = 'none'
local combat_families = {attack=true,hit=true,contact=true,guard=true,parry=true,
    parry_release=true,deflect=true,damage=true,dodge=true,perfect_dodge=true,
    finisher=true,power=true,special_motion=true}
local sound_routes, routes_token, routes_retry = {}, nil, -1
local function sound_ready() return next(sound_routes)~=nil end
local trace_entries, trace_seq, trace_budget, trace_budget_frame, trace_dropped = {}, 0, 0, -1000, 0
local trace_control_session, trace_ack_session, trace_ack = nil, nil, 0
local last_defense_sample_frame = -1000
local pad_type = sdk.find_type_definition('via.hid.GamePad')
local function safe(f)
    local ok, value = pcall(f)
    if not ok then
        local message = tostring(value)
        if errors[#errors] ~= message then
            errors[#errors+1] = message
            if #errors > 8 then table.remove(errors,1) end
        end
        return nil
    end
    return value
end
local function trace_sampled(kind)
    return gameplay_allowed and (kind=='guard' or kind=='parry' or kind=='deflect' or frames-last_defense_sample_frame<=30)
end
local function trace_enabled(kind)
    if not control or control.defense_trace~=true or control.output_enabled~=true or not trace_sampled(kind) then return false end
    if type(control.timestamp)~='number' then return false end
    local age=os.time()-control.timestamp
    return age>=0 and age<=2
end
local function trace_available(kind)
    if not trace_enabled(kind) then return false end
    if frames-trace_budget_frame>=60 then trace_budget_frame=frames; trace_budget=0 end
    return trace_budget<32
end
local function trace(reason, info)
    safe(function()
        local kind=info and info.kind or defense_kind
        if not trace_enabled(kind) then return end
        if frames-trace_budget_frame>=60 then trace_budget_frame=frames; trace_budget=0 end
        if trace_budget>=32 then
            trace_dropped=trace_dropped+1
            return
        end
        trace_budget=trace_budget+1; trace_seq=trace_seq+1
        local item={seq=trace_seq,frame=frames,reason=reason}
        if info then for key,value in pairs(info) do item[key]=value end end
        trace_entries[#trace_entries+1]=item
        if #trace_entries>32 then table.remove(trace_entries,1); trace_dropped=trace_dropped+1 end
    end)
end
local function set_native_suppression(wanted)
    local pad=sdk.get_native_singleton('via.hid.GamePad')
    if not pad then return end
    native_feedback=sdk.call_native_func(pad,pad_type,'get_ForceFeedbackEnable')
    if wanted then
        if original_feedback==nil then original_feedback=native_feedback end
        if native_feedback then sdk.call_native_func(pad,pad_type,'set_ForceFeedbackEnable',false) end
        native_feedback=sdk.call_native_func(pad,pad_type,'get_ForceFeedbackEnable')
        suppressed=native_feedback==false
    else
        if original_feedback~=nil then
            sdk.call_native_func(pad,pad_type,'set_ForceFeedbackEnable',original_feedback)
            original_feedback=nil
        end
        suppressed=false
    end
end
local function emit(kind, id, metadata)
    event_id = event_id + 1
    local item={seq=event_id, kind=kind, id=id or 0, frame=frames}
    if metadata then for key,value in pairs(metadata) do item[key]=value end end
    events[#events+1] = item
    if #events > 128 then table.remove(events,1) end
end
local function request_switches(info, route)
    local result={}
    if route and route.switches==false then return result end
    local dict=info:call('get_SwitchInfoDict')
    local entries=dict and dict:get_field('_entries')
    if not entries then return result end
    for _,entry in ipairs(entries:get_elements()) do
        local key=entry:get_field('key')
        local value=entry:get_field('value')
        if key and key~=0 and value then result[tostring(key)]=value end
    end
    return result
end
local function hook(td, name, callback)
    local method = td and td:get_method(name)
    if not method then error('Missing method: ' .. name) end
    sdk.hook(method, function(args)
        safe(function() callback(args) end)
    end, function(retval) return retval end)
end
local function same_object(a,b)
    return a and b and a:get_address()==b:get_address()
end
local function end_native_bow(reason, combat)
    bow_owner=nil; bow_until=-1
    if native_bow then
        native_bow=false; bow_last_reason=reason
        local counter=combat and 'combat_releases' or 'other_releases'
        bow_counts[counter]=bow_counts[counter]+1
        emit('native_bow',0,{reason=reason})
    end
end
local function bow_charge_continues()
    -- Stored timer/stage values may outlive a charge. They can only extend a
    -- session opened by a real local start/effect callback, never create one.
    local subweapon=bow_owner and gameplay_allowed and player_entity and player_entity:call('get_SubWeapon') or nil
    if not same_object(subweapon,bow_owner) then return false end
    local timer=subweapon:get_field('_TimerCharge')
    local inner=timer and timer:get_field('_Timer')
    local stage=subweapon:call('getStrongShotType')
    return stage==1 or stage==2 or (inner~=nil and timer:call('get_Enabled')==true)
end
local function extra(id, minimum_frames, metadata, family)
    if not enabled then return end
    local is_ui = id:sub(1,3)=='ui_'
    if is_ui then if not ui_allowed then return end
    elseif not gameplay_allowed then return end
    -- An authenticated combat event takes precedence over stale bow fields.
    -- Cancel the session too, so LateUpdate cannot re-arm it from those fields.
    if native_bow and combat_families[family or id] then
        end_native_bow('event:'..(family or id),true)
    elseif native_bow and frames>bow_until and not bow_charge_continues() then
        end_native_bow('charge_finished',false)
    end
    if native_bow then
        local step=family=='footsteps' or id:match('^foot_') or id:match('^run_')
        local counter=step and 'blocked_steps' or 'blocked_other'
        bow_counts[counter]=bow_counts[counter]+1
        return
    end
    if frames-(extra_last[id] or -1000)<(minimum_frames or 4) then return end
    if id=='attack' or id=='hit' or id=='damage' or id=='guard' or id=='finisher' then combat_frame=frames end
    extra_last[id]=frames
    extra_counts[id]=(extra_counts[id] or 0)+1
    if control and control.output_enabled==true then pre_suppress_until=frames+4 end
    emit('extended',id,metadata)
end
local function entity_is_player(args)
    return gameplay_allowed and same_object(sdk.to_managed_object(args[2]),player_entity)
end
local function module_is_player(args)
    if not gameplay_allowed then return false end
    local module=sdk.to_managed_object(args[2])
    return module and same_object(module:call('get_OwnerCharacter'),player_character)
end
local function extra_hook(type_name,method_name,callback)
    local key=type_name..'.'..method_name
    local ok,err=pcall(function() hook(sdk.find_type_definition(type_name),method_name,callback) end)
    extra_hooks[key]=ok
    if not ok then errors[#errors+1]='Extension unavailable: '..key..': '..tostring(err) end
end
-- Hooks only observe existing game events. They never call an action, change game state,
-- or synthesize feedback from raw controller input.
local function preserve_bow(args)
    if not gameplay_allowed or not player_entity then return end
    local subweapon=sdk.to_managed_object(args[2])
    if not same_object(subweapon,player_entity:call('get_SubWeapon')) then return end
    bow_owner=subweapon; bow_until=frames
    if not native_bow then
        native_bow=true; bow_counts.starts=bow_counts.starts+1; bow_last_reason='charge_callback'
        emit('native_bow',1,{reason=bow_last_reason})
    end
    pre_suppress_until=-1; sound_pending={}
    set_native_suppression(false)
end
extra_hook('app.cPlayerSubWeaponSupporter','startStrongShot',preserve_bow)
extra_hook('app.cPlayerSubWeaponSupporter','requestEffectStrongShot',preserve_bow)
extra_hook('app.EPVExpertFootLandingCustom','play',function(args)
    if sound_ready() then return end
    local owner=sdk.to_managed_object(args[2])
    if not owner or not same_object(owner:get_field('_PlayerEntity'),player_entity) then return end
    if frames-combat_frame<24 then return end
    local step,foot=sdk.to_int64(args[3]),sdk.to_int64(args[4])
    -- Contact and Step indicate planted feet; Lift and Slide do not create footsteps.
    if step~=0 and step~=2 then return end
    if foot<0 or foot>5 then return end
    local move=player_entity:call('getCurrentActionMoveType')
    if move<0 then return end
    extra((move==0 and 'foot_' or 'run_')..(foot%2==0 and 'left' or 'right'),5)
end)
extra_hook('app.CharacterBase','evBaseActionEnter',function(args)
    if not same_object(sdk.to_managed_object(args[2]),player_character) then return end
    attack_params={}
    local action=sdk.to_managed_object(args[3])
    if action then
        local bits=action:call('get_ActStateBit')
        if (bits & (33554432 | 67108864 | 268435456 | 2147483648))~=0 then defense_frame=frames end
        if (bits & 536870912)~=0 then extra('dodge',8) end
    end
end)
extra_hook('app.cPlayerCharacterEntity','evAttackCollision',function(args)
    if not entity_is_player(args) or (sound_ready() and not native_bow) then return end
    local track=sdk.to_managed_object(args[3])
    if not track then return end
    if not track:call('get_IsOn') then return end
    end_native_bow('attack_collision',true)
    if sound_ready() then return end
    local parameter=tostring(track:call('get_AttackParamID'))..':'..tostring(track:call('get_RequestSetID'))
    if not attack_params[parameter] then
        attack_params[parameter]=true
        if frames-(extra_last.hit or -1000)>4 then extra('attack',4) end
    end
end)
extra_hook('app.cPlayerCharacterEntity','onHitAttackPostProcess',function(args)
    if not entity_is_player(args) then return end
    local hit=sdk.to_managed_object(args[3])
    if not hit then return end
    local key=tostring(hit:call('get_AttackUniqueID'))..':'..tostring(hit:call('get_HitID'))
    if hit_ids[key] and frames-hit_ids[key]<120 then return end
    hit_ids[key]=frames
    for id,frame in pairs(hit_ids) do if frames-frame>120 then hit_ids[id]=nil end end
    end_native_bow('player_hit',true)
    contact_frame=frames; combat_frame=frames
    if not sound_ready() then extra('hit',3) end
end)
extra_hook('app.cPlayerGuardController','addDamage',function(args)
    if module_is_player(args) then
        trace('guard_contact',{route='addDamage',family='guard',expected_source='player',observed_source='player',kind=defense_kind,bits=defense_state})
        end_native_bow('guard_contact',true)
        defense_frame=frames; combat_frame=frames
        if not sound_ready() then extra('guard',4) end
    end
end)
extra_hook('app.cPlayerJustDodgeSupporter','executeSuccessJustDodgeAction',function(args)
    if module_is_player(args) then extra('perfect_dodge',8) end
end)
extra_hook('app.cPlayerCharacterEntity','onAddHealth',function(args)
    if not entity_is_player(args) then return end
    local kind,amount=sdk.to_int64(args[3]),sdk.to_int64(args[4])
    if (kind==0 or kind==2) and amount>0 then extra('heal',15) end
    if kind==1 and amount~=0 then contact_frame=frames; extra('damage',18) end
end)
extra_hook('app.cPlayerSoulAbsorptionSupporter','executeSoulAbsorptionSuccess',function(args)
    if module_is_player(args) then extra('soul',6) end
end)
extra_hook('app.SoundGetItemEventHandler','onGetItem',function() extra('pickup',8) end)
extra_hook('app.cPlayerLockOnSupporter','requestLockOnCameraStart',function(args)
    if module_is_player(args) and sdk.to_managed_object(args[3]) then extra('lock_on',8) end
end)
extra_hook('app.cPlayerCharacterEntity','evOniChangeStartEvent',function(args)
    if entity_is_player(args) then extra('power',20) end
end)
extra_hook('app.cPlayerCharacterEntity','deadHeatActionImpactNotice',function(args)
    if same_object(sdk.to_managed_object(args[2]),player_entity) then
        if player_character and (player_character:call('getActionState') & 524288)~=0 then special_kind='deadheat'; special_frame=frames end
        if gameplay_allowed and not sound_ready() then extra('finisher',12) end
    end
end)
extra_hook('app.cPlayerCharacterEntity','attackBreakImpactNotice',function(args)
    if same_object(sdk.to_managed_object(args[2]),player_entity) and player_character then
        if (player_character:call('getActionState') & 524288)~=0 then special_kind='issen'; special_frame=frames end
        if entity_is_player(args) and not sound_ready() then extra('finisher',12) end
    end
end)
-- Observe the actual sound request, including menus whose GUI wrapper methods
-- are inlined. The catalog contains only bank-verified, user-extracted references.
extra_hook('soundlib.SoundManager','postRequestInfo',function(args)
    if not sound_ready() then return end
    local info=sdk.to_managed_object(args[2]) -- static method, first managed parameter
    if not info then return end
    local event_id=tostring(info:call('get_EventId'))
    local route=sound_routes[event_id]
    if not route and player_sound_object then
        if trace_enabled(defense_kind) then
            if trace_available(defense_kind) then
                local ok,unknown_src=pcall(function() return info:call('get_SrcGameObj') end)
                local unknown_name=nil
                if ok and unknown_src then ok,unknown_name=pcall(function() return unknown_src:call('get_Name') end) end
                trace(ok and unknown_name and 'unregistered' or 'trace_error',{event_id=event_id,route='unregistered',family='unknown',expected_source='unknown',observed_source=(ok and unknown_name) or 'diagnostic_accessor_failed',bits=defense_state,kind=defense_kind})
            else trace_dropped=trace_dropped+1 end
        end
        return
    end
    local src=info:call('get_SrcGameObj')
    if not src then
        if route then
            trace('missing_source',{event_id=event_id,route=route.id,family=route.family,expected_source=route.source,observed_source='missing_source',bits=defense_state,kind=defense_kind})
        elseif trace_available(defense_kind) then
            trace('missing_source',{event_id=event_id,route='unregistered',family='unknown',expected_source='unknown',observed_source='missing_source',bits=defense_state,kind=defense_kind})
        elseif trace_enabled(defense_kind) then
            trace_dropped=trace_dropped+1
        end
        return
    end
    local name=src:call('get_Name')
    if name=='Player_00' then player_sound_object=src end
    if not route then
        if trace_enabled(defense_kind) then
            if trace_available(defense_kind) then trace('unregistered',{event_id=event_id,route='unregistered',family='unknown',expected_source='unknown',observed_source=name,bits=defense_state,kind=defense_kind}) else trace_dropped=trace_dropped+1 end
        end
        return
    end
    if route.source=='GUI' then
        if name=='GUI' then extra(route.id,3,nil,route.family) end
        return
    end
    if not gameplay_allowed then return end
    local technique=special_context()
    if route.family=='special_motion' and technique=='none' then return end
    if route.source=='parry_pos' then
        if name~='parry_pos' then trace('source_mismatch',{event_id=event_id,route=route.id,family=route.family,expected_source='parry_pos',observed_source=name,bits=defense_state,kind=defense_kind}); return end
        if not player_character or not player_sound_object then trace('missing_player',{event_id=event_id,route=route.id,family=route.family,expected_source='parry_pos',observed_source=name,bits=defense_state,kind=defense_kind}); return end
        local a=src:call('get_Transform'):call('get_Position')
        local b=player_sound_object:call('get_Transform'):call('get_Position')
        local distance=(a.x-b.x)^2+(a.y-b.y)^2+(a.z-b.z)^2
        if distance>16 then trace('out_of_range',{event_id=event_id,route=route.id,family=route.family,expected_source='parry_pos',observed_source=name,distance_squared=distance,bits=defense_state,kind=defense_kind}); return end
        local bits=player_character:call('getActionState')
        local kind=classify_defense(bits)
        if route.family=='parry_stop' then trace('accepted',{event_id=event_id,route=route.id,family=route.family,expected_source='parry_pos',observed_source=name,bits=bits,kind=kind}); emit('defense_stop',route.stops); return end
        if route.family=='parry' and kind~='parry' then trace('state_mismatch',{event_id=event_id,route=route.id,family=route.family,expected_source='parry_pos',observed_source=name,bits=bits,kind=kind}); return end
        if route.family=='deflect' and kind~='deflect' then trace('state_mismatch',{event_id=event_id,route=route.id,family=route.family,expected_source='parry_pos',observed_source=name,bits=bits,kind=kind}); return end
        -- Release sounds can arrive after the action bit clears; the positional
        -- event itself is the verified Wwise release, not an inferred guard.
        if route.family=='parry_release' and kind=='dodge' then trace('state_mismatch',{event_id=event_id,route=route.id,family=route.family,expected_source='parry_pos',observed_source=name,bits=bits,kind=kind}); return end
        defense_frame=frames; combat_frame=frames
        trace('accepted',{event_id=event_id,route=route.id,family=route.family,expected_source='parry_pos',observed_source=name,bits=bits,kind=kind})
        extra(route.id,1,{switches=request_switches(info,route),defense_kind=kind},route.family)
        return
    end
    if route.source=='player' and name~='Player_00' then trace('source_mismatch',{event_id=event_id,route=route.id,family=route.family,expected_source='Player_00',observed_source=name,bits=defense_state,kind=defense_kind}); return end
    if route.source=='player_effect' and name~='effect_Player_00' then trace('source_mismatch',{event_id=event_id,route=route.id,family=route.family,expected_source='effect_Player_00',observed_source=name,bits=defense_state,kind=defense_kind}); return end
    if route.source=='TrgPos' then
        -- Some contact banks reuse parry_pos for the defense impact. Require
        -- every observed gate; never synthesize haptic feedback from state alone.
        if route.family=='contact' and name=='parry_pos' then
            if not player_character or not player_sound_object then trace('missing_player',{event_id=event_id,route=route.id,family=route.family,expected_source='TrgPos',observed_source=name,bits=defense_state,kind=defense_kind}); return end
            local a=src:call('get_Transform'):call('get_Position')
            local b=player_sound_object:call('get_Transform'):call('get_Position')
            local distance=(a.x-b.x)^2+(a.y-b.y)^2+(a.z-b.z)^2
            if distance>16 then trace('out_of_range',{event_id=event_id,route=route.id,family=route.family,expected_source='TrgPos',observed_source=name,distance_squared=distance,bits=defense_state,kind=defense_kind}); return end
            local bits=player_character:call('getActionState')
            local kind=classify_defense(bits)
            if kind~='parry' and kind~='deflect' then trace('state_mismatch',{event_id=event_id,route=route.id,family=route.family,expected_source='TrgPos',observed_source=name,bits=bits,kind=kind}); return end
            trace('accepted',{event_id=event_id,route=route.id,family=route.family,expected_source='TrgPos',observed_source=name,bits=bits,kind=kind})
            extra(route.id,1,{switches=request_switches(info,route),defense_kind=kind},route.family); defense_frame=frames; combat_frame=frames
            return
        end
        if name~='TrgPos' then trace('source_mismatch',{event_id=event_id,route=route.id,family=route.family,expected_source='TrgPos',observed_source=name,bits=defense_state,kind=defense_kind}); return end
        if not player_sound_object then trace('missing_player',{event_id=event_id,route=route.id,family=route.family,expected_source='TrgPos',observed_source=name,bits=defense_state,kind=defense_kind}); return end
        local a=src:call('get_Transform'):call('get_Position')
        local b=player_sound_object:call('get_Transform'):call('get_Position')
        local distance=(a.x-b.x)^2+(a.y-b.y)^2+(a.z-b.z)^2
        if distance>16 then trace('out_of_range',{event_id=event_id,route=route.id,family=route.family,expected_source='TrgPos',observed_source=name,distance_squared=distance,bits=defense_state,kind=defense_kind}); return end
        if technique~='none' then
            trace('accepted',{event_id=event_id,route=route.id,family=route.family,expected_source='TrgPos',observed_source=name,bits=defense_state,kind=defense_kind}); extra(route.id,1,{switches=request_switches(info,route),technique=technique},route.family); combat_frame=frames
            return
        end
        trace('deferred',{event_id=event_id,route=route.id,family=route.family,expected_source='TrgPos',observed_source=name,bits=defense_state,kind=defense_kind})
        sound_pending[#sound_pending+1]={id=route.id,frame=frames,switches=request_switches(info,route),family=route.family}
        if #sound_pending>16 then table.remove(sound_pending,1) end
        return
    end
    if route.family=='footsteps' then
        if not player_entity or player_entity:call('getCurrentActionMoveType')<0 then return end
        if frames-combat_frame<18 or frames-sound_step_frame<8 then return end
        sound_step_frame=frames; sound_side=not sound_side
        extra(route.id..(sound_side and '_left' or '_right'),1,{switches=request_switches(info,route)},route.family)
    elseif route.family=='guard' then
        trace('accepted',{event_id=event_id,route=route.id,family=route.family,expected_source=route.source,observed_source=name,bits=defense_state,kind=defense_kind}); defense_frame=frames; combat_frame=frames; extra(route.id,3,{switches=request_switches(info,route)},route.family)
    else
        if route.family=='attack' then combat_frame=frames end
        extra(route.id,technique~='none' and 1 or 4,{switches=request_switches(info,route),technique=technique},route.family)
    end
end)
local gui_type='ace.GUIBase`2<app.GUIID.ID,app.UIKey.TYPE>'
for method,id in pairs({triggerSoundSelectionChanged='ui_select',triggerSoundScroll='ui_select',
    triggerSoundScrollFlsBar='ui_select',triggerSoundDecide='ui_decide',triggerSoundDecideLong='ui_decide',
    triggerSoundMouseDecide='ui_decide',triggerSoundCancel='ui_cancel',triggerSoundCancelLong='ui_cancel'}) do
    extra_hook(gui_type,method,function(args)
        local gui=sdk.to_managed_object(args[2])
        if gui then extra(id,2) end
    end)
end
safe(function()
    hook(sdk.find_type_definition('app.AdaptiveTriggerManager'), 'onAdaptiveTrigger', function(args)
        if checking then
            local value = sdk.to_int64(args[3])
            if value == 0 or value == 1 then candidate=value; counters.adaptive=counters.adaptive+1 end
            if value==0 then counters.bow=counters.bow+1 elseif value==1 then counters.spawner=counters.spawner+1 end
        end
    end)
end)
re.on_application_entry('LateUpdateBehavior', function()
    frames=frames+1
    candidate=-1
    local paused = true
    gameplay_allowed=false; ui_allowed=false
    defense_kind='none'; defense_state=0
    safe(function()
        -- Read the companion control file immediately once, then at a small
        -- fixed cadence to avoid storage-backed JSON work every game frame.
        if frames-last_control_read>=control_read_interval then
            last_control_read=frames
            local ok,value=pcall(json.load_file,'onimusha_dualsense_control.json')
            if ok and value then control=value end
        end
        if control then
            if control.routes_token then
                if routes_token~=control.routes_token and frames>=routes_retry then
                    routes_retry=frames+60
                    local loaded,data=pcall(json.load_file,'onimusha_dualsense_routes.json')
                    if loaded and data and data.token==control.routes_token and type(data.sound_events)=='table' then
                        sound_routes=data.sound_events; routes_token=data.token
                    end
                end
            elseif control.sound_events then sound_routes=control.sound_events end
            if control.trace_session~=trace_control_session then
                trace_control_session=control.trace_session; trace_ack_session=nil; trace_ack=0
            end
            -- A trace ACK is valid only after the companion has observed this
            -- Lua session. A stale ACK from a previous reload must not evict
            -- newly captured rows.
            if control.trace_lua_session==session then
                trace_ack_session=session
                if type(control.trace_ack)=='number' and control.trace_ack>trace_ack then
                    trace_ack=control.trace_ack
                    local remaining={}
                    for _,item in ipairs(trace_entries) do if item.seq>trace_ack then remaining[#remaining+1]=item end end
                    trace_entries=remaining
                end
            end
            if control.ack_session==session and type(control.ack_event)=='number' then
                local remaining={}
                for _,event in ipairs(events) do if event.seq>control.ack_event then remaining[#remaining+1]=event end end
                events=remaining
            end
        end
    end)
    safe(function()
        local vm = sdk.get_managed_singleton('app.AppPadVibrationManager')
        paused = not vm or vm:get_field('_IsPauseVibration') or not vm:call('get_IsVibrationOn')
        ui_allowed=enabled and vm~=nil and vm:call('get_IsVibrationOn')
        if enabled and not paused then
            local manager=sdk.get_managed_singleton('app.AdaptiveTriggerManager')
            if manager then
                checking=true
                local ok,err=pcall(function() manager:call('checkOnAdaptiveTrigger') end)
                checking=false
                if not ok then error(err) end
            end
        end
    end)
    safe(function()
        local manager=sdk.get_managed_singleton('app.PlayerManager')
        local info=manager and manager:call('getControllingPlayer')
        player_entity=info and info:call('get_CharacterEntity') or nil
        player_character=info and info:call('get_Character') or nil
        local technique=special_context()
        gameplay_allowed=enabled and not paused and player_entity~=nil and player_character~=nil
            and ((info:call('get_IsControl') and not player_entity:call('isEvent',false)) or technique~='none')
        if gameplay_allowed then
            local current=player_character:call('getActionState')
            defense_state=current; defense_kind=classify_defense(current)
            if defense_kind=='guard' or defense_kind=='parry' or defense_kind=='deflect' then last_defense_sample_frame=frames end
            if defense_kind~=(previous_state and classify_defense(previous_state) or 'none') then
                trace('state_transition',{route='state',family='defense',expected_source='player',observed_source='player',bits=current,kind=defense_kind})
            end
            if same_object(previous_player,player_character) and previous_state and
                (previous_state & 4)~=0 and (current & 4)==0 and (current & 1)~=0 then extra('land',10) end
            previous_state=current; previous_player=player_character
        else previous_state=nil; previous_player=nil; attack_params={} end
    end)
    safe(function()
        if not gameplay_allowed then end_native_bow('inactive',false)
        elseif defense_kind~='none' then end_native_bow('defense:'..defense_kind,true)
        elseif native_bow and frames>bow_until and not bow_charge_continues() then
            end_native_bow('charge_finished',false)
        end
        -- Keep deferred positional sounds for their existing three-frame matching
        -- window. Bow protection must not erase a contact before its hit arrives.
        if native_bow then pre_suppress_until=-1 end
    end)
    safe(function()
        if gameplay_allowed then
            for i=#sound_pending,1,-1 do
                local request=sound_pending[i]
                if frames-request.frame>3 then table.remove(sound_pending,i)
                elseif math.abs(request.frame-contact_frame)<=3 or math.abs(request.frame-defense_frame)<=3 then
                    extra(request.id,3,{switches=request.switches},request.family); combat_frame=frames; table.remove(sound_pending,i)
                end
            end
        else sound_pending={}; player_sound_object=nil end
        local age=control and control.timestamp and os.time()-control.timestamp or 100
        set_native_suppression(not native_bow and enabled and control and age>=0 and age<=2 and
            (control.suppress_legacy==true or (control.output_enabled==true and frames<=pre_suppress_until)))
    end)
    -- Publish new events on the next update. State changes that affect the
    -- companion's output are also immediate; only unchanged idle state uses
    -- the lower-rate heartbeat.
    local state_changed = enabled~=last_dump_enabled or paused~=last_dump_paused
        or gameplay_allowed~=last_dump_gameplay or ui_allowed~=last_dump_ui
        or suppressed~=last_dump_suppressed or candidate~=last_dump_trigger
        or defense_kind~=last_dump_defense
    if event_id~=last_dump_event or state_changed or frames-last_dump_frame>=heartbeat_interval then
        sequence=sequence+1
        local wrote=false
        safe(function()
            local snapshot={version=3,session=session,seq=sequence,frame=frames,
            enabled=enabled,paused=paused,trigger=candidate,events=events,counters=counters,errors=errors,
            ui_allowed=ui_allowed,gameplay_allowed=gameplay_allowed,native_bow=native_bow,
            defense_kind=defense_kind,defense_state=defense_state,
            bow_counts=bow_counts,bow_last_reason=bow_last_reason,
            extended_counts=extra_counts,extended_hooks=extra_hooks,
            legacy_suppressed=suppressed,native_feedback=native_feedback}
            if trace_enabled(defense_kind) then snapshot.defense_trace=trace_entries; snapshot.defense_trace_dropped=trace_dropped end
            json.dump_file(path,snapshot)
            wrote=true
        end)
        if wrote then
            last_dump_event=event_id; last_dump_frame=frames
            last_dump_enabled=enabled; last_dump_paused=paused
            last_dump_gameplay=gameplay_allowed; last_dump_ui=ui_allowed
            last_dump_suppressed=suppressed; last_dump_trigger=candidate
            last_dump_defense=defense_kind
        end
    end
end)
re.on_draw_ui(function()
    if imgui.tree_node('Onimusha DualSense 1.1.2') then
        local changed,value=imgui.checkbox('Enable feedback bridge',enabled)
        if changed then enabled=value; if not enabled then emit('stop') end end
        imgui.text('Start-Mod.cmd must be running.')
        imgui.text('Native rumble suppressed: '..tostring(suppressed))
        imgui.text('Adaptive calls: '..counters.adaptive)
        for id,count in pairs(extra_counts) do imgui.text('Extra '..id..': '..count) end
        for _,err in ipairs(errors) do imgui.text(err) end
        imgui.tree_pop()
    end
end)
re.on_script_reset(function() safe(function() set_native_suppression(false) end) end)
