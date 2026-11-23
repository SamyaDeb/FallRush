"""
G4.9 Fall Rush prop kit: ~20 original capsule-toy props, generated headless in Blender.

    blender -b --factory-startup -P tools/blender/props.py -- --out client-unity/Assets/_Game/Art/Props
    blender -b --factory-startup -P tools/blender/props.py -- --glb web/site/public/models   (landing page .glb kit)

Everything is built here from maths (lathes, rounded boxes, tubes, extruded outlines): no imported mesh, texture or
reference file. Motif: a capsule-toy machine (capsule seams, coin slots, prize stars, wind-up keys).

Output per prop: <name>.fbx (no materials, no textures) and one props.json for the kit:
  * geometry in metres, Unity axes (x right, y up, z forward); the pivot is the collider origin it replaces (see
    "pivot" in props.json) and the size matches the collider box it renders ("box": centre + size, Unity axes);
  * per corner, UV0 = (palette slot, soft role) and UV1 = (shade, 0): shade = baked ambient occlusion x a bottom-dark
    gradient. Unity's importer step (Editor/PropKit.cs) resolves the slot to the palette tokens of BOTH looks (G4.8-1):
    COLOR = the soft albedo x shade, uv3 = (candy albedo x shade, 2 + 8 x role). The vertex COLOR written here is a
    preview of the soft colours only (for looking at the file in Blender).
  * <= 1500 triangles each (asserted here and by an EditMode test).
Deterministic: fixed sample directions for the AO, no random numbers, the FBX time stamp pinned and the FBX ids hashed
with SHA-1 instead of Python's per-run hash(), so two runs give byte-identical files (checked with shasum).
"""

import hashlib
import json
import math
import os
import sys

import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

# ------------------------------------------------------------------ determinism: pin the FBX writer's clock and ids

import datetime as _dt
from io_scene_fbx import export_fbx_bin, fbx_utils


class _FixedDatetime(_dt.datetime):
    @classmethod
    def now(cls, tz=None):
        return cls(2026, 11, 21, 0, 0, 0)


class _DtModule:
    datetime = _FixedDatetime


export_fbx_bin.datetime = _DtModule


def _stable_key_to_uuid(uuids, key):
    if isinstance(key, int) and 0 <= key < 2 ** 63:
        uuid = key
    else:
        uuid = int.from_bytes(hashlib.sha1(repr(key).encode("utf-8")).digest()[:8], "little") >> 1
    if uuid > int(1e9):
        t = uuid % int(1e9)
        if t not in uuids:
            uuid = t
    while uuid in uuids:
        uuid += 1
    return fbx_utils.UUID(uuid)


fbx_utils._key_to_uuid = _stable_key_to_uuid

# ------------------------------------------------------------------ palette slots (resolved in Unity: PropKit.cs)

# name -> preview sRGB (soft look, approximate; Unity uses the PaletteTokens values, both looks).
SLOTS = [
    ("hazardMagenta", 0xFF2D8A), ("hazardYellow", 0xFFD43B), ("ink", 0x2A1250), ("white", 0xFFFFFF),
    ("mint", 0x2FE0A8), ("grape", 0x7B2FF7), ("magenta", 0xFF3EA5), ("sunYellow", 0xFFD21F),
    ("goo", 0x9B3CFF), ("amber", 0xFFA51F), ("walkTop", 0xF2F0FF), ("walkSide", 0xD9DDF7),
    ("wallSide", 0xCBC0F5), ("sky", 0x3FA9F5), ("rimSodaBlue", 0x5FB8F0), ("rimTangerine", 0xFFA45C),
    ("rimBubblegum", 0xFF8CC0), ("rimSourApple", 0x8EDB6A), ("rimGrapeFizz", 0xB08CF0), ("rimLemonDrop", 0xFFD860),
    ("cream", 0xFFF3E0),
]
SLOT = {n: i for i, (n, _) in enumerate(SLOTS)}

# Soft-look roles (G4.8-1): 2 side, 3 rim, 4 hazard (glossy, view rim), 5 prop.
SIDE, RIM, HAZ, PROP = 2, 3, 4, 5

TRI_CAP = 1500

# ------------------------------------------------------------------ geometry (Unity axes, metres)


class Part:
    def __init__(self, verts, faces, slot, role, smooth=True):
        self.v = [Vector(p) for p in verts]
        self.f = [tuple(f) for f in faces]
        self.slot = SLOT[slot]
        self.role = role
        self.smooth = smooth

    def tris(self):
        return sum(len(f) - 2 for f in self.f)

    def moved(self, m):
        """Copy transformed by a 4x4 (mathutils.Matrix)."""
        p = Part([], self.f, SLOTS[self.slot][0], self.role, self.smooth)
        p.v = [m @ v for v in self.v]
        return p


def mat_trs(t=(0, 0, 0), r=None, s=(1, 1, 1)):
    from mathutils import Matrix, Quaternion
    m = Matrix.Translation(Vector(t))
    if r is not None:
        axis, deg = r
        m = m @ Matrix.Rotation(math.radians(deg), 4, Vector(axis))
    sc = Matrix.Diagonal((s[0], s[1], s[2], 1.0))
    return m @ sc


def lathe(profile, segs, slot, role, axis="y", cap_top=True, cap_bottom=True, phase=0.0, radial=None):
    """Revolve [(r, h)] (bottom to top) about the axis. radial(theta, h) -> radius factor (quilting, bulges)."""
    verts, faces = [], []
    rings = len(profile)
    for (r, h) in profile:
        for s in range(segs):
            a = phase + 2 * math.pi * s / segs
            k = radial(a, h) if radial else 1.0
            x, z = math.cos(a) * r * k, math.sin(a) * r * k
            verts.append(_ax(x, h, z, axis))
    for i in range(rings - 1):
        for s in range(segs):
            a, b = i * segs + s, i * segs + (s + 1) % segs
            c, d = a + segs, b + segs
            if profile[i][0] < 1e-6:
                faces.append((a, d, c))
            elif profile[i + 1][0] < 1e-6:
                faces.append((a, b, c))
            else:
                faces.append((a, b, d, c))
    if cap_bottom and profile[0][0] > 1e-6:
        verts.append(_ax(0, profile[0][1], 0, axis))
        ci = len(verts) - 1
        for s in range(segs):
            faces.append((ci, (s + 1) % segs, s))
    if cap_top and profile[-1][0] > 1e-6:
        verts.append(_ax(0, profile[-1][1], 0, axis))
        ci = len(verts) - 1
        base = (rings - 1) * segs
        for s in range(segs):
            faces.append((ci, base + s, base + (s + 1) % segs))
    return _dedupe_poles(Part(verts, faces, slot, role))


def _dedupe_poles(p):
    # Collapse the zero-radius rings into one vertex (so poles have no degenerate quads).
    seen, remap, out = {}, {}, []
    for i, v in enumerate(p.v):
        key = (round(v.x, 6), round(v.y, 6), round(v.z, 6))
        if key in seen:
            remap[i] = seen[key]
        else:
            seen[key] = len(out)
            remap[i] = len(out)
            out.append(v)
    faces = []
    for f in p.f:
        g = []
        for i in f:
            j = remap[i]
            if j not in g:
                g.append(j)
        if len(g) >= 3:
            faces.append(tuple(g))
    p.v, p.f = out, faces
    return p


def _ax(x, h, z, axis):
    if axis == "y":
        return (x, h, z)
    if axis == "z":
        return (x, z, h)
    return (h, x, z)  # x


def round_cyl_profile(radius, length, fillet, n_fillet=3, y0=None):
    """A cylinder along the lathe axis from y0 to y0 + length with rounded ends (fillet radius)."""
    y0 = -length / 2 if y0 is None else y0
    pr = [(0.0, y0)]
    for i in range(n_fillet + 1):
        a = math.pi / 2 * (1 - i / n_fillet)
        pr.append((radius - fillet + fillet * math.cos(a), y0 + fillet - fillet * math.sin(a)))
    for i in range(n_fillet + 1):
        a = math.pi / 2 * i / n_fillet
        pr.append((radius - fillet + fillet * math.cos(a), y0 + length - fillet + fillet * math.sin(a)))
    pr.append((0.0, y0 + length))
    return pr


def sphere(radius, segs, rings, slot, role, squash=1.0):
    pr = []
    for i in range(rings + 1):
        a = -math.pi / 2 + math.pi * i / rings
        pr.append((radius * math.cos(a) if 0 < i < rings else 0.0, radius * math.sin(a) * squash))
    return lathe(pr, segs, slot, role, cap_top=False, cap_bottom=False)


def band(radius, y, width, segs, slot, role, axis="y", bulge=0.0):
    """A short ring around the lathe axis (a stripe band / a seam) with rounded edges."""
    pr = [(radius - 0.004, y - width / 2), (radius + bulge * 0.6, y - width / 4), (radius + bulge, y),
          (radius + bulge * 0.6, y + width / 4), (radius - 0.004, y + width / 2)]
    return lathe(pr, segs, slot, role, axis=axis, cap_top=False, cap_bottom=False)


def round_box(size, r, n, slot, role, centre=(0, 0, 0), inflate=0.0):
    """Sphere-swept box of outer size `size` (corner radius r, n segments per corner arc); inflate bulges the faces.
    Built as a subdivided cube surface q (half size s + r) mapped to inner + r * normalize(q - inner)."""
    half = [size[i] / 2 for i in range(3)]
    r = min(r, *half)
    s = [half[i] - r for i in range(3)]
    ticks = []
    for i in range(3):
        side = [-s[i] - r * math.cos(math.pi / 2 * k / n) for k in range(n)]  # the corner arc (k = 0: the edge)
        m = max(1, int(round(2 * s[i] / 0.8)))
        mid = [-s[i] + 2 * s[i] * k / m for k in range(m + 1)] if s[i] > 1e-6 else [0.0]
        ticks.append(side + mid + [-x for x in reversed(side)])
    verts, faces, index = [], [], {}

    def vid(q):
        key = tuple(round(c, 6) for c in q)
        if key in index:
            return index[key]
        inner = Vector([max(-s[i], min(s[i], q[i])) for i in range(3)])
        d = Vector(q) - inner
        p = inner + (d.normalized() * r if d.length > 1e-9 else d)
        if inflate:
            m = max(range(3), key=lambda a: abs(d[a]))
            w = 1.0
            for a in range(3):
                if a != m and s[a] > 1e-6:
                    w *= max(0.0, 1 - (inner[a] / s[a]) ** 2)
            p[m] += math.copysign(inflate * w, q[m])
        index[key] = len(verts)
        verts.append(p + Vector(centre))
        return index[key]

    for axis in range(3):
        a1, a2 = [x for x in range(3) if x != axis]
        for sign in (-1, 1):
            t1, t2 = ticks[a1], ticks[a2]
            for i in range(len(t1) - 1):
                for j in range(len(t2) - 1):
                    quad = []
                    for (di, dj) in ((0, 0), (1, 0), (1, 1), (0, 1)):
                        q = [0.0, 0.0, 0.0]
                        q[axis] = sign * (s[axis] + r)
                        q[a1] = t1[i + di]
                        q[a2] = t2[j + dj]
                        quad.append(vid(q))
                    # outward: e(a1) x e(a2) points along +axis for axes 0 and 2, along -axis for axis 1
                    flip = (sign < 0) != (axis == 1)
                    faces.append(tuple(reversed(quad)) if flip else tuple(quad))
    return _dedupe_poles(Part(verts, faces, slot, role))


def box(size, slot, role, centre=(0, 0, 0)):
    """A plain box (flat tiles, decals)."""
    hx, hy, hz = size[0] / 2, size[1] / 2, size[2] / 2
    c = Vector(centre)
    v = [c + Vector((x * hx, y * hy, z * hz)) for x in (-1, 1) for y in (-1, 1) for z in (-1, 1)]
    f = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    return Part(v, f, slot, role, smooth=False)


def tube(points, radius, sides, slot, role, closed=False):
    """A tube along a polyline (Unity axes)."""
    pts = [Vector(p) for p in points]
    verts, faces = [], []
    n = len(pts)
    prev_side = None
    for i, p in enumerate(pts):
        if closed:
            t = (pts[(i + 1) % n] - pts[i - 1]).normalized()
        else:
            t = (pts[min(i + 1, n - 1)] - pts[max(i - 1, 0)]).normalized()
        ref = Vector((0, 1, 0)) if abs(t.y) < 0.9 else Vector((1, 0, 0))
        side = t.cross(ref).normalized() if prev_side is None else (prev_side - t * prev_side.dot(t)).normalized()
        prev_side = side
        up = side.cross(t).normalized()
        for s in range(sides):
            a = 2 * math.pi * s / sides
            verts.append(p + (side * math.cos(a) + up * math.sin(a)) * radius)
    segs = n if closed else n - 1
    for i in range(segs):
        for s in range(sides):
            a, b = i * sides + s, i * sides + (s + 1) % sides
            c, d = ((i + 1) % n) * sides + s, ((i + 1) % n) * sides + (s + 1) % sides
            faces.append((a, b, d, c))
    if not closed:
        for end, rng in ((0, range(sides)), (n - 1, range(sides))):
            verts.append(pts[end])
            ci = len(verts) - 1
            for s in rng:
                a, b = end * sides + s, end * sides + (s + 1) % sides
                faces.append((ci, b, a) if end == 0 else (ci, a, b))
    return Part(verts, faces, slot, role)


def extrude(outline, depth, slot, role, bevel=0.0):
    """Extrude a 2D outline (x, y) along z (centred). Optional bevel shrinks the faces."""
    verts, faces = [], []
    n = len(outline)
    zs = [-depth / 2, -depth / 2 + bevel, depth / 2 - bevel, depth / 2] if bevel > 0 else [-depth / 2, depth / 2]
    cx = sum(p[0] for p in outline) / n
    cy = sum(p[1] for p in outline) / n
    for zi, z in enumerate(zs):
        shrink = bevel if (bevel > 0 and zi in (0, 3)) else 0.0
        for (x, y) in outline:
            d = Vector((x - cx, y - cy))
            l = d.length
            k = (l - shrink) / l if l > 1e-6 else 1.0
            verts.append((cx + d.x * k, cy + d.y * k, z))
    layers = len(zs)
    for li in range(layers - 1):
        for i in range(n):
            a, b = li * n + i, li * n + (i + 1) % n
            faces.append((a, b, b + n, a + n))
    # caps: a fan from the centroid (the outlines are star-shaped about it)
    for li, rev in ((0, True), (layers - 1, False)):
        verts.append((cx, cy, zs[li]))
        ci = len(verts) - 1
        for i in range(n):
            a, b = li * n + i, li * n + (i + 1) % n
            faces.append((ci, b, a) if rev else (ci, a, b))
    return Part(verts, faces, slot, role, smooth=False)


def star_outline(r0, r1, points, phase=math.pi / 2):
    out = []
    for i in range(points * 2):
        a = phase + math.pi * i / points
        r = r0 if i % 2 == 0 else r1
        out.append((math.cos(a) * r, math.sin(a) * r))
    return out


def tri_flag(a, b, c, thick, slot, role):
    """A thin triangular slab (both sides visible)."""
    a, b, c = Vector(a), Vector(b), Vector(c)
    n = (b - a).cross(c - a).normalized() * (thick / 2)
    v = [a + n, b + n, c + n, a - n, b - n, c - n]
    f = [(0, 1, 2), (5, 4, 3), (0, 3, 4, 1), (1, 4, 5, 2), (2, 5, 3, 0)]
    return Part(v, f, slot, role, smooth=False)


# ------------------------------------------------------------------ props

PROPS = {}


def prop(name, pivot, box, note):
    def deco(fn):
        PROPS[name] = dict(fn=fn, pivot=pivot, box=box, note=note)
        return fn
    return deco


@prop("mallet_head", "head centre (HazardMallet head node)", ((0, 0, 0), (0.94, 0.94, 1.5)),
      "foam mallet head: HeadRadius 0.45 m, HeadHalfWidth 0.75 m (long along z for swingAxis 0; Unity turns it for 2)")
def mallet_head():
    parts = [lathe(round_cyl_profile(0.45, 1.5, 0.14, 3), 20, "hazardMagenta", HAZ, axis="z")]
    for z in (-0.5, 0.0, 0.5):  # hit stripe: three lemon bands
        parts.append(band(0.45, z, 0.16, 20, "hazardYellow", HAZ, axis="z", bulge=0.02))
    for z in (-0.25, 0.25):  # capsule seams between the bands
        parts.append(band(0.452, z, 0.03, 20, "ink", HAZ, axis="z", bulge=0.006))
    for z in (-0.75, 0.75):  # round foam caps (button) on the ends
        cap = sphere(0.16, 12, 6, "white", HAZ, squash=0.35)
        parts.append(cap.moved(mat_trs((0, 0, z - math.copysign(0.035, z)), ((1, 0, 0), 90))))
    return parts


@prop("pillow_bumper", "base centre (scaled x/z by the radius, y by the height)", ((0, 0.5, 0), (2.0, 1.0, 2.0)),
      "quilted cushion bumper; unit radius 1 m, height 1 m (prism collider r x h)")
def pillow_bumper():
    def quilt(a, h):
        return 1.0 + 0.018 * math.cos(6 * a) * math.cos(3 * math.pi * h) - 0.012
    pr = [(0.0, 0.0), (0.9, 0.0), (0.985, 0.05)]
    for i in range(1, 8):
        h = 0.05 + 0.8 * i / 8
        pr.append((0.985 + 0.012 * math.sin(math.pi * h), h))
    pr += [(0.96, 0.9), (0.86, 0.97), (0.6, 1.0), (0.0, 1.0)]
    parts = [lathe(pr, 24, "hazardMagenta", HAZ, radial=quilt)]
    for y in (0.35, 0.75):
        parts.append(band(0.99, y, 0.12, 24, "hazardYellow", HAZ, bulge=0.012))
    for k in range(6):  # quilting buttons
        a = 2 * math.pi * (k + 0.5) / 6
        b = sphere(0.045, 8, 4, "white", HAZ)
        parts.append(b.moved(mat_trs((math.cos(a) * 0.985, 0.55, math.sin(a) * 0.985))))
    return parts


@prop("padded_beam", "beam centre", ((0, 0, 0), (0.4, 0.4, 3.6)), "mallet frame beam with round caps (no collider)")
def padded_beam():
    parts = [round_box((0.4, 0.4, 3.3), 0.12, 2, "grape", SIDE)]
    for z in (-1.65, 1.65):
        cap = lathe(round_cyl_profile(0.2, 0.3, 0.1, 3), 12, "grape", SIDE, axis="z")
        parts.append(cap.moved(mat_trs((0, 0, z - math.copysign(0.0, z)))))
    for z in (-0.9, 0.0, 0.9):
        parts.append(band(0.205, z, 0.06, 12, "white", SIDE, axis="z", bulge=0.01).moved(mat_trs((0, 0, 0))))
    return parts


@prop("gumball", "ball centre (scaled by the radius)", ((0, 0, 0), (2.04, 2.04, 2.04)), "gumball with a lemon band and a shine band; unit radius 1 m")
def gumball():
    parts = [sphere(1.0, 20, 12, "hazardMagenta", HAZ)]
    parts.append(band(1.0, 0.0, 0.28, 20, "hazardYellow", HAZ, bulge=0.02))
    shine = band(1.0, 0.0, 0.07, 20, "white", HAZ, bulge=0.012)
    parts.append(shine.moved(mat_trs((0, 0, 0), ((1, 0, 1), 55))))
    return parts


@prop("spinner_cap", "bar end centre", ((0, 0, 0), (0.4, 0.4, 0.4)), "foam cap on a spinner bar end (bar radius 0.15 m)")
def spinner_cap():
    parts = [sphere(0.2, 14, 8, "hazardMagenta", HAZ)]
    parts.append(band(0.2, 0.0, 0.06, 14, "hazardYellow", HAZ, bulge=0.01).moved(mat_trs((0, 0, 0), ((1, 0, 0), 90))))
    return parts


@prop("spinner_hub", "pivot at bar height", ((0, 0, 0), (0.9, 0.5, 0.9)), "spinner hub on the post")
def spinner_hub():
    parts = [lathe(round_cyl_profile(0.45, 0.5, 0.12, 3), 20, "grape", HAZ)]
    parts.append(band(0.455, 0.0, 0.08, 20, "hazardYellow", HAZ, bulge=0.01))
    bolt = lathe(round_cyl_profile(0.14, 0.12, 0.05, 2, y0=0.25), 10, "white", HAZ)
    parts.append(bolt)
    return parts


@prop("start_arch", "arch centre at deck level (dressing)", ((0, 3.25, 0), (24.0, 11.8, 1.6)),
      "inflatable start arch over Capsule Run's 20 m start deck: posts at x +-11.2 (outside the deck), inner top 7.3 m up")
def start_arch():
    parts = []
    half, rise, base, r, foot = 11.2, 2.4, 5.6, 0.7, -2.6
    pts = []
    for i in range(17):
        a = math.pi * i / 16
        pts.append((-math.cos(a) * half, base + math.sin(a) * rise, 0.0))
    path = [(-half, foot + 0.4, 0.0)] + pts + [(half, foot + 0.4, 0.0)]
    parts.append(tube(path, r, 9, "mint", PROP))
    for i, p in enumerate(pts[1:-1:2]):  # inflatable seam rings
        q = Vector(p)
        ring = band(r + 0.02, 0.0, 0.12, 9, "white", PROP, bulge=0.02)
        t = Vector(pts[min(16, 2 * i + 2)]) - Vector(pts[max(0, 2 * i)])
        ang = math.degrees(math.atan2(t.y, t.x))
        parts.append(ring.moved(mat_trs(q, ((0, 0, 1), ang - 90))))
    for s in (-1, 1):  # weighted feet (in the air beside the deck, below its level)
        parts.append(round_box((1.6, 0.8, 1.6), 0.25, 1, "grape", PROP, centre=(s * half, foot, 0)))
    return parts


@prop("arch_post", "post centre (finish arch post box)", ((0, 0, 0), (1.4, 8.0, 1.4)), "finish arch post: inflatable column in its 1.4 x 8 x 1.4 collider box")
def arch_post():
    parts = [round_box((1.4, 8.0, 1.4), 0.32, 3, "mint", SIDE, inflate=0.0)]
    for y in (-2.4, 0.0, 2.4):  # inflatable seams
        parts.append(round_box((1.43, 0.12, 1.43), 0.06, 1, "white", SIDE, centre=(0, y, 0)))
    return parts


@prop("arch_seg", "segment centre (finish arch top box)", ((0, 0, 0), (1.9, 1.4, 1.4)), "finish arch top segment in its 1.9 x 1.4 x 1.4 collider box")
def arch_seg():
    parts = [round_box((1.9, 1.4, 1.4), 0.34, 3, "mint", SIDE)]
    parts.append(round_box((0.1, 1.43, 1.43), 0.05, 1, "white", SIDE, centre=(0.0, 0, 0)))
    return parts


@prop("capsule_machine", "cabinet base centre (dressing)", ((0, 1.0, 0), (2.0, 2.0, 2.2)), "capsule-toy machine cabinet: coin slot, crank, chute, chrome ring (globe = glass, MapDressing)")
def capsule_machine():
    parts = [round_box((1.8, 1.8, 1.8), 0.22, 2, "magenta", PROP, centre=(0, 0.9, 0))]
    parts.append(lathe(round_cyl_profile(1.0, 0.14, 0.06, 2, y0=1.8), 24, "sunYellow", PROP))
    parts.append(round_box((0.7, 0.5, 0.14), 0.06, 1, "ink", PROP, centre=(0, 0.45, -0.9)))  # chute
    parts += _coin_slot_parts((0, 1.25, -0.91))
    crank = lathe(round_cyl_profile(0.22, 0.2, 0.06, 2, y0=0.0), 16, "sunYellow", PROP, axis="z")
    parts.append(crank.moved(mat_trs((0, 0.85, -1.1))))
    parts.append(tube([(0, 0.85, -1.08), (0.3, 1.0, -1.1)], 0.05, 6, "grape", PROP))
    parts.append(sphere(0.12, 10, 6, "mint", PROP).moved(mat_trs((0.32, 1.02, -1.1))))
    return parts


def _coin_slot_parts(at):
    at = Vector(at)
    p = [round_box((0.5, 0.36, 0.08), 0.05, 1, "white", PROP, centre=at)]
    p.append(round_box((0.08, 0.24, 0.04), 0.02, 1, "ink", PROP, centre=at + Vector((0, 0, -0.04))))
    return p


@prop("coin_slot", "panel centre (dressing)", ((0, 0, 0), (1.2, 1.6, 0.4)), "giant coin slot panel with a coin half in")
def coin_slot():
    parts = [round_box((1.2, 1.6, 0.3), 0.14, 2, "grape", PROP)]
    parts.append(round_box((0.9, 1.3, 0.06), 0.08, 1, "white", PROP, centre=(0, 0, -0.17)))
    parts.append(round_box((0.16, 0.8, 0.06), 0.05, 1, "ink", PROP, centre=(0, 0.1, -0.2)))
    coin = lathe(round_cyl_profile(0.32, 0.07, 0.03, 2, y0=-0.035), 20, "sunYellow", PROP, axis="x")
    parts.append(coin.moved(mat_trs((0, 0.62, -0.2))))
    return parts


@prop("prize_star", "star centre (score only: Star Fountain dressing, podium)", ((0, 0, 0), (1.0, 0.95, 0.3)), "prize star, bevelled")
def prize_star():
    return [extrude(star_outline(0.5, 0.22, 5), 0.3, "sunYellow", PROP, bevel=0.06)]


@prop("confetti_cannon", "base centre (dressing)", ((0, 0.8, 0.2), (1.2, 1.7, 1.6)), "confetti cannon tilted up toward +z")
def confetti_cannon():
    parts = [round_box((1.1, 0.4, 1.1), 0.14, 2, "grape", PROP, centre=(0, 0.2, 0))]
    barrel = lathe(round_cyl_profile(0.32, 1.4, 0.08, 2, y0=0.0), 16, "white", PROP)
    m = mat_trs((0, 0.45, 0), ((1, 0, 0), 35))
    parts.append(barrel.moved(m))
    for k, y in enumerate((0.3, 0.7, 1.1)):
        parts.append(band(0.325, y, 0.12, 16, "magenta" if k % 2 == 0 else "sky", PROP, bulge=0.01).moved(m))
    parts.append(lathe([(0.25, 1.38), (0.3, 1.42), (0.0, 1.42)], 16, "ink", PROP, cap_bottom=False).moved(m))
    return parts


@prop("pennant", "pole foot (dressing)", ((0.55, 1.6, 0), (1.25, 3.2, 0.1)), "pennant flag on a pole, flying toward +x")
def pennant():
    parts = [tube([(0, 0, 0), (0, 3.1, 0)], 0.05, 6, "white", PROP)]
    parts.append(sphere(0.1, 8, 4, "sunYellow", PROP).moved(mat_trs((0, 3.15, 0))))
    parts.append(tri_flag((0.04, 3.0, 0), (0.04, 2.25, 0), (1.15, 2.62, 0), 0.04, "magenta", PROP))
    return parts


@prop("bunting", "left end (6 m span along +x, 0.5 m sag)", ((3.0, -0.6, 0), (6.0, 1.3, 0.1)), "bunting string with 8 party flags (never a lemon-magenta pair)")
def bunting():
    span, sag = 6.0, 0.5
    pts = [(span * i / 12, -sag * 4 * (i / 12) * (1 - i / 12), 0) for i in range(13)]
    parts = [tube(pts, 0.02, 4, "white", PROP)]
    cols = ["mint", "sky", "grape", "white", "magenta", "sky", "mint", "grape"]
    for i in range(8):
        t = (i + 0.5) / 8
        x, y = span * t, -sag * 4 * t * (1 - t)
        parts.append(tri_flag((x - 0.26, y, 0), (x + 0.26, y, 0), (x, y - 0.6, 0), 0.03, cols[i], PROP))
    return parts


@prop("bleacher_block", "front bottom centre (backdrop)", ((0, 1.0, 1.4), (4.0, 2.0, 2.8)), "four-step bleacher block")
def bleacher_block():
    parts = []
    for k in range(4):
        parts.append(round_box((4.0, 0.5 * (k + 1), 0.7), 0.08, 1, "wallSide" if k % 2 == 0 else "walkSide", PROP, centre=(0, 0.25 * (k + 1), 0.35 + 0.7 * k)))
    for s in (-1, 1):
        parts.append(round_box((0.2, 2.1, 2.8), 0.08, 1, "grape", PROP, centre=(s * 2.0, 1.05, 1.4)))
    return parts


@prop("inflatable_tower", "foot centre (dressing / backdrop)", ((0, 4.2, 0), (2.2, 8.4, 2.2)), "inflatable tower with bulge rings and a dome")
def inflatable_tower():
    pr = [(0.0, 0.0), (1.0, 0.0)]
    for i in range(1, 17):
        h = 7.4 * i / 16
        pr.append((0.92 + 0.08 * abs(math.sin(math.pi * h / 1.85)), h))
    for i in range(1, 6):
        a = math.pi / 2 * i / 5
        pr.append((0.95 * math.cos(a), 7.4 + 0.95 * math.sin(a)))
    pr[-1] = (0.0, pr[-1][1])
    parts = [lathe(pr, 16, "sky", PROP)]
    for y in (1.85, 3.7, 5.55):
        parts.append(band(0.93, y, 0.16, 16, "white", PROP, bulge=0.03))
    parts.append(sphere(0.2, 8, 4, "sunYellow", PROP).moved(mat_trs((0, 8.4, 0))))
    return parts


@prop("spring_pad", "pad top centre (scaled x/z by the pad size; collider 0.3 m thick below the top)", ((0, -0.15, 0), (1.0, 0.3, 1.0)),
      "spring pad: mint top, checker border, a coil band round the side; unit 1 x 0.3 x 1")
def spring_pad():
    parts = [round_box((1.0, 0.22, 1.0), 0.06, 1, "mint", SIDE, centre=(0, -0.11, 0))]
    # checker border on the top (8 tiles a side, alternating ink / white)
    n = 8
    for side in range(4):
        for k in range(n):
            if (k + side) % 2:
                continue
            t = -0.5 + (k + 0.5) / n
            c = (t * 0.94, 0.003, 0.44) if side == 0 else (0.44, 0.003, t * 0.94) if side == 1 else (t * 0.94, 0.003, -0.44) if side == 2 else (-0.44, 0.003, t * 0.94)
            parts.append(box((0.11, 0.01, 0.11), "ink", SIDE, centre=c))
    coil = []
    for i in range(49):  # a coil band round the side (inside the collider box)
        a = 2 * math.pi * i / 24
        coil.append((math.cos(a) * 0.47, -0.27 + 0.13 * i / 48, math.sin(a) * 0.47))
    parts.append(tube(coil, 0.025, 4, "white", SIDE))
    return parts


@prop("goo_blob", "blob foot centre (Goo Tower dressing)", ((0, 0.45, 0), (1.8, 0.9, 1.8)), "goo glitter blob")
def goo_blob():
    def lump(a, h):
        return 1.0 + 0.08 * math.sin(3 * a + 1.3) + 0.05 * math.sin(5 * a)
    pr = [(0.0, 0.0)] + [(0.9 * math.cos(math.pi / 2 * i / 7), 0.9 * math.sin(math.pi / 2 * i / 7)) for i in range(8)]
    pr[-1] = (0.0, 0.9)
    parts = [lathe(pr, 20, "goo", PROP, radial=lump)]
    for k in range(9):  # glitter specks
        a = 2.39996 * k
        h = 0.2 + 0.07 * k
        r = 0.9 * math.cos(math.asin(min(0.99, h / 0.9))) * 0.98
        parts.append(sphere(0.035, 6, 3, "white", PROP).moved(mat_trs((math.cos(a) * r, h, math.sin(a) * r))))
    return parts


@prop("claw", "hub centre (Lid Flip frame, hangs from the pole)", ((0, -0.5, 0), (1.6, 1.4, 1.6)), "claw-machine claw: hub + three prongs")
def claw():
    parts = [lathe(round_cyl_profile(0.35, 0.5, 0.12, 2, y0=-0.25), 16, "grape", PROP)]
    for k in range(3):
        a = 2 * math.pi * k / 3
        d = Vector((math.cos(a), 0, math.sin(a)))
        pts = [d * 0.3 + Vector((0, -0.2, 0)), d * 0.65 + Vector((0, -0.55, 0)), d * 0.72 + Vector((0, -0.95, 0)), d * 0.55 + Vector((0, -1.15, 0))]
        parts.append(tube([tuple(p) for p in pts], 0.07, 6, "white", PROP))
    parts.append(sphere(0.12, 10, 5, "sunYellow", PROP).moved(mat_trs((0, -0.3, 0))))
    return parts


@prop("windup_key", "shaft foot (dressing: on a capsule machine or a tower)", ((0, 0, 0.45), (1.4, 1.0, 0.9)), "wind-up key: shaft along +z, two round loops")
def windup_key():
    parts = [lathe(round_cyl_profile(0.07, 0.5, 0.03, 2, y0=0.0), 8, "sunYellow", PROP, axis="z")]
    parts.append(lathe(round_cyl_profile(0.14, 0.12, 0.04, 2, y0=0.45), 12, "sunYellow", PROP, axis="z"))
    for s in (-1, 1):
        loop = []
        for i in range(17):
            a = 2 * math.pi * i / 16
            loop.append((s * (0.38 + math.cos(a) * 0.3), math.sin(a) * 0.42, 0.62))
        parts.append(tube(loop[:-1], 0.07, 6, "sunYellow", PROP, closed=True))
    return parts


@prop("ribbon_banner", "banner centre (checkpoint / finish banners, above reach)", ((0, 0, 0), (6.4, 1.3, 0.3)), "wavy ribbon banner with forked tails")
def ribbon_banner():
    verts, faces = [], []
    n = 16
    for i in range(n + 1):
        x = -2.6 + 5.2 * i / n
        z = 0.08 * math.sin(i / n * 2 * math.pi)
        for (y, dz) in ((0.4, -0.04), (-0.4, -0.04), (-0.4, 0.04), (0.4, 0.04)):
            verts.append((x, y, z + dz))
    for i in range(n):
        for k in range(4):
            a, b = i * 4 + k, i * 4 + (k + 1) % 4
            faces.append((a, b, b + 4, a + 4))
    faces.append((3, 2, 1, 0))
    e = n * 4
    faces.append((e, e + 1, e + 2, e + 3))
    parts = [Part(verts, faces, "magenta", PROP, smooth=True)]
    for s in (-1, 1):  # forked tails
        x0 = s * 2.6
        parts.append(tri_flag((x0, 0.35, 0), (x0, -0.35, 0), (x0 + s * 0.6, 0.0, 0), 0.05, "grape", PROP))
        parts.append(tri_flag((x0, 0.35, 0), (x0 + s * 0.6, 0.5, 0), (x0 + s * 0.2, 0.0, 0), 0.05, "grape", PROP))
    parts.append(round_box((5.0, 0.08, 0.1), 0.03, 1, "white", PROP, centre=(0, 0.3, -0.06)))
    parts.append(round_box((5.0, 0.08, 0.1), 0.03, 1, "white", PROP, centre=(0, -0.3, -0.06)))
    return parts


# ------------------------------------------------------------------ bake + export

def ao_shade(verts, normals, faces_tris, lo_y, hi_y, size):
    """Per vertex: (1 - 0.55 x occlusion from 16 fixed hemisphere rays) x a bottom-dark gradient (0.8 -> 1)."""
    bvh = BVHTree.FromPolygons(verts, faces_tris, all_triangles=True, epsilon=0.0)
    dirs = []
    for i in range(16):  # golden-spiral directions on the upper hemisphere (fixed)
        z = 1 - (i + 0.5) / 16
        r = math.sqrt(max(0.0, 1 - z * z))
        a = i * 2.399963229728653
        dirs.append(Vector((math.cos(a) * r, math.sin(a) * r, z)))
    reach = max(0.25, 0.35 * size)
    out = []
    for p, n in zip(verts, normals):
        t = n.orthogonal().normalized()
        b = n.cross(t)
        hit = 0
        for d in dirs:
            w = (t * d.x + b * d.y + n * d.z).normalized()
            loc, _, _, dist = bvh.ray_cast(p + n * 0.004, w, reach)
            if loc is not None:
                hit += 1
        occ = hit / len(dirs)
        g = 0.8 + 0.2 * (0.0 if hi_y <= lo_y else min(1.0, max(0.0, (p.y - lo_y) / (hi_y - lo_y))))
        out.append(round((1 - 0.55 * occ) * g, 4))
    return out


def outward(pt):
    """Turn a part's faces outward: the signed volume about the part's centroid must be positive (closed shells and the
    open lathe bands around an axis through it alike)."""
    c = sum(pt.v, Vector((0, 0, 0))) / max(1, len(pt.v))
    vol = 0.0
    for f in pt.f:
        a = pt.v[f[0]] - c
        for k in range(1, len(f) - 1):
            b, d = pt.v[f[k]] - c, pt.v[f[k + 1]] - c
            vol += a.dot(b.cross(d))
    if vol < 0:
        pt.f = [tuple(reversed(f)) for f in pt.f]


def build(name, spec):
    parts = spec["fn"]()
    for pt in parts:
        outward(pt)
    verts, faces, slots, roles, smooth = [], [], [], [], []
    for pt in parts:
        base = len(verts)
        verts += pt.v
        for f in pt.f:
            faces.append(tuple(base + i for i in f))
            slots.append(pt.slot)
            roles.append(pt.role)
            smooth.append(pt.smooth)
    tris = sum(len(f) - 2 for f in faces)
    assert tris <= TRI_CAP, f"{name}: {tris} triangles > {TRI_CAP}"

    # Unity axes -> Blender (x, -z, y): the FBX writer (forward -Z, up Y) maps it back to Unity's (x, y, z).
    bverts = [(v.x, -v.z, v.y) for v in verts]
    me = bpy.data.meshes.new(name)
    me.from_pydata(bverts, [], faces)
    me.validate(clean_customdata=False)
    me.update()
    for i, poly in enumerate(me.polygons):
        poly.use_smooth = smooth[i] if i < len(smooth) else True
    me.set_sharp_from_angle(angle=math.radians(50))

    # Per-vertex shade (AO x gradient) from smooth vertex normals, in Unity axes.
    vn = {}
    for poly in me.polygons:
        for vi in poly.vertices:
            vn.setdefault(vi, Vector((0, 0, 0)))
            vn[vi] += poly.normal * poly.area
    norms_u, verts_u = [], []
    for i, v in enumerate(me.vertices):
        n = vn.get(i, Vector((0, 0, 1)))
        n = n.normalized() if n.length > 1e-9 else Vector((0, 0, 1))
        norms_u.append(Vector((n.x, n.z, -n.y)))
        verts_u.append(Vector((v.co.x, v.co.z, -v.co.y)))
    tri_idx = []
    for poly in me.polygons:
        vs = list(poly.vertices)
        for k in range(1, len(vs) - 1):
            tri_idx.append((vs[0], vs[k], vs[k + 1]))
    lo = min(v.y for v in verts_u)
    hi = max(v.y for v in verts_u)
    size = max(max(v.x for v in verts_u) - min(v.x for v in verts_u), hi - lo, max(v.z for v in verts_u) - min(v.z for v in verts_u))
    shade = ao_shade(verts_u, norms_u, tri_idx, lo, hi, size)

    uv_slot = me.uv_layers.new(name="SlotRole")
    uv_shade = me.uv_layers.new(name="Shade")
    col = me.color_attributes.new(name="Col", type="FLOAT_COLOR", domain="CORNER")
    for pi, poly in enumerate(me.polygons):
        s, r = slots[pi], roles[pi]
        hexv = SLOTS[s][1]
        base = ((hexv >> 16 & 255) / 255, (hexv >> 8 & 255) / 255, (hexv & 255) / 255)
        for li in poly.loop_indices:
            vi = me.loops[li].vertex_index
            uv_slot.data[li].uv = (float(s), float(r))
            uv_shade.data[li].uv = (shade[vi], 0.0)
            k = shade[vi]
            col.data[li].color = (base[0] * k, base[1] * k, base[2] * k, 1.0)

    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob, tris, verts_u


def export(ob, path):
    for o in bpy.context.scene.objects:
        o.select_set(False)
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"MESH"}, use_mesh_modifiers=False,
        mesh_smooth_type="OFF", use_tspace=False, colors_type="SRGB", add_leaf_bones=False, bake_anim=False,
        axis_forward="-Z", axis_up="Y", apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
        global_scale=1.0, bake_space_transform=True, path_mode="STRIP", embed_textures=False,
        use_custom_props=False, use_metadata=False)


def export_glb(ob, path):
    """Web landing (web/site): the same mesh as a .glb with the soft-look preview colours (COLOR_0 = albedo x shade)."""
    for o in bpy.context.scene.objects:
        o.select_set(False)
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.export_scene.gltf(
        filepath=path, export_format="GLB", use_selection=True, export_texcoords=False, export_normals=True,
        export_materials="NONE", export_vertex_color="ACTIVE", export_all_vertex_colors=False, export_yup=True,
        export_apply=False, export_animations=False, export_extras=False)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = "client-unity/Assets/_Game/Art/Props"
    if "--out" in argv:
        out = argv[argv.index("--out") + 1]
    if "--glb" in argv:
        # only the .glb files (the Unity FBX kit and props.json are left untouched)
        glb = argv[argv.index("--glb") + 1]
        os.makedirs(glb, exist_ok=True)
        bpy.ops.wm.read_factory_settings(use_empty=True)
        for name in sorted(PROPS):
            ob, tris, _ = build(name, PROPS[name])
            ob.data.color_attributes.active_color = ob.data.color_attributes["Col"]
            export_glb(ob, os.path.join(glb, name + ".glb"))
            bpy.data.objects.remove(ob)
            print(f"[props] {name}.glb: {tris} tris")
        return
    os.makedirs(out, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.unit_settings.system = "METRIC"
    bpy.context.scene.unit_settings.scale_length = 1.0
    report = {"generator": "tools/blender/props.py", "slots": [n for n, _ in SLOTS],
              "layout": "UV0 = (slot, role), UV1 = (shade, 0); Unity resolves the slot per look (PropKit.cs)",
              "triCap": TRI_CAP, "props": []}
    for name in sorted(PROPS):
        spec = PROPS[name]
        ob, tris, vu = build(name, spec)
        export(ob, os.path.join(out, name + ".fbx"))
        mn = [min(v[i] for v in vu) for i in range(3)]
        mx = [max(v[i] for v in vu) for i in range(3)]
        (cx, cy, cz), (sx, sy, sz) = spec["box"]
        report["props"].append({
            "name": name, "tris": tris, "pivot": spec["pivot"], "note": spec["note"],
            "box": {"centre": [cx, cy, cz], "size": [sx, sy, sz]},
            "bounds": {"min": [round(c, 4) for c in mn], "max": [round(c, 4) for c in mx]},
        })
        bpy.data.objects.remove(ob)
        print(f"[props] {name}: {tris} tris, bounds {[round(c, 3) for c in mn]} .. {[round(c, 3) for c in mx]}")
    with open(os.path.join(out, "props.json"), "w") as f:
        json.dump(report, f, indent=1, sort_keys=True)
        f.write("\n")
    print(f"[props] wrote {len(PROPS)} props to {out}")


main()
