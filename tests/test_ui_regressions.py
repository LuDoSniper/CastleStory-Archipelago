from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class UiRegressionTests(unittest.TestCase):
    def test_campaign_back_closes_both_popups(self):
        ui = (ROOT / "mod/ArchipelagoMenu.cs").read_text(encoding="utf-8")
        self.assertIn("private void ExitCampaignToMainMenu()", ui)
        segment = ui.split("private void ExitCampaignToMainMenu()", 1)[1].split("private void DrawCampaign()",1)[0]
        self.assertIn("campaignPreviewOpen = false;", segment)
        self.assertIn("apMenuOpen = false;", segment)
        self.assertIn("CloseNativeNewGame();", segment)
        self.assertIn("ExitCampaignToMainMenu();", ui)

    def test_ui_displays_both_server_manifest_and_bridge_details(self):
        code = (ROOT / "mod/ArchipelagoMenu.cs").read_text(encoding="utf-8")
        self.assertIn("Manifeste transmis :", code)
        self.assertIn("Champs du serveur :", code)
        self.assertIn("slotProtocolVersion", code)

if __name__ == "__main__":
    unittest.main()
