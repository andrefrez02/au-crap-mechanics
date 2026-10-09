#!/usr/bin/env python3
"""Gera os prefabs do mod Mecanica (YAML do Unity) a partir de uma especificacao.
Usa so assets do proprio jogo por referencia (meshes/material do decoupler e marcadores de porta) — nada copiado.
Saida: Content/Prefabs/<nome>.prefab (+ .meta quando nao existir)."""
import hashlib, math, os, sys, uuid

G = "06dd3d5416bf43ea368cc39ff96e6d5a"          # assembly de scripts do jogo
S_EPC, S_AREA, S_REND, S_COL = 807379410, -692492370, -2059388358, 2100049158
S_COL_CONVEXO = -1838397108   # DOTSColliderConvex (_mesh posto pelo plugin no configure)
S_PIPE = -906939338           # EPC_SCPipe: EPC + _fuelCapacity/_fuelStabilityThreshold/_fuelStabilitySpeed (o jogo liga nos canos)
S_TANQUE = -1613879900        # EPC_SCTank (so pra conferir o YAML do tanque do jogo)
MAT = "3e3aeaceae3d77442855f522ea98f2ea"
M_CORPO, M_PINTAVEL = "86d9cfe1dd2ba2c49bd20e4604dd33d4", "32075a3c0b563154a98163eb14f20d8c"
M_PORTA = {0: "c0b5977b88cf9914aa6e3f97f3368415", 1: "9ee315e960dc9754d9c7201f234c99ee", 2: "524c7b0ae261a9244b97246a5799c9b4", 3: "2d697d79043ceb1468a4f403ec2484ff",
           4: "3a17f26f6c26b8446b3b3a0e51cdcd7f", 5: "55c4d8170d158c149a9d1c46d03aea9a"}   # 0.15.0: Port Plasma (eixo) / Port Unset (bearing)
ICONE = "2a62eb16b31248646a75f695ccd9c6c6"
M_CANO = "eee58eee2f7506543b6195f2da5f1869"   # malha do SC_PipeQuarter (cano de 0,25 no eixo Z)
# etiqueta (actionable label do jogo, igual a do SC_LabelSmall / joysticks / levers): chapa "Label Small Actionable" (quad
# 0,21 x 0,086 em z -0,042 olhando pra +Z) + RendererText3D (texto que o jogador digita clicando nela; material do texto do jogo)
M_ETIQUETA = "80dc220d70857b34f84abe01cacde181"
MAT_TEXTO = "490dba756006ead4da58f156d65c8274"
MAT_VIDRO = "b7c8a2235f0e12b49a2fb45329a188a4"   # 0.20.0: material do vidro do jogo (SC_GlassA: Glass, layer 15) — barra-guia do pistao
S_ETIQUETA, S_TEXTO3D, S_FILTRO = 2117130703, 1760814809, 833806775
# direcao da porta -> rotacao (graus) do marcador (mesh olha pra +Z)
ROT_DIR = {0: (0, 90, 0), 1: (0, -90, 0), 2: (-90, 0, 0), 3: (90, 0, 0), 4: (0, 0, 0), 5: (0, 180, 0)}
NOME_PORTA = {0: "Port Input", 1: "Port Output", 2: "Port Power", 3: "Port InOut", 4: "Port Shaft", 5: "Port Unset", 10: "Port Unset", 6: "Port Shaft In", 7: "Port Shaft Out", 8: "Port Shaft InOut"}
# 0.16.0 (Andre: "roxo pra saida (#642d9a) e laranja pra entrada (#ebb4a2)"): tipos so do gerador — no prefab viram porta de plasma (4);
# o plugue e' o Port Plasma do jogo recolorido em runtime (marcador "Jogo_Port Plasma~<cor>", JogoMalhas.cs). 8 = metade de cada (transmitter).
CORES_EIXO = {6: "ebb4a2", 7: "642d9a", 8: "mix"}
TIPO_REAL = {6: 4, 7: 4, 8: 4, 10: 5}   # 10 = porta sem tipo (bearing) com plugue misto (Port Unset~mix)
def malha_porta(t):
    if t == 10:
        import gen_meshes; return gen_meshes.guid_marcador("Port Unset~mix")
    if t in CORES_EIXO:
        import gen_meshes; return gen_meshes.guid_marcador("Port Plasma~" + CORES_EIXO[t])
    return M_PORTA[t]


def fid(*partes):
    h = hashlib.sha1(("|".join(str(p) for p in partes)).encode()).digest()
    return int.from_bytes(h[:8], "little") & 0x3FFFFFFFFFFFFFFF or 1


def quat(ex, ey, ez):  # Unity: q = qY * qX * qZ
    def eixo(a, ang):
        h = math.radians(ang) / 2; v = [0.0, 0.0, 0.0]; v[a] = math.sin(h); return (v[0], v[1], v[2], math.cos(h))
    def mul(p, q):
        px, py, pz, pw = p; qx, qy, qz, qw = q
        return (pw*qx + px*qw + py*qz - pz*qy, pw*qy - px*qz + py*qw + pz*qx, pw*qz + px*qy - py*qx + pz*qw, pw*qw - px*qx - py*qy - pz*qz)
    return mul(mul(eixo(1, ey), eixo(0, ex)), eixo(2, ez))


def v3(v): return "{x: %s, y: %s, z: %s}" % tuple(fmt(a) for a in v)
def fmt(a): return ("%.6f" % a).rstrip("0").rstrip(".") if a != int(a) else str(int(a))


def faces(b):
    """6 faces da caixa b (tamanho) centrada na origem: (nome, vertices)."""
    hx, hy, hz = b[0] / 2, b[1] / 2, b[2] / 2
    return [
        ("-Y", [(hx, -hy, -hz), (-hx, -hy, -hz), (-hx, -hy, hz), (hx, -hy, hz)]),
        ("+Y", [(hx, hy, -hz), (-hx, hy, -hz), (-hx, hy, hz), (hx, hy, hz)]),
        ("-X", [(-hx, -hy, -hz), (-hx, hy, -hz), (-hx, hy, hz), (-hx, -hy, hz)]),
        ("+X", [(hx, -hy, -hz), (hx, hy, -hz), (hx, hy, hz), (hx, -hy, hz)]),
        ("-Z", [(-hx, -hy, -hz), (hx, -hy, -hz), (hx, hy, -hz), (-hx, hy, -hz)]),
        ("+Z", [(-hx, -hy, hz), (hx, -hy, hz), (hx, hy, hz), (-hx, hy, hz)]),
    ]


# 0.11.1: o texto e a caixa de clique sao FILHOS da entity da etiqueta (Parent = etiqueta): posicao/rotacao relativas a ela.
# Em 0.11.0 estavam em coordenadas do bloco -> a caixa de clique caia fora do bloco e a etiqueta nao dava pra clicar.
# Valores do SC_LabelSmall do jogo: texto em (0, 0, -0,0387) rot (270, 180, 0); caixa em (0, 0, -0,04) rot 0, 0,21 x 0,085 x 0,025.
ETQ_TEXTO_POS, ETQ_TEXTO_ROT = (0, 0, -0.0387), (270, 180, 0)
ETQ_COL_OFF, ETQ_COL_ESC = (0, 0, -0.04), (0.21, 0.085, 0.025)
ETQ_QUAD = 0.042   # o quad da chapa (malha Label Small Actionable) fica em z -0,042 da origem, olhando +Z


def etiqueta_em(fundo, n, rot, max_len=14):
    """Etiqueta do jogo num bolso: 'fundo' = centro do fundo do bolso (coordenadas do bloco), 'n' = normal da face (pra fora),
    'rot' = Euler (Unity) que leva o quad (olha +Z, cima +Y) pra olhar em n com o texto de pe. A chapa fica 2 mm acima do
    fundo; a origem da entity fica ETQ_QUAD atras dela (o quad esta em z -0,042 da malha)."""
    f = [float(a) for a in fundo]; n = [float(a) for a in n]
    org = tuple(f[i] + n[i] * (0.002 + ETQ_QUAD) for i in range(3))
    return dict(pos_quad=org, rot_quad=tuple(rot), pos_texto=ETQ_TEXTO_POS, rot_texto=ETQ_TEXTO_ROT,
                col_off=ETQ_COL_OFF, col_rot=(0, 0, 0), col_esc=ETQ_COL_ESC, max_len=max_len)


def etiqueta_face_x(y, x_face=0.25, prof=0.012, max_len=14):
    """Etiqueta na face +X do bloco (bolso de 'prof'), centro em y."""
    return etiqueta_em((x_face - prof, y, 0), (1, 0, 0), (0, 90, 0), max_len)


def etiqueta_face_menos_z(x, y, z_face=-0.25, prof=0.012, max_len=10):
    """Etiqueta na face -Z (a frente dos controles: o jogador olha de -Z pra +Z)."""
    return etiqueta_em((x, y, z_face + prof), (0, 0, -1), (0, 180, 0), max_len)


def prefab(nome, bounds, massa, areas, portas, colisor=True, escala_corpo=None, convexo=False, so_pintavel=False, script=S_EPC, extra_raiz="", enfeites=(), cano_icone=None, etiqueta=None, etiquetas=(), alavancas=None, colisor_caixa=None, colisor_esfera=None, pintaveis_extra=(), pintar_extras=(), malhas_jogo=None, textos=(), cor_padrao=0):
    """areas: [(tipo, vertices)]; portas: [(pos, direcao, tipo)]. 0.9.0: malhas e icone proprios (gen_meshes) quando REF tem o bloco.
    etiqueta/etiquetas (0.10.2/0.11.1): dicts de etiqueta_em — poe o actionable label do jogo (chapa + texto editavel) no bloco.
    alavancas (0.11.1): {sufixo do extra: (rot_min, rot_max, capsula)} — o renderer extra com esse sufixo vira uma alavanca do
    jogo (EPC_Actionable_Lever eixo vertical, volta pro meio, + capsula de clique relativa a ela). O plugin le o valor.
    colisor_caixa (0.11.1): (offset, escala) da caixa de colisao, quando nao e' o bloco inteiro."""
    PORTAS_ICONE[nome] = list(portas) + ([(cano_icone, 0, 9)] if cano_icone else [])
    AREAS_COLA[nome] = [vs for _, vs in areas]   # 0.22.5: entram na casca do _boundsCollider (a face de cola fica na borda)
    proprio = REF.get(nome)
    raiz_go, raiz_tr, raiz_mb = fid(nome, "go"), fid(nome, "tr"), fid(nome, "mb")
    filhos = []   # (go, tr, mb, nome, layer, texto_mb, pos, rot_euler, escala)
    esc = escala_corpo or tuple(b / 0.5 for b in bounds)

    def filho(tag, nomego, layer, script, corpo, pos=(0, 0, 0), rot=(0, 0, 0), escala=(1, 1, 1)):
        filhos.append((fid(nome, tag, "go"), fid(nome, tag, "tr"), fid(nome, tag, "mb"), nomego, layer, script, corpo, pos, rot, escala))
        return filhos[-1][2]

    for i, (tipo, vs) in enumerate(areas):
        corpo = "  _type: %d\n  _material: 4\n  _vertices:\n" % tipo + "".join("  - x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in v) for v in vs)
        filho("area%d" % i, "JointsArea" + ("" if i == 0 else " (%d)" % i), 7, S_AREA, corpo)

    def rend(pos, rot, escala, malha):
        return ("  _position:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in pos) +
                "  _rotation:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in rot) +
                "  _scale:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in escala) +
                "  _mesh: {fileID: 4300000, guid: %s, type: 2}\n  _submeshID: 0\n" % malha +
                "  _material: {fileID: 2100000, guid: %s, type: 2}\n  _layer: 0\n  _color: {r: 0, g: 0, b: 0, a: 0}\n" % MAT +
                "  _specular:\n    _strength: 0\n    _smoothness: 0\n  _hasHeat: 1\n  _spaceshipColor: 0\n")

    for i, (pos, d, t) in enumerate(portas):
        r_p = rend(pos, ROT_DIR[d], (1, 1, 1), malha_porta(t))
        # 0.22.2: porta de passagem (tipo 5) = plugue Port Unset do jogo; o plugin troca a malha pela do cabo ligado (Anel.cs), como o Frame With Ports
        filho("porta%d" % i, NOME_PORTA[t] + ("" if i == 0 else " %d" % i), 0, S_REND, r_p, pos, ROT_DIR[d])
    alavancas = alavancas or {}; alavs = []   # (go, tr, rend, lev, caps, filt, nome, pos, rot, malha, tipo, rot_min, rot_max, capsula)
    for i, ex in enumerate(list(enfeites) + (list(proprio[3]) if proprio and len(proprio) > 3 else [])):   # malhas extras (0.11.0: haste/cabeca do pistao)
        pos, rot, escala, malha = ex[:4]; suf = ex[4] if len(ex) > 4 else None
        if suf in alavancas:
            tipo, rmin, rmax, caps = alavancas[suf]
            alavs.append((fid(nome, suf, "go"), fid(nome, suf, "tr"), fid(nome, suf, "rend"), fid(nome, suf, "lev"), fid(nome, suf, "caps"), fid(nome, suf, "filt"),
                          "Actionable Lever" + ("" if len(alavs) == 0 else " %d" % len(alavs)), pos, rot, malha, tipo, rmin, rmax, caps))
            continue
        r_ex = rend(pos, rot, escala, malha)
        if suf == "Guia":   # 0.20.0: barra-guia do pistao = material de VIDRO do jogo (translucido), layer do vidro, cor alpha 1 (tingivel pelo plugin)
            r_ex = r_ex.replace("_material: {fileID: 2100000, guid: %s, type: 2}\n  _layer: 0\n  _color: {r: 0, g: 0, b: 0, a: 0}\n" % MAT,
                                "_material: {fileID: 2100000, guid: %s, type: 2}\n  _layer: 15\n  _color: {r: 0.3, g: 0.9, b: 0.4, a: 1}\n" % MAT_VIDRO, 1)
        if suf == "led":
            r_ex = r_ex.replace("  _specular:\n    _strength: 0\n", "  _specular:\n    _strength: %s\n" % LED_MARCA, 1)   # 0.19.2: marca do LED (o plugin acha pelo specular)
            # 0.19.8: igual o indicador do transmitter do jogo — alpha 1 (com alpha 0 o renderer nasce SEM CRPRendererMemory_Color e o plugin
            # nao tem onde pintar) e o material do indicador (CRPUnlit: o LED brilha de verdade)
            r_ex = r_ex.replace("  _color: {r: 0, g: 0, b: 0, a: 0}\n", "  _color: {r: 0, g: 0, b: 0, a: 1}\n", 1).replace("_material: {fileID: 2100000, guid: %s, type: 2}" % MAT, "_material: {fileID: 2100000, guid: %s, type: 2}" % MAT_LED, 1)
        mb_ex = filho("enfeite%d" % i, "Renderer Extra" + ("" if i == 0 else " %d" % i), 0, S_REND, r_ex, pos, rot, escala)
        if suf in pintar_extras: pintaveis_extra = list(pintaveis_extra) + [mb_ex]   # 0.12.1: extra pintavel (placas do turret)
    if malhas_jogo:   # 0.18.0: corpo/pintavel = malhas do jogo (marcadores religados em runtime) — o modelo proprio fica so no icone
        um = (1, 1, 1)   # 0.22.2: corpo None = malha propria (LRT: antena + globo) com a base pintavel do jogo
        filho("corpo", "Renderer", 0, S_REND, rend((0, 0, 0), (0, 0, 0), um, malhas_jogo[0] or (proprio[0] if proprio else M_CORPO)), escala=um)
        pint = filho("pintavel", "Paintable", 0, S_REND, rend((0, 0, 0), (0, 0, 0), um, malhas_jogo[1]), escala=um)
    elif proprio:
        um = (1, 1, 1)
        filho("corpo", "Renderer", 0, S_REND, rend((0, 0, 0), (0, 0, 0), um, proprio[0]), escala=um)
        pint = filho("pintavel", "Paintable", 0, S_REND, rend((0, 0, 0), (0, 0, 0), um, proprio[1] or proprio[0]), escala=um)
    else:
        if not so_pintavel:
            filho("corpo", "Renderer", 0, S_REND, rend((0, 0, 0), (0, 0, 0), esc, M_CORPO), escala=esc)
        pint = filho("pintavel", "Paintable", 0, S_REND, rend((0, 0, 0), (0, 0, 0), esc, M_PINTAVEL), escala=esc)
    for i, tx in enumerate(textos):   # 0.18.2: texto 3D do jogo so de mostrar (o plugin escreve) — numero do canal do Long Range Transmitter
        if tx.get("pintavel"): pintaveis_extra = list(pintaveis_extra) + [fid(nome, "texto%d" % i, "mb")]   # 0.22.2: igual o numero do canal do transmitter do jogo
        filho("texto%d" % i, tx.get("nome", "RendererText3D"), 7, S_TEXTO3D,
              "  _position:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in tx["pos"]) +
              "  _rotation:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in tx.get("rot", (0, 0, 0))) +
              "  _scale:\n    x: 1\n    y: 1\n    z: 1\n  _text: '%s'\n  _alignment: %d\n  _verticalAlignment: %d\n  _monospace: %d\n  _textScale: %s\n  _maxWidth: %s\n" % (
                  tx.get("texto", ""), tx.get("alin", 1), tx.get("valin", 1), tx.get("mono", 0), fmt(tx.get("escala", 0.1)), fmt(tx.get("larg", 0))) +
              "  _material: {fileID: 2100000, guid: %s, type: 2}\n  _layer: 7\n  _color: {r: 0, g: 0, b: 0, a: 0}\n  _specular:\n    _strength: 0\n    _smoothness: 0\n  _hasHeat: 1\n" % MAT_TEXTO)
    cols = []
    if colisor_esfera:   # 0.12.0 (turret seat): colisor esfera (DOTSColliderSphere) — girando em qualquer eixo nao entra no bloco de baixo
        cols.append(filho("colisor", "Collider", 7, S_COL_ESFERA, "  _center:\n    x: %s\n    y: %s\n    z: %s\n  _radius: %s\n" % (tuple(fmt(a) for a in colisor_esfera[0]) + (fmt(colisor_esfera[1]),))))
    elif colisor and convexo:
        cols.append(filho("colisor", "Collider", 7, S_COL_CONVEXO, "  _mesh: {fileID: 0}\n  _offset:\n    x: 0\n    y: 0\n    z: 0\n  _rotation:\n    x: 0\n    y: 0\n    z: 0\n" +
                          "  _scale:\n    x: 1\n    y: 1\n    z: 1\n  _bevelRadius: 0.02\n"))
    elif colisor and proprio and len(proprio) > 4 and proprio[4]:   # 0.19.3: os cascos valem mais que as caixas a mao
        # 0.19.2: cascos convexos gerados do modelo (gen_meshes.cascos): um DOTSColliderConvex por casco
        for k, g in enumerate(proprio[4]):
            cols.append(filho("colisor%d" % k, "Collider" + ("" if k == 0 else " %d" % k), 7, S_COL_CONVEXO, "  _mesh: {fileID: 4300000, guid: %s, type: 2}\n  _offset:\n    x: 0\n    y: 0\n    z: 0\n  _rotation:\n    x: 0\n    y: 0\n    z: 0\n" % g +
                              "  _scale:\n    x: 1\n    y: 1\n    z: 1\n  _bevelRadius: 0.006\n"))
    elif colisor:
        # 0.11.2: colisor_caixa pode ser uma lista de (offset, escala) — varias caixas (pedais: caixa das saidas + travessa da frente)
        caixas = colisor_caixa if colisor_caixa else [((0, 0, 0), bounds)]
        if caixas and not isinstance(caixas[0][0], (tuple, list)): caixas = [caixas]
        for k, (c_off, c_esc) in enumerate(caixas):
            cols.append(filho("colisor%d" % k, "Collider" + ("" if k == 0 else " %d" % k), 7, S_COL, "  _offset:\n    x: %s\n    y: %s\n    z: %s\n  _rotation:\n    x: 0\n    y: 0\n    z: 0\n" % tuple(fmt(a) for a in c_off) +
                              "  _scale:\n    x: %s\n    y: %s\n    z: %s\n  _bevelRadius: 0.025\n" % tuple(fmt(a) for a in c_esc)))

    out = ["%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:"]

    def go(g, comps, layer, n):
        out.append("--- !u!1 &%d" % g)
        out.append("GameObject:\n  serializedVersion: 6\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_Component:\n" +
                   "".join("  - component: {fileID: %d}\n" % c for c in comps) +
                   "  m_Layer: %d\n  m_Name: %s\n  m_TagString: Untagged\n  m_Icon: {fileID: 0}\n  m_NavMeshLayer: 0\n  m_StaticEditorFlags: 0\n  m_IsActive: 1" % (layer, n))

    def tr(t, g, pai, filhos_tr, pos=(0, 0, 0), rot=(0, 0, 0), escala=(1, 1, 1)):
        q = quat(*rot)
        out.append("--- !u!4 &%d" % t)
        out.append("Transform:\n  serializedVersion: 2\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: %d}\n" % g +
                   "  m_LocalRotation: {x: %s, y: %s, z: %s, w: %s}\n" % tuple(fmt(round(a, 7)) for a in q) +
                   "  m_LocalPosition: %s\n  m_LocalScale: %s\n  m_ConstrainProportionsScale: 0\n" % (v3(pos), v3(escala)) +
                   ("  m_Children: []\n" if not filhos_tr else "  m_Children:\n" + "".join("  - {fileID: %d}\n" % f for f in filhos_tr)) +
                   "  m_Father: {fileID: %d}\n  m_LocalEulerAnglesHint: %s" % (pai, v3(rot)))

    def mb(m, g, script, corpo):
        out.append("--- !u!114 &%d" % m)
        out.append("MonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: %d}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n" % g +
                   "  m_Script: {fileID: %d, guid: %s, type: 3}\n  m_Name: \n  m_EditorClassIdentifier: \n" % (script, G) + corpo.rstrip("\n"))

    # etiqueta: GameObject "Actionable Label" (EPC_ActionableLabel + EPC_Renderer da chapa + caixa de clique + filtro) com o
    # filho "RendererText3D"
    lista_etq = ([etiqueta] if etiqueta else []) + list(etiquetas)
    ets = []
    for k, e in enumerate(lista_etq):
        tag = "etq" if k == 0 else "etq%d" % k
        ets.append(dict(e=e, go=fid(nome, tag, "go"), tr=fid(nome, tag, "tr"), act=fid(nome, tag, "act"), rend=fid(nome, tag, "rend"),
                        col=fid(nome, tag, "col"), filt=fid(nome, tag, "filt"), tgo=fid(nome, tag, "tgo"), ttr=fid(nome, tag, "ttr"), txt=fid(nome, tag, "txt"),
                        nomego="Actionable Label" + ("" if k == 0 else " %d" % k)))
    for et in ets: pintaveis_extra = list(pintaveis_extra) + [et["act"], et["txt"]]   # 0.22.1: igual ao transmitter do jogo (o label pinta a chapa, o texto e' pintavel)
    go(raiz_go, [raiz_tr, raiz_mb], 7, nome)
    for f in filhos: go(f[0], [f[1], f[2]], f[4], f[3])
    for a in alavs: go(a[0], [a[1], a[2], a[3], a[4], a[5]], 0, a[6])
    for et in ets:
        go(et["go"], [et["tr"], et["act"], et["rend"], et["col"], et["filt"]], 7, et["nomego"])
        go(et["tgo"], [et["ttr"], et["txt"]], 7, "RendererText3D")
    tr(raiz_tr, raiz_go, 0, [f[1] for f in filhos] + [a[1] for a in alavs] + [et["tr"] for et in ets])
    raiz = ("  _mirroredVersion: {fileID: 0}\n  _bounds:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in bounds) +
            "  _mass: %s\n  _oceanFloatingSetup:\n    _linearSlowness: 2.5\n    _buoyancy: 0.7\n  _maxTemperature: 2\n" % fmt(massa) +
            ("  _colliders:\n" + "".join("  - {fileID: %d}\n" % c for c in cols) if cols else "  _colliders: []\n") +
            "  _colliderAirResistant: 1\n  _scGroup: 0\n  _scSecondaryGroup: 0\n  _availableAmount: 500\n  _inventoryDependency: []\n  _boundsCollider: {fileID: 0}\n" +
            "  _defaultPaintableColor: %d\n  _paintableRenderers:\n  - {fileID: %d}\n" % (cor_padrao, pint) + "".join("  - {fileID: %d}\n" % x for x in pintaveis_extra) + "  _collisionSoundMaterial: 1\n" +
            "  _soundObstacleMin:\n    x: 0\n    y: 0\n    z: 0\n  _soundObstacleMax:\n    x: 0\n    y: 0\n    z: 0\n" +
            "  _electricPorts:\n" + "".join("  - _position:\n      x: %s\n      y: %s\n      z: %s\n    _direction: %d\n    _type: %d\n" % (fmt(p[0]), fmt(p[1]), fmt(p[2]), d, TIPO_REAL.get(t, t)) for p, d, t in portas) +
            "  _iconTexture2D: {fileID: 2800000, guid: %s, type: 3}\n" % (proprio[2] if proprio else ICONE) +
            "  _uiPreviewBounds:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in bounds) +
            "  _categories: 512\n  _customProperties: []\n  _propertiesMaterial: 255\n" + extra_raiz)
    mb(raiz_mb, raiz_go, script, raiz)
    for f in filhos:
        tr(f[1], f[0], raiz_tr, [], f[7], f[8], f[9])
        mb(f[2], f[0], f[5], f[6])
    for a in alavs:   # alavanca: renderer da malha no pivo + script da alavanca + capsula de clique + filtro
        _, _, a_rend, a_lev, a_caps, a_filt, _, pos, rot, malha, tipo, rmin, rmax, caps = a
        tr(a[1], a[0], raiz_tr, [], pos, rot)
        mb(a_rend, a[0], S_REND, rend(pos, rot, (1, 1, 1), malha))
        if tipo == "botao":
            # 0.12.2: botao do jogo (EPC_Actionable_Button: 1 enquanto aperta, volta sozinho); rmax = rotacao apertado; caps = caixa de clique
            # 0.12.3: o jogo escreve o LocalTransform do botao = pose On/Off -> a posicao tem que ser a do pivo (0.12.2 punha 0: os
            # dois pedais iam pro centro do bloco e ficavam no mesmo x — o plugin trocava esquerdo/direito)
            pp = tuple(fmt(x) for x in pos)
            mb(a_lev, a[0], S_BOTAO, "  _lookAt: 0\n  _buttonTransformOnPos:\n    x: %s\n    y: %s\n    z: %s\n" % pp + "  _buttonTransformOnRot:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(x) for x in rmax) +
               "  _buttonTransformOffPos:\n    x: %s\n    y: %s\n    z: %s\n" % pp + "  _buttonTransformOffRot:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(x) for x in rmin))
            c_off, c_rot, c_esc = caps
            mb(a_caps, a[0], S_COL, "  _offset:\n    x: %s\n    y: %s\n    z: %s\n  _rotation:\n    x: %s\n    y: %s\n    z: %s\n  _scale:\n    x: %s\n    y: %s\n    z: %s\n  _bevelRadius: 0\n" % (tuple(fmt(x) for x in c_off) + tuple(fmt(x) for x in c_rot) + tuple(fmt(x) for x in c_esc)))
            mb(a_filt, a[0], S_FILTRO, "  _collisionFilterType: 0\n  _materialType: 0\n")
            continue
        if tipo == "switch":
            # 0.23.0: ActionableToggle do jogo (mesmo script do Input Switch do Datameter): duas poses da haste; o valor (0/1) sai
            # no ActionableData da entity, lido pelo plugin (Juntas.SwitchAngulo). caps = (offset, rotacao, escala) da caixa de clique
            pp = tuple(fmt(x) for x in pos)
            mb(a_lev, a[0], S_TOGGLE, "  _drawUIValue: 2\n  _buttonTransformOnPos:\n    x: %s\n    y: %s\n    z: %s\n" % pp +
               "  _buttonTransformOnRot:\n    x: %s\n    y: %s\n    z: %s\n  _meshOn: {fileID: 0}\n" % tuple(fmt(x) for x in rmax) +
               "  _buttonTransformOffPos:\n    x: %s\n    y: %s\n    z: %s\n" % pp +
               "  _buttonTransformOffRot:\n    x: %s\n    y: %s\n    z: %s\n  _meshOff: {fileID: 0}\n" % tuple(fmt(x) for x in rmin) +
               "  _defaultValue: 1\n  _canGoOn: 1\n  _canGoOff: 1\n")
            c_off, c_rot, c_esc = caps
            mb(a_caps, a[0], S_COL, "  _offset:\n    x: %s\n    y: %s\n    z: %s\n  _rotation:\n    x: %s\n    y: %s\n    z: %s\n  _scale:\n    x: %s\n    y: %s\n    z: %s\n  _bevelRadius: 0\n" % (tuple(fmt(x) for x in c_off) + tuple(fmt(x) for x in c_rot) + tuple(fmt(x) for x in c_esc)))
            mb(a_filt, a[0], S_FILTRO, "  _collisionFilterType: 0\n  _materialType: 0\n")
            continue
        if tipo == "knob":
            # 0.17.0: knob do jogo (EPC_Actionable_Knob, igual os da Camera Overlay): _roundingIntervals = numero de posicoes; gira em Y
            # de _rotationMin a _rotationMax; clique = esfera (caps = raio). 0.19.2: caps pode ser (raio, intervalos, valor padrao)
            # 0.19.3: 4o item = centro da esfera de clique (knob num painel vertical: ao longo de Z)
            raio, interv, padrao, centro = (caps, 3, 0, (0, 0.007, 0)) if not isinstance(caps, tuple) else (caps + (3, 0, (0, 0.007, 0)))[:4]
            mb(a_lev, a[0], S_KNOB, "  _defaultValue: %s\n  _sensitivity:\n    x: 0.07\n    y: 0.07\n  _roundingIntervals: %d\n  _loop: 0\n" % (fmt(padrao), interv) +
               "  _rotationMin:\n    x: %s\n    y: %s\n    z: %s\n  _rotationMax:\n    x: %s\n    y: %s\n    z: %s\n  _drawUIValue: 2\n" % (tuple(fmt(x) for x in rmin) + tuple(fmt(x) for x in rmax)))
            mb(a_caps, a[0], S_COL_ESFERA, "  _center:\n    x: %s\n    y: %s\n    z: %s\n  _radius: %s\n" % (tuple(fmt(x) for x in centro) + (fmt(raio),)))
            mb(a_filt, a[0], S_FILTRO, "  _collisionFilterType: 0\n  _materialType: 0\n")
            continue
        if tipo == "pisar":
            # EPC_Actionable_DoubleLever no modo simples = alavanca do jogo de UM sentido (0..1: _rotationMin = repouso, _rotationMax =
            # pisado). O plugin poe o ActionableJoystickReturnToMid nela (volta pro 0 sozinha) — 0.11.2, pedais
            mb(a_lev, a[0], S_DOUBLE, "  _sensitivity: 0.07\n  _defaultValue: 0\n  _isVertical: 1\n  _isDoubleMode: 0\n" +
               "  _rotationMin:\n    x: %s\n    y: %s\n    z: %s\n  _rotationMax:\n    x: %s\n    y: %s\n    z: %s\n" % (tuple(fmt(x) for x in rmin) + tuple(fmt(x) for x in rmax)) +
               "  _position:\n    x: %s\n    y: %s\n    z: %s\n  _zeroOffset:\n    x: 0\n    y: 0\n    z: 0\n  _drawUIValue: 1\n" % tuple(fmt(x) for x in pos))
        else:
            # EPC_Actionable_Lever (joystick de 1 eixo, -1..1, volta pro meio): "ver" = eixo vertical (mouse pra frente/tras), "hor" = horizontal
            hor = tipo == "hor"
            zero = (0, 0, 0)
            eixos = (("HorMin", pos if hor else zero, rmin if hor else zero), ("HorMax", pos if hor else zero, rmax if hor else zero),
                     ("VerMin", zero if hor else pos, zero if hor else rmin), ("VerMax", zero if hor else pos, zero if hor else rmax),
                     ("ZMin", zero, zero), ("ZMax", zero, zero))
            mb(a_lev, a[0], S_ALAVANCA, "  _sensitivity: 0.07\n  _usedValues: 1\n  _roundingIntervals: 0\n  _defaultValue: 0\n" +
               "".join("  _position%s:\n    x: %s\n    y: %s\n    z: %s\n  _rotation%s:\n    x: %s\n    y: %s\n    z: %s\n" % ((k,) + tuple(fmt(x) for x in p) + (k,) + tuple(fmt(x) for x in r)) for k, p, r in eixos) +
               "  _rotationOrder: 4\n  _returnToMid: 1\n  _usedAxisX: %d\n  _usedAxisY: %d\n  _usedAxisZ: 0\n  _drawUIValue: 1\n" % (1 if hor else 0, 0 if hor else 1))
        mb(a_caps, a[0], S_CAPSULA, "  _vertexA:\n    x: %s\n    y: %s\n    z: %s\n  _vertexB:\n    x: %s\n    y: %s\n    z: %s\n  _radius: %s\n" % (tuple(fmt(x) for x in caps[0]) + tuple(fmt(x) for x in caps[1]) + (fmt(caps[2]),)))
        mb(a_filt, a[0], S_FILTRO, "  _collisionFilterType: 0\n  _materialType: 0\n")
    for et in ets:
        e = et["e"]
        tr(et["tr"], et["go"], raiz_tr, [et["ttr"]])
        mb(et["act"], et["go"], S_ETIQUETA, "  _rendererText3D: {fileID: %d}\n  _maxLength: %d\n  _inputType: 0\n  _enableRotation: 1\n" % (et["txt"], e.get("max_len", 14)))
        mb(et["rend"], et["go"], S_REND, rend(e["pos_quad"], e["rot_quad"], (1, 1, 1), e.get("malha", M_ETIQUETA)))
        mb(et["col"], et["go"], S_COL, "  _offset:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in e["col_off"]) +
           "  _rotation:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in e["col_rot"]) +
           "  _scale:\n    x: %s\n    y: %s\n    z: %s\n  _bevelRadius: 0\n" % tuple(fmt(a) for a in e["col_esc"]))
        mb(et["filt"], et["go"], S_FILTRO, "  _collisionFilterType: 0\n  _materialType: 0\n")
        tr(et["ttr"], et["tgo"], et["tr"], [])
        mb(et["txt"], et["tgo"], S_TEXTO3D, "  _position:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in e["pos_texto"]) +
           "  _rotation:\n    x: %s\n    y: %s\n    z: %s\n" % tuple(fmt(a) for a in e["rot_texto"]) +
           "  _scale:\n    x: 1\n    y: 1\n    z: 1\n  _text: \n  _alignment: 1\n  _verticalAlignment: 1\n  _monospace: 0\n  _textScale: 0.085\n  _maxWidth: 0.19\n" +
           "  _material: {fileID: 2100000, guid: %s, type: 2}\n  _layer: 7\n  _color: {r: 0, g: 0, b: 0, a: 0}\n  _specular:\n    _strength: 0\n    _smoothness: 0\n  _hasHeat: 1\n" % MAT_TEXTO)
    return "\n".join(out) + "\n"


# (sufixo, tamanho da Rechargeable Battery equivalente, massa)
# (sufixo, caixa do eletrico, caixa do combustivel, massa eletrico, massa combustivel). 0.9.0 (form6): cubos 0,5 / 1 / 1,5;
# 0.10.2 (form9): P a combustivel = 4x8x4 casas (0,5 x 1 x 0,5) com as portas num grupo 2x2 ao lado do cano; eletrico P = cubo 0,5
MOTORES = [("P", (0.5, 0.5, 0.5), (0.5, 1, 0.5), 160, 200), ("M", (1, 1, 1), (1, 1, 1), 730, 730), ("G", (1.5, 1.5, 1.5), (1.5, 1.5, 1.5), 1450, 1450)]
REF = {}            # nome -> (guid malha fixa, guid malha pintavel, guid icone), preenchido pelo gen_meshes
PORTAS_ICONE = {}   # nome -> portas (+ boca de cano) pro icone
AREAS_COLA = {}     # 0.22.5: nome -> poligonos das areas de cola (JointsArea)
# 0.22.5 (Andre: "ve como o jogo faz ... frame curveds tem essas 'orelhas' no volume"): EPC_SpaceshipComponent._boundsCollider = malha;
# o jogo (EPC_SpaceshipComponent.CreateBaseColliderBAR) faz a CASCA CONVEXA dela (Core.getOrCreateConvexCollider) e usa como volume
# ocupado na garagem (GarageGrabber.CanMountComponentToGaragePos testa contra esses colisores); sem malha = a caixa inteira do _bounds.
# Aqui: casca convexa do modelo (corpo + pintavel) + as areas de cola, pras pecas redondas/irregulares liberarem os cantos.
# (motores ficam de fora por enquanto: a cola deles e' as 6 faces inteiras, entao a casca vira a propria caixa — precisa decidir onde a cola encolhe)
BOUNDS_BLOCOS = ["SC_MotorP", "SC_MotorM", "SC_MotorG", "SC_MotorCombP", "SC_MotorCombM", "SC_MotorCombG", "SC_TurretSeat", "SC_TanqueVerde", "SC_TanqueAzul",
                 "SC_RodaSportsP", "SC_RodaSportsM", "SC_RodaSportsG", "SC_RodaOffroadP", "SC_RodaOffroadM", "SC_RodaOffroadG"]   # 0.22.6: + motores (cola so embaixo) e turret + ["SC_RodaSportsP", "SC_RodaSportsM", "SC_RodaSportsG", "SC_RodaOffroadP", "SC_RodaOffroadM", "SC_RodaOffroadG"]

# rodas (Andre, form3 + 25/09): sports = leve, atrito 1,5; offroad = um pouco mais pesada, atrito 2,2 (420 kg derrubava o bearing); P/M/G = 1 / 1,5 / 2 m de diametro, 0,5 m de largura
RODAS = [("SC_RodaSportsP", 1.0, 40), ("SC_RodaSportsM", 1.5, 80), ("SC_RodaSportsG", 2.0, 140),
         ("SC_RodaOffroadP", 1.0, 60), ("SC_RodaOffroadM", 1.5, 120), ("SC_RodaOffroadG", 2.0, 200)]


def blocos():
    r = {}
    # pistoes: quarto de bloco, face de baixo cola (Normal), face de cima solta (Decoupler); entrada + energia na face +X
    # 0.22.5 (Andre: "pode remover todos itens antigos que sairam do inventario"): os pistoes 2/4/8 e o Piston End cubo sairam
    fs = dict(faces((0.5, 0.5, 0.5)))
    # 0.20.0 (Andre: "remove os tiers do pistao, deixa so um pistao com nivel 'infinito'"): pistao unico (ate 8 m; com o Piston End
    # na frente o curso e' a distancia ate ele) + Mini Piston (0,25 m, ate 4 m). Os 2/4/8 continuam registrados (BPs antigos), escondidos
    # do inventario (Espelhos.cs). Renderer extra "Guia" (vidro do jogo): o plugin estica com o H da garagem.
    # 0.22.0 (Andre: anel coletor): portas de PASSAGEM sem tipo (tipo 10) na face -Z (oposta ao knob) — 4 no bloco de 0,5 (fileira de
    # baixo), 1 no mini (celula +X/-Y) — sempre as ULTIMAS da lista (o plugin: indice >= total - Anel.NLaterais). O anel (SC_SlipRing /
    # SC_MiniSlipRing) na peca que se move tem as mesmas portas; o plugin liga porta i <-> porta i (Anel.cs).
    PASSAGEM_4 = [((x, -0.1875, -0.1875), 5, 5) for x in (-0.1875, -0.0625, 0.0625, 0.1875)]   # 6,25 cm pra dentro da face (centro da celula), como todas as portas. 0.22.1 (Andre): tipo 5 = plugue do Frame Quarter With Ports (cinza sem cabo, cor do cabo quando conecta)
    PASSAGEM_1 = [((0.0625, -0.0625, -0.0625), 5, 5)]
    # 0.22.1 (Andre: portas em L na mesma celula nao conectam): as de passagem do pistao vao pra face -X (oposta a entrada/energia)
    PASSAGEM_4X = [((-0.1875, -0.1875, z), 1, 5) for z in (-0.1875, -0.0625, 0.0625, 0.1875)]
    PASSAGEM_1X = [((-0.0625, -0.0625, 0.0625), 1, 5)]
    # 0.23.0 (Andre: "ele quer saber a rotacao atual do bearing pra fazer um calculo ... adiciona uma saida de dados"): saida do
    # angulo, SEMPRE a ULTIMA da lista (as de passagem continuam no mesmo indice dos BPs antigos; Anel.NSaidas desconta esta).
    # Bearing: celula +X da fileira de baixo da face do knob (+Z). Mini: as duas celulas livres sao do lado -X, entao vai na face -Z,
    # ao lado da de passagem.
    SAIDA_ANG = [((0.1875, -0.1875, 0.1875), 4, 1)]
    SAIDA_ANG_M = [((-0.0625, -0.0625, -0.0625), 5, 1)]
    SW_BEARING = {"switch": ("switch", (20, 0, 0), (-20, 0, 0), ((0, 0, 0.012), (0, 0, 0), (0.05, 0.05, 0.03)))}
    SW_MINI = {"switch": ("switch", (20, 0, 0), (-20, 0, 0), ((0, 0, 0.010), (0, 0, 0), (0.043, 0.043, 0.026)))}
    r["SC_Pistao"] = prefab("SC_Pistao", (0.5, 0.5, 0.5), 80, [(0, fs["-Y"]), (3, fs["+Y"])],
                            [((0.1875, -0.1875, 0.1875), 0, 0), ((0.1875, -0.1875, -0.1875), 0, 2)] + PASSAGEM_4X,
                            alavancas={"Knob": ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, 1.0, (0, 0, 0.007)))})
    fm = dict(faces((0.25, 0.25, 0.25)))
    r["SC_MiniPistao"] = prefab("SC_MiniPistao", (0.25, 0.25, 0.25), 20, [(0, fm["-Y"]), (3, fm["+Y"])],
                                [((0.0625, -0.0625, 0.0625), 0, 0), ((0.0625, -0.0625, -0.0625), 0, 2)] + PASSAGEM_1X,
                                alavancas={"Knob": ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, 1.0, (0, 0, 0.007)))})
    # 0.22.4 (Andre: "faz igual os blocos de logica que e' um 2x1x2"): Slip Ring = placa 0,5 x 0,125 x 0,5 (igual o mini painel solar),
    # Mini Slip Ring = placa 0,25 x 0,125 x 0,25; colam nas 6 faces; 4 portas de passagem, 2 em cada lateral oposta (1-2 na -X, 3-4 na +X).
    # Qualquer anel funciona em qualquer junta. Piston End em placa nos mesmos 2 tamanhos (o SC_PistaoPonta cubo continua pros BPs antigos,
    # escondido do inventario).
    for nome, w, massa in (("SC_SlipRing", 0.5, 4), ("SC_MiniSlipRing", 0.25, 1.5)):
        bb = (w, 0.125, w); cx = w / 2 - 0.0625
        r[nome] = prefab(nome, bb, massa, [(0, v) for _, v in faces(bb)],
                         [((-cx, 0, z), 1, 5) for z in (-0.0625, 0.0625)] + [((cx, 0, z), 0, 5) for z in (-0.0625, 0.0625)])
    for nome, w, massa in (("SC_PistaoFim", 0.5, 5), ("SC_MiniPistaoFim", 0.25, 2)):
        bb = (w, 0.125, w)
        r[nome] = prefab(nome, bb, massa, [(0, v) for _, v in faces(bb)], [])
    # 0.20.0: Mini Bearing / Mini Gyro Bearing (0,25 m): uma entrada so, na face de baixo. 0.20.1 (Andre: "entrada do mini bearing ta no
    # lugar errado ... joga pra uma das pontas"): o centro do bloco cai na LINHA da grade de portas, nao numa celula — a entrada fica na
    # celula +X/+Z (a versao M espelha pra -X)
    r["SC_MiniBearing"] = prefab("SC_MiniBearing", (0.25, 0.25, 0.25), 10, [(0, fm["-Y"]), (3, fm["+Y"])], [((0.0625, -0.0625, 0.0625), 3, 10)] + PASSAGEM_1 + SAIDA_ANG_M,
                                 alavancas=dict({"Knob": ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, 0.1, (0, 0, 0.007)))}, **{"Switch": SW_MINI["switch"]}))
    r["SC_MiniGyroBearing"] = prefab("SC_MiniGyroBearing", (0.25, 0.25, 0.25), 10, [(0, fm["-Y"]), (3, fm["+Y"])], [((0.0625, -0.0625, 0.0625), 3, 10)] + PASSAGEM_1 + SAIDA_ANG_M,
                                     alavancas=dict({"Knob": ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, 0.1, (0, 0, 0.007)))}, **{"Switch": SW_MINI["switch"]}))
    # bearing: quarter 0,5 m (a grade dos frames anda de 0,5 em 0,5). Face de baixo cola (Normal), face de cima solta
    # (Decoupler) e gira em torno do +Y. 4 entradas nos 4 quadradinhos do centro da face de baixo, olhando pra baixo
    # (casam com as portas do Frame Quarter With Ports).
    fs = dict(faces((0.5, 0.5, 0.5)))
    r["SC_Bearing"] = prefab("SC_Bearing", (0.5, 0.5, 0.5), 40, [(0, fs["-Y"]), (3, fs["+Y"])],
                             [((x, -0.1875, z), 3, 10) for x, z in ((-0.0625, -0.0625), (0.0625, -0.0625), (0.0625, 0.0625), (-0.0625, 0.0625))] + PASSAGEM_4 + SAIDA_ANG,
                             alavancas=dict({"Knob": ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, 0.1, (0, 0, 0.007)))}, **{"Switch": SW_BEARING["switch"]}))   # 0.19.2: knob do angulo do servo (continuo, padrao 0,1 = 45 graus); 0.19.3: gira em Z (painel +Z)   # 0.15.0: portas SEM TIPO (shaft = motor, dados = servo); 0.17.1: plugue meio laranja meio roxo (10)
    # 0.19.14: Gyro Bearing — mesmo bloco, o servo mira sozinho pra manter o lado de cima nivelado (Giro.cs); sinal = deslocamento
    r["SC_GyroBearing"] = prefab("SC_GyroBearing", (0.5, 0.5, 0.5), 40, [(0, fs["-Y"]), (3, fs["+Y"])],
                                 [((x, -0.1875, z), 3, 10) for x, z in ((-0.0625, -0.0625), (0.0625, -0.0625), (0.0625, 0.0625), (-0.0625, 0.0625))] + PASSAGEM_4 + SAIDA_ANG,
                                 alavancas=dict({"Knob": ("knob", (0, 0, -150), (0, 0, 150), (0.028, 0, 0.1, (0, 0, 0.007)))}, **{"Switch": SW_BEARING["switch"]}))
    # motores: tamanho das Rechargeable Batteries do mesmo nivel (Andre, 25/09: os do thruster ficaram exagerados);
    # massa, consumo e forca continuam os dos Electric Thrusters. Cola nas 6 faces. Face +X, fileira de baixo:
    # entrada e energia no canto -Z (lado a lado, igual o thruster), saida no canto +Z.
    # 0.10.1 (form8): 4a porta = FREIO (entrada amarela, por ultimo na lista pra nao mexer nos indices das outras), ao lado da energia.
    for suf, b, _, m, _ in MOTORES:
        x, y, z0 = b[0] / 2 - 0.0625, -b[1] / 2 + 0.0625, -b[2] / 2 + 0.0625
        r["SC_Motor" + suf] = prefab("SC_Motor" + suf, b, m, [(0, dict(faces(b))["-Y"])],   # 0.22.6 (Andre: "a cola do motor e' so na parte debaixo da base")
                                     [((x, y, z0), 0, 0), ((x, y, z0 + 0.125), 0, 2), ((x, y, -z0), 0, 7), ((x, y, z0 + 0.25), 0, 0)])   # 0.15.0: saida = Drive Shaft (plasma)
    # motores a combustivel (0.8.0, form5; 0.8.1 pedido do Andre: tudo na face +X). Fileira de baixo da face +X:
    # [cano 0,25 x 0,25 no canto -Z] ... [entrada amarela][saida azul][energia vermelha no canto +Z]. No pequeno (4 casas)
    # nao cabe: energia sobe uma fileira, em cima da saida. Cano = quadrado 0,25 (grade de 0,25) no canto baixo/-Z; o resto
    # da face +X cola (2 retangulos fora do quadrado). Boca de cano visivel = malha do Pipe Quarter do jogo, rasa, na face.
    # Script raiz = EPC_SCPipe: pro jogo e' um cano sem saida, que os canos enchem sozinhos (FuelData); o plugin tira o que
    # o motor gasta. Estabilidade 0 = aguenta qualquer mistura (igual Nanopipe).
    # 0.10.2 (form9): P = 0,5 x 1 x 0,5, cano no canto de baixo/-Z da face +X (igual M/G) e as 4 portas num grupo 2x2 do lado
    # +Z do cano (em cima: entrada, freio; embaixo: energia, saida — "vermelho e azul juntos embaixo"). Freio = 4a porta
    # (entrada), por ultimo na lista.
    for suf, _, b, _, m in MOTORES:
        hx, hy, hz = b[0] / 2, b[1] / 2, b[2] / 2
        fs = dict(faces(b))
        yb = -hy + 0.0625
        xp = hx - 0.0625
        cano = [(hx, -hy, -hz), (hx, -hy + 0.25, -hz), (hx, -hy + 0.25, -hz + 0.25), (hx, -hy, -hz + 0.25)]
        resto1 = [(hx, -hy, -hz + 0.25), (hx, hy, -hz + 0.25), (hx, hy, hz), (hx, -hy, hz)]
        resto2 = [(hx, -hy + 0.25, -hz), (hx, hy, -hz), (hx, hy, -hz + 0.25), (hx, -hy + 0.25, -hz + 0.25)]
        if b[2] >= 1:
            portas = [((xp, yb, hz - 0.3125), 0, 0), ((xp, yb, hz - 0.0625), 0, 2), ((xp, yb, hz - 0.1875), 0, 7), ((xp, yb, hz - 0.4375), 0, 0)]
        else:
            y1, y2, ze, zd = -hy + 0.1875, -hy + 0.0625, hz - 0.0625, hz - 0.1875   # ze = coluna longe do cano (+Z), zd = ao lado do cano
            portas = [((xp, y1, ze), 0, 0), ((xp, y2, ze), 0, 2), ((xp, y2, zd), 0, 7), ((xp, y1, zd), 0, 0)]
        areas = [(0, fs["-Y"]), (1, cano)]   # 0.22.6: cola so embaixo (+ a boca do cano, que e' a area de cano)
        # (0.9.1: o soquete octogonal do cano faz parte da malha propria; a "boca" do Pipe Quarter nao e' mais posta)
        r["SC_MotorComb" + suf] = prefab("SC_MotorComb" + suf, b, m, areas, portas, script=S_PIPE,
                                         extra_raiz="  _fuelCapacity: 0.25\n  _fuelStabilityThreshold: 0\n  _fuelStabilitySpeed: 0.25\n")
    # recoupler: quarter que cola nas 6 faces (e' parte do carro); reacopla encostado na face que solta de um Decoupler
    # que ja disparou. Entrada na face +X, canto de baixo (igual o pistao).
    fs = faces((0.5, 0.5, 0.5))
    # 0.10.2 (Andre): a entrada subiu pra faixa de cima (y 0,1875) — a boca redonda ficou limpa igual a do decoupler
    # 0.22.0: + 4 portas de passagem (anel coletor) na faixa de cima da face -X (oposta a entrada); o anel vai colado no decoupler onde ele engata
    r["SC_Recoupler"] = prefab("SC_Recoupler", (0.5, 0.5, 0.5), 60, [(0, v) for _, v in fs], [((0.1875, 0.1875, 0.1875), 0, 0)] + [((-0.1875, 0.1875, z), 1, 5) for z in (-0.1875, -0.0625, 0.0625, 0.1875)])
    # beacon: 0.11.2 = bloco de 0,25 igual o Wireless Transmitter (console em cunha, antena passando por cima do bloco), sem
    # etiqueta (Andre: "remove o action label de tudo"); cola nas 6 faces; porta de energia na face -X (o plugin cobra 5 P/s e so
    # mostra no radar com energia)
    import modelos_v2 as mv
    # 0.17.0: knob do icone (3 posicoes: ondas / carro / nave) — o plugin le o valor e mostra o icone no display e no radar
    r["SC_Beacon"] = prefab("SC_Beacon", (0.25, 0.25, 0.25), 6, [(0, v) for _, v in faces((0.25, 0.25, 0.25))], BEACON_PORTAS,
                            alavancas={"Knob": ("knob", (0, -60, 0), (0, 60, 0), 0.03)},
                            colisor_caixa=[((0, -0.1, 0), (0.25, 0.05, 0.25)),                       # placa da base
                                           ((-0.11, -0.0375, 0), (0.03, 0.075, 0.25)),                # parede de tras (porta)
                                           ((-0.001, -0.005, 0), (0.192, 0.14, 0.19))])               # console, ate y 0,065 (o knob fica acima)
    # volante (0.11.2): bloco proprio (EPC generico) com UMA alavanca do jogo (joystick horizontal: gira em Z +-90, inclinada
    # VOLANTE_INCL em X = perpendicular a coluna). Base pequena no canto de tras/baixo: cola no chao (face -Y, so a pegada da
    # base) e na parede de tras (+Z); saida na face de baixo da base; colisor = so a base. O plugin copia o valor pra porta.
    x0, x1, y0, y1, z0, z1 = mv.VOLANTE_BASE
    areas = [(0, [(x0, y0, 0.25), (x1, y0, 0.25), (x1, y1, 0.25), (x0, y1, 0.25)]), (0, [(x1, -0.25, z0), (x0, -0.25, z0), (x0, -0.25, z1), (x1, -0.25, z1)])]   # 0.12.2: parede de tras + chao (caixinha)
    r["SC_Volante"] = prefab("SC_Volante", (0.5, 0.5, 0.5), 15, areas, VOLANTE_PORTAS,
                             alavancas={"Alavanca": ("hor", (mv.VOLANTE_INCL, 0, 90), (mv.VOLANTE_INCL, 0, -90), ((0, 0.14, 0), (0, -0.14, 0), 0.16))},
                             colisor_caixa=[(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), (x1 - x0, y1 - y0, z1 - z0))])   # (0.19.3: substituido pelos cascos do modelo quando existem)
    # pedais (0.11.1/0.11.2, Andre: "dois pedais no mesmo bloco, so a acao de pisar"): bloco proprio, cola pela face de baixo,
    # 2 alavancas de UM sentido (0..1, eixo X: repouso = malha ja inclinada, pisado = +PEDAL_CURSO) e 2 saidas na face +Z (esquerda
    # -> porta 0, direita -> porta 1). Colisores: caixa das saidas (tras) + travessa da frente (os pedais ficam expostos).
    fs = dict(faces((0.5, 0.5, 0.5)))
    caps = mv.pedal_caixa_clique()   # 0.12.2 (Andre): pedais = dois BOTOES do jogo (1 enquanto aperta), uma saida (dir - esq)
    alav = {"AlavancaE": ("botao", (0, 0, 0), (mv.PEDAL_CURSO, 0, 0), caps), "AlavancaD": ("botao", (0, 0, 0), (mv.PEDAL_CURSO, 0, 0), caps)}
    r["SC_Pedal"] = prefab("SC_Pedal", (0.5, 0.5, 0.5), 15, [(0, fs["-Y"])], PEDAL_PORTAS, alavancas=alav,
                           colisor_caixa=[((0, -0.1875, 0.22), (0.5, 0.125, 0.06)), ((0, -0.2375, 0.095), (0.5, 0.025, 0.19)),
                                          ((0, -0.2325, mv.PEDAL_PIVO_Z), (0.5, 0.035, 0.10))])
    # 0.15.0: Differential (0,25: 1 entrada de eixo em -X, 4 saidas em +X) e Shaft Reverser (0,25 x 0,125 x 0,25 igual os de
    # matematica: entrada -Z, saida +Z). Portas de plasma (tipo 4) = Drive Shaft; colam nas 6 faces.
    bd = (0.25, 0.25, 0.25)
    r["SC_Differential"] = prefab("SC_Differential", bd, 12, [(0, v) for _, v in faces(bd)],
                                  [((-0.0625, y, z), 1, 6) for y in (-0.0625, 0.0625) for z in (-0.0625, 0.0625)] + [((0.0625, y, z), 0, 7) for y in (-0.0625, 0.0625) for z in (-0.0625, 0.0625)])
    brv = (0.25, 0.125, 0.25)
    r["SC_ShaftReverser"] = prefab("SC_ShaftReverser", brv, 5, [(0, v) for _, v in faces(brv)], [((-0.0625, 0, -0.0625), 5, 6), ((-0.0625, 0, 0.0625), 4, 7)])
    # 0.18.0 (Andre): Single Cell Battery — a bateria do jogo (EPC_SCBattery) com 1/4 da pequena (2000 -> 500), modelo proprio 2x4x2 e
    # a barra de carga do jogo; Long Range Transmitter — bloco proprio com as malhas do Wireless Transmitter, 2 portas (sinal entra/sai
    # + canal por dados), o plugin passa o sinal entre os do mesmo canal sem limite de distancia (Radio.cs)
    bc = (0.25, 0.5, 0.25)
    r["SC_SingleCellBattery"] = prefab("SC_SingleCellBattery", bc, 36, [(0, v) for _, v in faces(bc)], [CELULA_PORTA], script=S_BATERIA,
                                       enfeites=[(mv.CELULA_INDICADOR, (0, 0, 0), (1, 1, 1), M_IND_BATERIA)],
                                       extra_raiz="  _availablePower: 500\n  _chargeablePower: 500\n  _indicatorTransformRenderer: {fileID: %d}\n" % fid("SC_SingleCellBattery", "enfeite0", "mb") +
                                                  "  _indicatorTransformMinScale:\n    x: 0\n    y: 1\n    z: 1\n  _indicator3DTextRenderer: {fileID: 0}\n",
                                       colisor_caixa=[((0, 0, 0), (0.24, 0.5, 0.24))])
    bt = (0.25, 0.25, 0.25)
    # 0.22.1: corpo/pintavel proprios (modelos_v2.radio: cunha + console + 1 antena + globo) — as malhas do jogo ficam so na tela, etiqueta e LED
    # 0.22.2 (Andre: "arruma as cores do display e do label - label nao ta funcionando"): base pintavel do jogo de novo (malhas_jogo), so as
    # antenas sao malha propria (antena da direita + globo); tela, etiqueta (componente + texto) e numero do canal na lista de pintaveis,
    # igual o SC_WirelessTransmitter do jogo (fora dela a tela e a etiqueta ficavam brancas e o texto da etiqueta sumia)
    r["SC_LongRangeTransmitter"] = prefab("SC_LongRangeTransmitter", bt, 6, [(0, v) for nm, v in faces(bt) if nm != "+Y"], RADIO_PORTAS,
                                          malhas_jogo=(None, "342554b462ef80d4ba88174d2bce0060"), pintar_extras=("tela",),
                                          enfeites=[((0, 0, 0), (0, 0, 0), (1, 1, 1), "7911e74b4b59f284db6e6cb279dce854", "led"),    # indicador (LED, 0.19.2)
                                                    ((0, 0, 0), (0, 0, 0), (1, 1, 1), "ce137a3da99fda244ac2396b5fcd1c7c", "tela")],   # 0.18.1: tela (lisa, sem alavanca)
                                          # 0.18.1 (Andre: "deixa ele liso e reto, pode deixar o label"): a chapa da frente e' a etiqueta do jogo
                                          etiqueta=dict(pos_quad=(0, 0, 0), rot_quad=(0, 0, 0), malha="a51a79546a18cfb429fe0a216ff80cb6",
                                                        col_off=(0, -0.04, -0.095), col_rot=(305.15, 0, 0), col_esc=(0.23, 0.01, 0.082),
                                                        pos_texto=(0, -0.042780206, -0.0918596), rot_texto=(305.15, 0, 0), max_len=10),
                                          # 0.18.2 (Andre: "a placa do display mostra o valor que ele ta recebendo pro canal"): o numero do canal,
                                          # igual o do Wireless Transmitter (mesma posicao/tamanho, alinhado a direita); 3 casas (o plugin escreve)
                                          textos=[dict(nome="RendererText3D Channel", pos=RADIO_TEXTO, texto="  0", alin=2, valin=2, mono=1, escala=0.11, pintavel=True)],
                                          cor_padrao=3)   # 0.19.2 (Andre): mesma cor do Shaft Transmitter (DarkGray)
    PORTAS_ICONE["SC_DriveShaft"] = []
    PORTAS_ICONE["SC_MiniElectricThruster"] = []   # (copia do jogo: mini_propulsor())
    PORTAS_ICONE["SC_SolarPanelMini"] = []   # 0.19.0 (copia do jogo: solar_mini())
    PORTAS_ICONE["SC_ShaftTransmitter"] = [TRANSMISSOR_PORTA]   # (o prefab e' copiado do jogo em transmissor())
    for nome, _, _ in TANQUES: PORTAS_ICONE[nome] = [TANQUE_PORTA]   # (o prefab do tanque e' feito em tanque(); aqui so pro icone)
    PORTAS_ICONE["SC_TurretSeat"] = list(mv.TURRET_PORTAS)            # (idem: turret())
    # rodas: cilindro com eixo em Y (o mesmo eixo do bearing embaixo). A grade anda de 0,5 em 0,5 e o eixo tem que cair no
    # centro de um quarter (igual o bearing), entao a caixa ocupa um numero IMPAR de quarters: roda de 1 m ocupa 1,5.
    # Malha (visual e colisor convexo) e atrito sao postos pelo plugin (Rodas.cs). Cola so por um quadrado 0,5 x 0,5 no
    # centro das faces de baixo e de cima (pra nao grudar em nada perto do aro).
    for nome, diam, massa in RODAS:
        caixa = diam if round(diam / 0.5) % 2 == 1 else diam + 0.5
        q = [(0.25, -0.25, -0.25), (-0.25, -0.25, -0.25), (-0.25, -0.25, 0.25), (0.25, -0.25, 0.25)]
        baixo = [(x, -0.25, z) for x, _, z in q]; cima = [(x, 0.25, z) for x, _, z in q]
        # 0.12.14 (Andre): so o lado de dentro (-Y, flange) cola — a roda tem lado certo de encaixar
        r[nome] = prefab(nome, (caixa, 0.5, caixa), massa, [(0, baixo)], [], convexo=True, so_pintavel=True, escala_corpo=(1, 1, 1))
    return r


# tanques pequenos (0.8.0 form5; 0.9.0 form6; 0.10.1 form8): 1x2x1 m, 500 de combustivel, copia do prefab do tanque do jogo
# da MESMA cor (script EPC_SCTank, medidor, valvula, porta), mas com MALHA PROPRIA (modelos_v2.tanque) em escala 1 — antes
# tudo era encolhido junto e o plugue da porta e a boca do cano ficavam pequenos e fora da grade. Layout fixo dos dois:
# cano na cinta de baixo, frente +Z, celula x 0..0,25 / y -0,75..-0,5; medidor do jogo (com a porta de dados dentro) em
# (0, -0,3125, 0,5); valvula do jogo em (-0,30, -0,625, 0,465); cola pelas duas cintas na face de tras (-Z), igual o jogo.
TANQUES = [("SC_TanqueAzul", 0, "SC_BlueTank.prefab"), ("SC_TanqueVerde", 1, "SC_GreenTank.prefab")]
TANQUE_MASSA, TANQUE_COMB, TANQUE_CAIXA = 420, 500, (1, 2, 1)
TANQUE_COLISOR, TANQUE_COLISOR_OFF = (1, 2, 0.94), (0, 0, -0.03)   # 0.11.2: colisor para em z 0,44 (valvula em z 0,466 fica de fora)
TANQUE_MEDIDOR, TANQUE_VALVULA = (0, -0.3125, 0.5), (-0.12, -0.625, 0.466)   # 0.10.2: valvula na meia-lua, do lado do bico
TANQUE_PORTA = ((0.0625, -0.3125, 0.4375), 4, 1)
TANQUE_CANO = [(0, -0.75, 0.5), (0.25, -0.75, 0.5), (0.25, -0.5, 0.5), (0, -0.5, 0.5)]
TANQUE_COLA = [[(-0.5, 0.75, -0.5), (-0.5, 0.5, -0.5), (0.5, 0.5, -0.5), (0.5, 0.75, -0.5)],
               [(-0.5, -0.5, -0.5), (-0.5, -0.75, -0.5), (0.5, -0.75, -0.5), (0.5, -0.5, -0.5)]]


def tanque(nome, tipo, yaml):
    import re
    docs = yaml.split("\n--- ")
    assert "fileID: %d, guid: %s" % (S_TANQUE, G) in yaml
    proprio = REF[nome]
    def poe_v(txt, campo, v, ind="  "):
        return re.sub(r"(%s%s:\n%s  x: )\S+(\n%s  y: )\S+(\n%s  z: )\S+" % (ind, campo, ind, ind, ind),
                      lambda m: m.group(1) + fmt(v[0]) + m.group(2) + fmt(v[1]) + m.group(3) + fmt(v[2]), txt, count=1)
    def poe_area(txt, vs):
        it = iter(vs)
        return re.sub(r"  - x: \S+\n    y: \S+\n    z: \S+", lambda _m: (lambda v: "  - x: %s\n    y: %s\n    z: %s" % (fmt(v[0]), fmt(v[1]), fmt(v[2])))(next(it)), txt)
    nomes = {}
    for d in docs:
        m = re.match(r"!u!1 &(\d+)", d)
        if m: nomes[m.group(1)] = re.search(r"m_Name: (.*)", d).group(1)
    novos = []; cola = iter(TANQUE_COLA)
    for d in docs:
        m = re.match(r"!u!114 &(\d+)", d)
        if not m: novos.append(d); continue
        sc = int(re.search(r"m_Script: \{fileID: (-?\d+)", d).group(1))
        go = nomes.get(re.search(r"m_GameObject: \{fileID: (\d+)", d).group(1), "")
        if sc == S_REND:
            d = poe_v(d, "_scale", (1, 1, 1))
            if go == "Renderer": d = re.sub(r"_mesh: \{fileID: \d+, guid: \w+", "_mesh: {fileID: 4300000, guid: %s" % proprio[0], d, count=1)
            elif go == "Paintable": d = re.sub(r"_mesh: \{fileID: \d+, guid: \w+", "_mesh: {fileID: 4300000, guid: %s" % (proprio[1] or proprio[0]), d, count=1)
            elif go == "Tank Meter": d = poe_v(d, "_position", TANQUE_MEDIDOR)
            elif go == "Actionable Valve": d = poe_v(d, "_position", TANQUE_VALVULA)
            elif go.startswith("Port "): d = poe_v(d, "_position", TANQUE_PORTA[0])
        elif sc == S_COL_CONVEXO and go == "Collider":
            # colisor de malha (encolhido) -> caixa 1x2x0,94 (0.11.2: 6 cm mais curta na frente, senao a caixa de clique da valvula do
            # jogo fica DENTRO do colisor do bloco e o raio do jogador nao chega nela — Andre: "a valvula ainda ta inacessivel")
            d = re.sub(r"m_Script: \{fileID: -?\d+", "m_Script: {fileID: %d" % S_COL, d, count=1)
            d = re.sub(r"  _mesh: \{[^}]*\}\n", "", d, count=1)
            d = poe_v(poe_v(d, "_scale", TANQUE_COLISOR), "_offset", TANQUE_COLISOR_OFF)
        elif sc == S_AREA:
            if int(re.search(r"_type: (\d+)", d).group(1)) == 1: d = poe_area(d, TANQUE_CANO)
            else: d = poe_area(d, next(cola))
        elif sc == S_TANQUE:
            d = re.sub(r"_mirroredVersion: \{[^}]*\}", "_mirroredVersion: {fileID: 0}", d, count=1)
            d = poe_v(poe_v(d, "_bounds", TANQUE_CAIXA), "_uiPreviewBounds", TANQUE_CAIXA)
            d = re.sub(r"_mass: \S+", "_mass: %d" % TANQUE_MASSA, d, count=1)
            d = re.sub(r"_availableAmount: \S+", "_availableAmount: 500", d, count=1)
            d = re.sub(r"_categories: \S+", "_categories: 516", d, count=1)   # aba Combustivel (4) + aba Mecanica (512)
            d = re.sub(r"_fuelType: \S+", "_fuelType: %d" % tipo, d, count=1)
            d = re.sub(r"_maxFuel: \S+", "_maxFuel: %d" % TANQUE_COMB, d, count=1)
            d = re.sub(r"_iconTexture2D: \{fileID: \d+, guid: \w+", "_iconTexture2D: {fileID: 2800000, guid: %s" % proprio[2], d, count=1)
            (p, dr, t) = TANQUE_PORTA
            d = re.sub(r"(  - _position:\n      x: )\S+(\n      y: )\S+(\n      z: )\S+(\n    _direction: )\d+",
                       lambda mm: mm.group(1) + fmt(p[0]) + mm.group(2) + fmt(p[1]) + mm.group(3) + fmt(p[2]) + mm.group(4) + str(dr), d, count=1)
        novos.append(d)
    t = "\n--- ".join(novos)
    t = re.sub(r"m_Name: SC_(Green|Blue)Tank\n", "m_Name: %s\n" % nome, t, count=1)
    return t if t.endswith("\n") else t + "\n"


# 0.15.0 (Andre: "copia a logica do cabo da data/plasma"): o Drive Shaft e' um CABO do jogo (EPC_SCCable) do tipo Plasma — o jogo
# so conhece 4 tipos de cabo (data/power/nano/plasma) e o desenho de cada pedaco vem de uma tabela por tipo. Copia o prefab do cabo
# de plasma do jogo: nome, aba Mechanics, quantidade, icone proprio, cor padrao = ferro (MaterialIron = 200, cor que nao esta na
# paleta). Todas as malhas viram UM marcador (Jogo_CaboPlasma); o plugin copia as malhas do cabo de plasma do jogo em runtime.
def driveshaft(nome, yaml):
    import re, gen_meshes
    marc = gen_meshes.guid_marcador("CaboPlasma")
    t = re.sub(r"guid: (\w+), type: 2\}", lambda m: m.group(0) if m.group(1) == MAT else "guid: %s, type: 2}" % marc, yaml)
    t = re.sub(r"m_Name: SC_PlasmaCable\n", "m_Name: %s\n" % nome, t, count=1)
    t = re.sub(r"_availableAmount: \S+", "_availableAmount: 5000", t, count=1)
    t = re.sub(r"_categories: \S+", "_categories: 512", t, count=1)
    t = re.sub(r"_defaultPaintableColor: \S+", "_defaultPaintableColor: 3", t, count=1)   # 0.17.0: DarkGray (200 = ferro = cor dos frames)
    t = re.sub(r"_mirroredVersion: \{[^}]*\}", "_mirroredVersion: {fileID: 0}", t, count=1)
    t = re.sub(r"_iconTexture2D: \{fileID: \d+, guid: \w+", "_iconTexture2D: {fileID: 2800000, guid: %s" % REF[nome][2], t, count=1)
    assert "_type: 3" in t, "o cabo de plasma do jogo mudou (_type)"
    return t if t.endswith("\n") else t + "\n"


# 0.16.0 (Andre: "o wireless transmitter e' um 2x2x2, e eu quero exatamente igual ao do jogo, muda a cor so, e a saida"): o Shaft
# Transmitter e' uma COPIA do SC_WirelessTransmitter do jogo (mesma classe EPC_SCWirelessTransmitter: alavanca do canal, numero, etiqueta,
# indicador — tudo do jogo). Muda: nome, aba Mechanics, cor padrao (ferro), icone, e a porta: Port X de dados (InOut) -> porta de eixo
# (plasma, plugue meio laranja meio roxo). O plugin le o canal (SCTypeWirelessTransmitter._currentChannel) e liga os transmitters de eixo
# do mesmo canal entre si (Eixos.cs) — a porta de plasma nao troca dados com os transmitters de dados do jogo.
# As malhas do jogo viram marcadores (MALHAS_JOGO, religadas em runtime), inclusive a do colisor (JogoMalhas religa DOTSColliderConvex).
TRANSMISSOR_PORTA = ((-0.0625, -0.0625, 0.0625), 4, 8)
# 0.18.0
S_BATERIA = 1312948061   # EPC_SCBattery
M_IND_BATERIA = "14b26e76dee50ad45a1f460c93537766"   # Small Rechargeable Battery Indicator (barra de carga do jogo)
CELULA_PORTA = ((0.0625, -0.1875, -0.0625), 5, 2)   # energia na face -Z, embaixo
RADIO_TEXTO = (0.04958624, -0.01527491, -0.04406795)   # numero do canal (posicao do texto do Wireless Transmitter)
RADIO_PORTAS = [((-0.0625, -0.0625, 0.0625), 4, 3), ((0.0625, -0.0625, 0.0625), 4, 0)]   # sinal (entra/sai) + canal (dados)
S_PROPULSOR = -1784946918   # EPC_SCThruster
M_PORT_INOUT = "2d697d79043ceb1468a4f403ec2484ff"
S_TRANSMISSOR = 1484554865   # EPC_SCWirelessTransmitter
MAT_LED = "94f3adf3f8f47f349929f1b6dff8ce54"   # 0.19.8: material do indicador do Wireless Transmitter do jogo (CRPUnlit)
LED_MARCA = "0.0041"   # 0.19.2: specular strength que marca o renderer do LED dos transmitters (o plugin le CRPRendererMemory_Color +16)


def transmissor(nome, yaml, espelho=False):
    import re, hashlib
    assert "fileID: %d, guid: %s" % (S_TRANSMISSOR, G) in yaml, "SC_WirelessTransmitter do jogo esperado"
    assert yaml.count(M_PORT_INOUT) == 1, "o transmitter do jogo mudou (Port X)"
    t = yaml.replace("guid: %s, type: 2}" % M_PORT_INOUT, "guid: %s, type: 2}" % malha_porta(8))
    t = re.sub(r"\n  m_Name: Port X\n", "\n  m_Name: Port Shaft InOut\n", t, count=1)
    t = re.sub(r"m_Name: SC_WirelessTransmitter( M)?\n", "m_Name: %s\n" % (nome + (" M" if espelho else "")), t, count=1)
    t = re.sub(r"(  _electricPorts:\n  - _position:\n(?:      [xyz]: \S+\n){3}    _direction: \d+\n    _type: )3\n", r"\g<1>4\n", t, count=1)
    assert "    _type: 4\n  _iconTexture2D" in t, "porta do transmitter nao trocada"
    t = re.sub(r"_availableAmount: \S+", "_availableAmount: 500", t, count=1)
    t = re.sub(r"_categories: \S+", "_categories: 512", t, count=1)
    t = re.sub(r"_defaultPaintableColor: \S+", "_defaultPaintableColor: 3", t, count=1)   # 0.17.0: DarkGray (200 = ferro = cor dos frames)
    # 0.19.2: marca do LED (indicador) pro plugin achar o renderer e pintar verde/amarelo/vermelho
    t = re.sub(r"(_mesh: \{fileID: 4300000, guid: 7911e74b4b59f284db6e6cb279dce854, type: 2\}\n(?:.*\n){0,6}?  _specular:\n    _strength: )\S+", lambda m: m.group(1) + LED_MARCA, t, count=1)
    if espelho: t = re.sub(r"_mirroredVersion: \{[^}]*\}", "_mirroredVersion: {fileID: 0}", t, count=1)
    else:
        gm = hashlib.md5(("mecanica|prefab|" + nome + " M").encode()).hexdigest()
        t = re.sub(r"_mirroredVersion: \{fileID: (\d+), guid: \w+", lambda m: "_mirroredVersion: {fileID: %s, guid: %s" % (m.group(1), gm), t, count=1)
    t = re.sub(r"_iconTexture2D: \{fileID: \d+, guid: \w+", "_iconTexture2D: {fileID: 2800000, guid: %s" % REF[nome][2], t, count=1)
    # 0.22.2: so o renderer das antenas vira malha propria (antena da direita + engrenagem); base pintavel, tela, etiqueta e LED = os do jogo
    assert t.count("guid: 6ae3c4a33e20464449e77878864deb30, type: 2}") == 1, "malha do corpo do transmitter"
    t = t.replace("guid: 6ae3c4a33e20464449e77878864deb30, type: 2}", "guid: %s, type: 2}" % REF[nome][0], 1)
    return t if t.endswith("\n") else t + "\n"


def injetar_renderer(t, nome, tag, nomego, malha, pos=(0, 0, 0), espelho=False):
    """0.22.1: poe um GameObject filho (Transform + EPC_Renderer com a malha) num prefab copiado do jogo, pendurado no Transform raiz
    (o que tem m_Father 0). espelho: escala x -1 (a malha sai espelhada, igual o espelhar() faz com as nossas)."""
    import re
    m = re.search(r"--- !u!4 &(\d+)\nTransform:\n(?:  .*\n)*?  m_Children:((?:\n  - \{fileID: \d+\})*| \[\])\n  m_Father: \{fileID: 0\}", t)
    assert m, "transform raiz nao achado em " + nome
    raiz_tr = int(m.group(1))
    g, tr_, mb_ = fid(nome, tag, "go"), fid(nome, tag, "tr"), fid(nome, tag, "mb")
    esc = (-1, 1, 1) if espelho else (1, 1, 1)
    docs = ("--- !u!1 &%d\nGameObject:\n  serializedVersion: 6\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_Component:\n  - component: {fileID: %d}\n  - component: {fileID: %d}\n  m_Layer: 0\n  m_Name: %s\n  m_TagString: Untagged\n  m_Icon: {fileID: 0}\n  m_NavMeshLayer: 0\n  m_StaticEditorFlags: 0\n  m_IsActive: 1\n" % (g, tr_, mb_, nomego) +
            "--- !u!4 &%d\nTransform:\n  serializedVersion: 2\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: %d}\n  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n  m_LocalPosition: %s\n  m_LocalScale: %s\n  m_ConstrainProportionsScale: 0\n  m_Children: []\n  m_Father: {fileID: %d}\n  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}\n" % (tr_, g, v3(pos), v3(esc), raiz_tr) +
            "--- !u!114 &%d\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: %d}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n  m_Script: {fileID: %d, guid: %s, type: 3}\n  m_Name: \n  m_EditorClassIdentifier: \n" % (mb_, g, S_REND, G) +
            "  _position:\n    x: %s\n    y: %s\n    z: %s\n  _rotation:\n    x: 0\n    y: 0\n    z: 0\n  _scale:\n    x: %s\n    y: %s\n    z: %s\n" % (tuple(fmt(a) for a in pos) + tuple(fmt(a) for a in esc)) +
            "  _mesh: {fileID: 4300000, guid: %s, type: 2}\n  _submeshID: 0\n  _material: {fileID: 2100000, guid: %s, type: 2}\n  _layer: 0\n  _color: {r: 0, g: 0, b: 0, a: 0}\n  _specular:\n    _strength: 0\n    _smoothness: 0\n  _hasHeat: 1\n  _spaceshipColor: 0\n" % (malha, MAT))
    filhos = m.group(2)
    novo_filhos = ("\n  - {fileID: %d}" % tr_) if filhos.strip() == "[]" else filhos + "\n  - {fileID: %d}" % tr_
    t = t[:m.start(2)] + novo_filhos + t[m.end(2):]
    return (t if t.endswith("\n") else t + "\n") + docs, mb_


# 0.18.0 (Andre: "uma versao pequena de algum foguete, do tamanho de um frame quarter ou um pouco mais alto"; escolheu o propulsor
# eletrico pequeno): COPIA do SC_SmallElectricThruster do jogo em escala 0,5 (1 x 2 x 1 -> 0,5 x 1 x 0,5): tudo que e' posicao/escala
# x 0,5 (renderers, colisor, areas de cola, calor, particulas, sopro), forca e consumo / 4, massa 40. As 2 portas vao pras casas da
# face de baixo (entrada x +-0,0625, energia x +-0,1875); o botao de re vai junto da entrada.
K_MINI = 0.5
# 0.19.0: rotacoes Euler do Unity (R = Ry.Rx.Rz) pra girar o botao de re junto com a porta de entrada
def _rx(a): c, s_ = math.cos(math.radians(a)), math.sin(math.radians(a)); return [[1, 0, 0], [0, c, -s_], [0, s_, c]]
def _ry(a): c, s_ = math.cos(math.radians(a)), math.sin(math.radians(a)); return [[c, 0, s_], [0, 1, 0], [-s_, 0, c]]
def _rz(a): c, s_ = math.cos(math.radians(a)), math.sin(math.radians(a)); return [[c, -s_, 0], [s_, c, 0], [0, 0, 1]]
def _mat_mul(a, b): return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
def _rot_x(a): return _rx(a)
def _mat_euler(r): return _mat_mul(_ry(r[1]), _mat_mul(_rx(r[0]), _rz(r[2])))
def _euler_de(m):
    x = math.degrees(math.asin(max(-1.0, min(1.0, -m[1][2]))))
    if abs(m[1][2]) < 0.9999: y = math.degrees(math.atan2(m[0][2], m[2][2])); z = math.degrees(math.atan2(m[1][0], m[1][1]))
    else: y = math.degrees(math.atan2(-m[2][0], m[0][0])); z = 0.0
    return tuple(round(v, 4) + 0.0 for v in (x, y, z))
def mini_propulsor(nome, yaml, espelho=False):
    import re, hashlib
    assert "fileID: %d, guid: %s" % (S_PROPULSOR, G) in yaml, "SC_SmallElectricThruster do jogo esperado"
    k = K_MINI
    def v3re(campo, ind="  "):
        return re.compile(r"(%s%s:\n%s  x: )(\S+)(\n%s  y: )(\S+)(\n%s  z: )(\S+)" % (ind, campo, ind, ind, ind))
    def esc3(txt, campo, f, ind="  ", n=1):
        return v3re(campo, ind).sub(lambda m: m.group(1) + fmt(float(m.group(2)) * f[0]) + m.group(3) + fmt(float(m.group(4)) * f[1]) + m.group(5) + fmt(float(m.group(6)) * f[2]), txt, count=n)
    def poe3(txt, campo, v, ind="  "):
        return v3re(campo, ind).sub(lambda m: m.group(1) + fmt(v[0]) + m.group(3) + fmt(v[1]) + m.group(5) + fmt(v[2]), txt, count=1)
    def le3(txt, campo, ind="  "):
        m = v3re(campo, ind).search(txt); return (float(m.group(2)), float(m.group(4)), float(m.group(6)))
    def escf(txt, campo, f):
        return re.sub(r"(\n  %s: )(\S+)" % campo, lambda m: m.group(1) + fmt(float(m.group(2)) * f), txt, count=1)
    K3 = (k, k, k)
    docs = yaml.split("\n--- ")
    nomes = {}
    for d in docs:
        m = re.match(r"!u!1 &(\d+)", d)
        if m: nomes[m.group(1)] = re.search(r"m_Name: (.*)", d).group(1)
    # portas originais (do prefab) -> novas casas
    raiz = [d for d in docs if "fileID: %d, guid: %s" % (S_PROPULSOR, G) in d][0]
    pos_or = [(float(a), float(b), float(c)) for a, b, c in re.findall(r"  - _position:\n      x: (\S+)\n      y: (\S+)\n      z: (\S+)\n    _direction: \d+\n    _type: [02]", raiz)]
    assert len(pos_or) == 2, pos_or
    sx = 1.0 if pos_or[0][0] > 0 else -1.0; sz = 1.0 if pos_or[0][2] > 0 else -1.0
    # 0.19.0 (Andre: "tira as portas dele da parte debaixo da base e joga no cantinho lateral da base — sem ser os lados opostos, em
    # qualquer lado adjacente"): as duas na casa do canto de baixo, cada uma numa face lateral (entrada na face Z, energia na face X) = "L"
    # 0.19.1 (Andre: "fica flicando entre porta e nao porta, muito dificil de conectar"): as duas na MESMA casa brigavam no hover ->
    # cada uma na sua casa: entrada na casa do canto (face Z), energia na casa vizinha ao longo do Z (face X)
    novo_in = (sx * 0.1875, -0.4375, sz * 0.1875); novo_pw = (sx * 0.1875, -0.4375, sz * 0.0625)
    dir_in = 4 if sz > 0 else 5; dir_pw = 0 if sx > 0 else 1
    Rf = _rot_x(-90.0 if sz > 0 else 90.0)   # leva o -Y (porta embaixo, original) pra face da entrada
    def desloc_botao(p):
        o = [(p[i] - pos_or[0][i]) for i in range(3)]   # 0.19.0: tamanho real (a porta nao encolhe; com x0,5 o botao entrava na face)
        o = [sum(Rf[i][j] * o[j] for j in range(3)) for i in range(3)]
        o[2] += sz * 0.006   # 0.19.3 (Andre: "nao da pra ver o switch direito, puxa pra fora um pouco"): 6 mm pra fora da face
        return tuple(novo_in[i] + o[i] for i in range(3))
    def rot_botao(r): return _euler_de(_mat_mul(Rf, _mat_euler(r)))
    novos = []
    for d in docs:
        m = re.match(r"!u!114 &(\d+)", d)
        if re.match(r"!u!4 &", d):
            go = nomes.get(re.search(r"m_GameObject: \{fileID: (\d+)", d).group(1), "")
            mp = re.search(r"m_LocalPosition: \{x: (\S+), y: (\S+), z: (\S+)\}", d)
            if mp and go != nome and not go.startswith("SC_SmallElectricThruster"):
                p0 = tuple(float(x) for x in mp.groups())
                if go == "Port Input": p1 = novo_in
                elif go == "Port Power": p1 = novo_pw
                elif go.startswith("Port Input Reverse"): p1 = desloc_botao(p0)
                else: p1 = tuple(x * k for x in p0)
                d = d.replace(mp.group(0), "m_LocalPosition: {x: %s, y: %s, z: %s}" % tuple(fmt(x) for x in p1), 1)
            novos.append(d); continue
        if not m: novos.append(d); continue
        sc = int(re.search(r"m_Script: \{fileID: (-?\d+)", d).group(1))
        go = nomes.get(re.search(r"m_GameObject: \{fileID: (\d+)", d).group(1), "")
        if sc == S_PROPULSOR:
            d = esc3(esc3(d, "_bounds", K3), "_uiPreviewBounds", K3)
            d = esc3(esc3(d, "_soundObstacleMin", K3), "_soundObstacleMax", K3)
            d = re.sub(r"_mass: \S+", "_mass: 40", d, count=1)
            d = esc3(d, "_maxForce", (0.25, 0.25, 0.25))
            d = escf(d, "_powerConsumptionPerSec", 0.25)
            d = re.sub(r"_categories: \S+", "_categories: 642", d, count=1)   # propulsores (128) + eletronica (2) + Mechanics (512)
            d = re.sub(r"_availableAmount: \S+", "_availableAmount: 500", d, count=1)
            d = re.sub(r"_particleSystemMinVelocity: (\S+)", lambda mm: "_particleSystemMinVelocity: " + fmt(float(mm.group(1)) * k), d)
            d = re.sub(r"_particleSystemMaxVelocity: (\S+)", lambda mm: "_particleSystemMaxVelocity: " + fmt(float(mm.group(1)) * k), d)
            it = iter([(novo_in, dir_in), (novo_pw, dir_pw)])
            d = re.sub(r"(  - _position:\n      x: )\S+(\n      y: )\S+(\n      z: )\S+(\n    _direction: )\d+(\n    _type: [02])",
                       lambda mm: (lambda v, dd: mm.group(1) + fmt(v[0]) + mm.group(2) + fmt(v[1]) + mm.group(3) + fmt(v[2]) + mm.group(4) + str(dd) + mm.group(5))(*next(it)), d)
            d = re.sub(r"_iconTexture2D: \{fileID: \d+, guid: \w+", "_iconTexture2D: {fileID: 2800000, guid: %s" % REF["SC_MiniElectricThruster"][2], d, count=1)
            if espelho: d = re.sub(r"_mirroredVersion: \{[^}]*\}", "_mirroredVersion: {fileID: 0}", d, count=1)
            else:
                gm = hashlib.md5(("mecanica|prefab|" + nome + " M").encode()).hexdigest()
                d = re.sub(r"_mirroredVersion: \{fileID: (\d+), guid: \w+", lambda mm: "_mirroredVersion: {fileID: %s, guid: %s" % (mm.group(1), gm), d, count=1)
        elif sc == 884829252:   # EPC_ThrusterBlow (sopro)
            for c_ in ("_blowStartPosMin", "_blowStartPosMax", "_blowEndPosMin", "_blowEndPosMax"): d = esc3(d, c_, K3)
            d = escf(d, "_blowStrength", 0.25)
        elif sc == S_AREA:
            d = re.sub(r"(\n  - x: )(\S+)(\n    y: )(\S+)(\n    z: )(\S+)", lambda mm: mm.group(1) + fmt(float(mm.group(2)) * k) + mm.group(3) + fmt(float(mm.group(4)) * k) + mm.group(5) + fmt(float(mm.group(6)) * k), d)
        elif sc == S_REND:
            p0 = le3(d, "_position")
            # 0.19.2 (Andre: "rola um z-fighting, tem que fazer a entrada da porta pra fora"): o plugue sai 2 mm da face do corpo
            if go == "Port Input": d = poe3(poe3(d, "_position", (novo_in[0], novo_in[1], novo_in[2] + sz * 0.002)), "_rotation", ROT_DIR[dir_in])
            elif go == "Port Power": d = poe3(poe3(d, "_position", (novo_pw[0] + sx * 0.002, novo_pw[1], novo_pw[2])), "_rotation", ROT_DIR[dir_pw])
            elif go.startswith("Port Input Reverse"): d = poe3(poe3(d, "_position", desloc_botao(p0)), "_rotation", rot_botao(le3(d, "_rotation")))
            else: d = esc3(esc3(d, "_position", K3), "_scale", K3)
        elif sc == 762861932:   # botao de re: poses on/off junto da entrada
            for c_ in ("_buttonTransformOnPos", "_buttonTransformOffPos"): d = poe3(d, c_, desloc_botao(le3(d, c_)))
            for c_ in ("_buttonTransformOnRot", "_buttonTransformOffRot"): d = poe3(d, c_, rot_botao(le3(d, c_)))
        elif sc == S_COL:   # caixa de clique do botao (0.19.0: tamanho do jogo, igual a porta)
            pass
        elif sc == S_COL_CONVEXO:
            # 0.18.1 (Andre: "coloco o mini thruster, rodo a simulacao e tudo explode"): o colisor convexo do jogo nao respeita o _scale
            # (ficava do tamanho do propulsor normal, 1 x 2 x 1, dentro dos blocos vizinhos) -> caixa do tamanho do mini (malha: y -0,5..0,45)
            d = re.sub(r"m_Script: \{fileID: -?\d+", "m_Script: {fileID: %d" % S_COL, d, count=1)
            d = re.sub(r"  _mesh: \{[^}]*\}\n", "", d, count=1)
            d = poe3(poe3(d, "_offset", (0, -0.03, 0)), "_scale", (0.46, 0.92, 0.46))
            d = re.sub(r"_bevelRadius: \S+", "_bevelRadius: 0.02", d, count=1)
        elif sc == 23723654:   # calor
            d = esc3(esc3(d, "_position", K3), "_scale", K3)
        elif sc == -1515158256:   # EPC_Transform (particulas, som)
            d = esc3(d, "_position", K3)
        elif sc == -530793849:   # EPC_ParticleSystem
            d = escf(escf(d, "_positionRangeMin", k), "_positionRangeMax", k) if False else d
            d = re.sub(r"(\n    _positionRange(?:Min|Max): )(\S+)", lambda mm: mm.group(1) + fmt(float(mm.group(2)) * k), d)
            d = escf(escf(d, "_scaleMin", k), "_scaleMed", k)
            d = escf(d, "_velocityLinearChange", k)
        elif sc == 1786898767:   # som
            d = escf(d, "_size", k)
        novos.append(d)
    t = "\n--- ".join(novos)
    t = re.sub(r"m_Name: SC_SmallElectricThruster( M)?\n", "m_Name: %s\n" % (nome + (" M" if espelho else "")), t, count=1)
    return t if t.endswith("\n") else t + "\n"


# 0.19.0 (Andre: "seria legal ter um mini solar panel tambem"; "largura e comprimento de um frame quarter, espessura de 1x"): COPIA do
# SC_SolarPanel do jogo (1 x 0,125 x 2) em 0,5 x 0,125 x 0,5 (x 0,5, z 0,25): malhas/area de cola/colisor escalados, geracao e massa
# pela area (1/8), as 2 portas de energia nas pontas (uma em cada face Z, na casa do canto, igual o grande).
S_SOLAR = -1058504809   # EPC_SCSolarPanel
def solar_mini(nome, yaml):
    import re, hashlib
    assert "fileID: %d, guid: %s" % (S_SOLAR, G) in yaml, "SC_SolarPanel do jogo esperado"
    fx, fz = 0.5, 0.25
    def v3(campo, ind="  "):
        return re.compile(r"(%s%s:\n%s  x: )(\S+)(\n%s  y: )(\S+)(\n%s  z: )(\S+)" % (ind, campo, ind, ind, ind))
    def poe(txt, campo, v, ind="  "):
        return v3(campo, ind).sub(lambda m: m.group(1) + fmt(v[0]) + m.group(3) + fmt(v[1]) + m.group(5) + fmt(v[2]), txt, count=1)
    docs = yaml.split("\n--- "); novos = []
    for d in docs:
        m = re.match(r"!u!114 &(\d+)", d)
        if not m: novos.append(d); continue
        sc = int(re.search(r"m_Script: \{fileID: (-?\d+)", d).group(1))
        if sc == S_SOLAR:
            d = poe(poe(d, "_bounds", (0.5, 0.125, 0.5)), "_uiPreviewBounds", (0.5, 0.125, 0.5))
            d = re.sub(r"_mass: \S+", "_mass: 6", d, count=1)
            d = re.sub(r"_maxPowerGenerationPerSec: (\S+)", lambda mm: "_maxPowerGenerationPerSec: " + fmt(float(mm.group(1)) / 8.0), d, count=1)
            d = re.sub(r"_categories: \S+", "_categories: 514", d, count=1)   # eletronica (2) + Mechanics (512)
            d = re.sub(r"_availableAmount: \S+", "_availableAmount: 500", d, count=1)
            d = re.sub(r"(  - _position:\n      x: )(\S+)(\n      y: )(\S+)(\n      z: )(\S+)",
                       lambda mm: mm.group(1) + fmt(-0.1875) + mm.group(3) + mm.group(4) + mm.group(5) + fmt(0.1875 if float(mm.group(6)) > 0 else -0.1875), d)
            d = re.sub(r"_iconTexture2D: \{fileID: \d+, guid: \w+", "_iconTexture2D: {fileID: 2800000, guid: %s" % REF[nome][2], d, count=1)
            gm = hashlib.md5(("mecanica|prefab|" + nome + " M").encode()).hexdigest()
            d = re.sub(r"_mirroredVersion: \{fileID: (\d+), guid: \w+", lambda mm: "_mirroredVersion: {fileID: %s, guid: %s" % (mm.group(1), gm), d, count=1)
        elif sc == S_AREA:
            d = re.sub(r"(\n  - x: )(\S+)(\n    y: )(\S+)(\n    z: )(\S+)", lambda mm: mm.group(1) + fmt(float(mm.group(2)) * fx) + mm.group(3) + mm.group(4) + mm.group(5) + fmt(float(mm.group(6)) * fz), d)
        elif sc == S_REND:
            pm = re.search(r"_mesh: \{fileID: \d+, guid: (\w+)", d); g = pm.group(1) if pm else ""
            if g == "524c7b0ae261a9244b97246a5799c9b4":   # porta de energia: pra casa do canto
                p0 = v3("_position").search(d); z0 = float(p0.group(6))
                d = poe(d, "_position", (-0.1875, 0, 0.1875 if z0 > 0 else -0.1875))
            elif g in ("fe00b5509a5a3094493c4f4ab77c0e9d", "64708f33bff0c2745bf70df3270e39be"):
                # 0.22.5: corpo (celulas) e pintavel (bandeja/borda/cantoneiras) = modelo proprio (modelos_v2.solar_mini), sem esticar
                d = d.replace("guid: %s, type: 2}" % g, "guid: %s, type: 2}" % (REF[nome][0] if g == "fe00b5509a5a3094493c4f4ab77c0e9d" else REF[nome][1]), 1)
            else:
                d = poe(d, "_scale", (fx, 1, fz))
        elif sc == S_COL:
            d = poe(d, "_scale", (0.46, 0.0825, 0.46))
        novos.append(d)
    t = "\n--- ".join(novos)
    t = re.sub(r"m_Name: SC_SolarPanel\n", "m_Name: %s\n" % nome, t, count=1)
    # 0.22.2 (Andre: "o 'ferrinho' ali tem meio que 'cobrir' a porta"): no painel do jogo a porta fica embaixo do suporte do canto; escalado
    # (x 0,5 / z 0,25) o suporte encolheu e saiu de cima da porta (que fica na casa da grade). Caixa propria pintavel em volta de cada porta.
    return t if t.endswith("\n") else t + "\n"


# 0.11.0: volante e pedal eram clones dos joysticks do jogo. 0.11.2: os dois sao blocos proprios (ver blocos()); ficou so a tabela de
# portas. Beacon: porta de energia na face -X (unica celula da fileira de baixo, lado +Z) do bloco de 0,25.
VOLANTE_PORTAS = [((0.0625, -0.1875, 0.1875), 4, 1)]   # 0.19.3 (Andre): UMA saida, na face de tras (+Z) da caixinha, na celula da ponta
PEDAL_PORTAS = [((-0.1875, -0.1875, 0.1875), 4, 1)]   # 0.12.1: uma saida so (-1 esquerdo .. +1 direito), parede de tras; 0.19.2 (Andre): na celula do canto
BEACON_PORTA = ((-0.0625, -0.0625, 0.0625), 1, 2)
# 0.20.0 tinha uma entrada de dados aqui (liga/desliga o beacon); desfeito na mesma noite (Andre lembrou do Power Blocker do jogo).
# 0.19.14: o repo ainda estava com a porta a mais — voltou pra so a energia (igual ao bundle que esta no jogo desde a v65).
BEACON_PORTAS = [BEACON_PORTA]
S_ALAVANCA, S_CAPSULA, S_DOUBLE = -1362781425, 1452482761, -1393867716   # EPC_Actionable_Lever (joystick), DOTSColliderCapsule, EPC_Actionable_DoubleLever (alavanca comum)
S_KNOB = 1996836204   # 0.17.0: EPC_Actionable_Knob
S_TOGGLE = 762861932   # 0.23.0: EPC_Actionable_Toggle (switch de 2 posicoes: Input Switch do Datameter, botao de re do mini propulsor)
S_SEAT, S_COL_ESFERA, S_BOTAO = 738011195, -581501808, 98624692   # + EPC_Actionable_Button   # EPC_SCSeat (raiz do seat do jogo), DOTSColliderSphere (_center, _radius)


# ------------------------------------------------------------------ turret seat (0.12.0)
# Bloco proprio (modelos_v2.turret: anel, arco, placas, calota, gatilhos) com o SC_Seat do jogo DENTRO: os GameObjects do seat
# (Piston, Actionable Seat Upper + Upper Paintable, Actionable Lever) sao copiados do prefab do jogo, deslocados de TURRET_SEAT,
# e o script da raiz vira EPC_SCSeat (mesmos campos do EPC + _upperTransform/_pistonTransform/_actionableLever) — sentar, bindar,
# altura, tudo do jogo. A chapa "Lower" e os colisores de caixa do jogo saem (calota + colisor esfera no lugar). Cola pela base
# inteira e pelas 4 placas. 2 saidas (Mouse 1/2) na face de baixo = 2 alavancas 0..1 (gatilhos) que aparecem no menu de bind.
TURRET_MASSA = 130


def turret(nome, yaml):
    import re, modelos_v2 as mv
    assert "fileID: %d, guid: %s" % (S_SEAT, G) in yaml, "SC_Seat.prefab do jogo esperado"
    D = mv.TURRET_SEAT
    docs, nomes = _docs(yaml)
    # ids do seat do jogo: raiz, transform da raiz, e os MBs que a raiz referencia (upper/piston/lever/paintable)
    raiz_mb = next(d for d in docs if "m_Script: {fileID: %d, guid" % S_SEAT in d)
    ref = lambda campo: int(re.search(r"%s: \{fileID: (\d+)" % campo, raiz_mb).group(1))
    upper_mb, piston_mb, lever_mb = ref("_upperTransform"), ref("_pistonTransform"), ref("_actionableLever")
    pint_jogo = [int(x) for x in re.findall(r"  - \{fileID: (\d+)\}", re.search(r"_paintableRenderers:\n((?:  - \{fileID: \d+\}\n)+)", raiz_mb).group(1))]
    raiz_go = int(re.search(r"m_GameObject: \{fileID: (\d+)", raiz_mb).group(1))
    raiz_tr = None
    for d in docs:
        m = re.match(r"!u!4 &(\d+)", d)
        if m and "m_GameObject: {fileID: %d}" % raiz_go in d: raiz_tr = int(m.group(1))
    tirar_go = {g for g, n in nomes.items() if n in ("Lower", "JointsArea") or n.startswith("Collider")} | {str(raiz_go)}
    # --- 1) o bloco proprio
    hx, hy, hz = [b / 2 for b in mv.TURRET_BOUNDS]; px, py, pz = mv.TURRET_PIVO; a, b = mv.TURRET_PLACA_LADO, mv.TURRET_PLACA_TOPO
    # 0.12.6: bloco 2,5 x 2,5 x 1,5 = 5 x 5 x 3 frame quarters. Cola pela chapa da base (1,5 x 1,5 = 3 x 3 quarters do meio do fundo) e
    # pelas 4 placas, cada uma = o quarter do meio da sua face (lados x = +-1,25, topo y = 1,25, tras z = -0,75)
    hb = mv.TURRET_BASE_LADO / 2
    areas = [(0, [(hb, -hy, -hz), (-hb, -hy, -hz), (-hb, -hy, hz), (hb, -hy, hz)]),
             (0, [(-hx, py - a, pz - b), (-hx, py + a, pz - b), (-hx, py + a, pz + b), (-hx, py - a, pz + b)]),
             (0, [(hx, py - a, pz - b), (hx, py + a, pz - b), (hx, py + a, pz + b), (hx, py - a, pz + b)]),
             (0, [(px + a, hy, pz - b), (px - a, hy, pz - b), (px - a, hy, pz + b), (px + a, hy, pz + b)]),
             (0, [(px - a, py - a, -hz), (px + a, py - a, -hz), (px + a, py + a, -hz), (px - a, py + a, -hz)])]
    extra = "  _upperTransform: {fileID: %d}\n  _pistonTransform: {fileID: %d}\n  _actionableLever: {fileID: %d}\n" % (upper_mb, piston_mb, lever_mb)
    yb0, yb1 = mv.TURRET_BASE_Y
    txt = prefab(nome, mv.TURRET_BOUNDS, TURRET_MASSA, areas, mv.TURRET_PORTAS, script=S_SEAT, extra_raiz=extra,
                 colisor_caixa=[((0, (yb0 + yb1) / 2, 0), (mv.TURRET_BASE_LADO, yb1 - yb0, mv.TURRET_BASE_LADO))],
                 pintaveis_extra=[x for x in pint_jogo if nomes.get(_go_do(docs, x), "") in ("Piston", "Upper Paintable")], pintar_extras=("MovelPint",))
    meu_tr = fid(nome, "tr")
    # --- 2) os docs do seat do jogo (menos raiz, Lower, JointsArea e colisores da raiz), deslocados e pendurados na minha raiz
    copiar = []; filhos_tr = []
    for d in docs:
        m = re.match(r"!u!(\d+) &(\d+)", d)
        if not m: continue
        tipo, fid_ = m.group(1), m.group(2)
        go = fid_ if tipo == "1" else re.search(r"m_GameObject: \{fileID: (\d+)", d).group(1)
        if go in tirar_go: continue
        if tipo == "4" and ("m_Father: {fileID: %d}" % raiz_tr) in d:
            d = d.replace("m_Father: {fileID: %d}" % raiz_tr, "m_Father: {fileID: %d}" % meu_tr); filhos_tr.append(int(fid_))
        if tipo == "114":
            sc = int(re.search(r"m_Script: \{fileID: (-?\d+)", d).group(1))
            n = nomes.get(go, "")
            if sc == S_REND and n in ("Piston", "Actionable Seat Upper", "Actionable Lever"):
                d = _desloca_v(d, "_position", D)
            if sc == S_REND and n == "Actionable Lever":
                d = _poe_v(d, "_scale", (0, 0, 0))   # 0.12.2: alavanca de altura escondida (seat fixo no meio)
            if sc == S_ALAVANCA:
                for campo in ("_positionVerMin", "_positionVerMax"): d = _desloca_v(d, campo, D)
                d = _poe_v(_poe_v(d, "_rotationVerMin", (0, 0, 0)), "_rotationVerMax", (0, 0, 0))
            if sc == S_CAPSULA and n == "Actionable Lever":
                d = _poe_v(_poe_v(d, "_vertexA", (0, 0, 0)), "_vertexB", (0, 0.0001, 0)); d = re.sub(r"_radius: \S+", "_radius: 0.0001", d, count=1)
            if sc == S_COL and n == "Actionable Seat Upper" and "_scale:\n    x: 0.62" in d:
                d = _poe_v(_poe_v(d, "_scale", (0.01, 0.01, 0.01)), "_offset", (0, 0.07, -0.02))   # 0.12.2: sem colisor do encosto (fica parado enquanto o desenho gira e tapava os cliques)
        copiar.append(d)
    # pendura os transforms do seat na minha raiz
    txt = re.sub(r"(--- !u!4 &%d\nTransform:.*?m_Children:)((?:\n  - \{fileID: \d+\})*)" % meu_tr,
                 lambda m: m.group(1) + m.group(2) + "".join("\n  - {fileID: %d}" % f for f in filhos_tr), txt, count=1, flags=re.S)
    txt = txt.rstrip("\n") + "\n--- " + "\n--- ".join(copiar)
    return txt if txt.endswith("\n") else txt + "\n"


def _go_do(docs, mb):
    import re
    for d in docs:
        if re.match(r"!u!114 &%d\b" % mb, d): return re.search(r"m_GameObject: \{fileID: (\d+)", d).group(1)
    return ""


def _desloca_v(txt, campo, dv, ind="  "):
    import re
    def f(m):
        x, y, z = float(m.group(2)), float(m.group(4)), float(m.group(6))
        return m.group(1) + fmt(x + dv[0]) + m.group(3) + fmt(y + dv[1]) + m.group(5) + fmt(z + dv[2])
    return re.sub(r"(%s%s:\n%s  x: )(\S+)(\n%s  y: )(\S+)(\n%s  z: )(\S+)" % (ind, campo, ind, ind, ind), f, txt, count=1)


def _poe_v(txt, campo, v, ind="  "):
    import re
    return re.sub(r"(%s%s:\n%s  x: )\S+(\n%s  y: )\S+(\n%s  z: )\S+" % (ind, campo, ind, ind, ind),
                  lambda m: m.group(1) + fmt(v[0]) + m.group(2) + fmt(v[1]) + m.group(3) + fmt(v[2]), txt, count=1)


def _docs(yaml):
    import re
    docs = yaml.split("\n--- ")
    nomes = {}
    for d in docs:
        m = re.match(r"!u!1 &(\d+)", d)
        if m: nomes[m.group(1)] = re.search(r"m_Name: (.*)", d).group(1)
    return docs, nomes


def _raiz(d, nome, categorias, icone):
    import re
    d = re.sub(r"_mirroredVersion: \{[^}]*\}", "_mirroredVersion: {fileID: 0}", d, count=1)
    d = re.sub(r"_availableAmount: \S+", "_availableAmount: 500", d, count=1)
    d = re.sub(r"_categories: \S+", "_categories: %d" % categorias, d, count=1)
    d = re.sub(r"_iconTexture2D: \{fileID: \d+, guid: \w+", "_iconTexture2D: {fileID: 2800000, guid: %s" % icone, d, count=1)
    return d


def _malha(d, guid):
    import re
    d = re.sub(r"_mesh: \{fileID: \d+, guid: \w+", "_mesh: {fileID: 4300000, guid: %s" % guid, d, count=1)
    return re.sub(r"_material: \{fileID: \d+, guid: \w+", "_material: {fileID: 2100000, guid: %s" % MAT, d, count=1)


# ------------------------------------------------------------------ 0.12.14: versao espelhada (V na garagem)
# Igual o jogo faz (ex.: SC_LargeElectricThruster M): um prefab "<nome> M" com tudo espelhado em X nos DADOS do componente —
# renderers com escala X negativa (a malha sai espelhada de verdade), posicoes x -> -x, rotacoes (x, y, z) -> (x, -y, -z),
# portas x -> -x e direcao +X <-> -X, areas de cola x -> -x. As Transforms ficam (o jogo le os dados dos componentes).
# O link base -> M e' feito em runtime pelo plugin (Espelhos.cs) com as instancias registradas no Components Framework.
# Os que ja sao simetricos (ou dependem de pecas do jogo) apontam pra si mesmos: pedal, volante, turret seat, tanques.
ESPELHAR = ["SC_Pistao", "SC_MiniPistao", "SC_MiniBearing", "SC_Bearing", "SC_MotorP", "SC_MotorM", "SC_MotorG",   # 0.20.0: + pistao unico, minis
            "SC_MotorCombP", "SC_MotorCombM", "SC_MotorCombG", "SC_Recoupler", "SC_Differential", "SC_ShaftReverser",
            "SC_SingleCellBattery", "SC_LongRangeTransmitter", "SC_SolarPanelMini", "SC_Beacon"]   # 0.19.0; 0.19.2: beacon + [n for n, _, _ in RODAS]
S_COL_CONV_ = -1838397108
NAO_ESPELHAR = ["ce137a3da99fda244ac2396b5fcd1c7c", M_IND_BATERIA]   # malhas que a versao M mantem (tela do Wireless Transmitter, igual a M do jogo; 0.19.14: barra de carga da bateria — o jogo escala x de 0 a 1 a partir da posicao, espelhada ia parar fora do bloco, igual a M do jogo nao espelha)


def _neg(v):
    v = v.strip()
    if v in ("0", "-0"): return "0"
    return v[1:] if v.startswith("-") else "-" + v


def espelhar(txt, nome):
    import re
    docs = re.split(r"\n(?=--- !u!)", txt)
    out = []
    for d in docs:
        m = re.search(r"m_Script: \{fileID: (-?\d+)", d)
        sc = int(m.group(1)) if m else None
        if d.startswith("--- !u!1 ") and re.search(r"\n  m_Name: %s\n" % re.escape(nome), d):
            d = d.replace("\n  m_Name: %s\n" % nome, "\n  m_Name: %s M\n" % nome, 1)
        if sc == S_REND and any(g in d for g in NAO_ESPELHAR):
            pass   # 0.19.0: tela do Wireless Transmitter: o jogo tambem nao espelha (o numero do canal fica igual)
        elif sc == S_REND:
            d = re.sub(r"(\n  _position:\n    x: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)), d, count=1)
            d = re.sub(r"(\n  _rotation:\n    x: [^\n]+\n    y: )([^\n]+)(\n    z: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)) + k.group(3) + _neg(k.group(4)), d, count=1)
            d = re.sub(r"(\n  _scale:\n    x: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)), d, count=1)
        elif sc in (S_COL, S_COL_CONV_):
            d = re.sub(r"(\n  _offset:\n    x: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)), d, count=1)
            d = re.sub(r"(\n  _rotation:\n    x: [^\n]+\n    y: )([^\n]+)(\n    z: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)) + k.group(3) + _neg(k.group(4)), d, count=1)
        elif sc == S_AREA:
            d = re.sub(r"(\n  - x: )([^\n]+)", lambda k: k.group(1) + _neg(k.group(2)), d)
        elif sc in (S_EPC, S_PIPE) or (sc is not None and "\n  _electricPorts:" in d):   # 0.19.0: + raiz de bateria/painel (outros scripts com portas)
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
    assert "m_Name: %s M\n" % nome in t, nome
    return t


if __name__ == "__main__":
    pasta = sys.argv[1] if len(sys.argv) > 1 else "Content/Prefabs"
    os.makedirs(pasta, exist_ok=True)
    # 0.9.0: 1a passada so pra saber as portas (icones), depois malhas+icones (gen_meshes) e os prefabs de verdade
    jogo = sys.argv[2] if len(sys.argv) > 2 else None   # pasta Assets/Game/GameObject do workspace do Modkit (pros tanques)
    content = os.path.dirname(os.path.abspath(pasta.rstrip("/\\")))
    if jogo:
        import modelos_v2; modelos_v2.MESH_JOGO = os.path.join(os.path.dirname(jogo.rstrip("/\\")), "Mesh")
    blocos()
    import gen_meshes
    REF.update(gen_meshes.gerar(content, PORTAS_ICONE, os.path.join(os.path.dirname(jogo.rstrip("/\\")), "Mesh") if jogo else None))
    todos = blocos()
    if jogo:
        for nome, tipo, arq in TANQUES: todos[nome] = tanque(nome, tipo, open(os.path.join(jogo, arq), encoding="utf-8").read())
        todos["SC_TurretSeat"] = turret("SC_TurretSeat", open(os.path.join(jogo, "SC_Seat.prefab"), encoding="utf-8").read())
        todos["SC_DriveShaft"] = driveshaft("SC_DriveShaft", open(os.path.join(jogo, "SC_PlasmaCable.prefab"), encoding="utf-8").read())
        todos["SC_ShaftTransmitter"] = transmissor("SC_ShaftTransmitter", open(os.path.join(jogo, "SC_WirelessTransmitter.prefab"), encoding="utf-8").read())
        todos["SC_MiniElectricThruster"] = mini_propulsor("SC_MiniElectricThruster", open(os.path.join(jogo, "SC_SmallElectricThruster.prefab"), encoding="utf-8").read())
        todos["SC_MiniElectricThruster M"] = mini_propulsor("SC_MiniElectricThruster", open(os.path.join(jogo, "SC_SmallElectricThruster M.prefab"), encoding="utf-8").read(), espelho=True)
        todos["SC_SolarPanelMini"] = solar_mini("SC_SolarPanelMini", open(os.path.join(jogo, "SC_SolarPanel.prefab"), encoding="utf-8").read())   # 0.19.0
        todos["SC_ShaftTransmitter M"] = transmissor("SC_ShaftTransmitter", open(os.path.join(jogo, "SC_WirelessTransmitter M.prefab"), encoding="utf-8").read(), espelho=True)
    import hashlib
    for nome in ESPELHAR:
        if nome in todos: todos[nome + " M"] = espelhar(todos[nome], nome)
    # 0.22.5: _boundsCollider = casca convexa do modelo + areas de cola (normal e espelhada)
    import numpy as np
    from scipy.spatial import ConvexHull
    from geo import Malha
    mods = gen_meshes.modelos(PORTAS_ICONE)
    cont = os.path.dirname(os.path.abspath(pasta.rstrip("/\\")))
    for nome in BOUNDS_BLOCOS:
        if nome not in todos or nome not in mods: print("bounds: falta", nome); continue
        import modelos_v2
        ci = modelos_v2.com_extras(mods[nome]); pts = [v for m in (ci, mods[nome][1]) for t in m.tris for v in t[:3]]
        if nome.startswith("SC_Tanque"): pts += [v for poly in TANQUE_COLA for v in poly]
        else: pts += [v for poly in AREAS_COLA.get(nome, []) for v in poly]
        P = np.array(pts, float)
        if nome == "SC_TurretSeat":
            # 0.22.7 (Andre: portas laterais da base inacessiveis): a casca ia da quina de baixo da base (x 0,75) ate a placa (x 1,25) e
            # cobria 25% da celula do cabo ao lado da base. Na faixa da base a casca comeca em x +-0,625: a celula do cabo fica livre e a
            # borda da base continua 1/3 dentro (nada monta ali). So o volume muda; modelo, cola e portas ficam iguais.
            m = P[:, 1] < -1.1; P[m, 0] = np.clip(P[m, 0], -0.625, 0.625)
        for suf, sx in (("", 1.0), (" M", -1.0)):
            Q = P * np.array([sx, 1, 1]); hull = ConvexHull(Q); m = Malha(); cen = Q.mean(axis=0)
            for a, b, c in hull.simplices: m.tri(Q[a], Q[b], Q[c], (128, 128, 128, 0), centro=cen)
            n = nome.replace(" ", "") + ("_BoundsM" if sx < 0 else "_Bounds")
            open(os.path.join(cont, "Meshes", n + ".asset"), "w", newline="\n").write(gen_meshes.asset(n, m))
            open(os.path.join(cont, "Meshes", n + ".asset.meta"), "w", newline="\n").write(gen_meshes.META_MESH % gen_meshes.guid(n))
            alvo = nome + suf
            if alvo not in todos: continue
            assert todos[alvo].count("_boundsCollider: {fileID: 0}") == 1, "boundsCollider em " + alvo
            todos[alvo] = todos[alvo].replace("_boundsCollider: {fileID: 0}", "_boundsCollider: {fileID: 4300000, guid: %s, type: 2}" % gen_meshes.guid(n), 1)
        print("  %s: bounds = casca convexa (%d faces)" % (nome, len(ConvexHull(P).simplices)))
    # 0.12.15: malhas do jogo -> marcadores proprios (o plugin religa pela malha do jogo em runtime)
    for nome in list(todos):
        t = todos[nome]
        for g, n in gen_meshes.MALHAS_JOGO.items(): t = t.replace("guid: %s, type: 2}" % g, "guid: %s, type: 2}" % gen_meshes.guid_marcador(n))
        todos[nome] = t
    for nome, txt in todos.items():
        with open(os.path.join(pasta, nome + ".prefab"), "w", newline="\n") as f: f.write(txt)
        meta = os.path.join(pasta, nome + ".prefab.meta")
        if not os.path.exists(meta):
            g = hashlib.md5(("mecanica|prefab|" + nome).encode()).hexdigest() if nome.endswith(" M") else uuid.uuid4().hex
            with open(meta, "w", newline="\n") as f:
                f.write("fileFormatVersion: 2\nguid: %s\nPrefabImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n" % g)
        print(nome, len(txt), "bytes")
