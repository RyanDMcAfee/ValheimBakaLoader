/* The armed one-shot cleanse, from the switch on the Players hall to the reply that puts it
 * down again.
 *
 * WHY THIS EXISTS. The Players hall's Clear cheat marks button needs a host sitting at the
 * window at a moment when nobody is playing, because the sweep rewrites containers and a
 * container somebody has open refuses to reload itself. On a server people actually use,
 * that moment is four in the morning. 1.2.6 added a switch beside the button that arms the
 * same sweep and leaves it: BakaLoader waits for a minute with nobody on the server, runs
 * it, says what it cleared, posts it, and puts the switch down.
 *
 * Two things about that can quietly be wrong and neither shows up in a build:
 *
 *   THE SWITCH BELONGS TO ONE REALM. It is a field on the profile, so a window that draws
 *   it once and leaves it standing shows realm A's answer over realm B, and a host arms a
 *   sweep on a realm they are not looking at. So the rule drives the real renderCleanseArmed
 *   over the real switch element and holds it across a realm's answer changing.
 *
 *   A REFUSAL IS NOT A RESULT. The one reason the sweep is ever turned away is that somebody
 *   is on the server, which is exactly the thing the host armed it for, so a refusal has to
 *   leave the switch UP and wait for the next empty moment. Everything else puts it down,
 *   including a sweep still walking at the end of the window's ten minute wait, which is work
 *   in progress; and including a reply this build has no reading for, because a switch that
 *   stayed up on an answer nobody understands would send the same command out every minute of
 *   the night for ever. The reading lives on the host side in ArmedCleanseLands and the page
 *   words what it decided, so both halves are read here: the C# by its rules, the page by
 *   driving its own seam.
 *
 * Prints one line per case and exits non zero on the first failure.
 *
 *     node scripts/ui/cleanse_armed_selftest.js [app.js] [index.html] [en.json] [Bridge.cs] [ValheimServer.cs]
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
const SERVER = process.argv[6] || path.join(ROOT, "ValheimBakaLoader", "Game", "ValheimServer.cs");

const SOURCE = fs.readFileSync(APP, "utf8");
const MARKUP = fs.readFileSync(PAGE, "utf8");
const KEYS = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys;
const HOST = fs.readFileSync(BRIDGE, "utf8");
const GAME = fs.readFileSync(SERVER, "utf8");

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

/* A case that has to WAIT for something. The presses below go through a real event
   dispatch and the handler they reach is async, so they are collected here and run in
   order at the foot of the file rather than pretended to be synchronous. */
const later = [];
function atest(name, body) { later.push({ name, body }); }

/** One whole top level function out of app.js, named by its opening line. */
function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from > 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to) + "\n}";
}

/** The source between two openings, BOTH of which have to be there. */
function between(openMarker, closeMarker) {
  const from = SOURCE.indexOf(openMarker);
  const to = SOURCE.indexOf(closeMarker);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(openMarker));
  assert.ok(to > from,
    "app.js no longer holds " + JSON.stringify(closeMarker) + " after " + JSON.stringify(openMarker));
  return SOURCE.slice(from, to);
}

/* ------------------------------------------------------------------ the real painter
   A stand in for the one switch element, with only the two things the painter touches. */
function harness() {
  const classes = new Set();
  const el = {
    classList: {
      contains: c => classes.has(c),
      toggle(c, on) { if (on) classes.add(c); else classes.delete(c); },
    },
  };
  const context = {
    S: { prefs: null },
    $: selector => (selector === "#tCleanseArmed" ? el : null),
    console,
  };
  context.setT = (id, on) => context.$("#" + id).classList.toggle("on", !!on);
  context.swOn = id => context.$("#" + id).classList.contains("on");
  vm.createContext(context);
  vm.runInContext(fn("function renderCleanseArmed(){"), context, { filename: "app.js#armed" });
  return context;
}

test("the switch draws what THIS realm's profile says, both ways", () => {
  const H = harness();

  H.S.prefs = { ProfileName: "A", CleanseWhenEmpty: true };
  H.renderCleanseArmed();
  assert.strictEqual(H.swOn("tCleanseArmed"), true, "an armed realm draws the switch off");

  H.S.prefs = { ProfileName: "B", CleanseWhenEmpty: false };
  H.renderCleanseArmed();
  assert.strictEqual(H.swOn("tCleanseArmed"), false,
    "switching to a realm that is NOT armed leaves the previous realm's switch standing, so a"
    + " host arms a sweep on a realm they are not looking at");

  // A profile that has never carried the field at all is a realm that is not armed, not a
  // realm whose switch keeps whatever was last drawn.
  H.S.prefs = { ProfileName: "C" };
  H.renderCleanseArmed();
  assert.strictEqual(H.swOn("tCleanseArmed"), false, "a profile with no answer draws the switch on");

  // And no profile at all, which is the first frame.
  H.S.prefs = null;
  H.renderCleanseArmed();
  assert.strictEqual(H.swOn("tCleanseArmed"), false, "no profile draws the switch on");
});

test("the realm switch really redraws it", () => {
  const swap = between("function switchServer(", "function saveTimesStash(");
  assert.ok(/renderCleanseArmed\(\)/.test(swap),
    "a realm switch does not redraw the armed switch, so realm A's answer stands over realm B");
});

test("the first frame draws it from the profile the window opened on", () => {
  // It survives closing the app, so a window that only drew it after a realm switch would
  // show a host an unarmed switch over an armed profile until they switched realms.
  const at = SOURCE.indexOf("const prefs=await rpc(\"profiles.get\",{name:profName});");
  assert.ok(at > 0, "app.js no longer opens on a profile read");
  const boot = SOURCE.slice(at, at + 700);
  assert.ok(/renderCleanseArmed\(\)/.test(boot),
    "the boot path never draws the armed switch, so it reads off on an armed realm");
});

test("walking into the Players hall redraws it", () => {
  const go = fn("function goPage(name){");
  assert.ok(/name==="vikings"[\s\S]{0,120}renderCleanseArmed/.test(go),
    "the hall does not redraw the switch when it is opened");
});

/* ------------------------------------------------------------------ the host's own reading */

test("only a refusal keeps the switch up, on the host side", () => {
  const at = HOST.indexOf("public static bool ArmedCleanseLands(CleanseOutcome outcome)");
  assert.ok(at > 0, "the bridge no longer holds ArmedCleanseLands");
  const rule = HOST.slice(at, HOST.indexOf("\n        }", at));

  // A command that never got through, and a server that stopped answering under the wait.
  assert.ok(/outcome == null \|\| !outcome\.Ok\) return false/.test(rule),
    "a cleanse that did not get through puts the switch down, so the sweep never happens");
  assert.ok(/outcome\.NotAnswering\) return false/.test(rule),
    "a server that stopped answering puts the switch down");

  // The plugin's own refusal, and a sweep that gave up because somebody walked on.
  assert.ok(/StartsWith\("Error:"/.test(rule),
    "the plugin's refusal is not read, so a sweep turned away for a connected player puts the"
    + " switch down and the marks are never cleared");
  assert.ok(/CleansePlan\.StoppedPrefix/.test(rule),
    "a sweep that stopped part way puts the switch down, and the world still has marks on it");

  // And everything else lands, which is the part that stops it looping for ever.
  assert.ok(/return true;/.test(rule), "nothing ever puts the switch down");
});

test("the host wires the watch off the PROFILE and not off the launched options", () => {
  // Options are the ones the running process was launched with and are only refreshed on a
  // relaunch, so a switch moved while the server is up would not have reached the watcher
  // until the next restart: on all evening and nothing happens.
  assert.ok(/CleanseWhenEmptyWanted = \(\) =>[\s\S]{0,200}ServerPrefsProvider\.LoadPreferences\(profile\)\?\.CleanseWhenEmpty/.test(HOST),
    "the watch does not read the profile on disk, so a switch moved while the server runs means nothing");
  // Comments out: this very field is named in the note that explains why it is not used.
  const code = GAME.split("\n").filter(l => !/^\s*\/\//.test(l)).join("\n");
  assert.ok(!/Options\.CleanseWhenEmpty/.test(code),
    "the watcher reads Options.CleanseWhenEmpty, which is the launch time copy");
});

test("the sixty second wait, the empty check and the one shot are all really there", () => {
  assert.ok(/EmptyCleanseDelay = TimeSpan\.FromSeconds\(60\)/.test(GAME),
    "the wait before an armed sweep is not sixty seconds");

  const at = GAME.indexOf("private void ScheduleEmptyCleanse()");
  assert.ok(at > 0, "ValheimServer no longer holds ScheduleEmptyCleanse");
  const wait = GAME.slice(at, GAME.indexOf("\n        private void CancelEmptyCleanse()", at));

  // EmptyCleanseWait is the delay above unless a test has handed a shorter one, which is the
  // only way the suite can drive this end to end without a minute of wall clock per case.
  assert.ok(/Task\.Delay\(EmptyCleanseWait, token\)/.test(wait), "the wait is not the delay");
  assert.ok(/EmptyCleanseDelayOverride \?\? EmptyCleanseDelay/.test(GAME),
    "the shorter wait is not the sixty seconds with an override over it, so the two can differ");
  assert.ok(/CountActivePlayers\(PlayerDataRepository\.Data\) > 0\) return/.test(wait),
    "the sweep goes out without looking again at who is on the server");
  assert.ok(/Status != ServerStatus\.Running\) return/.test(wait),
    "the sweep goes out at a server that is not up");
  assert.ok(/CleanseWhenEmptyWanted\(\)\) return/.test(wait),
    "a switch put down DURING the wait still fires the sweep, which is a setting doing"
    + " something after it was turned off");

  // A player arriving cancels it, and so does a stop.
  const changed = GAME.slice(GAME.indexOf("private void OnPlayerStatusChanged("));
  assert.ok(/CancelEmptyCleanse\(\);/.test(changed.slice(0, 1400)),
    "a player joining does not cancel the armed sweep's wait");
  assert.ok(/ArmEmptyCleanseWatch\(\);/.test(changed.slice(0, 1400)),
    "the last player leaving does not start the armed sweep's wait");
});

test("the server coming up arms the watch, because a fresh server is empty", () => {
  // The last-player-left edge is the only thing that used to start it, and a server that has
  // just started has no such edge coming: an armed sweep would have waited for somebody to
  // join and leave first.
  const running = GAME.slice(GAME.indexOf("LastActivePlayerCount = 0;"));
  assert.ok(/ArmEmptyCleanseWatch\(\);/.test(running.slice(0, 700)),
    "a server that comes up empty does not arm the watch");
});

test("the switch is armed, and put down, in exactly one place on the host side", () => {
  const writes = (HOST.match(/prefs\.CleanseWhenEmpty = on;/g) || []).length;
  assert.strictEqual(writes, 1,
    "the armed switch is written in " + writes + " places, so two of them can disagree");
  assert.ok(/PostEvent\("players\.cleanseArmed", new \{ profile, on \}\)/.test(HOST),
    "the page is never told where the switch ended up");
  assert.ok(/RegisterRpc\("players\.cleanseWhenEmpty"/.test(HOST),
    "the bridge does not register the method the switch calls");
});

test("the armed sweep runs the SAME sweep as the button, poll and ceiling and all", () => {
  const at = HOST.indexOf("session.Server.RunEmptyCleanse = async () =>");
  assert.ok(at > 0, "the bridge no longer wires RunEmptyCleanse");
  const run = HOST.slice(at, at + 2600);

  assert.ok(/RunCleanseAsync\(/.test(run), "the armed sweep does not go through RunCleanseAsync");
  assert.ok(/CleansePollEvery/.test(run) && /CleansePollCeiling/.test(run),
    "the armed sweep uses its own clocks rather than the button's");
  assert.ok(/AppLogger\.Information\("\{Line:l\}"/.test(run),
    "the counts never reach the log the Saga hall draws, and this fires while nobody is watching");
  assert.ok(/DiscordEventPosts/.test(run),
    "the counts go to Discord whether or not the herald is on");
  assert.ok(/SendCheatMarksCleared/.test(run), "nothing is posted to Discord");
  assert.ok(/SetCleanseWhenEmpty\(profile, false\)/.test(run), "the switch is never put down");
  assert.ok(/PostEvent\("players\.cleanseRan"/.test(run), "the page is never told the sweep ran");
});

/* ------------------------------------------------------------------ the page's own wording */

test("the page words the two endings the button never has", () => {
  ["vikings.cleanse.armed.label", "vikings.cleanse.armed.title",
    "vikings.cleanse.armed.on.toast", "vikings.cleanse.armed.off.toast",
    "vikings.cleanse.armed.fired.toast", "vikings.cleanse.armed.waiting.toast"]
    .forEach(id => assert.ok(KEYS[id], "the catalog has no " + id));

  const heard = between('Native.on("players.cleanseRan"', 'Native.on("server.legacyWorld"');
  assert.ok(/vikings\.cleanse\.armed\.fired\.toast/.test(heard),
    "a sweep that landed never says the switch went down with it");
  assert.ok(/vikings\.cleanse\.armed\.waiting\.toast/.test(heard),
    "a sweep that was turned away never says the switch is still on");
  assert.ok(/cleanseToast\(cleanseReply\(said\)\)/.test(heard),
    "the armed sweep's reply is worded by something other than the button's own reader, so one"
    + " sweep has two wordings");
  assert.ok(/isActiveProfile\(d\?\.profile\)/.test(heard),
    "another realm's sweep toasts over this one");
});

test("the switch is on the Players hall, beside the button, and the walker owns its words", () => {
  assert.ok(/id="tCleanseArmed"/.test(MARKUP), "the switch is not in the document");
  const hall = MARKUP.slice(MARKUP.indexOf('id="page-vikings"'), MARKUP.indexOf('id="page-mods"'));
  assert.ok(/id="tCleanseArmed"/.test(hall), "the switch is not on the Players hall");
  assert.ok(hall.indexOf('id="tCleanseArmed"') > hall.indexOf('id="cleanseBtn"'),
    "the switch does not stand beside the button it arms");
  assert.ok(/data-i18n="vikings\.cleanse\.armed\.label"/.test(hall),
    "the label carries no catalog id, so it stays English after a language switch");
  assert.ok(/data-i18n-title="vikings\.cleanse\.armed\.title"/.test(hall),
    "the tooltip carries no catalog id");
});

test("the click asks the host and follows the answer, not the finger", () => {
  const press = fn("async function onCleanseArmedClick(){");
  assert.ok(/rpc\("players\.cleanseWhenEmpty",\{on:want\}\)/.test(press),
    "the switch does not tell the host anything, so nothing is saved");
  assert.ok(/if\(r===FAIL\|\|!r\)\{renderCleanseArmed\(\);return;\}/.test(press),
    "a refused save leaves the page saying a sweep is armed when nothing is");
  assert.ok(/setT\("tCleanseArmed",!!r\.on\)/.test(press),
    "the switch keeps what the finger did rather than what the host stored");
  assert.ok(/!Native\.available/.test(press),
    "a browser with no realm to arm still posts the call");
});

test("the seam a walk drives is really exported", () => {
  ["cleanseArmedState", "cleanseArmedEvent", "cleanseRanEvent"].forEach(name => {
    assert.ok(new RegExp(name + ":").test(SOURCE), "app.js exports no " + name + " seam");
  });
});

/* ------------------------------------------------------------------ the real press
   THE ORDER THE LISTENERS WERE ADDED IN, which is the one thing a regex over a handler's
   body cannot see. The switch carries data-t, so the class that says which way it just went
   is flipped by the generic toggle handler further down app.js, and onCleanseArmedClick
   READS that class. Both are ordinary click listeners on the SAME element, and a browser
   runs them in the order they were ADDED: a registration standing above the generic one
   reads the state the switch is LEAVING and posts the opposite of what the host asked for,
   so arming a sweep sends {on:false} and the reply puts the switch straight back down.
   1.2.6 shipped exactly that. So this lifts BOTH registration statements out of app.js in
   the order the file really adds them, puts them on one element with a real event model,
   and presses it. */
function pressHarness() {
  const lines = [];
  /* The generic wiring is a BLOCK now, not a line: batch D gave every switch a keydown, a role
     and a tabindex in the same loop, so the lift reads to the statement that closes it. The
     single-line reader this replaces stopped at the block's first line, and the two listeners
     never both existed in the harness. */
  for (const opening of ['$("#tCleanseArmed")?.addEventListener("click"',
    '$$("[data-t]").forEach(t=>{']) {
    const at = SOURCE.indexOf(opening);
    assert.ok(at > 0, "app.js no longer holds " + JSON.stringify(opening));
    const end = opening.endsWith("{")
      ? SOURCE.indexOf("\n});", at) + 4
      : SOURCE.indexOf("\n", at);
    assert.ok(end > at, JSON.stringify(opening) + " no longer closes where this can read it");
    lines.push({ at, code: SOURCE.slice(at, end) });
  }
  // In source order, because that IS the registration order for one script evaluated once.
  lines.sort((a, b) => a.at - b.at);

  const classes = new Set();
  const listeners = [];
  const el = {
    classList: {
      contains: c => classes.has(c),
      toggle(c, on) {
        if (on === undefined) { if (classes.has(c)) classes.delete(c); else classes.add(c); return; }
        if (on) classes.add(c); else classes.delete(c);
      },
    },
    addEventListener(type, fn) { if (type === "click") listeners.push(fn); },
    click() { for (const fn of listeners.slice()) fn({ type: "click" }); },
    // The generic wiring reads and writes these now: a switch says what it is to something
    // that is not a pair of eyes, and the harness has to let it.
    attrs: {},
    getAttribute(name) {
      return Object.prototype.hasOwnProperty.call(el.attrs, name) ? el.attrs[name] : null;
    },
    setAttribute(name, value) { el.attrs[name] = String(value); },
  };

  const calls = [];
  const said = [];
  const context = {
    S: { prefs: { ProfileName: "A", CleanseWhenEmpty: false } },
    FAIL: Symbol("FAIL"),
    Native: { available: true },
    console,
    $: s => (s === "#tCleanseArmed" ? el : null),
    $$: s => (s === "[data-t]" ? [el] : []),
    syncAdvGates: () => {},
    T: id => id,
    toast: text => said.push(text),
    logLine: (level, text) => said.push(level + ": " + text),
    // The host side, echoing what it was asked for the way the real one does.
    rpc: async (method, payload) => { calls.push([method, payload]); return { on: !!(payload && payload.on) }; },
  };
  context.setT = (id, on) => context.$("#" + id).classList.toggle("on", !!on);
  context.swOn = id => context.$("#" + id).classList.contains("on");
  vm.createContext(context);
  vm.runInContext(fn("function renderCleanseArmed(){"), context, { filename: "app.js#armed" });
  vm.runInContext(fn("async function onCleanseArmedClick(){"), context, { filename: "app.js#press" });

  // The press is fired by a listener that does not await it, so the promise is caught here
  // and the test waits on it rather than on a timer.
  const real = context.onCleanseArmedClick;
  let pending = null;
  context.onCleanseArmedClick = () => { pending = real(); return pending; };

  // The generic wiring calls both of these, so they are lifted rather than stubbed: what they
  // write is the aria half of a switch and its name, and a stub here would be a copy of the
  // thing under test. The fake switch below carries no row and no label, so nameSwitch answers
  // with nothing and writes nothing, which is the shape this rule wants: it is about the press.
  vm.runInContext(fn("function markSwitch(t){"), context, { filename: "app.js#aria" });
  const seq = SOURCE.indexOf("let SWITCH_LABEL_SEQ=");
  assert.ok(seq > 0, "app.js no longer holds the switch label counter");
  vm.runInContext(SOURCE.slice(seq, SOURCE.indexOf("\n", seq)), context, { filename: "app.js#seq" });
  vm.runInContext(fn("function nameSwitch(t){"), context, { filename: "app.js#name" });

  for (const line of lines) vm.runInContext(line.code, context, { filename: "app.js#wire" });

  return {
    el, calls, said, context,
    on: () => classes.has("on"),
    press: async () => { el.click(); await pending; },
  };
}

atest("a real press posts what the host asked for, through both listeners in app.js's own order", async () => {
  const H = pressHarness();
  assert.strictEqual(H.on(), false, "the harness starts with the switch already on");

  await H.press();

  // The payload is an object built inside the vm realm, so it is read field by field
  // rather than compared whole: deepStrictEqual holds prototypes to account too.
  assert.strictEqual(H.calls.length, 1, "one press did not make exactly one call");
  assert.strictEqual(H.calls[0][0], "players.cleanseWhenEmpty", "the press called something else");
  assert.strictEqual(H.calls[0][1].on, true,
    "arming the switch posted the state it was LEAVING, not the one the host asked for: the"
    + " registration on #tCleanseArmed is standing above the generic [data-t] handler, so it"
    + " reads .on before the flip and the sweep can never be armed from the window");
  assert.strictEqual(H.on(), true, "the switch did not settle on what the host stored");
  assert.ok(H.said.some(s => /armed\.on\.toast/.test(s)),
    "the press said the sweep was put down while arming it");
});

atest("and a second press puts it back down, posting off", async () => {
  const H = pressHarness();
  await H.press();
  await H.press();

  assert.strictEqual(H.calls.length, 2, "two presses did not make two calls");
  assert.strictEqual(H.calls[1][0], "players.cleanseWhenEmpty", "the second press called something else");
  assert.strictEqual(H.calls[1][1].on, false, "pressing an armed switch does not post off");
  assert.strictEqual(H.on(), false, "the switch stayed up after the host put it down");
});

(async () => {
  for (const one of later) {
    try {
      await one.body();
      passed++;
      console.log("  ok   " + one.name);
    } catch (problem) {
      failures.push(one.name);
      console.log("  FAIL " + one.name);
      console.log("       " + (problem && problem.message ? problem.message : problem));
    }
  }

  console.log("");
  if (failures.length) {
    console.log("cleanse armed selftest: " + failures.length + " FAILED, " + passed + " passed");
    process.exit(1);
  }
  console.log("cleanse armed selftest: " + passed + " passed");
})();
