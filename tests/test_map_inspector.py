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
                encoding="utf-8"
            )
            (folder / "LuaMarkersCount.json").write_text("{}", encoding="utf-8")
            result = maps.scan(td)
            self.assertEqual(len(result), 1)
            self.assertEqual(result[0]["name"], "MyOwnMap")
            self.assertIsNone(result[0]["verified_shards"])
            self.assertIn("LuaMarkersCount.json", result[0]["present_files"])

if __name__ == "__main__":
    unittest.main()
