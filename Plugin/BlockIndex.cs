using System;
using System.Collections.Generic;
using System.IO;

namespace CrapMechanics
{
    /// <summary>0.29.0 — the game's block catalogue, read once from Core._spaceshipComponents: the prefab name, the name the inventory
    /// shows (Localization key "&lt;prefab&gt;_Name"), the group the game files the block under (EPC_SpaceshipComponent._scGroup: Frame,
    /// Glass, BasicControllers, …) and the inventory tabs (_categories, a flags enum). [Bonus] extra_blocks uses this to turn one word
    /// into every block of that group, so a category doesn't have to be listed block by block.
    /// The same list is written to BepInEx/config/CrapMechanics/blocks.txt — that file is where to read the names to put in the .cfg.</summary>
    internal static class BlockIndex
    {
        internal sealed class Info { public string prefab, display, group, tabs; public int amount; }

        static readonly Dictionary<string, Info> byPrefab = new Dictionary<string, Info>(StringComparer.Ordinal);
        static readonly Dictionary<string, List<string>> byGroup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<string>> byTab = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> byDisplay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The catalogue has been read (the Core and the texts already existed).</summary>
        public static bool Ready;
        /// <summary>0.31.12: every prefab name in the catalogue (CircuitDump turns them into hashes).</summary>
        public static IEnumerable<string> Prefabs() => byPrefab.Keys;
        /// <summary>The catalogue could not be read — extra_blocks then only takes exact block names, as it did before 0.29.0.</summary>
        public static bool Failed;
        static float t = 9f;

        /// <summary>Reads the catalogue once, as soon as the Core and the localization map exist (same wait as Props.cs).</summary>
        public static void Step(float dt)
        {
            if (Ready || Failed) return;
            t += dt; if (t < 1f) return; t = 0f;
            var core = GameMeshes.CoreEntity; if (core == null) return;
            Il2CppSystem.Collections.Generic.Dictionary<string, string> map = null;
            try { var loc = Localization._cachedInstance; map = loc == null ? null : loc._map; } catch (Exception) { }
            if (map == null) return;
            try
            {
                var list = core._spaceshipComponents; if (list == null || list.Length == 0) return;
                for (int i = 0; i < list.Length; i++)
                {
                    var epc = list[i]; if (epc == null) continue;
                    string n = epc.name; if (string.IsNullOrEmpty(n)) continue;
                    var inf = new Info { prefab = n, group = epc._scGroup.ToString(), tabs = epc._categories.ToString(), amount = epc._availableAmount };
                    string key = n + "_Name";
                    try { if (map.ContainsKey(key)) inf.display = map[key]; } catch (Exception) { }
                    byPrefab[n] = inf;
                    Add(byGroup, inf.group, n);
                    foreach (var tab in inf.tabs.Split(',')) Add(byTab, tab.Trim(), n);
                    if (!string.IsNullOrEmpty(inf.display) && !byDisplay.ContainsKey(inf.display)) byDisplay[inf.display] = n;
                }
                Ready = true;
                var groups = new List<string>();
                foreach (var kv in byGroup) groups.Add(kv.Key + " " + kv.Value.Count);
                groups.Sort(StringComparer.Ordinal);
                ModLog.Info("blocks: " + byPrefab.Count + " block(s) in the catalogue; per group: " + string.Join(", ", groups));
                Dump();
            }
            catch (Exception ex) { Failed = true; ModLog.Warn("blocks: catalogue not read (" + ex.Message + ") — [Bonus] extra_blocks only takes exact block names"); }
        }

        static void Add(Dictionary<string, List<string>> d, string key, string prefab)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!d.TryGetValue(key, out var l)) d[key] = l = new List<string>();
            l.Add(prefab);
        }

        /// <summary>The blocks of a group (Frame, Glass, BasicControllers, …) or, with the "tab:" prefix, of an inventory tab
        /// (tab:Frame, tab:Electronics, …). null = the word is not a group or a tab.
        /// 0.29.1: the "group:" prefix is accepted as well (group:Frame = Frame), because it is the one that reads symmetrically with
        /// "tab:" — with the bare word the two kinds of category look the same in the .cfg, and a group and a tab with the same name
        /// (Frame, Glass) do not hold the same blocks.</summary>
        public static List<string> OfCategory(string word)
        {
            if (!Ready || string.IsNullOrEmpty(word)) return null;
            if (word.StartsWith("tab:", StringComparison.OrdinalIgnoreCase))
                return byTab.TryGetValue(word.Substring(4).Trim(), out var t) ? t : null;
            if (word.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
                word = word.Substring(6).Trim();
            return byGroup.TryGetValue(word, out var g) ? g : null;
        }

        /// <summary>The prefab of the block the inventory shows with this name ("Power Router"), or null.</summary>
        public static string OfDisplay(string display)
        {
            if (!Ready || string.IsNullOrEmpty(display)) return null;
            return byDisplay.TryGetValue(display, out var p) ? p : null;
        }

        public static bool Known(string prefab) => Ready && byPrefab.ContainsKey(prefab);
        /// <summary>0.31.8 (FlightTrace): every block name in the catalogue.</summary>
        public static IEnumerable<string> AllPrefabs => byPrefab.Keys;
        /// <summary>0.31.10: block name from the prefab hash (SCPrefab._prefab), for the logs. null if unknown or the catalogue isn't read yet.</summary>
        static Dictionary<ulong, string> byHash;
        public static string NameOfHash(ulong h)
        {
            if (!Ready) return null;
            if (byHash == null) { byHash = new Dictionary<ulong, string>(); foreach (var n in byPrefab.Keys) { ulong x = Ecs.HashPrefab(n); if (x != 0 && !byHash.ContainsKey(x)) byHash[x] = n; } }
            return byHash.TryGetValue(h, out var nm) ? nm : null;
        }

        /// <summary>BepInEx/config/CrapMechanics/blocks.txt: every block with its group, its inventory tabs and the amount the game
        /// gives. It is the list of names that [Bonus] extra_blocks accepts.</summary>
        static void Dump()
        {
            try
            {
                var folder = Path.Combine(BepInEx.Paths.ConfigPath, PluginInfo.PluginName); Directory.CreateDirectory(folder);
                var rows = new List<Info>(byPrefab.Values);
                rows.Sort((a, b) => { int c = string.Compare(a.group, b.group, StringComparison.Ordinal); return c != 0 ? c : string.Compare(a.prefab, b.prefab, StringComparison.Ordinal); });
                var lines = new List<string> {
                    "# Every block the game registered, written by Crap Mechanics " + PluginInfo.PluginVersion + " at start.",
                    "# [Bonus] extra_blocks in com.approximatelyup.mods.crapmechanics.cfg takes, separated by commas:",
                    "#   a block name from the 'block' column        SC_FrameAA=400",
                    "#   the name the inventory shows                Power Router=5",
                    "#   a group from the 'group' column             Frame=400        (every block of the group; group:Frame=400 also works)",
                    "#   an inventory tab, with the tab: prefix      tab:Electronics=50",
                    "# A group and an inventory tab with the same name are NOT the same list: group Frame is the normal frames, tab Frame also has",
                    "# the nanoframes and the micro frames. A later entry wins over an earlier one.",
                    "# The amount is ADDED to what the game gives (the 'game gives' column), and a block that is still locked is skipped.",
                    "#",
                    "# block\tgroup\tinventory tabs\tgame gives\tname in the inventory" };
                foreach (var r in rows) lines.Add(r.prefab + "\t" + r.group + "\t" + r.tabs + "\t" + r.amount + "\t" + (r.display ?? ""));
                File.WriteAllLines(Path.Combine(folder, "blocks.txt"), lines);
                ModLog.Info("blocks: the list of names for the .cfg is in config/" + PluginInfo.PluginName + "/blocks.txt");
            }
            catch (Exception ex) { ModLog.Warn("blocks: blocks.txt not written (" + ex.Message + ")"); }
        }
    }
}
