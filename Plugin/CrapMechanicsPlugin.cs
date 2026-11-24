using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>Pistons and bearings for Approximately Up. The blocks (prefabs in Content/) are registered by Components Framework;
    /// the physics (splitting the ship + PhysicsJoint with a motor) runs here, in Cycle, every frame.</summary>
    [BepInPlugin(PluginInfo.PluginGuid, PluginInfo.PluginDisplayName, PluginInfo.PluginVersion)]
    [BepInDependency("com.approximatelyup.mods.componentsframework")]
    [BepInDependency("com.approximatelyup.mods.modmenu", BepInDependency.DependencyFlags.SoftDependency)]
    public class CrapMechanicsPlugin : BasePlugin
    {
        public static CrapMechanicsPlugin Instance;
        public static ConfigEntry<float> PistonSpeed, PistonImpulse, PistonConsumption, MotorRamp, ServoImpulse, ServoDegrees, BearingSupport, WheelFriction, WheelFrictionOffroad;
        public static ConfigEntry<string> BonusExtras;   // 0.24.0
        public static ConfigEntry<float> ThrusterForce;   // 0.30.0
        public static ConfigEntry<bool> ThrusterJoints;   // 0.31.4
        public static ConfigEntry<bool> RingGrowList;     // 0.31.6
        public static ConfigEntry<bool> RingRebuildInTick, PowerWatchCfg;   // 0.31.16
        public static ConfigEntry<bool> RingEmbeddedTurns;   // 0.31.36
        public static ConfigEntry<bool> FlightTraceCfg;   // 0.31.8
        public static ConfigEntry<bool> StrainWatchCfg;   // 0.31.15
        public static ConfigEntry<bool> DragWatchCfg, NoDragCfg;   // 0.31.17
        public static ConfigEntry<bool> MomentumWatchCfg;   // 0.31.18
        public static ConfigEntry<bool> SealWatchCfg, FrameSeamCfg;   // 0.31.28 seal 2
        public static ConfigEntry<bool> DebugToolsCfg; public static ConfigEntry<float> SealSolidCfg;   // 0.31.30
        public static ConfigEntry<bool> SoundSealCfg, SoundWatchCfg;   // 0.31.30 sound (SoundSeal.cs)
        public static ConfigEntry<bool> WeldFirstCfg, DragAsOneCfg;   // 0.31.23 / 0.31.24
        public static ConfigEntry<bool> ContactScalingCfg;   // 0.31.26 (U1)
        public static ConfigEntry<bool> ThrustBalanceCfg; // 0.31.9
        public static ConfigEntry<float> RemotePower, RemoteRange; public static ConfigEntry<int> RemoteWindow;   // remote control (delivery 1)
        public static ConfigEntry<float> RemoteLongPress; public static ConfigEntry<bool> RemoteHand3D;   // remote control (delivery 2)
        public static ConfigEntry<float> RemoteHandLow; public static ConfigEntry<bool> RemoteGameBar;   // remote control (round 0.31.35)
        public static ConfigEntry<bool> PerfCacheCfg, PerfCompareCfg, PerfSplitWaitsCfg, SystemTimingCfg, SoundCompareCfg, SoundSettleCfg;   // 0.31.35 performance (Perf.cs, Ecs.cs, SoundSeal.cs)
        public static ConfigEntry<bool> MpOwnerOnly, MpFakeClient, MpBreakClient, MpRingClient, MpNetLog, MpLoopback;   // 0.24.0-mp; 0.28.0: + net_log, test_loopback
        public static ConfigEntry<float>[] MotorSpeed = new ConfigEntry<float>[3], MotorImpulse = new ConfigEntry<float>[3], MotorConsumption = new ConfigEntry<float>[3];
        public static ConfigEntry<bool> DetailedLog, IsActive, ServoInvert; public static ConfigEntry<float> ServoSpeed, ServoTipSpeed;   // 0.26.14
        public static ConfigEntry<bool> Stiffness; public static ConfigEntry<bool> ZeroGravLaunch; public static ConfigEntry<bool> IterationsAlways;   // 0.26.12
        public static ConfigEntry<int> SolverIterations, IconBeacon, IconSlot; public static ConfigEntry<float> StartTime, StartPause, NoCollision, PistonRails, PistonRailsPerMeter, RecouplerAngle; public static ConfigEntry<float> RecouplerSlack, BeaconHide, RecouplerPull, BeaconConsumption; public static ConfigEntry<bool> RecouplerMerge, BeaconOwn, BeaconFar;
        public static ConfigEntry<float> BaseReinforce, FuelConsumption, FuelPower, BrakeFactor, ForceReserve;
        public static ConfigEntry<float> SeatReach, FitWait, ServoGain, PistonTilt; public static ConfigEntry<bool> SnapBack; public static ConfigEntry<bool> PistonCollides, WholeUntilMoved, TurretWhole; public static ConfigEntry<float> MassRatio, MassFactorMax, TurretMassFactor; public static ConfigEntry<bool> SignalRelink; public static ConfigEntry<float> TurretReach, TurretSens, TurretGain, TurretMaxSpeed, TurretLinkAccel; public static ConfigEntry<bool> TurretInertia, TurretCamera, TurretViewRotates; public static ConfigEntry<string> TurretSeatMode, TurretSeatAdjust, TurretPlayerB, TurretViewA; public static ConfigEntry<string> TurretCursorKey;
        public static ConfigEntry<float>[] FuelMotorSpeed = new ConfigEntry<float>[3], FuelMotorImpulse = new ConfigEntry<float>[3];

        public static ConfigEntry<string> MarkKey, CircuitDumpKey; public static ConfigEntry<bool> MarkWindow; public static ConfigEntry<bool> TurretAdjust, TraceCfg;
        public override void Load()
        {
            Instance = this; ModLog.Source = Log;
            LogFile.Begin(System.IO.Path.GetDirectoryName(typeof(CrapMechanicsPlugin).Assembly.Location));
            if (OldNames.OldCopyInstalled(out var oldDll))   // 0.27.0: both copies would register every block twice
            {
                ModLog.Error("the old copy of this mod is still installed (" + oldDll + ", before 0.27.0): with both, every block would be registered twice. " +
                             "This copy stays OFF — delete the folder BepInEx/plugins/" + OldNames.OldFolder + " (settings, knobs and blueprints are carried over by this version).");
                return;
            }
            OldNames.Carry(Config);   // 0.27.0: the .cfg and knobs.txt of the old plugin name ("Mecanica"), before any Bind
            // 0.13.0: .cfg sections/keys in English; CfgMigration.Of carries the value over from the old (Portuguese) entry to the new one
            var C = Config;
            // 0.24.0: the keys are registered one section at a time — Mod Menu's list follows the order in which the mod
            // registers, not the file's (BepInEx already groups the .cfg). A new key: always at the end of its own section.

            // ---- [General] ----
            IsActive = CfgMigration.Of(C, C.Bind("General", "enabled", true, "Turns the blocks' physics on/off (the blocks still exist)"), "Geral", "ativo");
            DetailedLog = CfgMigration.Of(C, C.Bind("General", "detailed_log", false, "Step-by-step log (separation, joint, motor). Off by default; turn on to report a problem"), "Geral", "log_detalhe");

            // ---- [Piston] ----
            PistonSpeed = CfgMigration.Of(C, C.Bind("Piston", "speed", 1.0f, "Piston speed (m/s)"), "Pistao", "velocidade");
            // 0.24.0: DON'T raise this value thinking it's only a ceiling. In a position motor the solver spends the whole ceiling to
            // kill the position error in the same step, so the ceiling IS the force. Andre's test with 2,400,000 on a 38 t ship:
            // the piston burst its own already reinforced base (strain 67,304,296 of 1,320,000 = 5099%) and catapulted the leg
            // (stroke 5770 m, 16 km off the axis). The weight per leg needs ~780 of impulse/step; 150,000 is already ~190x that.
            PistonImpulse = CfgMigration.Of(C, C.Bind("Piston", "max_impulse", 150000f, "Motor force per physics step (0 = infinite)"), "Pistao", "impulso_max");
            if (PistonImpulse.Value == 20000f) PistonImpulse.Value = 150000f;   // 0.12.3 (Andre: "make the pistons a lot stronger")
            if (PistonImpulse.Value == 500f) PistonImpulse.Value = 20000f;
            // 0.23.0 (Andre: "lower the value but keep it proportional to the stroke size"): 1 P/s per meter of the REAL stroke
            // (up to the Piston End, or the maximum, x the knob) — full 8 m = 8 P/s, 2 m = 2 P/s. New key: the old value (2.5) is left behind.
            PistonConsumption = C.Bind("Piston", "power_per_meter_v2", 1f, "Power (P/s) while moving, per meter of the stroke it actually makes (knob included): 8 m = 8, 2 m = 2");
            PistonRails = CfgMigration.Of(C, C.Bind("Piston", "rails", 0.75f, "Distance (m) of the 4 rails parallel to the axis that keep the piston stiff sideways (0 = no rails)"), "Pistao", "trilhos");
            PistonTilt = C.Bind("Piston", "max_tilt", 15f, new ConfigDescription("A piston bent more than this many degrees off its own axis for a second stops pushing and only holds where it is. A bent piston still trying to close its position error pushes sideways, which walks the whole ship across the ground with no input at all. It resumes by itself once it straightens. 0 = never give up", new AcceptableValueRange<float>(0f, 90f)));
            PistonCollides = C.Bind("Piston", "collide_with_load", true, "Let the piston's part and the part it carries collide with each other. On = the load can't sink into the piston (the rod stops inside a damper, pieces don't pass through each other) and there is a hard floor at retracted. Off = they ignore each other. Safe with rigid_joints");
            WholeUntilMoved = C.Bind("Piston", "whole_until_moved", true, "With rigid_joints: at launch a piston stays ONE part with what it carries (the way the garage builds it) until it gets a target, instead of splitting right away. Nothing sags or breaks at the start of the simulation");
            TurretWhole = C.Bind("Turret Seat", "whole_until_aimed", true, "At launch the turret seat stays ONE part with what is on its plates (the way the garage builds it) and only releases them the first time someone sits and moves the aim. Nothing on the plates falls or breaks at the start of the simulation");
            // 0.31.2: the turret pieces' own cap for [Piston] mass_ratio_max (they counted as 10 t each on a 103 t ship and the turret tore itself apart)
            TurretMassFactor = C.Bind("Turret Seat", "mass_factor_max", 4f, new ConfigDescription("What is on the turret's plates is counted heavier for the physics so the pivot holds it (see [Piston] mass_ratio_max), but never more than this many times its real mass. Higher holds the pivot better and loads the turret's blocks more when the ship turns. 0 = no cap", new AcceptableValueRange<float>(0f, 50f)));
            MassRatio = C.Bind("Piston", "mass_ratio_max", 10f, "With rigid_joints: while two parts are held by a piston weld, the lighter one counts as at least 1/N of the heavier one FOR THE PHYSICS (the blueprint does not change, nothing falls faster). This is what keeps the weld from sagging: the physics solver can't hold a joint between 71 t and 65 kg, but holds 10:1 fine. 0 = off");
            // 0.31.0: a cap per piece (Andre's 125 t ship had 24 pieces of 100-500 kg counted as 8.6-9.7 t each)
            MassFactorMax = C.Bind("Piston", "mass_factor_max", 25f, new ConfigDescription("With mass_ratio_max: a piece is never counted as more than this many times its real mass (its turning inertia: this number squared). A piece jointed to a single part and more than 50 times lighter than it is not counted heavier at all (it only carries its own weight). The kilograms added are given back by the heaviest part of the vehicle. 0 = no cap", new AcceptableValueRange<float>(0f, 200f)));
            SnapBack = C.Bind("Piston", "snap_back", true, "When the piece on top of the piston has drifted since the garage (it fell while the game was still rebuilding the ship), create the joint at the GARAGE pose and let the motor and rails pull it back, instead of welding it where it landed. Only right after leaving the garage");
            PistonRailsPerMeter = C.Bind("Piston", "rails_per_meter", 0.4f, "The 4 rails move apart with the piston: this many meters of spread per meter of the piston's full reach (8 m piston = 3.2 m). A long rod is a lever — a fixed, close spread lets it tilt and the tip ends up meters off. Never closer than rails (0 = fixed at rails)");
            if (PistonRails.Value == 0.5f) PistonRails.Value = 0.75f;   // 0.12.3: rails farther apart = firmer sideways

            // ---- [Bearing] ----
            ServoImpulse = CfgMigration.Of(C, C.Bind("Bearing", "servo_impulse", 3000f, "Force used to hold the angle when the bearing gets a direct signal (no motor)"), "Bearing", "servo_impulso");
            if (ServoImpulse.Value == 500f) ServoImpulse.Value = 3000f;
            BearingSupport = CfgMigration.Of(C, C.Bind("Bearing", "extra_support", 1f, "Distance (m) of the extra support points on the axis (one on each side of the face); keeps the bearing stiff under load (0 = off)"), "Bearing", "apoio_extra");
            if (BearingSupport.Value == 0.5f) BearingSupport.Value = 1f;
            ServoGain = C.Bind("Bearing", "servo_gain", 8f, new ConfigDescription("Servo: how early it starts braking. It asks for this many rad/s per radian of error, capped by servo_speed, so braking only begins once the error is below servo_speed/servo_gain (with 3 and 8, about 21 degrees from the target). A heavy load overshoots and oscillates: LOWER this to start braking sooner", new AcceptableValueRange<float>(0.5f, 40f)));
            ServoSpeed = CfgMigration.Of(C, C.Bind("Bearing", "servo_speed", 3f, "Max speed (rad/s) of the servo (steering) moving to the requested angle (3 = about half a turn per second)"), "Bearing", "servo_velocidade");
            ServoTipSpeed = C.Bind("Bearing", "servo_tip_speed", 4f, "Servo bearings (and the Gyro Bearing): top speed in m/s of the farthest block of the piece they turn. A big piece (a 12 t wing) then turns slower, so the game's joints in it are not torn apart by the turn; a small one (a steering knuckle) is still limited only by servo_speed. 0 = only servo_speed");   // 0.26.14
            ServoInvert = CfgMigration.Of(C, C.Bind("Bearing", "servo_invert", false, "true = inverts the direction the servo turns (if the steering is reversed)"), "Bearing", "servo_inverter");
            ServoDegrees = CfgMigration.Of(C, C.Bind("Bearing", "servo_degrees", 45f, "Servo angle (degrees) at signal 1 for bearings WITHOUT the side knob (old blueprints); bearings with the knob use the knob (10 to 360 degrees)"), "Bearing", "servo_graus");

            // ---- [Motor] ----
            MotorRamp = CfgMigration.Of(C, C.Bind("Motor", "ramp", 1.0f, "Seconds to reach max speed"), "Motor", "rampa");
            ForceReserve = C.Bind("Motor", "force_pool", 4f, new ConfigDescription("Each motor has this many times its max_impulse, shared equally among the bearings it drives (1 bearing gets all of it; 4 bearings get max_impulse each)", new AcceptableValueRange<float>(1f, 16f)));
            BrakeFactor = CfgMigration.Of(C, C.Bind("Motor", "brake_factor", 30f, "Brake force (motor's 4th yellow port, signal 0..1) = motor impulse x this factor (at signal 1). Mechanical brake: works without power/fuel and locks ALL wheels of the vehicle (bearings with no direct steering signal), not just the motor's"), "Motor", "freio_fator");
            if (BrakeFactor.Value == 3f || BrakeFactor.Value == 15f) BrakeFactor.Value = 30f;   // migrate the old defaults

            // ---- [Small/Medium/Large Motor] ----
            string[] lvl = { "Motor Pequeno", "Motor Medio", "Motor Grande" };   // old sections (migration)
            string[] lvlEn = { "Small Motor", "Medium Motor", "Large Motor" };
            // 0.5.0: much stronger and faster (Andre: 2 large motors still too weak for the car). Old default value -> new default.
            float[] v = { 5f, 8f, 12f }, imp = { 1000f, 4000f, 13000f }, pps = { 7f, 28f, 70f }, ppsOld = { 15f, 60f, 160f };   // 0.7.6: 2 large ones < 150 P/s
            float[] vOld = { 1.5f, 2.5f, 4f }, impOld = { 200f, 790f, 2600f };
            for (int i = 0; i < 3; i++)
            {
                MotorSpeed[i] = CfgMigration.Of(C, C.Bind(lvlEn[i], "max_speed", v[i], "Max speed (rad/s) at signal 1 (6.28 = 1 turn/s)"), lvl[i], "velocidade_max");
                MotorImpulse[i] = CfgMigration.Of(C, C.Bind(lvlEn[i], "max_impulse", imp[i], "Motor force (torque) per physics step; same scale as the Electric Thrusters' force (0 = infinite)"), lvl[i], "impulso_max");
                if (MotorSpeed[i].Value == vOld[i]) MotorSpeed[i].Value = v[i];
                if (MotorImpulse[i].Value == impOld[i]) MotorImpulse[i].Value = imp[i];
                MotorConsumption[i] = CfgMigration.Of(C, C.Bind(lvlEn[i], "power", pps[i], "Power (P/s) at signal 1 (small 7, medium 28, large 70: two large motors use 140)"), lvl[i], "consumo");
                if (MotorConsumption[i].Value == ppsOld[i]) MotorConsumption[i].Value = pps[i];
            }

            // ---- [Fuel Motor] ----
            // 0.8.0 (form5): fuel motors — same speeds, +50% force, 0.85 fuel/s with signal 1, 5 P/s of power
            string[] lvlC = { "Motor Comb Pequeno", "Motor Comb Medio", "Motor Comb Grande" };   // old sections (migration)
            string[] lvlCEn = { "Small Fuel Motor", "Medium Fuel Motor", "Large Fuel Motor" };
            for (int i = 0; i < 3; i++)
            {
                FuelMotorSpeed[i] = CfgMigration.Of(C, C.Bind(lvlCEn[i], "max_speed", v[i], "Max speed (rad/s) at signal 1"), lvlC[i], "velocidade_max");
                FuelMotorImpulse[i] = CfgMigration.Of(C, C.Bind(lvlCEn[i], "max_impulse", imp[i] * 1.5f, "Motor force (torque) per physics step with the fuel mixture at 100% efficiency (electric motor of the same size +50%)"), lvlC[i], "impulso_max");
            }
            FuelConsumption = CfgMigration.Of(C, C.Bind("Fuel Motor", "fuel_per_second", 0.85f, "Fuel burned per second at signal 1 (same as the Small Fuel Thruster)"), "Motor Comb", "consumo_combustivel");
            FuelPower = CfgMigration.Of(C, C.Bind("Fuel Motor", "power", 5f, "Power (P/s) used while turning (starter)"), "Motor Comb", "consumo_energia");

            // ---- [Wheel] ----
            // 0.31.26 (item 13): default 1.5 -> 2.0. A normal block and the terrain have 1.7 with GeometricMean [recon_managed/Core__g3_core_04.cs:360-370;
            // recon/PlanetsCollidersUpdate.cs:178-181]; with different policies Unity Physics takes the larger one (Maximum), so 1.5 gave max(1.5, 1.7) = 1.7,
            // the same as any block. 2.0 beats the ground and stays below the Offroad (2.2).
            // 0.31.27: description corrected. Unity Physics takes the combine rule with the higher number of the two materials (GeometricMean 0,
            // Minimum 1, Maximum 2, ArithmeticMean 3) [UP Dynamics/Material/Material.cs:156-166, 214-230]; the wheel asks for Maximum (Wheels.cs)
            // and every material of the game is GeometricMean or Minimum (blocks and ground 1.7, dampers/springs 1.7 x 4, animals 0.9, player 1.4)
            // [recon_managed/Core__g3_core_04.cs:368-381; EPC_SCExtendableDamper.cs:40-45; recon/PlanetsCollidersUpdate.cs:178-181], so against
            // anything of the game the higher of the two frictions is used. The "~0.5" was Unity's default, not the game's.
            WheelFriction = CfgMigration.Of(C, C.Bind("Wheel", "friction", 2.0f, "Friction of the Sports wheels. The wheel uses the 'Maximum' combine rule, which wins over the game's own rule, so on contact the higher of the two frictions is used. Game blocks and the ground have 1.7 (dampers and springs 6.8): a value below 1.7 changes nothing against them"), "Roda", "atrito");
            WheelFrictionOffroad = CfgMigration.Of(C, C.Bind("Wheel", "offroad_friction", 2.2f, "Friction of the Offroad wheels"), "Roda", "atrito_offroad");
            NoCollision = CfgMigration.Of(C, C.Bind("Wheel", "no_collision_at_start", 1f, "At start a wheel doesn't collide with the parts above it in the bearing chain (steering wheel vs. chassis); collision is turned back on this many seconds after the start queue finishes (0 = collision is never disabled)"), "Roda", "sem_colisao_partida");
            if (NoCollision.Value == 3f) NoCollision.Value = 1f;

            // ---- [Physics] ----
            BaseReinforce = CfgMigration.Of(C, C.Bind("Physics", "base_reinforcement", 20f, "Multiplies the strength (breakForce, game default 3300) of the game joints that hold a bearing/piston to its base (1 = unchanged; capped at 1e7)"), "Fisica", "reforco_base");
            if (BaseReinforce.Value == 1000f) BaseReinforce.Value = 20f;   // 0.7.2 -> back to 0.7.1's
            StartTime = CfgMigration.Of(C, C.Bind("Physics", "soft_start", 0.3f, "Seconds bearings/pistons stay locked right after spawning (prevents spinning/breaking when the simulation starts; 0 = off)"), "Fisica", "partida_suave");
            if (StartTime.Value == 1f) StartTime.Value = 0.3f;   // 0.8.0: the problem was the servo's 360 (0.7.14), it can be quick to build up
            StartPause = CfgMigration.Of(C, C.Bind("Physics", "start_stagger", 0.05f, "After the soft start, unlocks ONE joint at a time with this pause (s) between each: first the ones attached to the chassis, then the ones above them (wheel on the steering knuckle); on the same level the lightest first (0 = unlock all at once)"), "Fisica", "partida_pausa");
            if (StartPause.Value == 0.3f) StartPause.Value = 0.05f;   // 0.8.0
            FitWait = C.Bind("Physics", "fit_wait", 3f, new ConfigDescription("Seconds a bearing/piston waits for its two sides to line up before giving up and creating the joint where they are. It gives up earlier if the fit stops getting closer. The first launch of a session is the slow one: the game is still rebuilding the parts", new AcceptableValueRange<float>(0f, 10f)));
            ZeroGravLaunch = C.Bind("Physics", "zero_gravity_on_launch", true, "At launch, hold the world's gravity at zero for a fraction of a second while the game finishes splitting the ship into parts. Nothing falls during that window, so pistons/bearings/turret are born in the pose you built instead of catching pieces mid-fall. Restored right after, with no catch-up jolt (the clock never stops). Only right after leaving the garage");
            Stiffness = C.Bind("Physics", "rigid_joints", true, "Pistons and servo bearings hold the position you asked for EXACTLY, like a weld whose length the mod sets, instead of pushing toward it with a force limit. No sagging under load, no wobble, doors close flush, and a chain of them stays square. A piston that is blocked simply stops instead of fighting (it never pushes more than 8 cm past where the rod really is). Off = the old behaviour, a position motor with max_impulse, rails and solver tuning");
            SolverIterations = CfgMigration.Of(C, C.Bind("Physics", "solver_iterations", 20, "Physics solver iterations while a piston/bearing is active (the game uses 8; more = less wobbly chains of parts — a single joint does not get stiffer — and costs CPU; 0 = unchanged)"), "Fisica", "iteracoes_solver");
            IterationsAlways = C.Bind("Physics", "solver_iterations_rigid", false, "Also raise the solver iterations (solver_iterations) while only welds, locked or free bearings are active. Stiffer joint chains (a car axle hanging on a steering bearing and mini pistons), but the whole world's physics costs more CPU. Once raised it stays until the game restarts");   // 0.26.12
            WeldFirstCfg = C.Bind("Physics", "weld_before_solver", true, "With rigid_joints: the parts held by a piston that is not moving or a servo bearing that is not turning go into the physics solver as ONE body, the way the same ship would if those pieces were welded on. Two things happen every physics tick: (1) while the game applies thrust and drag, every part has its real mass and real centre of mass (the heavier masses the mod gives light pieces so the welds hold exist only while the solver runs); (2) right before the solver, the welded parts are put on the single rigid motion that carries exactly the push and the spin they already have, so nothing is added or removed. Made for the ship with a gate on pistons/bearings that leans under thrust and in the air while the same ship with a welded wall does not. Off = as up to 0.31.22 (the solver's masses stay in all the time)");   // 0.31.23
            DragAsOneCfg = C.Bind("Physics", "welded_drag_as_one", true, "Needs weld_before_solver. The game's drag never turns a part: each part gets one braking number and its speed and spin are both scaled by it, and the number comes from that part's own shape only. A ship in one piece just slows down; the same ship with a gate on pistons/bearings has each gate piece braked on its own, far from the ship's centre, and that turns the ship in the air. On = a welded group is braked as one part: the total braking the game applied stays the same, the turning it caused is removed. Off = each part keeps its own drag, as up to 0.31.23");   // 0.31.24
            ContactScalingCfg = C.Bind("Physics", "contact_inertia_scaling", false, "Off = while gravity is near zero (below 0.05 m/s2), turn off the solver's contact inertia scaling (the game has it on), which shrinks the turning part of contact pushes and lets parts of a ship touching each other create spin. With gravity the game's value is always used. On = the game's value everywhere");   // 0.31.26 (U1)
            if (SolverIterations.Value == 12) SolverIterations.Value = 20;   // 0.12.3: firmer joints (the piston gave way under load)
            if (SolverIterations.Value == 8) SolverIterations.Value = 12;   // 0.7.9

            // ---- [Turret Seat] ----
            // 0.12.0: turret seat
            TurretSens = CfgMigration.Of(C, C.Bind("Turret Seat", "sensitivity", 0.1f, "Aim degrees per mouse pixel"), "Torreta", "sensibilidade");
            TurretGain = CfgMigration.Of(C, C.Bind("Turret Seat", "gain", 12f, "Servo: requested speed (rad/s) per radian of error (higher = stiffer/faster)"), "Torreta", "ganho");
            TurretMaxSpeed = CfgMigration.Of(C, C.Bind("Turret Seat", "max_speed", 720f, "Max turret rotation speed (degrees/s)"), "Torreta", "velocidade_max");
            TurretLinkAccel = CfgMigration.Of(C, C.Bind("Turret Seat", "link_acceleration", 360f, "Max acceleration (degrees/s2) of a structure attached to 2+ faces of the turret (a LINK), and of the whole ship when the base and the plates are on the same structure. Lower = smoother (less jolting on the ground)"), "Torreta", "elo_aceleracao");
            TurretSeatMode = CfgMigration.Of(C, C.Bind("Turret Seat", "seat_mode", "A", "cockpit: seat visible and you turn with it (nothing crosses your view); A: seat invisible to you only; B: seat visible and you sit at player_position_b. Num* cycles it in adjust mode (Num/)"), "Torreta", "modo_banco_v2");
            // 0.12.9: the new model already brings the seat 35 cm lower and 2 cm forward (the adjustment Andre made) -> the new adjustment is the old one minus that.
            // seat_adjust_v2 (0.12.8) is only read for this conversion and then removed from the .cfg.
            var adjustV2 = Config.Bind("Torreta", "banco_ajuste_v2", "0;0;0", "(old, only read for migration)");
            TurretSeatAdjust = CfgMigration.Of(C, C.Bind("Turret Seat", "seat_offset", "?", "Seat offset x;y;z (m) relative to the model's position (adjust mode: Num/, Num1, numeric keypad). The model already places the seat at (0; -0.85; 0.02) and the plate/rod follow the offset"), "Torreta", "banco_ajuste_v3");
            if (TurretSeatAdjust.Value == "?")
            {
                var bv = JointSystem.Read3(adjustV2.Value, Unity.Mathematics.float3.zero);
                bv = new Unity.Mathematics.float3 { x = bv.x, y = bv.y + 0.35f, z = bv.z - 0.02f };
                if (System.Math.Abs(bv.x) < 0.005f && System.Math.Abs(bv.y) < 0.005f && System.Math.Abs(bv.z) < 0.005f) bv = Unity.Mathematics.float3.zero;
                TurretSeatAdjust.Value = JointSystem.Scale3(bv);
            }
            try { Config.Remove(adjustV2.Definition); Config.Save(); } catch (Exception) { }
            TurretPlayerB = CfgMigration.Of(C, C.Bind("Turret Seat", "player_position_b", "0;0;0", "Mode B: where you sit, x;y;z (m) in the block (0;0;0 = block center = pivot center)"), "Torreta", "jogador_posicao_b");
            TurretViewA = CfgMigration.Of(C, C.Bind("Turret Seat", "view_position_a", "0;0.42;-0.46", "Mode A: where your view is, x;y;z (m) in the block (0;0;0 = pivot center)"), "Torreta", "visao_a_v2");
            TurretViewRotates = CfgMigration.Of(C, C.Bind("Turret Seat", "view_a_rotates", true, "Mode A: true = the view turns with the rings (always stays at the same spot in the cockpit); false = fixed point in the block"), "Torreta", "visao_a_gira");
            TurretInertia = CfgMigration.Of(C, C.Bind("Turret Seat", "infinite_inertia", true, "The seat's part is not turned by any force (weight on the plates doesn't make it tip); false = only the servo holds it"), "Torreta", "inercia_infinita");
            TurretCamera = CfgMigration.Of(C, C.Bind("Turret Seat", "camera", true, "While seated in the turret seat the view turns with the aim (false = game camera)"), "Torreta", "camera");
            TurretReach = C.Bind("Turret Seat", "bindings_reach", 4.5f, new ConfigDescription("Seated in the turret seat: seat key bindings work for bound parts up to this far (m) from you. The game itself only accepts ~2.1 m (4.5 m is how far you can click a part to bind it)", new AcceptableValueRange<float>(2.12f, 10f)));
            TurretCursorKey = CfgMigration.Of(C, C.Bind("Turret Seat", "cursor_key", "LeftCtrl", "Key (Input System name) that shows/hides the cursor while seated in the turret seat (aiming pauses while the cursor is shown)"), "Torreta", "tecla_cursor");

            // ---- [Recoupler] ----
            RecouplerSlack = CfgMigration.Of(C, C.Bind("Recoupler", "tolerance", 0.5f, "Max distance (m) between the Recoupler and the face released by the Decoupler for it to recouple"), "Recoupler", "folga");
            RecouplerMerge = CfgMigration.Of(C, C.Bind("Recoupler", "merge", true, "true = after pulling into place, merges the two parts into one (the opposite of the decoupler); false = only locks them with a fixed joint"), "Recoupler", "juntar");
            RecouplerPull = CfgMigration.Of(C, C.Bind("Recoupler", "pull_time", 0.6f, "Seconds to pull the recoupler into the right position"), "Recoupler", "tempo_puxar");
            RecouplerAngle = CfgMigration.Of(C, C.Bind("Recoupler", "max_angle", 30f, "Only couples/pulls if the recoupler is misaligned by at most this angle (degrees) relative to the socket"), "Recoupler", "angulo_max");

            // ---- [Beacon] ----
            BeaconConsumption = CfgMigration.Of(C, C.Bind("Beacon", "power", 5f, "Power (P/s) the Beacon draws from its power port; without power it doesn't show on radars (0 = needs no power)"), "Beacon", "consumo");
            BeaconFar = C.Bind("Beacon", "keep_simulated_far", true, "true = a loose part (a probe, a dropped rocket) that carries a powered Beacon keeps being simulated when it is more than 100 km from you: physics, camera, transmitter and thruster keep working (the game normally switches every part beyond 100 km off — the mod takes over that rule, same limit, beacons exempt). false = the game's rule as is");
            Far.Enabled = BeaconFar.Value;
            BeaconOwn = C.Bind("Beacon", "hide_on_own_vehicle", true, "true = the beacon disappears from every radar while you are on its own vehicle (so your radar doesn't show you). The game's radar can't hide it from just one radar, so with the Replay free camera (or watching from the garage) it is gone everywhere: set false to always show it");
            BeaconHide = CfgMigration.Of(C, C.Bind("Beacon", "hide_within", 40f, "The Beacon disappears from the radar when the player is closer than this distance (m): the radar you are looking at is the one on the beacon's own vehicle (0 = always shown)"), "Beacon", "esconder_perto");
            IconSlot = CfgMigration.Of(C, C.Bind("Beacon", "icon_slot", 8, "Preferred game icon slot for the beacon drawings (0..9). They only go over icons nothing in the world is using right now; the next free one is used otherwise"), "Beacon", "slot_icone");
            IconBeacon = CfgMigration.Of(C, C.Bind("Beacon", "icon", -1, "Beacon icon on the radar: -1 = the mod's drawing (signal waves) over icon_slot; 0..9 = an unmodified game icon (2 = package)"), "Beacon", "icone");

            // ---- [Seat] ----
            SeatReach = C.Bind("Seat", "bindings_reach", 4.5f, new ConfigDescription("Seated in a normal game seat: seat key bindings work for bound parts up to this far (m) from you. The game itself only accepts ~2.12 m; 4.5 m is how far you can click a part to bind it. Set 2.12 to leave the seat exactly as the game does", new AcceptableValueRange<float>(2.12f, 10f)));

            // ---- [Signal] ----
            SignalRelink = C.Bind("Signal", "fix_stuck_ports", true, "Repairs a Wireless Transmitter stuck with no signal (amber LED) after the parts split: its port kept no direction because the game resolves that only once, and asks the game to resolve it again. Only acts on a port that is actually stuck, and gives up after a few tries");

            // ---- [Bonus] ----
            // 0.24.0 (Andre: "+2000 of each micro frame"): extra amount of any block, including another mod's.
            // 0.29.0: an entry can name a whole category, and the bonuses that used to be fixed in the code (cables per motor, the car kit's
            // Radar) are entries of this default value now — so every extra amount is in this one line.
            BonusExtras = C.Bind("Bonus", "extra_blocks", "SC_DataCable=600, SC_PowerCable=300, SC_Radar=1",
                "Extra amount of blocks in the inventory, separated by commas. Each entry is name=amount and the amount is ADDED to what the game gives. "
                + "The name can be a block (SC_FrameAA=400), the name the inventory shows (Power Router=5), a group of the game (Frame=400 or group:Frame=400 "
                + "gives it to EVERY frame; also Glass, Pipe, Nanopipe, Nanoframe, Label, MathBlock, DigitalOrAnalogMeter, BasicControllers) or an inventory tab "
                + "with the tab: prefix (tab:Electronics=50). A group and a tab with the same name are NOT the same list: group Frame is the normal frames, tab "
                + "Frame also has the nanoframes and the micro frames. A later entry wins over an earlier one. config/CrapMechanics/blocks.txt lists every name "
                + "that works, with the group of each block. Works for other mods' blocks too; a block that is not unlocked yet is skipped. Empty = none");

            // 0.30.0 (Andre: "my ship got absurdly heavy"): more force for every thruster, fan and RCS block
            ThrusterForce = C.Bind("Thrusters", "force_multiplier", 1f, new ConfigDescription("Multiplies the force of EVERY thruster, fan and RCS block of the game, on top of each block's own slider below (electric, fuel, maneuvering, atmospheric, ducted fans, flat fans...). 1 = the game's own force, 3 = three times as much. It is applied to the blocks of the ship right away, also in the middle of the simulation. Electric power and fuel use are NOT multiplied: more force costs the same", new AcceptableValueRange<float>(0.1f, 100f)));
            // 0.31.4: a nave pesada com thrusters a x2,96 se despedacou — as juntas do jogo passavam de 3x o limite
            ThrusterJoints = C.Bind("Thrusters", "reinforce_joints", true, "While any thruster has a multiplier above 1, every game joint (between blocks) is as strong as the game made it times the largest multiplier in use, so a ship pushed x3 harder also holds x3 more (it gets that much tougher in crashes too). Back to 1 (or off) = the game's own strength again. Off = the game's own strength always");
            // 0.30.0: one slider per block type, on top of the global one (name in the inventory in the description). SC_DamagedLargeFuelThruster is a wreck, left out.
            foreach (var (prefab, key, shown) in new[] {
                ("SC_SmallElectricThruster", "small_electric_thruster", "Small Electric Thruster"), ("SC_MediumElectricThruster", "medium_electric_thruster", "Medium Electric Thruster"), ("SC_LargeElectricThruster", "large_electric_thruster", "Large Electric Thruster"),
                ("SC_SmallFuelThruster", "small_fuel_thruster", "Small Fuel Thruster"), ("SC_MediumFuelThruster", "medium_fuel_thruster", "Medium Fuel Thruster"), ("SC_LargeFuelThruster", "large_fuel_thruster", "Large Fuel Thruster"),
                ("SC_SmallManeuveringThruster", "small_maneuvering_thruster", "Small Maneuvering Thruster"), ("SC_MediumManeuveringThruster", "medium_maneuvering_thruster", "Medium Maneuvering Thruster"), ("SC_LargeManeuveringThruster", "large_maneuvering_thruster", "Large Maneuvering Thruster"),
                ("SC_BidirectionalElectricManeuveringThruster", "bidirectional_maneuvering_thruster", "Bidirectional Maneuvering Thruster"), ("SC_SmallSolidFuelThruster", "small_solid_fuel_thruster", "Small Solid Fuel Thruster"),
                ("SC_AtmosphericThruster", "atmospheric_thruster", "Atmospheric Thruster"), ("SC_AtmosphericFan", "atmospheric_fan", "Atmospheric Fan"), ("SC_AtmosphericLiftFan", "atmospheric_flat_fan", "Atmospheric Flat Fan"),
                ("SC_DuctedFan", "ducted_fan", "Ducted Fan"), ("SC_ElectricLiftThruster", "electric_flat_thruster", "Electric Flat Thruster"), ("SC_GimbalThruster", "rcs_thruster", "RCS Thruster"),
                ("SC_MiniElectricThruster", "mini_electric_thruster", "Mini Electric Thruster") })
                Thrusters.PerBlock[prefab] = C.Bind("Thrusters", key, 1f, new ConfigDescription("Force of the " + shown + " (multiplies force_multiplier). 1 = the game's own force", new AcceptableValueRange<float>(0.01f, 100f)));

            // ---- [Remote Computer] ---- (remote control, delivery 1: RemoteComputer.cs / Phone.cs)
            RemotePower = C.Bind("Remote Computer", "power", 2f, new ConfigDescription("Power (P/s) the Remote Computer draws from its red power port. Without power every row output is 0, nothing is sent on any channel and its LEDs are off (0 = needs no power)", new AcceptableValueRange<float>(0f, 100f)));
            RemoteRange = C.Bind("Remote Computer", "range_m", 2000f, new ConfigDescription("The phone drives the powered Remote Computers on its channel up to this far (m) from the player (2000 = the game's wireless range)", new AcceptableValueRange<float>(10f, 2000f)));
            RemoteLongPress = C.Bind("Remote Computer", "long_press_s", 0.35f, new ConfigDescription("Holding the action key (E) this long on a Phone Dock's phone, in flight, takes the phone to your hotbar; a shorter tap opens its app (and on an empty dock puts the phone back)", new AcceptableValueRange<float>(0.2f, 2f)));
            RemoteHand3D = C.Bind("Remote Computer", "hand_3d", true, "The phone in your hand (selected in the hotbar, in flight) is drawn in 3D in front of the camera with the dock phone's own model. Off = a flat phone drawn by the mod in the lower right corner instead");
            RemoteWindow = C.Bind("Remote Computer", "wireless_send_ticks", 3, new ConfigDescription("A row set to 'cable + channel' sends on its wireless channel only this many game ticks after each command (a held button: every tick while held, then this many ticks of the off value) and then stands by. A game Wireless Transmitter that receives keeps the last value it got, so the channel stays free: two computers on the same channels only clash if both send in the same ticks", new AcceptableValueRange<int>(1, 50)));
            RemoteHandLow = C.Bind("Remote Computer", "hand_low", -0.88f, new ConfigDescription("How low the phone rests in your hand: the screen height of its centre, where -1 is the bottom edge of the view and 0 the middle. -0.88 hides about a third of the phone below the edge (so the missing hand is not noticed); -0.62 shows the whole phone; -0.95 hides almost half. The focused phone (right click) is not affected", new AcceptableValueRange<float>(-1.2f, -0.3f)));
            RemoteGameBar = C.Bind("Remote Computer", "hotbar_game_look", true, "The in-flight hotbar (shown while you carry something) is a copy of the game's own garage hotbar: same place, frames, numbers and fonts. Off = the simple bar drawn by the mod (also used automatically if the copy cannot be made)");

            // ---- [Multiplayer] ----
            // 0.24.0-mp (multiplayer): the game isn't lockstep — the logic runs on every machine and the server rules the pose
            // of the parts (see Multi.cs). In a solo game (NetworkStatus.Off) none of this changes the behaviour.
            MpOwnerOnly = C.Bind("Multiplayer", "host_only", true, "The server runs the mod's physics (joints, motors, pistons, brake, turret, recoupler, knob restore); a client only draws the mod's parts from the poses the server sends, and sends the turret seat aim of whoever sits there. Off = every machine simulates its own copy (not recommended)");
            MpBreakClient = C.Bind("Multiplayer", "break_on_client", false, "(debug) Let a client break the game's joints at launch too. Off (default) = the server splits the parts and the game sends the split to everyone");
            // 0.31.6: [Slip Ring] grow_circuit_list — a circuit with no room left for the power bridge's entity gets a bigger list
            RingRebuildInTick = C.Bind("Slip Ring", "rebuild_circuits_in_tick", true, "When the game has to rebuild its electric circuits (a part was split or merged, a cable burned, a switch cut a circuit), it rebuilds them and updates the power in the same tick, and the slip ring's power bridge could only get into the new circuit on the next tick: one tick with no power on the far side. With this on, the mod runs the game's own rebuild at the start of the tick, puts the bridge into the new circuit and only then lets the game update the power. Turn it off if the game misbehaves with it; the log says once whether it is working");
            // 0.31.28 seal 2 (Andre: "a MicroFrame with its face closed still lets the air through"): see FrameFaces.cs / SealWatch.cs
            FrameSeamCfg = C.Bind("Air", "frame_seam_like_game", true, "On by default (Andre: a closed MicroFrame face that leaks is a bug to fix). The game closes a seam between two closed frame faces when the gap is within 2 % of the face size past each edge: 1 cm for its 0.5 m frames, but only 2.5 mm for a 0.125 m MicroFrame face, so a gate of MicroFrames on its own part (a few mm of gap) leaks where a gate of game frames would not. On = in flight, the MicroFrames blocks (only them: the game's own frames are never touched) get faces that reach exactly 1 cm past each edge, the same as a 0.5 m game frame (nothing more). Open faces stay open and still let the air through; the garage keeps the original faces. Off = the game's own 2 % on every frame size. Without this: close every face of the gate's frames (a fully closed frame counts as a solid block) or use the Rubber Seal");
            // 0.31.30 sound (Andre: "the air is sealed now, but the thrusters still come in"): see SoundSeal.cs
            SoundSealCfg = C.Bind("Air", "sound_sees_small_blocks", true, "The game's sound only sees blocks of 0.5 m or more of the part you are in: MicroFrames, Rubber Seals, the mod's blocks and every block of another part (a gate on pistons or bearings) do not muffle anything, so a gate that closes the air still lets the thrusters' noise in. On = the mod adds, for the sound only and only on your machine, the sides of the 0.5 m cells that MicroFrames, Rubber Seals and the parts on joints really close at this moment, tested with 49 short rays per side and the same rules as the air test (a side closes only if every ray is blocked; an open gate or a gap lets the sound through). Nothing is saved or sent to other players. Off = the game's own sound");
            // 0.31.35 (performance): the mod's sound cells go back to the cheap kind when nothing around them moves
            SoundSettleCfg = C.Bind("Air", "sound_cells_settle", true, "With sound_sees_small_blocks. The mod's sound cells next to a part on joints (a gate) or next to you are the 'moving' kind: the game redoes 27 cells of its sound grid for each of them on every update, on one thread, even while nothing moves (1300 to 1600 of them on a big ship). On = a new cell starts as the moving kind, so the game sees it at its next update with no rebuild of its whole grid (0.31.36; before, every batch of new cells asked for a rebuild, about 3 per second while a ship was being read; a new cell on the edge of the game's grid or outside it still asks for one, the grid has to grow); a still cell whose sides change becomes the moving kind at once, with no rebuild either; a moving-kind cell whose sides did not change for 5 s (10, 20... up to 60 s for a cell that keeps changing; 2 s for a new cell that never changed), that is not right around you and has no side waiting to be tested again, becomes the still kind again, all such cells in one go with one rebuild, at most every 10 s (every 4 s while 128 or more new cells wait, that is while a ship is being read). The sides given to the game are exactly the same either way, so the sound is the same. Off = as up to 0.31.34 (a cell that became the moving kind stays so, and a new cell or a still cell that changes asks for a full rebuild)");
            SealSolidCfg = C.Bind("Cheats", "rubber_seal_solid_percent", 100f, new ConfigDescription("CHEAT when below 100 (0.31.30, Andre: 'the gate is perfect and there is no room left in the ship'). How much of each Rubber Seal pushes things, counted from the glued face. 100 = the honest seal: the whole block is solid and only the 3 cm lip (2 cm on the mini) is soft. 50 = the half of the block nearest the lip is soft too: a gate can press into it as if the rubber squeezed. 0 = the whole seal is soft. The soft part still blocks the air, so below 100 the seal closes more than real rubber could. Steps of 12.5 % (1/8 of the block; a value between two steps keeps the more solid one). Changes the ships in flight within a second; the garage always uses the whole block", new AcceptableValueRange<float>(0f, 100f)));
            // 0.31.36: Bearing with Slip Ring (RingVisual.cs)
            RingEmbeddedTurns = C.Bind("Slip Ring", "embedded_ring_turns", true, "The slip ring built into the Bearing with Slip Ring is drawn turning with the piece on top, and the cables plugged into it stay with that piece. Off = the ring is drawn still; data and power pass the same. Takes effect at the next launch");
            RingGrowList = C.Bind("Slip Ring", "grow_circuit_list", true, "The power bridge of a slip ring adds one invisible entity to the game's list of components of each circuit it touches (one per circuit, shared by every slip ring of that circuit). That list has a fixed size; when it is already full of the game's own parts, on = make it bigger the same way the game does when it adds a part, off = leave it and that circuit's slip rings carry no power (the log says which)");
            MpRingClient = C.Bind("Multiplayer", "ring_on_client", true, "Run the Slip Ring data/power bridge on clients too (the game already syncs port values and electric power). Turn off if a client shows double power use across a slip ring");
            MpFakeClient = C.Bind("Multiplayer", "fake_client", false, "(debug) Pretend this machine is a client, with no network at all: nothing of the mod moves, but the client's drawing code runs — to see what a client gets");
            MpNetLog = C.Bind("Multiplayer", "net_log", false, "(debug) Log every message of the mod's own network channel (hello, turret seat aim)");
            MpLoopback = C.Bind("Multiplayer", "test_loopback", false, "(test, solo game) The turret seat aim goes through the mod's network message path and comes back as if a player on another machine had sent it: tests the server side of a client's aim on one machine");

            // ---- [Debug] ----
            // 0.31.30: every [Debug] bind is here, in one group, the master switch first (the in-game menu lists the settings in bind order: before, Debug showed up in three places)
            DebugToolsCfg = C.Bind("Debug", "debug_tools", false, "MASTER SWITCH of the debug lines and tests below (0.31.30, Andre: 'hard to find'). false = momentum_watch, seal_watch, sound_watch, drag_watch, no_drag, power_watch, strain_watch, flight_trace, trace, perf_compare, perf_split_waits, system_timing, sound_compare and the 'performance+:' lines are all OFF, whatever each one says; true = each one does what it says. The keys (mark_error_key, circuit_dump_key) and the garage panel (thrust_balance_on_h) are not affected. Leave it false to play");
            MomentumWatchCfg = C.Bind("Debug", "momentum_watch", false, "(log only) A ship flying with no air can only have its spin changed from outside, by its thrusters: the mod's welds, the servos, the pistons and the car resting on the floor only pass momentum from one piece to another, they cannot create it. Every half second this adds up the spin momentum of every part of the vehicle and says how fast the total is drifting, and what that drift is worth as degrees per second added to the hull each second. Drifting with the thrusters off means the mod is making spin out of nothing, and the number says how much. Costs almost nothing and needs no special flight");
            SealWatchCfg = C.Bind("Debug", "seal_watch", false, "(log only) Every 2 s while you are inside a ship (on the server/host, where the game does this test), repeats the game's own 'is this room closed' test from where you stand: 54 small balls of 3.5 cm shot in all directions, the same as the game. One 'seal watch:' line says how many are blocked, how many get out, and for the ones that get out which block and which face they crossed last and how close they passed to a closed face. Use it to find where your room leaks. A sealed room writes a line only when it changes (or every 30 s). Costs a few ms every 2 s");
            DragWatchCfg = C.Bind("Debug", "drag_watch", false, "(log only) Every 0.5 s one 'drag watch:' line about the game's own drag: the air density with real decimals (the flight line rounds it to 0.000, which hides whatever is left in high orbit), and, per part, the two numbers the game keeps for its drag and how much that part's speed and spin changed in the step the drag runs in. The game computes drag per PART, against that part's own shape, so a ship with an open gate is several bodies taking the wind instead of one. A part with no thruster pushing it that tick gets nothing else in that step, so for that one the change IS the drag");
            NoDragCfg = C.Bind("Debug", "no_drag", false, "(TEST ONLY — not a way to play) Zeroes the game's drag on every part, every tick. For telling apart 'the gate's drag is turning my ship' from 'something else is'. With this on nothing in flight is slowed by the air, so a bad manoeuvre or a pass near the sun stops costing what it should. The log warns once while it is on");
            PowerWatchCfg = C.Bind("Debug", "power_watch", false, "(log only) Every game tick, checks every circuit fed through a slip ring and every electric thruster for a component that asks for power and is not supplied. One 'power watch:' line per episode: what, for how many ticks, and what was happening in the tick it began (the game rebuilt a circuit, a joint changed, the feeding side had no power, or nothing). Costs a few hundredths of a millisecond per tick");
            StrainWatchCfg = C.Bind("Debug", "strain_watch", false, "(log only) Why blocks come off in flight. The game breaks a block off when the link between two blocks gets more strain than its break force; the strain comes from how hard each block is jerked in one physics step (and from collisions). Every frame this reads every link of the ship: a 'strain: BROKE' line says which two blocks came apart, how close to the limit the link was, the jolt of both blocks and the nearest mod joint; 'is OPEN' when a bearing or piston is more than 25 cm or 10 degrees off its fit; every 2 s the 5 links nearest to breaking. Costs a little every frame on a big ship: turn it on only to investigate");
            CircuitDumpKey = C.Bind("Debug", "circuit_dump_key", "Home", "(debug) Key that writes the ship's whole circuit (every block with data ports, each port's live value and where it comes from) to logs/circuits/circuit-HHmmss.txt. Use it in the simulation. Empty = off");
            TraceCfg = C.Bind("Debug", "trace", false, "(debug) Writes one line per step of the mod cycle and per patched call, every frame. Use it to find where a native crash happens: the log is flushed line by line, so the LAST line is the last step that ran. It makes the log huge — turn it on only to reproduce a crash");
            MarkKey = CfgMigration.Of(C, C.Bind("Debug", "mark_error_key", "", "(debug) Key that marks an error: log line + screenshot + note window. Empty = off (default). E.g.: F8"), "Debug", "tecla_marcar_erro");
            FlightTraceCfg = C.Bind("Debug", "flight_trace", false, "(log only) Every 2 s for the first 3 minutes of each launch (then every 10 s) one 'trace:' line: which way the ship is turning (in the hull's axes), the centre of mass with the real masses and with the physics masses, the thrust of the thrusters that are pushing and the torque it makes around the real centre of mass, the same with every thruster at its garage pose, and the thrusters off the hull that moved from it. Changes nothing in the game");
            ThrustBalanceCfg = C.Bind("Debug", "thrust_balance_on_h", false, "In the garage, while the game's H display is on: a panel in the top right corner with the ship's mass and, for each group of thrusters pushing the same way (up, forward...), how far from the centre of mass its thrust line passes, which way it turns the ship and how far to move them. Reads only");
            MarkWindow = C.Bind("Debug", "mark_note_window", false, "true = the mark key (F8) also opens a small window to type what you saw (the game loses focus). false = it only takes the screenshot and writes the black box snapshot to the log, and the game keeps running");
            TurretAdjust = CfgMigration.Of(C, C.Bind("Debug", "turret_adjust_numpad", false, "(debug) Numpad / toggles the turret seat adjust mode (the numpad moves the seat/view). Off by default"), "Debug", "ajuste_turret_numpad");
            SoundWatchCfg = C.Bind("Debug", "sound_watch", false, "(log only) Every 2 s while you are inside a ship part, one 'sound watch:' line: how many 0.5 m cells [Air] sound_sees_small_blocks gave to the game's sound and how many of their sides are closed, which cell you are in, the game's sound grid, and for the loudest thruster the path the game finds from it to you and how much the game muffles it now (0.92 = the most the game ever muffles). Fly with the gate closed and then open and compare");

            // 0.31.35 performance (Andre: "the game/mod performance is really bad"): measuring and proof switches, see Perf.cs / Ecs.cs / SoundSeal.cs
            PerfCacheCfg = C.Bind("Debug", "perf_read_cache", true, "(not a debug line: works without debug_tools) The mod remembers, between two changes of the world's structure, the answers to 'does this part exist / has it this component / where is its data' instead of asking the game again thousands of times per physics tick. The numbers read are the same memory as before, so nothing the mod computes changes. One answer in 256 is still checked against the game; a difference is logged ('read cache: DIFFERENCE') and turns this off until the game restarts. Off = every answer is asked from the game, as up to 0.31.34");
            PerfCompareCfg = C.Bind("Debug", "perf_compare", false, "(proof, slow) Checks EVERY answer of perf_read_cache against the game and uses the game's answer. The 'performance+:' line then says how many answers were checked and how many were DIFFERENT: it must be 0. Fly, move the gate, launch and recall with this on. It saves no time while on");
            PerfSplitWaitsCfg = C.Bind("Debug", "perf_split_waits", false, "(measuring) The time of each of the mod's hooks in the performance line includes waiting for the game's jobs (the first hook that touches the physics data after the solver waits for the solver). On = before each bundle of hooks the mod first lets the game finish its jobs and times that wait by itself ('performance+: perf_split_waits'), so the hook times are the mod's own work. The jobs do the same work; only the moment the game waits for them moves. Leave it off to play");
            SystemTimingCfg = C.Bind("Debug", "system_timing", false, "(measuring) Turn it on in flight: for 10 s every game system inside the physics tick is timed one by one, then two 'system timing:' lines list the groups and the 25 slowest systems (ms per tick) and this switches itself off. During those 10 s the mod runs the game's own loop over those systems itself (same systems, same order) with a clock around each");
            SoundCompareCfg = C.Bind("Debug", "sound_compare", false, "(proof) Each time [Air] sound_cells_settle turns moving-kind cells into still ones, the game's finished sound grid (the distance of every cell to you) is hashed before and after, and a 'sound compare:' line says SAME or DIFFERENT (or that it could not be compared because you or a cell changed in between)");

            // 0.24.0: removes from the .cfg the keys that no version of the mod reads anymore (old Portuguese sections and keys
            // replaced by a _vN). Runs after all the binds, so it doesn't delete anything CfgMigration.Of was still going to migrate.
            CfgMigration.Scan(C,
                new[] { "Geral", "Pistao", "Roda", "Fisica", "Torreta", "Motor Comb", "Motor Pequeno", "Motor Medio", "Motor Grande", "Motor Comb Pequeno", "Motor Comb Medio", "Motor Comb Grande" },
                new[] { "Piston|power_per_meter", "Bearing|velocidade_max", "Bearing|impulso_max", "Bearing|rampa", "Bearing|servo_impulso", "Bearing|apoio_extra", "Bearing|servo_velocidade", "Bearing|servo_inverter", "Bearing|servo_graus", "Motor|rampa", "Motor|freio_fator", "Recoupler|folga", "Recoupler|juntar", "Recoupler|tempo_puxar", "Recoupler|angulo_max", "Beacon|consumo", "Beacon|esconder_perto", "Beacon|slot_icone", "Beacon|icone", "Debug|tecla_marcar_erro", "Debug|ajuste_turret_numpad", "Piston|damping", "Piston|spring_frequency", "Piston|spring_damping", "Piston|hard_stop", "Physics|substeps", "Turret Seat|pitch_min", "Turret Seat|pitch_max", "Piston|merge_snap_max", "Piston|rebuild_interval", "Piston|merge_when_still", "Piston|move_without_splitting", "Piston|collider_update_interval" });

            // 0.19.1: the servo accepts up to 360 degrees (the old 180 -> 45 correction was removed)
            KeybindsLink.Name(PluginInfo.PluginGuid, Marker.ModName); KeybindsLink.Hide(PluginInfo.PluginGuid, "Turret Seat", "cursor_key");
            Config.SettingChanged += (s, e) => { try { Apply(); Marker.ForgetKeys(); } catch (Exception) { } };   // 0.12.13: changed in the menu (Mod settings) -> takes effect right away
            KeybindsLink.Register(Marker.ModName, "circuit_dump", "(debug) Write the ship's circuit to logs/circuits", CircuitDumpKey.Value);
            KeybindsLink.Register(Marker.ModName, "mark_error", "(debug) Mark an error (log line + screenshot + note window)", MarkKey.Value);
            KeybindsLink.Register(Marker.ModName, "turret_cursor", "Turret seat: show / hide the cursor", TurretCursorKey.Value);
            Apply();
            base.Log.LogInfo(PluginInfo.PluginName + " " + PluginInfo.PluginVersion + " loaded.");

            try
            {
                var regs = global::ComponentsFramework.ComponentsFrameworkApi.RegisterComponents(PluginInfo.PluginName, Wheels.Configure);
                Mirrors.Connect(regs);   // 0.12.14: V in the garage ("M" versions)
                RubberSeal.Connect(regs);   // 0.31.28: the seal prefabs (their lips get a no-push copy once the game has built them)
                Hotbar.Connect(regs);   // 0.31.35: the Phone Dock's inventory icon = the phone's icon in the in-flight hotbar
                global::ComponentsFramework.ComponentsFrameworkApi.RegisterLocalization();
            }
            catch (Exception ex) { base.Log.LogError("registering the blocks with the Components Framework failed: " + ex); }

            try
            {
                Texts.Folder = System.IO.Path.GetDirectoryName(typeof(CrapMechanicsPlugin).Assembly.Location);
                Marker.LogsFolder = System.IO.Path.Combine(Texts.Folder, "logs");
                var h = new HarmonyLib.Harmony(PluginInfo.PluginGuid); Texts.H = h; Cycle.H = h; h.PatchAll(typeof(Texts)); try { OldNames.BuildMap(); OldNames.ConvertBlueprintFiles(); } catch (Exception ex) { base.Log.LogWarning("old prefab names: blueprints not converted (" + ex.Message + ") — convert old blueprints with bp_convert.py"); }   // 0.28.1: files on disk, no Harmony patch
                h.PatchAll(typeof(InventoryTab)); h.PatchAll(typeof(Mirrors)); h.PatchAll(typeof(Wheels)); h.PatchAll(typeof(Rewind)); h.PatchAll(typeof(PatchSystems)); h.PatchAll(typeof(RadarIcon)); try { h.PatchAll(typeof(RubberSeal)); } catch (Exception ex) { base.Log.LogWarning("rubber seal: launch hook failed (" + ex.Message + ") — only the launch log line is lost"); } Unloading.Patch(h); Transition.Patch(h);   // 0.28.2: in 0.28.1 these were inside the comment above
            }
            catch (Exception ex) { base.Log.LogError("patching the port texts failed: " + ex); }

            AddComponent<Cycle>();
        }

        /// <summary>0.31.30: [Debug] debug_tools, the master switch (null before the binds = off).</summary>
        static bool Dbg => DebugToolsCfg != null && DebugToolsCfg.Value;
        static string lastDebugReport;
        /// <summary>0.31.30: one line when the debug state changes: which switches are on and whether the master lets them work.</summary>
        static void DebugReport()
        {
            try
            {
                var on = new List<string>();
                void A(ConfigEntry<bool> e) { if (e != null && e.Value) on.Add(e.Definition.Key); }
                A(MomentumWatchCfg); A(SealWatchCfg); A(SoundWatchCfg); A(DragWatchCfg); A(NoDragCfg); A(PowerWatchCfg); A(StrainWatchCfg); A(FlightTraceCfg); A(TraceCfg); A(PerfCompareCfg); A(PerfSplitWaitsCfg); A(SystemTimingCfg); A(SoundCompareCfg);
                string line = on.Count == 0 ? "debug: no debug line or test is on" + (Dbg ? " ([Debug] debug_tools = true, nothing to do)" : "")
                            : Dbg ? "debug: [Debug] debug_tools = true — ON: " + string.Join(", ", on) + (NoDragCfg != null && NoDragCfg.Value ? " (no_drag: the air does not slow anything!)" : "")
                                  : "debug: [Debug] debug_tools = false — all off (set to true but ignored: " + string.Join(", ", on) + ")";
                if (line != lastDebugReport) { lastDebugReport = line; ModLog.Info(line); }
            }
            catch (Exception) { }
        }

        public static void Apply()
        {
            JointSystem.PistonSpeed = PistonSpeed.Value; JointSystem.PistonImpulse = PistonImpulse.Value;
            JointSystem.PistonConsumptionPerM = PistonConsumption.Value; JointSystem.MotorRamp = MotorRamp.Value; JointSystem.ForceReserve = ForceReserve.Value; JointSystem.ServoImpulse = ServoImpulse.Value; JointSystem.ServoDegrees = ServoDegrees.Value; JointSystem.ServoSpeed = ServoSpeed.Value; JointSystem.ServoTipSpeed = ServoTipSpeed.Value; JointSystem.ServoGain = ServoGain.Value; JointSystem.SnapBack = SnapBack.Value; JointSystem.PistonCollides = PistonCollides.Value; JointSystem.WholeUntilMoved = WholeUntilMoved.Value; JointSystem.TurretWholeUntilAimed = TurretWhole.Value; JointSystem.MassRatio = MassRatio.Value; Marker.NoteWindow = MarkWindow != null && MarkWindow.Value; JointSystem.MassFactorMax = MassFactorMax.Value; JointSystem.TurretMassFactor = TurretMassFactor.Value; JointSystem.PistonTiltMax = PistonTilt.Value; ModLog.Tracing = Dbg && TraceCfg != null && TraceCfg.Value;   // 0.24.0: Apply runs on every change in the menu — without this, turning on the trace in Mod Menu did nothing
            // 0.24.1: this remainder was INSIDE the comment above since 0.24.0 (solver, reinforcement, rails, servo... went back to the code's defaults)
            JointSystem.ServoInvert = ServoInvert.Value; JointSystem.BearingSupport = BearingSupport.Value; Wheels.SportsFriction = WheelFriction.Value; Wheels.OffroadFriction = WheelFrictionOffroad.Value; JointSystem.SolverIterations = SolverIterations.Value; JointSystem.IterationsAlways = IterationsAlways.Value; JointSystem.Rigid = Stiffness.Value; JointSystem.ZeroGravLaunch = ZeroGravLaunch.Value; JointSystem.FitWait = FitWait.Value; JointSystem.BaseReinforce = BaseReinforce.Value; RadarIcon.Request = IconBeacon.Value; RadarIcon.Slot = IconSlot.Value; JointSystem.StartTime = StartTime.Value; JointSystem.StartPause = StartPause.Value; JointSystem.NoCollisionAtStart = NoCollision.Value; JointSystem.PistonRails = PistonRails.Value; JointSystem.PistonRailsPerMeter = PistonRailsPerMeter.Value; Couplers.MaxAngle = RecouplerAngle.Value; Couplers.Tolerance = RecouplerSlack.Value; Couplers.Join = RecouplerMerge.Value; Couplers.PullTime = RecouplerPull.Value; Couplers.HideNear = BeaconHide.Value; Couplers.HideOwn = BeaconOwn == null || BeaconOwn.Value; Couplers.BeaconPowerUse = BeaconConsumption.Value;
            if (BonusExtras != null) Bonus.ExtrasCfg = BonusExtras.Value;
            if (ThrusterForce != null) Thrusters.Multiplier = ThrusterForce.Value;
            if (ThrusterJoints != null) JointBoost.Enabled = ThrusterJoints.Value;
            if (RingGrowList != null) SlipRings.GrowList = RingGrowList.Value;
            if (RingEmbeddedTurns != null) RingVisual.Turns = RingEmbeddedTurns.Value;
            if (RingRebuildInTick != null) SlipRings.RebuildInTick = RingRebuildInTick.Value;
            if (PowerWatchCfg != null) SlipRings.WatchPower = Dbg && PowerWatchCfg.Value;
            if (FlightTraceCfg != null) FlightTrace.Enabled = Dbg && FlightTraceCfg.Value;
            if (StrainWatchCfg != null) StrainWatch.Enabled = Dbg && StrainWatchCfg.Value;
            if (DragWatchCfg != null) DragWatch.Enabled = Dbg && DragWatchCfg.Value;
            if (NoDragCfg != null) DragWatch.NoDrag = Dbg && NoDragCfg.Value;
            if (MomentumWatchCfg != null) MomentumWatch.Enabled = Dbg && MomentumWatchCfg.Value;
            if (SealWatchCfg != null) SealWatch.Enabled = Dbg && SealWatchCfg.Value;   // 0.31.28 seal 2
            if (SealSolidCfg != null) RubberSeal.SolidFraction = SealSolidCfg.Value / 100f;   // 0.31.30
            if (SoundSealCfg != null) SoundSeal.Enabled = SoundSealCfg.Value;   // 0.31.30 sound
            if (SoundWatchCfg != null) SoundSeal.Watch = Dbg && SoundWatchCfg.Value;
            // 0.31.35 performance
            Perf.On = Dbg;
            if (PerfCacheCfg != null) { Ecs.CacheWanted = PerfCacheCfg.Value; Ecs.CacheCompare = Dbg && PerfCompareCfg.Value; Perf.SplitWaits = Dbg && PerfSplitWaitsCfg.Value; Perf.SystemTiming = Dbg && SystemTimingCfg.Value; SoundSeal.Compare = Dbg && SoundCompareCfg.Value; }
            if (SoundSettleCfg != null) SoundSeal.Settle = SoundSettleCfg.Value;
            DebugReport();
            if (FrameSeamCfg != null) FrameFaces.SeamLikeGame = FrameSeamCfg.Value;
            if (WeldFirstCfg != null) JointSystem.WeldFirst = WeldFirstCfg.Value;
            if (DragAsOneCfg != null) JointSystem.DragAsOne = DragAsOneCfg.Value;
            if (ContactScalingCfg != null) JointSystem.ContactInertiaScaling = ContactScalingCfg.Value;   // 0.31.26 (U1)
            if (ThrustBalanceCfg != null) ThrustBalance.Enabled = ThrustBalanceCfg.Value;
            if (RemotePower != null) { RemoteComputer.PowerUse = RemotePower.Value; RemoteComputer.RangeM = RemoteRange.Value; RemoteComputer.WindowTicks = Math.Max(1, RemoteWindow.Value); }   // remote control (delivery 1)
            if (RemoteLongPress != null) { Phone.ShortPress = RemoteLongPress.Value; Hotbar.Hand3D = RemoteHand3D.Value; }   // remote control (delivery 2)
            if (RemoteHandLow != null) { Hotbar.HandLow = RemoteHandLow.Value; Hotbar.GameLook = RemoteGameBar.Value; }   // remote control (round 0.31.35)
            if (MpOwnerOnly != null) { Multi.OwnerOnly = MpOwnerOnly.Value; Multi.FakeClient = MpFakeClient.Value; Multi.BreakOnClient = MpBreakClient.Value; Multi.RingOnClient = MpRingClient.Value; ModNet.Log = MpNetLog.Value; ModNet.Loopback = MpLoopback.Value; }   // 0.24.0-mp; 0.28.0
            JointSystem.FuelConsumption = FuelConsumption.Value; JointSystem.FuelPower = FuelPower.Value; JointSystem.BrakeFactor = BrakeFactor.Value;
            JointSystem.TurretSens = TurretSens.Value; JointSystem.TurretGain = TurretGain.Value; JointSystem.TurretMaxSpeed = TurretMaxSpeed.Value; JointSystem.TurretLinkAccel = System.Math.Max(10f, TurretLinkAccel.Value);
            JointSystem.TurretInertia = TurretInertia.Value; JointSystem.ViewRotates = TurretViewRotates.Value; JointSystem.TurretCamera = TurretCamera.Value; JointSystem.BindsReach = Math.Max(JointSystem.GameReach, TurretReach.Value); JointSystem.SeatBindsReach = Math.Max(JointSystem.GameReach, SeatReach.Value);   /* 0.31.26 (item 25): the game's reach is 4.5 m (linear); a smaller value would shorten it */ Signal.Relink = SignalRelink.Value;
            JointSystem.LoadAdjust(TurretSeatMode.Value, TurretSeatAdjust.Value, TurretPlayerB.Value, TurretViewA.Value);
            try { if (Enum.TryParse<UnityEngine.InputSystem.Key>(TurretCursorKey.Value.Trim(), true, out var k)) JointSystem.TurretCursorKey = k; else ModLog.Warn("Turret Seat.cursor_key '" + TurretCursorKey.Value + "' does not exist; using LeftCtrl"); } catch (Exception) { }
            for (int i = 0; i < 3; i++) { JointSystem.FuelMotorSpeed[i] = FuelMotorSpeed[i].Value; JointSystem.FuelMotorImpulse[i] = FuelMotorImpulse[i].Value; }
            for (int i = 0; i < 3; i++) { JointSystem.MotorSpeed[i] = MotorSpeed[i].Value; JointSystem.MotorImpulse[i] = MotorImpulse[i].Value; JointSystem.MotorConsumption[i] = MotorConsumption[i].Value; }
            JointSystem.DetailedLog = DetailedLog.Value;
        }
    }

    /// <summary>0.12.0 (turret seat): prefix on SystemBase.Update — when the instance is the TransformSystemGroup (after the game's physics and
    /// camera, before the LocalToWorlds) writes the camera rotation = aim; in PresentationSystemGroup reinforces it in LocalToWorld.</summary>
    // 01/10 (crash when going to the main menu / loading a BP: coreclr.dll ProcessCLRException, c0000005): the patch on SystemBase.Update
    // wrapped EVERY game system in a managed trampoline — when a system throws an exception (normal on a scene change; the
    // ComponentSystemGroup.UpdateAllSystems catches and logs it), the native exception went through the .NET trampoline and crashed the game. Now
    // the patch is on the GROUPS' OnUpdate: a child system's exception is caught inside the group, below our frame.
    [HarmonyLib.HarmonyPatch(typeof(ComponentSystemGroup), "OnUpdate")]
    internal static class PatchSystems
    {
        static int errors;
        [HarmonyLib.HarmonyPrefix]
        static void Before(ComponentSystemGroup __instance)
        {
            // 0.31.35 (Perf.cs, only with [Debug] debug_tools): this group's own time starts when the mod's prefix ends
            long pf = Perf.On && __instance != null ? Perf.Now() : 0;
            try
            {
                if (__instance == null || !JointSystem.EmOk || Unloading.Active) return;
                var p = __instance.Pointer;
                if (p == Ecs.PtrTransform && Ecs.PtrTransform != IntPtr.Zero) JointSystem.TurretBeforeTransform();
                else if (p == Ecs.PtrPhysicsPre && Ecs.PtrPhysicsPre != IntPtr.Zero)
                {
                    // 0.31.27: every hook inside the tick is timed (TickProf: average and worst tick of each, in the performance line)
                    TickProf.TickStart(); long t;
                    Perf.Wait(JointSystem.CurrentEm, Perf.WPre);   // 0.31.35: [Debug] perf_split_waits
                    Ecs.Begin(JointSystem.CurrentEm);   // 0.31.35: the read cache answers from here to the end of this prefix (Ecs.cs)
                    JointSystem.TickSeen();   // 0.31.26: a new tick — what CheckJoints did in the last one makes the live-joint set stale
                    bool playback = ReplayLink.InPlayback();   // 0.31.27: read once per tick (it was read four times here)
                    t = TickProf.T0(); Far.Step(JointSystem.CurrentEm, playback); TickProf.Add(TickProf.Far, t);   // 0.19.13: 100 km rule (instead of the game's SPActiveUpdateSystem)
                    if (!playback) { t = TickProf.T0(); JointSystem.SplitsInTick(JointSystem.CurrentEm); TickProf.Add(TickProf.Splits, t); }   // 0.26.15: joint created in the same tick in which the game splits the parts
                    if (!playback) { t = TickProf.T0(); JointSystem.CommandsInTick(JointSystem.CurrentEm); TickProf.Add(TickProf.Commands, t); }   // 0.31.26 (U3): rigid piston/servo command, one step per tick
                    if (!playback)
                    {
                        // 0.31.23: the game's forces act on the REAL masses; the solver's masses come back at the end of this group
                        t = TickProf.T0(); MomentumWatch.Mark(JointSystem.CurrentEm, 0); TickProf.Add(TickProf.Momentum, t);
                        t = TickProf.T0(); JointSystem.WeldBeforeForces(JointSystem.CurrentEm); TickProf.Add(TickProf.WeldForces, t);
                        t = TickProf.T0(); MomentumWatch.Mark(JointSystem.CurrentEm, 1); TickProf.Add(TickProf.Momentum, t);
                        t = TickProf.T0(); DragWatch.BeforePhysics(JointSystem.CurrentEm); TickProf.Add(TickProf.Drag, t);   // 0.31.17: before the game applies thrust and its drag
                    }
                }
                // 0.31.26 (U7): start of PhysicsSystemGroup — after every OrderFirst system of the FixedStep group (the game's gravity write
                // included) and before the physics world is built: the launch gravity window is written here
                else if (p == Ecs.PtrPhysicsSystem && Ecs.PtrPhysicsSystem != IntPtr.Zero) { if (!ReplayLink.InPlayback()) { Perf.Wait(JointSystem.CurrentEm, Perf.WPhysics); long t = TickProf.T0(); JointSystem.GravityInTick(JointSystem.CurrentEm); TickProf.Add(TickProf.Gravity, t); } }
                else if (p == Ecs.PtrPresentation && Ecs.PtrPresentation != IntPtr.Zero) { if (JointSystem.SeatedTurret != null) JointSystem.TurretCameraLTW(true); Hotbar.PlaceHand(); }   // remote control (delivery 2): the phone in the hand follows the camera
                else if ((p == Ecs.PtrTickPre || p == Ecs.PtrTickPost) && p != IntPtr.Zero)
                {
                    // (0.19.6: the LRT copied its signal here too — 0.31.26, item 46: only in the TickPost postfix now, see below)
                    // 0.31.15: the slip ring bridges (data links and power) once per game tick, right before the game updates its circuits
                    // 0.31.21: and the momentum watch reads again here — the solver has just run
                    // 0.31.26 (item 12): right after the solver the real masses go back in (the made-up ones exist only while the solver runs);
                    // mark 4 reads before the swap (closes "the solver"), mark 5 after it (opens "the rest of the frame", real masses)
                    if (p == Ecs.PtrTickPost && !ReplayLink.InPlayback())
                    {
                        Perf.Wait(JointSystem.CurrentEm, Perf.WBeforeTickPost);   // 0.31.35: [Debug] perf_split_waits — here the wait is the physics step still running
                        Ecs.Begin(JointSystem.CurrentEm);
                        long t = TickProf.T0(); MomentumWatch.Mark(JointSystem.CurrentEm, 4); TickProf.Add(TickProf.Momentum, t);
                        t = TickProf.T0(); JointSystem.MassesRealAfterSolver(JointSystem.CurrentEm); TickProf.Add(TickProf.MassesReal, t);
                        t = TickProf.T0(); MomentumWatch.Mark(JointSystem.CurrentEm, 5); TickProf.Add(TickProf.Momentum, t);
                        t = TickProf.T0(); SlipRings.Tick(JointSystem.CurrentEm); TickProf.Add(TickProf.Rings, t);
                        // 0.31.26 (item 1): releases the mod asked for — 1e35 strain inside the tick, after the FrameInit zeroing and before CheckJoints,
                        // exactly where SCTick_Decoupler writes it [recon/EPC_SCDecoupler.cs:51; SpaceshipCheckJoints.cs:6,104-106]
                        t = TickProf.T0(); JointSystem.BreakInTick(JointSystem.CurrentEm); TickProf.Add(TickProf.Breaks, t);
                        t = TickProf.T0(); RemoteComputer.BeforeTick(JointSystem.CurrentEm); TickProf.Add(TickProf.Remote, t);   // remote control: the receivers' values before the game's wireless job (only during a send window)   // 0.31.35: timed
                    }
                }
            }
            catch (Exception ex) { if (errors++ < 3) ModLog.Error("prefix group OnUpdate: " + ex.Message); }
            finally { Ecs.End(); if (pf != 0) Perf.GroupEnter(__instance.Pointer, pf); }
        }
        [HarmonyLib.HarmonyPostfix]
        static void After(ComponentSystemGroup __instance)
        {
            long pf = Perf.On && __instance != null ? Perf.GroupExit(__instance.Pointer) : 0;   // 0.31.35 (Perf.cs): the group's own time ends where the mod's postfix starts
            try
            {
                if (__instance == null || !JointSystem.EmOk || Unloading.Active) return;
                if (__instance.Pointer == Ecs.PtrTransform && Ecs.PtrTransform != IntPtr.Zero) { JointSystem.TurretAfterTransform(); JointSystem.CaptureGameEye(); Hotbar.CaptureEye(); try { Couplers.WriteRadar(JointSystem.CurrentEm); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("radar/write: " + ex.Message); } }
                else if (__instance.Pointer == Ecs.PtrBindsGroup && Ecs.PtrBindsGroup != IntPtr.Zero) JointSystem.TurretBindsDistance();
                else if (__instance.Pointer == Ecs.PtrFrameSetup && Ecs.PtrFrameSetup != IntPtr.Zero) { if (TextInput.Active) TextInput.Capture(); }   // 0.31.35: typed text for the rename box (the game refilled its buffer in this group)
                else if (__instance.Pointer == Ecs.PtrPhysicsPre && Ecs.PtrPhysicsPre != IntPtr.Zero)
                {
                    if (!ReplayLink.InPlayback())
                    {
                        Perf.Wait(JointSystem.CurrentEm, Perf.WAfterPre);   // 0.31.35: [Debug] perf_split_waits
                        Ecs.Begin(JointSystem.CurrentEm);
                        // 0.31.17: thrust and air are in, the solver has not run. 0.31.23: the welded parts enter the solver as one body
                        long t = TickProf.T0(); DragWatch.AfterPhysics(JointSystem.CurrentEm); TickProf.Add(TickProf.Drag, t);
                        t = TickProf.T0(); MomentumWatch.Mark(JointSystem.CurrentEm, 2); TickProf.Add(TickProf.Momentum, t);
                        t = TickProf.T0(); JointSystem.WeldBeforeSolver(JointSystem.CurrentEm); TickProf.Add(TickProf.WeldSolver, t);
                        t = TickProf.T0(); MomentumWatch.Mark(JointSystem.CurrentEm, 3); TickProf.Add(TickProf.Momentum, t);
                        if (Ecs.PtrPhysicsSystem == IntPtr.Zero) { t = TickProf.T0(); JointSystem.GravityInTick(JointSystem.CurrentEm); TickProf.Add(TickProf.Gravity, t); }   // 0.31.26 (U7): fallback place
                    }
                }
                else if ((__instance.Pointer == Ecs.PtrTickPre || __instance.Pointer == Ecs.PtrTickPost) && __instance.Pointer != IntPtr.Zero)
                {
                    // 0.19.6: the LRT copies its signal inside the tick. 0.31.26 (item 46): once per tick, after the TickPost group — the camera
                    // writes its video (OrderFirst), GetInputs copies the ports (OrderFirst) and the monitor reads (OrderLast) all in that group,
                    // and nothing in the TickPre group touches these ports [recon/EPC_SCCamera.cs:31; recon/EPC_SCMonitor.cs:26;
                    // recon/Spaceship__part_a.cs:694]: the other three copies per tick wrote the same value (the latency does not change)
                    Perf.Wait(JointSystem.CurrentEm, __instance.Pointer == Ecs.PtrTickPost ? Perf.WAfterTickPost : Perf.WAfterTickPre);   // 0.31.35: [Debug] perf_split_waits
                    Ecs.Begin(JointSystem.CurrentEm);
                    if (__instance.Pointer == Ecs.PtrTickPost) { long t = TickProf.T0(); Radio.Copy(JointSystem.CurrentEm, true); TickProf.Add(TickProf.Radio, t); }
                    // remote control (delivery 1): the Remote Computer's row outputs and its wireless send, after SCTick_WirelessTransmitter (this group)
                    if (__instance.Pointer == Ecs.PtrTickPost && !ReplayLink.InPlayback()) { long t = TickProf.T0(); RemoteComputer.AfterTick(JointSystem.CurrentEm); TickProf.Add(TickProf.Remote, t); }   // 0.31.35: timed
                    // 0.31.26 (item 3): the thrusters have filled their buffers (SCTick_Thruster_Forces, inside this group) and the game's
                    // ApplyForces has not run yet [recon/SpaceshipPartsApplyForces.cs:6-7]: a welded group takes its thrust as one part
                    if (__instance.Pointer == Ecs.PtrTickPre && !ReplayLink.InPlayback()) { long t = TickProf.T0(); JointSystem.ThrustAsOne(JointSystem.CurrentEm); TickProf.Add(TickProf.Thrust, t); }
                    if (__instance.Pointer == Ecs.PtrTickPost) TickProf.TickEnd();   // 0.31.27: end of the tick (TickProf)
                }
            }
            catch (Exception ex) { if (errors++ < 3) ModLog.Error("postfix group OnUpdate: " + ex.Message); }
            finally { Ecs.End(); if (pf != 0) Perf.HookEnd(pf); }
        }
    }

    /// <summary>0.11.3: link to the Replay mod (if installed): while it plays a recording back, Crap Mechanics doesn't run its cycle
    /// (reads the Replay mod's playback flag by reflection, without depending on its DLL).</summary>
    internal static class ReplayLink
    {
        static System.Reflection.FieldInfo field; static float tTry = 999f; static int attempts;
        public static bool InPlayback()
        {
            if (field == null)
            {
                tTry += UnityEngine.Time.unscaledDeltaTime; if (tTry < 5f || attempts >= 12) return false;
                tTry = 0f; attempts++;
                try
                {
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (a.GetName().Name != "Replay") continue;
                        var t = a.GetType("Replay.State"); field = t?.GetField("InPlayback");                       // Replay 0.9.0+ (English names)
                        if (field == null) { t = a.GetType("Replay.Estado"); field = t?.GetField("EmReproducao"); }   // older Replay
                        if (field != null) ModLog.Info("Replay mod found: the mod's cycle pauses during playback");
                    }
                }
                catch (Exception) { }
                if (field == null) return false;
            }
            bool on; try { on = (bool)field.GetValue(null); } catch (Exception) { return false; }
            // 0.28.0: on a network Replay doesn't freeze the world during playback (it can't be paused for everyone), so the cycle goes on —
            // on the server, pausing it would leave every piston and bearing of every player limp while the world keeps running
            if (on && JointSystem.EmOk && Multi.Networked(JointSystem.CurrentEm))
            {
                if (!netNoted) { netNoted = true; ModLog.Info("Replay playback on a network: the world keeps running, so the mod's cycle keeps running too"); }
                return false;
            }
            return on;
        }
        static bool netNoted;
    }

    /// <summary>MonoBehaviour injected into the game: calls the joint system every frame.</summary>
    public class Cycle : UnityEngine.MonoBehaviour
    {
        public Cycle(IntPtr ptr) : base(ptr) { }
        static int errors = 0; static float tWorld = 0f; static World world;
        internal static HarmonyLib.Harmony H;   // 0.31.35: [Debug] system_timing puts its patch on when it is first turned on

        void Update()
        {
            try
            {
                TickProf.Frame();   // 0.31.27: ticks per drawn frame
                Perf.Frame();       // 0.31.35
                Texts.Undo();
                try { Transition.Step(); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("transition log: " + ex.Message); }   // 0.19.12: snapshots at +1/+3/+6 s (before any early return)
                try { Phone.Guard(); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("phone guard: " + ex.Message); }   // remote control: closes the phone / gives the input back even when the cycle returns early
                if (CrapMechanicsPlugin.IsActive == null || !CrapMechanicsPlugin.IsActive.Value) { if (world != null && world.IsCreated && !Unloading.Active) SoundSeal.Pause(world.EntityManager); return; }   // 0.31.30 sound: the mod's sound cells go too
                if (Unloading.Window) return;   // 01/10: the game is unloading the world — no reading the ECS (0.31.26, item 14: only the 3 s after ClearGame)
                if (Unloading.Pending) { Unloading.Confirm(); try { JointSystem.ForgetAll(); Ecs.ResetPointers(); FrameFaces.Forget(); SealWatch.Forget(); SoundSeal.Forget(); } catch (Exception ex) { ModLog.Warn("after unload: " + ex.Message); } }
                if (world != null && world.IsCreated) { try { Marker.Step(world.EntityManager); } catch (Exception ex) { if (errors++ < 5) ModLog.Error("mark: " + ex); } try { CircuitDump.Step(world.EntityManager); } catch (Exception ex) { if (errors++ < 5) ModLog.Error("circuit dump: " + ex); } }
                InventoryTab.Step();
                float dt = UnityEngine.Time.deltaTime;
                try { Props.Step(dt); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("properties: " + ex.Message); }   // 0.23.2
                try { BlockIndex.Step(dt); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("blocks: " + ex.Message); }   // 0.29.0
                if (world == null || !world.IsCreated)
                {
                    tWorld += dt; if (tWorld < 1f) return; tWorld = 0f;
                    var w = World.DefaultGameObjectInjectionWorld;
                    if (w == null || !w.IsCreated) return;
                    world = w; ModLog.Info("World: " + w.Name);
                }
                try { RubberSeal.PrefabStep(world.EntityManager); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("rubber seal prefabs: " + ex.Message); }   // 0.31.28: before the replay/menu returns (blueprints are built from these prefabs)
                if (ReplayLink.InPlayback())
                {
                    SoundSeal.Pause(world.EntityManager);   // 0.31.30 sound: the mod's sound cells go while the cycle is paused
                    JointSystem.SeatVisibleInPlayback(world.EntityManager);   // 0.11.3: the Replay mod is playing back (simulation frozen) — don't touch anything...
                    try { Far.Step(world.EntityManager, true); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("replay: far parts: " + ex.Message); }   // 0.19.13: turn back on what Recenter made the game turn off
                    try { Radio.Copy(world.EntityManager, false); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("replay: long range copy: " + ex.Message); }   // 0.19.11 (Andre: "it didn't record the LRT camera in the replay"): ...only the LRT keeps passing the signal on (the monitor shows the live camera)
                    return;
                }
                // 0.12.6 (Andre: "the BP menu got extremely laggy"): the BP previews become entities in the world (UIBlueprintSave.
                // CreateEntitiesFromBinary) and the mod found/processed all their blocks. With the menu open (outside the simulation) the cycle pauses.
                if (MenuBP()) { SoundSeal.Pause(world.EntityManager); return; }   // 0.31.30 sound
                Ecs.PreparePointers();
                try { if (H != null) Perf.SystemTimingStep(H); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("system timing: " + ex.Message); }   // 0.31.35
                Perf.Wait(world.EntityManager, Perf.WCycle);   // 0.31.35: [Debug] perf_split_waits
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Ecs.Begin(world.EntityManager);   // 0.31.35: the read cache answers during the mod's cycle (emptied by every structural change the mod makes)
                try { BlackBox.Step(world.EntityManager, dt); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("black box: " + ex.Message); }   // 0.31.1
                try { RubberSeal.Step(world.EntityManager); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("rubber seal: " + ex.Message); }   // 0.31.28
                try { FrameFaces.Step(world.EntityManager); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("frame faces: " + ex.Message); }   // 0.31.28 seal 2
                try { SealWatch.Step(world.EntityManager); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("seal watch: " + ex.Message); }   // 0.31.28 seal 2
                Ecs.End();   // 0.31.35: the sound cells are made and removed by the thousand (each one a structural change): this step reads the game directly, as before
                try { SoundSeal.Step(world.EntityManager); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("sound seal: " + ex.Message); }   // 0.31.30 sound: the game's sound sees MicroFrames, seals and gates on joints
                Ecs.Begin(world.EntityManager);
                try { Thrusters.Step(world.EntityManager, dt); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("thrusters: " + ex.Message); }   // 0.30.0
                Ecs.Sync(world.EntityManager);
                JointSystem.Step(world.EntityManager, dt);
                Ecs.Sync(world.EntityManager);
                try { RemoteComputer.Step(world.EntityManager, dt); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("remote computer: " + ex.Message); }   // remote control (delivery 1)
                try { Phone.Frame(world.EntityManager, dt); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("phone: " + ex.Message); }
                try { Hotbar.Frame(world.EntityManager, dt); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("hotbar: " + ex.Message); }   // remote control (delivery 2)
                try { StrainWatch.Frame(world.EntityManager, dt); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("strain watch: " + ex.Message); }   // 0.31.14 (after JointSystem: links it breaks on purpose are already marked)
                Ecs.End();
                Measure(sw.Elapsed.TotalMilliseconds, dt);
                RadarIcon.Step(dt); JointSystem.T("radar icons — cycle end"); if (JointSystem.Trail > 0) JointSystem.Trail--;
            }
            catch (Exception ex) { if (errors++ < 5) ModLog.Error("Update: " + ex); }
            finally { Ecs.End(); }
        }
        // 0.12.6: cycle time — every 10 s, if the average goes over 2 ms (or the peak over 20 ms), it goes to the log with the numbers
        static double sumMs, peakMs; static int nMed; static float tMed;
        static void Measure(double ms, float dt)
        {
            sumMs += ms; nMed++; if (ms > peakMs) peakMs = ms; tMed += UnityEngine.Time.unscaledDeltaTime;
            if (tMed < 10f) return;
            double average = sumMs / Math.Max(1, nMed);
            bool heavyTicks = TickProf.Heavy(nMed);
            string stages = JointSystem.SlowestSteps(nMed) + TickProf.Summary(nMed);   // 0.26.10: resets the step sums every 10 s, logging or not (0.31.27: + the hooks inside the game's ticks)
            if (average > 2.0 || peakMs > 20.0 || heavyTicks || Perf.On) ModLog.Warn("performance: mod cycle average " + average.ToString("F2") + " ms, peak " + peakMs.ToString("F1") + " ms (" + nMed + " frames) — " + JointSystem.Counts() + " | slowest steps, ms/frame: " + stages);
            if (world != null && world.IsCreated) Perf.Report(world.EntityManager);   // 0.31.35: the "performance+:" lines (only with [Debug] debug_tools)
            sumMs = 0; peakMs = 0; nMed = 0; tMed = 0f;
        }
        static bool menuOpen, bpWasOpen; static float tMenu = 99f;
        static bool MenuBP()
        {
            tMenu += UnityEngine.Time.unscaledDeltaTime;
            if (tMenu >= 0.2f)
            {
                tMenu = 0f; bool bpOpen = false, manualOpen = false;
                // 0.24.0 (Andre: "the tutorial menu opens as if it were the bp menu, and loads parts inside that menu"): the manual
                // also builds and tears down parts, and the mod cycle running on top of that crashed the game (crash in the tutorial of the
                // small maneuvering thrusters). Now it pauses the cycle like the blueprint menu.
                try
                {
                    var ui = UIManager._singleton;
                    if (ui != null)
                    {
                        var bp = ui._blueprintMenu; bpOpen = bp != null && bp.gameObject.activeInHierarchy;
                        var mm = ui._manualMenu; manualOpen = mm != null && mm.gameObject.activeInHierarchy;
                    }
                }
                catch (Exception) { }
                bool open = bpOpen || manualOpen;
                if (open != menuOpen)
                {
                    menuOpen = open; JointSystem.MenuBP = open;
                    ModLog.Info(open ? (bpOpen ? "blueprint" : "manual") + " menu OPEN: mod cycle paused" : "menu closed: mod cycle resumed");
                    if (!open && bpWasOpen) JointSystem.Trail = 8;   // the "after BP load" trail only makes sense after the blueprint menu
                    bpWasOpen = bpOpen;
                }
            }
            return menuOpen;
        }
        void OnGUI() { try { JointSystem.DrawAdjust(); } catch (Exception ex) { if (errors++ < 5) ModLog.Error("OnGUI: " + ex); } try { ThrustBalance.Draw(); } catch (Exception ex) { if (errors++ < 5) ModLog.Error("OnGUI (thrust balance): " + ex); } try { Hotbar.Draw(); } catch (Exception ex) { if (errors++ < 5) ModLog.Error("OnGUI (hotbar): " + ex); } try { Phone.Draw(); } catch (Exception ex) { if (errors++ < 5) ModLog.Error("OnGUI (phone): " + ex); } }
        void LateUpdate() { try { if (JointSystem.SeatedTurret != null) JointSystem.TurretCameraLTW(false); } catch (Exception ex) { if (errors++ < 5) ModLog.Error("LateUpdate: " + ex); } }
        void OnApplicationQuit() { try { if (world != null && world.IsCreated) JointSystem.Clean(world.EntityManager); } catch (Exception) { } }
    }
}
