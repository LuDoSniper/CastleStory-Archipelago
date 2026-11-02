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
    [BepInPlugin("fr.castlestory.archipelago", "Castle Story Archipelago", "0.4.0")]
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
        private int goalWave = 10;
        private int shardGoal = 3;
        private CampaignFile campaign;
        private int lastWave;
        private int lastObservedWave = -1;
        private int waveEventCount;
        private int conquestCaptured;
        private int conquestWinEvents;
        private bool conquestVictory;
        private float nextRead;
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
            File.WriteAllText(Path.Combine(directory, "diagnostic.txt"),
                "Castle Story Archipelago v0.4.0 Awake() OK." + Environment.NewLine);
            Logger.LogInfo("Castle Story Archipelago v0.4.0 initialised.");
            waveChecks = Config.Bind("Invasion", "EnableWaveChecks", false,
                "Send completed Invasion waves 1-10 to a connected AP Invasion slot.");
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

        private void RecordWave(int wave)
        {
            lastObservedWave = wave;
            waveEventCount++;
            Logger.LogInfo("[AP WAVE EVENT] value=" + wave +
                " at t=" + Time.realtimeSinceStartup.ToString("F1") +
                "s event #" + waveEventCount);
            if ((mode != "Invasion" && mode != "Hybrid") || wave < 1 || wave > goalWave) return;
            if (campaign != null && campaign.ready &&
                !IsSelectedMissionType("Invasion"))
            {
                Logger.LogWarning("[AP MISSION] Invasion wave ignored; select an Invasion mission.");
                return;
            }
            if (!waveChecks.Value || !bridgeConnected) return;
            if (wave > 1 && !checkedIds.Contains(WaveBase + wave - 1))
            {
                Logger.LogWarning("[AP CHECK] Wave " + wave +
                    " ignored; previous wave not checked in this AP session.");
                return;
            }
            if (checkedIds.Add(WaveBase + wave))
            {
                lastWave = wave;
                WriteChecks();
                Logger.LogInfo("[AP CHECK] Completed Invasion wave " + wave);
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
            if (campaign != null && campaign.ready)
            {
                if (!IsSelectedMissionType("Conquest"))
                {
                    Logger.LogWarning("[AP MISSION] Capture ignored: select a Conquest mission.");
                    return;
                }
                string key = CaptureKey(captured);
                if (!capturedKeys.Add(key))
                {
                    Logger.LogInfo("[AP MISSION] Duplicate capture skipped: " + key);
                    return;
                }
            }
            conquestCaptured++;
            if ((mode != "Conquest" && mode != "Hybrid") || !captureChecks.Value || !bridgeConnected)
            {
                Logger.LogInfo("[AP CONQUEST] Local capture counted for diagnostics only; " +
                    "selected AP mode=" + (mode == "" ? "<none>" : mode) +
                    ", check sending=" + (captureChecks.Value ? "enabled" : "disabled") +
                    ", bridge connected=" + bridgeConnected + ".");
                return;
            }
            if (conquestCaptured <= shardGoal && checkedIds.Add(ConquestBase + conquestCaptured))
            {
                WriteChecks();
                Logger.LogInfo("[AP CHECK] Conquest candidate capture #" + conquestCaptured);
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

        private void ReadBridge()
        {
            try
            {
                var sessionPath = Path.Combine(directory, "session.json");
                if (!File.Exists(sessionPath)) return;
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
                    checkedIds.Clear();
                    capturedObjects.Clear();
                    capturedKeys.Clear();
                    conqueredMissions.Clear();
                    chosenMission = "";
                    inventory.Clear();
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
                bridgeConnected = session.connected;
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
                    Logger.LogInfo("[AP INVENTORY] Synced " + items.items.Length +
                        " received items. Display only; game effects not yet implemented.");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[AP BRIDGE] Invalid files: " + e.Message);
            }
        }

        private void WriteChecks()
        {
            if (!bridgeConnected || String.IsNullOrEmpty(token)) return;
            var path = Path.Combine(directory, "checks.json");
            var temp = path + ".tmp";
            bool allShards = checkedIds.Contains(ConquestBase + shardGoal);
            bool invasionFinished = lastWave >= goalWave;
            bool conquestFinished = conquestVictory && allShards;
            bool won = mode == "Invasion" ? invasionFinished :
                mode == "Conquest" ? conquestFinished :
                (invasionFinished && conquestFinished);
            File.WriteAllText(temp, JsonUtility.ToJson(new CheckFile {
                token = token, mode = mode,
                location_ids = checkedIds.OrderBy(x => x).ToArray(),
                captured_keys = capturedKeys.OrderBy(x => x).ToArray(),
                completed_missions = conqueredMissions.OrderBy(x => x).ToArray(),
                goal = won
            }));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup >= nextRead)
            {
                nextRead = Time.realtimeSinceStartup + 0.7f;
                ReadBridge();
            }
            if (Input.GetKeyDown(KeyCode.F9))
                ProbeGameApis();
            if (Input.GetKeyDown(KeyCode.F8))
                Logger.LogInfo("[AP F8] t=" + Time.realtimeSinceStartup.ToString("F1") +
                    "s mode=" + mode + " connected=" + bridgeConnected +
                    " wave=" + lastObservedWave + " captures=" + conquestCaptured +
                    " victories=" + conquestWinEvents + " checks=" + checkedIds.Count);
        }

        private void OnGUI()
        {
            DrawApMenu();
            if (showOverlay == null || !showOverlay.Value) return;
            int oldDepth = GUI.depth;
            GUI.depth = -10000;
            GUI.Box(new Rect(8, 8, 680, 180), "");
            GUI.Label(new Rect(18, 16, 650, 22), "Castle Story AP v0.4.0 [EXPERIMENTAL]");
            GUI.Label(new Rect(18, 40, 650, 22),
                "Session: " + (token == "" ? "No session" :
                    (bridgeConnected ? "CONNECTED" : "OFFLINE")) +
                " | Mode: " + (mode == "" ? "not configured" : mode) +
                " | Checks: " + checkedIds.Count);
            GUI.Label(new Rect(18, 64, 650, 22),
                "Invasion wave=" + lastObservedWave +
                " | Conquest candidate captures=" + conquestCaptured +
                " | Win events=" + conquestWinEvents);
            GUI.Label(new Rect(18, 88, 650, 22),
                "Invasion sending=" + (waveChecks.Value ? "ON" : "OFF") +
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
            GUI.Label(new Rect(18, 115, 650, 45),
                "AP received inventory (display only): " + (itemText == "" ? "none" : itemText));
            GUI.Label(new Rect(18, 155, 650, 20),
                "F8 = write status to BepInEx log; do not test with an existing save.");
            GUI.depth = oldDepth;
        }

        private void OnDestroy()
        {
            if (harmony != null) harmony.UnpatchSelf();
            if (Instance == this) Instance = null;
        }
    }
}
