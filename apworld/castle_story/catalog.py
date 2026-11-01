"""Stable IDs shared by the APWorld, network bridge and Unity plugin."""
GAME = "Castle Story"
LOCATION_BASE = 276810000
CONQUEST_LOCATION_BASE = 276812000
ITEM_BASE = 276811000
WAVES = tuple(range(1, 11))
CAPTURES = tuple(range(1, 4))
INVASION_LOCATIONS = {f"Invasion - Survive Wave {n}": LOCATION_BASE + n for n in WAVES}
CONQUEST_LOCATIONS = {
    **{f"Conquest - Capture Shard {n}": CONQUEST_LOCATION_BASE + n for n in CAPTURES},
    "Conquest - Victory": CONQUEST_LOCATION_BASE + 100,
}
LOCATION_NAME_TO_ID = {**INVASION_LOCATIONS, **CONQUEST_LOCATIONS}
ITEM_NAMES = (
    "Archer Unlock", "Knight Unlock", "Catapult Unlock",
    "Bricktron Reinforcement", "Wood Supply", "Stone Supply",
)
ITEM_NAME_TO_ID = {name: ITEM_BASE + i for i, name in enumerate(ITEM_NAMES, 1)}
PROGRESSION_ITEMS = ("Archer Unlock", "Knight Unlock", "Catapult Unlock")
INVASION_POOL = (
    "Archer Unlock", "Knight Unlock", "Catapult Unlock",
    "Bricktron Reinforcement", "Bricktron Reinforcement",
    "Wood Supply", "Wood Supply", "Wood Supply", "Stone Supply", "Stone Supply",
)
CONQUEST_POOL = ("Archer Unlock", "Knight Unlock", "Bricktron Reinforcement", "Wood Supply")
assert len(INVASION_POOL) == len(INVASION_LOCATIONS)
assert len(CONQUEST_POOL) == len(CONQUEST_LOCATIONS)
GOAL_ITEM_NAME = "Castle Story Victory"
