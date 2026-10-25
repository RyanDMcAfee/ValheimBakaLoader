/* The Saga console's two empty states, held against the lines that keep arriving.
 *
 * WHY THIS EXISTS. 1.2.6 gave the console a second empty state: a pill or a search box that
 * hides every line now says "No lines match" and hands back a button that widens it again,
 * instead of leaving about thirteen hundred pixels of black with no text in it. The note was
 * drawn by renderTermEmpty, which only ran when the host CHANGED the pill or the search box.
 * Every new line went through termAppend, which began by tearing the note out and never put
 * it back. A server that is up writes lines the whole time, so the note lived until the next
 * one landed, which is a second or two, and the panel went black again with every line still
 * hidden. The fix was defeated within seconds of being shown.
 *
 * These drive the real lnVisible, applyTermVis, renderTermEmpty and termAppend out of app.js
 * over a small stand-in for the console element, so the rule is held where it broke: at the
 * seam between "the host narrowed the view" and "another line arrived".
 *
 * 1.2.16 adds the realms. With two realms running, every application line arrived untagged
 * and both realms' Saga showed both realms' starts, stops and sweeps. A line now carries the
 * realm it was written for (dataset.p) and lnVisible hides another realm's lines; a line that
 * belongs to no realm shows on every realm. Rules 6 to 12 drive the real logLine, logRaw,
 * lnVisible, applyTermVis, the replay and the Hearth card over two realms interleaved.
 *
 * Keeping every realm's lines meant the scrollback cap, which dropped the oldest line whoever
 * it was, let a background realm booting a modded server push the on-screen realm's history out
 * of the console. The last rules hold the cap realm by realm, and the replay's dedupe by the
 * line's own time, so an earlier session's identical sentence does not hide this one's.
 *
 * 1.3.0 (issue 27) turned the console over: the newest line is the top one and the cap trims
 * from the bottom. Every rule here holds as it did; the orders they read are newest first now,
 * and the loud realm keeps its newest line at the top of its run rather than the bottom.
 * saga_order_selftest.js holds the turning over itself.
 *
 *     node scripts/ui/saga_empty_selftest.js [app.js]
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const SOURCE = fs.readFileSync(APP, "utf8");

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
  return SOURCE.slice(from, to) + "\n}";
}

/* A console element with only what these four functions touch on it. */
function makeConsole() {
  const el = {
    children: [],
    scrollTop: 0,
    scrollHeight: 0,
    querySelectorAll(selector) {
      const want = selector.replace(/^#term\s+/, "").replace(/^\./, "");
      return el.children.filter(c => c.cls.split(" ").indexOf(want) >= 0);
    },
    querySelector(selector) { return el.querySelectorAll(selector)[0] || null; },
    appendChild(node) { node.parent = el; el.children.push(node); },
    insertBefore(node, before) {
      const at = el.children.indexOf(before);
      node.parent = el;
      if (at < 0) el.children.push(node); else el.children.splice(at, 0, node);
    },
    insertAdjacentHTML(_where, html) {
      const note = { cls: "empty-state", style: {}, dataset: {}, html, parent: el,
        remove() {
          if (this.parent) {
            this.parent.children = this.parent.children.filter(c => c !== this);
            this.parent = null;
          }
        } };
      el.children.push(note);
    },
    get firstChild() { return el.children[0] || null; },
  };
  return el;
}

function harness() {
  const term = makeConsole();
  let wired = 0;
  const context = {
    term,
    termQ: "",
    filter: "all",
    termPin: true,
    termUnseen: 0,
    termHeld: false,
    termGoingTop: false,
    termHoldBegin: () => {},
    TERM_CAP: 1200,
    T: id => id,
    esc: s => String(s),
    emptyState: o => "<empty " + o.title + ">",
    esWire: () => { wired++; },
    renderTermStream: () => {},
    console,
  };
  context.$$ = selector => term.querySelectorAll(selector);
  vm.createContext(context);
  context.S = { profileName: null };
  vm.runInContext("var termRealmCount=null, termRealmTotal=0;", context);
  for (const opening of ["function sagaRealmShown(p){", "function lnVisible(el){", "function applyTermVis(){",
    "function renderTermEmpty(){", "function termAppend(d){", "function termLineRealm(el){",
    "function termCount(el,by){", "function termEvict(){"]) {
    vm.runInContext(fn(opening), context, { filename: "app.js#" + opening });
  }
  context.line = kind => {
    const node = { cls: "ln " + kind, style: { display: "" }, dataset: { k: kind, s: "" },
      parent: null, remove() {
        if (this.parent) {
          this.parent.children = this.parent.children.filter(c => c !== this);
          this.parent = null;
        }
      } };
    node.style.display = context.lnVisible(node) ? "" : "none";
    context.pending = node;
    vm.runInContext("termAppend(pending)", context);
    return node;
  };
  context.noteKind = () => {
    const note = term.querySelector(".empty-state");
    return note ? note.dataset.emptyKind : null;
  };
  context.wiredCount = () => wired;
  return context;
}

/* ------------------------------------------------------ rule 1: the note survives traffic */
test("a narrowed console keeps saying so while the server writes", () => {
  const h = harness();
  vm.runInContext("renderTermEmpty()", h);
  assert.strictEqual(h.noteKind(), "cold", "a cold console no longer says it is empty");

  h.line("info");
  assert.strictEqual(h.term.querySelector(".empty-state"), null,
    "the cold note outlived the first line");

  h.filter = "err";
  vm.runInContext("applyTermVis()", h);
  assert.strictEqual(h.noteKind(), "narrowed", "narrowing no longer draws the narrowed note");

  for (let i = 0; i < 25; i++) h.line("info");
  assert.strictEqual(h.noteKind(), "narrowed",
    "the narrowed note was wiped by the lines that arrived after it: the console is black "
    + "again with every line hidden");
  assert.strictEqual(h.term.querySelectorAll(".ln").length, 26,
    "the lines themselves stopped arriving");
});

/* ----------------------------------------------- rule 2: and goes when it should not stand */
test("a line the pill does show clears the note again", () => {
  const h = harness();
  h.line("info");
  h.filter = "err";
  vm.runInContext("applyTermVis()", h);
  assert.strictEqual(h.noteKind(), "narrowed");

  h.line("err");
  assert.strictEqual(h.term.querySelector(".empty-state"), null,
    "a line the host CAN see left the empty state standing over it");
});

/* ------------------------------------------------- rule 3: a search that matches nothing */
test("a search that matches nothing draws the note too", () => {
  const h = harness();
  h.line("info");
  h.termQ = "nothing in here matches this";
  vm.runInContext("applyTermVis()", h);
  assert.strictEqual(h.noteKind(), "narrowed");

  h.line("info");
  assert.strictEqual(h.noteKind(), "narrowed", "the search's empty state did not survive a line");
});

/* ------------------------------------------- rule 4: asked per line, not redrawn per line */
test("the note is read on every line and redrawn on none of them", () => {
  const h = harness();
  h.line("info");
  h.filter = "err";
  vm.runInContext("applyTermVis()", h);
  const drawnOnce = h.wiredCount();

  for (let i = 0; i < 25; i++) h.line("info");
  assert.strictEqual(h.wiredCount(), drawnOnce,
    "the empty state is being torn out and rebuilt once per log line");
});

/* --------------------------------------- rule 5: the append road repaints, in the source */
test("termAppend asks renderTermEmpty rather than assuming", () => {
  const body = fn("function termAppend(d){");
  assert.ok(body.indexOf("renderTermEmpty()") > 0,
    "termAppend no longer repaints the empty state, so the next line blanks the console");
  assert.ok(body.indexOf('querySelector(".empty-state"); if(es) es.remove();') < 0,
    "termAppend is tearing the empty state out again and nothing puts it back");
});

/* ------------------------------------------------------------- the realms (1.2.16, F17) */

/** One top level const line out of app.js, run as it is written. */
function constLine(opening) {
  const from = SOURCE.indexOf("\n" + opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n", from + 1);
  return SOURCE.slice(from + 1, to);
}

/* The console plus everything logLine and the Hearth card touch, with two realms in it. */
function realmHarness() {
  const h = harness();
  const card = { innerHTML: "" };
  Object.assign(h, {
    currentPage: "hearth",
    hlogDirty: false,
    hlogRaf: 0,
    HLOG_MAX: 30,
    clock: () => "12:00",
    pad: n => String(n).padStart(2, "0"),
    requestAnimationFrame: cb => { cb(); return 0; },
    $: selector => (selector === "#hearthLog" ? card : null),
    document: {
      createElement: () => ({
        className: "",
        get cls() { return this.className; },
        style: { display: "" },
        dataset: {},
        innerHTML: "",
        parent: null,
        remove() {
          if (this.parent) {
            this.parent.children = this.parent.children.filter(c => c !== this);
            this.parent = null;
          }
        },
      }),
    },
  });
  for (const opening of ["const NET_RE=", "const LOG_TS_RE=", "const LOG_TAG_RE=", "const LOG_TAG_KIND=",
    "const LOG_ERR_RE=", "const LOG_FRAME_RE=", "const SAGA_DAY_RE=", "const HLOG_BUF="]) {
    vm.runInContext(constLine(opening), h, { filename: "app.js#" + opening });
  }
  vm.runInContext("var hlogBuf=[];", h);
  /* logRaw is written on one line, so it is taken as the line it is */
  vm.runInContext(constLine("function logRaw(line,profile,day){"), h, { filename: "app.js#logRaw" });
  for (const opening of ["function sagaLineRealm(profile){", "function sagaStamp(time,day,now){",
    "function logLine(kind,text,time,profile,day){",
    "function logDivider(text){", "function classifyLog(line){",
    "function sagaReplayUnseen(rows){", "function sagaRenameRealm(from,to){",
    "function hlogPush(kind,text,time,p){", "function renderHearthLog(){"]) {
    vm.runInContext(fn(opening), h, { filename: "app.js#" + opening });
  }
  h.card = card;
  h.run = code => vm.runInContext(code, h);
  h.on = name => { h.S.profileName = name; };
  /* the text of every line the host can see right now, in order: newest first since 1.3.0 */
  h.shown = () => h.term.children
    .filter(c => (c.cls || "").split(" ").indexOf("ln") >= 0 && c.dataset.k !== "div" && c.style.display !== "none")
    .map(c => c.dataset.s);
  h.dividersShown = () => h.term.children
    .filter(c => c.dataset && c.dataset.k === "div" && c.style.display !== "none").length;
  /* Alpha and Beta both running, their lines interleaved with lines that belong to neither */
  h.interleave = () => {
    h.run('logRaw("[12:00:01.000] alpha starting",  "Alpha")');
    h.run('logRaw("[12:00:02.000] beta starting",   "Beta")');
    h.run('logRaw("[12:00:03.000] [WRN] a language pack is out of date", null)');
    h.run('logRaw("[12:00:04.000] [ERR] alpha sweep failed", "Alpha")');
    h.run('logRaw("[12:00:05.000] [ERR] beta sweep failed",  "BETA")');
  };
  return h;
}

test("two realms interleaved: the realm on screen and the lines of no realm, nothing else", () => {
  const h = realmHarness();
  h.on("Alpha");
  h.interleave();
  assert.deepStrictEqual(h.shown(),
    ["alpha sweep failed", "a language pack is out of date", "alpha starting"],
    "the Saga of Alpha is showing Beta's lines, or hiding its own or the app's");
});

test("a switch shows the other realm's lines it already holds, and the divider stays", () => {
  const h = realmHarness();
  h.on("Alpha");
  h.interleave();
  h.run('logDivider("switched to Beta")');
  h.on("Beta");
  h.run("applyTermVis()");
  assert.deepStrictEqual(h.shown(),
    ["beta sweep failed", "a language pack is out of date", "beta starting"],
    "a switch to Beta did not show Beta's own lines (case aside) or still shows Alpha's");
  assert.strictEqual(h.dividersShown(), 1, "the switch divider was hidden by the realm rule");
  assert.strictEqual(h.term.querySelectorAll(".ln").filter(c => c.dataset.k !== "div").length, 5,
    "a switch cleared or replayed the console instead of showing what it holds");

  h.on("Alpha");
  h.run("applyTermVis()");
  assert.deepStrictEqual(h.shown(),
    ["alpha sweep failed", "a language pack is out of date", "alpha starting"],
    "switching back did not bring Alpha's lines back");
});

test("a line of no realm shows on both, and with no realm chosen everything shows", () => {
  const h = realmHarness();
  h.interleave();
  assert.strictEqual(h.shown().length, 5, "with no realm on screen a tagged line was hidden");
  for (const realm of ["Alpha", "Beta"]) {
    h.on(realm);
    h.run("applyTermVis()");
    assert.ok(h.shown().indexOf("a language pack is out of date") >= 0,
      "the line that belongs to no realm is missing on " + realm);
  }
});

test("search and the kind pills still combine with the realm", () => {
  const h = realmHarness();
  h.on("Alpha");
  h.interleave();
  h.filter = "err";
  h.run("applyTermVis()");
  assert.deepStrictEqual(h.shown(), ["alpha sweep failed"],
    "the Errors pill on Alpha showed another realm's error, or lost Alpha's");
  h.filter = "all";
  h.termQ = "sweep";
  h.run("applyTermVis()");
  assert.deepStrictEqual(h.shown(), ["alpha sweep failed"], "a search reached into Beta's lines");
  h.termQ = "beta";
  h.run("applyTermVis()");
  assert.deepStrictEqual(h.shown(), [], "a search on Alpha found Beta's lines");
  assert.strictEqual(h.noteKind(), "narrowed",
    "a search that only matches another realm left a black console instead of the note");
});

test("a page line defaults to the realm on screen, null means none, and the text stays verbatim", () => {
  const h = realmHarness();
  h.on("Alpha");
  h.run('logLine("ok","[BakaLoader] start requested")');
  h.run('logLine("info","[BakaLoader] the interface text size is now 3.",null,null)');
  const [global, action] = h.term.children.filter(c => c.dataset.k !== "div");
  assert.strictEqual(action.dataset.p, "alpha", "a realm action was not tagged with the realm on screen");
  assert.strictEqual(global.dataset.p, undefined, "a line passed null was tagged with a realm");
  assert.ok(action.innerHTML.indexOf("Alpha") < 0 && action.innerHTML.indexOf("alpha") < 0,
    "the realm was written into the line's text: the Saga is the verbatim log");
  h.on("Beta");
  h.run("applyTermVis()");
  assert.deepStrictEqual(h.shown(), ["[bakaloader] the interface text size is now 3."]);
});

test("the replay prints only what the console never saw", () => {
  const h = realmHarness();
  h.on("Beta");
  h.run('logRaw("10/20/2026 12:00:01: Game server connected", "Beta")');
  h.run('logRaw("10/20/2026 12:00:09: Connections 1 ZDOS:10 sent:0 recv:0", "Beta")');
  h.pendingRows = [
    { line: "10/20/2026 11:59:58: Loading world", profile: "Beta" },
    { line: "10/20/2026 12:00:01: Game server connected", profile: "Beta" },
    { line: "10/20/2026 12:00:09: Connections 1 ZDOS:10 sent:0 recv:0", profile: "Beta" },
    { line: "10/20/2026 12:00:09: Connections 1 ZDOS:10 sent:0 recv:0", profile: "Beta" },
  ];
  h.run("sagaReplayUnseen(pendingRows)");
  const lines = h.shown();
  assert.strictEqual(lines.filter(l => l.indexOf("game server connected") >= 0).length, 1,
    "a line the console already held was printed a second time on the replay");
  assert.strictEqual(lines.filter(l => l.indexOf("connections 1") >= 0).length, 2,
    "a line that really came twice lost its second copy to the dedupe");
  assert.ok(lines.some(l => l.indexOf("loading world") >= 0), "a line the page never saw was not replayed");
});

test("the Hearth card shows the realm on screen and the lines of no realm", () => {
  const h = realmHarness();
  h.on("Alpha");
  h.interleave();
  assert.ok(h.card.innerHTML.indexOf("alpha sweep failed") >= 0, "the card lost Alpha's own line");
  assert.ok(h.card.innerHTML.indexOf("a language pack is out of date") >= 0, "the card lost the app's line");
  assert.ok(h.card.innerHTML.indexOf("beta") < 0, "the Hearth card on Alpha shows Beta's lines");
  h.on("Beta");
  h.run("renderHearthLog()");
  assert.ok(h.card.innerHTML.indexOf("beta sweep failed") >= 0, "after a switch the card does not show Beta's line");
  assert.ok(h.card.innerHTML.indexOf("alpha") < 0, "after a switch the card still shows Alpha's lines");
});

test("a renamed realm keeps its lines", () => {
  const h = realmHarness();
  h.on("Alpha");
  h.interleave();
  h.on("Hearthstone");
  h.run('sagaRenameRealm("Alpha","Hearthstone")');
  assert.deepStrictEqual(h.shown(),
    ["alpha sweep failed", "a language pack is out of date", "alpha starting"],
    "a rename left the realm's lines under the old name, hidden for good");
  assert.ok(h.card.innerHTML.indexOf("alpha sweep failed") >= 0, "a rename lost the card's lines");
});

test("a loud background realm cannot push the realm on screen out of the scrollback", () => {
  const h = realmHarness();
  h.on("Alpha");
  for (let i = 0; i < 50; i++) h.run('logRaw("[12:00:00.000] alpha line ' + i + '", "Alpha")');
  for (let i = 0; i < 1300; i++) h.run('logRaw("[12:01:00.000] beta boot line ' + i + '", "Beta")');
  const held = h.term.children.filter(c => c.dataset && c.dataset.k);
  assert.ok(held.length <= h.TERM_CAP, "the console holds " + held.length + " lines, over the cap");
  const alpha = h.shown().filter(s => s.indexOf("alpha line ") === 0);
  assert.strictEqual(alpha.length, 50,
    "Beta's boot pushed " + (50 - alpha.length) + " of Alpha's lines out of the console Alpha's host is reading");
  /* newest first since 1.3.0: the realm's newest line heads its run, and what the cap took
     was the oldest lines, from the bottom */
  const beta = held.filter(c => c.dataset.p === "beta");
  assert.ok(beta.length > 0 && beta[0].dataset.s === "beta boot line 1299",
    "the background realm lost its newest lines instead of its oldest");
  assert.strictEqual(beta[beta.length - 1].dataset.s, "beta boot line " + (1300 - beta.length),
    "the cap did not take the background realm's oldest lines from the bottom");
});

test("the realm on screen, when it is the loud one, pays for its own noise", () => {
  const h = realmHarness();
  h.on("Alpha");
  for (let i = 0; i < 50; i++) h.run('logRaw("[12:00:00.000] beta line ' + i + '", "Beta")');
  h.run('logRaw("[12:00:00.000] a language pack is out of date", null)');
  for (let i = 0; i < 1300; i++) h.run('logRaw("[12:01:00.000] alpha boot line ' + i + '", "Alpha")');
  const held = h.term.children.filter(c => c.dataset && c.dataset.k);
  assert.ok(held.length <= h.TERM_CAP, "the console holds " + held.length + " lines, over the cap");
  assert.strictEqual(held.filter(c => c.dataset.p === "beta").length, 50, "Alpha's noise evicted Beta's quiet history");
  assert.ok(h.shown().indexOf("a language pack is out of date") >= 0, "Alpha's noise evicted the line of no realm");
  h.on("Beta");
  h.run("applyTermVis()");
  assert.strictEqual(h.shown().filter(s => s.indexOf("beta line ") === 0).length, 50);
});

test("an earlier session's identical line does not hide this session's from the replay", () => {
  const h = realmHarness();
  h.on("Beta");
  h.run('logRaw("[11:00:00.000] Game server connected", "Beta")');
  h.pendingRows = [
    { line: "[11:00:00.000] Game server connected", profile: "Beta" },
    { line: "[12:00:00.000] Game server connected", profile: "Beta" },
  ];
  h.run("sagaReplayUnseen(pendingRows)");
  assert.strictEqual(h.shown().filter(l => l === "game server connected").length, 2,
    "the replay took this session's line for the earlier session's and dropped it, or printed the held one twice");
});

test("the realm switch reapplies visibility and replays only through the dedupe", () => {
  const from = SOURCE.indexOf("async function switchServer(name,opts){");
  assert.ok(from > 0, "switchServer is gone");
  const body = SOURCE.slice(from, SOURCE.indexOf("\n}", from));
  assert.ok(body.indexOf("applyTermVis()") > 0, "a switch no longer reapplies the realm rule to the console");
  assert.ok(body.indexOf("sagaReplayUnseen(") > 0, "a switch no longer replays only the unseen lines");
  assert.ok(!/serverBuffer[\s\S]*forEach\(logRaw\)/.test(body),
    "a switch replays the whole server tail again, printing every line the console holds twice");
  assert.ok(/Native\.on\("log\.app",d=>\{[^\n]*d\.profile/.test(SOURCE), "log.app no longer reads the line's realm");
});

console.log("");
if (failures.length) {
  console.log("saga empty selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("saga empty selftest: " + passed + " passed");
