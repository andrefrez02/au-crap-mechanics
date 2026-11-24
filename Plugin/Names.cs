using System;
using System.Collections.Generic;

namespace CrapMechanics
{
    /// <summary>Remote control, round 0.31.35 (Andre: "the channel and every button can be renamed"): the names set AT THE COMPUTER, kept per
    /// block like the mod's knobs (by SCGuid), in BepInEx/config/CrapMechanics/names.txt (knobs.txt only holds numbers). One line per name:
    /// "{SCGuid}|ch&lt;TAB&gt;name" = the computer's channel name, "{SCGuid}|r{n}&lt;TAB&gt;name" = row n. Host / solo only (the caller checks).
    /// The names typed on the PHONE are in phone.txt (Phone.Memory) and win over these.</summary>
    internal static class Names
    {
        public const int MaxLen = 16;
        static readonly Dictionary<string, string> map = new Dictionary<string, string>();
        static bool loaded; static string path;
        /// <summary>Goes up whenever a name changes (callers keep their cached names in step with it).</summary>
        public static int Version { get; private set; }

        static string FilePath()
        {
            if (path == null) { var folder = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, PluginInfo.PluginName); try { System.IO.Directory.CreateDirectory(folder); } catch (Exception) { } path = System.IO.Path.Combine(folder, "names.txt"); }
            return path;
        }
        static void Load()
        {
            if (loaded) return; loaded = true; Version++;
            try
            {
                if (!System.IO.File.Exists(FilePath())) return;
                foreach (var line in System.IO.File.ReadAllLines(FilePath(), System.Text.Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    int t = line.IndexOf('\t'); if (t <= 0) continue;
                    string name = Clean(line.Substring(t + 1)); if (name.Length > 0) map[line.Substring(0, t)] = name;
                }
                ModLog.Info("names: " + map.Count + " computer name(s) loaded from " + FilePath());
            }
            catch (Exception ex) { ModLog.Warn("names: could not read " + FilePath() + ": " + ex.Message); }
        }
        static void Save()
        {
            try
            {
                var sb = new System.Text.StringBuilder("# Crap Mechanics: names set at the Remote Computers (per block). <SCGuid>|ch = channel name, <SCGuid>|r<n> = row n; TAB; the name\n");
                var keys = new List<string>(map.Keys); keys.Sort(StringComparer.Ordinal);
                foreach (var k in keys) sb.Append(k).Append('\t').Append(map[k]).Append('\n');
                System.IO.File.WriteAllText(FilePath(), sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) { ModLog.Warn("names: could not write " + FilePath() + ": " + ex.Message); }
        }
        /// <summary>key = "ch" or "r{n}". "" when the block has no such name.</summary>
        public static string Get(string guid, string key)
        {
            Load();
            if (string.IsNullOrEmpty(guid)) return "";
            return map.TryGetValue(guid + "|" + key, out var v) ? v : "";
        }
        /// <summary>An empty name removes the entry. Written to the file at once (names change rarely).</summary>
        public static bool Set(string guid, string key, string name)
        {
            Load();
            if (string.IsNullOrEmpty(guid)) return false;
            name = Clean(name); string k = guid + "|" + key;
            map.TryGetValue(k, out var cur);
            if ((cur ?? "") == name) return true;
            if (name.Length == 0) map.Remove(k); else map[k] = name;
            Version++; Save(); return true;
        }
        /// <summary>A character the rename box accepts: printable, and never &lt; or &gt; (the phone draws names with rich text).</summary>
        public static bool Allowed(char c) => c >= ' ' && c != (char)127 && c != '<' && c != '>' && !char.IsControl(c) && !char.IsSurrogate(c);
        /// <summary>Trimmed, only allowed characters, at most MaxLen.</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s) if (Allowed(c)) sb.Append(c);
            string r = sb.ToString().Trim();
            return r.Length > MaxLen ? r.Substring(0, MaxLen).Trim() : r;
        }
    }
}
