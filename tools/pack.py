from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

root = Path(__file__).resolve().parents[1]
destination = root / "castle_story.apworld"
with ZipFile(destination, "w", ZIP_DEFLATED) as archive:
    for path in sorted((root / "apworld/castle_story").rglob("*")):
        if path.is_file() and "__pycache__" not in path.parts and path.suffix != ".pyc":
            archive.write(path, path.relative_to(root / "apworld"))
print(destination)
