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
        /// <summary>3 s after Core.ClearGame (main menu / world unload): the mod does not read the ECS at all.</summary>
        public static bool Window => UnityEngine.Time.unscaledTime - t < 3f;
        /// <summary>true while the game is unloading/cleaning up the world: the 3 s after ClearGame, or a cleanup not yet followed by the
        /// mod's reset (Pending). The system hooks check this; the frame cycle checks Window and then does the reset.</summary>
        public static bool Active => Pending || Window;
        /// <summary>true once after each unload (to rebuild the state); the caller clears it with Confirm().</summary>
        public static bool Pending;
        public static void Confirm() { Pending = false; }

        [HarmonyPrefix, HarmonyPatch(typeof(Core), nameof(Core.ClearGame))]
        static void BeforeClearGame() { Mark("Core.ClearGame", true); }
        // 0.31.26 (item 14): CleanupWorldToDefaultState is not only the unload — DestroySpaceship (every recall) calls it
        // [recon_managed/SpaceshipSystem.cs:739-742,841-849] and so does a client on network event 10 [NetcoreDoIncomingEventsSystem.cs:450,560].
        // A 3 s pause there made a relaunch within 3 s of a recall skip the launch steps (zero-gravity launch, soft start, SplitsInTick,
        // the 100 km rule). Here it only asks for the reset (Pending), done on the next frame; the 3 s window is kept for ClearGame
        // (the 01/10 main-menu crash). Loading a BP is covered by MenuBP.
        [HarmonyPrefix, HarmonyPatch(typeof(Core), nameof(Core.CleanupWorldToDefaultState))]
        static void BeforeCleanup() { Mark("Core.CleanupWorldToDefaultState", false); }
        static void Mark(string srcE, bool pause)
        {
            try
            {
                try { Hotbar.BeforeUnload(); } catch (Exception) { }   // remote control (delivery 2): the mod's phone-in-the-hand entity goes before the world is cleared
                if (pause) t = UnityEngine.Time.unscaledTime;
                Pending = true; Ecs.ForgetCache();   // 0.31.35: nothing remembered about a world that is being torn down
                if (times++ < 20) ModLog.Info("world unloading (" + srcE + "): " + (pause ? "mod paused for 3 s, then set up again" : "mod set up again on the next frame"));
            }
            catch (Exception) { }
        }
        public static void Patch(Harmony h)
        {
            try { h.PatchAll(typeof(Unloading)); } catch (Exception ex) { ModLog.Warn("world unload hook failed (" + ex.Message + ")"); }
        }
    }
}
