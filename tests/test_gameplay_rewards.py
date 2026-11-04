"""Gameplay v1: real item grants, seed rules, Conquest checks and old-slot isolation."""
import ast
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest

from test_bridge import bridge, FakeWS

ROOT = Path(__file__).resolve().parents[1]
WORLD = ROOT / "apworld/castle_story/__init__.py"
GAMEPLAY = ROOT / "mod/BonusRewards.cs"
PLUGIN = ROOT / "mod/CastleStoryArchipelagoPlugin.cs"
SCENE = ROOT / "mod/NativeArchipelagoIntegration.cs"

campaign_spec = importlib.util.spec_from_file_location(
    "gameplay_campaign", ROOT / "apworld/castle_story/campaign.py")
campaign = importlib.util.module_from_spec(campaign_spec)
campaign_spec.loader.exec_module(campaign)


def reward_pool_for(seed, player, count, enabled=True):
    # Evaluate the actual APWorld method with a lightweight fake World object;
    # the Archipelago server dependencies are not installed in Python CI.
    tree = ast.parse(WORLD.read_text(encoding="utf-8"))
    world_class = next(x for x in tree.body if isinstance(x, ast.ClassDef)
                       and x.name == "CastleStoryWorld")
    method = next(x for x in world_class.body if isinstance(x, ast.FunctionDef)
                  and x.name == "_reward_pool")
    standalone = ast.fix_missing_locations(
        ast.Module(body=[method], type_ignores=[]))
    scope = {"seed_rng": campaign.seed_rng}
    exec(compile(standalone, str(WORLD), "exec"), scope)
    world = SimpleNamespace(
        player=player, multiworld=SimpleNamespace(seed=seed),
        options=SimpleNamespace(
            gameplay_rewards=SimpleNamespace(value=int(enabled))),
        _active_names=lambda: ["loc"] * count)
    return scope["_reward_pool"](world)


class GameplayWorldTests(unittest.TestCase):
    def test_rewards_are_deterministic_playable_and_match_location_count(self):
        for count in (1, 3, 5, 8, 10, 31):
            first = reward_pool_for(98765, 1, count)
            self.assertEqual(first, reward_pool_for(98765, 1, count))
            self.assertEqual(len(first), count)
            self.assertNotIn("Wood Supply", first)
            self.assertNotIn("Stone Supply", first)
            self.assertNotIn("Catapult Unlock", first)
            self.assertIn("Equipped Worker", first)
            for unlock, gift in (
                ("Archer Unlock", "Equipped Archer"),
                ("Knight Unlock", "Equipped Knight"),
                ("Alchemist Unlock", "Equipped Alchemist"),
            ):
                if unlock in first:
                    self.assertIn(gift, first)

    def test_longer_campaigns_vary_repeatable_reward_selection_by_seed(self):
        pools = {tuple(reward_pool_for(seed, 1, 22)) for seed in range(15)}
        self.assertGreater(len(pools), 1)
        self.assertIn("Archer Unlock", reward_pool_for(88, 1, 8))
        self.assertIn("Equipped Archer", reward_pool_for(88, 1, 8))

    def test_apworld_explicitly_opt_in_and_preserves_location_ids(self):
        text = WORLD.read_text(encoding="utf-8")
        options = (ROOT / "apworld/castle_story/options.py").read_text("utf-8")
        self.assertIn('"gameplay_rules": {', text)
        self.assertIn('"native_unit_rewards": bool(self.options.gameplay_rewards.value)', text)
        self.assertIn('class GameplayRewards(Toggle):', options)
        self.assertIn('default = 1', options)
        self.assertIn('gameplay_rewards: GameplayRewards', options)


class GameplayBridgeTests(unittest.IsolatedAsyncioTestCase):
    async def connect(self, directory, rules=None):
        b = bridge.Bridge(Path(directory), "Player", "")
        ws = FakeWS()
        await b.on_message(ws, {"cmd": "RoomInfo", "seed_name": "gameplay-seed"})
        slot_data = {"game_mode": "Invasion", "protocol_version": 6}
        if rules is not None:
            slot_data["gameplay_rules"] = rules
        await b.on_message(ws, {"cmd": "Connected", "slot": 2, "team": 0,
                                "slot_data": slot_data})
        return b, ws, bridge.load_json(Path(directory) / "session.json")

    async def test_new_seed_delivers_explicit_gameplay_rules(self):
        with tempfile.TemporaryDirectory() as tmp:
            b, ws, data = await self.connect(tmp, {
                "version": 1, "native_unit_rewards": True,
                "unlock_required_for_equipped_units": True
            })
            self.assertEqual(data["gameplay_protocol"], 1)
            self.assertTrue(data["gameplay_unit_rewards"])
            self.assertTrue(data["gameplay_unlocks_required"])
            await b.on_message(ws, {"cmd": "ReceivedItems", "index": 0,
                "items": [{"item": bridge.ITEM_BASE + 8, "player": 4},
                          {"item": bridge.ITEM_BASE + 9, "player": 2}]})
            items = bridge.load_json(Path(tmp) / "items.json")["items"]
            self.assertEqual([(x["index"], x["item"]) for x in items],
                             [(0, bridge.ITEM_BASE + 8),
                              (1, bridge.ITEM_BASE + 9)])
            await b.on_message(ws, {"cmd": "ReceivedItems", "index": 0,
                "items": [{"item": bridge.ITEM_BASE + 8, "player": 4},
                          {"item": bridge.ITEM_BASE + 9, "player": 2}]})
            self.assertEqual(len(bridge.load_json(Path(tmp) / "items.json")["items"]), 2)

    async def test_old_slots_and_unknown_gameplay_versions_are_safe(self):
        for rules in (None, {"version": 99, "native_unit_rewards": True},
                      {"version": 1, "native_unit_rewards": "true"}):
            with self.subTest(rules=rules), tempfile.TemporaryDirectory() as tmp:
                _, _, data = await self.connect(tmp, rules)
                self.assertFalse(data["gameplay_unit_rewards"])
                self.assertFalse(data["gameplay_unlocks_required"])


class NativeGameplaySafetyTests(unittest.TestCase):
    def test_new_seed_only_native_rewards_and_disposable_spawn_template(self):
        source = GAMEPLAY.read_text(encoding="utf-8")
        scene = SCENE.read_text(encoding="utf-8")
        self.assertIn("gameplayUnitRewardsEnabled", source)
        self.assertIn("DisableSeedUnitRewards", source)
        self.assertIn("!IsSeedGameplayActive()", source)
        self.assertIn("BonusUnlockReady(x.item)", source)
        self.assertIn('item == ItemBase + 4', source)
        self.assertIn("lastBonusSpawn ?? result as GameObject", source)
        self.assertIn('ticket.state = "spawning"', source)
        self.assertIn('ticket.state = "review"', source)
        self.assertIn('ticket.state = "delivered"', source)
        self.assertIn("WriteCheckLedgerFile(path,", source)
        self.assertIn("if (!SaveBonusRewards())", source)
        self.assertIn("(!nativeSpawnMethod.IsStatic && nativeSpawnHost == null)", source)
        self.assertIn("Reward journal unavailable; no unit spawned.", source)
        self.assertNotIn("if (File.Exists(path)) File.Delete(path);", source)
        self.assertIn("ClearNativeBonusSceneTemplate();", scene)

    def test_conquest_checks_are_seed_automatic_and_accept_offline_ledger(self):
        mod = PLUGIN.read_text(encoding="utf-8")
        self.assertIn("private bool IsActiveConquestMission()", mod)
        self.assertIn("!IsActiveConquestMission()", mod)
        self.assertIn("if (!campaignVictory && (!victoryChecks.Value || !bridgeConnected))", mod)
        self.assertIn("!IsActiveConquestMission()) return;", mod)
        self.assertIn("IsActiveInvasionMission() &&", mod)
        self.assertIn("IsActiveConquestMission())) return;", mod)
        self.assertIn("if (checksDirty && (bridgeConnected || IsActiveInvasionMission() ||", mod)


if __name__ == "__main__":
    unittest.main()
