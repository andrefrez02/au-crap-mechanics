using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;

namespace CrapMechanics
{
    /// <summary>0.31.28 seal 2 (Andre, 22/11: "a MicroFrame with its face closed still lets the air through, the game's frames seal").
    /// How the game decides it [revisao/seal2_microframe.md; lib_burst 21/11 0xC4D23-0xC5276]: a cast that touches a frame with some solid
    /// faces is blocked only if its CENTRE LINE goes through one of the solid face polygons (SCFramePrefabFacesData, block space), with a
    /// barycentric tolerance of 2 %. 2 % of the triangle = 1 cm past the edge of a 0.5 m game face, but only 2.5 mm past the edge of a 0.125 m
    /// MicroFrame face. A gap between two closed faces (a gate on its own part, the joint sagging a few mm) is closed by the game up to
    /// 2 cm between game frames and only up to 5 mm between MicroFrames: the air goes through the seam.
    ///
    /// What this file does ([Air] frame_seam_like_game, OFF by default since the review — see below): for frame blocks IN FLIGHT whose faces give less than 1 cm, the block's
    /// SCTypeFrame._facesDataPtr is pointed to a copy of its prefab's face table where each edge of each face is pushed out in its own plane
    /// just enough that the game's own 2 % reaches 1 cm past the real edge — the same as a 0.5 m game face. Nothing else changes: same faces,
    /// same open/solid bits, same meshes, same mass fractions; the game's test is untouched; open faces still leak (an open face next to a
    /// closed one loses 1 cm at that edge, exactly as next to a closed game face). Game frames (0.5 m) are left alone (their faces already
    /// give 1 cm). The garage keeps the original table (face picking and cables there use it). The copies are never freed.
    /// It also checks each face table it sees (count, 3/4 vertices, flat quads in perimeter order) and logs it; a bow-tie quad (vertex order
    /// that makes the game's split leave a hole) is put back in perimeter order in the copy.
    ///
    /// Review (22/11): off by default. This is NOT a bug fix: the MicroFrames' face data is right and the 2 % is the game's own rule. Growing
    /// the faces makes a 5-20 mm open seam between small closed faces count as closed, which the game's rule does not do for them (it only
    /// matches what the game does for its 0.5 m frames). Andre's rule is "never change the game's rule by the back door", so it is his call:
    /// he turns it on if he thinks parity with the game's frames is fair. The honest ways that need nothing from this file: close every face
    /// of the gate's frames (a fully closed frame is a solid block) or use the Rubber Seal (a solid block: the game's 3.5 cm ball applies).
    /// Review: the full pass is also triggered by a change in the number of parts or in the launched block list (Ecs.Launched stamp): a
    /// launch can replace the garage frames by the same number of flight frames, and the frame count alone would not see it for 30 s.</summary>
    internal static class FrameFaces
    {
        public static bool SeamLikeGame = true;  // [Air] frame_seam_like_game (0.31.29: on by default — Andre: the closed MicroFrame face leaking is a bug to fix)
        public const float Allow = 0.01f;          // m past each edge (2 % of a 0.5 m game face)
        public const float Tol = 0.02f;            // the game's barycentric tolerance (lib_burst 0xC50B3 / 0xC50C2)
        public const int FaceStride = 64, OffCount = 512, OffMeshIds = 516, TableSize = 1548;   // SCFramePrefabFacesData [interop FieldOffset]

        class Copy { public IntPtr orig, mine; public byte[] origHead; public string name; public int faces, grown, repaired; public float minBefore, growMax; public bool logged; }
        static readonly Dictionary<long, Copy> byOrig = new Dictionary<long, Copy>();
        static readonly Dictionary<long, Copy> byMine = new Dictionary<long, Copy>();
        static TypeIndex tiFrame; static EntityQuery qFrames; static bool ok, failed;
        static readonly Dictionary<long, bool> partOf = new Dictionary<long, bool>();
        static int lastCount = -1, lastParts = -1, lastStamp = -1; static float t = 99f, tFull = 0f; static bool lastSetting; static int warnings, keptNew;

        public static bool Types(EntityManager em)
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiFrame = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeFrame>());
                var a = new Il2CppStructArray<ComponentType>(2); a[0] = ComponentType.ReadOnly(tiFrame); a[1] = ComponentType.ReadOnly(Ecs.tiParent);
                qFrames = em.CreateEntityQuery(a);
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("frame faces: SCTypeFrame not available (" + ex.Message + ") — off"); return false; }
        }
        public static TypeIndex TiFrame => tiFrame;

        /// <summary>Frame cycle, every 1 s: a full pass only when the number of frame blocks in flight changes, the setting changes, or every 30 s.</summary>
        public static unsafe void Step(EntityManager em)
        {
            t += UnityEngine.Time.unscaledDeltaTime; if (t < 1f) return; tFull += t; t = 0f;
            if (Unloading.Active || !Ecs.Prepare(em) || !Types(em)) return;
            int n; try { n = qFrames.CalculateEntityCount(); } catch (Exception) { return; }
            int np = -1, st = -1; try { np = Ecs.Parts(em).Count; st = Ecs.Launched(em).stamp; } catch (Exception) { }   // review: a launch can keep the frame count
            if (n == lastCount && np == lastParts && st == lastStamp && SeamLikeGame == lastSetting && tFull < 30f) return;
            if (!SeamLikeGame && !lastSetting && byMine.Count == 0) { lastCount = n; lastParts = np; lastStamp = st; tFull = 0f; return; }   // off and never on: nothing to put back
            lastCount = n; lastParts = np; lastStamp = st; lastSetting = SeamLikeGame; tFull = 0f;
            int swapped = 0, back = 0, flight = 0; var newNames = new List<string>();
            partOf.Clear();
            foreach (var e in Ecs.QueryList(qFrames))
            {
                try
                {
                    var par = (Entity*)Ecs.RO(em, e, Ecs.tiParent); if (par == null) continue;
                    long pk = Ecs.K(*par);
                    if (!partOf.TryGetValue(pk, out bool isPart)) { isPart = Ecs.IsPart(em, *par); partOf[pk] = isPart; }
                    if (!isPart) continue;   // garage / previews: keep the game's table
                    flight++;
                    var f = (byte*)Ecs.RO(em, e, tiFrame); if (f == null) continue;
                    IntPtr cur = *(IntPtr*)f;
                    if (!GameFields.Plausible((void*)cur)) continue;
                    if (byMine.TryGetValue((long)cur, out var mineC))
                    {
                        if (!SeamLikeGame) { *(IntPtr*)f = mineC.orig; back++; }   // aligned 8-byte write: a job reading it sees the old or the new table, both valid
                        continue;
                    }
                    if (!SeamLikeGame) continue;
                    var c = CopyOf(em, e, cur);
                    if (c == null || c.mine == IntPtr.Zero) continue;
                    if (!c.logged) { c.logged = true; newNames.Add(Describe(c)); }
                    *(IntPtr*)f = c.mine; swapped++;   // through the RO pointer on purpose: no change version, no system reacts to it
                }
                catch (Exception ex) { if (warnings++ < 5) ModLog.Warn("frame faces: block " + e.Index + ": " + ex.Message); }
            }
            foreach (var s in newNames) ModLog.Info("frame faces: " + s);
            if (swapped > 0 || back > 0)
                ModLog.Info("frame faces: " + flight + " frame block(s) in flight; " + (swapped > 0 ? swapped + " now use the 1 cm face table (like a 0.5 m game face)" : "") + (back > 0 ? back + " back to the game's table (frame_seam_like_game off)" : "")
                            + (keptNew > 0 ? "; " + keptNew + " face table(s) left as the game has them (game frames, or MicroFrames that already close 1 cm)" : ""));
            keptNew = 0;
        }

        static string Describe(Copy c) => c.name + ": " + c.faces + " face(s), smallest edge allowance " + (c.minBefore * 1000f).ToString("F1") + " mm -> faces grown up to "
                                           + (c.growMax * 1000f).ToString("F1") + " mm in their plane (" + c.grown + " face(s)), every edge now closes " + (Allow * 1000f).ToString("F0") + " mm like a 0.5 m game frame"
                                           + (c.repaired > 0 ? "; " + c.repaired + " bow-tie quad(s) put back in perimeter order" : "");

        /// <summary>The copy for this original table (built once; rebuilt if the memory at that address no longer holds what was copied —
        /// a new game session can reuse the address for another prefab; review: the whole table is compared, mesh ids and sound bytes too). mine = Zero when nothing needs to change (0.5 m game frames).</summary>
        static unsafe Copy CopyOf(EntityManager em, Entity e, IntPtr orig)
        {
            var head = (byte*)orig;
            if (byOrig.TryGetValue((long)orig, out var c))
            {
                bool same = c.origHead.Length == TableSize; for (int i = 0; same && i < TableSize; i++) if (head[i] != c.origHead[i]) same = false;
                if (same) return c;
                byOrig.Remove((long)orig);   // (the old copy stays alive and mapped: blocks may still point to it)
            }
            int count = *(int*)(head + OffCount);
            if (count < 1 || count > 8) { if (warnings++ < 5) ModLog.Warn("frame faces: block " + e.Index + ": face table with " + count + " face(s) — left alone"); return null; }
            c = new Copy { orig = orig, origHead = new byte[TableSize], faces = count, minBefore = float.MaxValue };
            for (int i = 0; i < TableSize; i++) c.origHead[i] = head[i];
            try { c.name = BlockIndex.NameOfHash(Ecs.Prefab(em, e)) ?? ("prefab of block " + e.Index); } catch (Exception) { c.name = "prefab of block " + e.Index; }
            // 0.31.30 (Andre: "limita só pra microframe"): only the MicroFrames' blocks (SC_MF...) are grown; the game's own frames, half and quarter
            // ones included, keep the game's table. A block whose name is not known counts as a MicroFrame only if its faces are MicroFrame-small
            // (every game frame gives 7 mm or more; the MicroFrames 2.5 mm or less).
            bool named = c.name != null && !c.name.StartsWith("prefab of block");
            bool micro = named ? c.name.StartsWith("SC_MF") : SmallestAllowance(head, count) < MicroBelow;
            var faces = new float3[count][]; bool change = false;
            for (int i = 0; i < count; i++)
            {
                var fd = (float*)(head + FaceStride * i); int nv = *(int*)(head + FaceStride * i + 48);
                if (nv != 3 && nv != 4) { faces[i] = null; continue; }
                var v = new float3[nv]; for (int k = 0; k < nv; k++) v[k] = new float3 { x = fd[3 * k], y = fd[3 * k + 1], z = fd[3 * k + 2] };
                if (nv == 4 && BowTie(v)) { var tmp = v[2]; v[2] = v[3]; v[3] = tmp; c.repaired++; change = true; }
                float before = MinAllowance(v); if (before < c.minBefore) c.minBefore = before;
                if (SeamLikeGame && micro && before < Allow - 1e-4f)
                {
                    var g = Grow(v); if (g != null) { float m = 0f; for (int k = 0; k < nv; k++) m = System.Math.Max(m, Ecs.Len(Ecs.Sub(g[k], v[k]))); if (m > c.growMax) c.growMax = m; v = g; c.grown++; change = true; }
                }
                faces[i] = v;
            }
            if (change)
            {
                var mine = Marshal.AllocHGlobal(TableSize);
                Buffer.MemoryCopy((void*)orig, (void*)mine, TableSize, TableSize);
                var dst = (byte*)mine;
                for (int i = 0; i < count; i++)
                {
                    if (faces[i] == null) continue; var fd = (float*)(dst + FaceStride * i);
                    for (int k = 0; k < faces[i].Length; k++) { fd[3 * k] = faces[i][k].x; fd[3 * k + 1] = faces[i][k].y; fd[3 * k + 2] = faces[i][k].z; }
                }
                c.mine = mine; byMine[(long)mine] = c;
            }
            else keptNew++;
            byOrig[(long)orig] = c;
            return c;
        }

        const float MicroBelow = 0.004f;   // m: smallest edge allowance under this = MicroFrame-sized faces (game frames: 7.1 mm or more)
        static unsafe float SmallestAllowance(byte* head, int count)
        {
            float m = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var fd = (float*)(head + FaceStride * i); int nv = *(int*)(head + FaceStride * i + 48);
                if (nv != 3 && nv != 4) continue;
                var v = new float3[nv]; for (int k = 0; k < nv; k++) v[k] = new float3 { x = fd[3 * k], y = fd[3 * k + 1], z = fd[3 * k + 2] };
                if (nv == 4 && BowTie(v)) { var tmp = v[2]; v[2] = v[3]; v[3] = tmp; }
                m = System.Math.Min(m, MinAllowance(v));
            }
            return m;
        }

        // ---------------------------------------------------------------- geometry (the game's split: (v0,v1,v2) + (v0,v2,v3))
        /// <summary>For each outer edge (a, b) the vertex opposite to it in the game's triangle.</summary>
        static readonly int[][] Tri = { new[] { 0, 1, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 } };
        static readonly int[][] Quad = { new[] { 0, 1, 2 }, new[] { 1, 2, 0 }, new[] { 2, 3, 0 }, new[] { 3, 0, 2 } };
        static int[][] Edges(int nv) => nv == 3 ? Tri : Quad;

        /// <summary>How far past each outer edge (m) the game's 2 % reaches = 0.02 x height of the opposite vertex over that edge.</summary>
        public static float MinAllowance(float3[] v)
        {
            float m = float.MaxValue;
            foreach (var eo in Edges(v.Length))
            {
                var e = Ecs.Sub(v[eo[1]], v[eo[0]]); var w = Ecs.Sub(v[eo[2]], v[eo[0]]);
                float le = Ecs.Len(e); if (le < 1e-6f) continue;
                m = System.Math.Min(m, Tol * Ecs.Len(Ecs.Cross(e, w)) / le);
            }
            return m;
        }

        /// <summary>Quad whose diagonals v0-v2 and v1-v3 do not cross (vertex order 0,1,3,2): the game's split covers it with two triangles that
        /// overlap and leave a hole.</summary>
        static bool BowTie(float3[] v)
        {
            var n = Ecs.Cross(Ecs.Sub(v[1], v[0]), Ecs.Sub(v[2], v[0]));
            var n2 = Ecs.Cross(Ecs.Sub(v[2], v[0]), Ecs.Sub(v[3], v[0]));
            return Ecs.Dot(n, n2) < 0f;
        }

        /// <summary>Pushes each outer edge out in the face's plane by e_i so that e_i + 0.02 x (new height) = Allow; the vertices are where the
        /// moved edge lines meet. A few fixed-point rounds (the height grows with the push). null if the face is degenerate.</summary>
        public static float3[] Grow(float3[] v)
        {
            int nv = v.Length; var E = Edges(nv);
            var n = Ecs.Cross(Ecs.Sub(v[1], v[0]), Ecs.Sub(v[2], v[0])); float ln = Ecs.Len(n); if (ln < 1e-8f) return null; n = Ecs.Div(n, ln);
            float3 c = default; for (int k = 0; k < nv; k++) c = Ecs.Add(c, v[k]); c = Ecs.Div(c, nv);
            var ux = Ecs.Norm(Ecs.Sub(v[1], v[0])); var uy = Ecs.Cross(n, ux);
            var px = new double[nv]; var py = new double[nv];
            for (int k = 0; k < nv; k++) { var d = Ecs.Sub(v[k], c); px[k] = Ecs.Dot(d, ux); py[k] = Ecs.Dot(d, uy); }
            var off = new double[nv]; var qx = (double[])px.Clone(); var qy = (double[])py.Clone();
            for (int it = 0; it < 6; it++)
            {
                // edge i goes from vertex i to i+1 (E[i][0] == i); outward normal in 2D
                var mx = new double[nv]; var my = new double[nv]; var md = new double[nv];
                for (int i = 0; i < nv; i++)
                {
                    int a = i, b = (i + 1) % nv; double dx = px[b] - px[a], dy = py[b] - py[a], l = Math.Sqrt(dx * dx + dy * dy); if (l < 1e-9) return null;
                    double nx = dy / l, ny = -dx / l; if (nx * px[a] + ny * py[a] < 0) { nx = -nx; ny = -ny; }
                    mx[i] = nx; my[i] = ny; md[i] = nx * px[a] + ny * py[a] + off[i];
                }
                for (int k = 0; k < nv; k++)
                {
                    int i1 = (k - 1 + nv) % nv, i2 = k; double det = mx[i1] * my[i2] - my[i1] * mx[i2]; if (Math.Abs(det) < 1e-12) return null;
                    qx[k] = (md[i1] * my[i2] - my[i1] * md[i2]) / det; qy[k] = (mx[i1] * md[i2] - md[i1] * mx[i2]) / det;
                }
                for (int i = 0; i < nv; i++)
                {
                    int a = E[i][0], b = E[i][1], o = E[i][2];
                    double ex = qx[b] - qx[a], ey = qy[b] - qy[a], wx = qx[o] - qx[a], wy = qy[o] - qy[a], le = Math.Sqrt(ex * ex + ey * ey);
                    double h = Math.Abs(ex * wy - ey * wx) / le;
                    off[i] = Math.Max(0.0, Allow - Tol * h);
                }
            }
            var r = new float3[nv];
            for (int k = 0; k < nv; k++) r[k] = Ecs.Add(c, Ecs.Add(Ecs.Mul(ux, (float)qx[k]), Ecs.Mul(uy, (float)qy[k])));
            return r;
        }

        public static void Forget() { partOf.Clear(); lastCount = -1; lastParts = -1; lastStamp = -1; t = 99f; tFull = 0f; }
    }
}
