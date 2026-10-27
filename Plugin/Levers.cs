using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>0.11.1/0.11.2: mod blocks with game levers inside — SC_Pedal (2 pedals) and SC_SteeringWheel (1 rim).
    /// The game links only ONE lever to the port (SCTypeJoystick has one _actionableLeverEntity) and the joystick root writes its
    /// value into ALL ports of the block. That's why these blocks are generic (root EPC_SpaceshipComponent, no SCTypeJoystick):
    /// the game builds the levers (click, drag, seat binds, sound) and this file copies the value of each one
    /// (ActionableData._value.x) to its output port, in order of x position (left -> port 0, right -> port 1).
    /// Pedals (0.11.2): the game's ordinary one-way lever (EPC_Actionable_DoubleLever, 0..1 = press); the plugin adds
    /// ActionableJoystickReturnToMid so it goes back to 0 by itself when released (and Extended Controller Support treats it as a joystick).</summary>
    internal class LeverBlock
    {
        public Entity block; public string name; public Entity[] levers = new Entity[0]; public Entity[] ports = new Entity[0];
        public float[] latest = new float[4]; public bool childrenLogged, noLeverWarned, returnMidSet; public int warnings;
        public float tFind = 9f; public string[] valuesLogged = new string[4]; public bool viaBelong;
        public bool combines;    // 0.12.1: pedals / turret seat — ONE output = right - left (-1..1)
        public bool onlyDouble;   // 0.12.0: turret seat — only the ordinary levers (triggers); the seat's height lever (EPC_Actionable_Lever) is left out
    }

    internal static class Levers
    {
        static readonly Dictionary<long, LeverBlock> blocks = new Dictionary<long, LeverBlock>();
        static TypeIndex tiData, tiLever, tiDouble, tiBelong, tiReturnMid, tiButton; static EntityQuery qLevers; static bool ok, failed, returnMidOk;

        public static bool Has(long k) => blocks.ContainsKey(k);
        public static void Forget() { blocks.Clear(); }
        public static void Track(Entity e, string name, bool onlyDouble = false) { blocks[Ecs.K(e)] = new LeverBlock { block = e, name = name, onlyDouble = onlyDouble, combines = onlyDouble || name == "SC_Pedal" }; if (!onlyDouble) ModLog.Info(name + " found: block " + e.Index + ":" + e.Version); }

        public static string State(EntityManager em)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in blocks.Values)
            {
                sb.Append("  ").Append(p.name).Append(' ').Append(p.block.Index).Append(": ").Append(p.levers.Length).Append(" lever(s), ").Append(p.ports.Length).Append(" port(s)");
                for (int i = 0; i < p.levers.Length && i < 4; i++) sb.Append(" [").Append(i).Append("]=").Append(p.latest[i].ToString("F2"));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        static bool Prepare(EntityManager em)
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiData = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableData>());
                tiLever = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableLever>());
                tiDouble = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableDoubleLever>());
                tiBelong = TypeManager.GetTypeIndex(Il2CppType.Of<BelongingSC>());
                tiButton = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableButton>());   // 0.12.2: pedals = buttons
                var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(2);
                a[0] = ComponentType.ReadOnly(tiBelong); a[1] = ComponentType.ReadOnly(tiData);
                qLevers = em.CreateEntityQuery(a);
                try { tiReturnMid = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableJoystickReturnToMid>()); returnMidOk = true; }
                catch (Exception ex) { ModLog.Warn("levers: ActionableJoystickReturnToMid type: " + ex.Message + " (the pedals won't return on their own)"); }
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Error("levers: ActionableData/ActionableLever types: " + ex.Message); return false; }
        }

        static bool IsLever(EntityManager em, Entity f) =>
            em.HasComponent(f, ComponentType.ReadOnly(tiData)) && (em.HasComponent(f, ComponentType.ReadOnly(tiLever)) || em.HasComponent(f, ComponentType.ReadOnly(tiDouble)) || em.HasComponent(f, ComponentType.ReadOnly(tiButton)));

        public static void Step(EntityManager em, float dt)
        {
            if (blocks.Count == 0) return;
            if (!Prepare(em)) return;
            var deadSet = new List<long>();
            foreach (var kv in blocks)
            {
                var p = kv.Value;
                try
                {
                    if (!Ecs.Alive(em, p.block)) { deadSet.Add(kv.Key); continue; }
                    BlockStep(em, p, dt);
                }
                catch (Exception ex) { if (p.warnings++ < 5) ModLog.Warn(p.name + " " + p.block.Index + ": " + ex.Message); }
            }
            foreach (var k in deadSet) blocks.Remove(k);
        }

        static unsafe void BlockStep(EntityManager em, LeverBlock p, float dt)
        {
            // finds the levers (block children with ActionableData + Lever/DoubleLever) and the ports, again every 5 s (the game may recreate them)
            p.tFind += dt;
            if (p.tFind >= 5f || p.levers.Length == 0)
            {
                p.tFind = 0f;
                var foundOnes = new List<(Entity e, float x)>();
                foreach (var f in Ecs.Children(em, p.block)) if (IsLever(em, f) && (!p.onlyDouble || em.HasComponent(f, ComponentType.ReadOnly(tiDouble)))) foundOnes.Add((f, Ecs.LT(em, f).Position.x));
                if (foundOnes.Count == 0)   // the levers may have no Parent: find them by BelongingSC._sc (like the labels)
                {
                    foreach (var f in Ecs.QueryList(qLevers))
                    {
                        var b = (Ecs.HasC(em, f, tiBelong) ? (BelongingSC*)em.GetComponentDataRawRO(f, tiBelong) : null);
                        if (b == null || b->_sc.Index != p.block.Index || b->_sc.Version != p.block.Version || !IsLever(em, f)) continue;
                        if (p.onlyDouble && !em.HasComponent(f, ComponentType.ReadOnly(tiDouble))) continue;
                        float x = 0f; try { if (em.HasComponent(f, ComponentType.ReadOnly(Ecs.tiLT))) x = Ecs.LT(em, f).Position.x; } catch (Exception) { }
                        foundOnes.Add((f, x));
                    }
                    if (foundOnes.Count > 0 && !p.viaBelong) { p.viaBelong = true; ModLog.Info(p.name + " " + p.block.Index + ": levers found via BelongingSC (not children of the block)"); }
                }
                foundOnes.Sort((a, b) => System.Math.Abs(a.x - b.x) > 0.01f ? a.x.CompareTo(b.x) : a.e.Index.CompareTo(b.e.Index));   // 0.12.3: stable order (same x = left/right got swapped)
                var list = new Entity[foundOnes.Count]; for (int i = 0; i < foundOnes.Count; i++) list[i] = foundOnes[i].e;
                if (list.Length != p.levers.Length) { ModLog.Info(p.name + " " + p.block.Index + ": " + list.Length + " game lever(s) found"); p.returnMidSet = false; }
                p.levers = list;
                var ps = Ecs.Ports(em, p.block); Array.Sort(ps, (a, b) => Ecs.PortIndex(em, a).CompareTo(Ecs.PortIndex(em, b)));
                p.ports = ps;
                if (!p.childrenLogged && list.Length > 0)
                {
                    p.childrenLogged = true;
                    var sb = new System.Text.StringBuilder(p.name + " " + p.block.Index + ": block children:");
                    foreach (var f in Ecs.Children(em, p.block)) { var lt = Ecs.LT(em, f); sb.Append("\n    ").Append(f.Index).Append(" at (").Append(Ecs.F3(lt.Position)).Append("): ").Append(Ecs.Components(em, f)); }
                    sb.Append("\n  ports: ").Append(ps.Length);
                    for (int i = 0; i < ps.Length; i++) { Ecs.Port(em, ps[i], out var t, out _, out _); sb.Append(" [").Append(i).Append(": ").Append(ps[i].Index).Append(" t").Append(t).Append(" i").Append(Ecs.PortIndex(em, ps[i])).Append("]"); }
                    ModLog.Info(sb.ToString());
                }
                if (list.Length < p.ports.Length && !p.noLeverWarned && Ecs.IsPart(em, Ecs.Parent(em, p.block))) { p.noLeverWarned = true; ModLog.Warn(p.name + " " + p.block.Index + ": expected " + p.ports.Length + " lever(s), found " + list.Length + " — the game didn't build the prefab's lever(s)?"); }
                // 0.11.2: the ordinary lever (DoubleLever, 0..1) gets the joystick's "return to middle": the game brings it back to 0 when released
                if (returnMidOk && !p.returnMidSet)
                {
                    p.returnMidSet = true; int n = 0;
                    foreach (var a in list)
                    {
                        try
                        {
                            if (!em.HasComponent(a, ComponentType.ReadOnly(tiDouble)) || em.HasComponent(a, ComponentType.ReadOnly(tiReturnMid))) continue;
                            em.AddComponent(a, ComponentType.ReadWrite(tiReturnMid)); n++;
                        }
                        catch (Exception ex) { ModLog.Warn(p.name + " " + p.block.Index + ": could not add ReturnToMid to lever " + a.Index + ": " + ex.Message); }
                    }
                    if (n > 0) ModLog.Info(p.name + " " + p.block.Index + ": ReturnToMid added to " + n + " lever(s) (they return to 0 on their own)");
                }
            }
            // value of each lever -> port
            for (int i = 0; i < p.levers.Length && i < 4; i++)
            {
                var a = p.levers[i]; if (!Ecs.Alive(em, a)) continue;
                var v = (Ecs.HasC(em, a, tiData) ? (float*)em.GetComponentDataRawRO(a, tiData) : null); if (v == null) continue;
                float x = v[0];
                if (float.IsNaN(x) || float.IsInfinity(x)) x = 0f;
                if (Math.Abs(x - p.latest[i]) > 0.02f || (x == 0f && p.latest[i] != 0f))
                {
                    // diagnostics: logs the 4 components the first time the lever moves (the game's value is in _value.x — confirmed in the 0.11.1 log)
                    string all = v[0].ToString("F2") + "," + v[1].ToString("F2") + "," + v[2].ToString("F2") + "," + v[3].ToString("F2");
                    if (p.valuesLogged[i] == null) { p.valuesLogged[i] = all; ModLog.Info(p.name + " " + p.block.Index + " lever " + i + ": _value = (" + all + ")"); }
                    p.latest[i] = x;
                }
                if (!p.combines && i < p.ports.Length) Ecs.WritePort(em, p.ports[i], x);
            }
            // 0.19.2: steering wheel with 2 outputs (one on each side): both carry the value of the single lever
            // (0.19.3: the steering wheel went back to a single output)
            // 0.12.1: a single output — left (smaller x) pulls to -1, right to +1
            if (p.combines && p.ports.Length > 0 && p.levers.Length >= 2)
            {
                float v = System.Math.Max(-1f, System.Math.Min(1f, p.latest[0] - p.latest[1]));   // 0.12.4 (Andre): it was inverted — left -1, right +1
                Ecs.WritePort(em, p.ports[0], v);
            }
        }
    }
}
