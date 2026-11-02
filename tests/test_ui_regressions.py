from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
UI = ROOT / "mod/ArchipelagoMenu.cs"
NATIVE = ROOT / "mod/NativeMenuHooks.cs"
INTEGRATION = ROOT / "mod/NativeArchipelagoIntegration.cs"
PLUGIN = ROOT / "mod/CastleStoryArchipelagoPlugin.cs"


class NativeUiIntegrationTests(unittest.TestCase):
    def test_main_menu_uses_native_play_button_not_floating_logo(self):
        ui = UI.read_text(encoding="utf-8")
        native = INTEGRATION.read_text(encoding="utf-8")
        plugin = PLUGIN.read_text(encoding="utf-8")
        self.assertNotIn("                DrawBrandingButton();", ui)
        self.assertIn("Archipelago_NativeMainMenuEntry", native)
        self.assertIn("copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1)", native)
        self.assertIn('SetNativeUiLabel(copy, "Archipelago")', native)
        self.assertIn("InstallNativeButtonListener", native)
        self.assertIn("clickProperty.SetValue(clonedButton, click", native)
        self.assertIn("if (Input.GetKeyDown(KeyCode.F7))", plugin)

    def test_native_seed_mode_map_restrictions_and_restore(self):
        code = INTEGRATION.read_text(encoding="utf-8")
        self.assertIn("IsSandboxLabel(label)", code)
        self.assertIn('mode != "Hybrid" && namedMode != mode', code)
        self.assertIn("!AllowedSeedMap(mapId, selectedNativeMode)", code)
        self.assertIn("nativeMapLabels.TryGetValue", code)
        self.assertIn("private void RestoreNativeEntries()", code)
        self.assertIn("if (hiddenNativeEntries.Count > 0) RestoreNativeEntries();", code)
        self.assertIn("if (lastNativeFilterKey != filterKey)", code)
        self.assertIn("FindPlayMenuRoot(controls, labels)", code)
        self.assertIn("SelectedNativeGamemode()", code)
        self.assertIn("RestoreNativeMenuLayout()", code)
        self.assertIn("disconnectPending || String.IsNullOrEmpty(token)", code)

    def test_native_ui_filter_is_event_driven_and_bounded(self):
        code = INTEGRATION.read_text(encoding="utf-8")
        hooks = NATIVE.read_text(encoding="utf-8")
        self.assertIn("pendingFilterPasses = 6;", code)
        self.assertIn("if (pendingFilterPasses > 0 && now >= nextFilterPassAt)", code)
        update = code.split("private void UpdateNativeArchipelagoIntegration()", 1)[1]
        update = update.split("private void DrawArchipelagoIntroCredit()", 1)[0]
        self.assertNotIn("RefreshNativeSeedMenu();\n        }", update)
        self.assertIn('System.Diagnostics.Stopwatch.StartNew()', code)
        self.assertIn("[AP UI PERF]", code)
        self.assertIn('InstallNativeMenuLifecycleHooks();', hooks)
        self.assertIn('ObserveAnyMenuOpened(object __instance)', hooks)
        self.assertIn('ScheduleNativeUiFilter("native starter OpenSelf")', hooks)
        self.assertIn('ScheduleNativeUiFilter("native library OpenSelf")', hooks)

    def test_map_catalog_is_not_rewritten_for_each_card(self):
        native = NATIVE.read_text(encoding="utf-8")
        plugin = PLUGIN.read_text(encoding="utf-8")
        section = native.split("private void RegisterAvailableMap(", 1)[1]
        section = section.split("private void TryDiscoverNativeMaps(", 1)[0]
        self.assertNotIn("SaveObservedMapIds(", section)
        self.assertIn("nativeMapCatalogSaveAt = Time.realtimeSinceStartup + 1.5f;", section)
        self.assertIn("private void FlushNativeMapCatalogIfIdle()", native)
        self.assertIn("FlushNativeMapCatalogIfIdle();", plugin)

    def test_native_map_selection_binds_seed_mission_and_protects_checks(self):
        ui = UI.read_text(encoding="utf-8")
        native = NATIVE.read_text(encoding="utf-8")
        self.assertIn("else DrawArchipelagoIntroCredit();", ui)
        self.assertIn("if (apMenuOpen) DrawConnection();", ui)
        panel = (ROOT / "mod/NativeConnectionPanel.cs").read_text(encoding="utf-8")
        self.assertIn("UnityEngine.UI.InputField", panel)
        self.assertIn("NativeConnectAction", panel)
        self.assertIn("NativeDisconnectAction", panel)
        self.assertIn('SetUiProperty(passwordNativeInput, "text", "")', panel)
        self.assertIn("SelectMission(matching);", native)
        self.assertIn("startedMission = matching.id;", native)
        self.assertIn("String.Equals(MissionMapId(mission), mapId", native)
        self.assertIn("String.Equals(mission.mode, modeId", native)
        self.assertIn("return false;", native)
        self.assertIn("Blocked null native map starter", native)
        self.assertNotIn('Name.Contains("SpNewGameStarterMenu")', native)
        self.assertIn("ObserveNativeMapLabel(__0, __result as string)", native)
        self.assertIn("Instance.nativeNewGameVisible = false;", native)

    def test_intro_uses_bundled_official_logo_without_modifying_splash_assets(self):
        native = INTEGRATION.read_text(encoding="utf-8")
        self.assertIn("DrawArchipelagoIntroCredit()", native)
        self.assertIn("LoadApLogo();", native)
        self.assertIn("GUI.DrawTexture(new Rect((Screen.width - size)", native)
        self.assertIn("Texture2D.whiteTexture", native)
        self.assertIn("HasNativeOptionsSibling", native)
        self.assertIn("BeginOwnIntroCredit();", native)
        self.assertIn('Logger.LogInfo("[AP CREDITS] Dedicated', native)
        workflow = (ROOT / ".github/workflows/build.yml").read_text(encoding="utf-8")
        self.assertIn("archipelago-logo.png", workflow)

    def test_legacy_dialog_helpers_still_do_not_override_native_screen(self):
        native = NATIVE.read_text(encoding="utf-8")
        self.assertIn("seed filter retained", native)
        self.assertNotIn("Instance.RestoreNativeEntries();", native)
        self.assertIn("if (disconnectPending || String.IsNullOrEmpty(token)) return true;", native)
        ui = UI.read_text(encoding="utf-8")
        self.assertIn('bridgeConnected ? "Retour au jeu" : "Se connecter"', ui)
        self.assertIn("[AP UI EXCEPTION]", ui)


if __name__ == "__main__":
    unittest.main()
