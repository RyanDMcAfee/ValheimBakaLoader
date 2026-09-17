"""Check 6: long dashes, with the rule read as a function of the language.

Two things this fixes over the inline version it replaces:

  * It decodes with errors="replace" and strips a BOM, so a file the gate cannot decode
    is reported as a finding rather than ending the run in a traceback. A gate that dies
    tells you nothing, and the exit code looks the same as a real failure.

  * The rule is per language. Source files are always English, where the house rule is
    absolute: a long dash is a choice and grammar always offers a way round it. A
    catalog is read under its own language code, and some languages have no way round
    it (Russian writes "X is Y" with U+2014 standing in for the copula; leaving it out
    is a grammatical error). scripts/copy-gate/lang_dashes.json holds who is allowed
    what, and says why beside each entry.

Usage: sw_dashes.py <rules.json> <file> [<file> ...]
A file under WebUI/i18n is read under the language its filename names. Everything else
is read as English.
"""
import io
import json
import os
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

# U+2012 figure dash, U+2013 en dash, U+2014 em dash, U+2015 horizontal bar.
DASHES = {0x2012, 0x2013, 0x2014, 0x2015}


def read_text(path):
    with open(path, "rb") as handle:
        raw = handle.read()
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    return raw.decode("utf-8", errors="replace")


def language_of(path):
    """A catalog is read under the code its filename carries; anything else is English."""
    normalised = path.replace("\\", "/")
    if "/WebUI/i18n/" in normalised:
        return os.path.splitext(os.path.basename(normalised))[0]
    return "en"


def allowed_for(rules, language):
    languages = rules.get("languages") or {}
    allowed = languages.get(language, rules.get("default") or [])
    out = set()
    for entry in allowed:
        text = str(entry).strip().upper()
        if text.startswith("U+"):
            try:
                out.add(int(text[2:], 16))
            except ValueError:
                pass
    return out


def main(argv):
    if len(argv) < 2:
        print("  usage: sw_dashes.py <rules.json> <file> ...")
        return 2

    rules_path = argv[1]
    try:
        rules = json.loads(read_text(rules_path))
    except (OSError, ValueError) as problem:
        print("  dash rules could not be read (%s): %s" % (rules_path, problem))
        return 2

    findings = 0
    for path in argv[2:]:
        if not os.path.isfile(path):
            continue
        language = language_of(path)
        allowed = allowed_for(rules, language)
        text = read_text(path)
        counts = {}
        for line_number, line in enumerate(text.split("\n"), 1):
            for character in line:
                point = ord(character)
                if point in DASHES and point not in allowed:
                    counts.setdefault((point, line_number), 0)
                    counts[(point, line_number)] += 1
        for (point, line_number), count in sorted(counts.items()):
            print("  %s:%d: U+%04X x%d (read as language '%s')"
                  % (path.replace("\\", "/"), line_number, point, count, language))
            findings += count

    print("  unicode dash hits:", findings)
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
