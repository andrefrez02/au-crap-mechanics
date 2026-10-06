#!/usr/bin/env python3
"""Modelos v2 (estilo do jogo: chanfros de 45 graus, paineis rebaixados, bolsos das portas com moldura octogonal, parafusos
sextavados, cabos, 8/12/16 lados). Coordenadas do bloco (centradas, Y = cima), medidas em metros. Cada funcao devolve
(corpo fixo, parte pintavel) como geo.Malha. Os plugues das portas NAO estao aqui: sao as malhas do proprio jogo
(Port Input/Output/Power), que o prefab poe na posicao da porta; aqui so fica o bolso onde eles encaixam (o plugue tem
0,07 e fica no nivel da face, 0,02 pra dentro, igual nas pecas do jogo)."""
import math, os
import numpy as np
from geo import Malha, rebaixo, rebaixo_centrado

# paleta (vertex color RGBA copiada das pecas do jogo)
PRETO, ESCURO, MEDIO, CORPO, CLARO = (17, 17, 17, 0), (32, 32, 32, 54), (80, 80, 80, 54), (107, 107, 107, 54), (172, 172, 172, 54)
METAL = (200, 200, 200, 0)
PINTA = (64, 64, 64, 20)
VERMELHO, AMARELO = (176, 47, 47, 0), (211, 190, 59, 0)
LUZ = (230, 40, 30, 54)
VERDE_LUZ = (40, 230, 70, 54)   # led do recoupler (aceso)
AMARELO_LUZ = (240, 170, 20, 54)
PISTAO_LEDS = ((-0.09, "LedVerde"), (0.0, "LedAmarelo"), (0.09, "LedVermelho"))   # x dos 3 leds de estado no painel +Z (0.12.1)
PISTAO_LED_Y, PISTAO_LED_Z = -0.135, 0.224   # escondidos (topo 1 cm abaixo do painel); o plugin empurra 2,3 cm pra fora o do estado atual
PNEU = (33, 33, 33, 54)

PORTA_LADO, PORTA_PROF, PORTA_RI, PORTA_RE = 0.112, 0.02, 0.044, 0.054
MESH_JOGO = None   # pasta Assets/Game/Mesh do workspace (o gen_prefabs poe): so pra previa/icone do turret seat


def _desloca(m, tris, d):
    for a, b, c, cor in tris: m.tris.append((a + d, b + d, c + d, cor))


# ------------------------------------------------------------------ pecas comuns
def bolso_porta(u, v):
    """Bolso quadrado raso onde o plugue do jogo encaixa (fundo escuro)."""
    return rebaixo_centrado(u, v, PORTA_LADO, PORTA_LADO, PORTA_PROF, PRETO, ESCURO)


def moldura_porta(m, eixo, sinal, plano, u, v):
    """Anel octogonal em volta do plugue, do fundo do bolso ate o nivel da face (igual a moldura do decoupler do jogo)."""
    c = [0.0, 0.0, 0.0]; eu, ev = [(1, 2), (0, 2), (0, 1)][eixo]
    c[eixo] = plano - sinal * PORTA_PROF / 2; c[eu] = u; c[ev] = v
    m.anel(eixo, c, PORTA_RI, PORTA_RE, PORTA_PROF, MEDIO, n=8, rot0=math.pi / 8, chanfro=0.004)


def portas_na_face(c, portas, eixo, sinal, plano):
    """Bolsos (pra por no caixa_chanfrada) + molduras (no corpo) das portas de uma face."""
    eu, ev = [(1, 2), (0, 2), (0, 1)][eixo]
    reb = []
    for pos, d, t in (portas or []):
        if d // 2 != eixo or (1 if d % 2 == 0 else -1) != sinal: continue
        reb.append(bolso_porta(pos[eu], pos[ev])); moldura_porta(c, eixo, sinal, plano, pos[eu], pos[ev])
    return reb


def furo_parafuso(m, centro, eixo, sinal, r=0.016):
    """'Furo' de parafuso: octogono escuro quase no nivel da superficie (0,3 mm pra fora) — nao sai da caixa do bloco."""
    c = list(centro); c[eixo] += sinal * 0.0003
    m.cilindro(eixo, c, r, 0.0006, PRETO, n=8, rot0=math.pi / 8)


def soquete_cano(c, eixo, sinal, plano, u, v):
    """Placa octogonal com furo octogonal (igual o soquete de cano do tanque do jogo), no nivel da face, dentro de um bolso raso."""
    eu, ev = [(1, 2), (0, 2), (0, 1)][eixo]
    cen = [0.0, 0.0, 0.0]; cen[eixo] = plano - sinal * 0.012; cen[eu] = u; cen[ev] = v
    perfil = [(0.055, 0), (0.09, 0), (0.09, 0.008), (0.082, 0.012), (0.055, 0.012)]
    if sinal < 0: perfil = [(r, -t) for r, t in perfil]
    c.torno(perfil, eixo, cen, CORPO, n=8, rot0=math.pi / 8, tampas=False)
    cf = list(cen); cf[eixo] = plano - sinal * 0.010
    c.cilindro(eixo, cf, 0.055, 0.004, PRETO, n=8, rot0=math.pi / 8)
    for i in range(4):
        a = math.pi / 4 + i * math.pi / 2
        q = list(cen); q[eu] = u + 0.085 * math.cos(a); q[ev] = v + 0.085 * math.sin(a); q[eixo] += sinal * 0.0005
        c.parafuso(q, eixo, sinal, 0.011, 0.007, CLARO)
    return rebaixo_centrado(u, v, 0.2, 0.2, 0.012, MEDIO, ESCURO)


# ------------------------------------------------------------------ motores
def motor(bounds=(1.0, 1.0, 1.0), comb=False, portas=None):
    """Motor de caixa bounds (x, y, z); k = largura (0,5 P / 1 M / 1,5 G) escala os detalhes. portas: [(pos, direcao, tipo)]
    do gen_prefabs (todas na face +X). Placa pintavel (bolsos das portas e, no combustivel, o soquete do cano), berco
    escuro, corpo de 16 lados com aletas, tampas com parafusos, eixo com chaveta, caixa de ligacao (eletrico) ou cabecote +
    escapamento (combustivel), cabos. 0.10.2 (form9): o P a combustivel e' 0,5 x 1 x 0,5 com as 4 portas num grupo 2x2 ao
    lado do soquete do cano (base de 2 casas); o eletrico P continua o cubo de 0,5."""
    c, p = Malha(), Malha()
    hx, hy, hz = bounds[0] / 2, bounds[1] / 2, bounds[2] / 2
    k = bounds[0]
    ct = 0.035 * k   # chanfro de cima da placa
    ys = [pos[1] for pos, d, t in (portas or []) if d == 0]
    yb = max((0.26 if comb else 0.18) * k - hy, (max(ys) + PORTA_LADO / 2 + ct + 0.008) if ys else -hy)
    if comb: yb = max(yb, -hy + 0.125 + 0.10 + ct + 0.008)   # o soquete do cano fica na placa (canto de baixo/-Z)
    room = hy - yb
    r = min(0.40 * k, hx - 0.05 * k, (room - 0.12 * k) / 2)
    # ---------------- placa da base (pintavel)
    reb_x = portas_na_face(c, portas, 0, 1, hx)
    zs = sorted(pos[2] for pos, d, t in (portas or []) if d == 0)
    if len(zs) >= 2:
        g = max((zs[i + 1] - zs[i], zs[i], zs[i + 1]) for i in range(len(zs) - 1))
        if g[0] > 0.35: reb_x.append(rebaixo(ys[0] - 0.025, ys[0] + 0.025, g[1] + 0.09, g[2] - 0.09, 0.005, CLARO, ESCURO))
    if comb:
        reb_x.append(soquete_cano(c, 0, 1, hx, -hy + 0.125, -hz + 0.125))
    ym = (-hy + yb) / 2 - 0.01 * k; alt = yb + hy
    ranh = [rebaixo_centrado(ym, z, min(0.06 * k, alt * 0.3), 0.03 * k, 0.012, PRETO, ESCURO) for z in [(-0.3 + 0.15 * i) * k for i in range(5)]]
    reb_menos_x = [rebaixo_centrado(ym, 0, min(0.09 * k, alt * 0.45), 0.72 * k, 0.006, PINTA, ESCURO, ranh)]
    if ys and yb - max(ys) > 0.20:
        # placa alta com as portas embaixo (P): grade de ventilacao na face +X acima das portas
        yg = (max(ys) + PORTA_LADO / 2 + yb - ct) / 2 + 0.01
        zg = (-hz + 0.25 + hz) / 2 if comb else 0.0
        lg = (hz - (-hz + 0.25)) - 0.08 if comb else 0.72 * k
        ranh2 = [rebaixo_centrado(yg, zg + (-0.5 + 0.25 * i) * lg * 0.8, 0.06 * k, 0.03 * k, 0.012, PRETO, ESCURO) for i in range(5)]
        reb_x.append(rebaixo_centrado(yg, zg, 0.09 * k, lg, 0.006, PINTA, ESCURO, ranh2))
    reb_baixo = [rebaixo(-hx + 0.06 * k, -0.03 * k, -hz + 0.08 * k, hz - 0.08 * k, 0.012, PINTA, ESCURO),
                 rebaixo(0.03 * k, hx - 0.06 * k, -hz + 0.08 * k, hz - 0.08 * k, 0.012, PINTA, ESCURO)]
    reb_z = [rebaixo_centrado(0, ym, 0.6 * k, min(0.07 * k, alt * 0.35), 0.006, PINTA, ESCURO)]
    p.caixa_chanfrada(-hx, hx, -hy, yb, -hz, hz, PINTA, topo=ct, base=0.006 * k, vert=0.02 * k,
                      rebaixos={"+x": reb_x, "-y": reb_baixo, "-x": reb_menos_x, "+z": reb_z, "-z": reb_z})
    # ---------------- berco (dois troncos escuros) com parafusos nas laterais
    for z in (-0.20 * k, 0.20 * k):
        c.tronco(-(r + 0.04 * k), r + 0.04 * k, z - 0.075 * k, z + 0.075 * k, yb - 0.005, yb + r * 0.85, r * 0.5, ESCURO, recuo_z=0.0)
        for x in (-(r - 0.06 * k), r - 0.06 * k):
            for sz in (-1, 1):
                c.parafuso((x, yb + 0.08 * k, z + sz * 0.075 * k), 2, sz, 0.02 * k, 0.012 * k, CLARO)
    # ---------------- corpo (pintavel): 16 lados; tampas com 6 parafusos; eixo com chaveta; bossa; 5 aletas
    cy = yb + r; L = 0.72 * k
    p.cilindro(2, (0, cy, 0), r, L, PINTA, n=16, rot0=math.pi / 16, chanfro=0.035 * k)
    for sz in (-1, 1):
        c.cilindro(2, (0, cy, sz * (L / 2 + 0.025 * k)), r * 0.83, 0.05 * k, CORPO, n=16, rot0=math.pi / 16, chanfro=0.012 * k)
        for i in range(6):
            a = math.pi / 6 + i * math.pi / 3
            c.parafuso((r * 0.67 * math.cos(a), cy + r * 0.67 * math.sin(a), sz * (L / 2 + 0.05 * k)), 2, sz, 0.018 * k, 0.012 * k, CLARO)
    ze = -(L / 2 + 0.05 * k)
    c.cilindro(2, (0, cy, ze - 0.035 * k), 0.075 * k, 0.07 * k, METAL, n=12, chanfro=0.01 * k)
    c.caixa(-0.012 * k, 0.012 * k, cy + 0.06 * k, cy + 0.085 * k, ze - 0.062 * k, ze - 0.02 * k, ESCURO)   # chaveta (mais curta que o eixo: sem face coplanar na ponta)
    c.cilindro(2, (0, cy, -ze + 0.015 * k), r * 0.42, 0.03 * k, ESCURO, n=16, rot0=math.pi / 16, chanfro=0.008 * k)
    for i in range(5):
        z = (-0.26 + 0.13 * i) * k
        c.anel(2, (0, cy, z), r - 0.02 * k, r + 0.045 * k, 0.022 * k, MEDIO, n=16, rot0=math.pi / 16)
    topo = cy + r * math.cos(math.pi / 16)
    yt = min(hy - 0.02 * k, topo + 0.10 * k)
    # ---------------- em cima
    if comb:
        # cabecote com 3 ranhuras e 4 parafusos
        c.caixa_chanfrada(-0.24 * k, 0.10 * k, topo - 0.10 * k, yt, -0.16 * k, 0.16 * k, ESCURO, c=0.015 * k,
                          rebaixos={"+y": [rebaixo(-0.20 * k, 0.06 * k, -0.12 * k, 0.12 * k, 0.008 * k, MEDIO, ESCURO,
                                                   [rebaixo_centrado(x, 0, 0.05 * k, 0.16 * k, 0.014 * k, PRETO, ESCURO) for x in (-0.15 * k, -0.07 * k, 0.01 * k)])]})
        for x in (-0.20 * k, 0.06 * k):
            for z in (-0.12 * k, 0.12 * k):
                c.parafuso((x, yt, z), 1, 1, 0.016 * k, 0.012 * k, CLARO)
        # escapamento: sai do cabecote por -X, desce e entra num silencioso deitado na placa (ao longo de Z), com duas
        # bracadeiras e a ponta cromada saindo por tras (-Z)
        ye = topo + 0.02 * k; xe = -(hx - 0.08 * k); zex = 0.06 * k; ysil = yb + 0.05 * k; rs = 0.045 * k
        c.tubo([(-0.23 * k, ye, zex), (xe, ye, zex), (xe, ysil + 0.02 * k, zex)], 0.028 * k, ESCURO, n=8)
        c.cilindro(2, (xe, ysil, -0.11 * k), rs, 0.64 * k, MEDIO, n=12, chanfro=0.01 * k)
        c.cilindro(2, (xe, ysil, -(hz - 0.045 * k)), rs * 0.75, 0.05 * k, METAL, n=12, chanfro=0.006 * k)
        c.cilindro(2, (xe, ysil, -(hz - 0.022 * k)), rs * 0.55, 0.006 * k, PRETO, n=12)
        for z in (-0.34 * k, 0.06 * k):   # bracadeiras fora da sombra dos bercos (z +-0,125k..0,275k)
            c.anel(2, (xe, ysil, z), rs, rs + 0.008 * k, 0.03 * k, ESCURO, n=12)
            c.caixa_chanfrada(xe - 0.05 * k, xe + 0.05 * k, yb - 0.005, ysil, z - 0.02 * k, z + 0.02 * k, ESCURO, c=0.006 * k)
        # placa de terminais na face +X do cabecote: os cabos saem dela
        c.caixa_chanfrada(0.10 * k, 0.13 * k, topo - 0.02 * k, topo + 0.07 * k, -0.09 * k, 0.09 * k, MEDIO, c=0.006 * k)
        xtubo = 0.12 * k
    else:
        cx, cz = 0.15 * k, 0.12 * k
        c.caixa_chanfrada(-cx, cx, topo - 0.05 * k, yt, -cz, cz, ESCURO, c=0.015 * k,
                          rebaixos={"+y": [rebaixo(-cx + 0.035 * k, cx - 0.035 * k, -cz + 0.03 * k, cz - 0.03 * k, 0.008 * k, MEDIO, ESCURO)]})
        for x in (-cx + 0.035 * k, cx - 0.035 * k):
            for z in (-cz + 0.03 * k, cz - 0.03 * k):
                c.parafuso((x, yt, z), 1, 1, 0.016 * k, 0.012 * k, CLARO)
        xtubo = cx
    # ---------------- cabos: saem da caixa no nivel do arco, abracam o corpo (entre a ultima aleta e a tampa) e descem
    # reto ate um terminal na placa
    ra = r + 0.055 * k; x1 = xtubo + 0.03 * k
    a0 = math.degrees(math.acos(min(1.0, x1 / ra)))
    ytubo = cy + ra * math.sin(math.radians(a0))
    lado_portas = 1 if (zs and sum(zs) / len(zs) > 0) else -1
    for zc in (-(L / 2 - 0.02 * k), (L / 2 - 0.02 * k)):
        if zs and zs[-1] - zs[0] <= 0.2 and (zc > 0) != (lado_portas > 0): continue   # um grupo de portas so: um cabo so, do lado delas
        pts = [(xtubo - 0.01 * k, ytubo, zc * 0.3), (x1, ytubo, zc)]
        ang = a0 - 15
        while ang > 8:
            a = math.radians(ang); pts.append((ra * math.cos(a), cy + ra * math.sin(a), zc)); ang -= 15
        pts.append((ra, cy, zc)); pts.append((ra, yb - 0.01, zc))
        c.tubo(pts, 0.02 * k, ESCURO, n=8)
        xt = min(ra, hx - 0.045 * k)   # terminal nao passa da face +X
        c.caixa_chanfrada(xt - 0.04 * k, xt + 0.04 * k, yb - 0.005, yb + 0.06 * k, zc - 0.045 * k, zc + 0.045 * k, MEDIO, c=0.008 * k)
    return c, p


# ------------------------------------------------------------------ pistao (quarter 0,5: base cola embaixo, topo solta)
PISTAO_CABECA_POS, PISTAO_HASTE_POS, PISTAO_HASTE_REPOUSO = (0, 0.225, 0), (0, 0.105, 0), 0.095   # o plugin (Juntas.cs) usa os mesmos numeros


def pistao(bandas, portas=None):
    c, p = Malha(), Malha()
    h = 0.25
    reb_x = portas_na_face(c, portas, 0, 1, h)
    reb_lado = [rebaixo_centrado(-0.135, 0, 0.13, 0.36, 0.006, PINTA, ESCURO,
                                 [rebaixo_centrado(-0.135, z, 0.09, 0.02, 0.012, PRETO, ESCURO) for z in (-0.12, -0.06, 0, 0.06, 0.12)])]
    reb_z = [rebaixo_centrado(0, -0.135, 0.36, 0.11, 0.006, PINTA, ESCURO, [rebaixo_centrado(0, -0.135, 0.3, 0.05, 0.008, MEDIO, ESCURO)])]
    p.caixa_chanfrada(-h, h, -h, -0.02, -h, h, PINTA, topo=0.02, base=0.006, vert=0.015,
                      rebaixos={"+x": reb_x, "-x": reb_lado, "+z": reb_z, "-z": reb_z,
                                "-y": [rebaixo_centrado(0, 0, 0.36, 0.36, 0.01, PINTA, ESCURO)]})
    # camisa do cilindro: octogono escuro com faixas (1/2/3 = pistao 2/4/8) e flange com 4 parafusos
    c.torno([(0.18, -0.02), (0.20, 0.0), (0.20, 0.09), (0.185, 0.105)], 1, (0, 0, 0), ESCURO, n=8, rot0=math.pi / 8)
    for i in range(bandas):
        c.anel(1, (0, 0.02 + i * 0.03, 0), 0.20, 0.212, 0.014, CLARO, n=8, rot0=math.pi / 8)
    for i in range(4):
        a = math.pi / 4 + i * math.pi / 2
        c.parafuso((0.155 * math.cos(a), 0.105, 0.155 * math.sin(a)), 1, 1, 0.014, 0.01, CLARO)
    # mangueira hidraulica: sai de um bloco na placa e entra na camisa
    c.caixa_chanfrada(0.15, 0.23, -0.03, 0.02, -0.04, 0.04, MEDIO, c=0.006)
    c.tubo([(0.19, 0.0, 0.0), (0.19, 0.06, 0.0), (0.17, 0.06, 0.0)], 0.014, ESCURO, n=8)
    # 0.11.0: haste e cabeca sao renderers SEPARADOS (o plugin estica a haste e sobe a cabeca junto com o bloco de cima):
    # cabeca = placa centrada na propria origem, posta em (0, 0,225, 0); haste = cilindro de 1 m (y 0..1) posto na boca da
    # camisa (0, 0,105, 0) com escala y = 0,095 em repouso (PostTransformMatrix no jogo)
    cab = Malha()
    cab.caixa_chanfrada(-0.2, 0.2, -0.025, 0.025, -0.2, 0.2, MEDIO, c=0.012,
                        rebaixos={"+y": [rebaixo_centrado(x, z, 0.036, 0.036, 0.006, PRETO, ESCURO) for x in (-0.15, 0.15) for z in (-0.15, 0.15)]
                                  + [rebaixo_centrado(0, 0, 0.16, 0.16, 0.004, CORPO, ESCURO)]})
    haste = Malha()
    haste.cilindro(1, (0, 0.5, 0), 0.06, 1.0, METAL, n=12)
    extras = [("Cabeca", cab, PISTAO_CABECA_POS, (1, 1, 1)), ("Haste", haste, PISTAO_HASTE_POS, (1, PISTAO_HASTE_REPOUSO, 1))]
    extras.append(knob_painel(c, (PISTAO_KNOB[0], PISTAO_KNOB[1], h - 0.006), 1))   # 0.19.2: knob do curso
    # 0.12.1 (Andre: "mostra um visual nos pistoes"): 3 leds de estado no painel +Z — verde = ativo, amarelo = guia (paralelo a
    # outro pistao), vermelho = soldado (fecharia um anel / preso por outro caminho). Escondidos; o plugin empurra o do estado.
    zf = 0.25 - 0.014   # fundo do painel rebaixado (+Z)
    for x, suf in PISTAO_LEDS:
        cor = VERDE_LUZ if suf == "LedVerde" else AMARELO_LUZ if suf == "LedAmarelo" else LUZ
        c.anel(2, (x, PISTAO_LED_Y, zf + 0.002), 0.012, 0.018, 0.004, PRETO, n=10)
        c.cilindro(2, (x, PISTAO_LED_Y, zf + 0.0005), 0.012, 0.001, PRETO, n=10)
        led = Malha(); led.cilindro(2, (0, 0, 0), 0.011, 0.016, cor, n=10, chanfro=0.002)
        extras.append((suf, led, (x, PISTAO_LED_Y, PISTAO_LED_Z), (1, 1, 1)))
    return c, p, extras


# ------------------------------------------------------------------ bearing (quarter 0,5: base cola embaixo, topo solta e gira)
BEARING_LED = (0.12, -0.16)   # (x, y) do led de trava nos paineis +Z/-Z (0.17.0; o plugin procura por essa posicao)
BEARING_KNOB = (-0.12, -0.16)   # 0.19.2: knob do angulo do servo (10..360 graus, continuo), painel +Z, do outro lado do LED
PISTAO_KNOB = (-0.12, -0.135)   # 0.19.2: knob do curso (10..100 %), painel +Z
def knob_painel(c, pos, eixo, n_marcas=0, r=0.02, h=0.016):
    """0.19.2: knob deitado num painel vertical (eixo = +1 painel +Z / -1 painel -Z): anel de marcas no painel + o knob (extra "Knob").
    0.19.3 (Andre: "o knob no bearing ta pra dentro do modelo"): o EPC_Actionable_Knob do jogo ESCREVE a rotacao do knob
    (lerp de _rotationMin a _rotationMax), entao a rotacao de repouso (90,0,0) era descartada e o knob ficava com o eixo em +Y, dentro
    do bloco. Agora a malha ja e' modelada ao longo de Z (pra fora do painel) e o jogo gira em Z: _rotationMin (0,0,-150) /
    _rotationMax (0,0,150) (gen_prefabs), sem rotacao de repouso."""
    x, y, z = pos
    c.anel(2, (x, y, z + eixo * 0.0008), r + 0.004, r + 0.011, 0.0016, ESCURO, n=16)
    knob = Malha()
    knob.cilindro(2, (0, 0, eixo * 0.007), r, h, MEDIO, n=12, chanfro=0.002)
    zt = eixo * (0.007 + h / 2)
    knob.caixa(-0.0022, 0.0022, 0.003, r - 0.0025, min(zt, zt + eixo * 0.001), max(zt, zt + eixo * 0.001), CLARO)   # risco apontando (pra cima no meio do curso)
    return ("Knob", knob, (x, y, z), (1, 1, 1), (0, 0, 0))
def bearing(portas=None):
    c, p = Malha(), Malha()
    h = 0.25
    reb_y = portas_na_face(c, portas, 1, -1, -h)   # as 4 entradas ficam na face de baixo
    lado = [rebaixo_centrado(-0.16, 0, 0.09, 0.32, 0.006, PINTA, ESCURO, [rebaixo_centrado(-0.16, z, 0.05, 0.02, 0.012, PRETO, ESCURO) for z in (-0.1, -0.05, 0, 0.05, 0.1)])]
    p.caixa_chanfrada(-h, h, -h, -0.08, -h, h, PINTA, topo=0.015, base=0.006, vert=0.015,
                      rebaixos={"-y": reb_y, "+x": lado, "-x": lado, "+z": [rebaixo_centrado(0, -0.16, 0.32, 0.09, 0.006, PINTA, ESCURO)],
                                "-z": [rebaixo_centrado(0, -0.16, 0.32, 0.09, 0.006, PINTA, ESCURO)]})
    # pista: anel externo escuro, anel interno, esferas no meio
    c.anel(1, (0, -0.045, 0), 0.20, 0.245, 0.07, ESCURO, n=16, rot0=math.pi / 16, chanfro=0.008)
    c.anel(1, (0, -0.045, 0), 0.10, 0.185, 0.07, CORPO, n=16, rot0=math.pi / 16, chanfro=0.006)
    for i in range(12):
        a = 2 * math.pi * i / 12
        c.esfera((0.1925 * math.cos(a), -0.02, 0.1925 * math.sin(a)), 0.02, CLARO, n=8, m=4)
    # disco de cima (pintavel) + tampa escura com furos de parafuso e cubo
    p.torno([(0.19, -0.01), (0.225, 0.02), (0.225, 0.20), (0.205, 0.22)], 1, (0, 0, 0), PINTA, n=16, rot0=math.pi / 16)
    c.cilindro(1, (0, 0.235, 0), 0.215, 0.03, MEDIO, n=16, rot0=math.pi / 16, chanfro=0.008)
    for i in range(6):
        a = math.pi / 6 + i * math.pi / 3
        furo_parafuso(c, (0.16 * math.cos(a), 0.25, 0.16 * math.sin(a)), 1, 1, 0.016)
    c.cilindro(1, (0, 0.2503, 0), 0.06, 0.0006, CORPO, n=8, rot0=math.pi / 8)
    setas_giro(c, 0.2255, 0.11)
    # 0.17.0 (Andre: "trava os bearings e mostra visualmente o bearing travado"): led vermelho nos paineis +Z/-Z da base (igual o do
    # recoupler): escondido 2 cm pra dentro, o plugin empurra pra fora quando a rede de eixo dele trava (sinais opostos)
    extras = []
    for sz, suf in ((1, "LedA"), (-1, "LedB")):
        zf = sz * (h - 0.006)
        c.anel(2, (sz * BEARING_LED[0], BEARING_LED[1], zf + sz * 0.002), 0.012, 0.02, 0.004, PRETO, n=8, rot0=math.pi / 8)
        c.cilindro(2, (sz * BEARING_LED[0], BEARING_LED[1], zf + sz * 0.0005), 0.012, 0.001, PRETO, n=8, rot0=math.pi / 8)
        led = Malha(); led.cilindro(2, (0, 0, 0), 0.011, 0.016, LUZ, n=8, rot0=math.pi / 8, chanfro=0.003)
        extras.append((suf, led, (sz * BEARING_LED[0], BEARING_LED[1], zf - sz * 0.02), (1, 1, 1)))
    extras.append(knob_painel(c, (BEARING_KNOB[0], BEARING_KNOB[1], h - 0.006), 1))   # 0.19.2: knob do angulo do servo
    return c, p, extras


def setas_giro(c, r, y, n=3, arco=50, larg=0.009, cor=CLARO):
    """0.12.14 (Andre: "deixa visual a questao do servo/bearing"): setinhas discretas na lateral do disco de cima, no sentido
    em que o lado de cima gira com sinal POSITIVO: anti-horario visto de cima (de +X pra +Z — no Unity, eixo Y pra cima, isso e'
    o giro negativo pela regra da mao direita, que e' o que o motor/servo da Mecanica fazem com sinal > 0). Na versao espelhada
    (V) a malha e' desenhada espelhada (escala -1 em X), entao a seta vira horaria — e o plugin inverte o giro do bearing M."""
    import numpy as _np
    rr = r + 0.003
    def P(a, yy): return (rr * math.cos(a), yy, rr * math.sin(a))
    for k in range(n):
        a0 = math.radians(k * 360.0 / n + 20); a1 = a0 + math.radians(arco); passos = 6
        for i in range(passos):
            u0 = a0 + (a1 - a0) * i / passos; u1 = a0 + (a1 - a0) * (i + 1) / passos
            c.quad(P(u0, y - larg / 2), P(u1, y - larg / 2), P(u1, y + larg / 2), P(u0, y + larg / 2), cor, centro=(0, y, 0))
        ponta = a1 + math.radians(9)
        c.tri(P(a1, y - larg * 1.6), P(a1, y + larg * 1.6), P(ponta, y), cor, centro=(0, y, 0))


# ------------------------------------------------------------------ recoupler (quarter 0,5; cola nas 6 faces; a face de baixo reacopla no decoupler)
RECOUPLER_LED = (0.12, 0.19)        # (x, y) do led nas faces +Z/-Z; escondido 2 cm dentro (repouso), pra fora quando acopla
RECOUPLER_LED_PROF = 0.02


def recoupler(portas=None):
    """Espelho do decoupler do jogo (eles ficam frente a frente): boca redonda de 16 lados embaixo, com 4 pinos grandes
    e 4 pequenos no lugar dos 4 furos grandes e 4 pequenos dele; pescoco quadrado de 0,25; tronco alargando ate a tampa
    de cima (pintaveis). A porta fica na faixa de cima, face +X (0.10.2)."""
    c, p = Malha(), Malha()
    h = 0.25
    # boca: disco de 16 lados (r 0,2415 igual o do jogo), fundo rebaixado 6 mm (preto) onde ficam os pinos, nivelados com a face
    rb, rd = 0.20, 0.2415
    c.torno([(rb, -h + 0.006), (rb, -h), (rd - 0.01, -h), (rd, -h + 0.01), (rd, -0.14), (rd - 0.01, -0.13)], 1, (0, 0, 0), CORPO,
            n=16, rot0=math.pi / 16, cores=[PRETO, CORPO, CORPO, CORPO, CORPO])
    for sx in (-1, 1):
        for sz in (-1, 1):
            c.cilindro(1, (sx * 0.10, -h + 0.003, sz * 0.10), 0.055, 0.006, CLARO, n=8, rot0=math.pi / 8)
    for ang in range(4):
        a = ang * math.pi / 2
        c.cilindro(1, (0.165 * math.cos(a), -h + 0.003, 0.165 * math.sin(a)), 0.022, 0.006, CLARO, n=6)
    # colar escuro na base do pescoco
    c.anel(1, (0, -0.125, 0), 0.15, 0.185, 0.03, ESCURO, n=16, rot0=math.pi / 16, chanfro=0.004)   # entra 1 cm na boca
    # pescoco quadrado + tronco alargando + tampa de cima (pintaveis), tampa com painel rebaixado e 4 parafusos dentro dele
    p.caixa_chanfrada(-0.125, 0.125, -0.13, -0.0075, -0.125, 0.125, PINTA, topo=0.0, base=0.0, vert=0.01)
    p.tronco(-0.125, 0.125, -0.125, 0.125, -0.0075, 0.125, -0.125, PINTA)
    # 0.10.2 (Andre): a porta fica na faixa de cima (y 0,1875), na face +X; chanfros menores nela pro bolso caber
    reb_x = portas_na_face(c, portas, 0, 1, h) + [rebaixo_centrado(0.19, -0.04, 0.06, 0.26, 0.006, MEDIO, ESCURO)]
    p.caixa_chanfrada(-h, h, 0.125, h, -h, h, PINTA, topo=0.006, base=0.0, vert=0.006,
                      rebaixos={"+y": [rebaixo_centrado(0, 0, 0.40, 0.40, 0.010, PINTA, ESCURO)],
                                "+x": reb_x,
                                "-x": [rebaixo_centrado(0.19, 0, 0.06, 0.34, 0.006, MEDIO, ESCURO)],
                                "+z": [rebaixo_centrado(0, 0.19, 0.34, 0.06, 0.006, MEDIO, ESCURO)],
                                "-z": [rebaixo_centrado(0, 0.19, 0.34, 0.06, 0.006, MEDIO, ESCURO)]})
    for sx in (-1, 1):
        for sz in (-1, 1):
            c.parafuso((sx * 0.17, h - 0.010, sz * 0.17), 1, 1, 0.016, 0.008, CLARO)
    # 4 nervuras escuras nas quinas do tronco (do colar ate a tampa)
    for sx in (-1, 1):
        for sz in (-1, 1):
            m = Malha()
            m.tronco(-0.02, 0.02, -0.02, 0.02, -0.01, 0.12, -0.02, ESCURO)
            _desloca(c, m.tris, [sx * 0.125, 0, sz * 0.125])
    # 0.11.2 (Andre: "um detalhezinho quando o recoupler e o decoupler estiverem presos"): soquete preto no painel das faces
    # +Z e -Z, com um led verde (renderer extra) que fica 2 cm pra dentro (apagado/escondido) e sai pra fora quando acopla
    lx, ly = RECOUPLER_LED
    extras = []
    for sz, suf in ((1, "LedA"), (-1, "LedB")):
        zf = sz * (h - 0.006)   # fundo do painel rebaixado
        c.anel(2, (sz * lx, ly, zf + sz * 0.002), 0.012, 0.02, 0.004, PRETO, n=8, rot0=math.pi / 8)      # anel 4 mm pra fora do painel
        c.cilindro(2, (sz * lx, ly, zf + sz * 0.0005), 0.012, 0.001, PRETO, n=8, rot0=math.pi / 8)      # "furo" 1 mm pra fora (nada coplanar)
        led = Malha(); led.cilindro(2, (0, 0, 0), 0.011, 0.016, VERDE_LUZ, n=8, rot0=math.pi / 8, chanfro=0.003)
        extras.append((suf, led, (sz * lx, ly, zf - sz * RECOUPLER_LED_PROF), (1, 1, 1)))
    return c, p, extras


# ------------------------------------------------------------------ beacon (0,25, tamanho do Wireless Transmitter; consome 5 P/s)
# 0.11.1: console em cunha igual ao transmissor; 0.11.2 (Andre): "ta muito grande, do tamanho do transmitter mesmo, so a antena
# maiorzinha, sem a ponta vermelha, sem etiqueta". Bloco 0,25; a antena sai pra cima do bloco (igual as hastes do transmissor).
BEACON_H = 0.125                     # meio bloco
BEACON_BASE_TOPO = -BEACON_H + 0.05  # topo da placa pintavel (5 cm)
BEACON_TRAS, BEACON_FRENTE = 0.09, 0.045   # altura do tampo: metade de tras reta (y TRAS), metade da frente desce ate FRENTE
BEACON_MEIO = 0.095                  # meia largura da cunha
BEACON_INCL = math.degrees(math.atan2(BEACON_TRAS - BEACON_FRENTE, BEACON_MEIO))   # inclinacao da metade da frente (graus)
BEACON_ANTENA_TOPO = 0.34            # ponta da antena (acima do bloco de 0,25: y ate 0,125 + 0,215)
BEACON_KNOB = (-0.045, BEACON_TRAS - 0.003, 0.045)   # 0.17.0: knob do icone, no rebaixo da tampa reta (lado +Z, longe da antena)
def _beacon_display():
    """ponto do display (origem do icone aceso, 0,6 mm acima do fundo do painel escuro) e a normal da tampa inclinada, no bloco."""
    i = math.radians(BEACON_INCL); sn, cs = math.sin(i), math.cos(i)
    lx, ly, lz = 0.0, -0.003 + 0.0006, -0.03                      # no referencial da tampa (antes de girar)
    x = lx * cs + ly * sn; y = -lx * sn + ly * cs                    # girar(2, -INCL)
    return (x + BEACON_MEIO / 2, y + (BEACON_TRAS + BEACON_FRENTE) / 2, lz), (sn, cs, 0.0)
BEACON_ICONE_P0, BEACON_ICONE_N = _beacon_display()
BEACON_ICONE_ESCONDE = 0.004         # icone apagado: 4 mm (+1 mm por icone) pra dentro da tampa


def _girar_z(p, graus):
    a = math.radians(graus); cs, sn = math.cos(a), math.sin(a)
    return np.array([p[0] * cs - p[1] * sn, p[0] * sn + p[1] * cs, p[2]])


def beacon(portas=None):
    c, p = Malha(), Malha()
    h = BEACON_H
    yb = BEACON_BASE_TOPO
    # base pintavel em L: placa de 5 cm + parede de tras (x -0,125..-0,095, ate y 0) com a porta de energia na face -X (a celula
    # da porta tem 0,125 de altura: precisa de parede ate y 0 pro plugue nao ficar no ar)
    xw = -BEACON_MEIO
    reb_x = portas_na_face(c, portas, 0, -1, -h)
    p.caixa_chanfrada(-h, xw, -h, 0.0, -h, h, PINTA, c=0.004, rebaixos={"-x": reb_x, "+y": [rebaixo_centrado((-h + xw) / 2, 0, 0.018, 0.21, 0.003, PINTA, ESCURO)]})
    p.caixa_chanfrada(xw, h, -h, yb, -h, h, PINTA, topo=0.006, base=0.004, vert=0.004,
                      rebaixos={"+y": [rebaixo_centrado((xw + h) / 2, 0, 0.19, 0.21, 0.003, PINTA, ESCURO)]})
    for sx in (0.108, -0.078):
        for sz in (-1, 1):
            c.parafuso((sx, yb, sz * 0.108), 1, 1, 0.007, 0.004, CLARO)
    # console: metade de tras com o tampo reto, metade da frente inclinada (Andre: "meia parte na diagonal e meia parte reto").
    # O corpo (CORPO) fica 6 mm abaixo das tampas claras pra nada ficar coplanar.
    m = BEACON_MEIO; yt, yf = BEACON_TRAS - 0.006, BEACON_FRENTE - 0.006
    xa = -m - 0.002   # face de tras do console 2 mm dentro da parede (nada coplanar com a placa)
    b = [np.array(q, float) for q in ((xa, yb - 0.01, -m), (m, yb - 0.01, -m), (m, yb - 0.01, m), (xa, yb - 0.01, m))]
    t = [np.array(q, float) for q in ((xa, yt, -m), (0, yt, -m), (m, yf, -m), (m, yf, m), (0, yt, m), (xa, yt, m))]
    cen = np.array([0, (yb + yt) / 2, 0])
    c.quad(t[0], t[1], t[4], t[5], CORPO, centro=cen); c.quad(t[1], t[2], t[3], t[4], CORPO, centro=cen)   # tampo reto + inclinado
    c.quad(b[0], b[1], b[2], b[3], CORPO, centro=cen)                                                        # fundo
    c.quad(b[0], b[1], t[2], t[0], CORPO, nrm=(0, 0, -1)); c.tri(t[0], t[2], t[1], CORPO, nrm=(0, 0, -1))   # lado -Z (pentagono)
    c.quad(b[3], b[2], t[3], t[5], CORPO, nrm=(0, 0, 1)); c.tri(t[5], t[3], t[4], CORPO, nrm=(0, 0, 1))     # lado +Z
    c.quad(b[1], b[2], t[3], t[2], CORPO, nrm=(1, 0, 0)); c.quad(b[3], b[0], t[0], t[5], CORPO, nrm=(-1, 0, 0))   # frente / tras
    # tampa reta (atras) com as bases das antenas; tampa inclinada (frente) com painel escuro, led e grade
    tampa = Malha()
    tampa.caixa_chanfrada(-m - 0.003, 0.003, BEACON_TRAS - 0.012, BEACON_TRAS, -m - 0.003, m + 0.003, CLARO, c=0.004,
                          rebaixos={"+y": [rebaixo_centrado(-0.045, 0.0, 0.07, 0.15, 0.003, CLARO, ESCURO)]})
    _desloca(c, tampa.tris, [0, 0, 0])
    L = math.hypot(m, BEACON_TRAS - BEACON_FRENTE) / 2
    tampa = Malha()
    tampa.caixa_chanfrada(-L, L + 0.003, -0.012, 0.0, -m - 0.003, m + 0.003, CLARO, c=0.004,
                          rebaixos={"+y": [rebaixo_centrado(0.0, -0.03, 0.07, 0.10, 0.003, ESCURO, ESCURO),        # painel escuro
                                           rebaixo_centrado(0.0, 0.055, 0.026, 0.026, 0.003, PRETO, ESCURO),      # led
                                           rebaixo_centrado(-0.03, 0.07, 0.024, 0.036, 0.003, MEDIO, ESCURO,
                                                            [rebaixo_centrado(-0.03, z, 0.018, 0.005, 0.003, PRETO, ESCURO) for z in (0.061, 0.07, 0.079)])]})
    tampa.cilindro(1, (0.0, 0.0, 0.055), 0.008, 0.006, LUZ, n=8, rot0=math.pi / 8)
    tampa.girar(2, -BEACON_INCL)
    _desloca(c, tampa.tris, [m / 2, (BEACON_TRAS + BEACON_FRENTE) / 2, 0])
    # antena: mastro escuro atras (lado -Z), 2 aneis, ponta clara (sem lampada)
    xm, zm = -0.05, -0.04
    y0, y1 = 0.02, BEACON_ANTENA_TOPO
    c.cilindro(1, (xm, (y0 + y1) / 2, zm), 0.01, y1 - y0, ESCURO, n=8, chanfro=0.002)
    c.cilindro(1, (xm, BEACON_TRAS + 0.012, zm), 0.018, 0.03, MEDIO, n=8, rot0=math.pi / 8, chanfro=0.003)   # base da antena
    for y in (0.16, 0.25):
        c.anel(1, (xm, y, zm), 0.01, 0.016, 0.012, MEDIO, n=8)
    c.esfera((xm, y1 + 0.012, zm), 0.014, CLARO, n=8, m=4)
    # 0.17.0 (Andre: "no displayzinho do modelo do beacon eu escolho qual icone exibir, por um knob proprio"): a segunda haste saiu;
    # no lugar, um knob do jogo (EPC_Actionable_Knob, 3 posicoes: ondas / carro / nave) com um anel de marcas em volta. O painel
    # escuro da tampa inclinada virou display: 3 icones (renderers extras) escondidos dentro da tampa; o plugin empurra o escolhido.
    kx, ky, kz = BEACON_KNOB
    c.anel(1, (kx, ky + 0.0008, kz), 0.019, 0.026, 0.0016, ESCURO, n=16)
    for a in (-60, 0, 60):   # marcas das 3 posicoes
        t = Malha(); t.caixa_girada(1, math.radians(a + 90), (0.006, 0.002, 0.0016), 0.023, CLARO); _desloca(c, t.tris, np.array([kx, ky + 0.0012, kz]))
    knob = Malha()
    knob.cilindro(1, (0, 0.007, 0), 0.016, 0.014, MEDIO, n=12, chanfro=0.002)
    knob.caixa(-0.0022, 0.0022, 0.0141, 0.0151, 0.002, 0.0145, CLARO)   # risco apontando (repouso = -Z... o jogo gira em Y)
    extras = [("Knob", knob, (kx, ky, kz), (1, 1, 1))]
    for k in range(3):
        m_ = icone_display(k)
        m_.girar(2, -BEACON_INCL)
        pos = np.array(BEACON_ICONE_P0) - np.array(BEACON_ICONE_N) * (BEACON_ICONE_ESCONDE + 0.001 * k)
        extras.append(("Icone%d" % k, m_, tuple(float(v) for v in pos), (1, 1, 1)))
    return c, p, extras


def icone_display(k):
    """0.17.0: icone do display do beacon no plano da tampa (x = descendo a rampa, z = atravessado; y = normal), centrado na origem.
    Quem olha de frente (de +x) ve 'cima' = -x e 'direita' = -z. 0 = ondas (o icone do radar), 1 = carro, 2 = nave."""
    m = Malha(); cor = VERDE_LUZ; n = (0, 1, 0)
    def P(u, v): return (-v, 0.0, -u)
    def ret(u0, u1, v0, v1): m.quad(P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1), cor, nrm=n)
    def disco(u, v, r, lados=12):
        for i in range(lados):
            a0, a1 = 2 * math.pi * i / lados, 2 * math.pi * (i + 1) / lados
            m.tri(P(u, v), P(u + r * math.cos(a0), v + r * math.sin(a0)), P(u + r * math.cos(a1), v + r * math.sin(a1)), cor, nrm=n)
    def arco(r, larg, a0, a1, passos=8):
        for i in range(passos):
            b0 = a0 + (a1 - a0) * i / passos; b1 = a0 + (a1 - a0) * (i + 1) / passos
            ri, re = r - larg / 2, r + larg / 2
            m.quad(P(ri * math.cos(b0), ri * math.sin(b0)), P(re * math.cos(b0), re * math.sin(b0)),
                   P(re * math.cos(b1), re * math.sin(b1)), P(ri * math.cos(b1), ri * math.sin(b1)), cor, nrm=n)
    if k == 0:   # ((( . )))
        disco(0, 0, 0.0055)
        for lado in (0.0, math.pi):
            for r in (0.013, 0.021):
                arco(r, 0.0035, lado - math.radians(45), lado + math.radians(45))
    elif k == 1:   # carro de lado
        ret(-0.024, 0.024, -0.004, 0.006)
        m.quad(P(-0.014, 0.006), P(0.012, 0.006), P(0.006, 0.015), P(-0.010, 0.015), cor, nrm=n)
        disco(-0.014, -0.006, 0.0062); disco(0.014, -0.006, 0.0062)
    else:   # nave (foguete pra cima)
        ret(-0.0055, 0.0055, -0.014, 0.010)
        m.tri(P(-0.0055, 0.010), P(0.0055, 0.010), P(0, 0.024), cor, nrm=n)
        m.tri(P(-0.0055, -0.014), P(-0.0055, -0.002), P(-0.015, -0.017), cor, nrm=n)
        m.tri(P(0.0055, -0.014), P(0.0055, -0.002), P(0.015, -0.017), cor, nrm=n)
        m.quad(P(-0.003, -0.019), P(0.003, -0.019), P(0.003, -0.015), P(-0.003, -0.015), LUZ, nrm=n)
    return m


# ------------------------------------------------------------------ rodas (eixo Y, largura 0,5)
def roda(diam, offroad):
    c, p = Malha(), Malha()
    R = diam / 2; W = 0.46
    # pneu: torno com sulcos na banda de rodagem e ombros chanfrados
    rp = R - (0.06 if offroad else 0.0)
    perfil = [(rp - 0.12, -W / 2), (rp - 0.035, -W / 2), (rp, -W / 2 + 0.05)]
    nb = 6; t0, t1 = -W / 2 + 0.05, W / 2 - 0.05
    for i in range(nb + 1):
        t = t0 + (t1 - t0) * i / nb
        if 0 < i < nb:
            perfil += [(rp, t - 0.012), (rp - 0.02, t - 0.012), (rp - 0.02, t + 0.012), (rp, t + 0.012)]
    # 0.19.3 (Andre: "linha transparente entre o pneu e a lataria, dos dois lados"): o furo do pneu ficava aberto por dentro, e
    # entre a borda do prato/tampa (y 0,19..0,215) e a lateral do pneu (y 0,23) dava pra ver o verso da banda. Fecha o furo.
    perfil += [(rp, t1), (rp - 0.035, W / 2), (rp - 0.12, W / 2), (rp - 0.12, -W / 2)]
    c.torno(perfil, 1, (0, 0, 0), PNEU, n=32, tampas=False)
    if offroad:   # cravos em volta, alternados
        n = max(12, int(round(2 * math.pi * rp / 0.19)))
        for i in range(n):
            lado = 1 if i % 2 == 0 else -1
            m = Malha(); m.caixa_girada(1, 2 * math.pi * i / n, (0.07, 0.085, 0.24), rp + 0.02, PNEU)
            _desloca(c, m.tris, [0, lado * 0.09, 0])
    # 0.12.14 (Andre: "as rodas terem lado certo de encaixar"): so o lado de DENTRO (-Y, o que encosta no bearing) cola.
    # Lado de fora (+Y): aro pintavel fundo (prato) com 8 raios, 6 parafusos e calota. Lado de dentro (-Y): tampa lisa
    # pintavel + flange escuro de montagem com 4 prisioneiros — da pra ver de longe qual lado vai no eixo.
    ra = rp - 0.125
    # 0.19.3: a borda do prato/tampa (16 lados) entra 3 cm no pneu (32 lados) — antes, entre as cordas dos dois poligonos, ficavam frestas
    prato = [(0.09, 0.14), (0.14, 0.14), (ra - 0.05, 0.20), (ra, 0.215), (ra + 0.03, 0.19)]
    p.torno(list(reversed(prato)), 1, (0, 0, 0), PINTA, n=16, rot0=math.pi / 16, tampas=False)   # de fora pra dentro (normal +Y)
    for i in range(8):
        m = Malha(); m.caixa_girada(1, math.pi / 8 + 2 * math.pi * i / 8, (ra - 0.16, 0.05, 0.03), 0.09 + (ra - 0.16) / 2, PINTA)
        _desloca(p, m.tris, [0, 0.185, 0])
    for i in range(6):
        a = math.pi / 6 + i * math.pi / 3
        c.parafuso((0.065 * math.cos(a), 0.22, 0.065 * math.sin(a)), 1, 1, 0.012, 0.01, CLARO)
    c.cilindro(1, (0, 0.232, 0), 0.05, 0.012, CLARO, n=12, chanfro=0.004)   # calota
    dentro = [(0.10, -0.205), (ra, -0.205), (ra + 0.03, -0.19)]
    p.torno(dentro, 1, (0, 0, 0), PINTA, n=16, rot0=math.pi / 16, tampas=False)   # tampa lisa de dentro (normal -Y)
    c.cilindro(1, (0, -0.215, 0), 0.17, 0.02, ESCURO, n=16, rot0=math.pi / 16, chanfro=0.004)   # flange de montagem
    for i in range(4):
        a = math.pi / 4 + i * math.pi / 2
        c.parafuso((0.13 * math.cos(a), -0.225, 0.13 * math.sin(a)), 1, -1, 0.014, 0.012, CLARO)
    c.cilindro(1, (0, 0, 0), 0.10, 0.44, ESCURO, n=12, chanfro=0.012)
    c.cilindro(1, (0, 0, 0), 0.035, 0.46, CLARO, n=8, chanfro=0.006)
    return c, p


# ------------------------------------------------------------------ tanques pequenos (1x2x1, Y = cima, frente = +Z)
BRANCO_TANQUE = (223, 223, 223, 108)   # cor do corpo dos tanques do jogo
LINHA_VERDE, LINHA_AZUL = (0, 121, 0, 0), (61, 138, 206, 0)


def tanque(linha):
    """Capsula de 16 lados com cupulas (igual os tanques do jogo) e duas CINTAS iguais as do jogo (medidas do Oxygen Tank
    Paintable): atras (-Z) e' uma moldura quadrada de parede fina (0,05) com chapas diagonais nos dois cantos de tras, e na
    frente (+Z) o ferro segue a geometria do cilindro (meia-lua r 0,445..0,5 que emenda nas paredes laterais em z 0).
    Cintas em y 0,5..0,75 e -0,75..-0,5, e' por elas que ele cola (face de tras). Bico octogonal do cano na cinta de baixo
    (celula x 0..0,25 / y -0,75..-0,5, sai da meia-lua ate z 0,5), linha colorida (verde/azul), costuras escuras nas
    cupulas. O medidor (com a porta de dados dentro) e a valvula sao os do proprio jogo: o prefab poe em
    (0, -0,3125, 0,5) e (-0,12, -0,625, 0,466) (na meia-lua, do lado do bico)."""
    c, p = Malha(), Malha()
    R, y0, y1, hc = 0.44, -0.70, 0.70, 0.27
    fundo = [(R * math.cos(math.radians(a)), y0 - hc * math.sin(math.radians(a))) for a in range(90, -1, -15)]
    topo = [(R * math.cos(math.radians(a)), y1 + hc * math.sin(math.radians(a))) for a in range(0, 91, 15)]
    c.torno(fundo + topo, 1, (0, 0, 0), BRANCO_TANQUE, n=16, rot0=math.pi / 16, tampas=False)
    for y in (y0, y1):
        c.anel(1, (0, y, 0), R - 0.002, R + 0.004, 0.012, ESCURO, n=16, rot0=math.pi / 16)
    c.anel(1, (0, -0.42, 0), R - 0.002, R + 0.006, 0.02, linha, n=16, rot0=math.pi / 16)
    e = 0.05   # espessura das paredes
    for ya, yb in ((-0.75, -0.5), (0.5, 0.75)):
        yc = (ya + yb) / 2
        p.caixa_chanfrada(-0.5, 0.5, ya, yb, -0.5, -0.5 + e, PINTA, vert=0.012)        # parede de tras
        p.caixa(-0.5, -0.5 + e, ya, yb, -0.5 + e, 0.0, PINTA)                            # laterais, ate onde a meia-lua emenda
        p.caixa(0.5 - e, 0.5, ya, yb, -0.5 + e, 0.0, PINTA)
        p.arco(1, (0, yc, 0), 0.5 - e - 0.005, 0.5, yb - ya, -math.pi / 2, math.pi / 2, PINTA, n=16)   # meia-lua na frente
        for ang in (3 * math.pi / 4, 5 * math.pi / 4):   # chapas diagonais so nos cantos de tras
            m = Malha(); m.caixa_girada(1, ang, (0.02, 0.40, yb - ya - 0.002), 0.46, PINTA)
            _desloca(p, m.tris, [0, yc, 0])
        for sx in (-1, 1):
            c.parafuso((sx * 0.475, yb, -0.475), 1, 1, 0.014, 0.01, CLARO)
            c.parafuso((sx * 0.33, yb, math.sqrt(0.4725 ** 2 - 0.33 ** 2)), 1, 1, 0.014, 0.01, CLARO)
    # bico octogonal do cano: sai da meia-lua da cinta de baixo ate a face (z 0,5), com o furo escuro
    zb = 0.42
    c.torno([(0.055, 0), (0.105, 0), (0.115, 0.01), (0.115, 0.5 - zb - 0.008), (0.107, 0.5 - zb), (0.055, 0.5 - zb)], 2, (0.125, -0.625, zb), CORPO, n=8, rot0=math.pi / 8, tampas=False)
    c.cilindro(2, (0.125, -0.625, 0.5 - 0.008), 0.055, 0.004, PRETO, n=8, rot0=math.pi / 8)
    return c, p


# ------------------------------------------------------------------ 0.11.2: volante (bloco proprio: base pequena atras/embaixo,
# coluna na DIAGONAL, aro inclinado perpendicular a coluna) e pedais (bloco proprio, 2 alavancas, so a acao de pisar)
VOLANTE_BASE = (-0.125, 0.125, -0.25, -0.125, 0.0, 0.25)   # 0.19.2 (Andre): caixinha 2x1x2 (0,25 x 0,125 x 0,25) no canto de baixo/tras
# 0.19.3 (Andre, desenho): a coluna sai do CENTRO DA FACE DE CIMA da caixinha, inclinada pro jogador (-Z), com o volante na ponta,
# perpendicular a ela; uma unica saida de dados, na face de tras (+Z), na celula da ponta. Sem labio.
VOLANTE_COLUNA0 = (0, -0.125, 0.125)   # onde a coluna entra na caixinha: centro da face de cima (soquete preto, igual uma porta)
VOLANTE_ELEV = 55.0                    # elevacao da coluna (graus acima da horizontal, subindo pro -Z)
VOLANTE_COLUNA_L = 0.21                # comprimento da coluna ate o cubo
VOLANTE_CUBO = (0.0, VOLANTE_COLUNA0[1] + VOLANTE_COLUNA_L * math.sin(math.radians(VOLANTE_ELEV)), VOLANTE_COLUNA0[2] - VOLANTE_COLUNA_L * math.cos(math.radians(VOLANTE_ELEV)))
VOLANTE_INCL = math.degrees(math.atan2(VOLANTE_CUBO[1] - VOLANTE_COLUNA0[1], VOLANTE_COLUNA0[2] - VOLANTE_CUBO[2]))   # inclinacao do aro (= elevacao da coluna)
PEDAL_PIVO_X, PEDAL_PIVO_Y, PEDAL_PIVO_Z = 0.12, -0.20, -0.13   # dobradicas dos 2 pedais em (+-X, Y, Z)
PEDAL_REPOUSO = 38.0                 # inclinacao do pedal em repouso (graus acima da horizontal)
PEDAL_CURSO = 24.0                   # pisar = gira PEDAL_CURSO graus pra baixo (0..1, so uma acao)
PEDAL_BASE = (-0.25, 0.25, -0.25, -0.12, 0.0, 0.25)   # caixa das saidas (metade de tras do bloco)


def volante(portas=None):
    """0.19.3 (Andre, desenho): caixinha pintavel 2x1x2 no canto de baixo/tras; a coluna escura sai do centro da face de cima por um
    soquete preto (cara de porta), inclinada VOLANTE_ELEV graus pro jogador (-Z); o aro (alavanca do jogo: gira em Z +-90, inclinado
    VOLANTE_INCL em X) fica na ponta, perpendicular a coluna. Uma saida de dados so, na face de tras (+Z). A malha da alavanca e'
    centrada na propria origem (plano XY, normal Z; o lado -Z e' o do jogador)."""
    c, p, alav = Malha(), Malha(), Malha()
    x0, x1, y0, y1, z0, z1 = VOLANTE_BASE
    reb_z = portas_na_face(c, portas, 2, 1, z1)
    p.caixa_chanfrada(x0, x1, y0, y1, z0, z1, PINTA, topo=0.008, base=0.006, vert=0.008, rebaixos={"+z": reb_z})
    for sx in (-1, 1):
        for sz in (-1, 1):
            c.parafuso((sx * 0.1, y1, (z0 + z1) / 2 + sz * 0.1), 1, 1, 0.007, 0.004, CLARO)
    p0, p1 = np.array(VOLANTE_COLUNA0), np.array(VOLANTE_CUBO)
    d = p1 - p0; d /= np.linalg.norm(d)
    # soquete na face de cima (preto, igual o plugue de uma porta): anel + fundo escuro (a coluna inclinada passa folgada)
    c.anel(1, (p0[0], y1 + 0.004, p0[2]), 0.042, 0.056, 0.008, PRETO, n=12, rot0=math.pi / 12)
    c.cilindro(1, (p0[0], y1 + 0.0035, p0[2]), 0.043, 0.005, ESCURO, n=12, rot0=math.pi / 12)
    c.cilindro_entre(p0 - d * 0.02, p1 - d * 0.03, 0.03, ESCURO, n=12)     # coluna (comeca dentro do soquete)
    c.cilindro_entre(p1 - d * 0.085, p1 - d * 0.06, 0.038, MEDIO, n=12)    # colar perto do cubo
    c.cilindro_entre(p1 - d * 0.035, p1 - d * 0.005, 0.045, ESCURO, n=12)  # boss atras do aro
    alav.anel(2, (0, 0, 0), 0.142, 0.175, 0.034, PNEU, n=16, rot0=math.pi / 16, chanfro=0.007)
    for ang in (0.0, math.pi, 1.5 * math.pi):
        alav.caixa_girada(2, ang, (0.125, 0.034, 0.02), 0.085, CLARO)
    alav.cilindro(2, (0, 0, 0), 0.05, 0.05, CLARO, n=12, chanfro=0.006)
    alav.cilindro(2, (0, 0, -0.026), 0.025, 0.004, PRETO, n=8, rot0=math.pi / 8)
    return c, p, [("Alavanca", alav, VOLANTE_CUBO, (1, 1, 1), (VOLANTE_INCL, 0, 0))]   # aro inclinado (perpendicular a coluna)


def _girar_x(p, graus):
    a = math.radians(graus); cs, sn = math.cos(a), math.sin(a)
    return np.array([p[0], p[1] * cs - p[2] * sn, p[1] * sn + p[2] * cs])


def pedal_capsula():
    """Capsula de clique da alavanca do pedal (relativa ao pivo, no repouso): ao longo da pastilha."""
    a = _girar_x((0, 0.018, 0.05), -PEDAL_REPOUSO); b = _girar_x((0, 0.018, 0.25), -PEDAL_REPOUSO)
    return tuple(float(v) for v in a), tuple(float(v) for v in b), 0.07


def pedal_caixa_clique():
    """0.12.2 (pedal = botao do jogo): caixa de clique ao longo da pastilha, relativa ao pivo, girada junto (offset, rot, escala)."""
    c = _girar_x((0, 0.018, 0.145), -PEDAL_REPOUSO)
    return tuple(float(v) for v in c), (-PEDAL_REPOUSO, 0, 0), (0.17, 0.05, 0.27)


def pedal(portas=None):
    """0.12.1 (Andre): uma saida so (esquerdo no fundo = -1, direito no fundo = +1) e base inclinada igual o beacon — uma cunha
    que desce pra frente, pra o pedal no fundo deitar em cima dela sem aparecer a caixa. Atras, parede pintavel com a saida (+Z).
    Na frente, trilhos baixos + travessa com as dobradicas; pedais de chao (pastilha a PEDAL_REPOUSO graus, pisar = +PEDAL_CURSO)."""
    c, p = Malha(), Malha()
    x0, x1 = -0.25, 0.25
    zp0, zp1 = 0.19, 0.25               # parede de tras (com a saida)
    yb, ytf, ytt = -0.25, -0.225, -0.10  # chao, topo da cunha na frente (z 0) e atras (z 0,19)
    reb_z = portas_na_face(c, portas, 2, 1, zp1)
    p.caixa_chanfrada(x0, x1, yb, ytt + 0.005, zp0, zp1, PINTA, topo=0.01, base=0.004, vert=0.004,
                      rebaixos={"+z": reb_z, "+y": [rebaixo_centrado(0, (zp0 + zp1) / 2, 0.40, 0.03, 0.004, PINTA, ESCURO)]})
    # cunha (pintavel): prisma com o tampo inclinado, de z 0 a zp0
    # 0.19.3 (Andre: "no pedal, entre a parte diagonal e a caixinha da ponta" transparente): a cunha parava 2 mm antes da caixa e
    # nao tinha a face de tras — pelo vao (e pelo chanfro da caixa) via-se o verso. Agora entra 4 mm na caixa e e' fechada.
    zw0, zw1 = 0.0, zp0 + 0.004
    b = [(x0 + 0.004, yb, zw0), (x1 - 0.004, yb, zw0), (x1 - 0.004, yb, zw1), (x0 + 0.004, yb, zw1)]
    t = [(x0 + 0.004, ytf, zw0), (x1 - 0.004, ytf, zw0), (x1 - 0.004, ytt, zw1), (x0 + 0.004, ytt, zw1)]
    cen = (0, (yb + ytt) / 2, (zw0 + zw1) / 2)
    p.quad(b[0], b[1], b[2], b[3], PINTA, centro=cen); p.quad(t[0], t[1], t[2], t[3], PINTA, centro=cen)
    p.quad(b[0], b[1], t[1], t[0], PINTA, centro=cen); p.quad(b[1], b[2], t[2], t[1], PINTA, centro=cen); p.quad(b[3], b[0], t[0], t[3], PINTA, centro=cen)
    p.quad(b[2], b[3], t[3], t[2], PINTA, centro=cen)
    # faixas escuras antiderrapantes no tampo inclinado (1 mm acima)
    ang = math.atan2(ytt - ytf, zw1 - zw0)
    for k in range(4):
        u = 0.03 + k * 0.045
        zc, yc = zw0 + u * math.cos(ang) + 0.0, ytf + u * math.sin(ang) + 0.002
        tira = Malha(); tira.caixa(-0.20, 0.20, -0.004, 0.004, -0.008, 0.008, ESCURO)
        tira.girar(0, -math.degrees(ang)); _desloca(c, tira.tris, np.array([0, yc, zc + 0.10]))
    for sx in (-1, 1):
        c.parafuso((sx * 0.22, ytt + 0.005, 0.22), 1, 1, 0.011, 0.006, CLARO)
    py, pz = PEDAL_PIVO_Y, PEDAL_PIVO_Z
    for sx in (-1, 1):   # trilhos: da travessa ate a cunha (1 mm acima do chao do bloco)
        c.caixa_chanfrada(sx * 0.225 - 0.018, sx * 0.225 + 0.018, -0.249, -0.23, pz + 0.05, zw0 + 0.01, ESCURO, c=0.004)
    c.caixa_chanfrada(-0.245, 0.245, -0.25, -0.215, pz - 0.05, pz + 0.05, ESCURO, c=0.004,
                      rebaixos={"+y": [rebaixo_centrado(0, pz, 0.06, 0.06, 0.003, PRETO, ESCURO)]})
    extras = []
    for sx, suf in ((-1, "AlavancaE"), (1, "AlavancaD")):
        px = sx * PEDAL_PIVO_X
        for lado in (-1, 1):   # orelhas da dobradica
            xo = px + lado * 0.095
            c.caixa_chanfrada(xo - 0.012, xo + 0.012, -0.215, py + 0.03, pz - 0.035, pz + 0.035, ESCURO, c=0.004)
        c.cilindro(0, (px, py, pz), 0.012, 0.23, CLARO, n=8)   # pino
        alav = Malha()
        alav.cilindro(0, (0, 0, 0), 0.022, 0.16, ESCURO, n=10)
        alav.caixa_chanfrada(-0.03, 0.03, -0.012, 0.012, 0.0, 0.06, ESCURO, c=0.003)
        alav.caixa_chanfrada(-0.08, 0.08, 0.008, 0.028, 0.02, 0.27, ESCURO, c=0.005,
                             rebaixos={"+y": [rebaixo_centrado(0, z, 0.13, 0.016, 0.006, PRETO, ESCURO) for z in (0.08, 0.12, 0.16, 0.20, 0.24)]})
        alav.girar(0, -PEDAL_REPOUSO)
        extras.append((suf, alav, (px, py, pz), (1, 1, 1)))
    return c, p, extras


# ------------------------------------------------------------------ turret seat (0.12.1, Andre: "igual o Turret Seat do Scrap Mechanic")
# Bloco 1,5 x 2,0 x 1,5 (3 x 4 x 3 quarters de 0,5 — alinhado ao grid dos frames). BASE quadrada PARADA (y -1..-0,5) com a saida
# na lateral de tras (-Z; o seat comeca olhando pra +Z). Em cima dela, o conjunto que GIRA em volta do pivo (centro de um quarter,
# TURRET_PIVO): seat do jogo, anel, arco de tras, apoios de braco com os gatilhos e as 4 placas de apoio de 0,5 x 0,5 (esquerda,
# direita, cima, tras) rentes as faces do bloco no repouso. O conjunto e' desenhado em volta do pivo (renderers extras postos no
# pivo); o plugin gira o desenho e o que estiver colado nas placas vira peca de fisica presa no pivo. Nada que gira passa de
# TURRET_RAIO do pivo, entao nunca bate na base.
TURRET_BOUNDS = (2.5, 2.5, 1.5)     # 0.12.6 (Andre): 5 x 5 x 3 frame quarters (0,5) — o centro cai no meio do quarter do meio
TURRET_PIVO = (0.0, 0.0, 0.0)       # centro do bloco = centro do quarter do meio de cada face
TURRET_EXTRA = (0.0, 0.01, 0.0)     # onde ficam os renderers que giram (fora da origem; malha deslocada de volta pro pivo)
TURRET_MARCA = (0.0137, 0.0271, 0.0419)   # renderer invisivel que identifica o turret (plugin: Torreta.EhTurret)
TURRET_RAIO = 1.125                      # esfera livre em volta do pivo (topo da base = pivo - 1,125)
TURRET_R, TURRET_TUBO = 1.045, 0.055     # anel redondo (0.12.4: 0,66/0,035 — tudo x1,58)
TURRET_ARCO_Z = 0.62                     # semi-eixo do arco de tras na profundidade (o bloco continua com 1,5)
TURRET_HUB = 0.15                        # raio dos cubos que ligam o anel as placas
TURRET_PLACA = 0.04                      # espessura das placas de apoio
TURRET_PLACA_LADO, TURRET_PLACA_TOPO = 0.25, 0.25   # meios-lados: TODAS as placas = 1 frame quarter (0,5 x 0,5)
TURRET_BASE_Y = (-1.25, -1.125)          # base parada, 1 casa de espessura (igual)
TURRET_BASE_LADO = 1.5                   # chapa da base 1,5 x 1,5 (igual)
TURRET_PITCH_MAX = 135.0                 # olhar pra cima ate a placa de cima ficar a ~5 cm da base (conta em 0.12.6)
# o tick do seat do jogo (SCTick_Seat) poe o Upper em y -0,775 (altura 0) ignorando o _position do prefab; o plugin
# (Torreta.AlturasDosSeats) reescreve o LocalTransform do Upper = TURRET_UPPER antes do TransformSystemGroup (garagem e simulacao).
TURRET_UPPER = (0.0, -0.85, 0.02)    # 0.12.9: o ajuste que o Andre fez no jogo (banco_ajuste_v2 = 0;-0,35;0,02) entrou no modelo
TURRET_PEDESTAL = (0.0, 0.0213, 0.0)  # 0.12.9: extra "Pedestal" (chapa + haste): o plugin move ele junto com o ajuste do banco
TURRET_LUVA = 0.14                   # luva fixa no fundo do aro onde a haste do pedestal corre (ajuste de altura sem buraco)
TURRET_SEAT = (0.0, TURRET_UPPER[1] + 0.775, TURRET_UPPER[2])   # deslocamento aplicado a todo o SC_Seat do jogo (o Upper dele fica em y -0,775)
TURRET_GATILHO = (0.36, -0.135, 0.247)   # pivo dos gatilhos (+-X, na ponta dos apoios de braco), giram em X 0..TURRET_GATILHO_CURSO
TURRET_BRACO = (0.30, 0.42, -0.27, -0.15, -0.23, 0.30)   # apoio de braco direito (x0,x1,y0,y1,z0,z1); esquerdo espelhado
TURRET_GATILHO_CURSO = 20.0
TURRET_PORTAS = []   # 0.12.2 (Andre): sem saida, sem gatilhos


def _marca():
    """0.12.5: malha minuscula (fica com escala normal, mas e' um triangulo de 1 mm escondido dentro da base)."""
    m = Malha(); m.tri(np.array([0, 0, 0.0]), np.array([0.001, 0, 0]), np.array([0, 0, 0.001]), ESCURO); return m


def _toro(m, eixo, centro, R, r, cor, n=36, k=10, ang0=0.0, ang1=2 * math.pi, tampas=False, R2=None):
    """Tubo em arco (toro parcial): circulo de raio R em volta do eixo (0/1/2) passando por centro, tubo de raio r.
    Angulo no plano perpendicular ao eixo (eixo 2: 0 = +X, pi/2 = +Y; eixo 0: 0 = +Y, pi/2 = +Z). R2: elipse (semi-eixo na 2a direcao)."""
    c = np.array(centro, float); a1, a2 = [(1, 2), (2, 0), (0, 1)][eixo]
    e = np.zeros(3); e[eixo] = 1
    Rb = R if R2 is None else R2

    def rad(u):
        q = np.zeros(3); q[a1] = Rb * math.cos(u); q[a2] = R * math.sin(u); q /= np.linalg.norm(q); return q

    def cen(u):
        q = np.zeros(3); q[a1] = R * math.cos(u); q[a2] = Rb * math.sin(u); return q

    def P(u, v): return c + cen(u) + rad(u) * (r * math.cos(v)) + e * (r * math.sin(v))
    for i in range(n):
        u0, u1 = ang0 + (ang1 - ang0) * i / n, ang0 + (ang1 - ang0) * (i + 1) / n
        for j in range(k):
            v0, v1 = 2 * math.pi * j / k, 2 * math.pi * (j + 1) / k
            nrm = rad((u0 + u1) / 2) * math.cos((v0 + v1) / 2) + e * math.sin((v0 + v1) / 2)
            m.quad(P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1), cor, nrm=nrm)
    if tampas and (ang1 - ang0) < 2 * math.pi - 1e-6:
        for ang, s in ((ang0, -1), (ang1, 1)):
            tg = np.zeros(3); tg[a1] = -math.sin(ang) * s; tg[a2] = math.cos(ang) * s
            cc = c + cen(ang)
            for j in range(k):
                v0, v1 = 2 * math.pi * j / k, 2 * math.pi * (j + 1) / k
                m.tri(cc, P(ang, v0), P(ang, v1), cor, nrm=tg)


def turret(portas=None):
    """(base parada: corpo, pintavel; extras = [Movel (escuro), MovelPint (placas), GatilhoE, GatilhoD]; previa = seat do jogo)."""
    c, p = Malha(), Malha()
    hx, hz = TURRET_BOUNDS[0] / 2, TURRET_BOUNDS[2] / 2
    hb = TURRET_BASE_LADO / 2
    yb0, yb1 = TURRET_BASE_Y
    # ---- base parada (pintavel), 1 casa de espessura, 1,5 x 1,5 no meio do fundo
    # 0.19.2 (Andre: "o turret seat e' feio — mantem as placas, so deixa mais bonito"): base com chanfro grande e um rebaixo em
    # moldura; mesa giratoria em 2 aneis (escuro + claro), coroa de parafusos, cantos com placas de fixacao
    p.caixa_chanfrada(-hb, hb, yb0, yb1, -hb, hb, PINTA, topo=0.035, base=0.01, vert=0.05,
                      rebaixos={"+y": [rebaixo_centrado(0, 0, 1.34, 1.34, 0.006, PINTA, ESCURO, [rebaixo_centrado(0, 0, 1.24, 1.24, 0.004, MEDIO, ESCURO)])]})
    c.anel(1, (0, yb1 - 0.010 + 0.014, 0), 0.36, 0.66, 0.028, ESCURO, n=48, chanfro=0.006)   # mesa giratoria (parada): anel escuro
    c.anel(1, (0, yb1 - 0.010 + 0.034, 0), 0.42, 0.58, 0.012, CLARO, n=48, chanfro=0.004)    # faixa clara por cima
    for k in range(16):
        a = 2 * math.pi * (k + 0.5) / 16
        c.parafuso((0.63 * math.cos(a), yb1 - 0.010 + 0.028, 0.63 * math.sin(a)), 1, 1, 0.011, 0.006, CLARO)
    for sx in (-1, 1):
        for sz in (-1, 1):
            # 0.19.3: as placas ficavam em 0,70 (passavam do chanfro da base e flutuavam); agora em 0,66, enterradas ate abaixo do rebaixo
            c.caixa_chanfrada(sx * 0.66 - 0.05, sx * 0.66 + 0.05, yb1 - 0.011, yb1 + 0.010, sz * 0.66 - 0.05, sz * 0.66 + 0.05, MEDIO, c=0.004)   # placas de fixacao nos cantos
            c.parafuso((sx * 0.66, yb1 + 0.010, sz * 0.66), 1, 1, 0.014, 0.007, CLARO)
    # ---- o que gira: desenhado em volta do pivo (origem = pivo). 0.12.6: bloco 2,5 x 2,5 x 1,5 (5 x 5 x 3 frame quarters de 0,5);
    # placas = o quarter do meio de cada face (0,5 x 0,5), aro redondo maior, arco de tras eliptico (a profundidade continua 1,5)
    mv0, mp0 = Malha(), Malha()
    R, rt = TURRET_R, TURRET_TUBO
    _toro(mv0, 2, (0, 0, 0), R, rt, ESCURO, n=72, k=14)                                                   # anel (plano X-Y), mais liso
    # (0.19.3: o "friso" de 0.19.2 era um toro todo DENTRO do tubo — invisivel — fora)
    _toro(mv0, 0, (0, 0, 0), R, rt, ESCURO, n=36, k=14, ang0=-math.pi, ang1=0.0, R2=TURRET_ARCO_Z)        # arco de tras (Y-Z, z < 0)
    pl = TURRET_PLACA; lim = hx; limy = TURRET_BOUNDS[1] / 2 - TURRET_PIVO[1]; limz = hz; rh = TURRET_HUB
    def hub(eixo, c0, c1):
        """cubo de ligacao: cilindro escuro mais fino + flange chanfrado onde encosta no tubo + flange onde encosta na placa"""
        # 0.19.3 (z-fighting): o cilindro do meio ia de ponta a ponta e as tampas dele coincidiam com as dos flanges; agora ele so
        # liga um flange ao outro. Os parafusos ficavam dentro da placa (invisiveis): agora na cara do flange virada pro tubo.
        d = 1 if c1 > c0 else -1
        f_in = [0.0, 0.0, 0.0]; f_in[eixo] = c0 + d * 0.03
        mv0.cilindro(eixo, tuple(f_in), rh * 1.12, 0.06, ESCURO, n=20, chanfro=0.015)        # flange no tubo
        f_out = [0.0, 0.0, 0.0]; f_out[eixo] = c1 - d * 0.025
        mv0.cilindro(eixo, tuple(f_out), rh * 1.05, 0.05, ESCURO, n=20, chanfro=0.012)       # flange na placa
        meio = [0.0, 0.0, 0.0]; meio[eixo] = (c0 + d * 0.05 + c1 - d * 0.04) / 2; comp = abs(c1 - c0) - 0.09
        mv0.cilindro(eixo, tuple(meio), rh * 0.8, comp, MEDIO, n=20, chanfro=0.012)
        for k in range(6):   # parafusos na cara do flange da placa (lado do tubo)
            a = 2 * math.pi * k / 6
            pp = [0.0, 0.0, 0.0]; a1, a2 = [(1, 2), (2, 0), (0, 1)][eixo]
            pp[eixo] = f_out[eixo] - d * 0.025; pp[a1] = rh * 0.92 * math.cos(a); pp[a2] = rh * 0.92 * math.sin(a)
            mv0.parafuso(tuple(pp), eixo, -d, 0.011, 0.006, CLARO)
    for sx in (-1, 1):   # hubs: do tubo ate a placa
        hub(0, sx * (R - rt - 0.01), sx * (lim - pl + 0.001))
    hub(1, R - rt - 0.01, limy - pl + 0.001)
    hub(2, -(TURRET_ARCO_Z - rt - 0.01), -(limz - pl + 0.001))
    a, b = TURRET_PLACA_LADO, TURRET_PLACA_TOPO   # meios-lados (a = o maior, 0,5; b = 0,25)
    for (x0, x1, y0, y1, z0, z1, eixo, sn, w, h) in ((-lim, -lim + pl, -a, a, -b, b, 0, 1, 0.4, 0.4), (lim - pl, lim, -a, a, -b, b, 0, -1, 0.4, 0.4),
                                                     (-a, a, limy - pl, limy, -b, b, 1, -1, 0.4, 0.4), (-a, a, -a, a, -limz, -limz + pl, 2, 1, 0.4, 0.4)):
        chave = ("+" if sn > 0 else "-") + "xyz"[eixo]
        cu, cv = [(y0 + y1) / 2, (x0 + x1) / 2, (x0 + x1) / 2][eixo], [(z0 + z1) / 2, (z0 + z1) / 2, (y0 + y1) / 2][eixo]
        mp0.caixa_chanfrada(x0, x1, y0, y1, z0, z1, PINTA, c=0.012, rebaixos={chave: [rebaixo_centrado(cu, cv, w, h, 0.006, PINTA, ESCURO, [rebaixo_centrado(cu, cv, w - 0.08, h - 0.08, 0.004, MEDIO, ESCURO)])]})
        for su in (-1, 1):   # parafusos nos 4 cantos da placa (lado de fora)
            for sv in (-1, 1):
                pp = [0.0, 0.0, 0.0]; a1, a2 = [(1, 2), (2, 0), (0, 1)][eixo]
                pp[eixo] = (x1, y1, z1)[eixo] if sn > 0 else (x0, y0, z0)[eixo]   # face de dentro (a de fora encosta no bloco vizinho e fica lisa)
                pp[a1] = cu + su * (w / 2 + 0.03); pp[a2] = cv + sv * (h / 2 + 0.03)
                mv0.parafuso(tuple(pp), eixo, sn, 0.012, 0.005, CLARO)
    # pedestal do assento + chapa. 0.12.9 (Andre: "fica esse pezinho dentro do modelo"): a chapa ficava na altura do modelo e o
    # banco descia com o ajuste -> a chapa atravessava o assento. Agora chapa + haste sao um extra separado (Pedestal) que o plugin
    # move junto com o banco; a haste corre dentro de uma luva fixa no fundo do aro (sobe/desce ~15 cm sem abrir buraco).
    yu = TURRET_UPPER[1] - TURRET_PIVO[1]; zu = TURRET_UPPER[2] - TURRET_PIVO[2]
    mv0.cilindro(1, (0, -R + TURRET_LUVA / 2, zu), 0.085, TURRET_LUVA, ESCURO, n=20, chanfro=0.01)    # luva (gira com o aro)
    mv0.cilindro(1, (0, -R + TURRET_LUVA - 0.009, zu), 0.098, 0.024, MEDIO, n=20, chanfro=0.006)      # colar no topo da luva (0.19.3: 3 mm acima — o topo coincidia com o da luva = z-fighting)
    pd0 = Malha()
    pd0.cilindro_entre((0, -R - 0.02, zu), (0, yu + 0.02, zu), 0.062, ESCURO, n=16)                    # haste
    pd0.cilindro(1, (0, yu - 0.05, zu), 0.08, 0.03, MEDIO, n=16, chanfro=0.006)                        # colar sob a chapa
    pd0.caixa_chanfrada(-0.20, 0.20, yu - 0.02, yu + 0.015, zu - 0.22, zu + 0.12, MEDIO, c=0.012,
                        rebaixos={"+y": [rebaixo_centrado(0, zu - 0.05, 0.30, 0.24, 0.004, MEDIO, ESCURO)]})   # chapa sob o assento
    # os extras ficam em TURRET_EXTRA (1 cm acima do pivo): o plugin nao gira filhos na origem do bloco, e o pivo agora e' a origem
    d = np.array(TURRET_PIVO, float) - np.array(TURRET_EXTRA, float)
    mv, mp = Malha(), Malha(); _desloca(mv, mv0.tris, d); _desloca(mp, mp0.tris, d)
    pd = Malha(); _desloca(pd, pd0.tris, np.array(TURRET_PIVO, float) - np.array(TURRET_PEDESTAL, float))
    extras = [("Movel", mv, TURRET_EXTRA, (1, 1, 1)), ("MovelPint", mp, TURRET_EXTRA, (1, 1, 1)),
              ("Pedestal", pd, TURRET_PEDESTAL, (1, 1, 1)),
              ("Marca", _marca(), TURRET_MARCA, (1, 1, 1))]   # 0.12.5: marca invisivel = o plugin reconhece o turret ate na garagem
    # previa: malhas do seat do jogo (so imagem/icone)
    previa = None
    if MESH_JOGO:
        previa = Malha()
        try:
            import exportar_glb
            for arq, pos, cor, rot in (("Seat Upper.asset", TURRET_UPPER, CORPO, (0, 0, 0)), ("Seat Upper Paintable.asset", TURRET_UPPER, (205, 205, 205, 20), (0, 0, 0)),
                                       ):
                caminho = os.path.join(MESH_JOGO, arq)
                if not os.path.exists(caminho): continue
                vpos, vnrm, vcor, idx = exportar_glb.ler_malha(caminho)
                P0 = np.array(pos, float)
                for k in range(0, len(idx), 3):
                    a, b, cc = [P0 + rot_unity(np.array(vpos[idx[k + i]]), rot) for i in range(3)]
                    previa.tris.append((a, b, cc, cor))
        except Exception as ex:
            print("previa do seat do jogo:", ex)
    return c, p, extras, previa


def transmissor():
    """0.16.0: Shaft Transmitter = copia do SC_WirelessTransmitter do jogo (o prefab usa as malhas do jogo, religadas em runtime).
    Aqui so a previa do icone: malhas do jogo (nao vao pro bundle), a parte pintavel em cor de ferro."""
    previa = Malha()
    if MESH_JOGO:
        try:
            import exportar_glb
            for arq, cor in (("Wireless Transmitter Body.asset", None), ("Wireless Transmitter Body Paintable.asset", (70, 72, 78, 0)),
                             ("Wireless Transmitter Indicator.asset", None), ("Wireless Transmitter Actionable.asset", None),   # 0.19.0: tela clara, igual no jogo
                             ("Wireless Transmitter Label.asset", (70, 72, 78, 0))):
                caminho = os.path.join(MESH_JOGO, arq)
                if not os.path.exists(caminho): print("previa do transmitter: falta", arq); continue
                vpos, vnrm, vcor, idx = exportar_glb.ler_malha(caminho)
                for k in range(0, len(idx), 3):
                    a, b, cc = [np.array(vpos[idx[k + i]], float) for i in range(3)]
                    previa.tris.append((a, b, cc, cor or (tuple(vcor[idx[k]]) if vcor else (120, 120, 120, 0))))
        except Exception as ex:
            print("previa do transmitter:", ex)
    return Malha(), Malha(), [], numero_tela(previa)


# ------------------------------------------------------------------ 0.18.0: Single Cell Battery (2x4x2 casas = 0,25 x 0,5 x 0,25)
# Andre: "uma bateria que ocupa so 2x4x2, parecido com a bateria pequena do jogo mas pega so uma celula dela". Modelo proprio no estilo
# da bateria pequena do jogo (cores tiradas das malhas dela): base escura, corpo amarelo, faixa escura, tampa pintavel com 1 janela.
# O indicador de carga e' a barra do jogo (Small Rechargeable Battery Indicator, renderer extra) no painel da face -Z.
BAT_AMARELO, BAT_ESC1, BAT_ESC2, BAT_TAMPA = (210, 117, 3, 108), (31, 31, 31, 0), (44, 44, 44, 0), (119, 119, 119, 20)
CELULA_INDICADOR = (-0.057, 0.0, -0.1085)   # posicao da barra (cresce em +x a partir daqui; o jogo escala x pela carga)
def celula(portas=None):
    c, p = Malha(), Malha()
    hx, hz, b = 0.125, 0.125, 0.112
    c.caixa_chanfrada(-hx, hx, -0.25, -0.215, -hz, hz, BAT_ESC2, c=0.006)                     # base
    reb_z = portas_na_face(c, portas, 2, -1, -b) + [rebaixo_centrado(0.0, 0.0, 0.13, 0.05, 0.004, BAT_ESC1, BAT_ESC2)]
    c.caixa_chanfrada(-b, b, -0.215, 0.09, -b, b, BAT_AMARELO, vert=0.006, rebaixos={"-z": reb_z,
                      "+x": [rebaixo_centrado(-0.06, z, 0.006, 0.17, 0.003, BAT_ESC1, BAT_ESC1) for z in (-0.04, 0.04)],
                      "-x": [rebaixo_centrado(-0.06, z, 0.006, 0.17, 0.003, BAT_ESC1, BAT_ESC1) for z in (-0.04, 0.04)]})
    for sx in (-1, 1):
        for sz in (-1, 1):
            e = b + 0.002   # cantoneira 2 mm pra fora do corpo (nada coplanar)
            x0, x1 = (b - 0.012, e) if sx > 0 else (-e, -b + 0.012); z0, z1 = (b - 0.012, e) if sz > 0 else (-e, -b + 0.012)
            c.caixa(x0, x1, -0.215, 0.09, z0, z1, BAT_ESC2)
    c.caixa_chanfrada(-0.118, 0.118, 0.09, 0.106, -0.118, 0.118, BAT_ESC1, c=0.003)            # faixa escura
    p.caixa_chanfrada(-0.12, 0.12, 0.106, 0.25, -0.12, 0.12, BAT_TAMPA, topo=0.02, base=0.004, vert=0.01,
                      rebaixos={"+y": [rebaixo_centrado(0.0, 0.0, 0.11, 0.11, 0.004, BAT_AMARELO, BAT_ESC2)]})
    furo_parafuso(c, (0.085, 0.25, 0.085), 1, 1, 0.009)
    return c, p


def previa_jogo(arquivos, escala=1.0, pintar=None):
    """0.18.0: previa (so icone) a partir de malhas do jogo (MESH_JOGO), com escala; pintar = {arquivo: cor} pras partes pintaveis."""
    previa = Malha()
    if not MESH_JOGO: return previa
    try:
        import exportar_glb
        for arq in arquivos:
            caminho = os.path.join(MESH_JOGO, arq)
            if not os.path.exists(caminho): print("previa: falta", arq); continue
            vpos, vnrm, vcor, idx = exportar_glb.ler_malha(caminho)
            cor = (pintar or {}).get(arq)
            for k in range(0, len(idx), 3):
                a, b, cc = [np.array(vpos[idx[k + i]], float) * np.array(escala if isinstance(escala, tuple) else (escala,) * 3, float) for i in range(3)]
                previa.tris.append((a, b, cc, cor or (tuple(vcor[idx[k]]) if vcor else (120, 120, 120, 0))))
    except Exception as ex:
        print("previa:", ex)
    return previa


def numero_tela(previa, texto="0", cor=(25, 25, 25, 0)):
    """0.19.0 (Andre: "os dois transmitters tem modelo bugado no inventario — sem um numero no displayzinho"): o icone e' desenhado aqui,
    sem o texto 3D do jogo. Desenha o numero do canal (7 segmentos) deitado na tela, onde o jogo escreve (RendererText3D: x 0,0496,
    z -0,0441, alinhado a direita/embaixo, y logo acima da tela)."""
    seg = {"0": "abcdef", "1": "bc", "2": "abged", "3": "abgcd", "4": "fgbc", "5": "afgcd", "6": "afgedc", "7": "abc", "8": "abcdefg", "9": "abcdfg"}
    h, w, t, y = 0.058, 0.032, 0.0085, -0.0150
    x1 = 0.0496
    for ci, ch in enumerate(reversed(texto)):
        xd = x1 - ci * (w + 0.008); xe = xd - w; z0 = -0.0441; z1 = z0 + h; zm = (z0 + z1) / 2
        barras = {"a": (xe, xd, z1 - t, z1), "d": (xe, xd, z0, z0 + t), "g": (xe, xd, zm - t / 2, zm + t / 2),
                  "f": (xe, xe + t, zm, z1), "e": (xe, xe + t, z0, zm), "b": (xd - t, xd, zm, z1), "c": (xd - t, xd, z0, zm)}
        for sgm in seg.get(ch, ""):
            xa, xb, za, zb = barras[sgm]
            p00, p10, p11, p01 = [np.array(v, float) for v in ((xa, y, za), (xb, y, za), (xb, y, zb), (xa, y, zb))]
            for tri in ((p00, p11, p10), (p00, p01, p11), (p00, p10, p11), (p00, p11, p01)):   # os dois lados
                previa.tris.append(tri + (cor,))
    return previa


def mini_propulsor():
    """0.18.0: Mini Electric Thruster = copia do SC_SmallElectricThruster do jogo em escala 0,5 (o prefab usa as malhas do jogo)."""
    return Malha(), Malha(), [], previa_jogo(["Small Electric Thruster Body.asset", "Small Electric Thruster Body Paintable.asset", "Small Electric Thruster Ignition.asset"],
                                             0.5, {"Small Electric Thruster Body Paintable.asset": (150, 145, 135, 0)})


def radio():
    """0.18.0: Long Range Transmitter = malhas do Wireless Transmitter do jogo (so a previa do icone)."""
    return Malha(), Malha(), [], numero_tela(previa_jogo(["Wireless Transmitter Body.asset", "Wireless Transmitter Body Paintable.asset", "Wireless Transmitter Indicator.asset",
                                                          "Wireless Transmitter Actionable.asset", "Wireless Transmitter Label.asset"],   # 0.19.0: + tela e etiqueta
                                                         1.0, {"Wireless Transmitter Body Paintable.asset": (60, 62, 68, 0), "Wireless Transmitter Label.asset": (60, 62, 68, 0)}))


def turret_capsula():
    """Capsula de clique do gatilho (relativa ao pivo dele, no repouso): ao longo do cabo."""
    return (0.0, 0.02, 0.012), (0.0, 0.10, 0.06), 0.04


# ------------------------------------------------------------------ tabela
# (caixa do eletrico, caixa do combustivel). 0.10.2 (form9): P a combustivel = 4x8x4 casas (0,5 x 1 x 0,5), eletrico P = cubo 0,5
MOTORES = {"P": ((0.5, 0.5, 0.5), (0.5, 1.0, 0.5)), "M": ((1.0, 1.0, 1.0), (1.0, 1.0, 1.0)), "G": ((1.5, 1.5, 1.5), (1.5, 1.5, 1.5))}
RODAS = {"SC_RodaSportsP": (1.0, False), "SC_RodaSportsM": (1.5, False), "SC_RodaSportsG": (2.0, False),
         "SC_RodaOffroadP": (1.0, True), "SC_RodaOffroadM": (1.5, True), "SC_RodaOffroadG": (2.0, True)}


def partes(mod):
    """(corpo, pintavel, extras) de um item de modelos(); extras = [(sufixo, malha, pos, escala)] (renderers separados)."""
    return (mod[0], mod[1], mod[2] if len(mod) > 2 else [])


def rot_unity(p, rot):
    """Aplica a rotacao Euler do Unity (x, y, z graus; ordem Z, depois X, depois Y — R = Ry.Rx.Rz) a um ponto."""
    x, y, z = rot
    q = np.array(p, float)
    for eixo, g in ((2, z), (0, x), (1, y)):
        if not g: continue
        a = math.radians(g); cs, sn = math.cos(a), math.sin(a); a1, a2 = [(1, 2), (2, 0), (0, 1)][eixo]
        r = q.copy(); r[a1] = q[a1] * cs - q[a2] * sn; r[a2] = q[a1] * sn + q[a2] * cs; q = r
    return q


def com_extras(mod):
    """Corpo com os renderers extras aplicados na posicao/rotacao/escala de repouso (pra previa, checagem e .glb).
    extras = [(sufixo, malha, pos, escala[, rot Euler Unity])]."""
    corpo, pint, extras = partes(mod)
    if not extras: return corpo
    c = Malha(); c.tris = list(corpo.tris)
    for ex in extras:
        suf, m, pos, esc = ex[:4]; rot = ex[4] if len(ex) > 4 else (0, 0, 0)
        for a, b, cc, cor in m.tris:
            c.tris.append(tuple(rot_unity(np.array(v, float) * np.array(esc, float), rot) + np.array(pos, float) for v in (a, b, cc)) + (cor,))
    return c



# ------------------------------------------------------------------ 0.15.0 Drive Shaft (Andre: "cabo proprio pro motor")
def eixo_icone():
    """So pra imagem do inventario: pedaco de eixo de aco (o cabo de verdade usa as malhas do cabo de plasma do jogo, pintadas de ferro)."""
    c, p = Malha(), Malha()
    c.cilindro(2, (0, 0, 0), 0.030, 0.25, METAL, n=12)
    for z in (-0.09, 0.0, 0.09):
        c.cilindro(2, (0, 0, z), 0.042, 0.012, CLARO, n=12, chanfro=0.003)
    c.cilindro(2, (0, 0, -0.118), 0.05, 0.014, MEDIO, n=6)
    c.cilindro(2, (0, 0, 0.118), 0.05, 0.014, MEDIO, n=6)
    return c, p


def diferencial(portas=None):
    """Differential 0,25 (2x2x2 casas): 4 entradas de eixo na face -X (0.17.0; antes 1), 4 saidas na face +X. Carcaca pintavel com tampa redonda
    de engrenagem em cima (dentes em volta) e parafusos."""
    c, p = Malha(), Malha()
    h = 0.125
    reb_mx = portas_na_face(c, portas, 0, -1, -h); reb_px = portas_na_face(c, portas, 0, 1, h)
    p.caixa_chanfrada(-h, h, -h, h, -h, h, PINTA, topo=0.012, base=0.006, vert=0.01, rebaixos={"-x": reb_mx, "+x": reb_px})
    # coroa (engrenagem) em cima: disco escuro + 16 dentes + cubo claro
    c.cilindro(1, (0, h + 0.0003, 0), 0.085, 0.0006, ESCURO, n=16, rot0=math.pi / 16)
    for i in range(16):
        a = 2 * math.pi * i / 16
        t = Malha(); t.caixa_girada(1, a, (0.016, 0.013, 0.0024), 0.083, MEDIO); _desloca(c, t.tris, np.array([0.0, h + 0.0009, 0.0]))
    c.cilindro(1, (0, h + 0.0006, 0), 0.034, 0.0012, CLARO, n=8, rot0=math.pi / 8)
    for sx in (-1, 1):
        for sz in (-1, 1):
            furo_parafuso(c, (sx * 0.1, h, sz * 0.1), 1, 1, 0.008)
    return c, p


def reversor(portas=None):
    """Shaft Reverser 0,25 x 0,125 x 0,25 (2x1x2 casas, igual os blocos de matematica): entrada na face -Z, saida na +Z. Em cima,
    duas setas curvas em sentidos contrarios (o giro sai invertido)."""
    c, p = Malha(), Malha()
    hx, hy, hz = 0.125, 0.0625, 0.125
    reb_mz = portas_na_face(c, portas, 2, -1, -hz); reb_pz = portas_na_face(c, portas, 2, 1, hz)
    p.caixa_chanfrada(-hx, hx, -hy, hy, -hz, hz, PINTA, topo=0.008, base=0.004, vert=0.008, rebaixos={"-z": reb_mz, "+z": reb_pz})
    y = hy + 0.0004; larg = 0.010
    def seta(cx, horario):
        r = 0.045; a0, a1 = (math.radians(200), math.radians(-60)) if horario else (math.radians(-20), math.radians(240))
        passos = 10
        def P(a, rr): return (cx + rr * math.cos(a), y, rr * math.sin(a))
        for i in range(passos):
            u0 = a0 + (a1 - a0) * i / passos; u1 = a0 + (a1 - a0) * (i + 1) / passos
            c.quad(P(u0, r - larg / 2), P(u1, r - larg / 2), P(u1, r + larg / 2), P(u0, r + larg / 2), CLARO, centro=(cx, y - 1, 0))
        d = math.copysign(math.radians(22), a1 - a0)
        c.tri(P(a1, r - larg * 1.8), P(a1, r + larg * 1.8), P(a1 + d, r), CLARO, centro=(cx, y - 1, 0))
    seta(-0.058, False); seta(0.058, True)
    return c, p

def modelos(portas_por_bloco):
    """portas_por_bloco: nome -> [(pos, direcao, tipo)] (vem do gen_prefabs)."""
    P = lambda n: portas_por_bloco.get(n, [])
    r = {}
    for curso, bandas in ((2, 1), (4, 2), (8, 3)): r["SC_Pistao%d" % curso] = pistao(bandas, P("SC_Pistao%d" % curso))
    r["SC_Bearing"] = bearing(P("SC_Bearing"))
    for suf, (be, bc) in MOTORES.items():
        r["SC_Motor" + suf] = motor(be, False, P("SC_Motor" + suf)); r["SC_MotorComb" + suf] = motor(bc, True, P("SC_MotorComb" + suf))
    r["SC_Recoupler"] = recoupler(P("SC_Recoupler")); r["SC_Beacon"] = beacon(P("SC_Beacon"))
    r["SC_TanqueVerde"] = tanque(LINHA_VERDE); r["SC_TanqueAzul"] = tanque(LINHA_AZUL)
    r["SC_Volante"] = volante(P("SC_Volante")); r["SC_Pedal"] = pedal(P("SC_Pedal"))
    r["SC_TurretSeat"] = turret(P("SC_TurretSeat"))
    r["SC_DriveShaft"] = eixo_icone(); r["SC_Differential"] = diferencial(P("SC_Differential")); r["SC_ShaftReverser"] = reversor(P("SC_ShaftReverser"))
    r["SC_ShaftTransmitter"] = transmissor()   # 0.16.0
    r["SC_SingleCellBattery"] = celula(P("SC_SingleCellBattery")); r["SC_MiniElectricThruster"] = mini_propulsor(); r["SC_LongRangeTransmitter"] = radio()   # 0.18.0
    r["SC_SolarPanelMini"] = (Malha(), Malha(), [], previa_jogo(["Solar Panel Body.asset", "Solar Panel Body Paintable.asset"], (0.5, 1.0, 0.25),
                                                               {"Solar Panel Body Paintable.asset": (150, 145, 135, 0)}))   # 0.19.0
    for nome, (d, off) in RODAS.items(): r[nome] = roda(d, off)
    return r
