using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

namespace CrapMechanics
{
    // 0.31.23 (Andre, 14/11: "a parte de agir no inicio do solver era legal"; and before that: with the gate on pistons and bearings the ship
    // leans, with the same wall welded it does not — "é o mod").
    //
    // What the logs of 14/11 add up to. The six gate pieces weigh 385 kg and the mod counts them as 5230 kg so the weld holds (PartMass.cs);
    // to keep the SHIP's weight and centre of mass right, the hull gives those kilograms back and its own centre of mass is moved 0.26 m.
    // That is right for the solver and wrong for everything the GAME does to each part before the solver:
    //   - the game pushes each part through that part's centre of mass. The hull's was moved 0.26 m by the mod, so the hull's own thrust
    //     passes 0.26 m off the ship's centre: at 3.7 MN that is close to 1000 kN·m of turning force the welded ship does not have;
    //   - whatever the game does to a part in proportion to its mass (the drag slows each part down) lands on the gate pieces as if they
    //     weighed 25 times what they weigh, 7 m away from the centre.
    // So the made-up masses now exist ONLY while the solver runs:
    //   1. right before the game's forces (start of PhysicsPreGroup) every part gets its real mass and real centre of mass back;
    //   2. right before the solver (end of PhysicsPreGroup) the parts that are welded and still — hull and locked pieces — are put on the one
    //      rigid motion that carries the same push and the same spin they have, counted with the REAL masses. Nothing is added or taken
    //      out: it is what a perfect weld would do. The solver then finds those welds with nothing to correct;
    //   3. the solver's masses go back in, and the solver runs as before.
    // Only locked joints count (a piston that is not moving, a servo bearing that is not turning). A piece on a moving piston, a turning
    // servo, a free bearing or the turret keeps going through the solver exactly as it did; if it carries a made-up mass, the push the game
    // gave it is carried over unchanged. The gate, the door, the servo and the mass balance themselves are not touched.
    // [Physics] weld_before_solver = false turns all of it off (the solver's masses stay in all the time, as up to 0.31.22).
    // 0.31.26: step 1 now happens right AFTER the solver (MassesRealAfterSolver, item 12), so the game's systems between the solver and the
    // next tick see the real ship too; every move of a hull's centre carries its speed (SetCom, S-N2); in vacuum a fully locked vehicle keeps
    // the real masses through the solver (U2); and a locked group with stabilising thrusters gets the game's thrust rule as one part (ThrustAsOne, item 3).
    internal static partial class JointSystem
    {
        public static bool WeldFirst = true;   // [Physics] weld_before_solver
        // 0.31.24, from the game's own code (AerodynamicsSystem.DynamicObjectsJob, read from the Burst listing on 18/11): the game's drag
        // never turns a part. Each part gets one number, _drag01, and both its speed and its spin are multiplied by (1 - _drag01).
        // The number is worked out per part, from rays cast against that part's OWN collider only. A ship in one piece therefore just
        // slows down. The same ship with a gate on pistons has every gate piece slowed by its own number, far from the centre of the
        // ship, and that difference turns the ship — it is what Andre found on 14/11 ("o problema é sim o arrasto").
        // With this on, a welded group gets the game's rule as one part: the drag each of its parts received is undone, and the group
        // is slowed by the single number a one-piece ship of the same parts would have had (the mass-weighted mean of theirs), speed
        // and spin alike. The total braking the game applied is the same; only the turning it caused is gone.
        public static bool DragAsOne = true;   // [Physics] welded_drag_as_one
        static TypeIndex tiAeroW; static bool aeroTried, aeroOkW;
        static double wDrag; static int wDragTicks;
        static int lockFrame; static float lockNow;
        const float LockSeconds = 0.15f; const int LockFrames = 3;

        struct D3
        {
            public double x, y, z;
            public D3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
            public static D3 operator +(D3 a, D3 b) => new D3(a.x + b.x, a.y + b.y, a.z + b.z);
            public static D3 operator -(D3 a, D3 b) => new D3(a.x - b.x, a.y - b.y, a.z - b.z);
            public static D3 operator *(D3 a, double s) => new D3(a.x * s, a.y * s, a.z * s);
            public static D3 Cross(D3 a, D3 b) => new D3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
            public double Len => System.Math.Sqrt(x * x + y * y + z * z);
            public bool Bad => double.IsNaN(x + y + z) || double.IsInfinity(x + y + z);
            public static D3 Of(float3 v) => new D3(v.x, v.y, v.z);
            public float3 F => new float3 { x = (float)x, y = (float)y, z = (float)z };
        }
        struct M3   // row by row
        {
            public double a, b, c, d, e, f, g, h, i;
            public static M3 OfQ(quaternion q)
            {
                double x = q.value.x, y = q.value.y, z = q.value.z, w = q.value.w;
                double n = x * x + y * y + z * z + w * w; double s = n > 0 ? 2.0 / n : 0;
                return new M3
                {
                    a = 1 - s * (y * y + z * z), b = s * (x * y - z * w), c = s * (x * z + y * w),
                    d = s * (x * y + z * w), e = 1 - s * (x * x + z * z), f = s * (y * z - x * w),
                    g = s * (x * z - y * w), h = s * (y * z + x * w), i = 1 - s * (x * x + y * y)
                };
            }
            public D3 Mul(D3 v) => new D3(a * v.x + b * v.y + c * v.z, d * v.x + e * v.y + f * v.z, g * v.x + h * v.y + i * v.z);
            public D3 TMul(D3 v) => new D3(a * v.x + d * v.y + g * v.z, b * v.x + e * v.y + h * v.z, c * v.x + f * v.y + i * v.z);
            /// <summary>this (rotation) x diag(I) x this transposed, added to m.</summary>
            public void AddInertia(D3 I, ref M3 m)
            {
                m.a += a * a * I.x + b * b * I.y + c * c * I.z; m.b += a * d * I.x + b * e * I.y + c * f * I.z; m.c += a * g * I.x + b * h * I.y + c * i * I.z;
                m.d += d * a * I.x + e * b * I.y + f * c * I.z; m.e += d * d * I.x + e * e * I.y + f * f * I.z; m.f += d * g * I.x + e * h * I.y + f * i * I.z;
                m.g += g * a * I.x + h * b * I.y + i * c * I.z; m.h += g * d * I.x + h * e * I.y + i * f * I.z; m.i += g * g * I.x + h * h * I.y + i * i * I.z;
            }
            /// <summary>m (|r|² - r rT), added to this.</summary>
            public void AddPoint(double m, D3 r)
            {
                double rr = r.x * r.x + r.y * r.y + r.z * r.z;
                a += m * (rr - r.x * r.x); b -= m * r.x * r.y; c -= m * r.x * r.z;
                d -= m * r.y * r.x; e += m * (rr - r.y * r.y); f -= m * r.y * r.z;
                g -= m * r.z * r.x; h -= m * r.z * r.y; i += m * (rr - r.z * r.z);
            }
            public bool Solve(D3 r, out D3 x)
            {
                double c00 = e * i - f * h, c01 = f * g - d * i, c02 = d * h - e * g;
                double det = a * c00 + b * c01 + c * c02;
                double scale = System.Math.Abs(a * e * i);
                if (!(System.Math.Abs(det) > 1e-9 * (scale > 1e-12 ? scale : 1e-12))) { x = default; return false; }
                double inv = 1.0 / det;
                x = new D3(
                    (c00 * r.x + (c * h - b * i) * r.y + (b * f - c * e) * r.z) * inv,
                    (c01 * r.x + (a * i - c * g) * r.y + (c * d - a * f) * r.z) * inv,
                    (c02 * r.x + (b * g - a * h) * r.y + (a * e - b * d) * r.z) * inv);
                return !x.Bad;
            }
        }
        struct WBody { public Entity p; public long key; public int root; public double m, drag; public D3 com, v, wl, I; public M3 R; public bool skip; }
        struct WFit { public bool ok; public D3 V, W, C; public int n; }

        class LockState { public float cur, ang; public int joint, a, b; public int frame; public float time; }
        static readonly Dictionary<long, LockState> lockStates = new Dictionary<long, LockState>();
        static readonly List<WBody> wb = new List<WBody>();
        static readonly Dictionary<long, int> wbIndex = new Dictionary<long, int>();
        static readonly Dictionary<int, WFit> fits = new Dictionary<int, WFit>();
        static readonly HashSet<long> projected = new HashSet<long>();
        static readonly List<PartMass> swappedList = new List<PartMass>();
        static int weldWarnings;
        // for the log
        static int wTicks, wParts, wJoints, wSwapped, wCarried, wKeptReal, wThrustTicks, wThrustGroups; static double wRel, wRelPeak; static long wRelPart;
        static readonly HashSet<long> lockedNow = new HashSet<long>();   // 0.31.26: joints (block keys) found Locked in this tick
        static readonly HashSet<long> keepReal = new HashSet<long>();    // 0.31.26 (U2): parts that keep the real book through this solver
        // 0.31.35 (performance): kept between ticks instead of made new on every tick (only Clear + Add: the order they are walked in is
        // the order of a new one, so every sum runs in the same order as before)
        static readonly List<int> rootsTmp = new List<int>();
        static readonly Dictionary<long, long> krParent = new Dictionary<long, long>();
        static readonly List<(long a, long b, bool ok)> krEdges = new List<(long, long, bool)>();
        static readonly HashSet<long> krBad = new HashSet<long>();
        static readonly Dictionary<int, List<int>> members = new Dictionary<int, List<int>>();
        static readonly List<List<int>> memberPool = new List<List<int>>(); static int memberUsed;
        static IntPtr[] forceHdr = new IntPtr[64];

        internal static void ForgetWeldFirst() { lockStates.Clear(); swappedList.Clear(); thrustGrouped.Clear(); keepReal.Clear(); wTicks = 0; wThrustTicks = 0; wThrustGroups = 0; wDrag = 0; wDragTicks = 0; wRel = 0; wRelPeak = 0; }

        /// <summary>Is this joint a weld that is standing still (piston not moving, servo not turning)?</summary>
        static bool Locked(Joint j)
        {
            long k = Ecs.K(j.block);
            bool cand = j.state == 2 && !Ecs.IsNull(j.partA) && !Ecs.IsNull(j.partB) && j.partA.Index != j.partB.Index && !Ecs.IsNull(j.joint)
                     && j.tStart <= 0f && j.tPullT < 0f && !j.tilted && !j.moving
                     && (j.kind == Kind.Piston || (j.kind == Kind.Bearing && j.mode == 2 && j.angCmdOk && j.angVelCmd == 0f));
            if (!cand) { lockStates.Remove(k); return false; }
            int frame = lockFrame; float now = lockNow;
            if (!lockStates.TryGetValue(k, out var st)) { lockStates[k] = new LockState { cur = j.currentTarget, ang = j.angCmd, joint = j.joint.Index, a = j.partA.Index, b = j.partB.Index, frame = frame, time = now }; return false; }
            if (st.cur != j.currentTarget || st.ang != j.angCmd || st.joint != j.joint.Index || st.a != j.partA.Index || st.b != j.partB.Index)
            { st.cur = j.currentTarget; st.ang = j.angCmd; st.joint = j.joint.Index; st.a = j.partA.Index; st.b = j.partB.Index; st.frame = frame; st.time = now; return false; }
            return frame - st.frame >= LockFrames && now - st.time >= LockSeconds;
        }

        /// <summary>Start of PhysicsPreGroup, before the game applies thrust and drag: every part the mod re-weighed gets its real mass and
        /// its real centre of mass back for the game's forces.</summary>
        // 0.31.26 (item 12): normally they are back already — MassesRealAfterSolver put them back right after the last solver — and this only
        // records the speeds the game's forces start from. A part still on the solver's book (the frame's mass balance ran since) is swapped
        // here as before. A list left over from a tick whose end never came is simply recorded again: the flag says which book is in.
        internal static unsafe void WeldBeforeForces(EntityManager em)
        {
            swappedList.Clear();
            if (!WeldFirst || !Rigid || !Multi.Authoritative(em)) return;
            foreach (var r in masses.Values)
            {
                if (!r.wasMoved) continue;
                var pm = PM(em, r.part); if (pm == null) continue;
                if (!Ecs.HasC(em, r.part, Ecs.tiVel) || !Ecs.HasC(em, r.part, Ecs.tiLT)) continue;
                if (r.realIn) { if (pm->InverseMass != r.invM) { r.realIn = false; continue; } }   // the game rewrote it: the balance sorts that out
                else
                {
                    if (pm->InverseMass != r.writtenM) continue;   // the game rewrote it: the balance sorts that out
                    ToReal(em, r, pm);
                }
                var pv = (PhysicsVelocity*)Ecs.RO(em, r.part, Ecs.tiVel);
                r.linBefore = pv->Linear; r.angBefore = pv->Angular;
                swappedList.Add(r);
            }
        }

        /// <summary>0.31.26 (S-N2): moves a part's centre of mass to newLocal (PhysicsMass.Transform.pos), taking the speed along. The physics
        /// keeps Linear as the speed OF the centre of mass [recon/SpaceshipInertiaToShockwaveSystem.cs:48-49; recon/SpaceshipPartsApplyForces.cs:
        /// 46-48; UP ECS/Base/Components/PhysicsComponents.cs:197-205]: moving the centre without Linear += ω × Δ changes how the part really
        /// moves by ω×Δ — a push from nowhere. Same sum as the one WeldBeforeForces always did (0.31.23).</summary>
        static unsafe void SetCom(EntityManager em, Entity part, PhysicsMass* pm, float3 newLocal)
        {
            if (Eq(pm->Transform.pos, newLocal)) return;
            if (Ecs.HasC(em, part, Ecs.tiVel) && Ecs.HasC(em, part, Ecs.tiLT))
            {
                var lt = Ecs.LT(em, part); float s = lt.Scale == 0f ? 1f : lt.Scale;
                var pv = (PhysicsVelocity*)em.GetComponentDataRawRW(part, Ecs.tiVel);
                var wWorld = Ecs.Rot(Ecs.QMul(lt.Rotation, pm->InertiaOrientation), pv->Angular);
                var shift = Ecs.Cross(wWorld, Ecs.Rot(lt.Rotation, Ecs.Mul(Ecs.Sub(newLocal, pm->Transform.pos), s)));
                if (!float.IsNaN(shift.x + shift.y + shift.z) && !float.IsInfinity(shift.x + shift.y + shift.z)) pv->Linear = Ecs.Add(pv->Linear, shift);
            }
            pm->Transform.pos = newLocal;
        }

        /// <summary>0.31.26: one part onto its REAL book (mass, inertia, centre with the speed carried). Caller checked it holds the solver's.</summary>
        static unsafe void ToReal(EntityManager em, PartMass r, PhysicsMass* pm)
        {
            if (r.donor) SetCom(em, r.part, pm, r.com);
            pm->InverseMass = r.invM; if (!r.massOnly) pm->InverseInertia = r.invI;
            r.realIn = true;
        }

        /// <summary>0.31.26 (item 12): the one function that puts the solver's book back. carry = take the hull's speed to the solver's centre
        /// (SetCom); false only inside WeldBeforeSolver, where every such part is then either put on its group's rigid motion measured at the
        /// solver's centre, or gets the shift with its final spin in the "carried" loop. keep = parts that stay on the real book (U2).
        /// backOk tells, per part, whether it was done (false: not on the real book, kept, or the game rewrote it meanwhile).</summary>
        static unsafe void ToSolver(EntityManager em, bool carry, HashSet<long> keep)
        {
            foreach (var r in masses.Values)
            {
                r.backOk = false;
                if (!r.realIn) continue;
                try
                {
                    var pm = PM(em, r.part); if (pm == null || pm->InverseMass != r.invM) { r.realIn = false; continue; }   // gone, or the game rewrote it meanwhile
                    if (keep != null && keep.Contains(Ecs.K(r.part))) continue;
                    pm->InverseMass = r.writtenM; if (!r.massOnly) pm->InverseInertia = r.writtenI;
                    if (r.donor) { if (carry) SetCom(em, r.part, pm, r.writtenCom); else pm->Transform.pos = r.writtenCom; }
                    r.realIn = false; r.backOk = true;
                }
                catch (Exception) { }
            }
        }

        /// <summary>0.31.26 (item 12): prefix of SpaceshipComponentsTickPostGroup — the solver has just run. Every re-weighed part gets its real
        /// mass, inertia and centre back now, instead of at the start of the next PhysicsPreGroup. Between the solver and the next tick the game
        /// reads PhysicsMass in SCTick_Massmeter (shows 1/InverseMass of the part) [recon/EPC_SCMassmeter.cs:9,39,51-52], SCTick_Thruster_Blow
        /// (InverseMass, InverseInertia and centre of the body it blows on) [recon/EPC_ThrusterBlow.cs:12,41,145-152], ExplosionsSystem (impulse
        /// with InverseMass/InverseInertia) [recon/ExplosionsSystem.cs:8,220-227], SpaceshipInertiaToShockwaveSystem (Transform.pos)
        /// [recon/SpaceshipInertiaToShockwaveSystem.cs:5,33] and the server's NetcoreSyncSpaceshipPartsTransform (sends Linear of the part)
        /// [recon/NetcoreSyncSpaceshipPartsTransform.cs:6,24,81-83]: all of them now see the real ship. Unity Physics never writes PhysicsMass
        /// (the export writes only LocalTransform and PhysicsVelocity [UP PhysicsWorldExporter.cs:137-168]) and the next Build reads it in the
        /// next tick, so the solver of this tick is not affected. The 0.31.25 patch of SPData._frameInitVelocity is gone with this: the shockwave
        /// now compares speeds at the real centre, where _frameInitVelocity was recorded — keeping the patch would shift it twice.</summary>
        internal static unsafe void MassesRealAfterSolver(EntityManager em)
        {
            if (!WeldFirst || !Rigid || !Multi.Authoritative(em)) return;
            try
            {
                foreach (var r in masses.Values)
                {
                    if (!r.wasMoved || r.realIn) continue;
                    var pm = PM(em, r.part); if (pm == null || pm->InverseMass != r.writtenM) continue;   // gone, or the game rewrote it: the balance sorts that out
                    ToReal(em, r, pm);
                }
            }
            catch (Exception ex) { if (weldWarnings++ < 3) ModLog.Warn("real masses after the solver: " + ex.Message); }
        }

        /// <summary>0.31.26 (U2, for the momentum watch): the part's REAL book while the solver's is in PhysicsMass (otherwise false and the
        /// current values): inverse mass, inverse inertia and local centre.</summary>
        internal static unsafe bool RealBook(Entity part, PhysicsMass* pm, out float invM, out float3 invI, out float3 com)
        {
            invM = pm->InverseMass; invI = pm->InverseInertia; com = pm->Transform.pos;
            if (!masses.TryGetValue(Ecs.K(part), out var r) || !r.wasMoved || r.realIn || pm->InverseMass != r.writtenM) return false;
            invM = r.invM; if (!r.massOnly) invI = r.invI; if (r.donor) com = r.com;
            return true;
        }

        static unsafe bool ReadBody(EntityManager em, Entity p, D3 origin, bool hasOrigin, out WBody b, out D3 pos)
        {
            b = default; pos = default;
            // 0.31.27 (performance, runs twice per tick per welded part): one existence test, then HasComponent alone (HasC and LT tested the
            // existence again on every call — 10 calls into the game before the first read, now 4)
            if (!Ecs.Alive(em, p) || !Ecs.Has(em, p, Ecs.tiMass) || !Ecs.Has(em, p, Ecs.tiVel) || !Ecs.Has(em, p, Ecs.tiLT)) return false;
            var pm = (PhysicsMass*)Ecs.RO(em, p, Ecs.tiMass);
            var pv = (PhysicsVelocity*)Ecs.RO(em, p, Ecs.tiVel);
            var lt = *(Unity.Transforms.LocalTransform*)Ecs.RO(em, p, Ecs.tiLT); float s = lt.Scale == 0f ? 1f : lt.Scale;
            pos = D3.Of(lt.Position);
            b.p = p; b.key = Ecs.K(p);
            // a part the physics does not move (no mass), or one that cannot turn on some axis: its whole group is left to the solver
            if (!(pm->InverseMass > 0f) || !(pm->InverseInertia.x > 0f) || !(pm->InverseInertia.y > 0f) || !(pm->InverseInertia.z > 0f)) { b.skip = true; return true; }
            b.m = 1.0 / pm->InverseMass;
            b.I = new D3(1.0 / pm->InverseInertia.x, 1.0 / pm->InverseInertia.y, 1.0 / pm->InverseInertia.z);
            var o = hasOrigin ? origin : pos;
            b.com = (pos - o) + D3.Of(Ecs.Rot(lt.Rotation, Ecs.Mul(pm->CenterOfMass, s)));
            b.v = D3.Of(pv->Linear); b.wl = D3.Of(pv->Angular);
            if (aeroOkW && Ecs.Has(em, p, tiAeroW)) { float d = *(float*)Ecs.RO(em, p, tiAeroW); if (d > 0f) b.drag = d; }   // DynamicObjectAerodynamics._drag01 (+0)
            b.R = M3.OfQ(Ecs.QMul(lt.Rotation, pm->InertiaOrientation));
            return true;
        }

        /// <summary>End of PhysicsPreGroup: the game's thrust and drag are in, the solver has not run.</summary>
        internal static unsafe void WeldBeforeSolver(EntityManager em)
        {
            bool massesBack = false;
            try
            {
                projected.Clear(); fits.Clear(); wb.Clear(); wbIndex.Clear();
                if (!WeldFirst || !Rigid || !Multi.Authoritative(em)) return;
                lockFrame = UnityEngine.Time.frameCount; lockNow = UnityEngine.Time.time;
                if (!aeroTried) { aeroTried = true; try { tiAeroW = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<DynamicObjectAerodynamics>()); aeroOkW = true; } catch (Exception) { aeroOkW = false; } }
                // ---- who is welded and still
                int locked = 0; D3 origin = default; bool hasOrigin = false; lockedNow.Clear();
                foreach (var j in allJoints.Values)
                {
                    if (!Locked(j)) continue;
                    lockedNow.Add(Ecs.K(j.block));
                    int ia = Index(em, j.partA, ref origin, ref hasOrigin), ib = Index(em, j.partB, ref origin, ref hasOrigin);
                    if (ia < 0 || ib < 0) continue;
                    int ra = Root(ia), rb = Root(ib); if (ra != rb) { var x = wb[ra]; x.root = rb; wb[ra] = x; }
                    locked++;
                }
                // ---- each welded group: the one rigid motion with the same push and the same spin (real masses: they are in right now)
                if (locked > 0)
                {
                    for (int i = 0; i < wb.Count; i++)
                    {
                        int root = Root(i); fits.TryGetValue(root, out var f); f.n++;
                        if (wb[i].skip) f.n = -100000;   // a part that cannot be moved: leave the whole group alone
                        fits[root] = f;
                    }
                    rootsTmp.Clear(); foreach (int rk in fits.Keys) rootsTmp.Add(rk);   // 0.31.35: a kept list (it was a new one every tick); same order
                    foreach (int root in rootsTmp)
                    {
                        var f = fits[root]; if (f.n < 2) { f.ok = false; fits[root] = f; continue; }
                        // the game's drag as ONE part: undo what each part got, slow the whole group by the mass-weighted mean
                        double M = 0, dSum = 0, dMax = 0; D3 C = default;
                        for (int i = 0; i < wb.Count; i++) { if (Root(i) != root) continue; var b = wb[i]; M += b.m; C += b.com * b.m; dSum += b.drag * b.m; if (b.drag > dMax) dMax = b.drag; }
                        if (!(M > 0)) { f.ok = false; fits[root] = f; continue; }
                        C = C * (1.0 / M);
                        bool one = DragAsOne && dMax > 0 && dMax < 0.9; double dGroup = one ? dSum / M : 0;
                        D3 P = default, L = default; M3 J = default;
                        for (int i = 0; i < wb.Count; i++) { if (Root(i) != root) continue; var b = wb[i]; P += b.v * (b.m * (one ? 1.0 / (1.0 - b.drag) : 1.0)); }
                        var V = P * (1.0 / M);
                        for (int i = 0; i < wb.Count; i++)
                        {
                            if (Root(i) != root) continue; var b = wb[i]; var r = b.com - C; double k = one ? 1.0 / (1.0 - b.drag) : 1.0;
                            L += b.R.Mul(new D3(b.I.x * b.wl.x, b.I.y * b.wl.y, b.I.z * b.wl.z)) * k + D3.Cross(r, b.v * k - V) * b.m;
                            b.R.AddInertia(b.I, ref J); J.AddPoint(b.m, r);
                        }
                        if (one) { V = V * (1.0 - dGroup); L = L * (1.0 - dGroup); wDrag += dGroup; wDragTicks++; }
                        f.ok = J.Solve(L, out var W) && !V.Bad && !C.Bad; f.V = V; f.W = W; f.C = C; fits[root] = f;
                    }
                }
                // ---- 0.31.26 (U2): in vacuum, a vehicle whose joints are ALL locked and whose parts are ALL put on their rigid motion right
                // below keeps the REAL masses through the solver. Every solver impulse is an equal and opposite pair computed with the masses it is
                // given (Δv_A = J/m_sA, Δv_B = −J/m_sB) [UP Dynamics/Motion/Motion.cs:194-206]: with a piece counted 25× (inertia 625×) and a hull
                // that gave kilograms away, each position correction of a weld becomes real momentum J·(m_rA/m_sA − m_rB/m_sB), and the
                // projection of the next tick makes it the whole group's motion. With the projection having taken the relative motion out, the
                // solver only corrects positions there, and with real masses that conserves the real momentum. Joints that move, the turret, or
                // gravity (a foot resting on the ground) keep the solver's masses — that is what the balance is for.
                keepReal.Clear();
                if (locked > 0 && Vacuum(em)) KeepRealGroups(em);
                // ---- the solver's masses go back in (the speeds are taken care of below: rigid motion, or "carried")
                ToSolver(em, false, keepReal.Count > 0 ? keepReal : null); massesBack = true;
                // (0.31.25 moved SPData._frameInitVelocity to the solver's centre here; 0.31.26 puts the real masses back right after the
                // solver instead, so the game compares speeds at the real centre, where it recorded them — see MassesRealAfterSolver)
                // ---- the welded groups, on their rigid motion (measured at each part's centre of mass as the solver has it)
                int parts = 0; double relMax = 0; long relPart = 0;
                for (int i = 0; i < wb.Count; i++)
                {
                    var b = wb[i]; if (!fits.TryGetValue(Root(i), out var f) || !f.ok) continue;
                    if (!Ecs.Alive(em, b.p) || !Ecs.Has(em, b.p, Ecs.tiVel) || !Ecs.Has(em, b.p, Ecs.tiMass)) continue;
                    var pm = (PhysicsMass*)Ecs.RO(em, b.p, Ecs.tiMass);
                    if (!Ecs.Has(em, b.p, Ecs.tiLT)) throw new InvalidOperationException("entity " + b.p.Index + " is gone or lost the component");   // what Ecs.LT did
                    var lt = *(Unity.Transforms.LocalTransform*)Ecs.RO(em, b.p, Ecs.tiLT); float s = lt.Scale == 0f ? 1f : lt.Scale;
                    var com = (D3.Of(lt.Position) - origin) + D3.Of(Ecs.Rot(lt.Rotation, Ecs.Mul(pm->CenterOfMass, s)));
                    var v = f.V + D3.Cross(f.W, com - f.C); var wl = b.R.TMul(f.W);
                    if (v.Bad || wl.Bad) continue;
                    var pv = (PhysicsVelocity*)em.GetComponentDataRawRW(b.p, Ecs.tiVel);
                    double rel = (v - (b.v + D3.Cross(b.R.Mul(b.wl), com - b.com))).Len; if (rel > relMax) { relMax = rel; relPart = b.key; }
                    pv->Linear = v.F; pv->Angular = wl.F;
                    projected.Add(b.key); parts++;
                }
                // ---- a re-weighed part that is NOT in a welded group (moving piston, turning servo, turret): the push the game just gave it
                // is carried over to the solver's mass unchanged (push = mass x change of speed)
                int carried = 0;
                foreach (var r in swappedList)
                {
                    if (!r.backOk || projected.Contains(Ecs.K(r.part))) continue;
                    if (!Ecs.HasC(em, r.part, Ecs.tiVel) || !Ecs.HasC(em, r.part, Ecs.tiLT)) continue;
                    var pv = (PhysicsVelocity*)em.GetComponentDataRawRW(r.part, Ecs.tiVel);
                    // 0.31.26 (item 3): a part whose thrust ThrustAsOne already applied as a rigid change of its group was not pushed as a lone
                    // part: rescaling that as an impulse would be wrong. It only gets the centre shift below.
                    bool scale = !thrustGrouped.Contains(Ecs.K(r.part));
                    float km = scale && r.invM > 0f ? r.writtenM / r.invM : 1f;
                    var lin = Ecs.Add(r.linBefore, Ecs.Mul(Ecs.Sub(pv->Linear, r.linBefore), km));
                    var ang = pv->Angular;
                    if (!r.massOnly && scale)
                    {
                        ang.x = r.angBefore.x + (ang.x - r.angBefore.x) * (r.invI.x > 0f ? r.writtenI.x / r.invI.x : 1f);
                        ang.y = r.angBefore.y + (ang.y - r.angBefore.y) * (r.invI.y > 0f ? r.writtenI.y / r.invI.y : 1f);
                        ang.z = r.angBefore.z + (ang.z - r.angBefore.z) * (r.invI.z > 0f ? r.writtenI.z / r.invI.z : 1f);
                    }
                    if (r.donor && !Eq(r.com, r.writtenCom))
                    {
                        var pm = PM(em, r.part); var lt = Ecs.LT(em, r.part); float s = lt.Scale == 0f ? 1f : lt.Scale;
                        if (pm != null) { var wWorld = Ecs.Rot(Ecs.QMul(lt.Rotation, pm->InertiaOrientation), ang); lin = Ecs.Add(lin, Ecs.Cross(wWorld, Ecs.Rot(lt.Rotation, Ecs.Mul(Ecs.Sub(r.writtenCom, r.com), s)))); }
                    }
                    pv->Linear = lin; pv->Angular = ang; carried++;
                }
                // ---- for the log
                wTicks++; wParts = parts; wJoints = locked; wSwapped = swappedList.Count; wCarried = carried; wRel += relMax; wKeptReal = keepReal.Count;
                if (relMax > wRelPeak) { wRelPeak = relMax; wRelPart = relPart; }
            }
            catch (Exception ex) { if (weldWarnings++ < 3) ModLog.Warn("weld before the solver: " + ex.Message); }
            finally
            {
                if (!massesBack) { try { ToSolver(em, true, null); } catch (Exception) { } }   // nothing was projected yet: carry the hull's speed
                swappedList.Clear(); thrustGrouped.Clear();
            }
        }

        /// <summary>0.31.26 (U2): fills keepReal with the parts of every vehicle (parts linked by any of the mod's joints or a turret pivot) whose
        /// joints are all locked this tick and whose parts all have a good rigid fit (so they are projected right after). Called after the fits.</summary>
        static void KeepRealGroups(EntityManager em)
        {
            krParent.Clear(); krEdges.Clear(); krBad.Clear();
            foreach (var j in allJoints.Values) if (j.state == 1 || j.state == 2) KrEdge(j.partA, j.partB, j.state == 2 && lockedNow.Contains(Ecs.K(j.block)));
            foreach (var t in turrets.Values)   // the turret's pivot is a ball-and-socket that turns: never a weld
                if (!Ecs.IsNull(t.partBase)) foreach (var m in t.movs) if (Ecs.Alive(em, m.part)) KrEdge(t.partBase, m.part, false);
            foreach (var (a, _, ok) in krEdges) if (!ok) krBad.Add(KrFind(a));
            foreach (var k in krParent.Keys)
            {
                long root = KrFind(k); if (krBad.Contains(root)) continue;
                if (!wbIndex.TryGetValue(k, out int i) || !fits.TryGetValue(Root(i), out var f) || !f.ok) krBad.Add(root);
            }
            foreach (var k in krParent.Keys) if (!krBad.Contains(KrFind(k))) keepReal.Add(k);
        }
        static long KrFind(long x) { while (krParent.TryGetValue(x, out var q) && q != x) x = q; return x; }
        static void KrEdge(Entity a, Entity b, bool ok)
        {
            if (Ecs.IsNull(a) || Ecs.IsNull(b) || a.Index == b.Index) return;
            long ka = Ecs.K(a), kb = Ecs.K(b); if (!krParent.ContainsKey(ka)) krParent[ka] = ka; if (!krParent.ContainsKey(kb)) krParent[kb] = kb;
            long ra = KrFind(ka), rb = KrFind(kb); if (ra != rb) krParent[ra] = rb; krEdges.Add((ka, kb, ok));
        }

        // 0.31.26 (item 3): "thrust as one part". The game applies thrust per part [recon/SpaceshipPartsApplyForces.cs:39-152]. For plain
        // impulses that already adds up to the right torque on a welded group (the projection of WeldBeforeSolver turns I_iΔω_i + m_i(c_i − C)×Δv_i
        // into exactly (p − C)×J), but two pieces of that rule are not impulses: the angular stabilisation SETS the part's own Angular to its
        // target, or walks it there within a budget [:93-115], and the linear gimbal turns that part's own velocity toward the thrust axis
        // [:117-150]. On the hull of a welded group the stabilisation zeroes only the hull's own spin; the (c_hull − C)×J from the hull's push is
        // left over and becomes spin of the whole group every tick — the steady turn "with the thrusters on". A ship in one piece never has it
        // (the game has no physics joints: the stabilisation always covers the whole ship). So, for a locked group with any stabilising force,
        // the game's rule runs ONCE on the group (mass M, centre C, full inertia tensor J — the same group WeldBeforeSolver projects), the same
        // rigid change is added to every part, and the parts' buffers are emptied so the game's ApplyForces skips them (:39). Nothing else
        // reads that buffer: only SCTick_Thruster_Forces (writes) and ApplyForces (reads, clears) own a handle to it [types_layout.txt:
        // 30484-30492, 32601]; fuel, heat, particles, sound and ThrusterBlow read SCTypeThruster [recon/EPC_SCThruster.cs:154-167,274,292;
        // recon/EPC_ThrusterBlow.cs:109]. A group with no stabilising force is left to the game's per-part rule: that path is already exact.
        static TypeIndex tiPF; static bool pfTried, pfOk;
        static readonly HashSet<long> thrustGrouped = new HashSet<long>();   // parts whose buffers were taken this tick
        internal static unsafe void ThrustAsOne(EntityManager em)
        {
            thrustGrouped.Clear();
            if (!WeldFirst || !Rigid || !Multi.Authoritative(em)) return;
            if (!pfTried) { pfTried = true; try { tiPF = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<SpaceshipPartForce>()); pfOk = true; } catch (Exception ex) { ModLog.Warn("thrust as one part: off (" + ex.Message + ")"); } }
            if (!pfOk || allJoints.Count == 0) return;
            try
            {
                lockFrame = UnityEngine.Time.frameCount; lockNow = UnityEngine.Time.time;
                // the same groups as WeldBeforeSolver (which clears and rebuilds wb itself)
                wb.Clear(); wbIndex.Clear(); fits.Clear(); D3 origin = default; bool hasOrigin = false;
                foreach (var j in allJoints.Values)
                {
                    if (!Locked(j)) continue;
                    int ia = Index(em, j.partA, ref origin, ref hasOrigin), ib = Index(em, j.partB, ref origin, ref hasOrigin);
                    if (ia < 0 || ib < 0) continue; int ra = Root(ia), rb = Root(ib); if (ra != rb) { var x = wb[ra]; x.root = rb; wb[ra] = x; }
                }
                if (wb.Count == 0) return;
                members.Clear(); memberUsed = 0;   // 0.31.35: kept between ticks (it was a new dictionary and a new list per group, every tick)
                for (int i = 0; i < wb.Count; i++)
                {
                    int r = Root(i);
                    if (!members.TryGetValue(r, out var l))
                    {
                        if (memberUsed == memberPool.Count) memberPool.Add(new List<int>());
                        l = memberPool[memberUsed++]; l.Clear(); members[r] = l;
                    }
                    l.Add(wb[i].skip ? -1 : i);
                }
                if (forceHdr.Length < wb.Count) forceHdr = new IntPtr[wb.Count * 2];
                int groups = 0;
                foreach (var g in members.Values)
                {
                    if (g.Count < 2 || g.Contains(-1)) continue;   // a part the physics can't move: the game's per-part rule
                    double M = 0; D3 C = default; int heavy = g[0];
                    foreach (int i in g) { M += wb[i].m; C += wb[i].com * wb[i].m; if (wb[i].m > wb[heavy].m) heavy = i; }
                    if (!(M > 0)) continue;
                    C = C * (1.0 / M);
                    D3 P = default, L = default; M3 J = default;
                    foreach (int i in g) P += wb[i].v * wb[i].m;
                    var V0 = P * (1.0 / M);
                    foreach (int i in g) { var b = wb[i]; var r = b.com - C; L += b.R.Mul(new D3(b.I.x * b.wl.x, b.I.y * b.wl.y, b.I.z * b.wl.z)) + D3.Cross(r, b.v - V0) * b.m; b.R.AddInertia(b.I, ref J); J.AddPoint(b.m, r); }
                    if (!J.Solve(L, out var W0)) continue;
                    // ---- the forces (part-local -> world), as the game reads them
                    D3 Jlin = default, Jstab = default, Tmain = default, Tman = default, Sv = default; bool any = false, stab = false;
                    foreach (int i in g)
                    {
                        var h = Couplers.Header(em, wb[i].p, tiPF); forceHdr[i] = (IntPtr)h;   // 0.31.35: kept for the write below (it was looked up a second time: 5 calls into the game per part)
                        if (h == null || h->Length <= 0) continue;
                        var lt = Ecs.LT(em, wb[i].p); float s = lt.Scale == 0f ? 1f : lt.Scale; byte* e0 = Couplers.Elems(h);
                        for (int n = 0; n < h->Length; n++)
                        {
                            byte* e = e0 + 36 * n;   // SpaceshipPartForce, 36 bytes: _localForce +0, _localPosition +12, _gimbalLinear01 +24, _gimbalAngularPerForceUnit +28, _type +32 [types_layout.txt:30484-30490]
                            var Jw = D3.Of(Ecs.Rot(lt.Rotation, Ecs.Mul(*(float3*)(e + 0), 1f / 60f)));                                       // :67 (dt = 1/60 fixed), :119 TransformDirection
                            var pw = (D3.Of(lt.Position) - origin) + D3.Of(Ecs.Rot(lt.Rotation, Ecs.Mul(*(float3*)(e + 12), s)));                // :160 TransformPoint (with scale)
                            double gl = *(float*)(e + 24), ka = *(float*)(e + 28);
                            var tau = D3.Cross(pw - C, Jw); if (e[32] == 1) Tman += tau; else Tmain += tau;                                 // :74-77 Maneuvering / Main
                            Jlin += Jw * (1 - gl); Jstab += Jw * gl;                                                                         // :86-88
                            if (ka > 0) { Sv += Jw * (15.0 * ka); stab = true; }                                                              // :83-85 (x trace below)
                            if (gl > 0) stab = true; any = true;
                        }
                    }
                    if (!any || !stab) continue;   // no stabilisation in the group: today's per-part path is already exact
                    if (!J.Solve(Tmain, out var dWm) || !J.Solve(Tman, out var dWn)) continue;
                    // ---- angular (:92-113), world frame
                    var W1 = W0 + dWm + dWn; var W2 = W1;
                    double budget = Sv.Len * TraceInvDiag(J, wb[heavy].R);   // the game's trace = Σ 1/I of the part's diagonal inertia [recon/Spaceship__part_a.cs:304-322]
                    if (budget > 1e-9) { var target = dWn * 60.0; var rem = target - W1; double rl = rem.Len; W2 = budget >= rl ? target : W1 + rem * (budget / rl); }
                    // ---- linear (:116-149), double
                    var V1 = V0 + Jlin * (1.0 / M); var sv = Jstab * (1.0 / M); double sl = sv.Len;
                    if (sl > 1e-10)
                    {
                        var dir = sv * (1.0 / sl); var want = dir * V1.Len; if (dir.x * V1.x + dir.y * V1.y + dir.z * V1.z < 0) want = want * -1.0;
                        var diff = want - V1; double dl = diff.Len; double use = dl > 1e-12 ? System.Math.Min(dl, sl) : 0;   // the game divides 0/0 at :138
                        if (use > 0) V1 += diff * (use / dl);
                        V1 += dir * (sl - use);
                    }
                    var dV = V1 - V0; var dW = W2 - W0; if (dV.Bad || dW.Bad) continue;
                    // ---- the same rigid change on every part (ΣmΔv = MΔV since Σm(c−C) = 0; the angular momentum about C changes by exactly J·ΔW);
                    // the relative motion that is left stays with WeldBeforeSolver as before
                    foreach (int i in g)
                    {
                        var b = wb[i]; if (!Ecs.HasC(em, b.p, Ecs.tiVel)) continue;
                        var pv = (PhysicsVelocity*)em.GetComponentDataRawRW(b.p, Ecs.tiVel);
                        pv->Linear = Ecs.Add(pv->Linear, (dV + D3.Cross(dW, b.com - C)).F);
                        pv->Angular = Ecs.Add(pv->Angular, b.R.TMul(dW).F);
                        var h = (Couplers.Hdr*)forceHdr[i]; if (h != null) h->Length = 0;   // the game's ApplyForces leaves an empty buffer alone (:39)   // 0.31.35: the header found above — nothing structural happens in between
                        thrustGrouped.Add(b.key);
                    }
                    groups++;
                }
                if (groups > 0) { wThrustTicks++; wThrustGroups = System.Math.Max(wThrustGroups, groups); }
            }
            catch (Exception ex) { if (weldWarnings++ < 3) ModLog.Warn("thrust as one part: " + ex.Message); }
            finally { wb.Clear(); wbIndex.Clear(); fits.Clear(); }
        }
        /// <summary>0.31.26 (item 3): Σ 1/J_hh with J written in the frame R (the heaviest part's) — the game's budget uses Σ 1/I of a part's
        /// DIAGONAL inertia; for a positive definite tensor (J⁻¹)_ii ≥ 1/J_ii, so trace(J⁻¹) would stabilise a bit harder than the game.</summary>
        static double TraceInvDiag(M3 J, M3 R)
        {
            double t = 0;
            for (int c = 0; c < 3; c++)
            {
                var col = c == 0 ? new D3(R.a, R.d, R.g) : c == 1 ? new D3(R.b, R.e, R.h) : new D3(R.c, R.f, R.i);   // body axis c in the world
                var Jc = J.Mul(col); double d = col.x * Jc.x + col.y * Jc.y + col.z * Jc.z; if (d > 0) t += 1.0 / d;
            }
            return t;
        }

        static int Index(EntityManager em, Entity p, ref D3 origin, ref bool hasOrigin)
        {
            long k = Ecs.K(p); if (wbIndex.TryGetValue(k, out int i)) return i;
            if (!ReadBody(em, p, origin, hasOrigin, out var b, out var pos)) return -1;
            if (!hasOrigin) { origin = pos; hasOrigin = true; }
            b.root = wb.Count; wb.Add(b); wbIndex[k] = b.root; return b.root;
        }
        static int Root(int i) { while (wb[i].root != i) i = wb[i].root; return i; }

        /// <summary>One piece of the momentum watch's line; resets the sums.</summary>
        internal static string WeldSummary()
        {
            if (!WeldFirst || !Rigid) return " | weld before the solver: OFF";
            if (wTicks == 0) return "";
            string s = " | weld before the solver: " + wParts + " part(s) on " + wJoints + " locked joint(s) enter the solver as one body"
                     + (wSwapped > 0 ? ", " + wSwapped + " part(s) with their real mass during the game's forces" + (wCarried > 0 ? " (" + wCarried + " of them moving: push carried over)" : "") : "")
                     + "; relative motion taken out before the solver: " + (wRel / wTicks).ToString("F4") + " m/s per tick on average, worst " + wRelPeak.ToString("F3") + " m/s"
                     + (wRelPart != 0 ? " (part " + Ecs.OfKey(wRelPart).Index + ")" : "")
                     + (wThrustTicks > 0 ? "; thrust as one part: " + wThrustGroups + " group(s), in " + wThrustTicks + " tick(s)" : "")
                     + (wKeptReal > 0 ? "; in vacuum " + wKeptReal + " part(s) kept their real masses through the solver" : "") + (!DragAsOne ? "; welded_drag_as_one OFF" : wDragTicks > 0 ? "; drag as one part: the group slowed by " + (wDrag / wDragTicks * 100.0).ToString("F4") + "% per tick, no turning from it" : "");
            wTicks = 0; wRel = 0; wRelPeak = 0; wRelPart = 0; wDrag = 0; wDragTicks = 0; wThrustTicks = 0; wThrustGroups = 0;
            return s;
        }
    }
}
