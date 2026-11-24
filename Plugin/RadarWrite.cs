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
    /// TransformSystemGroup (before rendering).
    /// 0.31.26 (item 20): the position is the game's own formula (FindIconsJob / FindPlanetStationsJob [recon/EPC_SCRadar.cs:315-329, 352-363]):
    /// rel = pos - planet, p = W2P * (rel / R, 1), uv = p.xy / p.w, pinned to normalizesafe(uv) * 0.9 on the planet's far side, stored as
    /// uv * 0.5 + 0.5. Planet = the radar's closest planet (Singleton._closestPlanet = UniverseLocationData._id, radius from
    /// UniverseEntityRadius, centre from its LocalToWorld) [recon/EPC_SCRadar.cs:195-209]. None of the three old variants had the planet,
    /// the /R or the *0.5+0.5, so the old check never matched and always fell back to #0 (the icon landed nowhere in particular); the check
    /// and the variants are gone. The game only shows objects within 1.05 R; beyond that the mod keeps the beacon on the rim (0.9), the way
    /// the game shows the far hemisphere. No planet found: nothing is written (as the game). Duplicates are told by position now (the
    /// game's own icon of the ghost lands on the same uv), not by type — a beacon set to a game icon no longer hides behind a real one.</summary>
    internal static class RadarWrite
    {
        const int T = 16;
        static TypeIndex tiGPU, tiSing, tiLoc, tiRadius; static EntityQuery qGPU, qSing, qPlanet; static bool ok, failed, planetOk;
        static int frame = -1, writtenSet, logs; static float tLog = 9f; static string latest = "";
        static uint planetId = uint.MaxValue; static Entity planetE; static int noPlanetLogs;

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
                    tiLoc = TypeManager.GetTypeIndex(Il2CppType.Of<UniverseLocationData>());
                    tiRadius = TypeManager.GetTypeIndex(Il2CppType.Of<UniverseEntityRadius>());
                    var c = new Il2CppStructArray<ComponentType>(3); c[0] = ComponentType.ReadOnly(tiLoc); c[1] = ComponentType.ReadOnly(tiRadius); c[2] = ComponentType.ReadOnly(Ecs.tiLTW);
                    qPlanet = em.CreateEntityQuery(c); planetOk = true;
                }
                catch (Exception ex) { ModLog.Warn("radar/write: planet types not available (" + ex.Message + ") — the beacon will not be drawn by the mod (the game still draws it near a planet)"); }
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

        /// <summary>0.31.26 (item 20): the game's formula [recon/EPC_SCRadar.cs:315-329, 352-363]. `dir` = direction from the ship (floating
        /// origin) to the planet, as FindClosestPlanetJob stores it (ltw.Position / dist). Beyond 1.05 R (where the game shows nothing) the
        /// beacon is pinned on the rim like an object on the far hemisphere.</summary>
        static float2 Uv(float4x4 w2p, float3 pos, float3 planet, float radius, float include)
        {
            float3 rel = pos - planet;
            float dist = math.length(planet); float3 dir = dist > 1e-6f ? planet / dist : default;
            var p = math.mul(w2p, new float4(rel / radius, 1f));
            float2 uv = Math.Abs(p.w) > 1e-12f ? new float2(p.x, p.y) / p.w : new float2(p.x, p.y);
            if (math.dot(dir, rel) > 0f || math.lengthsq(rel) >= include * include || Math.Abs(p.w) <= 1e-12f) uv = NormSafe(uv) * 0.9f;
            return uv * 0.5f + 0.5f;
        }
        static float2 NormSafe(float2 v) { float l = math.length(v); return l > 1e-12f ? v / l : default; }
        /// <summary>The radar's closest planet (centre, radius, inclusion radius) — the entity whose UniverseLocationData._id is
        /// Singleton._closestPlanet; the black hole (id 999) uses 1.01 R, the planets 1.05 R [recon/EPC_SCRadar.cs:208, 237].</summary>
        static unsafe bool Planet(EntityManager em, uint id, out float3 centre, out float radius, out float include)
        {
            centre = default; radius = 0f; include = 0f;
            if (!planetOk) return false;
            if (id != planetId || !Ecs.Alive(em, planetE) || !Ecs.HasC(em, planetE, tiRadius))
            {
                planetId = id; planetE = Entity.Null;
                foreach (var e in Ecs.QueryList(qPlanet))
                {
                    var l = (uint*)Ecs.RO(em, e, tiLoc);
                    if (l != null && *l == id) { planetE = e; break; }
                }
            }
            if (Ecs.IsNull(planetE)) return false;
            radius = *(float*)Ecs.RO(em, planetE, tiRadius); if (!(radius > 0f)) return false;
            centre = Ecs.Position(em, planetE);
            include = radius * (id == 999 ? 1.01f : 1.05f);
            return true;
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
                var sp = (byte*)Ecs.RO(em, sings[0], tiSing); if (sp == null) return;
                if (sp[153] == 0) return;   // _anyActiveRadar
                var view = *(float4x4*)(sp + 8); var proj = *(float4x4*)(sp + 72);
                var w2p = math.mul(proj, view);   // = FrameData._worldToProjectionMatrix [recon_managed/SCTick_Radar.cs:118-123]
                uint closest = *(uint*)sp;        // Singleton._closestPlanet (+0)
                if (!Planet(em, closest, out var planet, out float radius, out float include))
                {
                    if (noPlanetLogs++ < 3) ModLog.Info("radar/write: the radar's closest planet (id " + closest + ") was not found — beacon icon not drawn by the mod (as the game draws nothing without a planet)");
                    return;
                }
                var owners = Ecs.QueryList(qGPU); if (owners.Count == 0) return;
                var h = Header(em, owners[0]); if (h == null) return;
                int n = *(int*)(h + 8); if (n < 0 || n > 4096) return;
                var el = Elements(h);
                // no duplicates: an icon of the same type already at (almost) the same place is the game's own icon of the ghost (FindIconsJob
                // picks it up within 1.05 R) — skip that beacon
                var uvs = new List<(float2 uv, uint icon)>(icons.Count);
                foreach (var (pos, icon) in icons)
                {
                    var uv = Uv(w2p, pos, planet, radius, include); bool dup = false;
                    for (int i = 0; i < n && !dup; i++) if (*(uint*)(el + i * T + 8) == icon && math.lengthsq(*(float2*)(el + i * T) - uv) < 0.0004f) dup = true;
                    if (!dup) uvs.Add((uv, icon));
                }
                int add = uvs.Count;
                if (add == 0) return;
                if (!Ensure(h, n + add)) { if (logs++ < 3) ModLog.Warn("radar/write: could not grow the radar buffer (" + n + " + " + add + ")"); return; }
                el = Elements(h); int k = n;
                foreach (var (uv, icon) in uvs)
                {
                    var q = el + k * T; *(float2*)q = uv; *(uint*)(q + 8) = icon; *(float*)(q + 12) = 0f; k++;
                }
                *(int*)(h + 8) = k; writtenSet++;
                ownerWritten = owners[0]; nBefore = n; kAfter = k; written = new byte[(k - n) * T];
                for (int i = 0; i < written.Length; i++) written[i] = el[n * T + i];
                tLog += UnityEngine.Time.deltaTime;
                string s = "radar/write: " + (k - n) + " beacon icon(s) added to the radar buffer (" + n + " from the game), relative to planet " + closest;
                if (s != latest && logs++ < 12) { latest = s; ModLog.Info(s); }
            }
            catch (Exception ex) { failed = true; ModLog.Warn("radar/write: " + ex.Message + " — turning it off"); }
        }
    }
}
