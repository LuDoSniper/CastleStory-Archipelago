from pathlib import Path
import tempfile
import unittest
from test_bridge import bridge

class LocalMapChecks(unittest.TestCase):
    def test_no_map_folder_is_unknown_not_false_negative(self):
        with tempfile.TemporaryDirectory() as td:
            directory = Path(td) / "BepInEx/config/castle_story_archipelago"
            directory.mkdir(parents=True)
            self.assertIsNone(bridge.detect_installed_maps(directory))
            self.assertEqual(bridge.reconcile_local_maps(directory, {"ready": True,
                "missions": [{"map": "Map"}]} )["ready"], True)

    def test_known_catalog_blocks_missing_seed_maps(self):
        with tempfile.TemporaryDirectory() as td:
            game = Path(td)
            (game / "Info/Maps/Existing").mkdir(parents=True)
            directory = game / "BepInEx/config/castle_story_archipelago"
            directory.mkdir(parents=True)
            campaign = {"ready": True, "missions": [
                {"map": "Existing"}, {"map": "Unavailable"}]}
            result = bridge.reconcile_local_maps(directory, campaign)
            self.assertFalse(result["ready"])
            self.assertIn("Unavailable", result["warning"])

if __name__ == "__main__":
    unittest.main()
