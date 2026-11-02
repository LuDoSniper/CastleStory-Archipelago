using System;
using System.IO;
using HarmonyLib;

namespace CastleStoryArchipelago
{
    public sealed partial class CastleStoryArchipelagoPlugin
    {
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
            }
            catch (Exception e) { Logger.LogError("[AP MENU] Cannot install guard: " + e); }
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
                var method = AccessTools.Method(map.GetType(), "GetHeightmapPath", Type.EmptyTypes);
                string heightmap = method == null ? null : method.Invoke(map, null) as string;
                if (String.IsNullOrEmpty(heightmap))
                {
                    statusLine = "Map identity unavailable: AP guard prevented unsafe start.";
                    Logger.LogWarning("[AP MENU] Could not resolve Asset_Map.GetHeightmapPath.");
                    return false;
                }
                string actual = Path.GetFileName(Path.GetDirectoryName(
                    heightmap.Replace('/', Path.DirectorySeparatorChar)
                             .Replace('\\', Path.DirectorySeparatorChar)));
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
