using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

namespace CrapMechanics
{
    /// <summary>0.31.18 (Andre, 14/11: "está me irritando o ciclo de dev, toda hora é alguma idiotice pra medir, nunca uma solução").
    /// This one measures the only thing that can settle where the ship's lean comes from, and it needs no special flight.
    ///
    /// A vehicle flying free (no air — and the drag watch showed the air in space is EXACTLY zero) can only change its total angular
    /// momentum through an outside force: the thrusters. Everything inside it — the mod's welds, the servos, the car resting on the
    /// floor, the pistons — only passes momentum from one piece to another; the TOTAL cannot change. So: add up the angular momentum of
    /// every part of the vehicle, every half second, and see whether it drifts. If it drifts with the thrusters off, the mod is making
    /// spin out of nothing, and the number says how fast. If it does not drift, the lean comes from outside the vehicle and I have to
    /// look somewhere else. Either way it is an answer, not another hypothesis.
    ///
    /// It is measured in the game's own bookkeeping (the masses the physics uses), because that is what the solver conserves. The mod
    /// changes those masses on purpose (PartMass: a light piece between two joints counts heavier so the weld holds); when they change
    /// between two samples the line says so, because then a jump in the total is expected and means nothing.</summary>
    internal static class MomentumWatch
    {
        public static bool Enabled = true;   // [Debug] momentum_watch
        // 0.31.21 (Andre, 14/11: "o problema acontece com thruster ligado tambem"). The split that works with the thrusters running is not
        // "engine on or off", it is WHERE IN THE FRAME:
        //   PhysicsPreGroup ... SpaceshipPartsApplyForces (the game's thrust) ... AerodynamicsSystem (the game's drag) | END
        //   PhysicsSystemGroup: the solver — where the mod's welds, servos and pistons act, and where the game's contacts are solved
        //   AfterPhysicsSystemGroup (SpaceshipComponentsTickPostGroup) | the mod reads again here
        // So the spin momentum is read at the END of the first group and again at the START of the last one. Everything the game's engines
        // and air did is already inside the first reading and drops out of the subtraction. What is left, for a vehicle touching nothing
        // outside itself, is internal — and internal cannot create spin. That is the mod's leak, measured with the ship flying normally.
        // That number is logged and nothing else: it is the leak, with the ship flying normally. (0.31.21 took it back out again every
        // tick; dropped in 0.31.22 — hiding the result is not ending the leak, and the fix belongs before the solver, not after it.)
        static float t; static bool has; static float3 lastL; static float lastMass, lastSpin; static int lastParts;
        static int warnings; static float tLog; static string pending;
        // 0.31.20: where the spin appears. Between two samples, each part's own spin momentum change (its inertia x how much its angular
        // velocity changed). With the thrusters off and no air, nothing outside may add any: whatever shows up came from the mod's welds,
        // and the part it shows up on, with what the mod is faking on that part, says which weld.
        static readonly Dictionary<long, float3> omega = new Dictionary<long, float3>();
        static readonly Dictionary<long, float3> inertiaOf = new Dictionary<long, float3>();
        // ---- 0.31.21: across the solver
        struct Body { public Entity p; public float m; public float3 com, lin, inertia; public quaternion rm; public float3 w; }
        static readonly List<Body> bodies = new List<Body>();
        static readonly List<Entity> veh = new List<Entity>();
        static float tKeep;

        public static void Forget() { has = false; t = 0f; pending = null; omega.Clear(); inertiaOf.Clear(); has1 = has3 = has5 = false; veh.Clear(); }

        /// <summary>Is any thruster pushing? 0.31.26 (item 31): the game's own factor, the same FlightTrace now uses — the thrust is
        /// _maxForce × _frameEfficiency × _atmoUnderwaterMultiplier01 [recon/EPC_SCThruster.cs:250 (line 835 of the listing), 260].</summary>
        static bool AnyThrust(EntityManager em)
        {
            if (!Thrusters.Ready) return true;   // can't tell: assume yes and stay quiet
            try
            {
                foreach (var e in Thrusters.Entities)
                {
                    if (!Ecs.Alive(em, e)) continue;
                    if (!Thrusters.Efficiency(em, e, out float eff)) return true;   // can't read it: assume yes
                    if (System.Math.Abs(eff) > 0.01f) return true;
                }
            }
            catch (Exception) { return true; }
            return false;
        }

        // 0.31.23: the frame cut in three, so one flight says WHERE the spin changes. Each tick (one in four is measured):
        //   0 start of PhysicsPreGroup ........................................ counts the tick
        //   1 same place, the real masses in .................................. closes "the rest of the frame", opens "the game's forces" (thrust, drag)
        //   2 end of PhysicsPreGroup, before the welded parts are made one .... closes "the game's forces"
        //   3 same place, welded parts made one, the solver's masses back in .. opens  "the solver" (the mod's joints, contacts)
        //   4 start of SpaceshipComponentsTickPostGroup ....................... closes "the solver"
        //   5 same place, the real masses just put back ...................... opens  "the rest of the frame"
        // 0.31.26 (item 12): "the rest" used to run from 4 (solver's masses) to 0 (real or solver's, depending on whether the frame's mass balance
        // ran in between): two different books at its two ends. Now it runs 5 -> 1, real masses at both ends. 0.31.26 (U2): "the solver" is
        // also measured with the REAL masses (the parts' real mass, inertia and centre, the same motion): the solver conserves the momentum
        // of the masses it is given, and what it does to the real momentum is what the ship feels.
        // Each piece is the net change of the vehicle's spin momentum inside it, per second. The engines show up in the first one, and
        // that is right. A number in the other two is spin made out of nothing.
        static int tick, vehTick = 1000; static bool measuring;
        static float3 l1, l3, l5, l3r; static bool has1, has3, has5;
        static float3 sumForces, sumSolver, sumSolverReal, sumRest; static int nForces, nSolver, nRest;
        public static void Mark(EntityManager em, int stage)
        {
            if (!Enabled) return;
            try
            {
                if (stage == 0)
                {
                    tick++;
                    measuring = (tick & 3) == 0;
                    if (measuring && (++vehTick >= 8 || veh.Count == 0)) { vehTick = 0; veh.Clear(); foreach (var p in Vehicle(em, out _)) veh.Add(p); }
                    has1 = false; has3 = false; return;
                }
                if (stage == 1 && has5) { has5 = false; if (Read(em, out var l, out _, out _)) { sumRest = Ecs.Add(sumRest, Ecs.Sub(l, l5)); nRest++; } }   // opened in the last measured tick
                if (!measuring) return;
                switch (stage)
                {
                    case 1: has1 = Read(em, out l1, out _, out _); break;
                    case 2: if (has1) { has1 = false; if (Read(em, out var l, out _, out _)) { sumForces = Ecs.Add(sumForces, Ecs.Sub(l, l1)); nForces++; } } break;
                    case 3: has3 = Read(em, out l3, out l3r, out _); break;
                    case 4:
                        if (has3) { has3 = false; if (Read(em, out var l4, out var l4r, out _)) { sumSolver = Ecs.Add(sumSolver, Ecs.Sub(l4, l3)); sumSolverReal = Ecs.Add(sumSolverReal, Ecs.Sub(l4r, l3r)); nSolver++; } }
                        break;
                    case 5: has5 = Read(em, out l5, out _, out _); break;
                }
            }
            catch (Exception ex) { has1 = has3 = has5 = false; if (warnings++ < 3) ModLog.Warn("momentum watch (stage " + stage + "): " + ex.Message); }
        }
        static string Pieces()
        {
            if (nForces + nSolver + nRest == 0) return "";
            string s = " | where the spin changes, kg m2/s per second (net): in the game's forces " + (nForces > 0 ? (Ecs.Len(sumForces) / nForces * 60f).ToString("F0") : "?")
                     + ", in the solver " + (nSolver > 0 ? (Ecs.Len(sumSolver) / nSolver * 60f).ToString("F0") + " (with the real masses " + (Ecs.Len(sumSolverReal) / nSolver * 60f).ToString("F0") + ")" : "?")
                     + ", in the rest of the frame " + (nRest > 0 ? (Ecs.Len(sumRest) / nRest * 60f).ToString("F0") : "?");
            sumForces = default; sumSolver = default; sumSolverReal = default; sumRest = default; nForces = nSolver = nRest = 0;
            return s;
        }

        /// <summary>Reads every part of the vehicle and returns the spin momentum about the vehicle's own centre: L in the masses the physics
        /// has now, Lreal with each part's REAL mass, inertia and centre (0.31.26, U2: same motion — the speed carried to the real centre).</summary>
        static unsafe bool Read(EntityManager em, out float3 L, out float3 Lreal, out float3 c)
        {
            L = default; Lreal = default; c = default; bodies.Clear(); realBodies.Clear();
            float mass = 0f, massR = 0f; float3 cR = default;
            foreach (var p in veh)
            {
                // 0.31.27 (performance, up to 6 reads of every part per measured tick): one existence test, then HasComponent alone
                if (!Ecs.Alive(em, p) || !Ecs.Has(em, p, Ecs.tiMass) || !Ecs.Has(em, p, Ecs.tiVel) || !Ecs.Has(em, p, Ecs.tiLT)) continue;
                var pm = (PhysicsMass*)Ecs.RO(em, p, Ecs.tiMass);
                var pv = (PhysicsVelocity*)Ecs.RO(em, p, Ecs.tiVel);
                if (pm->InverseMass <= 0f) continue;
                var lt = *(Unity.Transforms.LocalTransform*)Ecs.RO(em, p, Ecs.tiLT); float s = lt.Scale == 0f ? 1f : lt.Scale;
                var b = new Body { p = p, m = 1f / pm->InverseMass,
                                   com = Ecs.Add(lt.Position, Ecs.Rot(lt.Rotation, Ecs.Mul(pm->CenterOfMass, s))),
                                   lin = pv->Linear, w = pv->Angular,
                                   rm = Ecs.QMul(lt.Rotation, pm->InertiaOrientation),
                                   inertia = new float3 { x = pm->InverseInertia.x > 0f ? 1f / pm->InverseInertia.x : 0f,
                                                          y = pm->InverseInertia.y > 0f ? 1f / pm->InverseInertia.y : 0f,
                                                          z = pm->InverseInertia.z > 0f ? 1f / pm->InverseInertia.z : 0f } };
                bodies.Add(b); mass += b.m; c = Ecs.Add(c, Ecs.Mul(b.com, b.m));
                var br = b;
                if (JointSystem.RealBook(p, pm, out float invMr, out float3 invIr, out float3 comR) && invMr > 0f)
                {
                    br.m = 1f / invMr;
                    br.com = Ecs.Add(lt.Position, Ecs.Rot(lt.Rotation, Ecs.Mul(comR, s)));
                    br.lin = Ecs.Add(b.lin, Ecs.Cross(Ecs.Rot(b.rm, b.w), Ecs.Sub(br.com, b.com)));   // the same motion, measured at the real centre
                    br.inertia = new float3 { x = invIr.x > 0f ? 1f / invIr.x : 0f, y = invIr.y > 0f ? 1f / invIr.y : 0f, z = invIr.z > 0f ? 1f / invIr.z : 0f };
                }
                realBodies.Add(br); massR += br.m; cR = Ecs.Add(cR, Ecs.Mul(br.com, br.m));
            }
            if (bodies.Count == 0 || mass <= 0f || massR <= 0f) return false;
            c = Ecs.Div(c, mass); cR = Ecs.Div(cR, massR);
            L = Spin(bodies, c); Lreal = Spin(realBodies, cR);
            return true;
        }
        static readonly List<Body> realBodies = new List<Body>();
        static float3 Spin(List<Body> list, float3 c)
        {
            float3 L = default;
            foreach (var b in list)
            {
                var lBody = new float3 { x = b.inertia.x * b.w.x, y = b.inertia.y * b.w.y, z = b.inertia.z * b.w.z };
                L = Ecs.Add(L, Ecs.Rot(b.rm, lBody));
                L = Ecs.Add(L, Ecs.Mul(Ecs.Cross(Ecs.Sub(b.com, c), b.lin), b.m));
            }
            return L;
        }

        public static void Step(EntityManager em, float dt)
        {
            if (!Enabled) return;
            t += dt; if (t < 0.5f) return; float span = t; t = 0f;
            try { Sample(em, span); } catch (Exception ex) { if (warnings++ < 3) ModLog.Warn("momentum watch: " + ex.Message); }
        }

        /// <summary>The parts of the vehicle: the heaviest one and everything the mod's joints link to it.</summary>
        static List<Entity> Vehicle(EntityManager em, out Entity big)
        {
            big = Entity.Null; float bigM = 0f;
            var parts = Ecs.Parts(em);
            foreach (var p in parts) { if (!Ecs.HasC(em, p, Ecs.tiMass)) continue; float m = Ecs.Mass(em, p); if (m > bigM) { bigM = m; big = p; } }
            var set = new List<Entity>(); if (Ecs.IsNull(big)) return set;
            var seen = new HashSet<long> { Ecs.K(big) }; set.Add(big);
            var pairs = new List<(Entity, Entity)>(); foreach (var pr in JointSystem.LinkedParts()) pairs.Add(pr);
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var (a, b) in pairs)
                {
                    bool ha = seen.Contains(Ecs.K(a)), hb = seen.Contains(Ecs.K(b));
                    if (ha == hb) continue;
                    var add = ha ? b : a; if (!Ecs.Alive(em, add) || !Ecs.HasC(em, add, Ecs.tiMass)) continue;
                    seen.Add(Ecs.K(add)); set.Add(add); grew = true;
                }
            }
            return set;
        }

        static unsafe void Sample(EntityManager em, float span)
        {
            var parts = Vehicle(em, out var big);
            if (parts.Count == 0 || Ecs.IsNull(big)) { has = false; return; }
            float3 L = default; float mass = 0f; float iHull = 1f;
            // centre of mass of the whole vehicle first (the momentum is taken about it)
            float3 c = default;
            var data = new List<(float m, float3 com, float3 lin, float3 ang, quaternion rm, float3 inertia)>();
            foreach (var p in parts)
            {
                if (!Ecs.HasC(em, p, Ecs.tiMass) || !Ecs.HasC(em, p, Ecs.tiVel) || !Ecs.HasC(em, p, Ecs.tiLT)) continue;
                var pm = (PhysicsMass*)Ecs.RO(em, p, Ecs.tiMass);
                var pv = (PhysicsVelocity*)Ecs.RO(em, p, Ecs.tiVel);
                var lt = Ecs.LT(em, p);
                if (pm->InverseMass <= 0f) continue;   // static / kinematic: not part of the sum
                float m = 1f / pm->InverseMass; float s = lt.Scale == 0f ? 1f : lt.Scale;
                var com = Ecs.Add(lt.Position, Ecs.Rot(lt.Rotation, Ecs.Mul(pm->CenterOfMass, s)));
                var rm = Ecs.QMul(lt.Rotation, pm->InertiaOrientation);
                var inertia = new float3 { x = pm->InverseInertia.x > 0f ? 1f / pm->InverseInertia.x : 0f,
                                           y = pm->InverseInertia.y > 0f ? 1f / pm->InverseInertia.y : 0f,
                                           z = pm->InverseInertia.z > 0f ? 1f / pm->InverseInertia.z : 0f };
                data.Add((m, com, pv->Linear, pv->Angular, rm, inertia));
                mass += m; c = Ecs.Add(c, Ecs.Mul(com, m));
                if (p.Index == big.Index) iHull = System.Math.Max(1f, System.Math.Max(inertia.x, System.Math.Max(inertia.y, inertia.z)));
            }
            if (mass <= 0f) { has = false; return; }
            c = Ecs.Div(c, mass);
            foreach (var (m, com, lin, ang, rm, inertia) in data)
            {
                // spin: the angular velocity is kept in the body's inertia frame
                var lBody = new float3 { x = inertia.x * ang.x, y = inertia.y * ang.y, z = inertia.z * ang.z };
                L = Ecs.Add(L, Ecs.Rot(rm, lBody));
                // and the orbit of its own mass around the vehicle's centre
                L = Ecs.Add(L, Ecs.Mul(Ecs.Cross(Ecs.Sub(com, c), lin), m));
            }
            float spin = 0f;
            if (Ecs.HasC(em, big, Ecs.tiVel)) { var pv = (PhysicsVelocity*)Ecs.RO(em, big, Ecs.tiVel); spin = Ecs.Len(pv->Angular) * 57.29578f; }
            bool thrust = AnyThrust(em);
            if (has)
            {
                bool massChanged = System.Math.Abs(mass - lastMass) > 0.5f || parts.Count != lastParts;
                var dL = Ecs.Sub(L, lastL); float rate = Ecs.Len(dL) / System.Math.Max(0.01f, span);
                // what that drift is worth as spin on the hull, which is what is felt
                float deg = rate / iHull * 57.29578f;
                tLog += span;
                string why = massChanged ? " (the mod changed the masses in this window, so a jump here means nothing)" : "";
                string line = "momentum watch: " + parts.Count + " part(s), " + (mass / 1000f).ToString("F1") + " t | total spin momentum " + Ecs.Len(L).ToString("F0")
                            + " kg m2/s, drifting " + rate.ToString("F0") + " per second = " + deg.ToString("F4") + " deg/s added to the hull every second | hull spin now " + spin.ToString("F2") + " deg/s"
                            + (thrust ? " (thrusters pushing: the game may change the spin, this number proves nothing)" : " — FREE FLIGHT, no thruster pushing: nothing outside may change the spin") + why;
                if (!thrust && !massChanged) line += Where(em, span);
                // free flight is the one that settles it: always logged. With thrust, every 2 s or when it is big.
                tKeep += span;
                if (tKeep >= 2f)
                {
                    tKeep = 0f;
                    line += Pieces() + JointSystem.WeldSummary();
                }
                if (!thrust || deg > 0.05f || tLog >= 2f) { tLog = 0f; ModLog.Info(line); }
                pending = line;
            }
            lastL = L; lastMass = mass; lastParts = parts.Count; lastSpin = spin; has = true;
            omega.Clear(); inertiaOf.Clear();
            foreach (var p in parts)
            {
                if (!Ecs.HasC(em, p, Ecs.tiVel) || !Ecs.HasC(em, p, Ecs.tiMass) || !Ecs.HasC(em, p, Ecs.tiLT)) continue;
                var pv = (PhysicsVelocity*)Ecs.RO(em, p, Ecs.tiVel);
                var pm = (PhysicsMass*)Ecs.RO(em, p, Ecs.tiMass);
                var lt = Ecs.LT(em, p);
                omega[Ecs.K(p)] = Ecs.Rot(Ecs.QMul(lt.Rotation, pm->InertiaOrientation), pv->Angular);   // world
                inertiaOf[Ecs.K(p)] = new float3 { x = pm->InverseInertia.x > 0f ? 1f / pm->InverseInertia.x : 0f,
                                                   y = pm->InverseInertia.y > 0f ? 1f / pm->InverseInertia.y : 0f,
                                                   z = pm->InverseInertia.z > 0f ? 1f / pm->InverseInertia.z : 0f };
            }
        }

        /// <summary>Which parts the new spin appeared on, in free flight. Each one with what the mod is faking on it and which weld holds it.</summary>
        static unsafe string Where(EntityManager em, float span)
        {
            if (omega.Count == 0) return "";
            var got = new List<(float dl, Entity p, float3 dw)>();
            foreach (var kv in omega)
            {
                var p = Ecs.OfKey(kv.Key);
                if (!Ecs.Alive(em, p) || !Ecs.HasC(em, p, Ecs.tiVel) || !Ecs.HasC(em, p, Ecs.tiMass) || !Ecs.HasC(em, p, Ecs.tiLT)) continue;
                var pv = (PhysicsVelocity*)Ecs.RO(em, p, Ecs.tiVel);
                var pm = (PhysicsMass*)Ecs.RO(em, p, Ecs.tiMass);
                var lt = Ecs.LT(em, p);
                var w = Ecs.Rot(Ecs.QMul(lt.Rotation, pm->InertiaOrientation), pv->Angular);
                var dw = Ecs.Sub(w, kv.Value);
                if (!inertiaOf.TryGetValue(kv.Key, out var I)) continue;
                float imax = System.Math.Max(I.x, System.Math.Max(I.y, I.z));
                got.Add((imax * Ecs.Len(dw), p, dw));
            }
            if (got.Count == 0) return "";
            got.Sort((a, b) => b.dl.CompareTo(a.dl));
            var sb = new System.Text.StringBuilder(" | the new spin appeared on:");
            for (int i = 0; i < got.Count && i < 3; i++)
            {
                var (dl, p, dw) = got[i];
                if (dl < 1f) break;
                sb.Append(i == 0 ? " " : "; ").Append("part ").Append(p.Index).Append(" ").Append((dl / System.Math.Max(0.01f, span)).ToString("F0")).Append(" kg m2/s per second (")
                  .Append((Ecs.Len(dw) * 57.29578f / System.Math.Max(0.01f, span)).ToString("F2")).Append(" deg/s per second)");
                if (JointSystem.Faked(p, out float real, out float phys, out float spinF))
                    sb.Append(" [the mod counts it as ").Append(phys.ToString("F0")).Append(" kg instead of ").Append(real.ToString("F0")).Append(" kg, and ").Append(spinF.ToString("F0")).Append("x harder to turn]");
                var jn = JointSystem.JointOfPart(p); if (jn != null) sb.Append(" held by ").Append(jn);
            }
            return sb.Length > 30 ? sb.ToString() : "";
        }
        /// <summary>For the black box snapshot.</summary>
        public static string Summary() => pending ?? "momentum watch: no sample yet";
    }
}
