# Castle Story Archipelago — Experimental
Requires Castle Story for Windows, BepInEx 5 Unity Mono, the CastleStoryArchipelago plugin, Python 3.11+ and the bridge.

## Generation
Install the .apworld with your Archipelago Launcher. Put a player YAML in Players:
```yaml
name: CastleStoryPlayer
game: Castle Story
Castle Story:
  game_mode: conquest # or invasion
```
Generate a seed and host the resulting multiworld.

## Conquest
Capturing 1, 2 and 3 distinct shards provides three checks, and winning provides a fourth. Capture detection currently uses `CrystalVisuals.PlayCapturedSound`, which is **not yet verified in a real Conquest session**. Enable the capture-check option **only for a disposable test game**; leave it disabled otherwise.

## Invasion
Completed waves 1-10 provide checks; wave 10 is the goal.

## Current limitations
The plugin displays incoming AP item names/counts but does not yet modify game resources, equipment or unit permissions. The bridge must be connected and the selected in-game mode must match the slot's YAML mode. Save-to-seed binding is not implemented, so do not reuse ordinary saves.
