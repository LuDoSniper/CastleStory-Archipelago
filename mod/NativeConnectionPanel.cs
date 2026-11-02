using System;
using System.Reflection;
using UnityEngine;

namespace CastleStoryArchipelago
{
    public sealed partial class CastleStoryArchipelagoPlugin
    {
        // The primary connection experience is native Unity UI, built using
        // clones of the real main-menu Play/Options buttons and text controls.
        // The old IMGUI panel remains only as a compatibility fallback.
        private GameObject nativeConnectionPanel;
        private Component serverNativeInput, slotNativeInput, passwordNativeInput;
        private Component nativeStatusLabel;
        private string nativeStatusCache = "";
        private float lastConnectRequestAt = -100f;
        private bool nativeConnectPending;
        private string nativePanelDiagnostic = "";
        private GameObject mainMenuPlayTemplate, mainMenuOptionsTemplate;

        private static void SetUiProperty(object target, string property, object value)
        {
            if (target == null) return;
            PropertyInfo p = target.GetType().GetProperty(property,
                BindingFlags.Instance | BindingFlags.Public);
            if (p == null || !p.CanWrite) return;
            p.SetValue(target, value, null);
        }

        private static object GetUiProperty(object target, string property)
        {
            if (target == null) return null;
            PropertyInfo p = target.GetType().GetProperty(property,
                BindingFlags.Instance | BindingFlags.Public);
            return p != null && p.CanRead ? p.GetValue(target, null) : null;
        }

        private static GameObject NewNativeUiObject(string name, Transform parent,
                                                     Vector2 size, Vector2 offset)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            var rect = (RectTransform)obj.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = offset;
            return obj;
        }

        private Component AddUiImage(GameObject obj, Color color, bool raycast)
        {
            Type type = FindRuntimeType("UnityEngine.UI.Image");
            if (type == null) return null;
            Component image = obj.AddComponent(type);
            SetUiProperty(image, "color", color);
            SetUiProperty(image, "raycastTarget", raycast);
            return image;
        }

        private Component CloneNativeText(GameObject source, Transform parent,
                                          string text, Vector2 size,
                                          Vector2 offset, int fontSize)
        {
            Type type = FindRuntimeType("UnityEngine.UI.Text");
            if (type == null || source == null) return null;
            // Never clone the Play button's Text object. It inherits its
            // native RectTransform, alignment and layout-driving components;
            // that placed the server/slot texts outside our input fields.
            object font = nativeMenuFont;
            if (font == null)
                foreach (Component existing in source.GetComponentsInChildren(type, true))
                {
                    font = GetUiProperty(existing, "font");
                    if (font != null) break;
                }
            if (font == null)
                throw new InvalidOperationException("Native title font unavailable.");
            var labelObject = NewNativeUiObject("AP_Text_" + text, parent,
                size, offset);
            Component label = labelObject.AddComponent(type);
            SetUiProperty(label, "font", font);
            SetUiProperty(label, "text", text);
            SetUiProperty(label, "fontSize", fontSize);
            SetUiProperty(label, "alignment",
                text == "ARCHIPELAGO" || text == "Archipelago déconnecté" ?
                TextAnchor.MiddleCenter : TextAnchor.MiddleLeft);
            SetUiProperty(label, "color", new Color(.16f, .13f, .08f, 1f));
            SetUiProperty(label, "raycastTarget", false);
            return label;
        }

        private Component AddNativeInput(GameObject source, Transform parent,
                                         string title, string remembered,
                                         float y, bool secret)
        {
            Type fieldType = FindRuntimeType("UnityEngine.UI.InputField");
            if (fieldType == null) return null;
            CloneNativeText(source, parent, title,
                new Vector2(490, 25), new Vector2(0, y + 49), 17);
            var entry = NewNativeUiObject("AP_Input_" + title, parent,
                new Vector2(490, 42), new Vector2(0, y + 8));
            AddUiImage(entry, new Color(1f, .975f, .85f, 1f), true);
            Component text = CloneNativeText(source, entry.transform, "",
                new Vector2(454, 36), Vector2.zero, 18);
            if (text == null) return null;
            // Native InputField draws its caret, selection and editable text.
            SetUiProperty(text, "color", new Color(.14f, .11f, .07f, 1f));
            Component field = entry.AddComponent(fieldType);
            SetUiProperty(field, "textComponent", text);
            if (secret)
            {
                var prop = fieldType.GetProperty("contentType");
                if (prop != null && prop.CanWrite)
                    prop.SetValue(field, Enum.Parse(prop.PropertyType, "Password"), null);
            }
            SetUiProperty(field, "text", remembered);
            return field;
        }

        private GameObject CloneNativeAction(GameObject source, Transform parent,
                                             string caption, string method,
                                             float x, float y)
        {
            Type buttonType = FindRuntimeType("UnityEngine.UI.Button");
            if (source == null || buttonType == null) return null;
            var cloned = UnityEngine.Object.Instantiate(source) as GameObject;
            if (cloned == null) return null;
            cloned.name = "AP_" + method;
            cloned.SetActive(false);
            cloned.transform.SetParent(parent, false);
            var rect = cloned.transform as RectTransform;
            if (rect != null)
            {
                rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
                rect.sizeDelta = new Vector2(163, 49);
                rect.anchoredPosition = new Vector2(x, y);
            }
            Component button = cloned.GetComponent(buttonType);
            if (button == null ||
                !InstallNativeButtonListener(button, buttonType, method) ||
                !SetNativeUiLabelAny(cloned, caption))
            {
                UnityEngine.Object.Destroy(cloned);
                return null;
            }
            cloned.SetActive(true);
            return cloned;
        }

        private static bool SetNativeUiLabelAny(GameObject obj, string text)
        {
            Type type = FindRuntimeType("UnityEngine.UI.Text");
            if (type == null) return false;
            foreach (Component label in obj.GetComponentsInChildren(type, true))
            {
                SetUiProperty(label, "text", text);
                SetUiProperty(label, "fontSize", 18);
                SetUiProperty(label, "alignment", TextAnchor.MiddleCenter);
                SetUiProperty(label, "raycastTarget", false);
                // The cloned button previously retained an oversized caption
                // offset, cropping Connexion/Déconnexion in the screenshot.
                RectTransform rect = label.transform as RectTransform;
                if (rect != null)
                {
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.offsetMin = Vector2.zero;
                    rect.offsetMax = Vector2.zero;
                    rect.localScale = Vector3.one;
                }
                return true;
            }
            return false;
        }

        private bool ShowNativeConnectionPanel()
        {
            if (nativeConnectionPanel != null)
            {
                SetUiProperty(serverNativeInput, "text", loginServer);
                SetUiProperty(slotNativeInput, "text", loginSlot);
                SetUiProperty(passwordNativeInput, "text", "");
                nativeConnectionPanel.SetActive(true);
                UpdateNativePanelStatus();
                return true;
            }
            if (mainMenuPlayTemplate == null) return false;
            if (FindRuntimeType("UnityEngine.UI.InputField") == null ||
                FindRuntimeType("UnityEngine.UI.Text") == null ||
                FindRuntimeType("UnityEngine.UI.GraphicRaycaster") == null)
                return false;
            GameObject root = null;
            try
            {
                root = new GameObject("Archipelago_NativeConnectionCanvas",
                    typeof(RectTransform));
                UnityEngine.Object.DontDestroyOnLoad(root);
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.overrideSorting = true;
                canvas.sortingOrder = 32767;
                root.AddComponent(FindRuntimeType("UnityEngine.UI.GraphicRaycaster"));
                var background = NewNativeUiObject("ModalInputShield",
                    root.transform, Vector2.zero, Vector2.zero);
                var bg = (RectTransform)background.transform;
                bg.anchorMin = Vector2.zero;
                bg.anchorMax = Vector2.one;
                bg.offsetMin = bg.offsetMax = Vector2.zero;
                AddUiImage(background, new Color(0, 0, 0, .87f), true);
                var frame = NewNativeUiObject("ArchipelagoConnectionFrame",
                    root.transform, new Vector2(602, 578), Vector2.zero);
                AddUiImage(frame, new Color(.16f, .13f, .08f, 1f), true);
                var panel = NewNativeUiObject("ArchipelagoConnection",
                    root.transform, new Vector2(580, 555), Vector2.zero);
                // Castle Story's menus use warm parchment/gold rather than
                // the unrelated dark developer-panel background.
                AddUiImage(panel, new Color(.95f, .79f, .34f, 1f), true);
                CloneNativeText(mainMenuPlayTemplate, panel.transform,
                    "ARCHIPELAGO", new Vector2(520, 64),
                    new Vector2(0, 217), 31);
                serverNativeInput = AddNativeInput(mainMenuPlayTemplate, panel.transform,
                    "SERVEUR", loginServer, 117, false);
                slotNativeInput = AddNativeInput(mainMenuPlayTemplate, panel.transform,
                    "SLOT", loginSlot, 11, false);
                passwordNativeInput = AddNativeInput(mainMenuPlayTemplate, panel.transform,
                    "MOT DE PASSE", "", -95, true);
                nativeStatusLabel = CloneNativeText(mainMenuPlayTemplate,
                    panel.transform, "Archipelago déconnecté",
                    new Vector2(515, 50), new Vector2(0, -172), 15);
                var connect = CloneNativeAction(mainMenuPlayTemplate, panel.transform,
                    "Connexion", "NativeConnectAction", -172, -238);
                var disconnect = CloneNativeAction(
                    mainMenuOptionsTemplate != null ?
                    mainMenuOptionsTemplate : mainMenuPlayTemplate,
                    panel.transform, "Déconnexion", "NativeDisconnectAction", 0, -238);
                var close = CloneNativeAction(
                    mainMenuOptionsTemplate != null ?
                    mainMenuOptionsTemplate : mainMenuPlayTemplate,
                    panel.transform, "Fermer", "NativeCloseAction", 172, -238);
                if (serverNativeInput == null || slotNativeInput == null ||
                    passwordNativeInput == null || nativeStatusLabel == null ||
                    connect == null || disconnect == null || close == null)
                    throw new InvalidOperationException("Native UI components incomplete");
                nativeConnectionPanel = root;
                UpdateNativePanelStatus();
                Logger.LogInfo("[AP NATIVE UI] Native connection window created.");
                return true;
            }
            catch (Exception error)
            {
                nativePanelDiagnostic = error.Message;
                Logger.LogWarning("[AP NATIVE UI] Native input fallback: " +
                    error.GetType().Name + " " + error.Message);
                if (root != null) UnityEngine.Object.Destroy(root);
                serverNativeInput = slotNativeInput = passwordNativeInput = null;
                return false;
            }
        }

        private void ReadNativeInput()
        {
            loginServer = (GetUiProperty(serverNativeInput, "text") as string) ?? loginServer;
            loginSlot = (GetUiProperty(slotNativeInput, "text") as string) ?? loginSlot;
            loginPassword = (GetUiProperty(passwordNativeInput, "text") as string) ?? "";
        }

        private void UpdateNativePanelStatus()
        {
            if (nativeConnectionPanel == null || !nativeConnectionPanel.activeSelf)
                return;
            if (bridgeConnected) nativeConnectPending = false;
            string message = bridgeConnected ?
                "Connecté — " + mode + " / " + (loginSlot ?? "") :
                nativeConnectPending &&
                Time.realtimeSinceStartup - lastConnectRequestAt < 12f ?
                    "Connexion en cours… le bridge réessaie automatiquement." :
                    statusLine;
            if (message != nativeStatusCache)
            {
                nativeStatusCache = message;
                SetUiProperty(nativeStatusLabel, "text", message);
            }
        }

        private void NativeConnectAction()
        {
            if (nativeConnectPending && !bridgeConnected &&
                Time.realtimeSinceStartup - lastConnectRequestAt < 12f)
            {
                Logger.LogInfo("[AP LOGIN] Already connecting; duplicate click ignored.");
                return;
            }
            ReadNativeInput();
            if (String.IsNullOrEmpty(loginServer.Trim()) ||
                String.IsNullOrEmpty(loginSlot.Trim()))
            {
                statusLine = "Renseigne le serveur et le slot.";
                UpdateNativePanelStatus();
                return;
            }
            WriteConnection(true);
            nativeConnectPending = true;
            lastConnectRequestAt = Time.realtimeSinceStartup;
            SetUiProperty(passwordNativeInput, "text", "");
            loginPassword = "";
            nativeStatusCache = "";
            UpdateNativePanelStatus();
            Logger.LogInfo("[AP LOGIN] Login handed to bridge once; retry handled by bridge.");
        }

        private void NativeDisconnectAction()
        {
            nativeConnectPending = false;
            WriteConnection(false);
            SetUiProperty(passwordNativeInput, "text", "");
            loginPassword = "";
            UpdateNativePanelStatus();
        }

        private void NativeCloseAction()
        {
            ReadNativeInput();
            SaveRememberedConnection();
            SetUiProperty(passwordNativeInput, "text", "");
            loginPassword = "";
            if (nativeConnectionPanel != null) nativeConnectionPanel.SetActive(false);
            apMenuOpen = false;
        }

        private void HideNativeConnectionPanel()
        {
            if (nativeConnectionPanel != null) nativeConnectionPanel.SetActive(false);
        }

        private void DestroyNativeConnectionPanel()
        {
            if (nativeConnectionPanel != null)
                UnityEngine.Object.Destroy(nativeConnectionPanel);
            nativeConnectionPanel = null;
            serverNativeInput = slotNativeInput = passwordNativeInput = nativeStatusLabel = null;
        }
    }
}
