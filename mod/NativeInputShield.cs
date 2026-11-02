using System;
using System.Collections.Generic;
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
        private readonly List<Behaviour> pausedNativeEventSystems = new List<Behaviour>();
        private int nativeShieldKeepUntilFrame = -1;

        private void KeepInputShieldUntilMouseReleased()
        {
            nativeShieldKeepUntilFrame = Time.frameCount + 2;
        }

        // A topmost Raycaster is useful, but some Unity 5 game menus process
        // their own raycasts. Temporarily disabling active EventSystem
        // components prevents pointer handling by those native UI controls
        // while our IMGUI popup remains interactive.
        private void BlockNativeEventSystems()
        {
            Type type = FindRuntimeType("UnityEngine.EventSystems.EventSystem");
            if (type == null) return;
            try
            {
                UnityEngine.Object[] instances = UnityEngine.Object.FindObjectsOfType(type);
                foreach (UnityEngine.Object instance in instances)
                {
                    var behaviour = instance as Behaviour;
                    if (behaviour == null || !behaviour.enabled) continue;
                    behaviour.enabled = false;
                    if (!pausedNativeEventSystems.Contains(behaviour))
                        pausedNativeEventSystems.Add(behaviour);
                }
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP UI] Could not pause native EventSystem: " + error.Message);
            }
        }

        private void RestoreNativeEventSystems()
        {
            foreach (Behaviour component in pausedNativeEventSystems)
            {
                if (component == null) continue;
                try { component.enabled = true; }
                catch (Exception error)
                {
                    Logger.LogWarning("[AP UI] Could not restore EventSystem: " + error.Message);
                }
            }
            pausedNativeEventSystems.Clear();
        }

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
            // The popup stays open to show an error even when the bridge
            // heartbeat drops. Its pointer shield must remain in sync.
            return apMenuOpen || nativeNewGameVisible || campaignPreviewOpen;
        }

        private void UpdateNativeInputShield()
        {
            if (nativeInputShield == null && !nativeShieldAttempted)
                InitializeNativeInputShield();
            // Do not pass the MouseUp that closes the popup to a native button
            // in the same frame. Release shielding only after the click ends.
            bool modal = IsApModalVisible() || Input.GetMouseButton(0) &&
                pausedNativeEventSystems.Count > 0 ||
                Time.frameCount <= nativeShieldKeepUntilFrame;
            if (modal) BlockNativeEventSystems();
            else if (pausedNativeEventSystems.Count > 0)
                RestoreNativeEventSystems();
            if (nativeInputShield != null)
            {
                if (nativeInputShield.activeSelf != modal)
                {
                    nativeInputShield.SetActive(modal);
                    Logger.LogInfo("[AP UI] Native pointer shield " +
                        (modal ? "activated" : "deactivated") +
                        "; apMenu=" + apMenuOpen +
                        "; nativeNewGame=" + nativeNewGameVisible +
                        "; campaignPreview=" + campaignPreviewOpen +
                        "; connected=" + bridgeConnected);
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
            RestoreNativeEventSystems();
            if (nativeInputShield != null)
                UnityEngine.Object.Destroy(nativeInputShield);
            nativeInputShield = null;
        }
    }
}
