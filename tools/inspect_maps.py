"""Inspect Castle Story map folders and produce an evidence-backed local catalog.

No proprietary map payload is copied into the report. Values are
*unverified* until explicitly confirmed by the player using the built-in
world editor or a proven marker schema.
"""
import argparse
import json
from pathlib import Path

ROOTS = ("Info/Maps", "Info/UGC/Maps")
FILES = ("meta.json", "LuaMarkersCount.json", "Factions.json", "gameobjects.json")

def read_json(path):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (ValueError, OSError, UnicodeError):
        return None

def marker_evidence(data):
    """Return relevant counts with full marker paths; not inferred shard totals."""
    entries = []
    def walk(node, keys, depth):
        if depth > 10 or len(entries) >= 100:
            return
        if isinstance(node, dict):
            for key, value in node.items():
                if not isinstance(key, str):
                    continue
                label = key.lower()
                if type(value) is int and any(term in label for term in
                                               ("shard", "crystal", "capture")):
                    entries.append({"marker": ".".join(keys + [key]), "count": value})
                elif isinstance(value, (dict, list)):
                    walk(value, keys + [key], depth + 1)
        elif isinstance(node, list):
            for index, value in enumerate(node[:150]):
                walk(value, keys + [str(index)], depth + 1)
    walk(data, [], 0)
    return entries

def scan(game_root):
    root = Path(game_root)
    results = []
    for relative in ROOTS:
        base = root / relative
        if not base.is_dir():
            continue
        for folder in sorted((p for p in base.iterdir() if p.is_dir()), key=lambda p:p.name.casefold()):
            meta = read_json(folder / "meta.json")
            markers = read_json(folder / "LuaMarkersCount.json")
            results.append({
                "name": folder.name,
                "source": relative,
                "present_files": [f for f in FILES if (folder / f).is_file()],
                "metadata_keys": sorted(meta.keys()) if isinstance(meta, dict) else [],
                "marker_evidence": marker_evidence(markers),
                "verified_shards": None,
            })
    # The in-game native map asset registry is language-independent and can
    # include built-in maps packaged without corresponding Info/Maps folders.
    registry = root / "BepInEx/config/castle_story_archipelago/installed_maps.json"
    native = read_json(registry)
    if isinstance(native, dict) and isinstance(native.get("map_ids"), list):
        seen = {entry["name"].casefold() for entry in results}
        for key in native["map_ids"]:
            if not isinstance(key, str) or not key.strip():
                continue
            if key.casefold() in seen:
                for item in results:
                    if item["name"].casefold() == key.casefold():
                        item["verified_in_unity"] = True
                continue
            seen.add(key.casefold())
            results.append({
                "name": key,
                "source": "Castle Story native Asset_Map registry",
                "present_files": [],
                "metadata_keys": [],
                "marker_evidence": [],
                "verified_shards": None,
                "verified_in_unity": True,
            })
    return sorted(results, key=lambda e: e["name"].casefold())

def validate_approvals(entries, approvals):
    """Approve counts by exact installed map identity; never silently infer them."""
    catalog = {}
    available = {entry["name"]: entry for entry in entries}
    for name, count in approvals.items():
        if name not in available:
            raise ValueError("Unknown installed map: " + repr(name))
        if type(count) is not int or not 1 <= count <= 30:
            raise ValueError("Shard count must be 1..30 for " + repr(name))
        if ("LuaMarkersCount.json" not in available[name]["present_files"] and
                not available[name].get("verified_in_unity")):
            raise ValueError("Missing marker/native map evidence for: " + repr(name))
        catalog[name] = count
    return dict(sorted(catalog.items()))

def yaml_fragment(approved, invasion_ids=None):
    """YAML options with stable map IDs, not translated names."""
    invasion_ids = sorted(set(invasion_ids or []))
    lines = ["  invasion_map_pool:"]
    if not invasion_ids:
        lines = ["  invasion_map_pool: []"]
    else:
        lines.extend("    - " + json.dumps(name, ensure_ascii=False)
                     for name in invasion_ids)
    if approved:
        lines.append("  conquest_map_capacities:")
        for name, count in sorted(approved.items()):
            lines.append("    %s: %d" % (json.dumps(name, ensure_ascii=False), count))
    else:
        lines.append("  conquest_map_capacities: {}")
    return "\n".join(lines) + "\n"

def main():
    parser = argparse.ArgumentParser(description="Castle Story map inventory and YAML catalog")
    parser.add_argument("game_root")
    parser.add_argument("--output", default="castle_story_map_inventory.json")
    parser.add_argument("--approve", action="append", default=[], metavar="MAP=SHARDS",
                        help="Manually verified shard count for an installed map")
    parser.add_argument("--invasion", action="append", default=[], metavar="MAP_ID",
                        help="Mark a native/installed map ID as supporting Invasion") 
    parser.add_argument("--yaml-output", default="castle_story_map_options.yaml")
    args = parser.parse_args()
    entries = scan(args.game_root)
    approvals = {}
    for entry in args.approve:
        if "=" not in entry:
            parser.error("Use --approve 'Map Name=5'")
        name, value = entry.rsplit("=",1)
        if name in approvals:
            parser.error("Duplicate map approval: " + name)
        try:
            approvals[name] = int(value)
        except ValueError:
            parser.error("Non-integer shard count: " + value)
    try:
        approved = validate_approvals(entries, approvals)
    except ValueError as error:
        parser.error(str(error))
    installed = {entry["name"] for entry in entries}
    for key in args.invasion:
        if key not in installed:
            parser.error("Unknown installed Invasion map ID: " + key)
    for entry in entries:
        if entry["name"] in approved:
            entry["verified_shards"] = approved[entry["name"]]
        if entry["name"] in args.invasion:
            entry["invasion_approved"] = True
    report = {"status": "VERIFIED_PARTIAL" if approved else "NEEDS_SHARD_COUNT_VALIDATION",
              "map_count": len(entries), "verified_maps": len(approved),
              "maps": entries,
              "note": "Marker evidence is diagnostic. Counts are only assigned with --approve."}
    Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2),
                                 encoding="utf-8")
    Path(args.yaml_output).write_text(yaml_fragment(approved, args.invasion), encoding="utf-8")
    print("Scanned %d map IDs, confirmed %d Conquest counts, %d Invasion maps. " 
          "Report: %s; YAML: %s" %
          (len(entries), len(approved), len(args.invasion), args.output, args.yaml_output))

if __name__ == "__main__":
    main()
