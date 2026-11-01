import importlib.util
from pathlib import Path
import unittest

path = Path(__file__).resolve().parents[1]/"apworld/castle_story/catalog.py"
spec = importlib.util.spec_from_file_location("catalog",path)
catalog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)

class CatalogTests(unittest.TestCase):
    def test_pool(self):
        self.assertEqual(len(catalog.ITEM_POOL),10)
        self.assertEqual(len(catalog.LOCATION_NAME_TO_ID),10)
        self.assertTrue(set(catalog.ITEM_POOL).issubset(catalog.ITEM_NAME_TO_ID))
    def test_unique_ids(self):
        self.assertEqual(len(set(catalog.ITEM_NAME_TO_ID.values())),6)
        self.assertEqual(len(set(catalog.LOCATION_NAME_TO_ID.values())),10)
        self.assertFalse(set(catalog.ITEM_NAME_TO_ID.values()) & set(catalog.LOCATION_NAME_TO_ID.values()))
    def test_stability(self):
        self.assertEqual(catalog.LOCATION_NAME_TO_ID["Invasion - Survive Wave 10"],276810010)
if __name__=="__main__":
    unittest.main()
