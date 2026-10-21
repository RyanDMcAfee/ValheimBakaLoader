/* The page's one show/hide helper (REVEAL in app.js), driven with stand-in elements.
 *
 * WHY THIS EXISTS. The owner's rule of 2026-10-21, for all of BakaLoader's page: nothing
 * appears, vanishes or jumps when a toggle, checkbox or button is clicked. A part that shows
 * rolls down from nothing to its own height while it fades in, so the rows under it slide with
 * it; a part that hides rolls up and fades out; with prefers-reduced-motion it is a plain fade.
 * The Discord screen is the first to use it and the app-wide sweep after it reuses it, so the
 * quiet ways it goes wrong are proved here once: a box left with a fixed height or hidden
 * overflow after the roll (the next change of its contents is clipped), a fast second click
 * that starts from the wrong place or is undone by the first roll finishing late (a box stuck
 * half open, or shown when it was unticked), a repaint that rolls a box already where it should
 * be, movement under reduced motion, and a roll on a page that is not on screen.
 *
 * THE CODE UNDER TEST is the block between the REVEAL markers in app.js, evaluated here with
 * the browser's getComputedStyle, matchMedia and Element.animate stood in for.
 *
 * The rules:
 *   1. Show: from display:none to shown, rolled from nothing to the box's own height and faded
 *      in; at the end nothing is left on the box (no height, overflow or box-sizing).
 *   2. Hide: rolled from where it is to nothing and faded out, then display:none.
 *   3. Fast clicking: a second call while one runs starts from where the box is on screen, the
 *      first roll finishing late changes nothing, and the box always ends where the last call
 *      put it. A call that asks for where the box already is does nothing.
 *   4. Reduced motion, and a fade-only box: opacity only, no height, nothing set on the box.
 *   5. A box whose page is not on screen is set at once, with no roll.
 *   6. revealText fades changed words in and leaves the same words alone; revealSwap slides a
 *      box whose contents change to its new height.
 *   7. The CSS: the blocks the Discord screen rolls, and the fade-out stand-in for a dialog.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/reveal_selftest.js
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

/* Objects made inside the page's context have its prototypes, so they are compared as data. */
function same(actual, expected, why) {
  assert.deepStrictEqual(JSON.parse(JSON.stringify(actual)), expected, why);
}

function block() {
  const from = APP.indexOf("/* REVEAL-BEGIN");
  const to = APP.indexOf("/* REVEAL-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the REVEAL markers");
  return APP.slice(from, to);
}

/* A box 40px tall with 4px of padding top and bottom and 6px of margin below. While a roll
   runs, `now` is what the screen shows (the test moves it); otherwise the box is its own size. */
function box(opts) {
  opts = opts || {};
  const anims = [];
  const el = {
    style: { display: opts.hidden === false ? "" : "none", overflow: "", boxSizing: "" },
    parentElement: { getClientRects: () => (opts.offScreen ? [] : [{}]) },
    textContent: "", className: "",
    now: null,
    get offsetHeight() { return this.style.display === "none" ? 0 : (this.now ? parseFloat(this.now.height) : 40); },
    getClientRects() { return this.style.display === "none" || opts.offScreen ? [] : [{}]; },
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

function page(opts) {
  opts = opts || {};
  const context = {
    window: { matchMedia: q => ({ matches: !!opts.reduced && /prefers-reduced-motion: reduce/.test(q) }) },
    getComputedStyle: el => (el.now ? Object.assign({ opacity: "1", paddingTop: "4px", paddingBottom: "4px", marginTop: "0px", marginBottom: "6px" }, el.now)
                                    : { opacity: "1", paddingTop: "4px", paddingBottom: "4px", marginTop: "0px", marginBottom: "6px" }),
  };
  vm.createContext(context);
  vm.runInContext(block(), context, { filename: "app.js#reveal" });
  for (const name of ["reveal", "revealSwap", "revealText", "revealShown"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  return context;
}

/* Nothing of the roll is left on the box. */
function settled(el, display) {
  assert.strictEqual(el.style.display, display, "the box ends with display " + JSON.stringify(el.style.display));
  assert.strictEqual(el.style.overflow, "", "the roll left overflow on the box");
  assert.strictEqual(el.style.boxSizing, "", "the roll left box-sizing on the box");
  assert.ok(!("height" in el.style), "the roll pinned a height on the box");
}

test("1. show rolls the box down from nothing to its own height, fading in, and leaves nothing behind", () => {
  const p = page(), el = box();
  assert.strictEqual(p.reveal(el, true), true);
  assert.strictEqual(el.anims.length, 1, "showing did not roll");
  const [from, to] = el.anims[0].frames;
  same(from, { opacity: "0", height: "0px", paddingTop: "0px", paddingBottom: "0px", marginTop: "0px", marginBottom: "0px" });
  same(to, { opacity: "1", height: "40px", paddingTop: "4px", paddingBottom: "4px", marginTop: "0px", marginBottom: "6px" });
  assert.strictEqual(el.anims[0].timing.duration, 200);
  assert.strictEqual(el.anims[0].timing.easing, "ease-out");
  assert.strictEqual(el.style.display, "", "the box is not in the layout while it rolls in");
  assert.strictEqual(el.style.overflow, "hidden", "the contents spill out while the box is short");
  el.anims[0].finish();
  settled(el, "");
});

test("2. hide rolls the box up from where it is to nothing, fading out, then takes it out of the layout", () => {
  const p = page(), el = box({ hidden: false });
  assert.strictEqual(p.reveal(el, false), false);
  const [from, to] = el.anims[0].frames;
  assert.strictEqual(from.height, "40px");
  same(to, { opacity: "0", height: "0px", paddingTop: "0px", paddingBottom: "0px", marginTop: "0px", marginBottom: "0px" });
  assert.strictEqual(el.style.display, "", "the box vanished before it rolled up");
  el.anims[0].finish();
  settled(el, "none");
});

test("3. fast clicking starts from where the box is, a late finish changes nothing, the last call wins", () => {
  const p = page(), el = box();
  p.reveal(el, true);
  el.now = { height: "18px", opacity: "0.45", paddingTop: "2px", paddingBottom: "2px", marginBottom: "3px" };
  p.reveal(el, false);
  assert.ok(el.anims[0].cancelled, "the first roll was not stopped");
  assert.strictEqual(el.anims[1].frames[0].height, "18px", "the hide did not start from where the box was on screen");
  assert.strictEqual(el.anims[1].frames[0].opacity, "0.45");
  el.anims[0].onfinish && el.anims[0].onfinish(); /* the stopped roll reporting late */
  assert.strictEqual(el.style.display, "", "a stopped roll finishing late moved the box");
  el.now = { height: "9px", opacity: "0.2" };
  p.reveal(el, true);
  assert.strictEqual(el.anims[2].frames[0].height, "9px", "the show did not start from where the box was on screen");
  el.now = null;
  el.anims[1].onfinish && el.anims[1].onfinish();
  assert.strictEqual(el.style.display, "", "the hide finishing late hid a box the host ticked again");
  el.anims[2].finish();
  settled(el, "");
  /* Ten clicks in a row end where the tenth put it, never half open. */
  for (let i = 0; i < 10; i++) p.reveal(el, i % 2 === 1);
  el.anims[el.anims.length - 1].finish();
  settled(el, "");
  assert.strictEqual(p.revealShown(el), true);
  /* A repaint that asks for where the box already is rolls nothing. */
  const count = el.anims.length;
  p.reveal(el, true);
  assert.strictEqual(el.anims.length, count, "a repaint rolled a box that was already shown");
  const hidden = box();
  p.reveal(hidden, false);
  assert.strictEqual(hidden.anims.length, 0, "a repaint rolled a box that was already hidden");
});

test("4. reduced motion, and a fade-only box: a plain 0.2 s fade, nothing set on the box", () => {
  for (const [p, opts, why] of [[page({ reduced: true }), undefined, "reduced motion"], [page(), { fade: true }, "a fade-only box"]]) {
    const el = box();
    p.reveal(el, true, opts);
    same(el.anims[0].frames, [{ opacity: "0" }, { opacity: "1" }], why + " moved the box");
    assert.strictEqual(el.anims[0].timing.duration, 200);
    assert.strictEqual(el.style.overflow, "", why + " set overflow");
    el.anims[0].finish();
    settled(el, "");
    p.reveal(el, false, opts);
    same(el.anims[1].frames, [{ opacity: "1" }, { opacity: "0" }]);
    el.anims[1].finish();
    settled(el, "none");
  }
});

test("5. a box whose page is not on screen is set at once", () => {
  const p = page(), el = box({ offScreen: true });
  p.reveal(el, true);
  assert.strictEqual(el.anims.length, 0, "a box nobody can see was rolled");
  settled(el, "");
  p.reveal(el, false);
  settled(el, "none");
  /* And a stand-in with no animate at all (the other selftests' elements) is set at once too. */
  const bare = { style: { display: "none" } };
  p.reveal(bare, true);
  assert.strictEqual(bare.style.display, "");
  assert.strictEqual(p.reveal(null, true), false);
});

test("6. changed words fade in, the same words are left alone, and a box whose contents change slides", () => {
  const p = page(), el = box({ hidden: false });
  p.revealText(el, "Not set up yet", "wiz-stat dim");
  assert.strictEqual(el.textContent, "Not set up yet");
  assert.strictEqual(el.className, "wiz-stat dim");
  same(el.anims[0].frames, [{ opacity: "0" }, { opacity: "1" }]);
  p.revealText(el, "Not set up yet", "wiz-stat ok");
  assert.strictEqual(el.anims.length, 1, "the same words faded again");
  assert.strictEqual(el.className, "wiz-stat ok", "the tone did not follow");

  const prev = box({ hidden: false });
  p.revealSwap(prev, () => { prev.now = { height: "120px" }; });
  const [from, to] = prev.anims[0].frames;
  assert.strictEqual(from.height, "40px");
  assert.strictEqual(to.height, "120px", "the boxes under the preview jump to its new height");
  assert.strictEqual(prev.style.overflow, "hidden");
  prev.anims[0].finish();
  assert.strictEqual(prev.style.overflow, "", "the slide left overflow on the box");
  p.revealSwap(prev, () => {});
  assert.ok(!("height" in prev.anims[1].frames[0]), "a refresh of the same height slid");
  const calm = page({ reduced: true }), still = box({ hidden: false });
  calm.revealSwap(still, () => { still.now = { height: "90px" }; });
  assert.ok(!("height" in still.anims[0].frames[0]), "reduced motion slid the preview");
  let ran = false;
  p.revealSwap(null, () => { ran = true; });
  assert.ok(ran, "a missing box skipped the change");
});

test("7. the CSS: the rolled blocks, and a dialog's fade-out stand-in that takes no clicks", () => {
  assert.ok(/\.herald-sub\{[^}]*\}/.test(CSS), "app.css has no style for the blocks under a tick");
  assert.ok(/\.modal-bg\.modal-ghost\{[^}]*pointer-events:none[^}]*animation:revealOut \.2s ease-out forwards/.test(CSS), "the dialog's stand-in does not fade or takes clicks");
  assert.ok(/@keyframes revealOut\{to\{opacity:0\}\}/.test(CSS));
  const reduced = CSS.slice(CSS.indexOf("@media (prefers-reduced-motion: reduce){"));
  assert.ok(reduced.length > 0 && /\.modal-bg,\.modal\{animation:fadeIn \.2s ease-out\}/.test(reduced), "a dialog still moves under reduced motion");
  /* The stand-in carries no ids or names, so nothing reads it for the real dialog. */
  const fade = APP.slice(APP.indexOf("function modalFadeOut(){"), APP.indexOf("\n}", APP.indexOf("function modalFadeOut(){")));
  assert.ok(fade.includes('n.removeAttribute("id");n.removeAttribute("name");'), "the stand-in keeps ids or radio names");
  assert.ok(fade.indexOf('removeAttribute("name")') < fade.indexOf("insertBefore("), "the stand-in joins a radio group before its names are gone");
  assert.ok(/!box\.dataset\.fadeOut/.test(fade), "every dialog fades out, not only those that asked");
});

console.log(failures.length === 0
  ? "reveal selftest: all " + passed + " rules pass"
  : "reveal selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAIL");
process.exit(failures.length === 0 ? 0 : 1);
