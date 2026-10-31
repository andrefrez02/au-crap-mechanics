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
        sealed class R { public Entity block, signal, channelP, text; public bool portsOk, logged, searchedText; public int channel = int.MinValue, warnings, channelScreen = int.MinValue; public byte kindLog = 255; public float tText; public bool painted; public float tPaint, tLed = 9f; public Entity led; public int ledWritten = -1;
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
                        if (!em.HasComponent(f, ComponentType.ReadOnly(tiText))) continue;
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
        public static void Forget() { radios.Clear(); channelValue.Clear(); whoSends.Clear(); }
        public static void Track(Entity e) { radios[Ecs.K(e)] = new R { block = e }; ModLog.Info("SC_LongRangeTransmitter found: block " + e.Index + ":" + e.Version); }

        static unsafe byte RuntimeKind(EntityManager em, Entity p)
        {
            // 0.19.3 (crash dump 01/10, 16:47: AV reading [port+21] — the cached port had been destroyed when the game rebuilt the BP in the
            // garage): only reads if the entity still exists and has the component
            try { if (!Ecs.HasC(em, p, Ecs.tiPort)) return 255; var b = (byte*)em.GetComponentDataRawRO(p, Ecs.tiPort); return b == null ? (byte)255 : b[Layout.PortRuntime]; } catch (Exception) { return 255; }
        }

        /// <summary>0.19.0 (Andre: "the long transmitter isn't transmitting the camera's data"): the camera's video signal is a
        /// large identifier on the data port — Ecs.ReadInput zeroes values above 1e6 (the game's DataValue() does that to show
        /// numbers). Here the value goes raw (the 4 bytes), as the game's Wireless Transmitter passes it on.</summary>
        static unsafe bool ReadRaw(EntityManager em, Entity input, out float v)
        {
            v = 0f; var f = Ecs.Source(em, input, out _);
            if (Ecs.IsNull(f) || !Ecs.Alive(em, f) || !em.HasComponent(f, ComponentType.ReadOnly(Ecs.tiPort))) return false;
            var b = (byte*)em.GetComponentDataRawRO(f, Ecs.tiPort); if (b == null) return false;
            v = *(float*)(b + Layout.PortValue);
            // no filter: the identifier may have any bit (copies the 4 bytes as they are)
            if (!rawLog && Math.Abs(v) > 1e6f) { rawLog = true; ModLog.Info("SC_LongRangeTransmitter: passing a large signal (" + v.ToString("G9") + " — camera video?) through the channel"); }
            return true;
        }
        static bool rawLog;
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
                    bool hasChannel = Ecs.ReadInput(em, r.channelP, out var vc, out var source, out var trail); if (hasChannel) channel = (int)Math.Round(vc);
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
                    byte tr = RuntimeKind(em, r.signal);
                    if (tr != r.kindLog) { r.kindLog = tr; ModLog.Info("SC_LongRangeTransmitter " + r.block.Index + ": signal port is " + (tr == Ecs.PortInput ? "SENDING (wired to an output)" : tr == Ecs.PortOutput ? "RECEIVING (wired to an input)" : "not wired (type " + tr + ")") + " on channel " + r.channel); }
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
        public static void Step(EntityManager em)
        {
            if (radios.Count == 0) return;
            var deadSet = new List<long>();
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
                }
                catch (Exception ex) { if (r.warnings++ < 3) ModLog.Warn("SC_LongRangeTransmitter " + r.block.Index + ": " + ex.Message); }
            }
            foreach (var k in deadSet) radios.Remove(k);
            // 2) channel + signal copy (0.19.6: also runs inside the physics tick — see Copy)
            Copy(em, false);
            // 3) LEDs (0.19.2): per channel, how many there are and how many send
            var byChannel = new Dictionary<int, int>(); var senders = new Dictionary<int, int>();
            foreach (var r in radios.Values)
            {
                if (!r.portsOk) continue;
                byChannel.TryGetValue(r.channel, out int c); byChannel[r.channel] = c + 1;
                if (r.kindLog == Ecs.PortInput) { senders.TryGetValue(r.channel, out int e2); senders[r.channel] = e2 + 1; }
            }
            foreach (var r in radios.Values)
            {
                try
                {
                    r.tLed += UnityEngine.Time.deltaTime;
                    if (!Ecs.Alive(em, r.led) && r.tLed > 2f) { r.tLed = 0f; r.led = Led.Find(em, r.block); r.ledWritten = -1; }
                    if (Ecs.IsNull(r.led)) continue;
                    int state;
                    if (!r.portsOk || r.kindLog == 255 || r.kindLog > 1) state = Led.Red;
                    else
                    {
                        byChannel.TryGetValue(r.channel, out int n); senders.TryGetValue(r.channel, out int ne);
                        state = ne > 1 ? Led.Yellow : (n > 1 && ne == 1) ? Led.Green : Led.Red;
                    }
                    r.ledWritten = Led.Paint(em, r.led, state, r.ledWritten);
                }
                catch (Exception) { }
            }
        }
    }
}
