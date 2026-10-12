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

        /// <summary>Tira do .cfg entradas que nenhuma versao do mod registra mais: qualquer chave das secoes velhas (os nomes em
        /// portugues de antes da 0.13.0) e as chaves mortas listadas como "Secao|chave" (substituidas por uma _vN). O De() acima ja
        /// remove a chave velha quando carrega o valor dela; o que sobra aqui e' peso morto no arquivo. Tem que rodar DEPOIS de
        /// todos os binds, senao apaga uma chave que ainda ia ser migrada.</summary>
        public static void Varrer(ConfigFile cfg, string[] secoesMortas, string[] chavesMortas)
        {
            try
            {
                if (cfg == null || pOrfas == null) return;
                if (!(pOrfas.GetValue(cfg) is Dictionary<ConfigDefinition, string> orfas) || orfas.Count == 0) return;
                var fora = new List<ConfigDefinition>();
                foreach (var kv in orfas)
                {
                    bool morta = false;
                    if (secoesMortas != null)
                        for (int i = 0; i < secoesMortas.Length && !morta; i++) morta = kv.Key.Section == secoesMortas[i];
                    if (!morta && chavesMortas != null)
                        for (int i = 0; i < chavesMortas.Length && !morta; i++) morta = chavesMortas[i] == kv.Key.Section + "|" + kv.Key.Key;
                    if (morta) fora.Add(kv.Key);
                }
                if (fora.Count == 0) return;
                for (int i = 0; i < fora.Count; i++) { orfas.Remove(fora[i]); Registro.Info("config: dropping the dead entry [" + fora[i].Section + "] " + fora[i].Key); }
                cfg.Save();
            }
            catch (Exception) { }
        }
    }
}
