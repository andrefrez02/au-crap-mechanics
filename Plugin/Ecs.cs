using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Physics;

namespace CrapMechanics
{
    /// <summary>Raw access to the game's ECS (without interop generics, which aren't always compiled) + minimal math.</summary>
    internal static class Ecs
    {
        public static TypeIndex tiSPData, tiVel, tiMass, tiLT, tiLTW, tiParent, tiPWI, tiPar, tiJoint, tiDecJ, tiDecType, tiPortRef, tiPort, tiSing, tiPrefab, tiPower, tiFramePorts, tiWireless, tiCable;
        public static TypeIndex tiPTM; public static bool ptmOk;   // 0.11.0: PostTransformMatrix (non-uniform scale of the renderers)
        public static TypeIndex tiGuid; static bool guidOk;         // 0.11.1: SCGuid (id of the block that the game saves in the world/blueprint)
        public static bool Ok = false, Failed = false;
        static EntityQuery qParts, qBlocks, qSing, qComps, qWireless, qChildren;

        public static bool Prepare(EntityManager em)
        {
            if (Ok) return true; if (Failed) return false;
            try
            {
                tiSPData = TypeManager.GetTypeIndex(Il2CppType.Of<SPData>());
                tiVel = TypeManager.GetTypeIndex(Il2CppType.Of<PhysicsVelocity>());
                tiMass = TypeManager.GetTypeIndex(Il2CppType.Of<PhysicsMass>());
                tiLT = TypeManager.GetTypeIndex(Il2CppType.Of<LocalTransform>());
                tiLTW = TypeManager.GetTypeIndex(Il2CppType.Of<LocalToWorld>());
                tiParent = TypeManager.GetTypeIndex(Il2CppType.Of<Parent>());
                tiPWI = TypeManager.GetTypeIndex(Il2CppType.Of<PhysicsWorldIndex>());
                tiPar = TypeManager.GetTypeIndex(Il2CppType.Of<PhysicsConstrainedBodyPair>());
                tiJoint = TypeManager.GetTypeIndex(Il2CppType.Of<PhysicsJoint>());
                tiDecJ = TypeManager.GetTypeIndex(Il2CppType.Of<SCDecouplingJoint>());
                tiDecType = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeDecoupler>());
                tiPortRef = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipElectricPortRef>());
                tiPort = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipElectricPort>());
                tiSing = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipSingleton>());
                tiPrefab = TypeManager.GetTypeIndex(Il2CppType.Of<SCPrefab>());
                tiPower = TypeManager.GetTypeIndex(Il2CppType.Of<SCElectricPower>());
                tiFramePorts = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeFrameWithPorts>());
                try { tiCable = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeCableCell>()); } catch (Exception ex) { ModLog.Warn("SCTypeCableCell: " + ex.Message); }   // 0.19.10
                tiWireless = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeWirelessTransmitter>());
                var w = new Il2CppStructArray<ComponentType>(1); w[0] = ComponentType.ReadOnly(tiWireless);
                qWireless = em.CreateEntityQuery(w);
                var a = new Il2CppStructArray<ComponentType>(4); a[0] = ComponentType.ReadOnly(tiSPData); a[1] = ComponentType.ReadOnly(tiVel); a[2] = ComponentType.ReadOnly(tiMass); a[3] = ComponentType.ReadOnly(tiLT);
                qParts = em.CreateEntityQuery(a);
                var b = new Il2CppStructArray<ComponentType>(3); b[0] = ComponentType.ReadOnly(tiDecJ); b[1] = ComponentType.ReadOnly(tiLTW); b[2] = ComponentType.ReadOnly(tiParent);
                qBlocks = em.CreateEntityQuery(b);
                var c = new Il2CppStructArray<ComponentType>(1); c[0] = ComponentType.ReadOnly(tiSing);
                qSing = em.CreateEntityQuery(c);
                var d = new Il2CppStructArray<ComponentType>(3); d[0] = ComponentType.ReadOnly(tiPrefab); d[1] = ComponentType.ReadOnly(tiParent); d[2] = ComponentType.ReadOnly(tiLTW);
                qComps = em.CreateEntityQuery(d);
                // 0.11.0: everything with Parent + LocalTransform (blocks and the blocks' child renderers/colliders)
                var f = new Il2CppStructArray<ComponentType>(2); f[0] = ComponentType.ReadOnly(tiParent); f[1] = ComponentType.ReadOnly(tiLT);
                qChildren = em.CreateEntityQuery(f);
                try { tiPTM = TypeManager.GetTypeIndex(Il2CppType.Of<PostTransformMatrix>()); ptmOk = true; } catch (Exception ex) { ModLog.Warn("PostTransformMatrix not available: " + ex.Message); }
                int sj = Marshal.SizeOf<PhysicsJoint>(), sp = Marshal.SizeOf<PhysicsConstrainedBodyPair>();
                ModLog.Info("ECS ready. PhysicsJoint=" + sj + " B, BodyPair=" + sp + " B, SpaceshipJoint=" + Marshal.SizeOf<SpaceshipJoint>() + " B (game: " + IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore<SpaceshipJoint>.NativeClassPtr, ref align) + ")");
                Ok = true; return true;
            }
            catch (Exception ex) { Failed = true; ModLog.Error("ECS: setup failed: " + ex); return false; }
        }
        static uint align;

        // ---- 0.12.0: (il2cpp) pointers of managed system groups, so the Harmony prefix on SystemBase.Update recognises the instance
        public static IntPtr PtrTransform, PtrPresentation, PtrBindsGroup, PtrTickPre, PtrTickPost, PtrPhysicsPre, PtrPhysicsSystem, PtrFrameSetup, PtrFixedStep, PtrPhysicsPost; static bool ptrsOk;   // 0.31.35: + FixedStepSimulationSystemGroup and PhysicsPostGroup (Perf.cs, measuring only)   // 0.31.35: + CustomRenderPipelineFrameSetupGroup (typed text, TextInput.cs)   // 0.31.26: + PhysicsSystemGroup (launch gravity, U7)   // 0.19.13: + PhysicsPreGroup (100 km rule)   // 0.19.6: + tick groups of the components (LRT)
        public static void PreparePointers()
        {
            if (ptrsOk) return;
            try
            {
                var w = World.DefaultGameObjectInjectionWorld; if (w == null || !w.IsCreated) return;
                var list = w.Systems; int n = list.Count;
                for (int i = 0; i < n; i++)
                {
                    var s = list[i]; if (s == null) continue;
                    string name; try { name = s.GetIl2CppType().Name; } catch (Exception) { continue; }
                    if (name == "TransformSystemGroup") PtrTransform = s.Pointer;
                    else if (name == "PresentationSystemGroup") PtrPresentation = s.Pointer;
                    else if (name == "CustomRenderPipelineGroup") PtrBindsGroup = s.Pointer;   // 0.13.2: where SeatBindingsUpdateSystem runs
                    else if (name == "SpaceshipComponentsTickPreGroup") PtrTickPre = s.Pointer;
                    else if (name == "SpaceshipComponentsTickPostGroup") PtrTickPost = s.Pointer;
                    else if (name == "PhysicsPreGroup") PtrPhysicsPre = s.Pointer;   // 0.19.13
                    else if (name == "PhysicsSystemGroup") PtrPhysicsSystem = s.Pointer;   // 0.31.26 (U7)
                    else if (name == "CustomRenderPipelineFrameSetupGroup") PtrFrameSetup = s.Pointer;   // 0.31.35: the game refills its typed-text buffer in this group, once per frame
                    else if (name == "FixedStepSimulationSystemGroup") PtrFixedStep = s.Pointer;   // 0.31.35 (Perf.cs): the tick loop runs inside this group's OnUpdate
                    else if (name == "PhysicsPostGroup") PtrPhysicsPost = s.Pointer;               // 0.31.35 (Perf.cs): the game's sound grid system runs here, every tick
                    try { if (s.TryCast<ComponentSystemGroup>() != null) Perf.Name(s.Pointer, name); } catch (Exception) { }
                }
                ptrsOk = true;
                ModLog.Info("system groups: TransformSystemGroup " + (PtrTransform == IntPtr.Zero ? "NOT found" : "ok") + ", PresentationSystemGroup " + (PtrPresentation == IntPtr.Zero ? "NOT found" : "ok") + ", CustomRenderPipelineGroup (seat key bindings) " + (PtrBindsGroup == IntPtr.Zero ? "NOT found" : "ok") + ", SpaceshipComponentsTickPre/Post " + (PtrTickPre == IntPtr.Zero || PtrTickPost == IntPtr.Zero ? "NOT found" : "ok") + ", PhysicsPreGroup " + (PtrPhysicsPre == IntPtr.Zero ? "NOT found" : "ok") + ", PhysicsSystemGroup " + (PtrPhysicsSystem == IntPtr.Zero ? "NOT found" : "ok") + ", CustomRenderPipelineFrameSetupGroup (typed text) " + (PtrFrameSetup == IntPtr.Zero ? "NOT found" : "ok") + " (" + n + " managed systems)");
            }
            catch (Exception ex) { ptrsOk = true; ModLog.Warn("system group pointers: " + ex.Message); }
        }
        public static void ResetPointers() { ptrsOk = false; PtrTransform = IntPtr.Zero; PtrPresentation = IntPtr.Zero; PtrBindsGroup = IntPtr.Zero; PtrTickPre = IntPtr.Zero; PtrTickPost = IntPtr.Zero; PtrPhysicsPre = IntPtr.Zero; PtrPhysicsSystem = IntPtr.Zero; PtrFrameSetup = IntPtr.Zero; PtrFixedStep = IntPtr.Zero; PtrPhysicsPost = IntPtr.Zero; ForgetCache(); Perf.Forget(); Far.Restart(); }

        // ---- lists
        public static unsafe List<Entity> QueryList(EntityQuery q)
        {
            var r = new List<Entity>();
            var arr = q.ToEntityArray(new AllocatorManager.AllocatorHandle { Index = 2, Version = 0 });
            int n = arr.m_Length; var buf = (Entity*)arr.m_Buffer;
            for (int i = 0; i < n; i++) r.Add(buf[i]);
            try { arr.Dispose(); } catch (Exception) { }
            return r;
        }
        /// <summary>0.31.35 (performance): the same entities in the same order as QueryList, into a list the caller keeps (no new list per tick).</summary>
        public static unsafe void QueryInto(EntityQuery q, List<Entity> r)
        {
            r.Clear();
            var arr = q.ToEntityArray(new AllocatorManager.AllocatorHandle { Index = 2, Version = 0 });
            int n = arr.m_Length; var buf = (Entity*)arr.m_Buffer;
            for (int i = 0; i < n; i++) r.Add(buf[i]);
            try { arr.Dispose(); } catch (Exception) { }
        }
        public static List<Entity> Parts(EntityManager em) => QueryList(qParts);
        /// <summary>Blocks with an SCDecouplingJoint buffer (the game's decouplers and ours, which are copies of the decoupler).</summary>
        public static List<Entity> DecouplerBlocks(EntityManager em) => QueryList(qBlocks);
        /// <summary>Every ship component (SCPrefab + Parent + LocalToWorld) — to find bearings and motors, which aren't copies of the decoupler.</summary>
        public static List<Entity> Components(EntityManager em) => QueryList(qComps);
        /// <summary>0.31.15 (Andre's 27 thousand block ship: slip ring, plugs, discover, garage, radar and the black box each went through EVERY
        /// block 2 to 10 times per second, one or more calls into the game per block — the stutter every half second). The blocks of a query,
        /// indexed by prefab, rebuilt only when the number of blocks changes (a launch, a block lost), when a sampled block died (rewind: other
        /// entities, same count), on Forget, or every 30 s. `mod` = the mod's own blocks (KindFromHash), a few hundred at most: what the
        /// periodic scans actually want. An entity in these lists may have died since the last rebuild: check before reading it.</summary>
        public class BlockSet
        {
            public List<Entity> all = new List<Entity>();
            public List<(Entity e, ulong h)> mod = new List<(Entity, ulong)>();
            public Dictionary<ulong, List<Entity>> byHash = new Dictionary<ulong, List<Entity>>();
            public int stamp;
            internal int count = -1; internal float builtAt, checkedAt = -9f; internal bool hashesOk;
            static readonly List<Entity> none = new List<Entity>();
            public List<Entity> Of(ulong hash) => hash != 0 && byHash.TryGetValue(hash, out var l) ? l : none;
            public void Forget() { count = -1; checkedAt = -9f; }
        }
        static readonly BlockSet launched = new BlockSet();
        /// <summary>fresh = check now even if it was checked less than 0.2 s ago (a block that was just launched has to be seen in this frame).</summary>
        public static BlockSet Launched(EntityManager em, bool fresh = false) { if (fresh) launched.checkedAt = -9f; return Refresh(em, launched, qComps); }
        public static void ForgetBlocks() { launched.Forget(); }
        public static BlockSet Refresh(EntityManager em, BlockSet set, EntityQuery q)
        {
            float now = Now;
            if (set.count >= 0 && now - set.checkedAt < 0.2f) return set;
            set.checkedAt = now;
            try
            {
                int n; try { n = q.CalculateEntityCount(); } catch (Exception) { n = -1; }
                bool stale = n < 0 || n != set.count || set.hashesOk != JointSystem.HashesOk || now - set.builtAt > 30f;
                if (!stale && set.all.Count > 0) { var a = set.all; stale = !Alive(em, a[0]) || !Alive(em, a[a.Count / 2]) || !Alive(em, a[a.Count - 1]); }
                // a mod block replaced by another (moved in the garage: same count, another entity) shows as one of ours that is gone
                if (!stale) foreach (var (e, _) in set.mod) if (!Alive(em, e)) { stale = true; break; }
                if (!stale) return set;
                // new lists (never cleared in place): whoever is going through the old ones just finishes with them
                var all = new List<Entity>(); var mod = new List<(Entity, ulong)>(); var byHash = new Dictionary<ulong, List<Entity>>();
                foreach (var e in QueryList(q))
                {
                    all.Add(e);
                    long k = K(e);
                    if (!prefabOf.TryGetValue(k, out ulong h)) { h = PrefabRead(em, e); if (h != 0) { if (prefabOf.Count > 200000) prefabOf.Clear(); prefabOf[k] = h; } }   // fresh from the query: alive
                    if (h == 0) continue;
                    if (!byHash.TryGetValue(h, out var l)) { l = new List<Entity>(); byHash[h] = l; }
                    l.Add(e);
                    if (JointSystem.KindFromHash(h, out _, out _, out _)) mod.Add((e, h));
                }
                set.all = all; set.mod = mod; set.byHash = byHash;
                set.count = n >= 0 ? n : all.Count; set.builtAt = now; set.hashesOk = JointSystem.HashesOk; set.stamp++;
            }
            catch (Exception ex) { set.count = -1; if (blockSetWarnings++ < 3) ModLog.Warn("block index: " + ex.Message); }
            return set;
        }
        static int blockSetWarnings;
        /// <summary>Wireless Transmitter: SCTypeWirelessTransmitter._currentChannel (+32). -1 if it isn't a transmitter.</summary>
        public static unsafe int Channel(EntityManager em, Entity e)
        {
            try { if (IsNull(e) || !Ecs.Has(em, e, tiWireless)) return -1; return *(int*)((Ecs.HasC(em, e, tiWireless) ? (byte*)Ecs.RO(em, e, tiWireless) : null) + 32); }
            catch (Exception) { return -1; }
        }
        public static List<Entity> Transmitters(EntityManager em) => QueryList(qWireless);
        public static EntityQuery QSing => qSing;
        public static long K(Entity e) => ((long)e.Index << 32) | (uint)e.Version;
        public static Entity OfKey(long k) => new Entity { Index = (int)(k >> 32), Version = (int)(uint)(k & 0xffffffff) };
        /// <summary>0.11.0: entities whose Parent is `parent` (a block's renderers/colliders; a part's blocks).</summary>
        // 0.12.6: parent -> children index rebuilt at most once per frame. Before, every call scanned ALL the children in the world; with the
        // blueprint menu open (the game creates the entities of the BP previews) that became thousands x thousands per scan = the menu freezing.
        static int childrenFrame = -1; static readonly Dictionary<long, List<Entity>> childrenOf = new Dictionary<long, List<Entity>>();
        public static int TotalChildren;
        static readonly List<Entity> noChildren = new List<Entity>();
        // 0.26.9 (Andre's log, 22-23/10: the mod cycle cost 7-17 ms per frame with the ship, and grew with the number of entities in the
        // world, not with the number of joints — 4 ms with 0 joints and 12 thousand entities). The index below scans the whole world (17-21 thousand entities)
        // on the first request of each frame. Now the children come from the Child buffer that Unity keeps on each parent (only that block's).
        // The first 3 blocks with children are checked against the index; if they differ, it goes back to the index for good.
        static TypeIndex tiChild; static bool childOk, childFailed; static int childChecked;
        public static unsafe List<Entity> Children(EntityManager em, Entity parent)
        {
            if (!childFailed)
            {
                try
                {
                    if (!childOk) { tiChild = TypeManager.GetTypeIndex(Il2CppType.Of<Child>()); childOk = true; }
                    var r = new List<Entity>();
                    if (HasC(em, parent, tiChild)) foreach (var f in BufferEntities(em, parent, tiChild)) if (em.Exists(f)) r.Add(f);
                    if (childChecked >= 3) return r;
                    var idx = ChildrenIndex(em, parent);
                    if (idx.Count == 0) return r;
                    childChecked++;
                    if (idx.Count != r.Count)
                    {
                        childFailed = true;
                        ModLog.Warn("children: the Child buffer of entity " + parent.Index + " has " + r.Count + " but " + idx.Count + " entities point to it — scanning the whole world like before");
                        return idx;
                    }
                    if (childChecked == 3) ModLog.Info("children: read from each block's Child buffer (checked on 3 blocks) — no more scan of the whole world every frame");
                    return r;
                }
                catch (Exception ex) { childFailed = true; ModLog.Warn("children: Child buffer not available (" + ex.Message + ") — scanning the whole world like before"); }
            }
            return ChildrenIndex(em, parent);
        }
        /// <summary>0.26.9: total number of entities with Parent (only for the performance log; it used to come out of the every-frame scan).</summary>
        public static int CountChildren() { try { return qChildren.CalculateEntityCount(); } catch (Exception) { return TotalChildren; } }
        static unsafe List<Entity> ChildrenIndex(EntityManager em, Entity parent)
        {
            try
            {
                int f = UnityEngine.Time.frameCount;
                if (f != childrenFrame)
                {
                    childrenFrame = f; foreach (var l in childrenOf.Values) l.Clear();
                    var all = QueryList(qChildren); TotalChildren = all.Count;
                    foreach (var e in all)
                    {
                        var p = (Ecs.HasC(em, e, tiParent) ? (Parent*)Ecs.RO(em, e, tiParent) : null);
                        if (p == null) continue;
                        long k = K(p->Value);
                        if (!childrenOf.TryGetValue(k, out var list)) { list = new List<Entity>(); childrenOf[k] = list; }
                        list.Add(e);
                    }
                    if (childrenOf.Count > 20000) { var empty = new List<long>(); foreach (var kv in childrenOf) if (kv.Value.Count == 0) empty.Add(kv.Key); foreach (var k2 in empty) childrenOf.Remove(k2); }
                }
                // 0.26.0 (performance): returns the index's list, without copying — it used to be a new List per call, and there is a call per block
                // per frame. Callers only iterate (the index is only rebuilt on the FIRST call of each frame, never in the middle of a foreach).
                return childrenOf.TryGetValue(K(parent), out var r) ? r : noChildren;
            }
            catch (Exception ex) { ModLog.Warn("children of " + parent.Index + ": " + ex.Message); return new List<Entity>(); }
        }
        public static unsafe bool HasPTM(EntityManager em, Entity e) => ptmOk && Ecs.Has(em, e, tiPTM);
        public static unsafe void ScaleY(EntityManager em, Entity e, float sy)
        {
            var m = (Ecs.HasC(em, e, tiPTM) ? (float4x4*)em.GetComponentDataRawRW(e, tiPTM) : null);
            if (m == null) return;
            // 0.19.14: keeps the X/Z scale already in the matrix (the M version is scale -1 in X; before it became +1)
            float sx = m->c0.x, sz = m->c2.z; if (System.Math.Abs(sx) < 1e-4f) sx = 1f; if (System.Math.Abs(sz) < 1e-4f) sz = 1f;
            *m = float4x4.Scale(new float3 { x = sx, y = sy, z = sz });
        }
        public static unsafe string Components(EntityManager em, Entity e)
        {
            try
            {
                var arr = em.GetComponentTypes(e, Unity.Collections.Allocator.Temp);
                var sb = new System.Text.StringBuilder();
                int n = arr.Length;
                for (int i = 0; i < n; i++)
                {
                    var ct = arr[i]; string name = null;
                    try { var t = TypeManager.GetType(ct.TypeIndex); if (t != null) name = t.FullName; } catch (Exception) { }
                    sb.Append(i == 0 ? "" : ", ").Append(name ?? ("#" + ct.TypeIndex.Value));
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "(could not list: " + ex.Message + ")"; }
        }
        public static bool IsGameDecoupler(EntityManager em, Entity e) => Ecs.Has(em, e, tiDecType);
        /// <summary>0.11.1: the block's SCGuid (16 bytes) in hex, "" if it has none. It's the id the game keeps in the save/blueprint —
        /// used as a key to keep our own things per block (text of the generic blocks' labels).</summary>
        public static unsafe string Guid(EntityManager em, Entity e)
        {
            try
            {
                if (!guidOk) { guidOk = true; tiGuid = TypeManager.GetTypeIndex(Il2CppType.Of<SCGuid>()); }
                if (tiGuid.Value == 0 || !Ecs.Has(em, e, tiGuid)) return "";
                var b = (Ecs.HasC(em, e, tiGuid) ? (byte*)Ecs.RO(em, e, tiGuid) : null); if (b == null) return "";
                var sb = new System.Text.StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
            catch (Exception ex) { ModLog.Warn("block guid " + e.Index + ": " + ex.Message); return ""; }
        }
        // 0.19.12: player (PlayerControllerData) for the transition log
        static TypeIndex tiPly; static EntityQuery qPly; static bool playerOk;
        public static Entity MyPlayerOrNull(EntityManager em)
        {
            try
            {
                if (!playerOk) { playerOk = true; tiPly = TypeManager.GetTypeIndex(Il2CppType.Of<PlayerControllerData>()); var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiPly); qPly = em.CreateEntityQuery(a); }
                foreach (var e in QueryList(qPly)) return e;
            }
            catch (Exception) { }
            return Entity.Null;
        }
        public static bool IsNull(Entity e) => e.Index == 0 && e.Version == 0;
        public static bool Alive(EntityManager em, Entity e)
        {
            if (IsNull(e)) return false;
            if (cOn) return AliveCached(em, e);
            try { return em.Exists(e); } catch (Exception) { return false; }
        }

        // ---- 0.31.35 (performance, Andre: "the game/mod performance is really bad"): the read cache.
        // Every em.X(...) of the interop is an il2cpp_runtime_invoke with a boxed return; the hooks inside the game's tick asked the same
        // questions about the same 25 parts again and again (is it alive, has it the component, where is its PhysicsMass). Those answers
        // can only change with a STRUCTURAL change of the world: an entity is created, destroyed or moved to another chunk (= it gets or
        // loses a component, or a neighbour in its chunk is removed). Every structural change adds one to the "order version" of every
        // component type of the archetypes it touches [Burst listing 21/11, ChunkDataUtility: Allocate 0x11D5C, Deallocate, Remove, Clone,
        // AllocateClone, MoveArchetype, and ChangeArchetypeInPlace for both the old and the new archetype (0x12BDC, 0x12C1C), all run
        // m_ComponentTypeOrderVersion[type & 0x1fff]++]. So:
        //   "entity e has / has not component T" and "T's data of e is at this address" stay true while T's number does not change
        //   (if e has T, anything that moves or destroys e touches an archetype with T; if e gets T, it arrives in one);
        //   "e exists" stays true while the number of a type e is known to have does not change (or Entity's own number, which every
        //   change bumps); "e does not exist" stays true while Entity's own number does not change (review: it was "for good").
        // The numbers are read straight from the game's array (EntityDataAccess.m_EntityComponentStore.m_ComponentTypeOrderVersion, found
        // by field name), on every single answer, so a change the mod itself makes one line earlier is seen too. At every Begin the array
        // is checked against the game's own EntityManager.EntityOrderVersion (one call); if it ever disagrees, or the fields are not
        // found, the cache falls back to that one number for everything (and to Dirty() after the mod's own structural changes).
        //   Begin(em) ... End(): only inside them (the mod's tick hooks and its frame cycle) the cache answers; everywhere else every
        //     call is the real one, as before.
        //   RO(em, e, type): the stored address — but the FIRST read of each component type after each Begin is always the real call,
        //     because it is the real call that makes the game finish the jobs writing that type (the wait is per type, not per entity).
        //   Writable pointers (GetComponentDataRawRW) are never cached: same wait, same change-version stamp as before.
        // Proof: [Debug] perf_compare checks EVERY cached answer against the real call (same bool, same pointer) and returns the real
        // one; without it one answer in 256 is checked the same way. A difference is logged and switches the cache off for the session.
        public static bool CacheWanted = true;   // [Debug] perf_read_cache
        public static bool CacheCompare;         // [Debug] perf_compare (with debug_tools)
        struct CK : IEquatable<CK>
        {
            public long e; public int t;
            public bool Equals(CK o) => e == o.e && t == o.t;
            public override bool Equals(object o) => o is CK c && Equals(c);
            public override int GetHashCode() => ((int)(e >> 32) * 397) ^ (int)e ^ (t * 7919);
        }
        struct HE { public bool v; public int ver; }
        struct PE { public IntPtr p; public int ver; }
        const long DeadBit = 1L << 62; const int SlotMask = 0x1fff;
        static bool cOn, cBroken; static int cOrder = int.MinValue, cGen = 1, cEpoch = 1; static uint cSample;
        static unsafe int* cVer;   // the game's order-version array of this world (null: the one-number fallback)
        static int cEntitySlot, offVerArr = int.MinValue; static bool rawOff; static IntPtr cVerSeen = new IntPtr(-1);
        static readonly Dictionary<long, long> cAlive = new Dictionary<long, long>();   // entity -> (slot << 32 | version of that slot when it was asked) [| DeadBit = it did not exist then]
        static readonly Dictionary<CK, HE> cHas = new Dictionary<CK, HE>();
        static readonly Dictionary<CK, PE> cPtr = new Dictionary<CK, PE>();
        static readonly int[] cTypeEpoch = new int[SlotMask + 1];
        public static long CacheHits, CacheCalls, CacheChecks, CacheMismatches, CacheOrderChanges; static int cWarned;
        public static bool CacheActive => cOn;
        public static bool CacheBroken => cBroken;

        /// <summary>Start of a stretch of the mod's own code in which no game code runs (a tick hook, the frame cycle).</summary>
        public static unsafe void Begin(EntityManager em)
        {
            if (!CacheWanted || cBroken || !Ok || Unloading.Active) { cOn = false; return; }
            try
            {
                int v = em.EntityOrderVersion;
                cEpoch++;
                if (v != cOrder) { cOrder = v; cGen++; CacheOrderChanges++; }
                cVer = RawArray(em);
                if (cVer != null && cVer[cEntitySlot] != v)
                {
                    cVer = null; rawOff = true;
                    ModLog.Warn("read cache: the game's order-version array does not match EntityManager.EntityOrderVersion — using the single number instead (slower, still exact)");
                }
                // another array (another world) or another way of numbering than at the last Begin: nothing stored is comparable
                if ((IntPtr)cVer != cVerSeen) { cVerSeen = (IntPtr)cVer; DropAll(); }
                if (cAlive.Count > 400000 || cHas.Count > 400000 || cPtr.Count > 400000) DropAll();
                cOn = true;
            }
            catch (Exception) { cOn = false; }
        }
        public static void End() { cOn = false; }
        /// <summary>Only matters in the one-number fallback: looks at the world's structure number again (one call).</summary>
        public static unsafe void Sync(EntityManager em)
        {
            if (!cOn || cVer != null) return;
            try { int v = em.EntityOrderVersion; if (v != cOrder) { cOrder = v; cGen++; CacheOrderChanges++; } }
            catch (Exception) { cOn = false; }
        }
        /// <summary>The mod changed the world's structure itself. With the game's array every answer sees that by itself; in the one-number
        /// fallback nothing stored before is trusted after it.</summary>
        public static void Dirty() { cGen++; }
        /// <summary>The mod ran a game system from inside its own code: it may have scheduled jobs, so the first read of each component type
        /// is the real call again (and, in the fallback, the structure number is read again).</summary>
        public static void AfterGameCode(EntityManager em) { if (!cOn) return; cEpoch++; cGen++; Sync(em); }
        static void DropAll() { cAlive.Clear(); cHas.Clear(); cPtr.Clear(); }
        /// <summary>World unloaded or swapped.</summary>
        public static unsafe void ForgetCache() { cOn = false; DropAll(); cVer = null; cVerSeen = new IntPtr(-1); cOrder = int.MinValue; cGen++; cEpoch++; }
        static bool Check() => CacheCompare || (++cSample & 255u) == 0u;

        /// <summary>The address of m_ComponentTypeOrderVersion (int per component type) of this EntityManager's world, or null.</summary>
        static unsafe int* RawArray(EntityManager em)
        {
            if (rawOff) return null;
            if (offVerArr == int.MinValue)
            {
                try
                {
                    IntPtr f1 = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<EntityDataAccess>.NativeClassPtr, "m_EntityComponentStore");
                    IntPtr f2 = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<EntityComponentStore>.NativeClassPtr, "m_ComponentTypeOrderVersion");
                    if (f1 == IntPtr.Zero || f2 == IntPtr.Zero) throw new InvalidOperationException("fields not found");
                    int o1 = (int)IL2CPP.il2cpp_field_get_offset(f1) - 16, o2 = (int)IL2CPP.il2cpp_field_get_offset(f2) - 16;   // struct fields: il2cpp counts a 16-byte object header (as in Garage.cs)
                    // game of 21/11 (metadata dump): EntityManager.m_EntityDataAccess +0, EntityDataAccess.m_EntityComponentStore +0 (by value),
                    // EntityComponentStore.m_ComponentTypeOrderVersion +0 (the Burst code reads it at [store + 0] too). Anything else has not
                    // been looked at: the single number is used instead
                    if (o1 != 0 || o2 != 0) throw new InvalidOperationException("the fields moved (" + o1 + "/" + o2 + ") since the game version this was checked against");
                    var te = TypeManager.GetTypeIndex(Il2CppType.Of<Entity>());
                    // a type's place in the array is its index = Value & 0x1fff (the game's own loop): checked against the game's property
                    foreach (var t in new[] { te, tiMass, tiVel, tiLT, tiSPData, tiPort, tiParent, tiJoint })
                        if (t.Value == 0 || t.Index != (t.Value & SlotMask)) throw new InvalidOperationException("type index rule differs for #" + t.Value);
                    cEntitySlot = te.Value & SlotMask; offVerArr = o1 + o2;
                    ModLog.Info("read cache: the game's per-type structure numbers are read at EntityDataAccess+" + o1 + " / EntityComponentStore+" + o2 + " (checked against EntityManager.EntityOrderVersion at every use)");
                }
                catch (Exception ex) { rawOff = true; ModLog.Info("read cache: the game's per-type structure numbers are not available (" + ex.Message + ") — using the single number EntityManager.EntityOrderVersion instead"); return null; }
            }
            ulong eda = (ulong)em.m_EntityDataAccess; if (eda < 0x10000UL || eda >= 0x00007FFFFFFF0000UL) return null;
            ulong arr = *(ulong*)((byte*)eda + offVerArr); if (arr < 0x10000UL || arr >= 0x00007FFFFFFF0000UL || (arr & 3UL) != 0) return null;
            return (int*)arr;
        }
        static unsafe int Ver(int slot) => cVer != null ? cVer[slot] : cGen;
        static void Mismatch(string what, Entity e, int type, string had, string real)
        {
            CacheMismatches++;
            if (cWarned++ < 5) ModLog.Error("read cache: DIFFERENCE on " + what + " of entity " + e.Index + ":" + e.Version + (type != 0 ? " type #" + type : "") + " — stored " + had + ", the game says " + real
                                            + (CacheCompare ? " (perf_compare: the game's answer was used)" : " — the cache is OFF until the game restarts; please send this log"));
            if (!CacheCompare) { cBroken = true; cOn = false; }
            DropAll();
        }
        static unsafe bool AliveCached(EntityManager em, Entity e)
        {
            long k = K(e);
            if (cAlive.TryGetValue(k, out long st))
            {
                // review 0.31.35: "it does not exist" is trusted only while Entity's own number stands too (every creation bumps it), not for
                // good: an index is handed out again with the version its free slot already carries, so a not-yet-made entity could be asked about
                long sv = st & ~DeadBit;
                bool known = (int)sv == Ver((int)(sv >> 32));
                if (known)
                {
                    bool v = (st & DeadBit) == 0;
                    CacheHits++;
                    if (!Check()) return v;
                    CacheChecks++;
                    bool r; try { r = em.Exists(e); } catch (Exception) { return false; }
                    if (r != v) Mismatch("Exists", e, 0, v.ToString(), r.ToString());
                    return r;
                }
            }
            int gv = Ver(cEntitySlot);
            bool x; try { x = em.Exists(e); } catch (Exception) { return false; }
            CacheCalls++; cAlive[k] = (((long)cEntitySlot << 32) | (uint)gv) | (x ? 0L : DeadBit); return x;
        }
        /// <summary>needAlive: the caller asked "exists AND has" (HasC, two calls); otherwise "has" (one call).</summary>
        static unsafe bool HasCached(EntityManager em, Entity e, TypeIndex ti, bool needAlive)
        {
            int tv = ti.Value, slot = tv & SlotMask; long k = K(e); var key = new CK { e = k, t = tv };
            int ver = Ver(slot);
            if (cHas.TryGetValue(key, out var he) && he.ver == ver)
            {
                CacheHits++;
                if (!Check()) return he.v;
                CacheChecks++;
                bool r; try { r = em.Exists(e) && em.HasComponent(e, CT(ti)); } catch (Exception) { return false; }
                if (r != he.v) Mismatch("HasComponent", e, tv, he.v.ToString(), r.ToString());
                return r;
            }
            bool x;
            try
            {
                if (needAlive) x = AliveCached(em, e) && em.HasComponent(e, CT(ti));
                else { x = em.HasComponent(e, CT(ti)); if (x && !(cAlive.TryGetValue(k, out long st) && (st & DeadBit) == 0 && (int)st == Ver((int)(st >> 32)))) x = AliveCached(em, e); }   // a "yes" is stored only for an entity that exists
            }
            catch (Exception) { return false; }
            CacheCalls++; cHas[key] = new HE { v = x, ver = ver };
            if (x) cAlive[k] = ((long)slot << 32) | (uint)ver;   // it has this type: it exists for as long as this type's number stands
            return x;
        }
        /// <summary>GetComponentDataRawRO, through the cache when it is on. The caller has checked that the entity has the component,
        /// exactly as before every em.GetComponentDataRawRO.</summary>
        public static unsafe void* RO(EntityManager em, Entity e, TypeIndex ti)
        {
            if (!cOn) return em.GetComponentDataRawRO(e, ti);
            int tv = ti.Value, slot = tv & SlotMask; var key = new CK { e = K(e), t = tv };
            int ver = Ver(slot);
            if (cTypeEpoch[slot] == cEpoch && cPtr.TryGetValue(key, out var pe) && pe.ver == ver)
            {
                CacheHits++;
                if (!Check()) return (void*)pe.p;
                CacheChecks++;
                var r = em.GetComponentDataRawRO(e, ti);
                if ((IntPtr)r != pe.p) Mismatch("the read pointer", e, tv, "0x" + pe.p.ToString("X"), "0x" + ((IntPtr)r).ToString("X"));
                return r;
            }
            var q = em.GetComponentDataRawRO(e, ti);   // the real call: finishes the game's jobs that write this type
            CacheCalls++;
            if (cOn) { cTypeEpoch[slot] = cEpoch; cPtr[key] = new PE { p = (IntPtr)q, ver = ver }; }
            return q;
        }
        // ---- the mod's own structural changes: the same call, and the cache is told (even when it throws; it only matters in the one-number fallback)
        public static void Destroy(EntityManager em, Entity e) { try { em.DestroyEntity(e); } finally { Dirty(); } }
        public static Entity Create(EntityManager em, Il2CppStructArray<ComponentType> types) { try { return em.CreateEntity(types); } finally { Dirty(); } }
        /// <summary>0.31.36: the real byte size of an unmanaged component's struct = il2cpp_class_value_size of its class (the game's own
        /// sizeof; for a component with data that is its size in the chunk). NOT TypeManager.GetTypeInfo(ti).SizeInChunk: GetTypeInfo returns
        /// its struct BY REFERENCE, and in this game il2cpp_runtime_invoke hands back a boxed copy that the generated interop reads 16 bytes
        /// off, so every field of it is a wrong number (found by the Quick Start work: TypeSize 3 for a 176-byte struct). -1 = not known.
        /// Not for the hot path (two calls into the game per use): cache the result.</summary>
        public static int ComponentSize(TypeIndex ti)
        {
            try
            {
                var t = TypeManager.GetType(ti); if (t == null) return -1;
                IntPtr cls = IL2CPP.il2cpp_class_from_system_type(t.Pointer); if (cls == IntPtr.Zero) return -1;
                uint align = 0; int size = IL2CPP.il2cpp_class_value_size(cls, ref align);
                return size > 0 ? size : -1;
            }
            catch (Exception) { return -1; }
        }
        public static bool Add(EntityManager em, Entity e, ComponentType t) { try { return em.AddComponent(e, t); } finally { Dirty(); } }
        public static bool Remove(EntityManager em, Entity e, ComponentType t) { try { return em.RemoveComponent(e, t); } finally { Dirty(); } }
        /// <summary>For the performance line; resets the counts.</summary>
        public static unsafe string CacheSummary()
        {
            string s = !CacheWanted ? " | read cache: OFF ([Debug] perf_read_cache)" : cBroken ? " | read cache: OFF (a difference was found, see the 'read cache:' error)"
                     : " | read cache (" + (rawOff ? "one structure number for everything" : "per-type structure numbers") + "): " + CacheHits + " answers from the cache, " + CacheCalls + " calls into the game, the world's structure changed between the mod's hooks "
                       + CacheOrderChanges + " time(s), " + CacheChecks + " answers checked against the game, " + CacheMismatches + " DIFFERENT" + (CacheCompare ? " (perf_compare: every answer is checked, so no time is saved)" : "");
            CacheHits = 0; CacheCalls = 0; CacheChecks = 0; CacheOrderChanges = 0; if (!CacheCompare) CacheMismatches = 0;
            return s;
        }
        public static bool IsPart(EntityManager em, Entity e)
        {
            try { return Alive(em, e) && Ecs.Has(em, e, tiSPData) && Ecs.Has(em, e, tiMass) && Ecs.Has(em, e, tiPWI); }
            catch (Exception) { return false; }
        }
        public static unsafe Entity Parent(EntityManager em, Entity e)
        {
            // 0.31.27: one existence test and one HasComponent (it was HasComponent, then HasC again: 6 calls into the game for one read)
            try { if (!HasC(em, e, tiParent)) return Entity.Null; return ((Parent*)Ecs.RO(em, e, tiParent))->Value; }
            catch (Exception) { return Entity.Null; }
        }

        // ---- raw buffers. CAREFUL: EntityManager.GetBufferRawRO returns the pointer to the ELEMENTS (BufferHeader.GetElementPointer),
        // not to the header; the length comes from GetBufferLength. (Up to 0.1.3 I read it as if it were the header -> always "empty".)
        public static unsafe int[] BufferInts(EntityManager em, Entity e, TypeIndex ti)
        {
            int n = em.GetBufferLength(e, ti); if (n <= 0 || n > 4096) return new int[0];
            var data = (int*)em.GetBufferRawRO(e, ti); if (data == null) return new int[0];
            var r = new int[n]; for (int i = 0; i < n; i++) r[i] = data[i];
            return r;
        }
        public static unsafe Entity[] BufferEntities(EntityManager em, Entity e, TypeIndex ti)
        {
            int n = em.GetBufferLength(e, ti); if (n <= 0 || n > 4096) return new Entity[0];
            var data = (Entity*)em.GetBufferRawRO(e, ti); if (data == null) return new Entity[0];
            var r = new Entity[n]; for (int i = 0; i < n; i++) r[i] = data[i];
            return r;
        }

        // ---- SpaceshipSingleton._joints = UnsafeList<SpaceshipJoint>*
        [StructLayout(LayoutKind.Sequential)] public unsafe struct UnsafeListHeader { public void* Ptr; public int Length; public int Capacity; }
        public static unsafe SpaceshipJoint* Joints(EntityManager em, out int n)
        {
            n = 0;
            var sings = QueryList(qSing); if (sings.Count == 0) return null;
            var s = (Ecs.HasC(em, sings[0], tiSing) ? (SpaceshipSingleton*)Ecs.RO(em, sings[0], tiSing) : null);
            var ul = (UnsafeListHeader*)s->_joints; if (ul == null || ul->Ptr == null) return null;
            n = ul->Length; if (n < 0 || n > 1000000) { n = 0; return null; }
            return (SpaceshipJoint*)ul->Ptr;
        }

        // ---- signal of the block's input port (first port of the SpaceshipElectricPortRef buffer)
        /// <summary>Signal of the block's port [0]. SpaceshipElectricPort.DataValue() (game IL) = `_portValue` (float at offset 8)
        /// if it is <= 1e20, otherwise 0. Read raw so as not to call a game struct method on a chunk pointer.
        /// `reason` says why it wasn't read (to log once).</summary>
        public static unsafe bool Signal(EntityManager em, Entity block, out float v, out string reason)
        {
            v = 0f; reason = null;
            try
            {
                if (!Ecs.Has(em, block, tiPortRef)) { reason = "block has no SpaceshipElectricPortRef buffer"; return false; }
                var ports = BufferEntities(em, block, tiPortRef); if (ports.Length == 0) { reason = "SpaceshipElectricPortRef buffer is empty"; return false; }
                var p = PortOfType(em, block, PortInput); if (IsNull(p)) p = ports[0];
                if (!Alive(em, p)) { reason = "port " + p.Index + " does not exist"; return false; }
                if (!Ecs.Has(em, p, tiPort)) { reason = "port " + p.Index + " has no SpaceshipElectricPort"; return false; }
                var port = (Ecs.HasC(em, p, tiPort) ? (byte*)Ecs.RO(em, p, tiPort) : null);
                if (port == null) { reason = "port GetComponentDataRawRO returned null"; return false; }
                v = *(float*)(port + 8);
                // 0.31.26 (item 17): the game's own rule, DataValue() => _portValue > 1e20 ? 0 : _portValue (NaN also gives 0 — the compare
                // is vcmpnless) and negatives are never cut [recon/EPC_SCCable.cs:83-85]. It was 1e6 / -1e6: an altitude or a distance above
                // 1000 km arrived as 0 at a mod block while a game block on the same cable got it right. Camera video (1e21 + i*1e17) still reads 0.
                if (float.IsNaN(v) || v > 1e20f) v = 0f;
                return true;
            }
            catch (Exception ex) { reason = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        /// <summary>Port diagnostics, once per block: how many ports, which entity, raw value and what DataValue()/DataValue01() return.</summary>
        public static unsafe string SignalDiagnostic(EntityManager em, Entity block)
        {
            try
            {
                if (!Ecs.Has(em, block, tiPortRef)) return "no SpaceshipElectricPortRef buffer";
                var ports = BufferEntities(em, block, tiPortRef);
                var sb = new System.Text.StringBuilder(); sb.Append(ports.Length).Append(" port(s):");
                for (int i = 0; i < ports.Length && i < 4; i++)
                {
                    var p = ports[i]; sb.Append(" [").Append(i).Append(": ").Append(p.Index).Append(":").Append(p.Version);
                    if (!Alive(em, p)) { sb.Append(" dead]"); continue; }
                    if (!Ecs.Has(em, p, tiPort)) { sb.Append(" no SpaceshipElectricPort]"); continue; }
                    var port = (Ecs.HasC(em, p, tiPort) ? (byte*)Ecs.RO(em, p, tiPort) : null);
                    sb.Append(" raw+8=").Append((*(float*)(port + 8)).ToString("F3")).Append(" bytes=");
                    for (int k = 0; k < 32; k++) sb.Append(port[k].ToString("x2"));
                    try { var sp = *(SpaceshipElectricPort*)port; sb.Append(" DataValue=").Append(sp.DataValue().ToString("F3")).Append(" DataValue01=").Append(sp.DataValue01().ToString("F3")); }
                    catch (Exception ex) { sb.Append(" DataValue failed: ").Append(ex.Message); }
                    sb.Append("]");
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "diagnostics failed: " + ex.Message; }
        }

        // ---- ports (SpaceshipElectricPort: +0 _persistentConnectedCCOrPort, +8 _portValue, +12 _runtimeEndEntity, +20 _setupType, +21 _runtimeType, +22 _index)
        public const byte PortInput = 0, PortOutput = 1, PortPower = 2;
        public static Entity[] Ports(EntityManager em, Entity block)
        {
            try { if (!Ecs.Has(em, block, tiPortRef)) return new Entity[0]; return BufferEntities(em, block, tiPortRef); }
            catch (Exception) { return new Entity[0]; }
        }
        /// <summary>0.24.0: the type that counts in practice. _setupType (+20) is the prefab's; the game resolves _runtimeType (+21) from
        /// it and the runtime one is what rules when passing values. On a DataInOut (3) or Unset (5) port the setup doesn't give the direction —
        /// if the runtime one is already resolved to input or output, it's that one. (Without this SlipRings saw "no output on either side" on a
        /// receiving Wireless Transmitter, which is DataInOut in the prefab and DataOutput at run time, and didn't bridge.)</summary>
        /// <summary>0.31.11: in/out ports the game left without a direction because their cable ends at a pass-through port of a mod joint, with the
        /// direction the mod found on the other side of the joint (Radio). Read only by the mod (EffectiveType); the game's port is not touched.</summary>
        public static readonly Dictionary<long, byte> ResolvedTypes = new Dictionary<long, byte>();
        public static unsafe byte EffectiveType(EntityManager em, Entity p)
        {
            try
            {
                if (!HasC(em, p, tiPort)) return 255;   // 0.31.15: one check, not two
                var b = (byte*)Ecs.RO(em, p, tiPort); if (b == null) return 255;
                byte setup = b[20], run = b[21];
                if ((setup == 3 || setup == 5) && (run == PortInput || run == PortOutput)) return run;
                if (setup == 3 && ResolvedTypes.TryGetValue(K(p), out var rt)) return rt;   // 0.31.11: direction the mod found across one of its joints
                return setup;
            }
            catch (Exception) { return 255; }
        }
        public static unsafe bool Port(EntityManager em, Entity p, out byte kind, out float val, out Entity otherEnd)
        {
            kind = 255; val = 0f; otherEnd = Entity.Null;
            try
            {
                if (!HasC(em, p, tiPort)) return false;   // 0.31.15: one check, not two
                var b = (byte*)Ecs.RO(em, p, tiPort); if (b == null) return false;
                val = *(float*)(b + 8); if (float.IsNaN(val) || val > 1e20f) val = 0f;   // 0.31.26 (item 17): DataValue() exactly [recon/EPC_SCCable.cs:83-85]
                otherEnd = *(Entity*)(b + 12); kind = b[20];
                return true;
            }
            catch (Exception) { return false; }
        }
        /// <summary>Finds the port that FEEDS an input, going through Frame With Ports. When the input touches a frame port directly
        /// (no cable in between), the game leaves _runtimeEndEntity pointing at the frame's port and doesn't pass the value on.
        /// Here: frame port -> paired port on the opposite face of the same frame (index + n/2) -> its other end -> ... up to an output.
        /// `trail` describes the path (for the log).</summary>
        public static Entity Source(EntityManager em, Entity input, out string trail)
        {
            if (!PathCache) return SourceWalk(em, input, out trail);
            long k = K(input); float now = Now;
            if (paths.TryGetValue(k, out var h) && now < h.until && (IsNull(h.result) || Alive(em, h.result))) { trail = h.trail; return h.result; }
            var r = SourceWalk(em, input, out trail);
            if (h == null) { if (paths.Count > 20000) paths.Clear(); h = new PathHit(); paths[k] = h; }
            h.result = r; h.trail = trail; h.until = now + (IsNull(r) ? 0.1f : Ttl(k));   // "nothing found" is asked again soon (a cable still being set up at launch)
            return r;
        }
        // ---- 0.31.15 (Andre, 11/11: "you have to cut the ms per cable a lot"): the path of a cable doesn't change while the ship flies, and
        // walking it costs 4 calls into the game per cable cell, for every port the mod reads, every frame. The PATH (which port feeds this
        // one) is kept for 0.4-0.6 s (spread, so they don't all expire in the same frame); the VALUE is always read live from that port.
        // A path whose source died is walked again at once. Forgotten on rewind / world change (ForgetPaths) and when the mod itself rewires an
        // input (ForgetPath).
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        /// <summary>Seconds since the mod loaded (real time; cheap).</summary>
        public static float Now => (float)clock.Elapsed.TotalSeconds;
        public static bool PathCache = true;
        class PathHit { public Entity result; public string trail; public float until; }
        static readonly Dictionary<long, PathHit> paths = new Dictionary<long, PathHit>();
        class WirelessHit { public bool has; public Entity outPort; public string trail; public float until; }
        static readonly Dictionary<long, WirelessHit> wirelessHits = new Dictionary<long, WirelessHit>();
        static float Ttl(long k) => 0.4f + (float)(((ulong)k * 2654435761UL >> 16) & 255UL) / 1275f;
        public static void ForgetPaths() { paths.Clear(); wirelessHits.Clear(); foreach (var d in portOfType) d.Clear(); }
        public static void ForgetPath(Entity input) { paths.Remove(K(input)); }
        static Entity SourceWalk(EntityManager em, Entity input, out string trail)
        {
            var sb = new System.Text.StringBuilder(); trail = "";
            var seen = new HashSet<long>(); Entity cur = input;
            for (int hop = 0; hop < 16; hop++)
            {
                if (!seen.Add(K(cur))) { sb.Append(" loop"); break; }
                if (!RawPort(em, cur, out var t, out _, out var end, out var persist)) { sb.Append(" ?").Append(cur.Index); break; }
                var target = !IsNull(end) && !(end.Index == cur.Index && end.Version == cur.Version) ? end : persist;
                if (IsNull(target)) { sb.Append(" -> nothing"); break; }
                // 0.19.10 (logs from 02/10: after a time rewind / ship recreation, the game leaves _runtimeEndEntity pointing at the
                // cable cell ITSELF — every LRT "LOST (path: -> N(not a port))" at the same time): if the target is a cable cell, walk
                // along the cable (SCTypeCableCell +0x10 _connectedFwd / +0x18 _connectedBwd) to the port at the other end, without relying on the game.
                // 0.31.26 (item 18): when the game's end is a cell it is the LAST cell still inside the port's part (UpdateRuntimeEndsJob walks
                // only inside the part and stops at a burnt cell) [recon/Spaceship__part_b.cs:132-189] — not the cell next to the port. Walking
                // from it with `prev` = the port picked fwd blindly, and with 2+ cells fwd could lead back to the port ("dead end", depending on
                // the direction the cable was drawn). The walk now starts at _persistentConnectedCCOrPort, the cell plugged into the port, so
                // its neighbour is `cur` and the direction is certain.
                if (IsCable(em, target) && IsCable(em, persist)) target = persist;
                if (IsCable(em, target)) { int cells; var end2 = WalkCable(em, target, cur, out cells); sb.Append(" -> cable(").Append(cells).Append(" cell(s))"); if (IsNull(end2) || (end2.Index == cur.Index && end2.Version == cur.Version)) { sb.Append(" -> dead end"); break; } target = end2; }
                if (!RawPort(em, target, out var t2, out var v2, out _, out _)) { sb.Append(" -> ").Append(target.Index).Append("(not a port)"); break; }
                var owner = Parent(em, target);
                bool frame = !IsNull(owner) && Alive(em, owner) && Ecs.Has(em, owner, tiFramePorts);
                sb.Append(" -> ").Append(target.Index).Append("(t").Append(t2).Append(frame ? " frame " + owner.Index : "").Append(" v=").Append(v2.ToString("F2")).Append(")");
                if (t2 == PortOutput || !frame) { trail = sb.ToString(); return target; }
                var ps = Ports(em, owner); int i = Array.FindIndex(ps, x => x.Index == target.Index && x.Version == target.Version);
                if (i < 0 || ps.Length < 2) { sb.Append(" (port outside the frame's buffer)"); break; }
                cur = ps[(i + ps.Length / 2) % ps.Length];
                sb.Append(" => pair ").Append(cur.Index);
            }
            trail = sb.ToString(); return Entity.Null;
        }
        public static bool IsCable(EntityManager em, Entity e) { try { return tiCable.Value != 0 && Alive(em, e) && Ecs.Has(em, e, tiCable); } catch (Exception) { return false; } }
        /// <summary>0.19.10: follows a cable from the cell `cell` (coming from `srcE`) to the entity that isn't a cell (the port at the other end).
        /// Entity.Null if the cable ends in nothing. 0.31.26 (items 5/18): also Entity.Null at a burnt-through cell (_burnDamage01 +52 >= 1):
        /// the game cuts the cable there — runtime end and circuit cells stop at it [recon/Spaceship__part_b.cs:157-159;
        /// recon/Spaceship__part_a.cs:496-497]. Only power cables heat up, so data and Drive Shaft cables never stop here.</summary>
        public static unsafe Entity WalkCable(EntityManager em, Entity cell, Entity srcE, out int cells)
        {
            cells = 0; var prev = srcE; var cur = cell; var seen = new HashSet<long>();
            for (int i = 0; i < 2048; i++)
            {
                if (!IsCable(em, cur)) return cur;
                if (!seen.Add(K(cur))) return Entity.Null;
                cells++;
                var b = (byte*)Ecs.RO(em, cur, tiCable); if (b == null) return Entity.Null;
                if (*(float*)(b + 52) >= 1f) return Entity.Null;
                var fwd = *(Entity*)(b + 0x10); var bwd = *(Entity*)(b + 0x18);
                bool fwdIsPrev = fwd.Index == prev.Index && fwd.Version == prev.Version, bwdIsPrev = bwd.Index == prev.Index && bwd.Version == prev.Version;
                Entity nxt = fwdIsPrev ? bwd : bwdIsPrev ? fwd : (!IsNull(fwd) && !seen.Contains(K(fwd)) ? fwd : bwd);
                if (IsNull(nxt) || !Alive(em, nxt)) return Entity.Null;
                prev = cur; cur = nxt;
            }
            return Entity.Null;
        }
        /// <summary>Reads an input: value of the port that feeds it (going through frames), or the value of the port itself.</summary>
        public static bool ReadInput(EntityManager em, Entity input, out float val, out Entity source, out string trail)
        {
            val = 0f; source = Source(em, input, out trail);
            if (!IsNull(source) && Port(em, source, out _, out var vf, out _))
            {
                val = vf;
                if (WirelessChannel(em, source, out var vc, out var tc)) { val = vc; trail += tc; }   // 0.26.15
                return true;
            }
            return false;   // nothing connected (or only frames with no cable at the other end)
        }
        /// <summary>0.26.15 (Andre: the truck's articulation bearing "stopped turning"; the logs since 0.26.8 show its input
        /// always at "t3 v=0" — a game Wireless Transmitter as a RECEIVER, wired straight into the bearing's untyped port). The game only
        /// writes the channel's value into a transmitter port whose direction it has resolved, and with the cable ending on a mod port it
        /// never resolves it by itself (Signal.cs). Here the mod reads the channel on its own, with the game's pairing rules (WirelessWalk).
        /// (A receiver that IS DataOutput does get the value from the game: exactly 1 active sender within 2000 m
        /// [recon/EPC_SCWirelessTransmitter.cs:256-302].)</summary>
        static bool WirelessChannel(EntityManager em, Entity port, out float val, out string trail)
        {
            // 0.31.15: which transmitter sends on the channel is kept like the cable paths (it went through EVERY transmitter of the ship, each
            // with its cable walk, for every read of a wireless-fed input); the value is read live from the sender's output.
            val = 0f; trail = "";
            if (!PathCache) return WirelessWalk(em, port, out val, out trail, out _);
            long k = K(port); float now = Now;
            if (wirelessHits.TryGetValue(k, out var h) && now < h.until && (!h.has || Alive(em, h.outPort)))
            {
                if (!h.has) return false;
                if (RawPort(em, h.outPort, out _, out var vf, out _, out _)) { val = vf; trail = h.trail; return true; }
            }
            bool r = WirelessWalk(em, port, out val, out trail, out var op);
            if (h == null) { if (wirelessHits.Count > 20000) wirelessHits.Clear(); h = new WirelessHit(); wirelessHits[k] = h; }
            h.has = r; h.outPort = op; h.trail = trail; h.until = now + (r ? Ttl(k) : 0.15f);
            return r;
        }
        /// <summary>0.31.26 (item 19): the game's receiver rules [recon/EPC_SCWirelessTransmitter.cs:104, 256-302]: only transmitters with
        /// SCActive count (the job's query), only within 2000 m (distancesq of the LocalToWorld positions < 4e6, strict), only those whose
        /// port [0] is DataInput (runtime +21 == 0) are senders, and the value is copied only when there is EXACTLY one sender — with 0 or
        /// 2+ the game writes nothing and the receiver keeps its last value (here: false, so ReadInput keeps the port's own value). It used to
        /// take the first transmitter of the channel anywhere in the world. A sender's value is read from the output its cable reaches (live,
        /// as before) or, if its cable reaches no output, from its own port, which is what the game copies (raw). A transmitter the game
        /// left without a direction (DataInOut) still counts as a sender when its cable reaches an output — the mod's fallback for a cable
        /// that ends on a mod port.</summary>
        static bool WirelessWalk(EntityManager em, Entity port, out float val, out string trail, out Entity outPort)
        {
            val = 0f; trail = ""; outPort = Entity.Null;
            try
            {
                var owner = Parent(em, port); if (IsNull(owner) || !HasC(em, owner, tiWireless)) return false;
                // only the GAME's Wireless Transmitter: the mod's Shaft Transmitter is a copy of it (same component), with its own channels
                if (JointSystem.KindFromHash(Prefab(em, owner), out _, out _, out _)) return false;
                if (!ScActive(em, owner) || !HasC(em, owner, tiLTW)) return false;   // the game's receiver job only runs on active ones
                int channel = Channel(em, owner); if (channel < 0) return false;
                var here = Position(em, owner);
                int senders = 0; Entity one = Entity.Null; int sender = 0;
                foreach (var t in Transmitters(em))
                {
                    if ((t.Index == owner.Index && t.Version == owner.Version) || Channel(em, t) != channel) continue;
                    if (JointSystem.KindFromHash(Prefab(em, t), out _, out _, out _)) continue;
                    if (!ScActive(em, t) || !HasC(em, t, tiLTW)) continue;
                    var d = Sub(Position(em, t), here); if (Dot(d, d) >= 4000000f) continue;
                    var ps = Ports(em, t); if (ps.Length == 0) continue;
                    var p0 = ps[0];   // the game's job only uses port [0]
                    if (!RawPort(em, p0, out _, out _, out _, out _)) continue;
                    byte rt = RuntimeType(em, p0);
                    if (rt == PortOutput) continue;   // a receiver
                    var f = Source(em, p0, out _);
                    bool toOutput = !IsNull(f) && !(f.Index == p0.Index && f.Version == p0.Version) && RawPort(em, f, out var tf, out _, out _, out _) && tf == PortOutput;
                    if (rt != PortInput && !toOutput) continue;   // not a sender for the game, and no output behind it either
                    senders++; one = toOutput ? f : p0; sender = t.Index;
                    if (senders > 1) break;
                }
                if (senders != 1) return false;
                if (!RawPort(em, one, out _, out var vf, out _, out _)) return false;
                val = vf; outPort = one; trail = " => wireless channel " + channel + " read from the sending transmitter " + sender;
                return true;
            }
            catch (Exception) { }
            return false;
        }
        static unsafe byte RuntimeType(EntityManager em, Entity p)
        {
            try { if (!HasC(em, p, tiPort)) return 255; var b = (byte*)Ecs.RO(em, p, tiPort); return b == null ? (byte)255 : b[21]; } catch (Exception) { return 255; }
        }
        /// <summary>0.31.26: the block has SCActive (the game ticks it; KeepFar removes it beyond 100 km). True when the type is unknown.</summary>
        static TypeIndex tiSCActive; static int scActiveState;   // 0 not looked up, 1 ok, 2 not available
        public static bool ScActive(EntityManager em, Entity e)
        {
            if (scActiveState == 0) { try { tiSCActive = TypeManager.GetTypeIndex(Il2CppType.Of<SCActive>()); scActiveState = 1; } catch (Exception ex) { scActiveState = 2; ModLog.Warn("SCActive not available: " + ex.Message); } }
            return scActiveState != 1 || HasC(em, e, tiSCActive);
        }
        public static bool RawPortPub(EntityManager em, Entity p, out byte kind, out float val, out Entity end, out Entity persist) => RawPort(em, p, out kind, out val, out end, out persist);
        static unsafe bool RawPort(EntityManager em, Entity p, out byte kind, out float val, out Entity end, out Entity persist)
        {
            kind = 255; val = 0f; end = Entity.Null; persist = Entity.Null;
            try
            {
                if (!HasC(em, p, tiPort)) return false;   // 0.31.15: one check, not two
                var b = (byte*)Ecs.RO(em, p, tiPort); if (b == null) return false;
                persist = *(Entity*)b; val = *(float*)(b + 8); end = *(Entity*)(b + 12); kind = b[20];
                if (float.IsNaN(val) || float.IsInfinity(val)) val = 0f;
                return true;
            }
            catch (Exception) { return false; }
        }

        public static unsafe void WritePort(EntityManager em, Entity p, float v)
        {
            try { if (!Alive(em, p)) return; var b = (Ecs.HasC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRW(p, tiPort) : null); if (b != null) *(float*)(b + 8) = v; } catch (Exception) { }
        }
        /// <summary>0.11.0: _index of the port (+22): its position in the prefab's _electricPorts list.</summary>
        public static unsafe int PortIndex(EntityManager em, Entity p)
        {
            try { var b = (Ecs.HasC(em, p, tiPort) ? (byte*)Ecs.RO(em, p, tiPort) : null); return b == null ? -1 : b[22]; } catch (Exception) { return -1; }
        }
        /// <summary>First port of the requested type (0 input, 1 output, 2 power), in the prefab's order.</summary>
        // 0.31.15: a block's ports and their types come from the prefab and never change — the first port of each type is kept (it was
        // the whole port list read again, 3 calls per port, for every piston, recoupler and motor, every frame). Only a found port is kept.
        static readonly Dictionary<long, Entity>[] portOfType = { new Dictionary<long, Entity>(), new Dictionary<long, Entity>(), new Dictionary<long, Entity>(), new Dictionary<long, Entity>() };
        public static Entity PortOfType(EntityManager em, Entity block, byte kind)
        {
            var cache = kind < portOfType.Length ? portOfType[kind] : null; long k = K(block);
            if (cache != null && cache.TryGetValue(k, out var hit)) { if (Alive(em, hit)) return hit; cache.Remove(k); }
            foreach (var p in Ports(em, block)) if (Port(em, p, out var t, out _, out _) && t == kind) { if (cache != null) { if (cache.Count > 50000) cache.Clear(); cache[k] = p; } return p; }
            return Entity.Null;
        }

        // ---- power (SCElectricPower, which the game puts on every component with a red port):
        // +32 _consumptionPerSec, +36 _suppliedState (0 NonSupplied, 1 Deficiency, 2 Supplied), +37 _isElectricityInterrupted
        public static unsafe bool HasPower(EntityManager em, Entity block, out byte state)
        {
            state = 255;
            try
            {
                // 0.31.35: one test (it was HasComponent with the type built in the game, then HasC again: 4 calls for one read)
                var b = (Ecs.HasC(em, block, tiPower) ? (byte*)RO(em, block, tiPower) : null); if (b == null) return false;
                // 0.31.26 (item 36): _suppliedState alone — an interrupted member is already written NonSupplied by UpdateCircuits
                // [recon/Spaceship__part_a.cs:597-601 (lines 3620-3642)], so the extra _isElectricityInterrupted test said nothing new
                state = b[36]; return state == 2;
            }
            catch (Exception) { return false; }
        }
        /// <summary>0.31.26 (item 36): written only when it changes. The game's UpdateCircuits only READS _consumptionPerSec and never
        /// resets it [recon/Spaceship__part_a.cs:597-611], so the value stays; and a writable pointer every frame makes the game wait for
        /// its jobs (the same reason as SlipRings.WriteGhost, 0.31.15).</summary>
        public static unsafe void SetConsumption(EntityManager em, Entity block, float pps)
        {
            try
            {
                if (!HasC(em, block, tiPower)) return;
                var r = (byte*)Ecs.RO(em, block, tiPower); if (r == null || *(float*)(r + 32) == pps) return;
                var b = (byte*)em.GetComponentDataRawRW(block, tiPower); if (b != null) *(float*)(b + 32) = pps;
            }
            catch (Exception) { }
        }

        // ---- the block's prefab (SCPrefab._prefab = hash of the name)
        static bool prefabRaw = true, prefabWarned = false;
        // 0.26.11 (Andre's log, 23/10: with the ship, garage + slip ring + radar/beacon cost ~5 ms per frame — all of them scan the
        // world's blocks every 0.1-0.5 s reading each one's SCPrefab, 3 calls into the game per block). An entity's prefab never
        // changes, and K includes the version (a reused entity is another key): keep what was already read. An entity that no longer exists gives 0.
        static readonly Dictionary<long, ulong> prefabOf = new Dictionary<long, ulong>();
        public static ulong Prefab(EntityManager em, Entity e)
        {
            long k = K(e);
            if (prefabOf.TryGetValue(k, out var storedV)) { try { if (em.Exists(e)) return storedV; } catch (Exception) { } return 0; }
            ulong h = PrefabRead(em, e);
            if (h != 0) { if (prefabOf.Count > 200000) prefabOf.Clear(); prefabOf[k] = h; }
            return h;
        }
        static unsafe ulong PrefabRead(EntityManager em, Entity e)
        {
            try
            {
                // 0.24.0: the ternary returned null when the entity has no SCPrefab and ->_prefab read address 0 right away —
                // a native access violation that not even the catch handles (the process dies). That's what crashed the game in the tutorial menu,
                // scanning clickables that aren't ship blocks (log: "knobs: actionable 21494 owner 21443 — prefab?" as the last line).
                if (prefabRaw)
                {
                    try
                    {
                        var pf = (Ecs.HasC(em, e, tiPrefab) ? (SCPrefab*)Ecs.RO(em, e, tiPrefab) : null);
                        if (pf == null) return 0;
                        return pf->_prefab;
                    }
                    catch (Exception) { prefabRaw = false; }
                }
                return em.GetSharedComponent<SCPrefab>(e)._prefab;
            }
            catch (Exception ex) { if (!prefabWarned) { prefabWarned = true; ModLog.Warn("could not read the block's SCPrefab: " + ex.Message); } return 0; }
        }
        public static ulong HashPrefab(string name) { try { return new SCPrefab(name)._prefab; } catch (Exception ex) { ModLog.Warn("prefab hash " + name + ": " + ex.Message); return 0; } }

        // ---- transforms
        /// <summary>LocalTransform of the block (relative to the parent part).</summary>
        /// <summary>0.12.3: the entity exists AND has the component. Reading/writing RawRO/RW of a component the entity doesn't have (a block being
        /// deleted on a blueprint swap) reads chunk garbage or crashes the game (log: _value = -549571231744 and a crash right after).</summary>
        public static bool HasC(EntityManager em, Entity e, TypeIndex ti)
        {
            if (IsNull(e)) return false;
            if (cOn && ti.Value != 0) return HasCached(em, e, ti, true);
            try { return em.Exists(e) && em.HasComponent(e, CT(ti)); } catch (Exception) { return false; }
        }
        /// <summary>0.31.27 (performance): ComponentType.ReadOnly(ti) built here instead of in the game. Every call into the game through the
        /// interop goes through il2cpp_runtime_invoke and boxes its return value on the game's heap [interop Unity.Entities.dll: EntityManager.
        /// HasComponent]; ReadOnly(ti) was one such call (and one box) inside every HasC. Same value: FromTypeIndex + AccessMode.ReadOnly.</summary>
        public static ComponentType CT(TypeIndex ti) => new ComponentType { TypeIndex = ti, AccessModeType = ComponentType.AccessMode.ReadOnly };
        /// <summary>0.31.27: HasComponent alone — only right after Alive/HasC on the same entity in the same function (no structural change can
        /// happen in between), where HasC would test the existence again.</summary>
        public static bool Has(EntityManager em, Entity e, TypeIndex ti)
        {
            if (cOn && !IsNull(e) && ti.Value != 0) return HasCached(em, e, ti, false);
            try { return em.HasComponent(e, CT(ti)); } catch (Exception) { return false; }
        }
        public static void Require(EntityManager em, Entity e, TypeIndex ti) { if (!HasC(em, e, ti)) throw new InvalidOperationException("entity " + e.Index + " is gone or lost the component"); }
        public static unsafe LocalTransform LT(EntityManager em, Entity e) { Require(em, e, tiLT); return *(LocalTransform*)Ecs.RO(em, e, tiLT); }   // 0.31.15: Require already checked
        public static unsafe float3 Position(EntityManager em, Entity e)
        {
            Require(em, e, tiLTW);
            var ltw = (LocalToWorld*)Ecs.RO(em, e, tiLTW);   // 0.31.15: Require already checked
            return new float3 { x = ltw->Value.c3.x, y = ltw->Value.c3.y, z = ltw->Value.c3.z };
        }
        /// <summary>The block's local axes in the world (columns of LocalToWorld, normalized).</summary>
        public static unsafe void Axes(EntityManager em, Entity e, out float3 ex, out float3 ey, out float3 ez)
        {
            Require(em, e, tiLTW);
            var ltw = (Ecs.HasC(em, e, tiLTW) ? (LocalToWorld*)Ecs.RO(em, e, tiLTW) : null);
            ex = Norm(new float3 { x = ltw->Value.c0.x, y = ltw->Value.c0.y, z = ltw->Value.c0.z });
            ey = Norm(new float3 { x = ltw->Value.c1.x, y = ltw->Value.c1.y, z = ltw->Value.c1.z });
            ez = Norm(new float3 { x = ltw->Value.c2.x, y = ltw->Value.c2.y, z = ltw->Value.c2.z });
        }
        public static unsafe float3 CenterOfMass(EntityManager em, Entity part)
        {
            Require(em, part, tiLT); Require(em, part, tiMass);
            var lt = (Ecs.HasC(em, part, tiLT) ? (LocalTransform*)Ecs.RO(em, part, tiLT) : null); var m = (Ecs.HasC(em, part, tiMass) ? (PhysicsMass*)Ecs.RO(em, part, tiMass) : null);
            float sc = lt->Scale == 0f ? 1f : lt->Scale;
            return Add(lt->Position, Rot(lt->Rotation, Mul(m->Transform.pos, sc)));
        }
        public static unsafe float Mass(EntityManager em, Entity part) { Require(em, part, tiMass); var m = (Ecs.HasC(em, part, tiMass) ? (PhysicsMass*)Ecs.RO(em, part, tiMass) : null); return m->InverseMass > 0f ? 1f / m->InverseMass : 0f; }
        /// <summary>0.7.9: the block's pose in the world from PHYSICS (LocalTransform of the parent part ∘ LocalTransform of the block), without relying on
        /// LocalToWorld — which on the 1st frame after launching/rebuilding parts may lag behind (the fit was born in the wrong place and the
        /// wheel got stuck far from the axle).</summary>
        public static unsafe void Pose(EntityManager em, Entity block, out float3 pos, out quaternion rot)
        {
            Require(em, block, tiLT);
            var lb = (Ecs.HasC(em, block, tiLT) ? (LocalTransform*)Ecs.RO(em, block, tiLT) : null); var parent = Parent(em, block);
            if (IsNull(parent) || !Ecs.Has(em, parent, tiLT)) { pos = lb->Position; rot = lb->Rotation; return; }
            var lp = (Ecs.HasC(em, parent, tiLT) ? (LocalTransform*)Ecs.RO(em, parent, tiLT) : null); float s = lp->Scale == 0f ? 1f : lp->Scale;
            pos = Add(lp->Position, Rot(lp->Rotation, Mul(lb->Position, s))); rot = QMul(lp->Rotation, lb->Rotation);
        }
        public static quaternion QMul(quaternion a, quaternion b)
        {
            float4 p = a.value, q = b.value;
            return new quaternion { value = new float4 { x = p.w * q.x + p.x * q.w + p.y * q.z - p.z * q.y, y = p.w * q.y - p.x * q.z + p.y * q.w + p.z * q.x, z = p.w * q.z + p.x * q.y - p.y * q.x + p.z * q.w, w = p.w * q.w - p.x * q.x - p.y * q.y - p.z * q.z } };
        }
        public static readonly float3 UX = new float3 { x = 1f }, UY = new float3 { y = 1f }, UZ = new float3 { z = 1f };

        /// <summary>Joint frame in the part's local space.</summary>
        public static unsafe BodyFrame Frame(EntityManager em, Entity part, float3 worldPoint, float3 worldAxis, float3 worldPerp)
        {
            Require(em, part, tiLT);
            var lt = (Ecs.HasC(em, part, tiLT) ? (LocalTransform*)Ecs.RO(em, part, tiLT) : null);
            var qi = Inv(lt->Rotation); float s = lt->Scale == 0f ? 1f : lt->Scale;
            var bf = new BodyFrame();
            bf.Position = Div(Rot(qi, Sub(worldPoint, lt->Position)), s);
            bf.Axis = Rot(qi, worldAxis); bf.PerpendicularAxis = Rot(qi, worldPerp);
            return bf;
        }

        // ---- math (without calling Unity.Mathematics through il2cpp)
        public static float3 Rot(quaternion q, float3 v)
        {
            float qx = q.value.x, qy = q.value.y, qz = q.value.z, qw = q.value.w;
            float cx = qy * v.z - qz * v.y + qw * v.x, cy = qz * v.x - qx * v.z + qw * v.y, cz = qx * v.y - qy * v.x + qw * v.z;
            return new float3 { x = v.x + 2f * (qy * cz - qz * cy), y = v.y + 2f * (qz * cx - qx * cz), z = v.z + 2f * (qx * cy - qy * cx) };
        }
        public static quaternion Inv(quaternion q) { var r = q; r.value.x = -q.value.x; r.value.y = -q.value.y; r.value.z = -q.value.z; return r; }
        public static float3 Sub(float3 a, float3 b) => new float3 { x = a.x - b.x, y = a.y - b.y, z = a.z - b.z };
        public static float3 Add(float3 a, float3 b) => new float3 { x = a.x + b.x, y = a.y + b.y, z = a.z + b.z };
        public static float3 Mul(float3 a, float s) => new float3 { x = a.x * s, y = a.y * s, z = a.z * s };
        public static float3 Div(float3 a, float s) => new float3 { x = a.x / s, y = a.y / s, z = a.z / s };
        public static float Dot(float3 a, float3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static float Len(float3 v) => (float)System.Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        public static float Dist(float3 a, float3 b) => Len(Sub(a, b));
        public static float3 Norm(float3 v) { float c = Len(v); return c < 1e-6f ? v : Div(v, c); }
        public static float3 Cross(float3 a, float3 b) => new float3 { x = a.y * b.z - a.z * b.y, y = a.z * b.x - a.x * b.z, z = a.x * b.y - a.y * b.x };
        public static string F3(float3 v) => v.x.ToString("F2") + "," + v.y.ToString("F2") + "," + v.z.ToString("F2");
        public static float3 Perp(float3 axis)
        {
            var r = System.Math.Abs(axis.y) < 0.9f ? new float3 { x = 0, y = 1, z = 0 } : new float3 { x = 1, y = 0, z = 0 };
            return Norm(Cross(axis, r));
        }
    }

    internal static class ModLog
    {
        /// <summary>0.24.0: breadcrumbs for native crashes. A crash in coreclr leaves no exception or stack in the mod's log — the only
        /// way to know WHERE it happened is to mark each step and look at the last line written (the file has AutoFlush, so the last
        /// line really is the last step that ran). Only on with [Debug] trace, because it floods the log.</summary>
        public static bool Tracing;
        public static void Trace(string location) { if (Tracing) Info("trace: " + location); }

        public static BepInEx.Logging.ManualLogSource Source;
        public static void Info(string s) { Source?.LogInfo(s); }
        public static void Warn(string s) { Source?.LogWarning(s); }
        public static void Error(string s) { Source?.LogError(s); }
    }
}
