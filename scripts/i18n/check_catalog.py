"""Checks a language catalog the way the lookup reads it.

Run with no arguments from the repository root it checks the shipped English
catalog and everything about it that a person cannot hold in their head:

  * it parses, every id is spelled the one way ids are spelled, and no id is
    written twice in the file (JSON quietly keeps the last one, so a duplicate
    is a sentence that silently stopped being used)
  * a plural entry carries exactly the CLDR categories its language has, asked
    of Intl.PluralRules rather than of a list somebody typed
  * the two registers of one entry have the same named slots, and a translation
    has the same slots as the English it came from, because a spreadsheet editor
    eats braces and the result renders a literal {count} on screen
  * no markup in a value unless the entry says allowsHtml
  * the words that are names rather than words survive translation
  * the long dash rules, which are a function of the language: English has a way
    to write any sentence without one, Russian does not
  * that a sentence carrying a word from the Norse register carries the plain wording
    of that sentence beside it. Three rules used to watch this, one per road a
    sentence could take into the old swap table; the table is gone and the entry is
    the only thing there is, so there is one rule and it reads the register off
    scripts/i18n/norse_terms.json. It is the half no English screenshot can show,
    because it only appears with the Norse names switched off
  * and, for English, that the catalog and the interface agree BOTH WAYS. Every
    id the interface asks for exists, and every id the catalog holds is asked
    for by something. An orphan key is a sentence nobody reads that a translator
    still pays for.

Usage:
  check_catalog.py                          the shipped catalog, every check
  check_catalog.py --dir DIR                catalogs in DIR, no completeness
  check_catalog.py --norse TERMS.json       another Norse register list
  check_catalog.py --dir DIR --app A --html H [--i18n I]   with completeness

Prints one finding per line, then TOTAL n, and exits non zero when n is not 0.
"""

import io
import json
import os
import re
import subprocess
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))

DEFAULT_DIR = os.path.join(REPO, "ValheimBakaLoader", "WebUI", "i18n")
DEFAULT_APP = os.path.join(REPO, "ValheimBakaLoader", "WebUI", "app.js")
DEFAULT_HTML = os.path.join(REPO, "ValheimBakaLoader", "WebUI", "index.html")
DEFAULT_I18N = os.path.join(REPO, "ValheimBakaLoader", "WebUI", "i18n.js")
DEFAULT_DASHES = os.path.join(REPO, "scripts", "copy-gate", "lang_dashes.json")
DEFAULT_CSPROJ = os.path.join(REPO, "ValheimBakaLoader", "ValheimBakaLoader.csproj")

# The sentences that never reach a page are asked for on the C# side, through
# HostCatalog.T("host.something"). They are in the same catalog as everything else, so
# without this the completeness check reads every one of them as a key nobody asks for and
# says so, and the day somebody believes it the restart countdown loses its words.
#
# Whole FOLDERS rather than the three files that happen to call it today. A named-file
# list is right until somebody adds a HostCatalog.T call to a second window, and then the
# id it asks for is one this completeness check never sees, so the check calls that id an
# orphan nobody asks for and somebody eventually believes it.
DEFAULT_HOST_SOURCES = []
DEFAULT_HOST_DIRS = [
    os.path.join(REPO, "ValheimBakaLoader", "Tools"),
    os.path.join(REPO, "ValheimBakaLoader", "Forms"),
    os.path.join(REPO, "ValheimBakaLoader", "Game"),
]

# An id is a stable dotted name in lower case. Never the English text: keying on
# English orphans every translation the first time somebody edits a sentence.
ID_RE = re.compile(r"^[a-z][a-z0-9_]*(?:\.[a-z0-9_]+)+$")

SLOT_RE = re.compile(r"\{([A-Za-z_][A-Za-z0-9_]*)\}")

# Markup in a value. A tag opener or a named entity; either one means the value
# is going somewhere innerHTML rather than through esc().
HTML_RE = re.compile(r"<[A-Za-z/!]|&[A-Za-z#][A-Za-z0-9]*;")

LONG_DASHES = {"‒": "U+2012", "–": "U+2013", "—": "U+2014", "―": "U+2015"}

# Names rather than words. A translation that renders these differently is
# telling a host about a product that does not exist.
DO_NOT_TRANSLATE = [
    "BakaLoader", "Valheim", "Iron Gate", "BepInEx", "Thunderstore", "Jotunn",
    "Hexium", "Discord", "Steam", "Windows", "RCON", "ZDO",
]

# The backstop for the five languages the product is planned in, used only when
# node is not on the machine. Node is asked first so the gate and the lookup are
# reading the same CLDR data rather than two copies of it.
FALLBACK_CATEGORIES = {
    "en": ["one", "other"],   # and xx, the pseudo locale built from it
    "ru": ["one", "few", "many", "other"],
    "ja": ["other"],
    "zh-hans": ["other"],
    "zh-hant": ["other"],
    "zh": ["other"],
}


def shown(path):
    """A path to print. Relative to the repository when it is inside it, and the
    whole thing when it is not, which is how a fixture under the temp folder still
    names itself instead of raising on a different drive."""
    try:
        inside = os.path.relpath(path, REPO)
    except ValueError:
        return path.replace("\\", "/")
    if inside.startswith(".."):
        return path.replace("\\", "/")
    return inside.replace("\\", "/")


def read_text(path):
    """Bytes to text, never a traceback: a gate that crashes reports nothing."""
    with open(path, "rb") as handle:
        raw = handle.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    return raw.decode("utf-8", errors="replace")


def plural_categories(language, findings):
    """The CLDR categories this language actually has.

    Asked of Intl.PluralRules through node, which is the same machinery the page
    selects with, so a plural entry cannot be complete at the gate and short in
    the window. Falls back to a written down table only when node is missing.

    The one exception is xx, the generated pseudo locale. It is not a language and
    Intl does not know it, so every runtime quietly answers with the categories of
    whatever the machine's own locale is - which would make this gate pass here and
    fail on a build machine in Tokyo. It is built from the English catalog, so it
    has English's plural shape, and that is said here rather than asked.
    """
    if str(language).lower() == "xx":
        return sorted(FALLBACK_CATEGORIES["en"])
    script = (
        "try{process.stdout.write("
        "new Intl.PluralRules(process.argv[1]).resolvedOptions()"
        ".pluralCategories.join(','))}catch(e){process.exit(3)}"
    )
    for node in ("node", "node.exe"):
        try:
            done = subprocess.run(
                [node, "-e", script, language],
                capture_output=True, text=True, timeout=30,
            )
        except (OSError, subprocess.SubprocessError):
            continue
        if done.returncode == 0 and done.stdout.strip():
            return sorted(done.stdout.strip().split(","))
        break

    key = str(language).lower()
    if key in FALLBACK_CATEGORIES:
        return sorted(FALLBACK_CATEGORIES[key])
    findings.append(("%s" % language, "no plural categories known for this language and node could not be run"))
    return ["other"]


def allowed_dashes(path, language, findings):
    """Which long dashes this language may carry, from the copy gate's own file."""
    if not os.path.isfile(path):
        findings.append((path, "the dash rules file is missing"))
        return set()
    try:
        rules = json.loads(read_text(path))
    except ValueError as problem:
        findings.append((path, "the dash rules file is not valid JSON: %s" % problem))
        return set()
    languages = rules.get("languages") or {}
    named = languages.get(language)
    if named is None:
        named = rules.get("default") or []
    return set(named)


def pairs_hook(findings, where):
    """Catches an id written twice. json keeps the last one and says nothing."""
    def hook(pairs):
        seen = {}
        for key, value in pairs:
            if key in seen:
                findings.append((where, "id written twice in the file: %s" % key))
            seen[key] = value
        return seen
    return hook


def slots(value):
    """Every named slot in a value, plural object or plain string."""
    found = set()
    if isinstance(value, dict):
        for inner in value.values():
            found |= slots(inner)
    elif isinstance(value, str):
        found |= set(SLOT_RE.findall(value))
    return found


def texts(value):
    """Every string a value resolves to."""
    if isinstance(value, dict):
        out = []
        for inner in value.values():
            out.extend(texts(inner))
        return out
    return [value] if isinstance(value, str) else []


def check_entry(where, entry_id, entry, language, categories, dashes, english, findings):
    def note(what):
        findings.append(("%s:%s" % (where, entry_id), what))

    if not isinstance(entry, dict):
        note("the entry is not an object")
        return

    if not ID_RE.match(entry_id):
        note("the id is not a dotted lower case name")

    has_lore = entry.get("lore") is not None
    has_translation = entry.get("translation") is not None
    if language == "en" and not has_lore:
        # English is the source of record: lore is the wording everything else
        # is derived from, so an English entry without one is not an entry.
        note("the entry has no lore value")
    if language != "en" and not has_translation and not has_lore:
        note("the entry has nothing to show")
    # G7, in every language: a plain register with no lore beside it is half a
    # pair, and the half that goes missing is the one nobody notices.
    if entry.get("plain") is not None and not has_lore:
        note("the entry has a plain register but no lore one")

    values = {}
    for field in ("lore", "plain", "translation"):
        if entry.get(field) is not None:
            values[field] = entry[field]

    # Plurals: the categories are derived, never listed.
    plural_param = entry.get("plural")
    for field, value in values.items():
        if isinstance(value, dict):
            if not plural_param:
                note("%s is an object but the entry names no plural parameter" % field)
                continue
            have = sorted(k for k in value.keys())
            if have != sorted(categories):
                note("%s is missing plural categories for %s: has %s, needs %s"
                     % (field, language, ", ".join(have) or "(none)", ", ".join(sorted(categories))))
        elif plural_param and field in ("lore", "translation"):
            note("the entry names a plural parameter but %s is a single string" % field)

    # Slot parity inside the entry, then against the English it came from.
    lore_slots = slots(entry.get("lore")) if has_lore else set()
    if entry.get("plain") is not None:
        plain_slots = slots(entry["plain"])
        if plain_slots != lore_slots:
            note("lore and plain do not carry the same slots: %s against %s"
                 % (", ".join(sorted(lore_slots)) or "(none)", ", ".join(sorted(plain_slots)) or "(none)"))

    if language != "en" and english is not None:
        source = english.get(entry_id)
        if source is None:
            note("the English catalog has no such id")
        else:
            source_slots = slots(source.get("lore"))
            for field in ("translation", "lore", "plain"):
                if entry.get(field) is None:
                    continue
                mine = slots(entry[field])
                if mine != source_slots:
                    note("%s does not carry the English slots: %s against %s"
                         % (field, ", ".join(sorted(source_slots)) or "(none)",
                            ", ".join(sorted(mine)) or "(none)"))
            english_text = " ".join(texts(source.get("lore")))
            mine_text = " ".join(texts(entry.get("translation")) + texts(entry.get("lore")))
            for term in DO_NOT_TRANSLATE:
                if term in english_text and term not in mine_text:
                    note("the name %s does not survive in the translation" % term)

    # Declared parameters have to be the slots that are actually there.
    params = entry.get("params")
    if params is not None:
        if not isinstance(params, dict):
            note("params is not an object")
        else:
            declared = set(params.keys())
            everything = set()
            for value in values.values():
                everything |= slots(value)
            if declared != everything:
                note("params and slots disagree: declared %s, used %s"
                     % (", ".join(sorted(declared)) or "(none)", ", ".join(sorted(everything)) or "(none)"))
    if plural_param:
        if not isinstance(plural_param, str):
            note("plural does not name a parameter")
        else:
            everything = set()
            for value in values.values():
                everything |= slots(value)
            if plural_param not in everything:
                note("the plural parameter %s is not a slot in the wording" % plural_param)

    allows_html = bool(entry.get("allowsHtml"))
    for field, value in values.items():
        for text in texts(value):
            if not allows_html and HTML_RE.search(text):
                note("%s carries markup and the entry does not say allowsHtml" % field)
            for char, name in LONG_DASHES.items():
                if char in text and name not in dashes:
                    note("%s carries %s, which %s does not allow" % (field, name, language))


def unescape(text):
    """A JavaScript double quoted literal, as the runtime would read it."""
    out = []
    index = 0
    while index < len(text):
        char = text[index]
        if char != "\\":
            out.append(char)
            index += 1
            continue
        index += 1
        if index >= len(text):
            break
        nxt = text[index]
        index += 1
        if nxt == "u" and index + 4 <= len(text):
            try:
                out.append(chr(int(text[index:index + 4], 16)))
                index += 4
                continue
            except ValueError:
                pass
        out.append({"n": "\n", "t": "\t", "r": "\r", "b": "\b", "f": "\f"}.get(nxt, nxt))
    return "".join(out)


JS_STRING = r'"((?:[^"\\\n]|\\.)*)"'
T_CALL = re.compile(r"(?<![A-Za-z0-9_$])T\(\s*" + JS_STRING)
HTML_ATTR = re.compile(r'data-i18n(?:-title|-placeholder|-aria)?="([^"]*)"')

# A table that holds catalog ids rather than English names them in a property whose name
# ends in Id: labelId, introId, explainId, and whatever the next table needs. The world
# dials are the table this was written for. Their wording is rendered in a loop, so the
# id never appears inside a T("...") call the scan above can see, and without this rule
# every one of those 65 sentences would read as an orphan.
#
# Only a value shaped like an id counts, which is what keeps the rule from swallowing the
# buildId and PlayerId properties app.js already has. A value that IS shaped like an id
# and is not in the catalog is then named by the same finding a bad T() call gets, so a
# typo in a table is caught rather than rendered.
TABLE_ID = re.compile(r'(?<![A-Za-z0-9_$])[A-Za-z0-9_$]*Id\s*:\s*' + JS_STRING)

# HostCatalog.T("host.countdown.restart_in", ("time", ...)). The call shape is named rather
# than every id-shaped literal in the C# tree, because that tree is full of id-shaped
# literals that are not catalog ids at all: RPC method names, event names, and the id half
# of every host-facing throw. Only what this lookup is actually handed counts.
HOST_T_CALL = re.compile(r'HostCatalog\.T\(\s*"([^"]+)"')


def host_sources(paths, directories):
    """Every C# file the host-side lookup could be called from."""
    found = [p for p in (paths or []) if p and os.path.isfile(p)]
    wanted = [directories] if isinstance(directories, str) else list(directories or [])
    for directory in wanted:
        if not directory or not os.path.isdir(directory):
            continue
        for name in sorted(os.listdir(directory)):
            if name.lower().endswith(".cs"):
                path = os.path.join(directory, name)
                if path not in found:
                    found.append(path)
    return found


def host_ids(paths, directory):
    """Every catalog id the C# side asks for, with the file that asks."""
    asked = set()
    for path in host_sources(paths, directory):
        source = read_text(path)
        for match in HOST_T_CALL.finditer(source):
            asked.add((match.group(1), shown(path)))
    return asked


def table_ids(source):
    """Every catalog id a table names in an *Id property, in the order they appear."""
    out = []
    for match in TABLE_ID.finditer(source):
        value = unescape(match.group(1))
        if ID_RE.match(value):
            out.append(value)
    return out


# A table that has to keep its English as well as its id writes the two side by side:
#   label:"Combat",labelId:"world.wg.combat.label"
# The world dials do that because two composed sentences still read the English half
# (the forge dialog's help button, the first-run wizard's summary line) and those are
# the composed-message pass's to key, not this one's. Two copies of one sentence drift,
# so the gate holds them together: edit either half alone and it says so.
TABLE_PAIR = re.compile(
    r'(?<![A-Za-z0-9_$])([A-Za-z0-9_$]+)\s*:\s*' + JS_STRING +
    r'\s*,\s*\1Id\s*:\s*' + JS_STRING)


def table_english_drift(keys, app_path, findings):
    """A table that keeps English beside an id has to keep the catalog's English."""
    if not app_path or not os.path.isfile(app_path):
        return
    for match in TABLE_PAIR.finditer(read_text(app_path)):
        field, english, entry_id = match.group(1), unescape(match.group(2)), unescape(match.group(3))
        if not ID_RE.match(entry_id):
            continue
        entry = keys.get(entry_id)
        if not isinstance(entry, dict):
            continue                       # named by the completeness check
        if entry.get("lore") != english:
            findings.append((
                shown(app_path),
                "%s beside %s says %r where the catalog says %r"
                % (field, entry_id, english, entry.get("lore"))))


def completeness(keys, app_path, html_path, i18n_path, findings, host=None):
    """The catalog and the interface, checked against each other BOTH ways."""
    asked = set(host or ())

    for path in [p for p in (app_path, i18n_path) if p and os.path.isfile(p)]:
        source = read_text(path)
        rel = shown(path)
        for match in T_CALL.finditer(source):
            asked.add((unescape(match.group(1)), rel))
        for entry_id in table_ids(source):
            asked.add((entry_id, rel))

    if html_path and os.path.isfile(html_path):
        page = read_text(html_path)
        rel = shown(html_path)
        for match in HTML_ATTR.finditer(page):
            asked.add((match.group(1), rel))

    for entry_id, where in sorted(asked):
        if entry_id not in keys:
            findings.append((where, "asks for an id the English catalog does not have: %s" % entry_id))

    # The other direction, and it is strict now: an id nothing asks for is a sentence
    # nobody reads that a translator still pays for. There used to be a second way to
    # be used - an English sentence the TT() bridge still spelled out - and that bridge
    # is gone, so being asked for by id is the only way.
    asked_ids = {entry_id for entry_id, _ in asked}
    for entry_id in sorted(keys):
        if entry_id in asked_ids:
            continue
        findings.append(("en.json", "orphan key: nothing asks for %s" % entry_id))


DEFAULT_NORSE = os.path.join(HERE, "norse_terms.json")


def norse_register(path, findings):
    """The Norse register, and the ids that are allowed to carry it alone."""
    if not os.path.isfile(path):
        findings.append((shown(path), "the Norse register list is missing"))
        return None
    try:
        rules = json.loads(read_text(path))
    except ValueError as problem:
        findings.append((shown(path), "the Norse register list is not valid JSON: %s" % problem))
        return None
    terms = [str(t) for t in (rules.get("terms") or []) if str(t).strip()]
    if not terms:
        findings.append((shown(path), "the Norse register list names no terms"))
        return None
    return {
        "res": [(t, re.compile(r"(?<![A-Za-z])" + re.escape(t) + r"(?![A-Za-z])", re.I))
                for t in terms],
        "exempt": {k: v for k, v in (rules.get("exempt") or {}).items()
                   if not k.startswith("_")},
        "suffix": str(rules.get("caption_suffix") or ""),
        "prefix": str(rules.get("caption_prefix") or ""),
    }


def norse_register_drift(keys, rules, findings):
    """A sentence in the Norse register has to carry the plain wording of itself.

    This is the one rule left where three used to stand. All three watched the same
    regression from different sides - a sentence the swap table would have reworded,
    now answered by an entry that names only one wording - and all three were written
    to switch themselves off the day the table went. It has gone, so they are gone.

    What replaces them asks the question directly rather than by simulating a regex
    table: if the lore wording carries a word from the Norse register, the entry owes
    a plain wording of the same sentence. Two kinds of id are exempt. A Norse CAPTION
    (an id ending `.norse`, or anything under `common.norse.`) IS the Norse word, and
    CSS hides every one of them when the switch is off, so a plain register there is a
    wording nobody can read. And a handful of sentences quote a Norse word as data
    rather than speaking in it; those are named in norse_terms.json with the reason.
    """
    if not rules:
        return
    for entry_id in sorted(keys):
        entry = keys[entry_id]
        if not isinstance(entry, dict) or entry.get("plain") is not None:
            continue
        last = entry_id.rsplit(".", 1)[-1]
        if rules["suffix"] and last == rules["suffix"]:
            continue
        if rules["prefix"] and entry_id.startswith(rules["prefix"]):
            continue
        said = texts(entry.get("lore"))
        hit = [term for term, rx in rules["res"] if any(rx.search(t) for t in said)]
        if not hit:
            if entry_id in rules["exempt"]:
                findings.append((
                    "norse_terms.json",
                    "%s is exempted from the Norse register rule, but nothing in it is in"
                    " the register any more: drop the exemption" % entry_id))
            continue
        if entry_id in rules["exempt"]:
            continue
        findings.append((
            "en.json",
            "%s speaks in the Norse register (%s) and names no plain wording, so a host"
            " with the Norse names off reads it anyway"
            % (entry_id, ", ".join(sorted(set(hit))))))


def shipped_version(path, findings):
    if not os.path.isfile(path):
        return None
    match = re.search(r"<Version>([^<]+)</Version>", read_text(path))
    if not match:
        findings.append((os.path.basename(path), "the app project names no Version"))
        return None
    return match.group(1).strip()


def main(argv):
    directory = DEFAULT_DIR
    app_path = None
    html_path = None
    i18n_path = None
    dash_path = DEFAULT_DASHES
    norse_path = DEFAULT_NORSE
    csproj = None
    host_sources_paths = None
    host_sources_dir = None
    defaults = True

    index = 1
    while index < len(argv):
        flag = argv[index]
        value = argv[index + 1] if index + 1 < len(argv) else None
        if flag == "--dir":
            directory = value
            defaults = False
            index += 2
        elif flag == "--app":
            app_path = value
            index += 2
        elif flag == "--html":
            html_path = value
            index += 2
        elif flag == "--i18n":
            i18n_path = value
            index += 2
        elif flag == "--dashes":
            dash_path = value
            index += 2
        elif flag == "--norse":
            norse_path = value
            index += 2
        elif flag == "--csproj":
            csproj = value
            index += 2
        elif flag == "--host-dir":
            host_sources_dir = value
            index += 2
        else:
            print("unknown argument: %s" % flag)
            return 2

    if defaults:
        app_path = app_path or DEFAULT_APP
        html_path = html_path or DEFAULT_HTML
        i18n_path = i18n_path or DEFAULT_I18N
        csproj = csproj or DEFAULT_CSPROJ
        host_sources_paths = DEFAULT_HOST_SOURCES
        host_sources_dir = host_sources_dir or DEFAULT_HOST_DIRS

    findings = []

    if not os.path.isdir(directory):
        print("%s: no catalog folder" % directory)
        print("TOTAL 1")
        return 1

    files = sorted(
        os.path.join(directory, name)
        for name in os.listdir(directory)
        if name.lower().endswith(".json")
    )
    if not files:
        findings.append((directory, "the catalog folder holds no catalog"))

    english_keys = None
    parsed = {}

    for path in files:
        rel = shown(path)
        try:
            data = json.loads(read_text(path), object_pairs_hook=pairs_hook(findings, rel))
        except ValueError as problem:
            findings.append((rel, "not valid JSON: %s" % problem))
            continue
        if not isinstance(data, dict):
            findings.append((rel, "the catalog is not an object"))
            continue
        parsed[rel] = data
        meta = data.get("_meta") or {}
        if (meta.get("language") or "") == "en":
            english_keys = data.get("keys") or {}

    for rel, data in parsed.items():
        meta = data.get("_meta")
        if not isinstance(meta, dict):
            findings.append((rel, "the catalog has no _meta"))
            meta = {}
        language = str(meta.get("language") or "").strip()
        if not language:
            findings.append((rel, "_meta names no language"))
            language = "en"

        keys = data.get("keys")
        if not isinstance(keys, dict):
            findings.append((rel, "the catalog has no keys object"))
            continue

        categories = plural_categories(language, findings)
        dashes = allowed_dashes(dash_path, language, findings)
        english = english_keys if language != "en" else None

        for entry_id in sorted(keys):
            check_entry(rel, entry_id, keys[entry_id], language, categories, dashes, english, findings)

        if csproj and language == "en":
            version = shipped_version(csproj, findings)
            stamped = str(meta.get("appVersion") or "").strip()
            if version and stamped and stamped != version:
                findings.append((rel, "_meta.appVersion is %s and the app ships %s" % (stamped, version)))
            elif version and not stamped:
                findings.append((rel, "_meta names no appVersion"))

        if language == "en":
            norse_register_drift(keys, norse_register(norse_path, findings), findings)

        if language == "en" and (app_path or html_path):
            completeness(
                keys, app_path, html_path, i18n_path, findings,
                host=host_ids(host_sources_paths, host_sources_dir))
            table_english_drift(keys, app_path, findings)

    for where, what in findings:
        print("%s: %s" % (where, what))
    print("TOTAL %d" % len(findings))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
