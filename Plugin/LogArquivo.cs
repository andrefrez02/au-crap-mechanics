using System;
using System.IO;
using BepInEx.Logging;

namespace Mecanica
{
    /// <summary>Log proprio por sessao (0.7.5): o LogOutput.log do BepInEx e' apagado toda vez que o jogo abre, entao um crash
    /// levava o log junto. Aqui cada sessao grava BepInEx/plugins/Mecanica/logs/sessao-AAAAMMDD-HHMMSS.log com TUDO que passa
    /// pelo BepInEx (mod, CF, Unity), com flush a cada linha. Deixa as 5 ultimas em logs/ e move as mais antigas pra logs/_lixeira (nao apaga nada).</summary>
    internal sealed class LogArquivo : ILogListener
    {
        readonly StreamWriter w; readonly object trava = new object();
        public LogLevel LogLevelFilter => LogLevel.All;

        LogArquivo(string caminho) { w = new StreamWriter(new FileStream(caminho, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }; }

        public static void Iniciar(string pastaPlugin)
        {
            try
            {
                var pasta = Path.Combine(pastaPlugin, "logs"); Directory.CreateDirectory(pasta);
                // 0.25.0: nomes em ingles (session-*.log, _old); os sessao-*.log de versoes antigas vao direto pra _old
                var lixo = Path.Combine(pasta, "_old");
                foreach (var v in Directory.GetFiles(pasta, "sessao-*.log")) { try { Directory.CreateDirectory(lixo); var d = Path.Combine(lixo, Path.GetFileName(v)); if (!File.Exists(d)) File.Move(v, d); } catch (Exception) { } }
                var velhos = Directory.GetFiles(pasta, "session-*.log"); Array.Sort(velhos);
                // guarda as 4 mais novas + a que vai nascer = 5 (move as antigas pra _lixeira em vez de apagar)
                if (velhos.Length > 4)
                {
                    Directory.CreateDirectory(lixo);
                    // regra do projeto: nada e' apagado — as antigas vao pra logs/_lixeira (o Andre limpa quando quiser)
                    for (int i = 0; i < velhos.Length - 4; i++) { try { var d = Path.Combine(lixo, Path.GetFileName(velhos[i])); if (!File.Exists(d)) File.Move(velhos[i], d); } catch (Exception) { } }
                }
                var caminho = Path.Combine(pasta, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                var l = new LogArquivo(caminho);
                Logger.Listeners.Add(l);
                l.Escrever("[Mecanica] session log: " + caminho);
            }
            catch (Exception ex) { Registro.Aviso("per-session log did not start: " + ex.Message); }
        }

        void Escrever(string s) { lock (trava) { try { w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); } catch (Exception) { } } }
        public void LogEvent(object sender, LogEventArgs e) => Escrever("[" + e.Level + ":" + e.Source?.SourceName + "] " + e.Data);
        public void Dispose() { try { w.Dispose(); } catch (Exception) { } }
    }
}
