#!/usr/bin/env python3
"""Gera as malhas (Mesh .asset do Unity) e os icones (PNG 256x256) dos blocos do mod Mecanica (form6).

Cada bloco tem 2 malhas, igual as pecas do jogo:
  - "<nome>_Corpo": parte fixa, com a cor em vertex color (tons de cinza do jogo; alfa = brilho)
  - "<nome>_Pintavel": parte que o pincel pinta (vertex color fixa 64,64,64,20, igual o jogo)
Formato do vertice igual ao do jogo: posicao float3, normal float3, cor UNorm8x4 (28 bytes); indices uint16.
Saida: <Content>/Meshes/*.asset(+.meta) e <Content>/Icons/*.png(+.meta). GUIDs fixos (md5 do nome), entao rodar de novo
nao quebra as referencias dos prefabs. Uso: python3 gen_meshes.py <pasta Content>
"""
import hashlib, math, os, struct, sys
import numpy as np
from PIL import Image

from geo import Malha
import modelos_v2

# paleta (ver modelos_v2)
PRETO, ESCURO, MEDIO, CLARO, PINTA, LUZ = modelos_v2.PRETO, modelos_v2.ESCURO, modelos_v2.MEDIO, modelos_v2.CLARO, modelos_v2.PINTA, modelos_v2.LUZ


def guid(nome): return hashlib.md5(("mecanica|" + nome).encode()).hexdigest()


def modelos(portas_por_bloco=None):
    """nome -> (corpo fixo, pintavel). Os modelos estao em modelos_v2.py (estilo do jogo, 0.9.1)."""
    return modelos_v2.modelos(portas_por_bloco or {})


# plugues das portas (malhas do proprio jogo: Port Input/Output/Power), so pro icone e pro .glb — no jogo o prefab ja os poe
ARQ_PORTA = {0: "Port Input.asset", 1: "Port Output.asset", 2: "Port Power.asset", 3: "Port InOut.asset", 4: "Port Plasma.asset", 5: "Port Unset.asset",
             6: "Port Plasma.asset", 7: "Port Plasma.asset", 8: "Port Plasma.asset", 10: "Port Unset.asset"}   # 0.16.0: eixo entrada / saida / misto (recoloridos)
COR_EIXO_ENTRA, COR_EIXO_SAI = (0xeb, 0xb4, 0xa2), (0x64, 0x2d, 0x9a)   # Andre: entrada #ebb4a2, saida #642d9a
ESCURO_PORTA = (26, 0, 0)   # miolo dos plugues do jogo (nao recolore)

def cor_plugue(t, cor, cx):
    """0.16.0: cor de um triangulo do plugue: 6 = entrada, 7 = saida, 8 = metade de cada (pelo x local do centro do triangulo)."""
    if t not in (6, 7, 8, 10) or tuple(cor[:3]) == ESCURO_PORTA: return cor
    c = COR_EIXO_ENTRA if t == 6 or (t in (8, 10) and cx < 0) else COR_EIXO_SAI
    return (c[0], c[1], c[2], cor[3] if len(cor) > 3 else 0)
ROT_DIR = {0: (0, 90, 0), 1: (0, -90, 0), 2: (-90, 0, 0), 3: (90, 0, 0), 4: (0, 0, 0), 5: (0, 180, 0)}
_plug_cache = {}


def plugues(portas, mesh_jogo):
    """Malha com os plugues do jogo nas posicoes das portas (None se a pasta Mesh do jogo nao estiver disponivel)."""
    if not mesh_jogo: return None
    import exportar_glb
    m = Malha()
    for pos, d, t in portas:
        if t not in ARQ_PORTA: continue
        arq = os.path.join(mesh_jogo, ARQ_PORTA[t])
        if arq not in _plug_cache:
            if not os.path.exists(arq): return None
            _plug_cache[arq] = exportar_glb.ler_malha(arq)
        vpos, vnrm, vcor, idx = _plug_cache[arq]
        R = np.array(exportar_glb.rot_euler(*ROT_DIR[d])); P = np.array(pos, float)
        for k in range(0, len(idx), 3):
            a, b, c = [P + R @ np.array(vpos[idx[k + i]]) for i in range(3)]
            cx = sum(vpos[idx[k + i]][0] for i in range(3)) / 3.0
            m.tris.append((a, b, c, cor_plugue(t, tuple(vcor[idx[k]]), cx) if vcor else (200, 200, 0, 0)))
    return m


ESQ_MESH = """%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!43 &4300000
Mesh:
  serializedVersion: 12
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_Name: NOME
  m_SubMeshes:
  - serializedVersion: 2
    firstByte: 0
    indexCount: NIDX
    topology: 0
    baseVertex: 0
    firstVertex: 0
    vertexCount: NVTX
    localAABB:
      m_Center: CENTRO
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
CANAIS    m_DataSize: TAMANHO
    _typelessdata: DADOS
  m_CompressedMesh:
COMPRIMIDO  m_LocalAABB:
    m_Center: CENTRO
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
_canais = [(0, 3), (12, 3), (0, 0), (24, 4)] + [(0, 0)] * 10   # pos, normal, tangente(nao), cor, uv0..7, pesos, indices
CANAIS = "".join("    - stream: 0\n      offset: %d\n      format: %d\n      dimension: %d\n" % (o, 2 if i == 3 else 0, d) for i, (o, d) in enumerate(_canais))
COMP = "".join("    %s:\n%s" % (k, v) for k, v in [
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


def asset(nome, malha):
    vs, idx = bytearray(), bytearray()
    pts = []
    n = 0
    for a, b, c, cor in malha.tris:
        nrm = np.cross(b - a, c - a); L = np.linalg.norm(nrm)
        if L < 1e-12: continue
        nrm = nrm / L
        for p in (a, b, c):
            vs += struct.pack("<6f4B", p[0], p[1], p[2], nrm[0], nrm[1], nrm[2], *cor); pts.append(p)
        idx += struct.pack("<3H", n, n + 1, n + 2) if n + 2 < 65536 else b""
        n += 3
    assert n < 65536, nome
    pts = np.array(pts); lo, hi = pts.min(0), pts.max(0); ce, ex = (lo + hi) / 2, (hi - lo) / 2
    t = ESQ_MESH
    for k, v in (("NOME", nome), ("NIDX", str(n)), ("NVTX", str(n)), ("CENTRO", "{x: %s, y: %s, z: %s}" % tuple(f(x) for x in ce)),
                 ("EXTENT", "{x: %s, y: %s, z: %s}" % tuple(f(x) for x in ex)), ("FMTIDX", "0"), ("INDICES", idx.hex()),
                 ("CANAIS", CANAIS), ("TAMANHO", str(len(vs))), ("DADOS", vs.hex()), ("COMPRIMIDO", COMP)):
        t = t.replace(k, v)
    return t


META_MESH = "fileFormatVersion: 2\nguid: %s\nNativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: 4300000\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"

# ------------------------------------------------------------------ icone (rasterizador simples, ortografico, 4x supersample)
LUZ_DIR = np.array([0.45, 0.8, -0.4]); LUZ_DIR /= np.linalg.norm(LUZ_DIR)
VISTA = np.array([1.0, 0.85, -1.15]); VISTA /= np.linalg.norm(VISTA)   # de onde a camera olha (ve +X, +Y e -Z)
COR_PORTA = {0: (235, 200, 20), 1: (40, 120, 230), 2: (220, 50, 40), 3: (140, 160, 140), 4: (150, 150, 160), 5: (120, 120, 120), 9: (25, 25, 25),
             6: COR_EIXO_ENTRA, 7: COR_EIXO_SAI, 8: (170, 115, 160), 10: (170, 115, 160)}


VISTA_FRENTE_Z = np.array([1.0, 0.85, 1.15]); VISTA_FRENTE_Z /= np.linalg.norm(VISTA_FRENTE_Z)   # pecas com a frente em +Z (tanques)
VISTA_POR_BLOCO = {"SC_TanqueVerde": VISTA_FRENTE_Z, "SC_TanqueAzul": VISTA_FRENTE_Z, "SC_TurretSeat": VISTA_FRENTE_Z}


def icone(corpo, pint, portas, tam=256, ss=4, plugs=None, VISTA=VISTA):
    W = tam * ss
    fwd = -VISTA; up0 = np.array([0, 1.0, 0]); right = np.cross(up0, fwd); right /= np.linalg.norm(right); up = np.cross(fwd, right)
    tris = [(t, False) for t in corpo.tris] + [(t, True) for t in pint.tris]
    if plugs is not None:
        tris += [((a, b, c, cor), "plug") for a, b, c, cor in plugs.tris]
    else:   # sem as malhas do jogo: discos coloridos no lugar
        extra = Malha()
        for pos, d, tp in portas:
            if tp not in COR_PORTA or tp == 9: continue
            eixo = d // 2; sinal = 1 if d % 2 == 0 else -1
            cen = list(pos); cen[eixo] += sinal * 0.066
            extra.cilindro(eixo, cen, 0.045, 0.01, (0, 0, 0, tp), n=16)
        tris += [(t, "porta") for t in extra.tris]
    P = np.array([[v for v in (t[0], t[1], t[2])] for t, _ in tris])   # (n,3,3)
    sx, sy = P @ right, P @ up
    lo = np.array([sx.min(), sy.min()]); hi = np.array([sx.max(), sy.max()])
    esc = (W * 0.86) / max(hi - lo); off = (np.array([W, W]) - (hi - lo) * esc) / 2
    img = np.zeros((W, W, 4), np.float32); zb = np.full((W, W), -1e9, np.float32)
    for (t, tipo), px, py in zip(tris, sx, sy):
        a, b, c, cor = t
        nrm = np.cross(b - a, c - a); L = np.linalg.norm(nrm)
        if L < 1e-12: continue
        nrm /= L
        if nrm @ VISTA <= 0: continue
        luz = 0.55 + 0.45 * max(0.0, float(nrm @ LUZ_DIR))
        if tipo == "porta": base = np.array(COR_PORTA[cor[3]], float); luz = 0.9 + 0.1 * luz
        elif tipo == "plug": base = np.array(cor[:3], float) * 1.0 + 20; luz = 0.8 + 0.2 * luz
        elif tipo: base = np.array([205, 205, 205], float)
        else: base = np.minimum(255, np.array(cor[:3], float) * 1.6 + 30)
        rgb = np.clip(base * luz, 0, 255)
        X = (px - lo[0]) * esc + off[0]; Y = W - ((py - lo[1]) * esc + off[1])
        Z = np.array([a @ VISTA, b @ VISTA, c @ VISTA])
        x0, x1 = int(max(0, np.floor(X.min()))), int(min(W - 1, np.ceil(X.max())))
        y0, y1 = int(max(0, np.floor(Y.min()))), int(min(W - 1, np.ceil(Y.max())))
        if x1 < x0 or y1 < y0: continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        d = (X[1] - X[0]) * (Y[2] - Y[0]) - (X[2] - X[0]) * (Y[1] - Y[0])
        if abs(d) < 1e-9: continue
        w1 = ((gx - X[0]) * (Y[2] - Y[0]) - (X[2] - X[0]) * (gy - Y[0])) / d
        w2 = ((X[1] - X[0]) * (gy - Y[0]) - (gx - X[0]) * (Y[1] - Y[0])) / d
        w0 = 1 - w1 - w2
        dentro = (w0 >= -1e-4) & (w1 >= -1e-4) & (w2 >= -1e-4)
        z = w0 * Z[0] + w1 * Z[1] + w2 * Z[2] + (0.002 if tipo == "porta" else 0)
        sub = zb[y0:y1 + 1, x0:x1 + 1]; m = dentro & (z > sub)
        sub[m] = z[m]
        img[y0:y1 + 1, x0:x1 + 1][m] = np.array([*rgb, 255], np.float32)
    im = Image.fromarray(img.astype(np.uint8), "RGBA").resize((tam, tam), Image.LANCZOS)
    return im


META_ICONE_MODELO = os.path.join(os.path.dirname(os.path.abspath(__file__)), "icon_template.png.meta")


# 0.12.15: malhas do JOGO que os prefabs usam (plugues das portas, seat, medidor/valvula do tanque). O Modkit colocava uma copia
# delas dentro do bundle do mod (redistribuir asset do jogo). Agora o prefab aponta pra um MARCADOR proprio "Jogo_<nome>" (um
# triangulo minusculo) e o plugin (JogoMalhas.cs) troca pela malha de verdade do jogo, achada pelo nome nos componentes do jogo.
MALHAS_JOGO = {
    "fe00b5509a5a3094493c4f4ab77c0e9d": "Solar Panel Body", "64708f33bff0c2745bf70df3270e39be": "Solar Panel Body Paintable",   # 0.19.0
    "c0b5977b88cf9914aa6e3f97f3368415": "Port Input", "9ee315e960dc9754d9c7201f234c99ee": "Port Output", "524c7b0ae261a9244b97246a5799c9b4": "Port Power",
    "a6124f93d04cc514a8f1cde6c64fe83b": "Seat Lever", "7bef07ae0e31764439d6b3b46524f50d": "Seat Piston", "73928387c7508b444b75447f9f2dabc1": "Seat Upper",
    "8565a050479c9ad42a7d67a5126c6e93": "Seat Upper Paintable", "e66b1270eeda9a344b46fb53c6756302": "Tank Meter Arm",
    "408c91da95af2734fa0f382e37d5736c": "Tank Meter With Port Arm", "6c584c3034400a340ae1e257beb53569": "Tank Meter With Port Body",
    "7499b1d91b8544a4699364cd37ad2957": "Valve",
    "3a17f26f6c26b8446b3b3a0e51cdcd7f": "Port Plasma", "55c4d8170d158c149a9d1c46d03aea9a": "Port Unset",   # 0.15.0
    # 0.16.0: Shaft Transmitter = copia do SC_WirelessTransmitter do jogo (malhas religadas em runtime; a Body Paintable tambem no colisor)
    "6ae3c4a33e20464449e77878864deb30": "Wireless Transmitter Body", "342554b462ef80d4ba88174d2bce0060": "Wireless Transmitter Body Paintable",
    "7911e74b4b59f284db6e6cb279dce854": "Wireless Transmitter Indicator", "ce137a3da99fda244ac2396b5fcd1c7c": "Wireless Transmitter Actionable",
    "a51a79546a18cfb429fe0a216ff80cb6": "Wireless Transmitter Label",
    # 0.18.0: barra da bateria, porta InOut, Small Electric Thruster (mini)
    "14b26e76dee50ad45a1f460c93537766": "Small Rechargeable Battery Indicator", "2d697d79043ceb1468a4f403ec2484ff": "Port InOut",
    "3ac48c3be5bac7a48b2f2fd6129cc9c0": "Small Electric Thruster Body Paintable", "9bac70cb5308d2c4d92d98dc195fb2a8": "Small Electric Thruster Body",
    "5f5a0090af4e78546a12b662b231484a": "Small Electric Thruster Ignition", "aed498f03cf7e66458d8c778ac16b5dd": "Small Electric Thruster Collider",
    "bc068c895536370438c8005da9531562": "Port Reverse Mode Button", "e6ad0d2b24847434eaf5c03d818d8f68": "Thin Particle"}
PORTAS_EIXO_COR = ["Port Plasma~ebb4a2", "Port Plasma~642d9a", "Port Plasma~mix", "Port Unset~mix"]   # 0.16.0: plugues de eixo recoloridos em runtime
def guid_marcador(nome_jogo): return guid("jogo|" + nome_jogo)


def gerar_marcadores(content):
    for nome in list(MALHAS_JOGO.values()) + ["CaboPlasma"] + PORTAS_EIXO_COR:   # 0.15.0: CaboPlasma = malhas do Drive Shaft (copiadas do cabo de plasma em runtime)
        m = Malha(); m.tri((0, 0, 0), (0.001, 0, 0), (0, 0.001, 0), (17, 17, 17, 0))
        n = "Jogo_" + nome
        open(os.path.join(content, "Meshes", n + ".asset"), "w", newline="\n").write(asset(n, m))
        open(os.path.join(content, "Meshes", n + ".asset.meta"), "w", newline="\n").write(META_MESH % guid_marcador(nome))


# 0.19.2 (Andre: "as colisoes sao todas quadradas; no jogo muitas seguem o modelo e da pra passar cabo por uma quina"): colisores
# convexos a partir do MODELO. O bloco e' cortado em fatias horizontais onde a secao muda muito (base larga x corpo redondo) e cada
# fatia vira um casco convexo (scipy). Compostos: a base continua caixa, o corpo redondo deixa as quinas livres pros cabos.
HULL_BLOCOS = ["SC_Bearing", "SC_GyroBearing", "SC_MotorP", "SC_MotorM", "SC_MotorG", "SC_MotorCombP", "SC_MotorCombM", "SC_MotorCombG", "SC_Recoupler",
               "SC_Differential", "SC_ShaftReverser", "SC_Pistao", "SC_MiniPistao", "SC_MiniBearing", "SC_MiniGyroBearing", "SC_SlipRing", "SC_MiniSlipRing", "SC_PistaoFim", "SC_MiniPistaoFim",   # 0.20.0; 0.22.0/0.22.4: aneis e pontas
               "SC_Pedal", "SC_Volante", "SC_SingleCellBattery"]   # 0.19.3
def _componentes(T):
    """T (n,3,3) -> lista de arrays de indices de triangulos ligados por vertices em comum (arredondados a 0,01 mm)."""
    n = len(T)
    if n == 0: return []
    chave = np.round(T.reshape(-1, 3), 5)
    _, inv = np.unique(chave, axis=0, return_inverse=True)
    inv = inv.reshape(n, 3)
    pai = list(range(n))
    def achar(a):
        while pai[a] != a: pai[a] = pai[pai[a]]; a = pai[a]
        return a
    dono = {}
    for i in range(n):
        for v in inv[i]:
            v = int(v)
            if v in dono:
                a, b = achar(dono[v]), achar(i)
                if a != b: pai[a] = b
            else: dono[v] = i
    grupos = {}
    for i in range(n): grupos.setdefault(achar(i), []).append(i)
    return [np.array(g) for g in grupos.values()]


def _vol(pts):
    from scipy.spatial import ConvexHull
    if len(pts) < 8: return 0.0
    try: return ConvexHull(pts).volume
    except Exception: return 0.0


def _cortar(T, idx, prof, vol_min):
    """Casco(s) de um pedaco (indices idx em T). 0.19.3 (Andre: "a colisao tem que seguir o modelo, igual os frames curved do jogo:
    nos vaos cabem cabos, sensores, blocos de logica"): se cortar o pedaco num plano (3 eixos x varias posicoes) e refazer as
    componentes de cada lado baixar o volume total dos cascos em 15 %+, corta (ate 3 niveis = ate 8 pedacos por peca). Um bloco
    convexo nunca ganha com o corte; um anel vira arcos, um "U" vira 3 caixas."""
    pts = T[idx].reshape(-1, 3); v0 = _vol(pts)
    if v0 < vol_min: return []
    if prof >= 3: return [idx]
    cen = T[idx].mean(axis=1)   # centroides (k,3)
    melhor = (v0, None)
    for eixo in range(3):
        lo, hi = cen[:, eixo].min(), cen[:, eixo].max()
        if hi - lo < 0.03: continue
        for i in range(1, 8):
            corte = lo + (hi - lo) * i / 8
            lados = [idx[cen[:, eixo] < corte], idx[cen[:, eixo] >= corte]]
            if min(len(lados[0]), len(lados[1])) < 4: continue
            v = 0.0; partes = []
            for l in lados:
                for g in _componentes(T[l]): partes.append(l[g]); v += _vol(T[l[g]].reshape(-1, 3))
            if v < melhor[0]: melhor = (v, partes)
    if melhor[1] is None or melhor[0] > v0 * 0.85: return [idx]
    saida = []
    for parte in melhor[1]: saida += _cortar(T, parte, prof + 1, vol_min)
    return saida


def cascos(malhas, max_cascos=16, vol_min=5e-5):
    """Lista de Malhas (so o que fica parado) -> lista de cascos convexos (Malha). 0.19.3: um casco por PEDACO do modelo (componente
    ligada por vertices = cada primitiva: caixa, cilindro, anel...), pedacos concavos cortados (_cortar); miudezas (parafusos, leds)
    fora; no maximo max_cascos (os maiores)."""
    from scipy.spatial import ConvexHull
    T = np.array([[t[0], t[1], t[2]] for m in malhas for t in m.tris], float)   # (n,3,3)
    if len(T) < 4: return []
    pedacos = []
    for g in _componentes(T):
        for idx in _cortar(T, g, 0, vol_min):
            pts = T[idx].reshape(-1, 3); v = _vol(pts)
            if v >= vol_min: pedacos.append((v, pts))
    pedacos.sort(key=lambda t: -t[0])
    saida = []; cascos_feitos = []
    for v, sel in pedacos:
        if len(saida) >= max_cascos: break
        try: h = ConvexHull(sel)
        except Exception: continue
        # pedaco (quase) todo dentro de um casco maior ja feito (detalhe em cima de uma caixa): nao precisa
        dentro = False
        for eq in cascos_feitos:
            d = sel @ eq[:, :3].T + eq[:, 3]   # (pontos, planos): <= 0 dentro
            if (d.max(axis=1) <= 0.002).mean() >= 0.95: dentro = True; break
        if dentro: continue
        cascos_feitos.append(h.equations)
        m = Malha(); cen = sel[h.vertices].mean(axis=0)
        for sx in h.simplices:
            a, b, c = sel[sx[0]], sel[sx[1]], sel[sx[2]]
            m.tri(a, b, c, (120, 120, 120, 0), centro=cen)
        saida.append(m)
    return saida


def gerar(content, portas_por_bloco=None, mesh_jogo=None):
    os.makedirs(os.path.join(content, "Meshes"), exist_ok=True); os.makedirs(os.path.join(content, "Icons"), exist_ok=True)
    gerar_marcadores(content)
    meta_icone = open(META_ICONE_MODELO, encoding="utf-8").read()
    ref = {}
    for nome, mod in modelos(portas_por_bloco).items():
        corpo, pint = mod[0], mod[1]
        extras = mod[2] if len(mod) > 2 else []   # 0.11.0: renderers extras (sufixo, malha, pos, escala) — haste/cabeca do pistao
        for suf, m in [("_Corpo", corpo), ("_Pintavel", pint)] + [("_" + x[0], x[1]) for x in extras]:
            if not m.tris: continue
            n = nome + suf
            open(os.path.join(content, "Meshes", n + ".asset"), "w", newline="\n").write(asset(n, m))
            open(os.path.join(content, "Meshes", n + ".asset.meta"), "w", newline="\n").write(META_MESH % guid(n))
        pl = (portas_por_bloco or {}).get(nome, [])
        # icone: corpo + pintavel + extras na posicao/escala de repouso
        from modelos_v2 import com_extras
        ci = com_extras(mod)
        if len(mod) > 3 and mod[3] is not None: ci = Malha(); ci.tris = list(com_extras(mod).tris) + list(mod[3].tris)   # 0.12.0: previa (malhas do jogo, so na imagem)
        im = icone(ci, pint, pl, plugs=plugues(pl, mesh_jogo), VISTA=VISTA_POR_BLOCO.get(nome, VISTA))
        im.save(os.path.join(content, "Icons", nome + ".png"))
        import re
        open(os.path.join(content, "Icons", nome + ".png.meta"), "w", newline="\n").write(re.sub(r"^guid: [0-9a-f]+", "guid: " + guid(nome + "_Icone"), meta_icone, flags=re.M))
        cols = []
        if nome in HULL_BLOCOS:   # 0.19.2: cascos convexos do modelo (so corpo + pintavel: os extras se mexem)
            for k, cm in enumerate(cascos([corpo, pint])):
                n = nome + "_Col%d" % k
                open(os.path.join(content, "Meshes", n + ".asset"), "w", newline="\n").write(asset(n, cm))
                open(os.path.join(content, "Meshes", n + ".asset.meta"), "w", newline="\n").write(META_MESH % guid(n))
                cols.append(guid(n))
            print("  %s: %d convex collider(s)" % (nome, len(cols)))
        ref[nome] = (guid(nome + "_Corpo"), guid(nome + "_Pintavel") if pint.tris else None, guid(nome + "_Icone"),
                     [(ex[2], ex[4] if len(ex) > 4 else (0, 0, 0), ex[3], guid(nome + "_" + ex[0]), ex[0]) for ex in extras],   # 0.11.1: + sufixo (alavancas); 0.11.2: rotacao de repouso
                     cols)   # 0.19.2: malhas dos colisores convexos
    return ref


if __name__ == "__main__":
    print(gerar(sys.argv[1] if len(sys.argv) > 1 else "Content"))
