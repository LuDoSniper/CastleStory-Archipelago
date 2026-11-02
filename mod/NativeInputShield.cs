using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryArchipelago
{
    public sealed partial class CastleStoryArchipelagoPlugin
    {
        // Castle Story's native screens are Unity UI while our AP window is IMGUI.
        // Event.current.Use() only consumes IMGUI events; it cannot intercept a
        // Unity UI GraphicRaycaster. This transparent, topmost Canvas intercepts
        // native pointer events while the AP modal is open.
        private GameObject nativeInputShield;
        private bool nativeShieldAttempted;
        private bool nativeShieldWarningShown;

        private static Type FindRuntimeType(string fullname)
        {
            Type type = AccessTools.TypeByName(fullname);
            if (type != null) return type;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = asm.GetType(fullname, false);
                    if (type != null) return type;
                }
                catch (Exception) { }
            }
            return null;
        }

        private void InitializeNativeInputShield()
        {
            if (nativeShieldAttempted) return;
            nativeShieldAttempted = true;
            try
            {
                Type imageType = FindRuntimeType("UnityEngine.UI.Image");
                Type raycasterType = FindRuntimeType("UnityEngine.UI.GraphicRaycaster");
                if (imageType == null || raycasterType == null)
                {
                    Logger.LogWarning("[AP UI] Unity UI Image/GraphicRaycaster unavailable. " +
                        "Native click-through blocking is not installed.");
                    return;
                }

                var root = new GameObject("CastleStoryAP_NativeInputShield",
                    new[] { typeof(RectTransform) });
                nativeInputShield = root;
                UnityEngine.Object.DontDestroyOnLoad(root);
                Canvas canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.overrideSorting = true;
                canvas.sortingOrder = 32767;
                root.AddComponent(raycasterType);

                var graphic = new GameObject("TransparentRaycastSurface",
                    new[] { typeof(RectTransform) });
                graphic.transform.SetParent(root.transform, false);
                RectTransform rect = (RectTransform)graphic.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;

                Component image = graphic.AddComponent(imageType);
                PropertyInfo colorProperty = imageType.GetProperty("color");
                if (colorProperty != null)
                    colorProperty.SetValue(image, new Color(0f, 0f, 0f, 0f), null);
                PropertyInfo raycast = imageType.GetProperty("raycastTarget");
                if (raycast != null)
                    raycast.SetValue(image, true, null);
                else
                    throw new MissingMemberException(imageType.FullName, "raycastTarget");

                root.SetActive(false);
                Logger.LogInfo("[AP UI] Native Unity UI click-through shield installed.");
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP UI] Cannot install native pointer shield: " + error);
                if (nativeInputShield != null)
                    UnityEngine.Object.Destroy(nativeInputShield);
                nativeInputShield = null;
            }
        }

        private bool IsApModalVisible()
        {
            return apMenuOpen ||
                (bridgeConnected && (nativeNewGameVisible || campaignPreviewOpen));
        }

        private void UpdateNativeInputShield()
        {
            if (nativeInputShield == null && !nativeShieldAttempted)
                InitializeNativeInputShield();
            bool modal = IsApModalVisible();
            if (nativeInputShield != null)
            {
                if (nativeInputShield.activeSelf != modal)
                {
                    nativeInputShield.SetActive(modal);
                    Logger.LogInfo("[AP UI] Native pointer shield " +
                        (modal ? "activated" : "deactivated"));
                }
            }
            else if (modal && !nativeShieldWarningShown)
            {
                nativeShieldWarningShown = true;
                Logger.LogWarning("[AP UI] Popup is open without native pointer shield. " +
                    "Please provide BepInEx/LogOutput.log.");
            }
        }

        private void DestroyNativeInputShield()
        {
            if (nativeInputShield != null)
                UnityEngine.Object.Destroy(nativeInputShield);
            nativeInputShield = null;
        }
    }
}
