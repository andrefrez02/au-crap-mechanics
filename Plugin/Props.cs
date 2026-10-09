using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Mecanica
{
    /// <summary>0.23.2 (Andre: "faz igual as outras descricoes do mod, onde mostra mass, max force, etc. separadinho"): as linhas de
    /// status do painel do inventario. O jogo monta a lista em `EPC_SpaceshipComponent.GetPropertiesList`: massa (chave InvSCMass),
    /// material/calor da cola, e no fim um `list.AddRange(_customProperties)` — as strings do `_customProperties` entram COMO ESTAO,
    /// sem passar pelo Localization. Como o prefab e' um so pros dois idiomas, quem preenche e' o plugin, aqui, lendo as chaves
    /// `&lt;prefab&gt;_Prop1`, `_Prop2`, … do Localization.json (para na primeira que faltar). Uma vez so, quando o Core e o mapa de
    /// textos ja existem.</summary>
    internal static class Props
    {
        static bool feito, falhou; static float t = 9f;

        public static void Passo(float dt)
        {
            if (feito || falhou) return;
            t += dt; if (t < 1f) return; t = 0f;
            var core = JogoMalhas.Nucleo; if (core == null) return;
            Il2CppSystem.Collections.Generic.Dictionary<string, string> mapa = null;
            try { var loc = Localization._cachedInstance; mapa = loc == null ? null : loc._map; } catch (Exception) { }
            if (mapa == null) return;
            try
            {
                var lista = core._spaceshipComponents; if (lista == null) return;
                int blocos = 0, linhas = 0;
                for (int i = 0; i < lista.Length; i++)
                {
                    var epc = lista[i]; if (epc == null) continue;
                    string n = epc.name; if (string.IsNullOrEmpty(n) || !Juntas.EhDoMod(n)) continue;
                    var txt = new List<string>();
                    for (int p = 1; p <= 8; p++)
                    {
                        string chave = n + "_Prop" + p;
                        if (!mapa.ContainsKey(chave)) break;
                        string v = mapa[chave]; if (string.IsNullOrEmpty(v)) break;
                        txt.Add(v);
                    }
                    if (txt.Count == 0) continue;
                    var arr = new Il2CppStringArray(txt.Count);
                    for (int k = 0; k < txt.Count; k++) arr[k] = txt[k];
                    epc._customProperties = arr; blocos++; linhas += txt.Count;
                }
                feito = true;
                Registro.Info("properties: " + linhas + " status line(s) on " + blocos + " block(s)");
            }
            catch (Exception ex) { falhou = true; Registro.Aviso("properties: " + ex.Message); }
        }
        public static void Esquecer() { feito = false; t = 9f; }
    }
}
