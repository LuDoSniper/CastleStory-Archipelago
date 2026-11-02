import importlib.util
from pathlib import Path
import unittest

root = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("campaign", root / "apworld/castle_story/campaign.py")
campaign = importlib.util.module_from_spec(spec)
spec.loader.exec_module(campaign)

class CampaignPlanningTests(unittest.TestCase):
    def test_combine_maps_minimum_overage(self):
        capacities = {"official:A": 3, "official:B": 5, "official:C": 6}
        selected = campaign.choose_conquest_maps(capacities, 8, 123)
        self.assertEqual(sum(x[1] for x in selected), 8)
        self.assertEqual({x[0] for x in selected}, {"official:A", "official:B"})

    def test_minimize_map_count_after_overage(self):
        selected = campaign.choose_conquest_maps({"official:A": 4, "official:B": 4, "official:C": 8}, 8, 2)
        self.assertEqual(len(selected), 1)
        self.assertEqual(selected[0], ("official:C", 8))

    def test_oversized_goal_rejected(self):
        with self.assertRaises(campaign.CampaignError):
            campaign.choose_conquest_maps({"official:A": 3}, 5, 1)

    def test_invalid_capacities_rejected(self):
        with self.assertRaises(campaign.CampaignError):
            campaign.choose_conquest_maps({"official:A": True}, 1, 1)

    def test_seed_deterministic(self):
        caps = {"official:A": 3, "official:B": 4, "official:C": 5, "official:D": 6}
        self.assertEqual(campaign.choose_conquest_maps(caps, 7, 88),
                         campaign.choose_conquest_maps(caps, 7, 88))

    def test_hybrid_always_both_types(self):
        result = campaign.plan_campaign(
            "Hybrid", 5, 7, {"official:A": 3, "official:B": 4}, {"official:PioneerPassFolder"}, 14)
        self.assertTrue(result["ready"])
        self.assertEqual([m["mode"] for m in result["missions"]],
                         ["Invasion", "Conquest", "Conquest"])
        missions = result["missions"][1:]
        self.assertEqual(missions[0]["shard_start"], 1)
        self.assertEqual(missions[1]["shard_start"],
                         missions[0]["shard_start"] + {"official:A": 3, "official:B": 4}[missions[0]["map"]])
        self.assertEqual(sum({"official:A": 3, "official:B": 4}[m["map"]] for m in missions), 7)

    def test_hybrid_without_conquest_pool_is_explicitly_unready(self):
        result = campaign.plan_campaign(
            "Hybrid", 5, 8, {}, {"stable-map-a", "stable-map-b"}, 17)
        self.assertFalse(result["ready"])
        self.assertEqual(len(result["missions"]), 1)
        self.assertIn("Conquest", result["warning"])
        self.assertIn("Hybrid requires", result["warning"])

    def test_empty_invasion_pool_never_forces_pioneer_pass(self):
        result = campaign.plan_campaign("Invasion", 5, 3, {}, set(), 2)
        self.assertFalse(result["ready"])
        self.assertEqual(result["missions"], [])
        self.assertIn("No Invasion", result["warning"])

    def test_invasion_seed_varies_among_configured_ids(self):
        pool = {"stable-1", "stable-2", "stable-3", "stable-4"}
        samples = [campaign.plan_campaign("Invasion", 5, 3, {}, pool, seed)
                   ["missions"][0]["map_id"] for seed in range(24)]
        self.assertGreater(len(set(samples)), 1)
        self.assertTrue(set(samples).issubset(pool))
        self.assertTrue(all(campaign.plan_campaign(
            "Invasion", 5, 3, {}, pool, seed)["missions"][0]["map_id"] == samples[seed]
                             for seed in range(24)))

    def test_french_display_never_replaces_technical_identifier(self):
        result = campaign.plan_campaign(
            "Hybrid", 2, 1, {"pioneer_pass_asset": 1},
            {"pioneer_pass_asset", "other_invasion_asset"}, 42)
        self.assertTrue(result["ready"])
        self.assertEqual(result["missions"][1]["map_id"], "pioneer_pass_asset")
        self.assertTrue(all("map_id" in m for m in result["missions"]))

    def test_no_unverified_maps_means_unready_not_fabricated(self):
        result = campaign.plan_campaign(
            "Conquest", 5, 7, {}, set(), 14)
        self.assertFalse(result["ready"])
        self.assertEqual(result["missions"], [])
        self.assertIn("Not enough verified", result["warning"])

    def test_hybrid_missing_conquest_exposes_missing_branch(self):
        result = campaign.plan_campaign("Hybrid", 5, 3, {},
                                        {"official:MapFolder"}, 14)
        self.assertFalse(result["ready"])
        self.assertEqual(result["missing_modes"], ["Conquest"])
        self.assertIn("Hybrid requires at least one", result["warning"])

    def test_reject_translated_or_english_display_names(self):
        with self.assertRaises(campaign.CampaignError):
            campaign.normalize_map_id("Pioneer Pass")
        with self.assertRaises(campaign.CampaignError):
            campaign.normalize_map_id("Passage du pionnier")
        self.assertEqual(campaign.normalize_map_id("official:MapFolder"),
                         "official:MapFolder")


if __name__ == "__main__":
    unittest.main()
