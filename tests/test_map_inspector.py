from pathlib import Path
import importlib.util
import json
import tempfile
import unittest

path = Path(__file__).resolve().parents[1] / "tools/inspect_maps.py"
spec = importlib.util.spec_from_file_location("inspect_maps", path)
maps = importlib.util.module_from_spec(spec)
spec.loader.exec_module(maps)

class MapInspectorTests(unittest.TestCase):
    def test_inspect_never_invents_shard_counts(self):
        with tempfile.TemporaryDirectory() as td:
            folder = Path(td) / "Info/Maps/MyOwnMap"
            folder.mkdir(parents=True)
            (folder / "meta.json").write_text(
                json.dumps({"gamemodes": ["conquest"], "name": "MyOwnMap"}),
                encoding="utf-8")
            (folder / "LuaMarkersCount.json").write_text(
                json.dumps({"CrystalMarkers": {"Shard": 4, "HomeCrystal": 2}}),
                encoding="utf-8")
            results = maps.scan(td)
            self.assertEqual(len(results), 1)
            self.assertIsNone(results[0]["verified_shards"])
            self.assertEqual(len(results[0]["marker_evidence"]), 2)
            self.assertEqual(maps.validate_approvals(results, {"official:MyOwnMap": 4}),
                             {"official:MyOwnMap": 4})
            self.assertIn('"official:MyOwnMap": 4', maps.yaml_fragment({"official:MyOwnMap": 4}))

    def test_cannot_approve_missing_or_invalid_maps(self):
        results = [{"name": "Test", "present_files": ["meta.json"]}]
        with self.assertRaises(ValueError):
            maps.validate_approvals(results, {"Test": 5})
        with self.assertRaises(ValueError):
            maps.validate_approvals(results, {"Missing": 2})
        with self.assertRaises(ValueError):
            maps.validate_approvals(results, {"Test": True})

    def test_unity_catalog_can_cover_embedded_official_assets(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            catalog = root / "BepInEx/config/castle_story_archipelago/installed_maps.json"
            catalog.parent.mkdir(parents=True)
            catalog.write_text(json.dumps({
                "map_ids": ["official:Map123", "workshop:Steam456"]
            }), encoding="utf-8")
            entries = maps.scan(root)
            self.assertEqual({e["name"] for e in entries},
                             {"official:Map123", "workshop:Steam456"})
            self.assertTrue(all(e.get("verified_in_unity") for e in entries))
            self.assertEqual(maps.validate_approvals(entries, {"official:Map123": 5}),
                             {"official:Map123": 5})
            yaml = maps.yaml_fragment({"official:Map123": 5},
                                      ["official:Map123", "workshop:Steam456"])
            self.assertIn('invasion_map_pool:', yaml)
            self.assertIn('workshop:Steam456', yaml)
            self.assertIn('"official:Map123": 5', yaml)

    def test_scans_workshop_ugc_maps(self):
        with tempfile.TemporaryDirectory() as td:
            folder = Path(td) / "Info/UGC/Maps/Workshop_123"
            folder.mkdir(parents=True)
            result = maps.scan(td)
            self.assertEqual(result[0]["source"], "Info/UGC/Maps")
            self.assertEqual(result[0]["name"], "workshop:Workshop_123")

if __name__ == "__main__":
    unittest.main()
