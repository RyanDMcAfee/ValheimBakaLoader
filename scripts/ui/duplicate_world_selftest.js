/* Issue 17: Duplicate really duplicates.
 *
 * WHY THIS EXISTS. Duplicate switched profile and opened the New Server forge, and the forge
 * makes a brand new EMPTY world. So the second realm came up over nothing, with the first
 * realm's mods and none of its map, and the host found that out by walking into it. Nothing
 * on the page ever said so, and nothing in the wizard offered the other thing.
 *
 * Six rules, across three files, because the fix only works if all three agree:
 *
 *   1. Duplicate hands the forge the world it was opened from.
 *   2. The forge draws the copy switch ONLY when there is a world to copy, and draws it on.
 *   3. The payload carries copyWorldFrom only when that switch is on. A flag beside the pair
 *      would put the decision on the far side of the wire.
 *   4. The world section says what it is really going to do, both ways, from the catalog.
 *   5. The bridge reads the pair, resolves the folder from the named PROFILE rather than from
 *      anything the page sent, refuses the three ways it can refuse, and lands the copy
 *      BEFORE the first meeting that reads the world's own header. That order is the feature:
 *      after it, the copied world's dials and switches are never read.
 *   6. The Directories card says why this realm's boxes look the way they do.
 *
 * Prints one line per rule and exits non zero on the first failure.
 *
 *     node scripts/ui/duplicate_world_selftest.js [app.js] [index.html] [en.json]
 *
 * Run against 4fc598e's app.js every rule that reads it fails, which is what makes this a
 * gate rather than decoration.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const PAGE = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "index.html");
const CATALOG = process.argv[4] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");
const BRIDGE = process.argv[5] || path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

const SOURCE = fs.readFileSync(APP, "utf8");
const MARKUP = fs.readFileSync(PAGE, "utf8");
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

/** One whole top level function out of app.js, named by its opening line. */
function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from > 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to);
}

/** The servers.create handler out of the bridge, from its registration to the next one. */
function createHandler() {
  const bridge = fs.readFileSync(BRIDGE, "utf8");
  const from = bridge.indexOf('RegisterRpc("servers.create"');
  assert.ok(from > 0, "the bridge no longer registers servers.create");
  const to = bridge.indexOf("RegisterRpc(", from + 20);
  assert.ok(to > from, "the servers.create handler no longer ends at the next registration");
  return bridge.slice(from, to);
}

/* ------------------------------------------------ rule 1: Duplicate names the source world */
test("Duplicate hands the forge the world it was opened from", () => {
  const body = fn("async function duplicateServer(");

  assert.ok(/S\.prefs&&S\.prefs\.WorldName/.test(body),
    "duplicateServer never reads the world of the realm it is duplicating, so the forge has"
    + " nothing to offer to copy");
  assert.ok(/addServerProfile\(world\?\{profile:name,world\}:null\)/.test(body),
    "duplicateServer still opens the forge with nothing, which is the whole of the defect");

  const at = body.indexOf("switchServer(name)");
  const read = body.indexOf("S.prefs&&S.prefs.WorldName");
  assert.ok(at >= 0 && read > at,
    "the world is read before the switch, so it is the world of whichever realm was showing");
});

/* ------------------------------------------ rule 2: the switch, only with a world, and on */
test("the forge draws the copy switch only when there is a world, and draws it on", () => {
  const body = fn("async function addServerProfile(source)");

  assert.ok(/source=\(source&&source\.world\)\?source:null;/.test(body),
    "the forge does not check that the source it was handed really names a world");
  assert.ok(/\(source\r?\n?\s*\?`<div class="togglerow"/.test(body) || /\(source[\s\S]{0,40}togglerow/.test(body),
    "the copy switch is not drawn behind the source, so a plain New Server would show it");
  assert.ok(/id="wsCopyWorld"/.test(body), "there is no copy switch at all");
  assert.ok(/<div class="toggle on" id="wsCopyWorld">/.test(body),
    "the copy switch is drawn off, so Duplicate still makes an empty world unless the host"
    + " notices the switch");
});

/* ---------------------------------------- rule 3: the key rides only when the switch is on */
test("the payload carries copyWorldFrom only when the switch is on", () => {
  const body = fn("async function addServerProfile(source)");

  assert.ok(/if\(source&&copyT&&on\(copyT\)\) payload\.copyWorldFrom=\{profile:source\.profile,world:source\.world\}/.test(body),
    "the copy pair is not put on the payload behind the switch");

  // And never unconditionally, which would copy a world the host said not to copy.
  const unconditional = /copyWorldFrom:[^\n]*\n/.exec(body);
  assert.ok(!unconditional,
    "copyWorldFrom is written into the payload object itself: " + (unconditional && unconditional[0]));
});

/* -------------------------------------- rule 4: the world section says what it will really do */
test("the world section says what it will do, both ways, out of the catalog", () => {
  const body = fn("async function addServerProfile(source)");

  assert.ok(/const paintWorldSection=\(\)=>/.test(body),
    "nothing repaints the world section, so the note cannot follow the switch");
  assert.ok(/copyT\.addEventListener\("click",\(\)=>\{copyT\.classList\.toggle\("on"\);paintWorldSection\(\)\;\}\)/.test(body),
    "moving the copy switch does not repaint the note under it");
  assert.ok(/seedI\.disabled=copying/.test(body),
    "the seed box stays typable with a copy on, and a copied world keeps the seed it was"
    + " made with");

  ["realm.new.copy_world.label", "realm.new.copy_world.title", "realm.new.copy_world.on.note",
   "realm.new.world.empty.note", "realm.new.seed.copied.note"].forEach(id => {
    assert.ok(KEYS[id], "the catalog has no " + id);
    assert.ok(body.indexOf('"' + id + '"') >= 0, "the forge never asks for " + id);
  });

  // The two sentences are about two different outcomes, so they must not be one sentence.
  assert.notStrictEqual(KEYS["realm.new.copy_world.on.note"].lore,
    KEYS["realm.new.world.empty.note"].lore);
});

/* ----------------------------------------- rule 5: the bridge, and the order it does it in */
test("the bridge copies from the named profile's own folder, and before the first meeting", () => {
  const handler = createHandler();

  assert.ok(handler.indexOf('p["copyWorldFrom"] as JObject') > 0,
    "servers.create never reads copyWorldFrom, so the switch on the page is a no-op");

  // The folder is derived from the profile, never taken from the page: a page that could
  // name a folder could point a copy at a directory BakaLoader does not own.
  assert.ok(/ServerPrefsProvider\.LoadPreferences\(copySourceProfile\)/.test(handler),
    "the source folder is not read off the named profile's own preferences");
  assert.ok(handler.indexOf('copyFrom?.Value<string>("folder")') < 0,
    "servers.create takes a folder from the page");

  ["servers.create.copySourceMissing", "servers.create.copySeedConflict",
   "servers.create.copyBadSourceRef", "servers.create.copyNoSourceFolder"].forEach(id => {
    assert.ok(handler.indexOf('"' + id + '"') > 0, "servers.create cannot refuse with " + id);
  });

  // The Barrow's own rule, by the method the Barrow's copy calls.
  assert.ok(/RefuseWhileTheWorldIsBeingWritten\(copySourceWorldName, sourceFolder\)/.test(handler),
    "a copy can be taken from underneath a running server, which is a copy of half a save");

  // The order IS the feature.
  const copy = handler.indexOf("WorldStore.CopyWorldAs(copySource, world, targetSaveFolder)");
  const meeting = handler.indexOf("ImportWorldKeysOnFirstMeeting(");
  assert.ok(copy > 0, "nothing in servers.create copies the source world");
  assert.ok(meeting > 0, "servers.create no longer holds the first meeting");
  assert.ok(copy < meeting,
    "the copy lands after the first meeting reads the world header, so the copied world's"
    + " own dials and switches are never brought in and the realm comes up Normal");

  // Off the UI thread, because a 1.0 world is a whole directory tree.
  assert.ok(/await Task\.Run\(\(\) => WorldStore\.CopyWorldAs\(copySource, world, targetSaveFolder\)\)/.test(handler),
    "the copy runs on the UI thread and freezes the window for the length of a world");
});

/* --------------------------------------- rule 6: the Directories card says why it looks so */
test("the Directories card says whether this realm has its own install and folder", () => {
  assert.ok(MARKUP.indexOf('id="dirIsolationNote"') > 0,
    "index.html carries no note element for the Directories card");
  assert.ok(!/id="dirIsolationNote"[^>]*data-i18n/.test(MARKUP),
    "the note is written by app.js and by the walker: two owners, one element");

  const body = fn("function renderWorldDirs(");
  assert.ok(/#dirIsolationNote/.test(body), "nothing writes the note");
  assert.ok(/p\.IsolatedInstall/.test(body),
    "the note does not read whether this realm has its own install");
  assert.ok(/WORLD_DIR_SAVE_SOURCE/.test(body),
    "the note does not read whether this realm has its own save folder");

  ["world.dir.isolated_note", "world.dir.shared_note"].forEach(id => {
    assert.ok(KEYS[id], "the catalog has no " + id);
    assert.ok(body.indexOf('"' + id + '"') >= 0, "renderWorldDirs never asks for " + id);
  });
});

console.log("");
if (failures.length) {
  console.log("duplicate world selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("duplicate world selftest: " + passed + " passed");
