# Castle Story Archipelago — gameplay preview v0.9.0

Select `invasion`, `conquest`, or `hybrid`, then choose an
`invasion_goal` from 1 to 10 and `conquest_shards` from 1 to 30.
Hybrid needs at least one mission of each type.

## Maps

Archipelago seeds use technical native IDs (`official:<folder>` or
`workshop:<folder>`), never translated map names. If
`invasion_map_pool: []`, Castle Story chooses a deterministic compatible
Invasion map from its installed native catalog on the first connection to
this seed.

Conquest requires `conquest_map_capacities` with **verified** shard counts
for every eligible map. The APWorld does not have access to the player's game
installation, and it will reject seeds without enough verified capacity.

## Actual items and checks (gameplay protocol 1)

Newly generated seeds use `gameplay_rewards: true` by default. Invasion
checks are triggered by completing waves; Conquest checks by capturing
player-owned crystals and completing the seeded Conquest mission.

The playable item pool consists of `Equipped Worker`,
`Bricktron Reinforcement`, matching
`Archer Unlock` + `Equipped Archer`,
`Knight Unlock` + `Equipped Knight`, and
`Alchemist Unlock` + `Equipped Alchemist`, depending on location count.
An equipped unit received as a gift does not require its crafting unlock. It can be awarded before the corresponding technology is available.

The BepInEx plugin can create units using a verified local game's
`UNetProjectCmd.SpawnUnit` arguments. It cannot safely invent a unit
asset key or network owner: first observe an actual locally owned native
spawn. Gifts are journaled by AP item index; uncertain spawns are never
repeated blindly. Gifts currently count towards Castle Story's ordinary
population limit. Tech unlocks will eventually restrict native production of additional units, not gifted units. The game's crafting restrictions are not implemented yet.

`Wood Supply`, `Stone Supply`, `Wisp Supply`, and `Catapult Unlock`
still have reserved IDs but have no verified native effect, so they are not
placed in the active gameplay item pool. The implementation is a gameplay
preview and needs testing against the original game.

## Compatibility and testing

Old seeds that lack `gameplay_rules` retain the old behavior. Generate a
**new seed with the updated .apworld** to test native gifts. Install the
matching client bundle (bridge and BepInEx DLL), use a disposable solo
save, check the AP server for wave completion and press F10 in the game to
log gift-ticket status in `BepInEx/LogOutput.log`.

Native gift spawning can be turned off using the BepInEx config flag
`Bonus Rewards/DisableSeedUnitRewards` without changing checks or saves.
