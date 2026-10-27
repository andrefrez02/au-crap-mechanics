"""0.19.3 (Andre: "wrong face error": port pockets, tire/rim, pedal): renders each model from 8 angles; what the game
shows as transparent is a face seen from its back -> here it turns bright RED (or magenta, if it's a back "behind" nothing = a hole).
Usage: python3 check_faces.py <output_folder> [name ...]"""
import math, os, sys
import numpy as np
from PIL import Image
import gen_prefabs, gen_meshes, models_v2
from geo import Mesh


def _rot(euler):
    rx, ry, rz = [math.radians(a) for a in euler]
    cx, sx = math.cos(rx), math.sin(rx); cy, sy = math.cos(ry), math.sin(ry); cz, sz = math.cos(rz), math.sin(rz)
    Rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]]); Ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]]); Rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]])
    return Ry @ Rx @ Rz


def merge(model):
    body, paint_, extras_ = model[0], model[1], model[2] if len(model) > 2 else []
    tris = [(np.array(a), np.array(b), np.array(c)) for a, b, c, _ in body.tris] + [(np.array(a), np.array(b), np.array(c)) for a, b, c, _ in paint_.tris]
    for ex in extras_:
        name, m, pos, scl = ex[0], ex[1], np.array(ex[2], float), np.array(ex[3], float)
        R = _rot(ex[4]) if len(ex) > 4 else np.eye(3)
        for a, b, c, _ in m.tris:
            tris.append(tuple(R @ (np.array(p) * scl) + pos for p in (a, b, c)))
    return tris


def render(tris, view_, W=360):
    view_ = np.array(view_, float); view_ /= np.linalg.norm(view_)
    fwd = -view_; up0 = np.array([0, 1.0, 0]) if abs(view_[1]) < 0.95 else np.array([0, 0, 1.0])
    right = np.cross(up0, fwd); right /= np.linalg.norm(right); up = np.cross(fwd, right)
    P = np.array(tris)   # (n,3,3)
    sx, sy = P @ right, P @ up
    lo = np.array([sx.min(), sy.min()]); hi = np.array([sx.max(), sy.max()])
    scl = (W * 0.9) / max(hi - lo); off = (np.array([W, W]) - (hi - lo) * scl) / 2
    img = np.full((W, W, 3), 40, np.float32); zb = np.full((W, W), -1e9, np.float32)
    for (a, b, c), px, py in zip(tris, sx, sy):
        nrm = np.cross(b - a, c - a); L = np.linalg.norm(nrm)
        if L < 1e-12: continue
        nrm /= L
        back_side = nrm @ view_ <= 0
        light = 0.5 + 0.5 * abs(float(nrm @ np.array([0.3, 0.8, 0.5]) / 1.0))
        rgb = np.array([255, 30, 30], float) if back_side else np.array([170, 170, 170], float) * light
        X = (px - lo[0]) * scl + off[0]; Y = W - ((py - lo[1]) * scl + off[1])
        Z = np.array([a @ view_, b @ view_, c @ view_])
        x0, x1 = int(max(0, np.floor(X.min()))), int(min(W - 1, np.ceil(X.max()))); y0, y1 = int(max(0, np.floor(Y.min()))), int(min(W - 1, np.ceil(Y.max())))
        if x1 < x0 or y1 < y0: continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        d = (X[1] - X[0]) * (Y[2] - Y[0]) - (X[2] - X[0]) * (Y[1] - Y[0])
        if abs(d) < 1e-9: continue
        w1 = ((gx - X[0]) * (Y[2] - Y[0]) - (X[2] - X[0]) * (gy - Y[0])) / d
        w2 = ((X[1] - X[0]) * (gy - Y[0]) - (gx - X[0]) * (Y[1] - Y[0])) / d
        w0 = 1 - w1 - w2
        inside = (w0 >= -1e-4) & (w1 >= -1e-4) & (w2 >= -1e-4)
        z = w0 * Z[0] + w1 * Z[1] + w2 * Z[2]
        sub = zb[y0:y1 + 1, x0:x1 + 1]; m = inside & (z > sub)
        sub[m] = z[m]; img[y0:y1 + 1, x0:x1 + 1][m] = rgb
    return img


VIEWS = [(1, 0.6, 1), (-1, 0.6, 1), (1, 0.6, -1), (-1, 0.6, -1), (1, -0.6, 1), (-1, -0.6, -1), (0.2, 1, 0.1), (0.2, -1, 0.1)]


def mosaic(tris, W=360):
    imgs = [render(tris, v, W) for v in VIEWS]
    line1 = np.concatenate(imgs[:4], axis=1); line2 = np.concatenate(imgs[4:], axis=1)
    return Image.fromarray(np.concatenate([line1, line2], axis=0).astype(np.uint8))


def count_red(tris):
    """fraction of red pixels per view (informative only)."""
    out = []
    for v in VIEWS:
        img = render(tris, v, 180); out.append(float(((img[:, :, 0] > 200) & (img[:, :, 1] < 60)).mean()))
    return out


if __name__ == "__main__":
    exit_ = sys.argv[1]; os.makedirs(exit_, exist_ok=True)
    gen_prefabs.blocks()
    mods = gen_meshes.models(gen_prefabs.ICON_PORTS)
    names = sys.argv[2:] or sorted(mods)
    for n in names:
        tris = merge(mods[n])
        if not tris: print(n, "(no mesh of its own)"); continue
        mosaic(tris).save(os.path.join(exit_, n + ".png"))
        v = count_red(tris)
        print("%-24s %5d tris  red per view: %s" % (n, len(tris), " ".join("%.3f" % x for x in v)))
