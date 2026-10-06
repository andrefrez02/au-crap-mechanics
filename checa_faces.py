"""0.19.3 (Andre: "erro de face errada": bolsos de porta, pneu/aro, pedal): renderiza cada modelo de 8 angulos; o que o jogo
mostra transparente e' face vista pelo verso -> aqui fica VERMELHO vivo (ou magenta, se e' um verso "atras" de nada = buraco).
Uso: python3 checa_faces.py <pasta_saida> [nome ...]"""
import math, os, sys
import numpy as np
from PIL import Image
import gen_prefabs, gen_meshes, modelos_v2
from geo import Malha


def _rot(euler):
    rx, ry, rz = [math.radians(a) for a in euler]
    cx, sx = math.cos(rx), math.sin(rx); cy, sy = math.cos(ry), math.sin(ry); cz, sz = math.cos(rz), math.sin(rz)
    Rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]]); Ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]]); Rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]])
    return Ry @ Rx @ Rz


def juntar(modelo):
    corpo, pint, extras = modelo[0], modelo[1], modelo[2] if len(modelo) > 2 else []
    tris = [(np.array(a), np.array(b), np.array(c)) for a, b, c, _ in corpo.tris] + [(np.array(a), np.array(b), np.array(c)) for a, b, c, _ in pint.tris]
    for ex in extras:
        nome, m, pos, esc = ex[0], ex[1], np.array(ex[2], float), np.array(ex[3], float)
        R = _rot(ex[4]) if len(ex) > 4 else np.eye(3)
        for a, b, c, _ in m.tris:
            tris.append(tuple(R @ (np.array(p) * esc) + pos for p in (a, b, c)))
    return tris


def render(tris, vista, W=360):
    vista = np.array(vista, float); vista /= np.linalg.norm(vista)
    fwd = -vista; up0 = np.array([0, 1.0, 0]) if abs(vista[1]) < 0.95 else np.array([0, 0, 1.0])
    right = np.cross(up0, fwd); right /= np.linalg.norm(right); up = np.cross(fwd, right)
    P = np.array(tris)   # (n,3,3)
    sx, sy = P @ right, P @ up
    lo = np.array([sx.min(), sy.min()]); hi = np.array([sx.max(), sy.max()])
    esc = (W * 0.9) / max(hi - lo); off = (np.array([W, W]) - (hi - lo) * esc) / 2
    img = np.full((W, W, 3), 40, np.float32); zb = np.full((W, W), -1e9, np.float32)
    for (a, b, c), px, py in zip(tris, sx, sy):
        nrm = np.cross(b - a, c - a); L = np.linalg.norm(nrm)
        if L < 1e-12: continue
        nrm /= L
        verso = nrm @ vista <= 0
        luz = 0.5 + 0.5 * abs(float(nrm @ np.array([0.3, 0.8, 0.5]) / 1.0))
        rgb = np.array([255, 30, 30], float) if verso else np.array([170, 170, 170], float) * luz
        X = (px - lo[0]) * esc + off[0]; Y = W - ((py - lo[1]) * esc + off[1])
        Z = np.array([a @ vista, b @ vista, c @ vista])
        x0, x1 = int(max(0, np.floor(X.min()))), int(min(W - 1, np.ceil(X.max()))); y0, y1 = int(max(0, np.floor(Y.min()))), int(min(W - 1, np.ceil(Y.max())))
        if x1 < x0 or y1 < y0: continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        d = (X[1] - X[0]) * (Y[2] - Y[0]) - (X[2] - X[0]) * (Y[1] - Y[0])
        if abs(d) < 1e-9: continue
        w1 = ((gx - X[0]) * (Y[2] - Y[0]) - (X[2] - X[0]) * (gy - Y[0])) / d
        w2 = ((X[1] - X[0]) * (gy - Y[0]) - (gx - X[0]) * (Y[1] - Y[0])) / d
        w0 = 1 - w1 - w2
        dentro = (w0 >= -1e-4) & (w1 >= -1e-4) & (w2 >= -1e-4)
        z = w0 * Z[0] + w1 * Z[1] + w2 * Z[2]
        sub = zb[y0:y1 + 1, x0:x1 + 1]; m = dentro & (z > sub)
        sub[m] = z[m]; img[y0:y1 + 1, x0:x1 + 1][m] = rgb
    return img


VISTAS = [(1, 0.6, 1), (-1, 0.6, 1), (1, 0.6, -1), (-1, 0.6, -1), (1, -0.6, 1), (-1, -0.6, -1), (0.2, 1, 0.1), (0.2, -1, 0.1)]


def mosaico(tris, W=360):
    imgs = [render(tris, v, W) for v in VISTAS]
    linha1 = np.concatenate(imgs[:4], axis=1); linha2 = np.concatenate(imgs[4:], axis=1)
    return Image.fromarray(np.concatenate([linha1, linha2], axis=0).astype(np.uint8))


def contar_vermelho(tris):
    """fracao de pixels vermelhos por vista (so informativo)."""
    out = []
    for v in VISTAS:
        img = render(tris, v, 180); out.append(float(((img[:, :, 0] > 200) & (img[:, :, 1] < 60)).mean()))
    return out


if __name__ == "__main__":
    saida = sys.argv[1]; os.makedirs(saida, exist_ok=True)
    gen_prefabs.blocos()
    mods = gen_meshes.modelos(gen_prefabs.PORTAS_ICONE)
    nomes = sys.argv[2:] or sorted(mods)
    for n in nomes:
        tris = juntar(mods[n])
        if not tris: print(n, "(sem malha propria)"); continue
        mosaico(tris).save(os.path.join(saida, n + ".png"))
        v = contar_vermelho(tris)
        print("%-24s %5d tris  vermelho por vista: %s" % (n, len(tris), " ".join("%.3f" % x for x in v)))
