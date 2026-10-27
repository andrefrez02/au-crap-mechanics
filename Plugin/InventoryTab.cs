using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CrapMechanics
{
    /// <summary>"Mechanics" tab in the garage inventory.
    /// The inventory filters by `_categories & (1 << tab)` (tab = bit index; -1 = All). The game's categories use bits
    /// 0..8 (Frame..Other); the mod's blocks use bit 9 (512), which no game tab shows. Here I clone the button of the
    /// last tab, swap its text and click (-> ShowHeaderTab(9)), and copy the selected/unselected look from the game's tabs.</summary>
    [HarmonyPatch]
    internal static class InventoryTab
    {
        public const int Bit = 9;          // 1 << 9 = 512 = unused SpaceshipComponentUICategory
        public const string Name = "Mechanics", LocKey = "MenuInventoryCatMechanics";
        static string Text() { try { var t = Localization.Get(LocKey); if (!string.IsNullOrEmpty(t) && !t.StartsWith("?")) return t; } catch (Exception) { } return Name; }
        static RectTransform ours; static TMPro.TMP_Text labelText; static UIInventory inv;
        static float altSel, altUnsel; static bool didWarn;

        [HarmonyPostfix, HarmonyPatch(typeof(UIInventory), nameof(UIInventory.OnEnable))]
        static void AfterOpen(UIInventory __instance)
        {
            ModLog.Trace("InventoryTab.AfterOpen");
            try
            {
                if (ours != null && inv == __instance && ours.gameObject != null) { Paint(__instance._currentHeaderTab == Bit); return; }
                var hs = __instance._headerItems;
                if (hs == null || hs.Length < 2) { Warn("inventory has no tabs (" + (hs == null ? "null" : hs.Length.ToString()) + ")"); return; }
                var last = hs[hs.Length - 1]; var secondLast = hs[hs.Length - 2];
                if (last == null || last._rt == null) { Warn("game tab has no RectTransform"); return; }
                inv = __instance;

                var go = UnityEngine.Object.Instantiate(last._rt.gameObject, last._rt.parent);
                go.name = "CrapMechanics_Tab";
                ours = go.GetComponent<RectTransform>();
                // the parent ('Content') has HorizontalLayoutGroup + ContentSizeFitter inside a ScrollRect: at the end the tab ended up
                // outside the visible area. Put it right after "All" (the order on screen doesn't matter to the game, it uses _headerItems).
                // 0.11.1 (Andre): the tab goes LAST, after "Other" (before, it went after "All" because of the clipping at the end; the log
                // below shows the content width x the visible area so we know whether it fits)
                // 0.11.2: the order on screen is the order of the children of 'Content' (not of _headerItems): truly LAST
                ours.SetAsLastSibling();

                // text: the label is a TMProLocalizedText (the game's TextMeshProUGUI that translates itself from the key).
                // 0.5.x destroyed that component thinking it was only the translator -> the text vanished. Now it swaps the key
                // (MenuInventoryCatMechanics, in Localization.json) and the text.
                labelText = go.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (labelText != null)
                {
                    var loc = labelText.TryCast<TMProLocalizedText>();
                    if (loc != null) loc._initLocalizationKey = LocKey;
                    labelText.text = Text();
                }
                // click: throws away the copied click (ShowHeaderTab_Other) and adds ours
                var bt = go.GetComponentInChildren<Button>(true);
                if (bt != null)
                {
                    bt.onClick = new Button.ButtonClickedEvent();
                    bt.onClick.AddListener((UnityEngine.Events.UnityAction)(Action)Click);
                }
                else Warn("the copied tab has no Button — clicking won't work");

                // selected / unselected sizes, read from the game's tabs (the current one is selected)
                // selected / unselected heights read from the game's tabs (the current one is selected); the width
                // stays the layout's (the parent 'Content' has HorizontalLayoutGroup, the new tab goes at the end by itself)
                int current = __instance._currentHeaderTab + 1;
                altUnsel = last._rt.sizeDelta.y; altSel = altUnsel;
                for (int i = 0; i < hs.Length; i++) if (hs[i] != null && hs[i]._rt != null) { if (i == current) altSel = hs[i]._rt.sizeDelta.y; else altUnsel = hs[i]._rt.sizeDelta.y; }
                Paint(__instance._currentHeaderTab == Bit);
                // 0.5.0 shipped with width 0 (the tab layout measures the text, and the clone had not measured yet) and up to 0.11.1 I set the
                // width by hand (120 / text+padding / 224 = the width the copied tab CLAIMS before layout) — on screen the game's
                // tabs measure text + padding (~60-115 px) and ours came out wide. 0.11.2: let the tab measure itself like the others
                // (the clone's own ContentSizeFitter/layout), only force the layout to recalculate after the text is swapped; if it still
                // comes out too narrow (<30 px), set a minimum width from the text.
                float w0 = 0f, prefOrig = 0f, prefMine = 0f;
                try
                {
                    var lblOrig = last._rt.GetComponentInChildren<TMPro.TMP_Text>(true);
                    w0 = last._rt.rect.width;
                    if (lblOrig != null) { prefOrig = lblOrig.GetPreferredValues(lblOrig.text).x; prefMine = lblOrig.GetPreferredValues(Text()).x; }
                }
                catch (Exception) { }
                var readOld = go.GetComponent<LayoutElement>(); if (readOld != null) UnityEngine.Object.Destroy(readOld);
                ours.sizeDelta = new Vector2(last._rt.sizeDelta.x, ours.sizeDelta.y);
                try { LayoutRebuilder.ForceRebuildLayoutImmediate(ours); LayoutRebuilder.ForceRebuildLayoutImmediate(last._rt.parent.GetComponent<RectTransform>()); } catch (Exception) { }
                float wNow = ours.rect.width;
                if (wNow < 30f && prefMine > 1f)
                {
                    var rd = go.AddComponent<LayoutElement>(); rd.minWidth = prefMine + 24f; rd.preferredWidth = prefMine + 24f; rd.flexibleWidth = 0f; rd.layoutPriority = 10;
                    try { LayoutRebuilder.ForceRebuildLayoutImmediate(last._rt.parent.GetComponent<RectTransform>()); } catch (Exception) { }
                }
                try
                {
                    var sb = new System.Text.StringBuilder("tab: game tab widths (rect before layout):");
                    for (int i = 0; i < hs.Length; i++) if (hs[i] != null && hs[i]._rt != null) { var l = hs[i]._rt.GetComponentInChildren<TMPro.TMP_Text>(true); sb.Append(' ').Append(l != null ? l.text : "?").Append('=').Append(hs[i]._rt.rect.width.ToString("F0")); }
                    ModLog.Info(sb.ToString() + " | copied tab text " + prefOrig.ToString("F0") + " px, '" + Text() + "' " + prefMine.ToString("F0") + " px | copied " + w0.ToString("F0") + " -> ours after layout " + wNow.ToString("F0") + " -> " + ours.rect.width.ToString("F0") + " px");
                }
                catch (Exception) { }
                pending = 3;   // 0.11.2: check again on the next frames (the game's layout runs after OnEnable)
                ModLog.Info("tab '" + Name + "' created in the inventory (" + hs.Length + " game tabs, parent " + last._rt.parent.name + ", position " + ours.GetSiblingIndex() + ", active " + go.activeInHierarchy + ", size " + ours.sizeDelta + (bt != null ? "" : ", NO Button") + ")");
            }
            catch (Exception ex) { Warn("creating the tab failed: " + ex); }
        }

        // 0.11.2: on the frames after OnEnable the inventory layout has already run: make sure it's last and log the final width
        static int pending = 0;
        /// <summary>Called by Cycle (the plugin's Update) every frame.</summary>
        public static void Step()
        {
            try
            {
                if (pending <= 0 || ours == null || inv == null || !inv.gameObject.activeInHierarchy) return;
                pending--;
                ours.SetAsLastSibling();
                if (pending == 0) ModLog.Info("tab: final width " + ours.rect.width.ToString("F0") + " px, position " + ours.GetSiblingIndex() + " of " + ours.parent.childCount + " (siblings: " + string.Join(", ", Widths(ours.parent as RectTransform)) + ")");
            }
            catch (Exception) { }
        }
        static System.Collections.Generic.List<string> Widths(RectTransform parent)
        {
            var r = new System.Collections.Generic.List<string>();
            if (parent == null) return r;
            for (int i = 0; i < parent.childCount; i++) { var c = parent.GetChild(i) as RectTransform; if (c != null && c.gameObject.activeSelf) r.Add(c.name + "=" + c.rect.width.ToString("F0")); }
            return r;
        }

        /// <summary>When the game switches tabs, ours goes back to the unselected look.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(UIInventory), nameof(UIInventory.ShowHeaderTab))]
        static void AfterSwap(UIInventory __instance, int tabID)
        {
            ModLog.Trace("InventoryTab.AfterSwap " + tabID);
            try { if (__instance == inv) Paint(tabID == Bit); } catch (Exception) { }
        }

        static void Click()
        {
            try { if (inv == null) return; UIManager.PlaySoundUIClick(); inv.ShowHeaderTab(Bit, true); }
            catch (Exception ex) { Warn("tab click: " + ex.Message); }
        }

        static void Paint(bool selected)
        {
            if (ours == null || inv == null) return;
            ours.sizeDelta = new Vector2(ours.sizeDelta.x, selected ? altSel : altUnsel);
            if (labelText != null) { labelText.color = selected ? inv._selectedHeaderItemLabelColor : inv._unselectedHeaderItemLabelColor; labelText.text = Text(); }
        }

        static void Warn(string s) { if (didWarn) return; didWarn = true; ModLog.Warn("tab: " + s); }
    }

    /// <summary>0.10.0: tells JointSystem when the player loads a time rewind moment (Load button of the P menu).</summary>
    [HarmonyLib.HarmonyPatch(typeof(UITimebackMenu), nameof(UITimebackMenu.OnClick_Load))]
    internal static class Rewind
    {
        [HarmonyLib.HarmonyPostfix] static void After() { JointSystem.RewindRequested = true; ModLog.Info("time rewind: Load button"); }
    }
}
