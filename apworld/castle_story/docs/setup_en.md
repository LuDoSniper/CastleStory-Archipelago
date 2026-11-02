# Castle Story Archipelago campaign — developer preview

Game mode: `invasion`, `conquest` or `hybrid`. Hybrid requires both branches for
the final server goal. Set `invasion_goal` between 1 and 10 and
`conquest_shards` between 1 and 30.

## Seed-selected maps

The map planner chooses a subset with total shard capacity at least the
configured target, minimizing extra shard capacity and then the number of maps.
It uses the seed for deterministic ties.

**IMPORTANT:** the .apworld cannot read your game's installed map files.
`conquest_map_capacities` must list maps and VERIFIED shard counts. Empty or
insufficient map catalogs produce `campaign.ready=false`; the client must not
claim to have auto-selected playable maps. Do not guess counts. The default
invasion pool names the official Pioneer Pass map, but it must be installed.

## Game integration status

The client carries the seed-generated campaign to `session.json`. The Unity
plugin can display the campaign and connection UI, but the native game map-menu
replacement and automatic map launch still require verified game menu hooks.
Equipped Bricktron rewards are modeled in the item catalog, not yet granted
outside the Home Crystal's cap. The current release is a development build.
