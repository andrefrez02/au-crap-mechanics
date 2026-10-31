using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace CrapMechanics
{
    /// <summary>0.22.0 — slip ring (Andre: "I didn't like the cable approach, it hanging loose, it will never make sense on an object
    /// that is spinning 360 all the time ... let it go only through the ring"). 0.21.x spliced the cable that CROSSED the joint (following the cells
    /// to the port of the other part); that's gone. Now no cable crosses: every mod joint has untyped PASS-THROUGH ports on its base (the
    /// last ones in the prefab's list: 4 on the bearing / piston / recoupler, 1 on the minis, 4 x 4 on the turret seat — one set per plate) and the
    /// moving piece carries a Slip Ring (SC_SlipRing, 0.25 cube with 4 ports; 0.22.1: the Mini Slip Ring was removed). The plugin links
    /// pass-through port i of the joint <-> port i of the ring:
    ///  - DATA: on the side where the cable comes from an OUTPUT, the value goes to the input on the other side. As in 0.21.x: the input (or the untyped
    ///    port of a mod block) gets _runtimeEndEntity (+12) pointing at the output over there — the game's SpaceshipElectricGetInputs copies
    ///    that output's _portValue. Rewritten every frame (the game's job only redoes it when the ship changes). Output on both sides = conflict (warning).
    ///  - POWER: power cable on both sides = bridge through ghosts (0.21.4): an entity with only SCElectricPower in each circuit;
    ///    the ghost on the receiving side "generates" what that side consumes, the one on the giving side "consumes" it. No cap: above _maxPower
    ///    the game's melting burns the cable (Andre: "let it burn like the game does").
    /// Where the ring has to be: bearing / piston = on the block's axis, on the upper part (on the disc; on the piston head or right after the
    /// Piston End); recoupler = glued to the decoupler where it engaged; turret = on the piece hanging from that set's plate.</summary>
    internal static class SlipRings
    {
        public static bool Enabled = true, DetailedLog = true;
        /// <summary>How many pass-through ports the block has (the LAST ones in the prefab's list).</summary>
        public static int NPassThrough(string name) => name == null ? 0 : name.StartsWith("SC_TurretSeat") ? 16 : name.EndsWith("SlipRing") ? 4 : name.StartsWith("SC_Mini") ? 1 : 4;
        /// <summary>0.23.0: ports AFTER the pass-through ones in the prefab's list (the bearings' angle output; it was put last so as not to
        /// change the index of the pass-through ones in 0.22.x blueprints).</summary>
        public static int NOutputs(string name) => name != null && (name.StartsWith("SC_Bearing") || name.StartsWith("SC_GyroBearing") || name.StartsWith("SC_MiniBearing") || name.StartsWith("SC_MiniGyroBearing")) ? 1 : 0;
        /// <summary>0.23.1: range [begin, begin+n) of the pass-through ports in the prefab's list. The angle output (0.23.0) is the last one in the
        /// list, so the range is no longer "the last n"; the old prefab (0.22.x, without the output) is recognised by the type of the last port.</summary>
        public static bool Range(EntityManager em, Entity[] ps, string name, out int begin, out int n)
        {
            begin = 0; n = NPassThrough(name);
            if (n == 0 || ps == null || ps.Length < n) return false;
            int ns = 0;
            if (NOutputs(name) > 0 && ps.Length > n && Ecs.Port(em, ps[ps.Length - 1], out var tf, out _, out _) && tf == Ecs.PortOutput) ns = 1;
            begin = ps.Length - n - ns;
            return begin >= 0;
        }
        /// <summary>Is the port one of the block's pass-through ones (by its index in the prefab)? JointSystem (servo) and Axes (shaft) ignore these.</summary>
        public static bool IsPassThrough(EntityManager em, Entity owner, Entity port, string name)
        {
            var ps = Ecs.Ports(em, owner);
            if (!Range(em, ps, name, out int begin, out int n) || ps.Length <= n) return false;   // block without pass-through ports (old pistons 2/4/8)
            int i = Ecs.PortIndex(em, port);
            return i >= begin && i < begin + n;
        }

        class Channel
        {
            public Entity pj, pa; public int i;          // joint port, ring port
            public int direction;                          // 0 nothing, 1 joint -> ring, -1 ring -> joint, 2 conflict (output on both sides), 3 power
            public Entity source, target; public int writes; public float tLog = 99f; public int warnings;
            public Bridge bridge; public string latest = "";
        }
        class Link { public string key, name; public Entity jointBlock, ring; public List<Channel> channels = new List<Channel>(); public bool alive; public int portSet; }
        static readonly Dictionary<string, Link> links = new Dictionary<string, Link>();
        static float tScan = 9f; static int warnings; static ulong hRing, hMini; static bool hashes;
        static readonly string[] PLATES = { "left", "right", "back", "top" };   // order of the turret base port sets (modelos_v2 TURRET_PORTS)

        public static void Step(EntityManager em, float dt)
        {
            if (!Enabled) return;
            tScan += dt;
            if (tScan >= 0.5f) { tScan = 0f; try { Scan(em); } catch (Exception ex) { if (warnings++ < 3) ModLog.Warn("slip ring: " + ex.Message); } try { TintAll(em); } catch (Exception ex) { if (colorWarnings++ < 3) ModLog.Warn("pass-through plugs: " + ex.Message); } }
            frameBridges.Clear();
            foreach (var l in links.Values) foreach (var c in l.channels) { try { Apply(em, l, c, dt); } catch (Exception ex) { if (c.warnings++ < 3) ModLog.Warn("slip ring (" + l.name + " #" + c.i + "): " + ex.Message); } }
            try { Solve(em, dt); } catch (Exception ex) { if (netWarnings++ < 3) ModLog.Warn("power bridge network: " + ex.Message); }
        }

        // ================================================================ search: active joint -> ring on the moving piece
        struct Candidate { public Entity e, part; public float3 pos; }
        static List<Candidate> Rings(EntityManager em)
        {
            var r = new List<Candidate>();
            if (!hashes) { hRing = Ecs.HashPrefab("SC_SlipRing"); hMini = Ecs.HashPrefab("SC_MiniSlipRing"); hashes = hRing != 0; if (!hashes) return r; }
            foreach (var b in Ecs.Components(em))
            {
                ulong h = Ecs.Prefab(em, b); if (h == 0 || (h != hRing && h != hMini)) continue;   // 0.22.4: any ring on any joint
                var part = Ecs.Parent(em, b); if (Ecs.IsNull(part)) continue;
                r.Add(new Candidate { e = b, part = part, pos = Ecs.Position(em, b) });
            }
            return r;
        }
        /// <summary>Bearing / piston: ring on the axis (+Y of the block), beyond the block, on the upper part; the nearest along the axis.</summary>
        static TypeIndex tiGTa; static bool tiGTaOk;
        static unsafe bool GT(EntityManager em, Entity e, out RigidTransform g)
        {
            g = default;
            try
            {
                if (!tiGTaOk) { tiGTaOk = true; tiGTa = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<GarageTransform>()); }
                if (!em.HasComponent(e, ComponentType.ReadOnly(tiGTa))) return false;
                g = *(RigidTransform*)em.GetComponentDataRawRO(e, tiGTa); return true;
            }
            catch (Exception) { return false; }
        }
        static Entity RingOnAxis(EntityManager em, Joint j, List<Candidate> rings, HashSet<long> usedSet, out string why)
        {
            why = ""; Ecs.Axes(em, j.block, out _, out var up, out _); var p0 = Ecs.Position(em, j.block);
            Entity bestPick = Entity.Null; float bestAlong = float.MaxValue; int offAxis = 0;
            foreach (var a in rings)
            {
                if (usedSet.Contains(Ecs.K(a.e)) || a.part.Index != j.partB.Index) continue;
                var d = Ecs.Sub(a.pos, p0); float along = Ecs.Dot(d, up); float lt = Ecs.Len(Ecs.Sub(d, Ecs.Mul(up, along)));
                // 0.26.4 (Andre's log: the ring linked and unlinked when the weld gave way/merged crooked and the 2nd stage's signal blinked):
                // "on the axis, above the block" is GARAGE geometry. With GarageTransform on the block and on the ring, decide by it.
                if (GT(em, j.block, out var gJ) && GT(em, a.e, out var gR))
                { var dg = Ecs.Sub(gR.pos, gJ.pos); var ug = Ecs.Rot(gJ.rot, Ecs.UY); along = Ecs.Dot(dg, ug); lt = Ecs.Len(Ecs.Sub(dg, Ecs.Mul(ug, along))); }
                if (along < 0.3f * j.scale || lt > 0.1f) { offAxis++; continue; }
                if (along < bestAlong) { bestAlong = along; bestPick = a.e; }
            }
            if (Ecs.IsNull(bestPick) && offAxis > 0 && j.state != 4) why = offAxis + " ring(s) on the moving piece but off the joint's axis (more than 10 cm sideways)";
            return bestPick;
        }
        /// <summary>Recoupler: ring glued to the decoupler where it engaged (same part as the decoupler, up to 0.75 m from its center).</summary>
        static Entity RingNear(EntityManager em, Entity decoupler, List<Candidate> rings, HashSet<long> usedSet)
        {
            var part = Ecs.Parent(em, decoupler); var p0 = Ecs.Position(em, decoupler);
            Entity bestPick = Entity.Null; float md = 0.75f;
            foreach (var a in rings)
            {
                if (usedSet.Contains(Ecs.K(a.e)) || a.part.Index != part.Index) continue;
                float d = Ecs.Dist(a.pos, p0); if (d < md) { md = d; bestPick = a.e; }
            }
            return bestPick;
        }
        /// <summary>Turret: ring on the piece hanging from the plate (anywhere on its part; the nearest to the turret).</summary>
        static Entity RingOnPart(EntityManager em, Entity part, float3 p0, List<Candidate> rings, HashSet<long> usedSet)
        {
            Entity bestPick = Entity.Null; float md = float.MaxValue;
            foreach (var a in rings)
            {
                if (usedSet.Contains(Ecs.K(a.e)) || a.part.Index != part.Index) continue;
                float d = Ecs.Dist(a.pos, p0); if (d < md) { md = d; bestPick = a.e; }
            }
            return bestPick;
        }
        static readonly Dictionary<string, string> noRingLog = new Dictionary<string, string>();
        static void Scan(EntityManager em)
        {
            foreach (var l in links.Values) l.alive = false;
            var rings = Rings(em); var usedSet = new HashSet<long>();
            if (rings.Count > 0)
            {
                foreach (var j in JointSystem.ActiveJoints())
                {
                    if ((j.kind != Kind.Bearing && j.kind != Kind.Piston) || Ecs.IsNull(j.partB)) continue;
                    var a = RingOnAxis(em, j, rings, usedSet, out var why);
                    string k = Ecs.K(j.block) + ":0";
                    if (Ecs.IsNull(a)) { if (why.Length > 0 && (!noRingLog.TryGetValue(k, out var prv) || prv != why)) { noRingLog[k] = why; ModLog.Warn(j.name + " " + j.block.Index + ": no slip ring — " + why); } continue; }
                    noRingLog.Remove(k);
                    Connect(em, j.block, j.name, 0, a, usedSet);
                }
                foreach (var r in Couplers.Coupled())
                {
                    if (Ecs.IsNull(r.decoupler) || !Ecs.Alive(em, r.decoupler)) continue;
                    var a = RingNear(em, r.decoupler, rings, usedSet); if (Ecs.IsNull(a)) continue;
                    Connect(em, r.block, "SC_Recoupler", 0, a, usedSet);
                }
                foreach (var t in JointSystem.ActiveTurrets())
                {
                    var p0 = Ecs.Position(em, t.block);
                    if (t.state == 5)
                    {
                        // 0.26.8: whole turret — everything is a single part; each plate's ring is the nearest to the turret within its structure
                        foreach (var kv in t.faceStructure)
                        {
                            int c = Array.IndexOf(PLATES, kv.Key); if (c < 0) continue;
                            Entity bestPick = Entity.Null; float md = float.MaxValue;
                            foreach (var an in rings) { if (usedSet.Contains(Ecs.K(an.e)) || !kv.Value.Contains(Ecs.K(an.e))) continue; float dd = Ecs.Dist(an.pos, p0); if (dd < md) { md = dd; bestPick = an.e; } }
                            if (!Ecs.IsNull(bestPick)) Connect(em, t.block, t.name, c, bestPick, usedSet);
                        }
                        continue;
                    }
                    foreach (var mv in t.movs)
                    {
                        if (Ecs.IsNull(mv.part) || !Ecs.Alive(em, mv.part)) continue;
                        string face = (mv.faces ?? "").Split('+')[0]; int c = Array.IndexOf(PLATES, face); if (c < 0) continue;
                        var a = RingOnPart(em, mv.part, p0, rings, usedSet); if (Ecs.IsNull(a)) continue;
                        Connect(em, t.block, t.name, c, a, usedSet);
                    }
                }
            }
            Clean(em);
        }
        static void Connect(EntityManager em, Entity jointBlock, string name, int portSet, Entity ring, HashSet<long> usedSet)
        {
            usedSet.Add(Ecs.K(ring));
            string key = Ecs.K(jointBlock) + ":" + portSet;
            if (links.TryGetValue(key, out var l) && l.ring.Index == ring.Index && l.ring.Version == ring.Version) { l.alive = true; return; }
            if (l != null) { TurnOff(em, l, "the slip ring changed"); links.Remove(key); }
            var ps = Ecs.Ports(em, jointBlock); var pa = Ecs.Ports(em, ring); int n = NPassThrough(name);
            Entity[] lat;
            if (name != null && name.StartsWith("SC_TurretSeat")) { if (ps.Length < 16) { ModLog.Warn(name + " " + jointBlock.Index + ": the prefab has " + ps.Length + " port(s), expected 16 — rebuild the bundle in the Modkit"); return; } lat = new Entity[4]; Array.Copy(ps, ps.Length - 16 + portSet * 4, lat, 0, 4); }
            else { if (!Range(em, ps, name, out int begin, out n)) { ModLog.Warn(name + " " + jointBlock.Index + ": the prefab has " + ps.Length + " port(s), expected at least " + NPassThrough(name) + " — rebuild the bundle in the Modkit"); return; } lat = new Entity[n]; Array.Copy(ps, begin, lat, 0, n); }
            int m = Math.Min(lat.Length, pa.Length);
            l = new Link { key = key, jointBlock = jointBlock, ring = ring, portSet = portSet, alive = true,
                           name = name + " " + jointBlock.Index + (name != null && name.StartsWith("SC_TurretSeat") ? " (" + PLATES[portSet] + " plate)" : "") + " <-> " + (pa.Length > 0 && Ecs.Prefab(em, ring) == hMini ? "Mini Slip Ring " : "Slip Ring ") + ring.Index };
            for (int i = 0; i < m; i++) l.channels.Add(new Channel { pj = lat[i], pa = pa[i], i = i });
            links[key] = l;
            ModLog.Info("slip ring: " + l.name + " — " + m + " channel(s) (part " + Ecs.Parent(em, jointBlock).Index + " <-> part " + Ecs.Parent(em, ring).Index + ")");
        }
        static void Clean(EntityManager em)
        {
            var deadOnes = new List<string>();
            foreach (var kv in links) if (!kv.Value.alive || !Ecs.Alive(em, kv.Value.jointBlock) || !Ecs.Alive(em, kv.Value.ring)) deadOnes.Add(kv.Key);
            foreach (var k in deadOnes) { TurnOff(em, links[k], "joint gone, ring removed or no longer on the moving piece"); links.Remove(k); }
        }
        static void TurnOff(EntityManager em, Link l, string reason)
        {
            foreach (var c in l.channels) { UndoBridge(em, c); }
            ModLog.Info("slip ring: " + l.name + " — gone (" + reason + ")");
        }

        // ================================================================ per frame: data (runtimeEnd) or power (ghosts)
        static void Apply(EntityManager em, Link l, Channel c, float dt)
        {
            var fJ = Ecs.Source(em, c.pj, out _); var fA = Ecs.Source(em, c.pa, out _);
            byte tJ = 255, tA = 255;
            bool okJ = !Ecs.IsNull(fJ) && Ecs.Port(em, fJ, out tJ, out _, out _), okA = !Ecs.IsNull(fA) && Ecs.Port(em, fA, out tA, out _, out _);
            if (!okJ) tJ = 255; else tJ = Ecs.EffectiveType(em, fJ);   // 0.24.0: the runtime type wins when the setup doesn't give the direction
            if (!okA) tA = 255; else tA = Ecs.EffectiveType(em, fA);
            bool enJ = okJ && tJ == Ecs.PortPower, enA = okA && tA == Ecs.PortPower;
            int direction; string desc;
            if (enJ && enA) { direction = 3; desc = "power bridge"; }
            else if (enJ || enA) { direction = 0; desc = "a POWER cable on one side and a data cable (or nothing) on the other — nothing passes"; }
            else
            {
                bool outJ = okJ && tJ == Ecs.PortOutput, outA = okA && tA == Ecs.PortOutput;
                bool entJ = okJ && (tJ == Ecs.PortInput || tJ == JointSystem.PortUntyped), entA = okA && (tA == Ecs.PortInput || tA == JointSystem.PortUntyped);
                if (outJ && outA) { direction = 2; desc = "OUTPUTS on both sides (port " + fJ.Index + " and port " + fA.Index + ") — nothing passes"; }
                else if (outJ && entA) { direction = 1; desc = "data joint -> ring: output " + fJ.Index + " (block " + Ecs.Parent(em, fJ).Index + ") -> input " + fA.Index + " (block " + Ecs.Parent(em, fA).Index + ")"; }
                else if (outA && entJ) { direction = -1; desc = "data ring -> joint: output " + fA.Index + " (block " + Ecs.Parent(em, fA).Index + ") -> input " + fJ.Index + " (block " + Ecs.Parent(em, fJ).Index + ")"; }
                else { direction = 0; desc = okJ || okA ? "no output on either side" : "nothing connected"; }
            }
            if (direction != c.direction || desc != c.latest)
            {
                if (c.direction == 3 && direction != 3) UndoBridge(em, c);
                c.direction = direction; c.latest = desc; c.tLog = 99f;
                if (direction != 0 || desc != "nothing connected") { if (direction == 2 || desc.Contains("nothing passes")) ModLog.Warn("slip ring: " + l.name + " #" + c.i + ": " + desc); else ModLog.Info("slip ring: " + l.name + " #" + c.i + ": " + desc); }
            }
            // (0.23.0 added a log of the trail and of value changes to hunt a delay that didn't exist — it was a missing cable in
            // Andre's circuit; the game's Delay goes through the bridge just fine. Removed in 0.23.1.)
            // 0.24.0-mp: the game already syncs port values (SyncPortValue) and power (SyncElectricPower). Running the bridge on both
            // sides shouldn't conflict — if they diverge, the server wins. [Multiplayer] ring_on_client turns this off on the client.
            if (!Multi.RingOnClient && !Multi.MyPart(em, Ecs.Parent(em, l.jointBlock)))
            {
                if (c.direction == 3) UndoBridge(em, c);
                return;
            }
            if (direction == 1) { if (Write(em, fA, fJ)) c.writes++; }
            else if (direction == -1) { if (Write(em, fJ, fA)) c.writes++; }
            else if (direction == 3) Power(em, l, c, fJ, fA, dt);
        }
        static unsafe bool Write(EntityManager em, Entity input, Entity output)
        {
            if (!Ecs.HasC(em, input, Ecs.tiPort)) return false;
            var b = (byte*)em.GetComponentDataRawRW(input, Ecs.tiPort); if (b == null) return false;
            var end = (Entity*)(b + Layout.PortEnd);
            if (end->Index == output.Index && end->Version == output.Version) return false;
            *end = output; return true;
        }

        // ================================================================ power (0.21.4, unchanged): ghosts in the circuits
        // The game builds one circuit per cable network INSIDE the part (ElectricCircuitRoot + ElectricCircuitPort /
        // ElectricCircuitComponent buffers) and rebuilds those roots all the time. SpaceshipElectricUpdateCircuits (the ONLY one that reads the
        // component buffer, only through SCElectricPower) adds up the components' _generatePerSec (+28) and _consumptionPerSec (+32), drains the batteries
        // (_runtimePower +0) by the deficit and copies (total, real, generation) into each component's _circuitInfo (+8). Writing into the GAME's
        // blocks fed back (plasma generator: 119 thousand P/s) — that's why each side gets a GHOST: our own entity with only SCElectricPower,
        // added to the circuit's component buffer (it fits in the capacity; redone when the game rebuilds the root).
        class Bridge { public Entity ghostJ, ghostA, rootJ, rootA; public float maxCable = 150f; public float q; public int direction; public float tLog = 99f; public int bufferWarn; public bool loop; }
        static TypeIndex tiRoot, tiBufComp, tiBufPort; static EntityQuery qRoot; static bool circOk, circFailed;
        static bool PrepareCircuits(EntityManager em)
        {
            if (circOk) return true; if (circFailed) return false;
            try
            {
                tiRoot = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<ElectricCircuitRoot>());
                tiBufPort = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<ElectricCircuitPort>());
                tiBufComp = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<ElectricCircuitComponent>());
                var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiRoot);
                qRoot = em.CreateEntityQuery(a); circOk = true; return true;
            }
            catch (Exception ex) { circFailed = true; ModLog.Warn("power bridge: circuits not available: " + ex.Message); return false; }
        }
        /// <summary>Smallest _maxPower (+12) among the cells of the cable leaving the port.</summary>
        static unsafe float MaxCable(EntityManager em, Entity port)
        {
            float m = float.MaxValue;
            try
            {
                if (!Ecs.RawPortPub(em, port, out _, out _, out _, out var persist)) return 150f;
                var prev = port; var cur = persist; var seen = new HashSet<long>();
                for (int i = 0; i < 2048 && Ecs.IsCable(em, cur) && seen.Add(Ecs.K(cur)); i++)
                {
                    var b = (byte*)em.GetComponentDataRawRO(cur, Ecs.tiCable); if (b == null) break;
                    float mp = *(float*)(b + Layout.CellMaxPower); if (mp > 0f && mp < m) m = mp;
                    var fwd = *(Entity*)(b + Layout.CellFwd); var bwd = *(Entity*)(b + Layout.CellBwd);
                    var nxt = (fwd.Index == prev.Index && fwd.Version == prev.Version) ? bwd : fwd; prev = cur; cur = nxt;
                    if (Ecs.IsNull(cur)) break;
                }
            }
            catch (Exception) { }
            return m == float.MaxValue ? 150f : m;
        }
        /// <summary>Root of the circuit that lists the port (cached; if the game rebuilt the roots, search again). The pass-through (untyped) port
        /// may not be in the game's list: try it and, if not, the power port at the other end of the cable.</summary>
        static Entity RootOfPort(EntityManager em, Entity port, Entity source, Entity cache)
        {
            if (Ecs.Alive(em, cache) && Ecs.HasC(em, cache, tiRoot)) foreach (var pe in Ecs.BufferEntities(em, cache, tiBufPort)) if ((pe.Index == port.Index && pe.Version == port.Version) || (pe.Index == source.Index && pe.Version == source.Version)) return cache;
            foreach (var r in Ecs.QueryList(qRoot)) foreach (var pe in Ecs.BufferEntities(em, r, tiBufPort)) if ((pe.Index == port.Index && pe.Version == port.Version) || (pe.Index == source.Index && pe.Version == source.Version)) return r;
            return Entity.Null;
        }
        static unsafe Entity Ghost(EntityManager em, Entity current)
        {
            if (Ecs.Alive(em, current) && Ecs.HasC(em, current, Ecs.tiPower)) return current;
            var kinds = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(1); kinds[0] = ComponentType.ReadWrite(Ecs.tiPower);
            var e = em.CreateEntity(kinds);
            var b = (byte*)em.GetComponentDataRawRW(e, Ecs.tiPower); if (b != null) for (int i = 0; i < Layout.PowerSize; i++) b[i] = 0;
            return e;
        }
        /// <summary>Makes sure the ghost is in the root's ElectricCircuitComponent buffer (BufferHeader: Pointer, Length, Capacity; elements inline
        /// after the header when Pointer == null). With no spare capacity, leaves it alone (warns).</summary>
        static unsafe bool InCircuit(EntityManager em, Entity root, Entity ghostE, Bridge pt)
        {
            if (!Ecs.HasC(em, root, tiBufComp)) return false;
            var h = (byte*)em.GetComponentDataRawRW(root, tiBufComp); if (h == null) return false;
            byte* ptr = *(byte**)h; int len = *(int*)(h + 8), cap = *(int*)(h + 12);
            var data = (Entity*)(ptr != null ? ptr : h + 16);
            if (len < 0 || len > 4096 || cap < len) return false;
            for (int i = 0; i < len; i++) if (data[i].Index == ghostE.Index && data[i].Version == ghostE.Version) return true;
            if (len >= cap) { if (pt.bufferWarn++ < 1) ModLog.Warn("power bridge: the circuit's component buffer is full (" + len + "/" + cap + ") — cannot add the bridge entity"); return false; }
            data[len] = ghostE; *(int*)(h + 8) = len + 1; return true;
        }
        static unsafe void WriteGhost(EntityManager em, Entity ghostE, float generates, float consumes)
        {
            if (!Ecs.Alive(em, ghostE) || !Ecs.HasC(em, ghostE, Ecs.tiPower)) return;
            var b = (byte*)em.GetComponentDataRawRW(ghostE, Ecs.tiPower); if (b == null) return;
            *(float*)(b + Layout.PowerGen) = generates; *(float*)(b + Layout.PowerCons) = consumes;
        }
        /// <summary>Side J = joint, side A = ring. fJ / fA = power ports at the other ends of the cables. This only prepares (roots and
        /// ghosts in the circuit); how much goes through comes from Solve, which looks at all the bridges together.</summary>
        static void Power(EntityManager em, Link l, Channel c, Entity fJ, Entity fA, float dt)
        {
            if (!PrepareCircuits(em)) return;
            var pt = c.bridge;
            if (pt == null) { pt = c.bridge = new Bridge { maxCable = Math.Min(MaxCable(em, c.pj), MaxCable(em, c.pa)) }; ModLog.Info("power bridge: " + l.name + " #" + c.i + " — cables rated " + pt.maxCable.ToString("F0") + " P/s"); }
            pt.rootJ = RootOfPort(em, c.pj, fJ, pt.rootJ); pt.rootA = RootOfPort(em, c.pa, fA, pt.rootA);
            if (Ecs.IsNull(pt.rootJ) || Ecs.IsNull(pt.rootA) || pt.rootJ.Index == pt.rootA.Index) { WriteGhost(em, pt.ghostJ, 0f, 0f); WriteGhost(em, pt.ghostA, 0f, 0f); return; }
            pt.ghostJ = Ghost(em, pt.ghostJ); pt.ghostA = Ghost(em, pt.ghostA);
            bool okJ = InCircuit(em, pt.rootJ, pt.ghostJ, pt), okA = InCircuit(em, pt.rootA, pt.ghostA, pt);
            if (!okJ || !okA) { WriteGhost(em, pt.ghostJ, 0f, 0f); WriteGhost(em, pt.ghostA, 0f, 0f); return; }
            frameBridges.Add((l, c));
        }

        // ================================================================ 0.26.16: the bridge NETWORK
        // (Andre, discord: piston > slip ring > piston > slip ring ..., "on the third and fourth time the consumption doubles"; with the rod at 10%
        // it burned the wires the first time.) Each bridge decided on its own, from the circuit totals of the previous tick (which already include the
        // ghosts of the OTHER bridges): what the neighbour generated in a circuit became a "source", what it consumed became "demand". Log of
        // 24/10 00:29: two bridges flipping direction every frame and passing each other's demand on — 0.1 > 0.2 > 0.5 > ... > 2461 P/s
        // in half a second. Now: each circuit's consumption/generation/charge comes from ITS OWN components (no ghost at all, read in the frame,
        // after the pistons), the circuits linked by bridges form a network, the source is the circuit with the most energy (charge + generation),
        // and each bridge of the tree from the source carries exactly what the circuits past it use. A bridge that closes a loop stays
        // idle (the power goes the other way); parallel bridges between the same two circuits share the load.
        static readonly List<(Link l, Channel c)> frameBridges = new List<(Link, Channel)>();
        static readonly HashSet<long> sourcesBefore = new HashSet<long>(), sourcesNow = new HashSet<long>();
        static string netLog = ""; static int netWarnings;
        class Node { public Entity root; public long k; public float cons, gen, stored, dem; public int parent = -1, parentEdge = -1, size = 1; public List<int> edges = new List<int>(), parallels = new List<int>(); }
        /// <summary>Consumption and generation (P/s) and charge (P) of the circuit's components, without the bridges' ghosts.</summary>
        static unsafe Node Accumulate(EntityManager em, Entity root, HashSet<long> ghosts)
        {
            var n = new Node { root = root, k = Ecs.K(root) };
            foreach (var c in Ecs.BufferEntities(em, root, tiBufComp))
            {
                if (ghosts.Contains(Ecs.K(c)) || !Ecs.HasC(em, c, Ecs.tiPower)) continue;
                var b = (byte*)em.GetComponentDataRawRO(c, Ecs.tiPower); if (b == null) continue;
                n.stored += (float)*(double*)(b + Layout.PowerStored); n.gen += *(float*)(b + Layout.PowerGen); n.cons += *(float*)(b + Layout.PowerCons);
            }
            return n;
        }
        /// <summary>How much the bridge carries to cover the demand d (P/s) of the circuits past it. With a margin: generating EXACTLY the consumption
        /// leaves the circuit on the edge of "insufficient power" (float sums); negative = the far side has spare power and sends it back.</summary>
        static float Flow(float d) => d > 0f ? d * 1.002f + 0.002f : -Math.Max(0f, -d * 0.998f - 0.002f);
        static void Solve(EntityManager em, float dt)
        {
            sourcesNow.Clear();
            if (frameBridges.Count == 0) { sourcesBefore.Clear(); return; }
            var ghosts = new HashSet<long>();
            foreach (var l in links.Values) foreach (var c in l.channels) if (c.bridge != null) { ghosts.Add(Ecs.K(c.bridge.ghostJ)); ghosts.Add(Ecs.K(c.bridge.ghostA)); }
            int n = frameBridges.Count;
            var nodes = new List<Node>(); var idx = new Dictionary<long, int>();
            int NodeOf(Entity r) { long k = Ecs.K(r); if (!idx.TryGetValue(k, out int i)) { i = nodes.Count; idx[k] = i; nodes.Add(Accumulate(em, r, ghosts)); } return i; }
            var already = new int[n]; var na = new int[n]; var role = new int[n]; var fl = new float[n];   // role: -1 not seen, 1 tree, 2 parallel, 3 loop
            for (int e = 0; e < n; e++) { var pt = frameBridges[e].c.bridge; already[e] = NodeOf(pt.rootJ); na[e] = NodeOf(pt.rootA); nodes[already[e]].edges.Add(e); nodes[na[e]].edges.Add(e); role[e] = -1; }
            var seen = new bool[nodes.Count]; var queue = new Queue<int>(); var sb = new System.Text.StringBuilder(); var sig = new System.Text.StringBuilder();
            for (int s = 0; s < nodes.Count; s++)
            {
                if (seen[s]) continue;
                // circuits of this network and the source (most charge + generation; the previous one stays while it has 90% of the best)
                var comp = new List<int>(); queue.Enqueue(s); seen[s] = true;
                while (queue.Count > 0) { int u = queue.Dequeue(); comp.Add(u); foreach (int e in nodes[u].edges) { int v = already[e] == u ? na[e] : already[e]; if (!seen[v]) { seen[v] = true; queue.Enqueue(v); } } }
                int source = comp[0]; float bestPick = -1f;
                foreach (int u in comp) { float d = nodes[u].stored + nodes[u].gen; if (d > bestPick) { bestPick = d; source = u; } }
                foreach (int u in comp) if (u != source && sourcesBefore.Contains(nodes[u].k) && nodes[u].stored + nodes[u].gen >= bestPick * 0.9f) { source = u; break; }
                sourcesNow.Add(nodes[source].k);
                // tree from the source
                var order = new List<int>(); var inTree = new HashSet<int> { source }; queue.Enqueue(source); int loops = 0;
                while (queue.Count > 0)
                {
                    int u = queue.Dequeue(); order.Add(u);
                    foreach (int e in nodes[u].edges)
                    {
                        if (role[e] != -1) continue;
                        int v = already[e] == u ? na[e] : already[e];
                        if (inTree.Add(v)) { role[e] = 1; nodes[v].parent = u; nodes[v].parentEdge = e; queue.Enqueue(v); }
                        else if (nodes[v].parent == u) { role[e] = 2; nodes[v].parallels.Add(e); }
                        else if (nodes[u].parent == v) { role[e] = 2; nodes[u].parallels.Add(e); }
                        else { role[e] = 3; loops++; }
                    }
                }
                // bottom-up: each circuit asks for its own balance plus what the ones past it ask for
                bool hasPower = bestPick > 0.01f;
                for (int i = order.Count - 1; i >= 0; i--)
                {
                    var u = nodes[order[i]]; u.dem += u.cons - u.gen;
                    if (u.parent < 0) continue;
                    float f = hasPower ? Flow(u.dem) : 0f;
                    nodes[u.parent].dem += f; nodes[u.parent].size += u.size;
                    float piece = f / (1 + u.parallels.Count);
                    fl[u.parentEdge] = already[u.parentEdge] == u.parent ? piece : -piece;   // + = joint -> ring
                    foreach (int e in u.parallels) fl[e] = already[e] == u.parent ? piece : -piece;
                }
                string side = "";
                foreach (int e in nodes[source].edges) { side = (already[e] == source ? "joint side of " : "ring side of ") + frameBridges[e].l.name + " #" + frameBridges[e].c.i; break; }
                string line = "\n  " + comp.Count + " circuit(s), " + (order.Count - 1) + " carrying bridge path(s)" + (loops > 0 ? ", " + loops + " bridge(s) closing a loop (idle)" : "") + (hasPower ? " — source: " + side : " — no stored or generated power anywhere");
                sig.Append(line); sb.Append(line).Append(hasPower ? " (stores " + nodes[source].stored.ToString("F0") + " P, generates " + nodes[source].gen.ToString("F1") + " P/s)" : "");
            }
            sourcesBefore.Clear(); foreach (var k in sourcesNow) sourcesBefore.Add(k);
            string net = sig.ToString();   // only the shape of the network (the battery charge changes every frame)
            if (net != netLog) { netLog = net; if (DetailedLog) ModLog.Info("power bridge network:" + sb); }
            for (int e = 0; e < n; e++)
            {
                var (l, c) = frameBridges[e]; var pt = c.bridge; float q = Math.Abs(fl[e]);
                int direction = fl[e] > 1e-4f ? 1 : fl[e] < -1e-4f ? -1 : pt.direction;
                if (direction == 1) { WriteGhost(em, pt.ghostJ, 0f, q); WriteGhost(em, pt.ghostA, q, 0f); } else { WriteGhost(em, pt.ghostJ, q, 0f); WriteGhost(em, pt.ghostA, 0f, q); }
                bool loop = role[e] == 3; pt.q = q; pt.tLog += dt;
                if (direction != pt.direction || loop != pt.loop || pt.tLog >= 10f)
                {
                    pt.tLog = 0f; pt.direction = direction; pt.loop = loop;
                    var nj = nodes[already[e]]; var nA = nodes[na[e]]; var after = nj.parent == na[e] ? nj : nA;
                    if (!DetailedLog) continue;
                    if (loop) { ModLog.Info("power bridge: " + l.name + " #" + c.i + " — closes a loop with other slip rings: idle, the power goes the other way"); continue; }
                    ModLog.Info("power bridge: " + l.name + " #" + c.i + " — " + (direction == 1 ? "joint -> ring" : direction == -1 ? "ring -> joint" : "idle") + ": joint side stores " + nj.stored.ToString("F0") + " P (gen " + nj.gen.ToString("F1") + ", uses " + nj.cons.ToString("F1") + " P/s), ring side stores " + nA.stored.ToString("F0") + " P (gen " + nA.gen.ToString("F1") + ", uses " + nA.cons.ToString("F1") + " P/s) — bridging " + q.ToString("F1") + " P/s" + (after.size > 1 ? " (for " + after.size + " circuits past it)" : "") + (q > pt.maxCable ? " (ABOVE the cables' " + pt.maxCable.ToString("F0") + " P/s — they will burn)" : ""));
                }
            }
        }
        static void UndoBridge(EntityManager em, Channel c)
        {
            var pt = c.bridge; if (pt == null) return;
            try { if (Ecs.Alive(em, pt.ghostJ)) em.DestroyEntity(pt.ghostJ); } catch (Exception) { }
            try { if (Ecs.Alive(em, pt.ghostA)) em.DestroyEntity(pt.ghostA); } catch (Exception) { }
            c.bridge = null;
        }

        // ================================================================ 0.22.2: plugs of the pass-through ports
        // (Andre: "the untyped port is like the one on the Frame Quarter With Ports: no colour when disconnected and coloured when connected"). The game only does
        // this for blocks with SCTypeFrameWithPorts (SCTick_FrameWithPorts): it swaps the MESH of the plug renderer (CRPRendererData, DrawDataMeshReference
        // at +8 = MeshReference._instanceID) between _portMeshUnset / _portMeshInOut / _portMeshPower / _portMeshPlasma. Same here, by the cable
        // plugged into each pass-through port: data cable = Port InOut, power = Port Power, nothing = Port Unset. Works in the garage too.
        static TypeIndex tiRendData; static bool rdOk, rdFailed; static int colorWarnings, colorPasses;
        static int idUnset, idInOut, idPower; static bool idsOk, idsLogged;
        static readonly Dictionary<long, int> meshWritten = new Dictionary<long, int>();   // port -> instanceID written
        static readonly Dictionary<long, Entity> portRend = new Dictionary<long, Entity>();
        static bool PreparePlugs()
        {
            if (!rdOk && !rdFailed)
            {
                try { tiRendData = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<CRPRendererData>()); rdOk = true; }
                catch (Exception ex) { rdFailed = true; ModLog.Warn("pass-through plugs: CRPRendererData: " + ex.Message); }
            }
            if (!rdOk) return false;
            if (!idsOk)
            {
                var mu = GameMeshes.MeshOf("Port Unset"); var mi = GameMeshes.MeshOf("Port InOut"); var mp = GameMeshes.MeshOf("Port Power");
                if (mu == null || mi == null || mp == null) return false;
                idUnset = new MeshReference(mu)._instanceID; idInOut = new MeshReference(mi)._instanceID; idPower = new MeshReference(mp)._instanceID;
                idsOk = true;
            }
            return true;
        }
        static bool HasPassThrough(ulong hash, out string name, out int n)
        {
            name = null; n = 0;
            if (!JointSystem.KindFromHash(hash, out var kind, out _, out name) || name == null) return false;
            if (name.EndsWith("SlipRing")) { n = 4; return true; }
            if (kind != Kind.Bearing && kind != Kind.Piston && kind != Kind.Recoupler && kind != Kind.TurretSeat) return false;
            n = NPassThrough(name); return n > 0;
        }
        /// <summary>Renderer of the plug: the block's child with CRPRendererData at the port's position.</summary>
        static Entity PortRenderer(EntityManager em, Entity block, Entity port)
        {
            long k = Ecs.K(port);
            if (portRend.TryGetValue(k, out var r) && (Ecs.IsNull(r) || (Ecs.Alive(em, r) && Ecs.HasC(em, r, tiRendData)))) return r;
            r = Entity.Null;
            if (Ecs.HasC(em, port, tiRendData)) r = port;
            else if (Ecs.HasC(em, port, Ecs.tiLT))
            {
                var pp = Ecs.LT(em, port).Position;
                foreach (var f in Ecs.Children(em, block))
                {
                    if (f.Index == port.Index || !Ecs.HasC(em, f, tiRendData) || !Ecs.HasC(em, f, Ecs.tiLT)) continue;
                    if (Ecs.Dist(Ecs.LT(em, f).Position, pp) < 0.002f) { r = f; break; }
                }
            }
            portRend[k] = r; return r;
        }
        static readonly Dictionary<long, long> portPair = new Dictionary<long, long>();   // 0.23.1: port <-> port on the other side of the joint

        /// <summary>0.23.1 (Andre: "the frame with ports, when I connect a cable on one side, already shows on the other side the colour of the cable that
        /// will come out — can you do it?"): pairs joint port <-> ring port, from the GARAGE poses (GarageTransform, which stays on the
        /// block even after launching), so it works in the garage and in the simulation. Bearing/piston only: the ring on the axis (+Y of the block), in
        /// front, less than 5 cm sideways, the nearest. The simulation links take precedence (they are the real connection).</summary>
        static void Pairs(EntityManager em, List<Entity> blocks)
        {
            portPair.Clear();
            var rings = new List<(Entity e, string name, RigidTransform g)>();
            var jts = new List<(Entity e, string name, float reach, RigidTransform g)>();
            foreach (var b in blocks)
            {
                ulong h; try { h = Ecs.Prefab(em, b); } catch (Exception) { continue; }
                if (h == 0 || !JointSystem.KindFromHash(h, out var tp, out float stroke, out var name) || name == null) continue;
                if (!Garage.GT(em, b, out var g)) continue;
                if (name.EndsWith("SlipRing")) { rings.Add((b, name, g)); continue; }
                if (tp == Kind.Bearing) jts.Add((b, name, 1.2f, g));
                else if (tp == Kind.Piston) jts.Add((b, name, stroke + 1f, g));
            }
            if (rings.Count == 0 || jts.Count == 0) { LinkPairs(); return; }
            var usedSet = new HashSet<long>();
            foreach (var j in jts)
            {
                var up = Ecs.Rot(j.g.rot, Ecs.UY); float scale = JointSystem.ScaleFromName(j.name);
                Entity bestPick = Entity.Null; string nameA = null; float bestAo = 1e9f;
                foreach (var a in rings)
                {
                    if (usedSet.Contains(Ecs.K(a.e))) continue;
                    var d = Ecs.Sub(a.g.pos, j.g.pos); float ao = Ecs.Dot(d, up);
                    float lat = Ecs.Len(Ecs.Sub(d, Ecs.Mul(up, ao)));
                    if (ao < 0.2f * scale || ao > j.reach || lat > 0.05f) continue;
                    if (ao < bestAo) { bestAo = ao; bestPick = a.e; nameA = a.name; }
                }
                if (Ecs.IsNull(bestPick)) continue;
                usedSet.Add(Ecs.K(bestPick));
                var ps = Ecs.Ports(em, j.e); var pa = Ecs.Ports(em, bestPick);
                if (!Range(em, ps, j.name, out int i0, out int n0) || !Range(em, pa, nameA, out int i1, out int n1)) continue;
                int m = Math.Min(n0, n1);
                for (int i = 0; i < m; i++) { long kj = Ecs.K(ps[i0 + i]), ka = Ecs.K(pa[i1 + i]); portPair[kj] = ka; portPair[ka] = kj; }
            }
            LinkPairs();
        }
        static void LinkPairs()
        {
            foreach (var l in links.Values) foreach (var c in l.channels) { long kj = Ecs.K(c.pj), ka = Ecs.K(c.pa); portPair[kj] = ka; portPair[ka] = kj; }
        }

        static unsafe void TintAll(EntityManager em)
        {
            if (!JointSystem.HashesOk || !PreparePlugs()) return;
            if (++colorPasses % 10 == 0) { meshWritten.Clear(); portRend.Clear(); }   // check again every 5 s (the game may rebuild the part)
            int noRenderer = 0;
            var blocks = Ecs.Components(em); var seenSet = new HashSet<long>();
            foreach (var g in Garage.Blocks(em)) blocks.Add(g);   // 0.22.4: in the garage too (qComps only picks launched blocks)
            var list = new List<(Entity block, Entity port, long k)>(); var own = new Dictionary<long, int>();
            var unique = new List<Entity>();
            foreach (var b in blocks)
            {
                if (!seenSet.Add(Ecs.K(b))) continue;
                unique.Add(b);
                ulong h = Ecs.Prefab(em, b); if (h == 0 || !HasPassThrough(h, out var name, out int n)) continue;
                var ps = Ecs.Ports(em, b); if (!Range(em, ps, name, out int i0, out n)) continue;   // 0.23.1: the bearing's angle output is the LAST port — it doesn't count here
                for (int i = i0; i < i0 + n; i++)
                {
                    var port = ps[i]; long k = Ecs.K(port); int wants = idUnset;
                    if (Ecs.RawPortPub(em, port, out _, out _, out _, out var persist) && Ecs.IsCable(em, persist))
                        wants = IsPowerCable(em, persist) ? idPower : idInOut;
                    own[k] = wants; list.Add((b, port, k));
                }
            }
            Pairs(em, unique);
            foreach (var it in list)
            {
                int wants = own[it.k];
                // 0.23.1: with no cable on this side, show the colour of the cable on the OTHER side of the joint (like the Frame With Ports)
                if (wants == idUnset && portPair.TryGetValue(it.k, out var kp) && own.TryGetValue(kp, out var other)) wants = other;
                if (meshWritten.TryGetValue(it.k, out var scale) && scale == wants) continue;
                var r = PortRenderer(em, it.block, it.port);
                if (Ecs.IsNull(r)) { noRenderer++; meshWritten[it.k] = wants; continue; }
                var p = (byte*)em.GetComponentDataRawRW(r, tiRendData); if (p == null) continue;
                int current = *(int*)(p + Layout.RendDraw0);
                if (!idsLogged) { idsLogged = true; ModLog.Info("pass-through plugs: plug mesh ids — Unset " + idUnset + ", InOut " + idInOut + ", Power " + idPower + "; first plug renderer has " + current + (current == idUnset || current == idInOut || current == idPower ? " (matches)" : " (does NOT match — the swap may not show)")); }
                if (current != wants) *(int*)(p + Layout.RendDraw0) = wants;
                meshWritten[it.k] = wants;
            }
            if (noRenderer > 0 && colorWarnings++ < 1) ModLog.Warn("pass-through plugs: " + noRenderer + " port(s) without a plug renderer found");
        }
        /// <summary>Cable type from the cell plugged into the port: SCTypeCableCell._type (+1, SpaceshipCableType: 0 Data, 1 Power, 2 PowerNano, 3 Plasma).</summary>
        static unsafe bool IsPowerCable(EntityManager em, Entity cell)
        {
            try { var b = (byte*)em.GetComponentDataRawRO(cell, Ecs.tiCable); return b != null && (b[Layout.CellType] == 1 || b[Layout.CellType] == 2); } catch (Exception) { return false; }
        }

        /// <summary>Plugin exit / cleanup with the world alive: destroys the ghosts.</summary>
        public static void Shutdown(EntityManager em) { foreach (var l in links.Values) foreach (var c in l.channels) UndoBridge(em, c); links.Clear(); noRingLog.Clear(); meshWritten.Clear(); portRend.Clear(); }
        public static void Forget() { links.Clear(); frameBridges.Clear(); sourcesBefore.Clear(); netLog = ""; noRingLog.Clear(); hashes = false; meshWritten.Clear(); portRend.Clear(); portPair.Clear(); }
        public static string Situation()
        {
            if (links.Count == 0) return "no slip rings linked";
            var sb = new System.Text.StringBuilder(); sb.Append(links.Count).Append(" slip ring link(s):");
            foreach (var l in links.Values)
            {
                sb.Append("\n    ").Append(l.name).Append(':');
                foreach (var c in l.channels) sb.Append(" #").Append(c.i).Append('=').Append(c.direction == 1 ? "J>R" : c.direction == -1 ? "R>J" : c.direction == 3 ? "power" : c.direction == 2 ? "CONFLICT" : "-");
            }
            return sb.ToString();
        }
    }
}
