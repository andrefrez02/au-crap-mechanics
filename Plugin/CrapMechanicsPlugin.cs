using System;
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
        public static ConfigEntry<bool> MpOwnerOnly, MpFakeClient, MpBreakClient, MpRingClient, MpNetLog, MpLoopback;   // 0.24.0-mp; 0.28.0: + net_log, test_loopback
        public static ConfigEntry<float>[] MotorSpeed = new ConfigEntry<float>[3], MotorImpulse = new ConfigEntry<float>[3], MotorConsumption = new ConfigEntry<float>[3];
        public static ConfigEntry<bool> DetailedLog, IsActive, ServoInvert; public static ConfigEntry<float> ServoSpeed, ServoTipSpeed;   // 0.26.14
        public static ConfigEntry<bool> Stiffness; public static ConfigEntry<bool> ZeroGravLaunch; public static ConfigEntry<bool> IterationsAlways;   // 0.26.12
        public static ConfigEntry<int> SolverIterations, IconBeacon, IconSlot; public static ConfigEntry<float> StartTime, StartPause, NoCollision, PistonRails, PistonRailsPerMeter, RecouplerAngle; public static ConfigEntry<float> RecouplerSlack, BeaconHide, RecouplerPull, BeaconConsumption; public static ConfigEntry<bool> RecouplerMerge, BeaconOwn, BeaconFar;
        public static ConfigEntry<float> BaseReinforce, FuelConsumption, FuelPower, BrakeFactor, ForceReserve;
        public static ConfigEntry<float> SeatReach, FitWait, ServoGain, PistonTilt; public static ConfigEntry<bool> SnapBack; public static ConfigEntry<bool> PistonCollides, WholeUntilMoved, TurretWhole; public static ConfigEntry<float> MassRatio; public static ConfigEntry<bool> SignalRelink; public static ConfigEntry<float> TurretReach, TurretSens, TurretGain, TurretMaxSpeed, TurretLinkAccel; public static ConfigEntry<bool> TurretInertia, TurretCamera, TurretViewRotates; public static ConfigEntry<string> TurretSeatMode, TurretSeatAdjust, TurretPlayerB, TurretViewA; public static ConfigEntry<string> TurretCursorKey;
        public static ConfigEntry<float>[] FuelMotorSpeed = new ConfigEntry<float>[3], FuelMotorImpulse = new ConfigEntry<float>[3];

        public static ConfigEntry<string> MarkKey; public static ConfigEntry<bool> TurretAdjust, TraceCfg;
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
            if (!Layout.Init(out var layoutError))   // the game's struct fields this mod reads raw, looked up by name
            {
                ModLog.Error("this version of the game changed a struct this mod reads (" + layoutError + "). This copy stays OFF — update Crap Mechanics.");
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
            MassRatio = C.Bind("Piston", "mass_ratio_max", 10f, "With rigid_joints: while two parts are held by a piston weld, the lighter one counts as at least 1/N of the heavier one FOR THE PHYSICS (the blueprint does not change, nothing falls faster). This is what keeps the weld from sagging: the physics solver can't hold a joint between 71 t and 65 kg, but holds 10:1 fine. 0 = off");
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
            WheelFriction = CfgMigration.Of(C, C.Bind("Wheel", "friction", 1.5f, "Friction of the Sports wheels (a normal game block is ~0.5); on contact the higher of the two is used"), "Roda", "atrito");
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
            SolverIterations = CfgMigration.Of(C, C.Bind("Physics", "solver_iterations", 20, "Physics solver iterations while a piston/bearing is active (the game uses 4; more = stiffer joints and less wobbly part chains, costs CPU; 0 = unchanged)"), "Fisica", "iteracoes_solver");
            IterationsAlways = C.Bind("Physics", "solver_iterations_rigid", false, "Also raise the solver iterations (solver_iterations) while only welds, locked or free bearings are active. Stiffer joint chains (a car axle hanging on a steering bearing and mini pistons), but the whole world's physics costs more CPU. Once raised it stays until the game restarts");   // 0.26.12
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
            BonusExtras = C.Bind("Bonus", "extra_blocks", "", "Extra amount of any block in the inventory, as SC_Name=amount separated by commas (works for other mods' blocks too; a block that is not unlocked yet is skipped). Empty = none");

            // ---- [Multiplayer] ----
            // 0.24.0-mp (multiplayer): the game isn't lockstep — the logic runs on every machine and the server rules the pose
            // of the parts (see Multi.cs). In a solo game (NetworkStatus.Off) none of this changes the behaviour.
            MpOwnerOnly = C.Bind("Multiplayer", "host_only", true, "The server runs the mod's physics (joints, motors, pistons, brake, turret, recoupler, knob restore); a client only draws the mod's parts from the poses the server sends, and sends the turret seat aim of whoever sits there. Off = every machine simulates its own copy (not recommended)");
            MpBreakClient = C.Bind("Multiplayer", "break_on_client", false, "(debug) Let a client break the game's joints at launch too. Off (default) = the server splits the parts and the game sends the split to everyone");
            MpRingClient = C.Bind("Multiplayer", "ring_on_client", true, "Run the Slip Ring data/power bridge on clients too (the game already syncs port values and electric power). Turn off if a client shows double power use across a slip ring");
            MpFakeClient = C.Bind("Multiplayer", "fake_client", false, "(debug) Pretend this machine is a client, with no network at all: nothing of the mod moves, but the client's drawing code runs — to see what a client gets");
            MpNetLog = C.Bind("Multiplayer", "net_log", false, "(debug) Log every message of the mod's own network channel (hello, turret seat aim)");
            MpLoopback = C.Bind("Multiplayer", "test_loopback", false, "(test, solo game) The turret seat aim goes through the mod's network message path and comes back as if a player on another machine had sent it: tests the server side of a client's aim on one machine");

            // ---- [Debug] ----
            TraceCfg = C.Bind("Debug", "trace", false, "(debug) Writes one line per step of the mod cycle and per patched call, every frame. Use it to find where a native crash happens: the log is flushed line by line, so the LAST line is the last step that ran. It makes the log huge — turn it on only to reproduce a crash");
            MarkKey = CfgMigration.Of(C, C.Bind("Debug", "mark_error_key", "", "(debug) Key that marks an error: log line + screenshot + note window. Empty = off (default). E.g.: F8"), "Debug", "tecla_marcar_erro");
            TurretAdjust = CfgMigration.Of(C, C.Bind("Debug", "turret_adjust_numpad", false, "(debug) Numpad / toggles the turret seat adjust mode (the numpad moves the seat/view). Off by default"), "Debug", "ajuste_turret_numpad");

            // 0.24.0: removes from the .cfg the keys that no version of the mod reads anymore (old Portuguese sections and keys
            // replaced by a _vN). Runs after all the binds, so it doesn't delete anything CfgMigration.Of was still going to migrate.
            CfgMigration.Scan(C,
                new[] { "Geral", "Pistao", "Roda", "Fisica", "Torreta", "Motor Comb", "Motor Pequeno", "Motor Medio", "Motor Grande", "Motor Comb Pequeno", "Motor Comb Medio", "Motor Comb Grande" },
                new[] { "Piston|power_per_meter", "Bearing|velocidade_max", "Bearing|impulso_max", "Bearing|rampa", "Bearing|servo_impulso", "Bearing|apoio_extra", "Bearing|servo_velocidade", "Bearing|servo_inverter", "Bearing|servo_graus", "Motor|rampa", "Motor|freio_fator", "Recoupler|folga", "Recoupler|juntar", "Recoupler|tempo_puxar", "Recoupler|angulo_max", "Beacon|consumo", "Beacon|esconder_perto", "Beacon|slot_icone", "Beacon|icone", "Debug|tecla_marcar_erro", "Debug|ajuste_turret_numpad", "Piston|damping", "Piston|spring_frequency", "Piston|spring_damping", "Piston|hard_stop", "Physics|substeps", "Turret Seat|pitch_min", "Turret Seat|pitch_max", "Piston|merge_snap_max", "Piston|rebuild_interval", "Piston|merge_when_still", "Piston|move_without_splitting", "Piston|collider_update_interval" });

            // 0.19.1: the servo accepts up to 360 degrees (the old 180 -> 45 correction was removed)
            KeybindsLink.Name(PluginInfo.PluginGuid, Marker.ModName); KeybindsLink.Hide(PluginInfo.PluginGuid, "Turret Seat", "cursor_key");
            Config.SettingChanged += (s, e) => { try { Apply(); Marker.ForgetKeys(); } catch (Exception) { } };   // 0.12.13: changed in the menu (Mod settings) -> takes effect right away
            KeybindsLink.Register(Marker.ModName, "mark_error", "(debug) Mark an error (log line + screenshot + note window)", MarkKey.Value);
            KeybindsLink.Register(Marker.ModName, "turret_cursor", "Turret seat: show / hide the cursor", TurretCursorKey.Value);
            Apply();
            base.Log.LogInfo(PluginInfo.PluginName + " " + PluginInfo.PluginVersion + " loaded.");

            try
            {
                var regs = global::ComponentsFramework.ComponentsFrameworkApi.RegisterComponents(PluginInfo.PluginName, Wheels.Configure);
                Mirrors.Connect(regs);   // 0.12.14: V in the garage ("M" versions)
                global::ComponentsFramework.ComponentsFrameworkApi.RegisterLocalization();
            }
            catch (Exception ex) { base.Log.LogError("registering the blocks with the Components Framework failed: " + ex); }

            try
            {
                Texts.Folder = System.IO.Path.GetDirectoryName(typeof(CrapMechanicsPlugin).Assembly.Location);
                Marker.LogsFolder = System.IO.Path.Combine(Texts.Folder, "logs");
                var h = new HarmonyLib.Harmony(PluginInfo.PluginGuid); Texts.H = h; h.PatchAll(typeof(Texts)); try { OldNames.BuildMap(); OldNames.ConvertBlueprintFiles(); } catch (Exception ex) { base.Log.LogWarning("old prefab names: blueprints not converted (" + ex.Message + ") — convert old blueprints with bp_convert.py"); }   // 0.28.1: files on disk, no Harmony patch
                h.PatchAll(typeof(InventoryTab)); h.PatchAll(typeof(Mirrors)); h.PatchAll(typeof(Wheels)); h.PatchAll(typeof(Rewind)); h.PatchAll(typeof(PatchSystems)); h.PatchAll(typeof(RadarIcon)); Unloading.Patch(h); Transition.Patch(h);   // 0.28.2: in 0.28.1 these were inside the comment above
            }
            catch (Exception ex) { base.Log.LogError("patching the port texts failed: " + ex); }

            AddComponent<Cycle>();
        }

        public static void Apply()
        {
            JointSystem.PistonSpeed = PistonSpeed.Value; JointSystem.PistonImpulse = PistonImpulse.Value;
            JointSystem.PistonConsumptionPerM = PistonConsumption.Value; JointSystem.MotorRamp = MotorRamp.Value; JointSystem.ForceReserve = ForceReserve.Value; JointSystem.ServoImpulse = ServoImpulse.Value; JointSystem.ServoDegrees = ServoDegrees.Value; JointSystem.ServoSpeed = ServoSpeed.Value; JointSystem.ServoTipSpeed = ServoTipSpeed.Value; JointSystem.ServoGain = ServoGain.Value; JointSystem.SnapBack = SnapBack.Value; JointSystem.PistonCollides = PistonCollides.Value; JointSystem.WholeUntilMoved = WholeUntilMoved.Value; JointSystem.TurretWholeUntilAimed = TurretWhole.Value; JointSystem.MassRatio = MassRatio.Value; JointSystem.PistonTiltMax = PistonTilt.Value; ModLog.Tracing = TraceCfg != null && TraceCfg.Value;   // 0.24.0: Apply runs on every change in the menu — without this, turning on the trace in Mod Menu did nothing
            // 0.24.1: this remainder was INSIDE the comment above since 0.24.0 (solver, reinforcement, rails, servo... went back to the code's defaults)
            JointSystem.ServoInvert = ServoInvert.Value; JointSystem.BearingSupport = BearingSupport.Value; Wheels.SportsFriction = WheelFriction.Value; Wheels.OffroadFriction = WheelFrictionOffroad.Value; JointSystem.SolverIterations = SolverIterations.Value; JointSystem.IterationsAlways = IterationsAlways.Value; JointSystem.Rigid = Stiffness.Value; JointSystem.ZeroGravLaunch = ZeroGravLaunch.Value; JointSystem.FitWait = FitWait.Value; JointSystem.BaseReinforce = BaseReinforce.Value; RadarIcon.Request = IconBeacon.Value; RadarIcon.Slot = IconSlot.Value; JointSystem.StartTime = StartTime.Value; JointSystem.StartPause = StartPause.Value; JointSystem.NoCollisionAtStart = NoCollision.Value; JointSystem.PistonRails = PistonRails.Value; JointSystem.PistonRailsPerMeter = PistonRailsPerMeter.Value; Couplers.MaxAngle = RecouplerAngle.Value; Couplers.Tolerance = RecouplerSlack.Value; Couplers.Join = RecouplerMerge.Value; Couplers.PullTime = RecouplerPull.Value; Couplers.HideNear = BeaconHide.Value; Couplers.HideOwn = BeaconOwn == null || BeaconOwn.Value; Couplers.BeaconPowerUse = BeaconConsumption.Value;
            if (BonusExtras != null) Bonus.ExtrasCfg = BonusExtras.Value;
            if (MpOwnerOnly != null) { Multi.OwnerOnly = MpOwnerOnly.Value; Multi.FakeClient = MpFakeClient.Value; Multi.BreakOnClient = MpBreakClient.Value; Multi.RingOnClient = MpRingClient.Value; ModNet.Log = MpNetLog.Value; ModNet.Loopback = MpLoopback.Value; }   // 0.24.0-mp; 0.28.0
            JointSystem.FuelConsumption = FuelConsumption.Value; JointSystem.FuelPower = FuelPower.Value; JointSystem.BrakeFactor = BrakeFactor.Value;
            JointSystem.TurretSens = TurretSens.Value; JointSystem.TurretGain = TurretGain.Value; JointSystem.TurretMaxSpeed = TurretMaxSpeed.Value; JointSystem.TurretLinkAccel = System.Math.Max(10f, TurretLinkAccel.Value);
            JointSystem.TurretInertia = TurretInertia.Value; JointSystem.ViewRotates = TurretViewRotates.Value; JointSystem.TurretCamera = TurretCamera.Value; JointSystem.BindsReach = Math.Max(2.12f, TurretReach.Value); JointSystem.SeatBindsReach = Math.Max(2.12f, SeatReach.Value); Signal.Relink = SignalRelink.Value;
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
            try
            {
                if (__instance == null || !JointSystem.EmOk || Unloading.Active) return;
                var p = __instance.Pointer;
                if (p == Ecs.PtrTransform && Ecs.PtrTransform != IntPtr.Zero) JointSystem.TurretBeforeTransform();
                else if (p == Ecs.PtrPhysicsPre && Ecs.PtrPhysicsPre != IntPtr.Zero)
                {
                    Far.Step(JointSystem.CurrentEm, ReplayLink.InPlayback());   // 0.19.13: 100 km rule (instead of the game's SPActiveUpdateSystem)
                    if (!ReplayLink.InPlayback()) JointSystem.SplitsInTick(JointSystem.CurrentEm);   // 0.26.15: joint created in the same tick in which the game splits the parts
                }
                else if (p == Ecs.PtrPresentation && Ecs.PtrPresentation != IntPtr.Zero && JointSystem.SeatedTurret != null) JointSystem.TurretCameraLTW(true);
                else if ((p == Ecs.PtrTickPre || p == Ecs.PtrTickPost) && p != IntPtr.Zero) Radio.Copy(JointSystem.CurrentEm, true);   // 0.19.6: LRT copies the signal inside the tick
            }
            catch (Exception ex) { if (errors++ < 3) ModLog.Error("prefix group OnUpdate: " + ex.Message); }
        }
        [HarmonyLib.HarmonyPostfix]
        static void After(ComponentSystemGroup __instance)
        {
            try
            {
                if (__instance == null || !JointSystem.EmOk || Unloading.Active) return;
                if (__instance.Pointer == Ecs.PtrTransform && Ecs.PtrTransform != IntPtr.Zero) { JointSystem.TurretAfterTransform(); JointSystem.CaptureGameEye(); try { Couplers.WriteRadar(JointSystem.CurrentEm); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("radar/write: " + ex.Message); } }
                else if (__instance.Pointer == Ecs.PtrBindsGroup && Ecs.PtrBindsGroup != IntPtr.Zero) JointSystem.TurretBindsDistance();
                else if ((__instance.Pointer == Ecs.PtrTickPre || __instance.Pointer == Ecs.PtrTickPost) && __instance.Pointer != IntPtr.Zero) Radio.Copy(JointSystem.CurrentEm, true);   // 0.19.6
            }
            catch (Exception ex) { if (errors++ < 3) ModLog.Error("postfix group OnUpdate: " + ex.Message); }
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

        void Update()
        {
            try
            {
                Texts.Undo();
                try { Transition.Step(); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("transition log: " + ex.Message); }   // 0.19.12: snapshots at +1/+3/+6 s (before any early return)
                if (CrapMechanicsPlugin.IsActive == null || !CrapMechanicsPlugin.IsActive.Value) return;
                if (Unloading.Active) return;   // 01/10: the game is unloading the world — no reading the ECS
                if (Unloading.Pending) { Unloading.Confirm(); try { JointSystem.ForgetAll(); Ecs.ResetPointers(); } catch (Exception ex) { ModLog.Warn("after unload: " + ex.Message); } }
                if (world != null && world.IsCreated) { try { Marker.Step(world.EntityManager); } catch (Exception ex) { if (errors++ < 5) ModLog.Error("mark: " + ex); } }
                InventoryTab.Step();
                float dt = UnityEngine.Time.deltaTime;
                try { Props.Step(dt); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("properties: " + ex.Message); }   // 0.23.2
                if (world == null || !world.IsCreated)
                {
                    tWorld += dt; if (tWorld < 1f) return; tWorld = 0f;
                    var w = World.DefaultGameObjectInjectionWorld;
                    if (w == null || !w.IsCreated) return;
                    world = w; ModLog.Info("World: " + w.Name);
                }
                if (ReplayLink.InPlayback())
                {
                    JointSystem.SeatVisibleInPlayback(world.EntityManager);   // 0.11.3: the Replay mod is playing back (simulation frozen) — don't touch anything...
                    try { Far.Step(world.EntityManager, true); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("replay: far parts: " + ex.Message); }   // 0.19.13: turn back on what Recenter made the game turn off
                    try { Radio.Copy(world.EntityManager, false); } catch (Exception ex) { if (errors++ < 3) ModLog.Warn("replay: long range copy: " + ex.Message); }   // 0.19.11 (Andre: "it didn't record the LRT camera in the replay"): ...only the LRT keeps passing the signal on (the monitor shows the live camera)
                    return;
                }
                // 0.12.6 (Andre: "the BP menu got extremely laggy"): the BP previews become entities in the world (UIBlueprintSave.
                // CreateEntitiesFromBinary) and the mod found/processed all their blocks. With the menu open (outside the simulation) the cycle pauses.
                if (MenuBP()) return;
                Ecs.PreparePointers();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                JointSystem.Step(world.EntityManager, dt);
                Measure(sw.Elapsed.TotalMilliseconds, dt);
                RadarIcon.Step(dt); JointSystem.T("radar icons — cycle end"); if (JointSystem.Trail > 0) JointSystem.Trail--;
            }
            catch (Exception ex) { if (errors++ < 5) ModLog.Error("Update: " + ex); }
        }
        // 0.12.6: cycle time — every 10 s, if the average goes over 2 ms (or the peak over 20 ms), it goes to the log with the numbers
        static double sumMs, peakMs; static int nMed; static float tMed;
        static void Measure(double ms, float dt)
        {
            sumMs += ms; nMed++; if (ms > peakMs) peakMs = ms; tMed += UnityEngine.Time.unscaledDeltaTime;
            if (tMed < 10f) return;
            double average = sumMs / Math.Max(1, nMed);
            string stages = JointSystem.SlowestSteps(nMed);   // 0.26.10: resets the step sums every 10 s, logging or not
            if (average > 2.0 || peakMs > 20.0) ModLog.Warn("performance: mod cycle average " + average.ToString("F2") + " ms, peak " + peakMs.ToString("F1") + " ms (" + nMed + " frames) — " + JointSystem.Counts() + " | slowest steps, ms/frame: " + stages);
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
        void OnGUI() { try { JointSystem.DrawAdjust(); } catch (Exception ex) { if (errors++ < 5) ModLog.Error("OnGUI: " + ex); } }
        void LateUpdate() { try { if (JointSystem.SeatedTurret != null) JointSystem.TurretCameraLTW(false); } catch (Exception ex) { if (errors++ < 5) ModLog.Error("LateUpdate: " + ex); } }
        void OnApplicationQuit() { try { if (world != null && world.IsCreated) JointSystem.Clean(world.EntityManager); } catch (Exception) { } }
    }
}
