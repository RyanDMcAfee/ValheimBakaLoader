#!/usr/bin/env bash
# Copy gate for BakaLoader: no " - " (space hyphen space) may stand in for a dash
# in any user-facing string. Ranges (5-10), negatives (-212), CSS/code operators,
# regexes and comments are out of scope; the empty-value placeholder stays a bare
# hyphen. Run from the repo root.
set -uo pipefail
APP=ValheimBakaLoader
fail=0

echo "== 1. JS copy call sites: TT() / toast() / logLine() / confirmModal / promptModal / warn() =="
hits=$(grep -nE '(TT|toast|warn|logLine|confirmModal|promptModal)\([^)]*" - ' "$APP/WebUI/app.js" | grep -v '^\s*//' || true)
[ -n "$hits" ] && { echo "$hits"; fail=1; } || echo "  0 hits"

echo "== 2. JS/HTML title= attributes and placeholders =="
hits=$(grep -nE '(title=|placeholder=)"[^"]* - ' "$APP/WebUI/app.js" "$APP/WebUI/index.html" || true)
[ -n "$hits" ] && { echo "$hits"; fail=1; } || echo "  0 hits"

echo "== 3. index.html visible text (everything but the bare empty-value placeholder) =="
ial='id="skVikingsSub"|id="skModUpsSub"'   # the two bare empty-value placeholders
hits=$(grep -n ' - ' "$APP/WebUI/index.html" | grep -vE "$ial" || true)
[ -n "$hits" ] && { echo "$hits"; fail=1; } || echo "  0 hits"

echo "== 4. C# quoted text on non-comment lines (exceptions, Fail/FailDto, Error=, Description=, loggers) =="
# a double-quoted run containing " - ", on a line that is not a // or /// or * comment.
# "[1 - General]" is a BepInEx .cfg section header, a file format, not copy.
hits=$(grep -nE '"[^"]* - ' \
  "$APP/Forms/BlendWindow.Bridge.cs" "$APP/Game/ValheimServer.cs" $APP/Tools/*.cs \
  | grep -vE ':[0-9]+: *(//|\*|/\*)' | grep -v '\[1 - General\]' || true)
[ -n "$hits" ] && { echo "$hits"; fail=1; } || echo "  0 hits"

echo "== 5. Every C#/JS STRING LITERAL (tokenizer, not grep) =="
# first interpreter that actually runs (the Windows Store python3 stub does not)
py=""
for cand in python C:/Python314/python python3; do
  if "$cand" -c "print(1)" >/dev/null 2>&1; then py="$cand"; break; fi
done
[ -n "$py" ] || { echo "no working python found"; exit 2; }
"$py" "$(dirname "$0")/sw_scanjs.py" "$APP/WebUI/app.js" | tail -1
n=$("$py" "$(dirname "$0")/sw_scanjs.py" "$APP/WebUI/app.js" | tail -1 | awk '{print $2}')
[ "$n" != "0" ] && fail=1
# C# scan allows exactly one: the BepInEx cfg section header "[1 - General]"
"$py" "$(dirname "$0")/sw_scan.py" "$APP" | tail -2
n=$("$py" "$(dirname "$0")/sw_scan.py" "$APP" | tail -1 | awk '{print $2}')
[ "$n" != "1" ] && fail=1

echo "== 6. Unicode dashes U+2012/2013/2014/2015 =="
"$py" -c "
import glob,sys
BAD={0x2012,0x2013,0x2014,0x2015}
n=0
for f in glob.glob('$APP/WebUI/*.js')+glob.glob('$APP/WebUI/*.html')+glob.glob('$APP/WebUI/*.css')+['$APP/Forms/BlendWindow.Bridge.cs','$APP/Game/ValheimServer.cs']+glob.glob('$APP/Tools/*.cs'):
    n+=sum(1 for ch in open(f,'rb').read().decode('utf-8') if ord(ch) in BAD)
print('  unicode dash hits:',n); sys.exit(1 if n else 0)
" || fail=1

echo "== 7. node --check =="
node --check "$APP/WebUI/app.js" && echo "  app.js parses"

[ $fail -eq 0 ] && echo "GATE: PASS" || echo "GATE: FAIL"
exit $fail
