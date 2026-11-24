using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>0.31.12 (Andre: "the autopilot spins the ship and I'm too lazy to put Datameters everywhere"): one key ([Debug]
    /// circuit_dump_key, Home by default; Mod Menu shows it) writes the ship's whole logic, read from the game itself, to
    /// logs/circuit-HHmmss.txt: every block with data ports, each port with its name (the game's own text), its live value and where it
    /// comes from (the block and port that feed it, through cables, Frame With Ports and wireless channels), the outputs with who reads
    /// them, and the settings of the blocks that have them (Remapper ranges, Memory/Accumulator mode, Condition comparison, channels).
    /// It is a Datameter on every port at once. Read only: nothing in the game is changed. Use it in the simulation (the ports exist there).
    /// At the end of the file, the hash of every block name, so the .bp files can be read with real names.</summary>
    internal static class CircuitDump
    {
        static int vk = 0x24; static string keyName = null; static bool before; static int count;
        static TypeIndex tiMem, tiAcc, tiCond, tiRemap, tiLabelF, tiAData; static bool typesOk, typesTried;
        static readonly Dictionary<ulong, string> names = new Dictionary<ulong, string>();

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        static void RefreshKey()
        {
            string k = KeybindsLink.Key(Marker.ModName, "circuit_dump", CrapMechanicsPlugin.CircuitDumpKey != null ? CrapMechanicsPlugin.CircuitDumpKey.Value : "Home");
            if (k == keyName) return; keyName = k;
            if (string.IsNullOrWhiteSpace(k)) { vk = 0; return; }
            if (Enum.TryParse<UnityEngine.InputSystem.Key>(k.Trim(), true, out var key) && InputKeys.VKOf(key) > 0) vk = InputKeys.VKOf(key);
            else ModLog.Warn("circuit dump: key '" + k + "' can't be used; keeping the previous one");
        }

        public static void Step(EntityManager em)
        {
            RefreshKey();
            bool down = vk > 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;
            bool pressed = down && !before; before = down;
            if (!pressed) return;
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid != (uint)Environment.ProcessId) return;   // the key in another program
            // 0.31.15: the dump walks every cable itself (no kept paths): it must show the circuit as it is this instant
            bool kept = Ecs.PathCache; Ecs.PathCache = false;
            try { Dump(em); } catch (Exception ex) { ModLog.Warn("circuit dump: " + ex); }
            finally { Ecs.PathCache = kept; }
        }

        static void Types()
        {
            if (typesTried) return; typesTried = true;
            try
            {
                tiMem = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeMemory>()); tiAcc = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeAccumulator>());
                tiCond = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeCondition>()); tiRemap = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeRemapper>());
                tiLabelF = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableLabelAsFloat>()); tiAData = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableData>());
                typesOk = true;
            }
            catch (Exception ex) { ModLog.Warn("circuit dump: block settings not available (" + ex.Message + ")"); }
        }

        static void Names()
        {
            if (names.Count > 0 || !BlockIndex.Ready) return;
            foreach (var p in BlockIndex.Prefabs())
            {
                ulong h = Ecs.HashPrefab(p); if (h != 0 && !names.ContainsKey(h)) names[h] = p;
                ulong hm = Ecs.HashPrefab(p + " M"); if (hm != 0 && hm != h && !names.ContainsKey(hm)) names[hm] = p + " (mirrored)";
            }
        }

        static Il2CppSystem.Collections.Generic.Dictionary<string, string> Loc()
        {
            try { var loc = Localization._cachedInstance; return loc == null ? null : loc._map; } catch (Exception) { return null; }
        }
        static string Text(Il2CppSystem.Collections.Generic.Dictionary<string, string> map, string key)
        {
            try { if (map != null && map.ContainsKey(key)) { var s = map[key] ?? ""; int i = s.IndexOf('\n'); if (i >= 0) s = s.Substring(0, i); return System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "").Trim(); } } catch (Exception) { }
            return null;
        }
        static string Prefab(string n) { if (n == null) return null; int i = n.IndexOf(" (mirrored)"); return i >= 0 ? n.Substring(0, i) : n; }

        static unsafe float LabelF(EntityManager em, Entity e)
        {
            if (!Ecs.HasC(em, e, tiLabelF)) return float.NaN; var b = (byte*)Ecs.RO(em, e, tiLabelF); return b == null ? float.NaN : *(float*)b;
        }
        static unsafe float AData(EntityManager em, Entity e)
        {
            if (!Ecs.HasC(em, e, tiAData)) return float.NaN; var b = (byte*)Ecs.RO(em, e, tiAData); return b == null ? float.NaN : *(float*)b;
        }
        static string F(float v) => float.IsNaN(v) ? "?" : v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Settings of the blocks that have them, read from the game.</summary>
        static unsafe string Settings(EntityManager em, Entity block)
        {
            if (!typesOk) return "";
            try
            {
                if (Ecs.HasC(em, block, tiRemap))
                {
                    var b = (byte*)Ecs.RO(em, block, tiRemap); if (b == null) return "";
                    var v = new float[4]; for (int i = 0; i < 4; i++) v[i] = LabelF(em, *(Entity*)(b + 8 * i));
                    return "labels in order 0..3: " + F(v[0]) + " | " + F(v[1]) + " | " + F(v[2]) + " | " + F(v[3]);
                }
                if (Ecs.HasC(em, block, tiMem) || Ecs.HasC(em, block, tiAcc))
                {
                    var ti = Ecs.HasC(em, block, tiMem) ? tiMem : tiAcc; var b = (byte*)Ecs.RO(em, block, ti); if (b == null) return "";
                    return "mode toggle = " + F(AData(em, *(Entity*)b)) + " (last set/reset input " + F(*(float*)(b + 8)) + ")";
                }
                if (Ecs.HasC(em, block, tiCond))
                {
                    var b = (byte*)Ecs.RO(em, block, tiCond); if (b == null) return "";
                    return "comparison knob = " + F(AData(em, *(Entity*)(b + 8)));
                }
            }
            catch (Exception) { }
            return "";
        }

        sealed class B { public Entity e; public int n; public string name, prefab; public Entity[] ports; public Entity part; public Unity.Mathematics.float3 pos; }

        static void Dump(EntityManager em)
        {
            Types(); Names(); var map = Loc();
            var blocks = new List<B>(); var byKey = new Dictionary<long, B>();
            foreach (var e in Ecs.Components(em))
            {
                if (Ecs.IsCable(em, e) || Ecs.HasC(em, e, Ecs.tiFramePorts)) continue;
                var ports = Ecs.Ports(em, e); if (ports.Length == 0) continue;
                bool data = false;
                foreach (var p in ports) { byte t = Ecs.EffectiveType(em, p); if (t == Ecs.PortInput || t == Ecs.PortOutput || t == 3 || t == 5) { data = true; break; } }
                if (!data) continue;
                ulong h = Ecs.Prefab(em, e); names.TryGetValue(h, out var nm);
                var b = new B { e = e, ports = ports, name = nm ?? ("?" + h.ToString("x16")), prefab = Prefab(nm), part = Ecs.Parent(em, e) };
                try { b.pos = Ecs.LT(em, e).Position; } catch (Exception) { }
                blocks.Add(b);
            }
            blocks.Sort((a, c) => { int k = a.part.Index.CompareTo(c.part.Index); if (k != 0) return k; k = a.pos.x.CompareTo(c.pos.x); if (k != 0) return k; k = a.pos.z.CompareTo(c.pos.z); return k != 0 ? k : a.pos.y.CompareTo(c.pos.y); });
            for (int i = 0; i < blocks.Count; i++) { blocks[i].n = i + 1; byKey[Ecs.K(blocks[i].e)] = blocks[i]; }

            // who feeds each input; who reads each output
            var portOwner = new Dictionary<long, (B b, int i)>();
            foreach (var b in blocks) for (int i = 0; i < b.ports.Length; i++) portOwner[Ecs.K(b.ports[i])] = (b, i);
            var readers = new Dictionary<long, List<string>>();
            var feeds = new Dictionary<long, (string desc, float val, string trail)>();
            string PortName(B b, int i) { string t = b.prefab != null ? Text(map, b.prefab + "_Port" + i) : null; return t == null ? "port " + i : "port " + i + " \"" + t + "\""; }
            string Ref(Entity port)
            {
                if (Ecs.IsNull(port)) return "nothing";
                if (portOwner.TryGetValue(Ecs.K(port), out var o)) { int ch = Ecs.Channel(em, o.b.e); return "#" + o.b.n + " " + o.b.name + (ch >= 0 ? " ch" + ch : "") + " port " + o.i; }
                var owner = Ecs.Parent(em, port);
                if (!Ecs.IsNull(owner) && byKey.TryGetValue(Ecs.K(owner), out var ob)) return "#" + ob.n + " " + ob.name + " (port " + Ecs.PortIndex(em, port) + ")";
                ulong h = Ecs.IsNull(owner) ? 0 : Ecs.Prefab(em, owner); names.TryGetValue(h, out var nm);
                return "entity " + port.Index + (nm != null ? " on " + nm : "");
            }
            foreach (var b in blocks)
                for (int i = 0; i < b.ports.Length; i++)
                {
                    var p = b.ports[i]; byte t = Ecs.EffectiveType(em, p);
                    if (t != Ecs.PortInput && t != 3 && t != 5) continue;
                    Ecs.ReadInput(em, p, out float v, out Entity src, out string trail);
                    if (Ecs.IsNull(src) || (src.Index == p.Index && src.Version == p.Version)) continue;
                    feeds[Ecs.K(p)] = (Ref(src), v, trail);
                    if (!readers.TryGetValue(Ecs.K(src), out var l)) readers[Ecs.K(src)] = l = new List<string>();
                    l.Add("#" + b.n + " " + b.name + " port " + i);
                }

            count++;
            var sb = new StringBuilder();
            bool garage = false; try { garage = Ecs.QSing.CalculateEntityCountWithoutFiltering() == 0; } catch (Exception) { }
            sb.Append("Crap Mechanics ").Append(PluginInfo.PluginVersion).Append(" — circuit dump #").Append(count).Append(" at ").Append(DateTime.Now.ToString("HH:mm:ss.fff"))
              .Append(garage ? " (GARAGE: ports may not be live — use it in the simulation)" : " (simulation)").Append("\n")
              .Append(blocks.Count).Append(" block(s) with data ports. Position = the block's LocalTransform in its part, in metres (8 cells = 1 m).\n")
              .Append("Port types: in = input, out = output, in/out = pass-through or wireless, the value is the one the port holds now.\n\n");
            foreach (var b in blocks)
            {
                int ch = Ecs.Channel(em, b.e);
                sb.Append("#").Append(b.n).Append(" ").Append(b.name);
                if (ch >= 0) sb.Append("  CHANNEL ").Append(ch);
                sb.Append("   [part ").Append(b.part.Index).Append(" at ").Append(F(b.pos.x)).Append(", ").Append(F(b.pos.y)).Append(", ").Append(F(b.pos.z)).Append(" m; entity ").Append(b.e.Index).Append("]\n");
                string set = Settings(em, b.e); if (set.Length > 0) sb.Append("    settings: ").Append(set).Append("\n");
                for (int i = 0; i < b.ports.Length; i++)
                {
                    var p = b.ports[i]; byte t = Ecs.EffectiveType(em, p);
                    if (t == Ecs.PortPower) continue;
                    Ecs.Port(em, p, out _, out float own, out _);
                    string kind = t == Ecs.PortInput ? "in " : t == Ecs.PortOutput ? "out" : t == 3 ? "in/out" : t == 5 ? "unset" : "t" + t;
                    sb.Append("    ").Append(PortName(b, i)).Append("  ").Append(kind).Append(" = ").Append(F(own));
                    if (feeds.TryGetValue(Ecs.K(p), out var fd)) sb.Append("  <- ").Append(fd.desc).Append(" (reads ").Append(F(fd.val)).Append(")");
                    else if (t == Ecs.PortInput) sb.Append("  <- NOTHING CONNECTED");
                    if (readers.TryGetValue(Ecs.K(p), out var rl)) sb.Append("  -> ").Append(string.Join(", ", rl));
                    else if (t == Ecs.PortOutput && ch < 0) sb.Append("  -> nobody reads it");
                    sb.Append("\n");
                }
            }
            // wireless summary
            var chans = new SortedDictionary<int, List<string>>();
            foreach (var b in blocks) { int ch = Ecs.Channel(em, b.e); if (ch < 0) continue; if (!chans.TryGetValue(ch, out var l)) chans[ch] = l = new List<string>();
                bool sends = false; foreach (var p in b.ports) if (feeds.ContainsKey(Ecs.K(p))) sends = true;
                l.Add("#" + b.n + (sends ? " SENDS" : " receives")); }
            sb.Append("\nWireless channels (a transmitter whose port is fed by an output SENDS; more than one sender on a channel is a conflict):\n");
            foreach (var kv in chans) { int s = 0; foreach (var x in kv.Value) if (x.EndsWith("SENDS")) s++; sb.Append("  ch").Append(kv.Key).Append(": ").Append(string.Join(", ", kv.Value)).Append(s > 1 ? "   <-- " + s + " SENDERS" : s == 0 ? "   <-- nobody sends" : "").Append("\n"); }
            if (count == 1)
            {
                sb.Append("\nBlock name hashes (SCPrefab._prefab), for reading .bp files:\n");
                var rows = new List<string>(); foreach (var kv in names) rows.Add(kv.Key.ToString("x16") + "\t" + kv.Value); rows.Sort(StringComparer.Ordinal);
                foreach (var r in rows) sb.Append(r).Append("\n");
            }
            string folder = Path.Combine(Marker.LogsFolder ?? ".", "circuits"); try { Directory.CreateDirectory(folder); } catch (Exception) { }
            string file = Path.Combine(folder, "circuit-" + DateTime.Now.ToString("HHmmss") + ".txt");
            File.WriteAllText(file, sb.ToString());
            ModLog.Info("circuit dump #" + count + ": " + blocks.Count + " block(s) with data ports written to logs/circuits/" + Path.GetFileName(file));
        }
    }
}
