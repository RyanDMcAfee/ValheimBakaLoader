/* The row that says the last stop ended without a world save.
 *
 * WHY THIS EXISTS. A stop now asks for a save first and watches for the save line, and when
 * the process went with no save line printed after the stop began, the host side keeps that
 * fact and sends it with every state. The page owes the host one row for it. A row that is
 * not in CONDITION_ORDER is never drawn, a row that is never asked for is never raised, and
 * a row ranked too high hides a failure that is happening this second. All three look right
 * in a diff, so they are held here over the source, the way the save folder row is.
 *
 *   1. The row is a kind the bar draws: it is in CONDITION_ORDER.
 *   2. It ranks BELOW every live failure and live state. The stop is over and nothing the
 *      host presses now changes it; a save failing on a server that is up, a crash, a held
 *      start and a waiting update all come first. The bar draws ONE row.
 *   3. It ranks above the two rows that are only lists (mods with updates, a restart pending).
 *   4. The state raises it, a realm switch clears it, and a save on a server that is up
 *      clears it, so it is about this realm and goes when the world has been written.
 *   5. Closing it tells the host side, and the host side has the RPC and sends the field.
 *   6. Every id it asks for is in the English catalog, and none of its words carry a dash.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/unsaved_stop_row_selftest.js [app.js] [en.json] [Bridge.cs]
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const CATALOG = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");
const BRIDGE = process.argv[4] || path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
const SOURCE = fs.readFileSync(APP, "utf8");
const KEYS = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys || {};
const HOST = fs.readFileSync(BRIDGE, "utf8");

const KIND = "unsavedStop";

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

function fn(name) {
  const start = SOURCE.indexOf("function " + name + "(");
  assert.notStrictEqual(start, -1, name + " is not defined in " + APP);
  const end = SOURCE.indexOf("\n}", start);
  assert.notStrictEqual(end, -1, name + " never closes");
  return SOURCE.slice(start, end);
}

function order() {
  const m = /const\s+CONDITION_ORDER\s*=\s*\[([\s\S]*?)\]/.exec(SOURCE);
  assert.ok(m, "CONDITION_ORDER is gone, so nothing says which row wins");
  return m[1].split(",").map(s => s.trim().replace(/"/g, "")).filter(Boolean);
}

/* The bar draws the first kind in CONDITION_ORDER that is held, which is what this walks. */
function drawn(held) {
  for (const k of order()) if (held.includes(k)) return k;
  return null;
}

test("the row is a kind the condition bar draws", () => {
  assert.ok(order().includes(KIND),
    "CONDITION_ORDER does not name " + KIND + ", so the row is raised, held, and never drawn");
});

test("every live failure and live state outranks the row", () => {
  const live = ["launchHold", "saveFailed", "backupFailed", "crashRelaunch", "bepinexNoMods",
    "bepinexNotLoaded", "bepinexMissingFiles", "serverUpdate", "pluginFailure",
    "bepinexNotice", "appUpdate"];
  const names = order();
  for (const k of live) {
    assert.ok(names.includes(k), k + " is no longer in CONDITION_ORDER; this list needs looking at");
    assert.strictEqual(drawn([KIND, k]), k,
      "with " + k + " and " + KIND + " both held, the bar draws " + KIND
      + ", so a stop that is over hides something that is wrong right now");
  }
});

test("the row outranks the two rows that are only lists", () => {
  for (const k of ["modUpdates", "restartPending"]) {
    assert.strictEqual(drawn([KIND, k]), KIND,
      k + " outranks " + KIND + ", so a count of waiting updates hides a world that may be behind");
  }
});

test("the state raises it, a realm switch and a live save clear it", () => {
  const apply = fn("applyState");
  assert.ok(/"unsavedStop"\s+in\s+st\)\s*conditionUnsavedStop\(st\.unsavedStop\)/.test(apply),
    "applyState never reads st.unsavedStop, so the row is never raised");

  const switchClear = /\[([^\]]*"saveFolderStray"[^\]]*)\]\.forEach\(clearCondition\)/.exec(SOURCE);
  assert.ok(switchClear && switchClear[1].includes('"' + KIND + '"'),
    "the realm switch does not clear " + KIND + ", so one realm's stop is shown on another");

  const saved = SOURCE.indexOf('Native.on("server.worldSaved"');
  assert.notStrictEqual(saved, -1, "the world saved handler is gone");
  const body = SOURCE.slice(saved, SOURCE.indexOf("\n  });", saved));
  assert.ok(body.includes('clearCondition("unsavedStop")'),
    "a save on a server that is up does not clear the row, so it stands after the world was written");

  const raiser = fn("conditionUnsavedStop");
  assert.ok(/if\(!u\)\{clearCondition\("unsavedStop"\);return;\}/.test(raiser),
    "a state with no unsaved stop does not clear the row");
});

test("closing it tells the host side, which has the RPC and sends the field", () => {
  const raiser = fn("conditionUnsavedStop");
  assert.ok(/onDismiss:[^\n]*"server\.unsavedStopSeen"/.test(raiser),
    "closing the row never tells the host side, so a reload raises it again");
  assert.ok(HOST.includes('RegisterRpc("server.unsavedStopSeen"'),
    "the host side has no server.unsavedStopSeen, so the close answers an error");
  assert.ok(/unsavedStop\s*=\s*server\.UnsavedStop\s*==\s*null/.test(HOST),
    "BuildServerState does not send unsavedStop, so the page never learns of it");
});

test("every id the row asks for is in the English catalog, with no dashes", () => {
  const raiser = fn("conditionUnsavedStop");
  const ids = new Set();
  const call = /T\(\s*"([^"]+)"/g;
  let hit;
  while ((hit = call.exec(raiser)) !== null) ids.add(hit[1]);
  for (const id of ["cond.unsaved_stop.title", "cond.unsaved_stop.body", "cond.unsaved_stop.body.unnamed"])
    assert.ok(ids.has(id), "the row no longer asks for " + id);
  for (const id of [...ids, "host.saga.unsaved_stop"]) {
    assert.ok(KEYS[id], id + " is not in " + CATALOG);
    const lore = JSON.stringify(KEYS[id].lore);
    assert.ok(!/[\u2013\u2014]/.test(lore), id + " carries a dash");
  }
});

console.log("");
console.log(passed + " passed, " + failures.length + " failed");
process.exit(failures.length ? 1 : 0);
