#!/usr/bin/env python3
"""Geometria low-poly no estilo do jogo (flat shading, chanfros de 45 graus, paineis rebaixados, torno de 12/16 lados).
Tudo vira uma lista de triangulos (a, b, c, cor) com a normal chapada; quem escreve o .asset/.glb/icone e' o gen_meshes.

Primitivas (metodos de Malha):
  caixa, cilindro, caixa_girada            (as antigas)
  caixa_chanfrada(x0,x1,y0,y1,z0,z1, cor, c | topo/base/vert, rebaixos={'+x': [...], ...})
  face(eixo, sinal, plano, u0,u1,v0,v1, cor, rebaixos=[...])   plano com bolsos rebaixados (recursivo)
  torno(perfil [(r,t),...], eixo, centro, cor, n)              superficie de revolucao (cilindros, aneis, bicos, cupulas)
  tronco(x0,x1,z0,z1, y0,y1, recuo_topo, cor)                  tronco de piramide
  esfera, cilindro_entre(p0,p1,r), tubo(pontos,r)              tubos/cabos com cotovelos
  parafuso(centro, eixo, sinal, r, h, cor)                     sextavado
Rebaixo = dict(u0,u1,v0,v1, prof, fundo=cor, parede=cor, filhos=[rebaixos no fundo])
"""
import math
import numpy as np


def _n(v): v = np.array(v, float); L = np.linalg.norm(v); return v / L if L > 1e-12 else v


class Malha:
    def __init__(self): self.tris = []   # (p0, p1, p2, cor)

    # ---------------------------------------------------------------- base
    def tri(self, a, b, c, cor, centro=None, nrm=None):
        a, b, c = np.array(a, float), np.array(b, float), np.array(c, float)
        n = np.cross(b - a, c - a)
        if np.linalg.norm(n) < 1e-10: return
        if nrm is not None:
            if n @ np.array(nrm, float) < 0: b, c = c, b
        elif centro is not None and n @ ((a + b + c) / 3 - np.array(centro, float)) < 0: b, c = c, b
        self.tris.append((a, b, c, cor))

    def quad(self, a, b, c, d, cor, centro=None, nrm=None):
        self.tri(a, b, c, cor, centro, nrm); self.tri(a, c, d, cor, centro, nrm)

    def juntar(self, outra, desloc=(0, 0, 0)):
        d = np.array(desloc, float)
        for a, b, c, cor in outra.tris: self.tris.append((a + d, b + d, c + d, cor))

    # ---------------------------------------------------------------- caixas
    def caixa(self, x0, x1, y0, y1, z0, z1, cor):
        self.caixa_chanfrada(x0, x1, y0, y1, z0, z1, cor, 0)

    def caixa_chanfrada(self, x0, x1, y0, y1, z0, z1, cor, c=0.0, topo=None, base=None, vert=None, rebaixos=None, cor_chanfro=None):
        """Chanfro (45 graus) nas 4 arestas de cima (topo), de baixo (base) e nas 4 verticais (vert); c = valor pra todas.
        rebaixos: {'+x'|'-x'|'+y'|'-y'|'+z'|'-z': [rebaixo, ...]} em coordenadas do bloco (u,v = os outros dois eixos em ordem x<y<z)."""
        ct = c if topo is None else topo; cb = c if base is None else base; cv = c if vert is None else vert
        cc = cor if cor_chanfro is None else cor_chanfro
        X = {-1: x0, 1: x1}; Y = {-1: y0, 1: y1}; Z = {-1: z0, 1: z1}
        cen = np.array([(x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2])
        vX, vY, vZ = {}, {}, {}
        for sx in (-1, 1):
            for sy in (-1, 1):
                for sz in (-1, 1):
                    cs = ct if sy > 0 else cb   # chanfro das arestas horizontais deste nivel
                    vX[sx, sy, sz] = np.array([X[sx], Y[sy] - sy * cs, Z[sz] - sz * cv])
                    vY[sx, sy, sz] = np.array([X[sx] - sx * cs, Y[sy], Z[sz] - sz * cs])
                    vZ[sx, sy, sz] = np.array([X[sx] - sx * cv, Y[sy] - sy * cs, Z[sz]])
        reb = rebaixos or {}
        # faces
        for sx in (-1, 1):
            r = reb.get(("+x" if sx > 0 else "-x"))
            if r: self.face(0, sx, X[sx], y0 + cb, y1 - ct, z0 + cv, z1 - cv, cor, r)
            else: self.quad(vX[sx, -1, -1], vX[sx, 1, -1], vX[sx, 1, 1], vX[sx, -1, 1], cor, nrm=(sx, 0, 0))
        for sy in (-1, 1):
            cs = ct if sy > 0 else cb
            r = reb.get(("+y" if sy > 0 else "-y"))
            if r: self.face(1, sy, Y[sy], x0 + cs, x1 - cs, z0 + cs, z1 - cs, cor, r)
            elif cs <= 1e-9 and cv > 1e-9:
                # sem chanfro neste nivel mas com chanfro vertical: a face e' um octogono (cantos cortados); os triangulos
                # de canto ficariam chapados em cima dela (0.10.1: eram desenhados duas vezes, coplanares)
                pts = [vX[-1, sy, -1], vZ[-1, sy, -1], vZ[1, sy, -1], vX[1, sy, -1], vX[1, sy, 1], vZ[1, sy, 1], vZ[-1, sy, 1], vX[-1, sy, 1]]
                for i in range(1, len(pts) - 1): self.tri(pts[0], pts[i], pts[i + 1], cor, nrm=(0, sy, 0))
            else: self.quad(vY[-1, sy, -1], vY[1, sy, -1], vY[1, sy, 1], vY[-1, sy, 1], cor, nrm=(0, sy, 0))
        for sz in (-1, 1):
            r = reb.get(("+z" if sz > 0 else "-z"))
            if r: self.face(2, sz, Z[sz], x0 + cv, x1 - cv, y0 + cb, y1 - ct, cor, r)
            else: self.quad(vZ[-1, -1, sz], vZ[1, -1, sz], vZ[1, 1, sz], vZ[-1, 1, sz], cor, nrm=(0, 0, sz))
        # chanfros das arestas
        for sy in (-1, 1):
            for sz in (-1, 1):   # arestas ao longo de X
                self.quad(vY[-1, sy, sz], vY[1, sy, sz], vZ[1, sy, sz], vZ[-1, sy, sz], cc, centro=cen)
        for sx in (-1, 1):
            for sy in (-1, 1):   # ao longo de Z
                self.quad(vX[sx, sy, -1], vX[sx, sy, 1], vY[sx, sy, 1], vY[sx, sy, -1], cc, centro=cen)
        for sx in (-1, 1):
            for sz in (-1, 1):   # verticais
                self.quad(vX[sx, -1, sz], vX[sx, 1, sz], vZ[sx, 1, sz], vZ[sx, -1, sz], cc, centro=cen)
        # cantos (nao existem quando o nivel nao tem chanfro: ficariam chapados na face de cima/baixo)
        for sx in (-1, 1):
            for sy in (-1, 1):
                if (ct if sy > 0 else cb) <= 1e-9: continue
                for sz in (-1, 1):
                    self.tri(vX[sx, sy, sz], vY[sx, sy, sz], vZ[sx, sy, sz], cc, centro=cen)

    def face(self, eixo, sinal, plano, u0, u1, v0, v1, cor, rebaixos=None):
        """Retangulo no plano (coordenada 'plano' no eixo), u/v = os outros dois eixos em ordem (x<y<z), normal = sinal*eixo.
        rebaixos = [dict(u0,u1,v0,v1,prof,fundo,parede,filhos)] cortados no plano (grade de celulas)."""
        eu, ev = [(1, 2), (0, 2), (0, 1)][eixo]

        def P(u, v, w=plano):
            q = np.zeros(3); q[eixo] = w; q[eu] = u; q[ev] = v; return q
        nrm = np.zeros(3); nrm[eixo] = sinal
        rebs = [r for r in (rebaixos or []) if r["u1"] > u0 and r["u0"] < u1 and r["v1"] > v0 and r["v0"] < v1]
        if not rebs:
            self.quad(P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1), cor, nrm=nrm); return
        us = sorted({u0, u1} | {min(max(r[k], u0), u1) for r in rebs for k in ("u0", "u1")})
        vs = sorted({v0, v1} | {min(max(r[k], v0), v1) for r in rebs for k in ("v0", "v1")})
        for i in range(len(us) - 1):
            for j in range(len(vs) - 1):
                a, b, c_, d = us[i], us[i + 1], vs[j], vs[j + 1]
                if b - a < 1e-9 or d - c_ < 1e-9: continue
                cu, cv = (a + b) / 2, (c_ + d) / 2
                if any(r["u0"] <= cu <= r["u1"] and r["v0"] <= cv <= r["v1"] for r in rebs): continue
                self.quad(P(a, c_), P(b, c_), P(b, d), P(a, d), cor, nrm=nrm)
        for r in rebs:
            a, b = max(r["u0"], u0), min(r["u1"], u1); c_, d = max(r["v0"], v0), min(r["v1"], v1)
            fundo = plano - sinal * r["prof"]
            cf = r.get("fundo", cor); cp = r.get("parede", cf)
            # fundo (pode ter bolsos filhos)
            self.face(eixo, sinal, fundo, a, b, c_, d, cf, r.get("filhos"))
            # paredes: normal aponta pra dentro do bolso. 0.19.3 (Andre: "buraco da porta com face transparente" em varios blocos):
            # quando o bolso passava da borda da face (ate a aresta chanfrada) a parede desse lado nao era desenhada e dava pra ver o
            # verso da face oposta pelo vao entre o fundo e o chanfro. Agora as 4 paredes sao sempre fechadas (a de fora fica
            # encostada por dentro no chanfro).
            nu = np.zeros(3); nu[eu] = 1; nv = np.zeros(3); nv[ev] = 1
            self.quad(P(a, c_, fundo), P(a, d, fundo), P(a, d), P(a, c_), cp, nrm=nu)
            self.quad(P(b, c_, fundo), P(b, d, fundo), P(b, d), P(b, c_), cp, nrm=-nu)
            self.quad(P(a, c_, fundo), P(b, c_, fundo), P(b, c_), P(a, c_), cp, nrm=nv)
            self.quad(P(a, d, fundo), P(b, d, fundo), P(b, d), P(a, d), cp, nrm=-nv)

    def tronco(self, x0, x1, z0, z1, y0, y1, recuo, cor, recuo_x=None, recuo_z=None, cor_lados=None):
        """Tronco de piramide: base [x0,x1]x[z0,z1] em y0, topo recuado 'recuo' de cada lado em y1 (negativo = alarga)."""
        rx = recuo if recuo_x is None else recuo_x; rz = recuo if recuo_z is None else recuo_z
        cl = cor if cor_lados is None else cor_lados
        b = [(x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)]
        t = [(x0 + rx, y1, z0 + rz), (x1 - rx, y1, z0 + rz), (x1 - rx, y1, z1 - rz), (x0 + rx, y1, z1 - rz)]
        cen = ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2)
        self.quad(*b, cor, centro=cen); self.quad(*t, cor, centro=cen)
        for i in range(4):
            j = (i + 1) % 4
            self.quad(b[i], b[j], t[j], t[i], cl, centro=cen)

    # ---------------------------------------------------------------- revolucao
    def torno(self, perfil, eixo, centro, cor, n=16, rot0=0.0, tampas=True, cores=None):
        """perfil = [(r, t), ...] percorrido com o material do lado de dentro (r menor); t ao longo do eixo a partir do centro.
        cores = lista de cor por segmento (opcional)."""
        c = np.array(centro, float); a1, a2 = [(1, 2), (2, 0), (0, 1)][eixo]
        e = np.zeros(3); e[eixo] = 1

        def P(r, t, ang):
            q = c.copy(); q[eixo] += t; q[a1] += r * math.cos(ang); q[a2] += r * math.sin(ang); return q

        def radial(ang):
            q = np.zeros(3); q[a1] = math.cos(ang); q[a2] = math.sin(ang); return q
        for i in range(len(perfil) - 1):
            (r0, t0), (r1, t1) = perfil[i], perfil[i + 1]
            dr, dt = r1 - r0, t1 - t0
            cs = cores[i] if cores and i < len(cores) else cor
            for k in range(n):
                u0, u1 = rot0 + 2 * math.pi * k / n, rot0 + 2 * math.pi * (k + 1) / n
                nrm = dt * radial((u0 + u1) / 2) - dr * e
                self.quad(P(r0, t0, u0), P(r0, t0, u1), P(r1, t1, u1), P(r1, t1, u0), cs, nrm=nrm)
        if tampas:
            r0, t0 = perfil[0]; r1, t1 = perfil[-1]
            # 0.10.1: as tampas apontam pra fora conforme o SENTIDO do perfil (um parafuso com sinal negativo percorre t
            # decrescente: a tampa do comeco fica no fim do solido e vice-versa). Antes era fixo (-e / +e) e os parafusos
            # "de cabeca pra baixo" saiam ocos, com o fundo brigando (z-fighting) com a superficie onde sentavam.
            s = 1.0 if t1 >= t0 else -1.0
            if r0 > 1e-9:
                cb = c + e * t0
                for k in range(n):
                    u0, u1 = rot0 + 2 * math.pi * k / n, rot0 + 2 * math.pi * (k + 1) / n
                    self.tri(cb, P(r0, t0, u0), P(r0, t0, u1), cores[0] if cores else cor, nrm=-e * s)
            if r1 > 1e-9:
                ct = c + e * t1
                for k in range(n):
                    u0, u1 = rot0 + 2 * math.pi * k / n, rot0 + 2 * math.pi * (k + 1) / n
                    self.tri(ct, P(r1, t1, u0), P(r1, t1, u1), cores[-1] if cores else cor, nrm=e * s)

    def cilindro(self, eixo, centro, r, h, cor, n=24, rot0=0.0, tampas=True, chanfro=0.0):
        if chanfro > 0:
            self.torno([(r - chanfro, -h / 2), (r, -h / 2 + chanfro), (r, h / 2 - chanfro), (r - chanfro, h / 2)], eixo, centro, cor, n, rot0, tampas)
        else:
            self.torno([(r, -h / 2), (r, h / 2)], eixo, centro, cor, n, rot0, tampas)

    def arco(self, eixo, centro, r_int, r_ext, h, ang0, ang1, cor, n=16):
        """Pedaco de anel (prisma anular) entre os angulos ang0..ang1 (radianos; no eixo 1: 0 = +Z, pi/2 = +X), altura h
        centrada; com as duas faces de corte fechadas."""
        c = np.array(centro, float); a1, a2 = [(1, 2), (2, 0), (0, 1)][eixo]
        e = np.zeros(3); e[eixo] = 1

        def P(r, t, ang):
            q = c.copy(); q[eixo] += t; q[a1] += r * math.cos(ang); q[a2] += r * math.sin(ang); return q
        for k in range(n):
            u0, u1 = ang0 + (ang1 - ang0) * k / n, ang0 + (ang1 - ang0) * (k + 1) / n
            m = (u0 + u1) / 2; rad = np.zeros(3); rad[a1] = math.cos(m); rad[a2] = math.sin(m)
            self.quad(P(r_ext, -h / 2, u0), P(r_ext, -h / 2, u1), P(r_ext, h / 2, u1), P(r_ext, h / 2, u0), cor, nrm=rad)
            self.quad(P(r_int, -h / 2, u0), P(r_int, -h / 2, u1), P(r_int, h / 2, u1), P(r_int, h / 2, u0), cor, nrm=-rad)
            self.quad(P(r_int, h / 2, u0), P(r_ext, h / 2, u0), P(r_ext, h / 2, u1), P(r_int, h / 2, u1), cor, nrm=e)
            self.quad(P(r_int, -h / 2, u0), P(r_ext, -h / 2, u0), P(r_ext, -h / 2, u1), P(r_int, -h / 2, u1), cor, nrm=-e)
        for ang, s in ((ang0, -1), (ang1, 1)):
            tg = np.zeros(3); tg[a1] = -math.sin(ang) * s; tg[a2] = math.cos(ang) * s
            self.quad(P(r_int, -h / 2, ang), P(r_ext, -h / 2, ang), P(r_ext, h / 2, ang), P(r_int, h / 2, ang), cor, nrm=tg)

    def anel(self, eixo, centro, r_int, r_ext, h, cor, n=16, rot0=0.0, chanfro=0.0):
        """anel (tubo curto) entre r_int e r_ext, altura h, centrado."""
        c = chanfro
        if c > 0:
            perfil = [(r_int, -h / 2), (r_ext - c, -h / 2), (r_ext, -h / 2 + c), (r_ext, h / 2 - c), (r_ext - c, h / 2), (r_int, h / 2), (r_int, -h / 2)]
        else:
            perfil = [(r_int, -h / 2), (r_ext, -h / 2), (r_ext, h / 2), (r_int, h / 2), (r_int, -h / 2)]
        self.torno(perfil, eixo, centro, cor, n, rot0, tampas=False)

    def esfera(self, centro, r, cor, n=10, m=6):
        c = np.array(centro, float)
        perfil = [(r * math.sin(math.pi * i / m), -r * math.cos(math.pi * i / m)) for i in range(m + 1)]
        self.torno(perfil, 1, c, cor, n, tampas=False)

    def cilindro_entre(self, p0, p1, r, cor, n=8, tampas=True):
        p0, p1 = np.array(p0, float), np.array(p1, float); d = p1 - p0; L = np.linalg.norm(d)
        if L < 1e-9: return
        d /= L; a = np.array([0, 1.0, 0]) if abs(d[1]) < 0.9 else np.array([1.0, 0, 0])
        u = _n(np.cross(d, a)); v = np.cross(d, u); cen = (p0 + p1) / 2
        ring0 = [p0 + r * (math.cos(2 * math.pi * k / n) * u + math.sin(2 * math.pi * k / n) * v) for k in range(n)]
        ring1 = [q + d * L for q in ring0]
        for k in range(n):
            j = (k + 1) % n
            self.quad(ring0[k], ring0[j], ring1[j], ring1[k], cor, centro=cen)
            if tampas:
                self.tri(p0, ring0[j], ring0[k], cor, nrm=-d); self.tri(p1, ring1[k], ring1[j], cor, nrm=d)

    def tubo(self, pontos, r, cor, n=8, cotovelos=True):
        for i in range(len(pontos) - 1): self.cilindro_entre(pontos[i], pontos[i + 1], r, cor, n)
        if cotovelos:
            for p in pontos[1:-1]: self.esfera(p, r, cor, n, 4)

    def parafuso(self, centro, eixo, sinal, r, h, cor, n=6, rot0=0.0):
        """Sextavado saindo 'h' de uma superficie (centro na superficie), com o topo um pouco menor."""
        c = np.array(centro, float)
        perfil = [(r, 0), (r, h * 0.75), (r * 0.8, h)]
        # 0.19.2 (Andre: "a parede so e' visivel de um angulo, nos outros e' um corte transparente"): com sinal negativo o perfil era
        # percorrido com t decrescente -> normais das laterais pra DENTRO (o jogo so desenha um lado: a cabeca do parafuso virava um
        # buraco hexagonal). Agora o perfil e' invertido E percorrido ao contrario (material do lado de dentro, igual o torno espera).
        if sinal < 0: perfil = [(rr, -t) for rr, t in reversed(perfil)]
        self.torno(perfil, eixo, c, cor, n, rot0, tampas=True)

    def caixa_girada(self, eixo, ang, dims, centro_local, cor):
        """caixa com dims (radial, tangencial, ao longo do eixo) na distancia radial centro_local, girada 'ang' em volta do eixo."""
        a1, a2 = [(1, 2), (2, 0), (0, 1)][eixo]
        dr, dt, de = dims; r0 = centro_local
        cs, sn = math.cos(ang), math.sin(ang)

        def P(rr, tt, ee):
            q = np.zeros(3); q[eixo] = ee; q[a1] = rr * cs - tt * sn; q[a2] = rr * sn + tt * cs; return q
        v = [P(r0 + x * dr / 2, y * dt / 2, z * de / 2) for x, y, z in ((-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1), (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1))]
        cen = P(r0, 0, 0)
        for f in ((0, 3, 2, 1), (4, 5, 6, 7), (0, 4, 7, 3), (1, 2, 6, 5), (0, 1, 5, 4), (3, 7, 6, 2)):
            self.quad(*[v[i] for i in f], cor, centro=cen)

    def girar(self, eixo, graus, centro=(0, 0, 0)):
        """gira tudo que ja esta na malha em volta de um eixo (0/1/2) passando por centro."""
        a = math.radians(graus); cs, sn = math.cos(a), math.sin(a); a1, a2 = [(1, 2), (2, 0), (0, 1)][eixo]
        c = np.array(centro, float)

        def R(p):
            q = p - c; r = q.copy(); r[a1] = q[a1] * cs - q[a2] * sn; r[a2] = q[a1] * sn + q[a2] * cs; return r + c
        self.tris = [(R(a_), R(b), R(c_), cor) for a_, b, c_, cor in self.tris]

    def espelhar(self, eixo):
        self.tris = [(f(a), f(c), f(b), cor) for a, b, c, cor in self.tris for f in [lambda p: np.array([p[i] * (-1 if i == eixo else 1) for i in range(3)])]]


# ---------------------------------------------------------------- ajudantes de rebaixo
def rebaixo(u0, u1, v0, v1, prof, fundo, parede=None, filhos=None):
    return dict(u0=u0, u1=u1, v0=v0, v1=v1, prof=prof, fundo=fundo, parede=fundo if parede is None else parede, filhos=filhos or [])


def rebaixo_centrado(cu, cv, lu, lv, prof, fundo, parede=None, filhos=None):
    return rebaixo(cu - lu / 2, cu + lu / 2, cv - lv / 2, cv + lv / 2, prof, fundo, parede, filhos)
