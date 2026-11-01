using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryArchipelago
{
    [BepInPlugin("fr.castlestory.archipelago", "Castle Story Archipelago", "0.2.0")]
    public sealed class CastleStoryArchipelagoPlugin : BaseUnityPlugin
    {
        private const int WaveBase = 276810000;
        private string directory;
        private string token = "";
        private string seed = "";
        private int goalWave = 10;
        private readonly HashSet<int> checkedIds = new HashSet<int>();
        private readonly HashSet<int> itemIndices = new HashSet<int>();
        private ConfigEntry<bool> automaticWaveChecks;
        private ConfigEntry<bool> markWaveOnStatEvent;
        private ConfigEntry<bool> showOverlay;
        private float nextRead;
        private int lastWave;
        private Harmony harmony;
        internal static CastleStoryArchipelagoPlugin Instance;

        [Serializable] private sealed class SessionFile
        {
            public string token;
            public string seed;
            public int goal_wave;
        }
        [Serializable] private sealed class CheckFile
        {
            public string token;
            public int[] location_ids;
            public bool goal;
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
            automaticWaveChecks = Config.Bind("Invasion", "EnableWaveChecks", false,
                "EXPERIMENTAL: enable wave checks after verifying that the StatsTracker hook corresponds to completed rounds.");
            markWaveOnStatEvent = Config.Bind("Invasion", "StatEventMeansWaveComplete", false,
                "Must be enabled along with EnableWaveChecks to send real AP checks. Start with logs only.");
            showOverlay = Config.Bind("Interface", "ShowOverlay", true, "Display experimental integration status.");
            try
            {
                var type = AccessTools.TypeByName("Brix.Game.StatsTracker");
                var method = type == null ? null : AccessTools.Method(type, "InvasionWave", new[] { typeof(int) });
                if (method == null)
                {
                    Logger.LogError("InvasionWave(int) not found. Please provide BepInEx/LogOutput.log and your game version.");
                    return;
                }
                harmony = new Harmony("fr.castlestory.archipelago.invasion");
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(CastleStoryArchipelagoPlugin), "OnInvasionWave"));
                Logger.LogInfo("Attached Harmony postfix to Brix.Game.StatsTracker.InvasionWave(int).");
            }
            catch (Exception error) { Logger.LogError("Harmony hook failed: " + error); }
        }

        private static void OnInvasionWave(int __0)
        {
            if (Instance != null) Instance.HandleWave(__0);
        }

        private void HandleWave(int wave)
        {
            Logger.LogInfo("Castle Story stats InvasionWave(" + wave + ") fired. Does this indicate round START or END?");
            if (!automaticWaveChecks.Value || !markWaveOnStatEvent.Value) return;
            if (wave < 1 || wave > goalWave || String.IsNullOrEmpty(token)) return;
            if (wave > 1 && !checkedIds.Contains(WaveBase + wave - 1))
            {
                Logger.LogWarning("Ignoring nonsequential wave " + wave + "; previous wave has no check.");
                return;
            }
            if (checkedIds.Add(WaveBase + wave))
            {
                lastWave = wave;
                WriteChecks();
                Logger.LogInfo("Checked invasion wave " + wave);
            }
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup >= nextRead)
            {
                nextRead = Time.realtimeSinceStartup + 0.8f;
                ReadBridge();
            }
            if (Input.GetKeyDown(KeyCode.F8))
                Logger.LogInfo("Castle Story AP session=" + seed + ", checked=" + checkedIds.Count +
                    ", received=" + itemIndices.Count + ". Wave hook requires in-game validation.");
        }

        private void ReadBridge()
        {
            try
            {
                var sessionPath = Path.Combine(directory, "session.json");
                if (!File.Exists(sessionPath)) return;
                var session = JsonUtility.FromJson<SessionFile>(File.ReadAllText(sessionPath));
                if (session == null || String.IsNullOrEmpty(session.token)) return;
                if (session.token != token)
                {
                    token = session.token;
                    seed = session.seed ?? "";
                    goalWave = Math.Max(1, Math.Min(10, session.goal_wave));
                    checkedIds.Clear();
                    itemIndices.Clear();
                    lastWave = 0;
                    var checksPath = Path.Combine(directory, "checks.json");
                    if (File.Exists(checksPath))
                    {
                        var previous = JsonUtility.FromJson<CheckFile>(File.ReadAllText(checksPath));
                        if (previous != null && previous.token == token && previous.location_ids != null)
                        {
                            foreach (int id in previous.location_ids)
                                if (id >= WaveBase + 1 && id <= WaveBase + goalWave)
                                    checkedIds.Add(id);
                        }
                    }
                    lastWave = checkedIds.Count == 0 ? 0 : checkedIds.Max() - WaveBase;
                    Logger.LogInfo("AP session activated for seed " + seed);
                }
                var itemsPath = Path.Combine(directory, "items.json");
                if (!File.Exists(itemsPath)) return;
                var inbox = JsonUtility.FromJson<ItemsFile>(File.ReadAllText(itemsPath));
                if (inbox == null || inbox.token != token || inbox.items == null) return;
                foreach (var item in inbox.items)
                    if (itemIndices.Add(item.index))
                        Logger.LogInfo("Received AP item " + item.item + " (index=" + item.index +
                            "). Item effects are NOT implemented yet.");
            }
            catch (Exception error) { Logger.LogWarning("Failed reading bridge files: " + error.Message); }
        }

        private void WriteChecks()
        {
            var dest = Path.Combine(directory, "checks.json");
            var tmp = dest + ".tmp";
            var json = JsonUtility.ToJson(new CheckFile {
                token = token,
                location_ids = checkedIds.OrderBy(x => x).ToArray(),
                goal = lastWave >= goalWave
            });
            File.WriteAllText(tmp, json);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
        }

        private void OnGUI()
        {
            if (!showOverlay.Value) return;
            GUI.Label(new Rect(12, 12, 650, 58),
                "Castle Story AP [EXPERIMENTAL] | " + (token.Length == 0 ? "No connection" : "Connected") +
                "\nSeed: " + seed + " | Checks: " + checkedIds.Count + "/" + goalWave +
                " | Hook checks: " + (automaticWaveChecks.Value && markWaveOnStatEvent.Value ? "ON" : "LOG ONLY"));
        }

        private void OnDestroy()
        {
            if (harmony != null) harmony.UnpatchSelf();
            if (Instance == this) Instance = null;
        }
    }
}
