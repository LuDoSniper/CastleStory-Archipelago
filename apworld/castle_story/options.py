"""Options for Castle Story Archipelago campaign generation."""
from dataclasses import dataclass
from Options import Choice, OptionDict, OptionSet, PerGameCommonOptions, Range

class GameMode(Choice):
    """Invasion only, Conquest only, or both modes in a single campaign."""
    display_name = "Campaign Mode"
    option_invasion = 0
    option_conquest = 1
    option_hybrid = 2
    default = 0

class InvasionGoal(Range):
    display_name = "Completed Invasion Waves"
    range_start = 1
    range_end = 10
    default = 10

class ConquestShards(Range):
    display_name = "Conquest Shard Captures"
    range_start = 1
    range_end = 30
    default = 3

class ConquestMapCapacities(OptionDict):
    """Non-localized map asset IDs mapped to VERIFIED capturable shard counts.

    These IDs are map folders/asset keys, not translated UI names.
    """
    display_name = "Conquest Maps (Map ID: Shard Count)"
    default = {}

class InvasionMapPool(OptionSet):
    """Optional stable Invasion asset IDs.

    If empty, the game chooses a deterministic random compatible Invasion
    map from the installed native Asset_Map registry when New Game opens.
    No guessed default map IDs and no localization-dependent names.
    """
    display_name = "Invasion Map IDs"
    default = set()

@dataclass
class CastleStoryOptions(PerGameCommonOptions):
    game_mode: GameMode
    invasion_goal: InvasionGoal
    conquest_shards: ConquestShards
    conquest_map_capacities: ConquestMapCapacities
    invasion_map_pool: InvasionMapPool
