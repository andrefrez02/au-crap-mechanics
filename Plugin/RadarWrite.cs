using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace CrapMechanics
{
    /// <summary>0.19.2 (Andre: the beacon never showed up on the radar — the diagnostics show the ghost with icon 8 and the screen's buffer with only
    /// the base): instead of waiting for the game's FindIconsJob to pick up the ghost, the mod writes each beacon's icon STRAIGHT into the buffer the
    /// screen draws (SCTick_Radar.RadarLocationGPUData: _uv float2, _type uint, _isTarget float; 16 B), as the job does:
    /// uv = normalizesafe((worldToProjection x pos).xy). The matrices come from the radar's singleton (SCTick_Radar.Singleton: _viewMatrix +8,
    /// _projectionMatrix +72). The buffer is rebuilt by the game on every physics tick, so the write happens every frame, after the
    /// TransformSystemGroup (before rendering). The formula is checked once against the base icon (type 0) the game wrote: the variant
    /// that matches is the one used (world w=1, world w=0, relative to the planet).</summary>
    internal static class RadarWrite
    {
        static int T => Layout.GpuSize;   // RadarLocationGPUData stride
        static TypeIndex tiGPU, tiSing, tiStation; static EntityQuery qGPU, qSing, qStation; static bool ok, failed;
        static int variant = -1, frame = -1, writtenSet, logs, attempts; static float tLog = 9f; static string latest = "";

        static bool Prepare(EntityManager em)
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiGPU = TypeManager.GetTypeIndex(Il2CppType.Of<SCTick_Radar.RadarLocationGPUData>());
                tiSing = TypeManager.GetTypeIndex(Il2CppType.Of<SCTick_Radar.Singleton>());
                var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadWrite(tiGPU); qGPU = em.CreateEntityQuery(a);
                var b = new Il2CppStructArray<ComponentType>(1); b[0] = ComponentType.ReadOnly(tiSing); qSing = em.CreateEntityQuery(b);
                try
                {
                    tiStation = TypeManager.GetTypeIndex(Il2CppType.Of<PlanetStationData>());
                    var c = new Il2CppStructArray<ComponentType>(2); c[0] = ComponentType.ReadOnly(tiStation); c[1] = ComponentType.ReadOnly(Ecs.tiLTW); qStation = em.CreateEntityQuery(c);
                }
                catch (Exception ex) { ModLog.Warn("radar/write: PlanetStationData not available (" + ex.Message + ") — the uv formula will not be checked"); }
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("radar/write: not available (" + ex.Message + ")"); return false; }
        }

        /// <summary>Buffer header (BufferHeader in the chunk: Pointer +0, Length +8, Capacity +12). GetComponentDataRawRW on a buffer
        /// type returns the header (IL2CPP without checks doesn't complain).</summary>
        static unsafe byte* Header(EntityManager em, Entity e) => (byte*)em.GetComponentDataRawRW(e, tiGPU);
        static unsafe byte* Elements(byte* h) { var p = *(byte**)h; return p != null ? p : h + 16; }
        /// <summary>Ensures capacity for n elements (like BufferHeader.EnsureCapacity: a new Persistent block, copy, free the old one).</summary>
        static unsafe bool Ensure(byte* h, int n)
        {
            int cap = *(int*)(h + 12); if (n <= cap) return true;
            if (cap < 0 || cap > 100000) return false;
            int newCap = Math.Max(n, cap * 2);
            var newVal = (byte*)UnsafeUtility.Malloc((long)newCap * T, 16, Allocator.Persistent); if (newVal == null) return false;
            int len = *(int*)(h + 8); var old = Elements(h);
            for (int i = 0; i < len * T; i++) newVal[i] = old[i];
            var heap = *(byte**)h; if (heap != null) UnsafeUtility.Free(heap, Allocator.Persistent);
            *(byte**)h = newVal; *(int*)(h + 12) = newCap;
            return true;
        }

        // 0.19.3 (0.19.2 log: "hits #0=0 #1=0 #2=0" — I ALWAYS normalized, and the base icon sits near the center of the screen, not at the
        // edge): the uv is the projection itself; only what falls outside the circle (length > 1) is brought to the edge. Variants: #0 direct
        // projection (orthographic), #1 with division by w (perspective), #2 relative to the planet.
        static unsafe float2 Uv(float4x4 w2p, float3 pos, float3 planet, int v)
        {
            float4 p = v == 2 ? new float4(pos - planet, 1f) : new float4(pos, 1f);
            var r = math.mul(w2p, p);
            var uv = new float2(r.x, r.y);
            if (v == 1 && Math.Abs(r.w) > 1e-6f) uv /= r.w;
            float c = math.length(uv);
            return c > 1f ? uv / c : uv;
        }

        // 0.19.3 (Andre: "every simulation it leaves an old icon on the radar"): when SCTick_Radar stops rebuilding the buffer (end of the
        // simulation), whatever I added stayed there. I keep the bytes I wrote and where; if on the next frame the buffer is still
        // exactly like that (same length, same bytes = the game didn't rebuild it), I shorten it back before writing again (or not writing).
        static Entity ownerWritten; static int nBefore, kAfter; static byte[] written;
        static unsafe void Clean(EntityManager em)
        {
            if (written == null) return;
            try
            {
                if (Ecs.Alive(em, ownerWritten) && Ecs.HasC(em, ownerWritten, tiGPU))
                {
                    var h = Header(em, ownerWritten);
                    if (h != null && *(int*)(h + 8) == kAfter)
                    {
                        var el = Elements(h); bool equal = true;
                        for (int i = 0; i < written.Length && equal; i++) if (el[nBefore * T + i] != written[i]) equal = false;
                        if (equal) *(int*)(h + 8) = nBefore;
                    }
                }
            }
            catch (Exception) { }
            written = null;
        }
        public static void Forget() { written = null; }

        /// <summary>Writes the beacons' icons (list of (world position, icon)) into the radar buffer. Call once per frame, after the poses
        /// (also with an empty list: then it only removes what was left from the previous frame).</summary>
        public static unsafe void Write(EntityManager em, List<(float3 pos, uint icon)> icons)
        {
            if (!Prepare(em)) return;
            if (frame == UnityEngine.Time.frameCount) return; frame = UnityEngine.Time.frameCount;
            Clean(em);
            if (icons.Count == 0) return;
            try
            {
                var sings = Ecs.QueryList(qSing); if (sings.Count == 0) return;
                var sp = (byte*)em.GetComponentDataRawRO(sings[0], tiSing); if (sp == null) return;
                if (sp[Layout.RadarAny] == 0) return;   // _anyActiveRadar
                var view = *(float4x4*)(sp + Layout.RadarView); var proj = *(float4x4*)(sp + Layout.RadarProj);
                var w2p = math.mul(proj, view);
                var owners = Ecs.QueryList(qGPU); if (owners.Count == 0) return;
                var h = Header(em, owners[0]); if (h == null) return;
                int n = *(int*)(h + 8); if (n < 0 || n > 4096) return;
                var el = Elements(h);
                // once: which variant of the formula reproduces the base icon (type 0) the game wrote
                if (variant < 0) variant = Check(em, w2p, view, proj, el, n);
                int v = variant < 0 ? 0 : variant;
                float3 planet = default;   // (only variant 2 uses it; without FrameData, planet = origin)
                // no duplicates: if the game already placed an icon with our type (ghost picked up by the job), skip that one
                var kindsInBuffer = new HashSet<uint>(); for (int i = 0; i < n; i++) kindsInBuffer.Add(*(uint*)(el + i * T + Layout.GpuType));
                int add = 0;
                foreach (var (pos, icon) in icons) if (!kindsInBuffer.Contains(icon)) add++;
                if (add == 0) return;
                if (!Ensure(h, n + add)) { if (logs++ < 3) ModLog.Warn("radar/write: could not grow the radar buffer (" + n + " + " + add + ")"); return; }
                el = Elements(h); int k = n;
                foreach (var (pos, icon) in icons)
                {
                    if (kindsInBuffer.Contains(icon)) continue;
                    var uv = Uv(w2p, pos, planet, v);
                    var q = el + k * T; for (int b = 0; b < T; b++) q[b] = 0;
                    *(float2*)(q + Layout.GpuUv) = uv; *(uint*)(q + Layout.GpuType) = icon; *(float*)(q + Layout.GpuTarget) = 0f; k++;
                }
                *(int*)(h + 8) = k; writtenSet++;
                ownerWritten = owners[0]; nBefore = n; kAfter = k; written = new byte[(k - n) * T];
                for (int i = 0; i < written.Length; i++) written[i] = el[n * T + i];
                tLog += UnityEngine.Time.deltaTime;
                string s = "radar/write: " + (k - n) + " beacon icon(s) added to the radar buffer (" + n + " from the game), formula " + (variant < 0 ? "unchecked" : "#" + variant);
                if (s != latest && logs++ < 12) { latest = s; ModLog.Info(s); }
            }
            catch (Exception ex) { failed = true; ModLog.Warn("radar/write: " + ex.Message + " — turning it off"); }
        }

        /// <summary>Compares the 3 variants with the game's type 0 icons (stations): returns the one that matches (or -1 if it can't be checked yet).</summary>
        static unsafe int Check(EntityManager em, float4x4 w2p, float4x4 view, float4x4 proj, byte* el, int n)
        {
            try
            {
                if (qStation.Equals(default(EntityQuery)) || tiStation.Value == 0) return 0;
                var targets = new List<float2>();
                for (int i = 0; i < n; i++) if (*(uint*)(el + i * T + Layout.GpuType) == 0) targets.Add(*(float2*)(el + i * T + Layout.GpuUv));
                if (targets.Count == 0) return -1;
                var ests = Ecs.QueryList(qStation); if (ests.Count == 0) return -1;
                int[] hits = new int[3]; float[] error = new float[3];
                Entity near = Entity.Null; float dNear = float.MaxValue; var eye = UnityEngine.Camera.main != null ? (float3)UnityEngine.Camera.main.transform.position : default;
                foreach (var e in ests)
                {
                    var pos = Ecs.Position(em, e);
                    float d = math.length(pos - eye); if (d < dNear) { dNear = d; near = e; }
                    for (int v = 0; v < 3; v++)
                    {
                        var uv = Uv(w2p, pos, default, v);
                        float bestPick = 9f; foreach (var a in targets) bestPick = Math.Min(bestPick, math.length(a - uv));
                        if (bestPick < 0.05f) hits[v]++; error[v] += bestPick;
                    }
                }
                int best = 0; for (int v = 1; v < 3; v++) if (hits[v] > hits[best] || (hits[v] == hits[best] && error[v] < error[best])) best = v;
                var sb = new System.Text.StringBuilder();
                sb.Append("radar/write: uv formula check against ").Append(targets.Count).Append(" station icon(s) and ").Append(ests.Count).Append(" station(s): hits #0=").Append(hits[0]).Append(" #1=").Append(hits[1]).Append(" #2=").Append(hits[2]).Append(" -> using #").Append(best).Append(hits[best] == 0 ? " (NO match — icons may be misplaced; send the log)" : "");
                sb.Append("\n  station icons in the buffer:"); foreach (var a in targets) sb.Append(" (").Append(a.x.ToString("F3")).Append(',').Append(a.y.ToString("F3")).Append(')');
                if (!Ecs.IsNull(near))
                {
                    var pos = Ecs.Position(em, near); var r = math.mul(w2p, new float4(pos, 1f));
                    sb.Append("\n  nearest station ").Append(near.Index).Append(" at ").Append(Ecs.F3(pos)).Append(" (").Append(dNear.ToString("F0")).Append(" m from the camera): raw projection (").Append(r.x.ToString("F3")).Append(',').Append(r.y.ToString("F3")).Append(',').Append(r.z.ToString("F3")).Append(',').Append(r.w.ToString("F3")).Append(')');
                    for (int v = 0; v < 3; v++) { var uv = Uv(w2p, pos, default, v); sb.Append(" #").Append(v).Append("=(").Append(uv.x.ToString("F3")).Append(',').Append(uv.y.ToString("F3")).Append(')'); }
                }
                sb.Append("\n  view row0 ").Append(Ecs.F3(view.c0.xyz)).Append(" c3 ").Append(Ecs.F3(view.c3.xyz)).Append("; proj c0 ").Append(Ecs.F3(proj.c0.xyz)).Append(" c1 ").Append(Ecs.F3(proj.c1.xyz)).Append(" c3 ").Append(Ecs.F3(proj.c3.xyz));
                ModLog.Info(sb.ToString());
                if (hits[best] == 0) return ++attempts >= 4 ? 0 : -1;   // try again on the next frames (up to 4x)
                return best;
            }
            catch (Exception ex) { ModLog.Warn("radar/write: formula check: " + ex.Message); return 0; }
        }
    }
}
