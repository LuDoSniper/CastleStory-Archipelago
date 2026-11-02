# Castle Story × Archipelago

**v0.4.0 — campaign architecture preview, not a finished randomizer.**

Castle Story (Unity 5.6 / BepInEx 5) integration with the Archipelago
Multiworld network and ArchiLAN. The original Invasion wave hook, Conquest
neutral-shard capture hook, and sending checks to a real ArchiLAN server
have been validated by the tester.

## Campaign modes

The Archipelago YAML now supports three options for `game_mode`:
`invasion`, `conquest`, and `hybrid`. Hybrid collects checks for **both**
game modes in the same AP slot and requires both branches for the final goal.

`invasion_goal` is 1–10 waves. `conquest_shards` is 1–30 captures.
A deterministic planner chooses one or more Conquest maps whose VERIFIED
combined shard count is at least the goal. It first minimizes extra shards,
then map count. **No map shard counts are hardcoded or guessed.**

The YAML `conquest_map_capacities` lists the maps you have checked and their
actual shard counts, for example `{"CheckedMap": 3}` (example name/capacity
ONLY, not an actual Castle Story map). When too few verified map entries
are provided, the generated seed reports `campaign.ready=false`; it is
not playable as an automatically selected Conquest campaign yet.

See [campaign architecture](docs/CAMPAIGN_ARCHITECTURE.md) for the protocol,
the still-unimplemented game menu and Bricktron-cap work, and safety limits.

## Download and installation

Download the latest green GitHub Actions **castle-story-archipelago-test-kit**.
Install `CastleStoryArchipelago.dll` into
`Castle Story/BepInEx/plugins/` (BepInEx 5 Unity Mono, 64-bit game build).

Install Python 3.11+ and from the unpacked test kit start the bridge
**without** `-Server` or `-Slot`:

```powershell
powershell -ExecutionPolicy Bypass -File .\Start-Bridge.ps1 -GameDirectory "C:\Program Files (x86)\Steam\steamapps\common\Castle Story"
```

Launch Castle Story and click the **AP** button at the upper left. Enter
the WSS endpoint provided by ArchiLAN, your slot name and optional password.
`AP` is a placeholder for the official logo until the UI integration is
completed. Clicking AP again toggles the popup. The vanilla menu remains
available for safety; native menu replacement is **not yet implemented**.

A **seed-selected map list** is shown once `session.json` contains a
`campaign` manifest. Selecting a mission only records the selection; you
must still launch the chosen map manually until the native loader is hooked.

For older CLI-style use, `Start-Bridge.ps1 -Server ... -Slot ...` remains
supported. For a short disposable legacy Conquest test, set
`EnableCaptureChecks = true` in `BepInEx/config/fr.castlestory.archipelago.cfg`.

The bridge writes the credentials locally to
`BepInEx/config/castle_story_archipelago/connection.json`: **do not share
this file or commit it to git**. Logs should be reviewed before publishing.

## For mod development

- `F8`: session/check diagnostics.
- `F9`: **read-only** game API discovery in `BepInEx/LogOutput.log`.
  It prints signatures for menu, map, crystal and spawn-related methods.
- `python tools/inspect_maps.py "C:\path\to\Castle Story"` generates a
  candidate map-folder inventory. It intentionally does **not** invent
  shard capacities; they need verifying.
- `python -m unittest discover -s tests -v`: bridge/algorithm tests.

### Known unimplemented features

- Actual in-game creation and spawning of bonus equipped Bricktrons
  **outside the Home Crystal's quota**. Item IDs are reserved and received
  items are displayed; physical spawning and persistence are not implemented.
- Replacing the game's native map picker / actually launching a chosen map.
- Per-map progress and save/seed binding; robust mapping of shard identity
  across restarts.
- Genuine unlock logic, real resource rewards, death/respawn reconciliation.
- A certified preflight of this v0.4.0 .apworld inside ArchiLAN.
- The official Archipelago icon asset.

Do not use this prototype for an important multiworld or a normal Castle Story save.

The Castle Story assemblies are proprietary and are never redistributed.
