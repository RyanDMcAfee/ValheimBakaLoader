/* What baka_cleanse said, as a table, plus the rules that keep the command honest.
 *
 * WHY THIS EXISTS. It is the same defect the kick and the kill sweep were both fixed for,
 * one command later. baka_cleanse has six things it can say: it cleared marks and counts
 * them, it found none to clear, somebody is still connected and it names them, the world is
 * still loading, it could not read the world's object index, or the server is running a
 * plugin old enough not to know the verb at all. Five of those are not a cleanse, and a
 * toast that said "cleared" for any of them would send a host away believing a world had
 * been cleaned that had not been touched.
 *
 *   THE TABLE. Every reply the plugin can produce, word for word out of
 *   Resources/KillAll/BakaCleansePlan.cs, read by the REAL reader: the block between the
 *   CLEANSE markers in app.js, evaluated here. A copy of that function in this file would
 *   pass for ever while the page read something else.
 *
 *   THE RULES. The reply is never handed to a toast that words success on its own; the
 *   confirm says all four things a host has to know before pressing it; the command is
 *   reachable from the palette and from the Players hall and both go through that one
 *   dialog; and the bridge really registers the method the page calls.
 *
 * Prints one line per case and exits non zero on the first failure.
 *
 *     node scripts/ui/cleanse_reply_selftest.js [app.js] [index.html] [en.json]
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
const BRIDGE = process.argv[5] || path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

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

function block(beginMarker, endMarker) {
  const from = SOURCE.indexOf(beginMarker);
  const to = SOURCE.indexOf(endMarker);
  assert.ok(from >= 0 && to > from, "app.js no longer carries the " + beginMarker + " markers");
  return SOURCE.slice(from, to);
}

/** One whole top level function out of app.js, named by its opening line. */
function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from > 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to);
}

/** The real reader, out of app.js. */
function reader() {
  const context = { cleanseReply: null, cleanseCounts: null };
  vm.createContext(context);
  vm.runInContext(block("/* CLEANSE-BEGIN", "/* CLEANSE-END */"), context, { filename: "app.js#cleanse" });
  assert.strictEqual(typeof context.cleanseReply, "function", "cleanseReply did not come out of the block");
  return context;
}

const C = reader();

/* ------------------------------------------------------------------ the replies
   Every string below is the plugin's own, out of CleansePlan in
   Resources/KillAll/BakaCleansePlan.cs, and out of the two catch arms around it. */

const TAIL = " Anything a player is carrying lives on their own machine and was not touched.";

const CASES = [
  // --- it ran and it cleared something, and the counts are the answer ---
  ["Cleanse complete: 412 world objects cleared, 37 containers rewritten, 1883 items cleared." + TAIL,
    { kind: "done", zdos: 412, containers: 37, items: 1883 }],
  // the singular of every one of the three, which is a different sentence
  ["Cleanse complete: 1 world object cleared, 1 container rewritten, 1 item cleared." + TAIL,
    { kind: "done", zdos: 1, containers: 1, items: 1 }],
  ["Cleanse complete: 0 world objects cleared, 0 containers rewritten, 5 items cleared." + TAIL,
    { kind: "done", zdos: 0, containers: 0, items: 5 }],

  // --- it ran and there was nothing to do, which is a result and not a failure ---
  ["Cleanse complete: nothing in this world carries a cheat mark." + TAIL, { kind: "clean" }],

  // --- somebody is still on the server ---
  ["Error: 1 player is still connected (Bjorn). Cheat marks can only be cleared on an empty server.",
    { kind: "connected", count: 1, who: "Bjorn" }],
  ["Error: 3 players are still connected (Bjorn, Freya, Ω Σ). Cheat marks can only be cleared on an empty server.",
    { kind: "connected", count: 3, who: "Bjorn, Freya, Ω Σ" }],
  ["Error: 2 players are still connected ((unnamed), Bjorn). Cheat marks can only be cleared on an empty server.",
    { kind: "connected", count: 2, who: "(unnamed), Bjorn" }],

  // --- refusals ---
  ["Error: server not ready (world still loading)", { kind: "refused" }],
  ["Error: this build of Valheim keeps its world objects somewhere the cleanse cannot read,"
    + " so nothing was touched. The plugin needs rebuilding against it.", { kind: "refused" }],
  ["Error: the cleanse could not finish: Object reference not set to an instance of an object",
    { kind: "refused" }],
  ["Cleanse failed: Object reference not set to an instance of an object", { kind: "refused" }],

  // --- a server whose plugin is older than the command ---
  ["Unknown command: 'baka_cleanse' (Commander natively supports: broadcast, playerlist, dmg, tp,"
    + " kick, baka_spawn, baka_killall)", { kind: "unsupported" }],
  ["Forwarded to console: baka_cleanse", { kind: "unsupported" }],

  // --- nothing came back at all ---
  ["", { kind: "silent" }],
  ["   \r\n  ", { kind: "silent" }],
  [null, { kind: "silent" }],
  [undefined, { kind: "silent" }],

  // --- shapes this version has not met ---
  ["Cleanse complete", { kind: "unknown" }],
  ["Cleanse complete: everything is fine now", { kind: "unknown" }],
  ["Cleansed 5 things", { kind: "unknown" }],
];

CASES.forEach(([reply, want]) => {
  const shown = reply == null ? String(reply) : JSON.stringify(String(reply).slice(0, 56));
  test("reads " + shown, () => {
    const got = C.cleanseReply(reply);
    assert.strictEqual(got.kind, want.kind, "kind was " + got.kind);
    ["zdos", "containers", "items", "count", "who"].forEach(field => {
      if (field in want) assert.strictEqual(got[field], want[field],
        field + " was " + JSON.stringify(got[field]));
    });
  });
});

test("only the answers that really cleared something read as a cleanse", () => {
  const done = CASES.filter(([r]) => C.cleanseReply(r).kind === "done").length;
  assert.strictEqual(done, 3, "the done rows moved: " + done);
});

/* ---------------------------------------------------- the rules, over the real files */

test("every kind the reader can answer has a sentence, and the catalog has it", () => {
  const body = fn("function cleanseToast(");
  const kinds = ["done", "clean", "connected", "unsupported", "refused", "silent"];
  kinds.forEach(kind => {
    assert.ok(body.indexOf('case "' + kind + '":') >= 0, "cleanseToast says nothing for " + kind);
  });
  assert.ok(body.indexOf("default:") >= 0, "cleanseToast has no answer for a shape it has not met");

  const asked = (body.match(/T\("([a-z][a-z0-9_.]*)"/g) || []).map(s => s.slice(3, -1));
  assert.ok(asked.length >= kinds.length + 1, "cleanseToast asks for fewer sentences than it has kinds");
  asked.forEach(id => assert.ok(KEYS[id], "the catalog has no " + id));
});

test("the counts go through the reader rather than straight off the reply", () => {
  const body = fn("async function doCleanse(");
  assert.ok(/toast\(cleanseToast\(cleanseReply\(said\)\)\)/.test(body),
    "doCleanse words the server's answer without reading which of the six answers it is");
  assert.ok(!/T\("vikings\.cleanse\.done\.toast"/.test(body),
    "doCleanse raises the cleared toast itself, which is how a refusal reads as a success");
});

test("the confirm says all four things before anything is swept", () => {
  const body = fn("function cleanseModal(");
  ["vikings.cleanse.confirm.what", "vikings.cleanse.confirm.backpack",
   "vikings.cleanse.confirm.empty", "vikings.cleanse.confirm.console"].forEach(id => {
    assert.ok(body.indexOf('"' + id + '"') >= 0, "the confirm never says " + id);
    assert.ok(KEYS[id], "the catalog has no " + id);
  });

  // And each one really is about the thing it is named for, because four sentences that
  // said the same thing would pass the rule above and tell a host nothing.
  assert.ok(/chest/i.test(KEYS["vikings.cleanse.confirm.backpack"].lore),
    "the backpack sentence does not tell the host what to do about it");
  assert.ok(/empty server/i.test(KEYS["vikings.cleanse.confirm.empty"].lore),
    "the empty-server sentence does not say the server has to be empty");
  assert.ok(/console/i.test(KEYS["vikings.cleanse.confirm.console"].lore),
    "the console-flag sentence does not mention the console");
});

test("both ways in go through that one question, and the bridge answers the call", () => {
  // The palette.
  assert.ok(MARKUP.indexOf('data-cmd="clear_cheat_marks"') > 0,
    "the command palette has no Clear cheat marks entry");
  assert.ok(/clear_cheat_marks:1/.test(SOURCE),
    "the palette entry is not held behind a running server, so it would be offered with"
    + " nothing to send it to");
  assert.ok(/cmd==="clear_cheat_marks"[\s\S]{0,260}cleanseModal\(\)/.test(SOURCE),
    "the palette entry does not open the confirm");

  // The Players hall.
  assert.ok(MARKUP.indexOf('id="cleanseBtn"') > 0, "the Players hall has no way to the cleanse");
  assert.ok(/\$\("#cleanseBtn"\)\?\.addEventListener\("click",\(\)=>cleanseModal\(\)\)/.test(SOURCE),
    "the Players hall button is wired to nothing, or not to the confirm");

  // The console picker, which sends a complete command on a click, is held behind the
  // same question rather than sending the sweep on one tap.
  assert.ok(/\{cmd:"baka_cleanse",args:"",descId:"pal\.console\.cmd\.cleanse",asks:"cleanse"\}/.test(SOURCE),
    "the console picker row is missing or does not ask first");
  assert.ok(/dataset\.asks==="cleanse"/.test(SOURCE),
    "the console picker does not route its cleanse row to the confirm");

  // And the far side of the call.
  const bridge = fs.readFileSync(BRIDGE, "utf8");
  assert.ok(bridge.indexOf('RegisterRpc("players.cleanse"') > 0,
    "the bridge registers no players.cleanse, so the button calls a method that is not there");
  assert.ok(/SendRconCommandAsync\("baka_cleanse"\)/.test(bridge),
    "players.cleanse does not send baka_cleanse");
  assert.ok(/rpc\("players\.cleanse"\)/.test(SOURCE), "nothing on the page calls players.cleanse");
});

console.log("");
if (failures.length) {
  console.log("cleanse reply selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("cleanse reply selftest: " + passed + " passed");
