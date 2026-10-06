using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace Mecanica
{
    /// <summary>Carries a value over from an old .cfg entry (section/key renamed for the English release) to the new one.
    /// BepInEx keeps entries that nobody binds in ConfigFile.OrphanedEntries (private); if the old key is there, its value is
    /// parsed into the new entry and the old key is dropped from the file.</summary>
    internal static class CfgMigra
    {
        static readonly PropertyInfo pOrfas = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.NonPublic | BindingFlags.Instance);

        public static ConfigEntry<T> De<T>(ConfigFile cfg, ConfigEntry<T> novo, string secaoVelha, string chaveVelha)
        {
            try
            {
                if (cfg == null || novo == null || pOrfas == null) return novo;
                if (!(pOrfas.GetValue(cfg) is Dictionary<ConfigDefinition, string> orfas)) return novo;
                var k = new ConfigDefinition(secaoVelha, chaveVelha);
                if (!orfas.TryGetValue(k, out var txt)) return novo;
                try { novo.Value = (T)TomlTypeConverter.ConvertToValue(txt, typeof(T)); } catch (Exception) { }
                orfas.Remove(k);
                cfg.Save();
            }
            catch (Exception) { }
            return novo;
        }
    }
}
