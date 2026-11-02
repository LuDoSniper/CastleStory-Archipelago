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
            public string map_id;
            public int wave_goal;
            public int shard_start;
            public int shard_count;
        }
        [Serializable] private sealed class CampaignFile
        {
            public bool ready;
            public string warning;
            public string mode;
            public string[] missing_modes;
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
        private bool campaignPreviewOpen;
        // Closing the AP popup must not navigate the vanilla new-game menu.
        // Prevent a duplicate OpenSelf callback from reopening the popup.
        private bool apPopupDismissedOnNativeScreen;
        private int slotProtocolVersion;
        private string bridgeClientVersion = "";
        private string[] serverSlotDataFields;
        private bool serverManifestProvided;
        private bool serverSlotDataProvided;
        private bool disconnectPending;
        private string campaignStatus = "unknown";
        private string campaignMessage = "";
        private string loginServer = "";
        private string loginSlot = "";
        private string loginPassword = "";
        private string chosenMission = "";
        private string statusLine = "Connecte-toi à ta partie Archipelago.";
        // Bridge heartbeat is distinct from a user-action warning.
        private string campaignNotice = "";
        private bool campaignNoticeIsError;
        private string lastUiException = "";

        private void SetCampaignNotice(string message, bool isError)
        {
            campaignNotice = message ?? "";
            campaignNoticeIsError = isError;
            if (isError)
                Logger.LogWarning("[AP ACTION] " + campaignNotice);
            else
                Logger.LogInfo("[AP ACTION] " + campaignNotice);
        }
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
            // Use the real native Play button's font if Castle Story exposes it.
            ApplyNativeTypography();
        }

        [Serializable] private sealed class RememberedConnection
        {
            public string server;
            public string slot;
        }

        private void LoadRememberedConnection()
        {
            string preferences = Path.Combine(directory, "connection_preferences.json");
            string oldFile = Path.Combine(directory, "connection.json");
            try
            {
                // Migrate plaintext credentials from previous versions without
                // retaining their password. The old file is removed immediately.
                if (!File.Exists(preferences) && File.Exists(oldFile))
                {
                    var old = JsonUtility.FromJson<ConnectionFile>(
                        File.ReadAllText(oldFile));
                    if (old != null)
                    {
                        loginServer = old.server ?? "";
                        loginSlot = old.slot ?? "";
                        SaveRememberedConnection();
                    }
                }
                if (File.Exists(oldFile)) File.Delete(oldFile);
                if (File.Exists(preferences))
                {
                    var prefs = JsonUtility.FromJson<RememberedConnection>(
                        File.ReadAllText(preferences));
                    if (prefs != null)
                    {
                        loginServer = prefs.server ?? "";
                        loginSlot = prefs.slot ?? "";
                    }
                }
            }
            catch (Exception error)
            {
                Logger.LogWarning("[AP SESSION] Cannot migrate saved server/slot: " +
                    error.Message);
                // Do not retain an old connection.json with a plaintext password.
                try { if (File.Exists(oldFile)) File.Delete(oldFile); }
                catch (Exception ignored) {
                    Logger.LogWarning("[AP SESSION] Cannot delete legacy credentials: " +
                        ignored.GetType().Name);
                }
            }
            loginPassword = "";
        }

        private void SaveRememberedConnection()
        {
            File.WriteAllText(Path.Combine(directory, "connection_preferences.json"),
                JsonUtility.ToJson(new RememberedConnection {
                    server = loginServer.Trim(), slot = loginSlot.Trim()
                }));
        }

        private void SendBridgeControl(bool enable)
        {
            if (localBridge == null || localBridge.HasExited)
                throw new InvalidOperationException("Client Archipelago absent.");
            // StandardInput is an anonymous pipe. No password is written to
            // connection.json, arguments, environment variables or diagnostics.
            string payload = JsonUtility.ToJson(new ConnectionFile {
                enabled = enable, server = enable ? loginServer.Trim() : "",
                slot = enable ? loginSlot.Trim() : "",
                password = enable ? loginPassword : ""
            });
            // Bridge protocol uses cmd=connect/disconnect, not enabled.
            string command = enable ? "connect" : "disconnect";
            payload = payload.Substring(0, payload.Length - 1) +
                ",\"cmd\":\"" + command + "\"}";
            // Use ASCII-safe framed base64 to preserve Unicode passwords and
            // slot names regardless of the Windows console code page.
            string wire = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));
            localBridge.StandardInput.WriteLine("APIPC1:" + wire);
            localBridge.StandardInput.Flush();
        }

        private void WriteConnection(bool enable)
        {
            try
            {
                if (enable && (String.IsNullOrEmpty(loginServer.Trim()) ||
                               String.IsNullOrEmpty(loginSlot.Trim())))
                {
                    statusLine = "L'adresse du serveur et le nom du slot sont obligatoires.";
                    return;
                }
                if (enable)
                {
                    SaveRememberedConnection();
                    if (!StartLocalBridge()) return;
                }
                else if (localBridge == null || localBridge.HasExited)
                {
                    // The plugin can safely mark the session disconnected
                    // even if the child bridge has already exited.
                    disconnectPending = true;
                    bridgeConnected = false;
                    loginPassword = "";
                    return;
                }
                try { SendBridgeControl(enable); }
                finally { loginPassword = ""; }
                statusLine = enable ? "Connexion en cours…" : "Déconnexion demandée…";
                campaignNotice = "";
                if (enable)
                {
                    disconnectPending = false;
                    bridgeConnected = false;
                }
                else
                {
                    disconnectPending = true;
                    nativeNewGameVisible = false;
                    campaignPreviewOpen = false;
                    apMenuOpen = true;
                    bridgeConnected = false;
                    Logger.LogInfo("[AP SESSION] User requested disconnection.");
                }
            }
            catch (Exception error)
            {
                loginPassword = "";
                statusLine = "Connexion au bridge impossible : " + error.Message;
                Logger.LogWarning("[AP BRIDGE] Control channel: " +
                    error.GetType().Name);
            }
        }

        private bool StartLocalBridge()
        {
            try
            {
                if (localBridge != null && !localBridge.HasExited) return true;
                string exe = Path.Combine(Path.Combine(Paths.PluginPath,
                    "castle_story_ap"), "castle-story-ap-bridge.exe");
                if (!File.Exists(exe))
                {
                    statusLine = "Client manquant : installe tout le dossier BepInEx/plugins.";
                    return false;
                }
                var info = new ProcessStartInfo(exe);
                info.Arguments = "--stdin-control --bridge-dir " + Quote(directory);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardInput = true;
                info.WorkingDirectory = Path.GetDirectoryName(exe);
                localBridge = Process.Start(info);
                return localBridge != null;
            }
            catch (Exception error)
            {
                statusLine = "Démarrage du bridge impossible : " + error.Message;
                Logger.LogWarning("[AP BRIDGE] " + error.GetType().Name);
                return false;
            }
        }

        private static string Quote(string value)
        {
            string quotationMark = ((char)34).ToString();
            string slash = ((char)92).ToString();
            return quotationMark + value.Replace(quotationMark, slash + quotationMark) + quotationMark;
        }

        private string lastBridgeStatusState = "";

        private void ReadLocalBridgeStatus()
        {
            if (Time.realtimeSinceStartup < nextBridgeStatusRead) return;
            nextBridgeStatusRead = Time.realtimeSinceStartup + 1f;
            try
            {
                string path = Path.Combine(directory, "bridge_status.json");
                if (!File.Exists(path)) return;
                var state = JsonUtility.FromJson<BridgeStatusFile>(File.ReadAllText(path));
                if (state != null && !String.IsNullOrEmpty(state.state) &&
                    lastBridgeStatusState != state.state)
                {
                    lastBridgeStatusState = state.state;
                    Logger.LogInfo("[AP LOGIN STATE] Bridge state=" + state.state +
                        "; waitingForUser=" + (state.state == "idle") +
                        "; inSession=" + bridgeConnected);
                }
                // Do not hide an obsolete-client error behind a stale bridge status.
                // Never clobber an action error with the heartbeat every second.
                if (state != null && !String.IsNullOrEmpty(state.state) &&
                    campaignStatus != "obsolete_bridge" && campaignStatus != "stale" &&
                    !bridgeConnected && String.IsNullOrEmpty(campaignNotice))
                    statusLine = state.state + ": " + (state.message ?? "");
            }
            catch (Exception error) { Logger.LogWarning("[AP BRIDGE] " + error.Message); }
        }

        private void SelectMission(MissionFile mission)
        {
            if (mission == null || String.IsNullOrEmpty(token)) return;
            if (mission.map_id == "auto:invasion")
            {
                SetCampaignNotice(
                    "Ouvre Nouvelle partie dans Castle Story pour charger les cartes Invasion.",
                    true);
                return;
            }
            chosenMission = mission.id;
            try
            {
                File.WriteAllText(Path.Combine(directory, "mission_selection.json"),
                    JsonUtility.ToJson(new MissionSelectionFile {
                        token = token, mission_id = mission.id,
                        map = MissionMapId(mission), mode = mission.mode
                    }));
                SetCampaignNotice("Mission " + mission.id +
                    " sélectionnée. " + (nativeStarterMenu == null ?
                    "Ferme cette fenêtre puis ouvre Nouvelle partie pour la lancer." :
                    "Tu peux maintenant lancer la mission."), false);
                Logger.LogInfo("[AP MISSION] Choisie " + mission.id +
                    ", map " + mission.map + ", mode " + mission.mode);
            }
            catch (Exception error)
            {
                SetCampaignNotice("Impossible de sélectionner la mission : " + error.Message, true);
                Logger.LogWarning("[AP MISSION] " + error.Message);
            }
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
            GUI.color = hovered || apMenuOpen ? Color.white : new Color(1f, 1f, 1f, .87f);
            if (apLogo != null)
                GUI.DrawTexture(logoRect, apLogo, ScaleMode.ScaleToFit, true);
            else
                GUI.Label(logoRect, "AP", subTitleStyle);
            GUI.color = Color.white;
            if (GUI.Button(new Rect(15, 12, 67, 68), "", GUIStyle.none))
            {
                if (nativeNewGameVisible || campaignPreviewOpen)
                {
                    CloseCampaignPopup();
                    apMenuOpen = true;
                }
                else apMenuOpen = !apMenuOpen;
            }
            if (bridgeConnected)
                GUI.DrawTexture(new Rect(70, 17, 8, 8), greenPixel);
            if (hovered)
                GUI.Label(new Rect(89, 30, 188, 30), "Archipelago", eyebrowStyle);
        }

        private void DrawBackdrop()
        {
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), overlayPixel);
        }

        private void BlockImGuiClicksOutsidePanel(Rect panel)
        {
            // The native Unity UI shield protects underlying Canvas buttons.
            // These four invisible IMGUI controls protect any legacy IMGUI
            // elements OUTSIDE the modal without stealing its own button clicks.
            Rect[] outside = {
                new Rect(0, 0, Screen.width, Mathf.Max(0, panel.y)),
                new Rect(0, panel.y, Mathf.Max(0, panel.x), panel.height),
                new Rect(panel.xMax, panel.y,
                    Mathf.Max(0, Screen.width - panel.xMax), panel.height),
                new Rect(0, panel.yMax, Screen.width,
                    Mathf.Max(0, Screen.height - panel.yMax))
            };
            foreach (Rect area in outside)
                if (area.width > 0 && area.height > 0)
                    GUI.Button(area, "", GUIStyle.none);
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
            BlockImGuiClicksOutsidePanel(panel);
            GUI.Box(panel, "", panelStyle);
            DrawTopBar(panel, "CASTLE STORY  /  ARCHIPELAGO",
                       "Archipelago", bridgeConnected ?
                       "Session active. Le menu Nouvelle partie suit ta seed." :
                       "Connecte-toi pour activer la campagne Archipelago.");
            if (GUI.Button(new Rect(panel.xMax - 68, panel.y + 25, 40, 38),
                           "X", closeButton))
                apMenuOpen = false;

            float x = panel.x + 30;
            float inner = panel.width - 60;
            float y = panel.y + 154;
            GUI.Label(new Rect(x, y, inner, 20), "ADRESSE DU SERVEUR", inputLabelStyle);
            loginServer = GUI.TextField(new Rect(x, y + 24, inner, 43),
                loginServer, 240, inputStyle);
            y += 79;
            GUI.Label(new Rect(x, y, inner, 20), "NOM DU SLOT", inputLabelStyle);
            loginSlot = GUI.TextField(new Rect(x, y + 24, inner, 43),
                loginSlot, 140, inputStyle);
            y += 79;
            GUI.Label(new Rect(x, y, inner, 20), "MOT DE PASSE  (FACULTATIF)", inputLabelStyle);
            loginPassword = GUI.PasswordField(new Rect(x, y + 24, inner, 43),
                loginPassword, '*', 120, inputStyle);

            float connectionY = panel.yMax - 138;
            GUI.Box(new Rect(x, connectionY, inner, 54), "", cardStyle);
            GUI.DrawTexture(new Rect(x + 13, connectionY + 21, 10, 10),
                bridgeConnected ? greenPixel : borderPixel);
            GUI.Label(new Rect(x + 33, connectionY + 7, inner - 48, 38),
                bridgeConnected ? "Connecté  /  " + mode : statusLine,
                bridgeConnected ? bodyStyle : miniStyle);
            float bottom = panel.yMax - 64;
            if (GUI.Button(new Rect(x, bottom, inner * .63f - 7, 43),
                           bridgeConnected ? "Retour au jeu" : "Se connecter",
                           primaryButton))
            {
                if (bridgeConnected) apMenuOpen = false;
                else WriteConnection(true);
            }
            if (GUI.Button(new Rect(x + inner * .63f + 5, bottom,
                                     inner * .37f - 5, 43),
                           bridgeConnected ? "Déconnexion" : "Fermer",
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
            bool waitingForMap = mission.map_id == "auto:invasion";
            GUI.Box(card, "", selected ? activeCardStyle : cardStyle);
            GUI.DrawTexture(new Rect(card.x + 14, card.y + 16, 4, card.height - 32),
                mission.mode == "Conquest" ? greenPixel : accentPixel);
            GUI.Label(new Rect(card.x + 29, card.y + 9, card.width - 180, 22),
                "MISSION " + number + "  /  " + mission.mode.ToUpperInvariant(),
                eyebrowStyle);
            GUI.Label(new Rect(card.x + 29, card.y + 31, card.width - 160, 28),
                waitingForMap ? "Carte Invasion : sélection en cours…" :
                    mission.map ?? mission.map_id ?? "<unknown map>", subTitleStyle);
            int goal = mission.mode == "Invasion" ? mission.wave_goal : mission.shard_count;
            GUI.Label(new Rect(card.x + 29, card.y + 62, card.width - 175, 22),
                mission.mode == "Invasion" ?
                    ("Survivre à " + goal + " vagues") :
                    ("Capturer " + goal + " shards  /  " + CountMissionChecks(mission) + " terminés"),
                mutedStyle);
            if (GUI.Button(new Rect(card.xMax - 129, card.y + 29, 113, 39),
                           waitingForMap ? "Chargement" :
                               selected ? "Choisie" : "Choisir",
                           selected ? primaryButton : secondaryButton))
            {
                if (!waitingForMap) SelectMission(mission);
                else SetCampaignNotice(
                    "Ouvre Nouvelle partie et attends le catalogue de cartes.", true);
            }
        }

        private void CloseCampaignPopup()
        {
            KeepInputShieldUntilMouseReleased();
            // Only dismiss OUR overlay. The native Invasion/Conquest map selector
            // stays open so its own Back button continues to work normally.
            if (nativeStarterMenu != null)
                apPopupDismissedOnNativeScreen = true;
            nativeNewGameVisible = false;
            campaignPreviewOpen = false;
            apMenuOpen = false;
            GUIUtility.keyboardControl = 0;
            Logger.LogInfo("[AP MENU] AP campaign popup closed without navigating native menus.");
        }

        private void DrawCampaign()
        {
            float width = Mathf.Min(900f, Mathf.Max(320f, Screen.width - 26f));
            float height = Mathf.Min(730f, Mathf.Max(430f, Screen.height - 30f));
            Rect panel = new Rect((Screen.width - width) / 2f,
                                  (Screen.height - height) / 2f, width, height);
            DrawBackdrop();
            BlockImGuiClicksOutsidePanel(panel);
            GUI.Box(panel, "", panelStyle);
            DrawTopBar(panel, "ARCHIPELAGO  /  CAMPAGNE",
                       "Missions de la seed",
                       "Cette seed impose les cartes et les modes à jouer.");
            float x = panel.x + 28, inner = panel.width - 56;
            if (Event.current.type == EventType.KeyDown &&
                Event.current.keyCode == KeyCode.Escape)
            {
                CloseCampaignPopup();
                Event.current.Use();
                return;
            }
            if (GUI.Button(new Rect(panel.xMax - 245, panel.y + 25, 130, 38),
                           "Déconnexion", secondaryButton))
            {
                WriteConnection(false);
                return;
            }
            if (GUI.Button(new Rect(panel.xMax - 101, panel.y + 25, 72, 38),
                           "Fermer", subtleButton))
            {
                CloseCampaignPopup();
                return;
            }
            if (campaign == null || campaign.missions == null ||
                (campaign.missions.Length == 0 && campaignStatus == "ready"))
            {
                GUI.Box(new Rect(x, panel.y + 167, inner, 320), "", cardStyle);
                GUI.Label(new Rect(x + 20, panel.y + 182, inner - 40, 33),
                    "Campagne Archipelago indisponible", subTitleStyle);
                string reason = String.IsNullOrEmpty(campaignMessage) ?
                    (campaignStatus == "missing" ?
                        "ArchiLAN a accepté la connexion mais n'a transmis aucun manifeste de campagne. Une ancienne seed peut fonctionner sans missions : importe le nouvel APWorld et génère une nouvelle seed." :
                        "La session est connectée, mais la liste des missions n'a pas été chargée. Consulte le diagnostic ci-dessous.") :
                    campaignMessage;
                GUI.Label(new Rect(x + 20, panel.y + 221, inner - 40, 75),
                    reason, mutedStyle);
                GUI.Label(new Rect(x + 20, panel.y + 300, inner - 40, 26),
                    "Seed : " + (String.IsNullOrEmpty(seed) ? "inconnue" : seed) +
                    "  |  Mode : " + mode + "  |  Protocole : " +
                    slotProtocolVersion, miniStyle);
                GUI.Label(new Rect(x + 20, panel.y + 327, inner - 40, 25),
                    "Bridge : " + (String.IsNullOrEmpty(bridgeClientVersion) ?
                        "ancien / inconnu" : bridgeClientVersion) +
                    "  |  Manifeste transmis : " +
                    (serverManifestProvided ? "oui" : "non") +
                    "  |  Slot data : " +
                    (serverSlotDataProvided ? "oui" : "non"), miniStyle);
                GUI.Label(new Rect(x + 20, panel.y + 352, inner - 40, 22),
                    "Champs du serveur : " +
                    (serverSlotDataFields == null || serverSlotDataFields.Length == 0 ?
                        "aucun" : String.Join(", ", serverSlotDataFields)), miniStyle);
                GUI.Label(new Rect(x + 20, panel.y + 377, inner - 40, 22),
                    "Missions côté bridge : " + bridgeManifestMissionCount +
                    "  |  Missions côté Unity : 0  |  État : " +
                    campaignStatus, miniStyle);
                GUI.Label(new Rect(x + 20, panel.y + 402, inner - 40, 55),
                    String.IsNullOrEmpty(campaignDecodeDiagnostic) ?
                        "Diagnostic en cours..." : campaignDecodeDiagnostic,
                    errorStyle);

                return;
            }
            if (!campaign.ready)
            {
                GUI.Box(new Rect(x, panel.y + 167, inner, 150), "", cardStyle);
                bool hybrid = mode == "Hybrid" || campaign.mode == "Hybrid";
                string[] missing = campaign.missing_modes;
                string missingText = missing == null || missing.Length == 0 ?
                    "" : "Branche manquante : " + String.Join(", ", missing) + ". ";
                GUI.Label(new Rect(x + 20, panel.y + 182, inner - 40, 28),
                    hybrid ? "Campagne Hybride incomplète" :
                        "Cette campagne n'est pas prête", subTitleStyle);
                GUI.Label(new Rect(x + 20, panel.y + 218, inner - 40, 95),
                    missingText + (String.IsNullOrEmpty(campaign.warning) ?
                        "La seed ne contient pas toutes les missions requises." :
                        campaign.warning), errorStyle);
                return;
            }
            GUI.Label(new Rect(x, panel.y + 154, inner, 22),
                (campaign.mode ?? mode).ToUpperInvariant() +
                "  /  " + campaign.missions.Length + " CARTES",
                eyebrowStyle);
            float listY = panel.y + 189;
            // Make room for a persistent, readable message banner.
            float listHeight = Mathf.Max(80f, panel.height - 350f);
            Rect viewport = new Rect(x, listY, inner, listHeight);
            float rowHeight = 104f;
            Rect content = new Rect(0, 0, inner - 20,
                Mathf.Max(listHeight - 1, campaign.missions.Length * rowHeight));
            missionScroll = GUI.BeginScrollView(viewport, missionScroll, content);
            try
            {
                for (int i = 0; i < campaign.missions.Length; i++)
                    DrawMissionCard(new Rect(0, i * rowHeight, content.width, 96),
                                    campaign.missions[i], i + 1);
            }
            finally { GUI.EndScrollView(); }

            float noticeY = panel.yMax - 151;
            GUI.Box(new Rect(x, noticeY, inner, 48), "", cardStyle);
            string notice = !bridgeConnected ?
                "Connexion Archipelago interrompue. Vérifie le bridge et reconnecte-toi." :
                !String.IsNullOrEmpty(campaignNotice) ? campaignNotice :
                nativeStarterMenu == null ?
                    "Pour jouer : ferme cette fenêtre, ouvre Nouvelle partie et choisis la mission." :
                    "Choisis une mission puis clique sur Lancer cette mission.";
            GUI.Label(new Rect(x + 12, noticeY + 6, inner - 24, 38),
                notice, !bridgeConnected || campaignNoticeIsError ?
                    errorStyle : mutedStyle);

            float footerY = panel.yMax - 95;
            GUI.DrawTexture(new Rect(x, footerY - 14, inner, 1), borderPixel);
            bool isChosen = !String.IsNullOrEmpty(chosenMission);
            GUI.Label(new Rect(x, footerY, inner * .52f, 30),
                isChosen ? "Mission sélectionnée, prête à lancer" :
                    "Choisis une mission pour continuer.", mutedStyle);
            if (GUI.Button(new Rect(panel.xMax - 286, footerY - 3, 258, 46),
                           "Lancer cette mission", primaryButton))
            {
                if (isChosen) LaunchSelectedMission();
                else SetCampaignNotice(
                    "Choisis d'abord une mission Archipelago.", true);
            }
            GUI.Label(new Rect(x, panel.yMax - 39, inner, 23),
                "Lancement via Castle Story. " +
                    (campaignStatus == "waiting_for_game" ?
                        campaignMessage : statusLine),
                miniStyle);
        }

        private void HandleModalRawInput()
        {
            // Mouse events may be consumed by Unity UI before IMGUI sees them.
            // Input.GetMouseButtonDown provides an independent, reliable way
            // to dismiss the campaign dialog.
            if (!(nativeNewGameVisible || campaignPreviewOpen))
                return;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Logger.LogInfo("[AP UI] Escape: closing campaign dialog via Update.");
                CloseCampaignPopup();
                return;
            }
            if (!Input.GetMouseButtonDown(0)) return;
            Vector2 pos = new Vector2(Input.mousePosition.x,
                                      Screen.height - Input.mousePosition.y);
            float width = Mathf.Min(900f, Mathf.Max(320f, Screen.width - 26f));
            float height = Mathf.Min(730f, Mathf.Max(430f, Screen.height - 30f));
            Rect panel = new Rect((Screen.width - width) * .5f,
                                  (Screen.height - height) * .5f, width, height);
            Rect close = new Rect(panel.xMax - 101, panel.y + 25, 72, 38);
            if (close.Contains(pos))
            {
                Logger.LogInfo("[AP UI] MouseDown on Fermer at " + pos);
                CloseCampaignPopup();
            }
        }

        private void DrawApMenu()
        {
            int originalDepth = GUI.depth;
            Color originalColor = GUI.color;
            try
            {
                PrepareSkin();
                ReadLocalBridgeStatus();
                GUI.depth = -29000;

                // The map and mode selection now live entirely in Castle
                // Story's native menu. Only the connection dialog remains.
                if (apMenuOpen) DrawConnection();
                else DrawArchipelagoIntroCredit();
                lastUiException = "";
            }
            catch (Exception error)
            {
                string diagnostic = error.ToString();
                if (lastUiException != diagnostic)
                {
                    lastUiException = diagnostic;
                    Logger.LogError("[AP UI EXCEPTION] " + diagnostic);
                    campaignNotice = "Erreur d'interface : " + error.Message;
                    campaignNoticeIsError = true;
                }
            }
            finally
            {
                GUI.color = originalColor;
                GUI.depth = originalDepth;
            }
        }

        private void CleanupApUi()
        {
            if (apLogo != null) UnityEngine.Object.Destroy(apLogo);
            apLogo = null;
            skinReady = false;
        }
    }
}
