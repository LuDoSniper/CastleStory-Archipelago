from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class UiRegressionTests(unittest.TestCase):
    def test_close_campaign_popup_does_not_navigate_native_menu(self):
        ui = (ROOT / "mod/ArchipelagoMenu.cs").read_text(encoding="utf-8")
        self.assertIn("private void CloseCampaignPopup()", ui)
        segment = ui.split("private void CloseCampaignPopup()", 1)[1].split("private void DrawCampaign()",1)[0]
        self.assertIn("campaignPreviewOpen = false;", segment)
        self.assertIn("apMenuOpen = false;", segment)
        self.assertIn("nativeNewGameVisible = false;", segment)
        self.assertIn("apPopupDismissedOnNativeScreen = true;", segment)
        self.assertNotIn("CloseNativeNewGame(", segment)
        self.assertIn("CloseCampaignPopup();", ui)
        self.assertIn('"Fermer", subtleButton', ui)
        native = (ROOT / "mod/NativeMenuHooks.cs").read_text(encoding="utf-8")
        self.assertIn("!Instance.apPopupDismissedOnNativeScreen", native)
        self.assertIn("Instance.apPopupDismissedOnNativeScreen = false;", native)

    def test_modal_blocks_native_ui_without_consuming_its_own_clicks(self):
        blocker = (ROOT / "mod/NativeInputShield.cs").read_text(encoding="utf-8")
        self.assertIn("GraphicRaycaster", blocker)
        self.assertIn("raycastTarget", blocker)
        self.assertIn("ScreenSpaceOverlay", blocker)
        self.assertIn("nativeInputShield.SetActive(modal)", blocker)
        self.assertIn("DestroyNativeInputShield()", (
            ROOT / "mod/CastleStoryArchipelagoPlugin.cs").read_text(encoding="utf-8"))
        ui = (ROOT / "mod/ArchipelagoMenu.cs").read_text(encoding="utf-8")
        self.assertNotIn("Event.current.Use();\n            }\n            else if (apMenuOpen)", ui)
        self.assertIn('"Fermer", subtleButton', ui)

    def test_ui_displays_both_server_manifest_and_bridge_details(self):
        code = (ROOT / "mod/ArchipelagoMenu.cs").read_text(encoding="utf-8")
        self.assertIn("Manifeste transmis :", code)
        self.assertIn("Champs du serveur :", code)
        self.assertIn("slotProtocolVersion", code)

if __name__ == "__main__":
    unittest.main()
