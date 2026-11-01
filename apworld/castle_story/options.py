from dataclasses import dataclass
from Options import Choice, PerGameCommonOptions

class GameMode(Choice):
    """Choose which Castle Story mode to play for this Archipelago slot."""
    display_name = "Game Mode"
    option_invasion = 0
    option_conquest = 1
    default = 0

@dataclass
class CastleStoryOptions(PerGameCommonOptions):
    game_mode: GameMode
