using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Transforms;
using SW = CrapMechanics.SealWatch;
using V = CrapMechanics.SealWatch.V;
using Q = CrapMechanics.SealWatch.Q;

namespace CrapMechanics
{
    /// <summary>0.31.30 sound (Andre: "the room is sealed for the air but the thrusters still come in"). [Air] sound_sees_small_blocks.
    ///
    /// How the game muffles a sound [revisao/som_pesquisa.md]: SpaceshipSoundVoxelsSystem keeps a grid of 0.5 m cells in the garage space
    /// of the ONE ship part the local player is in (InteriorMeterData._spEntity). Only entities with SoundVoxelObstacle {int3 min, max}
    /// enter it, and only if they have no Parent or their Parent is that part (FindMinMaxJob and the 5 other jobs). A block gets the
    /// obstacle from its prefab's _soundObstacleMin/Max, and MicroFrames, Rubber Seals, every mod block (and the game's pipes, panels...)
    /// have none; the blocks of another part (a gate on mod joints) are skipped by the Parent filter. So for sound a gate of MicroFrames
    /// on joints is an open hole, while the air test (real physics) sees it closed.
    ///
    /// What this does, only on this machine and only while the local player is inside a ship part: for the 0.5 m cells of that part's
    /// grid that the game leaves empty but where such ignored geometry is (blocks of the part with no sound obstacle, and the blocks of
    /// every other part reaching into the grid at their current pose), each side between two cell centres is tested with 7x7 short
    /// parallel rays (0.5 m, the cell centre to the neighbour's centre, 8 cm apart, over +-24 cm), with the air test's rules (SealWatch: ball
    /// of 3.5 cm, ship colliders of filter 0x18, a frame blocks only where the centre line crosses one of its closed faces with the game's
    /// 2 % tolerance — the MicroFrames' table from FrameFaces when [Air] frame_seam_like_game is on —, a non-frame block blocks). A ray
    /// counts only geometry it ENTERS (a block it starts inside is skipped, a face at its very start is skipped), so one plate closes
    /// exactly one of the two pairs around it. A side is closed only if ALL 49 rays are blocked. Untested sides are open.
    /// Each cell with a closed side becomes one invisible entity holding only SoundVoxelObstacle {cell, cell} + SoundVoxelModularObstacle
    /// {open sides, bit0 +X, bit1 -X, bit2 +Y, bit3 -Y, bit4 +Z, bit5 -Z, garage axes of the part} [+ SoundVoxelDynamicObstacle]. A cell
    /// another part reaches (or that already changed once) is DYNAMIC: the game rewrites its sides every frame, so a moving gate needs no
    /// rebuild. The rest are STATIC, like a game frame: the game reads them only when it rebuilds the grid, so a hull of MicroFrames costs
    /// the game nothing per frame (its dynamic jobs redo 27 voxels per dynamic obstacle, every frame, on one thread); a static cell that
    /// changes asks for a rebuild and becomes dynamic. No Parent, no transform, no physics, no SC component: nothing saves, syncs or draws
    /// it. A dynamic cell that opens again keeps its entity with 0x3F (exactly an empty cell). The game's rebuild flag
    /// (SoundSystemSingleton._voxelsRebuildObstacles, +144) is set when entities are created or destroyed or a static one changes. They are
    /// destroyed when the player leaves the part, the setting goes off, the blueprint/manual menu or a replay playback opens, and on world
    /// unload (and any left behind are swept: only sound components, no Parent, no SCPrefab, no LocalTransform). The six sides around the
    /// listener's cell are left open when all six are shut or when geometry runs between the listener and that cell's centre (see Listener).
    /// Cost: the rays run with a budget of 0.35 ms per frame, nearest sides first; nothing runs while nothing moves. Geometry is checked
    /// every 0.3 s; the parts reaching in are also checked every 0.05 s: one that moved more than 1 cm gets its closed sides re-tested first
    /// with the 9 rays most likely to find an opening (open at once if one passes), then the full 49.</summary>
    internal static class SoundSeal
    {
        public static bool Enabled = true;   // [Air] sound_sees_small_blocks
        public static bool Watch;            // [Debug] sound_watch (only with [Debug] debug_tools)
        // 0.31.35 (performance; Andre's ship: 2319 to 2600 cell entities, 1335 to 1600 of them the moving kind). The game's sound grid system
        // runs in PhysicsPostGroup, on every physics TICK, and for every moving-kind obstacle it redoes 27 voxels in three jobs, two of them
        // on one thread (about 3 ms of a worker thread per update with 1335 cells) — while the gate stands still and no side changes.
        // And a still cell that changed asked for a rebuild of the whole grid (with a wait on the main thread) on every frame it changed.
        // [Air] sound_cells_settle (Settle):
        //   - a still cell whose sides change gets the moving tag in the same frame (all such cells of the frame one after the other, one
        //     wait for the game's jobs), and NO rebuild: the game's next moving-cell update writes its sides (as for any moving cell);
        //   - a still cell that opens completely stays as a moving cell with 0x3F, exactly an empty voxel (as moving cells already did);
        //   - a moving cell whose sides did not change for `quiet` seconds (5 s; doubled, up to 60 s, each time that cell had to become
        //     moving again), that is not one of the 27 cells around the listener and has no side waiting for rays, becomes still again:
        //     the tag is removed (an all-open one is destroyed instead) and ONE rebuild is asked for the whole batch; at most one batch
        //     every 10 s, and only for 32 cells or more (or a quarter of the moving ones).
        // The sound is the same: for a cell of one voxel the rebuild writes exactly sides << 26 (RebuildModularObstaclesJob, no inner sides
        // since min == max) and so does the moving-cell job (UpdateDynamicObstacleVoxelsJob); the links between voxels follow the same
        // rules in both [revisao/som_pesquisa.md 1.5-1.6]. Both states are ones the mod already produced before (new still cells after a
        // rebuild; moving cells): nothing new is asked of the game. [Debug] sound_compare hashes the game's finished grid around a batch.
        // 0.31.36 (Andre's log of 22/11: about 30 full rebuilds of the game's grid per 10 s while the cells were being made, one per 0.3 s
        // batch of new cells). With sound_cells_settle on, a NEW cell is born the moving kind when its voxel is strictly inside the grid the
        // game has now (GridHolds): the game's next moving-cell update writes its sides — as promptly as the rebuild did, and with no
        // rebuild. A new cell on the grid's outer layer or outside it (the grid must grow, and the game keeps one layer of air around
        // every obstacle), or when the grid cannot be read, is made as before and asks for the rebuild as before.
        // A cell that is moving-kind ONLY because of this (Cell.born: it never changed since it was made and is not next to the listener;
        // up to 0.31.35 it was made still) goes back to still in the quiet batch above, with two differences: it needs only 2 s without
        // a change (BornQuiet; the rebuild of the batch reads every cell's sides anyway), and while 128 or more of them wait (BornBatch:
        // the ship is being read) a batch may come 4 s after the last one (BornEvery) instead of 10 s — so the moving-kind cells the game
        // redoes on every update stay about as many as before, and the rebuilds go from one per 0.3 s to one per 4 s while the ship is
        // read, and to none once it is read (a gate that works makes a few new cells: they wait for the usual batch). A born cell whose
        // sides change is an ordinary moving cell from then on (5 s of quiet).
        public static bool Settle = true;    // [Air] sound_cells_settle
        public static bool Compare;          // [Debug] sound_compare (only with [Debug] debug_tools)
        const float QuietStart = 5f, QuietMax = 60f, SettleEvery = 10f, BornQuiet = 2f, BornEvery = 4f; const int SettleMin = 32, BornBatch = 128;

        const float Len = 0.5f, Eps = 0.001f, Shrink = 0.002f, Grow = SW.R + 0.001f;
        const float RefreshEvery = 0.3f, BudgetMs = 0.35f, MoveDirty = 0.005f, MoveHot = 0.02f, MotionEvery = 0.05f, OwnPoseEvery = 5f, BlockCacheEvery = 10f;   // 0.31.34: the air test of a cell does not flip with a few mm (was 2 mm / 1 cm: a flexing leaf in flight re-tested its cells all the time)
        const int MaxCells = 20000, MaxDead = 512, QuickRays = 9;
        // 7x7 rays 8 cm apart, the outer ones 1 cm inside the cell's edge: with the 3.5 cm ball a seam of 15 cm or more is always found
        // (from 9 cm on, depending on where it falls); under 7 cm it is closed, as in the air test
        static readonly float[] Off = { -0.24f, -0.16f, -0.08f, 0f, 0.08f, 0.16f, 0.24f };
        const int NOff = 7, NRays = NOff * NOff;
        static readonly int[] RayOrder = Order();   // corners first, then the centre and the edge middles: an open side usually shows at once
        static int[] Order() { var l = new List<int> { 0, 6, 42, 48, 24, 3, 21, 27, 45 }; for (int i = 0; i < NRays; i++) if (!l.Contains(i)) l.Add(i); return l.ToArray(); }

        // ---------------------------------------------------------------- keys (cell = 0.5 m, garage space of the player's part)
        const int B = 1 << 19;
        static long Key(int x, int y, int z) => ((long)(x + B) << 40) | ((long)(y + B) << 20) | (long)(z + B);
        static void Unkey(long k, out int x, out int y, out int z) { x = (int)((k >> 40) & 0xFFFFF) - B; y = (int)((k >> 20) & 0xFFFFF) - B; z = (int)(k & 0xFFFFF) - B; }
        static long PairKey(long cell, int axis) => (cell << 2) | (long)axis;
        static long Nb(long cell, int axis, int sgn) { Unkey(cell, out int x, out int y, out int z); if (axis == 0) x += sgn; else if (axis == 1) y += sgn; else z += sgn; return Key(x, y, z); }
        static int Fl(float v) => (int)Math.Floor(v * 2f);
        static bool Sane(V a) => Math.Abs(a.x) < 100000f && Math.Abs(a.y) < 100000f && Math.Abs(a.z) < 100000f;

        // ---------------------------------------------------------------- ship parts seen by the rays
        class Ref { public Ctx c; public SW.Child k; public int stamp; }
        class Ctx
        {
            public Entity part; public SW.Part geo; public long sig; public bool own;
            public Q q = new Q { w = 1f }, qi = new Q { w = 1f }; public V p;   // this part's space -> the player part's space
            public Ref[] refs;
            public readonly Dictionary<long, List<Ref>> hash = new Dictionary<long, List<Ref>>();   // cell -> colliders whose box (+R) reaches it
            public readonly HashSet<long> foot = new HashSet<long>();                               // cells holding geometry the game's sound ignores
            // own part only
            public HashSet<long> covered = new HashSet<long>(); public HashSet<long> obstacles = new HashSet<long>(); public int childCount = -2;
            public HashSet<long> doors = new HashSet<long>();   // review: blocks with the game's DYNAMIC sound obstacle (its doors): the game already
                                                                 // opens/closes them itself, and their collider moves in place, so the rays skip them
            public int cminX, cminY, cminZ, cmaxX, cmaxY, cmaxZ; public double poseSum; public float tPose;
            // other parts: the ray in this part's space (cached per ray)
            public int rs = -1; public V ro, rd;
        }
        static Ctx own; static readonly Dictionary<long, Ctx> ownCache = new Dictionary<long, Ctx>();
        static readonly Dictionary<long, Ctx> others = new Dictionary<long, Ctx>();
        static readonly Dictionary<long, float> posedAt = new Dictionary<long, float>();   // review: other parts' collider children re-read every 5 s, not every 0.3 s

        class Pair { public bool closed, queued, hot; }
        class Cell { public long key; public bool cand; public Entity proxy; public int written = -1; public bool dyn, changed; public float tChange, quiet = QuietStart; public bool settled, born; }
        static readonly Dictionary<long, Pair> pairs = new Dictionary<long, Pair>();
        static readonly Dictionary<long, Cell> cells = new Dictionary<long, Cell>();
        static readonly Queue<long> work = new Queue<long>(), hotWork = new Queue<long>();
        static readonly HashSet<long> sidesDirty = new HashSet<long>(), pending = new HashSet<long>(), toDyn = new HashSet<long>();
        static int proxies, bornLive;   // bornLive: cells with Cell.born (0.31.36)
        static void Unborn(Cell c) { if (c.born) { c.born = false; bornLive--; } }

        static Entity curPart = Entity.Null; static bool active; static long listenerKey = long.MinValue; static bool listenerOpen;
        static float tPrune, tRefresh = 99f, tWatch, tLog = -99f, tSweep = 99f, tBlocks, tMotion; static string lastLog = "";
        static bool sweepNeeded = true, needCand = true, noColliderSaid; static int warnings, stamp, rayStamp, tested, raysCast, listenerSaved, quickOpened;
        static double spentMs, buildMs; static int spentFrames;

        // ---------------------------------------------------------------- types
        static TypeIndex tiObs, tiMod, tiDyn, tiSSS, tiPC, tiIMD, tiMine, tiChild, tiThrSound, tiThr;
        static EntityQuery qMine, qSSS, qObs, qProxyLike, qThr, qDynObs; static bool ok, failed, thrOk;
        // 0.31.35: for the "performance+:" line and [Debug] sound_compare
        static int nRebuilds, nMade, nKilled, nToDyn, nToStill, nBatches, nStruct, nBorn, nOutside; static double structMs; static long writes; static float tSettle = -99f, tLastWrite = -99f;
        static Il2CppStructArray<ComponentType> kinds, kindsStatic;

        static bool Types(EntityManager em)
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiObs = TypeManager.GetTypeIndex(Il2CppType.Of<SoundVoxelObstacle>());
                tiMod = TypeManager.GetTypeIndex(Il2CppType.Of<SoundVoxelModularObstacle>());
                tiDyn = TypeManager.GetTypeIndex(Il2CppType.Of<SoundVoxelDynamicObstacle>());
                tiSSS = TypeManager.GetTypeIndex(Il2CppType.Of<SoundSystemSingleton>());
                tiPC = TypeManager.GetTypeIndex(Il2CppType.Of<Unity.Physics.PhysicsCollider>());
                tiIMD = TypeManager.GetTypeIndex(Il2CppType.Of<InteriorMeterData>());
                tiMine = TypeManager.GetTypeIndex(Il2CppType.Of<MyPlayerController>());
                tiChild = TypeManager.GetTypeIndex(Il2CppType.Of<Child>());
                qMine = Q2(em, tiMine, tiIMD);
                var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiSSS); qSSS = em.CreateEntityQuery(a);
                var b = new Il2CppStructArray<ComponentType>(1); b[0] = ComponentType.ReadOnly(tiObs); qObs = em.CreateEntityQuery(b);
                qDynObs = Q2(em, tiObs, tiDyn);   // 0.31.35: every moving-kind sound obstacle (the game's doors and the mod's cells), counted for the performance line
                // review: the sweep looks only at entities that could be ours (static and dynamic cells): obstacle + modular, and none of
                // Parent / SCPrefab / LocalTransform (every game block has them), so it no longer walks every frame of every ship
                var c = new Il2CppStructArray<ComponentType>(5); c[0] = ComponentType.ReadOnly(tiObs); c[1] = ComponentType.ReadOnly(tiMod);
                c[2] = Excl(Ecs.tiParent); c[3] = Excl(Ecs.tiPrefab); c[4] = Excl(Ecs.tiLT); qProxyLike = em.CreateEntityQuery(c);
                kinds = new Il2CppStructArray<ComponentType>(3); kinds[0] = ComponentType.ReadWrite(tiObs); kinds[1] = ComponentType.ReadWrite(tiMod); kinds[2] = ComponentType.ReadWrite(tiDyn);
                // review: a cell no other part reaches is STATIC (obstacle + modular, like a game frame): the game reads it only when it rebuilds
                // the grid, so it costs nothing per frame. The game's dynamic jobs redo 27 voxels per dynamic obstacle every frame on one thread
                kindsStatic = new Il2CppStructArray<ComponentType>(2); kindsStatic[0] = ComponentType.ReadWrite(tiObs); kindsStatic[1] = ComponentType.ReadWrite(tiMod);
                if (!FrameFaces.Types(em)) { failed = true; return false; }
                try
                {
                    tiThrSound = TypeManager.GetTypeIndex(Il2CppType.Of<SCThrusterSound>()); tiThr = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeThruster>());
                    var d = new Il2CppStructArray<ComponentType>(3); d[0] = ComponentType.ReadOnly(tiThrSound); d[1] = ComponentType.ReadOnly(tiThr); d[2] = ComponentType.ReadOnly(Ecs.tiLTW); qThr = em.CreateEntityQuery(d);
                    thrOk = true;
                }
                catch (Exception ex) { ModLog.Warn("sound seal: thruster types not available (" + ex.Message + ") — sound_watch will not show the thrusters"); }
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("sound seal: types not available (" + ex.Message + ") — off, the game's own sound"); return false; }
        }
        static EntityQuery Q2(EntityManager em, TypeIndex a, TypeIndex b) { var x = new Il2CppStructArray<ComponentType>(2); x[0] = ComponentType.ReadOnly(a); x[1] = ComponentType.ReadOnly(b); return em.CreateEntityQuery(x); }
        static ComponentType Excl(TypeIndex ti) { var t = ComponentType.ReadOnly(ti); t.AccessModeType = ComponentType.AccessMode.Exclude; return t; }   // as Garage.cs

        // ---------------------------------------------------------------- the cycle
        public static void Step(EntityManager em)
        {
            if (Unloading.Active) return;
            long t0 = Stopwatch.GetTimestamp();
            try { Run(em); }
            catch (Exception ex) { if (warnings++ < 5) ModLog.Warn("sound seal: " + ex.Message); }
            spentMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency; spentFrames++;
        }

        /// <summary>Blueprint/manual menu, replay playback, mod switched off: the cells go (the cycle stops reading the ship).</summary>
        public static void Pause(EntityManager em)
        {
            if (Unloading.Active || (!active && proxies == 0)) return;
            try { if (ok) Drop(em, "the mod cycle is paused (menu, replay or the mod off)", true); } catch (Exception ex) { if (warnings++ < 5) ModLog.Warn("sound seal: " + ex.Message); }
        }

        static unsafe void Run(EntityManager em)
        {
            if (!Ecs.Prepare(em) || !Types(em)) return;
            float dt = UnityEngine.Time.unscaledDeltaTime;
            // review: the sweep runs before the Enabled test, so cells left by an unload are removed even with the setting off
            tSweep += dt; if (sweepNeeded || tSweep > 10f) { tSweep = 0f; Sweep(em); }
            if (!Enabled) { if (active || proxies > 0) Drop(em, "[Air] sound_sees_small_blocks is off", true); return; }

            // the part the game builds its sound grid for: InteriorMeterData._spEntity of the local player
            Entity part = Entity.Null;
            foreach (var me in Ecs.QueryList(qMine)) { var imd = (byte*)Ecs.RO(em, me, tiIMD); if (imd != null) part = *(Entity*)(imd + 48); break; }
            if (Ecs.IsNull(part) || !Ecs.Alive(em, part) || !Ecs.Has(em, part, Ecs.tiSPData) || !Ecs.Has(em, part, Ecs.tiLT) || !Ecs.Has(em, part, tiPC))   // (no PhysicsWorldIndex test: a client's parts may lack it)
            {
                // review: say it once if the part is there but has no collider on this machine (a client?): then nothing can be tested
                if (!noColliderSaid && !Ecs.IsNull(part) && Ecs.Alive(em, part) && Ecs.Has(em, part, Ecs.tiSPData) && Ecs.Has(em, part, Ecs.tiLT) && !Ecs.Has(em, part, tiPC))
                { noColliderSaid = true; ModLog.Info("sound seal: the ship part you are in (" + part.Index + ") has no PhysicsCollider on this machine — nothing to test, the game's own sound (multiplayer client?)"); }
                if (active || proxies > 0) Drop(em, "you are not inside a ship part", true);
                curPart = Entity.Null; return;
            }
            if (part.Index != curPart.Index || part.Version != curPart.Version)
            {
                if (active || proxies > 0) Drop(em, "you moved to another part", false);
                curPart = part; active = true; tRefresh = 99f; others.Clear(); own = null; needCand = true;
            }

            // the listener: the game hears from LocalToWorld (0,0,0), the floating origin = the local player
            var lt = (LocalTransform*)Ecs.RO(em, part, Ecs.tiLT);
            var qP = new Q { x = lt->Rotation.value.x, y = lt->Rotation.value.y, z = lt->Rotation.value.z, w = lt->Rotation.value.w };
            var pP = new V(lt->Position.x, lt->Position.y, lt->Position.z);
            var lp = SW.Rot(SW.Inv(qP), SW.Mul(pP, -1f));
            // review: the game's listener voxel is (int)(2 M 0), a cast toward zero (UpdateSoundVoxelsDistancesJob), not a floor
            long lk = Sane(lp) ? Key((int)(lp.x * 2f), (int)(lp.y * 2f), (int)(lp.z * 2f)) : long.MinValue;
            if (lk != listenerKey) { DirtyAround(listenerKey); listenerKey = lk; DirtyAround(lk); }

            tRefresh += dt; tMotion += dt;
            bool refresh = tRefresh >= RefreshEvery;
            if (refresh) { tRefresh = 0f; tMotion = 0f; Refresh(em, part, pP, qP); if (own == null) return; }
            if (own == null) return;
            // review: a gate leaf that moved more than 1 cm is re-tested at once (first the rays that find an opening), not 0.3 s later
            if (!refresh && tMotion >= MotionEvery && others.Count > 0) { tMotion = 0f; Motion(em, pP, qP); }

            Work(em);
            Listener(em, lp);
            Apply(em);
            if (refresh) { Create(em); Report(); }
            if (cmpStage != 0) CompareStep(em);
            if (Watch) { tWatch += dt; if (tWatch >= 2f) { tWatch = 0f; try { WatchLine(em, part); } catch (Exception ex) { if (warnings++ < 5) ModLog.Warn("sound watch: " + ex.Message); } } }
        }

        // ---------------------------------------------------------------- geometry (every 0.3 s)
        static unsafe void Refresh(EntityManager em, Entity part, V pP, Q qP)
        {
            long t0 = Stopwatch.GetTimestamp();
            IntPtr blob = *(IntPtr*)Ecs.RO(em, part, tiPC); if (blob == IntPtr.Zero) return;
            var geo = SW.PartOf(em, part, blob); if (geo == null || geo.ch.Length == 0) return;
            int childCount = Ecs.HasC(em, part, tiChild) ? em.GetBufferLength(part, tiChild) : -1;
            float now = UnityEngine.Time.unscaledTime;
            tBlocks += RefreshEvery; if (tBlocks > BlockCacheEvery) { tBlocks = 0f; blocks.Clear(); }

            if (own == null) { ownCache.TryGetValue(Ecs.K(part), out own); }
            bool ownNew = own == null || own.geo != geo || own.sig != geo.sig || own.childCount != childCount;
            if (!ownNew && now - own.tPose > OwnPoseEvery)
            {
                // a game door moves its collider child in place: re-read the poses now and then
                own.tPose = now; SW.Pose(geo, (byte*)blob);
                if (Math.Abs(PoseSum(geo, own.doors) - own.poseSum) > 1e-4) ownNew = true;   // (the game's doors are left out: they move by design)
            }
            var dirty = new HashSet<long>(); bool openDirty = false, hotDirty = false, candChanged = needCand; needCand = false;   // needCand: a part taken back from the cache starts with no cells
            if (ownNew)
            {
                bool had = cells.Count > 0;
                if (had) ResetCells(em, "the blocks of the part changed");
                if (own == null || own.part.Index != part.Index || own.part.Version != part.Version) { own = new Ctx { part = part, own = true }; }
                own.geo = geo; own.sig = geo.sig; own.childCount = childCount; own.tPose = now;
                SW.Pose(geo, (byte*)blob);
                own.cminX = Fl(geo.pmin.x - 0.5f); own.cminY = Fl(geo.pmin.y - 0.5f); own.cminZ = Fl(geo.pmin.z - 0.5f);
                own.cmaxX = Fl(geo.pmax.x + 0.5f); own.cmaxY = Fl(geo.pmax.y + 0.5f); own.cmaxZ = Fl(geo.pmax.z + 0.5f);
                Obstacles(em, own);
                own.poseSum = PoseSum(geo, own.doors);
                Hash(own);
                if (ownCache.Count > 4) ownCache.Clear();
                ownCache[Ecs.K(part)] = own;
                others.Clear(); candChanged = true;
                buildMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                int ignored = 0; foreach (var r in own.refs) if (!own.obstacles.Contains(Ecs.K(r.k.e))) ignored++;
                ModLog.Info("sound seal: inside part " + part.Index + " — " + geo.ch.Length + " collider(s), " + own.obstacles.Count + " block(s) with a sound obstacle of the game (" + own.covered.Count + " cell(s) of 0.5 m), "
                            + ignored + " the game's sound does not see; testing the sides of " + own.foot.Count + " cell(s) of this part with rays (" + buildMs.ToString("F1") + " ms to read the part)");
            }

            // the other parts reaching into the grid (gate leaves on mod joints, docked ships), at their pose now
            var clipMin = new V(own.cminX * 0.5f, own.cminY * 0.5f, own.cminZ * 0.5f); var clipMax = new V((own.cmaxX + 1) * 0.5f, (own.cmaxY + 1) * 0.5f, (own.cmaxZ + 1) * 0.5f);
            float reach = SW.Len(SW.Sub(clipMax, clipMin)); var qPi = SW.Inv(qP);
            var seen = new HashSet<long>(); var alive = new HashSet<long>();
            foreach (var o in Ecs.Parts(em))
            {
                alive.Add(Ecs.K(o));
                if (o.Index == part.Index && o.Version == part.Version) continue;
                if (!Ecs.Has(em, o, tiPC)) continue;
                var lo = (LocalTransform*)Ecs.RO(em, o, Ecs.tiLT);
                var pO = new V(lo->Position.x, lo->Position.y, lo->Position.z);
                var pRel = SW.Rot(qPi, SW.Sub(pO, pP));
                var mid = SW.Mul(SW.Add(clipMin, clipMax), 0.5f);
                if (!Sane(pRel) || SW.Len(SW.Sub(pRel, mid)) > reach + 120f) continue;
                IntPtr ob = *(IntPtr*)Ecs.RO(em, o, tiPC); if (ob == IntPtr.Zero) continue;
                var og = SW.PartOf(em, o, ob); if (og == null || og.ch.Length == 0) continue;
                long key = Ecs.K(o);
                others.TryGetValue(key, out var c);
                // review: the children's in-part poses (they move only for a game door; a part on a joint moves as a whole = its
                // LocalTransform below): every 0.3 s for the parts reaching in, every 5 s for the rest (before: every part within ~120 m, every 0.3 s)
                bool inner = false;
                if (c != null || !posedAt.TryGetValue(key, out float tp) || now - tp > OwnPoseEvery)
                {
                    SW.Pose(og, (byte*)ob); posedAt[key] = now;
                    if (c != null && c.geo == og && Math.Abs(PoseSum(og, noSkip) - c.poseSum) > 1e-4) inner = true;   // a door of that part moved
                }
                var qO = new Q { x = lo->Rotation.value.x, y = lo->Rotation.value.y, z = lo->Rotation.value.z, w = lo->Rotation.value.w };
                var q = SW.Mul(qPi, qO);
                Box(og.pmin, og.pmax, q, pRel, out var bmn, out var bmx);
                bool overlaps = bmx.x >= clipMin.x && bmn.x <= clipMax.x && bmx.y >= clipMin.y && bmn.y <= clipMax.y && bmx.z >= clipMin.z && bmn.z <= clipMax.z;
                if (!overlaps) continue;
                seen.Add(key);
                if (c == null) { c = new Ctx { part = o }; others[key] = c; }
                bool changed = c.geo != og || c.sig != og.sig;
                float disp = changed || c.refs == null ? 1e9f : Disp(og, c.q, c.p, q, pRel);
                if (!changed && !inner && disp <= MoveDirty) continue;
                Retake(c, og, q, pRel, dirty);
                if (changed || inner) openDirty = true;           // its blocks changed (some gone): what it closed opens now and is tested again
                else if (disp > MoveHot) hotDirty = true;   // moved: tested again at once, opening first (no blind opening: a joint that flexes in
                                                            // flight would make the gate flicker open)
                candChanged = true;
            }
            var gone = new List<long>();
            foreach (var kv in others) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var key in gone) { foreach (var k in others[key].hash.Keys) dirty.Add(k); others.Remove(key); openDirty = true; candChanged = true; }

            if (candChanged) Candidates();
            if (dirty.Count > 0) MarkDirty(dirty, openDirty, hotDirty);
            tPrune += RefreshEvery;
            if (tPrune > 30f)
            {
                tPrune = 0f; SW.Prune(alive);   // parts gone (launch, split, rewind) leave the collider caches
                foreach (var k in new List<long>(ownCache.Keys)) if (!alive.Contains(k)) ownCache.Remove(k);
                foreach (var k in new List<long>(posedAt.Keys)) if (!alive.Contains(k)) posedAt.Remove(k);
            }
        }

        /// <summary>Another part at a new pose (or with new blocks): its old and new cells are dirty.</summary>
        static void Retake(Ctx c, SW.Part og, Q q, V pRel, HashSet<long> dirty)
        {
            foreach (var k in c.hash.Keys) dirty.Add(k);
            c.geo = og; c.sig = og.sig; c.q = q; c.qi = SW.Inv(q); c.p = pRel; c.poseSum = PoseSum(og, noSkip);
            Hash(c);
            foreach (var k in c.hash.Keys) dirty.Add(k);
        }
        static readonly HashSet<long> noSkip = new HashSet<long>();

        /// <summary>review: every 0.05 s, only the parts already reaching in (a few): moved more than 1 cm since their rays were cast = new pose
        /// now and their sides tested again first thing (an opening gate opens the sound within a frame or two, not 0.3 s later).</summary>
        static unsafe void Motion(EntityManager em, V pP, Q qP)
        {
            var qPi = SW.Inv(qP); HashSet<long> dirty = null; bool gone = false;
            foreach (var c in others.Values)
            {
                if (c.geo == null || c.refs == null) continue;
                if (!Ecs.Alive(em, c.part) || !Ecs.Has(em, c.part, Ecs.tiLT)) { gone = true; continue; }
                var lo = (LocalTransform*)Ecs.RO(em, c.part, Ecs.tiLT);
                var pRel = SW.Rot(qPi, SW.Sub(new V(lo->Position.x, lo->Position.y, lo->Position.z), pP));
                var q = SW.Mul(qPi, new Q { x = lo->Rotation.value.x, y = lo->Rotation.value.y, z = lo->Rotation.value.z, w = lo->Rotation.value.w });
                if (!Sane(pRel) || Disp(c.geo, c.q, c.p, q, pRel) <= MoveHot) continue;
                if (dirty == null) dirty = new HashSet<long>();
                Retake(c, c.geo, q, pRel, dirty);
            }
            if (gone) tRefresh = RefreshEvery;   // a part vanished: the full refresh next frame drops it and opens what it closed
            if (dirty == null) return;
            Candidates();
            MarkDirty(dirty, false, true);
        }

        static double PoseSum(SW.Part g, HashSet<long> skip) { double s = 0; foreach (var k in g.ch) { if (skip.Contains(Ecs.K(k.e))) continue; s += k.pos.x * 1.1 + k.pos.y * 1.3 + k.pos.z * 1.7 + k.rot.x + k.rot.y * 2 + k.rot.z * 3; } return s; }

        /// <summary>The axis-aligned box (player part space) of a box of another part's space.</summary>
        static void Box(V mn, V mx, Q q, V p, out V bmn, out V bmx)
        {
            bmn = new V(1e30f, 1e30f, 1e30f); bmx = new V(-1e30f, -1e30f, -1e30f);
            for (int i = 0; i < 8; i++)
            {
                var c = SW.Add(p, SW.Rot(q, new V((i & 1) != 0 ? mx.x : mn.x, (i & 2) != 0 ? mx.y : mn.y, (i & 4) != 0 ? mx.z : mn.z)));
                bmn = new V(Math.Min(bmn.x, c.x), Math.Min(bmn.y, c.y), Math.Min(bmn.z, c.z)); bmx = new V(Math.Max(bmx.x, c.x), Math.Max(bmx.y, c.y), Math.Max(bmx.z, c.z));
            }
        }
        /// <summary>How far (m) the corners of the part's box moved between two poses.</summary>
        static float Disp(SW.Part g, Q q0, V p0, Q q1, V p1)
        {
            float m = 0f;
            for (int i = 0; i < 8; i++)
            {
                var c = new V((i & 1) != 0 ? g.pmax.x : g.pmin.x, (i & 2) != 0 ? g.pmax.y : g.pmin.y, (i & 4) != 0 ? g.pmax.z : g.pmin.z);
                m = Math.Max(m, SW.Len(SW.Sub(SW.Add(p1, SW.Rot(q1, c)), SW.Add(p0, SW.Rot(q0, c)))));
            }
            return m;
        }

        /// <summary>The game's sound obstacles of this part: which blocks have one, and the cells they cover (never touched by this file).</summary>
        static unsafe void Obstacles(EntityManager em, Ctx c)
        {
            c.obstacles.Clear(); c.covered.Clear(); c.doors.Clear();
            foreach (var e in Ecs.QueryList(qObs))
            {
                if (!Ecs.Has(em, e, Ecs.tiParent)) continue;   // no Parent: the mod's own cells (and the garage's blocks)
                var par = *(Entity*)Ecs.RO(em, e, Ecs.tiParent);
                if (par.Index != c.part.Index || par.Version != c.part.Version) continue;
                c.obstacles.Add(Ecs.K(e));
                if (Ecs.Has(em, e, tiDyn)) c.doors.Add(Ecs.K(e));
                var o = (int*)Ecs.RO(em, e, tiObs); if (o == null) continue;
                int x0 = Math.Max(o[0], c.cminX), y0 = Math.Max(o[1], c.cminY), z0 = Math.Max(o[2], c.cminZ), x1 = Math.Min(o[3], c.cmaxX), y1 = Math.Min(o[4], c.cmaxY), z1 = Math.Min(o[5], c.cmaxZ);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 200000) continue;
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++) c.covered.Add(Key(x, y, z));
            }
        }

        /// <summary>Cells of each collider (its box grown by the ray's ball, for the rays) and, for geometry the game's sound ignores, the cells
        /// it is really in (box shrunk by 2 mm). Only inside the player part's box plus one cell.</summary>
        static void Hash(Ctx c)
        {
            c.hash.Clear(); c.foot.Clear();
            var g = c.geo;
            if (c.refs == null || c.refs.Length != g.ch.Length || (c.refs.Length > 0 && c.refs[0].k != g.ch[0]))
            {
                c.refs = new Ref[g.ch.Length]; for (int i = 0; i < g.ch.Length; i++) c.refs[i] = new Ref { c = c, k = g.ch[i] };
            }
            var o = own;
            var clipMin = new V(o.cminX * 0.5f, o.cminY * 0.5f, o.cminZ * 0.5f); var clipMax = new V((o.cmaxX + 1) * 0.5f, (o.cmaxY + 1) * 0.5f, (o.cmaxZ + 1) * 0.5f);
            V cmnO = default, cmxO = default;
            if (!c.own) Box(SW.Sub(clipMin, new V(Grow, Grow, Grow)), SW.Add(clipMax, new V(Grow, Grow, Grow)), c.qi, SW.Rot(c.qi, SW.Mul(c.p, -1f)), out cmnO, out cmxO);   // the clip box in that part's space
            foreach (var r in c.refs)
            {
                var k = r.k; V mn, mx;
                if (c.own && o.doors.Contains(Ecs.K(k.e))) continue;   // review: a game door (dynamic obstacle) is the game's own business, open or shut
                if (c.own) { mn = k.amin; mx = k.amax; }
                else
                {
                    if (k.amax.x < cmnO.x || k.amin.x > cmxO.x || k.amax.y < cmnO.y || k.amin.y > cmxO.y || k.amax.z < cmnO.z || k.amin.z > cmxO.z) continue;
                    Box(k.amin, k.amax, c.q, c.p, out mn, out mx);
                }
                if (!Sane(mn) || !Sane(mx)) continue;
                int x0 = Math.Max(Fl(mn.x - Grow), o.cminX), y0 = Math.Max(Fl(mn.y - Grow), o.cminY), z0 = Math.Max(Fl(mn.z - Grow), o.cminZ);
                int x1 = Math.Min(Fl(mx.x + Grow), o.cmaxX), y1 = Math.Min(Fl(mx.y + Grow), o.cmaxY), z1 = Math.Min(Fl(mx.z + Grow), o.cmaxZ);
                if (x0 > x1 || y0 > y1 || z0 > z1 || (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 20000) continue;
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
                {
                    long key = Key(x, y, z);
                    if (!c.hash.TryGetValue(key, out var l)) { l = new List<Ref>(4); c.hash[key] = l; }
                    l.Add(r);
                }
                if (c.own && o.obstacles.Contains(Ecs.K(k.e))) continue;   // the game already sees this block
                int a0 = Math.Max(Fl(mn.x + Shrink), o.cminX), b0 = Math.Max(Fl(mn.y + Shrink), o.cminY), d0 = Math.Max(Fl(mn.z + Shrink), o.cminZ);
                int a1 = Math.Min(Fl(mx.x - Shrink), o.cmaxX), b1 = Math.Min(Fl(mx.y - Shrink), o.cmaxY), d1 = Math.Min(Fl(mx.z - Shrink), o.cmaxZ);
                for (int x = a0; x <= a1; x++) for (int y = b0; y <= b1; y++) for (int z = d0; z <= d1; z++) c.foot.Add(Key(x, y, z));
            }
        }

        /// <summary>The cells to test: where ignored geometry is, minus the cells the game's own obstacles cover.</summary>
        static void Candidates()
        {
            var want = new HashSet<long>();
            foreach (var k in own.foot) if (!own.covered.Contains(k)) want.Add(k);
            foreach (var c in others.Values) foreach (var k in c.foot) if (!own.covered.Contains(k)) want.Add(k);
            if (want.Count > MaxCells)
            {
                if (warnings++ < 5) ModLog.Warn("sound seal: " + want.Count + " cells to test, more than " + MaxCells + " — only the " + MaxCells + " nearest to you are used");
                var l = new List<long>(want); l.Sort((a, b) => Dist2(a).CompareTo(Dist2(b))); want = new HashSet<long>(l.GetRange(0, MaxCells));
            }
            var fresh = new List<long>();
            foreach (var k in want)
            {
                if (!cells.TryGetValue(k, out var cl)) { cl = new Cell { key = k }; cells[k] = cl; }
                if (cl.cand) continue;
                cl.cand = true; sidesDirty.Add(k);
                for (int a = 0; a < 3; a++)
                {
                    long p1 = PairKey(k, a), p2 = PairKey(Nb(k, a, -1), a);
                    if (!pairs.ContainsKey(p1)) { pairs[p1] = new Pair(); fresh.Add(p1); }
                    if (!pairs.ContainsKey(p2)) { pairs[p2] = new Pair(); fresh.Add(p2); }
                }
            }
            var drop = new List<long>(); bool uncand = false;
            foreach (var cl in cells.Values)
            {
                if (!cl.cand || want.Contains(cl.key)) continue;
                cl.cand = false; sidesDirty.Add(cl.key); uncand = true;
                if (Ecs.IsNull(cl.proxy)) drop.Add(cl.key);
            }
            foreach (var k in drop) cells.Remove(k);
            if (uncand)   // review: only when a cell stopped being tested (before: nearly every call, a walk over every pair)
            {
                // a pair stays only while one of its two cells is tested
                var stale = new List<long>();
                foreach (var pk in pairs.Keys) { long a = pk >> 2; int ax = (int)(pk & 3); if (!IsCand(a) && !IsCand(Nb(a, ax, +1))) stale.Add(pk); }
                foreach (var pk in stale) pairs.Remove(pk);
            }
            Enqueue(fresh);
        }
        static bool IsCand(long k) => cells.TryGetValue(k, out var c) && c.cand;
        static long Dist2(long k)
        {
            if (listenerKey == long.MinValue) return 0;
            Unkey(k, out int x, out int y, out int z); Unkey(listenerKey, out int a, out int b, out int c);
            long dx = x - a, dy = y - b, dz = z - c; return dx * dx + dy * dy + dz * dz;
        }
        static void Enqueue(List<long> keys)
        {
            keys.Sort((a, b) => Dist2(a >> 2).CompareTo(Dist2(b >> 2)));   // nearest to you first
            foreach (var pk in keys) if (pairs.TryGetValue(pk, out var p) && !p.queued) { p.queued = true; work.Enqueue(pk); }
        }
        /// <summary>Geometry changed in these cells: their pairs are tested again. open = geometry is gone or was rebuilt (what it closed opens
        /// now); hot = it moved (the closed pairs get the quick test first: a few rays that find an opening at once).</summary>
        static void MarkDirty(HashSet<long> dirty, bool open, bool hot)
        {
            var l = new List<long>(); var h = hot && !open ? new List<long>() : null;
            foreach (var v in dirty) regs.Remove(v);   // 0.31.34: their air is computed again
            foreach (var v in dirty)
                for (int a = 0; a < 3; a++)
                    for (int s = 0; s < 2; s++)
                    {
                        long pk = s == 0 ? PairKey(v, a) : PairKey(Nb(v, a, -1), a);
                        if (!pairs.TryGetValue(pk, out var p)) continue;
                        if (open && p.closed) { p.closed = false; SidesOf(pk); }
                        if (h != null && p.closed && !p.hot) { p.hot = true; h.Add(pk); }
                        l.Add(pk);
                    }
            if (h != null && h.Count > 0) { h.Sort((x, y) => Dist2(x >> 2).CompareTo(Dist2(y >> 2))); foreach (var pk in h) hotWork.Enqueue(pk); }
            Enqueue(l);
        }
        static void SidesOf(long pk) { long a = pk >> 2; sidesDirty.Add(a); sidesDirty.Add(Nb(a, (int)(pk & 3), +1)); }

        // ---------------------------------------------------------------- rays
        static void Work(EntityManager em)
        {
            if (work.Count == 0 && hotWork.Count == 0) return;
            long t0 = Stopwatch.GetTimestamp(); long lim = (long)(BudgetMs * Stopwatch.Frequency / 1000.0);
            // review: first the quick test of closed sides near a part that moved: the 9 rays most likely to find an opening (corners, centre,
            // edge middles). One passes = open now. All blocked = it stays closed until the full test (already queued) says otherwise
            while (hotWork.Count > 0 && Stopwatch.GetTimestamp() - t0 < lim)
            {
                long pk = hotWork.Dequeue();
                if (!pairs.TryGetValue(pk, out var p)) continue;
                p.hot = false;
                if (!p.closed) continue;
                if (RegionOpen(em, pk)) { p.closed = false; SidesOf(pk); quickOpened++; }
            }
            while (work.Count > 0 && Stopwatch.GetTimestamp() - t0 < lim)
            {
                long pk = work.Dequeue();
                if (!pairs.TryGetValue(pk, out var p)) continue;
                p.queued = false;
                bool cl = !RegionOpen(em, pk);
                tested++;
                if (cl != p.closed) { p.closed = cl; SidesOf(pk); }
            }
        }

        static readonly List<Ref> gather = new List<Ref>(256);
        /// <summary>The side between cell A (pk >> 2) and its + neighbour on axis (pk &amp; 3): closed only if all 49 rays are blocked
        /// (maxRays &lt; 49: only those first rays were tried; false = surely open).</summary>
        static bool Closed(EntityManager em, long pk, int maxRays)
        {
            long a = pk >> 2; int axis = (int)(pk & 3); long b = Nb(a, axis, +1);
            gather.Clear(); stamp++;
            Collect(own, a); Collect(own, b);
            foreach (var c in others.Values) { Collect(c, a); Collect(c, b); }
            if (gather.Count == 0) return false;
            Unkey(a, out int x, out int y, out int z);
            var o0 = new V((x + 0.5f) * 0.5f, (y + 0.5f) * 0.5f, (z + 0.5f) * 0.5f);
            V d = axis == 0 ? new V(1, 0, 0) : axis == 1 ? new V(0, 1, 0) : new V(0, 0, 1);
            V u = axis == 0 ? new V(0, 1, 0) : new V(1, 0, 0), w = axis == 2 ? new V(0, 1, 0) : new V(0, 0, 1);
            for (int n = 0; n < maxRays && n < NRays; n++)
            {
                int i = RayOrder[n];
                var o = SW.Add(o0, SW.Add(SW.Mul(u, Off[i / NOff]), SW.Mul(w, Off[i % NOff])));
                raysCast++;
                if (!Blocked(em, o, d, Len, SW.R)) return false;
            }
            return true;
        }
        static void Collect(Ctx c, long cell)
        {
            if (c == null || !c.hash.TryGetValue(cell, out var l)) return;
            foreach (var r in l) if (r.stamp != stamp) { r.stamp = stamp; gather.Add(r); }
        }

        /// <summary>One ray (player part space, length 0.5 m): does it enter a block, or cross a closed face of a frame, on its way?
        /// The air test's rules, on a short segment.</summary>
        static bool Blocked(EntityManager em, V o, V d, float len, float rad)
        {
            rayStamp++;
            for (int i = 0; i < gather.Count; i++)
            {
                var r = gather[i]; var c = r.c; V oo, dd;
                if (c.own) { oo = o; dd = d; }
                else { if (c.rs != rayStamp) { c.rs = rayStamp; c.ro = SW.Rot(c.qi, SW.Sub(o, c.p)); c.rd = SW.Rot(c.qi, d); } oo = c.ro; dd = c.rd; }
                var k = r.k;
                if (!SW.Slab(oo, dd, k.amin, k.amax, rad, out float ta, out float tb)) continue;
                if (tb > len) tb = len; if (ta > tb) continue;
                var ki = SW.Inv(k.rot); var oc = SW.Rot(ki, SW.Sub(oo, k.pos)); var dc = SW.Rot(ki, dd);
                var bk = k.solidRoot ? solidBlk : BlockOf(em, k.e);
                if (bk.kind == 1) continue;   // a frame with no closed face: the air goes through, so does the sound
                if (bk.kind == 0)
                {
                    if (SW.CoreDist(k, oc) <= k.cr + rad) continue;   // the ray starts inside it: only geometry it enters counts
                    if (Touch(k, oc, dc, ta, tb, k.cr + rad)) { Hoist(i); return true; }
                    continue;
                }
                // a frame with some closed faces: blocked only where the centre line crosses one of them (after its start, before its end)
                if (!bk.lt || !Touch(k, oc, dc, ta, tb, k.cr + rad)) continue;
                var qi = SW.Inv(bk.lq); var ol = SW.Rot(qi, SW.Sub(oo, bk.lp)); var dl = SW.Rot(qi, dd);
                for (int f = 0; f < bk.count; f++)
                    if ((bk.mask >> f & 1) != 0 && FaceCross(bk.faces, f, ol, dl, len)) { Hoist(i); return true; }
            }
            return false;
        }
        /// <summary>SealWatch.Touch with the ball's radius as a parameter (the same golden-section search of the closest approach).</summary>
        static bool Touch(SW.Child k, V oc, V dc, float ta, float tb, float lim)
        {
            const float g = 0.618034f;
            float a = ta, b = tb, x1 = b - g * (b - a), x2 = a + g * (b - a);
            float f1 = SW.CoreDist(k, SW.Add(oc, SW.Mul(dc, x1))), f2 = SW.CoreDist(k, SW.Add(oc, SW.Mul(dc, x2)));
            for (int it = 0; it < 30 && b - a > 1e-4f; it++)
            {
                if (f1 < f2) { b = x2; x2 = x1; f2 = f1; x1 = b - g * (b - a); f1 = SW.CoreDist(k, SW.Add(oc, SW.Mul(dc, x1))); }
                else { a = x1; x1 = x2; f1 = f2; x2 = a + g * (b - a); f2 = SW.CoreDist(k, SW.Add(oc, SW.Mul(dc, x2))); }
            }
            float best = Math.Min(Math.Min(f1, f2), Math.Min(SW.CoreDist(k, SW.Add(oc, SW.Mul(dc, ta))), SW.CoreDist(k, SW.Add(oc, SW.Mul(dc, tb)))));
            return best <= lim;
        }
        static void Hoist(int i) { if (i > 0) { var t = gather[i]; gather[i] = gather[0]; gather[0] = t; } }   // the blocker of this ray is tried first on the next

        // ---------------------------------------------------------------- 0.31.34: the air of a cell, not rays
        /// <summary>0.31.34 (Andre: "5 layers of rubber, checked with noclip, the thrusters still come in" — sound watch dump of 22/11):
        /// the 49 parallel rays from one cell centre to the next could not see a wall that runs ALONG the rays. Where the gate's leaf lies
        /// on (or within a few mm of) a cell's centre plane — common, the MicroFrame grid of 0.125 m falls on the centre planes of the
        /// 0.5 m cells — one column of cells put the leaf on one side of its centre and the next column on the other side (1 mm of tilt
        /// is enough), and the side between those two cells, tested with rays parallel to the leaf, was open on both halves: the outside
        /// air walked in along the leaf. Same with any stepped wall (layers of rubber, a leaf behind its frame).
        /// Now each cell is cut into 8x8x8 small cubes of 6.25 cm; a small cube is solid where the air test's ball (3.5 cm) touches a
        /// block, or where it lies on a closed face of a frame (MicroFrames included). The cell's air for the game's sound = the connected
        /// air that holds (most of) the cell's centre. A side is open only if that air of one cell touches that air of the other across
        /// the side. So a side opens only where real air really goes from one to the other: a wall that walls off the air seals, whatever
        /// side of the centre it lies on; a hole of about 10 cm or more is still found. Honest both ways: nothing closes without geometry.</summary>
        const int NS = 8; const float SS = 0.5f / NS;
        class Reg { public readonly ulong[] air = new ulong[NS]; public int n; }   // bit (i + 8 j) of word k = small cube (i, j, k) of the chosen air
        static readonly Dictionary<long, Reg> regs = new Dictionary<long, Reg>();
        static readonly ulong[] occ = new ulong[NS];
        static readonly int[] lab = new int[NS * NS * NS], stack = new int[NS * NS * NS];
        static int regsBuilt;
        static bool Air(ulong[] m, int i, int j, int k) => ((m[k] >> (i + NS * j)) & 1UL) != 0;

        static bool RegionOpen(EntityManager em, long pk)
        {
            long a = pk >> 2; int axis = (int)(pk & 3); long b = Nb(a, axis, +1);
            var ra = RegionOf(em, a); var rb = RegionOf(em, b);
            if (ra.n == 0 || rb.n == 0) return false;
            for (int u = 0; u < NS; u++)
                for (int w = 0; w < NS; w++)
                {
                    bool A, Bb;
                    if (axis == 0) { A = Air(ra.air, NS - 1, u, w); Bb = Air(rb.air, 0, u, w); }
                    else if (axis == 1) { A = Air(ra.air, u, NS - 1, w); Bb = Air(rb.air, u, 0, w); }
                    else { A = Air(ra.air, u, w, NS - 1); Bb = Air(rb.air, u, w, 0); }
                    if (A && Bb) return true;
                }
            return false;
        }

        static Reg RegionOf(EntityManager em, long key)
        {
            if (regs.TryGetValue(key, out var rg)) return rg;
            rg = new Reg(); regsBuilt++;
            for (int k = 0; k < NS; k++) occ[k] = ulong.MaxValue;   // 1 = air
            gather.Clear(); stamp++;
            Collect(own, key); foreach (var c in others.Values) Collect(c, key);
            if (gather.Count == 0) { for (int k = 0; k < NS; k++) rg.air[k] = ulong.MaxValue; rg.n = NS * NS * NS; regs[key] = rg; return rg; }   // nothing here: all air
            Unkey(key, out int cx, out int cy, out int cz);
            float ox = cx * 0.5f, oy = cy * 0.5f, oz = cz * 0.5f;
            float faceT = 0.55f * SS, grow = Math.Max(SW.R, faceT) + 0.002f;
            foreach (var r in gather)
            {
                var k = r.k; var c = r.c;
                var bk = k.solidRoot ? solidBlk : BlockOf(em, k.e);
                if (bk.kind == 1) continue;                    // a frame with no closed face: air through it
                if (bk.kind == 2 && !bk.lt) continue;
                V mn, mx; if (c.own) { mn = k.amin; mx = k.amax; } else Box(k.amin, k.amax, c.q, c.p, out mn, out mx);
                int i0 = Math.Max(0, (int)Math.Floor((mn.x - grow - ox) / SS)), i1 = Math.Min(NS - 1, (int)Math.Floor((mx.x + grow - ox) / SS));
                int j0 = Math.Max(0, (int)Math.Floor((mn.y - grow - oy) / SS)), j1 = Math.Min(NS - 1, (int)Math.Floor((mx.y + grow - oy) / SS));
                int k0 = Math.Max(0, (int)Math.Floor((mn.z - grow - oz) / SS)), k1 = Math.Min(NS - 1, (int)Math.Floor((mx.z + grow - oz) / SS));
                if (i0 > i1 || j0 > j1 || k0 > k1) continue;
                // review 0.31.34 (Andre: "fps a bit low, 40 s to seal"): the small cubes walk a straight grid, so every space they are
                // carried into is affine in (i, j, k): the origin and three steps are turned once per collider, not per small cube
                var ki = SW.Inv(k.rot); Q bqi = bk.kind == 2 ? SW.Inv(bk.lq) : default;
                V p0 = new V(ox + (i0 + 0.5f) * SS, oy + (j0 + 0.5f) * SS, oz + (k0 + 0.5f) * SS);
                V P0, Ex, Ey, Ez;
                if (c.own) { P0 = p0; Ex = new V(SS, 0, 0); Ey = new V(0, SS, 0); Ez = new V(0, 0, SS); }
                else { P0 = SW.Rot(c.qi, SW.Sub(p0, c.p)); Ex = SW.Rot(c.qi, new V(SS, 0, 0)); Ey = SW.Rot(c.qi, new V(0, SS, 0)); Ez = SW.Rot(c.qi, new V(0, 0, SS)); }
                V C0 = SW.Rot(ki, SW.Sub(P0, k.pos)), Cx = SW.Rot(ki, Ex), Cy = SW.Rot(ki, Ey), Cz = SW.Rot(ki, Ez);   // child space
                bool fastBox = k.box && bk.kind == 0;
                V B0 = default, Bx = default, By = default, Bz = default; float lim = k.cr + SW.R, lim2 = lim * lim;
                if (fastBox) { var bi = SW.Inv(k.bq); B0 = SW.Rot(bi, SW.Sub(C0, k.bc)); Bx = SW.Rot(bi, Cx); By = SW.Rot(bi, Cy); Bz = SW.Rot(bi, Cz); }
                V L0 = default, Lx = default, Ly = default, Lz = default;
                if (bk.kind == 2) { L0 = SW.Rot(bqi, SW.Sub(P0, bk.lp)); Lx = SW.Rot(bqi, Ex); Ly = SW.Rot(bqi, Ey); Lz = SW.Rot(bqi, Ez); }
                for (int kk = k0; kk <= k1; kk++)
                    for (int jj = j0; jj <= j1; jj++)
                        for (int ii = i0; ii <= i1; ii++)
                        {
                            ulong bit = 1UL << (ii + NS * jj);
                            if ((occ[kk] & bit) == 0) continue;
                            float a = ii - i0, b = jj - j0, e = kk - k0;
                            if (fastBox)
                            {
                                float qx = Math.Abs(B0.x + a * Bx.x + b * By.x + e * Bz.x) - k.bh.x, qy = Math.Abs(B0.y + a * Bx.y + b * By.y + e * Bz.y) - k.bh.y, qz = Math.Abs(B0.z + a * Bx.z + b * By.z + e * Bz.z) - k.bh.z;
                                if (qx < 0f) qx = 0f; if (qy < 0f) qy = 0f; if (qz < 0f) qz = 0f;
                                if (qx * qx + qy * qy + qz * qz <= lim2) occ[kk] &= ~bit;
                                continue;
                            }
                            var pc = new V(C0.x + a * Cx.x + b * Cy.x + e * Cz.x, C0.y + a * Cx.y + b * Cy.y + e * Cz.y, C0.z + a * Cx.z + b * Cy.z + e * Cz.z);
                            if (bk.kind == 0) { if (SW.CoreDist(k, pc) <= lim) occ[kk] &= ~bit; continue; }
                            if (SW.CoreDist(k, pc) > k.cr + faceT) continue;   // not even near this frame
                            var ol = new V(L0.x + a * Lx.x + b * Ly.x + e * Lz.x, L0.y + a * Lx.y + b * Ly.y + e * Lz.y, L0.z + a * Lx.z + b * Ly.z + e * Lz.z);
                            for (int f = 0; f < bk.count; f++)
                                if ((bk.mask >> f & 1) != 0 && NearFace(bk.faces, f, ol, faceT)) { occ[kk] &= ~bit; break; }
                        }
            }
            // connected air (6 neighbours), labelled in index order
            const int T = NS * NS * NS; int labels = 0;
            for (int t = 0; t < T; t++) lab[t] = 0;
            for (int t = 0; t < T; t++)
            {
                int i = t % NS, j = (t / NS) % NS, kz = t / (NS * NS);
                if (lab[t] != 0 || !Air(occ, i, j, kz)) continue;
                labels++; int sp = 0; stack[sp++] = t; lab[t] = labels;
                while (sp > 0)
                {
                    int v = stack[--sp]; int vi = v % NS, vj = (v / NS) % NS, vk = v / (NS * NS);
                    for (int d = 0; d < 6; d++)
                    {
                        int ni = vi + (d == 0 ? 1 : d == 1 ? -1 : 0), nj = vj + (d == 2 ? 1 : d == 3 ? -1 : 0), nk = vk + (d == 4 ? 1 : d == 5 ? -1 : 0);
                        if (ni < 0 || nj < 0 || nk < 0 || ni >= NS || nj >= NS || nk >= NS) continue;
                        int nt = ni + NS * (nj + NS * nk);
                        if (lab[nt] != 0 || !Air(occ, ni, nj, nk)) continue;
                        lab[nt] = labels; stack[sp++] = nt;
                    }
                }
            }
            int pick = 0;
            if (labels > 0)
            {
                // the air holding most of the 8 small cubes around the centre; none: the air nearest to the centre
                var cnt = new int[labels + 1]; int lo = NS / 2 - 1, hi = NS / 2;
                for (int kz = lo; kz <= hi; kz++) for (int j = lo; j <= hi; j++) for (int i = lo; i <= hi; i++) cnt[lab[i + NS * (j + NS * kz)]]++;
                for (int l = 1; l <= labels; l++) if (cnt[l] > 0 && (pick == 0 || cnt[l] > cnt[pick])) pick = l;
                if (pick == 0)
                {
                    float best = 1e30f; float m = (NS - 1) * 0.5f;
                    for (int t = 0; t < T; t++)
                    {
                        if (lab[t] == 0) continue;
                        float dx = t % NS - m, dy = (t / NS) % NS - m, dz = t / (NS * NS) - m, d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 < best) { best = d2; pick = lab[t]; }
                    }
                }
            }
            if (pick > 0)
                for (int t = 0; t < T; t++) if (lab[t] == pick) { rg.air[t / (NS * NS)] |= 1UL << (t % (NS * NS)); rg.n++; }
            regs[key] = rg;
            return rg;
        }

        /// <summary>Is point o (block space) within thick of the closed face i (its polygon with the game's 2 % tolerance)?</summary>
        static bool NearFace(IntPtr faces, int i, V o, float thick)
        {
            int nv = FaceVerts(faces, i); if (nv != 3 && nv != 4) return false;
            var v0 = FaceVert(faces, i, 0); var v1 = FaceVert(faces, i, 1); var v2 = FaceVert(faces, i, 2);
            var nrm = SW.Cross(SW.Sub(v1, v0), SW.Sub(v2, v0)); float ln = SW.Len(nrm); if (ln < 1e-9f) return false;
            nrm = SW.Mul(nrm, 1f / ln);
            float d = SW.Dot(SW.Sub(o, v0), nrm); if (Math.Abs(d) > thick) return false;
            var pr = SW.Sub(o, SW.Mul(nrm, d));
            if (InTri(pr, v0, v1, v2)) return true;
            return nv == 4 && InTri(pr, v0, v2, FaceVert(faces, i, 3));
        }
        static bool InTri(V p, V a, V b, V c)
        {
            var e1 = SW.Sub(b, a); var e2 = SW.Sub(c, a); var w = SW.Sub(p, a);
            float d00 = SW.Dot(e1, e1), d01 = SW.Dot(e1, e2), d11 = SW.Dot(e2, e2), d20 = SW.Dot(w, e1), d21 = SW.Dot(w, e2);
            float den = d00 * d11 - d01 * d01; if (Math.Abs(den) < 1e-12f) return false;
            float v = (d11 * d20 - d01 * d21) / den, u2 = (d00 * d21 - d01 * d20) / den;
            return v >= -SW.Tol && u2 >= -SW.Tol && v + u2 <= 1f + SW.Tol;
        }

        static unsafe V FaceVert(IntPtr faces, int i, int k) { var p = (float*)((byte*)faces + FrameFaces.FaceStride * i) + 3 * k; return new V(p[0], p[1], p[2]); }
        static unsafe int FaceVerts(IntPtr faces, int i) => *(int*)((byte*)faces + FrameFaces.FaceStride * i + 48);
        static bool FaceCross(IntPtr faces, int i, V o, V d, float len)
        {
            int nv = FaceVerts(faces, i); if (nv != 3 && nv != 4) return false;
            var v0 = FaceVert(faces, i, 0); var v2 = FaceVert(faces, i, 2);
            if (Tri(o, d, v0, FaceVert(faces, i, 1), v2, len)) return true;
            return nv == 4 && Tri(o, d, v0, v2, FaceVert(faces, i, 3), len);
        }
        /// <summary>The game's FaceData.RayCast (barycentric tolerance 0.02 — FrameFaces), limited to this segment: (Eps, len + Eps].
        /// review: the next segment starts at len and skips its first Eps, so a face between len and len + Eps belongs to this one (before:
        /// len + 0.1 mm, and a face 0.1-1 mm past a cell centre was counted by neither side).</summary>
        static bool Tri(V o, V d, V v0, V v1, V v2, float len)
        {
            var e1 = SW.Sub(v1, v0); var e2 = SW.Sub(v2, v0); var p = SW.Cross(d, e2); float det = SW.Dot(e1, p);
            if (Math.Abs(det) < 1e-8f) return false;
            float inv = 1f / det; var s = SW.Sub(o, v0); float u = SW.Dot(s, p) * inv;
            if (u < -SW.Tol || u > 1f + SW.Tol) return false;
            var q = SW.Cross(s, e1); float v = SW.Dot(q, d) * inv;
            if (v < -SW.Tol || u + v > 1f + SW.Tol) return false;
            float t = SW.Dot(e2, q) * inv;
            return t > Eps && t <= len + Eps;
        }

        // ---------------------------------------------------------------- per block (frame faces), read when a ray first reaches it
        class Blk { public byte kind; public IntPtr faces; public byte mask; public int count; public bool lt; public V lp; public Q lq; }   // kind 0 solid, 1 open frame, 2 frame with some closed faces
        static readonly Blk solidBlk = new Blk { kind = 0 };
        static readonly Dictionary<long, Blk> blocks = new Dictionary<long, Blk>();
        static unsafe Blk BlockOf(EntityManager em, Entity e)
        {
            long key = Ecs.K(e);
            if (blocks.TryGetValue(key, out var b)) return b;
            b = new Blk { kind = 0 };
            try
            {
                if (Ecs.HasC(em, e, FrameFaces.TiFrame))
                {
                    var f = (byte*)Ecs.RO(em, e, FrameFaces.TiFrame);
                    if (f != null)
                    {
                        IntPtr faces = *(IntPtr*)f; byte mask = f[12];
                        if (GameFields.Plausible((void*)faces))
                        {
                            int count = *(int*)((byte*)faces + FrameFaces.OffCount);
                            if (count > 0 && count <= 8)
                            {
                                b.faces = faces; b.mask = mask; b.count = count;
                                b.kind = mask == 0 ? (byte)1 : ((-1 << count) ^ mask) == -1 ? (byte)0 : (byte)2;   // the game: all faces closed = a solid block
                                if (b.kind == 2 && Ecs.Has(em, e, Ecs.tiLT))
                                {
                                    var lt = (LocalTransform*)Ecs.RO(em, e, Ecs.tiLT);
                                    b.lp = new V(lt->Position.x, lt->Position.y, lt->Position.z); b.lq = new Q { x = lt->Rotation.value.x, y = lt->Rotation.value.y, z = lt->Rotation.value.z, w = lt->Rotation.value.w }; b.lt = true;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception) { b = new Blk { kind = 0 }; }
            blocks[key] = b;
            return b;
        }

        // ---------------------------------------------------------------- the game's side: the cells as entities
        static bool PairClosed(long pk)
        {
            if (!(pairs.TryGetValue(pk, out var p) && p.closed)) return false;
            if (listenerOpen && listenerKey != long.MinValue) { long a = pk >> 2; if (a == listenerKey || Nb(a, (int)(pk & 3), +1) == listenerKey) return false; }
            return true;
        }
        static byte Sides(Cell c)
        {
            if (!c.cand) return 0x3F;
            int s = 0;
            for (int d = 0; d < 6; d++)
            {
                int axis = d >> 1; long pk = (d & 1) == 0 ? PairKey(c.key, axis) : PairKey(Nb(c.key, axis, -1), axis);
                if (!PairClosed(pk)) s |= 1 << d;
            }
            return (byte)s;
        }
        static void DirtyAround(long k)
        {
            if (k == long.MinValue) return;
            sidesDirty.Add(k);
            for (int a = 0; a < 3; a++) { sidesDirty.Add(Nb(k, a, +1)); sidesDirty.Add(Nb(k, a, -1)); }
        }

        /// <summary>review: the six sides around the listener's cell are left open (on both cells of each side) when
        /// (a) all six are closed: the game would hear nothing at all (every sound 92 % muffled). Before, only the listener's own cell was
        ///     opened, but a side stays shut while the neighbour's half is closed, so it did not help;
        /// (b) geometry lies between the listener and its cell's centre: the rays were cast from the centre, so the listener is really on the
        ///     other side of that plate (standing within 25 cm of a thin wall, e.g. outside a MicroFrame hull) and must not be muffled as if
        ///     he were behind it. One thin ray (5 mm) from the listener to the centre, same rules (only geometry it enters).
        /// Open is the honest direction: never more closed than the geometry proves.</summary>
        static void Listener(EntityManager em, V lp)
        {
            bool open = false, split = false;
            if (listenerKey != long.MinValue && pairs.Count > 0)
            {
                int closed = 0;
                for (int a = 0; a < 3; a++)
                {
                    if (pairs.TryGetValue(PairKey(listenerKey, a), out var p1) && p1.closed) closed++;
                    if (pairs.TryGetValue(PairKey(Nb(listenerKey, a, -1), a), out var p2) && p2.closed) closed++;
                }
                if (closed == 6) open = true;
                else if (closed > 0)
                {
                    Unkey(listenerKey, out int x, out int y, out int z);
                    var cc = new V((x + 0.5f) * 0.5f, (y + 0.5f) * 0.5f, (z + 0.5f) * 0.5f);
                    var dv = SW.Sub(cc, lp); float len = SW.Len(dv);
                    if (len > 0.01f && len < 1f)
                    {
                        gather.Clear(); stamp++;
                        Collect(own, listenerKey); foreach (var c in others.Values) Collect(c, listenerKey);
                        if (gather.Count > 0 && Blocked(em, lp, SW.Mul(dv, 1f / len), len, 0.005f)) { open = true; split = true; }
                    }
                }
            }
            if (open != listenerOpen) { listenerOpen = open; DirtyAround(listenerKey); if (open) listenerSaved++; listenerSplit = split; }
        }
        static bool listenerSplit;

        static unsafe void Apply(EntityManager em)
        {
            if (sidesDirty.Count == 0) return;
            bool rebuild = false; float now = UnityEngine.Time.unscaledTime;
            foreach (var k in sidesDirty)
            {
                if (!cells.TryGetValue(k, out var c)) continue;
                byte s = Sides(c);
                if (!Ecs.IsNull(c.proxy))
                {
                    if (s == c.written) continue;
                    if (!Ecs.HasC(em, c.proxy, tiMod)) { mine.Remove(Ecs.K(c.proxy)); c.proxy = Entity.Null; proxies--; c.written = -1; Unborn(c); rebuild = true; writes++; if (s != 0x3F) pending.Add(k); continue; }
                    *(byte*)Ecs.RO(em, c.proxy, tiMod) = s;   // through the RO pointer on purpose (like FrameFaces): no sync point, the game's
                    c.written = s;                             // dynamic-obstacle job reads the byte every frame
                    c.tChange = now; tLastWrite = now; writes++;
                    Unborn(c);   // 0.31.36: its sides changed after it was made: an ordinary moving cell from now on
                    if (!c.dyn)
                    {
                        c.changed = true;
                        // 0.31.35 (sound_cells_settle): the moving tag in this same frame and no rebuild of the whole grid — the game's next
                        // moving-cell update writes the new sides. An all-open cell stays as a moving cell with 0x3F (an empty voxel)
                        if (Settle) toDyn.Add(k);
                        else
                        {
                            // review: a static cell is read by the game only when it rebuilds the grid: ask for one now. It becomes dynamic on the next
                            // batch (it changed once, it may change again), or goes away if it is all open
                            rebuild = true;
                            if (s == 0x3F) Kill(em, c); else toDyn.Add(k);
                        }
                    }
                }
                else if (s != 0x3F) pending.Add(k);
            }
            sidesDirty.Clear();
            if (Settle && toDyn.Count > 0) MakeMoving(em);
            if (rebuild) Rebuild(em);
        }

        /// <summary>The still cells in toDyn get the game's moving tag (their sides are already written).</summary>
        static void MakeMoving(EntityManager em)
        {
            long t0 = Stopwatch.GetTimestamp(); float now = UnityEngine.Time.unscaledTime; int n = 0;
            foreach (var k in toDyn)
                if (cells.TryGetValue(k, out var c) && !c.dyn && !Ecs.IsNull(c.proxy) && Ecs.HasC(em, c.proxy, tiMod))
                {
                    try
                    {
                        Ecs.Add(em, c.proxy, ComponentType.ReadWrite(tiDyn)); c.dyn = true; c.tChange = now; n++;
                        if (c.settled) { c.settled = false; c.quiet = Math.Min(QuietMax, c.quiet * 2f); }   // it had to move again: it waits twice as long next time
                    }
                    catch (Exception ex) { if (warnings++ < 5) ModLog.Warn("sound seal: " + ex.Message); }
                }
            toDyn.Clear();
            if (n > 0) { nToDyn += n; nStruct += n; structMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency; }
        }

        /// <summary>Every 0.3 s: the cells that got a closed side become entities. 0.31.36, [Air] sound_cells_settle on: born the moving
        /// kind where the game's grid already holds the voxel (no rebuild: the game's next moving-cell update writes the sides); the
        /// others as before, with one rebuild of the game's grid for the whole batch.</summary>
        static unsafe void Create(EntityManager em)
        {
            int made = 0, dropped = 0, born = 0; long t0 = Stopwatch.GetTimestamp(); float now = UnityEngine.Time.unscaledTime;
            if (toDyn.Count > 0) MakeMoving(em);   // review: static cells that changed once become dynamic (adding the game's tag; their sides are already in the grid)
            if (pending.Count > 0)
            {
                bool grid = Settle && GridRead(em);
                foreach (var k in pending)
                {
                    if (!cells.TryGetValue(k, out var c) || !Ecs.IsNull(c.proxy)) continue;
                    byte s = Sides(c); if (s == 0x3F) continue;
                    Unkey(k, out int x, out int y, out int z);
                    // 0.31.36: inside the game's grid = born the moving kind, seen by the game at its next update with no rebuild
                    bool inGrid = grid && GridHolds(x, y, z);
                    // a part on a joint reaches it, or it already changed once. 0.31.35 (sound_cells_settle): a cell next to a part on joints
                    // starts as a still cell too — the first time its sides change it becomes moving at once, with no rebuild (Apply)
                    bool asBefore = c.changed || Near(k, listenerKey) || (!Settle && NearOthers(k)), dyn = inGrid || asBefore;
                    var e = Ecs.Create(em, dyn ? kinds : kindsStatic);
                    var o = (int*)em.GetComponentDataRawRW(e, tiObs); o[0] = x; o[1] = y; o[2] = z; o[3] = x; o[4] = y; o[5] = z;
                    *(byte*)em.GetComponentDataRawRW(e, tiMod) = s;
                    c.proxy = e; c.written = s; c.dyn = dyn; c.tChange = now; c.settled = false; proxies++; made++; mine.Add(Ecs.K(e));
                    Unborn(c);
                    if (inGrid) { born++; if (!asBefore) { c.born = true; bornLive++; } }   // moving-kind only to spare the rebuild: still again in the next batch
                }
                pending.Clear();
            }
            // cells the geometry left (a gate swept through them) stay as open entities; too many of them: away, in one batch
            int dead = 0; foreach (var c in cells.Values) if (!c.cand && !Ecs.IsNull(c.proxy)) dead++;
            if (dead > MaxDead)
            {
                var gone = new List<long>();
                foreach (var c in cells.Values) if (!c.cand && !Ecs.IsNull(c.proxy)) { Kill(em, c); gone.Add(c.key); dropped++; }
                foreach (var k in gone) cells.Remove(k);
            }
            if (made > 0 || dropped > 0) { nMade += made; nBorn += born; nStruct += made + dropped; writes++; tLastWrite = now; structMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency; }
            int settled = Settle ? SettleQuiet(em, now) : 0;
            int outside = made - born;   // made the old way: the game reads them only when it rebuilds its grid
            if (Settle && outside > 0) nOutside += outside;
            if (born > 0 && bornSaid++ < 2) ModLog.Info("sound seal: " + born + " new cell(s) given to the game as moving-kind cells — no rebuild of the game's sound grid for them (the game writes their sides at its next update; they become still cells in the next batch, 2 s or more after they were made)"
                                                        + (outside > 0 ? "; " + outside + " more lie on the outer layer of the game's grid or outside it: one rebuild for those, as before" : "") + " ([Air] sound_cells_settle)");
            if (outside > 0 || dropped > 0 || settled > 0) Rebuild(em);
        }
        static int bornSaid;

        /// <summary>0.31.36: the box of the grid the game has now, read from both of its grid headers (SoundSystemSingleton._voxelsData0/1:
        /// part +64, _voxelsMin +88, _voxelsMax +100). The newer header is always one of the two (a rebuild writes the one being filled;
        /// without a rebuild the game copies the published one over it), so a voxel inside BOTH boxes is inside the grid the game's
        /// moving-cell jobs use next, whatever was rebuilt meanwhile. False (= make the cells the old way, with a rebuild) when a header is
        /// missing, belongs to another part, holds an empty grid, or a rebuild is already waiting.</summary>
        static int gx0, gy0, gz0, gx1, gy1, gz1;
        static unsafe bool GridRead(EntityManager em)
        {
            try
            {
                byte* p = Singleton(em); if (p == null || p[144] != 0) return false;
                gx0 = gy0 = gz0 = int.MinValue; gx1 = gy1 = gz1 = int.MaxValue;
                for (int i = 0; i < 2; i++)
                {
                    byte* vd = *(byte**)(p + 128 + 8 * i); if (vd == null) return false;
                    var owner = *(Entity*)(vd + 64); if (owner.Index != curPart.Index || owner.Version != curPart.Version) return false;
                    int* mn = (int*)(vd + 88); int* mx = (int*)(vd + 100);
                    if (mn[0] > mx[0] || mn[1] > mx[1] || mn[2] > mx[2]) return false;
                    gx0 = Math.Max(gx0, mn[0]); gy0 = Math.Max(gy0, mn[1]); gz0 = Math.Max(gz0, mn[2]);
                    gx1 = Math.Min(gx1, mx[0]); gy1 = Math.Min(gy1, mx[1]); gz1 = Math.Min(gz1, mx[2]);
                }
                return true;
            }
            catch (Exception) { return false; }
        }
        /// <summary>Strictly inside: not on the grid's outer layer (the one layer of empty voxels the game keeps around every obstacle; a
        /// cell there makes the next rebuild grow the grid, so it is made the old way, with the rebuild).</summary>
        static bool GridHolds(int x, int y, int z) => x > gx0 && x < gx1 && y > gy0 && y < gy1 && z > gz0 && z < gz1;

        /// <summary>0.31.35 ([Air] sound_cells_settle): the moving cells that have been quiet become still again, in one batch with one
        /// rebuild of the game's grid. Returns how many entities changed (0 = no rebuild needed).</summary>
        static readonly List<Cell> quietList = new List<Cell>();
        static int SettleQuiet(EntityManager em, float now)
        {
            // 0.31.36: while many cells wait that are moving-kind only because they were born so, the next batch may come sooner
            float every = bornLive >= BornBatch ? BornEvery : SettleEvery;
            if (now - tSettle < every) return 0;
            quietList.Clear(); int dyn = 0;
            foreach (var c in cells.Values)
            {
                if (Ecs.IsNull(c.proxy) || !c.dyn) continue;
                dyn++;
                if (now - c.tChange < (c.born ? BornQuiet : c.quiet) || Near(c.key, listenerKey) || Busy(c.key)) continue;
                quietList.Add(c);
            }
            // not worth a rebuild of the whole grid yet: fewer than 32 quiet cells count only when they are a quarter of the moving ones, and then
            // at most every 30 s. Look again in 2 s
            if (quietList.Count == 0 || (quietList.Count < SettleMin && (quietList.Count * 4 < dyn || now - tBatch < 30f))) { tSettle = now - every + 2f; return 0; }
            tSettle = now; tBatch = now;
            if (Compare) CompareBegin(em, now);
            long t0 = Stopwatch.GetTimestamp(); int n = 0, open = 0;
            foreach (var c in quietList)
            {
                try
                {
                    if (!Ecs.HasC(em, c.proxy, tiMod)) continue;   // gone by itself: Apply deals with it when its sides change
                    if (c.written == 0x3F || c.written < 0)
                    {
                        // all open (the geometry left, or every side opened): an entity that only says "empty" — away with it. A side that
                        // closes later makes a new one (Apply -> pending)
                        Kill(em, c); nKilled++; open++; n++;
                        if (!c.cand) cells.Remove(c.key);
                        continue;
                    }
                    // (a born cell that never changed is exactly the still cell 0.31.35 made: it does not count as one that settled before)
                    Ecs.Remove(em, c.proxy, ComponentType.ReadWrite(tiDyn)); c.dyn = false; c.settled = !c.born; Unborn(c); n++;
                }
                catch (Exception ex) { if (warnings++ < 5) ModLog.Warn("sound seal: " + ex.Message); }
            }
            quietList.Clear();
            if (n > 0)
            {
                nToStill += n - open; nBatches++; nStruct += n; structMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (settleSaid++ < 3) ModLog.Info("sound seal: " + (n - open) + " moving-kind cell(s) quiet for 5 s or more (2 s for one that never changed since it was made) became still again" + (open > 0 ? ", " + open + " all-open one(s) removed" : "") + " (" + (dyn - n)
                                                  + " stay moving: next to you, changed lately or waiting for rays) — one rebuild of the game's sound grid; the sides given to the game are unchanged ([Air] sound_cells_settle)");
            }
            return n;
        }
        static int settleSaid; static float tBatch = -99f;
        /// <summary>Is one of the cell's six sides waiting for its rays?</summary>
        static bool Busy(long k)
        {
            for (int d = 0; d < 6; d++)
            {
                int axis = d >> 1; long pk = (d & 1) == 0 ? PairKey(k, axis) : PairKey(Nb(k, axis, -1), axis);
                if (pairs.TryGetValue(pk, out var p) && (p.queued || p.hot)) return true;
            }
            return false;
        }

        /// <summary>Does another part's geometry reach this cell or one of its 6 neighbours (= can its sides change when that part moves)?</summary>
        static bool NearOthers(long k)
        {
            if (others.Count == 0) return false;
            foreach (var c in others.Values)
            {
                if (c.hash.ContainsKey(k)) return true;
                for (int a = 0; a < 3; a++) if (c.hash.ContainsKey(Nb(k, a, +1)) || c.hash.ContainsKey(Nb(k, a, -1))) return true;
            }
            return false;
        }
        static bool Near(long k, long l)
        {
            if (l == long.MinValue) return false;
            Unkey(k, out int x, out int y, out int z); Unkey(l, out int a, out int b, out int c);
            return Math.Abs(x - a) <= 1 && Math.Abs(y - b) <= 1 && Math.Abs(z - c) <= 1;
        }

        static readonly HashSet<long> mine = new HashSet<long>();
        static void Kill(EntityManager em, Cell c)
        {
            try { if (Ecs.Alive(em, c.proxy)) Ecs.Destroy(em, c.proxy); } catch (Exception) { }
            mine.Remove(Ecs.K(c.proxy)); c.proxy = Entity.Null; c.written = -1; c.dyn = false; c.settled = false; Unborn(c); proxies--;
        }

        /// <summary>SoundSystemSingleton._voxelsRebuildObstacles (+144): the game rebuilds the grid (when no distance job is running) and
        /// clears it. Written through the RO pointer like the game's own main-thread writers; no job writes this field.</summary>
        static unsafe void Rebuild(EntityManager em)
        {
            var p = Singleton(em); if (p != null) { p[144] = 1; nRebuilds++; }   // 0.31.35: the singleton's address is kept (it was a query, a list and a read that waits for the game's sound jobs, every time)
        }

        /// <summary>0.31.35: the address of the game's SoundSystemSingleton, kept while the world's structure does not change (one cheap call
        /// to know). Reading it this way waits for no job: only fields the main thread writes are read or written through it (+120, +124,
        /// +144, and the published buffer).</summary>
        static IntPtr sssPtr; static int sssOrder = int.MinValue;
        internal static unsafe byte* Singleton(EntityManager em)
        {
            if (!ok) return null;
            int v = em.EntityOrderVersion;
            if (sssPtr != IntPtr.Zero && v == sssOrder) return (byte*)sssPtr;
            sssPtr = IntPtr.Zero; sssOrder = v;
            foreach (var s in Ecs.QueryList(qSSS)) { sssPtr = (IntPtr)em.GetComponentDataRawRO(s, tiSSS); break; }
            return (byte*)sssPtr;
        }

        // ---------------------------------------------------------------- 0.31.35 [Debug] sound_compare
        // Around a settle batch: the game's published distance grid (ushort per voxel, SoundSystemSingleton +88/+96 by _voxelsBufferIndex & 1)
        // is hashed right before the batch and again after the rebuild it asked for has run and two more grids were published. If no side
        // was written and the listener stayed in his voxel meanwhile, the two must be identical.
        static int cmpStage, cmpLen, cmpIdx, cmpLx, cmpLy, cmpLz; static ulong cmpHash; static long cmpWrites; static float cmpAt; static bool cmpFresh;
        static unsafe bool GridHash(EntityManager em, out ulong h, out int len, out int idx, out int lx, out int ly, out int lz)
        {
            h = 1469598103934665603UL; len = 0; idx = 0; lx = ly = lz = 0;
            byte* p = Singleton(em); if (p == null) return false;
            idx = *(int*)(p + 124); int bi = idx & 1;
            byte* buf = *(byte**)(p + 88 + 8 * bi); byte* vd = *(byte**)(p + 128 + 8 * bi); if (buf == null || vd == null) return false;
            ushort* d = *(ushort**)buf; len = *(int*)(buf + 8); if (d == null || len <= 0 || len > 60000000) return false;
            for (int i = 0; i < len; i++) { h ^= d[i]; h *= 1099511628211UL; }
            int* li = (int*)(vd + 76); lx = li[0]; ly = li[1]; lz = li[2];
            return true;
        }
        static void CompareBegin(EntityManager em, float now)
        {
            try
            {
                cmpStage = 0;
                if (!GridHash(em, out cmpHash, out cmpLen, out cmpIdx, out cmpLx, out cmpLy, out cmpLz)) { ModLog.Info("sound compare: the game has no finished sound grid yet — nothing to compare for this batch"); return; }
                cmpFresh = now - tLastWrite > 2f;   // a side written less than 2 s ago may not be in the published grid yet
                cmpWrites = writes; cmpAt = now; cmpStage = 1;
            }
            catch (Exception ex) { cmpStage = 0; ModLog.Warn("sound compare: " + ex.Message); }
        }
        static unsafe void CompareStep(EntityManager em)
        {
            try
            {
                float now = UnityEngine.Time.unscaledTime;
                byte* p = Singleton(em); if (p == null) { cmpStage = 0; return; }
                if (now - cmpAt > 15f) { cmpStage = 0; ModLog.Info("sound compare: the game did not publish a new grid within 15 s of the batch — not compared"); return; }
                if (cmpStage == 1) { if (p[144] != 0) return; cmpIdx = *(int*)(p + 124); cmpStage = 2; return; }   // the rebuild has run
                if (*(int*)(p + 124) - cmpIdx < 2) return;   // two grids published since: the last one was computed wholly after the rebuild
                cmpStage = 0;
                if (!GridHash(em, out ulong h, out int len, out _, out int lx, out int ly, out int lz)) { ModLog.Info("sound compare: no finished grid after the batch — not compared"); return; }
                if (writes != cmpWrites || !cmpFresh) ModLog.Info("sound compare: NOT COMPARABLE this time — a cell's sides were written " + (cmpFresh ? "between the two grids" : "less than 2 s before the batch") + " (stand still with nothing moving and wait for the next batch)");
                else if (lx != cmpLx || ly != cmpLy || lz != cmpLz) ModLog.Info("sound compare: NOT COMPARABLE this time — you moved to another cell between the two grids (stand still and wait for the next batch)");
                else if (len == cmpLen && h == cmpHash) ModLog.Info("sound compare: SAME — the game's sound grid (" + len + " cells, hash " + h.ToString("X16") + ") is identical before and after the moving cells became still");
                else ModLog.Warn("sound compare: DIFFERENT — the game's sound grid changed across the batch (" + cmpLen + " cells, hash " + cmpHash.ToString("X16") + " -> " + len + " cells, hash " + h.ToString("X16")
                                 + "). If nothing of the game moved either (a door, a hatch), turn [Air] sound_cells_settle off and send this log");
            }
            catch (Exception ex) { cmpStage = 0; ModLog.Warn("sound compare: " + ex.Message); }
        }

        /// <summary>0.31.35: the mod's side of the "performance+:" sound line; resets the counts.</summary>
        internal static string PerfLine(EntityManager em)
        {
            try
            {
                int still = 0, moving = 0, open = 0;
                foreach (var c in cells.Values) { if (Ecs.IsNull(c.proxy)) continue; if (c.dyn) { moving++; if (c.written == 0x3F) open++; } else still++; }
                int allDyn = -1; try { if (ok) allDyn = qDynObs.CalculateEntityCount(); } catch (Exception) { }
                string s = " | the mod's sound cells: " + still + " still, " + moving + " moving-kind (" + open + " of them all open)" + (allDyn >= 0 ? ", the game's own moving sound obstacles: " + Math.Max(0, allDyn - moving) : "")
                         + "; since the last line: " + nRebuilds + " rebuild(s) of the grid asked by the mod, " + nMade + " cell(s) made (" + nBorn + " born moving-kind with no rebuild, " + nOutside + " the old way with a rebuild: on the edge of the game's grid, outside it, or a rebuild was already waiting), " + nKilled + " removed, " + nToDyn + " still -> moving, " + nToStill + " moving -> still in " + nBatches
                         + " batch(es), " + nStruct + " structural change(s) taking " + structMs.ToString("F1") + " ms in all" + (Settle ? "" : " ([Air] sound_cells_settle is OFF)");
                nRebuilds = 0; nMade = 0; nKilled = 0; nToDyn = 0; nToStill = 0; nBatches = 0; nStruct = 0; structMs = 0; nBorn = 0; nOutside = 0;
                return s;
            }
            catch (Exception) { return ""; }
        }

        static void ResetCells(EntityManager em, string why)
        {
            int n = 0;
            foreach (var c in cells.Values) if (!Ecs.IsNull(c.proxy)) { Kill(em, c); n++; }
            cells.Clear(); pairs.Clear(); regs.Clear(); work.Clear(); hotWork.Clear(); sidesDirty.Clear(); pending.Clear(); toDyn.Clear(); blocks.Clear();
            if (n > 0) { Rebuild(em); ModLog.Info("sound seal: " + n + " cell(s) removed (" + why + ") — testing again"); }
            proxies = 0; bornLive = 0; lastLog = ""; listenerOpen = false; cmpStage = 0; writes++;
        }

        static void Drop(EntityManager em, string why, bool forgetOwn)
        {
            int n = 0;
            foreach (var c in cells.Values) if (!Ecs.IsNull(c.proxy)) { Kill(em, c); n++; }
            cells.Clear(); pairs.Clear(); regs.Clear(); work.Clear(); hotWork.Clear(); sidesDirty.Clear(); pending.Clear(); toDyn.Clear(); blocks.Clear(); others.Clear();
            own = null; active = false; proxies = 0; bornLive = 0; listenerKey = long.MinValue; listenerOpen = false; lastLog = ""; cmpStage = 0; writes++;
            if (forgetOwn) { ownCache.Clear(); curPart = Entity.Null; }
            if (n > 0) { Rebuild(em); ModLog.Info("sound seal: " + n + " cell(s) removed — " + why + " (the game's own sound)"); }
            sweepNeeded = true;
        }

        /// <summary>Entities that look like the mod's cells (the sound components only: obstacle + modular [+ dynamic], no Parent, no SCPrefab,
        /// no LocalTransform) but are not in the live list: left by an unload or an error — destroyed (they would enter every grid, the garage's too).</summary>
        static void Sweep(EntityManager em)
        {
            sweepNeeded = false;
            int n = 0;
            foreach (var e in Ecs.QueryList(qProxyLike))
            {
                if (mine.Contains(Ecs.K(e))) continue;
                if (Ecs.Has(em, e, Ecs.tiParent) || Ecs.Has(em, e, Ecs.tiPrefab) || Ecs.Has(em, e, Ecs.tiLT)) continue;
                int types = -1; try { types = em.GetComponentTypes(e, Unity.Collections.Allocator.Temp).Length; } catch (Exception) { }
                if (types < 2 || types > 4) continue;   // ours: 2 (static) or 3 (dynamic) (+ Simulate, which Entities may add to every entity)
                try { Ecs.Destroy(em, e); n++; } catch (Exception) { }
            }
            if (n > 0) { Rebuild(em); ModLog.Info("sound seal: " + n + " leftover sound cell(s) of an earlier session removed"); }
        }

        /// <summary>World unload: forget everything without touching the ECS (the next run sweeps whatever survived).</summary>
        public static void Forget()
        {
            cells.Clear(); pairs.Clear(); regs.Clear(); work.Clear(); hotWork.Clear(); sidesDirty.Clear(); pending.Clear(); toDyn.Clear(); blocks.Clear(); others.Clear(); ownCache.Clear(); mine.Clear(); posedAt.Clear();
            own = null; active = false; proxies = 0; bornLive = 0; curPart = Entity.Null; listenerKey = long.MinValue; listenerOpen = false; lastLog = ""; sweepNeeded = true; tRefresh = 99f;
            sssPtr = IntPtr.Zero; sssOrder = int.MinValue; cmpStage = 0; quietList.Clear();
        }

        // ---------------------------------------------------------------- logs
        static void Counts(out int withEnt, out int closedCells, out int closedSides)
        {
            withEnt = 0; closedCells = 0; closedSides = 0;
            foreach (var c in cells.Values)
            {
                if (Ecs.IsNull(c.proxy)) continue; withEnt++;
                int s = c.written < 0 ? 0x3F : c.written, shut = 6 - Pop(s);
                if (shut > 0) { closedCells++; closedSides += shut; }
            }
        }
        static int Pop(int s) { int n = 0; for (int i = 0; i < 6; i++) n += (s >> i) & 1; return n; }

        /// <summary>One line when the result settles on something new (at most every 3 s).</summary>
        static void Report()
        {
            if (work.Count > 0 || hotWork.Count > 0 || pending.Count > 0) return;
            Counts(out int ent, out int cc, out int cs);
            int cand = 0, dyn = 0; foreach (var c in cells.Values) { if (c.cand) cand++; if (c.dyn && !Ecs.IsNull(c.proxy)) dyn++; }
            string sig = curPart.Index + "|" + ent + "|" + cc + "|" + cs;
            float now = UnityEngine.Time.unscaledTime;
            if (sig == lastLog || now - tLog < 3f) return;
            lastLog = sig; tLog = now;
            ModLog.Info("sound seal: part " + curPart.Index + " — " + cc + " cell(s) of 0.5 m with closed side(s), " + cs + " closed side(s) in all, given to the game's sound (" + ent + " cell entities, "
                        + dyn + " of them moving-type; " + cand + " cell(s) tested, " + others.Count + " other part(s) reaching in; " + tested + " side(s) tested (air of " + regsBuilt + " cell(s) worked out) since the last line"
                        + (quickOpened > 0 ? "; " + quickOpened + " side(s) opened at once by a part that moved" : "")
                        + (listenerSaved > 0 ? "; the sides around you were left open " + listenerSaved + " time(s) (you were boxed in, or a wall ran between you and your cell's centre)" : "") + "; "
                        + (spentMs / Math.Max(1, spentFrames)).ToString("F3") + " ms per frame)");
            tested = 0; raysCast = 0; regsBuilt = 0; listenerSaved = 0; quickOpened = 0; spentMs = 0; spentFrames = 0;
        }

        /// <summary>[Debug] sound_watch, every 2 s: the cells, the listener, and the occlusion the game computes now for the loudest thruster
        /// (SoundSystemSingleton.GetSoundOcclusion redone from the published buffer: GA 0xEBDD00 in dec/ga, 18/11 numbering).</summary>
        static unsafe void WatchLine(EntityManager em, Entity part)
        {
            var sb = new StringBuilder("sound watch: part ").Append(part.Index);
            Counts(out int ent, out int cc, out int cs);
            sb.Append(" — my cells ").Append(ent).Append(" (").Append(cc).Append(" with closed sides, ").Append(cs).Append(" closed sides), ").Append(work.Count + hotWork.Count).Append(" side(s) waiting for rays");
            if (listenerKey != long.MinValue)
            {
                Unkey(listenerKey, out int lx, out int ly, out int lz);
                sb.Append("; you are in cell (").Append(lx).Append(",").Append(ly).Append(",").Append(lz).Append(")");
                if (cells.TryGetValue(listenerKey, out var lc) && !Ecs.IsNull(lc.proxy)) sb.Append(" = one of my cells, open sides ").Append(SideNames(lc.written));
                else if (own != null && own.covered.Contains(listenerKey)) sb.Append(" = a cell of a game block (!)");
                if (listenerOpen) sb.Append(listenerSplit ? " [a wall runs between you and the centre of your cell: its sides are left open]" : " [boxed in on all 6 sides: its sides are left open]");
            }
            byte* ss = null; foreach (var s in Ecs.QueryList(qSSS)) { ss = (byte*)Ecs.RO(em, s, tiSSS); break; }
            if (ss == null) { ModLog.Info(sb.Append("; no SoundSystemSingleton").ToString()); return; }
            int bi = *(int*)(ss + 124) & 1;
            byte* buf = *(byte**)(ss + 88 + 8 * bi); byte* vd = *(byte**)(ss + 128 + 8 * bi);
            if (buf == null || vd == null || *(int*)(buf + 8) == 0) { ModLog.Info(sb.Append("; the game's sound grid is EMPTY (nothing is muffled at all)").ToString()); return; }
            int* mn = (int*)(vd + 88); int* mx = (int*)(vd + 100); int* li = (int*)(vd + 76);
            int sx = mx[0] - mn[0] + 1, sy = mx[1] - mn[1] + 1, sz = mx[2] - mn[2] + 1;
            var owner = *(Entity*)(vd + 64);
            sb.Append("; game grid ").Append(sx).Append("x").Append(sy).Append("x").Append(sz).Append(" from (").Append(mn[0]).Append(",").Append(mn[1]).Append(",").Append(mn[2]).Append(") of part ").Append(owner.Index)
              .Append(", listener voxel (").Append(li[0] + mn[0]).Append(",").Append(li[1] + mn[1]).Append(",").Append(li[2] + mn[2]).Append(")");
            if (!thrOk) { ModLog.Info(sb.ToString()); return; }
            Entity best = Entity.Null; float vol = -1f; int playing = 0;
            foreach (var t in Ecs.QueryList(qThr))
            {
                var th = (byte*)Ecs.RO(em, t, tiThr); if (th == null) continue;
                float v = (float)Math.Sqrt(Math.Max(0f, Math.Min(1f, Math.Abs(*(float*)(th + 24)) * *(float*)(th + 28))));   // Sound_Thruster's volume
                if (v > 0.001f) playing++;
                if (v > vol) { vol = v; best = t; }
            }
            if (Ecs.IsNull(best) || vol <= 0.001f) { ModLog.Info(sb.Append("; no thruster is making sound now").ToString()); return; }
            var snd = *(Entity*)Ecs.RO(em, best, tiThrSound);
            bool own3 = Ecs.Alive(em, snd) && Ecs.Has(em, snd, Ecs.tiLTW);
            var pos = Ecs.Position(em, own3 ? snd : best);
            float* M = (float*)vd;   // float4x4, columns c0..c3
            float px = M[0] * pos.x + M[4] * pos.y + M[8] * pos.z + M[12], py = M[1] * pos.x + M[5] * pos.y + M[9] * pos.z + M[13], pz = M[2] * pos.x + M[6] * pos.y + M[10] * pos.z + M[14];
            float vx = Math.Min(Math.Max(2f * px - mn[0], 0.5f), sx - 0.5f), vy = Math.Min(Math.Max(2f * py - mn[1], 0.5f), sy - 0.5f), vz = Math.Min(Math.Max(2f * pz - mn[2], 0.5f), sz - 0.5f);
            int ix = (int)vx, iy = (int)vy, iz = (int)vz;
            int idx = (iz * sy + iy) * sx + ix; if (idx < 0 || idx >= *(int*)(buf + 8)) { ModLog.Info(sb.Append("; (grid and buffer sizes differ this frame)").ToString()); return; }
            ushort sample = (*(ushort**)buf)[idx];
            int dx = Math.Abs(ix - li[0]), dy = Math.Abs(iy - li[1]), dz = Math.Abs(iz - li[2]);
            int hi = Math.Max(dx, Math.Max(dy, dz)), lo = Math.Min(dx, Math.Min(dy, dz)), md = dx + dy + dz - hi - lo;
            int free = 71 * lo + 58 * (md - lo) + 41 * (hi - md);
            float occ = Math.Max(0f, Math.Min(1f, (sample - free) / 41f * 0.035f)) * 0.92f;
            string name; try { name = BlockIndex.NameOfHash(Ecs.Prefab(em, best)) ?? "?"; } catch (Exception) { name = "?"; }
            sb.Append("; loudest thruster ").Append(name).Append(" #").Append(best.Index).Append(" (volume ").Append(vol.ToString("F2")).Append(", ").Append(playing).Append(" making sound)")
              .Append(own3 ? "" : " [its sound entity has no LocalToWorld: block position used]")
              .Append(": its sound sits in voxel (").Append(ix + mn[0]).Append(",").Append(iy + mn[1]).Append(",").Append(iz + mn[2]).Append("), path ").Append(sample == 0xFFFF ? "NONE (no way through)" : sample.ToString())
              .Append(" vs ").Append(free).Append(" in a straight line -> occlusion ").Append(occ.ToString("F2")).Append(" (muffled ").Append((occ * 100f).ToString("F0")).Append(" %, the game's maximum is 92 %)");
            ModLog.Info(sb.ToString());
            if (sample != 0xFFFF && occ < 0.9f) PathLine(em, *(ushort**)buf, *(int*)(buf + 8), sx, sy, sz, mn, li, ix, iy, iz);
        }

        /// <summary>0.31.33 [Debug] sound_watch (Andre: "5 layers of rubber, checked with noclip, the thrusters still come in"): the way the game's
        /// sound takes from the loudest thruster to you, walked back on the game's own distance buffer (each step goes to the neighbour whose
        /// distance is exactly this one minus the step's cost 41/58/71, so it is a shortest way the game really found). Every cell on it is
        /// listed with what fills it: M = one of this mod's cells (its open sides), G = a cell of a game block's sound obstacle, - = empty for
        /// the game's sound, and the blocks whose colliders reach it. Where the way crosses the hull is the hole. Only when the thruster is
        /// muffled less than 90 %, at most every 6 s or when the way changes.</summary>
        static string lastPath = ""; static float tPath = -99f;
        static unsafe void PathLine(EntityManager em, ushort* D, int n, int sx, int sy, int sz, int* mn, int* li, int x, int y, int z)
        {
            var way = new List<int>(64);
            for (int guard = 0; guard < 2000; guard++)
            {
                int cur = (z * sy + y) * sx + x; if (cur < 0 || cur >= n) break;
                way.Add(cur);
                int dv = D[cur]; if (dv == 0 || dv == 0xFFFF) break;
                int bx = 0, by = 0, bz = 0, bm = 9;
                for (int dz = -1; dz <= 1; dz++) for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int m = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz); if (m == 0 || m >= bm) continue;
                    int nx = x + dx, ny = y + dy, nz = z + dz; if (nx < 0 || ny < 0 || nz < 0 || nx >= sx || ny >= sy || nz >= sz) continue;
                    int ni = (nz * sy + ny) * sx + nx; if (ni >= n) continue;
                    int cost = m == 1 ? 41 : m == 2 ? 58 : 71;
                    if (D[ni] + cost == dv) { bx = dx; by = dy; bz = dz; bm = m; }
                }
                if (bm == 9) break;
                x += bx; y += by; z += bz;
            }
            var sb = new StringBuilder("sound watch: the way from the thruster to you (").Append(way.Count).Append(" cells; cell = 0.5 m, game cell coordinates of this part; listener (")
              .Append(li[0] + mn[0]).Append(",").Append(li[1] + mn[1]).Append(",").Append(li[2] + mn[2]).Append(")):");
            int air = 0; string sig = "";
            foreach (int i in way)
            {
                int gx = i % sx + mn[0], gy = (i / sx) % sy + mn[1], gz = i / (sx * sy) + mn[2];
                long key = Key(gx, gy, gz); sig += key + ";";
                string tag = "-";
                if (cells.TryGetValue(key, out var c) && !Ecs.IsNull(c.proxy)) tag = "M[open " + SideNames(c.written) + "]";
                else if (own != null && own.covered.Contains(key)) tag = "G";
                var names = new List<string>();
                if (own != null) Names(em, own, key, names);
                foreach (var oc in others.Values) Names(em, oc, key, names);
                if (tag == "-" && names.Count == 0) { air++; continue; }
                if (air > 0) { sb.Append(" ..").Append(air).Append(" empty.."); air = 0; }
                sb.Append(" (").Append(gx).Append(",").Append(gy).Append(",").Append(gz).Append(")").Append(tag);
                if (names.Count > 0) sb.Append("{").Append(string.Join(",", names)).Append("}");
            }
            if (air > 0) sb.Append(" ..").Append(air).Append(" empty..");
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (sig == lastPath && now - tPath < 6f) return;
            bool fresh = sig != lastPath;
            lastPath = sig; tPath = now;
            ModLog.Info(sb.ToString());
            if (fresh && dumps < 6) { dumps++; try { Dump(em, way, sx, sy, mn); } catch (Exception ex) { ModLog.Warn("sound watch dump: " + ex.Message); } }
        }

        /// <summary>0.31.33 [Debug] sound_watch: for a new way, every tested cell on it with its six sides (C = closed by the rays, O = open:
        /// how many of the 49 rays got through, where the first one ran and which blocks it skipped because it started inside them, - = not
        /// tested: a cell the game's own obstacles fill or with no geometry), then every collider reaching those cells (block, part, kind,
        /// box in this part's garage metres). Enough to rebuild the spot offline.</summary>
        static int dumps;
        static readonly string[] SideN = { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };
        static unsafe void Dump(EntityManager em, List<int> way, int sx, int sy, int* mn)
        {
            var keys = new List<long>();
            foreach (int i in way)
            {
                long key = Key(i % sx + mn[0], (i / sx) % sy + mn[1], i / (sx * sy) + mn[2]);
                if (cells.TryGetValue(key, out var c) && c.cand) keys.Add(key);
            }
            if (keys.Count > 24) keys = keys.GetRange(keys.Count - 24, 24);
            var refs = new List<Ref>(); var seen = new HashSet<Ref>();
            foreach (long key in keys)
            {
                Unkey(key, out int x, out int y, out int z);
                var sb = new StringBuilder("sound watch dump: cell (").Append(x).Append(",").Append(y).Append(",").Append(z).Append(") = garage [")
                  .Append((x * 0.5f).ToString("F2")).Append("..").Append((x * 0.5f + 0.5f).ToString("F2")).Append(", ").Append((y * 0.5f).ToString("F2")).Append("..").Append((y * 0.5f + 0.5f).ToString("F2")).Append(", ")
                  .Append((z * 0.5f).ToString("F2")).Append("..").Append((z * 0.5f + 0.5f).ToString("F2")).Append("]");
                if (cells.TryGetValue(key, out var cl)) sb.Append(" written ").Append(SideNames(cl.written)).Append(cl.dyn ? " (moving-type)" : "");
                for (int d = 0; d < 6; d++)
                {
                    int axis = d >> 1; long pk = (d & 1) == 0 ? PairKey(key, axis) : PairKey(Nb(key, axis, -1), axis);
                    sb.Append(" | ").Append(SideN[d]).Append(" ");
                    if (!pairs.TryGetValue(pk, out var pr)) { sb.Append("-"); continue; }
                    if (pr.closed) { sb.Append("C"); continue; }
                    long a = pk >> 2; long b = Nb(a, axis, +1);
                    var ra = RegionOf(em, a); var rb = RegionOf(em, b);
                    sb.Append(RegionOpen(em, pk) ? "O" : "c(now closed)").Append(" air ").Append(ra.n).Append("|").Append(rb.n);
                    gather.Clear(); stamp++;
                    Collect(own, a); Collect(own, b); foreach (var oc in others.Values) { Collect(oc, a); Collect(oc, b); }
                    foreach (var g in gather) if (seen.Add(g)) refs.Add(g);
                }
                ModLog.Info(sb.ToString());
            }
            var sg = new StringBuilder("sound watch dump: ").Append(refs.Count).Append(" collider(s) near the way (garage metres of this part):");
            int cnt = 0;
            foreach (var r in refs)
            {
                if (cnt++ >= 120) { sg.Append(" ..."); break; }
                var k = r.k; V mn2, mx2;
                if (r.c.own) { mn2 = k.amin; mx2 = k.amax; } else Box(k.amin, k.amax, r.c.q, r.c.p, out mn2, out mx2);
                var bk = k.solidRoot ? solidBlk : BlockOf(em, k.e);
                sg.Append("\n  ").Append(RefName(em, r)).Append(" e").Append(k.e.Index).Append(bk.kind == 0 ? " solid" : bk.kind == 1 ? " open-frame" : " frame(mask " + bk.mask + "/" + bk.count + ")")
                  .Append(k.box ? " box" : " hull" + (k.verts != null ? k.verts.Length : 0)).Append(" cr ").Append(k.cr.ToString("F3"))
                  .Append(" [").Append(mn2.x.ToString("F3")).Append(",").Append(mn2.y.ToString("F3")).Append(",").Append(mn2.z.ToString("F3")).Append(" .. ")
                  .Append(mx2.x.ToString("F3")).Append(",").Append(mx2.y.ToString("F3")).Append(",").Append(mx2.z.ToString("F3")).Append("]");
                if (k.box) sg.Append(" half ").Append(k.bh.x.ToString("F3")).Append(",").Append(k.bh.y.ToString("F3")).Append(",").Append(k.bh.z.ToString("F3"));
            }
            ModLog.Info(sg.ToString());
        }
        static string RefName(EntityManager em, Ref r)
        {
            string nm; try { nm = BlockIndex.NameOfHash(Ecs.Prefab(em, r.k.e)) ?? "?"; } catch (Exception) { nm = "?"; }
            return r.c.own ? nm : nm + "@" + r.c.part.Index;
        }
        static void Names(EntityManager em, Ctx c, long key, List<string> names)
        {
            if (!c.hash.TryGetValue(key, out var l)) return;
            foreach (var r in l)
            {
                if (names.Count >= 4) { if (!names.Contains("...")) names.Add("..."); return; }
                string nm; try { nm = BlockIndex.NameOfHash(Ecs.Prefab(em, r.k.e)) ?? "?"; } catch (Exception) { nm = "?"; }
                if (!c.own) nm += "@" + c.part.Index;
                if (!names.Contains(nm)) names.Add(nm);
            }
        }
        static string SideNames(int s)
        {
            if (s < 0) s = 0x3F; if (s == 0x3F) return "all"; if (s == 0) return "none";
            var n = new[] { "+X", "-X", "+Y", "-Y", "+Z", "-Z" }; var l = new List<string>(); for (int i = 0; i < 6; i++) if ((s >> i & 1) != 0) l.Add(n[i]); return string.Join(" ", l);
        }
    }
}
