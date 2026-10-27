using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace CrapMechanics
{
    internal enum Kind { Piston, Bearing, Motor, Recoupler, Beacon, Levers, TurretSeat, Transmission, Radio, End }   // 0.20.0: End = Piston End (only marks the end of the stroke)

    /// <summary>A piston or bearing inside a launched ship, and the physics joint it keeps.</summary>
    internal partial class Joint
    {
        public Entity block, neighbor, partA, partB, joint, joint2, joint3;
        public Kind kind; public float stroke; public string name;
        // 0.20.0: scale = the block's scale (1 = 0.5 m; 0.5 = Mini); middle = half the side; strokeMax = the prefab's stroke (8 / 4 m); with a
        // Piston End aligned in front, stroke = distance to it and extBase = rod extension when the joint frames coincide
        // (the rod is born already stretched to the end). endBlock = the Piston End block used.
        public float scale = 1f, strokeMax; public float middle => 0.25f * scale; public float extBase; public Entity endBlock; public bool withEnd; public float tEnd; public bool endWaitLog;
        public bool mirror;      // 0.12.14: mirrored version (V): the bearing turns the other way with the same signal
        public int state;        // 0 waiting for launch, 1 separating, 2 active, 4 merged (still piston = a single part, 0.26.3)
        public float tState;     // seconds in the current state
        public int idxMotor = -1; public float target, currentTarget;
        public float3 axis, anchor;
        // 0.7.4: point/axis/perpendicular of the fit on the upper side, stored in the space of the neighbour BLOCK (not of the part, which the
        // game recreates). That way the joint can be recreated at the same pose when the game rebuilds the parts.
        public float3 bLocP, bLocE, bLocQ, gP, gE, gQ; public long locNeighbor = -1; public int deferrals; public float tWait, tImprove, bestLateral;   // 0.24.0: wait for the fit by time/convergence
        public float tTilt, tTiltAcc, measuredStroke, strokeBefore, gaveUpTarget, tYieldLog; public bool tilted; public bool measuredStrokeOk;
        public BodyFrame faExtra, fbExtra; public bool extraWeld; public float angCmd; public bool angCmdOk;   // 0.26.0: rigid joint
        public float tStill, tSettled, strokeSettled, tMergeFailed; public string mergeLog; public bool unmerging;   // 0.26.3: merge of the still piston (PistonMerge.cs)
        public List<int> colIdx; public long colBlob; public List<Entity> topSide; public long sidePart = -1; public bool moving; public float tRecalc, tNoPart, tColLog; public string kinLog; public long colBefore; public int recalcs;   // 0.26.5: kinematic piston (Fusao.cs)
        public RigidTransform poseRelNb; public bool poseRelNbOk;   // 0.26.1: pose of the upper block relative to the piston, to hold it during the separation window   // 0.24.0: a piston that can't close the error stops pushing
        // piston: stroke (m) at which the two joint frames coincide. 0 the 1st time; when recreating = current stroke. Motor target =
        // -(requested stroke - strokeBase), the same convention that already works the 1st time (frames together, -x stretches x).
        public float strokeBase; public float tAlign; public string alignLog;
        // piston with a recoupled load (0.7.6): extra motor joint straight onto the piece hanging from the Recoupler (the car), so it
        // doesn't depend on a chain piston -> light top (110 kg) -> heavy car (7 t), which couldn't lift it
        public Entity extra, extraPart; public int idxExtra = -1; public float strokeBaseExtra;
        public float tStart;   // 0.7.8: seconds the joint stays locked after being born (soft start)
        public List<Entity> rails = new List<Entity>();   // 0.7.9: piston rails (prismatics parallel to the axis)
        // 0.7.12: a bearing born off the axle is pulled into the right place during the start (fixed joint whose side A frame
        // moves from where the wheel is to the right fit, like the Recoupler); apA/apB = extra support points (local)
        public float angServo = 999f, angLog = 999f, tAngLog, angCont;   // 0.19.1: angCont = angle without wrapping at 180 (servo up to 360)   // 0.7.14: servo done by the mod (measured angle, rad)
        public BodyFrame pullA0, pullA1; public float tPullT = -1f, pullDur; public float3[] apA0, apA1, apB;
        public BodyFrame fa, fb;
        public int mode = -1;     // bearing: 0 free, 1 motor, 2 servo
        public bool spin; public bool spinRefOk; public float spinRef, spinTarget; public int spinLog;   // 0.19.14: Gyro Bearing (leveller) — the servo's target comes from gravity
        public Entity knob; public float tKnob = 9f; public int knobHint = int.MinValue; public float servoDegrees = float.NaN, strokeFraction = 1f;   // 0.19.2: knob on the block
        public Entity swAng; public float tSwAng = 9f; public float lastOutput = float.NaN; public bool swLog;   // 0.23.0: switch + angle output
        public bool didWarnNet;   // 0.24.0-mp: already logged that this part belongs to the server
        public bool letGo;       // 0.26.10: the upper piece was let go (Create refused to weld far away): doesn't separate again until the garage / time rewind
        public float tLaunch = -1f;   // 0.26.12: Time.time at which the block showed up in a part (left the garage); -1 in the garage
        public float servoRadius; public long partRadius = -1;   // 0.26.14: distance from the axis to the farthest block of the piece the servo turns
        public float angVelCmd;   // 0.26.10: speed of the rigid servo's command (rad/s), with limited acceleration
        public float impulseMode; public string modeOrigin;
        public int warnings;
        // ship joints (SpaceshipSingleton._joints) that we forced to break: index and original _breakForce, to give back later
        public List<(int idx, Entity a, Entity b, float breakForce)> broken = new List<(int, Entity, Entity, float)>();
        public string signalReason; public float signalLogged = -999f; public float tSignalLog;
        public byte powerState = 254;
        // game joints around the block (its own and those of the blocks glued to it): to reinforce and to watch the strain
        public List<(int idx, Entity a, Entity b)> watched = new List<(int, Entity, Entity)>();
        public float strainMax, strainMaxBf; public int strainMaxIdx = -1; public Entity strainA, strainB;
        // 0.11.0: piston — separate renderers for the rod and the head (children of the block); the mod stretches/raises them with the stroke
        public Entity rod, head; public float visualExt = -1f; public int visualAttempts; public float tVisual, tRetract; public bool retractedLog, garageLog;
        public int splitFailures; public bool didWarnStuck;   // 0.11.1: separation attempts that didn't work / "held by another path" warning
        public bool rail; public bool welded;                 // 0.12.1: rail = piston parallel to another one linking the same pieces (it only slides); welded = ring/stuck
        public Entity[] leds; public int ledState = -2; public float tLeds = 9f; public float3[] ledRest;   // 0.17.0: + bearing LEDs (locked)   // 0.12.1: green/yellow/red LEDs (extra renderers)
    }

    /// <summary>Motor: reads the input (-1..1), draws power from the red port and publishes the effective signal on the output.
    /// The bearing wired to the output (data cable, may go through Frame With Ports) uses its maxVel/impulse.</summary>
    internal class Motor
    {
        public Entity block, input, power, output; public string name;
        public Entity brakePort; public float brake; public bool brakeLog;   // 0.11.0: 2nd input = brake (0..1), locks the wheels
        public float velMax, impulse, maxConsumption;
        public float signal; public bool powered; public byte state = 254;
        public bool portsOk; public int warnings; public string trail; public float signalLog = -9f;
        // 0.8.0: fuel motor (the block is an EPC_SCPipe: the game's pipes fill its FuelData)
        public bool fuel; public float baseImpulse, efficiency = 1f, fuelHas = -1f, usage = 0f; public string fuelLog; public float rendLog = -1f;
        public float effectiveImpulse => fuel ? baseImpulse * System.Math.Max(0.05f, efficiency) : impulse;
        public int bearingsLog = -1;   // 0.15.0
    }

    /// <summary>Finds the mod's blocks in the ships and keeps their physics joints.</summary>
    internal static partial class JointSystem
    {
        // mod prefabs: name -> (kind, piston stroke in m | motor level)
        static readonly (string name, Kind kind, float stroke)[] PREFABS = {
            ("SC_Piston", Kind.Piston, 8f), ("SC_MiniPiston", Kind.Piston, 4f),   // 0.20.0: single piston (up to 8 m), mini (up to 4 m), Piston End
            ("SC_Bearing", Kind.Bearing, 0f), ("SC_GyroBearing", Kind.Bearing, 1f),   // 0.19.14: stroke 1 = gyroscope (leveller)
            ("SC_MiniBearing", Kind.Bearing, 0f), ("SC_MiniGyroBearing", Kind.Bearing, 1f),   // 0.20.0
            ("SC_MotorSmall", Kind.Motor, 0f), ("SC_MotorMedium", Kind.Motor, 1f), ("SC_MotorLarge", Kind.Motor, 2f),
            ("SC_MotorFuelSmall", Kind.Motor, 3f), ("SC_MotorFuelMedium", Kind.Motor, 4f), ("SC_MotorFuelLarge", Kind.Motor, 5f),
            ("SC_Recoupler", Kind.Recoupler, 0f), ("SC_Beacon", Kind.Beacon, 0f), ("SC_Pedal", Kind.Levers, 0f), ("SC_SteeringWheel", Kind.Levers, 0f),
            ("SC_TurretSeat", Kind.TurretSeat, 0f),
            ("SC_DriveShaft", Kind.Transmission, 0f), ("SC_Differential", Kind.Transmission, 0f), ("SC_ShaftReverser", Kind.Transmission, 0f), ("SC_ShaftTransmitter", Kind.Transmission, 0f),
            ("SC_SingleCellBattery", Kind.Transmission, 0f), ("SC_MiniElectricThruster", Kind.Transmission, 0f), ("SC_LongRangeTransmitter", Kind.Radio, 0f), ("SC_SolarPanelMini", Kind.Transmission, 0f),
            ("SC_SlipRing", Kind.Transmission, 0f), ("SC_MiniSlipRing", Kind.Transmission, 0f),   // 0.22.0: slip ring (passive: SlipRings.cs finds it by hash); 0.22.4: normal/mini plates
            ("SC_PistonEnd", Kind.End, 0f), ("SC_MiniPistonEnd", Kind.End, 0f) };   // 0.22.4: Piston End as a plate (normal/mini, fit both pistons)   // 0.19.0: mini solar panel (game only)   // 0.15.0 / 0.16.0 / 0.18.0 (battery and mini thruster: game only)
        /// <summary>0.12.15: is the name (with or without " M") a block of this mod?</summary>
        public static bool IsModBlock(string name)
        {
            if (name == null) return false; if (name.EndsWith(" M")) name = name.Substring(0, name.Length - 2);
            foreach (var p in PREFABS) if (p.name == name) return true;
            return name.StartsWith("SC_Wheel") || name.StartsWith("SC_TankSmall");
        }
        static readonly Dictionary<ulong, int> byHash = new Dictionary<ulong, int>();
        /// <summary>0.19.15: a mod block with a knob? (bearing, gyro, pistons) — by the prefab's hash; works in the garage.</summary>
        public static bool BlockWithKnob(ulong hash, out string name, out bool bearing, out bool spin, out float stroke)
        {
            name = null; bearing = false; spin = false; stroke = 0f;
            if (!hashesOk || !byHash.TryGetValue(hash, out var i)) return false;
            var p = PREFABS[i]; if (p.kind != Kind.Bearing && p.kind != Kind.Piston) return false;
            name = p.name + (hashMirror.Contains(hash) ? " M" : ""); bearing = p.kind == Kind.Bearing; spin = bearing && p.stroke == 1f; stroke = p.stroke; return true;
        }
        /// <summary>0.20.0: the prefab's kind by hash (End/Piston/...); false if it isn't the mod's.</summary>
        public static bool KindFromHash(ulong hash, out Kind kind, out float stroke, out string name)
        {
            kind = Kind.End; stroke = 0f; name = null;
            if (!hashesOk || !byHash.TryGetValue(hash, out var i)) return false;
            kind = PREFABS[i].kind; stroke = PREFABS[i].stroke; name = PREFABS[i].name; return true;
        }
        public static float ScaleFromName(string name) => name != null && name.StartsWith("SC_Mini") ? 0.5f : 1f;
        public static bool HashesOk => hashesOk;
        public static bool HasJoint(Entity e) => allJoints.ContainsKey(Ecs.K(e));
        /// <summary>0.21.0: pairs of parts linked by an active mod joint (+ turrets). 0.22.0: the cable splice was removed; kept for whoever needs it.</summary>
        public static IEnumerable<(Entity, Entity)> LinkedParts()
        {
            foreach (var j in allJoints.Values) if (j.state == 2 && !Ecs.IsNull(j.partA) && !Ecs.IsNull(j.partB)) yield return (j.partA, j.partB);
            foreach (var (ta, tb) in TurretLinks()) yield return (ta, tb);
        }
        public static bool ActiveJoint(Entity e) => allJoints.TryGetValue(Ecs.K(e), out var j) && (j.state == 2 || j.state == 4);   // 0.20.5; 0.26.3: merged counts
        /// <summary>0.22.0: active joints (for SlipRings.cs: bearing / piston -> slip ring on the upper piece).</summary>
        internal static IEnumerable<Joint> ActiveJoints() { foreach (var j in allJoints.Values) if (j.state == 2 || j.state == 4) yield return j; }   // 0.26.3: merged: partB = the single part (the ring stays linked)
        static readonly HashSet<ulong> hashMirror = new HashSet<ulong>();
        static bool hashesOk = false;
        static readonly Dictionary<long, Joint> allJoints = new Dictionary<long, Joint>();
        static readonly Dictionary<long, Motor> motors = new Dictionary<long, Motor>();
        /// <summary>0.14.0: each motor has ForceReserve x the listed force, split among the bearings it moves (0.15.0: through the Drive Shafts, DriveShaft.cs).</summary>
        public static float ForceReserve = 4f;
        static readonly Dictionary<long, Motor> motorByOutput = new Dictionary<long, Motor>();   // output port -> motor
        static readonly HashSet<long> ignored = new HashSet<long>(); static readonly HashSet<ulong> hashesSeen = new HashSet<ulong>();
        static float tDiscover = 9f, tKnobs = 9f;

        // config (filled in by the plugin)
        public static float PistonSpeed = 1f, PistonImpulse = 20000f, PistonConsumptionPerM = 2.5f;
        public static float[] MotorSpeed = { 5f, 8f, 12f }, MotorImpulse = { 1000f, 4000f, 13000f }, MotorConsumption = { 7f, 28f, 70f };
        public static float[] FuelMotorSpeed = { 5f, 8f, 12f }, FuelMotorImpulse = { 1500f, 6000f, 19500f };
        public static float FuelConsumption = 0.85f, FuelPower = 5f;
        public static float BrakeFactor = 3f;   // 0.11.0: brake impulse = motor impulse x factor
        public static float MotorRamp = 1f, ServoImpulse = 3000f, ServoDegrees = 45f, BearingSupport = 1f, ServoSpeed = 3f, ServoTipSpeed = 4f; public static bool ServoInvert = false;   // 0.26.14: + ServoTipSpeed ([Bearing] servo_tip_speed)
        // 0.24.0 (Andre: "when I tell it to turn 180 it overshoots and keeps falling without ever stopping"): the gain decides WHERE the servo starts
        // braking — it asks for ServoGain x error, capped at ServoSpeed, so braking only starts with an error below ServoSpeed/ServoGain
        // (with 3 and 8, 21 degrees from the target). A heavy load needs to start braking much earlier: SMALLER gain = earlier and longer braking.
        public static float ServoGain = 8f;   // rad/s of requested speed per rad of error
        // 0.26.0 (Andre: "the piston really had to have infinite force, like the one in minecraft ... it pushes up to x and back, there's no
        // weight it can't push"). Up to 0.25 the piston was a POSITION MOTOR: a spring with a force ceiling, so the position was a
        // request and not a fact — it gave way under weight, oscillated, misaligned in a chain and no door closed tight. Here it becomes a WELD
        // whose length the mod chooses: a fixed joint redone at the commanded position. There's no "couldn't do it", there's "done" or "is
        // blocked" (the collar below). The bearing's servo follows the same idea: a weld at the requested angle, instead of a velocity motor.
        public static bool Rigid = true;
        public static bool PistonCollides = true;   // 0.26.1: [Piston] collide_with_load — collision between the piston's part and the load
        public const float RigidSlack = 0.08f;      // m: how far the command may get ahead of what the rod really moved
        public const float RigidSlackAng = 0.12f;   // rad (~7 degrees): the same collar on the servo
        public static int SolverIterations = 8; public static bool IterationsAlways = false;   // 0.26.12
        public static float BaseReinforce = 20f, StartTime = 1f, StartPause = 0.3f, PistonRails = 0.5f, PistonRailsPerMeter = 0.4f;
        public const float ReinforceCap = 1e7f;
        public static float FitWait = 3f; public const float StillWait = 1f; public static bool SnapBack = true;
        public static float PistonTiltMax = 15f; public const float PistonTiltTime = 1f;   // 0.24.0: [Piston] max_tilt
        public const float IdleConsumption = 0.05f;   // P/s while still: keeps the circuit's power state valid
        public static bool DetailedLog = true;

        /// <summary>0.19.0 (crash when loading another BP, 01/10: the log stopped at "paint: ..." after closing the BP menu): in the first
        /// cycles after the BP menu closes, each step goes to the log (the per-session log of Replay writes line by line — if the game crashes,
        /// the last line says at which step).</summary>
        public static int Trail;
        public static void T(string stage)
        {
            // 0.26.10: time of each step (from the previous T up to this one), added up until the next performance log — the trace measures by writing
            // a line to the log, and that distorts exactly the steps that log the most
            double now = swStage.Elapsed.TotalMilliseconds;
            if (stage != "cycle start") { stageTime.TryGetValue(stage, out var v); stageTime[stage] = v + (now - tPrevStage); }
            tPrevStage = now;
            if (Trail > 0) ModLog.Info("after BP load (" + Trail + "): " + stage); else ModLog.Trace("cycle " + stage);
        }
        static readonly System.Diagnostics.Stopwatch swStage = System.Diagnostics.Stopwatch.StartNew();
        static readonly Dictionary<string, double> stageTime = new Dictionary<string, double>(); static double tPrevStage;
        /// <summary>0.26.10: the slowest steps since the last call (ms per frame) — goes into the performance log. Resets the sum.</summary>
        public static string SlowestSteps(int frames)
        {
            var l = new List<KeyValuePair<string, double>>(stageTime); l.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < l.Count && i < 6; i++) sb.Append(i == 0 ? "" : ", ").Append(l[i].Key).Append(' ').Append((l[i].Value / System.Math.Max(1, frames)).ToString("F2"));
            stageTime.Clear(); return sb.ToString();
        }
        public static void Step(EntityManager em, float dt)
        {
            if (!Ecs.Prepare(em)) return;
            T("cycle start");
            WatchTick(em);
            if (RewindRequested) { RewindRequested = false; try { Rewind(em); RewindTurrets(em); } catch (Exception ex) { ModLog.Error("time rewind: " + ex); } }
            Bonus.Step(em, dt); T("bonus");
            Solver(em, dt); T("solver");
            try { GravityControl(em); } catch (Exception ex) { WarnLimited("gravity: " + ex.Message); } T("gravity");
            if (!hashesOk)
            {
                hashesOk = true;
                for (int i = 0; i < PREFABS.Length; i++)
                {
                    ulong h = Ecs.HashPrefab(PREFABS[i].name); if (h != 0) byHash[h] = i;
                    ulong hm = Ecs.HashPrefab(PREFABS[i].name + " M"); if (hm != 0 && hm != h) { byHash[hm] = i; hashMirror.Add(hm); }   // 0.12.14: mirrored version
                    ModLog.Info("prefab " + PREFABS[i].name + " hash=" + h + " (M " + hm + ")");
                }
            }
            T("hashes"); tDiscover += dt;
            // 0.20.1: a piston with a Piston End that was just launched (the garage knew about it): discover it NOW, in the same frame — with every 0.5 s the
            // piece attached to the end had already fallen up to 1 m before the motor was born
            T("pistons with end..."); if (tDiscover < 0.5f) foreach (var pe in Garage.PistonsWithEnd()) if (!allJoints.ContainsKey(Ecs.K(pe)) && Ecs.IsPart(em, Ecs.Parent(em, pe))) { tDiscover = 9f; ModLog.Info("piston with a Piston End just launched (block " + pe.Index + ") — discovering now"); break; }
            if (tDiscover >= 0.5f) { tDiscover = 0f; T("discover..."); Discover(em); T("discover done"); }
            // 0.20.0 (Andre: "lower it to .1"): garage knobs every 0.1 s (it was together with Discover, 0.5 s); + garage step (piston + Piston End, H guide)
            T("pistons with end done"); tKnobs += dt;
            if (tKnobs >= 0.1f) { tKnobs = 0f; T("knobs (garage)..."); try { Knobs.GarageStep(em); } catch (Exception ex) { WarnLimited("knobs (garage): " + ex.Message); } }   // 0.19.15
            T("knobs (garage) done"); try { Garage.Step(em, dt); } catch (Exception ex) { WarnLimited("garage: " + ex.Message); }
            T("garage pass done"); Knobs.Store(dt);
            T("knobs saved"); try { Painting.Step(em, dt); } catch (Exception ex) { WarnLimited("paint: " + ex.Message); }   // 0.17.1
            T("paint done");
            try { Radio.Step(em); } catch (Exception ex) { WarnLimited("long range: " + ex.Message); }   // 0.18.0
            T("long range done");

            // motors first (the bearings read their state)
            var deadSet = new List<long>();
            foreach (var kv in motors)
            {
                var m = kv.Value;
                try { if (!Ecs.Alive(em, m.block)) { deadSet.Add(kv.Key); continue; } MotorStep(em, m); }
                catch (Exception ex) { if (m.warnings++ < 5) ModLog.Warn(m.name + " " + m.block.Index + ": " + ex.Message); }
            }
            foreach (var k in deadSet) motors.Remove(k);
            if (deadSet.Count > 0) RedoOutputMap();
            T("motors done");
            try { MapAxes(em, dt); } catch (Exception ex) { WarnLimited("map: " + ex.Message); }
            T("shafts done");   // 0.15.0: who moves whom through the Drive Shafts

            var deadOnes = new List<long>();
            foreach (var kv in allJoints)
            {
                var j = kv.Value;
                try
                {
                    if (!Ecs.Alive(em, j.block)) { Undo(em, j, "block is gone"); deadOnes.Add(kv.Key); continue; }
                    j.tState += dt;
                    // 0.28.0: per machine — the server (or a solo game) drives the mod's physics, a client only draws (Multi.cs)
                    bool ours = Multi.MyJoint(em, j.partA, j.partB);
                    try { Knobs.Step(em, j, dt); } catch (Exception ex) { if (j.warnings++ < 2) ModLog.Warn(j.name + " knob: " + ex.Message); }   // 0.19.2
                    // 0.19.6 (Andre: "it didn't retract the piston when the simulation ended"): the game rebuilds the garage entities after
                    // the recall; while the piston is out of the simulation, check again every 0.5 s
                    if (j.kind == Kind.Piston && j.state == 0) { j.tRetract += dt; if (j.tRetract >= 0.5f) { j.tRetract = 0f; RetractInGarage(em, j); } }
                    if (j.kind == Kind.Piston) PistonLeds(em, j, dt);
                    if (j.kind == Kind.Bearing) { try { AngleOutput(em, j, dt); } catch (Exception ex) { if (j.warnings++ < 2) ModLog.Warn(j.name + " angle output: " + ex.Message); } }   // 0.23.0
                    // 0.24.0-mp: the server's parts get no physics from a client. State 0 (breaking the game's joints to release the upper
                    // piece) stays behind break_on_client (debug): the server splits the parts and the game sends the split to the clients.
                    if (ours ? j.state == 2 : j.cReady) Multi.Tally(ours);
                    if (!ours && !(j.state == 0 && Multi.BreakOnClient))
                    {
                        if (!j.didWarnNet) { j.didWarnNet = true; ModLog.Info(j.name + " " + j.block.Index + ": on a client — the server drives it, this machine only draws it"); }
                        try { ClientView(em, j, dt); } catch (Exception ex) { if (j.warnings++ < 3) ModLog.Warn(j.name + " " + j.block.Index + ": client view: " + ex.Message); }   // 0.28.0
                        continue;
                    }
                    switch (j.state)
                    {
                        case 0: WaitingTop(em, j); break;
                        case 1: Separating(em, j); break;
                        case 2: if (j.kind == Kind.Bearing) ActiveBearing(em, j, dt); else ActivePiston(em, j, dt); break;
                        case 4: Merged(em, j, dt); break;   // 0.26.3
                    }
                }
                catch (Exception ex) { if (j.warnings++ < 5) ModLog.Warn(j.name + " " + j.block.Index + ": " + ex.Message); }
            }
            foreach (var k in deadOnes) allJoints.Remove(k);
            T("joints loop");   // 0.26.10: separates the joints loop from the mass balance in the performance log
            try { BalanceMasses(em); } catch (Exception ex) { WarnLimited("piston mass balance: " + ex.Message); }   // 0.26.7
            T("mass balance");
            tReplan += dt; if (tReplan >= 1f) { tReplan = 0f; try { ReplanFree(em); } catch (Exception ex) { ModLog.Warn("replanning rings: " + ex.Message); } }
            T("joints done");
            LaunchQueue(em, dt); T("queue");
            Couplers.Step(em, dt); T("recoupler/beacon");
            try { SlipRings.Step(em, dt); } catch (Exception ex) { WarnLimited("slip ring: " + ex.Message); } T("slip ring");   // 0.21.0; 0.22.0: slip ring
            try { Multi.Step(em, dt); } catch (Exception ex) { WarnLimited("multiplayer: " + ex.Message); }   // 0.24.0-mp
            try { ModNet.Step(em, dt); } catch (Exception ex) { WarnLimited("mod channel: " + ex.Message); }   // 0.28.0
            try { Signal.Step(em, dt); } catch (Exception ex) { WarnLimited("signal: " + ex.Message); }   // 0.24.0: transmitter stuck without signal
            Levers.Step(em, dt); T("levers");
            TurretsStep(em, dt); T("turrets");
        }

        // PhysicsStep.SolverIterationCount (+20): the game uses the default (4). With more iterations the joints give way much less under
        // weight (large Offroad wheel). Only raises it (never lowers) and only while we have an active joint; checks every 2 s.
        static TypeIndex tiStep; static EntityQuery qStep; static bool stepOk; static float tStep = 9f; static int stepLogged = -1;
        static unsafe void Solver(EntityManager em, float dt)
        {
            if (SolverIterations <= 0) return;
            tStep += dt; if (tStep < 2f) return; tStep = 0f;
            try
            {
                // 0.26.0: raising the solver iterations costs CPU in the whole world, and only a SOFT joint needs it. With rigid pistons and servos
                // what's left is the bearing in motor mode (velocity), which is soft on purpose. A ship with only pistons/doors runs with
                // the game's own iterations.
                // 0.26.12: [Physics] solver_iterations_rigid = raise it also with only welds/free bearings active (car test: chain
                // body -> axle -> mini pistons -> hubs -> wheels giving way with the game's iterations)
                bool hasJoint = false; foreach (var j in allJoints.Values) if (j.state == 2 && (IterationsAlways || !Rigid || (j.kind == Kind.Bearing && j.mode == 1))) { hasJoint = true; break; }
                if (!hasJoint) return;
                if (!stepOk) { tiStep = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<PhysicsStep>()); var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiStep); qStep = em.CreateEntityQuery(a); stepOk = true; }
                foreach (var e in Ecs.QueryList(qStep))
                {
                    var ps = (Ecs.HasC(em, e, tiStep) ? (PhysicsStep*)em.GetComponentDataRawRW(e, tiStep) : null); if (ps == null) continue;
                    if (ps->SolverIterationCount < SolverIterations)
                    {
                        int before = ps->SolverIterationCount; ps->SolverIterationCount = SolverIterations;
                        if (stepLogged != before) { stepLogged = before; ModLog.Info("physics: solver iterations " + before + " -> " + SolverIterations + " (stiffer joints)"); }
                    }
                }
            }
            catch (Exception ex) { SolverIterations = 0; ModLog.Warn("physics: could not change PhysicsStep: " + ex.Message); }
        }

        // 0.26.2 (Andre: "what if we freeze the physics at the start ... why doesn't the physics stay frozen during those 3s?"). The right idea, except
        // that freezing the whole simulation group (a) also freezes the system that SPLITS the parts — which is what we're waiting for —
        // and (b) gives a catch-up jolt when turned back on (it already froze the game in Replay). So we don't stop the clock: we zero the world's
        // GRAVITY (PhysicsStep.Gravity, +4) during the launch settling window. Physics keeps running (the parts
        // split), but nothing accelerates downwards, so the pieces that were just released stay where they were built and the weld is born
        // square. Applies ONLY right after leaving the garage; restored right after (no jolt, the clock never stopped). It covers the whole world
        // at once — including loose substructures that the per-piece holding doesn't track (it was one of those falling as a block that
        // gave the same 28 cm on several pistons).
        public static bool ZeroGravLaunch = true;
        static float tGravOff, tGravMax; static bool gravSaved; static float3 gravOrig;
        /// <summary>0.26.12: the joint left the garage less than 5 s ago (works for bearings too; Garage.JustLaunched only knows pistons).</summary>
        static bool JustLaunchedJ(Joint j) => j.tLaunch >= 0f && UnityEngine.Time.time - j.tLaunch < 5f;
        public static void RequestZeroGrav()
        {
            if (!ZeroGravLaunch) return;
            float now = UnityEngine.Time.time;
            if (tGravOff <= 0f) tGravMax = now + 4f;                                  // absolute ceiling: never stays without gravity longer than this
            tGravOff = System.Math.Min(now + 0.4f, tGravMax);                         // extends while there is still a piece separating
            frameGravOff = UnityEngine.Time.frameCount + GravFrames; tRamp = -1f;    // 0.26.13
        }
        // 0.26.13 (Andre's log, 23/10: the 0.4 s window was wall-clock time, and the launch's own 611 ms frame used it up —
        // gravity came back right after the 1st long frame and the 2nd (311 ms, 18 ticks) already ran with the full weight; the car's axle
        // split 1 ms after "gravity restored"). Now (1) gravity only comes back after GravFrames FRAMES with no new request, so
        // the window spans the long frames; and (2) it comes back on a ramp of GravRamp s, instead of the whole weight in a single step.
        const int GravFrames = 10; const float GravRamp = 0.5f;
        static int frameGravOff; static float tRamp = -1f;
        static unsafe void GravityControl(EntityManager em)
        {
            float now = UnityEngine.Time.time;
            bool wants = tGravOff > 0f && now < tGravMax && (now < tGravOff || UnityEngine.Time.frameCount < frameGravOff);
            if (!wants && !gravSaved) { if (tGravOff > 0f) tGravOff = 0f; return; }
            if (!wants && tRamp < 0f) tRamp = now;
            float f = wants ? 0f : System.Math.Min(1f, (now - tRamp) / GravRamp);
            if (!stepOk) { tiStep = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<PhysicsStep>()); var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiStep); qStep = em.CreateEntityQuery(a); stepOk = true; }
            foreach (var e in Ecs.QueryList(qStep))
            {
                var ps = (Ecs.HasC(em, e, tiStep) ? (PhysicsStep*)em.GetComponentDataRawRW(e, tiStep) : null); if (ps == null) continue;
                if (wants) { if (!gravSaved) { gravOrig = ps->Gravity; gravSaved = true; ModLog.Info("physics: gravity held at zero while the launch settles (pieces stay put, joints are born square)"); } ps->Gravity = float3.zero; }
                else ps->Gravity = Ecs.Mul(gravOrig, f);
            }
            if (!wants && gravSaved && f >= 1f) { gravSaved = false; tGravOff = 0f; tRamp = -1f; ModLog.Info("physics: gravity restored (ramped back over " + GravRamp.ToString("F1") + " s)"); }
        }

        static void Discover(EntityManager em)
        {
            bool newMotor = false;
            foreach (var e in Ecs.Components(em))
            {
                long key = Ecs.K(e);
                if (allJoints.ContainsKey(key) || motors.ContainsKey(key) || Couplers.Has(key) || Levers.Has(key) || HasTurret(key) || Radio.Has(key) || ignored.Contains(key)) continue;   // 0.12.3: HasTurret (the turret was recreated on every scan: the aim went back to the start)
                ulong h = Ecs.Prefab(em, e);
                if (!byHash.TryGetValue(h, out var i)) { ignored.Add(key); continue; }
                bool mir = hashMirror.Contains(h); string nameP = PREFABS[i].name + (mir ? " M" : "");
                if (PREFABS[i].kind != Kind.Motor && Ecs.IsGameDecoupler(em, e)) { ignored.Add(key); continue; }
                if (PREFABS[i].kind == Kind.Transmission || PREFABS[i].kind == Kind.End) { ignored.Add(key); continue; }   // 0.15.0: only read by MapAxes; 0.20.0: the Piston End is only searched for by the piston
                if (PREFABS[i].kind == Kind.Radio) { Radio.Track(e); continue; }   // 0.18.0
                if (PREFABS[i].kind == Kind.Recoupler) { Couplers.NewRecoupler(e); continue; }
                if (PREFABS[i].kind == Kind.Beacon) { Couplers.NewBeacon(e); continue; }
                if (PREFABS[i].kind == Kind.Levers) { Levers.Track(e, PREFABS[i].name); continue; }
                if (PREFABS[i].kind == Kind.TurretSeat) { NewTurret(e, PREFABS[i].name); continue; }   // 0.12.0: turret seat (0.12.2: no triggers)
                if (PREFABS[i].kind == Kind.Motor)
                {
                    int n = (int)PREFABS[i].stroke; bool fuel = n >= 3; n %= 3;
                    motors[key] = fuel ? new Motor { block = e, name = nameP, fuel = true, velMax = FuelMotorSpeed[n], impulse = FuelMotorImpulse[n], baseImpulse = FuelMotorImpulse[n], maxConsumption = FuelPower }
                                        : new Motor { block = e, name = nameP, velMax = MotorSpeed[n], impulse = MotorImpulse[n], maxConsumption = MotorConsumption[n] };
                    newMotor = true;
                    ModLog.Info(nameP + " found: block " + e.Index + ":" + e.Version);
                    continue;
                }
                var j = new Joint { block = e, kind = PREFABS[i].kind, stroke = PREFABS[i].stroke, strokeMax = PREFABS[i].stroke, name = nameP, state = 0, mirror = mir, spin = PREFABS[i].kind == Kind.Bearing && PREFABS[i].stroke == 1f, scale = ScaleFromName(PREFABS[i].name) };
                allJoints[key] = j;
                ModLog.Info(j.name + " found: block " + e.Index + ":" + e.Version);
                if (j.kind == Kind.Piston) RetractInGarage(em, j);   // 0.19.5 (Andre: "piston extended outside the simulation")
            }
            if (newMotor) RedoOutputMap();
        }

        static void RedoOutputMap() { motorByOutput.Clear(); foreach (var m in motors.Values) if (!Ecs.IsNull(m.output)) motorByOutput[Ecs.K(m.output)] = m; }

        // ================================================================ motor
        static void MotorStep(EntityManager em, Motor m)
        {
            if (!m.portsOk)
            {
                m.input = Ecs.PortOfType(em, m.block, Ecs.PortInput); m.output = Ecs.PortOfType(em, m.block, PortShaft);   // 0.15.0: Drive Shaft output (plasma port) m.power = Ecs.PortOfType(em, m.block, Ecs.PortPower);
                if (Ecs.IsNull(m.input) || Ecs.IsNull(m.output)) { if (m.warnings++ < 2) ModLog.Warn(m.name + " " + m.block.Index + ": ports not found (" + Ecs.Ports(em, m.block).Length + " in buffer)"); return; }
                m.portsOk = true; RedoOutputMap();
                // 0.11.0: brake = the input with the highest _index (4th port of the prefab); the signal input = the one with the lowest _index
                Entity ent = Entity.Null, fre = Entity.Null; int iEnt = 999, iFre = -1; var desc = new System.Text.StringBuilder();
                foreach (var p in Ecs.Ports(em, m.block))
                {
                    if (!Ecs.Port(em, p, out var t, out _, out _)) continue;
                    int ix = Ecs.PortIndex(em, p); desc.Append(' ').Append(p.Index).Append("(t").Append(t).Append(" i").Append(ix).Append(')');
                    if (t != Ecs.PortInput) continue;
                    if (ix < iEnt) { iEnt = ix; ent = p; }
                    if (ix > iFre) { iFre = ix; fre = p; }
                }
                if (!Ecs.IsNull(ent) && !Ecs.IsNull(fre) && (fre.Index != ent.Index || fre.Version != ent.Version)) { m.input = ent; m.brakePort = fre; }
                if (DetailedLog) ModLog.Info(m.name + " " + m.block.Index + ": ports input=" + m.input.Index + " power=" + m.power.Index + " output=" + m.output.Index + (Ecs.IsNull(m.brakePort) ? " (no brake port)" : " brake=" + m.brakePort.Index) + " |" + desc);
            }
            if (!Ecs.IsPart(em, Ecs.Parent(em, m.block))) return;   // garage
            float s = 0f;
            if (Ecs.ReadInput(em, m.input, out var v, out var source, out var trail)) s = System.Math.Max(-1f, System.Math.Min(1f, v));
            if (DetailedLog && trail != m.trail) { m.trail = trail; ModLog.Info(m.name + " " + m.block.Index + ": input" + trail); }
            // 0.11.0: brake (0..1, positive side only): while braking the motor neither accelerates nor spends (the bearings lock the wheel)
            float fr = 0f;
            if (!Ecs.IsNull(m.brakePort) && Ecs.ReadInput(em, m.brakePort, out var vf, out _, out _)) fr = System.Math.Max(0f, System.Math.Min(1f, vf));
            if ((fr > 0.02f) != m.brakeLog) { m.brakeLog = fr > 0.02f; ModLog.Info(m.name + " " + m.block.Index + ": brake " + (m.brakeLog ? "ENGAGED (" + fr.ToString("F2") + ")" : "released")); }
            m.brake = fr;
            if (fr > 0.02f) s = 0f;
            if (DetailedLog && System.Math.Abs(s - m.signalLog) >= 0.1f) { m.signalLog = s; ModLog.Info(m.name + " " + m.block.Index + ": signal=" + s.ToString("F2") + (m.powered ? "" : " (no power)")); }
            m.powered = Ecs.HasPower(em, m.block, out var state);
            if (m.fuel) s = Fuel(em, m, s, UnityEngine.Time.deltaTime);   // effective signal = what the fuel gave
            m.signal = s;
            Ecs.SetConsumption(em, m.block, System.Math.Max(IdleConsumption, System.Math.Abs(s) * m.maxConsumption));
            // 0.15.0: the output is a shaft (plasma): the mod carries the signal through the Drive Shafts, it doesn't write a value to the port
            if (DetailedLog && state != m.state) { m.state = state; ModLog.Info(m.name + " " + m.block.Index + ": power " + StateName(state)); }
        }
        /// <summary>Fuel motor (like the game's SCTick_Thruster_Fuel): wants FuelConsumption x |signal| x dt from the block's
        /// FuelData; takes whatever there is (the 3 parts of the mix in the same proportion); usage01 = what it got / what it would spend with signal 1;
        /// efficiency = FuelProperties.GetFuelPerformance(what it got, usage01, FuelComboSingleton). Effective signal = signal capped
        /// at usage01 (no fuel = 0, brakes); force = impulse x efficiency (rounded in 5% steps so the joint isn't redone
        /// every frame). Without a signal it spends nothing and holds with the force of the last efficiency.</summary>
        static TypeIndex tiFuel, tiCombo; static bool fuelOk, perfFailed; static EntityQuery qCombo;
        static unsafe float Fuel(EntityManager em, Motor m, float s, float dt)
        {
            try
            {
                if (!fuelOk)
                {
                    fuelOk = true;
                    tiFuel = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<FuelData>());
                    tiCombo = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<FuelComboSingleton>());
                    var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiCombo); qCombo = em.CreateEntityQuery(a);
                }
                if (!em.HasComponent(m.block, ComponentType.ReadOnly(tiFuel))) { if (m.fuelLog != "no FuelData") { m.fuelLog = "no FuelData"; ModLog.Warn(m.name + " " + m.block.Index + ": block has no FuelData (the game didn't connect it to the pipes?)"); } return 0f; }
                var fd = (Ecs.HasC(em, m.block, tiFuel) ? (byte*)em.GetComponentDataRawRW(m.block, tiFuel) : null);
                double* cur = (double*)(fd + 24);
                double sum = System.Math.Max(0.0, cur[0]) + System.Math.Max(0.0, cur[1]) + System.Math.Max(0.0, cur[2]);
                m.fuelHas = (float)sum;
                float abs = System.Math.Abs(s);
                if (abs < 1e-4f || dt <= 0f) { LogFuel(m, sum); return 0f; }
                double wants = FuelConsumption * abs * dt, grab = System.Math.Min(wants, sum);
                float usage = FuelConsumption <= 0f ? abs : (float)(grab / (FuelConsumption * dt));
                if (grab > 0.0 && sum > 0.0)
                {
                    // the game's Fuel = 3 doubles (24 B), but the Il2CppInterop proxy only has 8 B (a fixed buffer becomes 1 field): I build
                    // the 24 B by hand and call the game's method directly (il2cpp_runtime_invoke) with a pointer to them
                    double* t = stackalloc double[3]; double f = grab / sum;
                    for (int i = 0; i < 3; i++) { double v = System.Math.Max(0.0, cur[i]); t[i] = v * f; cur[i] = v - t[i]; }
                    float perf = 1f;
                    if (!perfFailed)
                    {
                        try
                        {
                            var list = Ecs.QueryList(qCombo);
                            if (list.Count > 0) { var combo = *(Ecs.HasC(em, list[0], tiCombo) ? (FuelComboSingleton*)em.GetComponentDataRawRO(list[0], tiCombo) : null); perf = Efficiency(t, System.Math.Min(1f, usage), &combo); }
                        }
                        catch (Exception ex) { perfFailed = true; ModLog.Warn("fuel motor: the game's efficiency calculation failed (using 100%): " + ex.Message); }
                    }
                    if (float.IsNaN(perf) || perf < 0f) perf = 0f;
                    m.efficiency = (float)System.Math.Round(System.Math.Min(2f, perf) * 20f) / 20f;
                }
                m.usage = usage;
                LogFuel(m, sum - grab);
                return System.Math.Sign(s) * System.Math.Min(abs, usage);
            }
            catch (Exception ex) { if (m.warnings++ < 5) ModLog.Warn(m.name + " " + m.block.Index + ": fuel: " + ex.Message); return 0f; }
        }
        static IntPtr miPerf;
        static unsafe float Efficiency(double* fuel24, float usage01, FuelComboSingleton* combo)
        {
            if (miPerf == IntPtr.Zero)
            {
                var fi = typeof(FuelProperties).GetField("NativeMethodInfoPtr_GetFuelPerformance_Public_Static_Single_Fuel_Single_byref_FuelComboSingleton_0", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (fi == null) throw new Exception("GetFuelPerformance method not found in the proxy");
                miPerf = (IntPtr)fi.GetValue(null);
                if (miPerf == IntPtr.Zero) throw new Exception("GetFuelPerformance has no pointer");
            }
            float u = usage01;
            IntPtr* args = stackalloc IntPtr[3];
            args[0] = (IntPtr)fuel24; args[1] = (IntPtr)(&u); args[2] = (IntPtr)combo;
            IntPtr exc = IntPtr.Zero;
            IntPtr r = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(miPerf, IntPtr.Zero, (void**)args, ref exc);
            if (exc != IntPtr.Zero || r == IntPtr.Zero) throw new Exception("exception in GetFuelPerformance");
            return *(float*)Il2CppInterop.Runtime.IL2CPP.il2cpp_object_unbox(r);
        }

        static void LogFuel(Motor m, double spare)
        {
            if (!DetailedLog) return;
            string st = spare <= 1e-6 ? "out of fuel" : "has fuel";
            if (st != m.fuelLog) { m.fuelLog = st; ModLog.Info(m.name + " " + m.block.Index + ": " + st + (spare > 1e-6 ? " (" + spare.ToString("F2") + " in the motor)" : " — connect it to a tank with a pipe")); }
            if (System.Math.Abs(m.efficiency - m.rendLog) >= 0.1f) { m.rendLog = m.efficiency; ModLog.Info(m.name + " " + m.block.Index + ": mixture efficiency " + (m.efficiency * 100f).ToString("F0") + "% -> force " + m.effectiveImpulse.ToString("F0")); }
        }

        public static string StateName(byte e) => e == 0 ? "no power" : e == 1 ? "insufficient power" : e == 2 ? "powered" : e == 255 ? "no SCElectricPower" : "?" + e;

        // ================================================================ piston and bearing: the top face releases (like the decoupler)
        static unsafe void WaitingTop(EntityManager em, Joint j)
        {
            var pa = Ecs.Parent(em, j.block);
            if (!Ecs.IsPart(em, pa)) { j.letGo = false; j.tLaunch = -1f; return; }   // still in the garage
            if (j.tLaunch < 0f) j.tLaunch = UnityEngine.Time.time;   // 0.26.12
            // 0.26.10 (Andre's log, 23/10: a refused bearing repeated "separating" every 6 s with the wheel 21 m away — each time it broke
            // the game's joints again, and the game rebuilt the parts of the whole ship): a piece that was let go stays loose until the garage / rewind
            if (j.letGo) return;
            if (j.tState < 0f) return;         // wait after a failure
            Ecs.Axes(em, j.block, out var ex, out var ey, out var ez);
            var pb = Ecs.Position(em, j.block);
            j.axis = ey; j.anchor = Ecs.Add(pb, Ecs.Mul(ey, j.middle));
            var joints = Ecs.Joints(em, out int n);
            if (joints == null)
            {
                // 0.20.2: tries again on the next frame (it was 1 s) and, meanwhile, holds the Piston End's piece together with the base
                if (j.warnings++ < 3) ModLog.Warn("SpaceshipSingleton._joints not available" + (j.kind == Kind.Piston ? " — retrying next frame" : ""));
                if (j.kind == Kind.Piston) HoldEnd(em, j, pa);
                j.tState = j.kind == Kind.Piston ? -0.001f : -1f; return;
            }
            int broken = 0; Entity nbr = Entity.Null; float bestPick = 99f;
            j.broken.Clear();
            var mineSet = TopJoints(em, j.block, joints, n, out nbr);
            if (mineSet.Count == 0 || Ecs.IsNull(nbr))
            {
                // 0.20.0: nothing glued to the top face — is there a Piston End aligned on the axis (from the garage pose)? Then the end (and whatever
                // is glued to it) was already born as ANOTHER part (the garage doesn't link the two): there's no joint to break, the motor is born now,
                // at the garage pose, with the rod stretched up to there (stroke = the distance, extBase).
                string reasonP = null;
                if (j.kind == Kind.Piston && Garage.FindEnd(em, j.block, j.middle, j.strokeMax, out var endBlock, out var gap, out reasonP))
                {
                    var pp = Ecs.Parent(em, endBlock);
                    if (!Ecs.IsPart(em, pp)) { j.tState = -0.001f; return; }   // the end hasn't become a part yet: try on the next frame (0.20.1: it was 0.5 s)
                    HoldPart(em, pp, pa);   // 0.20.2: the end's piece is born with the base's velocity (without falling) until the motor exists
                    if (pp.Index == pa.Index)
                    {
                        // 0.20.3 (Andre's log: on the 1st launch the end was in the same part — the car's bearings hadn't separated yet):
                        // waits up to 3 s, per frame, for the game to rebuild the parts after the mod's other joints break; only then is it "welded"
                        j.tEnd += UnityEngine.Time.deltaTime;
                        if (j.tEnd < 3f)
                        {
                            if (!j.endWaitLog) { j.endWaitLog = true; ModLog.Info(j.name + " " + j.block.Index + ": Piston End " + endBlock.Index + " found " + gap.ToString("F2") + " m ahead, still in the same part as the base — waiting for the game to split the parts (other joints breaking)"); }
                            j.tState = -0.001f; return;
                        }
                        j.welded = true;
                        if (!j.didWarnStuck) { j.didWarnStuck = true; ModLog.Warn(j.name + " " + j.block.Index + ": Piston End " + endBlock.Index + " found " + gap.ToString("F2") + " m ahead, but after 3 s it is still in the SAME part as the piston's base (attached to the base through another path) — not separating. Retrying in 30 s."); }
                        j.tEnd = 0f; j.endWaitLog = false; j.tState = -30f; return;
                    }
                    j.tEnd = 0f; j.endWaitLog = false;
                    j.didWarnStuck = false; j.welded = false; j.broken.Clear();
                    j.neighbor = endBlock; j.endBlock = endBlock; j.withEnd = true; j.partA = pa; j.partB = pp; j.extBase = gap; j.stroke = gap; j.locNeighbor = -1;
                    deferred = false;
                    if (Create(em, j)) { j.state = 2; j.tState = 0f; Reinforce(em, j); ModLog.Info(j.name + " " + j.block.Index + ": Piston End " + endBlock.Index + " at " + gap.ToString("F2") + " m — stroke = " + gap.ToString("F2") + " m, rod born extended (part " + pa.Index + " -> part " + pp.Index + ")"); }
                    else if (deferred) { j.partA = Entity.Null; j.partB = Entity.Null; j.tState = 0f; }   // try on the next frame
                    else { j.tState = -5f; }
                    return;
                }
                if (j.warnings++ < 3) ModLog.Warn(j.name + " " + j.block.Index + ": nothing attached to the top face" + (j.kind == Kind.Piston ? " and no Piston End on its axis" + (reasonP != null ? " (" + reasonP + ")" : "") : "") + " — nothing to do (retrying in 30 s). Its joints:" + ListJoints(em, j, joints, n, pb, ey));
                j.tState = -30f; return;
            }
            if (j.withEnd) { j.withEnd = false; j.extBase = 0f; j.stroke = j.strokeMax; j.endBlock = Entity.Null; }
            // 0.11.1: if the upper block is still linked to the base through ANOTHER path (a piece glued on both sides, or the structure
            // closes a ring around the piston), breaking the joints separates nothing — the ship doesn't become two parts, and each
            // attempt (every 5 s) made the game rebuild all the parts, messing up the other pistons/bearings. The top-face joints
            // of ALL the mod's joints in this part count as "will break" (pistons side by side).
            var willBreak = new HashSet<int>(mineSet);
            foreach (var o in allJoints.Values)
                if (o != j && o.state == 0 && Ecs.Alive(em, o.block) && Same(Ecs.Parent(em, o.block), pa))
                    foreach (var k in TopJoints(em, o.block, joints, n, out _)) willBreak.Add(k);
            if (ConnectedOtherWay(em, joints, n, nbr, j.block, willBreak, out int viaN))
            {
                j.welded = true;
                if (!j.didWarnStuck) { j.didWarnStuck = true; ModLog.Warn(j.name + " " + j.block.Index + ": the top block (" + nbr.Index + ") is still attached to the base through another path (" + viaN + " joints on the path) — not separating. Detach the top part from the rest of the structure (only the " + (j.kind == Kind.Piston ? "piston" : "bearing") + " may connect the two sides). Retrying in 30 s."); }
                j.tState = -30f; return;
            }
            // 0.11.2: no RING of joints. Andre launched 4 pistons linking the same base to the same top through two paths
            // (base -> P1 -> A -> P2 -> top <- P3 <- B <- P4 <- base): physics doesn't close the ring (each piston pulls its own
            // way), within 2 s the fits were meters/km apart and the game rebuilt the parts 6 times. Rule: the mod's joints
            // form a TREE — the one that would close a ring stays welded (doesn't become a joint) and warns.
            PlanRings(em, joints, n);
            j.rail = becomesRail.Contains(Ecs.K(j.block));
            if (j.rail && !j.didWarnStuck) ModLog.Info(j.name + " " + j.block.Index + ": closes a RING of joints — " + (j.kind == Kind.Piston ? "keeps pushing with its own signal, with rails (yellow LED)" : "stays FREE (only spins)"));
            if (closesRing.Contains(Ecs.K(j.block)))
            {
                j.welded = true;
                if (!j.didWarnStuck) { j.didWarnStuck = true; ModLog.Warn(j.name + " " + j.block.Index + ": this " + (j.kind == Kind.Piston ? "piston" : "bearing") + " would close a RING of joints (the top block " + nbr.Index + " is already connected to the base by other pistons/bearings) — stays WELDED, it won't become a joint. The mod only links parts as a tree: each part attached to the base by ONE path of joints. Retrying in 30 s."); }
                j.tState = -30f; return;
            }
            j.didWarnStuck = false; j.welded = false;
            // 0.26.1 (Andre: "at the start of the simulation the joints break ... things fall into each other"): NOW, with everything still
            // rigid in a single part, the pose of the upper block relative to the piston is the build pose (the correct one). We keep it to hold
            // the piece at that pose during the window in which it is already released but the game hasn't finished splitting the parts — otherwise it falls
            // freely and the weld is born with that offset (that was the source of the 28 cm of side error the piston fought to close).
            try { Ecs.Pose(em, j.block, out var pP0, out var rP0); Ecs.Pose(em, nbr, out var pV0, out var rV0);
                  j.poseRelNb = math.mul(math.inverse(new RigidTransform(rP0, pP0)), new RigidTransform(rV0, pV0)); j.poseRelNbOk = true; }
            catch (Exception) { j.poseRelNbOk = false; }
            if (StayWhole(em, j, pa, nbr)) return;   // 0.26.4: a piston without a target doesn't separate at launch
            // 0.26.12 (Andre's log, 23/10: on the 1st launch after loading, the game ran 18 physics ticks in a 677 ms frame right
            // after the car's bearings broke the game's joints and BEFORE the mod created its own — the pieces were loose and under
            // gravity for 0.3 s and the car scattered). The launch zero gravity was only requested by the pistons, and only on the next
            // frame. Now it applies to every just-launched joint and kicks in before the next physics.
            if (JustLaunchedJ(j)) { RequestZeroGrav(); try { GravityControl(em); } catch (Exception) { } }
            foreach (var k in mineSet) { Break(j, &joints[k], k); broken++; }
            j.neighbor = nbr; j.partA = pa; j.state = 1; j.tState = 0f;
            ModLog.Info(j.name + " " + j.block.Index + ": separating (" + broken + " joint(s), neighbor " + nbr.Index + ")");
        }

        /// <summary>0.20.2: while the motor of the piston with a Piston End doesn't exist, the end's part moves together with the base (velocity copied,
        /// spin zeroed) — instead of falling until the joint is born.</summary>
        static void HoldEnd(EntityManager em, Joint j, Entity pa)
        {
            try
            {
                if (!Garage.FindEnd(em, j.block, j.middle, j.strokeMax, out var endBlock, out _, out _)) return;
                var pp = Ecs.Parent(em, endBlock); if (!Ecs.IsPart(em, pp) || pp.Index == pa.Index) return;
                HoldPart(em, pp, pa);
            }
            catch (Exception) { }
        }
        static unsafe void HoldPart(EntityManager em, Entity piece, Entity base_)
        {
            try
            {
                var va = (Ecs.HasC(em, base_, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(base_, Ecs.tiVel) : null);
                var vb = (Ecs.HasC(em, piece, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRW(piece, Ecs.tiVel) : null);
                if (va != null && vb != null) { vb->Linear = va->Linear; vb->Angular = float3.zero; }
            }
            catch (Exception) { }
        }

        /// <summary>0.26.1: during the separation window (piece already released, parts not split yet) holds the upper block at the pose it
        /// had when the mod broke the joints — relative to the piston, with the base's velocity. Prevents the free fall that made the weld
        /// be born crooked. Same treatment as the turret (HoldSeparating).</summary>
        static unsafe void HoldSeparatingPiston(EntityManager em, Joint j)
        {
            if (!j.poseRelNbOk || Ecs.IsNull(j.neighbor) || !Ecs.Alive(em, j.neighbor)) return;
            var pa = Ecs.Parent(em, j.block); var pv = Ecs.Parent(em, j.neighbor);
            if (!Ecs.IsPart(em, pa) || !Ecs.IsPart(em, pv) || pv.Index == pa.Index) return;   // not separated yet: the game still rules it
            try
            {
                Ecs.Pose(em, j.block, out var pP, out var rP);
                var targetBlock = math.mul(new RigidTransform(rP, pP), j.poseRelNb);          // where the upper block has to be, in the world
                var lt = Ecs.LT(em, j.neighbor);                                              // the block inside its part
                var targetPart = math.mul(targetBlock, math.inverse(new RigidTransform(lt.Rotation, lt.Position)));
                var pp = (Ecs.HasC(em, pv, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(pv, Ecs.tiLT) : null);
                if (pp != null) { pp->Position = targetPart.pos; pp->Rotation = targetPart.rot; }
                HoldPart(em, pv, pa);
            }
            catch (Exception) { }
        }

        // ---- 0.12.2: with the ship moving, rings open and close (the recoupler grabs/releases the car): once per second redo who stays free.
        // Graph of PARTS: locked recouplers and turrets first, then the active joints (the ones already pushing first, then by
        // index); the one linking two parts that are already linked closes a ring -> free. Whatever changed gets its joint redone right away, at the current pose.
        static float tReplan;
        static readonly Dictionary<long, long> ufPart = new Dictionary<long, long>();
        static long RootP(long k) { while (ufPart.TryGetValue(k, out var p) && p != k) k = p; return k; }
        static bool UnionP(long a, long b) { a = RootP(a); b = RootP(b); if (!ufPart.ContainsKey(a)) ufPart[a] = a; if (!ufPart.ContainsKey(b)) ufPart[b] = b; if (a == b) return false; ufPart[a] = b; return true; }
        static void ReplanFree(EntityManager em)
        {
            ufPart.Clear();
            foreach (var r in Couplers.Locked()) if (!Ecs.IsNull(r.partA) && !Ecs.IsNull(r.partB)) UnionP(Ecs.K(r.partA), Ecs.K(r.partB));
            foreach (var (ta, tb) in TurretLinks()) UnionP(Ecs.K(ta), Ecs.K(tb));
            var actives = new List<Joint>();
            foreach (var j in allJoints.Values) if (j.state == 2 && (j.kind == Kind.Piston || j.kind == Kind.Bearing) && !Ecs.IsNull(j.partA) && !Ecs.IsNull(j.partB) && j.tStart <= 0f) actives.Add(j);
            actives.Sort((a, b) => a.rail != b.rail ? (a.rail ? 1 : -1) : a.block.Index.CompareTo(b.block.Index));
            foreach (var j in actives)
            {
                bool free = !UnionP(Ecs.K(j.partA), Ecs.K(j.partB));
                if (free == j.rail) continue;
                j.rail = free;
                ModLog.Info(j.name + " " + j.block.Index + ": " + (j.kind == Kind.Piston ? (free ? "the ring closed (yellow LED, keeps pushing)" : "the ring opened (green LED)") : (free ? "the ring closed — stays FREE" : "the ring opened — pushing again")));
                if (j.kind != Kind.Piston) RedoJoint(em, j);   // 0.12.3: a ring's piston only changes the LED
            }
        }
        static void RedoJoint(EntityManager em, Joint j)
        {
            try { if (Ecs.Alive(em, j.joint)) em.DestroyEntity(j.joint); } catch (Exception) { }
            try { if (Ecs.Alive(em, j.joint2)) em.DestroyEntity(j.joint2); } catch (Exception) { }
            try { if (Ecs.Alive(em, j.joint3)) em.DestroyEntity(j.joint3); } catch (Exception) { }
            j.joint = Entity.Null; j.joint2 = Entity.Null; j.joint3 = Entity.Null; DeleteRails(em, j);
            if (j.kind == Kind.Bearing) { j.mode = -1; j.impulseMode = 0f; }
            deferred = false;
            if (!Create(em, j, true)) { Undo(em, j, "rebuilding the joint (ring) failed"); j.state = 0; j.tState = -1f; }
        }

        // ---- 0.17.0: the bearing's red LED (base panels +Z/-Z, x = +-0.12): hidden 2 cm inwards; comes out when its shaft network
        // is LOCKED (opposite signals)
        const float BLedX = 0.12f, BLedY = -0.16f, BLedRestZ = 0.224f, BLedOut = 0.023f;
        static unsafe void LedBearing(EntityManager em, Joint j, bool lit, float dt)
        {
            // 0.20.0: Mini Bearing — LED at (0.065, -0.08), depth 0.112, comes out 0.0115 (modelos_v2 BEARING_LED_S)
            float BLedX = j.scale < 1f ? 0.065f : JointSystem.BLedX, BLedY = j.scale < 1f ? -0.08f : JointSystem.BLedY, BLedRestZ = JointSystem.BLedRestZ * j.scale, BLedOut = JointSystem.BLedOut * j.scale;
            try
            {
                j.tLeds += dt;
                if (j.leds == null || j.tLeds >= 5f)
                {
                    j.tLeds = 0f; var l = new List<Entity>(); var r = new List<float3>();
                    foreach (var f in Ecs.Children(em, j.block))
                    {
                        var p = Ecs.LT(em, f).Position;
                        if (System.Math.Abs(System.Math.Abs(p.x) - BLedX) > 0.003f || System.Math.Abs(p.y - BLedY) > 0.003f) continue;
                        float az = System.Math.Abs(p.z);
                        if (System.Math.Abs(az - BLedRestZ) > 0.003f && System.Math.Abs(az - BLedRestZ - BLedOut) > 0.003f) continue;
                        l.Add(f); r.Add(new float3 { x = p.x, y = p.y, z = p.z < 0f ? -BLedRestZ : BLedRestZ });
                    }
                    bool first = j.leds == null; j.leds = l.ToArray(); j.ledRest = r.ToArray();
                    if (first) { j.ledState = -2; if (l.Count == 0 && lit) ModLog.Warn(j.name + " " + j.block.Index + ": bearing LEDs not found (rebuild the bundle in the Modkit)"); }
                }
                int est = lit ? 1 : 0; if (est == j.ledState) return;
                j.ledState = est;
                for (int i = 0; i < j.leds.Length; i++)
                {
                    var e = j.leds[i]; if (!Ecs.Alive(em, e)) continue;
                    var lt = (Ecs.HasC(em, e, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(e, Ecs.tiLT) : null); if (lt == null) continue;
                    var p = j.ledRest[i];
                    lt->Position = new float3 { x = p.x, y = p.y, z = lit ? p.z + (p.z < 0f ? -BLedOut : BLedOut) : p.z };
                }
            }
            catch (Exception ex) { if (j.warnings++ < 3) ModLog.Warn(j.name + " " + j.block.Index + ": LED: " + ex.Message); }
        }

        // ---- 0.12.1: piston state LEDs (extra renderers hidden in the +Z panel; the current state's one comes out 2.3 cm)
        const float LedY = -0.135f, LedZ = 0.224f, LedOut = 0.023f;
        static readonly float[] LedXs = { -0.09f, 0f, 0.09f };   // green, yellow, red
        static readonly float[] LedXsMini = { -0.01f, 0.035f, 0.08f }, LedXsMiniM = { 0.01f, -0.035f, -0.08f };   // 0.20.0: Mini Piston (modelos_v2 PISTON_LED_XS[0.5]); 0.20.3: M version
        static unsafe void PistonLeds(EntityManager em, Joint j, float dt)
        {
            float[] LedXs = j.scale < 1f ? (j.mirror ? LedXsMiniM : LedXsMini) : JointSystem.LedXs; float LedY = JointSystem.LedY * j.scale, LedZ = JointSystem.LedZ * j.scale, LedOut = JointSystem.LedOut * j.scale;   // 0.20.3: M = mirrored x
            try
            {
                bool onShip = Ecs.IsPart(em, Ecs.Parent(em, j.block));
                int est = !onShip ? -1 : j.state == 2 || j.state == 4 ? (j.rail ? 1 : 0) : j.welded ? 2 : j.cReady ? 0 : -1;   // 0.28.0: cReady = drawn on a client (green; the server's yellow/red aren't sent)
                j.tLeds += dt;
                if (j.leds == null || j.tLeds >= 5f)
                {
                    j.tLeds = 0f; var l = new Entity[3]; int found = 0;
                    foreach (var f in Ecs.Children(em, j.block))
                    {
                        var p = Ecs.LT(em, f).Position;
                        if (System.Math.Abs(p.y - LedY) > 0.003f || (System.Math.Abs(p.z - LedZ) > 0.003f && System.Math.Abs(p.z - LedZ - LedOut) > 0.003f)) continue;
                        for (int i = 0; i < 3; i++) if (System.Math.Abs(p.x - LedXs[i]) < 0.003f) { l[i] = f; found++; }
                    }
                    bool first = j.leds == null; j.leds = l;
                    if (first && found < 3 && onShip) ModLog.Warn(j.name + " " + j.block.Index + ": piston LEDs not found (" + found + "/3) — rebuild the bundle in the Modkit");
                    if (first) j.ledState = -2;
                }
                if (est == j.ledState) return;
                j.ledState = est;
                for (int i = 0; i < 3; i++)
                {
                    var e = j.leds[i]; if (Ecs.IsNull(e) || !Ecs.Alive(em, e)) continue;
                    var lt = (Ecs.HasC(em, e, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(e, Ecs.tiLT) : null); if (lt == null) continue;
                    lt->Position = new float3 { x = LedXs[i], y = LedY, z = LedZ + (i == est ? LedOut : 0f) };
                }
                if (onShip) ModLog.Info(j.name + " " + j.block.Index + ": LED " + (est == 0 ? "GREEN (active)" : est == 1 ? "YELLOW (guided)" : est == 2 ? "RED (welded)" : "off"));
            }
            catch (Exception ex) { if (j.warnings++ < 3) ModLog.Warn(j.name + " " + j.block.Index + ": LEDs: " + ex.Message); }
        }

        // ---- ring plan (0.11.2), once per frame: rigid groups = blocks linked by the game's joints WITHOUT the top joints
        // of the candidate joints (state 0, in flight); each joint is an edge between its block's group and the
        // upper block's group; Kruskal (the ones already separated first, then the candidates by entity index): the candidate
        // whose edge links two groups that are ALREADY linked would close a ring -> stays welded.
        static int ringFrame = -1; static readonly HashSet<long> closesRing = new HashSet<long>(), becomesRail = new HashSet<long>();
        static readonly Dictionary<long, long> ufBlock = new Dictionary<long, long>();
        static long RootB(long k) { while (ufBlock.TryGetValue(k, out var p) && p != k) k = p; return k; }
        static void UnionB(long a, long b) { a = RootB(a); b = RootB(b); if (!ufBlock.ContainsKey(a)) ufBlock[a] = a; if (!ufBlock.ContainsKey(b)) ufBlock[b] = b; if (a != b) ufBlock[a] = b; }
        static unsafe void PlanRings(EntityManager em, SpaceshipJoint* joints, int n)
        {
            if (ringFrame == UnityEngine.Time.frameCount) return; ringFrame = UnityEngine.Time.frameCount;
            closesRing.Clear(); becomesRail.Clear(); ufBlock.Clear();
            var cand = new List<(Joint j, Entity nbr)>(); var skip = new HashSet<int>();
            var doneOnes = new List<Joint>();
            foreach (var o in allJoints.Values)
            {
                if (!Ecs.Alive(em, o.block) || !Ecs.IsPart(em, Ecs.Parent(em, o.block))) continue;
                if (o.state == 0) { var ks = TopJoints(em, o.block, joints, n, out var v); if (ks.Count > 0 && !Ecs.IsNull(v)) { cand.Add((o, v)); foreach (var k in ks) skip.Add(k); } }
                else if (!Ecs.IsNull(o.neighbor) && Ecs.Alive(em, o.neighbor)) doneOnes.Add(o);
            }
            if (cand.Count == 0) return;
            for (int k = 0; k < n; k++)
            {
                if (skip.Contains(k)) continue;
                var sj = &joints[k];
                if (sj->_breakForce < 0f) continue;   // already broken by us
                UnionB(Ecs.K(sj->_a), Ecs.K(sj->_b));
            }
            // 0.12.1: rigid groups BEFORE the joints (to find parallel pistons between the same two pieces)
            var g0 = new Dictionary<long, (long a, long b)>();
            foreach (var o in doneOnes) g0[Ecs.K(o.block)] = (RootB(Ecs.K(o.block)), RootB(Ecs.K(o.neighbor)));
            foreach (var (o, v) in cand) g0[Ecs.K(o.block)] = (RootB(Ecs.K(o.block)), RootB(Ecs.K(v)));
            var accepted = new List<Joint>();
            doneOnes.Sort((a, b) => a.block.Index.CompareTo(b.block.Index));
            cand.Sort((a, b) => a.j.block.Index.CompareTo(b.j.block.Index));
            foreach (var o in doneOnes) { UnionB(Ecs.K(o.block), Ecs.K(o.neighbor)); if (!o.rail) accepted.Add(o); }
            foreach (var (o, v) in cand)
            {
                long ga = RootB(Ecs.K(o.block)), gb = RootB(Ecs.K(v));
                if (ga != gb) { UnionB(ga, gb); accepted.Add(o); continue; }
                // would close a ring: if it's a piston PARALLEL (same direction) to another one linking the SAME two pieces, it becomes a rail
                bool rail = false;
                if (o.kind == Kind.Piston && g0.TryGetValue(Ecs.K(o.block), out var go))
                {
                    Ecs.Axes(em, o.block, out _, out var eyO, out _);
                    foreach (var a in accepted)
                    {
                        if (a.kind != Kind.Piston || !g0.TryGetValue(Ecs.K(a.block), out var ga0)) continue;
                        bool sameSet = (ga0.a == go.a && ga0.b == go.b);
                        if (!sameSet) continue;
                        Ecs.Axes(em, a.block, out _, out var eyA, out _);
                        if (Ecs.Dot(eyO, eyA) > 0.98f && System.Math.Abs(a.stroke - o.stroke) < 0.01f) { rail = true; break; }
                    }
                }
                // 0.12.2 (Andre, winch in a ring): instead of welding, the joint that closes the ring stays FREE — a piston becomes a rail (only slides),
                // a bearing stays loose (only turns). The others in the ring push; nothing fights and nothing locks.
                becomesRail.Add(Ecs.K(o.block));
            }
        }

        /// <summary>Indices of the game joints between the block and whatever is glued to its top face (+Y of the block); nbr = the
        /// nearest upper block.</summary>
        static unsafe List<int> TopJoints(EntityManager em, Entity block, SpaceshipJoint* joints, int n, out Entity nbr)
        {
            var r = new List<int>(); nbr = Entity.Null; float bestPick = 99f;
            Ecs.Axes(em, block, out _, out var ey, out _); var pb = Ecs.Position(em, block);
            // 0.26.4 (Andre's log: a foot that sank 1 m moved to the OTHER side of the piston and the top face's joint "vanished"): the face
            // of a joint is GARAGE geometry, not where the pieces are now. With GarageTransform on both sides, decide by it.
            RigidTransform gB = default; bool hasGB = false;
            try { if (!tiGTok) { tiGTok = true; tiGT = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<GarageTransform>()); }
                  if (em.HasComponent(block, ComponentType.ReadOnly(tiGT))) { gB = *(RigidTransform*)em.GetComponentDataRawRO(block, tiGT); hasGB = true; } } catch (Exception) { }
            for (int k = 0; k < n; k++)
            {
                var sj = &joints[k];
                bool a = Same(sj->_a, block), b = Same(sj->_b, block);
                if (!a && !b) continue;
                var other = a ? sj->_b : sj->_a;
                float3 po; try { if (!Ecs.Alive(em, other)) continue; po = Ecs.Position(em, other); } catch (Exception) { continue; }
                if (hasGB && em.HasComponent(other, ComponentType.ReadOnly(tiGT)))
                {
                    var gO = *(RigidTransform*)em.GetComponentDataRawRO(other, tiGT);
                    var dg = Ecs.Sub(gO.pos, gB.pos); float cg = Ecs.Len(dg); if (cg < 1e-4f) continue;
                    if (Ecs.Dot(dg, Ecs.Rot(gB.rot, Ecs.UY)) / cg < 0.5f) continue;   // joint of another face (in the garage)
                    r.Add(k);
                    if (cg < bestPick) { bestPick = cg; nbr = other; }
                    continue;
                }
                var d = Ecs.Sub(po, pb); float comp = Ecs.Len(d); if (comp < 1e-4f) continue;
                if (Ecs.Dot(d, ey) / comp < 0.5f) continue;   // joint of another face
                r.Add(k);
                if (comp < bestPick) { bestPick = comp; nbr = other; }
            }
            return r;
        }

        /// <summary>Breadth-first search through the game's joints (except the ones that will break): can we get from block 'srcE' to 'until'?</summary>
        static unsafe bool ConnectedOtherWay(EntityManager em, SpaceshipJoint* joints, int n, Entity srcE, Entity until, HashSet<int> skip, out int steps)
        {
            steps = 0;
            var nbr = new Dictionary<long, List<long>>();
            for (int k = 0; k < n; k++)
            {
                if (skip.Contains(k)) continue;
                var sj = &joints[k];
                if (sj->_breakForce < 0f) continue;   // already broken (by us)
                long ka = Ecs.K(sj->_a), kb = Ecs.K(sj->_b);
                if (!nbr.TryGetValue(ka, out var la)) nbr[ka] = la = new List<long>(); la.Add(kb);
                if (!nbr.TryGetValue(kb, out var lb)) nbr[kb] = lb = new List<long>(); lb.Add(ka);
            }
            long target = Ecs.K(until); var queue = new Queue<(long, int)>(); var seen = new HashSet<long> { Ecs.K(srcE) };
            queue.Enqueue((Ecs.K(srcE), 0));
            while (queue.Count > 0)
            {
                var (cur, d) = queue.Dequeue();
                if (cur == target) { steps = d; return true; }
                if (!nbr.TryGetValue(cur, out var ls)) continue;
                foreach (var nx in ls) if (seen.Add(nx)) queue.Enqueue((nx, d + 1));
            }
            return false;
        }

        static unsafe void ActivePiston(EntityManager em, Joint j, float dt)
        {
            Watch(em, j);
            if (!Valid(em, j)) { if (Rebuild(em, j)) return; Undo(em, j, "part/joint changed (time rewind, load or break)"); j.state = 0; j.tState = -1f; return; }
            Alignment(em, j, dt);
            try { PistonVisual(em, j, dt); } catch (Exception ex) { if (j.warnings++ < 3) ModLog.Warn(j.name + " " + j.block.Index + ": rod visual: " + ex.Message); }
            // 0.12.3 (Andre: "the yellow is weak and doesn't update along"): the piston that closes the ring also pushes with its own signal
            if (j.tStart > 0f) return;   // soft start: holds where it was born until the queue releases it (0.7.12)
            Payload(em, j);
            if (!Rigid && PistonTiltMax > 0f) Tilted(em, j, dt);   // 0.26.0: on the weld the collar plays this role, without giving up or locking the piston
            float signal = 0f; string reason = null; bool has = false;
            var pe = Ecs.PortOfType(em, j.block, Ecs.PortInput);
            if (Ecs.IsNull(pe)) reason = "no input port";
            else { has = Ecs.ReadInput(em, pe, out signal, out _, out var trail); if (!has) reason = "nothing connected to the input"; if (DetailedLog && trail != j.modeOrigin) { j.modeOrigin = trail; ModLog.Info(j.name + " " + j.block.Index + ": input" + trail); } }
            LogSignal(em, j, has, signal, reason, dt);
            bool power = Ecs.HasPower(em, j.block, out var state);
            if (DetailedLog && state != j.powerState) { j.powerState = state; ModLog.Info(j.name + " " + j.block.Index + ": power " + StateName(state)); }
            float request = has ? System.Math.Max(0f, System.Math.Min(1f, signal)) * j.stroke * j.strokeFraction : j.currentTarget;   // no signal: stays where it is (0.19.2: x the stroke knob)
            // 0.24.0: gave up = the target becomes where the rod is (zero error, no push). It comes back into effect as soon as the player
            // asks for SOMETHING ELSE — otherwise the piston would be useless after getting stuck once.
            if (j.tilted && System.Math.Abs(request - j.gaveUpTarget) > 1e-3f) { j.tilted = false; j.tTiltAcc = 0f; ModLog.Info(j.name + " " + j.block.Index + ": new target — the motor is pushing again"); }
            if (j.tilted) request = j.measuredStroke;
            bool wants = System.Math.Abs(request - j.currentTarget) > 1e-4f;
            // 0.23.0 (Andre: "now all pistons have the maximum stroke ... I was only using 1..2 m and it still consumed 20 P/s"):
            // the consumption follows the stroke the piston REALLY makes (distance to the Piston End, or the maximum, x the knob's fraction)
            Ecs.SetConsumption(em, j.block, wants && !j.tilted ? PistonConsumptionPerM * j.stroke * j.strokeFraction : IdleConsumption);
            if (MaybeMerge(em, j, wants, dt)) return;   // 0.26.3: still = becomes a single part with the lower one
            if (!wants || !power) return;   // no power: holds where it is
            float step = PistonSpeed * dt;
            float target = request > j.currentTarget + step ? j.currentTarget + step : request < j.currentTarget - step ? j.currentTarget - step : request;
            // 0.26.0b COLLAR (redone). The first version PULLED the command behind the measurement: with a damper below, the measured stroke
            // wobbles, the command wobbled along and I rewrote the weld at that length every frame — that is, the mod drove the
            // piston with the damper's oscillation (positive feedback: the "dancing" came back). Now the COMMAND IS SOVEREIGN and the measurement
            // only decides whether it MOVES or WAITS: stuck (the rod fell more than RigidSlack behind in the direction it is going),
            // the command holds where it is; released, it continues. The measurement never changes the command's value.
            if (Rigid && j.measuredStrokeOk)
            {
                float lag = target - j.measuredStroke;
                if (lag > RigidSlack && target > j.currentTarget) target = j.currentTarget;        // extending and the rod doesn't follow
                else if (lag < -RigidSlack && target < j.currentTarget) target = j.currentTarget;  // retracting and the rod doesn't follow
            }
            // 0.26.0b: a STILL weld injects no energy — it's only rewritten when the commanded length really changes. While it
            // doesn't change, the joint is just any passive constraint and whatever hangs from it settles by itself.
            if (Rigid && System.Math.Abs(target - j.currentTarget) < 1e-5f) return;
            j.currentTarget = target;
            var pj = (Ecs.HasC(em, j.joint, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(j.joint, Ecs.tiJoint) : null);
            if (pj == null) return;
            if (Rigid) Weld(em, j.partA, j.fa, j.fb, target - j.strokeBase, pj);
            else
            {
                Constraint* c = ConstraintAt(pj, j.idxMotor);
                c->Target.x = -(target - j.strokeBase);   // position motor: -x stretches x from where the frames coincide
                pj->m_Version++;
            }
            if (!Ecs.IsNull(j.extra) && Ecs.Alive(em, j.extra) && (j.idxExtra >= 0 || j.extraWeld))
            {
                var px = (Ecs.HasC(em, j.extra, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(j.extra, Ecs.tiJoint) : null);
                if (px != null)
                {
                    if (j.extraWeld) Weld(em, j.partA, j.faExtra, j.fbExtra, target - j.strokeBaseExtra, px);
                    else { ConstraintAt(px, j.idxExtra)->Target.x = -(target - j.strokeBaseExtra); px->m_Version++; }
                }
            }
        }

        // 0.11.0: a rod that stretches and a head that rises. In the prefab the head and the rod are separate EPC_Renderers (children of the block,
        // they become entities with LocalTransform relative to the block): head at (0, 0.225, 0); rod = 1 m cylinder at (0, 0.105, 0)
        // with y scale 0.095 (PostTransformMatrix). Here: head.y = 0.225 + stroke, rod y scale = 0.095 + stroke.
        const float HeadY = 0.225f, RodY = 0.105f, RodRest = 0.095f;
        internal static float HeadYOf(float scale) => HeadY * scale;
        /// <summary>0.20.0: finds the rod, head and guide bar (children on the block's axis): rod at y = 0.105 x scale; guide at y = 0.25 x scale
        /// (top face, tiny y scale at rest); head = any other height above rest. Works in the garage and in the simulation.</summary>
        internal static unsafe void FindPistonParts(EntityManager em, Entity block, float scale, float strokeMax, out Entity rod, out Entity head, out Entity rail, System.Text.StringBuilder sb = null)
        {
            rod = Entity.Null; head = Entity.Null; rail = Entity.Null;
            foreach (var f in Ecs.Children(em, block))
            {
                var lt = Ecs.LT(em, f); var p = lt.Position;
                if (sb != null) sb.Append("\n    ").Append(f.Index).Append(" at (").Append(Ecs.F3(p)).Append(") scale ").Append(lt.Scale.ToString("F3")).Append(Ecs.HasPTM(em, f) ? " [PTM]" : "").Append(": ").Append(Ecs.Components(em, f));
                if (System.Math.Abs(p.x) > 1e-3f || System.Math.Abs(p.z) > 1e-3f) continue;
                // 0.19.5: the head may be raised (it stayed extended from a previous simulation): accepts any height above rest
                // 0.19.14 (Andre, elevator: "the piston doesn't have its rod"): in the M version the head ALSO has PostTransformMatrix
                // (the mirror is scale -1 in X on every renderer) — the "no PTM" test never found the M piston's head
                // and the visual (head rises, rod stretches) stayed at rest. Rod = y 0.105; head = any other one on the axis.
                if (System.Math.Abs(p.y - RodY * scale) < 1e-3f) rod = f;
                else if (System.Math.Abs(p.y - 0.25f * scale) < 1e-3f && Ecs.HasPTM(em, f)) rail = f;   // 0.20.0 (only the single/mini piston has it)
                else if (p.y >= HeadY * scale - 1e-3f && p.y <= HeadY * scale + strokeMax + 0.6f) head = f;
            }
        }
        static unsafe void PistonVisual(EntityManager em, Joint j, float dt)
        {
            if (Ecs.IsNull(j.head) || !Ecs.Alive(em, j.head) || Ecs.IsNull(j.rod) || !Ecs.Alive(em, j.rod))
            {
                j.tVisual += dt;
                if (j.visualAttempts >= 5 || (j.visualAttempts > 0 && j.tVisual < 2f)) return;
                j.visualAttempts++; j.tVisual = 0f;
                var sb = DetailedLog ? new System.Text.StringBuilder() : null;
                FindPistonParts(em, j.block, j.scale, j.strokeMax, out j.rod, out j.head, out _, sb);
                if (sb == null) sb = new System.Text.StringBuilder();
                if (Ecs.IsNull(j.head) || Ecs.IsNull(j.rod))
                {
                    if (j.visualAttempts == 1 || j.visualAttempts == 5) ModLog.Warn(j.name + " " + j.block.Index + ": head/rod renderers not found (attempt " + j.visualAttempts + ") — block children:" + (sb.Length > 0 ? sb.ToString() : " none"));
                    return;
                }
                if (!Ecs.HasPTM(em, j.rod))
                {
                    ModLog.Warn(j.name + " " + j.block.Index + ": the rod has no PostTransformMatrix — it can't be stretched (only the head moves). Children:" + sb);
                }
                else ModLog.Info(j.name + " " + j.block.Index + ": renderers found — head " + j.head.Index + ", rod " + j.rod.Index + (DetailedLog ? ". Block children:" + sb : ""));
                j.visualExt = -1f;
            }
            Fits(em, j, out var pA, out var eA, out _, out var pB, out _, out _);
            float extReal = Ecs.Dot(Ecs.Sub(pB, pA), eA) + j.extBase;   // 0.20.0: + birth extension (Piston End)
            j.measuredStroke = extReal; j.measuredStrokeOk = true;   // 0.26.0: the real stroke, every frame — it's what holds the weld's collar
            float ext = extReal;
            if (ext < 0f) ext = 0f; if (ext > j.strokeMax + 0.5f) ext = j.strokeMax + 0.5f;
            // 0.24.0 (Andre: "the piston's tip, even retracted, is inside the damper"): the drawing CLAMPS at 0 — with a negative stroke the
            // rod is drawn retracted while the two parts are, in reality, closer than retracted. The lower block
            // seems to swallow the rod, but there's no contact at all: what happens is the piston GIVING WAY under the weight, and the drawing hiding it.
            if (extReal < -0.05f && j.tYieldLog <= 0f)
            {
                j.tYieldLog = 10f;
                ModLog.Warn(j.name + " " + j.block.Index + ": sagging " + (-extReal * 100f).ToString("F0") + " cm BELOW retracted (parts "
                    + j.partA.Index + " " + Ecs.Mass(em, j.partA).ToString("F0") + " kg and " + j.partB.Index + " " + Ecs.Mass(em, j.partB).ToString("F0")
                    + " kg). The rod is drawn at 0, so the sag looks like the rod buried in the block below");
            }
            if (j.tYieldLog > 0f) j.tYieldLog -= dt;
            if (System.Math.Abs(ext - j.visualExt) < 0.002f) return;
            j.visualExt = ext;
            SetPistonVisual(em, j.rod, j.head, ext, j.scale);
        }
        /// <summary>0.20.0: head at y = (0.225 + ext) x scale... (the head rises ext; the rod stretches ext) — shared with the garage (Garage.cs).</summary>
        internal static unsafe void SetPistonVisual(EntityManager em, Entity rod, Entity head, float ext, float scale)
        {
            if (Ecs.Alive(em, head)) { var ltc = (Ecs.HasC(em, head, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(head, Ecs.tiLT) : null); if (ltc != null) ltc->Position = new float3 { x = 0f, y = HeadY * scale + ext, z = 0f }; }
            if (Ecs.Alive(em, rod) && Ecs.HasPTM(em, rod)) Ecs.ScaleY(em, rod, RodRest * scale + ext);
        }

        /// <summary>If a Recoupler locked something on top of this piston (the top's decoupler/recoupler is in its partB), creates a
        /// second position motor straight between the piston's base and the locked piece, at the current pose. Released together.</summary>
        static unsafe void Payload(EntityManager em, Joint j)
        {
            Entity targetPartE = Entity.Null;
            foreach (var r in Couplers.Locked())
            {
                if (r.partB.Index == j.partB.Index && r.partA.Index != j.partA.Index) { targetPartE = r.partA; break; }
                if (r.partA.Index == j.partB.Index && r.partB.Index != j.partA.Index) { targetPartE = r.partB; break; }
            }
            if (Ecs.IsNull(targetPartE))
            {
                if (!Ecs.IsNull(j.extra)) { DropLoad(em, j, "recoupler released"); }
                return;
            }
            if (!Ecs.IsNull(j.extra) && j.extraPart.Index == targetPartE.Index && Ecs.Alive(em, j.extra)) return;
            if (!Ecs.IsNull(j.extra)) DropLoad(em, j, "piece changed");
            try
            {
                Fits(em, j, out var pA, out var eA, out var qA, out var pB, out _, out _);
                var fa = Ecs.Frame(em, j.partA, pA, eA, qA); var fc = Ecs.Frame(em, targetPartE, pA, eA, qA);
                PhysicsJoint pj;
                j.strokeBaseExtra = Ecs.Dot(Ecs.Sub(pB, pA), eA) + j.extBase;
                j.faExtra = fa; j.fbExtra = fc; j.extraWeld = Rigid;
                if (Rigid) { pj = PhysicsJoint.CreateFixed(fa, fc); j.idxExtra = -1; Weld(em, j.partA, fa, fc, j.currentTarget - j.strokeBaseExtra, &pj); }
                else
                {
                    pj = PhysicsJoint.CreatePositionMotor(fa, fc, 0f, PistonImpulse <= 0f ? float.PositiveInfinity : PistonImpulse * j.scale * j.scale);
                    j.idxExtra = FindMotor(pj, ConstraintType.PositionMotor); if (j.idxExtra < 0) return;
                    ConstraintAt(&pj, j.idxExtra)->Target.x = -(j.currentTarget - j.strokeBaseExtra);
                }
                var pair = new PhysicsConstrainedBodyPair(); pair.Entities = new EntityPair { EntityA = j.partA, EntityB = targetPartE }; pair.EnableCollision = 0;
                var kinds = new Il2CppStructArray<ComponentType>(3);
                kinds[0] = ComponentType.ReadWrite(Ecs.tiPar); kinds[1] = ComponentType.ReadWrite(Ecs.tiJoint); kinds[2] = ComponentType.ReadWrite(Ecs.tiPWI);
                var e = em.CreateEntity(kinds);
                *(Ecs.HasC(em, e, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(e, Ecs.tiPar) : null) = pair;
                *(Ecs.HasC(em, e, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(e, Ecs.tiJoint) : null) = pj;
                j.extra = e; j.extraPart = targetPartE;
                ModLog.Info(j.name + " " + j.block.Index + ": load recoupled — extra motor directly on part " + targetPartE.Index + " (" + Ecs.Mass(em, targetPartE).ToString("F0") + " kg), stroke " + j.strokeBaseExtra.ToString("F2") + " m");
            }
            catch (Exception ex) { if (j.warnings++ < 5) ModLog.Warn(j.name + " " + j.block.Index + ": extra load motor failed: " + ex.Message); }
        }

        // ================================================================ 0.10.0: time rewind
        /// <summary>The time rewind's Load (UITimebackMenu.OnClick_Load -> Bursted.LoadTimepoint) creates NEW parts at the place and
        /// velocity of the saved moment, keeps the same blocks (only swaps the Parent), destroys the old parts and restores the game's
        /// joint list. Our PhysicsJoints kept pointing at dead parts. Here: recreates each joint on the new
        /// parts, AT THE POSE OF THE SAVED MOMENT (a piston measures the stroke, a servo measures the angle, wheels keep the saved spin). If the moment is
        /// from before the separation (same part), it goes back to the start (separates again). Detected by the Load button patch (InventoryTab.cs) and,
        /// as a fallback, by the physics tick (UniverseCoreSingleton._physicsTickID) going backwards.</summary>
        public static bool RewindRequested; static int lastTick = -1, ticksLogged; static TypeIndex tiUniv; static EntityQuery qUniv; static bool univOk, univFailed;
        static unsafe void WatchTick(EntityManager em)
        {
            if (univFailed) return;
            try
            {
                if (!univOk) { univOk = true; tiUniv = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<UniverseCoreSingleton>()); var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiUniv); qUniv = em.CreateEntityQuery(a); }
                var l = Ecs.QueryList(qUniv); if (l.Count == 0) { lastTick = -1; return; }
                int tick = ((Ecs.HasC(em, l[0], tiUniv) ? (UniverseCoreSingleton*)em.GetComponentDataRawRO(l[0], tiUniv) : null))->_physicsTickID;
                if (lastTick >= 0 && tick < lastTick - 5 && !RewindRequested) { RewindRequested = true; ModLog.Info("time rewind: physics tick went back " + lastTick + " -> " + tick); }
                // 0.26.11 (Andre's log, 23/10: on the FIRST launch after loading, the car blew up in a ~300 ms frame without the
                // mod running — the axle split and the rear 8 m away when the mod came back). Diagnostics: how many physics ticks the game
                // ran in a single frame (the mod only acts between frames). Up to 10 lines per load.
                if (lastTick >= 0 && tick - lastTick >= 4 && ticksLogged < 10) { ticksLogged++; ModLog.Info("physics: " + (tick - lastTick) + " physics ticks in ONE frame (the frame took " + (UnityEngine.Time.unscaledDeltaTime * 1000f).ToString("F0") + " ms) — the mod only acts between frames"); }
                lastTick = tick;
            }
            catch (Exception ex) { univFailed = true; ModLog.Warn("time rewind: can't read the physics tick (" + ex.Message + ") — only the Load button is detected"); }
        }

        static void Rewind(EntityManager em)
        {
            int recreated = 0, since0 = 0, deferredList = 0;
            foreach (var j in allJoints.Values)
            {
                j.letGo = false;   // 0.26.10: the rewind may have gone back to before the piece was let go
                if (j.state == 1) { j.broken.Clear(); j.state = 0; j.tState = 0f; j.unmerging = false; since0++; continue; }   // was separating: start over
                if (j.state != 2 && j.state != 4) continue;   // 0.26.3: merged too (if the rewind went back to before the merge, the parts are separate)
                try { if (Ecs.Alive(em, j.joint)) em.DestroyEntity(j.joint); } catch (Exception) { }
                try { if (Ecs.Alive(em, j.joint2)) em.DestroyEntity(j.joint2); } catch (Exception) { }
                try { if (Ecs.Alive(em, j.joint3)) em.DestroyEntity(j.joint3); } catch (Exception) { }
                j.joint = Entity.Null; j.joint2 = Entity.Null; j.joint3 = Entity.Null; DeleteRails(em, j); DropLoad(em, j, "time rewind");
                j.broken.Clear(); j.deferrals = 0; j.tPullT = -1f; j.alignLog = null; j.angServo = 999f; j.angLog = 999f;
                var pa = Ecs.Parent(em, j.block); var pb = Ecs.Parent(em, j.neighbor);
                if (Ecs.IsPart(em, pa) && Ecs.IsPart(em, pb) && pa.Index != pb.Index)
                {
                    j.partA = pa; j.partB = pb; deferred = false;
                    if (Create(em, j, true))
                    {
                        j.state = 2; Reinforce(em, j); recreated++;
                        if (DetailedLog) ModLog.Info(j.name + " " + j.block.Index + ": time rewind — recreated on the new parts (" + pa.Index + " / " + pb.Index + ")" + (j.kind == Kind.Piston ? ", stroke " + j.currentTarget.ToString("F2") + " m" : ", mode " + (j.mode == 1 ? "motor" : j.mode == 2 ? "servo" : "free")));
                    }
                    else if (deferred) deferredList++;   // Rebuild tries again on the next frames
                    else { Undo(em, j, "time rewind: recreating failed"); j.state = 0; j.tState = -1f; since0++; }
                }
                else { Undo(em, j, "time rewind to before separating"); j.state = 0; j.tState = 0f; since0++; }
            }
            Couplers.Rewound(em);
            ModLog.Warn("===== TIME REWIND: " + recreated + " joint(s) recreated in the saved pose" + (deferredList > 0 ? ", " + deferredList + " waiting for the game" : "") + (since0 > 0 ? ", " + since0 + " back to the start (will separate again)" : "") + " =====");
        }

        // ================================================================ 0.7.12: queued start
        /// <summary>Every joint is born locked (fixed; a piston holds where it is). After StartTime without any
        /// new one being born, unlocks ONE at a time every StartPause s: first the ones attached straight to the chassis (level 0 = the base isn't
        /// the top of any other joint), then the ones above them (e.g. the wheel bearing attached to the steering knuckle); within the
        /// same level, the one with the lighter piece first. That way the physics solver doesn't have to solve everything at once.</summary>
        static float tQueue, tNextQueue;
        static void LaunchQueue(EntityManager em, float dt)
        {
            Joint bestPick = null; int nBest = 0; float mBest = 0f; int lockedOnes = 0; bool pulling = false;
            foreach (var j in allJoints.Values)
            {
                if (j.state != 2 || j.tStart <= 0f) continue;
                lockedOnes++;
                if (j.tPullT >= 0f) { pulling = true; continue; }
            }
            StartCollision(em, dt, lockedOnes);
            if (lockedOnes == 0) { tQueue = 0f; tNextQueue = 0f; return; }
            tQueue += dt;
            if (tQueue < StartTime) return;
            if (StartPause <= 0f)
            {
                if (pulling) return;
                foreach (var j in allJoints.Values) if (j.state == 2 && j.tStart > 0f) Release(em, j, Level(j), 0);
                return;
            }
            tNextQueue -= dt; if (tNextQueue > 0f) return;
            foreach (var j in allJoints.Values)
            {
                if (j.state != 2 || j.tStart <= 0f || j.tPullT >= 0f) continue;
                // 0.7.13: bearings (wheels) first, pistons last — the car settles before the pistons move
                int n = Level(j); float m = 0f; try { m = Ecs.Mass(em, j.partB); } catch (Exception) { }
                int g = j.kind == Kind.Piston ? 1 : 0, gM = bestPick == null ? 9 : bestPick.kind == Kind.Piston ? 1 : 0;
                if (bestPick == null || g < gM || g == gM && (n < nBest || n == nBest && (m < mBest - 0.5f || System.Math.Abs(m - mBest) <= 0.5f && j.block.Index < bestPick.block.Index))) { bestPick = j; nBest = n; mBest = m; }
            }
            if (bestPick == null) return;   // only the ones still being pulled to the axle are left
            Release(em, bestPick, nBest, lockedOnes - 1);
            tNextQueue = StartPause;
        }

        static void Release(EntityManager em, Joint j, int level, int missingN)
        {
            j.tStart = 0f;
            if (j.kind == Kind.Bearing) { j.mode = -1; j.impulseMode = -1f; }   // next frame rewrites the joint in the right mode
            if (DetailedLog)
            {
                string m = "?"; try { m = Ecs.Mass(em, j.partB).ToString("F0"); } catch (Exception) { }
                ModLog.Info(j.name + " " + j.block.Index + ": staggered start — released (level " + level + ", top part " + j.partB.Index + " " + m + " kg" + (missingN > 0 ? ", remaining " + missingN : ", last") + ")");
            }
        }

        /// <summary>Bearing: distance between the two sides of the fit, split into along the axis (+ = wheel pushed outwards,
        /// towards the upper block) and sideways; angle between the axes.</summary>
        static void Measure(EntityManager em, Joint j, out float total, out float axial, out float lateral, out float ang)
        {
            Fits(em, j, out var pA, out var eA, out _, out var pB, out var eB, out _);
            var d = Ecs.Sub(pB, pA); total = Ecs.Len(d); axial = Ecs.Dot(d, eA); lateral = Ecs.Len(Ecs.Sub(d, Ecs.Mul(eA, axial)));
            ang = (float)(System.Math.Acos(System.Math.Max(-1f, System.Math.Min(1f, Ecs.Dot(eA, eB)))) * 180.0 / System.Math.PI);
        }
        static string AxisText(float axial, float lateral) => "along the axis " + (axial >= 0f ? "+" : "") + (axial * 100f).ToString("F0") + " cm, sideways " + (lateral * 100f).ToString("F0") + " cm";

        // ================================================================ 0.7.13: no wheel x chassis collision only during the start
        /// <summary>While the start queue runs (and NoCollisionAtStart s after), the wheel doesn't collide with the parts ABOVE it in the
        /// bearing chain (e.g. steering wheel x chassis; the knuckle already doesn't collide because it is the joint's own pair). Then it
        /// turns it back on and logs where each wheel was before and 1 s after — if it only leaves the axle when collision comes back, the cause is the
        /// tyre touching the chassis. "Empty" joint = LimitedDistance 0..1e6 m (holds nothing) with EnableCollision = 0.</summary>
        public static float NoCollisionAtStart = 3f;
        class NoCol { public Entity e, wheel, above; public Joint j; }
        static readonly List<NoCol> noCol = new List<NoCol>();
        static float tAfterQueue; static readonly List<Joint> relinked = new List<Joint>(); static float tRelinked = -1f;
        static unsafe void StartCollision(EntityManager em, float dt, int lockedOnes)
        {
            // pairs that went stale (parts rebuilt): remove
            for (int i = noCol.Count - 1; i >= 0; i--)
            {
                var c = noCol[i];
                bool ok = Ecs.Alive(em, c.e) && Ecs.IsPart(em, c.wheel) && Ecs.IsPart(em, c.above) && c.j.state == 2 && c.j.partB.Index == c.wheel.Index;
                if (!ok) { try { if (Ecs.Alive(em, c.e)) em.DestroyEntity(c.e); } catch (Exception) { } noCol.RemoveAt(i); }
            }
            if (tRelinked >= 0f)
            {
                tRelinked += dt;
                if (tRelinked >= 1f)
                {
                    tRelinked = -1f;
                    foreach (var j in relinked) { try { Measure(em, j, out var t, out var ax, out var la, out var an); ModLog.Info(j.name + " " + j.block.Index + ": 1 s after re-enabling collision — wheel at " + (t * 100f).ToString("F0") + " cm (" + AxisText(ax, la) + "), tilted " + an.ToString("F1") + " degrees"); } catch (Exception) { } }
                    relinked.Clear();
                }
            }
            if (NoCollisionAtStart <= 0f) { if (noCol.Count > 0) Relink(em); return; }
            if (lockedOnes > 0)
            {
                tAfterQueue = 0f;
                foreach (var j in allJoints.Values)
                {
                    if (j.kind != Kind.Bearing || j.state != 2 || Ecs.IsNull(j.partB)) continue;
                    var cur = j; int kept = 0;
                    while (kept++ < 10)
                    {
                        Joint parent = null;
                        foreach (var o in allJoints.Values) if (o != cur && o.kind == Kind.Bearing && o.state == 2 && !Ecs.IsNull(o.partB) && o.partB.Index == cur.partA.Index) { parent = o; break; }
                        if (parent == null) break;
                        var above = parent.partA;
                        if (above.Index != j.partA.Index && above.Index != j.partB.Index && !noCol.Exists(c => c.wheel.Index == j.partB.Index && c.above.Index == above.Index))
                        {
                            try
                            {
                                var pair = new PhysicsConstrainedBodyPair(); pair.Entities = new EntityPair { EntityA = above, EntityB = j.partB }; pair.EnableCollision = 0;
                                var pj = PhysicsJoint.CreateLimitedDistance(float3.zero, float3.zero, new Unity.Physics.Math.FloatRange { Min = 0f, Max = 1e6f });
                                var kinds = new Il2CppStructArray<ComponentType>(3);
                                kinds[0] = ComponentType.ReadWrite(Ecs.tiPar); kinds[1] = ComponentType.ReadWrite(Ecs.tiJoint); kinds[2] = ComponentType.ReadWrite(Ecs.tiPWI);
                                var e = em.CreateEntity(kinds);
                                *(Ecs.HasC(em, e, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(e, Ecs.tiPar) : null) = pair;
                                *(Ecs.HasC(em, e, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(e, Ecs.tiJoint) : null) = pj;
                                noCol.Add(new NoCol { e = e, wheel = j.partB, above = above, j = j });
                                if (DetailedLog) ModLog.Info(j.name + " " + j.block.Index + ": start — wheel (part " + j.partB.Index + ") without collision with part " + above.Index + " (" + Ecs.Mass(em, above).ToString("F0") + " kg) above it until " + NoCollisionAtStart.ToString("F0") + " s after the queue");
                            }
                            catch (Exception ex) { ModLog.Warn(j.name + " " + j.block.Index + ": disabling collision at start failed: " + ex.Message); }
                        }
                        cur = parent;
                    }
                }
                return;
            }
            if (noCol.Count == 0) return;
            tAfterQueue += dt;
            if (tAfterQueue >= NoCollisionAtStart) Relink(em);
        }

        static void Relink(EntityManager em)
        {
            relinked.Clear();
            foreach (var c in noCol)
            {
                if (!relinked.Contains(c.j))
                {
                    relinked.Add(c.j);
                    try { Measure(em, c.j, out var t, out var ax, out var la, out var an); ModLog.Info(c.j.name + " " + c.j.block.Index + ": re-enabling collision wheel x part " + c.above.Index + " — the wheel is now at " + (t * 100f).ToString("F0") + " cm (" + AxisText(ax, la) + "), tilted " + an.ToString("F1") + " degrees"); } catch (Exception) { }
                }
                try { if (Ecs.Alive(em, c.e)) em.DestroyEntity(c.e); } catch (Exception) { }
            }
            ModLog.Info("start: wheel x chassis collision re-enabled (" + noCol.Count + " pair(s))");
            noCol.Clear(); tRelinked = 0f;
        }

        /// <summary>How many of the mod's joints there are between this one and the chassis (0 = its base isn't the top of another).</summary>
        static int Level(Joint j)
        {
            int n = 0; var cur = j;
            while (n < 10)
            {
                Joint parent = null;
                foreach (var o in allJoints.Values) if (o != cur && o.state == 2 && !Ecs.IsNull(o.partB) && o.partB.Index == cur.partA.Index) { parent = o; break; }
                if (parent == null) break;
                n++; cur = parent;
            }
            return n;
        }

        /// <summary>A bearing born off the axle: moves the frame of side A (fixed) from where the wheel is to the right fit.</summary>
        static unsafe void Pulling(EntityManager em, Joint j, float dt)
        {
            if (j.tPullT < 0f) return;
            try
            {
                j.tPullT += dt;
                float t = j.pullDur <= 0f ? 1f : System.Math.Min(1f, j.tPullT / j.pullDur); t = t * t * (3f - 2f * t);
                var fa = new BodyFrame();
                fa.Position = Ecs.Add(Ecs.Mul(j.pullA0.Position, 1f - t), Ecs.Mul(j.pullA1.Position, t));
                var ax = Ecs.Norm(Ecs.Add(Ecs.Mul(j.pullA0.Axis, 1f - t), Ecs.Mul(j.pullA1.Axis, t)));
                var pp = Ecs.Add(Ecs.Mul(j.pullA0.PerpendicularAxis, 1f - t), Ecs.Mul(j.pullA1.PerpendicularAxis, t));
                pp = Ecs.Norm(Ecs.Sub(pp, Ecs.Mul(ax, Ecs.Dot(pp, ax))));
                fa.Axis = ax; fa.PerpendicularAxis = pp;
                var pj = (Ecs.HasC(em, j.joint, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(j.joint, Ecs.tiJoint) : null);
                int v = pj->m_Version; var newVal = PhysicsJoint.CreateFixed(fa, j.fb); newVal.m_Version = (byte)(v + 1); *pj = newVal;
                if (j.apA0 != null)
                {
                    var ex = new[] { j.joint2, j.joint3 };
                    for (int i = 0; i < 2; i++)
                    {
                        if (!Ecs.Alive(em, ex[i])) continue;
                        var pb = (Ecs.HasC(em, ex[i], Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(ex[i], Ecs.tiJoint) : null); int vb = pb->m_Version;
                        var bs = PhysicsJoint.CreateBallAndSocket(Ecs.Add(Ecs.Mul(j.apA0[i], 1f - t), Ecs.Mul(j.apA1[i], t)), j.apB[i]); bs.m_Version = (byte)(vb + 1); *pb = bs;
                    }
                }
                if (t < 1f) return;
                j.tPullT = -1f;
                if (DetailedLog) ModLog.Info(j.name + " " + j.block.Index + ": wheel pulled onto the correct axis in " + j.pullDur.ToString("F1") + " s");
            }
            catch (Exception ex) { j.tPullT = -1f; ModLog.Warn(j.name + " " + j.block.Index + ": pulling onto the axis failed: " + ex.Message); }
        }

        /// <summary>Snapshot of the state of all joints, for the error mark (F8).</summary>
        public static string State(EntityManager em)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var j in allJoints.Values)
            {
                sb.Append("  ").Append(j.name).Append(' ').Append(j.block.Index).Append(": state ").Append(j.state == 0 ? "waiting" : j.state == 1 ? "separating" : j.state == 4 ? "MERGED (one part), stroke " + j.currentTarget.ToString("F2") + " m" : "active");
                if (j.state == 2)
                {
                    try
                    {
                        sb.Append(j.tStart > 0f ? " LOCKED (start" + (j.tPullT >= 0f ? ", pulling onto the axis" : "") + ")" : "");
                        sb.Append(j.kind == Kind.Bearing ? ", mode " + (j.mode == 1 ? "motor" : j.mode == 2 ? "servo" : j.mode == 0 ? "free" : "?") + " target " + j.target.ToString("F2") : ", requested stroke " + j.currentTarget.ToString("F2") + " m");
                        sb.Append(", level ").Append(Level(j));
                        sb.Append(", part ").Append(j.partA.Index).Append(' ').Append(Ecs.Mass(em, j.partA).ToString("F0")).Append(" kg -> part ").Append(j.partB.Index).Append(' ').Append(Ecs.Mass(em, j.partB).ToString("F0")).Append(" kg");
                        Fits(em, j, out var pA, out var eA, out _, out var pB, out var eB, out _);
                        var d = Ecs.Sub(pB, pA); float ax = Ecs.Dot(d, eA); float lat = j.kind == Kind.Piston ? Ecs.Len(Ecs.Sub(d, Ecs.Mul(eA, ax))) : Ecs.Len(d);
                        float ang = (float)(System.Math.Acos(System.Math.Max(-1f, System.Math.Min(1f, Ecs.Dot(eA, eB)))) * 180.0 / System.Math.PI);
                        sb.Append(", off-axis ").Append((lat * 100f).ToString("F1")).Append(" cm");
                        if (j.kind == Kind.Bearing) { var perp = Ecs.Len(Ecs.Sub(d, Ecs.Mul(eA, ax))); sb.Append(" (").Append(AxisText(ax, perp)).Append(')'); }
                        sb.Append(", tilted ").Append(ang.ToString("F1")).Append(" degrees");
                        if (j.kind == Kind.Piston) sb.Append(", actual stroke ").Append((ax + j.extBase).ToString("F2")).Append(" m");
                        unsafe
                        {
                            var va = (Ecs.HasC(em, j.partA, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(j.partA, Ecs.tiVel) : null); var vb = (Ecs.HasC(em, j.partB, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(j.partB, Ecs.tiVel) : null);
                            if (va != null && vb != null) sb.Append(", vel A ").Append(Ecs.Len(va->Linear).ToString("F1")).Append(" m/s spin ").Append(Ecs.Len(va->Angular).ToString("F1")).Append(" | vel B ").Append(Ecs.Len(vb->Linear).ToString("F1")).Append(" m/s spin ").Append(Ecs.Len(vb->Angular).ToString("F1")).Append(" rad/s");
                        }
                    }
                    catch (Exception ex) { sb.Append(" (read error: ").Append(ex.Message).Append(')'); }
                }
                sb.Append('\n');
            }
            foreach (var m in motors.Values) sb.Append("  ").Append(m.name).Append(' ').Append(m.block.Index).Append(": signal ").Append(m.signal.ToString("F2")).Append(m.powered ? ", powered" : ", NO power").Append(m.fuel ? ", fuel in motor " + m.fuelHas.ToString("F2") + ", usage " + (m.usage * 100f).ToString("F0") + "%, mixture efficiency " + (m.efficiency * 100f).ToString("F0") + "%" : "").Append('\n');
            sb.Append(TurretStates());
            return sb.ToString();
        }

        /// <summary>Pairs of parts linked by the mod's joints (to know what is the same vehicle).</summary>
        public static IEnumerable<(Entity a, Entity b)> Links() { foreach (var j in allJoints.Values) if (j.state == 2 && !Ecs.IsNull(j.partA) && !Ecs.IsNull(j.partB)) yield return (j.partA, j.partB); }
        /// <summary>If the part is the top of an active piston, the part of its base (the ship); otherwise Entity.Null.</summary>
        public static Entity PistonBase(Entity piece) { foreach (var j in allJoints.Values) if (j.state == 2 && j.kind == Kind.Piston && j.partB.Index == piece.Index) return j.partA; return Entity.Null; }

        static void DeleteRails(EntityManager em, Joint j)
        {
            foreach (var g in j.rails) { try { if (Ecs.Alive(em, g)) em.DestroyEntity(g); } catch (Exception) { } }
            j.rails.Clear();
        }

        static void DropLoad(EntityManager em, Joint j, string reason)
        {
            try { if (Ecs.Alive(em, j.extra)) em.DestroyEntity(j.extra); } catch (Exception) { }
            if (!Ecs.IsNull(j.extra) && DetailedLog) ModLog.Info(j.name + " " + j.block.Index + ": extra load motor removed (" + reason + ")");
            j.extra = Entity.Null; j.extraPart = Entity.Null; j.idxExtra = -1; j.extraWeld = false;
        }

        /// <summary>Diagnostics (0.7.5): how far the piston's top left the axis. Real stroke (along the axis), side deviation and
        /// angle between the block's axis and the neighbour's. Logs at most once per second and only when it exceeds 5 cm / 3 degrees or changes a lot.</summary>
        /// <summary>0.7.11: how far the wheel left the bearing's axis (the two points of the fit should be the same, and the axes
        /// parallel). Logs "TILTED" like the piston, at most once per second and only when it changes band.</summary>
        static void BearingAlignment(EntityManager em, Joint j, float dt)
        {
            if (!DetailedLog) return;
            j.tAlign += dt; if (j.tAlign < 1f) return; j.tAlign = 0f;
            try
            {
                Measure(em, j, out float d, out float axial, out float lateral, out float ang);
                bool bad = d > 0.05f || ang > 3f;
                string key = bad ? "bad " + ((int)(d * 10f)) + " " + ((int)(ang / 5f)) : "ok";
                if (key != j.alignLog)
                {
                    j.alignLog = key;
                    string s = "wheel off-axis " + (d * 100f).ToString("F0") + " cm (" + AxisText(axial, lateral) + "), tilted " + ang.ToString("F1") + " degrees (mode " + (j.mode == 1 ? "motor" : j.mode == 2 ? "servo" : "free") + ", part " + j.partA.Index + " " + Ecs.Mass(em, j.partA).ToString("F0") + " kg -> part " + j.partB.Index + " " + Ecs.Mass(em, j.partB).ToString("F0") + " kg)";
                    if (bad) ModLog.Warn(j.name + " " + j.block.Index + ": MISALIGNED — " + s); else ModLog.Info(j.name + " " + j.block.Index + ": aligned — " + s);
                }
            }
            catch (Exception) { }
        }

        static void Alignment(EntityManager em, Joint j, float dt)
        {
            if (!DetailedLog) return;
            j.tAlign += dt; if (j.tAlign < 1f) return; j.tAlign = 0f;
            try
            {
                Fits(em, j, out var pA, out var eA, out _, out var pB, out var eB, out _);
                var d = Ecs.Sub(pB, pA); float ax = Ecs.Dot(d, eA); float lat = Ecs.Len(Ecs.Sub(d, Ecs.Mul(eA, ax)));
                float cos = System.Math.Max(-1f, System.Math.Min(1f, Ecs.Dot(eA, eB))); float ang = (float)(System.Math.Acos(cos) * 180.0 / System.Math.PI);
                bool bad = lat > 0.05f || ang > 3f;
                string s = "actual stroke " + (ax + j.extBase).ToString("F2") + " m (requested " + j.currentTarget.ToString("F2") + "), off-axis " + (lat * 100f).ToString("F0") + " cm, tilted " + ang.ToString("F1") + " degrees";
                string key = bad ? "bad " + (lat * 10f).ToString("F0") + " " + ((int)(ang / 3f)) : "ok";
                if (key != j.alignLog) { j.alignLog = key; if (bad) ModLog.Warn(j.name + " " + j.block.Index + ": MISALIGNED — " + s); else ModLog.Info(j.name + " " + j.block.Index + ": aligned — " + s); }
            }
            catch (Exception) { }
        }


        /// <summary>0.24.0 (Andre: "with no motor, thruster or fan on, the ship slid off the platform by itself"). A piston
        /// that got crooked keeps pushing to close the position error — except the push no longer comes out along its axis, so a constant
        /// SIDE component is left and the ship moves by itself, landed and still. In Andre's log it was stuck at -0.15 m with 27
        /// degrees, stable for minutes (it isn't oscillation: it's a constant fight). Crooked above PistonTiltMax for PistonTiltTime: the
        /// target becomes where the rod is (zero error, no push) and the joint only holds. Comes back by itself if it straightens up.</summary>
        static unsafe void Tilted(EntityManager em, Joint j, float dt)
        {
            j.tTilt += dt; if (j.tTilt < 0.25f) return; float passed = j.tTilt; j.tTilt = 0f;
            try
            {
                Fits(em, j, out var pA, out var eA, out _, out var pB, out var eB, out _);
                float cos = System.Math.Max(-1f, System.Math.Min(1f, Ecs.Dot(eA, eB)));
                float ang = (float)(System.Math.Acos(cos) * 180.0 / System.Math.PI);
                var d = Ecs.Sub(pB, pA); float stroke = Ecs.Dot(d, eA) + j.extBase;
                float error = System.Math.Abs(stroke - j.currentTarget);
                float moved = System.Math.Abs(stroke - j.strokeBefore); j.strokeBefore = stroke;
                j.measuredStroke = stroke;
                // two reasons to give up, and both are the same thing seen from different angles: the piston can't reach
                // the target. (a) TILTED: the push comes out off its axis. (b) STUCK: the error is large and the rod doesn't move — the load
                // won. In both cases insisting only injects a constant force into the ship, which walks off by itself and shakes the damper.
                bool tilted = PistonTiltMax > 0f && ang > (j.tilted ? System.Math.Max(1f, PistonTiltMax - 3f) : PistonTiltMax);
                bool stuck = error > (j.tilted ? 0.03f : 0.05f) && moved < 0.01f;
                if (!tilted && !stuck)
                {
                    j.tTiltAcc = 0f;
                    if (j.tilted) { j.tilted = false; ModLog.Info(j.name + " " + j.block.Index + ": moving again (" + ang.ToString("F0") + " degrees off axis, " + (error * 100f).ToString("F0") + " cm from the target) — the motor is pushing again"); }
                    return;
                }
                j.tTiltAcc += passed;
                if (!j.tilted && j.tTiltAcc >= PistonTiltTime)
                {
                    j.tilted = true; j.gaveUpTarget = j.currentTarget;
                    ModLog.Warn(j.name + " " + j.block.Index + ": stuck " + (error * 100f).ToString("F0") + " cm from its target (" + ang.ToString("F0")
                        + " degrees off axis) for " + PistonTiltTime.ToString("F0") + " s — the motor stopped fighting and now only holds where it is."
                        + " It was pushing with no hope of getting there, and that constant force shoves the whole ship and shakes whatever is under it."
                        + " It goes back to normal as soon as you give it a new target");
                }
            }
            catch (Exception) { }
        }

        // ================================================================ bearing (quarter 0.5 m: bottom face glues, top face releases and turns)
        static unsafe void ActiveBearing(EntityManager em, Joint j, float dt)
        {
            Watch(em, j);
            if (!Valid(em, j)) { if (Rebuild(em, j)) return; Undo(em, j, "part/joint changed (time rewind, load or break)"); j.state = 0; j.tState = -1f; return; }
            BearingAlignment(em, j, dt);
            if (j.tStart > 0f) { Pulling(em, j, dt); return; }   // soft start: locked (fixed) until the queue releases it (0.7.12)
            if (j.rail) { if (j.mode != 0) { var pjg = (Ecs.HasC(em, j.joint, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(j.joint, Ecs.tiJoint) : null); int vg = pjg->m_Version; var ng = PhysicsJoint.CreateHinge(j.fa, j.fb); ng.m_Version = (byte)(vg + 1); *pjg = ng; j.mode = 0; j.idxMotor = -1; j.impulseMode = 0f; ModLog.Info(j.name + " " + j.block.Index + ": FREE (closes a ring)"); } return; }   // 0.12.2
            // 0.15.0: who's in charge: the motor whose Drive Shaft reaches this bearing (MapAxes) > data signal on an input (servo) > nothing (free).
            // With both: the shaft rules while it turns; with the shaft at 0 (or without power) the servo counts.
            Motor mot = null; bool servo = false; float valServo = 0f; string origin = "nothing connected", servoOrigin = "";
            float valMotor = 0f; Net net = null;
            // 0.17.0: network of motors (they add up); value = average weighted by force; conflicting signals = locked
            if (driven.TryGetValue(Ecs.K(j.block), out var ac) && ac.net != null && ac.net.rep != null) { net = ac.net; mot = net.rep; valMotor = ac.val; origin = ac.path + (net.motors.Count > 1 ? " [" + net.name + "]" : ""); }
            foreach (var p in Ecs.Ports(em, j.block))
            {
                if (!Ecs.Port(em, p, out var t, out _, out _) || (t != Ecs.PortInput && t != PortUntyped)) continue;
                if (PortOnAxisCache(em, p)) continue;
                if (SlipRings.IsPassThrough(em, j.block, p, j.name)) continue;   // 0.22.0: pass-through ports (slip ring) aren't servo
                if (!Ecs.ReadInput(em, p, out var v, out _, out var trail)) continue;
                if (!servo) { servo = true; valServo = v; servoOrigin = "direct signal, port " + p.Index + trail; }
            }
            bool axisLocked = net != null && net.conflict;
            bool axisTurning = mot != null && (axisLocked || (net.powered && System.Math.Abs(valMotor) > 0.001f && mot.brake <= 0.02f));
            if (j.spin && !axisTurning && !axisLocked) { servoOrigin = "gyro: levelling against gravity" + (servo ? " + offset from " + servoOrigin : ""); servo = true; }   // 0.19.14
            if (servo && !axisTurning && mot != null) { mot = null; origin = servoOrigin + " (shaft at 0)"; }
            else if (servo && mot == null) origin = servoOrigin;
            else if (axisTurning) servo = false;
            int mode; float target, impulse;
            // 0.11.2 (Andre: "raise the brake force some more"): MECHANICAL brake — works without power/fuel and locks ALL the
            // vehicle's wheels (bearings without a direct servo signal) when any motor of the same vehicle gets the brake; the force
            // is the braking motor's x Motor.brake_factor (30). A bearing with a direct signal (steering) doesn't lock.
            var braking = servo ? null : MotorBraking(em, j, mot, net);
            if (axisLocked)
            {
                // 0.17.0 (Andre: "if reversed signals come in, lock the bearings and show the locked bearing visually")
                mode = 3; impulse = -4f; target = 0f;
                origin = "LOCKED by the shaft: " + net.conflictLog + " [" + net.name + "]";
            }
            else if (braking != null && braking.brake >= 0.9f)
            {
                // 0.12.1 (Andre: "even braked it keeps turning and goes down the hill"): brake all the way = LOCKED wheel (fixed joint at the current pose)
                mode = 3; impulse = -3f; target = 0f;
                origin = "brake LOCKED by " + braking.name + " " + braking.block.Index + (braking == mot ? "" : " (same vehicle)");
            }
            else if (braking != null)
            {
                mode = 1; impulse = System.Math.Max(braking.impulse, braking.baseImpulse) * (1f + (BrakeFactor - 1f) * braking.brake); target = 0f;
                origin = "brake from " + braking.name + " " + braking.block.Index + (braking == mot ? "" : " (same vehicle)");
            }
            else if (mot != null)
            {
                if (net.powered)
                {
                    mode = 1; impulse = net.bearingForce;
                    float request = System.Math.Max(-1f, System.Math.Min(1f, valMotor)) * net.velMax * (j.mirror ? -1f : 1f), step = MotorRamp <= 0f ? 999f : net.velMax / MotorRamp * dt;
                    target = request > j.currentTarget + step ? j.currentTarget + step : request < j.currentTarget - step ? j.currentTarget - step : request;
                    // 0.21.5 (Andre: "in motor mode the bearing stays static with signal 0, even though there's already a brake port"): signal ~0 (dead
                    // zone 0.02) with the ramp already at ~0 = neutral — free wheel, like a bearing with nothing; stopping on a slope is done with the brake
                    if (System.Math.Abs(valMotor) < 0.02f && System.Math.Abs(target) < 0.05f) { mode = 0; target = 0f; impulse = 0f; origin += " at 0: coasting"; }
                }
                else { mode = 0; target = 0f; impulse = 0f; origin += " no power"; }
            }
            else if (servo)
            {
                mode = 2; impulse = ServoImpulse * j.scale * j.scale; target = System.Math.Max(-1f, System.Math.Min(1f, valServo)) * (float.IsNaN(j.servoDegrees) ? ServoDegrees : j.servoDegrees) * (float)(System.Math.PI / 180.0) * (j.mirror ? -1f : 1f);   // 0.19.2: the block's knob rules; without a knob = config
                if (j.spin) target = GyroTarget(em, j, target, dt);   // 0.19.14: leveller — the signal is only an offset on top of the gravity target
                if (DetailedLog && System.Math.Abs(valServo - j.signalLogged) >= 0.05f) { j.signalLogged = valServo; ModLog.Info(j.name + " " + j.block.Index + ": servo signal=" + valServo.ToString("F2")); }
            }
            else { mode = 0; target = 0f; impulse = 0f; }

            var pj = (Ecs.HasC(em, j.joint, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(j.joint, Ecs.tiJoint) : null);
            if (mode == j.mode && mode == 1 && impulse != j.impulseMode && j.idxMotor >= 0 && impulse > 0f && j.impulseMode > 0f)
            {
                // 0.8.0: a fuel motor changes its force with the mix — only swaps the motor's limit, without redoing the joint
                ConstraintAt(pj, j.idxMotor)->MaxImpulse = new float3 { x = impulse, y = impulse, z = impulse }; pj->m_Version++; j.impulseMode = impulse;
            }
            if (mode != j.mode || impulse != j.impulseMode)
            {
                int version = pj->m_Version;
                PhysicsJoint newVal;
                if (mode == 3)
                {
                    Fits(em, j, out var pAt, out var eAt, out var qAt, out _, out _, out _);
                    newVal = PhysicsJoint.CreateFixed(j.fa, Ecs.Frame(em, j.partB, pAt, eAt, qAt)); j.idxMotor = -1;
                }
                else newVal = Build(j, mode, target, impulse, out j.idxMotor);
                newVal.m_Version = (byte)(version + 1);
                *pj = newVal;
                if (mode != j.mode || origin != j.modeOrigin) ModLog.Info(j.name + " " + j.block.Index + ": mode " + (mode == 0 ? "free" : mode == 1 ? "motor" : mode == 3 ? "LOCKED" : "servo") + " (" + origin + ")");
                j.mode = mode; j.impulseMode = impulse; j.modeOrigin = origin; j.currentTarget = mode == 1 ? target : 0f; j.target = target; j.angServo = 999f;
                // 0.26.0: the servo's weld is born with the frames coinciding = the GARAGE angle. If the bearing had already turned, that
                // would be a one-frame jump until ServoStep takes over. So it takes over right here: reads the current angle and welds at it.
                if (mode == 2 && Rigid) { try { ServoStep(em, j, pj, target, dt); } catch (Exception) { } }
                return;
            }
            LedBearing(em, j, axisLocked, dt);
            if (mode == 0 || mode == 3) return;
            if (mode == 2) { ServoStep(em, j, pj, target, dt); return; }
            if (mode == 1) j.currentTarget = target;
            if (System.Math.Abs(target - j.target) < 1e-5f) return;
            j.target = target;
            Constraint* c = ConstraintAt(pj, j.idxMotor);
            c->Target.x = target;
            pj->m_Version++;
        }

        // ---- vehicle = parts linked by the mod's joints (active joints) and locked recouplers; recalculated once per frame
        static readonly Dictionary<long, long> partGroup = new Dictionary<long, long>(); static int groupFrame = -1;
        static long Root(long k) { while (partGroup.TryGetValue(k, out var p) && p != k) k = p; return k; }
        static void Union(long a, long b) { a = Root(a); b = Root(b); if (!partGroup.ContainsKey(a)) partGroup[a] = a; if (!partGroup.ContainsKey(b)) partGroup[b] = b; if (a != b) partGroup[a] = b; }
        static void RefreshGroups(EntityManager em)
        {
            if (groupFrame == UnityEngine.Time.frameCount) return; groupFrame = UnityEngine.Time.frameCount;
            partGroup.Clear();
            foreach (var o in allJoints.Values) if (o.state == 2 && !Ecs.IsNull(o.partA) && !Ecs.IsNull(o.partB)) Union(Ecs.K(o.partA), Ecs.K(o.partB));
            foreach (var (ta, tb) in TurretLinks()) Union(Ecs.K(ta), Ecs.K(tb));
            foreach (var r in Couplers.Locked()) if (!Ecs.IsNull(r.partA) && !Ecs.IsNull(r.partB)) Union(Ecs.K(r.partA), Ecs.K(r.partB));
        }
        /// <summary>Motor with its brake applied that counts for this bearing. 0.22.0 (Andre: "the brake of the right motor was locking my motor
        /// on the left too"): a bearing with its own motor (Drive Shaft arriving) obeys ONLY the motors of its network (the one with the highest brake);
        /// "any motor of the same vehicle" (0.11.2) only applies to a bearing with nothing connected (free wheel).</summary>
        static Motor MotorBraking(EntityManager em, Joint j, Motor own, Net net)
        {
            if (own != null)
            {
                Motor largest = null;
                if (net != null) { foreach (var m in net.motors) if (m.brake > 0.02f && (largest == null || m.brake > largest.brake)) largest = m; }
                else if (own.brake > 0.02f) largest = own;
                return largest;
            }
            bool any = false; foreach (var m in motors.Values) if (m.brake > 0.02f) { any = true; break; }
            if (!any) return null;
            RefreshGroups(em);
            long ga = Root(Ecs.K(j.partA)), gb = Ecs.IsNull(j.partB) ? ga : Root(Ecs.K(j.partB));
            Motor bestPick = null;
            foreach (var m in motors.Values)
            {
                if (m.brake <= 0.02f || !Ecs.Alive(em, m.block)) continue;
                var pm = Ecs.Parent(em, m.block); if (!Ecs.IsPart(em, pm)) continue;
                long gm = Root(Ecs.K(pm));
                if (gm != ga && gm != gb) continue;
                if (bestPick == null || m.brake > bestPick.brake) bestPick = m;
            }
            return bestPick;
        }

        /// <summary>0.23.0 (PapaSaffron on Nexus: "some way to read how many degrees a bearing has rotated"): the LAST port of the
        /// bearing / gyro / minis is an OUTPUT with the current angle of the upper side. The panel's switch picks the unit: on =
        /// degrees (-180..180), off = the same number the servo input would ask for to stay at that angle (-1..1 by the knob),
        /// so you can feed the output into your math and send the result back to the input. Without an active joint (garage) the output stays at 0.</summary>
        static void AngleOutput(EntityManager em, Joint j, float dt)
        {
            j.tSwAng += dt;
            if (j.tSwAng >= 3f) { j.tSwAng = 0f; if (!Ecs.Alive(em, j.swAng)) j.swAng = Knobs.FindSwitch(em, j.block); }
            var ports = Ecs.Ports(em, j.block); if (ports == null || ports.Length == 0) return;
            var output = ports[ports.Length - 1];
            if (!Ecs.Port(em, output, out var kind, out _, out _) || kind != Ecs.PortOutput) return;   // old prefab (without the output)
            float v = 0f; float th = 0f; bool measured = false;
            if (j.state == 2 && !Ecs.IsNull(j.joint)) { try { th = ServoAngle(em, j); measured = true; } catch (Exception) { return; } }
            else if (j.cAngleOk) { th = j.cAngle; measured = true; }   // 0.28.0: on a client, from the server's poses (ClientView.cs)
            if (measured)
            {
                bool degrees = Knobs.SwitchOn(em, j.swAng);
                float target = ServoInvert ? th : -th;   // the servo takes the measured angle to -target: undo it to get back to the input's scale
                if (degrees) v = target * 57.29578f;
                else
                {
                    float g = (float.IsNaN(j.servoDegrees) ? ServoDegrees : j.servoDegrees) * (float)(System.Math.PI / 180.0) * (j.mirror ? -1f : 1f);
                    v = System.Math.Abs(g) < 1e-4f ? 0f : target / g;
                    v = System.Math.Max(-1f, System.Math.Min(1f, v));
                }
                if (!j.swLog) { j.swLog = true; ModLog.Info(j.name + " " + j.block.Index + ": angle output in " + (degrees ? "degrees" : "-1..1 (knob range)") + (Ecs.IsNull(j.swAng) ? " (no switch on this block: old prefab)" : "")); }
            }
            if (!float.IsNaN(j.lastOutput) && System.Math.Abs(v - j.lastOutput) < 1e-4f) return;
            j.lastOutput = v; Ecs.WritePort(em, output, v);
        }

        /// <summary>Angle (rad, -pi..pi) of the bearing's upper side around the axis, relative to the lower side: perpendicular
        /// of the block (side A) x perpendicular stored on the neighbour block (side B). 0 = as it was in the garage.</summary>
        static float ServoAngle(EntityManager em, Joint j)
        {
            Fits(em, j, out _, out var eA, out var qA, out _, out _, out var qB);
            var qBp = Ecs.Norm(Ecs.Sub(qB, Ecs.Mul(eA, Ecs.Dot(qB, eA))));
            return (float)System.Math.Atan2(Ecs.Dot(Ecs.Cross(qA, qBp), eA), Ecs.Dot(qA, qBp));
        }

        /// <summary>The mod's servo: measures the angle and asks the velocity motor for Gain x error (capped at ServoSpeed). The velocity
        /// motor forces (wA - wB)·axis = target, and the measured angle grows with (wB - wA)·axis -> motor target = -velocity.
        /// Same direction as the old RotationMotor (it took the measured angle to -target); Bearing.servo_invert swaps it.</summary>
        /// <summary>0.26.14: largest distance from the bearing's axis to a block of the part it turns (partB). Recomputed when the part changes.</summary>
        static float ServoRadius(EntityManager em, Joint j)
        {
            long kp = Ecs.K(j.partB);
            if (j.partRadius == kp) return j.servoRadius;
            j.partRadius = kp; j.servoRadius = 0f; int n = 0;
            try
            {
                Fits(em, j, out var pA, out var eA, out _, out _, out _, out _);
                foreach (var b in Ecs.Children(em, j.partB))
                {
                    var d = Ecs.Sub(Ecs.Position(em, b), pA); float lat = Ecs.Len(Ecs.Sub(d, Ecs.Mul(eA, Ecs.Dot(d, eA)))); n++;
                    if (lat > j.servoRadius) j.servoRadius = lat;
                }
            }
            catch (Exception) { }
            if (DetailedLog && ServoTipSpeed > 0f && j.servoRadius > 0.5f)
            {
                float v = System.Math.Min(ServoSpeed, ServoTipSpeed / j.servoRadius);
                ModLog.Info(j.name + " " + j.block.Index + ": servo turns a piece reaching " + j.servoRadius.ToString("F1") + " m from the axis (" + n + " block(s), part " + j.partB.Index + ") — top speed " + (v * 57.2958f).ToString("F0") + " degrees/s" + (v < ServoSpeed - 1e-3f ? " (limited by [Bearing] servo_tip_speed = " + ServoTipSpeed.ToString("F1") + " m/s)" : ""));
            }
            return j.servoRadius;
        }
        static unsafe void ServoStep(EntityManager em, Joint j, PhysicsJoint* pj, float target, float dt)
        {
            j.target = target;
            float th; try { th = ServoAngle(em, j); } catch (Exception) { return; }
            float thTarget = ServoInvert ? target : -target;
            // 0.19.1 (Andre: "can servo mode be 360?"): the measured angle comes clamped to -180..180; here it is added up turn by turn
            // (angCont), so a target of up to 360 degrees turns the full circle without jumping. When (re)starting, the chosen turn is the one nearest to the target.
            const float PI2 = 2f * (float)System.Math.PI;
            if (j.angServo > 900f) { j.angCmdOk = false; j.angCont = th; while (j.angCont - thTarget > (float)System.Math.PI) j.angCont -= PI2; while (thTarget - j.angCont > (float)System.Math.PI) j.angCont += PI2; }
            else
            {
                float d = th - j.angServo; if (d > (float)System.Math.PI) d -= PI2; else if (d < -(float)System.Math.PI) d += PI2;
                if (System.Math.Abs(d) > 0.5f && DetailedLog) ModLog.Warn(j.name + " " + j.block.Index + ": servo — angle jumped " + (d * 57.2958f).ToString("F0") + " degrees in one frame");
                j.angCont += d;
            }
            j.angServo = th;
            float err = thTarget - j.angCont;
            if (Rigid)
            {
                // 0.26.0: weld at the requested angle. The command moves at most ServoSpeed rad/s and, like the piston, never more than
                // RigidSlackAng ahead of the measured angle: stuck, it stops; released, it continues. Exact angle at rest — it's what
                // was missing for a door to close tight and for the piston + bearing set not to get out of square.
                bool first = !j.angCmdOk;
                if (first) { j.angCmd = j.angCont; j.angCmdOk = true; j.angVelCmd = 0f; }
                // 0.26.10 (Andre: "the bearings are moving jerkily, they turn a bit and stop really abruptly, and then go again, and in that
                // movement everything around breaks"): the command moved at full ServoSpeed from the first frame and stopped dead — at the target or
                // at the collar. On a weld that's infinite acceleration at the start and end of every movement. Now the command's speed
                // ramps up and down with limited acceleration (ServoSpeed x 4 per second: full speed in 0.25 s) and brakes in time
                // to stop at the target.
                // Profile speed = the highest that still lets it stop at the target with that acceleration, counting this frame's step (with
                // low FPS the continuous formula sqrt(2.a.d) alone overshot the target and stopped dead).
                // 0.26.14 (Andre: "both wings of the ship totally broken after I turned 180 degrees — turning at normal speed"):
                // the weld turns the piece along the requested profile with unlimited force; on a 12.8 t wing, 172 degrees/s and 12 rad/s2 become thousands of
                // N on each block at the tip and the game's joints (~3300) break. Now speed and acceleration also respect the
                // speed AT THE TIP of the piece (ServoTipSpeed m/s; acceleration: that speed in 0.5 s). A small piece (the car's
                // steering) doesn't change; a large piece turns more slowly.
                float vMax = ServoSpeed, accel = ServoSpeed * 4f; float radius = ServoRadius(em, j);
                if (ServoTipSpeed > 0f && radius > 0.5f) { vMax = System.Math.Min(vMax, ServoTipSpeed / radius); accel = System.Math.Min(accel, 2f * ServoTipSpeed / radius); }
                float dvMax = accel * dt, cmd = j.angCmd, missing = thTarget - cmd;
                float vProfile = System.Math.Sign(missing) * System.Math.Min(vMax, accel * ((float)System.Math.Sqrt(dt * dt + 2f * System.Math.Abs(missing) / accel) - dt));
                // 0.26.0b: the same collar as the piston, and for the same reason — the previous version pulled the command behind the measured angle,
                // so any wobble became a command and the weld rewritten every frame fed the wobble. Now the measurement only
                // decides whether the command moves or waits, and the weld is only rewritten when the commanded angle changes.
                // 0.26.10: the piece fell more than RigidSlackAng behind in the direction of motion = the command BRAKES (instead of stopping dead)
                float lagA = cmd - j.angCont;
                bool collar = (lagA > RigidSlackAng && vProfile > 0f) || (lagA < -RigidSlackAng && vProfile < 0f);
                if (!collar && j.angVelCmd * vProfile > 0f && System.Math.Abs(vProfile) < System.Math.Abs(j.angVelCmd)) j.angVelCmd = vProfile;   // braking: follows the profile
                else { float dv = (collar ? 0f : vProfile) - j.angVelCmd; j.angVelCmd += dv > dvMax ? dvMax : dv < -dvMax ? -dvMax : dv; }
                cmd += j.angVelCmd * dt;
                if (missing != 0f && (thTarget - cmd) * missing <= 0f) { cmd = thTarget; j.angVelCmd = 0f; }   // went past the target this frame: stays on it
                if (!first && System.Math.Abs(cmd - j.angCmd) < 1e-5f) return;
                j.angCmd = cmd; j.currentTarget = cmd;
                var fr = new BodyFrame { Axis = j.fa.Axis, Position = j.fa.Position, PerpendicularAxis = Ecs.Rot(quaternion.AxisAngle(j.fa.Axis, cmd), j.fa.PerpendicularAxis) };
                int vr = pj->m_Version; var nr = PhysicsJoint.CreateFixed(fr, j.fb); nr.m_Version = (byte)(vr + 1); *pj = nr;
            }
            else
            {
                float w = System.Math.Max(-ServoSpeed, System.Math.Min(ServoSpeed, ServoGain * err));
                float motorTarget = -w;
                if (System.Math.Abs(motorTarget - j.currentTarget) > 1e-3f && j.idxMotor >= 0)
                {
                    ConstraintAt(pj, j.idxMotor)->Target.x = motorTarget; pj->m_Version++; j.currentTarget = motorTarget;
                }
            }
            j.tAngLog += dt;
            if (DetailedLog && j.tAngLog >= 0.5f && System.Math.Abs(th - j.angLog) >= 5f / 57.2958f)
            {
                j.tAngLog = 0f; j.angLog = th;
                ModLog.Info(j.name + " " + j.block.Index + ": servo angle " + (j.angCont * 57.2958f).ToString("F0") + " degrees (requested " + (thTarget * 57.2958f).ToString("F0") + ")");
            }
        }

        /// <summary>Walks up the circuit from an output port: if it's the output of a Motor, found it; otherwise looks at the
        /// inputs of the component that owns it (Data Router, inverter, math...) and walks up through each one (up to 8 levels).</summary>
        static Motor MotorAbove(EntityManager em, Entity output, int level, HashSet<long> seenSet, out string via)
        {
            via = "";
            if (Ecs.IsNull(output) || level > 8) return null;
            if (motorByOutput.TryGetValue(Ecs.K(output), out var m)) return m;
            var owner = Ecs.Parent(em, output); if (Ecs.IsNull(owner) || !Ecs.Alive(em, owner)) return null;
            seenSet ??= new HashSet<long>(); if (!seenSet.Add(Ecs.K(owner))) return null;
            // Wireless Transmitter receiving: the signal comes from ANOTHER transmitter on the same channel (the one wired to an output)
            int channel = Ecs.Channel(em, owner);
            if (channel >= 0)
            {
                foreach (var t in Ecs.Transmitters(em))
                {
                    if (t.Index == owner.Index && t.Version == owner.Version) continue;
                    if (Ecs.Channel(em, t) != channel || !seenSet.Add(Ecs.K(t))) continue;
                    foreach (var p in Ecs.Ports(em, t))
                    {
                        var f = Ecs.Source(em, p, out _); if (Ecs.IsNull(f) || Ecs.Channel(em, Ecs.Parent(em, f)) >= 0) continue;
                        var r = MotorAbove(em, f, level + 1, seenSet, out var v2);
                        if (r != null) { via = " via wireless channel " + channel + " (" + owner.Index + "<-" + t.Index + ")" + v2; return r; }
                    }
                }
                return null;
            }
            foreach (var p in Ecs.Ports(em, owner))
            {
                if (p.Index == output.Index && p.Version == output.Version) continue;
                if (!Ecs.Port(em, p, out var t, out _, out _) || (t != Ecs.PortInput && t != 3)) continue;
                var f = Ecs.Source(em, p, out _); if (Ecs.IsNull(f)) continue;
                var r = MotorAbove(em, f, level + 1, seenSet, out var v2);
                if (r != null) { via = " via " + owner.Index + v2; return r; }
            }
            return null;
        }

        /// <summary>0.26.0: redoes the joint as a WELD with frame A shifted d meters along the axis — d = commanded stroke minus the birth
        /// stroke. Shifting frame A forward along the axis forces body B to move by the same amount: it's the piston with an imposed length,
        /// no spring and no force ceiling. (BodyFrame.Position comes divided by the part's scale, hence d / s.)</summary>
        static unsafe void Weld(EntityManager em, Entity part, BodyFrame fa, BodyFrame fb, float d, PhysicsJoint* pj)
        {
            float s = 1f;
            var lt = (Ecs.HasC(em, part, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(part, Ecs.tiLT) : null);
            if (lt != null && lt->Scale != 0f) s = lt->Scale;
            var f = new BodyFrame { Axis = fa.Axis, PerpendicularAxis = fa.PerpendicularAxis, Position = Ecs.Add(fa.Position, Ecs.Mul(fa.Axis, d / s)) };
            int v = pj->m_Version; var newVal = PhysicsJoint.CreateFixed(f, fb); newVal.m_Version = (byte)(v + 1); *pj = newVal;
        }
        /// <summary>0.26.0: frame A slid d meters along its own axis (same math as Weld, for whoever needs the frame and not the joint).</summary>
        static unsafe BodyFrame ElapsedFrame(EntityManager em, Entity part, BodyFrame fa, float d)
        {
            float s = 1f;
            var lt = (Ecs.HasC(em, part, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(part, Ecs.tiLT) : null);
            if (lt != null && lt->Scale != 0f) s = lt->Scale;
            return new BodyFrame { Axis = fa.Axis, PerpendicularAxis = fa.PerpendicularAxis, Position = Ecs.Add(fa.Position, Ecs.Mul(fa.Axis, d / s)) };
        }

        static PhysicsJoint Build(Joint j, int mode, float target, float impulse, out int idx)
        {
            float imp = impulse <= 0f ? float.PositiveInfinity : impulse;
            PhysicsJoint pj; ConstraintType t;
            if (mode == 1) { pj = PhysicsJoint.CreateAngularVelocityMotor(j.fa, j.fb, target, imp); t = ConstraintType.AngularVelocityMotor; }
            // 0.7.14: servo = VELOCITY motor with the angle measured by the mod (see ServoStep). Unity Physics' RotationMotor
            // reads the angle with Quaternion.ToAngleAxis (0..360): when the rotation comes with the sign flipped (q and -q are the same), it
            // reads 359 instead of -1 and does a full turn — the knuckle + wheel did a 360 at the start and went through the chassis
            // 0.26.0: rigid servo = weld at the requested angle (ServoStep redoes it every frame). Motor mode (shaft) is still a velocity
            // motor: there the right thing really is to spin freely with limited force, holding an angle is the servo's job.
            else if (mode == 2 && Rigid) { idx = -1; return PhysicsJoint.CreateFixed(j.fa, j.fb); }
            else if (mode == 2) { pj = PhysicsJoint.CreateAngularVelocityMotor(j.fa, j.fb, 0f, imp); t = ConstraintType.AngularVelocityMotor; }
            else { idx = -1; return PhysicsJoint.CreateHinge(j.fa, j.fb); }
            idx = FindMotor(pj, t);
            if (idx < 0) ModLog.Error(j.name + ": joint has no motor (" + pj.m_Constraints.A.Type + "," + pj.m_Constraints.B.Type + "," + pj.m_Constraints.C.Type + ")");
            return pj;
        }

        static unsafe string ListJoints(EntityManager em, Joint j, SpaceshipJoint* joints, int n, float3 pb, float3 ey)
        {
            var sb = new System.Text.StringBuilder(); int c = 0;
            for (int k = 0; k < n && c < 8; k++)
            {
                var sj = &joints[k];
                bool a = Same(sj->_a, j.block), b = Same(sj->_b, j.block); if (!a && !b) continue;
                var o = a ? sj->_b : sj->_a; c++;
                string side = "?"; try { var d = Ecs.Sub(Ecs.Position(em, o), pb); side = (Ecs.Dot(d, ey) / System.Math.Max(1e-4f, Ecs.Len(d))).ToString("F2"); } catch (Exception) { }
                sb.Append(" [").Append(k).Append(" other=").Append(o.Index).Append(" above=").Append(side).Append(" breakForce=").Append(sj->_breakForce.ToString("F0")).Append("]");
            }
            return c == 0 ? " none" : sb.ToString();
        }

        // ================================================================ common
        static bool Same(Entity a, Entity b) => a.Index == b.Index && a.Version == b.Version;

        /// <summary>Forces a ship joint to break on the next tick. SCTick_Decoupler writes _currentStrain=1e9, but it runs
        /// INSIDE the physics tick (after SpaceshipPhysicsFrameInit, which zeroes _currentStrain, and before SpaceshipCheckJoints).
        /// We run in Update: lower _breakForce to -1 (the check is "_currentStrain >= _breakForce") and give it back afterwards.</summary>
        static unsafe void Break(Joint j, SpaceshipJoint* sj, int k)
        {
            if (sj->_breakForce > 0f) j.broken.Add((k, sj->_a, sj->_b, sj->_breakForce));
            sj->_breakForce = -1f; sj->_currentStrain = 1e9f;
        }

        static bool Valid(EntityManager em, Joint j) =>
            Ecs.Alive(em, j.joint) && Ecs.IsPart(em, j.partA) && Ecs.IsPart(em, j.partB) && Ecs.Parent(em, j.block).Index == j.partA.Index && Ecs.Parent(em, j.neighbor).Index == j.partB.Index;

        static unsafe Constraint* ConstraintAt(PhysicsJoint* pj, int idx) => idx == 0 ? &pj->m_Constraints.A : idx == 1 ? &pj->m_Constraints.B : &pj->m_Constraints.C;
        static int FindMotor(PhysicsJoint pj, ConstraintType t)
        {
            var cs = pj.m_Constraints;
            return cs.Length > 0 && cs.A.Type == t ? 0 : cs.Length > 1 && cs.B.Type == t ? 1 : cs.Length > 2 && cs.C.Type == t ? 2 : -1;
        }

        static void LogSignal(EntityManager em, Joint j, bool has, float signal, string reason, float dt)
        {
            if (!DetailedLog) return;
            if (j.signalLogged == -999f) { ModLog.Info(j.name + " " + j.block.Index + ": port -> " + Ecs.SignalDiagnostic(em, j.block)); j.signalLogged = has ? signal : -998f; }
            if (!has && reason != j.signalReason) { j.signalReason = reason; ModLog.Warn(j.name + " " + j.block.Index + ": no signal (" + reason + ")"); }
            j.tSignalLog += dt;
            if (has && j.tSignalLog >= 0.5f && System.Math.Abs(signal - j.signalLogged) >= 0.05f) { j.tSignalLog = 0f; j.signalLogged = signal; ModLog.Info(j.name + " " + j.block.Index + ": signal=" + signal.ToString("F3") + " target=" + j.currentTarget.ToString("F3")); }
        }

        // ---------------------------------------------------------------- 0.26.15: separation inside the physics tick
        /// <summary>0.26.15 (Andre's log, 23/10, 1st launch: the pieces that had just separated were at 6-48 m/s and spinning at 460-4300
        /// degrees/s, with gravity zeroed — the bearings broke the game's joints, then came a 665 ms frame with 18 physics ticks, and only
        /// AFTER that did the mod create the joints: in all those ticks the upper and lower pieces were two bodies without a joint, colliding with each
        /// other). Now, on every tick (PhysicsPreGroup prefix), whatever is waiting for the game to split the parts gets its joint right there:
        /// pending separation (state 1), a joint left pointing at the old part (state 2) and a whole piston the game split (4).</summary>
        internal static void SplitsInTick(EntityManager em)
        {
            if (!Ecs.Ok || MenuBP) return;
            if (!Multi.Authoritative(em)) return;   // 0.28.0: a client creates no joints (Multi.cs)
            insideTick = true;
            try
            {
                foreach (var j in allJoints.Values)
                {
                    if (j.state != 1 && j.state != 2 && j.state != 4) continue;
                    try
                    {
                        // common case (active joint, nothing changed): only the two Parents and the live joint — this runs on every tick
                        var pa = Ecs.Parent(em, j.block); var pb = Ecs.Parent(em, j.neighbor);
                        if (Ecs.IsNull(pa) || Ecs.IsNull(pb) || pa.Index == pb.Index) continue;
                        if (j.state == 2 && Same(pa, j.partA) && Same(pb, j.partB) && Ecs.Alive(em, j.joint)) continue;
                        if (!Ecs.IsPart(em, pa) || !Ecs.IsPart(em, pb)) continue;
                        if (j.state == 1) Separating(em, j);
                        else if (j.state == 4) Merged(em, j, 0f);
                        else if (!Valid(em, j)) Rebuild(em, j);   // if it doesn't work, the frame cycle undoes it (as before)
                    }
                    catch (Exception ex) { if (j.warnings++ < 5) ModLog.Warn(j.name + " " + j.block.Index + ": inside the physics tick: " + ex.Message); }
                }
            }
            finally { insideTick = false; }
        }
        static bool insideTick;

        // ---------------------------------------------------------------- state 1: waits for the ship to become two parts, creates the joint
        static void Separating(EntityManager em, Joint j)
        {
            try { if (Garage.JustLaunched(j.block) || JustLaunchedJ(j)) RequestZeroGrav(); HoldSeparatingPiston(em, j); } catch (Exception) { }   // 0.26.12: + bearings
            var pa = Ecs.Parent(em, j.block); var pb = Ecs.Parent(em, j.neighbor);
            if (Ecs.IsPart(em, pa) && Ecs.IsPart(em, pb) && pa.Index != pb.Index)
            {
                RestoreBreakForce(em, j);   // already separated: gives back the original _breakForce
                j.partA = pa; j.partB = pb; j.splitFailures = 0;
                deferred = false;
                bool undone = j.unmerging; float tSep = j.tState;   // 0.26.3: leaving the merge: born at the current length and moves right away (no start queue)
                if (Create(em, j, undone)) { j.state = 2; j.tState = 0f; j.unmerging = false; Reinforce(em, j); if (undone) ModLog.Info(j.name + " " + j.block.Index + ": split in " + tSep.ToString("F2") + " s — moving on a weld from " + j.currentTarget.ToString("F2") + " m" + (insideTick ? " (inside the physics tick)" : "")); }
                else if (deferred) { j.partA = Entity.Null; j.partB = Entity.Null; return; }   // stays in "separating" and tries on the next frame
                else { j.state = 0; j.tState = -5f; j.unmerging = false; }
                return;
            }
            if (j.tState > 3f)
            {
                RestoreBreakForce(em, j);
                j.splitFailures++;
                bool giveUp = j.splitFailures >= 3;   // 0.11.1: 3 attempts and then stops for 2 min (each break made the game rebuild the parts of the whole ship)
                ModLog.Warn(j.name + " " + j.block.Index + ": the ship did not separate within 3 s (partA=" + pa.Index + " partB=" + pb.Index + ") — the top block must also be attached to the ship through another path; " + (giveUp ? "3 attempts, pausing for 2 min" : "retrying"));
                j.state = 0; j.tState = giveUp ? -120f : -2f; j.unmerging = false; if (giveUp) j.splitFailures = 0;
            }
        }

        /// <summary>The block (bearing/piston) stays attached to the base by game joints (breakForce ~3300). Multiplies the breakForce
        /// of the joints left on the block by BaseReinforce (as in 0.7.1). 0.7.1 multiplied again on every recreation
        /// (66000 -> 1320000): now it remembers the value it wrote and doesn't accumulate. (0.7.2 tried reinforcing the neighbours x1000 and
        /// turning off wheel/chassis collision: the bearings got buggy -> reverted in 0.7.3.)</summary>
        static readonly Dictionary<(long, long), float> writtenSet = new Dictionary<(long, long), float>();
        static unsafe void Reinforce(EntityManager em, Joint j)
        {
            j.watched.Clear(); j.strainMax = 0f; j.strainMaxIdx = -1;
            try
            {
                var joints = Ecs.Joints(em, out int n); if (joints == null) return;
                int c = 0, already = 0; float before = 0f, after = 0f;
                for (int k = 0; k < n; k++)
                {
                    var sj = &joints[k];
                    if (!Same(sj->_a, j.block) && !Same(sj->_b, j.block)) continue;
                    if (sj->_breakForce <= 0f) continue;
                    j.watched.Add((k, sj->_a, sj->_b));
                    if (BaseReinforce <= 1f) continue;
                    var key = (Ecs.K(sj->_a), Ecs.K(sj->_b));
                    if (writtenSet.TryGetValue(key, out var mine) && sj->_breakForce == mine) { already++; continue; }
                    float target = System.Math.Min(sj->_breakForce * BaseReinforce, ReinforceCap);
                    if (sj->_breakForce >= target) continue;
                    before = sj->_breakForce; sj->_breakForce = target; after = target; writtenSet[key] = target; c++;
                }
                if (DetailedLog && (c > 0 || already > 0)) ModLog.Info(j.name + " " + j.block.Index + ": base joints" + (c > 0 ? " " + c + " reinforced (" + before.ToString("F0") + " -> " + after.ToString("F0") + ")" : "") + (already > 0 ? " " + already + " already reinforced" : ""));
            }
            catch (Exception ex) { if (j.warnings++ < 5) ModLog.Warn(j.name + " " + j.block.Index + ": reinforcing the base failed: " + ex.Message); }
        }

        /// <summary>Keeps the highest strain (strain/breakForce) seen on the joints around; logs it when the joint comes undone, to know what broke.</summary>
        static unsafe void Watch(EntityManager em, Joint j)
        {
            if (j.watched.Count == 0) return;
            try
            {
                var joints = Ecs.Joints(em, out int n); if (joints == null) return;
                foreach (var v in j.watched)
                {
                    if (v.idx >= n) continue;
                    var sj = &joints[v.idx];
                    if (sj->_a.Index != v.a.Index || sj->_b.Index != v.b.Index) continue;
                    float bf = sj->_breakForce, st = sj->_currentStrain; if (bf <= 0f || st <= 0f || st > 1e8f) continue;
                    if (st / bf > (j.strainMaxBf > 0f ? j.strainMax / j.strainMaxBf : 0f)) { j.strainMax = st; j.strainMaxBf = bf; j.strainMaxIdx = v.idx; j.strainA = v.a; j.strainB = v.b; }
                }
            }
            catch (Exception) { }
        }

        static unsafe void RestoreBreakForce(EntityManager em, Joint j)
        {
            if (j.broken.Count == 0) return;
            try
            {
                var joints = Ecs.Joints(em, out int n);
                int ok = 0;
                if (joints != null)
                    foreach (var q in j.broken)
                    {
                        if (q.idx < 0 || q.idx >= n) continue;
                        var sj = &joints[q.idx];
                        if (sj->_a.Index != q.a.Index || sj->_b.Index != q.b.Index || sj->_breakForce != -1f) continue;
                        sj->_breakForce = q.breakForce; sj->_currentStrain = 0f; ok++;
                    }
                if (DetailedLog) ModLog.Info(j.name + " " + j.block.Index + ": _breakForce restored on " + ok + "/" + j.broken.Count + " joint(s)");
            }
            catch (Exception ex) { if (j.warnings++ < 5) ModLog.Warn(j.name + " " + j.block.Index + ": restoring _breakForce failed: " + ex.Message); }
            j.broken.Clear();
        }

        static float3 ToLocal(float3 v, float3 ex, float3 ey, float3 ez) => new float3 { x = Ecs.Dot(v, ex), y = Ecs.Dot(v, ey), z = Ecs.Dot(v, ez) };
        static float3 ToWorld(float3 l, float3 ex, float3 ey, float3 ez) => Ecs.Add(Ecs.Add(Ecs.Mul(ex, l.x), Ecs.Mul(ey, l.y)), Ecs.Mul(ez, l.z));

        /// <summary>Fit of both sides, in the world, NOW. Side A = top face of the block (center + 0.25 along its +Y). Side B =
        /// the same point, but attached to the neighbour block (stored in its space the 1st time): if the piston is extended or the
        /// bearing turned, side B comes shifted/turned along, and the recreated joint holds the current pose.</summary>
        static TypeIndex tiGT; static bool tiGTok;
        static unsafe void Fits(EntityManager em, Joint j, out float3 pA, out float3 eA, out float3 qA, out float3 pB, out float3 eB, out float3 qB)
        {
            // side A: top face of the block, from the physical pose (LocalTransform), not from LocalToWorld
            Ecs.Pose(em, j.block, out var posA, out var rotA);
            eA = Ecs.Rot(rotA, Ecs.UY); qA = Ecs.Rot(rotA, Ecs.UX); pA = Ecs.Add(posA, Ecs.Mul(eA, j.middle));
            Ecs.Pose(em, j.neighbor, out var posV, out var rotV); var qiV = Ecs.Inv(rotV);
            // 0.7.10: ALWAYS recalculates when the garage allows it (before it was stored per neighbour block: if you changed the ship/car in the
            // garage, the blocks stayed the same entities and the old fit pulled the wheel to the old place)
            if (true)
            {
                // 0.7.9: side B's "zero" comes from the GARAGE (GarageTransform: position on the grid, exact) — not from where the pieces
                // are on the frame the joint is born
                bool ok = false;
                try
                {
                    if (!tiGTok) { tiGTok = true; tiGT = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<GarageTransform>()); }
                    if (em.HasComponent(j.block, ComponentType.ReadOnly(tiGT)) && em.HasComponent(j.neighbor, ComponentType.ReadOnly(tiGT)))
                    {
                        var gb = *(Ecs.HasC(em, j.block, tiGT) ? (RigidTransform*)em.GetComponentDataRawRO(j.block, tiGT) : null); var gv = *(Ecs.HasC(em, j.neighbor, tiGT) ? (RigidTransform*)em.GetComponentDataRawRO(j.neighbor, tiGT) : null);
                        var qi = Ecs.Inv(gv.rot);
                        var eyG = Ecs.Rot(gb.rot, Ecs.UY);
                        j.bLocP = Ecs.Rot(qi, Ecs.Sub(Ecs.Add(gb.pos, Ecs.Mul(eyG, j.middle)), gv.pos));
                        j.bLocE = Ecs.Rot(qi, eyG); j.bLocQ = Ecs.Rot(qi, Ecs.Rot(gb.rot, Ecs.UX));
                        ok = true;
                    }
                }
                catch (Exception) { }
                if (!ok) { j.bLocP = Ecs.Rot(qiV, Ecs.Sub(pA, posV)); j.bLocE = Ecs.Rot(qiV, eA); j.bLocQ = Ecs.Rot(qiV, qA); }
                if (!ok && j.locNeighbor == Ecs.K(j.neighbor)) { j.bLocP = j.gP; j.bLocE = j.gE; j.bLocQ = j.gQ; }   // no garage: keeps the one from the 1st time
                else if (!ok) { j.gP = j.bLocP; j.gE = j.bLocE; j.gQ = j.bLocQ; }
                if (j.locNeighbor != Ecs.K(j.neighbor) && DetailedLog) ModLog.Info(j.name + " " + j.block.Index + ": top-side fit " + (ok ? "from the garage" : "from the current pose (no GarageTransform)"));
                j.locNeighbor = Ecs.K(j.neighbor);
            }
            pB = Ecs.Add(posV, Ecs.Rot(rotV, j.bLocP)); eB = Ecs.Norm(Ecs.Rot(rotV, j.bLocE)); qB = Ecs.Norm(Ecs.Rot(rotV, j.bLocQ));
        }

        /// <summary>The game rebuilt the parts (decoupler released, something broke, scraped...), but the block and the neighbour are still in different
        /// parts: recreates the joint NOW, at the current pose, keeping mode/target. Before (up to 0.7.3) it undid it and went the slow
        /// way (several frames with the wheel/piston top loose -> it fell, hit, broke more things -> cascade of explosions).</summary>
        static bool Rebuild(EntityManager em, Joint j)
        {
            try
            {
                if (!Ecs.Alive(em, j.block) || !Ecs.Alive(em, j.neighbor)) return false;
                var pa = Ecs.Parent(em, j.block); var pb = Ecs.Parent(em, j.neighbor);
                if (!Ecs.IsPart(em, pa) || !Ecs.IsPart(em, pb) || pa.Index == pb.Index) return false;
                string before = j.partA.Index + "/" + j.partB.Index;
                try { if (Ecs.Alive(em, j.joint)) em.DestroyEntity(j.joint); } catch (Exception) { }
                try { if (Ecs.Alive(em, j.joint2)) em.DestroyEntity(j.joint2); } catch (Exception) { }
                try { if (Ecs.Alive(em, j.joint3)) em.DestroyEntity(j.joint3); } catch (Exception) { }
                j.joint = Entity.Null; j.joint2 = Entity.Null; j.joint3 = Entity.Null; DeleteRails(em, j);
                DropLoad(em, j, "parts rebuilt");   // Payload() recreates it on the next frame if the recoupler is still locked
                j.partA = pa; j.partB = pb;
                deferred = false;
                if (!Create(em, j, true)) return deferred;   // deferred: tries again on the next frame (Valid stays false)
                Reinforce(em, j);
                ModLog.Info(j.name + " " + j.block.Index + ": parts rebuilt by the game (" + before + " -> " + pa.Index + " " + Ecs.Mass(em, pa).ToString("F0") + " kg / " + pb.Index + " " + Ecs.Mass(em, pb).ToString("F0") + " kg), joint recreated immediately in the same pose" + (insideTick ? " (inside the physics tick)" : "") + (j.kind == Kind.Piston ? " (current stroke " + j.currentTarget.ToString("F2") + " m)" : " (mode " + j.mode + ")"));
                return true;
            }
            catch (Exception ex) { ModLog.Warn(j.name + " " + j.block.Index + ": immediate recreation failed: " + ex.Message); return false; }
        }

        /// <summary>0.26.14: diagnostics — velocity of the upper piece relative to the base (m/s and degrees/s). Tells "fell" from "was pushed".</summary>
        static unsafe string RelativeVel(EntityManager em, Joint j)
        {
            try
            {
                var va = (Ecs.HasC(em, j.partA, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(j.partA, Ecs.tiVel) : null);
                var vb = (Ecs.HasC(em, j.partB, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(j.partB, Ecs.tiVel) : null);
                if (va == null || vb == null) return "";
                return "; the piece moves at " + Ecs.Len(Ecs.Sub(vb->Linear, va->Linear)).ToString("F1") + " m/s and spins at " + (Ecs.Len(Ecs.Sub(vb->Angular, va->Angular)) * 57.2958f).ToString("F0") + " degrees/s relative to the base";
            }
            catch (Exception) { return ""; }
        }
        static bool deferred;
        const float TooFar = 0.5f;   // 0.26.9: m — fit farther than this after the wait = the piece was let go, doesn't weld
        static unsafe bool Create(EntityManager em, Joint j, bool proceed = false)
        {
            try
            {
                Fits(em, j, out var pA, out var eA, out var qA, out var pB, out var eB, out var qB);
                // 0.7.10: both sides have to be together (bearing: same point; piston: on the same axis). If they aren't, it's because
                // the game has just rebuilt the parts and the positions don't match yet (the wheel ran off the axle after recoupling or
                // after weight changed nearby): waits up to 5 frames. If it persists, creates it at the current pose (without pulling the wheel away).
                var dAB = Ecs.Sub(pB, pA); float lateral = j.kind == Kind.Piston ? Ecs.Len(Ecs.Sub(dAB, Ecs.Mul(eA, Ecs.Dot(dAB, eA)))) : Ecs.Len(dAB);
                bool coincide = false, pull = false;
                float angE = (float)(System.Math.Acos(System.Math.Max(-1f, System.Math.Min(1f, Ecs.Dot(eA, eB)))) * 180.0 / System.Math.PI);
                // 0.7.12: a bearing off the axle (up to 1 m) is pulled slowly into the right place with the wheel locked, instead of being born crooked
                // 0.7.13: only a small adjustment (up to 10 cm / 10 degrees) and only when born — in 0.7.12 it pulled 44-71 cm / 50 degrees
                // after a break in the middle of the simulation and the jolt spread the explosion
                bool canPull = j.kind == Kind.Bearing && !proceed && StartTime > 0f && lateral <= 0.1f && angE <= 10f;
                // 0.20.1 (Andre: "whatever is going to stick to the piston falls before the piston gets there and attaches in the wrong place"): a piston with a Piston End
                // being born: the end (another part) may already have fallen/drifted — the joint is born pointing at the GARAGE pose (B's pivot at
                // the right point of B, target = the garage gap), and the motor + rails pull the piece back into place instead of accepting where it fell
                // 0.24.0 (Andre: "the piston fell on the first run"): the log showed the pieces settling 50 to 82 cm off and STOPPING there
                // ("the fit stopped moving" / "time limit"), so waiting longer doesn't fix it — they don't come back by themselves. The same
                // treatment that already existed for the Piston End now applies to any piece on top of the piston: the joint is born pointing
                // at the GARAGE pose and the motor + rails pull it back, instead of welding where it fell. Still applies only right
                // after leaving the garage (on a load the saved pose is the right one).
                bool snapEnd = j.kind == Kind.Piston && (j.withEnd || SnapBack) && !proceed && Garage.JustLaunched(j.block);   // 0.20.5: only right after leaving the garage (on a load the saved pose counts)
                if (lateral > 0.1f && !snapEnd)
                {
                    // 0.24.0 (Andre: "the first simulation always bugs the pistons, they fall and fit in the wrong place"): waiting
                    // 5 FRAMES doesn't work on the first take-off — the game is still rebuilding the parts and each frame is long, so
                    // the 5 frames ended with the structure still settling and the joint was born wherever the piece was (the log of 10/10
                    // showed ALL the ship's pistons between 46 and 93 cm off). Now the wait is by TIME, and it leaves earlier if the fit
                    // stops getting closer (no point waiting for what won't improve).
                    float now = UnityEngine.Time.unscaledTime;
                    if (j.deferrals == 0) { j.tWait = now; j.tImprove = now; j.bestLateral = lateral; }
                    j.deferrals++;
                    // 0.24.0b: "stopped improving" was too early — on the first take-off the piece is FALLING, so the fit
                    // gets worse before it gets better and the shortcut fired at 0.5 s (log: "after 0,5 s / 4 frame(s) (it stopped getting
                    // closer)" on every piston). The shortcut now only applies when the fit is really STILL: any movement,
                    // closer or farther, restarts the count.
                    if (System.Math.Abs(lateral - j.bestLateral) > 0.01f) { j.bestLateral = lateral; j.tImprove = now; }
                    float wait = now - j.tWait;
                    bool timedOut = wait >= FitWait, stoppedImproving = now - j.tImprove >= StillWait;
                    if (!timedOut && !stoppedImproving)
                    {
                        if (j.deferrals == 1 && DetailedLog) ModLog.Info(j.name + " " + j.block.Index + ": fit off by " + (lateral * 100f).ToString("F0") + " cm — waiting for the game to finish rebuilding the parts (holding the piece to the base meanwhile)" + RelativeVel(em, j));
                        // 0.26.9 (Andre's log, 22/10: 40 pistons welded 14 cm to 813 m out of place; 23/10: the car's mini piston at
                        // 10.78 m). When the game rebuilds the parts with the joint active, Rebuild deletes the joint before calling Create —
                        // during the whole wait (up to FitWait s) the piece had NO joint at all, falling freely. Now it moves with the base.
                        HoldPart(em, j.partB, j.partA);
                        deferred = true; return false;
                    }
                    // 0.26.9: after the wait, a piece more than TooFar from the fit didn't "fall into place" — it was let go. Welding there
                    // held the piece where it stopped (the car was stuck on a wheel 10 m away). It stays without a joint and the log says so.
                    if (lateral > TooFar)
                    {
                        ModLog.Warn(j.name + " " + j.block.Index + ": fit still off by " + (lateral * 100f).ToString("F0") + " cm after " + wait.ToString("F1") + " s — more than " + (TooFar * 100f).ToString("F0") + " cm away, the piece came off: NOT creating the joint (it would weld the piece where it landed); it stays loose until the garage or a time rewind");
                        j.deferrals = 0; j.letGo = true; return false;
                    }
                    coincide = true;
                    ModLog.Warn(j.name + " " + j.block.Index + ": fit still off by " + (lateral * 100f).ToString("F0") + " cm after " + wait.ToString("F1") + " s / " + j.deferrals + " frame(s) ("
                        + (timedOut ? "time limit" : "the fit stopped moving") + ") — creating in the current pose");
                }
                else if (canPull && (lateral > 0.02f || angE > 2f)) pull = true;
                if (DetailedLog && j.kind == Kind.Bearing) ModLog.Info(j.name + " " + j.block.Index + ": " + (proceed ? "recreating" : "spawning") + " with the wheel " + (lateral * 100f).ToString("F1") + " cm / " + angE.ToString("F1") + " degrees from the correct fit" + (pull ? " — will pull it into place" : ""));
                j.deferrals = 0;
                if (coincide) { pB = pA; }
                j.axis = eA; j.anchor = pA;
                j.fa = Ecs.Frame(em, j.partA, pA, eA, qA);
                // 0.7.4 recreated the piston with B's pivot shifted (at the neighbour block's "zero") and target -stroke: after the
                // decoupler the top flew off/made a curve. Now B's pivot is born ON TOP of A's pivot (like the 1st time, which
                // works) and the target is relative (strokeBase). The bearing keeps B from the neighbour block (it worked).
                // 0.26.0: on the rigid piston B's frame is ALWAYS at its fit point. Since Ecs.Frame stores that in
                // the part's own coordinates, the pair of frames carries the GARAGE relation — so the weld puts the piece back
                // square, instead of accepting the twist it got while the game split the parts (that's what piston -> piston
                // froze: when RECREATING the joint, snapEnd doesn't apply and the crooked pose became the new zero).
                j.fb = j.kind == Kind.Piston && !snapEnd && !Rigid ? Ecs.Frame(em, j.partB, pA, eB, qB) : Ecs.Frame(em, j.partB, pB, eB, qB);
                PhysicsJoint joint;
                if (false)   // 0.12.3: a ring's piston = normal piston (motor + rails); only the LED turns yellow
                {
                    joint = PhysicsJoint.CreatePrismatic(j.fa, j.fb, new Unity.Physics.Math.FloatRange { Min = -1000f, Max = 1000f }); j.idxMotor = -1;
                    float strokeNow = Ecs.Dot(Ecs.Sub(pB, pA), eA) + j.extBase; j.currentTarget = strokeNow; j.target = strokeNow; j.strokeBase = strokeNow;
                }
                else if (j.kind == Kind.Piston)
                {
                    // current stroke measured from the geometry (0 the 1st time); the motor starts holding where it is, doesn't go back to 0
                    // 0.20.0: with a Piston End the rod is born already stretched (extBase): the measured stroke is the shift since the garage + extBase
                    float strokeNow = Ecs.Dot(Ecs.Sub(pB, pA), eA) + j.extBase;
                    if (snapEnd)
                    {
                        j.currentTarget = j.extBase; j.target = j.extBase; j.strokeBase = j.extBase;   // frames coincide at the garage pose: target 0 = go back there
                        float drop = Ecs.Dot(Ecs.Sub(pA, pB), eA);
                        if (System.Math.Abs(drop) > 0.005f || lateral > 0.005f) ModLog.Info(j.name + " " + j.block.Index + ": the Piston End piece had already moved " + (drop * 100f).ToString("F0") + " cm along the axis and " + (lateral * 100f).ToString("F0") + " cm sideways since the garage — pulling it back into place");
                    }
                    // 0.26.0: on the weld the ZERO is always the fit's geometry (extBase), never the current crooked pose — otherwise crooked
                    // would become the new right. Only the LENGTH is born where the piece is, so there's no jolt; from there it moves to the request at the
                    // piston's normal speed. The squareness (side and twist) the weld already corrects right away.
                    else if (Rigid) { j.strokeBase = j.extBase; j.currentTarget = strokeNow; j.target = strokeNow; }
                    else { j.currentTarget = strokeNow; j.target = strokeNow; j.strokeBase = strokeNow; }   // relative target = 0: holds where it is
                    if (Rigid) { joint = PhysicsJoint.CreateFixed(ElapsedFrame(em, j.partA, j.fa, j.currentTarget - j.strokeBase), j.fb); j.idxMotor = -1; }
                    else
                    {
                        joint = PhysicsJoint.CreatePositionMotor(j.fa, j.fb, 0f, PistonImpulse <= 0f ? float.PositiveInfinity : PistonImpulse * j.scale * j.scale);   // 0.20.0: mini = 1/4 of the force
                        j.idxMotor = FindMotor(joint, ConstraintType.PositionMotor);
                        if (j.idxMotor < 0) { ModLog.Error(j.name + ": the joint came without a motor"); return false; }
                    }
                    // 0.26.0: one line per piston joint with EVERYTHING that decides the stroke — it's what answers "why did this one go to the max"
                    ModLog.Info(j.name + " " + j.block.Index + ": joint " + (proceed ? "recreated" : "created") + " — " + (Rigid ? "rigid (weld)" : "position motor")
                        + "; stroke " + j.stroke.ToString("F2") + " m of " + j.strokeMax.ToString("F2") + " max, knob " + (j.strokeFraction * 100f).ToString("F0") + "%"
                        + (j.withEnd ? ", Piston End at " + j.extBase.ToString("F2") + " m" : ", no Piston End")
                        + "; born at " + j.currentTarget.ToString("F2") + " m (zero = " + j.strokeBase.ToString("F2") + "), fit off by " + (lateral * 100f).ToString("F0") + " cm");
                }
                else if (pull)
                {
                    j.pullA0 = Ecs.Frame(em, j.partA, pB, eB, qB);   // starts exactly where the wheel is (no jolt)
                    var ppA = Ecs.Norm(Ecs.Sub(qB, Ecs.Mul(eA, Ecs.Dot(qB, eA))));   // doesn't spin the wheel around the axle
                    j.pullA1 = Ecs.Frame(em, j.partA, pA, eA, ppA);
                    j.tPullT = 0f; j.pullDur = 0.5f + System.Math.Min(1.5f, lateral * 3f + angE / 20f);
                    joint = PhysicsJoint.CreateFixed(j.pullA0, j.fb); j.idxMotor = -1;
                    if (!proceed) { j.mode = 0; j.impulseMode = 0f; j.modeOrigin = "nothing connected"; j.target = 0f; j.currentTarget = 0f; }
                    if (j.tStart <= 0f) j.tStart = System.Math.Max(0.01f, StartTime);   // locked until the queue releases it
                    tQueue = 0f; tNextQueue = 0f;
                }
                else if (!proceed && StartTime > 0f || proceed && j.tStart > 0f) { joint = PhysicsJoint.CreateFixed(j.fa, j.fb); j.idxMotor = -1; if (!proceed) { j.mode = 0; j.impulseMode = 0f; j.modeOrigin = "nothing connected"; j.target = 0f; j.currentTarget = 0f; } }
                else if (proceed && j.mode >= 0) { joint = Build(j, j.mode, j.target, j.impulseMode, out j.idxMotor); if (j.mode == 2) { j.currentTarget = 0f; j.angServo = 999f; } if (j.mode == 3) j.mode = -1; }   // 0.12.1: locked -> redo the lock on the next frame
                else { joint = PhysicsJoint.CreateHinge(j.fa, j.fb); j.idxMotor = -1; j.mode = 0; j.impulseMode = 0f; j.modeOrigin = "nothing connected"; j.target = 0f; j.currentTarget = 0f; }
                var pair = new PhysicsConstrainedBodyPair(); pair.Entities = new EntityPair { EntityA = j.partA, EntityB = j.partB };
                // 0.26.1: the piston collides with the load (nothing sinks into it); bearing/others still have no collision between the two parts
                pair.EnableCollision = (byte)(j.kind == Kind.Piston && PistonCollides ? 1 : 0);
                var kinds = new Il2CppStructArray<ComponentType>(3);
                kinds[0] = ComponentType.ReadWrite(Ecs.tiPar); kinds[1] = ComponentType.ReadWrite(Ecs.tiJoint); kinds[2] = ComponentType.ReadWrite(Ecs.tiPWI);
                var e = em.CreateEntity(kinds);
                *(Ecs.HasC(em, e, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(e, Ecs.tiPar) : null) = pair;
                *(Ecs.HasC(em, e, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(e, Ecs.tiJoint) : null) = joint;
                j.joint = e;
                // piston: 4 rails parallel to the axis at PistonRails m (prismatic = only slides straight). With a single point, any
                // sideways force made a lever at the top (log: up to 18 cm off the axis / 6 degrees)
                DeleteRails(em, j);
                if (j.kind == Kind.Piston && PistonRails > 0f && !Rigid)   // 0.26.0: on the weld the rails are redundant — removed (4 fewer joints per piston)
                {
                    // 0.24.0 (Andre's ship, 8 m pistons in two stages): the fixed 0.75 m spacing held a short piston well
                    // and a long one not at all — the log showed the error growing with the stroke (0.8 m -> 4 cm / 2.3 degrees; 4.5 m ->
                    // 130 cm / 12.7 degrees) until the game broke the part. The side deviation is the tilt x the lever arm, so the support
                    // has to grow with the rod: total reach x PistonRailsPerMeter, with PistonRails as the floor.
                    float reach = j.strokeMax + (j.extBase > 0f ? j.extBase : 0f);
                    float g = System.Math.Max(PistonRails * j.scale, reach * PistonRailsPerMeter);   // 0.20.0: mini = rails closer
                    var eZ = Ecs.Norm(Ecs.Cross(eA, qA));
                    foreach (var off in new[] { Ecs.Mul(qA, g), Ecs.Mul(qA, -g), Ecs.Mul(eZ, g), Ecs.Mul(eZ, -g) })
                    {
                        var pg = Ecs.Add(pA, off);
                        var ga = Ecs.Frame(em, j.partA, pg, eA, qA); var gbf = Ecs.Frame(em, j.partB, snapEnd ? Ecs.Add(pB, off) : pg, eB, qB);   // 0.20.1: rails at the garage pose too
                        var range = new Unity.Physics.Math.FloatRange { Min = -1000f, Max = 1000f };
                        var pr = PhysicsJoint.CreatePrismatic(ga, gbf, range);
                        var eg = em.CreateEntity(kinds);
                        *(Ecs.HasC(em, eg, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(eg, Ecs.tiPar) : null) = pair;
                        *(Ecs.HasC(em, eg, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(eg, Ecs.tiJoint) : null) = pr;
                        j.rails.Add(eg);
                    }
                }
                // bearing: two extra points on the axis (ball-and-socket), one on each side of the face, against the wheel tipping over
                if (j.kind == Kind.Bearing && BearingSupport > 0f)
                {
                    var extras = new List<Entity>();
                    j.apA0 = new float3[2]; j.apA1 = new float3[2]; j.apB = new float3[2]; int ia = 0;
                    foreach (var signalD in new[] { 1f, -1f })
                    {
                        var p2A = Ecs.Add(pA, Ecs.Mul(eA, signalD * BearingSupport * j.scale)); var p2B = Ecs.Add(pB, Ecs.Mul(eB, signalD * BearingSupport * j.scale));
                        var a2 = Ecs.Frame(em, j.partA, p2A, eA, qA).Position; var b2 = Ecs.Frame(em, j.partB, p2B, eB, qB).Position;
                        j.apA1[ia] = a2; j.apA0[ia] = Ecs.Frame(em, j.partA, p2B, eA, qA).Position; j.apB[ia] = b2; ia++;
                        var bs = PhysicsJoint.CreateBallAndSocket(pull ? j.apA0[ia - 1] : a2, b2);
                        var e2 = em.CreateEntity(kinds);
                        *(Ecs.HasC(em, e2, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(e2, Ecs.tiPar) : null) = pair;
                        *(Ecs.HasC(em, e2, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(e2, Ecs.tiJoint) : null) = bs;
                        extras.Add(e2);
                    }
                    j.joint2 = extras[0]; j.joint3 = extras[1];
                }
                if (!pull) j.tPullT = -1f;
                if (!proceed)
                {
                    if (!pull) j.tStart = StartTime;
                    tQueue = 0f; tNextQueue = 0f;   // the queue waits StartTime after the last one born
                    // the piece just released is born with the base's velocity (no spin): the separation jolt doesn't become rotation
                    try
                    {
                        var va = (Ecs.HasC(em, j.partA, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(j.partA, Ecs.tiVel) : null); var vb = (Ecs.HasC(em, j.partB, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRW(j.partB, Ecs.tiVel) : null);
                        if (va != null && vb != null) { vb->Linear = va->Linear; vb->Angular = float3.zero; }
                    }
                    catch (Exception) { }
                }
                if (!proceed) ModLog.Info(j.name + " " + j.block.Index + ": joint created (" + e.Index + ")" + (insideTick ? " inside the physics tick" : "") + " between part " + j.partA.Index + " (" + Ecs.Mass(em, j.partA).ToString("F0") + " kg) and part " + j.partB.Index + " (" + Ecs.Mass(em, j.partB).ToString("F0") + " kg)" + (j.kind == Kind.Bearing ? ", locked until the start queue releases it" : ", motor at index " + j.idxMotor + ", current stroke " + j.currentTarget.ToString("F2") + " m"));
                return true;
            }
            catch (Exception ex) { ModLog.Error(j.name + " " + j.block.Index + ": creating the joint failed: " + ex); return false; }
        }

        static void Undo(EntityManager em, Joint j, string reason)
        {
            try { if (Ecs.Alive(em, j.joint)) em.DestroyEntity(j.joint); } catch (Exception) { }
            try { if (Ecs.Alive(em, j.joint2)) em.DestroyEntity(j.joint2); } catch (Exception) { }
            try { if (Ecs.Alive(em, j.joint3)) em.DestroyEntity(j.joint3); } catch (Exception) { }
            j.joint2 = Entity.Null; j.joint3 = Entity.Null;
            if (j.state == 2) ModLog.Info(j.name + " " + j.block.Index + ": joint removed (" + reason + ")" + (j.strainMaxIdx >= 0 ? " — highest strain seen around it: " + j.strainMax.ToString("F0") + " of " + j.strainMaxBf.ToString("F0") + " (" + (100f * j.strainMax / j.strainMaxBf).ToString("F0") + "%, joint " + j.strainMaxIdx + " between " + j.strainA.Index + " e " + j.strainB.Index + ")" : ""));
            j.watched.Clear(); j.strainMax = 0f; j.strainMaxBf = 0f; j.strainMaxIdx = -1;
            DropLoad(em, j, reason); DeleteRails(em, j);
            j.joint = Entity.Null; j.idxMotor = -1; j.partA = Entity.Null; j.partB = Entity.Null; j.signalLogged = -999f; j.signalReason = null; j.mode = -1; j.powerState = 254;
            if (j.withEnd) { j.withEnd = false; j.extBase = 0f; j.stroke = j.strokeMax; j.endBlock = Entity.Null; }   // 0.20.0: WaitingTop finds the end again
            RestVisual(em, j);
        }

        /// <summary>0.19.5: a newly discovered piston (garage, after a simulation): if the head stayed raised, puts rod and
        /// head back to rest. Before, this only happened in Undo — and ForgetAll on unload doesn't go through it.</summary>
        static unsafe void RetractInGarage(EntityManager em, Joint j)
        {
            try
            {
                FindPistonParts(em, j.block, j.scale, j.strokeMax, out var h, out var c, out _);
                if (!Ecs.IsNull(h)) j.rod = h; if (!Ecs.IsNull(c)) j.head = c;
                // 0.19.10 (Andre: "piston extended at the end of the simulation -> stays extended in the garage"): diagnostics once per block outside the
                // simulation — how many children the mod sees and where the head is (if it can't find it, the log says why it doesn't retract)
                bool inGarage = !Ecs.IsPart(em, Ecs.Parent(em, j.block));
                if (inGarage && !j.garageLog) { j.garageLog = true; int nf = Ecs.Children(em, j.block).Count; ModLog.Info(j.name + " " + j.block.Index + ": garage check — " + nf + " child(ren) seen, head " + (Ecs.IsNull(j.head) ? "NOT found" : "entity " + j.head.Index + " at y=" + Ecs.LT(em, j.head).Position.y.ToString("F3")) + ", rod " + (Ecs.IsNull(j.rod) ? "NOT found" : "entity " + j.rod.Index)); }
                if (Ecs.IsNull(j.head)) return;
                // 0.20.0: in the garage Garage.cs rules the visual (rod stretched up to the aligned Piston End); here it only retracts
                // what Garage doesn't handle (block still in a part after the simulation, or without a Piston End)
                if (inGarage && Garage.Active) return;   // 0.20.2: in the garage ONLY Garage.cs touches the rod (stretches up to the end / retracts without an end)
                // 0.20.3: launched with an aligned Piston End: the rod stays stretched (the motor is born at that length); only retracts without an end
                if (!inGarage && Garage.FindEnd(em, j.block, j.middle, j.strokeMax, out _, out _, out _)) return;
                float y = Ecs.LT(em, j.head).Position.y, rep = HeadY * j.scale;
                if (y > rep + 1e-3f) { j.visualExt = y - rep; RestVisual(em, j); if (!j.retractedLog) { j.retractedLog = true; ModLog.Info(j.name + " " + j.block.Index + ": rod/head were left extended (" + (y - rep).ToString("F2") + " m) — back to rest" + (inGarage ? " (in the garage)" : "")); } }
            }
            catch (Exception) { }
        }
        /// <summary>0.11.0: the piston's head and rod back to rest (the block is going to become one again / garage).</summary>
        static unsafe void RestVisual(EntityManager em, Joint j)
        {
            try
            {
                if (j.kind != Kind.Piston || j.visualExt <= 0f) return;
                SetPistonVisual(em, j.rod, j.head, 0f, j.scale);
            }
            catch (Exception) { }
            j.visualExt = -1f;
        }

        /// <summary>01/10: after the world unloads, forgets everything WITHOUT touching the entities (they no longer exist) — the next scan redoes it.</summary>
        public static int JointCount() => allJoints.Count;   // 0.19.12
        public static void ForgetAll()
        {
            allJoints.Clear(); motors.Clear(); motorByOutput.Clear(); ignored.Clear(); noCol.Clear(); relinked.Clear(); tRelinked = -1f; ticksLogged = 0;
            try { Couplers.Forget(); } catch (Exception) { }
            try { Radio.Forget(); } catch (Exception) { }
            try { Levers.Forget(); } catch (Exception) { }
            try { Knobs.Forget(); } catch (Exception) { }   // 0.19.15
            try { Signal.Forget(); } catch (Exception) { }   // 0.24.0
            try { Garage.Forget(); } catch (Exception) { }   // 0.20.0
            try { SlipRings.Forget(); } catch (Exception) { }   // 0.22.0
            try { Multi.Forget(); ModNet.Forget(); } catch (Exception) { }   // 0.28.0
            try { ForgetTurrets(); } catch (Exception) { }
            ModLog.Info("after world unload: mod state cleared (joints, motors, beacons, radios, levers, turrets)");
        }
        public static void Clean(EntityManager em)
        {
            try { SlipRings.Shutdown(em); } catch (Exception) { }   // 0.21.4 / 0.22.0: the circuits' ghosts
            foreach (var j in allJoints.Values) Undo(em, j, "cleaning up");
            CleanTurrets(em);
            foreach (var c in noCol) { try { if (Ecs.Alive(em, c.e)) em.DestroyEntity(c.e); } catch (Exception) { } }
            noCol.Clear(); relinked.Clear(); tRelinked = -1f;
            Couplers.Clean(em);
            allJoints.Clear(); motors.Clear(); motorByOutput.Clear(); ignored.Clear();
        }
    }
}
