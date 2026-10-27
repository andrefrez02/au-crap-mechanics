using System;
using System.Collections.Generic;
using HarmonyLib;

namespace CrapMechanics
{
    /// <summary>0.12.14 (Andre: "mirror the block next to it with V"): the game mirrors a selection by swapping each piece for the version in
    /// EPC_SpaceshipComponent._mirroredVersion (Core.Initialize builds _normalToMirrorVersion/_mirrorToNormalVersion from
    /// that; a blueprint keeps the normal piece). The bundle carries "&lt;name&gt; M" (gen_prefabs.mirror: data mirrored in X, meshes
    /// with scale -1). Components Framework registers EACH prefab as an instance of its own, so the link has to point to the
    /// registered INSTANCE (not to the bundle's asset): done here, before Core.Initialize. Symmetric pieces (or ones built
    /// from game pieces) point to themselves, as the game does with the straight pipe and glass.</summary>
    [HarmonyPatch]
    internal static class Mirrors
    {
        /// <summary>Names of the mod's " M" versions (only those leave the inventory).</summary>
        static readonly HashSet<string> namesM = new HashSet<string>();

        static readonly string[] Self = { "SC_Pedal", "SC_SteeringWheel", "SC_TurretSeat", "SC_TankSmallBlue", "SC_TankSmallGreen", "SC_DriveShaft", "SC_SlipRing", "SC_MiniSlipRing", "SC_PistonEnd", "SC_MiniPistonEnd" };   // 0.22.4: symmetric plates   // 0.20.0: Piston End (symmetric); 0.22.0: Slip Ring (ports symmetric in X)
        /// <summary>0.20.0 (Andre: "remove the piston tiers"): the 2/4/8 pistons stay registered (old BPs) but leave the inventory.</summary>
        static readonly string[] Hidden = { };   // 0.22.5: the old items (2/4/8 pistons, cube Piston End) were removed

        public static void Connect(IReadOnlyList<EPC_SpaceshipComponent> regs)
        {
            if (regs == null) return;
            try
            {
                var byName = new Dictionary<string, EPC_SpaceshipComponent>();
                foreach (var e in regs) if (e != null && !string.IsNullOrEmpty(e.name)) byName[e.name] = e;
                int pairs = 0, ownSet = 0; var noPair = new List<string>();
                foreach (var kv in byName)
                {
                    if (kv.Key.EndsWith(" M")) { kv.Value._mirroredVersion = null; namesM.Add(kv.Key); continue; }
                    if (byName.TryGetValue(kv.Key + " M", out var m)) { kv.Value._mirroredVersion = m; pairs++; }
                    else if (Array.IndexOf(Self, kv.Key) >= 0) { kv.Value._mirroredVersion = kv.Value; ownSet++; }
                    else noPair.Add(kv.Key);
                }
                ModLog.Info("mirror (V): " + pairs + " part(s) with a mirrored version, " + ownSet + " symmetric" + (noPair.Count > 0 ? "; no mirrored version: " + string.Join(", ", noPair) : ""));
            }
            catch (Exception ex) { ModLog.Warn("mirror (V): could not link the mirrored versions: " + ex.Message); }
        }
        /// <summary>0.13.1 (Andre: "the M pieces are duplicated in the inventory"): UIInventory.PreAwake creates an item for every piece in
        /// Core._componentsMap, and Components Framework puts the M versions there too (the game's M ones don't go in). Here the items of the mod's M
        /// pieces leave _allListItems (the list that RefreshItems/OnEnable/search go through) and are turned off; the prefab->item map stays
        /// intact (the game may query it). V keeps swapping to the M.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(UIInventory), nameof(UIInventory.PreAwake))]
        static void RemoveFromInventory(UIInventory __instance)
        {
            ModLog.Trace("Mirrors.RemoveFromInventory");
            if (__instance == null) return;
            try
            {
                var l = __instance._allListItems; if (l == null) return; int removed = 0, olds = 0;
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    var it = l[i]; var pf = it != null ? it._prefab : null;
                    if (pf == null) continue;
                    bool m = namesM.Contains(pf.name), old = Array.IndexOf(Hidden, pf.name) >= 0;
                    if (!m && !old) continue;
                    try { it.gameObject.SetActive(false); } catch (Exception) { }
                    l.RemoveAt(i); if (m) removed++; else olds++;
                }
                ModLog.Info("mirror (V): " + removed + " mirrored version(s) hidden from the inventory" + (olds > 0 ? "; " + olds + " old piston tier(s) hidden (Piston 2/4/8 — old blueprints still work)" : ""));
            }
            catch (Exception ex) { ModLog.Warn("mirror (V): could not hide the mirrored versions from the inventory: " + ex.Message); }
        }
    }
}
