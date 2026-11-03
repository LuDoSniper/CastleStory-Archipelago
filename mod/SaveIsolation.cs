using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
            // Persist the real AP slot/team, not a transient bridge process ID.
            public bool has_slot_identity;
            public int team;
            public int slot_id;
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
        private readonly HashSet<string> conflictingSaveTags =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> knownSaveFolders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string apSaveRoot;
        private bool saveBaselineReady;
        private bool saveRegistryWritable = true;
        private float nextSaveScanAt;
        private float nextLoadMenuFilterAt;
        private int pendingLoadFilterPasses;
        private int lastSaveMenuDiagnosticCount;
        private DateTime lastPrimaryTagStamp = DateTime.MinValue;
        private DateTime lastMirrorTagStamp = DateTime.MinValue;
        private float nextSaveRegistryCheckAt;
        private readonly HashSet<string> cachedSaveFolderNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> cachedAllowedSaveNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool apSavePolicyPrepared;
        private string lastSavePolicyToken = null;
        private bool lastSavePolicyActive;
        private bool authenticatedApSaveSession;
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

        private string IndividualSaveTagPath(string folder, bool durable)
        {
            string root = durable ?
                Path.Combine(Path.Combine(Paths.GameRootPath, "Info"),
                    "ArchipelagoSaveTags") :
                Path.Combine(directory, "save_tags.d");
            // An external per-save file is not dependent on one shared index.
            // Hash folder names so they cannot escape this dedicated directory.
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(
                    NormalizeSaveFolder(folder).ToLowerInvariant()));
                var file = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) file.Append(value.ToString("x2"));
                return Path.Combine(root, file + ".json");
            }
        }

        private bool SaveIndividualTag(ApSaveTag tag, bool durable)
        {
            string path = IndividualSaveTagPath(tag.folder, durable);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string contents = JsonUtility.ToJson(tag, true);
                if (File.Exists(path) && File.ReadAllText(path) == contents)
                    return true;
                string temp = path + ".tmp";
                File.WriteAllText(temp, contents);
                if (File.Exists(path)) File.Copy(path, path + ".bak", true);
                File.Copy(temp, path, true);
                File.Delete(temp);
                return true;
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP SAVE SIDECAR] Cannot persist " +
                    (durable ? "durable" : "config") + " tag: " +
                    error.GetType().Name);
                return false;
            }
        }

        private bool PersistIndividualTags()
        {
            bool allSaved = true;
            foreach (ApSaveTag tag in apSaveTags.Values)
            {
                if (tag == null || String.IsNullOrEmpty(tag.folder) ||
                    String.IsNullOrEmpty(tag.seed)) continue;
                bool durable = SaveIndividualTag(tag, true);
                bool config = SaveIndividualTag(tag, false);
                if (!durable && !config) allSaved = false;
            }
            return allSaved;
        }

        private int RestoreIndividualTags()
        {
            int restored = 0;
            foreach (bool durable in new [] { true, false })
            {
                string folder = Path.GetDirectoryName(
                    IndividualSaveTagPath("__probe__", durable));
                if (!Directory.Exists(folder)) continue;
                foreach (string path in Directory.GetFiles(folder, "*.json"))
                {
                    try
                    {
                        ApSaveTag tag = JsonUtility.FromJson<ApSaveTag>(
                            File.ReadAllText(path).TrimStart('\\uFEFF', ' ', '\\r', '\\n', '\\t'));
                        if (tag == null || String.IsNullOrEmpty(tag.folder) ||
                            String.IsNullOrEmpty(tag.seed) ||
                            String.IsNullOrEmpty(tag.mission) ||
                            String.IsNullOrEmpty(tag.token)) continue;
                        string name = NormalizeSaveFolder(tag.folder);
                        if (!String.Equals(Path.GetFileName(path),
                            Path.GetFileName(IndividualSaveTagPath(name, durable)),
                            StringComparison.OrdinalIgnoreCase)) continue;
                        ApSaveTag existing;
                        if (apSaveTags.TryGetValue(name, out existing))
                        {
                            if (!String.Equals(existing.seed, tag.seed, StringComparison.Ordinal) ||
                                !String.Equals(existing.slot, tag.slot, StringComparison.Ordinal) ||
                                !String.Equals(existing.mission, tag.mission, StringComparison.Ordinal))
                            {
                                conflictingSaveTags.Add(name);
                                Logger.LogWarning("[AP SAVE SIDECAR] Conflicting identities for " +
                                    name + "; blocked until manually resolved.");
                            }
                            continue;
                        }
                        apSaveTags[name] = tag;
                        restored++;
                    }
                    catch (Exception error)
                    {
                        Logger.LogWarning("[AP SAVE SIDECAR] Invalid tag " +
                            Path.GetFileName(path) + ": " + error.GetType().Name);
                    }
                }
            }
            Logger.LogInfo("[AP SAVE SIDECAR] recovered=" + restored +
                " conflicts=" + conflictingSaveTags.Count);
            return restored;
        }

        private void MergeSaveTagsFrom(string path, bool primary)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                var registry = JsonUtility.FromJson<ApSaveRegistry>(
                    File.ReadAllText(path).TrimStart('\uFEFF', ' ', '\r', '\n', '\t'));
                // An empty registry may serialize saves as null (Unity's
                // JsonUtility) or [] (PowerShell's ConvertTo-Json). Neither
                // means corrupt data, and neither may lock future writes.
                if (registry == null || registry.schema != 1)
                    throw new InvalidDataException("Invalid schema");
                if (registry.saves == null)
                {
                    if (primary) saveRegistryWritable = true;
                    Logger.LogInfo("[AP SAVE] Empty valid index " +
                        Path.GetFileName(path));
                    return;
                }
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
                if (primary) saveRegistryWritable = true;
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

        private int RecoverApSaveTagsFromBackups()
        {
            // The PowerShell association script and installer retain backups.
            // An empty but valid live index must not permanently orphan a
            // previously associated save. NEVER deduce ownership from names.
            var paths = new List<string>();
            try
            {
                if (Directory.Exists(directory))
                {
                    paths.AddRange(Directory.GetFiles(directory, "save_tags.json.bak*"));
                    foreach (string folder in Directory.GetDirectories(directory,
                        "upgrade-backup-*"))
                    {
                        string backup = Path.Combine(folder, "save_tags.json");
                        if (File.Exists(backup)) paths.Add(backup);
                    }
                }
                if (!String.IsNullOrEmpty(apSaveRoot) && Directory.Exists(apSaveRoot))
                    paths.AddRange(Directory.GetFiles(apSaveRoot,
                        ".archipelago-save-tags.json.bak*"));
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP SAVE RECOVERY] Cannot enumerate backups: " +
                    error.GetType().Name);
            }
            var found = new Dictionary<string, ApSaveTag>(StringComparer.OrdinalIgnoreCase);
            var conflicting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int scanned = 0;
            foreach (string backup in paths.OrderByDescending(GetTagTimestamp).Take(80))
            {
                scanned++;
                try
                {
                    var data = JsonUtility.FromJson<ApSaveRegistry>(
                        File.ReadAllText(backup).TrimStart('\uFEFF', ' ', '\r', '\n', '\t'));
                    if (data == null || data.schema != 1 || data.saves == null)
                        continue;
                    foreach (ApSaveTag tag in data.saves)
                    {
                        if (tag == null || String.IsNullOrEmpty(tag.folder) ||
                            String.IsNullOrEmpty(tag.token) ||
                            String.IsNullOrEmpty(tag.mission)) continue;
                        string folder = NormalizeSaveFolder(tag.folder);
                        if (String.IsNullOrEmpty(folder) ||
                            apSaveTags.ContainsKey(folder) || conflicting.Contains(folder))
                            continue;
                        ApSaveTag previous;
                        if (found.TryGetValue(folder, out previous) &&
                            (!String.Equals(previous.token, tag.token, StringComparison.Ordinal) ||
                             !String.Equals(previous.mission, tag.mission, StringComparison.Ordinal) ||
                             !String.Equals(previous.mode, tag.mode, StringComparison.Ordinal) ||
                             !String.Equals(previous.map_id, tag.map_id, StringComparison.Ordinal)))
                        {
                            found.Remove(folder);
                            conflicting.Add(folder);
                            continue;
                        }
                        found[folder] = tag;
                    }
                }
                catch (Exception error)
                {
                    Logger.LogWarning("[AP SAVE RECOVERY] Invalid backup " +
                        Path.GetFileName(backup) + ": " + error.GetType().Name);
                }
            }
            foreach (var pair in found)
                apSaveTags[pair.Key] = pair.Value;
            Logger.LogInfo("[AP SAVE RECOVERY] backups=" + scanned +
                " recovered=" + found.Count + " conflicts=" + conflicting.Count +
                " (ownership never inferred from save names)");
            return found.Count;
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
            int recoveredTags = RecoverApSaveTagsFromBackups();
            int sidecarTags = RestoreIndividualTags();
            Logger.LogInfo("[AP SAVE] Registry loaded tags=" + apSaveTags.Count +
                " saveRoot=" + (apSaveRoot == null ? "not-found" :
                    Path.GetFileName(apSaveRoot)));
            if (apSaveTags.Count == 0 && apSaveRoot != null)
                Logger.LogWarning("[AP SAVE] No AP tags found. Old saves " +
                    "cannot be assigned a seed without explicit confirmation; " +
                    "the kit includes Associate-CastleStoryAPSave.ps1.");
            if (apSaveRoot != null) SnapshotNativeSaveFolders();
            RebuildSaveVisibilityPolicy("initial registry");
            lastPrimaryTagStamp = GetTagTimestamp(SaveTagsPath);
            lastMirrorTagStamp = GetTagTimestamp(SaveTagsMirrorPath);
            // Reconstruct the primary index if it was removed by reinstalling
            // BepInEx but a durable sidecar or upgrade backup survived.
            if (recoveredTags > 0 || sidecarTags > 0 ||
                (!File.Exists(SaveTagsPath) && apSaveTags.Count > 0))
                PersistApSaveRegistry();
            else if (apSaveTags.Count > 0)
                PersistIndividualTags(); // Upgrade existing live tags to durable records.
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
                    if (knownSaveFolders.Contains(name)) continue;
                    if (apSaveTags.ContainsKey(name))
                    {
                        // An existing known AP save must never be silently
                        // reassigned to a different multiworld.
                        knownSaveFolders.Add(name);
                        Logger.LogInfo("[AP SAVE] Existing tagged folder unchanged: " + name);
                        continue;
                    }
                    MissionFile active = campaign == null ||
                        campaign.missions == null ? null :
                        campaign.missions.FirstOrDefault(item =>
                            item != null && item.id == startedMission);
                    var tag = new ApSaveTag {
                        folder = name, token = token, seed = seed,
                        slot = currentApSlotName, team = currentApTeam,
                        slot_id = currentApSlotNumber, has_slot_identity = true,
                        mode = mode, mission = startedMission,
                        map_id = active == null ? "" : MissionMapId(active),
                        created_utc = DateTime.UtcNow.ToString("o")
                    };
                    apSaveTags[name] = tag;
                    if (PersistApSaveRegistry())
                    {
                        knownSaveFolders.Add(name);
                        Logger.LogInfo("[AP SAVE] Tagged newly created Castle Story save '" +
                            name + "' for seed=" + seed +
                            " team=" + currentApTeam +
                            " slot=" + currentApSlotName);
                    }
                    else
                    {
                        apSaveTags.Remove(name);
                        Logger.LogWarning("[AP SAVE] Tag could not be persisted for " +
                            name + "; retry on next scan.");
                    }
                }
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP SAVE] Save discovery failed: " +
                    error.GetType().Name);
            }
        }

        private static DateTime GetTagTimestamp(string path)
        {
            try { return !String.IsNullOrEmpty(path) && File.Exists(path) ?
                File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch (Exception) { return DateTime.MinValue; }
        }

        private bool PersistApSaveRegistry()
        {
            // Each save gets its own durable metadata on disk. The original
            // shared registry is retained for backwards compatibility.
            bool sidecarsWritten = PersistIndividualTags();
            if (!saveRegistryWritable)
            {
                Logger.LogWarning("[AP SAVE] Primary index invalid; durable " +
                    "individual tags preserved without overwriting it.");
                return sidecarsWritten;
            }
            bool written = false;
            try
            {
                string contents = JsonUtility.ToJson(new ApSaveRegistry {
                    schema = 1, saves = apSaveTags.Values.OrderBy(
                        entry => entry.folder, StringComparer.OrdinalIgnoreCase).ToArray()
                }, true);
                // Do not replace native save files. Mirror the index in
                // Info/Saves, with recovery files before any overwrite.
                foreach (string target in new [] { SaveTagsMirrorPath, SaveTagsPath })
                {
                    if (String.IsNullOrEmpty(target)) continue;
                    string temp = target + ".tmp";
                    string backup = target + ".bak";
                    try
                    {
                        File.WriteAllText(temp, contents);
                        if (File.Exists(target)) File.Copy(target, backup, true);
                        File.Copy(temp, target, true);
                        File.Delete(temp);
                        written = true;
                    }
                    catch (Exception error)
                    {
                        Logger.LogError("[AP SAVE] Index write failed at " +
                            Path.GetFileName(target) + ": " + error.Message);
                    }
                }
                lastPrimaryTagStamp = GetTagTimestamp(SaveTagsPath);
                lastMirrorTagStamp = GetTagTimestamp(SaveTagsMirrorPath);
                RebuildSaveVisibilityPolicy("new save tagged");
            }
            catch (Exception error)
            {
                Logger.LogError("[AP SAVE] Cannot persist save tags: " + error.Message);
            }
            return sidecarsWritten || written;
        }

        private void RefreshSaveRegistryWhenModified(bool force = false)
        {
            DateTime primary = GetTagTimestamp(SaveTagsPath);
            DateTime mirror = GetTagTimestamp(SaveTagsMirrorPath);
            if (!force && lastPrimaryTagStamp == primary &&
                lastMirrorTagStamp == mirror) return;
            lastPrimaryTagStamp = primary;
            lastMirrorTagStamp = mirror;
            // External association utility can change tags while Castle Story
            // is already running. Reload metadata only when file stamps
            // change, never on each navigation. Primary remains authoritative.
            MergeSaveTagsFrom(SaveTagsMirrorPath, false);
            MergeSaveTagsFrom(SaveTagsPath, true);
            RestoreIndividualTags();
            RebuildSaveVisibilityPolicy("save index updated");
            Logger.LogInfo("[AP SAVE] Refreshed registry from disk, tagged=" +
                apSaveTags.Count + ".");
        }

        private void RebuildSaveVisibilityPolicy(string reason)
        {
            if (String.IsNullOrEmpty(apSaveRoot))
                apSaveRoot = FindCastleStorySaveRoot();
            bool active = IsArchipelagoSaveSessionActive();
            if (apSavePolicyPrepared &&
                lastSavePolicyToken == token && lastSavePolicyActive == active &&
                reason == "unchanged") return;
            cachedSaveFolderNames.Clear();
            cachedAllowedSaveNames.Clear();
            if (!String.IsNullOrEmpty(apSaveRoot) &&
                Directory.Exists(apSaveRoot))
                foreach (string path in Directory.GetDirectories(apSaveRoot))
                    cachedSaveFolderNames.Add(NormalizeSaveFolder(path));
            foreach (string name in cachedSaveFolderNames)
                if (IsAllowedNativeSave(name))
                    cachedAllowedSaveNames.Add(name);
            lastSavePolicyToken = token;
            lastSavePolicyActive = active;
            apSavePolicyPrepared = true;
            ReconcileKnownNativeSaveCards();
            Logger.LogInfo("[AP UI PREPARED] " + reason +
                ": AP=" + active + " saves=" + cachedSaveFolderNames.Count +
                " allowed=" + cachedAllowedSaveNames.Count +
                " tagged=" + apSaveTags.Count);
            foreach (var pair in apSaveTags)
            {
                if (pair.Value == null) continue;
                Logger.LogInfo("[AP SAVE POLICY] folder=" + pair.Key +
                    " present=" + cachedSaveFolderNames.Contains(pair.Key) +
                    " seedMatch=" + String.Equals(pair.Value.seed, seed,
                        StringComparison.Ordinal) +
                    " slotMatch=" + String.Equals(pair.Value.slot,
                        currentApSlotName, StringComparison.OrdinalIgnoreCase) +
                    " teamMatch=" + (!pair.Value.has_slot_identity ||
                        pair.Value.team == currentApTeam) +
                    " tokenMatch=" + String.Equals(pair.Value.token, token,
                        StringComparison.Ordinal) +
                    " allowed=" + cachedAllowedSaveNames.Contains(pair.Key));
            }
        }

        private bool IsArchipelagoSaveSessionActive()
        {
            // A cached session.json is NOT an authenticated connection.
            // Once live connection is proven, retain AP isolation through
            // brief bridge heartbeat drops, until explicit logout.
            return authenticatedApSaveSession && !disconnectPending &&
                !String.IsNullOrEmpty(token) && campaign != null &&
                campaign.ready;
        }

        private void OnApAuthenticationChanged(bool becameConnected)
        {
            bool before = IsArchipelagoSaveSessionActive();
            if (disconnectPending) authenticatedApSaveSession = false;
            else if (becameConnected) authenticatedApSaveSession = true;
            RefreshSaveRegistryWhenModified();
            bool after = IsArchipelagoSaveSessionActive();
            if (before != after || lastSavePolicyActive != after ||
                lastSavePolicyToken != token || !apSavePolicyPrepared)
            {
                // All Brix cards remain alive. Clear visual masks from the
                // previous connection and reclassify the existing screen
                // under the new policy in the same frame, before render.
                ClearPreRenderCardPolicy();
                RestoreNativeEntries();
                RebuildSaveVisibilityPolicy("connection state changed");
                ApplyPrecomputedPolicyToVisibleTitles();
            }
        }

        private bool TagBelongsToCurrentRun(ApSaveTag tag)
        {
            if (tag == null || !IsArchipelagoSaveSessionActive() ||
                String.IsNullOrEmpty(tag.seed) || String.IsNullOrEmpty(seed) ||
                !String.Equals(tag.seed, seed, StringComparison.Ordinal))
                return false;
            // Seed alone would expose another player's saves in the SAME
            // multiworld. Always scope to the slot identity as well.
            if (!String.IsNullOrEmpty(tag.slot) &&
                !String.Equals(tag.slot, currentApSlotName,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (tag.has_slot_identity)
                return !String.IsNullOrEmpty(tag.slot) &&
                    tag.team == currentApTeam && tag.slot_id == currentApSlotNumber;
            // v0.7.x tags contain seed + slot name + deterministic token.
            // An old tag without a slot name remains token-only, so it can
            // never be assigned to an unrelated player by guessing.
            return !String.IsNullOrEmpty(tag.slot) ||
                String.Equals(tag.token, token, StringComparison.Ordinal);
        }

        private bool IsAllowedNativeSave(string folder)
        {
            folder = NormalizeSaveFolder(folder);
            if (conflictingSaveTags.Contains(folder)) return false;
            ApSaveTag tag;
            bool tagged = apSaveTags.TryGetValue(folder, out tag);
            if (IsArchipelagoSaveSessionActive())
                return tagged && TagBelongsToCurrentRun(tag);
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
            if (nativeCaptionHookInstalled)
            {
                // The membership snapshot exists before opening Load. Brix
                // captions are filtered at assignment and OnEnable. Do not
                // ever schedule a visible delayed 0.13-second hide.
                return;
            }
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
                var distinctFolders = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (Component text in node.GetComponentsInChildren(textType, true))
                {
                    string value = GetUiProperty(text, "text") as string;
                    string folder = SaveFolderForCaption(value, saveNames);
                    if (folder != null) distinctFolders.Add(folder);
                    if (distinctFolders.Count > 1) break;
                }
                // A card may repeat its own save title (e.g. name and
                // thumbnail caption). Only TWO DISTINCT saves mean we
                // climbed into the whole list and must stop.
                if (distinctFolders.Count > 1) break;
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
            if (matched == 0 && known.Count > 0 && lastSaveMenuDiagnosticCount < 2)
            {
                Logger.LogWarning("[AP SAVE UI] No exact folder labels detected. " +
                    "Sampling displayed load menu labels to map Brix cards.");
                GameObject library = GameObject.Find(
                    "Menu: SpLoadGameLibraryMenu");
                if (library != null)
                {
                    int count = 0;
                    foreach (Component text in library.GetComponentsInChildren(
                        textType, true))
                    {
                        string value = GetUiProperty(text, "text") as string;
                        if (String.IsNullOrEmpty(value)) continue;
                        Logger.LogInfo("[AP SAVE UI PROBE] caption=" +
                            value.Replace("\n", " ").Replace("\r", " ") +
                            " parent=" + (text.transform.parent == null ?
                                "root" : text.transform.parent.name));
                        if (++count == 20) break;
                    }
                }
                else Logger.LogInfo("[AP SAVE UI PROBE] Native load library " +
                    "GameObject not active by standard Brix name.");
            }
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
            if (now >= nextSaveRegistryCheckAt)
            {
                nextSaveRegistryCheckAt = now + 1.25f;
                RefreshSaveRegistryWhenModified();
            }
            if (now >= nextSaveScanAt)
            {
                nextSaveScanAt = now + 1f;
                ScanNewNativeSaves();
            }
            if (!nativeCaptionHookInstalled &&
                pendingLoadFilterPasses > 0 && now >= nextLoadMenuFilterAt)
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
                !TagBelongsToCurrentRun(tag) ||
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
