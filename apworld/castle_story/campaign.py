"""Deterministic campaign map planner.

Map capacities MUST be supplied from inspected game map data. The planner has
no built-in guesses and reports a campaign as unready when data is missing.
"""
from dataclasses import dataclass
import random

MAX_MAPS = 32
MAX_SHARDS = 30

class CampaignError(ValueError):
    pass

def choose_conquest_maps(capacities, target, seed):
    """Find distinct maps with sum>=target; prefer least overage, then fewest maps.

    Ties use a deterministic seed-dependent shuffle. No fabricated map counts.
    """
    if not isinstance(target, int) or not 1 <= target <= MAX_SHARDS:
        raise CampaignError("Conquest shard goal must be between 1 and 30.")
    if not isinstance(capacities, dict):
        raise CampaignError("Map shard capacities must be a dictionary.")
    if len(capacities) > MAX_MAPS:
        raise CampaignError("At most 32 maps may be configured per campaign.")
    entries = []
    for name, capacity in capacities.items():
        if not isinstance(name, str) or not name.strip() or name != name.strip():
            raise CampaignError("Each map must have a nonempty, trimmed name.")
        if type(capacity) is not int or capacity < 1 or capacity > MAX_SHARDS:
            raise CampaignError("Map %r: shard count must be an integer between 1 and 30." % name)
        entries.append((name, capacity))
    rng = random.Random(str(seed))
    rng.shuffle(entries)
    # Sum -> tuple of selected map indices; keep the fewest maps for each sum.
    possibilities = {0: ()}
    for index, (_, count) in enumerate(entries):
        snapshot = list(possibilities.items())
        for total, indices in snapshot:
            candidate_total = total + count
            candidate = indices + (index,)
            if (candidate_total not in possibilities or
                    len(candidate) < len(possibilities[candidate_total])):
                possibilities[candidate_total] = candidate
    winners = [(total - target, len(indices), indices)
               for total, indices in possibilities.items() if total >= target]
    if not winners:
        raise CampaignError("Not enough verified shards: need %d, catalog contains %d." %
                            (target, sum(capacities.values())))
    _, _, chosen = min(winners, key=lambda row: (row[0], row[1], row[2]))
    return [entries[i] for i in chosen]

def plan_campaign(mode, invasion_goal, shard_goal, conquest_maps,
                  invasion_maps, seed):
    if mode not in ("Invasion", "Conquest", "Hybrid"):
        raise CampaignError("Unsupported game mode: %r" % mode)
    result = {"ready": True, "warning": "", "mode": mode,
              "missions": [], "invasion_goal": int(invasion_goal),
              "conquest_shards": int(shard_goal)}
    want_invasion = mode in ("Invasion", "Hybrid")
    want_conquest = mode in ("Conquest", "Hybrid")
    if want_invasion:
        maps = sorted(set(invasion_maps))
        if not maps:
            result["ready"] = False
            result["warning"] = "No verified Invasion map configured."
        else:
            picked = random.Random(str(seed) + ":invasion").choice(maps)
            result["missions"].append({
                "id": "invasion_1", "mode": "Invasion", "map": picked,
                "wave_goal": int(invasion_goal), "shard_start": 0,
                "shard_count": 0, "required": True
            })
    if want_conquest:
        try:
            maps = choose_conquest_maps(conquest_maps, int(shard_goal), seed)
        except CampaignError as exc:
            result["ready"] = False
            result["warning"] = (result["warning"] + " " + str(exc)).strip()
        else:
            first_shard = 1
            for i, (name, count) in enumerate(maps, 1):
                result["missions"].append({
                    "id": "conquest_%d" % i, "mode": "Conquest", "map": name,
                    "wave_goal": 0, "shard_start": first_shard,
                    "shard_count": min(count, int(shard_goal) - first_shard + 1),
                    "required": True
                })
                first_shard += count
    result["mission_count"] = len(result["missions"])
    return result
