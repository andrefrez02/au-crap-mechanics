using System;
using System.Collections.Generic;
using System.Text;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

namespace CrapMechanics
{
    /// <summary>0.31.1 (Andre: "instead of me writing down the time of everything, make a version that records what you need — a way for you
    /// to see what I see"). A flight recorder, log only (it changes nothing in the game):
    ///  - every 5 s in the simulation, one "flight:" line: speed, spin, air density, parts, the vehicle's real and physics mass;
    ///  - by itself, with a screenshot and a full snapshot, when something comes off (new parts appear while flying), when a camera or a
    ///    monitor changes state, and it logs each time the game moves the universe back to the player (the "jolts");
    ///  - F8: the same capture whenever Andre presses it, without stopping the game (the note window is optional now).
    /// The snapshot: every part (real/physics mass, speed, spin), the mass balance, the game's joints closest to breaking, the thrusters,
    /// every camera and monitor with its ports and power (Transition.Block), the mod joints and the turret seats.</summary>
    internal static class BlackBox
    {
        static float tFlight = 0f, tWatch = 0f, tCooldown = 0f, tQuiet = 0f;
        static int partsBefore = -1; static long refPart; static float3 refPos; static float3 refVel; static bool refOk;
        static readonly Dictionary<long, string> camState = new Dictionary<long, string>();
        static int captures;
        static TypeIndex tiCam, tiMon, tiAir; static bool tiOk, tiFailed;
        static EntityQuery qCam, qMon; static bool qOk;

        public static void Forget() { partsBefore = -1; refOk = false; camState.Clear(); tQuiet = 0f; FlightTrace.Forget(); }

        static bool Prepare(EntityManager em)
        {
            if (tiOk) return true; if (tiFailed) return false;
            try
            {
                tiCam = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<SCTypeCamera>());
                tiMon = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<SCTypeMonitor>());
                tiAir = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<AtmosphereDensityMeter>());
                tiOk = true;
                // 0.31.15: launched cameras / monitors straight from a query (WatchCameras asked every block of the ship, 4 calls each, twice a second)
                try
                {
                    var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(3); a[0] = ComponentType.ReadOnly(tiCam); a[1] = ComponentType.ReadOnly(Ecs.tiParent); a[2] = ComponentType.ReadOnly(Ecs.tiLTW);
                    var b = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(3); b[0] = ComponentType.ReadOnly(tiMon); b[1] = ComponentType.ReadOnly(Ecs.tiParent); b[2] = ComponentType.ReadOnly(Ecs.tiLTW);
                    qCam = em.CreateEntityQuery(a); qMon = em.CreateEntityQuery(b); qOk = true;
                }
                catch (Exception ex) { qOk = false; ModLog.Warn("black box: camera/monitor queries: " + ex.Message); }
            }
            catch (Exception ex) { tiFailed = true; ModLog.Warn("black box: off (" + ex.Message + ")"); }
            return tiOk;
        }

        /// <summary>Simulation cycle (after the menu checks).</summary>
        public static unsafe void Step(EntityManager em, float dt)
        {
            if (!Prepare(em)) return;
            tFlight += dt; tWatch += dt; tCooldown -= dt; tQuiet += dt;
            if (tWatch < 0.5f) return;
            float span = tWatch; tWatch = 0f;
            var parts = Ecs.Parts(em);
            // the biggest part = the vehicle you are on (as a reference for speed and for the universe shift)
            Entity big = Entity.Null; float bigM = 0f;
            foreach (var p in parts) { float m = Ecs.Mass(em, p); if (m > bigM) { bigM = m; big = p; } }
            if (Ecs.IsNull(big)) { partsBefore = -1; refOk = false; return; }
            var lt = Ecs.LT(em, big); var v = Vel(em, big, out var w);
            // 1) universe shift: the reference part jumped far more than its speed explains
            if (refOk && refPart == Ecs.K(big))
            {
                // 0.31.1 logged a "jolt" every second: the game keeps the part you are on at the origin all the time (it moves the universe
                // every frame), so "it didn't move at its speed" is normal. A jolt is a jump that is neither ~0 nor ~speed x time.
                float3 expected = refPos + refVel * span; float off = System.Math.Min(math.length(lt.Position - expected), math.length(lt.Position - refPos));
                if (off > 200f) ModLog.Info("black box: the game moved the universe by " + (off / 1000f).ToString("F1") + " km (the part you are on went from " + Fmt(refPos) + " to " + Fmt(lt.Position) + " in " + span.ToString("F1") + " s at " + math.length(refVel).ToString("F0") + " m/s) — a 'jolt'");
            }
            refPart = Ecs.K(big); refPos = lt.Position; refVel = v; refOk = true;
            // 2) something came off: more parts than half a second ago, while nothing was launched in the last 10 s
            int count = parts.Count;
            if (partsBefore >= 0 && count > partsBefore && tQuiet > 10f)
                Capture(em, (count - partsBefore) + " new part(s) appeared in flight (" + partsBefore + " -> " + count + ") — something came off or the game split a part");
            if (partsBefore < 0 || count < partsBefore - 5 || count > partsBefore + 20) tQuiet = 0f;   // launch, garage, a big rebuild: settle first
            partsBefore = count;
            // 3) cameras and monitors that change state
            WatchCameras(em);
            // 5) 0.31.8: spin axis, centre of mass and thrust torque (FlightTrace)
            FlightTrace.Step(em, span, big, parts);
            // 4) flight line
            if (tFlight >= 5f)
            {
                tFlight = 0f;
                ModLog.Info("flight: speed " + math.length(v).ToString("F1") + " m/s, spin " + math.degrees(math.length(w)).ToString("F1") + " deg/s, air " + Air(em, big)
                    + ", " + count + " part(s), vehicle part " + big.Index + " " + bigM.ToString("F0") + " kg physics; " + JointSystem.MassSummary());
            }
        }

        static unsafe void WatchCameras(EntityManager em)
        {
            var seen = new HashSet<long>(); string changed = null;
            if (!qOk) return;
            var watched = new List<(Entity e, bool cam)>();
            foreach (var e in Ecs.QueryList(qCam)) watched.Add((e, true));
            foreach (var e in Ecs.QueryList(qMon)) watched.Add((e, false));
            foreach (var (e, cam) in watched)
            {
                long k = Ecs.K(e); if (!seen.Add(k)) continue;
                string s;
                try
                {
                    Ecs.HasPower(em, e, out byte st);
                    s = (st == 255 ? "power n/a" : "power " + JointSystem.StateName(st)) + (Far.Active(em, e) ? "" : ", SWITCHED OFF");
                    if (cam) { var c = (byte*)Ecs.RO(em, e, tiCam); var ce = *(Entity*)c; s += ", camera entity " + (Ecs.Alive(em, ce) ? "ok" : "DEAD"); }
                    else
                    {
                        // (the render slices turn over all the time — only the input, power and SCActive count as a change)
                        s += ", input " + (Ecs.Signal(em, e, out float sv, out _) ? sv.ToString("0.###") : "?");
                    }
                }
                catch (Exception ex) { s = "unreadable (" + ex.Message + ")"; }
                if (camState.TryGetValue(k, out var prev) && prev != s)
                {
                    ModLog.Info("black box: " + (cam ? "camera " : "monitor ") + e.Index + " changed: " + prev + "  ->  " + s);
                    if (changed == null) changed = (cam ? "camera " : "monitor ") + e.Index + ": " + prev + " -> " + s;
                }
                camState[k] = s;
            }
            if (changed != null && tQuiet > 10f) Capture(em, changed);
        }

        /// <summary>Screenshot + full snapshot, at most one every 10 s (F8 always).</summary>
        public static void Capture(EntityManager em, string why, bool fromKey = false)
        {
            if (!fromKey && tCooldown > 0f) return;
            Prepare(em);
            tCooldown = 10f; int n = ++captures;
            string snap; try { snap = Snapshot(em); } catch (Exception ex) { snap = "  (snapshot failed: " + ex.Message + ")\n"; }
            // 0.31.1: "f8-" / "auto-" in the name, so the captures Andre asked for are easy to tell apart when he reviews them; F8 beeps (no window now)
            string shot = Marker.Shot((fromKey ? "f8-" : "auto-") + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + n, snap, why);
            if (fromKey) Marker.Beep();
            ModLog.Warn("===== BLACK BOX #" + n + (fromKey ? " (F8)" : " (automatic)") + " — " + why + " — " + (shot ?? "no screenshot (game window not in front)") + " =====\n" + snap + "===== end of black box #" + n + " =====");
        }

        public static unsafe string Snapshot(EntityManager em)
        {
            var sb = new StringBuilder();
            sb.Append("parts (more than 50 kg), physics mass / spin / speed / air:\n");
            var parts = Ecs.Parts(em); parts.Sort((a, b) => Ecs.Mass(em, b).CompareTo(Ecs.Mass(em, a)));
            int shown = 0;
            foreach (var p in parts)
            {
                float m = Ecs.Mass(em, p); if (m < 50f) continue; if (++shown > 40) { sb.Append("  ...\n"); break; }
                var v = Vel(em, p, out var w);
                sb.Append("  part ").Append(p.Index).Append(' ').Append(m.ToString("F0")).Append(" kg").Append(JointSystem.RealMassNote(p))
                  .Append(", ").Append(math.length(v).ToString("F1")).Append(" m/s, ").Append(math.degrees(math.length(w)).ToString("F1")).Append(" deg/s, air ").Append(Air(em, p)).Append('\n');
            }
            sb.Append("mass balance: ").Append(JointSystem.MassSummary()).Append('\n');
            sb.Append("game joints closest to breaking (strain / break force):\n").Append(Strain(em));
            sb.Append(MomentumWatch.Summary()).Append("\n");   // 0.31.18
            sb.Append("thrusters:\n").Append(Thrusters.Summary(em));
            sb.Append("cameras and monitors:");
            var cams = new StringBuilder();
            foreach (var e in Ecs.Components(em))
            {
                if (Ecs.HasC(em, e, tiCam)) Transition.Block(em, cams, "Camera", e);
                else if (Ecs.HasC(em, e, tiMon)) Transition.Block(em, cams, "Monitor", e);
            }
            sb.Append(cams.Length == 0 ? " none\n" : cams.ToString() + "\n");
            sb.Append("turret seats:\n").Append(JointSystem.TurretSummary(em));
            sb.Append("mod joints:\n").Append(JointSystem.State(em));
            return sb.ToString();
        }

        static unsafe string Strain(EntityManager em)
        {
            var joints = Ecs.Joints(em, out int n); if (joints == null) return "  (not available)\n";
            var top = new List<(float r, int i)>();
            var live = JointSystem.LiveSet(em, n);   // 0.31.26: a broken link keeps its strain frozen at the break (1e35 when the mod/decoupler released it) [recon/SpaceshipCheckJoints.cs:113-122]
            for (int i = 0; i < n; i++) { var sj = &joints[i]; if (sj->_breakForce <= 0f || sj->_breakForce > 1e8f || (live != null && !live[i])) continue; float r = sj->_currentStrain / sj->_breakForce; if (r > 0.05f) top.Add((r, i)); }
            top.Sort((a, b) => b.r.CompareTo(a.r));
            var sb = new StringBuilder();
            for (int k = 0; k < top.Count && k < 10; k++)
            {
                var sj = &joints[top[k].i];
                sb.Append("  joint ").Append(top[k].i).Append(" blocks ").Append(sj->_a.Index).Append(" (part ").Append(Ecs.Parent(em, sj->_a).Index).Append(") - ").Append(sj->_b.Index)
                  .Append(": ").Append(sj->_currentStrain.ToString("F0")).Append(" / ").Append(sj->_breakForce.ToString("F0")).Append(" (").Append((100f * top[k].r).ToString("F0")).Append("%)\n");
            }
            return sb.Length == 0 ? "  none above 5%\n" : sb.ToString();
        }

        static unsafe float3 Vel(EntityManager em, Entity part, out float3 w)
        {
            w = default;
            if (!Ecs.HasC(em, part, Ecs.tiVel)) return default;
            var pv = (PhysicsVelocity*)Ecs.RO(em, part, Ecs.tiVel); w = pv->Angular; return pv->Linear;
        }
        static unsafe string Air(EntityManager em, Entity e)
        {
            try { var p = Ecs.HasC(em, e, tiAir) ? (float*)Ecs.RO(em, e, tiAir) : null; return p != null ? p[0].ToString("F3") : "?"; } catch (Exception) { return "?"; }
        }
        static string Fmt(float3 p) => "(" + (p.x / 1000f).ToString("F1") + ", " + (p.y / 1000f).ToString("F1") + ", " + (p.z / 1000f).ToString("F1") + ") km";
    }
}
