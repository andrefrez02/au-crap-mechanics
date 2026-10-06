using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HarmonyLib;

namespace Mecanica
{
    /// <summary>O Components Framework aplica o Localization.json no FIM do Core.Initialize, mas o jogo le os textos das portas
    /// (chave "{prefab}_Port{i}") no meio dele, quando monta as entities dos componentes — por isso as portas dos nossos blocos
    /// ficavam sem explicacao. Aqui os textos entram no mapa do jogo antes da primeira InitializeEntity.</summary>
    [HarmonyPatch(typeof(EPC_SpaceshipComponent), nameof(EPC_SpaceshipComponent.InitializeEntity))]
    internal static class Textos
    {
        static bool feito = false;
        public static HarmonyLib.Harmony H; static bool desfeito;
        /// <summary>01/10: depois de aplicar, tira o patch (InitializeEntity roda ao montar blocos; um patch a menos onde o jogo pode lancar excecao).</summary>
        public static void Desfazer()
        {
            if (!feito || desfeito || H == null) return; desfeito = true;
            try { H.Unpatch(AccessTools.Method(typeof(EPC_SpaceshipComponent), nameof(EPC_SpaceshipComponent.InitializeEntity)), HarmonyPatchType.Prefix, H.Id); Registro.Info("texts: patch removed (no longer needed)"); }
            catch (Exception ex) { Registro.Aviso("texts: could not remove the patch: " + ex.Message); }
        }
        public static string Pasta;

        static void Prefix()
        {
            if (feito) return;
            try
            {
                var loc = Localization._cachedInstance; if (loc == null || loc._map == null) return;
                feito = true;
                var arq = Path.Combine(Pasta ?? "", "Localization.json");
                if (!File.Exists(arq)) { Registro.Aviso("texts: " + arq + " not found"); return; }
                var tudo = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(arq));
                string lingua = null; try { lingua = Localization.GetLanguage(); } catch (Exception) { }
                if (lingua == null || !tudo.TryGetValue(lingua, out var tabela))
                {
                    if (lingua == "brazilian" && tudo.TryGetValue("portuguese", out tabela)) { }
                    else if (!tudo.TryGetValue("english", out tabela)) return;
                }
                int n = 0;
                foreach (var kv in tabela) { if (loc._map.ContainsKey(kv.Key)) continue; loc._map[kv.Key] = kv.Value; n++; }
                Registro.Info("texts: " + n + " string(s) (" + (lingua ?? "?") + ") applied before the components were built");
            }
            catch (Exception ex) { Registro.Erro("texts: " + ex); }
        }
    }
}
