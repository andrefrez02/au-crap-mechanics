using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace CrapMechanics
{
    /// <summary>Recoupler (0.7.7): re-attaches two pieces that a game Decoupler ALREADY separated.
    /// Rules: signal &gt;= 0.5 (level, no rising edge needed); touching (Tolerance slack) the releasing face (+Y) of a Decoupler of
    /// ANOTHER piece that has already fired. After locking/merging, it only acts again when the signal drops &lt; 0.5 or the pieces move apart.
    /// Stages: (1) PULL — a fixed joint that brings the recoupler, in ~0.6 s, to the right pose on the decoupler: the original garage
    /// pose (GarageTransform) if the two were neighbours there; otherwise flush (face to face, rotation in steps of 90).
    /// (2) MERGE (only in the "original" case) — the opposite of the decoupler: moves the blocks of the recoupler's piece to the
    /// decoupler's part, reconnects the game joint between the two, rearms the decoupler and asks the game to recalculate the part
    /// (SpaceshipPartConsistencyCheck). If anything fails, only the fixed joint stays.</summary>
    internal class Recoupler
    {
        public bool didWarnNet;   // 0.24.0-mp: already logged that these parts belong to the server
        public Entity block, joint, partA, partB, decoupler; public int warnings;
        public int state;          // 0 free, 1 pulling/locked (fixed joint), 2 merged
        public bool armed = true, original; public float tPull; public string reasonLog; public float tReason;
        public BodyFrame fb, fa0, fa1;        // fb on the recoupler's piece; fa0 (start pose) -> fa1 (right pose) on the decoupler's piece
        public int gameJoint = -1; public bool rearmDecoupler;
        public Entity anchor; public bool onAnchor; public float duration = 0.6f;   // 0.7.8: pulls while held by the piston base (heavy ship), not by the light top
        public Entity[] leds; public float3[] ledRest; public bool ledLit; public float tLed = 9f; public bool touching; public float tTouching = 9f;   // 0.11.2: green LEDs (extra renderers) that come out of the panel when it couples
    }

    /// <summary>Beacon: shows on the game's radar (own icon, see RadarIcon), in real time. The radar draws every entity with
    /// LocalToWorld + RadarAppearanceIcon (SCTick_Radar.FindIconsJob); I create a "ghost" entity with those two and copy the
    /// block's position every frame (I don't touch the block's entity).</summary>
    internal class Beacon { public Entity block, ghost; public bool hidden; public float tLog; public bool childrenLogged; public string name = ""; public float tName = 9f; public string guid; public bool noPower; public byte energyState = 255;
        public Entity knob; public Entity[] icons; public int icon, iconShown = -1; public float tKnob = 9f, knobLog = -9f; public bool iconsWarned; public int hint = -1; public Entity knobTip; public bool mirror, mirrorTested; }   // 0.17.0; 0.19.1: hint = icon written in the knob's hint

    internal static class Couplers
    {
        public static float Tolerance = 0.5f;    // m of slack to count as "touching" (0.7.0-0.7.5: 0.2; config Recoupler.tolerance)
        public static bool HideOwn = true;   // 0.17.3: config Beacon.hide_on_own_vehicle
        public static float HideNear = 40f;   // m: a beacon near the player doesn't show (it's the vehicle they are in)
        public static float BeaconPowerUse = 5f;    // 0.11.1: P/s the beacon asks for at its power port (config Beacon.power)
        static TypeIndex tiPlayer; static EntityQuery qPlayer; static bool playerOk;
        public static uint IconBeacon = 2;        // RadarIconID.PlanetPackage until RadarIcon builds its own icon
        static readonly Dictionary<long, Recoupler> recs = new Dictionary<long, Recoupler>();
        static readonly Dictionary<long, Beacon> beacons = new Dictionary<long, Beacon>();
        static TypeIndex tiIcon; static bool ok; static EntityQuery qDec; static bool qDecOk;

        public static bool Has(long k) => recs.ContainsKey(k) || beacons.ContainsKey(k);
        /// <summary>0.7.12: snapshot of the state for the error mark (F8).</summary>
        public static string State(EntityManager em)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var r in recs.Values)
            {
                sb.Append("  SC_Recoupler ").Append(r.block.Index).Append(": ").Append(r.state == 0 ? "free" : r.state == 1 ? (r.onAnchor ? "pulling" : "locked") : "merged");
                if (!Ecs.IsNull(r.decoupler)) sb.Append(", decoupler ").Append(r.decoupler.Index);
                if (!Ecs.IsNull(r.partA)) sb.Append(", part ").Append(r.partA.Index).Append(" / ").Append(r.partB.Index);
                sb.Append('\n');
            }
            foreach (var b in beacons.Values) sb.Append("  SC_Beacon ").Append(b.block.Index).Append(b.hidden ? ": hidden" : ": visible").Append(Ecs.IsNull(b.ghost) ? ", no ghost" : ", ghost " + b.ghost.Index).Append('\n');
            try { sb.Append(Levers.State(em)); } catch (Exception) { }
            if (sb.Length == 0) sb.Append("  (none)\n");
            return sb.ToString();
        }
        /// <summary>0.26.3: all of them (so the piston merge knows whether some lock uses the part).</summary>
        internal static IEnumerable<Recoupler> All() => recs.Values;
        public static IEnumerable<Recoupler> Locked() { foreach (var r in recs.Values) if (r.state == 1 && !r.onAnchor && !Ecs.IsNull(r.joint)) yield return r; }
        /// <summary>0.22.0: engaged recouplers (locked by a joint or already merged into the decoupler's part) — for SlipRings.cs (ring glued to the decoupler).</summary>
        public static IEnumerable<Recoupler> Coupled() { foreach (var r in recs.Values) if ((r.state == 1 && !r.onAnchor && !Ecs.IsNull(r.joint)) || r.state == 2) yield return r; }
        public static void NewRecoupler(Entity e) { recs[Ecs.K(e)] = new Recoupler { block = e }; ModLog.Info("SC_Recoupler found: block " + e.Index + ":" + e.Version); }
        public static void NewBeacon(Entity e) { beacons[Ecs.K(e)] = new Beacon { block = e }; ModLog.Info("SC_Beacon found: block " + e.Index + ":" + e.Version); }
        public static IEnumerable<Beacon> Beacons() => beacons.Values;   // 0.19.13: 100 km rule (IsActive)

        public static void Step(EntityManager em, float dt)
        {
            if (!ok) { ok = true; try { tiIcon = TypeManager.GetTypeIndex(Il2CppType.Of<RadarAppearanceIcon>()); } catch (Exception ex) { ModLog.Error("beacon: RadarAppearanceIcon type: " + ex.Message); } }
            var deadSet = new List<long>();
            foreach (var kv in recs)
            {
                try { if (!Ecs.Alive(em, kv.Value.block)) { Drop(em, kv.Value, "block is gone"); deadSet.Add(kv.Key); continue; } RecStep(em, kv.Value); }
                catch (Exception ex) { if (kv.Value.warnings++ < 5) ModLog.Warn("recoupler " + kv.Value.block.Index + ": " + ex.Message); }
            }
            foreach (var k in deadSet) recs.Remove(k);
            deadSet.Clear();
            foreach (var kv in beacons)
            {
                try
                {
                    if (!Ecs.Alive(em, kv.Value.block)) { DeleteGhost(em, kv.Value); deadSet.Add(kv.Key); continue; }
                    // 0.11.0 (label research): lists once the block's children (renderers, label, text) and their components
                    if (!kv.Value.childrenLogged && Ecs.IsPart(em, Ecs.Parent(em, kv.Value.block)))
                    {
                        kv.Value.childrenLogged = true;
                        var sb = new System.Text.StringBuilder("SC_Beacon " + kv.Value.block.Index + ": block children:");
                        foreach (var f in Ecs.Children(em, kv.Value.block)) { var lt = Ecs.LT(em, f); sb.Append("\n    ").Append(f.Index).Append(" at (").Append(Ecs.F3(lt.Position)).Append("): ").Append(Ecs.Components(em, f)); }
                        sb.Append("\n  block components: ").Append(Ecs.Components(em, kv.Value.block));
                        ModLog.Info(sb.ToString());
                    }
                    try { BeaconIcon.Step(em, kv.Value, UnityEngine.Time.deltaTime); } catch (Exception exk) { ModLog.Warn("beacon " + kv.Value.block.Index + " knob: " + exk.Message); }
                    BeaconStep(em, kv.Value);
                }
                catch (Exception ex) { ModLog.Warn("beacon " + kv.Value.block.Index + ": " + ex.Message); }
            }
            foreach (var k in deadSet) beacons.Remove(k);
            DestroyOrphans(em, dt);
            if (beacons.Count > 0) DiagRadar(em);
            // 0.17.1: beacon drawings only over icons the game isn't using right now (RadarIcon.Refresh)
            try
            {
                int requests = 0; var ghosts = new HashSet<int>();
                foreach (var bc in beacons.Values) { if (Ecs.IsPart(em, Ecs.Parent(em, bc.block))) requests |= 1 << bc.icon; if (!Ecs.IsNull(bc.ghost)) ghosts.Add(bc.ghost.Index); }
                RadarIcon.Refresh(em, requests, ghosts, UnityEngine.Time.deltaTime);
                // 0.19.2: list of what goes straight into the radar buffer (RadarWrite, after TransformSystemGroup)
                radarIcons.Clear();
                foreach (var bc in beacons.Values) if (Ecs.Alive(em, bc.ghost) && !bc.hidden && !bc.noPower) radarIcons.Add((Ecs.Position(em, bc.block), RadarIcon.SlotOf(bc.icon)));
            }
            catch (Exception ex) { ModLog.Warn("radar icons: " + ex.Message); }
        }

        // ---------------------------------------------------------------- radar diagnostics (0.7.9)
        // The game's radar (SCTick_Radar.FindIconsJob) writes each icon into a RadarLocationGPUData buffer {_uv, _type, _isTarget}
        // (16 B). Here I read that buffer and log which types are in it: if 8 (the beacon's) doesn't show up, the game didn't even pick up
        // our ghost entity. And I log the components of a real game icon, to copy whatever is missing.
        static TypeIndex tiGPU; static EntityQuery qGPU, qIcons; static bool diagOk, diagFailed, compLogged, ghostLogged; static readonly List<TypeIndex> extraTags = new List<TypeIndex>(); static float tDiag = 9f; static string diagLog; static int diagLogs;
        static unsafe void DiagRadar(EntityManager em)
        {
            if (diagFailed || diagLogs >= 60) return;   // 0.17.3: logs the radar buffer again (when it changes) to find out why the icon doesn't show
            tDiag += UnityEngine.Time.deltaTime; if (tDiag < 3f) return; tDiag = 0f;
            try
            {
                if (!diagOk)
                {
                    diagOk = true;
                    tiGPU = TypeManager.GetTypeIndex(Il2CppType.Of<SCTick_Radar.RadarLocationGPUData>());
                    var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiGPU); qGPU = em.CreateEntityQuery(a);
                    var b = new Il2CppStructArray<ComponentType>(1); b[0] = ComponentType.ReadOnly(tiIcon); qIcons = em.CreateEntityQuery(b);
                }
                var sb = new System.Text.StringBuilder();
                var owners = Ecs.QueryList(qGPU);
                foreach (var e in owners)
                {
                    int n = em.GetBufferLength(e, tiGPU); var p = (byte*)em.GetBufferRawRO(e, tiGPU);
                    var cnt = new SortedDictionary<uint, int>();
                    for (int i = 0; i < n && p != null; i++) { uint t = *(uint*)(p + i * 16 + 8); cnt.TryGetValue(t, out int c); cnt[t] = c + 1; }
                    sb.Append(" [entity ").Append(e.Index).Append(": ").Append(n).Append(" icon(s)");
                    foreach (var kv in cnt) sb.Append(", type ").Append(kv.Key).Append(" x").Append(kv.Value);
                    sb.Append("]");
                }
                int ghosts = 0, fromGame = 0; Entity example = Entity.Null, mine = Entity.Null;
                var mineSet = new HashSet<int>(); foreach (var bc in beacons.Values) if (!Ecs.IsNull(bc.ghost)) mineSet.Add(bc.ghost.Index);
                foreach (var e in Ecs.QueryList(qIcons)) { if (mineSet.Contains(e.Index)) { ghosts++; mine = e; } else { fromGame++; if (Ecs.IsNull(example)) example = e; } }
                string line = "radar/diag: radar buffers:" + (owners.Count == 0 ? " none" : sb.ToString()) + " | entities with RadarAppearanceIcon: " + ghosts + " ours, " + fromGame + " from the game";
                if (!compLogged && !Ecs.IsNull(example)) { compLogged = true; MissingTags(em, example); }
                if (ghosts > 0 && line != diagLog) { diagLog = line; diagLogs++; ModLog.Info(line + " | ours use icon " + string.Join(",", GhostIcons(em)) + " | " + RadarIcon.State()); }
            }
            catch (Exception ex) { diagFailed = true; ModLog.Warn("radar/diag failed: " + ex.Message); }
        }

        static unsafe List<uint> GhostIcons(EntityManager em)
        {
            var l = new List<uint>();
            foreach (var bc in beacons.Values) { if (Ecs.IsNull(bc.ghost) || !Ecs.Alive(em, bc.ghost)) continue; var p = (Ecs.HasC(em, bc.ghost, tiIcon) ? (uint*)em.GetComponentDataRawRO(bc.ghost, tiIcon) : null); if (p != null) l.Add(*p); }
            return l;
        }

        /// <summary>0.7.10: the radar buffer only had the HQ icon (type 0) — our ghost (type 8) never got in. I copy onto the
        /// ghost the TAGS (components without data) that a real game icon has and the ghost doesn't — the radar query may
        /// require one of them. Components with data I don't copy (only log the name) so as not to wake up other game systems.</summary>
        static unsafe void MissingTags(EntityManager em, Entity example)
        {
            try
            {
                var arr = em.GetComponentTypes(example, Unity.Collections.Allocator.Temp);
                var lacking = new System.Text.StringBuilder();
                for (int i = 0; i < arr.Length; i++)
                {
                    var ti = arr[i].TypeIndex;
                    if (ti.Value == Ecs.tiLTW.Value || ti.Value == tiIcon.Value) continue;
                    bool tag = false; try { tag = TypeManager.GetTypeInfo(ti).IsZeroSized; } catch (Exception) { }
                    string name = "#" + ti.Value; try { var t = TypeManager.GetType(ti); if (t != null) name = t.FullName; } catch (Exception) { }
                    if (tag) { extraTags.Add(ti); lacking.Append(" +").Append(name); } else lacking.Append(" (data, not copying: ").Append(name).Append(")");
                }
                ModLog.Info("radar/diag: for the ghost:" + lacking);
                foreach (var b in beacons.Values) if (Ecs.Alive(em, b.ghost)) ByTags(em, b.ghost);
            }
            catch (Exception ex) { ModLog.Warn("radar/diag: tags: " + ex.Message); }
        }
        static void ByTags(EntityManager em, Entity e)
        {
            foreach (var ti in extraTags) { try { if (!em.HasComponent(e, ComponentType.ReadOnly(ti))) em.AddComponent(e, ComponentType.ReadWrite(ti)); } catch (Exception) { } }
        }

        static unsafe string Components(EntityManager em, Entity e)
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
                    try { if (TypeManager.GetTypeInfo(ct.TypeIndex).IsZeroSized) name = (name ?? "#" + ct.TypeIndex.Value) + "(tag)"; } catch (Exception) { }
                    sb.Append(i == 0 ? "" : ", ").Append(name ?? ("#" + ct.TypeIndex.Value));
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "(could not list: " + ex.Message + ")"; }
        }

        // ---------------------------------------------------------------- recoupler
        public static bool Join = true; public static float PullTime = 0.6f, MaxAngle = 30f;
        static TypeIndex tiChild, tiPrev, tiLEG, tiGT, tiConn, tiCheck; static bool tiRecOk;

        static unsafe void RecStep(EntityManager em, Recoupler r)
        {
            if (!tiRecOk)
            {
                tiRecOk = true;
                tiChild = TypeManager.GetTypeIndex(Il2CppType.Of<Child>()); tiPrev = TypeManager.GetTypeIndex(Il2CppType.Of<PreviousParent>());
                tiLEG = TypeManager.GetTypeIndex(Il2CppType.Of<LinkedEntityGroup>()); tiGT = TypeManager.GetTypeIndex(Il2CppType.Of<GarageTransform>());
                tiConn = TypeManager.GetTypeIndex(Il2CppType.Of<SCConnectedJoint>()); tiCheck = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipPartConsistencyCheck>());
            }
            var pa = Ecs.Parent(em, r.block);
            if (!Ecs.IsPart(em, pa)) { r.state = 0; r.armed = true; RefreshLed(em, r); return; }   // garage
            RefreshLed(em, r);
            float s = 0f; var pe = Ecs.PortOfType(em, r.block, Ecs.PortInput);
            if (!Ecs.IsNull(pe)) Ecs.ReadInput(em, pe, out s, out _, out _);
            bool high = s >= 0.5f;
            if (!high) r.armed = true;

            if (r.state == 2)
            {
                // merged: keeps going while recoupler and decoupler are in the same part; rearms the decoupler when its signal drops
                if (r.rearmDecoupler) Rearm(em, r);
                if (!Ecs.Alive(em, r.decoupler) || Ecs.Parent(em, r.decoupler).Index != pa.Index)
                {
                    r.state = 0; ModLog.Info("SC_Recoupler " + r.block.Index + ": separated again (decoupler fired or broke)");
                }
                return;
            }
            if (r.state == 1)
            {
                bool valid = Ecs.Alive(em, r.joint) && Ecs.IsPart(em, r.partA) && Ecs.IsPart(em, r.partB) && pa.Index == r.partA.Index && Ecs.Alive(em, r.decoupler) && Ecs.Parent(em, r.decoupler).Index == r.partB.Index && (!r.onAnchor || Ecs.IsPart(em, r.anchor));
                if (!valid) { Drop(em, r, "pieces changed (break or load)"); return; }
                if (!high) { Drop(em, r, "signal < 0.5"); return; }
                Pull(em, r);
                return;
            }
            // free: if it isn't armed, rearm when it moves away from every decoupler
            if (!high) return;
            string reason; var bestPick = Search(em, r, pa, out reason, out float dist);
            if (!r.armed)
            {
                if (Ecs.IsNull(bestPick) && dist > Tolerance + 0.2f) r.armed = true;
                else return;
            }
            if (Ecs.IsNull(bestPick))
            {
                // at most 1 line every 3 s (the distance changes every frame)
                r.tReason -= UnityEngine.Time.deltaTime;
                if (r.tReason <= 0f) { ModLog.Info("SC_Recoupler " + r.block.Index + ": signal on, not coupling yet (" + reason + ")"); r.reasonLog = reason; r.tReason = 3f; }
                return;
            }
            // 0.7.9: only pulls if it is at most MaxAngle crooked (turning 7 t by 90 degrees with the wheels on the ground blew up the car)
            r.decoupler = bestPick;
            RightPose(em, r, out _, out var axC, out var ayC, out _);
            Ecs.Axes(em, r.block, out var rx0, out var ry0, out _);
            float ang = Angle(rx0, ry0, axC, ayC);
            if (ang > MaxAngle)
            {
                r.tReason -= UnityEngine.Time.deltaTime;
                if (r.tReason <= 0f) { ModLog.Info("SC_Recoupler " + r.block.Index + ": signal on, not coupling yet (it is " + ang.ToString("F0") + " degrees off relative to Decoupler " + bestPick.Index + "; the maximum is " + MaxAngle.ToString("F0") + " — approach straighter)"); r.tReason = 3f; }
                return;
            }
            r.reasonLog = null; r.tReason = 0f;
            Lock(em, r, pa, Ecs.Parent(em, bestPick), bestPick);
            r.duration = PullTime + ang * 0.03f;
        }

        /// <summary>Already-fired Decoupler nearest to the recoupler's face, within the slack. reason = why none was found.</summary>
        static Entity Search(EntityManager em, Recoupler r, Entity pa, out string reason, out float distMin)
        {
            var pR = Ecs.Position(em, r.block);
            Entity bestPick = Entity.Null; float eBest = 99f; reason = "no Decoupler nearby"; distMin = 1e9f; string nearest = null;
            foreach (var d in Decouplers(em))
            {
                var pd = Ecs.Parent(em, d); if (!Ecs.IsPart(em, pd) || pd.Index == pa.Index) continue;
                Ecs.Axes(em, d, out _, out var eyD, out _);
                var v = Ecs.Sub(pR, Ecs.Position(em, d)); float ao = Ecs.Dot(v, eyD);
                float lat = Ecs.Len(Ecs.Sub(v, Ecs.Mul(eyD, ao)));
                float error = System.Math.Abs(ao - 0.5f) + lat;
                float dd = Ecs.Len(Ecs.Sub(v, Ecs.Mul(eyD, 0.5f)));
                if (dd < distMin) { distMin = dd; nearest = "Decoupler " + d.Index + " at " + dd.ToString("F2") + " m from the release face (along " + (ao - 0.5f).ToString("F2") + ", sideways " + lat.ToString("F2") + ")"; }
                if (ao < 0.5f - Tolerance || ao > 0.5f + Tolerance || lat > Tolerance) continue;
                int stuckOnes = 0; try { stuckOnes = em.GetBufferLength(d, Ecs.tiDecJ); } catch (Exception) { }
                if (stuckOnes > 0) { reason = "Decoupler " + d.Index + " has not decoupled yet"; continue; }
                if (error < eBest) { eBest = error; bestPick = d; }
            }
            if (Ecs.IsNull(bestPick) && reason == "no Decoupler nearby" && nearest != null) reason = "closest: " + nearest + "; tolerance " + Tolerance.ToString("F2") + " m";
            return bestPick;
        }

        static float Angle(float3 ax, float3 ay, float3 bx, float3 by)
        {
            float c = System.Math.Min(Ecs.Dot(Ecs.Norm(ax), Ecs.Norm(bx)), Ecs.Dot(Ecs.Norm(ay), Ecs.Norm(by)));
            return (float)(System.Math.Acos(System.Math.Max(-1f, System.Math.Min(1f, c))) * 180.0 / System.Math.PI);
        }
        static float3 X1 => new float3 { x = 1f }; static float3 Y1 => new float3 { y = 1f };
        static quaternion QMul(quaternion a, quaternion b)
        {
            float4 p = a.value, q = b.value;
            return new quaternion { value = new float4 { x = p.w * q.x + p.x * q.w + p.y * q.z - p.z * q.y, y = p.w * q.y - p.x * q.z + p.y * q.w + p.z * q.x, z = p.w * q.z + p.x * q.y - p.y * q.x + p.z * q.w, w = p.w * q.w - p.x * q.x - p.y * q.y - p.z * q.z } };
        }
        static float3 Snap(float3 v) { float ax = System.Math.Abs(v.x), ay = System.Math.Abs(v.y), az = System.Math.Abs(v.z); return ax >= ay && ax >= az ? new float3 { x = System.Math.Sign(v.x) } : ay >= az ? new float3 { y = System.Math.Sign(v.y) } : new float3 { z = System.Math.Sign(v.z) }; }
        static float3 InWorld(float3 l, float3 ex, float3 ey, float3 ez) => Ecs.Add(Ecs.Add(Ecs.Mul(ex, l.x), Ecs.Mul(ey, l.y)), Ecs.Mul(ez, l.z));

        /// <summary>Right pose of the recoupler (in the world, now): point + X and Y axes. "original" = they were neighbours in the garage.</summary>
        static unsafe void RightPose(EntityManager em, Recoupler r, out float3 p, out float3 ax, out float3 ay, out bool original)
        {
            Ecs.Axes(em, r.decoupler, out var dx, out var dy, out var dz); var pd = Ecs.Position(em, r.decoupler);
            original = false;
            try
            {
                if (em.HasComponent(r.block, ComponentType.ReadOnly(tiGT)) && em.HasComponent(r.decoupler, ComponentType.ReadOnly(tiGT)))
                {
                    var gr = *(Ecs.HasC(em, r.block, tiGT) ? (RigidTransform*)em.GetComponentDataRawRO(r.block, tiGT) : null); var gd = *(Ecs.HasC(em, r.decoupler, tiGT) ? (RigidTransform*)em.GetComponentDataRawRO(r.decoupler, tiGT) : null);
                    var qi = Ecs.Inv(gd.rot);
                    var tp = Ecs.Rot(qi, Ecs.Sub(gr.pos, gd.pos));
                    if (Ecs.Dist(tp, new float3 { y = 0.5f }) < 0.02f)
                    {
                        original = true;
                        p = Ecs.Add(pd, InWorld(tp, dx, dy, dz));
                        ax = InWorld(Ecs.Rot(qi, Ecs.Rot(gr.rot, X1)), dx, dy, dz); ay = InWorld(Ecs.Rot(qi, Ecs.Rot(gr.rot, Y1)), dx, dy, dz);
                        return;
                    }
                }
            }
            catch (Exception) { }
            // flush: center 0.5 m off the releasing face, the recoupler's axes rounded to the decoupler's
            Ecs.Axes(em, r.block, out var rx, out var ry, out _);
            var lx = Snap(new float3 { x = Ecs.Dot(rx, dx), y = Ecs.Dot(rx, dy), z = Ecs.Dot(rx, dz) });
            var ly = Snap(new float3 { x = Ecs.Dot(ry, dx), y = Ecs.Dot(ry, dy), z = Ecs.Dot(ry, dz) });
            if (System.Math.Abs(Ecs.Dot(lx, ly)) > 0.5f) { lx = new float3 { x = Ecs.Dot(rx, dx), y = Ecs.Dot(rx, dy), z = Ecs.Dot(rx, dz) }; ly = new float3 { x = Ecs.Dot(ry, dx), y = Ecs.Dot(ry, dy), z = Ecs.Dot(ry, dz) }; }
            p = Ecs.Add(pd, Ecs.Mul(dy, 0.5f)); ax = Ecs.Norm(InWorld(lx, dx, dy, dz)); ay = Ecs.Norm(InWorld(ly, dx, dy, dz));
        }

        static unsafe void Lock(EntityManager em, Recoupler r, Entity pa, Entity pb, Entity dec)
        {
            // 0.24.0-mp: locking is a joint + merging parts — only on the machine that owns both (the server creates/destroys parts on the network)
            if (!Multi.MyJoint(em, pa, pb))
            {
                if (!r.didWarnNet) { r.didWarnNet = true; ModLog.Info("SC_Recoupler " + r.block.Index + ": the server owns these parts — coupling left to the host"); }
                return;
            }
            r.decoupler = dec; r.partA = pa; r.partB = pb;
            // who holds during the pull: if the decoupler is on top of a piston, the piston's BASE (ship, heavy) — otherwise the
            // light top (110 kg) would be the one going to the car (7 t). The piston keeps the top still relative to the base.
            var baseP = JointSystem.PistonBase(pb); r.onAnchor = !Ecs.IsNull(baseP) && Ecs.IsPart(em, baseP) && baseP.Index != pa.Index; r.anchor = r.onAnchor ? baseP : pb;
            var pR = Ecs.Position(em, r.block); Ecs.Axes(em, r.block, out var rx, out var ry, out _);
            r.fb = Ecs.Frame(em, pa, pR, rx, ry);                  // on the recoupler's piece: the recoupler itself
            r.fa0 = Ecs.Frame(em, r.anchor, pR, rx, ry);           // on the holder: where it is now
            RightPose(em, r, out var pC, out var axC, out var ayC, out r.original);
            r.fa1 = Ecs.Frame(em, r.anchor, pC, axC, ayC);         // ... and where it has to be
            var pj = PhysicsJoint.CreateFixed(r.fa0, r.fb);
            var pair = new PhysicsConstrainedBodyPair(); pair.Entities = new EntityPair { EntityA = r.anchor, EntityB = pa }; pair.EnableCollision = 0;
            var kinds = new Il2CppStructArray<ComponentType>(3);
            kinds[0] = ComponentType.ReadWrite(Ecs.tiPar); kinds[1] = ComponentType.ReadWrite(Ecs.tiJoint); kinds[2] = ComponentType.ReadWrite(Ecs.tiPWI);
            var e = em.CreateEntity(kinds);
            *(Ecs.HasC(em, e, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(e, Ecs.tiPar) : null) = pair;
            *(Ecs.HasC(em, e, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(e, Ecs.tiJoint) : null) = pj;
            r.joint = e; r.state = 1; r.armed = false; r.tPull = 0f;
            ModLog.Info("SC_Recoupler " + r.block.Index + ": COUPLING to Decoupler " + dec.Index + " (part " + pa.Index + " " + Ecs.Mass(em, pa).ToString("F0") + " kg + part " + pb.Index + " " + Ecs.Mass(em, pb).ToString("F0") + " kg), pulling to the " + (r.original ? "original garage pose" : "straight flush pose") + (r.onAnchor ? " anchored to the piston base (part " + r.anchor.Index + " " + Ecs.Mass(em, r.anchor).ToString("F0") + " kg)" : "") + ", initial error " + Ecs.Dist(pR, pC).ToString("F2") + " m");
        }

        /// <summary>Takes frame A from fa0 to fa1 in PullTime; when it arrives and is aligned, merges (original case).</summary>
        static unsafe void Pull(EntityManager em, Recoupler r)
        {
            if (r.tPull < 0f) return;   // already done (locked without merging)
            r.tPull += UnityEngine.Time.deltaTime;
            float t = r.duration <= 0f ? 1f : System.Math.Min(1f, r.tPull / r.duration); t = t * t * (3f - 2f * t);
            var fa = new BodyFrame();
            fa.Position = Ecs.Add(Ecs.Mul(r.fa0.Position, 1f - t), Ecs.Mul(r.fa1.Position, t));
            var ax = Ecs.Norm(Ecs.Add(Ecs.Mul(r.fa0.Axis, 1f - t), Ecs.Mul(r.fa1.Axis, t)));
            var pp = Ecs.Add(Ecs.Mul(r.fa0.PerpendicularAxis, 1f - t), Ecs.Mul(r.fa1.PerpendicularAxis, t));
            pp = Ecs.Norm(Ecs.Sub(pp, Ecs.Mul(ax, Ecs.Dot(pp, ax))));
            fa.Axis = ax; fa.PerpendicularAxis = pp;
            var pj = (Ecs.HasC(em, r.joint, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(r.joint, Ecs.tiJoint) : null);
            int v = pj->m_Version; var newVal = PhysicsJoint.CreateFixed(fa, r.fb); newVal.m_Version = (byte)(v + 1); *pj = newVal;
            if (t < 1f) return;
            // arrived: measure the real error
            RightPose(em, r, out var pC, out var axC, out var ayC, out _);
            Ecs.Axes(em, r.block, out var rx, out var ry, out _);
            float dErr = Ecs.Dist(Ecs.Position(em, r.block), pC);
            float aErr = (float)(System.Math.Acos(System.Math.Min(1f, System.Math.Min(Ecs.Dot(rx, axC), Ecs.Dot(ry, ayC)))) * 180.0 / System.Math.PI);
            if ((dErr > 0.02f || aErr > 2f) && r.tPull < r.duration + 2f) return;   // wait to settle, up to 2 s
            // 0.28.0: merging moves blocks between parts and destroys one straight in the ECS — the game sends no event for that, so on a network the
            // clients would keep the two parts and stop getting the poses of the destroyed one. On a network the recoupler only locks (a fixed
            // joint between two parts the game keeps syncing as usual).
            bool network = Multi.Networked(em);
            if (network && r.original && Join && !r.didWarnNet) { r.didWarnNet = true; ModLog.Info("SC_Recoupler " + r.block.Index + ": on a network — locks instead of merging the parts (a merge isn't sent to the other players)"); }
            if (!r.original || !Join || network)
            {
                r.tPull = -1f; LockOnDecoupler(em, r);
                ModLog.Info("SC_Recoupler " + r.block.Index + ": RECOUPLED (locked, error " + (dErr * 100f).ToString("F1") + " cm / " + aErr.ToString("F1") + " degrees)" + (!r.original ? " — they were not neighbors in the garage, so the parts are not merged" : network ? " — on a network the parts are not merged" : " — merge disabled in config"));
                return;
            }
            if (dErr > 0.05f || aErr > 5f) { r.tPull = -1f; LockOnDecoupler(em, r); ModLog.Warn("SC_Recoupler " + r.block.Index + ": did not align (" + (dErr * 100f).ToString("F1") + " cm / " + aErr.ToString("F1") + " degrees) — only locked"); return; }
            string failure = MergeParts(em, r);
            if (failure != null) { r.tPull = -1f; LockOnDecoupler(em, r); ModLog.Warn("SC_Recoupler " + r.block.Index + ": merge failed (" + failure + ") — only locked"); }
        }

        /// <summary>Swaps the pulling joint (held by the ship) for a fixed one between the decoupler and the recoupler at the current pose — so the
        /// piston keeps moving whatever got locked.</summary>
        static unsafe void LockOnDecoupler(EntityManager em, Recoupler r)
        {
            if (!r.onAnchor) return;
            try
            {
                var pR = Ecs.Position(em, r.block); Ecs.Axes(em, r.block, out var rx, out var ry, out _);
                var fa = Ecs.Frame(em, r.partB, pR, rx, ry); var fb = Ecs.Frame(em, r.partA, pR, rx, ry);
                var pj = (Ecs.HasC(em, r.joint, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(r.joint, Ecs.tiJoint) : null); int v = pj->m_Version;
                var newVal = PhysicsJoint.CreateFixed(fa, fb); newVal.m_Version = (byte)(v + 1); *pj = newVal;
                var pair = (Ecs.HasC(em, r.joint, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(r.joint, Ecs.tiPar) : null);
                pair->Entities = new EntityPair { EntityA = r.partB, EntityB = r.partA };
                r.onAnchor = false; r.anchor = r.partB;
            }
            catch (Exception ex) { ModLog.Warn("SC_Recoupler " + r.block.Index + ": handing the lock over to the decoupler failed: " + ex.Message); }
        }

        // ---- BufferHeader (Entities): {Pointer, Length, Capacity}; elements at Pointer (heap) or right after the header (inline)
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] internal unsafe struct Hdr { public byte* Pointer; public int Length; public int Capacity; }
        internal static unsafe Hdr* Header(EntityManager em, Entity e, TypeIndex ti)
        {
            var h = (Ecs.HasC(em, e, ti) ? (Hdr*)em.GetComponentDataRawRW(e, ti) : null); if (h == null) return null;
            int n = em.GetBufferLength(e, ti); var raw = (byte*)em.GetBufferRawRW(e, ti);
            byte* elems = h->Pointer != null ? h->Pointer : (byte*)h + sizeof(Hdr);
            if (h->Length != n || elems != raw || h->Capacity < n) return null;
            return h;
        }
        internal static unsafe byte* Elems(Hdr* h) => h->Pointer != null ? h->Pointer : (byte*)h + sizeof(Hdr);
        internal static unsafe void Attach(Hdr* h, void* item, int size)
        {
            if (h->Length >= h->Capacity)
            {
                int newOne = System.Math.Max(8, h->Capacity * 2);
                var p = (byte*)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Malloc((long)newOne * size, 16, Unity.Collections.Allocator.Persistent);
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(p, Elems(h), (long)h->Length * size);
                if (h->Pointer != null) Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free(h->Pointer, Unity.Collections.Allocator.Persistent);
                h->Pointer = p; h->Capacity = newOne;
            }
            Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(Elems(h) + (long)h->Length * size, item, size);
            h->Length++;
        }
        internal static unsafe bool HasInt(Hdr* h, int v) { var p = (int*)Elems(h); for (int i = 0; i < h->Length; i++) if (p[i] == v) return true; return false; }

        /// <summary>The opposite of the decoupler. Returns null on success, otherwise the reason.</summary>
        static unsafe string MergeParts(EntityManager em, Recoupler r)
        {
            var dec = r.decoupler; var pDec = r.partB; var pRec = r.partA;
            // 1) game joint between decoupler and recoupler (the one the decoupler broke)
            var joints = Ecs.Joints(em, out int n); if (joints == null) return "no joint list";
            int k = -1;
            for (int i = 0; i < n; i++) { var sj = &joints[i]; if ((sj->_a.Index == dec.Index && sj->_b.Index == r.block.Index) || (sj->_b.Index == dec.Index && sj->_a.Index == r.block.Index)) { k = i; break; } }
            if (k < 0) return "game joint between the Decoupler and the Recoupler not found (were they not attached in the garage?)";
            // 2) buffer headers (everything validated before touching anything)
            var cDec = Header(em, dec, tiConn); var cRec = Header(em, r.block, tiConn);
            var chDec = Header(em, pDec, tiChild); var chRec = Header(em, pRec, tiChild);
            if (cDec == null || cRec == null) return "SCConnectedJoint buffer has an unexpected format";
            if (chDec == null || chRec == null) return "the parts' Child buffer has an unexpected format";
            var children = new List<Entity>(); var pf = (Entity*)Elems(chRec); for (int i = 0; i < chRec->Length; i++) children.Add(pf[i]);
            if (children.Count == 0) return "recoupler part has no children";
            // garage transform: pose on the decoupler's part = LT_dec * inv(GT_dec) * GT_child
            var ltDec = *(Ecs.HasC(em, dec, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(dec, Ecs.tiLT) : null);
            var gDec = *(Ecs.HasC(em, dec, tiGT) ? (RigidTransform*)em.GetComponentDataRawRO(dec, tiGT) : null); var qiG = Ecs.Inv(gDec.rot);
            var newOnes = new List<(Entity e, LocalTransform lt)>();
            foreach (var f in children)
            {
                if (!em.HasComponent(f, ComponentType.ReadOnly(tiGT)) || !em.HasComponent(f, ComponentType.ReadOnly(Ecs.tiLT))) return "block " + f.Index + " has no GarageTransform/LocalTransform";
                var g = *(Ecs.HasC(em, f, tiGT) ? (RigidTransform*)em.GetComponentDataRawRO(f, tiGT) : null); var lt = *(Ecs.HasC(em, f, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(f, Ecs.tiLT) : null);
                var rel = Ecs.Rot(qiG, Ecs.Sub(g.pos, gDec.pos));
                lt.Position = Ecs.Add(ltDec.Position, Ecs.Rot(ltDec.Rotation, rel));
                lt.Rotation = QMul(ltDec.Rotation, QMul(qiG, g.rot));
                newOnes.Add((f, lt));
            }
            // 3) apply: blocks -> decoupler's part
            bool hasPrev = false;
            foreach (var (f, lt) in newOnes)
            {
                *(Ecs.HasC(em, f, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(f, Ecs.tiLT) : null) = lt;
                *(Ecs.HasC(em, f, Ecs.tiParent) ? (Entity*)em.GetComponentDataRawRW(f, Ecs.tiParent) : null) = pDec;
                if (em.HasComponent(f, ComponentType.ReadOnly(tiPrev))) { *(Ecs.HasC(em, f, tiPrev) ? (Entity*)em.GetComponentDataRawRW(f, tiPrev) : null) = pDec; hasPrev = true; }
            }
            chDec = Header(em, pDec, tiChild); chRec = Header(em, pRec, tiChild);   // (re-read: the pointers can change with writes)
            if (chDec == null || chRec == null) return "Child changed midway (blocks already moved!)";
            foreach (var (f, _) in newOnes) { var ff = f; Attach(chDec, &ff, sizeof(Entity)); }
            chRec->Length = 0;
            // 4) reconnect the joint and rearm the decoupler
            var sjk = &joints[k]; sjk->_currentStrain = 0f; if (sjk->_breakForce <= 0f) sjk->_breakForce = 3300f;
            cDec = Header(em, dec, tiConn); cRec = Header(em, r.block, tiConn);
            if (cDec != null && !HasInt(cDec, k)) Attach(cDec, &k, 4);
            if (cRec != null && !HasInt(cRec, k)) Attach(cRec, &k, 4);
            r.gameJoint = k; r.rearmDecoupler = true;
            // 0.7.9: the game's list of ACTIVE connections (SpaceshipSingleton._connectedJoints, UnsafeList<int>): only what is in it
            // is checked for breaking. Without this the decoupler was told to release and nothing happened.
            string active = ActivateJoint(em, k);
            // 5) release the fixed joint, remove the recoupler's empty part and ask the game to recalculate the decoupler's part
            try { if (Ecs.Alive(em, r.joint)) em.DestroyEntity(r.joint); } catch (Exception) { }
            r.joint = Entity.Null;
            string leg = "";
            try
            {
                if (em.HasComponent(pRec, ComponentType.ReadOnly(tiLEG)))
                {
                    var cl = Header(em, pRec, tiLEG);
                    if (cl != null) { var rd = (Entity*)Elems(cl); int m = 0; for (int i = 0; i < cl->Length; i++) if (rd[i].Index == pRec.Index) rd[m++] = rd[i]; leg = " (LinkedEntityGroup " + cl->Length + " -> " + m + ")"; cl->Length = m; }
                }
            }
            catch (Exception ex) { leg = " (LEG: " + ex.Message + ")"; }
            try { em.DestroyEntity(pRec); } catch (Exception ex) { ModLog.Warn("SC_Recoupler " + r.block.Index + ": destroying the empty part failed: " + ex.Message); }
            em.AddComponent(pDec, ComponentType.ReadWrite(tiCheck));
            r.state = 2; r.partA = Entity.Null; r.partB = Entity.Null;
            ModLog.Info("SC_Recoupler " + r.block.Index + ": MERGED — " + newOnes.Count + " block(s) moved to part " + pDec.Index + ", game joint " + k + " re-enabled (" + active + ")" + (hasPrev ? ", PreviousParent updated" : "") + leg + "; the game will recompute the part");
            Rearm(em, r);
            return null;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] unsafe struct IntList { public int* Ptr; public int Length; public int Capacity; public ushort AllocIndex; public ushort AllocVersion; }
        internal static unsafe string ActivateJoint(EntityManager em, int k)
        {
            try
            {
                var q = Ecs.QueryList(Ecs.QSing); if (q.Count == 0) return "no SpaceshipSingleton";
                var sing = (Ecs.HasC(em, q[0], Ecs.tiSing) ? (SpaceshipSingleton*)em.GetComponentDataRawRO(q[0], Ecs.tiSing) : null);
                var l = (IntList*)sing->_connectedJoints; if (l == null || l->Ptr == null) return "active list is null";
                if (l->Length < 0 || l->Length > l->Capacity || l->Capacity > 10000000) return "active list has an odd format";
                for (int i = 0; i < l->Length; i++) if (l->Ptr[i] == k) return "was already active";
                if (l->Length >= l->Capacity)
                {
                    if (l->AllocIndex != 4) return "list full and allocator " + l->AllocIndex + " (not Persistent) — NOT activated";
                    int newOne = System.Math.Max(64, l->Capacity * 2);
                    var p = (int*)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Malloc((long)newOne * 4, 16, Unity.Collections.Allocator.Persistent);
                    Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(p, l->Ptr, (long)l->Length * 4);
                    Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free(l->Ptr, Unity.Collections.Allocator.Persistent);
                    l->Ptr = p; l->Capacity = newOne;
                }
                l->Ptr[l->Length] = k; l->Length++;
                return "active again";
            }
            catch (Exception ex) { return "activation failed: " + ex.Message; }
        }

        /// <summary>Puts the reconnected joint back in the decoupler's list (so it can release again) — only when its signal
        /// is &lt; 0.5, otherwise it would fire right away.</summary>
        static unsafe void Rearm(EntityManager em, Recoupler r)
        {
            try
            {
                if (!Ecs.Alive(em, r.decoupler) || r.gameJoint < 0) { r.rearmDecoupler = false; return; }
                Ecs.Signal(em, r.decoupler, out float sd, out _);
                if (sd >= 0.5f) return;
                var c = Header(em, r.decoupler, Ecs.tiDecJ); if (c == null) { r.rearmDecoupler = false; ModLog.Warn("SC_Recoupler " + r.block.Index + ": could not re-arm the decoupler (buffer)"); return; }
                int k = r.gameJoint; if (!HasInt(c, k)) Attach(c, &k, 4);
                r.rearmDecoupler = false;
                ModLog.Info("SC_Recoupler " + r.block.Index + ": Decoupler " + r.decoupler.Index + " re-armed (can release again)");
            }
            catch (Exception ex) { r.rearmDecoupler = false; ModLog.Warn("SC_Recoupler " + r.block.Index + ": re-arming decoupler: " + ex.Message); }
        }

        // ---- 0.11.2 (Andre: "a little detail when the recoupler and the decoupler are locked"): two green LEDs (extra
        // renderers "LedA"/"LedB" of the prefab) sit 2 cm INSIDE the panel of the +Z/-Z faces (off/hidden); coupled (locked or
        // merged) they come 3 mm out of the panel. Same trick as the piston rod: moves the LocalTransform of the child entity.
        const float LedX = 0.12f, LedY = 0.19f, LedRestZ = 0.224f, LedOffset = 0.023f;
        static unsafe void RefreshLed(EntityManager em, Recoupler r)
        {
            // 0.12.1 (Andre): also lit at the start of the simulation, while the recoupler is glued to a decoupler (same part, less than
            // 0.6 m away) that hasn't fired yet — shows it is attached
            r.tTouching += UnityEngine.Time.deltaTime;
            if (r.tTouching >= 0.5f)
            {
                r.tTouching = 0f; r.touching = false;
                try
                {
                    var pa = Ecs.Parent(em, r.block);
                    if (r.state == 0 && Ecs.IsPart(em, pa))
                    {
                        var pr = Ecs.Position(em, r.block);
                        foreach (var d in Decouplers(em))
                        {
                            if (Ecs.Parent(em, d).Index != pa.Index || Ecs.Dist(Ecs.Position(em, d), pr) > 0.6f) continue;
                            int stuckOnes = 0; try { stuckOnes = em.GetBufferLength(d, Ecs.tiDecJ); } catch (Exception) { }
                            if (stuckOnes > 0) { r.touching = true; break; }
                        }
                    }
                }
                catch (Exception) { }
            }
            bool lit = (r.state == 1 && !r.onAnchor && !Ecs.IsNull(r.joint)) || r.state == 2 || r.touching;
            r.tLed += UnityEngine.Time.deltaTime;
            if (r.leds == null || r.tLed >= 5f)
            {
                r.tLed = 0f;
                var foundAll = new List<Entity>(); var rep = new List<float3>();
                foreach (var f in Ecs.Children(em, r.block))
                {
                    var p = Ecs.LT(em, f).Position;
                    if (System.Math.Abs(System.Math.Abs(p.x) - LedX) < 0.003f && System.Math.Abs(p.y - LedY) < 0.003f && (System.Math.Abs(System.Math.Abs(p.z) - LedRestZ) < 0.003f || System.Math.Abs(System.Math.Abs(p.z) - (LedRestZ + LedOffset)) < 0.003f))
                    {
                        foundAll.Add(f); rep.Add(new float3 { x = p.x, y = p.y, z = p.z < 0f ? -LedRestZ : LedRestZ });
                    }
                }
                bool first = r.leds == null;
                r.leds = foundAll.ToArray(); r.ledRest = rep.ToArray();
                if (first) { if (r.leds.Length == 0) ModLog.Warn("SC_Recoupler " + r.block.Index + ": LEDs not found among the block's children (old prefab? rebuild the bundle in the Modkit)"); r.ledLit = !lit; }
            }
            if (lit == r.ledLit || r.leds.Length == 0) return;
            r.ledLit = lit;
            for (int i = 0; i < r.leds.Length; i++)
            {
                if (!Ecs.Alive(em, r.leds[i])) continue;
                var lt = (Ecs.HasC(em, r.leds[i], Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(r.leds[i], Ecs.tiLT) : null); if (lt == null) continue;
                var p = r.ledRest[i];
                lt->Position = new float3 { x = p.x, y = p.y, z = lit ? p.z + (p.z < 0f ? -LedOffset : LedOffset) : p.z };
            }
            ModLog.Info("SC_Recoupler " + r.block.Index + ": LED " + (lit ? (r.touching ? "on (held by a decoupler that hasn't fired yet)" : "on (coupled)") : "off"));
        }

        static void Drop(EntityManager em, Recoupler r, string reason)
        {
            if (Ecs.IsNull(r.joint)) { r.state = 0; return; }
            try { if (Ecs.Alive(em, r.joint)) em.DestroyEntity(r.joint); } catch (Exception) { }
            ModLog.Info("SC_Recoupler " + r.block.Index + ": released (" + reason + ")");
            r.joint = Entity.Null; r.partA = Entity.Null; r.partB = Entity.Null; r.state = 0;
        }

        static List<Entity> Decouplers(EntityManager em)
        {
            if (!qDecOk) { var a = new Il2CppStructArray<ComponentType>(2); a[0] = ComponentType.ReadOnly(Ecs.tiDecType); a[1] = ComponentType.ReadOnly(Ecs.tiLTW); qDec = em.CreateEntityQuery(a); qDecOk = true; }
            return Ecs.QueryList(qDec);
        }

        // ---------------------------------------------------------------- beacon
        static unsafe void BeaconStep(EntityManager em, Beacon b)
        {
            if (tiIcon.Value == 0) return;
            if (!Ecs.IsPart(em, Ecs.Parent(em, b.block))) { DeleteGhost(em, b); return; }   // in flight only
            // 0.7.6: a vehicle's own beacon doesn't show on its radar. The radar the player looks at is the one of the vehicle they
            // are in, so: player less than HideNear m from the beacon -> it disappears from the radar.
            // 0.7.8: hides the beacon that is on the SAME vehicle as the radar nearest to the player (the radar they are looking at).
            // Vehicle = parts linked by bearing/piston/recoupler. No radar in flight: back to the distance rule.
            RefreshVehicle(em);
            bool hideIt; string why;
            if (!Ecs.IsNull(radarNear))
            {
                hideIt = vehicle.Contains(Ecs.Parent(em, b.block).Index);
                why = hideIt ? "same vehicle as radar " + radarNear.Index : "other vehicle, radar " + radarNear.Index;
            }
            else
            {
                float dj = PlayerDist(em, Ecs.Position(em, b.block));
                hideIt = HideNear > 0f && dj < HideNear; why = "no radar in flight; player at " + (dj > 1e8f ? "?" : dj.ToString("F0")) + " m";
            }
            // 0.17.3 (Andre: with the Replay free camera the player stays in the car and the car's beacon vanished from ALL radars — the radar
            // has no per-radar filter: either the ghost exists for everyone or for no one). hide_on_own_vehicle = false: never hides.
            if (!HideOwn && hideIt) { hideIt = false; why += " — hide_on_own_vehicle is off"; }
            if (hideIt != b.hidden) { b.hidden = hideIt; ModLog.Info("SC_Beacon " + b.block.Index + (hideIt ? ": hidden from radar (" : ": visible on radar (") + why + ")"); }
            // (0.11.2: the label was removed from every block — Andre)
            // 0.11.1 (Andre: "make it consume power too, 5 P"): asks for 5 P/s at the power port; without power it stays off the radar
            Ecs.SetConsumption(em, b.block, BeaconPowerUse);
            byte stateE = 255; bool power = BeaconPowerUse <= 0f || Ecs.HasPower(em, b.block, out stateE);
            if (stateE != b.energyState) { b.energyState = stateE; ModLog.Info("SC_Beacon " + b.block.Index + ": power " + JointSystem.StateName(stateE)); }
            if (!power != b.noPower) { b.noPower = !power; ModLog.Info("SC_Beacon " + b.block.Index + (power ? ": powered, back on radar" : ": NO power — off radar")); }
            if (hideIt || !power) { DeleteGhost(em, b); return; }
            if (!Ecs.Alive(em, b.ghost))
            {
                var kinds = new Il2CppStructArray<ComponentType>(2);
                kinds[0] = ComponentType.ReadWrite(Ecs.tiLTW); kinds[1] = ComponentType.ReadWrite(tiIcon);
                b.ghost = em.CreateEntity(kinds);
                ghostsCreated.Add(Ecs.K(b.ghost));   // 0.19.5: every ghost is registered so it gets destroyed if it becomes an orphan
                ByTags(em, b.ghost);
                *(Ecs.HasC(em, b.ghost, tiIcon) ? (uint*)em.GetComponentDataRawRW(b.ghost, tiIcon) : null) = RadarIcon.SlotOf(b.icon);
                ModLog.Info("SC_Beacon " + b.block.Index + ": on radar (icon " + RadarIcon.SlotOf(b.icon) + " = " + BeaconIcon.Names[b.icon] + ", entity " + b.ghost.Index + ")");
            }
            *(Ecs.HasC(em, b.ghost, Ecs.tiLTW) ? (LocalToWorld*)em.GetComponentDataRawRW(b.ghost, Ecs.tiLTW) : null) = *(Ecs.HasC(em, b.block, Ecs.tiLTW) ? (LocalToWorld*)em.GetComponentDataRawRO(b.block, Ecs.tiLTW) : null);
            GhostWorld(em, b);
            var ic = (Ecs.HasC(em, b.ghost, tiIcon) ? (uint*)em.GetComponentDataRawRW(b.ghost, tiIcon) : null);
            uint wants = RadarIcon.SlotOf(b.icon);
            if (*ic != wants) { ModLog.Info("SC_Beacon " + b.block.Index + ": radar icon " + *ic + " -> " + wants + " (" + BeaconIcon.Names[b.icon] + ")"); *ic = wants; }
        }
        static readonly List<(float3 pos, uint icon)> radarIcons = new List<(float3, uint)>();
        /// <summary>0.19.2: called in the TransformSystemGroup postfix — writes the beacons' icons into the radar buffer.</summary>
        public static void WriteRadar(EntityManager em) { RadarWrite.Write(em, radarIcons); }
        static Entity radarNear; static readonly HashSet<int> vehicle = new HashSet<int>(); static float tVehicle = 9f; static int vehicleFrame = -1;
        static ulong hRadar, hRadarM;
        static void RefreshVehicle(EntityManager em)
        {
            if (vehicleFrame == UnityEngine.Time.frameCount) return; vehicleFrame = UnityEngine.Time.frameCount;
            tVehicle += UnityEngine.Time.deltaTime; if (tVehicle < 0.5f) return; tVehicle = 0f;
            try
            {
                if (hRadar == 0) { hRadar = Ecs.HashPrefab("SC_Radar"); hRadarM = Ecs.HashPrefab("SC_Radar M"); }
                var pj = PlayerPos(em, out bool hasPlayer);
                Entity bestPick = Entity.Null; float dm = 1e9f;
                foreach (var e in Ecs.Components(em))
                {
                    ulong h = Ecs.Prefab(em, e); if (h != hRadar && h != hRadarM) continue;
                    if (!Ecs.IsPart(em, Ecs.Parent(em, e))) continue;
                    float d = hasPlayer ? Ecs.Dist(Ecs.Position(em, e), pj) : 0f; if (d < dm) { dm = d; bestPick = e; }
                }
                radarNear = bestPick; vehicle.Clear();
                if (Ecs.IsNull(bestPick)) return;
                // group of parts linked to the radar by the mod's joints
                var nbr = new Dictionary<int, List<int>>();
                void Link(Entity a, Entity b2) { if (Ecs.IsNull(a) || Ecs.IsNull(b2)) return; if (!nbr.TryGetValue(a.Index, out var la)) nbr[a.Index] = la = new List<int>(); la.Add(b2.Index); if (!nbr.TryGetValue(b2.Index, out var lb)) nbr[b2.Index] = lb = new List<int>(); lb.Add(a.Index); }
                foreach (var (a, b2) in JointSystem.Links()) Link(a, b2);
                foreach (var r in recs.Values) if (r.state == 1) { Link(r.partA, r.partB); Link(r.partA, r.anchor); }
                var queue = new Queue<int>(); int begin = Ecs.Parent(em, bestPick).Index; vehicle.Add(begin); queue.Enqueue(begin);
                while (queue.Count > 0) { int x = queue.Dequeue(); if (nbr.TryGetValue(x, out var l)) foreach (var y in l) if (vehicle.Add(y)) queue.Enqueue(y); }
            }
            catch (Exception ex) { radarNear = Entity.Null; if (!vehWarn) { vehWarn = true; ModLog.Warn("beacon: finding the closest radar failed: " + ex.Message); } }
        }
        static bool vehWarn;

        static float3 PlayerPos(EntityManager em, out bool has)
        {
            has = false;
            try
            {
                if (!playerOk) { playerOk = true; tiPlayer = TypeManager.GetTypeIndex(Il2CppType.Of<PlayerControllerData>()); var a = new Il2CppStructArray<ComponentType>(2); a[0] = ComponentType.ReadOnly(tiPlayer); a[1] = ComponentType.ReadOnly(Ecs.tiLTW); qPlayer = em.CreateEntityQuery(a); }
                foreach (var e in Ecs.QueryList(qPlayer)) { has = true; return Ecs.Position(em, e); }
            }
            catch (Exception) { }
            return float3.zero;
        }

        // 0.7.11: the game's icons have WormholeWorldIndex (which "world" they are in), ActiveWormholeWorld (tag of the active world),
        // UniversePosition (double3) and LocalTransform. The radar probably only picks the ones in the active world -> the ghost copies these
        // 4 from the beacon's piece (block or part), every frame.
        static TypeIndex tiWWI, tiAWW, tiUP; static bool worldOk, worldLogged;
        static unsafe void GhostWorld(EntityManager em, Beacon b)
        {
            try
            {
                if (!worldOk) { worldOk = true; tiWWI = TypeManager.GetTypeIndex(Il2CppType.Of<WormholeWorldIndex>()); tiAWW = TypeManager.GetTypeIndex(Il2CppType.Of<ActiveWormholeWorld>()); tiUP = TypeManager.GetTypeIndex(Il2CppType.Of<UniversePosition>()); }
                var g = b.ghost; var part = Ecs.Parent(em, b.block);
                Entity Source(TypeIndex ti) => em.HasComponent(b.block, ComponentType.ReadOnly(ti)) ? b.block : (!Ecs.IsNull(part) && em.HasComponent(part, ComponentType.ReadOnly(ti))) ? part : Entity.Null;
                var fW = Source(tiWWI); var fA = Source(tiAWW); var fU = Source(tiUP);
                if (!em.HasComponent(g, ComponentType.ReadOnly(tiWWI))) em.AddComponent(g, ComponentType.ReadWrite(tiWWI));
                if (!em.HasComponent(g, ComponentType.ReadOnly(tiUP))) em.AddComponent(g, ComponentType.ReadWrite(tiUP));
                if (!em.HasComponent(g, ComponentType.ReadOnly(Ecs.tiLT))) em.AddComponent(g, ComponentType.ReadWrite(Ecs.tiLT));
                bool wantsActive = Ecs.IsNull(fA) ? true : true;   // the beacon is in the world the player is flying in
                if (wantsActive && !em.HasComponent(g, ComponentType.ReadOnly(tiAWW))) em.AddComponent(g, ComponentType.ReadWrite(tiAWW));
                if (!Ecs.IsNull(fW)) *(Ecs.HasC(em, g, tiWWI) ? (int*)em.GetComponentDataRawRW(g, tiWWI) : null) = *(Ecs.HasC(em, fW, tiWWI) ? (int*)em.GetComponentDataRawRO(fW, tiWWI) : null);
                Ecs.Pose(em, b.block, out var pos, out var rot);
                if (!Ecs.IsNull(fU))
                {
                    // the ghost's universe position = the source's + (block position - source position)
                    var up = *(Ecs.HasC(em, fU, tiUP) ? (double3*)em.GetComponentDataRawRO(fU, tiUP) : null);
                    var pf = fU.Index == b.block.Index ? pos : Ecs.LT(em, fU).Position;
                    var d = Ecs.Sub(pos, pf);
                    *(Ecs.HasC(em, g, tiUP) ? (double3*)em.GetComponentDataRawRW(g, tiUP) : null) = new double3 { x = up.x + d.x, y = up.y + d.y, z = up.z + d.z };
                }
                var lt = new LocalTransform { Position = pos, Rotation = rot, Scale = 1f };
                *(Ecs.HasC(em, g, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(g, Ecs.tiLT) : null) = lt;
                if (!worldLogged) { worldLogged = true; ModLog.Info("SC_Beacon " + b.block.Index + ": ghost with the game's world — WormholeWorldIndex from " + Which(b, part, fW) + ", ActiveWormholeWorld " + (Ecs.IsNull(fA) ? "(neither block nor part has it; set anyway)" : "from " + Which(b, part, fA)) + ", UniversePosition from " + Which(b, part, fU)); }
            }
            catch (Exception ex) { if (!worldLogged) { worldLogged = true; ModLog.Warn("SC_Beacon: copying the world to the ghost failed: " + ex.Message); } }
        }
        static string Which(Beacon b, Entity part, Entity f) => Ecs.IsNull(f) ? "none" : f.Index == b.block.Index ? "block" : "part";

        static float PlayerDist(EntityManager em, float3 p)
        {
            try
            {
                if (!playerOk) { playerOk = true; tiPlayer = TypeManager.GetTypeIndex(Il2CppType.Of<PlayerControllerData>()); var a = new Il2CppStructArray<ComponentType>(2); a[0] = ComponentType.ReadOnly(tiPlayer); a[1] = ComponentType.ReadOnly(Ecs.tiLTW); qPlayer = em.CreateEntityQuery(a); }
                float bestPick = 1e9f;
                foreach (var e in Ecs.QueryList(qPlayer)) { float d = Ecs.Dist(Ecs.Position(em, e), p); if (d < bestPick) bestPick = d; }
                return bestPick;
            }
            catch (Exception) { return 1e9f; }
        }

        static void DeleteGhost(EntityManager em, Beacon b)
        {
            try { if (Ecs.Alive(em, b.ghost)) em.DestroyEntity(b.ghost); } catch (Exception) { }
            ghostsCreated.Remove(Ecs.K(b.ghost));
            b.ghost = Entity.Null;
        }
        // 0.19.5 (Andre's log: "entities with RadarAppearanceIcon: 19 from the game" turning into 42 during the session; "NO free icon";
        // duplicated icons): when going back to the garage, Forget cleared the beacon list WITHOUT destroying the ghosts, which the game then
        // treated as its own icons (they took every slot and showed up on the radar). Now every ghost created is registered and the ones
        // that belong to no beacon are destroyed (every 2 s and right after unloading).
        static readonly HashSet<long> ghostsCreated = new HashSet<long>(); static float tOrphans = 9f;
        static void DestroyOrphans(EntityManager em, float dt)
        {
            tOrphans += dt; if (tOrphans < 2f || ghostsCreated.Count == 0) return; tOrphans = 0f;
            var aliveSet = new HashSet<long>(); foreach (var b in beacons.Values) if (!Ecs.IsNull(b.ghost)) aliveSet.Add(Ecs.K(b.ghost));
            var orphans = new List<long>(); foreach (var k in ghostsCreated) if (!aliveSet.Contains(k)) orphans.Add(k);
            int n = 0;
            foreach (var k in orphans)
            {
                ghostsCreated.Remove(k);
                var e = Ecs.OfKey(k);
                try { if (Ecs.Alive(em, e) && Ecs.HasC(em, e, tiIcon)) { em.DestroyEntity(e); n++; } } catch (Exception) { }
            }
            if (n > 0) { ModLog.Info("radar: " + n + " orphan beacon icon entity(ies) destroyed"); RadarIcon.RequestRecheck = true; }
        }

        /// <summary>0.10.0: time rewind — the game rebuilt the parts of the saved moment (if the car was merged afterwards, it comes back separate).
        /// Releases every recoupler and disarms: to couple again, the signal has to drop and rise again.</summary>
        public static void Rewound(EntityManager em)
        {
            foreach (var r in recs.Values)
            {
                if (r.state != 0) Drop(em, r, "time rewind");
                r.state = 0; r.armed = false; r.tPull = 0f; r.rearmDecoupler = false; r.onAnchor = false; r.gameJoint = -1;
            }
        }

        public static void Forget() { recs.Clear(); beacons.Clear(); radarIcons.Clear(); RadarWrite.Forget(); }
        public static void Clean(EntityManager em)
        {
            foreach (var r in recs.Values) Drop(em, r, "cleaning up");
            foreach (var b in beacons.Values) DeleteGhost(em, b);
            recs.Clear(); beacons.Clear();
        }
    }
}
