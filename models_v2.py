#!/usr/bin/env python3
"""Models v2 (the game's style: 45 degree chamfers, recessed panels, port pockets with an octagonal frame, hexagonal
screws, cables, 8/12/16 sides). Block coordinates (centered, Y = up), sizes in metres. Each function returns
(fixed body, paintable part) as geo.Mesh. The port plugs are NOT here: they are the game's own meshes
(Port Input/Output/Power), which the prefab places at the port's position; here there's only the pocket where they fit (the plug is
0.07 and sits at face level, 0.02 inward, like on the game's parts)."""
import math, os
import numpy as np
from geo import Mesh, recess, centered_recess

# palette (RGBA vertex color copied from the game's parts)
BLACK, DARK, MEDIUM, BODY, LIGHT = (17, 17, 17, 0), (32, 32, 32, 54), (80, 80, 80, 54), (107, 107, 107, 54), (172, 172, 172, 54)
METAL = (200, 200, 200, 0)
PAINT = (64, 64, 64, 20)
RED, YELLOW = (176, 47, 47, 0), (211, 190, 59, 0)
LIGHT_COL = (230, 40, 30, 54)
GREEN_LIGHT = (40, 230, 70, 54)   # recoupler LED (lit)
YELLOW_LIGHT = (240, 170, 20, 54)
PISTON_LEDS = ((-0.09, "LedVerde"), (0.0, "LedAmarelo"), (0.09, "LedVermelho"))   # x of the 3 status LEDs on the +Z panel (0.12.1)
PISTON_LED_XS = {1.0: (-0.09, 0.0, 0.09), 0.5: (-0.01, 0.035, 0.08)}   # 0.20.0: on the mini the LEDs move to the right (the knob stays at real size); the plugin uses the same numbers
PISTON_GUIDE_REST = 0.0005   # 0.20.0: y scale of the hidden guide bar (the plugin stretches it)
PISTON_LED_Y, PISTON_LED_Z = -0.135, 0.224   # hidden (top 1 cm below the panel); the plugin pushes the current state's one 2.3 cm out
TIRE = (33, 33, 33, 54)

PORT_SIDE, PORT_DEPTH, PORT_RIN, PORT_RO = 0.112, 0.02, 0.044, 0.054
GAME_MESH_DIR = None   # the workspace's Assets/Game/Mesh folder (gen_prefabs sets it): only for the turret seat's preview/icon


def _shift(m, tris, d):
    for a, b, c, color in tris: m.tris.append((a + d, b + d, c + d, color))


# ------------------------------------------------------------------ common parts
def port_pocket(u, v):
    """Shallow square pocket where the game's plug fits (dark bottom)."""
    return centered_recess(u, v, PORT_SIDE, PORT_SIDE, PORT_DEPTH, BLACK, DARK)


def port_frame(m, axis_v, sign, plane, u, v):
    """Octagonal ring around the plug, from the bottom of the pocket up to the face level (like the frame of the game's decoupler)."""
    c = [0.0, 0.0, 0.0]; eu, ev = [(1, 2), (0, 2), (0, 1)][axis_v]
    c[axis_v] = plane - sign * PORT_DEPTH / 2; c[eu] = u; c[ev] = v
    m.ring(axis_v, c, PORT_RIN, PORT_RO, PORT_DEPTH, MEDIUM, n=8, rot0=math.pi / 8, chamfer=0.004)


def ports_on_face(c, ports, axis_v, sign, plane, frame=True):
    """Pockets (to pass to chamfered_box) + frames (on the body) of a face's ports. frame=False (0.22.0): only the pocket — for a
    port whose pocket goes past the face's edge (chamfered_box cuts the pocket at the edge; the frame would float)."""
    eu, ev = [(1, 2), (0, 2), (0, 1)][axis_v]
    rec = []
    for pos, d, t in (ports or []):
        if d // 2 != axis_v or (1 if d % 2 == 0 else -1) != sign: continue
        rec.append(port_pocket(pos[eu], pos[ev]))
        if frame: port_frame(c, axis_v, sign, plane, pos[eu], pos[ev])
    return rec


def screw_hole(m, center, axis_v, sign, r=0.016):
    """Screw 'hole': dark octagon almost at surface level (0.3 mm outward) — doesn't leave the block's box."""
    c = list(center); c[axis_v] += sign * 0.0003
    m.cylinder(axis_v, c, r, 0.0006, BLACK, n=8, rot0=math.pi / 8)


def pipe_socket(c, axis_v, sign, plane, u, v):
    """Octagonal plate with an octagonal hole (like the pipe socket of the game's tank), at face level, inside a shallow pocket."""
    eu, ev = [(1, 2), (0, 2), (0, 1)][axis_v]
    ctr = [0.0, 0.0, 0.0]; ctr[axis_v] = plane - sign * 0.012; ctr[eu] = u; ctr[ev] = v
    profile = [(0.055, 0), (0.09, 0), (0.09, 0.008), (0.082, 0.012), (0.055, 0.012)]
    if sign < 0: profile = [(r, -t) for r, t in profile]
    c.lathe(profile, axis_v, ctr, BODY, n=8, rot0=math.pi / 8, lids=False)
    cf = list(ctr); cf[axis_v] = plane - sign * 0.010
    c.cylinder(axis_v, cf, 0.055, 0.004, BLACK, n=8, rot0=math.pi / 8)
    for i in range(4):
        a = math.pi / 4 + i * math.pi / 2
        q = list(ctr); q[eu] = u + 0.085 * math.cos(a); q[ev] = v + 0.085 * math.sin(a); q[axis_v] += sign * 0.0005
        c.screw(q, axis_v, sign, 0.011, 0.007, LIGHT)
    return centered_recess(u, v, 0.2, 0.2, 0.012, MEDIUM, DARK)


# ------------------------------------------------------------------ motors
def motor(bounds=(1.0, 1.0, 1.0), fuel=False, ports=None):
    """Motor with bounds box (x, y, z); k = width (0.5 Small / 1 Medium / 1.5 Large) scales the details. ports: [(pos, direction, kind)]
    from gen_prefabs (all on the +X face). Paintable plate (the ports' pockets and, on the fuel one, the pipe socket), dark
    cradle, 16-sided body with fins, caps with screws, keyed shaft, junction box (electric) or cylinder head +
    exhaust (fuel), cables. 0.10.2 (form9): the fuel Small is 0.5 x 1 x 0.5 with the 4 ports in a 2x2 group beside
    the pipe socket (2-cell base); the electric Small stays the 0.5 cube."""
    c, p = Mesh(), Mesh()
    hx, hy, hz = bounds[0] / 2, bounds[1] / 2, bounds[2] / 2
    k = bounds[0]
    ct = 0.035 * k   # top chamfer of the plate
    ys = [pos[1] for pos, d, t in (ports or []) if d == 0]
    yb = max((0.26 if fuel else 0.18) * k - hy, (max(ys) + PORT_SIDE / 2 + ct + 0.008) if ys else -hy)
    if fuel: yb = max(yb, -hy + 0.125 + 0.10 + ct + 0.008)   # the pipe socket sits on the plate (bottom/-Z corner)
    room = hy - yb
    r = min(0.40 * k, hx - 0.05 * k, (room - 0.12 * k) / 2)
    # ---------------- base plate (paintable)
    rec_x = ports_on_face(c, ports, 0, 1, hx)
    zs = sorted(pos[2] for pos, d, t in (ports or []) if d == 0)
    if len(zs) >= 2:
        g = max((zs[i + 1] - zs[i], zs[i], zs[i + 1]) for i in range(len(zs) - 1))
        if g[0] > 0.35: rec_x.append(recess(ys[0] - 0.025, ys[0] + 0.025, g[1] + 0.09, g[2] - 0.09, 0.005, LIGHT, DARK))
    if fuel:
        rec_x.append(pipe_socket(c, 0, 1, hx, -hy + 0.125, -hz + 0.125))
    ym = (-hy + yb) / 2 - 0.01 * k; hgt = yb + hy
    groove = [centered_recess(ym, z, min(0.06 * k, hgt * 0.3), 0.03 * k, 0.012, BLACK, DARK) for z in [(-0.3 + 0.15 * i) * k for i in range(5)]]
    rec_minus_x = [centered_recess(ym, 0, min(0.09 * k, hgt * 0.45), 0.72 * k, 0.006, PAINT, DARK, groove)]
    if ys and yb - max(ys) > 0.20:
        # tall plate with the ports at the bottom (Small): ventilation grille on the +X face above the ports
        yg = (max(ys) + PORT_SIDE / 2 + yb - ct) / 2 + 0.01
        zg = (-hz + 0.25 + hz) / 2 if fuel else 0.0
        lg = (hz - (-hz + 0.25)) - 0.08 if fuel else 0.72 * k
        groove2 = [centered_recess(yg, zg + (-0.5 + 0.25 * i) * lg * 0.8, 0.06 * k, 0.03 * k, 0.012, BLACK, DARK) for i in range(5)]
        rec_x.append(centered_recess(yg, zg, 0.09 * k, lg, 0.006, PAINT, DARK, groove2))
    rec_low = [recess(-hx + 0.06 * k, -0.03 * k, -hz + 0.08 * k, hz - 0.08 * k, 0.012, PAINT, DARK),
                 recess(0.03 * k, hx - 0.06 * k, -hz + 0.08 * k, hz - 0.08 * k, 0.012, PAINT, DARK)]
    rec_z = [centered_recess(0, ym, 0.6 * k, min(0.07 * k, hgt * 0.35), 0.006, PAINT, DARK)]
    p.chamfered_box(-hx, hx, -hy, yb, -hz, hz, PAINT, top=ct, base=0.006 * k, vert=0.02 * k,
                      recesses={"+x": rec_x, "-y": rec_low, "-x": rec_minus_x, "+z": rec_z, "-z": rec_z})
    # ---------------- cradle (two dark frustums) with screws on the sides
    for z in (-0.20 * k, 0.20 * k):
        c.frustum(-(r + 0.04 * k), r + 0.04 * k, z - 0.075 * k, z + 0.075 * k, yb - 0.005, yb + r * 0.85, r * 0.5, DARK, inset_z=0.0)
        for x in (-(r - 0.06 * k), r - 0.06 * k):
            for sz in (-1, 1):
                c.screw((x, yb + 0.08 * k, z + sz * 0.075 * k), 2, sz, 0.02 * k, 0.012 * k, LIGHT)
    # ---------------- body (paintable): 16 sides; caps with 6 screws; keyed shaft; boss; 5 fins
    cy = yb + r; L = 0.72 * k
    p.cylinder(2, (0, cy, 0), r, L, PAINT, n=16, rot0=math.pi / 16, chamfer=0.035 * k)
    for sz in (-1, 1):
        c.cylinder(2, (0, cy, sz * (L / 2 + 0.025 * k)), r * 0.83, 0.05 * k, BODY, n=16, rot0=math.pi / 16, chamfer=0.012 * k)
        for i in range(6):
            a = math.pi / 6 + i * math.pi / 3
            c.screw((r * 0.67 * math.cos(a), cy + r * 0.67 * math.sin(a), sz * (L / 2 + 0.05 * k)), 2, sz, 0.018 * k, 0.012 * k, LIGHT)
    ze = -(L / 2 + 0.05 * k)
    c.cylinder(2, (0, cy, ze - 0.035 * k), 0.075 * k, 0.07 * k, METAL, n=12, chamfer=0.01 * k)
    c.box(-0.012 * k, 0.012 * k, cy + 0.06 * k, cy + 0.085 * k, ze - 0.062 * k, ze - 0.02 * k, DARK)   # key (shorter than the shaft: no coplanar face at the tip)
    c.cylinder(2, (0, cy, -ze + 0.015 * k), r * 0.42, 0.03 * k, DARK, n=16, rot0=math.pi / 16, chamfer=0.008 * k)
    for i in range(5):
        z = (-0.26 + 0.13 * i) * k
        c.ring(2, (0, cy, z), r - 0.02 * k, r + 0.045 * k, 0.022 * k, MEDIUM, n=16, rot0=math.pi / 16)
    top = cy + r * math.cos(math.pi / 16)
    yt = min(hy - 0.02 * k, top + 0.10 * k)
    # ---------------- on top
    if fuel:
        # cylinder head with 3 grooves and 4 screws
        c.chamfered_box(-0.24 * k, 0.10 * k, top - 0.10 * k, yt, -0.16 * k, 0.16 * k, DARK, c=0.015 * k,
                          recesses={"+y": [recess(-0.20 * k, 0.06 * k, -0.12 * k, 0.12 * k, 0.008 * k, MEDIUM, DARK,
                                                   [centered_recess(x, 0, 0.05 * k, 0.16 * k, 0.014 * k, BLACK, DARK) for x in (-0.15 * k, -0.07 * k, 0.01 * k)])]})
        for x in (-0.20 * k, 0.06 * k):
            for z in (-0.12 * k, 0.12 * k):
                c.screw((x, yt, z), 1, 1, 0.016 * k, 0.012 * k, LIGHT)
        # exhaust: leaves the cylinder head through -X, goes down and into a muffler lying on the plate (along Z), with two
        # clamps and the chrome tip coming out the back (-Z)
        ye = top + 0.02 * k; xe = -(hx - 0.08 * k); zex = 0.06 * k; ysil = yb + 0.05 * k; rs = 0.045 * k
        c.tube([(-0.23 * k, ye, zex), (xe, ye, zex), (xe, ysil + 0.02 * k, zex)], 0.028 * k, DARK, n=8)
        c.cylinder(2, (xe, ysil, -0.11 * k), rs, 0.64 * k, MEDIUM, n=12, chamfer=0.01 * k)
        c.cylinder(2, (xe, ysil, -(hz - 0.045 * k)), rs * 0.75, 0.05 * k, METAL, n=12, chamfer=0.006 * k)
        c.cylinder(2, (xe, ysil, -(hz - 0.022 * k)), rs * 0.55, 0.006 * k, BLACK, n=12)
        for z in (-0.34 * k, 0.06 * k):   # clamps outside the cradles' shadow (z +-0.125k..0.275k)
            c.ring(2, (xe, ysil, z), rs, rs + 0.008 * k, 0.03 * k, DARK, n=12)
            c.chamfered_box(xe - 0.05 * k, xe + 0.05 * k, yb - 0.005, ysil, z - 0.02 * k, z + 0.02 * k, DARK, c=0.006 * k)
        # terminal plate on the +X face of the cylinder head: the cables come out of it
        c.chamfered_box(0.10 * k, 0.13 * k, top - 0.02 * k, top + 0.07 * k, -0.09 * k, 0.09 * k, MEDIUM, c=0.006 * k)
        xtube = 0.12 * k
    else:
        cx, cz = 0.15 * k, 0.12 * k
        c.chamfered_box(-cx, cx, top - 0.05 * k, yt, -cz, cz, DARK, c=0.015 * k,
                          recesses={"+y": [recess(-cx + 0.035 * k, cx - 0.035 * k, -cz + 0.03 * k, cz - 0.03 * k, 0.008 * k, MEDIUM, DARK)]})
        for x in (-cx + 0.035 * k, cx - 0.035 * k):
            for z in (-cz + 0.03 * k, cz - 0.03 * k):
                c.screw((x, yt, z), 1, 1, 0.016 * k, 0.012 * k, LIGHT)
        xtube = cx
    # ---------------- cables: leave the box at the arc's level, wrap around the body (between the last fin and the cap) and go down
    # straight to a terminal on the plate
    ra = r + 0.055 * k; x1 = xtube + 0.03 * k
    a0 = math.degrees(math.acos(min(1.0, x1 / ra)))
    ytube = cy + ra * math.sin(math.radians(a0))
    ports_side = 1 if (zs and sum(zs) / len(zs) > 0) else -1
    for zc in (-(L / 2 - 0.02 * k), (L / 2 - 0.02 * k)):
        if zs and zs[-1] - zs[0] <= 0.2 and (zc > 0) != (ports_side > 0): continue   # a single port group: a single cable, on their side
        pts = [(xtube - 0.01 * k, ytube, zc * 0.3), (x1, ytube, zc)]
        ang = a0 - 15
        while ang > 8:
            a = math.radians(ang); pts.append((ra * math.cos(a), cy + ra * math.sin(a), zc)); ang -= 15
        pts.append((ra, cy, zc)); pts.append((ra, yb - 0.01, zc))
        c.tube(pts, 0.02 * k, DARK, n=8)
        xt = min(ra, hx - 0.045 * k)   # the terminal doesn't go past the +X face
        c.chamfered_box(xt - 0.04 * k, xt + 0.04 * k, yb - 0.005, yb + 0.06 * k, zc - 0.045 * k, zc + 0.045 * k, MEDIUM, c=0.008 * k)
    return c, p


# ------------------------------------------------------------------ piston (0.5 quarter: base glues at the bottom, top loose)
PISTON_HEAD_POS, PISTON_ROD_POS, PISTON_ROD_REST = (0, 0.225, 0), (0, 0.105, 0), 0.095   # the plugin (JointSystem.cs) uses the same numbers


def piston(bands, ports=None, s=1.0, guide=False):
    """s = block scale (1 = 0.5 m; 0.5 = Mini Piston of 0.25 m, 0.20.0). The ports' pockets, the knob and the LEDs stay at
    real size (they are game parts / clickable); the rest scales. guide (0.20.0): extra renderer "Guide" = a 1 m bar along
    +Y from the top face (the game's glass, y scale 0 at rest) — the plugin stretches it to the stroke when H is on."""
    c, p = Mesh(), Mesh()
    h = 0.25 * s
    rec_x = ports_on_face(c, ports, 0, 1, h)
    rec_side = [centered_recess(-0.135 * s, 0, 0.13 * s, 0.36 * s, 0.006 * s, PAINT, DARK,
                                 [centered_recess(-0.135 * s, z * s, 0.09 * s, 0.02 * s, 0.012 * s, BLACK, DARK) for z in (-0.12, -0.06, 0, 0.06, 0.12)])]
    rec_z = [centered_recess(0, -0.135 * s, 0.36 * s, 0.11 * s, 0.006 * s, PAINT, DARK, [centered_recess(0, -0.135 * s, 0.3 * s, 0.05 * s, 0.008 * s, MEDIUM, DARK)])]
    # 0.22.0: pass-through ports (slip ring); 0.22.1 (Andre: "the ports that are parallel to each other (in an L) don't connect properly ...
    # the ideal is to put the ports on the side opposite the other ports"): on the -X face (opposite the input/power), in place of the ventilation grille
    rec_xn = ports_on_face(c, ports, 0, -1, -h)
    # 0.20.0: on the mini the plate goes up to y = 0 (the port's pocket, 0.112, didn't fit in a 0.115 plate) and the chamfers are smaller
    yp = -0.02 * s if s >= 1 else 0.0
    ct, cb, cv = (0.02 * s, 0.006 * s, 0.015 * s) if s >= 1 else (0.004, 0.003, 0.0075)
    p.chamfered_box(-h, h, -h, yp, -h, h, PAINT, top=ct, base=cb, vert=cv,
                      recesses={"+x": rec_x, "-x": rec_xn or rec_side, "+z": rec_z, "-z": rec_z,
                                "-y": [centered_recess(0, 0, 0.36 * s, 0.36 * s, 0.01 * s, PAINT, DARK)]})
    # cylinder sleeve: dark octagon with bands (1/2/3 = piston 2/4/8; 0.20.0: 3 on the single piston) and a flange with 4 screws
    c.lathe([(0.18 * s, yp), (0.20 * s, yp + 0.02 * s), (0.20 * s, 0.09 * s), (0.185 * s, 0.105 * s)], 1, (0, 0, 0), DARK, n=8, rot0=math.pi / 8)
    for i in range(bands):
        c.ring(1, (0, (0.02 + i * 0.03) * s, 0), 0.20 * s, 0.212 * s, 0.014 * s, LIGHT, n=8, rot0=math.pi / 8)
    for i in range(4):
        a = math.pi / 4 + i * math.pi / 2
        c.screw((0.155 * s * math.cos(a), 0.105 * s, 0.155 * s * math.sin(a)), 1, 1, 0.014 * s, 0.01 * s, LIGHT)
    # (0.22.2, Andre: "that weird little thing, remove it": the hydraulic hose is gone)
    # 0.11.0: rod and head are SEPARATE renderers (the plugin stretches the rod and raises the head together with the block above):
    # head = plate centered on its own origin, placed at (0, 0.225, 0); rod = 1 m cylinder (y 0..1) placed at the sleeve's mouth
    # (0, 0.105, 0) with y scale = 0.095 at rest (PostTransformMatrix in the game). Everything x s on the mini.
    hdr = Mesh()
    hdr.chamfered_box(-0.2 * s, 0.2 * s, -0.025 * s, 0.025 * s, -0.2 * s, 0.2 * s, MEDIUM, c=0.012 * s,
                        recesses={"+y": [centered_recess(x * s, z * s, 0.036 * s, 0.036 * s, 0.006 * s, BLACK, DARK) for x in (-0.15, 0.15) for z in (-0.15, 0.15)]
                                  + [centered_recess(0, 0, 0.16 * s, 0.16 * s, 0.004 * s, BODY, DARK)]})
    rod = Mesh()
    rod.cylinder(1, (0, 0.5, 0), 0.06 * s, 1.0, METAL, n=12)
    extras_ = [("Head", hdr, tuple(v * s for v in PISTON_HEAD_POS), (1, 1, 1)), ("Rod", rod, tuple(v * s for v in PISTON_ROD_POS), (1, PISTON_ROD_REST * s, 1))]
    kx, ky = PISTON_KNOB_S[s]
    extras_.append(knob_panel(c, (kx, ky, h - 0.006 * s), 1))   # 0.19.2: stroke knob
    # 0.12.1 (Andre: "show something visual on the pistons"): 3 status LEDs on the +Z panel — green = active, yellow = guide (parallel to
    # another piston), red = welded (it would close a loop / held by another path). Hidden; the plugin pushes out the state's one.
    zf = h - 0.014 * s   # bottom of the recessed panel (+Z)
    ly = PISTON_LED_Y * s
    for x, sfx in zip(PISTON_LED_XS[s], ("LedVerde", "LedAmarelo", "LedVermelho")):
        color = GREEN_LIGHT if sfx == "LedVerde" else YELLOW_LIGHT if sfx == "LedAmarelo" else LIGHT_COL
        c.ring(2, (x, ly, zf + 0.002 * s), 0.012 * s, 0.018 * s, 0.004 * s, BLACK, n=10)
        c.cylinder(2, (x, ly, zf + 0.0005 * s), 0.012 * s, 0.001 * s, BLACK, n=10)
        led = Mesh(); led.cylinder(2, (0, 0, 0), 0.011 * s, 0.016 * s, color, n=10, chamfer=0.002 * s)
        extras_.append((sfx, led, (x, ly, PISTON_LED_Z * s), (1, 1, 1)))
    if guide:
        g = Mesh(); side = 0.03 * s
        g.box(-side, side, 0.0, 1.0, -side, side, LIGHT)
        extras_.append(("Guide", g, (0, h, 0), (1, PISTON_GUIDE_REST, 1)))
    return c, p, extras_


def piston_tip():
    """0.20.0: Piston End (0.25 m): a block that marks how far the piston goes. Glues on all 6 faces. 0.22.1 (Andre: "too simple, change it
    a bit so it looks more original"): at the bottom a thick octagonal plate (where the rod's head rests) with the dark socket in the middle and
    4 screws; on the 4 sides a recessed panel with 3 dark ribs; on top a recessed cross target with the center pin."""
    c, p = Mesh(), Mesh()
    h = 0.125
    side = [centered_recess(0, 0, 0.19, 0.19, 0.004, PAINT, DARK, [centered_recess(u, 0, 0.018, 0.15, 0.006, BLACK, DARK) for u in (-0.05, 0.0, 0.05)])]
    cross_ = [centered_recess(0, 0, 0.19, 0.03, 0.004, DARK, DARK), centered_recess(0, 0, 0.03, 0.19, 0.004, DARK, DARK)]
    p.chamfered_box(-h, h, -h, h, -h, h, PAINT, c=0.01,
                      recesses={"-y": [centered_recess(0, 0, 0.23, 0.23, 0.016, DARK, DARK)], "+y": cross_,
                                "+x": side, "-x": side, "+z": side, "-z": side})
    # octagonal plate inside the bottom recess (1.6 cm): nothing goes past the block's box; dark socket and light ring at its bottom
    c.cylinder(1, (0, -h + 0.010, 0), 0.105, 0.012, MEDIUM, n=8, rot0=math.pi / 8, chamfer=0.003)
    c.ring(1, (0, -h + 0.0015, 0), 0.05, 0.075, 0.003, LIGHT, n=8, rot0=math.pi / 8)
    c.cylinder(1, (0, -h + 0.001, 0), 0.05, 0.002, BLACK, n=8, rot0=math.pi / 8)
    for i in range(4):
        a = math.pi / 4 + i * math.pi / 2
        c.screw((0.088 * math.cos(a), -h + 0.004, 0.088 * math.sin(a)), 1, -1, 0.009, 0.0035, LIGHT)
    # pin at the center of the target (on top)
    c.cylinder(1, (0, h - 0.004 + 0.002, 0), 0.012, 0.004, LIGHT, n=8, rot0=math.pi / 8)
    return c, p, []


def slip_ring(ports=None, w=0.5):
    """0.22.0 Slip Ring; 0.22.4 (Andre: "do it like the logic blocks, 2x1x2 ... normal and mini, both work for both parts, it's just to
    align better"): PLATE w x 0.125 x w (normal 0.5 = like the mini solar panel; mini 0.25 = like the logic blocks). Glues on all 6
    faces, goes on the moving piece. 4 pass-through ports, 2 on each opposite side (ports 1-2 on -X, 3-4 on +X). Top and bottom, the
    contact disc (dark cap with 3 light tracks, the same on both sides so it can be flipped); on the +-Z faces a light band."""
    c, p = Mesh(), Mesh()
    hx, hy = w / 2, 0.0625; k = w / 0.5
    band = [centered_recess(0, 0, 0.36 * k, 0.05, 0.003, PAINT, DARK, [centered_recess(0, 0, 0.3 * k, 0.018, 0.003, LIGHT, DARK)])]
    rec = {"-x": ports_on_face(c, ports, 0, -1, -hx), "+x": ports_on_face(c, ports, 0, 1, hx), "+z": band, "-z": band,
           "+y": [centered_recess(0, 0, 0.44 * k, 0.44 * k, 0.002, PAINT, DARK)], "-y": [centered_recess(0, 0, 0.44 * k, 0.44 * k, 0.002, PAINT, DARK)]}
    p.chamfered_box(-hx, hx, -hy, hy, -hx, hx, PAINT, c=0.004, recesses=rec)
    for sy in (1, -1):
        y = sy * (hy - 0.002)   # bottom of the face's recess
        c.cylinder(1, (0, y + sy * 0.00075, 0), 0.2 * k, 0.0015, DARK, n=16, rot0=math.pi / 16)
        for r0 in (0.065, 0.11, 0.155):
            c.ring(1, (0, y + sy * 0.00175, 0), r0 * k, (r0 + 0.02) * k, 0.0005, LIGHT, n=16, rot0=math.pi / 16)
        c.cylinder(1, (0, y + sy * 0.00175, 0), 0.025 * k, 0.0005, BLACK, n=8, rot0=math.pi / 8)
        for i in range(4):
            a = math.pi / 4 + i * math.pi / 2
            c.screw((0.185 * k * math.cos(a), y + sy * 0.00025, 0.185 * k * math.sin(a)), 1, sy, 0.008 * max(k, 0.75), 0.00175, LIGHT)
    return c, p, []


def piston_end(w=0.5):
    """0.22.4 (Andre: "do the same for the piston end, exactly the same"): Piston End as a PLATE w x 0.125 x w (normal 0.5 / mini 0.25), works with
    both pistons. Glues on all 6 faces. At the bottom (-Y, the face looking at the piston) the octagonal socket with 4 screws inside a dark
    recess; on top the cross target with the center pin; on the sides a panel with 3 ribs."""
    c, p = Mesh(), Mesh()
    hx, hy = w / 2, 0.0625; k = w / 0.5
    side = [centered_recess(0, 0, 0.38 * k, 0.07, 0.003, PAINT, DARK, [centered_recess(u * k, 0, 0.03 * k, 0.05, 0.004, BLACK, DARK) for u in (-0.1, 0.0, 0.1)])]
    cross_ = [centered_recess(0, 0, 0.38 * k, 0.04 * k, 0.003, DARK, DARK), centered_recess(0, 0, 0.04 * k, 0.38 * k, 0.003, DARK, DARK)]
    p.chamfered_box(-hx, hx, -hy, hy, -hx, hx, PAINT, c=0.004,
                      recesses={"-y": [centered_recess(0, 0, 0.44 * k, 0.44 * k, 0.012, DARK, DARK)], "+y": cross_,
                                "+x": side, "-x": side, "+z": side, "-z": side})
    c.cylinder(1, (0, -hy + 0.0075, 0), 0.2 * k, 0.009, MEDIUM, n=8, rot0=math.pi / 8, chamfer=0.002)   # socket plate (inside the recess)
    c.ring(1, (0, -hy + 0.0015, 0), 0.1 * k, 0.15 * k, 0.003, LIGHT, n=8, rot0=math.pi / 8)
    c.cylinder(1, (0, -hy + 0.001, 0), 0.1 * k, 0.002, BLACK, n=8, rot0=math.pi / 8)
    for i in range(4):
        a = math.pi / 4 + i * math.pi / 2
        c.screw((0.175 * k * math.cos(a), -hy + 0.0035, 0.175 * k * math.sin(a)), 1, -1, 0.012 * k, 0.003, LIGHT)
    c.cylinder(1, (0, hy - 0.003 + 0.0015, 0), 0.02 * k, 0.003, LIGHT, n=8, rot0=math.pi / 8)   # target pin
    return c, p, []


# ------------------------------------------------------------------ bearing (0.5 quarter: base glues at the bottom, top loose and turns)
# 0.23.0 (Andre: "add a data output ... on the side that has the knob but the opposite side, and move the LED more to the side ...
# the LED moves more to the center, it sits between the knob and the new data output ... a switch ... on the right of the knob"):
# the bearing's +Z panel now has, from left to right (seen from outside, +X on the left): the angle OUTPUT (port, cell
# +X of the bottom row), lock LED, servo knob and the degrees / -1..1 SWITCH. On the mini it doesn't all fit on one face: the output and the
# LED go to the +Z panel (with the knob) and the switch goes to the -Z face, in place of the second LED (which is gone).
BEARING_LED = (0.025, -0.16)   # (x, y) of the lock LED on the +Z panel (0.17.0; the plugin looks for this position)
BEARING_KNOB = (-0.12, -0.16)   # 0.19.2: servo angle knob (10..360 degrees, continuous), +Z panel
BEARING_SWITCH = (-0.205, -0.16)   # 0.23.0: switch of the angle output (on = degrees, off = -1..1)
PISTON_KNOB = (-0.12, -0.135)   # 0.19.2: stroke knob (10..100 %), +Z panel
PISTON_KNOB_S = {1.0: PISTON_KNOB, 0.5: (-0.07, -0.0675)}   # 0.20.0: Mini Piston (knob at real size)
BEARING_KNOB_S = {1.0: BEARING_KNOB, 0.5: (-0.065, -0.08)}; BEARING_LED_S = {1.0: BEARING_LED, 0.5: (0.065, -0.08)}   # 0.20.0: Mini Bearing
# 0.23.0: on the mini the two free cells are on the -X side, so the angle output goes to the -Z face (next to the pass-through one) and the
# +Z panel gets the LED (+X edge), switch (middle) and knob — the switch doesn't fit on the right of the knob on a 0.25 block.
BEARING_SWITCH_S = {1.0: BEARING_SWITCH, 0.5: (0.010, -0.08)}
BEARING_PANEL_S = {1.0: (-0.0625, 0.345), 0.5: (-0.010, 0.190)}   # (center x, width) of the +Z panel: makes room for the switch and gets out from under the output's pocket
def knob_panel(c, pos, axis_v, n_marks=0, r=0.02, h=0.016, sfx="Knob"):
    """0.19.2: knob lying on a vertical panel (axis = +1 panel +Z / -1 panel -Z): ring of marks on the panel + the knob (extra "Knob").
    0.19.3 (Andre: "the knob on the bearing is inside the model"): the game's EPC_Actionable_Knob WRITES the knob's rotation
    (lerp from _rotationMin to _rotationMax), so the rest rotation (90,0,0) was discarded and the knob had its axis in +Y, inside
    the block. Now the mesh is already modeled along Z (out of the panel) and the game turns it in Z: _rotationMin (0,0,-150) /
    _rotationMax (0,0,150) (gen_prefabs), without a rest rotation."""
    x, y, z = pos
    c.ring(2, (x, y, z + axis_v * 0.0008), r + 0.004, r + 0.011, 0.0016, DARK, n=16)
    knob = Mesh()
    knob.cylinder(2, (0, 0, axis_v * 0.007), r, h, MEDIUM, n=12, chamfer=0.002)
    zt = axis_v * (0.007 + h / 2)
    knob.box(-0.0022, 0.0022, 0.003, r - 0.0025, min(zt, zt + axis_v * 0.001), max(zt, zt + axis_v * 0.001), LIGHT)   # pointer line (up in the middle of the travel)
    return (sfx, knob, (x, y, z), (1, 1, 1), (0, 0, 0))
def switch_panel(c, pos, axis_v, scl=1.0, sfx="Switch"):
    """0.23.0: two-position switch on a vertical panel (axis = +1 panel +Z / -1 panel -Z). It's the game's ActionableToggle — the
    same as the Datameter's Input Switch and the mini thruster's reverse button: the game writes the rod's pose (_buttonTransformOn/Off),
    so the rod is modeled along Z (out of the panel) and the prefab tilts it in X."""
    x, y, z = pos
    lu, lv = 0.055 * scl, 0.034 * scl
    c.box(x - lu / 2, x + lu / 2, y - lv / 2, y + lv / 2, z - axis_v * 0.004 * scl, z + axis_v * 0.0008, DARK)
    c.box(x - lu / 2 + 0.006 * scl, x + lu / 2 - 0.006 * scl, y - lv / 2 + 0.006 * scl, y + lv / 2 - 0.006 * scl,
            z - axis_v * 0.007 * scl, z + axis_v * 0.0004, BLACK)
    sw = Mesh()
    # (the rod starts INSIDE the foot so no faces are left touching — check_mesh)
    sw.box(-0.009 * scl, 0.009 * scl, -0.0055 * scl, 0.0055 * scl, *sorted((axis_v * 0.004 * scl, axis_v * 0.019 * scl)), color=LIGHT)
    sw.box(-0.011 * scl, 0.011 * scl, -0.008 * scl, 0.008 * scl, *sorted((-axis_v * 0.002 * scl, axis_v * 0.006 * scl)), color=MEDIUM)
    return (sfx, sw, (x, y, z), (1, 1, 1), (0, 0, 0))


GYRO_GREEN = (56, 168, 96, 54)   # 0.19.14: Gyro Bearing ring
def bearing(ports=None, spin=False, s=1.0):
    """s = scale (1 = 0.5 m; 0.5 = Mini Bearing / Mini Gyro Bearing of 0.25 m, 0.20.0 — a single input, knob and LED at real size)."""
    c, p = Mesh(), Mesh()
    h = 0.25 * s
    rec_y = ports_on_face(c, ports, 1, -1, -h)   # the inputs are on the bottom face
    # 0.22.0 (Andre: "add more ports to the bearing ... on the sides, keep the 4 bottom ports that are only for movement intact,
    # and add 4 more on the side opposite the knob"): PASS-THROUGH ports (slip ring) on the -Z face of the base. On the bearing they fit in the bottom
    # row (the base only goes up to y -0.04); the -Z panel and LED go away (the lock LED stays only on the +Z panel). On the mini the base is 8.5 cm
    # tall: the pocket (11.2 cm) is cut at the top of the base — the plug sticks out a bit, out of the track's way.
    rec_zn = ports_on_face(c, ports, 2, -1, -h, frame=s >= 1)
    rec_zp = ports_on_face(c, ports, 2, 1, h, frame=s >= 1)   # 0.23.0: angle output on the knob's face
    side = [centered_recess(-0.16 * s, 0, 0.09 * s, 0.32 * s, 0.006 * s, PAINT, DARK, [centered_recess(-0.16 * s, z * s, 0.05 * s, 0.02 * s, 0.012 * s, BLACK, DARK) for z in (-0.1, -0.05, 0, 0.05, 0.1)])]
    # 0.22.0: on the mini the base rises from -0.04 to -0.025 (the side port's plug, 7 cm, fits inside) and the track gets 2 cm
    # 0.22.2 (Andre: "make the mini bearing's base just a little taller"): on the mini the base goes up to y 0 (the side port's pocket, down to -0.0065, fits
    # entirely) and the disc starts 4 mm above (y0d); the track sits in those 4 mm
    base_top = -0.08 * s if s >= 1 else 0.0
    y0d = -0.01 * s if s >= 1 else 0.004
    p.chamfered_box(-h, h, -h, base_top, -h, h, PAINT, top=0.015 * s, base=0.006 * s, vert=0.015 * s,
                      recesses={"-y": rec_y, "+x": side, "-x": side,
                                "+z": rec_zp + [centered_recess(BEARING_PANEL_S[s][0], -0.16 * s, BEARING_PANEL_S[s][1], 0.09 * s, 0.006 * s, PAINT, DARK)],
                                "-z": rec_zn})
    # track: dark outer ring, inner ring, balls in the middle (from the top of the base to the disc: y -0.08..-0.01 on the bearing; -0.025..-0.005 on the mini)
    yp0, yp1 = base_top, y0d; ypc, yph = (yp0 + yp1) / 2, yp1 - yp0
    c.ring(1, (0, ypc, 0), 0.20 * s, 0.245 * s, yph, DARK, n=16, rot0=math.pi / 16, chamfer=min(0.008 * s, yph * 0.2))
    c.ring(1, (0, ypc, 0), 0.10 * s, 0.185 * s, yph, BODY, n=16, rot0=math.pi / 16, chamfer=min(0.006 * s, yph * 0.2))
    for i in range(12):
        a = 2 * math.pi * i / 12
        c.sphere((0.1925 * s * math.cos(a), -0.02 * s if s >= 1 else ypc, 0.1925 * s * math.sin(a)), 0.02 * s if s >= 1 else 0.004, LIGHT, n=8, m=4)
    # top disc (paintable) + dark cap with screw holes and a hub
    p.lathe([(0.19 * s, y0d), (0.225 * s, y0d + 0.03 * s), (0.225 * s, 0.20 * s), (0.205 * s, 0.22 * s)], 1, (0, 0, 0), PAINT, n=16, rot0=math.pi / 16)
    c.cylinder(1, (0, 0.235 * s, 0), 0.215 * s, 0.03 * s, MEDIUM, n=16, rot0=math.pi / 16, chamfer=0.008 * s)
    for i in range(6):
        a = math.pi / 6 + i * math.pi / 3
        screw_hole(c, (0.16 * s * math.cos(a), 0.25 * s, 0.16 * s * math.sin(a)), 1, 1, 0.016 * s)
    c.cylinder(1, (0, 0.2503 * s, 0), 0.06 * s, 0.0006, BODY, n=8, rot0=math.pi / 8)
    if spin:
        # 0.19.14: Gyro Bearing — instead of the arrows, a green ring on the side of the disc (the horizon line) and one on the track:
        # the top side aims by itself to stay level, it has no turning "direction"
        c.ring(1, (0, 0.11 * s, 0), 0.2255 * s, 0.2295 * s, 0.014 * s, GYRO_GREEN, n=16, rot0=math.pi / 16)
        c.ring(1, (0, (-0.045 + 0.036) * s if s >= 1 else yp1 - 0.001, 0), 0.20 * s, 0.245 * s, 0.006 * s if s >= 1 else 0.0015, GYRO_GREEN, n=16, rot0=math.pi / 16)
    else: spin_arrows(c, 0.2255 * s, 0.11 * s, wid=0.009 * s)
    # 0.17.0 (Andre: "lock the bearings and show the locked bearing visually"): red LED on the base's +Z/-Z panels (like the
    # recoupler's): hidden 2 cm inside, the plugin pushes it out when its shaft network locks (opposite signals)
    # 0.23.0: a single LED (on the +Z panel, between the angle output and the knob); on the mini the second LED left so the switch fits on the -Z face
    extras_ = []
    lx, ly = BEARING_LED_S[s]
    zf = h - 0.006 * s
    c.ring(2, (lx, ly, zf + 0.002 * s), 0.012 * s, 0.02 * s, 0.004 * s, BLACK, n=8, rot0=math.pi / 8)
    c.cylinder(2, (lx, ly, zf + 0.0005 * s), 0.012 * s, 0.001 * s, BLACK, n=8, rot0=math.pi / 8)
    led = Mesh(); led.cylinder(2, (0, 0, 0), 0.011 * s, 0.016 * s, LIGHT_COL, n=8, rot0=math.pi / 8, chamfer=0.003 * s)
    extras_.append(("LedA", led, (lx, ly, zf - 0.02 * s), (1, 1, 1)))
    kx, ky = BEARING_KNOB_S[s]
    extras_.append(knob_panel(c, (kx, ky, h - 0.006 * s), 1))   # 0.19.2: servo angle knob
    sx, sy = BEARING_SWITCH_S[s]
    extras_.append(switch_panel(c, (sx, sy, h - 0.006 * s), 1, 1.0 if s >= 1 else 0.85))
    return c, p, extras_


def spin_arrows(c, r, y, n=3, arc=50, wid=0.009, color=LIGHT):
    """0.12.14 (Andre: "make the servo/bearing thing visual"): discreet little arrows on the side of the top disc, in the direction
    the top side turns with a POSITIVE signal: counter-clockwise seen from above (from +X to +Z — in Unity, Y axis up, that's
    the negative turn by the right-hand rule, which is what Crap Mechanics' motor/servo do with a signal > 0). In the mirrored version
    (V) the mesh is drawn mirrored (scale -1 in X), so the arrow becomes clockwise — and the plugin reverses the M bearing's turn."""
    import numpy as _np
    rr = r + 0.003
    def P(a, yy): return (rr * math.cos(a), yy, rr * math.sin(a))
    for k in range(n):
        a0 = math.radians(k * 360.0 / n + 20); a1 = a0 + math.radians(arc); steps = 6
        for i in range(steps):
            u0 = a0 + (a1 - a0) * i / steps; u1 = a0 + (a1 - a0) * (i + 1) / steps
            c.quad_(P(u0, y - wid / 2), P(u1, y - wid / 2), P(u1, y + wid / 2), P(u0, y + wid / 2), color, center=(0, y, 0))
        tip = a1 + math.radians(9)
        c.tri(P(a1, y - wid * 1.6), P(a1, y + wid * 1.6), P(tip, y), color, center=(0, y, 0))


# ------------------------------------------------------------------ recoupler (0.5 quarter; glues on all 6 faces; the bottom face recouples to the decoupler)
RECOUPLER_LED = (0.12, 0.19)        # (x, y) of the LED on the +Z/-Z faces; hidden 2 cm inside (rest), out when it couples
RECOUPLER_LED_DEPTH = 0.02


def recoupler(ports=None):
    """Mirror of the game's decoupler (they sit face to face): 16-sided round mouth at the bottom, with 4 big pins
    and 4 small ones in place of its 4 big and 4 small holes; 0.25 square neck; frustum widening up to the top
    cap (paintable). The port is on the top band, +X face (0.10.2)."""
    c, p = Mesh(), Mesh()
    h = 0.25
    # mouth: 16-sided disc (r 0.2415 like the game's), bottom recessed 6 mm (black) where the pins sit, flush with the face
    rb, rd = 0.20, 0.2415
    c.lathe([(rb, -h + 0.006), (rb, -h), (rd - 0.01, -h), (rd, -h + 0.01), (rd, -0.14), (rd - 0.01, -0.13)], 1, (0, 0, 0), BODY,
            n=16, rot0=math.pi / 16, colors=[BLACK, BODY, BODY, BODY, BODY])
    for sx in (-1, 1):
        for sz in (-1, 1):
            c.cylinder(1, (sx * 0.10, -h + 0.003, sz * 0.10), 0.055, 0.006, LIGHT, n=8, rot0=math.pi / 8)
    for ang in range(4):
        a = ang * math.pi / 2
        c.cylinder(1, (0.165 * math.cos(a), -h + 0.003, 0.165 * math.sin(a)), 0.022, 0.006, LIGHT, n=6)
    # dark collar at the base of the neck
    c.ring(1, (0, -0.125, 0), 0.15, 0.185, 0.03, DARK, n=16, rot0=math.pi / 16, chamfer=0.004)   # goes 1 cm into the mouth
    # square neck + widening frustum + top cap (paintable), cap with a recessed panel and 4 screws inside it
    p.chamfered_box(-0.125, 0.125, -0.13, -0.0075, -0.125, 0.125, PAINT, top=0.0, base=0.0, vert=0.01)
    p.frustum(-0.125, 0.125, -0.125, 0.125, -0.0075, 0.125, -0.125, PAINT)
    # 0.10.2 (Andre): the port is on the top band (y 0.1875), on the +X face; smaller chamfers there so the pocket fits
    rec_x = ports_on_face(c, ports, 0, 1, h) + [centered_recess(0.19, -0.04, 0.06, 0.26, 0.006, MEDIUM, DARK)]
    rec_xn = ports_on_face(c, ports, 0, -1, -h)   # 0.22.0: 4 pass-through ports (slip ring) on the top band of the -X face
    p.chamfered_box(-h, h, 0.125, h, -h, h, PAINT, top=0.006, base=0.0, vert=0.006,
                      recesses={"+y": [centered_recess(0, 0, 0.40, 0.40, 0.010, PAINT, DARK)],
                                "+x": rec_x,
                                "-x": rec_xn,
                                "+z": [centered_recess(0, 0.19, 0.34, 0.06, 0.006, MEDIUM, DARK)],
                                "-z": [centered_recess(0, 0.19, 0.34, 0.06, 0.006, MEDIUM, DARK)]})
    for sx in (-1, 1):
        for sz in (-1, 1):
            c.screw((sx * 0.17, h - 0.010, sz * 0.17), 1, 1, 0.016, 0.008, LIGHT)
    # 4 dark ribs on the frustum's corners (from the collar to the cap)
    for sx in (-1, 1):
        for sz in (-1, 1):
            m = Mesh()
            m.frustum(-0.02, 0.02, -0.02, 0.02, -0.01, 0.12, -0.02, DARK)
            _shift(c, m.tris, [sx * 0.125, 0, sz * 0.125])
    # 0.11.2 (Andre: "a little detail when the recoupler and the decoupler are locked"): black socket on the panel of the faces
    # +Z and -Z, with a green LED (extra renderer) that sits 2 cm inside (off/hidden) and comes out when it couples
    lx, ly = RECOUPLER_LED
    extras_ = []
    for sz, sfx in ((1, "LedA"), (-1, "LedB")):
        zf = sz * (h - 0.006)   # bottom of the recessed panel
        c.ring(2, (sz * lx, ly, zf + sz * 0.002), 0.012, 0.02, 0.004, BLACK, n=8, rot0=math.pi / 8)      # ring 4 mm out of the panel
        c.cylinder(2, (sz * lx, ly, zf + sz * 0.0005), 0.012, 0.001, BLACK, n=8, rot0=math.pi / 8)      # "hole" 1 mm outward (nothing coplanar)
        led = Mesh(); led.cylinder(2, (0, 0, 0), 0.011, 0.016, GREEN_LIGHT, n=8, rot0=math.pi / 8, chamfer=0.003)
        extras_.append((sfx, led, (sz * lx, ly, zf - sz * RECOUPLER_LED_DEPTH), (1, 1, 1)))
    return c, p, extras_


# ------------------------------------------------------------------ beacon (0.25, the Wireless Transmitter's size; uses 5 P/s)
# 0.11.1: wedge console like the transmitter; 0.11.2 (Andre): "it's too big, make it the transmitter's size, just a slightly
# bigger antenna, without the red tip, without a label". 0.25 block; the antenna goes up above the block (like the transmitter's rods).
BEACON_H = 0.125                     # half block
BEACON_BASE_TOP = -BEACON_H + 0.05  # top of the paintable plate (5 cm)
BEACON_BACK, BEACON_FRONT = 0.09, 0.045   # lid height: back half flat (y BACK), front half goes down to FRONT
BEACON_MID = 0.095                  # half width of the wedge
BEACON_TILT = math.degrees(math.atan2(BEACON_BACK - BEACON_FRONT, BEACON_MID))   # tilt of the front half (degrees)
BEACON_ANTENNA_TOP = 0.34            # antenna tip (above the 0.25 block: y up to 0.125 + 0.215)
BEACON_KNOB = (-0.045, BEACON_BACK - 0.003, 0.045)   # 0.17.0: icon knob, in the recess of the flat lid (+Z side, away from the antenna)
def _beacon_display():
    """display point (origin of the lit icon, 0.6 mm above the bottom of the dark panel) and the normal of the tilted lid, in the block."""
    i = math.radians(BEACON_TILT); sn, cs = math.sin(i), math.cos(i)
    lx, ly, lz = 0.0, -0.003 + 0.0006, -0.03                      # in the lid's frame (before rotating)
    x = lx * cs + ly * sn; y = -lx * sn + ly * cs                    # rotate(2, -TILT)
    return (x + BEACON_MID / 2, y + (BEACON_BACK + BEACON_FRONT) / 2, lz), (sn, cs, 0.0)
BEACON_ICON_P0, BEACON_ICON_N = _beacon_display()
BEACON_ICON_HIDE = 0.004         # icon off: 4 mm (+1 mm per icon) inside the lid


def _rotate_z(p, degs):
    a = math.radians(degs); cs, sn = math.cos(a), math.sin(a)
    return np.array([p[0] * cs - p[1] * sn, p[0] * sn + p[1] * cs, p[2]])


def beacon(ports=None):
    c, p = Mesh(), Mesh()
    h = BEACON_H
    yb = BEACON_BASE_TOP
    # L-shaped paintable base: 5 cm plate + back wall (x -0.125..-0.095, up to y 0) with the power port on the -X face (the port's
    # cell is 0.125 tall: it needs a wall up to y 0 so the plug isn't left in the air)
    xw = -BEACON_MID
    rec_x = ports_on_face(c, ports, 0, -1, -h)
    p.chamfered_box(-h, xw, -h, 0.0, -h, h, PAINT, c=0.004, recesses={"-x": rec_x, "+y": [centered_recess((-h + xw) / 2, 0, 0.018, 0.21, 0.003, PAINT, DARK)]})
    p.chamfered_box(xw, h, -h, yb, -h, h, PAINT, top=0.006, base=0.004, vert=0.004,
                      recesses={"+y": [centered_recess((xw + h) / 2, 0, 0.19, 0.21, 0.003, PAINT, DARK)]})
    for sx in (0.108, -0.078):
        for sz in (-1, 1):
            c.screw((sx, yb, sz * 0.108), 1, 1, 0.007, 0.004, LIGHT)
    # console: back half with the flat lid, front half tilted (Andre: "half on the diagonal and half flat").
    # The body (BODY) sits 6 mm below the light lids so nothing is coplanar.
    m = BEACON_MID; yt, yf = BEACON_BACK - 0.006, BEACON_FRONT - 0.006
    xa = -m - 0.002   # back face of the console 2 mm inside the wall (nothing coplanar with the plate)
    b = [np.array(q, float) for q in ((xa, yb - 0.01, -m), (m, yb - 0.01, -m), (m, yb - 0.01, m), (xa, yb - 0.01, m))]
    t = [np.array(q, float) for q in ((xa, yt, -m), (0, yt, -m), (m, yf, -m), (m, yf, m), (0, yt, m), (xa, yt, m))]
    ctr = np.array([0, (yb + yt) / 2, 0])
    c.quad_(t[0], t[1], t[4], t[5], BODY, center=ctr); c.quad_(t[1], t[2], t[3], t[4], BODY, center=ctr)   # flat + tilted lid
    c.quad_(b[0], b[1], b[2], b[3], BODY, center=ctr)                                                        # bottom
    c.quad_(b[0], b[1], t[2], t[0], BODY, nrm=(0, 0, -1)); c.tri(t[0], t[2], t[1], BODY, nrm=(0, 0, -1))   # -Z side (pentagon)
    c.quad_(b[3], b[2], t[3], t[5], BODY, nrm=(0, 0, 1)); c.tri(t[5], t[3], t[4], BODY, nrm=(0, 0, 1))     # +Z side
    c.quad_(b[1], b[2], t[3], t[2], BODY, nrm=(1, 0, 0)); c.quad_(b[3], b[0], t[0], t[5], BODY, nrm=(-1, 0, 0))   # front / back
    # flat lid (back) with the antennas' bases; tilted lid (front) with a dark panel, LED and grille
    lid = Mesh()
    lid.chamfered_box(-m - 0.003, 0.003, BEACON_BACK - 0.012, BEACON_BACK, -m - 0.003, m + 0.003, LIGHT, c=0.004,
                          recesses={"+y": [centered_recess(-0.045, 0.0, 0.07, 0.15, 0.003, LIGHT, DARK)]})
    _shift(c, lid.tris, [0, 0, 0])
    L = math.hypot(m, BEACON_BACK - BEACON_FRONT) / 2
    lid = Mesh()
    lid.chamfered_box(-L, L + 0.003, -0.012, 0.0, -m - 0.003, m + 0.003, LIGHT, c=0.004,
                          recesses={"+y": [centered_recess(0.0, -0.03, 0.07, 0.10, 0.003, DARK, DARK),        # dark panel
                                           centered_recess(0.0, 0.055, 0.026, 0.026, 0.003, BLACK, DARK),      # led
                                           centered_recess(-0.03, 0.07, 0.024, 0.036, 0.003, MEDIUM, DARK,
                                                            [centered_recess(-0.03, z, 0.018, 0.005, 0.003, BLACK, DARK) for z in (0.061, 0.07, 0.079)])]})
    lid.cylinder(1, (0.0, 0.0, 0.055), 0.008, 0.006, LIGHT_COL, n=8, rot0=math.pi / 8)
    lid.rotate(2, -BEACON_TILT)
    _shift(c, lid.tris, [m / 2, (BEACON_BACK + BEACON_FRONT) / 2, 0])
    # antenna: dark mast at the back (-Z side), 2 rings, light tip (no lamp)
    xm, zm = -0.05, -0.04
    y0, y1 = 0.02, BEACON_ANTENNA_TOP
    c.cylinder(1, (xm, (y0 + y1) / 2, zm), 0.01, y1 - y0, DARK, n=8, chamfer=0.002)
    c.cylinder(1, (xm, BEACON_BACK + 0.012, zm), 0.018, 0.03, MEDIUM, n=8, rot0=math.pi / 8, chamfer=0.003)   # antenna base
    for y in (0.16, 0.25):
        c.ring(1, (xm, y, zm), 0.01, 0.016, 0.012, MEDIUM, n=8)
    c.sphere((xm, y1 + 0.012, zm), 0.014, LIGHT, n=8, m=4)
    # 0.17.0 (Andre: "on the beacon model's little display I choose which icon to show, with a knob of its own"): the second rod is gone;
    # in its place, a game knob (EPC_Actionable_Knob, 3 positions: waves / car / ship) with a ring of marks around it. The tilted lid's dark
    # panel became a display: 3 icons (extra renderers) hidden inside the lid; the plugin pushes out the chosen one.
    kx, ky, kz = BEACON_KNOB
    c.ring(1, (kx, ky + 0.0008, kz), 0.019, 0.026, 0.0016, DARK, n=16)
    for a in (-60, 0, 60):   # marks of the 3 positions
        t = Mesh(); t.rotated_box(1, math.radians(a + 90), (0.006, 0.002, 0.0016), 0.023, LIGHT); _shift(c, t.tris, np.array([kx, ky + 0.0012, kz]))
    knob = Mesh()
    knob.cylinder(1, (0, 0.007, 0), 0.016, 0.014, MEDIUM, n=12, chamfer=0.002)
    knob.box(-0.0022, 0.0022, 0.0141, 0.0151, 0.002, 0.0145, LIGHT)   # pointer line (rest = -Z... the game turns it in Y)
    extras_ = [("Knob", knob, (kx, ky, kz), (1, 1, 1))]
    for k in range(3):
        m_ = icon_display(k)
        m_.rotate(2, -BEACON_TILT)
        pos = np.array(BEACON_ICON_P0) - np.array(BEACON_ICON_N) * (BEACON_ICON_HIDE + 0.001 * k)
        extras_.append(("Icon%d" % k, m_, tuple(float(v) for v in pos), (1, 1, 1)))
    return c, p, extras_


def icon_display(k):
    """0.17.0: icon of the beacon's display on the lid's plane (x = down the ramp, z = across; y = normal), centered on the origin.
    Whoever looks from the front (from +x) sees 'up' = -x and 'right' = -z. 0 = waves (the radar icon), 1 = car, 2 = ship."""
    m = Mesh(); color = GREEN_LIGHT; n = (0, 1, 0)
    def P(u, v): return (-v, 0.0, -u)
    def ret(u0, u1, v0, v1): m.quad_(P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1), color, nrm=n)
    def disc(u, v, r, sides=12):
        for i in range(sides):
            a0, a1 = 2 * math.pi * i / sides, 2 * math.pi * (i + 1) / sides
            m.tri(P(u, v), P(u + r * math.cos(a0), v + r * math.sin(a0)), P(u + r * math.cos(a1), v + r * math.sin(a1)), color, nrm=n)
    def arc(r, wid, a0, a1, steps=8):
        for i in range(steps):
            b0 = a0 + (a1 - a0) * i / steps; b1 = a0 + (a1 - a0) * (i + 1) / steps
            ri, re = r - wid / 2, r + wid / 2
            m.quad_(P(ri * math.cos(b0), ri * math.sin(b0)), P(re * math.cos(b0), re * math.sin(b0)),
                   P(re * math.cos(b1), re * math.sin(b1)), P(ri * math.cos(b1), ri * math.sin(b1)), color, nrm=n)
    if k == 0:   # ((( . )))
        disc(0, 0, 0.0055)
        for side in (0.0, math.pi):
            for r in (0.013, 0.021):
                arc(r, 0.0035, side - math.radians(45), side + math.radians(45))
    elif k == 1:   # car from the side
        ret(-0.024, 0.024, -0.004, 0.006)
        m.quad_(P(-0.014, 0.006), P(0.012, 0.006), P(0.006, 0.015), P(-0.010, 0.015), color, nrm=n)
        disc(-0.014, -0.006, 0.0062); disc(0.014, -0.006, 0.0062)
    else:   # ship (rocket pointing up)
        ret(-0.0055, 0.0055, -0.014, 0.010)
        m.tri(P(-0.0055, 0.010), P(0.0055, 0.010), P(0, 0.024), color, nrm=n)
        m.tri(P(-0.0055, -0.014), P(-0.0055, -0.002), P(-0.015, -0.017), color, nrm=n)
        m.tri(P(0.0055, -0.014), P(0.0055, -0.002), P(0.015, -0.017), color, nrm=n)
        m.quad_(P(-0.003, -0.019), P(0.003, -0.019), P(0.003, -0.015), P(-0.003, -0.015), LIGHT_COL, nrm=n)
    return m


# ------------------------------------------------------------------ wheels (Y axis, width 0.5)
def wheel(diam, offroad_):
    c, p = Mesh(), Mesh()
    R = diam / 2; W = 0.46
    # tire: lathe with grooves on the tread and chamfered shoulders
    rp = R - (0.06 if offroad_ else 0.0)
    profile = [(rp - 0.12, -W / 2), (rp - 0.035, -W / 2), (rp, -W / 2 + 0.05)]
    nb = 6; t0, t1 = -W / 2 + 0.05, W / 2 - 0.05
    for i in range(nb + 1):
        t = t0 + (t1 - t0) * i / nb
        if 0 < i < nb:
            profile += [(rp, t - 0.012), (rp - 0.02, t - 0.012), (rp - 0.02, t + 0.012), (rp, t + 0.012)]
    # 0.19.3 (Andre: "transparent line between the tire and the body, on both sides"): the tire's hole was open on the inside, and
    # between the edge of the dish/cap (y 0.19..0.215) and the tire's side (y 0.23) you could see the back of the tread. Closes the hole.
    profile += [(rp, t1), (rp - 0.035, W / 2), (rp - 0.12, W / 2), (rp - 0.12, -W / 2)]
    c.lathe(profile, 1, (0, 0, 0), TIRE, n=32, lids=False)
    if offroad_:   # studs around, alternating
        n = max(12, int(round(2 * math.pi * rp / 0.19)))
        for i in range(n):
            side = 1 if i % 2 == 0 else -1
            m = Mesh(); m.rotated_box(1, 2 * math.pi * i / n, (0.07, 0.085, 0.24), rp + 0.02, TIRE)
            _shift(c, m.tris, [0, side * 0.09, 0])
    # 0.12.14 (Andre: "the wheels should have a right side to fit"): only the INNER side (-Y, the one touching the bearing) glues.
    # Outer side (+Y): deep paintable rim (dish) with 8 spokes, 6 screws and a hubcap. Inner side (-Y): plain paintable
    # cap + dark mounting flange with 4 studs — you can tell from afar which side goes on the axle.
    ra = rp - 0.125
    # 0.19.3: the edge of the dish/cap (16 sides) goes 3 cm into the tire (32 sides) — before, between the chords of the two polygons, there were gaps
    plate = [(0.09, 0.14), (0.14, 0.14), (ra - 0.05, 0.20), (ra, 0.215), (ra + 0.03, 0.19)]
    p.lathe(list(reversed(plate)), 1, (0, 0, 0), PAINT, n=16, rot0=math.pi / 16, lids=False)   # from outside in (normal +Y)
    for i in range(8):
        m = Mesh(); m.rotated_box(1, math.pi / 8 + 2 * math.pi * i / 8, (ra - 0.16, 0.05, 0.03), 0.09 + (ra - 0.16) / 2, PAINT)
        _shift(p, m.tris, [0, 0.185, 0])
    for i in range(6):
        a = math.pi / 6 + i * math.pi / 3
        c.screw((0.065 * math.cos(a), 0.22, 0.065 * math.sin(a)), 1, 1, 0.012, 0.01, LIGHT)
    c.cylinder(1, (0, 0.232, 0), 0.05, 0.012, LIGHT, n=12, chamfer=0.004)   # hubcap
    inside = [(0.10, -0.205), (ra, -0.205), (ra + 0.03, -0.19)]
    p.lathe(inside, 1, (0, 0, 0), PAINT, n=16, rot0=math.pi / 16, lids=False)   # plain inner cap (normal -Y)
    c.cylinder(1, (0, -0.215, 0), 0.17, 0.02, DARK, n=16, rot0=math.pi / 16, chamfer=0.004)   # mounting flange
    for i in range(4):
        a = math.pi / 4 + i * math.pi / 2
        c.screw((0.13 * math.cos(a), -0.225, 0.13 * math.sin(a)), 1, -1, 0.014, 0.012, LIGHT)
    c.cylinder(1, (0, 0, 0), 0.10, 0.44, DARK, n=12, chamfer=0.012)
    c.cylinder(1, (0, 0, 0), 0.035, 0.46, LIGHT, n=8, chamfer=0.006)
    return c, p


# ------------------------------------------------------------------ small tanks (1x2x1, Y = up, front = +Z)
TANK_WHITE = (223, 223, 223, 108)   # body color of the game's tanks
GREEN_LINE, BLUE_LINE = (0, 121, 0, 0), (61, 138, 206, 0)


def tank(line):
    """16-sided capsule with domes (like the game's tanks) and two BANDS like the game's (measurements of the Oxygen Tank
    Paintable): at the back (-Z) it's a thin-walled square frame (0.05) with diagonal plates on the two back corners, and at the
    front (+Z) the iron follows the cylinder's geometry (half-moon r 0.445..0.5 joining the side walls at z 0).
    Bands at y 0.5..0.75 and -0.75..-0.5, it glues by them (back face). Octagonal pipe nozzle on the bottom band
    (cell x 0..0.25 / y -0.75..-0.5, it leaves the half-moon up to z 0.5), colored line (green/blue), dark seams on the
    domes. The gauge (with the data port inside) and the valve are the game's own: the prefab puts them at
    (0, -0.3125, 0.5) and (-0.12, -0.625, 0.466) (on the half-moon, on the nozzle's side)."""
    c, p = Mesh(), Mesh()
    R, y0, y1, hc = 0.44, -0.70, 0.70, 0.27
    bottom = [(R * math.cos(math.radians(a)), y0 - hc * math.sin(math.radians(a))) for a in range(90, -1, -15)]
    top = [(R * math.cos(math.radians(a)), y1 + hc * math.sin(math.radians(a))) for a in range(0, 91, 15)]
    c.lathe(bottom + top, 1, (0, 0, 0), TANK_WHITE, n=16, rot0=math.pi / 16, lids=False)
    for y in (y0, y1):
        c.ring(1, (0, y, 0), R - 0.002, R + 0.004, 0.012, DARK, n=16, rot0=math.pi / 16)
    c.ring(1, (0, -0.42, 0), R - 0.002, R + 0.006, 0.02, line, n=16, rot0=math.pi / 16)
    e = 0.05   # wall thickness
    for ya, yb in ((-0.75, -0.5), (0.5, 0.75)):
        yc = (ya + yb) / 2
        p.chamfered_box(-0.5, 0.5, ya, yb, -0.5, -0.5 + e, PAINT, vert=0.012)        # back wall
        p.box(-0.5, -0.5 + e, ya, yb, -0.5 + e, 0.0, PAINT)                            # sides, up to where the half-moon joins
        p.box(0.5 - e, 0.5, ya, yb, -0.5 + e, 0.0, PAINT)
        p.arc(1, (0, yc, 0), 0.5 - e - 0.005, 0.5, yb - ya, -math.pi / 2, math.pi / 2, PAINT, n=16)   # half-moon at the front
        for ang in (3 * math.pi / 4, 5 * math.pi / 4):   # diagonal plates only on the back corners
            m = Mesh(); m.rotated_box(1, ang, (0.02, 0.40, yb - ya - 0.002), 0.46, PAINT)
            _shift(p, m.tris, [0, yc, 0])
        for sx in (-1, 1):
            c.screw((sx * 0.475, yb, -0.475), 1, 1, 0.014, 0.01, LIGHT)
            c.screw((sx * 0.33, yb, math.sqrt(0.4725 ** 2 - 0.33 ** 2)), 1, 1, 0.014, 0.01, LIGHT)
    # octagonal pipe nozzle: leaves the half-moon of the bottom band up to the face (z 0.5), with the dark hole
    zb = 0.42
    c.lathe([(0.055, 0), (0.105, 0), (0.115, 0.01), (0.115, 0.5 - zb - 0.008), (0.107, 0.5 - zb), (0.055, 0.5 - zb)], 2, (0.125, -0.625, zb), BODY, n=8, rot0=math.pi / 8, lids=False)
    c.cylinder(2, (0.125, -0.625, 0.5 - 0.008), 0.055, 0.004, BLACK, n=8, rot0=math.pi / 8)
    return c, p


# ------------------------------------------------------------------ 0.11.2: steering wheel (a block of its own: small base at the back/bottom,
# column on the DIAGONAL, tilted rim perpendicular to the column) and pedals (a block of its own, 2 levers, only the stepping action)
WHEEL_BASE = (-0.125, 0.125, -0.25, -0.125, 0.0, 0.25)   # 0.19.2 (Andre): small 2x1x2 box (0.25 x 0.125 x 0.25) at the bottom/back corner
# 0.19.3 (Andre, drawing): the column leaves the CENTER OF THE TOP FACE of the small box, tilted toward the player (-Z), with the wheel at the tip,
# perpendicular to it; a single data output, on the back face (+Z), in the end cell. No lip.
WHEEL_COLUMN0 = (0, -0.125, 0.125)   # where the column enters the small box: center of the top face (black socket, like a port)
WHEEL_ELEV = 55.0                    # column elevation (degrees above horizontal, rising toward -Z)
WHEEL_COLUMN_L = 0.21                # column length up to the hub
WHEEL_HUB = (0.0, WHEEL_COLUMN0[1] + WHEEL_COLUMN_L * math.sin(math.radians(WHEEL_ELEV)), WHEEL_COLUMN0[2] - WHEEL_COLUMN_L * math.cos(math.radians(WHEEL_ELEV)))
WHEEL_TILT = math.degrees(math.atan2(WHEEL_HUB[1] - WHEEL_COLUMN0[1], WHEEL_COLUMN0[2] - WHEEL_HUB[2]))   # rim tilt (= column elevation)
PEDAL_PIVOT_X, PEDAL_PIVOT_Y, PEDAL_PIVOT_Z = 0.12, -0.20, -0.13   # hinges of the 2 pedals at (+-X, Y, Z)
PEDAL_REST = 38.0                 # pedal tilt at rest (degrees above horizontal)
PEDAL_TRAVEL = 24.0                   # step on = turns PEDAL_TRAVEL degrees down (0..1, a single action)
PEDAL_BASE = (-0.25, 0.25, -0.25, -0.12, 0.0, 0.25)   # the outputs' box (back half of the block)


def steering_wheel(ports=None):
    """0.19.3 (Andre, drawing): small paintable 2x1x2 box at the bottom/back corner; the dark column leaves the center of the top face through a
    black socket (looks like a port), tilted WHEEL_ELEV degrees toward the player (-Z); the rim (a game lever: turns in Z +-90, tilted
    WHEEL_TILT in X) sits at the tip, perpendicular to the column. A single data output, on the back face (+Z). The lever's mesh is
    centered on its own origin (XY plane, normal Z; the -Z side is the player's)."""
    c, p, lev = Mesh(), Mesh(), Mesh()
    x0, x1, y0, y1, z0, z1 = WHEEL_BASE
    rec_z = ports_on_face(c, ports, 2, 1, z1)
    p.chamfered_box(x0, x1, y0, y1, z0, z1, PAINT, top=0.008, base=0.006, vert=0.008, recesses={"+z": rec_z})
    for sx in (-1, 1):
        for sz in (-1, 1):
            c.screw((sx * 0.1, y1, (z0 + z1) / 2 + sz * 0.1), 1, 1, 0.007, 0.004, LIGHT)
    p0, p1 = np.array(WHEEL_COLUMN0), np.array(WHEEL_HUB)
    d = p1 - p0; d /= np.linalg.norm(d)
    # socket on the top face (black, like a port's plug): ring + dark bottom (the tilted column passes with room to spare)
    c.ring(1, (p0[0], y1 + 0.004, p0[2]), 0.042, 0.056, 0.008, BLACK, n=12, rot0=math.pi / 12)
    c.cylinder(1, (p0[0], y1 + 0.0035, p0[2]), 0.043, 0.005, DARK, n=12, rot0=math.pi / 12)
    c.cylinder_between(p0 - d * 0.02, p1 - d * 0.03, 0.03, DARK, n=12)     # column (starts inside the socket)
    c.cylinder_between(p1 - d * 0.085, p1 - d * 0.06, 0.038, MEDIUM, n=12)    # collar near the hub
    c.cylinder_between(p1 - d * 0.035, p1 - d * 0.005, 0.045, DARK, n=12)  # boss behind the rim
    lev.ring(2, (0, 0, 0), 0.142, 0.175, 0.034, TIRE, n=16, rot0=math.pi / 16, chamfer=0.007)
    for ang in (0.0, math.pi, 1.5 * math.pi):
        lev.rotated_box(2, ang, (0.125, 0.034, 0.02), 0.085, LIGHT)
    lev.cylinder(2, (0, 0, 0), 0.05, 0.05, LIGHT, n=12, chamfer=0.006)
    lev.cylinder(2, (0, 0, -0.026), 0.025, 0.004, BLACK, n=8, rot0=math.pi / 8)
    return c, p, [("Lever", lev, WHEEL_HUB, (1, 1, 1), (WHEEL_TILT, 0, 0))]   # tilted rim (perpendicular to the column)


def _rotate_x(p, degs):
    a = math.radians(degs); cs, sn = math.cos(a), math.sin(a)
    return np.array([p[0], p[1] * cs - p[2] * sn, p[1] * sn + p[2] * cs])


def pedal_capsule():
    """Click capsule of the pedal's lever (relative to the pivot, at rest): along the pad."""
    a = _rotate_x((0, 0.018, 0.05), -PEDAL_REST); b = _rotate_x((0, 0.018, 0.25), -PEDAL_REST)
    return tuple(float(v) for v in a), tuple(float(v) for v in b), 0.07


def pedal_click_box():
    """0.12.2 (pedal = a game button): click box along the pad, relative to the pivot, turned with it (offset, rot, scale)."""
    c = _rotate_x((0, 0.018, 0.145), -PEDAL_REST)
    return tuple(float(v) for v in c), (-PEDAL_REST, 0, 0), (0.17, 0.05, 0.27)


def pedal(ports=None):
    """0.12.1 (Andre): a single output (left pressed down = -1, right pressed down = +1) and a tilted base like the beacon — a wedge
    going down toward the front, so the pedal pressed down lies on top of it without the box showing. At the back, a paintable wall with the output (+Z).
    At the front, low rails + a crossbar with the hinges; floor pedals (pad at PEDAL_REST degrees, step on = +PEDAL_TRAVEL)."""
    c, p = Mesh(), Mesh()
    x0, x1 = -0.25, 0.25
    zp0, zp1 = 0.19, 0.25               # back wall (with the output)
    yb, ytf, ytt = -0.25, -0.225, -0.10  # floor, top of the wedge at the front (z 0) and at the back (z 0.19)
    rec_z = ports_on_face(c, ports, 2, 1, zp1)
    p.chamfered_box(x0, x1, yb, ytt + 0.005, zp0, zp1, PAINT, top=0.01, base=0.004, vert=0.004,
                      recesses={"+z": rec_z, "+y": [centered_recess(0, (zp0 + zp1) / 2, 0.40, 0.03, 0.004, PAINT, DARK)]})
    # wedge (paintable): prism with the tilted top, from z 0 to zp0
    # 0.19.3 (Andre: "on the pedal, between the diagonal part and the little box at the end" transparent): the wedge stopped 2 mm before the box and
    # had no back face — through the gap (and the box's chamfer) you saw the back. Now it goes 4 mm into the box and is closed.
    zw0, zw1 = 0.0, zp0 + 0.004
    b = [(x0 + 0.004, yb, zw0), (x1 - 0.004, yb, zw0), (x1 - 0.004, yb, zw1), (x0 + 0.004, yb, zw1)]
    t = [(x0 + 0.004, ytf, zw0), (x1 - 0.004, ytf, zw0), (x1 - 0.004, ytt, zw1), (x0 + 0.004, ytt, zw1)]
    ctr = (0, (yb + ytt) / 2, (zw0 + zw1) / 2)
    p.quad_(b[0], b[1], b[2], b[3], PAINT, center=ctr); p.quad_(t[0], t[1], t[2], t[3], PAINT, center=ctr)
    p.quad_(b[0], b[1], t[1], t[0], PAINT, center=ctr); p.quad_(b[1], b[2], t[2], t[1], PAINT, center=ctr); p.quad_(b[3], b[0], t[0], t[3], PAINT, center=ctr)
    p.quad_(b[2], b[3], t[3], t[2], PAINT, center=ctr)
    # dark anti-slip stripes on the tilted top (1 mm above)
    ang = math.atan2(ytt - ytf, zw1 - zw0)
    for k in range(4):
        u = 0.03 + k * 0.045
        zc, yc = zw0 + u * math.cos(ang) + 0.0, ytf + u * math.sin(ang) + 0.002
        strip_ = Mesh(); strip_.box(-0.20, 0.20, -0.004, 0.004, -0.008, 0.008, DARK)
        strip_.rotate(0, -math.degrees(ang)); _shift(c, strip_.tris, np.array([0, yc, zc + 0.10]))
    for sx in (-1, 1):
        c.screw((sx * 0.22, ytt + 0.005, 0.22), 1, 1, 0.011, 0.006, LIGHT)
    py, pz = PEDAL_PIVOT_Y, PEDAL_PIVOT_Z
    for sx in (-1, 1):   # rails: from the crossbar to the wedge (1 mm above the block's floor)
        c.chamfered_box(sx * 0.225 - 0.018, sx * 0.225 + 0.018, -0.249, -0.23, pz + 0.05, zw0 + 0.01, DARK, c=0.004)
    c.chamfered_box(-0.245, 0.245, -0.25, -0.215, pz - 0.05, pz + 0.05, DARK, c=0.004,
                      recesses={"+y": [centered_recess(0, pz, 0.06, 0.06, 0.003, BLACK, DARK)]})
    extras_ = []
    for sx, sfx in ((-1, "LeverL"), (1, "LeverR")):
        px = sx * PEDAL_PIVOT_X
        for side in (-1, 1):   # hinge lugs
            xo = px + side * 0.095
            c.chamfered_box(xo - 0.012, xo + 0.012, -0.215, py + 0.03, pz - 0.035, pz + 0.035, DARK, c=0.004)
        c.cylinder(0, (px, py, pz), 0.012, 0.23, LIGHT, n=8)   # pin
        lev = Mesh()
        lev.cylinder(0, (0, 0, 0), 0.022, 0.16, DARK, n=10)
        lev.chamfered_box(-0.03, 0.03, -0.012, 0.012, 0.0, 0.06, DARK, c=0.003)
        lev.chamfered_box(-0.08, 0.08, 0.008, 0.028, 0.02, 0.27, DARK, c=0.005,
                             recesses={"+y": [centered_recess(0, z, 0.13, 0.016, 0.006, BLACK, DARK) for z in (0.08, 0.12, 0.16, 0.20, 0.24)]})
        lev.rotate(0, -PEDAL_REST)
        extras_.append((sfx, lev, (px, py, pz), (1, 1, 1)))
    return c, p, extras_


# ------------------------------------------------------------------ turret seat (0.12.1, Andre: "like Scrap Mechanic's Turret Seat")
# Block 1.5 x 2.0 x 1.5 (3 x 4 x 3 quarters of 0.5 — aligned to the frames' grid). Square BASE that STAYS STILL (y -1..-0.5) with the output
# on the back side (-Z; the seat starts facing +Z). On top of it, the assembly that TURNS around the pivot (center of a quarter,
# TURRET_PIVOT): the game's seat, ring, back arc, armrests with the triggers and the 4 0.5 x 0.5 support plates (left,
# right, top, back) flush with the block's faces at rest. The assembly is drawn around the pivot (extra renderers placed at the
# pivot); the plugin turns the drawing and whatever is glued to the plates becomes a physics piece held at the pivot. Nothing that turns goes beyond
# TURRET_RADIUS from the pivot, so it never hits the base.
TURRET_BOUNDS = (2.5, 2.5, 1.5)     # 0.12.6 (Andre): 5 x 5 x 3 frame quarters (0.5) — the center falls in the middle of the middle quarter
TURRET_PIVOT = (0.0, 0.0, 0.0)       # block center = center of the middle quarter of each face
TURRET_EXTRA = (0.0, 0.01, 0.0)     # where the turning renderers sit (off the origin; mesh shifted back to the pivot)
TURRET_MARK = (0.0137, 0.0271, 0.0419)   # invisible renderer that identifies the turret (plugin: JointSystem.IsTurret)
TURRET_RADIUS = 1.125                      # free sphere around the pivot (top of the base = pivot - 1.125)
TURRET_R, TURRET_TUBE = 1.045, 0.055     # round ring (0.12.4: 0.66/0.035 — everything x1.58)
TURRET_ARC_Z = 0.62                     # semi-axis of the back arc in depth (the block stays at 1.5)
TURRET_HUB = 0.15                        # radius of the hubs that join the ring to the plates
TURRET_PLATE = 0.04                      # support plate thickness
TURRET_PLATE_SIDE, TURRET_PLATE_TOP = 0.25, 0.25   # half sides: ALL the plates = 1 frame quarter (0.5 x 0.5)
TURRET_BASE_Y = (-1.25, -1.125)          # still base, 1 cell thick (the same)
TURRET_BASE_SIDE = 1.5                   # base plate 1.5 x 1.5 (the same)
# 0.25.0 (Andre: "three knobs ... so I can set its maximum angle in each dimension ... front part of the base"): on the +Z face
# of the base, to the right of the 4 pass-through ports, in a 1.8 cm recess (the knob doesn't go past the face, nor the collision hull):
# yaw (0..180 degrees, 180 = free) / pitch up (0..135) / pitch down (0..135). The plugin finds the three by their X position.
TURRET_KNOB_XS = (0.575, 0.455, 0.335)   # facing the +Z face, +X is on the left: reading left to right = yaw, up, down
TURRET_KNOB_Y = -1.1875
TURRET_KNOB_DEPTH = 0.018
TURRET_KNOB_SUF = ("Knob", "KnobUp", "KnobDown")
TURRET_KNOB_DEFAULT = (1.0, 1.0, round(30.0 / 135.0, 4))   # = what the turret did before: free yaw, 135 up, 30 down
TURRET_PITCH_MAX = 135.0                 # look up until the top plate is ~5 cm from the base (worked out in 0.12.6)
# the tick of the game's seat (SCTick_Seat) puts the Upper at y -0.775 (height 0) ignoring the prefab's _position; the plugin
# (JointSystem.SeatHeights) rewrites the Upper's LocalTransform = TURRET_UPPER before the TransformSystemGroup (garage and simulation).
TURRET_UPPER = (0.0, -0.85, 0.02)    # 0.12.9: the adjustment Andre made in the game (banco_ajuste_v2 = 0;-0.35;0.02) went into the model
TURRET_PEDESTAL = (0.0, 0.0213, 0.0)  # 0.12.9: extra "Pedestal" (plate + rod): the plugin moves it together with the seat adjustment
TURRET_SLEEVE = 0.14                   # fixed sleeve at the bottom of the rim where the pedestal's rod slides (height adjustment without a hole)
TURRET_SEAT = (0.0, TURRET_UPPER[1] + 0.775, TURRET_UPPER[2])   # offset applied to the whole game SC_Seat (its Upper sits at y -0.775)
TURRET_TRIGGER = (0.36, -0.135, 0.247)   # pivot of the triggers (+-X, at the tip of the armrests), they turn in X 0..TURRET_TRIGGER_TRAVEL
TURRET_ARM = (0.30, 0.42, -0.27, -0.15, -0.23, 0.30)   # right armrest (x0,x1,y0,y1,z0,z1); left one mirrored
TURRET_TRIGGER_TRAVEL = 20.0
# 0.22.0 (Andre: "the turret seat goes in too, add the ports to its base, one set on each side matching that side's
# plate"): 4 sets of 4 pass-through ports without a kind on the sides of the BASE (y -1.1875, the base's cell), in the plates' order:
# 0-3 -X (left plate), 4-7 +X (right), 8-11 -Z (back), 12-15 +Z (= the top plate, which has no side of its own). (0.12.2: no output, no triggers)
TURRET_PORTS = ([((-0.6875, -1.1875, z), 1, 5) for z in (-0.1875, -0.0625, 0.0625, 0.1875)] + [((0.6875, -1.1875, z), 0, 5) for z in (-0.1875, -0.0625, 0.0625, 0.1875)]
                 + [((x, -1.1875, -0.6875), 5, 5) for x in (-0.1875, -0.0625, 0.0625, 0.1875)] + [((x, -1.1875, 0.6875), 4, 5) for x in (-0.1875, -0.0625, 0.0625, 0.1875)])   # 0.22.2: kind 5 (colorless plug, like the Frame Quarter With Ports)   # 6.25 cm into the base's face (0.75)


def _mark():
    """0.12.5: tiny mesh (it keeps normal scale, but it's a 1 mm triangle hidden inside the base)."""
    m = Mesh(); m.tri(np.array([0, 0, 0.0]), np.array([0.001, 0, 0]), np.array([0, 0, 0.001]), DARK); return m


def _torus(m, axis_v, center, R, r, color, n=36, k=10, ang0=0.0, ang1=2 * math.pi, lids=False, R2=None):
    """Arc tube (partial torus): circle of radius R around the axis (0/1/2) going through center, tube of radius r.
    Angle in the plane perpendicular to the axis (axis 2: 0 = +X, pi/2 = +Y; axis 0: 0 = +Y, pi/2 = +Z). R2: ellipse (semi-axis in the 2nd direction)."""
    c = np.array(center, float); a1, a2 = [(1, 2), (2, 0), (0, 1)][axis_v]
    e = np.zeros(3); e[axis_v] = 1
    Rb = R if R2 is None else R2

    def rad(u):
        q = np.zeros(3); q[a1] = Rb * math.cos(u); q[a2] = R * math.sin(u); q /= np.linalg.norm(q); return q

    def ctr(u):
        q = np.zeros(3); q[a1] = R * math.cos(u); q[a2] = Rb * math.sin(u); return q

    def P(u, v): return c + ctr(u) + rad(u) * (r * math.cos(v)) + e * (r * math.sin(v))
    for i in range(n):
        u0, u1 = ang0 + (ang1 - ang0) * i / n, ang0 + (ang1 - ang0) * (i + 1) / n
        for j in range(k):
            v0, v1 = 2 * math.pi * j / k, 2 * math.pi * (j + 1) / k
            nrm = rad((u0 + u1) / 2) * math.cos((v0 + v1) / 2) + e * math.sin((v0 + v1) / 2)
            m.quad_(P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1), color, nrm=nrm)
    if lids and (ang1 - ang0) < 2 * math.pi - 1e-6:
        for ang, s in ((ang0, -1), (ang1, 1)):
            tg = np.zeros(3); tg[a1] = -math.sin(ang) * s; tg[a2] = math.cos(ang) * s
            cc = c + ctr(ang)
            for j in range(k):
                v0, v1 = 2 * math.pi * j / k, 2 * math.pi * (j + 1) / k
                m.tri(cc, P(ang, v0), P(ang, v1), color, nrm=tg)


def turret(ports=None):
    """(still base: body, paintable; extras = [Moving (dark), MovingPaint (plates)]; preview = the game's seat)."""
    c, p = Mesh(), Mesh()
    hx, hz = TURRET_BOUNDS[0] / 2, TURRET_BOUNDS[2] / 2
    hb = TURRET_BASE_SIDE / 2
    yb0, yb1 = TURRET_BASE_Y
    # ---- still base (paintable), 1 cell thick, 1.5 x 1.5 in the middle of the bottom
    # 0.19.2 (Andre: "the turret seat is ugly — keep the plates, just make it prettier"): base with a big chamfer and a framed
    # recess; turntable in 2 rings (dark + light), a crown of screws, corners with fixing plates
    # 0.22.0 (Andre): pass-through ports (slip ring) on the 4 sides of the BASE, one set per plate (-X left, +X right, -Z back,
    # +Z = top plate). The base is 12.5 cm: so the pocket (11.2 cm) fits, the base's top/bottom chamfers got smaller (0.035 -> 0.006)
    p.chamfered_box(-hb, hb, yb0, yb1, -hb, hb, PAINT, top=0.006, base=0.004, vert=0.05,
                      recesses={"+y": [centered_recess(0, 0, 1.34, 1.34, 0.006, PAINT, DARK, [centered_recess(0, 0, 1.24, 1.24, 0.004, MEDIUM, DARK)])],
                                "-x": ports_on_face(c, ports, 0, -1, -hb), "+x": ports_on_face(c, ports, 0, 1, hb),
                                "-z": ports_on_face(c, ports, 2, -1, -hb),
                                "+z": ports_on_face(c, ports, 2, 1, hb) + [centered_recess(sum(TURRET_KNOB_XS) / 3, TURRET_KNOB_Y, max(TURRET_KNOB_XS) - min(TURRET_KNOB_XS) + 0.1, 0.09, TURRET_KNOB_DEPTH, MEDIUM, DARK)]})
    c.ring(1, (0, yb1 - 0.010 + 0.014, 0), 0.36, 0.66, 0.028, DARK, n=48, chamfer=0.006)   # turntable (still): dark ring
    c.ring(1, (0, yb1 - 0.010 + 0.034, 0), 0.42, 0.58, 0.012, LIGHT, n=48, chamfer=0.004)    # light band on top
    for k in range(16):
        a = 2 * math.pi * (k + 0.5) / 16
        c.screw((0.63 * math.cos(a), yb1 - 0.010 + 0.028, 0.63 * math.sin(a)), 1, 1, 0.011, 0.006, LIGHT)
    for sx in (-1, 1):
        for sz in (-1, 1):
            # 0.19.3: the plates were at 0.70 (they went past the base's chamfer and floated); now at 0.66, buried below the recess
            c.chamfered_box(sx * 0.66 - 0.05, sx * 0.66 + 0.05, yb1 - 0.011, yb1 + 0.010, sz * 0.66 - 0.05, sz * 0.66 + 0.05, MEDIUM, c=0.004)   # fixing plates on the corners
            c.screw((sx * 0.66, yb1 + 0.010, sz * 0.66), 1, 1, 0.014, 0.007, LIGHT)
    # ---- what turns: drawn around the pivot (origin = pivot). 0.12.6: block 2.5 x 2.5 x 1.5 (5 x 5 x 3 frame quarters of 0.5);
    # plates = the middle quarter of each face (0.5 x 0.5), bigger round rim, elliptic back arc (the depth stays 1.5)
    mv0, mp0 = Mesh(), Mesh()
    R, rt = TURRET_R, TURRET_TUBE
    _torus(mv0, 2, (0, 0, 0), R, rt, DARK, n=72, k=14)                                                   # ring (X-Y plane), smoother
    # (0.19.3: the "trim" of 0.19.2 was a torus entirely INSIDE the tube — invisible — removed)
    _torus(mv0, 0, (0, 0, 0), R, rt, DARK, n=36, k=14, ang0=-math.pi, ang1=0.0, R2=TURRET_ARC_Z)        # back arc (Y-Z, z < 0)
    pl = TURRET_PLATE; lim = hx; limy = TURRET_BOUNDS[1] / 2 - TURRET_PIVOT[1]; limz = hz; rh = TURRET_HUB
    def hub(axis_v, c0, c1):
        """joining hub: thinner dark cylinder + chamfered flange where it touches the tube + flange where it touches the plate"""
        # 0.19.3 (z-fighting): the middle cylinder went end to end and its caps coincided with the flanges'; now it only
        # joins one flange to the other. The screws were inside the plate (invisible): now on the flange's face turned toward the tube.
        d = 1 if c1 > c0 else -1
        f_in = [0.0, 0.0, 0.0]; f_in[axis_v] = c0 + d * 0.03
        mv0.cylinder(axis_v, tuple(f_in), rh * 1.12, 0.06, DARK, n=20, chamfer=0.015)        # flange on the tube
        f_out = [0.0, 0.0, 0.0]; f_out[axis_v] = c1 - d * 0.025
        mv0.cylinder(axis_v, tuple(f_out), rh * 1.05, 0.05, DARK, n=20, chamfer=0.012)       # flange on the plate
        mid = [0.0, 0.0, 0.0]; mid[axis_v] = (c0 + d * 0.05 + c1 - d * 0.04) / 2; length = abs(c1 - c0) - 0.09
        mv0.cylinder(axis_v, tuple(mid), rh * 0.8, length, MEDIUM, n=20, chamfer=0.012)
        for k in range(6):   # screws on the face of the plate's flange (tube side)
            a = 2 * math.pi * k / 6
            pp = [0.0, 0.0, 0.0]; a1, a2 = [(1, 2), (2, 0), (0, 1)][axis_v]
            pp[axis_v] = f_out[axis_v] - d * 0.025; pp[a1] = rh * 0.92 * math.cos(a); pp[a2] = rh * 0.92 * math.sin(a)
            mv0.screw(tuple(pp), axis_v, -d, 0.011, 0.006, LIGHT)
    for sx in (-1, 1):   # hubs: from the tube to the plate
        hub(0, sx * (R - rt - 0.01), sx * (lim - pl + 0.001))
    hub(1, R - rt - 0.01, limy - pl + 0.001)
    hub(2, -(TURRET_ARC_Z - rt - 0.01), -(limz - pl + 0.001))
    a, b = TURRET_PLATE_SIDE, TURRET_PLATE_TOP   # half sides (a = the bigger one, 0.5; b = 0.25)
    for (x0, x1, y0, y1, z0, z1, axis_v, sn, w, h) in ((-lim, -lim + pl, -a, a, -b, b, 0, 1, 0.4, 0.4), (lim - pl, lim, -a, a, -b, b, 0, -1, 0.4, 0.4),
                                                     (-a, a, limy - pl, limy, -b, b, 1, -1, 0.4, 0.4), (-a, a, -a, a, -limz, -limz + pl, 2, 1, 0.4, 0.4)):
        key_ = ("+" if sn > 0 else "-") + "xyz"[axis_v]
        cu, cv = [(y0 + y1) / 2, (x0 + x1) / 2, (x0 + x1) / 2][axis_v], [(z0 + z1) / 2, (z0 + z1) / 2, (y0 + y1) / 2][axis_v]
        mp0.chamfered_box(x0, x1, y0, y1, z0, z1, PAINT, c=0.012, recesses={key_: [centered_recess(cu, cv, w, h, 0.006, PAINT, DARK, [centered_recess(cu, cv, w - 0.08, h - 0.08, 0.004, MEDIUM, DARK)])]})
        for su in (-1, 1):   # screws on the 4 corners of the plate (outer side)
            for sv in (-1, 1):
                pp = [0.0, 0.0, 0.0]; a1, a2 = [(1, 2), (2, 0), (0, 1)][axis_v]
                pp[axis_v] = (x1, y1, z1)[axis_v] if sn > 0 else (x0, y0, z0)[axis_v]   # inner face (the outer one touches the neighboring block and stays plain)
                pp[a1] = cu + su * (w / 2 + 0.03); pp[a2] = cv + sv * (h / 2 + 0.03)
                mv0.screw(tuple(pp), axis_v, sn, 0.012, 0.005, LIGHT)
    # seat pedestal + plate. 0.12.9 (Andre: "this little foot stays inside the model"): the plate sat at the model's height and the
    # seat went down with the adjustment -> the plate went through the seat. Now plate + rod are a separate extra (Pedestal) that the plugin
    # moves together with the seat; the rod slides inside a fixed sleeve at the bottom of the rim (goes up/down ~15 cm without opening a hole).
    yu = TURRET_UPPER[1] - TURRET_PIVOT[1]; zu = TURRET_UPPER[2] - TURRET_PIVOT[2]
    mv0.cylinder(1, (0, -R + TURRET_SLEEVE / 2, zu), 0.085, TURRET_SLEEVE, DARK, n=20, chamfer=0.01)    # sleeve (turns with the rim)
    mv0.cylinder(1, (0, -R + TURRET_SLEEVE - 0.009, zu), 0.098, 0.024, MEDIUM, n=20, chamfer=0.006)      # collar on top of the sleeve (0.19.3: 3 mm above — its top coincided with the sleeve's = z-fighting)
    pd0 = Mesh()
    pd0.cylinder_between((0, -R - 0.02, zu), (0, yu + 0.02, zu), 0.062, DARK, n=16)                    # rod
    pd0.cylinder(1, (0, yu - 0.05, zu), 0.08, 0.03, MEDIUM, n=16, chamfer=0.006)                        # collar under the plate
    pd0.chamfered_box(-0.20, 0.20, yu - 0.02, yu + 0.015, zu - 0.22, zu + 0.12, MEDIUM, c=0.012,
                        recesses={"+y": [centered_recess(0, zu - 0.05, 0.30, 0.24, 0.004, MEDIUM, DARK)]})   # plate under the seat
    # the extras sit at TURRET_EXTRA (1 cm above the pivot): the plugin doesn't turn children at the block's origin, and the pivot is now the origin
    d = np.array(TURRET_PIVOT, float) - np.array(TURRET_EXTRA, float)
    mv, mp = Mesh(), Mesh(); _shift(mv, mv0.tris, d); _shift(mp, mp0.tris, d)
    pd = Mesh(); _shift(pd, pd0.tris, np.array(TURRET_PIVOT, float) - np.array(TURRET_PEDESTAL, float))
    extras_ = [("Moving", mv, TURRET_EXTRA, (1, 1, 1)), ("MovingPaint", mp, TURRET_EXTRA, (1, 1, 1)),
              ("Pedestal", pd, TURRET_PEDESTAL, (1, 1, 1)),
              ("Mark", _mark(), TURRET_MARK, (1, 1, 1))]   # 0.12.5: invisible mark = the plugin recognizes the turret even in the garage
    for kx, sfx in zip(TURRET_KNOB_XS, TURRET_KNOB_SUF):   # 0.25.0: the 3 limit knobs (yaw / pitch up / pitch down)
        extras_.append(knob_panel(c, (kx, TURRET_KNOB_Y, hb - TURRET_KNOB_DEPTH), 1, sfx=sfx))
    # preview: the game's seat meshes (picture/icon only)
    preview = None
    if GAME_MESH_DIR:
        preview = Mesh()
        try:
            import export_glb
            for fpath, pos, color, rot in (("Seat Upper.asset", TURRET_UPPER, BODY, (0, 0, 0)), ("Seat Upper Paintable.asset", TURRET_UPPER, (205, 205, 205, 20), (0, 0, 0)),
                                       ):
                path_ = os.path.join(GAME_MESH_DIR, fpath)
                if not os.path.exists(path_): continue
                vpos, vnrm, vcor, idx = export_glb.read_mesh(path_)
                P0 = np.array(pos, float)
                for k in range(0, len(idx), 3):
                    a, b, cc = [P0 + rot_unity(np.array(vpos[idx[k + i]]), rot) for i in range(3)]
                    preview.tris.append((a, b, cc, color))
        except Exception as ex:
            print("preview of the game's seat:", ex)
    return c, p, extras_, preview


TRANSM_ANTENNA = (0.044, 0.081)    # (x, z) of the base of the game's right antenna; the detail goes to the back/left top corner
TRANSM_DETAIL = (-0.088, 0.088)  # (x, z) of the detail (globe / gear): free corner of the lid (the screen goes up to x -0.073, z 0.054)
TRANSM_TOP = -0.01               # top of the base of the game's transmitter
TRANSM_HOLE = (0.03, 0.0815, 0.0225)   # 0.22.4: hexagonal holes in the lid of the game's paintable base (x +-, z, vertex radius)
GRAY_T, LIGHT_T = (83, 83, 83, 0), (208, 208, 208, 0)   # colors of the antennas of the game's transmitter
def transmitter_body(detail):
    """0.22.1/0.22.2 (Andre: "both will have only the right antenna, and on the opposite side ... a single detail"; "remove that black rectangle,
    keep just the little antenna and the little ball, and keep them apart"): the transmitter's RENDERER (the game's antennas come stuck in a single mesh)
    becomes a mesh of our own: the right antenna like the game's and, at the back/left corner of the lid, the detail ("globe" or "gear").
    0.22.4 (Andre: "it was left cut there and you can see what's behind the hole"): the game's paintable base has 2 hexagonal HOLES in the lid
    (x +-0.03, z 0.0815, radius 0.0225) where the antennas' bases go in — both bases are back (the left one closed, without an antenna).
    The paintable base, the screen, the label and the LED are still the game's (prefab)."""
    c = Mesh(); y1 = TRANSM_TOP
    for bx in (-TRANSM_HOLE[0], TRANSM_HOLE[0]):   # hexagonal bases (they close the holes; vertex toward +-Z like the hole)
        c.cylinder(1, (bx, y1 - 0.002 + 0.006, TRANSM_HOLE[1]), TRANSM_HOLE[2], 0.012, GRAY_T, n=6, rot0=math.pi / 6, chamfer=0.003)
    c.cylinder(1, (-TRANSM_HOLE[0], y1 + 0.0105, TRANSM_HOLE[1]), 0.008, 0.002, BLACK, n=6, rot0=math.pi / 6)   # blind hole in place of the antenna that left
    ax, az = TRANSM_HOLE[0], TRANSM_HOLE[1]
    p0 = (ax, y1 + 0.01, az); p1 = (ax + 0.022, 0.098, az + 0.01)
    c.cylinder_between(p0, p1, 0.0045, LIGHT_T, n=8)
    c.sphere(p1, 0.009, LIGHT_T, n=8, m=4)
    dx, dz = TRANSM_DETAIL
    c.cylinder(1, (dx, y1 + 0.005, dz), 0.012, 0.01, GRAY_T, n=8, rot0=math.pi / 8, chamfer=0.002)   # octagonal base (like the antenna's)
    if detail == "globe":
        r = 0.019; cy = y1 + 0.01 + 0.03 + r
        c.cylinder(1, (dx, y1 + 0.01 + 0.015, dz), 0.0045, 0.03, LIGHT_T, n=8)                     # rod
        c.merge(globe((dx, cy, dz), r))
    else:
        r = 0.026; cy = y1 + 0.01 + 0.006 + r
        c.cylinder(1, (dx, y1 + 0.01 + 0.003, dz), 0.008, 0.006, LIGHT_T, n=8)                     # axle pin
        c.merge(gear((dx, cy, dz), r))
    return c


def gear(center, r=0.026, teeth=10, thick=0.01):
    """0.22.2 (Andre: "this little thing has to become a pretty gear"): gear with trapezoidal teeth (a real outline),
    upright (axis in Z, facing the player), with a hole and a hub. Flat faces + outer wall + hole wall."""
    m = Mesh(); x, cy, z = center
    rr = r * 0.78; rf = r * 0.28   # root of the teeth, hole
    pts = []
    for i in range(teeth):
        a0 = 2 * math.pi * i / teeth; da = 2 * math.pi / teeth
        for f, rad in ((0.0, rr), (0.18, r), (0.5, r), (0.68, rr)):
            pts.append((a0 + f * da, rad))
    def P(a, rad, zz): return (x + rad * math.cos(a), cy + rad * math.sin(a), zz)
    z0, z1 = z - thick / 2, z + thick / 2
    n = len(pts)
    for k in range(n):
        (a, ra), (b, rb) = pts[k], pts[(k + 1) % n]
        for zz, sg in ((z1, 1), (z0, -1)):   # faces (ring between the hole and the outline)
            m.quad_(P(a, rf, zz), P(a, ra, zz), P(b, rb, zz), P(b, rf, zz), GRAY_T, nrm=(0, 0, sg))
        m.quad_(P(a, ra, z0), P(b, rb, z0), P(b, rb, z1), P(a, ra, z1), GRAY_T, center=(x, cy, z))      # outer wall
        m.quad_(P(a, rf, z0), P(b, rf, z0), P(b, rf, z1), P(a, rf, z1), GRAY_T, center=(x, cy, z + 1))  # hole wall (normal inward)
    m.ring(2, (x, cy, z), rf * 0.45, rf, thick + 0.004, LIGHT_T, n=8)   # light hub
    return m


def globe(center, r=0.02):
    """0.22.1 (Andre: "a little internet globe icon to say that it's global"): light sphere with a dark equator and 2 meridians."""
    m = Mesh(); x, cy, z = center
    m.sphere((x, cy, z), r, LIGHT_T, n=16, m=8)
    for axis_v in (0, 1, 2):
        m.ring(axis_v, (x, cy, z), r - 0.0015, r + 0.002, 0.003, GRAY_T, n=16)   # equator (Y) and 2 meridians (X, Z)
    return m



def transmitter():
    """0.16.0: Shaft Transmitter = copy of the game's SC_WirelessTransmitter. 0.22.2: the antennas' renderer becomes a mesh of our own (transmitter_body: right antenna + gear);
    paintable base, screen, label and LED stay the game's (the icon's preview shows the game's)."""
    return transmitter_body("gear"), Mesh(), [], screen_number(game_preview(["Wireless Transmitter Body Paintable.asset", "Wireless Transmitter Indicator.asset",
                                                                       "Wireless Transmitter Actionable.asset", "Wireless Transmitter Label.asset"],
                                                                      1.0, {"Wireless Transmitter Body Paintable.asset": (70, 72, 78, 0), "Wireless Transmitter Label.asset": (70, 72, 78, 0)}))



# ------------------------------------------------------------------ 0.18.0: Single Cell Battery (2x4x2 cells = 0.25 x 0.5 x 0.25)
# Andre: "a battery that takes only 2x4x2, like the game's small battery but taking just one of its cells". A model of our own in the style
# of the game's small battery (colors taken from its meshes): dark base, yellow body, dark band, paintable lid with 1 window.
# The charge indicator is the game's bar (Small Rechargeable Battery Indicator, extra renderer) on the -Z face's panel.
BAT_YELLOW, BAT_DARK1, BAT_DARK2, BAT_LID = (210, 117, 3, 108), (31, 31, 31, 0), (44, 44, 44, 0), (119, 119, 119, 20)
CELL_INDICATOR = (-0.057, 0.0, -0.1085)   # position of the bar (grows in +x from here; the game scales x by the charge)
def cell(ports=None):
    c, p = Mesh(), Mesh()
    hx, hz, b = 0.125, 0.125, 0.112
    c.chamfered_box(-hx, hx, -0.25, -0.215, -hz, hz, BAT_DARK2, c=0.006)                     # base
    rec_z = ports_on_face(c, ports, 2, -1, -b) + [centered_recess(0.0, 0.0, 0.13, 0.05, 0.004, BAT_DARK1, BAT_DARK2)]
    c.chamfered_box(-b, b, -0.215, 0.09, -b, b, BAT_YELLOW, vert=0.006, recesses={"-z": rec_z,
                      "+x": [centered_recess(-0.06, z, 0.006, 0.17, 0.003, BAT_DARK1, BAT_DARK1) for z in (-0.04, 0.04)],
                      "-x": [centered_recess(-0.06, z, 0.006, 0.17, 0.003, BAT_DARK1, BAT_DARK1) for z in (-0.04, 0.04)]})
    for sx in (-1, 1):
        for sz in (-1, 1):
            e = b + 0.002   # corner bracket 2 mm out of the body (nothing coplanar)
            x0, x1 = (b - 0.012, e) if sx > 0 else (-e, -b + 0.012); z0, z1 = (b - 0.012, e) if sz > 0 else (-e, -b + 0.012)
            c.box(x0, x1, -0.215, 0.09, z0, z1, BAT_DARK2)
    c.chamfered_box(-0.118, 0.118, 0.09, 0.106, -0.118, 0.118, BAT_DARK1, c=0.003)            # dark band
    p.chamfered_box(-0.12, 0.12, 0.106, 0.25, -0.12, 0.12, BAT_LID, top=0.02, base=0.004, vert=0.01,
                      recesses={"+y": [centered_recess(0.0, 0.0, 0.11, 0.11, 0.004, BAT_YELLOW, BAT_DARK2)]})
    screw_hole(c, (0.085, 0.25, 0.085), 1, 1, 0.009)
    return c, p


def game_preview(files, scale=1.0, paint_it=None):
    """0.18.0: preview (icon only) from the game's meshes (GAME_MESH_DIR), with a scale; paint_it = {file: color} for the paintable parts."""
    preview = Mesh()
    if not GAME_MESH_DIR: return preview
    try:
        import export_glb
        for fpath in files:
            path_ = os.path.join(GAME_MESH_DIR, fpath)
            if not os.path.exists(path_): print("preview: missing", fpath); continue
            vpos, vnrm, vcor, idx = export_glb.read_mesh(path_)
            color = (paint_it or {}).get(fpath)
            for k in range(0, len(idx), 3):
                a, b, cc = [np.array(vpos[idx[k + i]], float) * np.array(scale if isinstance(scale, tuple) else (scale,) * 3, float) for i in range(3)]
                preview.tris.append((a, b, cc, color or (tuple(vcor[idx[k]]) if vcor else (120, 120, 120, 0))))
    except Exception as ex:
        print("preview:", ex)
    return preview


def screen_number(preview, text="0", color=(25, 25, 25, 0)):
    """0.19.0 (Andre: "both transmitters have a buggy model in the inventory — without a number on the little display"): the icon is drawn here,
    without the game's 3D text. Draws the channel number (7 segments) lying on the screen, where the game writes it (RendererText3D: x 0.0496,
    z -0.0441, right/bottom aligned, y just above the screen)."""
    seg = {"0": "abcdef", "1": "bc", "2": "abged", "3": "abgcd", "4": "fgbc", "5": "afgcd", "6": "afgedc", "7": "abc", "8": "abcdefg", "9": "abcdfg"}
    h, w, t, y = 0.058, 0.032, 0.0085, -0.0150
    x1 = 0.0496
    for ci, ch in enumerate(reversed(text)):
        xd = x1 - ci * (w + 0.008); xe = xd - w; z0 = -0.0441; z1 = z0 + h; zm = (z0 + z1) / 2
        bars = {"a": (xe, xd, z1 - t, z1), "d": (xe, xd, z0, z0 + t), "g": (xe, xd, zm - t / 2, zm + t / 2),
                  "f": (xe, xe + t, zm, z1), "e": (xe, xe + t, z0, zm), "b": (xd - t, xd, zm, z1), "c": (xd - t, xd, z0, zm)}
        for sgm in seg.get(ch, ""):
            xa, xb, za, zb = bars[sgm]
            p00, p10, p11, p01 = [np.array(v, float) for v in ((xa, y, za), (xb, y, za), (xb, y, zb), (xa, y, zb))]
            for tri in ((p00, p11, p10), (p00, p01, p11), (p00, p10, p11), (p00, p11, p01)):   # both sides
                preview.tris.append(tri + (color,))
    return preview


def mini_thruster():
    """0.18.0: Mini Electric Thruster = copy of the game's SC_SmallElectricThruster at scale 0.5 (the prefab uses the game's meshes)."""
    return Mesh(), Mesh(), [], game_preview(["Small Electric Thruster Body.asset", "Small Electric Thruster Body Paintable.asset", "Small Electric Thruster Ignition.asset"],
                                             0.5, {"Small Electric Thruster Body Paintable.asset": (150, 145, 135, 0)})




def radio_():
    """0.18.0: Long Range Transmitter. 0.22.2: the antennas' renderer becomes a mesh of our own (transmitter_body: right antenna + globe);
    paintable base, screen, label and LED stay the game's (the icon's preview shows the game's)."""
    return transmitter_body("globe"), Mesh(), [], screen_number(game_preview(["Wireless Transmitter Body Paintable.asset", "Wireless Transmitter Indicator.asset",
                                                                       "Wireless Transmitter Actionable.asset", "Wireless Transmitter Label.asset"],
                                                                      1.0, {"Wireless Transmitter Body Paintable.asset": (60, 62, 68, 0), "Wireless Transmitter Label.asset": (60, 62, 68, 0)}))


def turret_capsule():
    """Click capsule of the trigger (relative to its pivot, at rest): along the handle."""
    return (0.0, 0.02, 0.012), (0.0, 0.10, 0.06), 0.04


# ------------------------------------------------------------------ table
# (electric box, fuel box). 0.10.2 (form9): fuel Small = 4x8x4 cells (0.5 x 1 x 0.5), electric Small = 0.5 cube
MOTORS = {"Small": ((0.5, 0.5, 0.5), (0.5, 1.0, 0.5)), "Medium": ((1.0, 1.0, 1.0), (1.0, 1.0, 1.0)), "Large": ((1.5, 1.5, 1.5), (1.5, 1.5, 1.5))}
WHEELS = {"SC_WheelSportsSmall": (1.0, False), "SC_WheelSportsMedium": (1.5, False), "SC_WheelSportsLarge": (2.0, False),
         "SC_WheelOffroadSmall": (1.0, True), "SC_WheelOffroadMedium": (1.5, True), "SC_WheelOffroadLarge": (2.0, True)}


def parts(mod):
    """(body, paintable, extras) of an item of models(); extras = [(suffix, mesh, pos, scale)] (separate renderers)."""
    return (mod[0], mod[1], mod[2] if len(mod) > 2 else [])


def rot_unity(p, rot):
    """Applies Unity's Euler rotation (x, y, z degrees; order Z, then X, then Y — R = Ry.Rx.Rz) to a point."""
    x, y, z = rot
    q = np.array(p, float)
    for axis_v, g in ((2, z), (0, x), (1, y)):
        if not g: continue
        a = math.radians(g); cs, sn = math.cos(a), math.sin(a); a1, a2 = [(1, 2), (2, 0), (0, 1)][axis_v]
        r = q.copy(); r[a1] = q[a1] * cs - q[a2] * sn; r[a2] = q[a1] * sn + q[a2] * cs; q = r
    return q


def with_extras(mod):
    """Body with the extra renderers applied at their rest position/rotation/scale (for the preview, checking and .glb).
    extras = [(suffix, mesh, pos, scale[, Unity Euler rot])]."""
    body, paint_, extras_ = parts(mod)
    if not extras_: return body
    c = Mesh(); c.tris = list(body.tris)
    for ex in extras_:
        sfx, m, pos, scl = ex[:4]; rot = ex[4] if len(ex) > 4 else (0, 0, 0)
        for a, b, cc, color in m.tris:
            c.tris.append(tuple(rot_unity(np.array(v, float) * np.array(scl, float), rot) + np.array(pos, float) for v in (a, b, cc)) + (color,))
    return c



# ------------------------------------------------------------------ 0.15.0 Drive Shaft (Andre: "a cable of its own for the motor")
def axis_icon():
    """Only for the inventory picture: a piece of steel shaft (the real cable uses the meshes of the game's plasma cable, painted iron)."""
    c, p = Mesh(), Mesh()
    c.cylinder(2, (0, 0, 0), 0.030, 0.25, METAL, n=12)
    for z in (-0.09, 0.0, 0.09):
        c.cylinder(2, (0, 0, z), 0.042, 0.012, LIGHT, n=12, chamfer=0.003)
    c.cylinder(2, (0, 0, -0.118), 0.05, 0.014, MEDIUM, n=6)
    c.cylinder(2, (0, 0, 0.118), 0.05, 0.014, MEDIUM, n=6)
    return c, p


def differential(ports=None):
    """Differential 0.25 (2x2x2 cells): 4 shaft inputs on the -X face (0.17.0; 1 before), 4 outputs on the +X face. Paintable housing with a round gear
    cover on top (teeth around it) and screws."""
    c, p = Mesh(), Mesh()
    h = 0.125
    rec_mx = ports_on_face(c, ports, 0, -1, -h); rec_px = ports_on_face(c, ports, 0, 1, h)
    p.chamfered_box(-h, h, -h, h, -h, h, PAINT, top=0.012, base=0.006, vert=0.01, recesses={"-x": rec_mx, "+x": rec_px})
    # crown (gear) on top: dark disc + 16 teeth + light hub
    c.cylinder(1, (0, h + 0.0003, 0), 0.085, 0.0006, DARK, n=16, rot0=math.pi / 16)
    for i in range(16):
        a = 2 * math.pi * i / 16
        t = Mesh(); t.rotated_box(1, a, (0.016, 0.013, 0.0024), 0.083, MEDIUM); _shift(c, t.tris, np.array([0.0, h + 0.0009, 0.0]))
    c.cylinder(1, (0, h + 0.0006, 0), 0.034, 0.0012, LIGHT, n=8, rot0=math.pi / 8)
    for sx in (-1, 1):
        for sz in (-1, 1):
            screw_hole(c, (sx * 0.1, h, sz * 0.1), 1, 1, 0.008)
    return c, p


def reverser(ports=None):
    """Shaft Reverser 0.25 x 0.125 x 0.25 (2x1x2 cells, like the math blocks): input on the -Z face, output on the +Z. On top,
    two curved arrows in opposite directions (the turn comes out reversed)."""
    c, p = Mesh(), Mesh()
    hx, hy, hz = 0.125, 0.0625, 0.125
    rec_mz = ports_on_face(c, ports, 2, -1, -hz); rec_pz = ports_on_face(c, ports, 2, 1, hz)
    p.chamfered_box(-hx, hx, -hy, hy, -hz, hz, PAINT, top=0.008, base=0.004, vert=0.008, recesses={"-z": rec_mz, "+z": rec_pz})
    y = hy + 0.0004; wid = 0.010
    def arrow(cx, clockwise):
        r = 0.045; a0, a1 = (math.radians(200), math.radians(-60)) if clockwise else (math.radians(-20), math.radians(240))
        steps = 10
        def P(a, rr): return (cx + rr * math.cos(a), y, rr * math.sin(a))
        for i in range(steps):
            u0 = a0 + (a1 - a0) * i / steps; u1 = a0 + (a1 - a0) * (i + 1) / steps
            c.quad_(P(u0, r - wid / 2), P(u1, r - wid / 2), P(u1, r + wid / 2), P(u0, r + wid / 2), LIGHT, center=(cx, y - 1, 0))
        d = math.copysign(math.radians(22), a1 - a0)
        c.tri(P(a1, r - wid * 1.8), P(a1, r + wid * 1.8), P(a1 + d, r), LIGHT, center=(cx, y - 1, 0))
    arrow(-0.058, False); arrow(0.058, True)
    return c, p

SOLAR_PORT_X = -0.1875   # power port of the mini solar panel (gen_prefabs.mini_solar): x -0.1875, z +-0.1875, facing +-Z
SOLAR_BLUE, SOLAR_GRID = (25, 21, 70, 220), (32, 32, 32, 54)   # colors of the game's solar panel cells / grid
def mini_solar(ports=None):
    """0.22.5 (Andre: "it's still ugly, it became a pile of hacks in the model - and it's black instead of following the model's color"): the mini solar
    panel stops being the game's stretched mesh (x 0.5 / z 0.25, which deformed the corner bracket) and becomes a model of our own, drawn for the
    0.5 x 0.125 x 0.5 size: paintable tray with a rim, 4 one-cell corner brackets on the corners (the two on -X carry the power ports,
    with a pocket + frame like the other blocks), and 6 x 6 blue cells with a dark grid (the game's panel colors)."""
    c, p = Mesh(), Mesh()
    h = 0.25; yb, yt = -0.0625, -0.02   # tray; the corner brackets (only where there's a port) go up to the top of the block
    # 0.22.6 (Andre: "z-fighting ... and the little bit on the other two corners that have no port - remove it"): only the 2 corner brackets of the ports (-X);
    # the tray sits 2 mm inside the block's faces (the corner brackets and the rims stay flush with the faces: nothing coplanar with the tray)
    e = 0.002; b = 0.02; cz = 0.03   # tray inset, rim width, corner bracket bottom
    p.chamfered_box(-h + e, h - e, yb + 0.001, yt, -h + e, h - e, PAINT, top=0.0, base=0.004, vert=0.004)   # bottom 1 mm above the corner brackets'
    p.chamfered_box(-h, -h + b, yt, 0.0, -h + cz, h - cz, PAINT, c=0.003)            # -X rim (between the corner brackets)
    p.chamfered_box(h - b, h, yt, 0.0, -h, h, PAINT, c=0.003)                        # +X rim (whole)
    for sz in (-1, 1):                                                                 # +-Z rims (from the corner bracket to the +X rim)
        p.chamfered_box(-h + 0.125, h - b, yt, 0.0, sz * h - (b if sz > 0 else 0), sz * h + (0 if sz > 0 else b), PAINT, c=0.003)
    for sz in (-1, 1):   # the ports' corner brackets: one cell (0.125) in X, 3 cm in Z, from the bottom to the top of the block
        z0, z1 = (-h, -h + cz) if sz < 0 else (h - cz, h)
        face_ = "+z" if sz > 0 else "-z"
        rec = ports_on_face(p, [pp for pp in (ports or []) if (pp[0][2] < 0) == (sz < 0)], 2, sz, sz * h)
        p.chamfered_box(-h, -h + 0.125, yb, 0.0625, z0, z1, PAINT, c=0.003, recesses={face_: rec} if rec else None)
    # cells: 6 x 6 inside the rim, on the tray (dark grid underneath, blue cells 1 mm above)
    i0, i1 = -h + b + 0.004, h - b - 0.004; z0, z1 = -h + b + 0.004, h - b - 0.004
    c.box(i0, i1, yt, yt + 0.002, z0, z1, SOLAR_GRID)
    n = 6; dx, dz = (i1 - i0) / n, (z1 - z0) / n; g = 0.004
    for a in range(n):
        for k in range(n):
            c.box(i0 + a * dx + g, i0 + (a + 1) * dx - g, yt + 0.002, yt + 0.003, z0 + k * dz + g, z0 + (k + 1) * dz - g, SOLAR_BLUE)
    return c, p, []


def models(ports_per_block):
    """ports_per_block: name -> [(pos, direction, kind)] (comes from gen_prefabs)."""
    P = lambda n: ports_per_block.get(n, [])
    r = {}
    r["SC_Piston"] = piston(3, P("SC_Piston"), guide=True); r["SC_MiniPiston"] = piston(2, P("SC_MiniPiston"), s=0.5, guide=True)   # 0.20.0: single piston (up to 8 m) + mini (up to 4 m)
    r["SC_SlipRing"] = slip_ring(P("SC_SlipRing"), 0.5); r["SC_MiniSlipRing"] = slip_ring(P("SC_MiniSlipRing"), 0.25)   # 0.22.4: 4x1x4 / 2x1x2 plates
    r["SC_PistonEnd"] = piston_end(0.5); r["SC_MiniPistonEnd"] = piston_end(0.25)   # 0.22.4: Piston End as a plate (the cube SC_PistaoPonta stays for old BPs)
    r["SC_Bearing"] = bearing(P("SC_Bearing")); r["SC_GyroBearing"] = bearing(P("SC_GyroBearing"), spin=True)   # 0.19.14
    r["SC_MiniBearing"] = bearing(P("SC_MiniBearing"), s=0.5); r["SC_MiniGyroBearing"] = bearing(P("SC_MiniGyroBearing"), spin=True, s=0.5)   # 0.20.0
    for sfx, (be, bc) in MOTORS.items():
        r["SC_Motor" + sfx] = motor(be, False, P("SC_Motor" + sfx)); r["SC_MotorFuel" + sfx] = motor(bc, True, P("SC_MotorFuel" + sfx))
    r["SC_Recoupler"] = recoupler(P("SC_Recoupler")); r["SC_Beacon"] = beacon(P("SC_Beacon"))
    r["SC_TankSmallGreen"] = tank(GREEN_LINE); r["SC_TankSmallBlue"] = tank(BLUE_LINE)
    r["SC_SteeringWheel"] = steering_wheel(P("SC_SteeringWheel")); r["SC_Pedal"] = pedal(P("SC_Pedal"))
    r["SC_TurretSeat"] = turret(P("SC_TurretSeat"))
    r["SC_DriveShaft"] = axis_icon(); r["SC_Differential"] = differential(P("SC_Differential")); r["SC_ShaftReverser"] = reverser(P("SC_ShaftReverser"))
    r["SC_ShaftTransmitter"] = transmitter()   # 0.16.0
    r["SC_SingleCellBattery"] = cell(P("SC_SingleCellBattery")); r["SC_MiniElectricThruster"] = mini_thruster(); r["SC_LongRangeTransmitter"] = radio_()   # 0.18.0
    r["SC_SolarPanelMini"] = mini_solar([((SOLAR_PORT_X, 0.0, -0.1875), 5, 2), ((SOLAR_PORT_X, 0.0, 0.1875), 4, 2)])   # 0.19.0; 0.22.5: a model of our own
    for name, (d, off) in WHEELS.items(): r[name] = wheel(d, off)
    return r
