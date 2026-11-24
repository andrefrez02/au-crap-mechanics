using System;
using System.Collections.Generic;
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
            if (ClientFromLayout(em, t, dt)) return;   // 0.31.26 (p2_torre N1): the server's layout; below, the old guess (server without it)
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

        // ================================================================ 0.31.26 (p2_torre N1): the joint layout from the server (ModNet TTopo)
        // A client's SpaceshipSingleton has _joints = null and _connectedJoints = null [recon_managed/NetcoreDoIncomingEventsSystem.cs:745], so
        // FindClientPlate / ClientFindTop never found anything there: the seat drawing stayed still while the plate pieces (moved by the server)
        // turned, the whole-ship mode showed the yaw twice (the drawing turned it over a ship that turns too), the turret's slip ring bridge
        // (t.movs) never existed and the bearing's angle output / the rod of a piston without a Piston End were never drawn.

        /// <summary>Server side: the layout to send — each turret seat in a part (its mode and, with pieces on the plates, one block per piece
        /// with the faces and the garage rotation the server itself uses) and each active piston/bearing with its top block (or Piston End).</summary>
        internal static void TopologyOut(EntityManager em, List<ModNet.TopoTurret> ts, List<ModNet.TopoJoint> js)
        {
            foreach (var t in turrets.Values)
            {
                if (!Ecs.Alive(em, t.block) || !Ecs.IsPart(em, Ecs.Parent(em, t.block))) continue;
                string g = Ecs.Guid(em, t.block); if (g.Length != 32) continue;
                byte mode = t.state == 5 ? (byte)2 : t.state != 2 ? (byte)0 : t.wholeShip ? (byte)3 : t.movs.Count > 0 ? (byte)1 : (byte)4;
                var rec = new ModNet.TopoTurret { guid = g, mode = mode };
                if (mode == 1)
                    foreach (var m in t.movs)
                    {
                        if (rec.pieces.Count >= 255) break;
                        if (!Ecs.Alive(em, m.nbr)) continue;
                        string gn = Ecs.Guid(em, m.nbr); if (gn.Length != 32) continue;
                        rec.pieces.Add(new ModNet.TopoPiece { guid = gn, faces = m.faces ?? "", rn0 = m.Rn0 });
                    }
                ts.Add(rec);
                if (ts.Count >= 65535) break;
            }
            foreach (var j in allJoints.Values)
            {
                if ((j.state != 2 && j.state != 4) || (j.kind != Kind.Piston && j.kind != Kind.Bearing)) continue;
                if (!Ecs.Alive(em, j.block) || !Ecs.Alive(em, j.neighbor)) continue;
                string g = Ecs.Guid(em, j.block), gn = Ecs.Guid(em, j.neighbor); if (g.Length != 32 || gn.Length != 32) continue;
                js.Add(new ModNet.TopoJoint { guid = g, nbr = gn, withEnd = j.withEnd, extBase = j.withEnd ? j.extBase : 0f });
                if (js.Count >= 65535) break;
            }
        }

        /// <summary>Client side: the block with this SCGuid. Review acopla_rede-3: the map is built from the cached BlockSet (Ecs.Launched) and
        /// rebuilt only when that set changes (its stamp: count change, rewind, 30 s refresh) — a guid that is missing costs one dictionary
        /// lookup per retry, not a scan of every block each second. An entry is trusted only while that entity still carries the same SCGuid.</summary>
        static readonly Dictionary<string, Entity> guidMap = new Dictionary<string, Entity>(); static int guidStamp = int.MinValue;
        internal static Entity ByGuid(EntityManager em, string guid)
        {
            if (guid == null || guid.Length != 32) return Entity.Null;
            var set = Ecs.Launched(em);
            if (set.stamp != guidStamp)
            {
                guidStamp = set.stamp; guidMap.Clear();
                foreach (var b in set.all) { if (!Ecs.Alive(em, b)) continue; var g = Ecs.Guid(em, b); if (g.Length == 32) guidMap[g] = b; }
            }
            return guidMap.TryGetValue(guid, out var e) && Ecs.Alive(em, e) && Ecs.Guid(em, e) == guid ? e : Entity.Null;
        }

        /// <summary>Client side: draws the seat from the server's layout. Builds movs that are DRAWING ONLY (no joint, no inertia, no velocity —
        /// the client never touches physics), which also give the slip ring its turret bridge (SlipRings.Scan goes through t.movs), and uses the
        /// server's own formula for Qvis (ActiveTurret / WholeShipTurns). False = the server sent no layout for this block.</summary>
        static bool ClientFromLayout(EntityManager em, Turret t, float dt)
        {
            if (!ModNet.TopoHeard) return false;
            if (t.cGuid == null || t.cGuid.Length != 32) { t.cGuid = Ecs.Guid(em, t.block); if (t.cGuid.Length != 32) return false; }
            var rec = ModNet.TopoTurretOf(t.cGuid);
            if (rec == null) { if (t.topoVer != -1) { t.movs.Clear(); t.topoVer = -1; t.topoMode = -1; } return false; }
            bool missing = rec.mode == 1 && t.movs.Count < rec.pieces.Count;
            if (rec.ver != t.topoVer || (missing && (t.tTopo -= dt) <= 0f))
            {
                bool fresh = rec.ver != t.topoVer;
                t.topoVer = rec.ver; t.topoMode = rec.mode; t.tTopo = 1f;
                t.movs.Clear();
                if (rec.mode == 1)
                    foreach (var p in rec.pieces)
                    {
                        var e = ByGuid(em, p.guid); if (Ecs.IsNull(e)) continue;
                        t.movs.Add(new TurretMov { nbr = e, part = Entity.Null, Rn0 = p.rn0, faces = p.faces ?? "" });   // part: set below, once separated here
                    }
                if (fresh || t.movs.Count == rec.pieces.Count)
                    ModLog.Info(t.name + " " + t.block.Index + ": client view — server layout: " + (rec.mode == 1 ? rec.pieces.Count + " piece(s) on the plates, " + t.movs.Count + " found here" : rec.mode == 2 ? "WHOLE until aimed" : rec.mode == 3 ? "the whole ship turns" : rec.mode == 4 ? "nothing on the plates (the seat follows the aim)" : "nothing turns now"));
            }
            Ecs.Pose(em, t.block, out _, out var rotB);
            var pBase = Ecs.Parent(em, t.block);
            switch (rec.mode)
            {
                case 1:
                {
                    // ActiveTurret: the drawing follows the REAL pose of the first piece, Qvis = inv(rotB)·rotV·inv(Rn0)
                    t.Qvis = quaternion.identity; bool first = true;
                    foreach (var m in t.movs)
                    {
                        // the client recreates parts when the server's events say so: follow the block. Not separated here yet (still in the
                        // ship's part) = no part, so nothing turns and the slip ring doesn't take a ring of the ship's own part for the plate's
                        var pv = Ecs.Alive(em, m.nbr) ? Ecs.Parent(em, m.nbr) : Entity.Null;
                        m.part = Ecs.IsPart(em, pv) && pv.Index != pBase.Index ? pv : Entity.Null;
                        if (!first || Ecs.IsNull(m.part)) continue;
                        first = false;
                        Ecs.Pose(em, m.nbr, out _, out var rotV);
                        t.Qvis = Ecs.QMul(Ecs.QMul(Ecs.Inv(rotB), rotV), Ecs.Inv(m.Rn0));
                    }
                    break;
                }
                case 3: t.Qvis = t.isSeated ? QuatAxis(Ecs.UX, -t.pitch * (float)(System.Math.PI / 180.0)) : quaternion.identity; break;   // WholeShipTurns: only the tilt (the yaw is the real ship)
                case 4: t.Qvis = t.isSeated ? AimQuat(t) : quaternion.identity; break;   // ActiveTurret with no piece: the drawing goes straight to the aim (only this player's is known here)
                default: t.Qvis = quaternion.identity; break;   // whole until aimed / waiting / separating / welded: nothing turns
            }
            return true;
        }

        /// <summary>0.31.26 (p2_torre N1): the server's top block for a piston/bearing on a client (ClientView.cs), resolved by SCGuid.</summary>
        static bool TopFromLayout(EntityManager em, Joint j, out Entity nbr, out float extBase, out string how)
        {
            nbr = Entity.Null; extBase = 0f; how = null;
            if (!ModNet.TopoHeard) return false;
            if (j.cGuid == null || j.cGuid.Length != 32) { j.cGuid = Ecs.Guid(em, j.block); if (j.cGuid.Length != 32) return false; }
            var rec = ModNet.TopoJointOf(j.cGuid); if (rec == null) return false;
            var e = ByGuid(em, rec.nbr); if (Ecs.IsNull(e)) return false;
            nbr = e; extBase = rec.extBase; j.cTopoVer = rec.ver;
            how = rec.withEnd ? "Piston End " + rec.extBase.ToString("F2") + " m ahead, from the server's layout" : "glued to the top face, from the server's layout";
            return true;
        }
    }
}
