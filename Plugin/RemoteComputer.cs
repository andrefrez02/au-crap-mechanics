using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;

namespace CrapMechanics
{
    /// <summary>Remote control, delivery 1 (Andre: a computer the phone drives, rows like vanilla switches, and a channel STANDBY so two
    /// computers using the same wireless channels never fight). SC_RemoteComputer: a PC tower with a red power port, a main channel display
    /// (front) and 10 rows (back). Each row: a data output, a channel display, three switches and an LED.
    ///   DESTINATION switch: off = cable only (the row's display is blank and its channel 0); on = cable + wireless channel.
    ///   TYPE switch: off = button (on while the phone button is held); on = switch (each press toggles, the state stays).
    ///   VALUE switch: off = 0..1; on = x..y (off = x, on = y, both typed on the phone).
    /// Where the row's state lives, so the game's own systems keep it (research remoto_e1_pesquisa.md, section 2):
    ///   on/off = ActionableData._value.y of the row's TYPE switch (the game's click only writes .x; TimeRewind saves the whole float4;
    ///   the garage builds new entities, so the garage state is off, like a vanilla switch that was never pressed in the garage);
    ///   x / y = _value.z / .w of the row's VALUE switch (+ .y = 1 once they were set).
    ///   The switches and the channel displays (.x) are kept per block in knobs.txt (Knobs.Persist), like the mod's other knobs.
    /// Wireless (plan A, verified on the 21/11 build): a vanilla RECEIVING transmitter keeps its last value when nobody sends on its channel.
    /// So a "cable + channel" row does NOT occupy its channel all the time: it sends only during a short window around a command (a few
    /// ticks; a held button sends while held, then the off value, then nothing). The send imitates a vanilla sender, inside the game's tick
    /// (right after SCTick_WirelessTransmitter in SpaceshipComponentsTickPostGroup): same 2000 m range between transmitters, same rule
    /// (a receiver takes the value only when EXACTLY one sender is in its range; with 2+ it keeps its value and blinks red).
    /// Host / solo only: on a client the computer does nothing (its outputs are not written there; multiplayer is not tested in delivery 1).
    /// Round 0.31.35 (Andre: "the mod must not guess"): a 4th switch per row, DIRECTION: off = OUTPUT (everything above), on = INPUT.
    ///   The row ports are the game's in/out kind in the prefab (blue + yellow, DataInOut 3), so a cable of either direction can be plugged in
    ///   the garage. In flight the mod writes the port's _setupType and _runtimeType (+20, +21) from the switch: OUTPUT = DataOutput 1,
    ///   INPUT = DataInput 0 — a port with a fixed setup type is never re-resolved by the game (research r03135_remoto_pesquisa.md, section 1).
    ///   When the far end of the cable has no direction of its own (a vanilla Wireless Transmitter), the mod asks the game to resolve it again
    ///   with the game's own tag (SpaceshipElectricPortUpdateRuntimeEndEntity, the same one Signal.cs uses), main thread only.
    ///   An INPUT row never writes its port and never sends. Its value (shown on the phone, never used by the mod for anything else):
    ///     the wireless channel while EXACTLY one sender is on the row's channel within 2000 m (DESTINATION = cable + channel; vanilla senders
    ///     and other rows in a send window count, like a vanilla receiver); otherwise the cable, when it comes from an output; with 2+ senders,
    ///     or with no cable, the last channel value (a vanilla receiver keeps its value too).
    /// Names (0.31.35): each computer keeps a channel name and a name per row (Names.cs, names.txt, by SCGuid); the phone shows them unless it
    /// has its own. Set at the computer: aim at the front display (channel) or at a row's display / switches (row) and press E (Phone.cs).</summary>
    internal static class RemoteComputer
    {
        public const int Rows = 10;
        public static float PowerUse = 2f, RangeM = 2000f; public static int WindowTicks = 3;
        const float WirelessRangeSq = 4000000f;   // the game's MAX_RANGE 2000 m, distancesq < 4e6 (strict)
        // the row's switches are told apart by their order along block X: DESTINATION, TYPE, VALUE (-0.083, -0.035, 0.013) and, since the
        // 0.31.35 content, DIRECTION (0.061); the older content has three (-0.080, -0.025, 0.030) and its rows are outputs
        static float RowY(int n) => 0.6875f - 0.125f * n;
        static readonly float3 MainLedPos = new float3 { x = -0.150f, y = 0.465f, z = 0.3722f }, MainTextPos = new float3 { x = 0.025f, y = 0.465f, z = 0.3744f };
        const float RowLedX = 0.100f, RowLedXOld = 0.080f, RowLedZ = -0.3722f, RowTextX = -0.155f, RowTextZ = -0.3744f;
        // 0.31.36: "SC_RemoteComputer M" (the mirrored version, V in the garage). Its case is mirrored (ports, fans, side panel) but the
        // controls are NOT: each row keeps number, display, DESTINATION, TYPE, VALUE, DIRECTION, LED from left to right, only moved by
        // +0.123 along block X (models_v2.RC_M_DX) to sit beside the port column, which is on the other side; the front panel is the same.
        // So the switches' order along X means the same on both, and only the row display / digit text / LED positions get the offset.
        const float MirrorDX = 0.123f;

        internal sealed class Row
        {
            public int n; public Entity port, lever, dst, typ, val, led, text;
            public int ch; public bool dstOn, typSwitch, valXY;
            public bool on; public float x, y = 1f; public bool hasXY;
            public int ledWritten = -1, textShown = int.MinValue, hDst = -1, hTyp = -1, hVal = -1;
            public int window; public bool hold, sending, logStart; public int conflictTicks, deliveredMax, onTicks;
            public int result = -1; public float resultUntil, flashUntil; public int preview = Led.Yellow;
            public bool startSend;   // delivery 2: the garage start state is ON on a "cable + channel" row: sent once the computer has power
            // 0.31.35: DIRECTION switch (Entity.Null on the older content = output), the row's name set at the computer, and the input state
            public Entity dir; public bool input, dirSynced, farNoted; public int hDir = -1, wiring; public float tWire; public string name = "";
            /// <summary>INPUT row: what the phone shows. inFrom: 0 = no signal, 1 = cable, 2 = wireless channel (one sender), 3 = the last channel value.</summary>
            public float inValue; public int inFrom, inFromLogged = -1;
            /// <summary>INPUT row with a channel, written inside the game's tick: senders in range this tick, the single sender's value, the last one kept.</summary>
            public int chSenders; public float chValue, latched, chGreenUntil, chRedUntil, chFlashUntil; public bool hasLatched;
            public float Out(bool powered) => !powered ? 0f : on ? (valXY ? y : 1f) : (valXY ? x : 0f);
        }
        internal sealed class Comp
        {
            public Entity block; public Row[] rows = new Row[Rows];
            public Entity mainLever, mainLed, mainText; public int mainCh, mainShown = int.MinValue, mainLedWritten = -1;
            public bool controlsOk, inFlight, powered, poweredLogged, seeded, startApplied; public byte energy = 254;
            public float tFind = 9f, tRead = 9f, flashUntil; public int passes, warnings; public float3 pos; public string missing;
            // 0.31.35: names set at the computer (names.txt, by SCGuid) and the front display's hint
            public string guid = "", chName = ""; public int namesVersion = -1, hMain = int.MinValue, dirLogs, inLogs; public bool oldContent;
            public bool mirrored;   // 0.31.36: the block is "SC_RemoteComputer M" (read from its prefab every time the controls are looked for)
            public Comp() { for (int i = 0; i < Rows; i++) rows[i] = new Row { n = i + 1 }; }
        }
        /// <summary>Delivery 2: `taken` = the phone is in the player's hotbar (Hotbar.cs); the dock's phone is hidden by writing CRPLayerID = 31 on
        /// the toggle entity (renderer, toggle and click box are one entity: the click box stays, so the empty dock can still be aimed at).</summary>
        internal sealed class Dock { public Entity block, toggle, hiddenOn; public float tFind = 9f; public int hintState = -1; public bool taken, inFlight; public int savedLayer; }

        static readonly Dictionary<long, Comp> comps = new Dictionary<long, Comp>();
        static readonly Dictionary<long, Dock> docks = new Dictionary<long, Dock>();
        static TypeIndex tiData, tiToggle, tiText, tiMem, tiLayer, tiTag; static bool ok, failed, layerOk, tagOk;
        /// <summary>This machine runs the computers (solo or host). Written on the main thread every frame, read in the tick.</summary>
        public static bool Host;
        static ulong dockHash, compHash, compHashM;

        public static bool Has(long k) => comps.ContainsKey(k) || docks.ContainsKey(k);
        public static void Track(Entity e, bool dock, bool garage = false)
        {
            if (dock) { docks[Ecs.K(e)] = new Dock { block = e }; if (!garage) ModLog.Info("SC_PhoneDock found: block " + e.Index + ":" + e.Version); }
            else { comps[Ecs.K(e)] = new Comp { block = e }; if (!garage) ModLog.Info("SC_RemoteComputer found: block " + e.Index + ":" + e.Version); }
        }
        public static void Forget() { comps.Clear(); docks.Clear(); rxSaved.Clear(); prefixValid = false; inTx.Clear(); anyInputCh = false; lastTickSeen = -1; }
        public static IEnumerable<Comp> All => comps.Values;

        static bool Prepare()
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiData = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableData>());
                tiToggle = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableToggle>());
                tiText = TypeManager.GetTypeIndex(Il2CppType.Of<CRPText3DString>());
                tiMem = TypeManager.GetTypeIndex(Il2CppType.Of<CRPRendererMemory_Color>());
                try { tiLayer = TypeManager.GetTypeIndex(Il2CppType.Of<CRPLayerID>()); layerOk = true; } catch (Exception ex) { ModLog.Warn("remote computer: CRPLayerID not found (" + ex.Message + ") — a taken phone stays visible on its dock"); }
                // 0.31.35: the game's "resolve this port's direction again" tag (Signal.cs uses the same one)
                try { tiTag = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipElectricPortUpdateRuntimeEndEntity>()); tagOk = tiTag.Value != 0; } catch (Exception) { tagOk = false; }
                if (!tagOk) ModLog.Warn("remote computer: the game's port tag (SpaceshipElectricPortUpdateRuntimeEndEntity) was not found — a Wireless Transmitter cabled to a row gets its direction written by the mod instead of resolved by the game");
                dockHash = Ecs.HashPrefab("SC_PhoneDock"); compHash = Ecs.HashPrefab("SC_RemoteComputer"); compHashM = Ecs.HashPrefab("SC_RemoteComputer M");
                if (compHashM == compHash) compHashM = 0;
                Knobs.Ready(); Led.Ready();
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Error("remote computer: types: " + ex.Message); return false; }
        }

        internal static string Text(string key, string defaultValue)
        {
            try { var loc = Localization._cachedInstance; if (loc != null && loc._map != null && loc._map.ContainsKey(key)) return loc._map[key]; } catch (Exception) { }
            return defaultValue;
        }

        // ------------------------------------------------------------------ main thread, every frame
        static float tPreview, tGarage = 9f;
        /// <summary>Delivery 2: physics tick (JointSystem.CurrentTick) and time when the current flight started — the garage start state is applied
        /// only in the first 30 s of a flight (the game saves its first rewind point 60 s after the launch), never later.</summary>
        public static int FlightStartTick = -1; public static float FlightStartTime = -999f; static bool wasFlight;
        public const int StartWindowTicks = 1800; public const float StartWindowS = 30f;
        static bool InStartWindow()
        {
            int t = JointSystem.CurrentTick;
            // review: a tick BELOW the flight's start tick (the counter was reset under us) is never "in the window" — the start state is then
            // not applied rather than applied late over a state the player (or a rewind) already set
            if (t >= 0 && FlightStartTick >= 0) { int d = t - FlightStartTick; return d >= 0 && d < StartWindowTicks; }
            return UnityEngine.Time.unscaledTime - FlightStartTime < StartWindowS;
        }
        public static void Step(EntityManager em, float dt)
        {
            bool flight; try { flight = Ecs.Ok && Ecs.QSing.CalculateEntityCountWithoutFiltering() > 0; } catch (Exception) { flight = false; }
            if (flight && !wasFlight) { FlightStartTick = JointSystem.CurrentTick; FlightStartTime = UnityEngine.Time.unscaledTime; lastTickSeen = -1; }
            wasFlight = flight;
            // 0.31.35: a time rewind (the physics tick went back): the game tags every port again and may rebuild blocks — each row tells the game
            // its direction again, and the kept channel values (display only) are dropped
            int tickNow = flight ? JointSystem.CurrentTick : -1;
            if (tickNow >= 0 && lastTickSeen >= 0 && tickNow < lastTickSeen - 5)
            {
                int n = 0;
                foreach (var c in comps.Values) foreach (var r in c.rows) { r.dirSynced = false; r.hasLatched = false; r.chSenders = 0; if (!Ecs.IsNull(r.dir)) n++; }
                if (n > 0) ModLog.Info("remote computer: time rewind (tick " + lastTickSeen + " -> " + tickNow + ") — the row directions are told to the game again, kept channel values dropped");
            }
            lastTickSeen = tickNow;
            // the garage's blocks (GarageTransform) are not in the launched set JointSystem.Discover reads: found here, so the switches and
            // displays set while building are shown and kept in knobs.txt
            tGarage += dt;
            if (tGarage >= 1f)
            {
                tGarage = 0f;
                try
                {
                    if (Prepare() && JointSystem.HashesOk)
                        foreach (var (e, h) in Garage.Set(em).mod)
                            if ((h == compHash || h == compHashM || h == dockHash) && h != 0 && !Has(Ecs.K(e)) && Ecs.Alive(em, e)) Track(e, h == dockHash, true);   // 0.31.36: + the mirrored computer
                }
                catch (Exception ex) { if (previewWarn++ < 3) ModLog.Warn("remote computer: garage scan: " + ex.Message); }
            }
            if (comps.Count == 0 && docks.Count == 0) return;
            if (!Prepare()) return;
            Host = Multi.Authoritative(em);
            float now = Ecs.Now;
            var dead = new List<long>();
            foreach (var kv in comps)
            {
                var c = kv.Value;
                try
                {
                    if (!Ecs.Alive(em, c.block)) { dead.Add(kv.Key); continue; }
                    CompStep(em, c, dt, now);
                }
                catch (Exception ex) { if (c.warnings++ < 5) ModLog.Warn("SC_RemoteComputer " + c.block.Index + ": " + ex.Message); }
            }
            foreach (var k in dead) comps.Remove(k);
            dead.Clear();
            foreach (var kv in docks)
            {
                var d = kv.Value;
                try { if (!Ecs.Alive(em, d.block)) { dead.Add(kv.Key); continue; } DockStep(em, d, dt); }
                catch (Exception) { }
            }
            foreach (var k in dead) docks.Remove(k);
            tPreview += dt; if (tPreview >= 0.5f) { tPreview = 0f; try { Preview(em); } catch (Exception ex) { if (previewWarn++ < 3) ModLog.Warn("remote computer: wireless preview: " + ex.Message); } }
        }
        static int previewWarn, lastTickSeen = -1;

        static void CompStep(EntityManager em, Comp c, float dt, float now)
        {
            c.inFlight = Ecs.IsPart(em, Ecs.Parent(em, c.block));
            c.tFind += dt;
            if (!c.controlsOk ? c.tFind >= 1f : (c.tFind >= 3f && !ControlsAlive(em, c))) { c.tFind = 0f; FindControls(em, c); }
            else if (c.tFind >= 3f) c.tFind = 0f;
            try { c.pos = Ecs.Position(em, c.block); } catch (Exception) { }
            c.tRead += dt;
            if (c.tRead >= 0.1f) { c.tRead = 0f; ReadControls(em, c); }
            // power (like the beacon): consumption written only when it changes; supplied = _suppliedState 2
            Ecs.SetConsumption(em, c.block, PowerUse);
            byte st = 255; bool powered = PowerUse <= 0f || Ecs.HasPower(em, c.block, out st);
            if (!c.inFlight) powered = false;
            if (c.inFlight && (powered != c.powered || !c.poweredLogged))
            {
                c.poweredLogged = true;
                ModLog.Info("SC_RemoteComputer " + c.block.Index + ": " + (powered ? "powered (" + JointSystem.StateName(st) + "), listening on channel " + c.mainCh : "NO power (" + JointSystem.StateName(st) + ") — outputs 0, nothing sent, LEDs off"));
            }
            if (!powered && c.powered) foreach (var r in c.rows) { r.window = 0; r.hold = false; r.chSenders = 0; }
            c.powered = powered;
            // delivery 2: a "cable + channel" row that starts ON (garage start state) tells its receivers once the computer has power
            if (c.inFlight && Host && powered)
                foreach (var r in c.rows)
                    if (r.startSend) { r.startSend = false; if (r.dstOn && r.on && !r.input && InStartWindow()) { r.window = WindowTicks; r.logStart = true; r.conflictTicks = 0; r.deliveredMax = 0; } }
            if (c.inFlight && Host) Leds(em, c, now);
            if (Ecs.PtrTickPost == IntPtr.Zero && Host && c.inFlight) WritePorts(em, c);   // fallback when the tick hook is missing
        }

        static bool ControlsAlive(EntityManager em, Comp c)
        {
            if (!Ecs.Alive(em, c.mainLever)) return false;
            foreach (var r in c.rows) if (!Ecs.Alive(em, r.typ) || !Ecs.Alive(em, r.port)) return false;
            // review 22/11: in the garage an actionable can be swapped for a new entity while the old one stays alive (Knobs.Step's note,
            // 0.19.3): a control that no longer belongs to this block = look again (else a switch set in the garage would not reach knobs.txt)
            if (Belongs(em, c.mainLever, c.block) == 0 || Belongs(em, c.rows[0].typ, c.block) == 0 || Belongs(em, c.rows[Rows - 1].val, c.block) == 0) return false;
            return true;
        }

        /// <summary>1 = the actionable's BelongingSC is this block, 0 = another block, -1 = unknown (no BelongingSC).</summary>
        static int Belongs(EntityManager em, Entity a, Entity block)
        {
            try { var b = BeaconIcon.BlockOfActionable(em, a); if (Ecs.IsNull(b)) return -1; return b.Index == block.Index && b.Version == block.Version ? 1 : 0; }
            catch (Exception) { return -1; }
        }
        /// <summary>The controls are told apart by their position in the block (no GameObject names at runtime): naming contract in
        /// revisao/remoto_e1_conteudo.md. Actionables by world position projected on the block's axes (in the garage they may not be in the
        /// block's Child buffer, only in BelongingSC); renderers and texts by LocalTransform (they are the block's children).</summary>
        static void FindControls(EntityManager em, Comp c)
        {
            var acts = new List<Entity>(); var seen = new HashSet<long>();
            foreach (var f in Ecs.Children(em, c.block)) if (Ecs.HasC(em, f, tiData) && seen.Add(Ecs.K(f))) acts.Add(f);
            if (acts.Count < 51) foreach (var f in BeaconIcon.ActionablesOf(em, c.block)) if (seen.Add(Ecs.K(f))) acts.Add(f);
            Ecs.Axes(em, c.block, out var ex, out var ey, out var ez); var bp = Ecs.Position(em, c.block);
            // 0.31.36: the mirrored version has its row displays, digit texts and LEDs MirrorDX further along X (the front panel is the same)
            bool mirrored = false; try { mirrored = compHashM != 0 && Ecs.Prefab(em, c.block) == compHashM; } catch (Exception) { }
            if (mirrored != c.mirrored) { c.mirrored = mirrored; c.controlsOk = false; }
            float rowTextX = RowTextX + (mirrored ? MirrorDX : 0f), rowLedX = RowLedX + (mirrored ? MirrorDX : 0f);
            c.mainLever = Entity.Null; c.mainLed = Entity.Null; c.mainText = Entity.Null;
            foreach (var r in c.rows) { r.port = r.lever = r.dst = r.typ = r.val = r.dir = r.led = r.text = Entity.Null; r.ledWritten = -1; r.textShown = int.MinValue; r.hDst = r.hTyp = r.hVal = r.hDir = -1; r.dirSynced = false; }
            c.mainShown = int.MinValue; c.mainLedWritten = -1; c.hMain = int.MinValue;
            int nActs = 0; var toggles = new List<(Entity a, float lx)>[Rows];
            foreach (var a in acts)
            {
                float3 d; try { d = Ecs.Sub(Ecs.Position(em, a), bp); } catch (Exception) { continue; }
                float lx = Ecs.Dot(d, ex), ly = Ecs.Dot(d, ey), lz = Ecs.Dot(d, ez);
                bool toggle = Ecs.HasC(em, a, tiToggle);
                if (lz > 0f) { if (!toggle && Math.Abs(ly - 0.465f) < 0.05f) { c.mainLever = a; nActs++; } continue; }
                int n = (int)Math.Round((0.6875f - ly) / 0.125f); if (n < 1 || n > Rows || Math.Abs(ly - RowY(n)) > 0.04f) continue;
                var r = c.rows[n - 1];
                if (!toggle) { if (Math.Abs(lx - rowTextX) < 0.03f) { r.lever = a; nActs++; } continue; }
                (toggles[n - 1] ?? (toggles[n - 1] = new List<(Entity, float)>())).Add((a, lx));
            }
            // 0.31.35: a row's switches in their order along X — 4 = DESTINATION, TYPE, VALUE, DIRECTION; 3 = the older content (no DIRECTION)
            int rows4 = 0, rows3 = 0;
            for (int i = 0; i < Rows; i++)
            {
                var l = toggles[i]; if (l == null || (l.Count != 3 && l.Count != 4)) continue;
                l.Sort((p, q) => p.lx.CompareTo(q.lx));
                var r = c.rows[i]; r.dst = l[0].a; r.typ = l[1].a; r.val = l[2].a; nActs += l.Count;
                if (l.Count == 4) { r.dir = l[3].a; rows4++; } else rows3++;
            }
            int nLed = 0, nText = 0;
            foreach (var f in Ecs.Children(em, c.block))
            {
                float3 p; try { p = Ecs.LT(em, f).Position; } catch (Exception) { continue; }
                if (Ecs.HasC(em, f, tiText))
                {
                    if (Near(p, MainTextPos, 0.01f)) { c.mainText = f; nText++; continue; }
                    if (Math.Abs(p.x - rowTextX) > 0.01f || Math.Abs(p.z - RowTextZ) > 0.01f) continue;
                    int n = (int)Math.Round((0.6875f - p.y) / 0.125f); if (n < 1 || n > Rows || Math.Abs(p.y - RowY(n)) > 0.01f) continue;
                    c.rows[n - 1].text = f; nText++; continue;
                }
                if (!Ecs.HasC(em, f, tiMem)) continue;
                if (Near(p, MainLedPos, 0.006f)) { c.mainLed = f; nLed++; continue; }
                if ((Math.Abs(p.x - rowLedX) > 0.006f && (mirrored || Math.Abs(p.x - RowLedXOld) > 0.006f)) || Math.Abs(p.z - RowLedZ) > 0.006f) continue;
                int m = (int)Math.Round((0.6875f - p.y) / 0.125f); if (m < 1 || m > Rows || Math.Abs(p.y - RowY(m)) > 0.006f) continue;
                c.rows[m - 1].led = f; nLed++;
            }
            int nPorts = 0;
            foreach (var p in Ecs.Ports(em, c.block))
            {
                int i = Ecs.PortIndex(em, p);
                if (i >= 1 && i <= Rows) { c.rows[i - 1].port = p; nPorts++; }
            }
            bool oldContent = rows3 == Rows && nActs == 41;
            bool complete = nPorts == Rows && ((rows4 == Rows && nActs == 51) || oldContent);
            string missing = "actionables " + nActs + (oldContent ? "/41 (main display, 10 row displays, 30 switches)" : "/51 (main display, 10 row displays, 40 switches)") + ", LEDs " + nLed + "/11, digit texts " + nText + "/11, row ports " + nPorts + "/10";
            if (complete && !c.controlsOk) ModLog.Info("SC_RemoteComputer " + c.block.Index + ": controls found — " + missing + (mirrored ? "; this is the MIRRORED version (SC_RemoteComputer M): ports on the other side, controls in the same order" : "") + (c.inFlight ? " (in flight)" : " (garage)"));
            else if (!complete && c.missing != missing && c.inFlight) ModLog.Warn("SC_RemoteComputer " + c.block.Index + ": controls incomplete — " + missing + " (rebuild the bundle with the 0.31.35 content?)");
            if (complete && oldContent && !c.oldContent) ModLog.Warn("SC_RemoteComputer " + c.block.Index + ": this is the older content (3 switches per row, no DIRECTION switch) — every row works as an OUTPUT; rebuild the bundle with the 0.31.35 content for input rows");
            c.missing = missing; c.controlsOk = complete; c.oldContent = complete && oldContent;
        }
        static bool Near(float3 a, float3 b, float tol) => Math.Abs(a.x - b.x) < tol && Math.Abs(a.y - b.y) < tol && Math.Abs(a.z - b.z) < tol;

        static unsafe float* Data(EntityManager em, Entity a, bool rw)
        {
            if (!Ecs.HasC(em, a, tiData)) return null;
            return rw ? (float*)em.GetComponentDataRawRW(a, tiData) : (float*)Ecs.RO(em, a, tiData);
        }
        static int ChannelOf(float x) => float.IsNaN(x) ? 0 : Math.Max(0, Math.Min(99, (int)(x * 100f)));

        /// <summary>10 times a second: switches, displays, state (rewind changes it under us), knobs.txt, digits and hints.</summary>
        static unsafe void ReadControls(EntityManager em, Comp c)
        {
            c.passes++;
            if (Ecs.Alive(em, c.mainLever))
            {
                var p = Data(em, c.mainLever, false);
                if (p != null) { float v = Knobs.Persist(em, c.mainLever, c.block, p[0], "main"); int ch = ChannelOf(v); if (ch != c.mainCh && c.mainShown != int.MinValue) ModLog.Info("SC_RemoteComputer " + c.block.Index + ": main channel " + c.mainCh + " -> " + ch); c.mainCh = ch; }
            }
            WriteText(em, c.mainText, c.mainCh, ref c.mainShown);
            // 0.31.35: names set at the computer (host / solo), and the "E: name" note on the hints
            string sfx = "";
            if (Host) { RefreshNames(em, c); sfx = Text("SC_RemoteComputer_HintRename", " (E: name)"); }
            // the front display's hint (the 0.31.35 content lets the mod write it; the older one shows the game's own number): channel, name, "E: name"
            int hm = c.mainCh | ((c.namesVersion & 0x3FFF) << 8) | (Host ? 1 << 24 : 0);
            if (hm != c.hMain && Ecs.Alive(em, c.mainLever))
            { c.hMain = hm; Knobs.Hint(em, c.mainLever, Text("SC_RemoteComputer_MainChannel", "Computer channel: {0}").Replace("{0}", c.mainCh.ToString()) + (c.chName.Length > 0 ? " - " + c.chName : "") + sfx); }
            float now = Ecs.Now;
            foreach (var r in c.rows)
            {
                string n = r.n.ToString();
                bool dst = Switch(em, c, r.dst, "r" + n + "dst"), typ = Switch(em, c, r.typ, "r" + n + "typ"), val = Switch(em, c, r.val, "r" + n + "val");
                bool dirIn = !Ecs.IsNull(r.dir) && Switch(em, c, r.dir, "r" + n + "dir");
                if (dirIn != r.input)
                {
                    // the DIRECTION switch changed (or was just read after the launch): an input row never sends; its channel state starts clean
                    r.input = dirIn; r.window = 0; r.hold = false; r.startSend = false; r.dirSynced = false; r.hasLatched = false; r.chSenders = 0; r.inFrom = 0; r.inFromLogged = -1; r.wiring = 0;
                    if (c.inFlight && Host) ModLog.Info("SC_RemoteComputer " + c.block.Index + " row " + n + ": DIRECTION = " + (dirIn ? "INPUT (the phone shows what comes in; nothing is sent)" : "OUTPUT (phone button " + n + ")"));
                }
                if (r.dstOn && !dst) { r.window = 0; r.hold = false; r.hasLatched = false; r.chSenders = 0; }
                r.dstOn = dst; r.typSwitch = typ; r.valXY = val;
                if (Ecs.Alive(em, r.lever))
                {
                    var p = Data(em, r.lever, false);
                    if (p != null)
                    {
                        float v = Knobs.Persist(em, r.lever, c.block, p[0], "r" + n + "ch");
                        // cable only: the display is off and the row's channel is 0 (Andre's spec); the lever is held at 0
                        if (!dst && Math.Abs(v) > 0.0001f && Host) { var w = Data(em, r.lever, true); if (w != null) { w[0] = 0f; v = Knobs.Persist(em, r.lever, c.block, 0f, "r" + n + "ch"); } }
                        r.ch = dst ? ChannelOf(v) : 0;
                    }
                }
                WriteText(em, r.text, dst ? r.ch : -1, ref r.textShown);
                // state: TYPE .y = on; VALUE .y = 1 when x/y were set, .z = x, .w = y
                if (c.inFlight) { if (Ecs.Alive(em, r.typ)) { var p = Data(em, r.typ, false); if (p != null) r.on = !(p[1] < 0.5f); } }
                else r.on = typ && !r.input && GarageStart(em, c, r.n);   // delivery 2: in the garage the row shows the start state kept in knobs.txt
                if (Ecs.Alive(em, r.val)) { var p = Data(em, r.val, false); if (p != null) { r.hasXY = !(p[1] < 0.5f); if (r.hasXY) { r.x = p[2]; r.y = p[3]; } } }
                // 0.31.35: the row tells the game its direction (flight, host), then its hints
                if (c.inFlight && Host && c.controlsOk && !Ecs.IsNull(r.dir))
                {
                    SyncDirection(em, c, r, now);
                    if (r.input && c.powered) { RefreshInput(em, r); LogInput(c, r); }
                }
                int sf = sfx.Length > 0 ? 8 : 0;
                if (r.input)
                {
                    Hint(em, r.dst, (dst ? 1 : 0) | 2 | sf, ref r.hDst, dst ? "SC_RemoteComputer_DestIn1" : "SC_RemoteComputer_DestIn0", dst ? "Row {0} receives: cable + wireless channel" : "Row {0} receives: cable only", n, sfx);
                    Hint(em, r.typ, 2 | sf, ref r.hTyp, "SC_RemoteComputer_UnusedIn", "Row {0} is an input: switch not used", n, sfx);
                    Hint(em, r.val, 2 | sf, ref r.hVal, "SC_RemoteComputer_UnusedIn", "Row {0} is an input: switch not used", n, sfx);
                }
                else
                {
                    Hint(em, r.dst, (dst ? 1 : 0) | sf, ref r.hDst, dst ? "SC_RemoteComputer_Dest1" : "SC_RemoteComputer_Dest0", dst ? "Row {0} sends: cable + wireless channel" : "Row {0} sends: cable only", n, sfx);
                    Hint(em, r.typ, (typ ? 1 : 0) | sf, ref r.hTyp, typ ? "SC_RemoteComputer_Type1" : "SC_RemoteComputer_Type0", typ ? "Row {0}: switch (each press toggles)" : "Row {0}: button (on while held)", n, sfx);
                    Hint(em, r.val, (val ? 1 : 0) | sf, ref r.hVal, val ? "SC_RemoteComputer_Value1" : "SC_RemoteComputer_Value0", val ? "Row {0} value: off x, on y (set on phone)" : "Row {0} value: off 0, on 1", n, sfx);
                }
                if (!Ecs.IsNull(r.dir))
                {
                    // the DIRECTION switch's hint also carries the row's name (set at the computer) or the wrong-cable warning
                    int hd = (r.input ? 1 : 0) | (r.wiring == 1 ? 2 : 0) | sf | ((c.namesVersion & 0x3FFF) << 4);
                    if (hd != r.hDir && Ecs.Alive(em, r.dir))
                    {
                        r.hDir = hd;
                        string t = r.wiring == 1 ? (r.input ? Text("SC_RemoteComputer_DirWrong1", "Row {0}: INPUT, but the cable ends at an input") : Text("SC_RemoteComputer_DirWrong0", "Row {0}: OUTPUT, but the cable ends at an output"))
                                                 : (r.input ? Text("SC_RemoteComputer_Dir1", "Row {0}: INPUT") : Text("SC_RemoteComputer_Dir0", "Row {0}: OUTPUT")) + (r.name.Length > 0 ? " - " + r.name : "");
                        Knobs.Hint(em, r.dir, t.Replace("{0}", n) + sfx);
                    }
                }
            }
            // x / y not received yet on this launch: seeded from the phone's memory for this channel (same machine; the phone is the one
            // that programs the computer). Once, 0.3 s after the computer was seen in flight (the main channel restored from knobs.txt first).
            if (c.inFlight && Host && !c.seeded && c.passes >= 3 && c.controlsOk)
            {
                c.seeded = true; var seededRows = new List<string>();
                foreach (var r in c.rows)
                {
                    if (r.hasXY) continue;
                    var (x, y) = Phone.Memory.XY(c.mainCh, r.n);
                    if (WriteXY(em, r, x, y) && (x != 0f || y != 1f)) seededRows.Add(r.n + " (x " + Phone.Num(x) + ", y " + Phone.Num(y) + ")");
                }
                if (seededRows.Count > 0) ModLog.Info("SC_RemoteComputer " + c.block.Index + ": x..y values from the phone's memory for channel " + c.mainCh + ": row " + string.Join(", row ", seededRows));
            }
            if (c.inFlight && Host && c.controlsOk && c.passes >= 2 && !c.startApplied) ApplyStart(em, c);
        }
        /// <summary>Delivery 2: the start state of a switch-type row, set on the phone in the garage (knobs.txt "{guid}|r{n}on"; nothing stored = OFF).</summary>
        static bool GarageStart(EntityManager em, Comp c, int n) => Knobs.StoredValue(em, c.block, "r" + n + "on", out var v) && !(v < 0.5f);
        /// <summary>Delivery 2: once per flight, right after launch (the TYPE switches already restored from knobs.txt on the 1st pass): each switch-type
        /// row takes its garage start state (TYPE _value.y); a button row starts OFF. Only in the first 30 s of the flight: a computer found later
        /// (or after a rewind) keeps the state it has, as in delivery 1.</summary>
        static void ApplyStart(EntityManager em, Comp c)
        {
            c.startApplied = true;
            if (!InStartWindow()) { ModLog.Info("SC_RemoteComputer " + c.block.Index + ": seen more than " + StartWindowS.ToString("F0") + " s after the launch — the garage start state is not applied (rows keep their state)"); return; }
            var onRows = new List<string>();
            foreach (var r in c.rows)
            {
                if (r.input) continue;   // 0.31.35: an input row has no state
                bool on = r.typSwitch && GarageStart(em, c, r.n);
                if (on == r.on) { if (on && r.dstOn) r.startSend = true; if (on) onRows.Add(r.n.ToString()); continue; }
                if (on) r.onTicks = 0;
                WriteState(em, r, on);
                if (on) { onRows.Add(r.n.ToString()); if (r.dstOn) r.startSend = true; }
            }
            ModLog.Info("SC_RemoteComputer " + c.block.Index + ": garage start state applied — " + (onRows.Count == 0 ? "every row OFF" : "row " + string.Join(", ", onRows) + " ON, the rest OFF"));
        }
        static unsafe bool Switch(EntityManager em, Comp c, Entity sw, string suffix)
        {
            if (!Ecs.Alive(em, sw)) return false;
            var p = Data(em, sw, false); if (p == null) return false;
            float v = Knobs.Persist(em, sw, c.block, p[0], suffix);
            return !(v < 0.5f);
        }
        static void Hint(EntityManager em, Entity sw, int state, ref int shown, string key, string def, string n, string sfx = "")
        {
            if (state == shown || !Ecs.Alive(em, sw)) return;
            shown = state; Knobs.Hint(em, sw, Text(key, def).Replace("{0}", n) + sfx);
        }

        // ------------------------------------------------------------------ 0.31.35: names set at the computer
        static void RefreshNames(EntityManager em, Comp c)
        {
            if (c.guid.Length == 0) { try { c.guid = Ecs.Guid(em, c.block) ?? ""; } catch (Exception) { } if (c.guid.Length == 0) return; }
            if (c.namesVersion == Names.Version) return;
            c.chName = Names.Get(c.guid, "ch");
            foreach (var r in c.rows) r.name = Names.Get(c.guid, "r" + r.n);
            c.namesVersion = Names.Version;
        }
        static bool Same(Entity a, Entity b) => a.Index == b.Index && a.Version == b.Version && !Ecs.IsNull(a);
        /// <summary>Is the game's pointer target a control of a Remote Computer? row = 0 for the front display (the channel name), 1..10 for a
        /// row's display or switches (the row's name).</summary>
        public static bool RenameTarget(EntityManager em, Entity target, out Comp comp, out int row)
        {
            comp = null; row = -1;
            if (Ecs.IsNull(target) || comps.Count == 0) return false;
            foreach (var c in comps.Values)
            {
                if (!c.controlsOk || !Ecs.Alive(em, c.block)) continue;
                if (Same(c.mainLever, target)) { comp = c; row = 0; return true; }
                foreach (var r in c.rows) if (Same(r.lever, target) || Same(r.dst, target) || Same(r.typ, target) || Same(r.val, target) || Same(r.dir, target)) { comp = c; row = r.n; return true; }
            }
            return false;
        }
        /// <summary>The name this computer has for its channel (row 0) or for a row (1..10); "" = none.</summary>
        public static string NameOf(Comp c, int row) => c == null ? "" : row <= 0 ? c.chName : c.rows[row - 1].name;
        /// <summary>Sets (or, with an empty text, removes) a name at the computer: names.txt, by the block's SCGuid. Host / solo only.</summary>
        public static bool SetName(EntityManager em, Comp c, int row, string name)
        {
            if (c == null || !Host || !Ecs.Alive(em, c.block)) return false;
            RefreshNames(em, c);
            if (c.guid.Length == 0) { ModLog.Warn("SC_RemoteComputer " + c.block.Index + ": the block has no SCGuid yet — the name was not saved"); return false; }
            if (!Names.Set(c.guid, row <= 0 ? "ch" : "r" + row, name)) return false;
            RefreshNames(em, c);
            return true;
        }

        // ------------------------------------------------------------------ 0.31.35: DIRECTION (main thread, flight, host)
        static void Tag(EntityManager em, Entity e)
        {
            try { if (Ecs.Alive(em, e) && !Ecs.Has(em, e, tiTag)) Ecs.Add(em, e, ComponentType.ReadWrite(tiTag)); }
            catch (Exception ex) { if (tagWarn++ < 3) ModLog.Warn("remote computer: could not tag a port for the game: " + ex.Message); }
        }
        static int tagWarn;
        /// <summary>10 times a second per row with a DIRECTION switch: the row's port carries the switch's direction as its FIXED type (setup and
        /// runtime), so the game treats it like any block's input or output. When it was just written (launch, the switch moved, a rewind
        /// rebuilt the block) the port at the far end of the cable is tagged for the game to resolve again, if it has no direction of its own.
        /// Then the wiring is checked (CheckWiring): 0.6 s after a change, then every 5 s (every 2 s while it is wrong).</summary>
        static unsafe void SyncDirection(EntityManager em, Comp c, Row r, float now)
        {
            if (!Ecs.Alive(em, r.port) || !Ecs.HasC(em, r.port, Ecs.tiPort)) return;
            var b = (byte*)Ecs.RO(em, r.port, Ecs.tiPort); if (b == null) return;
            byte want = r.input ? Ecs.PortInput : Ecs.PortOutput, hadSetup = b[20], hadRun = b[21];
            if (hadSetup != want || hadRun != want)
            {
                var w = (byte*)em.GetComponentDataRawRW(r.port, Ecs.tiPort); if (w == null) return;
                w[20] = want; w[21] = want; if (r.input) *(float*)(w + 8) = 0f;
                r.dirSynced = false;
                if (c.dirLogs++ < 60) ModLog.Info("SC_RemoteComputer " + c.block.Index + " row " + r.n + ": data port " + r.port.Index + " is now a fixed " + (r.input ? "INPUT" : "OUTPUT") + " (it was setup " + hadSetup + ", runtime " + hadRun + "; 0 = input, 1 = output, 3 = in/out)");
            }
            if (!r.dirSynced)
            {
                r.dirSynced = true; r.tWire = now + 0.6f; r.farNoted = false;
                Ecs.ForgetPath(r.port);
                var far = Ecs.Source(em, r.port, out _);
                if (tagOk && !Ecs.IsNull(far) && Ecs.HasC(em, far, Ecs.tiPort))
                {
                    byte fs = ((byte*)Ecs.RO(em, far, Ecs.tiPort))[20];
                    if (fs == 3 || fs == 5)
                    {
                        // structural change (a tag), main thread, outside the tick: the game resolves the far port from this row's fixed type
                        Tag(em, far); Tag(em, r.port);
                        if (c.dirLogs++ < 60) ModLog.Info("SC_RemoteComputer " + c.block.Index + " row " + r.n + ": the port at the far end of the cable (" + far.Index + ", setup " + fs + ") has no direction of its own — asked the game to resolve it from this row");
                    }
                }
                return;
            }
            if (now >= r.tWire) { r.tWire = now + (r.wiring == 1 ? 2f : 5f); CheckWiring(em, c, r); }
        }
        /// <summary>Every few seconds per row: an OUTPUT row whose cable ends at an output, or an INPUT row whose cable ends at
        /// an input, passes nothing — the DIRECTION switch's hint says so and the log warns once. And a far end of the in/out kind (a vanilla
        /// Wireless Transmitter) that the game did not resolve (the cable crosses a joint of the mod: the game only walks inside one part)
        /// gets its runtime type written, the one byte Signal.Aim also writes.</summary>
        static unsafe void CheckWiring(EntityManager em, Comp c, Row r)
        {
            int wiring = 0;
            if (Ecs.RawPortPub(em, r.port, out _, out _, out _, out var persist) && !Ecs.IsNull(persist))
            {
                var far = Ecs.Source(em, r.port, out _);
                if (!Ecs.IsNull(far) && Ecs.HasC(em, far, Ecs.tiPort))
                {
                    var fb = (byte*)Ecs.RO(em, far, Ecs.tiPort);
                    if (fb != null)
                    {
                        byte fs = fb[20], fr = fb[21], wantFar = r.input ? Ecs.PortOutput : Ecs.PortInput;
                        if (fs == 3 && fr != wantFar)
                        {
                            var fw = (byte*)em.GetComponentDataRawRW(far, Ecs.tiPort);
                            if (fw != null)
                            {
                                fw[21] = wantFar;
                                if (!r.farNoted) { r.farNoted = true; ModLog.Info("SC_RemoteComputer " + c.block.Index + " row " + r.n + ": the in/out port at the far end (" + far.Index + ") was still " + (fr == 3 ? "without a direction" : "the wrong way (" + fr + ")") + " — set to " + (wantFar == 0 ? "input (it sends what this row outputs)" : "output (it feeds this row)")); }
                            }
                        }
                        if (Ecs.EffectiveType(em, far) == (r.input ? Ecs.PortInput : Ecs.PortOutput)) wiring = 1;
                    }
                }
            }
            if (wiring == r.wiring) return;
            r.wiring = wiring;
            if (wiring == 1) ModLog.Warn("SC_RemoteComputer " + c.block.Index + " row " + r.n + ": DIRECTION is " + (r.input ? "INPUT but its cable ends at an INPUT" : "OUTPUT but its cable ends at an OUTPUT") + " — nothing passes; flip the row's DIRECTION switch (or rewire)");
            else ModLog.Info("SC_RemoteComputer " + c.block.Index + " row " + r.n + ": the cable now matches the row's DIRECTION");
        }
        /// <summary>INPUT row: the value the phone shows (main thread; the cable path is cached, the value is read live).
        /// RULE (Andre's answer A; one place to change it): the wireless channel while exactly one sender is on it in range; with 2+ senders the
        /// last channel value (or the cable when there is none yet); with no sender the cable, when it is plugged and comes from an output;
        /// otherwise the last channel value; otherwise nothing.</summary>
        public static unsafe void RefreshInput(EntityManager em, Row r)
        {
            bool cable = false; float cv = 0f;
            try
            {
                if (Ecs.Alive(em, r.port) && Ecs.RawPortPub(em, r.port, out _, out _, out _, out var persist) && !Ecs.IsNull(persist)
                    && Ecs.ReadInput(em, r.port, out var v, out var src, out _) && Ecs.EffectiveType(em, src) == Ecs.PortOutput)
                {
                    cable = true; cv = v;
                    // the raw value for the display (a block reads a value above 1e20 as 0; the phone shows what is really on the wire)
                    if (Ecs.HasC(em, src, Ecs.tiPort)) { float raw = *(float*)((byte*)Ecs.RO(em, src, Ecs.tiPort) + 8); float dv = float.IsNaN(raw) || raw > 1e20f ? 0f : raw; if (dv == v) cv = raw; }
                }
            }
            catch (Exception) { cable = false; }
            bool ch = r.dstOn;
            if (ch && r.chSenders == 1) { r.inFrom = 2; r.inValue = r.chValue; }
            else if (ch && r.chSenders >= 2 && r.hasLatched) { r.inFrom = 3; r.inValue = r.latched; }
            else if (cable) { r.inFrom = 1; r.inValue = cv; }
            else if (ch && r.hasLatched) { r.inFrom = 3; r.inValue = r.latched; }
            else { r.inFrom = 0; r.inValue = 0f; }
        }
        /// <summary>The phone app refreshes the rows it shows every frame (ReadControls does it 10 times a second for the log).</summary>
        public static void RefreshInputs(EntityManager em, Comp c)
        {
            if (c == null || !c.inFlight || !Host) return;
            foreach (var r in c.rows) if (r.input) RefreshInput(em, r);
        }
        static void LogInput(Comp c, Row r)
        {
            if (r.inFrom == r.inFromLogged) return;
            r.inFromLogged = r.inFrom;
            if (c.inLogs++ >= 80) return;
            ModLog.Info("SC_RemoteComputer " + c.block.Index + " row " + r.n + " (INPUT): " + (r.inFrom == 1 ? "value from the CABLE (" + Phone.Num(r.inValue) + ")"
                : r.inFrom == 2 ? "value from wireless CHANNEL " + r.ch + " (one sender in range: " + Phone.Num(r.inValue) + ")"
                : r.inFrom == 3 ? "keeps the last value of channel " + r.ch + " (" + Phone.Num(r.inValue) + "; " + (r.chSenders >= 2 ? r.chSenders + " senders in range: conflict" : "no sender now, no cable signal") + ")"
                : "no signal (" + (r.dstOn ? "no sender on channel " + r.ch + " in range and " : "") + "no cable from an output)"));
        }
        static unsafe void WriteText(EntityManager em, Entity t, int value, ref int shown)
        {
            if (value == shown || !Ecs.Alive(em, t) || !Ecs.HasC(em, t, tiText)) return;
            int len = em.GetBufferLength(t, tiText); if (len <= 0 || len > 8) return;
            var b = (char*)em.GetBufferRawRW(t, tiText); if (b == null) return;
            string s = value < 0 ? "" : value.ToString(); if (s.Length > len) s = s.Substring(s.Length - len);
            s = s.PadLeft(len);
            for (int i = 0; i < len; i++) b[i] = s[i];
            shown = value;
        }

        // ------------------------------------------------------------------ LEDs (main thread)
        static void Leds(EntityManager em, Comp c, float now)
        {
            bool slow = Led.Lit, fast = ((int)(now * 10f) & 1) == 0;
            // main status LED: like the vanilla transmitter's (green, blinking) while powered; a fast blink after a command
            int baseMain = c.powered ? Led.Green : Led.Unlit;
            int wantMain = !c.powered ? Led.Unlit : now < c.flashUntil ? (fast ? baseMain : Led.Unlit) : (slow ? baseMain : Led.Unlit);
            if (wantMain != c.mainLedWritten && Ecs.Alive(em, c.mainLed)) c.mainLedWritten = Led.PaintSolid(em, c.mainLed, wantMain, c.mainLedWritten);
            foreach (var r in c.rows)
            {
                if (!Ecs.Alive(em, r.led)) continue;
                int state;
                if (!c.powered) state = Led.Unlit;
                else if (r.input)
                {
                    // 0.31.35, input row: cable only = off; cable + channel = a vanilla RECEIVER's LED (green = exactly one sender, red = 2 or more,
                    // off = none); a sender that just appeared shows at once (a row's send lasts only a few ticks), then it blinks like the game's
                    state = !r.dstOn ? Led.Unlit : now < r.chRedUntil ? Led.Red : now < r.chGreenUntil ? Led.Green : Led.Unlit;
                    int wantIn = state == Led.Unlit ? Led.Unlit : (slow || now < r.chFlashUntil) ? state : Led.Unlit;
                    if (wantIn != r.ledWritten) r.ledWritten = Led.PaintSolid(em, r.led, wantIn, r.ledWritten);
                    continue;
                }
                // cable only: the vanilla transmitter with no wireless use = off; a short green flash when a command arrives
                else if (!r.dstOn) state = now < r.flashUntil ? Led.Green : Led.Unlit;
                // cable + channel: the vanilla SENDER's meaning — red = another sender on the channel, green = somebody receives,
                // orange = nobody receives — from the last send (1.5 s) or, in standby, from what a send would meet now
                else state = r.window > 0 || r.hold || now < r.resultUntil ? (r.result >= 0 ? r.result : r.preview) : r.preview;
                int want = state == Led.Unlit ? Led.Unlit : now < r.flashUntil ? (fast ? state : Led.Unlit) : (r.dstOn ? (slow ? state : Led.Unlit) : Led.Unlit);
                if (want != r.ledWritten) r.ledWritten = Led.PaintSolid(em, r.led, want, r.ledWritten);
            }
        }

        // ------------------------------------------------------------------ wireless: who is on each channel (main thread, 2 Hz)
        struct Tx { public Entity t, port, led; public int ch; public float3 pos; public byte rt; }
        static readonly List<Tx> txs = new List<Tx>();
        /// <summary>0.31.35: the vanilla transmitters on the channels of the INPUT rows (refreshed twice a second on the main thread; their
        /// direction and value are read live inside the tick), and whether any powered input row listens to a channel at all.</summary>
        static readonly List<Tx> inTx = new List<Tx>(); static bool anyInputCh;
        static unsafe void Transmitters(EntityManager em, HashSet<int> channels, List<Tx> txs = null)
        {
            if (txs == null) txs = RemoteComputer.txs;
            txs.Clear();
            foreach (var t in Ecs.Transmitters(em))
            {
                int ch = Ecs.Channel(em, t); if (ch < 0 || (channels != null && !channels.Contains(ch))) continue;
                if (JointSystem.KindFromHash(Ecs.Prefab(em, t), out _, out _, out _)) continue;   // the mod's Shaft Transmitter (plasma port) is not one
                if (!Ecs.ScActive(em, t) || !Ecs.HasC(em, t, Ecs.tiLTW)) continue;
                var ps = Ecs.Ports(em, t); if (ps.Length == 0 || !Ecs.HasC(em, ps[0], Ecs.tiPort)) continue;
                byte rt = ((byte*)Ecs.RO(em, ps[0], Ecs.tiPort))[21];
                if (rt != Ecs.PortInput && rt != Ecs.PortOutput) continue;   // not wired: the game ignores it
                Entity led = Entity.Null;
                try { var b = (byte*)Ecs.RO(em, t, Ecs.tiWireless); if (b != null) led = *(Entity*)(b + 24); } catch (Exception) { }
                txs.Add(new Tx { t = t, port = ps[0], led = led, ch = ch, pos = Ecs.Position(em, t), rt = rt });
            }
        }
        static float D2(float3 a, float3 b) { var d = Ecs.Sub(a, b); return Ecs.Dot(d, d); }
        static void Preview(EntityManager em)
        {
            if (!Host) { anyInputCh = false; return; }
            var channels = new HashSet<int>(); HashSet<int> inCh = null;
            foreach (var c in comps.Values)
                if (c.inFlight && c.powered)
                    foreach (var r in c.rows)
                    {
                        if (!r.dstOn) continue;
                        if (r.input) (inCh ?? (inCh = new HashSet<int>())).Add(r.ch); else channels.Add(r.ch);
                    }
            anyInputCh = inCh != null;
            if (anyInputCh) Transmitters(em, inCh, inTx); else inTx.Clear();
            if (channels.Count == 0) return;
            Transmitters(em, channels);
            foreach (var c in comps.Values)
            {
                if (!c.inFlight || !c.powered) continue;
                foreach (var r in c.rows)
                {
                    if (!r.dstOn || r.input) continue;
                    int receivers = 0; bool other = false;
                    foreach (var t in txs)
                    {
                        if (t.ch != r.ch || D2(t.pos, c.pos) >= WirelessRangeSq) continue;
                        if (t.rt == Ecs.PortOutput) receivers++; else other = true;
                    }
                    if (!other) foreach (var c2 in comps.Values)
                        {
                            if (c2 == c || !c2.inFlight || !c2.powered || D2(c2.pos, c.pos) >= WirelessRangeSq) continue;
                            foreach (var r2 in c2.rows) if (r2.dstOn && !r2.input && r2.ch == r.ch && (r2.window > 0 || r2.hold)) { other = true; break; }
                        }
                    // 0.31.35: input rows listening to this channel in range (this computer's own too) are receivers
                    if (anyInputCh) foreach (var c2 in comps.Values)
                        {
                            if (!c2.inFlight || !c2.powered || D2(c2.pos, c.pos) >= WirelessRangeSq) continue;
                            foreach (var r2 in c2.rows) if (r2.input && r2.dstOn && r2.ch == r.ch) receivers++;
                        }
                    r.preview = other ? Led.Red : receivers > 0 ? Led.Green : Led.Yellow;
                }
            }
        }

        // ------------------------------------------------------------------ commands from the phone (main thread)
        /// <summary>Powered computers in flight on `channel` within RangeM of `from`, nearest first.</summary>
        public static List<Comp> Matching(EntityManager em, int channel, float3 from)
        {
            var l = new List<(Comp c, float d)>();
            if (!Host) return new List<Comp>();
            float r2 = RangeM * RangeM;
            foreach (var c in comps.Values)
            {
                if (!c.inFlight || !c.powered || c.mainCh != channel || !c.controlsOk || !Ecs.Alive(em, c.block)) continue;
                float d = D2(c.pos, from); if (d > r2) continue;
                l.Add((c, d));
            }
            l.Sort((a, b) => a.d.CompareTo(b.d));
            var r = new List<Comp>(l.Count); foreach (var (c, _) in l) r.Add(c);
            return r;
        }
        /// <summary>Delivery 2: garage computers on `channel` (the phone app in the garage sets their start state): no power and no range needed
        /// (it is configuration), nearest first.</summary>
        public static List<Comp> GarageMatching(EntityManager em, int channel, float3 from)
        {
            var l = new List<(Comp c, float d)>();
            if (!Host) return new List<Comp>();
            foreach (var c in comps.Values)
            {
                if (c.inFlight || c.mainCh != channel || !c.controlsOk || !Ecs.Alive(em, c.block)) continue;
                l.Add((c, D2(c.pos, from)));
            }
            l.Sort((a, b) => a.d.CompareTo(b.d));
            var r = new List<Comp>(l.Count); foreach (var (c, _) in l) r.Add(c);
            return r;
        }
        /// <summary>Delivery 2: the start state of switch-type row n of every garage computer in the list (saved in knobs.txt per block).
        /// Returns how many were saved.</summary>
        public static int SetGarageStart(EntityManager em, List<Comp> list, int n, bool on)
        {
            float now = Ecs.Now; int saved = 0;
            foreach (var c in list)
            {
                if (!Ecs.Alive(em, c.block) || c.inFlight) continue;
                var r = c.rows[n - 1]; if (!r.typSwitch || r.input) continue;
                if (Knobs.StoreValue(em, c.block, "r" + n + "on", on ? 1f : 0f)) saved++;
                r.on = on; r.flashUntil = now + 0.35f; c.flashUntil = now + 0.45f;
            }
            return saved;
        }
        static unsafe bool WriteState(EntityManager em, Row r, bool on)
        {
            r.on = on;
            if (!Ecs.Alive(em, r.typ)) return false;
            var p = Data(em, r.typ, true); if (p == null) return false;
            p[1] = on ? 1f : 0f; return true;
        }
        static unsafe bool WriteXY(EntityManager em, Row r, float x, float y)
        {
            r.x = x; r.y = y; r.hasXY = true;
            if (!Ecs.Alive(em, r.val)) return false;
            var p = Data(em, r.val, true); if (p == null) return false;
            p[1] = 1f; p[2] = x; p[3] = y; return true;
        }
        /// <summary>Phone button `n` (1..10): every computer of the list sets row n to `on` with the phone's x / y. A "cable + channel" row
        /// opens its send window: `hold` = it sends every tick until the release; otherwise WindowTicks ticks, then standby.</summary>
        public static void Apply(EntityManager em, List<Comp> list, int n, bool on, float x, float y, bool hold)
        {
            float now = Ecs.Now;
            foreach (var c in list)
            {
                if (!Ecs.Alive(em, c.block)) continue;
                var r = c.rows[n - 1];
                if (r.input) continue;   // 0.31.35: an input row takes no command
                if (on && !r.on) r.onTicks = 0;
                WriteState(em, r, on); WriteXY(em, r, x, y);
                r.flashUntil = now + 0.35f; c.flashUntil = now + 0.45f;
                if (r.dstOn && c.powered) { r.hold = hold; r.window = hold ? 0 : WindowTicks; r.logStart = true; r.conflictTicks = 0; r.deliveredMax = 0; }
            }
        }
        /// <summary>New x / y for row n without touching its state (edited on the phone); a channel row re-sends its current value.</summary>
        public static void ApplyXY(EntityManager em, List<Comp> list, int n, float x, float y)
        {
            float now = Ecs.Now;
            foreach (var c in list)
            {
                if (!Ecs.Alive(em, c.block)) continue;
                var r = c.rows[n - 1]; if (r.input) continue;
                WriteXY(em, r, x, y); r.flashUntil = now + 0.35f; c.flashUntil = now + 0.45f;
                if (r.dstOn && c.powered && !r.hold) { r.window = WindowTicks; r.logStart = true; r.conflictTicks = 0; r.deliveredMax = 0; }
            }
        }
        /// <summary>A quick tap of a button row: the "on" must reach the output (and the channel) for a couple of game ticks before the
        /// release turns it off, or a tap shorter than a tick would never be seen. True once every row n of the list was on for 2+ ticks.</summary>
        public static bool SeenOn(EntityManager em, List<Comp> list, int n)
        {
            foreach (var c in list) { if (!Ecs.Alive(em, c.block) || !c.powered) continue; var r = c.rows[n - 1]; if (!r.input && r.on && r.onTicks < 2) return false; }
            return true;
        }
        /// <summary>A held button was released: the off value goes out for WindowTicks ticks, then standby.</summary>
        public static void Release(EntityManager em, List<Comp> list, int n, float x, float y) => Apply(em, list, n, false, x, y, false);

        // ------------------------------------------------------------------ docks
        static void DockStep(EntityManager em, Dock d, float dt)
        {
            d.inFlight = Ecs.IsPart(em, Ecs.Parent(em, d.block));
            d.tFind += dt;
            if (!Ecs.Alive(em, d.toggle) && d.tFind >= 1f)
            {
                d.tFind = 0f; d.toggle = Entity.Null; d.hintState = -1;
                foreach (var f in Ecs.Children(em, d.block)) if (Ecs.HasC(em, f, tiToggle)) { d.toggle = f; break; }
                if (Ecs.IsNull(d.toggle)) foreach (var f in BeaconIcon.ActionablesOf(em, d.block)) if (Ecs.HasC(em, f, tiToggle)) { d.toggle = f; break; }
            }
            if (!Ecs.Alive(em, d.toggle)) return;
            // delivery 2: a taken phone stays hidden (the toggle may have been found again as another entity: hide that one too)
            if (d.taken) Hide(em, d);
            int want = !d.inFlight || !Host ? 0 : d.taken ? 2 : 1;   // (review: a client cannot take the phone: it keeps the delivery 1 hint)
            if (want != d.hintState)
            {
                d.hintState = want;
                Knobs.Hint(em, d.toggle, want == 0 ? Text("SC_PhoneDock_Hint", "Phone: tap E to open")
                                        : want == 1 ? Text("SC_PhoneDock_HintTake", "Phone: tap E to open, hold E to take")
                                        : Text("SC_PhoneDock_HintEmpty", "Phone dock: tap E to put the phone back"));
            }
        }
        static unsafe int* Layer(EntityManager em, Entity e, bool rw)
        {
            if (!layerOk || !Ecs.HasC(em, e, tiLayer)) return null;
            return rw ? (int*)em.GetComponentDataRawRW(e, tiLayer) : (int*)Ecs.RO(em, e, tiLayer);
        }
        /// <summary>The game hides a part the same way (GarageGrabber.SetSCInvisible: CRPLayerID = 31, Invalid); the old layer is kept to show it again.</summary>
        static unsafe void Hide(EntityManager em, Dock d)
        {
            var p = Layer(em, d.toggle, false); if (p == null || *p == 31) return;
            int old = *p; var w = Layer(em, d.toggle, true); if (w == null) return;
            d.savedLayer = old; *w = 31; d.hiddenOn = d.toggle;
        }
        static unsafe void Show(EntityManager em, Dock d)
        {
            if (Ecs.Alive(em, d.hiddenOn)) { var w = Layer(em, d.hiddenOn, true); if (w != null && *w == 31) *w = d.savedLayer; }
            if (Ecs.Alive(em, d.toggle) && (d.toggle.Index != d.hiddenOn.Index || d.toggle.Version != d.hiddenOn.Version)) { var w = Layer(em, d.toggle, true); if (w != null && *w == 31) *w = d.savedLayer; }
            d.hiddenOn = Entity.Null;
        }
        static Dock DockOf(Entity block) => docks.TryGetValue(Ecs.K(block), out var d) ? d : null;
        /// <summary>Delivery 2: does this dock show its phone? (false = taken, or not a tracked dock)</summary>
        public static bool DockHasPhone(Entity block) { var d = DockOf(block); return d != null && !d.taken; }
        public static bool DockTracked(Entity block) => DockOf(block) != null;
        /// <summary>Delivery 2: the dock's phone goes to the hotbar: hidden on the dock. false = not a tracked dock or already taken.</summary>
        public static bool TakeFromDock(EntityManager em, Entity block)
        {
            var d = DockOf(block); if (d == null || d.taken) return false;
            d.taken = true; d.hintState = -1;
            if (Ecs.Alive(em, d.toggle)) Hide(em, d);
            return true;
        }
        /// <summary>Delivery 2: the phone is back on its dock (shown again). Safe on a dock that already shows it.</summary>
        public static void ReturnToDock(EntityManager em, Entity block)
        {
            var d = DockOf(block); if (d == null) return;
            d.taken = false; d.hintState = -1;
            try { Show(em, d); } catch (Exception) { }
        }
        /// <summary>Delivery 2: the dock's phone entity (renderer + toggle + click box), the source of the phone drawn in the hand.</summary>
        public static Entity DockToggle(Entity block) { var d = DockOf(block); return d == null ? Entity.Null : d.toggle; }
        /// <summary>Delivery 2: the layer the dock's phone had before it was hidden (0 if never hidden).</summary>
        public static int DockLayer(Entity block) { var d = DockOf(block); return d == null ? 0 : d.savedLayer; }
        /// <summary>Delivery 2: any flight dock whose phone entity is alive (source for the phone in the hand when the item's own dock is gone).</summary>
        public static Entity AnyFlightToggle(EntityManager em)
        {
            foreach (var d in docks.Values) if (d.inFlight && Ecs.Alive(em, d.toggle) && Ecs.Alive(em, d.block)) return d.toggle;
            return Entity.Null;
        }
        /// <summary>Delivery 2: the flight dock with this SCGuid (a rewind may rebuild a destroyed dock as a new entity). Entity.Null if none.</summary>
        public static Entity FlightDockByGuid(EntityManager em, string guid)
        {
            if (string.IsNullOrEmpty(guid)) return Entity.Null;
            foreach (var d in docks.Values)
            {
                if (!d.inFlight || !Ecs.Alive(em, d.block)) continue;
                string g; try { g = Ecs.Guid(em, d.block); } catch (Exception) { continue; }
                if (g == guid) return d.block;
            }
            return Entity.Null;
        }
        /// <summary>Delivery 2: every tracked dock shows its phone again (the hotbar was reset).</summary>
        public static void ShowAllDocks(EntityManager em) { foreach (var d in docks.Values) if (d.taken || !Ecs.IsNull(d.hiddenOn)) { d.taken = false; d.hintState = -1; try { Show(em, d); } catch (Exception) { } } }
        /// <summary>Is the game's pointer target (ActionablesController._targetEntity) the phone of a Phone Dock? Returns the dock block.</summary>
        public static bool IsDockPhone(EntityManager em, Entity target, out Entity dock)
        {
            dock = Entity.Null;
            if (!Prepare() || Ecs.IsNull(target) || !Ecs.Alive(em, target)) return false;
            foreach (var d in docks.Values) if (d.toggle.Index == target.Index && d.toggle.Version == target.Version && Ecs.Alive(em, d.block)) { dock = d.block; return true; }
            var b = BeaconIcon.BlockOfActionable(em, target);
            if (Ecs.IsNull(b) || !Ecs.Alive(em, b) || dockHash == 0 || Ecs.Prefab(em, b) != dockHash) return false;
            dock = b; return true;
        }

        // ------------------------------------------------------------------ inside the game's tick (SpaceshipComponentsTickPostGroup)
        static readonly Dictionary<long, float> rxSaved = new Dictionary<long, float>(); static bool prefixValid;
        static bool AnyWindow()
        {
            foreach (var c in comps.Values) if (c.inFlight && c.powered) foreach (var r in c.rows) if (r.dstOn && !r.input && (r.window > 0 || r.hold)) return true;
            return false;
        }
        /// <summary>Prefix of the group (before SCTick_WirelessTransmitter): the value of every vanilla receiver on a channel a row is about to
        /// send on, so a conflict leaves it exactly as the game would (2+ senders = the port is not written).</summary>
        public static unsafe void BeforeTick(EntityManager em)
        {
            prefixValid = false;
            if (!Host || comps.Count == 0 || !ok || !AnyWindow()) return;
            try
            {
                var channels = new HashSet<int>();
                foreach (var c in comps.Values) if (c.inFlight && c.powered) foreach (var r in c.rows) if (r.dstOn && !r.input && (r.window > 0 || r.hold)) channels.Add(r.ch);
                rxSaved.Clear();
                Transmitters(em, channels);
                foreach (var t in txs) if (t.rt == Ecs.PortOutput && Ecs.HasC(em, t.port, Ecs.tiPort)) rxSaved[Ecs.K(t.port)] = *(float*)((byte*)Ecs.RO(em, t.port, Ecs.tiPort) + 8);
                prefixValid = true;
            }
            catch (Exception ex) { if (tickWarn++ < 3) ModLog.Warn("remote computer (tick prefix): " + ex.Message); }
        }
        static int tickWarn;
        static unsafe void WritePorts(EntityManager em, Comp c)
        {
            foreach (var r in c.rows)
            {
                if (r.input) continue;   // 0.31.35: an input row's port is written by the game (the value on its cable), never by the mod
                // the state is read live from the TYPE switch here too: right after a time rewind the port follows the restored state at once
                if (Ecs.Alive(em, r.typ)) { var p = Data(em, r.typ, false); if (p != null) r.on = !(p[1] < 0.5f); }
                if (Ecs.Alive(em, r.port)) Ecs.WritePort(em, r.port, r.Out(c.powered));
                if (r.on && c.powered && r.onTicks < 1000) r.onTicks++;
            }
        }
        struct Sender { public Comp c; public Row r; public float v; public int delivered; public bool conflict; }
        /// <summary>Postfix of the group, after the game's wireless job: the row outputs (every tick, like a vanilla switch's port) and the
        /// wireless send of the rows in a send window.</summary>
        public static unsafe void AfterTick(EntityManager em)
        {
            if (!Host || comps.Count == 0 || !ok) return;
            try
            {
                foreach (var c in comps.Values) if (c.inFlight && Ecs.Alive(em, c.block)) WritePorts(em, c);
                bool any = AnyWindow();
                if (!any) { prefixValid = false; if (anyInputCh) SampleInputs(em, noSenders); return; }
                var senders = new List<Sender>(); var channels = new HashSet<int>();
                foreach (var c in comps.Values)
                {
                    if (!c.inFlight || !c.powered) continue;
                    foreach (var r in c.rows) if (r.dstOn && !r.input && (r.window > 0 || r.hold)) { senders.Add(new Sender { c = c, r = r, v = r.Out(true) }); channels.Add(r.ch); }
                }
                Transmitters(em, channels);
                var arr = senders.ToArray();
                // the receivers: exactly one sender in range (counting the vanilla ones) = it takes the row's value
                foreach (var rx in txs)
                {
                    if (rx.rt != Ecs.PortOutput) continue;
                    int mod = 0, mine = -1, vanilla = 0;
                    for (int i = 0; i < arr.Length; i++) if (arr[i].r.ch == rx.ch && D2(arr[i].c.pos, rx.pos) < WirelessRangeSq) { mod++; mine = i; }
                    if (mod == 0) continue;
                    foreach (var tx in txs) if (tx.rt == Ecs.PortInput && tx.ch == rx.ch && D2(tx.pos, rx.pos) < WirelessRangeSq) vanilla++;
                    if (mod + vanilla == 1)
                    {
                        Ecs.WritePort(em, rx.port, arr[mine].v); arr[mine].delivered++;
                        if (Ecs.Alive(em, rx.led)) Led.Paint(em, rx.led, Led.Green, -1);
                    }
                    else
                    {
                        // 2+ senders: the game leaves the port as it was (its job may have written its own sender's value: put it back)
                        if (vanilla > 0 && prefixValid && rxSaved.TryGetValue(Ecs.K(rx.port), out var before)) Ecs.WritePort(em, rx.port, before);
                        if (Ecs.Alive(em, rx.led)) Led.Paint(em, rx.led, Led.Red, -1);
                        for (int i = 0; i < arr.Length; i++) if (arr[i].r.ch == rx.ch && D2(arr[i].c.pos, rx.pos) < WirelessRangeSq) arr[i].conflict = true;
                    }
                }
                // 0.31.35: the input rows listening to a channel are receivers too (before the senders' results are worked out)
                if (anyInputCh) SampleInputs(em, arr);
                // a sender's own view (like the vanilla sender LED): another sender on the channel within range = conflict
                for (int i = 0; i < arr.Length; i++)
                {
                    for (int j = 0; j < arr.Length && !arr[i].conflict; j++) if (j != i && arr[j].r.ch == arr[i].r.ch && D2(arr[j].c.pos, arr[i].c.pos) < WirelessRangeSq) arr[i].conflict = true;
                    foreach (var tx in txs)
                        if (tx.rt == Ecs.PortInput && tx.ch == arr[i].r.ch && D2(tx.pos, arr[i].c.pos) < WirelessRangeSq) { arr[i].conflict = true; if (Ecs.Alive(em, tx.led)) Led.Paint(em, tx.led, Led.Red, -1); }
                }
                float now = Ecs.Now;
                for (int i = 0; i < arr.Length; i++)
                {
                    var s = arr[i]; var r = s.r;
                    r.result = s.conflict ? Led.Red : s.delivered > 0 ? Led.Green : Led.Yellow; r.resultUntil = now + 1.5f;
                    if (s.conflict) r.conflictTicks++; if (s.delivered > r.deliveredMax) r.deliveredMax = s.delivered;
                    if (r.logStart)
                    {
                        r.logStart = false; r.sending = true;
                        ModLog.Info("remote: SC_RemoteComputer " + s.c.block.Index + " row " + r.n + ": SEND on wireless channel " + r.ch + " (" + (r.on ? "on" : "off") + ", value " + Phone.Num(s.v) + (r.hold ? ", while the button is held" : ", " + WindowTicks + " tick(s)") + ") — "
                            + (s.conflict ? "CONFLICT: another sender on channel " + r.ch + " within 2000 m, receivers keep their value" : s.delivered + " receiver(s) in range"));
                    }
                    if (!r.hold && r.window > 0) r.window--;
                    if (!r.hold && r.window == 0 && r.sending)
                    {
                        r.sending = false;
                        ModLog.Info("remote: SC_RemoteComputer " + s.c.block.Index + " row " + r.n + ": standby on channel " + r.ch + " (" + (r.conflictTicks > 0 ? "conflict in " + r.conflictTicks + " tick(s); " : "") + "receivers keep " + Phone.Num(s.v) + ", reached " + r.deliveredMax + ")");
                    }
                }
                prefixValid = false;
            }
            catch (Exception ex) { if (tickWarn++ < 3) ModLog.Warn("remote computer (tick): " + ex.Message); }
        }
        static readonly Sender[] noSenders = new Sender[0];
        /// <summary>0.31.35, inside the tick (after the rows' own send): every powered INPUT row set to "cable + channel" counts the senders on its
        /// channel within 2000 m of its computer, as a vanilla receiver does — vanilla transmitters that send (port 0 is a DataInput at run time,
        /// read live; their list and positions are 0.5 s old) and the mod's OUTPUT rows sending this tick. Exactly one = its value is taken and
        /// kept; two or more = conflict (the kept value stays). Nothing of the game is written here except the sender's LED colour.</summary>
        static unsafe void SampleInputs(EntityManager em, Sender[] arr)
        {
            float now = Ecs.Now;
            foreach (var c in comps.Values)
            {
                if (!c.inFlight || !c.powered) continue;
                foreach (var r in c.rows)
                {
                    if (!r.input || !r.dstOn) continue;
                    int n = 0, mine = -1; float v = 0f; Entity vLed = Entity.Null;
                    for (int i = 0; i < arr.Length; i++) if (arr[i].r.ch == r.ch && D2(arr[i].c.pos, c.pos) < WirelessRangeSq) { n++; mine = i; v = arr[i].v; }
                    for (int i = 0; i < inTx.Count; i++)
                    {
                        var t = inTx[i];
                        if (t.ch != r.ch || D2(t.pos, c.pos) >= WirelessRangeSq || !Ecs.HasC(em, t.port, Ecs.tiPort)) continue;
                        var b = (byte*)Ecs.RO(em, t.port, Ecs.tiPort); if (b == null || b[21] != Ecs.PortInput) continue;   // a receiver, or no direction
                        n++; mine = -1; v = *(float*)(b + 8); vLed = t.led;
                    }
                    if (n > 0 && r.chSenders == 0) r.chFlashUntil = now + 0.5f;
                    r.chSenders = n;
                    if (n == 1)
                    {
                        r.chValue = v; r.latched = v; r.hasLatched = true; r.chGreenUntil = now + 0.5f;
                        if (mine >= 0) arr[mine].delivered++;
                        // a vanilla sender whose only listeners are input rows: its own job painted "nobody listens" (orange) this tick
                        else if (Led.Lit && Ecs.Alive(em, vLed)) Led.Paint(em, vLed, Led.Green, -1);
                    }
                    else if (n >= 2)
                    {
                        r.chRedUntil = now + 0.5f;
                        for (int i = 0; i < arr.Length; i++) if (arr[i].r.ch == r.ch && D2(arr[i].c.pos, c.pos) < WirelessRangeSq) arr[i].conflict = true;
                    }
                }
            }
        }
    }
}
