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
            foreach (string typeName in new [] {
                "UnityEngine.UI.Text", "TMPro.TextMeshProUGUI" })
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
            foreach (string typeName in new [] {
                "UnityEngine.UI.Text", "TMPro.TextMeshProUGUI" })
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

        private void BeginOwnIntroCredit()
        {
            // Start only AFTER Play+Options are present, which marks the end
            // of the studio splash sequence. Never overlay a studio logo.
            if (introCreditShown) return;
            introCreditShown = true;
            introCreditActive = true;
            introCreditStart = Time.realtimeSinceStartup;
            Logger.LogInfo("[AP CREDITS] Dedicated Archipelago credit after studio splashes.");
        }

        private void TryInstallNativeArchipelagoButton()
        {
            if (nativeArchipelagoButton != null) return;
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
                    BeginOwnIntroCredit();
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
                labels[item.gameObject] = NativeUiLabels(item.gameObject);
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
            pendingFilterPasses = 6;
            nextFilterPassAt = Time.realtimeSinceStartup;
            Logger.LogInfo("[AP NATIVE UI] One-shot filter scheduled from " + source);
        }

        private void UpdateNativeArchipelagoIntegration()
        {
            float now = Time.realtimeSinceStartup;
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
