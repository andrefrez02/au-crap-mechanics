using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>Extra amounts of blocks in the inventory, all of them from [Bonus] extra_blocks in the .cfg (0.29.0: until 0.28.x the
    /// cables per unlocked motor and the Radar of the complete car kit were fixed in the code — they are entries in the .cfg now, so every
    /// bonus is in one place and editable without rebuilding the mod).
    /// How: Core.Singleton._maxComponentsAvailability (+104) = UnsafeHashMap&lt;SCPrefab,int&gt;* (the map the inventory
    /// queries). I add the bonus on top of the game's value; if the game recalculates the map (mission completed, load), the value
    /// changes and I add it again on top of the new value.</summary>
    internal static class Bonus
    {
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
                // 0.31.3: offsets pelo nome do campo (GameFields), nao mais 96/104 fixos no codigo
                int oMax = GameFields.Of(typeof(Core.Singleton), "_maxComponentsAvailability", 104), oStand = GameFields.Of(typeof(Core.Singleton), "_standaloneComponentsAvailability", 96);
                if (oMax < 0) { failed = true; return; }
                var m = *(Map**)(cs + oMax); if (m == null || !GameFields.Plausible(m) || m->Keys == null || m->Ptr == null) return;
                if (!layoutLogged) { layoutLogged = true; ModLog.Info("bonus: availability map Count=" + m->Count + " Capacity=" + m->Capacity + " Buckets=" + m->BucketCapacity + " SizeOfTValue=" + m->SizeOfTValue); }
                if (m->SizeOfTValue != 4 || m->Count <= 0 || m->Count > m->Capacity || m->BucketCapacity <= 0) { failed = true; ModLog.Warn("bonus: the map doesn't have the expected format — disabled"); return; }

                // 0.29.0: one pass over the map to find where each key lives, instead of scanning the whole table per block —
                // a category in extra_blocks is hundreds of blocks, and the old search was the whole table for each one of them.
                BuildIndex(m, idx0);
                maxRadar = Read(m, idx0, Ecs.HashPrefab("SC_Radar"), out _);
                // 0.7.3: the garage does NOT use the map above to limit: GarageGrabber.SetUsedComponentsCountMapJob (Burst) reads
                // Core.Singleton._standaloneComponentsAvailability (+96), adds up per group (SCPrefabData +0x3c) and subtracts what is used ->
                // _remainingComponentsMapPtr. So the bonus goes into both maps (+104 is the one the inventory shows).
                var sm = oStand >= 0 ? *(Map**)(cs + oStand) : null;
                if (sm != null && GameFields.Plausible(sm) && sm->Keys != null && sm->Ptr != null && sm->SizeOfTValue == 4 && sm->Count > 0 && sm->Count <= sm->Capacity && sm->BucketCapacity > 0)
                {
                    BuildIndex(sm, idx1);
                    if (!layoutSLogged) { layoutSLogged = true; ModLog.Info("bonus: standalone map Count=" + sm->Count + " Capacity=" + sm->Capacity); }
                    standRadar = Read(sm, idx1, Ecs.HashPrefab("SC_Radar"), out _);
                }
                else if (warnings.Add("standalone")) ModLog.Warn("bonus: standalone map (+96) doesn't have the expected format — only the maximum was changed");
                // 0.24.0 (Andre: "+2000 of each micro frame"): extra blocks from the .cfg list ([Bonus] extra_blocks). Works for
                // any block, including another mod's — Apply only writes into a key that ALREADY exists in the map, never inserts.
                var extras = Extras();
                bool smOk = sm != null && GameFields.Plausible(sm) && sm->Keys != null && sm->Ptr != null && sm->SizeOfTValue == 4 && sm->Count > 0;
                foreach (var x in extras)
                {
                    Apply(m, 0, idx0, x.block, x.hash, x.amount, x.why);
                    if (smOk) Apply(sm, 1, idx1, x.block, x.hash, x.amount, x.why);
                }
            }
        }

        /// <summary>Radar diagnostics: the inventory/garage uses GarageGrabberSingleton._remainingComponentsMapPtr (+216,
        /// "how many more I can place") and _isComponentsLimitOverflow (+129, "over the limit" -> won't let you fly). Logs when it changes:
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
                        // 0.31.3 (crash do G): o +216 fixo virou parte de _selectionPoints na 1.0.300 (o mapa foi pra +232) — pelo nome agora
                        int oOver = GameFields.Of(typeof(GarageGrabberSingleton), "_isComponentsLimitOverflow", 129), oRest = GameFields.Of(typeof(GarageGrabberSingleton), "_remainingComponentsMapPtr", 216);
                        if (oOver >= 0) over = (*(g + oOver) != 0).ToString();
                        var mr = oRest >= 0 ? *(Map**)(g + oRest) : null;
                        if (oRest < 0) rest = "field not in this game version";
                        else if (mr == null) rest = "null map";
                        else if (!GameFields.Plausible(mr)) rest = "not a pointer (" + ((ulong)mr).ToString("X") + ")";
                        else if (mr->SizeOfTValue != 4 || mr->Count < 0 || mr->Count > mr->Capacity || mr->BucketCapacity <= 0) rest = "map with an odd format";
                        else { BuildIndex(mr, idx2); int v = Read(mr, idx2, Ecs.HashPrefab("SC_Radar"), out bool found); rest = found ? v.ToString() : "not in map"; }
                    }
                }
                ulong h1 = Ecs.HashPrefab("SC_Radar"), h2 = Ecs.HashPrefab("SC_Radar M"); int n = 0;
                foreach (var e in Ecs.Components(em)) { ulong h = Ecs.Prefab(em, e); if (h == h1 || h == h2) n++; }
                string s = "bonus/radar: max in Core=" + maxRadar + ", standalone=" + standRadar + ", remaining in garage=" + rest + ", garage over limit=" + over;
                if (s != radarLog) { radarLog = s; ModLog.Info(s + ", radars in world=" + n); }   // (0.7.8: the count flickers between garage and flight — only logs when the remainder changes)
            }
            catch (Exception ex) { if (radarLog != "error") { radarLog = "error"; ModLog.Warn("bonus/radar diagnostics: " + ex.Message); } }
        }

        /// <summary>hp = the block's hash, worked out once in Extras (Ecs.HashPrefab builds an Il2Cpp object, and this runs for every
        /// block of every category once a second).</summary>
        static unsafe void Apply(Map* m, int mapId, Dictionary<ulong, int> idx, string cable, ulong hp, int bonus, string why)
        {
            ulong h = hp ^ ((ulong)mapId * 0x9E3779B97F4A7C15UL);
            string nm = mapId == 0 ? "" : " [standalone]";
            int* v = Find(m, idx, hp); if (v == null) { if (warnings.Add(cable + nm)) ModLog.Warn("bonus: " + cable + nm + " not in map (the block is still locked, or the name is not in blocks.txt)"); return; }
            written.TryGetValue(h, out int latest); bool hasLast = written.ContainsKey(h);
            int gameBase = hasLast && *v == latest ? latest - Bonus_(h) : *v;
            int target = gameBase + bonus;
            if (*v == target) return;
            *v = target; written[h] = target; currentBonus[h] = bonus;
            ModLog.Info("bonus: " + cable + nm + " = " + gameBase + " + " + bonus + " = " + target + " (from: " + why + ")");
        }
        static readonly Dictionary<ulong, int> currentBonus = new Dictionary<ulong, int>();
        static int Bonus_(ulong h) => currentBonus.TryGetValue(h, out var b) ? b : 0;

        /// <summary>0.24.0: "SC_X=2000, SC_Y=50" from the .cfg -> list. 0.29.0: an entry can also name a whole category instead of one
        /// block — a group of the game ("Frame=400", every frame), an inventory tab ("tab:Electronics=50") or the name the inventory shows
        /// ("Power Router=5"). config/CrapMechanics/blocks.txt lists every name that works. A later entry wins, so
        /// "Frame=400, SC_FrameAA=5000" gives 400 to every frame and 5000 to that one.</summary>
        public static string ExtrasCfg = "";
        internal struct Extra { public string block, why; public int amount; public ulong hash; }
        static string extrasRead; static bool extrasReady; static List<Extra> extrasCache = new List<Extra>();
        static List<Extra> Extras()
        {
            if (extrasRead == ExtrasCfg && extrasReady == BlockIndex.Ready) return extrasCache;
            // the catalogue is what turns a category into blocks; without it even a plain name would warn for nothing
            if (!BlockIndex.Ready && !BlockIndex.Failed && !string.IsNullOrWhiteSpace(ExtrasCfg)) { extrasRead = null; return extrasCache = new List<Extra>(); }
            extrasRead = ExtrasCfg; extrasReady = BlockIndex.Ready;
            var acc = new Dictionary<string, Extra>(StringComparer.Ordinal); var notes = new List<string>();
            foreach (var chunk in (ExtrasCfg ?? "").Split(',', ';'))
            {
                var s = chunk.Trim(); if (s.Length == 0) continue;
                int i = s.IndexOf('='); if (i <= 0) { if (warnings.Add("extra:" + s)) ModLog.Warn("bonus: [Bonus] extra_blocks: '" + s + "' is not in the 'name=amount' format"); continue; }
                var token = s.Substring(0, i).Trim();
                if (!int.TryParse(s.Substring(i + 1).Trim(), out int n) || n == 0) { if (warnings.Add("extra:" + s)) ModLog.Warn("bonus: [Bonus] extra_blocks: '" + s + "' has no valid amount"); continue; }
                var why = "[Bonus] extra_blocks: " + token;
                var cat = BlockIndex.OfCategory(token);
                if (cat != null)
                {
                    foreach (var b in cat) acc[b] = new Extra { block = b, amount = n, why = why, hash = Ecs.HashPrefab(b) };
                    notes.Add(token + " (" + cat.Count + " block(s)) +" + n);
                    continue;
                }
                var name = OldNames.Current(token);   // 0.27.0: a Crap Mechanics block typed with its old name
                if (!BlockIndex.Known(name))
                {
                    var byName = BlockIndex.OfDisplay(token);   // 0.29.0: the name the inventory shows
                    if (byName != null) { notes.Add(token + " = " + byName + " +" + n); name = byName; }
                    else if (BlockIndex.Ready && warnings.Add("extra:" + token)) ModLog.Warn("bonus: [Bonus] extra_blocks: '" + token + "' is not a block, a group or an inventory tab — see config/" + PluginInfo.PluginName + "/blocks.txt");
                }
                acc[name] = new Extra { block = name, amount = n, why = why, hash = Ecs.HashPrefab(name) };
            }
            extrasCache = new List<Extra>(acc.Values);
            if (extrasCache.Count > 0) ModLog.Info("bonus: [Bonus] extra_blocks gives an extra amount to " + extrasCache.Count + " block(s)" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : ""));
            return extrasCache;
        }

        static unsafe int Read(Map* m, Dictionary<ulong, int> idx, ulong h, out bool found) { var p = Find(m, idx, h); found = p != null; return p != null ? *p : 0; }

        static readonly Dictionary<ulong, int> idx0 = new Dictionary<ulong, int>(), idx1 = new Dictionary<ulong, int>(), idx2 = new Dictionary<ulong, int>();

        /// <summary>Walks the lists of each bucket (live entries only) once and notes where each key lives.</summary>
        static unsafe void BuildIndex(Map* m, Dictionary<ulong, int> idx)
        {
            idx.Clear();
            for (int b = 0; b < m->BucketCapacity; b++)
            {
                int i = m->Buckets[b], kept = 0;
                while (i >= 0 && i < m->Capacity && kept++ < 4096) { idx[m->Keys[i]] = i; i = m->Next[i]; }
            }
        }
        static unsafe int* Find(Map* m, Dictionary<ulong, int> idx, ulong h) => idx.TryGetValue(h, out int i) && i >= 0 && i < m->Capacity ? (int*)m->Ptr + i : null;
    }
}
