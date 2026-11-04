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
    //
    // 0.30.3 (Andre: "my ship is off its axis, feels pulled, and absurdly heavy" — and with mass_ratio_max = 0 the big pistons fell and the
    // turret came apart, so the balance IS needed): the kilograms added to the light pieces were real for the whole ship (+27 t on a 103 t ship,
    // sitting on the turret plates and piston heads, away from the centre of mass). Now each vehicle (parts linked by these welds/pivots) gives the
    // same kilograms back from its heaviest part (the hull), and the hull's own centre of mass is moved so the VEHICLE's total mass and centre of
    // mass stay exactly where the real ones are. The solver still sees 10:1 at every weld; the thrusters see the real ship.
    internal static partial class JointSystem
    {
        public static float MassRatio = 10f;   // [Piston] mass_ratio_max (0 or 1 = off)
        public static float MassFactorMax = 25f;   // [Piston] mass_factor_max (0 = no cap)
        public static float TurretMassFactor = 4f;   // 0.31.2: [Turret Seat] mass_factor_max
        // 0.31.0 (Andre's 125 t ship, 31/10: 24 light pieces each counted as 8.6-9.7 t = +113-126 t of physics-only mass, and the hull came apart in
        // flight — the game's stress follows those kilograms; with mass_ratio_max = 0 the landing legs, dampers and flat fans held and only the
        // pieces BETWEEN two joints (a 104 kg piston head carrying a bearing) and the 10 t sections on mini pistons gave way). So:
        //  - a LEAF (a piece jointed to one single part) much lighter than that part (below 1/LeafRatio of it) is not balanced at all: it only
        //    carries its own weight, which the weld holds without help;
        //  - no piece is worth more than MassFactorMax times its real mass (and its inertia MassFactorMax² times);
        //  - the turret pivot is kept as it was (it came apart without it).
        const float LeafRatio = 50f;

        class PartMass { public Entity part; public float invM; public float3 invI; public float3 com, writtenCom; public float writtenM; public float3 writtenI; public bool wasMoved; public float logged; public bool massOnly; public bool donor; }
        static readonly Dictionary<long, PartMass> masses = new Dictionary<long, PartMass>();
        static readonly Dictionary<long, string> donorLog = new Dictionary<long, string>();
        static readonly HashSet<long> donorKeys = new HashSet<long>();
        static string lastSummary = "no physics-only mass";

        /// <summary>0.31.1 (black box): the last mass balance in one line.</summary>
        internal static string MassSummary() => lastSummary;
        /// <summary>0.31.1 (black box): " (real 104 kg)" when the mod changed this part's mass, "" otherwise.</summary>
        internal static string RealMassNote(Entity part) => masses.TryGetValue(Ecs.K(part), out var r) && r.wasMoved ? " (real " + Inv(r.invM).ToString("F0") + " kg" + (r.donor ? ", gives the kilograms back" : "") + ")" : "";
        class Donor { public long key; public float extra; public float3 moment; public int pieces; }

        /// <summary>0.30.3: per vehicle (parts linked by the edges), the heaviest part and what its light pieces gained: sum of the extra kg and
        /// of extra kg x world centre of mass of each piece.</summary>
        static unsafe List<Donor> Donors(EntityManager em, List<(long a, long b, bool spin)> edges, HashSet<long> used, Dictionary<long, float> ef)
        {
            var parent = new Dictionary<long, long>();
            long Find(long x) { while (parent.TryGetValue(x, out var p) && p != x) { parent[x] = parent.TryGetValue(p, out var pp) ? pp : p; x = p; } return x; }
            foreach (var k in used) parent[k] = k;
            foreach (var (a, b, _) in edges) { long ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
            var heavy = new Dictionary<long, long>(); var r = new List<Donor>();
            foreach (var k in used)
            {
                if (!masses.TryGetValue(k, out var pmk)) continue; float m0 = Inv(pmk.invM); if (float.IsInfinity(m0)) continue;
                long root = Find(k);
                if (!heavy.TryGetValue(root, out var h) || Inv(masses[h].invM) < m0) heavy[root] = k;
            }
            var byRoot = new Dictionary<long, Donor>();
            foreach (var kv in heavy) { var d = new Donor { key = kv.Value }; byRoot[kv.Key] = d; r.Add(d); }
            foreach (var k in used)
            {
                if (!masses.TryGetValue(k, out var pmk) || !ef.TryGetValue(k, out var target)) continue;
                float m0 = Inv(pmk.invM); if (float.IsInfinity(m0) || float.IsInfinity(target)) continue;
                float dm = target - m0; if (dm <= m0 * 0.001f) continue;
                if (!byRoot.TryGetValue(Find(k), out var d) || d.key == k) continue;
                try { var lt = Ecs.LT(em, pmk.part); var c = lt.Position + math.rotate(lt.Rotation, pmk.com * lt.Scale); d.extra += dm; d.moment += c * dm; d.pieces++; } catch (Exception) { }
            }
            r.RemoveAll(d => d.pieces == 0);
            return r;
        }

        /// <summary>0.30.3: the heaviest part loses the kilograms its pieces gained (at most half its own) and its centre of mass moves so the
        /// vehicle's total mass and centre of mass are the real ones.</summary>
        static unsafe void ApplyDonor(EntityManager em, Donor d)
        {
            if (!masses.TryGetValue(d.key, out var r)) return;
            var pm = PM(em, r.part); if (pm == null) return;
            float M = Inv(r.invM); if (float.IsInfinity(M) || M <= 0f) return;
            float give = System.Math.Min(d.extra, 0.5f * M); float f = d.extra > 0f ? give / d.extra : 0f;
            var lt = Ecs.LT(em, r.part);
            float3 c0 = lt.Position + math.rotate(lt.Rotation, r.com * lt.Scale);
            // (M - give) c' + give * (pieces' weighted position) = M c0  ->  c'
            float3 cw = (M * c0 - d.moment * f) / (M - give);
            float3 local = math.rotate(math.inverse(lt.Rotation), cw - lt.Position) / (lt.Scale > 0f ? lt.Scale : 1f);
            pm->InverseMass = 1f / (M - give); r.writtenM = pm->InverseMass;
            pm->Transform.pos = local; r.writtenCom = local;
            r.wasMoved = true; r.donor = true; r.massOnly = true;   // its inertia is left as it is
            string sig = (give / 100f).ToString("F0") + "|" + d.pieces;
            if (!donorLog.TryGetValue(d.key, out var prev) || prev != sig)
            {
                donorLog[d.key] = sig;
                ModLog.Info("joints: mass balance — +" + d.extra.ToString("F0") + " kg on " + d.pieces + " light piece(s) for the solver, given back by part " + r.part.Index
                    + " (" + M.ToString("F0") + " kg -> " + (M - give).ToString("F0") + " kg, its centre of mass moved " + math.length(cw - c0).ToString("F2") + " m)"
                    + (f < 0.999f ? " — only " + (100f * f).ToString("F0") + "% given back (at most half of the part), the vehicle is " + (d.extra - give).ToString("F0") + " kg heavier" : " — the vehicle's mass and centre of mass are the real ones"));
            }
        }

        /// <summary>0.31.8 (FlightTrace): a part's REAL mass and centre of mass (what the mod changed undone) and the ones the physics sees now, in
        /// the world. False for a static/kinematic part.</summary>
        internal static unsafe bool RealOf(EntityManager em, Entity part, out float mReal, out float3 cReal, out float mPhys, out float3 cPhys)
        {
            mReal = mPhys = 0f; cReal = cPhys = default;
            if (!Ecs.IsPart(em, part) || !Ecs.HasC(em, part, Ecs.tiMass) || !Ecs.HasC(em, part, Ecs.tiLT)) return false;
            var pm = (PhysicsMass*)em.GetComponentDataRawRO(part, Ecs.tiMass); if (pm == null) return false;
            var lt = Ecs.LT(em, part); float s = lt.Scale == 0f ? 1f : lt.Scale;
            float invP = pm->InverseMass, invR = invP; float3 lp = pm->Transform.pos, lr = lp;
            if (masses.TryGetValue(Ecs.K(part), out var r) && r.wasMoved) { invR = r.invM; if (r.donor) lr = r.com; }
            if (invP <= 0f || invR <= 0f) return false;
            mPhys = 1f / invP; mReal = 1f / invR;
            cPhys = lt.Position + math.rotate(lt.Rotation, lp * s); cReal = lt.Position + math.rotate(lt.Rotation, lr * s);
            return true;
        }

        static unsafe PhysicsMass* PM(EntityManager em, Entity part) =>
            Ecs.IsPart(em, part) && Ecs.HasC(em, part, Ecs.tiMass) ? (PhysicsMass*)em.GetComponentDataRawRW(part, Ecs.tiMass) : null;

        static bool Eq(float3 a, float3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        static float Inv(float x) => x > 0f ? 1f / x : float.PositiveInfinity;
        static float MaxI(float3 invI) { float a = Inv(invI.x), b = Inv(invI.y), c = Inv(invI.z); float m = 0f; foreach (var v in new[] { a, b, c }) if (!float.IsInfinity(v) && v > m) m = v; return m; }

        unsafe static void Register(EntityManager em, Entity p, bool massOnly, HashSet<long> onlyM)
        {
            long k = Ecs.K(p);
            var pm = PM(em, p); if (pm == null) return;
            // new, or the game rewrote the mass (part rebuilt/recalculated): what is there is the original
            if (!masses.TryGetValue(k, out var r) || (r.wasMoved && pm->InverseMass != r.writtenM) || (!r.wasMoved && pm->InverseMass != r.invM))
            {
                // 0.30.3: a hull the game re-massed keeps the centre of mass it really has, not the one the mod moved (if the game left that alone)
                var com = r != null && r.donor && Eq(pm->Transform.pos, r.writtenCom) ? r.com : pm->Transform.pos;
                masses[k] = r = new PartMass { part = p, invM = pm->InverseMass, invI = pm->InverseInertia, com = com, writtenCom = com, writtenM = pm->InverseMass, writtenI = pm->InverseInertia };
            }
            if (massOnly) onlyM.Add(k);
        }

        /// <summary>Every frame, after the joints: recalculates how much each light part held by a piston weld (or by the turret's pivot)
        /// is worth for physics.</summary>
        static unsafe void BalanceMasses(EntityManager em)
        {
            var edges = new List<(long a, long b, bool spin)>(); var onlyM = new HashSet<long>(); var turretPieces = new HashSet<long>();
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
                            { Register(em, t.partBase, true, onlyM); Register(em, m.part, true, onlyM); edges.Add((Ecs.K(t.partBase), Ecs.K(m.part), false)); turretPieces.Add(Ecs.K(m.part)); }
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
            // 0.31.0: who may be raised at all, and how far
            var nbrs = new Dictionary<long, HashSet<long>>();
            foreach (var (a, b, _) in edges) { if (!nbrs.TryGetValue(a, out var na)) nbrs[a] = na = new HashSet<long>(); na.Add(b); if (!nbrs.TryGetValue(b, out var nb)) nbrs[b] = nb = new HashSet<long>(); nb.Add(a); }
            float Real(long k) => masses.TryGetValue(k, out var rr) ? Inv(rr.invM) : float.PositiveInfinity;
            var skipped = new HashSet<long>();
            foreach (var kv in nbrs)
            {
                if (kv.Value.Count != 1 || turretPieces.Contains(kv.Key)) continue;
                long other = 0; foreach (var o in kv.Value) other = o;
                float me = Real(kv.Key), them = Real(other);
                if (!float.IsInfinity(me) && !float.IsInfinity(them) && me * LeafRatio < them) skipped.Add(kv.Key);
            }
            // 0.31.2 (log 31/10 13:41: the two turret pieces, 666 and 1241 kg, counted as 10.3 t each and the turret tore itself apart at the
            // limit with the ship turning at 290 m/s): the turret pieces have a cap of their own
            float Cap(long k, float wanted)
            {
                if (skipped.Contains(k)) return Real(k);
                float f = turretPieces.Contains(k) ? TurretMassFactor : MassFactorMax; if (f <= 0f) return wanted;
                float m0 = Real(k); return float.IsInfinity(m0) ? wanted : System.Math.Min(wanted, m0 * f);
            }
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
                    float na = Cap(a, System.Math.Max(ma, mb / MassRatio)), nb = Cap(b, System.Math.Max(mb, ma / MassRatio));
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
                    if (skipped.Contains(a) || skipped.Contains(b)) continue;   // 0.31.0: a light leaf keeps its own inertia (and doesn't pass it on)
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
                if (skipped.Contains(a) || skipped.Contains(b)) continue;
                floor[a] = System.Math.Max(floor.TryGetValue(a, out var pa) ? pa : 0f, efI[b] / MassRatio);
                floor[b] = System.Math.Max(floor.TryGetValue(b, out var pb) ? pb : 0f, efI[a] / MassRatio);
            }
            // apply / give back
            var exit = new List<long>();
            var donors = Donors(em, edges, used, ef); donorKeys.Clear(); foreach (var d in donors) donorKeys.Add(d.key);
            foreach (var kv in masses)
            {
                var r = kv.Value; var pm = PM(em, r.part);
                if (pm == null) { exit.Add(kv.Key); continue; }
                if (donorKeys.Contains(kv.Key)) continue;   // the hull that gives the kilograms back: ApplyDonor
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
                    float Axis(float inv) { if (inv <= 0f) return 0f; float I = (1f / inv) / fM; if (targetI > I) I = targetI; if (MassFactorMax > 0f) I = System.Math.Min(I, (1f / inv) * MassFactorMax * MassFactorMax); return 1f / I; }
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
                        if (r.donor) { pm->Transform.pos = r.com; r.writtenCom = r.com; r.donor = false; donorLog.Remove(kv.Key); }
                        r.wasMoved = false; r.logged = 0f;
                        ModLog.Info("joints: part " + r.part.Index + " back to its real " + m0.ToString("F0") + " kg");
                    }
                    if (!used.Contains(kv.Key)) exit.Add(kv.Key);
                }
            }
            foreach (var k in exit) masses.Remove(k);
            foreach (var d in donors) ApplyDonor(em, d);
            {
                float extraAll = 0f; int pieces = 0;
                foreach (var kv in masses) { var r = kv.Value; if (!r.wasMoved || r.donor) continue; float m0 = Inv(r.invM); if (float.IsInfinity(m0) || r.writtenM <= 0f) continue; float now = 1f / r.writtenM; if (now > m0) { extraAll += now - m0; pieces++; } }
                float given = 0f; foreach (var d in donors) if (masses.TryGetValue(d.key, out var dr)) given += System.Math.Min(d.extra, 0.5f * Inv(dr.invM));
                lastSummary = pieces == 0 ? "no physics-only mass" : "+" + extraAll.ToString("F0") + " kg on " + pieces + " piece(s), " + given.ToString("F0") + " kg given back by the hull";
            }
        }
    }
}
