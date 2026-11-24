using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

namespace CrapMechanics
{
    /// <summary>0.31.17 (Andre, 14/11, two flights with the same ship: with the microframe gate it starts to lean in space and ends up
    /// tumbling at 86 deg/s; with the opening closed by a welded frame wall it holds 0,3 deg/s at 3000 m/s). His flight 2 also proves the
    /// lean is NOT the thrust line being off the centre of mass: there the thrusters make 4068 kN·m of torque around it and the ship does
    /// not turn, because the game cancels the torque of its own thrusters. So the ship is being turned by a force the game does not
    /// compensate, and the gate is what changes between the two flights.
    ///
    /// The game's own drag (AerodynamicsSystem, PhysicsPreGroup, OrderLast): for each PART, with air density above 1e-7, it casts rays
    /// against THAT PART's collider along the velocity and changes its PhysicsVelocity. Per part, so with the gate closed there are three
    /// different holes: the whole doorway missing from the hull's collider (the door is another part), the door itself taking the wind as
    /// a body of its own and pushing the hull through the bearing, and the thin gap around the door that can never be closed (the
    /// microframes must not touch the hull). This file only MEASURES them; it changes nothing by itself.
    ///
    ///  - [Debug] drag_watch: every 0,5 s one "drag watch:" line — the air density with real decimals (the flight line rounds it to 0,000
    ///    and that hides whatever is left up there), and, per part, the two numbers the game keeps for its drag (DynamicObjectAerodynamics
    ///    _drag01 / _aerodynamics01) and how much speed and spin the game's drag took from that part. 0.31.26 (item 32): that is worked out
    ///    from the drag itself — the game does exactly v *= (1 − _drag01) and ω *= (1 − _drag01) [recon/AerodynamicsSystem.cs:136-155], so
    ///    what it took is |v_after|·d/(1−d) (same for ω). It used to be the change of speed across the whole step, which also holds the
    ///    wind, the ocean, the solar storm, the GreenTrench and the accretion disc [recon/SCApplyForceToSPSystem.cs:6-7, 45-75], and the
    ///    "no thruster force" filter read the thrust buffer where it is always empty (ApplyForces clears it in the tick before
    ///    [recon/SpaceshipPartsApplyForces.cs:89], SCTick_Thruster_Forces fills it later [recon/EPC_SCThruster.cs:38-46]).
    ///  - [Debug] no_drag: the A/B switch. Zeroes those two numbers on every part at the start of the tick. It is a TEST tool, not a way
    ///    to play: with it on, nothing in flight is slowed by the air. The log says, once, whether the game accepted the zero or
    ///    recomputed it in the same tick, and the measured numbers above say whether the force actually stopped.</summary>
    internal static class DragWatch
    {
        public static bool Enabled = false, NoDrag = false;
        // 0.31.17: two ways to turn the game's drag off, because it is not known whether the game keeps the numbers we write or
        // recomputes them inside its own job. 1) write 0 into the two numbers; if the postfix finds them back, 2) take the AIR away from
        // the part instead (the game's drag skips a part whose air density is at or below 1e-7), and give it back at the end of the
        // group. The air is only taken where it is already thin (below 0.01), so a thruster's efficiency in real atmosphere is not
        // touched; the air meters themselves are refreshed in another group, so nothing else sees the zero.
        static bool airLever; const float AirThin = 0.01f;
        static TypeIndex tiAero, tiAir; static bool ok, failed, aeroOk, airOk;
        struct Snap { public bool zeroed; public float drag, aero, airWas; }   // 0.31.26: only what no_drag needs
        static readonly Dictionary<long, Snap> snap = new Dictionary<long, Snap>();
        class Acc { public float dv, dw, drag, aero; public int n; }
        static readonly Dictionary<long, Acc> acc = new Dictionary<long, Acc>();
        static float tLog, air;
        static int warnings; static bool saidNoDrag, saidKept, saidRecomputed;

        public static void Forget() { snap.Clear(); acc.Clear(); saidKept = false; saidRecomputed = false; airLever = false; }

        static bool Prepare()
        {
            if (ok) return true; if (failed) return false;
            try
            {
                try { tiAero = TypeManager.GetTypeIndex(Il2CppType.Of<DynamicObjectAerodynamics>()); aeroOk = true; } catch (Exception ex) { ModLog.Warn("drag watch: DynamicObjectAerodynamics not available (" + ex.Message + ")"); }
                try { tiAir = TypeManager.GetTypeIndex(Il2CppType.Of<AtmosphereDensityMeter>()); airOk = true; } catch (Exception) { }
                ok = aeroOk; failed = !ok; return ok;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("drag watch: off (" + ex.Message + ")"); return false; }
        }

        static unsafe bool Aero(EntityManager em, Entity p, out float drag, out float aero)
        {
            drag = 0f; aero = 0f;
            if (!Ecs.HasC(em, p, tiAero)) return false;
            var b = (float*)Ecs.RO(em, p, tiAero); if (b == null) return false;
            drag = b[0]; aero = b[1]; return true;   // _drag01 +0, _aerodynamics01 +4
        }
        static unsafe void Zero(EntityManager em, Entity p)
        {
            if (!Ecs.HasC(em, p, tiAero)) return;
            var b = (float*)em.GetComponentDataRawRW(p, tiAero); if (b == null) return;
            b[0] = 0f; b[1] = 0f;
        }
        static unsafe bool Air(EntityManager em, Entity p, out float a)
        {
            a = 0f;
            if (!airOk || !Ecs.HasC(em, p, tiAir)) return false;
            var f = (float*)Ecs.RO(em, p, tiAir); if (f == null) return false;
            a = f[0]; return true;
        }
        static unsafe void WriteAir(EntityManager em, Entity p, float v)
        {
            if (!airOk || !Ecs.HasC(em, p, tiAir)) return;
            var f = (float*)em.GetComponentDataRawRW(p, tiAir); if (f != null) f[0] = v;
        }
        static unsafe bool Vel(EntityManager em, Entity p, out float3 lin, out float3 ang)
        {
            lin = default; ang = default;
            if (!Ecs.HasC(em, p, Ecs.tiVel)) return false;
            var v = (PhysicsVelocity*)Ecs.RO(em, p, Ecs.tiVel); if (v == null) return false;
            lin = v->Linear; ang = v->Angular; return true;
        }

        /// <summary>Start of PhysicsPreGroup — before the game applies thrust and, last in that group, its drag.</summary>
        public static void BeforePhysics(EntityManager em)
        {
            if (!NoDrag || !Prepare()) return;   // 0.31.26: only the no_drag switch needs anything before the game's drag
            if (NoDrag && !saidNoDrag) { saidNoDrag = true; ModLog.Warn("[Debug] no_drag is ON: the mod is zeroing the game's drag on every part. This is a TEST switch — nothing in flight is slowed by the air while it is on. Turn it off to play."); }
            try
            {
                snap.Clear();
                foreach (var p in Ecs.Parts(em))
                {
                    Aero(em, p, out float d, out float a);
                    float airWas = -1f;
                    Zero(em, p);
                    if (airLever && Air(em, p, out float av) && av > 0f && av < AirThin) { airWas = av; WriteAir(em, p, 0f); }
                    snap[Ecs.K(p)] = new Snap { zeroed = true, drag = d, aero = a, airWas = airWas };
                }
            }
            catch (Exception ex) { if (warnings++ < 3) ModLog.Warn("drag watch (before): " + ex.Message); }
        }

        /// <summary>End of PhysicsPreGroup — the game's drag has just run.</summary>
        public static unsafe void AfterPhysics(EntityManager em)
        {
            if ((!Enabled && !NoDrag) || !Prepare() || (!Enabled && snap.Count == 0)) return;
            try
            {
                foreach (var p in Ecs.Parts(em))
                {
                    long k = Ecs.K(p);
                    bool hasSnap = snap.TryGetValue(k, out var s);
                    if (!hasSnap && !Enabled) continue;
                    if (!Vel(em, p, out var lin, out var ang)) continue;
                    Aero(em, p, out float d, out float a);
                    if (hasSnap && s.airWas >= 0f) WriteAir(em, p, s.airWas);   // the air goes back the moment the game's drag is done with it
                    if (hasSnap && s.zeroed && !saidKept && !saidRecomputed)
                    {
                        if (d > 1e-6f || a > 1e-6f)
                        {
                            saidRecomputed = true; airLever = true;
                            ModLog.Info("drag watch: no_drag — the game put its own numbers back inside the same tick (drag01 " + d.ToString("F3") + ", aero01 " + a.ToString("F3") + "), so writing them is not enough. From now on the switch takes the AIR away from each part instead, where it is already thinner than " + AirThin.ToString("F2") + " (the game's drag skips a part with no air, and the air goes back at the end of the step). Real atmosphere is left alone, so this only turns the drag off high up and in space.");
                        }
                        else if (s.drag > 1e-6f) { saidKept = true; ModLog.Info("drag watch: no_drag — the zero stayed through the tick (the game had drag01 " + s.drag.ToString("F3") + " there)."); }
                    }
                    if (!Enabled) continue;
                    if (!acc.TryGetValue(k, out var ac)) { ac = new Acc(); acc[k] = ac; }
                    // 0.31.26 (item 32): what the game's drag took this tick, from its own number: v_before·(1 − d) = v_after
                    float keep = d > 0f && d < 0.999f ? d / (1f - d) : 0f;
                    ac.dv += Ecs.Len(lin) * keep; ac.dw += Ecs.Len(ang) * keep;
                    ac.drag = d; ac.aero = a; ac.n++;
                    if (airOk && Ecs.HasC(em, p, tiAir)) { var f = (float*)Ecs.RO(em, p, tiAir); if (f != null && f[0] > air) air = f[0]; }   // the thickest air any part of the ship is in, this window
                }
                snap.Clear();
                if (!Enabled) return;
                tLog += 1f / 60f;
                if (tLog < 0.5f) return;
                tLog = 0f;
                Line(em);
                acc.Clear(); air = 0f;
            }
            catch (Exception ex) { if (warnings++ < 3) ModLog.Warn("drag watch (after): " + ex.Message); }
        }

        static void Line(EntityManager em)
        {
            Entity big = Entity.Null; float bigM = 0f;
            foreach (var kv in acc) { var p = Ecs.OfKey(kv.Key); if (!Ecs.Alive(em, p) || !Ecs.HasC(em, p, Ecs.tiMass)) continue; float m = Ecs.Mass(em, p); if (m > bigM) { bigM = m; big = p; } }
            var sb = new System.Text.StringBuilder("drag watch: air ");
            sb.Append(air.ToString("E2")).Append(NoDrag ? (airLever ? " | NO_DRAG ON (by taking the air away)" : " | NO_DRAG ON (by zeroing the numbers)") : "");
            sb.Append(" | ").Append(acc.Count).Append(" part(s)");
            if (!Ecs.IsNull(big) && acc.TryGetValue(Ecs.K(big), out var ab)) sb.Append(" | vehicle ").Append(big.Index).Append(": ").Append(Of(ab));
            // the part whose spin the drag took the most from (0.31.26: computed from the drag itself, thrust or no thrust)
            long bk = 0; float bw = -1f;
            foreach (var kv in acc) { if (kv.Value.n == 0 || kv.Key == Ecs.K(big)) continue; float w = kv.Value.dw / kv.Value.n; if (w > bw) { bw = w; bk = kv.Key; } }
            if (bk != 0)
            {
                var p = Ecs.OfKey(bk); var ac = acc[bk];
                sb.Append(" | most braked in spin by the drag: part ").Append(p.Index).Append(" ");
                if (Ecs.Alive(em, p) && Ecs.HasC(em, p, Ecs.tiMass)) sb.Append(Ecs.Mass(em, p).ToString("F0")).Append(" kg ");
                sb.Append(Of(ac));
            }
            ModLog.Info(sb.ToString());
        }
        /// <summary>Per tick -> per second (60 ticks), spin in degrees.</summary>
        static string Of(Acc a)
        {
            int n = System.Math.Max(1, a.n);
            return "drag01 " + a.drag.ToString("F3") + " aero01 " + a.aero.ToString("F3")
                 + ", the drag took " + (a.dv / n * 60f).ToString("F3") + " m/s and " + (a.dw / n * 60f * 57.29578f).ToString("F3") + " deg/s per second";
        }
    }
}
