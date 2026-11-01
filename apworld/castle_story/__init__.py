from BaseClasses import Item, ItemClassification, Location, Region, Tutorial
from worlds.AutoWorld import WebWorld, World
from .catalog import (
    GAME, GOAL_ITEM_NAME, ITEM_NAME_TO_ID, LOCATION_NAME_TO_ID,
    PROGRESSION_ITEMS, INVASION_LOCATIONS, CONQUEST_LOCATIONS,
    INVASION_POOL, CONQUEST_POOL,
)
from .options import CastleStoryOptions

class CastleStoryItem(Item):
    game = GAME

class CastleStoryLocation(Location):
    game = GAME

class CastleStoryWeb(WebWorld):
    game = GAME
    theme = "stone"
    tutorials = [Tutorial(
        "Setup", "Castle Story experimental setup", "English",
        "setup_en.md", "setup/en", ["Castle Story AP"],
    )]

class CastleStoryWorld(World):
    game = GAME
    web = CastleStoryWeb()
    options_dataclass = CastleStoryOptions
    item_name_to_id = ITEM_NAME_TO_ID
    location_name_to_id = LOCATION_NAME_TO_ID

    def _conquest(self):
        return self.options.game_mode.value == 1

    def create_regions(self):
        menu = Region("Menu", self.player, self.multiworld)
        gameplay = Region("Conquest" if self._conquest() else "Invasion", self.player, self.multiworld)
        self.multiworld.regions.extend((menu, gameplay))
        menu.connect(gameplay)
        locations = CONQUEST_LOCATIONS if self._conquest() else INVASION_LOCATIONS
        for name, code in locations.items():
            gameplay.locations.append(CastleStoryLocation(self.player, name, code, gameplay))
        # Server victory is communicated explicitly using StatusUpdate(status=30).
        # This event is a generation-time completion token; it is not an in-game reward.
        goal = CastleStoryLocation(self.player, "Victory event", None, gameplay)
        goal.place_locked_item(CastleStoryItem(
            GOAL_ITEM_NAME, ItemClassification.progression, None, self.player))
        gameplay.locations.append(goal)

    def create_item(self, name):
        classification = (
            ItemClassification.progression if name in PROGRESSION_ITEMS
            else ItemClassification.useful if name == "Bricktron Reinforcement"
            else ItemClassification.filler
        )
        return CastleStoryItem(name, classification, ITEM_NAME_TO_ID[name], self.player)

    def create_items(self):
        pool = CONQUEST_POOL if self._conquest() else INVASION_POOL
        self.multiworld.itempool.extend(self.create_item(name) for name in pool)

    def get_filler_item_name(self):
        return "Wood Supply"

    def set_rules(self):
        # No AP-item gates yet: real in-game equipment gates are not implemented.
        # Avoid inventing softlocks by assuming that AP inventory controls game unlocks.
        self.multiworld.completion_condition[self.player] = (
            lambda state: state.has(GOAL_ITEM_NAME, self.player)
        )

    def fill_slot_data(self):
        return {
            "game_mode": "Conquest" if self._conquest() else "Invasion",
            "goal_wave": 10,
            "conquest_capture_goal": len(CONQUEST_LOCATIONS) - 1,
            "protocol_version": 3,
        }
