using System;
using System.Collections.Generic;
using System.Reflection;

namespace Mecanica
{
    /// <summary>Optional link to the Mod Menu library (ModMenu.dll) (by reflection: this mod works fine without it). With the library the keys
    /// show up in the game's options menu ("Mod keys") and live in its .cfg; without it they come from this mod's own .cfg. The library
    /// also shows this mod's .cfg settings ("Mod settings"): Name() gives the display name, Hide() hides entries it should not show
    /// (the key entries, now edited as keys). The library may load after this mod, so the lookup is retried for a minute and pending calls are replayed.</summary>
    internal static class KeybindsLink
    {
        static Func<string, string, string> get; static Action<string, string, string, string> reg;
        static Action<string, string> setName; static Action<string, string, string> hide;
        static bool found; static int tries; static long next;
        static readonly List<Action> pend = new List<Action>();

        static void Look()
        {
            if (found || tries >= 12) return;
            long now = Environment.TickCount64; if (now < next) return; next = now + 5000; tries++;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("ModMenu.ModMenuApi", false); if (t == null) continue;
                    var mr = t.GetMethod("Register", BindingFlags.Public | BindingFlags.Static);
                    var mg = t.GetMethod("GetKey", BindingFlags.Public | BindingFlags.Static);
                    if (mr == null || mg == null) continue;
                    reg = (Action<string, string, string, string>)Delegate.CreateDelegate(typeof(Action<string, string, string, string>), mr);
                    get = (Func<string, string, string>)Delegate.CreateDelegate(typeof(Func<string, string, string>), mg);
                    var mn = t.GetMethod("SetModName", BindingFlags.Public | BindingFlags.Static);
                    if (mn != null) setName = (Action<string, string>)Delegate.CreateDelegate(typeof(Action<string, string>), mn);
                    var mh = t.GetMethod("HideSetting", BindingFlags.Public | BindingFlags.Static);
                    if (mh != null) hide = (Action<string, string, string>)Delegate.CreateDelegate(typeof(Action<string, string, string>), mh);
                    found = true;
                    Registro.Info("Mod Menu found: keys and settings can be changed in the options menu");
                    foreach (var a in pend) { try { a(); } catch (Exception) { } }
                    return;
                }
                if (tries == 12) Registro.Info("Mod Menu not installed: keys and settings come from this mod's .cfg");
            }
            catch (Exception ex) { Registro.Aviso("Mod Menu link failed: " + ex.Message); reg = null; get = null; found = false; tries = 99; }
        }
        static void Call(Action a) { pend.Add(a); Look(); if (found) { try { a(); } catch (Exception) { } } }

        public static void Register(string mod, string id, string label, string defaultKey) => Call(() => reg?.Invoke(mod, id, label, defaultKey ?? ""));
        /// <summary>Display name of this mod in the "Mod settings" section.</summary>
        public static void Name(string pluginGuid, string displayName) => Call(() => setName?.Invoke(pluginGuid, displayName));
        /// <summary>Hides one .cfg entry (key "*" = the whole section) from the "Mod settings" section.</summary>
        public static void Hide(string pluginGuid, string section, string key) => Call(() => hide?.Invoke(pluginGuid, section, key));
        /// <summary>Input System key name chosen in the options menu ("" = unbound); fallback (the mod's own cfg value) if the library is absent.</summary>
        public static string Key(string mod, string id, string fallback)
        {
            Look(); try { if (found) return get(mod, id) ?? fallback; } catch (Exception) { }
            return fallback;
        }
    }
}
