using System;
using System.IO;
using System.Diagnostics;
using BepInEx;
using UnityEngine;

namespace CastleStoryArchipelago
{
    public sealed partial class CastleStoryArchipelagoPlugin
    {
        [Serializable] private sealed class MissionFile
        {
            public string id;
            public string mode;
            public string map;
            public int wave_goal;
            public int shard_start;
            public int shard_count;
        }
        [Serializable] private sealed class CampaignFile
        {
            public bool ready;
            public string warning;
            public string mode;
            public MissionFile[] missions;
        }
        [Serializable] private sealed class ConnectionFile
        {
            public bool enabled;
            public string server;
            public string slot;
            public string password;
        }
        [Serializable] private sealed class BridgeStatusFile
        {
            public string state;
            public string message;
        }
        [Serializable] private sealed class MissionSelectionFile
        {
            public string token;
            public string mission_id;
            public string map;
            public string mode;
        }

        private bool apMenuOpen;
        private string loginServer = "";
        private string loginSlot = "";
        private string loginPassword = "";
        private string chosenMission = "";
        private string statusLine = "Ready to connect to Archipelago.";
        private Process localBridge;
        private float nextBridgeStatusRead;

        private void WriteConnection(bool enable)
        {
            try
            {
                if (enable && (String.IsNullOrEmpty(loginServer) || String.IsNullOrEmpty(loginSlot)))
                {
                    statusLine = "Server and slot are required.";
                    return;
                }
                File.WriteAllText(Path.Combine(directory, "connection.json"),
                    JsonUtility.ToJson(new ConnectionFile {
                        enabled = enable, server = enable ? loginServer.Trim() : "",
                        slot = enable ? loginSlot.Trim() : "",
                        password = enable ? loginPassword : ""
                    }));
                statusLine = enable ? "Connection requested." : "Disconnect requested.";
                if (enable) StartLocalBridge();
                if (!enable) loginPassword = "";
            }
            catch (Exception error) { statusLine = "Cannot save connection request: " + error.Message; }
        }

        private void StartLocalBridge()
        {
            // Standalone bridge packaged in the test kit; no Python installation required.
            // A user can still start client/bridge.py manually if this executable is absent.
            try
            {
                if (localBridge != null && !localBridge.HasExited) return;
                string exe = Path.Combine(Paths.PluginPath, "castle_story_ap", "castle-story-ap-bridge.exe");
                if (!File.Exists(exe))
                {
                    statusLine = "Bridge executable not installed; use Start-Bridge.ps1.";
                    return;
                }
                var info = new ProcessStartInfo(exe);
                info.Arguments = "--bridge-dir " + Quote(directory);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WorkingDirectory = Path.GetDirectoryName(exe);
                localBridge = Process.Start(info);
                statusLine = "Local bridge started; connecting...";
            }
            catch (Exception error)
            {
                statusLine = "Cannot launch bridge: " + error.Message;
                Logger.LogWarning("[AP BRIDGE] " + error);
            }
        }

        private static string Quote(string value)
        {
            return "\\\"" + value.Replace("\\\"", "\\\\\\\"") + "\\\"";
        }

        private void ReadLocalBridgeStatus()
        {
            if (Time.realtimeSinceStartup < nextBridgeStatusRead) return;
            nextBridgeStatusRead = Time.realtimeSinceStartup + 1f;
            try
            {
                string path = Path.Combine(directory, "bridge_status.json");
                if (!File.Exists(path)) return;
                var state = JsonUtility.FromJson<BridgeStatusFile>(File.ReadAllText(path));
                if (state != null && !String.IsNullOrEmpty(state.state))
                    statusLine = state.state + ": " + (state.message ?? "");
            }
            catch (Exception error) { Logger.LogWarning("[AP BRIDGE] " + error.Message); }
        }

        private void DrawApMenu()
        {
            // The native new-game menu's OnOk is guarded by NativeMenuHooks.
            // The AP overlay lists only seed-selected missions.
            ReadLocalBridgeStatus();
            int oldDepth = GUI.depth;
            GUI.depth = -20000;
            if (GUI.Button(new Rect(8, 8, 52, 34), "AP"))
                apMenuOpen = !apMenuOpen;
            if (!apMenuOpen)
            {
                GUI.depth = oldDepth;
                return;
            }

            GUI.Box(new Rect(8, 46, 650, 520), "Archipelago campaign - development preview");
            GUI.Label(new Rect(24, 76, 120, 24), "Server:");
            loginServer = GUI.TextField(new Rect(150, 76, 490, 24), loginServer, 200);
            GUI.Label(new Rect(24, 108, 120, 24), "Slot:");
            loginSlot = GUI.TextField(new Rect(150, 108, 490, 24), loginSlot, 120);
            GUI.Label(new Rect(24, 140, 120, 24), "Password:");
            loginPassword = GUI.PasswordField(new Rect(150, 140, 490, 24), loginPassword, '*', 120);
            if (GUI.Button(new Rect(24, 176, 130, 30), "Connect")) WriteConnection(true);
            if (GUI.Button(new Rect(166, 176, 130, 30), "Disconnect")) WriteConnection(false);
            GUI.Label(new Rect(24, 216, 620, 24),
                "Session: " + (bridgeConnected ? "CONNECTED" : "OFFLINE") +
                " | Mode: " + (mode == "" ? "none" : mode));
            GUI.Label(new Rect(24, 242, 620, 30), statusLine);
            GUI.Label(new Rect(24, 278, 620, 22), "Seed-selected maps:");
            if (campaign == null || campaign.missions == null)
            {
                GUI.Label(new Rect(24, 305, 620, 30), "Connect for your seed's mission list.");
            }
            else if (!campaign.ready)
            {
                GUI.Label(new Rect(24, 305, 620, 35), "Map catalog incomplete: " + campaign.warning);
            }
            else
            {
                int max = Math.Min(campaign.missions.Length, 5);
                for (int i = 0; i < max; i++)
                {
                    var m = campaign.missions[i];
                    if (m == null) continue;
                    int y = 304 + i * 32;
                    GUI.Label(new Rect(24, y, 465, 30),
                        m.mode + " | " + m.map +
                        (m.mode == "Invasion" ? " | wave " + m.wave_goal :
                         " | shard capacity " + m.shard_count));
                    if (GUI.Button(new Rect(502, y, 138, 28),
                                   chosenMission == m.id ? "Selected" : "Select"))
                    {
                        chosenMission = m.id;
                        try
                        {
                            File.WriteAllText(Path.Combine(directory, "mission_selection.json"),
                                JsonUtility.ToJson(new MissionSelectionFile {
                                    token = token, mission_id = m.id, map = m.map, mode = m.mode
                                }));
                            Logger.LogInfo("[AP MISSION] Selected " + m.id + " / " + m.map);
                        }
                        catch (Exception error) { Logger.LogWarning("Mission selection: " + error.Message); }
                    }
                }
            }
            GUI.Label(new Rect(24, 486, 620, 70),
                "Choose a mission here, then start that exact map in the native NEW GAME menu. " +
                "Other maps are blocked while a ready AP campaign is active. " +
                "Automatic native map launch is still experimental.");
            GUI.depth = oldDepth;
        }
    }
}
