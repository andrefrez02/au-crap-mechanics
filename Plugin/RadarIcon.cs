using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>The Beacon's own icon on the game's radar.
    /// How the game does it: GameFeature (a render pipeline feature, a ScriptableObject) has _radarIcons (Texture2D[], masks
    /// in the red channel only, R8 format) and in Create builds _radarIconsT2A (Texture2DArray 256x256, one slice per RadarIconID).
    /// Every frame (FrameSetup) it passes _radarIconsT2A to the shader as _RadarIconsTexture2DArray, and the shader draws the slice
    /// RadarLocationGPUData._type. The colour comes from the shader (it can't be changed per icon), so the way out is a new drawing.
    /// Here: I create a Texture2DArray with one extra slice (copy the game's ones on the GPU and draw the beacon's: a dot with waves),
    /// swap _radarIconsT2A and the Beacon uses the new index. If the game recreates the array (graphics quality change etc.),
    /// I do it again.</summary>
    internal static class RadarIcon
    {
        public static int Request = -1;          // config: -1 = own icon; 0..9 = a game icon
        public const uint Reserve = 2;          // RadarIconID.PlanetPackage, if our own fails
        static readonly System.Collections.Generic.HashSet<IntPtr> doneSet = new System.Collections.Generic.HashSet<IntPtr>(); static Texture2DArray mine; static IntPtr myPtr; static int myIdx; static IntPtr hNew; static float t = 9f, wait = 1f; static bool failed; static int attempts; static string step = "";

        public static void Step(float dt)
        {
            if (Request >= 0) { Couplers.IconBeacon = (uint)Request; return; }
            if (failed) { Couplers.IconBeacon = Reserve; return; }
            t += dt; if (t < wait) return; t = 0f;
            try
            {
                step = "find GameFeature";
                var objs = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<GameFeature>());
                if (objs == null) return;
                foreach (var o in objs)
                {
                    step = "TryCast GameFeature";
                    var gf = o.TryCast<GameFeature>(); if (gf == null) continue;
                    step = "read _radarIconsT2A";
                    var old = gf._radarIconsT2A; if (old == null) continue;
                    step = "compare with mine";
                    if (doneSet.Contains(old.Pointer)) { Couplers.IconBeacon = (uint)myIdx; continue; }
                    Build(gf, old);
                }
            }
            catch (Exception ex)
            {
                // 0.7.2 gave up on the 1st failure ("Object was garbage collected in IL2CPP domain", while loading the world).
                // Now it tries 5x, every 5 s; if my array was collected, forget it and build it again.
                attempts++; wait = 5f; mine = null; myPtr = IntPtr.Zero; Couplers.IconBeacon = Reserve;
                ModLog.Warn("radar: custom beacon icon failed (attempt " + attempts + "/5, step '" + step + "'): " + (attempts == 1 ? ex.ToString() : ex.Message));
                if (attempts >= 5) { failed = true; ModLog.Warn("radar: gave up on the custom icon, the beacon keeps icon " + Reserve + " (config Beacon.icon picks a game icon)"); }
            }
        }

        public static int Slot = 8;   // RadarIconID.ReindeerAntlers (event): the beacon's drawing goes over it

        /// <summary>0.17.1 (Andre: "check which ones are in the world and which missions use icons, use only the ones not being used, and
        /// on every R check again"): the game's radar only has 10 icons (RadarIconID 0..9) and all of them are the game's. The beacon's drawings
        /// (waves, car, ship) go over icons that NOBODY is using right now (no entity with RadarAppearanceIcon of that
        /// type other than the beacons). On every swap: the freed slice goes back to the game's original drawing (a copy kept on the GPU).
        /// Checks on load, every 30 s, when a knob changes and when entering/leaving the simulation (UIManager.OnSpaceshipLaunch).</summary>
        static readonly int[] Preference = { 8, 7, 3, 9, 5, 6, 4, 2, 1, 0 };
        static readonly int[] current = { -1, -1, -1 };      // slice of each drawing (waves, car, ship)
        static int[] painted;                              // slice -> drawing in it (-1 = the game's)
        static Texture2DArray copy; static Texture2D[] drawings; static int W, H, Mips; static TextureFormat Fmt;
        static float tRecheck = 25f; static int lastRequests = -1; static TypeIndex tiIc; static EntityQuery qIc; static bool qOk;
        public static bool RequestRecheck;
        static readonly List<float> launchRechecks = new List<float>(); static string launchReason = "";

        [HarmonyLib.HarmonyPostfix, HarmonyLib.HarmonyPatch(typeof(UIManager), nameof(UIManager.OnSpaceshipLaunch))]
        static void OnLaunch(bool start)
        {
            try
            {
                launchReason = start ? "simulation started" : "back to the garage";
                Signal.Launched = start;   // 0.24.0: Signal only touches the ports with the simulation running
                launchRechecks.Clear(); launchRechecks.Add(Time.unscaledTime + 1f); launchRechecks.Add(Time.unscaledTime + 5f);
            }
            catch (Exception) { }
        }

        public static string State()
        {
            var sb = new System.Text.StringBuilder("icon array ");
            try { sb.Append(mine == null ? "none" : (mine.depth + " slices" + (mine.Pointer == myPtr ? "" : " (?)"))); } catch (Exception) { sb.Append("?"); }
            if (painted != null) { sb.Append(", painted:"); for (int i = 0; i < painted.Length; i++) if (painted[i] >= 0) sb.Append(' ').Append(i).Append('=').Append(BeaconIcon.Names[painted[i]]); }
            return sb.ToString();
        }

        /// <summary>Radar icon for the beacon knob's choice (0 waves, 1 car, 2 ship).</summary>
        public static uint SlotOf(int k)
        {
            if (Request >= 0) return (uint)Request;
            if (failed || painted == null) return Reserve;
            if (k >= 0 && k < 3 && current[k] >= 0) return (uint)current[k];
            if (current[0] >= 0) return (uint)current[0];
            return Reserve;
        }

        /// <summary>Keeps a copy of the game's array and prepares the 3 drawings (the assignment comes in Refresh).</summary>
        static void Build(GameFeature gf, Texture2DArray arr)
        {
            step = "read size/format";
            W = arr.width; H = arr.height; int d = arr.depth; Mips = arr.mipmapCount; Fmt = arr.format;
            step = "copy of the game's icons";
            copy = new Texture2DArray(W, H, d, Fmt, Mips > 1, true);
            Il2CppKeep(copy.Pointer);
            int mc = System.Math.Min(Mips, copy.mipmapCount);
            for (int sl = 0; sl < d; sl++) for (int m = 0; m < mc; m++) Graphics.CopyTexture(arr, sl, m, copy, sl, m);
            step = "drawings";
            drawings = new[] { TextureOf(Drawing(W, H)), TextureOf(CarDrawing(W, H)), TextureOf(ShipDrawing(W, H)) };
            painted = new int[d]; for (int i = 0; i < d; i++) painted[i] = -1;
            current[0] = current[1] = current[2] = -1;
            mine = arr; myPtr = arr.Pointer; doneSet.Add(myPtr); wait = 1f; lastRequests = -1; RequestRecheck = true;
            ModLog.Info("radar: " + d + " game icons (" + W + "x" + H + ", " + Fmt + ", " + Mips + " mips); the beacon's icons go over the ones nobody is using");
        }
        static void Il2CppKeep(IntPtr p) => hold.Add(Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(p, false));
        static readonly List<IntPtr> hold = new List<IntPtr>();
        static Texture2D TextureOf(byte[] pr)
        {
            var px = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(W * H);
            IntPtr hPx = Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(px.Pointer, false);
            for (int i = 0; i < pr.Length; i++) px[i] = new Color32(pr[i], pr[i], pr[i], pr[i]);
            var t2 = new Texture2D(W, H, Fmt, Mips > 1, true); Il2CppKeep(t2.Pointer);
            t2.SetPixels32(px); t2.Apply(true, false);
            Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_free(hPx);
            return t2;
        }
        static void CopySlice(Texture src, int sliceSrc, int slice)
        {
            int mt = System.Math.Min(Mips, mine.mipmapCount);
            for (int m = 0; m < mt; m++) Graphics.CopyTexture(src, sliceSrc, m, mine, slice, m);
        }

        /// <summary>Called by the beacons' cycle: requests = bits of the drawings in use (1 waves, 2 car, 4 ship); ghosts = the beacons'
        /// entities (they don't count as "in use by the game").</summary>
        public static unsafe void Refresh(EntityManager em, int requests, HashSet<int> ghosts, float dt)
        {
            if (Request >= 0 || failed || painted == null || mine == null) return;
            tRecheck += dt;
            // 0.17.2 (Andre: "when I enter the simulation holding R or when I leave it"): UIManager.OnSpaceshipLaunch (start/stop) schedules
            // a recheck 1 s and 5 s later (the world's/objectives' entities are still being born on the launch frame)
            bool keyCode = false;
            if (launchRechecks.Count > 0 && Time.unscaledTime >= launchRechecks[0]) { launchRechecks.RemoveAt(0); keyCode = true; }
            if (!(keyCode || RequestRecheck || requests != lastRequests || tRecheck >= 30f)) return;
            RequestRecheck = false; tRecheck = 0f; lastRequests = requests;
            try
            {
                if (!qOk) { tiIc = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<RadarAppearanceIcon>()); var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiIc); qIc = em.CreateEntityQuery(a); qOk = true; }
                var usedSet = new HashSet<int>();
                foreach (var e in Ecs.QueryList(qIc))
                {
                    if (ghosts.Contains(e.Index)) continue;
                    var p = (uint*)em.GetComponentDataRawRO(e, tiIc); if (p != null && *p < (uint)painted.Length) usedSet.Add((int)*p);
                }
                var newVal = new int[] { -1, -1, -1 }; var occupied = new HashSet<int>();
                for (int k = 0; k < 3; k++)   // whoever already had a free slice stays on it (doesn't blink)
                    if ((requests & (1 << k)) != 0 && current[k] >= 0 && !usedSet.Contains(current[k])) { newVal[k] = current[k]; occupied.Add(current[k]); }
                for (int k = 0; k < 3; k++)
                {
                    if ((requests & (1 << k)) == 0 || newVal[k] >= 0) continue;
                    var order = new List<int>(); if (Slot >= 0 && Slot < painted.Length) order.Add(Slot); order.AddRange(Preference);
                    foreach (int s in order) if (s < painted.Length && !usedSet.Contains(s) && !occupied.Contains(s)) { newVal[k] = s; occupied.Add(s); break; }
                }
                // give back the slices that left, paint the new ones
                for (int s = 0; s < painted.Length; s++)
                {
                    int wants = -1; for (int k = 0; k < 3; k++) if (newVal[k] == s) wants = k;
                    if (painted[s] == wants) continue;
                    if (wants < 0) CopySlice(copy, s, s); else CopySlice(drawings[wants], 0, s);
                    painted[s] = wants;
                }
                bool changed = newVal[0] != current[0] || newVal[1] != current[1] || newVal[2] != current[2];
                current[0] = newVal[0]; current[1] = newVal[1]; current[2] = newVal[2];
                if (changed || keyCode)
                {
                    var sb = new System.Text.StringBuilder("radar: game icons in use now: ");
                    var l = new List<int>(usedSet); l.Sort(); sb.Append(l.Count == 0 ? "none" : string.Join(",", l));
                    for (int k = 0; k < 3; k++) if ((requests & (1 << k)) != 0) sb.Append(" | ").Append(BeaconIcon.Names[k]).Append(" -> ").Append(current[k] >= 0 ? "icon " + current[k] : "NO free icon (shows " + (current[0] >= 0 ? "the waves" : "a package") + ")");
                    ModLog.Info(sb.ToString() + (keyCode ? " (" + launchReason + ")" : ""));
                }
            }
            catch (Exception ex) { ModLog.Warn("radar: re-check failed: " + ex.Message); }
        }

        static void Paint(Texture2DArray arr, int slice, int w, int h, TextureFormat fmt, int mips, byte[] pr)
        {
            var px = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(w * h);
            IntPtr hPx = Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(px.Pointer, false);
            for (int i = 0; i < pr.Length; i++) px[i] = new Color32(pr[i], pr[i], pr[i], pr[i]);
            var t2 = new Texture2D(w, h, fmt, mips > 1, true);
            var hT2 = Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(t2.Pointer, false);
            t2.SetPixels32(px); t2.Apply(true, false);
            int mt = System.Math.Min(System.Math.Min(mips, t2.mipmapCount), arr.mipmapCount);
            for (int m = 0; m < mt; m++) Graphics.CopyTexture(t2, 0, m, arr, slice, m);
            Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_free(hT2); Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_free(hPx);
        }

        delegate bool Shape(float x, float y);
        /// <summary>Mask with 4x4 supersampling of a shape in -1..1 (y up).</summary>
        static byte[] Rasterize(int w, int h, Shape f)
        {
            var r = new byte[w * h]; const int S = 4;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int sum = 0;
                    for (int sj = 0; sj < S; sj++)
                        for (int si = 0; si < S; si++)
                            if (f((i + (si + 0.5f) / S) / w * 2f - 1f, (j + (sj + 0.5f) / S) / h * 2f - 1f)) sum++;
                    r[j * w + i] = (byte)(255 * sum / (S * S));
                }
            return r;
        }
        static bool Circle(float x, float y, float cx, float cy, float r) => (x - cx) * (x - cx) + (y - cy) * (y - cy) < r * r;
        static bool Tri(float x, float y, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d1 = (x - bx) * (ay - by) - (ax - bx) * (y - by), d2 = (x - cx) * (by - cy) - (bx - cx) * (y - cy), d3 = (x - ax) * (cy - ay) - (cx - ax) * (y - ay);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0; return !(neg && pos);
        }
        /// <summary>Car from the side (like the display): body, trapezoid cabin, 2 wheels.</summary>
        static byte[] CarDrawing(int w, int h) => Rasterize(w, h, (x, y) =>
            (x > -0.86f && x < 0.86f && y > -0.18f && y < 0.2f) ||
            Tri(x, y, -0.5f, 0.2f, 0.42f, 0.2f, 0.2f, 0.56f) || Tri(x, y, -0.5f, 0.2f, 0.2f, 0.56f, -0.36f, 0.56f) ||
            Circle(x, y, -0.5f, -0.24f, 0.24f) || Circle(x, y, 0.5f, -0.24f, 0.24f));
        /// <summary>Ship (rocket pointing up): body, nose, 2 fins.</summary>
        static byte[] ShipDrawing(int w, int h) => Rasterize(w, h, (x, y) =>
            (x > -0.2f && x < 0.2f && y > -0.5f && y < 0.36f) || Tri(x, y, -0.2f, 0.36f, 0.2f, 0.36f, 0f, 0.88f) ||
            Tri(x, y, -0.2f, -0.5f, -0.2f, -0.05f, -0.56f, -0.62f) || Tri(x, y, 0.2f, -0.5f, 0.2f, -0.05f, 0.56f, -0.62f) ||
            (x > -0.1f && x < 0.1f && y > -0.72f && y < -0.5f));

        /// <summary>0..255 mask: a dot in the center with two waves on each side ((( • ))), thick stroke like the game's icons.</summary>
        static byte[] Drawing(int w, int h)
        {
            var r = new byte[w * h]; const int S = 4;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int sum = 0;
                    for (int sj = 0; sj < S; sj++)
                        for (int si = 0; si < S; si++)
                        {
                            float x = (i + (si + 0.5f) / S) / w * 2f - 1f, y = (j + (sj + 0.5f) / S) / h * 2f - 1f;
                            float rr = (float)Math.Sqrt(x * x + y * y);
                            bool inside = rr < 0.24f;
                            if (!inside && Math.Atan2(Math.Abs(y), Math.Abs(x)) <= 48.0 * Math.PI / 180.0)
                                inside = Math.Abs(rr - 0.56f) < 0.095f || Math.Abs(rr - 0.88f) < 0.095f;
                            if (inside) sum++;
                        }
                    r[j * w + i] = (byte)(255 * sum / (S * S));
                }
            return r;
        }
    }
}
