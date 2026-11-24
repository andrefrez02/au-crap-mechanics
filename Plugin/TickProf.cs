using System;
using System.Diagnostics;

namespace CrapMechanics
{
    /// <summary>0.31.27 (Andre, 21/11: "when the ship is moving the gate's joints the FPS drops a lot"). The performance line measured only the
    /// mod's cycle in Update (~3 ms per frame on 3proto3), and the same log shows frames with 4 to 18 physics ticks in them. Everything the mod
    /// does INSIDE the game's tick (the hooks in PatchSystems) was not measured, and at 20 fps every millisecond added to a tick is paid three
    /// times per frame — and makes the next frame longer, so it has even more ticks. This times each hook per tick (average and worst tick),
    /// the ticks per frame, and the game's own share of the tick: from the start of PhysicsPreGroup to the end of
    /// SpaceshipComponentsTickPostGroup (thrust, drag, the physics step, the circuits) minus the mod's hooks inside that span.
    /// A hook's time includes any wait for the game's jobs it caused (a component opened for reading or writing makes the game finish the
    /// jobs that use it first). Measuring costs two clock reads per hook.</summary>
    internal static class TickProf
    {
        public const int Far = 0, Splits = 1, Commands = 2, Momentum = 3, WeldForces = 4, Drag = 5, WeldSolver = 6, Gravity = 7, Thrust = 8,
                         MassesReal = 9, Rings = 10, Breaks = 11, Radio = 12, Remote = 13, N = 14;   // 0.31.35: + the Remote Computer's two hooks
        static readonly string[] names = { "100 km rule", "splits", "piston/servo commands", "momentum watch", "real masses before the forces",
                                           "drag watch/no_drag", "weld before the solver", "launch gravity", "thrust as one part",
                                           "real masses after the solver", "slip rings", "joint releases", "long range copy", "remote computer" };
        static readonly double toMs = 1000.0 / Stopwatch.Frequency;
        static readonly double[] sum = new double[N], worst = new double[N], now = new double[N];
        static long tickStart; static double tickMod; static bool open;
        static int ticks, spans, ticksFrame, maxPerFrame; static double sumMod, worstMod, sumGame, worstGame, worstSpan;
        static int gc0 = -1;
        // review: the drawn frames are counted here, in the same span as the ticks. The performance line's own frame count (nMed) skips
        // the frames the mod's cycle returns early (garage/BP menu, replay playback, world unloading) while the ticks go on being counted,
        // which made "ticks per frame" and "ms per frame" too high in the first line after such a pause
        static int frames;

        public static long T0() => Stopwatch.GetTimestamp();
        /// <summary>Adds the time since t0 to hook id, in the current tick.</summary>
        public static void Add(int id, long t0)
        {
            double ms = (Stopwatch.GetTimestamp() - t0) * toMs;
            now[id] += ms; tickMod += ms;
        }
        /// <summary>Prefix of PhysicsPreGroup: a new tick starts (one that never reached its end is closed without a span).</summary>
        public static void TickStart()
        {
            if (open) Close(false);
            open = true; tickStart = Stopwatch.GetTimestamp(); tickMod = 0; Array.Clear(now, 0, N);
            ticks++; ticksFrame++;
        }
        /// <summary>Postfix of SpaceshipComponentsTickPostGroup: the tick is over.</summary>
        public static void TickEnd() { if (open) Close(true); }
        static void Close(bool complete)
        {
            open = false;
            for (int i = 0; i < N; i++) { sum[i] += now[i]; if (now[i] > worst[i]) worst[i] = now[i]; }
            sumMod += tickMod; if (tickMod > worstMod) worstMod = tickMod;
            if (!complete) return;
            double span = (Stopwatch.GetTimestamp() - tickStart) * toMs, game = Math.Max(0.0, span - tickMod);
            sumGame += game; if (game > worstGame) worstGame = game; if (span > worstSpan) worstSpan = span; spans++;
        }
        /// <summary>Once per drawn frame (the mod's Update).</summary>
        public static void Frame() { if (ticksFrame > maxPerFrame) maxPerFrame = ticksFrame; ticksFrame = 0; frames++; }
        /// <summary>The mod's hooks cost more than 1 ms per frame on average (so the performance line is written even with a light cycle).</summary>
        public static bool Heavy(int cycleFrames) => frames > 0 && sumMod / frames > 1.0;

        /// <summary>The piece of the performance line; resets the sums.</summary>
        public static string Summary(int cycleFrames)
        {
            if (ticks == 0) { frames = 0; return ""; }
            var sb = new System.Text.StringBuilder();
            sb.Append(" | game ticks: ").Append(ticks).Append(" in ").Append(frames).Append(" frames (").Append((ticks / (double)Math.Max(1, frames)).ToString("F1"))
              .Append(" per frame, up to ").Append(maxPerFrame).Append(")");
            if (spans > 0)
                sb.Append(", one tick from PhysicsPreGroup to the end of the components' tick: the game ").Append((sumGame / spans).ToString("F2")).Append(" ms average, worst ")
                  .Append(worstGame.ToString("F1")).Append(" ms (whole tick worst ").Append(worstSpan.ToString("F1")).Append(" ms)");
            sb.Append("; the mod's hooks ").Append((sumMod / ticks).ToString("F3")).Append(" ms per tick, worst ").Append(worstMod.ToString("F1"))
              .Append(" ms, ").Append((sumMod / Math.Max(1, frames)).ToString("F2")).Append(" ms per frame | per hook, ms per tick (average / worst tick):");
            var idx = new int[N]; for (int i = 0; i < N; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => sum[b].CompareTo(sum[a]));
            int shown = 0;
            foreach (int i in idx)
            {
                if (sum[i] <= 0.0 || shown >= 8) continue;
                sb.Append(shown++ == 0 ? " " : ", ").Append(names[i]).Append(' ').Append((sum[i] / ticks).ToString("F3")).Append('/').Append(worst[i].ToString("F2"));
            }
            if (shown == 0) sb.Append(" none");
            int g = GC.CollectionCount(0);
            if (gc0 >= 0) sb.Append(" | .NET garbage collections: ").Append(g - gc0);
            gc0 = g;
            Array.Clear(sum, 0, N); Array.Clear(worst, 0, N);
            ticks = 0; spans = 0; maxPerFrame = 0; frames = 0; sumMod = 0; worstMod = 0; sumGame = 0; worstGame = 0; worstSpan = 0;
            return sb.ToString();
        }
    }
}
