using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HarmonyLib;

namespace CrapMechanics
{
    /// <summary>Components Framework applies Localization.json at the END of Core.Initialize, but the game reads the port texts
    /// (key "{prefab}_Port{i}") in the middle of it, when it builds the components' entities — that's why the ports of our blocks
    /// had no explanation. Here the texts go into the game's map before the first InitializeEntity.</summary>
    [HarmonyPatch(typeof(EPC_SpaceshipComponent), nameof(EPC_SpaceshipComponent.InitializeEntity))]
    internal static class Texts
    {
        static bool isDone = false;
        public static HarmonyLib.Harmony H; static bool isUndone;
        /// <summary>01/10: after applying, removes the patch (InitializeEntity runs when blocks are built; one less patch where the game may throw).</summary>
        public static void Undo()
        {
            if (!isDone || isUndone || H == null) return; isUndone = true;
            try { H.Unpatch(AccessTools.Method(typeof(EPC_SpaceshipComponent), nameof(EPC_SpaceshipComponent.InitializeEntity)), HarmonyPatchType.Prefix, H.Id); ModLog.Info("texts: patch removed (no longer needed)"); }
            catch (Exception ex) { ModLog.Warn("texts: could not remove the patch: " + ex.Message); }
        }
        public static string Folder;

        static void Prefix()
        {
            if (isDone) return;
            try
            {
                var loc = Localization._cachedInstance; if (loc == null || loc._map == null) return;
                isDone = true;
                var file = Path.Combine(Folder ?? "", "Localization.json");
                if (!File.Exists(file)) { ModLog.Warn("texts: " + file + " not found"); return; }
                var everything = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(file));
                string language = null; try { language = Localization.GetLanguage(); } catch (Exception) { }
                if (language == null || !everything.TryGetValue(language, out var table))
                {
                    if (language == "brazilian" && everything.TryGetValue("portuguese", out table)) { }
                    else if (!everything.TryGetValue("english", out table)) return;
                }
                int n = 0;
                foreach (var kv in table) { if (loc._map.ContainsKey(kv.Key)) continue; loc._map[kv.Key] = kv.Value; n++; }
                ModLog.Info("texts: " + n + " string(s) (" + (language ?? "?") + ") applied before the components were built");
            }
            catch (Exception ex) { ModLog.Error("texts: " + ex); }
        }
    }
}
