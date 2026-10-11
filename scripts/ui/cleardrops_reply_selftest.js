/* What baka_cleardrops said, as a table, plus the dialog's command builder.
 *
 * WHY THIS EXISTS. The ground sweep answers on the line RCON carries with every one of: its
 * counts, a "started" line for a world too big to walk inside the answer, a refusal because
 * one is already walking, a stopped-early line, a usage sentence and its "Error:" lines. A
 * Commander older than 1.9.0 has never heard of the verb and answers that it forwarded the
 * line to the console, which reads like an answer and is not one: nothing was cleared. The
 * window has to read each of those for what it is, and killall_reply_selftest.js already
 * proved what happens when a toast is chosen before the server has answered.
 *
 * THE TABLE. Every reply the plugin can produce, word for word out of
 * ValheimBakaLoader/Resources/Commander/BakaClearDropsPlan.cs (the same strings
 * ClearDropsPlanTests pins on the C# side), read by the REAL reader: the block between the
 * CLEARDROPS markers in app.js, evaluated here. A copy of that function in this file would
 * pass forever while the page read something else. The dialog's command builder is driven
 * the same way, because the line it writes is what actually reaches the server.
 *
 * Prints one line per case and exits non zero on the first failure.
 *
 * node scripts/ui/cleardrops_reply_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const PLAN = path.join(ROOT, "ValheimBakaLoader", "Resources", "Commander", "BakaClearDropsPlan.cs");

let passed = 0;
const failures = [];

function test(name, body) {
  try {
    body();
    passed++;
    console.log("  ok   " + name);
  } catch (problem) {
    failures.push(name + ": " + (problem && problem.message ? problem.message : problem));
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}

const SOURCE = fs.readFileSync(APP, "utf8");

/** Pulls one marked block out of app.js. */
function block(begin, end) {
  const from = SOURCE.indexOf(begin);
  const to = SOURCE.indexOf(end);
  assert.ok(from >= 0 && to > from, "app.js no longer carries the " + begin + " markers");
  return SOURCE.slice(from, to);
}

/** The real reader and the real builder, out of app.js. consoleRefused lives in the
    KILLALL block and the reader calls it, so that block comes along too. */
function readers() {
  const context = {};
  vm.createContext(context);
  vm.runInContext(block("/* KILLALL-BEGIN", "/* KILLALL-END */"), context, { filename: "app.js#killall" });
  vm.runInContext(block("/* CLEARDROPS-BEGIN", "/* CLEARDROPS-END */"), context, { filename: "app.js#cleardrops" });
  for (const name of ["clearDropsReply", "clearDropsCounts", "clearDropsCommand", "clearDropsBlank"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  return context;
}

const C = readers();

/* ------------------------------------------------------------------ the replies
   Every string below is the plugin's own, assembled the way ClearDropsPlan assembles it:
   Reply(), Counts(), Started(), AlreadyRunning(), StoppedEarly(), Usage, BadRadius,
   BadKeepGear, NotReady, NoIndex; Commander's own "Error: command timed out" and its
   fallback for a verb it does not know.                                                  */

const DOT = " · ";

const CASES = [
  // --- a finished sweep, with counts ---
  ["cleardrops: removed 412" + DOT + "kept 9 gear" + DOT + "kept 31 near players" + DOT + "scanned 184220 objects",
    { kind: "complete", removed: 412, gear: 9, near: 31, scanned: 184220, unreadable: 0 }],
  ["cleardrops: removed 1" + DOT + "kept 0 gear" + DOT + "kept 0 near players" + DOT + "scanned 1 object",
    { kind: "complete", removed: 1, gear: 0, near: 0, scanned: 1, unreadable: 0 }],
  // --- with the unreadable clause on the end ---
  ["cleardrops: removed 40" + DOT + "kept 2 gear" + DOT + "kept 3 near players" + DOT + "scanned 9000 objects" + DOT + "kept 4 unreadable",
    { kind: "complete", removed: 40, gear: 2, near: 3, scanned: 9000, unreadable: 4 }],
  // --- zero removed: a finished sweep that had nothing to do ---
  ["cleardrops: removed 0" + DOT + "kept 12 gear" + DOT + "kept 7 near players" + DOT + "scanned 50211 objects",
    { kind: "complete", removed: 0, gear: 12, near: 7, scanned: 50211, unreadable: 0 }],
  ["cleardrops: removed 0" + DOT + "kept 0 gear" + DOT + "kept 0 near players" + DOT + "scanned 0 objects",
    { kind: "complete", removed: 0, gear: 0, near: 0, scanned: 0, unreadable: 0 }],
  // --- the middle dot after an older encoding turned it into a question mark ---
  ["cleardrops: removed 5 ? kept 1 gear ? kept 0 near players ? scanned 300 objects",
    { kind: "complete", removed: 5, gear: 1, near: 0, scanned: 300, unreadable: 0 }],

  // --- too big to finish inside the answer ---
  ["cleardrops: started on 5120 ground items out of 184220 objects. The result line follows in the server log when the sweep finishes.",
    { kind: "started", items: 5120 }],
  ["cleardrops: started on 1 ground item out of 2 objects. The result line follows in the server log when the sweep finishes.",
    { kind: "started", items: 1 }],

  // --- a second sweep while the first is still walking ---
  ["cleardrops: already running, 300 ground items still to go. Wait for its result line before starting another.",
    { kind: "busy" }],

  // --- it threw part way, and the counts it reached are real work ---
  ["cleardrops: stopped early: NullReferenceException. Up to that point: removed 3" + DOT + "kept 1 gear" + DOT + "kept 0 near players" + DOT + "scanned 90 objects.",
    { kind: "stopped", removed: 3, gear: 1, near: 0, scanned: 90 }],
  // not a line the plugin writes today, which always carries its counts; read anyway, so a
  // plugin that loses them is said to have stopped rather than to have cleared nothing
  ["cleardrops: stopped early: no reason given.", { kind: "stopped", counted: false }],

  // --- every refusal ---
  ["Error: the radius has to be a number of metres from 0 to 1000, like 32.", { kind: "refused" }],
  ["Error: keepgear takes 1 to keep gear on the ground or 0 to clear it too.", { kind: "refused" }],
  ["Error: server not ready (world still loading)", { kind: "refused" }],
  ["Error: this build of Valheim keeps its world objects somewhere cleardrops cannot read, so nothing was touched. The plugin needs rebuilding against it.",
    { kind: "refused" }],
  ["Error: the world's object index could not be read (bad cast), so nothing was touched.", { kind: "refused" }],
  ["Error: cleardrops could not start: object reference not set to an instance of an object", { kind: "refused" }],
  ["Error: command timed out (server main thread busy)", { kind: "refused" }],
  ["Usage: baka_cleardrops [keepgear=0|1] [radius=<metres>]. Both are optional: gear is kept and nothing within 32 metres of a player is touched unless you say otherwise. radius=0 clears right up to the players' feet.",
    { kind: "refused" }],

  // --- the server is not running: the plugin is not there to answer at all, and RCON
  //     carries nothing back. sendConsole says "not delivered" before the reader is ever
  //     asked, and an empty answer from a server that IS there is its own kind. ---
  ["", { kind: "silent" }],
  ["   \r\n  ", { kind: "silent" }],
  [null, { kind: "silent" }],
  [undefined, { kind: "silent" }],

  // --- a Commander older than the verb: unknown command, in both of its spellings ---
  ["Forwarded to console: baka_cleardrops keepgear=1 radius=32", { kind: "unsupported" }],
  // (the list in brackets is a 1.8.1 Commander's, which is the one that never heard of the verb)
  ["Unknown command: 'baka_cleardrops keepgear=1 radius=32' (Commander natively supports: broadcast, playerlist, dmg, tp, kick, baka_spawn, baka_killall, baka_cleanse, baka_cleanse_status)",
    { kind: "unsupported" }],

  // --- a shape this version has not met ---
  ["cleardrops: removed some of it", { kind: "unknown" }],
  ["cleardrops queued.", { kind: "unknown" }],
  ["KillAll complete: 1 hostile slain, 0 out of reach, 0 spared (players, pets & allies)", { kind: "unknown" }],
];

CASES.forEach(([reply, want]) => {
  const shown = reply == null ? String(reply) : JSON.stringify(String(reply).slice(0, 56));
  test("reads " + shown, () => {
    const got = C.clearDropsReply(reply);
    assert.strictEqual(got.kind, want.kind, "kind was " + got.kind);
    for (const field of ["removed", "gear", "near", "scanned", "unreadable", "items", "counted"]) {
      if (field in want) assert.strictEqual(got[field], want[field], field + " was " + got[field]);
    }
  });
});

test("only the finished-sweep rows read as a result", () => {
  const complete = CASES.filter(([r]) => C.clearDropsReply(r).kind === "complete").length;
  assert.strictEqual(complete, 6, "the finished-sweep rows moved: " + complete);
});

/* ------------------------------------------- the table against the plugin's own words */

test("the plugin still opens its lines the way this table does", () => {
  /* The C# side is pinned by ClearDropsPlanTests. This is the seam: if a phrase the reader
     keys on stops appearing in the plan's source, the table above is reading a plugin that
     no longer exists. */
  const plan = fs.readFileSync(PLAN, "utf8");
  for (const phrase of [
    'ReplyPrefix = "cleardrops:"', '" started on "', '" already running, "', '" stopped early: "',
    '"removed "', '" gear"', '" near players"', '"scanned "', '" unreadable"',
    '"Usage: baka_cleardrops', '"Error: the radius', '"Error: keepgear', '"Error: server not ready',
    'Dot = " \\u00B7 "',
  ]) assert.ok(plan.includes(phrase), "BakaClearDropsPlan.cs no longer carries " + phrase);
});

/* -------------------------------------------------------------- the command builder */

const BUILDS = [
  [{}, { cmd: "baka_cleardrops keepgear=1 radius=32" }],
  [null, { cmd: "baka_cleardrops keepgear=1 radius=32" }],
  [{ keepGear: true, keepNear: true, radius: "32" }, { cmd: "baka_cleardrops keepgear=1 radius=32" }],
  [{ keepGear: false, keepNear: true, radius: "50" }, { cmd: "baka_cleardrops keepgear=0 radius=50" }],
  [{ keepGear: true, keepNear: false, radius: "50" }, { cmd: "baka_cleardrops keepgear=1 radius=0" }],
  [{ keepGear: false, keepNear: false, radius: "" }, { cmd: "baka_cleardrops keepgear=0 radius=0" }],
  // a switched-off radius ignores whatever is sitting in the box
  [{ keepGear: true, keepNear: false, radius: "wide" }, { cmd: "baka_cleardrops keepgear=1 radius=0" }],
  [{ keepNear: true, radius: " 64 " }, { cmd: "baka_cleardrops keepgear=1 radius=64" }],
  [{ keepNear: true, radius: "12,5" }, { cmd: "baka_cleardrops keepgear=1 radius=12.5" }],
  [{ keepNear: true, radius: "1000" }, { cmd: "baka_cleardrops keepgear=1 radius=1000" }],
  [{ keepNear: true, radius: "32,5" }, { cmd: "baka_cleardrops keepgear=1 radius=32.5" }],
  // a grouping comma is refused, never read as a decimal point ("1,000" is not a radius of 1)
  [{ keepNear: true, radius: "1,000" }, { problemId: "pal.clear_drops.problem.radius" }],
  [{ keepNear: true, radius: "1,5,0" }, { problemId: "pal.clear_drops.problem.radius" }],
  [{ keepNear: true, radius: ",5" }, { problemId: "pal.clear_drops.problem.radius" }],
  [{ keepNear: true, radius: "32," }, { problemId: "pal.clear_drops.problem.radius" }],
  [{ keepNear: true, radius: "" }, { problemId: "pal.clear_drops.problem.radius" }],
  [{ keepNear: true, radius: "0" }, { problemId: "pal.clear_drops.problem.radius" }],
  [{ keepNear: true, radius: "-5" }, { problemId: "pal.clear_drops.problem.radius" }],
  [{ keepNear: true, radius: "1001" }, { problemId: "pal.clear_drops.problem.radius" }],
  [{ keepNear: true, radius: "wide" }, { problemId: "pal.clear_drops.problem.radius" }],
];

BUILDS.forEach(([state, want]) => {
  test("builds " + JSON.stringify(state), () => {
    const got = C.clearDropsCommand(state);
    if (want.cmd) assert.strictEqual(got.cmd, want.cmd, "got " + JSON.stringify(got));
    else assert.strictEqual(got.problemId, want.problemId, "got " + JSON.stringify(got));
  });
});

test("the blank dialog keeps gear and 32 metres, and builds the plugin's own defaults", () => {
  const blank = C.clearDropsBlank();
  assert.strictEqual(blank.keepGear, true);
  assert.strictEqual(blank.keepNear, true);
  assert.strictEqual(blank.radius, "32");
  assert.strictEqual(C.clearDropsCommand(blank).cmd, "baka_cleardrops keepgear=1 radius=32");
});

test("every line the builder writes is one the plugin's parser takes", () => {
  /* The plugin takes keepgear=0|1 and radius=<metres> from 0 to 1000, key=value, each once.
     Asked of every runnable row above, so a builder that grows a third argument or spells
     one differently fails here rather than as an "Error:" toast on a live server. */
  for (const [state] of BUILDS) {
    const got = C.clearDropsCommand(state);
    if (!got.cmd) continue;
    const words = got.cmd.split(" ");
    assert.strictEqual(words[0], "baka_cleardrops");
    assert.strictEqual(words.length, 3, got.cmd);
    assert.ok(/^keepgear=[01]$/.test(words[1]), got.cmd);
    const radius = /^radius=(.+)$/.exec(words[2]);
    assert.ok(radius, got.cmd);
    const metres = Number(radius[1]);
    assert.ok(isFinite(metres) && metres >= 0 && metres <= 1000, got.cmd);
  }
});

/* -------------------------------------------- the wiring, over the real file */

test("the dialog sends through sendConsole and reads the reply rather than deciding before it", () => {
  assert.ok(SOURCE.includes("sendConsole(built.cmd,reply=>clearDropsToast(clearDropsReply(reply)));"),
    "the ground sweep no longer hands its reply to the reader");
});

test("every way in opens the same dialog", () => {
  for (const wire of [
    '$("#clearDropsBtn")?.addEventListener("click",()=>clearDropsModal());',
    'if(r.dataset.asks==="cleardrops"){modalClose();clearDropsModal();return;}',
    '}else if(cmd==="clear_drops"){',
    "clear_drops:1",
  ]) assert.ok(SOURCE.includes(wire), "app.js no longer carries " + wire);
});

console.log("");
if (failures.length) {
  console.log("CLEARDROPS FAIL " + failures.length + " of " + (passed + failures.length));
  failures.forEach(line => console.log("  " + line));
  process.exit(1);
}
console.log("CLEARDROPS PASS " + passed + " cases");
