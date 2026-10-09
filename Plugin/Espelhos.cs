using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Mecanica
{
    /// <summary>0.12.14 (Andre: "espelhar bloco ao lado com V"): o jogo espelha uma selecao trocando cada peca pela versao em
    /// EPC_SpaceshipComponent._mirroredVersion (o Core.Initialize monta _normalToMirrorVersion/_mirrorToNormalVersion a partir
    /// disso; blueprint guarda a peca normal). O bundle traz "&lt;nome&gt; M" (gen_prefabs.espelhar: dados espelhados em X, malhas
    /// com escala -1). O Components Framework registra CADA prefab como uma instancia propria, entao o link tem que apontar pra
    /// INSTANCIA registrada (nao pro asset do bundle): feito aqui, antes do Core.Initialize. Pecas simetricas (ou montadas a
    /// partir de pecas do jogo) apontam pra si mesmas, igual o jogo faz com cano reto e vidro.</summary>
    [HarmonyPatch]
    internal static class Espelhos
    {
        /// <summary>Nomes das versoes " M" do mod (so elas saem do inventario).</summary>
        static readonly HashSet<string> nomesM = new HashSet<string>();

        static readonly string[] SiMesmo = { "SC_Pedal", "SC_Volante", "SC_TurretSeat", "SC_TanqueAzul", "SC_TanqueVerde", "SC_DriveShaft", "SC_SlipRing", "SC_MiniSlipRing", "SC_PistaoFim", "SC_MiniPistaoFim" };   // 0.22.4: placas simetricas   // 0.20.0: Piston End (simetrico); 0.22.0: Slip Ring (portas simetricas em X)
        /// <summary>0.20.0 (Andre: "remove os tiers do pistao"): os pistoes 2/4/8 continuam registrados (BPs antigos) mas saem do inventario.</summary>
        static readonly string[] Escondidos = { };   // 0.22.5: os itens antigos (pistoes 2/4/8, Piston End cubo) foram removidos

        public static void Ligar(IReadOnlyList<EPC_SpaceshipComponent> regs)
        {
            if (regs == null) return;
            try
            {
                var porNome = new Dictionary<string, EPC_SpaceshipComponent>();
                foreach (var e in regs) if (e != null && !string.IsNullOrEmpty(e.name)) porNome[e.name] = e;
                int pares = 0, proprios = 0; var semPar = new List<string>();
                foreach (var kv in porNome)
                {
                    if (kv.Key.EndsWith(" M")) { kv.Value._mirroredVersion = null; nomesM.Add(kv.Key); continue; }
                    if (porNome.TryGetValue(kv.Key + " M", out var m)) { kv.Value._mirroredVersion = m; pares++; }
                    else if (Array.IndexOf(SiMesmo, kv.Key) >= 0) { kv.Value._mirroredVersion = kv.Value; proprios++; }
                    else semPar.Add(kv.Key);
                }
                Registro.Info("mirror (V): " + pares + " part(s) with a mirrored version, " + proprios + " symmetric" + (semPar.Count > 0 ? "; no mirrored version: " + string.Join(", ", semPar) : ""));
            }
            catch (Exception ex) { Registro.Aviso("mirror (V): could not link the mirrored versions: " + ex.Message); }
        }
        /// <summary>0.13.1 (Andre: "as pecas M estao duplicadas no inventario"): o UIInventory.PreAwake cria um item pra cada peca de
        /// Core._componentsMap, e o Components Framework poe as versoes M la tambem (as M do jogo nao entram). Aqui os itens das M do
        /// mod saem de _allListItems (lista que o RefreshItems/OnEnable/busca percorrem) e ficam desligados; o mapa prefab->item fica
        /// intacto (o jogo pode consultar). O V continua trocando pela M.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(UIInventory), nameof(UIInventory.PreAwake))]
        static void TirarDoInventario(UIInventory __instance)
        {
            if (__instance == null) return;
            try
            {
                var l = __instance._allListItems; if (l == null) return; int tirados = 0, velhos = 0;
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    var it = l[i]; var pf = it != null ? it._prefab : null;
                    if (pf == null) continue;
                    bool m = nomesM.Contains(pf.name), velho = Array.IndexOf(Escondidos, pf.name) >= 0;
                    if (!m && !velho) continue;
                    try { it.gameObject.SetActive(false); } catch (Exception) { }
                    l.RemoveAt(i); if (m) tirados++; else velhos++;
                }
                Registro.Info("mirror (V): " + tirados + " mirrored version(s) hidden from the inventory" + (velhos > 0 ? "; " + velhos + " old piston tier(s) hidden (Piston 2/4/8 — old blueprints still work)" : ""));
            }
            catch (Exception ex) { Registro.Aviso("mirror (V): could not hide the mirrored versions from the inventory: " + ex.Message); }
        }
    }
}
