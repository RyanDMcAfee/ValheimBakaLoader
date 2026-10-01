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
    textContent: "",
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
    scrollIntoView(how) { el.scrolled = (el.scrolled || 0) + 1; el.scrolledHow = how; },
    /* The palette's landing focuses the control it named and lights the row around it, so both
       have to exist here or openAppTab throws inside the frame callback. */
    focus(how) { el.focused = (el.focused || 0) + 1; el.focusedHow = how; },
    closest(selector) {
      if (selector === ".togglerow") return el.row || null;
      return null;
    },
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

  /* Each of the three controls the palette can land on sits in a row of its own, which is what
     carries the words and therefore what the landing lights. */
  const rowed = id => {
    const el = element(id);
    el.row = element(id + "Row", ["togglerow"]);
    return el;
  };
  const byId = {
    "#worldTabServer": server, "#worldTabApp": app, "#worldUnsaved": band,
    "#page-world": page, "#selTextSize": rowed("selTextSize"),
    "#selAppLang": rowed("selAppLang"), "#tStartWin": rowed("tStartWin"),
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
    /* The realm band at the top of the window, which belongs to the Server tab. Recorded rather
       than drawn: what it really does is held by its own rule further down. */
    renderEditBar() { context.asked.push("editBar"); },
    flashRow(el) { context.flashed.push(el && el.id); },
    goPage(name) { opened.push("page:" + name); },
    asked: [],
    flashed: [],
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
  assert.ok(s.asked.indexOf("startWinNote") < 0 && s.asked.indexOf("langRefresh") < 0,
    "the Server tab asked for the App tab's own reads: " + s.asked.join(", "));
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
  vm.runInContext(fn1("let SWITCH_LABEL_SEQ="), context, { filename: "app.js#labelSeq" });
  vm.runInContext(fn("function nameSwitch(t){"), context, { filename: "app.js#name" });
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

/* ------------------------------------------- 1.2.7: what a switch is CALLED, not just how it is set

   The walk of 1.2.6 read the accessibility tree over the real window: all 35 switches carried
   role=switch, tabindex and a correct aria-checked, and every one of them had the name "". A
   screen reader announced "switch, on" thirty-five times and never once said what had moved,
   because the words beside a switch are a sibling <span class="tl"> and that is a label to a
   reader with eyes and nothing at all to the tree. */

/** Every switch index.html ships, with the label span that stands beside it. Read out of the
 *  markup rather than listed, so a switch added later is a switch this rule covers. */
function switchesInMarkup() {
  const found = [];
  const tag = /<(span|div)\b[^>]*\bdata-t\b[^>]*>/g;
  for (let m = tag.exec(HTML); m; m = tag.exec(HTML)) {
    const id = /\bid="([^"]+)"/.exec(m[0]);
    if (!id) continue;
    const rowAt = HTML.lastIndexOf('class="togglerow', m.index);
    const label = rowAt < 0 ? null
      : /<span class="tl"([^>]*)>([\s\S]*?)<\/span>/.exec(HTML.slice(rowAt, m.index));
    found.push({
      id: id[1],
      title: (/\btitle="([^"]*)"/.exec(m[0]) || [])[1] || "",
      labelId: label ? (/\bid="([^"]+)"/.exec(label[1]) || [])[1] || "" : "",
      labelText: label ? label[2].replace(/<[^>]*>/g, "").trim() : "",
      inRow: rowAt >= 0,
    });
  }
  return found;
}

/** The real naming pass, run over one switch with one label beside it. */
function named(one) {
  const label = { id: one.labelId || "", textContent: one.labelText };
  const row = {
    textContent: one.labelText,
    querySelector: selector => (selector === ".tl" && one.labelText ? label : null),
  };
  const attrs = one.title ? { title: one.title } : {};
  const el = {
    id: one.id,
    classList: { contains: () => false, toggle: () => false },
    getAttribute: name => (Object.prototype.hasOwnProperty.call(attrs, name) ? attrs[name] : null),
    setAttribute(name, value) { attrs[name] = String(value); },
    closest: selector => (selector === ".togglerow" && one.inRow ? row : null),
  };
  const context = { Object, String, console, el, label, attrs };
  vm.createContext(context);
  vm.runInContext(fn1("let SWITCH_LABEL_SEQ="), context, { filename: "app.js#labelSeq" });
  vm.runInContext(fn("function nameSwitch(t){"), context, { filename: "app.js#name" });
  const answer = vm.runInContext("nameSwitch(el)", context);
  return { answer, attrs, label };
}

test("every switch in the window has a name, and the name is the words beside it", () => {
  const all = switchesInMarkup();
  assert.ok(all.length >= 30, "found only " + all.length + " switches in index.html");

  const nameless = [];
  for (const one of all) {
    const read = named(one);
    const points = read.attrs["aria-labelledby"];
    const spelled = read.attrs["aria-label"];
    if (!points && !spelled) { nameless.push(one.id + " (no name at all)"); continue; }
    if (points) {
      if (points !== read.label.id) nameless.push(one.id + " points at " + points + " and the label is " + read.label.id);
      else if (!read.label.textContent) nameless.push(one.id + " points at an empty label");
    } else if (!String(spelled).trim()) {
      nameless.push(one.id + " is named with an empty string");
    }
  }
  assert.strictEqual(nameless.length, 0,
    nameless.length + " of " + all.length + " switches resolve to no accessible name: "
    + nameless.join("; "));
});

test("the name is a pointer at the label rather than a copy of its words", () => {
  const one = switchesInMarkup().find(s => s.labelText && !s.labelId);
  assert.ok(one, "every label in the markup already carries an id, so this rule cannot be read");

  const read = named(one);
  assert.strictEqual(read.attrs["aria-labelledby"], one.id + "-label",
    "a label with no id of its own was not given one derived from the switch");
  assert.strictEqual(read.label.id, one.id + "-label", "the id was not written onto the label");
  assert.ok(!read.attrs["aria-label"],
    "the words were COPIED onto the switch, so the name stops following a language switch");
});

test("a label that already has an id keeps it", () => {
  const one = switchesInMarkup().find(s => s.labelId);
  assert.ok(one, "no label in the markup carries an id of its own any more");
  assert.strictEqual(named(one).attrs["aria-labelledby"], one.labelId);
});

test("the generic pass is what names them, so a switch added later cannot be the forgotten one", () => {
  const from = SOURCE.indexOf('$$("[data-t]").forEach(t=>{');
  const to = SOURCE.indexOf("\n});", from);
  const pass = SOURCE.slice(from, to);
  assert.ok(pass.indexOf("nameSwitch(t);") > 0,
    "the one pass that sets role and tabindex does not set a name");
  // And no switch is named by hand in the markup instead, which is how 34 of 35 get forgotten.
  const byHand = [...HTML.matchAll(/<[^>]*\bdata-t\b[^>]*\baria-label(?:ledby)?="[^"]*"[^>]*>/g)];
  assert.strictEqual(byHand.length, 0,
    "a switch is named in the markup rather than by the pass: " + byHand.map(m => m[0]).join(" "));
});

/* --------------------------- 1.2.7: the switches a DIALOG builds, which the pass never reaches */

/** The real dialog wiring, over one switch its dialog wires a click on AFTERWARDS, which is the
 *  order modalOpen really runs in: the pass happens as the dialog is written and the opener
 *  registers its own listeners on the next line. */
function dialogSwitch(opts) {
  const held = new Set((opts && opts.classes) || []);
  const listeners = { click: [], keydown: [] };
  const watchers = [];
  const label = { id: "", textContent: (opts && opts.labelText) || "Separate install (own mods)" };
  const row = {
    textContent: label.textContent,
    querySelector: selector => (selector === ".tl" ? label : null),
  };
  const attrs = {};
  const flips = [];
  /* A class watcher, run on the spot rather than on a microtask. The browser's is asynchronous;
     what is being read here is that the aria is taken from the class AFTER it moved, whichever
     listener moved it, which is the property a second click listener cannot have. */
  const fire = () => { for (const watch of watchers.slice()) watch(); };
  const el = {
    id: (opts && opts.id) || "wsIso",
    classList: {
      contains: c => held.has(c),
      toggle(c, on) {
        if (on === undefined) {
          if (held.has(c)) held.delete(c); else held.add(c);
          fire();
          return held.has(c);
        }
        if (on) held.add(c); else held.delete(c);
        fire();
        return !!on;
      },
    },
    getAttribute: name => (Object.prototype.hasOwnProperty.call(attrs, name) ? attrs[name] : null),
    setAttribute(name, value) { attrs[name] = String(value); },
    closest: selector => (selector === ".togglerow" ? row : null),
    addEventListener(type, fn) { (listeners[type] || (listeners[type] = [])).push(fn); },
    click() { for (const fn of listeners.click.slice()) fn({ type: "click" }); },
    key(name) {
      let stopped = false;
      for (const fn of listeners.keydown.slice()) fn({ key: name, preventDefault() { stopped = true; } });
      return stopped;
    },
    held,
  };
  function Watcher(fn) { this.fn = fn; }
  Watcher.prototype.observe = function () { watchers.push(this.fn); };
  const context = {
    Object, String, console, el, attrs, label, flips,
    MutationObserver: Watcher,
  };
  vm.createContext(context);
  vm.runInContext(fn1("let SWITCH_LABEL_SEQ="), context, { filename: "app.js#labelSeq" });
  vm.runInContext(fn("function markSwitch(t){"), context, { filename: "app.js#aria" });
  vm.runInContext(fn("function nameSwitch(t){"), context, { filename: "app.js#name" });
  vm.runInContext(fn("function wireDialogSwitch(t){"), context, { filename: "app.js#dialogSwitch" });
  vm.runInContext(fn("function gateDialogSwitch(t){"), context, { filename: "app.js#gate" });

  // The pass runs FIRST, as modalOpen runs it, and the dialog registers its own click after.
  vm.runInContext("wireDialogSwitch(el)", context);
  el.addEventListener("click", () => { el.classList.toggle("on"); flips.push(el.held.has("on")); });
  return { el, attrs, label, flips, context };
}

test("a dialog's switch is a switch, with a name, and in the tab order", () => {
  const one = dialogSwitch({ classes: ["toggle", "on"] });
  assert.strictEqual(one.attrs.role, "switch", "a dialog switch is still a bare div to a reader");
  assert.strictEqual(one.attrs.tabindex, "0", "a dialog switch is still out of the tab order");
  assert.strictEqual(one.attrs["aria-checked"], "true", "it does not say which way it is set");
  assert.strictEqual(one.attrs["aria-labelledby"], "wsIso-label", "it does not say what it is");
  assert.strictEqual(one.label.id, "wsIso-label");
});

test("Enter and Space move a dialog's switch, down the road its own dialog wired", () => {
  const one = dialogSwitch({ classes: ["toggle"] });

  assert.ok(one.el.key("Enter"), "Enter does not even stop the page scrolling");
  assert.deepStrictEqual(one.flips, [true], "Enter did not go through the dialog's own listener");
  assert.strictEqual(one.attrs["aria-checked"], "true", "the aria did not follow the flip");

  assert.ok(one.el.key(" "));
  assert.deepStrictEqual(one.flips, [true, false]);
  assert.strictEqual(one.attrs["aria-checked"], "false");

  one.el.key("a");
  assert.deepStrictEqual(one.flips, [true, false], "any key at all moves it");
});

test("the aria follows the class whoever moved it, with no listener order to be wrong about", () => {
  const one = dialogSwitch({ classes: ["toggle"] });

  /* A press, through a listener registered AFTER this pass ran. A second click listener here
     would have read the class the switch was leaving, which is the bug 1.2.6 shipped twice. */
  one.el.click();
  assert.strictEqual(one.attrs["aria-checked"], "true",
    "a press left the aria on the state the switch was leaving");

  /* And the host side's own answer, which is not a press at all: the Forge dialog turns the copy
     switch off when the host side says the world cannot be copied. */
  one.el.classList.toggle("on", false);
  assert.strictEqual(one.attrs["aria-checked"], "false",
    "a switch moved by an answer rather than a finger still tells a reader the old state");

  const watching = fn("function wireDialogSwitch(t){");
  assert.ok(watching.indexOf("MutationObserver") > 0,
    "the aria is back on a click listener, which has an order to be on the wrong side of");
  assert.ok(watching.indexOf('attributeFilter:["class"]') > 0,
    "the watcher is not narrowed to the class, so it fires on every attribute this pass sets");
});

test("a dialog switch the host side refused leaves the tab order with its pointer events", () => {
  const one = dialogSwitch({ classes: ["toggle", "on"] });
  vm.runInContext("gateDialogSwitch(el)", one.context);
  assert.strictEqual(one.attrs.tabindex, "-1",
    "a keyboard walker still lands on a control whose press is already being ignored");
  assert.strictEqual(one.attrs["aria-disabled"], "true");
});

test("and it answers neither Enter nor Space, which was the one road left into it", () => {
  const one = dialogSwitch({ classes: ["toggle", "on"] });
  vm.runInContext("gateDialogSwitch(el)", one.context);

  /* The gate took the tab stop and marked it disabled, and the pointer road was already told to
     ignore it; the keyboard road read neither attribute, so a switch the host side had REFUSED
     could still be moved with a key. A tab stop of -1 is not a wall either: the dialog focuses
     controls itself, and a reader's own navigation reaches it.
     The key is not swallowed, so Space still scrolls the dialog the way it does over anything
     that is not a control. */
  assert.strictEqual(one.el.key(" "), false,
    "the refused switch still swallows Space, so the dialog does not scroll under it");
  assert.strictEqual(one.el.key("Enter"), false);
  assert.deepStrictEqual(one.flips, [], "a switch the host side refused was moved from the keyboard");
  assert.strictEqual(one.attrs["aria-checked"], "true", "the aria moved with a press that never happened");

  // And an ungated switch beside it still answers both, so this is a gate rather than a wall.
  const live = dialogSwitch({ classes: ["toggle"] });
  assert.ok(live.el.key(" "));
  assert.deepStrictEqual(live.flips, [true]);
});

test("every switch a dialog builds is wired by one pass over the dialog, not by the dialog", () => {
  /* The ONE call, beside esWire, in the one place that knows a dialog exists. A per-dialog call
     is how four of twelve got forgotten in the first place: it is a line somebody has to
     remember to write, and the dialog it is forgotten in is the one nobody walks. */
  const open = fn("function modalOpen(html,again){");
  assert.ok(open.indexOf("wireDialogSwitches(modalBg);") > 0,
    "modalOpen does not wire the switches in the dialog it just wrote");
  assert.ok(open.indexOf("esWire(modalBg);") < open.indexOf("wireDialogSwitches(modalBg);"),
    "the switch pass runs before the dialog is even on screen");

  const pass = fn("function wireDialogSwitches(root){");
  assert.ok(/querySelectorAll\(["']\.toggle["']\)/.test(pass),
    "the pass no longer looks for switches by the class every switch carries: " + pass);

  // And no dialog wires one of its own any more, which is the state this replaced.
  const perDialog = [...SOURCE.matchAll(/wireDialogSwitch\(/g)];
  assert.strictEqual(perDialog.length, 2,
    "wireDialogSwitch is called " + perDialog.length + " times: it is declared once and called"
    + " once, from the pass. A third call is a dialog wiring its own switch again.");

  // Nor does any dialog spell the role or the tab stop into its own markup.
  const byHand = [...SOURCE.matchAll(/class="toggle[^"]*"[^>]*\b(?:role|tabindex|aria-checked)=/g)];
  assert.strictEqual(byHand.length, 0,
    "a dialog writes a switch's own role or tab stop into its markup: " + byHand.map(m => m[0]).join(" "));
});

test("the pass reaches every switch in a dialog with more than one", () => {
  /* Three switches in one dialog, which is the Forge wizard's shape: the pass has to reach all of
     them rather than the first. */
  const made = [];
  const built = ["wsIso", "wsSeed", "wsSaveIso"].map(id => {
    const attrs = {};
    const listeners = [];
    const label = { id: "", textContent: id + " label" };
    const el = {
      id, attrs,
      classList: { contains: () => false, toggle: () => false },
      getAttribute: name => (Object.prototype.hasOwnProperty.call(attrs, name) ? attrs[name] : null),
      setAttribute(name, value) { attrs[name] = String(value); },
      closest: () => ({ textContent: label.textContent, querySelector: () => label }),
      addEventListener(type) { listeners.push(type); },
      listeners,
    };
    made.push(el);
    return el;
  });
  function Watcher() {}
  Watcher.prototype.observe = function () {};
  const context = {
    Object, String, console, MutationObserver: Watcher,
    root: { querySelectorAll: () => built },
  };
  vm.createContext(context);
  vm.runInContext(fn1("let SWITCH_LABEL_SEQ="), context, { filename: "app.js#labelSeq" });
  vm.runInContext(fn("function markSwitch(t){"), context, { filename: "app.js#aria" });
  vm.runInContext(fn("function nameSwitch(t){"), context, { filename: "app.js#name" });
  vm.runInContext(fn("function wireDialogSwitch(t){"), context, { filename: "app.js#dialogSwitch" });
  vm.runInContext(fn("function wireDialogSwitches(root){"), context, { filename: "app.js#pass" });

  assert.strictEqual(vm.runInContext("wireDialogSwitches(root)", context), 3,
    "the pass did not wire all three switches");
  for (const el of made) {
    assert.strictEqual(el.attrs.role, "switch", el.id + " is still a bare div");
    assert.strictEqual(el.attrs.tabindex, "0", el.id + " is still out of the tab order");
    assert.strictEqual(el.attrs["aria-labelledby"], el.id + "-label", el.id + " has no name");
    assert.ok(el.listeners.indexOf("keydown") >= 0, el.id + " answers no key");
  }
  assert.strictEqual(vm.runInContext("wireDialogSwitches(null)", context), 0,
    "a dialog with nothing in it threw instead of wiring nothing");
});

/* ------------------------------- 1.2.7: the realm band belongs to the Server tab */

test("the realm band is not drawn on the App tab", () => {
  const body = fn("async function renderEditBar(){");
  const guard = body.slice(0, body.indexOf("\n", body.indexOf("currentPage!==\"world\"")));
  assert.ok(guard.indexOf('WORLD_TAB==="app"') > 0,
    "the band that says which realm is being edited still stands over a page where nothing belongs"
    + " to a realm: " + guard);
  assert.ok(guard.indexOf('bar.style.display="none"') > 0, "the guard no longer takes the band down");

  // And the tab press repaints it, or the band would only change on the next walk into the hall.
  const tab = fn("function worldTab(name){");
  const moved = tab.indexOf("WORLD_TAB=want;");
  const painted = tab.indexOf("renderEditBar();");
  assert.ok(painted > 0, "pressing the tab no longer repaints the band");
  assert.ok(moved < painted,
    "the band is repainted before WORLD_TAB moves, so it reads the tab the hall is leaving");
});

/* ------------------------------- 1.2.7: the palette points AT the setting it landed on */

test("the palette's landing lights the row and focuses the control", () => {
  const h = hall();
  vm.runInContext('openAppTab("selTextSize")', h);

  const want = vm.runInContext('$("#selTextSize")', h);
  assert.strictEqual(want.scrolled, 1, "the control was not scrolled to");
  assert.strictEqual(want.scrolledHow && want.scrolledHow.block, "center",
    'a row already inside the scroller is left where it is by block:"nearest", which on a short'
    + " window is the row the host cannot find");
  assert.deepStrictEqual(h.flashed, ["selTextSizeRow"],
    "the row the palette promised is not marked, so the landing points at nothing: "
    + JSON.stringify(h.flashed));
  assert.strictEqual(want.focused, 1, "the control was not focused");
  assert.strictEqual(want.focusedHow && want.focusedHow.preventScroll, true,
    "the focus scrolls again and fights the scroll above it");

  // And the bare landing still just opens the tab, with nothing to point at.
  const bare = hall();
  vm.runInContext("openAppTab()", bare);
  assert.strictEqual(bare.parts.app.scrolled, 1);
  assert.deepStrictEqual(bare.flashed, [], "openAppTab with no control named lit a row anyway");
});

console.log("");
console.log("app tab selftest: " + passed + " passed"
  + (failures.length ? ", " + failures.length + " FAILED" : ""));
if (failures.length) process.exit(1);
