using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CrapMechanics
{
    /// <summary>0.12.8: the Replay mod reads this by reflection — entities whose DRAWING Crap Mechanics turned this frame (turret seat): Replay records the
    /// visual pose (LocalToWorld) of those only. (Replay 0.3.5 compared every child and recorded garbage on the ones that move with physics.)</summary>
    public static class ReplayBridge { public static readonly HashSet<long> Rotated = new HashSet<long>(); }

    /// <summary>A piece glued to a Turret Seat's plates: becomes a part of its own attached by a ball-and-socket to the pivot, and the mod
    /// turns it along with the aim (servo by angular velocity, infinite inertia = doesn't sag with weight).</summary>
    internal class TurretMov
    {
        public Entity nbr, part, joint;
        public quaternion Rn0 = quaternion.identity;   // rotation of the glued block relative to the turret (garage)
        public float3 originalInertia; public bool savedInertia;
        public bool link; public string faces = "";   // 0.12.11: structure attached to 2+ faces of the turret (plate+plate, plate+base...) = LINK
    }

    /// <summary>Turret Seat (0.12.1): the BLOCK stays attached to the ship (base and output still — cables don't break). What turns: (1) the drawing
    /// (the game's seat, ring, plates, triggers) — the mod turns the LocalTransforms of the child renderers around the pivot right before the game
    /// computes the LocalToWorlds and undoes it right after (the game keeps seeing everything in place); (2) whatever is glued to the plates — separate
    /// physics pieces, attached to the pivot, that follow the aim.</summary>
    internal class Turret
    {
        public Entity block, partBase;
        public int state;            // 0 waiting for launch, 1 separating, 2 active, 3 welded (something on the plates is also attached to the ship)
        public float tState; public int warnings, splitFailures; public bool didWarnStuck, heightLogged;
        public List<(int idx, Entity a, Entity b, float breakForce)> broken = new List<(int, Entity, Entity, float)>();
        public List<Entity> nbrPlates = new List<Entity>();
        public Dictionary<long, string> faceOf = new Dictionary<long, string>();   // 0.12.11: neighbour block -> face of the turret where it glues
        public Dictionary<long, RigidTransform> poseRel = new Dictionary<long, RigidTransform>();   // 0.26.0: pose of each neighbour relative to the turret at release time
        public bool wholeShip; public float3 top0, front0;   // 0.12.11: turret + structure are one thing -> the whole ship turns to the aim
        public float headingCont, headingLast; public bool headingOk;   // 0.19.6: the ship's heading CONTINUOUS (no wrapping at +-180) in whole-ship mode
        public List<TurretMov> movs = new List<TurretMov>();
        public float yaw, pitch;                            // aim (degrees)
        public quaternion Qvis = quaternion.identity;       // rotation of the drawing (in the block's space)
        public float tLog, errLog = 999f; public string name = "SC_TurretSeat";
        public bool isSeated;
        public bool release; public bool wholeLog; public Dictionary<string, HashSet<long>> faceStructure = new Dictionary<string, HashSet<long>>();   // 0.26.8: whole until aimed
        public Entity[] movables; public float tMovables = 9f;  // child renderers that turn
        public LocalTransform[] saved; public bool rotated;
        // 0.28.0 multiplayer (ModNet.cs): localAim = the mouse of THIS machine aims it; remote* = the aim a player seated on another machine sent;
        // on a client: cNbr/cRn0 = a block on a plate and its garage rotation relative to the turret, to draw the seat from the server's poses
        public bool localAim, remoteSeated, sentSeated, lbActive; public float remoteYaw, remotePitch, lbYaw, lbPitch;
        public Entity cNbr; public quaternion cRn0 = quaternion.identity; public float tCNbr; public bool cPlates, cLogged;
    }

    internal static partial class JointSystem
    {
        static readonly Dictionary<long, Turret> turrets = new Dictionary<long, Turret>();
        public static readonly float3 TurretPivot = new float3 { x = 0f, y = 0f, z = 0f };   // = modelos_v2.TURRET_PIVOT (0.12.5: center of the 2 x 2 x 1.5 block)
        // 0.24.0 (Andre: "the turret seat's ports are turning along with the seat itself instead of staying on the base"): everything from here
        // down is the base and does NOT turn — the 16 port plugs and the bottom glue area are all at y -1.1875, and the lowest thing that
        // turns is the seat, at y -0.25. (The old TurretPort was (99,99,99), a sentinel that never matched anything:
        // in practice every child off the origin turned, ports included.)
        public const float TurretBaseY = -1.1f;
        // config (filled in by the plugin)
        public static float TurretSens = 0.1f, TurretGain = 12f, TurretMaxSpeed = 720f, TurretPitchMin = -30f, TurretPitchMax = 135f,   // 0.25.0: only the default when the turret has no knobs (old bundle)
                             TurretLinkAccel = 360f;
        public static bool TurretInertia = true, TurretCamera = true; public static Key TurretCursorKey = Key.LeftCtrl;
        public static bool FreeCursor;
        public static Turret SeatedTurret;
        public static EntityManager CurrentEm; public static bool EmOk;
        public static bool MenuBP;   // 0.12.6: blueprint menu open (CrapMechanicsPlugin.MenuBP)
        public static string Counts() => allJoints.Count + " joints, " + motors.Count + " motors, " + turrets.Count + " turrets, " + turretsSeen.Count + " turrets in world, " + Ecs.CountChildren() + " children in world";
        static TypeIndex tiSeatSing, tiPCD, tiGT2; static EntityQuery qSeatSing, qMine; static bool seatOk, seatFailed, gt2Ok;
        static Entity camEnt; static float tCam = 9f; static int camErrors, spinErrors;
        public static int PatchesSeen;

        static void NewTurret(Entity e, string name)
        {
            turrets[Ecs.K(e)] = new Turret { block = e, name = name, state = 0 };
            ModLog.Info(name + " found: block " + e.Index + ":" + e.Version + " (turret seat)");
        }
        public static bool HasTurret(long k) => turrets.ContainsKey(k);
        /// <summary>0.22.0: active turrets (for SlipRings.cs: ring on the piece hanging from each plate).</summary>
        internal static IEnumerable<Turret> ActiveTurrets() { foreach (var t in turrets.Values) if (t.state == 2 || t.state == 3 || t.state == 5) yield return t; }   // 0.26.8: whole too (ring)
        public static bool TurretWholeUntilAimed = true;   // [Turret Seat] whole_until_aimed

        /// <summary>0.26.8: state 5 — everything in a single part. Whoever is seated and moves the aim releases the plates (it becomes the usual turret),
        /// and the first movement already counts for the aim.</summary>
        static void TurretWhole(EntityManager em, Turret t)
        {
            if (t.remoteSeated)
            {
                // 0.28.0: a player seated on another machine (ModNet.cs) — the first aim that moves releases the plates, like the mouse here
                if (System.Math.Abs(t.remoteYaw - t.yaw) + System.Math.Abs(t.remotePitch - t.pitch) < 0.5f) return;
                t.yaw = t.remoteYaw; t.pitch = t.remotePitch; LimitAim(t);
                ModLog.Info(t.name + " " + t.block.Index + ": aimed by a remote player — releasing what is on the plates");
                t.release = true; t.state = 0; t.tState = 0f;
                TurretWaiting(em, t);
                return;
            }
            if (!t.localAim || FreeCursor) return;
            bool locked = true; try { locked = Cursor.lockState == CursorLockMode.Locked; } catch (Exception) { }
            if (!locked) return;
            var d = InputKeys.MouseDelta();
            if (System.Math.Abs(d.x) + System.Math.Abs(d.y) < 0.5f) return;
            t.yaw += d.x * TurretSens; t.pitch += d.y * TurretSens; LimitAim(t);
            ModLog.Info(t.name + " " + t.block.Index + ": aiming — releasing what is on the plates");
            t.release = true; t.state = 0; t.tState = 0f;
            TurretWaiting(em, t);
        }
        /// <summary>0.26.8: blocks of each plate (whatever is glued to it and everything linked to that without going through the turret) — so the slip ring
        /// finds the right ring of each plate while everything is a single part.</summary>
        static unsafe Dictionary<string, HashSet<long>> StructureByFace(EntityManager em, Turret t, SpaceshipJoint* joints, int n)
        {
            var r = new Dictionary<string, HashSet<long>>();
            long kb = Ecs.K(t.block);
            var nbr = new Dictionary<long, List<long>>();
            for (int k = 0; k < n; k++)
            {
                var sj = &joints[k]; if (sj->_breakForce < 0f) continue;
                long ka = Ecs.K(sj->_a), kc = Ecs.K(sj->_b); if (ka == kb || kc == kb) continue;
                if (!nbr.TryGetValue(ka, out var la)) nbr[ka] = la = new List<long>(); la.Add(kc);
                if (!nbr.TryGetValue(kc, out var lc)) nbr[kc] = lc = new List<long>(); lc.Add(ka);
            }
            foreach (var v in t.nbrPlates)
            {
                string face = t.faceOf.TryGetValue(Ecs.K(v), out var f) ? f : "?";
                if (!r.TryGetValue(face, out var comp)) r[face] = comp = new HashSet<long>();
                var queue = new Queue<long>(); if (comp.Add(Ecs.K(v))) queue.Enqueue(Ecs.K(v));
                while (queue.Count > 0) { var c = queue.Dequeue(); if (!nbr.TryGetValue(c, out var ls)) continue; foreach (var x in ls) if (comp.Add(x)) queue.Enqueue(x); }
            }
            return r;
        }

        static bool PrepareSeat(EntityManager em)
        {
            if (seatOk) return true; if (seatFailed) return false;
            try
            {
                tiSeatSing = TypeManager.GetTypeIndex(Il2CppType.Of<SeatBindingsUpdateSystem.Singleton>());
                tiPCD = TypeManager.GetTypeIndex(Il2CppType.Of<PlayerControllerData>());
                var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiSeatSing); qSeatSing = em.CreateEntityQuery(a);
                var m = new Il2CppStructArray<ComponentType>(2); m[0] = ComponentType.ReadOnly(TypeManager.GetTypeIndex(Il2CppType.Of<MyPlayerController>())); m[1] = ComponentType.ReadOnly(tiPCD);
                qMine = em.CreateEntityQuery(m);
                seatOk = true; return true;
            }
            catch (Exception ex) { seatFailed = true; ModLog.Error("turret: seat/player types: " + ex.Message); return false; }
        }
        static unsafe Entity CurrentSeat(EntityManager em)
        {
            try
            {
                if (qSeatSing.CalculateEntityCountWithoutFiltering() != 1) return Entity.Null;
                var eSing = qSeatSing.GetSingletonEntity(); var s = Ecs.HasC(em, eSing, tiSeatSing) ? (SeatBindingsUpdateSystem.Singleton*)em.GetComponentDataRawRO(eSing, tiSeatSing) : null;
                return s == null ? Entity.Null : s->_currentSeat;
            }
            catch (Exception) { return Entity.Null; }
        }
        static bool IsTheBlock(EntityManager em, Entity chair, Entity block)
        {
            if (Ecs.IsNull(chair)) return false;
            var e = chair;
            for (int i = 0; i < 3 && !Ecs.IsNull(e); i++) { if (e.Index == block.Index && e.Version == block.Version) return true; e = Ecs.Parent(em, e); }
            return false;
        }

        // ================================================================ cycle (JointSystem.Step)
        static void TurretsStep(EntityManager em, float dt)
        {
            CurrentEm = em; EmOk = true;
            if (turrets.Count == 0) { SeatedTurret = null; AdjustStep(em); return; }
            if (!PrepareSeat(em)) return;
            var chair = CurrentSeat(em);
            Turret seated = null;
            bool authoritative = Multi.Authoritative(em), loopback = ModNet.Loopback && !Multi.Networked(em);   // 0.28.0
            var deadOnes = new List<long>();
            foreach (var kv in turrets)
            {
                var t = kv.Value;
                try
                {
                    if (!Ecs.Alive(em, t.block)) { UndoTurret(em, t, "block is gone"); deadOnes.Add(kv.Key); continue; }
                    t.tState += dt;
                    t.isSeated = IsTheBlock(em, chair, t.block);
                    if (t.isSeated) seated = t;
                    if (!Ecs.IsPart(em, Ecs.Parent(em, t.block))) { t.release = false; t.wholeLog = false; if (t.state != 0) { UndoTurret(em, t, "back to the garage"); t.state = 0; } t.yaw = 0f; t.pitch = 0f; t.headingOk = false; t.Qvis = quaternion.identity; t.cNbr = Entity.Null; t.lbActive = false; continue; }
                    // 0.28.0: on a client the server moves the turret — this machine sends the aim of whoever sits here and draws the seat
                    if (!authoritative) { ClientTurret(em, t, dt); continue; }
                    t.localAim = t.isSeated && !loopback;
                    if (loopback) LoopbackAim(em, t);   // [Multiplayer] test_loopback: the local mouse goes through ModNet like a remote player's
                    t.remoteSeated = ModNet.RemoteAim(em, t.block, out t.remoteYaw, out t.remotePitch);
                    if (t.remoteSeated && !t.wholeShip) { t.remoteYaw = Wrap180(t.remoteYaw); }
                    switch (t.state)
                    {
                        case 0: TurretWaiting(em, t); break;
                        case 1: TurretSeparating(em, t); break;
                        case 2: ActiveTurret(em, t, dt); break;
                        case 3: if (t.tState >= 0f) { t.state = 0; } break;
                        case 5: TurretWhole(em, t); break;   // 0.26.8
                    }
                }
                catch (Exception ex) { if (t.warnings++ < 5) ModLog.Warn(t.name + " " + t.block.Index + ": " + ex.Message); }
            }
            foreach (var k in deadOnes) turrets.Remove(k);
            if (seated != SeatedTurret)
            {
                if (seated != null)
                {
                    // 0.12.1 (Andre): sat down = the cursor vanishes right away and it already aims; Ctrl toggles from then on
                    FreeCursor = false;
                    try { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } catch (Exception) { }
                    ModLog.Info("turret seat " + seated.block.Index + ": sat down (state " + TurretName(seated) + "; aiming with the mouse, " + TurretCursorKey + " shows the cursor; input: " + InputKeys.Mode + ")");
                }
                else ModLog.Info("turret seat: stood up");
                SeatedTurret = seated;
            }
            if (seated != null && InputKeys.Pressed(TurretCursorKey)) ReleaseCursor(!FreeCursor);
            AdjustStep(em);   // 0.12.6: adjust mode (Num/) and invisible seat (version A)
        }
        static string TurretName(Turret t) => t.state == 0 ? "waiting for launch" : t.state == 1 ? "separating" : t.state == 2 ? "active" : t.state == 5 ? "WHOLE until you aim" : "WELDED";

        static void ReleaseCursor(bool free)
        {
            FreeCursor = free;
            try { if (free) { Cursor.lockState = CursorLockMode.Confined; Cursor.visible = true; } else { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } }
            catch (Exception ex) { ModLog.Warn("turret: cursor: " + ex.Message); }
            ModLog.Info("turret seat: cursor " + (free ? "FREE (aim paused)" : "hidden (aiming)"));
        }

        // ---- state 0: launched -> breaks the joints of the PLATES (everything but the base) so whatever is glued to them becomes another part (or parts)
        static unsafe void TurretWaiting(EntityManager em, Turret t)
        {
            if (t.tState < 0f) return;
            var joints = Ecs.Joints(em, out int n);
            if (joints == null) { if (t.warnings++ < 3) ModLog.Warn("SpaceshipSingleton._joints not available"); t.tState = -1f; return; }
            var plates = PlateJoints(em, t.block, joints, n, out var nbrs);
            t.partBase = Ecs.Parent(em, t.block); t.movs.Clear(); t.broken.Clear(); t.nbrPlates = nbrs;
            if (plates.Count == 0)
            {
                t.state = 2; t.tState = 0f;
                ModLog.Info(t.name + " " + t.block.Index + ": active with nothing on the plates (only the seat turns)");
                return;
            }
            var skip = new HashSet<int>(plates);
            // 0.12.11 (Andre: "each link turns along with the turret"): a structure glued to a plate AND to the turret's base no longer stays WELDED —
            // the base joints that lead to that same structure (without going through the turret) are released too, and it turns along with the aim.
            t.faceOf.Clear();
            foreach (var v in nbrs) t.faceOf[Ecs.K(v)] = FaceOf(em, t.block, v);
            var structureBase = BaseJointsOn(em, t.block, joints, n, nbrs, out var nbrsBase, out int othersBase);
            t.wholeShip = false;
            if (structureBase.Count > 0 && othersBase == 0)
            {
                // the turret's base is only in this same structure: turret + structure are one thing (the tank). Nothing is released: the whole ship
                // turns to the aim (only on the top axis, limited force) and the seat tilts only in the drawing.
                t.wholeShip = true; t.partBase = Ecs.Parent(em, t.block); t.movs.Clear();
                Ecs.Axes(em, t.block, out _, out t.top0, out t.front0); t.yaw = 0f; t.pitch = 0f; t.headingOk = false;
                t.state = 2; t.tState = 0f;
                ModLog.Info(t.name + " " + t.block.Index + ": the same structure is on the plates and the whole base — the WHOLE SHIP turns with the aim (link, top axis only, " + Ecs.Mass(em, t.partBase).ToString("F0") + " kg)");
                return;
            }
            foreach (var k in structureBase) { plates.Add(k); skip.Add(k); }
            foreach (var w in nbrsBase) { t.faceOf[Ecs.K(w)] = "base"; if (!nbrs.Contains(w)) nbrs.Add(w); }
            if (structureBase.Count > 0) ModLog.Info(t.name + " " + t.block.Index + ": structure attached to a plate and to the base — also releases " + structureBase.Count + " base joint(s) (" + nbrsBase.Count + " block(s)); it turns along (link)");
            foreach (var v in nbrs)
            {
                if (ConnectedOtherWay(em, joints, n, v, t.block, skip, out int viaN))
                {
                    if (!t.didWarnStuck) { t.didWarnStuck = true; ModLog.Warn(t.name + " " + t.block.Index + ": what is attached to the plates (block " + v.Index + ") is also attached to the ship through another path (" + viaN + " joints) — it can't turn like that: stays WELDED. Attach to the plates only what should turn with them. Retrying in 30 s."); }
                    t.state = 3; t.tState = -30f; return;
                }
            }
            t.didWarnStuck = false;
            // 0.26.0 (Andre: "it starts with everything falling and in the air it tries to fix the joints, and then everything breaks"): between breaking the game's
            // joints and the game finishing splitting the parts up to 3 s go by — and during that window whatever is on the plates is a free body and FALLS.
            // So the mod built the turret with the pieces wherever they had fallen. Here we keep the pose of each one relative to the turret;
            // HoldSeparating rewrites that pose every frame while the window lasts, so nothing gets to move out of place.
            t.poseRel.Clear();
            try
            {
                Ecs.Pose(em, t.block, out var pT0, out var rT0);
                var invT0 = math.inverse(new RigidTransform(rT0, pT0));
                foreach (var v in nbrs) { if (!Ecs.Alive(em, v)) continue; Ecs.Pose(em, v, out var pv0, out var rv0); t.poseRel[Ecs.K(v)] = math.mul(invT0, new RigidTransform(rv0, pv0)); }
            }
            catch (Exception) { t.poseRel.Clear(); }
            // 0.26.8 (Andre: "the turret seat breaks almost every time ... when still it stays whole and when I sit down and move it it
            // becomes a joint like the piston"): with nobody aiming, nothing is released — the garage already built everything as a single part. It only releases the
            // first time someone seated moves the aim (TurretWhole).
            if (TurretWholeUntilAimed && !t.release)
            {
                t.faceStructure = StructureByFace(em, t, joints, n);
                t.state = 5; t.tState = 0f;
                if (!t.wholeLog) { t.wholeLog = true; ModLog.Info(t.name + " " + t.block.Index + ": stays WHOLE (one part with what is on the plates) — it releases them the first time you sit and aim"); }
                return;
            }
            t.release = false;
            foreach (var k in plates) { var sj = &joints[k]; if (sj->_breakForce > 0f) t.broken.Add((k, sj->_a, sj->_b, sj->_breakForce)); sj->_breakForce = -1f; sj->_currentStrain = 1e9f; }
            t.state = 1; t.tState = 0f;
            ModLog.Info(t.name + " " + t.block.Index + ": releasing what is on the plates (" + plates.Count + " joint(s), " + nbrs.Count + " block(s))");
        }

        /// <summary>0.12.11: face of the turret where the neighbour glues (by the dominant side of its position on the block).</summary>
        static string FaceOf(EntityManager em, Entity block, Entity nbr)
        {
            try
            {
                Ecs.Axes(em, block, out var ex, out var ey, out var ez);
                var d = Ecs.Norm(Ecs.Sub(Ecs.Position(em, nbr), Ecs.Position(em, block)));
                float dx = Ecs.Dot(d, ex), dy = Ecs.Dot(d, ey), dz = Ecs.Dot(d, ez);
                float ax = System.Math.Abs(dx), ay = System.Math.Abs(dy), az = System.Math.Abs(dz);
                if (ax >= ay && ax >= az) return dx > 0 ? "right" : "left";
                if (ay >= az) return dy > 0 ? "top" : "base";
                return dz > 0 ? "front" : "back";
            }
            catch (Exception) { return "?"; }
        }
        /// <summary>0.12.11: joints of the turret's BASE whose neighbour belongs to the same structure as some block of the plates (linked without going
        /// through the turret itself).</summary>
        static unsafe List<int> BaseJointsOn(EntityManager em, Entity block, SpaceshipJoint* joints, int n, List<Entity> nbrsPlates, out List<Entity> nbrsBase, out int othersBase)
        {
            var r = new List<int>(); nbrsBase = new List<Entity>(); othersBase = 0;
            if (nbrsPlates.Count == 0) return r;
            long kb = Ecs.K(block);
            var nbr = new Dictionary<long, List<long>>();
            for (int k = 0; k < n; k++)
            {
                var sj = &joints[k]; if (sj->_breakForce < 0f) continue;
                long ka = Ecs.K(sj->_a), kc = Ecs.K(sj->_b); if (ka == kb || kc == kb) continue;   // without going through the turret
                if (!nbr.TryGetValue(ka, out var la)) nbr[ka] = la = new List<long>(); la.Add(kc);
                if (!nbr.TryGetValue(kc, out var lc)) nbr[kc] = lc = new List<long>(); lc.Add(ka);
            }
            var comp = new HashSet<long>(); var queue = new Queue<long>();
            foreach (var v in nbrsPlates) { long kv = Ecs.K(v); if (comp.Add(kv)) queue.Enqueue(kv); }
            while (queue.Count > 0) { var c = queue.Dequeue(); if (!nbr.TryGetValue(c, out var ls)) continue; foreach (var x in ls) if (comp.Add(x)) queue.Enqueue(x); }
            var platesK = new HashSet<long>(); foreach (var v in nbrsPlates) platesK.Add(Ecs.K(v));
            for (int k = 0; k < n; k++)
            {
                var sj = &joints[k]; if (sj->_breakForce < 0f) continue;
                bool a = Same(sj->_a, block), b = Same(sj->_b, block); if (!a && !b) continue;
                var other = a ? sj->_b : sj->_a; long ko = Ecs.K(other);
                if (platesK.Contains(ko)) continue;   // plate joint (already included)
                if (!comp.Contains(ko)) { othersBase++; continue; }   // structure that is only on the base (holds the turret)
                r.Add(k); if (!nbrsBase.Contains(other)) nbrsBase.Add(other);
            }
            return r;
        }
        /// <summary>Game joints between the block and whatever is glued to the plates = all of them, except the ones of the bottom face (the base).</summary>
        static unsafe List<int> PlateJoints(EntityManager em, Entity block, SpaceshipJoint* joints, int n, out List<Entity> nbrs)
        {
            var r = new List<int>(); nbrs = new List<Entity>(); var seenSet = new HashSet<long>();
            Ecs.Axes(em, block, out _, out var ey, out _); var pb = Ecs.Position(em, block);
            for (int k = 0; k < n; k++)
            {
                var sj = &joints[k];
                bool a = Same(sj->_a, block), b = Same(sj->_b, block);
                if (!a && !b) continue;
                if (sj->_breakForce < 0f) continue;
                var other = a ? sj->_b : sj->_a;
                float3 po; try { if (!Ecs.Alive(em, other)) continue; po = Ecs.Position(em, other); } catch (Exception) { continue; }
                var d = Ecs.Sub(po, pb); float comp = Ecs.Len(d); if (comp < 1e-4f) continue;
                if (Ecs.Dot(d, ey) < -0.9f) continue;   // below the block (the base glues underneath: the neighbour's center >= 1 m below)
                r.Add(k); if (seenSet.Add(Ecs.K(other))) nbrs.Add(other);
            }
            return r;
        }

        static unsafe void RestoreBreakForceTurret(EntityManager em, Turret t)
        {
            if (t.broken.Count == 0) return;
            try
            {
                var joints = Ecs.Joints(em, out int n); int ok = 0;
                if (joints != null)
                    foreach (var q in t.broken)
                    {
                        if (q.idx < 0 || q.idx >= n) continue;
                        var sj = &joints[q.idx];
                        if (sj->_a.Index != q.a.Index || sj->_b.Index != q.b.Index || sj->_breakForce != -1f) continue;
                        sj->_breakForce = q.breakForce; sj->_currentStrain = 0f; ok++;
                    }
                if (DetailedLog) ModLog.Info(t.name + " " + t.block.Index + ": _breakForce restored on " + ok + "/" + t.broken.Count + " joint(s)");
            }
            catch (Exception ex) { if (t.warnings++ < 5) ModLog.Warn(t.name + " " + t.block.Index + ": restoring _breakForce: " + ex.Message); }
            t.broken.Clear();
        }

        // ---- state 1: waits for whatever is on the plates to become a part (or parts) of its own, attaches each one to the pivot
        /// <summary>0.26.0: while the game hasn't finished splitting the parts, the plate pieces that already left stay HELD at the pose they
        /// had when the mod released them (relative to the turret), with the ship's own velocity. Without this they fall during the window and the
        /// turret is born built on top of pieces out of place — or the game breaks whatever is in the way.</summary>
        static unsafe void HoldSeparating(EntityManager em, Turret t)
        {
            if (t.poseRel.Count == 0) return;
            var pBase = Ecs.Parent(em, t.block); if (!Ecs.IsPart(em, pBase)) return;
            float3 vLin = float3.zero, vAng = float3.zero;
            var vb = (Ecs.HasC(em, pBase, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(pBase, Ecs.tiVel) : null);
            if (vb != null) { vLin = vb->Linear; vAng = vb->Angular; }
            Ecs.Pose(em, t.block, out var pT, out var rT);
            var worldT = new RigidTransform(rT, pT);
            foreach (var v in t.nbrPlates)
            {
                if (!Ecs.Alive(em, v) || !t.poseRel.TryGetValue(Ecs.K(v), out var rel)) continue;
                var part = Ecs.Parent(em, v);
                if (!Ecs.IsPart(em, part) || part.Index == pBase.Index) continue;   // not separated yet: the game still rules it
                try
                {
                    var lt = Ecs.LT(em, v);   // the block inside its part
                    var target = math.mul(math.mul(worldT, rel), math.inverse(new RigidTransform(lt.Rotation, lt.Position)));
                    var pp = (Ecs.HasC(em, part, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(part, Ecs.tiLT) : null);
                    if (pp != null) { pp->Position = target.pos; pp->Rotation = target.rot; }
                    var pv = (Ecs.HasC(em, part, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRW(part, Ecs.tiVel) : null);
                    if (pv != null) { pv->Linear = vLin; pv->Angular = vAng; }
                }
                catch (Exception) { }
            }
        }

        static void TurretSeparating(EntityManager em, Turret t)
        {
            try { RequestZeroGrav(); HoldSeparating(em, t); } catch (Exception) { }
            var pBase = Ecs.Parent(em, t.block);
            bool all = true;
            foreach (var v in t.nbrPlates) { if (!Ecs.Alive(em, v)) continue; var pv = Ecs.Parent(em, v); if (!Ecs.IsPart(em, pv) || pv.Index == pBase.Index) { all = false; break; } }
            if (all)
            {
                RestoreBreakForceTurret(em, t);
                t.partBase = pBase; t.splitFailures = 0;
                if (CreateMovs(em, t, false)) { t.state = 2; t.tState = 0f; }
                else { t.state = 0; t.tState = -5f; }
                return;
            }
            if (t.tState > 3f)
            {
                RestoreBreakForceTurret(em, t);
                t.splitFailures++; bool giveUp = t.splitFailures >= 3;
                ModLog.Warn(t.name + " " + t.block.Index + ": what is on the plates did not separate from the ship within 3 s — it must be attached to the ship through another path; " + (giveUp ? "3 attempts, pausing for 2 min" : "retrying"));
                t.state = 3; t.tState = giveUp ? -120f : -2f; if (giveUp) t.splitFailures = 0;
            }
        }

        static quaternion GarageRot(EntityManager em, Entity e, out bool ok)
        {
            ok = false;
            try
            {
                if (!gt2Ok) { gt2Ok = true; tiGT2 = TypeManager.GetTypeIndex(Il2CppType.Of<GarageTransform>()); }
                unsafe { if (em.HasComponent(e, ComponentType.ReadOnly(tiGT2))) { ok = true; return ((Ecs.HasC(em, e, tiGT2) ? (RigidTransform*)em.GetComponentDataRawRO(e, tiGT2) : null))->rot; } }
            }
            catch (Exception) { }
            return quaternion.identity;
        }

        /// <summary>One TurretMov per distinct part among the plates' blocks: ball-and-socket at the pivot with the ship's part.</summary>
        static unsafe bool CreateMovs(EntityManager em, Turret t, bool recreating)
        {
            try
            {
                foreach (var m in t.movs) { try { if (Ecs.Alive(em, m.joint)) em.DestroyEntity(m.joint); } catch (Exception) { } RestoreInertia(em, m); }
                t.movs.Clear();
                Ecs.Pose(em, t.block, out var posB, out var rotB);
                var qgB = GarageRot(em, t.block, out bool okB);
                var pivot = Ecs.Add(posB, Ecs.Rot(rotB, TurretPivot));
                var byPart = new Dictionary<long, TurretMov>();
                foreach (var v in t.nbrPlates)
                {
                    if (!Ecs.Alive(em, v)) continue;
                    var pv = Ecs.Parent(em, v); if (!Ecs.IsPart(em, pv) || pv.Index == t.partBase.Index) continue;
                    long kp = Ecs.K(pv);
                    string face = t.faceOf.TryGetValue(Ecs.K(v), out var fc) ? fc : "?";
                    if (byPart.TryGetValue(kp, out var mj)) { if (!mj.faces.Contains(face)) mj.faces += (mj.faces.Length > 0 ? "+" : "") + face; continue; }
                    var m = new TurretMov { nbr = v, part = pv, faces = face };
                    var qgV = GarageRot(em, v, out bool okV);
                    if (okB && okV) m.Rn0 = Ecs.QMul(Ecs.Inv(qgB), qgV);
                    else { Ecs.Pose(em, v, out _, out var rotV); m.Rn0 = Ecs.QMul(Ecs.QMul(Ecs.Inv(t.Qvis), Ecs.Inv(rotB)), rotV); }
                    var fa = Ecs.Frame(em, pv, pivot, Ecs.UY, Ecs.UX); var fb = Ecs.Frame(em, t.partBase, pivot, Ecs.UY, Ecs.UX);
                    var joint = PhysicsJoint.CreateBallAndSocket(fa.Position, fb.Position);
                    var pair = new PhysicsConstrainedBodyPair(); pair.Entities = new EntityPair { EntityA = pv, EntityB = t.partBase }; pair.EnableCollision = 0;
                    var kinds = new Il2CppStructArray<ComponentType>(3);
                    kinds[0] = ComponentType.ReadWrite(Ecs.tiPar); kinds[1] = ComponentType.ReadWrite(Ecs.tiJoint); kinds[2] = ComponentType.ReadWrite(Ecs.tiPWI);
                    var e = em.CreateEntity(kinds);
                    *(Ecs.HasC(em, e, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(e, Ecs.tiPar) : null) = pair;
                    *(Ecs.HasC(em, e, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(e, Ecs.tiJoint) : null) = joint;
                    m.joint = e;
                    try
                    {
                        var va = (Ecs.HasC(em, t.partBase, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(t.partBase, Ecs.tiVel) : null); var vb = (Ecs.HasC(em, pv, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRW(pv, Ecs.tiVel) : null);
                        if (va != null && vb != null && !recreating) { vb->Linear = va->Linear; vb->Angular = float3.zero; }
                    }
                    catch (Exception) { }
                    byPart[kp] = m; t.movs.Add(m);
                    ModLog.Info(t.name + " " + t.block.Index + ": " + (recreating ? "recreated" : "attached to the pivot") + " — part " + pv.Index + " (" + Ecs.Mass(em, pv).ToString("F0") + " kg, block " + v.Index + ") on ship " + t.partBase.Index + " (" + Ecs.Mass(em, t.partBase).ToString("F0") + " kg), joint " + e.Index + (okB && okV ? "" : " (no garage: current pose)"));
                }
                // 0.12.11 (Andre: "each link turns along with the turret, it just mustn't explode"): the piece attached to 2+ faces is a LINK — it turns with the
                // aim like the others, but with normal inertia and limited acceleration (infinite inertia + imposed velocity against the ground
                // was what made the tank fly off when the tyre touched)
                foreach (var m in t.movs)
                {
                    m.link = m.faces.Contains("+");
                    if (m.link && !recreating) ModLog.Info(t.name + " " + t.block.Index + ": part " + m.part.Index + " attached at " + m.faces + " = LINK (normal inertia, acceleration up to " + TurretLinkAccel.ToString("F0") + " degrees/s2)");
                }
                return true;
            }
            catch (Exception ex) { ModLog.Error(t.name + " " + t.block.Index + ": creating joints failed: " + ex); return false; }
        }

        static void UndoTurret(EntityManager em, Turret t, string reason)
        {
            foreach (var m in t.movs) { try { if (Ecs.Alive(em, m.joint)) em.DestroyEntity(m.joint); } catch (Exception) { } RestoreInertia(em, m); }
            if (t.movs.Count > 0) ModLog.Info(t.name + " " + t.block.Index + ": joints removed (" + reason + ")");
            t.movs.Clear(); t.partBase = Entity.Null;
        }
        static unsafe void RestoreInertia(EntityManager em, TurretMov m)
        {
            try { if (m.savedInertia && Ecs.IsPart(em, m.part)) { var pm = (Ecs.HasC(em, m.part, Ecs.tiMass) ? (PhysicsMass*)em.GetComponentDataRawRW(m.part, Ecs.tiMass) : null); if (pm != null) pm->InverseInertia = m.originalInertia; } } catch (Exception) { }
            m.savedInertia = false;
        }

        // ---- state 2: aim + servo of the pieces + rotation of the drawing
        static unsafe void ActiveTurret(EntityManager em, Turret t, float dt)
        {
            var pBase = Ecs.Parent(em, t.block);
            // did the game rebuild the parts? relink whoever is in a part of its own; if any went back to the ship's part, start over
            bool redo = pBase.Index != t.partBase.Index;
            foreach (var m in t.movs)
            {
                if (!Ecs.Alive(em, m.nbr)) continue;
                var pv = Ecs.Parent(em, m.nbr);
                if (!Ecs.Alive(em, m.joint) || !Ecs.IsPart(em, m.part) || pv.Index != m.part.Index) redo = true;
                if (pv.Index == pBase.Index) { UndoTurret(em, t, "what was on the plate stuck to the ship again"); t.state = 0; t.tState = -1f; return; }
            }
            if (redo) { t.partBase = pBase; if (!CreateMovs(em, t, true)) { UndoTurret(em, t, "recreating failed"); t.state = 0; t.tState = -1f; return; } }
            // aim: this machine's mouse, or (0.28.0) the aim a player seated on another machine sent
            if (t.remoteSeated && !t.localAim) { t.yaw = t.remoteYaw; t.pitch = t.remotePitch; }
            else if (t.localAim && !FreeCursor)
            {
                bool locked = true; try { locked = Cursor.lockState == CursorLockMode.Locked; } catch (Exception) { }
                if (locked)
                {
                    var d = InputKeys.MouseDelta();
                    // 0.12.6: going past 90 degrees up the view turns upside down -> the sideways mouse inverts to stay 'natural'
                    float sx = System.Math.Abs(t.pitch) > 90f ? -1f : 1f;
                    t.yaw += sx * d.x * TurretSens; t.pitch += d.y * TurretSens;
                    // 0.19.6 (Andre: "if I turn a lot to the right it starts turning left"): in whole-ship mode the aim does NOT wrap at
                    // +-180 (log: aim 159 -> -75 and the ship at 97 took the "shortest" way, -172 = left). Aim and heading are continuous.
                    if (!t.wholeShip) { if (t.yaw > 180f) t.yaw -= 360f; else if (t.yaw < -180f) t.yaw += 360f; }
                }
            }
            LimitAim(t);
            if (t.wholeShip) { WholeShipTurns(em, t, dt); return; }
            var QTarget = Ecs.QMul(QuatAxis(Ecs.UY, t.yaw * (float)(System.Math.PI / 180.0)), QuatAxis(Ecs.UX, -t.pitch * (float)(System.Math.PI / 180.0)));
            Ecs.Pose(em, t.block, out _, out var rotB);
            var worldTarget = Ecs.QMul(rotB, QTarget);
            // the ship's angular velocity (in the world), to add: the turret turns relative to the ship
            float3 wShip = float3.zero;
            try
            {
                var ltN = (Ecs.HasC(em, t.partBase, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(t.partBase, Ecs.tiLT) : null); var pmN = (Ecs.HasC(em, t.partBase, Ecs.tiMass) ? (PhysicsMass*)em.GetComponentDataRawRO(t.partBase, Ecs.tiMass) : null); var pvN = (Ecs.HasC(em, t.partBase, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(t.partBase, Ecs.tiVel) : null);
                if (ltN != null && pmN != null && pvN != null) wShip = Ecs.Rot(Ecs.QMul(ltN->Rotation, pmN->Transform.rot), pvN->Angular);
            }
            catch (Exception) { }
            float maxError = 0f; bool firstOne = true;
            foreach (var m in t.movs)
            {
                if (!Ecs.Alive(em, m.nbr) || !Ecs.IsPart(em, m.part)) continue;
                // 0.24.0-mp: a piece the server dictates — neither inertia nor angular velocity from here (the turn of the DRAWING and the camera
                // continue, they are local: it's what makes the player see the aim)
                if (!Multi.MyPart(em, m.part)) { if (m.savedInertia) RestoreInertia(em, m); continue; }
                var pm = (Ecs.HasC(em, m.part, Ecs.tiMass) ? (PhysicsMass*)em.GetComponentDataRawRW(m.part, Ecs.tiMass) : null);
                if (pm != null && TurretInertia && !m.link) { if (!m.savedInertia) { m.originalInertia = pm->InverseInertia; m.savedInertia = true; } pm->InverseInertia = float3.zero; }
                if (m.link && m.savedInertia) RestoreInertia(em, m);
                Ecs.Pose(em, m.nbr, out _, out var rotV);
                var targetV = Ecs.QMul(worldTarget, m.Rn0);
                var qe = Ecs.QMul(targetV, Ecs.Inv(rotV));
                if (qe.value.w < 0f) { qe.value.x = -qe.value.x; qe.value.y = -qe.value.y; qe.value.z = -qe.value.z; qe.value.w = -qe.value.w; }
                float w = System.Math.Min(1f, qe.value.w); float ang = 2f * (float)System.Math.Acos(w); float s = (float)System.Math.Sqrt(System.Math.Max(0f, 1f - w * w));
                float3 axis = s < 1e-6f ? Ecs.UY : new float3 { x = qe.value.x / s, y = qe.value.y / s, z = qe.value.z / s };
                float vel = System.Math.Min(TurretMaxSpeed * (float)(System.Math.PI / 180.0), TurretGain * ang);
                var omega = Ecs.Add(wShip, Ecs.Mul(axis, vel));
                var lt = (Ecs.HasC(em, m.part, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(m.part, Ecs.tiLT) : null);
                var pv = (Ecs.HasC(em, m.part, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRW(m.part, Ecs.tiVel) : null);
                if (lt != null && pv != null && pm != null)
                {
                    var qMass = Ecs.QMul(lt->Rotation, pm->Transform.rot);
                    if (m.link)
                    {
                        // link: changes the angular velocity little by little (limited acceleration) — the ground/wheels can push without turning into an explosion
                        var current = Ecs.Rot(qMass, pv->Angular); var diff = Ecs.Sub(omega, current);
                        float lim = TurretLinkAccel * (float)(System.Math.PI / 180.0) * dt, cd = Ecs.Len(diff);
                        if (cd > lim && cd > 1e-6f) diff = Ecs.Mul(diff, lim / cd);
                        omega = Ecs.Add(current, diff);
                    }
                    pv->Angular = Ecs.Rot(Ecs.Inv(qMass), omega);
                }
                maxError = System.Math.Max(maxError, ang);
                // the drawing follows the REAL pose of the first piece (stays together with whatever is glued)
                if (firstOne) { firstOne = false; t.Qvis = Ecs.QMul(Ecs.QMul(Ecs.Inv(rotB), rotV), Ecs.Inv(m.Rn0)); }
            }
            if (firstOne) t.Qvis = QTarget;   // nothing on the plates: the drawing goes straight to the aim
            t.tLog += dt;
            if (DetailedLog && t.tLog >= 0.5f && System.Math.Abs(maxError * 57.2958f - t.errLog) >= 5f)
            {
                t.tLog = 0f; t.errLog = maxError * 57.2958f;
                ModLog.Info(t.name + " " + t.block.Index + ": aim yaw " + t.yaw.ToString("F0") + " pitch " + t.pitch.ToString("F0") + ", error " + t.errLog.ToString("F1") + " degrees (" + t.movs.Count + " part(s))");
            }
        }

        /// <summary>0.12.11: turret + structure are one thing (base and plates in the same structure) — the WHOLE SHIP turns to the aim (yaw, on the
        /// "top" axis from when it was turned on), with limited acceleration; the seat tilts (pitch) only in the drawing. The view follows the real ship.</summary>
        static unsafe void WholeShipTurns(EntityManager em, Turret t, float dt)
        {
            var ship = Ecs.Parent(em, t.block); if (!Ecs.IsPart(em, ship)) return;
            t.partBase = ship;
            Ecs.Axes(em, t.block, out _, out _, out var ez);
            var up = Ecs.Norm(t.top0); var f0 = Ecs.Norm(t.front0); var r0 = Ecs.Norm(Ecs.Cross(up, f0));
            var fh = Ecs.Sub(ez, Ecs.Mul(up, Ecs.Dot(ez, up)));
            float currentG = (float)(System.Math.Atan2(Ecs.Dot(fh, r0), Ecs.Dot(fh, f0)) * 180.0 / System.Math.PI);
            if (!t.headingOk) { t.headingOk = true; t.headingCont = currentG; t.headingLast = currentG; }
            else { float dR = currentG - t.headingLast; while (dR > 180f) dR -= 360f; while (dR < -180f) dR += 360f; t.headingCont += dR; t.headingLast = currentG; }
            float error = t.yaw - t.headingCont;   // continuous: turned 300 degrees to the right = the ship turns 300 degrees to the right
            if (error > 720f || error < -720f) { t.yaw = t.headingCont; error = 0f; }   // (safety: never accumulates full turns)
            float velTarget = System.Math.Max(-TurretMaxSpeed, System.Math.Min(TurretMaxSpeed, TurretGain * error)) * (float)(System.Math.PI / 180.0);
            try
            {
                var lt = (Ecs.HasC(em, ship, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(ship, Ecs.tiLT) : null);
                var pm = (Ecs.HasC(em, ship, Ecs.tiMass) ? (PhysicsMass*)em.GetComponentDataRawRO(ship, Ecs.tiMass) : null);
                var pv = (Ecs.HasC(em, ship, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRW(ship, Ecs.tiVel) : null);
                if (lt != null && pm != null && pv != null)
                {
                    var qMass = Ecs.QMul(lt->Rotation, pm->Transform.rot);
                    var w = Ecs.Rot(qMass, pv->Angular); float wUp = Ecs.Dot(w, up);
                    float lim = TurretLinkAccel * (float)(System.Math.PI / 180.0) * dt;
                    float newVal = wUp + System.Math.Max(-lim, System.Math.Min(lim, velTarget - wUp));
                    w = Ecs.Add(w, Ecs.Mul(up, newVal - wUp));
                    pv->Angular = Ecs.Rot(Ecs.Inv(qMass), w);
                }
            }
            catch (Exception ex) { if (t.warnings++ < 3) ModLog.Warn(t.name + " " + t.block.Index + ": whole ship turning: " + ex.Message); }
            t.Qvis = QuatAxis(Ecs.UX, -t.pitch * (float)(System.Math.PI / 180.0));   // only the tilt, in the drawing (the yaw is the real ship)
            t.tLog += dt;
            if (DetailedLog && t.tLog >= 0.5f && System.Math.Abs(error - t.errLog) >= 5f) { t.tLog = 0f; t.errLog = error; ModLog.Info(t.name + " " + t.block.Index + ": whole ship — aim yaw " + t.yaw.ToString("F0") + ", ship at " + t.headingCont.ToString("F0") + " (error " + error.ToString("F0") + " degrees)"); }
        }
        /// <summary>0.25.0: limits from the base's 3 knobs (yaw / pitch up / pitch down) — every frame, not only when the mouse
        /// moves: turning a knob down with the aim outside the limit brings the aim back at the turret's normal speed. In whole-ship
        /// mode the yaw is counted from the heading at the time it was turned on.</summary>
        static void LimitAim(Turret t) => LimitAim(t, ref t.yaw, ref t.pitch);
        static void LimitAim(Turret t, ref float aimYaw, ref float aimPitch)   // 0.28.0: also for an aim kept apart (test_loopback)
        {
            float yaw = Knobs.TurretYawMaxDeg, top = TurretPitchMax, low = -TurretPitchMin;
            Knobs.TurretLimits(t.block, ref yaw, ref top, ref low);
            if (yaw < Knobs.TurretYawMaxDeg - 0.5f) { if (aimYaw > yaw) aimYaw = yaw; else if (aimYaw < -yaw) aimYaw = -yaw; }
            if (aimPitch > top) aimPitch = top; else if (aimPitch < -low) aimPitch = -low;
        }
        static quaternion QuatAxis(float3 axis, float rad)
        {
            float s = (float)System.Math.Sin(rad / 2), c = (float)System.Math.Cos(rad / 2);
            return new quaternion { value = new float4 { x = axis.x * s, y = axis.y * s, z = axis.z * s, w = c } };
        }

        // ================================================================ the drawing that turns (prefix/postfix of TransformSystemGroup)
        /// <summary>Children of the block that turn: all but the base's body/paintable (at the origin) and the output plug.</summary>
        // 0.12.4: the game's SCTick_Seat writes the Upper's LocalTransform (y -0.775 = height 0 of the lever, which is hidden) and ignores
        // the prefab's _position -> the seat sank into the base. Here (TransformSystemGroup prefix) the Upper goes to TurretUpper
        // (= modelos_v2.TURRET_UPPER); the Upper Paintable is its child and goes along.
        public static readonly float3 TurretUpper = new float3 { x = 0f, y = -0.85f, z = 0.02f };   // = modelos_v2.TURRET_UPPER (0.12.9: Andre's adjustment went into the model)
        public static readonly float3 TurretPedestal = new float3 { x = 0f, y = 0.0213f, z = 0f };   // = modelos_v2.TURRET_PEDESTAL (0.12.9: plate + rod follow the adjustment)
        static readonly Dictionary<long, Entity> pedestalOf = new Dictionary<long, Entity>(); static readonly Dictionary<long, float3> pedestalOff = new Dictionary<long, float3>();
        public static readonly float3 TurretMark = new float3 { x = 0.0137f, y = 0.0271f, z = 0.0419f };   // = modelos_v2.TURRET_MARK
        static TypeIndex tiSCSeat; static bool scSeatOk, scSeatFailed; static EntityQuery qSeats; static float tSeats = 99f;
        static readonly List<Entity> turretsSeen = new List<Entity>(); static readonly HashSet<long> heightLogged = new HashSet<long>();
        /// <summary>Is the block a turret seat of ours? (it has the "Mark" renderer at TurretMark — works in the garage, where SCPrefab doesn't show up)</summary>
        static bool IsTurret(EntityManager em, Entity e)
        {
            foreach (var f in Ecs.Children(em, e)) { try { if (Ecs.Dist(Ecs.LT(em, f).Position, TurretMark) < 0.002f) return true; } catch (Exception) { } }
            return false;
        }
        static unsafe void SeatHeights(EntityManager em)
        {
            try
            {
                if (!scSeatOk)
                {
                    if (scSeatFailed) return;
                    try
                    {
                        tiSCSeat = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeSeat>());
                        var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiSCSeat); qSeats = em.CreateEntityQuery(a);
                        scSeatOk = true;
                    }
                    catch (Exception ex) { scSeatFailed = true; ModLog.Warn("turret: SCTypeSeat not available (" + ex.Message + ") — the seat stays at the game's height"); return; }
                }
                tSeats += UnityEngine.Time.unscaledDeltaTime;
                if (tSeats >= 0.5f)
                {
                    tSeats = 0f; turretsSeen.Clear();
                    foreach (var e in Ecs.QueryList(qSeats)) if (IsTurret(em, e)) turretsSeen.Add(e);
                }
                foreach (var e in turretsSeen)
                {
                    if (!Ecs.HasC(em, e, tiSCSeat)) continue;
                    var up = *(Entity*)em.GetComponentDataRawRO(e, tiSCSeat);   // SCTypeSeat._upperTransform
                    if (!Ecs.HasC(em, up, Ecs.tiLT)) continue;
                    var lt = (LocalTransform*)em.GetComponentDataRawRW(up, Ecs.tiLT);
                    if (heightLogged.Add(Ecs.K(e))) ModLog.Info("turret seat " + e.Index + ": seat Upper (entity " + up.Index + ") was at " + Ecs.F3(lt->Position) + " -> " + Ecs.F3(Ecs.Add(TurretUpper, SeatOff)));
                    lt->Position = Ecs.Add(TurretUpper, SeatOff);   // 0.12.6: + seat adjustment (adjust mode, Num/)
                    ApplyPlayer(em, e, up, lt->Position);
                    MovePedestal(em, e);
                }
            }
            catch (Exception ex) { if (heightErrors++ < 3) ModLog.Warn("turret: seat height: " + ex.Message); }
        }
        static int heightErrors;

        /// <summary>0.12.9 (Andre: "this little foot stays inside the model"): the plate and the rod under the seat are a renderer of their own (Pedestal)
        /// that moves along with the seat adjustment. Runs before the drawing's turn (the turn saves/restores the LocalTransform with the adjustment).</summary>
        static unsafe void MovePedestal(EntityManager em, Entity block)
        {
            long kb = Ecs.K(block);
            pedestalOff.TryGetValue(kb, out var offBefore);
            if (!pedestalOf.TryGetValue(kb, out var ped) || !Ecs.Alive(em, ped) || !Ecs.HasC(em, ped, Ecs.tiLT))
            {
                ped = Entity.Null;
                foreach (var f in Ecs.Children(em, block))
                {
                    if (!Ecs.HasC(em, f, Ecs.tiLT)) continue;
                    var p = Ecs.LT(em, f).Position;
                    if (Ecs.Dist(p, Ecs.Add(TurretPedestal, offBefore)) < 0.002f || Ecs.Dist(p, TurretPedestal) < 0.002f) { ped = f; break; }
                }
                if (Ecs.IsNull(ped)) return;   // old blueprint (without Pedestal)
                pedestalOf[kb] = ped;
                if (pedestalLogged.Add(kb)) ModLog.Info("turret seat " + block.Index + ": pedestal (entity " + ped.Index + ") follows the seat offset");
            }
            var lt = (LocalTransform*)em.GetComponentDataRawRW(ped, Ecs.tiLT); if (lt == null) return;
            lt->Position = Ecs.Add(TurretPedestal, SeatOff);
            pedestalOff[kb] = SeatOff;
        }
        static readonly HashSet<long> pedestalLogged = new HashSet<long>();

        static unsafe void FindMovables(EntityManager em, Turret t)
        {
            var l = new List<Entity>();
            foreach (var f in Ecs.Children(em, t.block))
            {
                var p = Ecs.LT(em, f).Position;
                if (Ecs.Len(p) < 0.003f) continue;                              // base (Renderer / Paintable / collider at the origin)
                if (p.y <= TurretBaseY) continue;                               // base: port plugs (y -1.1875) and bottom fit
                l.Add(f);
            }
            bool first = t.movables == null;
            t.movables = l.ToArray(); t.saved = new LocalTransform[t.movables.Length];
            if (first) ModLog.Info(t.name + " " + t.block.Index + ": " + t.movables.Length + " renderer(s) that turn");
        }
        /// <summary>Before TransformSystemGroup: turns the drawing of each active turret around the pivot (and the camera of whoever is seated).</summary>
        public static unsafe void TurretBeforeTransform()
        {
            if (!EmOk || MenuBP) return;   // 0.12.6: blueprint menu open -> nothing
            var em = CurrentEm;
            SeatHeights(em);   // 0.12.5: every turret in the world (garage too), before turning
            ReplayBridge.Rotated.Clear();
            if (turrets.Count == 0) return;
            foreach (var t in turrets.Values)
            {
                t.rotated = false;
                if (t.state != 2 || !Ecs.Alive(em, t.block)) continue;
                var q = t.Qvis;
                if (System.Math.Abs(q.value.w) > 0.99999f) continue;   // at rest
                try
                {
                    t.tMovables += UnityEngine.Time.unscaledDeltaTime;
                    if (t.movables == null || t.tMovables > 5f) { t.tMovables = 0f; FindMovables(em, t); }
                    if (!Ecs.Alive(em, t.block)) continue;
                    for (int i = 0; i < t.movables.Length; i++)
                    {
                        var e = t.movables[i];
                        if (!Ecs.Alive(em, e) || !em.HasComponent(e, ComponentType.ReadOnly(Ecs.tiLT))) { t.saved[i].Scale = -1f; t.tMovables = 99f; continue; }   // 0.12.2: piece vanished (blueprint swapped) — the crash came from here
                        var lt = (Ecs.HasC(em, e, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(e, Ecs.tiLT) : null); if (lt == null) { t.saved[i].Scale = -1f; continue; }
                        t.saved[i] = *lt;
                        var rel = Ecs.Sub(lt->Position, TurretPivot);
                        lt->Position = Ecs.Add(TurretPivot, Ecs.Rot(q, rel));
                        lt->Rotation = Ecs.QMul(q, lt->Rotation);
                        ReplayBridge.Rotated.Add(Ecs.K(e));
                    }
                    t.rotated = true;
                }
                catch (Exception ex) { if (spinErrors++ < 3) ModLog.Warn("turret: turning visuals: " + ex.Message); }
            }
            PatchesSeen |= 1;
            RotatePlayer(em);   // 0.12.7: cockpit mode — the character (and the camera, its child) turns along with the drawing
            TurretCameraLT();
        }

        // ---- 0.12.7 (Andre): COCKPIT mode. The seat and the rings turn only in the drawing (around the pivot) and the character stayed still where the game
        // puts it — looking far up, the seat went through the camera. Now the character's (root) LocalTransform also turns around the
        // pivot (in the world) before TransformSystemGroup and comes back in the postfix: the camera (its child) goes along, nothing crosses the view, and
        // from outside (block camera, Replay) you show up sitting on the turning seat. Physics and the game keep seeing the character in place.
        static Entity playerRotated; static LocalTransform playerSaved; static bool playerHasSaved, playerParentLogged;
        static unsafe void RotatePlayer(EntityManager em)
        {
            playerHasSaved = false;
            var t = SeatedTurret;
            if (SeatMode == 2 || t == null || !t.rotated || t.state != 2 || !Ecs.Alive(em, t.block)) return;   // 0.12.8: cockpit and A (in A the camera goes to ViewA)
            try
            {
                var l = Ecs.QueryList(qMine); if (l.Count == 0) return;
                var j = l[0];
                if (!Ecs.HasC(em, j, Ecs.tiLT)) return;
                var parent = Ecs.Parent(em, j);
                if (!Ecs.IsNull(parent)) { if (!playerParentLogged) { playerParentLogged = true; ModLog.Info("turret seat: the seated character has a parent (entity " + parent.Index + ") — not turning it (if the parent is the seat it already turns along)"); } return; }
                Ecs.Pose(em, t.block, out var posB, out var rotB);
                var Rw = Ecs.QMul(Ecs.QMul(rotB, t.Qvis), Ecs.Inv(rotB));
                var Pw = Ecs.Add(posB, Ecs.Rot(rotB, TurretPivot));
                var lt = (LocalTransform*)em.GetComponentDataRawRW(j, Ecs.tiLT);
                playerSaved = *lt; playerRotated = j; playerHasSaved = true;
                lt->Position = Ecs.Add(Pw, Ecs.Rot(Rw, Ecs.Sub(lt->Position, Pw)));
                lt->Rotation = Ecs.QMul(Rw, lt->Rotation);
                PatchesSeen |= 8;
            }
            catch (Exception ex) { if (spinErrors++ < 3) ModLog.Warn("turret: turning the character: " + ex.Message); }
        }
        static unsafe void UnrotatePlayer(EntityManager em)
        {
            if (!playerHasSaved) return; playerHasSaved = false;
            try { if (Ecs.HasC(em, playerRotated, Ecs.tiLT)) *(LocalTransform*)em.GetComponentDataRawRW(playerRotated, Ecs.tiLT) = playerSaved; } catch (Exception) { }
        }
        /// <summary>After TransformSystemGroup: undoes it (the game — seat height, triggers — keeps seeing everything in place).</summary>
        public static unsafe void TurretAfterTransform()
        {
            if (!EmOk || turrets.Count == 0) return;
            var em = CurrentEm;
            UnrotatePlayer(em);
            foreach (var t in turrets.Values)
            {
                if (!t.rotated || t.movables == null) continue;
                t.rotated = false;
                for (int i = 0; i < t.movables.Length; i++)
                {
                    if (t.saved[i].Scale < 0f) continue;
                    try { if (!Ecs.Alive(em, t.movables[i])) continue; var lt = (Ecs.HasC(em, t.movables[i], Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(t.movables[i], Ecs.tiLT) : null); if (lt != null) *lt = t.saved[i]; } catch (Exception) { }
                }
            }
        }

        // ================================================================ camera: the view turns along with the aim
        static bool ViewRotation(EntityManager em, out quaternion r)
        {
            r = quaternion.identity;
            var t = SeatedTurret;
            if (!TurretCamera || t == null || t.state != 2 || !Ecs.Alive(em, t.block)) return false;
            try { Ecs.Pose(em, t.block, out _, out var rotB); r = Ecs.QMul(rotB, t.Qvis); return true; } catch (Exception) { return false; }
        }
        static unsafe Entity PlayerCamera(EntityManager em)
        {
            try
            {
                var l = Ecs.QueryList(qMine); if (l.Count == 0) return Entity.Null;
                var b = (Ecs.HasC(em, l[0], tiPCD) ? (byte*)em.GetComponentDataRawRO(l[0], tiPCD) : null); if (b == null) return Entity.Null;
                return *(Entity*)(b + Layout.PlayerCamera);   // PlayerControllerData._cameraEntity
            }
            catch (Exception) { return Entity.Null; }
        }
        static bool Camera(EntityManager em, out Entity cam)
        {
            tCam += UnityEngine.Time.unscaledDeltaTime;
            if (Ecs.IsNull(camEnt) || !Ecs.Alive(em, camEnt) || tCam > 2f) { tCam = 0f; camEnt = PlayerCamera(em); }
            cam = camEnt; return !Ecs.IsNull(cam) && Ecs.Alive(em, cam);
        }
        public static unsafe void TurretCameraLT()
        {
            if (!EmOk || SeatedTurret == null) return;   // 0.12.2: with the free cursor the view stays the seat's
            var em = CurrentEm;
            try
            {
                if (!ViewRotation(em, out var r) || !Camera(em, out var cam) || !em.HasComponent(cam, ComponentType.ReadOnly(Ecs.tiLT))) return;
                var rp = quaternion.identity; var e = Ecs.Parent(em, cam);
                for (int i = 0; i < 8 && !Ecs.IsNull(e) && Ecs.Alive(em, e) && em.HasComponent(e, ComponentType.ReadOnly(Ecs.tiLT)); i++)
                {
                    rp = Ecs.QMul(((Ecs.HasC(em, e, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(e, Ecs.tiLT) : null))->Rotation, rp);
                    e = Ecs.Parent(em, e);
                }
                var lt = (Ecs.HasC(em, cam, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(cam, Ecs.tiLT) : null);
                if (lt != null) { lt->Rotation = Ecs.QMul(Ecs.Inv(rp), r); PatchesSeen |= 4; }
            }
            catch (Exception ex) { if (camErrors++ < 3) ModLog.Warn("turret: camera (LocalTransform): " + ex.Message); }
        }
        static bool viewLogged;
        public static unsafe void TurretCameraLTW(bool fromPrefix)
        {
            if (!EmOk || SeatedTurret == null) return;   // 0.12.2: with the free cursor the view stays the seat's
            var em = CurrentEm;
            try
            {
                if (!ViewRotation(em, out var r) || !Camera(em, out var cam) || !em.HasComponent(cam, ComponentType.ReadOnly(Ecs.tiLTW))) return;
                var p = (Ecs.HasC(em, cam, Ecs.tiLTW) ? (LocalToWorld*)em.GetComponentDataRawRW(cam, Ecs.tiLTW) : null); if (p == null) return;
                var m = p->Value;
                var cx = Ecs.Rot(r, Ecs.UX); var cy = Ecs.Rot(r, Ecs.UY); var cz = Ecs.Rot(r, Ecs.UZ);
                m.c0 = new float4 { x = cx.x, y = cx.y, z = cx.z, w = 0f }; m.c1 = new float4 { x = cy.x, y = cy.y, z = cy.z, w = 0f }; m.c2 = new float4 { x = cz.x, y = cz.y, z = cz.z, w = 0f };
                // 0.12.8 (Andre): in mode A the view stays at a fixed point of the block (ViewA, near the center of rotation: no ring/hub passes through it)
                var t = SeatedTurret;
                if (t != null && Ecs.Alive(em, t.block))
                {
                    Ecs.Pose(em, t.block, out var posB, out var rotB);
                    if (!viewLogged) { viewLogged = true; var rel = Ecs.Rot(Ecs.Inv(rotB), Ecs.Sub(new float3 { x = m.c3.x, y = m.c3.y, z = m.c3.z }, posB)); ModLog.Info("turret seat " + t.block.Index + ": game view (seated) in block = " + Ecs.F3(rel) + "; mode A uses " + Ecs.F3(ViewA)); }
                    if (SeatMode == 1)
                    {
                        // 0.12.9: the view turns along with the rings (it always stays at the same spot of the "cockpit") — so it can be farther from the
                        // center (higher and further back) without any ring passing through it; view_a_rotates = false goes back to 0.12.8's fixed point
                        var rel = ViewRotates ? Ecs.Add(TurretPivot, Ecs.Rot(t.Qvis, Ecs.Sub(ViewA, TurretPivot))) : ViewA;
                        var v = Ecs.Add(posB, Ecs.Rot(rotB, rel)); m.c3 = new float4 { x = v.x, y = v.y, z = v.z, w = 1f };
                    }
                }
                p->Value = m;
                if (fromPrefix) PatchesSeen |= 2;
            }
            catch (Exception ex) { if (camErrors++ < 3) ModLog.Warn("turret: camera (LocalToWorld): " + ex.Message); }
        }

        // ================================================================ 0.13.2: the seat's bound keys on the turret seat
        /// <summary>Andre: "when I turn my back with the turret seat I can't use the ship/car actions". SeatBindingsUpdateSystem
        /// (in CustomRenderPipelineGroup) keeps in each BindingActionable._distance (+88) the SQUARED distance from the camera to the piece, and
        /// IsInRange = up to 4.5 (~2.1 m). On the turret the view turns around the pivot (view_position_a is 0.46 m behind the center), so with
        /// your back turned the eye moves ~1 m and the car's pieces go out of reach. Here (group postfix) the distance is measured from a FIXED point of the
        /// block: where the view is with the turret looking forward — the reach doesn't change with the aim. FixedEyeActive tells 3P View (which
        /// measures from the head in F5) to leave this to Crap Mechanics.</summary>
        /// <summary>FixedEyeActive = Crap Mechanics rewrote the distances in the last frame (3P View then doesn't touch them). Measuring point: the
        /// NEARER of the game's seated view (before Crap Mechanics moves the camera to view_position_a) and the turret's view looking
        /// forward — the reach is never smaller than the normal seat's, and doesn't change with the aim.</summary>
        public static bool FixedEyeActive; public static float3 FixedEye, GameEye; static int gameEyeFrame = -1; public static float BindsReach = 4.5f;
        static int bindsErrors; static bool loggedBinds; static readonly HashSet<string> reasonsLogged = new HashSet<string>();
        static void Reason(string m) { if (reasonsLogged.Add(m)) ModLog.Info("turret seat: seat key bindings not rewritten (" + m + ")"); }
        /// <summary>TransformSystemGroup postfix: the camera's LocalToWorld has just been computed by the game (still without the turret's view).</summary>
        public static unsafe void CaptureGameEye()
        {
            if (!EmOk) return;
            // 0.24.0: the normal seat needs the game's eye too — but only when someone is really seated (otherwise
            // the behaviour is exactly as before, which only captured it when seated on a turret of ours).
            if (SeatedTurret == null) { if (SeatBindsReach <= 2.12f) return; var emS = CurrentEm; if (!PrepareSeat(emS) || Ecs.IsNull(CurrentSeat(emS))) return; }
            try
            {
                var em = CurrentEm; if (!Camera(em, out var cam) || !Ecs.HasC(em, cam, Ecs.tiLTW)) return;
                var p = (LocalToWorld*)em.GetComponentDataRawRO(cam, Ecs.tiLTW); if (p == null) return;
                GameEye = new float3 { x = p->Value.c3.x, y = p->Value.c3.y, z = p->Value.c3.z }; gameEyeFrame = UnityEngine.Time.frameCount;
            }
            catch (Exception) { }
        }
        public static unsafe void TurretBindsDistance()
        {
            bool wrote = false;
            try
            {
                if (!EmOk) return;
                var t = SeatedTurret;
                if (t == null || t.state != 2) { SeatBinds(CurrentEm); return; }   // 0.24.0: seated on the game's seat
                var em = CurrentEm;
                if (!Ecs.Alive(em, t.block)) return;
                if (!ViewRotation(em, out _)) { Reason("turret view off: the game measures from its own view"); return; }
                Ecs.Pose(em, t.block, out var posB, out var rotB);
                FixedEye = Ecs.Add(posB, Ecs.Rot(rotB, ViewA));
                bool hasGame = gameEyeFrame == UnityEngine.Time.frameCount;
                if (!PrepareSeat(em)) { Reason("seat types"); return; }
                if (qSeatSing.CalculateEntityCountWithoutFiltering() != 1) { Reason("no bindings singleton"); return; }
                var eSing = qSeatSing.GetSingletonEntity(); if (!Ecs.HasC(em, eSing, tiSeatSing)) { Reason("singleton without data"); return; }
                var b = (byte*)em.GetComponentDataRawRW(eSing, tiSeatSing); if (b == null) { Reason("null singleton"); return; }
                var list = *(byte**)(b + Layout.SeatList); if (list == null) { Reason("no list"); return; }
                var ptr = *(byte**)list; int n = *(int*)(list + 8);
                if (ptr == null || n <= 0 || n > 4096) { Reason("this seat has no bound parts (" + n + ")"); return; }
                int insideF = 0, insideJ = 0, inside = 0; float minF = 1e9f, minJ = 1e9f; var detail = new System.Text.StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    var el = ptr + i * Layout.BindSize; var e = *(Entity*)(el + Layout.BindTarget);
                    if (!Ecs.Alive(em, e) || !Ecs.HasC(em, e, Ecs.tiLTW)) continue;
                    var ltw = (LocalToWorld*)em.GetComponentDataRawRO(e, Ecs.tiLTW); if (ltw == null) continue;
                    var q = ltw->Value.c3;
                    float fx = q.x - FixedEye.x, fy = q.y - FixedEye.y, fz = q.z - FixedEye.z; float dF = fx * fx + fy * fy + fz * fz;
                    float d2 = dF;
                    if (hasGame) { float jx = q.x - GameEye.x, jy = q.y - GameEye.y, jz = q.z - GameEye.z; float dJ = jx * jx + jy * jy + jz * jz; if (dJ < d2) d2 = dJ; if (dJ <= 4.5f) insideJ++; if (dJ < minJ) minJ = dJ; }
                    // 0.13.2: the game accepts up to 4.5 squared (~2.1 m); here up to BindsReach (default 4.5 m = the click distance for binding):
                    // scales the squared distance (keeps the order) so that "inside" = up to BindsReach
                    float scale = 4.5f / (BindsReach * BindsReach);
                    *(float*)(el + Layout.BindDistance) = d2 * scale;
                    if (dF <= 4.5f) insideF++; if (dF < minF) minF = dF; if (d2 * scale <= 4.5f) inside++;
                    if (!loggedBinds) detail.Append(" #").Append(i).Append(' ').Append(System.Math.Sqrt(d2).ToString("F2")).Append('m');
                }
                wrote = true;
                if (!loggedBinds) { loggedBinds = true; ModLog.Info("turret seat: seat key bindings in reach " + inside + "/" + n + " with bindings_reach (from the game's seated view " + (hasGame ? insideJ + ", closest " + System.Math.Sqrt(minJ).ToString("F2") + " m" : "unknown") + "; from the turret's forward view " + insideF + ", closest " + System.Math.Sqrt(minF).ToString("F2") + " m; the game's reach is 2.12 m, this mod's " + BindsReach.ToString("F2") + " m) — nearest distance per bound part:" + detail); }
            }
            catch (Exception ex) { if (bindsErrors++ < 3) ModLog.Warn("turret seat: seat key bindings: " + ex.Message); }
            finally { FixedEyeActive = wrote; }
        }

        // 0.24.0 (Andre: "keybinds don't work on the normal seat"): the same reach fix, now on the GAME's seat. On the turret the
        // distance is measured from the turret's view or the game's (whichever is smaller); here there's only the game's, so it's simpler.
        // The game accepts up to 4.5 squared (~2.12 m); we rescale the squared distance (keeping the order) so that "inside" applies
        // up to SeatBindsReach. With 2.12 (or less) it writes nothing and the seat stays exactly as the game does it.
        public static float SeatBindsReach = 4.5f;   // [Seat] bindings_reach
        static bool loggedSeatBinds; static int seatBindsErrors;
        static unsafe void SeatBinds(EntityManager em)
        {
            if (SeatBindsReach <= 2.12f) return;
            if (gameEyeFrame != UnityEngine.Time.frameCount) return;   // without the game's eye for this frame it can't measure
            try
            {
                if (!PrepareSeat(em)) return;
                if (Ecs.IsNull(CurrentSeat(em))) { loggedSeatBinds = false; return; }   // nobody seated
                if (qSeatSing.CalculateEntityCountWithoutFiltering() != 1) return;
                var eSing = qSeatSing.GetSingletonEntity(); if (!Ecs.HasC(em, eSing, tiSeatSing)) return;
                var b = (byte*)em.GetComponentDataRawRW(eSing, tiSeatSing); if (b == null) return;
                var list = *(byte**)(b + Layout.SeatList); if (list == null) return;
                var ptr = *(byte**)list; int n = *(int*)(list + 8);
                if (ptr == null || n <= 0 || n > 4096) return;
                float scale = 4.5f / (SeatBindsReach * SeatBindsReach);
                int inside = 0, insideGame = 0; float minD = 1e9f;
                for (int i = 0; i < n; i++)
                {
                    var el = ptr + i * Layout.BindSize; var e = *(Entity*)(el + Layout.BindTarget);
                    if (!Ecs.Alive(em, e) || !Ecs.HasC(em, e, Ecs.tiLTW)) continue;
                    var ltw = (LocalToWorld*)em.GetComponentDataRawRO(e, Ecs.tiLTW); if (ltw == null) continue;
                    var q = ltw->Value.c3;
                    float dx = q.x - GameEye.x, dy = q.y - GameEye.y, dz = q.z - GameEye.z; float d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 <= 4.5f) insideGame++;
                    if (d2 < minD) minD = d2;
                    *(float*)(el + Layout.BindDistance) = d2 * scale;
                    if (d2 * scale <= 4.5f) inside++;
                }
                if (!loggedSeatBinds)
                {
                    loggedSeatBinds = true;
                    ModLog.Info("seat: seat key bindings in reach " + inside + "/" + n + " with bindings_reach " + SeatBindsReach.ToString("F2")
                        + " m (the game's own reach is 2.12 m and would allow " + insideGame + "; closest bound part " + System.Math.Sqrt(minD).ToString("F2") + " m)");
                }
            }
            catch (Exception ex) { if (seatBindsErrors++ < 3) ModLog.Warn("seat: seat key bindings: " + ex.Message); }
        }

        static string TurretStates()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in turrets.Values)
            {
                sb.Append("  ").Append(t.name).Append(' ').Append(t.block.Index).Append(": ").Append(TurretName(t));
                if (t.state == 2) sb.Append(", yaw ").Append(t.yaw.ToString("F0")).Append(" pitch ").Append(t.pitch.ToString("F0")).Append(t.isSeated ? " (seated)" : "").Append(", ").Append(t.movs.Count).Append(" part(s) on the plates, error ").Append(t.errLog.ToString("F1"));
                sb.Append('\n');
            }
            return sb.ToString();
        }
        static IEnumerable<(Entity a, Entity b)> TurretLinks()
        {
            foreach (var t in turrets.Values) if (t.state == 2) foreach (var m in t.movs) if (!Ecs.IsNull(m.part) && !Ecs.IsNull(t.partBase)) yield return (m.part, t.partBase);
        }
        static void RewindTurrets(EntityManager em)
        {
            foreach (var t in turrets.Values)
            {
                if (t.state == 1) { t.broken.Clear(); t.state = 0; t.tState = 0f; continue; }
                if (t.state != 2 || t.movs.Count == 0) continue;
                t.partBase = Ecs.Parent(em, t.block);
                if (!CreateMovs(em, t, true)) { UndoTurret(em, t, "time rewind"); t.state = 0; t.tState = 0f; }
            }
        }
        static void ForgetTurrets() { turrets.Clear(); SeatedTurret = null; }
        static void CleanTurrets(EntityManager em)
        {
            foreach (var t in turrets.Values) UndoTurret(em, t, "cleaning up");
            turrets.Clear(); SeatedTurret = null;
        }
    }
}
