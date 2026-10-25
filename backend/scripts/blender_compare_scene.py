"""在 Blender 中搭建对比场景（在 Blender 的 Python 环境里运行）。

前置：
1. 用 TexTools ConsoleTools /unwrap 把 TBSE-X（SFW 选项）的 top/dwn/glv/sho.mdl 导出为 FBX，放在 FBX_DIR；
   头模：/extract chara/human/c0101/obj/face/f0001/model/c0101f0001_fac.mdl 后 /unwrap 为 fac.fbx
2. 运行 scripts/export_blender_compare.py 生成 <name>.blender.json

在 Blender 中：exec(open(r"<本文件路径>", encoding="utf-8").read())，再调用
build(json_path, fbx_dir, skeleton_json)，skeleton_json 为 src/posegraph/retarget/data/ffxiv_c0101.json。
Blender 的 FBX 导入对非 ASCII 路径不稳定，路径请用纯英文。

场景内容：橙色 = TBSE-X 套用重定向姿势；半透明绿色 = SAM 3D Body 的 MHR 网格；PhotoCam = 照片拍摄相机。
FFXIV 与 MHR 以两侧髋关节中点对齐。
"""

import json
import math

import bpy
from mathutils import Matrix, Quaternion, Vector

TO_WORLD = Matrix.Rotation(math.radians(90), 4, "X")  # 模型空间（y 上）→ Blender 世界（z 上）


def _skin_matrix(entry: dict) -> Matrix:
    q = entry["delta"]
    D = Quaternion((q[3], q[0], q[1], q[2])).to_matrix().to_4x4()
    return Matrix.Translation(Vector(entry["position"])) @ D @ Matrix.Translation(-Vector(entry["bind_position"]))


def pose_armature(arm: bpy.types.Object, ff: dict) -> list[str]:
    """按蒙皮变换设置 pose；不在 ff 中的骨骼跟随父骨骼。返回未在姿势数据中的骨骼名。"""
    targets: dict[str, Matrix] = {}
    missing = []

    def target(bone):
        if bone.name not in targets:
            if bone.name in ff:
                targets[bone.name] = _skin_matrix(ff[bone.name]) @ bone.matrix_local
            elif bone.parent:
                missing.append(bone.name)
                targets[bone.name] = target(bone.parent) @ bone.parent.matrix_local.inverted() @ bone.matrix_local
            else:
                missing.append(bone.name)
                targets[bone.name] = bone.matrix_local.copy()
        return targets[bone.name]

    for bone in arm.data.bones:
        parent_rest = (target(bone.parent) @ bone.parent.matrix_local.inverted() @ bone.matrix_local
                       if bone.parent else bone.matrix_local)
        arm.pose.bones[bone.name].matrix_basis = parent_rest.inverted() @ target(bone)
    return missing


def build_ivcs_armature(skeleton_json: str, ff: dict, extra_bones: dict) -> bpy.types.Object:
    """用 IVCS .sklb 解析出的骨架（retarget/data/ffxiv_c0101.json）建骨架，骨骼头部 = 绑定姿势位置。
    extra_bones：IVCS 身体骨架里没有、来自其它部件（如头模的脸部骨骼）的骨骼 {名称: (head, tail, 父骨骼名)}。"""
    sk = json.load(open(skeleton_json, encoding="utf-8"))
    arm_data = bpy.data.armatures.new("IVCS_c0101")
    arm = bpy.data.objects.new("IVCS_c0101", arm_data)
    bpy.context.collection.objects.link(arm)
    arm.matrix_world = TO_WORLD
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="EDIT")
    eb = arm_data.edit_bones
    heads = {n: Vector(ff[n]["bind_position"]) for n in sk["names"]}
    children: dict[str, list[str]] = {}
    for i, n in enumerate(sk["names"]):
        if sk["parents"][i] >= 0:
            children.setdefault(sk["names"][sk["parents"][i]], []).append(n)
    for i, n in enumerate(sk["names"]):
        b = eb.new(n)
        b.head = heads[n]
        kids = [heads[c] for c in children.get(n, []) if (heads[c] - heads[n]).length > 0.01]
        tip = sum(kids, Vector()) / len(kids) if kids else None
        b.tail = tip if tip is not None and (tip - b.head).length > 0.01 else b.head + Vector((0, 0.02, 0))
        if sk["parents"][i] >= 0:
            b.parent = eb[sk["names"][sk["parents"][i]]]
    pending = dict(extra_bones)
    while pending:  # 按父子顺序添加额外骨骼
        progressed = False
        for n, (head, tail, parent) in list(pending.items()):
            if parent is None or parent in eb:
                b = eb.new(n)
                b.head, b.tail = Vector(head), Vector(tail)
                if parent:
                    b.parent = eb[parent]
                del pending[n]
                progressed = True
        if not progressed:  # 父骨骼不存在：挂到 j_kao 下
            for n, (head, tail, _) in pending.items():
                b = eb.new(n); b.head, b.tail = Vector(head), Vector(tail); b.parent = eb.get("j_kao")
            break
    bpy.ops.object.mode_set(mode="OBJECT")
    return arm


def build(json_path: str, fbx_dir: str, skeleton_json: str, parts=("top", "dwn", "glv", "sho", "fac")) -> dict:
    """骨架使用 IVCS 骨骼文件重建（TexTools 导出的骨架不认识 IVCS，扩展骨骼没有父子关系），
    身体与头模的网格都重新绑定到它上面。"""
    data = json.load(open(json_path, encoding="utf-8"))
    if bpy.context.object and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)

    for n in parts:
        bpy.ops.import_scene.fbx(filepath=f"{fbx_dir}/{n}.fbx")
    imported_arms = [o for o in bpy.data.objects if o.type == "ARMATURE"]
    ivcs_names = set(json.load(open(skeleton_json, encoding="utf-8"))["names"])
    extra = {}
    for a in imported_arms:
        for b in a.data.bones:
            if b.name not in ivcs_names and b.name not in extra:
                extra[b.name] = (tuple(b.head_local), tuple(b.tail_local), b.parent.name if b.parent else None)

    arm = build_ivcs_armature(skeleton_json, data["ffxiv"], extra)
    for o in [o for o in bpy.data.objects if o.type == "MESH"]:
        world = o.matrix_world.copy()
        o.parent = arm
        o.matrix_world = world
        for m in o.modifiers:
            if m.type == "ARMATURE":
                m.object = arm
    for o in list(bpy.data.objects):
        if o.type in ("ARMATURE", "EMPTY") and o is not arm:
            bpy.data.objects.remove(o, do_unlink=True)

    ff_mat = bpy.data.materials.get("FF_mat") or bpy.data.materials.new("FF_mat")
    ff_mat.diffuse_color = (0.95, 0.55, 0.2, 1.0)
    for o in bpy.data.objects:
        if o.type == "MESH":
            o.data.materials.clear()
            o.data.materials.append(ff_mat)
    missing = pose_armature(arm, data["ffxiv"])
    arm.matrix_world = Matrix.Translation(TO_WORLD @ Vector(data["ffxiv_offset"])) @ TO_WORLD
    report = {"bones": len(arm.data.bones), "extra_bones": len(extra), "follow_parent": len(missing)}

    me = bpy.data.meshes.new("MHR_mesh")
    me.from_pydata(data["mhr"]["vertices"], [], data["mhr"]["faces"])
    me.update()
    mhr = bpy.data.objects.new("MHR", me)
    bpy.context.collection.objects.link(mhr)
    mhr.matrix_world = TO_WORLD
    mhr_mat = bpy.data.materials.get("MHR_mat") or bpy.data.materials.new("MHR_mat")
    mhr_mat.diffuse_color = (0.2, 0.9, 0.3, 0.35)
    mhr.data.materials.append(mhr_mat)
    mhr.show_transparent = True

    c = data["camera"]
    cam_data = bpy.data.cameras.new("PhotoCam")
    cam_data.sensor_fit = "VERTICAL"
    cam_data.angle_y = math.radians(c["fov_y_deg"])
    cam = bpy.data.objects.new("PhotoCam", cam_data)
    bpy.context.collection.objects.link(cam)
    cam.location = TO_WORLD @ Vector(c["position"])
    # 基础朝向：看向世界 +Y（模型 -z），上方向 +Z；再叠加站姿重力校正旋转（模型系 → 世界系）
    q = c.get("rotation", [0, 0, 0, 1])
    upright = TO_WORLD @ Quaternion((q[3], q[0], q[1], q[2])).to_matrix().to_4x4() @ TO_WORLD.inverted()
    cam.matrix_world = Matrix.Translation(cam.location) @ upright @ Matrix.Rotation(math.radians(90), 4, "X")
    scene = bpy.context.scene
    scene.camera = cam
    scene.render.resolution_y = 1000
    scene.render.resolution_x = int(1000 * c["aspect"])
    bpy.context.view_layer.update()
    return report
