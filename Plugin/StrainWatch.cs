using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

namespace CrapMechanics
{
    /// <summary>0.31.14 (Andre, 11/11: the giant ship "broke around the bearings and pistons first, usually microframes", in the air AND in
    /// space, worse with speed). In the game (1.0.311) a block comes off when a SpaceshipJoint (block-to-block link) gets _currentStrain >=
    /// _breakForce. The strain comes from shockwaves: collisions, explosions and SpaceshipInertiaToShockwaveSystem — the velocity change of
    /// each block in ONE physics step (SCInertiaToShockwave._frameDeltaVelocity) times its mass. So a part that is jerked (a joint
    /// correcting, a piston pushing, a heavy piece hanging from a thin link) loads the links around it even with no air. Log only, changes
    /// nothing; runs with [Debug] strain_watch (0.31.15: its own switch, off by default):
    ///  - every frame: the strain/break ratio of every game link; when a link disappears without the mod or a decoupler breaking it, one
    ///    "strain: BROKE" line with both blocks, the ratio it had, its peak in the last 3 s, the jolt (delta-v) of both blocks and the
    ///    nearest mod joint;
    ///  - every frame: how far each active mod joint is from its fit (cm and degrees) — the "opening";
    ///  - with each trace line (2 s): the 5 links nearest to breaking and the joint that opened the most.</summary>
    internal static class StrainWatch
    {
        public static bool Enabled = false;   // 0.31.15: its own switch, [Debug] strain_watch (it reads every link of the ship every frame)
        static TypeIndex tiInertia; static bool ok, failed, hasInertia;
        static Entity[] prevA = new Entity[0], prevB = new Entity[0]; static float[] prevR = new float[0]; static bool[] prevLive = new bool[0]; static int prevN = -1;
        class Peak { public Entity a, b; public float r, st, bf, t; }
        static readonly Dictionary<long, Peak> recent = new Dictionary<long, Peak>();
        static float t, tPrune, tBreakWindow, tReport; static int breaksInWindow, breaksHidden, warnings;
        static float openPos, openAng, openPosT, openAngT; static string openPosWho, openAngWho; static float tOpenLog;

        public static void Forget() { prevN = -1; recent.Clear(); openPos = openAng = 0f; openPosWho = openAngWho = null; }

        static bool Prepare()
        {
            if (ok) return true; if (failed) return false;
            try
            {
                try { tiInertia = TypeManager.GetTypeIndex(Il2CppType.Of<SCInertiaToShockwave>()); hasInertia = true; } catch (Exception) { hasInertia = false; }
                ok = true;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("strain watch: off (" + ex.Message + ")"); }
            return ok;
        }

        static long Key(Entity a, Entity b) => ((long)a.Index << 32) | (uint)b.Index;

        /// <summary>Every simulation frame, AFTER JointSystem.Step.
        /// 0.31.26 (item 15): _joints never shrinks — a broken joint stays in it with its strain frozen at the break (>= its _breakForce) and only
        /// leaves _connectedJoints [recon/SpaceshipCheckJoints.cs:113-122]; FrameInit only zeroes the connected ones [SpaceshipPhysicsFrameInit.cs:32].
        /// So a BREAK = an index that left _connectedJoints since the last frame (indices are stable), and its value is the frozen one. Until 0.31.25 it
        /// waited for _joints to shrink (never) and showed broken links at 100%+ in "nearest to breaking" until the recall.</summary>
        public static unsafe void Frame(EntityManager em, float dt)
        {
            if (!Enabled || !Prepare()) return;
            t += dt;
            try
            {
                var joints = Ecs.Joints(em, out int n);
                if (joints == null || n == 0) { prevN = -1; return; }
                var live = JointSystem.LiveSet(em, n);
                if (live == null) { prevN = -1; return; }
                if (prevN == n) Breaks(em, joints, n, live);   // a new launch (other list) only starts over
                if (prevA.Length < n) { int c = System.Math.Max(n, prevA.Length * 2); prevA = new Entity[c]; prevB = new Entity[c]; prevR = new float[c]; prevLive = new bool[c]; }
                for (int i = 0; i < n; i++)
                {
                    var sj = &joints[i];
                    float bf = sj->_breakForce, st = sj->_currentStrain;
                    prevA[i] = sj->_a; prevB[i] = sj->_b; prevLive[i] = live[i];
                    if (!live[i]) { prevR[i] = -1f; continue; }   // peaks only on links still connected (a broken one keeps its frozen strain)
                    float r = (bf <= 0f || bf > 1e8f || st < 0f || st > 1e8f) ? -1f : st / bf;
                    prevR[i] = r;
                    if (r > 0.2f)
                    {
                        long k = Key(sj->_a, sj->_b);
                        if (!recent.TryGetValue(k, out var p)) { p = new Peak(); recent[k] = p; }
                        if (r >= p.r || t - p.t > 3f) { p.a = sj->_a; p.b = sj->_b; p.r = r; p.st = st; p.bf = bf; p.t = t; }
                    }
                }
                prevN = n;
                if (t - tPrune > 1f) { tPrune = t; var old = new List<long>(); foreach (var kv in recent) if (t - kv.Value.t > 3f) old.Add(kv.Key); foreach (var k in old) recent.Remove(k); }
                Opening(em);
                if (t - tReport >= 2f) { tReport = t; ModLog.Info(Report(em)); }
            }
            catch (Exception ex) { if (warnings++ < 3) ModLog.Warn("strain watch: " + ex.Message); }
        }

        static unsafe void Breaks(EntityManager em, SpaceshipJoint* joints, int n, bool[] live)
        {
            if (t - tBreakWindow > 1f) { if (breaksHidden > 0) ModLog.Info("strain: ... and " + breaksHidden + " more link(s) broke in that second"); tBreakWindow = t; breaksInWindow = 0; breaksHidden = 0; }
            int gone = 0;
            for (int i = 0; i < n; i++)
            {
                if (!prevLive[i] || live[i]) continue;                               // only what left _connectedJoints since the last frame
                var sj = &joints[i]; var a = sj->_a; var b = sj->_b;
                if (a.Index != prevA[i].Index || b.Index != prevB[i].Index) continue;
                if (JointSystem.ModReleased(i)) continue;                            // released on purpose by the mod (its own 1e35, pending set)
                if (!Ecs.Alive(em, a) || !Ecs.Alive(em, b)) { gone++; continue; }   // block destroyed / unloaded
                float st = sj->_currentStrain, bf = sj->_breakForce;                 // frozen at the break
                if (st >= 1e30f) continue;                                           // 1e35: a decoupler or a ClimaBomb [recon/EPC_SCDecoupler.cs:51; EPC_SCClimaBomb.cs:121]
                if (breaksInWindow++ >= 6) { breaksHidden++; continue; }
                ModLog.Info(BreakLine(em, a, b, bf > 0f ? st / bf : -1f, prevR[i]));
            }
            if (gone > 20) ModLog.Info("strain: " + gone + " link(s) left with their blocks (blocks destroyed or unloaded)");
        }

        static string BreakLine(EntityManager em, Entity a, Entity b, float breakR, float lastR)
        {
            var sb = new StringBuilder("strain: BROKE ");
            sb.Append(Who(em, a)).Append(" - ").Append(Who(em, b));
            if (breakR >= 0f) sb.Append(" | at the break ").Append((100f * breakR).ToString("F0")).Append("% of its break force");
            sb.Append(" | last frame before ").Append(lastR < 0f ? "-" : (100f * lastR).ToString("F0") + "%");
            if (lastR >= 0f && lastR < 0.05f) sb.Append(" (nothing building up: one sudden jolt)");
            if (recent.TryGetValue(Key(a, b), out var p)) sb.Append(" | peak ").Append((100f * p.r).ToString("F0")).Append("% (").Append(p.st.ToString("F0")).Append(" / ").Append(p.bf.ToString("F0")).Append(") ").Append((t - p.t).ToString("F1")).Append(" s before");
            sb.Append(" | jolt ").Append(Jolt(em, a)).Append(" / ").Append(Jolt(em, b));
            sb.Append(" | ").Append(NearestJoint(em, Ecs.Position(em, a)));
            var part = Ecs.Parent(em, a);
            if (Ecs.HasC(em, part, Ecs.tiVel)) { var v = Vel(em, part, out var w); sb.Append(" | part ").Append(part.Index).Append(" ").Append(Ecs.Len(v).ToString("F0")).Append(" m/s, spin ").Append((Ecs.Len(w) * 57.29578f).ToString("F0")).Append(" deg/s"); }
            return sb.ToString();
        }

        static string Who(EntityManager em, Entity e)
        {
            string nm = null; try { nm = BlockIndex.NameOfHash(Ecs.Prefab(em, e)); } catch (Exception) { }
            return (nm ?? "block") + " " + e.Index;
        }
        /// <summary>Velocity change of the block in the last physics step (what the game turns into strain), m/s.</summary>
        static unsafe string Jolt(EntityManager em, Entity e)
        {
            if (!hasInertia || !Ecs.HasC(em, e, tiInertia)) return "-";
            var p = (float3*)Ecs.RO(em, e, tiInertia); if (p == null) return "-";
            return Ecs.Len(*p).ToString("F2") + " m/s";
        }
        static unsafe float3 Vel(EntityManager em, Entity part, out float3 w)
        {
            var pv = (PhysicsVelocity*)Ecs.RO(em, part, Ecs.tiVel); w = pv->Angular; return pv->Linear;
        }
        static string NearestJoint(EntityManager em, float3 p)
        {
            Joint best = null; float md = 3f;
            foreach (var j in JointSystem.ActiveJoints())
            {
                if (!Ecs.Alive(em, j.block)) continue;
                float d = Ecs.Dist(Ecs.Position(em, j.block), p); if (d < md) { md = d; best = j; }
            }
            return best == null ? "no mod joint within 3 m" : "near " + best.name + " " + best.block.Index + " (" + md.ToString("F1") + " m)";
        }

        /// <summary>How far each active bearing/piston is from its fit: bearing = distance between the two frame points; piston = the same,
        /// sideways only (it slides along the axis); both = angle between the axes.</summary>
        static void Opening(EntityManager em)
        {
            foreach (var j in JointSystem.ActiveJoints())
            {
                if (j.state != 2 || (j.kind != Kind.Bearing && j.kind != Kind.Piston) || j.tStart > 0f || j.tPullT >= 0f) continue;
                if (!Ecs.Alive(em, j.partA) || !Ecs.Alive(em, j.partB) || !Ecs.HasC(em, j.partA, Ecs.tiLT) || !Ecs.HasC(em, j.partB, Ecs.tiLT)) continue;
                var la = Ecs.LT(em, j.partA); var lb = Ecs.LT(em, j.partB);
                float sa = la.Scale == 0f ? 1f : la.Scale, sb = lb.Scale == 0f ? 1f : lb.Scale;
                var pa = Ecs.Add(la.Position, Ecs.Rot(la.Rotation, Ecs.Mul(j.fa.Position, sa)));
                var pb = Ecs.Add(lb.Position, Ecs.Rot(lb.Rotation, Ecs.Mul(j.fb.Position, sb)));
                var ea = Ecs.Norm(Ecs.Rot(la.Rotation, j.fa.Axis)); var eb = Ecs.Norm(Ecs.Rot(lb.Rotation, j.fb.Axis));
                var d = Ecs.Sub(pb, pa);
                if (j.kind == Kind.Piston) d = Ecs.Sub(d, Ecs.Mul(ea, Ecs.Dot(d, ea)));
                float pos = Ecs.Len(d);
                float ang = (float)System.Math.Acos(System.Math.Min(1f, System.Math.Abs(Ecs.Dot(ea, eb)))) * 57.29578f;
                string who = j.name + " " + j.block.Index;
                if (pos > openPos) { openPos = pos; openPosWho = who; openPosT = t; }
                if (ang > openAng) { openAng = ang; openAngWho = who; openAngT = t; }
                if ((pos > 0.25f || ang > 10f) && t - tOpenLog > 1f) { tOpenLog = t; ModLog.Info("strain: " + who + " is OPEN " + (pos * 100f).ToString("F0") + " cm / " + ang.ToString("F1") + " degrees off its fit"); }
            }
        }

        /// <summary>For the trace line (every 2 s): the links nearest to breaking in the last 2 s and the joint that opened the most.</summary>
        public static string Report(EntityManager em)
        {
            if (!ok) return "strain: off";
            var sb = new StringBuilder("strain: ");
            var top = new List<Peak>(); foreach (var p in recent.Values) if (t - p.t <= 2f) top.Add(p);
            top.Sort((x, y) => y.r.CompareTo(x.r));
            if (top.Count == 0) sb.Append("no link above 20% of its break force");
            else
            {
                sb.Append("links nearest to breaking (2 s):");
                for (int i = 0; i < top.Count && i < 5; i++)
                {
                    var p = top[i]; string a = Ecs.Alive(em, p.a) ? Who(em, p.a) : "block " + p.a.Index, b = Ecs.Alive(em, p.b) ? Who(em, p.b) : "block " + p.b.Index;
                    sb.Append(i == 0 ? " " : "; ").Append(a).Append(" - ").Append(b).Append(" ").Append((100f * p.r).ToString("F0")).Append("%");
                    if (Ecs.Alive(em, p.a)) sb.Append(" (jolt ").Append(Jolt(em, p.a)).Append(", ").Append(NearestJoint(em, Ecs.Position(em, p.a))).Append(")");
                }
            }
            sb.Append(" | most open joint: ");
            if (openPosWho == null && openAngWho == null) sb.Append("none active");
            else sb.Append(openPosWho ?? "-").Append(" ").Append((openPos * 100f).ToString("F1")).Append(" cm; ").Append(openAngWho ?? "-").Append(" ").Append(openAng.ToString("F1")).Append(" degrees");
            openPos = openAng = 0f; openPosWho = openAngWho = null;
            return sb.ToString();
        }
    }
}
