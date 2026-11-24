using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;

namespace CrapMechanics
{
    /// <summary>0.19.2 (Andre: "the status LEDs on both transmitters are missing — the game's one blinks green / red / yellow"): the LED is
    /// the "Indicator" renderer of both blocks. 0.19.3 (Andre: "the leds didn't turn on"): CRPRendererMemory_Color is NOT float4 + specular
    /// (it's 8 bytes: colour and specular packed — uint2 _data, with Unpack(out color, out specularPacked)); I read 16 bytes ahead and
    /// never found the mark. Now: on the Shaft Transmitter (copy of the game's transmitter) the LED is SCTypeWirelessTransmitter._indicatorEntity
    /// (+24); on the LRT it's the child with the mark (specular strength 0.0041 from the prefab, compared already packed) or, if the mark is lost in
    /// packing, the child with colour (0,0,0,0) (the LED is the only renderer with alpha 0). Paints with the game's colours (SCTick_WirelessTransmitter):
    /// green (0, 1.4, 0), orange (0.95, 0.5, 0) — the `Yellow` constant —, red (1.9, 0, 0). 0.31.26 (item 21): with the game's MEANING
    /// [recon/EPC_SCWirelessTransmitter.cs:226-240, 266-277] — sender: red = another sender on the channel, green = somebody listens,
    /// orange = nobody listens; receiver: green = exactly 1 sender, red = 2 or more, off = none; a port with no direction: off. (It used to
    /// be yellow = interference, red = nobody at the other end.) 0.31.26 (item 44): blinks like the game, lit while frac(t * 0.75) < 0.5
    /// (period 1.33 s) and only with a SpaceshipSingleton (always off in the garage); off = all zero. The renderer's packed specular is kept.</summary>
    internal static class Led
    {
        public const int Unlit = 0, Green = 1, Yellow = 2, Red = 3;
        static TypeIndex tiMem, tiTransm, tiRend, tiBounds; static bool ok, failed; static uint mark, noMark; static bool markValid;
        // 0.19.6 (Andre: "sometimes the transmitter turns all green or all red"): the LRT's LED was the renderer with colour (0,0,0,0) — before
        // the paint is applied the BODY also has zero colour and I painted the body. Now the LED is recognised by the indicator's MESH
        // (CRPRendererData._meshReference, +0), learned from the _indicatorEntity of any wireless transmitter (the mod's or the game's).
        static uint ledMesh; static bool meshOk; static float tMesh = 9f; static bool didWarnMesh;
        // 0.31.26 (item 44): off = float4.zero, as the game writes it (`default` / float4.zero) [recon/EPC_SCWirelessTransmitter.cs:233, 295, 366]
        static readonly float4[] COLORS = { new float4(0f, 0f, 0f, 0f), new float4(0f, 1.4f, 0f, 1f), new float4(0.95f, 0.5f, 0f, 1f), new float4(1.9f, 0f, 0f, 1f) };
        static readonly HashSet<ulong> loggedSet = new HashSet<ulong>(); static readonly HashSet<long> warnedSet = new HashSet<long>();

        static bool Prepare()
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiMem = TypeManager.GetTypeIndex(Il2CppType.Of<CRPRendererMemory_Color>());
                try { tiTransm = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeWirelessTransmitter>()); } catch (Exception ex) { ModLog.Warn("led: SCTypeWirelessTransmitter: " + ex.Message); }
                try { tiRend = TypeManager.GetTypeIndex(Il2CppType.Of<CRPRendererData>()); } catch (Exception ex) { ModLog.Warn("led: CRPRendererData: " + ex.Message); }
                try { tiBounds = TypeManager.GetTypeIndex(Il2CppType.Of<CRPLocalBounds>()); } catch (Exception ex) { ModLog.Warn("led: CRPLocalBounds: " + ex.Message); }
                var sm = new SpecularSetup(); sm._strength = 0.0041f; sm._smoothness = 0f;
                new CRPRendererMemory_Color(new float4(0f, 0f, 0f, 1f), sm).Unpack(out _, out mark);
                new CRPRendererMemory_Color(new float4(0f, 0f, 0f, 1f), new SpecularSetup()).Unpack(out _, out noMark);
                markValid = mark != noMark;
                ModLog.Info("led: specular marker packs to " + mark + " (no specular: " + noMark + ")" + (markValid ? "" : " — marker lost in packing, using the alpha-0 renderer instead"));
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("led: " + ex.Message); return false; }
        }

        static unsafe bool Read(EntityManager em, Entity e, out float4 color, out uint spec)
        {
            color = default; spec = 0;
            if (!Ecs.HasC(em, e, tiMem)) return false;
            var p = (CRPRendererMemory_Color*)Ecs.RO(em, e, tiMem); if (p == null) return false;
            (*p).Unpack(out color, out spec); return true;
        }

        /// <summary>The block's LED renderer (the transmitter's indicator or the marked child). Entity.Null if not found.</summary>
        public static unsafe Entity Find(EntityManager em, Entity block)
        {
            if (!Prepare() || !Ecs.Alive(em, block)) return Entity.Null;
            var foundOne = Entity.Null; string how = "";
            if (tiTransm.Value != 0 && Ecs.HasC(em, block, tiTransm))
            {
                var p = (byte*)Ecs.RO(em, block, tiTransm);
                if (p != null) { var ind = *(Entity*)(p + 24); if (Ecs.Alive(em, ind) && Ecs.HasC(em, ind, tiMem)) { foundOne = ind; how = "the transmitter's indicator entity"; } }
            }
            if (Ecs.IsNull(foundOne))
            {
                // 0.19.6b (log: "mesh ref 1" on everything — _meshReference doesn't tell them apart; the body was still green): the LED is the smallest
                // renderer with colour on the block (CRPLocalBounds: the transmitter's indicator is a few millimeters; body, screen and label are cm)
                float smallest = float.MaxValue;
                foreach (var f in Ecs.Children(em, block))
                {
                    if (!Read(em, f, out var color, out var spec)) continue;
                    if (markValid && spec == mark) { foundOne = f; how = "the specular marker"; smallest = 0f; break; }
                    float ext = Extension(em, f);
                    if (ext < smallest) { smallest = ext; foundOne = f; how = "the smallest coloured renderer (extent " + (ext * 100f).ToString("F1") + " cm)"; }
                }
                if (!Ecs.IsNull(foundOne) && smallest > 0.03f) { if (warnedSet.Add(Ecs.K(block))) ModLog.Warn("transmitter block " + block.Index + ": the smallest coloured renderer is " + (smallest * 100f).ToString("F1") + " cm — not an LED, leaving it off (rebuild the bundle: the LED needs the 0.19.8 prefab)"); foundOne = Entity.Null; }
            }
            ulong h = Ecs.Prefab(em, block);
            if (loggedSet.Add(h))
            {
                var sb = new System.Text.StringBuilder("transmitter block " + block.Index + ": LED " + (Ecs.IsNull(foundOne) ? "NOT found" : "= renderer " + foundOne.Index + " (" + how + ")") + "; renderer children:");
                foreach (var f in Ecs.Children(em, block)) if (Read(em, f, out var color, out var spec)) sb.Append("\n    ").Append(f.Index).Append(" color (").Append(color.x.ToString("F2")).Append(',').Append(color.y.ToString("F2")).Append(',').Append(color.z.ToString("F2")).Append(',').Append(color.w.ToString("F2")).Append(") specular ").Append(spec).Append(" extent ").Append((Extension(em, f) * 100f).ToString("F1")).Append(" cm mesh ").Append(MeshOf(em, f));
                ModLog.Info(sb.ToString());
            }
            return foundOne;
        }

        /// <summary>Largest half-extent of the renderer's local box (CRPLocalBounds: AABB = Center float3 + Extents float3).</summary>
        static unsafe float Extension(EntityManager em, Entity e)
        {
            if (tiBounds.Value == 0 || !Ecs.HasC(em, e, tiBounds)) return float.MaxValue;
            var p = (float*)Ecs.RO(em, e, tiBounds); if (p == null) return float.MaxValue;
            return Math.Max(Math.Abs(p[3]), Math.Max(Math.Abs(p[4]), Math.Abs(p[5])));
        }
        static unsafe uint MeshOf(EntityManager em, Entity e)
        {
            if (tiRend.Value == 0 || !Ecs.HasC(em, e, tiRend)) return 0xffffffff;
            var p = (uint*)Ecs.RO(em, e, tiRend); return p == null ? 0xffffffff : *p;
        }
        /// <summary>Learns the indicator's mesh from the _indicatorEntity of any wireless transmitter (1 attempt every 3 s).</summary>
        static unsafe void LearnMesh(EntityManager em)
        {
            if (meshOk || tiTransm.Value == 0 || tiRend.Value == 0) return;
            tMesh += UnityEngine.Time.deltaTime; if (tMesh < 3f) return; tMesh = 0f;
            try
            {
                foreach (var t in Ecs.Transmitters(em))
                {
                    if (!Ecs.HasC(em, t, tiTransm)) continue;
                    var p = (byte*)Ecs.RO(em, t, tiTransm); if (p == null) continue;
                    var ind = *(Entity*)(p + 24); if (!Ecs.Alive(em, ind)) continue;
                    uint m = MeshOf(em, ind); if (m == 0xffffffff) continue;
                    ledMesh = m; meshOk = true; ModLog.Info("led: indicator mesh learned from transmitter " + t.Index + " (mesh ref " + m + ")"); return;
                }
            }
            catch (Exception ex) { ModLog.Warn("led: learning the indicator mesh: " + ex.Message); }
        }

        /// <summary>0.31.26 (item 44): the game's blink — `_indicatorOn = frac(ElapsedTime * 0.75) < 0.5 && HasSingleton&lt;SpaceshipSingleton&gt;()`
        /// [recon/EPC_SCWirelessTransmitter.cs:141-142]: on for the first half of every 1.33 s, never in the garage. (It was 1 Hz, also in the
        /// garage.) The singleton check is done once per frame.</summary>
        public static bool Lit
        {
            get
            {
                int f = UnityEngine.Time.frameCount;
                if (f != singFrame) { singFrame = f; try { singOn = !Ecs.Ok || Ecs.QSing.CalculateEntityCountWithoutFiltering() > 0; } catch (Exception) { singOn = true; } }
                double t = UnityEngine.Time.timeAsDouble * 0.75;
                return singOn && t - Math.Floor(t) < 0.5;
            }
        }
        static int singFrame = -1; static bool singOn = true;

        /// <summary>Remote control (delivery 1): the types are looked up (Paint/PaintSolid do nothing before this succeeds).</summary>
        public static bool Ready() => Prepare();
        /// <summary>Remote control (delivery 1): the state's colour with no blink (a short flash after a command). Returns what was written.</summary>
        public static unsafe int PaintSolid(EntityManager em, Entity led, int state, int written)
        {
            if (!ok || !Ecs.Alive(em, led) || !Ecs.HasC(em, led, tiMem)) return written;
            if (state < 0 || state >= COLORS.Length) state = Unlit;
            var p = (CRPRendererMemory_Color*)em.GetComponentDataRawRW(led, tiMem); if (p == null) return written;
            (*p).Unpack(out _, out uint spec);
            *p = new CRPRendererMemory_Color(COLORS[state], spec);
            return state;
        }
        /// <summary>Writes the state's colour (or off in the dark half of the blink). Returns what was written (so as not to rewrite for nothing).</summary>
        public static unsafe int Paint(EntityManager em, Entity led, int state, int written)
        {
            if (!ok || !Ecs.Alive(em, led) || !Ecs.HasC(em, led, tiMem)) return written;
            int wants = Lit ? state : Unlit;
            // (rewrites every frame: on the Shaft Transmitter the game's tick may paint the indicator too)
            var p = (CRPRendererMemory_Color*)em.GetComponentDataRawRW(led, tiMem); if (p == null) return written;
            (*p).Unpack(out _, out uint spec);
            *p = new CRPRendererMemory_Color(COLORS[wants], spec);
            return wants;
        }
    }
}
