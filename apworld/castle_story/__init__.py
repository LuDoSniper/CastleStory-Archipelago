from BaseClasses import Item, ItemClassification, Location, Region, Tutorial
from worlds.AutoWorld import WebWorld, World
from worlds.generic.Rules import set_rule
from .catalog import GAME, GOAL_ITEM_NAME, GOAL_NAME, ITEM_NAME_TO_ID, ITEM_POOL, LOCATION_NAME_TO_ID, PROGRESSION_ITEMS

class CastleStoryItem(Item):
    game = GAME

class CastleStoryLocation(Location):
    game = GAME

class CastleStoryWeb(WebWorld):
    game = GAME
    theme = "stone"
    tutorials = [Tutorial("Setup", "Castle Story experimental setup", "English", "setup_en.md", "setup/en", ["Castle Story AP"])]

class CastleStoryWorld(World):
    game = GAME
    web = CastleStoryWeb()
    item_name_to_id = ITEM_NAME_TO_ID
    location_name_to_id = LOCATION_NAME_TO_ID

    def create_regions(self):
        menu = Region("Menu", self.player, self.multiworld)
        invasion = Region("Invasion", self.player, self.multiworld)
        self.multiworld.regions.extend((menu, invasion))
        menu.connect(invasion)
        for name, code in LOCATION_NAME_TO_ID.items():
            invasion.locations.append(CastleStoryLocation(self.player, name, code, invasion))
        goal = CastleStoryLocation(self.player, GOAL_NAME, None, invasion)
        goal.place_locked_item(CastleStoryItem(GOAL_ITEM_NAME, ItemClassification.progression, None, self.player))
        invasion.locations.append(goal)

    def create_item(self, name):
        classification = (ItemClassification.progression if name in PROGRESSION_ITEMS else
                          ItemClassification.useful if name == "Bricktron Reinforcement" else ItemClassification.filler)
        return CastleStoryItem(name, classification, ITEM_NAME_TO_ID[name], self.player)

    def create_items(self):
        self.multiworld.itempool.extend(self.create_item(name) for name in ITEM_POOL)

    def get_filler_item_name(self):
        return "Wood Supply"

    def set_rules(self):
        # Sequential wave progression; the current prototype does not enforce item gates in-game.
        for n in range(2, 11):
            set_rule(self.get_location(f"Invasion - Survive Wave {n}"),
                     lambda state, n=n: state.can_reach(f"Invasion - Survive Wave {n-1}", "Location", self.player))
        set_rule(self.get_location(GOAL_NAME),
                 lambda state: state.can_reach("Invasion - Survive Wave 10", "Location", self.player))
        self.multiworld.completion_condition[self.player] = lambda state: state.has(GOAL_ITEM_NAME, self.player)

    def fill_slot_data(self):
        return {"game_mode": "Invasion", "goal_wave": 10, "protocol_version": 2}
