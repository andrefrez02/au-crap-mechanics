using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;

namespace CrapMechanics
{
    /// <summary>0.20.0 (Andre: "put the piston in the garage already at a specific position ... already extended" / "when pressing H ... a line at
    /// its tip showing the rod's stroke ... if the block is misaligned the line of the nearest piston turns red").
    /// GARAGE step (every 0.1 s), per block with GarageTransform outside a part:
    /// - piston with a Piston End (SC_PistonEnd) aligned on its axis (up to the max stroke): rod and head are already stretched up to the
    ///   end — the stroke in the simulation will be that distance (JointSystem.WaitingTop finds the end the same way);
    /// - with the game's H on (GaragebotSystem._visible = the garage robot with the center of mass/weight), the guide bar (the game's glass renderer
    ///   on the top face) stretches up to max stroke x knob, green; it turns RED on the piston nearest to a Piston End that isn't
    ///   aligned with any piston. The game's H isn't touched: only read. If it can't be read, the H key (Input System) toggles.</summary>
    internal static class Garage
    {
        class Info { public Entity rod, head, rail; public float ext = -1f, railL = -1f; public int color = -1; public bool log; public string name; public float scale, strokeMax; public Entity endBlock; public float tLaunched = -1f; }
        static readonly Dictionary<long, Info> pistons = new Dictionary<long, Info>();
        static readonly HashSet<long> seenSet = new HashSet<long>();
        static TypeIndex tiGT, tiMem; static EntityQuery qGar; static bool ok, failed; static float t = 9f; static ulong hashEnd; static bool hashOk;
        static int warnings;
        public static bool DetailedLog = true;

        static bool Prepare(EntityManager em)
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiGT = TypeManager.GetTypeIndex(Il2CppType.Of<GarageTransform>());
                tiMem = TypeManager.GetTypeIndex(Il2CppType.Of<CRPRendererMemory_Color>());
                var a = new Il2CppStructArray<ComponentType>(2); a[0] = ComponentType.ReadOnly(Ecs.tiPrefab); a[1] = ComponentType.ReadOnly(tiGT);
                qGar = em.CreateEntityQuery(a);
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("garage: " + ex.Message); return false; }
        }

        /// <summary>Is Garage.cs taking care of this block's visual (rod/head) in the garage? (JointSystem.RetractInGarage doesn't touch it then.)</summary>
        public static bool HandleVisual(Entity block) => seenSet.Contains(Ecs.K(block));
        public static bool Active => ok;
        /// <summary>0.20.1: pistons the garage saw with an aligned Piston End (to find out right away when they are launched).</summary>
        public static List<Entity> PistonsWithEnd()
        {
            var r = new List<Entity>();
            foreach (var kv in pistons) if (!Ecs.IsNull(kv.Value.endBlock)) r.Add(Ecs.OfKey(kv.Key));
            return r;
        }
        /// <summary>0.20.5 (Andre's log: on a quick load the mod pulled the end piece 58 cm back to the garage pose): has the piston just LEFT
        /// the garage with a Piston End (the garage saw it less than 5 s ago)? Only in that case is the joint born pulling to the garage pose; after a
        /// load / rebuild the current pose is the one that counts.</summary>
        public static bool JustLaunched(Entity block)
        {
            // 0.24.0: the Piston End is no longer required. Without it tLaunched was never recorded and JustLaunched was always false,
            // so the snap back to the garage pose ([Piston] snap_back) never got to run on Andre's pistons, which don't
            // use any Piston End ("17 piston(s), 0 Piston End(s)" in the log).
            if (!pistons.TryGetValue(Ecs.K(block), out var i)) return false;
            return i.tLaunched < 0f || UnityEngine.Time.time - i.tLaunched < 5f;
        }
        /// <summary>Gap up to the Piston End aligned with this piston in the garage (0 if there's none / it's not from the garage).</summary>
        public static float ExtOf(Entity block) => pistons.TryGetValue(Ecs.K(block), out var i) && seenSet.Contains(Ecs.K(block)) && i.ext > 0f ? i.ext : 0f;

        public static void Step(EntityManager em, float dt)
        {
            if (!JointSystem.HashesOk || !Prepare(em)) return;
            KeyH();   // every frame (wasPressedThisFrame)
            t += dt; if (t < (pistons.Count > 0 ? 0.1f : 0.5f)) return; t = 0f;   // no piston in the garage (flying): rarer scan
            try { Scan(em); }
            catch (Exception ex) { if (warnings++ < 3) ModLog.Warn("garage pass: " + ex.Message); }
        }

        /// <summary>0.23.1: a block's garage pose (SlipRings.cs uses it to pair ring x joint in the garage).</summary>
        public static unsafe bool GT(EntityManager em, Entity e, out RigidTransform gt)
        {
            gt = default;
            if (!Ecs.HasC(em, e, tiGT)) return false;
            var p = (RigidTransform*)em.GetComponentDataRawRO(e, tiGT); if (p == null) return false;
            gt = *p; return true;
        }

        /// <summary>Is Piston End `gE` aligned with piston `gP` (middle = half the piston's side)? gap = gap between the piston's top face and the
        /// end's bottom face (= stroke); ax/lat = along the axis / sideways (for diagnostics).</summary>
        /// <summary>0.22.4: half thickness of the Piston End along the piston's axis: old cube (SC_PistonEndCube) 0.125; new plates 0.0625.</summary>
        public static float HalfEnd(string name) => 0.0625f;   // 0.22.5: only the plates (the cube was removed)
        /// <summary>0.22.4: garage blocks (SCPrefab + GarageTransform) — so SlipRings.cs paints the plugs in the garage too.</summary>
        public static List<Entity> Blocks(EntityManager em) => Prepare(em) ? Ecs.QueryList(qGar) : new List<Entity>();
        public static bool Aligned(RigidTransform gP, float middle, float strokeMax, RigidTransform gE, out float gap, out float ax, out float lat, float halfEnd = 0.125f)
        {
            var up = Ecs.Rot(gP.rot, Ecs.UY); var d = Ecs.Sub(gE.pos, gP.pos);
            ax = Ecs.Dot(d, up); lat = Ecs.Len(Ecs.Sub(d, Ecs.Mul(up, ax)));
            gap = ax - middle - halfEnd;
            if (lat > 0.02f) return false;
            if (Math.Abs(Ecs.Dot(Ecs.Rot(gE.rot, Ecs.UY), up)) < 0.99f) return false;   // end turned sideways: the socket doesn't face the piston
            return gap >= -0.002f && gap <= strokeMax + 0.002f;
        }

        /// <summary>In the simulation (JointSystem.WaitingTop): the Piston End aligned with this piston, from the GARAGE pose of both. Entity.Null if there is none.</summary>
        public static bool FindEnd(EntityManager em, Entity piston, float middle, float strokeMax, out Entity endBlock, out float gap, out string reason)
        {
            endBlock = Entity.Null; gap = 0f; reason = null;
            if (!Prepare(em)) { reason = "garage helper not ready"; return false; }
            if (!GT(em, piston, out var gP)) { reason = "the piston has no GarageTransform"; return false; }
            float bestPick = 999f; int near = 0; string endName = null;
            foreach (var e in Ecs.Components(em))
            {
                ulong h; try { h = Ecs.Prefab(em, e); } catch (Exception) { continue; }
                if (!JointSystem.KindFromHash(h, out var tp, out _, out var np) || tp != Kind.End || !GT(em, e, out var gE)) continue;   // 0.22.4: old cube + plates
                if (Aligned(gP, middle, strokeMax, gE, out var g, out var ax, out var lat, HalfEnd(np))) { if (g < bestPick) { bestPick = g; endBlock = e; gap = g; endName = np; } }
                else if (Ecs.Len(Ecs.Sub(gE.pos, gP.pos)) < strokeMax + 1f) near++;
            }
            if (Ecs.IsNull(endBlock)) { reason = near > 0 ? near + " Piston End(s) nearby but not on this piston's axis" : "no Piston End on this piston's axis"; return false; }
            // 0.26.0 (Andre: "in the piston + piston circuit, the second piston ignores the stroke and becomes the maximum possible size"): two
            // stacked pistons sit on the SAME axis, so both see the same Piston End — and the one further away was born
            // extended up to it (the stroke becomes the distance to the end). The end belongs to the nearest one; the others get no end.
            if (GT(em, endBlock, out var gEnd))
            {
                foreach (var e in Ecs.Components(em))
                {
                    if (e.Index == piston.Index && e.Version == piston.Version) continue;
                    ulong h2; try { h2 = Ecs.Prefab(em, e); } catch (Exception) { continue; }
                    if (!JointSystem.KindFromHash(h2, out var tp2, out var stroke2, out var np2) || tp2 != Kind.Piston || !GT(em, e, out var gO)) continue;
                    if (!Aligned(gO, 0.25f * JointSystem.ScaleFromName(np2), stroke2, gEnd, out var g2, out _, out _, HalfEnd(endName)) || g2 >= bestPick - 0.002f) continue;
                    reason = "the Piston End on this axis is " + (bestPick * 100f).ToString("F0") + " cm away but only " + (g2 * 100f).ToString("F0")
                           + " cm from another piston — it belongs to that one";
                    endBlock = Entity.Null; gap = 0f; return false;
                }
            }
            return true;
        }

        static void Scan(EntityManager em)
        {
            bool h = HOn(em);
            var pst = new List<(Entity e, RigidTransform gt, float scale, float strokeMax, string name)>();
            var ends = new List<(Entity e, RigidTransform gt, float half)>();
            foreach (var e in Ecs.QueryList(qGar))
            {
                // 0.26.11: the type first (cached prefab, Ecs.Prefab) — before, there were 8 calls into the game per block (Parent + IsPart) on
                // ALL the ~10 thousand blocks with GarageTransform, every 0.1 s, even in flight, just to discard whatever isn't a piston
                ulong hh; try { hh = Ecs.Prefab(em, e); } catch (Exception) { continue; }
                if (!JointSystem.KindFromHash(hh, out var kind, out var stroke, out var name) || (kind != Kind.End && kind != Kind.Piston)) continue;
                if (Ecs.IsPart(em, Ecs.Parent(em, e))) continue;   // launched: the Joint takes care of it
                if (kind == Kind.End) { if (GT(em, e, out var g)) ends.Add((e, g, HalfEnd(name))); }
                else if (kind == Kind.Piston) { if (GT(em, e, out var g)) pst.Add((e, g, JointSystem.ScaleFromName(name), stroke, name)); }
            }
            seenSet.Clear();
            // piston -> aligned end (the nearest)
            var endOf = new Dictionary<long, (Entity endBlock, float gap)>(); var used = new HashSet<long>();
            foreach (var p in pst)
            {
                float bestPick = 999f; Entity pe = Entity.Null;
                foreach (var q in ends) if (Aligned(p.gt, 0.25f * p.scale, p.strokeMax, q.gt, out var g, out _, out _, q.half) && g < bestPick) { bestPick = g; pe = q.e; }
                if (!Ecs.IsNull(pe)) { endOf[Ecs.K(p.e)] = (pe, bestPick); used.Add(Ecs.K(pe)); }
            }
            // loose end (no piston) -> the nearest piston turns red
            var reds = new HashSet<long>();
            foreach (var q in ends)
            {
                if (used.Contains(Ecs.K(q.e))) continue;
                float bestPick = 999f; long kp = 0;
                foreach (var p in pst) { float d = Ecs.Len(Ecs.Sub(q.gt.pos, p.gt.pos)); if (d < bestPick && d < p.strokeMax + 1f) { bestPick = d; kp = Ecs.K(p.e); } }
                if (kp != 0) reds.Add(kp);
            }
            foreach (var p in pst)
            {
                long k = Ecs.K(p.e); seenSet.Add(k);
                if (!pistons.TryGetValue(k, out var inf)) { inf = new Info { scale = p.scale, strokeMax = p.strokeMax, name = p.name }; pistons[k] = inf; }
                if (!Ecs.Alive(em, inf.rod) || !Ecs.Alive(em, inf.head) || (!Ecs.Alive(em, inf.rail) && inf.railL != -2f))
                {
                    JointSystem.FindPistonParts(em, p.e, p.scale, p.strokeMax, out inf.rod, out inf.head, out inf.rail);
                    inf.ext = -1f; inf.railL = Ecs.IsNull(inf.rail) ? -2f : -1f; inf.color = -1;   // -2 = this prefab has no guide (2/4/8): don't look again
                    if (!inf.log) { inf.log = true; if (DetailedLog) ModLog.Info(p.name + " " + p.e.Index + ": garage — rod " + (Ecs.IsNull(inf.rod) ? "NOT found" : "ok") + ", head " + (Ecs.IsNull(inf.head) ? "NOT found" : "ok") + ", guide bar " + (Ecs.IsNull(inf.rail) ? "none" : "ok")); }
                }
                // rod/head: stretched up to the aligned end (0 without an end)
                float ext = 0f; Entity endBlock = Entity.Null;
                if (endOf.TryGetValue(k, out var pg)) { ext = pg.gap; endBlock = pg.endBlock; }
                // 0.20.2: checks by the REAL POSITION of the head (not by a stored value): if someone retracted/moved it, puts it back
                float yHead = float.NaN; try { if (Ecs.Alive(em, inf.head)) yHead = Ecs.LT(em, inf.head).Position.y; } catch (Exception) { }
                bool touched = !float.IsNaN(yHead) && Math.Abs(yHead - (JointSystem.HeadYOf(p.scale) + ext)) > 0.002f;
                if (Math.Abs(ext - inf.ext) > 0.0005f || touched)
                {
                    bool changed = Math.Abs(ext - inf.ext) > 0.0005f;
                    JointSystem.SetPistonVisual(em, inf.rod, inf.head, ext, p.scale); inf.ext = ext;
                    if (DetailedLog && changed) ModLog.Info(p.name + " " + p.e.Index + ": garage — " + (Ecs.IsNull(endBlock) ? "no Piston End on its axis: rod retracted" : "Piston End " + endBlock.Index + " at " + ext.ToString("F3") + " m: rod drawn extended (stroke will be " + ext.ToString("F3") + " m)"));
                }
                inf.endBlock = endBlock;
                // guide bar (only with H on): max stroke x knob; red if a loose end is nearer to this piston
                if (Ecs.IsNull(inf.rail) || !Ecs.Alive(em, inf.rail)) continue;
                float L = 0.0005f; int color = -1;
                if (h)
                {
                    float frac = 1f;
                    try { var kn = Knobs.Find(em, p.e); float v = Knobs.ReadValue(em, kn); if (!float.IsNaN(v)) frac = Knobs.StrokeFraction(v); } catch (Exception) { }
                    L = Math.Max(0.01f, p.strokeMax * frac); color = reds.Contains(k) ? 1 : 0;
                }
                if (Math.Abs(L - inf.railL) > 0.0005f) { Ecs.ScaleY(em, inf.rail, L); inf.railL = L; }
                if (color >= 0 && color != inf.color) { Paint(em, inf.rail, color); inf.color = color; }
            }
            // pistons that left the garage (launched / deleted): hide the guide; forget — except the ones launched with a Piston End, which stay
            // in the list (0.20.1: JointSystem finds out right away through it) until the Joint exists or the entity dies
            var outside = new List<long>();
            foreach (var kv in pistons) if (!seenSet.Contains(kv.Key)) outside.Add(kv.Key);
            foreach (var k in outside)
            {
                var inf = pistons[k]; var e = Ecs.OfKey(k);
                try { if (Ecs.Alive(em, inf.rail) && inf.railL > 0.001f) { Ecs.ScaleY(em, inf.rail, 0.0005f); inf.railL = 0.0005f; } } catch (Exception) { }
                if (Ecs.Alive(em, e) && !JointSystem.ActiveJoint(e)) { if (inf.tLaunched < 0f) inf.tLaunched = UnityEngine.Time.time; continue; }   // 0.20.5: stays until the motor is born (JustLaunched); 0.24.0: every piston, with or without a Piston End
                pistons.Remove(k);
            }
            if (h != hBefore) { hBefore = h; if (DetailedLog) ModLog.Info("garage: H display " + (h ? "on" : "off") + (hSource == 2 ? " (H key)" : " (game's garage bot)") + " — " + pst.Count + " piston(s), " + ends.Count + " Piston End(s)"); }
        }
        static bool hBefore;

        static readonly float4 Green = new float4(0.25f, 0.95f, 0.35f, 1f), Red = new float4(0.98f, 0.12f, 0.08f, 1f);
        static unsafe void Paint(EntityManager em, Entity rail, int color)
        {
            if (!Ecs.HasC(em, rail, tiMem)) { if (warnings++ < 3) ModLog.Warn("garage: the guide bar has no CRPRendererMemory_Color (rebuild the bundle in the Modkit)"); return; }
            var p = (CRPRendererMemory_Color*)em.GetComponentDataRawRW(rail, tiMem); if (p == null) return;
            (*p).Unpack(out _, out uint spec);
            *p = new CRPRendererMemory_Color(color == 1 ? Red : Green, spec);
        }

        // ---------------------------------------------------------------- the game's H
        // GaragebotSystem (the game's ISystem): the system's struct lives in SystemState.m_SystemPtr; `_visible` is the garage robot (H).
        // Field offsets from il2cpp (il2cpp_field_get_offset counts the object header, 16 bytes, in structs): checked by reading
        // `_entity` (it must be a live entity). If none of this works, the H key (Input System) toggles a state of its own.
        static IntPtr sys; static float tSys = 9f; static int hSource;   // 0 = not known yet, 1 = the game's system, 2 = key
        static int offVis = -1, offEnt = -1, offSysPtr = -1; static bool hManual; static int hWarn;
        static unsafe bool HOn(EntityManager em)
        {
            if (hSource == 2) return hManual;
            tSys += 0.1f;
            try
            {
                if (sys == IntPtr.Zero || tSys >= 5f)
                {
                    tSys = 0f;
                    var w = World.DefaultGameObjectInjectionWorld; if (w == null || !w.IsCreated) return hManual;
                    var wu = w.Unmanaged;
                    var hd = wu.GetExistingUnmanagedSystem(Il2CppType.Of<GaragebotSystem>());
                    var m = typeof(WorldUnmanaged).GetMethod("ResolveSystemState", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(SystemHandle) }, null);
                    if (m == null) throw new InvalidOperationException("WorldUnmanaged.ResolveSystemState not found");
                    IntPtr state = (IntPtr)System.Reflection.Pointer.Unbox(m.Invoke(wu, new object[] { hd }));
                    if (state == IntPtr.Zero) throw new InvalidOperationException("GaragebotSystem has no SystemState (not in this world?)");
                    if (offSysPtr < 0)
                    {
                        offSysPtr = Layout.Off(typeof(SystemState), "m_SystemPtr");
                        offVis = Layout.Off(typeof(GaragebotSystem), "_visible");
                        offEnt = Layout.Off(typeof(GaragebotSystem), "_entity");
                    }
                    IntPtr s = *(IntPtr*)(state + offSysPtr);
                    if (s == IntPtr.Zero) throw new InvalidOperationException("m_SystemPtr is null");
                    var ent = *(Entity*)(s + offEnt);
                    if (ent.Index <= 0 || !em.Exists(ent)) throw new InvalidOperationException("sanity check failed (_entity " + ent.Index + ":" + ent.Version + " is not a live entity; offsets " + offSysPtr + "/" + offEnt + "/" + offVis + ")");
                    if (sys != s && DetailedLog) ModLog.Info("garage: reading the game's H display from GaragebotSystem._visible (garage bot entity " + ent.Index + ")");
                    sys = s; hSource = 1;
                }
                return *(bool*)(sys + offVis);
            }
            catch (Exception ex)
            {
                sys = IntPtr.Zero;
                if (hWarn++ < 1) ModLog.Warn("garage: could not read the game's H display (" + ex.Message + ") — the H key toggles the piston guide instead");
                hSource = 2; return hManual;
            }
        }
        static bool keyFailed;
        static void KeyH()
        {
            if (hSource != 2 || keyFailed) return;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current; if (kb == null) return;
                if (kb.hKey.wasPressedThisFrame) hManual = !hManual;
            }
            catch (Exception ex) { keyFailed = true; ModLog.Warn("garage: H key: " + ex.Message); }
        }

        public static void Forget() { pistons.Clear(); seenSet.Clear(); sys = IntPtr.Zero; }
    }
}
