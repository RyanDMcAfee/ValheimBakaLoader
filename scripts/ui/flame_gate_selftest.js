/* The hearth fire's gate, its frame cap, and the idle pause on the endless CSS animations.
 *
 * WHY THIS EXISTS. 1.2.10 found the WebView2 renderer and gpu processes holding about a tenth
 * of a core each while BakaLoader sat behind the game. The fire's loop only asked two things,
 * which hall was showing and document.hidden, and document.hidden is false for a window that
 * is merely behind another one. So the window now says window.active, and three things hang
 * on it that no other test reads:
 *
 *   1. THE GATE. flameShouldRun() has to be true in exactly one of the eight corners of
 *      active/inactive x hidden/visible x Dashboard/another hall, and the window.active
 *      handler has to start and stop the loop from that answer.
 *   2. THE CAP. Thirty frames a second by timestamp, and the first frame after coming back to
 *      the front draws at once instead of waiting a frame or counting the parked time.
 *   3. THE CSS. Every rule in app.css that runs an infinite animation has a body.idle twin
 *      that pauses it, so a new endless animation added later fails here until it has one.
 *
 * Every function is the real one, lifted out of app.js and run. Prints one line per rule and
 * exits non zero on the first failure.
 *
 *     node scripts/ui/flame_gate_selftest.js [app.js] [app.css]
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const CSS = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.css");

const SOURCE = fs.readFileSync(APP, "utf8").replace(/\r\n/g, "\n");
const STYLE = fs.readFileSync(CSS, "utf8").replace(/\r\n/g, "\n");

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

/* A function that opens at the left margin and closes at the left margin. */
function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to) + "\n}";
}

/* A statement that closes with "});" at the left margin, like a Native.on block. */
function block(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n});", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to) + "\n});";
}

function line(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  return SOURCE.slice(from, SOURCE.indexOf("\n", from));
}

/* ------------------------------------------------------------------ the fake page */

function hearth() {
  const body = new Set();
  const handlers = {};
  const queued = [];
  let nextRaf = 0;
  const painted = { frames: 0 };
  const ctx = {
    clearRect() { painted.frames++; },
    createRadialGradient() { return { addColorStop() {} }; },
    fillRect() {}, beginPath() {}, arc() {}, fill() {},
    set fillStyle(v) {}, set globalAlpha(v) {}, set globalCompositeOperation(v) {},
    set shadowColor(v) {}, set shadowBlur(v) {},
  };
  const sandbox = {
    S: { windowActive: true },
    FLAME_READY: true,
    currentPage: "hearth",
    document: {
      hidden: false,
      body: { classList: {
        toggle(c, on) { if (on) body.add(c); else body.delete(c); return !!on; },
        contains: c => body.has(c),
      } },
    },
    performance: { now: () => sandbox.clock },
    clock: 1000,
    requestAnimationFrame(cb) { const id = ++nextRaf; queued.push({ id, cb }); return id; },
    cancelAnimationFrame(id) { const i = queued.findIndex(q => q.id === id); if (i >= 0) queued.splice(i, 1); },
    Native: { on(event, cb) { handlers[event] = cb; } },
    Math,
  };
  vm.createContext(sandbox);
  const code = [
    "var " + line("const FLAME_VW=").slice("const ".length),
    line("const FLAME={"),
    line("const FLAME_FRAME_MS="),
    line("const flameNow="),
    "const FLAME_PAL={burning:{rate:34,lift:[1.1,2.2],life:[38,70],size:[3,7],wobble:.9,colors:['#fff'],glow:'rgba(0,0,0,0)',smoke:0,ember:1}};",
    fn("function flamePal(){"),
    fn("function flameSpawn(){"),
    fn("function flameStep(s){"),
    fn("function flameFrame(ts){"),
    fn("function flameShouldRun(){"),
    fn("function flameTick(){"),
    block('Native.on("window.active",d=>{'),
    "FLAME.state='burning'; FLAME.ctx=ctx; FLAME.cv={isConnected:true};",
    "this.FLAME=FLAME; this.flameShouldRun=flameShouldRun; this.flameTick=flameTick; this.flameFrame=flameFrame;",
  ].join("\n");
  sandbox.ctx = ctx;
  vm.runInContext(code, sandbox);
  /* Run the one animation frame the page asked for, at a given timestamp. */
  sandbox.frame = ts => {
    const q = queued.shift();
    assert.ok(q, "no animation frame was asked for");
    sandbox.clock = ts;
    q.cb(ts);
  };
  sandbox.queued = queued;
  sandbox.painted = painted;
  sandbox.handlers = handlers;
  sandbox.body = body;
  return sandbox;
}

/* ------------------------------------------------------------------ 1. the gate */

test("flameShouldRun is true in exactly one corner of active x hidden x page", () => {
  const h = hearth();
  const rows = [];
  for (const active of [true, false])
    for (const hidden of [false, true])
      for (const page of ["hearth", "world"]) {
        h.S.windowActive = active;
        h.document.hidden = hidden;
        h.currentPage = page;
        const run = h.flameShouldRun();
        assert.strictEqual(typeof run, "boolean", "the gate answered " + JSON.stringify(run) + ", not a boolean");
        rows.push((active ? "active  " : "inactive") + " " + (hidden ? "hidden " : "visible") + " " + page + " -> " + run);
        assert.strictEqual(run, active && !hidden && page === "hearth",
          "wrong answer for active=" + active + " hidden=" + hidden + " page=" + page);
      }
  rows.forEach(r => console.log("         " + r));
});

test("reduced motion and a detached canvas still keep the fire parked while active", () => {
  const h = hearth();
  h.FLAME.reduced = true;
  assert.strictEqual(h.flameShouldRun(), false);
  h.FLAME.reduced = false;
  h.FLAME.cv.isConnected = false;
  assert.strictEqual(h.flameShouldRun(), false);
});

test("window.active false parks the loop, sets body.idle, and true brings both back", () => {
  const h = hearth();
  h.flameTick();
  assert.strictEqual(h.queued.length, 1, "an active Dashboard did not start the loop");
  h.handlers["window.active"]({ active: false });
  assert.strictEqual(h.S.windowActive, false);
  assert.ok(h.body.has("idle"), "body.idle was not set behind another window");
  assert.strictEqual(h.queued.length, 0, "the loop kept its animation frame behind another window");
  assert.strictEqual(h.FLAME.raf, 0);
  h.handlers["window.active"]({ active: true });
  assert.strictEqual(h.S.windowActive, true);
  assert.ok(!h.body.has("idle"), "body.idle stayed on in front");
  assert.strictEqual(h.queued.length, 1, "coming back to the front did not restart the loop");
});

test("a window.active with no payload or a missing field reads as in front", () => {
  const h = hearth();
  h.handlers["window.active"](null);
  assert.strictEqual(h.S.windowActive, true);
  h.handlers["window.active"]({});
  assert.strictEqual(h.S.windowActive, true);
});

/* ------------------------------------------------------------------ 2. the cap */

test("frames closer than a thirtieth of a second apart are skipped, not painted", () => {
  const h = hearth();
  h.flameTick();                       // draws at once, stamps clock 1000
  const first = h.painted.frames;
  assert.strictEqual(first, 1, "activation did not paint at once");
  let ts = 1000;
  for (let i = 0; i < 60; i++) { ts += 1000 / 60; h.frame(ts); }   // one second of 60 Hz vsync
  const drawn = h.painted.frames - first;
  console.log("         60 vsyncs in one second painted " + drawn + " frames");
  assert.ok(drawn >= 29 && drawn <= 30, "painted " + drawn + " frames in a second, want about 30");
});

test("the simulation keeps its 60 a second pace under the cap", () => {
  const h = hearth();
  h.flameTick();
  const t0 = h.FLAME.t;
  let ts = 1000;
  for (let i = 0; i < 60; i++) { ts += 1000 / 60; h.frame(ts); }
  const steps = h.FLAME.t - t0;
  assert.ok(steps >= 58 && steps <= 61, "the fire stepped " + steps + " times in a second, want about 60");
});

test("the first frame after activation draws at once and does not catch up the parked time", () => {
  const h = hearth();
  h.flameTick();
  h.frame(1000 + 34);
  h.handlers["window.active"]({ active: false });
  const before = h.painted.frames, t0 = h.FLAME.t;
  h.clock = 60000;                     // a minute behind the game
  h.handlers["window.active"]({ active: true });
  assert.strictEqual(h.painted.frames, before + 1, "activation did not paint a frame at once");
  assert.strictEqual(h.FLAME.t - t0, 1, "the parked minute was stepped through on the way back");
  h.frame(60000 + 1000 / 60);          // the very next vsync is too soon
  assert.strictEqual(h.painted.frames, before + 1, "the vsync right after the redraw painted again");
  h.frame(60000 + 2000 / 60);
  assert.strictEqual(h.painted.frames, before + 2);
});

test("a long gap while running steps at most three times, never a burst", () => {
  const h = hearth();
  h.flameTick();
  const t0 = h.FLAME.t;
  h.frame(1000 + 5000);
  assert.strictEqual(h.FLAME.t - t0, 3);
});

/* ------------------------------------------------------------------ 3. the css */

test("every infinite animation in app.css has a body.idle rule that pauses it", () => {
  const noComments = STYLE.replace(/\/\*[\s\S]*?\*\//g, "");
  const rules = [...noComments.matchAll(/([^{}]+)\{([^{}]*)\}/g)];
  const idle = rules.filter(r => /animation-play-state\s*:\s*paused/.test(r[2]))
    .flatMap(r => r[1].split(",").map(s => s.trim().replace(/\s+/g, " ")))
    .filter(s => s.startsWith("body.idle "));
  assert.ok(idle.length > 0, "app.css holds no body.idle pause rule");
  const paused = new Set(idle.map(s => s.slice("body.idle ".length)));
  const names = new Set();
  const missing = [];
  for (const r of rules) {
    const anim = /(?:^|;)\s*animation\s*:([^;]*)/.exec(r[2]);
    if (!anim || !/\binfinite\b/.test(anim[1])) continue;
    const name = /^\s*([A-Za-z][\w-]*)/.exec(anim[1]);
    if (name) names.add(name[1]);
    for (const sel of r[1].split(",").map(s => s.trim().replace(/\s+/g, " "))) {
      if (sel.startsWith("@")) continue;
      if (!paused.has(sel)) missing.push(sel);
    }
  }
  console.log("         paused: " + [...names].sort().join(", "));
  assert.deepStrictEqual(missing, [], "endless animations with no body.idle pause");
  for (const want of ["emberW", "lmSweep", "hbsweep", "blink", "saveCfgPulse"])
    assert.ok(names.has(want), want + " is no longer an infinite animation the gate saw");
});

console.log("");
console.log(passed + " passed, " + failures.length + " failed");
process.exit(failures.length ? 1 : 0);
