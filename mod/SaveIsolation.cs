using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using BepInEx;
using UnityEngine;

namespace CastleStoryArchipelago
{
    public sealed partial class CastleStoryArchipelagoPlugin
    {
        // Save metadata is separate from the proprietary Castle Story save.
        // Nothing in Info/Saves is rewritten or moved by Archipelago.
        [Serializable] private sealed class ApSaveTag
        {
            public string folder;
            public string token;
            public string seed;
            public string slot;
            public string mode;
            public string mission;
            public string map_id;
            public string created_utc;
        }

        [Serializable] private sealed class ApSaveRegistry
        {
            public int schema = 1;
            public ApSaveTag[] saves;
        }

        private readonly Dictionary<string, ApSaveTag> apSaveTags =
            new Dictionary<string, ApSaveTag>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> knownSaveFolders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string apSaveRoot;
        private bool saveBaselineReady;
        private bool saveRegistryWritable = true;
        private float nextSaveScanAt;
        private float nextLoadMenuFilterAt;
        private int pendingLoadFilterPasses;
        private int lastSaveMenuDiagnosticCount;
        private readonly HashSet<string> probedNativeLoadMenus =
            new HashSet<string>(StringComparer.Ordinal);

        private string SaveTagsPath
        {
            get { return Path.Combine(directory, "save_tags.json"); }
        }

        private string SaveTagsMirrorPath
        {
            get { return String.IsNullOrEmpty(apSaveRoot) ? null :
                Path.Combine(apSaveRoot, ".archipelago-save-tags.json"); }
        }

        private void MergeSaveTagsFrom(string path, bool primary)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                var registry = JsonUtility.FromJson<ApSaveRegistry>(
                    File.ReadAllText(path));
                if (registry == null || registry.saves == null)
                    throw new InvalidDataException("Missing saves array");
                int imported = 0;
                foreach (ApSaveTag item in registry.saves)
                {
                    if (item == null || String.IsNullOrEmpty(item.folder) ||
                        String.IsNullOrEmpty(item.token)) continue;
                    string key = NormalizeSaveFolder(item.folder);
                    if (primary || !apSaveTags.ContainsKey(key))
                    {
                        apSaveTags[key] = item;
                        imported++;
                    }
                }
                Logger.LogInfo("[AP SAVE] Imported " + imported +
                    " tags from " + Path.GetFileName(path));
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP SAVE] Failed to read " +
                    Path.GetFileName(path) + ": " + error.GetType().Name);
                if (primary) saveRegistryWritable = false;
            }
        }

        private static string NormalizeSaveFolder(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            return Path.GetFileName(value.Trim().TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
        }

        private static string FindCastleStorySaveRoot()
        {
            // The game's Windows save root documented in current builds:
            // <Castle Story>/Info/Saves/<save-name>/...
            string root = Path.Combine(Paths.GameRootPath, "Info");
            foreach (string subfolder in new [] { "Saves", "Save", "Sauvegarde" })
            {
                string candidate = Path.Combine(root, subfolder);
                if (Directory.Exists(candidate)) return candidate;
            }
            // A few distributions store saves under LocalLow instead.
            string local = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            string persistent = Path.Combine(Path.Combine(
                local, "Low"), "Castle Story");
            string alternate = Path.Combine(persistent, "SaveGames");
            return Directory.Exists(alternate) ? alternate : null;
        }

        private void InitializeApSaveRegistry()
        {
            apSaveRoot = FindCastleStorySaveRoot();
            // Info/Saves sidecar survives BepInEx upgrades and contains no
            // proprietary game data. It is a backup index, NOT a saved game.
            MergeSaveTagsFrom(SaveTagsMirrorPath, false);
            // Older installer versions could have kept an archive of the
            // primary registry under upgrade-backup-*. Prefer the live file.
            try
            {
                foreach (string backup in Directory.GetDirectories(directory,
                    "upgrade-backup-*").OrderByDescending(x => x))
                    MergeSaveTagsFrom(Path.Combine(backup, "save_tags.json"), false);
            }
            catch (Exception) { /* No legacy installer backups. */ }
            MergeSaveTagsFrom(SaveTagsPath, true);
            Logger.LogInfo("[AP SAVE] Registry loaded tags=" + apSaveTags.Count +
                " saveRoot=" + (apSaveRoot == null ? "not-found" :
                    Path.GetFileName(apSaveRoot)));
            if (apSaveRoot != null) SnapshotNativeSaveFolders();
            // Reconstruct the primary index if it was removed by reinstalling
            // BepInEx but a durable sidecar or upgrade backup survived.
            if (!File.Exists(SaveTagsPath) && apSaveTags.Count > 0)
                PersistApSaveRegistry();
        }

        private void SnapshotNativeSaveFolders()
        {
            if (String.IsNullOrEmpty(apSaveRoot) ||
                !Directory.Exists(apSaveRoot)) return;
            try
            {
                foreach (string folder in Directory.GetDirectories(apSaveRoot))
                    knownSaveFolders.Add(NormalizeSaveFolder(folder));
                saveBaselineReady = true;
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP SAVE] Cannot read saves: " +
                    error.GetType().Name);
            }
        }

        private void PrepareNativeSaveTracking()
        {
            // Called only after the map/mode has been validated against seed.
            if (String.IsNullOrEmpty(apSaveRoot))
                apSaveRoot = FindCastleStorySaveRoot();
            SnapshotNativeSaveFolders();
            nextSaveScanAt = Time.realtimeSinceStartup + 2f;
            Logger.LogInfo("[AP SAVE] Seed mission started; pre-existing saves=" +
                knownSaveFolders.Count + " mission=" + startedMission);
        }

        private bool IsSeedGameplayActive()
        {
            return !String.IsNullOrEmpty(startedMission) &&
                !String.IsNullOrEmpty(token) && !disconnectPending &&
                campaign != null && campaign.ready &&
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name !=
                    "SceneMenu";
        }

        private void ScanNewNativeSaves(bool finishingRun = false)
        {
            if (!finishingRun && !IsSeedGameplayActive()) return;
            if (finishingRun && (String.IsNullOrEmpty(startedMission) ||
                String.IsNullOrEmpty(token) || disconnectPending))
                return;
            if (String.IsNullOrEmpty(apSaveRoot) ||
                !Directory.Exists(apSaveRoot))
            {
                apSaveRoot = FindCastleStorySaveRoot();
                if (String.IsNullOrEmpty(apSaveRoot)) return;
                // The first save may create the root itself. Treat folders
                // that appear after validated start as new AP saves.
                saveBaselineReady = true;
            }
            if (!saveBaselineReady) return;
            try
            {
                foreach (string folder in Directory.GetDirectories(apSaveRoot))
                {
                    string name = NormalizeSaveFolder(folder);
                    // Wait until Castle Story actually writes data into
                    // the new directory. Avoid tagging empty folders created
                    // manually or by unrelated tools.
                    if (Directory.GetFiles(folder).Length == 0) continue;
                    if (!knownSaveFolders.Add(name)) continue;
                    if (apSaveTags.ContainsKey(name))
                    {
                        // An existing known AP save must never be silently
                        // reassigned to a different multiworld.
                        Logger.LogInfo("[AP SAVE] Existing tagged folder unchanged: " + name);
                        continue;
                    }
                    MissionFile active = campaign == null ||
                        campaign.missions == null ? null :
                        campaign.missions.FirstOrDefault(item =>
                            item != null && item.id == startedMission);
                    var tag = new ApSaveTag {
                        folder = name, token = token, seed = seed,
                        slot = loginSlot, mode = mode,
                        mission = startedMission,
                        map_id = active == null ? "" : MissionMapId(active),
                        created_utc = DateTime.UtcNow.ToString("o")
                    };
                    apSaveTags[name] = tag;
                    PersistApSaveRegistry();
                    Logger.LogInfo("[AP SAVE] Tagged newly created Castle Story save '" +
                        name + "' for current Archipelago seed.");
                }
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP SAVE] Save discovery failed: " +
                    error.GetType().Name);
            }
        }

        private void PersistApSaveRegistry()
        {
            if (!saveRegistryWritable) return;
            string temp = SaveTagsPath + ".tmp";
            try
            {
                string contents = JsonUtility.ToJson(new ApSaveRegistry {
                    schema = 1, saves = apSaveTags.Values.OrderBy(
                        entry => entry.folder, StringComparer.OrdinalIgnoreCase).ToArray()
                }, true);
                File.WriteAllText(temp, contents);
                if (File.Exists(SaveTagsPath))
                    File.Replace(temp, SaveTagsPath, null);
                else File.Move(temp, SaveTagsPath);

                string mirror = SaveTagsMirrorPath;
                if (!String.IsNullOrEmpty(mirror))
                {
                    try
                    {
                        string mirrorTemp = mirror + ".tmp";
                        File.WriteAllText(mirrorTemp, contents);
                        if (File.Exists(mirror))
                            File.Replace(mirrorTemp, mirror, null);
                        else File.Move(mirrorTemp, mirror);
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning("[AP SAVE] Sidecar backup unavailable: " +
                            e.GetType().Name);
                    }
                }
            }
            catch (Exception error)
            {
                Logger.LogError("[AP SAVE] Cannot persist save tags: " + error.Message);
            }
        }

        private bool IsArchipelagoSaveSessionActive()
        {
            // The ArchiLAN bridge drops its heartbeat for ~0.7s several times
            // in the supplied logs. That MUST NOT temporarily show vanilla
            // saves or disable seed validation. Only explicit disconnect or
            // loss of a valid manifest leaves Archipelago save mode.
            return !disconnectPending && !String.IsNullOrEmpty(token) &&
                campaign != null && campaign.ready;
        }

        private bool IsAllowedNativeSave(string folder)
        {
            folder = NormalizeSaveFolder(folder);
            ApSaveTag tag;
            bool tagged = apSaveTags.TryGetValue(folder, out tag);
            if (IsArchipelagoSaveSessionActive())
                return tagged && String.Equals(tag.token, token,
                    StringComparison.Ordinal);
            // Vanilla mode must not expose Archipelago-tagged saves.
            return !tagged;
        }

        private void ProbeNativeSaveSelection(object menu)
        {
            if (menu == null) return;
            Type type = menu.GetType();
            if (!probedNativeLoadMenus.Add(type.FullName)) return;
            int reported = 0;
            var flags = BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic;
            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                string name = property.Name.ToLowerInvariant();
                if (!name.Contains("save") && !name.Contains("load") &&
                    !name.Contains("selected") && !name.Contains("entry"))
                    continue;
                Logger.LogInfo("[AP SAVE API] property " +
                    property.PropertyType.Name + " " + property.Name);
                if (++reported >= 18) break;
            }
            foreach (FieldInfo field in type.GetFields(flags))
            {
                string name = field.Name.ToLowerInvariant();
                if (!name.Contains("save") && !name.Contains("load") &&
                    !name.Contains("selected") && !name.Contains("entry"))
                    continue;
                Logger.LogInfo("[AP SAVE API] field " +
                    field.FieldType.Name + " " + field.Name);
                if (++reported >= 35) break;
            }
            Logger.LogInfo("[AP SAVE API] Selection probe completed for " + type.FullName);
        }

        private void ScheduleNativeSaveFilter(string source)
        {
            if (!titleSceneSeen ||
                (!IsArchipelagoSaveSessionActive() && apSaveTags.Count == 0))
                return;
            if (String.IsNullOrEmpty(apSaveRoot))
                apSaveRoot = FindCastleStorySaveRoot();
            if (apSaveRoot == null) return;
            // Native Brix load libraries can populate cards after OpenSelf.
            pendingLoadFilterPasses = 4;
            nextLoadMenuFilterAt = Time.realtimeSinceStartup + .03f;
            Logger.LogInfo("[AP SAVE UI] Filter scheduled from " + source);
        }

        private static string SaveFolderForCaption(string caption,
                                                   HashSet<string> saveNames)
        {
            if (String.IsNullOrEmpty(caption)) return null;
            string normalized = caption.Trim();
            if (saveNames.Contains(normalized)) return normalized;
            // Some native Brix cards display a save name followed by a
            // separate timestamp line; match the ENTIRE first line, never
            // arbitrary substring matches that could hide a different save.
            int newline = normalized.IndexOfAny(new [] { '\n', '\r' });
            if (newline > 0)
            {
                string first = normalized.Substring(0, newline).Trim();
                if (saveNames.Contains(first)) return first;
            }
            return null;
        }

        private GameObject FindNativeSaveCardRoot(Component label,
                                                  HashSet<string> saveNames)
        {
            if (label == null) return null;
            Type textType = FindRuntimeType("UnityEngine.UI.Text");
            if (textType == null) return null;
            GameObject card = null;
            Transform node = label.transform.parent;
            for (int level = 0; node != null && level < 6; level++,
                 node = node.parent)
            {
                RectTransform rect = node as RectTransform;
                if (node.GetComponent<Canvas>() != null ||
                    rect == null || rect.rect.height > 450f)
                    break;
                int folderCaptions = 0;
                foreach (Component text in node.GetComponentsInChildren(textType, true))
                {
                    string value = GetUiProperty(text, "text") as string;
                    if (SaveFolderForCaption(value, saveNames) != null)
                        folderCaptions++;
                    if (folderCaptions > 1) break;
                }
                // Stop before the whole list or menu: only one save folder's
                // entry may be hidden, never all the user's save games.
                if (folderCaptions > 1) break;
                if (HasNativeInteractiveSurface(node))
                    card = node.gameObject;
            }
            return card;
        }

        private void FilterNativeSaveChoices()
        {
            if (String.IsNullOrEmpty(apSaveRoot) ||
                !Directory.Exists(apSaveRoot)) return;
            Type textType = FindRuntimeType("UnityEngine.UI.Text");
            if (textType == null) return;
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetDirectories(apSaveRoot))
                known.Add(NormalizeSaveFolder(path));
            int matched = 0, hidden = 0, unknown = 0;
            foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsOfType(textType))
            {
                Component label = obj as Component;
                if (label == null || !label.gameObject.activeInHierarchy)
                    continue;
                string caption = GetUiProperty(label, "text") as string;
                string folder = SaveFolderForCaption(caption, known);
                if (String.IsNullOrEmpty(folder)) continue;
                GameObject card = FindNativeSaveCardRoot(label, known);
                if (card == null ||
                    (hiddenNativeEntries.ContainsKey(card) && !card.activeSelf))
                {
                    unknown++;
                    continue;
                }
                matched++;
                if (!IsAllowedNativeSave(folder))
                {
                    HideNativeEntry(card);
                    hidden++;
                    if (hidden <= 6)
                        Logger.LogInfo("[AP SAVE UI] Hidden off-seed save card: " +
                            folder + " / " + card.name);
                }
            }
            if (matched == 0 && known.Count > 0 && lastSaveMenuDiagnosticCount < 3)
                Logger.LogWarning("[AP SAVE UI] Save folder labels not exposed " +
                    "as native Text; filtering needs the real load-menu hierarchy.");
            if (matched > 0 || lastSaveMenuDiagnosticCount++ < 2)
                Logger.LogInfo("[AP SAVE UI] matched=" + matched +
                    " hidden=" + hidden + " noCard=" + unknown +
                    " tagged=" + apSaveTags.Count +
                    " AP=" + IsArchipelagoSaveSessionActive() +
                    " currentSeed=" + (!String.IsNullOrEmpty(token)));
        }

        private void UpdateSaveIsolation()
        {
            float now = Time.realtimeSinceStartup;
            if (now >= nextSaveScanAt)
            {
                nextSaveScanAt = now + 1f;
                ScanNewNativeSaves();
            }
            if (pendingLoadFilterPasses > 0 && now >= nextLoadMenuFilterAt)
            {
                pendingLoadFilterPasses--;
                nextLoadMenuFilterAt = now + .13f;
                FilterNativeSaveChoices();
            }
        }

        private static void ObserveNativeLoadLibraryOpened(object __instance)
        {
            if (Instance == null || __instance == null) return;
            string name = __instance.GetType().Name;
            if (name.IndexOf("LoadGame", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            Instance.Logger.LogInfo("[AP SAVE UI] Native save library opened: " + name);
            Instance.ProbeNativeSaveSelection(__instance);
            Instance.ScheduleNativeSaveFilter(name + ".OpenSelf");
        }

        private void InstallNativeSaveMenuHooks()
        {
            Type baseMenu = AccessTools.TypeByName("Brix.UI.Builder.Menu.BaseMenu");
            if (baseMenu == null) return;
            int discovered = 0, patchedLibraries = 0;
            var patched = new HashSet<MethodInfo>();
            Type[] candidates;
            try { candidates = baseMenu.Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error)
            {
                candidates = error.Types;
                Logger.LogWarning("[AP SAVE API] Partial type discovery.");
            }
            foreach (Type candidate in candidates)
            {
                if (candidate == null || candidate.IsAbstract ||
                    !baseMenu.IsAssignableFrom(candidate) ||
                    candidate.Name.IndexOf("LoadGame", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                discovered++;
                Logger.LogInfo("[AP SAVE API] Native load menu " + candidate.FullName);
                // The real Castle Story build inherits OpenSelf and OnOk.
                // DeclaredOnly returned 0 load hooks in v0.7.6. Patch each
                // concrete/inherited OpenSelf method once and check the
                // runtime instance type in the callback.
                MethodInfo open = candidate.GetMethod("OpenSelf",
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (open != null && patched.Add(open))
                {
                    try
                    {
                        harmony.Patch(open, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(CastleStoryArchipelagoPlugin),
                                "ObserveNativeLoadLibraryOpened")));
                        patchedLibraries++;
                    }
                    catch (Exception error)
                    {
                        Logger.LogWarning("[AP SAVE API] OpenSelf hook: " +
                            error.GetType().Name);
                    }
                }
            }
            Logger.LogInfo("[AP SAVE API] Native load menus=" + discovered +
                " OpenSelf hooks=" + patchedLibraries +
                "; native starter load guard uses BaseMapStarterMenu.OnOk.");
        }

        private static bool BeforeNativeLoadConfirmed(object __instance)
        {
            if (Instance == null) return true;
            return Instance.ValidateNativeLoad(__instance);
        }

        private static string NativeSaveFolderFromHeightmap(object selectedMap)
        {
            if (selectedMap == null) return null;
            try
            {
                MethodInfo getPath = AccessTools.Method(selectedMap.GetType(),
                    "GetHeightmapPath", Type.EmptyTypes);
                string heightmap = getPath == null ? null :
                    getPath.Invoke(selectedMap, new object[0]) as string;
                if (String.IsNullOrEmpty(heightmap)) return null;
                string normalized = heightmap.Replace('\\', '/');
                const string marker = "/Info/Saves/";
                int index = normalized.IndexOf(marker,
                    StringComparison.OrdinalIgnoreCase);
                if (index < 0) return null;
                string rest = normalized.Substring(index + marker.Length);
                int slash = rest.IndexOf('/');
                if (slash <= 0) return null;
                string folder = rest.Substring(0, slash);
                if (folder == "." || folder == ".." ||
                    folder.IndexOfAny(new [] { ':', '/', '\\' }) >= 0)
                    return null;
                return folder;
            }
            catch (Exception) { return null; }
        }

        private bool ValidateNativeLoadStarter(object starter)
        {
            // SpLoadGameStarterMenu derives from BaseMapStarterMenu; its
            // inherited OnOk was intercepted by our NEW-GAME guard,
            // incorrectly demanding a map/mode from a save heightmap.
            object selectedMap = null;
            if (starter != null)
            {
                PropertyInfo property = AccessTools.Property(starter.GetType(),
                    "SelectedMap");
                if (property != null && property.CanRead)
                    selectedMap = property.GetValue(starter, null);
            }
            string folder = NativeSaveFolderFromHeightmap(selectedMap);
            if (String.IsNullOrEmpty(folder))
            {
                Logger.LogWarning("[AP SAVE LOAD] Cannot resolve save folder from " +
                    "SpLoadGameStarterMenu.SelectedMap.GetHeightmapPath.");
                // Never allow an unverified save into a live AP session.
                return !IsArchipelagoSaveSessionActive();
            }
            return ValidateTaggedSaveFolder(folder);
        }

        private bool ValidateTaggedSaveFolder(string folder)
        {
            folder = NormalizeSaveFolder(folder);
            if (String.IsNullOrEmpty(folder)) return !IsArchipelagoSaveSessionActive();
            if (!IsAllowedNativeSave(folder))
            {
                Logger.LogWarning("[AP SAVE LOAD] Rejected nonmatching or untagged " +
                    "save: " + folder + " AP=" + IsArchipelagoSaveSessionActive());
                statusLine = IsArchipelagoSaveSessionActive() ?
                    "Cette sauvegarde n'appartient pas à la seed Archipelago active." :
                    "Une sauvegarde Archipelago exige une connexion à sa seed.";
                return false;
            }

            ApSaveTag tag;
            if (!apSaveTags.TryGetValue(folder, out tag))
            {
                Logger.LogInfo("[AP SAVE LOAD] Starting ordinary Castle Story save: " +
                    folder);
                return true;
            }
            if (!IsArchipelagoSaveSessionActive() ||
                tag.token != token ||
                campaign.missions == null)
                return false;
            MissionFile mission = campaign.missions.FirstOrDefault(item =>
                item != null && item.id == tag.mission);
            if (mission == null ||
                !String.Equals(tag.mode, mode, StringComparison.Ordinal) ||
                (!String.IsNullOrEmpty(tag.map_id) &&
                 !String.Equals(tag.map_id, MissionMapId(mission),
                    StringComparison.OrdinalIgnoreCase)))
            {
                Logger.LogWarning("[AP SAVE LOAD] Saved mission/map/mode differs " +
                    "from the active campaign: " + folder);
                return false;
            }
            SelectMission(mission);
            startedMission = mission.id;
            PrepareNativeSaveTracking();
            Logger.LogInfo("[AP SAVE LOAD] Resuming mission=" + mission.id +
                " save=" + folder);
            return true;
        }

        private bool ValidateNativeLoad(object menu)
        {
            if (menu == null || String.IsNullOrEmpty(apSaveRoot)) return true;
            try
            {
                // The inherited native starter exposes the *save map*
                // as SelectedMap (its heightmap lies under /Info/Saves/).
                if (menu.GetType().Name.IndexOf("LoadGameStarter",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    return ValidateNativeLoadStarter(menu);

                object selected = null;
                foreach (string name in new [] {
                    "SelectedSave", "SelectedSaveGame", "SelectedGame",
                    "SelectedItem", "SelectedEntry" })
                {
                    PropertyInfo property = AccessTools.Property(menu.GetType(), name);
                    if (property == null || !property.CanRead ||
                        property.GetIndexParameters().Length > 0) continue;
                    selected = property.GetValue(menu, null);
                    if (selected != null) break;
                }
                if (selected == null) return !IsArchipelagoSaveSessionActive();
                string path = selected as string;
                if (path == null)
                    foreach (string prop in new [] {
                        "FolderName", "SaveName", "Name", "Path", "Directory" })
                    {
                        PropertyInfo value = AccessTools.Property(
                            selected.GetType(), prop);
                        if (value == null || !value.CanRead ||
                            value.GetIndexParameters().Length != 0) continue;
                        path = value.GetValue(selected, null) as string;
                        if (!String.IsNullOrEmpty(path)) break;
                    }
                if (String.IsNullOrEmpty(path)) return !IsArchipelagoSaveSessionActive();
                string folder = NormalizeSaveFolder(path);
                if (Path.HasExtension(folder))
                {
                    string parent = Path.GetDirectoryName(path);
                    if (!String.IsNullOrEmpty(parent))
                        folder = NormalizeSaveFolder(parent);
                }
                return ValidateTaggedSaveFolder(folder);
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP SAVE LOAD] Guard exception: " +
                    error.GetType().Name);
                return !IsArchipelagoSaveSessionActive();
            }
        }
    }
}
