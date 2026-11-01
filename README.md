# Castle Story × Archipelago

**Developer preview — not a fully playable Archipelago integration.** This repository contains an APWorld generator, websocket bridge and an experimental BepInEx/Harmony hook into the actual game's invasion statistics. In-game unlocks, grants and save-to-seed binding still require implementation. First-wave completion timing has been observed in one run. Do not use this build for a serious multiworld.

## Discovered from supplied game assemblies

The supplied `Assembly-CSharp-firstpass.dll` contains `Brix.Game.StatsTracker.InvasionWave(int)`, `GameWon()` and `GameLost()`. The tiny `Assembly-CSharp.dll` is not the primary game-code assembly. `InvasionWave(int)` is a **statistics event**. In one controlled run, `InvasionWave(0)` occurred before and during the first wave, and `InvasionWave(1)` occurred after the player defeated both enemies. Positive values are *candidate completed-wave counts*. Sending to Archipelago is **opt-in** while save binding is unfinished.

## Layout

- `apworld/castle_story/`: ten invasion locations, stable IDs, generation logic
- `client/bridge.py`: Archipelago websocket client with seed/slot file bridge
- `mod/`: BepInEx 5 Mono/Harmony plugin; reflective game hook, no distributed game DLLs
- `tests/`: small protocol/catalog checks
- `.github/workflows/build.yml`: APWorld tests, packaging, experimental plugin build

## Setup (Windows)

1. Download the CI artifacts from **Actions** once the workflow succeeds.
2. Install `castle_story.apworld` using Archipelago Launcher, then generate a Castle Story slot YAML and seed.
3. Install BepInEx **5 Unity Mono**, matching the Castle Story executable architecture, into your Castle Story folder. Run the game once.
4. Place `CastleStoryArchipelago.dll` in `<game>/BepInEx/plugins/`.
5. Install Python 3.11+ and run `pip install -r client/requirements.txt`.
6. Launch the bridge:

   ```powershell
   python client/bridge.py --server localhost:38281 --slot "CastleStoryPlayer" --bridge-dir "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Castle Story\\BepInEx\\config\\castle_story_archipelago"
   ```

7. Launch Castle Story, start **Invasion** and inspect `BepInEx/LogOutput.log` for `InvasionWave(...)` while observing whether the wave is starting or ending.

The default BepInEx setting `EnableWaveChecks = false` stays **LOG ONLY**. Version **0.2.3** permits opt-in wave checks for values 1–10 when both `EnableWaveChecks = true` and a matching bridge session are present. For a controlled multiworld test, connect the intended AP slot, launch a **new Invasion game**, edit `BepInEx/config/fr.castlestory.archipelago.cfg`, and restart the game. Confirm `Hook checks: ON` in the overlay. Do not use a personal save: save-to-seed binding is **not** implemented. The old `StatEventMeansWaveComplete` key is obsolete and ignored. Press F8 to log status.


## Validation note — 2026-11-01

The first real-game test with **v0.2.1** confirmed the plugin loaded and Harmony hooked `StatsTracker.InvasionWave(int)`. The log recorded four `InvasionWave(0)` calls and one `InvasionWave(1)` call. The tester manually started **one wave** and defeated both enemies. This is consistent with a counter progressing to 1 but **does not establish that the event fires at wave completion**.

Version **0.2.2** collected a second real-game log with F8 markers **before starting (85.5s)**, **after starting (91.4s)**, and **after finishing (157.2s)**. Wave 0 events occurred at **90.6s**, **140.3s** and **153.6s**. Wave 1 occurred at **153.7s**, between the start and completion markers, consistent with the user's report of killing both enemies. Version **0.2.3** therefore offers opt-in AP checks on positive wave values. This is one controlled observation, not proof across all game modes.

## Technical limitations

- Item effects/unlocks (archers, knights, catapults, etc.) are NOT implemented. The APWorld's items are placeholders.
- Check persistence is session-token scoped, **not save-game scoped**. Reusing a save with another seed is unsafe.
- The goal packet is derived from checked wave 10; currently only usable once completion timing has been validated.
- No guarantee the portable NuGet Unity assemblies match your game's Unity version; a CI failure may require adjusting dependencies after inspecting logs.
- A running real game, real multiworld server and installed BepInEx are required to validate behavior.

## Development

```sh
pip install -r client/requirements.txt
python -m unittest discover -s tests -v
python tools/pack.py
```

For bugs, include the failing GitHub Actions job URL, `BepInEx/LogOutput.log`, your Castle Story version and whether a hooked `InvasionWave(n)` appeared at round start or completion. Do not publish your private seeds/passwords in logs.

We never commit proprietary Castle Story assemblies.
