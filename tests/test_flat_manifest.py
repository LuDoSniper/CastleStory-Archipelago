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
            self.assertEqual(flat["bridge_client_version"], "0.7.3")

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


    def test_wire_snapshot_supports_unicode_and_roundtrip(self):
        import base64
        with tempfile.TemporaryDirectory() as td:
            folder = Path(td)
            b = bridge.Bridge(folder, "Tester", "")
            b.token = "fresh-token"
            b.mode = "Invasion"
            b.protocol_version = 6
            b.campaign_status = "ready"
            b.campaign = {"ready": True, "mode": "Invasion", "missions": [{
                "id": "invasion_1", "mode": "Invasion",
                "map_id": "official:MapName", "map": "Île des héros",
                "wave_goal": 7, "shard_start": 0, "shard_count": 0
            }]}
            b.persist_session()
            wire = (folder / "campaign_wire.txt").read_text(encoding="ascii").splitlines()
            self.assertEqual(wire[0], "CASTLE_STORY_AP_CAMPAIGN_V1")
            self.assertEqual(wire[1], "fresh-token")
            self.assertEqual(wire[9], "1")
            fields = wire[10].split("\t")
            self.assertEqual(len(fields), 7)
            self.assertEqual(base64.b64decode(fields[3]).decode("utf-8"), "Île des héros")
            self.assertEqual(fields[4:], ["7", "0", "0"])

    def test_wire_snapshot_preserves_empty_manifest_and_token(self):
        with tempfile.TemporaryDirectory() as td:
            b = bridge.Bridge(Path(td), "Tester", "")
            b.token = "test-token"
            b.mode = "Conquest"
            b.campaign_status = "missing"
            b.campaign_message = "Campagne indisponible"
            b.persist_session()
            wire = (Path(td) / "campaign_wire.txt").read_text(encoding="ascii").splitlines()
            self.assertEqual(wire[1], "test-token")
            self.assertEqual(wire[9], "0")
            self.assertEqual(len(wire), 10)
            self.assertIn("campaign_view.json", str(Path(td) / "campaign_view.json"))

    def test_unity_decoder_enforces_token_protocol_and_mission_count(self):
        root = Path(__file__).resolve().parents[1]
        mod = (root / "mod/CastleStoryArchipelagoPlugin.cs").read_text(encoding="utf-8")
        self.assertIn('Path.Combine(directory, "campaign_wire.txt")', mod)
        self.assertIn("lines[1] != session.token", mod)
        self.assertIn("protocol != session.protocol_version", mod)
        self.assertIn("lines.Length < count + 11", mod)
        self.assertIn("ReadCampaignWire(session);", mod)


if __name__ == "__main__":
    unittest.main()
