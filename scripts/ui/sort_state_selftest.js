/* The shape behind issue 14, held off the page without a browser.
 *
 * WHY THIS EXISTS. wireSort wires both sortable tables ONCE, while app.js is being
 * evaluated, and never again. Until 1.2.1 it was handed the state OBJECT (S.modSort,
 * S.vikSort) and switchServer replaced both objects on every realm switch, so from the
 * first switch every header click wrote to an orphan while the renderers read the live
 * one. All twelve headers stopped sorting and only a reload cured it.
 *
 * scripts/ui/sort_survives_realm_switch_probe.js proves the behaviour in a real browser.
 * This is the cheap half that runs on every commit: three rules over the source.
 *
 *   1. wireSort is handed a FUNCTION at every call site, never an object, and it calls
 *      that function inside the click rather than at wiring time.
 *   2. Neither sort state is ever ASSIGNED after the one that makes it. Putting one back
 *      is done in place (sortReset), because a fresh object is a state the headers that
 *      were wired over the old one can no longer reach.
 *   3. The class, not the two names: no call made at statement level (evaluated once) is
 *      handed a container that some later line replaces. That is the sweep, run as a gate
 *      so the next handler of this shape is caught the day it is written.
 *
 * Prints one line per rule and exits non zero on the first failure.
 *
 * node scripts/ui/sort_state_selftest.js [app.js]
 *
 * The path is there so a mutation can be driven without touching the tree: point it at a
 * copy with the getter turned back into an object and rules 1 and 3 have to go red, or the
 * gate is decoration. Run against 709f241's app.js it reports 4 failures.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const SOURCE = fs.readFileSync(APP, "utf8");
const LINES = SOURCE.split("\n");

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

/* The two sort states, named here rather than derived, because the rule is about these two
   in particular: they are the only pair the page wires a handler over once and never again. */
const SORT_STATES = ["S.modSort", "S.vikSort"];

/* ------------------------------------------------------------------ rule 1: the getter */

test("wireSort is handed a way to find the state, not the state itself", () => {
  const calls = [];
  LINES.forEach((line, i) => {
    const at = line.indexOf("wireSort(");
    if (at < 0 || /function wireSort\(/.test(line)) return;
    calls.push({ line: i + 1, text: line.trim() });
  });
  assert.ok(calls.length >= 2, "app.js no longer wires both sortable tables: " + calls.length);

  calls.forEach(call => {
    // wireSort(selector, <second argument>, ...)
    const args = call.text.slice(call.text.indexOf("wireSort(") + "wireSort(".length);
    const second = args.split(",")[1] || "";
    assert.ok(/^\s*(\(\s*\)\s*=>|function\b)/.test(second),
      "app.js:" + call.line + " hands wireSort " + JSON.stringify(second.trim()) +
      ", which is the object itself and not a way to find it");
  });
});

test("wireSort resolves the state inside the click, not while it is wiring", () => {
  const from = SOURCE.indexOf("function wireSort(");
  assert.ok(from > 0, "app.js no longer holds wireSort");
  const body = SOURCE.slice(from, SOURCE.indexOf("\n}", from));
  const parameter = (body.match(/function wireSort\(\s*[^,]+,\s*([A-Za-z_$][\w$]*)/) || [])[1];
  assert.ok(parameter, "wireSort's second parameter could not be read");
  assert.ok(body.indexOf(parameter + "()") > 0,
    "wireSort never calls " + parameter + ", so it is holding whatever it was handed");
  assert.ok(/sortCycle\(\s*[A-Za-z_$][\w$]*\(\)/.test(body),
    "the click no longer resolves the state through the getter before cycling it");
});

/* --------------------------------------------------- rule 2: put back, never replaced */

SORT_STATES.forEach(state => {
  test(state + " is put back in place, never assigned a new object", () => {
    const written = [];
    LINES.forEach((line, i) => {
      const found = new RegExp(state.replace(".", "\\.") + "\\s*=[^=]").exec(line);
      if (found) written.push((i + 1) + ": " + line.trim());
    });
    assert.deepStrictEqual(written, [],
      state + " is assigned here, and every handler wired over the old object is orphaned " +
      "by it:\n       " + written.join("\n       "));
  });
});

test("switchServer puts both sort states back", () => {
  const from = SOURCE.indexOf("async function switchServer(");
  assert.ok(from > 0, "app.js no longer holds switchServer");
  const body = SOURCE.slice(from, SOURCE.indexOf("\n}", from));
  SORT_STATES.forEach(state =>
    assert.ok(body.indexOf("sortReset(" + state + ")") > 0,
      "switchServer no longer puts " + state + " back, so a switch keeps the previous " +
      "realm's column sorted"));
});

/* ------------------------------------------------ rule 3: the same shape anywhere else

   Two halves make the defect: a container that is REPLACED by a later line, and that same
   container handed to a call made at statement level, which is a call made once. Either on
   its own is ordinary; the pair is issue 14. */

const KEYWORDS = new Set(["if", "for", "while", "switch", "catch", "return", "function",
                          "else", "do", "try", "new", "typeof", "const", "let", "var",
                          "class", "throw", "delete"]);
const OWNERS = /(?:^|[;{}\s])((?:S|ATLAS|WIZ|LANG|CFG)\.[A-Za-z_$][\w$]*)\s*=\s*[{[]/g;

test("no handler wired once is handed a container something later replaces", () => {
  const replaced = new Map();
  LINES.forEach((line, i) => {
    let found;
    OWNERS.lastIndex = 0;
    while ((found = OWNERS.exec(line)) !== null) {
      if (!replaced.has(found[1])) replaced.set(found[1], []);
      replaced.get(found[1]).push(i + 1);
    }
  });

  const handed = [];
  LINES.forEach((line, i) => {
    const call = /^([A-Za-z_$][\w$]*)\((.+)$/.exec(line);
    if (!call || KEYWORDS.has(call[1])) return;
    for (const name of replaced.keys())
      if (call[2].indexOf(name) >= 0)
        handed.push((i + 1) + ": " + call[1] + " is handed " + name +
          ", which is replaced at line " + replaced.get(name).join(", "));
  });

  assert.deepStrictEqual(handed, [],
    "these are wired once over an object a later line swaps out:\n       " + handed.join("\n       "));
});

console.log("");
if (failures.length) {
  console.log("sort state selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("sort state selftest: " + passed + " passed");
