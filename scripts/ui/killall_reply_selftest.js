/* What baka_killall said, as a table, plus the rule that made the table necessary.
 *
 * WHY THIS EXISTS. sendConsole raises its toast when RCON has DELIVERED a line, and the
 * kill sweep answers on that same line with seven different endings: the three counts,
 * a "started" line for a sweep too big to finish inside the answer, a refusal because
 * one is already walking, a stopped-early line, and five sentences that open with
 * "Error:". Every one of them used to raise "KillAll unleashed". A host who typed a
 * creature name the game has never heard of was told the sweep had run.
 *
 * So there are two halves here and both have to hold:
 *
 *   THE TABLE. Every reply the plugin can produce, word for word out of
 *   Resources/KillAll/BakaKillAllPlan.cs, read by the REAL reader: the block between the
 *   KILLALL markers in app.js, evaluated here. A copy of that function in this file
 *   would pass forever while the page read something else. The scope builder is driven
 *   the same way, because the command it writes is what actually reaches the server.
 *
 *   THE RULE. No sendConsole call may hand a finished toast in as its second argument.
 *   That is the exact shape of the defect: a sentence chosen before the answer arrived.
 *   The second argument is a function now, and it is handed the reply. This check is
 *   what stops the next caller going back to a string, and it FAILS on the code as it
 *   stood before this pass.
 *
 * Prints one line per case and exits non zero on the first failure.
 *
 * node scripts/ui/killall_reply_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");

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

/** The real reader and the real scope builder, out of app.js. */
function readers() {
  const from = SOURCE.indexOf("/* KILLALL-BEGIN");
  const to = SOURCE.indexOf("/* KILLALL-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the KILLALL markers");
  const context = {
    consoleRefused: null, killAllCounts: null, killAllReply: null,
    killAllScopeBlank: null, killAllCommand: null,
  };
  vm.createContext(context);
  vm.runInContext(SOURCE.slice(from, to), context, { filename: "app.js#killall" });
  assert.strictEqual(typeof context.killAllReply, "function", "killAllReply did not come out of the block");
  assert.strictEqual(typeof context.killAllCommand, "function", "killAllCommand did not come out of the block");
  return context;
}

const K = readers();

/* ------------------------------------------------------------------ the replies
   Every string below is the plugin's own, assembled the way KillAllPlan assembles it.
   Resources/KillAll/BakaKillAllPlan.cs: Reply(), Counts(), Started(), AlreadyRunning(),
   StoppedEarly(), NoteSentence(), BadRadius; Resources/KillAll/BakaKillAllSweep.cs for
   the five "Error:" lines; Resources/KillAll/BakaKillAll.cs for "KillAll failed:".      */

const CASES = [
  // --- a finished sweep, in all the shapes the counts clause takes ---
  ["KillAll complete: 37 hostiles slain, 4 out of reach, 162 spared (players, pets & allies)",
    { kind: "complete", slain: 37, unreachable: 4, spared: 162 }],
  ["KillAll complete: 1 hostile slain, 0 out of reach, 12 spared (players, pets & allies)",
    { kind: "complete", slain: 1, unreachable: 0, spared: 12 }],
  ["KillAll complete: 0 hostiles slain, 0 out of reach, 0 spared (players, pets & allies)",
    { kind: "complete", slain: 0, unreachable: 0, spared: 0 }],
  // the same line with each of the four notes on the end of it
  ["KillAll complete: 0 hostiles slain, 0 out of reach, 88 spared (players, pets & allies). " +
    "There is no creature called 'Eikthyrr' in this game.",
    { kind: "complete", slain: 0, unreachable: 0, spared: 88 }],
  ["KillAll complete: 0 hostiles slain, 0 out of reach, 88 spared (players, pets & allies). " +
    "Deer is not a hostile creature, so KillAll leaves it where it stands.",
    { kind: "complete", slain: 0, unreachable: 0, spared: 88 }],
  ["KillAll complete: 0 hostiles slain, 0 out of reach, 88 spared (players, pets & allies). " +
    "No Eikthyr is in the world right now.",
    { kind: "complete", slain: 0, unreachable: 0, spared: 88 }],
  ["KillAll complete: 0 hostiles slain, 0 out of reach, 88 spared (players, pets & allies). " +
    "Nothing hostile was standing inside that radius around Bjorn.",
    { kind: "complete", slain: 0, unreachable: 0, spared: 88 }],

  // --- too big to finish inside the answer ---
  ["KillAll started: 4120 candidates. The result line follows in the server log when the sweep finishes.",
    { kind: "started", candidates: 4120 }],
  ["KillAll started: 1 candidate. The result line follows in the server log when the sweep finishes.",
    { kind: "started", candidates: 1 }],

  // --- a second sweep while the first is still walking: nothing was struck ---
  ["KillAll is already running: 900 candidates still to go. Wait for its result line before starting another.",
    { kind: "busy" }],
  ["KillAll is already running: 1 candidate still to go. Wait for its result line before starting another.",
    { kind: "busy" }],

  // --- it threw part way, and the counts it reached are real work ---
  ["KillAll stopped early: NullReferenceException. Up to that point: 12 hostiles slain, 1 out of reach, 40 spared (players, pets & allies).",
    { kind: "stopped", slain: 12, unreachable: 1, spared: 40 }],

  // --- every refusal ---
  ["Error: the radius has to be a number of metres above zero, like 50.", { kind: "refused" }],
  ["Error: server not ready (world still loading)", { kind: "refused" }],
  ["Error: player 'Bjorn' not found", { kind: "refused" }],
  ["Error: the world's object index could not be read (bad cast), so nothing was touched.", { kind: "refused" }],
  ["Error: command timed out (server main thread busy)", { kind: "refused" }],
  ["KillAll failed: object reference not set to an instance of an object", { kind: "refused" }],
  ["Unknown command: 'baka_killall' (Commander natively supports: broadcast, playerlist, dmg, tp, kick, baka_spawn, baka_killall)",
    { kind: "refused" }],

  // --- nothing came back at all ---
  ["", { kind: "silent" }],
  ["   \r\n  ", { kind: "silent" }],
  [null, { kind: "silent" }],
  [undefined, { kind: "silent" }],

  // --- the usage sentence: a line the plugin could not parse, and nothing was struck ---
  ["Usage: baka_killall for every hostile, baka_killall <PrefabName> for one kind, " +
    "or baka_killall near <player> <radius> for everything hostile around somebody.", { kind: "refused" }],

  // --- a shape this version has not met ---
  ["KillAll complete: it went fine", { kind: "unknown" }],
  ["KillAll queued. The count lands here when it is done.", { kind: "unknown" }],
];

CASES.forEach(([reply, want]) => {
  const shown = reply == null ? String(reply) : JSON.stringify(String(reply).slice(0, 52));
  test("reads " + shown, () => {
    const got = K.killAllReply(reply);
    assert.strictEqual(got.kind, want.kind, "kind was " + got.kind);
    for (const field of ["slain", "unreachable", "spared", "candidates"]) {
      if (field in want) assert.strictEqual(got[field], want[field], field + " was " + got[field]);
    }
  });
});

test("not one of those replies is read as an unqualified success", () => {
  /* The whole point, said once: the only kind that means "it ran and here is what it
     did" is complete, and exactly seven of the table's rows are that. */
  const complete = CASES.filter(([r]) => K.killAllReply(r).kind === "complete").length;
  assert.strictEqual(complete, 7, "the finished-sweep rows moved: " + complete);
});

/* -------------------------------------------------------------- the scope builder */

const BUILDS = [
  [{ scope: "everywhere" }, { cmd: "baka_killall" }],
  [{}, { cmd: "baka_killall" }],
  [{ scope: "near", who: "Bjorn", radius: "100" }, { cmd: "baka_killall near Bjorn 100" }],
  [{ scope: "near", who: "Bjorn", radius: " 50 " }, { cmd: "baka_killall near Bjorn 50" }],
  [{ scope: "near", who: "Bjorn", radius: "12,5" }, { cmd: "baka_killall near Bjorn 12.5" }],
  // a player name may hold spaces: the plugin takes the radius off the END of the line
  [{ scope: "near", who: "Bjorn the Red", radius: "80" }, { cmd: "baka_killall near Bjorn the Red 80" }],
  [{ scope: "near", who: "", radius: "100" }, { problemId: "pal.kill.problem.no_player" }],
  [{ scope: "near", who: "   ", radius: "100" }, { problemId: "pal.kill.problem.no_player" }],
  [{ scope: "near", who: "Bjorn", radius: "" }, { problemId: "pal.kill.problem.radius" }],
  [{ scope: "near", who: "Bjorn", radius: "0" }, { problemId: "pal.kill.problem.radius" }],
  [{ scope: "near", who: "Bjorn", radius: "-5" }, { problemId: "pal.kill.problem.radius" }],
  [{ scope: "near", who: "Bjorn", radius: "wide" }, { problemId: "pal.kill.problem.radius" }],
  [{ scope: "creature", name: "Eikthyr" }, { cmd: "baka_killall Eikthyr" }],
  [{ scope: "creature", name: "  Greydwarf " }, { cmd: "baka_killall Greydwarf" }],
  [{ scope: "creature", name: "" }, { problemId: "pal.kill.problem.no_creature" }],
  [{ scope: "creature", name: "Grey dwarf" }, { problemId: "pal.kill.problem.creature_spaces" }],
];

BUILDS.forEach(([scope, want]) => {
  test("builds " + JSON.stringify(scope), () => {
    const got = K.killAllCommand(scope);
    if (want.cmd) assert.strictEqual(got.cmd, want.cmd);
    else assert.strictEqual(got.problemId, want.problemId, "problem was " + JSON.stringify(got));
  });
});

test("the blank scope is the everywhere one with a hundred metre default", () => {
  const blank = K.killAllScopeBlank();
  assert.strictEqual(blank.scope, "everywhere");
  assert.strictEqual(blank.radius, "100");
  assert.strictEqual(K.killAllCommand(blank).cmd, "baka_killall");
});

/* ---------------------------------------------------- the rule, over the real file */

/* sendConsole(cmd, read). The second argument, when there is one, is a FUNCTION that is
   handed the reply. A string there is a sentence chosen before the server answered,
   which is the defect this whole file exists for. */
const SEND = /(?<![A-Za-z0-9_$])sendConsole\s*\(/g;

function argumentsOf(source, open) {
  let depth = 0, index = open, quote = "", out = "";
  for (; index < source.length; index++) {
    const char = source[index];
    if (quote) {
      out += char;
      if (char === "\\") { out += source[++index] || ""; continue; }
      if (char === quote) quote = "";
      continue;
    }
    if (char === '"' || char === "'" || char === "`") { quote = char; out += char; continue; }
    if (char === "(" || char === "[" || char === "{") depth++;
    if (char === ")" || char === "]" || char === "}") { depth--; if (depth === 0) return out.slice(1); }
    out += char;
  }
  return null;
}

function splitTop(text) {
  const parts = [];
  let depth = 0, quote = "", current = "";
  for (let index = 0; index < text.length; index++) {
    const char = text[index];
    if (quote) {
      current += char;
      if (char === "\\") { current += text[++index] || ""; continue; }
      if (char === quote) quote = "";
      continue;
    }
    if (char === '"' || char === "'" || char === "`") { quote = char; current += char; continue; }
    if ("([{".includes(char)) depth++;
    if (")]}".includes(char)) depth--;
    if (char === "," && depth === 0) { parts.push(current); current = ""; continue; }
    current += char;
  }
  parts.push(current);
  return parts.map(p => p.trim()).filter(p => p !== "");
}

/** Every sendConsole call in a source, with its arguments, comments and strings intact. */
function calls(source) {
  const found = [];
  let match;
  SEND.lastIndex = 0;
  while ((match = SEND.exec(source))) {
    /* The declaration itself is not a call. */
    const before = source.slice(Math.max(0, match.index - 30), match.index);
    if (/function\s+$/.test(before) || /async\s+function\s+$/.test(before)) continue;
    const args = argumentsOf(source, match.index + match[0].length - 1);
    if (args == null) continue;
    found.push({ at: source.slice(0, match.index).split("\n").length, args: splitTop(args) });
  }
  return found;
}

test("every sendConsole call reads the reply rather than deciding before it", () => {
  const found = calls(SOURCE);
  assert.ok(found.length >= 4, "the call sites moved: " + found.length + " found");
  const bad = found.filter(c => c.args.length > 1 && !/=>|^function\b/.test(c.args[1]));
  assert.deepStrictEqual(bad, [],
    "a toast was chosen before the server answered, at app.js:" + bad.map(c => c.at).join(", "));
});

test("the rule fails on the shape that used to be here", () => {
  /* Falsification. The gate has to go red on the two calls this pass replaced, or it is
     only watching an empty room. */
  const before = [
    'sendConsole("save","ᛉ "+T("pal.save_world.done.toast"));',
    'sendConsole("baka_killall","ᚦ "+T("pal.kill_monsters.done.toast"));',
    'sendConsole(v);',
    'sendConsole(sel.dataset.raw||"");',
  ].join("\n");
  const bad = calls(before).filter(c => c.args.length > 1 && !/=>|^function\b/.test(c.args[1]));
  assert.strictEqual(bad.length, 2, "the rule did not catch the old shape: " + JSON.stringify(bad));
});

test("the rule lets the shape that is here now through", () => {
  const now = [
    'sendConsole(built.cmd,reply=>killAllToast(killAllReply(reply)));',
    'sendConsole("save",reply=>consoleRefused(reply)?a:b);',
    'sendConsole("save",function(reply){return reply;});',
    'sendConsole(v);',
  ].join("\n");
  const bad = calls(now).filter(c => c.args.length > 1 && !/=>|^function\b/.test(c.args[1]));
  assert.deepStrictEqual(bad, []);
});

console.log("");
if (failures.length) {
  console.log("KILLALL FAIL " + failures.length + " of " + (passed + failures.length));
  failures.forEach(line => console.log("  " + line));
  process.exit(1);
}
console.log("KILLALL PASS " + passed + " cases");
