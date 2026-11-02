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
            if (type == null) return null;
            foreach (Component item in source.GetComponentsInChildren(type, true))
            {
                GameObject copy = UnityEngine.Object.Instantiate(item.gameObject)
                    as GameObject;
                if (copy == null) continue;
                copy.name = "AP_Text_" + text;
                copy.transform.SetParent(parent, false);
                var rect = copy.transform as RectTransform;
                if (rect != null)
                {
                    rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
                    rect.sizeDelta = size;
                    rect.anchoredPosition = offset;
                }
                Component label = copy.GetComponent(type);
                SetUiProperty(label, "text", text);
                SetUiProperty(label, "fontSize", fontSize);
                SetUiProperty(label, "raycastTarget", false);
                return label;
            }
            return null;
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
            AddUiImage(entry, new Color(.96f, .97f, .98f, 1f), true);
            Component text = CloneNativeText(source, entry.transform, "",
                new Vector2(454, 36), Vector2.zero, 18);
            if (text == null) return null;
            // Native InputField draws its caret, selection and editable text.
            SetUiProperty(text, "color", new Color(.1f, .13f, .18f, 1f));
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
                var panel = NewNativeUiObject("ArchipelagoConnection",
                    root.transform, new Vector2(580, 555), Vector2.zero);
                AddUiImage(panel, new Color(.13f, .17f, .21f, .98f), true);
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
                    "Connexion", "NativeConnectAction", -177, -238);
                var disconnect = CloneNativeAction(
                    mainMenuOptionsTemplate != null ?
                    mainMenuOptionsTemplate : mainMenuPlayTemplate,
                    panel.transform, "Déconnexion", "NativeDisconnectAction", 0, -238);
                var close = CloneNativeAction(
                    mainMenuOptionsTemplate != null ?
                    mainMenuOptionsTemplate : mainMenuPlayTemplate,
                    panel.transform, "Fermer", "NativeCloseAction", 177, -238);
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
            string message = bridgeConnected ?
                "Connecté — " + mode + " / " + (loginSlot ?? "") : statusLine;
            if (message != nativeStatusCache)
            {
                nativeStatusCache = message;
                SetUiProperty(nativeStatusLabel, "text", message);
            }
        }

        private void NativeConnectAction()
        {
            ReadNativeInput();
            WriteConnection(true);
            SetUiProperty(passwordNativeInput, "text", "");
            loginPassword = "";
            if (!String.IsNullOrEmpty(statusLine))
                Logger.LogInfo("[AP NATIVE UI] Connect requested (credentials omitted).");
        }

        private void NativeDisconnectAction()
        {
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
