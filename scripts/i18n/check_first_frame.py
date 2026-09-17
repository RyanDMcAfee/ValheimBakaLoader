"""Catches the one way a catalog id reaches the screen as itself.

app.js runs top to bottom and paints as it goes: the Hearth draws itself at the bottom
of the Hearth block, the Upkeep card gates its row at the bottom of the Upkeep one, and
several halls seed their controls the same way. The English catalog is FETCHED, so none
of that has words yet: T() answers an id it cannot look up with the id, and the first
frame would read "hearth.appbar.lifecycle.start" on a button.

Static markup does not have this problem. Every data-i18n element carries its English in
index.html and the walker only ever replaces it, so the page is right before the catalog
lands and identical after. The dynamic half has no such floor, which is why the fix is a
second paint: repaintBootCopy() runs the same painters again the moment the words arrive.

So the rule this gate holds is:

  * the boot walk calls repaintBootCopy(), and only when a catalog actually landed
  * every name repaintBootCopy calls is a function app.js declares
  * every function app.js calls as a STATEMENT AT COLUMN ZERO - which is to say, while
    the file is still being evaluated and long before the fetch resolves - either asks
    T() for nothing, or is one of the painters repaintBootCopy runs again
  * and the same for the functions those painters call as statements of their own, one
    level down, which is where a painter's helper would otherwise hide

What it does NOT see: a T() call added three levels below a painter that is never
repainted. That is the honest limit of a source gate, and it is why the painters that
are NOT repainted are worth keeping few. The browser probe in the language pipeline is
what closes the rest: load the page with the catalog fetch blocked and assert that no
text on screen looks like a dotted id.

Usage:  check_first_frame.py [app.js]
Prints one finding per line, then TOTAL n, and exits non zero when n is not 0.
"""

import io
import os
import re
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DEFAULT_APP = os.path.join(REPO, "ValheimBakaLoader", "WebUI", "app.js")

# T("id"), the lookup. The lookbehind keeps TT("English") out of it, which is the bridge
# and has words of its own whatever the catalog is doing.
T_CALL = re.compile(r'(?<![A-Za-z0-9_$])T\(\s*"')

NAME = r"[A-Za-z_$][A-Za-z0-9_$]*"


def mask(source):
    """The source with every string, comment and regular expression blanked out, the
    same length and the same line breaks, so brace matching and identifier scanning read
    code and only code. A brace inside a template literal closed a function early,
    which is the bug this exists to not have."""
    out = list(source)
    size = len(source)
    index = 0
    state = None
    quote = ""
    previous = ""
    starts_regex = set("(,=:[!&|?{};+-*%~^<>\n")
    while index < size:
        char = source[index]
        if state is None:
            if source.startswith("//", index):
                state = "line"
                out[index] = out[index + 1] = " "
                index += 2
                continue
            if source.startswith("/*", index):
                state = "block"
                out[index] = out[index + 1] = " "
                index += 2
                continue
            if char == "/" and (previous == "" or previous in starts_regex):
                state = "regex"
                out[index] = " "
                index += 1
                continue
            if char in "\"'`":
                state = "string"
                quote = char
                index += 1              # the quote itself stays, so T(" is still findable
                continue
            if not char.isspace():
                previous = char
            index += 1
            continue
        if state in ("line", "block"):
            if state == "line" and char == "\n":
                state = None
                index += 1
                continue
            if state == "block" and source.startswith("*/", index):
                out[index] = out[index + 1] = " "
                state = None
                index += 2
                continue
            if char != "\n":
                out[index] = " "
            index += 1
            continue
        if state == "regex":
            if char == "\\":
                out[index] = out[index + 1] = " "
                index += 2
                continue
            if char == "\n":            # not a regular expression after all
                state = None
                index += 1
                continue
            out[index] = " "
            if char == "/":
                state = None
                previous = "/"
            index += 1
            continue
        # inside a string or a template literal
        if char == "\\":
            out[index] = " "
            if index + 1 < size and source[index + 1] != "\n":
                out[index + 1] = " "
            index += 2
            continue
        if char == quote:
            state = None
            previous = '"'
            index += 1                  # the closing quote stays too
            continue
        if char != "\n":
            out[index] = " "
        index += 1
    return "".join(out)


def bodies(masked):
    """Every function app.js declares, as name -> (open brace, past the close brace)."""
    found = {}
    for match in re.finditer(r"\bfunction\s+(" + NAME + r")\s*\(", masked):
        depth = 0
        index = match.end() - 1
        while index < len(masked) and masked[index] != ")":
            index += 1
        while index < len(masked) and masked[index] != "{":
            index += 1
        start = index
        while index < len(masked):
            if masked[index] == "{":
                depth += 1
            elif masked[index] == "}":
                depth -= 1
                if depth == 0:
                    break
            index += 1
        found.setdefault(match.group(1), (start, index + 1))
    return found


def without_bodies(masked):
    """The file with every function body blanked: what is left is the code that runs
    while app.js is being evaluated, `if(!Native.available){...}` blocks and all. A call
    at column zero is the obvious form; the preview's whole boot block sits inside a top
    level if, one indent in, and runs just as early."""
    out = list(masked)
    index = 0
    size = len(masked)
    while index < size:
        opener = None
        if masked.startswith("function", index) and (index == 0 or not (
                masked[index - 1].isalnum() or masked[index - 1] in "_$.")):
            cursor = index + len("function")
            while cursor < size and masked[cursor] != "(" and masked[cursor] != "{":
                cursor += 1
            if cursor < size and masked[cursor] == "(":
                depth = 0
                while cursor < size:
                    if masked[cursor] == "(":
                        depth += 1
                    elif masked[cursor] == ")":
                        depth -= 1
                        if depth == 0:
                            break
                    cursor += 1
            while cursor < size and masked[cursor] not in "{;\n":
                cursor += 1
            if cursor < size and masked[cursor] == "{":
                opener = cursor
        elif masked.startswith("=>", index):
            cursor = index + 2
            while cursor < size and masked[cursor] in " \t\r\n":
                cursor += 1
            if cursor < size and masked[cursor] == "{":
                opener = cursor
            else:
                # A concise arrow: the body is an expression, and it ends where the
                # argument or the statement it sits in does. Blanked for the same reason
                # as a braced one - x=>goPage(x) runs when the host clicks, not now.
                depth = 0
                end = cursor
                while end < size:
                    char = masked[end]
                    if char in "([{":
                        depth += 1
                    elif char in ")]}":
                        if depth == 0:
                            break
                        depth -= 1
                    elif depth == 0 and char in ",;":
                        break
                    end += 1
                for blank in range(cursor, min(end, size)):
                    if out[blank] != "\n":
                        out[blank] = " "
                index = end
                continue
        if opener is None:
            index += 1
            continue
        depth = 0
        cursor = opener
        while cursor < size:
            if masked[cursor] == "{":
                depth += 1
            elif masked[cursor] == "}":
                depth -= 1
                if depth == 0:
                    break
            cursor += 1
        for blank in range(opener + 1, min(cursor, size)):
            if out[blank] != "\n":
                out[blank] = " "
        index = cursor + 1 if cursor < size else size
    return "".join(out)


def statement_calls(fragment, known, arrows=True):
    """The functions this fragment calls as a STATEMENT of its own. A name handed to
    addEventListener or to then() is not one of these, which is the point: it runs when
    the host does something, by which time the words are long since here.

    `arrows` decides whether a call that IS a concise arrow body counts. Looking for
    what runs early it should, because `x=>renderThing(x)` handed straight to a
    subscription does run. Walking OUT from repaintBootCopy it must not, and the
    difference is not academic: the mod-update condition row wires its button with
    `()=>goPage("mods")`, goPage refreshes the journal, and the journal draws the
    roster - so with arrows followed, repainting the mod table alone made the gate
    believe the ROSTER was repainted too, and a roster dropped from the repaint passed
    in silence. A click handler five names down is not a repaint path.
    """
    called = []
    head = r"(?:^|[;{}]|=>)" if arrows else r"(?:^|[;{}])"
    for match in re.finditer(head + r"\s*(" + NAME + r")\s*\(", fragment, re.M):
        if match.group(1) in known and match.group(1) not in called:
            called.append(match.group(1))
    return called


def main(argv):
    app_path = argv[0] if argv else DEFAULT_APP
    with open(app_path, "rb") as handle:
        raw = handle.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    source = raw.decode("utf-8", errors="replace")
    masked = mask(source)
    declared = bodies(masked)

    findings = []

    def asks_for_words(name):
        start, end = declared[name]
        return bool(T_CALL.search(source[start:end]))

    # 1. the boot walk, and the only condition it may run under
    walk = re.search(r"const walk\s*=\s*ok\s*=>\s*\{(.*?)\n  \};", masked, re.S)
    if not walk:
        findings.append("app.js: the catalog boot no longer has a walk(ok) to hang the repaint on")
    elif "repaintBootCopy()" not in walk.group(1):
        findings.append("app.js: the catalog boot does not repaint the copy it already painted "
                        "(walk(ok) never calls repaintBootCopy)")
    elif not re.search(r"if\s*\(\s*ok\s*\)", walk.group(1)):
        findings.append("app.js: the repaint is not held behind ok, so a catalog that never "
                        "arrived would repaint anyway and paint ids over the English")

    if "repaintBootCopy" not in declared:
        findings.append("app.js: repaintBootCopy is gone, so nothing paints the dynamic copy "
                        "again once the words arrive")
        print("\n".join(findings))
        print("TOTAL %d" % len(findings))
        return 1 if findings else 0

    start, end = declared["repaintBootCopy"]
    direct = statement_calls(masked[start:end], declared, arrows=False)
    if not direct:
        findings.append("app.js: repaintBootCopy runs nothing")

    # Everything the repaint reaches, not only what it names: a painter it calls paints
    # its own helpers with it. Straight calls only, never through a concise arrow body:
    # see statement_calls for the path that made this set swallow a whole hall.
    repainted = set()
    queue = list(direct)
    while queue:
        name = queue.pop()
        if name in repainted or name not in declared:
            continue
        repainted.add(name)
        here, there = declared[name]
        queue.extend(statement_calls(masked[here:there], declared, arrows=False))

    # The condition bar cannot be repainted by calling its painter: a condition stores the
    # sentence it was raised with. Its raisers hand the bar an `again` closure instead, and
    # rerenderConditions replays them, so a raiser that hands one in is covered as surely
    # as a painter the repaint calls by name.
    if "rerenderConditions" in repainted:
        for name, (here, there) in declared.items():
            if re.search(r"^\s*again\s*:", masked[here:there], re.M):
                repainted.add(name)
    elif "setCondition" in declared:
        findings.append("app.js: repaintBootCopy does not reach rerenderConditions, so a "
                        "condition raised before the catalog arrived keeps the ids it was "
                        "built with")
    if "rerenderConditions" in declared:
        here, there = declared["rerenderConditions"]
        if ".again(" not in masked[here:there]:
            findings.append("app.js: rerenderConditions no longer replays the condition's "
                            "own again(), so the bar is never rebuilt from the facts")

    # 2. everything painted while the file is still being evaluated
    top = statement_calls(without_bodies(masked), declared)

    for name in sorted(top):
        if name in repainted:
            continue
        if asks_for_words(name):
            findings.append(
                "app.js: %s paints while app.js is still being evaluated and asks T() for "
                "words the catalog has not answered yet, and repaintBootCopy does not run it "
                "again: the first frame shows the id" % name)
            continue
        # one level down, through the helpers it calls as statements
        start, end = declared[name]
        for helper in statement_calls(masked[start:end], declared):
            if helper in repainted or helper == name:
                continue
            if asks_for_words(helper):
                findings.append(
                    "app.js: %s is called by %s while app.js is still being evaluated and asks "
                    "T() for words that have not arrived, and repaintBootCopy runs neither of "
                    "them again" % (helper, name))

    for finding in findings:
        print(finding)
    print("TOTAL %d" % len(findings))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
