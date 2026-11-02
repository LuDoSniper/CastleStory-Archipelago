from pathlib import Path
import tempfile
import unittest
from test_bridge import bridge

class LocalAutoInvasionTests(unittest.TestCase):
    def create_bridge(self, directory, seed="seed-a", slot="Player1"):
        client = bridge.Bridge(directory, slot, "")
        client.seed = seed
        client.token = bridge.token_for(seed, 1, slot)
        client.mode = "Invasion"
        client.connected = True
        client.source_campaign = {
            "ready": True, "mode": "Invasion", "missions": [{
                "id": "invasion_1", "mode": "Invasion",
                "map_id": "auto:invasion",
                "map_policy": "local_seeded_invasion", "wave_goal": 5,
                "map": "Carte Invasion aleatoire (selection locale)"
            }]
        }
        client.campaign = dict(client.source_campaign)
        return client

    def test_manifest_allows_local_auto_only_for_invasion(self):
        mission = {
            "id": "invasion_1", "mode": "Invasion",
            "map_id": "auto:invasion", "map_policy": "local_seeded_invasion"}
        good = {"ready": True, "missions": [mission]}
        self.assertIsNone(bridge.validate_campaign_manifest(good, "Invasion"))
        self.assertIsNotNone(bridge.validate_campaign_manifest(
            {"ready": True, "missions": [
                dict(mission, mode="Conquest")]}, "Conquest"))

    def test_waits_until_game_has_scanned_all_maps(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td)
            client = self.create_bridge(path)
            self.assertTrue(client.refresh_dynamic_campaign())
            self.assertEqual(client.campaign_status, "waiting_for_game")
            self.assertTrue(client.campaign["ready"])
            bridge.save_json(path / "installed_maps.json", {
                "scan_complete": False,
                "map_ids": ["official:a"], "invasion_map_ids": ["official:a"]
            })
            self.assertFalse(client.refresh_dynamic_campaign())
            self.assertEqual(client.campaign["missions"][0]["map_id"],
                             "auto:invasion")

    def test_native_catalog_picks_deterministic_map_and_pins_it(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td)
            first = self.create_bridge(path)
            choices = ["official:MapA", "official:MapB", "official:MapC"]
            bridge.save_json(path / "installed_maps.json", {
                "scan_complete": True, "map_ids": choices,
                "invasion_map_ids": choices
            })
            self.assertTrue(first.refresh_dynamic_campaign())
            self.assertEqual(first.campaign_status, "ready")
            chosen = first.campaign["missions"][0]["map_id"]
            self.assertIn(chosen, choices)
            self.assertEqual(first.campaign["missions"][0]["resolved_from"],
                             "native_invasion_catalog")
            self.assertTrue(first.refresh_dynamic_campaign() is False)
            next_client = self.create_bridge(path)
            next_client.refresh_dynamic_campaign()
            self.assertEqual(next_client.campaign["missions"][0]["map_id"], chosen)
            self.assertEqual(bridge.load_json(path / "auto_map_selection.json")["token"],
                             first.token)

    def test_stable_choice_varies_between_seed_names(self):
        choices = ["official:A", "official:B", "official:C", "official:D"]
        selections = {bridge.seeded_local_map_choice(str(i), "Player1", choices)
                      for i in range(35)}
        self.assertGreater(len(selections), 1)
        self.assertEqual(bridge.seeded_local_map_choice("one", "Player1", choices),
                         bridge.seeded_local_map_choice("one", "Player1", list(reversed(choices))))

    def test_missing_previous_map_is_error_not_reroll(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td)
            client = self.create_bridge(path)
            bridge.save_json(path / "auto_map_selection.json", {
                "token": client.token, "map_id": "official:Missing"})
            bridge.save_json(path / "installed_maps.json", {
                "scan_complete": True,
                "map_ids": ["official:Present"],
                "invasion_map_ids": ["official:Present"]
            })
            client.refresh_dynamic_campaign()
            self.assertFalse(client.campaign["ready"])
            self.assertEqual(client.campaign_status, "unready")
            self.assertIn("official:Missing", client.campaign_message)

if __name__ == "__main__":
    unittest.main()
