/* The Log turned over (1.3.0, issue 27), held against the real app.js.
 *
 * WHY THIS EXISTS. A tester's mock-up of the Log asked for the newest line first, the command
 * line on top, every line stamped with its date as well as its time, and live streaming that
 * pauses while the reader is looking at older lines. Turning a console over is the kind of change
 * that looks done in a screenshot and is wrong a minute later: the cap trims the newest lines
 * instead of the oldest, a paused reader's line slides away under every new line, the chip still
 * points down, a host row from an older host reads "undefined 12:00:01", the console opens on the
 * wrong end. Mapping the Log also turned up two things that were already broken: the console
 * opened on its OLDEST lines when it had been folded at load (and said it was streaming), and a
 * real exception line was classed Info, because \bexception\b does not match
 * NullReferenceException and the stamp's \s* ate the indentation the stack-frame test looked for.
 *
 * These drive the real termAppend, termEvict, logLine, sagaStamp, classifyLog, the hold, the
 * scroll rule, the chip, the Pause button and the opening out of app.js over a stand-in console
 * that does layout arithmetic the way chromium does it (a line is 19.25 px, a hidden one is 0,
 * offsetTop and scrollTop come out in whole pixels, scrollTop is clamped), so "the line being read
 * did not move" is a number, not a reading.
 * scripts/ui/saga_order_probe.js asks the same questions of the real page in chromium.
 *
 *     node scripts/ui/saga_order_selftest.js [app.js]
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const SOURCE = fs.readFileSync(APP, "utf8").replace(/\r\n/g, "\n");
const WEB = path.dirname(APP);
const HTML = fs.readFileSync(path.join(WEB, "index.html"), "utf8").replace(/\r\n/g, "\n");
const CSS = fs.readFileSync(path.join(WEB, "app.css"), "utf8").replace(/\r\n/g, "\n");

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
/** One top level line out of app.js, run as it is written. */
function line(opening) {
  const from = SOURCE.indexOf("\n" + opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  return SOURCE.slice(from + 1, SOURCE.indexOf("\n", from + 1));
}

const LINE_H = 19.25;   // 11px mono at line-height 1.75, as .term draws a line

/* A node with what these functions touch on it, and a box: y is where layout puts it, from the
   boxes above it; offsetTop rounds that to whole pixels as a browser does, and the rect is where
   it is on screen with the console's scroll taken off. */
function node(cls, h) {
  const n = {
    className: cls || "",
    get cls() { return this.className; },
    style: { display: "" },
    dataset: {},
    innerHTML: "",
    h: h || LINE_H,
    parent: null,
    get parentNode() { return this.parent; },
    get y() {
      const p = this.parent;
      if (!p) return 0;
      let y = p.padTop;
      for (const c of p.children) {
        if (c === this) return y;
        if (c.style.display !== "none") y += c.h;
      }
      return 0;
    },
    get offsetTop() { return Math.round(this.y); },
    getBoundingClientRect() { return { top: this.y - (this.parent ? this.parent._top : 0), height: this.h }; },
    remove() {
      if (this.parent) {
        this.parent.children = this.parent.children.filter(c => c !== this);
        this.parent = null;
      }
    },
  };
  return n;
}

function makeConsole(clientHeight) {
  const el = {
    children: [],
    padTop: 10,
    padBottom: 10,
    clientHeight,
    _top: 0,
    writes: 0,
    slides: [],
    contentHeight() { return el.children.reduce((t, c) => t + (c.style.display === "none" ? 0 : c.h), 0); },
    get scrollHeight() { return Math.max(el.clientHeight, el.padTop + el.contentHeight() + el.padBottom); },
    get scrollTop() { return el._top; },
    set scrollTop(v) {   // whole pixels, as chromium keeps it
      el.writes++;
      el._top = Math.round(Math.max(0, Math.min(Math.max(0, el.scrollHeight - el.clientHeight), v)));
    },
    scrollTo(o) { el.slides.push(o); el.scrollTop = o.top; },
    get firstChild() { return el.children[0] || null; },
    querySelectorAll(selector) {
      const want = selector.replace(/^#term\s+/, "").replace(/^\./, "");
      return el.children.filter(c => c.cls.split(" ").indexOf(want) >= 0);
    },
    querySelector(selector) { return el.querySelectorAll(selector)[0] || null; },
    appendChild(n) { n.parent = el; el.children.push(n); },
    insertBefore(n, before) {
      const at = before ? el.children.indexOf(before) : -1;
      n.parent = el;
      if (at < 0) el.children.push(n); else el.children.splice(at, 0, n);
    },
    insertAdjacentHTML(_where, html) {
      const note = node("empty-state", 120);
      note.html = html;
      note.parent = el;
      el.children.push(note);
    },
    addEventListener() {},
  };
  return el;
}

function classes() {
  const set = new Set();
  return {
    add: c => set.add(c),
    remove: c => set.delete(c),
    toggle: (c, on) => { if (on === undefined ? !set.has(c) : on) set.add(c); else set.delete(c); },
    contains: c => set.has(c),
  };
}
function chipLike() {
  const attrs = {};
  return { classList: classes(), textContent: "", setAttribute: (k, v) => { attrs[k] = v; }, attr: k => attrs[k] };
}

function harness(opts) {
  const o = opts || {};
  const term = makeConsole(o.clientHeight || 400);
  const chip = chipLike(), hold = chipLike();
  chip.classList.add("hidden");
  const frames = [];
  const timers = [];
  const hearth = [];
  const context = {
    term,
    termQ: "",
    filter: "all",
    termPin: true,
    termUnseen: 0,
    termHeld: false,
    termGoingTop: false,
    termGoingT: 0,
    termHoldRef: null,
    termHoldAt: 0,
    termHoldRaf: 0,
    termHoldCarry: 0,
    termRealmCount: null,
    termRealmTotal: 0,
    TERM_CAP: o.cap || 1200,
    S: { profileName: o.realm === undefined ? "Alpha" : o.realm },
    T: (id, p) => (p && p.count !== undefined ? id + ":" + p.count : id),
    esc: s => String(s),
    pad: n => String(n).padStart(2, "0"),
    emptyState: x => "<empty " + x.title + ">",
    esWire: () => {},
    hlogPush: (kind, text, time) => hearth.push(time),
    revealReduced: () => !!context.reduced,
    reduced: false,
    requestAnimationFrame: cb => { frames.push(cb); return frames.length; },
    cancelAnimationFrame: id => { frames[id - 1] = null; },
    setTimeout: (cb, ms) => { timers.push(cb); return timers.length; },
    clearTimeout: id => { if (id) timers[id - 1] = null; },
    $: s => ({ "#termNew": chip, "#termHold": hold }[s] || null),
    document: { createElement: () => node("") },
    console,
  };
  context.$$ = selector => term.querySelectorAll(selector);
  vm.createContext(context);
  for (const opening of ["const NET_RE=", "const LOG_TS_RE=", "const LOG_TAG_RE=", "const LOG_TAG_KIND=",
    "const LOG_ERR_RE=", "const LOG_FRAME_RE=", "const SAGA_DAY_RE="])
    vm.runInContext(line(opening), context, { filename: "app.js#" + opening });
  vm.runInContext(line("function logRaw(line,profile,day){"), context, { filename: "app.js#logRaw" });
  for (const opening of ["function sagaRealmShown(p){", "function sagaLineRealm(profile){", "function lnVisible(el){",
    "function applyTermVis(){", "function renderTermStream(){", "function renderTermEmpty(){",
    "function termContentTop(el){", "function termHoldBegin(){", "function termHoldSettle(){", "function termHoldDrop(){",
    "function termAppend(d){", "function termLineRealm(el){", "function termCount(el,by){", "function termEvict(){",
    "function sagaStamp(time,day,now){", "function logLine(kind,text,time,profile,day){", "function logDivider(text){",
    "function classifyLog(line){", "function sagaReplayUnseen(rows){", "function termOnScroll(){",
    "function sagaGoLive(){", "function termTakeBack(){", "function sagaHoldToggle(){", "function sagaToTop(){"]) {
    vm.runInContext(fn(opening), context, { filename: "app.js#" + opening });
  }
  const h = context;
  h.run = code => vm.runInContext(code, h);
  h.chip = chip; h.hold = hold; h.hearth = hearth;
  h.frames = () => frames.filter(Boolean).length;
  /* the browser runs the frame's callbacks before it paints that frame */
  h.paint = () => { const due = frames.splice(0); due.forEach(cb => cb && cb()); };
  h.fireTimers = () => { const due = timers.splice(0); due.forEach(cb => cb && cb()); };
  /* the reader moves the console; a scroll event follows, as in a browser */
  h.scrollTo = top => { term.scrollTop = top; h.run("termOnScroll()"); };
  h.host = (n, realm) => {
    for (let i = 0; i < n; i++) h.run('logRaw("[12:00:00.000] line ' + h.count++ + '", ' + JSON.stringify(realm === undefined ? "Alpha" : realm) + ', "2026-10-24")');
  };
  h.count = 0;
  h.texts = () => term.children.filter(c => c.dataset.k && c.dataset.k !== "div").map(c => c.dataset.s);
  /* the first line whose box reaches below the top of the view: what the reader is looking at,
     and where it stands on screen */
  h.lineAtTop = () => term.children.find(c => c.dataset.k && c.style.display !== "none"
    && c.y + c.h > term.scrollTop);
  h.onScreen = el => el.getBoundingClientRect().top;
  return h;
}

/* ---------------------------------------------------------- 1. newest first */
test("1. the newest line is the top one, host rows and page rows alike", () => {
  const h = harness();
  h.run('logRaw("[12:00:01.000] alpha starting", "Alpha", "2026-10-24")');
  h.run('logLine("ok","[BakaLoader] start requested")');
  h.run('logRaw("[12:00:03.000] Game server connected", "Alpha", "2026-10-24")');
  assert.deepStrictEqual(h.texts(), ["game server connected", "[bakaloader] start requested", "alpha starting"],
    "the console no longer reads newest first");
  h.run('logDivider("session · Alpha")');
  assert.strictEqual(h.term.children[0].dataset.k, "div", "a divider did not go in at the top with the lines");
  assert.ok(/insertBefore\(d,term\.firstChild\)/.test(fn("function termAppend(d){")),
    "termAppend no longer puts the line at the top");
});

/* ---------------------------------------------------------- 2. the cap trims from the bottom */
test("2. the cap takes the oldest lines, from the bottom", () => {
  const h = harness({ cap: 40 });
  h.host(55);
  const held = h.texts();
  assert.strictEqual(held.length, 40, "the console holds " + held.length + " lines against a cap of 40");
  assert.strictEqual(held[0], "line 54", "the cap took the newest line off the top");
  assert.strictEqual(held[held.length - 1], "line 15", "the cap did not take the oldest lines off the bottom");
});

/* ---------------------------------------------------------- 3. live at the top */
test("3. live at the top: a new line lands on top, nothing scrolls, nothing is counted", () => {
  const h = harness();
  h.host(60);
  assert.strictEqual(h.termPin, true);
  const writes = h.term.writes;
  h.host(5);
  h.paint();
  assert.strictEqual(h.term.scrollTop, 0, "a live console left its top");
  assert.strictEqual(h.term.writes, writes, "a live console wrote scrollTop on a new line (it would cut a slide back to the top short)");
  assert.strictEqual(h.termUnseen, 0, "a live console counted lines as unseen");
  assert.ok(h.chip.classList.contains("hidden"), "the chip shows while the console is live");
  assert.strictEqual(h.frames(), 0, "a live console held its view for a frame it does not need");
});

/* ---------------------------------------------------------- 4. pause on scroll, no jump */
test("4. scrolled down to older lines, new lines go in above and the line being read does not move", () => {
  const h = harness();
  h.host(100);
  h.scrollTo(400);
  assert.strictEqual(h.termPin, false, "leaving the top did not pause the console");
  const ref = h.lineAtTop();
  const seen = h.onScreen(ref);
  /* a frame's worth: three of this realm, one of another realm (hidden), a divider */
  h.host(3);
  h.host(1, "Beta");
  h.run('logDivider("session · Alpha")');
  assert.ok(h.onScreen(ref) > seen, "the stand-in did not move the line, so this proves nothing");
  assert.strictEqual(h.frames(), 1, "the hold asked for more than one frame for one burst");
  h.paint();
  assert.ok(Math.abs(h.onScreen(ref) - seen) < 0.5,
    "the line being read moved by " + (h.onScreen(ref) - seen) + " px when lines went in above it");
  assert.strictEqual(h.termUnseen, 3, "the chip counts " + h.termUnseen + " unseen lines, not the 3 the reader can see");
  assert.ok(!h.chip.classList.contains("hidden"), "the chip is not showing what arrived");
  assert.strictEqual(h.chip.textContent, "↑ saga.stream.unseen.top:3", "the chip does not point up to the new lines");
  h.run("termOnScroll()");   // the browser's own scroll event for the hold's move
  assert.strictEqual(h.termPin, false, "the hold's own scroll unpaused the console");
});

test("4b. a flood while paused is one read and one write a frame, and the view still holds", () => {
  const h = harness();
  h.host(200);
  h.scrollTo(900);
  const ref = h.lineAtTop();
  const seen = h.onScreen(ref);
  const writes = h.term.writes;
  h.host(300);
  assert.strictEqual(h.frames(), 1, "a flood of 300 lines asked for " + h.frames() + " frames");
  h.paint();
  assert.strictEqual(h.term.writes - writes, 1, "a flood of 300 lines wrote scrollTop " + (h.term.writes - writes) + " times");
  assert.ok(Math.abs(h.onScreen(ref) - seen) < 0.5, "the flood moved the line being read by " + (h.onScreen(ref) - seen) + " px");
});

test("4c. one line a frame for a minute's worth of frames: the rounding is carried, the line does not walk", () => {
  const h = harness();
  h.host(120);
  h.scrollTo(500);
  const ref = h.lineAtTop();
  const seen = h.onScreen(ref);
  for (let i = 0; i < 40; i++) { h.host(1); h.paint(); }
  assert.ok(Math.abs(h.onScreen(ref) - seen) < 0.5,
    "forty lines one a frame walked the reader's line " + (h.onScreen(ref) - seen).toFixed(2)
    + " px (a 19.25 px line against a scrollTop kept in whole pixels)");
  /* a move the browser cut short, because the lines did not fill the console yet, is not owed */
  const s = harness({ clientHeight: 400 });
  s.host(5);
  s.run("sagaHoldToggle()");
  s.host(3); s.paint();
  assert.strictEqual(s.termHoldCarry, 0, "a clamped move was carried into the next frame as if owed");
});

/* ---------------------------------------------------------- 5. back to the top */
test("5. the chip says back to the top and slides there; its own slide does not pause it", () => {
  const h = harness();
  h.host(100);
  h.scrollTo(600);
  h.host(4);
  h.paint();
  const real = h.term.scrollTo;
  h.term.scrollTo = o => { h.term.slides.push(o); };   // a smooth slide takes frames to get there
  h.run("sagaGoLive()");
  const slide = h.term.slides[h.term.slides.length - 1] || {};
  assert.ok(slide.top === 0 && slide.behavior === "smooth", "the chip does not slide to the top: " + JSON.stringify(slide));
  assert.strictEqual(h.termPin, true, "the chip did not make the console live");
  assert.strictEqual(h.termUnseen, 0, "the chip left lines counted");
  assert.ok(h.chip.classList.contains("hidden"), "the chip stayed up after it was pressed");
  h.term._top = 300; h.run("termOnScroll()");   // the slide passing 300 on its way up
  assert.strictEqual(h.termPin, true, "the slide's own scroll event paused the console");
  h.host(2);
  assert.strictEqual(h.frames(), 0, "lines arriving during the slide were held as if paused");
  h.term._top = 0; h.run("termOnScroll()");
  assert.strictEqual(h.termGoingTop, false, "the slide never finished");
  h.term.scrollTo = real;
  /* reduced motion: a jump, no slide */
  const r = harness(); r.reduced = true;
  r.host(100); r.scrollTo(600); r.run("sagaGoLive()");
  assert.strictEqual(r.term.slides[r.term.slides.length - 1].behavior, "auto", "reduced motion still slides");
  assert.strictEqual(r.term.scrollTop, 0);
});

test("5b. a wheel during the slide hands the console back to the reader", () => {
  const h = harness();
  h.host(100);
  h.scrollTo(600);
  h.term.scrollTo = o => { h.term.slides.push(o); };
  h.run("sagaGoLive()");
  h.run("termTakeBack()");   // the wheel listener
  h.term._top = 450; h.run("termOnScroll()");
  assert.strictEqual(h.termPin, false, "the reader's own scroll during the slide did not pause the console");
  /* and a slide that never got there is asked about afresh */
  const s = harness();
  s.host(100); s.scrollTo(600);
  s.term.scrollTo = o => { s.term.slides.push(o); };
  s.run("sagaGoLive()");
  s.fireTimers();
  assert.strictEqual(s.termGoingTop, false, "a slide that stalled kept its scroll events muted for good");
  assert.strictEqual(s.termPin, false, "a slide that stalled short of the top left the console claiming to be live");
});

/* ---------------------------------------------------------- 6. the Pause button */
test("6. Pause holds the console where it stands, even at the top; Live follows again", () => {
  const h = harness();
  h.host(60);
  h.run("sagaHoldToggle()");
  assert.strictEqual(h.termPin, false, "Pause did not pause the console");
  assert.strictEqual(h.hold.attr("aria-pressed"), "true", "the button does not say it is pressed");
  assert.ok(h.hold.classList.contains("paused"), "the button does not show the pause");
  const ref = h.lineAtTop();
  const seen = h.onScreen(ref);
  h.host(3);
  h.paint();
  assert.ok(Math.abs(h.onScreen(ref) - seen) < 0.5, "a held console at its top let the lines push the reader's line down");
  h.scrollTo(0);
  assert.strictEqual(h.termPin, false, "scrolling back to the top undid a pause the button made");
  h.run("sagaHoldToggle()");
  assert.strictEqual(h.termPin, true, "Live did not make the console live");
  assert.strictEqual(h.hold.attr("aria-pressed"), "false");
  assert.ok(!h.hold.classList.contains("paused"), "the button still shows a pause after Live");
  /* paused by scrolling, the same button reads Live */
  h.scrollTo(300);
  assert.ok(h.hold.classList.contains("paused"), "a pause by scrolling does not show on the button");
  h.run("sagaHoldToggle()");
  assert.strictEqual(h.termPin, true, "the button did not take a scrolled console back to live");
});

/* ---------------------------------------------------------- 7. opens at the newest line */
test("7. the console opens on its newest line, whatever it was left at", () => {
  const h = harness();
  h.host(100);
  h.scrollTo(700);
  h.host(5);     // held, a frame pending
  h.run("sagaToTop()");
  assert.strictEqual(h.term.scrollTop, 0, "opening did not show the newest line");
  assert.strictEqual(h.termPin, true, "opening left the console paused");
  assert.strictEqual(h.termUnseen, 0);
  h.paint();
  assert.strictEqual(h.term.scrollTop, 0, "a hold from before the opening moved the console after it");
  /* the roads in: the section's way open and the walk into the Dashboard with it open */
  const row = SOURCE.slice(SOURCE.indexOf('{key:"saga",'), SOURCE.indexOf("\n", SOURCE.indexOf('{key:"saga",')));
  assert.ok(row.includes("enter:()=>{try{sagaToTop();}catch(_){}}"), "the Log does not open on its newest line");
  const walk = fn("function goPage(name){");
  assert.ok(/if\(name==="hearth"&&from!=="hearth"&&sectIsOpen\("saga"\)\)\{try\{sagaToTop\(\);\}catch\(_\)\{\}\}/.test(walk),
    "walking into the Dashboard with the Log open does not show its newest line");
  assert.ok(walk.indexOf("const from=currentPage;") > 0 && walk.indexOf("const from=currentPage;") < walk.indexOf("currentPage=name;"),
    "goPage reads where it came from after it has already moved");
});

/* ---------------------------------------------------------- 8. the stamp */
test("8. every row says its date and time; a host row without a day says the time alone", () => {
  const h = harness();
  h.run('logRaw("[23:59:59.120] before midnight", "Alpha", "2026-10-24")');
  h.run('logRaw("[00:00:01.004] after midnight", "Alpha", "2026-10-25")');
  h.run('logRaw("[12:00:01.000] from an older host", "Alpha")');
  h.run('logRaw("[12:00:02.000] null day", "Alpha", null)');
  h.run('logRaw("[12:00:03.000] odd day", "Alpha", "tomorrow")');
  const stamp = i => /<span class="t">([^<]*)<\/span>/.exec(h.term.children[i].innerHTML)[1];
  assert.strictEqual(stamp(4), "2026-10-24 23:59:59", "a host row does not show its own day and time");
  assert.strictEqual(stamp(3), "2026-10-25 00:00:01", "the line after midnight does not show the new day");
  for (const i of [2, 1, 0]) {
    assert.ok(/^\d\d:\d\d:\d\d$/.test(stamp(i)), "a row without a usable day shows " + JSON.stringify(stamp(i))
      + " (no undefined, no date the page made up)");
  }
  h.run('logLine("ok","[BakaLoader] start requested")');
  assert.ok(/^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d$/.test(stamp(0)), "a page row is not stamped whole: " + stamp(0));
  assert.strictEqual(h.run("sagaStamp(null,null,new Date(2026,9,24,7,5,3))"), "2026-10-24 07:05:03",
    "the page's own stamp is not YYYY-MM-DD HH:MM:SS in fixed digits");
  assert.deepStrictEqual(h.hearth.slice(0, 2), ["23:59:59", "00:00:01"], "the Hearth card lost the time, or took the whole stamp");
  /* a replayed row keeps the day it was written */
  h.pendingRows = [{ line: "[22:10:00.000] replayed from yesterday", profile: "Alpha", day: "2026-10-23" }];
  h.run("sagaReplayUnseen(pendingRows)");
  assert.strictEqual(stamp(0), "2026-10-23 22:10:00", "a replayed row was stamped without the day it was written");
  /* the seams: both live events and the boot replay hand the day on */
  assert.ok(/Native\.on\("log\.app",d=>\{[^\n]*logRaw\(d\.line,[^\n]*,d\.day\)/.test(SOURCE), "log.app drops the row's day");
  assert.ok(/Native\.on\("log\.server",d=>\{[\s\S]{0,120}logRaw\(d\.line,[^\n]*,d\.day\);/.test(SOURCE), "log.server drops the row's day");
  assert.ok(/logRaw\(r\.line,r\.profile==null\?null:r\.profile,r\.day\)/.test(SOURCE), "the boot replay drops the row's day");
  assert.ok(/logRaw\(row\.line,profile,row\.day\)/.test(fn("function sagaReplayUnseen(rows){")), "the replay drops the row's day");
});

/* ---------------------------------------------------------- 9. exceptions are errors */
test("9. exceptions, stack frames and BepInEx's own levels are classed as they are", () => {
  const h = harness();
  const kind = l => h.run("classifyLog(" + JSON.stringify(l) + ")").kind;
  const cases = [
    ["[12:00:00.000] NullReferenceException: Object reference not set", "err"],
    ["[12:00:00.002]     at Foo.Bar () [0x00000] in <abc>:0", "err"],
    ["[12:00:00.002] at UnityEngine.Object.Instantiate[T] (T original) [0x00001] in <x>:0", "err"],
    ["[12:00:00.003] Unhandled exception in the world thread", "err"],
    ["[12:00:00.003] FATAL could not open the world", "err"],
    ["[12:00:00.004] [Error  : Unity Log] something broke", "err"],
    ["[12:00:00.004] [Warning:   BepInEx] slow plugin", "warn"],
    ["[12:00:00.004] [Warning:   BepInEx] a plugin failed to load a dependency", "warn"],
    ["[12:00:00.004] [Warning:   BepInEx] InvalidOperationException in a patch", "err"],
    ["[12:00:00.005] [Info   :   BepInEx] Loading [ErrorHandler 1.0.0]", "info"],
    ["[12:00:00.005] at 12:00 (local) the gate opened", "info"],
    ["[12:00:00.005] Game server connected", "info"],
    ["[12:00:00.006] [WRN] a language pack is out of date", "warn"],
    ["[12:00:00.006] [ERR] the sweep failed", "err"],
    ["[12:00:00.007] Connections 1 ZDOS:123  sent:5 recv:6", "net"],
    ["[12:00:00.008] Steam game server failed to start", "err"],
  ];
  for (const [l, want] of cases) assert.strictEqual(kind(l), want, JSON.stringify(l) + " is classed " + kind(l));
  const frame = h.run('classifyLog("[12:00:00.002]     at Foo.Bar () [0x00000] in <abc>:0")');
  assert.strictEqual(frame.text, "    at Foo.Bar () [0x00000] in <abc>:0", "the stack frame lost its indentation to the stamp");
  assert.strictEqual(frame.time, "12:00:00");
  const plain = h.run('classifyLog("[12:00:00.123] Game server connected")');
  assert.strictEqual(plain.text, "Game server connected", "the space the host puts after the stamp is left in the text");
});

/* ---------------------------------------------------------- 10. the markup */
test("10. the command line stands over the lines, the chip floats over them, the view is the page's to hold", () => {
  const sect = HTML.slice(HTML.indexOf('id="sect-saga"'), HTML.indexOf("<!-- ============ PAGE: VIKINGS"));
  const at = s => { const i = sect.indexOf(s); assert.ok(i > 0, "the Log has no " + s); return i; };
  assert.ok(at('class="termhead"') < at('class="termin"') && at('class="termin"') < at('id="term"'),
    "the command line is not between the head and the lines");
  assert.ok(at('class="termpane"') < at('id="termNew"'), "the chip is not over the lines");
  assert.ok(sect.indexOf('id="termNew"') > sect.indexOf('id="term"'), "the chip is drawn under the lines it floats over");
  assert.ok(/<button type="button" class="fpill termhold" id="termHold" aria-pressed="false"/.test(sect), "the Pause button is missing or not a toggle");
  assert.ok(/data-i18n="saga\.hold\.pause">Pause<\/span><span class="th-live" data-i18n="saga\.hold\.live">Live</.test(sect),
    "the Pause button does not carry both of its words");
  /* the button is the state: a second "streaming" pill beside it said the same thing again */
  assert.ok(!/id="termStream"/.test(sect), "the streaming pill is back beside the Pause button");
  assert.ok(/\.termhead>\.fpill,\.termtools>:not\(\.termsearch\)\{flex-shrink:0\}/.test(CSS),
    "a pill in the head row can shrink below its own words; only the search box should give way");
  assert.ok(/\.term\{[^}]*overflow-anchor:none/.test(CSS), "the browser's own scroll anchoring would move a held view a second time");
  assert.ok(/\.termpane #termNew\.hidden\{opacity:0;visibility:hidden/.test(CSS), "the chip vanishes rather than fading");
  assert.ok(!/\.termhead \.hidden\{display:none\}/.test(CSS), "the chip's old display rule is back");
  /* the boot replay's divider stands over the rows it marks */
  const boot = SOURCE.slice(SOURCE.indexOf('const sbuf=await rpc("logs.serverBuffer");\n    if(sbuf!==FAIL'));
  assert.ok(boot.indexOf("sagaReplayUnseen(sbuf.slice(-200));") < boot.indexOf('logDivider(T("saga.divider.earlier"));'),
    "the boot replay writes its divider before its rows, so it ends up under them");
});

console.log("");
if (failures.length) {
  console.log("saga order selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("saga order selftest: " + passed + " passed");
