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
        private int mapRegistrationCount;
        private bool unknownMapTypeLogged;
        private bool nativeMapScanComplete;
        [Serializable] private sealed class NativeMapCatalog
        {
            public string[] map_ids;
            public string[] invasion_map_ids;
            public string[] conquest_map_ids;
            public bool scan_complete;
        }

        private static string MissionMapId(MissionFile mission)
        {
            if (mission == null) return null;
            return !String.IsNullOrEmpty(mission.map_id) ?
                mission.map_id : mission.map; // Legacy pre-protocol-5 slots.
        }

        private void SaveObservedMapIds(bool scanComplete)
        {
            try
            {
                var ids = new List<string>(observedMapAssets.Keys);
                ids.Sort(StringComparer.OrdinalIgnoreCase);
                var invasion = new List<string>();
                var conquest = new List<string>();
                if (nativeStarterMenu != null)
                {
                    var getModes = AccessTools.Method(nativeStarterMenu.GetType(),
                        "GetGamemodes");
                    if (getModes != null && getModes.GetParameters().Length == 1)
                    {
                        foreach (string mapId in ids)
                        {
                            try
                            {
                                var modes = getModes.Invoke(nativeStarterMenu,
                                    new object[] { observedMapAssets[mapId] })
                                    as System.Collections.IEnumerable;
                                if (modes == null) continue;
                                foreach (object candidate in modes)
                                {
                                    string code = ModeIdentity(candidate);
                                    if (code == "Invasion" && !invasion.Contains(mapId))
                                        invasion.Add(mapId);
                                    else if (code == "Conquest" && !conquest.Contains(mapId))
                                        conquest.Add(mapId);
                                }
                            }
                            catch (Exception e)
                            {
                                Logger.LogWarning("[AP MAPS] Cannot inspect modes for " +
                                    mapId + ": " + e.GetType().Name);
                            }
                        }
                    }
                }
                invasion.Sort(StringComparer.OrdinalIgnoreCase);
                conquest.Sort(StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(Path.Combine(directory, "installed_maps.json"),
                    UnityEngine.JsonUtility.ToJson(new NativeMapCatalog {
                        map_ids = ids.ToArray(),
                        invasion_map_ids = invasion.ToArray(),
                        conquest_map_ids = conquest.ToArray(),
                        scan_complete = scanComplete
                    }));
                Logger.LogInfo("[AP MAPS] Exported " + ids.Count + " IDs; " +
                    invasion.Count + " verified Invasion maps and " +
                    conquest.Count + " Conquest maps.");
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP MAPS] Cannot write installed map IDs: " + error.Message);
            }
        }

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
                Type soloType = AccessTools.TypeByName(
                    "Brix.UI.Builder.Menu.SpNewGameStarterMenu");
                var soloOnOk = soloType == null ? null :
                    AccessTools.Method(soloType, "OnOk", Type.EmptyTypes);
                if (soloOnOk != null && soloOnOk != target)
                {
                    harmony.Patch(soloOnOk, prefix: new HarmonyMethod(before));
                    Logger.LogInfo("[AP MENU] Hooked concrete single-player start as well.");
                }
                TryPatchNativeObservation(
                    "Brix.UI.Builder.Menu.BaseNewGameLibraryMenu",
                    "GetAssetLabel", "ObserveMapListed");
                TryPatchNativeObservation(
                    "Brix.UI.Builder.Menu.SpNewGameStarterMenu",
                    "OpenSelf", "ObserveStarterOpened");
                TryPatchNativeObservation(
                    "Brix.UI.Builder.Menu.SpNewGameStarterMenu",
                    "CloseSelf", "ObserveStarterClosed");
                TryPatchNativeObservation(
                    "Brix.UI.Builder.Menu.SpNewGameStarterMenu",
                    "OnBack", "ObserveStarterClosed");
                TryPatchNativeObservation(
                    "Brix.UI.Builder.Menu.SpNewGameStarterMenu",
                    "OnCancel", "ObserveStarterClosed");
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
            {
                if (!observedMapAssets.ContainsKey(key))
                {
                    mapRegistrationCount++;
                    Logger.LogInfo("[AP MENU] Registered native map " + key +
                        " (catalog size=" + mapRegistrationCount + ")");
                }
                observedMapAssets[key] = gameMap;
                if (nativeStarterMenu != null)
                    SaveObservedMapIds(nativeMapScanComplete);
            }
            else if (!unknownMapTypeLogged && gameMap != null)
            {
                unknownMapTypeLogged = true;
                Logger.LogInfo("[AP MENU] GetAssetLabel candidate type=" +
                    gameMap.GetType().FullName + "; map identity unresolved.");
            }
        }

        private void TryDiscoverNativeMaps(object menu)
        {
            if (menu == null) return;
            try
            {
                Type type = menu.GetType();
                var flags = BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic;
                foreach (FieldInfo field in type.GetFields(flags))
                    if (LooksLikeMapCollection(field.Name))
                        ObserveMapCollection(field.GetValue(menu));
                foreach (PropertyInfo property in type.GetProperties(flags))
                    if (LooksLikeMapCollection(property.Name) &&
                        property.GetIndexParameters().Length == 0 && property.CanRead)
                    {
                        try { ObserveMapCollection(property.GetValue(menu, null)); }
                        catch (Exception) { /* Some properties are scene-specific. */ }
                    }
                // Prefer known explicit map-list methods to searching game-wide globals.
                foreach (string name in new [] { "GetMaps", "GetMapAssets",
                                                  "GetAvailableMaps", "GetAllMaps" })
                {
                    var method = AccessTools.Method(type, name, Type.EmptyTypes);
                    if (method != null)
                        try { ObserveMapCollection(method.Invoke(menu, new object[0])); }
                        catch (Exception) { }
                }
                nativeMapScanComplete = true;
                SaveObservedMapIds(true);
                Logger.LogInfo("[AP MENU] Map discovery after OpenSelf: " +
                    observedMapAssets.Count + " native assets. Stable IDs saved to installed_maps.json.");
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP MENU] Map discovery failed: " + error.Message);
            }
        }

        private static bool LooksLikeMapCollection(string memberName)
        {
            if (String.IsNullOrEmpty(memberName)) return false;
            string name = memberName.ToLowerInvariant();
            return name.Contains("map") || name.Contains("asset") ||
                name.Contains("library");
        }

        private void ObserveMapCollection(object collection)
        {
            if (collection == null || collection is string) return;
            var values = collection as System.Collections.IEnumerable;
            if (values == null) return;
            int inspected = 0;
            foreach (object value in values)
            {
                if (inspected++ >= 200) break;
                if (value == null) continue;
                RegisterAvailableMap(value);
                foreach (string property in new [] { "Map", "Asset", "Value" })
                {
                    var info = AccessTools.Property(value.GetType(), property);
                    if (info == null || !info.CanRead || info.GetIndexParameters().Length != 0)
                        continue;
                    try { RegisterAvailableMap(info.GetValue(value, null)); }
                    catch (Exception) { }
                }
            }
        }

        private string ModeIdentity(object modeObject)
        {
            if (modeObject == null) return null;
            foreach (string methodName in new [] { "GetIntroImagePath", "GetVictoryImagePath" })
            {
                try
                {
                    var getter = AccessTools.Method(modeObject.GetType(), methodName, Type.EmptyTypes);
                    string path = getter == null ? null : getter.Invoke(modeObject, null) as string;
                    if (!String.IsNullOrEmpty(path))
                    {
                        string normalized = path.Replace('\\', '/');
                        if (normalized.IndexOf("conquest", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "Conquest";
                        if (normalized.IndexOf("invasion", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "Invasion";
                        string folder = Path.GetFileName(Path.GetDirectoryName(
                            path.Replace('/', Path.DirectorySeparatorChar)
                                .Replace('\\', Path.DirectorySeparatorChar)));
                        if (!String.IsNullOrEmpty(folder)) return folder;
                    }
                }
                catch (Exception error) { Logger.LogWarning("[AP MENU] Mode identity: " + error.Message); }
            }
            return null;
        }

        private static void ObserveStarterOpened(object __instance)
        {
            if (Instance != null)
            {
                Instance.nativeStarterMenu = __instance;
                Instance.nativeMapScanComplete = false;
                Instance.TryDiscoverNativeMaps(__instance);
                Instance.nativeNewGameVisible = Instance.bridgeConnected;
                Instance.apMenuOpen = false;
                Instance.Logger.LogInfo("[AP MENU] New Game opened; Archipelago mission UI=" +
                    Instance.nativeNewGameVisible + " campaign ready=" +
                    (Instance.campaign != null && Instance.campaign.ready) +
                    " missions=" + (Instance.campaign == null ||
                    Instance.campaign.missions == null ? 0 :
                    Instance.campaign.missions.Length));
            }
        }

        private static void ObserveStarterClosed(object __instance)
        {
            if (Instance == null || !Object.ReferenceEquals(Instance.nativeStarterMenu, __instance))
                return;
            Instance.nativeNewGameVisible = false;
            Instance.Logger.LogInfo("[AP MENU] Native New Game screen closed.");
        }

        private void CloseNativeNewGame()
        {
            nativeNewGameVisible = false;
            if (nativeStarterMenu == null) return;
            try
            {
                var method = AccessTools.Method(nativeStarterMenu.GetType(),
                    "CloseSelf", Type.EmptyTypes);
                if (method == null)
                    method = AccessTools.Method(nativeStarterMenu.GetType(),
                        "OnBack", Type.EmptyTypes);
                if (method == null)
                {
                    statusLine = "Use Castle Story's Back button to close New Game.";
                    return;
                }
                method.Invoke(nativeStarterMenu, new object[0]);
                nativeStarterMenu = null;
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP MENU] Could not close native menu: " + error);
            }
        }

        private bool unknownMapPathLogged;
        private string MapIdentity(object map)
        {
            if (map == null) return null;
            try
            {
                var method = AccessTools.Method(map.GetType(), "GetHeightmapPath", Type.EmptyTypes);
                string path = method == null ? null : method.Invoke(map, null) as string;
                if (String.IsNullOrEmpty(path)) return null;
                string normalized = path.Replace('\\', '/');
                if (!normalized.StartsWith("/")) normalized = "/" + normalized;
                // Stable internal map folders; NEVER use localized Asset_Map labels.
                string[] needles = { "/Info/UGC/Maps/", "/Info/Maps/", "/Maps/" };
                string[] prefixes = { "workshop:", "official:", "official:" };
                for (int i = 0; i < needles.Length; i++)
                {
                    int marker = normalized.IndexOf(needles[i],
                        StringComparison.OrdinalIgnoreCase);
                    if (marker < 0) continue;
                    string rest = normalized.Substring(marker + needles[i].Length);
                    int slash = rest.IndexOf('/');
                    string folder = slash >= 0 ? rest.Substring(0, slash) : rest;
                    if (!String.IsNullOrEmpty(folder) && folder != "." && folder != "..")
                        return prefixes[i] + folder;
                }
                if (!unknownMapPathLogged)
                {
                    unknownMapPathLogged = true;
                    Logger.LogWarning("[AP MAPS] Unknown native heightmap path layout: " +
                        normalized + ". No map identity was guessed.");
                }
                return null;
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
            string wantedId = MissionMapId(mission);
            if (String.IsNullOrEmpty(wantedId))
            {
                statusLine = "La seed ne fournit pas d'identifiant de carte.";
                return;
            }
            if (!observedMapAssets.TryGetValue(wantedId, out asset))
            {
                TryDiscoverNativeMaps(nativeStarterMenu);
                if (!observedMapAssets.TryGetValue(wantedId, out asset))
                {
                    statusLine = "Identifiant introuvable : " + wantedId +
                        " (" + observedMapAssets.Count + " cartes vues). " +
                        "Consulte installed_maps.json et la configuration ArchiLAN.";
                    Logger.LogWarning("[AP MAPS] Unresolved seed map_id=" + wantedId +
                        "; native IDs=" + String.Join(",",
                            new List<string>(observedMapAssets.Keys).ToArray()));
                    return;
                }
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
                object gameMode = null;
                var getModes = AccessTools.Method(starter, "GetGamemodes");
                if (getModes != null)
                {
                    var available = getModes.Invoke(nativeStarterMenu, new [] { asset })
                        as System.Collections.IEnumerable;
                    if (available != null)
                        foreach (object candidate in available)
                            if (String.Equals(ModeIdentity(candidate), mission.mode,
                                              StringComparison.OrdinalIgnoreCase))
                            {
                                gameMode = candidate;
                                break;
                            }
                }
                if (gameMode == null)
                {
                    object current = selectedMode.GetValue(nativeStarterMenu, null);
                    if (String.Equals(ModeIdentity(current), mission.mode,
                                      StringComparison.OrdinalIgnoreCase))
                        gameMode = current;
                }
                if (gameMode == null)
                {
                    statusLine = "Cannot find " + mission.mode +
                        " game mode for this map. Select it manually.";
                    return;
                }
                if (selectedMode.CanWrite)
                    selectedMode.SetValue(nativeStarterMenu, gameMode, null);
                selectedMap.SetValue(nativeStarterMenu, asset, null);
                Logger.LogInfo("[AP MENU] Auto-selected map ID " + wantedId +
                    " for " + mission.id);
                var start = AccessTools.Method(starter, "OnOk", Type.EmptyTypes);
                if (start == null)
                {
                    statusLine = "Native start method unavailable; manual start required.";
                    return;
                }
                start.Invoke(nativeStarterMenu, new object[0]);
                statusLine = "Lancement de " + (mission.map ?? wantedId) + "...";
                nativeNewGameVisible = false;
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
            // Vanilla mode stays untouched when AP is disconnected.
            if (!bridgeConnected) return true;
            if (menuInstance == null ||
                !menuInstance.GetType().Name.Contains("SpNewGameStarterMenu"))
                return true;
            if (campaign == null || !campaign.ready ||
                campaign.missions == null || campaign.missions.Length == 0)
            {
                statusLine = "The connected AP campaign has no playable map list.";
                Logger.LogWarning("[AP MENU] Native start blocked: missing/unready seed campaign.");
                nativeNewGameVisible = true;
                return false;
            }
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
                    statusLine = "Choisis la carte ID " + MissionMapId(selected) +
                        " avant de démarrer.";
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
                if (!String.Equals(actual, MissionMapId(selected),
                                   StringComparison.OrdinalIgnoreCase))
                {
                    statusLine = "ID attendu : " + MissionMapId(selected) +
                        " (carte choisie : " + actual + ")";
                    Logger.LogWarning("[AP MENU] Blocked map_id=" + actual +
                        " for mission " + selected.id +
                        " expected map_id=" + MissionMapId(selected));
                    return false;
                }
                var modeProperty = AccessTools.Property(menuInstance.GetType(), "SelectedGamemode");
                object actualMode = modeProperty == null ? null :
                    modeProperty.GetValue(menuInstance, null);
                string code = ModeIdentity(actualMode);
                if (code == null ||
                    !String.Equals(code, selected.mode, StringComparison.OrdinalIgnoreCase))
                {
                    statusLine = "Expected game mode: " + selected.mode +
                        " (selected " + (code ?? "<unknown>") + ")";
                    Logger.LogWarning("[AP MENU] Blocked wrong game mode.");
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
