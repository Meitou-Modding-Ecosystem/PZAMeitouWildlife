# Blender batch: decimate a Meshy GLB to a target face count, keeping UVs and the texture (PZA).
#   blender -b --python decimate_glb.py -- <in.glb> <out.glb> <target_faces>
import bpy, sys
a = sys.argv[sys.argv.index('--') + 1:]
src, dst, target = a[0], a[1], int(a[2])
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
for ob in [o for o in bpy.context.scene.objects if o.type == 'MESH']:
    n = len(ob.data.polygons)
    m = ob.modifiers.new('dec', 'DECIMATE'); m.ratio = min(1.0, target / max(n, 1))
    bpy.context.view_layer.objects.active = ob; ob.select_set(True)
    bpy.ops.object.modifier_apply(modifier='dec')
    print('decimated', ob.name, n, '->', len(ob.data.polygons))
bpy.ops.export_scene.gltf(filepath=dst, export_format='GLB')
