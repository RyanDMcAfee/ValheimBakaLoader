/* The app-wide sweep of the owner's no-jump rule (1.2.18), proved against app.js, app.css and
 * index.html as they are.
 *
 * WHY THIS EXISTS. Nothing on the page may appear, vanish or jump when a toggle, checkbox,
 * button or menu is clicked. reveal() (the REVEAL block, proved by reveal_selftest.js) is the
 * one way a part of the page shows or hides, and the 1.2.17 Discord screen was the first to use
 * it. The sweep put it under every other show and hide a click can cause, gave the menus, the
 * palette, the hover panel and every dialog a fade on the way out (fadeGhost), and made the
 * folding cards roll. The quiet ways that regresses are a new style.display written by hand
 * somewhere a click reaches, a fold whose body the stylesheet still hides (reveal cannot show
 * what a class rule hides), a menu whose entrance moves under reduced motion, and the helper's
 * own additions (a box shown as flex, a tab swap, a hide with work to do after it) coming
 * undone. docs/motion-sweep.md is the record of every spot the sweep touched or left.
 *
 * The rules:
 *   1. style.display is written by hand only where the record says the sweep left it, and the
 *      record names each of those places.
 *   2. The folding cards and sections: the body starts hidden in the markup, the stylesheet no
 *      longer hides or shows it by class, and the wiring rolls it through reveal with the
 *      chevron class kept.
 *   3. The overlays: the row menu, the globe menu, the palette, the hover panel and every
 *      dialog fade in over 0.2 s ease-out and leave through fadeGhost.
 *   4. Reduced motion: every entrance that moves is a plain fade under it.
 *   5. The helper's additions: {display} shows a box as what it was asked and rolls it away
 *      laid out the same way, {now} sets a box with no roll, {done} runs after the roll or at
 *      once without one, and not when a later call turns the roll around.
 *   6. The Settings tabs swap by taking the old one down at once and fading the new one in,
 *      the unsaved band leaves through fadeGhost, a Gale config preview rolls shut before the
 *      part is drawn again without it, and the Gale dialog's two sections (1.3.0) roll through
 *      reveal with the chevron turning and their counts fading.
 *   7. The Log (1.3.0): the "new lines" chip floats over the lines and fades rather than being
 *      shown and hidden by display, and the Pause button cross-fades its two stacked words, so
 *      a press changes no width in the head row.
 *   8. The sidebar's sub-menus (1.3.0): the dropdown under the hall on screen is drawn hidden and
 *      rolls through reveal; the hover flyout slides out from behind the rail and leaves through
 *      fadeGhost; under reduced motion both are plain fades and the ring's sheen holds still.
 *   9. A Mods row's status pill (1.3.1, issue 30): the table is drawn whole on every redraw, so
 *      renderMods reads each row's status words first and a pill whose words changed fades them
 *      in through revealText; the record names it and the Gale review's Install from Hexium.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/motion_sweep_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const vm = require("vm");
const assert = require("assert");

const ROOT = path.resolve(__dirname, "..", "..");
const WEB = path.join(ROOT, "ValheimBakaLoader", "WebUI");
const APP = fs.readFileSync(path.join(WEB, "app.js"), "utf8").replace(/\r\n/g, "\n");
const CSS = fs.readFileSync(path.join(WEB, "app.css"), "utf8").replace(/\r\n/g, "\n");
const HTML = fs.readFileSync(path.join(WEB, "index.html"), "utf8").replace(/\r\n/g, "\n");
const RECORD = fs.readFileSync(path.join(ROOT, "docs", "motion-sweep.md"), "utf8").replace(/\r\n/g, "\n");

let passed = 0;
const failures = [];
function test(name, fn) {
  try {
    fn();
    passed++;
    console.log("  ok   " + name);
  } catch (problem) {
    failures.push(name);
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}

function block() {
  const from = APP.indexOf("/* REVEAL-BEGIN");
  const to = APP.indexOf("/* REVEAL-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the REVEAL markers");
  return APP.slice(from, to);
}
function body(head) {
  const at = APP.indexOf(head);
  assert.ok(at >= 0, "app.js no longer has " + head);
  return APP.slice(at, APP.indexOf("\n}", at) + 2);
}

/* The places a style.display written by hand is still right, each with the reason the record
   gives. A new one anywhere else is the sweep coming undone. */
const LEFT = [
  { why: "the helper itself", test: (line, i) => i >= block().split("\n").length ? false : true, inBlock: true },
  { why: "goPage: walking between halls", test: line => /p\.style\.display=on\?\(FLEX_PAGES/.test(line) },
  { why: "the rail's realm strip, fitted by measurement", test: line => /sep\.style\.display=list\.length|more\.style\.display=/.test(line) },
  { why: "the Saga's lines, filtered and streamed", test: line => /l\.style\.display=lnVisible\(l\)|d\.style\.display=lnVisible\(d\)/.test(line) },
  { why: "the console caret under typing", test: line => /tCaret\.style\.display=/.test(line) },
  { why: "the row menu's own open and close, which fade through the stylesheet and fadeGhost", test: line => /ctxEl\.style\.display=/.test(line) },
  { why: "the console dialog's rows, narrowed by typing", test: line => /r\.style\.display=\(!q\|\|r\.dataset\.cmd/.test(line) },
  { why: "a Gale preview drawn hidden for its roll in", test: line => /pre\.style\.display="none";reveal\(pre,true\)/.test(line) },
];

test("1. style.display is written by hand only where the record says the sweep left it", () => {
  const lines = APP.split("\n");
  const from = APP.slice(0, APP.indexOf("/* REVEAL-BEGIN")).split("\n").length;
  const to = APP.slice(0, APP.indexOf("/* REVEAL-END */")).split("\n").length;
  const strays = [];
  const used = new Set();
  lines.forEach((line, i) => {
    if (!/\.style\.display\s*=/.test(line)) return;
    const n = i + 1;
    if (n >= from && n <= to) { used.add("the helper itself"); return; }
    const rule = LEFT.find(r => !r.inBlock && r.test(line));
    if (rule) used.add(rule.why); else strays.push(n + ": " + line.trim().slice(0, 90));
  });
  assert.deepStrictEqual(strays, [], "style.display written by hand where a click can reach it (use reveal):\n" + strays.join("\n"));
  for (const r of LEFT) {
    assert.ok(used.has(r.why), "the allow list names a place that no longer exists: " + r.why);
    assert.ok(RECORD.includes(r.why), "docs/motion-sweep.md does not record what was left: " + r.why);
  }
  assert.ok(!/\.hidden\s*=/.test(APP), "something hides through the hidden property");
});

test("2. the folding cards and sections roll: bodies hidden in the markup, no class rule hides them, reveal moves them", () => {
  for (const id of ["upkeepBody", "connBody"])
    assert.ok(new RegExp('id="' + id + '" style="display:none"').test(HTML), "#" + id + " does not start hidden in the markup");
  const bodies = HTML.match(/<div class="formsec-body"[^>]*>/g) || [];
  assert.strictEqual(bodies.length, 3, "the Settings hall has " + bodies.length + " folding sections, not 3");
  for (const b of bodies) assert.ok(b.includes('style="display:none"'), "a folding section's body does not start hidden: " + b);
  for (const rule of [".upkeep-body{display:none}", ".conn-body{display:none}", ".formsec.collapsible+.formsec-body{display:none}",
                      ".formsec.collapsible.open+.formsec-body{display:block}", ".b-upkeep.open .upkeep-body{display:block",
                      ".b-conn.open .conn-body{display:block"])
    assert.ok(!CSS.includes(rule), "the stylesheet still hides or shows a fold by class: " + rule);
  assert.ok(/\.b-upkeep\.open \.upkeep-body\{margin-top:4px\}/.test(CSS) && /\.b-conn\.open \.conn-body\{margin-top:4px\}/.test(CSS),
    "an open card lost the spacing the class gave its body");
  const wire = body("function wireCollapsible(headId,body,targetEl){");
  assert.ok(wire.includes('target.classList.toggle("open",open);'), "the chevron class is no longer kept in step");
  assert.ok(wire.includes("reveal(body,open,{done:"), "the fold's body does not roll through reveal");
  assert.ok(wire.includes("rec.set=set;"), "a fold cannot be opened by name (the Directories section needs it)");
  assert.ok(body("function syncDirsSection(){").includes("rec.set(true)"), "the Directories section pops open instead of rolling");
  /* The bento row holds its content height until a card has rolled shut, or the row snaps. */
  assert.ok(body("function syncBentoRow(){").includes("_rollingShut"), "the bento row snaps back under a card still rolling shut");
});

test("3. the overlays fade in over 0.2 s ease-out and leave through fadeGhost", () => {
  for (const sel of [".langmenu.open{display:block;animation:fadeIn .2s ease-out}",
                     ".ctxmenu.ctxfixed.open{display:block;animation:fadeIn .2s ease-out}",
                     ".wgtip.open{display:block;animation:fadeIn .2s ease-out}"])
    assert.ok(CSS.includes(sel), "an overlay no longer fades in, or moves: " + sel);
  assert.ok(/\.palette-bg\{[^}]*animation:fadeIn \.2s ease-out\}/.test(CSS), "the palette's backdrop does not fade in over 0.2 s");
  assert.ok(/\.modal-bg\{[^}]*animation:fadeIn \.2s ease-out\}/.test(CSS), "a dialog's backdrop does not fade in over 0.2 s");
  assert.ok(!/animation:pageIn \.1[0-9]s/.test(CSS), "a menu still slides in");
  for (const [name, head] of [["the row menu", "function ctxClose(){"], ["the globe menu", "function langMenuClose(){"],
                              ["the palette", "function closePal(){"], ["the hover panel", "function wgTipClose(){"],
                              ["every dialog", "function modalFadeOut(){"]])
    assert.ok(body(head).includes("fadeGhost("), name + " vanishes rather than fading");
  assert.ok(!/dataset\.fadeOut/.test(APP), "a dialog still has to ask to fade out");
});

test("4. reduced motion: every entrance that moves is a plain fade", () => {
  const reduced = CSS.slice(CSS.indexOf("@media (prefers-reduced-motion: reduce){"));
  assert.ok(/\.modal-bg,\.modal\{animation:fadeIn \.2s ease-out\}/.test(reduced), "a dialog still moves under reduced motion");
  assert.ok(/\.page,\.palette-bg,\.palette,\.ctxmenu,\.ctxmenu\.ctxfixed\.open,\.langmenu\.open,\.wgtip\.open\{animation:fadeIn \.2s ease-out\}/.test(reduced),
    "a hall, a menu, the palette or the hover panel still moves under reduced motion");
  assert.ok(/#page-world\.active ~ \.worldunsaved\.on\{animation:none\}/.test(CSS), "the unsaved band still slides under reduced motion");
});

/* A box 40px tall, as reveal_selftest.js stands it in. */
function box(opts) {
  opts = opts || {};
  const anims = [];
  const el = {
    style: { display: opts.hidden === false ? "" : "none", overflow: "", boxSizing: "" },
    parentElement: { getClientRects: () => [{}] },
    get offsetHeight() { return this.style.display === "none" ? 0 : 40; },
    getClientRects() { return this.style.display === "none" ? [] : [{}]; },
    animate(frames, timing) {
      const a = { frames, timing, cancelled: false, onfinish: null,
                  cancel() { this.cancelled = true; },
                  finish() { if (!this.cancelled && this.onfinish) this.onfinish(); } };
      anims.push(a);
      return a;
    },
  };
  el.anims = anims;
  return el;
}
function page() {
  const context = {
    window: { matchMedia: () => ({ matches: false }) },
    getComputedStyle: () => ({ opacity: "1", paddingTop: "4px", paddingBottom: "4px", marginTop: "0px", marginBottom: "6px" }),
  };
  vm.createContext(context);
  vm.runInContext(block(), context, { filename: "app.js#reveal" });
  return context;
}

test("5. the helper's additions: {display}, {now} and {done}", () => {
  const p = page();
  /* A bar laid out as a row: shown as flex, and still flex while it rolls away. */
  const bar = box();
  p.reveal(bar, true, { display: "flex" });
  assert.strictEqual(bar.style.display, "flex", "the bar was not shown as what it asked");
  bar.anims[0].finish();
  assert.strictEqual(bar.style.display, "flex", "the roll in took the bar's layout away");
  p.reveal(bar, false);
  assert.strictEqual(bar.style.display, "flex", "the bar lost its layout before it had rolled away");
  bar.anims[1].finish();
  assert.strictEqual(bar.style.display, "none");
  /* {now}: the box is set with no roll. */
  const tab = box({ hidden: false });
  p.reveal(tab, false, { now: true });
  assert.strictEqual(tab.style.display, "none", "the tab did not go at once");
  assert.strictEqual(tab.anims.length, 0, "a {now} call rolled");
  /* {done}: after the roll; at once with none; never when turned around. */
  const log = [];
  const el = box({ hidden: false });
  p.reveal(el, false, { done: () => log.push("rolled") });
  assert.deepStrictEqual(log, [], "done ran before the roll ended");
  el.anims[0].finish();
  assert.deepStrictEqual(log, ["rolled"], "done did not run after the roll");
  p.reveal(el, false, { done: () => log.push("already") });
  assert.deepStrictEqual(log, ["rolled", "already"], "done did not run for a box already where it was asked");
  const off = box({ hidden: false });
  off.parentElement = { getClientRects: () => [] };
  p.reveal(off, false, { done: () => log.push("off") });
  assert.deepStrictEqual(log, ["rolled", "already", "off"], "done did not run for a box whose hall is off screen");
  const turned = box({ hidden: false });
  p.reveal(turned, false, { done: () => log.push("turned") });
  p.reveal(turned, true);
  turned.anims[0].finish();
  turned.anims[1].finish();
  assert.deepStrictEqual(log, ["rolled", "already", "off"], "done ran for a hide that was turned back into a show");
});

test("6. the tab swap, the unsaved band and a Gale preview", () => {
  const tab = body("function worldTab(name){");
  assert.ok(tab.includes("const swap=(el,on)=>reveal(el,on,on?{fade:true}:{now:true});"), "the Settings tabs no longer swap as leave-at-once, fade-in");
  assert.ok(tab.includes('reveal(save,want==="server",{fade:true})'), "Save Config pops in and out with the tab");
  assert.ok(/if\(!any\) fadeGhost\(notice\);/.test(APP), "the unsaved band vanishes rather than fading");
  const gale = body("function galeCfgToggle(path){");
  assert.ok(gale.includes("reveal(pre,false,{done:close})"), "a Gale preview is torn out before it has rolled shut");
  assert.ok(gale.includes('pre.style.display="none";reveal(pre,true);'), "a Gale preview pops in with the redraw");
  /* 1.3.0: the Gale dialog's two sections fold through reveal under the halls' chevron look,
     a folded body starts hidden in its markup, and the counts fade to their new words. */
  const sect = body("function galeSectSet(key,open){");
  assert.ok(sect.includes('sect.classList.toggle("open",open);') && sect.includes('reveal(m.querySelector(key==="mods"?"#galeModsBody":"#galeCfg"),open);'),
    "a Gale dialog section pops open or shut instead of rolling");
  assert.ok(body("function galeDialogHtml(st){").includes(`const shut=k=>GALE_SECT[k]?"":' style="display:none"';`), "a folded Gale section is not hidden in its markup");
  assert.ok(/\.modal \.galesec\.open>\.galesech \.sectchev svg\{transform:none\}/.test(CSS), "the Gale chevron does not turn with its section");
  const counts = body("function galeTicksShow(){");
  assert.ok(counts.includes('revealText(m.querySelector("#galeModsCount")') && counts.includes('revealText(m.querySelector("#galeCfgCount")'), "a Gale count jumps to its new words");
});

test("7. the Log's chip fades over the lines and the Pause button cross-fades its words", () => {
  assert.ok(/<div class="termpane">[\s\S]*id="termNew"/.test(HTML), "the chip is no longer over the lines");
  assert.ok(!/\.termhead \.hidden\{display:none\}/.test(CSS), "the chip is shown and hidden by display again");
  assert.ok(/\.termpane #termNew\{[^}]*transition:opacity \.2s ease-out,visibility 0s linear 0s\}/.test(CSS), "the chip does not fade in");
  assert.ok(/\.termpane #termNew\.hidden\{opacity:0;visibility:hidden;pointer-events:none;\s*transition:opacity \.2s ease-out,visibility 0s linear \.2s\}/.test(CSS),
    "the chip vanishes rather than fading out");
  assert.ok(/<button type="button" class="fpill termhold" id="termHold"[^>]*><span class="th-pause"[^>]*>Pause<\/span><span class="th-live"[^>]*>Live<\/span><\/button>/.test(HTML),
    "the Pause button no longer holds both of its words");
  assert.ok(/\.termhold>span\{grid-area:1\/1;transition:opacity \.2s ease-out/.test(CSS), "the Pause button's words are not stacked in one cell, or do not fade");
  const stream = body("function renderTermStream(){");
  assert.ok(!/\.style\.display|textContent=T\("saga\.hold/.test(stream), "the Pause button or the chip is swapped by display or by rewriting the words");
  assert.ok(RECORD.includes("the Log's Pause button"), "docs/motion-sweep.md does not record the Log's chip and Pause button");
});

test("8. the sidebar's sub-menus: the dropdown rolls, the flyout slides out and fades away, both fades under reduced motion", () => {
  const sync = body("function subSync(page,opts){");
  assert.ok(sync.includes("reveal(nav,on,opts&&opts.now?{now:true}:undefined);"), "the sub-menu pops in and out instead of rolling");
  assert.ok(body("function subWire(){").includes('style="display:none"><div class="subwell">'), "the sub-menu is not drawn hidden for its roll");
  assert.ok(/\.navgroup\{[^}]*flex:none\}/.test(CSS), "the sub-menu sits in the rail's gap, so the gap appears at once when it rolls");
  assert.ok(body("function subFlyClose(){").includes("fadeGhost(f.el);"), "the flyout vanishes rather than fading");
  assert.ok(CSS.includes(".subfly.open .subfly-panel{animation:subFlyIn .2s ease-out}") && CSS.includes("@keyframes subFlyIn{from{opacity:0;transform:translateX(-8px)}to{opacity:1;transform:none}}"),
    "the flyout does not slide 8px out over 0.2 s");
  const rm = CSS.slice(CSS.indexOf("@media (prefers-reduced-motion:reduce){\n  .subfly.open .subfly-panel"));
  assert.ok(rm.slice(0, rm.indexOf("\n}")).includes(".subfly.open .subfly-panel{animation:fadeIn .2s ease-out}"), "the flyout slides under reduced motion");
  assert.ok(RECORD.includes("`subSync` (1.3.0)") && RECORD.includes("`subFlyClose` (1.3.0)"), "docs/motion-sweep.md does not record the sub-menus");
});

test("9. a Mods row's status pill fades its new words in when the table is drawn again (1.3.1)", () => {
  /* The table is rewritten whole, so the only way a pill's new words can fade is to read them
     before and compare after; gale_hexium_selftest.js drives the two functions themselves. */
  const render = body("function renderMods(){");
  const before = render.indexOf('const statusWas=modStatusWords($("#modTable"));');
  const write = render.indexOf('$("#modTable").innerHTML=shown.map(m=>modRowHtml(m,blind))');
  const after = render.indexOf('modStatusFade($("#modTable"),statusWas);');
  assert.ok(before >= 0 && write > before && after > write, "renderMods no longer reads the status words before the redraw and fades them after");
  assert.ok(body("function modStatusFade(tb,was){").includes("revealText(p,words);"), "a status pill's new words no longer go through revealText");
  assert.ok(RECORD.includes("`modStatusFade` in `renderMods` (1.3.1)") && RECORD.includes("`galeHexiumInstall` (1.3.1)"), "docs/motion-sweep.md does not record the 1.3.1 Mods row and Gale review changes");
});

console.log(failures.length === 0
  ? "motion sweep selftest: all " + passed + " rules pass"
  : "motion sweep selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAIL");
process.exit(failures.length === 0 ? 0 : 1);
