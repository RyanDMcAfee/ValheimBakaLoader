/* What baka_cleanse said, as a table, plus the rules that keep the command honest.
 *
 * WHY THIS EXISTS. It is the same defect the kick and the kill sweep were both fixed for,
 * one command later. baka_cleanse has one answer that means it cleared marks and counts
 * them, and a row of others that do not: it found none to clear, somebody is still
 * connected and it names them, the world is still loading, it could not read the world's
 * object index, a sweep is already walking, the status verb has nothing to report, or the
 * server is running a plugin old enough not to know the verb at all. A toast that said
 * "cleared" for any of those would send a host away believing a world had been cleaned
 * that had not been touched, and a toast that said "something this version does not
 * recognise" about the plugin BakaLoader itself ships is the same failure one step milder.
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

/**
 * The source between two openings, BOTH of which have to be there.
 *
 * String.slice takes a negative end as an offset from the end of the string, so an end
 * index of -1 from a lookup that found nothing reads as "all but the last character":
 * the rule then runs over the whole file and passes on code that has nothing to do with
 * it. A missing marker has to fail the rule rather than widen it.
 */
function between(openMarker, closeMarker) {
  const from = SOURCE.indexOf(openMarker);
  const to = SOURCE.indexOf(closeMarker);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(openMarker));
  assert.ok(to > from,
    "app.js no longer holds " + JSON.stringify(closeMarker) + " after "
    + JSON.stringify(openMarker));
  return SOURCE.slice(from, to);
}

/**
 * Whether a compiled assembly carries a string literal.
 *
 * A .NET string literal lives in the assembly's user-string heap as plain UTF-16, but the
 * heap entries are not two-byte aligned: each one sits behind a length prefix of one, two
 * or four bytes, so about half of them start on an odd offset and a straight UTF-16 read
 * of the file walks straight past them. Both alignments are read.
 */
function dllHolds(buffer, literal) {
  return buffer.toString("utf16le").indexOf(literal) >= 0
      || buffer.slice(1).toString("utf16le").indexOf(literal) >= 0;
}

/**
 * Whether app.js reads a reply that opens with this phrase.
 *
 * The page matches openings with regular expressions, and some of them write a space as
 * \s* or \s+ so a plugin that pads its line differently is still read. So a space in the
 * plugin's own phrase is matched here against either a real space or one of those.
 */
function pageMentions(literal) {
  const loose = literal
    .replace(/[.*+?^${}()|[\]\\]/g, "\\$&")
    .replace(/ /g, "(?:\\\\s[*+]|\\s)+");
  return new RegExp(loose).test(SOURCE);
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

  // --- it started and is still walking (plugin 1.8.1 slices the sweep) ---
  ["Cleanse started: 240000 objects to check. The counts follow in the server log when it"
    + " finishes, and baka_cleanse_status says how far it has got.", { kind: "running" }],
  ["Cleanse running: 80000 of 240000 objects checked.", { kind: "running" }],

  // --- a second press while the first sweep is still walking ---
  // The window polls for up to ten minutes now, so a host who saw nothing happen has
  // plenty of time to press again, and this is what their own plugin answers.
  ["Cleanse is already running: 900 objects still to check. Wait for its result line before"
    + " starting another.", { kind: "already", count: 900 }],
  ["Cleanse is already running: 1 object still to check. Wait for its result line before"
    + " starting another.", { kind: "already", count: 1 }],

  // --- the status verb with nothing to report, which the poll meets after a restart ---
  ["Cleanse status: nothing is running. Run baka_cleanse on an empty server to clear the"
    + " cheat marks a 1.0.9 to 1.1.2 spawn left behind.", { kind: "idle" }],

  // --- somebody walked onto the server while it was walking ---
  ["Cleanse stopped: 1 player connected while it was running (Bjorn). Up to that point:"
    + " 12 world objects cleared, 3 containers rewritten, 40 items cleared."
    + " Run it again when the server is empty.",
    { kind: "stopped", count: 1, who: "Bjorn", zdos: 12, containers: 3, items: 40 }],
  ["Cleanse stopped: 2 players connected while it was running ((unnamed), Bjorn). Up to that"
    + " point: 0 world objects cleared, 0 containers rewritten, 0 items cleared."
    + " Run it again when the server is empty.",
    { kind: "stopped", count: 2, who: "(unnamed), Bjorn", zdos: 0, containers: 0, items: 0 }],

  // --- refusals ---
  ["Error: server not ready (world still loading)", { kind: "refused" }],
  ["Error: this build of Valheim keeps its world objects somewhere the cleanse cannot read,"
    + " so nothing was touched. The plugin needs rebuilding against it.", { kind: "refused" }],
  ["Error: the cleanse could not finish: Object reference not set to an instance of an object",
    { kind: "refused" }],
  // The same throw with the counts it had reached on it, which is the shape from 1.8.1.
  ["Error: the cleanse could not finish: Object reference not set to an instance of an object."
    + " Up to that point: 4 world objects cleared, 0 containers rewritten, 0 items cleared.",
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

test("a second press and an idle status are never read as a cleanse", () => {
  const already = C.cleanseReply(
    "Cleanse is already running: 900 objects still to check. Wait for its result line"
    + " before starting another.");
  assert.strictEqual(already.kind, "already",
    "the window tells a host their own bundled plugin answered with something this"
    + " version does not recognise");

  const idle = C.cleanseReply(
    "Cleanse status: nothing is running. Run baka_cleanse on an empty server to clear the"
    + " cheat marks a 1.0.9 to 1.1.2 spawn left behind.");
  assert.strictEqual(idle.kind, "idle", "an idle status reads as an unknown shape");

  // Both are worded, rather than falling through to the sentence about a shape this
  // version has not met.
  ["vikings.cleanse.already.toast", "vikings.cleanse.idle.toast"].forEach(id => {
    assert.ok(KEYS[id], "the catalog has no " + id);
  });
  const toast = SOURCE.slice(SOURCE.indexOf("function cleanseToast(read){"));
  assert.ok(toast.indexOf('case "already":') > 0, "the toast has no case for a second press");
  assert.ok(toast.indexOf('case "idle":') > 0, "the toast has no case for an idle status");
});

/* The press that makes the second press possible: the sweep is sliced and polled now, so
   the window can sit on one for ten minutes with nothing on screen moving. */
test("the button cannot be pressed a second time while a cleanse is running", () => {
  const press = between("async function doCleanse(){", "async function runCleanse(){");
  assert.ok(/if\(S\.cleansing\)\{/.test(press),
    "nothing holds a second press, so a host who saw nothing happen starts a second one");
  assert.ok(/S\.cleansing=true;/.test(press) && /S\.cleansing=false;/.test(press),
    "the in flight mark is never set, or never cleared");
  assert.ok(/btn\.disabled=true/.test(press) && /btn\.disabled=false/.test(press),
    "the button never says it is working, which is the whole reason for the second press");
  assert.ok(/finally\{/.test(press),
    "a press that threw would leave the button disabled for the session");

  // And the second press ANSWERS. It used to return in silence, and on a page where
  // nothing is moving a button that answers nothing is a button that did not work.
  assert.ok(/toast\(cleanseToast\(\{kind:"running"\}\)\)/.test(press),
    "the guarded second press says nothing at all, which is what it did before");
  assert.ok(/logLine\("warn"/.test(press),
    "the guarded second press leaves no line in the Saga log either");
  assert.ok(KEYS["vikings.cleanse.running.toast"],
    "the catalog has no vikings.cleanse.running.toast");

  // The other way to the same command is held on the same mark.
  const gate = fn("function updatePalGating(");
  assert.ok(/cmd==="clear_cheat_marks"&&S\.cleansing/.test(gate),
    "the palette row is not held while a cleanse is walking, so the second press can"
    + " come from there instead");
  assert.ok(KEYS["pal.gate.cleanse_running"], "the catalog has no pal.gate.cleanse_running");
});

/**
 * The two openings this file slices between are both real. A rule that slices to an index
 * of -1 reads to the end of the file and matches code that has nothing to do with it, so
 * the slicing itself is worth one rule of its own.
 */
test("a rule that slices between two markers fails when a marker is gone", () => {
  assert.ok(between("async function doCleanse(){", "async function runCleanse(){").length > 0);

  assert.throws(() => between("async function doCleanse(){", "function thisIsNotInAppJs(){"),
    /no longer holds/,
    "a marker that is not there widens the slice instead of failing the rule");
  assert.throws(() => between("function alsoNotInAppJs(){", "async function runCleanse(){"),
    /no longer holds/);
});

test("only the answers that really cleared something read as a cleanse", () => {
  const done = CASES.filter(([r]) => C.cleanseReply(r).kind === "done").length;
  assert.strictEqual(done, 3, "the done rows moved: " + done);

  // And a sweep that stopped part way is never one of them, however many marks it took off
  // before it stopped: the world still has marks on it and the host has to run it again.
  CASES.filter(([r]) => typeof r === "string" && /^Cleanse stopped:/.test(r))
    .forEach(([r]) => assert.notStrictEqual(C.cleanseReply(r).kind, "done",
      "a stopped sweep reads as a finished one"));
});

/* ---------------------------------------------------- the rules, over the real files */

test("every kind the reader can answer has a sentence, and the catalog has it", () => {
  const body = fn("function cleanseToast(");
  const kinds = ["done", "clean", "connected", "stopped", "running", "already", "idle",
                 "gone", "unsupported", "refused", "silent"];
  kinds.forEach(kind => {
    assert.ok(body.indexOf('case "' + kind + '":') >= 0, "cleanseToast says nothing for " + kind);
  });
  assert.ok(body.indexOf("default:") >= 0, "cleanseToast has no answer for a shape it has not met");

  const asked = (body.match(/T\("([a-z][a-z0-9_.]*)"/g) || []).map(s => s.slice(3, -1));
  assert.ok(asked.length >= kinds.length + 1, "cleanseToast asks for fewer sentences than it has kinds");
  asked.forEach(id => assert.ok(KEYS[id], "the catalog has no " + id));
});

test("the counts go through the reader rather than straight off the reply", () => {
  // The press is two functions since 1.2.4: the hold that stops a second press, and the
  // press itself. What is asked about here is the press itself.
  const body = fn("async function runCleanse(){");
  assert.ok(/toast\(cleanseToast\(cleanseReply\(said\)\)\)/.test(body),
    "the press words the server's answer without reading which of the answers it is");
  assert.ok(!/T\("vikings\.cleanse\.done\.toast"/.test(body),
    "the press raises the cleared toast itself, which is how a refusal reads as a success");
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
  assert.ok(/await send\("baka_cleanse"\)/.test(bridge),
    "the wait does not start the cleanse");
  assert.ok(/await send\("baka_cleanse_status"\)/.test(bridge),
    "the wait never asks how far the sweep has got, so a sliced sweep would never be read");
  assert.ok(/SendRconCommandAsync\(command/.test(bridge),
    "players.cleanse does not hand the wait a real RCON transport");
  assert.ok(/rpc\("players\.cleanse"\)/.test(SOURCE), "nothing on the page calls players.cleanse");
});

/* --------------------------------------------- the sliced sweep, on both sides of the seam */

/**
 * A server that stopped answering is not a sweep that is still running, and the toast for
 * it is not the ceiling's toast. The wait used to read a lost status line as "ask again",
 * sit out the whole ten minutes and then tell the host their cleanse was still walking a
 * world on a server that had gone.
 */
test("a server that stopped answering is worded as that and not as the ceiling", () => {
  const body = fn("async function runCleanse(){");

  assert.ok(/r\.notAnswering/.test(body),
    "the press never reads the answer that says the server stopped answering");
  assert.ok(/toast\(cleanseToast\(\{kind:"gone"\}\)\)/.test(body),
    "the server going away is not worded at all, or not through the reader");
  assert.ok(KEYS["vikings.cleanse.unreachable.toast"],
    "the catalog has no vikings.cleanse.unreachable.toast");

  // The two are different sentences. One says the sweep is still going; the other says it
  // is not, and they must never be the same words.
  assert.notStrictEqual(
    KEYS["vikings.cleanse.unreachable.toast"].lore,
    KEYS["vikings.cleanse.still_running.toast"].lore);

  // And the far side really answers with it.
  const bridge = fs.readFileSync(BRIDGE, "utf8");
  assert.ok(/notAnswering = outcome\.NotAnswering/.test(bridge),
    "the bridge does not carry the not-answering answer back to the page");
  assert.ok(/NotAnswering = true/.test(bridge),
    "nothing in the wait ever sets it, so the page reads a field that is never true");
  assert.ok(/while \(clock\.Elapsed < ceiling\)/.test(bridge),
    "the ceiling is counted rather than clocked, so ten minutes of polls is ten minutes"
    + " of waiting PLUS every round trip it took");
});

test("a sweep that outlasts the window's wait says so rather than wording a result", () => {
  const body = fn("async function runCleanse(){");

  // The ceiling case is read BEFORE the reply is worded. Wording it as a result would be
  // the one lie this toast must not tell: nothing has gone wrong, the sweep is still going.
  const ceiling = body.indexOf("r.stillRunning");
  const worded = body.indexOf("toast(cleanseToast(cleanseReply(said)))");
  assert.ok(ceiling > 0, "the press never reads the still-running answer");
  assert.ok(worded > ceiling,
    "the reply is worded before the still-running answer is read, so a sweep that is"
    + " still walking would be reported as a result");
  assert.ok(KEYS["vikings.cleanse.still_running.toast"],
    "the catalog has no vikings.cleanse.still_running.toast");
});

/**
 * The page reads a reply by the words it opens with, and those words are the PLUGIN's.
 * Nothing here spells one out: every literal below is read from the plugin's own source
 * and then held against the page, against the bridge, and against the DLLs that actually
 * ship. A prefix reworded on one side of that and not the other is a reply the host can
 * read and the window cannot, which is the whole defect this file exists for.
 */
test("the plugin, the page and the shipped DLLs agree on the words a reply opens with", () => {
  const planPath = path.join(ROOT, "ValheimBakaLoader", "Resources", "KillAll", "BakaCleansePlan.cs");
  const plan = fs.readFileSync(planPath, "utf8");
  const bridge = fs.readFileSync(BRIDGE, "utf8");

  // The named ones, read out of the plan rather than copied into this file.
  const openings = {};
  const named = /const string (\w+Prefix) = "([^"]+)"/g;
  let found;
  while ((found = named.exec(plan))) openings[found[1]] = found[2];

  ["CompletePrefix", "StartedPrefix", "RunningPrefix", "StoppedPrefix"].forEach(name => {
    assert.ok(openings[name], "BakaCleansePlan.cs no longer names " + name);
  });

  // And the two the plan spells inside its own builders, which the page reads just as
  // hard: a second press and an idle status are both matched on these words.
  const already = /"(Cleanse is already running: )"/.exec(plan);
  assert.ok(already, "BakaCleansePlan.cs no longer opens its refusal with a known phrase");
  openings.AlreadyRunning = already[1].trim();

  const idle = /"(Cleanse status: nothing is running)/.exec(plan);
  assert.ok(idle, "BakaCleansePlan.cs no longer opens its idle status with a known phrase");
  openings.StatusIdle = idle[1];

  Object.keys(openings).forEach(name => {
    assert.ok(pageMentions(openings[name]),
      "app.js never mentions " + JSON.stringify(openings[name]) + ", so a reply opening"
      + " with it reads as a shape this version has not met");
  });

  // And the matcher is not one that says yes to everything: a phrase the plugin does not
  // write is a phrase the page has no reason to hold.
  assert.ok(!pageMentions("Cleanse abandoned: nothing here says this"),
    "the page matcher answers yes to a phrase nothing writes");

  // The window's wait reads them through the plan rather than spelling them again.
  assert.ok(/CleansePlan\.StartedPrefix/.test(bridge) && /CleansePlan\.RunningPrefix/.test(bridge),
    "the bridge spells the opening words itself instead of reading the plugin's own names");

  // And the DLLs that actually ship carry the same words. The plan is source; the two
  // assemblies beside it are what a host's server loads, and a rebuild that did not
  // happen is exactly as bad as a prefix that was reworded.
  [["BakaLoaderCommander", path.join(ROOT, "ValheimBakaLoader", "Resources", "Commander", "BakaLoaderCommander.dll")],
   ["BakaKillAll", path.join(ROOT, "ValheimBakaLoader", "Resources", "KillAll", "BakaKillAll.dll")]]
    .forEach(([plugin, dll]) => {
      assert.ok(fs.existsSync(dll), "the shipped " + plugin + " is missing: " + dll);
      const bytes = fs.readFileSync(dll);
      Object.keys(openings).forEach(name => {
        assert.ok(dllHolds(bytes, openings[name]),
          plugin + ".dll does not carry " + JSON.stringify(openings[name])
          + ": either the prefix moved without a rebuild, or the DLL beside the source is"
          + " older than the source it is built from");
      });
    });
});

test("the status verb exists on both plugins and is offered by the fallback line", () => {
  const killall = fs.readFileSync(
    path.join(ROOT, "ValheimBakaLoader", "Resources", "KillAll", "BakaKillAll.cs"), "utf8");
  const commander = fs.readFileSync(
    path.join(ROOT, "ValheimBakaLoader", "Resources", "Commander", "BakaLoaderCommander.cs"), "utf8");

  assert.ok(killall.indexOf('"baka_cleanse_status"') > 0,
    "the console plugin does not register baka_cleanse_status");
  assert.ok(commander.indexOf('case "baka_cleanse_status":') > 0,
    "Commander does not answer baka_cleanse_status, so the window's wait asks for nothing");
  assert.ok(/baka_cleanse, baka_cleanse_status\)/.test(commander),
    "the unknown-command line does not offer baka_cleanse_status");

  // A sliced sweep only finishes because something drives it every frame.
  assert.ok(/CleanseSweep\.Pump\(\)/.test(killall), "the console plugin never pumps the cleanse");
  assert.ok(/CleanseSweep\.Pump\(\)/.test(commander), "Commander never pumps the cleanse");
});

console.log("");
if (failures.length) {
  console.log("cleanse reply selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("cleanse reply selftest: " + passed + " passed");
