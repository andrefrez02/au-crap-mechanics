using System;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CrapMechanics
{
    internal static partial class JointSystem
    {
        // ================================================================ 0.28.0: turret seat on a network (ModNet.cs)
        // The server moves the turret (it breaks the plate joints, holds the pieces and turns them), so a player seated on a CLIENT has to
        // send the aim there. On the client the turret never runs its state machine: no joint is broken, no inertia or velocity is touched.
        // It only (1) adds this player's mouse to the aim, with the same limits, and sends it; (2) draws the seat and rings at the real pose
        // of the pieces on the plates, which the server keeps sending (the camera and the cockpit mode follow that drawing).

        static void ClientTurret(EntityManager em, Turret t, float dt)
        {
            if (t.state != 2) t.state = 2;   // = drawn (TurretBeforeTransform turns the renderers by Qvis)
            if (t.isSeated)
            {
                if (!t.sentSeated) { t.sentSeated = true; ModLog.Info(t.name + " " + t.block.Index + ": seated on a client — the aim goes to the server, which turns the turret"); }
                if (!FreeCursor && CursorLocked()) MouseInto(ref t.yaw, ref t.pitch);
                LimitAim(t);
                ModNet.SendAim(em, t.block, t.yaw, t.pitch, true);
            }
            else if (t.sentSeated) { t.sentSeated = false; ModNet.SendAim(em, t.block, t.yaw, t.pitch, false); }
            ClientDrawing(em, t, dt);
        }

        /// <summary>[Multiplayer] test_loopback in a solo game: the seated player's mouse is kept apart and sent through ModNet, and the turret only
        /// moves with what comes back — the server side of a remote aim, testable on one machine.</summary>
        static void LoopbackAim(EntityManager em, Turret t)
        {
            if (t.isSeated)
            {
                if (!t.lbActive) { t.lbActive = true; t.lbYaw = t.yaw; t.lbPitch = t.pitch; }
                if (!FreeCursor && CursorLocked()) MouseInto(ref t.lbYaw, ref t.lbPitch);
                LimitAim(t, ref t.lbYaw, ref t.lbPitch);
                ModNet.SendAim(em, t.block, t.lbYaw, t.lbPitch, true);
            }
            else if (t.lbActive) { t.lbActive = false; ModNet.SendAim(em, t.block, t.lbYaw, t.lbPitch, false); }
        }

        static bool CursorLocked() { try { return Cursor.lockState == CursorLockMode.Locked; } catch (Exception) { return true; } }
        /// <summary>The mouse of this frame added to an aim, like ActiveTurret does (past 90 degrees up the sideways mouse inverts). No wrapping:
        /// the server wraps unless the whole ship turns.</summary>
        static void MouseInto(ref float yaw, ref float pitch)
        {
            var d = InputKeys.MouseDelta();
            float sx = System.Math.Abs(pitch) > 90f ? -1f : 1f;
            yaw += sx * d.x * TurretSens; pitch += d.y * TurretSens;
        }
        static float Wrap180(float a) { while (a > 180f) a -= 360f; while (a < -180f) a += 360f; return a; }
        static quaternion AimQuat(Turret t) => Ecs.QMul(QuatAxis(Ecs.UY, t.yaw * (float)(System.Math.PI / 180.0)), QuatAxis(Ecs.UX, -t.pitch * (float)(System.Math.PI / 180.0)));

        static void ClientDrawing(EntityManager em, Turret t, float dt)
        {
            if (!Ecs.Alive(em, t.cNbr)) { t.tCNbr -= dt; if (t.tCNbr <= 0f) { t.tCNbr = 1f; FindClientPlate(em, t); } }
            if (Ecs.Alive(em, t.cNbr) && Ecs.Parent(em, t.cNbr).Index != Ecs.Parent(em, t.block).Index)
            {
                // same formula as the server (ActiveTurret): the drawing follows the real pose of the piece on the plate
                Ecs.Pose(em, t.block, out _, out var rotB); Ecs.Pose(em, t.cNbr, out _, out var rotV);
                t.Qvis = Ecs.QMul(Ecs.QMul(Ecs.Inv(rotB), rotV), Ecs.Inv(t.cRn0));
            }
            else if (!t.cPlates) t.Qvis = t.isSeated ? AimQuat(t) : quaternion.identity;   // nothing on the plates: the seat goes straight to this player's aim
            else t.Qvis = quaternion.identity;   // the plates are still one part with the ship (whole until aimed / whole ship): nothing turns in the drawing
        }

        static unsafe void FindClientPlate(EntityManager em, Turret t)
        {
            var joints = Ecs.Joints(em, out int n); if (joints == null) return;
            PlateJoints(em, t.block, joints, n, out var nbrs);
            t.cPlates = nbrs.Count > 0;
            Entity pick = Entity.Null; foreach (var v in nbrs) if (Ecs.Alive(em, v)) { pick = v; break; }
            if (Ecs.IsNull(pick))
            {
                if (!t.cLogged) { t.cLogged = true; ModLog.Info(t.name + " " + t.block.Index + ": client view — nothing on the plates" + (t.cPlates ? " is loaded yet" : "") + "; the seat follows this player's aim"); }
                return;
            }
            var qgB = GarageRot(em, t.block, out bool okB); var qgV = GarageRot(em, pick, out bool okV);
            if (okB && okV) t.cRn0 = Ecs.QMul(Ecs.Inv(qgB), qgV);
            else { Ecs.Pose(em, t.block, out _, out var rotB); Ecs.Pose(em, pick, out _, out var rotV); t.cRn0 = Ecs.QMul(Ecs.Inv(rotB), rotV); }
            t.cNbr = pick;
            if (!t.cLogged) { t.cLogged = true; ModLog.Info(t.name + " " + t.block.Index + ": client view — drawn from the pose of block " + pick.Index + " on the plates (" + nbrs.Count + " block(s) there)"); }
        }
    }
}
