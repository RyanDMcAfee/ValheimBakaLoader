"""English prose written straight into a call that PAINTS a sentence into the window.

WHY THIS EXISTS. A sentence written into a toast, a dialog or the condition bar reads
correctly on an English host and reads English on a Japanese, Russian or Chinese one, and
nothing else in the suite has an opinion about it. The dash gate counts dashes;
check_catalog.py reads the catalog and asks whether the interface asks for every id in it,
which a sentence that never became an id is invisible to. This is the rule for that class.

WHAT IS AND IS NOT COPY. The window's own sentences are copy and belong in the catalog:

    toast(MESSAGE, opts)           promptModal(TITLE, ...)
    confirmModal(TITLE, BODY, ...) setCondition(kind, {title, msg, ...})

The SAGA LOG is not. Every logLine body is English and verbatim by decision, so logLine is
not scanned at all: a log line is a record of what this machine did, written where it is
read, and a host who sends their log to somebody for help sends the same words whatever
language their window is in. That decision is held by WebUiDialogCopyTests and
WebUiUpdateCopyTests as well. Until 1.2.6 this rule covered logLine and carried a long
allowlist to let every Saga line through; the allowlist was the rule arguing with the
decision, so the rule was narrowed and the allowlist went.

Inside a scanned argument, every string literal that reads as prose is a finding: two or
more words of three letters or more, at least two of them lowercase. A source tag
("[RCON] "), a separator, a rune, a version number and a bare identifier are not prose and
are not counted, and neither is the HTML a composed body is built out of, which reads as
prose to a word counter and is class names rather than words. An allowlist is still read
when one is handed in, so a project that wants an exception can keep one beside its reason.

Usage:
  scan_copy_literals.py APP.JS [ALLOWLIST.txt]

Prints one finding per line, then TOTAL n, and exits non zero when n is not 0.
"""

import io
import os
import re
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

# The arguments of each call that PAINT, by their position in the argument list.
#
# logLine is deliberately not here. The Saga log is English and verbatim by decision, so a
# sentence written straight into a log line is the right thing rather than the finding this
# rule is looking for; see the module docstring.
CALLS = {
    "toast": (0,),
    "promptModal": (0,),
    # The title and the body. A body is composed HTML, so the markup in it is skipped and
    # the sentences between the tags are not.
    "confirmModal": (0, 1),
    # setCondition(kind, cond): the whole condition object, which is where its title and
    # its message are written.
    "setCondition": (1,),
}

WORD = re.compile(r"[A-Za-z][A-Za-z'’]*")
REGEX_PREV = set("(,=:[!&|?{};+-*%~^<>\n")


def strip_code(src):
    """The source with every comment and every STRING BODY blanked out, plus the body of
    each literal beside the offsets it sat at.

    A tokenizer rather than a line grep, for the same reason sw_scanjs.py is one: a
    sentence can hold a brace and a comment can hold a call. Template literals are walked
    properly, holes and all: the first cut of this stopped at the first backtick after the
    opening one, which desynchronised the whole file the moment one template's hole held
    another template, and every call after that point was invisible. The code inside a hole
    stays code here, so a copy call written inside one is still found.
    """
    n = len(src)
    i = 0
    out = []
    strings = []
    prev = ""
    # A stack of what we are inside. "code" frames know whether they are a template hole
    # (and how deep in braces they are); "tpl" frames remember where the current literal
    # chunk began.
    stack = [{"kind": "code", "hole": False, "depth": 0}]

    def blank(a, b):
        out.append("".join("\n" if ch == "\n" else " " for ch in src[a:b]))

    while i < n:
        top = stack[-1]
        c = src[i]

        if top["kind"] == "tpl":
            if c == "\\":
                i += 2
                continue
            if c == "`":
                strings.append((top["at"], i + 1, "`", src[top["at"] + 1:i]))
                # From AFTER the marker: the marker itself was already written out when the
                # chunk opened, and blanking it twice makes every offset after this point
                # drift, which is how the first cut of this lost half the file's calls.
                blank(top["at"] + 1, i + 1)
                stack.pop()
                prev = "`"
                i += 1
                continue
            if src.startswith("${", i):
                strings.append((top["at"], i, "`", src[top["at"] + 1:i]))
                blank(top["at"] + 1, i + 2)
                stack.append({"kind": "code", "hole": True, "depth": 0})
                prev = "("
                i += 2
                continue
            i += 1
            continue

        # inside code
        if src.startswith("//", i):
            j = src.find("\n", i)
            j = n if j < 0 else j
            blank(i, j)
            i = j
            continue
        if src.startswith("/*", i):
            j = src.find("*/", i)
            j = n if j < 0 else j + 2
            blank(i, j)
            i = j
            continue
        if c == "/" and (prev == "" or prev in REGEX_PREV):
            j = i + 1
            closed = False
            while j < n:
                if src[j] == "\\":
                    j += 2
                    continue
                if src[j] == "[":
                    j += 1
                    while j < n and src[j] != "]":
                        j += 2 if src[j] == "\\" else 1
                    j += 1
                    continue
                if src[j] == "/":
                    closed = True
                    break
                if src[j] == "\n":
                    break
                j += 1
            if closed:
                blank(i, j + 1)
                i = j + 1
                prev = "/"
                continue
            out.append(c)
            i += 1
            continue
        if c in "\"'":
            j = i + 1
            body = []
            while j < n:
                if src[j] == "\\":
                    body.append(src[j:j + 2])
                    j += 2
                    continue
                if src[j] == c or src[j] == "\n":
                    break
                body.append(src[j])
                j += 1
            end = j + 1 if j < n and j < n and src[j] == c else j
            strings.append((i, end, c, "".join(body)))
            blank(i, end)
            i = end
            prev = c
            continue
        if c == "`":
            out.append(" ")
            stack.append({"kind": "tpl", "at": i})
            i += 1
            continue
        if c == "{":
            top["depth"] += 1
        elif c == "}":
            if top["hole"] and top["depth"] == 0:
                # the hole closes and we are back in the template
                out.append(" ")
                stack.pop()
                stack[-1]["at"] = i          # the next chunk starts at the brace
                prev = ")"
                i += 1
                continue
            top["depth"] -= 1

        out.append(c)
        if not c.isspace():
            prev = c
        i += 1

    return "".join(out), strings


def arguments(code, opening):
    """The top level argument spans of the call whose "(" sits at `opening`. Depth is
    counted over the code with every string already blanked, so a comma inside a sentence
    or inside a nested call is never a separator."""
    depth = 0
    start = opening + 1
    spans = []
    i = opening
    n = len(code)
    while i < n:
        c = code[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
            if depth <= 0:
                spans.append((start, i))
                return spans
        elif c == ";" and depth == 1:
            # A call whose argument list never closes would otherwise run to the end of the
            # file and drag every literal after it into its message position.
            spans.append((start, i))
            return spans
        elif c == "," and depth == 1:
            spans.append((start, i))
            start = i + 1
        i += 1
    spans.append((start, n))
    return spans


# A whole tag, and a tag left open where a chunk ends at a hole. Deliberately NOT "everything
# up to the first >": that form ate the head off any sentence with a > in it, which is a
# sentence this rule then never sees.
TAG = re.compile(r"</?[A-Za-z][^>]*>|</?[A-Za-z][A-Za-z0-9]*\s[^>]*$")


def visible(text):
    """The words a literal would actually put on screen, with its HTML taken out.

    A dialog body and a condition bar's action row are composed, so their chunks are things
    like `<div class="mbody-note">` and `<button class="btn btn-ember btn-sm" id="cbBepLeftAct">`.
    Every one of those reads as prose to a word counter, because a class name is a run of
    lowercase letters with spaces between them, and none of it is copy. Taking the tags out
    rather than skipping the whole chunk is what keeps the rule honest: a sentence written
    between the tags of one chunk, `<div class="mbody-note">the backups sit beside the world
    on disk</div>`, is still found, and that is the shape a hand written body really takes.

    A chunk that falls BETWEEN two holes inside one tag carries neither angle bracket, so
    there is no tag here to take out: the progress bar's row is written as
    `... role="progressbar" aria-label="${...}" aria-valuemin="0" ...` and the piece between
    those two holes is attribute text with nothing around it. An attribute pair is the tell,
    so a chunk still holding one after the tags have gone is markup rather than a sentence.

    The one thing this cannot see, then, is an English sentence written inside an ATTRIBUTE,
    a literal title= or aria-label= on a hand built tag. Every one of those in app.js is an
    escaped catalog lookup in a hole rather than a literal, and keeping attribute text would
    make every class list in the file a finding, which is the rule crying wolf instead.
    """
    left = TAG.sub(" ", text)
    return "" if '="' in left else left


def prose(text):
    """True when a literal reads as a sentence rather than as a tag, a rune or a number."""
    if " " not in text:
        return False
    words = [w for w in WORD.findall(text) if len(w) >= 3]
    return sum(1 for w in words if w[:1].islower()) >= 2


def allowed(path):
    if not path or not os.path.exists(path):
        return set()
    keep = set()
    for line in io.open(path, encoding="utf-8-sig"):
        line = line.rstrip("\r\n")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        keep.add(line)
    return keep


def main():
    app = sys.argv[1]
    allowlist = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "copy_literals_allowlist.txt")

    src = io.open(app, encoding="utf-8-sig").read()
    code, strings = strip_code(src)
    keep = allowed(allowlist)

    findings = []
    for name, positions in CALLS.items():
        for match in re.finditer(r"(?<![A-Za-z0-9_$.])" + name + r"\s*\(", code):
            opening = match.end() - 1
            spans = arguments(code, opening)
            for position in positions:
                if position >= len(spans):
                    continue
                lo, hi = spans[position]
                for (start, end, quote, body) in strings:
                    if start < lo or end > hi:
                        continue
                    if not prose(visible(body)) or body in keep:
                        continue
                    findings.append((src.count("\n", 0, start) + 1, name, body))

    findings.sort()
    seen = []
    said = set()
    for line, name, body in findings:
        if (line, body) in said:
            continue
        said.add((line, body))
        seen.append((line, name, body))
        print("%d: %s() %s" % (line, name, body[:200]))
    print("TOTAL", len(seen))
    return 1 if seen else 0


sys.exit(main())
