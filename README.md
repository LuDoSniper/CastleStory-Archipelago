# Castle Story × Archipelago

**Developer preview — not a fully playable Archipelago integration.** This repository contains an APWorld generator, websocket bridge and an experimental BepInEx/Harmony hook into the actual game's invasion statistics. In-game unlocks, grants, save binding and wave completion timing still require implementation/validation. Do not use this build for a serious multiworld.

## Discovered from supplied game assemblies

The supplied `Assembly-CSharp-firstpass.dll` contains `Brix.Game.StatsTracker.InvasionWave(int)`, `GameWon()` and `GameLost()`. The tiny `Assembly-CSharp.dll` is not the primary game-code assembly. `InvasionWave(int)` is a **statistics event**; whether it fires on wave start or wave completion has **not yet been established**. The plugin logs it by default and does not claim a wave completion until explicitly enabled.

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

The default BepInEx settings are deliberately **LOG ONLY**. Don't enable `EnableWaveChecks` and `StatEventMeansWaveComplete` until the log confirms that the stats callback means a *completed* wave. If it fires at wave start, a different hook or an adjusted mapping will be required. Press F8 to log the current integration status.


## Validation note — 2026-11-01

The first real-game test with **v0.2.1** confirmed the plugin loaded and Harmony hooked `StatsTracker.InvasionWave(int)`. The log recorded four `InvasionWave(0)` calls and one `InvasionWave(1)` call. The tester manually started **one wave** and defeated both enemies. This is consistent with a counter progressing to 1 but **does not establish that the event fires at wave completion**.

Version **0.2.2** displays the last observed wave and event count in the overlay and logs elapsed game time for each wave event. Press F8 at three points during **a single short wave** (before starting, after starting, after defeating the final enemy), then send `BepInEx/LogOutput.log`. F8 adds timestamped `[AP TEST MARKER F8]` lines. Leave `Hook checks: LOG ONLY` until start/end timing is confirmed.

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
