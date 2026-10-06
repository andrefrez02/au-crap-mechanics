using System;
using HarmonyLib;

namespace Mecanica
{
    /// <summary>01/10 (crash ao sair pro menu principal e ao carregar BP, sempre em coreclr ProcessCLRException = violacao de acesso
    /// dentro de codigo .NET): os mods leem o ECS por ponteiro cru; na descarga do mundo (Core.ClearGame / CleanupWorldToDefaultState)
    /// o jogo destroi tudo. Aqui, no comeco da descarga, o mod para por 3 s e depois refaz tudo do zero (queries, ponteiros, caches).</summary>
    [HarmonyPatch]
    internal static class Descarga
    {
        static float t = -99f; static int vezes;
        /// <summary>true enquanto o jogo esta descarregando/limpando o mundo (3 s depois do aviso).</summary>
        public static bool Ativa => UnityEngine.Time.unscaledTime - t < 3f;
        /// <summary>true 1x depois de cada descarga (pra refazer o estado); o chamador zera com Confirmar().</summary>
        public static bool Pendente;
        public static void Confirmar() { Pendente = false; }

        [HarmonyPrefix, HarmonyPatch(typeof(Core), nameof(Core.ClearGame))]
        static void AntesClearGame() { Marcar("Core.ClearGame"); }
        [HarmonyPrefix, HarmonyPatch(typeof(Core), nameof(Core.CleanupWorldToDefaultState))]
        static void AntesCleanup() { Marcar("Core.CleanupWorldToDefaultState"); }
        static void Marcar(string de)
        {
            try { t = UnityEngine.Time.unscaledTime; Pendente = true; if (vezes++ < 20) Registro.Info("world unloading (" + de + "): mod paused for 3 s, then set up again"); }
            catch (Exception) { }
        }
        public static void Patch(Harmony h)
        {
            try { h.PatchAll(typeof(Descarga)); } catch (Exception ex) { Registro.Aviso("world unload hook failed (" + ex.Message + ")"); }
        }
    }
}
