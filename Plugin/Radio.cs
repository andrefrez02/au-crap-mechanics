using System;
using Il2CppInterop.Runtime;
using System.Collections.Generic;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>0.18.0 (Andre: "a long range transmitter that instead of setting the channel on the little screen takes the channel from a data
    /// input, which can change during the simulation, and its range will be huge"; "two ports: one in/out, another for the channel").
    /// A mod block with the Wireless Transmitter's meshes: port 0 = signal (InOut, like the game transmitter's), port 1 = channel (data
    /// input, rounded). Every frame: whoever has the signal wired to an OUTPUT (the port resolves as input) sends; whoever has the signal wired
    /// to an INPUT (the port resolves as output) receives the value of whoever sends on the same channel. No distance limit (any ship in the
    /// world). Channels of its own: it doesn't talk to the game's Wireless Transmitter.</summary>
    internal static class Radio
    {
        sealed class R { public Entity block, signal, channelP, text; public bool portsOk, logged, searchedText; public int channel = int.MinValue, warnings, channelScreen = int.MinValue; public byte kindLog = 255; public float tText; public bool painted; public float tPaint, tLed = 9f; public Entity led; public int ledWritten = -1; public byte resolved = 255; public float tResolve = 9f; public string resolvedVia; public byte wroteKind = 255;
            public int channelCand = int.MinValue; public float tCand; public Entity channelSource; public bool heldLog;
            public float latestV = float.NaN, tLastV; public bool frozenLog, noSourceLog; }   // 0.19.8: frozen video source diagnostics   // 0.19.7: channel source (holds the channel if the same source reads 0)   // 0.19.6: candidate channel (only becomes the channel after 0.3 s stable)
        static readonly Dictionary<long, R> radios = new Dictionary<long, R>();
        public static IEnumerable<Entity> Blocks() { foreach (var r in radios.Values) yield return r.block; }   // 0.19.12: transition log
        public static int RadioCount() => radios.Count;
        static readonly Dictionary<int, float> channelValue = new Dictionary<int, float>();
        static readonly Dictionary<int, Entity> whoSends = new Dictionary<int, Entity>();
        static readonly HashSet<int> twoSendWarn = new HashSet<int>();

        public static bool Has(long k) => radios.ContainsKey(k);
        // 0.18.2: channel number on the screen (the game's 3D text, "RendererText3D Channel", 3 digits right-aligned)
        static TypeIndex tiText; static bool textOk, textFailed;
        static readonly Unity.Mathematics.float3 TextPos = new Unity.Mathematics.float3 { x = 0.04958624f, y = -0.01527491f, z = -0.04406795f };
        static unsafe void ScreenSize(EntityManager em, R r, float dt)
        {
            if (textFailed) return;
            try
            {
                if (!textOk) { tiText = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<CRPText3DString>()); textOk = true; }
                r.tText += dt;
                if (!Ecs.Alive(em, r.text) && (!r.searchedText || r.tText > 3f))
                {
                    r.searchedText = true; r.tText = 0f; r.text = Entity.Null; Entity three = Entity.Null;
                    foreach (var f in Ecs.Children(em, r.block))
                    {
                        if (!Ecs.Has(em, f, tiText)) continue;
                        var p = Ecs.LT(em, f).Position;
                        if (System.Math.Abs(System.Math.Abs(p.x) - TextPos.x) +   /* 0.19.0: the M version has x mirrored */ System.Math.Abs(p.y - TextPos.y) + System.Math.Abs(p.z - TextPos.z) < 0.01f) { r.text = f; break; }
                        if (em.GetBufferLength(f, tiText) == 3) three = f;
                    }
                    if (Ecs.IsNull(r.text)) r.text = three;
                    if (Ecs.IsNull(r.text)) { if (r.warnings++ < 1 && Ecs.IsPart(em, Ecs.Parent(em, r.block))) ModLog.Warn("SC_LongRangeTransmitter " + r.block.Index + ": channel display not found (rebuild the bundle in the Modkit)"); return; }
                    r.channelScreen = int.MinValue;
                }
                if (Ecs.IsNull(r.text) || r.channel == r.channelScreen) return;
                int n = em.GetBufferLength(r.text, tiText); var b = (char*)em.GetBufferRawRW(r.text, tiText); if (b == null || n <= 0) return;
                string s = r.channel.ToString(); if (s.Length > n) s = s.Substring(s.Length - n);
                s = s.PadLeft(n);
                for (int i = 0; i < n; i++) b[i] = s[i];
                r.channelScreen = r.channel;
            }
            catch (Exception ex) { textFailed = true; ModLog.Warn("SC_LongRangeTransmitter: channel display: " + ex.Message); }
        }
        public static void Forget() { foreach (var r in radios.Values) Ecs.ResolvedTypes.Remove(Ecs.K(r.signal)); radios.Clear(); channelValue.Clear(); whoSends.Clear(); }
        /// <summary>0.31.11 (Andre: the channel 1 receiver stayed red, "not wired" — its signal cable goes straight into a pass-through port of an
        /// SC_Bearing M): the game gives the in/out signal port its direction only from the port at the other end of its cable, and a mod joint's
        /// pass-through port has none. Twice a second: if the game left it without a direction and the cable ends at a joint's pass-through port,
        /// look across the joint (SlipRings.AcrossJoint): an input over there (a monitor) = this one is an OUTPUT (receiving), an output over there
        /// (a camera) = an INPUT (sending). The slip ring then bridges the data across like any other (Ecs.EffectiveType).
        /// 0.31.26 (item 6): the direction is also written into the port's _runtimeType (+21), as Signal.Aim does and as the game's Frame With
        /// Ports does in UpdatePortsDataInOutJob. The game's GetInputs only copies when the port at the other end has the BYTE +21 == DataOutput
        /// [recon/Spaceship__part_a.cs:709-714]; the mod's dictionary alone never reached a game block (a game monitor across the ring never got
        /// this LRT's video). The game keeps the byte: that job only rewrites +21 when the end is Null, and stops (break) on an Unset port that
        /// is not a Frame With Ports [recon/Spaceship__part_b.cs:207-245]. Put back to DataInOut (3) when the mod's reason for it is gone.</summary>
        static unsafe void Resolve(EntityManager em, R r)
        {
            r.tResolve += UnityEngine.Time.deltaTime; if (r.tResolve < 0.5f) return; r.tResolve = 0f;
            long k = Ecs.K(r.signal); byte now = 255; string via = null;
            byte game = RuntimeKind(em, r.signal);
            bool ours = r.wroteKind != 255 && game == r.wroteKind;   // the byte holds what this method wrote, not the game's own answer
            bool endUntyped = false, endNull = false;
            if ((game != Ecs.PortInput && game != Ecs.PortOutput) || ours)
            {
                // the CABLE's far end (not the game's runtime end: when this one sends across the joint, the slip ring rewrites that one)
                Entity end = Entity.Null;
                if (Ecs.RawPortPub(em, r.signal, out _, out _, out _, out var persist) && !Ecs.IsNull(persist)) end = Ecs.IsCable(em, persist) ? Ecs.WalkCable(em, persist, r.signal, out _) : persist;
                endNull = Ecs.IsNull(end);
                if (!Ecs.IsNull(end) && Ecs.Port(em, end, out byte te, out _, out _) && te == JointSystem.PortUntyped)
                {
                    endUntyped = true;
                    if (SlipRings.AcrossJoint(em, end, out var far, out via))
                    {
                        byte tf = Ecs.EffectiveType(em, far);
                        if (tf == Ecs.PortInput) now = Ecs.PortOutput; else if (tf == Ecs.PortOutput) now = Ecs.PortInput;
                        if (now == 255 && r.resolvedVia != "!" + via) { r.resolvedVia = "!" + via; ModLog.Info("SC_LongRangeTransmitter " + r.block.Index + ": its signal cable goes into a joint (" + via + "), but on the other side there is " + (tf == 255 ? "no port" : "a port of type " + tf) + " — no direction"); }
                        via = "on the other side of " + via;
                    }
                    else
                    {
                        // 0.31.11 (Andre: "there is no slip ring in that circuit" — the receiver drives an SC_Bearing M's servo straight through that port;
                        // log: "mode servo (direct signal, port 154655 -> 154645)"): an untyped port of a MOD block that is not a slip ring channel is
                        // the block's signal input (servo / motor / piston), read by the mod — so the transmitter receives.
                        var owner = Ecs.Parent(em, end);
                        if (JointSystem.KindFromHash(Ecs.Prefab(em, owner), out _, out _, out var nm)) { now = Ecs.PortOutput; via = "into the signal input of " + nm + " " + owner.Index; }
                    }
                }
            }
            r.resolved = now;
            if (now != 255) { Ecs.ResolvedTypes[k] = now; r.resolvedVia = via; } else Ecs.ResolvedTypes.Remove(k);
            // 0.31.26 (item 6): the game's byte. Written only when the game left the port without a direction (or it still holds our value);
            // given back as DataInOut only when the game itself would not give it one (cable ending in nothing or on a mod's untyped port),
            // so a direction the game resolved on its own is never undone.
            try
            {
                if (now != 255 && (game != Ecs.PortInput && game != Ecs.PortOutput || ours))
                {
                    if (game != now) { var w = Ecs.HasC(em, r.signal, Ecs.tiPort) ? (byte*)em.GetComponentDataRawRW(r.signal, Ecs.tiPort) : null; if (w != null) { w[21] = now; Ecs.ForgetPaths(); } }
                    if (r.wroteKind != now) ModLog.Info("SC_LongRangeTransmitter " + r.block.Index + ": signal port set to " + (now == Ecs.PortInput ? "DataInput (sending)" : "DataOutput (receiving)") + " in the game's port too, so the game's blocks across the joint read it");
                    r.wroteKind = now;
                }
                else if (now == 255 && ours)
                {
                    if (endNull || endUntyped) { var w = Ecs.HasC(em, r.signal, Ecs.tiPort) ? (byte*)em.GetComponentDataRawRW(r.signal, Ecs.tiPort) : null; if (w != null) w[21] = 3; ModLog.Info("SC_LongRangeTransmitter " + r.block.Index + ": no direction across the joint any more — its port is in/out again"); }
                    r.wroteKind = 255;
                }
                else if (!ours) r.wroteKind = 255;
            }
            catch (Exception ex) { if (r.warnings++ < 3) ModLog.Warn("SC_LongRangeTransmitter " + r.block.Index + ": writing the port direction: " + ex.Message); }
        }
        public static void Track(Entity e) { radios[Ecs.K(e)] = new R { block = e }; ModLog.Info("SC_LongRangeTransmitter found: block " + e.Index + ":" + e.Version); }

        static unsafe byte RuntimeKind(EntityManager em, Entity p)
        {
            // 0.19.3 (crash dump 01/10, 16:47: AV reading [port+21] — the cached port had been destroyed when the game rebuilt the BP in the
            // garage): only reads if the entity still exists and has the component
            try { if (!Ecs.HasC(em, p, Ecs.tiPort)) return 255; var b = (byte*)Ecs.RO(em, p, Ecs.tiPort); return b == null ? (byte)255 : b[21]; } catch (Exception) { return 255; }
        }

        /// <summary>0.19.0 (Andre: "the long transmitter isn't transmitting the camera's data"): the camera's video signal is a
        /// large identifier on the data port (1e21 + index * 1e17) — Ecs.ReadInput zeroes values above 1e20, as the game's DataValue() does
        /// (0.31.26, item 17: it is 1e20, not 1e6 [recon/EPC_SCCable.cs:83-85]). Here the value goes raw (the 4 bytes), as the game's Wireless
        /// Transmitter passes it on.</summary>
        static unsafe bool ReadRaw(EntityManager em, Entity input, out float v)
        {
            v = 0f; var f = Ecs.Source(em, input, out _);
            if (Ecs.IsNull(f) || !Ecs.Alive(em, f) || !Ecs.Has(em, f, Ecs.tiPort)) return false;
            var b = (byte*)Ecs.RO(em, f, Ecs.tiPort); if (b == null) return false;
            v = *(float*)(b + 8);
            // no filter: the identifier may have any bit (copies the 4 bytes as they are)
            if (!rawLog && Math.Abs(v) > 1e6f) { rawLog = true; ModLog.Info("SC_LongRangeTransmitter: passing a large signal (" + v.ToString("G9") + " — camera video?) through the channel"); }
            return true;
        }
        static bool rawLog;
        /// <summary>0.31.10 (Andre: the channel 1 receiver's LED is red, "not wired (type 3)", and the cable is NOT loose): the whole path of the cable
        /// on a port, for the log — each port (block, port number, type set in the prefab and the type the game gives it now), the cable (data/power,
        /// cells) and where it ends; through Frame With Ports it goes on from the paired port, as the game does.</summary>
        static string TypeName(byte t) => t == 0 ? "input" : t == 1 ? "output" : t == 2 ? "power" : t == 3 ? "in/out" : t == 4 ? "plasma" : t == 5 ? "pass-through" : "?" + t;
        internal static unsafe string CablePath(EntityManager em, Entity start)
        {
            var sb = new System.Text.StringBuilder(); var seen = new HashSet<long>(); Entity cur = start; bool viaFrame = false;
            try
            {
                for (int hop = 0; hop < 12; hop++)
                {
                    if (!seen.Add(Ecs.K(cur))) { sb.Append(" -> back where it started (loop)"); break; }
                    if (!Ecs.HasC(em, cur, Ecs.tiPort)) { sb.Append(hop == 0 ? "" : " -> ").Append("entity ").Append(cur.Index).Append(" (not a port)"); break; }
                    var b = (byte*)Ecs.RO(em, cur, Ecs.tiPort);
                    Entity persist = *(Entity*)b, end = *(Entity*)(b + 12); byte setup = b[20], now = b[21];
                    var owner = Ecs.Parent(em, cur);
                    string nm = BlockIndex.NameOfHash(Ecs.Prefab(em, owner)) ?? "block";
                    int pi = Array.FindIndex(Ecs.Ports(em, owner), x => x.Index == cur.Index && x.Version == cur.Version);
                    sb.Append(hop == 0 ? "" : viaFrame ? " => " : " -> ").Append(nm).Append(' ').Append(owner.Index).Append(" port #").Append(pi).Append(" (").Append(cur.Index)
                      .Append(", ").Append(TypeName(setup)).Append(setup != now ? ", the game made it " + TypeName(now) : "").Append(")");
                    if (hop == 0) sb.Append(" [game's end of the line: ").Append(Ecs.IsNull(end) ? "none" : end.Index.ToString()).Append("]");
                    bool frame = !Ecs.IsNull(owner) && Ecs.Alive(em, owner) && Ecs.Has(em, owner, Ecs.tiFramePorts);
                    if (hop > 0 && !viaFrame)
                    {
                        if (!frame) break;   // a real block: the end of the cable
                        var ps = Ecs.Ports(em, owner); if (pi < 0 || ps.Length < 2) { sb.Append(" (frame port outside its buffer)"); break; }
                        cur = ps[(pi + ps.Length / 2) % ps.Length]; viaFrame = true; continue;   // through the frame to the paired port
                    }
                    viaFrame = false;
                    if (Ecs.IsNull(persist)) { sb.Append(" -> NOTHING plugged in"); break; }
                    if (!Ecs.IsCable(em, persist)) { sb.Append(" -> plugged straight into "); cur = persist; continue; }
                    var cb = (byte*)Ecs.RO(em, persist, Ecs.tiCable); byte ct = cb == null ? (byte)255 : cb[1];
                    var far = Ecs.WalkCable(em, persist, cur, out int cells);
                    sb.Append(" -> ").Append(ct == 0 ? "data" : ct == 1 || ct == 2 ? "POWER" : ct == 3 ? "PLASMA" : "?").Append(" cable, ").Append(cells).Append(" cell(s)");
                    if (Ecs.IsNull(far)) { sb.Append(" -> the cable ENDS IN NOTHING (its other end isn't plugged into a port)"); break; }
                    cur = far;
                }
            }
            catch (Exception ex) { sb.Append(" (path failed: ").Append(ex.Message).Append(")"); }
            return sb.ToString();
        }
        /// <summary>0.19.6 (Andre: "the camera keeps flickering non-stop using the LRT" at 3x and even at normal speed; "losing signal after
        /// a while"): the signal was copied once per FRAME (Update), but the video identifier and the monitor move per physics TICK
        /// (at 3x there are 3 ticks per frame: 2 kept an old value = static); and the channel, read between ticks, dropped to 0 for a few frames.
        /// Now the copy runs in the prefix/postfix of the components' tick groups (and in Update, to be safe), and the channel only changes
        /// after 0.3 s stable.</summary>
        public static void Copy(EntityManager em, bool inTick)
        {
            if (radios.Count == 0) return;
            channelValue.Clear(); whoSends.Clear();
            float now = UnityEngine.Time.unscaledTime;
            foreach (var r in radios.Values)
            {
                if (!r.portsOk || !Ecs.Alive(em, r.block)) continue;
                try
                {
                    int channel = 0;
                    bool hasChannel = Ecs.ReadInput(em, r.channelP, out var vc, out var source, out var trail); if (hasChannel) channel = (int)Math.Max(int.MinValue + 1.0, Math.Min(int.MaxValue, Math.Round((double)vc)));   // 0.31.26: values up to 1e20 now reach here (item 17) — kept inside int (int.MinValue is "no channel yet")
                    // 0.19.7 (log: the 3 LRTs went to channel 0 together when leaving the seat / on time rewind and the signal vanished): if the SAME source that
                    // gave a channel != 0 starts reading 0, hold the channel (the game zeroes the ports at those moments).
                    // 0.19.8 (Andre: "leaving the atmosphere it loses the signal"; log: channel 0 without "holding" = the SOURCE vanished, it didn't read 0): also holds
                    // when the path to the source vanishes, and logs the path (trail) once to see what the game changed.
                    bool sameSource = source.Index == r.channelSource.Index && source.Version == r.channelSource.Version;
                    if ((!hasChannel || channel == 0) && r.channel != int.MinValue && r.channel != 0 && (sameSource || !hasChannel))
                    {
                        channel = r.channel;
                        if (!r.heldLog) { r.heldLog = true; ModLog.Info("SC_LongRangeTransmitter " + r.block.Index + ": channel source " + (hasChannel ? "read 0" : "LOST (path:" + trail + ")") + " — holding channel " + r.channel + "; " + Diag(em, r)); }
                    }
                    else if (hasChannel && channel != 0) { r.channelSource = source; if (r.heldLog) { r.heldLog = false; ModLog.Info("SC_LongRangeTransmitter " + r.block.Index + ": channel source is back (" + channel + ")"); } }
                    if (r.channel == int.MinValue) { r.channel = channel; r.channelCand = channel; ModLog.Info("SC_LongRangeTransmitter " + r.block.Index + ": channel " + channel); }
                    else if (channel != r.channel)
                    {
                        if (channel != r.channelCand) { r.channelCand = channel; r.tCand = now; }
                        else if (now - r.tCand >= 0.3f) { r.channel = channel; ModLog.Info("SC_LongRangeTransmitter " + r.block.Index + ": channel " + channel); }
                    }
                    else r.channelCand = channel;
                    byte tr = RuntimeKind(em, r.signal); bool across = false;
                    if (tr != Ecs.PortInput && tr != Ecs.PortOutput && r.resolved != 255) { tr = r.resolved; across = true; }   // 0.31.11: through a mod joint
                    else if (r.wroteKind != 255 && tr == r.wroteKind) across = true;   // 0.31.26 (item 6): the byte the mod wrote
                    if (tr != r.kindLog) { r.kindLog = tr; ModLog.Info("SC_LongRangeTransmitter " + r.block.Index + ": signal port is " + (tr == Ecs.PortInput ? "SENDING (wired to an output" : tr == Ecs.PortOutput ? "RECEIVING (wired to an input" : "not wired (type " + tr + ")") + (across ? " " + r.resolvedVia + ")" : tr == Ecs.PortInput || tr == Ecs.PortOutput ? ")" : "") + " on channel " + r.channel
                        + (tr != Ecs.PortInput && tr != Ecs.PortOutput ? " — where its cable goes: " + CablePath(em, r.signal) : "")); }   // 0.31.10
                    float v = 0f; bool hasVideo = tr == Ecs.PortInput && ReadRaw(em, r.signal, out v);
                    if (tr == Ecs.PortInput && !hasVideo && !inTick) { if (!r.noSourceLog) { r.noSourceLog = true; Ecs.Source(em, r.signal, out var tv); ModLog.Warn("SC_LongRangeTransmitter " + r.block.Index + ": SENDING but the signal source is gone (path:" + tv + "); " + Diag(em, r)); } }
                    else if (hasVideo && r.noSourceLog) { r.noSourceLog = false; ModLog.Info("SC_LongRangeTransmitter " + r.block.Index + ": signal source is back"); }
                    if (hasVideo)
                    {
                        if (whoSends.TryGetValue(r.channel, out var other) && other.Index < r.block.Index) { if (twoSendWarn.Add(r.channel)) ModLog.Warn("long range channel " + r.channel + ": more than one transmitter is SENDING — the first one wins (block " + other.Index + ")"); continue; }
                        whoSends[r.channel] = r.block; channelValue[r.channel] = v;
                        if (!inTick) Frozen(em, r, v, now);   // 0.19.8 (Andre: "I left the atmosphere and lost the signal"): video that stops changing
                    }
                }
                catch (Exception ex) { if (r.warnings++ < 3) ModLog.Warn("SC_LongRangeTransmitter " + r.block.Index + ": " + ex.Message); }
            }
            foreach (var r in radios.Values)
            {
                if (!r.portsOk || r.kindLog != Ecs.PortOutput) continue;
                Ecs.WritePort(em, r.signal, channelValue.TryGetValue(r.channel, out var v) ? v : 0f);
            }
        }
        /// <summary>0.19.8: the video identifier changes every tick; if it stopped changing for 2 s, the other vehicle's camera isn't being
        /// simulated (outside the atmosphere / another planet the game removes ActiveWormholeWorld from the blocks over there). Log only, once per block.</summary>
        static TypeIndex tiActive; static bool activeOk;
        /// <summary>0.19.8: block state for the log (ActiveWormholeWorld, part, live ports).</summary>
        static string Diag(EntityManager em, R r)
        {
            string isActive = "?";
            try { if (!activeOk) { activeOk = true; tiActive = TypeManager.GetTypeIndex(Il2CppType.Of<ActiveWormholeWorld>()); } isActive = Ecs.HasC(em, r.block, tiActive) ? "yes" : "NO"; } catch (Exception) { }
            var parent = Ecs.Parent(em, r.block);
            return "block ActiveWormholeWorld " + isActive + ", parent " + parent.Index + (Ecs.IsPart(em, parent) ? " (part)" : " (not a part)") + ", signal port " + (Ecs.Alive(em, r.signal) ? "alive" : "DEAD") + ", channel port " + (Ecs.Alive(em, r.channelP) ? "alive" : "DEAD") + "; signal port raw: " + Ecs.SignalDiagnostic(em, r.block);
        }
        static void Frozen(EntityManager em, R r, float v, float now)
        {
            if (Math.Abs(v) < 1e6f) { r.latestV = v; r.tLastV = now; r.frozenLog = false; return; }   // not video
            if (float.IsNaN(r.latestV) || v != r.latestV) { r.latestV = v; r.tLastV = now; if (r.frozenLog) { r.frozenLog = false; ModLog.Info("SC_LongRangeTransmitter " + r.block.Index + ": video source is changing again"); } return; }
            if (r.frozenLog || now - r.tLastV < 2f) return;
            r.frozenLog = true;
            string isActive = "?";
            try { if (!activeOk) { activeOk = true; tiActive = TypeManager.GetTypeIndex(Il2CppType.Of<ActiveWormholeWorld>()); } isActive = Ecs.HasC(em, r.block, tiActive) ? "yes" : "NO"; } catch (Exception) { }
            ModLog.Warn("SC_LongRangeTransmitter " + r.block.Index + ": the video it sends stopped changing 2 s ago (camera not simulated? block ActiveWormholeWorld: " + isActive + ") — the monitor on the other end shows static");
        }
        static readonly List<long> deadSet = new List<long>(); static readonly Dictionary<int, int> receivers = new Dictionary<int, int>(), senders = new Dictionary<int, int>();
        public static void Step(EntityManager em)
        {
            if (radios.Count == 0) return;
            deadSet.Clear();   // 0.31.35: kept between frames (it was a new list and two new dictionaries every frame)
            channelValue.Clear(); whoSends.Clear();
            // 1) channels and who sends
            foreach (var kv in radios)
            {
                var r = kv.Value;
                if (!Ecs.Alive(em, r.block)) { deadSet.Add(kv.Key); continue; }
                try
                {
                    if (r.portsOk && (!Ecs.Alive(em, r.signal) || !Ecs.Alive(em, r.channelP))) { r.portsOk = false; r.signal = Entity.Null; r.channelP = Entity.Null; r.kindLog = 255; }   // 0.19.3: ports rebuilt by the game
                    if (!r.portsOk)
                    {
                        foreach (var p in Ecs.Ports(em, r.block))
                        {
                            if (!Ecs.Port(em, p, out var t, out _, out _)) continue;
                            if (t == 3 && Ecs.IsNull(r.signal)) r.signal = p; else if (t == Ecs.PortInput && Ecs.IsNull(r.channelP)) r.channelP = p;
                        }
                        if (Ecs.IsNull(r.signal) || Ecs.IsNull(r.channelP)) { if (r.warnings++ < 2) ModLog.Warn("SC_LongRangeTransmitter " + r.block.Index + ": ports not found (" + Ecs.Ports(em, r.block).Length + ")"); continue; }
                        r.portsOk = true;
                    }
                    if (!r.painted && r.warnings == 0) { r.tPaint += UnityEngine.Time.deltaTime; if (r.tPaint > 1f) r.painted = Painting.Paint(em, r.block); }   // 0.19.2: default colour (DarkGray) on the renderers
                    ScreenSize(em, r, UnityEngine.Time.deltaTime);
                    Resolve(em, r);   // 0.31.11
                }
                catch (Exception ex) { if (r.warnings++ < 3) ModLog.Warn("SC_LongRangeTransmitter " + r.block.Index + ": " + ex.Message); }
            }
            foreach (var k in deadSet) radios.Remove(k);
            // 2) channel + signal copy (0.19.6: also runs inside the physics tick — see Copy)
            Copy(em, false);
            // 3) LEDs (0.19.2): per channel, how many send and how many receive
            receivers.Clear(); senders.Clear();
            foreach (var r in radios.Values)
            {
                if (!r.portsOk) continue;
                if (r.kindLog == Ecs.PortInput) { senders.TryGetValue(r.channel, out int e2); senders[r.channel] = e2 + 1; }
                else if (r.kindLog == Ecs.PortOutput) { receivers.TryGetValue(r.channel, out int c); receivers[r.channel] = c + 1; }
            }
            foreach (var r in radios.Values)
            {
                try
                {
                    r.tLed += UnityEngine.Time.deltaTime;
                    if (!Ecs.Alive(em, r.led) && r.tLed > 2f) { r.tLed = 0f; r.led = Led.Find(em, r.block); r.ledWritten = -1; }
                    if (Ecs.IsNull(r.led)) continue;
                    // 0.31.26 (item 21): the game's meaning [recon/EPC_SCWirelessTransmitter.cs:226-240, 266-277]. Sender: red = another sender
                    // on the channel, green = somebody receives, orange (Yellow) = nobody receives. Receiver: green = exactly 1 sender, red = 2+,
                    // off = none. No direction: off. (It was yellow = 2+ senders, red = nobody / not wired.)
                    int state;
                    senders.TryGetValue(r.channel, out int ns); receivers.TryGetValue(r.channel, out int nr);
                    if (!r.portsOk || (r.kindLog != Ecs.PortInput && r.kindLog != Ecs.PortOutput)) state = Led.Unlit;
                    else if (r.kindLog == Ecs.PortInput) state = ns > 1 ? Led.Red : nr > 0 ? Led.Green : Led.Yellow;
                    else state = ns == 1 ? Led.Green : ns >= 2 ? Led.Red : Led.Unlit;
                    r.ledWritten = Led.Paint(em, r.led, state, r.ledWritten);
                }
                catch (Exception) { }
            }
        }
    }
}
