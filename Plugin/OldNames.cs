using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;

namespace CrapMechanics
{
    /// <summary>0.27.0 — the English release. Before it the mod was called "Mecanica" (plugin folder, .dll, .cfg and config folder) and
    /// several prefabs had Portuguese names (SC_Pistao, SC_RodaSportsP, ...). Nothing a player made should be lost to that:
    ///  - the old .cfg and knobs.txt are carried over once, on the first launch of the new version (Carry);
    ///  - a blueprint stores each part by the hash of its prefab name (SCPrefab._prefab), so one saved before the rename would come back
    ///    without those parts: the .bp files in the game's blueprint folder are converted on disk when the mod loads, before the game reads
    ///    them (ConvertBlueprintFiles, 0.28.1), with a backup of each changed file;
    ///  - prefab-renames.txt (config folder) lists both names and both hashes, for the offline converter (bp_convert.py) for blueprints
    ///    kept somewhere else.</summary>
    internal static class OldNames
    {
        public const string OldGuid = "com.approximatelyup.mods.mecanica", OldFolder = "Mecanica";

        /// <summary>Old prefab name -> new one (the " M" mirrored versions follow the same names).</summary>
        public static readonly (string old, string now)[] Prefabs =
        {
            ("SC_Pistao", "SC_Piston"), ("SC_MiniPistao", "SC_MiniPiston"), ("SC_PistaoFim", "SC_PistonEnd"), ("SC_MiniPistaoFim", "SC_MiniPistonEnd"),
            ("SC_MotorP", "SC_MotorSmall"), ("SC_MotorM", "SC_MotorMedium"), ("SC_MotorG", "SC_MotorLarge"),
            ("SC_MotorCombP", "SC_MotorFuelSmall"), ("SC_MotorCombM", "SC_MotorFuelMedium"), ("SC_MotorCombG", "SC_MotorFuelLarge"),
            ("SC_RodaSportsP", "SC_WheelSportsSmall"), ("SC_RodaSportsM", "SC_WheelSportsMedium"), ("SC_RodaSportsG", "SC_WheelSportsLarge"),
            ("SC_RodaOffroadP", "SC_WheelOffroadSmall"), ("SC_RodaOffroadM", "SC_WheelOffroadMedium"), ("SC_RodaOffroadG", "SC_WheelOffroadLarge"),
            ("SC_Volante", "SC_SteeringWheel"), ("SC_TanqueAzul", "SC_TankSmallBlue"), ("SC_TanqueVerde", "SC_TankSmallGreen"),
        };

        /// <summary>The current name of a prefab typed with its old name (e.g. in [Bonus] extra_blocks); any other name comes back as is.</summary>
        public static string Current(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            bool m = name.EndsWith(" M"); string b = m ? name.Substring(0, name.Length - 2) : name;
            foreach (var (old, now) in Prefabs) if (old == b) return now + (m ? " M" : "");
            return name;
        }

        /// <summary>Is the old Mecanica.dll still somewhere under BepInEx/plugins (BepInEx loads any .dll under it, in any folder)?</summary>
        public static bool OldCopyInstalled(out string path)
        {
            path = null;
            try
            {
                foreach (var f in Directory.GetFiles(BepInEx.Paths.PluginPath, OldFolder + ".dll", SearchOption.AllDirectories)) { path = f; return true; }
            }
            catch (Exception) { }
            return false;
        }

        // ---------------------------------------------------------------- .cfg and knobs.txt of the old plugin name
        /// <summary>Copies the old plugin's .cfg (and knobs.txt) to the new names once, when the new ones don't exist yet. Call before any Bind.</summary>
        public static void Carry(ConfigFile cfg)
        {
            try
            {
                string now = cfg.ConfigFilePath, old = Path.Combine(Path.GetDirectoryName(now) ?? "", OldGuid + ".cfg");
                if (!File.Exists(now) && File.Exists(old)) { File.Copy(old, now); cfg.Reload(); ModLog.Info("settings carried over from " + Path.GetFileName(old) + " (the mod's name before 0.27.0); the old file was left in place"); }
            }
            catch (Exception ex) { ModLog.Warn("could not carry over the old .cfg: " + ex.Message); }
            try
            {
                string nowDir = Path.Combine(BepInEx.Paths.ConfigPath, PluginInfo.PluginName), oldFile = Path.Combine(BepInEx.Paths.ConfigPath, OldFolder, "knobs.txt");
                string nowFile = Path.Combine(nowDir, "knobs.txt");
                if (!File.Exists(nowFile) && File.Exists(oldFile)) { Directory.CreateDirectory(nowDir); File.Copy(oldFile, nowFile); ModLog.Info("knob values carried over from config/" + OldFolder + "/knobs.txt"); }
            }
            catch (Exception ex) { ModLog.Warn("could not carry over the old knobs.txt: " + ex.Message); }
        }

        // ---------------------------------------------------------------- blueprints saved with the old prefab names
        // 0.28.1: 0.27.0/0.28.0 swapped the hashes in memory with a Harmony postfix on BlueprintUtility.BlueprintCreateBinaryData. The game calls
        // it for EVERY saved blueprint while it starts (RunBlueprintConversion), and the interop's trampoline can't carry its NativeList<T>
        // parameters (generic structs passed by value): the game closed on its own right after the Components Framework applied the texts.
        // Now the files themselves are converted, here in Load — before the game reads any of them — with a backup of each changed one.
        static Dictionary<ulong, ulong> byHash;

        /// <summary>Old hash -> new hash for every renamed prefab (and its " M"), from the game's own SCPrefab(name); writes prefab-renames.txt.</summary>
        public static void BuildMap()
        {
            if (byHash != null) return;
            byHash = new Dictionary<ulong, ulong>();
            var lines = new List<string> { "# Crap Mechanics " + PluginInfo.PluginVersion + " prefab renames (used by bp_convert.py): old name, new name, old hash, new hash",
                                           "# A blueprint stores each part by the hash of its prefab name; these are the hashes the game computes for both names." };
            foreach (var (old, now) in Prefabs)
                foreach (var suf in new[] { "", " M" })
                {
                    ulong a = Ecs.HashPrefab(old + suf), b = Ecs.HashPrefab(now + suf);
                    if (a == 0 || b == 0) continue;
                    byHash[a] = b; lines.Add(old + suf + "\t" + now + suf + "\t" + a + "\t" + b);
                }
            try
            {
                var folder = Path.Combine(BepInEx.Paths.ConfigPath, PluginInfo.PluginName); Directory.CreateDirectory(folder);
                File.WriteAllLines(Path.Combine(folder, "prefab-renames.txt"), lines);
            }
            catch (Exception ex) { ModLog.Warn("prefab-renames.txt: " + ex.Message); }
            ModLog.Info("old prefab names: " + byHash.Count + " hash(es) mapped to the new names");
        }

        public const string BackupSuffix = ".bak-0.27";

        /// <summary>Every .bp in the game's blueprint folder (subfolders too): the records whose part hash is an old name get the new hash. Same strict
        /// parse as bp_convert.py (a file that doesn't follow the format to the last byte is left alone); each changed file is first copied to
        /// &lt;name&gt;.bp.bak-0.27 (an existing backup is never overwritten) and is replaced in one step (.tmp + move). A converted file has no old
        /// hash left, so the next launches only read it.</summary>
        public static void ConvertBlueprintFiles()
        {
            if (byHash == null || byHash.Count == 0) return;
            string folder = BlueprintFolder();
            if (folder == null || !Directory.Exists(folder)) { ModLog.Info("old prefab names: no blueprint folder (" + folder + ")"); return; }
            int files = 0, changed = 0, parts = 0, skipped = 0;
            foreach (var f in Directory.GetFiles(folder, "*.bp", SearchOption.AllDirectories))
            {
                files++;
                try
                {
                    var data = File.ReadAllBytes(f);
                    var offs = RecordOffsets(data);
                    if (offs == null) { skipped++; continue; }
                    int here = 0;
                    foreach (var o in offs)
                    {
                        if (!byHash.TryGetValue(BitConverter.ToUInt64(data, o), out var now)) continue;
                        BitConverter.TryWriteBytes(new Span<byte>(data, o, 8), now); here++;
                    }
                    if (here == 0) continue;
                    var bak = f + BackupSuffix; if (!File.Exists(bak)) File.Copy(f, bak);
                    var tmp = f + ".tmp"; File.WriteAllBytes(tmp, data); File.Move(tmp, f, true);
                    changed++; parts += here;
                    ModLog.Info("old prefab names: " + Path.GetRelativePath(folder, f) + " — " + here + " part(s) renamed (backup " + Path.GetFileName(bak) + ")");
                }
                catch (Exception ex) { ModLog.Warn("old prefab names: " + f + ": " + ex.Message); }
            }
            ModLog.Info("old prefab names: " + files + " blueprint(s) in " + folder + ", " + changed + " converted (" + parts + " part(s))" + (skipped > 0 ? ", " + skipped + " not in the known format and left alone" : ""));
        }

        static string BlueprintFolder()
        {
            try { var p = UnityEngine.Application.persistentDataPath; if (!string.IsNullOrEmpty(p)) return Path.Combine(p, "Blueprints"); } catch (Exception) { }
            try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "ApproximatelyGames", "ApproximatelyUp", "Blueprints"); } catch (Exception) { return null; }
        }

        /// <summary>[u32 nStructs] + nStructs x [u64 typeHash][u32 structSize][u32 fieldCount] + fieldCount x 16 B; then records to the end of the file:
        /// [u64 componentTypeHash][u32 schemaIndex][u32 reserved] + structSize B. Offsets of the records' hash, or null if it doesn't parse exactly.</summary>
        static List<int> RecordOffsets(byte[] d)
        {
            var offs = new List<int>();
            if (d.Length == 0) return offs;
            if (d.Length < 4) return null;
            uint n = BitConverter.ToUInt32(d, 0); int off = 4;
            if (n > 4096) return null;
            var sizes = new List<uint>();
            for (uint i = 0; i < n; i++)
            {
                if (off + 16 > d.Length) return null;
                uint size = BitConverter.ToUInt32(d, off + 8), nf = BitConverter.ToUInt32(d, off + 12); off += 16;
                if (nf > 4096 || off + 16L * nf > d.Length) return null;
                off += (int)(16 * nf); sizes.Add(size);
            }
            while (off < d.Length)
            {
                if (off + 16 > d.Length) return null;
                uint idx = BitConverter.ToUInt32(d, off + 8);
                if (idx >= sizes.Count || off + 16L + sizes[(int)idx] > d.Length) return null;
                offs.Add(off); off += 16 + (int)sizes[(int)idx];
            }
            return offs;
        }
    }
}
