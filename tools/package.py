"""Check distributable files and optionally create a reproducible game ZIP."""

import argparse
import hashlib
import json
import re
from pathlib import Path
import xml.etree.ElementTree as ET
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED

ROOT = Path(__file__).resolve().parent.parent
CONFIG = json.loads((ROOT / "tools/public-package.json").read_text(encoding="utf-8"))
MOD = ROOT / CONFIG["mod_path"]


def check():
    about = ET.parse(MOD / "About/About.xml")
    for key, expected in {
        "packageId": CONFIG["package_id"], "author": "Vragerr",
        "modVersion": CONFIG["version"]
    }.items():
        if about.findtext(key) != expected:
            raise ValueError(f"Unexpected {key}: {about.findtext(key)}")
    if [node.text for node in about.findall("supportedVersions/li")] != ["1.7"]:
        raise ValueError("Expected RimWorld 1.7.")
    for relative in CONFIG["files"]:
        file = MOD / relative
        if file.suffix == ".dll":
            continue
        if not file.is_file() or not file.resolve().is_relative_to(MOD.resolve()):
            raise ValueError(f"Missing or unsafe path: {relative}")
        if file.suffix == ".xml":
            ET.parse(file)
    expected = set(CONFIG["files"])
    found = {p.relative_to(MOD).as_posix() for p in MOD.rglob("*") if p.is_file()}
    if found - expected:
        raise ValueError(f"Unexpected mod files: {sorted(found - expected)}")
    keys = {}
    for locale in ("English", "Russian"):
        values = {}
        for file in (MOD / "Languages" / locale / "Keyed").glob("*.xml"):
            for item in ET.parse(file).getroot():
                if item.tag in values or not item.text:
                    raise ValueError(f"{locale}: invalid key {item.tag}")
                values[item.tag] = sorted(re.findall(r"\{\d+\}", item.text))
        keys[locale] = values
    for key in keys["English"].keys() | keys["Russian"].keys():
        if any(group in key for group in (".Settings.", ".Tab.", ".Diagnostics.", ".Anima.", ".Vent.", ".ColumnPipe.")):
            if keys["English"].get(key) != keys["Russian"].get(key):
                raise ValueError(f"Missing translation or placeholder mismatch: {key}")
    print(f"{CONFIG['package_id']}: XML, localization and mod inventory OK")


def archive(output):
    members = {}
    for relative in CONFIG["files"]:
        file = MOD / relative
        if not file.resolve().is_relative_to(MOD.resolve()):
            raise ValueError(f"Unsafe archive path: {relative}")
        contents = file.read_bytes()
        if file.suffix == ".dll" and contents[:2] != b"MZ":
            raise ValueError(f"Invalid assembly: {relative}")
        members[f"{CONFIG['folder']}/{relative}"] = contents
    output.mkdir(parents=True, exist_ok=True)
    path = output / f"{CONFIG['repository']}-v{CONFIG['version']}.zip"
    with ZipFile(path, "w", compression=ZIP_DEFLATED, compresslevel=9) as zip_file:
        for name, data in sorted(members.items()):
            info = ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            zip_file.writestr(info, data, compress_type=ZIP_DEFLATED, compresslevel=9)
    with ZipFile(path) as zip_file:
        if zip_file.testzip() is not None or any(zip_file.read(name) != data for name, data in members.items()):
            raise ValueError(f"Archive verification failed: {path}")
    checksum = hashlib.sha256(path.read_bytes()).hexdigest()
    path.with_suffix(".zip.sha256").write_text(f"{checksum}  {path.name}\n", encoding="utf-8")
    print(f"{checksum}  {path.name}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, help="Write a game ZIP and its SHA-256 after local compilation.")
    args = parser.parse_args()
    check()
    if args.output:
        archive(args.output)
