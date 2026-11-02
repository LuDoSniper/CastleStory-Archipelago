"""Inspect Castle Story map files without publishing assets.

This intentionally does NOT infer shard counts from arbitrary JSON keys:
LuaMarkersCount.json, crystals.json and gameobjects.json are not interchangeable
and may mix Home Crystals with capturable shards. The report is a *candidate*
inventory and must be validated against the actual map.
"""
import argparse
import json
from pathlib import Path

ROOTS = ("Info/Maps", "Info/UGC/Maps")
FILES = ("meta.json", "LuaMarkersCount.json", "crystals.json", "gameobjects.json")

def scan(game_root):
    entries = []
    for relative in ROOTS:
        base = Path(game_root) / relative
        if not base.is_dir():
            continue
        for folder in sorted(p for p in base.iterdir() if p.is_dir()):
            entry = {"name": folder.name, "source": relative, "verified_shards": None}
            entry["present_files"] = [n for n in FILES if (folder / n).is_file()]
            meta = folder / "meta.json"
            if meta.is_file():
                try:
                    obj = json.loads(meta.read_text(encoding="utf-8-sig"))
                    if isinstance(obj, dict):
                        entry["metadata_keys"] = sorted(obj.keys())
                except (ValueError, OSError):
                    entry["metadata_keys"] = ["unreadable"]
            entries.append(entry)
    return entries

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("game_root", help="Castle Story install directory")
    parser.add_argument("--output", default="castle_story_map_inventory.json")
    args = parser.parse_args()
    maps = scan(args.game_root)
    report = {
        "status": "NEEDS_SHARD_COUNT_VALIDATION",
        "map_count": len(maps),
        "maps": maps,
        "note": "Counts are intentionally null until individual maps are verified."
    }
    Path(args.output).write_text(json.dumps(report, indent=2, ensure_ascii=False),
                                 encoding="utf-8")
    print("Wrote %s; found %d candidate map folders." % (args.output, len(maps)))

if __name__ == "__main__":
    main()
