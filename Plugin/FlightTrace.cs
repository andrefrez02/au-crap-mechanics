using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

namespace CrapMechanics
{
    /// <summary>0.31.8 (Andre, 31/10: "the ship climbed with only the flat thrusters and the normal ones on, and when it reached space it started
    /// to fall by itself" — the black box showed it turning 1.2-2.1 deg/s the whole flight with no input, faster as the air got thinner = a
    /// constant torque). Log only, changes nothing: every 2 s for the first 3 minutes of each launch (then every 10 s), one "trace:" line with
    ///  1) the spin, as an axis in the hull's own axes (the garage axes): which way it pitches/rolls/yaws;
    ///  2) the vehicle's centre of mass with the REAL masses and with the masses the physics sees (the mod's mass balance must keep them equal);
    ///  3) the thrust of every thruster that is pushing (0.31.26: the game's own factor, _frameEfficiency x _atmoUnderwaterMultiplier01), its total, and the
    ///     torque it makes around the real centre of mass + where the thrust line passes;
    ///  4) the same torque if every thruster were exactly where the garage put it relative to the hull — the difference is what the mod's
    ///     joints (pistons, bearings) bending under the thrust add; the rest is how the ship is built;
    ///  5) the thrusters that are NOT on the hull and have moved from their garage pose relative to the hull (degrees and cm).
    /// [Debug] flight_trace turns it off.</summary>
    internal static class FlightTrace
    {
        public static bool Enabled = false;   // [Debug] flight_trace (off by default)
        static TypeIndex tiGT, tiElec; static bool ok, failed, hasElec;
        static long vehicleKey; static float tSince, tNext; static int warnings;
        static Entity refBlock;
        static readonly Dictionary<ulong, string> names = new Dictionary<ulong, string>(); static bool namesDone;

        public static void Forget() { vehicleKey = 0; refBlock = Entity.Null; StrainWatch.Forget(); }

        static bool Prepare()
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiGT = TypeManager.GetTypeIndex(Il2CppType.Of<GarageTransform>());
                try { tiElec = TypeManager.GetTypeIndex(Il2CppType.Of<SCThrusterElectric>()); hasElec = true; } catch (Exception) { hasElec = false; }
                ok = true;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("flight trace: off (" + ex.Message + ")"); }
            return ok;
        }

        /// <summary>Called by the black box every ~0.5 s with the time since the last call and the biggest part (the vehicle you are on).</summary>
        public static void Step(EntityManager em, float span, Entity big, List<Entity> parts)
        {
            if (!Enabled || !Prepare() || !Thrusters.Ready) return;
            long k = Ecs.K(big);
            if (k != vehicleKey) { vehicleKey = k; tSince = 0f; tNext = 1f; refBlock = Entity.Null; }
            tSince += span;
            if (tSince < tNext) return;
            tNext = tSince + (tSince < 180f ? 2f : 10f);
            try { ModLog.Info(Line(em, big, parts)); }   // (0.31.15: the strain watch logs on its own, [Debug] strain_watch)
            catch (Exception ex) { if (warnings++ < 3) ModLog.Warn("flight trace: " + ex.Message); }
        }

        static unsafe bool GT(EntityManager em, Entity e, out RigidTransform g)
        {
            g = default;
            if (!Ecs.HasC(em, e, tiGT)) return false;
            var p = (RigidTransform*)Ecs.RO(em, e, tiGT); if (p == null) return false;
            g = *p; return true;
        }
        static float Angle(quaternion a, quaternion b) { float d = System.Math.Abs(math.dot(a.value, b.value)); return math.degrees(2f * (float)System.Math.Acos(System.Math.Min(1f, d))); }
        static string V(float3 v, string f = "F2") => "(" + v.x.ToString(f) + ", " + v.y.ToString(f) + ", " + v.z.ToString(f) + ")";
        static string Dir(float3 v) { float l = math.length(v); return l < 1e-6f ? "(0, 0, 0)" : V(v / l); }

        static string Name(EntityManager em, Entity e)
        {
            if (!namesDone && BlockIndex.Ready)
            {
                namesDone = true;
                foreach (var n in BlockIndex.AllPrefabs)
                    if (n.IndexOf("Thrust", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Fan", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("RCS", StringComparison.OrdinalIgnoreCase) >= 0)
                    { ulong h = Ecs.HashPrefab(n); if (h != 0) names[h] = n; }
            }
            ulong ph = Ecs.Prefab(em, e);
            return names.TryGetValue(ph, out var nm) ? nm : "thruster";
        }

        static unsafe string Line(EntityManager em, Entity big, List<Entity> parts)
        {
            var sb = new StringBuilder();
            var hull = Ecs.LT(em, big); quaternion hqi = math.inverse(hull.Rotation);
            float3 H(float3 w) => math.rotate(hqi, w);   // world vector -> hull (garage) axes
            // the vehicle: parts near the hull moving with it
            var bigC = Ecs.CenterOfMass(em, big);
            float3 bigV = default, bigW = default; quaternion inertiaRot = quaternion.identity;
            if (Ecs.HasC(em, big, Ecs.tiVel)) { var pv = (PhysicsVelocity*)Ecs.RO(em, big, Ecs.tiVel); bigV = pv->Linear; bigW = pv->Angular; }
            if (Ecs.HasC(em, big, Ecs.tiMass)) inertiaRot = ((PhysicsMass*)Ecs.RO(em, big, Ecs.tiMass))->Transform.rot;
            var mine = new HashSet<long>(); double mR = 0, mP = 0; double3 sR = default, sP = default;
            foreach (var p in parts)
            {
                if (!JointSystem.RealOf(em, p, out float mr, out float3 cr, out float mp, out float3 cp)) continue;
                float3 v = default; if (Ecs.HasC(em, p, Ecs.tiVel)) v = ((PhysicsVelocity*)Ecs.RO(em, p, Ecs.tiVel))->Linear;
                if (math.length(cp - bigC) > 80f || math.length(v - bigV) > 15f) continue;
                mine.Add(Ecs.K(p)); mR += mr; mP += mp; sR += (double3)cr * mr; sP += (double3)cp * mp;
            }
            if (mR <= 0 || mP <= 0) return "trace: no vehicle";
            var cReal = (float3)(sR / mR); var cPhys = (float3)(sP / mP);
            // 1) spin: PhysicsVelocity.Angular is in the body's inertia axes -> hull axes
            float3 wHull = math.rotate(inertiaRot, bigW); float wDeg = math.degrees(math.length(wHull));
            sb.Append("trace: t=").Append(tSince.ToString("F0")).Append(" s | spin ").Append(wDeg.ToString("F2")).Append(" deg/s around hull axis ").Append(Dir(wHull))
              .Append(" | ").Append(mine.Count).Append(" part(s), real ").Append((mR / 1000.0).ToString("F1")).Append(" t, physics ").Append((mP / 1000.0).ToString("F1"))
              .Append(" t, centre of mass physics - real = ").Append(V(H(cPhys - cReal))).Append(" m");
            // reference block on the hull, for the garage poses
            bool refOk = false; float3 refPos = default; quaternion refRot = quaternion.identity; RigidTransform gRef = default;
            if (!Ecs.Alive(em, refBlock) || Ecs.Parent(em, refBlock).Index != big.Index || !GT(em, refBlock, out gRef)) refBlock = Entity.Null;
            foreach (var e in Thrusters.Entities)
            {
                if (!Ecs.IsNull(refBlock)) break;
                if (Ecs.Alive(em, e) && Ecs.Parent(em, e).Index == big.Index && GT(em, e, out gRef)) refBlock = e;
            }
            if (Ecs.IsNull(refBlock))   // no thruster on the hull: any block of it (once per launch)
                foreach (var e in Ecs.Components(em)) if (Ecs.Parent(em, e).Index == big.Index && GT(em, e, out gRef)) { refBlock = e; break; }
            if (!Ecs.IsNull(refBlock) && GT(em, refBlock, out gRef)) { Ecs.Pose(em, refBlock, out refPos, out refRot); refOk = true; }
            quaternion gRefInv = math.inverse(gRef.rot), refInv = math.inverse(refRot);
            // 3/4/5) thrusters
            int off = Thrusters.MaxForceOffset; int pushing = 0, idle = 0, noPower = 0; float3 F = default, T = default, Tg = default;
            var byKind = new Dictionary<string, (int n, float3 f)>(); var moved = new List<(float tilt, string s)>();
            float hullTilt = 0f, hullMove = 0f;
            foreach (var e in Thrusters.Entities)
            {
                if (!Ecs.Alive(em, e) || !Ecs.HasC(em, e, Thrusters.Ti)) continue;
                var part = Ecs.Parent(em, e); if (Ecs.IsNull(part) || !mine.Contains(Ecs.K(part))) continue;
                var fl = *(float3*)((byte*)Ecs.RO(em, e, Thrusters.Ti) + off);
                // 0.31.26 (item 31): the game's own factor (_frameEfficiency × _atmoUnderwaterMultiplier01, signed), not power used / max
                // power or |signal| — see Thrusters.Efficiency [recon/EPC_SCThruster.cs:250, 260]
                if (!Thrusters.Efficiency(em, e, out float ratio)) ratio = 0f;
                if (System.Math.Abs(ratio) < 0.01f)
                {
                    // an electric thruster asked to push (signal) but with no power: counted apart, as before
                    if (hasElec && Ecs.HasC(em, e, tiElec) && !Ecs.HasPower(em, e, out _) && Ecs.Signal(em, e, out float sv, out _) && System.Math.Abs(sv) > 0.01f) noPower++;
                    idle++; continue;
                }
                pushing++;
                Ecs.Pose(em, e, out float3 pos, out quaternion rot);
                float3 f = math.rotate(rot, fl) * ratio; F += f; T += math.cross(pos - cReal, f);
                string kind = Name(em, e); byKind.TryGetValue(kind, out var a); byKind[kind] = (a.n + 1, a.f + f);
                if (refOk && GT(em, e, out var g))
                {
                    quaternion qg = math.mul(gRefInv, g.rot), qn = math.mul(refInv, rot);
                    float3 pg = math.rotate(gRefInv, g.pos - gRef.pos), pn = math.rotate(refInv, pos - refPos);
                    float tilt = Angle(qg, qn), mv = math.length(pn - pg);
                    float3 posG = refPos + math.rotate(refRot, pg); quaternion rotG = math.mul(refRot, qg);
                    Tg += math.cross(posG - cReal, math.rotate(rotG, fl) * ratio);
                    if (part.Index == big.Index) { hullTilt = System.Math.Max(hullTilt, tilt); hullMove = System.Math.Max(hullMove, mv); }
                    else if (tilt > 0.3f || mv > 0.02f) moved.Add((tilt, kind + " " + e.Index + " (part " + part.Index + ") " + tilt.ToString("F1") + " deg, " + (mv * 100f).ToString("F0") + " cm"));
                }
                else Tg += math.cross(pos - cReal, f);
            }
            sb.Append(" | ").Append(pushing).Append(" thruster(s) pushing, ").Append(idle).Append(" idle").Append(noPower > 0 ? ", " + noPower + " WITHOUT POWER" : "");
            if (pushing == 0) return sb.ToString();
            float fl2 = math.lengthsq(F); float3 lineAt = fl2 > 1e-6f ? math.cross(F, T) / fl2 : default;
            sb.Append(" | thrust ").Append((math.length(F) / 1000f).ToString("F0")).Append(" kN along hull ").Append(Dir(H(F)))
              .Append(" | torque around the real centre of mass ").Append((math.length(T) / 1000f).ToString("F1")).Append(" kN·m around hull ").Append(Dir(H(T)))
              .Append(", thrust line ").Append(V(H(lineAt))).Append(" m from it")
              .Append(" | with every thruster at its garage pose: ").Append((math.length(Tg) / 1000f).ToString("F1")).Append(" kN·m around hull ").Append(Dir(H(Tg)))
              .Append(" — the joints bending add ").Append((math.length(T - Tg) / 1000f).ToString("F1")).Append(" kN·m");
            sb.Append(" | by type:");
            foreach (var kv in byKind) sb.Append(' ').Append(kv.Key).Append(" x").Append(kv.Value.n).Append(' ').Append((math.length(kv.Value.f) / 1000f).ToString("F0")).Append(" kN ").Append(Dir(H(kv.Value.f)));
            if (refOk)
            {
                sb.Append(" | hull thrusters vs garage (check, must be ~0): ").Append(hullTilt.ToString("F1")).Append(" deg, ").Append((hullMove * 100f).ToString("F0")).Append(" cm");
                moved.Sort((x, y) => y.tilt.CompareTo(x.tilt));
                sb.Append(" | off the hull, moved from the garage pose: ");
                if (moved.Count == 0) sb.Append("none");
                for (int i = 0; i < moved.Count && i < 8; i++) sb.Append(i > 0 ? "; " : "").Append(moved[i].s);
                if (moved.Count > 8) sb.Append("; +").Append(moved.Count - 8).Append(" more");
            }
            else sb.Append(" | no garage pose on the hull to compare with");
            return sb.ToString();
        }
    }
}
