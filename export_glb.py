#!/usr/bin/env python3
"""Exports models to binary glTF (.glb), to open in Blender (File > Import > glTF 2.0).

Usage:
  python3 export_glb.py mod  <output_folder> [<game Mesh folder>]  -> the mod's blocks (+ the game's plugs on the ports)
  python3 export_glb.py game <GameObject_folder> <Mesh_folder> <output_folder> SC_Name1 SC_Name2 ...
                                                                    -> game parts, read from the Modkit workspace
Each part becomes a .glb with one object per mesh: "<part>_Body" (fixed part, color per vertex = the game's tones) and
"<part>_Paintable" (the part the brush paints, light gray). Sizes in metres, Y up. Unity is left-handed, glTF
right-handed: here z becomes -z (and the triangles are flipped), as Unity's exporters do.
Only uses Python's standard library (runs on Andre's PC without installing anything).
"""
import json, math, os, re, struct, sys


# ------------------------------------------------------------------ writing the .glb
def glb(path_, objects_):
    """objects: [(name, positions[(x,y,z)], normals[(x,y,z)], colors[(r,g,b,a) 0..255] or None, indices[int], paintable)]"""
    binary = bytearray(); views = []; accessors = []; meshes = []; nodes = []

    def add_view(data, target=None):
        while len(binary) % 4: binary.append(0)
        views.append({"buffer": 0, "byteOffset": len(binary), "byteLength": len(data)} | ({"target": target} if target else {}))
        binary.extend(data); return len(views) - 1

    mats = [{"name": "Fixed (color per vertex)", "pbrMetallicRoughness": {"baseColorFactor": [1, 1, 1, 1], "metallicFactor": 0, "roughnessFactor": 0.8}},
            {"name": "Paintable", "pbrMetallicRoughness": {"baseColorFactor": [0.75, 0.75, 0.75, 1], "metallicFactor": 0, "roughnessFactor": 0.6}}]
    for name, pos, nrm, color, idx, paintable in objects_:
        if not idx: continue
        P = [(x, y, -z) for x, y, z in pos]; N = [(x, y, -z) for x, y, z in nrm]
        I = []
        for k in range(0, len(idx), 3): I += [idx[k], idx[k + 2], idx[k + 1]]   # mirrored in z: flips the winding
        lo = [min(p[i] for p in P) for i in range(3)]; hi = [max(p[i] for p in P) for i in range(3)]
        vp = add_view(struct.pack("<%df" % (3 * len(P)), *[c for p in P for c in p]), 34962)
        accessors.append({"bufferView": vp, "componentType": 5126, "count": len(P), "type": "VEC3", "min": lo, "max": hi}); ap = len(accessors) - 1
        vn = add_view(struct.pack("<%df" % (3 * len(N)), *[c for n in N for c in n]), 34962)
        accessors.append({"bufferView": vn, "componentType": 5126, "count": len(N), "type": "VEC3"}); an = len(accessors) - 1
        attrs = {"POSITION": ap, "NORMAL": an}
        if color and not paintable:
            vc = add_view(bytes(b for c in color for b in (c[0], c[1], c[2], 255)), 34962)
            accessors.append({"bufferView": vc, "componentType": 5121, "normalized": True, "count": len(color), "type": "VEC4"}); attrs["COLOR_0"] = len(accessors) - 1
        vi = add_view(struct.pack("<%dI" % len(I), *I), 34963)
        accessors.append({"bufferView": vi, "componentType": 5125, "count": len(I), "type": "SCALAR"}); ai = len(accessors) - 1
        meshes.append({"name": name, "primitives": [{"attributes": attrs, "indices": ai, "material": 1 if paintable else 0}]})
        nodes.append({"name": name, "mesh": len(meshes) - 1})
    while len(binary) % 4: binary.append(0)
    doc = {"asset": {"version": "2.0", "generator": "Crap Mechanics export_glb.py"}, "scene": 0, "scenes": [{"nodes": list(range(len(nodes)))}],
           "nodes": nodes, "meshes": meshes, "materials": mats, "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(binary)}]}
    js = json.dumps(doc, separators=(",", ":")).encode()
    while len(js) % 4: js += b" "
    with open(path_, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(binary)))
        f.write(struct.pack("<II", len(js), 0x4E4F534A)); f.write(js)
        f.write(struct.pack("<II", len(binary), 0x004E4942)); f.write(binary)


# ------------------------------------------------------------------ the mod's blocks
def export_mod(exit_, game_mesh=None):
    """The mod's blocks (models_v2 via gen_meshes) + the game's plugs on the ports (if the game's Mesh folder is given)."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import gen_meshes, gen_prefabs
    from geo import Mesh
    gen_prefabs.blocks()   # fills in each block's ports
    os.makedirs(exit_, exist_ok=True)
    import models_v2
    for name, mod in gen_meshes.models(gen_prefabs.ICON_PORTS).items():
        body, paint_, extras_ = models_v2.parts(mod)
        objs = []
        parts = [("_Body", body, False), ("_Paintable", paint_, True)]
        for ex in extras_:   # separate renderers (piston rod/head, levers), in the rest pose
            mm = models_v2.with_extras((Mesh(), paint_, [ex])); parts.append(("_" + ex[0], mm, False))
        plugs_ = gen_meshes.plug_list(gen_prefabs.ICON_PORTS.get(name, []), game_mesh)
        if plugs_ is not None and plugs_.tris: parts.append(("_Ports", plugs_, False))
        for sfx, m, pt in parts:
            pos, nrm, color, idx = [], [], [], []
            for a, b, c, col in m.tris:
                n = [(b[1] - a[1]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[1] - a[1]), (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]), (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])]
                L = math.sqrt(sum(x * x for x in n))
                if L < 1e-12: continue
                n = [x / L for x in n]
                for p in (a, b, c): idx.append(len(pos)); pos.append(tuple(float(x) for x in p)); nrm.append(tuple(n)); color.append(tuple(col))
            objs.append((name + sfx, pos, nrm, color, idx, pt))
        glb(os.path.join(exit_, name + ".glb"), objs)
        print(name)


# ------------------------------------------------------------------ game parts (YAML from the Modkit workspace)
def read_mesh(path_):
    t = open(path_, encoding="utf-8").read()
    n = int(re.search(r"m_VertexCount: (\d+)", t).group(1)); ds = int(re.search(r"m_DataSize: (\d+)", t).group(1))
    data = bytes.fromhex(re.search(r"_typelessdata: ([0-9a-f]*)", t).group(1))
    channels = [tuple(int(x) for x in c) for c in re.findall(r"- stream: (\d+)\n\s+offset: (\d+)\n\s+format: (\d+)\n\s+dimension: (\d+)", t)]
    fmt32 = int(re.search(r"m_IndexFormat: (\d+)", t).group(1)) == 1
    ib = bytes.fromhex(re.search(r"m_IndexBuffer: ([0-9a-f]*)", t).group(1))
    idx = list(struct.unpack("<%d%s" % (len(ib) // (4 if fmt32 else 2), "I" if fmt32 else "H"), ib))
    subs = [(int(a), int(b), int(c)) for a, b, c in re.findall(r"firstByte: (\d+)\n\s+indexCount: (\d+)\n\s+topology: \d+\n\s+baseVertex: (\d+)", t)]
    step = ds // n if n else 0
    def channel(i):
        if i >= len(channels) or channels[i][3] == 0 or channels[i][0] != 0: return None
        _, off, fm, dim = channels[i]
        out = []
        for v in range(n):
            b = v * step + off
            if fm == 0: out.append(struct.unpack_from("<%df" % dim, data, b))
            elif fm == 1: out.append(struct.unpack_from("<%de" % dim, data, b))
            elif fm == 2: out.append(tuple(data[b:b + dim]))
            else: return None
        return out
    pos, nrm, color = channel(0), channel(1), channel(3)
    tris = []
    for fb, cnt, base in subs or [(0, len(idx) * (4 if fmt32 else 2), 0)]:
        begin = fb // (4 if fmt32 else 2)
        tris += [i + base for i in idx[begin:begin + cnt]]
    return pos, nrm, color, tris


def rot_euler(x, y, z):   # Unity: q = qY * qX * qZ  -> matrix R = Ry Rx Rz
    cx, sx, cy, sy, cz, sz = [f(math.radians(a)) for a in (x, y, z) for f in (math.cos, math.sin)]
    Rx = [[1, 0, 0], [0, cx, -sx], [0, sx, cx]]; Ry = [[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]]; Rz = [[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]]
    mul = lambda A, B: [[sum(A[i][k] * B[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
    return mul(mul(Ry, Rx), Rz)


def export_game(go_folder, mesh_folder, exit_, names):
    os.makedirs(exit_, exist_ok=True)
    guid_file = {}
    for f in os.listdir(mesh_folder):
        if f.endswith(".asset.meta"):
            with open(os.path.join(mesh_folder, f), encoding="utf-8") as h:
                for line in h:
                    if line.startswith("guid:"): guid_file[line.split()[1]] = os.path.join(mesh_folder, f[:-5]); break
    for name in names:
        fpath = os.path.join(go_folder, name + ".prefab")
        if not os.path.exists(fpath): print("not found", name); continue
        t = open(fpath, encoding="utf-8").read()
        paint_ = set(re.findall(r"_paintableRenderers:\n((?:  - \{fileID: \d+\}\n)+)", t) and re.findall(r"\d+", re.search(r"_paintableRenderers:\n((?:  - \{fileID: \d+\}\n)+)", t).group(1)))
        gos = {m.group(1): re.search(r"m_Name: (.*)", m.group(0)).group(1) for m in re.finditer(r"--- !u!1 &(\d+)\nGameObject:(?:.*\n)*?  m_Name: .*", t)}
        objs = []
        for m in re.finditer(r"--- !u!114 &(\d+)\nMonoBehaviour:\n((?:  .*\n)+)", t):
            fid, body = m.group(1), m.group(2)
            if "fileID: -2059388358," not in body: continue
            g = re.search(r"_mesh: \{fileID: \d+, guid: ([0-9a-f]+)", body)
            if not g or g.group(1) not in guid_file: continue
            v3 = lambda field_: [float(x) for x in re.search(r"  %s:\n    x: (\S+)\n    y: (\S+)\n    z: (\S+)" % field_, body).groups()]
            P, E, S = v3("_position"), v3("_rotation"), v3("_scale")
            R = rot_euler(*E)
            pos, nrm, color, tris = read_mesh(guid_file[g.group(1)])
            if not pos or not tris: continue
            apply_ = lambda v: tuple(P[i] + sum(R[i][k] * v[k] * S[k] for k in range(3)) for i in range(3))
            def apply_n(n):
                w = [n[k] / (S[k] if S[k] else 1) for k in range(3)]; r = [sum(R[i][k] * w[k] for k in range(3)) for i in range(3)]
                L = math.sqrt(sum(x * x for x in r)) or 1; return tuple(x / L for x in r)
            mirrored = S[0] * S[1] * S[2] < 0
            idx = []
            for k in range(0, len(tris), 3): idx += [tris[k], tris[k + 2], tris[k + 1]] if mirrored else tris[k:k + 3]
            go = re.search(r"m_GameObject: \{fileID: (\d+)", body).group(1)
            objs.append(("%s_%s" % (name, gos.get(go, fid)), [apply_(v) for v in pos], [apply_n(n) for n in nrm] if nrm else [(0, 1, 0)] * len(pos),
                         [tuple(c) for c in color] if color else None, idx, fid in paint_))
        glb(os.path.join(exit_, name + ".glb"), objs)
        print(name, len(objs), "meshes")


if __name__ == "__main__":
    if sys.argv[1] == "mod": export_mod(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
    else: export_game(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5:])
