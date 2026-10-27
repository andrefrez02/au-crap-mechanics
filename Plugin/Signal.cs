using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>0.24.0 (Andre: "the wireless transmitter doesn't always work on a separated joint... it sort of stays without signal (yellow led)").
    ///
    /// The amber LED does NOT come from the wireless pairing: SCTick_WirelessTransmitter redoes the pairing from SCRATCH every tick (scan per
    /// channel, range of 2000 units, no cache, no filter per ship or per part), so two transmitters on different parts see
    /// each other normally and there is no "initialisation" that could lose the race.
    ///
    /// What gets stuck is the port's DIRECTION. SpaceshipElectricPort has _setupType (+20) and _runtimeType (+21), and in EPC_SpaceshipComponent
    /// both are born equal — on the transmitter, DataInOut (3). What resolves whether the port is an input or an output is
    /// SpaceshipPostUpdatePortsRuntimeEndEntities, whose two jobs only take ports marked with the tag
    /// SpaceshipElectricPortUpdateRuntimeEndEntity — and which REMOVE the tag when done. It's one-shot: if it fails, nothing reschedules it.
    /// And UpdateRuntimeEndsJob walks the cable only INSIDE the same part (it compares each cell's grandparent with the part's root and aborts if
    /// it changes); when that fails, _runtimeEndEntity stays pointing at a cable cell, UpdatePortsDataInOutJob doesn't find the port at the other
    /// end and _runtimeType never leaves DataInOut. The transmitter's job, seeing DataInOut, paints the LED amber and returns. Forever.
    ///
    /// The race: joint breaks -> the part is marked -> the ports are marked -> the system resolves. If that runs while the parent
    /// hierarchy is still in transition right after the split, the grandparent test fails and the only chance has already been spent.
    ///
    /// Fix: when a transmitter shows up with its port stuck in DataInOut, mark the port with the tag again — the game's official path,
    /// the same one SpaceshipPostCheckConsistency_1 uses. The game recalculates on the next update and removes the tag by itself. It only acts on what is
    /// broken, and gives up after a few attempts (if it keeps failing it's because the cable really crosses the joint — then it's the game's
    /// structure, not a race, and the log says so).</summary>
    internal static class Signal
    {
        public static bool Relink = true;        // [Signal] fix_stuck_ports
        public static int MaxAttempts = 3;
        /// <summary>UIManager.OnSpaceshipLaunch (RadarIcon.OnLaunch). Only touches things with the simulation running: IN THE GARAGE the ports are
        /// unresolved by nature (the block isn't even in a part), so the mod would mark everything again and make a structural change in the middle
        /// of the frame — that's what froze the game in 0.24.0 on 10/10.</summary>
        public static bool Launched;

        const byte DataInOut = 3;
        static TypeIndex tiTag, tiPrefabTag; static bool ok, failed; static float t = 9f;
        static readonly Dictionary<long, int> stuckSet = new Dictionary<long, int>();
        static readonly Dictionary<long, int> attempts = new Dictionary<long, int>();
        static readonly HashSet<long> gaveUp = new HashSet<long>();

        static bool Prepare()
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiTag = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipElectricPortUpdateRuntimeEndEntity>());
                try { tiPrefabTag = TypeManager.GetTypeIndex(Il2CppType.Of<Unity.Entities.Prefab>()); } catch (Exception) { }
                ok = tiTag.Value != 0; failed = !ok;
                if (failed) ModLog.Warn("signal: SpaceshipElectricPortUpdateRuntimeEndEntity not available — a transmitter stuck with no signal will not be repaired");
                return ok;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("signal: port tag not available (" + ex.Message + ")"); return false; }
        }

        public static unsafe void Step(EntityManager em, float dt)
        {
            if (!Relink || !Launched) { if (!Launched && (attempts.Count > 0 || stuckSet.Count > 0)) Forget(); return; }
            t += dt; if (t < 1f) return; t = 0f;
            if (!Prepare()) return;
            List<Entity> fix = null;
            foreach (var tr in Ecs.Transmitters(em))
            {
                try
                {
                    if (!Ecs.Alive(em, tr)) continue;
                    if (tiPrefabTag.Value != 0 && em.HasComponent(tr, ComponentType.ReadOnly(tiPrefabTag))) continue;   // a prefab entity, not a world one
                    var ports = Ecs.Ports(em, tr); if (ports.Length == 0) continue;
                    var p = ports[0];   // the game's job only uses port [0]
                    if (!Ecs.Alive(em, p) || !Ecs.HasC(em, p, Ecs.tiPort)) continue;
                    var b = (byte*)em.GetComponentDataRawRO(p, Ecs.tiPort); if (b == null) continue;
                    long k = Ecs.K(p);
                    if (b[21] != DataInOut) { stuckSet.Remove(k); if (attempts.Remove(k)) gaveUp.Remove(k); continue; }   // resolved: forget it
                    if (gaveUp.Contains(k)) continue;
                    // only acts after TWO consecutive passes stuck (1 s): lets the game's normal resolution happen by itself
                    stuckSet.TryGetValue(k, out int consecutive); consecutive++; stuckSet[k] = consecutive;
                    if (consecutive < 2) continue;
                    attempts.TryGetValue(k, out int n); n++; attempts[k] = n;
                    if (n > MaxAttempts)
                    {
                        gaveUp.Add(k);
                        if (Aim(em, tr, p) || ByChannel(em, tr, p)) { tCensus = 3f; continue; }   // 0.24.0: by topology, or by channel
                        ModLog.Warn("Wireless Transmitter " + tr.Index + ": its port is still undirected after " + MaxAttempts
                            + " retries — the game cannot resolve it on its own." + Report(em, tr, p, b));
                        tCensus = 3f;
                        continue;
                    }
                    if (em.HasComponent(p, ComponentType.ReadOnly(tiTag))) continue;   // already marked, waiting for the game
                    (fix ?? (fix = new List<Entity>())).Add(p);
                    ModLog.Info("Wireless Transmitter " + tr.Index + ": port " + p.Index + " stuck with no direction (amber LED) — asking the game to resolve it again (try " + n + "/" + MaxAttempts + ")");
                }
                catch (Exception) { }
            }
            if (tCensus > 0f) { tCensus -= 1f; if (tCensus <= 0f && !censusDone) { censusDone = true; Census(em); } }
            // the structural change (AddComponent) only AFTER the loop: touching the chunks in the middle of the scan invalidates the iteration
            if (fix == null) return;
            for (int i = 0; i < fix.Count; i++)
            {
                try { var p = fix[i]; if (Ecs.Alive(em, p) && !em.HasComponent(p, ComponentType.ReadOnly(tiTag))) em.AddComponent(p, ComponentType.ReadWrite(tiTag)); }
                catch (Exception ex) { ModLog.Warn("signal: could not re-tag the port: " + ex.Message); }
            }
        }


        /// <summary>0.24.0 (report of 10/10): the transmitter's port had no direction because its cable ends on a port of a
        /// block of OURS, and our ports are UNTYPED (Unset) on purpose — "shaft = motor, data = servo", 0.15.0. The game resolves
        /// a DataInOut by walking the cable up to a port with a defined type, and knows how to go through Frame With Ports; on an Unset port of a
        /// block that isn't a Frame With Ports it stops, and the transmitter stays amber forever — in the same part, with no joint in between.
        ///
        /// But the MOD knows the direction: the ports before the pass-through range are the block's inputs and the ones after it are outputs. If the cable
        /// reaches an INPUT of ours, the transmitter is what feeds it => it's a RECEIVER (DataOutput port). If it reaches an OUTPUT of ours,
        /// it's a SENDER (DataInput). Only that is written, and only on the transmitter's own port: the game already resolved _runtimeEndEntity
        /// correctly (the report shows the right port, in the same part), so that byte really is all that's missing.</summary>
        static unsafe bool Aim(EntityManager em, Entity tr, Entity port)
        {
            try
            {
                var f = Ecs.Source(em, port, out _);
                if (Ecs.IsNull(f) || !Ecs.Alive(em, f) || !Ecs.HasC(em, f, Ecs.tiPort)) return false;
                var owner = Ecs.Parent(em, f); if (Ecs.IsNull(owner) || !Ecs.Alive(em, owner)) return false;
                ulong h; try { h = Ecs.Prefab(em, owner); } catch (Exception) { return false; }
                if (!JointSystem.KindFromHash(h, out _, out _, out var name) || string.IsNullOrEmpty(name)) return false;   // not a block of ours
                var ps = Ecs.Ports(em, owner); int i = Ecs.PortIndex(em, f);
                if (ps == null || ps.Length == 0 || i < 0) return false;
                if (!SlipRings.Range(em, ps, name, out int begin, out int nl)) return false;
                byte newVal; string role;
                if (i < begin) { newVal = 1; role = "receiver (its port feeds the " + name + "'s input)"; }
                else if (i >= begin + nl) { newVal = 0; role = "sender (the " + name + "'s output feeds its port)"; }
                else return false;   // pass-through port: the direction depends on the other side, can't be stated
                var w = (byte*)em.GetComponentDataRawRW(port, Ecs.tiPort); if (w == null) return false;
                w[21] = newVal;
                ModLog.Info("Wireless Transmitter " + tr.Index + ": port " + port.Index + " had no direction because the cable ends on port " + f.Index
                    + " of " + name + " " + owner.Index + ", and this mod's ports are deliberately typeless — the game stops there. Setting it to "
                    + Name(newVal) + ": this transmitter is the " + role);
                return true;
            }
            catch (Exception ex) { ModLog.Warn("signal: could not set the direction: " + ex.Message); return false; }
        }

        /// <summary>0.24.0, 2nd route: when the cable ends on a PASS-THROUGH port of ours (Slip Ring, for example), the topology doesn't give the
        /// direction — it depends on the other side of the ring. But the CHANNEL does: a channel has a single sender. If there's already another transmitter
        /// on the same channel resolved as a sender, this one can only be a receiver. It doesn't depend on cable, ring or part.
        /// (Only in this direction: "there's no sender on the channel" does NOT prove this one is the sender — the sender may be stuck too.)</summary>
        static unsafe bool ByChannel(EntityManager em, Entity tr, Entity port)
        {
            try
            {
                int channel = Ecs.Channel(em, tr); if (channel < 0) return false;
                Entity sender = Entity.Null; int onChannel = 0;
                foreach (var o in Ecs.Transmitters(em))
                {
                    if (!Ecs.Alive(em, o) || (o.Index == tr.Index && o.Version == tr.Version)) continue;
                    if (tiPrefabTag.Value != 0 && em.HasComponent(o, ComponentType.ReadOnly(tiPrefabTag))) continue;
                    if (Ecs.Channel(em, o) != channel) continue;
                    onChannel++;
                    var ps = Ecs.Ports(em, o); if (ps.Length == 0 || !Ecs.HasC(em, ps[0], Ecs.tiPort)) continue;
                    var b = (byte*)em.GetComponentDataRawRO(ps[0], Ecs.tiPort); if (b == null) continue;
                    if (b[21] == 0) sender = o;   // DataInput = sender
                }
                if (Ecs.IsNull(sender)) return false;
                var w = (byte*)em.GetComponentDataRawRW(port, Ecs.tiPort); if (w == null) return false;
                w[21] = 1;   // DataOutput = receiver
                ModLog.Info("Wireless Transmitter " + tr.Index + ": port " + port.Index + " ends on a pass-through port of this mod (the direction depends on the other side of the ring, so the topology cannot say it)"
                    + " — but channel " + channel + " already has a sender (transmitter " + sender.Index + ") among its " + (onChannel + 1) + " transmitters, so this one can only be the receiver. Setting it to DataOutput");
                return true;
            }
            catch (Exception ex) { ModLog.Warn("signal: could not set the direction by channel: " + ex.Message); return false; }
        }

        /// <summary>" , owned by <block> (<mod prefab, or 'a game block'>)" — without it the report doesn't say which piece the cable ends on.</summary>
        static string Owner(EntityManager em, Entity port)
        {
            try
            {
                if (Ecs.IsNull(port) || !Ecs.Alive(em, port)) return "";
                var d = Ecs.Parent(em, port); if (Ecs.IsNull(d) || !Ecs.Alive(em, d)) return " (no owner block)";
                string name = null; try { if (JointSystem.KindFromHash(Ecs.Prefab(em, d), out _, out _, out var nm)) name = nm; } catch (Exception) { }
                int i = Ecs.PortIndex(em, port); var ps = Ecs.Ports(em, d);
                var sb = new System.Text.StringBuilder(" of block ").Append(d.Index).Append(' ').Append(name ?? "(a game block)");
                sb.Append(" [port ").Append(i).Append(" of ").Append(ps == null ? 0 : ps.Length).Append(']');
                if (name != null && ps != null && SlipRings.Range(em, ps, name, out int begin, out int nl))
                    sb.Append(i < begin ? " = an input of ours" : i >= begin + nl ? " = an output of ours" : " = a pass-through of ours");
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        /// <summary>0.24.0: census of the transmitters per channel, once a few seconds after we touch some port. Shows, for
        /// each channel, who the game considers a sender (DataInput), a receiver (DataOutput) and who is still without a direction (DataInOut) — it's the
        /// only way to know whether the pair can find each other.</summary>
        static float tCensus = -1f; static bool censusDone;
        static unsafe void Census(EntityManager em)
        {
            try
            {
                var byChannel = new Dictionary<int, System.Text.StringBuilder>();
                foreach (var tr in Ecs.Transmitters(em))
                {
                    if (!Ecs.Alive(em, tr)) continue;
                    if (tiPrefabTag.Value != 0 && em.HasComponent(tr, ComponentType.ReadOnly(tiPrefabTag))) continue;
                    int c = Ecs.Channel(em, tr); if (c < 0) continue;
                    var ps = Ecs.Ports(em, tr); byte rt = 255; float v = 0f;
                    if (ps.Length > 0 && Ecs.HasC(em, ps[0], Ecs.tiPort))
                    {
                        var b = (byte*)em.GetComponentDataRawRO(ps[0], Ecs.tiPort);
                        if (b != null) { rt = b[21]; v = *(float*)(b + 8); }
                    }
                    if (!byChannel.TryGetValue(c, out var sb)) { sb = new System.Text.StringBuilder(); byChannel[c] = sb; }
                    sb.Append(sb.Length > 0 ? ", " : "").Append(tr.Index).Append('=').Append(Name(rt))
                      .Append(rt == 0 ? "(sender)" : rt == 1 ? "(receiver)" : "(UNRESOLVED)").Append(" value=").Append(v.ToString("F3"));
                }
                if (byChannel.Count == 0) return;
                var o = new System.Text.StringBuilder("signal: wireless census —");
                foreach (var kv in byChannel) o.Append(" channel ").Append(kv.Key).Append(": ").Append(kv.Value).Append(';');
                ModLog.Info(o.ToString());
            }
            catch (Exception ex) { ModLog.Warn("signal: census: " + ex.Message); }
        }

        /// <summary>0.24.0: what exactly is keeping the port from resolving. Comes out once per port, when the mod gives up.
        /// (The game's resolution only walks the cable INSIDE the same part; the mod knows how to walk the cable on its own — Ecs.Source —
        /// so comparing the two tells right away whether the end fell on another part, whether there's no cable, or whether it's something else.)</summary>
        static unsafe string Report(EntityManager em, Entity tr, Entity port, byte* b)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                Ecs.RawPortPub(em, port, out var setup, out var val, out var end, out var persist);
                sb.Append(" Port ").Append(port.Index).Append(" of block ").Append(Describe(em, tr)).Append(':');
                sb.Append(" setup=").Append(Name(setup)).Append(" runtime=").Append(Name(b[21])).Append(" value=").Append(val.ToString("F3"));
                sb.Append("; connected to ").Append(Describe(em, persist));
                sb.Append("; the game's end is ").Append(Describe(em, end));
                var f = Ecs.Source(em, port, out var trail);
                sb.Append("; walking the cable myself:").Append(trail.Length > 0 ? trail : " (nothing)");
                if (!Ecs.IsNull(f))
                {
                    Ecs.RawPortPub(em, f, out var setupF, out _, out _, out _);
                    sb.Append(" => ").Append(Describe(em, f)).Append(Owner(em, f)).Append(" setup=").Append(Name(setupF));
                }
                return sb.ToString();
            }
            catch (Exception ex) { return " (could not inspect the port: " + ex.Message + ")"; }
        }
        static string Name(byte t) => t == 0 ? "DataInput" : t == 1 ? "DataOutput" : t == 2 ? "Power" : t == 3 ? "DataInOut" : t == 4 ? "Plasma" : t == 5 ? "Unset" : t.ToString();
        /// <summary>"N port on part P" — what the entity is and which part it is on (the part is what the game's resolution compares).</summary>
        static string Describe(EntityManager em, Entity e)
        {
            if (Ecs.IsNull(e)) return "nothing";
            var sb = new System.Text.StringBuilder().Append(e.Index);
            if (!Ecs.Alive(em, e)) return sb.Append(" (gone)").ToString();
            if (Ecs.IsCable(em, e)) sb.Append(" (a cable cell)");
            else if (Ecs.HasC(em, e, Ecs.tiPort)) sb.Append(" (a port)");
            var cur = e;
            for (int i = 0; i < 8 && !Ecs.IsNull(cur); i++) { if (Ecs.IsPart(em, cur)) return sb.Append(" on part ").Append(cur.Index).ToString(); cur = Ecs.Parent(em, cur); }
            return sb.Append(" on no part").ToString();
        }

        public static void Forget() { attempts.Clear(); gaveUp.Clear(); stuckSet.Clear(); t = 9f; tCensus = -1f; censusDone = false; }
    }
}
