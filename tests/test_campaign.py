import importlib.util
from pathlib import Path
import unittest

root = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("campaign", root / "apworld/castle_story/campaign.py")
campaign = importlib.util.module_from_spec(spec)
spec.loader.exec_module(campaign)

class CampaignPlanningTests(unittest.TestCase):
    def test_combine_maps_minimum_overage(self):
        capacities = {"A": 3, "B": 5, "C": 6}
        selected = campaign.choose_conquest_maps(capacities, 8, 123)
        self.assertEqual(sum(x[1] for x in selected), 8)
        self.assertEqual({x[0] for x in selected}, {"A", "B"})

    def test_minimize_map_count_after_overage(self):
        selected = campaign.choose_conquest_maps({"A": 4, "B": 4, "C": 8}, 8, 2)
        self.assertEqual(len(selected), 1)
        self.assertEqual(selected[0], ("C", 8))

    def test_oversized_goal_rejected(self):
        with self.assertRaises(campaign.CampaignError):
            campaign.choose_conquest_maps({"A": 3}, 5, 1)

    def test_invalid_capacities_rejected(self):
        with self.assertRaises(campaign.CampaignError):
            campaign.choose_conquest_maps({"A": True}, 1, 1)

    def test_seed_deterministic(self):
        caps = {"A": 3, "B": 4, "C": 5, "D": 6}
        self.assertEqual(campaign.choose_conquest_maps(caps, 7, 88),
                         campaign.choose_conquest_maps(caps, 7, 88))

    def test_hybrid_always_both_types(self):
        result = campaign.plan_campaign(
            "Hybrid", 5, 7, {"A": 3, "B": 4}, {"Pioneer Pass"}, 14)
        self.assertTrue(result["ready"])
        self.assertEqual([m["mode"] for m in result["missions"]],
                         ["Invasion", "Conquest", "Conquest"])
        self.assertEqual([m["shard_start"] for m in result["missions"][1:]], [1, 4])

    def test_no_unverified_maps_means_unready_not_fabricated(self):
        result = campaign.plan_campaign(
            "Conquest", 5, 7, {}, set(), 14)
        self.assertFalse(result["ready"])
        self.assertEqual(result["missions"], [])
        self.assertIn("Not enough verified", result["warning"])

if __name__ == "__main__":
    unittest.main()
