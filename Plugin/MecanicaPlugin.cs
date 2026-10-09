using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Unity.Entities;

namespace Mecanica
{
    /// <summary>Pistao e bearing para Approximately Up. Os blocos (prefabs em Content/) sao registrados pelo Components Framework;
    /// a fisica (separar a nave + PhysicsJoint com motor) roda aqui, no Ciclo, todo frame.</summary>
    [BepInPlugin(PluginInfo.PluginGuid, PluginInfo.PluginDisplayName, PluginInfo.PluginVersion)]
    [BepInDependency("com.approximatelyup.mods.componentsframework")]
    [BepInDependency("com.approximatelyup.mods.modmenu", BepInDependency.DependencyFlags.SoftDependency)]
    public class MecanicaPlugin : BasePlugin
    {
        public static MecanicaPlugin Instancia;
        public static ConfigEntry<float> PistaoVel, PistaoImpulso, PistaoConsumo, MotorRampa, ServoImpulso, ServoGraus, BearingApoio, RodaAtrito, RodaAtritoOffroad;
        public static ConfigEntry<float>[] MotorVel = new ConfigEntry<float>[3], MotorImpulso = new ConfigEntry<float>[3], MotorConsumo = new ConfigEntry<float>[3];
        public static ConfigEntry<bool> LogDetalhe, Ativo, ServoInverter; public static ConfigEntry<float> ServoVel;
        public static ConfigEntry<int> IteracoesSolver, IconeBeacon, SlotIcone; public static ConfigEntry<float> PartidaTempo, PartidaPausa, SemColisao, PistaoGuia, RecouplerAngulo; public static ConfigEntry<float> RecouplerFolga, BeaconEsconde, RecouplerPuxar, BeaconConsumo; public static ConfigEntry<bool> RecouplerJuntar, BeaconProprio, BeaconLonge;
        public static ConfigEntry<float> ReforcoBase, CombConsumo, CombEnergia, FreioFator, ForcaReserva;
        public static ConfigEntry<float> TorretaAlcance, TorretaSens, TorretaGanho, TorretaVelMax, TorretaPitchMin, TorretaPitchMax, TorretaEloAcel; public static ConfigEntry<bool> TorretaInercia, TorretaCamera, TorretaVisaoGira; public static ConfigEntry<string> TorretaModoBanco, TorretaBancoAjuste, TorretaJogadorB, TorretaVisaoA; public static ConfigEntry<string> TorretaTeclaCursor;
        public static ConfigEntry<float>[] MotorCombVel = new ConfigEntry<float>[3], MotorCombImpulso = new ConfigEntry<float>[3];

        public static ConfigEntry<string> TeclaMarca; public static ConfigEntry<bool> AjusteTurret;
        public override void Load()
        {
            Instancia = this; Registro.Fonte = Log;
            LogArquivo.Iniciar(System.IO.Path.GetDirectoryName(typeof(MecanicaPlugin).Assembly.Location));
            // 0.13.0: secoes/chaves do .cfg em ingles; CfgMigra.De traz o valor da entrada antiga (portugues) pra nova
            var C = Config;
            Ativo = CfgMigra.De(C, C.Bind("General", "enabled", true, "Turns the blocks' physics on/off (the blocks still exist)"), "Geral", "ativo");
            LogDetalhe = CfgMigra.De(C, C.Bind("General", "detailed_log", false, "Step-by-step log (separation, joint, motor). Off by default; turn on to report a problem"), "Geral", "log_detalhe");
            PistaoVel = CfgMigra.De(C, C.Bind("Piston", "speed", 1.0f, "Piston speed (m/s)"), "Pistao", "velocidade");
            PistaoImpulso = CfgMigra.De(C, C.Bind("Piston", "max_impulse", 150000f, "Motor force per physics step (0 = infinite)"), "Pistao", "impulso_max");
            if (PistaoImpulso.Value == 20000f) PistaoImpulso.Value = 150000f;   // 0.12.3 (Andre: "deixa bem mais forte os pistoes")
            if (PistaoImpulso.Value == 500f) PistaoImpulso.Value = 20000f;
            // 0.23.0 (Andre: "diminui o valor mas ainda deixa proporcional pelo tamanho do stroke"): 1 P/s por metro do curso REAL
            // (ate o Piston End, ou o maximo, x o knob) — 8 m cheio = 8 P/s, 2 m = 2 P/s. Chave nova: o valor velho (2,5) fica pra tras.
            PistaoConsumo = C.Bind("Piston", "power_per_meter_v2", 1f, "Power (P/s) while moving, per meter of the stroke it actually makes (knob included): 8 m = 8, 2 m = 2");
            string[] niv = { "Motor Pequeno", "Motor Medio", "Motor Grande" };   // secoes antigas (migracao)
            string[] nivEn = { "Small Motor", "Medium Motor", "Large Motor" };
            // 0.5.0: bem mais fortes e rapidos (Andre: 2 motores grandes ainda fracos pro carro). Valor antigo padrao -> novo padrao.
            float[] v = { 5f, 8f, 12f }, imp = { 1000f, 4000f, 13000f }, pps = { 7f, 28f, 70f }, ppsVelho = { 15f, 60f, 160f };   // 0.7.6: 2 grandes < 150 P/s
            float[] vVelho = { 1.5f, 2.5f, 4f }, impVelho = { 200f, 790f, 2600f };
            for (int i = 0; i < 3; i++)
            {
                MotorVel[i] = CfgMigra.De(C, C.Bind(nivEn[i], "max_speed", v[i], "Max speed (rad/s) at signal 1 (6.28 = 1 turn/s)"), niv[i], "velocidade_max");
                MotorImpulso[i] = CfgMigra.De(C, C.Bind(nivEn[i], "max_impulse", imp[i], "Motor force (torque) per physics step; same scale as the Electric Thrusters' force (0 = infinite)"), niv[i], "impulso_max");
                if (MotorVel[i].Value == vVelho[i]) MotorVel[i].Value = v[i];
                if (MotorImpulso[i].Value == impVelho[i]) MotorImpulso[i].Value = imp[i];
                MotorConsumo[i] = CfgMigra.De(C, C.Bind(nivEn[i], "power", pps[i], "Power (P/s) at signal 1 (small 7, medium 28, large 70: two large motors use 140)"), niv[i], "consumo");
                if (MotorConsumo[i].Value == ppsVelho[i]) MotorConsumo[i].Value = pps[i];
            }
            // 0.8.0 (form5): motores a combustivel — mesmas velocidades, +50% de forca, 0,85 de combustivel/s com sinal 1, 5 P/s de energia
            string[] nivC = { "Motor Comb Pequeno", "Motor Comb Medio", "Motor Comb Grande" };   // secoes antigas (migracao)
            string[] nivCEn = { "Small Fuel Motor", "Medium Fuel Motor", "Large Fuel Motor" };
            for (int i = 0; i < 3; i++)
            {
                MotorCombVel[i] = CfgMigra.De(C, C.Bind(nivCEn[i], "max_speed", v[i], "Max speed (rad/s) at signal 1"), nivC[i], "velocidade_max");
                MotorCombImpulso[i] = CfgMigra.De(C, C.Bind(nivCEn[i], "max_impulse", imp[i] * 1.5f, "Motor force (torque) per physics step with the fuel mixture at 100% efficiency (electric motor of the same size +50%)"), nivC[i], "impulso_max");
            }
            CombConsumo = CfgMigra.De(C, C.Bind("Fuel Motor", "fuel_per_second", 0.85f, "Fuel burned per second at signal 1 (same as the Small Fuel Thruster)"), "Motor Comb", "consumo_combustivel");
            CombEnergia = CfgMigra.De(C, C.Bind("Fuel Motor", "power", 5f, "Power (P/s) used while turning (starter)"), "Motor Comb", "consumo_energia");
            MotorRampa = CfgMigra.De(C, C.Bind("Motor", "ramp", 1.0f, "Seconds to reach max speed"), "Motor", "rampa");
            ForcaReserva = C.Bind("Motor", "force_pool", 4f, new ConfigDescription("Each motor has this many times its max_impulse, shared equally among the bearings it drives (1 bearing gets all of it; 4 bearings get max_impulse each)", new AcceptableValueRange<float>(1f, 16f)));
            FreioFator = CfgMigra.De(C, C.Bind("Motor", "brake_factor", 30f, "Brake force (motor's 4th yellow port, signal 0..1) = motor impulse x this factor (at signal 1). Mechanical brake: works without power/fuel and locks ALL wheels of the vehicle (bearings with no direct steering signal), not just the motor's"), "Motor", "freio_fator");
            if (FreioFator.Value == 3f || FreioFator.Value == 15f) FreioFator.Value = 30f;   // migra os padroes antigos
            ServoImpulso = CfgMigra.De(C, C.Bind("Bearing", "servo_impulse", 3000f, "Force used to hold the angle when the bearing gets a direct signal (no motor)"), "Bearing", "servo_impulso");
            if (ServoImpulso.Value == 500f) ServoImpulso.Value = 3000f;
            RodaAtrito = CfgMigra.De(C, C.Bind("Wheel", "friction", 1.5f, "Friction of the Sports wheels (a normal game block is ~0.5); on contact the higher of the two is used"), "Roda", "atrito");
            RodaAtritoOffroad = CfgMigra.De(C, C.Bind("Wheel", "offroad_friction", 2.2f, "Friction of the Offroad wheels"), "Roda", "atrito_offroad");
            BearingApoio = CfgMigra.De(C, C.Bind("Bearing", "extra_support", 1f, "Distance (m) of the extra support points on the axis (one on each side of the face); keeps the bearing stiff under load (0 = off)"), "Bearing", "apoio_extra");
            if (BearingApoio.Value == 0.5f) BearingApoio.Value = 1f;
            ReforcoBase = CfgMigra.De(C, C.Bind("Physics", "base_reinforcement", 20f, "Multiplies the strength (breakForce, game default 3300) of the game joints that hold a bearing/piston to its base (1 = unchanged; capped at 1e7)"), "Fisica", "reforco_base");
            if (ReforcoBase.Value == 1000f) ReforcoBase.Value = 20f;   // 0.7.2 -> volta pro da 0.7.1
            RecouplerFolga = CfgMigra.De(C, C.Bind("Recoupler", "tolerance", 0.5f, "Max distance (m) between the Recoupler and the face released by the Decoupler for it to recouple"), "Recoupler", "folga");
            RecouplerJuntar = CfgMigra.De(C, C.Bind("Recoupler", "merge", true, "true = after pulling into place, merges the two parts into one (the opposite of the decoupler); false = only locks them with a fixed joint"), "Recoupler", "juntar");
            RecouplerPuxar = CfgMigra.De(C, C.Bind("Recoupler", "pull_time", 0.6f, "Seconds to pull the recoupler into the right position"), "Recoupler", "tempo_puxar");
            BeaconConsumo = CfgMigra.De(C, C.Bind("Beacon", "power", 5f, "Power (P/s) the Beacon draws from its power port; without power it doesn't show on radars (0 = needs no power)"), "Beacon", "consumo");
            BeaconLonge = C.Bind("Beacon", "keep_simulated_far", true, "true = a loose part (a probe, a dropped rocket) that carries a powered Beacon keeps being simulated when it is more than 100 km from you: physics, camera, transmitter and thruster keep working (the game normally switches every part beyond 100 km off — the mod takes over that rule, same limit, beacons exempt). false = the game's rule as is");
            Longe.Ligado = BeaconLonge.Value;
            BeaconProprio = C.Bind("Beacon", "hide_on_own_vehicle", true, "true = the beacon disappears from every radar while you are on its own vehicle (so your radar doesn't show you). The game's radar can't hide it from just one radar, so with the Replay free camera (or watching from the garage) it is gone everywhere: set false to always show it");
            BeaconEsconde = CfgMigra.De(C, C.Bind("Beacon", "hide_within", 40f, "The Beacon disappears from the radar when the player is closer than this distance (m): the radar you are looking at is the one on the beacon's own vehicle (0 = always shown)"), "Beacon", "esconder_perto");
            SlotIcone = CfgMigra.De(C, C.Bind("Beacon", "icon_slot", 8, "Preferred game icon slot for the beacon drawings (0..9). They only go over icons nothing in the world is using right now; the next free one is used otherwise"), "Beacon", "slot_icone");
            PartidaTempo = CfgMigra.De(C, C.Bind("Physics", "soft_start", 0.3f, "Seconds bearings/pistons stay locked right after spawning (prevents spinning/breaking when the simulation starts; 0 = off)"), "Fisica", "partida_suave");
            if (PartidaTempo.Value == 1f) PartidaTempo.Value = 0.3f;   // 0.8.0: o problema era o 360 do servo (0.7.14), da pra montar rapido
            SemColisao = CfgMigra.De(C, C.Bind("Wheel", "no_collision_at_start", 1f, "At start a wheel doesn't collide with the parts above it in the bearing chain (steering wheel vs. chassis); collision is turned back on this many seconds after the start queue finishes (0 = collision is never disabled)"), "Roda", "sem_colisao_partida");
            if (SemColisao.Value == 3f) SemColisao.Value = 1f;
            PartidaPausa = CfgMigra.De(C, C.Bind("Physics", "start_stagger", 0.05f, "After the soft start, unlocks ONE joint at a time with this pause (s) between each: first the ones attached to the chassis, then the ones above them (wheel on the steering knuckle); on the same level the lightest first (0 = unlock all at once)"), "Fisica", "partida_pausa");
            if (PartidaPausa.Value == 0.3f) PartidaPausa.Value = 0.05f;   // 0.8.0
            IconeBeacon = CfgMigra.De(C, C.Bind("Beacon", "icon", -1, "Beacon icon on the radar: -1 = the mod's drawing (signal waves) over icon_slot; 0..9 = an unmodified game icon (2 = package)"), "Beacon", "icone");
            IteracoesSolver = CfgMigra.De(C, C.Bind("Physics", "solver_iterations", 20, "Physics solver iterations while a piston/bearing is active (the game uses 4; more = stiffer joints and less wobbly part chains, costs CPU; 0 = unchanged)"), "Fisica", "iteracoes_solver");
            if (IteracoesSolver.Value == 12) IteracoesSolver.Value = 20;   // 0.12.3: joints mais firmes (pistao cedia com carga)
            if (IteracoesSolver.Value == 8) IteracoesSolver.Value = 12;   // 0.7.9
            PistaoGuia = CfgMigra.De(C, C.Bind("Piston", "rails", 0.75f, "Distance (m) of the 4 rails parallel to the axis that keep the piston stiff sideways (0 = no rails)"), "Pistao", "trilhos");
            if (PistaoGuia.Value == 0.5f) PistaoGuia.Value = 0.75f;   // 0.12.3: trilhos mais afastados = mais firme de lado
            RecouplerAngulo = CfgMigra.De(C, C.Bind("Recoupler", "max_angle", 30f, "Only couples/pulls if the recoupler is misaligned by at most this angle (degrees) relative to the socket"), "Recoupler", "angulo_max");
            ServoVel = CfgMigra.De(C, C.Bind("Bearing", "servo_speed", 3f, "Max speed (rad/s) of the servo (steering) moving to the requested angle (3 = about half a turn per second)"), "Bearing", "servo_velocidade");
            ServoInverter = CfgMigra.De(C, C.Bind("Bearing", "servo_invert", false, "true = inverts the direction the servo turns (if the steering is reversed)"), "Bearing", "servo_inverter");
            ServoGraus = CfgMigra.De(C, C.Bind("Bearing", "servo_degrees", 45f, "Servo angle (degrees) at signal 1 for bearings WITHOUT the side knob (old blueprints); bearings with the knob use the knob (10 to 360 degrees)"), "Bearing", "servo_graus");
            // 0.12.0: turret seat
            TorretaSens = CfgMigra.De(C, C.Bind("Turret Seat", "sensitivity", 0.1f, "Aim degrees per mouse pixel"), "Torreta", "sensibilidade");
            TorretaGanho = CfgMigra.De(C, C.Bind("Turret Seat", "gain", 12f, "Servo: requested speed (rad/s) per radian of error (higher = stiffer/faster)"), "Torreta", "ganho");
            TorretaVelMax = CfgMigra.De(C, C.Bind("Turret Seat", "max_speed", 720f, "Max turret rotation speed (degrees/s)"), "Torreta", "velocidade_max");
            TorretaPitchMin = CfgMigra.De(C, C.Bind("Turret Seat", "pitch_min", -30f, "Downward limit (degrees)"), "Torreta", "pitch_min");
            TorretaPitchMax = CfgMigra.De(C, C.Bind("Turret Seat", "pitch_max", 135f, "Upward limit (degrees). 135 = upside down until the top plate is ~5 cm from the base"), "Torreta", "pitch_max");
            if (TorretaPitchMax.Value == 75f) TorretaPitchMax.Value = 135f;
            TorretaEloAcel = CfgMigra.De(C, C.Bind("Turret Seat", "link_acceleration", 360f, "Max acceleration (degrees/s2) of a structure attached to 2+ faces of the turret (a LINK), and of the whole ship when the base and the plates are on the same structure. Lower = smoother (less jolting on the ground)"), "Torreta", "elo_aceleracao");
            TorretaModoBanco = CfgMigra.De(C, C.Bind("Turret Seat", "seat_mode", "A", "cockpit: seat visible and you turn with it (nothing crosses your view); A: seat invisible to you only; B: seat visible and you sit at player_position_b. Num* cycles it in adjust mode (Num/)"), "Torreta", "modo_banco_v2");
            // 0.12.9: o modelo novo ja traz o banco 35 cm mais baixo e 2 cm pra frente (o ajuste que o Andre fez) -> o ajuste novo e' o antigo menos isso.
            // banco_ajuste_v2 (0.12.8) so e' lido pra essa conversao e depois tirado do .cfg.
            var ajusteV2 = Config.Bind("Torreta", "banco_ajuste_v2", "0;0;0", "(old, only read for migration)");
            TorretaBancoAjuste = CfgMigra.De(C, C.Bind("Turret Seat", "seat_offset", "?", "Seat offset x;y;z (m) relative to the model's position (adjust mode: Num/, Num1, numeric keypad). The model already places the seat at (0; -0.85; 0.02) and the plate/rod follow the offset"), "Torreta", "banco_ajuste_v3");
            if (TorretaBancoAjuste.Value == "?")
            {
                var bv = Juntas.Ler3(ajusteV2.Value, Unity.Mathematics.float3.zero);
                bv = new Unity.Mathematics.float3 { x = bv.x, y = bv.y + 0.35f, z = bv.z - 0.02f };
                if (System.Math.Abs(bv.x) < 0.005f && System.Math.Abs(bv.y) < 0.005f && System.Math.Abs(bv.z) < 0.005f) bv = Unity.Mathematics.float3.zero;
                TorretaBancoAjuste.Value = Juntas.Esc3(bv);
            }
            try { Config.Remove(ajusteV2.Definition); Config.Save(); } catch (Exception) { }
            TorretaJogadorB = CfgMigra.De(C, C.Bind("Turret Seat", "player_position_b", "0;0;0", "Mode B: where you sit, x;y;z (m) in the block (0;0;0 = block center = pivot center)"), "Torreta", "jogador_posicao_b");
            TorretaVisaoA = CfgMigra.De(C, C.Bind("Turret Seat", "view_position_a", "0;0.42;-0.46", "Mode A: where your view is, x;y;z (m) in the block (0;0;0 = pivot center)"), "Torreta", "visao_a_v2");
            TorretaVisaoGira = CfgMigra.De(C, C.Bind("Turret Seat", "view_a_rotates", true, "Mode A: true = the view turns with the rings (always stays at the same spot in the cockpit); false = fixed point in the block"), "Torreta", "visao_a_gira");
            TorretaInercia = CfgMigra.De(C, C.Bind("Turret Seat", "infinite_inertia", true, "The seat's part is not turned by any force (weight on the plates doesn't make it tip); false = only the servo holds it"), "Torreta", "inercia_infinita");
            TorretaCamera = CfgMigra.De(C, C.Bind("Turret Seat", "camera", true, "While seated in the turret seat the view turns with the aim (false = game camera)"), "Torreta", "camera");
            TorretaAlcance = C.Bind("Turret Seat", "bindings_reach", 4.5f, new ConfigDescription("Seated in the turret seat: seat key bindings work for bound parts up to this far (m) from you. The game itself only accepts ~2.1 m (4.5 m is how far you can click a part to bind it)", new AcceptableValueRange<float>(2.12f, 10f)));
            TorretaTeclaCursor = CfgMigra.De(C, C.Bind("Turret Seat", "cursor_key", "LeftCtrl", "Key (Input System name) that shows/hides the cursor while seated in the turret seat (aiming pauses while the cursor is shown)"), "Torreta", "tecla_cursor");
            // 0.19.1: o servo aceita ate 360 graus (a correcao antiga 180 -> 45 saiu)
            KeybindsLink.Name(PluginInfo.PluginGuid, Marca.ModNome); KeybindsLink.Hide(PluginInfo.PluginGuid, "Turret Seat", "cursor_key");
            Config.SettingChanged += (s, e) => { try { Aplicar(); Marca.EsquecerTeclas(); } catch (Exception) { } };   // 0.12.13: mudou no menu (Mod settings) -> vale na hora
            TeclaMarca = CfgMigra.De(C, C.Bind("Debug", "mark_error_key", "", "(debug) Key that marks an error: log line + screenshot + note window. Empty = off (default). E.g.: F8"), "Debug", "tecla_marcar_erro");
            AjusteTurret = CfgMigra.De(C, C.Bind("Debug", "turret_adjust_numpad", false, "(debug) Numpad / toggles the turret seat adjust mode (the numpad moves the seat/view). Off by default"), "Debug", "ajuste_turret_numpad");
            KeybindsLink.Register(Marca.ModNome, "mark_error", "(debug) Mark an error (log line + screenshot + note window)", TeclaMarca.Value);
            KeybindsLink.Register(Marca.ModNome, "turret_cursor", "Turret seat: show / hide the cursor", TorretaTeclaCursor.Value);
            Aplicar();
            base.Log.LogInfo(PluginInfo.PluginName + " " + PluginInfo.PluginVersion + " loaded.");

            try
            {
                var regs = global::ComponentsFramework.ComponentsFrameworkApi.RegisterComponents(PluginInfo.PluginName, Rodas.Configurar);
                Espelhos.Ligar(regs);   // 0.12.14: V na garagem (versoes "M")
                global::ComponentsFramework.ComponentsFrameworkApi.RegisterLocalization();
            }
            catch (Exception ex) { base.Log.LogError("registering the blocks with the Components Framework failed: " + ex); }

            try
            {
                Textos.Pasta = System.IO.Path.GetDirectoryName(typeof(MecanicaPlugin).Assembly.Location);
                Marca.PastaLogs = System.IO.Path.Combine(Textos.Pasta, "logs");
                var h = new HarmonyLib.Harmony(PluginInfo.PluginGuid); Textos.H = h; h.PatchAll(typeof(Textos)); h.PatchAll(typeof(Aba)); h.PatchAll(typeof(Espelhos)); h.PatchAll(typeof(Rodas)); h.PatchAll(typeof(Rebobinar)); h.PatchAll(typeof(PatchSistemas)); h.PatchAll(typeof(RadarIcone)); Descarga.Patch(h); Transicao.Patch(h);
            }
            catch (Exception ex) { base.Log.LogError("patching the port texts failed: " + ex); }

            AddComponent<Ciclo>();
        }

        public static void Aplicar()
        {
            Juntas.PistaoVel = PistaoVel.Value; Juntas.PistaoImpulso = PistaoImpulso.Value;
            Juntas.PistaoConsumoPorM = PistaoConsumo.Value; Juntas.MotorRampa = MotorRampa.Value; Juntas.ForcaReserva = ForcaReserva.Value; Juntas.ServoImpulso = ServoImpulso.Value; Juntas.ServoGraus = ServoGraus.Value; Juntas.ServoVel = ServoVel.Value; Juntas.ServoInverter = ServoInverter.Value; Juntas.BearingApoio = BearingApoio.Value; Rodas.AtritoSports = RodaAtrito.Value; Rodas.AtritoOffroad = RodaAtritoOffroad.Value; Juntas.IteracoesSolver = IteracoesSolver.Value; Juntas.ReforcoBase = ReforcoBase.Value; RadarIcone.Pedido = IconeBeacon.Value; RadarIcone.Slot = SlotIcone.Value; Juntas.PartidaTempo = PartidaTempo.Value; Juntas.PartidaPausa = PartidaPausa.Value; Juntas.SemColisaoPartida = SemColisao.Value; Juntas.PistaoGuia = PistaoGuia.Value; Acoplador.MaxAngulo = RecouplerAngulo.Value; Acoplador.Tolerancia = RecouplerFolga.Value; Acoplador.Juntar = RecouplerJuntar.Value; Acoplador.PuxarTempo = RecouplerPuxar.Value; Acoplador.EsconderPerto = BeaconEsconde.Value; Acoplador.EsconderProprio = BeaconProprio == null || BeaconProprio.Value; Acoplador.ConsumoBeacon = BeaconConsumo.Value;
            Juntas.CombConsumo = CombConsumo.Value; Juntas.CombEnergia = CombEnergia.Value; Juntas.FreioFator = FreioFator.Value;
            Juntas.TorretaSens = TorretaSens.Value; Juntas.TorretaGanho = TorretaGanho.Value; Juntas.TorretaVelMax = TorretaVelMax.Value; Juntas.TorretaPitchMin = TorretaPitchMin.Value; Juntas.TorretaPitchMax = TorretaPitchMax.Value; Juntas.TorretaEloAcel = System.Math.Max(10f, TorretaEloAcel.Value);
            Juntas.TorretaInercia = TorretaInercia.Value; Juntas.VisaoGira = TorretaVisaoGira.Value; Juntas.TorretaCamera = TorretaCamera.Value; Juntas.AlcanceBinds = Math.Max(2.12f, TorretaAlcance.Value);
            Juntas.CarregarAjuste(TorretaModoBanco.Value, TorretaBancoAjuste.Value, TorretaJogadorB.Value, TorretaVisaoA.Value);
            try { if (Enum.TryParse<UnityEngine.InputSystem.Key>(TorretaTeclaCursor.Value.Trim(), true, out var k)) Juntas.TorretaTeclaCursor = k; else Registro.Aviso("Turret Seat.cursor_key '" + TorretaTeclaCursor.Value + "' does not exist; using LeftCtrl"); } catch (Exception) { }
            for (int i = 0; i < 3; i++) { Juntas.MotorCombVel[i] = MotorCombVel[i].Value; Juntas.MotorCombImpulso[i] = MotorCombImpulso[i].Value; }
            for (int i = 0; i < 3; i++) { Juntas.MotorVel[i] = MotorVel[i].Value; Juntas.MotorImpulso[i] = MotorImpulso[i].Value; Juntas.MotorConsumo[i] = MotorConsumo[i].Value; }
            Juntas.LogDetalhe = LogDetalhe.Value;
        }
    }

    /// <summary>0.12.0 (turret seat): prefix em SystemBase.Update — quando a instancia e' o TransformSystemGroup (depois da fisica e da
    /// camera do jogo, antes dos LocalToWorld) escreve a rotacao da camera = mira; no PresentationSystemGroup reforca no LocalToWorld.</summary>
    // 01/10 (crash ao ir pro menu principal / carregar BP: coreclr.dll ProcessCLRException, c0000005): o patch em SystemBase.Update
    // embrulhava TODO system do jogo num trampolim gerenciado — quando um system lanca excecao (normal na troca de cena; o
    // ComponentSystemGroup.UpdateAllSystems pega e loga), a excecao nativa atravessava o trampolim do .NET e derrubava o jogo. Agora
    // o patch e' no OnUpdate dos GRUPOS: a excecao de um system filho e' pega dentro do grupo, abaixo do nosso quadro.
    [HarmonyLib.HarmonyPatch(typeof(ComponentSystemGroup), "OnUpdate")]
    internal static class PatchSistemas
    {
        static int erros;
        [HarmonyLib.HarmonyPrefix]
        static void Antes(ComponentSystemGroup __instance)
        {
            try
            {
                if (__instance == null || !Juntas.EmOk || Descarga.Ativa) return;
                var p = __instance.Pointer;
                if (p == Ecs.PtrTransform && Ecs.PtrTransform != IntPtr.Zero) Juntas.TorretaAntesTransform();
                else if (p == Ecs.PtrPhysicsPre && Ecs.PtrPhysicsPre != IntPtr.Zero) Longe.Passo(Juntas.EmAtual, ReplayPonte.EmReproducao());   // 0.19.13: regra dos 100 km (no lugar do SPActiveUpdateSystem do jogo)
                else if (p == Ecs.PtrPresentation && Ecs.PtrPresentation != IntPtr.Zero && Juntas.TorreSentada != null) Juntas.TorretaCameraLTW(true);
                else if ((p == Ecs.PtrTickPre || p == Ecs.PtrTickPost) && p != IntPtr.Zero) Radio.Copiar(Juntas.EmAtual, true);   // 0.19.6: LRT copia o sinal dentro do tick
            }
            catch (Exception ex) { if (erros++ < 3) Registro.Erro("prefix group OnUpdate: " + ex.Message); }
        }
        [HarmonyLib.HarmonyPostfix]
        static void Depois(ComponentSystemGroup __instance)
        {
            try
            {
                if (__instance == null || !Juntas.EmOk || Descarga.Ativa) return;
                if (__instance.Pointer == Ecs.PtrTransform && Ecs.PtrTransform != IntPtr.Zero) { Juntas.TorretaDepoisTransform(); Juntas.CapturarOlhoJogo(); try { Acoplador.EscreverRadar(Juntas.EmAtual); } catch (Exception ex) { if (erros++ < 3) Registro.Aviso("radar/write: " + ex.Message); } }
                else if (__instance.Pointer == Ecs.PtrGrupoBinds && Ecs.PtrGrupoBinds != IntPtr.Zero) Juntas.DistanciaBindsTorre();
                else if ((__instance.Pointer == Ecs.PtrTickPre || __instance.Pointer == Ecs.PtrTickPost) && __instance.Pointer != IntPtr.Zero) Radio.Copiar(Juntas.EmAtual, true);   // 0.19.6
            }
            catch (Exception ex) { if (erros++ < 3) Registro.Erro("postfix group OnUpdate: " + ex.Message); }
        }
    }

    /// <summary>0.11.3: ponte com o mod Replay (se estiver instalado): enquanto ele reproduz uma gravacao, a Mecanica nao roda o ciclo
    /// (le Replay.Estado.EmReproducao por reflexao, sem depender da DLL).</summary>
    internal static class ReplayPonte
    {
        static System.Reflection.FieldInfo campo; static float tTenta = 999f; static int tentativas;
        public static bool EmReproducao()
        {
            if (campo == null)
            {
                tTenta += UnityEngine.Time.unscaledDeltaTime; if (tTenta < 5f || tentativas >= 12) return false;
                tTenta = 0f; tentativas++;
                try
                {
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (a.GetName().Name != "Replay") continue;
                        var t = a.GetType("Replay.Estado"); campo = t?.GetField("EmReproducao");
                        if (campo != null) Registro.Info("Replay mod found: the mod's cycle pauses during playback");
                    }
                }
                catch (Exception) { }
                if (campo == null) return false;
            }
            try { return (bool)campo.GetValue(null); } catch (Exception) { return false; }
        }
    }

    /// <summary>MonoBehaviour injetado no jogo: chama o sistema de juntas todo frame.</summary>
    public class Ciclo : UnityEngine.MonoBehaviour
    {
        public Ciclo(IntPtr ptr) : base(ptr) { }
        static int erros = 0; static float tMundo = 0f; static World mundo;

        void Update()
        {
            try
            {
                Textos.Desfazer();
                try { Transicao.Passo(); } catch (Exception ex) { if (erros++ < 3) Registro.Aviso("transition log: " + ex.Message); }   // 0.19.12: fotos +1/+3/+6 s (antes de qualquer early-return)
                if (MecanicaPlugin.Ativo == null || !MecanicaPlugin.Ativo.Value) return;
                if (Descarga.Ativa) return;   // 01/10: o jogo esta descarregando o mundo — nada de ler o ECS
                if (Descarga.Pendente) { Descarga.Confirmar(); try { Juntas.EsquecerTudo(); Ecs.ReiniciarPonteiros(); } catch (Exception ex) { Registro.Aviso("after unload: " + ex.Message); } }
                if (mundo != null && mundo.IsCreated) { try { Marca.Passo(mundo.EntityManager); } catch (Exception ex) { if (erros++ < 5) Registro.Erro("mark: " + ex); } }
                Aba.Passo();
                float dt = UnityEngine.Time.deltaTime;
                try { Props.Passo(dt); } catch (Exception ex) { if (erros++ < 3) Registro.Aviso("properties: " + ex.Message); }   // 0.23.2
                if (mundo == null || !mundo.IsCreated)
                {
                    tMundo += dt; if (tMundo < 1f) return; tMundo = 0f;
                    var w = World.DefaultGameObjectInjectionWorld;
                    if (w == null || !w.IsCreated) return;
                    mundo = w; Registro.Info("World: " + w.Name);
                }
                if (ReplayPonte.EmReproducao())
                {
                    Juntas.BancoVisivelNaReproducao(mundo.EntityManager);   // 0.11.3: o mod Replay esta reproduzindo (simulacao congelada) — nao mexe em nada...
                    try { Longe.Passo(mundo.EntityManager, true); } catch (Exception ex) { if (erros++ < 3) Registro.Aviso("replay: far parts: " + ex.Message); }   // 0.19.13: religa o que o Recentrar fez o jogo desligar
                    try { Radio.Copiar(mundo.EntityManager, false); } catch (Exception ex) { if (erros++ < 3) Registro.Aviso("replay: long range copy: " + ex.Message); }   // 0.19.11 (Andre: "nao gravou a camera do LRT no replay"): ...so o LRT continua passando o sinal (o monitor mostra a camera ao vivo)
                    return;
                }
                // 0.12.6 (Andre: "o menu de BP ficou extremamente lagado"): as previas dos BPs viram entities no mundo (UIBlueprintSave.
                // CreateEntitiesFromBinary) e o mod achava/processava todos os blocos delas. Com o menu aberto (fora da simulacao) o ciclo pausa.
                if (MenuBP()) return;
                Ecs.PrepararPonteiros();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Juntas.Passo(mundo.EntityManager, dt);
                Medir(sw.Elapsed.TotalMilliseconds, dt);
                RadarIcone.Passo(dt); Juntas.T("radar icons — cycle end"); if (Juntas.Trilha > 0) Juntas.Trilha--;
            }
            catch (Exception ex) { if (erros++ < 5) Registro.Erro("Update: " + ex); }
        }
        // 0.12.6: tempo do ciclo — a cada 10 s, se a media passar de 2 ms (ou o pico de 20 ms), vai pro log com os numeros
        static double somaMs, picoMs; static int nMed; static float tMed;
        static void Medir(double ms, float dt)
        {
            somaMs += ms; nMed++; if (ms > picoMs) picoMs = ms; tMed += UnityEngine.Time.unscaledDeltaTime;
            if (tMed < 10f) return;
            double media = somaMs / Math.Max(1, nMed);
            if (media > 2.0 || picoMs > 20.0) Registro.Aviso("performance: mod cycle average " + media.ToString("F2") + " ms, peak " + picoMs.ToString("F1") + " ms (" + nMed + " frames) — " + Juntas.Contagens());
            somaMs = 0; picoMs = 0; nMed = 0; tMed = 0f;
        }
        static bool menuAberto; static float tMenu = 99f;
        static bool MenuBP()
        {
            tMenu += UnityEngine.Time.unscaledDeltaTime;
            if (tMenu >= 0.2f)
            {
                tMenu = 0f; bool aberto = false;
                try { var ui = UIManager._singleton; if (ui != null) { var bp = ui._blueprintMenu; aberto = bp != null && bp.gameObject.activeInHierarchy; } } catch (Exception) { }
                if (aberto != menuAberto) { menuAberto = aberto; Juntas.MenuBP = aberto; Registro.Info(aberto ? "blueprint menu OPEN: mod cycle paused" : "blueprint menu closed: mod cycle resumed"); if (!aberto) Juntas.Trilha = 8; }
            }
            return menuAberto;
        }
        void OnGUI() { try { Juntas.DesenharAjuste(); } catch (Exception ex) { if (erros++ < 5) Registro.Erro("OnGUI: " + ex); } }
        void LateUpdate() { try { if (Juntas.TorreSentada != null) Juntas.TorretaCameraLTW(false); } catch (Exception ex) { if (erros++ < 5) Registro.Erro("LateUpdate: " + ex); } }
        void OnApplicationQuit() { try { if (mundo != null && mundo.IsCreated) Juntas.Limpar(mundo.EntityManager); } catch (Exception) { } }
    }
}
