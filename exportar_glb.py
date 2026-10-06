#!/usr/bin/env python3
"""Exporta modelos pra glTF binario (.glb), pra abrir no Blender (File > Import > glTF 2.0).

Uso:
  python3 exportar_glb.py mod  <pasta_saida> [<pasta_Mesh do jogo>]  -> os blocos do mod (+ plugues do jogo nas portas)
  python3 exportar_glb.py jogo <pasta_GameObject> <pasta_Mesh> <pasta_saida> SC_Nome1 SC_Nome2 ...
                                                                    -> pecas do jogo, lidas do workspace do Modkit
Cada peca vira um .glb com um objeto por malha: "<peca>_Corpo" (parte fixa, cor por vertice = tons do jogo) e
"<peca>_Pintavel" (parte que o pincel pinta, cinza claro). Medidas em metros, Y pra cima. Unity e' mao esquerda, glTF
mao direita: aqui z vira -z (e os triangulos sao invertidos), igual os exportadores do Unity fazem.
So usa a biblioteca padrao do Python (roda no PC do Andre sem instalar nada).
"""
import json, math, os, re, struct, sys


# ------------------------------------------------------------------ escrita do .glb
def glb(caminho, objetos):
    """objetos: [(nome, posicoes[(x,y,z)], normais[(x,y,z)], cores[(r,g,b,a) 0..255] ou None, indices[int], pintavel)]"""
    binario = bytearray(); views = []; acessores = []; malhas = []; nos = []

    def junta(dados, alvo=None):
        while len(binario) % 4: binario.append(0)
        views.append({"buffer": 0, "byteOffset": len(binario), "byteLength": len(dados)} | ({"target": alvo} if alvo else {}))
        binario.extend(dados); return len(views) - 1

    mats = [{"name": "Fixo (cor por vertice)", "pbrMetallicRoughness": {"baseColorFactor": [1, 1, 1, 1], "metallicFactor": 0, "roughnessFactor": 0.8}},
            {"name": "Pintavel", "pbrMetallicRoughness": {"baseColorFactor": [0.75, 0.75, 0.75, 1], "metallicFactor": 0, "roughnessFactor": 0.6}}]
    for nome, pos, nrm, cor, idx, pintavel in objetos:
        if not idx: continue
        P = [(x, y, -z) for x, y, z in pos]; N = [(x, y, -z) for x, y, z in nrm]
        I = []
        for k in range(0, len(idx), 3): I += [idx[k], idx[k + 2], idx[k + 1]]   # espelhou em z: inverte o sentido
        lo = [min(p[i] for p in P) for i in range(3)]; hi = [max(p[i] for p in P) for i in range(3)]
        vp = junta(struct.pack("<%df" % (3 * len(P)), *[c for p in P for c in p]), 34962)
        acessores.append({"bufferView": vp, "componentType": 5126, "count": len(P), "type": "VEC3", "min": lo, "max": hi}); ap = len(acessores) - 1
        vn = junta(struct.pack("<%df" % (3 * len(N)), *[c for n in N for c in n]), 34962)
        acessores.append({"bufferView": vn, "componentType": 5126, "count": len(N), "type": "VEC3"}); an = len(acessores) - 1
        attrs = {"POSITION": ap, "NORMAL": an}
        if cor and not pintavel:
            vc = junta(bytes(b for c in cor for b in (c[0], c[1], c[2], 255)), 34962)
            acessores.append({"bufferView": vc, "componentType": 5121, "normalized": True, "count": len(cor), "type": "VEC4"}); attrs["COLOR_0"] = len(acessores) - 1
        vi = junta(struct.pack("<%dI" % len(I), *I), 34963)
        acessores.append({"bufferView": vi, "componentType": 5125, "count": len(I), "type": "SCALAR"}); ai = len(acessores) - 1
        malhas.append({"name": nome, "primitives": [{"attributes": attrs, "indices": ai, "material": 1 if pintavel else 0}]})
        nos.append({"name": nome, "mesh": len(malhas) - 1})
    while len(binario) % 4: binario.append(0)
    doc = {"asset": {"version": "2.0", "generator": "Mecanica exportar_glb.py"}, "scene": 0, "scenes": [{"nodes": list(range(len(nos)))}],
           "nodes": nos, "meshes": malhas, "materials": mats, "accessors": acessores, "bufferViews": views, "buffers": [{"byteLength": len(binario)}]}
    js = json.dumps(doc, separators=(",", ":")).encode()
    while len(js) % 4: js += b" "
    with open(caminho, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(binario)))
        f.write(struct.pack("<II", len(js), 0x4E4F534A)); f.write(js)
        f.write(struct.pack("<II", len(binario), 0x004E4942)); f.write(binario)


# ------------------------------------------------------------------ blocos do mod
def exportar_mod(saida, mesh_jogo=None):
    """Blocos do mod (modelos_v2 via gen_meshes) + os plugues do jogo nas portas (se a pasta Mesh do jogo for dada)."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import gen_meshes, gen_prefabs
    from geo import Malha
    gen_prefabs.blocos()   # preenche as portas de cada bloco
    os.makedirs(saida, exist_ok=True)
    import modelos_v2
    for nome, mod in gen_meshes.modelos(gen_prefabs.PORTAS_ICONE).items():
        corpo, pint, extras = modelos_v2.partes(mod)
        objs = []
        partes = [("_Corpo", corpo, False), ("_Pintavel", pint, True)]
        for ex in extras:   # renderers separados (haste/cabeca do pistao, alavancas), na pose de repouso
            mm = modelos_v2.com_extras((Malha(), pint, [ex])); partes.append(("_" + ex[0], mm, False))
        plugs = gen_meshes.plugues(gen_prefabs.PORTAS_ICONE.get(nome, []), mesh_jogo)
        if plugs is not None and plugs.tris: partes.append(("_Portas", plugs, False))
        for suf, m, pt in partes:
            pos, nrm, cor, idx = [], [], [], []
            for a, b, c, col in m.tris:
                n = [(b[1] - a[1]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[1] - a[1]), (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]), (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])]
                L = math.sqrt(sum(x * x for x in n))
                if L < 1e-12: continue
                n = [x / L for x in n]
                for p in (a, b, c): idx.append(len(pos)); pos.append(tuple(float(x) for x in p)); nrm.append(tuple(n)); cor.append(tuple(col))
            objs.append((nome + suf, pos, nrm, cor, idx, pt))
        glb(os.path.join(saida, nome + ".glb"), objs)
        print(nome)


# ------------------------------------------------------------------ pecas do jogo (YAML do workspace do Modkit)
def ler_malha(caminho):
    t = open(caminho, encoding="utf-8").read()
    n = int(re.search(r"m_VertexCount: (\d+)", t).group(1)); ds = int(re.search(r"m_DataSize: (\d+)", t).group(1))
    dados = bytes.fromhex(re.search(r"_typelessdata: ([0-9a-f]*)", t).group(1))
    canais = [tuple(int(x) for x in c) for c in re.findall(r"- stream: (\d+)\n\s+offset: (\d+)\n\s+format: (\d+)\n\s+dimension: (\d+)", t)]
    fmt32 = int(re.search(r"m_IndexFormat: (\d+)", t).group(1)) == 1
    ib = bytes.fromhex(re.search(r"m_IndexBuffer: ([0-9a-f]*)", t).group(1))
    idx = list(struct.unpack("<%d%s" % (len(ib) // (4 if fmt32 else 2), "I" if fmt32 else "H"), ib))
    subs = [(int(a), int(b), int(c)) for a, b, c in re.findall(r"firstByte: (\d+)\n\s+indexCount: (\d+)\n\s+topology: \d+\n\s+baseVertex: (\d+)", t)]
    passo = ds // n if n else 0
    def canal(i):
        if i >= len(canais) or canais[i][3] == 0 or canais[i][0] != 0: return None
        _, off, fm, dim = canais[i]
        out = []
        for v in range(n):
            b = v * passo + off
            if fm == 0: out.append(struct.unpack_from("<%df" % dim, dados, b))
            elif fm == 1: out.append(struct.unpack_from("<%de" % dim, dados, b))
            elif fm == 2: out.append(tuple(dados[b:b + dim]))
            else: return None
        return out
    pos, nrm, cor = canal(0), canal(1), canal(3)
    tris = []
    for fb, cnt, base in subs or [(0, len(idx) * (4 if fmt32 else 2), 0)]:
        ini = fb // (4 if fmt32 else 2)
        tris += [i + base for i in idx[ini:ini + cnt]]
    return pos, nrm, cor, tris


def rot_euler(x, y, z):   # Unity: q = qY * qX * qZ  -> matriz R = Ry Rx Rz
    cx, sx, cy, sy, cz, sz = [f(math.radians(a)) for a in (x, y, z) for f in (math.cos, math.sin)]
    Rx = [[1, 0, 0], [0, cx, -sx], [0, sx, cx]]; Ry = [[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]]; Rz = [[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]]
    mul = lambda A, B: [[sum(A[i][k] * B[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
    return mul(mul(Ry, Rx), Rz)


def exportar_jogo(pasta_go, pasta_mesh, saida, nomes):
    os.makedirs(saida, exist_ok=True)
    guid_arq = {}
    for f in os.listdir(pasta_mesh):
        if f.endswith(".asset.meta"):
            with open(os.path.join(pasta_mesh, f), encoding="utf-8") as h:
                for linha in h:
                    if linha.startswith("guid:"): guid_arq[linha.split()[1]] = os.path.join(pasta_mesh, f[:-5]); break
    for nome in nomes:
        arq = os.path.join(pasta_go, nome + ".prefab")
        if not os.path.exists(arq): print("nao achei", nome); continue
        t = open(arq, encoding="utf-8").read()
        pint = set(re.findall(r"_paintableRenderers:\n((?:  - \{fileID: \d+\}\n)+)", t) and re.findall(r"\d+", re.search(r"_paintableRenderers:\n((?:  - \{fileID: \d+\}\n)+)", t).group(1)))
        gos = {m.group(1): re.search(r"m_Name: (.*)", m.group(0)).group(1) for m in re.finditer(r"--- !u!1 &(\d+)\nGameObject:(?:.*\n)*?  m_Name: .*", t)}
        objs = []
        for m in re.finditer(r"--- !u!114 &(\d+)\nMonoBehaviour:\n((?:  .*\n)+)", t):
            fid, corpo = m.group(1), m.group(2)
            if "fileID: -2059388358," not in corpo: continue
            g = re.search(r"_mesh: \{fileID: \d+, guid: ([0-9a-f]+)", corpo)
            if not g or g.group(1) not in guid_arq: continue
            v3 = lambda campo: [float(x) for x in re.search(r"  %s:\n    x: (\S+)\n    y: (\S+)\n    z: (\S+)" % campo, corpo).groups()]
            P, E, S = v3("_position"), v3("_rotation"), v3("_scale")
            R = rot_euler(*E)
            pos, nrm, cor, tris = ler_malha(guid_arq[g.group(1)])
            if not pos or not tris: continue
            aplica = lambda v: tuple(P[i] + sum(R[i][k] * v[k] * S[k] for k in range(3)) for i in range(3))
            def aplica_n(n):
                w = [n[k] / (S[k] if S[k] else 1) for k in range(3)]; r = [sum(R[i][k] * w[k] for k in range(3)) for i in range(3)]
                L = math.sqrt(sum(x * x for x in r)) or 1; return tuple(x / L for x in r)
            espelho = S[0] * S[1] * S[2] < 0
            idx = []
            for k in range(0, len(tris), 3): idx += [tris[k], tris[k + 2], tris[k + 1]] if espelho else tris[k:k + 3]
            go = re.search(r"m_GameObject: \{fileID: (\d+)", corpo).group(1)
            objs.append(("%s_%s" % (nome, gos.get(go, fid)), [aplica(v) for v in pos], [aplica_n(n) for n in nrm] if nrm else [(0, 1, 0)] * len(pos),
                         [tuple(c) for c in cor] if cor else None, idx, fid in pint))
        glb(os.path.join(saida, nome + ".glb"), objs)
        print(nome, len(objs), "malhas")


if __name__ == "__main__":
    if sys.argv[1] == "mod": exportar_mod(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
    else: exportar_jogo(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5:])
