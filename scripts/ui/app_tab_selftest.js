/* The Settings hall's tab strip, its App tab, the Connection card's fold, and the palette rows
 * that land on the moved controls.
 *
 * WHY THIS EXISTS. 1.2.6 moved nine rows off the Dashboard's Upkeep card: three connection
 * switches and a test onto a Connection card of their own, and five preferences onto a new App
 * tab in the Settings hall. Everything that moved kept its id, so every existing test went on
 * passing, and the three things the regroup actually changed had no test at all:
 *
 *   1. WHICH TAB IS SHOWING. worldTab() moves the two panels, the pressed state, aria-selected,
 *      the Save Config button and the unsaved band. The band promised "Save Config writes them"
 *      on a tab whose Save Config is hidden and whose every row writes itself as it is moved,
 *      and it went on standing there through 1.2.6's first cut.
 *   2. THE CONNECTION CARD'S FOLD, which is the only reason the Dashboard did not grow six rows
 *      taller when those switches moved out of Upkeep.
 *   3. THE PALETTE STILL LANDING ON THEM. That was the regroup's promise, openAppTab was written
 *      for it, and openAppTab had no caller anywhere in the file.
 *
 * Plus two rules about the markup the same move broke: a card head that carried the Hearth's
 * Norse word into the Settings hall, and three dropdowns the platform drew because they sit in a
 * .togglerow rather than a .field.
 *
 * Every painter is the real one, lifted out of app.js and run. Prints one line per rule and
 * exits non zero on the first failure.
 *
 *     node scripts/ui/app_tab_selftest.js [app.js] [index.html] [app.css] [en.json]
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const PAGE = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "index.html");
const CSS = process.argv[4] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.css");
const CATALOG = process.argv[5] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");

const SOURCE = fs.readFileSync(APP, "utf8").replace(/\r\n/g, "\n");
const HTML = fs.readFileSync(PAGE, "utf8").replace(/\r\n/g, "\n");
const STYLE = fs.readFileSync(CSS, "utf8").replace(/\r\n/g, "\n");
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

function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to) + "\n}";
}

function fn1(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  return SOURCE.slice(from, SOURCE.indexOf("\n", from));
}

/* ------------------------------------------------------------------ the fake hall */

function element(id, classes) {
  const held = new Set(classes || []);
  const el = {
    id,
    style: { display: "" },
    dataset: {},
    attrs: {},
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
    setAttribute(name, value) { el.attrs[name] = String(value); },
    getAttribute(name) {
      if (name === "data-wtab") return el.attrs["data-wtab"] || null;
      return Object.prototype.hasOwnProperty.call(el.attrs, name) ? el.attrs[name] : null;
    },
    scrollIntoView() { el.scrolled = (el.scrolled || 0) + 1; },
    held,
  };
  return el;
}

function hall() {
  const server = element("worldTabServer");
  const app = element("worldTabApp");
  const save = element("saveCfg", ["savecfg"]);
  const band = element("worldUnsaved", []);
  const page = element("page-world", ["page", "active"]);
  page.attrs["data-wtab"] = "server";
  const tabServer = element("wtabServer", ["wtab", "on"]);
  const tabApp = element("wtabApp", ["wtab"]);
  tabServer.attrs["data-wtab"] = "server";
  tabApp.attrs["data-wtab"] = "app";

  const byId = {
    "#worldTabServer": server, "#worldTabApp": app, "#worldUnsaved": band,
    "#page-world": page, "#selTextSize": element("selTextSize"),
    "#selAppLang": element("selAppLang"), "#tStartWin": element("tStartWin"),
  };
  const opened = [];

  const context = {
    Object, Array, JSON, Map, Set, String, Number, console,
    requestAnimationFrame: run => run(),
    $: selector => byId[selector] || null,
    $$: selector => (selector === ".wtab" ? [tabServer, tabApp] : []),
    document: {
      querySelector: selector => (selector === "#page-world .savecfg" ? save : byId[selector] || null),
    },
    refreshStartWinNote() { context.asked.push("startWinNote"); },
    langRefresh() { context.asked.push("langRefresh"); },
    refreshScrollCues() {},
    goPage(name) { opened.push("page:" + name); },
    asked: [],
    opened,
    parts: { server, app, save, band, page, tabServer, tabApp },
  };
  vm.createContext(context);
  vm.runInContext(fn1("let WORLD_TAB="), context, { filename: "app.js#tab" });
  vm.runInContext(fn("function worldTab(name){"), context, { filename: "app.js#worldTab" });
  vm.runInContext(fn("function openAppTab(focusId){"), context, { filename: "app.js#openAppTab" });
  return context;
}

/* ------------------------------------------------------ rule 1: which tab is showing */

test("the App tab shows its own panel and takes the pressed state with it", () => {
  const h = hall();
  vm.runInContext('worldTab("app")', h);

  assert.strictEqual(h.parts.app.style.display, "block", "the App tab's panel is not showing");
  assert.strictEqual(h.parts.server.style.display, "none", "the Server tab's panel is still showing");
  assert.ok(h.parts.tabApp.classList.contains("on"), "the App tab does not read as pressed");
  assert.ok(!h.parts.tabServer.classList.contains("on"), "the Server tab still reads as pressed");
  assert.strictEqual(h.parts.tabApp.getAttribute("aria-selected"), "true");
  assert.strictEqual(h.parts.tabServer.getAttribute("aria-selected"), "false");
});

test("Save Config is hidden on the App tab and back on the Server tab", () => {
  const h = hall();
  vm.runInContext('worldTab("app")', h);
  assert.strictEqual(h.parts.save.style.display, "none",
    "Save Config is offered on a tab where every row saves itself and there is nothing to write");
  vm.runInContext('worldTab("server")', h);
  assert.strictEqual(h.parts.save.style.display, "", "Save Config did not come back on the Server tab");
});

test("the unsaved band goes with it, and the edits do not", () => {
  const h = hall();
  // Something really is unsaved: the band is up.
  h.parts.band.classList.add("on");
  vm.runInContext('worldTab("server")', h);
  assert.strictEqual(h.parts.page.dataset.wtab, "server");
  assert.ok(h.parts.page.classList.contains("unsaved-room"),
    "the hall gave up no room for a band that is up");

  vm.runInContext('worldTab("app")', h);
  assert.strictEqual(h.parts.page.dataset.wtab, "app",
    "the hall does not say which tab is showing, so the stylesheet cannot keep the band off it");
  assert.ok(!h.parts.page.classList.contains("unsaved-room"),
    "the App tab still gives up a band's worth of room at its foot, for a band that is not there");
  assert.ok(h.parts.band.classList.contains("on"),
    "the edits were dropped: the band promises they are kept until Save Config, on either tab");

  vm.runInContext('worldTab("server")', h);
  assert.ok(h.parts.page.classList.contains("unsaved-room"), "the band's room did not come back");
  assert.ok(h.parts.band.classList.contains("on"), "the band did not come back");
});

test("and the stylesheet is what keeps it off the App tab", () => {
  assert.ok(STYLE.indexOf('#page-world.active[data-wtab="app"] ~ .worldunsaved.on{display:none}') > 0,
    "app.css has no rule keeping the unsaved band off the App tab");
  // and it has to outweigh the rule that shows it
  const shows = STYLE.indexOf("#page-world.active ~ .worldunsaved.on{display:flex");
  const hides = STYLE.indexOf('#page-world.active[data-wtab="app"] ~ .worldunsaved.on');
  assert.ok(shows >= 0 && hides > shows, "the hiding rule does not come after the showing one");
});

test("the App tab asks for the two things only it can answer", () => {
  const h = hall();
  vm.runInContext('worldTab("app")', h);
  assert.ok(h.asked.indexOf("startWinNote") >= 0,
    "the App tab does not re-read the Windows startup entry, which moves outside this window");
  assert.ok(h.asked.indexOf("langRefresh") >= 0,
    "the App tab does not fill its two language lists");

  const s = hall();
  vm.runInContext('worldTab("server")', s);
  assert.strictEqual(s.asked.length, 0, "the Server tab asked for the App tab's own reads");
});

/* --------------------------------------- rule 3: the palette lands on the moved controls */

test("the palette has a row for each of the three moved controls", () => {
  for (const [name, id] of [["text_size", "pal.cmd.text_size"],
    ["interface_language", "pal.cmd.interface_language"],
    ["start_with_windows", "pal.cmd.start_with_windows"]]) {
    assert.ok(HTML.indexOf('data-cmd="' + name + '"') > 0,
      "the palette has no row for " + name);
    assert.ok(HTML.indexOf('data-i18n="' + id + '"') > 0, "the row for " + name + " names no sentence");
    assert.ok(KEYS[id], id + " is not in the English catalog");
  }
});

test("each of those rows goes through openAppTab and scrolls its own control up", () => {
  const dispatch = SOURCE.slice(SOURCE.indexOf("function invokePal(){"),
    SOURCE.indexOf('$("#cmdchip").addEventListener'));
  for (const [name, focus] of [["text_size", "selTextSize"],
    ["interface_language", "selAppLang"], ["start_with_windows", "tStartWin"]]) {
    assert.ok(dispatch.indexOf('sel.dataset.cmd==="' + name + '"') > 0,
      "the palette never dispatches " + name);
    assert.ok(dispatch.indexOf('openAppTab("' + focus + '")') > 0,
      name + " does not land on " + focus);
  }
});

test("openAppTab really opens the tab and scrolls what it was asked for", () => {
  const h = hall();
  vm.runInContext('openAppTab("selTextSize")', h);
  assert.ok(h.opened.indexOf("page:world") >= 0, "openAppTab does not walk to the Settings hall");
  assert.strictEqual(h.parts.app.style.display, "block", "openAppTab did not open the App tab");
  assert.strictEqual(vm.runInContext('$("#selTextSize").scrolled', h), 1,
    "openAppTab scrolled the tab rather than the control the palette named");

  const bare = hall();
  vm.runInContext("openAppTab()", bare);
  assert.strictEqual(bare.parts.app.scrolled, 1, "openAppTab with no control named scrolled nothing");
});

/* ------------------------------------------------ rule 2: the Connection card's own fold */

test("the Connection card is a fold, and it is wired like the Upkeep card", () => {
  assert.ok(HTML.indexOf('id="connHead"') > 0, "index.html has no Connection card head");
  assert.ok(HTML.indexOf('id="connBody"') > 0, "index.html has no Connection card body");
  assert.ok(SOURCE.indexOf('wireCollapsible("connHead",$("#connBody"),$("#connCard"))') > 0,
    "the Connection card head is not wired as a collapsible, so it cannot be opened or closed");

  // Folded to start with, which is the whole reason the Dashboard did not grow taller.
  const card = HTML.slice(HTML.indexOf('id="connCard"') - 200, HTML.indexOf('id="connCard"') + 200);
  assert.ok(card.indexOf("open") < 0 || card.indexOf('class="card b-conn"') >= 0,
    "the Connection card arrives open, so the Dashboard is taller than it was: " + card);
});

test("the fold answers a keyboard as well as a pointer", () => {
  const wire = fn("function wireCollapsible(headId,body,targetEl){");
  assert.ok(wire.indexOf('head.addEventListener("click",toggle)') > 0, "the head answers no click");
  assert.ok(/head\.addEventListener\("keydown"/.test(wire), "the head answers no key");
  assert.ok(/e\.key==="Enter"\|\|e\.key===" "/.test(wire), "Enter and Space do not open it");
});

/* ------------------------------------------- the markup the same move got wrong */

test("the Settings hall carries no Norse caption belonging to the Dashboard", () => {
  const from = HTML.indexOf('<section class="page" id="page-world"');
  const to = HTML.indexOf('<!-- ============ PAGE: ATLAS', from);
  assert.ok(from > 0 && to > from, "index.html no longer holds the Settings hall");
  const hallHtml = HTML.slice(from, to);
  assert.ok(hallHtml.indexOf("common.norse.hearth") < 0,
    "the App tab's card head still carries the Hearth's own Norse word, which names the wrong hall");
  assert.ok(hallHtml.indexOf('data-i18n="app.sec.window"') > 0,
    "the App tab has lost its heading altogether");
});

test("every select in the window is drawn in the app's own face", () => {
  // The styled selectors, read out of app.css rather than listed here.
  const styled = [];
  for (const m of STYLE.matchAll(/^([^{}@\n][^{}]*)\{([^{}]*appearance\s*:\s*none[^{}]*)\}/gm))
    for (const one of m[1].split(","))
      if (/select\s*$/.test(one.trim())) styled.push(one.trim());
  assert.ok(styled.length >= 1, "app.css styles no select at all any more");

  const ids = [...HTML.matchAll(/<select[^>]*\bid="([^"]+)"/g)].map(m => m[1]);
  assert.ok(ids.length >= 8, "found only " + ids.length + " selects in index.html");

  // EVERY enclosing container, not just the nearest one: #fWorld sits in a .pathrow inside a
  // .field, and `.field select` is a descendant selector that reaches it. A reader that stopped
  // at the closest class called a styled control unstyled.
  const KNOWN = ["field", "togglerow", "pathrow", "modal", "rowline"];
  const reaches = (before, where) => {
    const around = KNOWN.filter(name => new RegExp('class="[^"]*\\b' + name + '\\b').test(before));
    assert.ok(around.length, where + " is a bare select inside nothing this rule knows");
    assert.ok(styled.some(one => one === "select" || around.some(name => one.indexOf("." + name) >= 0)),
      where + " sits in " + around.map(n => "." + n).join(" ") + ", and no appearance:none rule"
      + " reaches any of those: it is drawn by the platform in system Arial on a grey ground."
      + " Styled: " + styled.join(" | "));
  };

  for (const id of ids) {
    const at = HTML.indexOf('id="' + id + '"');
    reaches(HTML.slice(Math.max(0, at - 900), at), "#" + id);
  }

  // And the ones the page BUILDS. A select written into a template string is exactly as
  // platform-drawn as one in the markup, and no gate on index.html can see it.
  const built = [...SOURCE.matchAll(/<select[^>]*/g)];
  assert.ok(built.length >= 3, "app.js builds no selects any more, which is worth checking too");
  for (const m of built) {
    const line = SOURCE.slice(0, m.index).split("\n").length;
    reaches(SOURCE.slice(Math.max(0, m.index - 900), m.index), "the select app.js builds at line " + line);
  }
});

/* ---------------------------------------- the switches, from a keyboard rather than a mouse */

/** The generic switch wiring, run over one span with a real event model. */
function switches() {
  const held = new Set();
  const listeners = { click: [], keydown: [] };
  const el = {
    attrs: {},
    classList: {
      contains: c => held.has(c),
      toggle(c, on) {
        if (on === undefined) { if (held.has(c)) held.delete(c); else held.add(c); return held.has(c); }
        if (on) held.add(c); else held.delete(c);
        return !!on;
      },
    },
    getAttribute(name) {
      return Object.prototype.hasOwnProperty.call(el.attrs, name) ? el.attrs[name] : null;
    },
    setAttribute(name, value) { el.attrs[name] = String(value); },
    addEventListener(type, fn) { (listeners[type] || (listeners[type] = [])).push(fn); },
    click() { for (const fn of listeners.click.slice()) fn({ type: "click" }); },
    key(name) {
      let stopped = false;
      for (const fn of listeners.keydown.slice())
        fn({ key: name, preventDefault() { stopped = true; } });
      return stopped;
    },
    held,
  };
  const context = {
    Object, String, console,
    $$: selector => (selector === "[data-t]" ? [el] : []),
    syncAdvGates() { context.gated = (context.gated || 0) + 1; },
    el,
  };
  vm.createContext(context);
  vm.runInContext(fn("function markSwitch(t){"), context, { filename: "app.js#aria" });
  const from = SOURCE.indexOf('$$("[data-t]").forEach(t=>{');
  assert.ok(from > 0, "app.js no longer wires every switch in one place");
  const to = SOURCE.indexOf("\n});", from) + 4;
  vm.runInContext(SOURCE.slice(from, to), context, { filename: "app.js#toggles" });
  return context;
}

test("every switch is a switch to something that is not a pair of eyes", () => {
  const h = switches();
  assert.strictEqual(h.el.getAttribute("role"), "switch", "a switch says it is a span and no more");
  assert.strictEqual(h.el.getAttribute("tabindex"), "0", "a switch is not in the tab order");
  assert.strictEqual(h.el.getAttribute("aria-checked"), "false", "a switch does not say which way it is");
});

test("Enter and Space move a switch, down the same road a press takes", () => {
  const h = switches();

  assert.ok(h.el.key("Enter"), "Enter does not even stop the page from scrolling");
  assert.ok(h.el.classList.contains("on"), "Enter did not move the switch");
  assert.strictEqual(h.el.getAttribute("aria-checked"), "true", "the aria did not follow the flip");

  assert.ok(h.el.key(" "), "Space does not move a switch");
  assert.ok(!h.el.classList.contains("on"), "Space did not move the switch back");
  assert.strictEqual(h.el.getAttribute("aria-checked"), "false");

  // A key that is not one of the two is left alone.
  h.el.key("a");
  assert.ok(!h.el.classList.contains("on"), "any key at all moves the switch");

  // And the road really is the click road: the gating that runs on a press ran on the key.
  assert.ok(h.gated >= 2, "a key flipped the class without going through the press road");
});

test("and there is ONE keydown handler rather than one per switch", () => {
  const generic = SOURCE.indexOf('$$("[data-t]").forEach(t=>{');
  const perSwitch = [...SOURCE.matchAll(/\$\("#t[A-Za-z0-9_]+"\)\??\.addEventListener\("keydown"/g)];
  assert.strictEqual(perSwitch.length, 0,
    "a switch has grown a keydown listener of its own: " + perSwitch.map(m => m[0]).join(", "));
  assert.ok(generic > 0);
});

test("a switch set from the host side takes its aria with it", () => {
  const h = switches();
  const setT = fn1("const setT=(id,on)=>{");
  assert.ok(setT.length > 0);
  // setT is a block now, so it is lifted whole.
  const from = SOURCE.indexOf("const setT=(id,on)=>{");
  const to = SOURCE.indexOf("\n};", from) + 3;
  h.$ = () => h.el;
  vm.runInContext(SOURCE.slice(from, to), h, { filename: "app.js#setT" });
  vm.runInContext('setT("tStartWin",true)', h);
  assert.ok(h.el.classList.contains("on"));
  assert.strictEqual(h.el.getAttribute("aria-checked"), "true",
    "a switch set from an answer still tells a screen reader the state it had before");
  vm.runInContext('setT("tStartWin",false)', h);
  assert.strictEqual(h.el.getAttribute("aria-checked"), "false");
});

test("a focused switch shows that it is focused", () => {
  assert.ok(/\.toggle:focus-visible\{[^}]*outline/.test(STYLE),
    "a switch is in the tab order with nothing to show that a key would move it");
});

console.log("");
console.log("app tab selftest: " + passed + " passed"
  + (failures.length ? ", " + failures.length + " FAILED" : ""));
if (failures.length) process.exit(1);
