using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>0.30.0 (Andre: "my ship got absurdly heavy, more force for the thrusters, fans, etc."; "a slider for each type of thruster, and each
    /// level"): [Thrusters] force_multiplier scales the game's thrust for all of them and one slider per block type scales that type on top of it. Every thruster, fan and RCS block carries SCTypeThruster; its _maxForce (float3, +36) is what the thrust is computed from, and
    /// the fans also carry SCThrusterFan._baseMaxForce (float3, +76). The first value seen of each block TYPE (prefab) is kept as its base and
    /// base * multiplier is written back. 0.30.0 first kept the base per ENTITY, and the garage entities carry the value the mod wrote into the entity
    /// the game copies for the simulation, so each launch took the already multiplied value as the new base and it compounded (Andre's log: base 1e5, then 1.2e8,
    /// then 1.7e8, and 0.1 was still absurdly strong).
    /// 0.30.2 (Andre: "my ship is off its axis, almost uncontrollable with the thrusters; the RCS stops working out of nowhere"):
    ///  - a thruster mirrored with V is ANOTHER prefab (EPC_SpaceshipComponent._mirroredVersion) that has no slider of its own, so with
    ///    medium_fuel_thruster = 15.7 one side of the ship pushed 15.7x and its mirror twin 1x (the log listed 4 such prefabs only by their hash).
    ///    The mirrored version now takes the slider of its normal one.
    ///  - only the MAGNITUDE is set (|game's force| x multiplier, along the direction the game has in _maxForce right now); writing the whole
    ///    float3 every frame froze whatever direction the game gives it (the RCS gimbal, the fans).
    ///  - a multiplier within 0.5% of 1 counts as 1 (the menu slider leaves 1.0000004), so at 1 the mod does not touch the block at all. Electric power and fuel use are
    /// NOT scaled: more force costs the same.</summary>
    internal static class Thrusters
    {
        public static float Multiplier = 1f;
        /// <summary>One slider per block type (prefab name -> setting), bound in CrapMechanicsPlugin; the global one multiplies on top.</summary>
        public static readonly Dictionary<string, BepInEx.Configuration.ConfigEntry<float>> PerBlock = new Dictionary<string, BepInEx.Configuration.ConfigEntry<float>>();
        // 0.31.3: pelo nome do campo no interop (eram 36 e 76 fixos)
        static int OffMaxForce => GameFields.Of(typeof(SCTypeThruster), "_maxForce", 36);
        static int OffFanBase => GameFields.Of(typeof(SCThrusterFan), "_baseMaxForce", 76);
        struct Baseline { public float mag, fanMag; public bool fan; }
        static readonly Dictionary<ulong, Baseline> baseline = new Dictionary<ulong, Baseline>();   // per block type: |game's force|, read before the mod writes anything
        static readonly Dictionary<ulong, string> nameOf = new Dictionary<ulong, string>();
        static readonly HashSet<long> touched = new HashSet<long>();   // entities the mod has written: at 1 the rest are left alone
        static List<Entity> list = new List<Entity>();
        static TypeIndex tiThr, tiFan; static EntityQuery q; static bool ready, failed, hashed;
        static float tScan = 9f; static string lastSig = "";

        // 0.31.8 (FlightTrace): read-only access to what Step already keeps
        internal static bool Ready => ready && !failed && OffMaxForce >= 0;
        internal static List<Entity> Entities => list;
        internal static int MaxForceOffset => OffMaxForce;
        internal static TypeIndex Ti => tiThr;
        // 0.31.26 (item 31): the game's own thrust factor. SCTick_Thruster_Forces pushes with _maxForce × (_frameEfficiency ×
        // _atmoUnderwaterMultiplier01) [recon/EPC_SCThruster.cs:250 (listing line 835), 260]; that product already holds the efficiency in air,
        // water or space, the fuel coughing and overheating, and the sign of reverse. SCTypeThruster: _frameEfficiency +24,
        // _atmoUnderwaterMultiplier01 +28 [types_layout.txt:4372-4373], read by name.
        static int OffFrameEff => GameFields.Of(typeof(SCTypeThruster), "_frameEfficiency", 24);
        static int OffAtmoMul => GameFields.Of(typeof(SCTypeThruster), "_atmoUnderwaterMultiplier01", 28);
        /// <summary>0.31.26 (item 31): _frameEfficiency × _atmoUnderwaterMultiplier01 of a thruster (signed). False when it cannot be read.</summary>
        internal static unsafe bool Efficiency(EntityManager em, Entity e, out float eff)
        {
            eff = 0f;
            int a = OffFrameEff, b = OffAtmoMul;
            if (a < 0 || b < 0 || !Ecs.HasC(em, e, tiThr)) return false;
            var p = (byte*)Ecs.RO(em, e, tiThr); if (p == null) return false;
            eff = *(float*)(p + a) * *(float*)(p + b);
            return !float.IsNaN(eff) && !float.IsInfinity(eff);
        }
        public static void Forget() { list.Clear(); touched.Clear(); ready = false; failed = false; tScan = 9f; JointBoost.Forget(); }

        static float MultiplierOf(ulong h)
        {
            float m = Multiplier * (nameOf.TryGetValue(h, out var n) && PerBlock.TryGetValue(n, out var c) ? c.Value : 1f);
            return Math.Abs(m - 1f) < 0.005f ? 1f : m;
        }

        /// <summary>Prefab hash -> slider: each block's own name and the name of its mirrored version (V in the garage).</summary>
        static bool Hash()
        {
            var core = GameMeshes.CoreEntity; if (core == null) return false;
            var comps = core._spaceshipComponents; if (comps == null || comps.Length == 0) return false;
            int ok = 0; var mirrors = new List<string>();
            for (int i = 0; i < comps.Length; i++)
            {
                var epc = comps[i]; if (epc == null || string.IsNullOrEmpty(epc.name) || !PerBlock.ContainsKey(epc.name)) continue;
                ulong h = Ecs.HashPrefab(epc.name); if (h == 0) return false;
                nameOf[h] = epc.name; ok++;
                EPC_SpaceshipComponent mv = null; try { mv = epc._mirroredVersion; } catch (Exception) { }
                if (mv == null || string.IsNullOrEmpty(mv.name) || mv.name == epc.name) continue;
                ulong hm = Ecs.HashPrefab(mv.name); if (hm == 0 || nameOf.ContainsKey(hm)) continue;
                nameOf[hm] = epc.name; mirrors.Add(mv.name + " -> " + epc.name);
            }
            ModLog.Info("thrusters: " + ok + " block type(s) with a slider; mirrored versions (V) that follow the slider of the normal one: " + (mirrors.Count == 0 ? "none" : string.Join(", ", mirrors)));
            return ok > 0;
        }

        static unsafe void Scale(float* f, float mag, float m)
        {
            float cur = (float)Math.Sqrt(f[0] * f[0] + f[1] * f[1] + f[2] * f[2]);
            if (cur < 1e-6f || mag <= 0f) return;   // the game set it to zero: leave it
            float k = mag * m / cur; if (Math.Abs(k - 1f) < 1e-5f) return;
            f[0] *= k; f[1] *= k; f[2] *= k;
        }

        public static unsafe void Step(EntityManager em, float dt)
        {
            if (failed) return;
            if (!ready)
            {
                try
                {
                    tiThr = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeThruster>());
                    tiFan = TypeManager.GetTypeIndex(Il2CppType.Of<SCThrusterFan>());
                    var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiThr);
                    q = em.CreateEntityQuery(a); ready = true;
                }
                catch (Exception ex) { failed = true; ModLog.Warn("thrusters: not available (" + ex.Message + ")"); return; }
            }
            if (OffMaxForce < 0) { failed = true; return; }   // 0.31.3: campo sumiu nesta versao do jogo (GameFields ja avisou)
            if (!hashed) { try { hashed = Hash(); } catch (Exception ex) { hashed = true; ModLog.Warn("thrusters: block names not read (" + ex.Message + ") — only force_multiplier works"); } }
            tScan += dt;
            if (tScan >= 0.5f) { tScan = 0f; list = Ecs.QueryList(q); }
            int n = 0, fans = 0, scaled = 0; float maxM = 1f;   // 0.31.4: maior multiplicador em uso (JointBoost)
            foreach (var e in list)
            {
                if (!Ecs.HasC(em, e, tiThr)) continue;
                var p = (byte*)em.GetComponentDataRawRW(e, tiThr); if (p == null) continue;
                ulong h = Ecs.Prefab(em, e); var f = (float*)(p + OffMaxForce);
                bool hasFan = OffFanBase >= 0 && Ecs.HasC(em, e, tiFan);
                if (!baseline.TryGetValue(h, out var b))
                {
                    b = new Baseline { mag = (float)Math.Sqrt(f[0] * f[0] + f[1] * f[1] + f[2] * f[2]) };
                    if (hasFan) { var fp = (float*)((byte*)Ecs.RO(em, e, tiFan) + OffFanBase); b.fan = true; b.fanMag = (float)Math.Sqrt(fp[0] * fp[0] + fp[1] * fp[1] + fp[2] * fp[2]); }
                    baseline[h] = b;
                    bool named = nameOf.TryGetValue(h, out var nm);
                    var ef = (float*)(p + GameFields.Of(typeof(SCTypeThruster), "_efficiencyInSpace", 16));   // 0.31.13: by name (game 1.0.311 moved it to +32; the log read zeros). 0.31.0: _efficiencyInSpace / _efficiencyInAtmosphere / _efficiencyInWater (+16/+20/+24): how much of the force each medium gives
                    ModLog.Info("thrusters: " + (named ? nm : "prefab " + h) + " — game's force " + b.mag + (b.fan ? " (fan base " + b.fanMag + ")" : "") + "; efficiency in space " + ef[0].ToString("0.##") + ", in atmosphere " + ef[1].ToString("0.##") + ", in water " + ef[2].ToString("0.##") + (named || !hashed ? "" : " — NOT a block with a slider: only force_multiplier applies"));
                }
                float m = MultiplierOf(h); long ek = Ecs.K(e); if (m > maxM) maxM = m;
                if (m == 1f && !touched.Contains(ek)) { n++; continue; }
                if (m == 1f) touched.Remove(ek); else touched.Add(ek);   // at 1: puts the game's own force back once and lets go
                Scale(f, b.mag, m);
                if (b.fan && hasFan) { Scale((float*)((byte*)em.GetComponentDataRawRW(e, tiFan) + OffFanBase), b.fanMag, m); fans++; }
                if (m != 1f) scaled++;
                n++;
            }
            try { JointBoost.Step(em, maxM, dt); } catch (Exception ex) { ModLog.Warn("thrusters: reinforcing the game joints: " + ex.Message); }
            string sig = Multiplier + "|" + string.Join(",", Sig());
            if (sig != lastSig && n > 0) { lastSig = sig; ModLog.Info("thrusters: " + n + " thruster(s) in the world, " + scaled + " with a multiplier other than 1 (global x" + Multiplier.ToString("0.##") + "; " + fans + " with a fan)"); }
        }
        /// <summary>0.31.1 (black box): per block type in the world, how many, the force now and the multiplier.</summary>
        internal static unsafe string Summary(EntityManager em)
        {
            if (!ready || OffMaxForce < 0) return "  (not read yet)\n";
            var by = new Dictionary<ulong, (int n, float sum)>();
            foreach (var e in Ecs.QueryList(q))
            {
                if (!Ecs.HasC(em, e, tiThr)) continue; var f = (float*)((byte*)Ecs.RO(em, e, tiThr) + OffMaxForce);
                ulong h = Ecs.Prefab(em, e); by.TryGetValue(h, out var a); by[h] = (a.n + 1, a.sum + (float)Math.Sqrt(f[0] * f[0] + f[1] * f[1] + f[2] * f[2]));
            }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in by)
                sb.Append("  ").Append(nameOf.TryGetValue(kv.Key, out var nm) ? nm : "prefab " + kv.Key).Append(" x").Append(kv.Value.n).Append(": force now ").Append((kv.Value.sum / kv.Value.n).ToString("F0"))
                  .Append(" each (game's ").Append(baseline.TryGetValue(kv.Key, out var b) ? b.mag.ToString("F0") : "?").Append("), multiplier ").Append(MultiplierOf(kv.Key).ToString("0.##")).Append('\n');
            return sb.Length == 0 ? "  none\n" : sb.ToString();
        }
        static IEnumerable<string> Sig() { foreach (var kv in PerBlock) yield return kv.Value.Value.ToString("0.###"); }
    }
}
