#!/usr/bin/env python3
"""Verificador de z-fighting: procura pares de triangulos COPLANARES, com a MESMA normal, que se sobrepoem (area > 0).
Dois triangulos assim sao desenhados um em cima do outro no mesmo plano e o jogo nao sabe qual mostrar (a superficie
"pisca"). Triangulos coplanares com normais opostas nao sao problema (um dos dois e' descartado pelo backface culling).

Uso: python3 checa_malha.py            -> checa todas as pecas do modelos_v2 (corpo + pintavel + plugues das portas)
     python3 checa_malha.py SC_MotorM  -> so essas"""
import math, sys, os
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def _clip(poly, a, b):
    """Sutherland-Hodgman: fica com o lado esquerdo da aresta a->b (poligonos em 2D, anti-horarios)."""
    out = []
    n = len(poly)
    for i in range(n):
        p, q = poly[i], poly[(i + 1) % n]
        sp = (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])
        sq = (b[0] - a[0]) * (q[1] - a[1]) - (b[1] - a[1]) * (q[0] - a[0])
        if sp >= 0: out.append(p)
        if (sp >= 0) != (sq >= 0):
            t = sp / (sp - sq)
            out.append((p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t))
    return out


def _area(poly):
    return 0.5 * sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1] for i in range(len(poly)))


def _ccw(poly):
    return poly if _area(poly) >= 0 else list(reversed(poly))


def sobreposicoes(tris, origem=None, tol_plano=2e-4, area_min=2e-6):
    """tris: [(a, b, c, ...)] em numpy. origem: lista paralela com um rotulo por triangulo (so pra imprimir).
    Devolve [(i, j, area)] dos pares coplanares (mesma normal) que se sobrepoem."""
    grupos = {}
    info = []
    for i, t in enumerate(tris):
        a, b, c = np.array(t[0], float), np.array(t[1], float), np.array(t[2], float)
        n = np.cross(b - a, c - a); L = np.linalg.norm(n)
        if L < 1e-12: info.append(None); continue
        n /= L; d = float(n @ a)
        # base 2D no plano
        ax = np.array([1.0, 0, 0]) if abs(n[0]) < 0.9 else np.array([0, 1.0, 0])
        u = np.cross(n, ax); u /= np.linalg.norm(u); v = np.cross(n, u)
        poly = _ccw([(float(p @ u), float(p @ v)) for p in (a, b, c)])
        xs = [p[0] for p in poly]; ys = [p[1] for p in poly]
        info.append((n, d, poly, (min(xs), max(xs), min(ys), max(ys))))
        chave = (tuple(np.round(n, 2)), round(d / tol_plano))
        grupos.setdefault(chave, []).append(i)
        # o arredondamento de d pode cair em vizinhos: registra tambem no vizinho
        grupos.setdefault((chave[0], chave[1] + 1), []).append(i)
    achados = {}
    for chave, idx in grupos.items():
        if len(idx) < 2: continue
        for x in range(len(idx)):
            i = idx[x]; ni, di, pi, bi = info[i]
            for y in range(x + 1, len(idx)):
                j = idx[y]
                if (min(i, j), max(i, j)) in achados: continue
                nj, dj, pj, bj = info[j]
                if abs(di - dj) > tol_plano or ni @ nj < 0.9995: continue
                if bi[1] < bj[0] or bj[1] < bi[0] or bi[3] < bj[2] or bj[3] < bi[2]: continue
                # projeta j na base de i (as bases podem diferir): usa os pontos 3D
                a, b, c = [np.array(p, float) for p in tris[j][:3]]
                ax = np.array([1.0, 0, 0]) if abs(ni[0]) < 0.9 else np.array([0, 1.0, 0])
                u = np.cross(ni, ax); u /= np.linalg.norm(u); v = np.cross(ni, u)
                pj2 = _ccw([(float(p @ u), float(p @ v)) for p in (a, b, c)])
                clip = pi
                for k in range(3):
                    clip = _clip(clip, pj2[k], pj2[(k + 1) % 3])
                    if not clip: break
                if clip and abs(_area(clip)) > area_min:
                    achados[(min(i, j), max(i, j))] = abs(_area(clip))
    return sorted(((i, j, a) for (i, j), a in achados.items()), key=lambda t: -t[2])


def checar_peca(nome, corpo, pint, plug=None, mostrar=8):
    tris = []; orig = []
    for m, rot in ((corpo, "corpo"), (pint, "pintavel"), (plug, "plugue")):
        if m is None: continue
        for t in m.tris: tris.append(t); orig.append(rot)
    pares = sobreposicoes(tris)
    print("%-18s %5d tris  %3d sobreposicoes" % (nome, len(tris), len(pares)))
    for i, j, a in pares[:mostrar]:
        ci = (np.array(tris[i][0]) + np.array(tris[i][1]) + np.array(tris[i][2])) / 3
        n = np.cross(np.array(tris[i][1]) - np.array(tris[i][0]), np.array(tris[i][2]) - np.array(tris[i][0])); n /= np.linalg.norm(n)
        print("   area %.2e  em (%.3f, %.3f, %.3f) normal (%.1f, %.1f, %.1f)  %s cor %s  x  %s cor %s" % (
            a, ci[0], ci[1], ci[2], n[0], n[1], n[2], orig[i], tris[i][3][:3] if len(tris[i]) > 3 and tris[i][3] else "-", orig[j], tris[j][3][:3] if len(tris[j]) > 3 and tris[j][3] else "-"))
    return pares


if __name__ == "__main__":
    import gen_prefabs as GP, modelos_v2 as M
    GP.blocos()
    portas = GP.PORTAS_ICONE
    so = sys.argv[1:]
    mods = M.modelos(portas)
    plug = None
    try:
        sys.path.insert(0, "/tmp/claude-0/-home-claude/096e274b-1c8d-5dbc-a2f7-9d0f742a3ed8/scratchpad")
        from preview_v2 import plugues
        plug = plugues
    except Exception as ex:
        print("(sem plugues do jogo: %s)" % ex)
    total = 0
    for n, mod in mods.items():
        if so and n not in so: continue
        c, p = M.com_extras(mod), mod[1]
        total += len(checar_peca(n, c, p, plug(portas.get(n, [])) if plug else None))
    print("total:", total)
