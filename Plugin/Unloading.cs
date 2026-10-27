using System;
using HarmonyLib;

namespace CrapMechanics
{
    /// <summary>01/10 (crash when leaving to the main menu and when loading a BP, always in coreclr ProcessCLRException = access violation
    /// inside .NET code): the mods read the ECS through raw pointers; when the world unloads (Core.ClearGame / CleanupWorldToDefaultState)
    /// the game destroys everything. Here, at the start of the unload, the mod stops for 3 s and then rebuilds everything from scratch (queries, pointers, caches).</summary>
    [HarmonyPatch]
    internal static class Unloading
    {
        static float t = -99f; static int times;
        /// <summary>true while the game is unloading/cleaning up the world (3 s after the notice).</summary>
        public static bool Active => UnityEngine.Time.unscaledTime - t < 3f;
        /// <summary>true once after each unload (to rebuild the state); the caller clears it with Confirm().</summary>
        public static bool Pending;
        public static void Confirm() { Pending = false; }

        [HarmonyPrefix, HarmonyPatch(typeof(Core), nameof(Core.ClearGame))]
        static void BeforeClearGame() { Mark("Core.ClearGame"); }
        [HarmonyPrefix, HarmonyPatch(typeof(Core), nameof(Core.CleanupWorldToDefaultState))]
        static void BeforeCleanup() { Mark("Core.CleanupWorldToDefaultState"); }
        static void Mark(string srcE)
        {
            try { t = UnityEngine.Time.unscaledTime; Pending = true; if (times++ < 20) ModLog.Info("world unloading (" + srcE + "): mod paused for 3 s, then set up again"); }
            catch (Exception) { }
        }
        public static void Patch(Harmony h)
        {
            try { h.PatchAll(typeof(Unloading)); } catch (Exception ex) { ModLog.Warn("world unload hook failed (" + ex.Message + ")"); }
        }
    }
}
