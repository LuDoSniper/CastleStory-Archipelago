"""Castle Story Archipelago world: mode-aware campaign locations and seed manifest."""
from BaseClasses import Item, ItemClassification, Location, Region, Tutorial
from worlds.AutoWorld import WebWorld, World
from .catalog import (
    GAME, GOAL_ITEM_NAME, ITEM_NAME_TO_ID, LOCATION_NAME_TO_ID,
    INVASION_LOCATIONS, CONQUEST_LOCATIONS, PROGRESSION_ITEMS, USEFUL_ITEMS,
)
from .campaign import plan_campaign
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

    def create_regions(self):
        menu = Region("Menu", self.player, self.multiworld)
        campaign = Region("Campaign", self.player, self.multiworld)
        self.multiworld.regions.extend((menu, campaign))
        menu.connect(campaign)
        self.active_names = self._active_names()
        for name in self.active_names:
            campaign.locations.append(CastleStoryLocation(
                self.player, name, LOCATION_NAME_TO_ID[name], campaign))
        self.campaign_plan = plan_campaign(
            self._mode(), self.options.invasion_goal.value,
            self.options.conquest_shards.value,
            dict(self.options.conquest_map_capacities.value),
            self.options.invasion_map_pool.value,
            self.multiworld.seed,
        )
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

    def create_items(self):
        count = len(self.active_names)
        # Pool changes with the generated campaign size, never leaving unfilled
        # locations. Reserve a safe base: no hard gates before in-game effects exist.
        pool = ["Archer Unlock", "Knight Unlock", "Alchemist Unlock",
                "Bricktron Reinforcement", "Equipped Worker",
                "Equipped Archer", "Equipped Knight", "Equipped Alchemist"]
        pool = pool[:count]
        while len(pool) < count:
            pool.append(["Wood Supply", "Stone Supply", "Wisp Supply"][len(pool) % 3])
        self.multiworld.itempool.extend(self.create_item(x) for x in pool)

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
            "protocol_version": 4,
        }
