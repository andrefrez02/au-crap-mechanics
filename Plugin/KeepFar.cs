using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CrapMechanics
{
    /// <summary>0.19.13 (Andre, 03/10: "when a loose object has a beacon, keep running its physics"). The game has the
    /// `SPActiveUpdateSystem` (PhysicsPreGroup, OrderFirst): every part (SPData + LocalTransform + Child) more than 100 km from the center of the
    /// universe (|LocalTransform.Position|² > 1E+10, the center is the player) loses `SPActive` + `PhysicsWorldIndex` and its blocks
    /// lose `SCActive`; whatever comes back within 100 km gets `SPActive` and `SCActive` back (the game does NOT give back PhysicsWorldIndex).
    /// Without SPActive/SCActive the physics stops, and so does the tick of the camera, the transmitter, the thruster — that's what killed the probe's camera at 61 s
    /// of the 03/10 video (it wasn't the atmosphere or the power).
    ///
    /// Here the game's system is TURNED OFF (SystemState.Enabled = false, through the state pointer) and the same rule runs in the
    /// PhysicsPreGroup prefix (same place, same math), with one exception: a part that has a powered SC_Beacon of the mod keeps being simulated at
    /// any distance (and, if the game had already turned it off, it's turned back on: SPActive + PhysicsWorldIndex 0 + SCActive on the blocks).
    /// Config [Beacon] keep_simulated_far. If it can't turn off the game's system, it touches nothing (warning in the log).</summary>
    internal static class Far
    {
        public static bool Enabled = true;
        const float LIMIT2 = 1E+10f;   // (100 km)²

        static TypeIndex tiSPActive, tiSCActive, tiChild; static EntityQuery qPieces; static bool tiOk, failed;
        static World worldOff; static IntPtr gameState; static bool disabled; static float tCheck;
        static readonly Dictionary<long, Entity> exemptSet = new Dictionary<long, Entity>();   // part -> powered beacon
        static readonly Dictionary<long, byte> situation = new Dictionary<long, byte>();       // part -> 0 near, 1 far and off, 2 far and kept
        static int errors; static bool netNoted;

        static bool Prepare(EntityManager em)
        {
            if (tiOk) return true; if (failed) return false;
            try
            {
                tiSPActive = TypeManager.GetTypeIndex(Il2CppType.Of<SPActive>());
                tiSCActive = TypeManager.GetTypeIndex(Il2CppType.Of<SCActive>());
                tiChild = TypeManager.GetTypeIndex(Il2CppType.Of<Child>());
                var a = new Il2CppStructArray<ComponentType>(3);
                a[0] = ComponentType.ReadOnly(Ecs.tiSPData); a[1] = ComponentType.ReadOnly(Ecs.tiLT); a[2] = ComponentType.ReadOnly(tiChild);
                qPieces = em.CreateEntityQuery(a);
                tiOk = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("far parts: setup failed (" + ex.Message + ") — the game keeps its 100 km rule"); return false; }
        }

        /// <summary>Turns off the game's SPActiveUpdateSystem in this World. Interop: WorldUnmanaged.ResolveSystemState(handle) returns the
        /// raw pointer of the SystemState (a method that returns a pointer — called by reflection, like GetComponentDataRawRO); the
        /// native set_Enabled is called through il2cpp_runtime_invoke with that pointer as `this` (unboxed struct, as the
        /// interop itself does with il2cpp_object_unbox).</summary>
        static unsafe bool TurnOffGame(World w)
        {
            if (disabled && worldOff == w && tCheck < 10f) return true;
            try
            {
                // the state pointer is resolved again every 10 s (and always after a Restart): if the game recreated the system,
                // the old pointer isn't touched
                var wu = w.Unmanaged;
                var h = wu.GetExistingUnmanagedSystem(Il2CppType.Of<SPActiveUpdateSystem>());
                var m = typeof(WorldUnmanaged).GetMethod("ResolveSystemState", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(SystemHandle) }, null);
                if (m == null) throw new InvalidOperationException("WorldUnmanaged.ResolveSystemState not found in the interop");
                object r = m.Invoke(wu, new object[] { h });
                IntPtr state = (IntPtr)System.Reflection.Pointer.Unbox(r);
                if (state == IntPtr.Zero) throw new InvalidOperationException("SystemState is null");
                bool before = ReadEnabled(state);
                bool sameOne = disabled && state == gameState && worldOff == w;
                if (!before && sameOne) { tCheck = 0f; return true; }   // checked: still off
                WriteEnabled(state, false);
                if (ReadEnabled(state)) throw new InvalidOperationException("Enabled still true after writing false");
                if (sameOne) ModLog.Warn("far parts: the game switched its SPActiveUpdateSystem back on — switched off again");
                else ModLog.Info("far parts: the game's SPActiveUpdateSystem is off (was " + (before ? "on" : "off") + (disabled ? "; system recreated" : "") + ") — the 100 km rule now runs in the mod, with powered beacons exempt");
                gameState = state; disabled = true; worldOff = w; tCheck = 0f;
                return true;
            }
            catch (Exception ex) { failed = true; disabled = false; gameState = IntPtr.Zero; ModLog.Warn("far parts: could not switch the game's SPActiveUpdateSystem off (" + ex.Message + ") — the game keeps its 100 km rule"); return false; }
        }
        static IntPtr NativeMethod(string field)
        {
            var f = typeof(SystemState).GetField(field, BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) throw new InvalidOperationException("SystemState." + field + " not found in the interop");
            return (IntPtr)f.GetValue(null);
        }
        static unsafe bool ReadEnabled(IntPtr state)
        {
            IntPtr exc = IntPtr.Zero;
            IntPtr ret = IL2CPP.il2cpp_runtime_invoke(NativeMethod("NativeMethodInfoPtr_get_Enabled_Public_get_Boolean_0"), state, null, ref exc);
            Il2CppException.RaiseExceptionIfNecessary(exc);
            return *(bool*)IL2CPP.il2cpp_object_unbox(ret);
        }
        static unsafe void WriteEnabled(IntPtr state, bool v)
        {
            IntPtr* args = stackalloc IntPtr[1]; args[0] = (IntPtr)(&v); IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(NativeMethod("NativeMethodInfoPtr_set_Enabled_Public_set_Void_Boolean_0"), state, (void**)args, ref exc);
            Il2CppException.RaiseExceptionIfNecessary(exc);
        }
        /// <summary>Gives the system back to the game (config off / exit).</summary>
        public static void RelinkGame()
        {
            if (!disabled) return;
            try { if (worldOff != null && worldOff.IsCreated && gameState != IntPtr.Zero) { WriteEnabled(gameState, true); ModLog.Info("far parts: the game's SPActiveUpdateSystem is back on"); } }
            catch (Exception ex) { ModLog.Warn("far parts: could not switch the game's system back on (" + ex.Message + ")"); }
            disabled = false; gameState = IntPtr.Zero; worldOff = null;
        }
        /// <summary>World unloaded/swapped: the system's state died with it.</summary>
        public static void Restart() { disabled = false; gameState = IntPtr.Zero; worldOff = null; situation.Clear(); exemptSet.Clear(); }

        /// <summary>PhysicsPreGroup prefix (where the game's system ran, OrderFirst): the 100 km rule, with the beacon exception.</summary>
        /// <summary>0.19.13 (replay): during Replay playback the world is frozen and Recenter moves the center of the universe to the replay
        /// camera — the live ship ends up thousands of km from the center and, at the moments the fixed step gets to run (3x in the middle of the replay),
        /// lost SCActive: the camera tick that Replay runs stopped drawing (monitor showing static in the 03/10 video). During playback
        /// nobody is turned off, and whatever is off is turned back on (also called from Update, since the fixed step doesn't run).</summary>
        public static unsafe void Step(EntityManager em, bool playback = false)
        {
            if (!Enabled || failed || Unloading.Pending) return;
            // 0.28.0: on a network the 100 km rule goes back to the game. The mod's copy measures from YOUR position only; on the server
            // that would switch off the parts next to another player (the game's own system may count every player — it's native code).
            if (Multi.Networked(em))
            {
                if (!netNoted) { netNoted = true; ModLog.Info("far parts: on a network — the game's own 100 km rule is used (beacons are not exempt in multiplayer)"); }
                RelinkGame(); return;
            }
            netNoted = false;
            if (!Prepare(em)) return;
            var w = World.DefaultGameObjectInjectionWorld; if (w == null || !w.IsCreated) return;
            if (!playback) tCheck += UnityEngine.Time.fixedDeltaTime;
            if (!TurnOffGame(w)) return;
            try
            {

                exemptSet.Clear();
                foreach (var b in Couplers.Beacons())
                {
                    if (b.noPower || !Ecs.Alive(em, b.block)) continue;
                    var p = Ecs.Parent(em, b.block); if (Ecs.IsNull(p) || !Ecs.Alive(em, p)) continue;
                    exemptSet[Ecs.K(p)] = b.block;
                }
                var pieces = Ecs.QueryList(qPieces);
                foreach (var e in pieces)
                {
                    if (!Ecs.HasC(em, e, Ecs.tiLT)) continue;
                    bool isActive = em.HasComponent(e, ComponentType.ReadOnly(tiSPActive));
                    var lt = (LocalTransform*)em.GetComponentDataRawRO(e, Ecs.tiLT);
                    float d2 = math.lengthsq(lt->Position);
                    bool near = d2 < LIMIT2;                       // the game: near <=> !(1E10 < d²) && (1E10 − d² != 0)
                    long k = Ecs.K(e); bool exempt = exemptSet.TryGetValue(k, out var beacon);
                    if (playback)
                    {
                        if (!isActive) { Activate(em, e, true); Situation(k, 3, e, d2, beacon); }
                        continue;
                    }
                    if (isActive && !near && !exempt) { Deactivate(em, e); Situation(k, 1, e, d2, beacon); }
                    else if (!isActive && near) { Activate(em, e, false); Situation(k, 0, e, d2, beacon); }
                    else if (!isActive && exempt) { Activate(em, e, true); Situation(k, 2, e, d2, beacon); }   // the game had already turned it off (the beacon was turned on later / the mod loaded later)
                    else Situation(k, near ? (byte)0 : exempt ? (byte)2 : (byte)1, e, d2, beacon);
                }
            }
            catch (Exception ex) { if (errors++ < 3) ModLog.Warn("far parts: " + ex.Message); }
        }

        static void Situation(long k, byte s, Entity e, float d2, Entity beacon)
        {
            situation.TryGetValue(k, out var before);
            if (before == s) return;
            situation[k] = s;
            string km = (Math.Sqrt(d2) / 1000.0).ToString("F0");
            if (s == 1) ModLog.Info("far parts: part " + e.Index + " is " + km + " km from you — switched off by the 100 km rule (no powered beacon on it)");
            else if (s == 2) ModLog.Info("far parts: part " + e.Index + " is " + km + " km from you — kept simulated (SC_Beacon " + beacon.Index + " powered): physics, camera and transmitter ticks go on");
            else if (s == 3) ModLog.Info("far parts: part " + e.Index + " was switched off " + km + " km from the universe center during playback — switched on again (camera/monitor ticks need it)");
            else if (before != 0) ModLog.Info("far parts: part " + e.Index + " is back within 100 km (" + km + " km)" + (before == 1 ? " — switched on again" : ""));
        }

        static void Deactivate(EntityManager em, Entity e)
        {
            em.RemoveComponent(e, ComponentType.ReadWrite(tiSPActive));
            if (em.HasComponent(e, ComponentType.ReadOnly(Ecs.tiPWI))) em.RemoveComponent(e, ComponentType.ReadWrite(Ecs.tiPWI));
            foreach (var c in Ecs.BufferEntities(em, e, tiChild))
                if (Ecs.Alive(em, c) && em.HasComponent(c, ComponentType.ReadOnly(tiSCActive))) em.RemoveComponent(c, ComponentType.ReadWrite(tiSCActive));
        }
        static void Activate(EntityManager em, Entity e, bool withPhysics)
        {
            em.AddComponent(e, ComponentType.ReadWrite(tiSPActive));
            if (withPhysics && !em.HasComponent(e, ComponentType.ReadOnly(Ecs.tiPWI))) em.AddComponent(e, ComponentType.ReadWrite(Ecs.tiPWI));   // shared component with the default value = world 0 (the game puts (PhysicsWorldIndex)0)
            foreach (var c in Ecs.BufferEntities(em, e, tiChild))
                if (Ecs.Alive(em, c) && !em.HasComponent(c, ComponentType.ReadOnly(tiSCActive))) em.AddComponent(c, ComponentType.ReadWrite(tiSCActive));
        }
    }
}
