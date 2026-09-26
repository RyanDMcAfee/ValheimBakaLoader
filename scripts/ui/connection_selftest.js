/* Issue 18 on the page: a scan that failed says so, and the card that fixes it exists.
 *
 * WHY THIS EXISTS. A host whose machine could not reach thunderstore.io pressed Scan, waited,
 * and got back the panel that says mods have not been scanned yet, with a Scan button on it.
 * Nothing on the page ever said the site had not answered, so the only thing left to do was
 * press the same button again. Three things had to change together, and this holds all three:
 * the scan gives up after a minute instead of spinning for ever; a failure is its own state
 * with its own sentence, a Retry and a way to the connection test; and the Upkeep card really
 * carries the two switches and the test the failure state sends a host to.
 *
 *   1. The ceiling. scanMods races the call against a timer and reads which one settled.
 *   2. The failure state. renderMods draws three states, not two, and the unscanned one is
 *      never what a failure lands on.
 *   3. The reason sentences are named in a table and every one of them is in the catalog,
 *      so a state cannot be drawn with an id in it.
 *   4. The Upkeep card really holds the two switches, the test button and a result element
 *      nothing else writes, the switches ride in and out on userprefs, and the RPC the test
 *      calls is one the bridge registers.
 *   5. The test's own stage and verdict sentences are a table too, and all of them exist.
 *   6. The Detailed log switch is on that same card, rides in and out on userprefs like the
 *      other two, and is GATED rather than movable when the command line already set the
 *      level: a switch offering a choice the command line has already made is a lie about
 *      what the window can do.
 *
 * Prints one line per rule and exits non zero on the first failure.
 *
 *     node scripts/ui/connection_selftest.js [app.js] [index.html] [en.json]
 *
 * The paths are there so a mutation can be driven without touching the tree. Run against
 * 4fc598e's app.js every one of the five fails, which is what makes this a gate.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const PAGE = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "index.html");
const CATALOG = process.argv[4] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");
const BRIDGE = path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

const SOURCE = fs.readFileSync(APP, "utf8");
const MARKUP = fs.readFileSync(PAGE, "utf8");
const KEYS = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys;

let passed = 0;
const failures = [];

function test(name, body) {
  try {
    body();
    passed++;
    console.log("  ok   " + name);
  } catch (problem) {
    failures.push(name);
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}

/** One whole top level function out of app.js, named by its opening line. */
function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from > 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to);
}

/** A const table out of app.js, evaluated rather than parsed by eye. */
function table(name) {
  const from = SOURCE.indexOf("const " + name + "={");
  assert.ok(from > 0, "app.js no longer holds " + name);
  const to = SOURCE.indexOf("\n};", from);
  assert.ok(to > from, name + " no longer closes at the left margin");
  const context = {};
  vm.createContext(context);
  vm.runInContext(SOURCE.slice(from, to + 3) + "\nthis.TABLE=" + name + ";",
    context, { filename: "app.js#" + name });
  return context.TABLE;
}

/* ------------------------------------------------------------------ rule 1: the ceiling */
test("a scan that never answers is given up on after a minute", () => {
  const body = fn("async function scanMods(");

  const ceiling = /const\s+SCAN_CEILING_MS\s*=\s*(\d+)/.exec(SOURCE);
  assert.ok(ceiling, "app.js names no ceiling for a scan, so a scan that never answers spins for ever");
  assert.strictEqual(Number(ceiling[1]), 60000,
    "the scan ceiling is " + ceiling[1] + "ms, and the release says a minute");

  assert.ok(/Promise\.race\(/.test(body),
    "scanMods still awaits the call on its own, so nothing ends a scan that never answers");
  assert.ok(/setTimeout\([^)]*SCAN_CEILING_MS|SCAN_CEILING_MS\)/.test(body),
    "the race is not run against the ceiling");
  assert.ok(/modsScanError\s*=\s*"timeout"/.test(body),
    "a scan that timed out is not written down as one, so the page cannot say which failure it was");
});

/* --------------------------------------------- rule 2: a failure is not the unscanned state */
test("a failed scan draws its own state, never the one that says nothing has been read", () => {
  const body = fn("function renderMods(");

  assert.ok(/const\s+failed\s*=\s*!S\.modsScanning\s*&&\s*S\.modsScanError/.test(body),
    "renderMods does not read the scan failure at all, so a failure still lands on the"
    + " unscanned panel with a Scan button on it");

  const empty = body.indexOf("mods.empty.unscanned.title");
  const fail = body.indexOf("mods.empty.failed.title");
  assert.ok(fail > 0, "there is no failed empty state");
  assert.ok(fail < empty,
    "the failed state is chosen after the unscanned one, so the unscanned one wins");

  assert.ok(/action:\{name:"scanMods"/.test(body.slice(fail, empty)),
    "the failed state offers no way to try again");
  assert.ok(/action2:\{name:"openConnectionTest"/.test(body.slice(fail, empty)),
    "the failed state offers no way to the connection test");

  assert.ok(/mods\.index\.line\.failed/.test(body),
    "the line under the Mods heading still says the index was not scanned after a failure");

  const scan = fn("async function scanMods(");
  assert.ok(/S\.modsScanError\s*=\s*"failed"/.test(scan),
    "a refused scan is not written down, so the hall goes back to the unscanned state");
  assert.ok(/S\.modsScanError\s*=\s*null/.test(scan),
    "the failure is never cleared, so a scan that works still shows the failure state");
});

/* ------------------------------------------- rule 3: every reason sentence is in the catalog */
test("the reason sentences are a table and every one of them is worded", () => {
  const reasons = table("MODS_SCAN_FAIL");
  const names = Object.keys(reasons).sort();
  assert.deepStrictEqual(names, ["failed", "timeout"],
    "the failure kinds moved: " + names.join(", "));

  for (const name of names) {
    const id = reasons[name].reasonId;
    assert.ok(id, name + " names no sentence");
    assert.ok(KEYS[id], "the catalog has no " + id + ", so the panel would show the id");
  }

  ["mods.empty.failed.title", "mods.empty.failed.action", "mods.empty.failed.action.test",
   "mods.index.line.failed", "mods.scan.timeout.toast"].forEach(id => {
    assert.ok(KEYS[id], "the catalog has no " + id);
  });

  // The empty state can carry two buttons now, because a failure has two things to do
  // about it. A single-button emptyState would silently drop the second one.
  assert.ok(/o\.action2/.test(fn("function emptyState(")),
    "emptyState draws only one button, so the connection-test button is dropped in silence");
});

/* ------------------------------------------ rule 4: the card the failure state sends them to */
test("the Upkeep card holds the two switches, the test and a result line", () => {
  ['id="tNoProxy"', 'id="tIPv4"', 'id="btnNetTest"', 'id="netTestOut"'].forEach(needle => {
    assert.ok(MARKUP.indexOf(needle) >= 0, "index.html no longer carries " + needle);
  });

  // The result line is written by app.js, so the walker must not own it as well.
  assert.ok(!/id="netTestOut"[^>]*data-i18n/.test(MARKUP),
    "netTestOut carries a data-i18n as well as being written by app.js: two owners, one element");

  assert.ok(/setT\("tNoProxy"/.test(SOURCE) && /setT\("tIPv4"/.test(SOURCE),
    "the two switches are never drawn from what is stored, so they show whatever the"
    + " document shipped with");
  assert.ok(/BypassSystemProxy:swOn\("tNoProxy"\)/.test(SOURCE)
    && /ForceIPv4:swOn\("tIPv4"\)/.test(SOURCE),
    "the two switches are never saved, so moving one is forgotten at the next launch");

  assert.ok(/\$\("#btnNetTest"\)\?\.addEventListener\("click",runConnectionTest\)/.test(SOURCE),
    "the Test connection button is wired to nothing");
  assert.ok(/openConnectionTest:\(\)=>/.test(SOURCE),
    "the named action the failed scan offers does not exist, so its button does nothing");

  const bridge = fs.readFileSync(BRIDGE, "utf8");
  assert.ok(bridge.indexOf('RegisterRpc("net.diagnose"') > 0,
    "the bridge registers no net.diagnose, so the button calls a method that is not there");
});

/* ----------------------------------------- rule 5: the test's own sentences, all of them */
test("every step and every verdict the test can report has a sentence", () => {
  const stages = table("NET_STAGES");
  const verdicts = table("NET_VERDICTS");

  const wantStages = ["proxy", "dns", "tcp", "tls", "http", "http_noproxy", "http_ipv4", "http_both"];
  assert.deepStrictEqual(Object.keys(stages).sort(), wantStages.slice().sort(),
    "the steps moved: " + Object.keys(stages).join(", "));

  const wantVerdicts = ["ok", "noproxy", "ipv4", "both", "none"];
  assert.deepStrictEqual(Object.keys(verdicts).sort(), wantVerdicts.slice().sort(),
    "the verdicts moved: " + Object.keys(verdicts).join(", "));

  for (const key of Object.keys(stages))
    assert.ok(KEYS[stages[key].lineId], "the catalog has no " + stages[key].lineId);
  for (const key of Object.keys(verdicts))
    assert.ok(KEYS[verdicts[key].lineId], "the catalog has no " + verdicts[key].lineId);

  // The native side names the same steps and the same verdicts, or the page would word an
  // answer it never gets and drop the one it does.
  const diagnostics = fs.readFileSync(
    path.join(ROOT, "ValheimBakaLoader", "Tools", "ConnectionDiagnostics.cs"), "utf8");
  for (const key of wantStages)
    assert.ok(diagnostics.indexOf('"' + key + '"') > 0,
      "the native test never reports a step called " + key);
  for (const key of wantVerdicts)
    assert.ok(diagnostics.indexOf('"' + key + '"') > 0,
      "the native test never answers with the verdict " + key);
});

/* ------------------------------------- rule 6: the third switch, and when it cannot be moved */
test("the Detailed log switch is on the card, saved, and gated under --verbose", () => {
  ['id="rowDetailedLog"', 'id="tDetailedLog"', 'id="detailedLogForced"'].forEach(needle => {
    assert.ok(MARKUP.indexOf(needle) >= 0, "index.html no longer carries " + needle);
  });

  // Both sentences on the row are the walker's, so the card reads correctly on the frame
  // before the catalog lands, exactly like the two switches above it.
  ["hearth.upkeep.connection.detailed_log",
   "hearth.upkeep.connection.detailed_log.note",
   "hearth.upkeep.connection.detailed_log.forced"].forEach(id => {
    assert.ok(KEYS[id], "the catalog has no " + id);
    assert.ok(MARKUP.indexOf('data-i18n="' + id + '"') >= 0,
      "index.html does not word " + id + " on the first frame");
  });

  // Drawn from what is stored, and saved when it moves, the same as the other two.
  assert.ok(/setT\("tDetailedLog"/.test(SOURCE),
    "the switch is never drawn from what is stored, so it shows whatever the document"
    + " shipped with");
  assert.ok(/DetailedLog:detailedLogSaved\(\)/.test(SOURCE),
    "the switch is never saved, so moving it is forgotten at the next launch");

  // The command line wins for the session, so the row is gated rather than movable and the
  // PREFERENCE is left exactly as the host set it.
  assert.ok(/DETAILED_LOG_FORCED=!!up\.DetailedLogForcedByCommandLine/.test(SOURCE),
    "the page never reads whether the command line set the level");
  assert.ok(/function detailedLogSaved\(\)\{[\s\S]{0,200}DETAILED_LOG_FORCED\?DETAILED_LOG_PREF/.test(SOURCE),
    "a save under --verbose writes the drawn state back, which turns a host's off into on");
  assert.ok(/row\.classList\.toggle\("gated",DETAILED_LOG_FORCED\)/.test(SOURCE),
    "the row is not gated under --verbose, so it offers a choice that is not there");
  assert.ok(/closest\("#tDetailedLog"\)[\s\S]{0,240}preventDefault\(\)/.test(SOURCE),
    "nothing stops a click on the gated switch, and the generic [data-t] handler flips it"
    + " before any listener of ours runs");

  // The row's tooltip is worded from the catalog ONCE, at init, so a language switch has
  // to re-word it the way it re-words every other painted-from-state surface. Without
  // that, a host who switches language reads the gate's note in the language they left.
  assert.ok(/try\{syncDetailedLogGate\(\);\}catch\(_\)\{\}/.test(SOURCE),
    "the gated row's tooltip is never drawn again, so a language switch leaves the old"
    + " wording on it");
  const repaint = SOURCE.slice(SOURCE.indexOf("function repaintBootCopy(){"));
  assert.ok(repaint.indexOf("syncDetailedLogGate()") > 0
    && repaint.indexOf("syncDetailedLogGate()") < repaint.indexOf("\n}"),
    "the redraw that every language switch goes through does not include the gated row");

  // And the far side really sends the flag the page reads.
  const bridge = fs.readFileSync(BRIDGE, "utf8");
  assert.ok(bridge.indexOf("DetailedLogForcedByCommandLine") > 0,
    "the bridge never tells the page the command line set the level");
  assert.ok(bridge.indexOf('Apply("DetailedLog"') > 0,
    "userprefs.save ignores DetailedLog, so the switch saves nothing");
});

/* ------------------------------------------- rule 7: the copy says what the switch does */
test("the switch's own note describes the lines it really writes", () => {
  const note = KEYS["hearth.upkeep.connection.detailed_log.note"].lore;

  // TWO lines per request, not one. The handler writes one when the request goes out and
  // one when it comes back, and under IPv4 only there is a connect line as well. "One line
  // per web request" was a number a host could count and find wrong on the first read.
  assert.ok(!/one line per web request/i.test(note),
    "the note still promises one line per request, and the handler writes two");
  assert.ok(/goes out/i.test(note) && /comes back/i.test(note),
    "the note does not say a line goes out and a line comes back");

  // And it still says what is deliberately kept out, which is what makes the file safe to
  // attach to a public issue.
  ["header", "body", "query string", "webhook key"].forEach(word => {
    assert.ok(note.toLowerCase().indexOf(word) >= 0,
      "the note no longer says " + JSON.stringify(word) + " is kept out of the log");
  });
});

console.log("");
if (failures.length) {
  console.log("connection selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("connection selftest: " + passed + " passed");
