using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CrapMechanics
{
    /// <summary>Wheels: passive blocks (they go on top of a Bearing). The game has no cylinder collider, so the plugin generates
    /// a cylinder (24 sides) and puts it in the visual mesh (EPC_Renderer._mesh) and in the convex collider (DOTSColliderConvex._mesh)
    /// before the game builds the prefab; and swaps the physics material (high friction, "the highest friction wins").</summary>
    [HarmonyPatch]
    internal static class Wheels
    {
        // name -> diameter (m); width 0.5 m, axis along Y
        static readonly Dictionary<string, float> DIAMETER = new Dictionary<string, float> {
            { "SC_WheelSportsSmall", 1f }, { "SC_WheelSportsMedium", 1.5f }, { "SC_WheelSportsLarge", 2f },
            { "SC_WheelOffroadSmall", 1f }, { "SC_WheelOffroadMedium", 1.5f }, { "SC_WheelOffroadLarge", 2f } };
        public static float SportsFriction = 1.5f, OffroadFriction = 2.2f;
        const int Sides = 24; const float Width = 0.5f;

        static string Base(string name) => name != null && name.EndsWith(" M") ? name.Substring(0, name.Length - 2) : name;   // 0.12.14: mirrored version
        public static bool IsWheel(string name) => name != null && DIAMETER.ContainsKey(Base(name));

        /// <summary>Components Framework callback (before the Core builds the prefabs).</summary>
        public static void Configure(EPC_SpaceshipComponent epc, Core core)
        {
            JointSystem.PrepareCable(epc, core);   // 0.15.0: Drive Shaft = meshes of the game's plasma cable (before Relink)
            GameMeshes.Relink(epc, core);   // 0.12.15: markers -> the game's meshes (every mod block goes through here)
            try
            {
                if (epc == null || !DIAMETER.TryGetValue(Base(epc.name), out var d)) return;
                float r = d / 2f;
                var visual = Cylinder(r, Width, Sides, true, epc.name + "_visual");
                var collision = Cylinder(r, Width, Sides, false, epc.name + "_collision");
                int nr = 0, nc = 0;
                // 0.9.0: the prefab already carries its own mesh (tyre + rim + hub, gen_meshes); only swaps it if it is empty (old bundle)
                foreach (var ren in epc.GetComponentsInChildren<EPC_Renderer>(true)) { if (ren._mesh == null) { ren._mesh = visual; nr++; } }
                foreach (var col in epc.GetComponentsInChildren<DOTSColliderConvex>(true)) { col._mesh = collision; nc++; }
                ModLog.Info(epc.name + ": wheel, " + d.ToString("F1") + " m (" + nr + " visual mesh(es), " + nc + " collider(s))");
            }
            catch (Exception ex) { ModLog.Error("wheel " + (epc != null ? epc.name : "?") + ": " + ex); }
        }

        /// <summary>The wheels' physics material: high friction; on contact the highest wins (the game's ground has normal friction).</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(EPC_SpaceshipComponent), nameof(EPC_SpaceshipComponent.GetPhysicsMaterial))]
        static void Material(EPC_SpaceshipComponent __instance, ref Unity.Physics.Material __result)
        {
            // 0.26.0 (performance): .name comes from the native side (string marshalling on every read) and this postfix runs for every
            // component every time the game builds a part. Now it's ONE read, and the trace line is only built with the trace on.
            try
            {
                if (__instance == null) return;
                var name = __instance.name;
                if (ModLog.Tracing) ModLog.Trace("Wheels.Material " + name);
                if (!IsWheel(name)) return;
                __result.Friction = name.StartsWith("SC_WheelOffroad") ? OffroadFriction : SportsFriction;
                __result.FrictionCombinePolicy = Unity.Physics.Material.CombinePolicy.Maximum;
            }
            catch (Exception) { }
        }

        /// <summary>Cylinder centered at the origin, Y axis. Visual: separate faces (correct normals, UV); collision: only the 2*n points.</summary>
        static Mesh Cylinder(float r, float h, int n, bool visual, string name)
        {
            var m = new Mesh(); m.name = name; m.hideFlags = HideFlags.HideAndDontSave;
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
                // side (tread)
                for (int i = 0; i <= n; i++)
                {
                    float a = 2f * Mathf.PI * i / n; float c = Mathf.Cos(a), s = Mathf.Sin(a);
                    vs.Add(new Vector3(r * c, y0, r * s)); ns.Add(new Vector3(c, 0, s)); uv.Add(new Vector2((float)i / n, 0));
                    vs.Add(new Vector3(r * c, y1, r * s)); ns.Add(new Vector3(c, 0, s)); uv.Add(new Vector2((float)i / n, 1));
                }
                for (int i = 0; i < n; i++) { int a0 = 2 * i, a1 = a0 + 1, b0 = a0 + 2, b1 = a0 + 3; ts.Add(a0); ts.Add(a1); ts.Add(b0); ts.Add(b0); ts.Add(a1); ts.Add(b1); }
                // caps
                foreach (var (y, up) in new[] { (y0, -1f), (y1, 1f) })
                {
                    int center = vs.Count; vs.Add(new Vector3(0, y, 0)); ns.Add(new Vector3(0, up, 0)); uv.Add(new Vector2(0.5f, 0.5f));
                    for (int i = 0; i < n; i++) { float a = 2f * Mathf.PI * i / n; vs.Add(new Vector3(r * Mathf.Cos(a), y, r * Mathf.Sin(a))); ns.Add(new Vector3(0, up, 0)); uv.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a))); }
                    for (int i = 0; i < n; i++) { int p = center + 1 + i, q = center + 1 + (i + 1) % n; if (up > 0) { ts.Add(center); ts.Add(q); ts.Add(p); } else { ts.Add(center); ts.Add(p); ts.Add(q); } }
                }
            }
            if (visual) { int nt = ts.Count; for (int i = 0; i < nt; i += 3) { ts.Add(ts[i]); ts.Add(ts[i + 2]); ts.Add(ts[i + 1]); } }   // double-sided: doesn't depend on the winding
            m.vertices = vs.ToArray(); m.triangles = ts.ToArray();
            if (visual) { m.normals = ns.ToArray(); m.uv = uv.ToArray(); m.RecalculateTangents(); }
            m.RecalculateBounds();
            return m;
        }
    }
}
