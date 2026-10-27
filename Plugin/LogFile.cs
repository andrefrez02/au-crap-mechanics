using System;
using System.IO;
using BepInEx.Logging;

namespace CrapMechanics
{
    /// <summary>Own per-session log (0.7.5): BepInEx's LogOutput.log is deleted every time the game opens, so a crash
    /// took the log with it. Here each session writes BepInEx/plugins/CrapMechanics/logs/session-YYYYMMDD-HHMMSS.log with EVERYTHING that goes
    /// through BepInEx (mod, CF, Unity), with a flush on every line. Keeps the last 5 in logs/ and moves the older ones to logs/_old (deletes nothing).</summary>
    internal sealed class LogFile : ILogListener
    {
        readonly StreamWriter w; readonly object lockV = new object();
        public LogLevel LogLevelFilter => LogLevel.All;

        LogFile(string path) { w = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }; }

        public static void Begin(string pluginFolder)
        {
            try
            {
                var folder = Path.Combine(pluginFolder, "logs"); Directory.CreateDirectory(folder);
                // 0.25.0: English names (session-*.log, _old); the sessao-*.log files of old versions go straight to _old
                var trash = Path.Combine(folder, "_old");
                foreach (var v in Directory.GetFiles(folder, "sessao-*.log")) { try { Directory.CreateDirectory(trash); var d = Path.Combine(trash, Path.GetFileName(v)); if (!File.Exists(d)) File.Move(v, d); } catch (Exception) { } }
                var olds = Directory.GetFiles(folder, "session-*.log"); Array.Sort(olds);
                // keeps the 4 newest + the one about to be born = 5 (moves the old ones to _old instead of deleting)
                if (olds.Length > 4)
                {
                    Directory.CreateDirectory(trash);
                    // project rule: nothing is deleted — the old ones go to logs/_old (Andre cleans up whenever he wants)
                    for (int i = 0; i < olds.Length - 4; i++) { try { var d = Path.Combine(trash, Path.GetFileName(olds[i])); if (!File.Exists(d)) File.Move(olds[i], d); } catch (Exception) { } }
                }
                var path = Path.Combine(folder, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                var l = new LogFile(path);
                Logger.Listeners.Add(l);
                l.Write("[CrapMechanics] session log: " + path);
            }
            catch (Exception ex) { ModLog.Warn("per-session log did not start: " + ex.Message); }
        }

        void Write(string s) { lock (lockV) { try { w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); } catch (Exception) { } } }
        public void LogEvent(object sender, LogEventArgs e) => Write("[" + e.Level + ":" + e.Source?.SourceName + "] " + e.Data);
        public void Dispose() { try { w.Dispose(); } catch (Exception) { } }
    }
}
