/* Stop when empty on the Settings hall (issue 28), driven with stand-in controls.
 *
 * WHY THIS EXISTS. 1.3.1 puts two switches under Restart when empty in Advanced: Stop when
 * empty, and the Windows half of it. Three things about them can go wrong without a build or a
 * C# test noticing. The two empty-server switches can both end up on, which the host side
 * settles by letting the stop win, so the page would show a restart that is never going to
 * happen. The Windows switch can be pressed while the stop is off and look like it means
 * something. And the save can leave a key out, which the host side reads as false.
 *
 * THE CODE UNDER TEST is the EMPTY-TIMERS block of app.js (syncAdvGates, updAdvLabels, the
 * two exclusive listeners, the capture-phase block on the Windows row, renderEmptyTimers and
 * emptyTimerPrefs), cut out by its markers and run here with stand-in controls. A press is
 * what the page does: the shared [data-t] handler flips the class and runs syncAdvGates, then
 * the listeners the block registered on that switch run in the order they were added.
 *
 * The rules:
 *   1. Restart on, then Stop on: Restart goes off. Stop on, then Restart on: Stop goes off.
 *      Turning one off leaves the other alone.
 *   2. The delay box sleeps when both switches are off and wakes for either one.
 *   3. The Windows row sleeps (row and field gated, aria-disabled) while Stop is off, keeps
 *      its stored value, and its press is refused in the capture phase; with Stop on it is
 *      awake and the press goes through.
 *   4. The save sends the three switches as booleans, exactly as they stand, and the save
 *      handler spreads them into the profile beside the delay.
 *   5. The load sets the three switches from the profile before the gates are worked out.
 *   6. Both empty-server labels are rebuilt from the box through revealText, with the number
 *      handed in, so the plural category comes from the value: 1 is "one", 7 is "other", and
 *      an empty box reads as 5.
 *   7. The markup: the two rows sit after the delay box and before Scheduled Restart, the
 *      number label carries no data-i18n, the static English matches the catalog, and both
 *      switches are in the unsaved-changes list.
 *   8. The exclusive listeners are registered after the shared [data-t] handler, or they would
 *      read the class the press is leaving.
 *   9. The catalog carries the four keys in English, and the pseudo catalog carries them too.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/empty_stop_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const vm = require("vm");
const assert = require("assert");

const ROOT = path.resolve(__dirname, "..", "..");
const WEB = path.join(ROOT, "ValheimBakaLoader", "WebUI");
const SRC = fs.readFileSync(path.join(WEB, "app.js"), "utf8");
const HTML = fs.readFileSync(path.join(WEB, "index.html"), "utf8").replace(/\r\n/g, "\n");
const EN = JSON.parse(fs.readFileSync(path.join(WEB, "i18n", "en.json"), "utf8")).keys;
const XX = JSON.parse(fs.readFileSync(path.join(WEB, "i18n", "xx.json"), "utf8")).keys;

/* The en dash and the em dash, by code so this file carries neither. */
const DASHES = new RegExp("[" + String.fromCharCode(0x2013, 0x2014) + "]");

const BEGIN = "/* EMPTY-TIMERS-BEGIN */";
const END = "/* EMPTY-TIMERS-END */";
const start = SRC.indexOf(BEGIN);
const end = SRC.indexOf(END);
assert.ok(start > 0 && end > start, "the EMPTY-TIMERS block is missing from app.js");
const BLOCK = SRC.slice(start, end + END.length);

/* ---------------------------------------------------------------- stand-ins */

function classes() {
  const set = new Set();
  return {
    set,
    contains: c => set.has(c),
    add: c => set.add(c),
    remove: c => set.delete(c),
    toggle(c, on) {
      const want = on === undefined ? !set.has(c) : !!on;
      if (want) set.add(c); else set.delete(c);
      return want;
    },
  };
}

function node(id, parents) {
  const listeners = [];
  const attrs = {};
  return {
    id,
    value: "",
    disabled: false,
    textContent: "",
    classList: classes(),
    listeners,
    attrs,
    setAttribute(k, v) { attrs[k] = String(v); },
    getAttribute(k) { return k in attrs ? attrs[k] : null; },
    addEventListener(type, fn, capture) { listeners.push({ type, fn, capture: !!capture }); },
    closest(sel) { return (parents || {})[sel] || null; },
  };
}

/* One page: every control the block touches, each with its row and field. */
function page(opts) {
  const els = {};
  const sw = (id) => {
    const row = node(id + "Row"), field = node(id + "Field");
    const el = node(id, { ".togglerow": row, ".field": field });
    els[id] = el; els[id + "Row"] = row; els[id + "Field"] = field;
    return el;
  };
  const box = (id, value) => {
    const field = node(id + "Field");
    const el = node(id, { ".field": field });
    el.value = value;
    els[id] = el; els[id + "Field"] = field;
    return el;
  };
  ["tEmpty", "tEmptyStop", "tEmptyStopWin", "tSched", "tCrash", "tRcon"].forEach(sw);
  box("fEmptyDelay", (opts && opts.minutes) != null ? opts.minutes : "5");
  box("fSchedHours", "6"); box("fCrashDelay", "10"); box("fRconPort", "25575"); box("fRconPw", "");
  ["tEmptyLbl", "tEmptyStopLbl", "tSchedLbl", "tRconLbl"].forEach(id => { els[id] = node(id); });

  const reveals = [];
  const asked = [];
  const plural = new Intl.PluralRules("en");
  const T = (key, params) => {
    asked.push({ key, params });
    const entry = EN[key];
    if (!entry) return key;
    let text = entry.lore;
    if (text && typeof text === "object") text = text[plural.select(Number(params && params[entry.plural]))] || text.other;
    return String(text).replace(/\{(\w+)\}/g, (_, k) => (params && k in params ? params[k] : "{" + k + "}"));
  };
  const ctx = {
    $: sel => els[sel.replace(/^#/, "")] || null,
    T,
    revealText(el, text) { reveals.push({ id: el && el.id, text }); if (el) el.textContent = String(text); },
    Number, String, Intl,
  };
  ctx.swOn = id => ctx.$("#" + id).classList.contains("on");
  ctx.setT = (id, on) => { const el = ctx.$("#" + id); el.classList.toggle("on", !!on); return !!on; };
  vm.createContext(ctx);
  vm.runInContext(BLOCK, ctx);
  const run = code => vm.runInContext(code, ctx);

  /* A press as the page makes it: capture listeners on the row first (a stop there ends it),
     then the shared handler flips the class and syncs the gates, then the switch's own. */
  function press(id) {
    const el = els[id], row = els[id + "Row"];
    let stopped = false;
    const event = { target: el, stopPropagation() { stopped = true; }, preventDefault() {} };
    for (const l of row.listeners) if (l.type === "click" && l.capture) l.fn(event);
    if (stopped) return false;
    el.classList.toggle("on");
    run("syncAdvGates()");
    for (const l of el.listeners) if (l.type === "click") l.fn(event);
    return true;
  }
  return { els, run, press, reveals, asked, on: id => els[id].classList.contains("on") };
}

let failed = 0, count = 0;
function rule(n, text, fn) {
  count++;
  try { fn(); console.log(`  ok   ${n}. ${text}`); }
  catch (e) { failed++; console.log(`  FAIL ${n}. ${text}\n       ${e.message}`); }
}

/* ---------------------------------------------------------------- rules */

rule(1, "turning either empty-server switch on turns the other off, and off leaves it alone", () => {
  const p = page();
  p.run("syncAdvGates()");
  p.press("tEmpty");
  assert.ok(p.on("tEmpty") && !p.on("tEmptyStop"));
  p.press("tEmptyStop");
  assert.ok(p.on("tEmptyStop"), "the stop did not go on");
  assert.ok(!p.on("tEmpty"), "Restart stayed on beside Stop");
  p.press("tEmpty");
  assert.ok(p.on("tEmpty"), "Restart did not go on");
  assert.ok(!p.on("tEmptyStop"), "Stop stayed on beside Restart");
  p.press("tEmpty");
  assert.ok(!p.on("tEmpty") && !p.on("tEmptyStop"), "turning Restart off moved Stop");
});

rule(2, "the delay box sleeps with both switches off and wakes for either", () => {
  const p = page();
  p.run("syncAdvGates()");
  const box = p.els.fEmptyDelay, field = p.els.fEmptyDelayField;
  assert.ok(box.disabled && field.classList.contains("gated"), "awake with both off");
  p.press("tEmpty");
  assert.ok(!box.disabled && !field.classList.contains("gated"), "asleep with Restart on");
  p.press("tEmptyStop");
  assert.ok(!box.disabled && !field.classList.contains("gated"), "asleep with Stop on");
  p.press("tEmptyStop");
  assert.ok(box.disabled && field.classList.contains("gated"), "awake after both went off");
});

rule(3, "the Windows row sleeps with Stop off, keeps its value, and refuses the press", () => {
  const p = page();
  p.run("renderEmptyTimers({EmptyServerRestart:false,EmptyServerStop:false,EmptyServerStopShutsDownWindows:true}); syncAdvGates();");
  assert.ok(p.els.tEmptyStopWinRow.classList.contains("gated"), "row awake with Stop off");
  assert.ok(p.els.tEmptyStopWinField.classList.contains("gated"), "field awake with Stop off");
  assert.strictEqual(p.els.tEmptyStopWin.getAttribute("aria-disabled"), "true");
  assert.ok(p.on("tEmptyStopWin"), "the stored value was dropped by the gate");
  assert.strictEqual(p.press("tEmptyStopWin"), false, "a press on the sleeping switch went through");
  assert.ok(p.on("tEmptyStopWin"), "a refused press moved the switch");

  p.press("tEmptyStop");
  assert.ok(!p.els.tEmptyStopWinRow.classList.contains("gated"), "row asleep with Stop on");
  assert.strictEqual(p.els.tEmptyStopWin.getAttribute("aria-disabled"), "false");
  assert.strictEqual(p.press("tEmptyStopWin"), true, "a press on the awake switch was refused");
  assert.ok(!p.on("tEmptyStopWin"));
});

rule(4, "the save sends all three switches as they stand, spread beside the delay", () => {
  for (const [r, s, w] of [[false, false, false], [true, false, false], [false, true, false], [false, true, true], [false, false, true]]) {
    const p = page();
    p.run(`renderEmptyTimers({EmptyServerRestart:${r},EmptyServerStop:${s},EmptyServerStopShutsDownWindows:${w}})`);
    const sent = JSON.parse(p.run("JSON.stringify(emptyTimerPrefs())"));
    assert.deepStrictEqual(sent, { EmptyServerRestart: r, EmptyServerStop: s, EmptyServerStopShutsDownWindows: w });
  }
  /* The Save Config payload: the one profiles.save whose object is built from the form,
     which is the one that reads the delay box. */
  const box = SRC.indexOf('EmptyServerRestartDelayMinutes:num("fEmptyDelay"');
  assert.ok(box > 0, "the save no longer reads the delay box");
  const save = SRC.indexOf('const r=await rpc("profiles.save",{name,prefs});', box);
  const from = SRC.lastIndexOf("const prefs={...cur,", box);
  assert.ok(from > 0 && save > box, "the profiles.save call moved");
  const payload = SRC.slice(from, save);
  assert.ok(payload.includes("...emptyTimerPrefs(), EmptyServerRestartDelayMinutes:num(\"fEmptyDelay\""),
    "the save payload does not spread the three switches beside the delay");
  assert.ok(!/EmptyServerRestart:swOn/.test(SRC.replace(BLOCK, "")), "a second writer of EmptyServerRestart outside the block");
});

rule(5, "the load sets the three switches before the gates are worked out", () => {
  const p = page();
  p.run("renderEmptyTimers({EmptyServerRestart:false,EmptyServerStop:true,EmptyServerStopShutsDownWindows:true}); syncAdvGates();");
  assert.ok(!p.on("tEmpty") && p.on("tEmptyStop") && p.on("tEmptyStopWin"));
  const form = SRC.slice(SRC.indexOf("function renderWorldForm(){"));
  const load = form.indexOf("renderEmptyTimers(p);");
  const gates = form.indexOf("syncAdvGates();");
  assert.ok(load > 0 && gates > load, "renderWorldForm does not load the switches before syncAdvGates");
});

rule(6, "both empty-server labels come from the box through revealText, plural from the value", () => {
  for (const [minutes, category] of [["1", "one"], ["7", "other"], ["", "other"]]) {
    const p = page({ minutes });
    p.run("updAdvLabels()");
    const shown = minutes || "5";
    const want = n => EN[n].lore[category].replace("{minutes}", shown);
    const stop = p.reveals.find(r => r.id === "tEmptyStopLbl");
    const restart = p.reveals.find(r => r.id === "tEmptyLbl");
    assert.ok(stop && restart, "a label was written without revealText");
    assert.strictEqual(stop.text, want("world.empty_stop.label"));
    assert.strictEqual(restart.text, want("world.empty.label"));
    const ask = p.asked.find(a => a.key === "world.empty_stop.label");
    assert.strictEqual(JSON.stringify(ask.params), JSON.stringify({ minutes: shown }));
  }
  assert.strictEqual(EN["world.empty_stop.label"].plural, "minutes");
  assert.deepStrictEqual(Object.keys(EN["world.empty_stop.label"].lore).sort(), ["one", "other"]);
});

rule(7, "the markup: two rows after the delay box, static English matches, both in the unsaved list", () => {
  const delay = HTML.indexOf('id="fEmptyDelay"');
  const stop = HTML.indexOf('id="tEmptyStop"');
  const win = HTML.indexOf('id="tEmptyStopWin"');
  const sched = HTML.indexOf('id="tSched"');
  assert.ok(delay > 0 && delay < stop && stop < win && win < sched, "the rows are not between the delay box and Scheduled Restart");
  assert.ok(HTML.includes('<span class="tl" id="tEmptyStopLbl">Stop when empty for 5 min</span>'), "the number label is keyed or its English moved");
  assert.ok(HTML.includes('data-i18n="world.empty_stop_windows.label">' + EN["world.empty_stop_windows.label"].lore + "<"));
  assert.ok(HTML.includes('data-i18n="world.field.empty_stop">' + EN["world.field.empty_stop"].lore + "<"));
  assert.ok(HTML.includes('data-i18n="world.field.empty_stop_windows">' + EN["world.field.empty_stop_windows"].lore + "<"));
  const list = SRC.slice(SRC.indexOf("const WORLD_FORM_TOGGLES="), SRC.indexOf("];", SRC.indexOf("const WORLD_FORM_TOGGLES=")));
  assert.ok(list.includes('"tEmptyStop"') && list.includes('"tEmptyStopWin"'), "a switch is missing from the unsaved list");
});

rule(8, "the exclusive listeners come after the shared [data-t] handler", () => {
  const shared = SRC.indexOf('$$("[data-t]").forEach(t=>{');
  const mine = SRC.indexOf('$("#tEmpty")?.addEventListener("click"');
  const theirs = SRC.indexOf('$("#tEmptyStop")?.addEventListener("click"');
  assert.ok(shared > 0, "the shared handler moved");
  assert.ok(mine > shared && theirs > shared, "an exclusive listener is registered before the shared handler");
});

rule(9, "the catalog carries the four keys, and so does the pseudo catalog", () => {
  assert.strictEqual(EN["world.field.empty_stop"].lore, "Empty-Server Stop");
  assert.strictEqual(EN["world.field.empty_stop_windows"].lore, "After the Stop");
  assert.strictEqual(EN["world.empty_stop_windows.label"].lore, "Shut Windows down after the stop");
  assert.strictEqual(EN["world.empty_stop.label"].lore.other, "Stop when empty for {minutes} min");
  for (const k of ["world.field.empty_stop", "world.field.empty_stop_windows", "world.empty_stop_windows.label", "world.empty_stop.label"]) {
    assert.ok(XX[k], "xx.json has no " + k + ": run scripts/i18n/make_pseudo.py");
  }
  for (const k of ["world.field.empty_stop", "world.field.empty_stop_windows", "world.empty_stop_windows.label", "world.empty_stop.label"]) {
    assert.ok(!DASHES.test(JSON.stringify(EN[k])), k + " carries a dash");
  }
});

console.log(failed ? `empty stop selftest: ${failed} of ${count} rule(s) FAILED` : `empty stop selftest: ${count} rules pass`);
process.exit(failed ? 1 : 0);
