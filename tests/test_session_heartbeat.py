from pathlib import Path
import time
import unittest
from test_bridge import bridge

class HeartbeatTests(unittest.TestCase):
    def test_session_json_contains_fresh_timestamp_and_version(self):
        import tempfile
        with tempfile.TemporaryDirectory() as td:
            instance = bridge.Bridge(Path(td), "Tester", "")
            instance.seed = "test-seed"
            instance.token = "test-token"
            instance.mode = "Invasion"
            instance.connected = True
            instance.persist_session()
            record = bridge.load_json(Path(td) / "session.json")
            self.assertEqual(record["bridge_client_version"], "0.6.5")
            self.assertTrue(record["connected"])
            self.assertLess(abs(time.time() - record["updated_at"]), 3)

    def test_unity_only_accepts_recent_heartbeat(self):
        code = (Path(__file__).resolve().parents[1] /
                "mod/CastleStoryArchipelagoPlugin.cs").read_text(encoding="utf-8")
        self.assertIn("session.updated_at > 0", code)
        self.assertIn("heartbeatAge < 8", code)
        self.assertIn("session.connected && freshHeartbeat", code)

    def test_popup_close_has_input_update_fallback(self):
        root = Path(__file__).resolve().parents[1]
        ui = (root / "mod/ArchipelagoMenu.cs").read_text(encoding="utf-8")
        mod = (root / "mod/CastleStoryArchipelagoPlugin.cs").read_text(encoding="utf-8")
        shield = (root / "mod/NativeInputShield.cs").read_text(encoding="utf-8")
        self.assertIn("Input.GetMouseButtonDown(0)", ui)
        self.assertIn("close.Contains(pos)", ui)
        self.assertIn("CloseCampaignPopup();", ui)
        self.assertIn("HandleModalRawInput();", mod)
        self.assertIn("BlockNativeEventSystems()", shield)
        self.assertIn("RestoreNativeEventSystems()", shield)
        self.assertIn("KeepInputShieldUntilMouseReleased()", ui)

if __name__ == "__main__":
    unittest.main()
