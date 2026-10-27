using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrapMechanics
{
    /// <summary>0.12.15: the mod's bundle does NOT carry any game mesh. The prefabs point to markers of their own "Game_&lt;name&gt;"
    /// (port plugs, seat parts, the tank's gauge and valve); here, in the Components Framework configure (before the
    /// Core builds the prefabs), each marker is swapped for the game's real mesh, found by name in the game components
    /// already in the catalog (Core._spaceshipComponents).</summary>
    internal static class GameMeshes
    {
        // markers are "Game_<name>"; bundles built before the English rename (0.27.0) still carry "Jogo_<name>"
        static readonly string[] Prefixes = { "Game_", "Jogo_" };
        /// <summary>The game mesh name behind a marker mesh name, or null if it isn't a marker.</summary>
        static string Marker(string n) { if (n != null) foreach (var p in Prefixes) if (n.StartsWith(p)) return n.Substring(p.Length); return null; }
        static Dictionary<string, Mesh> fromGame; static readonly Dictionary<string, Material> texts = new Dictionary<string, Material>(); static int swapped, lacking, textsSwapped; static readonly HashSet<string> warned = new HashSet<string>();

        static void Index(Core core)
        {
            fromGame = new Dictionary<string, Mesh>();
            try
            {
                var list = core._spaceshipComponents; if (list == null) return;
                for (int i = 0; i < list.Length; i++)
                {
                    var epc = list[i]; if (epc == null) continue;
                    string n = epc.name ?? ""; if (n.StartsWith("SC_") && JointSystem.IsModBlock(n)) continue;
                    foreach (var ps in epc.GetComponentsInChildren<EPC_ParticleSystem>(true))   // 0.18.0: particle mesh (mini thruster)
                    {
                        var m = ps._mesh; if (m != null && !string.IsNullOrEmpty(m.name) && Marker(m.name) == null && !fromGame.ContainsKey(m.name)) fromGame[m.name] = m;
                    }
                    foreach (var tx in epc.GetComponentsInChildren<EPC_RendererText3D>(true))   // 0.17.1: the game's text materials
                    {
                        var mt = tx._material; if (mt != null && !string.IsNullOrEmpty(mt.name) && !texts.ContainsKey(mt.name)) texts[mt.name] = mt;
                    }
                    foreach (var r in epc.GetComponentsInChildren<EPC_Renderer>(true))
                    {
                        var m = r._mesh; if (m == null) continue;
                        string mn = m.name; if (string.IsNullOrEmpty(mn) || Marker(mn) != null || fromGame.ContainsKey(mn)) continue;
                        fromGame[mn] = m;
                    }
                }
                ModLog.Info("game meshes: " + fromGame.Count + " indexed from the game's components");
            }
            catch (Exception ex) { ModLog.Warn("game meshes: indexing failed: " + ex.Message); }
        }

        /// <summary>0.22.2: the game's mesh by name (null if not indexed yet / doesn't exist) — SlipRings.cs swaps the plug of the pass-through ports.</summary>
        public static Mesh MeshOf(string name) { if (fromGame == null) return null; return fromGame.TryGetValue(name, out var m) ? m : null; }

        /// <summary>0.23.2: the Core that Components Framework passes in the configure — Props.cs uses it to find the components later.</summary>
        public static Core CoreEntity;
        public static void Relink(EPC_SpaceshipComponent epc, Core core)
        {
            if (core != null) CoreEntity = core;
            if (epc == null) return;
            if (fromGame == null) Index(core);
            try
            {
                // 0.17.1: 3D text (the Shaft Transmitter's channel number): CF's MaterialRelinker only relinks EPC_Renderer; the text's
                // material stayed the bundle's copy (empty shader) = invisible text. Swap it for the game's live material with the same name.
                foreach (var tx in epc.GetComponentsInChildren<EPC_RendererText3D>(true))
                {
                    var mt = tx._material; if (mt == null || string.IsNullOrEmpty(mt.name)) continue;
                    string nm = mt.name.Replace(" (Instance)", "");
                    if (texts.TryGetValue(nm, out var aliveV) && aliveV.Pointer != mt.Pointer) { tx._material = aliveV; textsSwapped++; }
                    else if (!texts.ContainsKey(nm) && warned.Add("text:" + nm)) ModLog.Warn("game meshes: text material '" + nm + "' not found in the game (" + epc.name + " text may be invisible)");
                }
                foreach (var r in epc.GetComponentsInChildren<EPC_Renderer>(true))
                {
                    var m = r._mesh; var marker = Marker(m?.name); if (marker == null) continue;
                    var real = Find(marker, epc.name);
                    if (real != null) { r._mesh = real; swapped++; }
                }
                // 0.18.0: particle system with a marker (mini thruster: flame of the Small Electric Thruster)
                foreach (var ps in epc.GetComponentsInChildren<EPC_ParticleSystem>(true))
                {
                    var m = ps._mesh; var marker = Marker(m?.name); if (marker == null) continue;
                    var real = Find(marker, epc.name);
                    if (real != null) { ps._mesh = real; swapped++; }
                }
                // 0.16.0: convex collider with a marker (Shaft Transmitter = collider of the game's transmitter)
                foreach (var c in epc.GetComponentsInChildren<DOTSColliderConvex>(true))
                {
                    var m = c._mesh; var marker = Marker(m?.name); if (marker == null) continue;
                    var real = Find(marker, epc.name);
                    if (real != null) { c._mesh = real; swapped++; }
                }
            }
            catch (Exception ex) { ModLog.Warn("game meshes: " + epc.name + ": " + ex.Message); }
        }
        /// <summary>The game's mesh by name; "Name~rrggbb" = recoloured copy (shaft plugs, 0.16.0), "Name~mix" = half input
        /// (local x &lt; 0) and half output.</summary>
        static Mesh Find(string target, string who)
        {
            if (fromGame.TryGetValue(target, out var real)) return real;
            int t = target.IndexOf('~');
            if (t > 0 && fromGame.TryGetValue(target.Substring(0, t), out var baseM))
            {
                try { var v = Recolor(baseM, target.Substring(t + 1)); fromGame[target] = v; return v; }
                catch (Exception ex) { if (warned.Add(target)) ModLog.Warn("game meshes: could not recolor '" + target + "': " + ex.Message + " (using the plain one)"); return baseM; }
            }
            lacking++; if (warned.Add(target)) ModLog.Warn("game meshes: '" + target + "' not found (" + who + " will lack this visual piece)");
            return null;
        }

        public static readonly Color32 InputColor = new Color32(0xeb, 0xb4, 0xa2, 0), OutputColor = new Color32(0x64, 0x2d, 0x9a, 0);   // Andre: input #ebb4a2, output #642d9a
        static bool Dark(Color32 c) => c.r < 40 && c.g < 10 && c.b < 10;   // core of the game's plugs (26,0,0): stays

        static Mesh Recolor(Mesh orig, string variant)
        {
            var vs = orig.vertices; var ns = orig.normals; var cs = orig.colors32; var ts = orig.triangles;
            if (cs == null || cs.Length != vs.Length) throw new Exception("the mesh has no vertex colors");
            bool mix = variant == "mix"; Color32 color = mix ? OutputColor : Hex(variant);
            var nv = new List<Vector3>(); var nn = new List<Vector3>(); var nc = new List<Color32>(); var nt = new List<int>();
            bool hasN = ns != null && ns.Length == vs.Length;
            if (mix)
            {
                for (int i = 0; i + 2 < ts.Length; i += 3)   // vertices split per triangle: the division stays straight
                {
                    float cx = (vs[ts[i]].x + vs[ts[i + 1]].x + vs[ts[i + 2]].x) / 3f; var c = cx < 0f ? InputColor : OutputColor;
                    for (int k = 0; k < 3; k++)
                    {
                        int j = ts[i + k]; nv.Add(vs[j]); if (hasN) nn.Add(ns[j]);
                        var o = cs[j]; nc.Add(Dark(o) ? o : new Color32(c.r, c.g, c.b, o.a)); nt.Add(nv.Count - 1);
                    }
                }
            }
            else
            {
                for (int j = 0; j < vs.Length; j++) { nv.Add(vs[j]); if (hasN) nn.Add(ns[j]); var o = cs[j]; nc.Add(Dark(o) ? o : new Color32(color.r, color.g, color.b, o.a)); }
                for (int i = 0; i < ts.Length; i++) nt.Add(ts[i]);
            }
            var m = new Mesh(); m.name = orig.name + "~" + variant; m.hideFlags = HideFlags.HideAndDontSave;
            m.vertices = nv.ToArray(); if (hasN) m.normals = nn.ToArray(); m.colors32 = nc.ToArray(); m.triangles = nt.ToArray();
            m.RecalculateBounds();
            return m;
        }
        static Color32 Hex(string h)
        {
            int v = Convert.ToInt32(h, 16);
            return new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 0);
        }

        public static void Summary() { ModLog.Info("game meshes: " + swapped + " marker(s) replaced with the game mesh, " + textsSwapped + " text material(s) relinked" + (lacking > 0 ? ", " + lacking + " without a mesh" : "")); }
    }
}
