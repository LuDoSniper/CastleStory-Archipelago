# Castle Story × Archipelago — v0.5.0 development build

Castle Story (Unity 5.6 / BepInEx 5) ↔ Archipelago ↔ ArchiLAN.

The tester verified Invasion wave events, a real Conquest shard capture,
and checks reaching ArchiLAN. **The new mission launcher, map validation
and native bonus-unit spawning have compiled, but have not yet been
validated in a real Castle Story campaign. Use disposable solo saves only.**

## Three campaign modes

A player slot chooses **invasion**, **conquest**, or **hybrid** in its YAML.
Hybrid requires both the configured Invasion wave goal and Conquest's
shard goal plus final victory. The planner chooses seed-specific map
combinations covering a configurable 1–30 Conquest shards, minimizing excess
shards and then the number of maps. It does not invent map counts.

Example slot:

```yaml
name: CastleStoryPlayer
game: Castle Story
Castle Story:
  game_mode: hybrid
  invasion_goal: 5
  conquest_shards: 7
  invasion_map_pool:
    - Helix
  conquest_map_capacities:
    "My Verified Map A": 3
    "My Verified Map B": 4
```

**The two `My Verified Map` names/counts above are format examples ONLY.**
Use your own maps and counts confirmed from the actual game. If the catalog
cannot cover the shard goal, the campaign is marked not ready. ArchiLAN must
generate a new seed with the v0.5.0 .apworld before a Hybrid run can be used.

## Installing the complete test kit

Download the latest *green* GitHub Actions artifact named
`castle-story-archipelago-test-kit`. Extract it and copy the **contents of
the `BepInEx/plugins` directory** into your existing
`Castle Story/BepInEx/plugins` folder, preserving its subdirectories.

The test kit includes:

- `CastleStoryArchipelago.dll`: Unity/BepInEx mod.
- `BepInEx/plugins/castle_story_ap/castle-story-ap-bridge.exe`: self-contained
  Windows bridge. Clicking **Connect** in the in-game AP popup starts it
  automatically; **no Python installation is required** for this path.
- `castle_story.apworld`: install/upload in ArchiLAN.
- `Start-Bridge.ps1` and Python sources for optional manual diagnostics.
- An example slot YAML.

In Castle Story, use the **AP** button at the upper left to open/close the
connection popup. Enter the full `wss://` ArchiLAN URL, the slot name and
optional password. A connection creates `connection.json` locally; it
contains the slot password in plaintext. **Do not upload that file or share
it with logs.**

While a ready AP campaign is connected, selecting a mission determines
the allowed map and mode. The plugin hooks the real Castle Story
`BaseMapStarterMenu.OnOk` to block accidental starts on other maps/modes.
It also observes the game's new-map library and offers **Launch selected
AP mission**. For this first integration test, open the game's native
**New Game** screen once so its map assets and starter context are loaded.
The plugin then tries to select the exact seeded map, choose its game
mode, and call the native start action. Native API interactions need a
real-game validation before the vanilla menu can safely be removed.

## Map catalog — no guessed shard counts

The map inspector reads the official `Info/Maps` and Workshop
`Info/UGC/Maps` folders, reads `LuaMarkersCount.json`, and reports
shard/crystal marker evidence without treating every crystal as a
capturable shard.

From a terminal with Python:

```powershell
py tools/inspect_maps.py "C:\Program Files (x86)\Steam\steamapps\common\Castle Story"
```

This creates `castle_story_map_inventory.json` and
`castle_story_map_options.yaml`. After validating specific maps and shard
counts in Castle Story, approve them:

```powershell
py tools/inspect_maps.py "C:\Program Files (x86)\Steam\steamapps\common\Castle Story" --approve "Actual Map=4" --approve "Another Actual Map=5"
```

The generated `conquest_map_capacities` YAML can be pasted into the slot
configuration in ArchiLAN. The Windows bridge also checks whether the
maps in a seed exist on the local machine, and marks the campaign unready
if verified locally missing.

## Equipped Bricktron reward delivery — experimental

`Equipped Worker`, `Equipped Archer`, `Equipped Knight` and
`Equipped Alchemist` have distinct item IDs. The mod persists an AP
item-indexed delivery journal per seed; duplicates are not automatically
regranted after a reconnect/restart.

An **experimental native SpawnUnit adapter** records a normal, locally
owned Bricktron spawn as a template. If explicitly enabled, it can replay
the game's spawn API with the requested occupation. It uses a two-phase
`spawning`→`delivered/review` journal so an interrupted call is never
blindly retried. Importantly, an equipped unit is not considered delivered
unless an actual, locally owned GameObject is returned.

The default config keeps `EnableNativeBonusSpawns = false`.
This adapter is **NOT proven to bypass Home Crystal quotas, preserve kits,
respawn, or survive reloading**. `GeneralRules.bricktronCap` is not raised.
A truly quota-independent bonus unit still requires game instrumentation and
validation of its ownership and Crystal count sources.

Do not enable experimental native spawns for real play. If you want to
test the adapter on a disposable save, do so knowing that extra units
could interact incorrectly with game population limits.

## Diagnostics

- `F8`: check/session status.
- `F9`: read-only menu, map, spawn and crystal API signature report.
- `F10`: bonus delivery queue, native template and occupation names.
- `BepInEx/LogOutput.log`: game diagnostics (review for credentials before
  sharing).
- `python -m unittest discover -s tests -v`: automated bridge/catalog tests.

Detailed architecture and limitations:
[docs/CAMPAIGN_ARCHITECTURE.md](docs/CAMPAIGN_ARCHITECTURE.md).

The proprietary Castle Story assemblies are **never** shipped.
