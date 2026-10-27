using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace CrapMechanics
{
    // 0.26.3 (Andre: "the piston really had to have infinite force, like the one in minecraft"). The weld (fixed joint) is born square, but
    // Unity Physics' iterative solver doesn't hold ANY constraint with a 400:1 mass ratio (a 71 t ship on 72-173 kg legs): it
    // gives 5-20 cm after being born. The only truly rigid thing in this engine is a single part. So: a STILL piston = the two parts
    // become ONE (like the Recoupler merges decoupler and recoupler); a piston that has to MOVE = splits, moves on the weld, and merges again
    // when it stops — already at the commanded length.
    internal static partial class JointSystem
    {
        // 0.26.7 (Andre: "I don't want the hack"): merge-on-stop and kinematic piston turned OFF. The merge joined the blocks without taking
        // the upper part's collision shapes along, and the kinematic one teleported colliders (jumps), froze (a collider of 3418 shapes
        // rebuilt every frame) and left cables in the air. Only the launch-time "whole until moved" stays, which merges nothing (the garage already
        // builds it like that). The code is still here, turned off.
        public static bool MergeStill = false;
        public static bool WholeUntilMoved = true;   // [Piston] whole_until_moved
        const float MergeStillT = 0.3f, MergeSettledT = 0.3f, MergeLimitT = 1.5f;
        const float UnmergeThreshold = 0.01f;         // m: the request has to change by this much to split (a shaky analog signal doesn't keep splitting)

        static TypeIndex tiFChild, tiFPrev, tiFLEG, tiFGT, tiFConn, tiFCheck; static bool tiFOk;
        static void MergeKinds()
        {
            if (tiFOk) return; tiFOk = true;
            tiFChild = TypeManager.GetTypeIndex(Il2CppType.Of<Child>()); tiFPrev = TypeManager.GetTypeIndex(Il2CppType.Of<PreviousParent>());
            tiFLEG = TypeManager.GetTypeIndex(Il2CppType.Of<LinkedEntityGroup>()); tiFGT = TypeManager.GetTypeIndex(Il2CppType.Of<GarageTransform>());
            tiFConn = TypeManager.GetTypeIndex(Il2CppType.Of<SCConnectedJoint>()); tiFCheck = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipPartConsistencyCheck>());
        }

        // ---- rigid transform (rot, pos) without Unity.Mathematics through il2cpp
        struct Rt { public quaternion r; public float3 p; }
        static Rt RtOf(LocalTransform lt) => new Rt { r = lt.Rotation, p = lt.Position };
        static Rt RtOf(RigidTransform t) => new Rt { r = t.rot, p = t.pos };
        static Rt RtMul(Rt a, Rt b) => new Rt { r = Ecs.QMul(a.r, b.r), p = Ecs.Add(a.p, Ecs.Rot(a.r, b.p)) };
        static Rt RtInv(Rt a) { var qi = Ecs.Inv(a.r); return new Rt { r = qi, p = Ecs.Rot(qi, Ecs.Mul(a.p, -1f)) }; }
        static float AngleQ(quaternion a, quaternion b)
        {
            float d = System.Math.Abs(a.value.x * b.value.x + a.value.y * b.value.y + a.value.z * b.value.z + a.value.w * b.value.w);
            return (float)(2.0 * System.Math.Acos(System.Math.Min(1f, d)) * 180.0 / System.Math.PI);
        }

        /// <summary>Called from ActivePiston when the piston has nothing to do (request == command). Counts the still/settled time and merges
        /// when possible.</summary>
        static bool MaybeMerge(EntityManager em, Joint j, bool wants, float dt)
        {
            if (!Rigid || !MergeStill || j.kind != Kind.Piston || j.withEnd || j.rail || j.tStart > 0f || !Ecs.IsNull(j.extra) || j.unmerging) { j.tStill = 0f; return false; }
            if (wants) { j.tStill = 0f; j.tSettled = 0f; return false; }
            j.tStill += dt;
            if (j.measuredStrokeOk && System.Math.Abs(j.measuredStroke - j.strokeSettled) <= 0.005f) j.tSettled += dt; else { j.tSettled = 0f; j.strokeSettled = j.measuredStroke; }
            if (j.tStill < MergeStillT || (j.tSettled < MergeSettledT && j.tStill < MergeLimitT)) return false;
            if (j.tMergeFailed > 0f) { j.tMergeFailed -= dt; return false; }
            string why = Merge(em, j, out string how);
            if (why == null) { ModLog.Info(j.name + " " + j.block.Index + ": still — MERGED into one part (" + how + ")"); return true; }
            if (why != j.mergeLog) { j.mergeLog = why; ModLog.Info(j.name + " " + j.block.Index + ": still, but not merging: " + why + " — stays a weld"); }
            j.tMergeFailed = 5f; return false;
        }

        static bool SamePart(Entity a, Entity b) => !Ecs.IsNull(a) && a.Index == b.Index && a.Version == b.Version;

        /// <summary>Merges the upper part (partB) into the lower one (partA). Returns null on success, otherwise the reason (nothing changed).</summary>
        static unsafe string Merge(EntityManager em, Joint j, out string how)
        {
            how = "";
            MergeKinds();
            var A = j.partA; var D = j.partB;
            if (!Ecs.IsPart(em, A) || !Ecs.IsPart(em, D) || A.Index == D.Index) return "parts not valid";
            // 1) nobody else may be hanging from the part that will vanish (besides the mod's joints, which rebuild themselves)
            foreach (var t in turrets.Values)
            {
                if (t.state == 0) continue;
                if (SamePart(t.partBase, D)) return "a Turret Seat sits on the top part";
                foreach (var mv in t.movs) if (SamePart(mv.part, D)) return "a Turret Seat moves the top part";
            }
            foreach (var r in Couplers.All())
                if (r.state == 1 && (SamePart(r.partA, D) || SamePart(r.partB, D) || SamePart(r.anchor, D))) return "a Recoupler is holding the top part";
            foreach (var o in allJoints.Values)
                if (o != j && o.state == 2 && ((SamePart(o.partA, A) && SamePart(o.partB, D)) || (SamePart(o.partA, D) && SamePart(o.partB, A)))) return "another joint links the same two parts";
            var ltA = Ecs.LT(em, A); var ltD = Ecs.LT(em, D);
            if (System.Math.Abs(ltA.Scale - 1f) > 1e-3f || System.Math.Abs(ltD.Scale - 1f) > 1e-3f) return "scaled part";
            // 2) the game joints of the top face (the ones we broke to split): they must exist and go to the upper part
            var joints = Ecs.Joints(em, out int n); if (joints == null) return "game joint list not available";
            var mineSet = TopJoints(em, j.block, joints, n, out var nbr);
            if (mineSet.Count == 0 || Ecs.IsNull(nbr)) return "no game joints on the top face";
            foreach (var k in mineSet)
            {
                var sj = &joints[k]; var other = Same(sj->_a, j.block) ? sj->_b : sj->_a;
                if (!SamePart(Ecs.Parent(em, other), D)) return "a top-face joint goes to another part";
            }
            // 3) buffers (everything validated before touching anything)
            var chA = Couplers.Header(em, A, tiFChild); var chD = Couplers.Header(em, D, tiFChild);
            if (chA == null || chD == null) return "the parts' Child buffer has an unexpected format";
            var children = new List<Entity>(); var pf = (Entity*)Couplers.Elems(chD); for (int i = 0; i < chD->Length; i++) children.Add(pf[i]);
            if (children.Count == 0) return "the top part has no blocks";
            foreach (var f in children) if (!em.HasComponent(f, ComponentType.ReadOnly(Ecs.tiLT)) || !em.HasComponent(f, ComponentType.ReadOnly(Ecs.tiParent))) return "block " + f.Index + " has no LocalTransform/Parent";
            foreach (var k in mineSet)
            {
                var sj = &joints[k];
                if (Couplers.Header(em, sj->_a, tiFConn) == null || Couplers.Header(em, sj->_b, tiFConn) == null) return "SCConnectedJoint buffer has an unexpected format";
            }
            // 4) M = upper part's space -> lower part's space. Now: the pose they have. Exact: the upper piece in the GARAGE pose
            // relative to the piston, shifted by the commanded length (what the weld should be holding).
            var M = RtMul(RtInv(RtOf(ltA)), RtOf(ltD));
            var ltV = RtOf(Ecs.LT(em, j.neighbor));
            float deviation = 0f, angDeviation = 0f; bool exact = false;
            if (em.HasComponent(j.block, ComponentType.ReadOnly(tiFGT)) && em.HasComponent(j.neighbor, ComponentType.ReadOnly(tiFGT)) && SamePart(Ecs.Parent(em, j.neighbor), D))
            {
                var gp = RtOf(*(RigidTransform*)em.GetComponentDataRawRO(j.block, tiFGT)); var gv = RtOf(*(RigidTransform*)em.GetComponentDataRawRO(j.neighbor, tiFGT));
                var ltP = RtOf(Ecs.LT(em, j.block));
                float d = j.currentTarget - j.strokeBase;
                var targetV = RtMul(RtMul(ltP, new Rt { r = quaternion.identity, p = new float3 { y = d } }), RtMul(RtInv(gp), gv));
                var nowV = RtMul(M, ltV);
                deviation = Ecs.Len(Ecs.Sub(targetV.p, nowV.p)); angDeviation = AngleQ(targetV.r, nowV.r);
                // 0.26.4 (Andre's log: 11-42 cm of drop before merging, most merged crooked and stayed crooked): ALWAYS goes back to the
                // commanded length and to square. The command is never more than RigidSlack ahead of the rod (collar), so
                // the difference left is the weld giving way — exactly what the merge exists to undo.
                M = RtMul(targetV, RtInv(ltV)); exact = true;
            }
            how = exact ? "top piece set at exactly " + j.currentTarget.ToString("F2") + " m, corrected " + (deviation * 100f).ToString("F1") + " cm / " + angDeviation.ToString("F1") + " deg"
                         : "kept where it is (no garage pose to snap to)";
            // 5) apply: blocks -> lower part
            foreach (var f in children)
            {
                var lt = (LocalTransform*)em.GetComponentDataRawRW(f, Ecs.tiLT);
                var nv = RtMul(M, RtOf(*lt)); lt->Position = nv.p; lt->Rotation = nv.r;
                *(Entity*)em.GetComponentDataRawRW(f, Ecs.tiParent) = A;
                if (em.HasComponent(f, ComponentType.ReadOnly(tiFPrev))) *(Entity*)em.GetComponentDataRawRW(f, tiFPrev) = A;
            }
            chA = Couplers.Header(em, A, tiFChild); chD = Couplers.Header(em, D, tiFChild);
            if (chA == null || chD == null) { ModLog.Error(j.name + " " + j.block.Index + ": merge: Child changed midway (blocks already moved!)"); return null; }
            foreach (var f in children) { var ff = f; Couplers.Attach(chA, &ff, sizeof(Entity)); }
            chD->Length = 0;
            // 6) reconnect the game joints of the top face (with the same reinforcement as the base: a still piston is as strong as the chassis)
            string active = ""; int relinkedSet = 0;
            joints = Ecs.Joints(em, out n);
            foreach (var k in mineSet)
            {
                if (joints == null || k >= n) break;
                var sj = &joints[k]; sj->_currentStrain = 0f; if (sj->_breakForce <= 0f) sj->_breakForce = 3300f;
                if (BaseReinforce > 1f)
                {
                    var key = (Ecs.K(sj->_a), Ecs.K(sj->_b));
                    if (!(writtenSet.TryGetValue(key, out var mine) && sj->_breakForce == mine))
                    { float target = System.Math.Min(sj->_breakForce * BaseReinforce, ReinforceCap); if (sj->_breakForce < target) { sj->_breakForce = target; writtenSet[key] = target; } }
                }
                int kk = k;
                var ca = Couplers.Header(em, sj->_a, tiFConn); if (ca != null && !Couplers.HasInt(ca, kk)) Couplers.Attach(ca, &kk, 4);
                var cb = Couplers.Header(em, sj->_b, tiFConn); if (cb != null && !Couplers.HasInt(cb, kk)) Couplers.Attach(cb, &kk, 4);
                active = Couplers.ActivateJoint(em, kk); relinkedSet++;
            }
            // 7) remove the weld and the empty part; the game recalculates the lower part (mass, collider)
            try { if (Ecs.Alive(em, j.joint)) em.DestroyEntity(j.joint); } catch (Exception) { }
            try { if (Ecs.Alive(em, j.joint2)) em.DestroyEntity(j.joint2); } catch (Exception) { }
            try { if (Ecs.Alive(em, j.joint3)) em.DestroyEntity(j.joint3); } catch (Exception) { }
            j.joint = Entity.Null; j.joint2 = Entity.Null; j.joint3 = Entity.Null; DeleteRails(em, j);
            try
            {
                if (em.HasComponent(D, ComponentType.ReadOnly(tiFLEG)))
                {
                    var cl = Couplers.Header(em, D, tiFLEG);
                    if (cl != null) { var rd = (Entity*)Couplers.Elems(cl); int m = 0; for (int i = 0; i < cl->Length; i++) if (rd[i].Index == D.Index) rd[m++] = rd[i]; cl->Length = m; }
                }
            }
            catch (Exception) { }
            try { em.DestroyEntity(D); } catch (Exception ex) { ModLog.Warn(j.name + " " + j.block.Index + ": merge: destroying the empty part failed: " + ex.Message); }
            try { em.AddComponent(A, ComponentType.ReadWrite(tiFCheck)); } catch (Exception) { }
            how += "; " + children.Count + " block(s) moved to part " + A.Index + ", " + relinkedSet + " game joint(s) re-enabled (" + active + ")";
            j.neighbor = nbr; j.partB = A; j.state = 4; j.tState = 0f; j.tStill = 0f; j.tSettled = 0f; j.mergeLog = null;
            // 8) whoever was hanging from the vanished part: rebuild right away (before the next physics step)
            int redone = 0;
            foreach (var o in allJoints.Values)
            {
                if (o == j) continue;
                bool uses = SamePart(o.partA, D) || SamePart(o.partB, D);
                if (!uses) continue;
                if (o.state == 4) { o.partA = A; o.partB = A; continue; }
                if (o.state == 1) { if (SamePart(o.partA, D)) o.partA = A; continue; }
                if (o.state != 2) continue;
                if (Rebuild(em, o)) redone++;
                else { Undo(em, o, "the part under it was merged"); o.state = 0; o.tState = -1f; }
            }
            if (redone > 0) how += ", " + redone + " joint(s) on top recreated on the merged part";
            return null;
        }

        /// <summary>0.26.4: the piston's request (m), as in ActivePiston. No signal = stays where it is.</summary>
        static float PistonRequest(EntityManager em, Joint j, out bool has)
        {
            var pe = Ecs.PortOfType(em, j.block, Ecs.PortInput);
            float signal = 0f; has = !Ecs.IsNull(pe) && Ecs.ReadInput(em, pe, out signal, out _, out _);
            return has ? System.Math.Max(0f, System.Math.Min(1f, signal)) * j.stroke * j.strokeFraction : j.currentTarget;
        }

        /// <summary>0.26.4 (Andre's log: 8 legs sinking up to 1 m right after being born, before any merge): at launch the
        /// piston is already ONE part with what it carries (the garage builds it like that). If the request is the current stroke, there's no reason to split
        /// — it stays whole (state 4) and only splits when it gets a target. Called from WaitingTop at the point where it would break the joints.</summary>
        static bool StayWhole(EntityManager em, Joint j, Entity pa, Entity nbr)
        {
            if (!Rigid || !WholeUntilMoved || j.kind != Kind.Piston || j.withEnd || j.rail) return false;
            j.neighbor = nbr; j.partA = pa; j.partB = pa; j.extBase = 0f; j.strokeBase = 0f;
            float now = 0f;
            try { Fits(em, j, out var pA, out var eA, out _, out var pB, out _, out _); now = Ecs.Dot(Ecs.Sub(pB, pA), eA); } catch (Exception) { }
            if (System.Math.Abs(now) < 0.005f) now = 0f;
            j.currentTarget = now; j.target = now;
            float request = PistonRequest(em, j, out bool has);
            if (has && System.Math.Abs(request - now) > UnmergeThreshold) { j.partB = Entity.Null; return false; }   // already has a target: split now
            j.broken.Clear(); j.state = 4; j.tState = 0f; j.tStill = 0f; j.unmerging = false; j.mergeLog = null; j.tStart = 0f;
            ModLog.Info(j.name + " " + j.block.Index + ": stays WHOLE at " + now.ToString("F2") + " m (one part with what it carries, nothing to move" + (has ? "" : ", no signal yet") + ") — splits only when it gets a target");
            return true;
        }

        /// <summary>State 4: merged piston (a single part). Measures, lights the LED, reads the request; if it changes, splits again.</summary>
        static unsafe void Merged(EntityManager em, Joint j, float dt)
        {
            var pa = Ecs.Parent(em, j.block);
            var pv = Ecs.Alive(em, j.neighbor) ? Ecs.Parent(em, j.neighbor) : Entity.Null;
            // 0.26.5: while the game rebuilds parts (some other piston splitting) the block may go a few frames without a part: wait 1 s
            if (!Ecs.IsPart(em, pa) || !Ecs.IsPart(em, pv))
            {
                j.tNoPart += dt; if (j.tNoPart < 1f) return;
                j.moving = false; Undo(em, j, !Ecs.IsPart(em, pa) ? "left the ship" : "the top block is gone"); j.state = 0; j.tState = -1f; j.tNoPart = 0f; return;
            }
            if (pv.Index != pa.Index)
            {
                j.moving = false; j.topSide = null;
                // the game split the part (something broke, decoupler, rewind): back to a weld at the pose it is in
                j.partA = pa; j.partB = pv; deferred = false;
                if (Create(em, j, true)) { j.state = 2; j.tState = 0f; Reinforce(em, j); ModLog.Info(j.name + " " + j.block.Index + ": the merged part was split by the game — back to a weld (stroke " + j.currentTarget.ToString("F2") + " m)"); }
                else if (!deferred) { Undo(em, j, "split while merged, recreating failed"); j.state = 0; j.tState = -1f; }
                return;
            }
            j.partA = pa; j.partB = pa; j.tNoPart = 0f;
            try { PistonVisual(em, j, dt); } catch (Exception) { }
            bool power = Ecs.HasPower(em, j.block, out _);
            float request = PistonRequest(em, j, out _);
            float diff = request - j.currentTarget;
            // hysteresis: starts moving with UnmergeThreshold of difference (a shaky signal doesn't move it), and once moving goes all the way to the request
            bool wants = power && (j.moving ? System.Math.Abs(diff) > 1e-4f : System.Math.Abs(diff) > UnmergeThreshold);
            Ecs.SetConsumption(em, j.block, wants ? PistonConsumptionPerM * j.stroke * j.strokeFraction : IdleConsumption);
            if (!wants)
            {
                if (j.moving) { j.moving = false; if (DetailedLog) ModLog.Info(j.name + " " + j.block.Index + ": stopped at " + j.currentTarget.ToString("F2") + " m (still one part)"); }
                return;
            }
            if (!j.moving)
            {
                string why = Kinematic ? PrepareSide(em, j, pa) : null;
                if (!Kinematic) { Unmerge(em, j, "new target " + request.ToString("F2") + " m"); return; }
                if (why != null)
                {
                    if (why != j.kinLog) { j.kinLog = why; ModLog.Info(j.name + " " + j.block.Index + ": can't move as one part (" + why + ") — splitting and moving on a weld"); }
                    Unmerge(em, j, "new target " + request.ToString("F2") + " m"); return;
                }
                j.kinLog = null; j.moving = true; j.tRecalc = 0f;
                ModLog.Info(j.name + " " + j.block.Index + ": moving as ONE part to " + request.ToString("F2") + " m (" + j.topSide.Count + " block(s) on the moving side)");
            }
            if (j.sidePart != Ecs.K(pa))
            {
                // the game swapped the part in the middle of the movement: rebuild the upper side before continuing
                string why = PrepareSide(em, j, pa);
                if (why != null) { j.moving = false; ModLog.Info(j.name + " " + j.block.Index + ": part changed while moving (" + why + ") — splitting and moving on a weld"); Unmerge(em, j, "new target " + request.ToString("F2") + " m"); return; }
            }
            MoveSide(em, j, pa, request, dt);
        }

        // ================================================================ 0.26.5: kinematic piston (Andre: "yes, do it")
        // The piston NEVER splits: the blocks of the upper side move inside the part itself, PistonSpeed x dt per frame, along the
        // axis. There is no joint to give way: if the foot touches the ground and the piston keeps extending, physics pushes the foot out
        // of the ground by pushing the whole part (it's the ship that rises). Collider and mass: the game recalculates the part (consistency check) every
        // KinRecalc s while moving and once when it stops.
        public static bool Kinematic = false;    // 0.26.7: turned off (see MergeStill)
        public static float KinRecalc = 0f;       // [Piston] rebuild_interval (0 = every frame)

        /// <summary>Finds the blocks of the upper side (what would become the upper part if it split): searches the game joints inside the
        /// part, starting from the top face's neighbours, without going through the piston. Returns null if it can move like this, otherwise the reason.</summary>
        static unsafe string PrepareSide(EntityManager em, Joint j, Entity part)
        {
            var joints = Ecs.Joints(em, out int n); if (joints == null) return "game joint list not available";
            var mineSet = TopJoints(em, j.block, joints, n, out var nbr);
            if (mineSet.Count == 0 || Ecs.IsNull(nbr)) return "no game joints on the top face";
            var skip = new HashSet<int>(mineSet);
            var adj = new Dictionary<long, List<Entity>>();
            for (int k = 0; k < n; k++)
            {
                if (skip.Contains(k)) continue;
                var sj = &joints[k]; if (sj->_breakForce < 0f) continue;
                var a = sj->_a; var b = sj->_b;
                if (!Ecs.Alive(em, a) || !Ecs.Alive(em, b)) continue;
                if (!SamePart(Ecs.Parent(em, a), part) || !SamePart(Ecs.Parent(em, b), part)) continue;
                long ka = Ecs.K(a), kb = Ecs.K(b);
                if (!adj.TryGetValue(ka, out var la)) adj[ka] = la = new List<Entity>(); la.Add(b);
                if (!adj.TryGetValue(kb, out var lb)) adj[kb] = lb = new List<Entity>(); lb.Add(a);
            }
            long kp = Ecs.K(j.block);
            var seen = new HashSet<long>(); var queue = new Queue<Entity>(); var side = new List<Entity>();
            foreach (var k in mineSet) { var sj = &joints[k]; var o = Same(sj->_a, j.block) ? sj->_b : sj->_a; if (SamePart(Ecs.Parent(em, o), part) && seen.Add(Ecs.K(o))) queue.Enqueue(o); }
            while (queue.Count > 0)
            {
                var e = queue.Dequeue(); side.Add(e);
                if (!adj.TryGetValue(Ecs.K(e), out var ls)) continue;
                foreach (var x in ls)
                {
                    long kx = Ecs.K(x);
                    if (kx == kp) return "the top side is also attached to the base through another path";
                    if (seen.Add(kx)) queue.Enqueue(x);
                }
            }
            if (side.Count == 0) return "nothing on the top side";
            // anything with a physics joint attached to a moving block can't be dragged (its frame would be left behind)
            foreach (var o in allJoints.Values)
            {
                if (o == j || (o.state != 1 && o.state != 2)) continue;
                if ((!Ecs.IsNull(o.block) && seen.Contains(Ecs.K(o.block))) || (!Ecs.IsNull(o.neighbor) && seen.Contains(Ecs.K(o.neighbor))))
                    return (o.kind == Kind.Bearing ? "a bearing" : "a piston") + " (" + o.name + " " + o.block.Index + ") with its own joint is on the moving side";
            }
            foreach (var t in turrets.Values)
            {
                if (t.state == 0) continue;
                if (seen.Contains(Ecs.K(t.block))) return "a Turret Seat is on the moving side";
                foreach (var v in t.nbrPlates) if (seen.Contains(Ecs.K(v))) return "a Turret Seat holds a piece on the moving side";
            }
            foreach (var r in Couplers.All())
                if (r.state == 1 && (seen.Contains(Ecs.K(r.block)) || (!Ecs.IsNull(r.decoupler) && seen.Contains(Ecs.K(r.decoupler))))) return "a Recoupler on the moving side is holding something";
            foreach (var e in side) if (!em.HasComponent(e, ComponentType.ReadOnly(Ecs.tiLT))) return "block " + e.Index + " has no LocalTransform";
            j.neighbor = nbr; j.topSide = side; j.sidePart = Ecs.K(part);
            MapCollider(em, j, part);
            return null;
        }

        static unsafe void MoveSide(EntityManager em, Joint j, Entity part, float request, float dt)
        {
            float step = PistonSpeed * dt;
            float target = request > j.currentTarget + step ? j.currentTarget + step : request < j.currentTarget - step ? j.currentTarget - step : request;
            float d = target - j.currentTarget; if (d == 0f) return;
            var ltP = Ecs.LT(em, j.block);
            var axis = Ecs.Rot(ltP.Rotation, Ecs.UY);                       // the piston's +Y, in the part's space
            var shift = Ecs.Mul(axis, d);   // parts with scale 1 (the merge already refuses the others)
            int movedSet = 0;
            foreach (var e in j.topSide)
            {
                if (!Ecs.Alive(em, e)) continue;
                var lt = (LocalTransform*)em.GetComponentDataRawRW(e, Ecs.tiLT); if (lt == null) continue;
                lt->Position = Ecs.Add(lt->Position, shift); movedSet++;
            }
            j.currentTarget = target; j.target = target;
            MoveCollider(em, j, part, shift);
        }

        // ---- 0.26.5b: the collider. The test showed "did NOT rebuild the part's collider": the game does NOT rebuild the part's collider when the
        // blocks change (and the consistency check tag even made the game SPLIT the part — "merged part was split by the game"). A part's
        // collider is a Unity Physics CompoundCollider: a list of child shapes (CompoundFromChild = pose in the part's space)
        // + a tree (BVH) on top. The mod moves the shapes of the upper side's blocks along with them and asks for CompoundCollider.Update()
        // (rebuilds the tree and the AABB) once per part per frame.
        static TypeIndex tiColF; static bool tiColOk;
        static unsafe Collider* PtrCollider(EntityManager em, Entity part)
        {
            try
            {
                if (!tiColOk) { tiColOk = true; tiColF = TypeManager.GetTypeIndex(Il2CppType.Of<PhysicsCollider>()); }
                if (!em.HasComponent(part, ComponentType.ReadOnly(tiColF))) return null;
                return *(Collider**)em.GetComponentDataRawRO(part, tiColF);
            }
            catch (Exception) { return null; }
        }
        const int ChildSize = 40;   // CompoundCollider.Child: CompoundFromChild (28) + m_ColliderOffset (4) + Entity (8)
        static unsafe byte* Children(CompoundCollider* cc, out int n)
        {
            var ba = (int*)((byte*)cc + 24);   // m_ChildrenBlob: Offset relative to the field itself, Length
            n = ba[1]; return (byte*)ba + ba[0];
        }
        /// <summary>Which shapes of the part's compound collider belong to the moving blocks: by the Entity recorded in the shape (the block or one of
        /// its children); if the game doesn't record it, by the nearest block.</summary>
        static unsafe void MapCollider(EntityManager em, Joint j, Entity part)
        {
            j.colIdx = null; j.colBlob = 0;
            var col = PtrCollider(em, part);
            if (col == null) { ModLog.Warn(j.name + " " + j.block.Index + ": the part has no collider — the moving side will pass through things"); return; }
            if (*(byte*)col != (byte)ColliderType.Compound) { ModLog.Warn(j.name + " " + j.block.Index + ": the part's collider is not a compound (type " + *(byte*)col + ") — can't move its shapes"); return; }
            var cc = (CompoundCollider*)col; var fs = Children(cc, out int n);
            // owner of each entity: block of the part (and its children, 3 levels)
            var owner = new Dictionary<long, Entity>(); var blocks = new List<(Entity e, float3 p)>();
            var ch = Couplers.Header(em, part, tiFChild);
            if (ch != null) { var pe = (Entity*)Couplers.Elems(ch); for (int i = 0; i < ch->Length; i++) blocks.Add((pe[i], Ecs.LT(em, pe[i]).Position)); }
            foreach (var (b, _) in blocks)
            {
                owner[Ecs.K(b)] = b;
                foreach (var f1 in Ecs.Children(em, b)) { owner[Ecs.K(f1)] = b; foreach (var f2 in Ecs.Children(em, f1)) { owner[Ecs.K(f2)] = b; foreach (var f3 in Ecs.Children(em, f2)) owner[Ecs.K(f3)] = b; } }
            }
            var moveIt = new HashSet<long>(); foreach (var e in j.topSide) moveIt.Add(Ecs.K(e));
            var idx = new List<int>(); int byEnt = 0, byNear = 0, noOwner = 0;
            for (int i = 0; i < n; i++)
            {
                var c = (CompoundCollider.Child*)(fs + (long)i * ChildSize);
                Entity b = Entity.Null;
                if (!Ecs.IsNull(c->Entity) && owner.TryGetValue(Ecs.K(c->Entity), out var d)) { b = d; byEnt++; }
                else if (blocks.Count > 0)
                {
                    float m = float.MaxValue; foreach (var (e, p) in blocks) { float dd = Ecs.Len(Ecs.Sub(p, c->CompoundFromChild.pos)); if (dd < m) { m = dd; b = e; } }
                    if (m <= 0.6f) byNear++; else { b = Entity.Null; noOwner++; }
                }
                if (!Ecs.IsNull(b) && moveIt.Contains(Ecs.K(b))) idx.Add(i);
            }
            j.colIdx = idx; j.colBlob = (long)col;
            ModLog.Info(j.name + " " + j.block.Index + ": collider — " + idx.Count + " of " + n + " shape(s) move with the top side (" + byEnt + " matched by entity, " + byNear + " by nearest block" + (noOwner > 0 ? ", " + noOwner + " unmatched" : "") + ")");
        }
        static readonly Dictionary<long, Entity> colDirty = new Dictionary<long, Entity>();
        static unsafe void MoveCollider(EntityManager em, Joint j, Entity part, float3 shift)
        {
            var col = PtrCollider(em, part);
            if (col == null) return;
            if ((long)col != j.colBlob) MapCollider(em, j, part);   // the game swapped the collider: rebuild the map (the blocks are already in the new place)
            if (j.colIdx == null || j.colIdx.Count == 0 || (long)col != j.colBlob) return;
            var fs = Children((CompoundCollider*)col, out int n);
            foreach (var i in j.colIdx) { if (i >= n) continue; var c = (CompoundCollider.Child*)(fs + (long)i * ChildSize); c->CompoundFromChild.pos = Ecs.Add(c->CompoundFromChild.pos, shift); }
            colDirty[Ecs.K(part)] = part;
        }
        static float tColUpdate; static bool colUpdateFailed;
        /// <summary>Once per frame (after all joints): rebuilds the collider tree/AABB of the parts whose shapes moved.</summary>
        static unsafe void RefreshColliders(EntityManager em, float dt)
        {
            if (colDirty.Count == 0) return;
            tColUpdate += dt; if (KinRecalc > 0f && tColUpdate < KinRecalc) return; tColUpdate = 0f;
            foreach (var part in colDirty.Values)
            {
                try { var col = PtrCollider(em, part); if (col != null && *(byte*)col == (byte)ColliderType.Compound) ((CompoundCollider*)col)->Update(false); }
                catch (Exception ex) { if (!colUpdateFailed) { colUpdateFailed = true; ModLog.Warn("piston: updating the part's collider failed: " + ex.Message); } }
            }
            colDirty.Clear();
        }

        /// <summary>Breaks the game joints of the top face again; Separating creates the weld at the current length (no start queue).</summary>
        static unsafe void Unmerge(EntityManager em, Joint j, string reason)
        {
            var joints = Ecs.Joints(em, out int n); if (joints == null) return;   // try on the next frame
            var mineSet = TopJoints(em, j.block, joints, n, out var nbr);
            if (mineSet.Count == 0 || Ecs.IsNull(nbr)) { ModLog.Warn(j.name + " " + j.block.Index + ": merged, but no game joints on the top face to release — starting over"); Undo(em, j, "merged without top joints"); j.state = 0; j.tState = -1f; return; }
            j.neighbor = nbr;
            try { Ecs.Pose(em, j.block, out var pP0, out var rP0); Ecs.Pose(em, nbr, out var pV0, out var rV0);
                  j.poseRelNb = math.mul(math.inverse(new RigidTransform(rP0, pP0)), new RigidTransform(rV0, pV0)); j.poseRelNbOk = true; }
            catch (Exception) { j.poseRelNbOk = false; }
            j.broken.Clear();
            foreach (var k in mineSet) Break(j, &joints[k], k);
            j.partA = Ecs.Parent(em, j.block); j.partB = Entity.Null; j.state = 1; j.tState = 0f; j.unmerging = true; j.tStill = 0f;
            ModLog.Info(j.name + " " + j.block.Index + ": " + reason + " — splitting the merged part (" + mineSet.Count + " joint(s)) to move");
        }
    }
}
