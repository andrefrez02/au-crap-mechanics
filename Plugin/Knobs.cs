using System;
using Il2CppInterop.Runtime;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>0.19.2 (Andre: "set the bearing's maximum rotation with a knob on the bearing itself"; "if we added knobs to the pistons
    /// we could limit the level without logic"): the game's knob (EPC_Actionable_Knob, continuous, ActionableData._value.x 0..1) on the +Z panel:
    /// bearing: servo angle = 10 + 350 x value degrees (default 0.1 = 45); piston: stroke = (10 + 90 x value) % of the stroke (default 1 = 100 %).
    /// The pointer hint (ActionableTargetDrawCustomUIValue, FixedString64Bytes) shows the value, like the monitor's knob.</summary>
    internal static class Knobs
    {
        static TypeIndex tiData, tiHint, tiKnobC, tiToggle; static bool ok, failed;
        static bool Prepare()
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiData = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableData>());
                tiHint = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableTargetDrawCustomUIValue>());
                // 0.23.0: the bearing now has TWO actionables (knob + switch of the angle output) — both have ActionableData,
                // so the knob is now found by ActionableKnob and the switch by ActionableToggle.
                try { tiKnobC = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableKnob>()); } catch (Exception ex) { ModLog.Warn("knobs: ActionableKnob: " + ex.Message); }
                try { tiToggle = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableToggle>()); } catch (Exception ex) { ModLog.Warn("knobs: ActionableToggle: " + ex.Message); }
                BeaconIcon.SkipToggle = tiToggle;
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("knobs: " + ex.Message); return false; }
        }

        /// <summary>Child of the block with ActionableData (the knob). Entity.Null if the block has none (old prefab).</summary>
        public static Entity Find(EntityManager em, Entity block)
        {
            if (!Prepare()) return Entity.Null;
            if (tiKnobC.Value != 0) foreach (var f in Ecs.Children(em, block)) if (Ecs.HasC(em, f, tiKnobC)) return f;
            foreach (var f in Ecs.Children(em, block)) if (Ecs.HasC(em, f, tiData) && !(tiToggle.Value != 0 && Ecs.HasC(em, f, tiToggle))) return f;
            return BeaconIcon.FindKnobOf(em, block);   // 0.19.3: in the garage the knob isn't in the block's Child (only in BelongingSC)
        }
        /// <summary>0.23.0: the switch of the angle output (child of the block with ActionableToggle). Entity.Null if the block has none.</summary>
        public static Entity FindSwitch(EntityManager em, Entity block)
        {
            if (!Prepare() || tiToggle.Value == 0) return Entity.Null;
            foreach (var f in Ecs.Children(em, block)) if (Ecs.HasC(em, f, tiToggle)) return f;
            foreach (var f in BeaconIcon.ActionablesOf(em, block)) if (Ecs.HasC(em, f, tiToggle)) return f;
            return Entity.Null;
        }
        /// <summary>0.23.0: true = switch on (degrees); false = off (-1..1). Without a switch (old prefab) returns the default.</summary>
        public static unsafe bool SwitchOn(EntityManager em, Entity sw, bool defaultValue = true)
        {
            if (!ok || !Ecs.Alive(em, sw) || !Ecs.HasC(em, sw, tiData)) return defaultValue;
            var p = (float*)em.GetComponentDataRawRO(sw, tiData); if (p == null) return defaultValue;
            return !(p[0] < 0.5f);
        }
        /// <summary>0..1 value of the knob (NaN if it can't be read).</summary>
        public static unsafe float ReadValue(EntityManager em, Entity knob)
        {
            if (!ok || !Ecs.Alive(em, knob) || !Ecs.HasC(em, knob, tiData)) return float.NaN;
            var p = (float*)em.GetComponentDataRawRO(knob, tiData); if (p == null) return float.NaN;
            float v = p[0]; return float.IsNaN(v) ? float.NaN : Math.Max(0f, Math.Min(1f, v));
        }
        /// <summary>Text of the pointer hint (up to 60 UTF-8 bytes).</summary>
        public static unsafe void Hint(EntityManager em, Entity knob, string txt)
        {
            if (!ok || !Ecs.Alive(em, knob) || !Ecs.HasC(em, knob, tiHint)) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(txt); int n = Math.Min(bytes.Length, 60);
            var p = (byte*)em.GetComponentDataRawRW(knob, tiHint); if (p == null) return;
            *(ushort*)p = (ushort)n; for (int i = 0; i < n; i++) p[2 + i] = bytes[i]; p[2 + n] = 0;
        }

        public static float ServoDegreesOf(float v) => (float)Math.Round(10f + 350f * v);
        /// <summary>0.20.0: " (1.50 m)" = pct of the stroke up to the Piston End; empty without an end.</summary>
        static string Meters(int pct, float strokeM) => strokeM > 0.0005f ? " (" + (strokeM * pct / 100f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " m)" : "";
        public static float StrokeFraction(float v) => (float)Math.Round(10f + 90f * v) / 100f;

        // ---------------------------------------------------------------- 0.25.0 (Andre: "three knobs on the turret seat, so I can set
        // its maximum angle in each dimension ... front part of the base"; "it applies in whole-ship mode too"): on the base's +Z face,
        // read left to right (= decreasing block X): yaw limit (0..180, 180 = free), pitch up (0..135) and
        // pitch down (0..135). The three are identical knobs; which is which comes from the X position. Value stored by SCGuid + suffix.
        public const float TurretYawMaxDeg = 180f, TurretPitchMaxDeg = 135f;
        static readonly string[] TurretSuffix = { "yaw", "up", "down" };
        public static int TurretDegrees(int i, float v) => (int)Math.Round((i == 0 ? TurretYawMaxDeg : TurretPitchMaxDeg) * v);
        static string TurretHint(int i, int g)
        {
            if (i == 0) return g >= TurretYawMaxDeg ? Text("SC_TurretSeat_KnobYawFree", "Turn: free (full circle)") : Text("SC_TurretSeat_KnobYaw", "Turn limit: ±{0}°").Replace("{0}", g.ToString());
            return (i == 1 ? Text("SC_TurretSeat_KnobUp", "Aim up limit: {0}°") : Text("SC_TurretSeat_KnobDown", "Aim down limit: {0}°")).Replace("{0}", g.ToString());
        }
        static readonly System.Collections.Generic.Dictionary<long, (Entity block, int i)> turretKnobs = new System.Collections.Generic.Dictionary<long, (Entity, int)>();
        static readonly System.Collections.Generic.Dictionary<long, int[]> turretLimits = new System.Collections.Generic.Dictionary<long, int[]>();
        static readonly System.Collections.Generic.HashSet<long> turretLogged = new System.Collections.Generic.HashSet<long>();
        /// <summary>0.25.0: turret seat limits (degrees) read from the 3 knobs. false = knobs not found yet (the caller's default stays).</summary>
        public static bool TurretLimits(Entity block, ref float yaw, ref float top, ref float low)
        {
            if (!turretLimits.TryGetValue(Ecs.K(block), out var l)) return false;
            if (l[0] >= 0) yaw = l[0]; if (l[1] >= 0) top = l[1]; if (l[2] >= 0) low = l[2];
            return true;
        }

        /// <summary>Per joint, every frame (cheap): finds the knob every 3 s if it doesn't have one; reads the value; updates the hint when it changes.</summary>
        public static void Step(EntityManager em, Joint j, float dt)
        {
            if (!Prepare()) return;
            j.tKnob += dt;
            // 0.19.3 (Andre: "the tooltip only shows up after I start the simulation"): like the beacon — looks for the knob again every 3 s
            // (in the garage it may switch entities without the old one dying) and rewrites the hint when the entity changes; logs once when found
            if (!Ecs.Alive(em, j.knob) || j.tKnob >= 3f)
            {
                if (j.tKnob < 3f && !Ecs.Alive(em, j.knob)) { if (j.tKnob < 0.5f) return; }
                j.tKnob = 0f; var k = Find(em, j.block);
                if (Ecs.IsNull(k)) { j.knob = k; return; }
                if (k.Index != j.knob.Index || k.Version != j.knob.Version)
                {
                    j.knob = k; j.knobHint = int.MinValue;
                    if (DetailedLogKnob) ModLog.Info(j.name + " " + j.block.Index + ": knob = entity " + k.Index + (Ecs.HasC(em, k, tiHint) ? " (tooltip ok)" : " (NO tooltip component — rebuild the bundle in the Modkit)"));
                }
            }
            if (Ecs.IsNull(j.knob)) return;
            float v = ReadValue(em, j.knob); if (float.IsNaN(v)) return;
            v = Persist(em, j.knob, j.block, v);   // 0.19.15: value stored by SCGuid (the game doesn't save a generic block's knob in the BP)
            if (j.kind == Kind.Bearing)
            {
                int g = (int)ServoDegreesOf(v); j.servoDegrees = g;
                if (g != j.knobHint) { j.knobHint = g; Hint(em, j.knob, Text(j.spin ? "SC_GyroBearing_KnobServo" : "SC_Bearing_KnobServo", j.spin ? "Offset range: {0}°" : "Servo: {0}°").Replace("{0}", g.ToString())); if (DetailedLogKnob) ModLog.Info(j.name + " " + j.block.Index + ": servo knob " + g + " degrees"); }
            }
            else
            {
                int pct = (int)Math.Round(StrokeFraction(v) * 100f); j.strokeFraction = pct / 100f;
                // 0.20.0: with a Piston End the stroke is the distance to it: the hint shows the meters (key = pct x 1000 + meters in cm, to rewrite when it changes)
                float strokeM = j.withEnd ? j.stroke : Garage.ExtOf(j.block); if (strokeM <= 0.0005f) strokeM = j.strokeMax;   // 0.22.5 (Andre: "show it in % and in meters even without the piston end")
                int key = pct * 100000 + (int)Math.Round(strokeM * 100f);
                if (key != j.knobHint) { j.knobHint = key; Hint(em, j.knob, Text("SC_Piston_KnobStroke", "Stroke: {0}%").Replace("{0}", pct.ToString()) + Meters(pct, strokeM)); if (DetailedLogKnob) ModLog.Info(j.name + " " + j.block.Index + ": stroke knob " + pct + " %" + Meters(pct, strokeM)); }
            }
        }
        public static bool DetailedLogKnob = true;

        // ---------------------------------------------------------------- 0.19.15 (Andre: "the knobs only update the tooltip during the
        // simulation" / "the knob value isn't being kept in the bp"). (1) In the garage the blocks don't become Joints (they aren't in a part), so
        // the knob was never read: GarageStep scans every actionable with BelongingSC and handles the ones belonging to a block of ours with a knob.
        // (2) The game only saves in the blueprint the generic EPC's BlueprintData (guid, pose, colour): the knob goes back to the default on every
        // garage/launch. We keep the value by SCGuid in BepInEx/config/CrapMechanics/knobs.txt and write it back into the
        // ActionableData when the knob (re)appears — works in the garage, at launch and on time rewind.
        static readonly System.Collections.Generic.Dictionary<string, float> saved = new System.Collections.Generic.Dictionary<string, float>();
        // 0.24.0: keeps the SCGuid along. It was a HashSet never cleared: an entity's index/version is reused between garage and
        // launch, so a NEW knob could land on an old key, skip the restore and write the default over the saved value.
        static readonly System.Collections.Generic.Dictionary<long, string> applied = new System.Collections.Generic.Dictionary<long, string>();   // knob entity -> guid already checked
        static readonly System.Collections.Generic.Dictionary<long, int> garageHint = new System.Collections.Generic.Dictionary<long, int>();
        static bool loaded, dirty; static float tStore; static string filePath;
        static string FilePath()
        {
            if (filePath == null) { var folder = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, PluginInfo.PluginName); System.IO.Directory.CreateDirectory(folder); filePath = System.IO.Path.Combine(folder, "knobs.txt"); }
            return filePath;
        }
        static void LoadFile()
        {
            if (loaded) return; loaded = true;
            try
            {
                if (!System.IO.File.Exists(FilePath())) return;
                foreach (var l in System.IO.File.ReadAllLines(FilePath()))
                {
                    var p = l.Trim().Split(' '); if (p.Length != 2) continue;
                    if (float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) saved[p[0]] = v;
                }
                ModLog.Info("knobs: " + saved.Count + " saved knob value(s) loaded from " + FilePath());
            }
            catch (Exception ex) { ModLog.Warn("knobs: could not read " + FilePath() + ": " + ex.Message); }
        }
        /// <summary>Writes the file at most once every 2 s when something changed.</summary>
        public static void Store(float dt)
        {
            if (!dirty) return; tStore += dt; if (tStore < 2f) return; tStore = 0f; dirty = false;
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in saved) sb.Append(kv.Key).Append(' ').Append(kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                System.IO.File.WriteAllText(FilePath(), sb.ToString());
            }
            catch (Exception ex) { ModLog.Warn("knobs: could not write " + FilePath() + ": " + ex.Message); }
        }
        /// <summary>The 1st time a knob (entity) is seen: if there's a stored value for the block's SCGuid, writes it into ActionableData and
        /// returns that value; after that, every change by the player goes to the file. Returns the value in effect.</summary>
        public static unsafe float Persist(EntityManager em, Entity knob, Entity block, float v, string suffix = null)
        {
            // 0.28.0: on a client the server's knob values rule. The game sends every actionable whose value changed on a client to the server
            // (NetcoreSendActionableValues), so a restore here would overwrite the host's knobs with this machine's saved ones.
            if (!Multi.Authoritative(em)) return v;
            LoadFile();
            string guid; try { guid = Ecs.Guid(em, block); } catch (Exception) { guid = ""; }
            if (string.IsNullOrEmpty(guid)) return v;
            if (suffix != null) guid += "|" + suffix;   // 0.23.1: the angle output switch has its own key
            long k = Ecs.K(knob);
            // 0.24.0 (Andre: "this update reset the bearings' angle and the pistons' size"): the file is only written
            // when the change came from the player. Before, a single pass in which the restore didn't happen (reused entity,
            // or ActionableData not writable yet) marked the knob as checked and the next call wrote the DEFAULT over
            // the saved value — permanent loss. Now it only marks it checked when the restore really took effect.
            if (!applied.TryGetValue(k, out var guidConf) || guidConf != guid)
            {
                if (saved.TryGetValue(guid, out var sv))
                {
                    if (Math.Abs(sv - v) <= 0.0005f) { applied[k] = guid; return v; }   // already at the saved value
                    float* p = null; try { p = (float*)em.GetComponentDataRawRW(knob, tiData); } catch (Exception) { }
                    if (p == null) return v;   // can't write yet: try again on the next step, without saving anything
                    p[0] = sv; applied[k] = guid;
                    if (DetailedLogKnob) ModLog.Info("knobs: block " + block.Index + " (" + guid.Substring(0, 8) + "…): knob restored to " + sv.ToString("F3") + " (was " + v.ToString("F3") + ")");
                    return sv;
                }
                saved[guid] = v; dirty = true; applied[k] = guid;   // block seen for the 1st time: seeds the file
                return v;
            }
            if (!saved.TryGetValue(guid, out var current) || Math.Abs(current - v) > 0.0005f) { saved[guid] = v; dirty = true; }
            return v;
        }
        /// <summary>Every 0.5 s: knobs of our blocks that are NOT in a Joint (garage): tooltip + persistence.</summary>
        // 0.20.1: the scan of ALL actionables (BlockOfActionable + SCPrefab of each) only every 1 s; every 0.1 s it reads only the knobs
        // already known to belong to a block of ours (the 0.1 s step on a big ship scanned hundreds of the game's buttons)
        static readonly System.Collections.Generic.Dictionary<long, (Entity block, bool bearing, bool spin, float stroke)> knobsMod = new System.Collections.Generic.Dictionary<long, (Entity, bool, bool, float)>();
        static readonly System.Collections.Generic.Dictionary<long, Entity> switchesMod = new System.Collections.Generic.Dictionary<long, Entity>();   // 0.23.1: angle output switch
        static float tMap = 9f, tLast = -1f;
        public static unsafe void GarageStep(EntityManager em)
        {
            if (!Prepare()) return;
            float now = UnityEngine.Time.unscaledTime; tMap += tLast < 0f ? 9f : now - tLast; tLast = now;
            if (tMap >= 1f)
            {
                tMap = 0f; knobsMod.Clear(); switchesMod.Clear(); turretKnobs.Clear(); turretTmp.Clear(); turretBlock.Clear();
                ModLog.Trace("knobs: listing the world's actionables");
                Entity[] knobs; try { knobs = BeaconIcon.AllActionables(em); } catch (Exception) { return; }
                ModLog.Trace("knobs: " + knobs.Length + " actionable(s) to look at");
                foreach (var kn in knobs)
                {
                    if (ModLog.Tracing) ModLog.Trace("knobs: actionable " + kn.Index + ":" + kn.Version + " — owner?");
                    var block = BeaconIcon.BlockOfActionable(em, kn); if (Ecs.IsNull(block) || !Ecs.Alive(em, block)) continue;
                    if (ModLog.Tracing) ModLog.Trace("knobs: actionable " + kn.Index + " owner " + block.Index + " — prefab?");
                    ulong h; try { h = Ecs.Prefab(em, block); } catch (Exception) { continue; }
                    if (ModLog.Tracing) ModLog.Trace("knobs: actionable " + kn.Index + " prefab ok");
                    if (tiKnobC.Value != 0 && Ecs.HasC(em, kn, tiKnobC) && JointSystem.KindFromHash(h, out var tpT, out _, out _) && tpT == Kind.TurretSeat)
                    {
                        float x = 0f; try { Ecs.Axes(em, block, out var ex, out _, out _); x = Ecs.Dot(Ecs.Sub(Ecs.Position(em, kn), Ecs.Position(em, block)), ex); } catch (Exception) { continue; }
                        long kb = Ecs.K(block); if (!turretTmp.TryGetValue(kb, out var lt)) turretTmp[kb] = lt = new System.Collections.Generic.List<(Entity, float)>();
                        lt.Add((kn, x)); turretBlock[kb] = block; continue;
                    }
                    if (!JointSystem.BlockWithKnob(h, out _, out var bearing, out var spin, out var strokeP)) continue;
                    // 0.23.1 (Andre: "the bearing's switch has the wrong text"): the bearing has TWO actionables — the angle output
                    // switch landed here and took the knob's hint and stored value. Now it goes to its own list.
                    if (tiToggle.Value != 0 && Ecs.HasC(em, kn, tiToggle)) { switchesMod[Ecs.K(kn)] = block; continue; }
                    knobsMod[Ecs.K(kn)] = (block, bearing, spin, strokeP);
                }
            }
            if (turretTmp.Count > 0) TurretMap();
            ModLog.Trace("knobs: map ready (" + knobsMod.Count + " knob(s), " + switchesMod.Count + " switch(es))");
            foreach (var kv in knobsMod)
            {
                var kn = Ecs.OfKey(kv.Key); var block = kv.Value.block; bool bearing = kv.Value.bearing, spin = kv.Value.spin; float strokeMaxP = kv.Value.stroke;
                if (!Ecs.Alive(em, kn) || !Ecs.Alive(em, block)) continue;
                if (JointSystem.HasJoint(block)) continue;   // in the simulation the Joint takes care of it
                float v = ReadValue(em, kn); if (float.IsNaN(v)) continue;
                v = Persist(em, kn, block, v);
                long k = Ecs.K(kn); garageHint.TryGetValue(k, out var before);
                if (bearing)
                {
                    int g = (int)ServoDegreesOf(v);
                    if (g != before) { garageHint[k] = g; Hint(em, kn, Text(spin ? "SC_GyroBearing_KnobServo" : "SC_Bearing_KnobServo", spin ? "Offset range: {0}°" : "Servo: {0}°").Replace("{0}", g.ToString())); }
                }
                else
                {
                    int pct = (int)Math.Round(StrokeFraction(v) * 100f); float strokeM = Garage.ExtOf(block); if (strokeM <= 0.0005f) strokeM = strokeMaxP;   // 0.22.5: no Piston End = the piston's max stroke
                    int key = pct * 100000 + (int)Math.Round(strokeM * 100f);
                    if (key != before) { garageHint[k] = key; Hint(em, kn, Text("SC_Piston_KnobStroke", "Stroke: {0}%").Replace("{0}", pct.ToString()) + Meters(pct, strokeM)); }
                }
            }
            foreach (var kv in turretKnobs)
            {
                var kn = Ecs.OfKey(kv.Key); var block = kv.Value.block; int i = kv.Value.i;
                if (!Ecs.Alive(em, kn) || !Ecs.Alive(em, block)) continue;
                float v = ReadValue(em, kn); if (float.IsNaN(v)) continue;
                v = Persist(em, kn, block, v, TurretSuffix[i]);
                int g = TurretDegrees(i, v); long kb = Ecs.K(block);
                if (!turretLimits.TryGetValue(kb, out var l)) turretLimits[kb] = l = new[] { -1, -1, -1 };
                if (l[i] != g) { if (DetailedLogKnob && l[i] >= 0) ModLog.Info("turret seat " + block.Index + ": " + TurretSuffix[i] + " limit " + l[i] + " -> " + g + " degrees"); l[i] = g; }
                long k = Ecs.K(kn); garageHint.TryGetValue(k, out var before);
                if (before != g + 1000) { garageHint[k] = g + 1000; Hint(em, kn, TurretHint(i, g)); }
            }
            // 0.23.1: angle output switch — its own hint and a value stored per block (the game doesn't save a generic block's actionable)
            ModLog.Trace("knobs: knobs done");
            foreach (var kv in switchesMod)
            {
                var sw = Ecs.OfKey(kv.Key); var block = kv.Value;
                if (!Ecs.Alive(em, sw) || !Ecs.Alive(em, block)) continue;
                float v = ReadValue(em, sw); if (float.IsNaN(v)) continue;
                v = Persist(em, sw, block, v, "sw");
                bool degrees = !(v < 0.5f); long k = Ecs.K(sw); int wants = degrees ? 1 : 2;
                if (!garageHint.TryGetValue(k, out var before) || before != wants)
                { garageHint[k] = wants; Hint(em, sw, Text(degrees ? "SC_Bearing_SwitchDegrees" : "SC_Bearing_SwitchNorm", degrees ? "Angle out: degrees" : "Angle out: -1..1")); }
            }
            ModLog.Trace("knobs: switches done");
            if (garageHint.Count > 4096) garageHint.Clear();
        }
        static readonly System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<(Entity kn, float x)>> turretTmp = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<(Entity, float)>>();
        static readonly System.Collections.Generic.Dictionary<long, Entity> turretBlock = new System.Collections.Generic.Dictionary<long, Entity>();
        /// <summary>0.25.0: the knobs of each turret seat, in decreasing X order = yaw, pitch up, pitch down.</summary>
        static void TurretMap()
        {
            foreach (var kv in turretTmp)
            {
                var list = kv.Value; var block = turretBlock[kv.Key];
                if (list.Count < 3)
                {
                    if (turretLogged.Add(kv.Key)) ModLog.Warn("turret seat " + block.Index + ": " + list.Count + " knob(s), expected 3 — rebuild the bundle in the Modkit (the limits stay at yaw free / 135 up / 30 down)");
                    continue;
                }
                list.Sort((a, b) => b.x.CompareTo(a.x));
                for (int i = 0; i < 3; i++) turretKnobs[Ecs.K(list[i].kn)] = (block, i);
                if (turretLogged.Add(kv.Key)) ModLog.Info("turret seat " + block.Index + ": limit knobs = entities " + list[0].kn.Index + " (turn), " + list[1].kn.Index + " (up), " + list[2].kn.Index + " (down); x " + list[0].x.ToString("F2") + " / " + list[1].x.ToString("F2") + " / " + list[2].x.ToString("F2"));
            }
        }
        public static void Forget() { applied.Clear(); garageHint.Clear(); knobsMod.Clear(); switchesMod.Clear(); turretKnobs.Clear(); turretLogged.Clear(); tMap = 9f; }
        static string Text(string key, string defaultValue)
        {
            try { var loc = Localization._cachedInstance; if (loc != null && loc._map != null && loc._map.ContainsKey(key)) return loc._map[key]; } catch (Exception) { }
            return defaultValue;
        }
    }
}
