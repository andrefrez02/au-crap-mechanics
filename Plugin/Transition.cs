using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;

namespace CrapMechanics
{
    /// <summary>0.19.12 (Andre, 03/10: "add more logging, mainly when this CleanupWorldToDefaultState runs; read all the values that
    /// break, before and after"). LOG ONLY, nothing changes. On each Core.CleanupWorldToDefaultState / Core.ClearGame: snapshot BEFORE (prefix),
    /// AFTER (postfix) and at +1 s, +3 s and +6 s, always of the SAME entities captured in the "before" snapshot: LRTs, the game's Wireless Transmitters,
    /// cameras and monitors — live block, part, power, and per port: alive, type, value, persistent link, _runtimeEndEntity (port / cable / nothing)
    /// and the path to the source; transmitter: channel; camera: slice and signal; monitor: slices and signals. Plus air density and speed.</summary>
    [HarmonyPatch]
    internal static class Transition
    {
        static int n; static readonly List<Entity> lrts = new List<Entity>(), transm = new List<Entity>(), cams = new List<Entity>(), mons = new List<Entity>();
        static readonly List<(float em, string labelText)> pendingList = new List<(float, string)>(); static float t0; static bool tiOk, tiFailed;
        static TypeIndex tiCam, tiMon, tiAir, tiVel, tiLTW, tiTransm, tiPortAlt;
        static string reason = "";

        static bool PrepareTypes()   // 0.28.1: not "Prepare" — Harmony calls a method with that name in a patch class by itself, at patch time
        {
            if (tiOk) return true; if (tiFailed) return false;
            try
            {
                tiCam = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeCamera>());
                tiMon = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeMonitor>());
                tiTransm = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeWirelessTransmitter>());
                tiAir = TypeManager.GetTypeIndex(Il2CppType.Of<AtmosphereDensityMeter>());
                tiVel = TypeManager.GetTypeIndex(Il2CppType.Of<Unity.Physics.PhysicsVelocity>());
                tiLTW = TypeManager.GetTypeIndex(Il2CppType.Of<Unity.Transforms.LocalToWorld>());
                tiPortAlt = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipElectricPort>());
                tiOk = true; return true;
            }
            catch (Exception ex) { tiFailed = true; ModLog.Warn("transition log: " + ex.Message); return false; }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Core), nameof(Core.CleanupWorldToDefaultState))]
        static void BeforeCleanup() { Before("Core.CleanupWorldToDefaultState"); }
        [HarmonyPostfix, HarmonyPatch(typeof(Core), nameof(Core.CleanupWorldToDefaultState))]
        static void AfterCleanup() { After(); }
        [HarmonyPrefix, HarmonyPatch(typeof(Core), nameof(Core.ClearGame))]
        static void BeforeClear() { Before("Core.ClearGame"); }
        [HarmonyPostfix, HarmonyPatch(typeof(Core), nameof(Core.ClearGame))]
        static void AfterClear() { After(); }

        static void Before(string srcE)
        {
            try
            {
                if (!JointSystem.EmOk || !PrepareTypes()) return;
                var em = JointSystem.CurrentEm; n++; reason = srcE; t0 = UnityEngine.Time.unscaledTime;
                lrts.Clear(); transm.Clear(); cams.Clear(); mons.Clear();
                foreach (var e in Radio.Blocks()) lrts.Add(e);
                foreach (var e in Ecs.Components(em))
                {
                    if (Ecs.HasC(em, e, tiTransm)) transm.Add(e);
                    else if (Ecs.HasC(em, e, tiCam)) cams.Add(e);
                    else if (Ecs.HasC(em, e, tiMon)) mons.Add(e);
                }
                pendingList.Clear(); pendingList.Add((1f, "+1 s")); pendingList.Add((3f, "+3 s")); pendingList.Add((6f, "+6 s"));
                Snapshot(em, "BEFORE");
            }
            catch (Exception ex) { ModLog.Warn("transition log (before): " + ex.Message); }
        }
        static void After()
        {
            try { if (JointSystem.EmOk && tiOk && n > 0) Snapshot(JointSystem.CurrentEm, "AFTER (method returned)"); }
            catch (Exception ex) { ModLog.Warn("transition log (after): " + ex.Message); }
        }
        /// <summary>Called from the plugin's Update (before any early return): the delayed snapshots.</summary>
        public static void Step()
        {
            if (pendingList.Count == 0 || !JointSystem.EmOk) return;
            float dt = UnityEngine.Time.unscaledTime - t0;
            for (int i = 0; i < pendingList.Count; i++)
            {
                if (dt < pendingList[i].em) continue;
                var r = pendingList[i].labelText; pendingList.RemoveAt(i);
                try { Snapshot(JointSystem.CurrentEm, r); } catch (Exception ex) { ModLog.Warn("transition log (" + r + "): " + ex.Message); }
                return;
            }
        }

        static unsafe void Snapshot(EntityManager em, string atTime)
        {
            var sb = new StringBuilder();
            sb.Append("===== TRANSITION #").Append(n).Append(" (").Append(reason).Append(") — ").Append(atTime).Append(" — t=").Append(UnityEngine.Time.unscaledTime.ToString("F2")).Append(" s; ");
            sb.Append("mod state: ").Append(JointSystem.EmOk ? "ecs ok" : "ecs NOT ok").Append(Unloading.Active ? ", mod paused (unload window)" : "").Append(Unloading.Pending ? ", reset pending" : "");
            sb.Append("; radios tracked ").Append(Radio.RadioCount()).Append(", joints tracked ").Append(JointSystem.JointCount());
            sb.Append("\n  player: ").Append(Player(em));
            foreach (var e in lrts) Block(em, sb, "LRT", e);
            foreach (var e in transm) Block(em, sb, "WirelessTransmitter", e);
            foreach (var e in cams) Block(em, sb, "Camera", e);
            foreach (var e in mons) Block(em, sb, "Monitor", e);
            ModLog.Info(sb.ToString());
        }

        static unsafe string Player(EntityManager em)
        {
            try
            {
                var j = Ecs.MyPlayerOrNull(em); if (Ecs.IsNull(j) || !Ecs.Alive(em, j)) return "not found";
                var s = "entity " + j.Index + ":" + j.Version + " parent " + Ecs.Parent(em, j).Index;
                s += " air " + Air(em, j) + " vel " + Vel(em, j) + " seat " + (Ecs.IsNull(Ecs.Parent(em, j)) ? "none" : (Ecs.IsPart(em, Ecs.Parent(em, j)) ? "part" : "entity " + Ecs.Parent(em, j).Index));
                return s;
            }
            catch (Exception ex) { return "error " + ex.Message; }
        }
        static unsafe string Air(EntityManager em, Entity e)
        {
            try
            {
                var p = Ecs.HasC(em, e, tiAir) ? (float*)em.GetComponentDataRawRO(e, tiAir) : null; if (p != null) return p[0].ToString("F3");
                var parent = Ecs.Parent(em, e); p = Ecs.HasC(em, parent, tiAir) ? (float*)em.GetComponentDataRawRO(parent, tiAir) : null; if (p != null) return p[0].ToString("F3") + "(part)";
                return "?";
            }
            catch (Exception) { return "?"; }
        }
        static unsafe string Vel(EntityManager em, Entity e)
        {
            try
            {
                var target = Ecs.HasC(em, e, tiVel) ? e : Ecs.Parent(em, e);
                var p = Ecs.HasC(em, target, tiVel) ? (float*)em.GetComponentDataRawRO(target, tiVel) : null; if (p == null) return "?";
                return Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]).ToString("F1") + " m/s";
            }
            catch (Exception) { return "?"; }
        }

        static unsafe void Block(EntityManager em, StringBuilder sb, string kind, Entity e)
        {
            sb.Append("\n  ").Append(kind).Append(' ').Append(e.Index).Append(':').Append(e.Version);
            if (!Ecs.Alive(em, e)) { sb.Append(" DEAD"); return; }
            var parent = Ecs.Parent(em, e);
            sb.Append(" parent ").Append(parent.Index).Append(':').Append(parent.Version).Append(Ecs.IsPart(em, parent) ? " (part)" : " (not a part)");
            byte estE; bool power = Ecs.HasPower(em, e, out estE); sb.Append(" power ").Append(estE == 255 ? "n/a" : JointSystem.StateName(estE));
            sb.Append(" air ").Append(Air(em, e)).Append(" vel ").Append(Vel(em, e));
            try
            {
                if (kind == "WirelessTransmitter") sb.Append(" channel ").Append(Ecs.Channel(em, e));
                if (kind == "Camera")
                {
                    var c = Ecs.HasC(em, e, tiCam) ? (byte*)em.GetComponentDataRawRO(e, tiCam) : null;
                    if (c != null) { var ce = *(Entity*)(c + Layout.CamEntity); sb.Append(" cameraEntity ").Append(ce.Index).Append(Ecs.Alive(em, ce) ? "" : "(DEAD)").Append(" slice ").Append(*(int*)(c + Layout.CamSlice)).Append(" nextDraw ").Append((*(double*)(c + Layout.CamNextDraw)).ToString("F2")); }
                }
                if (kind == "Monitor")
                {
                    var m = Ecs.HasC(em, e, tiMon) ? (byte*)em.GetComponentDataRawRO(e, tiMon) : null;
                    if (m != null) { sb.Append(" slices (").Append(*(int*)(m + Layout.MonSlices)).Append(',').Append(*(int*)(m + Layout.MonSlices + 4)).Append(") lastSignals (").Append((*(float*)(m + Layout.MonSignals)).ToString("G9")).Append(',').Append((*(float*)(m + Layout.MonSignals + 4)).ToString("G9")).Append(')'); }
                }
            }
            catch (Exception ex) { sb.Append(" [").Append(ex.Message).Append(']'); }
            var ports = Ecs.Ports(em, e);
            sb.Append(" ports ").Append(ports.Length).Append(':');
            for (int i = 0; i < ports.Length; i++)
            {
                var p = ports[i]; sb.Append("\n      [").Append(i).Append("] ").Append(p.Index).Append(':').Append(p.Version);
                if (!Ecs.Alive(em, p)) { sb.Append(" DEAD"); continue; }
                if (!Ecs.RawPortPub(em, p, out var t, out var v, out var end, out var persist)) { sb.Append(" no SpaceshipElectricPort"); continue; }
                byte rt = 255; try { var b = Ecs.HasC(em, p, tiPortAlt) ? (byte*)em.GetComponentDataRawRO(p, tiPortAlt) : null; if (b != null) rt = b[Layout.PortRuntime]; } catch (Exception) { }
                sb.Append(" setup ").Append(t).Append(" runtime ").Append(rt).Append(" value ").Append(v.ToString("G9"));
                sb.Append(" persistent -> ").Append(Who(em, persist)).Append(" runtimeEnd -> ").Append(Who(em, end));
                if (t == Ecs.PortInput || rt == Ecs.PortInput || t == 3)
                {
                    try { var source = Ecs.Source(em, p, out var trail); sb.Append(" source ").Append(Ecs.IsNull(source) ? "NONE" : source.Index.ToString()).Append(" path:").Append(trail); } catch (Exception ex) { sb.Append(" source error ").Append(ex.Message); }
                }
            }
        }
        static string Who(EntityManager em, Entity x)
        {
            if (Ecs.IsNull(x)) return "nothing";
            if (!Ecs.Alive(em, x)) return x.Index + "(DEAD)";
            if (Ecs.IsCable(em, x)) return x.Index + "(cable cell)";
            if (Ecs.RawPortPub(em, x, out var t, out _, out _, out _)) { var owner = Ecs.Parent(em, x); return x.Index + "(port t" + t + " of " + owner.Index + ")"; }
            return x.Index + "(other)";
        }

        public static void Patch(Harmony h)
        {
            try { h.PatchAll(typeof(Transition)); ModLog.Info("transition log: on (snapshots before/after Core.CleanupWorldToDefaultState and Core.ClearGame, +1/+3/+6 s)"); }
            catch (Exception ex) { ModLog.Warn("transition log hook failed (" + ex.Message + ")"); }
        }
    }
}
