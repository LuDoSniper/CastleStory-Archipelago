import importlib.util
from pathlib import Path
import unittest

path = Path(__file__).resolve().parents[1] / "apworld/castle_story/catalog.py"
spec = importlib.util.spec_from_file_location("catalog", path)
catalog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)

class CatalogTests(unittest.TestCase):
    def test_mode_pools_match_location_counts(self):
        self.assertEqual(len(catalog.INVASION_POOL), len(catalog.INVASION_LOCATIONS))
        self.assertEqual(len(catalog.CONQUEST_POOL), len(catalog.CONQUEST_LOCATIONS))
        self.assertEqual(len(catalog.LOCATION_NAME_TO_ID), 14)

    def test_ids_are_unique_stable(self):
        items = set(catalog.ITEM_NAME_TO_ID.values())
        locations = set(catalog.LOCATION_NAME_TO_ID.values())
        self.assertEqual(len(items), 6)
        self.assertEqual(len(locations), 14)
        self.assertFalse(items & locations)
        self.assertEqual(catalog.INVASION_LOCATIONS["Invasion - Survive Wave 10"], 276810010)
        self.assertEqual(catalog.CONQUEST_LOCATIONS["Conquest - Victory"], 276812100)

if __name__ == "__main__":
    unittest.main()
