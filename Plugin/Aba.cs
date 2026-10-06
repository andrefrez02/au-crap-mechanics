using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Mecanica
{
    /// <summary>Aba "Mechanics" no inventario da garagem.
    /// O inventario filtra por `_categories & (1 << aba)` (aba = indice do bit; -1 = Todos). As categorias do jogo usam os bits
    /// 0..8 (Frame..Other); os blocos do mod usam o bit 9 (512), que nenhuma aba do jogo mostra. Aqui eu clono o botao da
    /// ultima aba, troco o texto e o clique (-> ShowHeaderTab(9)), e copio o visual de selecionado/nao selecionado das abas do jogo.</summary>
    [HarmonyPatch]
    internal static class Aba
    {
        public const int Bit = 9;          // 1 << 9 = 512 = SpaceshipComponentUICategory livre
        public const string Nome = "Mechanics", Chave = "MenuInventoryCatMecanica";
        static string Texto() { try { var t = Localization.Get(Chave); if (!string.IsNullOrEmpty(t) && !t.StartsWith("?")) return t; } catch (Exception) { } return Nome; }
        static RectTransform minha; static TMPro.TMP_Text rotulo; static UIInventory inv;
        static float altSel, altNaoSel; static bool avisou;

        [HarmonyPostfix, HarmonyPatch(typeof(UIInventory), nameof(UIInventory.OnEnable))]
        static void DepoisDeAbrir(UIInventory __instance)
        {
            try
            {
                if (minha != null && inv == __instance && minha.gameObject != null) { Pintar(__instance._currentHeaderTab == Bit); return; }
                var hs = __instance._headerItems;
                if (hs == null || hs.Length < 2) { Aviso("inventory has no tabs (" + (hs == null ? "null" : hs.Length.ToString()) + ")"); return; }
                var ultima = hs[hs.Length - 1]; var penultima = hs[hs.Length - 2];
                if (ultima == null || ultima._rt == null) { Aviso("game tab has no RectTransform"); return; }
                inv = __instance;

                var go = UnityEngine.Object.Instantiate(ultima._rt.gameObject, ultima._rt.parent);
                go.name = "Mecanica_Aba";
                minha = go.GetComponent<RectTransform>();
                // o pai ('Content') tem HorizontalLayoutGroup + ContentSizeFitter dentro de um ScrollRect: no fim a aba ficava
                // fora da area visivel. Poe logo depois de "All" (a ordem na tela nao importa pro jogo, ele usa _headerItems).
                // 0.11.1 (Andre): a aba fica por ULTIMO, depois de "Other" (antes ficava depois de "All" por causa do corte no fim; log
                // abaixo mostra a largura do conteudo x da area visivel pra saber se ela cabe)
                // 0.11.2: a ordem na tela e' a ordem dos filhos do 'Content' (nao a de _headerItems): por ULTIMO de verdade
                minha.SetAsLastSibling();

                // texto: o label e' um TMProLocalizedText (TextMeshProUGUI do jogo que se traduz sozinho pela chave).
                // A 0.5.x destruia esse componente achando que era so o tradutor -> sumia o texto. Agora troca a chave
                // (MenuInventoryCatMecanica, no Localization.json) e o texto.
                rotulo = go.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (rotulo != null)
                {
                    var loc = rotulo.TryCast<TMProLocalizedText>();
                    if (loc != null) loc._initLocalizationKey = Chave;
                    rotulo.text = Texto();
                }
                // clique: joga fora o clique copiado (ShowHeaderTab_Other) e poe o nosso
                var bt = go.GetComponentInChildren<Button>(true);
                if (bt != null)
                {
                    bt.onClick = new Button.ButtonClickedEvent();
                    bt.onClick.AddListener((UnityEngine.Events.UnityAction)(Action)Clique);
                }
                else Aviso("the copied tab has no Button — clicking won't work");

                // tamanhos de selecionado / nao selecionado, lidos das abas do jogo (a atual esta selecionada)
                // alturas de selecionada / nao selecionada lidas das abas do jogo (a atual esta selecionada); a largura
                // fica a do layout (o pai 'Content' tem HorizontalLayoutGroup, a aba nova entra no fim sozinha)
                int atual = __instance._currentHeaderTab + 1;
                altNaoSel = ultima._rt.sizeDelta.y; altSel = altNaoSel;
                for (int i = 0; i < hs.Length; i++) if (hs[i] != null && hs[i]._rt != null) { if (i == atual) altSel = hs[i]._rt.sizeDelta.y; else altNaoSel = hs[i]._rt.sizeDelta.y; }
                Pintar(__instance._currentHeaderTab == Bit);
                // 0.5.0 saiu com largura 0 (o layout da aba mede o texto, e o clone ainda nao tinha medido) e ate a 0.11.1 eu fixava a
                // largura na mao (120 / texto+folga / 224 = a largura que a aba copiada DIZ ter antes do layout) — na tela as abas do
                // jogo medem o texto + folga (~60-115 px) e a nossa saia larga. 0.11.2: deixa a aba se medir sozinha igual as outras
                // (ContentSizeFitter/layout do proprio clone), so forca o layout a recalcular depois do texto trocado; se mesmo assim
                // ela sair estreita demais (<30 px), poe uma largura minima pelo texto.
                float w0 = 0f, prefOrig = 0f, prefMeu = 0f;
                try
                {
                    var lblOrig = ultima._rt.GetComponentInChildren<TMPro.TMP_Text>(true);
                    w0 = ultima._rt.rect.width;
                    if (lblOrig != null) { prefOrig = lblOrig.GetPreferredValues(lblOrig.text).x; prefMeu = lblOrig.GetPreferredValues(Texto()).x; }
                }
                catch (Exception) { }
                var leVelho = go.GetComponent<LayoutElement>(); if (leVelho != null) UnityEngine.Object.Destroy(leVelho);
                minha.sizeDelta = new Vector2(ultima._rt.sizeDelta.x, minha.sizeDelta.y);
                try { LayoutRebuilder.ForceRebuildLayoutImmediate(minha); LayoutRebuilder.ForceRebuildLayoutImmediate(ultima._rt.parent.GetComponent<RectTransform>()); } catch (Exception) { }
                float wAgora = minha.rect.width;
                if (wAgora < 30f && prefMeu > 1f)
                {
                    var le = go.AddComponent<LayoutElement>(); le.minWidth = prefMeu + 24f; le.preferredWidth = prefMeu + 24f; le.flexibleWidth = 0f; le.layoutPriority = 10;
                    try { LayoutRebuilder.ForceRebuildLayoutImmediate(ultima._rt.parent.GetComponent<RectTransform>()); } catch (Exception) { }
                }
                try
                {
                    var sb = new System.Text.StringBuilder("tab: game tab widths (rect before layout):");
                    for (int i = 0; i < hs.Length; i++) if (hs[i] != null && hs[i]._rt != null) { var l = hs[i]._rt.GetComponentInChildren<TMPro.TMP_Text>(true); sb.Append(' ').Append(l != null ? l.text : "?").Append('=').Append(hs[i]._rt.rect.width.ToString("F0")); }
                    Registro.Info(sb.ToString() + " | copied tab text " + prefOrig.ToString("F0") + " px, '" + Texto() + "' " + prefMeu.ToString("F0") + " px | copied " + w0.ToString("F0") + " -> ours after layout " + wAgora.ToString("F0") + " -> " + minha.rect.width.ToString("F0") + " px");
                }
                catch (Exception) { }
                pendente = 3;   // 0.11.2: confere de novo nos proximos frames (o layout do jogo roda depois do OnEnable)
                Registro.Info("tab '" + Nome + "' created in the inventory (" + hs.Length + " game tabs, parent " + ultima._rt.parent.name + ", position " + minha.GetSiblingIndex() + ", active " + go.activeInHierarchy + ", size " + minha.sizeDelta + (bt != null ? "" : ", NO Button") + ")");
            }
            catch (Exception ex) { Aviso("creating the tab failed: " + ex); }
        }

        // 0.11.2: nos frames seguintes ao OnEnable o layout do inventario ja rodou: garante ultima posicao e loga a largura final
        static int pendente = 0;
        /// <summary>Chamado pelo Ciclo (Update do plugin) todo frame.</summary>
        public static void Passo()
        {
            try
            {
                if (pendente <= 0 || minha == null || inv == null || !inv.gameObject.activeInHierarchy) return;
                pendente--;
                minha.SetAsLastSibling();
                if (pendente == 0) Registro.Info("tab: final width " + minha.rect.width.ToString("F0") + " px, position " + minha.GetSiblingIndex() + " of " + minha.parent.childCount + " (siblings: " + string.Join(", ", Larguras(minha.parent as RectTransform)) + ")");
            }
            catch (Exception) { }
        }
        static System.Collections.Generic.List<string> Larguras(RectTransform pai)
        {
            var r = new System.Collections.Generic.List<string>();
            if (pai == null) return r;
            for (int i = 0; i < pai.childCount; i++) { var c = pai.GetChild(i) as RectTransform; if (c != null && c.gameObject.activeSelf) r.Add(c.name + "=" + c.rect.width.ToString("F0")); }
            return r;
        }

        /// <summary>Quando o jogo troca de aba, a nossa volta pro visual de nao selecionada.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(UIInventory), nameof(UIInventory.ShowHeaderTab))]
        static void DepoisDeTrocar(UIInventory __instance, int tabID)
        {
            try { if (__instance == inv) Pintar(tabID == Bit); } catch (Exception) { }
        }

        static void Clique()
        {
            try { if (inv == null) return; UIManager.PlaySoundUIClick(); inv.ShowHeaderTab(Bit, true); }
            catch (Exception ex) { Aviso("tab click: " + ex.Message); }
        }

        static void Pintar(bool selecionada)
        {
            if (minha == null || inv == null) return;
            minha.sizeDelta = new Vector2(minha.sizeDelta.x, selecionada ? altSel : altNaoSel);
            if (rotulo != null) { rotulo.color = selecionada ? inv._selectedHeaderItemLabelColor : inv._unselectedHeaderItemLabelColor; rotulo.text = Texto(); }
        }

        static void Aviso(string s) { if (avisou) return; avisou = true; Registro.Aviso("tab: " + s); }
    }

    /// <summary>0.10.0: avisa as Juntas quando o jogador carrega um momento do time rewind (botao Load do menu do P).</summary>
    [HarmonyLib.HarmonyPatch(typeof(UITimebackMenu), nameof(UITimebackMenu.OnClick_Load))]
    internal static class Rebobinar
    {
        [HarmonyLib.HarmonyPostfix] static void Depois() { Juntas.RewindPedido = true; Registro.Info("time rewind: Load button"); }
    }
}
