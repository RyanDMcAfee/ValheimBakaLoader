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
  pack_tools.py verify-pack --zip lang-ru-1.2.0.zip [--css WebUI/app.css]
  pack_tools.py verify-ids --zip lang-ru-1.2.0.zip --catalog WebUI/i18n/en.json

build-pack and build-manifest print what they wrote as JSON. verify-pack and verify-ids
print one problem per line and exit non zero when there are any.

--css reads the stylesheet the pack is for and holds the two against each other: every
family the pack publishes has to be one the stacks for that language actually ask for,
and every family those stacks ask for has to be one somebody declares. A pack that
installs cleanly and publishes its faces under names the page never asks for is a pack
whose bytes are downloaded, stored, served and never drawn, and nothing inside the zip
can see that.

A line starting NOTE is something the reading could not settle either way. It is printed
and not counted, and the run is still clean with notes on it.
"""

import argparse
import datetime
import hashlib
import io
import json
import os
import re
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


def _listed(described):
    """The face list out of fonts.json, or None when the file is not in that shape.

    The font builder writes an object about the whole folder: the code it was built for,
    what it was built from, a "fonts" LIST of entries and the licences. The older shape
    is a plain object keyed by file name. Both are read, and the list is the one that can
    say a thing the other cannot: the same file, published twice, under two families with
    two ranges. That is how a CJK pack puts its own full width ellipsis in front of Inter
    without shipping the bytes a second time.
    """
    if isinstance(described, list):
        return described
    if isinstance(described, dict) and isinstance(described.get("fonts"), list):
        return described["fonts"]
    return None


def _faces_from_list(fonts_dir, on_disk, listed):
    """One face per ENTRY, in the order the list gives them.

    Entries may name the same file. What comes back is what the page is handed, so two
    entries over one file are two @font-face rules with one address between them, which
    is the whole point of the shape.
    """
    known = {name.lower(): name for name in on_disk}
    named = set()
    faces = []

    for position, entry in enumerate(listed, start=1):
        if not isinstance(entry, dict):
            raise ValueError("fonts.json entry %d is not an object" % position)

        said = str(entry.get("file") or "").replace("\\", "/").strip()
        if not said:
            raise ValueError("fonts.json entry %d names no file" % position)

        name = known.get(os.path.basename(said).lower())
        if name is None:
            raise ValueError(
                "fonts.json entry %d names %s and there is no such face in %s"
                % (position, os.path.basename(said), fonts_dir))

        path = os.path.join(fonts_dir, name)
        digest = _digest_file(path)

        # The digest is worked out from the file every time. A stated one is read as a
        # claim about that file and checked against it, because a pack whose published
        # digest and published bytes disagree is refused by the installer, and cutting
        # time is the cheap place to find that out.
        stated = str(entry.get("sha256") or "").strip().lower()
        if stated and stated != digest:
            raise ValueError(
                "fonts.json says %s is %s and the file on disk is %s" % (name, stated, digest))

        face = {
            "file": "%s/%s" % (FONTS_DIR, name),
            "family": entry.get("family") or os.path.splitext(name)[0],
            "weight": str(entry.get("weight") or "400"),
            "style": entry.get("style") or "normal",
            "sha256": digest,
        }
        if entry.get("unicodeRange"):
            face["unicodeRange"] = entry["unicodeRange"]

        faces.append(face)
        named.add(name)

    # A face in the folder that no entry names would be packed by nobody and paid for by
    # nobody, which is a build that quietly ships less than it was asked to.
    missed = [name for name in on_disk if name not in named]
    if missed:
        raise ValueError(
            "fonts.json does not name %s, which is in %s"
            % (", ".join(missed), fonts_dir))

    return faces


def _faces_from_files(fonts_dir, on_disk, described):
    """One face per FILE, with whatever a fonts.json keyed by file name says about it."""
    faces = []
    for name in on_disk:
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

    return faces


def font_faces(fonts_dir):
    """Every face the folder publishes, with whatever the folder says about it.

    fonts.json beside the faces is optional and carries the parts a file name cannot:
    the family the page asks for, the weights the face covers, and the unicode range
    when it is a subset. Without it the family is the file name and the face is a
    normal 400, which is enough to install and enough for a fixture.

    Two shapes are read. A "fonts" LIST is one face per entry, entries may share a file,
    and it is what the font builder writes. An object keyed by file name is one face per
    file, which is the older shape and still works.
    """
    if not fonts_dir or not os.path.isdir(fonts_dir):
        return [], []

    contents = sorted(os.listdir(fonts_dir))
    on_disk = [name for name in contents if name.lower().endswith((".woff2", ".woff"))]

    described = None
    meta_path = os.path.join(fonts_dir, "fonts.json")
    if os.path.isfile(meta_path):
        described = _read_json(meta_path)

    listed = _listed(described)
    if listed is None:
        faces = _faces_from_files(
            fonts_dir, on_disk, described if isinstance(described, dict) else {})
    else:
        faces = _faces_from_list(fonts_dir, on_disk, listed)

    licences = [
        os.path.join(fonts_dir, name)
        for name in contents
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
    # Two faces may be one file published twice, so the bytes go in once. A zip with the
    # same name in it twice is a zip whose readers disagree about which one they got.
    packed = set()
    for face in faces:
        if face["file"] in packed:
            continue
        packed.add(face["file"])

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


# ---------------------------------------------------------------- the stylesheet's side
#
# A pack can be perfectly well formed, install cleanly, hash correctly and still draw
# nothing: the page only ever asks for the families app.css names, so a pack that
# publishes its faces under any other name is a pack whose bytes are downloaded, stored,
# served and never used. Nothing in the pack can see that, because the names it would
# have to be held against live in the stylesheet. So the stylesheet is read here.

# The Windows faces the stacks fall back to on purpose, and the generic keywords. These
# are the only names in a stack that are allowed to have no rule behind them.
SYSTEM_FACES = {
    "georgia",
    "times new roman",
    "segoe ui",
    "consolas",
    "cascadia mono",
    "yu gothic ui",
    "microsoft yahei ui",
    "microsoft jhenghei ui",
    "segoe ui historic",
    # the generic keywords, which name a family the browser picks rather than one anybody ships
    "serif",
    "sans-serif",
    "monospace",
    "cursive",
    "fantasy",
    "system-ui",
    "ui-serif",
    "ui-sans-serif",
    "ui-monospace",
    "ui-rounded",
    "emoji",
    "math",
    "fangsong",
}

# The four variables every font-family rule in the product goes through.
FONT_VARS = ("--serif", "--serif-small", "--sans", "--mono")

# What a language is actually written in, as a couple of characters to hold a range
# against. A code that is not here is checked for names and not for coverage.
SCRIPT_SAMPLES = {
    "ru": ("Cyrillic", (0x0410, 0x0430)),
    "ja": ("Japanese", (0x3042, 0x4E00)),
    "zh-Hans": ("Han", (0x4E00, 0x6C34)),
    "zh-Hant": ("Han", (0x4E00, 0x6C34)),
}


def _without_comments(css):
    return re.sub(r"/\*.*?\*/", " ", css, flags=re.S)


def _balanced(css, open_at):
    """The body of the block that opens at open_at, and the index just past its close."""
    depth = 0
    for index in range(open_at, len(css)):
        if css[index] == "{":
            depth += 1
        elif css[index] == "}":
            depth -= 1
            if depth == 0:
                return css[open_at + 1:index], index + 1
    return css[open_at + 1:], len(css)


def _rules(css):
    """Every (selector, declarations) pair, including the ones inside an at-rule."""
    found = []
    start = 0
    index = 0
    while index < len(css):
        char = css[index]
        if char == "{":
            selector = css[start:index].strip()
            body, after = _balanced(css, index)
            if selector.startswith("@") and "{" in body:
                found.extend(_rules(body))
            else:
                found.append((selector, body))
            index = after
            start = after
            continue
        if char == "}":
            start = index + 1
        index += 1

    return found


def _declarations(body):
    """The declarations in a block, as a dict, last one winning the way CSS does."""
    out = {}
    for piece in body.split(";"):
        if ":" not in piece:
            continue
        name, _, value = piece.partition(":")
        out[name.strip().lower()] = value.strip()
    return out


def _selects(selector, wanted):
    """True when this rule's selector list holds exactly the selector asked for."""
    return any(part.strip() == wanted for part in selector.split(","))


def _vars_for(rules, wanted):
    """Every font variable set by the rules whose selector is exactly this one."""
    out = {}
    for selector, body in rules:
        if not _selects(selector, wanted):
            continue
        for name, value in _declarations(body).items():
            if name in FONT_VARS:
                out[name] = value
    return out


def _resolve(values, name, seen=None):
    """A stack with its var() references followed, so --serif-small reads as a stack."""
    seen = seen or set()
    if name in seen:
        return ""
    seen.add(name)

    value = values.get(name)
    if not value:
        return ""

    match = re.fullmatch(r"var\(\s*(--[A-Za-z0-9_-]+)\s*\)", value.strip())
    if match:
        return _resolve(values, match.group(1), seen)
    return value


def _families(stack):
    """The family names in a stack, in order, with the quotes off."""
    names = []
    for piece in stack.split(","):
        piece = piece.strip().strip("'\"").strip()
        if piece:
            names.append(piece)
    return names


def _ranges(text):
    """A unicode-range value as a list of pairs. None means the face covers everything."""
    if not text:
        return None

    spans = []
    for piece in str(text).split(","):
        piece = piece.strip().upper().lstrip("U+")
        if not piece:
            continue
        low, _, high = piece.partition("-")
        try:
            first = int(low.replace("?", "0"), 16)
            last = int((high or low).replace("?", "F"), 16)
        except ValueError:
            continue
        spans.append((first, last))

    return spans or None


def _covers(spans, samples):
    """True when a face declared over these ranges is asked for any of these characters.

    A face declared with no unicode-range at all is asked for every character there is,
    so it is covering by definition and None answers True. What it actually HOLDS is
    another question, and one this check cannot reach: a full font does hold the script
    and a subset published without its range does not, and the declaration reads the same
    either way. The caller keeps that apart and notes it rather than counting it.
    """
    if spans is None:
        return True
    return any(first <= point <= last for point in samples for first, last in spans)


def _css_faces(rules):
    """The families app.css declares itself, each with the ranges it declares them over."""
    declared = {}
    for selector, body in rules:
        if selector.strip().lower() != "@font-face":
            continue
        said = _declarations(body)
        family = said.get("font-family", "").strip().strip("'\"").strip().lower()
        if not family:
            continue
        declared.setdefault(family, []).append(_ranges(said.get("unicode-range")))
    return declared


def _css_problems(code, faces, css_path, notes):
    """The pack and the stylesheet, held against each other for one language.

    Answers the problems. Anything it could not actually prove either way is appended to
    notes, which the caller prints and does not count.
    """
    problems = []

    try:
        with open(css_path, "r", encoding="utf-8") as handle:
            css = handle.read()
    except OSError as error:
        return ["%s could not be read: %s" % (css_path, error)]

    rules = _rules(_without_comments(css))
    css_declared = _css_faces(rules)

    values = dict(_vars_for(rules, ":root"))
    values.update(_vars_for(rules, 'html[data-lang="%s"]' % code))
    if not values:
        return ["%s names no font stacks, so there is nothing to hold the pack against" % css_path]

    stacks = {name: _families(_resolve(values, name)) for name in FONT_VARS}
    asked = {family.lower() for names in stacks.values() for family in names}

    packed = {}
    spelled = []
    for face in faces:
        family = str(face.get("family") or "").strip()
        if not family:
            continue
        if family.lower() not in packed:
            spelled.append(family)
        packed.setdefault(family.lower(), []).append(_ranges(face.get("unicodeRange")))

    # 1. a family the pack publishes that no stack asks for is a face nobody will draw
    for family in spelled:
        if family.lower() not in asked:
            problems.append(
                "the pack publishes %r and no stack that applies to %s asks for it" % (family, code))

    # 2. a family in a stack that nothing declares falls through to whatever Windows has
    for name in FONT_VARS:
        for family in stacks[name]:
            key = family.lower()
            if key in SYSTEM_FACES or key in packed or key in css_declared:
                continue
            problems.append(
                "the %s stack for %s names %r, which neither app.css nor the pack declares"
                % (name, code, family))

    # 3. a stack with no face behind the language's own script draws it in a system face
    script = SCRIPT_SAMPLES.get(code)
    if script:
        label, samples = script
        for name in FONT_VARS:
            covered = False
            unproved = None
            for family in stacks[name]:
                key = family.lower()
                for spans in packed.get(key, []) + css_declared.get(key, []):
                    if not _covers(spans, samples):
                        continue
                    if spans is None:
                        # Declared over everything, so the stack does reach it and the
                        # check passes. Whether the file behind it holds a single glyph
                        # of this script is not written down anywhere this can read.
                        if unproved is None:
                            unproved = family
                        continue
                    covered = True
                    break
                if covered:
                    break

            if covered:
                continue
            if unproved is not None:
                notes.append(
                    "the %s stack for %s reaches %r, which is declared with no unicode range, so "
                    "it is taken as covering %s and this check did not prove that it does"
                    % (name, code, unproved, label))
                continue
            problems.append(
                "the %s stack for %s carries no face over %s, so it falls to the system face"
                % (name, code, label))

    return problems


def verify_pack(zip_path, css_path=None, notes=None):
    """Reads a built pack back the way the installer reads it. Answers a list of problems.

    Anything the reading could not settle either way is appended to notes when one is
    handed in. A note is not a problem and is never counted as one.
    """
    problems = []
    if notes is None:
        notes = []

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

            if css_path:
                problems.extend(_css_problems(code, faces, css_path, notes))
    except zipfile.BadZipFile as error:
        return ["%s is not a readable zip: %s" % (zip_path, error)]

    return problems


def catalog_ids(text):
    """The ids a catalog holds, as a set.

    A catalog with no keys block is said so rather than answered as an empty set: an
    empty set compares as every id missing, which reads like a translation disaster and
    is really a file in the wrong shape.
    """
    keys = json.loads(text).get("keys")
    if not isinstance(keys, dict):
        raise ValueError("there is no keys block in it")
    return set(keys.keys())


def verify_ids(zip_path, catalog_path):
    """The ids in a built pack, held against the English catalog it was cut from.

    The COUNT is not the question and never was. A pack that lost one id and gained
    another counts exactly right, and what a host gets is one line stuck in English
    beside a translation nothing will ever look up. So the two sets are compared, and
    the number the English catalog happens to hold today is never written down here.
    """
    if not os.path.isfile(zip_path):
        return ["%s is not a file" % zip_path]

    try:
        with zipfile.ZipFile(zip_path) as zip_file:
            if STRINGS_NAME not in set(zip_file.namelist()):
                return ["%s is missing from %s" % (STRINGS_NAME, zip_path)]
            theirs = catalog_ids(zip_file.read(STRINGS_NAME).decode("utf-8"))
    except (zipfile.BadZipFile, ValueError) as error:
        return ["%s does not read as a pack: %s" % (zip_path, error)]

    try:
        with open(catalog_path, "r", encoding="utf-8") as handle:
            english = catalog_ids(handle.read())
    except (OSError, ValueError) as error:
        return ["%s could not be read: %s" % (catalog_path, error)]

    problems = []
    missing = sorted(english - theirs)
    extra = sorted(theirs - english)

    if missing:
        problems.append(
            "%d id(s) in %s are not in the pack, among them %s"
            % (len(missing), catalog_path, ", ".join(missing[:8])))
    if extra:
        problems.append(
            "%d id(s) in the pack are in no catalog in the tree, among them %s"
            % (len(extra), ", ".join(extra[:8])))

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
    verify.add_argument(
        "--css", default=None,
        help="hold the families the pack publishes against the stacks in this stylesheet")

    ids = commands.add_parser("verify-ids", help="hold a pack's ids against the English catalog")
    ids.add_argument("--zip", required=True)
    ids.add_argument("--catalog", required=True, help="the English catalog to compare against")

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

    if args.command == "verify-ids":
        problems = verify_ids(args.zip, args.catalog)
        for problem in problems:
            print(problem)
        print("TOTAL %d" % len(problems))
        return 1 if problems else 0

    notes = []
    problems = verify_pack(args.zip, css_path=args.css, notes=notes)
    for note in notes:
        print("NOTE %s" % note)
    for problem in problems:
        print(problem)
    print("TOTAL %d" % len(problems))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(_cli(sys.argv[1:]))
