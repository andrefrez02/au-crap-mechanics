#!/usr/bin/env python3
"""Generates the Crap Mechanics mod's prefabs (Unity YAML) from a specification.
Uses only the game's own assets by reference (the decoupler's meshes/material and port markers) — nothing copied.
Output: Content/Prefabs/<name>.prefab (+ .meta when it doesn't exist)."""
import hashlib, math, os, sys, uuid

G = "06dd3d5416bf43ea368cc39ff96e6d5a"          # the game's scripts assembly
S_EPC, S_AREA, S_REND, S_COL = 807379410, -692492370, -2059388358, 2100049158
S_COL_CONVEX = -1838397108   # DOTSColliderConvex (_mesh set by the plugin in configure)
S_PIPE = -906939338           # EPC_SCPipe: EPC + _fuelCapacity/_fuelStabilityThreshold/_fuelStabilitySpeed (the game connects it to the pipes)
S_TANK = -1613879900        # EPC_SCTank (only to check the game's tank YAML)
MAT = "3e3aeaceae3d77442855f522ea98f2ea"
M_BODY, M_PAINTABLE = "86d9cfe1dd2ba2c49bd20e4604dd33d4", "32075a3c0b563154a98163eb14f20d8c"
M_PORT = {0: "c0b5977b88cf9914aa6e3f97f3368415", 1: "9ee315e960dc9754d9c7201f234c99ee", 2: "524c7b0ae261a9244b97246a5799c9b4", 3: "2d697d79043ceb1468a4f403ec2484ff",
           4: "3a17f26f6c26b8446b3b3a0e51cdcd7f", 5: "55c4d8170d158c149a9d1c46d03aea9a"}   # 0.15.0: Port Plasma (shaft) / Port Unset (bearing)
ICON = "2a62eb16b31248646a75f695ccd9c6c6"
M_PIPE = "eee58eee2f7506543b6195f2da5f1869"   # mesh of SC_PipeQuarter (0.25 pipe along the Z axis)
# label (the game's actionable label, like the one of SC_LabelSmall / joysticks / levers): plate "Label Small Actionable" (quad
# 0.21 x 0.086 at z -0.042 facing +Z) + RendererText3D (text the player types by clicking it; the game's text material)
M_LABEL = "80dc220d70857b34f84abe01cacde181"
MAT_TEXT = "490dba756006ead4da58f156d65c8274"
MAT_GLASS = "b7c8a2235f0e12b49a2fb45329a188a4"   # 0.20.0: the game's glass material (SC_GlassA: Glass, layer 15) — the piston's guide bar
S_LABEL, S_TEXT3D, S_FILTER = 2117130703, 1760814809, 833806775
# port direction -> rotation (degrees) of the marker (the mesh faces +Z)
ROT_DIR = {0: (0, 90, 0), 1: (0, -90, 0), 2: (-90, 0, 0), 3: (90, 0, 0), 4: (0, 0, 0), 5: (0, 180, 0)}
PORT_NAME = {0: "Port Input", 1: "Port Output", 2: "Port Power", 3: "Port InOut", 4: "Port Shaft", 5: "Port Unset", 10: "Port Unset", 6: "Port Shaft In", 7: "Port Shaft Out", 8: "Port Shaft InOut"}
# 0.16.0 (Andre: "purple for output (#642d9a) and orange for input (#ebb4a2)"): kinds only for the generator — in the prefab they become a plasma port (4);
# the plug is the game's Port Plasma recolored at runtime (marker "Game_Port Plasma~<color>", GameMeshes.cs). 8 = half of each (transmitter).
SHAFT_COLORS = {6: "ebb4a2", 7: "642d9a", 8: "mix"}
REAL_KIND = {6: 4, 7: 4, 8: 4, 10: 5}   # 10 = port without a kind (bearing) with a mixed plug (Port Unset~mix)
def port_mesh(t):
    if t == 10:
        import gen_meshes; return gen_meshes.marker_guid("Port Unset~mix")
    if t in SHAFT_COLORS:
        import gen_meshes; return gen_meshes.marker_guid("Port Plasma~" + SHAFT_COLORS[t])
    return M_PORT[t]


def fid(*parts):
    h = hashlib.sha1(("|".join(str(p) for p in parts)).encode()).digest()
    return int.from_bytes(h[:8], "little") & 0x3FFFFFFFFFFFFFFF or 1


def quat(ex, ey, ez):  # Unity: q = qY * qX * qZ
    def axis_v(a, ang):
        h = math.radians(ang) / 2; v = [0.0, 0.0, 0.0]; v[a] = math.sin(h); return (v[0], v[1], v[2], math.cos(h))
    def mul(p, q):
        px, py, pz, pw = p; qx, qy, qz, qw = q
        return (pw*qx + px*qw + py*qz - pz*qy, pw*qy - px*qz + py*qw + pz*qx, pw*qz + px*qy - py*qx + pz*qw, pw*qw - px*qx - py*qy - pz*qz)
    return mul(mul(axis_v(1, ey), axis_v(0, ex)), axis_v(2, ez))


def v3(v): return "{x: %s, y: %s, z: %s}" % tuple(fmt(a) for a in v)
def fmt(a): return ("%.6f" % a).rstrip("0").rstrip(".") if a != int(a) else str(int(a))


def faces_(b):
    """6 faces of the box b (size) centered on the origin: (name, vertices)."""
    hx, hy, hz = b[0] / 2, b[1] / 2, b[2] / 2
    return [
        ("-Y", [(hx, -hy, -hz), (-hx, -hy, -hz), (-hx, -hy, hz), (hx, -hy, hz)]),
        ("+Y", [(hx, hy, -hz), (-hx, hy, -hz), (-hx, hy, hz), (hx, hy, hz)]),
        ("-X", [(-hx, -hy, -hz), (-hx, hy, -hz), (-hx, hy, hz), (-hx, -hy, hz)]),
        ("+X", [(hx, -hy, -hz), (hx, hy, -hz), (hx, hy, hz), (hx, -hy, hz)]),
        ("-Z", [(-hx, -hy, -hz), (hx, -hy, -hz), (hx, hy, -hz), (-hx, hy, -hz)]),
        ("+Z", [(-hx, -hy, hz), (hx, -hy, hz), (hx, hy, hz), (-hx, hy, hz)]),
    ]


# 0.11.1: the text and the click box are CHILDREN of the label's entity (Parent = label): position/rotation relative to it.
# In 0.11.0 they were in block coordinates -> the click box fell outside the block and the label couldn't be clicked.
# Values from the game's SC_LabelSmall: text at (0, 0, -0.0387) rot (270, 180, 0); box at (0, 0, -0.04) rot 0, 0.21 x 0.085 x 0.025.
LBL_TEXT_POS, LBL_TEXT_ROT = (0, 0, -0.0387), (270, 180, 0)
LBL_COL_OFF, LBL_COL_SCALE = (0, 0, -0.04), (0.21, 0.085, 0.025)
LBL_QUAD = 0.042   # the plate's quad (mesh Label Small Actionable) sits at z -0.042 from the origin, facing +Z


def label_at(bottom, n, rot, max_len=14):
    """The game's label in a pocket: 'bottom' = center of the pocket's bottom (block coordinates), 'n' = the face normal (outward),
    'rot' = Euler (Unity) that turns the quad (faces +Z, up +Y) to face n with the text upright. The plate sits 2 mm above the
    bottom; the entity's origin is LBL_QUAD behind it (the quad is at z -0.042 of the mesh)."""
    f = [float(a) for a in bottom]; n = [float(a) for a in n]
    org = tuple(f[i] + n[i] * (0.002 + LBL_QUAD) for i in range(3))
    return dict(quad_pos=org, quad_rot=tuple(rot), text_pos=LBL_TEXT_POS, text_rot=LBL_TEXT_ROT,
                col_off=LBL_COL_OFF, col_rot=(0, 0, 0), col_esc=LBL_COL_SCALE, max_len=max_len)


def label_face_x(y, x_face=0.25, depth=0.012, max_len=14):
    """Label on the block's +X face (pocket of 'depth'), centered at y."""
    return label_at((x_face - depth, y, 0), (1, 0, 0), (0, 90, 0), max_len)


def label_face_minus_z(x, y, z_face=-0.25, depth=0.012, max_len=10):
    """Label on the -Z face (the front of the controls: the player looks from -Z toward +Z)."""
    return label_at((x, y, z_face + depth), (0, 0, -1), (0, 180, 0), max_len)


def prefab(name, bounds, mass, areas_, ports, collider=True, body_scale=None, convex=False, only_paintable=False, script=S_EPC, root_extra="", trims=(), pipe_icon=None, label=None, labels=(), levers=None, box_collider=None, sphere_collider=None, extra_paintables=(), paint_extras=(), game_meshes=None, texts=(), default_color=0):
    """areas: [(kind, vertices)]; ports: [(pos, direction, kind)]. 0.9.0: meshes and icon of our own (gen_meshes) when REF has the block.
    label/labels (0.10.2/0.11.1): dicts from label_at — puts the game's actionable label (plate + editable text) on the block.
    levers (0.11.1): {suffix of the extra: (rot_min, rot_max, capsule)} — the extra renderer with that suffix becomes a game
    lever (EPC_Actionable_Lever, vertical axis, back to the middle, + a click capsule relative to it). The plugin reads the value.
    box_collider (0.11.1): (offset, scale) of the collision box, when it isn't the whole block."""
    ICON_PORTS[name] = list(ports) + ([(pipe_icon, 0, 9)] if pipe_icon else [])
    GLUE_AREAS[name] = [vs for _, vs in areas_]   # 0.22.5: they go into the _boundsCollider hull (the glue face stays on the edge)
    own = REF.get(name)
    root_go, root_tr, root_mb = fid(name, "go"), fid(name, "tr"), fid(name, "mb")
    children = []   # (go, tr, mb, name, layer, text_mb, pos, rot_euler, scale)
    scl = body_scale or tuple(b / 0.5 for b in bounds)

    def child(tag, goname, layer, script, body, pos=(0, 0, 0), rot=(0, 0, 0), scale=(1, 1, 1)):
        children.append((fid(name, tag, "go"), fid(name, tag, "tr"), fid(name, tag, "mb"), goname, layer, script, body, pos, rot, scale))
        return children[-1][2]

    for i, (kind, vs) in enumerate(areas_):
        body = "  _type: %d\n  _material: 4\n  _vertices:\n" % kind + "".join("  - x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in v) for v in vs)
        child("area%d" % i, "JointsArea" + ("" if i == 0 else " (%d)" % i), 7, S_AREA, body)

    def rend(pos, rot, scale, mesh):
        return ("  _position:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in pos) +
                "  _rotation:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in rot) +
                "  _scale:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in scale) +
                "  _mesh: {fileID: 4300000, guid: %s, type: 2}\n  _submeshID: 0\n" % mesh +
                "  _material: {fileID: 2100000, guid: %s, type: 2}\n  _layer: 0\n  _color: {r: 0, g: 0, b: 0, a: 0}\n" % MAT +
                "  _specular:\n    _strength: 0\n    _smoothness: 0\n  _hasHeat: 1\n  _spaceshipColor: 0\n")

    for i, (pos, d, t) in enumerate(ports):
        r_p = rend(pos, ROT_DIR[d], (1, 1, 1), port_mesh(t))
        # 0.22.2: pass-through port (kind 5) = the game's Port Unset plug; the plugin swaps the mesh for the connected cable's (SlipRings.cs), like the Frame With Ports
        child("port%d" % i, PORT_NAME[t] + ("" if i == 0 else " %d" % i), 0, S_REND, r_p, pos, ROT_DIR[d])
    levers = levers or {}; levs = []   # (go, tr, rend, lev, caps, filt, name, pos, rot, mesh, kind, rot_min, rot_max, capsule)
    for i, ex in enumerate(list(trims) + (list(own[3]) if own and len(own) > 3 else [])):   # extra meshes (0.11.0: piston rod/head)
        pos, rot, scale, mesh = ex[:4]; sfx = ex[4] if len(ex) > 4 else None
        if sfx in levers:
            kind, rmin, rmax, caps_ = levers[sfx]
            levs.append((fid(name, sfx, "go"), fid(name, sfx, "tr"), fid(name, sfx, "rend"), fid(name, sfx, "lev"), fid(name, sfx, "caps"), fid(name, sfx, "filt"),
                          "Actionable Lever" + ("" if len(levs) == 0 else " %d" % len(levs)), pos, rot, mesh, kind, rmin, rmax, caps_))
            continue
        r_ex = rend(pos, rot, scale, mesh)
        if sfx == "Guide":   # 0.20.0: the piston's guide bar = the game's GLASS material (translucent), the glass layer, color alpha 1 (tintable by the plugin)
            r_ex = r_ex.replace("_material: {fileID: 2100000, guid: %s, type: 2}\n  _layer: 0\n  _color: {r: 0, g: 0, b: 0, a: 0}\n" % MAT,
                                "_material: {fileID: 2100000, guid: %s, type: 2}\n  _layer: 15\n  _color: {r: 0.3, g: 0.9, b: 0.4, a: 1}\n" % MAT_GLASS, 1)
        if sfx == "led":
            r_ex = r_ex.replace("  _specular:\n    _strength: 0\n", "  _specular:\n    _strength: %s\n" % LED_MARK, 1)   # 0.19.2: LED mark (the plugin finds it by the specular)
            # 0.19.8: like the indicator of the game's transmitter — alpha 1 (with alpha 0 the renderer is born WITHOUT CRPRendererMemory_Color and the plugin
            # has nowhere to paint) and the indicator's material (CRPUnlit: the LED really glows)
            r_ex = r_ex.replace("  _color: {r: 0, g: 0, b: 0, a: 0}\n", "  _color: {r: 0, g: 0, b: 0, a: 1}\n", 1).replace("_material: {fileID: 2100000, guid: %s, type: 2}" % MAT, "_material: {fileID: 2100000, guid: %s, type: 2}" % MAT_LED, 1)
        mb_ex = child("trim%d" % i, "Renderer Extra" + ("" if i == 0 else " %d" % i), 0, S_REND, r_ex, pos, rot, scale)
        if sfx in paint_extras: extra_paintables = list(extra_paintables) + [mb_ex]   # 0.12.1: paintable extra (turret plates)
    if game_meshes:   # 0.18.0: body/paintable = the game's meshes (markers relinked at runtime) — the model of our own is only in the icon
        one = (1, 1, 1)   # 0.22.2: body None = mesh of our own (LRT: antenna + globe) with the game's paintable base
        child("body", "Renderer", 0, S_REND, rend((0, 0, 0), (0, 0, 0), one, game_meshes[0] or (own[0] if own else M_BODY)), scale=one)
        paint_ = child("paintable", "Paintable", 0, S_REND, rend((0, 0, 0), (0, 0, 0), one, game_meshes[1]), scale=one)
    elif own:
        one = (1, 1, 1)
        child("body", "Renderer", 0, S_REND, rend((0, 0, 0), (0, 0, 0), one, own[0]), scale=one)
        paint_ = child("paintable", "Paintable", 0, S_REND, rend((0, 0, 0), (0, 0, 0), one, own[1] or own[0]), scale=one)
    else:
        if not only_paintable:
            child("body", "Renderer", 0, S_REND, rend((0, 0, 0), (0, 0, 0), scl, M_BODY), scale=scl)
        paint_ = child("paintable", "Paintable", 0, S_REND, rend((0, 0, 0), (0, 0, 0), scl, M_PAINTABLE), scale=scl)
    for i, tx in enumerate(texts):   # 0.18.2: the game's 3D text, display only (the plugin writes it) — the Long Range Transmitter's channel number
        if tx.get("paintable"): extra_paintables = list(extra_paintables) + [fid(name, "text%d" % i, "mb")]   # 0.22.2: like the channel number of the game's transmitter
        child("text%d" % i, tx.get("name", "RendererText3D"), 7, S_TEXT3D,
              "  _position:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in tx["pos"]) +
              "  _rotation:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in tx.get("rot", (0, 0, 0))) +
              "  _scale:\n    x: 1\n    y: 1\n    z: 1\n  _text: '%s'\n  _alignment: %d\n  _verticalAlignment: %d\n  _monospace: %d\n  _textScale: %s\n  _maxWidth: %s\n" % (
                  tx.get("text", ""), tx.get("align", 1), tx.get("valin", 1), tx.get("mono", 0), fmt(tx.get("scale", 0.1)), fmt(tx.get("wid", 0))) +
              "  _material: {fileID: 2100000, guid: %s, type: 2}\n  _layer: 7\n  _color: {r: 0, g: 0, b: 0, a: 0}\n  _specular:\n    _strength: 0\n    _smoothness: 0\n  _hasHeat: 1\n" % MAT_TEXT)
    cols = []
    if sphere_collider:   # 0.12.0 (turret seat): sphere collider (DOTSColliderSphere) — turning on any axis it doesn't go into the block below
        cols.append(child("collider", "Collider", 7, S_COL_SPHERE, "  _center:\n    x: %s\n    y: %s\n    z: %s\n  _radius: %s\n" % (tuple(fmt(a) for a in sphere_collider[0]) + (fmt(sphere_collider[1]),))))
    elif collider and convex:
        cols.append(child("collider", "Collider", 7, S_COL_CONVEX, "  _mesh: {fileID: 0}\n  _offset:\n    x: 0\n    y: 0\n    z: 0\n  _rotation:\n    x: 0\n    y: 0\n    z: 0\n" +
                          "  _scale:\n    x: 1\n    y: 1\n    z: 1\n  _bevelRadius: 0.02\n"))
    elif collider and own and len(own) > 4 and own[4]:   # 0.19.3: the hulls are worth more than the hand-made boxes
        # 0.19.2: convex hulls generated from the model (gen_meshes.hulls): one DOTSColliderConvex per hull
        for k, g in enumerate(own[4]):
            cols.append(child("collider%d" % k, "Collider" + ("" if k == 0 else " %d" % k), 7, S_COL_CONVEX, "  _mesh: {fileID: 4300000, guid: %s, type: 2}\n  _offset:\n    x: 0\n    y: 0\n    z: 0\n  _rotation:\n    x: 0\n    y: 0\n    z: 0\n" % g +
                              "  _scale:\n    x: 1\n    y: 1\n    z: 1\n  _bevelRadius: 0.006\n"))
    elif collider:
        # 0.11.2: box_collider can be a list of (offset, scale) — several boxes (pedals: the outputs' box + the front crossbar)
        boxes = box_collider if box_collider else [((0, 0, 0), bounds)]
        if boxes and not isinstance(boxes[0][0], (tuple, list)): boxes = [boxes]
        for k, (c_off, c_esc) in enumerate(boxes):
            cols.append(child("collider%d" % k, "Collider" + ("" if k == 0 else " %d" % k), 7, S_COL, "  _offset:\n    x: %s\n    y: %s\n    z: %s\n  _rotation:\n    x: 0\n    y: 0\n    z: 0\n" % tuple(fmt(a) for a in c_off) +
                              "  _scale:\n    x: %s\n    y: %s\n    z: %s\n  _bevelRadius: 0.025\n" % tuple(fmt(a) for a in c_esc)))

    out = ["%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:"]

    def go(g, lengths, layer, n):
        out.append("--- !u!1 &%d" % g)
        out.append("GameObject:\n  serializedVersion: 6\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_Component:\n" +
                   "".join("  - component: {fileID: %d}\n" % c for c in lengths) +
                   "  m_Layer: %d\n  m_Name: %s\n  m_TagString: Untagged\n  m_Icon: {fileID: 0}\n  m_NavMeshLayer: 0\n  m_StaticEditorFlags: 0\n  m_IsActive: 1" % (layer, n))

    def tr(t, g, parent, children_tr, pos=(0, 0, 0), rot=(0, 0, 0), scale=(1, 1, 1)):
        q = quat(*rot)
        out.append("--- !u!4 &%d" % t)
        out.append("Transform:\n  serializedVersion: 2\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: %d}\n" % g +
                   "  m_LocalRotation: {x: %s, y: %s, z: %s, w: %s}\n" % tuple(fmt(round(a, 7)) for a in q) +
                   "  m_LocalPosition: %s\n  m_LocalScale: %s\n  m_ConstrainProportionsScale: 0\n" % (v3(pos), v3(scale)) +
                   ("  m_Children: []\n" if not children_tr else "  m_Children:\n" + "".join("  - {fileID: %d}\n" % f for f in children_tr)) +
                   "  m_Father: {fileID: %d}\n  m_LocalEulerAnglesHint: %s" % (parent, v3(rot)))

    def mb(m, g, script, body):
        out.append("--- !u!114 &%d" % m)
        out.append("MonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: %d}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n" % g +
                   "  m_Script: {fileID: %d, guid: %s, type: 3}\n  m_Name: \n  m_EditorClassIdentifier: \n" % (script, G) + body.rstrip("\n"))

    # label: GameObject "Actionable Label" (EPC_ActionableLabel + EPC_Renderer of the plate + click box + filter) with the
    # child "RendererText3D"
    label_list = ([label] if label else []) + list(labels)
    lbs = []
    for k, e in enumerate(label_list):
        tag = "etq" if k == 0 else "etq%d" % k
        lbs.append(dict(e=e, go=fid(name, tag, "go"), tr=fid(name, tag, "tr"), act=fid(name, tag, "act"), rend=fid(name, tag, "rend"),
                        col=fid(name, tag, "col"), filt=fid(name, tag, "filt"), tgo=fid(name, tag, "tgo"), ttr=fid(name, tag, "ttr"), txt=fid(name, tag, "txt"),
                        goname="Actionable Label" + ("" if k == 0 else " %d" % k)))
    for lb in lbs: extra_paintables = list(extra_paintables) + [lb["act"], lb["txt"]]   # 0.22.1: like the game's transmitter (the label paints the plate, the text is paintable)
    go(root_go, [root_tr, root_mb], 7, name)
    for f in children: go(f[0], [f[1], f[2]], f[4], f[3])
    for a in levs: go(a[0], [a[1], a[2], a[3], a[4], a[5]], 0, a[6])
    for lb in lbs:
        go(lb["go"], [lb["tr"], lb["act"], lb["rend"], lb["col"], lb["filt"]], 7, lb["goname"])
        go(lb["tgo"], [lb["ttr"], lb["txt"]], 7, "RendererText3D")
    tr(root_tr, root_go, 0, [f[1] for f in children] + [a[1] for a in levs] + [lb["tr"] for lb in lbs])
    root = ("  _mirroredVersion: {fileID: 0}\n  _bounds:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in bounds) +
            "  _mass: %s\n  _oceanFloatingSetup:\n    _linearSlowness: 2.5\n    _buoyancy: 0.7\n  _maxTemperature: 2\n" % fmt(mass) +
            ("  _colliders:\n" + "".join("  - {fileID: %d}\n" % c for c in cols) if cols else "  _colliders: []\n") +
            "  _colliderAirResistant: 1\n  _scGroup: 0\n  _scSecondaryGroup: 0\n  _availableAmount: 500\n  _inventoryDependency: []\n  _boundsCollider: {fileID: 0}\n" +
            "  _defaultPaintableColor: %d\n  _paintableRenderers:\n  - {fileID: %d}\n" % (default_color, paint_) + "".join("  - {fileID: %d}\n" % x for x in extra_paintables) + "  _collisionSoundMaterial: 1\n" +
            "  _soundObstacleMin:\n    x: 0\n    y: 0\n    z: 0\n  _soundObstacleMax:\n    x: 0\n    y: 0\n    z: 0\n" +
            "  _electricPorts:\n" + "".join("  - _position:\n      x: %s\n      y: %s\n      z: %s\n    _direction: %d\n    _type: %d\n" % (fmt(p[0]), fmt(p[1]), fmt(p[2]), d, REAL_KIND.get(t, t)) for p, d, t in ports) +
            "  _iconTexture2D: {fileID: 2800000, guid: %s, type: 3}\n" % (own[2] if own else ICON) +
            "  _uiPreviewBounds:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in bounds) +
            "  _categories: 512\n  _customProperties: []\n  _propertiesMaterial: 255\n" + root_extra)
    mb(root_mb, root_go, script, root)
    for f in children:
        tr(f[1], f[0], root_tr, [], f[7], f[8], f[9])
        mb(f[2], f[0], f[5], f[6])
    for a in levs:   # lever: renderer of the mesh at the pivot + the lever's script + click capsule + filter
        _, _, a_rend, a_lev, a_caps, a_filt, _, pos, rot, mesh, kind, rmin, rmax, caps_ = a
        tr(a[1], a[0], root_tr, [], pos, rot)
        mb(a_rend, a[0], S_REND, rend(pos, rot, (1, 1, 1), mesh))
        if kind == "button":
            # 0.12.2: the game's button (EPC_Actionable_Button: 1 while pressed, comes back by itself); rmax = pressed rotation; caps = click box
            # 0.12.3: the game writes the button's LocalTransform = On/Off pose -> the position has to be the pivot's (0.12.2 set 0: the
            # two pedals went to the center of the block and had the same x — the plugin swapped left/right)
            pp = tuple(fmt(x) for x in pos)
            mb(a_lev, a[0], S_BUTTON, "  _lookAt: 0\n  _buttonTransformOnPos:\n    x: %s\n    y: %s\n    z: %s\n" % pp + "  _buttonTransformOnRot:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(x) for x in rmax) +
               "  _buttonTransformOffPos:\n    x: %s\n    y: %s\n    z: %s\n" % pp + "  _buttonTransformOffRot:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(x) for x in rmin))
            c_off, c_rot, c_esc = caps_
            mb(a_caps, a[0], S_COL, "  _offset:\n    x: %s\n    y: %s\n    z: %s\n  _rotation:\n    x: %s\n    y: %s\n    z: %s\n  _scale:\n    x: %s\n    y: %s\n    z: %s\n  _bevelRadius: 0\n" % (tuple(fmt(x) for x in c_off) + tuple(fmt(x) for x in c_rot) + tuple(fmt(x) for x in c_esc)))
            mb(a_filt, a[0], S_FILTER, "  _collisionFilterType: 0\n  _materialType: 0\n")
            continue
        if kind == "switch":
            # 0.23.0: the game's ActionableToggle (same script as the Datameter's Input Switch): two poses of the rod; the value (0/1) comes out
            # in the entity's ActionableData, read by the plugin (JointSystem.SwitchOn). caps = (offset, rotation, scale) of the click box
            pp = tuple(fmt(x) for x in pos)
            mb(a_lev, a[0], S_TOGGLE, "  _drawUIValue: 2\n  _buttonTransformOnPos:\n    x: %s\n    y: %s\n    z: %s\n" % pp +
               "  _buttonTransformOnRot:\n    x: %s\n    y: %s\n    z: %s\n  _meshOn: {fileID: 0}\n" % tuple(fmt(x) for x in rmax) +
               "  _buttonTransformOffPos:\n    x: %s\n    y: %s\n    z: %s\n" % pp +
               "  _buttonTransformOffRot:\n    x: %s\n    y: %s\n    z: %s\n  _meshOff: {fileID: 0}\n" % tuple(fmt(x) for x in rmin) +
               "  _defaultValue: 1\n  _canGoOn: 1\n  _canGoOff: 1\n")
            c_off, c_rot, c_esc = caps_
            mb(a_caps, a[0], S_COL, "  _offset:\n    x: %s\n    y: %s\n    z: %s\n  _rotation:\n    x: %s\n    y: %s\n    z: %s\n  _scale:\n    x: %s\n    y: %s\n    z: %s\n  _bevelRadius: 0\n" % (tuple(fmt(x) for x in c_off) + tuple(fmt(x) for x in c_rot) + tuple(fmt(x) for x in c_esc)))
            mb(a_filt, a[0], S_FILTER, "  _collisionFilterType: 0\n  _materialType: 0\n")
            continue
        if kind == "knob":
            # 0.17.0: the game's knob (EPC_Actionable_Knob, like the ones of the Camera Overlay): _roundingIntervals = number of positions; turns in Y
            # from _rotationMin to _rotationMax; click = sphere (caps = radius). 0.19.2: caps can be (radius, intervals, default value)
            # 0.19.3: 4th item = center of the click sphere (knob on a vertical panel: along Z)
            radius, interv, default, center = (caps_, 3, 0, (0, 0.007, 0)) if not isinstance(caps_, tuple) else (caps_ + (3, 0, (0, 0.007, 0)))[:4]
            mb(a_lev, a[0], S_KNOB, "  _defaultValue: %s\n  _sensitivity:\n    x: 0.07\n    y: 0.07\n  _roundingIntervals: %d\n  _loop: 0\n" % (fmt(default), interv) +
               "  _rotationMin:\n    x: %s\n    y: %s\n    z: %s\n  _rotationMax:\n    x: %s\n    y: %s\n    z: %s\n  _drawUIValue: 2\n" % (tuple(fmt(x) for x in rmin) + tuple(fmt(x) for x in rmax)))
            mb(a_caps, a[0], S_COL_SPHERE, "  _center:\n    x: %s\n    y: %s\n    z: %s\n  _radius: %s\n" % (tuple(fmt(x) for x in center) + (fmt(radius),)))
            mb(a_filt, a[0], S_FILTER, "  _collisionFilterType: 0\n  _materialType: 0\n")
            continue
        if kind == "step_on":
            # EPC_Actionable_DoubleLever in simple mode = the game's ONE-way lever (0..1: _rotationMin = rest, _rotationMax =
            # stepped on). The plugin puts ActionableJoystickReturnToMid on it (goes back to 0 by itself) — 0.11.2, pedals
            mb(a_lev, a[0], S_DOUBLE, "  _sensitivity: 0.07\n  _defaultValue: 0\n  _isVertical: 1\n  _isDoubleMode: 0\n" +
               "  _rotationMin:\n    x: %s\n    y: %s\n    z: %s\n  _rotationMax:\n    x: %s\n    y: %s\n    z: %s\n" % (tuple(fmt(x) for x in rmin) + tuple(fmt(x) for x in rmax)) +
               "  _position:\n    x: %s\n    y: %s\n    z: %s\n  _zeroOffset:\n    x: 0\n    y: 0\n    z: 0\n  _drawUIValue: 1\n" % tuple(fmt(x) for x in pos))
        else:
            # EPC_Actionable_Lever (1-axis joystick, -1..1, back to the middle): "ver" = vertical axis (mouse forward/back), "hor" = horizontal
            horiz = kind == "hor"
            zero = (0, 0, 0)
            axes = (("HorMin", pos if horiz else zero, rmin if horiz else zero), ("HorMax", pos if horiz else zero, rmax if horiz else zero),
                     ("VerMin", zero if horiz else pos, zero if horiz else rmin), ("VerMax", zero if horiz else pos, zero if horiz else rmax),
                     ("ZMin", zero, zero), ("ZMax", zero, zero))
            mb(a_lev, a[0], S_LEVER, "  _sensitivity: 0.07\n  _usedValues: 1\n  _roundingIntervals: 0\n  _defaultValue: 0\n" +
               "".join("  _position%s:\n    x: %s\n    y: %s\n    z: %s\n  _rotation%s:\n    x: %s\n    y: %s\n    z: %s\n" % ((k,) + tuple(fmt(x) for x in p) + (k,) + tuple(fmt(x) for x in r)) for k, p, r in axes) +
               "  _rotationOrder: 4\n  _returnToMid: 1\n  _usedAxisX: %d\n  _usedAxisY: %d\n  _usedAxisZ: 0\n  _drawUIValue: 1\n" % (1 if horiz else 0, 0 if horiz else 1))
        mb(a_caps, a[0], S_CAPSULE, "  _vertexA:\n    x: %s\n    y: %s\n    z: %s\n  _vertexB:\n    x: %s\n    y: %s\n    z: %s\n  _radius: %s\n" % (tuple(fmt(x) for x in caps_[0]) + tuple(fmt(x) for x in caps_[1]) + (fmt(caps_[2]),)))
        mb(a_filt, a[0], S_FILTER, "  _collisionFilterType: 0\n  _materialType: 0\n")
    for lb in lbs:
        e = lb["e"]
        tr(lb["tr"], lb["go"], root_tr, [lb["ttr"]])
        mb(lb["act"], lb["go"], S_LABEL, "  _rendererText3D: {fileID: %d}\n  _maxLength: %d\n  _inputType: 0\n  _enableRotation: 1\n" % (lb["txt"], e.get("max_len", 14)))
        mb(lb["rend"], lb["go"], S_REND, rend(e["quad_pos"], e["quad_rot"], (1, 1, 1), e.get("mesh", M_LABEL)))
        mb(lb["col"], lb["go"], S_COL, "  _offset:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in e["col_off"]) +
           "  _rotation:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in e["col_rot"]) +
           "  _scale:\n    x: %s\n    y: %s\n    z: %s\n  _bevelRadius: 0\n" % tuple(fmt(a) for a in e["col_esc"]))
        mb(lb["filt"], lb["go"], S_FILTER, "  _collisionFilterType: 0\n  _materialType: 0\n")
        tr(lb["ttr"], lb["tgo"], lb["tr"], [])
        mb(lb["txt"], lb["tgo"], S_TEXT3D, "  _position:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in e["text_pos"]) +
           "  _rotation:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in e["text_rot"]) +
           "  _scale:\n    x: 1\n    y: 1\n    z: 1\n  _text: \n  _alignment: 1\n  _verticalAlignment: 1\n  _monospace: 0\n  _textScale: 0.085\n  _maxWidth: 0.19\n" +
           "  _material: {fileID: 2100000, guid: %s, type: 2}\n  _layer: 7\n  _color: {r: 0, g: 0, b: 0, a: 0}\n  _specular:\n    _strength: 0\n    _smoothness: 0\n  _hasHeat: 1\n" % MAT_TEXT)
    return "\n".join(out) + "\n"


# (suffix, size of the equivalent Rechargeable Battery, mass)
# (suffix, electric box, fuel box, electric mass, fuel mass). 0.9.0 (form6): cubes 0.5 / 1 / 1.5;
# 0.10.2 (form9): fuel Small = 4x8x4 cells (0.5 x 1 x 0.5) with the ports in a 2x2 group beside the pipe; electric Small = 0.5 cube
MOTORS = [("Small", (0.5, 0.5, 0.5), (0.5, 1, 0.5), 160, 200), ("Medium", (1, 1, 1), (1, 1, 1), 730, 730), ("Large", (1.5, 1.5, 1.5), (1.5, 1.5, 1.5), 1450, 1450)]
REF = {}            # name -> (guid of the fixed mesh, guid of the paintable mesh, guid of the icon), filled in by gen_meshes
ICON_PORTS = {}   # name -> ports (+ pipe mouth) for the icon
GLUE_AREAS = {}     # 0.22.5: name -> polygons of the glue areas (JointsArea)
# 0.22.5 (Andre: "see how the game does it ... curved frames have these 'ears' in the volume"): EPC_SpaceshipComponent._boundsCollider = mesh;
# the game (EPC_SpaceshipComponent.CreateBaseColliderBAR) makes its CONVEX HULL (Core.getOrCreateConvexCollider) and uses it as the volume
# taken in the garage (GarageGrabber.CanMountComponentToGaragePos tests against these colliders); no mesh = the whole _bounds box.
# Here: convex hull of the model (body + paintable) + the glue areas, so round/irregular parts free up the corners.
# (motors are left out for now: their glue is all 6 whole faces, so the hull becomes the box itself — need to decide where the glue shrinks)
BOUNDS_BLOCKS = ["SC_MotorSmall", "SC_MotorMedium", "SC_MotorLarge", "SC_MotorFuelSmall", "SC_MotorFuelMedium", "SC_MotorFuelLarge", "SC_TurretSeat", "SC_TankSmallGreen", "SC_TankSmallBlue",
                 "SC_WheelSportsSmall", "SC_WheelSportsMedium", "SC_WheelSportsLarge", "SC_WheelOffroadSmall", "SC_WheelOffroadMedium", "SC_WheelOffroadLarge"]   # 0.22.6: + motors (glue only at the bottom) and turret + ["SC_WheelSportsSmall", "SC_WheelSportsMedium", "SC_WheelSportsLarge", "SC_WheelOffroadSmall", "SC_WheelOffroadMedium", "SC_WheelOffroadLarge"]

# wheels (Andre, form3 + 25/09): sports = light, friction 1.5; offroad = a bit heavier, friction 2.2 (420 kg knocked the bearing over); Small/Medium/Large = 1 / 1.5 / 2 m in diameter, 0.5 m wide
WHEELS = [("SC_WheelSportsSmall", 1.0, 40), ("SC_WheelSportsMedium", 1.5, 80), ("SC_WheelSportsLarge", 2.0, 140),
         ("SC_WheelOffroadSmall", 1.0, 60), ("SC_WheelOffroadMedium", 1.5, 120), ("SC_WheelOffroadLarge", 2.0, 200)]


def blocks():
    r = {}
    # pistons: quarter block, bottom face glues (Normal), top face loose (Decoupler); input + power on the +X face
    # 0.22.5 (Andre: "you can remove all the old items that left the inventory"): the 2/4/8 pistons and the cube Piston End are gone
    fs = dict(faces_((0.5, 0.5, 0.5)))
    # 0.20.0 (Andre: "remove the piston tiers, leave just one piston with an 'infinite' level"): a single piston (up to 8 m; with the Piston End
    # in front the stroke is the distance to it) + Mini Piston (0.25 m, up to 4 m). The 2/4/8 stay registered (old BPs), hidden
    # from the inventory (Mirrors.cs). Extra renderer "Guide" (the game's glass): the plugin stretches it with H in the garage.
    # 0.22.0 (Andre: slip ring): pass-through ports without a kind (kind 10) on the -Z face (opposite the knob) — 4 on the 0.5 block (bottom
    # row), 1 on the mini (cell +X/-Y) — always the LAST ones in the list (the plugin: index >= total - SlipRings.NPassThrough). The ring (SC_SlipRing /
    # SC_MiniSlipRing) on the moving piece has the same ports; the plugin links port i <-> port i (SlipRings.cs).
    PASSAGE_4 = [((x, -0.1875, -0.1875), 5, 5) for x in (-0.1875, -0.0625, 0.0625, 0.1875)]   # 6.25 cm into the face (cell center), like every port. 0.22.1 (Andre): kind 5 = plug of the Frame Quarter With Ports (gray without a cable, the cable's color when connected)
    PASSAGE_1 = [((0.0625, -0.0625, -0.0625), 5, 5)]
    # 0.22.1 (Andre: L-shaped ports in the same cell don't connect): the piston's pass-through ones go to the -X face (opposite the input/power)
    PASSAGE_4X = [((-0.1875, -0.1875, z), 1, 5) for z in (-0.1875, -0.0625, 0.0625, 0.1875)]
    PASSAGE_1X = [((-0.0625, -0.0625, 0.0625), 1, 5)]
    # 0.23.0 (Andre: "he wants to know the bearing's current rotation to do a calculation ... add a data output"): angle
    # output, ALWAYS the LAST in the list (the pass-through ones keep the same index as in old BPs; SlipRings.NOutputs discounts this one).
    # Bearing: cell +X of the bottom row of the knob's face (+Z). Mini: the two free cells are on the -X side, so it goes on the -Z face,
    # next to the pass-through one.
    EXIT_ANG = [((0.1875, -0.1875, 0.1875), 4, 1)]
    EXIT_ANG_M = [((-0.0625, -0.0625, -0.0625), 5, 1)]
    SW_BEARING = {"switch": ("switch", (20, 0, 0), (-20, 0, 0), ((0, 0, 0.012), (0, 0, 0), (0.05, 0.05, 0.03)))}
    SW_MINI_S = {"switch": ("switch", (20, 0, 0), (-20, 0, 0), ((0, 0, 0.010), (0, 0, 0), (0.043, 0.043, 0.026)))}
    r["SC_Piston"] = prefab("SC_Piston", (0.5, 0.5, 0.5), 80, [(0, fs["-Y"]), (3, fs["+Y"])],
                            [((0.1875, -0.1875, 0.1875), 0, 0), ((0.1875, -0.1875, -0.1875), 0, 2)] + PASSAGE_4X,
                            levers={"Knob": ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, 1.0, (0, 0, 0.007)))})
    fm = dict(faces_((0.25, 0.25, 0.25)))
    r["SC_MiniPiston"] = prefab("SC_MiniPiston", (0.25, 0.25, 0.25), 20, [(0, fm["-Y"]), (3, fm["+Y"])],
                                [((0.0625, -0.0625, 0.0625), 0, 0), ((0.0625, -0.0625, -0.0625), 0, 2)] + PASSAGE_1X,
                                levers={"Knob": ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, 1.0, (0, 0, 0.007)))})
    # 0.22.4 (Andre: "do it like the logic blocks, which are 2x1x2"): Slip Ring = 0.5 x 0.125 x 0.5 plate (like the mini solar panel),
    # Mini Slip Ring = 0.25 x 0.125 x 0.25 plate; glue on all 6 faces; 4 pass-through ports, 2 on each opposite side (1-2 on -X, 3-4 on +X).
    # Any ring works on any joint. Piston End as a plate in the same 2 sizes (the cube SC_PistaoPonta stays for old BPs,
    # hidden from the inventory).
    for name, w, mass in (("SC_SlipRing", 0.5, 4), ("SC_MiniSlipRing", 0.25, 1.5)):
        bb = (w, 0.125, w); cx = w / 2 - 0.0625
        r[name] = prefab(name, bb, mass, [(0, v) for _, v in faces_(bb)],
                         [((-cx, 0, z), 1, 5) for z in (-0.0625, 0.0625)] + [((cx, 0, z), 0, 5) for z in (-0.0625, 0.0625)])
    for name, w, mass in (("SC_PistonEnd", 0.5, 5), ("SC_MiniPistonEnd", 0.25, 2)):
        bb = (w, 0.125, w)
        r[name] = prefab(name, bb, mass, [(0, v) for _, v in faces_(bb)], [])
    # 0.20.0: Mini Bearing / Mini Gyro Bearing (0.25 m): a single input, on the bottom face. 0.20.1 (Andre: "the mini bearing's input is in the
    # wrong place ... move it to one of the ends"): the block's center falls on a LINE of the port grid, not in a cell — the input goes in the
    # +X/+Z cell (the M version mirrors it to -X)
    r["SC_MiniBearing"] = prefab("SC_MiniBearing", (0.25, 0.25, 0.25), 10, [(0, fm["-Y"]), (3, fm["+Y"])], [((0.0625, -0.0625, 0.0625), 3, 10)] + PASSAGE_1 + EXIT_ANG_M,
                                 levers=dict({"Knob": ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, 0.1, (0, 0, 0.007)))}, **{"Switch": SW_MINI_S["switch"]}))
    r["SC_MiniGyroBearing"] = prefab("SC_MiniGyroBearing", (0.25, 0.25, 0.25), 10, [(0, fm["-Y"]), (3, fm["+Y"])], [((0.0625, -0.0625, 0.0625), 3, 10)] + PASSAGE_1 + EXIT_ANG_M,
                                     levers=dict({"Knob": ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, 0.1, (0, 0, 0.007)))}, **{"Switch": SW_MINI_S["switch"]}))
    # bearing: 0.5 m quarter (the frame grid steps by 0.5). Bottom face glues (Normal), top face loose
    # (Decoupler) and it turns around +Y. 4 inputs on the 4 small squares at the center of the bottom face, facing down
    # (they match the ports of the Frame Quarter With Ports).
    fs = dict(faces_((0.5, 0.5, 0.5)))
    r["SC_Bearing"] = prefab("SC_Bearing", (0.5, 0.5, 0.5), 40, [(0, fs["-Y"]), (3, fs["+Y"])],
                             [((x, -0.1875, z), 3, 10) for x, z in ((-0.0625, -0.0625), (0.0625, -0.0625), (0.0625, 0.0625), (-0.0625, 0.0625))] + PASSAGE_4 + EXIT_ANG,
                             levers=dict({"Knob": ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, 0.1, (0, 0, 0.007)))}, **{"Switch": SW_BEARING["switch"]}))   # 0.19.2: servo angle knob (continuous, default 0.1 = 45 degrees); 0.19.3: turns in Z (panel +Z)   # 0.15.0: ports WITHOUT a KIND (shaft = motor, data = servo); 0.17.1: half orange half purple plug (10)
    # 0.19.14: Gyro Bearing — same block, the servo aims by itself to keep the top side level (Gyro.cs); signal = offset
    r["SC_GyroBearing"] = prefab("SC_GyroBearing", (0.5, 0.5, 0.5), 40, [(0, fs["-Y"]), (3, fs["+Y"])],
                                 [((x, -0.1875, z), 3, 10) for x, z in ((-0.0625, -0.0625), (0.0625, -0.0625), (0.0625, 0.0625), (-0.0625, 0.0625))] + PASSAGE_4 + EXIT_ANG,
                                 levers=dict({"Knob": ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, 0.1, (0, 0, 0.007)))}, **{"Switch": SW_BEARING["switch"]}))
    # motors: size of the Rechargeable Batteries of the same level (Andre, 25/09: the thruster-sized ones were oversized);
    # mass, consumption and force stay those of the Electric Thrusters. Glue on all 6 faces. Face +X, bottom row:
    # input and power at the -Z corner (side by side, like the thruster), output at the +Z corner.
    # 0.10.1 (form8): 4th port = BRAKE (yellow input, last in the list so the other indices don't move), next to the power.
    for sfx, b, _, m, _ in MOTORS:
        x, y, z0 = b[0] / 2 - 0.0625, -b[1] / 2 + 0.0625, -b[2] / 2 + 0.0625
        r["SC_Motor" + sfx] = prefab("SC_Motor" + sfx, b, m, [(0, dict(faces_(b))["-Y"])],   # 0.22.6 (Andre: "the motor's glue is only on the bottom of the base")
                                     [((x, y, z0), 0, 0), ((x, y, z0 + 0.125), 0, 2), ((x, y, -z0), 0, 7), ((x, y, z0 + 0.25), 0, 0)])   # 0.15.0: output = Drive Shaft (plasma)
    # fuel motors (0.8.0, form5; 0.8.1 Andre's request: everything on the +X face). Bottom row of the +X face:
    # [0.25 x 0.25 pipe at the -Z corner] ... [yellow input][blue output][red power at the +Z corner]. On the small one (4 cells)
    # it doesn't fit: power goes up a row, above the output. Pipe = 0.25 square (0.25 grid) at the bottom/-Z corner; the rest
    # of the +X face glues (2 rectangles outside the square). Visible pipe mouth = mesh of the game's Pipe Quarter, shallow, on the face.
    # Root script = EPC_SCPipe: for the game it's a pipe with no outlet, which the pipes fill by themselves (FuelData); the plugin takes out what
    # the motor burns. Stability 0 = takes any mixture (like Nanopipe).
    # 0.10.2 (form9): Small = 0.5 x 1 x 0.5, pipe at the bottom/-Z corner of the +X face (like Medium/Large) and the 4 ports in a 2x2 group on the
    # +Z side of the pipe (top: input, brake; bottom: power, output — "red and blue together at the bottom"). Brake = 4th port
    # (input), last in the list.
    for sfx, _, b, _, m in MOTORS:
        hx, hy, hz = b[0] / 2, b[1] / 2, b[2] / 2
        fs = dict(faces_(b))
        yb = -hy + 0.0625
        xp = hx - 0.0625
        pipe = [(hx, -hy, -hz), (hx, -hy + 0.25, -hz), (hx, -hy + 0.25, -hz + 0.25), (hx, -hy, -hz + 0.25)]
        rest1 = [(hx, -hy, -hz + 0.25), (hx, hy, -hz + 0.25), (hx, hy, hz), (hx, -hy, hz)]
        rest2 = [(hx, -hy + 0.25, -hz), (hx, hy, -hz), (hx, hy, -hz + 0.25), (hx, -hy + 0.25, -hz + 0.25)]
        if b[2] >= 1:
            ports = [((xp, yb, hz - 0.3125), 0, 0), ((xp, yb, hz - 0.0625), 0, 2), ((xp, yb, hz - 0.1875), 0, 7), ((xp, yb, hz - 0.4375), 0, 0)]
        else:
            y1, y2, ze, zd = -hy + 0.1875, -hy + 0.0625, hz - 0.0625, hz - 0.1875   # ze = column far from the pipe (+Z), zd = next to the pipe
            ports = [((xp, y1, ze), 0, 0), ((xp, y2, ze), 0, 2), ((xp, y2, zd), 0, 7), ((xp, y1, zd), 0, 0)]
        areas_ = [(0, fs["-Y"]), (1, pipe)]   # 0.22.6: glue only at the bottom (+ the pipe mouth, which is the pipe area)
        # (0.9.1: the pipe's octagonal socket is part of our own mesh; the Pipe Quarter "mouth" isn't placed anymore)
        r["SC_MotorFuel" + sfx] = prefab("SC_MotorFuel" + sfx, b, m, areas_, ports, script=S_PIPE,
                                         root_extra="  _fuelCapacity: 0.25\n  _fuelStabilityThreshold: 0\n  _fuelStabilitySpeed: 0.25\n")
    # recoupler: quarter that glues on all 6 faces (it's part of the car); recouples when touching the loose face of a Decoupler
    # that has already fired. Input on the +X face, bottom corner (like the piston).
    fs = faces_((0.5, 0.5, 0.5))
    # 0.10.2 (Andre): the input moved up to the top band (y 0.1875) — the round mouth stays clean like the decoupler's
    # 0.22.0: + 4 pass-through ports (slip ring) on the top band of the -X face (opposite the input); the ring sits glued on the decoupler where it engages
    r["SC_Recoupler"] = prefab("SC_Recoupler", (0.5, 0.5, 0.5), 60, [(0, v) for _, v in fs], [((0.1875, 0.1875, 0.1875), 0, 0)] + [((-0.1875, 0.1875, z), 1, 5) for z in (-0.1875, -0.0625, 0.0625, 0.1875)])
    # beacon: 0.11.2 = 0.25 block like the Wireless Transmitter (wedge console, antenna passing over the block), without
    # a label (Andre: "remove the action label from everything"); glue on all 6 faces; power port on the -X face (the plugin charges 5 P/s and only
    # shows on the radar with power)
    import models_v2 as mv
    # 0.17.0: icon knob (3 positions: waves / car / ship) — the plugin reads the value and shows the icon on the display and on the radar
    r["SC_Beacon"] = prefab("SC_Beacon", (0.25, 0.25, 0.25), 6, [(0, v) for _, v in faces_((0.25, 0.25, 0.25))], BEACON_PORTS,
                            levers={"Knob": ("knob", (0, -60, 0), (0, 60, 0), 0.03)},
                            box_collider=[((0, -0.1, 0), (0.25, 0.05, 0.25)),                       # base plate
                                           ((-0.11, -0.0375, 0), (0.03, 0.075, 0.25)),                # back wall (port)
                                           ((-0.001, -0.005, 0), (0.192, 0.14, 0.19))])               # console, up to y 0.065 (the knob sits above)
    # steering wheel (0.11.2): a block of its own (generic EPC) with ONE game lever (horizontal joystick: turns in Z +-90, tilted
    # WHEEL_TILT in X = perpendicular to the column). Small base at the back/bottom corner: glues to the floor (face -Y, only the base's
    # footprint) and to the back wall (+Z); output on the bottom face of the base; collider = the base only. The plugin copies the value to the port.
    x0, x1, y0, y1, z0, z1 = mv.WHEEL_BASE
    areas_ = [(0, [(x0, y0, 0.25), (x1, y0, 0.25), (x1, y1, 0.25), (x0, y1, 0.25)]), (0, [(x1, -0.25, z0), (x0, -0.25, z0), (x0, -0.25, z1), (x1, -0.25, z1)])]   # 0.12.2: back wall + floor (small box)
    r["SC_SteeringWheel"] = prefab("SC_SteeringWheel", (0.5, 0.5, 0.5), 15, areas_, WHEEL_PORTS,
                             levers={"Lever": ("hor", (mv.WHEEL_TILT, 0, 90), (mv.WHEEL_TILT, 0, -90), ((0, 0.14, 0), (0, -0.14, 0), 0.16))},
                             box_collider=[(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), (x1 - x0, y1 - y0, z1 - z0))])   # (0.19.3: replaced by the model's hulls when they exist)
    # pedals (0.11.1/0.11.2, Andre: "two pedals on the same block, just the stepping action"): a block of its own, glues by the bottom face,
    # 2 ONE-way levers (0..1, X axis: rest = mesh already tilted, stepped on = +PEDAL_TRAVEL) and 2 outputs on the +Z face (left
    # -> port 0, right -> port 1). Colliders: the outputs' box (back) + the front crossbar (the pedals stay exposed).
    fs = dict(faces_((0.5, 0.5, 0.5)))
    caps_ = mv.pedal_click_box()   # 0.12.2 (Andre): pedals = two game BUTTONS (1 while pressed), one output (right - left)
    lev = {"LeverL": ("button", (0, 0, 0), (mv.PEDAL_TRAVEL, 0, 0), caps_), "LeverR": ("button", (0, 0, 0), (mv.PEDAL_TRAVEL, 0, 0), caps_)}
    r["SC_Pedal"] = prefab("SC_Pedal", (0.5, 0.5, 0.5), 15, [(0, fs["-Y"])], PEDAL_PORTS, levers=lev,
                           box_collider=[((0, -0.1875, 0.22), (0.5, 0.125, 0.06)), ((0, -0.2375, 0.095), (0.5, 0.025, 0.19)),
                                          ((0, -0.2325, mv.PEDAL_PIVOT_Z), (0.5, 0.035, 0.10))])
    # 0.15.0: Differential (0.25: 1 shaft input on -X, 4 outputs on +X) and Shaft Reverser (0.25 x 0.125 x 0.25 like the
    # math ones: input -Z, output +Z). Plasma ports (kind 4) = Drive Shaft; glue on all 6 faces.
    bd = (0.25, 0.25, 0.25)
    r["SC_Differential"] = prefab("SC_Differential", bd, 12, [(0, v) for _, v in faces_(bd)],
                                  [((-0.0625, y, z), 1, 6) for y in (-0.0625, 0.0625) for z in (-0.0625, 0.0625)] + [((0.0625, y, z), 0, 7) for y in (-0.0625, 0.0625) for z in (-0.0625, 0.0625)])
    brv = (0.25, 0.125, 0.25)
    r["SC_ShaftReverser"] = prefab("SC_ShaftReverser", brv, 5, [(0, v) for _, v in faces_(brv)], [((-0.0625, 0, -0.0625), 5, 6), ((-0.0625, 0, 0.0625), 4, 7)])
    # 0.18.0 (Andre): Single Cell Battery — the game's battery (EPC_SCBattery) with 1/4 of the small one (2000 -> 500), a 2x4x2 model of its own and
    # the game's charge bar; Long Range Transmitter — a block of its own with the Wireless Transmitter's meshes, 2 ports (signal in/out
    # + channel by data), the plugin passes the signal between those on the same channel with no distance limit (Radio.cs)
    bc = (0.25, 0.5, 0.25)
    r["SC_SingleCellBattery"] = prefab("SC_SingleCellBattery", bc, 36, [(0, v) for _, v in faces_(bc)], [CELL_PORT], script=S_BATTERY,
                                       trims=[(mv.CELL_INDICATOR, (0, 0, 0), (1, 1, 1), M_BATTERY_IND)],
                                       root_extra="  _availablePower: 500\n  _chargeablePower: 500\n  _indicatorTransformRenderer: {fileID: %d}\n" % fid("SC_SingleCellBattery", "trim0", "mb") +
                                                  "  _indicatorTransformMinScale:\n    x: 0\n    y: 1\n    z: 1\n  _indicator3DTextRenderer: {fileID: 0}\n",
                                       box_collider=[((0, 0, 0), (0.24, 0.5, 0.24))])
    bt = (0.25, 0.25, 0.25)
    # 0.22.1: body/paintable of our own (models_v2.radio: wedge + console + 1 antenna + globe) — the game's meshes stay only on the screen, label and LED
    # 0.22.2 (Andre: "fix the colors of the display and the label - the label isn't working"): the game's paintable base again (game_meshes), only the
    # antennas are a mesh of our own (right antenna + globe); screen, label (component + text) and channel number in the paintables list,
    # like the game's SC_WirelessTransmitter (outside it the screen and the label turned white and the label's text vanished)
    r["SC_LongRangeTransmitter"] = prefab("SC_LongRangeTransmitter", bt, 6, [(0, v) for nm, v in faces_(bt) if nm != "+Y"], RADIO_PORTS,
                                          game_meshes=(None, "342554b462ef80d4ba88174d2bce0060"), paint_extras=("screen",),
                                          trims=[((0, 0, 0), (0, 0, 0), (1, 1, 1), "7911e74b4b59f284db6e6cb279dce854", "led"),    # indicator (LED, 0.19.2)
                                                    ((0, 0, 0), (0, 0, 0), (1, 1, 1), "ce137a3da99fda244ac2396b5fcd1c7c", "screen")],   # 0.18.1: screen (flat, no lever)
                                          # 0.18.1 (Andre: "make it flat and straight, you can keep the label"): the front plate is the game's label
                                          label=dict(quad_pos=(0, 0, 0), quad_rot=(0, 0, 0), mesh="a51a79546a18cfb429fe0a216ff80cb6",
                                                        col_off=(0, -0.04, -0.095), col_rot=(305.15, 0, 0), col_esc=(0.23, 0.01, 0.082),
                                                        text_pos=(0, -0.042780206, -0.0918596), text_rot=(305.15, 0, 0), max_len=10),
                                          # 0.18.2 (Andre: "the display plate shows the value it's receiving for the channel"): the channel number,
                                          # like the Wireless Transmitter's (same position/size, right aligned); 3 digits (the plugin writes it)
                                          texts=[dict(name="RendererText3D Channel", pos=RADIO_TEXT, text="  0", align=2, valin=2, mono=1, scale=0.11, paintable=True)],
                                          default_color=3)   # 0.19.2 (Andre): same color as the Shaft Transmitter (DarkGray)
    ICON_PORTS["SC_DriveShaft"] = []
    ICON_PORTS["SC_MiniElectricThruster"] = []   # (copy of the game's: mini_thruster())
    ICON_PORTS["SC_SolarPanelMini"] = []   # 0.19.0 (copy of the game's: mini_solar())
    ICON_PORTS["SC_ShaftTransmitter"] = [TRANSMITTER_PORT]   # (the prefab is copied from the game in transmitter())
    for name, _, _ in TANKS: ICON_PORTS[name] = [TANK_PORT]   # (the tank's prefab is made in tank(); here only for the icon)
    ICON_PORTS["SC_TurretSeat"] = list(mv.TURRET_PORTS)            # (same: turret())
    # wheels: cylinder with its axis in Y (the same axis as the bearing below). The grid steps by 0.5 and the axis has to fall at the
    # center of a quarter (like the bearing), so the box takes an ODD number of quarters: a 1 m wheel takes 1.5.
    # Mesh (visual and convex collider) and friction are set by the plugin (Wheels.cs). Glues only by a 0.5 x 0.5 square at the
    # center of the bottom and top faces (so it doesn't stick to anything near the rim).
    for name, diam, mass in WHEELS:
        box = diam if round(diam / 0.5) % 2 == 1 else diam + 0.5
        q = [(0.25, -0.25, -0.25), (-0.25, -0.25, -0.25), (-0.25, -0.25, 0.25), (0.25, -0.25, 0.25)]
        low = [(x, -0.25, z) for x, _, z in q]; above = [(x, 0.25, z) for x, _, z in q]
        # 0.12.14 (Andre): only the inner side (-Y, flange) glues — the wheel has a right side to fit
        r[name] = prefab(name, (box, 0.5, box), mass, [(0, low)], [], convex=True, only_paintable=True, body_scale=(1, 1, 1))
    return r


# small tanks (0.8.0 form5; 0.9.0 form6; 0.10.1 form8): 1x2x1 m, 500 fuel, copy of the game's tank prefab
# with the SAME color (EPC_SCTank script, gauge, valve, port), but with a MESH OF ITS OWN (models_v2.tank) at scale 1 — before,
# everything was shrunk together and the port plug and the pipe mouth ended up small and off the grid. Fixed layout for both:
# pipe on the bottom band, front +Z, cell x 0..0.25 / y -0.75..-0.5; the game's gauge (with the data port inside) at
# (0, -0.3125, 0.5); the game's valve at (-0.30, -0.625, 0.465); glues by the two bands on the back face (-Z), like the game.
TANKS = [("SC_TankSmallBlue", 0, "SC_BlueTank.prefab"), ("SC_TankSmallGreen", 1, "SC_GreenTank.prefab")]
TANK_MASS, TANK_FUEL, TANK_BOX = 420, 500, (1, 2, 1)
TANK_COLLIDER, TANK_COLLIDER_OFF = (1, 2, 0.94), (0, 0, -0.03)   # 0.11.2: the collider stops at z 0.44 (the valve at z 0.466 stays out)
TANK_GAUGE, TANK_VALVE = (0, -0.3125, 0.5), (-0.12, -0.625, 0.466)   # 0.10.2: valve on the half-moon, on the spout's side
TANK_PORT = ((0.0625, -0.3125, 0.4375), 4, 1)
TANK_PIPE = [(0, -0.75, 0.5), (0.25, -0.75, 0.5), (0.25, -0.5, 0.5), (0, -0.5, 0.5)]
TANK_GLUE = [[(-0.5, 0.75, -0.5), (-0.5, 0.5, -0.5), (0.5, 0.5, -0.5), (0.5, 0.75, -0.5)],
               [(-0.5, -0.5, -0.5), (-0.5, -0.75, -0.5), (0.5, -0.75, -0.5), (0.5, -0.5, -0.5)]]


def tank(name, kind, yaml):
    import re
    docs = yaml.split("\n--- ")
    assert "fileID: %d, guid: %s" % (S_TANK, G) in yaml
    own = REF[name]
    def put_v(txt, field_, v, indic="  "):
        return re.sub(r"(%s%s:\n%s  x: )\S+(\n%s  y: )\S+(\n%s  z: )\S+" % (indic, field_, indic, indic, indic),
                      lambda m: m.group(1) + fmt(v[0]) + m.group(2) + fmt(v[1]) + m.group(3) + fmt(v[2]), txt, count=1)
    def put_area(txt, vs):
        it = iter(vs)
        return re.sub(r"  - x: \S+\n    y: \S+\n    z: \S+", lambda _m: (lambda v: "  - x: %s\n    y: %s\n    z: %s" % (fmt(v[0]), fmt(v[1]), fmt(v[2])))(next(it)), txt)
    names = {}
    for d in docs:
        m = re.match(r"!u!1 &(\d+)", d)
        if m: names[m.group(1)] = re.search(r"m_Name: (.*)", d).group(1)
    new_ = []; glue = iter(TANK_GLUE)
    for d in docs:
        m = re.match(r"!u!114 &(\d+)", d)
        if not m: new_.append(d); continue
        sc = int(re.search(r"m_Script: \{fileID: (-?\d+)", d).group(1))
        go = names.get(re.search(r"m_GameObject: \{fileID: (\d+)", d).group(1), "")
        if sc == S_REND:
            d = put_v(d, "_scale", (1, 1, 1))
            if go == "Renderer": d = re.sub(r"_mesh: \{fileID: \d+, guid: \w+", "_mesh: {fileID: 4300000, guid: %s" % own[0], d, count=1)
            elif go == "Paintable": d = re.sub(r"_mesh: \{fileID: \d+, guid: \w+", "_mesh: {fileID: 4300000, guid: %s" % (own[1] or own[0]), d, count=1)
            elif go == "Tank Meter": d = put_v(d, "_position", TANK_GAUGE)
            elif go == "Actionable Valve": d = put_v(d, "_position", TANK_VALVE)
            elif go.startswith("Port "): d = put_v(d, "_position", TANK_PORT[0])
        elif sc == S_COL_CONVEX and go == "Collider":
            # mesh collider (shrunk) -> 1x2x0.94 box (0.11.2: 6 cm shorter at the front, otherwise the click box of the game's valve
            # ends up INSIDE the block's collider and the player's ray doesn't reach it — Andre: "the valve is still unreachable")
            d = re.sub(r"m_Script: \{fileID: -?\d+", "m_Script: {fileID: %d" % S_COL, d, count=1)
            d = re.sub(r"  _mesh: \{[^}]*\}\n", "", d, count=1)
            d = put_v(put_v(d, "_scale", TANK_COLLIDER), "_offset", TANK_COLLIDER_OFF)
        elif sc == S_AREA:
            if int(re.search(r"_type: (\d+)", d).group(1)) == 1: d = put_area(d, TANK_PIPE)
            else: d = put_area(d, next(glue))
        elif sc == S_TANK:
            d = re.sub(r"_mirroredVersion: \{[^}]*\}", "_mirroredVersion: {fileID: 0}", d, count=1)
            d = put_v(put_v(d, "_bounds", TANK_BOX), "_uiPreviewBounds", TANK_BOX)
            d = re.sub(r"_mass: \S+", "_mass: %d" % TANK_MASS, d, count=1)
            d = re.sub(r"_availableAmount: \S+", "_availableAmount: 500", d, count=1)
            d = re.sub(r"_categories: \S+", "_categories: 516", d, count=1)   # Fuel tab (4) + Mechanics tab (512)
            d = re.sub(r"_fuelType: \S+", "_fuelType: %d" % kind, d, count=1)
            d = re.sub(r"_maxFuel: \S+", "_maxFuel: %d" % TANK_FUEL, d, count=1)
            d = re.sub(r"_iconTexture2D: \{fileID: \d+, guid: \w+", "_iconTexture2D: {fileID: 2800000, guid: %s" % own[2], d, count=1)
            (p, dr, t) = TANK_PORT
            d = re.sub(r"(  - _position:\n      x: )\S+(\n      y: )\S+(\n      z: )\S+(\n    _direction: )\d+",
                       lambda mm: mm.group(1) + fmt(p[0]) + mm.group(2) + fmt(p[1]) + mm.group(3) + fmt(p[2]) + mm.group(4) + str(dr), d, count=1)
        new_.append(d)
    t = "\n--- ".join(new_)
    t = re.sub(r"m_Name: SC_(Green|Blue)Tank\n", "m_Name: %s\n" % name, t, count=1)
    return t if t.endswith("\n") else t + "\n"


# 0.15.0 (Andre: "copy the logic of the data/plasma cable"): the Drive Shaft is a game CABLE (EPC_SCCable) of the Plasma kind — the game
# only knows 4 cable kinds (data/power/nano/plasma) and the drawing of each piece comes from a table per kind. Copies the prefab of the game's
# plasma cable: name, Mechanics tab, quantity, icon of its own, default color = iron (MaterialIron = 200, a color that isn't in the
# palette). All the meshes become ONE marker (Game_DriveShaftCable); the plugin copies the meshes of the game's plasma cable at runtime.
def driveshaft(name, yaml):
    import re, gen_meshes
    mk = gen_meshes.marker_guid("DriveShaftCable")
    t = re.sub(r"guid: (\w+), type: 2\}", lambda m: m.group(0) if m.group(1) == MAT else "guid: %s, type: 2}" % mk, yaml)
    t = re.sub(r"m_Name: SC_PlasmaCable\n", "m_Name: %s\n" % name, t, count=1)
    t = re.sub(r"_availableAmount: \S+", "_availableAmount: 5000", t, count=1)
    t = re.sub(r"_categories: \S+", "_categories: 512", t, count=1)
    t = re.sub(r"_defaultPaintableColor: \S+", "_defaultPaintableColor: 3", t, count=1)   # 0.17.0: DarkGray (200 = iron = the frames' color)
    t = re.sub(r"_mirroredVersion: \{[^}]*\}", "_mirroredVersion: {fileID: 0}", t, count=1)
    t = re.sub(r"_iconTexture2D: \{fileID: \d+, guid: \w+", "_iconTexture2D: {fileID: 2800000, guid: %s" % REF[name][2], t, count=1)
    assert "_type: 3" in t, "the game's plasma cable changed (_type)"
    return t if t.endswith("\n") else t + "\n"


# 0.16.0 (Andre: "the wireless transmitter is a 2x2x2, and I want it exactly like the game's, only change the color, and the output"): the Shaft
# Transmitter is a COPY of the game's SC_WirelessTransmitter (same class EPC_SCWirelessTransmitter: channel lever, number, label,
# indicator — all from the game). Changes: name, Mechanics tab, default color (iron), icon, and the port: data Port X (InOut) -> shaft port
# (plasma, half orange half purple plug). The plugin reads the channel (SCTypeWirelessTransmitter._currentChannel) and links the shaft transmitters
# of the same channel to each other (DriveShaft.cs) — the plasma port doesn't exchange data with the game's data transmitters.
# The game's meshes become markers (GAME_MESHES, relinked at runtime), including the collider's (GameMeshes relinks DOTSColliderConvex).
TRANSMITTER_PORT = ((-0.0625, -0.0625, 0.0625), 4, 8)
# 0.18.0
S_BATTERY = 1312948061   # EPC_SCBattery
M_BATTERY_IND = "14b26e76dee50ad45a1f460c93537766"   # Small Rechargeable Battery Indicator (the game's charge bar)
CELL_PORT = ((0.0625, -0.1875, -0.0625), 5, 2)   # power on the -Z face, at the bottom
RADIO_TEXT = (0.04958624, -0.01527491, -0.04406795)   # channel number (position of the Wireless Transmitter's text)
RADIO_PORTS = [((-0.0625, -0.0625, 0.0625), 4, 3), ((0.0625, -0.0625, 0.0625), 4, 0)]   # signal (in/out) + channel (data)
S_THRUSTER = -1784946918   # EPC_SCThruster
M_PORT_INOUT = "2d697d79043ceb1468a4f403ec2484ff"
S_TRANSMITTER = 1484554865   # EPC_SCWirelessTransmitter
MAT_LED = "94f3adf3f8f47f349929f1b6dff8ce54"   # 0.19.8: material of the game's Wireless Transmitter indicator (CRPUnlit)
LED_MARK = "0.0041"   # 0.19.2: specular strength that marks the renderer of the transmitters' LED (the plugin reads CRPRendererMemory_Color +16)


def transmitter(name, yaml, mirrored=False):
    import re, hashlib
    assert "fileID: %d, guid: %s" % (S_TRANSMITTER, G) in yaml, "the game's SC_WirelessTransmitter expected"
    assert yaml.count(M_PORT_INOUT) == 1, "the game's transmitter changed (Port X)"
    t = yaml.replace("guid: %s, type: 2}" % M_PORT_INOUT, "guid: %s, type: 2}" % port_mesh(8))
    t = re.sub(r"\n  m_Name: Port X\n", "\n  m_Name: Port Shaft InOut\n", t, count=1)
    t = re.sub(r"m_Name: SC_WirelessTransmitter( M)?\n", "m_Name: %s\n" % (name + (" M" if mirrored else "")), t, count=1)
    t = re.sub(r"(  _electricPorts:\n  - _position:\n(?:      [xyz]: \S+\n){3}    _direction: \d+\n    _type: )3\n", r"\g<1>4\n", t, count=1)
    assert "    _type: 4\n  _iconTexture2D" in t, "transmitter port not swapped"
    t = re.sub(r"_availableAmount: \S+", "_availableAmount: 500", t, count=1)
    t = re.sub(r"_categories: \S+", "_categories: 512", t, count=1)
    t = re.sub(r"_defaultPaintableColor: \S+", "_defaultPaintableColor: 3", t, count=1)   # 0.17.0: DarkGray (200 = iron = the frames' color)
    # 0.19.2: LED (indicator) mark so the plugin finds the renderer and paints it green/yellow/red
    t = re.sub(r"(_mesh: \{fileID: 4300000, guid: 7911e74b4b59f284db6e6cb279dce854, type: 2\}\n(?:.*\n){0,6}?  _specular:\n    _strength: )\S+", lambda m: m.group(1) + LED_MARK, t, count=1)
    if mirrored: t = re.sub(r"_mirroredVersion: \{[^}]*\}", "_mirroredVersion: {fileID: 0}", t, count=1)
    else:
        gm = hashlib.md5(("crapmechanics|prefab|" + name + " M").encode()).hexdigest()
        t = re.sub(r"_mirroredVersion: \{fileID: (\d+), guid: \w+", lambda m: "_mirroredVersion: {fileID: %s, guid: %s" % (m.group(1), gm), t, count=1)
    t = re.sub(r"_iconTexture2D: \{fileID: \d+, guid: \w+", "_iconTexture2D: {fileID: 2800000, guid: %s" % REF[name][2], t, count=1)
    # 0.22.2: only the antennas' renderer becomes a mesh of our own (right antenna + gear); paintable base, screen, label and LED = the game's
    assert t.count("guid: 6ae3c4a33e20464449e77878864deb30, type: 2}") == 1, "transmitter body mesh"
    t = t.replace("guid: 6ae3c4a33e20464449e77878864deb30, type: 2}", "guid: %s, type: 2}" % REF[name][0], 1)
    return t if t.endswith("\n") else t + "\n"


def inject_renderer(t, name, tag, goname, mesh, pos=(0, 0, 0), mirrored=False):
    """0.22.1: puts a child GameObject (Transform + EPC_Renderer with the mesh) into a prefab copied from the game, hung on the root Transform
    (the one with m_Father 0). mirrored: scale x -1 (the mesh comes out mirrored, like mirror() does with ours)."""
    import re
    m = re.search(r"--- !u!4 &(\d+)\nTransform:\n(?:  .*\n)*?  m_Children:((?:\n  - \{fileID: \d+\})*| \[\])\n  m_Father: \{fileID: 0\}", t)
    assert m, "root transform not found in " + name
    root_tr = int(m.group(1))
    g, tr_, mb_ = fid(name, tag, "go"), fid(name, tag, "tr"), fid(name, tag, "mb")
    scl = (-1, 1, 1) if mirrored else (1, 1, 1)
    docs = ("--- !u!1 &%d\nGameObject:\n  serializedVersion: 6\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_Component:\n  - component: {fileID: %d}\n  - component: {fileID: %d}\n  m_Layer: 0\n  m_Name: %s\n  m_TagString: Untagged\n  m_Icon: {fileID: 0}\n  m_NavMeshLayer: 0\n  m_StaticEditorFlags: 0\n  m_IsActive: 1\n" % (g, tr_, mb_, goname) +
            "--- !u!4 &%d\nTransform:\n  serializedVersion: 2\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: %d}\n  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n  m_LocalPosition: %s\n  m_LocalScale: %s\n  m_ConstrainProportionsScale: 0\n  m_Children: []\n  m_Father: {fileID: %d}\n  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}\n" % (tr_, g, v3(pos), v3(scl), root_tr) +
            "--- !u!114 &%d\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: %d}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n  m_Script: {fileID: %d, guid: %s, type: 3}\n  m_Name: \n  m_EditorClassIdentifier: \n" % (mb_, g, S_REND, G) +
            "  _position:\n    x: %s\n    y: %s\n    z: %s\n  _rotation:\n    x: 0\n    y: 0\n    z: 0\n  _scale:\n    x: %s\n    y: %s\n    z: %s\n" % (tuple(fmt(a) for a in pos) + tuple(fmt(a) for a in scl)) +
            "  _mesh: {fileID: 4300000, guid: %s, type: 2}\n  _submeshID: 0\n  _material: {fileID: 2100000, guid: %s, type: 2}\n  _layer: 0\n  _color: {r: 0, g: 0, b: 0, a: 0}\n  _specular:\n    _strength: 0\n    _smoothness: 0\n  _hasHeat: 1\n  _spaceshipColor: 0\n" % (mesh, MAT))
    children = m.group(2)
    new_children = ("\n  - {fileID: %d}" % tr_) if children.strip() == "[]" else children + "\n  - {fileID: %d}" % tr_
    t = t[:m.start(2)] + new_children + t[m.end(2):]
    return (t if t.endswith("\n") else t + "\n") + docs, mb_


# 0.18.0 (Andre: "a small version of some rocket, the size of a frame quarter or a bit taller"; he chose the small electric
# thruster): a COPY of the game's SC_SmallElectricThruster at scale 0.5 (1 x 2 x 1 -> 0.5 x 1 x 0.5): everything that is position/scale
# x 0.5 (renderers, collider, glue areas, heat, particles, blast), force and consumption / 4, mass 40. The 2 ports go to the cells of the
# bottom face (input x +-0.0625, power x +-0.1875); the reverse button goes along with the input.
K_MINI = 0.5
# 0.19.0: Unity Euler rotations (R = Ry.Rx.Rz) to turn the reverse button together with the input port
def _rx(a): c, s_ = math.cos(math.radians(a)), math.sin(math.radians(a)); return [[1, 0, 0], [0, c, -s_], [0, s_, c]]
def _ry(a): c, s_ = math.cos(math.radians(a)), math.sin(math.radians(a)); return [[c, 0, s_], [0, 1, 0], [-s_, 0, c]]
def _rz(a): c, s_ = math.cos(math.radians(a)), math.sin(math.radians(a)); return [[c, -s_, 0], [s_, c, 0], [0, 0, 1]]
def _mat_mul(a, b): return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
def _rot_x(a): return _rx(a)
def _mat_euler(r): return _mat_mul(_ry(r[1]), _mat_mul(_rx(r[0]), _rz(r[2])))
def _euler_of(m):
    x = math.degrees(math.asin(max(-1.0, min(1.0, -m[1][2]))))
    if abs(m[1][2]) < 0.9999: y = math.degrees(math.atan2(m[0][2], m[2][2])); z = math.degrees(math.atan2(m[1][0], m[1][1]))
    else: y = math.degrees(math.atan2(-m[2][0], m[0][0])); z = 0.0
    return tuple(round(v, 4) + 0.0 for v in (x, y, z))
def mini_thruster(name, yaml, mirrored=False):
    import re, hashlib
    assert "fileID: %d, guid: %s" % (S_THRUSTER, G) in yaml, "the game's SC_SmallElectricThruster expected"
    k = K_MINI
    def v3re(field_, indic="  "):
        return re.compile(r"(%s%s:\n%s  x: )(\S+)(\n%s  y: )(\S+)(\n%s  z: )(\S+)" % (indic, field_, indic, indic, indic))
    def scl3(txt, field_, f, indic="  ", n=1):
        return v3re(field_, indic).sub(lambda m: m.group(1) + fmt(float(m.group(2)) * f[0]) + m.group(3) + fmt(float(m.group(4)) * f[1]) + m.group(5) + fmt(float(m.group(6)) * f[2]), txt, count=n)
    def put3(txt, field_, v, indic="  "):
        return v3re(field_, indic).sub(lambda m: m.group(1) + fmt(v[0]) + m.group(3) + fmt(v[1]) + m.group(5) + fmt(v[2]), txt, count=1)
    def read3(txt, field_, indic="  "):
        m = v3re(field_, indic).search(txt); return (float(m.group(2)), float(m.group(4)), float(m.group(6)))
    def sclf(txt, field_, f):
        return re.sub(r"(\n  %s: )(\S+)" % field_, lambda m: m.group(1) + fmt(float(m.group(2)) * f), txt, count=1)
    K3 = (k, k, k)
    docs = yaml.split("\n--- ")
    names = {}
    for d in docs:
        m = re.match(r"!u!1 &(\d+)", d)
        if m: names[m.group(1)] = re.search(r"m_Name: (.*)", d).group(1)
    # original ports (from the prefab) -> new cells
    root = [d for d in docs if "fileID: %d, guid: %s" % (S_THRUSTER, G) in d][0]
    pos_orig = [(float(a), float(b), float(c)) for a, b, c in re.findall(r"  - _position:\n      x: (\S+)\n      y: (\S+)\n      z: (\S+)\n    _direction: \d+\n    _type: [02]", root)]
    assert len(pos_orig) == 2, pos_orig
    sx = 1.0 if pos_orig[0][0] > 0 else -1.0; sz = 1.0 if pos_orig[0][2] > 0 else -1.0
    # 0.19.0 (Andre: "take its ports off the bottom of the base and put them in the little side corner of the base — not on opposite sides, on
    # any adjacent side"): both in the bottom corner cell, each one on a side face (input on the Z face, power on the X face) = "L"
    # 0.19.1 (Andre: "it keeps flicking between port and not port, very hard to connect"): both in the SAME cell fought over the hover ->
    # each one in its own cell: input in the corner cell (Z face), power in the neighboring cell along Z (X face)
    new_in = (sx * 0.1875, -0.4375, sz * 0.1875); new_pw = (sx * 0.1875, -0.4375, sz * 0.0625)
    dir_in = 4 if sz > 0 else 5; dir_pw = 0 if sx > 0 else 1
    Rf = _rot_x(-90.0 if sz > 0 else 90.0)   # takes the -Y (port at the bottom, original) to the input's face
    def button_offset(p):
        o = [(p[i] - pos_orig[0][i]) for i in range(3)]   # 0.19.0: real size (the port doesn't shrink; at x0.5 the button went into the face)
        o = [sum(Rf[i][j] * o[j] for j in range(3)) for i in range(3)]
        o[2] += sz * 0.006   # 0.19.3 (Andre: "you can't see the switch properly, pull it out a bit"): 6 mm out of the face
        return tuple(new_in[i] + o[i] for i in range(3))
    def button_rot(r): return _euler_of(_mat_mul(Rf, _mat_euler(r)))
    new_ = []
    for d in docs:
        m = re.match(r"!u!114 &(\d+)", d)
        if re.match(r"!u!4 &", d):
            go = names.get(re.search(r"m_GameObject: \{fileID: (\d+)", d).group(1), "")
            mp = re.search(r"m_LocalPosition: \{x: (\S+), y: (\S+), z: (\S+)\}", d)
            if mp and go != name and not go.startswith("SC_SmallElectricThruster"):
                p0 = tuple(float(x) for x in mp.groups())
                if go == "Port Input": p1 = new_in
                elif go == "Port Power": p1 = new_pw
                elif go.startswith("Port Input Reverse"): p1 = button_offset(p0)
                else: p1 = tuple(x * k for x in p0)
                d = d.replace(mp.group(0), "m_LocalPosition: {x: %s, y: %s, z: %s}" % tuple(fmt(x) for x in p1), 1)
            new_.append(d); continue
        if not m: new_.append(d); continue
        sc = int(re.search(r"m_Script: \{fileID: (-?\d+)", d).group(1))
        go = names.get(re.search(r"m_GameObject: \{fileID: (\d+)", d).group(1), "")
        if sc == S_THRUSTER:
            d = scl3(scl3(d, "_bounds", K3), "_uiPreviewBounds", K3)
            d = scl3(scl3(d, "_soundObstacleMin", K3), "_soundObstacleMax", K3)
            d = re.sub(r"_mass: \S+", "_mass: 40", d, count=1)
            d = scl3(d, "_maxForce", (0.25, 0.25, 0.25))
            d = sclf(d, "_powerConsumptionPerSec", 0.25)
            d = re.sub(r"_categories: \S+", "_categories: 642", d, count=1)   # thrusters (128) + electronics (2) + Mechanics (512)
            d = re.sub(r"_availableAmount: \S+", "_availableAmount: 500", d, count=1)
            d = re.sub(r"_particleSystemMinVelocity: (\S+)", lambda mm: "_particleSystemMinVelocity: " + fmt(float(mm.group(1)) * k), d)
            d = re.sub(r"_particleSystemMaxVelocity: (\S+)", lambda mm: "_particleSystemMaxVelocity: " + fmt(float(mm.group(1)) * k), d)
            it = iter([(new_in, dir_in), (new_pw, dir_pw)])
            d = re.sub(r"(  - _position:\n      x: )\S+(\n      y: )\S+(\n      z: )\S+(\n    _direction: )\d+(\n    _type: [02])",
                       lambda mm: (lambda v, dd: mm.group(1) + fmt(v[0]) + mm.group(2) + fmt(v[1]) + mm.group(3) + fmt(v[2]) + mm.group(4) + str(dd) + mm.group(5))(*next(it)), d)
            d = re.sub(r"_iconTexture2D: \{fileID: \d+, guid: \w+", "_iconTexture2D: {fileID: 2800000, guid: %s" % REF["SC_MiniElectricThruster"][2], d, count=1)
            if mirrored: d = re.sub(r"_mirroredVersion: \{[^}]*\}", "_mirroredVersion: {fileID: 0}", d, count=1)
            else:
                gm = hashlib.md5(("crapmechanics|prefab|" + name + " M").encode()).hexdigest()
                d = re.sub(r"_mirroredVersion: \{fileID: (\d+), guid: \w+", lambda mm: "_mirroredVersion: {fileID: %s, guid: %s" % (mm.group(1), gm), d, count=1)
        elif sc == 884829252:   # EPC_ThrusterBlow (blast)
            for c_ in ("_blowStartPosMin", "_blowStartPosMax", "_blowEndPosMin", "_blowEndPosMax"): d = scl3(d, c_, K3)
            d = sclf(d, "_blowStrength", 0.25)
        elif sc == S_AREA:
            d = re.sub(r"(\n  - x: )(\S+)(\n    y: )(\S+)(\n    z: )(\S+)", lambda mm: mm.group(1) + fmt(float(mm.group(2)) * k) + mm.group(3) + fmt(float(mm.group(4)) * k) + mm.group(5) + fmt(float(mm.group(6)) * k), d)
        elif sc == S_REND:
            p0 = read3(d, "_position")
            # 0.19.2 (Andre: "there's some z-fighting, the port's mouth has to stick out"): the plug comes out 2 mm from the body's face
            if go == "Port Input": d = put3(put3(d, "_position", (new_in[0], new_in[1], new_in[2] + sz * 0.002)), "_rotation", ROT_DIR[dir_in])
            elif go == "Port Power": d = put3(put3(d, "_position", (new_pw[0] + sx * 0.002, new_pw[1], new_pw[2])), "_rotation", ROT_DIR[dir_pw])
            elif go.startswith("Port Input Reverse"): d = put3(put3(d, "_position", button_offset(p0)), "_rotation", button_rot(read3(d, "_rotation")))
            else: d = scl3(scl3(d, "_position", K3), "_scale", K3)
        elif sc == 762861932:   # reverse button: on/off poses next to the input
            for c_ in ("_buttonTransformOnPos", "_buttonTransformOffPos"): d = put3(d, c_, button_offset(read3(d, c_)))
            for c_ in ("_buttonTransformOnRot", "_buttonTransformOffRot"): d = put3(d, c_, button_rot(read3(d, c_)))
        elif sc == S_COL:   # the button's click box (0.19.0: the game's size, like the port)
            pass
        elif sc == S_COL_CONVEX:
            # 0.18.1 (Andre: "I place the mini thruster, run the simulation and everything blows up"): the game's convex collider doesn't respect _scale
            # (it stayed the size of the normal thruster, 1 x 2 x 1, inside the neighboring blocks) -> box the size of the mini (mesh: y -0.5..0.45)
            d = re.sub(r"m_Script: \{fileID: -?\d+", "m_Script: {fileID: %d" % S_COL, d, count=1)
            d = re.sub(r"  _mesh: \{[^}]*\}\n", "", d, count=1)
            d = put3(put3(d, "_offset", (0, -0.03, 0)), "_scale", (0.46, 0.92, 0.46))
            d = re.sub(r"_bevelRadius: \S+", "_bevelRadius: 0.02", d, count=1)
        elif sc == 23723654:   # heat
            d = scl3(scl3(d, "_position", K3), "_scale", K3)
        elif sc == -1515158256:   # EPC_Transform (particles, sound)
            d = scl3(d, "_position", K3)
        elif sc == -530793849:   # EPC_ParticleSystem
            d = sclf(sclf(d, "_positionRangeMin", k), "_positionRangeMax", k) if False else d
            d = re.sub(r"(\n    _positionRange(?:Min|Max): )(\S+)", lambda mm: mm.group(1) + fmt(float(mm.group(2)) * k), d)
            d = sclf(sclf(d, "_scaleMin", k), "_scaleMed", k)
            d = sclf(d, "_velocityLinearChange", k)
        elif sc == 1786898767:   # sound
            d = sclf(d, "_size", k)
        new_.append(d)
    t = "\n--- ".join(new_)
    t = re.sub(r"m_Name: SC_SmallElectricThruster( M)?\n", "m_Name: %s\n" % (name + (" M" if mirrored else "")), t, count=1)
    return t if t.endswith("\n") else t + "\n"


# 0.19.0 (Andre: "it'd be nice to have a mini solar panel too"; "width and length of a frame quarter, thickness of 1x"): a COPY of the
# game's SC_SolarPanel (1 x 0.125 x 2) at 0.5 x 0.125 x 0.5 (x 0.5, z 0.25): meshes/glue area/collider scaled, generation and mass
# by area (1/8), the 2 power ports at the ends (one on each Z face, in the corner cell, like the big one).
S_SOLAR = -1058504809   # EPC_SCSolarPanel
def mini_solar(name, yaml):
    import re, hashlib
    assert "fileID: %d, guid: %s" % (S_SOLAR, G) in yaml, "the game's SC_SolarPanel expected"
    fx, fz = 0.5, 0.25
    def v3(field_, indic="  "):
        return re.compile(r"(%s%s:\n%s  x: )(\S+)(\n%s  y: )(\S+)(\n%s  z: )(\S+)" % (indic, field_, indic, indic, indic))
    def put(txt, field_, v, indic="  "):
        return v3(field_, indic).sub(lambda m: m.group(1) + fmt(v[0]) + m.group(3) + fmt(v[1]) + m.group(5) + fmt(v[2]), txt, count=1)
    docs = yaml.split("\n--- "); new_ = []
    for d in docs:
        m = re.match(r"!u!114 &(\d+)", d)
        if not m: new_.append(d); continue
        sc = int(re.search(r"m_Script: \{fileID: (-?\d+)", d).group(1))
        if sc == S_SOLAR:
            d = put(put(d, "_bounds", (0.5, 0.125, 0.5)), "_uiPreviewBounds", (0.5, 0.125, 0.5))
            d = re.sub(r"_mass: \S+", "_mass: 6", d, count=1)
            d = re.sub(r"_maxPowerGenerationPerSec: (\S+)", lambda mm: "_maxPowerGenerationPerSec: " + fmt(float(mm.group(1)) / 8.0), d, count=1)
            d = re.sub(r"_categories: \S+", "_categories: 514", d, count=1)   # electronics (2) + Mechanics (512)
            d = re.sub(r"_availableAmount: \S+", "_availableAmount: 500", d, count=1)
            d = re.sub(r"(  - _position:\n      x: )(\S+)(\n      y: )(\S+)(\n      z: )(\S+)",
                       lambda mm: mm.group(1) + fmt(-0.1875) + mm.group(3) + mm.group(4) + mm.group(5) + fmt(0.1875 if float(mm.group(6)) > 0 else -0.1875), d)
            d = re.sub(r"_iconTexture2D: \{fileID: \d+, guid: \w+", "_iconTexture2D: {fileID: 2800000, guid: %s" % REF[name][2], d, count=1)
            gm = hashlib.md5(("crapmechanics|prefab|" + name + " M").encode()).hexdigest()
            d = re.sub(r"_mirroredVersion: \{fileID: (\d+), guid: \w+", lambda mm: "_mirroredVersion: {fileID: %s, guid: %s" % (mm.group(1), gm), d, count=1)
        elif sc == S_AREA:
            d = re.sub(r"(\n  - x: )(\S+)(\n    y: )(\S+)(\n    z: )(\S+)", lambda mm: mm.group(1) + fmt(float(mm.group(2)) * fx) + mm.group(3) + mm.group(4) + mm.group(5) + fmt(float(mm.group(6)) * fz), d)
        elif sc == S_REND:
            pm = re.search(r"_mesh: \{fileID: \d+, guid: (\w+)", d); g = pm.group(1) if pm else ""
            if g == "524c7b0ae261a9244b97246a5799c9b4":   # power port: to the corner cell
                p0 = v3("_position").search(d); z0 = float(p0.group(6))
                d = put(d, "_position", (-0.1875, 0, 0.1875 if z0 > 0 else -0.1875))
            elif g in ("fe00b5509a5a3094493c4f4ab77c0e9d", "64708f33bff0c2745bf70df3270e39be"):
                # 0.22.5: body (cells) and paintable (tray/rim/corner brackets) = model of our own (models_v2.mini_solar), not stretched
                d = d.replace("guid: %s, type: 2}" % g, "guid: %s, type: 2}" % (REF[name][0] if g == "fe00b5509a5a3094493c4f4ab77c0e9d" else REF[name][1]), 1)
            else:
                d = put(d, "_scale", (fx, 1, fz))
        elif sc == S_COL:
            d = put(d, "_scale", (0.46, 0.0825, 0.46))
        new_.append(d)
    t = "\n--- ".join(new_)
    t = re.sub(r"m_Name: SC_SolarPanel\n", "m_Name: %s\n" % name, t, count=1)
    # 0.22.2 (Andre: "the 'little iron bit' there sort of has to 'cover' the port"): on the game's panel the port sits under the corner bracket; scaled
    # (x 0.5 / z 0.25) the bracket shrank and moved off the port (which stays in its grid cell). A paintable box of our own around each port.
    return t if t.endswith("\n") else t + "\n"


# 0.11.0: steering wheel and pedal were clones of the game's joysticks. 0.11.2: both are blocks of their own (see blocks()); only the ports
# table stayed. Beacon: power port on the -X face (the only cell of the bottom row, +Z side) of the 0.25 block.
WHEEL_PORTS = [((0.0625, -0.1875, 0.1875), 4, 1)]   # 0.19.3 (Andre): ONE output, on the back face (+Z) of the small box, in the end cell
PEDAL_PORTS = [((-0.1875, -0.1875, 0.1875), 4, 1)]   # 0.12.1: a single output (-1 left .. +1 right), back wall; 0.19.2 (Andre): in the corner cell
BEACON_PORT = ((-0.0625, -0.0625, 0.0625), 1, 2)
# 0.20.0 had a data input here (turns the beacon on/off); undone the same night (Andre remembered the game's Power Blocker).
# 0.19.14: the repo still had the extra port — back to power only (like the bundle that has been in the game since v65).
BEACON_PORTS = [BEACON_PORT]
S_LEVER, S_CAPSULE, S_DOUBLE = -1362781425, 1452482761, -1393867716   # EPC_Actionable_Lever (joystick), DOTSColliderCapsule, EPC_Actionable_DoubleLever (common lever)
S_KNOB = 1996836204   # 0.17.0: EPC_Actionable_Knob
S_TOGGLE = 762861932   # 0.23.0: EPC_Actionable_Toggle (2-position switch: the Datameter's Input Switch, the mini thruster's reverse button)
S_SEAT, S_COL_SPHERE, S_BUTTON = 738011195, -581501808, 98624692   # + EPC_Actionable_Button   # EPC_SCSeat (root of the game's seat), DOTSColliderSphere (_center, _radius)


# ------------------------------------------------------------------ turret seat (0.12.0)
# A block of its own (models_v2.turret: ring, arc, plates, cap, triggers) with the game's SC_Seat INSIDE: the seat's GameObjects
# (Piston, Actionable Seat Upper + Upper Paintable, Actionable Lever) are copied from the game's prefab, shifted by TURRET_SEAT,
# and the root's script becomes EPC_SCSeat (the EPC's same fields + _upperTransform/_pistonTransform/_actionableLever) — sitting, binding,
# height, all from the game. The "Lower" plate and the game's box colliders go away (cap + sphere collider in their place). Glues by the whole
# base and by the 4 plates. 2 outputs (Mouse 1/2) on the bottom face = 2 levers 0..1 (triggers) that show up in the bind menu.
TURRET_MASS = 130


def turret(name, yaml):
    import re, models_v2 as mv
    assert "fileID: %d, guid: %s" % (S_SEAT, G) in yaml, "the game's SC_Seat.prefab expected"
    D = mv.TURRET_SEAT
    docs, names = _docs(yaml)
    # ids of the game's seat: root, root transform, and the MBs the root references (upper/piston/lever/paintable)
    root_mb = next(d for d in docs if "m_Script: {fileID: %d, guid" % S_SEAT in d)
    ref = lambda field_: int(re.search(r"%s: \{fileID: (\d+)" % field_, root_mb).group(1))
    upper_mb, piston_mb, lever_mb = ref("_upperTransform"), ref("_pistonTransform"), ref("_actionableLever")
    game_paint = [int(x) for x in re.findall(r"  - \{fileID: (\d+)\}", re.search(r"_paintableRenderers:\n((?:  - \{fileID: \d+\}\n)+)", root_mb).group(1))]
    root_go = int(re.search(r"m_GameObject: \{fileID: (\d+)", root_mb).group(1))
    root_tr = None
    for d in docs:
        m = re.match(r"!u!4 &(\d+)", d)
        if m and "m_GameObject: {fileID: %d}" % root_go in d: root_tr = int(m.group(1))
    remove_go = {g for g, n in names.items() if n in ("Lower", "JointsArea") or n.startswith("Collider")} | {str(root_go)}
    # --- 1) the block of our own
    hx, hy, hz = [b / 2 for b in mv.TURRET_BOUNDS]; px, py, pz = mv.TURRET_PIVOT; a, b = mv.TURRET_PLATE_SIDE, mv.TURRET_PLATE_TOP
    # 0.12.6: block 2.5 x 2.5 x 1.5 = 5 x 5 x 3 frame quarters. Glues by the base plate (1.5 x 1.5 = 3 x 3 quarters in the middle of the bottom) and
    # by the 4 plates, each one = the middle quarter of its face (sides x = +-1.25, top y = 1.25, back z = -0.75)
    hb = mv.TURRET_BASE_SIDE / 2
    areas_ = [(0, [(hb, -hy, -hz), (-hb, -hy, -hz), (-hb, -hy, hz), (hb, -hy, hz)]),
             (0, [(-hx, py - a, pz - b), (-hx, py + a, pz - b), (-hx, py + a, pz + b), (-hx, py - a, pz + b)]),
             (0, [(hx, py - a, pz - b), (hx, py + a, pz - b), (hx, py + a, pz + b), (hx, py - a, pz + b)]),
             (0, [(px + a, hy, pz - b), (px - a, hy, pz - b), (px - a, hy, pz + b), (px + a, hy, pz + b)]),
             (0, [(px - a, py - a, -hz), (px + a, py - a, -hz), (px + a, py + a, -hz), (px - a, py + a, -hz)])]
    extra = "  _upperTransform: {fileID: %d}\n  _pistonTransform: {fileID: %d}\n  _actionableLever: {fileID: %d}\n" % (upper_mb, piston_mb, lever_mb)
    yb0, yb1 = mv.TURRET_BASE_Y
    txt = prefab(name, mv.TURRET_BOUNDS, TURRET_MASS, areas_, mv.TURRET_PORTS, script=S_SEAT, root_extra=extra,
                 box_collider=[((0, (yb0 + yb1) / 2, 0), (mv.TURRET_BASE_SIDE, yb1 - yb0, mv.TURRET_BASE_SIDE))],
                 extra_paintables=[x for x in game_paint if names.get(_go_of(docs, x), "") in ("Piston", "Upper Paintable")], paint_extras=("MovingPaint",),
                 levers={sfx: ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, pad, (0, 0, 0.007))) for sfx, pad in zip(mv.TURRET_KNOB_SUF, mv.TURRET_KNOB_DEFAULT)})   # 0.25.0: limit knobs
    my_tr = fid(name, "tr")
    # --- 2) the docs of the game's seat (except root, Lower, JointsArea and the root's colliders), shifted and hung on my root
    copy_ = []; children_tr = []
    for d in docs:
        m = re.match(r"!u!(\d+) &(\d+)", d)
        if not m: continue
        kind, fid_ = m.group(1), m.group(2)
        go = fid_ if kind == "1" else re.search(r"m_GameObject: \{fileID: (\d+)", d).group(1)
        if go in remove_go: continue
        if kind == "4" and ("m_Father: {fileID: %d}" % root_tr) in d:
            d = d.replace("m_Father: {fileID: %d}" % root_tr, "m_Father: {fileID: %d}" % my_tr); children_tr.append(int(fid_))
        if kind == "114":
            sc = int(re.search(r"m_Script: \{fileID: (-?\d+)", d).group(1))
            n = names.get(go, "")
            if sc == S_REND and n in ("Piston", "Actionable Seat Upper", "Actionable Lever"):
                d = _shift_v(d, "_position", D)
            if sc == S_REND and n == "Actionable Lever":
                d = _put_v(d, "_scale", (0, 0, 0))   # 0.12.2: height lever hidden (seat fixed in the middle)
            if sc == S_LEVER:
                for field_ in ("_positionVerMin", "_positionVerMax"): d = _shift_v(d, field_, D)
                d = _put_v(_put_v(d, "_rotationVerMin", (0, 0, 0)), "_rotationVerMax", (0, 0, 0))
            if sc == S_CAPSULE and n == "Actionable Lever":
                d = _put_v(_put_v(d, "_vertexA", (0, 0, 0)), "_vertexB", (0, 0.0001, 0)); d = re.sub(r"_radius: \S+", "_radius: 0.0001", d, count=1)
            if sc == S_COL and n == "Actionable Seat Upper" and "_scale:\n    x: 0.62" in d:
                d = _put_v(_put_v(d, "_scale", (0.01, 0.01, 0.01)), "_offset", (0, 0.07, -0.02))   # 0.12.2: no backrest collider (it stays still while the drawing turns and blocked the clicks)
        copy_.append(d)
    # hangs the seat's transforms on my root
    txt = re.sub(r"(--- !u!4 &%d\nTransform:.*?m_Children:)((?:\n  - \{fileID: \d+\})*)" % my_tr,
                 lambda m: m.group(1) + m.group(2) + "".join("\n  - {fileID: %d}" % f for f in children_tr), txt, count=1, flags=re.S)
    txt = txt.rstrip("\n") + "\n--- " + "\n--- ".join(copy_)
    return txt if txt.endswith("\n") else txt + "\n"


def _go_of(docs, mb):
    import re
    for d in docs:
        if re.match(r"!u!114 &%d\b" % mb, d): return re.search(r"m_GameObject: \{fileID: (\d+)", d).group(1)
    return ""


def _shift_v(txt, field_, dv, indic="  "):
    import re
    def f(m):
        x, y, z = float(m.group(2)), float(m.group(4)), float(m.group(6))
        return m.group(1) + fmt(x + dv[0]) + m.group(3) + fmt(y + dv[1]) + m.group(5) + fmt(z + dv[2])
    return re.sub(r"(%s%s:\n%s  x: )(\S+)(\n%s  y: )(\S+)(\n%s  z: )(\S+)" % (indic, field_, indic, indic, indic), f, txt, count=1)


def _put_v(txt, field_, v, indic="  "):
    import re
    return re.sub(r"(%s%s:\n%s  x: )\S+(\n%s  y: )\S+(\n%s  z: )\S+" % (indic, field_, indic, indic, indic),
                  lambda m: m.group(1) + fmt(v[0]) + m.group(2) + fmt(v[1]) + m.group(3) + fmt(v[2]), txt, count=1)


def _docs(yaml):
    import re
    docs = yaml.split("\n--- ")
    names = {}
    for d in docs:
        m = re.match(r"!u!1 &(\d+)", d)
        if m: names[m.group(1)] = re.search(r"m_Name: (.*)", d).group(1)
    return docs, names


def _root(d, name, categories, icon):
    import re
    d = re.sub(r"_mirroredVersion: \{[^}]*\}", "_mirroredVersion: {fileID: 0}", d, count=1)
    d = re.sub(r"_availableAmount: \S+", "_availableAmount: 500", d, count=1)
    d = re.sub(r"_categories: \S+", "_categories: %d" % categories, d, count=1)
    d = re.sub(r"_iconTexture2D: \{fileID: \d+, guid: \w+", "_iconTexture2D: {fileID: 2800000, guid: %s" % icon, d, count=1)
    return d


def _mesh(d, guid):
    import re
    d = re.sub(r"_mesh: \{fileID: \d+, guid: \w+", "_mesh: {fileID: 4300000, guid: %s" % guid, d, count=1)
    return re.sub(r"_material: \{fileID: \d+, guid: \w+", "_material: {fileID: 2100000, guid: %s" % MAT, d, count=1)


# ------------------------------------------------------------------ 0.12.14: mirrored version (V in the garage)
# Like the game does (e.g. SC_LargeElectricThruster M): a prefab "<name> M" with everything mirrored in X in the component's DATA —
# renderers with negative X scale (the mesh really comes out mirrored), positions x -> -x, rotations (x, y, z) -> (x, -y, -z),
# ports x -> -x and direction +X <-> -X, glue areas x -> -x. The Transforms stay (the game reads the components' data).
# The base -> M link is made at runtime by the plugin (Mirrors.cs) with the instances registered in Components Framework.
# The ones that are already symmetric (or depend on game parts) point to themselves: pedal, steering wheel, turret seat, tanks.
TO_MIRROR = ["SC_Piston", "SC_MiniPiston", "SC_MiniBearing", "SC_Bearing", "SC_MotorSmall", "SC_MotorMedium", "SC_MotorLarge",   # 0.20.0: + single piston, minis
            "SC_MotorFuelSmall", "SC_MotorFuelMedium", "SC_MotorFuelLarge", "SC_Recoupler", "SC_Differential", "SC_ShaftReverser",
            "SC_SingleCellBattery", "SC_LongRangeTransmitter", "SC_SolarPanelMini", "SC_Beacon"] + [n for n, _, _ in WHEELS]   # 0.19.0; 0.19.2: beacon + wheels; 0.27.0: the wheels were missing from the list (the game still had their 0.19 "... M" prefabs)
S_COL_CONVX_ = -1838397108
NO_MIRROR = ["ce137a3da99fda244ac2396b5fcd1c7c", M_BATTERY_IND]   # meshes the M version keeps (Wireless Transmitter screen, like the game's M; 0.19.14: battery charge bar — the game scales x from 0 to 1 from the position, mirrored it ended up outside the block, like the game's M doesn't mirror it)


def _neg(v):
    v = v.strip()
    if v in ("0", "-0"): return "0"
    return v[1:] if v.startswith("-") else "-" + v


def mirror(txt, name):
    import re
    docs = re.split(r"\n(?=--- !u!)", txt)
    out = []
    for d in docs:
        m = re.search(r"m_Script: \{fileID: (-?\d+)", d)
        sc = int(m.group(1)) if m else None
        if d.startswith("--- !u!1 ") and re.search(r"\n  m_Name: %s\n" % re.escape(name), d):
            d = d.replace("\n  m_Name: %s\n" % name, "\n  m_Name: %s M\n" % name, 1)
        if sc == S_REND and any(g in d for g in NO_MIRROR):
            pass   # 0.19.0: Wireless Transmitter screen: the game doesn't mirror it either (the channel number stays the same)
        elif sc == S_REND:
            d = re.sub(r"(\n  _position:\n    x: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)), d, count=1)
            d = re.sub(r"(\n  _rotation:\n    x: [^\n]+\n    y: )([^\n]+)(\n    z: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)) + k.group(3) + _neg(k.group(4)), d, count=1)
            d = re.sub(r"(\n  _scale:\n    x: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)), d, count=1)
        elif sc == S_TOGGLE:
            # 0.25.1 (Andre: "the switch of the inverted bearing (M mode) is on top of the data port"): the game WRITES the rod's pose
            # (_buttonTransformOn/OffPos/Rot) into the switch's renderer, in block coordinates — the _position of the mirrored renderer was
            # discarded and the rod went back to the normal version's side.
            for field_ in ("_buttonTransformOnPos", "_buttonTransformOffPos"):
                d = re.sub(r"(\n  %s:\n    x: )([^\n]+)" % field_, lambda k: k.group(1) + _neg(k.group(2)), d, count=1)
            for field_ in ("_buttonTransformOnRot", "_buttonTransformOffRot"):
                d = re.sub(r"(\n  %s:\n    x: [^\n]+\n    y: )([^\n]+)(\n    z: )([^\n]+)" % field_, lambda k: k.group(1) + _neg(k.group(2)) + k.group(3) + _neg(k.group(4)), d, count=1)
        elif sc in (S_COL, S_COL_CONVX_):
            d = re.sub(r"(\n  _offset:\n    x: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)), d, count=1)
            d = re.sub(r"(\n  _rotation:\n    x: [^\n]+\n    y: )([^\n]+)(\n    z: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)) + k.group(3) + _neg(k.group(4)), d, count=1)
        elif sc == S_AREA:
            d = re.sub(r"(\n  - x: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)), d)
        elif sc in (S_EPC, S_PIPE) or (sc is not None and "\n  _electricPorts:" in d):   # 0.19.0: + battery/panel root (other scripts with ports)
            d = re.sub(r"(\n  - _position:\n      x: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)), d)
            d = re.sub(r"(\n    _direction: )([01])\n", lambda k: k.group(1) + ("1" if k.group(2) == "0" else "0") + "\n", d)
            mn = re.search(r"\n  _soundObstacleMin:\n    x: ([^\n]+)", d); mx = re.search(r"\n  _soundObstacleMax:\n    x: ([^\n]+)", d)
            if mn and mx:
                a, b = mn.group(1), mx.group(1)
                d = d.replace("\n  _soundObstacleMin:\n    x: " + a, "\n  _soundObstacleMin:\n    x: " + _neg(b), 1)
                d = d.replace("\n  _soundObstacleMax:\n    x: " + b, "\n  _soundObstacleMax:\n    x: " + _neg(a), 1)
        elif sc is not None and sc not in (S_REND,):
            pass
        out.append(d)
    t = "\n".join(out)
    assert "m_Name: %s M\n" % name in t, name
    return t


if __name__ == "__main__":
    folder = sys.argv[1] if len(sys.argv) > 1 else "Content/Prefabs"
    os.makedirs(folder, exist_ok=True)
    # 0.9.0: 1st pass only to know the ports (icons), then meshes+icons (gen_meshes) and the real prefabs
    game_dir = sys.argv[2] if len(sys.argv) > 2 else None   # the Modkit workspace's Assets/Game/GameObject folder (for the tanks)
    content = os.path.dirname(os.path.abspath(folder.rstrip("/\\")))
    if game_dir:
        import models_v2; models_v2.GAME_MESH_DIR = os.path.join(os.path.dirname(game_dir.rstrip("/\\")), "Mesh")
    blocks()
    import gen_meshes
    REF.update(gen_meshes.generate(content, ICON_PORTS, os.path.join(os.path.dirname(game_dir.rstrip("/\\")), "Mesh") if game_dir else None))
    all_ = blocks()
    if game_dir:
        for name, kind, fpath in TANKS: all_[name] = tank(name, kind, open(os.path.join(game_dir, fpath), encoding="utf-8").read())
        all_["SC_TurretSeat"] = turret("SC_TurretSeat", open(os.path.join(game_dir, "SC_Seat.prefab"), encoding="utf-8").read())
        all_["SC_DriveShaft"] = driveshaft("SC_DriveShaft", open(os.path.join(game_dir, "SC_PlasmaCable.prefab"), encoding="utf-8").read())
        all_["SC_ShaftTransmitter"] = transmitter("SC_ShaftTransmitter", open(os.path.join(game_dir, "SC_WirelessTransmitter.prefab"), encoding="utf-8").read())
        all_["SC_MiniElectricThruster"] = mini_thruster("SC_MiniElectricThruster", open(os.path.join(game_dir, "SC_SmallElectricThruster.prefab"), encoding="utf-8").read())
        all_["SC_MiniElectricThruster M"] = mini_thruster("SC_MiniElectricThruster", open(os.path.join(game_dir, "SC_SmallElectricThruster M.prefab"), encoding="utf-8").read(), mirrored=True)
        all_["SC_SolarPanelMini"] = mini_solar("SC_SolarPanelMini", open(os.path.join(game_dir, "SC_SolarPanel.prefab"), encoding="utf-8").read())   # 0.19.0
        all_["SC_ShaftTransmitter M"] = transmitter("SC_ShaftTransmitter", open(os.path.join(game_dir, "SC_WirelessTransmitter M.prefab"), encoding="utf-8").read(), mirrored=True)
    import hashlib
    for name in TO_MIRROR:
        if name in all_: all_[name + " M"] = mirror(all_[name], name)
    # 0.22.5: _boundsCollider = convex hull of the model + glue areas (normal and mirrored)
    import numpy as np
    from scipy.spatial import ConvexHull
    from geo import Mesh
    mods = gen_meshes.models(ICON_PORTS)
    cont = os.path.dirname(os.path.abspath(folder.rstrip("/\\")))
    for name in BOUNDS_BLOCKS:
        if name not in all_ or name not in mods: print("bounds: missing", name); continue
        import models_v2
        ci = models_v2.with_extras(mods[name]); pts = [v for m in (ci, mods[name][1]) for t in m.tris for v in t[:3]]
        if name.startswith("SC_TankSmall"): pts += [v for poly in TANK_GLUE for v in poly]
        else: pts += [v for poly in GLUE_AREAS.get(name, []) for v in poly]
        P = np.array(pts, float)
        if name == "SC_TurretSeat":
            # 0.22.7 (Andre: the base's side ports unreachable): the hull went from the bottom corner of the base (x 0.75) to the plate (x 1.25) and
            # covered 25% of the cable cell next to the base. In the base's band the hull starts at x +-0.625: the cable cell stays free and the
            # base's edge stays 1/3 inside (nothing mounts there). Only the volume changes; model, glue and ports stay the same.
            m = P[:, 1] < -1.1; P[m, 0] = np.clip(P[m, 0], -0.625, 0.625)
        for sfx, sx in (("", 1.0), (" M", -1.0)):
            Q = P * np.array([sx, 1, 1]); hull = ConvexHull(Q); m = Mesh(); ctr = Q.mean(axis=0)
            for a, b, c in hull.simplices: m.tri(Q[a], Q[b], Q[c], (128, 128, 128, 0), center=ctr)
            n = name.replace(" ", "") + ("_BoundsM" if sx < 0 else "_Bounds")
            open(os.path.join(cont, "Meshes", n + ".asset"), "w", newline="\n").write(gen_meshes.asset(n, m))
            open(os.path.join(cont, "Meshes", n + ".asset.meta"), "w", newline="\n").write(gen_meshes.META_MESH % gen_meshes.guid(n))
            target = name + sfx
            if target not in all_: continue
            assert all_[target].count("_boundsCollider: {fileID: 0}") == 1, "boundsCollider in " + target
            all_[target] = all_[target].replace("_boundsCollider: {fileID: 0}", "_boundsCollider: {fileID: 4300000, guid: %s, type: 2}" % gen_meshes.guid(n), 1)
        print("  %s: bounds = convex hull (%d faces)" % (name, len(ConvexHull(P).simplices)))
    # 0.12.15: the game's meshes -> markers of our own (the plugin relinks them to the game's mesh at runtime)
    for name in list(all_):
        t = all_[name]
        for g, n in gen_meshes.GAME_MESHES.items(): t = t.replace("guid: %s, type: 2}" % g, "guid: %s, type: 2}" % gen_meshes.marker_guid(n))
        all_[name] = t
    for name, txt in all_.items():
        with open(os.path.join(folder, name + ".prefab"), "w", newline="\n") as f: f.write(txt)
        meta = os.path.join(folder, name + ".prefab.meta")
        if not os.path.exists(meta):
            g = hashlib.md5(("crapmechanics|prefab|" + name).encode()).hexdigest() if name.endswith(" M") else uuid.uuid4().hex
            with open(meta, "w", newline="\n") as f:
                f.write("fileFormatVersion: 2\nguid: %s\nPrefabImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n" % g)
        print(name, len(txt), "bytes")
