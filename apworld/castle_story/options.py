"""Settings exposed in Archipelago and ArchiLAN YAML generators."""
from dataclasses import dataclass
from Options import Choice, OptionDict, OptionSet, PerGameCommonOptions, Range

class GameMode(Choice):
    """Single player campaign: Invasion, Conquest, or both in Hybrid."""
    display_name = "Campaign Mode"
    option_invasion = 0
    option_conquest = 1
    option_hybrid = 2
    default = 0

class InvasionGoal(Range):
    """Completed invasion waves needed per invasion mission (1-10)."""
    display_name = "Invasion Wave Goal"
    range_start = 1
    range_end = 10
    default = 10

class ConquestShards(Range):
    """Total distinct shard captures required across selected conquest maps."""
    display_name = "Conquest Shards"
    range_start = 1
    range_end = 30
    default = 3

class ConquestMapCapacities(OptionDict):
    """Verified installed conquest maps and their shard counts; e.g. {MapName: 4}.
    Unknown values must NOT be guessed. Empty catalog leaves campaign unready.
    """
    display_name = "Conquest Map Shard Counts"
    default = {}

class InvasionMapPool(OptionSet):
    """Names of invasion maps installed locally (one chosen by the seed)."""
    display_name = "Installed Invasion Maps"
    default = {"Pioneer Pass"}

@dataclass
class CastleStoryOptions(PerGameCommonOptions):
    game_mode: GameMode
    invasion_goal: InvasionGoal
    conquest_shards: ConquestShards
    conquest_map_capacities: ConquestMapCapacities
    invasion_map_pool: InvasionMapPool
