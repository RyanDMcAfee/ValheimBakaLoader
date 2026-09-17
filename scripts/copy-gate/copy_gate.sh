#!/usr/bin/env bash
# Copy gate for BakaLoader: no " - " (space hyphen space) may stand in for a dash
# in any user-facing string. Ranges (5-10), negatives (-212), CSS/code operators,
# regexes and comments are out of scope; the empty-value placeholder stays a bare
# hyphen. Run from the repo root.
#
# Checks 1 to 6 read the source AND the language catalogs under WebUI/i18n. That
# second half matters more than it looks: the day a string moves out of app.js and
# into a catalog, a gate that only knows the old file list goes green while scanning
# a file that no longer holds the copy. A false green is worse than no gate, because
# it gets trusted. The catalog arm is wired in now, before a single string moves.
#
# What is not a hardcoded count any more: the one allowed C# hit used to be asserted
# as "expect exactly 1", which passes just as happily when the one hit is a different
# string. scripts/copy-gate/allowlist.txt names the literal instead, and says why.
set -uo pipefail
APP=ValheimBakaLoader
HERE="$(dirname "$0")"
ALLOWLIST="$HERE/allowlist.txt"
DASH_RULES="$HERE/lang_dashes.json"
I18N_DIR="$APP/WebUI/i18n"
fail=0

# first interpreter that actually runs (the Windows Store python3 stub does not)
py=""
for cand in python C:/Python314/python python3; do
  if "$cand" -c "print(1)" >/dev/null 2>&1; then py="$cand"; break; fi
done
[ -n "$py" ] || { echo "no working python found"; exit 2; }

# The catalogs, when there are any. Empty until the extraction pass lands; the checks
# below all cope with an empty list and say so rather than skipping in silence.
CATALOGS=()
if [ -d "$I18N_DIR" ]; then
  while IFS= read -r f; do [ -n "$f" ] && CATALOGS+=("$f"); done < <(find "$I18N_DIR" -maxdepth 1 -name '*.json' | sort)
fi
cat_count=${#CATALOGS[@]}
cat_files() { [ "$cat_count" -gt 0 ] && printf '%s\n' "${CATALOGS[@]}"; }
cat_note() {
  if [ "$cat_count" -eq 0 ]; then echo "  + catalogs: none on disk yet ($I18N_DIR)"
  else echo "  + catalogs: $cat_count scanned ($(cat_files | tr '\n' ' '))"; fi
}

# Drops every finding the allowlist vouches for. Reads on stdin, writes on stdout.
allow_filter() {
  local text="$1" line
  if [ -f "$ALLOWLIST" ]; then
    while IFS= read -r line || [ -n "$line" ]; do
      case "$line" in ''|'#'*) continue;; esac
      text=$(printf '%s\n' "$text" | grep -vF -- "$line" || true)
    done < "$ALLOWLIST"
  fi
  printf '%s' "$text" | grep -v '^[[:space:]]*$' || true
}

echo "== 1. JS copy call sites: TT() / toast() / logLine() / confirmModal / promptModal / warn() =="
hits=$(grep -nE '(TT|toast|warn|logLine|confirmModal|promptModal)\([^)]*" - ' "$APP/WebUI/app.js" | grep -v '^\s*//' || true)
if [ "$cat_count" -gt 0 ]; then
  # In a catalog every value IS a copy call site: the lookup is the call.
  chits=$(grep -nE '" - ' $(cat_files) || true)
  [ -n "$chits" ] && hits="${hits}${hits:+$'\n'}${chits}"
fi
[ -n "$hits" ] && { echo "$hits"; fail=1; } || echo "  0 hits"
cat_note

echo "== 2. JS/HTML title= attributes and placeholders =="
files=("$APP/WebUI/app.js" "$APP/WebUI/index.html")
[ "$cat_count" -gt 0 ] && while IFS= read -r f; do files+=("$f"); done < <(cat_files)
hits=$(grep -nE '(title=|placeholder=)"[^"]* - ' "${files[@]}" || true)
if [ "$cat_count" -gt 0 ]; then
  # A catalog spells the same two as keys rather than as attributes.
  chits=$(grep -nEi '"[^"]*(title|placeholder)[^"]*"[[:space:]]*:[[:space:]]*"[^"]* - ' $(cat_files) || true)
  [ -n "$chits" ] && hits="${hits}${hits:+$'\n'}${chits}"
fi
[ -n "$hits" ] && { echo "$hits"; fail=1; } || echo "  0 hits"
cat_note

echo "== 3. index.html visible text (everything but the bare empty-value placeholder) =="
ial='id="skVikingsSub"|id="skModUpsSub"'   # the two bare empty-value placeholders
hits=$(grep -n ' - ' "$APP/WebUI/index.html" | grep -vE "$ial" || true)
if [ "$cat_count" -gt 0 ]; then
  chits=$(grep -n ' - ' $(cat_files) || true)
  [ -n "$chits" ] && hits="${hits}${hits:+$'\n'}${chits}"
fi
[ -n "$hits" ] && { echo "$hits"; fail=1; } || echo "  0 hits"
cat_note

echo "== 4. C# quoted text on non-comment lines (exceptions, Fail/FailDto, Error=, Description=, loggers) =="
# a double-quoted run containing " - ", on a line that is not a // or /// or * comment.
hits=$(grep -nE '"[^"]* - ' \
  "$APP/Forms/BlendWindow.Bridge.cs" "$APP/Game/ValheimServer.cs" $APP/Tools/*.cs \
  | grep -vE ':[0-9]+: *(//|\*|/\*)' || true)
if [ "$cat_count" -gt 0 ]; then
  chits=$(grep -nE '"[^"]* - ' $(cat_files) || true)
  [ -n "$chits" ] && hits="${hits}${hits:+$'\n'}${chits}"
fi
hits=$(allow_filter "$hits")
[ -n "$hits" ] && { echo "$hits"; fail=1; } || echo "  0 hits"
cat_note

echo "== 5. Every C#/JS/CATALOG STRING LITERAL (tokenizer, not grep) =="
"$py" "$HERE/sw_scanjs.py" "$APP/WebUI/app.js" | tail -1
n=$("$py" "$HERE/sw_scanjs.py" "$APP/WebUI/app.js" | tail -1 | awk '{print $2}')
[ "$n" != "0" ] && fail=1
# C#: every finding the allowlist does not vouch for is a failure. No counts.
csout=$("$py" "$HERE/sw_scan.py" "$APP" | grep -v '^TOTAL ' || true)
csleft=$(allow_filter "$csout")
if [ -n "$csleft" ]; then echo "$csleft"; echo "  C# TOTAL (after allowlist) $(printf '%s\n' "$csleft" | wc -l)"; fail=1
else echo "  C# TOTAL (after allowlist) 0"; fi
# Catalogs: walked as JSON, so a value is read the way the lookup reads it.
if [ "$cat_count" -gt 0 ]; then
  catout=$("$py" "$HERE/sw_scancat.py" $(cat_files))
  echo "$catout" | tail -1 | sed 's/^/  catalog /'
  n=$(echo "$catout" | tail -1 | awk '{print $2}')
  [ "$n" != "0" ] && { echo "$catout" | grep -v '^TOTAL '; fail=1; }
else
  echo "  catalog TOTAL 0 (no catalogs on disk yet)"
fi

echo "== 6. Unicode dashes U+2012/2013/2014/2015, per language =="
dashfiles=()
while IFS= read -r f; do [ -n "$f" ] && dashfiles+=("$f"); done < <(
  { ls -1 "$APP"/WebUI/*.js "$APP"/WebUI/*.html "$APP"/WebUI/*.css 2>/dev/null
    echo "$APP/Forms/BlendWindow.Bridge.cs"
    echo "$APP/Game/ValheimServer.cs"
    ls -1 "$APP"/Tools/*.cs 2>/dev/null
    cat_files; } )
"$py" "$HERE/sw_dashes.py" "$DASH_RULES" "${dashfiles[@]}" || fail=1
cat_note

echo "== 7. node --check =="
# The && used to swallow a parse error: a file that did not parse printed nothing
# and the gate carried on to say PASS. Both files are checked and both count.
if node --check "$APP/WebUI/app.js"; then echo "  app.js parses"; else fail=1; fi
if node --check "$APP/WebUI/i18n.js"; then echo "  i18n.js parses"; else fail=1; fi

echo "== 8. the lookup's own self test =="
# i18n.js answers for nearly every sentence a host reads, so it is checked here
# rather than only in the C# suite: the gate runs on every commit that touches
# copy, and a broken plural or a walker that eats markup is a copy defect.
I18N_SELFTEST="$(dirname "$HERE")/i18n/i18n_selftest.js"
if [ -f "$I18N_SELFTEST" ]; then
  if out=$(node "$I18N_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the self test is missing: $I18N_SELFTEST"; fail=1
fi

echo "== 9. the catalogs, read the way the lookup reads them =="
# Ids, plural completeness derived from Intl, slot parity, markup, the names that
# are not words, the dash rules per language, and English completeness both ways.
CHECK_CATALOG="$(dirname "$HERE")/i18n/check_catalog.py"
if [ -f "$CHECK_CATALOG" ]; then
  if out=$("$py" "$CHECK_CATALOG"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the catalog check is missing: $CHECK_CATALOG"; fail=1
fi

echo "== 10. the copy painted before the catalog arrives =="
# The catalog is fetched, and app.js paints as it is evaluated, so a T() call on that
# road answers with its own id. This is a copy defect like any other: the first frame
# reads "hearth.appbar.lifecycle.start" on a button.
CHECK_FIRST_FRAME="$(dirname "$HERE")/i18n/check_first_frame.py"
if [ -f "$CHECK_FIRST_FRAME" ]; then
  if out=$("$py" "$CHECK_FIRST_FRAME"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the first frame check is missing: $CHECK_FIRST_FRAME"; fail=1
fi

[ $fail -eq 0 ] && echo "GATE: PASS" || echo "GATE: FAIL"
exit $fail
