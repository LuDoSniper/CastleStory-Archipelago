using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

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
        private bool savingNativeMapCatalog;
        private bool nativeMapCatalogDirty;
        private float nativeMapCatalogSaveAt;
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
            // GetGamemodes may itself call game menu callbacks intercepted by
            // RegisterAvailableMap. Prevent recursion and unbounded disk writes.
            if (savingNativeMapCatalog) return;
            savingNativeMapCatalog = true;
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
                nativeMapCatalogDirty = false;
                Logger.LogInfo("[AP MAPS] Exported " + ids.Count + " IDs; " +
                    invasion.Count + " verified Invasion maps and " +
                    conquest.Count + " Conquest maps.");
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP MAPS] Cannot write installed map IDs: " + error.Message);
            }
            finally { savingNativeMapCatalog = false; }
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
                InstallNativeMenuLifecycleHooks();
                InstallTitleMenuLifecycleHooks();
                TryPatchNativeObservation(
                    "Brix.UI.Builder.Menu.BaseNewGameLibraryMenu",
                    "GetAssetLabel", "ObserveMapListed");
                // Filter at native menu construction, before the first
                // rendered frame, rather than showing all maps and removing
                // them visibly after a polling delay.
                TryPatchNativeObservation(
                    "Brix.UI.Builder.Menu.BaseNewGameLibraryMenu",
                    "OpenSelf", "ObserveNativeLibraryOpened");
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

        // Castle Story uses its own Brix menu lifecycle. Detect native
        // screens once on OpenSelf, rather than scanning every button forever.
        private void InstallTitleMenuLifecycleHooks()
        {
            // Some Castle Story builds host the first Play/Options screen
            // outside Brix.BaseMenu. Try its controller without assuming a
            // particular scene name or fixed time since startup.
            foreach (string typeName in new [] {
                "MainMenuViewController",
                "Brix.UI.Builder.Menu.MainMenuViewController",
                "Brix.UI.Builder.Menu.TitleMenu" })
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null) continue;
                MethodInfo method = null;
                foreach (string name in new [] { "Show", "ShowSelf", "Open", "OpenSelf",
                                                 "OnEnable" })
                {
                    method = type.GetMethod(name,
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                        null, Type.EmptyTypes, null);
                    if (method != null) break;
                }
                if (method == null) continue;
                try
                {
                    var callback = AccessTools.Method(
                        typeof(CastleStoryArchipelagoPlugin), "ObserveNativeTitleReady");
                    harmony.Patch(method, postfix: new HarmonyMethod(callback));
                    Logger.LogInfo("[AP CREDITS] Hooked native title callback " +
                        method.DeclaringType.FullName + "." + method.Name);
                    return;
                }
                catch (Exception error)
                {
                    Logger.LogWarning("[AP CREDITS] Title hook failed: " +
                        error.GetType().Name);
                }
            }
            Logger.LogInfo("[AP CREDITS] No separate title controller callback found; " +
                "will rely on Brix menu readiness.");
        }

        private static void ObserveNativeTitleReady(object __instance)
        {
            if (Instance == null) return;
            Instance.Logger.LogInfo("[AP NATIVE SCREEN] Title ready: " +
                (__instance == null ? "unknown" : __instance.GetType().FullName));
            Instance.BeginOwnIntroCredit();
            Instance.mainMenuProbeCount = 0;
            Instance.nextMainMenuProbeAt = Time.realtimeSinceStartup;
        }

        private void InstallNativeMenuLifecycleHooks()
        {
            Type baseType = AccessTools.TypeByName("Brix.UI.Builder.Menu.BaseMenu");
            if (baseType == null)
            {
                Logger.LogWarning("[AP NATIVE UI] Brix BaseMenu unavailable.");
                return;
            }
            int installed = 0;
            try
            {
                var callback = AccessTools.Method(
                    typeof(CastleStoryArchipelagoPlugin), "ObserveAnyMenuOpened");
                foreach (Type candidate in baseType.Assembly.GetTypes())
                {
                    if (candidate == null || !baseType.IsAssignableFrom(candidate) ||
                        candidate.IsAbstract) continue;
                    if (candidate.Namespace == null ||
                        !candidate.Namespace.StartsWith("Brix.UI.Builder.Menu",
                             StringComparison.Ordinal)) continue;
                    MethodInfo method = candidate.GetMethod("OpenSelf",
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                        null, Type.EmptyTypes, null);
                    if (method == null || method.IsAbstract) continue;
                    harmony.Patch(method, postfix: new HarmonyMethod(callback));
                    installed++;
                }
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP NATIVE UI] Menu lifecycle: " + error.Message);
            }
            Logger.LogInfo("[AP NATIVE UI] Hooked " + installed +
                " real Brix menu OpenSelf methods.");
        }

        private static void ObserveAnyMenuOpened(object __instance)
        {
            if (Instance == null || __instance == null) return;
            var plugin = Instance;
            string simple = __instance.GetType().Name.ToLowerInvariant();
            // Castle Story fires LuaMenu.OpenSelf continuously in gameplay.
            // The old generic hook scheduled full UI reflection passes
            // hundreds of times after a map started. Only SceneMenu needs
            // title-submenu filtering; gameplay Lua menus are untouched.
            if (simple == "luamenu")
            {
                if (plugin.titleSceneSeen && plugin.bridgeConnected &&
                    Time.realtimeSinceStartup - plugin.lastLuaTitleFilterAt > .65f)
                {
                    plugin.lastLuaTitleFilterAt = Time.realtimeSinceStartup;
                    plugin.ScheduleNativeUiFilter("title LuaMenu");
                }
                return;
            }
            string name = __instance.GetType().FullName;
            plugin.Logger.LogInfo("[AP NATIVE SCREEN] OpenSelf: " + name);
            if (plugin.titleSceneSeen || plugin.nativeStarterMenu != null)
                plugin.ScheduleNativeUiFilter(name);
            if (simple.Contains("load") || simple.Contains("save"))
            {
                plugin.ProbeNativeSaveSelection(__instance);
                if (simple.Contains("load"))
                    plugin.ScheduleNativeSaveFilter(name);
            }
            // The extra credit begins after the native title menu is ready,
            // not over the initial studio splash sequence.
            if (simple.Contains("title") || simple.Contains("mainmenu"))
            {
                plugin.BeginOwnIntroCredit();
                plugin.mainMenuProbeCount = 0;
                plugin.nextMainMenuProbeAt = Time.realtimeSinceStartup;
            }
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

        private static void ObserveNativeLibraryOpened(object __instance)
        {
            if (Instance == null) return;
            Instance.ScheduleNativeUiFilter("native library OpenSelf");
            Instance.RefreshNativeSeedMenu();
            Instance.Logger.LogInfo("[AP NATIVE UI] Library opened; seed filter applied before first frame.");
        }

        private static void ObserveMapListed(object __0, object __result)
        {
            if (Instance == null) return;
            Instance.RegisterAvailableMap(__0);
            // GetAssetLabel gives the native localized label for a technical
            // Asset_Map. Retain that mapping for precise native UI filtering.
            Instance.ObserveNativeMapLabel(__0, __result as string);
        }

        private void RegisterAvailableMap(object gameMap)
        {
            string key = MapIdentity(gameMap);
            if (!String.IsNullOrEmpty(key))
            {
                bool isNew = !observedMapAssets.ContainsKey(key);
                if (isNew)
                {
                    mapRegistrationCount++;
                    Logger.LogInfo("[AP MENU] Registered native map " + key +
                        " (catalog size=" + mapRegistrationCount + ")");
                }
                observedMapAssets[key] = gameMap;
                if (isNew)
                {
                    // Never regenerate+write the ENTIRE map catalog for every
                    // GetAssetLabel call during tile construction (O(n²)).
                    nativeMapCatalogDirty = true;
                    nativeMapCatalogSaveAt = Time.realtimeSinceStartup + 1.5f;
                }
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
                nativeMapCatalogDirty = false;
                Logger.LogInfo("[AP MENU] Map discovery after OpenSelf: " +
                    observedMapAssets.Count + " native assets. Stable IDs saved to installed_maps.json.");
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP MENU] Map discovery failed: " + error.Message);
            }
        }

        private void FlushNativeMapCatalogIfIdle()
        {
            if (!nativeMapCatalogDirty || savingNativeMapCatalog ||
                nativeStarterMenu == null ||
                Time.realtimeSinceStartup < nativeMapCatalogSaveAt)
                return;
            // A second generation or a late-added workshop card is
            // persisted once, after the map list stops changing.
            SaveObservedMapIds(nativeMapScanComplete);
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
                // Clear obsolete "open New Game first" instructions now that
                // the game's native map selector has actually opened.
                Instance.campaignNotice = "";
                Instance.campaignNoticeIsError = false;
                Instance.nativeMapScanComplete = false;
                Instance.TryDiscoverNativeMaps(__instance);
                // Native selection replaces the custom AP campaign popup.
                // This flag must remain false so the input shield does not
                // steal mouse events from Castle Story's own map selector.
                Instance.nativeNewGameVisible = false;
                Instance.apMenuOpen = false;
                Instance.ScheduleNativeUiFilter("native starter OpenSelf");
                Instance.RefreshNativeSeedMenu();
                Instance.Logger.LogInfo("[AP MENU] Native New Game opened; native seed filtering=" +
                    Instance.bridgeConnected + " campaign ready=" +
                    (Instance.campaign != null && Instance.campaign.ready) +
                    " missions=" + (Instance.campaign == null ||
                    Instance.campaign.missions == null ? 0 :
                    Instance.campaign.missions.Length));
            }
        }

        private static void ObserveStarterClosed(object __instance)
        {
            if (Instance == null || !System.Object.ReferenceEquals(Instance.nativeStarterMenu, __instance))
                return;
            Instance.nativeNewGameVisible = false;
            Instance.nativeStarterMenu = null;
            Instance.apPopupDismissedOnNativeScreen = false;
            // Keep already-filtered cards disabled on menu return. Re-enabling
            // them during CloseSelf triggers the game's native card rebuilding
            // and was causing duplicates upon opening Invasion again.
            Instance.Logger.LogInfo("[AP MENU] Native New Game screen closed; seed filter retained.");
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
                SetCampaignNotice("Connecte-toi à une campagne Archipelago prête avant de lancer.", true);
                return;
            }
            if (nativeStarterMenu == null)
            {
                SetCampaignNotice("Pour lancer : ferme cette fenêtre, ouvre Nouvelle partie dans Castle Story, puis sélectionne et lance la mission.", true);
                Logger.LogWarning("[AP LAUNCH] Blocked: native New Game menu has not been opened.");
                return;
            }
            MissionFile mission = null;
            foreach (var m in campaign.missions)
                if (m != null && m.id == chosenMission) mission = m;
            if (mission == null)
            {
                SetCampaignNotice("Choisis une mission avant de lancer une partie.", true);
                return;
            }
            object asset;
            string wantedId = MissionMapId(mission);
            if (String.IsNullOrEmpty(wantedId))
            {
                SetCampaignNotice("La seed ne fournit pas d'identifiant de carte.", true);
                return;
            }
            if (!observedMapAssets.TryGetValue(wantedId, out asset))
            {
                TryDiscoverNativeMaps(nativeStarterMenu);
                if (!observedMapAssets.TryGetValue(wantedId, out asset))
                {
                    SetCampaignNotice("Carte introuvable : " + wantedId +
                        " (" + observedMapAssets.Count + " cartes vues). " +
                        "Vérifie installed_maps.json et la configuration ArchiLAN.", true);
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
                    SetCampaignNotice("Sélecteur de carte ou de mode indisponible sur cette version du jeu.", true);
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
                    SetCampaignNotice("Mode " + mission.mode +
                        " indisponible sur cette carte. Vérifie le menu natif.", true);
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
                    SetCampaignNotice("Impossible de lancer automatiquement : méthode native absente.", true);
                    return;
                }
                start.Invoke(nativeStarterMenu, new object[0]);
                SetCampaignNotice("Lancement de " + (mission.map ?? wantedId) + "...", false);
                nativeNewGameVisible = false;
            }
            catch (Exception error)
            {
                SetCampaignNotice("Lancement impossible : " + error.Message +
                    ". Consulte BepInEx/LogOutput.log.", true);
                Logger.LogWarning("[AP MENU] Native launch failed: " + error);
            }
        }

        private static bool BeforeNativeNewGame(object __instance)
        {
            return Instance == null || Instance.CanStartNativeMap(__instance);
        }

        private bool CanStartNativeMap(object menuInstance)
        {
            // Only an explicit disconnect or absent session restores vanilla
            // starts. Short bridge drops must not bypass seed restrictions.
            if (disconnectPending || String.IsNullOrEmpty(token)) return true;
            // This prefix is installed on BaseMapStarterMenu.OnOk and the
            // single-player override. Validate every native map start, so
            // a sandbox variant cannot bypass the seed through another
            // BaseMapStarterMenu subtype or by keyboard navigation.
            if (menuInstance == null)
            {
                Logger.LogWarning("[AP NATIVE UI] Blocked null native map starter.");
                return false;
            }
            if (campaign == null || !campaign.ready ||
                campaign.missions == null || campaign.missions.Length == 0)
            {
                SetCampaignNotice("La seed Archipelago ne contient aucune carte jouable.", true);
                Logger.LogWarning("[AP NATIVE UI] Blocked launch without a ready campaign.");
                return false;
            }
            try
            {
                var mapProperty = AccessTools.Property(menuInstance.GetType(), "SelectedMap");
                var modeProperty = AccessTools.Property(menuInstance.GetType(), "SelectedGamemode");
                object map = mapProperty == null ? null : mapProperty.GetValue(menuInstance, null);
                object selectedMode = modeProperty == null ? null :
                    modeProperty.GetValue(menuInstance, null);
                string mapId = MapIdentity(map);
                string modeId = ModeIdentity(selectedMode);
                if (String.IsNullOrEmpty(mapId) || String.IsNullOrEmpty(modeId))
                {
                    SetCampaignNotice(
                        "Sélectionne une carte et un mode disponibles dans la seed.",
                        true);
                    Logger.LogWarning("[AP NATIVE UI] Missing selected map/mode in native start.");
                    return false;
                }
                MissionFile matching = null;
                foreach (MissionFile mission in campaign.missions)
                {
                    if (mission == null ||
                        !String.Equals(MissionMapId(mission), mapId,
                                       StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(mission.mode, modeId,
                                       StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (matching != null)
                    {
                        SetCampaignNotice(
                            "Plusieurs missions utilisent cette carte et ce mode. " +
                            "Une sélection explicite sera nécessaire.", true);
                        Logger.LogWarning("[AP NATIVE UI] Ambiguous seed mapping for " +
                            mapId + " / " + modeId);
                        return false;
                    }
                    matching = mission;
                }
                if (matching == null)
                {
                    SetCampaignNotice(
                        "Cette carte ou ce mode n'appartient pas à la seed Archipelago.",
                        true);
                    Logger.LogWarning("[AP NATIVE UI] Rejected unseeded native start: " +
                        mapId + " / " + modeId);
                    return false;
                }
                // The user's native selection is authoritative. No custom
                // popup is necessary to select a map or mission.
                SelectMission(matching);
                startedMission = matching.id;
                PrepareNativeSaveTracking();
                lastRejectedWave = -1;
                Logger.LogInfo("[AP MISSION START] " + matching.id +
                    " map=" + mapId + " mode=" + modeId +
                    " via=native-menu");
                return true;
            }
            catch (Exception error)
            {
                SetCampaignNotice("Erreur du sélecteur natif : " + error.Message, true);
                Logger.LogWarning("[AP NATIVE UI] Native start guard: " + error);
                return false;
            }
        }
    }
}
