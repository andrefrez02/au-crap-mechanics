#!/usr/bin/env python3
"""Low-poly geometry in the game's style (flat shading, 45 degree chamfers, recessed panels, 12/16-sided lathe).
Everything becomes a list of triangles (a, b, c, color) with the flat normal; whoever writes the .asset/.glb/icon is gen_meshes.

Primitives (Mesh methods):
  box, cylinder, rotated_box            (the old ones)
  chamfered_box(x0,x1,y0,y1,z0,z1, color, c | top/base/vert, recesses={'+x': [...], ...})
  face_(axis, sign, plane, u0,u1,v0,v1, color, recesses=[...])   plane with recessed pockets (recursive)
  lathe(profile [(r,t),...], axis, center, color, n)              surface of revolution (cylinders, rings, nozzles, domes)
  frustum(x0,x1,z0,z1, y0,y1, top_inset, color)                  pyramid frustum
  sphere, cylinder_between(p0,p1,r), tube(points,r)              tubes/cables with elbows
  screw(center, axis, sign, r, h, color)                     hexagonal
Recess = dict(u0,u1,v0,v1, depth, bottom=color, wall=color, children=[recesses on the bottom])
"""
import math
import numpy as np


def _n(v): v = np.array(v, float); L = np.linalg.norm(v); return v / L if L > 1e-12 else v


class Mesh:
    def __init__(self): self.tris = []   # (p0, p1, p2, color)

    # ---------------------------------------------------------------- base
    def tri(self, a, b, c, color, center=None, nrm=None):
        a, b, c = np.array(a, float), np.array(b, float), np.array(c, float)
        n = np.cross(b - a, c - a)
        if np.linalg.norm(n) < 1e-10: return
        if nrm is not None:
            if n @ np.array(nrm, float) < 0: b, c = c, b
        elif center is not None and n @ ((a + b + c) / 3 - np.array(center, float)) < 0: b, c = c, b
        self.tris.append((a, b, c, color))

    def quad_(self, a, b, c, d, color, center=None, nrm=None):
        self.tri(a, b, c, color, center, nrm); self.tri(a, c, d, color, center, nrm)

    def merge(self, other, offs=(0, 0, 0)):
        d = np.array(offs, float)
        for a, b, c, color in other.tris: self.tris.append((a + d, b + d, c + d, color))

    # ---------------------------------------------------------------- boxes
    def box(self, x0, x1, y0, y1, z0, z1, color):
        self.chamfered_box(x0, x1, y0, y1, z0, z1, color, 0)

    def chamfered_box(self, x0, x1, y0, y1, z0, z1, color, c=0.0, top=None, base=None, vert=None, recesses=None, chamfer_color=None):
        """Chamfer (45 degrees) on the 4 top edges (top), bottom edges (base) and the 4 vertical ones (vert); c = value for all of them.
        recesses: {'+x'|'-x'|'+y'|'-y'|'+z'|'-z': [recess, ...]} in block coordinates (u,v = the other two axes in order x<y<z)."""
        ct = c if top is None else top; cb = c if base is None else base; cv = c if vert is None else vert
        cc = color if chamfer_color is None else chamfer_color
        X = {-1: x0, 1: x1}; Y = {-1: y0, 1: y1}; Z = {-1: z0, 1: z1}
        ctr = np.array([(x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2])
        vX, vY, vZ = {}, {}, {}
        for sx in (-1, 1):
            for sy in (-1, 1):
                for sz in (-1, 1):
                    cs = ct if sy > 0 else cb   # chamfer of this level's horizontal edges
                    vX[sx, sy, sz] = np.array([X[sx], Y[sy] - sy * cs, Z[sz] - sz * cv])
                    vY[sx, sy, sz] = np.array([X[sx] - sx * cs, Y[sy], Z[sz] - sz * cs])
                    vZ[sx, sy, sz] = np.array([X[sx] - sx * cv, Y[sy] - sy * cs, Z[sz]])
        rec = recesses or {}
        # faces
        for sx in (-1, 1):
            r = rec.get(("+x" if sx > 0 else "-x"))
            if r: self.face_(0, sx, X[sx], y0 + cb, y1 - ct, z0 + cv, z1 - cv, color, r)
            else: self.quad_(vX[sx, -1, -1], vX[sx, 1, -1], vX[sx, 1, 1], vX[sx, -1, 1], color, nrm=(sx, 0, 0))
        for sy in (-1, 1):
            cs = ct if sy > 0 else cb
            r = rec.get(("+y" if sy > 0 else "-y"))
            if r: self.face_(1, sy, Y[sy], x0 + cs, x1 - cs, z0 + cs, z1 - cs, color, r)
            elif cs <= 1e-9 and cv > 1e-9:
                # no chamfer on this level but with a vertical chamfer: the face is an octagon (cut corners); the corner
                # triangles would lie flat on top of it (0.10.1: they were drawn twice, coplanar)
                pts = [vX[-1, sy, -1], vZ[-1, sy, -1], vZ[1, sy, -1], vX[1, sy, -1], vX[1, sy, 1], vZ[1, sy, 1], vZ[-1, sy, 1], vX[-1, sy, 1]]
                for i in range(1, len(pts) - 1): self.tri(pts[0], pts[i], pts[i + 1], color, nrm=(0, sy, 0))
            else: self.quad_(vY[-1, sy, -1], vY[1, sy, -1], vY[1, sy, 1], vY[-1, sy, 1], color, nrm=(0, sy, 0))
        for sz in (-1, 1):
            r = rec.get(("+z" if sz > 0 else "-z"))
            if r: self.face_(2, sz, Z[sz], x0 + cv, x1 - cv, y0 + cb, y1 - ct, color, r)
            else: self.quad_(vZ[-1, -1, sz], vZ[1, -1, sz], vZ[1, 1, sz], vZ[-1, 1, sz], color, nrm=(0, 0, sz))
        # edge chamfers
        for sy in (-1, 1):
            for sz in (-1, 1):   # edges along X
                self.quad_(vY[-1, sy, sz], vY[1, sy, sz], vZ[1, sy, sz], vZ[-1, sy, sz], cc, center=ctr)
        for sx in (-1, 1):
            for sy in (-1, 1):   # along Z
                self.quad_(vX[sx, sy, -1], vX[sx, sy, 1], vY[sx, sy, 1], vY[sx, sy, -1], cc, center=ctr)
        for sx in (-1, 1):
            for sz in (-1, 1):   # vertical ones
                self.quad_(vX[sx, -1, sz], vX[sx, 1, sz], vZ[sx, 1, sz], vZ[sx, -1, sz], cc, center=ctr)
        # corners (they don't exist when the level has no chamfer: they'd lie flat on the top/bottom face)
        for sx in (-1, 1):
            for sy in (-1, 1):
                if (ct if sy > 0 else cb) <= 1e-9: continue
                for sz in (-1, 1):
                    self.tri(vX[sx, sy, sz], vY[sx, sy, sz], vZ[sx, sy, sz], cc, center=ctr)

    def face_(self, axis_v, sign, plane, u0, u1, v0, v1, color, recesses=None):
        """Rectangle on the plane (coordinate 'plane' on the axis), u/v = the other two axes in order (x<y<z), normal = sign*axis.
        recesses = [dict(u0,u1,v0,v1,depth,bottom,wall,children)] cut into the plane (grid of cells)."""
        eu, ev = [(1, 2), (0, 2), (0, 1)][axis_v]

        def P(u, v, w=plane):
            q = np.zeros(3); q[axis_v] = w; q[eu] = u; q[ev] = v; return q
        nrm = np.zeros(3); nrm[axis_v] = sign
        recs = [r for r in (recesses or []) if r["u1"] > u0 and r["u0"] < u1 and r["v1"] > v0 and r["v0"] < v1]
        if not recs:
            self.quad_(P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1), color, nrm=nrm); return
        us = sorted({u0, u1} | {min(max(r[k], u0), u1) for r in recs for k in ("u0", "u1")})
        vs = sorted({v0, v1} | {min(max(r[k], v0), v1) for r in recs for k in ("v0", "v1")})
        for i in range(len(us) - 1):
            for j in range(len(vs) - 1):
                a, b, c_, d = us[i], us[i + 1], vs[j], vs[j + 1]
                if b - a < 1e-9 or d - c_ < 1e-9: continue
                cu, cv = (a + b) / 2, (c_ + d) / 2
                if any(r["u0"] <= cu <= r["u1"] and r["v0"] <= cv <= r["v1"] for r in recs): continue
                self.quad_(P(a, c_), P(b, c_), P(b, d), P(a, d), color, nrm=nrm)
        for r in recs:
            a, b = max(r["u0"], u0), min(r["u1"], u1); c_, d = max(r["v0"], v0), min(r["v1"], v1)
            bottom = plane - sign * r["depth"]
            cf = r.get("bottom", color); cp = r.get("wall", cf)
            # bottom (may have child pockets)
            self.face_(axis_v, sign, bottom, a, b, c_, d, cf, r.get("children"))
            # walls: the normal points into the pocket. 0.19.3 (Andre: "port hole with a transparent face" on several blocks):
            # when the pocket went past the face's edge (up to the chamfered edge) the wall on that side wasn't drawn and you could see the
            # back of the opposite face through the gap between the bottom and the chamfer. Now the 4 walls are always closed (the outer one rests
            # against the chamfer from inside).
            nu = np.zeros(3); nu[eu] = 1; nv = np.zeros(3); nv[ev] = 1
            self.quad_(P(a, c_, bottom), P(a, d, bottom), P(a, d), P(a, c_), cp, nrm=nu)
            self.quad_(P(b, c_, bottom), P(b, d, bottom), P(b, d), P(b, c_), cp, nrm=-nu)
            self.quad_(P(a, c_, bottom), P(b, c_, bottom), P(b, c_), P(a, c_), cp, nrm=nv)
            self.quad_(P(a, d, bottom), P(b, d, bottom), P(b, d), P(a, d), cp, nrm=-nv)

    def frustum(self, x0, x1, z0, z1, y0, y1, inset, color, inset_x=None, inset_z=None, side_color=None):
        """Pyramid frustum: base [x0,x1]x[z0,z1] at y0, top inset by 'inset' on each side at y1 (negative = widens)."""
        rx = inset if inset_x is None else inset_x; rz = inset if inset_z is None else inset_z
        cl = color if side_color is None else side_color
        b = [(x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1)]
        t = [(x0 + rx, y1, z0 + rz), (x1 - rx, y1, z0 + rz), (x1 - rx, y1, z1 - rz), (x0 + rx, y1, z1 - rz)]
        ctr = ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2)
        self.quad_(*b, color, center=ctr); self.quad_(*t, color, center=ctr)
        for i in range(4):
            j = (i + 1) % 4
            self.quad_(b[i], b[j], t[j], t[i], cl, center=ctr)

    # ---------------------------------------------------------------- revolution
    def lathe(self, profile, axis_v, center, color, n=16, rot0=0.0, lids=True, colors=None):
        """profile = [(r, t), ...] traversed with the material on the inner side (smaller r); t along the axis from the center.
        colors = list of color per segment (optional)."""
        c = np.array(center, float); a1, a2 = [(1, 2), (2, 0), (0, 1)][axis_v]
        e = np.zeros(3); e[axis_v] = 1

        def P(r, t, ang):
            q = c.copy(); q[axis_v] += t; q[a1] += r * math.cos(ang); q[a2] += r * math.sin(ang); return q

        def radial(ang):
            q = np.zeros(3); q[a1] = math.cos(ang); q[a2] = math.sin(ang); return q
        for i in range(len(profile) - 1):
            (r0, t0), (r1, t1) = profile[i], profile[i + 1]
            dr, dt = r1 - r0, t1 - t0
            cs = colors[i] if colors and i < len(colors) else color
            for k in range(n):
                u0, u1 = rot0 + 2 * math.pi * k / n, rot0 + 2 * math.pi * (k + 1) / n
                nrm = dt * radial((u0 + u1) / 2) - dr * e
                self.quad_(P(r0, t0, u0), P(r0, t0, u1), P(r1, t1, u1), P(r1, t1, u0), cs, nrm=nrm)
        if lids:
            r0, t0 = profile[0]; r1, t1 = profile[-1]
            # 0.10.1: the caps point outward according to the profile's DIRECTION (a screw with a negative sign traverses t
            # decreasing: the start cap ends up at the end of the solid and vice versa). Before it was fixed (-e / +e) and the screws
            # "upside down" came out hollow, with the bottom fighting (z-fighting) the surface they sat on.
            s = 1.0 if t1 >= t0 else -1.0
            if r0 > 1e-9:
                cb = c + e * t0
                for k in range(n):
                    u0, u1 = rot0 + 2 * math.pi * k / n, rot0 + 2 * math.pi * (k + 1) / n
                    self.tri(cb, P(r0, t0, u0), P(r0, t0, u1), colors[0] if colors else color, nrm=-e * s)
            if r1 > 1e-9:
                ct = c + e * t1
                for k in range(n):
                    u0, u1 = rot0 + 2 * math.pi * k / n, rot0 + 2 * math.pi * (k + 1) / n
                    self.tri(ct, P(r1, t1, u0), P(r1, t1, u1), colors[-1] if colors else color, nrm=e * s)

    def cylinder(self, axis_v, center, r, h, color, n=24, rot0=0.0, lids=True, chamfer=0.0):
        if chamfer > 0:
            self.lathe([(r - chamfer, -h / 2), (r, -h / 2 + chamfer), (r, h / 2 - chamfer), (r - chamfer, h / 2)], axis_v, center, color, n, rot0, lids)
        else:
            self.lathe([(r, -h / 2), (r, h / 2)], axis_v, center, color, n, rot0, lids)

    def arc(self, axis_v, center, r_int, r_ext, h, ang0, ang1, color, n=16):
        """Ring segment (annular prism) between the angles ang0..ang1 (radians; on axis 1: 0 = +Z, pi/2 = +X), height h
        centered; with both cut faces closed."""
        c = np.array(center, float); a1, a2 = [(1, 2), (2, 0), (0, 1)][axis_v]
        e = np.zeros(3); e[axis_v] = 1

        def P(r, t, ang):
            q = c.copy(); q[axis_v] += t; q[a1] += r * math.cos(ang); q[a2] += r * math.sin(ang); return q
        for k in range(n):
            u0, u1 = ang0 + (ang1 - ang0) * k / n, ang0 + (ang1 - ang0) * (k + 1) / n
            m = (u0 + u1) / 2; rad = np.zeros(3); rad[a1] = math.cos(m); rad[a2] = math.sin(m)
            self.quad_(P(r_ext, -h / 2, u0), P(r_ext, -h / 2, u1), P(r_ext, h / 2, u1), P(r_ext, h / 2, u0), color, nrm=rad)
            self.quad_(P(r_int, -h / 2, u0), P(r_int, -h / 2, u1), P(r_int, h / 2, u1), P(r_int, h / 2, u0), color, nrm=-rad)
            self.quad_(P(r_int, h / 2, u0), P(r_ext, h / 2, u0), P(r_ext, h / 2, u1), P(r_int, h / 2, u1), color, nrm=e)
            self.quad_(P(r_int, -h / 2, u0), P(r_ext, -h / 2, u0), P(r_ext, -h / 2, u1), P(r_int, -h / 2, u1), color, nrm=-e)
        for ang, s in ((ang0, -1), (ang1, 1)):
            tg = np.zeros(3); tg[a1] = -math.sin(ang) * s; tg[a2] = math.cos(ang) * s
            self.quad_(P(r_int, -h / 2, ang), P(r_ext, -h / 2, ang), P(r_ext, h / 2, ang), P(r_int, h / 2, ang), color, nrm=tg)

    def ring(self, axis_v, center, r_int, r_ext, h, color, n=16, rot0=0.0, chamfer=0.0):
        """ring (short tube) between r_int and r_ext, height h, centered."""
        c = chamfer
        if c > 0:
            profile = [(r_int, -h / 2), (r_ext - c, -h / 2), (r_ext, -h / 2 + c), (r_ext, h / 2 - c), (r_ext - c, h / 2), (r_int, h / 2), (r_int, -h / 2)]
        else:
            profile = [(r_int, -h / 2), (r_ext, -h / 2), (r_ext, h / 2), (r_int, h / 2), (r_int, -h / 2)]
        self.lathe(profile, axis_v, center, color, n, rot0, lids=False)

    def sphere(self, center, r, color, n=10, m=6):
        c = np.array(center, float)
        profile = [(r * math.sin(math.pi * i / m), -r * math.cos(math.pi * i / m)) for i in range(m + 1)]
        self.lathe(profile, 1, c, color, n, lids=False)

    def cylinder_between(self, p0, p1, r, color, n=8, lids=True):
        p0, p1 = np.array(p0, float), np.array(p1, float); d = p1 - p0; L = np.linalg.norm(d)
        if L < 1e-9: return
        d /= L; a = np.array([0, 1.0, 0]) if abs(d[1]) < 0.9 else np.array([1.0, 0, 0])
        u = _n(np.cross(d, a)); v = np.cross(d, u); ctr = (p0 + p1) / 2
        ring0 = [p0 + r * (math.cos(2 * math.pi * k / n) * u + math.sin(2 * math.pi * k / n) * v) for k in range(n)]
        ring1 = [q + d * L for q in ring0]
        for k in range(n):
            j = (k + 1) % n
            self.quad_(ring0[k], ring0[j], ring1[j], ring1[k], color, center=ctr)
            if lids:
                self.tri(p0, ring0[j], ring0[k], color, nrm=-d); self.tri(p1, ring1[k], ring1[j], color, nrm=d)

    def tube(self, points, r, color, n=8, elbows=True):
        for i in range(len(points) - 1): self.cylinder_between(points[i], points[i + 1], r, color, n)
        if elbows:
            for p in points[1:-1]: self.sphere(p, r, color, n, 4)

    def screw(self, center, axis_v, sign, r, h, color, n=6, rot0=0.0):
        """Hexagon sticking out 'h' from a surface (center on the surface), with the top a bit smaller."""
        c = np.array(center, float)
        profile = [(r, 0), (r, h * 0.75), (r * 0.8, h)]
        # 0.19.2 (Andre: "the wall is only visible from one angle, from the others it's a transparent cut"): with a negative sign the profile was
        # traversed with decreasing t -> side normals INWARD (the game only draws one side: the screw's head became a
        # hexagonal hole). Now the profile is inverted AND traversed backward (material on the inner side, as lathe expects).
        if sign < 0: profile = [(rr, -t) for rr, t in reversed(profile)]
        self.lathe(profile, axis_v, c, color, n, rot0, lids=True)

    def rotated_box(self, axis_v, ang, dims, local_center, color):
        """box with dims (radial, tangential, along the axis) at the radial distance local_center, rotated 'ang' around the axis."""
        a1, a2 = [(1, 2), (2, 0), (0, 1)][axis_v]
        dr, dt, de = dims; r0 = local_center
        cs, sn = math.cos(ang), math.sin(ang)

        def P(rr, tt, ee):
            q = np.zeros(3); q[axis_v] = ee; q[a1] = rr * cs - tt * sn; q[a2] = rr * sn + tt * cs; return q
        v = [P(r0 + x * dr / 2, y * dt / 2, z * de / 2) for x, y, z in ((-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1), (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1))]
        ctr = P(r0, 0, 0)
        for f in ((0, 3, 2, 1), (4, 5, 6, 7), (0, 4, 7, 3), (1, 2, 6, 5), (0, 1, 5, 4), (3, 7, 6, 2)):
            self.quad_(*[v[i] for i in f], color, center=ctr)

    def rotate(self, axis_v, degs, center=(0, 0, 0)):
        """rotates everything already in the mesh around an axis (0/1/2) going through center."""
        a = math.radians(degs); cs, sn = math.cos(a), math.sin(a); a1, a2 = [(1, 2), (2, 0), (0, 1)][axis_v]
        c = np.array(center, float)

        def R(p):
            q = p - c; r = q.copy(); r[a1] = q[a1] * cs - q[a2] * sn; r[a2] = q[a1] * sn + q[a2] * cs; return r + c
        self.tris = [(R(a_), R(b), R(c_), color) for a_, b, c_, color in self.tris]

    def mirror(self, axis_v):
        self.tris = [(f(a), f(c), f(b), color) for a, b, c, color in self.tris for f in [lambda p: np.array([p[i] * (-1 if i == axis_v else 1) for i in range(3)])]]


# ---------------------------------------------------------------- recess helpers
def recess(u0, u1, v0, v1, depth, bottom, wall=None, children=None):
    return dict(u0=u0, u1=u1, v0=v0, v1=v1, depth=depth, bottom=bottom, wall=bottom if wall is None else wall, children=children or [])


def centered_recess(cu, cv, lu, lv, depth, bottom, wall=None, children=None):
    return recess(cu - lu / 2, cu + lu / 2, cv - lv / 2, cv + lv / 2, depth, bottom, wall, children)
