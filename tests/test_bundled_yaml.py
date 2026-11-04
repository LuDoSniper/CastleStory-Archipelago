from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class BundledYamlTests(unittest.TestCase):
    def test_default_yaml_does_not_claim_hybrid_without_shard_capacities(self):
        default = (ROOT / "examples/CastleStoryPlayer.yaml").read_text(encoding="utf-8")
        hybrid = (ROOT / "examples/CastleStoryHybrid.template.yaml").read_text(encoding="utf-8")
        self.assertIn("game_mode: invasion", default)
        self.assertIn("invasion_map_pool: []", default)
        self.assertIn("gameplay_rewards: true", default)
        self.assertIn("gameplay_rewards: true", hybrid)
        self.assertIn("game_mode: hybrid", hybrid)
        self.assertIn("NOT GENERATABLE", hybrid)
        self.assertIn("conquest_map_capacities: {}", hybrid)

if __name__ == "__main__":
    unittest.main()
