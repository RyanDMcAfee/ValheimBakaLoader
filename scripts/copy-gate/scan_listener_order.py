"""A listener on a shared control, registered ABOVE the generic handler for that control.

WHY THIS EXISTS. Batch C of 1.2.6 shipped this twice, and it is invisible to every other kind
of check. Two click listeners on one element run in the order they were ADDED, not in the order
anybody would read them in:

  * The armed sweep's switch carries `data-t`, so the class that says which way it just went is
    flipped by the generic `$$("[data-t]")` handler. onCleanseArmedClick READS that class. The
    registration beside the button stood ABOVE the generic one, so it read the state the switch
    was LEAVING and posted the opposite of what the host asked for: arming a sweep from the
    window was impossible.
  * The Settings hall's App tab refreshed itself from a listener on `.wtab` that stood above the
    one which sets WORLD_TAB, so the first press of App read the tab the hall was LEAVING,
    returned early, and both of the tab's re-reads were dead.

Neither is a syntax error, neither fails a unit test, and both read perfectly well in a diff.
The only thing that tells them apart is SOURCE POSITION, which is what this reads.

THE RULE. For every element the page shares with a generic handler (anything carrying `data-t`
in index.html, and anything with class `wtab`), a click or keydown listener added to it by id or
by that class must appear AFTER the generic handler's own registration.

Usage:
  scan_listener_order.py APP.JS INDEX.HTML

Prints one finding per line, then TOTAL n, and exits non zero when n is not 0.
"""

import io
import re
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

# The generic handlers, and what each one of them owns. The anchor is the REGISTRATION and not
# just the selector: worldTab() reads $$(".wtab") itself, hundreds of lines above the line that
# wires it, and an anchor on the bare selector would have pointed at that read and called every
# real listener "below" it.
GENERIC = [
    {
        "anchor": '$$("[data-t]").forEach(t=>{',
        "what": "every switch carrying data-t",
        # Elements are found by id out of index.html.
        "ids_from": "data-t",
        "selectors": ["[data-t]"],
    },
    {
        "anchor": '$$(".wtab").forEach(b=>b.addEventListener("click",()=>worldTab(',
        "what": "the Settings hall's two tabs",
        "ids_from": "wtab-class",
        "selectors": [".wtab"],
    },
]

LISTEN = re.compile(r'addEventListener\s*\(\s*"(click|keydown)"')


def line_of(text, index):
    return text.count("\n", 0, index) + 1


def switch_ids(html):
    """Every id on an element that carries data-t, so a listener on one can be recognised."""
    found = set()
    for tag in re.findall(r"<[^>]*\bdata-t\b[^>]*>", html):
        m = re.search(r'\bid="([^"]+)"', tag)
        if m:
            found.add(m.group(1))
    return found


def wtab_ids(html):
    found = set()
    for tag in re.findall(r'<[^>]*class="[^"]*\bwtab\b[^"]*"[^>]*>', html):
        m = re.search(r'\bid="([^"]+)"', tag)
        if m:
            found.add(m.group(1))
    return found


def statement_start(js, at):
    """The beginning of the statement a registration sits in, so the target can be read."""
    cut = max(js.rfind("\n", 0, at), js.rfind(";", 0, at))
    return cut + 1 if cut >= 0 else 0


def main():
    app, page = sys.argv[1], sys.argv[2]
    js = io.open(app, encoding="utf-8-sig").read().replace("\r\n", "\n")
    html = io.open(page, encoding="utf-8-sig").read().replace("\r\n", "\n")

    findings = []

    for generic in GENERIC:
        anchor = js.find(generic["anchor"])
        if anchor < 0:
            findings.append("app.js no longer holds the generic handler " + generic["anchor"]
                            + " (" + generic["what"] + "), so nothing here can be ordered"
                            + " against it")
            continue

        ids = switch_ids(html) if generic["ids_from"] == "data-t" else wtab_ids(html)

        for m in LISTEN.finditer(js):
            if m.start() >= anchor:
                continue

            begin = statement_start(js, m.start())
            statement = js[begin:m.start()]

            # By id: $("#tCleanseArmed")?.addEventListener("click", ...)
            named = None
            for one in re.findall(r'\$\(\s*"#([A-Za-z0-9_-]+)"\s*\)', statement):
                if one in ids:
                    named = "#" + one
                    break

            # Or by the shared class itself.
            if named is None:
                for selector in generic["selectors"]:
                    if '"' + selector + '"' in statement:
                        named = selector
                        break

            if named is None:
                continue

            findings.append(
                "app.js:%d adds a %s listener to %s, which is %d lines ABOVE the generic %s"
                " handler at app.js:%d. Two listeners on one element run in the order they"
                " were ADDED, so this one reads the state the element is LEAVING."
                % (line_of(js, m.start()), m.group(1), named,
                   line_of(js, anchor) - line_of(js, m.start()),
                   generic["anchor"], line_of(js, anchor)))

        # And the generic handler has to SAY it is the one everything else comes after, or the
        # next person to move a line has nothing to read.
        window = js[max(0, anchor - 1400):anchor]
        if "scripts/copy-gate/scan_listener_order.py" not in window:
            findings.append("the generic " + generic["anchor"] + " handler at app.js:"
                            + str(line_of(js, anchor)) + " does not name"
                            + " scripts/copy-gate/scan_listener_order.py in the note above it,"
                            + " so the rule that keeps every other listener below it is written"
                            + " down nowhere a reader will find")

    for line in findings:
        print(line)
    print("TOTAL", len(findings))
    return 1 if findings else 0


sys.exit(main())
