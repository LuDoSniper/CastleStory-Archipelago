"""Stable IDs shared by the APWorld, websocket bridge and BepInEx plugin.

Old IDs 276810001-010, 276812001-003 and 276812100 stay unchanged.
"""
GAME = "Castle Story"
LOCATION_BASE = 276810000
CONQUEST_LOCATION_BASE = 276812000
ITEM_BASE = 276811000
WAVES = tuple(range(1, 11))
CAPTURES = tuple(range(1, 31))
INVASION_LOCATIONS = {f"Invasion - Survive Wave {n}": LOCATION_BASE + n for n in WAVES}
CONQUEST_LOCATIONS = {
    **{f"Conquest - Capture Shard {n}": CONQUEST_LOCATION_BASE + n for n in CAPTURES},
    "Conquest - Victory": CONQUEST_LOCATION_BASE + 100,
}
LOCATION_NAME_TO_ID = {**INVASION_LOCATIONS, **CONQUEST_LOCATIONS}
ITEM_NAMES = (
    "Archer Unlock", "Knight Unlock", "Catapult Unlock",
    "Bricktron Reinforcement", "Wood Supply", "Stone Supply",
    "Alchemist Unlock", "Equipped Worker", "Equipped Archer",
    "Equipped Knight", "Equipped Alchemist", "Wisp Supply",
)
ITEM_NAME_TO_ID = {name: ITEM_BASE + i for i, name in enumerate(ITEM_NAMES, 1)}
PROGRESSION_ITEMS = ("Archer Unlock", "Knight Unlock", "Catapult Unlock", "Alchemist Unlock")
USEFUL_ITEMS = ("Bricktron Reinforcement", "Equipped Worker", "Equipped Archer",
                "Equipped Knight", "Equipped Alchemist", "Wisp Supply")
GOAL_ITEM_NAME = "Castle Story Victory"
