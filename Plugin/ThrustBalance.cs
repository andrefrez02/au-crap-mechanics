using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CrapMechanics
{
    /// <summary>0.31.9 (Andre, 31/10: the flight trace showed his ship's lift thrusters' line 60 cm in front of the centre of mass and the main
    /// thrusters' line 33 cm above it — "do it, only on H"). In the GARAGE, only while the game's H display is on (Garage.HIsOn, the garage robot
    /// with the mass), a panel in the top right corner: the ship's mass and, for each group of thrusters pushing the same way (up, forward...),
    /// where its thrust line passes relative to the centre of mass, which way it turns the ship and how far to move it. Mass and centre of mass come
    /// from each block's SCMass (_mass +0, _centerOfMass +4, in the block's axes) at its GarageTransform; thrust from SCTypeThruster._maxForce
    /// (the force the mod's sliders leave in it), at full throttle. Reads only; changes nothing.</summary>
    internal static class ThrustBalance
    {
        public static bool Enabled = false;   // [Debug] thrust_balance_on_h (off by default)
        static TypeIndex tiMass, tiGT; static bool ok, failed; static float t = 9f; static int warnings;
        static int offM = -1, offC = -1;
        static string[] lines;   // what OnGUI draws (built in the mod cycle: OnGUI never touches the ECS)

        static bool Prepare()
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiMass = TypeManager.GetTypeIndex(Il2CppType.Of<SCMass>());
                tiGT = TypeManager.GetTypeIndex(Il2CppType.Of<GarageTransform>());
                offM = GameFields.Of(typeof(SCMass), "_mass", 0); offC = GameFields.Of(typeof(SCMass), "_centerOfMass", 4);
                if (offM < 0 || offC < 0) throw new InvalidOperationException("SCMass fields not found");
                ok = true;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("thrust balance: off (" + ex.Message + ")"); }
            return ok;
        }

        public static void Step(EntityManager em, float dt)
        {
            if (!Enabled || !Garage.HIsOn) { lines = null; return; }
            t += dt; if (t < 0.5f) return; t = 0f;
            if (!Prepare() || !Thrusters.Ready) { lines = null; return; }
            try { lines = Build(em); }
            catch (Exception ex) { lines = null; if (warnings++ < 3) ModLog.Warn("thrust balance: " + ex.Message); }
        }

        class Group { public float3 dir; public float3 F, T; public int n; public Dictionary<string, int> kinds = new Dictionary<string, int>(); }
        static readonly float3[] DIRS = { new float3(0, 1, 0), new float3(0, -1, 0), new float3(1, 0, 0), new float3(-1, 0, 0), new float3(0, 0, 1), new float3(0, 0, -1) };
        static readonly Dictionary<ulong, string> names = new Dictionary<ulong, string>(); static bool namesDone;
        static string Name(EntityManager em, Entity e)
        {
            if (!namesDone && BlockIndex.Ready)
            {
                namesDone = true;
                foreach (var n in BlockIndex.AllPrefabs)
                    if (n.IndexOf("Thrust", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Fan", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("RCS", StringComparison.OrdinalIgnoreCase) >= 0)
                    { ulong h = Ecs.HashPrefab(n); if (h != 0) names[h] = n.StartsWith("SC_") ? n.Substring(3) : n; }
            }
            return names.TryGetValue(Ecs.Prefab(em, e), out var nm) ? nm : "other";
        }

        static unsafe string[] Build(EntityManager em)
        {
            double M = 0; double3 S = default;
            var thr = new List<(Entity e, float3 pos, float3 f)>();
            foreach (var b in Garage.Blocks(em))
            {
                if (!Ecs.Alive(em, b) || Ecs.IsPart(em, Ecs.Parent(em, b))) continue;   // launched copies are not the garage ship
                var gp = (RigidTransform*)em.GetComponentDataRawRO(b, tiGT); if (gp == null) continue; var g = *gp;
                if (Ecs.HasC(em, b, tiMass))
                {
                    var mp = (byte*)em.GetComponentDataRawRO(b, tiMass);
                    float m = *(float*)(mp + offM); float3 c = *(float3*)(mp + offC);
                    if (m > 0f && m < 1e7f && !float.IsNaN(m)) { M += m; S += (double3)(g.pos + math.rotate(g.rot, c)) * m; }
                }
                if (Ecs.HasC(em, b, Thrusters.Ti))
                {
                    var fl = *(float3*)((byte*)em.GetComponentDataRawRO(b, Thrusters.Ti) + Thrusters.MaxForceOffset);
                    if (math.lengthsq(fl) > 1f) thr.Add((b, g.pos, math.rotate(g.rot, fl)));
                }
            }
            if (M <= 0) return null;
            var com = (float3)(S / M);
            var r = new List<string> { "THRUST BALANCE (H)   ship " + (M / 1000.0).ToString("F1") + " t, " + thr.Count + " thruster(s), full throttle" };
            if (thr.Count == 0) { r.Add("no thrusters"); return r.ToArray(); }
            // groups: by the garage axis each thruster pushes along most
            var groups = new Dictionary<int, Group>();
            foreach (var (e, pos, f) in thr)
            {
                float3 d = math.normalize(f); int best = 0; float bd = -2f;
                for (int i = 0; i < 6; i++) { float x = math.dot(d, DIRS[i]); if (x > bd) { bd = x; best = i; } }
                if (!groups.TryGetValue(best, out var gr)) groups[best] = gr = new Group { dir = DIRS[best] };
                gr.F += f; gr.T += math.cross(pos - com, f); gr.n++;
                string k = Name(em, e); gr.kinds.TryGetValue(k, out int c); gr.kinds[k] = c + 1;
            }
            // "front" = where the strongest sideways group pushes (the main thrusters); up = +Y of the garage
            float3 up = new float3(0, 1, 0), front = new float3(0, 0, 1); float bestF = 0f; string frontAxis = "+Z (no forward thrusters: assumed)";
            foreach (var kv in groups) { if (kv.Key <= 1) continue; float fm = math.length(kv.Value.F); if (fm > bestF) { bestF = fm; front = kv.Value.dir; frontAxis = AxisName(kv.Key); } }
            float3 right = math.cross(up, front);
            r.Add("front = garage " + frontAxis + " (where the strongest forward thrusters push); offsets are of the thrust line from the centre of mass");
            var order = new List<Group>(groups.Values); order.Sort((a, b) => math.length(b.F).CompareTo(math.length(a.F)));
            float3 FT = default, TT = default;
            foreach (var gr in order)
            {
                FT += gr.F; TT += gr.T;
                var kinds = new List<string>(); foreach (var kv in gr.kinds) kinds.Add(kv.Key + " x" + kv.Value);
                r.Add(Label(gr.dir, up, front, right) + " — " + string.Join(", ", kinds) + ", " + (math.length(gr.F) / 1000f).ToString("F0") + " kN");
                r.Add("      " + Describe(gr.F, gr.T, up, front, right, true));
            }
            if (order.Count > 1) r.Add("ALL AT ONCE — " + (math.length(FT) / 1000f).ToString("F0") + " kN: " + Describe(FT, TT, up, front, right, false));
            return r.ToArray();
        }
        static string AxisName(int i) => i == 0 ? "+Y" : i == 1 ? "-Y" : i == 2 ? "+X" : i == 3 ? "-X" : i == 4 ? "+Z" : "-Z";
        static string Label(float3 d, float3 up, float3 front, float3 right)
        {
            if (math.dot(d, up) > 0.5f) return "UP"; if (math.dot(d, up) < -0.5f) return "DOWN";
            if (math.dot(d, front) > 0.5f) return "FORWARD"; if (math.dot(d, front) < -0.5f) return "BACKWARD";
            return math.dot(d, right) > 0f ? "RIGHT" : "LEFT";
        }
        /// <summary>Where the thrust line passes (cm in front/behind, above/below, right/left of the centre of mass), the turn it makes and how to fix it.</summary>
        static string Describe(float3 F, float3 T, float3 up, float3 front, float3 right, bool fix)
        {
            float f2 = math.lengthsq(F); if (f2 < 1f) return "no thrust";
            float3 at = math.cross(F, T) / f2;   // from the centre of mass to the nearest point of the thrust line
            float cf = math.dot(at, front) * 100f, cu = math.dot(at, up) * 100f, cr = math.dot(at, right) * 100f;
            var where = new List<string>();
            if (Math.Abs(cf) >= 1f) where.Add(Math.Abs(cf).ToString("F0") + " cm " + (cf > 0 ? "in front" : "behind"));
            if (Math.Abs(cu) >= 1f) where.Add(Math.Abs(cu).ToString("F0") + " cm " + (cu > 0 ? "above" : "below"));
            if (Math.Abs(cr) >= 1f) where.Add(Math.Abs(cr).ToString("F0") + " cm " + (cr > 0 ? "right" : "left"));
            float tq = math.length(T);
            if (where.Count == 0 || tq < 1000f) return "line through the centre of mass (within 1 cm): balanced";
            // which way it turns (as if the ship turned the same in every axis): the motion of the nose and of the right side
            float3 td = T / tq; float3 nose = math.cross(td, front), side = math.cross(td, right);
            var turn = new List<string>();
            float pitch = math.dot(nose, up), yaw = math.dot(nose, right), roll = math.dot(side, up);
            if (Math.Abs(pitch) > 0.3f) turn.Add(pitch > 0 ? "nose UP" : "nose DOWN");
            if (Math.Abs(yaw) > 0.3f) turn.Add(yaw > 0 ? "nose RIGHT" : "nose LEFT");
            if (Math.Abs(roll) > 0.3f) turn.Add(roll > 0 ? "rolls LEFT (right side up)" : "rolls RIGHT (left side up)");
            string s = "passes " + string.Join(", ", where) + " -> " + string.Join(" + ", turn) + " (" + (tq / 1000f).ToString("F0") + " kN·m)";
            if (fix)
            {
                var mv = new List<string>();
                if (Math.Abs(cf) >= 1f) mv.Add(Math.Abs(cf).ToString("F0") + " cm " + (cf > 0 ? "back" : "forward"));
                if (Math.Abs(cu) >= 1f) mv.Add(Math.Abs(cu).ToString("F0") + " cm " + (cu > 0 ? "down" : "up"));
                if (Math.Abs(cr) >= 1f) mv.Add(Math.Abs(cr).ToString("F0") + " cm " + (cr > 0 ? "left" : "right"));
                s += " — move them " + string.Join(", ", mv) + " (or the centre of mass the other way)";
            }
            return s;
        }

        // ---- panel (OnGUI)
        static GUIStyle st;
        public static void Draw()
        {
            var l = lines; if (l == null || l.Length == 0) return;
            if (st == null) { st = new GUIStyle(); st.fontSize = 14; var n = st.normal; n.textColor = Color.white; st.normal = n; st.wordWrap = false; }
            float lh = 20f, w = Math.Min(980f, Screen.width - 24f), x = Screen.width - w - 12f, y = 60f;
            GUI.Box(new Rect(x, y, w, lh * l.Length + 10f), ""); GUI.Box(new Rect(x, y, w, lh * l.Length + 10f), "");
            for (int i = 0; i < l.Length; i++) GUI.Label(new Rect(x + 8f, y + 5f + i * lh, w - 16f, lh), l[i], st);
        }
        public static void Forget() { lines = null; }
    }
}
