#!/usr/bin/env bash
# ------------------------------------------------------------------------------------
# The release preflight for language packs. Run it from the repo root, on the folder a
# cut left behind, BEFORE any of it is uploaded to a release:
#
#     bash scripts/release/check_packs.sh dist
#     bash scripts/release/check_packs.sh dist C:/Python314/python
#
# WHEN TO RUN IT. Every time packs are cut or re-cut, and again on the exact folder that
# is about to be uploaded. If the folder changes, run it again on the new one.
#
# WHY IT EXISTS. Four packs were once cut, verified, hashed, published and installed with
# every family named after its own file, so the page asked for families nobody published
# and not one byte of any face was ever drawn. Two checks in this tree would have caught
# it, and both of them are opt in: pack_tools.py only reads the stylesheet when it is
# handed --css, and LanguagePackRealArtefactTests does nothing at all unless BAKA_PACK_DIST
# names a folder. A recipe that is followed by hand is a recipe that gets skipped on the
# one tired evening, so this runs both with the switches ON and refuses a release where
# either of them was never asked the question. The env gated test saying it had nothing
# to read counts as a failure here, not as a pass.
#
# WHAT IT READS. Whatever the tree it runs in holds. The English catalog is read out of
# ValheimBakaLoader/WebUI/i18n/en.json and the stacks out of ValheimBakaLoader/WebUI/app.css,
# so a release cut the day the catalog grows a line is checked against the catalog as it
# is that day. There is no count written down anywhere in here.
#
# It prints one PASS or FAIL line per step and RELEASE PACKS: PASS or FAIL at the end,
# and exits non zero on any failure.
# ------------------------------------------------------------------------------------
set -uo pipefail

APP=ValheimBakaLoader
TESTS=ValheimBakaLoader.Tests
CSS="$APP/WebUI/app.css"
EN="$APP/WebUI/i18n/en.json"
TOOLS=scripts/i18n/pack_tools.py
PROBE=scripts/ui/pack_fonts_probe.js
GATED="$TESTS/Tools/LanguagePackRealArtefactTests.cs"
MANIFEST_NAME=lang-manifest.json

# The sentence that test prints when BAKA_PACK_DIST left it nothing to read. Finding it in
# the output means the strongest check in the tree stood down, which is the failure this
# whole script exists to make impossible.
SLEPT="is not set to a folder, so there are no release artefacts to read"

fail=0
step() {
  # step ok|no <sentence>
  if [ "$1" = "ok" ]; then echo "PASS $2"; else echo "FAIL $2"; fail=1; fi
}

DIST="${1:-}"
if [ -z "$DIST" ]; then
  echo "usage: bash scripts/release/check_packs.sh <folder of cut packs> [python]"
  exit 2
fi

# The repo root, because every path above is relative to it and a run from anywhere else
# would read a stylesheet and a catalog that are not the ones being released.
if [ ! -f "$TESTS/$TESTS.csproj" ] || [ ! -f "$CSS" ]; then
  echo "run this from the repo root: $CSS is not here"
  exit 2
fi

# The first interpreter that actually runs, the same way scripts/copy-gate/copy_gate.sh
# finds one (the Windows Store python3 stub answers to the name and then opens a shop).
# A second argument names one outright and is taken as given.
py="${2:-}"
if [ -n "$py" ]; then
  "$py" -c "print(1)" >/dev/null 2>&1 || { echo "$py does not run"; exit 2; }
else
  for cand in python C:/Python314/python python3; do
    if "$cand" -c "print(1)" >/dev/null 2>&1; then py="$cand"; break; fi
  done
  [ -n "$py" ] || { echo "no working python found"; exit 2; }
fi

echo "== 1. the folder =="
if [ ! -d "$DIST" ]; then
  step no "$DIST is not a folder"
elif [ -z "$(ls -A "$DIST" 2>/dev/null)" ]; then
  step no "$DIST is empty, so there is no release in it"
elif [ ! -f "$DIST/$MANIFEST_NAME" ]; then
  step no "$DIST holds no $MANIFEST_NAME, so it is not a release folder"
else
  step ok "$DIST holds $MANIFEST_NAME"
fi

if [ $fail -ne 0 ]; then
  echo ""
  echo "RELEASE PACKS: FAIL"
  exit 1
fi

MANIFEST="$DIST/$MANIFEST_NAME"

echo "== 2. the manifest and the zips, both directions =="
# A zip the manifest names and nobody cut is a language that answers 404 to the one host
# who picks it. A zip nobody named is a pack that was built, paid for and never published.
#
# The carriage returns come off on the way out. Python writes a text line ending on
# Windows, and a file name carrying one matches nothing on disk: the first cut of this
# script found every pack missing in step 2 and then reported steps 3 and 4 as PASS
# having read not one zip, which is the exact shape of false green everything here is
# built to refuse. Hence the count that follows, as well.
#
# A language row that names no asset stops the reading here rather than dropping out of
# the list further down, where the only sign of it would be a smaller count that still
# said PASS.
#
# Read first and strip afterwards, rather than down a pipe. The status wanted here is
# python's, and the status of a pipeline is whatever the last stage felt like saying.
if named=$("$py" -c "import json,sys
manifest=json.load(open(sys.argv[1],encoding='utf-8'))
rows=manifest.get('languages') or []
assets=[str(row.get('asset') or '') for row in rows]
if not assets or not all(assets):
    sys.exit('%d language rows and %d of them name no asset' % (len(rows), assets.count('')))
print('\n'.join(assets))" "$MANIFEST" 2>&1); then told=0; else told=1; fi
named=$(printf '%s' "$named" | tr -d '\r')

if [ $told -ne 0 ] || [ -z "$named" ]; then
  printf '%s\n' "$named" | sed 's/^/  /'
  named=""
  wanted=0
  step no "$MANIFEST_NAME does not name a set of packs this can read"
else
  wanted=$(printf '%s\n' "$named" | grep -c . || true)
  trouble=""

  # Read a line at a time rather than split on whitespace, so a name with a space in it
  # is one name.
  while IFS= read -r asset; do
    [ -n "$asset" ] || continue
    [ -f "$DIST/$asset" ] || trouble="${trouble}  the manifest names $asset and the folder has no such file"$'\n'
  done <<< "$named"

  for file in "$DIST"/lang-*.zip; do
    [ -e "$file" ] || continue
    here=$(basename "$file")
    printf '%s\n' "$named" | grep -qxF "$here" || trouble="${trouble}  $here is in the folder and the manifest does not name it"$'\n'
  done

  if [ -n "$trouble" ]; then
    printf '%s' "$trouble"
    step no "the manifest and the folder disagree about which packs this release has"
  else
    step ok "the manifest names $wanted packs and the folder holds exactly those"
  fi
fi

echo "== 3. every pack against the stacks in $CSS =="
# --css is the switch that was never on. It is on here, for every pack, every time.
trouble=""
seen=0
while IFS= read -r asset; do
  [ -n "$asset" ] || continue
  [ -f "$DIST/$asset" ] || continue
  seen=$((seen + 1))
  out=$("$py" "$TOOLS" verify-pack --zip "$DIST/$asset" --css "$CSS" 2>&1)
  printf '%s\n' "$out" | grep '^NOTE ' | sed 's/^/  /'
  if [ "$(printf '%s\n' "$out" | tail -1)" != "TOTAL 0" ]; then
    trouble="${trouble}$(printf '%s\n' "$out" | grep -v '^NOTE ' | sed "s/^/  $asset: /")"$'\n'
  fi
done <<< "$named"
if [ -n "$trouble" ]; then
  printf '%s' "$trouble"
  step no "a pack publishes faces the stacks for its language do not ask for"
elif [ "$seen" -eq 0 ]; then
  step no "not one pack was read here, so a clean run of this step would mean nothing"
elif [ "$seen" -ne "$wanted" ]; then
  step no "$seen of the $wanted packs were read, so this says nothing about the rest"
else
  step ok "all $seen packs publish families the stacks for their language ask for"
fi

echo "== 4. every pack's ids against $EN =="
# The same SET, not the same count. A pack that lost one id and gained another counts
# right and leaves a line in English beside a translation nothing looks up.
trouble=""
seen=0
while IFS= read -r asset; do
  [ -n "$asset" ] || continue
  [ -f "$DIST/$asset" ] || continue
  seen=$((seen + 1))
  out=$("$py" "$TOOLS" verify-ids --zip "$DIST/$asset" --catalog "$EN" 2>&1)
  if [ "$(printf '%s\n' "$out" | tail -1)" != "TOTAL 0" ]; then
    trouble="${trouble}$(printf '%s\n' "$out" | sed "s/^/  $asset: /")"$'\n'
  fi
done <<< "$named"
if [ -n "$trouble" ]; then
  printf '%s' "$trouble"
  step no "a pack holds a different set of ids from the English catalog in this tree"
elif [ "$seen" -eq 0 ]; then
  step no "not one pack was read here, so a clean run of this step would mean nothing"
elif [ "$seen" -ne "$wanted" ]; then
  step no "$seen of the $wanted packs were read, so this says nothing about the rest"
else
  step ok "all $seen packs hold exactly the ids $EN holds"
fi

echo "== 5. the real artefacts through the real installer =="
# BAKA_PACK_DIST is the other switch nobody has to throw. It is thrown here, and the test
# reporting that it had nothing to read is read as a failure rather than as a green run.
if ! grep -qF "$SLEPT" "$GATED"; then
  step no "$GATED no longer prints the sentence this step watches for, so rewrite the sentence here too"
else
  out=$(BAKA_PACK_DIST="$DIST" dotnet test "$TESTS/$TESTS.csproj" \
    -o "$TESTS/bin/pgrun" \
    --filter "FullyQualifiedName~LanguagePackRealArtefactTests" \
    --logger "console;verbosity=detailed" 2>&1)
  ran=$?

  if printf '%s\n' "$out" | grep -qF "$SLEPT"; then
    printf '%s\n' "$out" | grep -F "$SLEPT" | sed 's/^/  /'
    step no "the test read no artefacts, so nothing about these packs was proved by it"
  elif [ $ran -ne 0 ]; then
    printf '%s\n' "$out" | tail -30 | sed 's/^/  /'
    step no "the test over the real artefacts did not come back clean, see the lines above"
  else
    printf '%s\n' "$out" | grep -E '^ +(ok |reading |[0-9]+ packs )' | sed 's/^ */  /'
    step ok "every pack the manifest names installs through the service the app uses"
  fi
fi

echo "== 6. the faces, drawn in a real browser =="
if ! command -v node >/dev/null 2>&1; then
  step no "node is not on this machine, and the probe is the only thing that reads a rendered glyph"
else
  out=$(node "$PROBE" "$DIST" 2>&1)
  drew=$?
  if [ $drew -ne 0 ]; then
    printf '%s\n' "$out" | grep -E '^(FAIL|TOTAL|  )' | sed 's/^/  /'
    step no "a face the packs publish is never drawn by the page"
  else
    printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
    step ok "every face every pack publishes loads, is reached by a stack, and draws the script"
  fi
fi

echo ""
[ $fail -eq 0 ] && echo "RELEASE PACKS: PASS" || echo "RELEASE PACKS: FAIL"
exit $fail
