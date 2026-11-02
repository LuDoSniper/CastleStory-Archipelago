"""Seed-deterministic Castle Story campaign selection using stable map IDs.

Map IDs are the game asset's non-localized map folder identity, not the
translated title displayed in the Castle Story map picker.
"""
import hashlib
import random

MAX_MAPS = 32
MAX_SHARDS = 30

class CampaignError(ValueError):
    pass

def normalize_map_id(value):
    """Canonicalize a map folder/asset ID without translating a display label."""
    if not isinstance(value, str) or not value.strip():
        raise CampaignError("Map IDs must be non-empty strings.")
    # Forward/backslash are separators: preserve the last component for older
    # manifest values but never interpret a translated display label as an ID.
    name = value.strip().replace("\\", "/").rstrip("/").split("/")[-1]
    if name in (".", "..") or not name:
        raise CampaignError("Invalid map ID: %r" % value)
    return name

def seed_rng(seed, purpose):
    # Python hash() is process-randomized; a stable SHA256 seed works across
    # Python versions, machines and server restarts.
    digest = hashlib.sha256(("%s|%s" % (seed, purpose)).encode()).digest()
    return random.Random(int.from_bytes(digest, "big"))

def choose_conquest_maps(capacities, target, seed, diversity=0):
    """Choose distinct map IDs covering the goal without invented shard counts.

    With diversity=0 the algorithm finds minimum overage, then minimum maps.
    With diversity>0 it chooses among near-optimal subsets so distinct seeds
    can vary, always satisfying the capacity goal.
    """
    if type(target) is not int or not 1 <= target <= MAX_SHARDS:
        raise CampaignError("Conquest shard goal must be between 1 and 30.")
    if not isinstance(capacities, dict):
        raise CampaignError("Map shard capacities must be a dictionary.")
    if len(capacities) > MAX_MAPS:
        raise CampaignError("At most 32 maps may be configured per campaign.")
    entries = []
    seen = set()
    for key, capacity in capacities.items():
        name = normalize_map_id(key)
        if name.casefold() in seen:
            raise CampaignError("Repeated map ID: " + name)
        seen.add(name.casefold())
        if type(capacity) is not int or not 1 <= capacity <= MAX_SHARDS:
            raise CampaignError("Map %r needs a verified count from 1 to 30." % name)
        entries.append((name, capacity))
    entries.sort(key=lambda x: x[0].casefold())
    rng = seed_rng(seed, "conquest-map-choice")
    rng.shuffle(entries)

    # Dynamic-programming choice of one near-optimal subset per capacity.
    possibilities = {0: ()}
    for index, (_, count) in enumerate(entries):
        for total, indices in list(possibilities.items()):
            candidate_total = total + count
            candidate = indices + (index,)
            old = possibilities.get(candidate_total)
            if old is None or len(candidate) < len(old):
                possibilities[candidate_total] = candidate
    candidates = [(total, indices) for total, indices in possibilities.items()
                  if total >= target]
    if not candidates:
        raise CampaignError("Not enough verified Conquest shards: need %d, catalog has %d." %
                            (target, sum(capacities.values())))
    best_overage = min(total - target for total, _ in candidates)
    tolerance = max(0, int(diversity))
    eligible = [(total, indices) for total, indices in candidates
                if total - target <= best_overage + tolerance]
    # Limit additional map count to one over the shortest suitable combination.
    fewest = min(len(indices) for _, indices in eligible)
    eligible = [(total, indices) for total, indices in eligible
                if len(indices) <= fewest + (1 if tolerance else 0)]
    selected_total, selected = rng.choice(eligible)
    return [entries[i] for i in selected]

def plan_campaign(mode, invasion_goal, shard_goal, conquest_maps, invasion_maps, seed):
    if mode not in ("Invasion", "Conquest", "Hybrid"):
        raise CampaignError("Unsupported game mode: %r" % mode)
    result = {"ready": True, "warning": "", "mode": mode, "missions": [],
              "invasion_goal": int(invasion_goal), "conquest_shards": int(shard_goal),
              "map_identity_version": 2}
    warning = []
    if mode in ("Invasion", "Hybrid"):
        pool = sorted(set(normalize_map_id(x) for x in invasion_maps),
                      key=str.casefold)
        if not pool:
            warning.append("No Invasion map IDs configured. Add verified invasion_map_pool entries.")
        else:
            picked = seed_rng(seed, "invasion-map-choice").choice(pool)
            result["missions"].append({
                "id": "invasion_1", "mode": "Invasion",
                "map_id": picked, "map": picked, "wave_goal": int(invasion_goal),
                "shard_start": 0, "shard_count": 0, "required": True,
            })
    if mode in ("Conquest", "Hybrid"):
        try:
            maps = choose_conquest_maps(conquest_maps, int(shard_goal), seed,
                                        diversity=max(1, int(shard_goal) // 4))
        except CampaignError as exc:
            warning.append(str(exc))
        else:
            first = 1
            for i, (map_id, count) in enumerate(maps, 1):
                take = min(count, int(shard_goal) - first + 1)
                result["missions"].append({
                    "id": "conquest_%d" % i, "mode": "Conquest",
                    "map_id": map_id, "map": map_id, "wave_goal": 0,
                    "shard_start": first, "shard_count": take, "required": True,
                })
                first += take
    if mode == "Hybrid":
        branch_types = {m["mode"] for m in result["missions"]}
        if branch_types != {"Invasion", "Conquest"}:
            warning.append("Hybrid requires at least one Invasion AND one Conquest mission.")
    result["mission_count"] = len(result["missions"])
    result["ready"] = not warning
    result["warning"] = " ".join(warning)
    return result
