using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryArchipelago
{
    [BepInPlugin("fr.castlestory.archipelago", "Castle Story Archipelago", "0.7.3")]
    public sealed partial class CastleStoryArchipelagoPlugin : BaseUnityPlugin
    {
        private const int WaveBase = 276810000;
        private const int ConquestBase = 276812000;
        private const int ItemBase = 276811000;
        private string directory;
        private string token = "";
        private string seed = "";
        private string mode = "";
        private bool bridgeConnected;
        // This is set only after the native Castle Story start guard validates
        // the exact Archipelago map and game mode.
        private string startedMission = "";
        private int goalWave = 10;
        private int shardGoal = 3;
        private CampaignFile campaign;
        private int lastWave;
        private int lastObservedWave = -1;
        private int waveEventCount;
        private int lastLoggedWave = -1;
        private int lastRejectedWave = -1;
        private bool checksDirty;
        private int conquestCaptured;
        private int conquestWinEvents;
        private bool conquestVictory;
        private float nextRead;
        private bool debugOverlayVisible;
        private Harmony harmony;
        private ConfigEntry<bool> waveChecks;
        private ConfigEntry<bool> captureChecks;
        private ConfigEntry<bool> victoryChecks;
        private ConfigEntry<bool> showOverlay;
        private readonly HashSet<int> checkedIds = new HashSet<int>();
        private readonly HashSet<int> capturedObjects = new HashSet<int>();
        private readonly HashSet<string> capturedKeys = new HashSet<string>();
        private readonly HashSet<string> conqueredMissions = new HashSet<string>();
        private readonly Dictionary<int, int> inventory = new Dictionary<int, int>();
        internal static CastleStoryArchipelagoPlugin Instance;

        [Serializable] private sealed class SessionFile
        {
            public string token;
            public string seed;
            public string mode;
            public bool connected;
            public int goal_wave;
            public int shard_goal;
            public CampaignFile campaign;
            public int campaign_mission_count;
            public int protocol_version;
            public string bridge_client_version;
            public double updated_at;
            public bool slot_data_provided;
            public bool manifest_provided;
            public string[] slot_data_fields;
            public string campaign_status;
            public string campaign_message;
        }
        // Flat campaign file is intentionally separate from session.json:
        // Unity 5.6's legacy serializer may not parse deeply nested arrays.
        [Serializable] private sealed class CampaignViewFile
        {
            public string token;
            public int protocol_version;
            public string bridge_client_version;
            public string campaign_status;
            public string campaign_message;
            public bool manifest_provided;
            public bool ready;
            public string mode;
            public string warning;
            public int mission_count;
            public string[] missing_modes;
            public MissionFile[] missions;
        }

        [Serializable] private sealed class CheckFile
        {
            public string token;
            public string mode;
            public int[] location_ids;
            public bool goal;
            public string[] captured_keys;
            public string[] completed_missions;
        }
        [Serializable] private sealed class ItemEntry
        {
            public int index;
            public int item;
            public int from_player;
        }
        [Serializable] private sealed class ItemsFile
        {
            public string token;
            public ItemEntry[] items;
        }

        private void Awake()
        {
            Instance = this;
            directory = Path.Combine(Paths.ConfigPath, "castle_story_archipelago");
            Directory.CreateDirectory(directory);
            LoadRememberedConnection();
            File.WriteAllText(Path.Combine(directory, "diagnostic.txt"),
                "Castle Story Archipelago v0.7.3 Awake() OK." + Environment.NewLine);
            Logger.LogInfo("Castle Story Archipelago v0.7.3 initialised.");
            waveChecks = Config.Bind("Invasion", "EnableWaveChecks", false,
                "Legacy-only opt-in for old slots without a campaign. A launched Archipelago seed enables Invasion checks automatically.");
            captureChecks = Config.Bind("Conquest", "EnableCaptureChecks", false,
                "EXPERIMENTAL: send unique locally owned crystal capture sound events as shard checks. One neutral shard validated; other crystal types not yet proven.");
            victoryChecks = Config.Bind("Conquest", "EnableVictoryCheck", false,
                "Send Conquest GameWon() event as an AP victory when a Conquest slot is connected.");
            showOverlay = Config.Bind("Interface", "ShowOverlay", false,
                "Display connection, wave/capture statistics and AP item inventory.");
            harmony = new Harmony("fr.castlestory.archipelago.gameplay");
            Patch("Brix.Game.StatsTracker", "InvasionWave", "OnInvasionWave",
                new [] { typeof(int) });
            Patch("Brix.Game.StatsTracker", "GameWon", "OnGameWon", Type.EmptyTypes);
            Patch("CrystalVisuals", "PlayCapturedSound", "OnCapturedSound", null);
            InstallNativeMenuGuard();
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged +=
                OnArchipelagoSceneChanged;
            InstallBonusSpawnHooks();
            Logger.LogInfo("[AP NATIVE UI] Native main menu integration enabled; F7 fallback.");
            Logger.LogInfo("All available gameplay hook installation attempts finished.");
        }

        private void Patch(string gameType, string gameMethod, string ownMethod, Type[] args)
        {
            try
            {
                var type = AccessTools.TypeByName(gameType);
                var method = type == null ? null : AccessTools.Method(type, gameMethod, args);
                if (method == null)
                {
                    Logger.LogWarning("[AP HOOK] Missing " + gameType + "." + gameMethod +
                        "; upload your game log before enabling checks for this mode.");
                    return;
                }
                var postfix = AccessTools.Method(typeof(CastleStoryArchipelagoPlugin), ownMethod);
                harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                Logger.LogInfo("[AP HOOK] Attached to " + gameType + "." + gameMethod);
            }
            catch (Exception e)
            {
                Logger.LogError("[AP HOOK] Could not patch " + gameType + "." +
                    gameMethod + ": " + e);
            }
        }

        private static void OnInvasionWave(int __0)
        {
            if (Instance != null) Instance.RecordWave(__0);
        }

        private static void OnGameWon()
        {
            if (Instance != null) Instance.RecordVictory();
        }

        private static void OnCapturedSound(object __instance, object __0)
        {
            if (Instance != null) Instance.RecordCapture(__instance, __0);
        }

        private bool IsActiveInvasionMission()
        {
            return !String.IsNullOrEmpty(token) && !disconnectPending &&
                campaign != null && campaign.ready &&
                !String.IsNullOrEmpty(startedMission) &&
                String.Equals(startedMission, chosenMission, StringComparison.Ordinal) &&
                IsSelectedMissionType("Invasion");
        }

        private void RecordWave(int wave)
        {
            lastObservedWave = wave;
            waveEventCount++;
            // StatsTracker.InvasionWave fires repeatedly for the same value;
            // only transitions are useful for check delivery.
            if (waveEventCount == 1 || wave != lastLoggedWave)
            {
                Logger.LogInfo("[AP WAVE EVENT] value=" + wave +
                    " at t=" + Time.realtimeSinceStartup.ToString("F1") +
                    "s event #" + waveEventCount);
                lastLoggedWave = wave;
            }
            if ((mode != "Invasion" && mode != "Hybrid") ||
                wave < 1 || wave > goalWave) return;
            bool activeSeedMission = IsActiveInvasionMission();
            if (campaign != null && campaign.ready && !activeSeedMission)
            {
                if (lastRejectedWave != wave)
                    Logger.LogWarning("[AP CHECK] Wave " + wave +
                        " ignored: no active seed-launched Invasion mission.");
                lastRejectedWave = wave;
                return;
            }
            // A real campaign enables checks automatically, even if an older
            // installation left EnableWaveChecks=false in its config.
            // Keep that opt-in for legacy manifests only.
            if (!activeSeedMission && (!waveChecks.Value || !bridgeConnected))
                return;
            // The active seed remains trusted during short websocket outages.
            // Write to its token-scoped checks.json, then replay on reconnect.
            if (wave > 1 && !checkedIds.Contains(WaveBase + wave - 1))
            {
                Logger.LogWarning("[AP CHECK] Wave " + wave +
                    " ignored; prior wave has not been observed in this seed.");
                return;
            }
            if (checkedIds.Add(WaveBase + wave))
            {
                lastWave = wave;
                WriteChecks();
                Logger.LogInfo("[AP CHECK] Invasion wave " + wave +
                    " queued for Archipelago; network=" +
                    (bridgeConnected ? "online" : "temporarily offline") +
                    "; id=" + (WaveBase + wave));
            }
        }

        private void RecordCapture(object captured, object faction)
        {
            // A faction argument is present in the actual game's DLL signature.
            // Only the local player's faction may generate a candidate check.
            // Sound may also be played on non-shard crystals; opt-in remains required.
            // Diagnostics are always enabled, even without an AP server/slot.
            // Only actual checks are gated on the Conquest slot below.
            bool ownedByPlayer = false;
            try
            {
                if (faction != null)
                {
                    var ownedProperty = AccessTools.Property(faction.GetType(), "IsOwnedByMe");
                    if (ownedProperty != null)
                        ownedByPlayer = Convert.ToBoolean(ownedProperty.GetValue(faction, null));
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[AP CONQUEST CAPTURE] Could not check faction ownership: " + e.Message);
            }
            Logger.LogInfo("[AP CONQUEST CAPTURE] faction=" +
                (faction == null ? "<null>" : faction.GetType().FullName) +
                " ownedByPlayer=" + ownedByPlayer);
            if (!ownedByPlayer) return;
            int objectId = captured == null ? 0 :
                (captured is UnityEngine.Object ? ((UnityEngine.Object)captured).GetInstanceID()
                 : captured.GetHashCode());
            if (objectId == 0) return;
            bool isNew = capturedObjects.Add(objectId);
            if (campaign != null && campaign.ready && IsSelectedMissionType("Conquest"))
                isNew = !capturedKeys.Contains(CaptureKey(captured));
            Logger.LogInfo("[AP CONQUEST CAPTURE] candidate id=" + objectId +
                " new=" + isNew + " t=" + Time.realtimeSinceStartup.ToString("F1") + "s");
            if (!isNew) return;
            MissionFile selected = null;
            if (campaign != null && campaign.ready && campaign.missions != null)
            {
                foreach (MissionFile m in campaign.missions)
                    if (m != null && m.id == chosenMission && m.mode == "Conquest")
                        selected = m;
                if (selected == null)
                {
                    Logger.LogWarning("[AP MISSION] Capture ignored: no Conquest mission selected.");
                    return;
                }
                string key = CaptureKey(captured);
                int completedOnThisMap = capturedKeys.Count(x =>
                    x.StartsWith(chosenMission + "|", StringComparison.Ordinal));
                if (completedOnThisMap >= selected.shard_count)
                {
                    Logger.LogInfo("[AP MISSION] All configured shards already checked on " +
                        selected.id + "; ignoring extra capture.");
                    return;
                }
                if (!capturedKeys.Add(key))
                {
                    Logger.LogInfo("[AP MISSION] Duplicate capture skipped: " + key);
                    return;
                }
                int ordinal = selected.shard_start + completedOnThisMap;
                conquestCaptured = capturedKeys.Count;
                if ((mode != "Conquest" && mode != "Hybrid") ||
                    !captureChecks.Value || !bridgeConnected) return;
                int checkId = ConquestBase + ordinal;
                if (ordinal <= shardGoal && checkedIds.Add(checkId))
                {
                    WriteChecks();
                    Logger.LogInfo("[AP CHECK] Conquest mission=" + selected.id +
                        " local shard=" + (completedOnThisMap + 1) +
                        " global shard=" + ordinal + " check=" + checkId);
                }
                return;
            }
            // Old slots without a campaign manifest keep the legacy 1-3 checks.
            conquestCaptured++;
            if ((mode != "Conquest" && mode != "Hybrid") ||
                !captureChecks.Value || !bridgeConnected) return;
            if (conquestCaptured <= shardGoal &&
                checkedIds.Add(ConquestBase + conquestCaptured))
            {
                WriteChecks();
                Logger.LogInfo("[AP CHECK] Legacy Conquest capture #" + conquestCaptured);
            }
        }

        private void RecordVictory()
        {
            conquestWinEvents++;
            Logger.LogInfo("[AP VICTORY] StatsTracker.GameWon() at t=" +
                Time.realtimeSinceStartup.ToString("F1") + "s, mode=" + mode);
            if ((mode != "Conquest" && mode != "Hybrid") || !victoryChecks.Value || !bridgeConnected) return;
            if (campaign != null && campaign.ready && campaign.missions != null)
            {
                if (!IsSelectedMissionType("Conquest"))
                {
                    Logger.LogWarning("[AP MISSION] Victory ignored: no Conquest mission selected.");
                    return;
                }
                conqueredMissions.Add(chosenMission);
                conquestVictory = campaign.missions.Where(m => m != null &&
                    m.mode == "Conquest").All(m => conqueredMissions.Contains(m.id));
                Logger.LogInfo("[AP MISSION] Won " + chosenMission +
                    ". All Conquest missions complete=" + conquestVictory);
                if (!conquestVictory)
                {
                    WriteChecks();
                    return;
                }
            }
            else conquestVictory = true; // Backward compatibility with v0.3 slots.
            if (checkedIds.Add(ConquestBase + 100))
                Logger.LogInfo("[AP CHECK] Conquest victory detected");
            WriteChecks();
        }

        private bool IsSelectedMissionType(string expected)
        {
            if (campaign == null || campaign.missions == null) return false;
            foreach (MissionFile mission in campaign.missions)
                if (mission != null && mission.id == chosenMission &&
                    mission.mode == expected) return true;
            return false;
        }

        private string CaptureKey(object crystal)
        {
            // Map-specific + position-based key where available. Unlike Unity's
            // GetInstanceID(), transform positions tend to survive save reloads.
            string id = crystal == null ? "unknown" : crystal.GetHashCode().ToString();
            var component = crystal as Component;
            if (component != null)
            {
                Vector3 p = component.transform.position;
                id = Math.Round(p.x, 1).ToString("F1",
                    System.Globalization.CultureInfo.InvariantCulture) + "," +
                    Math.Round(p.y, 1).ToString("F1",
                    System.Globalization.CultureInfo.InvariantCulture) + "," +
                    Math.Round(p.z, 1).ToString("F1",
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            return chosenMission + "|" + id;
        }

        private static bool ValidCheck(string selectedMode, int id, int waveLimit, int shardLimit)
        {
            bool invasion = (selectedMode == "Invasion" || selectedMode == "Hybrid") &&
                id > WaveBase && id <= WaveBase + waveLimit;
            bool conquest = (selectedMode == "Conquest" || selectedMode == "Hybrid") &&
                ((id > ConquestBase && id <= ConquestBase + shardLimit) ||
                 id == ConquestBase + 100);
            return invasion || conquest;
        }

        private int bridgeManifestMissionCount = -1;
        private string campaignDecodeDiagnostic = "";
        private string lastCampaignDecodeLog = "";

        private void ReadCampaignView(SessionFile session)
        {
            int embeddedCount = session.campaign != null &&
                session.campaign.missions != null ?
                session.campaign.missions.Length : -1;
            bridgeManifestMissionCount = session.campaign_mission_count;
            string path = Path.Combine(directory, "campaign_view.json");
            if (!File.Exists(path))
            {
                campaignDecodeDiagnostic = "Fichier campaign_view.json absent : " +
                    "remplace le bridge et reconnecte-toi.";
                return;
            }
            try
            {
                var view = JsonUtility.FromJson<CampaignViewFile>(
                    File.ReadAllText(path));
                if (view == null || view.token != session.token)
                {
                    campaignDecodeDiagnostic =
                        "Le manifeste ne correspond pas à la session courante.";
                    return;
                }
                if (view.missions == null)
                {
                    campaignDecodeDiagnostic =
                        "Unity n'a pas pu lire missions[] dans campaign_view.json.";
                    return;
                }
                bridgeManifestMissionCount = view.mission_count;
                if (view.missions.Length != view.mission_count)
                {
                    campaignDecodeDiagnostic = "Missions : le bridge annonce " +
                        view.mission_count + ", Unity en lit " + view.missions.Length +
                        ". Désérialisation incomplète.";
                    return;
                }
                // Prefer the flat payload even when the nested payload was
                // present; the fields come from the same Archipelago slot.
                campaign = new CampaignFile {
                    ready = view.ready,
                    warning = view.warning ?? "",
                    mode = view.mode ?? session.mode,
                    missing_modes = view.missing_modes,
                    missions = view.missions,
                };
                campaignDecodeDiagnostic = "Campagne reçue : " +
                    view.mission_count + " mission(s), statut " +
                    (view.campaign_status ?? "inconnu") +
                    " (champ imbriqué : " + embeddedCount + ").";
                if (!String.IsNullOrEmpty(view.campaign_status))
                    campaignStatus = view.campaign_status;
                if (!String.IsNullOrEmpty(view.campaign_message))
                    campaignMessage = view.campaign_message;
            }
            catch (Exception error)
            {
                campaignDecodeDiagnostic =
                    "Erreur de lecture du manifeste : " + error.Message;
            }
        }

        private static string DecodeWireText(string encoded)
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }

        private void ReadCampaignWire(SessionFile session)
        {
            string path = Path.Combine(directory, "campaign_wire.txt");
            if (!File.Exists(path))
            {
                campaignDecodeDiagnostic += " | campaign_wire.txt absent (bridge ancien ?).";
                return;
            }
            try
            {
                string[] lines = File.ReadAllText(path).Replace("\r", "").Split('\n');
                if (lines.Length < 11 || lines[0] != "CASTLE_STORY_AP_CAMPAIGN_V1")
                    throw new FormatException("En-tête ou taille de manifeste incorrect.");
                if (lines[1] != session.token)
                    throw new FormatException("Token de campagne différent de la session.");
                int protocol = Int32.Parse(lines[2], System.Globalization.CultureInfo.InvariantCulture);
                if (protocol != session.protocol_version)
                    throw new FormatException("Protocole de campagne différent de la session.");
                int count = Int32.Parse(lines[9], System.Globalization.CultureInfo.InvariantCulture);
                if (count < 0 || count > 256 || lines.Length < count + 11)
                    throw new FormatException("Nombre de missions invalide ou fichier tronqué.");
                var missions = new MissionFile[count];
                for (int i = 0; i < count; i++)
                {
                    string[] columns = lines[10 + i].Split('\t');
                    if (columns.Length != 7)
                        throw new FormatException("Mission " + i + " : colonnes incorrectes.");
                    missions[i] = new MissionFile {
                        id = DecodeWireText(columns[0]),
                        mode = DecodeWireText(columns[1]),
                        map_id = DecodeWireText(columns[2]),
                        map = DecodeWireText(columns[3]),
                        wave_goal = Int32.Parse(columns[4], System.Globalization.CultureInfo.InvariantCulture),
                        shard_start = Int32.Parse(columns[5], System.Globalization.CultureInfo.InvariantCulture),
                        shard_count = Int32.Parse(columns[6], System.Globalization.CultureInfo.InvariantCulture)
                    };
                    if (String.IsNullOrEmpty(missions[i].id) ||
                        (missions[i].mode != "Invasion" && missions[i].mode != "Conquest"))
                        throw new FormatException("Mission " + i + " : ID ou mode invalide.");
                }
                string missing = DecodeWireText(lines[8]);
                campaign = new CampaignFile {
                    ready = lines[5] == "1",
                    mode = DecodeWireText(lines[6]),
                    warning = DecodeWireText(lines[7]),
                    missing_modes = String.IsNullOrEmpty(missing) ?
                        new string[0] : missing.Split(','),
                    missions = missions
                };
                bridgeManifestMissionCount = count;
                campaignStatus = DecodeWireText(lines[3]);
                campaignMessage = DecodeWireText(lines[4]);
                campaignDecodeDiagnostic = "Transfert texte V1 : " + count +
                    " mission(s) décodée(s), statut " + campaignStatus + ".";
            }
            catch (Exception error)
            {
                campaignDecodeDiagnostic += " | Transfert texte invalide : " + error.Message;
            }
        }

        private void ReadBridge()
        {
            try
            {
                var sessionPath = Path.Combine(directory, "session.json");
                if (!File.Exists(sessionPath))
                {
                    bridgeConnected = false;
                    return;
                }
                var session = JsonUtility.FromJson<SessionFile>(File.ReadAllText(sessionPath));
                if (session == null || String.IsNullOrEmpty(session.token)) return;
                if (session.mode != "Invasion" && session.mode != "Conquest" && session.mode != "Hybrid")
                {
                    Logger.LogWarning("[AP SESSION] Unsupported or missing mode.");
                    bridgeConnected = false;
                    return;
                }
                if (token != session.token || mode != session.mode)
                {
                    token = session.token;
                    mode = session.mode;
                    seed = session.seed ?? "";
                    goalWave = Math.Max(1, Math.Min(10, session.goal_wave));
                    shardGoal = Math.Max(1, Math.Min(30, session.shard_goal == 0 ? 3 : session.shard_goal));
                    campaign = session.campaign;
                    Logger.LogInfo("[AP CAMPAIGN] ready=" +
                        (campaign != null && campaign.ready) +
                        " maps=" + (campaign == null || campaign.missions == null ?
                            0 : campaign.missions.Length) +
                        " reason=" + (campaign == null ? "No campaign manifest" :
                            (campaign.warning ?? "")));
                    checkedIds.Clear();
                    capturedObjects.Clear();
                    capturedKeys.Clear();
                    conqueredMissions.Clear();
                    chosenMission = "";
                    startedMission = "";
                    lastRejectedWave = -1;
                    checksDirty = false;
                    inventory.Clear();
                    ResetBonusRewards(token);
                    conquestCaptured = 0;
                    conquestVictory = false;
                    lastWave = 0;
                    var checksPath = Path.Combine(directory, "checks.json");
                    if (File.Exists(checksPath))
                    {
                        var prior = JsonUtility.FromJson<CheckFile>(
                            File.ReadAllText(checksPath));
                        if (prior != null && prior.token == token &&
                            prior.mode == mode)
                        {
                            if (prior.location_ids != null)
                                foreach (int id in prior.location_ids)
                                    if (ValidCheck(mode, id, goalWave, shardGoal))
                                        checkedIds.Add(id);
                            if (prior.captured_keys != null)
                                foreach (string key in prior.captured_keys)
                                    if (!String.IsNullOrEmpty(key)) capturedKeys.Add(key);
                            if (prior.completed_missions != null)
                                foreach (string id in prior.completed_missions)
                                    if (!String.IsNullOrEmpty(id)) conqueredMissions.Add(id);
                        }
                    }
                    if (mode == "Invasion" || mode == "Hybrid")
                    {
                        var waves = checkedIds.Where(id =>
                            id > WaveBase && id <= WaveBase + goalWave);
                        lastWave = waves.Any() ? waves.Max() - WaveBase : 0;
                    }
                    if (mode == "Conquest" || mode == "Hybrid")
                    {
                        conquestCaptured = checkedIds.Count(id =>
                            id > ConquestBase && id <= ConquestBase + shardGoal);
                        conquestVictory = checkedIds.Contains(ConquestBase + 100);
                    }
                    Logger.LogInfo("[AP SESSION] " + mode + " seed=" + seed +
                        " checked=" + checkedIds.Count);
                }
                // The server connection and the seed mission manifest are
                // separate states: an older seed can connect successfully while
                // having no campaign data in its AP slot_data.
                string nextStatus = String.IsNullOrEmpty(session.campaign_status) ?
                    (session.campaign == null ? "missing" :
                        (session.campaign.ready ? "ready" : "unready")) :
                    session.campaign_status;
                string nextMessage = session.campaign_message ?? "";
                if (campaignStatus != nextStatus || campaignMessage != nextMessage ||
                    slotProtocolVersion != session.protocol_version ||
                    bridgeClientVersion != session.bridge_client_version)
                {
                    Logger.LogInfo("[AP CAMPAIGN] seed=" + (session.seed ?? "") +
                        " protocol=" + session.protocol_version +
                        " bridge=" + (session.bridge_client_version ?? "legacy") +
                        " slot_data=" + session.slot_data_provided +
                        " manifest=" + session.manifest_provided +
                        " fields=" + String.Join(",", session.slot_data_fields ?? new string[0]) +
                        " status=" + nextStatus + " reason=" + nextMessage);
                }
                campaignStatus = nextStatus;
                campaignMessage = nextMessage;
                slotProtocolVersion = session.protocol_version;
                bridgeClientVersion = session.bridge_client_version ?? "";
                serverSlotDataProvided = session.slot_data_provided;
                serverManifestProvided = session.manifest_provided;
                serverSlotDataFields = session.slot_data_fields;
                campaign = session.campaign;
                ReadCampaignView(session);
                ReadCampaignWire(session);
                if (campaignDecodeDiagnostic != lastCampaignDecodeLog)
                {
                    Logger.LogInfo("[AP MANIFEST] " + campaignDecodeDiagnostic +
                        " seed=" + (session.seed ?? "") + " token=" + session.token);
                    lastCampaignDecodeLog = campaignDecodeDiagnostic;
                }
                DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0,
                    DateTimeKind.Utc);
                double nowSeconds = (DateTime.UtcNow - epoch).TotalSeconds;
                double heartbeatAge = nowSeconds - session.updated_at;
                bool modernBridge = !String.IsNullOrEmpty(session.bridge_client_version) &&
                    session.updated_at > 0;
                bool freshHeartbeat = modernBridge && heartbeatAge >= -5 &&
                    heartbeatAge < 8;
                bool previouslyConnected = bridgeConnected;
                bridgeConnected = session.connected && freshHeartbeat &&
                    !disconnectPending;
                if (previouslyConnected != bridgeConnected)
                {
                    Logger.LogInfo("[AP SESSION] Connection " +
                        (bridgeConnected ? "restored" : "lost") +
                        "; remoteConnected=" + session.connected +
                        "; heartbeatAge=" + heartbeatAge.ToString("F1",
                            System.Globalization.CultureInfo.InvariantCulture) +
                        "s; campaign=" + campaignStatus +
                        "; protocol=" + slotProtocolVersion);
                }
                if (!freshHeartbeat)
                {
                    if (session.connected)
                    {
                        statusLine = modernBridge ?
                            "Session Archipelago périmée : relance le bridge et reconnecte-toi." :
                            "Bridge Archipelago ancien : installe le client 0.7.3 complet.";
                        campaignStatus = modernBridge ? "stale" : "obsolete_bridge";
                        campaignMessage = statusLine;
                    }
                    else if (!disconnectPending)
                    {
                        statusLine = "Archipelago déconnecté.";
                    }
                }
                var itemsPath = Path.Combine(directory, "items.json");
                if (!File.Exists(itemsPath)) return;
                var items = JsonUtility.FromJson<ItemsFile>(File.ReadAllText(itemsPath));
                if (items == null || items.token != token || items.items == null) return;
                var totals = new Dictionary<int, int>();
                foreach (var item in items.items)
                {
                    if (item == null || item.item < ItemBase + 1 ||
                        item.item > ItemBase + 12) continue;
                    if (!totals.ContainsKey(item.item)) totals[item.item] = 0;
                    totals[item.item]++;
                }
                bool changed = totals.Count != inventory.Count ||
                    totals.Any(pair => !inventory.ContainsKey(pair.Key) ||
                        inventory[pair.Key] != pair.Value);
                if (changed)
                {
                    inventory.Clear();
                    foreach (var pair in totals) inventory[pair.Key] = pair.Value;
                    ReconcileBonusRewards(items.items);
                    Logger.LogInfo("[AP INVENTORY] Synced " + items.items.Length +
                        " received items. Bonus unit deliveries tracked separately.");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[AP BRIDGE] Invalid files: " + e.Message);
            }
        }

        private void WriteChecks()
        {
            // Do not lose a valid seed check when ArchiLAN temporarily
            // disconnects. The bridge will replay the token-scoped ledger.
            if (String.IsNullOrEmpty(token) || disconnectPending ||
                (!bridgeConnected && !IsActiveInvasionMission())) return;
            var path = Path.Combine(directory, "checks.json");
            var temp = path + ".tmp";
            bool allShards = checkedIds.Contains(ConquestBase + shardGoal);
            bool invasionFinished = lastWave >= goalWave;
            bool conquestFinished = conquestVictory && allShards;
            bool won = mode == "Invasion" ? invasionFinished :
                mode == "Conquest" ? conquestFinished :
                (invasionFinished && conquestFinished);
            try
            {
                File.WriteAllText(temp, JsonUtility.ToJson(new CheckFile {
                    token = token, mode = mode,
                    location_ids = checkedIds.OrderBy(x => x).ToArray(),
                    captured_keys = capturedKeys.OrderBy(x => x).ToArray(),
                    completed_missions = conqueredMissions.OrderBy(x => x).ToArray(),
                    goal = won
                }));
                // Atomic replacement on NTFS avoids readers observing the
                // deletion gap of the old File.Delete + File.Move sequence.
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                checksDirty = false;
                Logger.LogInfo("[AP CHECK FILE] persisted=" + checkedIds.Count +
                    " goal=" + won + " online=" + bridgeConnected);
            }
            catch (Exception error)
            {
                checksDirty = true;
                Logger.LogError("[AP CHECK FILE] Could not persist checks: " + error);
            }
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup >= nextRead)
            {
                nextRead = Time.realtimeSinceStartup + 0.7f;
                ReadBridge();
                MaintainBridgeHandshake();
                FlushNativeMapCatalogIfIdle();
                // Retry a temporarily locked ledger without depending on
                // further InvasionWave callbacks.
                if (checksDirty && (bridgeConnected || IsActiveInvasionMission()))
                    WriteChecks();
            }
            if (Input.GetKeyDown(KeyCode.F9))
                ProbeGameApis();
            if (Input.GetKeyDown(KeyCode.F10))
                DumpBonusRewardQueue();
            ProcessPendingNativeBonus();
            HandleModalRawInput();
            UpdateNativeArchipelagoIntegration();
            UpdateNativeInputShield();
            UpdateNativePanelStatus();
            if (Input.GetKeyDown(KeyCode.F7))
            {
                if (nativeConnectionPanel != null &&
                    nativeConnectionPanel.activeSelf)
                    HideNativeConnectionPanel();
                else if (apMenuOpen)
                    apMenuOpen = false;
                else
                    OpenArchipelagoFromNativeMenu();
                Logger.LogInfo("[AP NATIVE UI] F7 connection panel toggled.");
            }
            if (Input.GetKeyDown(KeyCode.F8))
            {
                debugOverlayVisible = !debugOverlayVisible;
                Logger.LogInfo("[AP F8] t=" + Time.realtimeSinceStartup.ToString("F1") +
                    "s mode=" + mode + " connected=" + bridgeConnected +
                    " wave=" + lastObservedWave + " captures=" + conquestCaptured +
                    " victories=" + conquestWinEvents + " checks=" + checkedIds.Count +
                    " activeMission=" + (startedMission == "" ? "none" : startedMission) +
                    " autoChecks=" + IsActiveInvasionMission() +
                    " legacyFlag=" + waveChecks.Value);
            }
            // Unity IMGUI uses top-left coordinates, Input.mousePosition uses bottom-left.
            Vector2 mouse = new Vector2(Input.mousePosition.x,
                Screen.height - Input.mousePosition.y);
            float targetScale = new Rect(15, 12, 67, 68).Contains(mouse) ? 1.14f : 1f;
            logoScale = Mathf.Lerp(logoScale, targetScale,
                1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
        }

        private void OnGUI()
        {
            DrawApMenu();
            if (showOverlay == null || !showOverlay.Value || !debugOverlayVisible ||
                apMenuOpen || (bridgeConnected && nativeNewGameVisible)) return;
            int oldDepth = GUI.depth;
            GUI.depth = -10000;
            float diagnosticX = Mathf.Max(6, Screen.width - 690);
            float diagnosticY = Mathf.Max(6, Screen.height - 190);
            GUI.Box(new Rect(diagnosticX, diagnosticY, 680, 180), "");
            GUI.Label(new Rect(diagnosticX + 10, diagnosticY + 8, 650, 22), "Castle Story AP v0.7.3 [EXPERIMENTAL]");
            GUI.Label(new Rect(diagnosticX + 10, diagnosticY + 32, 650, 22),
                "Session: " + (token == "" ? "No session" :
                    (bridgeConnected ? "CONNECTED" : "OFFLINE")) +
                " | Mode: " + (mode == "" ? "not configured" : mode) +
                " | Checks: " + checkedIds.Count);
            GUI.Label(new Rect(diagnosticX + 10, diagnosticY + 56, 650, 22),
                "Invasion wave=" + lastObservedWave +
                " | Conquest candidate captures=" + conquestCaptured +
                " | Win events=" + conquestWinEvents);
            GUI.Label(new Rect(diagnosticX + 10, diagnosticY + 80, 650, 22),
                "Invasion sending=" + (IsActiveInvasionMission() ?
                    "AUTO (seed)" : waveChecks.Value ? "ON (legacy)" : "OFF") +
                " | Conquest captures=" + (captureChecks.Value ? "ON" : "OFF") +
                " | Victory=" + (victoryChecks.Value ? "ON" : "OFF"));
            string itemText = "";
            string[] names = { "Archer", "Knight", "Catapult", "Reinforcements", "Wood",
                "Stone", "Alchemist", "Worker bonus", "Archer bonus", "Knight bonus",
                "Alchemist bonus", "Wisps" };
            for (int i = 1; i <= 12; i++)
            {
                int amount;
                if (inventory.TryGetValue(ItemBase + i, out amount))
                    itemText += names[i - 1] + " x" + amount + "  ";
            }
            GUI.Label(new Rect(diagnosticX + 10, diagnosticY + 107, 650, 45),
                "AP received inventory (display only): " + (itemText == "" ? "none" : itemText));
            GUI.Label(new Rect(diagnosticX + 10, diagnosticY + 147, 650, 20),
                "F8 = write status to BepInEx log; do not test with an existing save.");
            GUI.depth = oldDepth;
        }

        private void OnDestroy()
        {
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged -=
                OnArchipelagoSceneChanged;
            if (harmony != null) harmony.UnpatchSelf();
            // The standalone bridge is a child process only when launched by this plugin.
            // Do not kill a bridge started by the user from PowerShell.
            if (localBridge != null)
            {
                try { if (!localBridge.HasExited) localBridge.Kill(); }
                catch (Exception error) { Logger.LogWarning("[AP BRIDGE] " + error.Message); }
                localBridge.Dispose();
                localBridge = null;
            }
            DestroyNativeInputShield();
            RestoreNativeEntries();
            RestoreNativeMenuLayout();
            ClearPendingBridgeHandshake();
            DestroyNativeConnectionPanel();
            if (nativeArchipelagoButton != null)
                UnityEngine.Object.Destroy(nativeArchipelagoButton);
            nativeArchipelagoButton = null;
            CleanupApUi();
            if (Instance == this) Instance = null;
        }
    }
}
