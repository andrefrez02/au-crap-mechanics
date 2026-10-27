using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CrapMechanics
{
    /// <summary>0.17.0 (Andre: "a car icon and a ship icon + what we already have, and on the beacon's little display I pick which icon to show,
    /// with a knob of the beacon itself"): the beacon's knob (the game's EPC_Actionable_Knob, 3 positions) picks 0 = waves, 1 = car,
    /// 2 = ship. The display (dark panel of the slanted cover) has the 3 icons hidden 4/5/6 mm inwards; the chosen one comes out.
    /// The radar uses the slice of the chosen icon (RadarIcon: waves over icon 8, car and ship in new slices).</summary>
    internal static class BeaconIcon
    {
        // from modelos_v2 (BEACON_ICON_P0 / _N / _HIDE, knob): point of the lit icon and normal of the slanted cover, in the block
        static readonly float3 P0 = new float3 { x = 0.0464726f, y = 0.0653310f, z = -0.03f };
        static readonly float3 N = new float3 { x = 0.4280863f, y = 0.9037378f, z = 0f };
        const float HideMode = 0.004f, IconStep = 0.001f;
        static TypeIndex tiData, tiBelong; static bool ok, failed; static EntityQuery qData;
        public static readonly string[] Names = { "signal waves", "car", "ship" };

        static bool Prepare(EntityManager em)
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiData = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableData>());
                tiBelong = TypeManager.GetTypeIndex(Il2CppType.Of<BelongingSC>());
                var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(2);
                a[0] = ComponentType.ReadOnly(tiBelong); a[1] = ComponentType.ReadOnly(tiData);
                qData = em.CreateEntityQuery(a); ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Error("beacon knob: types: " + ex.Message); return false; }
        }

        static float Dot(float3 a, float3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        /// <summary>Reads the knob and updates the display. Runs in the garage too (to see the chosen icon while building).</summary>
        public static unsafe void Step(EntityManager em, Beacon b, float dt)
        {
            if (!Prepare(em)) return;
            b.tKnob += dt;
            if (b.tKnob >= 3f || (b.knob.Index == 0 && b.knob.Version == 0 && b.tKnob >= 0.5f))
            {
                b.tKnob = 0f;
                if (b.knob.Index == 0 && b.knob.Version == 0 || !Ecs.Alive(em, b.knob)) b.knob = FindKnob(em, b.block);
                if (b.icons == null || b.icons.Length < 3 || !Ecs.Alive(em, b.icons[0])) FindIcons(em, b);
            }
            int k = b.icon;
            if (Ecs.Alive(em, b.knob))
            {
                var v = (Ecs.HasC(em, b.knob, tiData) ? (float*)em.GetComponentDataRawRO(b.knob, tiData) : null);
                if (v != null)
                {
                    float x = v[0]; if (float.IsNaN(x)) x = 0f;
                    k = x <= 1.01f ? (int)Math.Round(x * 2f) : (int)Math.Round(x);   // 3 positions: 0 / 0.5 / 1 (or 0 / 1 / 2)
                    k = Math.Max(0, Math.Min(2, k));
                    if (Math.Abs(x - b.knobLog) > 0.01f) { if (b.knobLog < -1f) ModLog.Info("SC_Beacon " + b.block.Index + ": knob value " + x.ToString("F2")); b.knobLog = x; }
                }
            }
            Hint(em, b, k);
            if (k != b.icon || b.iconShown != k) { if (k != b.icon) ModLog.Info("SC_Beacon " + b.block.Index + ": icon " + Names[k]); b.icon = k; Show(em, b); }
        }

        /// <summary>0.19.1 (Andre: "the beacon's knob has no tooltip; on the monitor's knob it says which mode is set"): the beacon's
        /// knob is _drawUIValue = Custom, and the game gives it an ActionableTargetDrawCustomUIValue (FixedString64Bytes: +0 ushort
        /// length in UTF-8 bytes, then the text) that the pointer hint shows — empty. Here the text becomes the chosen icon.</summary>
        static TypeIndex tiHint; static bool hintOk, hintFailed;
        static unsafe void Hint(EntityManager em, Beacon b, int k)
        {
            if (hintFailed || !Ecs.Alive(em, b.knob)) return;
            if (k == b.hint && b.knob.Index == b.knobTip.Index && b.knob.Version == b.knobTip.Version) return;
            try
            {
                if (!hintOk) { tiHint = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableTargetDrawCustomUIValue>()); hintOk = true; }
                if (!Ecs.HasC(em, b.knob, tiHint)) { b.hint = k; b.knobTip = b.knob; if (!hintWarned) { hintWarned = true; ModLog.Warn("beacon knob: no custom tooltip component on the knob (rebuild the bundle in the Modkit)"); } return; }
                string txt = Text(k);
                var bytes = System.Text.Encoding.UTF8.GetBytes(txt); int n = Math.Min(bytes.Length, 60);
                var p = (byte*)em.GetComponentDataRawRW(b.knob, tiHint);
                *(ushort*)p = (ushort)n; for (int i = 0; i < n; i++) p[2 + i] = bytes[i]; p[2 + n] = 0;
                b.hint = k; b.knobTip = b.knob;
            }
            catch (Exception ex) { hintFailed = true; ModLog.Warn("beacon knob tooltip: " + ex.Message); }
        }
        static bool hintWarned;
        static readonly string[] DefaultHint = { "Radar icon: signal waves", "Radar icon: car", "Radar icon: ship" };
        static string Text(int k)
        {
            string key = "SC_Beacon_KnobIcon" + k;
            try { var loc = Localization._cachedInstance; if (loc != null && loc._map != null && loc._map.ContainsKey(key)) return loc._map[key]; } catch (Exception) { }
            return DefaultHint[k];
        }

        /// <summary>0.19.3: the same finder for the bearing/piston knobs (Knobs.cs) — in the garage the knob may not be in the block's
        /// Child buffer, only in its BelongingSC.</summary>
        public static Entity FindKnobOf(EntityManager em, Entity block) => Prepare(em) ? FindKnob(em, block) : Entity.Null;
        /// <summary>0.19.15: every actionable with BelongingSC (knobs, levers, buttons) and the block that owns each one.</summary>
        public static Entity[] AllActionables(EntityManager em) => Prepare(em) ? Ecs.QueryList(qData).ToArray() : new Entity[0];
        public static unsafe Entity BlockOfActionable(EntityManager em, Entity a)
        {
            var bs = (Ecs.HasC(em, a, tiBelong) ? (BelongingSC*)em.GetComponentDataRawRO(a, tiBelong) : null);
            return bs == null ? Entity.Null : bs->_sc;
        }
        /// <summary>0.23.0: Knobs sends here the TypeIndex of ActionableToggle (the bearing's switch) so it isn't mistaken for the knob.</summary>
        public static TypeIndex SkipToggle;
        static bool Skip(EntityManager em, Entity f) => SkipToggle.Value != 0 && Ecs.HasC(em, f, SkipToggle);
        /// <summary>0.23.0: all the actionables (by BelongingSC) of a block — the bearing's switch in the garage isn't in Child.</summary>
        public static unsafe System.Collections.Generic.List<Entity> ActionablesOf(EntityManager em, Entity block)
        {
            var r = new System.Collections.Generic.List<Entity>();
            if (!Prepare(em)) return r;
            foreach (var f in Ecs.QueryList(qData))
            {
                var bs = (Ecs.HasC(em, f, tiBelong) ? (BelongingSC*)em.GetComponentDataRawRO(f, tiBelong) : null);
                if (bs != null && bs->_sc.Index == block.Index && bs->_sc.Version == block.Version) r.Add(f);
            }
            return r;
        }
        static unsafe Entity FindKnob(EntityManager em, Entity block)
        {
            foreach (var f in Ecs.Children(em, block)) if (Ecs.HasC(em, f, tiData) && !Skip(em, f)) return f;
            foreach (var f in Ecs.QueryList(qData))
            {
                var bs = (Ecs.HasC(em, f, tiBelong) ? (BelongingSC*)em.GetComponentDataRawRO(f, tiBelong) : null);
                if (bs != null && bs->_sc.Index == block.Index && bs->_sc.Version == block.Version && !Skip(em, f)) return f;
            }
            return Entity.Null;
        }

        /// <summary>0.19.2: M version (mirrored in X): P0.x and N.x change sign. Found out from the side the icons are on.</summary>
        static float3 P0Of(Beacon b) => b.mirror ? new float3 { x = -P0.x, y = P0.y, z = P0.z } : P0;
        static float3 NOf(Beacon b) => b.mirror ? new float3 { x = -N.x, y = N.y, z = N.z } : N;
        static void FindIcons(EntityManager em, Beacon b)
        {
            var l = new Entity[3]; int n = 0;
            if (!b.mirrorTested) { b.mirrorTested = true; b.mirror = false; int nn = 0; foreach (var f in Ecs.Children(em, b.block)) { var p = Ecs.LT(em, f).Position; if (Math.Abs(p.x + P0.x) < 0.01f && Math.Abs(p.y - P0.y) < 0.03f) nn++; } b.mirror = nn >= 2; }
            var P0b = P0Of(b); var Nb = NOf(b);
            foreach (var f in Ecs.Children(em, b.block))
            {
                var p = Ecs.LT(em, f).Position; var d = new float3 { x = p.x - P0b.x, y = p.y - P0b.y, z = p.z - P0b.z };
                float depth = Dot(d, Nb); var t = new float3 { x = d.x - Nb.x * depth, y = d.y - Nb.y * depth, z = d.z - Nb.z * depth };
                if (Dot(t, t) > 0.0015f * 0.0015f) continue;
                int k = (int)Math.Round((-depth - HideMode) / IconStep);
                if (k < 0 || k > 2 || Math.Abs(-depth - HideMode - k * IconStep) > 0.0003f) continue;   // (only the hidden ones; the lit one is already known)
                l[k] = f; n++;
            }
            if (n == 3 || b.icons == null) { b.icons = l; b.iconShown = -1; }
            if (n < 3 && !b.iconsWarned && Ecs.IsPart(em, Ecs.Parent(em, b.block))) { b.iconsWarned = true; ModLog.Warn("SC_Beacon " + b.block.Index + ": display icons not found (" + n + "/3) — rebuild the bundle in the Modkit"); }
        }

        static unsafe void Show(EntityManager em, Beacon b)
        {
            if (b.icons == null) return;
            for (int k = 0; k < b.icons.Length; k++)
            {
                var e = b.icons[k]; if (!Ecs.Alive(em, e)) continue;
                var lt = (Ecs.HasC(em, e, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(e, Ecs.tiLT) : null); if (lt == null) continue;
                float depth = k == b.icon ? 0f : -(HideMode + IconStep * k);
                var P0b = P0Of(b); var Nb = NOf(b);
                lt->Position = new float3 { x = P0b.x + Nb.x * depth, y = P0b.y + Nb.y * depth, z = P0b.z + Nb.z * depth };
            }
            b.iconShown = b.icon;
        }
    }
}
