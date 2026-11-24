import bpy, json, math, sys
from pathlib import Path
from mathutils import Matrix, Vector, Quaternion

args=sys.argv[sys.argv.index('--')+1:]
output=Path(args[0])
fbx_path=Path(args[1]) if len(args)>1 else output/'wulfa_all11.fbx'
report_path=fbx_path.with_suffix('.validation.json') if len(args)>1 else output/'blender_fbx_validation.json'
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(fbx_path),automatic_bone_orientation=False,use_prepost_rot=True)
clip='A_actor_wulfa_interact_sit_start'
arms=[o for o in bpy.data.objects if o.type=='ARMATURE' and 'Bip001_L_Hand' in o.pose.bones]
assert len(arms)==1,[(a.name,len(a.pose.bones)) for a in arms]
arm=arms[0]
actions=[a for a in bpy.data.actions if clip in a.name]
assigned=0
for obj in bpy.data.objects:
    matches=[a for a in actions if a.name.split('|')[0]==obj.name]
    if len(matches)==1:
        ad=obj.animation_data_create();ad.action=matches[0];ad.use_nla=False
        ad.action_slot=next(s for s in matches[0].slots if s.target_id_type=='OBJECT');assigned+=1
assert arm.animation_data and clip in arm.animation_data.action.name,(arm.name,[a.name for a in actions[:4]])
expected=json.loads((output/'sit_start_expected.json').read_text(encoding='utf-8-sig'))
tracks={t['Path'].split('/')[-1]:t for t in expected['TrackList'] if t['Path']}
human=['Bip001','Bip001_Pelvis','Bip001_Spine','Bip001_Spine1','Bip001_Spine2','Bip001_Head','Bip001_Neck']
human += ['Bip001_'+s+'_'+b for s in ['L','R'] for b in ['Thigh','Calf','Foot','Toe0','Clavicle','UpperArm','Forearm','Hand']]
report=[]
for sample in [0,31,62,93,124,155,185]:
    time=sample/60;frame=1+time*bpy.context.scene.render.fps/bpy.context.scene.render.fps_base
    bpy.context.scene.frame_set(math.floor(frame),subframe=frame-math.floor(frame));bpy.context.view_layer.update()
    for name in human:
        track=tracks[name];p=arm.pose.bones[name]
        local=p.parent.matrix.inverted()@p.matrix if p.parent else p.matrix
        def value(channel):return min(track[channel],key=lambda k:abs(k['time']-time))['value']
        q=value('Rotations');v=value('Translations');s=value('Scalings')
        target=Matrix.LocRotScale(Vector((v['X'],v['Y'],v['Z'])),Quaternion((q['W'],q['X'],q['Y'],q['Z'])),Vector((s['X'],s['Y'],s['Z'])))
        dot=abs(local.to_quaternion().normalized().dot(target.to_quaternion().normalized()))
        angle=math.degrees(2*math.acos(min(1,dot)))
        distance=(local.translation-target.translation).length
        report.append(dict(frame=frame,bone=name,angle_degrees=angle,position_m=distance))
max_angle=max(r['angle_degrees'] for r in report);max_position=max(r['position_m'] for r in report)
result=dict(armature=arm.name,assigned_objects=assigned,actions=len(bpy.data.actions),max_angle_degrees=max_angle,max_position_m=max_position,samples=report)
report_path.write_text(json.dumps(result,indent=2))
print('FBX_COMPARISON',json.dumps({k:v for k,v in result.items() if k!='samples'}))
assert max_angle<0.15,(max_angle,sorted(report,key=lambda r:r['angle_degrees'])[-3:])
assert max_position<0.0002,(max_position,sorted(report,key=lambda r:r['position_m'])[-3:])
print('BLENDER_FBX_HUMANOID_SUCCESS')
