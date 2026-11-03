using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
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
        private float nextSaveScanAt;
        private float nextLoadMenuFilterAt;
        private int pendingLoadFilterPasses;
        private int lastSaveMenuDiagnosticCount;

        private string SaveTagsPath
        {
            get { return Path.Combine(directory, "save_tags.json"); }
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
            if (File.Exists(SaveTagsPath))
            {
                try
                {
                    var registry = JsonUtility.FromJson<ApSaveRegistry>(
                        File.ReadAllText(SaveTagsPath));
                    if (registry != null && registry.saves != null)
                        foreach (ApSaveTag item in registry.saves)
                        {
                            if (item == null || String.IsNullOrEmpty(item.folder) ||
                                String.IsNullOrEmpty(item.token)) continue;
                            apSaveTags[NormalizeSaveFolder(item.folder)] = item;
                        }
                }
                catch (Exception error)
                {
                    Logger.LogWarning("[AP SAVE] Invalid save_tags.json: " +
                        error.GetType().Name);
                    // Never overwrite corrupt metadata without an explicit
                    // backup. Unknown saves remain untagged.
                }
            }
            Logger.LogInfo("[AP SAVE] Registry loaded tags=" + apSaveTags.Count +
                " saveRoot=" + (apSaveRoot == null ? "not-found" :
                    Path.GetFileName(apSaveRoot)));
            if (apSaveRoot != null) SnapshotNativeSaveFolders();
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

        private void ScanNewNativeSaves()
        {
            if (!IsSeedGameplayActive() || !saveBaselineReady ||
                String.IsNullOrEmpty(apSaveRoot) ||
                !Directory.Exists(apSaveRoot)) return;
            try
            {
                foreach (string folder in Directory.GetDirectories(apSaveRoot))
                {
                    string name = NormalizeSaveFolder(folder);
                    if (!knownSaveFolders.Add(name)) continue;
                    if (apSaveTags.ContainsKey(name))
                    {
                        // An existing known AP save must never be silently
                        // reassigned to a different multiworld.
                        Logger.LogInfo("[AP SAVE] Existing tagged folder unchanged: " + name);
                        continue;
                    }
                    var tag = new ApSaveTag {
                        folder = name, token = token, seed = seed,
                        slot = loginSlot, mode = mode,
                        mission = startedMission,
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
            string temp = SaveTagsPath + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonUtility.ToJson(new ApSaveRegistry {
                    schema = 1, saves = apSaveTags.Values.OrderBy(
                        entry => entry.folder, StringComparer.OrdinalIgnoreCase).ToArray()
                }, true));
                if (File.Exists(SaveTagsPath))
                    File.Replace(temp, SaveTagsPath, null);
                else File.Move(temp, SaveTagsPath);
            }
            catch (Exception error)
            {
                Logger.LogError("[AP SAVE] Cannot persist save tags: " + error.Message);
            }
        }

        private bool IsAllowedNativeSave(string folder)
        {
            folder = NormalizeSaveFolder(folder);
            ApSaveTag tag;
            bool tagged = apSaveTags.TryGetValue(folder, out tag);
            // With AP connected only saves tagged for THIS seed/slot are allowed.
            if (bridgeConnected || (!disconnectPending &&
                !String.IsNullOrEmpty(token) && campaign != null && campaign.ready))
                return tagged && String.Equals(tag.token, token,
                    StringComparison.Ordinal);
            // Vanilla mode must not expose Archipelago-tagged saves.
            return !tagged;
        }

        private void ScheduleNativeSaveFilter(string source)
        {
            if (String.IsNullOrEmpty(apSaveRoot))
                apSaveRoot = FindCastleStorySaveRoot();
            if (apSaveRoot == null) return;
            pendingLoadFilterPasses = 3;
            nextLoadMenuFilterAt = Time.realtimeSinceStartup;
            Logger.LogInfo("[AP SAVE UI] Filter scheduled from " + source);
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
                string folder = NormalizeSaveFolder(caption);
                if (!known.Contains(folder)) continue;
                GameObject card = FindNativeChoiceRoot(label);
                if (card == null || hiddenNativeEntries.ContainsKey(card))
                {
                    unknown++;
                    continue;
                }
                matched++;
                if (!IsAllowedNativeSave(folder))
                {
                    HideNativeEntry(card);
                    hidden++;
                }
            }
            if (matched > 0 || lastSaveMenuDiagnosticCount++ < 2)
                Logger.LogInfo("[AP SAVE UI] matched=" + matched +
                    " hidden=" + hidden + " noCard=" + unknown +
                    " tagged=" + apSaveTags.Count +
                    " AP=" + bridgeConnected +
                    " currentSeed=" + (!String.IsNullOrEmpty(token)));
        }

        private void UpdateSaveIsolation()
        {
            float now = Time.realtimeSinceStartup;
            if (now >= nextSaveScanAt)
            {
                nextSaveScanAt = now + 3f;
                ScanNewNativeSaves();
            }
            if (pendingLoadFilterPasses > 0 && now >= nextLoadMenuFilterAt)
            {
                pendingLoadFilterPasses--;
                nextLoadMenuFilterAt = now + .13f;
                FilterNativeSaveChoices();
            }
        }

        private void InstallNativeSaveMenuHooks()
        {
            // Discover real load menus by their type, never patch game save
            // serialization or modify proprietary files.
            Type baseMenu = AccessTools.TypeByName("Brix.UI.Builder.Menu.BaseMenu");
            if (baseMenu == null) return;
            int found = 0, patched = 0;
            foreach (Type candidate in baseMenu.Assembly.GetTypes())
            {
                string typeName = candidate.Name.ToLowerInvariant();
                if (!(typeName.Contains("load") || typeName.Contains("save")) ||
                    !baseMenu.IsAssignableFrom(candidate) ||
                    candidate.IsAbstract) continue;
                found++;
                Logger.LogInfo("[AP SAVE API] Native menu " + candidate.FullName);
                MethodInfo onOk = candidate.GetMethod("OnOk",
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
                if (onOk == null) continue;
                try
                {
                    harmony.Patch(onOk, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(CastleStoryArchipelagoPlugin),
                            "BeforeNativeLoadConfirmed")));
                    patched++;
                }
                catch (Exception e)
                {
                    Logger.LogWarning("[AP SAVE API] Load hook: " + e.GetType().Name);
                }
            }
            Logger.LogInfo("[AP SAVE API] Candidate menus=" + found +
                " guarded OnOk methods=" + patched);
        }

        private static bool BeforeNativeLoadConfirmed(object __instance)
        {
            if (Instance == null) return true;
            return Instance.ValidateNativeLoad(__instance);
        }

        private bool ValidateNativeLoad(object menu)
        {
            if (menu == null || apSaveRoot == null) return true;
            try
            {
                // Never guess selected saves from visible labels. Only an
                // explicit native selection property can authorize loading.
                object chosen = null;
                foreach (string name in new [] {
                    "SelectedSave", "SelectedSaveGame", "SelectedGame",
                    "SelectedItem", "SelectedEntry" })
                {
                    PropertyInfo p = AccessTools.Property(menu.GetType(), name);
                    if (p == null || !p.CanRead ||
                        p.GetIndexParameters().Length > 0) continue;
                    chosen = p.GetValue(menu, null);
                    if (chosen != null) break;
                }
                if (chosen == null)
                {
                    Logger.LogWarning("[AP SAVE] Native load selection unknown; " +
                        "cannot verify seed. No changes to original save.");
                    return true;
                }
                string folder = chosen as string;
                if (folder == null)
                    foreach (string prop in new [] {
                        "FolderName", "SaveName", "Name", "Path", "Directory" })
                    {
                        PropertyInfo info = AccessTools.Property(chosen.GetType(), prop);
                        if (info == null || !info.CanRead ||
                            info.GetIndexParameters().Length > 0) continue;
                        folder = info.GetValue(chosen, null) as string;
                        if (!String.IsNullOrEmpty(folder)) break;
                    }
                if (String.IsNullOrEmpty(folder)) return true;
                folder = NormalizeSaveFolder(folder);
                if (IsAllowedNativeSave(folder))
                {
                    ApSaveTag tag;
                    if (apSaveTags.TryGetValue(folder, out tag) &&
                        tag.token == token)
                    {
                        startedMission = tag.mission ?? "";
                        Logger.LogInfo("[AP SAVE] Resume validated for mission=" +
                            startedMission);
                    }
                    return true;
                }
                Logger.LogWarning("[AP SAVE] Blocked cross-mode/foreign-seed load '" +
                    folder + "'.");
                return false;
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP SAVE] Load guard probe failed: " +
                    error.GetType().Name);
                // Unknown game internals must not break vanilla loading.
                return true;
            }
        }
    }
}
