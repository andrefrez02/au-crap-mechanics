using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Il2CppInterop.Runtime;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>0.31.35 (Andre, 22/11: "the game/mod performance is really bad", 10 FPS on the 29 000-block ship with 7 to 9 physics ticks per
    /// frame). Measuring only, and only with [Debug] debug_tools: nothing here changes what the game or the mod computes.
    ///
    /// The existing performance line (TickProf.cs) times the mod's hooks and "the game" from the start of PhysicsPreGroup to the end of the
    /// components' tick. It does not see the rest of the tick (PhysicsPostGroup: the game's sound grid, the joint check), it cannot tell
    /// the mod's own work from the time a hook spends waiting for the game's jobs, and it does not say which game system is slow. This adds:
    ///   1. the frame's budget: the whole FixedStepSimulationSystemGroup per frame, so the whole tick = that / ticks, and the rest of the frame;
    ///   2. every system group's own time (the patch on ComponentSystemGroup.OnUpdate already runs for each of them), child groups and the
    ///      mod's hooks taken out;
    ///   3. [Debug] perf_split_waits: before each bundle of hooks the game's jobs are finished (EntityManager.CompleteAllTrackedJobs) and that
    ///      wait is timed by itself, so the hooks show their own work only;
    ///   4. the game's sound grid system: how long PhysicsPostGroup takes in a tick with nothing / with a new update of the moving cells /
    ///      with a full rebuild of the grid, how often each happens, and the mod's sound cells (SoundSeal.PerfLine);
    ///   5. [Debug] system_timing: for 10 s, every system inside the fixed step is timed one by one (see RunGroup);
    ///   6. what one call into the game costs on this machine (1000 x EntityManager.Exists, once).
    /// The lines start with "performance+:" and are written with the mod's own performance line, every 10 s.</summary>
    internal static class Perf
    {
        public static bool On;             // [Debug] debug_tools
        public static bool SplitWaits;     // [Debug] perf_split_waits
        public static bool SystemTiming;   // [Debug] system_timing
        static readonly double toMs = 1000.0 / Stopwatch.Frequency;
        public static long Now() => Stopwatch.GetTimestamp();

        // ---------------------------------------------------------------- names of the groups (Ecs.PreparePointers)
        static readonly Dictionary<IntPtr, string> names = new Dictionary<IntPtr, string>();
        public static void Name(IntPtr p, string name) { if (p != IntPtr.Zero) names[p] = name; }
        /// <summary>World unloaded: the system objects are gone.</summary>
        public static void Forget()
        {
            names.Clear(); groups.Clear(); depth = 0; fixedAt = -1; sysOn = false; sysNames.Clear(); sys.Clear();
            ResetWindow();
        }

        // ---------------------------------------------------------------- 1 + 2: groups
        struct Fr { public IntPtr p; public long t0; public double child; }
        class G { public string name; public double total, self, worst; public int n; public bool inFixed; }
        static readonly Fr[] stack = new Fr[48]; static int depth, fixedAt = -1;
        static readonly Dictionary<IntPtr, G> groups = new Dictionary<IntPtr, G>();
        static double fixedMs, fixedWorst, frameMs, hooksMs; static int frames, ticks, fixedCalls;
        public static bool InFixed => fixedAt >= 0;

        /// <summary>Once per drawn frame (the mod's Update): no group is running then.</summary>
        public static void Frame()
        {
            depth = 0; fixedAt = -1;
            if (!On) return;
            frames++; frameMs += UnityEngine.Time.unscaledDeltaTime * 1000.0;
        }
        /// <summary>End of the mod's prefix of a group's OnUpdate. hookStart = Now() at the prefix's first line.</summary>
        public static void GroupEnter(IntPtr p, long hookStart)
        {
            long now = Stopwatch.GetTimestamp();
            double hook = (now - hookStart) * toMs; hooksMs += hook;
            if (depth > 0 && depth <= stack.Length) stack[depth - 1].child += hook;   // the mod's own code is not the parent group's
            if (depth < stack.Length) { stack[depth].p = p; stack[depth].t0 = now; stack[depth].child = 0; }
            if (p == Ecs.PtrFixedStep && p != IntPtr.Zero) fixedAt = depth;
            else if (p == Ecs.PtrPhysicsPre && p != IntPtr.Zero) ticks++;
            else if (p == Ecs.PtrPhysicsPost && p != IntPtr.Zero) SoundBefore();
            depth++;
        }
        /// <summary>First line of the mod's postfix of a group's OnUpdate. Returns Now() (0 = nothing to close).</summary>
        public static long GroupExit(IntPtr p)
        {
            long now = Stopwatch.GetTimestamp();
            int d = Math.Min(depth, stack.Length) - 1;
            while (d >= 0 && stack[d].p != p) d--;   // a group whose OnUpdate threw has no postfix: drop what is above
            if (d < 0) return now;
            depth = d;
            double dt = (now - stack[d].t0) * toMs, self = dt - stack[d].child;
            if (d > 0) stack[d - 1].child += dt;
            if (!groups.TryGetValue(p, out var g)) { g = new G(); names.TryGetValue(p, out g.name); if (g.name == null) g.name = "group 0x" + p.ToString("X"); groups[p] = g; }
            g.total += dt; g.self += self; g.n++; if (self > g.worst) g.worst = self; g.inFixed = fixedAt >= 0 && d > fixedAt;
            if (p == Ecs.PtrFixedStep) { fixedMs += dt; fixedCalls++; if (dt > fixedWorst) fixedWorst = dt; fixedAt = -1; }
            else if (p == Ecs.PtrPhysicsPost) SoundAfter(dt);
            return now;
        }
        /// <summary>Last line of the mod's postfix.</summary>
        public static void HookEnd(long hookStart)
        {
            double hook = (Stopwatch.GetTimestamp() - hookStart) * toMs; hooksMs += hook;
            if (depth > 0 && depth <= stack.Length) stack[depth - 1].child += hook;
        }

        // ---------------------------------------------------------------- 3: waiting for the game's jobs, timed by itself
        public const int WPre = 0, WPhysics = 1, WAfterTickPre = 2, WAfterPre = 3, WBeforeTickPost = 4, WAfterTickPost = 5, WCycle = 6, WN = 7;
        static readonly string[] waitNames = { "start of PhysicsPreGroup", "start of PhysicsSystemGroup", "after the components' pre-tick (thrusters)", "after PhysicsPreGroup (forces, drag)",
                                               "after the physics step (= the solver still running)", "after the components' tick", "the mod's frame cycle" };
        static readonly double[] waitSum = new double[WN], waitWorst = new double[WN]; static readonly int[] waitN = new int[WN];
        /// <summary>[Debug] perf_split_waits: finishes the game's jobs now and times it. The jobs do the same work either way; only the moment
        /// the main thread waits for them moves from "inside the first hook that touches their data" to here.</summary>
        public static void Wait(EntityManager em, int where)
        {
            if (!On || !SplitWaits) return;
            long t0 = Stopwatch.GetTimestamp();
            try { em.CompleteAllTrackedJobs(); } catch (Exception) { return; }
            double ms = (Stopwatch.GetTimestamp() - t0) * toMs;
            waitSum[where] += ms; waitN[where]++; if (ms > waitWorst[where]) waitWorst[where] = ms;
        }

        // ---------------------------------------------------------------- 4: the game's sound grid system (PhysicsPostGroup, every tick)
        static readonly double[] sndSum = new double[3], sndWorst = new double[3]; static readonly int[] sndN = new int[3];
        static int sndIdx0; static byte sndReb0; static bool sndRead; static int gridX, gridY, gridZ;
        static unsafe void SoundBefore()
        {
            sndRead = false;
            if (Unloading.Active || !JointSystem.EmOk) return;   // the world is going away: no call into it
            try
            {
                byte* p = SoundSeal.Singleton(JointSystem.CurrentEm); if (p == null) return;
                sndIdx0 = *(int*)(p + 124); sndReb0 = p[144]; sndRead = true;
            }
            catch (Exception) { }
        }
        static unsafe void SoundAfter(double dt)
        {
            if (!sndRead || Unloading.Active || !JointSystem.EmOk) return;
            try
            {
                byte* p = SoundSeal.Singleton(JointSystem.CurrentEm); if (p == null) return;
                int idx = *(int*)(p + 124); byte reb = p[144];
                int kind = sndReb0 != 0 && reb == 0 ? 2 : idx != sndIdx0 ? 1 : 0;
                sndSum[kind] += dt; sndN[kind]++; if (dt > sndWorst[kind]) sndWorst[kind] = dt;
                byte* vd = *(byte**)(p + 128 + 8 * (idx & 1));
                if (vd != null) { int* mn = (int*)(vd + 88), mx = (int*)(vd + 100); gridX = mx[0] - mn[0] + 1; gridY = mx[1] - mn[1] + 1; gridZ = mx[2] - mn[2] + 1; }
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- 6: what one call into the game costs
        static double callUs = -1;
        static void Calibrate(EntityManager em)
        {
            try
            {
                var e = new Entity { Index = 1, Version = 1 };
                for (int i = 0; i < 50; i++) em.Exists(e);
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < 1000; i++) em.Exists(e);
                callUs = (Stopwatch.GetTimestamp() - t0) * toMs;   // ms per 1000 calls = microseconds per call
            }
            catch (Exception) { callUs = 0; }
        }

        // ---------------------------------------------------------------- the lines, every 10 s (with the mod's performance line)
        static void ResetWindow()
        {
            fixedMs = 0; fixedWorst = 0; frameMs = 0; hooksMs = 0; frames = 0; ticks = 0; fixedCalls = 0;
            Array.Clear(waitSum, 0, WN); Array.Clear(waitWorst, 0, WN); Array.Clear(waitN, 0, WN);
            Array.Clear(sndSum, 0, 3); Array.Clear(sndWorst, 0, 3); Array.Clear(sndN, 0, 3);
            foreach (var g in groups.Values) { g.total = 0; g.self = 0; g.worst = 0; g.n = 0; }
        }
        public static void Report(EntityManager em)
        {
            if (!On) { if (frames > 0 || ticks > 0) ResetWindow(); return; }
            try
            {
                if (callUs < 0) Calibrate(em);
                if (frames > 0 && ticks > 0)
                {
                    double F = frameMs / frames, fx = fixedMs / frames, T = fixedMs / ticks, R = F - fx, n = ticks / (double)frames;
                    var sb = new StringBuilder("performance+: ").Append(frames).Append(" frames of ").Append(F.ToString("F1")).Append(" ms (").Append((1000.0 / Math.Max(0.01, F)).ToString("F1"))
                        .Append(" FPS), ").Append(n.ToString("F1")).Append(" physics ticks per frame; the WHOLE fixed step ").Append(fx.ToString("F1")).Append(" ms per frame (worst ").Append(fixedWorst.ToString("F0"))
                        .Append(") = ").Append(T.ToString("F2")).Append(" ms per tick, the mod's hooks included; the rest of the frame ").Append(R.ToString("F1")).Append(" ms");
                    if (T < 16.0) sb.Append("; if the rest stayed the same, a frame would settle at ").Append((R / (1.0 - T / 16.667)).ToString("F0")).Append(" ms, and with 1 ms less per tick at ")
                        .Append((R / (1.0 - Math.Max(0.0, T - 1.0) / 16.667)).ToString("F0")).Append(" ms");
                    else sb.Append("; a tick takes as long as the 16.7 ms it simulates: the game cannot keep up and drops simulation time");
                    if (groups.TryGetValue(Ecs.PtrFixedStep, out var fg) && fg.n > 0) sb.Append("; systems sitting directly in the fixed-step group (in no sub-group) ").Append((fg.self / ticks).ToString("F3")).Append(" ms per tick");
                    sb.Append("; one call into the game costs ").Append(callUs.ToString("F2")).Append(" us here");
                    sb.Append(Ecs.CacheSummary());
                    ModLog.Info(sb.ToString());

                    // groups: own time (children and the mod's hooks taken out)
                    var l = new List<G>(); foreach (var g in groups.Values) if (g.n > 0) l.Add(g);
                    l.Sort((a, b) => b.self.CompareTo(a.self));
                    sb.Clear().Append("performance+: the game's system groups, their OWN time (child groups and the mod's hooks taken out) — inside the fixed step, ms per tick (worst):");
                    int shown = 0;
                    foreach (var g in l) { if (!g.inFixed || shown >= 12) continue; sb.Append(shown++ == 0 ? " " : ", ").Append(g.name).Append(' ').Append((g.self / ticks).ToString("F3")).Append(" (").Append(g.worst.ToString("F1")).Append(')'); }
                    if (shown == 0) sb.Append(" none seen");
                    sb.Append(" | outside it, ms per frame (worst):"); shown = 0;
                    foreach (var g in l) { if (g.inFixed || shown >= 8 || g.name == "FixedStepSimulationSystemGroup") continue; sb.Append(shown++ == 0 ? " " : ", ").Append(g.name).Append(' ').Append((g.self / frames).ToString("F2")).Append(" (").Append(g.worst.ToString("F1")).Append(')'); }
                    sb.Append(" | the mod's code in group hooks ").Append((hooksMs / frames).ToString("F2")).Append(" ms per frame");
                    ModLog.Info(sb.ToString());

                    if (SplitWaits)
                    {
                        sb.Clear().Append("performance+: perf_split_waits ON — the hook times in the performance line are the mod's own work; waiting for the game's jobs, ms per tick (worst):");
                        double sum = 0;
                        for (int i = 0; i < WN; i++)
                        {
                            if (waitN[i] == 0) continue;
                            bool cyc = i == WCycle; double per = waitSum[i] / (cyc ? frames : ticks); if (!cyc) sum += per;
                            sb.Append(' ').Append(waitNames[i]).Append(' ').Append(per.ToString("F3")).Append(cyc ? " per frame" : "").Append(" (").Append(waitWorst[i].ToString("F1")).Append(");");
                        }
                        sb.Append(" total inside the tick ").Append(sum.ToString("F2")).Append(" ms per tick (the game would wait for the same jobs later in the tick; what the mod really costs is the part the main thread could have used meanwhile)");
                        ModLog.Info(sb.ToString());
                    }

                    int st = sndN[0] + sndN[1] + sndN[2];
                    if (st > 0)
                    {
                        sb.Clear().Append("performance+: the game's sound grid (PhysicsPostGroup, every tick) — ");
                        string[] kn = { "ticks with nothing new", "ticks that started a new update of the moving cells", "ticks with a FULL rebuild of the grid" };
                        for (int k = 0; k < 3; k++) sb.Append(k == 0 ? "" : "; ").Append(kn[k]).Append(": ").Append(sndN[k]).Append(sndN[k] > 0 ? ", the group took " + (sndSum[k] / sndN[k]).ToString("F2") + " ms on average, worst " + sndWorst[k].ToString("F1") : "");
                        sb.Append("; grid ").Append(gridX).Append('x').Append(gridY).Append('x').Append(gridZ).Append(" = ").Append((long)gridX * gridY * gridZ).Append(" cells");
                        sb.Append(SoundSeal.PerfLine(em));
                        ModLog.Info(sb.ToString());
                    }
                }
            }
            catch (Exception ex) { ModLog.Warn("performance+: " + ex.Message); }
            ResetWindow();
        }

        // ---------------------------------------------------------------- 5: [Debug] system_timing
        // For 10 s the loop of ComponentSystemGroup.UpdateAllSystems is run by the mod for the groups inside the fixed step, with a clock
        // around each system. It is the same loop [GameAssembly 21/11, 0x3A79E80]: sort if m_systemSortDirty (CheckCreated + RecurseUpdate =
        // SortSystems, 0x3A79DB0); the length of m_MasterUpdateList read once; each entry is a ushort, bit 15 = managed system (index into
        // m_managedSystemsToUpdate, virtual Update), otherwise an unmanaged one (16-byte SystemHandle in m_UnmanagedSystemsToUpdate,
        // WorldUnmanagedImpl.UpdateSystem — what SystemHandle.Update calls, 0x3AF2800); an exception of one system is logged and the loop
        // goes on; World.QuitUpdate ends it. The game's own per-system callbacks (OnUpdateBefore / OnUpdateAfter, +0x78 / +0x80) are not
        // set by the game; if somebody sets them the group is left to the game's loop. Each system is called through the interop
        // (il2cpp_runtime_invoke), which hands a game exception back as a .NET one: nothing is thrown across the hook.
        // The patch is put on the first time the switch is turned on and answers "run the original" at once outside the window.
        class Sy { public string name; public double sum, worst; public int n; public bool group; }
        static readonly Dictionary<long, Sy> sys = new Dictionary<long, Sy>();
        static readonly Dictionary<long, string> sysNames = new Dictionary<long, string>();
        static bool sysOn, sysPatched, sysDone, sysFailed, offsOk; static float sysUntil; static int sysTicksAll, sysErrors;
        static int offDirty, offMaster, offManaged, offUnmanaged, offBefore, offAfter;

        /// <summary>Every frame from the mod's cycle.</summary>
        public static void SystemTimingStep(HarmonyLib.Harmony h)
        {
            if (!SystemTiming || !On) { sysDone = false; if (sysOn) { sysOn = false; SystemReport("stopped early"); } return; }
            float now = UnityEngine.Time.unscaledTime;
            if (!sysOn && !sysDone && !sysFailed)
            {
                try
                {
                    if (!offsOk) Offsets();
                    if (!sysPatched)
                    {
                        var m = typeof(ComponentSystemGroup).GetMethod("UpdateAllSystems", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (m == null) throw new InvalidOperationException("ComponentSystemGroup.UpdateAllSystems not found in the interop");
                        h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(Perf).GetMethod(nameof(UpdateAllPrefix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
                        sysPatched = true;
                    }
                    sys.Clear(); sysTicksAll = 0; sysErrors = 0; sysUntil = now + 10f; sysOn = true;
                    ModLog.Info("system timing: ON for 10 s — every system inside the fixed step is timed one by one (the mod runs the group's loop itself meanwhile; same systems, same order)");
                }
                catch (Exception ex) { sysFailed = true; ModLog.Warn("system timing: could not start (" + ex.Message + ") — nothing was changed"); }
            }
            if (sysOn && now >= sysUntil) { sysOn = false; sysDone = true; SystemReport("10 s"); try { if (CrapMechanicsPlugin.SystemTimingCfg != null) CrapMechanicsPlugin.SystemTimingCfg.Value = false; } catch (Exception) { } }
        }
        static int Off(string field)
        {
            IntPtr f = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<ComponentSystemGroup>.NativeClassPtr, field);
            if (f == IntPtr.Zero) throw new InvalidOperationException("field " + field + " not found");
            return (int)IL2CPP.il2cpp_field_get_offset(f);
        }
        static void Offsets()
        {
            offDirty = Off("m_systemSortDirty"); offMaster = Off("m_MasterUpdateList"); offManaged = Off("m_managedSystemsToUpdate"); offUnmanaged = Off("m_UnmanagedSystemsToUpdate");
            offBefore = Off("OnUpdateBefore"); offAfter = Off("OnUpdateAfter");
            // the loop in RunGroup was checked against the game build of 21/11, where these fields sit at +0x18, +0x30, +0x20, +0x48, +0x78 and
            // +0x80 of the group object. Another layout = another Entities version: the loop may differ, so the timing stays off
            if (offDirty != 0x18 || offMaster != 0x30 || offManaged != 0x20 || offUnmanaged != 0x48 || offBefore != 0x78 || offAfter != 0x80)
                throw new InvalidOperationException("this game version lays the system group out differently (0x" + offDirty.ToString("X") + "/0x" + offMaster.ToString("X") + "/0x" + offManaged.ToString("X") + "/0x" + offUnmanaged.ToString("X")
                                                    + "/0x" + offBefore.ToString("X") + "/0x" + offAfter.ToString("X") + ") from the one the timing loop was checked against (game of 21/11)");
            offsOk = true;
        }
        static bool UpdateAllPrefix(ComponentSystemGroup __instance)
        {
            if (!sysOn || fixedAt < 0 || __instance == null) return true;
            return !RunGroup(__instance);
        }
        /// <summary>true = the group's systems were updated here (the original must not run). Everything that can fail is done before the
        /// first system is updated; from there on nothing is thrown.</summary>
        static unsafe bool RunGroup(ComponentSystemGroup group)
        {
            IntPtr g; World world; WorldUnmanaged wu; Il2CppSystem.Collections.Generic.List<ComponentSystemBase> managed; IntPtr managedPtr; int n;
            try
            {
                g = group.Pointer; if (g == IntPtr.Zero) return false;
                if (*(IntPtr*)((byte*)g + offBefore) != IntPtr.Zero || *(IntPtr*)((byte*)g + offAfter) != IntPtr.Zero) return false;
                world = group.World; if (world == null) return false;
                if (*((byte*)g + offDirty) != 0) group.SortSystems();
                wu = world.Unmanaged;
                managedPtr = *(IntPtr*)((byte*)g + offManaged); managed = managedPtr != IntPtr.Zero ? group.m_managedSystemsToUpdate : null;
                n = *(int*)((byte*)g + offMaster + 8);
                if (n < 0 || n > 4096) return false;
                if (g == Ecs.PtrFixedStep) { sysTicksAll++; }
            }
            catch (Exception ex) { if (sysErrors++ < 3) ModLog.Warn("system timing: a group was left to the game's own loop (" + ex.Message + ")"); return false; }
            for (int i = 0; i < n; i++)
            {
                long key = 0; bool isGroup = false; long t0 = 0; object named = null; SystemHandle hnd = default; bool unmanagedSys = false;
                try
                {
                    ushort idx = (*(ushort**)((byte*)g + offMaster))[i];
                    if ((idx & 0x8000) != 0)
                    {
                        IntPtr mp = *(IntPtr*)((byte*)g + offManaged);
                        if (mp != managedPtr) { managedPtr = mp; managed = group.m_managedSystemsToUpdate; }
                        var s = managed[idx & 0x7fff];
                        key = (long)s.Pointer; named = s; isGroup = names.ContainsKey(s.Pointer);
                        t0 = Stopwatch.GetTimestamp();
                        s.Update();
                    }
                    else
                    {
                        hnd = *(SystemHandle*)(*(byte**)((byte*)g + offUnmanaged) + 16 * idx); unmanagedSys = true;
                        key = unchecked((long)(0x4000000000000000UL | ((ulong)(uint)hnd.m_Entity.Index << 16) | hnd.m_Handle));
                        t0 = Stopwatch.GetTimestamp();
                        hnd.Update(wu);
                    }
                }
                catch (Exception ex)
                {
                    // the game's loop: Debug.LogException(e), and on with the next system
                    try { if (sysErrors++ < 5) ModLog.Warn("system timing: a game system threw (the game's own loop logs it and goes on, so does this one): " + ex.Message); } catch (Exception) { }
                }
                try
                {
                    if (t0 != 0)
                    {
                        double ms = (Stopwatch.GetTimestamp() - t0) * toMs;
                        if (!sys.TryGetValue(key, out var y))
                        {
                            y = new Sy { group = isGroup };
                            if (!sysNames.TryGetValue(key, out y.name))
                            {
                                try { y.name = unmanagedSys ? wu.GetTypeOfSystem(hnd)?.Name : ((ComponentSystemBase)named).GetIl2CppType().Name; } catch (Exception) { }
                                if (y.name == null) y.name = "system " + key.ToString("X");
                                sysNames[key] = y.name;
                            }
                            sys[key] = y;
                        }
                        y.sum += ms; y.n++; if (ms > y.worst) y.worst = ms;
                    }
                    if (world.QuitUpdate) break;
                }
                catch (Exception) { }
            }
            return true;
        }
        static void SystemReport(string why)
        {
            try
            {
                int t = Math.Max(1, sysTicksAll);
                var l = new List<Sy>(sys.Values); l.Sort((a, b) => b.sum.CompareTo(a.sum));
                var sb = new StringBuilder("system timing (").Append(why).Append(", ").Append(sysTicksAll).Append(" ticks): GROUPS inside the fixed step, everything in them included, ms per tick (worst):");
                int shown = 0;
                foreach (var y in l) { if (!y.group || shown >= 14) continue; sb.Append(shown++ == 0 ? " " : ", ").Append(y.name).Append(' ').Append((y.sum / t).ToString("F3")).Append(" (").Append(y.worst.ToString("F1")).Append(')'); }
                ModLog.Info(sb.ToString());
                sb.Clear().Append("system timing: the 25 slowest SYSTEMS inside the fixed step, ms per tick (worst) — a system that only schedules jobs shows only that; the jobs' time shows where somebody waits for them:");
                shown = 0; double rest = 0; int restN = 0;
                foreach (var y in l)
                {
                    if (y.group) continue;
                    if (shown >= 25) { rest += y.sum; restN++; continue; }
                    sb.Append(shown++ == 0 ? " " : ", ").Append(y.name).Append(' ').Append((y.sum / t).ToString("F3")).Append(" (").Append(y.worst.ToString("F1")).Append(')');
                }
                if (restN > 0) sb.Append(" | the other ").Append(restN).Append(" systems together ").Append((rest / t).ToString("F3"));
                ModLog.Info(sb.ToString());
            }
            catch (Exception ex) { ModLog.Warn("system timing: " + ex.Message); }
            sys.Clear();
        }
    }
}
