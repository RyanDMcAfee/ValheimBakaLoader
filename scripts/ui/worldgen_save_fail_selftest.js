/* A refused difficulty write leaves the dials unsaved, and the page has to say so.
 *
 * WHY THIS EXISTS. Save Config writes twice: the profile through profiles.save, and the
 * world's difficulty dials and five switches through worldgen.save. The second reply was
 * read for two lists and nothing else, so a FAIL from it passed in silence. The handler
 * then ran to its foot and took a fresh snapshot of the whole form, which is what declares
 * the form saved: the notice went down, the Save button stopped breathing, every changed
 * dial lost its marker, and the host walked away believing a difficulty that is not on disk.
 *
 * The rules below hold the repair off the page, over the source, the way the sort-state
 * selftest beside this one does. Four of them, and the ordering one is the whole point: a
 * restore written ABOVE the snapshot would be overwritten by it and look right in a diff.
 *
 *   1. The handler keeps the clean state from before the write (snapBefore) and a flag.
 *   2. A FAIL from worldgen.save sets that flag. The reply is read for more than its lists.
 *   3. On a failure the dial and switch controls are put back to their saved values and
 *      the signs are repainted, and that happens AFTER the snapshot, not before it.
 *   4. The controls put back are derived from WORLDGEN and WORLDGEN_SWITCHES rather than
 *      typed out, so a dial added later is covered on the day it is added. And the host is
 *      told: the failure sentence is asked for by the handler and is in the catalog.
 *
 * Prints one line per rule and exits non zero on the first failure.
 *
 *     node scripts/ui/worldgen_save_fail_selftest.js [app.js] [en.json]
 *
 * The paths are there so a mutation can be driven without touching the tree: point it at a
 * copy with the restore block deleted and rules 1, 3 and 4 go red. Run against 4fc598e's
 * app.js every one of the four fails, which is what makes this a gate rather than decoration.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const CATALOG = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");
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

/* The Save Config click handler, from its first line to the line that closes it. Read as
   one block so "after the snapshot" is a question that can be asked at all. */
function saveHandler() {
  const start = SOURCE.indexOf('$("#saveCfgBtn").addEventListener("click"');
  assert.notStrictEqual(start, -1, "the Save Config click handler was not found in " + APP);
  const end = SOURCE.indexOf("\n});", start);
  assert.notStrictEqual(end, -1, "the Save Config click handler never closes");
  return SOURCE.slice(start, end);
}

const HANDLER = saveHandler();

/* ------------------------------------------------- rule 1: the clean state is kept */
test("the handler keeps the clean state from before the write", () => {
  const kept = /const\s+snapBefore\s*=\s*WORLD_SNAP\s*\?/.test(HANDLER);
  assert.ok(kept, "nothing in the handler copies WORLD_SNAP before the write, so there is"
    + " no saved value to put a refused dial back to");

  assert.ok(/let\s+worldGenFailed\s*=\s*false/.test(HANDLER),
    "the handler names no flag for a refused difficulty write");

  const copiedAt = HANDLER.search(/const\s+snapBefore\s*=/);
  const writtenAt = HANDLER.indexOf('rpc("worldgen.save"');
  assert.ok(copiedAt >= 0 && writtenAt >= 0 && copiedAt < writtenAt,
    "the clean state is copied after the write, which copies the values the host just"
    + " changed and proves nothing");
});

/* ------------------------------------------------- rule 2: the reply is read for FAIL */
test("a FAIL from worldgen.save is read as a failure", () => {
  const read = /saved\s*===\s*FAIL\s*\)\s*worldGenFailed\s*=\s*true/.test(HANDLER);
  assert.ok(read, "the worldgen.save reply is still read only for passThrough and switches,"
    + " so a refused write passes in silence");
});

/* ------------------- rule 3: the dials go back, and after the snapshot rather than before */
test("a refused write puts the dials back, after the snapshot", () => {
  const snapAt = HANDLER.lastIndexOf("worldFormSnapshot();");
  assert.notStrictEqual(snapAt, -1, "the handler no longer snapshots the form at its foot");

  const restoreAt = HANDLER.search(/if\s*\(\s*worldGenFailed\s*&&\s*snapBefore\s*\)/);
  assert.notStrictEqual(restoreAt, -1,
    "nothing in the handler puts the dials back when the difficulty write is refused");

  assert.ok(restoreAt > snapAt,
    "the dials are put back BEFORE the snapshot at line offset " + snapAt + ", and the"
    + " snapshot then overwrites every one of them: the notice still goes down");

  const tail = HANDLER.slice(restoreAt);
  assert.ok(/WORLD_SNAP\[\s*id\s*\]\s*=\s*snapBefore\[\s*id\s*\]/.test(tail),
    "the restore does not write the saved values back into WORLD_SNAP");
  assert.ok(/renderWorldDirty\(\)/.test(tail),
    "the restore never repaints, so the marker, the notice, the band and the pulse are"
    + " left showing the state the snapshot put them in");
});

/* --------------- rule 4: the control list is derived, and the host is told in words */
test("the controls are derived from the dial tables and the host is told", () => {
  const fn = /function\s+worldGenFormControlIds\s*\(\s*\)\s*\{([\s\S]*?)\n\}/.exec(SOURCE);
  assert.ok(fn, "worldGenFormControlIds is not defined, so the restore names its own list");

  assert.ok(/for\s*\(\s*const\s+key\s+in\s+WORLDGEN\s*\)/.test(fn[1]),
    "the dials are not read off WORLDGEN, so a dial added later is not put back");
  assert.ok(/WORLDGEN_SWITCH_KEYS/.test(fn[1]) && /WORLDGEN_SWITCHES/.test(fn[1]),
    "the five world switches are not read off WORLDGEN_SWITCHES, so a refused switch is"
    + " declared saved");

  const asked = /T\("world\.difficulty\.save_failed\.toast"\)/.test(HANDLER);
  assert.ok(asked, "the handler never tells the host the difficulty write was refused");

  const keys = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys || {};
  assert.ok(keys["world.difficulty.save_failed.toast"],
    "the catalog has no world.difficulty.save_failed.toast, so the toast would render its id");
});

console.log("");
if (failures.length) {
  console.log("worldgen save fail selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("worldgen save fail selftest: " + passed + " passed");
