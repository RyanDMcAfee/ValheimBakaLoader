"""Every STRING VALUE in a language catalog, walked as JSON rather than grepped.

The sibling scanners tokenize C# and JS so a gate reads what the program reads rather
than what a regex guesses. A catalog is the third shape the copy lives in, and once the
copy moves there the file-list scanners go green while pointing at files that no longer
hold any copy. That is the failure this file exists to stop: a false green is worse than
no gate, because it will be trusted.

Prints one line per finding, then TOTAL n. Keys are walked as well as values, because a
key that is shown anywhere is copy too, and a catalog whose keys carry prose is a thing
that happens.

Usage: sw_scancat.py <file.json> [<file.json> ...]
"""
import io
import json
import os
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

BAD = " - "


def read_text(path):
    """Bytes to text, never a traceback. A BOM is stripped and a bad byte is replaced,
    because a gate that crashes on an encoding reports nothing at all."""
    with open(path, "rb") as handle:
        raw = handle.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    return raw.decode("utf-8", errors="replace")


def walk(node, path, out):
    if isinstance(node, dict):
        for key, value in node.items():
            here = path + "." + str(key) if path else str(key)
            if isinstance(key, str) and BAD in key:
                out.append((here, "(key) " + key))
            walk(value, here, out)
    elif isinstance(node, list):
        for index, value in enumerate(node):
            walk(value, "%s[%d]" % (path, index), out)
    elif isinstance(node, str):
        if BAD in node:
            out.append((path, node))


def main(argv):
    files = argv[1:]
    findings = []

    for path in files:
        if not os.path.isfile(path):
            continue
        text = read_text(path)
        rel = path.replace("\\", "/")
        try:
            data = json.loads(text)
        except ValueError as problem:
            # Unreadable catalog is itself a finding: nothing downstream can check it.
            findings.append((rel + ": not valid JSON", str(problem)))
            continue
        here = []
        walk(data, "", here)
        for where, what in here:
            findings.append((rel + ":" + where, what))

    for where, what in findings:
        print("%s: %s" % (where, what[:300]))
    print("TOTAL", len(findings))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
