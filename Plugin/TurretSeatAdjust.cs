using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CrapMechanics
{
    /// <summary>0.12.6 (Andre): two versions of the turret to test, and an adjust mode with the game running.
    /// Version A (seat_invisible = true): seated on the turret, the renderers of the game's seat (Upper, Upper Paintable, Piston) go to the
    /// CRPLayer.MyPlayer layer (4) — the main camera doesn't draw that layer (it's what hides your own body); the block camera and the
    /// Replay free camera keep showing it. When standing up/switching version, the layer comes back.
    /// COCKPIT mode (default, 0.12.7): visible seat and the player's character turns along with the drawing (Turret.RotatePlayer) — the camera is its child.
    /// Version B (false): visible seat and you sit at player_position_b (ActionableSeat._playerTransform.pos rewritten, relative to Upper).
    /// Adjust mode (only seated on the turret): Num/ turns it on; Num* A/B; Num1 seat, Num3 player; Num4/6 x, Num8/2 y, Num7/9 z (Shift x5);
    /// Num5 resets the target to the default. Every change goes to the log and to the cfg.</summary>
    internal static partial class JointSystem
    {
        // 0.12.7: seat mode — 0 COCKPIT (default: visible seat and YOU turn along with it — the camera goes along, nothing crosses the view),
        // 1 A (seat invisible to you), 2 B (visible seat, you sit at player_position_b)
        public static int SeatMode; public static bool Adjust; static int adjustTarget;   // target 0 seat, 1 player
        public static bool SeatInvisible => SeatMode == 1;
        static string ModeName(int m) => m == 0 ? "cockpit" : m == 1 ? "A" : "B";
        public static readonly float3 DefaultView = new float3 { x = 0f, y = 0.42f, z = -0.46f };   // 0.12.9: 7 cm higher and 6 cm further back (Andre), ~6 cm from the tube of the rear arc
        public static bool ViewRotates = true;
        public static float3 SeatOff, PlayerB, ViewA = DefaultView;
        static TypeIndex tiLayer, tiActSeat; static bool layerOk, layerFailed;
        static readonly Dictionary<long, int> originalLayer = new Dictionary<long, int>();
        static readonly Dictionary<long, float3> originalPlayer = new Dictionary<long, float3>();
        static Entity hiddenBlock; static bool hiddenActive;

        public static void LoadAdjust(string mode, string seat, string player, string viewMode)
        {
            ViewA = Read3(viewMode, ViewA);
            var m = (mode ?? "").Trim().ToLowerInvariant(); SeatMode = m == "a" ? 1 : m == "b" ? 2 : 0; SeatOff = Read3(seat, float3.zero); PlayerB = Read3(player, float3.zero);
        }
        /// <summary>0.12.9: is the view (turning with the rings) less than ~4 cm from the tube of the rear arc (ellipse y 1.045 x z 0.62, tube 0.055)?</summary>
        static bool ArcNear(float3 v)
        {
            if (v.z > 0f) return false;
            float r = (float)System.Math.Sqrt(v.y * v.y + v.z * v.z); if (r < 1e-4f) return false;
            float s2 = v.y * v.y / (r * r), c2 = v.z * v.z / (r * r);
            float rArc = 1f / (float)System.Math.Sqrt(s2 / (1.045f * 1.045f) + c2 / (0.62f * 0.62f));
            return rArc - r < 0.055f + 0.04f;
        }
        internal static float3 Read3(string s, float3 defaultValue)
        {
            try
            {
                var p = s.Split(';'); if (p.Length != 3) return defaultValue;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                return new float3 { x = float.Parse(p[0].Trim().Replace(',', '.'), ci), y = float.Parse(p[1].Trim().Replace(',', '.'), ci), z = float.Parse(p[2].Trim().Replace(',', '.'), ci) };
            }
            catch (Exception) { return defaultValue; }
        }
        internal static string Scale3(float3 v) { var ci = System.Globalization.CultureInfo.InvariantCulture; return v.x.ToString("0.###", ci) + ";" + v.y.ToString("0.###", ci) + ";" + v.z.ToString("0.###", ci); }
        static void Save()
        {
            try
            {
                CrapMechanicsPlugin.TurretSeatMode.Value = ModeName(SeatMode);
                CrapMechanicsPlugin.TurretSeatAdjust.Value = Scale3(SeatOff);
                CrapMechanicsPlugin.TurretPlayerB.Value = Scale3(PlayerB);
                CrapMechanicsPlugin.TurretViewA.Value = Scale3(ViewA);
            }
            catch (Exception ex) { ModLog.Warn("turret adjust: saving to cfg: " + ex.Message); }
        }
        static string Summary() => "mode " + (SeatMode == 0 ? "COCKPIT (you turn with the seat)" : SeatMode == 1 ? "A (seat invisible to you)" : "B (seat visible, you at player_position_b)") + " | seat = model + (" + Scale3(SeatOff) + ") = Upper at (" + Scale3(Ecs.Add(TurretUpper, SeatOff)) + ") | player B at (" + Scale3(PlayerB) + ") | view A at (" + Scale3(ViewA) + "), " + Mat3(ViewA).ToString("F2") + " m from center";
        static float Mat3(float3 v) => (float)System.Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);

        static bool PrepareLayer(EntityManager em)
        {
            if (layerOk) return true; if (layerFailed) return false;
            try { tiLayer = TypeManager.GetTypeIndex(Il2CppType.Of<CRPLayerID>()); tiActSeat = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableSeat>()); layerOk = true; return true; }
            catch (Exception ex) { layerFailed = true; ModLog.Warn("turret adjust: CRPLayerID/ActionableSeat types: " + ex.Message); return false; }
        }

        /// <summary>Update: adjust mode keys + seat layer (version A).</summary>
        static void AdjustStep(EntityManager em)
        {
            var t = SeatedTurret;
            // ---- layer: hides the seat from whoever is seated (version A), gives it back when standing up or switching to B
            bool wantsHide = t != null && SeatInvisible && Ecs.Alive(em, t.block);
            if (wantsHide && (!hiddenActive || hiddenBlock != t.block)) { if (hiddenActive) Layer(em, hiddenBlock, false); Layer(em, t.block, true); hiddenBlock = t.block; hiddenActive = true; }
            else if (!wantsHide && hiddenActive) { Layer(em, hiddenBlock, false); hiddenActive = false; }
            if (t == null) { if (Adjust) { Adjust = false; ModLog.Info("turret adjust: off (stood up)"); } return; }
            // ---- keys
            if (CrapMechanicsPlugin.TurretAdjust == null || !CrapMechanicsPlugin.TurretAdjust.Value) { if (Adjust) Adjust = false; return; }   // 0.12.14: debug, off by default
            if (InputKeys.Pressed(Key.NumpadDivide)) { Adjust = !Adjust; ModLog.Info("turret adjust: " + (Adjust ? "ON — " + Summary() : "off")); }
            if (!Adjust) return;
            bool changed = false;
            if (InputKeys.Pressed(Key.NumpadMultiply)) { SeatMode = (SeatMode + 1) % 3; changed = true; }
            if (InputKeys.Pressed(Key.Numpad1)) { adjustTarget = 0; ModLog.Info("turret adjust: moving the SEAT"); }
            if (InputKeys.Pressed(Key.Numpad3)) { adjustTarget = adjustTarget == 2 ? 1 : 2; ModLog.Info("turret adjust: moving " + (adjustTarget == 2 ? "the VIEW (mode A)" : "the PLAYER POSITION (mode B)")); }
            float step = (InputKeys.Holding(Key.LeftShift) || InputKeys.Holding(Key.RightShift)) ? 0.05f : 0.01f;
            float3 d = float3.zero;
            if (InputKeys.Pressed(Key.Numpad4)) d.x -= step; if (InputKeys.Pressed(Key.Numpad6)) d.x += step;
            if (InputKeys.Pressed(Key.Numpad8)) d.y += step; if (InputKeys.Pressed(Key.Numpad2)) d.y -= step;
            if (InputKeys.Pressed(Key.Numpad7)) d.z -= step; if (InputKeys.Pressed(Key.Numpad9)) d.z += step;
            if (d.x != 0f || d.y != 0f || d.z != 0f) { if (adjustTarget == 0) SeatOff = Ecs.Add(SeatOff, d); else if (adjustTarget == 2) ViewA = Ecs.Add(ViewA, d); else PlayerB = Ecs.Add(PlayerB, d); changed = true; }
            if (InputKeys.Pressed(Key.Numpad5)) { if (adjustTarget == 0) SeatOff = float3.zero; else if (adjustTarget == 2) ViewA = DefaultView; else PlayerB = float3.zero; changed = true; }
            if (changed) { Save(); ModLog.Info("turret adjust: " + Summary()); }
        }

        /// <summary>0.12.10 (Andre: "the turret's chair showed up in the video but not right away"): during Replay playback the Crap Mechanics
        /// cycle stops — if you got in seated on the turret (mode A), the seat stayed on the invisible layer. Gives it back when it starts; when
        /// playback ends and you are still seated, the normal cycle hides it again.</summary>
        public static void SeatVisibleInPlayback(EntityManager em)
        {
            if (!hiddenActive) return;
            Layer(em, hiddenBlock, false); hiddenActive = false;
            ModLog.Info("turret seat: Replay playback — seat visible during playback");
        }

        /// <summary>Puts (or gives back) the MyPlayer layer on the renderers of this turret's game seat: Upper, Upper's children, Piston.</summary>
        static unsafe void Layer(EntityManager em, Entity block, bool hideIt)
        {
            try
            {
                if (!PrepareLayer(em) || !scSeatOk || !Ecs.HasC(em, block, tiSCSeat)) return;
                var sc = (Entity*)em.GetComponentDataRawRO(block, tiSCSeat);
                var list = new List<Entity> { sc[0], sc[1] };
                list.AddRange(Ecs.Children(em, sc[0]));
                int n = 0;
                foreach (var e in list)
                {
                    if (!Ecs.HasC(em, e, tiLayer)) continue;
                    var b = (int*)em.GetComponentDataRawRW(e, tiLayer); long k = Ecs.K(e);
                    if (hideIt) { if (!originalLayer.ContainsKey(k)) originalLayer[k] = *b; *b = 4; n++; }
                    else if (originalLayer.TryGetValue(k, out var orig)) { *b = orig; originalLayer.Remove(k); n++; }
                }
                ModLog.Info("turret seat " + block.Index + ": seat " + (hideIt ? "INVISIBLE to you (MyPlayer layer)" : "visible again") + " — " + n + " renderer(s)");
            }
            catch (Exception ex) { ModLog.Warn("turret seat: seat layer: " + ex.Message); }
        }

        /// <summary>In the TransformSystemGroup prefix (SeatHeights): version B = where the player sits (relative to Upper) = PlayerB - Upper;
        /// version A = the game's original value.</summary>
        static unsafe void ApplyPlayer(EntityManager em, Entity block, Entity upper, float3 upperPos)
        {
            try
            {
                if (!PrepareLayer(em) || !Ecs.HasC(em, upper, tiActSeat)) return;
                var p = (float3*)((byte*)em.GetComponentDataRawRW(upper, tiActSeat) + 16);   // RigidTransform _playerTransform: quaternion rot, float3 pos
                long k = Ecs.K(upper);
                if (!originalPlayer.TryGetValue(k, out var orig)) { orig = *p; originalPlayer[k] = orig; ModLog.Info("turret seat " + block.Index + ": player position on the seat (game's) = " + Ecs.F3(orig) + " relative to Upper = " + Ecs.F3(Ecs.Add(upperPos, orig)) + " in block"); }
                *p = SeatMode == 2 ? Ecs.Sub(PlayerB, upperPos) : orig;
            }
            catch (Exception ex) { if (playerErrors++ < 3) ModLog.Warn("turret seat: player position: " + ex.Message); }
        }
        static int playerErrors;

        // ---- little panel (OnGUI)
        static GUIStyle stAdj;
        public static void DrawAdjust()
        {
            if (!Adjust || SeatedTurret == null) return;
            if (stAdj == null) { stAdj = new GUIStyle(); stAdj.fontSize = 14; var n = stAdj.normal; n.textColor = Color.white; stAdj.normal = n; stAdj.wordWrap = false; }
            var lines = new[]
            {
                "TURRET ADJUST  (Num/ exits)",
                "Num* mode: " + (SeatMode == 0 ? "COCKPIT — you turn with the seat" : SeatMode == 1 ? "A — seat INVISIBLE to you" : "B — seat visible, you sit at the player point"),
                "Num1 seat / Num3 view(A) <-> player(B) — moving: " + (adjustTarget == 0 ? "SEAT" : adjustTarget == 2 ? "VIEW" + (SeatMode != 1 ? " (mode A only)" : "") : "PLAYER" + (SeatMode != 2 ? " (mode B only)" : "")),
                "view (A): (" + Scale3(ViewA) + ") — " + Mat3(ViewA).ToString("F2") + " m from center" + (!ViewRotates && Mat3(ViewA) > 0.56f ? "  (far from center: the ring/hub may cross the view)" : ViewRotates && ArcNear(ViewA) ? "  (close to the back arch)" : ""),
                "seat: +(" + Scale3(SeatOff) + ")  -> Upper at (" + Scale3(Ecs.Add(TurretUpper, SeatOff)) + ")",
                "player (B): (" + Scale3(PlayerB) + ") in block (0;0;0 = pivot center)",
                "Num4/6 left/right  Num8/2 up/down  Num7/9 back/front  (Shift x5)  Num5 resets the target",
            };
            float x = 12f, y = 120f, w = 560f, lh = 20f;
            GUI.Box(new Rect(x, y, w, lh * lines.Length + 10f), ""); GUI.Box(new Rect(x, y, w, lh * lines.Length + 10f), "");
            for (int i = 0; i < lines.Length; i++) GUI.Label(new Rect(x + 8f, y + 5f + i * lh, w - 16f, lh), lines[i], stAdj);
        }
    }
}
