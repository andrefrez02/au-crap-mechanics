using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Transforms;

namespace CrapMechanics
{
    /// <summary>0.31.28 seal 2: [Debug] seal_watch (log only). Every 2 s, while the local player is inside a ship on the server/host, repeats
    /// the game's own room test for the player and writes one line: how many of the 54 casts are blocked / open / gappy and, for the casts that
    /// get out, which block, prefab and face they crossed last (top 5) and by how much they missed the nearest closed face.
    ///
    /// The game's test, as read in the 21/11 Burst (InteriorMetersPreUpdate; lib_burst 0xC4500-0xC5276, revisao/seal2_microframe.md):
    ///  - InitJob: the player's point inside any collider of the filter -> no casts, counts as closed.
    ///  - 54 directions (3x3 jittered grid on each cube face, uniformCubePoint); SphereCast radius 0.035 m, 200 m, ALL hits, filter
    ///    BelongsTo ~0 / CollidesWith 0x18 (ship blocks and frames) — so only the ships' part colliders.
    ///  - per hit: the leaf entity (CompoundCollider.Child.Entity) not a frame -> SOLID, stop. Frame with no solid face -> GAPPY, go on.
    ///    All faces solid -> SOLID. Else the CENTRE LINE (infinite, no distance check), taken into the block's space with the part body's
    ///    pose and the block's LocalTransform (no scale), against each solid face split (v0,v1,v2)+(v0,v2,v3), barycentric -0.02..1.02 ->
    ///    SOLID; none -> GAPPY. Result: SOLID if any hit is solid, GAPPY if something was hit, else OPEN (air).
    /// Here the same, on the part colliders read straight from memory (compound children: box exact, other hulls by their planes, which can
    /// only make a block look touched a little more often near its edges). Nothing is written. Cost: a few ms every 2 s.</summary>
    internal static class SealWatch
    {
        public static bool Enabled = true;   // [Debug] seal_watch
        internal const float R = 0.035f, L = 200f, Tol = 0.02f; const int Rays = 54; const uint Filter = 0x18;   // 0.31.30 sound: R/Tol/L shared with SoundSeal.cs
        const float Period = 2f;

        internal struct V { public float x, y, z; public V(float a, float b, float c) { x = a; y = b; z = c; } }
        internal struct Q { public float x, y, z, w; }
        internal static V Add(V a, V b) => new V(a.x + b.x, a.y + b.y, a.z + b.z);
        internal static V Sub(V a, V b) => new V(a.x - b.x, a.y - b.y, a.z - b.z);
        internal static V Mul(V a, float s) => new V(a.x * s, a.y * s, a.z * s);
        internal static float Dot(V a, V b) => a.x * b.x + a.y * b.y + a.z * b.z;
        internal static V Cross(V a, V b) => new V(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        internal static float Len(V a) => (float)Math.Sqrt(Dot(a, a));
        internal static V Rot(Q q, V v)
        {
            float cx = q.y * v.z - q.z * v.y + q.w * v.x, cy = q.z * v.x - q.x * v.z + q.w * v.y, cz = q.x * v.y - q.y * v.x + q.w * v.z;
            return new V(v.x + 2f * (q.y * cz - q.z * cy), v.y + 2f * (q.z * cx - q.x * cz), v.z + 2f * (q.x * cy - q.y * cx));
        }
        internal static Q Inv(Q q) => new Q { x = -q.x, y = -q.y, z = -q.z, w = q.w };
        internal static Q Mul(Q p, Q q) => new Q { x = p.w * q.x + p.x * q.w + p.y * q.z - p.z * q.y, y = p.w * q.y - p.x * q.z + p.y * q.w + p.z * q.x, z = p.w * q.z + p.x * q.y - p.y * q.x + p.z * q.w, w = p.w * q.w - p.x * q.x - p.y * q.y - p.z * q.z };
        internal static unsafe V RdV(byte* p) { var f = (float*)p; return new V(f[0], f[1], f[2]); }
        internal static unsafe Q RdQ(byte* p) { var f = (float*)p; return new Q { x = f[0], y = f[1], z = f[2], w = f[3] }; }

        // ---------------------------------------------------------------- part colliders (cached per collider blob)
        internal class Child
        {
            public Entity e; public bool solidRoot; public float cr;
            public bool box; public V bc, bh; public Q bq;          // box core (centre, half extents minus bevel, orientation) in child space
            public V[] verts; public float[] planes;                // other hulls: core vertices, planes (nx, ny, nz, d) in child space
            public V cmin, cmax;                                    // child-space AABB incl. the convex radius
            public int offset;                                      // byte offset of the Child record in the blob (pose re-read every run)
            public Q rot; public V pos; public V amin, amax;        // CompoundFromChild and part-space AABB (every run)
        }
        internal class Part { public IntPtr blob; public long sig; public Child[] ch; public int[] order; public V[] gmin, gmax; public V pmin, pmax; public int unsupported; }
        static readonly Dictionary<long, Part> partCache = new Dictionary<long, Part>();
        const int Group = 16;

        static TypeIndex tiPC, tiIMD, tiMine; static EntityQuery qMine; static bool ok, failed;
        static float t = 0f, tLog = -99f; static string lastSig = ""; static int warnings;
        static readonly Random rng = new Random(1234);

        static bool Types(EntityManager em)
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiPC = TypeManager.GetTypeIndex(Il2CppType.Of<Unity.Physics.PhysicsCollider>());
                tiIMD = TypeManager.GetTypeIndex(Il2CppType.Of<InteriorMeterData>());
                tiMine = TypeManager.GetTypeIndex(Il2CppType.Of<MyPlayerController>());
                var a = new Il2CppStructArray<ComponentType>(2); a[0] = ComponentType.ReadOnly(tiMine); a[1] = ComponentType.ReadOnly(tiIMD);
                qMine = em.CreateEntityQuery(a);
                if (!FrameFaces.Types(em)) { failed = true; return false; }
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("seal watch: types not available (" + ex.Message + ") — off"); return false; }
        }

        internal static unsafe Part PartOf(EntityManager em, Entity part, IntPtr blob)
        {
            var c = (byte*)blob; long key = Ecs.K(part);
            byte type = c[0]; int n = 1, off = 0; byte* kids = null;
            if (type == 8) { off = *(int*)(c + 24); n = *(int*)(c + 28); if (n <= 0 || n > 4000000 || off <= 0) return null; kids = c + 24 + off; }
            // signature: count + every child's entity and collider offset (a freed blob can come back at the same address)
            long sig = n * 1000003L + type;
            if (kids != null) for (int i = 0; i < n; i++) { byte* ch = kids + 40 * i; sig = sig * 31 + *(int*)(ch + 32) * 7919L + *(int*)(ch + 36) + *(int*)(ch + 28); }
            if (partCache.TryGetValue(key, out var p) && p.blob == blob && p.sig == sig) return p;
            p = new Part { blob = blob, sig = sig };
            var list = new List<Child>(n);
            for (int i = 0; i < n; i++)
            {
                byte* col; Child k;
                if (kids != null) { byte* ch = kids + 40 * i; col = ch + 28 + *(int*)(ch + 28); k = new Child { e = *(Entity*)(ch + 32), offset = (int)(ch - c) }; }
                else { col = c; k = new Child { e = part, solidRoot = true, offset = -1 }; }   // not a compound: the game calls any hit solid
                byte ct = col[0];
                uint belongs = *(uint*)(col + 8), collides = *(uint*)(col + 12);
                if ((belongs & Filter) == 0 || collides == 0) continue;     // the game's filter: CollidesWith 0x18 vs the child's BelongsTo
                if (ct > 6 || ct == 3 || ct == 4) { p.unsupported++; continue; }   // mesh / nested compound / terrain children: not in ships; review: triangle/quad
                                                                                    // too (their 2 planes are the polygon's two sides: the plane test would see an infinite plane)
                k.cr = *(float*)(col + 32);
                int vOff = *(int*)(col + 36), vN = *(int*)(col + 40), pOff = *(int*)(col + 44), pN = *(int*)(col + 48);
                if (vN <= 0 || vN > 512 || pN < 0 || pN > 512) { p.unsupported++; continue; }
                byte* vp = col + 36 + vOff; k.verts = new V[vN];
                for (int j = 0; j < vN; j++) k.verts[j] = RdV(vp + 12 * j);
                if (pN > 0) { byte* pp = col + 44 + pOff; k.planes = new float[4 * pN]; for (int j = 0; j < 4 * pN; j++) k.planes[j] = ((float*)pp)[j]; }
                if (ct == 5)
                {
                    k.box = true; k.bc = RdV(col + 84); k.bq = RdQ(col + 464); var s = RdV(col + 480);
                    k.bh = new V(Math.Max(0f, s.x * 0.5f - k.cr), Math.Max(0f, s.y * 0.5f - k.cr), Math.Max(0f, s.z * 0.5f - k.cr));
                }
                V mn = k.verts[0], mx = k.verts[0];
                foreach (var v in k.verts) { mn = new V(Math.Min(mn.x, v.x), Math.Min(mn.y, v.y), Math.Min(mn.z, v.z)); mx = new V(Math.Max(mx.x, v.x), Math.Max(mx.y, v.y), Math.Max(mx.z, v.z)); }
                k.cmin = Sub(mn, new V(k.cr, k.cr, k.cr)); k.cmax = Add(mx, new V(k.cr, k.cr, k.cr));
                list.Add(k);
            }
            p.ch = list.ToArray();
            Pose(p, c);
            // groups of 16 children close together (Morton order of the centres): the ray tests a group's box before its children
            var codes = new long[p.ch.Length]; p.order = new int[p.ch.Length];
            for (int i = 0; i < p.ch.Length; i++)
            {
                p.order[i] = i; var m = Mul(Add(p.ch[i].amin, p.ch[i].amax), 0.5f);
                codes[i] = Morton((int)Math.Floor(m.x * 2f) + 1024, (int)Math.Floor(m.y * 2f) + 1024, (int)Math.Floor(m.z * 2f) + 1024);
            }
            Array.Sort(codes, p.order);
            int g = (p.ch.Length + Group - 1) / Group; p.gmin = new V[g]; p.gmax = new V[g];
            partCache[key] = p;
            return p;
        }
        static long Morton(int x, int y, int z)
        {
            long r = 0; for (int b = 0; b < 21; b++) r |= ((long)((x >> b) & 1) << (3 * b)) | ((long)((y >> b) & 1) << (3 * b + 1)) | ((long)((z >> b) & 1) << (3 * b + 2));
            return r;
        }
        /// <summary>CompoundFromChild re-read every run (a door of the game moves its child in place) and the part-space boxes.</summary>
        internal static unsafe void Pose(Part p, byte* c)
        {
            V pmn = new V(1e30f, 1e30f, 1e30f), pmx = new V(-1e30f, -1e30f, -1e30f);
            foreach (var k in p.ch)
            {
                if (k.offset >= 0) { k.rot = RdQ(c + k.offset); k.pos = RdV(c + k.offset + 16); } else { k.rot = new Q { w = 1f }; k.pos = default; }
                var cc = Mul(Add(k.cmin, k.cmax), 0.5f); var h = Mul(Sub(k.cmax, k.cmin), 0.5f);
                var ax = Rot(k.rot, new V(1, 0, 0)); var ay = Rot(k.rot, new V(0, 1, 0)); var az = Rot(k.rot, new V(0, 0, 1));
                var e = new V(Math.Abs(ax.x) * h.x + Math.Abs(ay.x) * h.y + Math.Abs(az.x) * h.z, Math.Abs(ax.y) * h.x + Math.Abs(ay.y) * h.y + Math.Abs(az.y) * h.z, Math.Abs(ax.z) * h.x + Math.Abs(ay.z) * h.y + Math.Abs(az.z) * h.z);
                var w = Add(k.pos, Rot(k.rot, cc));
                k.amin = Sub(w, e); k.amax = Add(w, e);
                pmn = new V(Math.Min(pmn.x, k.amin.x), Math.Min(pmn.y, k.amin.y), Math.Min(pmn.z, k.amin.z)); pmx = new V(Math.Max(pmx.x, k.amax.x), Math.Max(pmx.y, k.amax.y), Math.Max(pmx.z, k.amax.z));
            }
            p.pmin = pmn; p.pmax = pmx;
            if (p.order != null)
                for (int g = 0; g < p.gmin.Length; g++)
                {
                    V mn = new V(1e30f, 1e30f, 1e30f), mx = new V(-1e30f, -1e30f, -1e30f);
                    for (int i = g * Group; i < Math.Min(p.ch.Length, (g + 1) * Group); i++) { var k = p.ch[p.order[i]]; mn = new V(Math.Min(mn.x, k.amin.x), Math.Min(mn.y, k.amin.y), Math.Min(mn.z, k.amin.z)); mx = new V(Math.Max(mx.x, k.amax.x), Math.Max(mx.y, k.amax.y), Math.Max(mx.z, k.amax.z)); }
                    p.gmin[g] = mn; p.gmax[g] = mx;
                }
        }

        /// <summary>Segment o + t d (t in 0..L) against a box grown by m: the t interval inside, or false.</summary>
        internal static bool Slab(V o, V d, V mn, V mx, float m, out float ta, out float tb)
        {
            ta = 0f; tb = L;
            if (!Axis(o.x, d.x, mn.x - m, mx.x + m, ref ta, ref tb)) return false;
            if (!Axis(o.y, d.y, mn.y - m, mx.y + m, ref ta, ref tb)) return false;
            return Axis(o.z, d.z, mn.z - m, mx.z + m, ref ta, ref tb);
        }
        static bool Axis(float o, float d, float lo, float hi, ref float ta, ref float tb)
        {
            if (Math.Abs(d) < 1e-9f) return o >= lo && o <= hi;
            float a = (lo - o) / d, b = (hi - o) / d; if (a > b) { var x = a; a = b; b = x; }
            if (a > ta) ta = a; if (b < tb) tb = b; return ta <= tb;
        }
        /// <summary>Distance from p (child space) to the core shape (0 inside).</summary>
        internal static float CoreDist(Child k, V p)
        {
            if (k.box)
            {
                var q = Rot(Inv(k.bq), Sub(p, k.bc));
                float x = Math.Max(Math.Abs(q.x) - k.bh.x, 0f), y = Math.Max(Math.Abs(q.y) - k.bh.y, 0f), z = Math.Max(Math.Abs(q.z) - k.bh.z, 0f);
                return (float)Math.Sqrt(x * x + y * y + z * z);
            }
            if (k.planes != null)
            {
                float m = -1e30f; var pl = k.planes;
                for (int i = 0; i < pl.Length; i += 4) { float s = pl[i] * p.x + pl[i + 1] * p.y + pl[i + 2] * p.z + pl[i + 3]; if (s > m) m = s; }
                return Math.Max(m, 0f);
            }
            if (k.verts.Length == 2)
            {
                var a = k.verts[0]; var ab = Sub(k.verts[1], a); float l2 = Dot(ab, ab); float u = l2 > 1e-12f ? Math.Max(0f, Math.Min(1f, Dot(Sub(p, a), ab) / l2)) : 0f;
                return Len(Sub(p, Add(a, Mul(ab, u))));
            }
            float best = 1e30f; foreach (var v in k.verts) best = Math.Min(best, Len(Sub(p, v))); return best;
        }
        /// <summary>Does the swept sphere touch the child? (min over t of the core distance, golden section: the distance to a convex set
        /// along a line is convex.) tHit = where.</summary>
        internal static bool Touch(Child k, V oc, V dc, float ta, float tb, out float tHit)
        {
            float lim = R + k.cr; const float g = 0.618034f;
            float a = ta, b = tb, x1 = b - g * (b - a), x2 = a + g * (b - a);
            float f1 = CoreDist(k, Add(oc, Mul(dc, x1))), f2 = CoreDist(k, Add(oc, Mul(dc, x2)));
            for (int it = 0; it < 30 && b - a > 1e-4f; it++)
            {
                if (f1 < f2) { b = x2; x2 = x1; f2 = f1; x1 = b - g * (b - a); f1 = CoreDist(k, Add(oc, Mul(dc, x1))); }
                else { a = x1; x1 = x2; f1 = f2; x2 = a + g * (b - a); f2 = CoreDist(k, Add(oc, Mul(dc, x2))); }
            }
            float fa = CoreDist(k, Add(oc, Mul(dc, ta))), fb = CoreDist(k, Add(oc, Mul(dc, tb)));
            float best = f1; tHit = x1; if (f2 < best) { best = f2; tHit = x2; } if (fa < best) { best = fa; tHit = ta; } if (fb < best) { best = fb; tHit = tb; }
            return best <= lim;
        }

        // ---------------------------------------------------------------- per block (read once per run)
        class Block { public bool frame; public IntPtr faces; public byte mask; public int count; public bool lt; public V lp; public Q lq; public string name; public bool poseChecked; }
        static readonly Dictionary<long, Block> blocks = new Dictionary<long, Block>();
        static unsafe Block BlockOf(EntityManager em, Entity e)
        {
            long key = Ecs.K(e);
            if (blocks.TryGetValue(key, out var b)) return b;
            b = new Block();
            try
            {
                if (Ecs.HasC(em, e, FrameFaces.TiFrame))
                {
                    var f = (byte*)Ecs.RO(em, e, FrameFaces.TiFrame);
                    if (f != null) { b.faces = *(IntPtr*)f; b.mask = f[12]; b.frame = GameFields.Plausible((void*)b.faces); if (b.frame) b.count = *(int*)((byte*)b.faces + FrameFaces.OffCount); if (b.count < 0 || b.count > 8) b.frame = false; }
                    if (Ecs.Has(em, e, Ecs.tiLT)) { var lt = (LocalTransform*)Ecs.RO(em, e, Ecs.tiLT); b.lp = new V(lt->Position.x, lt->Position.y, lt->Position.z); b.lq = new Q { x = lt->Rotation.value.x, y = lt->Rotation.value.y, z = lt->Rotation.value.z, w = lt->Rotation.value.w }; b.lt = true; }
                }
            }
            catch (Exception) { b.frame = false; }
            blocks[key] = b;
            return b;
        }
        static string NameOf(EntityManager em, Entity e, Block b)
        {
            if (b.name != null) return b.name;
            try { b.name = BlockIndex.NameOfHash(Ecs.Prefab(em, e)) ?? "?"; } catch (Exception) { b.name = "?"; }
            return b.name;
        }

        static unsafe V FaceVert(IntPtr faces, int i, int k) { var p = (float*)((byte*)faces + FrameFaces.FaceStride * i) + 3 * k; return new V(p[0], p[1], p[2]); }
        static unsafe int FaceVerts(IntPtr faces, int i) => *(int*)((byte*)faces + FrameFaces.FaceStride * i + 48);
        /// <summary>The game's FaceData.RayCast as used by the interior test: no distance check, tolerance 0.02 (and |det| >= 1e-8).</summary>
        static bool LineTri(V o, V d, V v0, V v1, V v2)
        {
            var e1 = Sub(v1, v0); var e2 = Sub(v2, v0); var p = Cross(d, e2); float det = Dot(e1, p);
            if (Math.Abs(det) < 1e-8f) return false;
            float inv = 1f / det; var s = Sub(o, v0); float u = Dot(s, p) * inv;
            if (u < -Tol || u > 1f + Tol) return false;
            var q = Cross(s, e1); float v = Dot(q, d) * inv;
            return v >= -Tol && u + v <= 1f + Tol;
        }
        static bool LineFace(IntPtr faces, int i, V o, V d)
        {
            int nv = FaceVerts(faces, i); var v0 = FaceVert(faces, i, 0); var v2 = FaceVert(faces, i, 2);
            if (LineTri(o, d, v0, FaceVert(faces, i, 1), v2)) return true;
            return nv == 4 && LineTri(o, d, v0, v2, FaceVert(faces, i, 3));
        }
        /// <summary>Where the line meets the face's plane (s along d) and how far that point is outside the polygon (0 = inside).</summary>
        static bool PlaneMiss(IntPtr faces, int i, V o, V d, out float s, out float miss, out V nrm)
        {
            int nv = FaceVerts(faces, i); s = 0f; miss = 1e30f; nrm = default; if (nv != 3 && nv != 4) return false;
            var v = new V[nv]; for (int k = 0; k < nv; k++) v[k] = FaceVert(faces, i, k);
            var n = Cross(Sub(v[1], v[0]), Sub(v[2], v[0])); float ln = Len(n); if (ln < 1e-9f) return false; n = Mul(n, 1f / ln);
            float dn = Dot(n, d); if (Math.Abs(dn) < 1e-6f) return false;
            s = Dot(n, Sub(v[0], o)) / dn; var x = Add(o, Mul(d, s));
            bool inside = true; float best = 1e30f; float sign = 0f;
            for (int k = 0; k < nv; k++)
            {
                var a = v[k]; var b = v[(k + 1) % nv]; var ab = Sub(b, a);
                float side = Dot(Cross(ab, Sub(x, a)), n); if (sign == 0f && Math.Abs(side) > 1e-9f) sign = Math.Sign(side); else if (side * sign < -1e-9f) inside = false;
                float l2 = Dot(ab, ab); float u = l2 > 1e-12f ? Math.Max(0f, Math.Min(1f, Dot(Sub(x, a), ab) / l2)) : 0f;
                best = Math.Min(best, Len(Sub(x, Add(a, Mul(ab, u)))));
            }
            miss = inside ? 0f : best;
            var cen = default(V); for (int k = 0; k < nv; k++) cen = Add(cen, v[k]); cen = Mul(cen, 1f / nv);
            nrm = Dot(n, cen) < 0f ? Mul(n, -1f) : n;   // outward (the block's centre is its origin)
            return true;
        }

        // ---------------------------------------------------------------- the run
        struct Hit { public Part p; public Child k; public float t; public V pp; public Q pq; }
        class Esc { public int n; public float miss = 1e30f; }

        public static unsafe void Step(EntityManager em)
        {
            if (!Enabled) return;
            t += UnityEngine.Time.unscaledDeltaTime; if (t < Period) return; t = 0f;
            if (Unloading.Active || !Ecs.Prepare(em) || !Types(em)) return;
            if (Multi.IAmClient(em)) return;   // the game runs this test on the server/host only
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { Run(em, sw); }
            catch (Exception ex) { if (warnings++ < 5) ModLog.Warn("seal watch: " + ex.Message); }
        }

        static unsafe void Run(EntityManager em, System.Diagnostics.Stopwatch sw)
        {
            // the local player (MyPlayerController) and the game's own meter on it
            Entity me = Entity.Null; foreach (var e in Ecs.QueryList(qMine)) { me = e; break; }
            if (Ecs.IsNull(me) || !Ecs.HasC(em, me, Ecs.tiLT)) return;
            V O; bool parented = Ecs.Has(em, me, Ecs.tiParent);
            if (parented && Ecs.Has(em, me, Ecs.tiLTW)) { var w = Ecs.Position(em, me); O = new V(w.x, w.y, w.z); }
            else { var lt = (LocalTransform*)Ecs.RO(em, me, Ecs.tiLT); O = new V(lt->Position.x, lt->Position.y, lt->Position.z); }
            var imd = (byte*)Ecs.RO(em, me, tiIMD);
            float vacuum = imd != null ? *(float*)(imd + 12) : -1f; Entity bound = imd != null ? *(Entity*)(imd + 48) : Entity.Null;
            bool tested = Ecs.Has(em, me, Ecs.tiPWI);   // the game's InitJob needs PhysicsWorldIndex (a seated player has none)

            // ship parts near the player
            var near = new List<(Part p, V pp, Q pq)>(); bool insideBox = false;
            var seen = new HashSet<long>();   // review: parts gone since (launch, split, recall) leave the cache
            foreach (var part in Ecs.Parts(em))
            {
                seen.Add(Ecs.K(part));
                if (!Ecs.Has(em, part, tiPC)) continue;
                IntPtr blob = *(IntPtr*)Ecs.RO(em, part, tiPC); if (blob == IntPtr.Zero) continue;
                var lt = (LocalTransform*)Ecs.RO(em, part, Ecs.tiLT);
                var pp = new V(lt->Position.x, lt->Position.y, lt->Position.z); var pq = new Q { x = lt->Rotation.value.x, y = lt->Rotation.value.y, z = lt->Rotation.value.z, w = lt->Rotation.value.w };
                var p = PartOf(em, part, blob); if (p == null || p.ch.Length == 0) continue;
                Pose(p, (byte*)blob);
                var ol = Rot(Inv(pq), Sub(O, pp));
                float dx = Math.Max(0f, Math.Max(p.pmin.x - ol.x, ol.x - p.pmax.x)), dy = Math.Max(0f, Math.Max(p.pmin.y - ol.y, ol.y - p.pmax.y)), dz = Math.Max(0f, Math.Max(p.pmin.z - ol.z, ol.z - p.pmax.z));
                if (dx + dy + dz == 0f) insideBox = true;
                if ((float)Math.Sqrt(dx * dx + dy * dy + dz * dz) > L) continue;
                near.Add((p, pp, pq));
            }
            if (partCache.Count > seen.Count) { var gone = new List<long>(); foreach (var key in partCache.Keys) if (!seen.Contains(key)) gone.Add(key); foreach (var key in gone) partCache.Remove(key); }
            if (Ecs.IsNull(bound) && !insideBox) return;   // not inside a ship
            blocks.Clear();

            // InitJob: the point itself inside a collider -> no casts
            foreach (var (p, pp, pq) in near)
            {
                var ol = Rot(Inv(pq), Sub(O, pp));
                foreach (var k in p.ch)
                {
                    if (ol.x < k.amin.x || ol.x > k.amax.x || ol.y < k.amin.y || ol.y > k.amax.y || ol.z < k.amin.z || ol.z > k.amax.z) continue;
                    if (CoreDist(k, Rot(Inv(k.rot), Sub(ol, k.pos))) <= k.cr)
                    {
                        var b = BlockOf(em, k.e);
                        Emit("seal watch: the player's point is inside " + NameOf(em, k.e, b) + " #" + k.e.Index + " — the game casts nothing and counts the room as closed by that block" + Game(vacuum, bound, tested), "inside" + k.e.Index, false);
                        return;
                    }
                }
            }

            int[] cnt = new int[3]; var esc = new Dictionary<string, Esc>(); int mism = 0; string mismEx = null; var hits = new List<Hit>();
            var faceTab = new[] { (new V(1, 0, 0), new V(0, 1, 0), new V(0, 0, 1)), (new V(-1, 0, 0), new V(0, 1, 0), new V(0, 0, 1)), (new V(0, 1, 0), new V(1, 0, 0), new V(0, 0, 1)),
                                  (new V(0, -1, 0), new V(1, 0, 0), new V(0, 0, 1)), (new V(0, 0, 1), new V(1, 0, 0), new V(0, 1, 0)), (new V(0, 0, -1), new V(1, 0, 0), new V(0, 1, 0)) };
            for (int r = 0; r < Rays; r++)
            {
                // the game's directions: face r/9, cell r%9 of a 3x3 grid, jittered, then uniformCubePoint
                var (fo, fa, fb) = faceTab[r / 9]; int cell = r % 9;
                float u = ((cell % 3 + (float)rng.NextDouble()) / 3f) * 2f - 1f, v = ((cell / 3 + (float)rng.NextDouble()) / 3f) * 2f - 1f;
                var c = Add(fo, Add(Mul(fa, u), Mul(fb, v)));
                float x2 = c.x * c.x, y2 = c.y * c.y, z2 = c.z * c.z;
                var D = new V(c.x * (float)Math.Sqrt(1 - y2 / 2 - z2 / 2 + y2 * z2 / 3), c.y * (float)Math.Sqrt(1 - z2 / 2 - x2 / 2 + z2 * x2 / 3), c.z * (float)Math.Sqrt(1 - x2 / 2 - y2 / 2 + x2 * y2 / 3));
                D = Mul(D, 1f / Len(D));

                hits.Clear();
                foreach (var (p, pp, pq) in near)
                {
                    var qi = Inv(pq); var o = Rot(qi, Sub(O, pp)); var d = Rot(qi, D);
                    if (!Slab(o, d, p.pmin, p.pmax, R, out _, out _)) continue;
                    for (int g = 0; g < p.gmin.Length; g++)
                    {
                        if (!Slab(o, d, p.gmin[g], p.gmax[g], R, out _, out _)) continue;
                        for (int i = g * Group; i < Math.Min(p.ch.Length, (g + 1) * Group); i++)
                        {
                            var k = p.ch[p.order[i]];
                            if (!Slab(o, d, k.amin, k.amax, R, out float ta, out float tb)) continue;
                            var ki = Inv(k.rot); var oc = Rot(ki, Sub(o, k.pos)); var dc = Rot(ki, d);
                            if (Touch(k, oc, dc, ta, tb, out float th)) hits.Add(new Hit { p = p, k = k, t = th, pp = pp, pq = pq });
                        }
                    }
                }

                int type = hits.Count == 0 ? 0 : 2;
                hits.Sort((a, b) => a.t.CompareTo(b.t));   // the game's answer does not depend on the order; nearest first only saves lookups
                foreach (var h in hits)
                {
                    if (h.k.solidRoot) { type = 1; break; }
                    var b = BlockOf(em, h.k.e);
                    if (!b.frame) { type = 1; break; }
                    if (b.mask == 0) continue;
                    if (((-1 << b.count) ^ b.mask) == -1) { type = 1; break; }
                    if (!b.lt) continue;
                    // pose check: the game takes the face test into the block with its LocalTransform, the collider uses CompoundFromChild
                    if (!b.poseChecked)
                    {
                        b.poseChecked = true;
                        float dp = Len(Sub(b.lp, h.k.pos)); float dq = Math.Abs(b.lq.x * h.k.rot.x + b.lq.y * h.k.rot.y + b.lq.z * h.k.rot.z + b.lq.w * h.k.rot.w);
                        if (dp > 0.002f || dq < 0.99985f) { mism++; if (mismEx == null) mismEx = NameOf(em, h.k.e, b) + " #" + h.k.e.Index + " (" + (dp * 1000f).ToString("F1") + " mm, " + (2.0 * Math.Acos(Math.Min(1f, dq)) * 57.29578).ToString("F2") + "°)"; }
                    }
                    var q = Mul(h.pq, b.lq); var bp = Add(h.pp, Rot(h.pq, b.lp)); var qi2 = Inv(q);
                    var ol = Rot(qi2, Sub(O, bp)); var dl = Rot(qi2, D);
                    bool solid = false;
                    for (int f = 0; f < b.count && !solid; f++) if ((b.mask >> f & 1) != 0 && LineFace(b.faces, f, ol, dl)) solid = true;
                    if (solid) { type = 1; break; }
                }
                cnt[type]++;
                if (type == 1) continue;
                // the cast got out: where (last frame it crossed, the face it left by) and how near it passed a closed face
                string key; float miss = 1e30f;
                if (hits.Count == 0) key = "nothing hit (open sky)";
                else
                {
                    Hit last = hits[0]; foreach (var h in hits) if (h.t > last.t) last = h;
                    var b = BlockOf(em, last.k.e); key = NameOf(em, last.k.e, b) + " #" + last.k.e.Index;
                    foreach (var h in hits)
                    {
                        var hb = BlockOf(em, h.k.e); if (!hb.frame || !hb.lt || hb.mask == 0) continue;
                        var q = Mul(h.pq, hb.lq); var qi2 = Inv(q); var ol = Rot(qi2, Sub(O, Add(h.pp, Rot(h.pq, hb.lp)))); var dl = Rot(qi2, D);
                        for (int f = 0; f < hb.count; f++) if ((hb.mask >> f & 1) != 0 && PlaneMiss(hb.faces, f, ol, dl, out float s, out float m, out _) && s > 0f && m < miss) miss = m;
                    }
                    if (b.frame && b.lt)
                    {
                        var q = Mul(last.pq, b.lq); var qi2 = Inv(q); var ol = Rot(qi2, Sub(O, Add(last.pp, Rot(last.pq, b.lp)))); var dl = Rot(qi2, D);
                        int fx = -1; float sx = -1e30f; V nx = default;
                        for (int f = 0; f < b.count; f++) if (PlaneMiss(b.faces, f, ol, dl, out float s, out float m, out V n) && m <= 0f && s > sx) { sx = s; fx = f; nx = n; }
                        if (fx >= 0) { var ns = Rot(b.lq, nx); key += " face " + fx + " (" + AxisName(ns) + ", " + ((b.mask >> fx & 1) != 0 ? "closed" : "open") + ")"; }
                        else key += " (passed by its edge)";
                    }
                    else key += " (not a frame?)";
                }
                if (!esc.TryGetValue(key, out var es)) { es = new Esc(); esc[key] = es; }
                es.n++; if (miss < es.miss) es.miss = miss;
            }

            // standing inside the box of a part but not in a room (on the hull, in a hangar): the game has not bound you and most casts get out
            if (Ecs.IsNull(bound) && cnt[1] + cnt[2] < Rays / 2) return;
            var sb = new StringBuilder();
            sb.Append("seal watch: ").Append(cnt[1]).Append(" blocked, ").Append(cnt[0]).Append(" open, ").Append(cnt[2]).Append(" gappy of ").Append(Rays).Append(" casts").Append(Game(vacuum, bound, tested));
            if (esc.Count > 0)
            {
                var top = new List<KeyValuePair<string, Esc>>(esc); top.Sort((a, b) => b.Value.n.CompareTo(a.Value.n));
                sb.Append(" — got out via: ");
                for (int i = 0; i < Math.Min(5, top.Count); i++)
                {
                    if (i > 0) sb.Append("; ");
                    sb.Append(top[i].Key).Append(" x").Append(top[i].Value.n);
                    if (top[i].Value.miss < 1e29f) sb.Append(", nearest closed face missed by ").Append((top[i].Value.miss * 1000f).ToString("F1")).Append(" mm");
                }
                if (top.Count > 5) sb.Append("; +").Append(top.Count - 5).Append(" more");
            }
            if (mism > 0) sb.Append(" | WARNING: ").Append(mism).Append(" frame(s) whose LocalTransform is not where their collider is (the game's face test is off for them), e.g. ").Append(mismEx);
            int uns = 0; foreach (var (p, _, _) in near) uns += p.unsupported; if (uns > 0) sb.Append(" | ").Append(uns).Append(" collider child(ren) of a kind this watch does not read");
            sb.Append(" | ").Append(sw.Elapsed.TotalMilliseconds.ToString("F1")).Append(" ms");
            string sig = cnt[0] + "/" + cnt[1] + "/" + cnt[2] + "|" + string.Join(",", esc.Keys) + "|" + mism;
            Emit(sb.ToString(), sig, cnt[0] + cnt[2] > 0);
        }

        static string Game(float vacuum, Entity bound, bool tested) =>
            " [game now: " + (vacuum >= 0f ? "vacuum " + vacuum.ToString("F2") + " (≈" + Math.Round(vacuum * vacuum * Rays) + "/" + Rays + " solid), " : "") + (Ecs.IsNull(bound) ? "not bound to a part" : "inside part " + bound.Index) + (tested ? "" : ", seated: the game does not test a seated player") + "]";

        static string AxisName(V n)
        {
            float ax = Math.Abs(n.x), ay = Math.Abs(n.y), az = Math.Abs(n.z);
            if (ax >= ay && ax >= az) return n.x >= 0 ? "+X" : "-X"; if (ay >= az) return n.y >= 0 ? "+Y" : "-Y"; return n.z >= 0 ? "+Z" : "-Z";
        }

        /// <summary>A leaking room: a line every 2 s (each run re-jitters the casts, so the line changes). Sealed: on change, else every 30 s.</summary>
        static void Emit(string line, string sig, bool leaking)
        {
            float now = UnityEngine.Time.unscaledTime;
            if (!leaking && sig == lastSig && now - tLog < 30f) return;
            lastSig = sig; tLog = now;
            ModLog.Info(line);
        }

        /// <summary>0.31.30 sound: SoundSeal reads parts through PartOf too; parts no longer alive leave the cache (SoundSeal, every 30 s).</summary>
        internal static void Prune(HashSet<long> alive) { if (partCache.Count == 0) return; var gone = new List<long>(); foreach (var key in partCache.Keys) if (!alive.Contains(key)) gone.Add(key); foreach (var key in gone) partCache.Remove(key); }

        public static void Forget() { partCache.Clear(); blocks.Clear(); lastSig = ""; tLog = -99f; t = 0f; }
    }
}
