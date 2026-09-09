-- Onimusha DualSense bridge 1.0. Sound-derived haptics and adaptive triggers.
local path = 'onimusha_dualsense_bridge.json'
local enabled, frames, sequence, event_id = true, 0, 0, 0
local candidate, checking, events, errors = -1, false, {}, {}
local session = tostring(os.time()) .. ':' .. tostring(os.clock())
local counters = {adaptive=0, bow=0, spawner=0}
local original_feedback, native_feedback = nil, nil
local control, suppressed = nil, false
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
    if player_entity:call('isDeadHeatAction') then return 'deadheat' end
    if special_kind=='issen' and frames-special_frame<=600 then return 'issen' end
    return 'none'
end
local extra_counts, extra_hooks, extra_last = {}, {}, {}
local attack_params, hit_ids, combat_frame = {}, {}, -1000
local last_dump_event = -1
local sound_pending, player_sound_object = {}, nil
local contact_frame, defense_frame, sound_step_frame, sound_side = -1000, -1000, -1000, false
local pre_suppress_until = -1
local function sound_ready() return control and control.sound_events and next(control.sound_events)~=nil end
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
local function request_switches(info)
    local result={}
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
local function extra(id, minimum_frames, metadata)
    if not enabled then return end
    local is_ui = id:sub(1,3)=='ui_'
    if is_ui then if not ui_allowed then return end
    elseif not gameplay_allowed then return end
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
    if not entity_is_player(args) or sound_ready() then return end
    local track=sdk.to_managed_object(args[3])
    if not track then return end
    if not track:call('get_IsOn') then return end
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
    contact_frame=frames; combat_frame=frames
    if not sound_ready() then extra('hit',3) end
end)
extra_hook('app.cPlayerGuardController','addDamage',function(args)
    if module_is_player(args) then
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
    if entity_is_player(args) and not sound_ready() then extra('finisher',12) end
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
    local src=info:call('get_SrcGameObj')
    if not src then return end
    local name=src:call('get_Name')
    if name=='Player_00' then player_sound_object=src end
    local route=control.sound_events[tostring(info:call('get_EventId'))]
    if not route then return end
    if route.source=='GUI' then
        if name=='GUI' then extra(route.id,3) end
        return
    end
    if not gameplay_allowed then return end
    local technique=special_context()
    if route.family=='special_motion' and technique=='none' then return end
    if route.source=='parry_pos' then
        if name~='parry_pos' or not player_character or not player_sound_object then return end
        local a=src:call('get_Transform'):call('get_Position')
        local b=player_sound_object:call('get_Transform'):call('get_Position')
        if (a.x-b.x)^2+(a.y-b.y)^2+(a.z-b.z)^2>16 then return end
        local kind=classify_defense(player_character:call('getActionState'))
        if route.family=='parry_stop' then emit('defense_stop',route.stops); return end
        if route.family=='parry' and kind~='parry' then return end
        if route.family=='deflect' and kind~='deflect' then return end
        -- Release sounds can arrive after the action bit clears; the positional
        -- event itself is the verified Wwise release, not an inferred guard.
        if route.family=='parry_release' and kind=='dodge' then return end
        defense_frame=frames; combat_frame=frames
        extra(route.id,1,{switches=request_switches(info),defense_kind=kind})
        return
    end
    if route.source=='player' and name~='Player_00' then return end
    if route.source=='player_effect' and name~='effect_Player_00' then return end
    if route.source=='TrgPos' then
        if name~='TrgPos' or not player_sound_object then return end
        local a=src:call('get_Transform'):call('get_Position')
        local b=player_sound_object:call('get_Transform'):call('get_Position')
        if (a.x-b.x)^2+(a.y-b.y)^2+(a.z-b.z)^2>16 then return end
        if technique~='none' then
            extra(route.id,1,{switches=request_switches(info),technique=technique}); combat_frame=frames
            return
        end
        sound_pending[#sound_pending+1]={id=route.id,frame=frames,switches=request_switches(info)}
        if #sound_pending>16 then table.remove(sound_pending,1) end
        return
    end
    if route.family=='footsteps' then
        if not player_entity or player_entity:call('getCurrentActionMoveType')<0 then return end
        if frames-combat_frame<18 or frames-sound_step_frame<8 then return end
        sound_step_frame=frames; sound_side=not sound_side
        extra(route.id..(sound_side and '_left' or '_right'),1,{switches=request_switches(info)})
    elseif route.family=='guard' then
        defense_frame=frames; combat_frame=frames; extra(route.id,3,{switches=request_switches(info)})
    else
        if route.family=='attack' then combat_frame=frames end
        extra(route.id,technique~='none' and 1 or 4,{switches=request_switches(info),technique=technique})
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
        local ok,value=pcall(json.load_file,'onimusha_dualsense_control.json')
        if ok and value then control=value end
        local age=control and control.timestamp and os.time()-control.timestamp or 100
        set_native_suppression(enabled and control and age>=0 and age<=2 and
            (control.suppress_legacy==true or (control.output_enabled==true and frames<=pre_suppress_until)))
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
            if same_object(previous_player,player_character) and previous_state and
                (previous_state & 4)~=0 and (current & 4)==0 and (current & 1)~=0 then extra('land',10) end
            previous_state=current; previous_player=player_character
        else previous_state=nil; previous_player=nil; attack_params={} end
    end)
    safe(function()
        if gameplay_allowed then
            for i=#sound_pending,1,-1 do
                local request=sound_pending[i]
                if frames-request.frame>3 then table.remove(sound_pending,i)
                elseif math.abs(request.frame-contact_frame)<=3 or math.abs(request.frame-defense_frame)<=3 then
                    extra(request.id,3,{switches=request.switches}); combat_frame=frames; table.remove(sound_pending,i)
                end
            end
        else sound_pending={}; player_sound_object=nil end
        local age=control and control.timestamp and os.time()-control.timestamp or 100
        if enabled and control and control.output_enabled==true and age>=0 and age<=2 and frames<=pre_suppress_until then
            set_native_suppression(true)
        end
    end)
    -- Publish new events on the next update; keep a lower-rate idle heartbeat.
    if event_id~=last_dump_event or frames % 2 == 0 then
        last_dump_event=event_id
        sequence=sequence+1
        safe(function() json.dump_file(path, {version=3,session=session,seq=sequence,frame=frames,
            enabled=enabled,paused=paused,trigger=candidate,events=events,counters=counters,errors=errors,
            ui_allowed=ui_allowed,gameplay_allowed=gameplay_allowed,
            defense_kind=defense_kind,defense_state=defense_state,
            extended_counts=extra_counts,extended_hooks=extra_hooks,
            legacy_suppressed=suppressed,native_feedback=native_feedback}) end)
    end
end)
re.on_draw_ui(function()
    if imgui.tree_node('Onimusha DualSense 1.0') then
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
