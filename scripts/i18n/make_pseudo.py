# -*- coding: utf-8 -*-
"""Builds the pseudo-locale catalog the language switch is proved against.

WHY A PSEUDO LOCALE. A language switch that quietly misses a surface cannot be seen in
English, because English after the switch looks exactly like English before it. So the
proof needs a catalog that is unmistakable on sight and needs no translator: every
sentence wrapped in brackets with a marked X either side, and padded by a third so a
label that was going to overflow in German overflows here instead. A screenshot under xx
then reads as a map of the switch: everything bracketed followed the re-render, and
anything still in plain English did not.

It is a GENERATED file and is regenerated whenever en.json changes:

    python scripts/i18n/make_pseudo.py

and the copy gate asks whether the file on disk is still the one en.json makes:

    python scripts/i18n/make_pseudo.py --check

which builds it into a temporary file, compares the bytes, and says to re-run the
line above when they differ. It never writes into the tree, so a gate cannot be the
thing that quietly updates the file it is supposed to be holding.

What it keeps, exactly: every id, every named slot, every plural category, the rune a
toast leads with, the allowsHtml flag, and every name that must not be translated -
BakaLoader, Valheim, Thunderstore and the rest survive because the English sentence is
embedded whole rather than replaced. What it adds is the wrapper and the padding, and
neither carries a brace, a long dash or a tag, so the catalog gate reads this file under
the same rules it reads a real pack under.

The padding character is U+1E8A, LATIN CAPITAL LETTER X WITH DOT ABOVE. It is Latin, so
it needs no packed font to render, and it is not a letter any English sentence contains,
so a run of it is unmistakably this script's work and never a translator's.
"""

import io
import json
import math
import os
import re
import sys
import tempfile

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DEFAULT_IN = os.path.join(REPO, "ValheimBakaLoader", "WebUI", "i18n", "en.json")
DEFAULT_OUT = os.path.join(REPO, "ValheimBakaLoader", "WebUI", "i18n", "xx.json")

MARK = "Ẋ"              # X with a dot above
OPEN = "[" + MARK + " "
CLOSE = " " + MARK + "]"
GROWTH = 1.3                 # a third longer, which is about what German runs to

# What the padding must never land in the middle of: a named slot, or a run of markup on
# an entry that allows it. Padding only ever goes on the end, so this is only read to
# measure how much of a value is wording rather than machinery.
SLOT_RE = re.compile(r"\{[A-Za-z_][A-Za-z0-9_]*\}")
TAG_RE = re.compile(r"<[^>]*>")


def pad_for(text):
    """How many filler characters this value needs to read a third longer.

    Measured against the WORDING only. A sentence that is mostly a slot name or mostly
    markup would otherwise be padded for length it never shows on screen.
    """
    visible = TAG_RE.sub("", SLOT_RE.sub("", text))
    want = int(math.ceil(len(visible) * (GROWTH - 1.0)))
    return max(1, want)


def pseudo(text):
    """One English sentence, wrapped and padded, with everything else left alone.

    The spaced wrapper is the readable one and is used for all but one shape. A value
    that ENDS on a bare hyphen - the empty-value placeholder, "avg -" - would come out
    of it reading "avg - X]", and a space hyphen space is exactly what the copy gate
    refuses. That finding would be this script's, not the copy's, so such a value takes
    the tight wrapper instead: the generated file is held to the same rules a real pack
    is held to, rather than excused from them.
    """
    tail = " " + (MARK * pad_for(text))
    spaced = OPEN + text + CLOSE + tail
    if " - " in spaced and " - " not in text:
        return "[" + MARK + text + MARK + "]" + tail
    return spaced


def convert(value):
    """A lore value: a sentence, or an object of CLDR categories."""
    if isinstance(value, dict):
        return dict((category, pseudo(inner) if isinstance(inner, str) else inner)
                    for category, inner in value.items())
    return pseudo(value) if isinstance(value, str) else value


def build(english):
    keys = english.get("keys") or {}
    out = {}
    for entry_id in keys:
        entry = keys[entry_id]
        if not isinstance(entry, dict):
            continue
        made = {"translation": convert(entry.get("lore"))}
        # Machinery, carried across untouched. A plural entry that lost its parameter, or
        # a toast that lost its rune, would be a defect of this script rather than of the
        # thing it is here to test.
        for field in ("plural", "params", "mark", "allowsHtml", "maxPx", "gameTerm"):
            if entry.get(field) is not None:
                made[field] = entry[field]
        out[entry_id] = made

    meta = english.get("_meta") or {}
    return {
        "_meta": {
            "language": "xx",
            "appVersion": meta.get("appVersion"),
            "catalog": meta.get("catalog"),
            "generatedFrom": "en.json",
            "note": "Generated by scripts/i18n/make_pseudo.py. Do not hand edit.",
        },
        "keys": out,
    }


def render(made):
    """The file's exact bytes. CRLF and one entry per line, the way every other file in
    this tree is written, so a regeneration shows as the lines that actually changed."""
    body = json.dumps(made, ensure_ascii=False, indent=2)
    return (body.replace("\n", "\r\n") + "\r\n").encode("utf-8")


def main(argv):
    # --check writes nowhere near the tree: it builds the file into a temporary one and
    # compares the bytes, so a gate can hold the generated file to its source without a
    # run of the gate being the thing that updates it.
    check = "--check" in argv[1:]
    rest = [word for word in argv[1:] if word != "--check"]
    source = rest[0] if len(rest) > 0 else DEFAULT_IN
    target = rest[1] if len(rest) > 1 else DEFAULT_OUT

    with open(source, "rb") as handle:
        raw = handle.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    english = json.loads(raw.decode("utf-8"))

    made = build(english)
    wanted = render(made)

    if check:
        handle, scratch = tempfile.mkstemp(prefix="make_pseudo_", suffix=".json")
        try:
            os.write(handle, wanted)
            os.close(handle)
            with open(scratch, "rb") as reading:
                fresh = reading.read()
        finally:
            try:
                os.remove(scratch)
            except OSError:
                pass
        try:
            with open(target, "rb") as reading:
                on_disk = reading.read()
        except IOError:
            on_disk = None
        if on_disk == fresh:
            print("%s: up to date, %d keys from %s"
                  % (os.path.basename(target), len(made["keys"]), os.path.basename(source)))
            return 0
        gone = "is missing" if on_disk is None else "is not what %s makes any more" % os.path.basename(source)
        print("%s %s. Re-run: python scripts/i18n/make_pseudo.py"
              % (os.path.basename(target), gone))
        return 1

    with open(target, "wb") as handle:
        handle.write(wanted)

    print("%s: %d keys from %s" % (os.path.basename(target), len(made["keys"]),
                                   os.path.basename(source)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
