# Campaign integration — development status v0.5.0

## Implemented and CI-tested

- APWorld modes `invasion`, `conquest`, `hybrid`, goals and deterministic
  map subset selection from explicitly supplied shard capacities.
- Stable location/item IDs and Hybrid mode-check filtering.
- Bridge handshake, item receipt, session file and map-availability check.
- PyInstaller self-contained Windows client added to the GitHub Actions bundle.
- Native Unity main-menu connection overlay and AP mission list.
- Harmony new-game OnOk guard; code path to auto-select cached native
  `Asset_Map` from the new-game library, query available `Asset_Gamemode`,
  and invoke Castle Story's own native start method.
- Distinct per-mission captured shard offsets and victory bookkeeping.
- Per-seed, per-item-index native bonus delivery journal and opt-in spawn
  adapter using the validated UNetProjectCmd SpawnUnit parameter signature.
- Map inspector and explicit confirmation of observed shard counts.

## Must validate in Castle Story itself

1. Native menu hook called for starting a solo new game, map identity from
   `Asset_Map.GetHeightmapPath()`, gamemode identity from
   `Asset_Gamemode.GetIntroImagePath()`, and actual launcher call.
   These names and signatures were inspected in the supplied Assembly DLL,
   but the precise live object lifetime remains unverified.
2. Shard capture matching to per-mission identity survives scene reload
   and revisiting the same crystal without duplicating checks.
3. The locally owned `UNetProjectCmd.SpawnUnit` reflection callback
   returns the expected `GameObject`; occupation enum names and equipment
   support; extra units' interactions with crystal cap.
4. Capturing/destroying multiple Home Crystals produces the intended
   `GameWon` event for Conquest and Hybrid. The previous in-game test
   only validated a shard capture and its AP check.
5. ArchiLAN preflight of the *new* APWorld and seed options; prior live
   ArchiLAN test covered an earlier Conquest version, not v0.5.0.

## Data safety / follow-up architecture

- Default native bonus spawn **OFF** and no global cap modification.
- Never reset a bonus ticket in `spawning` or `review` automatically.
- Need a save GUID to bind mission progress and bonus ownership; the
  current seed token is not a per-save identity.
- Need event-driven per-mission starts to ignore detections from the
  previous scene; current selected mission is an explicit manual step.
- Need a verified quota accounting hook before claiming bonus units
  are invisible to Home Crystal population limits.
- Need a usable, non-blocking UI integrated with the *real* main menu
  instead of permanent overlay. Current AP button is a placeholder,
  not the official Archipelago logo graphic.
