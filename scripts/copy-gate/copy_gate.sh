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
      # A Windows checkout writes this file with CRLF, and the carriage return
      # rides along into the pattern: every entry then matched nothing, and the
      # gate reported a literal the allowlist vouches for as copy.
      line=${line%$'\r'}
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

# The heading says " - " on purpose. This whole file is a DASH gate: checks 1 to 6 read
# string literals and ask one question of each, which is whether a space hyphen space is
# standing in for a dash. What it is NOT is a check that a literal is going through the
# catalog, and it used to be headed as though it were ("Every C#/JS/CATALOG STRING
# LITERAL"), which is how a raw English sentence handed to an event and toasted by the
# page shipped under a green "C# TOTAL (after allowlist) 0". That one belongs to
# scripts/i18n/check_catalog.py and to the tests; this one counts dashes.
echo "== 5. \" - \" inside any C#/JS/CATALOG string literal (tokenizer, not grep) =="
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

echo "== 11. the map's age line, tier by tier =="
# The one formatter whose English no Intl style can write, so its four sentences are
# catalog entries and a copy edit can move them. Run as a table against both arms: the
# catalog one and the hand-rolled floor for a window whose lookup never arrived.
ATLAS_AGE="$(dirname "$HERE")/i18n/atlas_age_selftest.js"
if [ -f "$ATLAS_AGE" ]; then
  if out=$(node "$ATLAS_AGE"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the map age table is missing: $ATLAS_AGE"; fail=1
fi

echo "== 12. the pseudo locale, against the English it is made from =="
# xx.json is generated, and a generated file that drifts is worse than no file: the
# language walk is driven with it, so a sentence added to en.json and missing here
# reads as plain English under xx and the walk calls the surface unreachable. The
# check builds it into a temporary file and compares the bytes; it never writes into
# the tree, so running the gate can never be what updates the file.
MAKE_PSEUDO="$(dirname "$HERE")/i18n/make_pseudo.py"
if [ -f "$MAKE_PSEUDO" ]; then
  if out=$("$py" "$MAKE_PSEUDO" --check); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the pseudo locale builder is missing: $MAKE_PSEUDO"; fail=1
fi

echo "== 13. the server's answer, read rather than assumed =="
# A toast that says a command worked is copy, and a toast chosen BEFORE the server
# answered is copy that is not true. baka_killall answers on the same line whether it
# ran, refused, started, was already running or fell over, and every one of those used
# to read "KillAll unleashed". The table drives the real reader out of app.js against
# every reply the plugin can produce, and the rule beside it refuses the next
# sendConsole call that hands in a finished sentence instead of reading the reply.
KILLALL_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/killall_reply_selftest.js"
if [ -f "$KILLALL_SELFTEST" ]; then
  if out=$(node "$KILLALL_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the reply table is missing: $KILLALL_SELFTEST"; fail=1
fi

echo "== 14. what a kick actually did, read rather than assumed =="
# The same rule as 13, on the other command that reports on a person. A kick used to
# raise "Kicked X" for any answer at all, including the one that says nobody of that
# name is on the server, which is exactly what a name read in the wrong encoding
# produces. The table drives the real reader out of app.js against every reply the
# plugin can give, and the rules beside it keep the hostId on the call and the kick
# out of doPlayerAct.
KICK_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/kick_reply_selftest.js"
if [ -f "$KICK_SELFTEST" ]; then
  if out=$(node "$KICK_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the kick reply table is missing: $KICK_SELFTEST"; fail=1
fi

echo "== 15. what the loader row says about an install, read rather than assumed =="
# The same rule as 13 and 14, on a surface where the wrong sentence is worse than a wrong
# toast: the row used to draw nine different shapes of install as "Outside" with an Update
# button that wrote without a word. The table drives the REAL state and wording choosers out
# of app.js over every shape the host's answer can arrive in, and the rules beside it keep
# every id a chooser can name in the catalog, keep a press that has to be asked about from
# going out with nothing to say for itself, and keep every refusal the host side can throw
# paired with a sentence, so no raw message from the machine reaches a toast.
BEPINEX_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/bepinex_row_selftest.js"
if [ -f "$BEPINEX_SELFTEST" ]; then
  if out=$(node "$BEPINEX_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the loader row table is missing: $BEPINEX_SELFTEST"; fail=1
fi

echo "== 16. a sortable header still sorts after a realm switch =="
# Issue 14. wireSort wires both sortable tables ONCE, while app.js is being evaluated, and
# until 1.2.1 it was handed the state OBJECT: switchServer replaced both objects on every
# realm switch, so from the first switch every header click wrote to an orphan while the
# renderers read the live one. All twelve headers stopped sorting and only a reload cured
# it, with the header still glowing because that glow is CSS. The browser probe beside this
# (scripts/ui/sort_survives_realm_switch_probe.js) proves the behaviour; this is the cheap
# half that runs on every commit, and its last rule is the SHAPE rather than the two names,
# so the next handler wired once over an object something later replaces is caught here.
SORT_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/sort_state_selftest.js"
if [ -f "$SORT_SELFTEST" ]; then
  if out=$(node "$SORT_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the sort state selftest is missing: $SORT_SELFTEST"; fail=1
fi

echo "== 17. the five world switches, from the game's vocabulary to the save =="
# Issue 15. A switch the game has and the page has no control for is not a missing feature,
# it is data loss: BakaLoader emits -resetmodifiers at every start, which clears a world's
# whole starting key list, so a switch set in the game client was wiped at the first start
# with no way in the window to put it back. The rules hold WorldGen.Switches, the controls
# in index.html, the sentences in the catalog, the unsaved-band reader and the save against
# each other, and drive the real paint and scrape functions out of app.js.
SWITCH_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/world_switches_selftest.js"
if [ -f "$SWITCH_SELFTEST" ]; then
  if out=$(node "$SWITCH_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the world switches selftest is missing: $SWITCH_SELFTEST"; fail=1
fi

echo "== 18. a refused difficulty write leaves the dials unsaved =="
# Save Config writes twice, and the second reply was read for two lists and nothing else. A
# FAIL from worldgen.save therefore passed in silence, the snapshot at the foot of the
# handler declared the whole form saved, and the notice, the markers and the breathing Save
# button all went out over a difficulty that is not on disk. The rules hold the flag, the
# restore, and that the restore happens AFTER the snapshot rather than before it.
WGFAIL_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/worldgen_save_fail_selftest.js"
if [ -f "$WGFAIL_SELFTEST" ]; then
  if out=$(node "$WGFAIL_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the worldgen save fail selftest is missing: $WGFAIL_SELFTEST"; fail=1
fi

echo "== 19. a scan that could not reach the site says so, and says what to do =="
# Issue 18. A host whose machine could not reach thunderstore.io pressed Scan, waited, and
# got back the panel that says mods have not been scanned yet with a Scan button on it. The
# rules hold the one-minute ceiling, the failure state and its two buttons, the Upkeep card's
# two connection switches and its test, and that every step and verdict the native test can
# report has a sentence on both sides of the seam.
NETCONN_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/connection_selftest.js"
if [ -f "$NETCONN_SELFTEST" ]; then
  if out=$(node "$NETCONN_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the connection selftest is missing: $NETCONN_SELFTEST"; fail=1
fi

echo "== 20. Duplicate really duplicates =="
# Issue 17. Duplicate switched profile and opened the forge, and the forge makes a brand new
# EMPTY world: the second realm came up with the first one's mods and none of its map, and
# the host found that out by walking into it. The rules hold the source the forge is handed,
# the switch and its default, the payload key riding only when the switch is on, the two
# sentences the world section says, the bridge's own refusals, and the ONE ordering that
# makes the feature work: the copy lands before the first meeting reads the world's header.
DUP_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/duplicate_world_selftest.js"
if [ -f "$DUP_SELFTEST" ]; then
  if out=$(node "$DUP_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the duplicate world selftest is missing: $DUP_SELFTEST"; fail=1
fi

echo "== 21. what a cleanse actually did, read rather than assumed =="
# The same rule as 13 and 14, on the command that takes the cheat marks back off a world.
# baka_cleanse has six things it can say and five of them are not a cleanse; a toast that
# said "cleared" for any of those would send a host away believing a world had been cleaned
# that had not been touched. The table drives the real reader out of app.js against every
# reply the plugin can give, and the rules beside it hold the four-sentence confirm, both
# ways in, and the bridge method the page calls.
CLEANSE_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/cleanse_reply_selftest.js"
if [ -f "$CLEANSE_SELFTEST" ]; then
  if out=$(node "$CLEANSE_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the cleanse reply table is missing: $CLEANSE_SELFTEST"; fail=1
fi

echo "== 22. why a scan came back with nothing, read rather than guessed =="
# The panel that says a scan failed has had its own sentence since 1.2.3, and the sentence was
# the same one whether the site could not be reached, the backoff was still running or the
# server path was missing. The host side knows which of those it was and now says so. The
# rules hold the reply's two new fields, that a scan which checked NOTHING lands on the
# failed state rather than on a table of dashes, that the page renders the host's sentence
# only when it has words for it, and that every id either side can name is in the catalog
# with the slots it declares.
SCAN_REASON_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/mods_scan_reason_selftest.js"
if [ -f "$SCAN_REASON_SELFTEST" ]; then
  if out=$(node "$SCAN_REASON_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the scan reason selftest is missing: $SCAN_REASON_SELFTEST"; fail=1
fi

echo "== 23. the notes under Start with Windows, drawn rather than described =="
# Issue 18's side finding. An entry written while BakaLoader ran as administrator lives in the
# machine wide Run key, an ordinary run cannot remove it, and so turning the switch off left the
# switch reading off while Windows went on starting the app with nothing in the window to say
# so. The rules drive the real reader out of app.js over every shape the host's answer can come
# in, keep the id in the catalog on both sides of the seam, keep the element under the switch it
# is about, and keep the Run key WRITE behind a save that actually MOVED that switch. They
# also keep a save that FAILED from clearing a note it never changed, keep the second note
# from being hidden by the first, and count the notes against the catalog so no comment that
# says how many there are can rot.
STARTWIN_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/start_with_windows_selftest.js"
if [ -f "$STARTWIN_SELFTEST" ]; then
  if out=$(node "$STARTWIN_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the start with windows selftest is missing: $STARTWIN_SELFTEST"; fail=1
fi

echo "== 24. the two Hearth cards after a realm switch =="
# The real-app walk, two realms up. The World saves card kept one set of write times for the
# whole window, so a switch left the header reading the previous realm's clock over a body
# that said nothing had been measured; and uptime counted from the switch rather than from
# the moment the server came up. The rules drive the real chooser and the real painters out
# of app.js and hold the seam that carries the session's own start time across the bridge.
SWITCH_CARDS_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/realm_switch_cards_selftest.js"
if [ -f "$SWITCH_CARDS_SELFTEST" ]; then
  if out=$(node "$SWITCH_CARDS_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the realm switch cards selftest is missing: $SWITCH_CARDS_SELFTEST"; fail=1
fi

echo "== 25. the Saga console's empty states =="
# 1.2.6 gave the console a "no lines match" state for a pill or a search that hides every
# line. It was drawn only when the host changed the pill, and the next log line tore it out
# and put nothing back, so on a running server it lived about a second. These drive the real
# append road and hold the note against the traffic that used to wipe it.
SAGA_EMPTY_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/saga_empty_selftest.js"
if [ -f "$SAGA_EMPTY_SELFTEST" ]; then
  if out=$(node "$SAGA_EMPTY_SELFTEST"); then printf '%s
' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s
' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the saga empty selftest is missing: $SAGA_EMPTY_SELFTEST"; fail=1
fi

echo "== 26. the world integrity note on the Worlds card =="
# The note that says a world's chunk files hold fewer records than its own index promises was
# painted only in front of a start, and never cleared, so a host who never pressed Kindle
# never saw it and a realm switch left the previous realm's warning about lost data standing.
# These drive the real painter and the real refresher.
WORLD_INTEGRITY_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/world_integrity_card_selftest.js"
if [ -f "$WORLD_INTEGRITY_SELFTEST" ]; then
  if out=$(node "$WORLD_INTEGRITY_SELFTEST"); then printf '%s
' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s
' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the world integrity card selftest is missing: $WORLD_INTEGRITY_SELFTEST"; fail=1
fi

echo "== 27. what the page still holds about the previous realm =="
# Eight pieces of state that belong to ONE realm were read for the whole window, and two of
# them wrote to disk: the Runes editor kept the previous realm's text while the list redrew
# for the new one, so one press of Save wrote realm A's scroll into realm B's file; and the
# difficulty dials were keyed on the world NAME alone, so a set abandoned on one realm rode
# into the next and was written by that realm's Save Config. These drive the real choosers
# out of app.js and hold the seam that carries the realm on the scan reply and the write.
SWITCH_STATE_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/realm_switch_state_selftest.js"
if [ -f "$SWITCH_STATE_SELFTEST" ]; then
  if out=$(node "$SWITCH_STATE_SELFTEST"); then printf '%s
' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s
' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the realm switch state selftest is missing: $SWITCH_STATE_SELFTEST"; fail=1
fi

echo "== 28. the armed one-shot cleanse, from the switch to the reply that puts it down =="
# The switch is a field on ONE realm's profile and the sweep it arms fires while nobody is
# watching, so the two things that can quietly be wrong are a switch showing another realm's
# answer and a refusal being read as a result. These drive the real painter and hold both the
# host's reading and the page's wording.
CLEANSE_ARMED_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/cleanse_armed_selftest.js"
if [ -f "$CLEANSE_ARMED_SELFTEST" ]; then
  if out=$(node "$CLEANSE_ARMED_SELFTEST"); then printf '%s
' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s
' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the cleanse armed selftest is missing: $CLEANSE_ARMED_SELFTEST"; fail=1
fi

echo "== 29. every small word against every ground it can sit on =="
# Measured rather than eyeballed. Four colour tokens were under AA on the lighter cards, and
# the one a microlabel's rune was drawn in came out at 2.5 composited. Nothing in the suite
# had an opinion about any of it until this existed. It reads the tokens out of app.css, works
# out which grounds the stylesheet actually paints, and measures every small-text rule whose
# colour is a token against all of them. What it cannot resolve it PRINTS rather than passes.
CONTRAST_GATE="$(dirname "$(dirname "$HERE")")/scripts/ui/contrast_gate.js"
if [ -f "$CONTRAST_GATE" ]; then
  if out=$(node "$CONTRAST_GATE"); then printf '%s
' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s
' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the contrast gate is missing: $CONTRAST_GATE"; fail=1
fi

echo "== 30. no file gains a UTF-8 BOM =="
# A byte-order mark is invisible in every editor and in every diff, and it changes the file.
# A .cs file that gains one still compiles; app.js that gains one is served with three bytes
# in front of its first statement; a shell script that gains one no longer runs at all. One
# batch of edits in 1.2.6 added a BOM to twenty files that had none, which is the whole
# reason this check exists.
#
# It used to compare the CHANGED files against the commit they were changed from, and that
# gave it a lifetime: the moment the work was committed there were no changed files left and
# the rule could never fail again, whatever a later commit did. It reads every tracked text
# file now. The rule is still not "no BOMs", because 34 files here have carried one since
# Visual Studio wrote them; scripts/copy-gate/bom_baseline.txt is the whole of what is
# forgiven, and a file in that list that no longer has one fails too, so the list stays true.
BOM_BASE_LIST="$HERE/bom_baseline.txt"
if git -C . rev-parse --git-dir >/dev/null 2>&1; then
  bomfail=0
  checked=0
  bomseen=""
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    [ -f "$f" ] || continue
    case "$f" in
      *.png|*.jpg|*.jpeg|*.gif|*.ico|*.webp|*.zip|*.dll|*.exe|*.pdb|*.bin|*.snk) continue;;
      *.ttf|*.otf|*.woff|*.woff2|*.mp3|*.wav|*.ogg|*.fwl2|*.db2|*.chunk|*.chunks) continue;;
    esac
    checked=$((checked+1))
    now=$(head -c3 "$f" | od -An -tx1 | tr -d ' 
')
    vouched=0
    if [ -f "$BOM_BASE_LIST" ] && grep -qxF -- "$f" "$BOM_BASE_LIST"; then vouched=1; fi
    if [ "$now" = "efbbbf" ]; then
      bomseen="$bomseen$f
"
      if [ "$vouched" -ne 1 ]; then echo "  gained a BOM: $f"; bomfail=1; fi
    elif [ "$vouched" -eq 1 ]; then
      echo "  the BOM baseline names a file that no longer starts with one: $f (delete the line)"
      bomfail=1
    fi
  done < <(git ls-files 2>/dev/null)
  if [ "$bomfail" -ne 0 ]; then fail=1
  else
    echo "  0 hits ($checked tracked text files read; $(printf '%s' "$bomseen" | grep -c . || true) vouched for in bom_baseline.txt)"
  fi
else
  echo "  not a git checkout, so there is nothing to read"
fi

echo "== 31. every link to this repository names this repository =="
# A link with the wrong owner in it is a 404 for every reader, and nothing in the suite reads
# a URL. 1.2.6 shipped three: the README's new privacy link and two inside the client
# companion's own files, which go out in the release zip and into a Thunderstore listing. The
# rule is narrow on purpose: a github.com link to somebody else's project is ordinary and
# stays, and only a link whose path IS this repository has to name this repository's owner.
# The test project is left out because it holds wrong-owner URLs on purpose, as the fixtures
# that prove the app refuses to open a release page somewhere else.
OWNER="RyanDMcAfee"
linkhits=$(grep -rnoE "github[.]com/[A-Za-z0-9_.-]+/ValheimBakaLoader" \
  --include='*.md' --include='*.json' --include='*.cs' --include='*.js' \
  --include='*.html' --include='*.ps1' --include='*.css' --include='*.sh' \
  --exclude-dir='ValheimBakaLoader.Tests' --exclude-dir='bin' --exclude-dir='obj' \
  --exclude-dir='BuildStaging126' --exclude-dir='Build' \
  . 2>/dev/null | grep -v "github[.]com/$OWNER/ValheimBakaLoader" || true)
if [ -n "$linkhits" ]; then
  printf '%s\n' "$linkhits" | sed 's/^/  wrong owner: /'
  fail=1
else
  echo "  0 hits (every github.com/<owner>/ValheimBakaLoader names $OWNER)"
fi

echo "== 32. the BepInEx notice bar: one fact, one notice, one action =="
# The owner closed the same "BepInEx was left as it was" bar after every launch for two days,
# because a scheduled restart window records the same refusal every few hours and the page
# stood the same fact up again. Beside it were two buttons that both only closed the bar.
# These drive the real painters: a fact that has been read raises nothing, a reason a press
# cannot help with carries its wiki link inside the sentence and one button, and a reason
# where a press does work keeps its offer.
BEP_NOTICE_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/bepinex_notice_selftest.js"
if [ -f "$BEP_NOTICE_SELFTEST" ]; then
  if out=$(node "$BEP_NOTICE_SELFTEST"); then printf '%s
' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s
' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the BepInEx notice selftest is missing: $BEP_NOTICE_SELFTEST"; fail=1
fi

echo "== 33. no English sentence written straight into a call that paints the window =="
# A sentence written into a toast, a dialog or the condition bar reads English on a
# Japanese, Russian or Chinese host, the dash gate counts dashes rather than asking where a
# sentence came from, and check_catalog.py cannot see a sentence that never became an id.
# This reads the title and message arguments of every toast / confirmModal / promptModal /
# setCondition call and fails on prose that is not a catalog lookup.
# logLine is not scanned. The Saga log is English and verbatim by decision, so a sentence
# written straight into a log line is the right thing there rather than a finding.
"$py" "$HERE/scan_copy_literals.py" "$APP/WebUI/app.js" > /tmp/copylit.$$ 2>&1 || true
litn=$(tail -1 /tmp/copylit.$$ | awk '{print $2}')
if [ "$litn" = "0" ]; then sed 's/^/  /' /tmp/copylit.$$
else sed 's/^/  /' /tmp/copylit.$$; fail=1; fi
rm -f /tmp/copylit.$$

echo "== 34. the contrast gate's own blind spots, each proved with a planted defect =="
# A gate is worth what it FAILS on. This one went green over four readable words under 4.5,
# because of five things it could not read: an opacity written as a percentage, an opacity on an
# ancestor rule, a compound class on one element, a colour form the parser did not know, and a
# @keyframes strip that stopped at the first inner brace. Each of those gets a defect planted
# into a scratchpad copy of app.css, and the copy has to fail.
CONTRAST_PROOFS="$(dirname "$(dirname "$HERE")")/scripts/ui/contrast_gate_proofs.js"
if [ -f "$CONTRAST_PROOFS" ]; then
  if out=$(node "$CONTRAST_PROOFS"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the contrast gate proofs are missing: $CONTRAST_PROOFS"; fail=1
fi

echo "== 35. no listener on a shared control is registered above its generic handler =="
# 1.2.6 shipped this twice and nothing could see it: two click listeners on one element run in
# the order they were ADDED, so a listener above the generic one reads the state the element is
# LEAVING. Arming the cheat-mark sweep from the window was impossible, and the App tab's own
# two re-reads were dead. Neither is a syntax error and neither fails a unit test; the only
# thing that tells them apart is source position.
"$py" "$HERE/scan_listener_order.py" "$APP/WebUI/app.js" "$APP/WebUI/index.html" > /tmp/lorder.$$ 2>&1 || true
lon=$(tail -1 /tmp/lorder.$$ | awk '{print $2}')
sed 's/^/  /' /tmp/lorder.$$
[ "$lon" = "0" ] || fail=1
rm -f /tmp/lorder.$$

echo "== 36. every wiki line that says where a control is, against index.html =="
# 1.2.6 moved nine rows off the Upkeep card and the pages went on sending a host to the old
# place in several sentences. Nothing in the suite reads a wiki page, so a page that points at a
# card a switch is not on is exactly as green as one that does not. The pages live outside the
# repository until release day, so the folder is handed in with BAKA_WIKI_DIR, or found beside
# the repository, or the run says out loud with BAKA_WIKI_SKIP=1 that it is not checking them.
PLACEMENT_GATE="$(dirname "$(dirname "$HERE")")/scripts/docs/placement_gate.py"
# NOT RUN used to be a pass, which made the one gate that reads the documentation the one
# gate a run could skip by saying nothing at all. So the folder is looked for before it is
# given up on, and giving up costs the run.
#
# Where a wiki clone sits on a machine that has one: cloning the wiki remote makes
# ValheimBakaLoader.wiki beside the repository, and the publishing script clones the same
# remote into a folder it simply calls wiki. Both names are tried beside the repository and
# one level above it, and a candidate counts only if it holds Home.md, so a stale empty
# folder of that name cannot turn this gate off by existing.
if [ -z "${BAKA_WIKI_DIR:-}" ]; then
  REPO_ROOT="$(dirname "$(dirname "$HERE")")"
  for guess in \
    "$REPO_ROOT/../ValheimBakaLoader.wiki" \
    "$REPO_ROOT/../wiki" \
    "$REPO_ROOT/../../ValheimBakaLoader.wiki" \
    "$REPO_ROOT/../../wiki"
  do
    if [ -f "$guess/Home.md" ]; then
      BAKA_WIKI_DIR="$guess"
      echo "  found the wiki pages at $guess"
      break
    fi
  done
fi
if [ ! -f "$PLACEMENT_GATE" ]; then
  echo "  the placement gate is missing: $PLACEMENT_GATE"; fail=1
elif [ -z "${BAKA_WIKI_DIR:-}" ] && [ "${BAKA_WIKI_SKIP:-}" = "1" ]; then
  echo "  NOT RUN: no wiki folder was found and BAKA_WIKI_SKIP=1 says that is on purpose."
  echo "  Set BAKA_WIKI_DIR to the pages to check them."
elif [ -z "${BAKA_WIKI_DIR:-}" ]; then
  echo "  NOT RUN, and that is a failure: no wiki folder was found, so the pages were not"
  echo "  checked against index.html. Set BAKA_WIKI_DIR to them, or set BAKA_WIKI_SKIP=1 to"
  echo "  say out loud that this run is not checking them."
  fail=1
elif [ ! -d "${BAKA_WIKI_DIR}" ]; then
  echo "  BAKA_WIKI_DIR is set to something that is not a folder: ${BAKA_WIKI_DIR}"; fail=1
else
  "$py" "$PLACEMENT_GATE" "${BAKA_WIKI_DIR}" "$APP/WebUI/index.html" > /tmp/place.$$ 2>&1 || true
  sed 's/^/  /' /tmp/place.$$
  pn=$(tail -1 /tmp/place.$$ | awk '{print $2}')
  [ "$pn" = "0" ] || fail=1
  rm -f /tmp/place.$$
fi

echo "== 37. the Settings hall's tab strip, its App tab, the Connection fold and the switches =="
# Everything the 1.2.6 regroup moved kept its id, so every existing test went on passing and the
# three things the move actually changed had no test at all: which tab is showing (and what goes
# with it), the Connection card's fold, and the palette still landing on the moved controls. Plus
# the switches, which answered a mouse and nothing else.
APP_TAB_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/app_tab_selftest.js"
if [ -f "$APP_TAB_SELFTEST" ]; then
  if out=$(node "$APP_TAB_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the App tab selftest is missing: $APP_TAB_SELFTEST"; fail=1
fi

echo "== 38. the Atlas's place names, at every text size and in the host's own words =="
# Two findings from the real-app walk of 1.2.6, and nothing in the suite could see either. Choosing
# a bigger text size REMOVED every place name from the chart: text size is a page zoom, so the atlas
# wrap shrinks and the auto-fit lands at more metres per CSS pixel, and the label cull was written
# against that number. And the Waypoints list printed Valheim's own localization keys ($enemy_eikthyr,
# $hud_pin_hildir1) while the altars group two rows above already read "Eikthyr". The rules drive the
# real fit arithmetic, the real cull and the real resolver, and hold the pin names against the altar
# names the host side uses for the same bosses.
ATLAS_NAMES_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/atlas_names_selftest.js"
if [ -f "$ATLAS_NAMES_SELFTEST" ]; then
  if out=$(node "$ATLAS_NAMES_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the atlas names selftest is missing: $ATLAS_NAMES_SELFTEST"; fail=1
fi

echo "== 39. a language row with a newer pack published behind it =="
# The owner's own machine read "Pack from 1.2.0; newer sentences in English until the 1.2.6 pack is
# out" on the Russian and Japanese rows while the manifest in the same install named lang-ja-1.2.6.zip
# with every key in it. langRowLine could only ask whether a pack was installed AT ALL, so
# installed-but-older never offered a download and the sentence it did show was not true. The rules
# drive the real choosers and the real row painter over all three states, and hold the one thing an
# Update press must not do: switch the window's language out from under the host.
LANG_ROWS_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/lang_rows_selftest.js"
if [ -f "$LANG_ROWS_SELFTEST" ]; then
  if out=$(node "$LANG_ROWS_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the language rows selftest is missing: $LANG_ROWS_SELFTEST"; fail=1
fi

echo "== 40. the page in a real browser: the names a reader hears and the ink on the chart =="
# Four of the 1.2.7 findings are invisible to every check above, because what they are about is
# what a BROWSER makes of the page: the accessibility tree reported the name "" for every switch
# in the window, the new-realm dialog's four answered a mouse and nothing else, a bigger text size
# took every place name off the map (text size is a page zoom, so it only reads at a real
# devicePixelRatio), and the empty saves chart overflowed its own row. The probe serves WebUI over
# http, drives it headless and reads the tree and the canvas pixels.
#
# It needs node, the playwright package and the chromium build it drives, so it is the one check
# here that can be unavailable. Unavailable is NOT RUN rather than a failure, and it names what is
# missing: the probe answers that question itself (--check), because a gate that worked it out
# for itself would eventually say yes to a box the probe then failed on.
SWITCH_PROBE="$(dirname "$(dirname "$HERE")")/scripts/ui/switch_names_probe.js"
if [ ! -f "$SWITCH_PROBE" ]; then
  echo "  the switch names probe is missing: $SWITCH_PROBE"; fail=1
elif ! command -v node >/dev/null 2>&1; then
  echo "  NOT RUN: there is no node on this box, so nothing read the page in a browser."
elif ! ready=$(node "$SWITCH_PROBE" --check); then
  printf '%s\n' "  NOT RUN: ${ready#switch names probe: }"
  echo "  Nothing above reads the accessibility tree or the chart's pixels, so install it before"
  echo "  a release: npm install -g playwright && npx playwright install chromium"
else
  if out=$(node "$SWITCH_PROBE"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
fi

echo "== 41. a save folder an older build put in the wrong place, and one that moves while the world is up =="
# Issue 17. The shipped default save folder is the literal string
# "%USERPROFILE%\AppData\LocalLow\IronGate\Valheim", and a raw %USERPROFILE% is a folder NAME
# until something fills it in: a duplicate on 1.2.8 made a real folder called %USERPROFILE%
# inside the tester's install and put his world in it. 1.2.9 expands on every read, which
# leaves those worlds out of the app's sight, so the fix owes the host two sentences no unit
# test can see: the condition bar row that names BOTH folders and offers ONE button, and the
# save toast that says BakaLoader follows the new folder while the running server does not.
# The rules hold the row's place in the bar, that both paths reach both sentences, that the
# button is offered for one shape only, both refusals, both roads the row is asked for on,
# the clear on a realm switch, and that the toast branch sits ABOVE the plain running one.
SAVEDIR_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/save_folder_row_selftest.js"
if [ -f "$SAVEDIR_SELFTEST" ]; then
  if out=$(node "$SAVEDIR_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the save folder row selftest is missing: $SAVEDIR_SELFTEST"; fail=1
fi

echo "== 42. what a ground sweep actually cleared, read rather than assumed =="
# The rule 13 holds for the kill sweep, on the World upkeep group's Clear ground items.
# baka_cleardrops answers with its counts, a started line, a busy line, a stopped line, a
# usage sentence and its errors, and a Commander older than the verb forwards it to the
# console and says so, which must read as "this server's plugin is too old" and never as a
# sweep. The table drives the real reader out of app.js against every one of those, and the
# dialog's builder against the plugin's own argument rules.
CLEARDROPS_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/cleardrops_reply_selftest.js"
if [ -f "$CLEARDROPS_SELFTEST" ]; then
  if out=$(node "$CLEARDROPS_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the ground sweep reply table is missing: $CLEARDROPS_SELFTEST"; fail=1
fi

echo "== 43. the cheat-mark controls stand in World upkeep, not on the Players page =="
# 1.2.10 moved Clear cheat marks and its armed switch out of the Players head and into the
# Hearth's World upkeep group, ids and handlers unchanged. Because app.js finds both by id, a
# merge that put the old head back would go on passing every other check here, with the
# button on the wrong page or on two pages at once. The scan reads index.html with a real
# parser and walks each control's parents.
"$py" "$HERE/scan_world_upkeep.py" "$APP/WebUI/index.html" > /tmp/wupk.$$ 2>&1 || true
sed 's/^/  /' /tmp/wupk.$$
wn=$(tail -1 /tmp/wupk.$$ | awk '{print $2}')
[ "$wn" = "0" ] || fail=1
rm -f /tmp/wupk.$$

echo "== 44. the Mod folder button is offered only when the folder is there =="
# The Mods head opens this realm's BepInEx plugins folder. Offered on a realm with no such
# folder it is a press that answers "Folder not found", and offered before the host side has
# said where the folder is it opens the last realm's. The selftest drives the real gate and
# click code out of app.js: a folder enables it and the press sends shell.open "plugins", no
# folder or no answer yet keeps it disabled with a sentence, and a stale realm's reply is dropped.
MODFOLDER_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/modfolder_selftest.js"
if [ -f "$MODFOLDER_SELFTEST" ]; then
  if out=$(node "$MODFOLDER_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the mod folder selftest is missing: $MODFOLDER_SELFTEST"; fail=1
fi

echo "== 45. a stop that ended without a world save: the row, its rank and its close =="
# A stop asks for a save first and watches for the save line; when the process went with none,
# the host sends the fact with every state and the page owes one row for it. The row ranks
# below every live failure (the stop is over, a failing save on a running server is not) and
# above the two lists, is cleared by a realm switch and by a live save, and its close tells
# the host side so a reload does not raise it again.
UNSAVED_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/unsaved_stop_row_selftest.js"
if [ -f "$UNSAVED_SELFTEST" ]; then
  if out=$(node "$UNSAVED_SELFTEST"); then printf '%s
' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s
' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the unsaved stop row selftest is missing: $UNSAVED_SELFTEST"; fail=1
fi

echo "== 46. the mod profile code on the Discord hall, from the radio to the post =="
# 1.2.11 put a mod manager's share code on the status post. It is the one thing on the Discord
# hall that belongs to a realm rather than to the app, so the quiet ways it goes wrong are a line
# naming the wrong manager, a blank box taking something else down with it, a refused code sent
# anyway, and one realm's code left standing on another after a switch. The selftest drives the
# real block out of app.js and holds the page's patterns to the host side's.
MODCODE_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/herald_modcode_selftest.js"
if [ -f "$MODCODE_SELFTEST" ]; then
  if out=$(node "$MODCODE_SELFTEST"); then printf '%s
' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s
' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the herald mod code selftest is missing: $MODCODE_SELFTEST"; fail=1
fi

echo "== 47. the Duplicate dialog's dials open on the source world and mark what differs =="
# With Copy the world on, the dials used to open on Normal and send only what was moved, so a
# Casual source stayed Casual and Normal could not be chosen. The selftest drives the real
# prefill and marking block out of app.js, and holds the word it sends for Normal to the host
# side's parser and the merge that follows the first meeting.
DUPDIALS_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/duplicate_dials_selftest.js"
if [ -f "$DUPDIALS_SELFTEST" ]; then
  if out=$(node "$DUPDIALS_SELFTEST"); then printf '%s
' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s
' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the duplicate dials selftest is missing: $DUPDIALS_SELFTEST"; fail=1
fi

echo "== 48. Mods and Configs read the new folder after the Server Executable moves =="
# Both halls are read off the BepInEx install beside the server executable, and a Save Config
# that moved it left them showing the old install's mods and config files until a restart, with
# the rows and the open file still acting by name on the new one. The selftest drives the real
# re-read, the scan and count that land after a move, and the Refresh press out of app.js.
DISKREAD_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/disk_reread_selftest.js"
if [ -f "$DISKREAD_SELFTEST" ]; then
  if out=$(node "$DISKREAD_SELFTEST"); then printf '%s
' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s
' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the disk reread selftest is missing: $DISKREAD_SELFTEST"; fail=1
fi

echo "== 49. following a Gale profile: the box, the row, its rank and the approval dialog =="
# A followed Gale profile installs mods because somebody else's list moved. The quiet ways it goes
# wrong are a row that outranks a live failure, a row raised by a check that never reached Gale,
# a remembered untick ticked again, Install offered while the server has the plugins loaded, a
# refused code sent anyway, and a half failed walk stamped as approved. The selftest drives the
# real block out of app.js against the real catalog, and holds the host side to its six RPCs.
GALE_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/gale_profile_selftest.js"
if [ -f "$GALE_SELFTEST" ]; then
  if out=$(node "$GALE_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the gale profile selftest is missing: $GALE_SELFTEST"; fail=1
fi

echo "== 50. the Herald's look: the name, the picture, the title, the status line and the link =="
# How every post is signed and headed. The quiet ways it goes wrong are a picture link that is not a
# picture sent anyway or refused in silence, a refused value riding along on the next switch press,
# the line under the boxes promising what the post will not carry, and the page and the host side
# disagreeing about which links are pictures. The selftest drives the real block out of app.js
# against the real catalog, and holds the host side to its rules and its save.
LOOK_SELFTEST="$(dirname "$(dirname "$HERE")")/scripts/ui/herald_look_selftest.js"
if [ -f "$LOOK_SELFTEST" ]; then
  if out=$(node "$LOOK_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the herald look selftest is missing: $LOOK_SELFTEST"; fail=1
fi

# 1.2.15, issue 20. Each of these drives a marked block out of app.js against the real catalog,
# the way 44 to 50 do. The four are one feature each: the Mods table resting on Name with Author
# and the two dates sortable and the sort said in the head; a row's dates, Installed pill and Off
# state with its menu's switch, reinstall and configs; the Configs list's folder prefixes; and a
# realm chip's rune and colour, which come off a file a hand can edit.
UI_DIR="$(dirname "$(dirname "$HERE")")/scripts/ui"
for spec in \
  "51|the Mods table rests on Name, sorts Author and both dates, and says how it is sorted|mod_sort_selftest.js" \
  "52|a Mods row's dates, its Installed pill, the Off state and the row menu's new entries|mod_row_selftest.js" \
  "53|the Configs list draws each file's folders ahead of its name and finds it by them|cfg_list_selftest.js" \
  "54|a realm chip's rune and colour, and the letter for anything the lists do not hold|chip_look_selftest.js"
do
  IFS='|' read -r num what file <<<"$spec"
  echo "== $num. $what =="
  if [ -f "$UI_DIR/$file" ]; then
    if out=$(node "$UI_DIR/$file"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
    else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
  else
    echo "  the selftest is missing: $UI_DIR/$file"; fail=1
  fi
done

echo "== 55. issue 20's layout in a real browser: row padding, the grip and the scrollbar, the sticky head =="
# Three answers only a browser has: whether every settings row is padded the same, whether the
# right-hand resize grip still covers the scrollbar's thumb, and whether the Dashboard, Mods and
# Settings heads stay on screen while their halls scroll. Since 1.3.0 (issue 27) the third also
# asks it of every section head, at the top and at the foot of the pane, and where a section
# opened from the top lands once it has rolled open; and of the Map hall, where Statistics stands
# since 1.3.0, that the map fills the pane, keeps its size and its zoom through every fold, and
# zooms under the wheel while the hall scrolls everywhere else. Same readiness question as 40 asks.
LAYOUT_PROBE="$UI_DIR/issue20_layout_probe.js"
if [ ! -f "$LAYOUT_PROBE" ]; then
  echo "  the layout probe is missing: $LAYOUT_PROBE"; fail=1
elif ! command -v node >/dev/null 2>&1; then
  echo "  NOT RUN: there is no node on this box, so nothing read the page in a browser."
elif ! ready=$(node "$SWITCH_PROBE" --check); then
  printf '%s\n' "  NOT RUN: ${ready#switch names probe: }"
else
  if out=$(node "$LAYOUT_PROBE"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
fi

echo "== 56. a test copy of a realm: its names, the listing held off, the character, and its T =="
# 1.2.16, issue 20. A test copy is only worth having while it stays apart from its source: its
# own name and world, never listed, and a copy of a character rather than the character. The
# selftest drives the real TEST-COPY block out of app.js against the real catalog, and holds the
# host side to the listing it refuses and the refusals it words.
TESTCOPY_SELFTEST="$UI_DIR/test_copy_selftest.js"
if [ -f "$TESTCOPY_SELFTEST" ]; then
  if out=$(node "$TESTCOPY_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the test copy selftest is missing: $TESTCOPY_SELFTEST"; fail=1
fi

echo "== 57. the hearth fire's gate, its frame cap, and the idle pause on every endless animation =="
# 1.2.10 wrote this selftest and nothing ever ran it: it was in no list here and CI ran no node at
# all, so a new endless animation without its body.idle twin, or a loop that ran on behind the
# game, would have gone out green. It lifts flameShouldRun, the frame cap and the window.active
# handler out of app.js and reads app.css for the idle twins.
FLAME_SELFTEST="$UI_DIR/flame_gate_selftest.js"
if [ -f "$FLAME_SELFTEST" ]; then
  if out=$(node "$FLAME_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the flame gate selftest is missing: $FLAME_SELFTEST"; fail=1
fi

echo "== 58. what a mod needs: the row's pill, its menu entry, the dialog and the walk, the Gale walk left alone =="
# 1.2.16. A mod installed by link without its core library never loaded, and nothing said so. The
# quiet ways the fix goes wrong are a pill on a hand copy, an off dependency fetched over instead
# of switched on, a walk that goes on past a running server, and the Gale walk or a Reinstall
# opening a dialog nobody is there to answer. The selftest drives the real blocks out of app.js
# against the real catalog, and holds the host side to the field on the add and on the scan.
DEPS_SELFTEST="$UI_DIR/mod_deps_selftest.js"
if [ -f "$DEPS_SELFTEST" ]; then
  if out=$(node "$DEPS_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the mod deps selftest is missing: $DEPS_SELFTEST"; fail=1
fi

echo "== 59. the Herald per realm: the hall edits the realm on screen, Publish and Remove act on its post =="
# 1.2.16, issue 24. Each realm keeps a Discord post of its own, so the hall edits the realm on
# screen. The quiet ways that goes wrong are a realm switch leaving the last realm's settings on
# screen for the next save to write onto this one, a press landing on whichever realm is on screen
# when the reply comes back, and the master switch losing its place over every realm. The selftest
# drives the real block out of app.js against the real catalog and holds the host side's RPCs.
REALM_SELFTEST="$UI_DIR/herald_realm_selftest.js"
if [ -f "$REALM_SELFTEST" ]; then
  if out=$(node "$REALM_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the herald realm selftest is missing: $REALM_SELFTEST"; fail=1
fi

echo "== 60. the Herald's event posts: three kinds per realm, each with its own switch and place =="
# 1.2.16, issue 24. One "Event posts" switch became Server news, Crashes and players per realm, each
# with its own webhook and thread, and a role to ping on a crash. The quiet ways that goes wrong are
# a realm switch leaving the last realm's routing on screen for the next save, a box the page sends
# that the host side never reads, and the wizard's one switch flattening a host's choice. The
# selftest drives the real block out of app.js and holds every key to the host side's apply.
EVENTS_SELFTEST="$UI_DIR/herald_events_selftest.js"
if [ -f "$EVENTS_SELFTEST" ]; then
  if out=$(node "$EVENTS_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the herald events selftest is missing: $EVENTS_SELFTEST"; fail=1
fi

echo "== 61. the Herald's preview: the hall draws the post a publish sends =="
# 1.2.16, issue 24. The Discord hall draws the realm's post as Discord would, from discord.preview,
# which takes it from the code a publish sends. The quiet ways that goes wrong are something the
# host typed reaching the page as markup, a reply for the realm the host just left painting over
# the realm on screen, and an edit that never asks again. The selftest drives the real block.
PREVIEW_SELFTEST="$UI_DIR/herald_preview_selftest.js"
if [ -f "$PREVIEW_SELFTEST" ]; then
  if out=$(node "$PREVIEW_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the herald preview selftest is missing: $PREVIEW_SELFTEST"; fail=1
fi

echo "== 62. the Discord screen: one card about the realm on screen, a (?) on every heading, each tick's boxes under it =="
# 1.2.17. The owner found the screen before it confusing: an app-wide switch and webhook beside a
# realm's own, and every kind of message with its own boxes on the main screen. The quiet ways
# the one card goes wrong are a control drifting back onto it or out of order, a heading without
# its (?) or a (?) only a mouse can open, and a dialog closed by Escape losing what was typed or
# keeping the panel the saves read. The selftest drives the real block and reads the markup.
SCREEN_SELFTEST="$UI_DIR/herald_screen_selftest.js"
if [ -f "$SCREEN_SELFTEST" ]; then
  if out=$(node "$SCREEN_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the herald screen selftest is missing: $SCREEN_SELFTEST"; fail=1
fi

echo "== 63. nothing on the page appears, vanishes or jumps: the one show/hide helper =="
# 1.2.17 follow-up, the owner's rule for all of the page: a part that shows rolls down and fades
# in, one that hides rolls up and fades out, and the rows under it slide with it; reduced motion
# gets a plain fade. The Discord screen uses it first and the app-wide sweep reuses it, so its
# quiet failures (a height left pinned, a fast second click undone by the first roll, movement
# under reduced motion) are proved once here against the real REVEAL block.
REVEAL_SELFTEST="$UI_DIR/reveal_selftest.js"
if [ -f "$REVEAL_SELFTEST" ]; then
  if out=$(node "$REVEAL_SELFTEST"); then printf '%s
' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s
' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the reveal selftest is missing: $REVEAL_SELFTEST"; fail=1
fi

echo "== 64. the sweep: every show and hide a click can cause goes through the helper, the overlays fade =="
# 1.2.18. The rule above, applied to the whole page: a style.display written by hand where a click
# reaches it, a fold the stylesheet still hides by class (reveal cannot show it), a menu or dialog
# that vanishes rather than fading, an entrance that moves under reduced motion, and the helper's
# additions ({display}, {now}, {done}) coming undone are each caught here. docs/motion-sweep.md is
# the record the selftest holds the allow list against.
SWEEP_SELFTEST="$UI_DIR/motion_sweep_selftest.js"
if [ -f "$SWEEP_SELFTEST" ]; then
  if out=$(node "$SWEEP_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the motion sweep selftest is missing: $SWEEP_SELFTEST"; fail=1
fi

echo "== 65. the simpler sidebar: five halls, the other four as remembered sections, every road still landing =="
# 1.2.18. The rail lists Dashboard, Players, Map, Mods, Settings; the Log, Statistics, Configs and
# Discord are folding sections of those halls, remembered per section through userprefs, each
# behind a glowing chevron, and every old road (the palette, the condition bar, a mod row's
# configs entry, a toast's action) opens the hall, unfolds the section and lights it. The quiet
# failures (a hall dropped with its ids, a default or a saved answer not honoured, a road still
# walking to a hall the rail lost, a check still reading currentPage for a section, the
# preference missing on one side of the bridge) are each caught here, against docs/sidebar.md.
# Since 1.3.0 (issue 27) Statistics is a section under the map on the Map hall, whose map is a
# section too; rule 9 holds the map's size and zoom through a fold, the fit owed while it is
# folded, and Statistics read again for the realm on screen.
SIDEBAR_SELFTEST="$UI_DIR/sidebar_sections_selftest.js"
if [ -f "$SIDEBAR_SELFTEST" ]; then
  if out=$(node "$SIDEBAR_SELFTEST"); then printf '%s\n' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s\n' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the sidebar sections selftest is missing: $SIDEBAR_SELFTEST"; fail=1
fi

echo "== 66. the world box answers for the realm on screen, never the last one =="
# Issue 25. A realm switch lands before the world list is fetched again, and the Editing bar (and
# Save) read the last realm's world out of the box in that window. The box now says which realm
# it was filled for, and the refill draws the bar again.
WORLD_REALM_SELFTEST="$UI_DIR/world_field_realm_selftest.js"
if [ -f "$WORLD_REALM_SELFTEST" ]; then
  if out=$(node "$WORLD_REALM_SELFTEST"); then printf '%s
' "$out" | tail -1 | sed 's/^/  /'
  else printf '%s
' "$out" | sed 's/^/  /'; fail=1; fi
else
  echo "  the world field realm selftest is missing: $WORLD_REALM_SELFTEST"; fail=1
fi

[ $fail -eq 0 ] && echo "GATE: PASS" || echo "GATE: FAIL"
exit $fail
