using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>Extra cables per unlocked motor (cumulative). Later this becomes a mission; for now the condition is "the motor is
    /// available in the inventory" (Core.Singleton.GetMaxAvailableComponents > 0).
    ///   Small Motor:  +100 Data Cable, +100 Power Cable
    ///   Medium Motor: +250 Data Cable, +100 Power Cable
    ///   Large Motor:  +250 Data Cable, +100 Power Cable
    /// How: Core.Singleton._maxComponentsAvailability (+104) = UnsafeHashMap&lt;SCPrefab,int&gt;* (the map the inventory
    /// queries). I add the bonus on top of the game's value; if the game recalculates the map (mission completed, load), the value
    /// changes and I add it again on top of the new value.</summary>
    internal static class Bonus
    {
        static readonly (string motor, int data, int power)[] RULES = { ("SC_MotorSmall", 100, 100), ("SC_MotorMedium", 250, 100), ("SC_MotorLarge", 250, 100) };
        const string CableData = "SC_DataCable", CablePower = "SC_PowerCable";
        static TypeIndex tiCore; static EntityQuery qCore; static bool ok, failed, layoutLogged;
        static readonly Dictionary<ulong, int> written = new Dictionary<ulong, int>();   // cable -> value I wrote last
        static readonly HashSet<string> warnings = new HashSet<string>();
        static float t = 9f;

        // HashMapHelper<TKey> (Unity.Collections 2.x), inside the UnsafeHashMap: Ptr(values) Keys Next Buckets Count Capacity Log2MinGrowth BucketCapacity AllocatedIndex FirstFreeIdx SizeOfTValue
        unsafe struct Map { public byte* Ptr; public ulong* Keys; public int* Next; public int* Buckets; public int Count, Capacity, Log2MinGrowth, BucketCapacity, AllocatedIndex, FirstFreeIdx, SizeOfTValue; }

        public static unsafe void Step(EntityManager em, float dt)
        {
            t += dt; if (t < 1f || failed) return; t = 0f;
            try
            {
                if (!ok)
                {
                    tiCore = TypeManager.GetTypeIndex(Il2CppType.Of<Core.Singleton>());
                    var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiCore); qCore = em.CreateEntityQuery(a); ok = true;
                }
                var es = Ecs.QueryList(qCore); if (es.Count == 0) return;
                if (es.Count != nCoreLog) { nCoreLog = es.Count; ModLog.Info("bonus: " + es.Count + " Core.Singleton in world"); }
                for (int ie = 0; ie < es.Count; ie++) CoreStep(em, es[ie], ie);
                Garage(em);
            }
            catch (Exception ex) { failed = true; ModLog.Error("bonus: " + ex); }
        }
        static int nCoreLog = -1;

        static unsafe void CoreStep(EntityManager em, Entity core, int ie)
        {
            {
                var cs = (Ecs.HasC(em, core, tiCore) ? (byte*)em.GetComponentDataRawRW(core, tiCore) : null); if (cs == null) return;
                var m = *(Map**)(cs + Layout.CoreMax); if (m == null || m->Keys == null || m->Ptr == null) return;
                if (!layoutLogged) { layoutLogged = true; ModLog.Info("bonus: availability map Count=" + m->Count + " Capacity=" + m->Capacity + " Buckets=" + m->BucketCapacity + " SizeOfTValue=" + m->SizeOfTValue); }
                if (m->SizeOfTValue != 4 || m->Count <= 0 || m->Count > m->Capacity || m->BucketCapacity <= 0) { failed = true; ModLog.Warn("bonus: the map doesn't have the expected format — disabled"); return; }

                int bData = 0, bPower = 0; var releasedSet = new List<string>();
                foreach (var r in RULES) { int v = Read(m, Ecs.HashPrefab(r.motor), out bool found); if (found && v > 0) { bData += r.data; bPower += r.power; releasedSet.Add(r.motor); } }
                Apply(m, 0, CableData, bData, releasedSet); Apply(m, 0, CablePower, bPower, releasedSet);
                // +1 Radar when the player has unlocked everything that builds a car (one of each; 4 wheels; 4 bearings)
                bool car = Has(m, "SC_DecouplerQuarter", 1) && Has(m, "SC_Recoupler", 1) && Has(m, "SC_Beacon", 1) && Has(m, "SC_Bearing", 4)
                             && (Has(m, "SC_MotorSmall", 1) || Has(m, "SC_MotorMedium", 1) || Has(m, "SC_MotorLarge", 1))
                             && Sum(m, WHEELS) >= 4;
                var lr = car ? new List<string> { "complete car kit" } : new List<string>();
                Apply(m, 0, "SC_Radar", car ? 1 : 0, lr);
                maxRadar = Read(m, Ecs.HashPrefab("SC_Radar"), out _);
                // 0.7.3: the garage does NOT use the map above to limit: GarageGrabber.SetUsedComponentsCountMapJob (Burst) reads
                // Core.Singleton._standaloneComponentsAvailability (+96), adds up per group (SCPrefabData +0x3c) and subtracts what is used ->
                // _remainingComponentsMapPtr. So the bonus goes into both maps (+104 is the one the inventory shows).
                var sm = *(Map**)(cs + Layout.CoreStandalone);
                if (sm != null && sm->Keys != null && sm->Ptr != null && sm->SizeOfTValue == 4 && sm->Count > 0 && sm->Count <= sm->Capacity && sm->BucketCapacity > 0)
                {
                    if (!layoutSLogged) { layoutSLogged = true; ModLog.Info("bonus: standalone map Count=" + sm->Count + " Capacity=" + sm->Capacity + "; before bonus: Radar=" + Read(sm, Ecs.HashPrefab("SC_Radar"), out _) + " DataCable=" + Read(sm, Ecs.HashPrefab(CableData), out _) + " PowerCable=" + Read(sm, Ecs.HashPrefab(CablePower), out _)); }
                    Apply(sm, 1, CableData, bData, releasedSet); Apply(sm, 1, CablePower, bPower, releasedSet);
                    Apply(sm, 1, "SC_Radar", car ? 1 : 0, lr);
                    standRadar = Read(sm, Ecs.HashPrefab("SC_Radar"), out _);
                }
                else if (warnings.Add("standalone")) ModLog.Warn("bonus: standalone map (+96) doesn't have the expected format — only the maximum was changed");
                // 0.24.0 (Andre: "+2000 of each micro frame"): extra blocks from the .cfg list ([Bonus] extra_blocks). Works for
                // any block, including another mod's — Apply only writes into a key that ALREADY exists in the map, never inserts.
                var extras = Extras();
                if (extras.Count > 0)
                {
                    var why = new List<string> { "[Bonus] extra_blocks" };
                    foreach (var kv in extras)
                    {
                        Apply(m, 0, kv.Key, kv.Value, why);
                        if (sm != null && sm->Keys != null && sm->Ptr != null && sm->SizeOfTValue == 4 && sm->Count > 0) Apply(sm, 1, kv.Key, kv.Value, why);
                    }
                }
            }
        }

        /// <summary>Radar diagnostics: the inventory/garage uses GarageGrabberSingleton._remainingComponentsMapPtr
        /// ("how many more I can place") and _isComponentsLimitOverflow ("over the limit" -> won't let you fly). Logs when it changes:
        /// maximum in the Core's map, remaining in the garage, how many radars exist in the world.</summary>
        static int maxRadar = -1, standRadar = -1; static bool layoutSLogged; static string radarLog; static TypeIndex tiGar; static EntityQuery qGar; static bool garOk;
        static unsafe void Garage(EntityManager em)
        {
            try
            {
                if (!garOk) { garOk = true; tiGar = TypeManager.GetTypeIndex(Il2CppType.Of<GarageGrabberSingleton>()); var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiGar); qGar = em.CreateEntityQuery(a); }
                string rest = "no garage", over = "?";
                var gs = Ecs.QueryList(qGar);
                if (gs.Count > 0)
                {
                    var g = (Ecs.HasC(em, gs[0], tiGar) ? (byte*)em.GetComponentDataRawRO(gs[0], tiGar) : null);
                    if (g != null)
                    {
                        over = (*(g + Layout.GarOver) != 0).ToString();
                        var mr = *(Map**)(g + Layout.GarRemaining);
                        if (mr == null) rest = "null map";
                        else if (mr->SizeOfTValue != 4 || mr->Count < 0 || mr->Count > mr->Capacity || mr->BucketCapacity <= 0) rest = "map with an odd format";
                        else { int v = Read(mr, Ecs.HashPrefab("SC_Radar"), out bool found); rest = found ? v.ToString() : "not in map"; }
                    }
                }
                ulong h1 = Ecs.HashPrefab("SC_Radar"), h2 = Ecs.HashPrefab("SC_Radar M"); int n = 0;
                foreach (var e in Ecs.Components(em)) { ulong h = Ecs.Prefab(em, e); if (h == h1 || h == h2) n++; }
                string s = "bonus/radar: max in Core=" + maxRadar + ", standalone=" + standRadar + ", remaining in garage=" + rest + ", garage over limit=" + over;
                if (s != radarLog) { radarLog = s; ModLog.Info(s + ", radars in world=" + n); }   // (0.7.8: the count flickers between garage and flight — only logs when the remainder changes)
            }
            catch (Exception ex) { if (radarLog != "error") { radarLog = "error"; ModLog.Warn("bonus/radar diagnostics: " + ex.Message); } }
        }

        static unsafe void Apply(Map* m, int mapId, string cable, int bonus, List<string> releasedSet)
        {
            ulong hp = Ecs.HashPrefab(cable), h = hp ^ ((ulong)mapId * 0x9E3779B97F4A7C15UL);
            string nm = mapId == 0 ? "" : " [standalone]";
            int* v = Find(m, hp); if (v == null) { if (warnings.Add(cable + nm)) ModLog.Warn("bonus: " + cable + nm + " not in map (not unlocked yet?)"); return; }
            written.TryGetValue(h, out int latest); bool hasLast = written.ContainsKey(h);
            int gameBase = hasLast && *v == latest ? latest - Bonus_(h) : *v;
            int target = gameBase + bonus;
            if (*v == target) return;
            *v = target; written[h] = target; currentBonus[h] = bonus;
            ModLog.Info("bonus: " + cable + nm + " = " + gameBase + " + " + bonus + " = " + target + " (from: " + string.Join(", ", releasedSet) + ")");
        }
        static readonly Dictionary<ulong, int> currentBonus = new Dictionary<ulong, int>();
        static int Bonus_(ulong h) => currentBonus.TryGetValue(h, out var b) ? b : 0;

        /// <summary>0.24.0: "SC_X=2000, SC_Y=50" from the .cfg -> list. Empty line = no extras (the default).</summary>
        public static string ExtrasCfg = "";
        static string extrasRead; static Dictionary<string, int> extrasCache = new Dictionary<string, int>();
        static Dictionary<string, int> Extras()
        {
            if (extrasRead == ExtrasCfg) return extrasCache;
            extrasRead = ExtrasCfg; extrasCache = new Dictionary<string, int>();
            foreach (var chunk in (ExtrasCfg ?? "").Split(',', ';'))
            {
                var s = chunk.Trim(); if (s.Length == 0) continue;
                int i = s.IndexOf('='); if (i <= 0) { if (warnings.Add("extra:" + s)) ModLog.Warn("bonus: [Bonus] extra_blocks: '" + s + "' is not in the 'SC_Name=amount' format"); continue; }
                var name = OldNames.Current(s.Substring(0, i).Trim());   // 0.27.0: a Crap Mechanics block typed with its old name
                if (!int.TryParse(s.Substring(i + 1).Trim(), out int n) || n == 0) { if (warnings.Add("extra:" + s)) ModLog.Warn("bonus: [Bonus] extra_blocks: '" + s + "' has no valid amount"); continue; }
                extrasCache[name] = n;
            }
            if (extrasCache.Count > 0) ModLog.Info("bonus: " + extrasCache.Count + " block(s) with extra amount from [Bonus] extra_blocks");
            return extrasCache;
        }

        static readonly string[] WHEELS = { "SC_WheelSportsSmall", "SC_WheelSportsMedium", "SC_WheelSportsLarge", "SC_WheelOffroadSmall", "SC_WheelOffroadMedium", "SC_WheelOffroadLarge" };
        static unsafe bool Has(Map* m, string name, int n) { int v = Read(m, Ecs.HashPrefab(name), out bool found); return found && v >= n; }
        static unsafe int Sum(Map* m, string[] names) { int t = 0; foreach (var n in names) { int v = Read(m, Ecs.HashPrefab(n), out bool a); if (a && v > 0) t += v; } return t; }

        static unsafe int Read(Map* m, ulong h, out bool found) { var p = Find(m, h); found = p != null; return p != null ? *p : 0; }

        /// <summary>Walks the lists of each bucket (live entries only) looking for the key.</summary>
        static unsafe int* Find(Map* m, ulong h)
        {
            for (int b = 0; b < m->BucketCapacity; b++)
            {
                int i = m->Buckets[b], kept = 0;
                while (i >= 0 && i < m->Capacity && kept++ < 4096)
                {
                    if (m->Keys[i] == h) return (int*)m->Ptr + i;
                    i = m->Next[i];
                }
            }
            return null;
        }
    }
}
