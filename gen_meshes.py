#!/usr/bin/env python3
"""Generates the meshes (Unity Mesh .asset) and icons (256x256 PNG) of the Crap Mechanics mod's blocks (form6).

Each block has 2 meshes, like the game's parts:
  - "<name>_Body": fixed part, with the color in vertex color (the game's gray tones; alpha = gloss)
  - "<name>_Paintable": the part the brush paints (fixed vertex color 64,64,64,20, like the game)
Vertex format like the game's: float3 position, float3 normal, UNorm8x4 color (28 bytes); uint16 indices.
Output: <Content>/Meshes/*.asset(+.meta) and <Content>/Icons/*.png(+.meta). Fixed GUIDs (md5 of the name), so running it again
doesn't break the prefabs' references. Usage: python3 gen_meshes.py <Content folder>
"""
import hashlib, math, os, struct, sys
import numpy as np
from PIL import Image

from geo import Mesh
import models_v2

# palette (see models_v2)
BLACK, DARK, MEDIUM, LIGHT, PAINT, LIGHT_COL = models_v2.BLACK, models_v2.DARK, models_v2.MEDIUM, models_v2.LIGHT, models_v2.PAINT, models_v2.LIGHT_COL


def guid(name): return hashlib.md5(("crapmechanics|" + name).encode()).hexdigest()


def models(ports_per_block=None):
    """name -> (fixed body, paintable). The models are in models_v2.py (the game's style, 0.9.1)."""
    return models_v2.models(ports_per_block or {})


# port plugs (the game's own meshes: Port Input/Output/Power), only for the icon and the .glb — in the game the prefab already places them
PORT_FILE = {0: "Port Input.asset", 1: "Port Output.asset", 2: "Port Power.asset", 3: "Port InOut.asset", 4: "Port Plasma.asset", 5: "Port Unset.asset",
             6: "Port Plasma.asset", 7: "Port Plasma.asset", 8: "Port Plasma.asset", 10: "Port Unset.asset"}   # 0.16.0: shaft input / output / mixed (recolored)
SHAFT_COLOR_IN, SHAFT_COLOR_OUT = (0xeb, 0xb4, 0xa2), (0x64, 0x2d, 0x9a)   # Andre: input #ebb4a2, output #642d9a
PORT_DARK = (26, 0, 0)   # core of the game's plugs (not recolored)

def plug_color(t, color, cx):
    """0.16.0: color of a plug triangle: 6 = input, 7 = output, 8 = half of each (by the local x of the triangle's center)."""
    if t not in (6, 7, 8, 10) or tuple(color[:3]) == PORT_DARK: return color
    c = SHAFT_COLOR_IN if t == 6 or (t in (8, 10) and cx < 0) else SHAFT_COLOR_OUT
    return (c[0], c[1], c[2], color[3] if len(color) > 3 else 0)
ROT_DIR = {0: (0, 90, 0), 1: (0, -90, 0), 2: (-90, 0, 0), 3: (90, 0, 0), 4: (0, 0, 0), 5: (0, 180, 0)}
_plug_cache = {}


def plug_list(ports, game_mesh):
    """Mesh with the game's plugs at the port positions (None if the game's Mesh folder isn't available)."""
    if not game_mesh: return None
    import export_glb
    m = Mesh()
    for pos, d, t in ports:
        if t not in PORT_FILE: continue
        fpath = os.path.join(game_mesh, PORT_FILE[t])
        if fpath not in _plug_cache:
            if not os.path.exists(fpath): return None
            _plug_cache[fpath] = export_glb.read_mesh(fpath)
        vpos, vnrm, vcor, idx = _plug_cache[fpath]
        R = np.array(export_glb.rot_euler(*ROT_DIR[d])); P = np.array(pos, float)
        for k in range(0, len(idx), 3):
            a, b, c = [P + R @ np.array(vpos[idx[k + i]]) for i in range(3)]
            cx = sum(vpos[idx[k + i]][0] for i in range(3)) / 3.0
            m.tris.append((a, b, c, plug_color(t, tuple(vcor[idx[k]]), cx) if vcor else (200, 200, 0, 0)))
    return m


MESH_SCHEMA = """%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!43 &4300000
Mesh:
  serializedVersion: 12
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_Name: MESHNAME
  m_SubMeshes:
  - serializedVersion: 2
    firstByte: 0
    indexCount: NIDX
    topology: 0
    baseVertex: 0
    firstVertex: 0
    vertexCount: NVTX
    localAABB:
      m_Center: AABBCENTER
      m_Extent: EXTENT
  m_Shapes:
    vertices: []
    shapes: []
    channels: []
    fullWeights: []
  m_BindPose: []
  m_BoneNameHashes:
  m_RootBoneNameHash: 0
  m_BonesAABB: []
  m_VariableBoneCountWeights:
    m_Data:
  m_MeshCompression: 0
  m_IsReadable: 1
  m_KeepVertices: 0
  m_KeepIndices: 0
  m_IndexFormat: FMTIDX
  m_IndexBuffer: INDICES
  m_VertexData:
    serializedVersion: 3
    m_VertexCount: NVTX
    m_Channels:
CHANNELLIST    m_DataSize: DATASIZE
    _typelessdata: VERTEXDATA
  m_CompressedMesh:
COMPRESSEDMESH  m_LocalAABB:
    m_Center: AABBCENTER
    m_Extent: EXTENT
  m_MeshUsageFlags: 0
  m_CookingOptions: 30
  m_BakedConvexCollisionMesh:
  m_BakedTriangleCollisionMesh:
  m_MeshMetrics[0]: 1
  m_MeshMetrics[1]: 1
  m_MeshOptimizationFlags: -1
  m_StreamData:
    serializedVersion: 2
    offset: 0
    size: 0
    path:
  m_MeshLodInfo:
    serializedVersion: 2
    m_LodSelectionCurve:
      m_LodSlope: 0
      m_LodBias: 0
    m_NumLevels: 1
    m_SubMeshes:
    - serializedVersion: 2
      m_Levels:
      - m_IndexStart: 0
        m_IndexCount: 0
"""
_channels = [(0, 3), (12, 3), (0, 0), (24, 4)] + [(0, 0)] * 10   # pos, normal, tangent(no), color, uv0..7, weights, indices
CHANNELS = "".join("    - stream: 0\n      offset: %d\n      format: %d\n      dimension: %d\n" % (o, 2 if i == 3 else 0, d) for i, (o, d) in enumerate(_channels))
LENGTH = "".join("    %s:\n%s" % (k, v) for k, v in [
    ("m_Vertices", "      m_NumItems: 0\n      m_Range: 0\n      m_Start: 0\n      m_Data:\n      m_BitSize: 0\n"),
    ("m_UV", "      m_NumItems: 0\n      m_Range: 0\n      m_Start: 0\n      m_Data:\n      m_BitSize: 0\n"),
    ("m_Normals", "      m_NumItems: 0\n      m_Range: 0\n      m_Start: 0\n      m_Data:\n      m_BitSize: 0\n"),
    ("m_Tangents", "      m_NumItems: 0\n      m_Range: 0\n      m_Start: 0\n      m_Data:\n      m_BitSize: 0\n"),
    ("m_Weights", "      m_NumItems: 0\n      m_Data:\n      m_BitSize: 0\n"),
    ("m_NormalSigns", "      m_NumItems: 0\n      m_Data:\n      m_BitSize: 0\n"),
    ("m_TangentSigns", "      m_NumItems: 0\n      m_Data:\n      m_BitSize: 0\n"),
    ("m_FloatColors", "      m_NumItems: 0\n      m_Range: 0\n      m_Start: 0\n      m_Data:\n      m_BitSize: 0\n"),
    ("m_BoneIndices", "      m_NumItems: 0\n      m_Data:\n      m_BitSize: 0\n"),
    ("m_Triangles", "      m_NumItems: 0\n      m_Data:\n      m_BitSize: 0\n")]) + "    m_UVInfo: 0\n"


def f(v): return repr(float(round(v, 6)))


def asset(name, mesh):
    vs, idx = bytearray(), bytearray()
    pts = []
    n = 0
    for a, b, c, color in mesh.tris:
        nrm = np.cross(b - a, c - a); L = np.linalg.norm(nrm)
        if L < 1e-12: continue
        nrm = nrm / L
        for p in (a, b, c):
            vs += struct.pack("<6f4B", p[0], p[1], p[2], nrm[0], nrm[1], nrm[2], *color); pts.append(p)
        idx += struct.pack("<3H", n, n + 1, n + 2) if n + 2 < 65536 else b""
        n += 3
    assert n < 65536, name
    pts = np.array(pts); lo, hi = pts.min(0), pts.max(0); ce, ex = (lo + hi) / 2, (hi - lo) / 2
    t = MESH_SCHEMA
    for k, v in (("MESHNAME", name), ("NIDX", str(n)), ("NVTX", str(n)), ("AABBCENTER", "{x: %s, y: %s, z: %s}" % tuple(f(x) for x in ce)),
                 ("EXTENT", "{x: %s, y: %s, z: %s}" % tuple(f(x) for x in ex)), ("FMTIDX", "0"), ("INDICES", idx.hex()),
                 ("CHANNELLIST", CHANNELS), ("DATASIZE", str(len(vs))), ("VERTEXDATA", vs.hex()), ("COMPRESSEDMESH", LENGTH)):
        t = t.replace(k, v)
    return t


META_MESH = "fileFormatVersion: 2\nguid: %s\nNativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: 4300000\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"

# ------------------------------------------------------------------ icon (simple rasterizer, orthographic, 4x supersample)
LIGHT_DIR = np.array([0.45, 0.8, -0.4]); LIGHT_DIR /= np.linalg.norm(LIGHT_DIR)
VIEW = np.array([1.0, 0.85, -1.15]); VIEW /= np.linalg.norm(VIEW)   # where the camera looks from (sees +X, +Y and -Z)
PORT_COLOR = {0: (235, 200, 20), 1: (40, 120, 230), 2: (220, 50, 40), 3: (140, 160, 140), 4: (150, 150, 160), 5: (120, 120, 120), 9: (25, 25, 25),
             6: SHAFT_COLOR_IN, 7: SHAFT_COLOR_OUT, 8: (170, 115, 160), 10: (170, 115, 160)}


VIEW_FRONT_Z = np.array([1.0, 0.85, 1.15]); VIEW_FRONT_Z /= np.linalg.norm(VIEW_FRONT_Z)   # parts with the front at +Z (tanks)
VIEW_PER_BLOCK = {"SC_TankSmallGreen": VIEW_FRONT_Z, "SC_TankSmallBlue": VIEW_FRONT_Z, "SC_TurretSeat": VIEW_FRONT_Z}


def icon(body, paint_, ports, size_=256, ss=4, plugs_=None, VIEW=VIEW):
    W = size_ * ss
    fwd = -VIEW; up0 = np.array([0, 1.0, 0]); right = np.cross(up0, fwd); right /= np.linalg.norm(right); up = np.cross(fwd, right)
    tris = [(t, False) for t in body.tris] + [(t, True) for t in paint_.tris]
    if plugs_ is not None:
        tris += [((a, b, c, color), "plug") for a, b, c, color in plugs_.tris]
    else:   # without the game's meshes: colored discs in their place
        extra = Mesh()
        for pos, d, tp in ports:
            if tp not in PORT_COLOR or tp == 9: continue
            axis_v = d // 2; sign = 1 if d % 2 == 0 else -1
            ctr = list(pos); ctr[axis_v] += sign * 0.066
            extra.cylinder(axis_v, ctr, 0.045, 0.01, (0, 0, 0, tp), n=16)
        tris += [(t, "port") for t in extra.tris]
    P = np.array([[v for v in (t[0], t[1], t[2])] for t, _ in tris])   # (n,3,3)
    sx, sy = P @ right, P @ up
    lo = np.array([sx.min(), sy.min()]); hi = np.array([sx.max(), sy.max()])
    scl = (W * 0.86) / max(hi - lo); off = (np.array([W, W]) - (hi - lo) * scl) / 2
    img = np.zeros((W, W, 4), np.float32); zb = np.full((W, W), -1e9, np.float32)
    for (t, kind), px, py in zip(tris, sx, sy):
        a, b, c, color = t
        nrm = np.cross(b - a, c - a); L = np.linalg.norm(nrm)
        if L < 1e-12: continue
        nrm /= L
        if nrm @ VIEW <= 0: continue
        light = 0.55 + 0.45 * max(0.0, float(nrm @ LIGHT_DIR))
        if kind == "port": base = np.array(PORT_COLOR[color[3]], float); light = 0.9 + 0.1 * light
        elif kind == "plug": base = np.array(color[:3], float) * 1.0 + 20; light = 0.8 + 0.2 * light
        elif kind: base = np.array([205, 205, 205], float)
        else: base = np.minimum(255, np.array(color[:3], float) * 1.6 + 30)
        rgb = np.clip(base * light, 0, 255)
        X = (px - lo[0]) * scl + off[0]; Y = W - ((py - lo[1]) * scl + off[1])
        Z = np.array([a @ VIEW, b @ VIEW, c @ VIEW])
        x0, x1 = int(max(0, np.floor(X.min()))), int(min(W - 1, np.ceil(X.max())))
        y0, y1 = int(max(0, np.floor(Y.min()))), int(min(W - 1, np.ceil(Y.max())))
        if x1 < x0 or y1 < y0: continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        d = (X[1] - X[0]) * (Y[2] - Y[0]) - (X[2] - X[0]) * (Y[1] - Y[0])
        if abs(d) < 1e-9: continue
        w1 = ((gx - X[0]) * (Y[2] - Y[0]) - (X[2] - X[0]) * (gy - Y[0])) / d
        w2 = ((X[1] - X[0]) * (gy - Y[0]) - (gx - X[0]) * (Y[1] - Y[0])) / d
        w0 = 1 - w1 - w2
        inside = (w0 >= -1e-4) & (w1 >= -1e-4) & (w2 >= -1e-4)
        z = w0 * Z[0] + w1 * Z[1] + w2 * Z[2] + (0.002 if kind == "port" else 0)
        sub = zb[y0:y1 + 1, x0:x1 + 1]; m = inside & (z > sub)
        sub[m] = z[m]
        img[y0:y1 + 1, x0:x1 + 1][m] = np.array([*rgb, 255], np.float32)
    im = Image.fromarray(img.astype(np.uint8), "RGBA").resize((size_, size_), Image.LANCZOS)
    return im


META_ICON_TEMPLATE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "icon_template.png.meta")


# 0.12.15: the GAME's meshes that the prefabs use (port plugs, seat, tank gauge/valve). The Modkit put a copy
# of them inside the mod's bundle (redistributing a game asset). Now the prefab points to a MARKER of our own "Game_<name>" (a
# tiny triangle) and the plugin (GameMeshes.cs) swaps it for the game's real mesh, found by name in the game's components.
GAME_MESHES = {
    "fe00b5509a5a3094493c4f4ab77c0e9d": "Solar Panel Body", "64708f33bff0c2745bf70df3270e39be": "Solar Panel Body Paintable",   # 0.19.0
    "c0b5977b88cf9914aa6e3f97f3368415": "Port Input", "9ee315e960dc9754d9c7201f234c99ee": "Port Output", "524c7b0ae261a9244b97246a5799c9b4": "Port Power",
    "a6124f93d04cc514a8f1cde6c64fe83b": "Seat Lever", "7bef07ae0e31764439d6b3b46524f50d": "Seat Piston", "73928387c7508b444b75447f9f2dabc1": "Seat Upper",
    "8565a050479c9ad42a7d67a5126c6e93": "Seat Upper Paintable", "e66b1270eeda9a344b46fb53c6756302": "Tank Meter Arm",
    "408c91da95af2734fa0f382e37d5736c": "Tank Meter With Port Arm", "6c584c3034400a340ae1e257beb53569": "Tank Meter With Port Body",
    "7499b1d91b8544a4699364cd37ad2957": "Valve",
    "3a17f26f6c26b8446b3b3a0e51cdcd7f": "Port Plasma", "55c4d8170d158c149a9d1c46d03aea9a": "Port Unset",   # 0.15.0
    # 0.16.0: Shaft Transmitter = copy of the game's SC_WirelessTransmitter (meshes relinked at runtime; the Body Paintable in the collider too)
    "6ae3c4a33e20464449e77878864deb30": "Wireless Transmitter Body", "342554b462ef80d4ba88174d2bce0060": "Wireless Transmitter Body Paintable",
    "7911e74b4b59f284db6e6cb279dce854": "Wireless Transmitter Indicator", "ce137a3da99fda244ac2396b5fcd1c7c": "Wireless Transmitter Actionable",
    "a51a79546a18cfb429fe0a216ff80cb6": "Wireless Transmitter Label",
    # 0.18.0: battery bar, InOut port, Small Electric Thruster (mini)
    "14b26e76dee50ad45a1f460c93537766": "Small Rechargeable Battery Indicator", "2d697d79043ceb1468a4f403ec2484ff": "Port InOut",
    "3ac48c3be5bac7a48b2f2fd6129cc9c0": "Small Electric Thruster Body Paintable", "9bac70cb5308d2c4d92d98dc195fb2a8": "Small Electric Thruster Body",
    "5f5a0090af4e78546a12b662b231484a": "Small Electric Thruster Ignition", "aed498f03cf7e66458d8c778ac16b5dd": "Small Electric Thruster Collider",
    "bc068c895536370438c8005da9531562": "Port Reverse Mode Button", "e6ad0d2b24847434eaf5c03d818d8f68": "Thin Particle"}
SHAFT_PORT_COLORS = ["Port Plasma~ebb4a2", "Port Plasma~642d9a", "Port Plasma~mix", "Port Unset~mix"]   # 0.16.0: shaft plugs recolored at runtime
def marker_guid(game_name): return guid("game|" + game_name)


def generate_markers(content):
    for name in list(GAME_MESHES.values()) + ["DriveShaftCable"] + SHAFT_PORT_COLORS:   # 0.15.0: CaboPlasma = Drive Shaft meshes (copied from the plasma cable at runtime)
        m = Mesh(); m.tri((0, 0, 0), (0.001, 0, 0), (0, 0.001, 0), (17, 17, 17, 0))
        n = "Game_" + name
        open(os.path.join(content, "Meshes", n + ".asset"), "w", newline="\n").write(asset(n, m))
        open(os.path.join(content, "Meshes", n + ".asset.meta"), "w", newline="\n").write(META_MESH % marker_guid(name))


# 0.19.2 (Andre: "the collisions are all square; in the game many follow the model and you can run a cable past a corner"): convex
# colliders from the MODEL. The block is cut into horizontal slices where the cross-section changes a lot (wide base x round body) and each
# slice becomes a convex hull (scipy). Compounds: the base stays a box, the round body leaves the corners free for cables.
HULL_BLOCKS = ["SC_Bearing", "SC_GyroBearing", "SC_MotorSmall", "SC_MotorMedium", "SC_MotorLarge", "SC_MotorFuelSmall", "SC_MotorFuelMedium", "SC_MotorFuelLarge", "SC_Recoupler",
               "SC_Differential", "SC_ShaftReverser", "SC_Piston", "SC_MiniPiston", "SC_MiniBearing", "SC_MiniGyroBearing", "SC_SlipRing", "SC_MiniSlipRing", "SC_PistonEnd", "SC_MiniPistonEnd",   # 0.20.0; 0.22.0/0.22.4: rings and tips
               "SC_Pedal", "SC_SteeringWheel", "SC_SingleCellBattery"]   # 0.19.3
def _components(T):
    """T (n,3,3) -> list of index arrays of triangles connected by shared vertices (rounded to 0.01 mm)."""
    n = len(T)
    if n == 0: return []
    key_ = np.round(T.reshape(-1, 3), 5)
    _, inv = np.unique(key_, axis=0, return_inverse=True)
    inv = inv.reshape(n, 3)
    parent = list(range(n))
    def find_(a):
        while parent[a] != a: parent[a] = parent[parent[a]]; a = parent[a]
        return a
    owner = {}
    for i in range(n):
        for v in inv[i]:
            v = int(v)
            if v in owner:
                a, b = find_(owner[v]), find_(i)
                if a != b: parent[a] = b
            else: owner[v] = i
    groups_ = {}
    for i in range(n): groups_.setdefault(find_(i), []).append(i)
    return [np.array(g) for g in groups_.values()]


def _vol(pts):
    from scipy.spatial import ConvexHull
    if len(pts) < 8: return 0.0
    try: return ConvexHull(pts).volume
    except Exception: return 0.0


def _cut(T, idx, depth, vol_min):
    """Hull(s) of one piece (indices idx in T). 0.19.3 (Andre: "the collision has to follow the model, like the game's curved frames:
    cables, sensors, logic blocks fit in the gaps"): if cutting the piece by a plane (3 axes x several positions) and redoing the
    components on each side lowers the total hull volume by 15 %+, it cuts (up to 3 levels = up to 8 pieces per part). A convex
    block never gains from the cut; a ring becomes arcs, a "U" becomes 3 boxes."""
    pts = T[idx].reshape(-1, 3); v0 = _vol(pts)
    if v0 < vol_min: return []
    if depth >= 3: return [idx]
    ctr = T[idx].mean(axis=1)   # centroids (k,3)
    best = (v0, None)
    for axis_v in range(3):
        lo, hi = ctr[:, axis_v].min(), ctr[:, axis_v].max()
        if hi - lo < 0.03: continue
        for i in range(1, 8):
            cut = lo + (hi - lo) * i / 8
            sides = [idx[ctr[:, axis_v] < cut], idx[ctr[:, axis_v] >= cut]]
            if min(len(sides[0]), len(sides[1])) < 4: continue
            v = 0.0; parts = []
            for l in sides:
                for g in _components(T[l]): parts.append(l[g]); v += _vol(T[l[g]].reshape(-1, 3))
            if v < best[0]: best = (v, parts)
    if best[1] is None or best[0] > v0 * 0.85: return [idx]
    exit_ = []
    for part in best[1]: exit_ += _cut(T, part, depth + 1, vol_min)
    return exit_


def hulls(meshes, max_hulls=16, vol_min=5e-5):
    """List of Meshes (only what stays still) -> list of convex hulls (Mesh). 0.19.3: one hull per PIECE of the model (component
    connected by vertices = each primitive: box, cylinder, ring...), concave pieces cut (_cut); small bits (screws, leds)
    left out; at most max_hulls (the biggest ones)."""
    from scipy.spatial import ConvexHull
    T = np.array([[t[0], t[1], t[2]] for m in meshes for t in m.tris], float)   # (n,3,3)
    if len(T) < 4: return []
    pieces = []
    for g in _components(T):
        for idx in _cut(T, g, 0, vol_min):
            pts = T[idx].reshape(-1, 3); v = _vol(pts)
            if v >= vol_min: pieces.append((v, pts))
    pieces.sort(key=lambda t: -t[0])
    exit_ = []; hulls_done = []
    for v, sel in pieces:
        if len(exit_) >= max_hulls: break
        try: h = ConvexHull(sel)
        except Exception: continue
        # piece (almost) entirely inside a bigger hull already made (a detail on top of a box): not needed
        inside = False
        for eq in hulls_done:
            d = sel @ eq[:, :3].T + eq[:, 3]   # (points, planes): <= 0 inside
            if (d.max(axis=1) <= 0.002).mean() >= 0.95: inside = True; break
        if inside: continue
        hulls_done.append(h.equations)
        m = Mesh(); ctr = sel[h.vertices].mean(axis=0)
        for sx in h.simplices:
            a, b, c = sel[sx[0]], sel[sx[1]], sel[sx[2]]
            m.tri(a, b, c, (120, 120, 120, 0), center=ctr)
        exit_.append(m)
    return exit_


def generate(content, ports_per_block=None, game_mesh=None):
    os.makedirs(os.path.join(content, "Meshes"), exist_ok=True); os.makedirs(os.path.join(content, "Icons"), exist_ok=True)
    generate_markers(content)
    meta_icon = open(META_ICON_TEMPLATE, encoding="utf-8").read()
    ref = {}
    for name, mod in models(ports_per_block).items():
        body, paint_ = mod[0], mod[1]
        extras_ = mod[2] if len(mod) > 2 else []   # 0.11.0: extra renderers (suffix, mesh, pos, scale) — piston rod/head
        for sfx, m in [("_Body", body), ("_Paintable", paint_)] + [("_" + x[0], x[1]) for x in extras_]:
            if not m.tris: continue
            n = name + sfx
            open(os.path.join(content, "Meshes", n + ".asset"), "w", newline="\n").write(asset(n, m))
            open(os.path.join(content, "Meshes", n + ".asset.meta"), "w", newline="\n").write(META_MESH % guid(n))
        pl = (ports_per_block or {}).get(name, [])
        # icon: body + paintable + extras at the rest position/scale
        from models_v2 import with_extras
        ci = with_extras(mod)
        if len(mod) > 3 and mod[3] is not None: ci = Mesh(); ci.tris = list(with_extras(mod).tris) + list(mod[3].tris)   # 0.12.0: preview (the game's meshes, only in the picture)
        im = icon(ci, paint_, pl, plugs_=plug_list(pl, game_mesh), VIEW=VIEW_PER_BLOCK.get(name, VIEW))
        im.save(os.path.join(content, "Icons", name + ".png"))
        import re
        open(os.path.join(content, "Icons", name + ".png.meta"), "w", newline="\n").write(re.sub(r"^guid: [0-9a-f]+", "guid: " + guid(name + "_Icon"), meta_icon, flags=re.M))
        cols = []
        if name in HULL_BLOCKS:   # 0.19.2: convex hulls of the model (only body + paintable: the extras move)
            for k, cm in enumerate(hulls([body, paint_])):
                n = name + "_Col%d" % k
                open(os.path.join(content, "Meshes", n + ".asset"), "w", newline="\n").write(asset(n, cm))
                open(os.path.join(content, "Meshes", n + ".asset.meta"), "w", newline="\n").write(META_MESH % guid(n))
                cols.append(guid(n))
            print("  %s: %d convex collider(s)" % (name, len(cols)))
        ref[name] = (guid(name + "_Body"), guid(name + "_Paintable") if paint_.tris else None, guid(name + "_Icon"),
                     [(ex[2], ex[4] if len(ex) > 4 else (0, 0, 0), ex[3], guid(name + "_" + ex[0]), ex[0]) for ex in extras_],   # 0.11.1: + suffix (levers); 0.11.2: rest rotation
                     cols)   # 0.19.2: meshes of the convex colliders
    return ref


if __name__ == "__main__":
    print(generate(sys.argv[1] if len(sys.argv) > 1 else "Content"))
