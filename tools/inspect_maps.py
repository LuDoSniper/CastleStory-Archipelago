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
    return results

def validate_approvals(entries, approvals):
    """Approve counts by exact installed map identity; never silently infer them."""
    catalog = {}
    available = {entry["name"]: entry for entry in entries}
    for name, count in approvals.items():
        if name not in available:
            raise ValueError("Unknown installed map: " + repr(name))
        if type(count) is not int or not 1 <= count <= 30:
            raise ValueError("Shard count must be 1..30 for " + repr(name))
        if "LuaMarkersCount.json" not in available[name]["present_files"]:
            raise ValueError("Missing marker file, cannot confirm map: " + repr(name))
        catalog[name] = count
    return dict(sorted(catalog.items()))

def yaml_fragment(approved):
    """YAML object fragment for Castle Story -> conquest_map_capacities."""
    if not approved:
        return "  conquest_map_capacities: {}\n"
    lines = ["  conquest_map_capacities:"]
    for name,count in sorted(approved.items()):
        # JSON-quoted strings are valid YAML scalars.
        lines.append("    %s: %d" % (json.dumps(name, ensure_ascii=False), count))
    return "\n".join(lines) + "\n"

def main():
    parser = argparse.ArgumentParser(description="Castle Story map inventory and YAML catalog")
    parser.add_argument("game_root")
    parser.add_argument("--output", default="castle_story_map_inventory.json")
    parser.add_argument("--approve", action="append", default=[], metavar="MAP=SHARDS",
                        help="Manually verified shard count for an installed map")
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
    for entry in entries:
        if entry["name"] in approved:
            entry["verified_shards"] = approved[entry["name"]]
    report = {"status": "VERIFIED_PARTIAL" if approved else "NEEDS_SHARD_COUNT_VALIDATION",
              "map_count": len(entries), "verified_maps": len(approved),
              "maps": entries,
              "note": "Marker evidence is diagnostic. Counts are only assigned with --approve."}
    Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2),
                                 encoding="utf-8")
    Path(args.yaml_output).write_text(yaml_fragment(approved), encoding="utf-8")
    print("Scanned %d maps, confirmed %d. Report: %s; YAML: %s" %
          (len(entries), len(approved), args.output, args.yaml_output))

if __name__ == "__main__":
    main()
