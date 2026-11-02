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
        private bool nativeNewGameVisible;
        private string loginServer = "";
        private string loginSlot = "";
        private string loginPassword = "";
        private string chosenMission = "";
        private string statusLine = "Connect to your Archipelago world.";
        private Process localBridge;
        private float nextBridgeStatusRead;
        private float logoScale = 1f;
        private Vector2 missionScroll;
        private Texture2D apLogo;
        private bool logoLoadAttempted;
        private bool skinReady;
        private GUIStyle panelStyle, cardStyle, activeCardStyle, inputStyle;
        private GUIStyle primaryButton, secondaryButton, subtleButton;
        private GUIStyle titleStyle, subTitleStyle, bodyStyle, mutedStyle, miniStyle;
        private GUIStyle eyebrowStyle, errorStyle, inputLabelStyle;
        private GUIStyle closeButton, logoButton;
        private Texture2D overlayPixel, accentPixel, greenPixel, borderPixel;

        private static readonly Color TextPrimary = new Color(.94f, .96f, 1f, 1f);
        private static readonly Color TextMuted = new Color(.62f, .68f, .79f, 1f);
        private static readonly Color Accent = new Color(.43f, .77f, 1f, 1f);

        private void LoadApLogo()
        {
            if (logoLoadAttempted) return;
            logoLoadAttempted = true;
            try
            {
                string path = Path.Combine(Path.Combine(Paths.PluginPath,
                    "castle_story_ap"), "archipelago-logo.png");
                if (!File.Exists(path))
                {
                    Logger.LogWarning("[AP UI] Official Archipelago logo missing: " + path);
                    return;
                }
                apLogo = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                if (!apLogo.LoadImage(File.ReadAllBytes(path)))
                {
                    UnityEngine.Object.Destroy(apLogo);
                    apLogo = null;
                    Logger.LogWarning("[AP UI] Could not decode official Archipelago logo.");
                }
            }
            catch (Exception error) { Logger.LogWarning("[AP UI] Logo: " + error.Message); }
        }

        private static Texture2D SolidColor(Color color)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private static GUIStyle RoundedPanel(Color background, Color outline, int radius)
        {
            int size = radius * 2 + 12;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            float centerMin = radius + 4;
            float centerMax = size - radius - 5;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x < centerMin ? centerMin - x :
                        (x > centerMax ? x - centerMax : 0f);
                    float dy = y < centerMin ? centerMin - y :
                        (y > centerMax ? y - centerMax : 0f);
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    Color c = Color.clear;
                    if (distance < radius + .3f)
                    {
                        c = distance > radius - 1.1f ? outline : background;
                        c.a *= Mathf.Clamp01(radius + .3f - distance);
                    }
                    texture.SetPixel(x, y, c);
                }
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.Apply();
            GUIStyle style = new GUIStyle(GUI.skin.box);
            style.normal.background = texture;
            style.hover.background = texture;
            style.active.background = texture;
            style.border = new RectOffset(radius + 5, radius + 5,
                                          radius + 5, radius + 5);
            style.padding = new RectOffset(0, 0, 0, 0);
            style.margin = new RectOffset(0, 0, 0, 0);
            return style;
        }

        private static GUIStyle StyledText(int size, Color color, FontStyle weight)
        {
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = size;
            style.fontStyle = weight;
            style.normal.textColor = color;
            style.hover.textColor = color;
            style.wordWrap = true;
            style.alignment = TextAnchor.MiddleLeft;
            style.padding = new RectOffset(0, 0, 0, 0);
            return style;
        }

        private void PrepareSkin()
        {
            if (skinReady) return;
            skinReady = true;
            overlayPixel = SolidColor(new Color(.035f, .052f, .085f, .91f));
            accentPixel = SolidColor(Accent);
            greenPixel = SolidColor(new Color(.26f, .85f, .63f, 1f));
            borderPixel = SolidColor(new Color(.28f, .39f, .53f, .65f));
            panelStyle = RoundedPanel(new Color(.075f, .103f, .17f, 1f),
                                      new Color(.19f, .28f, .42f, 1f), 18);
            cardStyle = RoundedPanel(new Color(.105f, .147f, .229f, 1f),
                                     new Color(.2f, .27f, .39f, 1f), 13);
            activeCardStyle = RoundedPanel(new Color(.128f, .205f, .299f, 1f),
                                           new Color(.38f, .73f, .95f, 1f), 13);
            inputStyle = new GUIStyle(GUI.skin.textField);
            inputStyle.normal.background = RoundedPanel(
                new Color(.062f, .09f, .145f, 1f),
                new Color(.25f, .35f, .46f, 1f), 9).normal.background;
            inputStyle.focused.background = inputStyle.normal.background;
            inputStyle.hover.background = inputStyle.normal.background;
            inputStyle.border = new RectOffset(15, 15, 15, 15);
            inputStyle.fontSize = 15;
            inputStyle.normal.textColor = TextPrimary;
            inputStyle.focused.textColor = TextPrimary;
            inputStyle.padding = new RectOffset(13, 12, 8, 8);
            primaryButton = new GUIStyle(GUI.skin.button);
            primaryButton.normal.background = RoundedPanel(
                new Color(.29f, .68f, .95f, 1f),
                new Color(.45f, .81f, 1f, 1f), 10).normal.background;
            primaryButton.hover.background = RoundedPanel(
                new Color(.40f, .78f, 1f, 1f), Color.white, 10).normal.background;
            primaryButton.active.background = primaryButton.hover.background;
            primaryButton.border = new RectOffset(15, 15, 15, 15);
            primaryButton.normal.textColor = new Color(.045f, .10f, .15f);
            primaryButton.hover.textColor = new Color(.045f, .10f, .15f);
            primaryButton.fontStyle = FontStyle.Bold;
            primaryButton.fontSize = 15;
            secondaryButton = new GUIStyle(primaryButton);
            secondaryButton.normal.background = cardStyle.normal.background;
            secondaryButton.hover.background = activeCardStyle.normal.background;
            secondaryButton.normal.textColor = TextPrimary;
            secondaryButton.hover.textColor = TextPrimary;
            subtleButton = new GUIStyle(secondaryButton);
            subtleButton.fontSize = 13;
            closeButton = new GUIStyle(secondaryButton);
            closeButton.fontSize = 18;
            logoButton = new GUIStyle(GUI.skin.button);
            logoButton.normal.background = null;
            logoButton.hover.background = null;
            logoButton.active.background = null;
            logoButton.normal.textColor = Color.clear;
            logoButton.hover.textColor = Color.clear;
            titleStyle = StyledText(29, TextPrimary, FontStyle.Bold);
            subTitleStyle = StyledText(20, TextPrimary, FontStyle.Bold);
            eyebrowStyle = StyledText(12, Accent, FontStyle.Bold);
            bodyStyle = StyledText(15, TextPrimary, FontStyle.Normal);
            mutedStyle = StyledText(14, TextMuted, FontStyle.Normal);
            miniStyle = StyledText(12, TextMuted, FontStyle.Normal);
            errorStyle = StyledText(14, new Color(1f, .69f, .53f), FontStyle.Normal);
            inputLabelStyle = StyledText(13, TextMuted, FontStyle.Bold);
        }

        private void WriteConnection(bool enable)
        {
            try
            {
                if (enable && (String.IsNullOrEmpty(loginServer.Trim()) ||
                               String.IsNullOrEmpty(loginSlot.Trim())))
                {
                    statusLine = "Server address and slot are required.";
                    return;
                }
                File.WriteAllText(Path.Combine(directory, "connection.json"),
                    JsonUtility.ToJson(new ConnectionFile {
                        enabled = enable, server = enable ? loginServer.Trim() : "",
                        slot = enable ? loginSlot.Trim() : "",
                        password = enable ? loginPassword : ""
                    }));
                statusLine = enable ? "Connection requested." : "Disconnected.";
                if (enable) StartLocalBridge();
                else
                {
                    loginPassword = "";
                    nativeNewGameVisible = false;
                    apMenuOpen = false;
                    bridgeConnected = false;
                }
            }
            catch (Exception error) { statusLine = "Cannot save connection: " + error.Message; }
        }

        private void StartLocalBridge()
        {
            try
            {
                if (localBridge != null && !localBridge.HasExited) return;
                string exe = Path.Combine(Path.Combine(Paths.PluginPath,
                    "castle_story_ap"), "castle-story-ap-bridge.exe");
                if (!File.Exists(exe))
                {
                    statusLine = "Client bridge not found. Install the full BepInEx/plugins folder.";
                    return;
                }
                var info = new ProcessStartInfo(exe);
                info.Arguments = "--bridge-dir " + Quote(directory);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WorkingDirectory = Path.GetDirectoryName(exe);
                localBridge = Process.Start(info);
                statusLine = "Starting Archipelago client...";
            }
            catch (Exception error)
            {
                statusLine = "Bridge startup failed: " + error.Message;
                Logger.LogWarning("[AP BRIDGE] " + error);
            }
        }

        private static string Quote(string value)
        {
            string quotationMark = ((char)34).ToString();
            string slash = ((char)92).ToString();
            return quotationMark + value.Replace(quotationMark, slash + quotationMark) + quotationMark;
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

        private void SelectMission(MissionFile mission)
        {
            if (mission == null || String.IsNullOrEmpty(token)) return;
            chosenMission = mission.id;
            try
            {
                File.WriteAllText(Path.Combine(directory, "mission_selection.json"),
                    JsonUtility.ToJson(new MissionSelectionFile {
                        token = token, mission_id = mission.id,
                        map = mission.map, mode = mission.mode
                    }));
                Logger.LogInfo("[AP MISSION] Selected " + mission.id +
                    ", map " + mission.map + ", mode " + mission.mode);
            }
            catch (Exception error) { Logger.LogWarning("[AP MISSION] " + error.Message); }
        }

        private void DrawBrandingButton()
        {
            LoadApLogo();
            Rect target = new Rect(21, 18, 54, 54);
            bool hovered = target.Contains(Event.current.mousePosition);
            float scale = logoScale;
            Rect logoRect = new Rect(
                target.center.x - target.width * scale * .5f,
                target.center.y - target.height * scale * .5f,
                target.width * scale, target.height * scale);
            GUI.Box(new Rect(15, 12, 67, 68), "", cardStyle);
            GUI.color = hovered || apMenuOpen ? Color.white : new Color(1f, 1f, 1f, .87f);
            if (apLogo != null)
                GUI.DrawTexture(logoRect, apLogo, ScaleMode.ScaleToFit, true);
            else
                GUI.Label(logoRect, "AP", subTitleStyle);
            GUI.color = Color.white;
            if (GUI.Button(new Rect(15, 12, 67, 68), "", logoButton))
                apMenuOpen = !apMenuOpen;
            if (bridgeConnected)
                GUI.DrawTexture(new Rect(70, 17, 8, 8), greenPixel);
            if (hovered)
                GUI.Label(new Rect(89, 30, 188, 30), "Archipelago", eyebrowStyle);
        }

        private void DrawBackdrop()
        {
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), overlayPixel);
        }

        private void DrawTopBar(Rect panel, string kicker, string heading, string subtitle)
        {
            GUI.Label(new Rect(panel.x + 28, panel.y + 22, panel.width - 104, 20),
                kicker, eyebrowStyle);
            GUI.Label(new Rect(panel.x + 28, panel.y + 48, panel.width - 100, 36),
                heading, titleStyle);
            GUI.Label(new Rect(panel.x + 28, panel.y + 91, panel.width - 55, 36),
                subtitle, mutedStyle);
            GUI.DrawTexture(new Rect(panel.x + 28, panel.y + 135,
                panel.width - 56, 1), borderPixel);
        }

        private void DrawConnection()
        {
            float width = Mathf.Min(665f, Mathf.Max(300f, Screen.width - 28f));
            float height = Mathf.Min(580f, Mathf.Max(380f, Screen.height - 30f));
            Rect panel = new Rect((Screen.width - width) / 2f,
                                  (Screen.height - height) / 2f, width, height);
            DrawBackdrop();
            GUI.Box(panel, "", panelStyle);
            DrawTopBar(panel, "MULTIWORLD  /  CONNECTION",
                       "Archipelago", "Connect to your seed and unlock its Castle Story campaign.");
            if (GUI.Button(new Rect(panel.xMax - 68, panel.y + 25, 40, 38),
                           "X", closeButton))
                apMenuOpen = false;

            float x = panel.x + 30;
            float inner = panel.width - 60;
            float y = panel.y + 154;
            GUI.Label(new Rect(x, y, inner, 20), "SERVER ADDRESS", inputLabelStyle);
            loginServer = GUI.TextField(new Rect(x, y + 24, inner, 43),
                loginServer, 240, inputStyle);
            y += 79;
            GUI.Label(new Rect(x, y, inner, 20), "SLOT NAME", inputLabelStyle);
            loginSlot = GUI.TextField(new Rect(x, y + 24, inner, 43),
                loginSlot, 140, inputStyle);
            y += 79;
            GUI.Label(new Rect(x, y, inner, 20), "PASSWORD  (OPTIONAL)", inputLabelStyle);
            loginPassword = GUI.PasswordField(new Rect(x, y + 24, inner, 43),
                loginPassword, '*', 120, inputStyle);

            float connectionY = panel.yMax - 138;
            GUI.Box(new Rect(x, connectionY, inner, 54), "", cardStyle);
            GUI.DrawTexture(new Rect(x + 13, connectionY + 21, 10, 10),
                bridgeConnected ? greenPixel : borderPixel);
            GUI.Label(new Rect(x + 33, connectionY + 7, inner - 48, 38),
                bridgeConnected ? "Connected  /  " + mode : statusLine,
                bridgeConnected ? bodyStyle : miniStyle);
            float bottom = panel.yMax - 64;
            if (GUI.Button(new Rect(x, bottom, inner * .63f - 7, 43),
                           bridgeConnected ? "View campaign" : "Connect to server",
                           primaryButton))
            {
                if (bridgeConnected)
                {
                    apMenuOpen = false;
                    nativeNewGameVisible = true;
                }
                else WriteConnection(true);
            }
            if (GUI.Button(new Rect(x + inner * .63f + 5, bottom,
                                     inner * .37f - 5, 43),
                           bridgeConnected ? "Disconnect" : "Close",
                           secondaryButton))
            {
                if (bridgeConnected) WriteConnection(false);
                else apMenuOpen = false;
            }
        }

        private int CountMissionChecks(MissionFile mission)
        {
            if (mission == null) return 0;
            if (mission.mode == "Invasion")
            {
                int count = 0;
                for (int i = 1; i <= mission.wave_goal; i++)
                    if (checkedIds.Contains(WaveBase + i)) count++;
                return count;
            }
            int captured = 0;
            for (int i = 0; i < mission.shard_count; i++)
                if (checkedIds.Contains(ConquestBase + mission.shard_start + i))
                    captured++;
            return captured;
        }

        private void DrawMissionCard(Rect card, MissionFile mission, int number)
        {
            if (mission == null) return;
            bool selected = chosenMission == mission.id;
            GUI.Box(card, "", selected ? activeCardStyle : cardStyle);
            GUI.DrawTexture(new Rect(card.x + 14, card.y + 16, 4, card.height - 32),
                mission.mode == "Conquest" ? greenPixel : accentPixel);
            GUI.Label(new Rect(card.x + 29, card.y + 9, card.width - 180, 22),
                "MISSION " + number + "  /  " + mission.mode.ToUpperInvariant(),
                eyebrowStyle);
            GUI.Label(new Rect(card.x + 29, card.y + 31, card.width - 160, 28),
                mission.map ?? "<unknown map>", subTitleStyle);
            int goal = mission.mode == "Invasion" ? mission.wave_goal : mission.shard_count;
            GUI.Label(new Rect(card.x + 29, card.y + 62, card.width - 175, 22),
                mission.mode == "Invasion" ?
                    ("Survive " + goal + " waves") :
                    ("Capture " + goal + " shards  /  " + CountMissionChecks(mission) + " completed"),
                mutedStyle);
            if (GUI.Button(new Rect(card.xMax - 129, card.y + 29, 113, 39),
                           selected ? "Selected" : "Select",
                           selected ? primaryButton : secondaryButton))
                SelectMission(mission);
        }

        private void DrawCampaign()
        {
            float width = Mathf.Min(900f, Mathf.Max(320f, Screen.width - 26f));
            float height = Mathf.Min(730f, Mathf.Max(430f, Screen.height - 30f));
            Rect panel = new Rect((Screen.width - width) / 2f,
                                  (Screen.height - height) / 2f, width, height);
            DrawBackdrop();
            GUI.Box(panel, "", panelStyle);
            DrawTopBar(panel, "ARCHIPELAGO  /  CAMPAIGN",
                       "Your missions",
                       "This run controls which maps and game modes you can play.");
            float x = panel.x + 28, inner = panel.width - 56;
            if (GUI.Button(new Rect(panel.xMax - 85, panel.y + 25, 56, 38),
                           "Back", subtleButton))
            {
                apMenuOpen = false;
                // Closing the native screen also dismisses the AP mission picker.
                CloseNativeNewGame();
                return;
            }
            if (campaign == null || campaign.missions == null)
            {
                GUI.Box(new Rect(x, panel.y + 167, inner, 118), "", cardStyle);
                GUI.Label(new Rect(x + 20, panel.y + 182, inner - 40, 33),
                    "No campaign manifest received", subTitleStyle);
                GUI.Label(new Rect(x + 20, panel.y + 220, inner - 40, 53),
                    "Connect with an updated Castle Story APWorld and generate a new seed in ArchiLAN.",
                    mutedStyle);
                return;
            }
            if (!campaign.ready)
            {
                GUI.Box(new Rect(x, panel.y + 167, inner, 150), "", cardStyle);
                GUI.Label(new Rect(x + 20, panel.y + 182, inner - 40, 28),
                    "This campaign is not ready", subTitleStyle);
                GUI.Label(new Rect(x + 20, panel.y + 218, inner - 40, 84),
                    String.IsNullOrEmpty(campaign.warning) ?
                    "Your seed did not provide a playable map list." : campaign.warning,
                    errorStyle);
                return;
            }
            GUI.Label(new Rect(x, panel.y + 154, inner, 22),
                (campaign.mode ?? mode).ToUpperInvariant() +
                "  /  " + campaign.missions.Length + " MAPS",
                eyebrowStyle);
            float listY = panel.y + 189;
            float listHeight = Mathf.Max(100, panel.height - 300f);
            Rect viewport = new Rect(x, listY, inner, listHeight);
            float rowHeight = 104f;
            Rect content = new Rect(0, 0, inner - 20,
                Mathf.Max(listHeight - 1, campaign.missions.Length * rowHeight));
            missionScroll = GUI.BeginScrollView(viewport, missionScroll, content);
            for (int i = 0; i < campaign.missions.Length; i++)
                DrawMissionCard(new Rect(0, i * rowHeight, content.width, 96),
                                campaign.missions[i], i + 1);
            GUI.EndScrollView();

            float footerY = panel.yMax - 95;
            GUI.DrawTexture(new Rect(x, footerY - 14, inner, 1), borderPixel);
            bool isChosen = !String.IsNullOrEmpty(chosenMission);
            GUI.Label(new Rect(x, footerY, inner * .52f, 30),
                isChosen ? "Ready to launch selected mission" :
                    "Select a mission to continue.", mutedStyle);
            if (GUI.Button(new Rect(panel.xMax - 286, footerY - 3, 258, 46),
                           "Launch selected mission", primaryButton))
            {
                if (isChosen) LaunchSelectedMission();
                else statusLine = "Please select an Archipelago mission.";
            }
            GUI.Label(new Rect(x, panel.yMax - 39, inner, 23),
                "Mission launch uses Castle Story's native loader. " + statusLine,
                miniStyle);
        }

        private void DrawApMenu()
        {
            PrepareSkin();
            ReadLocalBridgeStatus();
            int originalDepth = GUI.depth;
            Color originalColor = GUI.color;
            GUI.depth = -29000;

            // Always draw the AP icon; it is separate from the optional F8 diagnostic.
            if (bridgeConnected && nativeNewGameVisible)
            {
                DrawCampaign();
                // Block clicks on the original map-selection UI under this modal overlay.
                if (Event.current.type == EventType.MouseDown ||
                    Event.current.type == EventType.MouseUp ||
                    Event.current.type == EventType.ScrollWheel)
                    Event.current.Use();
            }
            else if (apMenuOpen)
                DrawConnection();

            DrawBrandingButton();
            GUI.color = originalColor;
            GUI.depth = originalDepth;
        }

        private void CleanupApUi()
        {
            if (apLogo != null) UnityEngine.Object.Destroy(apLogo);
            apLogo = null;
            skinReady = false;
        }
    }
}
