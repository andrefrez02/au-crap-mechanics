using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;
using Unity.Entities;
using Unity.Mathematics;

namespace CrapMechanics
{
    /// <summary>0.28.0 — the mod's own network channel. The game talks over SteamNetworkingSockets connections with its own packet format
    /// (Netcore), and there is no way to add a message type to it from a mod. The mod uses the other Steam API, SteamNetworkingMessages,
    /// on its own channel: it rides the same Steam relay but never touches the game's connection or its packets. Peers are the players
    /// the game itself lists (NetcoreClient entities: on the server, one per client; on a client, the server). Each side says HELLO
    /// (mod version and the list of installed plugins — differences are logged) until it hears from the other; sending first is also what
    /// makes Steam accept the other side's messages, so no session callback is needed.
    /// What goes through it: the turret seat AIM of a player seated on a CLIENT (only the server moves the turret; it never saw that
    /// player's mouse). [Multiplayer] test_loopback sends the aim through the same encode/decode path in a solo game, so the
    /// server side can be tested alone; net_log logs every message.
    /// 0.31.26 (p2_torre N1): also the LAYOUT of the mod's joints, server -> clients. A client gets the SpaceshipSingleton with
    /// _joints = null and _connectedJoints = null [recon_managed/NetcoreDoIncomingEventsSystem.cs:745], so it can never find, from the
    /// game's joint list, which blocks hang from a turret seat's plates or sit on a piston/bearing's top. The server sends, whenever it
    /// changes (and to a player the first time they say hello, and every 10 s), the SCGuid of each turret / piston / bearing block and the
    /// SCGuid of one block per piece on the other side; the client finds those blocks by SCGuid (the id the aim already relies on).
    /// New message type, so the protocol went from 1 to 2.</summary>
    internal static class ModNet
    {
        public const int Channel = 7741;
        const byte M0 = 0x43, M1 = 0x4D, Protocol = 2;   // "CM" + protocol version (0.31.26: 2 = + TTopo)
        const byte THello = 1, TAim = 2, TTopo = 3;
        const int SendReliable = 8 | 32, SendFast = 1 | 32;   // k_nSteamNetworkingSend_Reliable / _NoNagle, + _AutoRestartBrokenSession
        public static bool Log, Loopback;   // [Multiplayer] net_log / test_loopback

        class Peer { public ulong id; public string name = "?"; public float tHello = 99f, tSeen; public bool heard, listed; public string version, plugins; }
        static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        static ulong me; static bool meOk, dead; static int sendErrors, readErrors;
        static TypeIndex tiClient; static EntityQuery qClients; static bool qOk, qFailed;
        static float tPeers = 9f; static Il2CppStructArray<IntPtr> inbox;
        static string myPlugins;

        public struct Aim { public float yaw, pitch, time; public bool seated; public uint seq; public ulong from; }
        static readonly Dictionary<string, Aim> aims = new Dictionary<string, Aim>();   // turret block SCGuid (hex) -> last aim from a remote seat
        static readonly HashSet<string> aimLogged = new HashSet<string>();
        static uint seqOut; static float tAimOut; static string aimGuidOut; static bool seatedOut;

        // 0.31.26 (p2_torre N1): the joint layout the server sends. mode of a turret seat: 0 nothing turns (waiting / separating / welded),
        // 1 pieces on the plates (one per part), 2 WHOLE until aimed, 3 the whole ship turns, 4 active with nothing on the plates
        internal sealed class TopoPiece { public string guid, faces; public quaternion rn0; }
        internal sealed class TopoTurret { public string guid, sig; public byte mode; public int ver; public List<TopoPiece> pieces = new List<TopoPiece>(); }
        internal sealed class TopoJoint { public string guid, nbr, sig; public bool withEnd; public float extBase; public int ver; }
        static Dictionary<string, TopoTurret> topoTurrets = new Dictionary<string, TopoTurret>();   // turret block SCGuid -> layout (client)
        static Dictionary<string, TopoJoint> topoJoints = new Dictionary<string, TopoJoint>();      // piston/bearing block SCGuid -> top block (client)
        static int topoCounter; static bool topoHeard, topoFromLoop;
        static float tTopo = 9f, tTopoFull; static byte[] topoLast; static bool topoForce;

        // ---------------------------------------------------------------- cycle (once per frame, main thread)
        public static void Step(EntityManager em, float dt)
        {
            if (dead) return;
            bool net = Multi.Networked(em) && !Multi.FakeClient;
            if (!net)
            {
                if (peers.Count > 0) { ModLog.Info("multiplayer: mod channel closed (left the network)"); peers.Clear(); }
                if (!Loopback) aims.Clear();
                // [Multiplayer] test_loopback: the joint layout goes through the same encode/decode path (only decoded and logged: a solo game
                // draws from its own joints)
                if (Loopback && !Multi.Networked(em) && !Multi.IAmClient(em)) { try { TopologyStep(em, dt); } catch (Exception ex) { if (readErrors++ < 3) ModLog.Warn("multiplayer: joint layout (test_loopback): " + ex.Message); } }
                else
                {
                    if (topoTurrets.Count + topoJoints.Count > 0) { topoTurrets = new Dictionary<string, TopoTurret>(); topoJoints = new Dictionary<string, TopoJoint>(); }
                    topoLast = null; topoHeard = false;
                }
                return;
            }
            try
            {
                if (!meOk) { me = SteamUser.GetSteamID().m_SteamID; meOk = true; ModLog.Info("multiplayer: mod channel " + Channel + " on SteamNetworkingMessages, this machine is " + me); }
                tPeers += dt; if (tPeers >= 1f) { tPeers = 0f; ListPeers(em); }
                foreach (var p in peers.Values)
                {
                    p.tHello += dt;
                    if (p.tHello >= (p.heard ? 30f : 2f)) { p.tHello = 0f; SendHello(p.id); }
                }
                if (topoFromLoop) { topoFromLoop = false; topoHeard = false; topoTurrets = new Dictionary<string, TopoTurret>(); topoJoints = new Dictionary<string, TopoJoint>(); }   // (left over from test_loopback)
                Receive(em);
                if (!Multi.IAmClient(em)) TopologyStep(em, dt);   // 0.31.26 (p2_torre N1): the server tells the clients where the mod's joints are
            }
            catch (Exception ex) { if (readErrors++ < 3) ModLog.Warn("multiplayer: mod channel: " + ex.Message); if (readErrors > 50) { dead = true; ModLog.Warn("multiplayer: mod channel switched off after repeated errors — the turret seat of a client won't aim"); } }
        }

        static unsafe void ListPeers(EntityManager em)
        {
            if (!qOk)
            {
                if (qFailed) return;
                try
                {
                    tiClient = TypeManager.GetTypeIndex(Il2CppType.Of<NetcoreClient>());
                    var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiClient); qClients = em.CreateEntityQuery(a); qOk = true;
                }
                catch (Exception ex) { qFailed = true; ModLog.Warn("multiplayer: NetcoreClient not available (" + ex.Message + ") — peers are only learned from their messages"); return; }
            }
            foreach (var p in peers.Values) p.listed = false;
            foreach (var e in Ecs.QueryList(qClients))
            {
                var c = (byte*)Ecs.RO(em, e, tiClient); if (c == null) continue;
                ulong id = *(ulong*)c;   // NetcoreClient._steamID (+0)
                if (id == 0 || id == me) continue;
                if (c[220] == 0) continue;   // 0.31.26 (item 23): _isOnline (+220) false = dropped, entity kept for a reconnect [recon_managed/Netcore.cs:322-331] — no HELLO to them
                if (!peers.TryGetValue(id, out var p))
                {
                    peers[id] = p = new Peer { id = id, name = Fixed128(c + 8) };   // _username (+8, FixedString128Bytes)
                    ModLog.Info("multiplayer: player " + p.name + " (" + id + ") — saying hello on the mod channel");
                }
                p.listed = true; p.tSeen = 0f;
            }
            var gone = new List<ulong>();
            foreach (var p in peers.Values) if (!p.listed && (p.tSeen += 1f) > 5f) gone.Add(p.id);
            foreach (var id in gone) { ModLog.Info("multiplayer: player " + peers[id].name + " (" + id + ") left"); peers.Remove(id); }
        }
        static unsafe string Fixed128(byte* f)
        {
            int n = *(ushort*)f; if (n <= 0 || n > 125) return "?";
            return Encoding.UTF8.GetString(f + 2, n);
        }

        // ---------------------------------------------------------------- send
        static unsafe bool Send(ulong to, byte[] data, int len, int flags)
        {
            var id = new SteamNetworkingIdentity();
            var p = (byte*)&id; for (int i = 0; i < 136; i++) p[i] = 0;
            *(int*)p = 16; *(int*)(p + 4) = 8; *(ulong*)(p + 8) = to;   // k_ESteamNetworkingIdentityType_SteamID, cbSize 8, steamID64
            EResult r;
            fixed (byte* d = data) r = SteamNetworkingMessages.SendMessageToUser(ref id, (IntPtr)d, (uint)len, flags, Channel);
            if (r != EResult.k_EResultOK) { if (sendErrors++ < 5) ModLog.Warn("multiplayer: sending to " + to + " failed: " + r); return false; }
            return true;
        }
        static byte[] Header(byte type, int capacity) { var b = new byte[capacity]; b[0] = M0; b[1] = M1; b[2] = Protocol; b[3] = type; return b; }

        static void SendHello(ulong to)
        {
            var w = new Writer(Header(THello, 4096), 4);
            w.Str(PluginInfo.PluginVersion); w.Str(Plugins()); w.Byte((byte)(Multi.IAmClient(JointSystem.CurrentEm) ? 0 : 1));
            if (Send(to, w.b, w.n, SendReliable) && Log) ModLog.Info("net: HELLO -> " + to);
        }
        static string Plugins()
        {
            if (myPlugins != null) return myPlugins;
            var l = new List<string>();
            try { foreach (var kv in BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins) l.Add(kv.Key + "=" + kv.Value.ToString().Replace(";", ",").Replace("=", "-")); } catch (Exception) { }   // "GUID=Name Version" (PluginInfo.ToString)
            l.Sort(StringComparer.Ordinal);
            return myPlugins = string.Join(";", l);
        }

        /// <summary>Client side (or loopback): the aim of the turret seat this player sits in. Called every frame while seated (rate-limited
        /// here: 30/s while it moves, 4/s while still) and once with seated = false when the player stands up.</summary>
        public static void SendAim(EntityManager em, Entity turretBlock, float yaw, float pitch, bool seated)
        {
            string guid = Ecs.Guid(em, turretBlock); if (guid.Length != 32) return;
            float now = UnityEngine.Time.unscaledTime;
            bool changed = seated != seatedOut || guid != aimGuidOut;
            if (!changed && now - tAimOut < (seated ? 1f / 30f : 1e9f)) return;
            if (!changed && seated && now - tAimOut < 0.25f && Same(yaw, pitch)) return;
            tAimOut = now; aimGuidOut = guid; seatedOut = seated; lastYaw = yaw; lastPitch = pitch;
            var w = new Writer(Header(TAim, 64), 4);
            for (int i = 0; i < 16; i++) w.Byte(Convert.ToByte(guid.Substring(i * 2, 2), 16));
            w.F(yaw); w.F(pitch); w.Byte((byte)(seated ? 1 : 0)); w.U(++seqOut);
            if (Loopback && !Multi.Networked(em)) { Handle(em, meOk ? me : 1UL, w.b, w.n); return; }
            foreach (var p in peers.Values) Send(p.id, w.b, w.n, seated ? SendFast : SendReliable);
            if (Log) ModLog.Info("net: AIM -> " + peers.Count + " peer(s): " + guid.Substring(0, 8) + " yaw " + yaw.ToString("F1") + " pitch " + pitch.ToString("F1") + (seated ? "" : " (stood up)"));
        }
        static float lastYaw = float.NaN, lastPitch = float.NaN;
        static bool Same(float yaw, float pitch) => Math.Abs(yaw - lastYaw) < 0.05f && Math.Abs(pitch - lastPitch) < 0.05f;

        /// <summary>Server side: the latest aim sent for this turret block by a player seated there on another machine (fresh = less than 1 s old).</summary>
        public static bool RemoteAim(EntityManager em, Entity turretBlock, out float yaw, out float pitch)
        {
            yaw = 0f; pitch = 0f;
            if (aims.Count == 0) return false;
            string guid = Ecs.Guid(em, turretBlock);
            if (!aims.TryGetValue(guid, out var a)) return false;
            if (!a.seated || UnityEngine.Time.unscaledTime - a.time > 1f) return false;
            yaw = a.yaw; pitch = a.pitch; return true;
        }

        // ---------------------------------------------------------------- 0.31.26 (p2_torre N1): joint layout, server -> clients
        /// <summary>Server side, every 0.25 s: builds the layout and sends it (reliable) when it changed, when a player was just heard and
        /// every 10 s anyway. The bytes carry no counter, so an unchanged layout is recognised by comparing them.</summary>
        static void TopologyStep(EntityManager em, float dt)
        {
            tTopoFull += dt; tTopo += dt; if (tTopo < 0.25f) return; tTopo = 0f;
            bool loop = Loopback && !Multi.Networked(em);
            if (!loop && peers.Count == 0) { topoLast = null; return; }
            var ts = new List<TopoTurret>(); var js = new List<TopoJoint>();
            JointSystem.TopologyOut(em, ts, js);
            int cap = 8 + js.Count * 40;
            foreach (var t in ts) { cap += 20; foreach (var p in t.pieces) cap += 16 + 2 + Encoding.UTF8.GetByteCount(p.faces ?? "") + 16; }
            var w = new Writer(Header(TTopo, cap), 4);
            w.U16(ts.Count);
            foreach (var t in ts)
            {
                w.Guid(t.guid); w.Byte(t.mode); w.Byte((byte)t.pieces.Count);
                foreach (var p in t.pieces) { w.Guid(p.guid); w.Str(p.faces); w.F(p.rn0.value.x); w.F(p.rn0.value.y); w.F(p.rn0.value.z); w.F(p.rn0.value.w); }
            }
            w.U16(js.Count);
            foreach (var j in js) { w.Guid(j.guid); w.Guid(j.nbr); w.Byte((byte)(j.withEnd ? 1 : 0)); w.F(j.extBase); }
            bool same = topoLast != null && topoLast.Length == w.n;
            if (same) for (int i = 0; i < w.n; i++) if (topoLast[i] != w.b[i]) { same = false; break; }
            if (same && !topoForce && tTopoFull < 10f) return;
            topoLast = new byte[w.n]; Array.Copy(w.b, topoLast, w.n); topoForce = false; tTopoFull = 0f;
            if (loop) { Handle(em, meOk ? me : 1UL, w.b, w.n); }
            else foreach (var p in peers.Values) Send(p.id, w.b, w.n, SendReliable);
            if (!same || Log) ModLog.Info("multiplayer: joint layout " + (same ? "sent again" : "changed") + " — " + ts.Count + " turret seat(s), " + js.Count + " piston/bearing(s) " + (loop ? "(test_loopback: through the network path)" : "-> " + peers.Count + " player(s)"));
        }

        /// <summary>Client side: the server's layout for this turret block (null when it sent none — an older Crap Mechanics there).</summary>
        public static TopoTurret TopoTurretOf(string guid) => guid != null && topoTurrets.TryGetValue(guid, out var t) ? t : null;
        /// <summary>Client side: the server's top block for this piston/bearing block (null when it sent none).</summary>
        public static TopoJoint TopoJointOf(string guid) => guid != null && topoJoints.TryGetValue(guid, out var j) ? j : null;
        /// <summary>Did a layout arrive from the server in this game? (the client then trusts it over its own guesses)</summary>
        public static bool TopoHeard => topoHeard;

        static void ReadTopology(EntityManager em, ulong from, Reader r)
        {
            var nt = new Dictionary<string, TopoTurret>(); var nj = new Dictionary<string, TopoJoint>();
            int ct = r.U16();
            for (int i = 0; i < ct; i++)
            {
                var t = new TopoTurret { guid = r.Guid(), mode = r.Byte() }; int np = r.Byte();
                var sig = new StringBuilder().Append(t.mode);
                for (int k = 0; k < np; k++)
                {
                    var p = new TopoPiece { guid = r.Guid(), faces = r.Str() };
                    float x = r.F(), y = r.F(), z = r.F(), qw = r.F(); p.rn0 = new quaternion(x, y, z, qw);
                    t.pieces.Add(p); sig.Append('|').Append(p.guid).Append(p.faces).Append(x.ToString("R")).Append(y.ToString("R")).Append(z.ToString("R")).Append(qw.ToString("R"));
                }
                t.sig = sig.ToString(); nt[t.guid] = t;
            }
            int cj = r.U16();
            for (int i = 0; i < cj; i++)
            {
                var j = new TopoJoint { guid = r.Guid(), nbr = r.Guid(), withEnd = r.Byte() == 1, extBase = r.F() };
                j.sig = j.nbr + (j.withEnd ? "E" : "") + j.extBase.ToString("R"); nj[j.guid] = j;
            }
            bool loop = Loopback && !Multi.Networked(em);
            if (!Multi.IAmClient(em) && !loop) return;   // only a client draws from it
            // a record that did not change keeps its version, so the client re-resolves only what really changed
            int changed = 0;
            foreach (var t in nt.Values) { if (topoTurrets.TryGetValue(t.guid, out var o) && o.sig == t.sig) t.ver = o.ver; else { t.ver = ++topoCounter; changed++; } }
            foreach (var j in nj.Values) { if (topoJoints.TryGetValue(j.guid, out var o) && o.sig == j.sig) j.ver = o.ver; else { j.ver = ++topoCounter; changed++; } }
            changed += Math.Max(0, topoTurrets.Count - nt.Count) + Math.Max(0, topoJoints.Count - nj.Count);
            topoTurrets = nt; topoJoints = nj; topoFromLoop = loop;
            if (!topoHeard || changed > 0 || Log) ModLog.Info("multiplayer: joint layout from " + Name(from) + ": " + nt.Count + " turret seat(s), " + nj.Count + " piston/bearing(s)" + (changed > 0 ? ", " + changed + " changed" : "") + (loop ? " (test_loopback: decoded only)" : " — drawn from it"));
            topoHeard = true;
        }

        // ---------------------------------------------------------------- receive
        static unsafe void Receive(EntityManager em)
        {
            if (inbox == null) inbox = new Il2CppStructArray<IntPtr>(32);
            for (int batch = 0; batch < 8; batch++)
            {
                int n = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, inbox, 32);
                if (n <= 0) return;
                for (int i = 0; i < n; i++)
                {
                    IntPtr m = inbox[i]; if (m == IntPtr.Zero) continue;
                    try
                    {
                        var b = (byte*)m;   // SteamNetworkingMessage_t: m_pData +0, m_cbSize +8, m_identityPeer +16 (steamID64 at +8 inside it)
                        var data = *(byte**)b; int size = *(int*)(b + 8); ulong from = *(ulong*)(b + 24);
                        if (data != null && size > 0 && size <= 65536)
                        {
                            var copy = new byte[size]; for (int k = 0; k < size; k++) copy[k] = data[k];
                            Handle(em, from, copy, size);
                        }
                    }
                    catch (Exception ex) { if (readErrors++ < 5) ModLog.Warn("multiplayer: bad mod message: " + ex.Message); }
                    finally { SteamNetworkingMessage_t.Release(m); }
                }
                if (n < 32) return;
            }
        }

        static void Handle(EntityManager em, ulong from, byte[] d, int len)
        {
            if (len < 4 || d[0] != M0 || d[1] != M1) return;
            if (d[2] != Protocol) { if (readErrors++ < 3) ModLog.Warn("multiplayer: player " + from + " uses mod channel protocol " + d[2] + " (this one: " + Protocol + ") — install the same Crap Mechanics version on every machine"); return; }
            var r = new Reader(d, len, 4);
            if (d[3] == THello)
            {
                string ver = r.Str(), plugins = r.Str(); bool theirServer = r.Byte() == 1;
                if (!peers.TryGetValue(from, out var p)) peers[from] = p = new Peer { id = from };
                bool first = !p.heard; p.heard = true; p.version = ver; p.plugins = plugins;
                if (first)
                {
                    ModLog.Info("multiplayer: mod channel up with " + p.name + " (" + from + ", " + (theirServer ? "server" : "client") + "): Crap Mechanics " + ver + (ver == PluginInfo.PluginVersion ? " (same as here)" : ""));
                    if (ver != PluginInfo.PluginVersion) ModLog.Warn("multiplayer: " + p.name + " has Crap Mechanics " + ver + " and this machine " + PluginInfo.PluginVersion + " — part names and behaviour may not match; everyone should run the same version");
                    ComparePlugins(p.name, plugins);
                    p.tHello = 99f;   // answers right away, so the other side doesn't wait 2 s
                    topoForce = true;   // 0.31.26 (p2_torre N1): a player just heard gets the joint layout right away
                }
                if (Log) ModLog.Info("net: HELLO <- " + from + " (" + ver + ")");
                return;
            }
            if (d[3] == TAim)
            {
                var sb = new StringBuilder(32); for (int i = 0; i < 16; i++) sb.Append(r.Byte().ToString("x2"));
                string guid = sb.ToString(); float yaw = r.F(), pitch = r.F(); bool seated = r.Byte() == 1; uint seq = r.U();
                if (!Multi.Authoritative(em)) return;   // only the server moves turrets
                if (aims.TryGetValue(guid, out var old) && old.from == from && seq <= old.seq && seq + 1000 > old.seq) return;   // late/duplicate
                aims[guid] = new Aim { yaw = yaw, pitch = pitch, seated = seated, seq = seq, from = from, time = UnityEngine.Time.unscaledTime };
                if (aimLogged.Add(guid + from)) ModLog.Info("multiplayer: turret seat " + guid.Substring(0, 8) + "… is aimed by player " + Name(from) + (Loopback && !Multi.Networked(em) ? " (test_loopback: the local aim goes through the network path)" : ""));
                if (!seated) { aimLogged.Remove(guid + from); ModLog.Info("multiplayer: player " + Name(from) + " left turret seat " + guid.Substring(0, 8) + "…"); }
                if (Log) ModLog.Info("net: AIM <- " + from + ": " + guid.Substring(0, 8) + " yaw " + yaw.ToString("F1") + " pitch " + pitch.ToString("F1") + " #" + seq);
                return;
            }
            if (d[3] == TTopo) { ReadTopology(em, from, r); return; }   // 0.31.26 (p2_torre N1)
        }
        static string Name(ulong id) => peers.TryGetValue(id, out var p) ? p.name + " (" + id + ")" : id.ToString();

        static void ComparePlugins(string who, string theirs)
        {
            var mine = Parse(Plugins()); var them = Parse(theirs);
            var diffs = new List<string>();
            foreach (var kv in mine) { if (!them.TryGetValue(kv.Key, out var v)) diffs.Add(kv.Key + " " + kv.Value + " only here"); else if (v != kv.Value) diffs.Add(kv.Key + " " + kv.Value + " here, " + v + " there"); }
            foreach (var kv in them) if (!mine.ContainsKey(kv.Key)) diffs.Add(kv.Key + " " + kv.Value + " only there");
            if (diffs.Count == 0) ModLog.Info("multiplayer: " + who + " has the same BepInEx plugins (" + mine.Count + ")");
            else ModLog.Warn("multiplayer: plugins differ from " + who + ": " + string.Join("; ", diffs));
        }
        static Dictionary<string, string> Parse(string s)
        {
            var d = new Dictionary<string, string>();
            foreach (var it in (s ?? "").Split(';')) { int i = it.IndexOf('='); if (i > 0) d[it.Substring(0, i)] = it.Substring(i + 1); }
            return d;
        }

        public static void Forget() { aims.Clear(); aimLogged.Clear(); aimGuidOut = null; seatedOut = false; topoLast = null; }   // 0.31.26: the layout is sent again (a client keeps the one it has: it is keyed by SCGuid)

        // ---------------------------------------------------------------- tiny little-endian writer/reader
        struct Writer
        {
            public byte[] b; public int n;
            public Writer(byte[] buf, int start) { b = buf; n = start; }
            public void Byte(byte v) { b[n++] = v; }
            public void U(uint v) { BitConverter.TryWriteBytes(new Span<byte>(b, n, 4), v); n += 4; }
            public void F(float v) { BitConverter.TryWriteBytes(new Span<byte>(b, n, 4), v); n += 4; }
            public void U16(int v) { b[n++] = (byte)v; b[n++] = (byte)(v >> 8); }
            public void Guid(string hex) { for (int i = 0; i < 16; i++) b[n++] = hex != null && hex.Length == 32 ? Convert.ToByte(hex.Substring(i * 2, 2), 16) : (byte)0; }   // SCGuid, 16 bytes
            public void Str(string s)
            {
                var u = Encoding.UTF8.GetBytes(s ?? ""); int l = Math.Min(u.Length, Math.Min(ushort.MaxValue, b.Length - n - 2));
                b[n++] = (byte)l; b[n++] = (byte)(l >> 8); Array.Copy(u, 0, b, n, l); n += l;
            }
        }
        struct Reader
        {
            readonly byte[] b; readonly int len; int n;
            public Reader(byte[] buf, int length, int start) { b = buf; len = length; n = start; }
            void Need(int k) { if (n + k > len) throw new InvalidOperationException("message too short"); }
            public byte Byte() { Need(1); return b[n++]; }
            public uint U() { Need(4); var v = BitConverter.ToUInt32(b, n); n += 4; return v; }
            public float F() { Need(4); var v = BitConverter.ToSingle(b, n); n += 4; return v; }
            public int U16() { Need(2); int v = b[n] | (b[n + 1] << 8); n += 2; return v; }
            public string Guid() { Need(16); var sb = new StringBuilder(32); for (int i = 0; i < 16; i++) sb.Append(b[n + i].ToString("x2")); n += 16; return sb.ToString(); }
            public string Str() { Need(2); int l = b[n] | (b[n + 1] << 8); n += 2; Need(l); var s = Encoding.UTF8.GetString(b, n, l); n += l; return s; }
        }
    }
}
