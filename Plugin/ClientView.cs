using System;
using Unity.Entities;
using Unity.Mathematics;

namespace CrapMechanics
{
    internal partial class Joint
    {
        // 0.28.0: client view (ClientView.cs) — the top block, its pose relative to the piston/bearing in the garage, and what was measured
        public Entity cNbr; public RigidTransform cRel0; public float cExtBase, cTry, cAngle; public bool cReady, cAngleOk, cLogged;
        // 0.31.26 (p2_torre N1): this block's SCGuid, the version of the server's layout record last looked at, and the one the top block came from
        public string cGuid; public int cTopoSeen = -1, cTopoVer = -1;
    }

    internal static partial class JointSystem
    {
        /// <summary>0.28.0 — on a client the server drives the piston/bearing (Multi.cs), and the client gets the poses of both parts from it.
        /// What the mod still has to do here is the drawing: the piston's rod and head (child renderers the game never syncs) and the bearing's
        /// angle output. Both come from the pose of the top block relative to the piston/bearing block, compared with the same relative pose in
        /// the garage (GarageTransform, which the server sends with every block). Nothing here touches physics or the game's joints.</summary>
        static unsafe void ClientView(EntityManager em, Joint j, float dt)
        {
            if (!Ecs.IsPart(em, Ecs.Parent(em, j.block))) { j.cReady = false; j.cAngleOk = false; j.cNbr = Entity.Null; return; }   // garage: Garage.cs draws
            if (j.cReady && ModNet.TopoHeard)
            {
                // 0.31.26 (p2_torre N1): the server's layout for this block changed, or the top block was a guess made while the layout's block
                // wasn't here yet (look again every second, quietly)
                if (j.cGuid == null) j.cGuid = Ecs.Guid(em, j.block);
                var tr = ModNet.TopoJointOf(j.cGuid); int v = tr != null ? tr.ver : -1;
                if (v != j.cTopoSeen) { j.cReady = false; j.cTry = 0f; }
                else if (v != -1 && j.cTopoVer != v && (j.cTry -= dt) <= 0f) { j.cReady = false; j.cTry = 0f; }
            }
            if (!j.cReady || !Ecs.Alive(em, j.cNbr))
            {
                j.cReady = false; j.cAngleOk = false;
                j.cTry -= dt; if (j.cTry > 0f) return; j.cTry = 1f;
                if (!ClientFindTop(em, j)) return;
            }
            Ecs.Pose(em, j.block, out var pB, out var rB); Ecs.Pose(em, j.cNbr, out var pN, out var rN);
            var now = math.mul(math.inverse(new RigidTransform(rB, pB)), new RigidTransform(rN, pN));   // top block in the block's space (+y = axis)
            if (j.kind == Kind.Piston)
            {
                float extReal = j.cExtBase + (now.pos.y - j.cRel0.pos.y);
                j.measuredStroke = extReal; j.measuredStrokeOk = true;
                float ext = System.Math.Max(0f, System.Math.Min(j.strokeMax + 0.5f, extReal));
                if (Ecs.IsNull(j.head) || !Ecs.Alive(em, j.head) || Ecs.IsNull(j.rod) || !Ecs.Alive(em, j.rod))
                {
                    if (j.visualAttempts >= 5) return;
                    j.visualAttempts++;
                    FindPistonParts(em, j.block, j.scale, j.strokeMax, out j.rod, out j.head, out _);
                    if (Ecs.IsNull(j.head) || Ecs.IsNull(j.rod)) return;
                    j.visualExt = -1f;
                }
                if (System.Math.Abs(ext - j.visualExt) < 0.002f) return;
                j.visualExt = ext;
                SetPistonVisual(em, j.rod, j.head, ext, j.scale);
            }
            else
            {
                // angle of the top around the block's +y, 0 = as in the garage (same convention as ServoAngle on the server)
                var v0 = Flat(math.mul(j.cRel0.rot, new float3(1f, 0f, 0f))); var v1 = Flat(math.mul(now.rot, new float3(1f, 0f, 0f)));
                if (math.lengthsq(v0) < 1e-6f || math.lengthsq(v1) < 1e-6f) { v0 = Flat(math.mul(j.cRel0.rot, new float3(0f, 0f, 1f))); v1 = Flat(math.mul(now.rot, new float3(0f, 0f, 1f))); }
                j.cAngle = (float)System.Math.Atan2(math.cross(v0, v1).y, math.dot(v0, v1)); j.cAngleOk = true;
            }
        }
        static float3 Flat(float3 v) { v.y = 0f; float l = math.length(v); return l < 1e-4f ? float3.zero : v / l; }

        /// <summary>Finds the block on the top face (the game's joints: the client has the same list, built from the same blueprint) or, for a
        /// piston, the Piston End on its axis; and their pose relative to each other in the garage.</summary>
        static unsafe bool ClientFindTop(EntityManager em, Joint j)
        {
            Entity nbr = Entity.Null; float extBase = 0f; string how = "glued to the top face";
            Entity before = j.cNbr;
            // 0.31.26 (p2_torre N1): a client has no joint list (_joints = null [recon_managed/NetcoreDoIncomingEventsSystem.cs:745]), so the
            // server's layout (ModNet TTopo: the top block or Piston End it uses, by SCGuid) comes first; the guesses below stay as the fallback
            j.cTopoVer = -1;
            if (ModNet.TopoHeard) { if (j.cGuid == null) j.cGuid = Ecs.Guid(em, j.block); var tr = ModNet.TopoJointOf(j.cGuid); j.cTopoSeen = tr != null ? tr.ver : -1; }
            if (TopFromLayout(em, j, out var nbrL, out var extL, out var howL)) { nbr = nbrL; extBase = extL; how = howL; }
            var joints = Ecs.Joints(em, out int n);
            if (Ecs.IsNull(nbr) && joints != null) TopJoints(em, j.block, joints, n, out nbr);
            if (Ecs.IsNull(nbr) && j.kind == Kind.Piston && Garage.FindEnd(em, j.block, j.middle, j.strokeMax, out var endB, out var gap, out _))
            { nbr = endB; extBase = gap; how = "Piston End " + gap.ToString("F2") + " m ahead"; }
            if (Ecs.IsNull(nbr) || !Ecs.Alive(em, nbr))
            {
                if (!j.cLogged) { j.cLogged = true; ModLog.Info(j.name + " " + j.block.Index + ": client view — no top block found yet" + (joints == null ? " (no joint list)" : "") + "; the " + (j.kind == Kind.Piston ? "rod is" : "angle output is") + " not drawn until it shows up"); }
                return false;
            }
            RigidTransform rel0;
            if (!tiGTok) { tiGTok = true; tiGT = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<GarageTransform>()); }
            if (Ecs.Has(em, j.block, tiGT) && Ecs.Has(em, nbr, tiGT))
            {
                var gb = *(RigidTransform*)Ecs.RO(em, j.block, tiGT); var gv = *(RigidTransform*)Ecs.RO(em, nbr, tiGT);
                rel0 = math.mul(math.inverse(gb), gv);
            }
            else if (Same(Ecs.Parent(em, j.block), Ecs.Parent(em, nbr)))
            {
                Ecs.Pose(em, j.block, out var pB, out var rB); Ecs.Pose(em, nbr, out var pN, out var rN);
                rel0 = math.mul(math.inverse(new RigidTransform(rB, pB)), new RigidTransform(rN, pN)); how += ", pose taken now (no GarageTransform)";
            }
            else
            {
                if (!j.cLogged) { j.cLogged = true; ModLog.Warn(j.name + " " + j.block.Index + ": client view — top block " + nbr.Index + " has no GarageTransform and is already in another part; not drawn"); }
                return false;
            }
            j.cNbr = nbr; j.cExtBase = extBase; j.cRel0 = rel0; j.cReady = true;
            if (!j.cLogged || (DetailedLog && (nbr.Index != before.Index || nbr.Version != before.Version))) ModLog.Info(j.name + " " + j.block.Index + ": client view — top block " + nbr.Index + " (" + how + "); drawn from the server's poses");
            j.cLogged = true;
            return true;
        }
    }
}
