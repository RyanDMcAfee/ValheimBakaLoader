"""Every wiki line that says WHERE a control is, checked against index.html.

WHY THIS EXISTS. 1.2.6 moved nine rows off the Dashboard's Upkeep card: three connection
switches and a test button to a Connection card of their own, and five preferences to the
Settings hall's App tab. The code moved, the pages were rewritten, and the pages still sent a
host to the old place in several sentences, because a rewrite is done by reading and a reader
skips the line that looks familiar. Nothing in the suite reads a wiki page, so a page that sends
somebody to a card a switch is not on is exactly as green as one that does not.

THE RULE. For every control this gate knows, the real container is derived from the LIVE markup
(which block of index.html holds its id). Any wiki line that names the control AND names a
container is then held to naming the right one, unless the line is historical.

WHAT COUNTS AS HISTORICAL. Wording that says so out loud ("used to", "before 1.2.6", "had
grown"), and any line inside a Release-notes.md section for a version before the one being
released: a note about 1.2.4 describes 1.2.4 and is right to say where a switch was then.

Usage:
  placement_gate.py WIKI_DIR [INDEX.HTML] [--version 1.2.6]

The wiki folder is an argument because these pages live outside the repository until release
day. Prints one finding per line, then TOTAL n, and exits non zero when n is not 0.
"""

import glob
import io
import os
import re
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))

# The blocks of index.html a control can live in, each named the way the pages name it, and the
# marker pair that bounds it. Derived rather than written down: the block is read out of the
# markup, so a control that moves again moves here with it.
BLOCKS = [
    ("Upkeep", 'id="upkeepBody"', "<!-- CONNECTION"),
    ("Connection card", 'id="connBody"', "<!-- RECENT LOG"),
    ("App tab", 'id="worldTabApp"', "<!-- The unsaved notice"),
]

# The controls the pages name, and the id each one is drawn with. A label is here because that is
# what a wiki line holds; the id is what index.html holds.
CONTROLS = {
    "Check for BakaLoader updates": "tCheckUpd",
    "Auto-update BakaLoader": "tAutoUpdApp",
    "Update mods at scheduled restarts": "tAutoUpdMods",
    "BepInEx kept up to date by BakaLoader": "tBepMaint",
    "Also check Hexium": "tUseHexium",
    "Do not use the Windows proxy for Thunderstore": "tNoProxy",
    "Connect over IPv4 only": "tIPv4",
    "Detailed log": "tDetailedLog",
    "Test connection": "btnNetTest",
    "Start BakaLoader with Windows": "tStartWin",
    "Start minimized": "tStartMin",
    "Share anonymous usage stats": "tShareStats",
    "Show Norse names": "tPlainTerms",
    "Messages to players": "selPlayerMsgLang",
    "Interface language": "selAppLang",
    "Text size": "selTextSize",
}

# How each container is NAMED on a page. The parenthesised form is in here because the one line
# that shipped wrong was a table cell reading "Start BakaLoader with Windows (Upkeep)", and a
# pattern that only knew "in the Upkeep card" walked straight past it.
CONTAINER_WORD = {
    "Upkeep": re.compile(r"\bin Upkeep\b|\bUpkeep card\b|\bUpkeep's\b|\bin the Upkeep\b|\(Upkeep\)"),
    "Connection card": re.compile(r"Connection card|\(Connection\)"),
    "App tab": re.compile(r"App tab|\(App tab\)"),
}

HISTORICAL = re.compile(
    r"before that|before 1\.2\.|until 1\.2\.|used to|it was in|since 1\.2\.|from 1\.2\."
    r"|moved off|moved on|had grown|no longer|was a block|went to the", re.I)

VERSION_HEADING = re.compile(r"^#{1,3}\s*v?([0-9]+(?:\.[0-9]+)*)\s*$")


def parts(text):
    return tuple(int(n) for n in text.split("."))


CLAUSE_SPLIT = re.compile(r"(?<=[.:;])\s+|,\s+and\s+|\s+and\s+(?=`)")


def clauses(line):
    """One line cut into the pieces a reader would read as separate statements."""
    pieces = [piece.strip() for piece in CLAUSE_SPLIT.split(line) if piece and piece.strip()]
    return pieces or [line]


def containers(html):
    """Where each control really is, read out of the markup."""
    blocks = []
    for name, opens, closes in BLOCKS:
        at = html.find(opens)
        if at < 0:
            print("index.html no longer holds " + opens)
            continue
        end = html.find(closes, at)
        blocks.append((name, html[at:end if end > at else len(html)]))

    where = {}
    for label, cid in CONTROLS.items():
        found = [name for name, body in blocks if 'id="%s"' % cid in body]
        where[label] = found[0] if len(found) == 1 else None
        if where[label] is None:
            print("the id %s for %r is in %s, so nothing can be checked against it"
                  % (cid, label, found or "no block this gate knows"))
    return where


def main():
    wiki = sys.argv[1]
    index = None
    release = None
    rest = sys.argv[2:]
    while rest:
        one = rest.pop(0)
        if one == "--version":
            release = parts(rest.pop(0))
        else:
            index = one
    if index is None:
        index = os.path.join(REPO, "ValheimBakaLoader", "WebUI", "index.html")
    if release is None:
        release = parts("1.2.6")

    html = io.open(index, encoding="utf-8-sig").read().replace("\r\n", "\n")
    where = containers(html)

    findings = []
    for path in sorted(glob.glob(os.path.join(wiki, "*.md"))):
        page = os.path.basename(path)
        section = None
        for number, line in enumerate(
                io.open(path, encoding="utf-8-sig").read().replace("\r\n", "\n").split("\n"), 1):
            heading = VERSION_HEADING.match(line.strip())
            if heading:
                try:
                    section = parts(heading.group(1))
                except ValueError:
                    section = None
            # A release note about an older version is a record of that version.
            older = section is not None and section < release

            # Per CLAUSE and not per line: one sentence can name two controls in two
            # different places and be right about both, and a whole-line read called that a
            # finding. "the three connection switches and the test are their own Connection
            # card, and Start BakaLoader with Windows is on the App tab" is one line and two
            # true statements.
            for clause in clauses(line):
                for label, real in where.items():
                    if real is None or label not in clause:
                        continue
                    named = [name for name, rx in CONTAINER_WORD.items() if rx.search(clause)]
                    if not named or real in named:
                        continue
                    if HISTORICAL.search(clause) or older:
                        continue
                    findings.append(
                        "%s:%d says %r is in %s. index.html draws it in the %s."
                        % (page, number, label, " and ".join(named), real))

    for line in findings:
        print(line)
    print("TOTAL", len(findings))
    return 1 if findings else 0


sys.exit(main())
