using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Unity.Entities;
using UnityEngine;

namespace CrapMechanics
{
    /// <summary>0.15.0 — Drive Shaft (Andre: "the motor splits its force per bearing", "own cable", "Differential", "reverse").
    /// The game only has 4 cable types and the fitting rule and the drawing come from its compiled code, so the Drive Shaft is a
    /// PLASMA-type cable of the mod (its own item, iron colour). The mod reads the transmission by itself: from the motor's shaft output (plasma port)
    /// it walks the cable's cells — only Drive Shaft cells count — up to the port at the other end:
    ///   bearing (untyped ports) = driven by that motor; Differential = continues through all its other ports;
    ///   Shaft Reverser = continues through the other port with the direction reversed; anything else (the game's plasma cable, plasma
    ///   generator...) = doesn't transmit (warning in the log once).
    /// The motor's force (x force_pool) is split among the bearings it reaches.</summary>
    internal static partial class JointSystem
    {
        public const string CABLE = "SC_DriveShaft", DIFFERENTIAL = "SC_Differential", REVERSER = "SC_ShaftReverser", TRANSMITTER = "SC_ShaftTransmitter";
        public const byte PortShaft = 4, PortUntyped = 5;
        static TypeIndex tiCell; static bool cellOk, cellFailed; static ulong hCable;
        /// <summary>0.17.0: network = motors + bearings linked by the shafts (motors that reach the same bearing become ONE network and add up).</summary>
        internal class Net
        {
            public List<Motor> motors = new List<Motor>(); public List<long> bearings = new List<long>();
            public Motor rep; public bool powered, conflict; public float bearingForce, velMax; public string name = ""; public string conflictLog = "";
        }
        /// <summary>A driven bearing: by which motors (and in which direction: -1 = went through a Reverser) and the combined value this frame.</summary>
        internal class Driven { public List<Motor> ms = new List<Motor>(); public List<float> ss = new List<float>(); public string path; public Net net; public float val; }
        static readonly Dictionary<long, Driven> driven = new Dictionary<long, Driven>();
        static readonly List<Net> nets = new List<Net>();
        static readonly HashSet<string> axisWarnings = new HashSet<string>();
        static readonly Dictionary<string, int> netsLog = new Dictionary<string, int>();
        static float tAxesMap = 9f;
        // how many INPUT ports each block has (the first ones in the prefab); the rest are outputs. Transmitter: its single port serves both.
        const int DifferentialInputs = 4, ReverserInputs = 1;

        static bool PrepareCell()
        {
            if (cellOk) return true; if (cellFailed) return false;
            try { tiCell = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeCableCell>()); hCable = Ecs.HashPrefab(CABLE); cellOk = true; return true; }
            catch (Exception ex) { cellFailed = true; ModLog.Error("drive shaft: cable cell type: " + ex.Message); return false; }
        }

        static void WarnLimited(string s) { if (axisWarnings.Add(s)) ModLog.Warn("drive shaft: " + s); }

        /// <summary>Mod prefab name (without " M") of a block, or "" if it belongs to the game.</summary>
        static string BlockName(EntityManager em, Entity block)
        {
            try { ulong h = Ecs.Prefab(em, block); return byHash.TryGetValue(h, out var i) ? PREFABS[i].name : ""; } catch (Exception) { return ""; }
        }
        static int PortIndexOf(EntityManager em, Entity owner, Entity port)
        {
            var ps = Ecs.Ports(em, owner); for (int i = 0; i < ps.Length; i++) if (Same(ps[i], port)) return i; return -1;
        }

        /// <summary>From the outside of a port to the port at the other end of ONE stretch (cable or touching). axisOnly = every cell
        /// is Drive Shaft; hasCable = went through a cell; cableKind = SpaceshipCableType of the 1st cell.</summary>
        static unsafe Entity OtherEnd(EntityManager em, Entity port, out bool axisOnly, out bool hasCable, out byte cableKind)
        {
            axisOnly = true; hasCable = false; cableKind = 255;
            if (!PrepareCell() || !Ecs.RawPortPub(em, port, out _, out _, out var end, out var persist)) return Entity.Null;
            if (Ecs.IsNull(persist) || !Ecs.Alive(em, persist)) return Entity.Null;
            if (Ecs.HasC(em, persist, Ecs.tiPort)) return persist;   // port touching the other one directly
            Entity prev = port, cur = persist, lastPort = Entity.Null;
            for (int i = 0; i < 4096; i++)
            {
                if (Ecs.IsNull(cur) || !Ecs.Alive(em, cur)) break;
                if (Ecs.HasC(em, cur, Ecs.tiPort)) { lastPort = cur; break; }
                if (!Ecs.HasC(em, cur, tiCell)) break;
                hasCable = true;
                var c = (byte*)em.GetComponentDataRawRO(cur, tiCell); if (c == null) break;
                if (cableKind == 255) cableKind = c[1];
                if (Ecs.Prefab(em, cur) != hCable) axisOnly = false;
                var fwd = *(Entity*)(c + 16); var bwd = *(Entity*)(c + 24);
                var nxt = Same(fwd, prev) ? bwd : fwd;
                if (Same(nxt, prev)) break;
                prev = cur; cur = nxt;
            }
            if (!Ecs.IsNull(lastPort)) return lastPort;
            return (!Ecs.IsNull(end) && !Same(end, port)) ? end : Entity.Null;
        }

        /// <summary>0.17.0: follows the shaft through Frame With Ports (port on one face -> paired port on the opposite face, like data:
        /// Ecs.Source) up to the port of a block that isn't a frame. plasma = some stretch is a plasma cable (Drive Shaft or the game's);
        /// axisOnly = no stretch is a game cable; via = frames crossed (for the log).</summary>
        static Entity Follow(EntityManager em, Entity port, out bool axisOnly, out bool plasma, out bool hasCable, out string via)
        {
            axisOnly = true; plasma = false; hasCable = false; via = "";
            var seen = new HashSet<long>(); Entity cur = port;
            for (int hop = 0; hop < 24; hop++)
            {
                if (!seen.Add(Ecs.K(cur))) return Entity.Null;
                var end = OtherEnd(em, cur, out bool sE, out bool tc, out byte tp);
                if (tc) { hasCable = true; if (tp == 3) plasma = true; if (!sE) axisOnly = false; }
                if (Ecs.IsNull(end)) return Entity.Null;
                var owner = Ecs.Parent(em, end);
                if (Ecs.IsNull(owner) || !Ecs.HasC(em, owner, Ecs.tiFramePorts)) return end;
                var ps = Ecs.Ports(em, owner); int i = Array.FindIndex(ps, x => Same(x, end));
                if (i < 0 || ps.Length < 2) return Entity.Null;
                cur = ps[(i + ps.Length / 2) % ps.Length];
                via += " -> frame " + owner.Index;
            }
            return Entity.Null;
        }

        /// <summary>Is the bearing's port on a shaft? (some stretch of plasma cable, or it reaches a shaft port, through frames)</summary>
        static bool PortOnAxis(EntityManager em, Entity port)
        {
            var otherOne = Follow(em, port, out _, out bool plasma, out _, out _);
            if (plasma) return true;
            if (Ecs.IsNull(otherOne)) return false;
            return Ecs.Port(em, otherOne, out var t, out _, out _) && t == PortShaft;
        }

        static readonly Dictionary<long, bool> onAxis = new Dictionary<long, bool>();
        static bool PortOnAxisCache(EntityManager em, Entity p)
        {
            long k = Ecs.K(p); if (onAxis.TryGetValue(k, out var b)) return b;
            b = false; try { b = PortOnAxis(em, p); } catch (Exception) { }
            onAxis[k] = b; return b;
        }

        // 0.16.0: Shaft Transmitter = copy of the game's wireless transmitter (the game's lever and channel number; channel at
        // SCTypeWirelessTransmitter +0x20, Ecs.Channel). SHAFT ones on the same channel link with each other, on any ship (like the data ones);
        // separate from the data ones: only the mod's go in here, and their port is plasma (it doesn't exchange data with the game's).
        static ulong hTransm, hTransmM; static List<Entity> transmList;
        // 0.19.2: Shaft Transmitter LEDs — per channel: how many transmitters and how many receive a motor's shaft (senders)
        sealed class LedT { public Entity led; public int written = -1; public float t = 9f; }
        static readonly Dictionary<long, LedT> transmLeds = new Dictionary<long, LedT>();
        static readonly HashSet<long> shaftSenders = new HashSet<long>();
        static void TransmitterLeds(EntityManager em)
        {
            var byChannel = new Dictionary<int, int>(); var tx = new Dictionary<int, int>();
            var list = TransmitterList(em);
            foreach (var t in list)
            {
                if (!Ecs.Alive(em, t)) { transmList = null; continue; }   // 0.19.3: list rebuilt if a transmitter vanished (garage preview)
                int c = Ecs.Channel(em, t); if (c < 0) continue;
                byChannel.TryGetValue(c, out int n); byChannel[c] = n + 1;
                if (shaftSenders.Contains(Ecs.K(t))) { tx.TryGetValue(c, out int e2); tx[c] = e2 + 1; }
            }
            foreach (var t in list)
            {
                if (!Ecs.Alive(em, t)) continue;
                long k = Ecs.K(t);
                if (!transmLeds.TryGetValue(k, out var l)) transmLeds[k] = l = new LedT();
                l.t += UnityEngine.Time.deltaTime;
                if (!Ecs.Alive(em, l.led) && l.t > 2f) { l.t = 0f; l.led = Led.Find(em, t); l.written = -1; }
                if (Ecs.IsNull(l.led)) continue;
                int c = Ecs.Channel(em, t); byChannel.TryGetValue(c, out int n); tx.TryGetValue(c, out int ne);
                int state = ne > 1 ? Led.Yellow : (n > 1 && ne == 1) ? Led.Green : Led.Red;
                l.written = Led.Paint(em, l.led, state, l.written);
            }
        }
        static readonly Dictionary<long, int> channelLog = new Dictionary<long, int>();
        static List<Entity> TransmitterList(EntityManager em)
        {
            if (transmList != null) return transmList;
            transmList = new List<Entity>();
            if (hTransm == 0) { hTransm = Ecs.HashPrefab(TRANSMITTER); hTransmM = Ecs.HashPrefab(TRANSMITTER + " M"); }
            foreach (var e in Ecs.Transmitters(em)) { ulong h = Ecs.Prefab(em, e); if (h == hTransm || h == hTransmM) transmList.Add(e); }
            return transmList;
        }

        /// <summary>Once every 0.25 s: for each motor, which bearings it reaches (and in which direction); then groups into networks the
        /// motors that reach the same bearings.</summary>
        static void MapAxes(EntityManager em, float dt)
        {
            tAxesMap += dt; if (tAxesMap < 0.25f) { RefreshNetworks(); try { TransmitterLeds(em); } catch (Exception) { } return; }
            tAxesMap = 0f;
            driven.Clear(); nets.Clear(); onAxis.Clear(); transmList = null; shaftSenders.Clear();
            if (!PrepareCell()) return;
            var reached = new Dictionary<Motor, List<long>>();
            foreach (var m in motors.Values)
            {
                if (!m.portsOk || Ecs.IsNull(m.output)) continue;
                var list = new List<long>(); reached[m] = list;
                var seenSet = new HashSet<long> { Ecs.K(m.block) };
                try { Walk(em, m, m.output, 1f, seenSet, 0, m.name + " " + m.block.Index, list); } catch (Exception ex) { WarnLimited(m.name + ": " + ex.Message); }
            }
            // networks: motors that share a bearing stay together (simple union)
            var netOf = new Dictionary<Motor, Net>();
            foreach (var kv in reached)
            {
                var r = new Net(); r.motors.Add(kv.Key); netOf[kv.Key] = r; nets.Add(r);
            }
            foreach (var ac in driven.Values)
            {
                Net target = null;
                foreach (var m in ac.ms)
                {
                    var r = netOf[m]; if (target == null) { target = r; continue; }
                    if (r == target) continue;
                    foreach (var m2 in r.motors) { target.motors.Add(m2); netOf[m2] = target; }
                    r.motors.Clear(); nets.Remove(r);
                }
            }
            foreach (var kv in driven) { var r = netOf[kv.Value.ms[0]]; kv.Value.net = r; r.bearings.Add(kv.Key); }
            foreach (var r in nets)
            {
                r.motors.Sort((a, b) => a.block.Index.CompareTo(b.block.Index));
                var sb = new System.Text.StringBuilder();
                foreach (var m in r.motors) sb.Append(sb.Length == 0 ? "" : " + ").Append(m.name).Append(' ').Append(m.block.Index);
                r.name = sb.ToString();
                string key = r.name;
                if (!netsLog.TryGetValue(key, out int nlog) || nlog != r.bearings.Count)
                {
                    netsLog[key] = r.bearings.Count;
                    ModLog.Info(r.name + ": drives " + r.bearings.Count + " bearing(s) through Drive Shafts" + (r.motors.Count > 1 ? " (" + r.motors.Count + " motors on the same shaft: their force adds up)" : "") + (r.bearings.Count > 1 ? ", force split " + r.bearings.Count + " ways" : ""));
                }
            }
            RefreshNetworks();
        }

        /// <summary>Walks the shaft leaving an OUTPUT port. Only enters a block through the INPUT port (orange) and leaves through the output ones
        /// (purple); a bearing accepts any port. A motor reaching the same bearing again: keeps the direction (conflict if opposite).</summary>
        static void Walk(EntityManager em, Motor m, Entity port, float direction, HashSet<long> seenSet, int depth, string path, List<long> foundAll)
        {
            if (depth > 24) return;
            var end = Follow(em, port, out bool axisOnly, out _, out bool hasCable, out string via);
            if (Ecs.IsNull(end)) return;
            var owner = Ecs.Parent(em, end); if (Ecs.IsNull(owner)) return;
            string name = BlockName(em, owner);
            path += via;
            if (hasCable && !axisOnly) { WarnLimited(m.name + " " + m.block.Index + ": a game cable (not a Drive Shaft) is in the way to " + (name.Length > 0 ? name : "block " + owner.Index) + " — it does not transmit"); return; }
            if (name == "SC_Bearing" || name == "SC_GyroBearing" || name == "SC_MiniBearing" || name == "SC_MiniGyroBearing")   // 0.20.4 (Andre: "the mini bearing isn't accepting a shaft"): only SC_Bearing was recognised
            {
                if (SlipRings.IsPassThrough(em, owner, end, name)) { WarnLimited(m.name + " " + m.block.Index + ": its shaft goes into a PASS-THROUGH port (slip ring) of the " + name + " " + owner.Index + " — connect it to one of the ports underneath"); return; }   // 0.22.0
                long kb = Ecs.K(owner);
                if (!driven.TryGetValue(kb, out var ac)) { ac = new Driven { path = path + " -> bearing" + (direction < 0 ? " (reversed)" : "") }; driven[kb] = ac; }
                for (int i = 0; i < ac.ms.Count; i++) if (ac.ms[i] == m && ac.ss[i] == direction) return;   // same path again
                ac.ms.Add(m); ac.ss.Add(direction); if (!foundAll.Contains(kb)) foundAll.Add(kb);
                return;
            }
            if (!seenSet.Add(Ecs.K(owner))) return;
            if (name == DIFFERENTIAL || name == REVERSER)
            {
                int nIn = name == REVERSER ? ReverserInputs : DifferentialInputs;
                int idx = PortIndexOf(em, owner, end);
                string shortOne = name == REVERSER ? "Shaft Reverser" : "Differential";
                if (idx < 0 || idx >= nIn) { WarnLimited(m.name + " " + m.block.Index + ": its shaft goes into an OUTPUT (purple) of the " + shortOne + " " + owner.Index + " — connect it to an input (orange)"); return; }
                float s = name == REVERSER ? -direction : direction;
                var ps = Ecs.Ports(em, owner);
                for (int i = nIn; i < ps.Length; i++) Walk(em, m, ps[i], s, seenSet, depth + 1, path + " -> " + (name == REVERSER ? "reverser" : "differential"), foundAll);
                return;
            }
            if (name == TRANSMITTER)
            {
                int channel = Ecs.Channel(em, owner);
                if (channel < 0) { WarnLimited("shaft transmitter " + owner.Index + ": channel not readable"); return; }
                long kd = Ecs.K(owner); shaftSenders.Add(kd);
                if (!channelLog.TryGetValue(kd, out int cl) || cl != channel) { channelLog[kd] = channel; ModLog.Info(m.name + " " + m.block.Index + ": shaft goes into Shaft Transmitter " + owner.Index + " on channel " + channel); }
                int others = 0;
                foreach (var t in TransmitterList(em))
                {
                    if (Same(t, owner)) continue;
                    if (Ecs.Channel(em, t) != channel || !seenSet.Add(Ecs.K(t))) continue;
                    others++;
                    foreach (var p in Ecs.Ports(em, t)) Walk(em, m, p, direction, seenSet, depth + 1, path + " -> transmitter ch " + channel, foundAll);
                }
                if (others == 0) WarnLimited(m.name + " " + m.block.Index + ": no other Shaft Transmitter on channel " + channel + " (set the same channel on the receiving one)");
                return;
            }
            if (PREFABS_MOTOR(name)) { WarnLimited(m.name + " " + m.block.Index + ": its shaft goes into the OUTPUT of another motor (" + name + " " + owner.Index + ") — to join motors, connect each one to an input of a Differential"); return; }
            WarnLimited(m.name + " " + m.block.Index + ": its shaft ends at " + (name.Length > 0 ? name : "a game part (block " + owner.Index + ")") + " — Drive Shafts only turn bearings, Differentials, Shaft Reversers and Shaft Transmitters (Frames With Ports pass it through)");
        }
        static bool PREFABS_MOTOR(string name) => name != null && name.StartsWith("SC_Motor");

        /// <summary>Every frame (signals change): force, speed and signal of each network. A motor without power/fuel doesn't count.
        /// Force = sum (x force_pool) / bearings; max speed and signal = average weighted by each motor's force.
        /// Opposite signals reaching the same bearing (motors reversed, or the same motor with and without a Reverser) = LOCKED network.</summary>
        static void RefreshNetworks()
        {
            foreach (var r in nets)
            {
                float ftot = 0f, vel = 0f; Motor rep = null; float frep = -1f;
                foreach (var m in r.motors)
                {
                    float f = m.powered ? m.effectiveImpulse : 0f;
                    ftot += f; vel += f * m.velMax;
                    if (f > frep) { frep = f; rep = m; }
                }
                r.rep = rep; r.powered = ftot > 0f;
                r.velMax = ftot > 0f ? vel / ftot : (rep != null ? rep.velMax : 0f);
                r.bearingForce = r.bearings.Count > 0 ? ftot * ForceReserve / r.bearings.Count : 0f;
                bool conflict = false; string why = "";
                foreach (var kb in r.bearings)
                {
                    var ac = driven[kb]; float num = 0f; bool pos = false, neg = false;
                    for (int i = 0; i < ac.ms.Count; i++)
                    {
                        var m = ac.ms[i]; float f = m.powered ? m.effectiveImpulse : 0f; if (f <= 0f) continue;
                        float v = m.signal * ac.ss[i]; num += f * v;
                        if (v > 0.001f) pos = true; else if (v < -0.001f) neg = true;
                    }
                    ac.val = ftot > 0f ? num / ftot : 0f;
                    if (pos && neg && !conflict) { conflict = true; why = "opposite shaft signals reach bearing " + (kb >> 32); }
                }
                if (conflict != r.conflict) ModLog.Info(r.name + ": " + (conflict ? "LOCKED — " + why + " (motors turning against each other, or a Shaft Reverser on one path only)" : "unlocked"));
                r.conflict = conflict; r.conflictLog = why;
            }
        }

        /// <summary>Components Framework configure: the Drive Shaft copies the meshes (and colliders) of the game's plasma cable.</summary>
        public static void PrepareCable(EPC_SpaceshipComponent epc, Core core)
        {
            if (epc == null || epc.name != CABLE) return;
            try
            {
                var mine = epc.TryCast<EPC_SCCable>(); var game = core != null ? core._prefabCablePlasma : null;
                if (mine == null || game == null) { ModLog.Warn("drive shaft: " + (mine == null ? "the prefab is not a cable" : "the game's plasma cable was not found") + " — it will be invisible"); return; }
                int n = 0;
                foreach (var pi in typeof(EPC_SCCable).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (pi.PropertyType != typeof(Mesh) || !pi.CanWrite || !pi.CanRead) continue;
                    try { pi.SetValue(mine, pi.GetValue(game)); n++; } catch (Exception) { }
                }
                if (mine._renderer != null && game._renderer != null) mine._renderer._mesh = game._renderer._mesh;
                if (mine._cableHolderRenderer != null && game._cableHolderRenderer != null) mine._cableHolderRenderer._mesh = game._cableHolderRenderer._mesh;
                CopyCollider(mine._cableColliderStraight, game._cableColliderStraight); CopyCollider(mine._cableColliderCurved, game._cableColliderCurved);
                ModLog.Info("drive shaft: " + n + " cable meshes copied from the game's plasma cable (type " + mine._type + ", color " + mine._defaultPaintableColor + ")");
            }
            catch (Exception ex) { ModLog.Warn("drive shaft: could not copy the plasma cable meshes: " + ex.Message); }
        }
        static void CopyCollider(DOTSCollider mine, DOTSCollider game)
        {
            try
            {
                var a = mine != null ? mine.TryCast<DOTSColliderConvex>() : null; var b = game != null ? game.TryCast<DOTSColliderConvex>() : null;
                if (a != null && b != null) a._mesh = b._mesh;
            }
            catch (Exception) { }
        }
    }
}
