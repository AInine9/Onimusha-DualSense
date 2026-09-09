"""Development test; the distribution contains neither Python nor these mocks."""
import json, pathlib
from lupa import LuaRuntime
ROOT = pathlib.Path(__file__).resolve().parent.parent
known = json.loads((ROOT / 'dev/lua-api.json').read_text(encoding='utf-8'))

lua = LuaRuntime(unpack_returned_tuples=True)
def table(value):
    if isinstance(value, dict): return lua.table_from({k: table(v) for k, v in value.items()})
    if isinstance(value, list): return lua.table_from([table(v) for v in value])
    return value
lua.globals().known = table(known)
lua.execute(r'''
hooks={}; tick=nil; snapshot=nil; paused=false; event_scene=false; move_type=0; action_bits=1
feedback=true; input_gui=true; open_gui=true; controls={timestamp=os.time(),suppress_legacy=false}
local function td(name)
 if not known[name] then return nil end
 return {get_parent_type=function() return td(known[name].parent) end,
 get_method=function(_,method)
  for _,n in ipairs(known[name].methods) do if n==method then return {owner=name,name=method} end end
  if known[name].parent then local parent=td(known[name].parent); if parent then return parent:get_method(method) end end
 end}
end
player={get_address=function() return 1 end,call=function(_,name)
 if name=='isDeadHeatAction' then return dead_heat==true elseif name=='isEvent' then return event_scene elseif name=='getCurrentActionMoveType' then return move_type end
 error('Unexpected player call '..name)
end}
character={get_address=function() return 2 end,call=function(_,name) assert(name=='getActionState'); return action_bits end}
local info={call=function(_,name)
 if name=='get_CharacterEntity' then return player elseif name=='get_Character' then return character elseif name=='get_IsControl' then return true end
 error('Unexpected info call '..name)
end}
local pm={call=function(_,name) assert(name=='getControllingPlayer'); return info end}
local vm={get_field=function(_,name) if name=='_IsPauseVibration' then return paused end end,
 call=function(_,name) assert(name=='get_IsVibrationOn'); return true end}
local am={call=function(_,name) assert(name=='checkOnAdaptiveTrigger') end}
sdk={find_type_definition=td,to_int64=function(v) return v end,to_managed_object=function(v) return v end,
 hook=function(method,pre,post) hooks[method.owner..'.'..method.name]=pre end,
 get_managed_singleton=function(name)
  if name=='app.PlayerManager' then return pm elseif name=='app.AppPadVibrationManager' then return vm elseif name=='app.AdaptiveTriggerManager' then return am end
 end,
 get_native_singleton=function() return {} end,
 call_native_func=function(_,_,name,value)
  if name=='get_ForceFeedbackEnable' then return feedback elseif name=='set_ForceFeedbackEnable' then feedback=value else error(name) end
 end}
json={load_file=function() return controls end,dump_file=function(_,data) snapshot=data end}
re={on_application_entry=function(_,f) tick=f end,on_draw_ui=function(f) draw=f end,on_script_reset=function(f) reset=f end}
function advance(n) for i=1,n do tick() end end
function fire(t,m,args) local fn=assert(hooks[t..'.'..m],t..'.'..m); fn(args) end
function last_id() local e=snapshot.events; return e[#e] and e[#e].id end
''')
lua.execute((ROOT / 'reframework/autorun/onimusha_dualsense_bridge.lua').read_text(encoding='utf-8'))
lua.execute(r'''
advance(2)
assert(snapshot.version==3 and snapshot.gameplay_allowed and snapshot.ui_allowed)
for method,ok in pairs(snapshot.extended_hooks) do assert(ok,'Metadata did not verify hook '..method) end
assert(#snapshot.errors==0,table.concat(snapshot.errors,'\n'))
local foot={get_field=function(_,name) assert(name=='_PlayerEntity'); return player end}
fire('app.EPVExpertFootLandingCustom','play',{[2]=foot,[3]=0,[4]=0})
assert(last_id()=='foot_left')
local count=#snapshot.events
fire('app.EPVExpertFootLandingCustom','play',{[2]=foot,[3]=1,[4]=1})
assert(#snapshot.events==count,'Lifting a foot must not generate a contact')
local enemy={get_field=function() return nil end}
fire('app.EPVExpertFootLandingCustom','play',{[2]=enemy,[3]=0,[4]=1})
assert(#snapshot.events==count,'Enemy steps must never vibrate the player')
advance(8); move_type=2
fire('app.EPVExpertFootLandingCustom','play',{[2]=foot,[3]=2,[4]=1})
assert(last_id()=='run_right')
local on=true
local track={call=function(_,name) if name=='get_IsOn' then return on elseif name=='get_AttackParamID' or name=='get_RequestSetID' then return 123 end; error(name) end}
fire('app.cPlayerCharacterEntity','evAttackCollision',{[2]=player,[3]=track})
assert(last_id()=='attack')
count=#snapshot.events
advance(1); fire('app.cPlayerCharacterEntity','evAttackCollision',{[2]=player,[3]=track})
assert(#snapshot.events==count,'A multi-frame attack track must not replay continuously')
advance(8); on=false; fire('app.cPlayerCharacterEntity','evAttackCollision',{[2]=player,[3]=track})
on=true; fire('app.cPlayerCharacterEntity','evAttackCollision',{[2]=player,[3]=track})
assert(#snapshot.events==count,'Track gaps do not start another sword swing')
local swing={call=function() return 262144 end}
fire('app.CharacterBase','evBaseActionEnter',{[2]=character,[3]=swing})
fire('app.cPlayerCharacterEntity','evAttackCollision',{[2]=player,[3]=track})
assert(#snapshot.events==count+1,'New action permits another swing')
local hit={call=function(_,name) if name=='get_AttackUniqueID' then return 44 elseif name=='get_HitID' then return 1 end; error(name) end}
fire('app.cPlayerCharacterEntity','onHitAttackPostProcess',{[2]=player,[3]=hit})
assert(last_id()=='hit'); count=#snapshot.events
advance(8); fire('app.cPlayerCharacterEntity','onHitAttackPostProcess',{[2]=player,[3]=hit})
assert(#snapshot.events==count,'Duplicate collider contact stays silent')
fire('app.EPVExpertFootLandingCustom','play',{[2]=foot,[3]=0,[4]=0})
assert(#snapshot.events==count,'Combat masks incidental footsteps')
fire('app.cPlayerCharacterEntity','onAddHealth',{[2]=player,[3]=1,[4]=-10}); assert(last_id()=='damage')
fire('app.cPlayerCharacterEntity','onAddHealth',{[2]=player,[3]=0,[4]=10}); assert(last_id()=='heal')
local action={call=function(_,name) assert(name=='get_ActStateBit'); return 536870912 end}
fire('app.CharacterBase','evBaseActionEnter',{[2]=character,[3]=action}); assert(last_id()=='dodge')
action_bits=4; advance(2); action_bits=1; advance(2); assert(last_id()=='land')
paused=true; advance(2); count=#snapshot.events
fire('app.cPlayerCharacterEntity','onHitAttackPostProcess',{[2]=player}); assert(#snapshot.events==count)
local gui={call=function(_,name) if name=='get_IsOpenVisibleState' then return open_gui elseif name=='get_CanInputIgnoreFrame' then return input_gui end; error(name) end}
fire('ace.GUIBase`2<app.GUIID.ID,app.UIKey.TYPE>','triggerSoundSelectionChanged',{[2]=gui}); assert(last_id()=='ui_select')
fire('ace.GUIBase`2<app.GUIID.ID,app.UIKey.TYPE>','triggerSoundDecide',{[2]=gui}); assert(last_id()=='ui_decide')
open_gui=false; input_gui=false; count=#snapshot.events
fire('ace.GUIBase`2<app.GUIID.ID,app.UIKey.TYPE>','triggerSoundCancel',{[2]=gui}); assert(last_id()=='ui_cancel'); count=#snapshot.events
paused=false; event_scene=true; advance(8)
fire('app.EPVExpertFootLandingCustom','play',{[2]=foot,[3]=0,[4]=1}); assert(#snapshot.events==count,'No synthetic steps in cinematics')
event_scene=false
imgui={tree_node=function() return true end,checkbox=function() return true,false end,text=function() end,tree_pop=function() end}
draw(); advance(8); count=#snapshot.events
fire('app.cPlayerCharacterEntity','onHitAttackPostProcess',{[2]=player}); assert(#snapshot.events==count,'User-disabled extension must stay silent')
imgui.checkbox=function() return true,true end; draw(); controls.suppress_legacy=true; advance(2); assert(feedback==false)
reset(); assert(feedback==true,'Original feedback setting restored on script reset')
assert(#snapshot.errors==0,table.concat(snapshot.errors,'\n'))
''')
lua.execute(r''' 
controls.suppress_legacy=false; controls.output_enabled=true
controls.sound_events={
 ['11']={id='ui_sound_11',family='ui',source='GUI'},
 ['12']={id='sound_12',family='footsteps',source='player'},
 ['13']={id='sound_13',family='attack',source='player'},
 ['14']={id='sound_14',family='guard',source='player_effect'},
 ['15']={id='sound_15',family='contact',source='TrgPos'}}
paused=false; event_scene=false; move_type=0; advance(30)
local function source(name)
 return {get_address=function() return name end,call=function(_,method)
  if method=='get_Name' then return name elseif method=='get_Transform' then return {call=function() return {x=name=='parry_pos' and (sound_distance or 0) or 0,y=0,z=0} end} end
  error(method)
 end}
end
function sound(id,name)
 local entry={get_field=function(_,field) if field=='key' then return 10 elseif field=='value' then return 20 end end}
 local entries={get_elements=function() return {entry} end}
 local switches={get_field=function(_,field) assert(field=='_entries'); return entries end}
 local info={call=function(_,method)
  if method=='get_EventId' then return id elseif method=='get_SrcGameObj' then return source(name)
  elseif method=='get_SwitchInfoDict' then return switches end
  error(method)
 end}
 fire('soundlib.SoundManager','postRequestInfo',{[2]=info})
end
sound(12,'Player_00'); assert(last_id()=='sound_12_left' and snapshot.events[#snapshot.events].switches['10']==20)
local count=#snapshot.events
sound(12,'Player_00'); sound(12,'Em300_00')
assert(#snapshot.events==count,'Layer duplicates and enemy sounds cannot add footsteps')
advance(10); sound(12,'Player_00'); assert(last_id()=='sound_12_right')
sound(15,'TrgPos'); sound(14,'effect_Player_00'); advance(1)
assert(last_id()=='sound_15','Contact sound pairs with actual player guard sound even without addDamage callback')
assert(snapshot.legacy_suppressed,'New sound event carries native-suppression ack on first publication')
advance(10); count=#snapshot.events; sound(15,'TrgPos'); advance(5)
assert(#snapshot.events==count,'Unrelated nearby contact lacks a player contact window')
paused=true; advance(2); sound(11,'GUI'); advance(1)
assert(last_id()=='ui_sound_11','Actual menu sound works during pause without GUI wrapper callbacks')
count=#snapshot.events; sound(13,'Player_00'); assert(#snapshot.events==count)
controls.output_enabled=false; controls.suppress_legacy=false; advance(6)
assert(feedback==true,'Focus/companion output disable releases proactive suppression')
assert(#snapshot.errors==0,table.concat(snapshot.errors,'\n'))
''')
print('PASS: reflected hook signatures, player-only contacts, attack edges, real health changes, evade/landing, paused UI, cutscene/disabled silence, suppression cleanup')
lua.execute(r'''
paused=false; event_scene=false
for _,case in ipairs({{16777216,'guard'},{33554432,'guard'},{67108864,'guard'},{268435456,'parry'},{134217728,'deflect'},{536870912,'dodge'},{8388608,'none'},{1,'none'},{268435456|536870912,'dodge'},{268435456|134217728,'deflect'}}) do
 action_bits=case[1]; advance(2); assert(snapshot.defense_kind==case[2], 'Defense classification '..case[1]); assert(snapshot.defense_state==case[1])
end
action_bits=268435456; paused=true; advance(2); assert(snapshot.defense_kind=='none')
paused=false; event_scene=true; advance(2); assert(snapshot.defense_kind=='none')
assert(#snapshot.errors==0,table.concat(snapshot.errors,'\n'))
''')
print('PASS: 4 distinct defenses, PARRIED excluded, conflicting bits fail closed, pause/cutscene clears parry')
lua.execute(r'''
paused=false; event_scene=false; action_bits=1; advance(2); sound(12,'Player_00')
controls.sound_events['16']={id='defense_sound_16',family='parry',source='parry_pos'}
controls.sound_events['17']={id='defense_sound_17',family='deflect',source='parry_pos'}
controls.sound_events['18']={id='defense_sound_18',family='parry_release',source='parry_pos'}
controls.sound_events['19']={id='defense_sound_19',family='parry_stop',source='parry_pos',stops='defense_sound_16'}
local n=#snapshot.events
action_bits=33554432; advance(2); sound(16,'parry_pos'); assert(#snapshot.events==n,'Normal guard cannot generate parry friction')
action_bits=268435456; advance(2); sound(16,'parry_pos'); assert(last_id()=='defense_sound_16'); n=#snapshot.events
advance(2); sound(16,'Em301_00'); assert(#snapshot.events==n,'Enemy object cannot route a dedicated defense sound')
sound_distance=5; sound(16,'parry_pos'); assert(#snapshot.events==n,'Far defense position is rejected'); sound_distance=0
action_bits=134217728; advance(2); sound(17,'parry_pos'); assert(last_id()=='defense_sound_17')
assert(snapshot.events[#snapshot.events].defense_kind=='deflect')
action_bits=1; advance(2); sound(18,'parry_pos'); assert(last_id()=='defense_sound_18','Actual release survives cleared action bit')
sound(19,'parry_pos'); assert(last_id()=='defense_sound_16' and snapshot.events[#snapshot.events].kind=='defense_stop')
action_bits=536870912; advance(2); n=#snapshot.events; sound(16,'parry_pos'); sound(17,'parry_pos'); sound(18,'parry_pos'); assert(#snapshot.events==n,'Dodge never routes defense sound')
assert(#snapshot.errors==0,table.concat(snapshot.errors,'\n'))
''')
print('PASS: positional defense sources, independent parry/deflect, real release/stop, distance and enemy rejection')
lua.execute(r'''
paused=false; event_scene=false; dead_heat=false; action_bits=1; advance(2); sound(12,'Player_00')
controls.sound_events['20']={id='sound_20',family='special_motion',source='player'}
action_bits=786432; advance(2)
fire('app.cPlayerCharacterEntity','attackBreakImpactNotice',{[2]=player})
advance(23); sound(15,'TrgPos'); assert(last_id()=='sound_15' and snapshot.events[#snapshot.events].technique=='issen','Issen contact is accepted 23 frames after the notice')
sound(13,'Player_00'); assert(snapshot.events[#snapshot.events].technique=='issen')
action_bits=65536; advance(2); local n=#snapshot.events; sound(20,'Player_00'); assert(#snapshot.events==n,'Special motion sounds cannot play outside a technique')
action_bits=524288; dead_heat=true; event_scene=true; advance(2)
assert(snapshot.gameplay_allowed,'Explicit dead heat action remains playable during paired animation')
sound(20,'Player_00'); assert(last_id()=='sound_20' and snapshot.events[#snapshot.events].technique=='deadheat')
advance(24); sound(15,'TrgPos'); assert(snapshot.events[#snapshot.events].technique=='deadheat')
paused=true; advance(2); n=#snapshot.events; sound(20,'Player_00'); assert(#snapshot.events==n,'Pause still mutes special techniques')
paused=false; dead_heat=false; action_bits=65536; advance(2); assert(not snapshot.gameplay_allowed,'Ordinary cutscene is not mistaken for a technique')
assert(#snapshot.errors==0,table.concat(snapshot.errors,'\n'))
''')
print('PASS: delayed issen impact, dedicated dead heat audio, paired animation, technique exit/pause/movie boundaries')
