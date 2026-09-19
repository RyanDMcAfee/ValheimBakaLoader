"""Builds a language pack, and the manifest a release publishes beside it.

One script does both halves on purpose. The release step cuts real packs with it and
the test suite builds its fixtures with it, so what the suite proves installable is
the same shape the release produces, by construction rather than by agreement.

A pack is a zip:

    pack.json          what this pack says about itself
    strings.json       the catalog for this language
    fonts/*.woff2      only the faces this language needs
    fonts/OFL.txt      the licences, shipped with the fonts

and lang-manifest.json is one small file per release that names every pack, its
address, its size and its digest. The app reads the address off that entry and never
builds one: a version spelled "v1.2.0" on one side and "1.2.0" on the other is a
silent 404 that reads to a host as "that language does not exist".

Usage:
  pack_tools.py build-pack --code ru --strings STRINGS.json --fonts DIR
                           --app-version 1.2.0 --out DIR [--status machine]
                           [--base-url URL] [--min-app-version 1.2.0]
  pack_tools.py build-manifest --entry ENTRY.json [--entry ...] --app-version 1.2.0
                               --base-url URL --out lang-manifest.json
  pack_tools.py verify-pack --zip lang-ru-1.2.0.zip

build-pack and build-manifest print what they wrote as JSON. verify-pack prints one
problem per line and exits non zero when there are any.
"""

import argparse
import datetime
import hashlib
import io
import json
import os
import sys
import zipfile

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

SCHEMA = 1
PACK_NAME = "pack.json"
STRINGS_NAME = "strings.json"
FONTS_DIR = "fonts"
STATUSES = ("machine", "reviewed")

# The same five the app compiles in (Tools/LanguageCodes.cs). The pseudo locale is a
# test fixture rather than a language and is never listed, but it can still be packed
# by name so the suite can drive a real install with it.
KNOWN = {
    "en": ("English", "English"),
    "ru": ("Русский", "Russian"),
    "ja": ("日本語", "Japanese"),
    "zh-Hans": ("简体中文", "Simplified Chinese"),
    "zh-Hant": ("繁體中文", "Traditional Chinese"),
    "xx": ("Pseudo", "Pseudo"),
}


def _read_json(path):
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def _digest_bytes(data):
    return hashlib.sha256(data).hexdigest()


def _digest_file(path):
    sha = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            sha.update(block)
    return sha.hexdigest()


def asset_name(code, app_version):
    """The published file name. Lower case, so nothing in the chain depends on case."""
    return "lang-%s-%s.zip" % (code.lower(), app_version)


def count_keys(catalog):
    """How many ids the catalog holds, and how many of them actually carry words.

    An entry is a small object with one value per register ("lore" and "plain") or a
    single "translation". A key whose registers are all empty is a key the pack has
    not translated yet, and the note the menu shows counts exactly those.
    """
    keys = catalog.get("keys") or {}
    total = 0
    translated = 0

    for value in keys.values():
        total += 1
        if isinstance(value, str):
            if value.strip():
                translated += 1
            continue
        if isinstance(value, dict):
            written = [v for v in value.values() if isinstance(v, str) and v.strip()]
            # A plural entry is an object of categories under a register, so one level
            # deeper still counts as written.
            nested = [
                inner
                for v in value.values()
                if isinstance(v, dict)
                for inner in v.values()
                if isinstance(inner, str) and inner.strip()
            ]
            if written or nested:
                translated += 1

    return total, translated


def font_faces(fonts_dir):
    """Every face in the folder, with whatever the folder says about it.

    fonts.json beside the faces is optional and carries the parts a file name cannot:
    the family the page asks for, the weights the face covers, and the unicode range
    when it is a subset. Without it the family is the file name and the face is a
    normal 400, which is enough to install and enough for a fixture.
    """
    if not fonts_dir or not os.path.isdir(fonts_dir):
        return [], []

    described = {}
    meta_path = os.path.join(fonts_dir, "fonts.json")
    if os.path.isfile(meta_path):
        described = _read_json(meta_path) or {}

    faces = []
    for name in sorted(os.listdir(fonts_dir)):
        if not name.lower().endswith((".woff2", ".woff")):
            continue

        path = os.path.join(fonts_dir, name)
        said = described.get(name) or {}
        face = {
            "file": "%s/%s" % (FONTS_DIR, name),
            "family": said.get("family") or os.path.splitext(name)[0],
            "weight": str(said.get("weight") or "400"),
            "style": said.get("style") or "normal",
            "sha256": _digest_file(path),
        }
        if said.get("unicodeRange"):
            face["unicodeRange"] = said["unicodeRange"]
        faces.append(face)

    licences = [
        os.path.join(fonts_dir, name)
        for name in sorted(os.listdir(fonts_dir))
        if name.lower().endswith(".txt")
    ]
    return faces, licences


def _write_zip(zip_path, files):
    """Writes the archive with a fixed order and a fixed timestamp.

    Two runs over the same inputs then produce the same bytes, which means the digest
    in the manifest is a statement about the contents rather than about the minute the
    release was cut.
    """
    stamp = (1980, 1, 1, 0, 0, 0)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zip_file:
        for name, data in files:
            info = zipfile.ZipInfo(name, date_time=stamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            zip_file.writestr(info, data)


def _checked_base_url(base_url):
    """
    The address every entry is built from, refused here when the app would refuse it
    there. The installer fetches a pack over https and nothing else, so a base url
    typed without the s cuts a manifest whose every pack is dead on arrival, and the
    first anyone hears of it is a host picking a language and being told no. Cutting
    time is the cheap place to find that out.
    """
    if base_url and not base_url.lower().startswith("https://"):
        raise ValueError(
            "the base url has to be https, because the installer will not fetch a pack "
            "from anywhere else. Got: %s" % base_url)

    return base_url


def build_pack(code, strings_json, fonts_dir, app_version, out_dir,
               status="machine", base_url=None, min_app_version=None,
               native_name=None, english_name=None):
    """Builds one pack zip and answers the manifest entry that describes it."""
    if status not in STATUSES:
        raise ValueError("status has to be one of %s" % ", ".join(STATUSES))

    base_url = _checked_base_url(base_url)

    catalog = _read_json(strings_json)
    meta = catalog.get("_meta")
    if not isinstance(meta, dict):
        raise ValueError("%s has no _meta block" % strings_json)

    # The pack says which language it holds, in both spellings the tree uses, so the
    # installer can check it against the language it asked for whichever one it reads.
    meta["lang"] = code
    meta["language"] = code
    meta["appVersion"] = app_version
    catalog["_meta"] = meta

    keys, translated = count_keys(catalog)
    faces, licences = font_faces(fonts_dir)
    names = KNOWN.get(code, (code, code))

    pack = {
        "schema": SCHEMA,
        "code": code,
        "appVersion": app_version,
        "asset": asset_name(code, app_version),
        "catalog": int(meta.get("catalog") or 0),
        "nativeName": native_name or names[0],
        "englishName": english_name or names[1],
        "keys": keys,
        "translated": translated,
        "status": status,
        "minAppVersion": min_app_version or app_version,
        "fonts": faces,
    }

    files = [
        (PACK_NAME, json.dumps(pack, ensure_ascii=False, indent=2).encode("utf-8")),
        (STRINGS_NAME, json.dumps(catalog, ensure_ascii=False, indent=2).encode("utf-8")),
    ]
    for face in faces:
        source = os.path.join(fonts_dir, os.path.basename(face["file"]))
        with open(source, "rb") as handle:
            files.append((face["file"], handle.read()))
    for licence in licences:
        with open(licence, "rb") as handle:
            files.append(("%s/%s" % (FONTS_DIR, os.path.basename(licence)), handle.read()))

    os.makedirs(out_dir, exist_ok=True)
    zip_path = os.path.join(out_dir, pack["asset"])
    _write_zip(zip_path, files)

    with open(zip_path, "rb") as handle:
        blob = handle.read()

    entry = dict(pack)
    entry["bytes"] = len(blob)
    entry["sha256"] = _digest_bytes(blob)
    if base_url:
        entry["url"] = "%s/%s" % (base_url.rstrip("/"), pack["asset"])

    return entry


def build_manifest(entries, app_version, base_url):
    """Gathers the entries into the one small file a release publishes."""
    base_url = _checked_base_url(base_url)
    languages = []
    for entry in entries:
        row = dict(entry)
        if base_url and not row.get("url"):
            row["url"] = "%s/%s" % (base_url.rstrip("/"), row["asset"])
        languages.append(row)

    languages.sort(key=lambda row: row.get("code") or "")
    catalogs = [row.get("catalog") or 0 for row in languages]

    return {
        "schema": SCHEMA,
        "appVersion": app_version,
        "generatedUtc": datetime.datetime.now(datetime.timezone.utc)
        .replace(microsecond=0)
        .isoformat()
        .replace("+00:00", "Z"),
        "catalog": max(catalogs) if catalogs else 0,
        "languages": languages,
    }


def verify_pack(zip_path):
    """Reads a built pack back the way the installer reads it. Answers a list of problems."""
    problems = []

    if not os.path.isfile(zip_path):
        return ["%s is not a file" % zip_path]

    try:
        with zipfile.ZipFile(zip_path) as zip_file:
            names = set(zip_file.namelist())

            for required in (PACK_NAME, STRINGS_NAME):
                if required not in names:
                    problems.append("%s is missing from the pack" % required)
            if problems:
                return problems

            try:
                pack = json.loads(zip_file.read(PACK_NAME).decode("utf-8"))
            except Exception as error:
                return ["%s does not parse: %s" % (PACK_NAME, error)]

            try:
                catalog = json.loads(zip_file.read(STRINGS_NAME).decode("utf-8"))
            except Exception as error:
                return ["%s does not parse: %s" % (STRINGS_NAME, error)]

            code = pack.get("code")
            if not code:
                problems.append("the pack does not say which language it holds")

            meta = catalog.get("_meta") or {}
            declared = meta.get("lang") or meta.get("language")
            if declared != code:
                problems.append(
                    "the catalog says it holds %r and the pack says %r" % (declared, code))

            if not pack.get("appVersion"):
                problems.append("the pack does not name the app version it was cut for")

            keys, translated = count_keys(catalog)
            if pack.get("keys") != keys:
                problems.append(
                    "the pack counts %s keys and the catalog holds %s" % (pack.get("keys"), keys))
            if pack.get("translated") != translated:
                problems.append(
                    "the pack counts %s translated and the catalog holds %s"
                    % (pack.get("translated"), translated))

            faces = pack.get("fonts") or []
            for face in faces:
                name = face.get("file")
                if name not in names:
                    problems.append("the pack names %s and does not carry it" % name)
                    continue
                digest = _digest_bytes(zip_file.read(name))
                if digest != face.get("sha256"):
                    problems.append("%s does not match the digest the pack published" % name)

            if faces and not any(
                    n.startswith(FONTS_DIR + "/") and n.lower().endswith(".txt") for n in names):
                problems.append("the pack carries fonts and no licence text")
    except zipfile.BadZipFile as error:
        return ["%s is not a readable zip: %s" % (zip_path, error)]

    return problems


def _cli(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)

    pack = commands.add_parser("build-pack", help="build one language pack zip")
    pack.add_argument("--code", required=True)
    pack.add_argument("--strings", required=True)
    pack.add_argument("--fonts", default=None)
    pack.add_argument("--app-version", required=True)
    pack.add_argument("--out", required=True)
    pack.add_argument("--status", default="machine", choices=list(STATUSES))
    pack.add_argument("--base-url", default=None)
    pack.add_argument("--min-app-version", default=None)
    pack.add_argument("--native-name", default=None)
    pack.add_argument("--english-name", default=None)
    pack.add_argument("--entry-out", default=None, help="also write the entry to this file")

    manifest = commands.add_parser("build-manifest", help="gather entries into lang-manifest.json")
    manifest.add_argument("--entry", action="append", required=True)
    manifest.add_argument("--app-version", required=True)
    manifest.add_argument("--base-url", default=None)
    manifest.add_argument("--out", required=True)

    verify = commands.add_parser("verify-pack", help="read a built pack back")
    verify.add_argument("--zip", required=True)

    args = parser.parse_args(argv)

    if args.command == "build-pack":
        entry = build_pack(
            args.code, args.strings, args.fonts, args.app_version, args.out,
            status=args.status, base_url=args.base_url, min_app_version=args.min_app_version,
            native_name=args.native_name, english_name=args.english_name)
        if args.entry_out:
            with open(args.entry_out, "w", encoding="utf-8") as handle:
                json.dump(entry, handle, ensure_ascii=False, indent=2)
        print(json.dumps(entry, ensure_ascii=False, indent=2))
        return 0

    if args.command == "build-manifest":
        entries = [_read_json(path) for path in args.entry]
        built = build_manifest(entries, args.app_version, args.base_url)
        with open(args.out, "w", encoding="utf-8") as handle:
            json.dump(built, handle, ensure_ascii=False, indent=2)
        print(json.dumps(built, ensure_ascii=False, indent=2))
        return 0

    problems = verify_pack(args.zip)
    for problem in problems:
        print(problem)
    print("TOTAL %d" % len(problems))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(_cli(sys.argv[1:]))
