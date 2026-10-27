/* The toast stack's lift (updateToastLift in app.js), driven with a stand-in document.
 *
 * WHY THIS EXISTS. Issue 29 (1.3.0): after the Gale review dialog closed, every toast landed
 * mid-screen instead of at the foot of the page. modalClose leaves a fading copy of the dialog
 * behind (fadeGhost, 220 ms) and then measures the lift; the copy still carried the open class,
 * so the function found the ghost's button row and kept the lift at the dialog's height until
 * the next dialog or a resize. The fix is one selector, and this test is what keeps it: a live
 * dialog lifts the stack, a ghost on its own does not, a ghost beside a live dialog is ignored
 * whichever of the two the document lists first, a dialog taller than the page is capped, an
 * empty page writes 0px, and the CSS still reads the variable the function writes.
 *
 * THE CODE UNDER TEST is updateToastLift and its base constant, cut out of app.js by their own
 * lines and evaluated here with document.querySelector stood in for. The stand-in understands
 * exactly what the selector asks: a compound of classes with :not() parts, then descendants, so
 * the old selector fails rule 2 and the fixed one passes it. Nothing else of the page is loaded.
 *
 * The rules:
 *   1. A live dialog: the stack is lifted to sit just above its button row.
 *   2. Only a ghost (the fading copy, class reveal-ghost, still open): the lift is 0px.
 *   3. A ghost listed before a live dialog: the live one is measured, the ghost is skipped.
 *   4. A dialog whose button row reaches the top of the page: the lift is capped inside the page.
 *   5. No dialog at all: 0px, and the value is always written (never left from before).
 *   6. The CSS: .toasts sits at calc(<base>px + var(--toast-lift,0px)) and the base matches the
 *      constant in app.js.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/toast_lift_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const vm = require("vm");
const assert = require("assert");

const ROOT = path.resolve(__dirname, "..", "..");
const APP_JS = path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const APP_CSS = path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.css");

const src = fs.readFileSync(APP_JS, "utf8");
const start = src.indexOf("const TOAST_BASE=");
const endMarker = "function setToastLift(){updateToastLift();}";
const end = src.indexOf(endMarker);
assert.ok(start > 0 && end > start, "could not find updateToastLift in app.js");
const block = src.slice(start, end + endMarker.length);

/* The stand-in document. A dialog is a class list plus the box of its button row; the
   selector's first compound is matched against the class list, the rest of the selector
   (" .modal .mbtns") means that dialog's button row. */
function makeDocument(dialogs, app) {
  const writes = [];
  function parseCompound(sel) {
    const first = sel.trim().split(/\s+/)[0];
    const need = [], ban = [];
    const re = /:not\(\.([\w-]+)\)|\.([\w-]+)/g;
    let m;
    while ((m = re.exec(first))) {
      if (m[1]) ban.push(m[1]); else need.push(m[2]);
    }
    return { need, ban };
  }
  return {
    writes,
    documentElement: { style: { setProperty(k, v) { writes.push([k, v]); } } },
    querySelector(sel) {
      const { need, ban } = parseCompound(sel);
      for (const d of dialogs) {
        const has = new Set(d.classes);
        if (need.every(c => has.has(c)) && !ban.some(c => has.has(c))) return d.mbtns;
      }
      return null;
    },
    app,
  };
}

function run(dialogs, app) {
  const document = makeDocument(dialogs, app);
  const ctx = {
    document,
    Math,
    $(sel) { return sel === "#app" ? app : null; },
  };
  vm.createContext(ctx);
  vm.runInContext(block + "\nupdateToastLift();", ctx);
  const last = document.writes[document.writes.length - 1];
  /* A top-level const does not become a property of the context, so it is read back as a script. */
  const base = vm.runInContext("TOAST_BASE", ctx);
  return { lift: last ? last[1] : null, base, writes: document.writes.length };
}

const app = { getBoundingClientRect: () => ({ top: 0, bottom: 800, height: 800 }) };
const row = (top, height) => ({ getBoundingClientRect: () => ({ top, height }) });
const live = { classes: ["modal-bg", "open"], mbtns: row(600, 40) };
/* The ghost's row sits higher than the live one, so a selector that matched the ghost would
   give a different number rather than the right one by luck. */
const ghost = { classes: ["modal-bg", "open", "reveal-ghost"], mbtns: row(300, 40) };
const tall = { classes: ["modal-bg", "open"], mbtns: row(0, 40) };

let failed = 0;
function rule(n, text, fn) {
  try { fn(); console.log(`rule ${n} ok: ${text}`); }
  catch (e) { failed++; console.log(`rule ${n} FAIL: ${text}\n    ${e.message}`); }
}

const base = run([], app).base;

rule(1, "a live dialog lifts the stack above its button row", () => {
  const r = run([live], app);
  assert.strictEqual(r.lift, `${800 - 600 + 12 - base}px`);
});
rule(2, "only a ghost of a closed dialog: the lift is 0px", () => {
  const r = run([ghost], app);
  assert.strictEqual(r.lift, "0px");
});
rule(3, "a ghost listed before a live dialog: the live one is measured", () => {
  const r = run([ghost, live], app);
  assert.strictEqual(r.lift, `${800 - 600 + 12 - base}px`);
});
rule(4, "a dialog reaching the top of the page: the lift is capped inside the page", () => {
  const r = run([tall], app);
  assert.strictEqual(r.lift, `${800 - base - 160}px`);
});
rule(5, "no dialog: 0px, and the value is written every time", () => {
  const r = run([], app);
  assert.strictEqual(r.lift, "0px");
  assert.strictEqual(r.writes, 1);
});
rule(6, "the CSS reads the variable and its base matches the constant", () => {
  const css = fs.readFileSync(APP_CSS, "utf8");
  const m = css.match(/\.toasts\{[^}]*bottom:calc\((\d+)px \+ var\(--toast-lift,0px\)\)/);
  assert.ok(m, ".toasts does not read --toast-lift");
  assert.strictEqual(Number(m[1]), base);
});

console.log(failed ? `toast lift selftest: ${failed} rule(s) FAILED` : "toast lift selftest: 6 rules pass");
process.exit(failed ? 1 : 0);
