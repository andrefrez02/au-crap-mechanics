using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CrapMechanics
{
    /// <summary>0.23.2 (Andre: "do it like the mod's other descriptions, where it shows mass, max force, etc. separately"): the status
    /// lines of the inventory panel. The game builds the list in `EPC_SpaceshipComponent.GetPropertiesList`: mass (key InvSCMass),
    /// glue material/heat, and at the end a `list.AddRange(_customProperties)` — the strings of `_customProperties` go in AS THEY ARE,
    /// without going through Localization. Since the prefab is the same for both languages, the plugin fills them in, here, reading the keys
    /// `&lt;prefab&gt;_Prop1`, `_Prop2`, … from Localization.json (stops at the first one missing). Only once, when the Core and the
    /// texts map already exist.</summary>
    internal static class Props
    {
        static bool isDone, failed; static float t = 9f;

        public static void Step(float dt)
        {
            if (isDone || failed) return;
            t += dt; if (t < 1f) return; t = 0f;
            var core = GameMeshes.CoreEntity; if (core == null) return;
            Il2CppSystem.Collections.Generic.Dictionary<string, string> map = null;
            try { var loc = Localization._cachedInstance; map = loc == null ? null : loc._map; } catch (Exception) { }
            if (map == null) return;
            try
            {
                var list = core._spaceshipComponents; if (list == null) return;
                int blocks = 0, lines = 0;
                for (int i = 0; i < list.Length; i++)
                {
                    var epc = list[i]; if (epc == null) continue;
                    string n = epc.name; if (string.IsNullOrEmpty(n) || !JointSystem.IsModBlock(n)) continue;
                    var txt = new List<string>();
                    for (int p = 1; p <= 8; p++)
                    {
                        string key = n + "_Prop" + p;
                        if (!map.ContainsKey(key)) break;
                        string v = map[key]; if (string.IsNullOrEmpty(v)) break;
                        txt.Add(v);
                    }
                    if (txt.Count == 0) continue;
                    var arr = new Il2CppStringArray(txt.Count);
                    for (int k = 0; k < txt.Count; k++) arr[k] = txt[k];
                    epc._customProperties = arr; blocks++; lines += txt.Count;
                }
                isDone = true;
                ModLog.Info("properties: " + lines + " status line(s) on " + blocks + " block(s)");
            }
            catch (Exception ex) { failed = true; ModLog.Warn("properties: " + ex.Message); }
        }
        public static void Forget() { isDone = false; t = 9f; }
    }
}
