using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Physics;
using UnityEngine;

namespace CrapMechanics
{
    /// <summary>0.31.28 Rubber Seal (Andre, 22/11: "a part the player builds and positions well — no cheating"). Straight pieces the size of the
    /// game's pipes (4/4, 2/4, 1/4: 0.25 x 0.25 x 1 / 0.5 / 0.25) and of the MicroFrames (mini: 0.125 x 0.125 x 0.5 / 0.25 / 0.125), whose rubber LIP
    /// sticks out of the +Y face (3 cm, mini 2 cm), and corners glued on -Y and -X whose lip is an L over +Y and +X (two lip boxes). The game decides "room closed" by casting 54 spheres of 3.5 cm from
    /// each player/object (InteriorMetersPreUpdate, filter 0x18 = ship blocks and frames); it never reads the material of what it hits
    /// [revisao/seal_pesquisa.md items 0-1]. The lip is an ordinary collider child of the block, so it blocks those casts where it physically is —
    /// and only there: a gap the rubber does not cover keeps leaking. The game's rule is untouched.
    ///
    /// What this file does: the lip must not PUSH anything (it sits 3 cm inside the gate piece it seals against; a solid lip would be shoved out
    /// at launch and rub with friction 1.7). Unity Physics skips contacts when either side's material has DisableCollisions, per child of a
    /// compound, while queries ignore the material [Manifold.cs:225-231, 446-451; Collector.cs:330]. The material comes from the block class
    /// (all its children share it), so:
    ///  1. once, after the game builds the seal prefabs: each lip entry of the prefab's SCColliderBlobInstances buffer is pointed to a copy of the
    ///     same box with the "no collisions" material, made by the game's own collider cache (Core.getOrCreateCollider: the key includes the
    ///     material, so it is a separate cached blob that lives exactly as long as the original). Every block is a copy of the prefab, and every
    ///     part collider (launch, split, client, time rewind) is built from these buffers [SpaceshipSystem.cs:218; Spaceship__part_a.cs:226;
    ///     part_b.cs:534; TimeRewindSystem:1614] — the compounds are born with the lip already soft. The cached blob of the original is never
    ///     written. The garage uses the block's own collider (_colliderBAR), not the buffer: there the seal is a normal block.
    ///  2. safety net, cheap: blocks that were built before step 1 get the same swap in their buffer; and a part containing seals whose collider
    ///     changed (new blob) is checked once — a lip child still solid gets the flag inside the part's private copy.
    /// Nothing runs per tick and no game system is hooked; a launch writes one line to the log.
    ///
    /// 0.31.30 [Cheats] rubber_seal_solid_percent (Andre: "the gate is perfect, there is no room left in the ship; let me choose in a slider how
    /// much of the rubber has collision — then it is an option to cheat or not"). The body of each seal is now 8 slabs (generator: the straight
    /// pieces in 8 layers along Y; the corner in 8 L-shaped layers away from its two walls). Each box has a DEPTH = how far its nearest edge is from
    /// the glued face(s), as a fraction of the block (0, 1/8 ... 7/8; the lips 1). A box is soft when depth >= the solid fraction: 100 % = only the
    /// lips (the honest seal, as before); 50 % = the 4 slabs nearest the lip too; 0 % = all. Soft boxes still block the air (the game's casts ignore
    /// the material), so below 100 % it is a cheat — Andre's choice, off by default. A change applies to the prefabs, the blocks' buffers and the
    /// part colliders in flight (flag byte of each child) within a second.</summary>
    [HarmonyPatch]
    internal static class RubberSeal
    {
        public static readonly string[] Names = { "SC_RubberSeal4", "SC_RubberSeal2", "SC_RubberSeal1", "SC_RubberSealCorner",
                                                  "SC_MiniRubberSeal4", "SC_MiniRubberSeal2", "SC_MiniRubberSeal1", "SC_MiniRubberSealCorner" };   // seal redo: pipe / MicroFrame sizes
        public static bool IsSeal(string name) => name != null && Array.IndexOf(Names, name) >= 0;

        const byte TypeBox = 5, TypeCompound = 8;
        const byte FlagTrigger = 1, FlagEvents = 2, FlagNoCollisions = 0x10;   // Unity.Physics.Material.MaterialFlags
        const int ElemSize = 48, ElemCollider = 32;   // CompoundCollider.ColliderBlobInstance: CompoundFromChild (28 + pad), Collider @32, Entity @40
        const int HdrMaterialFlags = 20;              // ConvexColliderHeader: Type, CollisionType, Version, Magic, ForceUniqueBlobID, Filter (12), Material @20
        const int BoxCenter = 84;                     // BoxCollider.m_Center (float3)

        /// <summary>0.31.30: [Cheats] rubber_seal_solid_percent / 100 (1 = honest: only the lips are soft).</summary>
        public static float SolidFraction = 1f;
        static float applied = -1f; static int gen;   // gen: bumps when the fraction changes (blocks and parts are looked at again)

        class Entry { public Vector3 c; public float depth; public IntPtr solid, soft; public bool wantSoft; }
        class Proto
        {
            public string name; public EPC_SpaceshipComponent epc; public ulong hash;
            public Entity prefab; public bool ready; public int lips, softBoxes; public string problem; public bool problemLogged;
            public Entry[] entries = new Entry[0]; public byte solidFlags;
            public float Size => name != null && name.StartsWith("SC_Mini") ? 0.125f : 0.25f;   // cross-section (the corner: the cube)
        }
        static readonly List<Proto> protos = new List<Proto>();
        static TypeIndex tiBlob, tiPC; static bool tiOk, tiFailed;
        static float tPrefab = 99f, tStep = 99f; static bool allReady, anyHash; static int warnings;

        // ---------------------------------------------------------------- registration
        /// <summary>The registered seal prefabs (Components Framework's instances, the ones the Core builds the prefab entities from).</summary>
        public static void Connect(IReadOnlyList<EPC_SpaceshipComponent> regs)
        {
            if (regs == null) return;
            try
            {
                foreach (var e in regs)
                {
                    if (e == null) continue; string n = e.name;
                    if (!IsSeal(n)) continue;
                    protos.Add(new Proto { name = n, epc = e });
                }
                ModLog.Info("rubber seal: " + protos.Count + " of " + Names.Length + " seal block(s) registered" + (protos.Count < Names.Length ? " — rebuild the asset bundle (Modkit Build + Deploy) with the new Content files" : ""));
            }
            catch (Exception ex) { ModLog.Warn("rubber seal: registration: " + ex.Message); }
        }

        static bool Types()
        {
            if (tiOk) return true; if (tiFailed) return false;
            try
            {
                tiBlob = TypeManager.GetTypeIndex(Il2CppType.Of<SCColliderBlobInstances>());
                tiPC = TypeManager.GetTypeIndex(Il2CppType.Of<PhysicsCollider>());
                tiOk = true; return true;
            }
            catch (Exception ex) { tiFailed = true; ModLog.Warn("rubber seal: component types not found (" + ex.Message + ") — the lips stay solid"); return false; }
        }

        // ---------------------------------------------------------------- raw blob reads
        static unsafe byte Type(IntPtr c) => *(byte*)c;
        static unsafe bool Soft(IntPtr c) => (*((byte*)c + HdrMaterialFlags) & FlagNoCollisions) != 0;
        static unsafe Vector3 Center(IntPtr c) { var f = (float*)((byte*)c + BoxCenter); return new Vector3(f[0], f[1], f[2]); }
        static bool IsBox(IntPtr c) => c != IntPtr.Zero && Type(c) == TypeBox;
        static bool Near(Vector3 a, Vector3 b) => System.Math.Abs(a.x - b.x) < 1e-4f && System.Math.Abs(a.y - b.y) < 1e-4f && System.Math.Abs(a.z - b.z) < 1e-4f;
        /// <summary>Same change Material.CollisionResponse = None makes (no trigger, no events, DisableCollisions).</summary>
        static byte SoftFlags(byte f) => (byte)((f & ~(FlagTrigger | FlagEvents)) | FlagNoCollisions);
        static byte SolidFlags(byte f, byte orig) => (byte)((f & ~(FlagNoCollisions | FlagTrigger | FlagEvents)) | (orig & (FlagTrigger | FlagEvents | FlagNoCollisions)));
        static bool WantSoft(float depth) => depth >= SolidFraction - 1e-3f;
        static Entry EntryAt(Proto p, Vector3 c) { foreach (var en in p.entries) if (en != null && Near(en.c, c)) return en; return null; }

        // ---------------------------------------------------------------- 1. the prefabs
        /// <summary>Every 0.5 s until the 8 prefabs are done, then every 5 s a quick look (a new game session rebuilds the prefab entities);
        /// right away when the solid fraction changes (0.31.30).</summary>
        public static void PrefabStep(EntityManager em)
        {
            if (protos.Count == 0) return;
            bool changed = System.Math.Abs(SolidFraction - applied) > 1e-4f;
            tPrefab += Time.unscaledDeltaTime; if (!changed && tPrefab < (allReady ? 5f : 0.5f)) return; tPrefab = 0f;
            if (Unloading.Active || !Types()) return;
            var core = Core._singleton; if (core == null) return;
            var map = EntityPrefabComponent.GameObjectToEntityMap; if (map == null) return;   // not Get(): that would BUILD the prefab outside the game's order
            if (changed)
            {
                bool first = applied < 0f; applied = SolidFraction; gen++; allReady = false; stampFlight = -1; stampGarage = -1; tStep = 99f;
                foreach (var p in protos) p.ready = false;
                if (!first || SolidFraction < 0.999f)
                    ModLog.Info("rubber seal: solid part " + (SolidFraction * 100f).ToString("F1") + " %" + (SolidFraction < 0.999f ? " — CHEAT ([Cheats] rubber_seal_solid_percent): the part of each seal nearest the lip is soft too (still blocks the air)" : " (honest: only the lips are soft)"));
            }
            int ready = 0, lips = 0, softBoxes = 0;
            foreach (var p in protos)
            {
                try
                {
                    if (p.hash == 0) { p.hash = Ecs.HashPrefab(p.name); if (p.hash != 0) anyHash = true; }   // review: before the checks below (Step finds the blocks by it)
                    var go = p.epc != null ? p.epc.gameObject : null;
                    if (go == null || !map.ContainsKey(go)) { p.ready = false; continue; }
                    Entity pe = map[go];
                    if (!Ecs.HasC(em, pe, tiBlob)) { p.ready = false; continue; }
                    if (p.ready && pe.Index == p.prefab.Index && pe.Version == p.prefab.Version && StillRight(em, pe, p)) { ready++; lips += p.lips; softBoxes += p.softBoxes; continue; }
                    p.prefab = pe; p.ready = PrepareProto(em, core, p, pe);
                    if (p.ready) { ready++; lips += p.lips; softBoxes += p.softBoxes; }
                    else if (!p.problemLogged && p.problem != null) { p.problemLogged = true; ModLog.Warn("rubber seal: " + p.name + ": " + p.problem + " — its lip stays SOLID (it can push what it touches)"); }
                }
                catch (Exception ex) { p.ready = false; if (warnings++ < 5) ModLog.Warn("rubber seal: " + p.name + ": " + ex.Message); }
            }
            bool now = ready == protos.Count;
            if (now && !allReady) ModLog.Info("rubber seal: " + ready + " prefab(s) ready — " + lips + " lip(s)" + (softBoxes > lips ? " and " + (softBoxes - lips) + " body slab(s)" : "")
                                              + " now use a no-push copy from the game's collider cache (blocks the air, pushes nothing); the garage keeps the normal block");
            allReady = now;
        }

        /// <summary>The prefab's buffer still points to the copies this fraction wants.</summary>
        static unsafe bool StillRight(EntityManager em, Entity pe, Proto p)
        {
            int n = em.GetBufferLength(pe, tiBlob); var buf = (byte*)em.GetBufferRawRO(pe, tiBlob);
            if (buf == null || n != p.entries.Length) return false;
            for (int k = 0; k < n; k++)
            {
                var en = p.entries[k]; if (en == null) continue;
                var c = *(IntPtr*)(buf + ElemSize * k + ElemCollider);
                if (c != (en.wantSoft ? en.soft : en.solid)) return false;
            }
            return true;
        }

        /// <summary>seal redo: was "Prepare" — Harmony takes a static method of that name in a [HarmonyPatch] class as the patch's own setup
        /// method and tries to call it with arguments it fills itself; these parameters are not ones it can fill, so PatchAll threw ("rubber seal:
        /// launch hook failed" in the 22/11 log) and the launch hook (OnLaunch, only the log line) was never applied.
        /// 0.31.30: every box gets a solid and a soft copy from the game's collider cache (the key includes the material: two cached blobs that
        /// live as long as the game's own); the buffer points to the one the solid fraction wants.</summary>
        static unsafe bool PrepareProto(EntityManager em, Core core, Proto p, Entity pe)
        {
            p.problem = null; p.lips = 0; p.softBoxes = 0;
            var cols = p.epc._colliders;
            int n = cols != null ? cols.Length : 0;
            int len = em.GetBufferLength(pe, tiBlob);
            if (n < 2 || len != n) { p.problem = "the prefab lists " + n + " collider(s) and its entity has " + len + " (expected the same number, in the same order)"; return false; }
            var filter = p.epc.GetCollisionFilter();
            var mat = p.epc.GetPhysicsMaterial();
            var soft = mat; soft.Flags = (Unity.Physics.Material.MaterialFlags)SoftFlags((byte)mat.Flags);
            p.solidFlags = (byte)mat.Flags;
            var buf = (byte*)em.GetBufferRawRW(pe, tiBlob);
            if (buf == null) { p.problem = "collider buffer not readable"; return false; }
            float size = p.Size, h = size / 2f;
            var entries = new Entry[n];
            for (int k = 0; k < n; k++)
            {
                var box = cols[k] != null ? cols[k].TryCast<DOTSColliderBox>() : null;
                var cur = *(IntPtr*)(buf + ElemSize * k + ElemCollider);
                if (box == null) continue;   // not a box: never touched
                var want = new Vector3(box._offset.x, box._offset.y, box._offset.z);
                float minX = box._offset.x - box._scale.x / 2f, minY = box._offset.y - box._scale.y / 2f;
                float depth = System.Math.Max((minX + h) / size, (minY + h) / size);   // 0 at the glued face(s); the lips start at 1
                if (!IsBox(cur) || !Near(Center(cur), want)) { p.problem = "buffer entry " + k + " is not the box the prefab describes"; return false; }
                DOTSCollider d = cols[k];
                var barS = core.getOrCreateCollider(ref d, filter, soft);
                IntPtr softP = !ReferenceEquals(barS, null) ? barS.m_data.m_Ptr : IntPtr.Zero;   // (no == on the interop wrapper: it calls into the game)
                IntPtr solidP = cur;
                if (Soft(cur))
                {
                    d = cols[k]; var barH = core.getOrCreateCollider(ref d, filter, mat);
                    solidP = !ReferenceEquals(barH, null) ? barH.m_data.m_Ptr : IntPtr.Zero;
                }
                if (softP == IntPtr.Zero || solidP == IntPtr.Zero || softP == solidP || !IsBox(softP) || !IsBox(solidP) || !Near(Center(softP), want) || !Near(Center(solidP), want) || !Soft(softP) || Soft(solidP))
                { p.problem = "the game's collider cache did not give a separate no-push copy of box " + k; return false; }
                var en = new Entry { c = want, depth = depth, solid = solidP, soft = softP, wantSoft = WantSoft(depth) };
                entries[k] = en;
                if (depth >= 0.999f) p.lips++;
                if (en.wantSoft) p.softBoxes++;
            }
            if (p.lips == 0) { p.problem = "no lip box in the prefab (old asset bundle?)"; return false; }
            p.entries = entries;
            for (int k = 0; k < n; k++) { var en = entries[k]; if (en != null) *(IntPtr*)(buf + ElemSize * k + ElemCollider) = en.wantSoft ? en.soft : en.solid; }
            return true;
        }

        // ---------------------------------------------------------------- 2. blocks and parts
        class PartState { public IntPtr blob; public int gen; public readonly List<int> sealChildren = new List<int>(); }
        static readonly Dictionary<long, PartState> parts = new Dictionary<long, PartState>();
        static readonly Dictionary<long, Proto> sealOf = new Dictionary<long, Proto>();
        static readonly Dictionary<long, Entity> partsNow = new Dictionary<long, Entity>();
        static int stampFlight = -1, stampGarage = -1;
        static int fixedBuffers, patchedBoxes, lipsNow, softNow, sealsFlight, partsWithSeals;   // fixed*/patched*: since the last launch; the rest: the last pass
        static float tReport = -1f;

        /// <summary>Frame cycle (not inside the game's ticks): every 0.5 s, and only for seals and the parts that hold them.</summary>
        public static void Step(EntityManager em)
        {
            // review: not gated on ALL the prefabs being ready — one prefab that fails must not switch off the safety net for the others
            if (protos.Count == 0 || !anyHash || Unloading.Active || !Types() || !Ecs.Prepare(em)) { Report(); return; }
            tStep += Time.unscaledDeltaTime;
            bool report = tReport > 0f && Time.unscaledTime >= tReport;
            if (tStep < 0.5f && !report) return;
            tStep = 0f;
            // blocks: the buffer of a block built before the prefab swap or before a fraction change (looked at when the block lists change)
            var fl = Ecs.Launched(em);
            if (fl.stamp != stampFlight) { stampFlight = fl.stamp; foreach (var p in protos) foreach (var e in fl.Of(p.hash)) FixBuffer(em, e, p); }
            var ga = Garage.Set(em);
            if (ga.stamp != stampGarage) { stampGarage = ga.stamp; foreach (var p in protos) foreach (var e in ga.Of(p.hash)) FixBuffer(em, e, p); }
            // parts in flight that hold seals
            sealOf.Clear(); partsNow.Clear(); int seals = 0;
            foreach (var p in protos) if (p.ready && !Ecs.IsNull(p.prefab)) sealOf[Ecs.K(p.prefab)] = p;   // review: never Entity.Null (key 0)
            foreach (var p in protos)
            {
                if (!p.ready) continue;
                foreach (var e in fl.Of(p.hash))
                {
                    if (!Ecs.Alive(em, e)) continue;
                    var part = Ecs.Parent(em, e); if (Ecs.IsNull(part) || !Ecs.IsPart(em, part)) continue;
                    seals++; sealOf[Ecs.K(e)] = p; partsNow[Ecs.K(part)] = part;
                }
            }
            lipsNow = 0; softNow = 0;
            foreach (var kv in partsNow) CheckPart(em, kv.Key, kv.Value);
            if (parts.Count > partsNow.Count) { var gone = new List<long>(); foreach (var k in parts.Keys) if (!partsNow.ContainsKey(k)) gone.Add(k); foreach (var k in gone) parts.Remove(k); }
            sealsFlight = seals; partsWithSeals = partsNow.Count;
            Report();
        }

        /// <summary>review: the replacement is read from the prefab's buffer as it is NOW, not from a remembered pointer — if the game ever rebuilt
        /// its collider cache, a remembered blob would be a freed one. 0.31.30: both ways (solid -> soft and soft -> solid).</summary>
        static unsafe void FixBuffer(EntityManager em, Entity e, Proto p)
        {
            try
            {
                if (!p.ready || !Ecs.HasC(em, e, tiBlob) || !Ecs.HasC(em, p.prefab, tiBlob)) return;
                int n = em.GetBufferLength(e, tiBlob); if (n <= 0 || n > 32 || em.GetBufferLength(p.prefab, tiBlob) != n) return;
                var ro = (byte*)em.GetBufferRawRO(e, tiBlob); if (ro == null) return;
                var src = (byte*)em.GetBufferRawRO(p.prefab, tiBlob); if (src == null) return;
                bool need = false;
                for (int k = 0; k < n && !need; k++)
                {
                    var c = *(IntPtr*)(ro + ElemSize * k + ElemCollider); var s = *(IntPtr*)(src + ElemSize * k + ElemCollider);
                    if (c != s && IsBox(c) && IsBox(s) && Soft(c) != Soft(s) && Near(Center(s), Center(c))) need = true;
                }
                if (!need) return;
                var buf = (byte*)em.GetBufferRawRW(e, tiBlob); if (buf == null) return;
                src = (byte*)em.GetBufferRawRO(p.prefab, tiBlob); if (src == null) return;
                for (int k = 0; k < n; k++)
                {
                    var c = *(IntPtr*)(buf + ElemSize * k + ElemCollider); var s = *(IntPtr*)(src + ElemSize * k + ElemCollider);
                    if (c != s && IsBox(c) && IsBox(s) && Soft(c) != Soft(s) && Near(Center(s), Center(c))) { *(IntPtr*)(buf + ElemSize * k + ElemCollider) = s; fixedBuffers++; }
                }
            }
            catch (Exception ex) { if (warnings++ < 5) ModLog.Warn("rubber seal: block " + e.Index + ": " + ex.Message); }
        }

        /// <summary>A part's collider is a compound with one child per collider of each block (CompoundCollider.Child: CompoundFromChild,
        /// collider offset @28, Entity @32 = the block). New blob or new fraction = walk it once and set each seal box's flag to what the
        /// fraction wants (in the part's private copy, never the cache's blob). Same blob: look at a few seal boxes found last time.</summary>
        static unsafe void CheckPart(EntityManager em, long key, Entity part)
        {
            try
            {
                if (!Ecs.HasC(em, part, tiPC)) return;
                IntPtr blob = *(IntPtr*)Ecs.RO(em, part, tiPC);
                if (blob == IntPtr.Zero || Type(blob) != TypeCompound) return;
                var c = (byte*)blob;
                int off = *(int*)(c + 24), n = *(int*)(c + 28);
                if (n <= 0 || n > 4000000 || off <= 0) return;
                byte* children = c + 24 + off;
                if (parts.TryGetValue(key, out var st) && st.blob == blob && st.gen == gen)
                {
                    bool ok = true; int seen = 0;
                    foreach (int i in st.sealChildren)
                    {
                        if (i >= n || seen++ >= 4) break;
                        byte* ch = children + 40 * i; var ent = *(Entity*)(ch + 32); byte* col = ch + 28 + *(int*)(ch + 28);
                        if (!sealOf.TryGetValue(Ecs.K(ent), out var p0) || !IsBox((IntPtr)col)) { ok = false; break; }
                        var en0 = EntryAt(p0, Center((IntPtr)col)); if (en0 == null || Soft((IntPtr)col) != en0.wantSoft) { ok = false; break; }
                    }
                    if (ok) { Count(st, children, n); return; }
                }
                if (st == null) { st = new PartState(); parts[key] = st; }
                st.blob = blob; st.gen = gen; st.sealChildren.Clear();
                for (int i = 0; i < n; i++)
                {
                    byte* ch = children + 40 * i;
                    var ent = *(Entity*)(ch + 32);
                    if (!sealOf.TryGetValue(Ecs.K(ent), out var p)) continue;
                    byte* col = ch + 28 + *(int*)(ch + 28);
                    if (!IsBox((IntPtr)col)) continue;
                    var en = EntryAt(p, Center((IntPtr)col)); if (en == null) continue;
                    st.sealChildren.Add(i);
                    if (Soft((IntPtr)col) != en.wantSoft)
                    {
                        col[HdrMaterialFlags] = en.wantSoft ? SoftFlags(col[HdrMaterialFlags]) : SolidFlags(col[HdrMaterialFlags], p.solidFlags);
                        patchedBoxes++;
                    }
                }
                Count(st, children, n);
            }
            catch (Exception ex) { if (warnings++ < 5) ModLog.Warn("rubber seal: part " + part.Index + ": " + ex.Message); }
        }

        static unsafe void Count(PartState st, byte* children, int n)
        {
            foreach (int i in st.sealChildren)
            {
                if (i >= n) continue;
                byte* ch = children + 40 * i; byte* col = ch + 28 + *(int*)(ch + 28);
                if (!Soft((IntPtr)col)) continue;
                softNow++;
                var ent = *(Entity*)(ch + 32);
                if (sealOf.TryGetValue(Ecs.K(ent), out var p)) { var en = EntryAt(p, Center((IntPtr)col)); if (en != null && en.depth >= 0.999f) lipsNow++; }
            }
        }

        // ---------------------------------------------------------------- launch log
        [HarmonyPostfix, HarmonyPatch(typeof(UIManager), nameof(UIManager.OnSpaceshipLaunch))]
        static void OnLaunch(bool start)
        {
            try { if (start) { tReport = Time.unscaledTime + 2f; fixedBuffers = 0; patchedBoxes = 0; tStep = 99f; } }
            catch (Exception) { }
        }

        static void Report()
        {
            if (tReport < 0f || Time.unscaledTime < tReport) return;
            tReport = -1f;
            string line = "rubber seal (launch): " + sealsFlight + " seal(s) in flight on " + partsWithSeals + " part(s); " + lipsNow + " lip(s) in the part colliders, all no-push"
                          + (softNow > lipsNow ? "; " + (softNow - lipsNow) + " body slab(s) soft too (solid part " + (SolidFraction * 100f).ToString("F1") + " % — CHEAT)" : "")
                          + (patchedBoxes > 0 ? " (" + patchedBoxes + " box(es) had to be set inside the part collider)" : " (born that way)") + (fixedBuffers > 0 ? "; " + fixedBuffers + " block buffer entr(ies) fixed" : "");
            if (protos.Count > 0 && !allReady)
            {
                var bad = new List<string>(); foreach (var p in protos) if (!p.ready) bad.Add(p.name + (p.problem != null ? " (" + p.problem + ")" : ""));
                ModLog.Warn(line + " — WARNING: prefab(s) not prepared, their lips are SOLID: " + string.Join(", ", bad));
            }
            else if (protos.Count == 0) ModLog.Info("rubber seal (launch): the seal blocks are not in the asset bundle");
            else if (sealsFlight > 0 && lipsNow == 0) ModLog.Warn(line + " — WARNING: no lip found in the part colliders (layout not as expected)");
            else ModLog.Info(line);
        }

        public static void Forget()
        {
            parts.Clear(); sealOf.Clear(); partsNow.Clear(); stampFlight = -1; stampGarage = -1; tStep = 99f;
            tPrefab = 99f;   // look at the prefabs again right away (a new session may have rebuilt them)
            foreach (var p in protos) { p.ready = false; p.entries = new Entry[0]; }   // 0.31.30: the cached blobs of the old session may be gone
        }
    }
}
