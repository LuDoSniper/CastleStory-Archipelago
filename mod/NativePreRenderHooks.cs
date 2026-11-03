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
            Instance.ApplyNativeCaptionBeforeRender(__instance as Component);
        }

        private static void ObserveNativeTextEnabled(object __instance)
        {
            if (Instance == null || !Instance.nativeCaptionHookInstalled)
                return;
            Instance.ApplyNativeCaptionBeforeRender(__instance as Component);
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
                if (allowed)
                {
                    bool wasHidden = hiddenNativeEntries.ContainsKey(card);
                    hiddenNativeEntries.Remove(card);
                    if (wasHidden && !card.activeSelf) card.SetActive(true);
                }
                else HideNativeEntry(card);
            }
        }

        private void ApplyKnownNativeRestrictionsBeforeRender()
        {
            // Constant work proportional to the small set of disallowed cards,
            // not to the total Unity UI. No FindObjectsOfType or reflection.
            EnforceHiddenNativeEntries();
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
                    // The save policy was computed when the seed became ready.
                    // The load screen is populated later: classify each card
                    // at the instant Brix assigns its native title.
                    string folder = SaveFolderForCaption(caption, cachedSaveFolderNames);
                    if (folder == null) return;
                    GameObject card = FindNativeSaveItemFromCaption(text);
                    if (card == null)
                    {
                        if (!observedSaveCardWithoutTag)
                        {
                            observedSaveCardWithoutTag = true;
                            Logger.LogWarning("[AP UI NATIVE] Save caption has no " +
                                "native ThumbnailListItem parent: " + folder);
                        }
                        return;
                    }
                    nativeSaveCardFolders[card] = folder;
                    if (!cachedAllowedSaveNames.Contains(folder))
                    {
                        HideNativeEntry(card);
                        if (nativeCaptionDecisions++ < 16)
                            Logger.LogInfo("[AP UI PRE-RENDER] Off-seed save hidden: " +
                                folder);
                    }
                    return;
                }
                if (!IsArchipelagoSaveSessionActive()) return;

                string selectedMode = SelectedNativeGamemode();
                string nativeMode = NativeModeFromLabel(caption);
                string mapId;
                bool forbidden = IsSandboxLabel(caption) ||
                    IsForbiddenPlaySubmenuEntry(caption) ||
                    (nativeMode != null && mode != "Hybrid" && nativeMode != mode) ||
                    (nativeMapLabels.TryGetValue(
                        NormalizeNativeLabel(caption), out mapId) &&
                     !AllowedSeedMap(mapId, selectedMode));
                if (!forbidden || IsAllowedPlaySubmenuEntry(caption))
                    return;
                GameObject choice = FindNativeChoiceRoot(text);
                if (choice == null) return;
                HideNativeEntry(choice);
                if (nativeCaptionDecisions++ < 16)
                    Logger.LogInfo("[AP UI PRE-RENDER] Native choice hidden: " +
                        NormalizeNativeLabel(caption));
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
