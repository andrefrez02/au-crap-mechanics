using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Mecanica
{
    /// <summary>Rodas: blocos passivos (vao em cima de um Bearing). O jogo nao tem colisor de cilindro, entao o plugin gera
    /// um cilindro (24 lados) e poe na malha visual (EPC_Renderer._mesh) e no colisor convexo (DOTSColliderConvex._mesh)
    /// antes do jogo montar o prefab; e troca o material fisico (atrito alto, "o maior atrito vence").</summary>
    [HarmonyPatch]
    internal static class Rodas
    {
        // nome -> diametro (m); largura 0,5 m, eixo em Y
        static readonly Dictionary<string, float> DIAMETRO = new Dictionary<string, float> {
            { "SC_RodaSportsP", 1f }, { "SC_RodaSportsM", 1.5f }, { "SC_RodaSportsG", 2f },
            { "SC_RodaOffroadP", 1f }, { "SC_RodaOffroadM", 1.5f }, { "SC_RodaOffroadG", 2f } };
        public static float AtritoSports = 1.5f, AtritoOffroad = 2.2f;
        const int Lados = 24; const float Largura = 0.5f;

        static string Base(string nome) => nome != null && nome.EndsWith(" M") ? nome.Substring(0, nome.Length - 2) : nome;   // 0.12.14: versao espelhada
        public static bool EhRoda(string nome) => nome != null && DIAMETRO.ContainsKey(Base(nome));

        /// <summary>Callback do Components Framework (antes do Core montar os prefabs).</summary>
        public static void Configurar(EPC_SpaceshipComponent epc, Core core)
        {
            Juntas.PrepararCabo(epc, core);   // 0.15.0: Drive Shaft = malhas do cabo de plasma do jogo (antes do Religar)
            JogoMalhas.Religar(epc, core);   // 0.12.15: marcadores -> malhas do jogo (todos os blocos do mod passam por aqui)
            try
            {
                if (epc == null || !DIAMETRO.TryGetValue(Base(epc.name), out var d)) return;
                float r = d / 2f;
                var visual = Cilindro(r, Largura, Lados, true, epc.name + "_visual");
                var colisao = Cilindro(r, Largura, Lados, false, epc.name + "_colisao");
                int nr = 0, nc = 0;
                // 0.9.0: o prefab ja traz a malha propria (pneu + aro + cubo, gen_meshes); so troca se estiver vazia (bundle antigo)
                foreach (var ren in epc.GetComponentsInChildren<EPC_Renderer>(true)) { if (ren._mesh == null) { ren._mesh = visual; nr++; } }
                foreach (var col in epc.GetComponentsInChildren<DOTSColliderConvex>(true)) { col._mesh = colisao; nc++; }
                Registro.Info(epc.name + ": wheel, " + d.ToString("F1") + " m (" + nr + " visual mesh(es), " + nc + " collider(s))");
            }
            catch (Exception ex) { Registro.Erro("wheel " + (epc != null ? epc.name : "?") + ": " + ex); }
        }

        /// <summary>Material fisico das rodas: atrito alto; no contato vence o maior (o chao do jogo tem atrito normal).</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(EPC_SpaceshipComponent), nameof(EPC_SpaceshipComponent.GetPhysicsMaterial))]
        static void Material(EPC_SpaceshipComponent __instance, ref Unity.Physics.Material __result)
        {
            try
            {
                if (__instance == null || !EhRoda(__instance.name)) return;
                __result.Friction = __instance.name.StartsWith("SC_RodaOffroad") ? AtritoOffroad : AtritoSports;
                __result.FrictionCombinePolicy = Unity.Physics.Material.CombinePolicy.Maximum;
            }
            catch (Exception) { }
        }

        /// <summary>Cilindro centrado na origem, eixo Y. Visual: faces separadas (normais certas, UV); colisao: so os 2*n pontos.</summary>
        static Mesh Cilindro(float r, float h, int n, bool visual, string nome)
        {
            var m = new Mesh(); m.name = nome; m.hideFlags = HideFlags.HideAndDontSave;
            var vs = new List<Vector3>(); var ns = new List<Vector3>(); var uv = new List<Vector2>(); var ts = new List<int>();
            float y0 = -h / 2f, y1 = h / 2f;
            if (!visual)
            {
                for (int i = 0; i < n; i++) { float a = 2f * Mathf.PI * i / n; float x = r * Mathf.Cos(a), z = r * Mathf.Sin(a); vs.Add(new Vector3(x, y0, z)); vs.Add(new Vector3(x, y1, z)); }
                for (int i = 0; i < n; i++) { int a0 = 2 * i, a1 = 2 * i + 1, b0 = 2 * ((i + 1) % n), b1 = b0 + 1; ts.Add(a0); ts.Add(a1); ts.Add(b0); ts.Add(b0); ts.Add(a1); ts.Add(b1); }
                for (int i = 1; i < n - 1; i++) { ts.Add(0); ts.Add(2 * (i + 1)); ts.Add(2 * i); ts.Add(1); ts.Add(2 * i + 1); ts.Add(2 * (i + 1) + 1); }
            }
            else
            {
                // lateral (banda de rodagem)
                for (int i = 0; i <= n; i++)
                {
                    float a = 2f * Mathf.PI * i / n; float c = Mathf.Cos(a), s = Mathf.Sin(a);
                    vs.Add(new Vector3(r * c, y0, r * s)); ns.Add(new Vector3(c, 0, s)); uv.Add(new Vector2((float)i / n, 0));
                    vs.Add(new Vector3(r * c, y1, r * s)); ns.Add(new Vector3(c, 0, s)); uv.Add(new Vector2((float)i / n, 1));
                }
                for (int i = 0; i < n; i++) { int a0 = 2 * i, a1 = a0 + 1, b0 = a0 + 2, b1 = a0 + 3; ts.Add(a0); ts.Add(a1); ts.Add(b0); ts.Add(b0); ts.Add(a1); ts.Add(b1); }
                // tampas
                foreach (var (y, up) in new[] { (y0, -1f), (y1, 1f) })
                {
                    int centro = vs.Count; vs.Add(new Vector3(0, y, 0)); ns.Add(new Vector3(0, up, 0)); uv.Add(new Vector2(0.5f, 0.5f));
                    for (int i = 0; i < n; i++) { float a = 2f * Mathf.PI * i / n; vs.Add(new Vector3(r * Mathf.Cos(a), y, r * Mathf.Sin(a))); ns.Add(new Vector3(0, up, 0)); uv.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a))); }
                    for (int i = 0; i < n; i++) { int p = centro + 1 + i, q = centro + 1 + (i + 1) % n; if (up > 0) { ts.Add(centro); ts.Add(q); ts.Add(p); } else { ts.Add(centro); ts.Add(p); ts.Add(q); } }
                }
            }
            if (visual) { int nt = ts.Count; for (int i = 0; i < nt; i += 3) { ts.Add(ts[i]); ts.Add(ts[i + 2]); ts.Add(ts[i + 1]); } }   // dupla face: nao depende do sentido
            m.vertices = vs.ToArray(); m.triangles = ts.ToArray();
            if (visual) { m.normals = ns.ToArray(); m.uv = uv.ToArray(); m.RecalculateTangents(); }
            m.RecalculateBounds();
            return m;
        }
    }
}
