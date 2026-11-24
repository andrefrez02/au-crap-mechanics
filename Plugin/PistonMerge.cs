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
        // builds it like that).
        // 0.31.26 (review item 28): the merge-on-stop (MaybeMerge/Merge) and kinematic (PrepareSide/MoveSide/MapCollider/MoveCollider/
        // RefreshColliders) code is gone — it was switched off since 0.26.7 and rested on a wrong premise: the game builds a part's collider and
        // mass from each block's GarageTransform and SCMass, never from its LocalTransform [recon/Spaceship__part_a.cs:216-324], so moving a
        // block's LocalTransform never carries its collision shape or mass along (the real cause of the 0.26.7 symptoms). A merged piston
        // (state 4, only from StayWhole) that gets a target now always splits (Unmerge) and moves on a weld.
        public static bool WholeUntilMoved = true;   // [Piston] whole_until_moved
        const float UnmergeThreshold = 0.01f;         // m: the request has to change by this much to split (a shaky analog signal doesn't keep splitting)

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
                j.moving = false;
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
            Unmerge(em, j, "new target " + request.ToString("F2") + " m");   // 0.31.26 (item 28): always — the kinematic move was dead code
        }

        /// <summary>Breaks the game joints of the top face again; Separating creates the weld at the current length (no start queue).</summary>
        static unsafe void Unmerge(EntityManager em, Joint j, string reason)
        {
            if (!JointsCanBreak(em)) return;   // review juntas-04: joints cannot break in this world (item 1) — the piston stays merged
            var joints = Ecs.Joints(em, out int n); if (joints == null) return;   // try on the next frame
            // review juntas-04: live joints only (IsMine). State 4 comes from StayWhole, which released nothing, so a dead top joint here was
            // broken by the GAME (strain / decoupler); taking it would let SplitFailed -> RelinkBroken repair crash damage.
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
