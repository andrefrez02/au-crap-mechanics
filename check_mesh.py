#!/usr/bin/env python3
"""Z-fighting checker: looks for pairs of COPLANAR triangles, with the SAME normal, that overlap (area > 0).
Two triangles like that are drawn on top of each other in the same plane and the game doesn't know which one to show (the surface
"flickers"). Coplanar triangles with opposite normals are not a problem (one of the two is discarded by backface culling).

Usage: python3 check_mesh.py            -> checks every part of models_v2 (body + paintable + port plugs)
     python3 check_mesh.py SC_MotorMedium  -> only those"""
import math, sys, os
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def _clip(poly, a, b):
    """Sutherland-Hodgman: keeps the left side of the edge a->b (2D polygons, counter-clockwise)."""
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


def overlaps(tris, origin=None, plane_tol=2e-4, area_min=2e-6):
    """tris: [(a, b, c, ...)] in numpy. origin: parallel list with one label per triangle (only for printing).
    Returns [(i, j, area)] of the coplanar pairs (same normal) that overlap."""
    groups_ = {}
    info = []
    for i, t in enumerate(tris):
        a, b, c = np.array(t[0], float), np.array(t[1], float), np.array(t[2], float)
        n = np.cross(b - a, c - a); L = np.linalg.norm(n)
        if L < 1e-12: info.append(None); continue
        n /= L; d = float(n @ a)
        # 2D basis on the plane
        ax = np.array([1.0, 0, 0]) if abs(n[0]) < 0.9 else np.array([0, 1.0, 0])
        u = np.cross(n, ax); u /= np.linalg.norm(u); v = np.cross(n, u)
        poly = _ccw([(float(p @ u), float(p @ v)) for p in (a, b, c)])
        xs = [p[0] for p in poly]; ys = [p[1] for p in poly]
        info.append((n, d, poly, (min(xs), max(xs), min(ys), max(ys))))
        key_ = (tuple(np.round(n, 2)), round(d / plane_tol))
        groups_.setdefault(key_, []).append(i)
        # rounding d may land on neighbors: also registers it in the neighbor
        groups_.setdefault((key_[0], key_[1] + 1), []).append(i)
    found_ = {}
    for key_, idx in groups_.items():
        if len(idx) < 2: continue
        for x in range(len(idx)):
            i = idx[x]; ni, di, pi, bi = info[i]
            for y in range(x + 1, len(idx)):
                j = idx[y]
                if (min(i, j), max(i, j)) in found_: continue
                nj, dj, pj, bj = info[j]
                if abs(di - dj) > plane_tol or ni @ nj < 0.9995: continue
                if bi[1] < bj[0] or bj[1] < bi[0] or bi[3] < bj[2] or bj[3] < bi[2]: continue
                # projects j onto i's basis (the bases may differ): uses the 3D points
                a, b, c = [np.array(p, float) for p in tris[j][:3]]
                ax = np.array([1.0, 0, 0]) if abs(ni[0]) < 0.9 else np.array([0, 1.0, 0])
                u = np.cross(ni, ax); u /= np.linalg.norm(u); v = np.cross(ni, u)
                pj2 = _ccw([(float(p @ u), float(p @ v)) for p in (a, b, c)])
                clip = pi
                for k in range(3):
                    clip = _clip(clip, pj2[k], pj2[(k + 1) % 3])
                    if not clip: break
                if clip and abs(_area(clip)) > area_min:
                    found_[(min(i, j), max(i, j))] = abs(_area(clip))
    return sorted(((i, j, a) for (i, j), a in found_.items()), key=lambda t: -t[2])


def check_part(name, body, paint_, plug_=None, show=8):
    tris = []; orig_ = []
    for m, rot in ((body, "body"), (paint_, "paintable"), (plug_, "plug")):
        if m is None: continue
        for t in m.tris: tris.append(t); orig_.append(rot)
    pairs = overlaps(tris)
    print("%-18s %5d tris  %3d sobreposicoes" % (name, len(tris), len(pairs)))
    for i, j, a in pairs[:show]:
        ci = (np.array(tris[i][0]) + np.array(tris[i][1]) + np.array(tris[i][2])) / 3
        n = np.cross(np.array(tris[i][1]) - np.array(tris[i][0]), np.array(tris[i][2]) - np.array(tris[i][0])); n /= np.linalg.norm(n)
        print("   area %.2e  at (%.3f, %.3f, %.3f) normal (%.1f, %.1f, %.1f)  %s color %s  x  %s color %s" % (
            a, ci[0], ci[1], ci[2], n[0], n[1], n[2], orig_[i], tris[i][3][:3] if len(tris[i]) > 3 and tris[i][3] else "-", orig_[j], tris[j][3][:3] if len(tris[j]) > 3 and tris[j][3] else "-"))
    return pairs


if __name__ == "__main__":
    import gen_prefabs as GP, models_v2 as M
    GP.blocks()
    ports = GP.ICON_PORTS
    only = sys.argv[1:]
    mods = M.models(ports)
    plug_ = None
    try:
        sys.path.insert(0, "/tmp/claude-0/-home-claude/096e274b-1c8d-5dbc-a2f7-9d0f742a3ed8/scratchpad")
        from preview_v2 import plug_list
        plug_ = plug_list
    except Exception as ex:
        print("(without the game's plugs: %s)" % ex)
    total = 0
    for n, mod in mods.items():
        if only and n not in only: continue
        c, p = M.with_extras(mod), mod[1]
        total += len(check_part(n, c, p, plug_(ports.get(n, [])) if plug_ else None))
    print("total:", total)
