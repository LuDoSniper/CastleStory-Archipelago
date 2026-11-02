# Castle Story × Archipelago

**Experimental v0.3.0** — Invasion and Conquest slots in the Archipelago world, client/server bridge, BepInEx game hooks, and receipt of remote items into an in-game HUD. **Received items are shown, but not yet applied to real resources, equipment or units.** Conquest capture detection was validated for **one neutral shard** in a real offline Conquest match; sending to the AP server stays opt-in until other crystal types and repeated captures are tested.

## How it works

- **Invasion**: wave 1 to 10 completion checks from `Brix.Game.StatsTracker.InvasionWave(int)`. F8 timing tests on 2026-11-01 showed `InvasionWave(1)` after the user defeated wave 1. Wave 10 sends the Archipelago goal.
- **Conquest**: three distinct capture-event candidates from `CrystalVisuals.PlayCapturedSound()`, plus victory from `Brix.Game.StatsTracker.GameWon()`. Conquest should involve capturing shards, then destroying or capturing the enemy Home Crystal. **Confirmed:** a neutral shard capture fired the hook with `ownedByPlayer=True` exactly once in the first test. **Not yet confirmed:** multiple distinct shards, recaptures, other crystal types, and the `GameWon()` victory hook.
- **AP networking**: WebSocket connection, item synchronization, reconnect and mode/seed-specific checks. Server checks and received items are demonstrated by automated mock protocol tests.
- **Safeguards**: location IDs are filtered by mode, session token and seed. **Do not reuse a regular save or connect a different seed to an ongoing game**: binding a Castle Story save to a particular Archipelago seed has not been implemented yet.

## Download and install

1. Run or select a green workflow under [Actions](https://github.com/LuDoSniper/CastleStory-Archipelago/actions). Download `castle-story-bepinex-plugin` and `castle-story-apworld`.
2. Install BepInEx 5 Unity Mono matching the architecture of `Castle Story.exe`. The supported test game uses **Unity 5.6** and a 64-bit executable. Place `CastleStoryArchipelago.dll` inside `Castle Story/BepInEx/plugins/`.
3. Install the `castle_story.apworld` through the Archipelago Launcher and restart it. Create a YAML file inside the Archipelago `Players` folder (example below), then generate a seed and start its server.
4. Install Python 3.11+ and install dependencies with `py -m pip install -r client/requirements.txt`.
5. In a terminal from the downloaded repository, run:

```powershell
py client/bridge.py --server localhost:38281 --slot CastleStoryPlayer --bridge-dir "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Castle Story\\BepInEx\\config\\castle_story_archipelago"
```

Replace `--server` with the actual AP host and port, `--slot` with the slot name in YAML, and `--bridge-dir` with the path to your own game's BepInEx config directory.

### YAML (choose a mode before generating)

```yaml
name: CastleStoryPlayer
game: Castle Story
Castle Story:
  game_mode: invasion  # or conquest
```

### Enable checks for a disposable test run

BepInEx creates `BepInEx/config/fr.castlestory.archipelago.cfg` after the first successful mod startup. Keep the game **closed** while editing this file.

- Invasion: `[Invasion] EnableWaveChecks = true`
- Conquest: `[Conquest] EnableVictoryCheck = true`. Set `EnableCaptureChecks = true` **only for a controlled, disposable Conquest capture test**, because `PlayCapturedSound` is a candidate hook that may also run for non-shard captures.

Restart the game, verify the AP HUD shows the correct mode and `CONNECTED`, and **start a new match**. Do not turn on both modes and assume they mix: each AP slot has exactly one mode.

### Test without a full match

Use the Python unit tests to check network packet filtering. F8 in-game prints wave/capture/victory statistics in `BepInEx/LogOutput.log`. **Validated 2026-11-02:** an offline neutral-shard capture raised `[AP CONQUEST CAPTURE]`, marked as locally owned and unique. F8 markers occurred at 152.3s (capture underway) and 159.9s (capture completed); the capture fired at 158.7s with `captures=1`. No more capture-building is required for this basic hook check. For the next stage, set up a disposable Archipelago Conquest slot and verify real `LocationChecks` on the server; a separate real match is still needed to confirm `GameWon()`.

## Constraints and future work

- Item grants are HUD/inventory synchronization only; physical game effects and gameplay gates are not implemented yet. This avoids falsely claiming unlocks that the game has not received.
- Conquest capture callback could fire on unrelated crystals. Never enable capture check sending for production until tested.
- Per-save AP seed binding, save restoration, APWorld generation using the full Archipelago distribution, and real multiworld game interoperability require validation.
- The APWorld is a *developer prototype*, not yet a balanced randomizer; do not use for a long multiworld campaign.
- Do not commit Castle Story proprietary assemblies to this repository.

## Development

```bash
pip install -r client/requirements.txt
python -m unittest discover -s tests -v
python tools/pack.py
```

## Validation evidence — real game Conquest test (2026-11-02)

- Castle Story under Unity 5.6.5; BepInEx 5.4.23.6; plugin v0.3.0 successfully loaded.
- Harmony postfix hooks attached for `StatsTracker.InvasionWave`, `StatsTracker.GameWon` and `CrystalVisuals.PlayCapturedSound`.
- User initiated a Conquest shard capture, pressed F8 at **152.3 s** while capturing, and at **159.9 s** after completing the capture.
- At **158.7 s**, log recorded `[AP CONQUEST CAPTURE] faction=Brix.Game.Faction ownedByPlayer=True` and `candidate id=-466248 new=True`.
- Second F8 log reported `captures=1 victories=0 checks=0`, `mode=` (no AP slot connected), and `connected=False`. This is expected for the offline hook-only test.
- The evidence supports correct detection of **one neutral shard owned by the local player**. It does not establish correct behavior for other crystals, multiple matches, repeated captures or the server's actual receipt of an Archipelago check.
