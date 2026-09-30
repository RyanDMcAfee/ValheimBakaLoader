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
    get firstChild() { return el.children[0]; },
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
  for (const opening of ["function lnVisible(el){", "function applyTermVis(){",
    "function renderTermEmpty(){", "function termAppend(d){"]) {
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

console.log("");
if (failures.length) {
  console.log("saga empty selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("saga empty selftest: " + passed + " passed");
