using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mecanica
{
    /// <summary>0.12.15: o bundle do mod NAO leva malha do jogo. Os prefabs apontam pra marcadores proprios "Jogo_&lt;nome&gt;"
    /// (plugues das portas, partes do seat, medidor e valvula do tanque); aqui, no configure do Components Framework (antes do
    /// Core montar os prefabs), cada marcador e' trocado pela malha de verdade do jogo, achada pelo nome nos componentes do jogo
    /// que ja estao no catalogo (Core._spaceshipComponents).</summary>
    internal static class JogoMalhas
    {
        const string Prefixo = "Jogo_";
        static Dictionary<string, Mesh> doJogo; static readonly Dictionary<string, Material> textos = new Dictionary<string, Material>(); static int trocadas, faltando, textosTrocados; static readonly HashSet<string> avisadas = new HashSet<string>();

        static void Indexar(Core core)
        {
            doJogo = new Dictionary<string, Mesh>();
            try
            {
                var lista = core._spaceshipComponents; if (lista == null) return;
                for (int i = 0; i < lista.Length; i++)
                {
                    var epc = lista[i]; if (epc == null) continue;
                    string n = epc.name ?? ""; if (n.StartsWith("SC_") && Juntas.EhDoMod(n)) continue;
                    foreach (var ps in epc.GetComponentsInChildren<EPC_ParticleSystem>(true))   // 0.18.0: malha das particulas (mini propulsor)
                    {
                        var m = ps._mesh; if (m != null && !string.IsNullOrEmpty(m.name) && !m.name.StartsWith(Prefixo) && !doJogo.ContainsKey(m.name)) doJogo[m.name] = m;
                    }
                    foreach (var tx in epc.GetComponentsInChildren<EPC_RendererText3D>(true))   // 0.17.1: materiais de texto do jogo
                    {
                        var mt = tx._material; if (mt != null && !string.IsNullOrEmpty(mt.name) && !textos.ContainsKey(mt.name)) textos[mt.name] = mt;
                    }
                    foreach (var r in epc.GetComponentsInChildren<EPC_Renderer>(true))
                    {
                        var m = r._mesh; if (m == null) continue;
                        string mn = m.name; if (string.IsNullOrEmpty(mn) || mn.StartsWith(Prefixo) || doJogo.ContainsKey(mn)) continue;
                        doJogo[mn] = m;
                    }
                }
                Registro.Info("game meshes: " + doJogo.Count + " indexed from the game's components");
            }
            catch (Exception ex) { Registro.Aviso("game meshes: indexing failed: " + ex.Message); }
        }

        /// <summary>0.22.2: malha do jogo pelo nome (null se nao indexou ainda / nao existe) — Anel.cs troca o plugue das portas de passagem.</summary>
        public static Mesh Malha(string nome) { if (doJogo == null) return null; return doJogo.TryGetValue(nome, out var m) ? m : null; }

        /// <summary>0.23.2: o Core que o Components Framework passa no configure — o Props.cs usa pra achar os componentes depois.</summary>
        public static Core Nucleo;
        public static void Religar(EPC_SpaceshipComponent epc, Core core)
        {
            if (core != null) Nucleo = core;
            if (epc == null) return;
            if (doJogo == null) Indexar(core);
            try
            {
                // 0.17.1: texto 3D (numero do canal do Shaft Transmitter): o MaterialRelinker do CF so religa EPC_Renderer; o material do
                // texto ficava a copia do bundle (shader vazio) = texto invisivel. Troca pelo material vivo do jogo com o mesmo nome.
                foreach (var tx in epc.GetComponentsInChildren<EPC_RendererText3D>(true))
                {
                    var mt = tx._material; if (mt == null || string.IsNullOrEmpty(mt.name)) continue;
                    string nm = mt.name.Replace(" (Instance)", "");
                    if (textos.TryGetValue(nm, out var vivo) && vivo.Pointer != mt.Pointer) { tx._material = vivo; textosTrocados++; }
                    else if (!textos.ContainsKey(nm) && avisadas.Add("text:" + nm)) Registro.Aviso("game meshes: text material '" + nm + "' not found in the game (" + epc.name + " text may be invisible)");
                }
                foreach (var r in epc.GetComponentsInChildren<EPC_Renderer>(true))
                {
                    var m = r._mesh; if (m == null || m.name == null || !m.name.StartsWith(Prefixo)) continue;
                    var real = Achar(m.name.Substring(Prefixo.Length), epc.name);
                    if (real != null) { r._mesh = real; trocadas++; }
                }
                // 0.18.0: sistema de particulas com marcador (mini propulsor: chama do Small Electric Thruster)
                foreach (var ps in epc.GetComponentsInChildren<EPC_ParticleSystem>(true))
                {
                    var m = ps._mesh; if (m == null || m.name == null || !m.name.StartsWith(Prefixo)) continue;
                    var real = Achar(m.name.Substring(Prefixo.Length), epc.name);
                    if (real != null) { ps._mesh = real; trocadas++; }
                }
                // 0.16.0: colisor convexo com marcador (Shaft Transmitter = colisor do transmitter do jogo)
                foreach (var c in epc.GetComponentsInChildren<DOTSColliderConvex>(true))
                {
                    var m = c._mesh; if (m == null || m.name == null || !m.name.StartsWith(Prefixo)) continue;
                    var real = Achar(m.name.Substring(Prefixo.Length), epc.name);
                    if (real != null) { c._mesh = real; trocadas++; }
                }
            }
            catch (Exception ex) { Registro.Aviso("game meshes: " + epc.name + ": " + ex.Message); }
        }
        /// <summary>Malha do jogo pelo nome; "Nome~rrggbb" = copia recolorida (plugues de eixo, 0.16.0), "Nome~mix" = metade entrada
        /// (x local &lt; 0) e metade saida.</summary>
        static Mesh Achar(string alvo, string quem)
        {
            if (doJogo.TryGetValue(alvo, out var real)) return real;
            int t = alvo.IndexOf('~');
            if (t > 0 && doJogo.TryGetValue(alvo.Substring(0, t), out var baseM))
            {
                try { var v = Recolorir(baseM, alvo.Substring(t + 1)); doJogo[alvo] = v; return v; }
                catch (Exception ex) { if (avisadas.Add(alvo)) Registro.Aviso("game meshes: could not recolor '" + alvo + "': " + ex.Message + " (using the plain one)"); return baseM; }
            }
            faltando++; if (avisadas.Add(alvo)) Registro.Aviso("game meshes: '" + alvo + "' not found (" + quem + " will lack this visual piece)");
            return null;
        }

        public static readonly Color32 CorEntrada = new Color32(0xeb, 0xb4, 0xa2, 0), CorSaida = new Color32(0x64, 0x2d, 0x9a, 0);   // Andre: entrada #ebb4a2, saida #642d9a
        static bool Escuro(Color32 c) => c.r < 40 && c.g < 10 && c.b < 10;   // miolo dos plugues do jogo (26,0,0): fica

        static Mesh Recolorir(Mesh orig, string variante)
        {
            var vs = orig.vertices; var ns = orig.normals; var cs = orig.colors32; var ts = orig.triangles;
            if (cs == null || cs.Length != vs.Length) throw new Exception("the mesh has no vertex colors");
            bool mix = variante == "mix"; Color32 cor = mix ? CorSaida : Hex(variante);
            var nv = new List<Vector3>(); var nn = new List<Vector3>(); var nc = new List<Color32>(); var nt = new List<int>();
            bool temN = ns != null && ns.Length == vs.Length;
            if (mix)
            {
                for (int i = 0; i + 2 < ts.Length; i += 3)   // vertices separados por triangulo: a divisao fica reta
                {
                    float cx = (vs[ts[i]].x + vs[ts[i + 1]].x + vs[ts[i + 2]].x) / 3f; var c = cx < 0f ? CorEntrada : CorSaida;
                    for (int k = 0; k < 3; k++)
                    {
                        int j = ts[i + k]; nv.Add(vs[j]); if (temN) nn.Add(ns[j]);
                        var o = cs[j]; nc.Add(Escuro(o) ? o : new Color32(c.r, c.g, c.b, o.a)); nt.Add(nv.Count - 1);
                    }
                }
            }
            else
            {
                for (int j = 0; j < vs.Length; j++) { nv.Add(vs[j]); if (temN) nn.Add(ns[j]); var o = cs[j]; nc.Add(Escuro(o) ? o : new Color32(cor.r, cor.g, cor.b, o.a)); }
                for (int i = 0; i < ts.Length; i++) nt.Add(ts[i]);
            }
            var m = new Mesh(); m.name = orig.name + "~" + variante; m.hideFlags = HideFlags.HideAndDontSave;
            m.vertices = nv.ToArray(); if (temN) m.normals = nn.ToArray(); m.colors32 = nc.ToArray(); m.triangles = nt.ToArray();
            m.RecalculateBounds();
            return m;
        }
        static Color32 Hex(string h)
        {
            int v = Convert.ToInt32(h, 16);
            return new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 0);
        }

        public static void Resumo() { Registro.Info("game meshes: " + trocadas + " marker(s) replaced with the game mesh, " + textosTrocados + " text material(s) relinked" + (faltando > 0 ? ", " + faltando + " without a mesh" : "")); }
    }
}
