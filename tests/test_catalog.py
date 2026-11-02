import importlib.util
from pathlib import Path
import unittest

path = Path(__file__).resolve().parents[1] / "apworld/castle_story/catalog.py"
spec = importlib.util.spec_from_file_location("catalog", path)
catalog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)

class CatalogTests(unittest.TestCase):
    def test_id_stability(self):
        self.assertEqual(catalog.LOCATION_NAME_TO_ID["Invasion - Survive Wave 10"], 276810010)
        self.assertEqual(catalog.LOCATION_NAME_TO_ID["Conquest - Capture Shard 3"], 276812003)
        self.assertEqual(catalog.LOCATION_NAME_TO_ID["Conquest - Victory"], 276812100)

    def test_stable_unique_ids(self):
        self.assertEqual(len(catalog.INVASION_LOCATIONS), 10)
        self.assertEqual(len(catalog.CONQUEST_LOCATIONS), 31)
        self.assertEqual(len(set(catalog.LOCATION_NAME_TO_ID.values())), 41)
        self.assertFalse(set(catalog.ITEM_NAME_TO_ID.values()) &
                         set(catalog.LOCATION_NAME_TO_ID.values()))
        self.assertEqual(len(set(catalog.ITEM_NAME_TO_ID.values())), 12)

if __name__ == "__main__":
    unittest.main()
