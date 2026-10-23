# Blender add-on: import / export FIFA 17 ANT animations (.antanim.json written by the antstate editor)
#
# Install: Blender > Edit > Preferences > Add-ons > Install... > pick this file > enable it.
# Import:  File > Import > FIFA 17 ANT animation (.antanim.json)
#          builds an armature in the game's bind pose (local joint transforms exactly as the rig's
#          default DOF values) and an action whose pose = bind^-1 * animated local transform, so
#          what you see is exactly what the game's sampler produces.  Channels that are not joints
#          (foot velocity/height floats, face-poser floats, effector helpers, ...) become animated
#          custom properties on the armature object named "ant:<channel>".
# Export:  select the armature, File > Export > FIFA 17 ANT animation (.antanim.json); one key per
#          frame of the scene/action range.  Feed the file back in Frosty Editor: Tools > Animation Editor,
#          open the clip, Animation tab, "Replace this animation from a file..." or "Add a new animation...".
bl_info = {
    "name": "FIFA 17 ANT animation (.antanim.json)",
    "author": "antstate editor",
    "version": (1, 0, 0),
    "blender": (2, 80, 0),
    "location": "File > Import-Export",
    "description": "Import/export FIFA 17 fifagame_win32_antstate.res animation clips",
    "category": "Import-Export",
}

import json
import math

import bpy
from bpy.props import BoolProperty, FloatProperty, StringProperty
from bpy_extras.io_utils import ExportHelper, ImportHelper
from mathutils import Matrix, Quaternion, Vector

META_KEY = "antanim_meta"


def game_quat(q):
    """game (x, y, z, w) -> mathutils Quaternion (w, x, y, z)"""
    return Quaternion((q[3], q[0], q[1], q[2]))


def trs(t, q, s):
    return (Matrix.Translation(Vector(t[:3])) @ game_quat(q).to_matrix().to_4x4() @
            Matrix.Diagonal((s[0], s[1], s[2], 1.0)))


def decompose_game(m):
    t, r, s = m.decompose()
    return [t.x, t.y, t.z], [r.x, r.y, r.z, r.w], [s.x, s.y, s.z]


def _fcurve_collection(action, obj):
    """F-curve container for legacy (Blender <= 4.3: action.fcurves) and layered actions
    (Blender 4.4+ / 5.x: slot -> layer -> keyframe strip -> channelbag.fcurves)"""
    if hasattr(action, "fcurves") and not hasattr(action, "slots"):
        return action.fcurves
    try:
        slot = action.slots.new(id_type="OBJECT", name=obj.name)
    except TypeError:
        slot = action.slots.new("OBJECT", obj.name)
    try:
        obj.animation_data.action_slot = slot
    except Exception:
        pass
    layer = action.layers.new("Layer") if len(action.layers) == 0 else action.layers[0]
    strip = layer.strips.new(type="KEYFRAME") if len(layer.strips) == 0 else layer.strips[0]
    return strip.channelbag(slot, ensure=True).fcurves


def build_armature(context, doc, name, up_axis_fix=True, unit_scale=1.0):
    rig = doc.get("rig")
    if not rig:
        raise RuntimeError("the .antanim.json has no rig/skeleton section")
    joints = rig["joints"]
    arm = bpy.data.armatures.new(name)
    obj = bpy.data.objects.new(name, arm)
    context.collection.objects.link(obj)
    context.view_layer.objects.active = obj
    obj.select_set(True)
    # bind world matrices
    world = []
    for j in joints:
        L = trs(j["t"], j["q"], j["s"])
        world.append(world[j["parent"]] @ L if j["parent"] >= 0 else L)
    children = {i: [] for i in range(len(joints))}
    for i, j in enumerate(joints):
        if j["parent"] >= 0:
            children[j["parent"]].append(i)
    bpy.ops.object.mode_set(mode="EDIT")
    ebs = []
    for i, j in enumerate(joints):
        eb = arm.edit_bones.new(j["name"])
        head = world[i].to_translation()
        ln = 0.0
        for c in children[i]:
            ln = max(ln, (world[c].to_translation() - head).length)
        if ln < 1e-4:
            ln = 0.1
        eb.head = head
        eb.tail = head + Vector((0.0, ln, 0.0))
        m = world[i].normalized()
        eb.matrix = m
        ebs.append(eb)
    for i, j in enumerate(joints):
        if j["parent"] >= 0:
            ebs[i].parent = ebs[j["parent"]]
            ebs[i].use_connect = False
    bpy.ops.object.mode_set(mode="OBJECT")
    if up_axis_fix:
        obj.rotation_euler = (math.pi / 2.0, 0.0, 0.0)     # game Y-up -> Blender Z-up
    obj.scale = (unit_scale, unit_scale, unit_scale)
    for pb in obj.pose.bones:
        pb.rotation_mode = "QUATERNION"
    return obj, world


def import_antanim(context, path, up_axis_fix=True, unit_scale=1.0):
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
    if doc.get("format") != "fifa17-antanim":
        raise RuntimeError("not a fifa17-antanim file")
    clip = doc["clip"]
    name = clip.get("name", "ant_clip").replace(".chan", "")
    obj, world = build_armature(context, doc, name, up_axis_fix, unit_scale)
    joints = doc["rig"]["joints"]
    jidx = {j["name"]: i for i, j in enumerate(joints)}
    bindL = [trs(j["t"], j["q"], j["s"]) for j in joints]
    bindL_inv = [m.inverted() for m in bindL]
    chans = doc["channels"]
    key_frames = doc["key_frames"]
    keys = doc["keys"]
    # which channels drive joints
    joint_ch = {}          # joint index -> {"q": col, "t": col, "s": col}
    extra = []             # (col, name, kind) channels stored as custom properties
    for col, c in enumerate(chans):
        nm = c["name"]
        if "." in nm:
            jn, comp = nm.rsplit(".", 1)
            if jn in jidx and comp in ("q", "t", "s"):
                joint_ch.setdefault(jidx[jn], {})[comp] = col
                continue
        extra.append((col, nm, c["kind"]))
    action = bpy.data.actions.new(name)
    obj.animation_data_create()
    obj.animation_data.action = action
    nk = len(key_frames)
    fcurves = _fcurve_collection(action, obj)

    def fc(path, index, group):
        try:
            if hasattr(fcurves, "new"):
                try:
                    return fcurves.new(path, index=index, action_group=group)
                except TypeError:
                    return fcurves.new(path, index=index)
        except RuntimeError:
            pass
        return fcurves.find(path, index=index)
    for ji, cols in joint_ch.items():
        jn = joints[ji]["name"]
        pb = obj.pose.bones[jn]
        locs, rots, scls = [], [], []
        prevq = None
        for k in range(nk):
            row = keys[k]
            t = row[cols["t"]] if "t" in cols else joints[ji]["t"]
            q = row[cols["q"]] if "q" in cols else joints[ji]["q"]
            s = row[cols["s"]] if "s" in cols else joints[ji]["s"]
            basis = bindL_inv[ji] @ trs(t, q, s)
            l, r, sc = basis.decompose()
            if prevq is not None and prevq.dot(r) < 0:
                r = -r
            prevq = r
            locs.append(l); rots.append(r); scls.append(sc)
        base = 'pose.bones["%s"]' % jn
        for comp, n, arr in (("location", 3, locs), ("rotation_quaternion", 4, rots), ("scale", 3, scls)):
            for ax in range(n):
                cv = fc(base + "." + comp, ax, jn)
                cv.keyframe_points.add(nk)
                co = []
                for k in range(nk):
                    co += [float(key_frames[k]), float(arr[k][ax])]
                cv.keyframe_points.foreach_set("co", co)
                for kp in cv.keyframe_points:
                    kp.interpolation = "LINEAR"
                cv.update()
    for col, nm, kind in extra:
        prop = "ant:" + nm
        n = 4 if kind == "quat" else (3 if kind == "vec3" else 1)
        first = keys[0][col] if nk else [0.0] * n
        obj[prop] = list(map(float, first)) if n > 1 else float(first[0])
        for ax in range(n):
            cv = fc('["%s"]' % prop, ax if n > 1 else 0, "ANT channels")
            cv.keyframe_points.add(nk)
            co = []
            for k in range(nk):
                co += [float(key_frames[k]), float(keys[k][col][ax])]
            cv.keyframe_points.foreach_set("co", co)
            for kp in cv.keyframe_points:
                kp.interpolation = "LINEAR"
            cv.update()
    meta = {"clip": clip, "channels": chans, "rig": doc["rig"], "up_axis_fix": up_axis_fix,
            "unit_scale": unit_scale}
    obj[META_KEY] = json.dumps(meta)
    sc = context.scene
    sc.render.fps = int(round(clip.get("fps", 30.0)))
    if nk:
        sc.frame_start = int(key_frames[0])
        sc.frame_end = int(key_frames[-1])
    return obj


def export_antanim(context, obj, path, frame_start=None, frame_end=None):
    if obj is None or obj.type != "ARMATURE" or META_KEY not in obj:
        raise RuntimeError("select an armature imported from a .antanim.json file")
    meta = json.loads(obj[META_KEY])
    joints = meta["rig"]["joints"]
    bindL = {j["name"]: trs(j["t"], j["q"], j["s"]) for j in joints}
    chans = meta["channels"]
    sc = context.scene
    f0 = sc.frame_start if frame_start is None else frame_start
    f1 = sc.frame_end if frame_end is None else frame_end
    keys = []
    frames = list(range(f0, f1 + 1))
    cur = sc.frame_current
    for fr in frames:
        sc.frame_set(fr)
        row = []
        cache = {}
        for c in chans:
            nm, kind = c["name"], c["kind"]
            jn, comp = nm.rsplit(".", 1) if "." in nm else (nm, "")
            if jn in bindL and comp in ("q", "t", "s") and jn in obj.pose.bones:
                if jn not in cache:
                    pb = obj.pose.bones[jn]
                    cache[jn] = decompose_game(bindL[jn] @ pb.matrix_basis)
                t, q, s = cache[jn]
                row.append(q if comp == "q" else (t if comp == "t" else s))
            else:
                v = obj.get("ant:" + nm)
                if v is None:
                    v = [0.0, 0.0, 0.0, 1.0] if kind == "quat" else ([0.0] * 3 if kind == "vec3" else [0.0])
                elif not hasattr(v, "__len__"):
                    v = [float(v)]
                row.append([float(x) for x in v])
        keys.append(row)
    sc.frame_set(cur)
    clip = dict(meta["clip"])
    clip["end_frame"] = frames[-1] - frames[0]
    clip["fps"] = float(sc.render.fps)
    doc = {"format": "fifa17-antanim", "version": 1, "clip": clip, "rig": meta["rig"],
           "channels": chans, "key_frames": [f - frames[0] for f in frames], "keys": keys}
    with open(path, "w", encoding="utf-8") as f:
        json.dump(doc, f, separators=(",", ":"))
    return path


class IMPORT_OT_antanim(bpy.types.Operator, ImportHelper):
    bl_idname = "import_anim.fifa17_antanim"
    bl_label = "Import FIFA 17 ANT animation"
    filename_ext = ".json"
    filter_glob: StringProperty(default="*.antanim.json;*.json", options={"HIDDEN"})
    up_axis_fix: BoolProperty(name="Y-up to Z-up", default=True)
    unit_scale: FloatProperty(name="Unit scale", default=1.0, min=0.0001,
                              description="0.3048 shows the game units as metres")

    def execute(self, context):
        import_antanim(context, self.filepath, self.up_axis_fix, self.unit_scale)
        return {"FINISHED"}


class EXPORT_OT_antanim(bpy.types.Operator, ExportHelper):
    bl_idname = "export_anim.fifa17_antanim"
    bl_label = "Export FIFA 17 ANT animation"
    filename_ext = ".antanim.json"
    filter_glob: StringProperty(default="*.antanim.json", options={"HIDDEN"})

    def execute(self, context):
        export_antanim(context, context.active_object, self.filepath)
        return {"FINISHED"}


def menu_import(self, context):
    self.layout.operator(IMPORT_OT_antanim.bl_idname, text="FIFA 17 ANT animation (.antanim.json)")


def menu_export(self, context):
    self.layout.operator(EXPORT_OT_antanim.bl_idname, text="FIFA 17 ANT animation (.antanim.json)")


def register():
    bpy.utils.register_class(IMPORT_OT_antanim)
    bpy.utils.register_class(EXPORT_OT_antanim)
    bpy.types.TOPBAR_MT_file_import.append(menu_import)
    bpy.types.TOPBAR_MT_file_export.append(menu_export)


def unregister():
    bpy.types.TOPBAR_MT_file_import.remove(menu_import)
    bpy.types.TOPBAR_MT_file_export.remove(menu_export)
    bpy.utils.unregister_class(EXPORT_OT_antanim)
    bpy.utils.unregister_class(IMPORT_OT_antanim)


if __name__ == "__main__":
    register()
