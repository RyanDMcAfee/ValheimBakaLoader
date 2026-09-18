/* The Map hall's "how old is this render" line, as a table.
 *
 * WHY THIS EXISTS. atlasAge is the one formatter in the product whose English cannot be
 * produced by any Intl.RelativeTimeFormat style: narrow writes "4m ago" and "1.5h ago",
 * short writes "4 min. ago" with a full stop, long writes "4 minutes ago", and what this
 * panel has always shown is "40s ago", "4 min ago", "1.5 h ago", "3 days ago". Rather
 * than let the wording drift to whichever style happened to be closest, the four
 * sentences are catalog entries and only the NUMBER goes through the lookup.
 *
 * That makes the English a thing a copy edit can move, so it is pinned here, at both
 * edges of every tier, against BOTH arms: the catalog one and the hand-rolled fallback
 * that runs when i18n.js never arrived. Both have to say the same thing, which is what
 * keeps the fallback honest rather than merely present.
 *
 * The code under test is the REAL code: the block between the ATLAS-AGE markers in
 * app.js, evaluated here with the two things it reaches for (intl() and T()) wired to
 * the real i18n.js and the real en.json. A copy of the function in this file would pass
 * forever while the page said something else.
 *
 * Prints one line per case and exits non zero on the first failure.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const LOOKUP = path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n.js");
const CATALOG = path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");

let passed = 0;
const failures = [];

function test(name, body) {
  try {
    body();
    passed++;
    console.log("  ok   " + name);
  } catch (problem) {
    failures.push(name + ": " + (problem && problem.message ? problem.message : problem));
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}

/** The tiers as app.js writes them, with the lookup and the catalog wired in. */
function withLookup() {
  const I18N = require(LOOKUP);
  I18N.load(JSON.parse(fs.readFileSync(CATALOG, "utf8")), "en");
  return build(() => I18N);
}

/** The same code with no lookup at all, which is the hand-rolled arm. */
function withoutLookup() {
  return build(() => null);
}

function build(intl) {
  const source = fs.readFileSync(APP, "utf8");
  const from = source.indexOf("/* ATLAS-AGE-BEGIN");
  const to = source.indexOf("/* ATLAS-AGE-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the ATLAS-AGE markers");

  const context = {
    intl: intl,
    T: (id, params) => {
      const lookup = intl();
      return lookup ? lookup.T(id, params) : String(id);
    },
    atlasAge: null,
  };
  vm.createContext(context);
  vm.runInContext(source.slice(from, to) + "\nthis.atlasAge = atlasAge;", context);
  return context.atlasAge;
}

/* Seconds in, English out. Both edges of every tier, and the two the panel is most
   often showing: a map rendered a moment ago and one rendered yesterday. */
const TABLE = [
  [0, "0s ago"],
  [1, "1s ago"],
  [40, "40s ago"],
  [89, "89s ago"],
  [90, "2 min ago"],
  [240, "4 min ago"],
  [3600, "60 min ago"],
  [5399, "90 min ago"],
  [5400, "1.5 h ago"],
  [7200, "2.0 h ago"],
  [86400, "24.0 h ago"],
  [172799, "48.0 h ago"],
  [172800, "2 days ago"],
  [259200, "3 days ago"],
  [864000, "10 days ago"],
];

test("every tier reads the way 1.1.x wrote it, out of the catalog", () => {
  const atlasAge = withLookup();
  for (const [seconds, words] of TABLE) {
    assert.strictEqual(atlasAge(seconds), words, seconds + "s came out as " + atlasAge(seconds));
  }
});

test("and the same, with no lookup in the room at all", () => {
  const atlasAge = withoutLookup();
  for (const [seconds, words] of TABLE) {
    assert.strictEqual(atlasAge(seconds), words, seconds + "s came out as " + atlasAge(seconds));
  }
});

test("a value that is not a number reads as the youngest tier rather than as NaN", () => {
  const atlasAge = withLookup();
  assert.strictEqual(atlasAge(null), "0s ago");
  assert.strictEqual(atlasAge(undefined), "0s ago");
  assert.strictEqual(atlasAge("not a number"), "0s ago");
});

/* The tier that has a plural, read in a locale that writes its own digits. The number
   in the sentence is the host's, and the category behind it is chosen from the number
   rather than from those digits: Number("٣") is NaN, so reading it back off the slot
   put every day on "other" and a pack with a form for two or for a few never saw it.
   English has one and other only, so the proof that the category is really chosen is
   in the lookup's own test; what is held here is that the sentence still reads right
   and carries nothing it should not. */
test("the day tier in a locale with its own digits", () => {
  const I18N = require(LOOKUP);
  I18N.load(JSON.parse(fs.readFileSync(CATALOG, "utf8")), "en");
  const atlasAge = build(() => I18N);
  try {
    I18N.setLocale("ar-EG");
    const said = atlasAge(259200);                 // three days
    assert.strictEqual(said, "٣ days ago", "the day tier came out as " + said);
    assert.ok(said.indexOf("pluralValue") < 0, "the extra parameter reached the screen");
  } finally {
    I18N.setLocale("en");
  }
});

console.log("");
if (failures.length) {
  console.log("ATLAS AGE FAIL " + failures.length + " of " + (passed + failures.length));
  failures.forEach(line => console.log("  " + line));
  process.exit(1);
}
console.log("ATLAS AGE PASS " + passed + " cases");
