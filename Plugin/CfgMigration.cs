using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace CrapMechanics
{
    /// <summary>Carries a value over from an old .cfg entry (section/key renamed for the English release) to the new one.
    /// BepInEx keeps entries that nobody binds in ConfigFile.OrphanedEntries (private); if the old key is there, its value is
    /// parsed into the new entry and the old key is dropped from the file.</summary>
    internal static class CfgMigration
    {
        static readonly PropertyInfo pOrphans = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.NonPublic | BindingFlags.Instance);

        public static ConfigEntry<T> Of<T>(ConfigFile cfg, ConfigEntry<T> newVal, string oldSection, string oldKey)
        {
            try
            {
                if (cfg == null || newVal == null || pOrphans == null) return newVal;
                if (!(pOrphans.GetValue(cfg) is Dictionary<ConfigDefinition, string> orphanEntries)) return newVal;
                var k = new ConfigDefinition(oldSection, oldKey);
                if (!orphanEntries.TryGetValue(k, out var txt)) return newVal;
                try { newVal.Value = (T)TomlTypeConverter.ConvertToValue(txt, typeof(T)); } catch (Exception) { }
                orphanEntries.Remove(k);
                cfg.Save();
            }
            catch (Exception) { }
            return newVal;
        }

        /// <summary>Removes from the .cfg entries that no version of the mod registers anymore: any key of the old sections (the
        /// Portuguese names from before 0.13.0) and the dead keys listed as "Section|key" (replaced by a _vN). Of() above already
        /// removes the old key when it carries its value over; what is left here is dead weight in the file. Must run AFTER
        /// all the binds, otherwise it deletes a key that was still going to be migrated.</summary>
        public static void Scan(ConfigFile cfg, string[] deadSections, string[] deadKeys)
        {
            try
            {
                if (cfg == null || pOrphans == null) return;
                if (!(pOrphans.GetValue(cfg) is Dictionary<ConfigDefinition, string> orphanEntries) || orphanEntries.Count == 0) return;
                var outside = new List<ConfigDefinition>();
                foreach (var kv in orphanEntries)
                {
                    bool dead = false;
                    if (deadSections != null)
                        for (int i = 0; i < deadSections.Length && !dead; i++) dead = kv.Key.Section == deadSections[i];
                    if (!dead && deadKeys != null)
                        for (int i = 0; i < deadKeys.Length && !dead; i++) dead = deadKeys[i] == kv.Key.Section + "|" + kv.Key.Key;
                    if (dead) outside.Add(kv.Key);
                }
                if (outside.Count == 0) return;
                for (int i = 0; i < outside.Count; i++) { orphanEntries.Remove(outside[i]); ModLog.Info("config: dropping the dead entry [" + outside[i].Section + "] " + outside[i].Key); }
                cfg.Save();
            }
            catch (Exception) { }
        }
    }
}
