"""Castle Story Archipelago world: mode-aware campaign locations and seed manifest."""
from BaseClasses import Item, ItemClassification, Location, Region, Tutorial
from worlds.AutoWorld import WebWorld, World
from .catalog import (
    GAME, GOAL_ITEM_NAME, ITEM_NAME_TO_ID, LOCATION_NAME_TO_ID,
    INVASION_LOCATIONS, CONQUEST_LOCATIONS, PROGRESSION_ITEMS, USEFUL_ITEMS,
)
from .campaign import plan_campaign, seed_rng
from .options import CastleStoryOptions

class CastleStoryItem(Item):
    game = GAME

class CastleStoryLocation(Location):
    game = GAME

class CastleStoryWeb(WebWorld):
    game = GAME
    theme = "stone"
    tutorials = [Tutorial("Setup", "Castle Story campaign setup",
                          "English", "setup_en.md", "setup/en", ["Castle Story AP"])]

class CastleStoryWorld(World):
    game = GAME
    web = CastleStoryWeb()
    options_dataclass = CastleStoryOptions
    item_name_to_id = ITEM_NAME_TO_ID
    location_name_to_id = LOCATION_NAME_TO_ID

    def _mode(self):
        return ("Invasion", "Conquest", "Hybrid")[self.options.game_mode.value]

    def _active_names(self):
        wave_goal = self.options.invasion_goal.value
        shard_goal = self.options.conquest_shards.value
        mode = self._mode()
        names = []
        if mode in ("Invasion", "Hybrid"):
            names.extend(f"Invasion - Survive Wave {n}" for n in range(1, wave_goal + 1))
        if mode in ("Conquest", "Hybrid"):
            names.extend(f"Conquest - Capture Shard {n}" for n in range(1, shard_goal + 1))
            names.append("Conquest - Victory")
        return names

    def generate_early(self):
        self.campaign_plan = plan_campaign(
            self._mode(), self.options.invasion_goal.value,
            self.options.conquest_shards.value,
            dict(self.options.conquest_map_capacities.value),
            self.options.invasion_map_pool.value, self.multiworld.seed,
        )
        if not self.campaign_plan["ready"]:
            raise ValueError(
                "Castle Story player %s: cannot generate a %s campaign: %s "
                "Configure installed technical map IDs and verified Conquest "
                "shard counts before generating a new seed." % (
                    self.player, self._mode(), self.campaign_plan["warning"]))

    def create_regions(self):
        menu = Region("Menu", self.player, self.multiworld)
        campaign = Region("Campaign", self.player, self.multiworld)
        self.multiworld.regions.extend((menu, campaign))
        menu.connect(campaign)
        self.active_names = self._active_names()
        for name in self.active_names:
            campaign.locations.append(CastleStoryLocation(
                self.player, name, LOCATION_NAME_TO_ID[name], campaign))
        if not hasattr(self, "campaign_plan"):
            self.generate_early()
        # Event kept for compatibility with the existing Archipelago world. In-game
        # victory is exclusively communicated via StatusUpdate from the client.
        event = CastleStoryLocation(self.player, "Victory event", None, campaign)
        event.place_locked_item(CastleStoryItem(
            GOAL_ITEM_NAME, ItemClassification.progression, None, self.player))
        campaign.locations.append(event)

    def create_item(self, name):
        classification = (
            ItemClassification.progression if name in PROGRESSION_ITEMS else
            ItemClassification.useful if name in USEFUL_ITEMS else
            ItemClassification.filler
        )
        return CastleStoryItem(name, classification, ITEM_NAME_TO_ID[name], self.player)

    def _reward_pool(self):
        """Playable seed rewards rather than inert placeholders.

        Unit rewards use a verified native SpawnUnit template. An equipped
        advanced Bricktron waits for its corresponding unlock; a basic worker
        or reinforcement is immediately usable. Other catalog IDs remain
        reserved for future safely verified resource/building APIs.
        """
        count = len(self._active_names())
        if not self.options.gameplay_rewards.value:
            return (["Bricktron Reinforcement", "Equipped Worker",
                     "Equipped Archer", "Equipped Knight", "Equipped Alchemist",
                     "Archer Unlock", "Knight Unlock", "Alchemist Unlock"] *
                    ((count + 7) // 8))[:count]
        # Unlocks must always be accompanied by an equipped-unit reward:
        # e.g. a 4-check seed must not contain an unusable Knight Unlock
        # without a possible Knight gift in its entire item pool.
        pool = ["Equipped Worker"]
        for bundle in (
            ("Archer Unlock", "Equipped Archer"),
            ("Knight Unlock", "Equipped Knight"),
            ("Alchemist Unlock", "Equipped Alchemist"),
        ):
            if count - len(pool) < 2:
                break
            pool.extend(bundle)
        if len(pool) < count:
            pool.append("Bricktron Reinforcement")
        rng = seed_rng(self.multiworld.seed, "gameplay-rewards-player-%s" % self.player)
        repeatable = ["Bricktron Reinforcement", "Equipped Worker",
                      "Equipped Worker", "Equipped Archer", "Equipped Knight",
                      "Equipped Alchemist"]
        while len(pool) < count:
            pool.append(rng.choice(repeatable))
        return pool

    def create_items(self):
        # The AP server randomizes item placement; all items in the bag have a
        # functioning native unit reward or a concrete prerequisite for one.
        self.multiworld.itempool.extend(
            self.create_item(name) for name in self._reward_pool())


    def get_filler_item_name(self):
        return "Wood Supply"

    def set_rules(self):
        # No progression item gates until actual Castle Story unlock effects are
        # validated. Do not assume that a received item already unlocks equipment.
        self.multiworld.completion_condition[self.player] = (
            lambda state: state.has(GOAL_ITEM_NAME, self.player)
        )

    def fill_slot_data(self):
        if not hasattr(self, "campaign_plan"):
            self.campaign_plan = plan_campaign(
                self._mode(), self.options.invasion_goal.value,
                self.options.conquest_shards.value,
                dict(self.options.conquest_map_capacities.value),
                self.options.invasion_map_pool.value, self.multiworld.seed)
        return {
            "game_mode": self._mode(),
            "goal_wave": self.options.invasion_goal.value,
            "conquest_capture_goal": self.options.conquest_shards.value,
            "location_ids": [LOCATION_NAME_TO_ID[name] for name in self._active_names()],
            "campaign": self.campaign_plan,
            "protocol_version": 6,
            # New seeds opt into real in-game effects; already generated slots
            # without this contract continue in read-only reward mode.
            "gameplay_rules": {
                "version": 1,
                "native_unit_rewards": bool(self.options.gameplay_rewards.value),
                "unlock_required_for_equipped_units": bool(
                    self.options.gameplay_rewards.value),
            },
        }
