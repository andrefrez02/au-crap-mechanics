using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

namespace CrapMechanics
{
    // 0.26.7 (Andre: "give weight to the joints so it's an even match"). The piston's weld gives way because Unity Physics' solver
    // solves each joint by pushing both sides in inverse proportion to their masses: with 71 t on one side and 65 kg on the other (1100:1),
    // only the light piece "obeys" the joint, and the error left at each iteration never closes. Here, while there is a piston
    // weld between two parts, the lighter one is worth at least 1/MassRatio of the heavier one FOR PHYSICS (PhysicsMass). The
    // blueprint doesn't change, and Unity Physics' gravity is an acceleration (it doesn't depend on mass), so nothing falls faster. In a
    // chain (ship -> leg -> foot) the math propagates: the foot is worth 1/R of the already adjusted leg. It goes away when the weld stops existing.
    //
    // 0.26.8 (Andre: car with a mini piston lying sideways leaving a gap at the wheel — "the piston is sagging a lot"): there the weight falls on the TIP
    // of the piston (the wheel), so what gives way is the weld's TWIST, not its length. The solver solves the twist through inertia, and inertia
    // goes with size squared: a body of meters against a 25 cm hub gives 1000:1 even with the mass at 10:1. Now inertia
    // is balanced too: each axis of the light piece is worth at least 1/R of the heavy one's largest axis.
    // Turret Seat: whatever is on the plates is attached to the ship's pivot by a ball-and-socket — only the mass counts (the turret already
    // handles the inertia: it zeroes it to aim).
    internal static partial class JointSystem
    {
        public static float MassRatio = 10f;   // [Piston] mass_ratio_max (0 or 1 = off)

        class PartMass { public Entity part; public float invM; public float3 invI; public float writtenM; public float3 writtenI; public bool wasMoved; public float logged; public bool massOnly; }
        static readonly Dictionary<long, PartMass> masses = new Dictionary<long, PartMass>();

        static unsafe PhysicsMass* PM(EntityManager em, Entity part) =>
            Ecs.IsPart(em, part) && Ecs.HasC(em, part, Ecs.tiMass) ? (PhysicsMass*)em.GetComponentDataRawRW(part, Ecs.tiMass) : null;

        static float Inv(float x) => x > 0f ? 1f / x : float.PositiveInfinity;
        static float MaxI(float3 invI) { float a = Inv(invI.x), b = Inv(invI.y), c = Inv(invI.z); float m = 0f; foreach (var v in new[] { a, b, c }) if (!float.IsInfinity(v) && v > m) m = v; return m; }

        unsafe static void Register(EntityManager em, Entity p, bool massOnly, HashSet<long> onlyM)
        {
            long k = Ecs.K(p);
            var pm = PM(em, p); if (pm == null) return;
            // new, or the game rewrote the mass (part rebuilt/recalculated): what is there is the original
            if (!masses.TryGetValue(k, out var r) || (r.wasMoved && pm->InverseMass != r.writtenM) || (!r.wasMoved && pm->InverseMass != r.invM))
                masses[k] = r = new PartMass { part = p, invM = pm->InverseMass, invI = pm->InverseInertia, writtenM = pm->InverseMass, writtenI = pm->InverseInertia };
            if (massOnly) onlyM.Add(k);
        }

        /// <summary>Every frame, after the joints: recalculates how much each light part held by a piston weld (or by the turret's pivot)
        /// is worth for physics.</summary>
        static unsafe void BalanceMasses(EntityManager em)
        {
            var edges = new List<(long a, long b, bool spin)>(); var onlyM = new HashSet<long>();
            if (Rigid && MassRatio > 1f)
            {
                foreach (var j in allJoints.Values)
                    if (j.state == 2 && j.kind == Kind.Piston && !Ecs.IsNull(j.partA) && !Ecs.IsNull(j.partB) && j.partA.Index != j.partB.Index)
                    { Register(em, j.partA, false, onlyM); Register(em, j.partB, false, onlyM); edges.Add((Ecs.K(j.partA), Ecs.K(j.partB), true)); }
                var fromPiston = new HashSet<long>(); foreach (var (a, b, _) in edges) { fromPiston.Add(a); fromPiston.Add(b); }
                foreach (var t in turrets.Values)
                    if (t.state == 2 && !t.wholeShip && !Ecs.IsNull(t.partBase))
                        foreach (var m in t.movs)
                            if (!Ecs.IsNull(m.part) && m.part.Index != t.partBase.Index)
                            { Register(em, t.partBase, true, onlyM); Register(em, m.part, true, onlyM); edges.Add((Ecs.K(t.partBase), Ecs.K(m.part), false)); }
                // 0.26.11 (Andre's car: body 8.2 t -> steering bearing (servo) -> 83 kg axle -> 2 mini pistons -> hubs ->
                // bearings -> wheels; right after separating, the mini piston was already 11 cm compressed and the wheel came loose). The light axle sits
                // between the body and the welds, and the steering bearing (100:1) wasn't counted. Now the bearing counts when its
                // LIGHTER side (real mass) is already held by a piston weld — the piston + bearing chain. A bearing straight on the
                // hull with a loose light piece (flap, wheel) stays out. A rigid servo is a weld: it balances the twist too; free/motor
                // only the mass.
                foreach (var j in allJoints.Values)
                {
                    if (j.state != 2 || j.kind != Kind.Bearing || Ecs.IsNull(j.partA) || Ecs.IsNull(j.partB) || j.partA.Index == j.partB.Index) continue;
                    long ka = Ecs.K(j.partA), kb = Ecs.K(j.partB);
                    if (!fromPiston.Contains(ka) && !fromPiston.Contains(kb)) continue;
                    Register(em, j.partA, false, onlyM); Register(em, j.partB, false, onlyM);
                    if (!masses.TryGetValue(ka, out var ra) || !masses.TryGetValue(kb, out var rb)) continue;
                    long light = Inv(ra.invM) <= Inv(rb.invM) ? ka : kb;
                    if (fromPiston.Contains(light)) edges.Add((ka, kb, j.mode == 2));
                }
            }
            var used = new HashSet<long>(); foreach (var (a, b, _) in edges) { used.Add(a); used.Add(b); }
            // effective mass and inertia (largest axis); a static/kinematic part (invM 0) counts as infinite and isn't touched
            var ef = new Dictionary<long, float>(); var efI = new Dictionary<long, float>();
            foreach (var k in used) { if (!masses.TryGetValue(k, out var r)) continue; ef[k] = Inv(r.invM); }
            for (int it = 0; it < 8; it++)
            {
                bool changed = false;
                foreach (var (a, b, spin) in edges)
                {
                    if (!ef.TryGetValue(a, out var ma) || !ef.TryGetValue(b, out var mb)) continue;
                    if (float.IsInfinity(ma) || float.IsInfinity(mb)) continue;   // against the ground/static there's no ratio to correct
                    float na = System.Math.Max(ma, mb / MassRatio), nb = System.Math.Max(mb, ma / MassRatio);
                    if (na > ma * 1.0001f) { ef[a] = na; changed = true; }
                    if (nb > mb * 1.0001f) { ef[b] = nb; changed = true; }
                }
                if (!changed) break;
            }
            // inertia (largest axis) already with the mass factor; then balance the twist through the welds
            foreach (var k in used) { if (!masses.TryGetValue(k, out var r) || !ef.ContainsKey(k)) continue; float m0 = Inv(r.invM); float f = float.IsInfinity(m0) || m0 <= 0f ? 1f : ef[k] / m0; efI[k] = MaxI(r.invI) * f; }
            for (int it = 0; it < 8; it++)
            {
                bool changed = false;
                foreach (var (a, b, spin) in edges)
                {
                    if (!spin || !ef.TryGetValue(a, out var ma) || !ef.TryGetValue(b, out var mb) || float.IsInfinity(ma) || float.IsInfinity(mb)) continue;
                    float ia = efI[a], ib = efI[b];
                    float nia = System.Math.Max(ia, ib / MassRatio), nib = System.Math.Max(ib, ia / MassRatio);
                    if (nia > ia * 1.0001f) { efI[a] = nia; changed = true; }
                    if (nib > ib * 1.0001f) { efI[b] = nib; changed = true; }
                }
                if (!changed) break;
            }
            // floor of each axis = the neighbour's largest axis / R (not its own: otherwise the piece would become a sphere for no reason)
            var floor = new Dictionary<long, float>();
            foreach (var (a, b, spin) in edges)
            {
                if (!spin || !efI.ContainsKey(a) || !efI.ContainsKey(b)) continue;
                floor[a] = System.Math.Max(floor.TryGetValue(a, out var pa) ? pa : 0f, efI[b] / MassRatio);
                floor[b] = System.Math.Max(floor.TryGetValue(b, out var pb) ? pb : 0f, efI[a] / MassRatio);
            }
            // apply / give back
            var exit = new List<long>();
            foreach (var kv in masses)
            {
                var r = kv.Value; var pm = PM(em, r.part);
                if (pm == null) { exit.Add(kv.Key); continue; }
                float m0 = Inv(r.invM);
                bool uses = used.Contains(kv.Key) && !float.IsInfinity(m0) && ef.ContainsKey(kv.Key);
                float targetM = uses ? ef[kv.Key] : m0;
                float fM = uses && targetM > m0 * 1.001f ? m0 / targetM : 1f;   // <= 1: multiplies the inverse
                // inertia: each axis = max(axis x mass factor, twist target); an infinite axis (invI 0) stays
                bool spin = uses && !onlyM.Contains(kv.Key);
                float3 newI = r.invI;
                float targetI = spin && floor.TryGetValue(kv.Key, out var pI) ? pI : 0f;
                if (spin)
                {
                    float3 i0 = r.invI; float3 n = i0;
                    float Axis(float inv) { if (inv <= 0f) return 0f; float I = (1f / inv) / fM; if (targetI > I) I = targetI; return 1f / I; }
                    n.x = Axis(i0.x); n.y = Axis(i0.y); n.z = Axis(i0.z); newI = n;
                }
                bool shouldMove = uses && (fM < 1f || (spin && (newI.x != r.invI.x || newI.y != r.invI.y || newI.z != r.invI.z)));
                if (shouldMove)
                {
                    pm->InverseMass = r.invM * fM; r.writtenM = pm->InverseMass;
                    if (spin) { pm->InverseInertia = newI; r.writtenI = newI; }
                    r.wasMoved = true; r.massOnly = !spin;
                    float iBefore = MaxI(r.invI), iAfter = MaxI(newI);
                    if (System.Math.Abs(targetM - r.logged) > 0.05f * targetM)
                    {
                        r.logged = targetM;
                        ModLog.Info("joints: part " + r.part.Index + " (" + m0.ToString("F0") + " kg) counts as " + targetM.ToString("F0") + " kg"
                            + (spin && iAfter > iBefore * 1.01f ? ", turning inertia x" + (iAfter / System.Math.Max(1e-6f, iBefore)).ToString("F0") : "")
                            + " for the " + (spin ? "piston welds" : "turret pivot") + " (at most " + MassRatio.ToString("F0") + ":1 between the two sides)");
                    }
                }
                else
                {
                    if (r.wasMoved)
                    {
                        pm->InverseMass = r.invM; r.writtenM = r.invM;
                        if (!r.massOnly) pm->InverseInertia = r.invI;   // the turret's inertia is its own business
                        r.wasMoved = false; r.logged = 0f;
                        ModLog.Info("joints: part " + r.part.Index + " back to its real " + m0.ToString("F0") + " kg");
                    }
                    if (!used.Contains(kv.Key)) exit.Add(kv.Key);
                }
            }
            foreach (var k in exit) masses.Remove(k);
        }
    }
}
