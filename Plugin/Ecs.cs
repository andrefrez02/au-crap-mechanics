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
        public static IntPtr PtrTransform, PtrPresentation, PtrBindsGroup, PtrTickPre, PtrTickPost, PtrPhysicsPre; static bool ptrsOk;   // 0.19.13: + PhysicsPreGroup (100 km rule)   // 0.19.6: + tick groups of the components (LRT)
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
                }
                ptrsOk = true;
                ModLog.Info("system groups: TransformSystemGroup " + (PtrTransform == IntPtr.Zero ? "NOT found" : "ok") + ", PresentationSystemGroup " + (PtrPresentation == IntPtr.Zero ? "NOT found" : "ok") + ", CustomRenderPipelineGroup (seat key bindings) " + (PtrBindsGroup == IntPtr.Zero ? "NOT found" : "ok") + ", SpaceshipComponentsTickPre/Post " + (PtrTickPre == IntPtr.Zero || PtrTickPost == IntPtr.Zero ? "NOT found" : "ok") + ", PhysicsPreGroup " + (PtrPhysicsPre == IntPtr.Zero ? "NOT found" : "ok") + " (" + n + " managed systems)");
            }
            catch (Exception ex) { ptrsOk = true; ModLog.Warn("system group pointers: " + ex.Message); }
        }
        public static void ResetPointers() { ptrsOk = false; PtrTransform = IntPtr.Zero; PtrPresentation = IntPtr.Zero; PtrBindsGroup = IntPtr.Zero; PtrTickPre = IntPtr.Zero; PtrTickPost = IntPtr.Zero; PtrPhysicsPre = IntPtr.Zero; Far.Restart(); }

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
        public static List<Entity> Parts(EntityManager em) => QueryList(qParts);
        /// <summary>Blocks with an SCDecouplingJoint buffer (the game's decouplers and ours, which are copies of the decoupler).</summary>
        public static List<Entity> DecouplerBlocks(EntityManager em) => QueryList(qBlocks);
        /// <summary>Every ship component (SCPrefab + Parent + LocalToWorld) — to find bearings and motors, which aren't copies of the decoupler.</summary>
        public static List<Entity> Components(EntityManager em) => QueryList(qComps);
        /// <summary>Wireless Transmitter: SCTypeWirelessTransmitter._currentChannel (+32). -1 if it isn't a transmitter.</summary>
        public static unsafe int Channel(EntityManager em, Entity e)
        {
            try { if (IsNull(e) || !em.HasComponent(e, ComponentType.ReadOnly(tiWireless))) return -1; return *(int*)((Ecs.HasC(em, e, tiWireless) ? (byte*)em.GetComponentDataRawRO(e, tiWireless) : null) + 32); }
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
                        var p = (Ecs.HasC(em, e, tiParent) ? (Parent*)em.GetComponentDataRawRO(e, tiParent) : null);
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
        public static unsafe bool HasPTM(EntityManager em, Entity e) => ptmOk && em.HasComponent(e, ComponentType.ReadOnly(tiPTM));
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
        public static bool IsGameDecoupler(EntityManager em, Entity e) => em.HasComponent(e, ComponentType.ReadOnly(tiDecType));
        /// <summary>0.11.1: the block's SCGuid (16 bytes) in hex, "" if it has none. It's the id the game keeps in the save/blueprint —
        /// used as a key to keep our own things per block (text of the generic blocks' labels).</summary>
        public static unsafe string Guid(EntityManager em, Entity e)
        {
            try
            {
                if (!guidOk) { guidOk = true; tiGuid = TypeManager.GetTypeIndex(Il2CppType.Of<SCGuid>()); }
                if (tiGuid.Value == 0 || !em.HasComponent(e, ComponentType.ReadOnly(tiGuid))) return "";
                var b = (Ecs.HasC(em, e, tiGuid) ? (byte*)em.GetComponentDataRawRO(e, tiGuid) : null); if (b == null) return "";
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
        public static bool Alive(EntityManager em, Entity e) { try { return !IsNull(e) && em.Exists(e); } catch (Exception) { return false; } }
        public static bool IsPart(EntityManager em, Entity e)
        {
            try { return Alive(em, e) && em.HasComponent(e, ComponentType.ReadOnly(tiSPData)) && em.HasComponent(e, ComponentType.ReadOnly(tiMass)) && em.HasComponent(e, ComponentType.ReadOnly(tiPWI)); }
            catch (Exception) { return false; }
        }
        public static unsafe Entity Parent(EntityManager em, Entity e)
        {
            try { if (!em.HasComponent(e, ComponentType.ReadOnly(tiParent))) return Entity.Null; return ((Ecs.HasC(em, e, tiParent) ? (Parent*)em.GetComponentDataRawRO(e, tiParent) : null))->Value; }
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
            var s = (Ecs.HasC(em, sings[0], tiSing) ? (SpaceshipSingleton*)em.GetComponentDataRawRO(sings[0], tiSing) : null);
            var ul = (UnsafeListHeader*)s->_joints; if (ul == null || ul->Ptr == null) return null;
            n = ul->Length; if (n < 0 || n > 1000000) { n = 0; return null; }
            return (SpaceshipJoint*)ul->Ptr;
        }

        // ---- signal of the block's input port (first port of the SpaceshipElectricPortRef buffer)
        /// <summary>Signal of the block's port [0]. SpaceshipElectricPort.DataValue() (game IL) = `_portValue` (float at offset 8)
        /// if it is <= a maximum, otherwise 0. Read raw so as not to call a game struct method on a chunk pointer.
        /// `reason` says why it wasn't read (to log once).</summary>
        public static unsafe bool Signal(EntityManager em, Entity block, out float v, out string reason)
        {
            v = 0f; reason = null;
            try
            {
                if (!em.HasComponent(block, ComponentType.ReadOnly(tiPortRef))) { reason = "block has no SpaceshipElectricPortRef buffer"; return false; }
                var ports = BufferEntities(em, block, tiPortRef); if (ports.Length == 0) { reason = "SpaceshipElectricPortRef buffer is empty"; return false; }
                var p = PortOfType(em, block, PortInput); if (IsNull(p)) p = ports[0];
                if (!Alive(em, p)) { reason = "port " + p.Index + " does not exist"; return false; }
                if (!em.HasComponent(p, ComponentType.ReadOnly(tiPort))) { reason = "port " + p.Index + " has no SpaceshipElectricPort"; return false; }
                var port = (Ecs.HasC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRO(p, tiPort) : null);
                if (port == null) { reason = "port GetComponentDataRawRO returned null"; return false; }
                v = *(float*)(port + Layout.PortValue);
                if (float.IsNaN(v) || float.IsInfinity(v) || v > 1e6f) v = 0f;
                return true;
            }
            catch (Exception ex) { reason = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        /// <summary>Port diagnostics, once per block: how many ports, which entity, raw value and what DataValue()/DataValue01() return.</summary>
        public static unsafe string SignalDiagnostic(EntityManager em, Entity block)
        {
            try
            {
                if (!em.HasComponent(block, ComponentType.ReadOnly(tiPortRef))) return "no SpaceshipElectricPortRef buffer";
                var ports = BufferEntities(em, block, tiPortRef);
                var sb = new System.Text.StringBuilder(); sb.Append(ports.Length).Append(" port(s):");
                for (int i = 0; i < ports.Length && i < 4; i++)
                {
                    var p = ports[i]; sb.Append(" [").Append(i).Append(": ").Append(p.Index).Append(":").Append(p.Version);
                    if (!Alive(em, p)) { sb.Append(" dead]"); continue; }
                    if (!em.HasComponent(p, ComponentType.ReadOnly(tiPort))) { sb.Append(" no SpaceshipElectricPort]"); continue; }
                    var port = (Ecs.HasC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRO(p, tiPort) : null);
                    sb.Append(" value=").Append((*(float*)(port + Layout.PortValue)).ToString("F3")).Append(" bytes=");
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
            try { if (!em.HasComponent(block, ComponentType.ReadOnly(tiPortRef))) return new Entity[0]; return BufferEntities(em, block, tiPortRef); }
            catch (Exception) { return new Entity[0]; }
        }
        /// <summary>0.24.0: the type that counts in practice. _setupType (+20) is the prefab's; the game resolves _runtimeType (+21) from
        /// it and the runtime one is what rules when passing values. On a DataInOut (3) or Unset (5) port the setup doesn't give the direction —
        /// if the runtime one is already resolved to input or output, it's that one. (Without this SlipRings saw "no output on either side" on a
        /// receiving Wireless Transmitter, which is DataInOut in the prefab and DataOutput at run time, and didn't bridge.)</summary>
        public static unsafe byte EffectiveType(EntityManager em, Entity p)
        {
            try
            {
                if (!Alive(em, p) || !em.HasComponent(p, ComponentType.ReadOnly(tiPort))) return 255;
                var b = (HasC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRO(p, tiPort) : null); if (b == null) return 255;
                byte setup = b[Layout.PortSetup], run = b[Layout.PortRuntime];
                if ((setup == 3 || setup == 5) && (run == PortInput || run == PortOutput)) return run;
                return setup;
            }
            catch (Exception) { return 255; }
        }
        public static unsafe bool Port(EntityManager em, Entity p, out byte kind, out float val, out Entity otherEnd)
        {
            kind = 255; val = 0f; otherEnd = Entity.Null;
            try
            {
                if (!Alive(em, p) || !em.HasComponent(p, ComponentType.ReadOnly(tiPort))) return false;
                var b = (Ecs.HasC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRO(p, tiPort) : null); if (b == null) return false;
                val = *(float*)(b + Layout.PortValue); if (float.IsNaN(val) || float.IsInfinity(val) || val > 1e6f || val < -1e6f) val = 0f;
                otherEnd = *(Entity*)(b + Layout.PortEnd); kind = b[Layout.PortSetup];
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
                if (IsCable(em, target)) { int cells; var end2 = WalkCable(em, target, cur, out cells); sb.Append(" -> cable(").Append(cells).Append(" cell(s))"); if (IsNull(end2) || (end2.Index == cur.Index && end2.Version == cur.Version)) { sb.Append(" -> dead end"); break; } target = end2; }
                if (!RawPort(em, target, out var t2, out var v2, out _, out _)) { sb.Append(" -> ").Append(target.Index).Append("(not a port)"); break; }
                var owner = Parent(em, target);
                bool frame = !IsNull(owner) && Alive(em, owner) && em.HasComponent(owner, ComponentType.ReadOnly(tiFramePorts));
                sb.Append(" -> ").Append(target.Index).Append("(t").Append(t2).Append(frame ? " frame " + owner.Index : "").Append(" v=").Append(v2.ToString("F2")).Append(")");
                if (t2 == PortOutput || !frame) { trail = sb.ToString(); return target; }
                var ps = Ports(em, owner); int i = Array.FindIndex(ps, x => x.Index == target.Index && x.Version == target.Version);
                if (i < 0 || ps.Length < 2) { sb.Append(" (port outside the frame's buffer)"); break; }
                cur = ps[(i + ps.Length / 2) % ps.Length];
                sb.Append(" => pair ").Append(cur.Index);
            }
            trail = sb.ToString(); return Entity.Null;
        }
        public static bool IsCable(EntityManager em, Entity e) { try { return tiCable.Value != 0 && Alive(em, e) && em.HasComponent(e, ComponentType.ReadOnly(tiCable)); } catch (Exception) { return false; } }
        /// <summary>0.19.10: follows a cable from the cell `cell` (coming from `srcE`) to the entity that isn't a cell (the port at the other end).
        /// Entity.Null if the cable ends in nothing.</summary>
        public static unsafe Entity WalkCable(EntityManager em, Entity cell, Entity srcE, out int cells)
        {
            cells = 0; var prev = srcE; var cur = cell; var seen = new HashSet<long>();
            for (int i = 0; i < 2048; i++)
            {
                if (!IsCable(em, cur)) return cur;
                if (!seen.Add(K(cur))) return Entity.Null;
                cells++;
                var b = (byte*)em.GetComponentDataRawRO(cur, tiCable); if (b == null) return Entity.Null;
                var fwd = *(Entity*)(b + Layout.CellFwd); var bwd = *(Entity*)(b + Layout.CellBwd);
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
        /// never resolves it (Signal.cs; not even forcing DataOutput makes the value arrive). Here the mod reads the channel on its own: finds the transmitter
        /// that SENDS on that channel (its port leads, through the cable, to an output) and returns that output's value.</summary>
        static bool WirelessChannel(EntityManager em, Entity port, out float val, out string trail)
        {
            val = 0f; trail = "";
            try
            {
                var owner = Parent(em, port); if (IsNull(owner) || !HasC(em, owner, tiWireless)) return false;
                // only the GAME's Wireless Transmitter: the mod's Shaft Transmitter is a copy of it (same component), with its own channels
                if (JointSystem.KindFromHash(Prefab(em, owner), out _, out _, out _)) return false;
                int channel = Channel(em, owner); if (channel < 0) return false;
                foreach (var t in Transmitters(em))
                {
                    if ((t.Index == owner.Index && t.Version == owner.Version) || Channel(em, t) != channel) continue;
                    if (JointSystem.KindFromHash(Prefab(em, t), out _, out _, out _)) continue;
                    foreach (var p in Ports(em, t))
                    {
                        var f = Source(em, p, out _);
                        if (IsNull(f) || (f.Index == p.Index && f.Version == p.Version)) continue;
                        if (!RawPort(em, f, out var tf, out var vf, out _, out _) || tf != PortOutput) continue;
                        val = vf; trail = " => wireless channel " + channel + " read from the sending transmitter " + t.Index;
                        return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }
        public static bool RawPortPub(EntityManager em, Entity p, out byte kind, out float val, out Entity end, out Entity persist) => RawPort(em, p, out kind, out val, out end, out persist);
        static unsafe bool RawPort(EntityManager em, Entity p, out byte kind, out float val, out Entity end, out Entity persist)
        {
            kind = 255; val = 0f; end = Entity.Null; persist = Entity.Null;
            try
            {
                if (!Alive(em, p) || !em.HasComponent(p, ComponentType.ReadOnly(tiPort))) return false;
                var b = (Ecs.HasC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRO(p, tiPort) : null); if (b == null) return false;
                persist = *(Entity*)(b + Layout.PortPersist); val = *(float*)(b + Layout.PortValue); end = *(Entity*)(b + Layout.PortEnd); kind = b[Layout.PortSetup];
                if (float.IsNaN(val) || float.IsInfinity(val)) val = 0f;
                return true;
            }
            catch (Exception) { return false; }
        }

        public static unsafe void WritePort(EntityManager em, Entity p, float v)
        {
            try { if (!Alive(em, p)) return; var b = (Ecs.HasC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRW(p, tiPort) : null); if (b != null) *(float*)(b + Layout.PortValue) = v; } catch (Exception) { }
        }
        /// <summary>0.11.0: _index of the port: its position in the prefab's _electricPorts list.</summary>
        public static unsafe int PortIndex(EntityManager em, Entity p)
        {
            try { var b = (Ecs.HasC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRO(p, tiPort) : null); return b == null ? -1 : b[Layout.PortIndex]; } catch (Exception) { return -1; }
        }
        /// <summary>First port of the requested type (0 input, 1 output, 2 power), in the prefab's order.</summary>
        public static Entity PortOfType(EntityManager em, Entity block, byte kind)
        {
            foreach (var p in Ports(em, block)) if (Port(em, p, out var t, out _, out _) && t == kind) return p;
            return Entity.Null;
        }

        // ---- power (SCElectricPower, which the game puts on every component with a red port):
        // _consumptionPerSec, _suppliedState (0 NonSupplied, 1 Deficiency, 2 Supplied), _isElectricityInterrupted
        public static unsafe bool HasPower(EntityManager em, Entity block, out byte state)
        {
            state = 255;
            try
            {
                if (!em.HasComponent(block, ComponentType.ReadOnly(tiPower))) return false;
                var b = (Ecs.HasC(em, block, tiPower) ? (byte*)em.GetComponentDataRawRO(block, tiPower) : null); if (b == null) return false;
                state = b[Layout.PowerState]; return state == 2 && b[Layout.PowerCut] == 0;
            }
            catch (Exception) { return false; }
        }
        public static unsafe void SetConsumption(EntityManager em, Entity block, float pps)
        {
            try { if (!em.HasComponent(block, ComponentType.ReadOnly(tiPower))) return; var b = (Ecs.HasC(em, block, tiPower) ? (byte*)em.GetComponentDataRawRW(block, tiPower) : null); if (b != null) *(float*)(b + Layout.PowerCons) = pps; } catch (Exception) { }
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
                        var pf = (Ecs.HasC(em, e, tiPrefab) ? (SCPrefab*)em.GetComponentDataRawRO(e, tiPrefab) : null);
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
        public static bool HasC(EntityManager em, Entity e, TypeIndex ti) { try { return !IsNull(e) && em.Exists(e) && em.HasComponent(e, ComponentType.ReadOnly(ti)); } catch (Exception) { return false; } }
        public static void Require(EntityManager em, Entity e, TypeIndex ti) { if (!HasC(em, e, ti)) throw new InvalidOperationException("entity " + e.Index + " is gone or lost the component"); }
        public static unsafe LocalTransform LT(EntityManager em, Entity e) { Require(em, e, tiLT); return *(Ecs.HasC(em, e, tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(e, tiLT) : null); }
        public static unsafe float3 Position(EntityManager em, Entity e)
        {
            Require(em, e, tiLTW);
            var ltw = (Ecs.HasC(em, e, tiLTW) ? (LocalToWorld*)em.GetComponentDataRawRO(e, tiLTW) : null);
            return new float3 { x = ltw->Value.c3.x, y = ltw->Value.c3.y, z = ltw->Value.c3.z };
        }
        /// <summary>The block's local axes in the world (columns of LocalToWorld, normalized).</summary>
        public static unsafe void Axes(EntityManager em, Entity e, out float3 ex, out float3 ey, out float3 ez)
        {
            Require(em, e, tiLTW);
            var ltw = (Ecs.HasC(em, e, tiLTW) ? (LocalToWorld*)em.GetComponentDataRawRO(e, tiLTW) : null);
            ex = Norm(new float3 { x = ltw->Value.c0.x, y = ltw->Value.c0.y, z = ltw->Value.c0.z });
            ey = Norm(new float3 { x = ltw->Value.c1.x, y = ltw->Value.c1.y, z = ltw->Value.c1.z });
            ez = Norm(new float3 { x = ltw->Value.c2.x, y = ltw->Value.c2.y, z = ltw->Value.c2.z });
        }
        public static unsafe float3 CenterOfMass(EntityManager em, Entity part)
        {
            Require(em, part, tiLT); Require(em, part, tiMass);
            var lt = (Ecs.HasC(em, part, tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(part, tiLT) : null); var m = (Ecs.HasC(em, part, tiMass) ? (PhysicsMass*)em.GetComponentDataRawRO(part, tiMass) : null);
            float sc = lt->Scale == 0f ? 1f : lt->Scale;
            return Add(lt->Position, Rot(lt->Rotation, Mul(m->Transform.pos, sc)));
        }
        public static unsafe float Mass(EntityManager em, Entity part) { Require(em, part, tiMass); var m = (Ecs.HasC(em, part, tiMass) ? (PhysicsMass*)em.GetComponentDataRawRO(part, tiMass) : null); return m->InverseMass > 0f ? 1f / m->InverseMass : 0f; }
        /// <summary>0.7.9: the block's pose in the world from PHYSICS (LocalTransform of the parent part ∘ LocalTransform of the block), without relying on
        /// LocalToWorld — which on the 1st frame after launching/rebuilding parts may lag behind (the fit was born in the wrong place and the
        /// wheel got stuck far from the axle).</summary>
        public static unsafe void Pose(EntityManager em, Entity block, out float3 pos, out quaternion rot)
        {
            Require(em, block, tiLT);
            var lb = (Ecs.HasC(em, block, tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(block, tiLT) : null); var parent = Parent(em, block);
            if (IsNull(parent) || !em.HasComponent(parent, ComponentType.ReadOnly(tiLT))) { pos = lb->Position; rot = lb->Rotation; return; }
            var lp = (Ecs.HasC(em, parent, tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(parent, tiLT) : null); float s = lp->Scale == 0f ? 1f : lp->Scale;
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
            var lt = (Ecs.HasC(em, part, tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(part, tiLT) : null);
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
