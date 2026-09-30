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
const vm = require("vm");

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
  assert.ok(/addServerProfile\(\{profile:name,world,/.test(body),
    "duplicateServer still opens the forge with nothing, which is the whole of the defect");
  /* And from 1.2.6 the source realm's password and its community listing ride along too,
     so the forge can show a host what a duplicate is about to keep. */
  assert.ok(/password:\(S\.prefs&&S\.prefs\.Password\)\|\|""/.test(body),
    "a duplicate no longer carries the source realm's password into the forge");
  assert.ok(/listed:!!\(S\.prefs&&S\.prefs\.Public\)/.test(body),
    "a duplicate no longer carries the source realm's community listing into the forge");

  const at = body.indexOf("switchServer(name)");
  const read = body.indexOf("S.prefs&&S.prefs.WorldName");
  assert.ok(at >= 0 && read > at,
    "the world is read before the switch, so it is the world of whichever realm was showing");
});

/* ------------------------------------------ rule 2: the switch, only with a world, and on */
test("the forge draws the copy switch only when there is a world, and draws it on", () => {
  const body = fn("async function addServerProfile(source)");

  /* Two questions since 1.2.6, not one. A duplicate names the realm it came from and gets
     its own heading and button; only a duplicate that also names a WORLD gets the copy
     switch. They used to be one test, so Duplicate on a realm with no world opened the
     plain forge under the plain heading. */
  assert.ok(/const duplicating=!!\(source&&source\.profile\);/.test(body),
    "the forge no longer knows whether it was opened by Duplicate");
  assert.ok(/const copyable=!!\(source&&source\.world\);/.test(body),
    "the forge does not check that the source it was handed really names a world");
  assert.ok(/\(copyable\r?\n?\s*\?`<div class="togglerow"/.test(body) || /\(copyable[\s\S]{0,40}togglerow/.test(body),
    "the copy switch is not drawn behind the source world, so a plain New Server would show it");
  assert.ok(/id="wsCopyWorld"/.test(body), "there is no copy switch at all");
  assert.ok(/<div class="toggle on" id="wsCopyWorld">/.test(body),
    "the copy switch is drawn off, so Duplicate still makes an empty world unless the host"
    + " notices the switch");
});

/* ---------------------------------------- rule 3: the key rides only when the switch is on */
test("the payload carries copyWorldFrom only when the switch is on", () => {
  const body = fn("async function addServerProfile(source)");

  assert.ok(/if\(copyable&&copyT&&on\(copyT\)\) payload\.copyWorldFrom=\{profile:source\.profile,world:source\.world\}/.test(body),
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

/* ---------------------------------------- rule 7: the realm's own password and its listing
 *
 * 1.2.6. The forge had no password field and no listing switch at all, so servers.create
 * seeded the new profile from whichever realm the host was standing on and the new realm
 * came up on that realm's password, that realm's RCON password, and that realm's Public
 * flag. A realm founded from a listed one went up listed, on a password nobody had been
 * told, sharing an RCON secret with its parent. */
test("the forge asks for a password of its own and does not list the realm by default", () => {
  const body = fn("async function addServerProfile(source)");
  const host = createHandler();

  assert.ok(/id="wsPass"/.test(body), "the forge has no password box");
  assert.ok(/id="wsPassGen"/.test(body), "the forge cannot roll a password");
  assert.ok(/<div class="toggle" id="wsPublic">/.test(body),
    "the community listing switch is missing, or it is drawn ON");
  assert.ok(/password, public:!!\(pubT&&on\(pubT\)\)/.test(body),
    "the payload does not carry the password and the listing the host chose");

  // And the host side: required, private by default, and its own RCON secret.
  assert.ok(/HostFacingException\("servers\.create\.passwordRequired"/.test(host),
    "the bridge accepts a realm with no password");
  assert.ok(/created\.Password = password;/.test(host),
    "the new realm still inherits the standing realm's password");
  assert.ok(/var listPublicly = p\.Value<bool\?>\("public"\) \?\? false;/.test(host),
    "the community listing does not default to off");
  assert.ok(/created\.Public = listPublicly;/.test(host),
    "the new realm still inherits the standing realm's community listing");
  assert.ok(/created\.RconPassword = NewRconSecret\(\);/.test(host),
    "the new realm still shares the standing realm's RCON password");

  for (const id of ["realm.new.password.label", "realm.new.password.generate",
                    "realm.new.password.note", "realm.new.password.required",
                    "realm.new.password.too_short", "realm.new.password.has_name",
                    "realm.new.password.has_world", "realm.new.public.label",
                    "realm.new.public.title"])
    assert.ok(KEYS[id], "the catalog has no " + id);
});

/* The four rules the game itself holds a start to, said before anything is forged. */
test("the password the forge accepts is one the game will accept", () => {
  const body = fn("function realmPasswordProblem(");
  const context = { T: id => id, console };
  vm.createContext(context);
  vm.runInContext(body + "\n}\nthis.realmPasswordProblem=realmPasswordProblem;", context,
    { filename: "app.js#realmPasswordProblem" });

  const ask = context.realmPasswordProblem;
  assert.strictEqual(ask("", "Midgard", "MidgardWorld"), "realm.new.password.required");
  assert.strictEqual(ask("   ", "Midgard", "MidgardWorld"), "realm.new.password.required");
  assert.strictEqual(ask("abcd", "Midgard", "MidgardWorld"), "realm.new.password.too_short");
  assert.strictEqual(ask("myMidgardkey", "Midgard", "World"), "realm.new.password.has_name");
  assert.strictEqual(ask("aWorldkey", "Midgard", "World"), "realm.new.password.has_world");
  assert.strictEqual(ask("bramble-fen-77", "Midgard", "World"), null);

  // And the rolled one passes its own rules, every time, for any realm and world name.
  const roll = fn("function newRealmPassword(");
  const rolls = { crypto: { getRandomValues: a => { for (let i = 0; i < a.length; i++) a[i] = (i * 37 + 11) % 256; return a; } } };
  const ctx2 = { window: rolls, Math, Uint8Array, console };
  vm.createContext(ctx2);
  vm.runInContext(roll + "\n}\nthis.newRealmPassword=newRealmPassword;", ctx2,
    { filename: "app.js#newRealmPassword" });
  const rolled = ctx2.newRealmPassword();
  assert.ok(rolled.length >= 12, "a rolled password is shorter than twelve characters");
  assert.strictEqual(ask(rolled, "Midgard", "World"), null,
    "a rolled password does not pass the form's own rules: " + rolled);
});

/* ---------------------------------------- rule 8: the duplicate dialog says it is one */
test("Duplicate has its own heading and its own button", () => {
  const body = fn("async function addServerProfile(source)");

  assert.ok(/duplicating\r?\n?\s*\?T\("realm\.duplicate\.title",\{name:source\.profile\}\)/.test(body),
    "the duplicate dialog still wears the New Realm heading");
  assert.ok(/duplicating\r?\n?\s*\?T\("realm\.duplicate\.ok"\)/.test(body),
    "the duplicate dialog still wears the Forge realm button");
  assert.ok(KEYS["realm.duplicate.title"], "the catalog has no realm.duplicate.title");
  assert.ok(KEYS["realm.duplicate.ok"], "the catalog has no realm.duplicate.ok");
});

/* ---------------------------------------- rule 9: an empty name is answered */
test("an empty realm name is answered rather than ignored", () => {
  const body = fn("async function addServerProfile(source)");

  assert.ok(/nameNote\.textContent=T\("realm\.new\.name\.required"\)/.test(body),
    "an empty name still moves the cursor and says nothing");
  assert.ok(/nameI\.classList\.add\("bad"\)/.test(body),
    "the field that wants a value is not marked");
  assert.ok(KEYS["realm.new.name.required"], "the catalog has no realm.new.name.required");
});

/* ---------------------------------------- rule 10: the copy prerequisite is asked at open */
test("whether the world can be copied is asked when the dialog opens", () => {
  const body = fn("async function addServerProfile(source)");
  const bridge = fs.readFileSync(BRIDGE, "utf8");

  assert.ok(/rpc\("servers\.copyCheck",\{profile:source\.profile,world:source\.world\}\)/.test(body),
    "the forge never asks whether the source world can be copied");
  assert.ok(/copyT\.classList\.remove\("on"\)/.test(body),
    "a refused copy leaves the switch on, so the host presses Forge and it fails");
  assert.ok(/hostSentence\(r\.reasonId,r\.reasonParams\)/.test(body),
    "the refusal is not worded from the catalog");

  assert.ok(bridge.indexOf('RegisterRpc("servers.copyCheck"') > 0,
    "the bridge does not answer the question the forge asks");
  // The same refusals servers.create throws, computed by the same code.
  assert.ok(/RefuseWhileTheWorldIsBeingWritten\(world, sourceFolder\);/
    .test(bridge.slice(bridge.indexOf('RegisterRpc("servers.copyCheck"'),
                       bridge.indexOf('RegisterRpc("servers.create"'))),
    "the check is written a second time by hand rather than run through the real gate");

  for (const id of ["realm.new.copy.reason.bad_source", "realm.new.copy.reason.no_folder",
                    "realm.new.copy.reason.missing", "realm.new.copy.unavailable"])
    assert.ok(KEYS[id], "the catalog has no " + id);

  const table = SOURCE.slice(SOURCE.indexOf("const HOST_SENTENCES=["));
  const rows = table.slice(0, table.indexOf("];"));
  for (const id of ["servers.create.copyBadSourceRef", "servers.create.copyNoSourceFolder",
                    "servers.create.copySourceMissing"])
    assert.ok(rows.indexOf('named:"' + id + '"') > 0,
      "the page pairs no sentence with " + id);
});

console.log("");
if (failures.length) {
  console.log("duplicate world selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("duplicate world selftest: " + passed + " passed");
