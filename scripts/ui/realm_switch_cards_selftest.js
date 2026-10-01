/* What the Hearth cards say after the host switches realm.
 *
 * WHY THIS EXISTS. A walk of the real app, two realms up, found three things wrong with the
 * two cards a host looks at first.
 *
 *   1. WORLD SAVES kept ONE set of write times for the whole window. Switching realm emptied
 *      the bars and the average and left the time beside "last rune-check" exactly where it
 *      was, so the header read a measurement taken on the realm the host had just left while
 *      the body under it said nothing had been measured at all.
 *   2. The same card could therefore show a measurement and an empty state at once, which is
 *      two answers to one question.
 *   3. UPTIME counted from the SWITCH. The page started its own clock the first time it saw a
 *      Running state, and a realm switch arrives with no previous state, so a world that had
 *      been up all evening read as freshly started in the condition bar and on the SERVER
 *      card both.
 *
 * The rules below drive the real choosers and painters out of app.js, and hold the seam that
 * carries the session's own start time from the host to the page.
 *
 *     node scripts/ui/realm_switch_cards_selftest.js [app.js] [en.json] [BlendWindow.Bridge.cs] [ValheimServer.cs]
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const CATALOG = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");
const BRIDGE = process.argv[4]
  || path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
const SERVER = process.argv[5]
  || path.join(ROOT, "ValheimBakaLoader", "Game", "ValheimServer.cs");

const SOURCE = fs.readFileSync(APP, "utf8");
const KEYS = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys;
const HOST = fs.readFileSync(BRIDGE, "utf8");
const SESSION = fs.readFileSync(SERVER, "utf8");

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

/** One whole top level function out of app.js, named by its opening line. Without the brace that
 *  closes it, which every caller here adds back: the older rules were written that way. */
function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from > 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to);
}

/** One whole statement on one line, named by its opening. */
function fn1(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from > 0, "app.js no longer holds " + JSON.stringify(opening));
  return SOURCE.slice(from, SOURCE.indexOf("\n", from));
}

/* ------------------------------------------------- rule 1: uptime is the session's */
test("a session that came up 31 minutes ago still reads 31 minutes after a switch", () => {
  const body = fn("function uptimeAnchor(");
  const context = { Date, console };
  vm.createContext(context);
  vm.runInContext(body + "\n}\nthis.uptimeAnchor=uptimeAnchor;", context,
    { filename: "app.js#uptimeAnchor" });

  const now = Date.parse("2026-09-29T20:00:00.000Z");
  const cameUp = new Date(now - 31 * 60000).toISOString();

  // The switch: no previous status at all, because switchServer empties S.state first.
  const anchor = context.uptimeAnchor(null, undefined,
    { status: "Running", runningSinceUtc: cameUp }, now);
  assert.strictEqual(Math.round((now - anchor) / 60000), 31,
    "the anchor after a switch is not the session's own start");

  // Switching away and back again lands on the same answer, every time.
  for (let i = 0; i < 3; i++) {
    assert.strictEqual(
      context.uptimeAnchor(null, undefined, { status: "Running", runningSinceUtc: cameUp }, now),
      anchor, "the anchor moved on switch number " + (i + 1));
  }
});

test("a host too old to send the field still gets a clock, and a held one is kept", () => {
  const body = fn("function uptimeAnchor(");
  const context = { Date, console };
  vm.createContext(context);
  vm.runInContext(body + "\n}\nthis.uptimeAnchor=uptimeAnchor;", context,
    { filename: "app.js#uptimeAnchor" });

  const now = 1000000;
  // First sighting with nothing in the reply: the page's own clock, as it always was.
  assert.strictEqual(context.uptimeAnchor(null, "Stopped", { status: "Running" }, now), now);
  // And a state that says nothing new does not restart a clock that is already running.
  assert.strictEqual(context.uptimeAnchor(12345, "Running", { status: "Running" }, now), 12345,
    "an anchor already held was thrown away for a fresh one");
});

/* ------------------------------------------------- rule 2: the seam carries it */
test("the session knows when it came up and the bridge says so", () => {
  assert.ok(/public DateTime\? RunningSinceUtc \{ get; private set; \}/.test(SESSION),
    "ValheimServer no longer records when the session came up");
  assert.ok(/RunningSinceUtc = AdoptedRunningSinceUtc \?\? DateTime\.UtcNow;/.test(SESSION),
    "nothing anchors the session's uptime on the move to Running");
  assert.ok(/RunningSinceUtc = null;/.test(SESSION),
    "a stopped session still claims an uptime");

  // And the road that is not a launch. BakaLoader adopts a server it finds already running,
  // which is what this box looks like every time the window is closed and opened again over a
  // world that stays up. Stamping the moment of the adoption read as freshly started on a
  // world that had been up all evening: the same wrong answer by a different road.
  const adopt = SESSION.indexOf("public void AdoptProcess(");
  assert.ok(adopt > 0, "ValheimServer no longer adopts a running server");
  const body = SESSION.slice(adopt,
    SESSION.indexOf("Force-kills only BakaLoader", adopt));
  assert.ok(body.indexOf("AdoptedRunningSinceUtc = ReadAdoptedStartUtc(existingProcess);") > 0,
    "an adopted server counts its uptime from the adoption rather than from its process");
  assert.ok(body.indexOf("AdoptedRunningSinceUtc = ReadAdoptedStartUtc")
    < body.indexOf("Status = ServerStatus.Running;"),
    "the anchor is read after the transition that would have used it");
  assert.ok(/existingProcess\?\.StartTime\.ToUniversalTime\(\)/.test(SESSION),
    "nothing reads the adopted process's own start time");
  assert.ok(HOST.indexOf("runningSinceUtc = server.RunningSinceUtc?.ToString(\"o\"") > 0,
    "the state the page reads no longer carries the session's start time");
  assert.ok(/st\.runningSinceUtc/.test(SOURCE),
    "the page never reads the start time the host sends");
});

/* ------------------------------------------------- rule 3: the saves card agrees with itself */
test("the header and the body of the saves card give one answer", () => {
  const painter = fn("function renderLastSave(");
  const bars = fn("function renderSaveBars(");
  const made = {};
  const context = {
    S: { saveDur: [], lastSaveAt: null, lastSaveMs: null },
    $: id => (made[id] = made[id] || {
      id, textContent: "", innerHTML: "", style: {},
      /* The chart row carries a class now, for the row that has no height for the empty state's
         ornament. Its own rule drives the real measurement; here it only has to exist. */
      classList: { add() {}, remove() {}, toggle() {}, contains: () => false },
    }),
    /* Measured on screen, so it has its own rule rather than a reading here. */
    saveBarsEmptyFit() {},
    T: (id) => id,
    pad: n => String(n).padStart(2, "0"),
    /* The clock face is written by the lookup's own formatter, the way every other time in
       the window is. The line this replaced built HH:MM by hand, which put this one time on
       the card into a notation no other time uses. */
    fmtT: d => {
      const t = new Date(d);
      return String(t.getHours()).padStart(2, "0") + ":" + String(t.getMinutes()).padStart(2, "0");
    },
    emptyState: o => "<empty>" + o.title + "</empty>",
    esWire() {},
    esc: t => String(t),
    console,
  };
  vm.createContext(context);
  vm.runInContext(painter + "\n}\nthis.renderLastSave=renderLastSave;", context,
    { filename: "app.js#renderLastSave" });
  vm.runInContext(bars + "\n}\nthis.renderSaveBars=renderSaveBars;", context,
    { filename: "app.js#renderSaveBars" });

  // Nothing measured: both halves say so.
  context.renderLastSave();
  context.renderSaveBars();
  assert.strictEqual(made["#lastSave"].textContent, "hearth.saves.last.none",
    "the header claims a measurement on a realm that has never saved");
  assert.ok(made["#saveBars"].innerHTML.indexOf("<empty>") === 0,
    "the body is not the empty state on a realm that has never saved");

  // One write measured: both halves say that too.
  context.S.saveDur = [214];
  context.S.lastSaveAt = new Date(2026, 8, 29, 22, 5);
  context.S.lastSaveMs = 214;
  context.renderLastSave();
  context.renderSaveBars();
  assert.strictEqual(made["#lastSave"].textContent, "hearth.saves.last.value",
    "the header says nothing was measured while the bars show a measurement");
  assert.ok(made["#saveBars"].innerHTML.indexOf("<empty>") < 0,
    "the body is still the empty state after a write was timed");

  assert.ok(KEYS["hearth.saves.last.none"], "the catalog has no hearth.saves.last.none");
  assert.ok(KEYS["hearth.saves.last.value"], "the catalog has no hearth.saves.last.value");
});

/* ------------------------------- 1.2.7: the empty chart stays inside the chart's own row */

/* The walk found the ᛉ of the empty state sitting on the "until next save" line, between the two
   metric columns, whenever the card was height-constrained. .savebars is a flex ROW of bars with
   align-items:flex-end, so the empty state that replaces them arrived as a flex ITEM: content
   width, bottom aligned, and taller than the row it was in, so it overflowed UPWARD into the
   lines above. The stylesheet gives it the whole row and clips it; whether there is room for all
   three lines is a measurement, and that half is here. */
test("the empty chart gives up its rune when the row is too short to hold it", () => {
  const CSS = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.css"), "utf8");

  const held = new Set();
  const box = {
    clientHeight: 0,
    innerHTML: "<empty>",
    querySelector: selector => (box.innerHTML.indexOf("<empty>") >= 0 && selector === ".empty-state"
      ? { id: "empty" } : null),
    classList: {
      contains: c => held.has(c),
      add: c => held.add(c),
      remove: c => held.delete(c),
      toggle(c, on) {
        if (on === undefined) { if (held.has(c)) held.delete(c); else held.add(c); return held.has(c); }
        if (on) held.add(c); else held.delete(c);
        return !!on;
      },
    },
  };
  const context = { Number, String, console, $: () => box, box, held };
  vm.createContext(context);
  vm.runInContext(fn1("const SAVE_EMPTY_FULL_PX="), context, { filename: "app.js#fullPx" });
  vm.runInContext(fn("function saveBarsEmptyFit(") + "\n}\nthis.fit=saveBarsEmptyFit;", context,
    { filename: "app.js#emptyFit" });

  // A card with room for the whole stack keeps the ornament.
  box.clientHeight = 110;
  assert.strictEqual(context.fit(), false, "a tall card was treated as cramped");
  assert.ok(!held.has("es-norune"), "a tall card lost the rune it had room for");

  // The row the walk measured: 26 CSS pixels is the row's own minimum, and the stack needs more.
  box.clientHeight = 26;
  assert.strictEqual(context.fit(), true, "a 26 pixel row was treated as having room for three lines");
  assert.ok(held.has("es-norune"),
    "the rune is still drawn in a row that cannot hold it, so it overflows into the metric line above");

  // Back to a tall card: the class comes off again rather than sticking.
  box.clientHeight = 110;
  context.fit();
  assert.ok(!held.has("es-norune"), "the rune never comes back once the card has room again");

  // A chart with bars in it is not an empty state at all.
  box.innerHTML = "<i></i><i></i>";
  box.clientHeight = 26;
  assert.strictEqual(context.fit(), false);
  assert.ok(!held.has("es-norune"), "a chart of real bars was marked as a cramped empty state");

  // And the half that can be written down once is written down once.
  assert.ok(CSS.indexOf(".savebars:has(>.empty-state){align-items:stretch;overflow:hidden}") > 0,
    "app.css no longer gives the empty state the whole chart row and clips it to it, so it can"
    + " overflow upward into the metric columns again");
  assert.ok(/\.savebars\.es-norune>\.empty-state>\.es-mark\{display:none\}/.test(CSS),
    "app.css has no rule for the row that has no room for the ornament");
  assert.ok(/\.savebars>\.empty-state\{[^}]*flex:1 1 100%/.test(CSS),
    "the empty state is a content-width flex item again rather than the whole row");
});

test("the painter and the window's own size both ask", () => {
  const bars = fn("function renderSaveBars(");
  assert.ok(bars.indexOf("saveBarsEmptyFit();") > 0,
    "the painter draws the empty state and never measures the room it has");
  assert.ok(bars.indexOf('box.classList.remove("es-norune");') > 0,
    "a chart that has bars again keeps the class an empty one left behind");
  assert.ok(SOURCE.indexOf('new ResizeObserver(()=>{try{saveBarsEmptyFit();}catch(_){}}).observe($("#saveBars"))') > 0,
    "nothing re-measures when the card's own height changes, and its height is the bento grid's:"
    + " it moves with the window and with the text size rather than with anything this file does");
});

/* ------------------------------- 1.2.7: the clock half of "1d ago at 19:37" */

test("the ago line's clock goes through the same formatter every other clock uses", () => {
  const context = {
    Date, Math, Number, String, console,
    T: (id, params) => (id === "common.ago.at" ? params.ago + " at " + params.clock : id),
    pad: n => String(n).padStart(2, "0"),
    intl: () => null,
  };
  vm.createContext(context);
  vm.runInContext(fn("function fmtT(d){") + "\n}", context, { filename: "app.js#fmtT" });
  vm.runInContext(fn("function agoAt(d){") + "\n}\nthis.agoAt=agoAt;", context,
    { filename: "app.js#agoAt" });

  const when = new Date(2026, 9, 1, 12, 13, 0);
  const said = context.agoAt(when.getTime() - 0);
  assert.ok(/ at \d\d:\d\d$/.test(said),
    "the clock half is still a bare four-digit string: " + said + ". Everything else on screen"
    + " writes a colon, and a twelve hour locale never gets a twelve hour reading out of this one");

  // And it is fmtT that writes it, so a locale that puts the hour another way is honoured.
  context.intl = () => ({
    fmtTime: () => "7:45 PM",
    fmtRelative: () => "1d ago",
    locale: () => "en-US",
  });
  const american = context.agoAt(when.getTime() - 86400 * 1000);
  assert.ok(american.indexOf("7:45 PM") > 0,
    "the lookup's own time format is bypassed: " + american);

  const body = fn("function agoAt(d){");
  assert.ok(body.indexOf("clock:fmtT(t)") > 0, "agoAt builds its own clock again: " + body);
  assert.ok(body.indexOf("pad(t.getHours())") < 0, "the hand-built clock is still in agoAt");
});

/* ------------------------------------------------- rule 4: the times are kept per realm */
test("each realm keeps its own write times across a switch", () => {
  const stash = fn("function saveTimesStash(");
  const load = fn("function saveTimesLoad(");
  const context = { S: { saveDur: [], lastSaveAt: null, lastSaveMs: null }, console };
  vm.createContext(context);
  vm.runInContext("const SAVE_TIMES={};\n" + stash + "\n}\n" + load + "\n}\n"
    + "this.saveTimesStash=saveTimesStash;this.saveTimesLoad=saveTimesLoad;"
    + "this.SAVE_TIMES=SAVE_TIMES;", context, { filename: "app.js#saveTimes" });

  const when = new Date(2026, 8, 29, 22, 5);
  context.S.saveDur = [198, 214];
  context.S.lastSaveAt = when;
  context.S.lastSaveMs = 214;

  // Leaving Final Sunset for a realm that has never saved.
  context.saveTimesStash("Final Sunset");
  context.saveTimesLoad("Second Realm");
  assert.strictEqual(JSON.stringify(context.S.saveDur), "[]",
    "the new realm inherited the old realm's bars");
  assert.strictEqual(context.S.lastSaveAt, null,
    "the new realm inherited the old realm's last write");
  assert.strictEqual(context.S.lastSaveMs, null,
    "the new realm inherited the old realm's write time");

  // And back again: the numbers are the ones that realm measured, not a blank.
  context.saveTimesStash("Second Realm");
  context.saveTimesLoad("Final Sunset");
  assert.strictEqual(JSON.stringify(context.S.saveDur), "[198,214]",
    "coming back to a realm lost its own write times");
  assert.strictEqual(context.S.lastSaveAt, when);
  assert.strictEqual(context.S.lastSaveMs, 214);

  // The stash is a copy: a later write on this realm must not reach the held set.
  context.S.saveDur.push(999);
  context.saveTimesLoad("Final Sunset");
  assert.strictEqual(JSON.stringify(context.S.saveDur), "[198,214]",
    "the stash holds the live array rather than a copy of it");
});

/* ------------------------------------------------- rule 5: the switch really calls them */
test("switching realm stashes the old realm and loads the new one, in that order", () => {
  const body = fn("async function switchServer(");
  const stashAt = body.indexOf("saveTimesStash(S.profileName)");
  const nameAt = body.indexOf("S.profileName=prefs.ProfileName");
  const loadAt = body.indexOf("saveTimesLoad(prefs.ProfileName)");

  assert.ok(stashAt > 0, "a realm switch no longer puts the old realm's write times down");
  assert.ok(loadAt > 0, "a realm switch no longer picks the new realm's write times up");
  assert.ok(stashAt < nameAt,
    "the old realm's times are filed AFTER its name is overwritten, so they go under the "
    + "wrong realm");
  assert.ok(nameAt < loadAt, "the new realm's times are loaded before its name is known");

  for (const painter of ["renderSaveAvg();", "renderLastSave();", "renderSaveBars();"]) {
    assert.ok(body.indexOf(painter) > 0,
      "a realm switch no longer repaints with " + painter);
  }
});

console.log("");
if (failures.length) {
  console.log("realm switch cards selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("realm switch cards selftest: " + passed + " passed");
