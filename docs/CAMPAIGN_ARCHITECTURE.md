# Castle Story Archipelago — technical gameplay status v0.9.0

## Stable, validated foundations

The original Castle Story native menus are modified before rendering so they
remain native-looking, without a flash or destructive global scans. APWorld
campaigns use explicit technical map identifiers and deterministic selection
by seed. Solo native startup and load-save guards require an actual mission
map/mode that belongs to the current Archipelago run.

Local save ownership uses seed/team/slot metadata outside the game's save
files. Each AP location is written into a run-specific check ledger and sent
through a separate Python WebSocket bridge. The bridge validates location IDs
against the server's own slot data; reconnects do not repeat completed checks.

## Gameplay v1 scope

### Checks

- Invasion wave checks are automatic after a verified seed mission starts.
- Conquest owner-verified crystal captures and victory checks now use the same
  automatic seed-mission rules. Legacy tests without a ready campaign still
  respect the old explicit opt-in flags. A duplicate crystal position/mission
  or wave number cannot grant a second check.
- Hybrid runs require both branches. The server receives GOAL only when
  Invasion and Conquest goals have both been marked in the local ledger.
- Missing/stale server heartbeats do not erase valid queued checks.

### Rewards and real game effects

- APWorld v0.9.0 sends `gameplay_rules: { version: 1, ... }`, which the bridge
  mirrors as simple versioned fields in `session.json` for Unity 5.6.
  Seeds that predate the gameplay contract do not enable automatic gifts.
- Only implemented item types enter the default live reward pool. Paired
  Archer/Knight/Alchemist unlocks and equipped units are always in the same
  seed pool, with a basic worker and additional reinforcements for remainder
  slots. Placement remains randomized by Archipelago's ordinary item filler,
  and repeatable reinforcement choices vary deterministically by seed.
- The existing, signature-validated
  `Brix.Game.Network.UNetProjectCmd.SpawnUnit` hook captures game-owned
  arguments only after verifying the spawned unit's local ownership. A
  received reinforcement or equipped unit can replay that native adapter
  without inventing AssetKey, owner GameObject, or occupation enum members.
- Advanced equipped gifts remain pending until the corresponding unlock item
  has been received. Native unit grants count toward the **normal** game
  population limit; they are not currently cap-neutral.
- Per-seed, per-item-index journals record a `spawning` intent durably
  BEFORE calling SpawnUnit. A crash or ambiguous return leaves a non-retried
  `spawning` or `review` state, not a duplicate unit. Templates are reset on
  scene changes. An emergency opt-out is provided for new-seed native gifts.

## Still pending runtime validation / further gameplay work

1. Run a new v0.9.0 seed against the local Windows Unity 5.6 game and confirm
   that a locally owned unit spawn populates the adapter and new gifts appear.
   Validate what the game's own cap does on units beyond a full Home Crystal.
2. Inspect game-native resource APIs before implementing `Wood Supply`,
   `Stone Supply`, and `Wisp Supply`; do not create fake inventory values.
3. Locate exact native build/equipment methods before enforcing received
   Archer/Knight/Alchemist/Catapult unlocks on *vanilla* production menus.
   Gating gifted advanced units is implemented, but gating every ordinary
   unit and building would be premature.
4. Verify crystal capture keys and Conquest victory events across multiple
   maps, save reloads, and a complete Hybrid goal.
5. Design per-save (rather than solely per-slot) delivered-unit reconciliation
   for users who branch from an older snapshot. Automatic regrant on save load
   is unsafe until saved native unit identity can be verified.

All normal Castle Story saves remain untouched by metadata helpers. Reward
spawning is a deliberate gameplay modification and should first be tested on a
disposable solo save. Build CI verifies compilation and protocol simulation;
it cannot emulate the proprietary native game runtime.
