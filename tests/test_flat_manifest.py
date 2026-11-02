from pathlib import Path
import tempfile
import unittest

from test_bridge import bridge


class FlatManifestContractTests(unittest.TestCase):
    def test_bridge_writes_flat_missions_alongside_session(self):
        with tempfile.TemporaryDirectory() as td:
            folder = Path(td)
            b = bridge.Bridge(folder, "Tester", "")
            b.seed = "testseed"
            b.token = "correct-seed-token"
            b.mode = "Invasion"
            b.connected = True
            b.protocol_version = 6
            b.manifest_provided = True
            b.slot_data_provided = True
            b.campaign_status = "ready"
            b.campaign = {
                "ready": True, "mode": "Invasion", "missions": [{
                    "id": "invasion_1", "mode": "Invasion",
                    "map_id": "official:KnownMap", "map": "Localized name",
                    "wave_goal": 5
                }]
            }
            b.persist_session()
            flat = bridge.load_json(folder / "campaign_view.json")
            session = bridge.load_json(folder / "session.json")
            self.assertEqual(flat["token"], session["token"])
            self.assertTrue(flat["manifest_provided"])
            self.assertEqual(flat["mission_count"], 1)
            self.assertEqual(session["campaign_mission_count"], 1)
            self.assertEqual(flat["missions"][0]["map_id"], "official:KnownMap")
            self.assertEqual(flat["bridge_client_version"], "0.6.6")

    def test_invalid_campaign_gets_explicit_zero_missions(self):
        with tempfile.TemporaryDirectory() as td:
            folder = Path(td)
            b = bridge.Bridge(folder, "Tester", "")
            b.token = "token"
            b.mode = "Invasion"
            b.campaign = {}
            b.campaign_status = "missing"
            b.campaign_message = "Manifest absent"
            b.persist_session()
            view = bridge.load_json(folder / "campaign_view.json")
            self.assertEqual(view["missions"], [])
            self.assertEqual(view["mission_count"], 0)
            self.assertFalse(view["ready"])
            self.assertEqual(view["campaign_message"], "Manifest absent")

    def test_unity_flat_fallback_is_token_scoped_and_reports_count(self):
        root = Path(__file__).resolve().parents[1]
        mod = (root / "mod/CastleStoryArchipelagoPlugin.cs").read_text(encoding="utf-8")
        ui = (root / "mod/ArchipelagoMenu.cs").read_text(encoding="utf-8")
        self.assertIn('Path.Combine(directory, "campaign_view.json")', mod)
        self.assertIn("view.token != session.token", mod)
        self.assertIn("view.missions.Length != view.mission_count", mod)
        self.assertIn("campaign = new CampaignFile", mod)
        self.assertIn("Missions côté bridge :", ui)
        self.assertIn("Missions côté Unity :", ui)


if __name__ == "__main__":
    unittest.main()
