using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CrapMechanics
{
    internal partial class Joint
    {
        // 0.31.36: Bearing with Slip Ring (SC_BearingRing). selfRing = the block carries its own ring (set when the joint is discovered);
        // ringTh / ringThOk = the angle AngleOutput measured this frame (the same number the angle output shows), for the ring's drawing
        public bool selfRing; public float ringTh; public bool ringThOk;
    }

    /// <summary>0.31.36 (Andre: "a bearing with the slip ring built in"): the Bearing with Slip Ring (SC_BearingRing / "SC_BearingRing M") is ONE
    /// block, glued to the base. Its top quarter is a ring plate with 4 ports, and that plate has to turn with the piece on top, or the
    /// cables plugged into it would be torn. Two halves:
    ///  - RingVisual (this class, every machine, drawing only): the plate (2 renderers) and the 4 plugs are children of the block whose
    ///    LocalTransform the mod rewrites with the measured angle — the same thing the piston's rod and head have done since 0.11.0
    ///    (JointSystem.SetPistonVisual). The port ENTITIES never move (they carry the garage's colliders and the game links cables to them
    ///    by entity, not by position).
    ///  - RingCables (below, host / solo only): the cable cells hanging from the 4 ring ports are handed to the top block before the split,
    ///    so the game itself puts them on the part that turns.
    /// Mode A = both of the above. Mode B (fallback, per block and per machine, logged once with the reason) = the ring is drawn still and
    /// no cable cell is touched; data and power pass exactly the same (the link is SlipRings.cs, pass-through i with ring port i).
    /// A block leaves mode B only on a new launch.</summary>
    internal static class RingVisual
    {
        /// <summary>[Slip Ring] embedded_ring_turns. Off = mode B for every block launched from then on.</summary>
        public static bool Turns = true;
        /// <summary>Centre of the ring plate in the block (the generator's models_v2.BEARING_RING_Y): the two ring renderers sit at (0, RingY, 0).</summary>
        public const float RingY = 0.1875f;
        const float MinStep = 0.0017f;   // rad (0.1 degree): smaller changes are not redrawn

        class Vis
        {
            public Entity block; public string name;
            public Entity[] ents; public float3[] restPos; public quaternion[] restRot;   // 2 ring renderers, then the 4 plugs
            public Entity[] ringPorts;                                                    // the 4 ring ports (prefab indices begin+4 .. begin+7)
            public float angle;                                                           // what is drawn now (rad; 0 = rest)
            public bool found, flying, planB, turns = true; public string why;
            public int tries; public float tTry = 9f, tFly, tCheck; public int misses;
            public bool modeLogged, controlsLogged, turnLogged, checkLogged; public float maxAbs; public int warnings, errors;
        }
        static readonly Dictionary<long, Vis> all = new Dictionary<long, Vis>();
        static readonly Dictionary<long, Entity> plugOf = new Dictionary<long, Entity>();   // ring port -> its plug renderer
        /// <summary>0.31.37 (Andre, 23/11: "the bearing with slip ring did not go back to its place after I left the simulation", and the next
        /// launch then found no plug on any ring port and fell to mode B): the rest pose of every renderer the mod turns, kept by ENTITY for as
        /// long as the world lives. The Vis above belongs to a joint and is gone the moment the flight ends — the pose must not be. With this,
        /// the garage pass puts every turned renderer back without needing the joint, the block list or anything else, and a new Find reuses the
        /// pose it already knows instead of reading a turned renderer as if that were its rest.</summary>
        struct Rest0 { public Entity block, ent; public float3 pos; public quaternion rot; }
        static readonly Dictionary<long, Rest0> restOf = new Dictionary<long, Rest0>();
        static readonly List<long> gone = new List<long>();
        static TypeIndex tiRend; static bool rendOk, rendFailed;

        static bool Same(Entity a, Entity b) => a.Index == b.Index && a.Version == b.Version;
        static bool Prepare()
        {
            if (!rendOk && !rendFailed)
            {
                try { tiRend = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<CRPRendererData>()); rendOk = true; }
                catch (Exception ex) { rendFailed = true; ModLog.Warn("built-in ring: CRPRendererData: " + ex.Message); }
            }
            return rendOk;
        }
        static Vis Get(Joint j)
        {
            long k = Ecs.K(j.block);
            if (!all.TryGetValue(k, out var v)) { v = new Vis { block = j.block, name = j.name }; all[k] = v; }
            return v;
        }
        /// <summary>The plug renderer of a ring port (Null if this is not one, or it was not found). SlipRings.PortRenderer asks here first:
        /// a turned plug is no longer at its port's position.</summary>
        public static Entity PlugOf(Entity port) => plugOf.TryGetValue(Ecs.K(port), out var r) ? r : Entity.Null;
        /// <summary>Is this block's ring in mode A right now (found, turning allowed)? RingCables only moves anchors when it is.</summary>
        public static bool ModeA(Entity block) => all.TryGetValue(Ecs.K(block), out var v) && v.flying && v.found && !v.planB;
        /// <summary>Does the block know its mode for this launch? WaitingTop holds the split until it does, so the cable cells are handed
        /// over BEFORE the game splits the parts. Normally known on the first frame of the flight; about 1 s when the renderers are not
        /// found (5 tries, then mode B); never more than 3 s.</summary>
        public static bool Decided(Entity block) => !all.TryGetValue(Ecs.K(block), out var v) || !v.flying || v.planB || v.found || v.tFly > 3f;
        /// <summary>The 4 ring ports of the block (null if the prefab does not have them).</summary>
        public static Entity[] RingPorts(EntityManager em, Entity block, string name)
        {
            var ps = Ecs.Ports(em, block);
            if (!SlipRings.Range(em, ps, name, out int begin, out int n) || n != 8) return null;
            var r = new Entity[4]; Array.Copy(ps, begin + 4, r, 0, 4); return r;
        }

        /// <summary>Per self-ring joint, every frame, on every machine (JointSystem's frame loop, right after AngleOutput).</summary>
        public static void Step(EntityManager em, Joint j, float dt, bool ours)
        {
            var v = Get(j);
            try { StepOf(em, j, v, dt, ours); }
            catch (Exception ex)
            {
                // three errors in one flight: stop drawing it turned (mode B) instead of failing every frame
                if (++v.errors >= 3 && v.flying && !v.planB) PlanB(em, v, "error while drawing the ring: " + ex.Message);
                else if (v.errors < 3) ModLog.Warn("built-in ring: " + v.name + " " + v.block.Index + ": " + ex.Message);
            }
        }
        static void StepOf(EntityManager em, Joint j, Vis v, float dt, bool ours)
        {
            var part = Ecs.Parent(em, j.block);
            if (!Ecs.IsPart(em, part))
            {
                // garage: the ring is at rest; the next launch decides the mode again
                if (v.flying)
                {
                    v.flying = false;
                    ModLog.Info("built-in ring: " + v.name + " " + v.block.Index + ": back in the garage — ring drawn at rest (largest angle drawn in that flight: " + (v.maxAbs * 57.29578f).ToString("F0") + " degrees, mode " + (v.planB ? "B" : v.found ? "A" : "not decided") + ")");
                }
                Rest(em, v); return;
            }
            if (!v.flying)
            {
                v.flying = true; v.planB = false; v.why = null; v.modeLogged = false; v.controlsLogged = false; v.turnLogged = false; v.checkLogged = false;
                v.tries = 0; v.tTry = 9f; v.tFly = 0f; v.tCheck = 0f; v.misses = 0; v.maxAbs = 0f; v.errors = 0;
                v.turns = Turns;   // the cfg is read once per launch: the mode never changes in the middle of a flight because of it
            }
            v.tFly += dt;
            Mode(em, v, dt);
            if (!v.controlsLogged && (v.tFly > 4f || (Ecs.Alive(em, j.knob) && Ecs.Alive(em, j.swAng))))
            {
                v.controlsLogged = true; var ps = Ecs.Ports(em, j.block);
                ModLog.Info("built-in ring: " + v.name + " " + v.block.Index + ": controls — servo knob " + (Ecs.Alive(em, j.knob) ? "entity " + j.knob.Index : "NOT found")
                    + ", angle switch " + (Ecs.Alive(em, j.swAng) ? "entity " + j.swAng.Index : "NOT found") + ", " + (ps == null ? 0 : ps.Length)
                    + " port(s) (expected 13: 0-3 servo inputs, 4-7 pass-through, 8-11 ring, 12 angle output)");
            }
            if (v.planB) { Rest(em, v); return; }
            if (!v.found) return;

            // the top block: the server knows it from the joint it broke, a client from ClientView
            Entity top = ours ? j.neighbor : j.cNbr;
            bool known = Ecs.Alive(em, top);
            bool apart = known && !Same(Ecs.Parent(em, top), part);
            float th = v.angle; string from = null;
            if (known && !apart) { th = 0f; }                                                    // one rigid part: the garage pose
            else if (apart && ours && j.ringThOk) { th = j.ringTh; from = "the angle output's measure"; }
            else if (apart && !ours && j.cReady) { th = Angle(em, j.block, top, j.cRel0.rot); from = "the server's poses"; }
            else if (apart && Garage.GT(em, j.block, out var gb) && Garage.GT(em, top, out var gt)) { th = Angle(em, j.block, top, Ecs.QMul(Ecs.Inv(gb.rot), gt.rot)); from = "the poses"; }
            else if (apart && j.ringThOk) { th = j.ringTh; from = "the angle output's measure"; }
            // (top not known yet: keep what is drawn)

            // mode B self-check, once a second while the two sides are different parts: the cell plugged into each ring port has to be on
            // the top block's part. It is not when nothing moved the anchors on this machine (a client whose game did not get them, or a
            // split that happened before the mod could act): then a turning plug would walk away from a cable that stays behind.
            if (apart)
            {
                v.tCheck += dt;
                if (v.tCheck >= 1f)
                {
                    v.tCheck = 0f;
                    int bad = Behind(em, v, Ecs.Parent(em, top), out int cabled, out string which);
                    if (bad > 0)
                    {
                        if (++v.misses >= 2) { PlanB(em, v, "after the split, the cable cell plugged into ring port(s) " + which + " is not on the top block's part (" + (ours ? "the anchors were not moved before the split" : "this client's game did not get the cells' part from the server") + ")"); return; }
                    }
                    else
                    {
                        v.misses = 0;
                        if (!v.checkLogged) { v.checkLogged = true; ModLog.Info("built-in ring: " + v.name + " " + v.block.Index + ": check passed — " + (cabled == 0 ? "no cable on the ring ports" : "the cell plugged into each of the " + cabled + " cabled ring port(s) is on the top block's part " + Ecs.Parent(em, top).Index) + " (mode A holds; checked every second)"); }
                    }
                }
            }
            else { v.tCheck = 0f; v.misses = 0; }

            if (float.IsNaN(th) || float.IsInfinity(th)) return;
            Apply(em, v, th);
            float a = Math.Abs(th); if (a > v.maxAbs) v.maxAbs = a;
            if (!v.turnLogged && a > 0.0175f && from != null)
            {
                v.turnLogged = true;
                ModLog.Info("built-in ring: " + v.name + " " + v.block.Index + ": turning with the top block " + top.Index + " — " + (th * 57.29578f).ToString("F1") + " degrees now (from " + from + "); ring plate and 4 plugs redrawn every frame the angle changes by 0.1 degree or more");
            }
        }

        /// <summary>Angle (rad) of the top block around the block's +Y, 0 = the garage pose (`rel0` = top's garage rotation in the block's
        /// space). Same convention as JointSystem.ServoAngle and ClientView, read-only.</summary>
        static float Angle(EntityManager em, Entity block, Entity top, quaternion rel0)
        {
            Ecs.Pose(em, block, out _, out var rB); Ecs.Pose(em, top, out _, out var rN);
            var now = Ecs.QMul(Ecs.Inv(rB), rN);
            var v0 = Flat(Ecs.Rot(rel0, Ecs.UX)); var v1 = Flat(Ecs.Rot(now, Ecs.UX));
            if (Ecs.Len(v0) < 0.5f || Ecs.Len(v1) < 0.5f) { v0 = Flat(Ecs.Rot(rel0, Ecs.UZ)); v1 = Flat(Ecs.Rot(now, Ecs.UZ)); }
            return (float)Math.Atan2(Ecs.Cross(v0, v1).y, Ecs.Dot(v0, v1));
        }
        static float3 Flat(float3 p) { p.y = 0f; float l = Ecs.Len(p); return l < 1e-4f ? default : Ecs.Mul(p, 1f / l); }

        /// <summary>How many cabled ring ports have their plugged cell on another part than `topPart`.</summary>
        static int Behind(EntityManager em, Vis v, Entity topPart, out int cabled, out string which)
        {
            cabled = 0; which = ""; int bad = 0;
            if (v.ringPorts == null) return 0;
            for (int i = 0; i < v.ringPorts.Length; i++)
            {
                if (!Ecs.RawPortPub(em, v.ringPorts[i], out _, out _, out _, out var cell) || !Ecs.IsCable(em, cell)) continue;
                cabled++;
                if (!Same(Ecs.Parent(em, cell), topPart)) { bad++; which += (which.Length > 0 ? ", " : "") + (i + 1); }
            }
            return bad;
        }

        static void Mode(EntityManager em, Vis v, float dt)
        {
            if (v.planB) return;
            if (!v.turns) { PlanB(em, v, "[Slip Ring] embedded_ring_turns = false", false); return; }
            if (v.found)
            {
                bool alive = true; foreach (var e in v.ents) if (!Ecs.Alive(em, e)) { alive = false; break; }
                if (alive) return;
                // the game rebuilt the block's children: what is left goes back to rest and the search starts again
                Rest(em, v); Unfind(v); v.tries = 0; v.tTry = 9f; v.modeLogged = false;
            }
            v.tTry += dt; if (v.tTry < 0.2f) return; v.tTry = 0f;   // first try on the first frame of the flight, then 4 more, 0.2 s apart
            v.tries++;
            if (Find(em, v, out string detail))
            {
                if (!v.modeLogged)
                {
                    v.modeLogged = true;
                    ModLog.Info("built-in ring: " + v.name + " " + v.block.Index + ": found " + detail + " — mode A: the ring is drawn turning with the piece on top"
                        + " (the ports themselves stay where they are; [Slip Ring] embedded_ring_turns)");
                }
            }
            else if (v.tries >= 5) PlanB(em, v, "ring / plug renderers not found after " + v.tries + " tries (" + detail + ") — rebuild the bundle in the Modkit");
        }
        static void PlanB(EntityManager em, Vis v, string why, bool warn = true)
        {
            v.planB = true; v.why = why; Rest(em, v);
            string line = "built-in ring: " + v.name + " " + v.block.Index + ": mode B — the ring is drawn STILL until the next launch (data and power pass the same): " + why;
            if (warn) ModLog.Warn(line); else ModLog.Info(line);
        }
        static void Unfind(Vis v)
        {
            if (v.ringPorts != null) foreach (var p in v.ringPorts) plugOf.Remove(Ecs.K(p));
            v.found = false; v.ents = null; v.restPos = null; v.restRot = null; v.ringPorts = null; v.angle = 0f;
        }

        /// <summary>The 2 ring renderers (children with CRPRendererData within 1 mm of (0, RingY, 0)) and the 4 plugs (the renderer child
        /// within 2 mm of each ring port, the rule SlipRings.PortRenderer uses). Their LocalTransform now is the rest pose — the search
        /// only runs on entities the mod has not turned yet.</summary>
        static bool Find(EntityManager em, Vis v, out string detail)
        {
            detail = "";
            if (!Prepare()) { detail = "CRPRendererData not available"; return false; }
            var ports = RingPorts(em, v.block, v.name);
            if (ports == null) { var ps = Ecs.Ports(em, v.block); detail = "the prefab has " + (ps == null ? 0 : ps.Length) + " port(s), expected 13"; return false; }
            var pp = new float3[4];
            for (int i = 0; i < 4; i++) { if (!Ecs.HasC(em, ports[i], Ecs.tiLT)) { detail = "ring port " + (i + 1) + " has no LocalTransform"; return false; } pp[i] = Ecs.LT(em, ports[i]).Position; }
            var centre = new float3 { x = 0f, y = RingY, z = 0f };
            RestoreKnown(em, v.block);   // 0.31.37: a renderer left turned by an earlier flight is at its rest pose again before the search
            var rings = new List<Entity>(); var plugs = new Entity[4]; int nPlugs = 0, extraRings = 0;
            foreach (var f in Ecs.Children(em, v.block))
            {
                if (!Ecs.HasC(em, f, tiRend) || !Ecs.HasC(em, f, Ecs.tiLT)) continue;
                var p = Ecs.LT(em, f).Position;
                if (Ecs.Dist(p, centre) < 0.001f) { if (rings.Count < 2) rings.Add(f); else extraRings++; continue; }
                for (int i = 0; i < 4; i++)
                {
                    if (!Ecs.IsNull(plugs[i]) || Same(f, ports[i]) || Ecs.Dist(p, pp[i]) >= 0.002f) continue;
                    plugs[i] = f; nPlugs++; break;
                }
            }
            if (rings.Count != 2 || extraRings > 0 || nPlugs != 4)
            { detail = (rings.Count + extraRings) + " ring renderer(s) at (0, " + RingY.ToString("F4") + ", 0) (expected 2) and " + nPlugs + " plug renderer(s) on the 4 ring ports (expected 4)"; return false; }
            var ents = new Entity[6]; ents[0] = rings[0]; ents[1] = rings[1]; for (int i = 0; i < 4; i++) ents[2 + i] = plugs[i];
            var rp = new float3[6]; var rr = new quaternion[6];
            for (int i = 0; i < 6; i++)
            {
                long k = Ecs.K(ents[i]);
                // 0.31.37: a pose this world already knows wins over what is on the entity now — the mod never takes a turned renderer for its rest
                if (restOf.TryGetValue(k, out var r0)) { rp[i] = r0.pos; rr[i] = r0.rot; Write(em, ents[i], rp[i], rr[i]); }
                else { var lt = Ecs.LT(em, ents[i]); rp[i] = lt.Position; rr[i] = lt.Rotation; restOf[k] = new Rest0 { block = v.block, ent = ents[i], pos = rp[i], rot = rr[i] }; }
            }
            v.ents = ents; v.restPos = rp; v.restRot = rr; v.ringPorts = ports; v.angle = 0f; v.found = true;
            for (int i = 0; i < 4; i++) plugOf[Ecs.K(ports[i])] = plugs[i];
            detail = "2 ring renderers (entities " + rings[0].Index + ", " + rings[1].Index + ") and the 4 plugs (entities " + plugs[0].Index + ", " + plugs[1].Index + ", " + plugs[2].Index + ", " + plugs[3].Index
                   + ") of ring ports 1-4 (entities " + ports[0].Index + ", " + ports[1].Index + ", " + ports[2].Index + ", " + ports[3].Index + ")";
            return true;
        }

        /// <summary>Draws the plate and the plugs turned by `th` around the block's +Y: position and rotation of each renderer = its rest
        /// pose turned in the block's space. Works for the M version too (its mirror is a scale applied after the rotation).</summary>
        static unsafe void Apply(EntityManager em, Vis v, float th)
        {
            if (!v.found || Math.Abs(th - v.angle) < MinStep) return;
            float h = th * 0.5f;
            var q = new quaternion { value = new float4 { x = 0f, y = (float)Math.Sin(h), z = 0f, w = (float)Math.Cos(h) } };
            for (int i = 0; i < v.ents.Length; i++)
            {
                var e = v.ents[i];
                if (!Ecs.HasC(em, e, Ecs.tiLT)) continue;
                var lt = (LocalTransform*)em.GetComponentDataRawRW(e, Ecs.tiLT); if (lt == null) continue;
                lt->Position = Ecs.Rot(q, v.restPos[i]); lt->Rotation = Ecs.QMul(q, v.restRot[i]);
            }
            v.angle = th;
        }
        /// <summary>Writes a pose, and says whether anything really moved (so nothing is logged when there was nothing to put back).</summary>
        static unsafe bool Write(EntityManager em, Entity e, float3 pos, quaternion rot)
        {
            if (!Ecs.HasC(em, e, Ecs.tiLT)) return false;
            var lt = (LocalTransform*)em.GetComponentDataRawRW(e, Ecs.tiLT); if (lt == null) return false;
            var q = lt->Rotation.value; var r = rot.value;
            bool same = Ecs.Dist(lt->Position, pos) < 1e-5f
                     && Math.Abs(q.x - r.x) + Math.Abs(q.y - r.y) + Math.Abs(q.z - r.z) + Math.Abs(q.w - r.w) < 1e-5f;
            lt->Position = pos; lt->Rotation = rot;
            return !same;
        }
        /// <summary>Puts back every child of the block whose rest pose is already known (Find then reads a pristine block, even after a flight
        /// that ended with the ring turned and the joint — with its Vis — gone).</summary>
        static int RestoreKnown(EntityManager em, Entity block)
        {
            int n = 0;
            foreach (var f in Ecs.Children(em, block))
                if (restOf.TryGetValue(Ecs.K(f), out var r0) && Write(em, f, r0.pos, r0.rot)) n++;
            return n;
        }
        /// <summary>Twice a second (RingCables.GaragePass): every renderer the mod ever turned, whose block is standing in a garage, goes back
        /// to its rest pose. It needs no joint and no block list, so it also covers the flight that ends with the mod's joint state cleared.</summary>
        public static void RestInGarage(EntityManager em)
        {
            if (restOf.Count == 0) return;
            gone.Clear(); int back = 0; Entity one = Entity.Null;
            foreach (var kv in restOf)
            {
                var r0 = kv.Value;
                if (!Ecs.Alive(em, r0.ent) || !Ecs.Alive(em, r0.block)) { gone.Add(kv.Key); continue; }
                if (Ecs.IsPart(em, Ecs.Parent(em, r0.block))) continue;   // flying: the flight draws it
                if (!Write(em, r0.ent, r0.pos, r0.rot)) continue;
                back++; one = r0.block;
                if (all.TryGetValue(Ecs.K(r0.block), out var v)) v.angle = 0f;
            }
            foreach (var k in gone) restOf.Remove(k);
            if (back > 0) ModLog.Info("built-in ring: back in the garage — " + back + " renderer(s) of the ring put back where the block has them (block " + one.Index + ")");
        }
        static unsafe void Rest(EntityManager em, Vis v)
        {
            if (!v.found || v.angle == 0f) return;
            try
            {
                for (int i = 0; i < v.ents.Length; i++)
                {
                    var e = v.ents[i];
                    if (!Ecs.HasC(em, e, Ecs.tiLT)) continue;
                    var lt = (LocalTransform*)em.GetComponentDataRawRW(e, Ecs.tiLT); if (lt == null) continue;
                    lt->Position = v.restPos[i]; lt->Rotation = v.restRot[i];
                }
            }
            catch (Exception ex) { if (v.warnings++ < 2) ModLog.Warn("built-in ring: " + v.name + " " + v.block.Index + ": putting the ring back to rest: " + ex.Message); }
            v.angle = 0f;
        }
        /// <summary>Garage pass (RingCables.GarageStep, twice a second): a block seen in the garage has its ring at rest.</summary>
        public static void GarageRest(EntityManager em, Entity block)
        {
            if (Ecs.IsPart(em, Ecs.Parent(em, block))) return;
            if (all.TryGetValue(Ecs.K(block), out var v)) Rest(em, v);
            RestoreKnown(em, block);   // 0.31.37: and whatever is left turned without a Vis behind it
        }
        static readonly List<long> dead = new List<long>();
        public static void Prune(EntityManager em)
        {
            dead.Clear();
            foreach (var kv in all) if (!Ecs.Alive(em, kv.Value.block)) dead.Add(kv.Key);
            foreach (var k in dead) { Unfind(all[k]); all.Remove(k); }
        }
        /// <summary>After a world unload (JointSystem.ForgetAll): every entity is gone, and a new world hands the same numbers out again.</summary>
        /// <summary>0.31.37: the rest poses are NOT dropped here. This runs at the end of every flight too (Core.CleanupWorldToDefaultState
        /// -> JointSystem.ForgetAll), which is exactly when the garage still has to put the ring back; and the key holds the entity's version,
        /// so an entry of a world that is really gone can never match a new entity — RestInGarage drops it when it sees it is dead.</summary>
        public static void WorldGone() { all.Clear(); plugOf.Clear(); }
        public static string Situation()
        {
            if (all.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var v in all.Values) sb.Append("\n    ").Append(v.name).Append(' ').Append(v.block.Index).Append(": built-in ring ").Append(!v.flying ? "in the garage" : v.planB ? "mode B (" + v.why + ")" : v.found ? "mode A, " + (v.angle * 57.29578f).ToString("F0") + " degrees" : "looking for its renderers");
            return sb.ToString();
        }
    }

    /// <summary>0.31.36 — host / solo only. What the game does with a cable when a part splits: each cable cell goes to the part of the block
    /// in its _anchorRangeEntity (SCTypeCableCell +40) [recon/Spaceship__part_a.cs:266-274]. A cell plugged into a port is anchored to the
    /// port's block, and the up to 12 loose cells after it take that anchor too [recon/GarageUpdateCables.cs:397-398, 536-543]. The ring
    /// ports belong to the bearing block, which stays on the base: left alone, the first cells of every ring cable would stay on the base
    /// while the rest turns. So, right before the mod breaks the top joints (JointSystem.WaitingTop) and while everything is still ONE
    /// part, those cells' range anchor is moved from the bearing to the block glued on top. The game's own split then takes them along.
    /// Only +40 is written (the anchor itself and the distance stay), only on cells whose anchor range is this block at the distance a
    /// cable from this port would give, and only while the cell, the bearing and the top block are in the same part — a cell's range
    /// anchor must always be a block of the cell's own part (the game's split job does not check for anything else).
    /// Back in the garage every cell gets its original anchor back.</summary>
    internal static class RingCables
    {
        struct Moved { public Entity cell, orig, now; }
        static readonly Dictionary<long, Moved> moved = new Dictionary<long, Moved>();
        static readonly Dictionary<long, long> said = new Dictionary<long, long>();   // block -> top block already logged
        static int warnings;
        static bool Same(Entity a, Entity b) => a.Index == b.Index && a.Version == b.Version;

        /// <summary>The next cell along the cable (the rule of Ecs.WalkCable).</summary>
        static Entity Next(EntityManager em, Entity fwd, Entity bwd, Entity prev, HashSet<long> seen)
        {
            if (Same(fwd, prev)) return bwd;
            if (Same(bwd, prev)) return fwd;
            return !Ecs.IsNull(fwd) && !seen.Contains(Ecs.K(fwd)) ? fwd : bwd;
        }

        public static unsafe void Follow(EntityManager em, Joint j, Entity nbr)
        {
            var block = j.block;
            // review: only the machine that runs the physics moves anchors (WaitingTop also runs on a client with the debug option
            // [Multiplayer] break_on_client: a client never writes into a cable cell)
            if (!Multi.Authoritative(em)) return;
            if (!RingVisual.ModeA(block)) return;   // mode B: the game's anchors stay as they are
            var part = Ecs.Parent(em, block);
            if (!Ecs.IsPart(em, part) || !Ecs.Alive(em, nbr) || !Same(Ecs.Parent(em, nbr), part)) return;
            var ports = RingVisual.RingPorts(em, block, j.name); if (ports == null) return;
            int cells = 0, already = 0, cabled = 0; var seen = new HashSet<long>(); string odd = "";
            for (int i = 0; i < ports.Length; i++)
            {
                if (!Ecs.RawPortPub(em, ports[i], out _, out _, out _, out var cur) || !Ecs.IsCable(em, cur)) continue;
                cabled++;
                var prev = ports[i];
                for (int k = 0; k <= 12 && Ecs.IsCable(em, cur) && seen.Add(Ecs.K(cur)); k++)
                {
                    var b = (byte*)Ecs.RO(em, cur, Ecs.tiCable); if (b == null) break;
                    var fwd = *(Entity*)(b + 16); var bwd = *(Entity*)(b + 24); var anchor = *(Entity*)(b + 32); var range = *(Entity*)(b + 40); int dist = *(int*)(b + 48);
                    bool mine = Same(range, block), done = Same(range, nbr);
                    // not plugged into this port / its range comes from somewhere else (a frame, another port) / not of this part
                    if ((k == 0 && !Same(anchor, block)) || dist != k || (!mine && !done) || !Same(Ecs.Parent(em, cur), part))
                    {
                        // the cell right at the plug should always pass: say what the game really stored there (for the next log)
                        if (k == 0) odd += "; ring port " + (i + 1) + ": the plugged cell " + cur.Index + " was left alone (anchor " + anchor.Index + ", anchor range " + range.Index + " at distance " + dist + ", part " + Ecs.Parent(em, cur).Index + "; expected anchor and range = the bearing " + block.Index + ", distance 0, part " + part.Index + ")";
                        break;
                    }
                    if (mine)
                    {
                        var w = (byte*)em.GetComponentDataRawRW(cur, Ecs.tiCable); if (w == null) break;
                        *(Entity*)(w + 40) = nbr;
                        moved[Ecs.K(cur)] = new Moved { cell = cur, orig = block, now = nbr }; cells++;
                    }
                    else already++;
                    var next = Next(em, fwd, bwd, prev, seen); prev = cur; cur = next;
                }
            }
            long kb = Ecs.K(block), kn = Ecs.K(nbr);
            if (cells > 0 || !said.TryGetValue(kb, out var before) || before != kn)
            {
                said[kb] = kn;
                ModLog.Info("built-in ring: " + j.name + " " + block.Index + ": " + (cabled == 0 ? "no cable on the 4 ring ports — nothing to hand over"
                    : cells + " cable cell(s) on " + cabled + " cabled ring port(s) now follow the top block " + nbr.Index + (already > 0 ? " (" + already + " already did)" : "")
                      + " — their anchor range moved from the bearing, before the split, so the game puts them on the part that turns") + " (host only)" + odd);
            }
        }

        static float tGarage;
        /// <summary>Garage pass, twice a second, every machine (the list of moved cells is only ever filled on the host; review: the
        /// two guards that write a cell's anchor range also run on the host / solo only — a client only puts its drawing at rest).</summary>
        public static void GarageStep(EntityManager em, float dt)
        {
            tGarage += dt; if (tGarage < 0.5f) return; tGarage = 0f;
            if (!JointSystem.HashesOk) return;
            bool host = Multi.Authoritative(em);
            if (host && moved.Count > 0) { try { FlightGuard(em); } catch (Exception ex) { if (warnings++ < 3) ModLog.Warn("built-in ring: flight check: " + ex.Message); } }
            GaragePass(em, host);
        }
        /// <summary>In flight: a moved cell whose new range anchor (the top block) no longer exists gets a block of the cell's OWN part
        /// instead — the bearing if it is in that part, else any block of it — so the rule "the range anchor is a block of the cell's
        /// part" still holds. Only a cell that still carries what the mod wrote is touched. (Nothing known destroys a block in flight;
        /// this is a guard, not a path that is expected to run.)</summary>
        static unsafe void FlightGuard(EntityManager em)
        {
            List<Moved> fix = null;
            foreach (var kv in moved)
            {
                var m = kv.Value;
                if (Ecs.Alive(em, m.now) || !Ecs.IsCable(em, m.cell)) continue;
                var part = Ecs.Parent(em, m.cell); if (!Ecs.IsPart(em, part)) continue;   // garage: GaragePass and Guard look after it
                var b = (byte*)Ecs.RO(em, m.cell, Ecs.tiCable); if (b == null || !Same(*(Entity*)(b + 40), m.now)) continue;
                Entity pick = Ecs.Alive(em, m.orig) && Same(Ecs.Parent(em, m.orig), part) ? m.orig : Entity.Null;
                if (Ecs.IsNull(pick)) foreach (var c in Ecs.Children(em, part)) { if (Ecs.IsCable(em, c) || Ecs.Prefab(em, c) == 0) continue; pick = c; break; }
                if (Ecs.IsNull(pick)) continue;
                var w = (byte*)em.GetComponentDataRawRW(m.cell, Ecs.tiCable); if (w == null) continue;
                *(Entity*)(w + 40) = pick;
                (fix ??= new List<Moved>()).Add(new Moved { cell = m.cell, orig = m.orig, now = pick });
            }
            if (fix == null) return;
            foreach (var m in fix) moved[Ecs.K(m.cell)] = m;
            if (warnings++ < 5) ModLog.Warn("built-in ring: the top block that " + fix.Count + " ring cable cell(s) followed no longer exists — they now hold on to block " + fix[0].now.Index + " of their own part");
        }
        static unsafe void GaragePass(EntityManager em, bool host)
        {
            if (host && moved.Count > 0)
            {
                int back = 0, gone = 0; List<long> drop = null;
                foreach (var kv in moved)
                {
                    var m = kv.Value;
                    if (!Ecs.IsCable(em, m.cell)) { (drop ??= new List<long>()).Add(kv.Key); gone++; continue; }
                    if (Ecs.IsPart(em, Ecs.Parent(em, m.cell))) continue;   // still flying
                    var b = (byte*)Ecs.RO(em, m.cell, Ecs.tiCable);
                    if (b != null && Same(*(Entity*)(b + 40), m.now) && Ecs.Alive(em, m.orig))
                    {
                        var w = (byte*)em.GetComponentDataRawRW(m.cell, Ecs.tiCable);
                        if (w != null) { *(Entity*)(w + 40) = m.orig; back++; }
                    }
                    (drop ??= new List<long>()).Add(kv.Key);
                }
                if (drop != null) foreach (var k in drop) moved.Remove(k);
                if (back > 0) ModLog.Info("built-in ring: back in the garage — " + back + " cable cell(s) got the bearing back as their anchor range (as the garage made them)");
            }
            // 0.31.37: first, and whatever the block list says, every renderer the mod turned goes back where the block has it
            RingVisual.RestInGarage(em);
            // every self-ring block standing in the garage: ring at rest, and no ring cable may point at a block that is gone
            foreach (var (e, h) in Garage.Set(em).mod)
            {
                if (!JointSystem.KindFromHash(h, out var kind, out _, out var name) || kind != Kind.Bearing || !SlipRings.SelfRing(name) || !Ecs.Alive(em, e)) continue;
                RingVisual.GarageRest(em, e); said.Remove(Ecs.K(e));
                if (host) { try { Guard(em, e, name); } catch (Exception ex) { if (warnings++ < 3) ModLog.Warn("built-in ring: garage check: " + ex.Message); } }
            }
            RingVisual.Prune(em);
            if (said.Count > 64) said.Clear();
        }
        /// <summary>A cell on one of the block's ring cables whose range anchor is an entity that no longer exists (the top block was
        /// deleted in the garage before the anchors went back) gets the bearing again.</summary>
        static unsafe void Guard(EntityManager em, Entity block, string name)
        {
            if (Ecs.IsPart(em, Ecs.Parent(em, block))) return;
            var ports = RingVisual.RingPorts(em, block, name); if (ports == null) return;
            int repaired = 0; var seen = new HashSet<long>();
            for (int i = 0; i < ports.Length; i++)
            {
                if (!Ecs.RawPortPub(em, ports[i], out _, out _, out _, out var cur) || !Ecs.IsCable(em, cur)) continue;
                var prev = ports[i];
                for (int k = 0; k <= 12 && Ecs.IsCable(em, cur) && seen.Add(Ecs.K(cur)); k++)
                {
                    var b = (byte*)Ecs.RO(em, cur, Ecs.tiCable); if (b == null) break;
                    var fwd = *(Entity*)(b + 16); var bwd = *(Entity*)(b + 24); var anchor = *(Entity*)(b + 32); var range = *(Entity*)(b + 40); int dist = *(int*)(b + 48);
                    if (k == 0 && !Same(anchor, block)) break;
                    if (dist != k) break;
                    if (!Ecs.IsNull(range) && !Ecs.Alive(em, range))
                    {
                        var w = (byte*)em.GetComponentDataRawRW(cur, Ecs.tiCable); if (w == null) break;
                        *(Entity*)(w + 40) = block; repaired++;
                    }
                    else if (!Same(range, block)) break;
                    var next = Next(em, fwd, bwd, prev, seen); prev = cur; cur = next;
                }
            }
            if (repaired > 0 && warnings++ < 5) ModLog.Warn("built-in ring: " + name + " " + block.Index + ": " + repaired + " cable cell(s) on its ring ports pointed at a block that no longer exists — given back to the bearing");
        }
        /// <summary>After a world unload: the cells are gone with the world.</summary>
        public static void WorldGone() { moved.Clear(); said.Clear(); }
    }
}
