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
        public static bool Enabled = true, DetailedLog = true, GrowList = true;   // 0.31.6: GrowList = [Slip Ring] grow_circuit_list
        /// <summary>How many pass-through ports the block has (the LAST ones in the prefab's list).</summary>
        public static int NPassThrough(string name) => name == null ? 0 : SelfRing(name) ? 8 : name.StartsWith("SC_TurretSeat") ? 16 : name.EndsWith("SlipRing") ? 4 : name.StartsWith("SC_Mini") ? 1 : 4;
        /// <summary>0.31.36: the Bearing with Slip Ring (SC_BearingRing, "SC_BearingRing M") — a Bearing that carries its own ring. Its prefab
        /// lists 13 ports: 0-3 servo inputs, 4-7 pass-through (base), 8-11 ring (top quarter), 12 angle output. The 8 in the middle are its
        /// "pass-through" range: the servo and the Drive Shaft ignore all of them, all 8 plugs take the cable's colour, and pass-through i
        /// (4+i) is linked to ring port i (8+i) by Connect, with the block as its own ring — no ring is looked for on the axis.
        /// The name starts with "SC_Bearing" (NOutputs below: the angle output is its last port) and does NOT end in "SlipRing" (that
        /// ending means a standalone ring plate, here and in HasPassThrough / Pairs) — both on purpose.</summary>
        public static bool SelfRing(string name) => name != null && name.StartsWith("SC_BearingRing");
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
            public Entity fJ, fA;                          // 0.31.5: port at the other end of each side's cable (to follow chains of joints)
            public bool resolved, mine; public float resolveAt;   // 0.31.15: what the channel links is kept between passes (Resolve)
            public Entity rtPort; public byte rtVal = 255;        // 0.31.26 (item 6): in/out port whose _runtimeType (+21) this channel wrote, and the value
        }
        class Link { public string key, name; public Entity jointBlock, ring; public List<Channel> channels = new List<Channel>(); public bool alive, self; public int portSet; public float lostAt = -1f; }
        static readonly Dictionary<string, Link> links = new Dictionary<string, Link>();
        static float tScan = 9f; static int warnings; static ulong hRing, hMini; static bool hashes;
        static readonly string[] PLATES = { "left", "right", "back", "top" };   // order of the turret base port sets (modelos_v2 TURRET_PORTS)

        // 0.31.15 (Andre, 11/11: "every time the game stutters, the circuits that go through the mod lose their charge, and right then the ship
        // loses thrust and drops its nose"): everything here ran once per DRAWN frame, while the game works its circuits once per physics tick.
        // At 15 fps that is 4 ticks per frame, and after a stutter many more in a row — all of them with the bridge as the last frame left it
        // (the far side's consumption changed, the game rebuilt a circuit: no power or no signal until the next frame). Now there are three paces:
        //  - Tick: every game tick, right before the game updates its circuits (prefix of SpaceshipComponentsTickPostGroup): puts the data
        //    links back and recomputes how much each power bridge carries from the consumption of THIS tick. Cheap: it uses what the full pass
        //    found (which ports, which circuits, which components) and only reads values. A circuit the game rebuilt makes it run the full
        //    pass right there, in the tick.
        //  - Body (the full pass): what every frame used to do, at most every 0.1 s while the ticks run (every frame when they don't: garage,
        //    pause). It follows changes: a channel's cables are walked again every 0.4-0.6 s (Resolve), not every frame.
        //  - Scan (0.5 s): which ring belongs to which joint, the plugs' colours.
        static float lastTick = -9f, lastBody = -9f; static bool resolveAll, resolveNow;
        public static double TickMs, TickPeakMs; public static int Ticks, TickBodies, TickScans, DataResets, LossEvents, LossTicks;
        // 0.31.16 (Andre, 12/11: "the mod's step to set the cables up is already born late"). Three places where the mod only reacted after the
        // game had already acted:
        //  1) the game rebuilds a circuit and updates its power in the SAME tick, and the bridge only came back on the next one. Now the mod
        //     runs the game's own rebuild (SpaceshipElectricCreateCircuits) itself, at the start of the tick, puts the bridge into the new
        //     circuit, and only then lets the game update the power ([Slip Ring] rebuild_circuits_in_tick);
        //  2) when a joint was born, split, merged or got a new part, the ring's link was dropped and only remade on the next 0.5 s scan
        //     (Andre's log: gone at 22:20:38.117, back at 38.633). Now the scan runs in the very tick the joints change (LinkSignature), and
        //     a link that stopped being found stays up for a moment (Grace) instead of being cut on the spot;
        //  3) launch: the same scan-on-change, instead of two 0.5 s clocks in a row.
        // Plus a log line whenever something fed by a bridge, or any electric thruster, is short of power (Watch).
        public static bool RebuildInTick = true, WatchPower = true;
        const float Grace = 1f;
        static long linkSig = long.MinValue; static bool tickRebuilt, tickRelinked, prevRebuilt, prevRelinked, rootsPending; static int tickNo, resolveStreak;
        public static void Step(EntityManager em, float dt)
        {
            if (!Enabled) return;
            tScan += dt;
            bool changed = SignatureChanged();
            if (tScan >= 0.5f || changed) { bool timer = tScan >= 0.5f; tScan = 0f; DoScan(em); if (timer) { try { TintAll(em); } catch (Exception ex) { if (colorWarnings++ < 3) ModLog.Warn("pass-through plugs: " + ex.Message); } } }
            float now = Ecs.Now; bool ticking = now - lastTick < 0.25f;
            if (!changed && ticking && now - lastBody < 0.1f) return;
            Body(em, ticking ? Math.Min(0.5f, now - lastBody) : dt);
        }
        static bool SignatureChanged() { long sig = JointSystem.LinkSignature(); if (sig == linkSig) return false; linkSig = sig; return true; }
        static void DoScan(EntityManager em) { try { Scan(em); } catch (Exception ex) { if (warnings++ < 3) ModLog.Warn("slip ring: " + ex.Message); } }
        /// <summary>Every game tick (CrapMechanicsPlugin.PatchSystems, before the game's own circuit systems run).</summary>
        public static void Tick(EntityManager em)
        {
            if (!Enabled) return;
            tickNo++;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            float now = Ecs.Now; float dt = Math.Min(0.1f, Math.Max(0.001f, now - lastTick)); lastTick = now;
            try
            {
                bool relinked = false;
                if (SignatureChanged()) { DoScan(em); relinked = true; TickScans++; }
                if (links.Count == 0) { firstLinkAt = -1f; return; }
                GameRebuild();
                int resets = 0;
                foreach (var l in links.Values) foreach (var c in l.channels) if (c.resolved && c.mine && (c.direction == 1 || c.direction == -1) && FastWrite(em, c)) resets++;
                DataResets += resets;
                // rootsPending: a bridge side lost its circuit and the last full pass did not find the new one yet — look again every tick
                // (it used to wait for the next frame's pass), for the few ticks RootCached allows before calling it "no circuit"
                // settle: a link had to be rewritten in the last full pass (a chain of joints settles one joint per pass) — the next
                // pass runs in the next tick instead of a tenth of a second later; at most 8 ticks in a row (two channels fighting over
                // one input would otherwise keep the full pass running every tick)
                bool settle = resolveAll && resolveStreak < 8; if (!resolveAll) resolveStreak = 0;
                bool rebuilt = false;
                if (relinked || !FastFlows(em) || rootsPending || settle) { Body(em, dt); TickBodies++; rebuilt = !relinked && !settle; if (settle) resolveStreak++; }
                prevRebuilt = tickRebuilt; prevRelinked = tickRelinked; tickRebuilt = rebuilt; tickRelinked = relinked;
                if (WatchPower) Watch(em);
            }
            catch (Exception ex) { if (netWarnings++ < 3) ModLog.Warn("slip ring (tick): " + ex.Message); }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            TickMs += ms; if (ms > TickPeakMs) TickPeakMs = ms; Ticks++;
        }
        // ---- 0.31.16: the game's own circuit rebuild, run by the mod at the start of the tick
        static SystemHandle hCreate; static IntPtr hCreateWorld; static bool hCreateFailed;
        static void GameRebuild()
        {
            if (!RebuildInTick) return;
            try
            {
                var w = World.DefaultGameObjectInjectionWorld; if (w == null || !w.IsCreated) return;
                if (hCreateWorld != w.Pointer)
                {
                    hCreateWorld = w.Pointer; hCreateFailed = false;
                    hCreate = w.GetExistingSystem(Il2CppInterop.Runtime.Il2CppType.Of<SpaceshipElectricCreateCircuits>());
                    if (!w.Unmanaged.IsSystemValid(hCreate)) { hCreateFailed = true; ModLog.Warn("slip ring: the game's circuit system was not found in this world — a circuit the game rebuilds keeps costing the bridge one tick"); return; }
                    ModLog.Info("slip ring: the game's circuit rebuild (SpaceshipElectricCreateCircuits) now runs at the start of each tick, before the bridges are checked ([Slip Ring] rebuild_circuits_in_tick)");
                }
                if (hCreateFailed) return;
                try { hCreate.Update(w.Unmanaged); }
                finally { Ecs.AfterGameCode(w.EntityManager); }   // 0.31.35: a game system ran (it may schedule jobs and rebuild circuit entities): the read cache asks the game again
            }
            catch (Exception ex) { hCreateFailed = true; ModLog.Warn("slip ring: running the game's circuit rebuild from the mod failed — turned off for this session (a circuit the game rebuilds costs the bridge one tick again): " + ex.Message); }
        }
        static void Body(EntityManager em, float dt)
        {
            lastBody = Ecs.Now; rootIndex = null;
            resolveNow = resolveAll; resolveAll = false;
            frameBridges.Clear(); carrier.Clear();
            foreach (var l in links.Values) foreach (var c in l.channels) { try { Apply(em, l, c, dt); } catch (Exception ex) { if (c.warnings++ < 3) ModLog.Warn("slip ring (" + l.name + " #" + c.i + "): " + ex.Message); } }
            resolveNow = false;
            try { Solve(em, dt); } catch (Exception ex) { if (netWarnings++ < 3) ModLog.Warn("power bridge network: " + ex.Message); }
            rootsPending = false;
            foreach (var l in links.Values) { foreach (var c in l.channels) { var pt = c.bridge; if (pt == null || c.direction != 3) continue; if ((Ecs.IsNull(pt.rootJ) && pt.missJ > 0 && pt.missJ < 3) || (Ecs.IsNull(pt.rootA) && pt.missA > 0 && pt.missA < 3)) { rootsPending = true; break; } } if (rootsPending) break; }
        }

        // ================================================================ search: active joint -> ring on the moving piece
        struct Candidate { public Entity e, part; public float3 pos; }
        static List<Candidate> Rings(EntityManager em)
        {
            var r = new List<Candidate>();
            if (!hashes) { hRing = Ecs.HashPrefab("SC_SlipRing"); hMini = Ecs.HashPrefab("SC_MiniSlipRing"); hashes = hRing != 0; if (!hashes) return r; }
            // 0.31.15: the rings come from the block index (it read every block of the ship here, twice a second)
            var set = Ecs.Launched(em);
            for (int pass = 0; pass < 2; pass++)
                foreach (var b in set.Of(pass == 0 ? hRing : hMini))   // 0.22.4: any ring on any joint
                {
                    if (pass == 1 && hMini == hRing) break;
                    if (!Ecs.HasC(em, b, Ecs.tiLTW)) continue;
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
                if (!Ecs.Has(em, e, tiGTa)) return false;
                g = *(RigidTransform*)Ecs.RO(em, e, tiGTa); return true;
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
        static readonly HashSet<long> recBlocks = new HashSet<long>();
        /// <summary>0.31.13: ring near a point (any part), the nearest within `max` metres.</summary>
        static Entity RingNearPoint(List<Candidate> rings, float3 p0, float max, HashSet<long> usedSet)
        {
            Entity bestPick = Entity.Null; float md = max;
            foreach (var a in rings) { if (usedSet.Contains(Ecs.K(a.e))) continue; float d = Ecs.Dist(a.pos, p0); if (d < md) { md = d; bestPick = a.e; } }
            return bestPick;
        }
        /// <summary>0.31.13 (Andre: "recoupler > slip ring < decoupler, car - ship: the ring is glued to the RECOUPLER, not to the decoupler, and
        /// everything on the decoupler's end falls when it fires"; "I'd use two recouplers in a row"): before, a recoupler only linked AFTER it had
        /// re-attached (Coupled), to a ring glued to the DECOUPLER — a car parked on a decoupler that never fired never got its ports across.
        /// Now, in any state (held by an unfired decoupler, pulling, merged): (1) two recouplers touching (centres up to 0.75 m apart) link their
        /// 4 pass-through ports to each other, port i with port i; (2) a recoupler with a slip ring glued to it (up to 0.75 m) links to that ring.
        /// They unlink as soon as they move apart (the car leaving).</summary>
        static void RecouplerLinks(EntityManager em, List<Candidate> rings, HashSet<long> usedSet)
        {
            var list = new List<(Entity e, float3 pos)>(); recBlocks.Clear();
            foreach (var r in Couplers.All()) { if (Ecs.IsNull(r.block) || !Ecs.Alive(em, r.block)) continue; recBlocks.Add(Ecs.K(r.block)); list.Add((r.block, Ecs.Position(em, r.block))); }
            var done = new HashSet<long>();
            foreach (var l in links.Values) if (l.alive) { done.Add(Ecs.K(l.jointBlock)); done.Add(Ecs.K(l.ring)); }
            // 0.31.14 (Andre, 11/11: "I need the ship's power in the car while it is docked" — two recouplers glued in the garage are ONE part,
            // nothing ever releases them): the layout that undocks is ship -> slip ring -> Decoupler (release face to the car) -> Recoupler (car).
            // The ring stays on the ship when the decoupler fires. Until 0.31.13 that ring only linked AFTER a recouple (Couplers.Coupled);
            // in the garage pose (decoupler not fired yet) nothing passed. Now: a recoupler touching a decoupler (<= 0.75 m) links to the
            // ring behind that decoupler (its part, <= 0.75 m from it, and nearer to it than to the recoupler), in any state. Drops when the car
            // drives away, comes back when it docks again.
            if (rings.Count > 0)
            {
                var decs = new List<(Entity e, float3 pos)>();
                foreach (var d in Couplers.Decouplers(em)) if (Ecs.Alive(em, d) && !recBlocks.Contains(Ecs.K(d)) && !JointSystem.KindFromHash(Ecs.Prefab(em, d), out _, out _, out _)) decs.Add((d, Ecs.Position(em, d)));
                foreach (var (e, pos) in list)
                {
                    if (done.Contains(Ecs.K(e))) continue;
                    Entity dec = Entity.Null; float3 pd = default; float md = 0.75f;
                    foreach (var d in decs) { float dd = Ecs.Dist(d.pos, pos); if (dd < md) { md = dd; dec = d.e; pd = d.pos; } }
                    if (Ecs.IsNull(dec)) continue;
                    var partD = Ecs.Parent(em, dec);
                    Entity pick = Entity.Null; float best = 0.75f;
                    foreach (var a in rings)
                    {
                        if (usedSet.Contains(Ecs.K(a.e)) || a.part.Index != partD.Index) continue;
                        float dDec = Ecs.Dist(a.pos, pd); if (dDec >= best || dDec >= Ecs.Dist(a.pos, pos) - 0.05f) continue;
                        best = dDec; pick = a.e;
                    }
                    if (Ecs.IsNull(pick)) continue;
                    Connect(em, e, "SC_Recoupler", 0, pick, usedSet); done.Add(Ecs.K(e)); done.Add(Ecs.K(pick));
                }
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (done.Contains(Ecs.K(list[i].e))) continue;
                int best = -1; float md = 0.75f;
                for (int j = 0; j < list.Count; j++)
                {
                    if (j == i || done.Contains(Ecs.K(list[j].e))) continue;
                    float d = Ecs.Dist(list[i].pos, list[j].pos); if (d < md) { md = d; best = j; }
                }
                if (best < 0) continue;
                var a = list[i].e; var b = list[best].e; if (Ecs.K(a) > Ecs.K(b)) { var t = a; a = b; b = t; }
                Connect(em, a, "SC_Recoupler", 0, b, usedSet); usedSet.Add(Ecs.K(a));
                done.Add(Ecs.K(a)); done.Add(Ecs.K(b));
            }
            if (rings.Count == 0) return;
            foreach (var (e, pos) in list)
            {
                if (done.Contains(Ecs.K(e))) continue;
                var ring = RingNearPoint(rings, pos, 0.75f, usedSet); if (Ecs.IsNull(ring)) continue;
                Connect(em, e, "SC_Recoupler", 0, ring, usedSet); done.Add(Ecs.K(e));
            }
        }
        static void Scan(EntityManager em)
        {
            foreach (var l in links.Values) l.alive = false;
            var rings = Rings(em); var usedSet = new HashSet<long>();
            // 0.31.36: Bearing with Slip Ring — linked to itself as soon as it flies, in any state, with or without other rings in the ship
            foreach (var j in JointSystem.SelfRingJoints(em)) Connect(em, j.block, j.name, 0, j.block, usedSet);
            if (rings.Count > 0)
            {
                foreach (var j in JointSystem.ActiveJoints())
                {
                    if ((j.kind != Kind.Bearing && j.kind != Kind.Piston) || Ecs.IsNull(j.partB)) continue;
                    if (j.selfRing) continue;   // 0.31.36: its pass-through ports only ever meet its own ring (no search on the axis)
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
            try { RecouplerLinks(em, rings, usedSet); } catch (Exception ex) { if (warnings++ < 3) ModLog.Warn("slip ring: recouplers: " + ex.Message); }   // 0.31.13
            ringsTaken = usedSet;
            Clean(em);
        }
        static HashSet<long> ringsTaken = new HashSet<long>();
        static void Connect(EntityManager em, Entity jointBlock, string name, int portSet, Entity ring, HashSet<long> usedSet)
        {
            usedSet.Add(Ecs.K(ring));
            string key = Ecs.K(jointBlock) + ":" + portSet;
            if (links.TryGetValue(key, out var l) && l.ring.Index == ring.Index && l.ring.Version == ring.Version) { l.alive = true; return; }
            if (l != null) { TurnOff(em, l, "the slip ring changed"); links.Remove(key); }
            var ps = Ecs.Ports(em, jointBlock); var pa = Ecs.Ports(em, ring); int n = NPassThrough(name);
            bool self = SelfRing(name) && ring.Index == jointBlock.Index && ring.Version == jointBlock.Version;   // 0.31.36: Bearing with Slip Ring
            // 0.31.13: the other side can be another RECOUPLER (two recouplers touching): its 4 pass-through ports play the ring's part
            bool ringIsRec = recBlocks.Contains(Ecs.K(ring));
            if (ringIsRec) { if (!Range(em, pa, "SC_Recoupler", out int rb, out int rn)) { ModLog.Warn("SC_Recoupler " + ring.Index + ": the prefab has " + pa.Length + " port(s), expected at least 4 — rebuild the bundle in the Modkit"); return; } var tr = new Entity[rn]; Array.Copy(pa, rb, tr, 0, rn); pa = tr; }
            Entity[] lat;
            if (self)
            {
                // its 8 "pass-through" ports: the first 4 are the base's (joint side), the last 4 the ring's (ring side)
                if (!Range(em, ps, name, out int b0, out int n0) || n0 != 8)
                {
                    string msg = "the prefab has " + ps.Length + " port(s), expected 13";
                    if (!noRingLog.TryGetValue(key, out var prv) || prv != msg) { noRingLog[key] = msg; ModLog.Warn(name + " " + jointBlock.Index + ": " + msg + " — its built-in slip ring is not linked; rebuild the bundle in the Modkit"); }
                    return;
                }
                lat = new Entity[4]; Array.Copy(ps, b0, lat, 0, 4); pa = new Entity[4]; Array.Copy(ps, b0 + 4, pa, 0, 4);
            }
            else if (name != null && name.StartsWith("SC_TurretSeat")) { if (ps.Length < 16) { ModLog.Warn(name + " " + jointBlock.Index + ": the prefab has " + ps.Length + " port(s), expected 16 — rebuild the bundle in the Modkit"); return; } lat = new Entity[4]; Array.Copy(ps, ps.Length - 16 + portSet * 4, lat, 0, 4); }
            else { if (!Range(em, ps, name, out int begin, out n)) { ModLog.Warn(name + " " + jointBlock.Index + ": the prefab has " + ps.Length + " port(s), expected at least " + NPassThrough(name) + " — rebuild the bundle in the Modkit"); return; } lat = new Entity[n]; Array.Copy(ps, begin, lat, 0, n); }
            int m = Math.Min(lat.Length, pa.Length);
            l = new Link { key = key, jointBlock = jointBlock, ring = ring, portSet = portSet, alive = true, self = self,
                           name = name + " " + jointBlock.Index + (name != null && name.StartsWith("SC_TurretSeat") ? " (" + PLATES[portSet] + " plate)" : "") +  " <-> " + (self ? "its own ring" : (ringIsRec ? "Recoupler " : pa.Length > 0 && Ecs.Prefab(em, ring) == hMini ? "Mini Slip Ring " : "Slip Ring ") + ring.Index) };
            for (int i = 0; i < m; i++) l.channels.Add(new Channel { pj = lat[i], pa = pa[i], i = i });
            Rematch(em, l, pa);
            links[key] = l;
            ModLog.Info("slip ring: " + l.name + " — " + m + " channel(s) (part " + Ecs.Parent(em, jointBlock).Index + " <-> part " + Ecs.Parent(em, ring).Index + ")");
            if (self)
            {
                // 0.31.36: which port met which, as linked (after the power-with-power matching above)
                noRingLog.Remove(key);
                var sb = new System.Text.StringBuilder("slip ring: " + l.name + " — built-in ring linked without a search:");
                foreach (var c in l.channels) sb.Append(" pass-through ").Append(c.i + 1).Append(" (port ").Append(Ecs.PortIndex(em, c.pj)).Append(", entity ").Append(c.pj.Index).Append(") <-> ring ").Append(Array.IndexOf(pa, c.pa) + 1).Append(" (port ").Append(Ecs.PortIndex(em, c.pa)).Append(", entity ").Append(c.pa.Index).Append(");");
                ModLog.Info(sb.ToString());
            }
        }
        /// <summary>0.31.2 (Andre's wings, 31/10: "the solar panels go through slip rings to the main area with the batteries" — yet each wing's
        /// mini piston said "a POWER cable on the joint's port and nothing on the ring's port #0"): a Mini Piston has 1 pass-through port and the
        /// slip ring 4, and port i only ever met port i, so a power cable on any other port of the ring never got across — the cameras on the wings
        /// lived on the solar panel alone and went dark when the wing turned away from the sun. Now a power cable meets a power cable: a channel
        /// with power on one side only takes the other side's power port from wherever it is, as long as nothing else is plugged into what it
        /// gives up (no data link is ever moved).</summary>
        static unsafe void Rematch(EntityManager em, Link l, Entity[] ring)
        {
            try
            {
                int Kind(Entity port) { if (!Ecs.RawPortPub(em, port, out _, out _, out _, out var persist) || !Ecs.IsCable(em, persist)) return 0; return IsPowerCable(em, persist) ? 2 : 1; }   // 0 nothing, 1 data, 2 power
                var owner = new Dictionary<long, Channel>(); foreach (var c in l.channels) owner[Ecs.K(c.pa)] = c;
                // each joint power port that faces no power on the ring looks for a ring power port (a channel with power only on the ring
                // side is covered too: it is the 'other' that gives its ring port away)
                foreach (var c in l.channels)
                {
                    if (Kind(c.pj) != 2 || Kind(c.pa) == 2) continue;
                    foreach (var r in ring)
                    {
                        if (Kind(r) != 2) continue;
                        owner.TryGetValue(Ecs.K(r), out var other);
                        if (other == c) continue;
                        if (other != null && Kind(other.pj) == 2) continue;   // already meets power over there
                        // 0.31.5 (Bearing 116193 / Mini Slip Ring 116274, 31/10: power on ring #1 and joint #3, the other cables on ring #3 and
                        // joint #1 — cross-wired, the swap was blocked because both "gave up" a cable): nothing that works is moved here — c's
                        // ring port faced power on the joint (nothing passes between power and data/nothing), and so did the other channel's
                        // joint port (it faced the power port r). So the two channels just swap ring ports, and if both leftovers are data
                        // cables they now meet each other too.
                        int kOld = Kind(c.pa), kOther = other != null ? Kind(other.pj) : 0;
                        var old = c.pa; c.pa = r; owner[Ecs.K(r)] = c;
                        if (other != null) { other.pa = old; owner[Ecs.K(old)] = other; } else owner.Remove(Ecs.K(old));
                        ModLog.Info("slip ring: " + l.name + " #" + c.i + ": the power cable on the ring is on another port — matched power with power (ring port " + Array.IndexOf(ring, r) + " instead of " + Array.IndexOf(ring, old) + ")"
                            + (other != null && kOld == 1 && kOther == 1 ? "; #" + other.i + " gets ring port " + Array.IndexOf(ring, old) + ", so its data cable meets the one on the ring" : other != null ? "; #" + other.i + " gets ring port " + Array.IndexOf(ring, old) : ""));
                        break;
                    }
                }
            }
            catch (Exception ex) { ModLog.Warn("slip ring: " + l.name + ": matching the power cables: " + ex.Message); }
        }

        static void Clean(EntityManager em)
        {
            var deadOnes = new List<string>(); float now = Ecs.Now;
            foreach (var kv in links)
            {
                var l = kv.Value;
                if (!Ecs.Alive(em, l.jointBlock) || !Ecs.Alive(em, l.ring)) { deadOnes.Add(kv.Key); continue; }
                if (l.alive) { l.lostAt = -1f; continue; }
                // 0.31.16: a bearing / piston / turret whose ring was not found THIS scan (the joint is splitting, merging or getting a new
                // part) keeps its link for a moment — it used to be cut here and remade half a second later, with everything behind it dark.
                // The recoupler's links go by distance (the car drove away): those end at once.
                // And only while no other joint took that ring in this scan.
                bool byDistance = l.name != null && l.name.StartsWith("SC_Recoupler");
                if (!byDistance && !ringsTaken.Contains(Ecs.K(l.ring)) && (JointSystem.HasJoint(l.jointBlock) || JointSystem.HasTurret(Ecs.K(l.jointBlock))))
                {
                    if (l.lostAt < 0f) l.lostAt = now;
                    if (now - l.lostAt < Grace) continue;
                }
                deadOnes.Add(kv.Key);
            }
            foreach (var k in deadOnes) { TurnOff(em, links[k], "joint gone, ring removed or no longer on the moving piece"); links.Remove(k); }
        }
        static void TurnOff(EntityManager em, Link l, string reason)
        {
            foreach (var c in l.channels) { UndoBridge(em, c); ReleaseRuntimeType(em, c); }
            ModLog.Info("slip ring: " + l.name + " — gone (" + reason + ")");
        }

        // ================================================================ per frame: data (runtimeEnd) or power (ghosts)
        static void Apply(EntityManager em, Link l, Channel c, float dt)
        {
            if (!c.resolved || resolveNow || Ecs.Now >= c.resolveAt) Resolve(em, l, c);
            if (!c.mine) { if (c.direction == 3) UndoBridge(em, c); return; }
            // a link that had to be (re)written means a cable changed hands somewhere: every channel looks again on the next pass (a chain of
            // joints settles one joint per pass, as it always did)
            if (c.direction == 1 || c.direction == -1) { if (FastWrite(em, c)) resolveAll = true; }
            else if (c.direction == 3) Power(em, l, c, c.fJ, c.fA, dt);
        }
        /// <summary>The data link of a resolved channel: the input over there reads the output over here (the game's _runtimeEndEntity).
        /// True if it had to be written (the game resets it when the ship changes).</summary>
        static bool FastWrite(EntityManager em, Channel c)
        {
            var input = c.direction == 1 ? c.fA : c.fJ; var output = c.direction == 1 ? c.fJ : c.fA;
            if (!Ecs.Alive(em, output)) { c.resolved = false; return false; }
            if (!Write(em, input, output)) return false;
            c.writes++; Ecs.ForgetPath(input); return true;
        }
        /// <summary>What the channel links (the slow part: walks both cables). Kept for 0.4-0.6 s; the passes in between only act on it.</summary>
        static void Resolve(EntityManager em, Link l, Channel c)
        {
            Ecs.ForgetPath(c.pj); Ecs.ForgetPath(c.pa);
            var fJ = Ecs.Source(em, c.pj, out _); var fA = Ecs.Source(em, c.pa, out _);
            // 0.31.26 (item 5): a side whose cable burnt through counts as nothing plugged in — the bridge drops (UndoBridge below), as a
            // cable the game burns stops carrying power. Before, the mod followed the cells regardless and kept feeding the far side.
            // (only a power cable can burn — melting only heats Power/PowerNano cells — so a data cable is never walked for this)
            bool burntJ = PowerCableAt(em, c.pj) && Burnt(em, c.pj), burntA = PowerCableAt(em, c.pa) && Burnt(em, c.pa);
            if (burntJ) fJ = Entity.Null; if (burntA) fA = Entity.Null;
            byte tJ = 255, tA = 255;
            bool okJ = !Ecs.IsNull(fJ) && Ecs.Port(em, fJ, out tJ, out _, out _), okA = !Ecs.IsNull(fA) && Ecs.Port(em, fA, out tA, out _, out _);
            if (!okJ) tJ = 255; else tJ = Ecs.EffectiveType(em, fJ);   // 0.24.0: the runtime type wins when the setup doesn't give the direction
            if (!okA) tA = 255; else tA = Ecs.EffectiveType(em, fA);
            // 0.31.4 (asas do Andre: nave -> pistao mini -> bearing -> bearing mini -> camera): na corrente de juntas o cabo de energia do anel
            // vai ate a porta de PASSAGEM da proxima junta, que nao tem tipo — olhando so o tipo da porta da outra ponta, a energia "nao era
            // energia" e a ponte nao ligava. Agora conta tambem o proprio cabo ligado na porta (o mesmo criterio do Rematch).
            bool enJ = !burntJ && (okJ && tJ == Ecs.PortPower || PowerCableAt(em, c.pj)), enA = !burntA && (okA && tA == Ecs.PortPower || PowerCableAt(em, c.pa));
            int direction; string desc;
            if (burntJ || burntA) { direction = 0; desc = "the power cable on the " + (burntJ ? "joint's" : "ring's") + " port is burnt through — nothing passes (as the game cuts a burnt cable)"; }
            else if (enJ && enA) { direction = 3; desc = "power bridge"; }
            // 0.29.1: say WHICH side has the power cable — on the turret seat the 16 plugs look alike and this was the only clue left
            else if (enJ || enA) { direction = 0; desc = "a POWER cable on the " + (enJ ? "joint's" : "ring's") + " port and a data cable (or nothing) on the "
                                      + (enJ ? "ring's" : "joint's") + " port with the same number (#" + c.i + ") — nothing passes: both have to be power, at the same number"; }
            else
            {
                // 0.31.26 (item 6): an in/out port (DataInOut: game Wireless Transmitter, LRT, ...) whose cable ends on this joint's pass-through
                // port never gets a direction from the game — UpdatePortsDataInOutJob stops at an Unset port that is not a Frame With Ports
                // [recon/Spaceship__part_b.cs:207-245] — and GetInputs only copies between a port with the BYTE +21 == DataInput and one with
                // +21 == DataOutput [recon/Spaceship__part_a.cs:709-714]. So the joint does what the Frame With Ports does in that job: when the
                // other side is a port with a direction, the in/out port gets the opposite one (part_b:218-222). Put back to DataInOut when the
                // channel no longer gives it (as the game does when the end is gone, part_b:211).
                // (only where this side runs the channel — the same rule as the data link itself, `mine` below)
                if (Multi.RingOnClient || Multi.MyPart(em, Ecs.Parent(em, l.jointBlock))) RuntimeTypeAcross(em, l, c, okJ ? fJ : Entity.Null, okA ? fA : Entity.Null, ref tJ, ref tA);
                else ReleaseRuntimeType(em, c);
                bool outJ = okJ && tJ == Ecs.PortOutput, outA = okA && tA == Ecs.PortOutput;
                bool entJ = okJ && (tJ == Ecs.PortInput || tJ == JointSystem.PortUntyped), entA = okA && (tA == Ecs.PortInput || tA == JointSystem.PortUntyped);
                if (outJ && outA) { direction = 2; desc = "OUTPUTS on both sides (port " + fJ.Index + " and port " + fA.Index + ") — nothing passes"; }
                else if (outJ && entA) { direction = 1; desc = "data joint -> ring: output " + fJ.Index + " (block " + Ecs.Parent(em, fJ).Index + ") -> input " + fA.Index + " (block " + Ecs.Parent(em, fA).Index + ")"; }
                else if (outA && entJ) { direction = -1; desc = "data ring -> joint: output " + fA.Index + " (block " + Ecs.Parent(em, fA).Index + ") -> input " + fJ.Index + " (block " + Ecs.Parent(em, fJ).Index + ")"; }
                else if (okJ != okA)
                {
                    // 0.31.37 (Andre, 23/11: "it did not pass the value to the end point"): one side cabled and the other empty used to say
                    // "no output on either side", which tells nobody what to do. Name the two ports that have to meet — for a built-in ring
                    // the numbers of the holes, which is the only way to see from the outside which one pairs with which.
                    string one = l.self ? "pass-through " + (c.i + 1) : "the joint's port #" + c.i;
                    string two = l.self ? "ring port " + (c.i + 1) : "the ring's port #" + c.i;
                    direction = 0;
                    desc = "a cable on " + (okJ ? one : two) + " reaches a port, and " + (okJ ? two : one) + " has no cable that reaches one"
                         + " — nothing passes: a channel needs both, with the same number"
                         + (l.self ? " (what each hole pairs with is in its hint, when you aim at it)" : "");
                }
                else { direction = 0; desc = okJ || okA ? "no output on either side" : "nothing connected"; }
            }
            if (direction == 3 || direction == 0 && (enJ || enA || burntJ || burntA)) ReleaseRuntimeType(em, c);   // 0.31.26 (item 6): a power channel writes no direction
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
            c.fJ = fJ; c.fA = fA;
            c.mine = Multi.RingOnClient || Multi.MyPart(em, Ecs.Parent(em, l.jointBlock));
            c.resolved = true; c.resolveAt = Ecs.Now + 0.4f + ((c.i * 37 + l.jointBlock.Index * 13) % 200) / 1000f;
        }
        /// <summary>0.31.26 (item 6): see Resolve. `fJ` / `fA` = the ports at the far ends of the two cables (Null when nothing). Writes the
        /// in/out side's _runtimeType only when the game left it without a direction (3) or it still holds this channel's own value, and
        /// only when the other side has a direction (0 or 1). tJ / tA are updated so the channel links in this same pass.</summary>
        static unsafe void RuntimeTypeAcross(EntityManager em, Link l, Channel c, Entity fJ, Entity fA, ref byte tJ, ref byte tA)
        {
            Entity want = Entity.Null; byte val = 255;
            bool ioJ = InOutFree(em, c, fJ), ioA = InOutFree(em, c, fA);
            if (ioJ && !ioA && (tA == Ecs.PortInput || tA == Ecs.PortOutput)) { want = fJ; val = tA == Ecs.PortOutput ? Ecs.PortInput : Ecs.PortOutput; }
            else if (ioA && !ioJ && (tJ == Ecs.PortInput || tJ == Ecs.PortOutput)) { want = fA; val = tJ == Ecs.PortOutput ? Ecs.PortInput : Ecs.PortOutput; }
            if (!Ecs.IsNull(c.rtPort) && (want.Index != c.rtPort.Index || want.Version != c.rtPort.Version || val != c.rtVal)) ReleaseRuntimeType(em, c);
            if (Ecs.IsNull(want)) return;
            try
            {
                var r = (byte*)Ecs.RO(em, want, Ecs.tiPort); if (r == null) return;
                if (r[21] != val)
                {
                    var w = (byte*)em.GetComponentDataRawRW(want, Ecs.tiPort); if (w == null) return;
                    w[21] = val;
                    ModLog.Info("slip ring: " + l.name + " #" + c.i + ": in/out port " + want.Index + " (block " + Ecs.Parent(em, want).Index + ") set to " + (val == Ecs.PortInput ? "DataInput" : "DataOutput") + " in the game's port, from the port on the other side of the joint (as a Frame With Ports does)");
                }
                c.rtPort = want; c.rtVal = val;
                if (want.Index == fJ.Index && want.Version == fJ.Version) tJ = val; else tA = val;
            }
            catch (Exception ex) { if (c.warnings++ < 3) ModLog.Warn("slip ring (" + l.name + " #" + c.i + "): writing the port direction: " + ex.Message); }
        }
        /// <summary>An in/out port (setup DataInOut) that the game left without a direction, or that holds the value this channel wrote.</summary>
        static unsafe bool InOutFree(EntityManager em, Channel c, Entity p)
        {
            if (Ecs.IsNull(p) || !Ecs.HasC(em, p, Ecs.tiPort)) return false;
            var b = (byte*)Ecs.RO(em, p, Ecs.tiPort); if (b == null || b[20] != 3) return false;
            if (b[21] == 3) return true;
            return p.Index == c.rtPort.Index && p.Version == c.rtPort.Version && b[21] == c.rtVal;
        }
        /// <summary>0.31.26 (item 6): gives the in/out port back its "no direction" (DataInOut), if it still holds what this channel wrote.</summary>
        static unsafe void ReleaseRuntimeType(EntityManager em, Channel c)
        {
            var p = c.rtPort; byte v = c.rtVal; c.rtPort = Entity.Null; c.rtVal = 255;
            if (Ecs.IsNull(p) || v == 255) return;
            try
            {
                if (!Ecs.HasC(em, p, Ecs.tiPort)) return;
                var r = (byte*)Ecs.RO(em, p, Ecs.tiPort); if (r == null || r[21] != v) return;
                // review cabos_sinais-1: same rule as Radio.Resolve — back to DataInOut only when the game itself would give no direction (the
                // port's own cable ends in nothing or on an untyped port). Otherwise the game may have resolved this same value on its own
                // (cable change), and nothing would make it resolve again until the next cable change.
                if (Ecs.RawPortPub(em, p, out _, out _, out _, out var persist) && !Ecs.IsNull(persist))
                {
                    var end = Ecs.IsCable(em, persist) ? Ecs.WalkCable(em, persist, p, out _) : persist;
                    if (!Ecs.IsNull(end) && !(Ecs.Port(em, end, out byte te, out _, out _) && te == JointSystem.PortUntyped)) return;
                }
                var w = (byte*)em.GetComponentDataRawRW(p, Ecs.tiPort); if (w != null) w[21] = 3;
            }
            catch (Exception) { }
        }
        static unsafe bool Write(EntityManager em, Entity input, Entity output)
        {
            if (!Ecs.HasC(em, input, Ecs.tiPort)) return false;
            // 0.31.15: look first with a read-only pointer — asking for a writable one every tick made the game wait for its own jobs each time
            var r = (byte*)Ecs.RO(em, input, Ecs.tiPort); if (r == null) return false;
            var cur = (Entity*)(r + 12);
            if (cur->Index == output.Index && cur->Version == output.Version) return false;
            var b = (byte*)em.GetComponentDataRawRW(input, Ecs.tiPort); if (b == null) return false;
            *(Entity*)(b + 12) = output; return true;
        }

        // ================================================================ power (0.21.4, unchanged): ghosts in the circuits
        // The game builds one circuit per cable network INSIDE the part (ElectricCircuitRoot + ElectricCircuitPort /
        // ElectricCircuitComponent buffers) and rebuilds those roots all the time. SpaceshipElectricUpdateCircuits (the ONLY one that reads the
        // component buffer, only through SCElectricPower) adds up the components' _generatePerSec (+28) and _consumptionPerSec (+32), drains the batteries
        // (_runtimePower +0) by the deficit and copies (total, real, generation) into each component's _circuitInfo (+8). Writing into the GAME's
        // blocks fed back (plasma generator: 119 thousand P/s) — that's why each side gets a GHOST: our own entity with only SCElectricPower,
        // added to the circuit's component buffer (it fits in the capacity; redone when the game rebuilds the root).
        class Bridge { public Entity ghostJ, ghostA, rootJ, rootA; public float maxCable = 150f; public float q; public int direction; public float tLog = 99f; public int bufferWarn; public bool loop; public int missJ, missA; public float waitJ, waitA; }
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
        /// <summary>Smallest _maxPower (+12) among the cells of the cable leaving the port. 0.31.26 (item 5): up to a burnt-through cell
        /// (_burnDamage01 +52 >= 1), where the game cuts the cable [recon/Spaceship__part_a.cs:496-497].</summary>
        static unsafe float MaxCable(EntityManager em, Entity port)
        {
            float m = float.MaxValue;
            try
            {
                if (!Ecs.RawPortPub(em, port, out _, out _, out _, out var persist)) return 150f;
                var prev = port; var cur = persist; var seen = new HashSet<long>();
                for (int i = 0; i < 2048 && Ecs.IsCable(em, cur) && seen.Add(Ecs.K(cur)); i++)
                {
                    var b = (byte*)Ecs.RO(em, cur, Ecs.tiCable); if (b == null) break;
                    float mp = *(float*)(b + 12); if (mp > 0f && mp < m) m = mp;
                    if (*(float*)(b + 52) >= 1f) break;
                    var fwd = *(Entity*)(b + 0x10); var bwd = *(Entity*)(b + 0x18);
                    var nxt = (fwd.Index == prev.Index && fwd.Version == prev.Version) ? bwd : fwd; prev = cur; cur = nxt;
                    if (Ecs.IsNull(cur)) break;
                }
            }
            catch (Exception) { }
            return m == float.MaxValue ? 150f : m;
        }
        /// <summary>Root of the circuit that lists the power port at the other end of the cable (cached; if the game rebuilt the roots, search
        /// again). 0.31.26 (item 34): only that port is looked up. The pass-through port itself was looked up first, which never found
        /// anything: only ports with _runtimeType == Power go into ElectricCircuitPort, and a pass-through port is Unset (5) for good
        /// [recon/Spaceship__part_a.cs:427-429, 492-493; recon/Spaceship__part_b.cs:207].</summary>
        // 0.31.15: the game never edits a circuit root — it destroys them all and builds new ones (SpaceshipElectricCreateCircuits), so a
        // root that is still alive still lists the same ports: no need to read its list every time. When one is gone, the port -> root table
        // is built once for the whole pass (it was every root's port list read again for EACH channel that missed).
        static Dictionary<long, Entity> rootIndex;
        static Entity RootOfPort(EntityManager em, Entity source, Entity cache)
        {
            if (Ecs.Alive(em, cache)) return cache;
            if (rootIndex == null)
            {
                rootIndex = new Dictionary<long, Entity>();
                foreach (var r in Ecs.QueryList(qRoot)) foreach (var pe in Ecs.BufferEntities(em, r, tiBufPort)) rootIndex[Ecs.K(pe)] = r;
            }
            if (!Ecs.IsNull(source) && rootIndex.TryGetValue(Ecs.K(source), out var r2)) return r2;
            return Entity.Null;
        }
        static unsafe Entity Ghost(EntityManager em, Entity current)
        {
            if (Ecs.Alive(em, current)) return current;   // ours: alive = still has its SCElectricPower
            var kinds = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(1); kinds[0] = ComponentType.ReadWrite(Ecs.tiPower);
            var e = Ecs.Create(em, kinds);
            var b = (byte*)em.GetComponentDataRawRW(e, Ecs.tiPower); if (b != null) for (int i = 0; i < 44; i++) b[i] = 0;
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
            if (len >= cap)
            {
                // 0.31.6: the list is full even for the ONE ghost of this circuit: make it bigger the way the game itself does (Grow)
                if (!GrowList || !Grow(h, len, cap)) { fullAt[Ecs.K(root)] = len + "/" + cap; return false; }   // warned by BuildEdges
                ptr = *(byte**)h; len = *(int*)(h + 8); cap = *(int*)(h + 12); data = (Entity*)(ptr != null ? ptr : h + 16);
                if (len < 0 || len > 4096 || len >= cap) { fullAt[Ecs.K(root)] = len + "/" + cap; return false; }
            }
            data[len] = ghostE; *(int*)(h + 8) = len + 1; return true;
        }
        /// <summary>0.31.6 ([Slip Ring] grow_circuit_list): a circuit whose ElectricCircuitComponent list is full even before the bridge's one entity.
        /// Unity's own BufferHeader.EnsureCapacity — the same function DynamicBuffer.Add calls when a list is full: it allocates a bigger block
        /// (Persistent), copies the list, frees the old block, and Unity frees the new one when the buffer goes away. Called right after
        /// GetComponentDataRawRW, which waits for every job using this type, so nothing is reading the old block when it is freed.
        /// Element = ElectricCircuitComponent { Entity } = 8 bytes. If the call fails once, it is not tried again this session.</summary>
        static bool growFailed; static int grownLogs;
        static unsafe bool Grow(byte* h, int len, int cap)
        {
            if (growFailed) return false;
            try
            {
                BufferHeader.EnsureCapacity((BufferHeader*)h, len + 1, 8, 8, BufferHeader.TrashMode.RetainOldData, false, 0);
                int now = *(int*)(h + 12);
                if (now <= len) { growFailed = true; ModLog.Warn("power bridge: making room in a full circuit list didn't work (" + len + "/" + now + ") — turned off for this session"); return false; }
                if (grownLogs++ < 5) ModLog.Info("power bridge: a circuit's component list was full (" + len + "/" + cap + ") — made room for the bridge (the list now holds " + now + ")" + (grownLogs == 5 ? " (not logged again)" : ""));
                return true;
            }
            catch (Exception ex) { growFailed = true; ModLog.Warn("power bridge: making room in a full circuit list failed — turned off for this session: " + ex.Message); return false; }
        }
        // (0.31.16 — a reserve of stored power on the bridge's entity was tried on paper and left out: the moment that matters is the one in
        // which that entity is NOT in the circuit, so what it holds is not there either; and counting what the game "took" from it rests on
        // how the game treats stored power on an entity that is not a battery, which could not be checked — a wrong guess drains the ship.
        // The reserve that works is the game's own: a battery on the fed side.)
        static unsafe void WriteGhost(EntityManager em, Entity ghostE, float generates, float consumes)
        {
            if (!Ecs.Alive(em, ghostE)) return;
            // 0.31.15: written only when it changes (a writable pointer makes the game wait for its jobs)
            var r = (byte*)Ecs.RO(em, ghostE, Ecs.tiPower); if (r == null) return;
            if (*(float*)(r + 28) == generates && *(float*)(r + 32) == consumes) return;
            var b = (byte*)em.GetComponentDataRawRW(ghostE, Ecs.tiPower); if (b == null) return;
            *(float*)(b + 28) = generates; *(float*)(b + 32) = consumes;
        }
        /// <summary>Side J = joint, side A = ring. fJ / fA = power ports at the other ends of the cables. This only prepares (roots and
        /// ghosts in the circuit); how much goes through comes from Solve, which looks at all the bridges together.</summary>
        static void Power(EntityManager em, Link l, Channel c, Entity fJ, Entity fA, float dt)
        {
            if (!PrepareCircuits(em)) return;
            var pt = c.bridge;
            if (pt == null) { pt = c.bridge = new Bridge { maxCable = Math.Min(MaxCable(em, c.pj), MaxCable(em, c.pa)) }; ModLog.Info("power bridge: " + l.name + " #" + c.i + " — cables rated " + pt.maxCable.ToString("F0") + " P/s"); }
            c.fJ = fJ; c.fA = fA;
            pt.rootJ = RootCached(em, c.pj, fJ, pt.rootJ, ref pt.missJ, ref pt.waitJ, dt); pt.rootA = RootCached(em, c.pa, fA, pt.rootA, ref pt.missA, ref pt.waitA, dt);
            bool hJ = !Ecs.IsNull(pt.rootJ), hA = !Ecs.IsNull(pt.rootA);
            if (hJ && hA && pt.rootJ.Index == pt.rootA.Index) { WriteGhost(em, pt.ghostJ, 0f, 0f); WriteGhost(em, pt.ghostA, 0f, 0f); return; }
            // 0.31.5 (asas do Andre: nave -> pistao -> slip -> bearing -> slip -> ...): o cabo de energia que vai de uma junta ate a porta de
            // passagem da proxima nao tem circuito do jogo (as duas pontas sao portas sem tipo), entao esse lado fica sem raiz. Antes a ponte
            // inteira caia aqui e a energia parava na primeira junta; agora o canal entra na lista com o lado que tem circuito, e o Solve segue
            // a corrente de junta em junta ate o proximo circuito de verdade.
            if (hJ) pt.ghostJ = Ghost(em, pt.ghostJ); else WriteGhost(em, pt.ghostJ, 0f, 0f);
            if (hA) pt.ghostA = Ghost(em, pt.ghostA); else WriteGhost(em, pt.ghostA, 0f, 0f);
            // 0.31.6 (carro do Andre, 31/10: dois foguetes em dois mini pistoes saindo do mesmo circuito, so um funcionava por vez — "the
            // circuit's component buffer is full (16/16)"): a lista de componentes do circuito tem tamanho fixo e cada ponte punha o SEU fantasma
            // nela; com 15 pecas do jogo so cabia um. Agora cada circuito tem UM fantasma, o primeiro que estiver na lista dele (o "carregador"),
            // e todas as pontes daquele circuito somam nele o que entra e o que sai. Os fantasmas das outras pontes ficam em zero.
            if (hJ) Carry(em, pt.rootJ, pt.ghostJ, pt);
            if (hA) Carry(em, pt.rootA, pt.ghostA, pt);
            frameBridges.Add((l, c));
        }
        /// <summary>0.31.6: circuit (root) -> the ghost that carries every bridge of that circuit this frame.</summary>
        static readonly Dictionary<long, Entity> carrier = new Dictionary<long, Entity>();
        static void Carry(EntityManager em, Entity root, Entity ghostE, Bridge pt)
        {
            long k = Ecs.K(root);
            if (carrier.ContainsKey(k)) return;
            if (InCircuit(em, root, ghostE, pt)) carrier[k] = ghostE;
        }
        /// <summary>0.31.5: RootOfPort, but a side that keeps having no circuit (the cable between two joints) is searched only twice a
        /// second instead of every frame (the search goes through every circuit of the ship). One miss alone doesn't wait: that is the game
        /// rebuilding the roots.</summary>
        static Entity RootCached(EntityManager em, Entity port, Entity source, Entity cache, ref int miss, ref float wait, float dt)
        {
            if (miss >= 3 && wait > 0f) { wait -= dt; return Entity.Null; }
            var r = RootOfPort(em, source, cache);
            if (Ecs.IsNull(r)) { miss++; wait = miss >= 3 ? 0.5f : 0f; } else { miss = 0; wait = 0f; }
            return r;
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
        class Node { public Entity root; public long k; public float cons, gen, stored, dem; public int parent = -1, parentEdge = -1, size = 1; public List<int> edges = new List<int>(), parallels = new List<int>(); public List<Entity> comps = new List<Entity>(); public bool isSource;
                     public int shortN; public Entity shortEx; public bool treePower = true;   // 0.31.16: components asking for power and not getting it (Watch)
                     public float space, chg, want, fIn, fChg; }   // 0.31.26 (items 4, 16): room to charge (P), charge asked by the subtree (P/s), flows (Distribute)
        /// <summary>Consumption and generation (P/s) and charge (P) of the circuit's components, without the bridges' ghosts.</summary>
        static unsafe Node Accumulate(EntityManager em, Entity root, HashSet<long> ghosts)
        {
            var n = new Node { root = root, k = Ecs.K(root) };
            foreach (var c in Ecs.BufferEntities(em, root, tiBufComp))
            {
                if (ghosts.Contains(Ecs.K(c)) || !Ecs.HasC(em, c, Ecs.tiPower)) continue;
                var b = (byte*)Ecs.RO(em, c, Ecs.tiPower); if (b == null) continue;
                double run = *(double*)b; float cap = *(float*)(b + 24);   // _runtimePower +0, _chargeablePower +24 [types_layout.txt:14962-14971]
                n.stored += (float)run; n.gen += *(float*)(b + 28); n.cons += *(float*)(b + 32);
                if (cap > run) n.space += (float)(cap - run);
                n.comps.Add(c);
            }
            return n;
        }
        /// <summary>How much the bridge carries to cover the demand d (P/s) of the circuits past it. With a margin: generating EXACTLY the consumption
        /// leaves the circuit on the edge of "insufficient power" (float sums); negative = the far side has spare power and sends it back.</summary>
        static float Flow(float d) => d > 0f ? d * 1.002f + 0.002f : -Math.Max(0f, -d * 0.998f - 0.002f);

        /// <summary>0.31.5: one edge of the network = two real circuits and the two ghosts that carry the power between them. A plain bridge is one
        /// channel (joint side X, ring side Y); a CHAIN is several joints in a row whose cables in between have no circuit of the game (pass-through
        /// port to pass-through port): it runs from the channel at one end (its side with a circuit) to the channel at the other end, and only those
        /// two ghosts are used.</summary>
        class Edge { public Entity rX, rY, gX, gY; public Bridge pt; public string name, sideX, sideY, ax, ay; public float maxCable; public int joints = 1; }
        static readonly List<Edge> frameEdges = new List<Edge>();
        static readonly HashSet<string> chainLog = new HashSet<string>(), chainBrokenLog = new HashSet<string>();
        static void BuildEdges(EntityManager em)
        {
            frameEdges.Clear();
            int n = frameBridges.Count;
            var at = new Dictionary<long, (int e, int side)>();   // port (joint or ring side of a channel) -> channel, side 0 joint / 1 ring
            for (int e = 0; e < n; e++) { var c = frameBridges[e].c; at[Ecs.K(c.pj)] = (e, 0); at[Ecs.K(c.pa)] = (e, 1); }
            Entity Root(int e, int s) => s == 0 ? frameBridges[e].c.bridge.rootJ : frameBridges[e].c.bridge.rootA;
            Entity Far(int e, int s) => s == 0 ? frameBridges[e].c.fJ : frameBridges[e].c.fA;
            string Nm(int e) => frameBridges[e].l.name + " #" + frameBridges[e].c.i;
            string Sd(int s) => s == 0 ? "joint" : "ring";
            var pairs = new HashSet<long>();
            for (int e = 0; e < n; e++)
            {
                var pt = frameBridges[e].c.bridge;
                bool hJ = !Ecs.IsNull(pt.rootJ), hA = !Ecs.IsNull(pt.rootA);
                if (hJ && hA) { frameEdges.Add(new Edge { rX = pt.rootJ, rY = pt.rootA, gX = Carrier(pt.rootJ), gY = Carrier(pt.rootA), pt = pt, name = Nm(e), sideX = "joint side of " + Nm(e), sideY = "ring side of " + Nm(e), ax = "joint", ay = "ring", maxCable = pt.maxCable }); continue; }
                if (!hJ && !hA) continue;   // middle of a chain: reached from one of its ends
                int s0 = hJ ? 0 : 1, cur = e, outSide = 1 - s0, end = -1, endSide = -1, joints = 1;
                var path = new System.Text.StringBuilder(Nm(e)); float mc = pt.maxCable; var visited = new HashSet<int> { e }; string why = "";
                for (int hop = 0; hop < 12; hop++)
                {
                    var far = Far(cur, outSide);
                    if (Ecs.IsNull(far)) { why = "the cable on the " + Sd(outSide) + " side of " + Nm(cur) + " ends in nothing"; break; }
                    if (!at.TryGetValue(Ecs.K(far), out var nx)) { why = "the cable on the " + Sd(outSide) + " side of " + Nm(cur) + " goes to port " + far.Index + ", which is neither a circuit nor a powered slip ring channel"; break; }
                    if (!visited.Add(nx.e)) { why = "the chain loops back"; break; }
                    joints++; path.Append(" -> ").Append(Nm(nx.e)); mc = Math.Min(mc, frameBridges[nx.e].c.bridge.maxCable);
                    int o = 1 - nx.side;
                    if (!Ecs.IsNull(Root(nx.e, nx.side))) { why = "unexpected circuit in the middle"; break; }
                    if (!Ecs.IsNull(Root(nx.e, o))) { end = nx.e; endSide = o; break; }
                    cur = nx.e; outSide = o;
                }
                string name = path.ToString();
                if (end < 0) { if ((s0 == 0 ? pt.missA : pt.missJ) >= 3 && chainBrokenLog.Add(name + why)) ModLog.Warn("power bridge: " + name + " — one side has no circuit and the chain of joints doesn't reach one (" + (why == "" ? "more than 12 joints" : why) + "): nothing passes"); continue; }
                long a = Math.Min(e, end), b = Math.Max(e, end); if (!pairs.Add((a << 20) | b)) continue;   // the chain is found from both ends
                var rX = Root(e, s0); var rY = Root(end, endSide);
                if (rX.Index == rY.Index) continue;
                if (chainLog.Add(name)) ModLog.Info("power bridge: chain of " + joints + " joints " + name + " — the cables between the joints have no circuit of their own: bridging from the " + Sd(s0) + " side of " + Nm(e) + " to the " + Sd(endSide) + " side of " + Nm(end));
                frameEdges.Add(new Edge { rX = rX, rY = rY, gX = Carrier(rX), gY = Carrier(rY), pt = pt, name = name, sideX = Sd(s0) + " side of " + Nm(e), sideY = Sd(endSide) + " side of " + Nm(end), ax = "this end", ay = "far end", maxCable = mc, joints = joints });
            }
            // 0.31.6: a circuit with no carrier = its component list was full even for one ghost: the bridges touching it can't work
            for (int i = frameEdges.Count - 1; i >= 0; i--)
            {
                var ed = frameEdges[i]; bool okX = !Ecs.IsNull(ed.gX), okY = !Ecs.IsNull(ed.gY);
                if (okX && okY) continue;
                string side = !okX ? ed.sideX : ed.sideY; fullAt.TryGetValue(Ecs.K(!okX ? ed.rX : ed.rY), out var lc);
                if (fullLog.Add(ed.name + side)) ModLog.Warn("power bridge: " + ed.name + " — the circuit on the " + side + " has no room left in the game's component list (" + (lc ?? "?") + ") even for one bridge entity: nothing passes");
                frameEdges.RemoveAt(i);
            }
        }
        static Entity Carrier(Entity root) => carrier.TryGetValue(Ecs.K(root), out var g) ? g : Entity.Null;
        static readonly Dictionary<long, string> fullAt = new Dictionary<long, string>();
        static readonly HashSet<string> fullLog = new HashSet<string>();
        static void ZeroUnused(EntityManager em)
        {
            var used = new HashSet<long>(); foreach (var ed in frameEdges) { used.Add(Ecs.K(ed.gX)); used.Add(Ecs.K(ed.gY)); }
            foreach (var (l, c) in frameBridges) { var pt = c.bridge; if (pt == null) continue; if (!used.Contains(Ecs.K(pt.ghostJ))) WriteGhost(em, pt.ghostJ, 0f, 0f); if (!used.Contains(Ecs.K(pt.ghostA))) WriteGhost(em, pt.ghostA, 0f, 0f); }
        }
        /// <summary>0.31.26 (items 4 and 16): how much each bridge of one tree carries this tick. The game's UpdateCircuits never limits anybody's
        /// consumption: a circuit short of power just becomes Deficiency, and stored power covers a deficit, while a SURPLUS of generation is
        /// shared in equal parts among the members with room to charge and the rest is lost [recon/Spaceship__part_a.cs:622-675, lines
        /// 3650-3728]. Before, the fed side's ghost generated its whole demand as soon as the source had any power at all (`stored + gen >
        /// 0.01`): a source with an empty battery and 2 P/s of solar behind 100 P/s of thrusters kept the far side Supplied with ~98 P/s that
        /// did not exist. And only consumption crossed: a source's spare power never charged a battery past a joint, while a wing's spare
        /// solar went back to the source even with the source's batteries full (lost there, and the wing's own batteries barely charged).
        ///  1) bottom-up (as before): each circuit asks for its own consumption minus generation plus what the circuits past it ask (Flow),
        ///     and, separately, for the room its batteries (and those past it that ask for power) have to charge, in P/s (room * 60);
        ///  2) top-down, from the source: a circuit first covers its own consumption and what it owes upstream, from its generation, what
        ///     it receives and what its children with spare power can send; then hands to the children that ask for power at most what is
        ///     left PLUS its stored power this tick (stored * 60) — shared in equal parts, each up to what it asks (as the game shares a
        ///     deficit). A child that gets less than it asks becomes Deficiency on its side, as one circuit joined by a cable would;
        ///  3) then charging, only from GENERATION (its own spare, the charge part it received, the children's spare — never stored power,
        ///     so no battery is drained into another): shared in equal parts between its own batteries and the children asking for charge;
        ///  4) a child with spare power sends up only what this side uses — consumption and charging — and keeps the rest for its own
        ///     batteries; its own room is a claimant in step 3 like any member (review cabos_sinais-2), and that share never crosses.
        /// Nothing is created: each ghost pair generates on one side exactly what it consumes on the other. Only the amounts change.</summary>
        static void Distribute(List<Node> nodes, List<int> order, bool hasPower, int[] a, float[] fl)
        {
            for (int i = order.Count - 1; i >= 0; i--)
            {
                var u = nodes[order[i]];
                u.dem += u.cons - u.gen; u.chg += u.space * 60f;
                if (u.parent < 0) continue;
                var p = nodes[u.parent];
                u.want = hasPower ? Flow(u.dem) : 0f;
                p.dem += u.want;
                if (u.want >= 0f) p.chg += u.chg;   // a child with spare power: its room is a claimant in the parent's charge split (top-down), not added here
            }
            void Put(Node u, float f)
            {
                float piece = f / (1 + u.parallels.Count);
                fl[u.parentEdge] = a[u.parentEdge] == u.parent ? piece : -piece;
                foreach (int e in u.parallels) fl[e] = a[e] == u.parent ? piece : -piece;
            }
            foreach (int ui in order) { var u = nodes[ui]; u.fIn = 0f; u.fChg = 0f; if (u.parent >= 0) Put(u, 0f); }
            if (!hasPower) return;
            var kids = new Dictionary<int, List<int>>();
            foreach (int ui in order) { var u = nodes[ui]; if (u.parent < 0) continue; if (!kids.TryGetValue(u.parent, out var l)) kids[u.parent] = l = new List<int>(); l.Add(ui); }
            foreach (int ui in order)   // breadth-first from the source: a circuit is settled before its children
            {
                if (!kids.TryGetValue(ui, out var ks)) continue;
                var u = nodes[ui]; int m = ks.Count;
                float fIn = u.fIn, owe = Math.Max(0f, -fIn);
                float gin = u.gen + Math.Max(0f, fIn), out0 = u.cons + owe;
                var capD = new float[m]; var capP = new float[m]; var capC = new float[m + 1]; float woff = 0f;
                for (int j = 0; j < m; j++) { var c = nodes[ks[j]]; if (c.want < 0f) { capP[j] = -c.want; woff += capP[j]; } else { capD[j] = c.want; capC[j] = c.chg; } }
                capC[m] = u.space * 60f;   // its own batteries
                // consumption: what is left after its own use and what it owes upstream, with its stored power, for the children asking
                var g = Share(Math.Max(0f, gin + woff + u.stored * 60f - out0), capD);
                float used = out0; for (int j = 0; j < m; j++) used += g[j];
                float fromPcons = Clamp(used - gin, 0f, woff);   // spare power from the children is used before stored power, as generation is in one circuit
                // review cabos_sinais-2: a child with spare power is a charge claimant like any member (the game shares a surplus in equal parts
                // among ALL members with room [recon/Spaceship__part_a.cs:654-669]), up to the spare it still has after this side's consumption
                var tc = Share(fromPcons, capP);
                for (int j = 0; j < m; j++) { var c = nodes[ks[j]]; if (c.want < 0f) capC[j] = Math.Max(0f, Math.Min(c.chg, capP[j] - tc[j])); }
                // charging: generation only
                float cs = Math.Min(u.fChg + Math.Max(0f, u.gen + woff - u.cons - owe), Math.Max(0f, gin + woff - used));
                float claims = 0f; for (int j = 0; j <= m; j++) claims += capC[j];
                float chUsed = Math.Min(cs, claims), pLeft = woff - fromPcons;
                var x = Share(chUsed, capC);
                // what a spare child's own batteries get stays there (paid by its own spare, never crosses)
                float kept = 0f; var capT = new float[m];
                for (int j = 0; j < m; j++) { if (nodes[ks[j]].want < 0f) { kept += x[j]; capT[j] = Math.Max(0f, capP[j] - x[j]); } }
                float fromPchg = Clamp(chUsed - kept - Math.Max(0f, cs - pLeft), 0f, Math.Max(0f, pLeft - kept));   // its own spare first, then the children's
                var t = Share(fromPcons + fromPchg, capT);
                for (int j = 0; j < m; j++)
                {
                    var c = nodes[ks[j]];
                    float f = c.want < 0f ? -t[j] : g[j] + x[j];
                    c.fIn = f; c.fChg = c.want < 0f ? 0f : x[j];
                    Put(c, f);
                }
            }
        }
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        /// <summary>Splits `amount` in equal parts, each up to its cap (what one gets beyond its cap goes to the others).</summary>
        static float[] Share(float amount, float[] caps)
        {
            int n = caps.Length; var r = new float[n]; if (n == 0 || !(amount > 0f)) return r;
            var idx = new int[n]; for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (p, q) => caps[p].CompareTo(caps[q]));
            float left = amount; int k = n;
            foreach (int i in idx) { float give = Math.Max(0f, Math.Min(caps[i], left / k)); r[i] = give; left -= give; k--; }
            return r;
        }
        static void Solve(EntityManager em, float dt)
        {
            sourcesNow.Clear();
            BuildEdges(em); ZeroUnused(em);
            if (frameEdges.Count == 0) { sourcesBefore.Clear(); fast = null; return; }
            var trees = new List<(List<int> order, bool hasPower)>();
            var ghosts = new HashSet<long>();
            foreach (var l in links.Values) foreach (var c in l.channels) if (c.bridge != null) { ghosts.Add(Ecs.K(c.bridge.ghostJ)); ghosts.Add(Ecs.K(c.bridge.ghostA)); }
            int n = frameEdges.Count;
            var nodes = new List<Node>(); var idx = new Dictionary<long, int>();
            int NodeOf(Entity r) { long k = Ecs.K(r); if (!idx.TryGetValue(k, out int i)) { i = nodes.Count; idx[k] = i; nodes.Add(Accumulate(em, r, ghosts)); } return i; }
            var already = new int[n]; var na = new int[n]; var role = new int[n]; var fl = new float[n];   // role: -1 not seen, 1 tree, 2 parallel, 3 loop
            for (int e = 0; e < n; e++) { var ed = frameEdges[e]; already[e] = NodeOf(ed.rX); na[e] = NodeOf(ed.rY); nodes[already[e]].edges.Add(e); nodes[na[e]].edges.Add(e); role[e] = -1; }
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
                sourcesNow.Add(nodes[source].k); nodes[source].isSource = true;
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
                bool hasPower = bestPick > 0.01f; trees.Add((order, hasPower));
                for (int i = order.Count - 1; i >= 0; i--) { var u = nodes[order[i]]; if (u.parent >= 0) nodes[u.parent].size += u.size; }
                Distribute(nodes, order, hasPower, already, fl);   // 0.31.26 (items 4, 16); + = joint -> ring
                string side = "";
                foreach (int e in nodes[source].edges) { side = already[e] == source ? frameEdges[e].sideX : frameEdges[e].sideY; break; }
                string line = "\n  " + comp.Count + " circuit(s), " + (order.Count - 1) + " carrying bridge path(s)" + (loops > 0 ? ", " + loops + " bridge(s) closing a loop (idle)" : "") + (hasPower ? " — source: " + side : " — no stored or generated power anywhere");
                sig.Append(line); sb.Append(line).Append(hasPower ? " (stores " + nodes[source].stored.ToString("F0") + " P, generates " + nodes[source].gen.ToString("F1") + " P/s)" : "");
            }
            sourcesBefore.Clear(); foreach (var k in sourcesNow) sourcesBefore.Add(k);
            string net = sig.ToString();   // only the shape of the network (the battery charge changes every frame)
            if (net != netLog) { netLog = net; if (DetailedLog) ModLog.Info("power bridge network:" + sb); }
            // 0.31.6: every bridge of a circuit adds into that circuit's one carrier ghost
            var sum = new Dictionary<long, (Entity g, float gen, float cons)>();
            void Add(Entity g, float gen, float cons) { long k = Ecs.K(g); sum.TryGetValue(k, out var v); sum[k] = (g, v.gen + gen, v.cons + cons); }
            for (int e = 0; e < n; e++)
            {
                var ed = frameEdges[e]; var pt = ed.pt; float q = Math.Abs(fl[e]);
                int direction = fl[e] > 1e-4f ? 1 : fl[e] < -1e-4f ? -1 : pt.direction;
                if (direction == 1) { Add(ed.gX, 0f, q); Add(ed.gY, q, 0f); } else { Add(ed.gX, q, 0f); Add(ed.gY, 0f, q); }
                bool loop = role[e] == 3; pt.q = q; pt.tLog += dt;
                if (direction != pt.direction || loop != pt.loop || pt.tLog >= 10f)
                {
                    pt.tLog = 0f; pt.direction = direction; pt.loop = loop;
                    var nj = nodes[already[e]]; var nA = nodes[na[e]]; var after = nj.parent == na[e] ? nj : nA;
                    if (!DetailedLog) continue;
                    if (loop) { ModLog.Info("power bridge: " + ed.name + " — closes a loop with other slip rings: idle, the power goes the other way"); continue; }
                    ModLog.Info("power bridge: " + ed.name + " — " + (direction == 1 ? ed.ax + " -> " + ed.ay : direction == -1 ? ed.ay + " -> " + ed.ax : "idle") + ": " + ed.ax + " side stores " + nj.stored.ToString("F0") + " P (gen " + nj.gen.ToString("F1") + ", uses " + nj.cons.ToString("F1") + " P/s), " + ed.ay + " side stores " + nA.stored.ToString("F0") + " P (gen " + nA.gen.ToString("F1") + ", uses " + nA.cons.ToString("F1") + " P/s) — bridging " + q.ToString("F1") + " P/s" + (after.size > 1 ? " (for " + after.size + " circuits past it)" : "") + (q > ed.maxCable ? " (ABOVE the cables' " + ed.maxCable.ToString("F0") + " P/s — they will burn)" : ""));
                }
            }
            foreach (var v in sum.Values) WriteGhost(em, v.g, v.gen, v.cons);
            fast = new FastNet { nodes = nodes, a = already, b = na, trees = trees, edges = new List<Edge>(frameEdges) };
        }
        /// <summary>0.31.15: the network as the last full pass left it — which circuits, which bridges, who feeds whom — for the ticks.</summary>
        class FastNet { public List<Node> nodes; public int[] a, b; public List<(List<int> order, bool hasPower)> trees; public List<Edge> edges; }
        static FastNet fast;
        static readonly Dictionary<long, (Entity g, float gen, float cons)> fastSum = new Dictionary<long, (Entity, float, float)>();
        /// <summary>Every tick: how much each bridge carries, from what the fed circuits consume and generate NOW (the same sums as Solve, over
        /// the same tree). False = a circuit of the network is gone (the game rebuilt it): the full pass has to run.</summary>
        static unsafe bool FastFlows(EntityManager em)
        {
            var f = fast; if (f == null) return true;
            foreach (var n in f.nodes) if (!Ecs.Alive(em, n.root)) { fast = null; return false; }
            foreach (var n in f.nodes)
            {
                n.dem = 0f; n.chg = 0f;
                // 0.31.26 (item 4): every circuit, the source too, is read live — stored power included — because what a circuit can hand on
                // in this tick is its stored power plus its generation minus its consumption (Distribute). (The source's totals used to be
                // only for choosing the source, kept from the full pass.)
                float gen = 0f, cons = 0f, space = 0f; double stored = 0; int sh = 0; Entity ex = default;
                foreach (var c in n.comps)
                {
                    if (!Ecs.HasC(em, c, Ecs.tiPower)) continue;
                    var b = (byte*)Ecs.RO(em, c, Ecs.tiPower); if (b == null) continue;
                    float cc = *(float*)(b + 32); double run = *(double*)b; float cap = *(float*)(b + 24);
                    gen += *(float*)(b + 28); cons += cc; stored += run; if (cap > run) space += (float)(cap - run);
                    if (!n.isSource && cc > 0.01f && b[36] != 2) { if (sh++ == 0) ex = c; }   // _suppliedState (+36): 2 = Supplied — as the game left it last tick
                }
                n.gen = gen; n.cons = cons; n.stored = (float)stored; n.space = space; n.shortN = sh; n.shortEx = ex;
            }
            flowsTick = tickNo;
            int ne = f.edges.Count; var fl = new float[ne];
            foreach (var (order, hasPower) in f.trees)
            {
                foreach (int ui in order) f.nodes[ui].treePower = hasPower;
                Distribute(f.nodes, order, hasPower, f.a, fl);   // 0.31.26 (items 4, 16)
            }
            fastSum.Clear();
            for (int e = 0; e < ne; e++)
            {
                var ed = f.edges[e]; float q = Math.Abs(fl[e]);
                int direction = fl[e] > 1e-4f ? 1 : fl[e] < -1e-4f ? -1 : ed.pt.direction;
                long kx = Ecs.K(ed.gX), ky = Ecs.K(ed.gY);
                fastSum.TryGetValue(kx, out var vx); fastSum.TryGetValue(ky, out var vy);
                if (direction == 1) { fastSum[kx] = (ed.gX, vx.gen, vx.cons + q); fastSum[ky] = (ed.gY, vy.gen + q, vy.cons); }
                else { fastSum[kx] = (ed.gX, vx.gen + q, vx.cons); fastSum[ky] = (ed.gY, vy.gen, vy.cons + q); }
                ed.pt.q = q;
            }
            foreach (var v in fastSum.Values) WriteGhost(em, v.g, v.gen, v.cons);
            return true;
        }
        // ================================================================ 0.31.16: power watch (log only)
        // (Andre, 12/11: the ship "loses thrust and drops its nose" when the game stutters; the trace, one sample every 2 s, showed every
        // thruster pushing the whole time — a cut shorter than 2 s cannot be seen there.) Every game tick: every circuit fed by a bridge,
        // and every electric thruster that is not behind one, is checked for a component that asks for power and is not "Supplied"
        // (SCElectricPower._suppliedState, as the game left it on the previous tick). One log line per episode, when it ends (or after 2 s
        // if it goes on): what, for how many ticks, and what the mod was doing in the tick it began. [Debug] power_watch.
        class Loss { public string who; public Entity example; public int first, last, ticks, comps; public bool rebuilt, relinked, noSource, behindBridge, said; }
        static readonly Dictionary<long, Loss> losses = new Dictionary<long, Loss>();
        static readonly HashSet<long> fedComps = new HashSet<long>(); static FastNet fedOf;
        static int flowsTick = -1, lossLines; static float lossWindow, firstLinkAt = -1f;
        static unsafe void Watch(EntityManager em)
        {
            float now = Ecs.Now;
            if (firstLinkAt < 0f) firstLinkAt = now;
            if (now - firstLinkAt < 5f) return;   // launch: everything is dark until the joints are born
            var f = fast;
            if (f != null)
            {
                if (flowsTick != tickNo)   // the full pass ran in this tick instead of the quick one: read the supply states here
                    foreach (var n in f.nodes)
                    {
                        n.shortN = 0; if (n.isSource) continue;
                        foreach (var c in n.comps)
                        {
                            if (!Ecs.HasC(em, c, Ecs.tiPower)) continue;
                            var b = (byte*)Ecs.RO(em, c, Ecs.tiPower); if (b == null) continue;
                            if (*(float*)(b + 32) > 0.01f && b[36] != 2) { if (n.shortN++ == 0) n.shortEx = c; }
                        }
                    }
                if (!ReferenceEquals(fedOf, f)) { fedOf = f; fedComps.Clear(); foreach (var n in f.nodes) if (!n.isSource) foreach (var c in n.comps) fedComps.Add(Ecs.K(c)); }
                foreach (var n in f.nodes)
                {
                    if (n.isSource || n.shortN == 0 || n.comps.Count == 0) continue;
                    string who = n.parentEdge >= 0 && n.parentEdge < f.edges.Count ? f.edges[n.parentEdge].name : "a bridged circuit";
                    Mark(Ecs.K(n.comps[0]), who, n.shortEx, n.shortN, !n.treePower, true);
                }
            }
            else if (fedOf != null) { fedOf = null; fedComps.Clear(); }
            if (Thrusters.Ready)
                foreach (var e in Thrusters.Entities)
                {
                    long k = Ecs.K(e); if (fedComps.Contains(k) || !Ecs.HasC(em, e, Ecs.tiPower)) continue;
                    var b = (byte*)Ecs.RO(em, e, Ecs.tiPower); if (b == null) continue;
                    if (*(float*)(b + 32) > 0.01f && b[36] != 2) Mark(k, null, e, 1, false, false);
                }
            if (losses.Count == 0) return;
            if (now - lossWindow > 10f) { lossWindow = now; lossLines = 0; }
            List<long> done = null;
            foreach (var kv in losses)
            {
                var l = kv.Value; bool ended = l.last < tickNo;
                if (!ended && (l.ticks != 120 || l.said)) continue;
                if (lossLines++ < 12)
                {
                    string ex = null; try { ex = Ecs.Alive(em, l.example) ? BlockIndex.NameOfHash(Ecs.Prefab(em, l.example)) : null; } catch (Exception) { }
                    string what = l.behindBridge ? "behind " + l.who + ": " + l.comps + " component(s) asking for power and not supplied" + (ex != null ? " (" + ex + " " + l.example.Index + ")" : "")
                                                 : (ex ?? "thruster") + " " + l.example.Index + " (not behind a slip ring) asking for power and not supplied";
                    string cause = l.noSource ? "the side that feeds it has no stored or generated power" : l.relinked ? "began in the tick a joint changed and the rings were linked again" : l.rebuilt ? "began in the tick the game rebuilt a circuit" : !l.behindBridge ? "its own circuit is short (nothing the mod bridges)" : "the bridge was up and nothing changed in that tick";
                    // 0.31.27 (Andre's log of 21/11: four thrusters "not behind a slip ring ... for 2 s and still going" and then 2408 ticks short in
                    // every performance line): a thruster outside the bridge network is in a circuit the mod does not feed, so the line now says
                    // WHICH circuit and WHY, from the game's own numbers of that circuit (read only when the line is written)
                    if (!l.behindBridge) { string why = null; try { why = CircuitWhy(em, l.example); } catch (Exception) { } if (why != null) cause += "; " + why; }
                    ModLog.Info("power watch: " + what + " — " + (ended ? "for " + l.ticks + " tick(s) (" + (l.ticks * 1000f / 60f).ToString("F0") + " ms)" : "for 2 s and still going") + "; " + cause);
                }
                if (ended) (done ?? (done = new List<long>())).Add(kv.Key); else l.said = true;
            }
            if (done != null) foreach (var k in done) losses.Remove(k);
        }
        /// <summary>Episodes still going (the performance line).</summary>
        public static int LossesOpen => losses.Count;
        /// <summary>0.31.27: for a component outside the bridge network that asks for power and is not supplied — its part, its circuit and the
        /// reason, from what the game wrote. SpaceshipElectricUpdateCircuits adds up every member's _generatePerSec (+28), _consumptionPerSec
        /// (+32) and _runtimePower (+0), skips members with _isElectricityInterrupted (+37) and gives all of them one state: NonSupplied (0) when
        /// nothing in the circuit stores, generates or uses power, Deficiency (1) when the stored power cannot cover this tick's deficit,
        /// Supplied (2) otherwise [recon/Spaceship__part_a.cs:3610-3728]. A component in no circuit keeps whatever it last had.</summary>
        static unsafe string CircuitWhy(EntityManager em, Entity c)
        {
            if (!Ecs.HasC(em, c, Ecs.tiPower)) return null;
            var b = (byte*)Ecs.RO(em, c, Ecs.tiPower);
            float use = *(float*)(b + 32); byte state = b[36]; bool cut = b[37] != 0;
            var part = Ecs.Parent(em, c);
            string where = "on part " + (Ecs.IsNull(part) ? "?" : part.Index.ToString());
            if (cut) return where + ": the game marked it as cut off (_isElectricityInterrupted: an electricity interruption reached it), it takes no part in its circuit";
            if (!PrepareCircuits(em)) return where;
            Entity root = Entity.Null;
            foreach (var r in Ecs.QueryList(qRoot)) { foreach (var m in Ecs.BufferEntities(em, r, tiBufComp)) if (m.Index == c.Index && m.Version == c.Version) { root = r; break; } if (!Ecs.IsNull(root)) break; }
            if (Ecs.IsNull(root)) return where + ": it is in NO circuit of the game (no power cable reaches it — a cable cut or burnt, or the piece that linked it came off); it asks " + use.ToString("F1") + " P/s";
            int n = 0, batteries = 0, gens = 0, bridged = 0; double stored = 0, cap = 0; float gen = 0f, cons = 0f;
            foreach (var m in Ecs.BufferEntities(em, root, tiBufComp))
            {
                if (!Ecs.HasC(em, m, Ecs.tiPower)) continue;
                var p = (byte*)Ecs.RO(em, m, Ecs.tiPower); n++;
                if (fedComps.Contains(Ecs.K(m))) bridged++;
                if (p[37] != 0) continue;   // interrupted members are left out by the game too
                double run = *(double*)p; float ch = *(float*)(p + 24), g = *(float*)(p + 28);
                stored += run; cap += ch; gen += g; cons += *(float*)(p + 32);
                if (ch > 0f) batteries++; if (g > 0f) gens++;
            }
            string sums = "its circuit (root " + root.Index + ", " + n + " component(s)) stores " + stored.ToString("F0") + " of " + cap.ToString("F0") + " P in " + batteries + " battery(ies), generates " + gen.ToString("F1") + " P/s from " + gens + " source(s), uses " + cons.ToString("F1") + " P/s";
            string why;
            if (state == 0) why = "the game sees nothing in that circuit that stores, generates or uses power";
            else if (stored < 1.0 && gen < cons) why = "a REAL shortage: nothing stored and less generated than used" + (batteries == 0 ? " (no battery in that circuit)" : " (its batteries are empty)") + (gens == 0 ? " and no generator" : "");
            else if (state == 1) why = "the stored power did not cover this tick's deficit";
            else why = "it was short when the game last updated the circuit and is supplied now";
            return where + ": " + sums + " — " + why + (bridged > 0 ? "" : "; no slip ring bridge reaches that circuit, so the mod's power sharing is not involved");
        }
        static void Mark(long key, string who, Entity example, int comps, bool noSource, bool behindBridge)
        {
            if (!losses.TryGetValue(key, out var l))
            {
                if (losses.Count > 512) return;
                l = new Loss { who = who, example = example, first = tickNo, comps = comps, rebuilt = tickRebuilt || prevRebuilt, relinked = tickRelinked || prevRelinked, noSource = noSource, behindBridge = behindBridge };
                losses[key] = l; LossEvents++;
            }
            l.last = tickNo; l.ticks++; LossTicks++; if (comps > l.comps) l.comps = comps;
        }

        static void UndoBridge(EntityManager em, Channel c)
        {
            var pt = c.bridge; if (pt == null) return;
            fast = null;   // 0.31.15: the ticks wait for the next full pass
            try { if (Ecs.Alive(em, pt.ghostJ)) Ecs.Destroy(em, pt.ghostJ); } catch (Exception) { }
            try { if (Ecs.Alive(em, pt.ghostA)) Ecs.Destroy(em, pt.ghostA); } catch (Exception) { }
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
            // 0.31.36: a ring port of a Bearing with Slip Ring — its plug is drawn turned (RingVisual), so it is not at the port's position
            var turned = RingVisual.PlugOf(port); if (!Ecs.IsNull(turned) && Ecs.HasC(em, turned, tiRendData)) return turned;
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
            var seats = new List<(Entity e, string name, RigidTransform g)>();
            var recs = new List<(Entity e, string name, RigidTransform g)>(); var decs = new List<RigidTransform>();   // 0.31.14
            foreach (var b in blocks)
            {
                ulong h; try { h = Ecs.Prefab(em, b); } catch (Exception) { continue; }
                if (h == 0 || !JointSystem.KindFromHash(h, out var tp, out float stroke, out var name) || name == null) continue;
                if (!Garage.GT(em, b, out var g)) continue;
                if (name.EndsWith("SlipRing")) { rings.Add((b, name, g)); continue; }
                if (SelfRing(name))
                {
                    // 0.31.36: Bearing with Slip Ring — pass-through i pairs with its own ring port i; it never pairs with a ring on its axis
                    var sp = Ecs.Ports(em, b);
                    if (Range(em, sp, name, out int s0, out int sn) && sn == 8)
                        for (int i = 0; i < 4; i++) { long kj = Ecs.K(sp[s0 + i]), ka = Ecs.K(sp[s0 + 4 + i]); portPair[kj] = ka; portPair[ka] = kj; }
                    continue;
                }
                if (tp == Kind.Bearing) jts.Add((b, name, 1.2f, g));
                else if (tp == Kind.Piston) jts.Add((b, name, stroke + 1f, g));
                else if (tp == Kind.TurretSeat) seats.Add((b, name, g));
                else if (tp == Kind.Recoupler) recs.Add((b, name, g));
            }
            if (recs.Count == 0 && (rings.Count == 0 || (jts.Count == 0 && seats.Count == 0))) { LinkPairs(); return; }
            // the game's decouplers (their own query; a mod block that is a copy of the decoupler is not one of them)
            if (recs.Count > 0)
                foreach (var d in Couplers.Decouplers(em))
                {
                    if (!Ecs.Alive(em, d) || JointSystem.KindFromHash(Ecs.Prefab(em, d), out _, out _, out _)) continue;
                    if (Garage.GT(em, d, out var gd)) decs.Add(gd);
                }
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
            PairsTurret(em, seats, rings, usedSet);
            PairsRecouplers(em, recs, decs, rings, usedSet);
            LinkPairs();
        }
        /// <summary>0.31.14 (Andre, 11/11: "the recouplers don't update the ports"): the recoupler had no garage pairing, so its plugs never took
        /// the colour of the cable on the other side (the cue for which port goes with which). Same rules as the simulation (RecouplerLinks),
        /// garage poses: 1) recoupler touching a decoupler -> the ring behind the decoupler; 2) two recouplers touching; 3) ring touching it.</summary>
        static void PairsRecouplers(EntityManager em, List<(Entity e, string name, RigidTransform g)> recs, List<RigidTransform> decs, List<(Entity e, string name, RigidTransform g)> rings, HashSet<long> usedSet)
        {
            if (recs.Count == 0) return;
            var done = new HashSet<long>();
            foreach (var r in recs)
            {
                float md = 0.75f; bool any = false; float3 pd = default;
                foreach (var d in decs) { float dd = Ecs.Dist(d.pos, r.g.pos); if (dd < md) { md = dd; pd = d.pos; any = true; } }
                if (!any) continue;
                int pick = -1; float best = 0.75f;
                for (int i = 0; i < rings.Count; i++)
                {
                    if (usedSet.Contains(Ecs.K(rings[i].e))) continue;
                    float dDec = Ecs.Dist(rings[i].g.pos, pd); if (dDec >= best || dDec >= Ecs.Dist(rings[i].g.pos, r.g.pos) - 0.05f) continue;
                    best = dDec; pick = i;
                }
                if (pick < 0) continue;
                usedSet.Add(Ecs.K(rings[pick].e)); done.Add(Ecs.K(r.e));
                PairPorts(em, r.e, r.name, rings[pick].e, rings[pick].name);
            }
            for (int i = 0; i < recs.Count; i++)
            {
                if (done.Contains(Ecs.K(recs[i].e))) continue;
                int best = -1; float md = 0.75f;
                for (int j = 0; j < recs.Count; j++)
                {
                    if (j == i || done.Contains(Ecs.K(recs[j].e))) continue;
                    float d = Ecs.Dist(recs[i].g.pos, recs[j].g.pos); if (d < md) { md = d; best = j; }
                }
                if (best < 0) continue;
                done.Add(Ecs.K(recs[i].e)); done.Add(Ecs.K(recs[best].e));
                PairPorts(em, recs[i].e, recs[i].name, recs[best].e, recs[best].name);
            }
            foreach (var r in recs)
            {
                if (done.Contains(Ecs.K(r.e))) continue;
                int pick = -1; float md = 0.75f;
                for (int i = 0; i < rings.Count; i++)
                {
                    if (usedSet.Contains(Ecs.K(rings[i].e))) continue;
                    float d = Ecs.Dist(rings[i].g.pos, r.g.pos); if (d < md) { md = d; pick = i; }
                }
                if (pick < 0) continue;
                usedSet.Add(Ecs.K(rings[pick].e)); done.Add(Ecs.K(r.e));
                PairPorts(em, r.e, r.name, rings[pick].e, rings[pick].name);
            }
        }
        static void PairPorts(EntityManager em, Entity a, string nameA, Entity b, string nameB)
        {
            var ps = Ecs.Ports(em, a); var pa = Ecs.Ports(em, b);
            if (!Range(em, ps, nameA, out int i0, out int n0) || !Range(em, pa, nameB, out int i1, out int n1)) return;
            int m = Math.Min(n0, n1);
            for (int i = 0; i < m; i++) { long kj = Ecs.K(ps[i0 + i]), ka = Ecs.K(pa[i1 + i]); portPair[kj] = ka; portPair[ka] = kj; }
        }
        static readonly Dictionary<long, string> pairLog = new Dictionary<long, string>();
        /// <summary>0.29.1 (Andre: "its ports are not reaching the slip rings at its ends, so I can't get power to what is glued to the seat"):
        /// the turret seat was missing from the pairing above, so in the GARAGE its 16 plugs never took the colour of the cable plugged in on the
        /// OTHER side — the only cue that says which of the 4 ports of a set goes with which port of the ring (the link pairs port i with port i).
        /// On the piston and the bearing that cue has been there since 0.23.1, which is why only the seat was impossible to wire by eye.
        /// In the garage there are no parts and no joint list yet, so each set takes the nearest ring that is clearly off that side of the seat.</summary>
        static void PairsTurret(EntityManager em, List<(Entity e, string name, RigidTransform g)> seats, List<(Entity e, string name, RigidTransform g)> rings, HashSet<long> usedSet)
        {
            foreach (var s in seats)
            {
                var ps = Ecs.Ports(em, s.e); if (ps == null || ps.Length < 16) continue;
                var ex = Ecs.Rot(s.g.rot, Ecs.UX); var ey = Ecs.Rot(s.g.rot, Ecs.UY); var ez = Ecs.Rot(s.g.rot, Ecs.UZ);
                var pick = new Entity[PLATES.Length]; var nameOf = new string[PLATES.Length]; var best = new float[PLATES.Length];
                for (int c = 0; c < PLATES.Length; c++) { pick[c] = Entity.Null; best[c] = float.MaxValue; }
                foreach (var a in rings)
                {
                    if (usedSet.Contains(Ecs.K(a.e))) continue;
                    var d = Ecs.Sub(a.g.pos, s.g.pos); float len = Ecs.Len(d);
                    if (len < 1e-4f || len > 16f) continue;                       // another ship's ring is not a hint for this seat
                    float dx = Ecs.Dot(d, ex), dy = Ecs.Dot(d, ey), dz = Ecs.Dot(d, ez);
                    float ax = Math.Abs(dx), ay = Math.Abs(dy), az = Math.Abs(dz);
                    float dom = Math.Max(ax, Math.Max(ay, az)); if (dom < 0.5f * len) continue;   // not clearly off one side: no hint, better than a wrong one
                    string face = ax >= ay && ax >= az ? (dx > 0f ? "right" : "left") : ay >= az ? (dy > 0f ? "top" : "base") : (dz > 0f ? "front" : "back");
                    int cc = Array.IndexOf(PLATES, face); if (cc < 0) continue;    // the base and the front face have no port set (the knobs are on the front)
                    if (len < best[cc]) { best[cc] = len; pick[cc] = a.e; nameOf[cc] = a.name; }
                }
                var said = new List<string>();
                for (int c = 0; c < PLATES.Length; c++)
                {
                    if (Ecs.IsNull(pick[c])) continue;
                    var pa = Ecs.Ports(em, pick[c]); if (!Range(em, pa, nameOf[c], out int i1, out int n1)) continue;
                    usedSet.Add(Ecs.K(pick[c]));
                    int i0 = ps.Length - 16 + c * 4; int m = Math.Min(4, n1);
                    for (int i = 0; i < m; i++) { long kj = Ecs.K(ps[i0 + i]), ka = Ecs.K(pa[i1 + i]); portPair[kj] = ka; portPair[ka] = kj; }
                    said.Add(PLATES[c] + " plate -> ring " + pick[c].Index);
                }
                long ks = Ecs.K(s.e); string sig = said.Count == 0 ? "no ring found off its plates" : string.Join(", ", said);
                if (!pairLog.TryGetValue(ks, out var prev) || prev != sig)
                { pairLog[ks] = sig; ModLog.Info("pass-through plugs: " + s.name + " " + s.e.Index + " — " + sig + " (the plug takes the colour of the cable on the other side; port i of the set goes with port i of the ring)"); }
            }
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
            // 0.31.15: only the mod's blocks, from the block indexes (it read every launched block AND every garage block, twice a second)
            var blocks = new List<Entity>(); var seenSet = new HashSet<long>();
            foreach (var (e, _) in Ecs.Launched(em).mod) blocks.Add(e);
            foreach (var (e, _) in Garage.Set(em).mod) blocks.Add(e);   // 0.22.4: in the garage too (qComps only picks launched blocks)
            var list = new List<(Entity block, Entity port, long k)>(); var own = new Dictionary<long, int>();
            var unique = new List<Entity>();
            foreach (var b in blocks)
            {
                if (!seenSet.Add(Ecs.K(b))) continue;
                if (!Ecs.Alive(em, b)) continue;
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
                int current = *(int*)(p + 8);
                if (!idsLogged) { idsLogged = true; ModLog.Info("pass-through plugs: plug mesh ids — Unset " + idUnset + ", InOut " + idInOut + ", Power " + idPower + "; first plug renderer has " + current + (current == idUnset || current == idInOut || current == idPower ? " (matches)" : " (does NOT match — the swap may not show)")); }
                if (current != wants) *(int*)(p + 8) = wants;
                meshWritten[it.k] = wants;
            }
            if (noRenderer > 0 && colorWarnings++ < 1) ModLog.Warn("pass-through plugs: " + noRenderer + " port(s) without a plug renderer found");
        }
        /// <summary>Cable type from the cell plugged into the port: SCTypeCableCell._type (+1, SpaceshipCableType: 0 Data, 1 Power, 2 PowerNano, 3 Plasma).</summary>
        /// <summary>0.31.4: a porta tem um cabo de energia ligado nela (o cabo, nao o tipo da porta da outra ponta).</summary>
        /// <summary>0.31.11 (Andre's channel 1 receiver: its signal cable goes straight into a pass-through port of an SC_Bearing M, and the game
        /// gives an in/out port its direction only from the port at the other end of the cable): the port at the end of the cable on the OTHER side of
        /// the joint, following a chain of joints (up to 6). False if this port isn't in a linked channel or the other side has nothing.</summary>
        internal static bool AcrossJoint(EntityManager em, Entity pass, out Entity far, out string via)
        {
            far = Entity.Null; via = "";
            var seen = new HashSet<long>(); Entity cur = pass;
            for (int hop = 0; hop < 6; hop++)
            {
                if (!seen.Add(Ecs.K(cur))) return false;
                Link found = null; Channel fc = null; bool jointSide = false;
                foreach (var l in links.Values) { foreach (var c in l.channels) { if (c.pj.Index == cur.Index && c.pj.Version == cur.Version) { found = l; fc = c; jointSide = true; break; } if (c.pa.Index == cur.Index && c.pa.Version == cur.Version) { found = l; fc = c; break; } } if (found != null) break; }
                if (found == null) return false;
                via += (via.Length > 0 ? " -> " : "") + found.name + " #" + fc.i;
                var other = jointSide ? fc.pa : fc.pj;
                var f = Ecs.Source(em, other, out _);
                if (Ecs.IsNull(f)) return false;
                if (!Ecs.Port(em, f, out byte t, out _, out _)) return false;
                if (t == JointSystem.PortUntyped) { cur = f; continue; }   // next joint of a chain
                far = f; return true;
            }
            return false;
        }
        /// <summary>0.31.26 (item 5): the cable plugged into the port has a burnt-through cell (_burnDamage01 +52 >= 1). The game cuts a cable
        /// there: the port's runtime end stops at that cell and the circuit's cells stop before it [recon/Spaceship__part_b.cs:157-159;
        /// recon/Spaceship__part_a.cs:496-497; recon/SpaceshipElectricCircuitsCableCellsMelting.cs:24-27]. Walks from the port to the
        /// first non-cell entity.</summary>
        static unsafe bool Burnt(EntityManager em, Entity port)
        {
            try
            {
                if (!Ecs.RawPortPub(em, port, out _, out _, out _, out var persist)) return false;
                var prev = port; var cur = persist; var seen = new HashSet<long>();
                for (int i = 0; i < 2048 && Ecs.IsCable(em, cur) && seen.Add(Ecs.K(cur)); i++)
                {
                    var b = (byte*)Ecs.RO(em, cur, Ecs.tiCable); if (b == null) return false;
                    if (*(float*)(b + 52) >= 1f) return true;
                    var fwd = *(Entity*)(b + 0x10); var bwd = *(Entity*)(b + 0x18);
                    var nxt = (fwd.Index == prev.Index && fwd.Version == prev.Version) ? bwd : fwd; prev = cur; cur = nxt;
                }
            }
            catch (Exception) { }
            return false;
        }
        static bool PowerCableAt(EntityManager em, Entity port)
        {
            try { return Ecs.RawPortPub(em, port, out _, out _, out _, out var persist) && Ecs.IsCable(em, persist) && IsPowerCable(em, persist); } catch (Exception) { return false; }
        }
        static unsafe bool IsPowerCable(EntityManager em, Entity cell)
        {
            try { var b = (byte*)Ecs.RO(em, cell, Ecs.tiCable); return b != null && (b[1] == 1 || b[1] == 2); } catch (Exception) { return false; }
        }

        /// <summary>Plugin exit / cleanup with the world alive: destroys the ghosts.</summary>
        public static void Shutdown(EntityManager em) { fast = null; foreach (var l in links.Values) foreach (var c in l.channels) { UndoBridge(em, c); ReleaseRuntimeType(em, c); } links.Clear(); noRingLog.Clear(); meshWritten.Clear(); portRend.Clear(); }
        public static void Forget() { losses.Clear(); fedComps.Clear(); fedOf = null; firstLinkAt = -1f; linkSig = long.MinValue; fast = null; rootIndex = null; resolveAll = false; Ecs.ForgetPaths(); Ecs.ForgetBlocks(); Garage.ForgetBlocks(); links.Clear(); frameBridges.Clear(); frameEdges.Clear(); carrier.Clear(); fullAt.Clear(); fullLog.Clear(); grownLogs = 0; growFailed = false; chainLog.Clear(); chainBrokenLog.Clear(); sourcesBefore.Clear(); netLog = ""; noRingLog.Clear(); hashes = false; meshWritten.Clear(); portRend.Clear(); portPair.Clear(); pairLog.Clear(); }
        public static string Situation()
        {
            if (links.Count == 0) return "no slip rings linked" + RingVisual.Situation();
            var sb = new System.Text.StringBuilder(); sb.Append(links.Count).Append(" slip ring link(s):");
            foreach (var l in links.Values)
            {
                sb.Append("\n    ").Append(l.name).Append(':');
                foreach (var c in l.channels) sb.Append(" #").Append(c.i).Append('=').Append(c.direction == 1 ? "J>R" : c.direction == -1 ? "R>J" : c.direction == 3 ? "power" : c.direction == 2 ? "CONFLICT" : "-");
            }
            sb.Append(RingVisual.Situation());   // 0.31.36: the built-in rings' drawing mode
            return sb.ToString();
        }
    }
}
