using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryArchipelago
{
    public sealed partial class CastleStoryArchipelagoPlugin
    {
        // This is an event-driven visibility policy, not a delayed UI scanner.
        // Session-level membership is calculated on connection. Native Brix
        // creates cards lazily; we enforce policy during caption assignment
        // and enable, BEFORE Unity renders the new controls.
        private bool nativeCaptionHookInstalled;
        private bool evaluatingNativeCaption;
        private int nativeCaptionDecisions;
        private bool observedSaveCardWithoutTag;
        private readonly Dictionary<GameObject, string> nativeSaveCardFolders =
            new Dictionary<GameObject, string>();
        private readonly HashSet<Component> pendingNativeCaptions =
            new HashSet<Component>();
        private sealed class NativeCardMask
        {
            public CanvasGroup group;
            public float initialAlpha;
            public bool initialInteractable;
            public bool initialBlocksRaycasts;
            public Component layout;
            public bool initialIgnoreLayout;
        }
        private readonly Dictionary<GameObject, NativeCardMask> maskedNativeCards =
            new Dictionary<GameObject, NativeCardMask>();
        private int nativeCardMasksApplied;
        private int nativeMapCardDiagnostics;

        private void InstallNativePreRenderHooks()
        {
            try
            {
                Type textType = FindRuntimeType("UnityEngine.UI.Text");
                if (textType == null)
                {
                    Logger.LogWarning("[AP UI NATIVE] Unity Text unavailable; " +
                        "using legacy fallback diagnostics.");
                    return;
                }
                MethodInfo textSetter = AccessTools.PropertySetter(textType, "text");
                if (textSetter == null)
                {
                    Logger.LogWarning("[AP UI NATIVE] Text.text setter unavailable.");
                    return;
                }
                var callback = AccessTools.Method(
                    typeof(CastleStoryArchipelagoPlugin), "ObserveNativeTextCommitted");
                harmony.Patch(textSetter, postfix: new HarmonyMethod(callback));
                nativeCaptionHookInstalled = true;

                MethodInfo enable = textType.GetMethod("OnEnable",
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
                if (enable != null)
                    harmony.Patch(enable, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CastleStoryArchipelagoPlugin),
                            "ObserveNativeTextEnabled")));

                // The first Brix card may be reactivated from an object pool
                // with unchanged text. The Canvas callback enforces already
                // classified cards BEFORE the renderer can draw them.
                Canvas.willRenderCanvases += ApplyKnownNativeRestrictionsBeforeRender;
                Logger.LogInfo("[AP UI NATIVE] Pre-render text/setter visibility " +
                    "interception installed; deferred scans disabled.");
            }
            catch (Exception error)
            {
                nativeCaptionHookInstalled = false;
                Logger.LogWarning("[AP UI NATIVE] Pre-render hook unavailable: " +
                    error.GetType().Name + " " + error.Message);
            }
        }

        private void UninstallNativePreRenderHooks()
        {
            Canvas.willRenderCanvases -= ApplyKnownNativeRestrictionsBeforeRender;
        }

        private static void ObserveNativeTextCommitted(object __instance)
        {
            if (Instance == null || !Instance.nativeCaptionHookInstalled)
                return;
            Instance.QueueNativeCaption(__instance as Component);
        }

        private static void ObserveNativeTextEnabled(object __instance)
        {
            if (Instance == null || !Instance.nativeCaptionHookInstalled)
                return;
            Instance.ApplyNativeCaptionBeforeRender(__instance as Component);
        }

        private void QueueNativeCaption(Component caption)
        {
            if (caption == null || !titleSceneSeen) return;
            pendingNativeCaptions.Add(caption);
            // Apply immediately for a native visual result, but never call
            // SetActive(false) while Brix is constructing a pooled card.
            ApplyNativeCaptionBeforeRender(caption);
        }

        private void HidePreRenderCard(GameObject card)
        {
            if (card == null || card.name.StartsWith("Archipelago_",
                StringComparison.Ordinal)) return;
            NativeCardMask state;
            if (!maskedNativeCards.TryGetValue(card, out state))
            {
                CanvasGroup group = card.GetComponent<CanvasGroup>();
                if (group == null) group = card.AddComponent<CanvasGroup>();
                Type layoutType = FindRuntimeType("UnityEngine.UI.LayoutElement");
                Component layout = layoutType == null ? null :
                    card.GetComponent(layoutType);
                state = new NativeCardMask {
                    group = group,
                    initialAlpha = group.alpha,
                    initialInteractable = group.interactable,
                    initialBlocksRaycasts = group.blocksRaycasts,
                    layout = layout,
                    initialIgnoreLayout = layout != null &&
                        GetUiProperty(layout, "ignoreLayout") is bool &&
                        (bool)GetUiProperty(layout, "ignoreLayout")
                };
                maskedNativeCards[card] = state;
                nativeCardMasksApplied++;
            }
            if (state.group != null)
            {
                state.group.alpha = 0f;
                state.group.interactable = false;
                state.group.blocksRaycasts = false;
            }
            if (state.layout != null)
                SetUiProperty(state.layout, "ignoreLayout", true);
        }

        private void ShowPreRenderCard(GameObject card)
        {
            if (card == null) return;
            NativeCardMask state;
            if (!maskedNativeCards.TryGetValue(card, out state)) return;
            if (state.group != null)
            {
                state.group.alpha = state.initialAlpha;
                state.group.interactable = state.initialInteractable;
                state.group.blocksRaycasts = state.initialBlocksRaycasts;
            }
            if (state.layout != null)
                SetUiProperty(state.layout, "ignoreLayout",
                    state.initialIgnoreLayout);
            maskedNativeCards.Remove(card);
        }

        private void ClearPreRenderCardPolicy()
        {
            foreach (var card in new List<GameObject>(maskedNativeCards.Keys))
                ShowPreRenderCard(card);
            maskedNativeCards.Clear();
            pendingNativeCaptions.Clear();
            nativeSaveCardFolders.Clear();
        }

        private void ReconcileKnownNativeSaveCards()
        {
            // Tag metadata can be associated externally while Load is open.
            // Reconcile just previously classified save cards, not the full
            // UI. Never restore an unrelated forbidden mode card.
            foreach (var pair in nativeSaveCardFolders)
            {
                GameObject card = pair.Key;
                if (card == null) continue;
                bool allowed = cachedAllowedSaveNames.Contains(pair.Value);
                if (allowed) ShowPreRenderCard(card);
                else HidePreRenderCard(card);
            }
        }

        private void ApplyKnownNativeRestrictionsBeforeRender()
        {
            // Unity invokes this before drawing a canvas. Re-evaluate captions
            // after the game's Brix builder has finished assigning all
            // labels; no deferred frame, no full-scene scan.
            if (pendingNativeCaptions.Count > 0)
            {
                var pending = new List<Component>(pendingNativeCaptions);
                pendingNativeCaptions.Clear();
                foreach (Component caption in pending)
                    if (caption != null) ApplyNativeCaptionBeforeRender(caption);
            }
            foreach (var entry in maskedNativeCards)
            {
                if (entry.Key == null || entry.Value.group == null) continue;
                entry.Value.group.alpha = 0f;
                entry.Value.group.interactable = false;
                entry.Value.group.blocksRaycasts = false;
            }
            if (!nativeCaptionHookInstalled) EnforceHiddenNativeEntries();
        }

        private static bool IsWithinNativeLoadLibrary(Transform node)
        {
            for (int i = 0; node != null && i < 13; i++, node = node.parent)
                if (node.name.IndexOf("LoadGameLibraryMenu",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static GameObject FindNativeSaveItemFromCaption(Component text)
        {
            if (text == null) return null;
            for (Transform node = text.transform; node != null;
                node = node.parent)
            {
                if (node.name.IndexOf("ThumbnailListItem",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                    return node.gameObject;
                if (node.name.IndexOf("LoadGameLibraryMenu",
                    StringComparison.OrdinalIgnoreCase) >= 0 &&
                    node.GetComponent<Canvas>() != null)
                    return null;
            }
            return null;
        }

        private GameObject FindIndividualNativeChoice(Component caption,
                                                      bool isMap)
        {
            if (caption == null) return null;
            // Maps are not always ThumbnailListItem prefabs. In the actual
            // SpNewGameStarterMenu they can be reskinned clickable entries.
            // Resolve the FIRST card-owned input surface, never a shared list.
            if (isMap)
            {
                GameObject mapCard = FindNativeMapCardFromCaption(caption);
                if (mapCard != null) return mapCard;
            }
            for (Transform node = caption.transform; node != null;
                node = node.parent)
            {
                string name = node.name ?? "";
                // Only actual native Brix card prefabs may be masked.
                // FindNativeChoiceRoot() previously climbed into a shared
                // parent before all sibling labels existed, hiding the entire
                // mode/map library permanently.
                if (name.IndexOf("GamemodeButton",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Btn_DblTxt_RightIcon",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    return node.gameObject;
                if (isMap &&
                    name.IndexOf("ThumbnailListItem",
                        StringComparison.OrdinalIgnoreCase) >= 0 &&
                    IsWithinNativeNewGameLibrary(node))
                    return node.gameObject;
                if (node.GetComponent<Canvas>() != null) break;
            }
            return null;
        }

        private static string NormalizeNativeMapCaption(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var result = new System.Text.StringBuilder(value.Length);
            foreach (char ch in value.ToLowerInvariant().Normalize(
                System.Text.NormalizationForm.FormD))
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) ==
                    System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (Char.IsLetterOrDigit(ch)) result.Append(ch);
            }
            return result.ToString();
        }

        private bool TryResolveNativeMapCaption(string caption, out string mapId)
        {
            if (nativeMapLabels.TryGetValue(NormalizeNativeLabel(caption), out mapId))
                return true;
            // Older Brix screens may display a beautified asset folder title
            // rather than the GetAssetLabel translation. Match ONLY against
            // the catalog of paths actually verified in this native session.
            string title = NormalizeNativeMapCaption(caption);
            if (title.Length < 4) { mapId = null; return false; }
            string found = null;
            foreach (string key in observedMapAssets.Keys)
            {
                int colon = key.IndexOf(':');
                string folder = colon < 0 ? key : key.Substring(colon + 1);
                if (!String.Equals(title, NormalizeNativeMapCaption(folder),
                    StringComparison.Ordinal)) continue;
                if (found != null && !String.Equals(found, key,
                    StringComparison.OrdinalIgnoreCase))
                {
                    mapId = null;
                    return false;
                }
                found = key;
            }
            mapId = found;
            return found != null;
        }

        private GameObject FindNativeMapCardFromCaption(Component caption)
        {
            if (caption == null ||
                !IsWithinNativeNewGameLibrary(caption.transform)) return null;
            Type textType = FindRuntimeType("UnityEngine.UI.Text");
            if (textType == null) return null;
            for (Transform node = caption.transform.parent; node != null;
                 node = node.parent)
            {
                if (node.GetComponent<Canvas>() != null) break;
                RectTransform rect = node as RectTransform;
                if (rect == null || rect.rect.height > 520f) break;

                // A shared library/list might have only one child populated
                // at this precise Text.text callback. Do not identify a
                // shared container by absence of sibling captions. Instead
                // require an interactive component OWNED by the item itself.
                bool ownsInput = false;
                foreach (Component own in node.GetComponents<Component>())
                {
                    if (own == null) continue;
                    string type = own.GetType().Name.ToLowerInvariant();
                    if (type.Contains("button") || type.Contains("toggle") ||
                        type.Contains("eventtrigger") || type.Contains("click"))
                    {
                        ownsInput = true;
                        break;
                    }
                }
                if (!ownsInput) continue;
                // Even if prefab naming changes, a map caption must map to
                // only this one native asset within its own interactive card.
                var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Component label in node.GetComponentsInChildren(textType, true))
                {
                    string value = GetUiProperty(label, "text") as string;
                    string mapId;
                    if (TryResolveNativeMapCaption(value, out mapId))
                        maps.Add(mapId);
                    if (maps.Count > 1) break;
                }
                if (maps.Count <= 1) return node.gameObject;
                break;
            }
            return null;
        }

        private static bool IsWithinNativeNewGameLibrary(Transform node)
        {
            for (int depth = 0; node != null && depth < 14;
                 depth++, node = node.parent)
                if (node.name.IndexOf("NewGameLibraryMenu",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    node.name.IndexOf("NewGameStarterMenu",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private void ApplyNativeCaptionBeforeRender(Component text)
        {
            if (!titleSceneSeen || evaluatingNativeCaption ||
                text == null || text.gameObject == null ||
                !apSavePolicyPrepared) return;

            string caption = GetUiProperty(text, "text") as string;
            if (String.IsNullOrEmpty(caption) || caption.Length > 180)
                return;
            evaluatingNativeCaption = true;
            try
            {
                if (IsWithinNativeLoadLibrary(text.transform))
                {
                    string folder = SaveFolderForCaption(caption,
                        cachedSaveFolderNames);
                    if (folder == null) return;
                    GameObject card = FindNativeSaveItemFromCaption(text);
                    if (card == null)
                    {
                        if (!observedSaveCardWithoutTag)
                        {
                            observedSaveCardWithoutTag = true;
                            Logger.LogWarning("[AP UI NATIVE] Save caption has no " +
                                "ThumbnailListItem card: " + folder);
                        }
                        return;
                    }
                    // On pooled cards the label may change from a forbidden
                    // save to an allowed one without ever changing Active.
                    // Use CanvasGroup instead of SetActive(false): Brix must
                    // be free to finish constructing/recycling the card.
                    nativeSaveCardFolders[card] = folder;
                    if (cachedAllowedSaveNames.Contains(folder))
                    {
                        ShowPreRenderCard(card);
                        if (nativeCaptionDecisions++ < 20)
                            Logger.LogInfo("[AP UI PRE-RENDER] Allowed save: " + folder);
                    }
                    else
                    {
                        HidePreRenderCard(card);
                        if (nativeCaptionDecisions++ < 20)
                            Logger.LogInfo("[AP UI PRE-RENDER] Off-seed save hidden: " +
                                folder);
                    }
                    return;
                }

                if (!IsArchipelagoSaveSessionActive()) return;

                string selectedMode = SelectedNativeGamemode();
                string nativeMode = NativeModeFromLabel(caption);
                string mapId;
                bool isMap = TryResolveNativeMapCaption(caption, out mapId);
                bool forbiddenMode = IsSandboxLabel(caption) ||
                    (nativeMode != null && mode != "Hybrid" &&
                     nativeMode != mode);
                bool forbiddenPlay = IsForbiddenPlaySubmenuEntry(caption);
                bool forbiddenMap = isMap &&
                    !AllowedSeedMap(mapId, selectedMode);
                if (IsAllowedPlaySubmenuEntry(caption))
                {
                    // A recycled editor/wonders card may become the native
                    // New Game / Load / Multiplayer control. Restore only
                    // on a known permitted ACTION label, never on arbitrary
                    // subtitle text from a forbidden card.
                    GameObject allowedAction =
                        FindIndividualNativeChoice(text, false);
                    if (allowedAction != null) ShowPreRenderCard(allowedAction);
                    return;
                }

                if (!forbiddenMode && !forbiddenPlay && !isMap) return;
                GameObject cardRoot = FindIndividualNativeChoice(text, isMap);
                if (isMap && nativeMapCardDiagnostics++ < 24)
                    Logger.LogInfo("[AP MAP UI] id=" + mapId +
                        " allowed=" + !forbiddenMap +
                        " item=" + (cardRoot == null ? "not-found" : cardRoot.name) +
                        " context=" + IsWithinNativeNewGameLibrary(text.transform));
                if (cardRoot == null) return;

                // Do not evaluate gamemode labels on an unrelated shared
                // parent or map card. An unknown/incomplete label is never
                // grounds for masking the menu as a whole.
                string cardName = cardRoot.name ?? "";
                bool isModeCard = cardName.IndexOf("GamemodeButton",
                    StringComparison.OrdinalIgnoreCase) >= 0;
                bool isPlayCard = cardName.IndexOf("Btn_DblTxt_RightIcon",
                    StringComparison.OrdinalIgnoreCase) >= 0;
                bool isMapCard = isMap &&
                    IsWithinNativeNewGameLibrary(cardRoot.transform) &&
                    !isModeCard;
                bool shouldHide =
                    (isModeCard && forbiddenMode) ||
                    (isPlayCard && forbiddenPlay) ||
                    (isMapCard && forbiddenMap);
                if (shouldHide)
                {
                    HidePreRenderCard(cardRoot);
                    if (nativeCaptionDecisions++ < 20)
                        Logger.LogInfo("[AP UI PRE-RENDER] Individual card hidden: " +
                            NormalizeNativeLabel(caption) + " / " + cardName);
                }
                else if ((isModeCard && nativeMode != null) ||
                         (isMapCard && isMap))
                    ShowPreRenderCard(cardRoot);
            }
            catch (Exception error)
            {
                if (nativeCaptionDecisions++ < 10)
                    Logger.LogWarning("[AP UI NATIVE] Caption policy error: " +
                        error.GetType().Name);
            }
            finally { evaluatingNativeCaption = false; }
        }

        private void ApplyPrecomputedPolicyToVisibleTitles()
        {
            if (!nativeCaptionHookInstalled || !titleSceneSeen) return;
            // ONLY on seed/login transitions, never each submenu navigation.
            Type textType = FindRuntimeType("UnityEngine.UI.Text");
            if (textType == null) return;
            int evaluated = 0;
            foreach (UnityEngine.Object obj in
                UnityEngine.Object.FindObjectsOfType(textType))
            {
                Component label = obj as Component;
                if (label == null || !label.gameObject.activeInHierarchy)
                    continue;
                ApplyNativeCaptionBeforeRender(label);
                evaluated++;
            }
            Logger.LogInfo("[AP UI PREPARED] One-time connection policy " +
                "applied to " + evaluated + " existing titles; future cards " +
                "are intercepted before render.");
        }
    }
}
