/* The world integrity note, on the card as well as in front of a start.
 *
 * WHY THIS EXISTS. 1.2.6 learned to compare a world's committed index against what its chunk
 * files really hold, and to say so when they disagree: this is how the owner's Final Sunset
 * lost 82,563 records without a word. The note was written twice, once as a confirm in front
 * of Kindle and once as a panel under the World field, and only the first one was ever
 * reached. renderWorldIntegrity had a single caller, inside the start path, so a host who
 * never pressed Start never found out, which is exactly the host the card was for. Worse, the
 * check is asked once per world per session, so once realm A's damaged world had been read
 * the panel sat there naming A's world, and switching to realm B left that warning about lost
 * data on B's card until B's own world was started. That is the cross realm bleed the rest of
 * the switch work was written to end, on the one card that reports data loss.
 *
 * These drive the real painter and the real refresher out of app.js.
 *
 *     node scripts/ui/world_integrity_card_selftest.js [app.js]
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
const queue = [];

function test(name, body) { queue.push({ name, body }); }

/** One whole top level function out of app.js, named by its opening line. */
function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from > 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to) + "\n}";
}

/* The card, the prefs and a native side that answers about two worlds: one whose files are
   short of what its index promises, one that is whole. */
function harness() {
  const card = { style: { display: "none" }, innerHTML: "" };
  const damaged = {
    world: "Final Sunset",
    missingTotal: 82563,
    shortfalls: [{ file: "chunk12.db", index: 91000, found: 8437 }],
  };
  const asked = [];
  const context = {
    Native: { available: true },
    FAIL: Symbol("FAIL"),
    S: { prefs: { WorldName: "Final Sunset" } },
    WORLD_INTEGRITY_READ: {},
    T: (id, params) => id + "(" + JSON.stringify(params || {}) + ")",
    esc: s => String(s),
    $: id => (id === "#fWorldIntegrity" ? card : null),
    Object, Array, JSON, Promise, Symbol, console,
    card,
    asked,
  };
  context.worldFieldValue = () => context.fieldWorld;
  context.fieldWorld = "Final Sunset";
  context.rpc = (method, params) => {
    asked.push(method + ":" + params.world);
    return Promise.resolve(params.world === "Final Sunset" ? damaged : null);
  };
  vm.createContext(context);
  for (const opening of ["async function worldIntegrityCheck(world){",
    "async function worldIntegrityFor(world){",
    "function worldIntegritySubject(){",
    "function refreshWorldIntegrity(){",
    "function renderWorldIntegrity(r){"]) {
    vm.runInContext(fn(opening), context, { filename: "app.js#" + opening });
  }
  return context;
}

/* Every read in here is one resolved promise deep, so a couple of turns settles the lot. */
const settle = () => new Promise(done => setImmediate(() => setImmediate(done)));

/* ----------------------------------- rule 1: the card is painted without pressing Kindle */
test("a damaged world puts its note on the Worlds card with no start in sight", async () => {
  const h = harness();
  vm.runInContext("refreshWorldIntegrity()", h);
  await settle();

  assert.strictEqual(h.card.style.display, "",
    "the Worlds card never shows the note, so a host who does not press Start never finds out");
  assert.ok(h.card.innerHTML.indexOf("world.integrity.note") > 0,
    "the card is showing but says nothing about the shortfall");
  assert.ok(h.card.innerHTML.indexOf("Final Sunset") > 0, "the note does not name the world");
  assert.ok(h.card.innerHTML.indexOf("chunk12.db") > 0, "the note does not name the file");
  assert.ok(h.card.innerHTML.indexOf("world.integrity.backups") > 0,
    "the note does not point at the backups");
});

/* --------------------------------------- rule 2: and it does not follow the host to realm B */
test("switching to a whole world clears the other realm's warning", async () => {
  const h = harness();
  vm.runInContext("refreshWorldIntegrity()", h);
  await settle();
  assert.strictEqual(h.card.style.display, "");

  // The switch: renderAllFromPrefs runs with the new realm's prefs and field.
  h.S.prefs = { WorldName: "Second Realm" };
  h.fieldWorld = "Second Realm";
  vm.runInContext("refreshWorldIntegrity()", h);

  assert.strictEqual(h.card.style.display, "none",
    "the previous realm's data loss warning is still on screen the moment the realm changes");
  assert.strictEqual(h.card.innerHTML, "", "the previous realm's sentences are still in the card");

  await settle();
  assert.strictEqual(h.card.style.display, "none",
    "the whole world's card came back with the other realm's warning on it");
});

/* ------------------------------------------ rule 3: one read per world, however many paints */
test("the files are read once per world per session", async () => {
  const h = harness();
  vm.runInContext("refreshWorldIntegrity()", h);
  await settle();
  vm.runInContext("refreshWorldIntegrity()", h);
  await settle();
  vm.runInContext("refreshWorldIntegrity()", h);
  await settle();

  assert.deepStrictEqual(h.asked, ["worlds.integrity:Final Sunset"],
    "the Worlds card is re-reading the world's chunk files on every paint");
  assert.strictEqual(h.card.style.display, "", "the repeated paints lost the note");
});

/* ------------------------- rule 4: an answer about the world we left is not painted late */
test("a read that lands after the host has moved on is dropped", async () => {
  const h = harness();
  let release;
  h.rpc = (method, params) => {
    h.asked.push(method + ":" + params.world);
    return new Promise(done => {
      release = () => done({
        world: "Final Sunset",
        missingTotal: 82563,
        shortfalls: [{ file: "chunk12.db", index: 91000, found: 8437 }],
      });
    });
  };
  vm.runInContext("refreshWorldIntegrity()", h);

  h.S.prefs = { WorldName: "Second Realm" };
  h.fieldWorld = "Second Realm";
  release();
  await settle();

  assert.strictEqual(h.card.style.display, "none",
    "a slow read about the realm the host left painted itself onto the realm they are on");
});

/* ----------------------------------- rule 5: the refresher is wired where a host will meet it */
test("the card is refreshed on a realm switch and on a change of world", () => {
  const painters = SOURCE.split("renderWorldIntegrity(").length - 1;
  assert.ok(painters >= 3, "renderWorldIntegrity lost its callers");

  const all = SOURCE.split("refreshWorldIntegrity()");
  assert.ok(all.length - 1 >= 3,
    "refreshWorldIntegrity is not called from enough places to reach a host who never starts");

  const prefs = fn("function renderAllFromPrefs(){");
  assert.ok(prefs.indexOf("refreshWorldIntegrity()") > 0,
    "a realm switch no longer repaints the world integrity note, so it keeps the old realm's");

  const change = SOURCE.indexOf('$("#fWorld").addEventListener("change"');
  assert.ok(change > 0, "the world field no longer has a change handler");
  const handler = SOURCE.slice(change, SOURCE.indexOf("\n});", change));
  assert.ok(handler.indexOf("refreshWorldIntegrity()") > 0,
    "picking another world leaves the previous world's note under the field");
});

(async () => {
  for (const item of queue) {
    try {
      await item.body();
      passed++;
      console.log("  ok   " + item.name);
    } catch (problem) {
      failures.push(item.name);
      console.log("  FAIL " + item.name);
      console.log("       " + (problem && problem.message ? problem.message : problem));
    }
  }
  console.log("");
  if (failures.length) {
    console.log("world integrity card selftest: " + failures.length + " FAILED, "
      + passed + " passed");
    process.exit(1);
  }
  console.log("world integrity card selftest: " + passed + " passed");
})();
