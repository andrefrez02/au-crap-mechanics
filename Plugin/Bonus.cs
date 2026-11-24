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
    /// How (0.31.26, item 24): the game builds Core.Singleton._standaloneComponentsAvailability (+96) from each block's
    /// EPC_SpaceshipComponent._availableAmount (+92) plus the objectives, and _maxComponentsAvailability (+104) from that one, pooling the
    /// blocks of a group (a grouped block may use the whole group's sum) [recon_managed/Core.cs:680-733]. The mod keeps each block's original
    /// _availableAmount, writes original + bonus there and asks the game to rebuild both maps (Core.RefreshStandaloneAvailableComponents),
    /// so every later rebuild by the game (objective completed, load, network event) keeps the bonus and the garage limit
    /// [GarageGrabber.cs:499-533] agrees with what the inventory shows. Until 0.31.25 the bonus was written straight into the two hash maps,
    /// which left grouped blocks out of step with the game's own pooling. The maps are only read now (radar diagnostics).</summary>
    internal static class Bonus
    {
        static TypeIndex tiCore; static EntityQuery qCore; static bool ok, failed, layoutLogged;
        static readonly HashSet<string> warnings = new HashSet<string>();
        static readonly Dictionary<string, int> origAmount = new Dictionary<string, int>(StringComparer.Ordinal);    // block -> the game's _availableAmount
        static readonly Dictionary<string, int> writtenAmount = new Dictionary<string, int>(StringComparer.Ordinal); // block -> what the mod wrote there
        static List<Extra> appliedList; static Core appliedCore;
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
                Amounts();
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
            }
        }

        /// <summary>0.31.26 (item 24): writes original + bonus into each block's EPC_SpaceshipComponent._availableAmount and, when something
        /// changed, has the game rebuild its availability maps (Core.RefreshStandaloneAvailableComponents [recon_managed/Core.cs:668-733]).
        /// Runs again only when the bonus list or the Core changes. A value the mod did not write (the game or another mod changed it) becomes
        /// the new original.</summary>
        static void Amounts()
        {
            var core = GameMeshes.CoreEntity; if (core == null) return;
            var extras = Extras();
            if (ReferenceEquals(extras, appliedList) && core == appliedCore) return;
            var list = core._spaceshipComponents; if (list == null || list.Length == 0) return;
            var bonusOf = new Dictionary<string, Extra>(StringComparer.Ordinal);
            foreach (var x in extras) bonusOf[x.block] = x;
            int changed = 0; var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < list.Length; i++)
            {
                var epc = list[i]; if (epc == null) continue;
                string name = epc.name; if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                int cur = epc._availableAmount;
                bool mine = writtenAmount.TryGetValue(name, out int w) && cur == w;
                if (!origAmount.TryGetValue(name, out int orig) || (!mine && cur != orig)) { origAmount[name] = orig = cur; writtenAmount.Remove(name); }
                bool has = bonusOf.TryGetValue(name, out var x);
                int want = orig + (has ? x.amount : 0);
                if (cur == want) continue;
                epc._availableAmount = want; writtenAmount[name] = want; changed++;
                ModLog.Info("bonus: " + name + " = " + orig + (has ? " + " + x.amount : " (bonus removed)") + " = " + want + (has ? " (from: " + x.why + ")" : ""));
            }
            foreach (var x in extras) if (!seen.Contains(x.block) && warnings.Add(x.block)) ModLog.Warn("bonus: " + x.block + " is not in the game's block list (the name is not in blocks.txt)");
            appliedList = extras; appliedCore = core;
            if (changed == 0) return;
            try { core.RefreshStandaloneAvailableComponents(); ModLog.Info("bonus: " + changed + " block amount(s) changed — the game rebuilt its availability maps"); }
            catch (Exception ex) { ModLog.Warn("bonus: the game could not rebuild its availability maps (" + ex.Message + ") — the bonus shows on its next rebuild"); }
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
                    var g = (Ecs.HasC(em, gs[0], tiGar) ? (byte*)Ecs.RO(em, gs[0], tiGar) : null);
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
                { var set = Ecs.Launched(em); n = set.Of(h1).Count + (h2 != h1 ? set.Of(h2).Count : 0); }   // 0.31.15: from the block index (it read every block of the ship once a second)
                string s = "bonus/radar: max in Core=" + maxRadar + ", standalone=" + standRadar + ", remaining in garage=" + rest + ", garage over limit=" + over;
                if (s != radarLog) { radarLog = s; ModLog.Info(s + ", radars in world=" + n); }   // (0.7.8: the count flickers between garage and flight — only logs when the remainder changes)
            }
            catch (Exception ex) { if (radarLog != "error") { radarLog = "error"; ModLog.Warn("bonus/radar diagnostics: " + ex.Message); } }
        }

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
