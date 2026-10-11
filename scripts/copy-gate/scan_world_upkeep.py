"""The cheat-mark controls belong to the Hearth's World upkeep group, and nowhere else.

WHY THIS EXISTS. 1.2.10 moved Clear cheat marks and its armed switch out of the Players head
and into World upkeep, with their ids and handlers as they were. A move that keeps the ids is
exactly the kind every other test goes on passing through either way: app.js finds the button
by id wherever it stands, so a merge that brought the old head back would put a second copy on
the Players page, or the only copy back there, and nothing would say so. This reads the live
markup with a real parser, finds the element each id opens, and walks its parents.

THE RULE. #cleanseBtn and #rowCleanseArmed each appear exactly once, each sits inside the
element with id="worldUpkeep", and neither sits anywhere inside id="page-vikings".

Usage:
  scan_world_upkeep.py [INDEX.HTML]

Prints one finding per line, then TOTAL n, and exits non zero when n is not 0.
"""

import io
import os
import sys
from html.parser import HTMLParser

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))

WANTED = ("cleanseBtn", "rowCleanseArmed")
GROUP = "worldUpkeep"
HALL = "page-vikings"

# Elements that never take a closing tag, so they never go on the stack of open parents.
VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta",
        "param", "source", "track", "wbr"}


class Walk(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stack = []        # (tag, id) for every element still open
        self.seen = {}         # id -> list of (line, ids of every open parent)

    def handle_starttag(self, tag, attrs):
        el_id = dict(attrs).get("id")
        if el_id in WANTED:
            parents = [i for _, i in self.stack if i]
            self.seen.setdefault(el_id, []).append((self.getpos()[0], parents))
        if tag not in VOID:
            self.stack.append((tag, el_id))

    def handle_startendtag(self, tag, attrs):
        el_id = dict(attrs).get("id")
        if el_id in WANTED:
            parents = [i for _, i in self.stack if i]
            self.seen.setdefault(el_id, []).append((self.getpos()[0], parents))

    def handle_endtag(self, tag):
        # Close back to the nearest open element of this name. A stray closer with no
        # opener is ignored rather than allowed to empty the stack.
        for k in range(len(self.stack) - 1, -1, -1):
            if self.stack[k][0] == tag:
                del self.stack[k:]
                return


def main():
    page = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        REPO, "ValheimBakaLoader", "WebUI", "index.html")
    with open(page, encoding="utf-8") as fh:
        markup = fh.read()
    w = Walk()
    w.feed(markup)
    w.close()

    findings = []
    for want in WANTED:
        hits = w.seen.get(want, [])
        if not hits:
            findings.append("#%s is not in the document at all" % want)
            continue
        if len(hits) > 1:
            findings.append("#%s appears %d times (lines %s); an id is one control"
                            % (want, len(hits), ", ".join(str(h[0]) for h in hits)))
        for line, parents in hits:
            if HALL in parents:
                findings.append("line %d: #%s is on the Players page; it moved to World upkeep in 1.2.10"
                                % (line, want))
            if GROUP not in parents:
                findings.append("line %d: #%s is not inside #%s" % (line, want, GROUP))

    for f in findings:
        print(f)
    if not findings:
        print("both cheat-mark controls sit in World upkeep and nowhere on the Players page")
    print("TOTAL %d" % len(findings))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
