/* What a kick said, as a table, plus the two rules that made the table necessary.
 *
 * WHY THIS EXISTS. A kick used to go through doPlayerAct, which raises "Kicked X" for
 * any answer that is not literally false. The server has four things to say back: it
 * kicked somebody and names them, nobody of that name or id is on the server, it
 * refused the line, or it says nothing at all, which is what the vanilla console does.
 * Three of those four read as a kick that had happened.
 *
 * It mattered most for the bug this was written for. A player named in Greek reached
 * the app through the wrong encoding, so the name BakaLoader sent back matched nobody,
 * the kick landed on no one, and the host was told it was done with the player still
 * standing there. The app sends the platform id first now, and this is the other half:
 * the toast says which of the four actually happened.
 *
 *   THE TABLE. Every reply the plugin can produce, word for word out of
 *   Resources/Commander/BakaLoaderCommander.cs, read by the REAL reader: the block
 *   between the KICK markers in app.js, evaluated here. A copy of that function in this
 *   file would pass forever while the page read something else. The same block holds
 *   the chooser that decides WHICH name the toast says, and it is driven as a second
 *   table: a 1.6.0 plugin answers "Kicked: " with the exact text it was handed, so the
 *   host id the app now sends first can come straight back where a person was expected.
 *
 *   THE RULES. No call site may hand a kick to doPlayerAct, which is the shape of the
 *   defect, and every players.kick call has to carry the hostId, or the kick goes out
 *   by a name the app may never have read correctly. The toast has to take its name
 *   through the chooser, the kick has to hand it the id it sent, and the ban's log line
 *   may claim a kick for the one answer that names a kicked player and no other.
 *
 * Prints one line per case and exits non zero on the first failure.
 *
 * node scripts/ui/kick_reply_selftest.js
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

function block(beginMarker, endMarker) {
  const from = SOURCE.indexOf(beginMarker);
  const to = SOURCE.indexOf(endMarker);
  assert.ok(from >= 0 && to > from, "app.js no longer carries the " + beginMarker + " markers");
  return SOURCE.slice(from, to);
}

/** One whole top level function out of app.js, named by its opening line. It ends at the
    first brace back at the left margin, which is where every function in that file ends. */
function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from > 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to);
}

/** The real reader, out of app.js, with the real refusal test it leans on. */
function reader() {
  const context = { consoleRefused: null, killAllReply: null, kickReply: null, kickedName: null };
  vm.createContext(context);
  // consoleRefused lives in the kill-all block and is shared by both readers.
  vm.runInContext(block("/* KILLALL-BEGIN", "/* KILLALL-END */"), context, { filename: "app.js#killall" });
  vm.runInContext(block("/* KICK-BEGIN", "/* KICK-END */"), context, { filename: "app.js#kick" });
  assert.strictEqual(typeof context.kickReply, "function", "kickReply did not come out of the block");
  assert.strictEqual(typeof context.kickedName, "function", "kickedName did not come out of the block");
  return context;
}

const K = reader();

/* ------------------------------------------------------------------ the replies
   Every string below is the plugin's own: CmdKick in Resources/Commander/
   BakaLoaderCommander.cs, ServerReady above it, and CmdFallback's unknown-command line. */

// The name the whole fix is for: two Greek letters, and the four Latin-1 characters the
// app used to make of them. Written as escapes so this file stays plain ASCII.
const GREEK = "Ω Σ";
const BROKEN = "Î© Î£";

const CASES = [
  // --- it kicked somebody, and the name it gives is the server's own spelling ---
  ["Kicked: Bjorn", { kind: "kicked", name: "Bjorn" }],
  ["Kicked: " + GREEK, { kind: "kicked", name: GREEK }],
  ["Kicked: Bjorn the Red", { kind: "kicked", name: "Bjorn the Red" }],
  ["Kicked: Steam_76561198000000000", { kind: "kicked", name: "Steam_76561198000000000" }],

  // --- nobody of that name or id is on the server: the answer this fix added ---
  ["Error: no player named 'Bjorn' is online", { kind: "missing", name: "Bjorn" }],
  ["Error: no player named '" + GREEK + "' is online", { kind: "missing", name: GREEK }],
  // the old bug, arriving as an answer: the app asked for a name it had mangled
  ["Error: no player named '" + BROKEN + "' is online", { kind: "missing", name: BROKEN }],
  ["Error: no player named '' is online", { kind: "missing", name: "" }],
  ["Error: no player named 'Bjorn the Red' is online", { kind: "missing", name: "Bjorn the Red" }],

  // --- refusals ---
  ["Error: server not ready (world still loading)", { kind: "refused" }],
  ["Error: command timed out (server main thread busy)", { kind: "refused" }],
  ["Usage: kick <player | hostId>", { kind: "refused" }],
  ["Unknown command: 'kick' (Commander natively supports: broadcast, playerlist, dmg, tp, kick, baka_spawn, baka_killall)",
    { kind: "refused" }],

  // --- nothing came back at all, which is what the vanilla console answers ---
  ["", { kind: "silent" }],
  ["   \r\n  ", { kind: "silent" }],
  [null, { kind: "silent" }],
  [undefined, { kind: "silent" }],

  // --- shapes this version has not met ---
  ["Kicked Bjorn", { kind: "unknown" }],
  ["Kicking user Bjorn", { kind: "unknown" }],
  ["Kicked:", { kind: "unknown" }],
];

CASES.forEach(([reply, want]) => {
  const shown = reply == null ? String(reply) : JSON.stringify(String(reply).slice(0, 52));
  test("reads " + shown, () => {
    const got = K.kickReply(reply);
    assert.strictEqual(got.kind, want.kind, "kind was " + got.kind);
    if ("name" in want) assert.strictEqual(got.name, want.name, "name was " + JSON.stringify(got.name));
  });
});

test("only the answers that name a kicked player read as a kick", () => {
  const kicked = CASES.filter(([r]) => K.kickReply(r).kind === "kicked").length;
  assert.strictEqual(kicked, 4, "the kicked rows moved: " + kicked);
});

/* ------------------------------------------------- which name the toast ends up saying
   A 1.6.0 Commander answers "Kicked: " with the exact text it was handed, and the app
   hands it the host id before the name, so that server's answer to a kick that worked is
   the id. Read as a name it puts "Kicked Steam_765611..." in front of a host who is
   looking at a row with a person's name on it. */

const ID = "Steam_76561198000000001";
const BARE = "76561198000000001";

const NAMES = [
  // the id we sent, handed straight back: not a name, so the row's own is said instead
  [ID, "Bjorn", ID, "Bjorn"],
  // the same id without the prefix on one side or the other, which is still that id
  [BARE, "Bjorn", ID, "Bjorn"],
  [ID, "Bjorn", BARE, "Bjorn"],
  [BARE, "Bjorn", BARE, "Bjorn"],
  // a real name is the server's own spelling of it and is never second-guessed
  ["Bjorn the Red", "Bjorn", ID, "Bjorn the Red"],
  [GREEK, "Bjorn", ID, GREEK],
  // a name that is plain ASCII and happens to be somebody's, with an id sent beside it
  ["Bjorn", "Bjorn", ID, "Bjorn"],
  // somebody ELSE's id is not the one we sent, so it is whatever the server meant by it
  ["Steam_76561198000000009", "Bjorn", ID, "Steam_76561198000000009"],
  // no id went out, so nothing about the answer can be read as an echo of one
  [ID, "Bjorn", null, ID],
  // nothing came back where a name should be: the row's own, as it always was
  ["", "Bjorn", ID, "Bjorn"],
  [null, "Bjorn", ID, "Bjorn"],
  // and the row has no name either, so the id is better than an empty sentence
  [ID, "", ID, ID],
];

NAMES.forEach(([said, who, hostId, want]) => {
  test("says " + JSON.stringify(want) + " for " + JSON.stringify(said) + " sent as " + JSON.stringify(hostId), () => {
    assert.strictEqual(K.kickedName(said, who, hostId), want);
  });
});

/* ---------------------------------------------------- the rules, over the real file */

test("no kick is handed to doPlayerAct, which words success before the answer arrives", () => {
  assert.ok(SOURCE.indexOf("doPlayerAct(\"players.kick\"") < 0,
    "a kick is going through doPlayerAct again, which raises a success toast for a refusal");
});

test("every players.kick call carries the hostId the server knows the player by", () => {
  const calls = SOURCE.match(/rpc\("players\.kick",\{[^}]*\}/g) || [];
  assert.ok(calls.length > 0, "no players.kick call found at all");
  calls.forEach((call) => {
    assert.ok(call.indexOf("hostId") >= 0, "a players.kick call sends no hostId: " + call);
  });
});

test("the toast has a sentence for every kind the reader can answer", () => {
  const body = fn("function kickToast(");
  ["kicked", "missing", "refused", "silent"].forEach((kind) => {
    assert.ok(body.indexOf("case \"" + kind + "\":") >= 0, "kickToast says nothing for " + kind);
  });
  assert.ok(body.indexOf("default:") >= 0, "kickToast has no answer for a shape it has not met");
  assert.ok(body.indexOf("vikings.kick.done.toast") >= 0, "the kicked sentence is gone");
  assert.ok(body.indexOf("vikings.kick.nobody.toast") >= 0, "the nobody-of-that-name sentence is gone");
});

test("the kicked sentence takes its name through the chooser, not straight off the reply", () => {
  const body = fn("function kickToast(");
  const line = /case\s*"kicked":([^\n]*)/.exec(body);
  assert.ok(line, "kickToast no longer answers the kicked reply");
  assert.ok(line[1].indexOf("kickedName(") >= 0,
    "the kicked toast words the server's answer as a name without asking whether it is only the id we sent");
});

test("the kick hands the toast the id it sent, or the chooser has nothing to compare", () => {
  const body = fn("async function doKick(");
  assert.ok(/kickToast\(kickReply\([^)]*\),[^)]*,[^)]*\)/.test(body),
    "doKick calls kickToast without the hostId it put on the call");
});

/* The other half of the same defect, one screen down: the ban kicks to enforce itself
   and then writes what happened into the log. Four of the five answers are not a kick
   anybody watched land, and the log used to claim one for two of those four. */
test("the ban's log line claims a kick for the one answer that names a kicked player", () => {
  const body = fn("async function doBan(");
  const at = body.indexOf("to enforce ban");
  assert.ok(at > 0, "the enforce-ban log sentence is gone");
  const chose = body.slice(0, at);
  assert.ok(/read\.kind\s*===\s*"kicked"/.test(chose),
    "the enforce-ban sentence is not gated on the one answer that names a kicked player");
  ["missing", "refused", "silent", "unknown"].forEach((kind) => {
    assert.ok(chose.indexOf("\"" + kind + "\"") < 0,
      "the enforce-ban sentence still branches on " + kind + ", which is not a kick");
  });
});

console.log("");
if (failures.length) {
  console.log("kick reply selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("kick reply selftest: " + passed + " passed");
