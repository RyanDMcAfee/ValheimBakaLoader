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
  * that no entry quietly changes the plain wording of a sentence the TT() bridge
    still answers, nor of one a T() call site now asks for by id, nor of one the
    walker writes into the page: three roads into the same regression, which is
    one no English screenshot can show because it only appears with the plain
    terminology switch the other way
  * and, for English, that the catalog and the interface agree BOTH WAYS. Every
    id the interface asks for exists, and every id the catalog holds is asked
    for by something. An orphan key is a sentence nobody reads that a translator
    still pays for.

Usage:
  check_catalog.py                          the shipped catalog, every check
  check_catalog.py --dir DIR                catalogs in DIR, no completeness
  check_catalog.py --dir DIR --app A --html H [--i18n I]   with completeness

Prints one finding per line, then TOTAL n, and exits non zero when n is not 0.
"""

import io
import json
import os
import re
import subprocess
import sys

from html.parser import HTMLParser

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))

DEFAULT_DIR = os.path.join(REPO, "ValheimBakaLoader", "WebUI", "i18n")
DEFAULT_APP = os.path.join(REPO, "ValheimBakaLoader", "WebUI", "app.js")
DEFAULT_HTML = os.path.join(REPO, "ValheimBakaLoader", "WebUI", "index.html")
DEFAULT_I18N = os.path.join(REPO, "ValheimBakaLoader", "WebUI", "i18n.js")
DEFAULT_DASHES = os.path.join(REPO, "scripts", "copy-gate", "lang_dashes.json")
DEFAULT_CSPROJ = os.path.join(REPO, "ValheimBakaLoader", "ValheimBakaLoader.csproj")

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
    "en": ["one", "other"],
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
    """
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
TT_CALL = re.compile(r"(?<![A-Za-z0-9_$])TT\(\s*" + JS_STRING)
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


def completeness(keys, app_path, html_path, i18n_path, findings):
    """The catalog and the interface, checked against each other BOTH ways."""
    asked = set()
    lore_used = set()

    for path in [p for p in (app_path, i18n_path) if p and os.path.isfile(p)]:
        source = read_text(path)
        rel = shown(path)
        for match in T_CALL.finditer(source):
            asked.add((unescape(match.group(1)), rel))
        for entry_id in table_ids(source):
            asked.add((entry_id, rel))
        for match in TT_CALL.finditer(source):
            lore_used.add(unescape(match.group(1)))

    if html_path and os.path.isfile(html_path):
        page = read_text(html_path)
        rel = shown(html_path)
        for match in HTML_ATTR.finditer(page):
            asked.add((match.group(1), rel))

    for entry_id, where in sorted(asked):
        if entry_id not in keys:
            findings.append((where, "asks for an id the English catalog does not have: %s" % entry_id))

    # The other direction. A key is used when something asks for it by id, or
    # when its English sentence is still going through the TT() bridge, which is
    # how a half migrated call site and a migrated one stay in step.
    asked_ids = {entry_id for entry_id, _ in asked}
    for entry_id in sorted(keys):
        if entry_id in asked_ids:
            continue
        lore = keys[entry_id].get("lore") if isinstance(keys[entry_id], dict) else None
        if isinstance(lore, str) and lore in lore_used:
            continue
        findings.append(("en.json", "orphan key: nothing asks for %s" % entry_id))


TERM_PAIRS_BLOCK = re.compile(r"const TERM_PAIRS\s*=\s*\[(.*?)\n\];", re.S)
TERM_PAIR = re.compile(r'\[\s*' + JS_STRING + r'\s*,\s*' + JS_STRING + r'\s*\]')


def term_pairs(source):
    """The plain-wording swap table as app.js holds it, or None once it is gone."""
    block = TERM_PAIRS_BLOCK.search(source)
    if not block:
        return None
    return [(unescape(a), unescape(b)) for a, b in TERM_PAIR.findall(block.group(1))]


def plainify(text, pairs):
    """The swap, spelled the way app.js spells it: word boundaries only where the
    phrase starts or ends on a word character, longest phrases first because the
    table is written in that order."""
    for frm, to in pairs:
        pattern = (r"\b" if re.match(r"\w", frm) else "") \
            + re.escape(frm) \
            + (r"\b" if re.search(r"\w$", frm) else "")
        text = re.sub(pattern, lambda _m, t=to: t, text)
    return text


def register_drift(keys, app_path, findings):
    """The plain register cannot move quietly while the TT() bridge is up.

    TT(sentence) asks idFor() first, so the moment the catalog knows that exact
    English the bridge answers out of the entry instead of running the swap. An
    entry whose lore the swap WOULD have reworded, with no plain register of its
    own and no second entry sharing the text to make idFor refuse, therefore
    changes what a host with plain wording on reads. Nothing in an English
    screenshot can show that: it only appears with the switch the other way.
    """
    if not app_path or not os.path.isfile(app_path):
        return
    source = read_text(app_path)
    pairs = term_pairs(source)
    if pairs is None:
        return  # TERM_PAIRS is gone, and the bridge went with it

    bridged = {unescape(match.group(1)) for match in TT_CALL.finditer(source)}

    shared = {}
    for entry_id, entry in keys.items():
        lore = entry.get("lore") if isinstance(entry, dict) else None
        if isinstance(lore, str):
            shared.setdefault(lore, []).append(entry_id)

    for entry_id in sorted(keys):
        entry = keys[entry_id]
        if not isinstance(entry, dict):
            continue
        lore = entry.get("lore")
        if not isinstance(lore, str) or lore not in bridged:
            continue
        if len(shared.get(lore, ())) > 1:
            continue  # idFor refuses a shared text, so the swap still runs
        if entry.get("plain") is not None:
            continue
        if plainify(lore, pairs) != lore:
            findings.append((
                "en.json",
                "%s answers a TT() sentence the plain swap would have reworded to %r, "
                "and names no plain register" % (entry_id, plainify(lore, pairs))))


# A sentence app.js sets RAW today - never through TT() - and that a later pass moved
# into the catalog by id. The swap never reached it, so naming a plain register there
# would move English where nothing moves it, and the rule below has to be told. Written
# as id -> the wording the swap WOULD have produced, so the exemption cannot rot: change
# the pairs and the gate speaks up again instead of staying quiet.
#
# Empty on purpose. Every id a T() call site asks for today came out of a TT() call site,
# which means the swap did reach it and the plain register has to say what the swap said.
DYNAMIC_SWAP_EXEMPT = {}


def dynamic_register_drift(keys, app_path, findings):
    """The plain register cannot move quietly on the run-time half either.

    register_drift above watches the sentences the bridge still answers by their English.
    static_register_drift watches the words the walker writes into index.html. This one
    watches the third road into the same regression, and the one the migration actually
    travels: a sentence app.js has stopped spelling out and now asks for BY ID.

    While TERM_PAIRS is still here, TT(sentence) rewords that sentence for a host reading
    plain wording. T(id) rewords nothing: it hands back the plain register when the entry
    has one and the lore wording when it does not. So an entry a T() call site asks for
    has to carry exactly what the swap produced, or the words move under a switch that no
    English screenshot is ever taken with. The inverse is the same defect the other way:
    a plain register on a sentence the swap left alone moves English where nothing moved
    it today.

    Switches itself off once TERM_PAIRS is gone, because at that point the catalog is the
    only thing there is and a writer may word the plain register however they like.
    """
    if not app_path or not os.path.isfile(app_path):
        return
    source = read_text(app_path)
    pairs = term_pairs(source)
    if pairs is None:
        return  # TERM_PAIRS is gone, and the swap went with it

    for entry_id in sorted({unescape(m.group(1)) for m in T_CALL.finditer(source)}
                           | set(table_ids(source))):
        entry = keys.get(entry_id)
        if not isinstance(entry, dict):
            continue                       # named by the completeness check
        lore = entry.get("lore")
        if not isinstance(lore, str):
            continue                       # a plural object is checked category by category
        plain = entry.get("plain")
        swapped = plainify(lore, pairs)

        if entry_id in DYNAMIC_SWAP_EXEMPT:
            if swapped != DYNAMIC_SWAP_EXEMPT[entry_id]:
                findings.append((
                    "en.json",
                    "%s is exempt from the plain swap for rewording to %r, but the swap now "
                    "says %r: check the exemption is still the right call"
                    % (entry_id, DYNAMIC_SWAP_EXEMPT[entry_id], swapped)))
            elif plain is not None:
                findings.append((
                    "en.json",
                    "%s is exempt from the plain swap and also names a plain register, "
                    "which are two different answers to the same question" % entry_id))
            continue

        if swapped != lore:
            if plain is None:
                findings.append((
                    "en.json",
                    "%s is asked for by a T() call and the plain swap rewords that sentence "
                    "to %r, and it names no plain register" % (entry_id, swapped)))
            elif plain != swapped:
                findings.append((
                    "en.json",
                    "%s names the plain register %r where the swap on the same sentence "
                    "says %r" % (entry_id, plain, swapped)))
        elif plain is not None:
            findings.append((
                "en.json",
                "%s is asked for by a T() call, but nothing rewords that sentence today, "
                "so the plain wording would move where it never moved" % entry_id))


TERM_STATIC_SEL = re.compile(r'const TERM_STATIC_SEL\s*=\s*"([^"]*)"')

# Places where the swap was WRONG, and the entry deliberately stops it.
#
# The swap is 129 regexes over whatever text happens to be on screen, so a word
# that belongs to one hall reaches an unrelated label in another. Naming the
# wording it produced, rather than just the id, means the exemption cannot rot:
# change the pairs and the gate speaks up again instead of staying quiet.
#
#   atlas.layers.label  The Map hall's layer bar says "Layers" over the chips
#                       that draw portals, altars, builds and pins. The Barrow's
#                       pair ["Layers","Backups"] was rewriting it to "Backups"
#                       whenever plain wording was on, which named the bar after
#                       a feature on a different hall. The Barrow's own heading
#                       keeps the swap through barrow.sec.layers, which shares
#                       the text so idFor() refuses it and TT("Layers") still
#                       runs the regex.
SWAP_COLLISIONS = {
    "atlas.layers.label": "Backups",
}


class _Walk(HTMLParser):
    """index.html as a stack of elements, remembering which ones name an id.

    Only what the rule needs: for every data-i18n* attribute, the chain of
    (tag, id, classes) it sits under, so a descendant selector can be answered.
    """

    VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr"}

    def __init__(self):
        HTMLParser.__init__(self, convert_charrefs=True)
        self.stack = []
        self.named = []          # (entry_id, kind, chain)

    def handle_starttag(self, tag, attrs):
        got = dict(attrs)
        node = (tag, got.get("id") or "", (got.get("class") or "").split())
        chain = self.stack + [node]
        for name, value in got.items():
            if name == "data-i18n" or name.startswith("data-i18n-"):
                kind = name[len("data-i18n"):]
                self.named.append((value, kind, chain))
        if tag not in self.VOID:
            self.stack.append(node)

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag not in self.VOID and self.stack:
            self.stack.pop()

    def handle_endtag(self, tag):
        for index in range(len(self.stack) - 1, -1, -1):
            if self.stack[index][0] == tag:
                del self.stack[index:]
                return


def _simple_matches(simple, node):
    tag, node_id, classes = node
    if simple.startswith("#"):
        return node_id == simple[1:]
    if simple.startswith("."):
        return simple[1:] in classes
    return tag == simple


def _read_selectors(selector_list, findings):
    """The selector list as shapes this can answer, said once.

    Understands exactly the shapes TERM_STATIC_SEL uses: one simple selector, or
    two with a descendant space between them. Anything else is reported here,
    once, rather than quietly answered false for every element on the page,
    because a selector this cannot read is a hole in the rule below, not a pass.
    """
    parsed = []
    for selector in selector_list:
        parts = selector.split()
        if len(parts) in (1, 2) and all(re.match(r"^[#.]?[A-Za-z][\w-]*$", p) for p in parts):
            parsed.append(parts)
            continue
        findings.append((
            "app.js",
            "TERM_STATIC_SEL holds %r, which the static register rule cannot read; "
            "teach it that shape before shipping it" % selector))
    return parsed


def _selector_matches(parts, chain):
    """Does the last element of the chain match this already-read selector?"""
    if not _simple_matches(parts[-1], chain[-1]):
        return False
    if len(parts) == 1:
        return True
    return any(_simple_matches(parts[0], node) for node in chain[:-1])


def static_register_drift(keys, app_path, html_path, findings):
    """The plain register cannot move quietly on the static half of the page either.

    register_drift above watches the sentences the TT() bridge answers. This one
    watches the other road into the same regression: the words the walker writes
    into index.html. applyTerms rewrites the text (and the tooltip) of every
    element under TERM_STATIC_SEL through the swap, so for those elements the
    catalog has to say the same thing the swap says, or a host with plain wording
    on reads something different after the walk than before it. And for every
    OTHER element the walker fills, the swap never ran, so naming a plain register
    there moves English where nothing moved it today.

    Both arms switch themselves off once TERM_PAIRS is gone, because at that point
    the catalog is the only thing there is and a plain register is free to say
    whatever a writer wants.
    """
    if not app_path or not os.path.isfile(app_path):
        return
    if not html_path or not os.path.isfile(html_path):
        return
    source = read_text(app_path)
    pairs = term_pairs(source)
    if pairs is None:
        return  # TERM_PAIRS is gone, and the swap went with it

    bridged = {unescape(match.group(1)) for match in TT_CALL.finditer(source)}

    walk = _Walk()
    walk.feed(read_text(html_path))

    found = TERM_STATIC_SEL.search(source)
    if not found:
        # Quiet when the page names no ids at all, which is a fixture with no static
        # half rather than a tree that lost its selector list. Loud when there are ids
        # to walk, because then the rule really cannot see half of what it guards.
        if walk.named:
            findings.append((
                "app.js",
                "the swap table is still here but TERM_STATIC_SEL is gone, so the static "
                "register rule cannot tell which labels the swap still rewrites"))
        return
    selectors = _read_selectors(
        [s.strip() for s in found.group(1).split(",") if s.strip()], findings)

    said = set()   # one finding per entry, however many elements name it
    for entry_id, kind, chain in walk.named:
        if entry_id in said:
            continue
        entry = keys.get(entry_id)
        if not isinstance(entry, dict):
            continue                       # named by the completeness check
        lore = entry.get("lore")
        if not isinstance(lore, str):
            continue
        plain = entry.get("plain")
        swapped = plainify(lore, pairs)

        # applyTerms reaches the text of these elements, and their tooltip with it.
        reached = kind in ("", "-title") and any(
            _selector_matches(selector, chain) for selector in selectors)

        if entry_id in SWAP_COLLISIONS:
            # A deliberate stop. It still has to be the wording the exemption was
            # written for, and it still has to name no plain register, or it is a
            # different decision wearing the same id.
            if swapped != SWAP_COLLISIONS[entry_id]:
                findings.append((
                    "en.json",
                    "%s is exempt from the plain swap for rewording to %r, but the swap "
                    "now says %r: check the exemption is still the right call"
                    % (entry_id, SWAP_COLLISIONS[entry_id], swapped)))
            elif plain is not None:
                findings.append((
                    "en.json",
                    "%s is exempt from the plain swap and also names a plain register, "
                    "which are two different answers to the same question" % entry_id))
            continue

        if reached and swapped != lore:
            if plain is None:
                said.add(entry_id)
                findings.append((
                    "en.json",
                    "%s is written into an element the plain swap rewords to %r, "
                    "and names no plain register" % (entry_id, swapped)))
            elif plain != swapped:
                said.add(entry_id)
                findings.append((
                    "en.json",
                    "%s names the plain register %r where the swap on the same element "
                    "says %r" % (entry_id, plain, swapped)))
        elif plain is not None and swapped == lore and lore not in bridged:
            said.add(entry_id)
            findings.append((
                "en.json",
                "%s names a plain register, but nothing rewords that sentence today, "
                "so the plain wording would move where it never moved" % entry_id))


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
    csproj = None
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
        elif flag == "--csproj":
            csproj = value
            index += 2
        else:
            print("unknown argument: %s" % flag)
            return 2

    if defaults:
        app_path = app_path or DEFAULT_APP
        html_path = html_path or DEFAULT_HTML
        i18n_path = i18n_path or DEFAULT_I18N
        csproj = csproj or DEFAULT_CSPROJ

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

        if language == "en" and (app_path or html_path):
            completeness(keys, app_path, html_path, i18n_path, findings)
            register_drift(keys, app_path, findings)
            dynamic_register_drift(keys, app_path, findings)
            static_register_drift(keys, app_path, html_path, findings)
            table_english_drift(keys, app_path, findings)

    for where, what in findings:
        print("%s: %s" % (where, what))
    print("TOTAL %d" % len(findings))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
