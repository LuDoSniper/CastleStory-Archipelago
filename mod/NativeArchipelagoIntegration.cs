using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryArchipelago
{
    public sealed partial class CastleStoryArchipelagoPlugin
    {
        // The main menu button is a clone of a real Unity UI control, not an
        // IMGUI control drawn over the main menu.
        private GameObject nativeArchipelagoButton;
        private Font nativeMenuFont;
        private readonly Dictionary<RectTransform, Vector2> shiftedMenuEntries =
            new Dictionary<RectTransform, Vector2>();
        private readonly Dictionary<GameObject, bool> hiddenNativeEntries =
            new Dictionary<GameObject, bool>();
        private readonly Dictionary<string, string> nativeMapLabels =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private float nextNativeUiRefresh;
        // Never walk every Unity button on every Update. Menu hooks schedule
        // a small, bounded number of passes after the native UI is built.
        private float nextMainMenuProbeAt;
        private int mainMenuProbeCount;
        private int pendingFilterPasses;
        private float nextFilterPassAt;
        private bool nativeButtonMissingLogged;
        private int nativeRestrictionLogCount;
        private string lastNativeFilterKey;
        private string lastFilterMode;
        private object lastFilterMenu;
        private bool introCreditShown;
        private bool introCreditActive;
        private float introCreditStart = -1f;
        private float titleSceneReadyAt = -1f;
        private bool titleSceneSeen;
        private bool titleSceneEvidenceLogged;
        private readonly Dictionary<GameObject, List<string>> cachedNativeLabels =
            new Dictionary<GameObject, List<string>>();
        private GUIStyle introCreditStyle;

        private static string NormalizeNativeLabel(string text)
        {
            return (text ?? "").Trim().ToLowerInvariant();
        }

        private static bool IsPlayLabel(string label)
        {
            string normalized = NormalizeNativeLabel(label);
            return normalized == "play" || normalized == "jouer" ||
                   normalized == "spielen" || normalized == "jugar" ||
                   normalized == "button_play" || normalized == "btnplay" ||
                   normalized == "playbutton" || normalized == "mainmenuplay";
        }

        private static bool IsSandboxLabel(string label)
        {
            string normalized = NormalizeNativeLabel(label);
            // Includes prefab identifiers such as "SandboxModeButton".
            if (normalized.Contains("sandbox") ||
                normalized.Contains("freebuild") ||
                normalized.Contains("free_build") ||
                normalized.Contains("construction libre"))
                return true;
            switch (normalized)
            {
                case "sandbox": case "creative": case "creative mode":
                case "free build": case "freebuild": case "free building":
                case "construction libre": case "mode libre":
                case "bac à sable": case "bac a sable":
                    return true;
                default: return false;
            }
        }

        private static string NativeModeFromLabel(string label)
        {
            string value = NormalizeNativeLabel(label);
            if (value == "invasion" || value == "mode_invasion" ||
                value == "invasionmode" || value.Contains("invasionmode") ||
                value.Contains("mode_invasion")) return "Invasion";
            if (value == "conquest" || value == "conquête" ||
                value == "mode_conquest" || value == "conquestmode" ||
                value.Contains("conquestmode") ||
                value.Contains("mode_conquest"))
                return "Conquest";
            return null;
        }

        private static PropertyInfo RuntimeTextProperty(Type type)
        {
            return type == null ? null : type.GetProperty("text",
                BindingFlags.Instance | BindingFlags.Public);
        }

        private static List<string> NativeUiLabels(GameObject obj)
        {
            var labels = new List<string>();
            if (obj == null) return labels;
            // Castle Story Unity 5.6 uses UnityEngine.UI.Text. Resolving
            // TMPro.TextMeshProUGUI on every tile emitted hundreds of
            // warnings and spent 100-200 ms per UI filter pass.
            foreach (string typeName in new [] { "UnityEngine.UI.Text" })
            {
                Type textType = FindRuntimeType(typeName);
                if (textType == null) continue;
                PropertyInfo property = RuntimeTextProperty(textType);
                if (property == null) continue;
                foreach (Component text in obj.GetComponentsInChildren(textType, true))
                {
                    try
                    {
                        string value = property.GetValue(text, null) as string;
                        if (!String.IsNullOrEmpty(value))
                            labels.Add(value.Trim());
                    }
                    catch (Exception) { }
                }
            }
            // Some prefab buttons expose only a native object name.
            if (!String.IsNullOrEmpty(obj.name)) labels.Add(obj.name);
            return labels;
        }

        private List<string> CachedNativeUiLabels(GameObject obj)
        {
            List<string> result;
            if (obj != null && cachedNativeLabels.TryGetValue(obj, out result))
                return result;
            result = NativeUiLabels(obj);
            if (obj != null) cachedNativeLabels[obj] = result;
            return result;
        }

        private static string NativeUiLabel(GameObject obj)
        {
            List<string> labels = NativeUiLabels(obj);
            return labels.Count == 0 ? null : labels[0];
        }

        private static bool SetNativeUiLabel(GameObject obj, string value)
        {
            if (obj == null) return false;
            Component fallback = null;
            PropertyInfo fallbackProperty = null;
            // Castle Story Unity 5.6 uses UnityEngine.UI.Text. Resolving
            // TMPro.TextMeshProUGUI on every tile emitted hundreds of
            // warnings and spent 100-200 ms per UI filter pass.
            foreach (string typeName in new [] { "UnityEngine.UI.Text" })
            {
                Type textType = FindRuntimeType(typeName);
                if (textType == null) continue;
                PropertyInfo textProp = RuntimeTextProperty(textType);
                if (textProp == null || !textProp.CanWrite) continue;
                foreach (Component text in obj.GetComponentsInChildren(textType, true))
                {
                    try
                    {
                        string current = textProp.GetValue(text, null) as string;
                        if (String.IsNullOrEmpty(current)) continue;
                        if (IsPlayLabel(current))
                        {
                            textProp.SetValue(text, value, null);
                            return true;
                        }
                        if (fallback == null)
                        {
                            fallback = text;
                            fallbackProperty = textProp;
                        }
                    }
                    catch (Exception) { }
                }
            }
            // A localized Play button may have a native prefab name
            // "Button_Play" but a translated or dynamically bound caption.
            if (fallback != null && fallbackProperty != null)
            {
                try
                {
                    fallbackProperty.SetValue(fallback, value, null);
                    return true;
                }
                catch (Exception) { }
            }
            return false;
        }

        private void CaptureNativeMenuFont(GameObject source)
        {
            Type type = FindRuntimeType("UnityEngine.UI.Text");
            if (type == null || source == null) return;
            PropertyInfo property = type.GetProperty("font");
            if (property == null) return;
            foreach (Component label in source.GetComponentsInChildren(type, true))
            {
                try
                {
                    nativeMenuFont = property.GetValue(label, null) as Font;
                    if (nativeMenuFont == null) continue;
                    ApplyNativeTypography();
                    return;
                }
                catch (Exception) { }
            }
        }

        private void ApplyNativeTypography()
        {
            if (nativeMenuFont == null) return;
            foreach (GUIStyle style in new [] {
                titleStyle, subTitleStyle, bodyStyle, mutedStyle,
                miniStyle, eyebrowStyle, errorStyle, inputLabelStyle,
                primaryButton, secondaryButton, subtleButton, inputStyle })
                if (style != null) style.font = nativeMenuFont;
        }

        private void OpenArchipelagoFromNativeMenu()
        {
            if (ShowNativeConnectionPanel())
            {
                apMenuOpen = false;
                Logger.LogInfo("[AP NATIVE UI] Opened native Archipelago connection panel.");
            }
            else
            {
                apMenuOpen = true;
                Logger.LogWarning("[AP NATIVE UI] Using legacy login fallback: " +
                    nativePanelDiagnostic);
            }
        }

        private bool InstallNativeButtonListener(Component clonedButton, Type buttonType,
                                                 string methodName)
        {
            try
            {
                var clickProperty = buttonType.GetProperty("onClick");
                if (clickProperty == null) return false;
                Type eventType = clickProperty.PropertyType;
                object click = Activator.CreateInstance(eventType);
                Type actionType = FindRuntimeType("UnityEngine.Events.UnityAction");
                if (actionType == null) return false;
                MethodInfo handler = GetType().GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Delegate listener = Delegate.CreateDelegate(actionType, this, handler);
                var addListener = eventType.GetMethod("AddListener",
                    new [] { actionType });
                if (addListener == null) return false;
                addListener.Invoke(click, new object[] { listener });
                if (clickProperty.CanWrite)
                    clickProperty.SetValue(clonedButton, click, null);
                else
                {
                    // No cloned Play/Start listener may remain attached!
                    var field = buttonType.GetField("m_OnClick",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field == null) return false;
                    field.SetValue(clonedButton, click);
                }
                return true;
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP NATIVE UI] Button listener unavailable: " +
                    error.Message);
                return false;
            }
        }

        private static bool IsOptionsLabel(string label)
        {
            string normalized = NormalizeNativeLabel(label);
            return normalized == "options" || normalized == "option" ||
                normalized == "settings" || normalized == "paramètres" ||
                normalized == "parametres" || normalized == "einstellungen";
        }

        private static bool HasNativeOptionsSibling(GameObject play)
        {
            // Castle Story can nest the Play and Options controls under
            // separate layout groups. Requiring the same immediate parent
            // caused the main menu button (and intro) never to appear.
            if (play == null) return false;
            Type type = FindRuntimeType("UnityEngine.UI.Button");
            if (type == null) return false;
            Transform ancestor = play.transform.parent;
            for (int level = 0; level < 4 && ancestor != null; level++,
                ancestor = ancestor.parent)
            {
                foreach (Component button in ancestor.GetComponentsInChildren(type, true))
                {
                    if (button == null || button.gameObject == play) continue;
                    foreach (string label in NativeUiLabels(button.gameObject))
                        if (IsOptionsLabel(label)) return true;
                }
            }
            return false;
        }

        private bool IsNativeTitleReady()
        {
            if (!titleSceneSeen || nativeStarterMenu != null ||
                observedMapAssets.Count > 0) return false;
            // The proprietary studio splash animation length is not in this
            // repository. Detect the transition to the actual title choices,
            // rather than guessing "4 seconds after SceneMenu".
            Type textType = FindRuntimeType("UnityEngine.UI.Text");
            if (textType == null) return false;
            bool play = false, options = false;
            foreach (UnityEngine.Object obj in
                UnityEngine.Object.FindObjectsOfType(textType))
            {
                Component component = obj as Component;
                if (component == null || !component.gameObject.activeInHierarchy)
                    continue;
                string value = GetUiProperty(component, "text") as string;
                if (IsPlayLabel(value)) play = true;
                else if (IsOptionsLabel(value)) options = true;
                if (play && options) return true;
            }
            return false;
        }

        private void BeginOwnIntroCredit()
        {
            // Start only AFTER Play+Options are present, which marks the end
            // of the studio splash sequence. Never overlay a studio logo.
            if (introCreditShown || !IsNativeTitleReady())
                return;
            introCreditShown = true;
            introCreditActive = true;
            introCreditStart = Time.realtimeSinceStartup;
            Logger.LogInfo("[AP CREDITS] Title Play+Options detected after " +
                (Time.realtimeSinceStartup - titleSceneReadyAt).ToString("F2") +
                "s in SceneMenu. Dedicated Archipelago credit started.");
        }

        private RectTransform FindTitleChoiceRect(Component label)
        {
            if (label == null) return null;
            RectTransform fallback = label.transform.parent as RectTransform;
            Transform ancestor = label.transform.parent;
            for (int depth = 0; ancestor != null && depth < 4; depth++,
                 ancestor = ancestor.parent)
            {
                RectTransform rect = ancestor as RectTransform;
                if (rect == null) continue;
                if (rect.rect.width >= 150f && rect.rect.height >= 32f &&
                    rect.rect.height <= 170f)
                    return rect;
            }
            return fallback;
        }

        private void ShiftTitleOptionsBelow(RectTransform play, RectTransform inserted)
        {
            if (play == null || inserted == null) return;
            Type textType = FindRuntimeType("UnityEngine.UI.Text");
            if (textType == null) return;
            foreach (UnityEngine.Object value in UnityEngine.Object.FindObjectsOfType(textType))
            {
                Component label = value as Component;
                if (label == null || !label.gameObject.activeInHierarchy)
                    continue;
                if (!IsOptionsLabel(GetUiProperty(label, "text") as string))
                    continue;
                RectTransform option = FindTitleChoiceRect(label);
                if (option == null || option == play ||
                    shiftedMenuEntries.ContainsKey(option) ||
                    option == inserted ||
                    option.IsChildOf(inserted))
                    continue;
                // Options may be under a DIFFERENT layout container.
                // Shift its own rect instead of only Play's sibling list.
                float shift = Mathf.Max(58f, play.rect.height + 16f);
                shiftedMenuEntries[option] = option.anchoredPosition;
                option.anchoredPosition += new Vector2(0, -shift);
                Logger.LogInfo("[AP TITLE] Moved Options down by " + shift +
                    " UI units. optionsParent=" +
                    (option.parent == null ? "root" : option.parent.name) +
                    " playParent=" +
                    (play.parent == null ? "root" : play.parent.name));
                return;
            }
            Logger.LogWarning("[AP TITLE] Options control not found; unable to reposition.");
        }

        private void MatchTitlePrimaryVisual(RectTransform nativePlay, GameObject target)
        {
            if (nativePlay == null || target == null) return;
            Type imageType = FindRuntimeType("UnityEngine.UI.Image");
            if (imageType == null) return;
            Component targetImage = target.GetComponent(imageType);
            if (targetImage == null) return;
            Transform node = nativePlay;
            for (int depth = 0; node != null && depth < 3; depth++,
                 node = node.parent)
            {
                Component source = node.GetComponent(imageType);
                if (source == null) continue;
                try
                {
                    foreach (string prop in new [] {
                        "sprite", "color", "type", "fillCenter", "material" })
                    {
                        PropertyInfo property = imageType.GetProperty(prop);
                        if (property != null && property.CanRead &&
                            property.CanWrite)
                            property.SetValue(targetImage,
                                property.GetValue(source, null), null);
                    }
                    Logger.LogInfo("[AP TITLE] Cloned primary Play visual from " +
                        node.name);
                    return;
                }
                catch (Exception error)
                {
                    Logger.LogWarning("[AP TITLE] Primary visual fallback: " +
                        error.GetType().Name);
                    break;
                }
            }
            Logger.LogInfo("[AP TITLE] No native UGUI primary Image; using warm gold fallback.");
        }

        private bool TryInstallTitleEntryFromText()
        {
            // SceneMenu's initial two controls do not expose the UGUI Button
            // component in the supplied runtime logs. Fall back to the REAL
            // title Play text/rect, rather than cloning a later map Play.
            Type textType = FindRuntimeType("UnityEngine.UI.Text");
            Type buttonType = FindRuntimeType("UnityEngine.UI.Button");
            if (textType == null || buttonType == null) return false;
            foreach (UnityEngine.Object instance in
                UnityEngine.Object.FindObjectsOfType(textType))
            {
                Component label = instance as Component;
                if (label == null || !label.gameObject.activeInHierarchy)
                    continue;
                string caption = GetUiProperty(label, "text") as string;
                if (!IsPlayLabel(caption)) continue;
                RectTransform playRect = FindTitleChoiceRect(label);
                if (playRect == null || playRect.parent == null) continue;
                // Do not insert when a map editor/starter is on screen.
                if (nativeStarterMenu != null || observedMapAssets.Count > 0)
                    return false;
                GameObject copy = NewNativeUiObject(
                    "Archipelago_NativeTitleEntry", playRect.parent,
                    new Vector2(Mathf.Max(230f, playRect.rect.width),
                                Mathf.Max(48f, playRect.rect.height)),
                    playRect.anchoredPosition +
                        new Vector2(0, -Mathf.Max(52f, playRect.rect.height + 9f)));
                AddUiImage(copy, new Color(.94f, .76f, .25f, 1f), true);
                MatchTitlePrimaryVisual(playRect, copy);
                Component button = copy.AddComponent(buttonType);
                if (!InstallNativeButtonListener(button, buttonType,
                        "OpenArchipelagoFromNativeMenu"))
                {
                    UnityEngine.Object.Destroy(copy);
                    return false;
                }
                Component text = CloneNativeText(label.gameObject,
                    copy.transform, "ARCHIPELAGO",
                    new Vector2(Mathf.Max(210f, playRect.rect.width - 24f), 44),
                    Vector2.zero, 23);
                if (text == null)
                {
                    UnityEngine.Object.Destroy(copy);
                    return false;
                }
                SetUiProperty(text, "alignment", TextAnchor.MiddleCenter);
                copy.transform.SetSiblingIndex(playRect.GetSiblingIndex() + 1);
                // Move the real Options control, even if nested elsewhere.
                ShiftTitleOptionsBelow(playRect, (RectTransform)copy.transform);
                nativeArchipelagoButton = copy;
                mainMenuPlayTemplate = copy;
                nativeMenuFont = GetUiProperty(label, "font") as Font;
                Logger.LogInfo("[AP TITLE] Inserted primary Archipelago option under " +
                    "the actual SceneMenu Play label; synthetic UGUI button with native font.");
                return true;
            }
            return false;
        }

        private void TryInstallNativeArchipelagoButton()
        {
            if (nativeArchipelagoButton != null) return;
            // Never clone the Play control from map selection. Runtime logs
            // prove the previous injection happened only after entering
            // SpNewGameStarterMenu, which created an entry in the wrong menu.
            if (!titleSceneSeen || nativeStarterMenu != null ||
                observedMapAssets.Count > 0) return;
            if (TryInstallTitleEntryFromText()) return;
            Type buttonType = FindRuntimeType("UnityEngine.UI.Button");
            if (buttonType == null) return;
            try
            {
                foreach (UnityEngine.Object instance in
                    UnityEngine.Object.FindObjectsOfType(buttonType))
                {
                    Component button = instance as Component;
                    if (button == null || button.gameObject == null ||
                        !button.gameObject.activeInHierarchy) continue;
                    string name = null;
                    foreach (string label in NativeUiLabels(button.gameObject))
                        if (IsPlayLabel(label)) { name = label; break; }
                    if (name == null) continue;
                    GameObject source = button.gameObject;
                    // Play can be nested separately from Options; this is
                    // only an advisory confidence check, not a hard gate.
                    bool optionsFound = HasNativeOptionsSibling(source);
                    if (!optionsFound)
                        Logger.LogInfo("[AP NATIVE UI] Play located without same-group Options; trying native insertion.");
                    mainMenuPlayTemplate = source;
                    Type buttonClass = FindRuntimeType("UnityEngine.UI.Button");
                    if (source.transform.parent != null)
                        foreach (Component sibling in source.transform.parent.GetComponentsInChildren(buttonClass, true))
                            foreach (string label in NativeUiLabels(sibling.gameObject))
                                if (IsOptionsLabel(label))
                                    mainMenuOptionsTemplate = sibling.gameObject;
                    GameObject copy = UnityEngine.Object.Instantiate(source)
                        as GameObject;
                    if (copy == null) return;
                    copy.name = "Archipelago_NativeMainMenuEntry";
                    copy.SetActive(false);
                    copy.transform.SetParent(source.transform.parent, false);
                    Component cloneButton = copy.GetComponent(buttonType);
                    if (cloneButton == null ||
                        !InstallNativeButtonListener(cloneButton, buttonType,
                            "OpenArchipelagoFromNativeMenu") ||
                        !SetNativeUiLabel(copy, "Archipelago"))
                    {
                        UnityEngine.Object.Destroy(copy);
                        continue;
                    }
                    copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
                    // A native VerticalLayoutGroup positions sibling buttons
                    // automatically; the anchor fallback covers fixed menus.
                    var layout = FindRuntimeType("UnityEngine.UI.VerticalLayoutGroup");
                    bool hasLayout = layout != null &&
                        source.transform.parent != null &&
                        source.transform.parent.GetComponent(layout) != null;
                    if (!hasLayout)
                    {
                        var srcRect = source.transform as RectTransform;
                        var dstRect = copy.transform as RectTransform;
                        if (srcRect != null && dstRect != null)
                        {
                            float delta = Mathf.Max(45f, srcRect.rect.height + 8f);
                            dstRect.anchoredPosition = srcRect.anchoredPosition +
                                new Vector2(0, -delta);
                            // In manually positioned menus, make room below
                            // Play rather than overlapping the next button.
                            Transform parent = source.transform.parent;
                            if (parent != null)
                                for (int i = 0; i < parent.childCount; i++)
                                {
                                    var child = parent.GetChild(i) as RectTransform;
                                    if (child == null || child == dstRect ||
                                        child == srcRect || child.GetSiblingIndex() <=
                                        srcRect.GetSiblingIndex() + 1) continue;
                                    if (child.anchoredPosition.y <=
                                        srcRect.anchoredPosition.y - 5f)
                                    {
                                        shiftedMenuEntries[child] = child.anchoredPosition;
                                        child.anchoredPosition += new Vector2(0, -delta);
                                    }
                                }
                        }
                    }
                    copy.SetActive(true);
                    nativeArchipelagoButton = copy;
                    CaptureNativeMenuFont(source);
                    Logger.LogInfo("[AP NATIVE UI] Created Archipelago entry after '" +
                        name + "' using its native Unity UI prefab.");
                    return;
                }
                if (!nativeButtonMissingLogged && Time.realtimeSinceStartup > 12f)
                {
                    nativeButtonMissingLogged = true;
                    Logger.LogWarning("[AP NATIVE UI] No supported native Play button found. " +
                        "F7 opens Archipelago; F9 reports native UI type details.");
                }
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP NATIVE UI] Button integration: " + error);
            }
        }

        private void RestoreNativeMenuLayout()
        {
            foreach (var item in shiftedMenuEntries)
                if (item.Key != null) item.Key.anchoredPosition = item.Value;
            shiftedMenuEntries.Clear();
        }

        private void RestoreNativeEntries()
        {
            foreach (var entry in hiddenNativeEntries)
                if (entry.Key != null) entry.Key.SetActive(entry.Value);
            hiddenNativeEntries.Clear();
            lastNativeFilterKey = null;
            lastFilterMenu = null;
            lastFilterMode = null;
        }

        private void HideNativeEntry(GameObject button)
        {
            if (button == null || hiddenNativeEntries.ContainsKey(button)) return;
            hiddenNativeEntries[button] = button.activeSelf;
            button.SetActive(false);
        }

        private bool AllowedSeedMap(string mapId, string selectedMode)
        {
            if (campaign == null || campaign.missions == null) return false;
            foreach (MissionFile mission in campaign.missions)
                if (mission != null &&
                    String.Equals(MissionMapId(mission), mapId,
                                  StringComparison.OrdinalIgnoreCase) &&
                    (String.IsNullOrEmpty(selectedMode) ||
                     String.Equals(mission.mode, selectedMode,
                                   StringComparison.OrdinalIgnoreCase)))
                    return true;
            return false;
        }

        private string SelectedNativeGamemode()
        {
            if (nativeStarterMenu == null) return mode == "Hybrid" ? null : mode;
            try
            {
                var selected = AccessTools.Property(
                    nativeStarterMenu.GetType(), "SelectedGamemode");
                object value = selected == null ? null :
                    selected.GetValue(nativeStarterMenu, null);
                string code = ModeIdentity(value);
                return code == "Invasion" || code == "Conquest" ?
                    code : mode == "Hybrid" ? null : mode;
            }
            catch (Exception) { return mode == "Hybrid" ? null : mode; }
        }

        private void ObserveNativeMapLabel(object map, string label)
        {
            if (String.IsNullOrEmpty(label)) return;
            string mapId = MapIdentity(map);
            if (String.IsNullOrEmpty(mapId)) return;
            nativeMapLabels[NormalizeNativeLabel(label)] = mapId;
        }

        private static int TransformDepth(Transform node)
        {
            int depth = 0;
            while (node != null && depth < 100)
            {
                depth++;
                node = node.parent;
            }
            return depth;
        }

        private static bool IsUnderNativeMenu(Transform child, Transform group)
        {
            while (child != null)
            {
                if (child == group) return true;
                child = child.parent;
            }
            return false;
        }

        private static Transform FindPlayMenuRoot(
            List<Component> controls,
            Dictionary<GameObject, List<string>> labels)
        {
            // Button wrappers often differ: the native menu choices need not
            // share an immediate parent. Find the deepest shared ancestor of
            // at least two New Game / Load / Multiplayer controls.
            var counts = new Dictionary<Transform, int>();
            foreach (Component control in controls)
            {
                if (control == null || control.gameObject == null) continue;
                List<string> names;
                if (!labels.TryGetValue(control.gameObject, out names)) continue;
                bool anchor = false;
                foreach (string label in names)
                    if (IsSubmenuAnchor(label)) { anchor = true; break; }
                if (!anchor) continue;
                Transform parent = control.transform.parent;
                for (int depth = 0; parent != null && depth < 6; depth++,
                     parent = parent.parent)
                {
                    int n;
                    counts.TryGetValue(parent, out n);
                    counts[parent] = n + 1;
                }
            }
            Transform best = null;
            int maxDepth = -1;
            foreach (var candidate in counts)
                if (candidate.Value >= 2 && candidate.Key != null &&
                    candidate.Key.GetComponent<Canvas>() == null)
                {
                    int depth = TransformDepth(candidate.Key);
                    if (depth > maxDepth)
                    {
                        maxDepth = depth;
                        best = candidate.Key;
                    }
                }
            return best;
        }

        private static bool IsKnownNativeChoice(string label)
        {
            return IsSandboxLabel(label) || NativeModeFromLabel(label) != null ||
                IsForbiddenPlaySubmenuEntry(label) ||
                IsAllowedPlaySubmenuEntry(label);
        }

        private static int CountDistinctNativeChoices(Transform node)
        {
            Type textType = FindRuntimeType("UnityEngine.UI.Text");
            if (node == null || textType == null) return 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Component text in node.GetComponentsInChildren(textType, true))
            {
                string value = GetUiProperty(text, "text") as string;
                if (IsKnownNativeChoice(value))
                {
                    seen.Add(NormalizeNativeLabel(value));
                    if (seen.Count >= 2) break;
                }
            }
            return seen.Count;
        }

        private static bool HasNativeInteractiveSurface(Transform node)
        {
            if (node == null) return false;
            foreach (Component component in node.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                string name = component.GetType().Name.ToLowerInvariant();
                if (name.Contains("button") || name.Contains("toggle") ||
                    name.Contains("pointer") || name.Contains("click") ||
                    name.Contains("selectable") || name.Contains("collider") ||
                    name.Contains("eventtrigger") || name.Contains("interact"))
                    return true;
                if (name == "image" &&
                    (GetUiProperty(component, "raycastTarget") is bool) &&
                    (bool)GetUiProperty(component, "raycastTarget"))
                    return true;
            }
            return false;
        }

        private GameObject FindNativeChoiceRoot(Component label)
        {
            if (label == null) return null;
            GameObject best = null;
            Transform node = label.transform.parent;
            // Seek the highest *individual card* containing its text and
            // click target. Hiding just the caption left a clickable empty
            // rectangle in the previous build.
            for (int depth = 0; node != null && depth < 6; depth++,
                 node = node.parent)
            {
                if (node.GetComponent<Canvas>() != null ||
                    node.name.StartsWith("AP_", StringComparison.Ordinal) ||
                    node.name.StartsWith("Archipelago_", StringComparison.Ordinal))
                    break;
                RectTransform rect = node as RectTransform;
                if (rect == null || rect.rect.height > 560f) break;
                if (CountDistinctNativeChoices(node) > 1) break;
                if (HasNativeInteractiveSurface(node))
                    best = node.gameObject;
            }
            return best;
        }

        private int HideNativeNonSelectableChoices(string selectedNativeMode)
        {
            // Some early Castle Story menus are not UGUI Button/Toggle.
            // Target their exact native captions, but hide only a wrapper
            // identified as interactive; never hide an unknown menu root.
            Type type = FindRuntimeType("UnityEngine.UI.Text");
            if (type == null) return 0;
            int count = 0;
            foreach (UnityEngine.Object value in UnityEngine.Object.FindObjectsOfType(type))
            {
                Component caption = value as Component;
                if (caption == null || caption.gameObject == null ||
                    !caption.gameObject.activeInHierarchy) continue;
                string text = GetUiProperty(caption, "text") as string;
                string nativeMode = NativeModeFromLabel(text);
                bool forbidden = IsSandboxLabel(text) ||
                    IsForbiddenPlaySubmenuEntry(text) ||
                    (nativeMode != null && mode != "Hybrid" && nativeMode != mode);
                if (!forbidden) continue;
                GameObject target = FindNativeChoiceRoot(caption);
                if (target == null || hiddenNativeEntries.ContainsKey(target)) continue;
                HideNativeEntry(target);
                count++;
                Logger.LogInfo("[AP NATIVE UI] Hidden entire native choice: " +
                    (nativeMode ?? NormalizeNativeLabel(text)) +
                    " root=" + target.name + " parent=" +
                    (target.transform.parent == null ? "root" :
                        target.transform.parent.name));
            }
            return count;
        }

        private void RefreshNativeSeedMenu()
        {
            if (disconnectPending || String.IsNullOrEmpty(token) ||
                campaign == null || !campaign.ready ||
                campaign.missions == null)
            {
                if (hiddenNativeEntries.Count > 0) RestoreNativeEntries();
                return;
            }

            string selectedNativeMode = SelectedNativeGamemode();
            string filterKey = token + "|" + selectedNativeMode;
            if (lastNativeFilterKey != filterKey)
            {
                // Only a real seed/mode switch can restore hidden controls.
                if (hiddenNativeEntries.Count > 0) RestoreNativeEntries();
                lastNativeFilterKey = filterKey;
            }

            Type selectable = FindRuntimeType("UnityEngine.UI.Selectable");
            if (selectable == null) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var controls = new List<Component>();
            var labels = new Dictionary<GameObject, List<string>>();
            foreach (UnityEngine.Object value in
                UnityEngine.Object.FindObjectsOfType(selectable))
            {
                Component item = value as Component;
                if (item == null || item.gameObject == null ||
                    hiddenNativeEntries.ContainsKey(item.gameObject)) continue;
                controls.Add(item);
                labels[item.gameObject] = CachedNativeUiLabels(item.gameObject);
            }
            Transform playRoot = FindPlayMenuRoot(controls, labels);
            int hidden = 0;
            foreach (Component item in controls)
            {
                if (item == null || item.gameObject == null) continue;
                GameObject go = item.gameObject;
                List<string> names;
                if (!labels.TryGetValue(go, out names)) continue;
                bool inPlaySubmenu = playRoot != null &&
                    IsUnderNativeMenu(go.transform, playRoot);
                bool allowedPlay = false;
                bool forbidden = false;
                foreach (string label in names)
                    if (IsAllowedPlaySubmenuEntry(label)) allowedPlay = true;
                if (inPlaySubmenu && !allowedPlay)
                    forbidden = true;
                foreach (string label in names)
                {
                    string namedMode = NativeModeFromLabel(label);
                    string mapId;
                    if (IsSandboxLabel(label) ||
                        (namedMode != null && mode != "Hybrid" && namedMode != mode) ||
                        (nativeMapLabels.TryGetValue(
                             NormalizeNativeLabel(label), out mapId) &&
                         !AllowedSeedMap(mapId, selectedNativeMode)) ||
                        IsForbiddenPlaySubmenuEntry(label))
                    {
                        forbidden = true;
                        break;
                    }
                }
                if (!forbidden) continue;
                HideNativeEntry(go);
                hidden++;
            }
            hidden += HideNativeNonSelectableChoices(selectedNativeMode);
            watch.Stop();
            if (hidden > 0 && nativeRestrictionLogCount++ < 12)
                Logger.LogInfo("[AP NATIVE UI] Hidden " + hidden +
                    " controls, playGroup=" + (playRoot == null ? "not-found" : playRoot.name) +
                    ", mode=" + selectedNativeMode + ", seed=" + seed + ".");
            if (watch.ElapsedMilliseconds > 30)
                Logger.LogWarning("[AP UI PERF] Native filter took " +
                    watch.ElapsedMilliseconds + " ms for " + controls.Count +
                    " active controls. No background polling active.");
        }

        private static bool IsAllowedPlaySubmenuEntry(string label)
        {
            switch (NormalizeNativeLabel(label))
            {
                case "new game": case "new game...": case "nouvelle partie":
                case "new": case "nouveau jeu":
                case "load": case "load game": case "load game...":
                case "charger": case "charger une partie": case "continuer":
                case "multiplayer": case "multijoueur":
                case "back": case "retour": case "previous":
                    return true;
                default: return false;
            }
        }

        private static bool IsSubmenuAnchor(string label)
        {
            string value = NormalizeNativeLabel(label);
            return value == "new game" || value == "nouvelle partie" ||
                value == "load game" || value == "charger une partie" ||
                value == "multiplayer" || value == "multijoueur";
        }

        private static bool IsPlaySubmenuGroup(Transform parent, Type buttonType)
        {
            if (parent == null || buttonType == null) return false;
            int known = 0;
            foreach (Component candidate in parent.GetComponentsInChildren(buttonType, true))
            {
                if (candidate == null || candidate.transform.parent != parent) continue;
                foreach (string label in NativeUiLabels(candidate.gameObject))
                    if (IsSubmenuAnchor(label)) { known++; break; }
                if (known >= 2) return true;
            }
            return false;
        }

        private static bool IsForbiddenPlaySubmenuEntry(string label)
        {
            switch (NormalizeNativeLabel(label))
            {
                case "map editor": case "éditeur de cartes":
                case "éditeur de carte": case "map maker":
                case "level editor": case "wonder": case "wonders":
                case "merveilles": case "merveille":
                case "workshop": case "steam workshop":
                    return true;
                default: return false;
            }
        }

        private void ProbeNativeArchipelagoUi()
        {
            Type buttonType = FindRuntimeType("UnityEngine.UI.Button");
            Logger.LogInfo("[AP NATIVE UI PROBE] Starting. ButtonType=" +
                (buttonType == null ? "absent" : buttonType.FullName) +
                " menu=" + (nativeStarterMenu == null ?
                    "not-open" : nativeStarterMenu.GetType().FullName) +
                " recognizedNativeMaps=" + nativeMapLabels.Count +
                " hiddenEntries=" + hiddenNativeEntries.Count);
            if (buttonType == null) return;
            int count = 0;
            foreach (UnityEngine.Object value in UnityEngine.Object.FindObjectsOfType(buttonType))
            {
                Component button = value as Component;
                if (button == null || button.gameObject == null) continue;
                string label = NativeUiLabel(button.gameObject);
                if (String.IsNullOrEmpty(label)) continue;
                string parent = button.transform.parent == null ?
                    "root" : button.transform.parent.name;
                Logger.LogInfo("[AP NATIVE UI PROBE] button='" +
                    label.Replace("\n", " ") + "' name='" + button.name +
                    "' parent='" + parent + "'");
                if (++count >= 60) break;
            }
            Logger.LogInfo("[AP NATIVE UI PROBE] Finished. visibleButtons=" + count);
        }

        private void ScheduleNativeUiFilter(string source)
        {
            // Three short checks cover asynchronous native card creation.
            // No continuous polling and no re-enabling cards on each pass.
            cachedNativeLabels.Clear();
            pendingFilterPasses = 3;
            nextFilterPassAt = Time.realtimeSinceStartup + .03f;
            Logger.LogInfo("[AP NATIVE UI] One-shot filter scheduled from " + source);
        }

        private void ProbeTitleScreenHierarchy()
        {
            if (!titleSceneSeen || nativeStarterMenu != null) return;
            Logger.LogInfo("[AP TITLE PROBE] SceneMenu native roots, before opening New Game:");
            int found = 0;
            foreach (GameObject go in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (go == null || go.name.StartsWith("Archipelago_", StringComparison.Ordinal))
                    continue;
                string name = (go.name ?? "").ToLowerInvariant();
                if (!(name.Contains("play") || name.Contains("jouer") ||
                      name.Contains("option") || name.Contains("menu") ||
                      name.Contains("button") || name.Contains("bouton") ||
                      name.Contains("intro") || name.Contains("logo")))
                    continue;
                string components = "";
                foreach (Component item in go.GetComponents<Component>())
                {
                    if (item == null) continue;
                    if (components.Length > 170) break;
                    components += (components.Length > 0 ? "," : "") +
                        item.GetType().Name;
                }
                Logger.LogInfo("[AP TITLE PROBE] object=" + go.name +
                    " parent=" + (go.transform.parent == null ?
                        "root" : go.transform.parent.name) +
                    " components=" + components);
                if (++found == 65) break;
            }
            Type labelType = FindRuntimeType("UnityEngine.UI.Text");
            int texts = 0;
            if (labelType != null)
                foreach (UnityEngine.Object instance in
                    UnityEngine.Object.FindObjectsOfType(labelType))
                {
                    Component label = instance as Component;
                    if (label == null) continue;
                    string value = GetUiProperty(label, "text") as string;
                    if (IsPlayLabel(value) || IsOptionsLabel(value))
                    {
                        Logger.LogInfo("[AP TITLE PROBE] label=" + value +
                            " object=" + label.name + " parent=" +
                            (label.transform.parent == null ? "root" :
                                label.transform.parent.name));
                        texts++;
                    }
                }
            Logger.LogInfo("[AP TITLE PROBE] done: objects=" + found +
                " recognizedTitleLabels=" + texts);
        }

        private void OnArchipelagoSceneChanged(
            UnityEngine.SceneManagement.Scene previous,
            UnityEngine.SceneManagement.Scene current)
        {
            string name = current.name ?? "";
            Logger.LogInfo("[AP NATIVE SCREEN] Scene changed: " + name);
            if (String.Equals(name, "SceneMenu", StringComparison.OrdinalIgnoreCase))
            {
                titleSceneSeen = true;
                titleSceneReadyAt = Time.realtimeSinceStartup;
                titleSceneEvidenceLogged = false;
                Logger.LogInfo("[AP CREDITS] SceneMenu loaded; startup credit is scheduled before the player opens New Game.");
            }
            else
            {
                titleSceneSeen = false;
            }
            // Reset bounded title discovery only when switching scenes.
            // This catches titles that load after a long studio intro
            // without performing expensive continuous UI polling.
            mainMenuProbeCount = 0;
            nextMainMenuProbeAt = Time.realtimeSinceStartup + .35f;
            ScheduleNativeUiFilter("scene " + name);
        }

        private void UpdateNativeArchipelagoIntegration()
        {
            float now = Time.realtimeSinceStartup;
            if (titleSceneSeen && !titleSceneEvidenceLogged &&
                now - titleSceneReadyAt >= 1.6f)
            {
                titleSceneEvidenceLogged = true;
                ProbeTitleScreenHierarchy();
            }
            // Do not guess the original credits' duration. Show our segment
            // as soon as the actual title controls become active.
            if (titleSceneSeen && !introCreditShown && titleSceneReadyAt >= 0f)
                BeginOwnIntroCredit();
            if (introCreditActive && now - introCreditStart >= 3.5f)
            {
                introCreditActive = false;
                Logger.LogInfo("[AP CREDITS] Archipelago splash completed.");
            }
            // A handful of title-screen probes only. Once native button is
            // inserted or the title cannot be found, stop scanning entirely.
            if (nativeArchipelagoButton == null && mainMenuProbeCount < 8 &&
                now >= nextMainMenuProbeAt)
            {
                mainMenuProbeCount++;
                nextMainMenuProbeAt = now + (mainMenuProbeCount < 4 ? 1f : 3f);
                TryInstallNativeArchipelagoButton();
                if (mainMenuProbeCount == 8 && nativeArchipelagoButton == null)
                {
                    Logger.LogWarning("[AP NATIVE UI] No native Play button found after bounded probes; F7 remains available.");
                    ProbeNativeArchipelagoUi();
                }
            }
            // Main Play, mode choice and Back may use custom game controls,
            // not Brix.OpenSelf. A click schedules finite filtering once,
            // unlike the old unconditional 500 ms scanner.
            if (titleSceneSeen && !introCreditActive && bridgeConnected &&
                (nativeConnectionPanel == null || !nativeConnectionPanel.activeSelf) &&
                (Input.GetMouseButtonUp(0) || Input.GetKeyDown(KeyCode.Return)))
                ScheduleNativeUiFilter("title/menu interaction");
            if (pendingFilterPasses > 0 && now >= nextFilterPassAt)
            {
                pendingFilterPasses--;
                nextFilterPassAt = now + .12f;
                RefreshNativeSeedMenu();
            }
        }

        private void DrawArchipelagoIntroCredit()
        {
            if (!introCreditActive) return;
            // This is an entire additional credit after the original game's
            // splash sequence, not a watermark on top of Sauropod's credit.
            // Main menu buttons exist under the full white canvas, blocked by
            // the input shield until this dedicated 3.5-second segment ends.
            LoadApLogo();
            float elapsed = Time.realtimeSinceStartup - introCreditStart;
            float opacity = Mathf.Clamp01(elapsed / .55f) *
                Mathf.Clamp01((3.5f - elapsed) / .65f);
            int oldDepth = GUI.depth;
            Color old = GUI.color;
            GUI.depth = -32000;
            try
            {
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height),
                    Texture2D.whiteTexture);
                GUI.color = new Color(1f, 1f, 1f, opacity);
                if (apLogo != null)
                {
                    float size = Mathf.Min(250f, Screen.height * .34f);
                    GUI.DrawTexture(new Rect((Screen.width - size) * .5f,
                        (Screen.height - size) * .43f, size, size),
                        apLogo, ScaleMode.ScaleToFit, true);
                }
                if (introCreditStyle == null)
                {
                    introCreditStyle = new GUIStyle(GUI.skin.label);
                    introCreditStyle.alignment = TextAnchor.MiddleCenter;
                    introCreditStyle.normal.textColor = Color.black;
                    introCreditStyle.fontSize = 27;
                    introCreditStyle.fontStyle = FontStyle.Bold;
                    if (nativeMenuFont != null) introCreditStyle.font = nativeMenuFont;
                }
                GUI.Label(new Rect(0, Screen.height * .69f,
                    Screen.width, 60f), "ARCHIPELAGO", introCreditStyle);
            }
            finally
            {
                GUI.color = old;
                GUI.depth = oldDepth;
            }
        }
    }
}
