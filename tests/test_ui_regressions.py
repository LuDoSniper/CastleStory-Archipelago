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
        self.assertIn("!AllowedSeedMap(mapId)", code)
        self.assertIn("nativeMapLabels.TryGetValue", code)
        self.assertIn("private void RestoreNativeEntries()", code)
        self.assertIn("disconnectPending || String.IsNullOrEmpty(token)", code)

    def test_native_map_selection_binds_seed_mission_and_protects_checks(self):
        ui = UI.read_text(encoding="utf-8")
        native = NATIVE.read_text(encoding="utf-8")
        self.assertIn("else DrawArchipelagoIntroCredit();", ui)
        self.assertIn("if (apMenuOpen) DrawConnection();", ui)
        self.assertIn("SelectMission(matching);", native)
        self.assertIn("startedMission = matching.id;", native)
        self.assertIn("String.Equals(MissionMapId(mission), mapId", native)
        self.assertIn("String.Equals(mission.mode, modeId", native)
        self.assertIn("return false;", native)
        self.assertIn("ObserveNativeMapLabel(__0, __result as string)", native)
        self.assertIn("Instance.nativeNewGameVisible = false;", native)

    def test_intro_uses_bundled_official_logo_without_modifying_splash_assets(self):
        native = INTEGRATION.read_text(encoding="utf-8")
        self.assertIn("DrawArchipelagoIntroCredit()", native)
        self.assertIn("LoadApLogo();", native)
        self.assertIn("GUI.DrawTexture(logo, apLogo", native)
        workflow = (ROOT / ".github/workflows/build.yml").read_text(encoding="utf-8")
        self.assertIn("archipelago-logo.png", workflow)

    def test_legacy_dialog_helpers_still_do_not_override_native_screen(self):
        native = NATIVE.read_text(encoding="utf-8")
        self.assertIn("RestoreNativeEntries();", native)
        self.assertIn("if (disconnectPending || String.IsNullOrEmpty(token)) return true;", native)
        ui = UI.read_text(encoding="utf-8")
        self.assertIn('bridgeConnected ? "Retour au jeu" : "Se connecter"', ui)
        self.assertIn("[AP UI EXCEPTION]", ui)


if __name__ == "__main__":
    unittest.main()
