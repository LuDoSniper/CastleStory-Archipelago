using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace CastleStoryArchipelago
{
    public sealed partial class CastleStoryArchipelagoPlugin
    {
        private readonly Dictionary<string, object> observedMapAssets =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private object nativeStarterMenu;

        private void InstallNativeMenuGuard()
        {
            try
            {
                Type type = AccessTools.TypeByName("Brix.UI.Builder.Menu.BaseMapStarterMenu");
                var target = type == null ? null : AccessTools.Method(type, "OnOk", Type.EmptyTypes);
                if (target == null)
                {
                    Logger.LogWarning("[AP MENU] No native new-game menu hook. AP menu selection is advisory.");
                    return;
                }
                var before = AccessTools.Method(typeof(CastleStoryArchipelagoPlugin), "BeforeNativeNewGame");
                harmony.Patch(target, prefix: new HarmonyMethod(before));
                Logger.LogInfo("[AP MENU] Native map launch guard attached.");
                TryPatchNativeObservation(
                    "Brix.UI.Builder.Menu.BaseNewGameLibraryMenu",
                    "GetAssetLabel", "ObserveMapListed");
                TryPatchNativeObservation(
                    "Brix.UI.Builder.Menu.SpNewGameStarterMenu",
                    "OpenSelf", "ObserveStarterOpened");
            }
            catch (Exception e) { Logger.LogError("[AP MENU] Cannot install guard: " + e); }
        }

        private void TryPatchNativeObservation(string name, string method, string callback)
        {
            try
            {
                Type type = AccessTools.TypeByName(name);
                var original = type == null ? null : AccessTools.Method(type, method);
                var after = AccessTools.Method(typeof(CastleStoryArchipelagoPlugin), callback);
                if (original != null && after != null)
                {
                    harmony.Patch(original, postfix: new HarmonyMethod(after));
                    Logger.LogInfo("[AP MENU] Installed observation on " + name + "." + method);
                }
            }
            catch (Exception e) { Logger.LogWarning("[AP MENU] Observation failed: " + e.Message); }
        }

        private static void ObserveMapListed(object __0)
        {
            if (Instance != null) Instance.RegisterAvailableMap(__0);
        }

        private void RegisterAvailableMap(object gameMap)
        {
            string key = MapIdentity(gameMap);
            if (!String.IsNullOrEmpty(key))
                observedMapAssets[key] = gameMap;
        }

        private static void ObserveStarterOpened(object __instance)
        {
            if (Instance != null)
            {
                Instance.nativeStarterMenu = __instance;
                Instance.Logger.LogInfo("[AP MENU] Single-player new-game starter opened.");
            }
        }

        private string MapIdentity(object map)
        {
            if (map == null) return null;
            try
            {
                var method = AccessTools.Method(map.GetType(), "GetHeightmapPath", Type.EmptyTypes);
                string heightmap = method == null ? null : method.Invoke(map, null) as string;
                if (String.IsNullOrEmpty(heightmap)) return null;
                return Path.GetFileName(Path.GetDirectoryName(
                    heightmap.Replace('/', Path.DirectorySeparatorChar)
                             .Replace('\\', Path.DirectorySeparatorChar)));
            }
            catch (Exception e)
            {
                Logger.LogWarning("[AP MENU] Map identity lookup failed: " + e.Message);
                return null;
            }
        }

        private void LaunchSelectedMission()
        {
            // Uses the actual Castle Story starter UI, not a fabricated map object.
            if (!bridgeConnected || campaign == null || !campaign.ready)
            {
                statusLine = "Connect to a ready Archipelago campaign first.";
                return;
            }
            if (nativeStarterMenu == null)
            {
                statusLine = "Open New Game in Castle Story first; its map list is not loaded.";
                return;
            }
            MissionFile mission = null;
            foreach (var m in campaign.missions)
                if (m != null && m.id == chosenMission) mission = m;
            if (mission == null)
            {
                statusLine = "Choose a mission first.";
                return;
            }
            object asset;
            if (!observedMapAssets.TryGetValue(mission.map, out asset))
            {
                statusLine = "Map not yet loaded by the game: " + mission.map +
                    ". Open the game's map browser.";
                return;
            }
            try
            {
                Type starter = nativeStarterMenu.GetType();
                var selectedMap = AccessTools.Property(starter, "SelectedMap");
                var selectedMode = AccessTools.Property(starter, "SelectedGamemode");
                if (selectedMap == null || !selectedMap.CanWrite ||
                    selectedMode == null)
                {
                    statusLine = "Native map/mode selector unavailable on this game version.";
                    return;
                }
                object gameMode = selectedMode.GetValue(nativeStarterMenu, null);
                // If Castle Story has no mode selected, do not start an
                // accidental Sandbox/other mode on the correct physical map.
                if (gameMode == null)
                {
                    statusLine = "Select " + mission.mode +
                        " as the game mode in Castle Story first.";
                    return;
                }
                string modeText = gameMode.ToString() ?? "";
                foreach (string name in new [] { "CodeName", "Codename", "codeName" })
                {
                    var property = AccessTools.Property(gameMode.GetType(), name);
                    if (property == null) continue;
                    object value = property.GetValue(gameMode, null);
                    if (value != null) { modeText = value.ToString(); break; }
                }
                if (modeText.IndexOf(mission.mode, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    statusLine = "Select " + mission.mode +
                        " in the native Game Mode picker before launching (current " +
                        modeText + ").";
                    return;
                }
                selectedMap.SetValue(nativeStarterMenu, asset, null);
                Logger.LogInfo("[AP MENU] Auto-selected map " + mission.map +
                    " for " + mission.id);
                var start = AccessTools.Method(starter, "OnOk", Type.EmptyTypes);
                if (start == null)
                {
                    statusLine = "Native start method unavailable; manual start required.";
                    return;
                }
                start.Invoke(nativeStarterMenu, new object[0]);
                statusLine = "Launching " + mission.map + "...";
            }
            catch (Exception error)
            {
                statusLine = "Cannot launch mission; check game logs.";
                Logger.LogWarning("[AP MENU] Native launch failed: " + error);
            }
        }

        private static bool BeforeNativeNewGame(object __instance)
        {
            return Instance == null || Instance.CanStartNativeMap(__instance);
        }

        private bool CanStartNativeMap(object menuInstance)
        {
            // Never intercept unrelated vanilla menus or ordinary saves.
            if (!bridgeConnected || campaign == null || !campaign.ready ||
                campaign.missions == null) return true;
            if (menuInstance == null || menuInstance.GetType().Name != "SpNewGameStarterMenu")
                return true;
            if (!IsSelectedMissionType("Conquest") && !IsSelectedMissionType("Invasion"))
            {
                statusLine = "Select an AP mission before starting a new game.";
                Logger.LogWarning("[AP MENU] Blocked new game: no seed-selected mission.");
                return false;
            }
            MissionFile selected = null;
            foreach (var mission in campaign.missions)
                if (mission != null && mission.id == chosenMission) selected = mission;
            if (selected == null) return false;
            try
            {
                var property = AccessTools.Property(menuInstance.GetType(), "SelectedMap");
                object map = property == null ? null : property.GetValue(menuInstance, null);
                if (map == null)
                {
                    statusLine = "Select " + selected.map + " in Castle Story before starting.";
                    return false;
                }
                // Asset_Map.GetHeightmapPath() is present in the supplied Castle Story
                // Assembly-CSharp-firstpass.dll. It provides a stable folder identity.
                string actual = MapIdentity(map);
                if (String.IsNullOrEmpty(actual))
                {
                    statusLine = "Map identity unavailable: AP guard prevented unsafe start.";
                    Logger.LogWarning("[AP MENU] Could not resolve Asset_Map.GetHeightmapPath.");
                    return false;
                }
                if (!String.Equals(actual, selected.map, StringComparison.OrdinalIgnoreCase))
                {
                    statusLine = "Expected map: " + selected.map + " (selected " + actual + ")";
                    Logger.LogWarning("[AP MENU] Blocked map " + actual +
                        " for mission " + selected.id);
                    return false;
                }
                Logger.LogInfo("[AP MENU] Allowed game start: " + selected.id +
                    " map=" + actual + " mode=" + selected.mode);
                return true;
            }
            catch (Exception error)
            {
                statusLine = "Could not validate new-game map; see BepInEx log.";
                Logger.LogWarning("[AP MENU] Map guard failed safely: " + error);
                return false;
            }
        }
    }
}
