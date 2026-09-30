/* What the page is still holding about the previous realm after a switch.
 *
 * WHY THIS EXISTS. A probe that drove the real page over two realms found eight pieces of
 * state that belong to ONE realm and were read for the whole window. The worst two write to
 * disk:
 *
 *   1. THE OPEN SCROLL. The Runes editor keeps the file name, the unsaved flag and the text in
 *      CFG, and refreshCfgList SKIPS the re-read while CFG.dirty is set. A switch redrew the
 *      list for the new realm and left realm A's text in the box, so one press of Save wrote
 *      realm A's scroll into realm B's file. No prompt, no toast, nothing in the Saga.
 *   2. THE DIFFICULTY DIALS. S.worldMods was keyed on the world NAME alone, so a set the host
 *      had abandoned on one realm rode into the next, was re-snapshotted as saved, and was
 *      written by that realm's Save Config under a success toast.
 *
 * And six that mislead rather than write: a scan still in flight landing its rows on the new
 * realm; an Update all freezing the other realm's Mods hall; the cleanse flag refusing every
 * other realm's Cleanse button; the World hall's typed settings thrown away with no prompt
 * while the unsaved band was still up; a dismissed mod-updates bar suppressed on the next
 * realm whenever the counts happened to match; and the scan failure sentence telling the new
 * realm Thunderstore could not be read for a scan it never ran.
 *
 *     node scripts/ui/realm_switch_state_selftest.js [app.js] [en.json] [BlendWindow.Bridge.cs]
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const CATALOG = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");
const BRIDGE = process.argv[4]
  || path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");

const SOURCE = fs.readFileSync(APP, "utf8");
const KEYS = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys;
const HOST = fs.readFileSync(BRIDGE, "utf8");

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

const SWITCH = fn("async function switchServer(");

/* ------------------------------------------- rule 1: every per-realm flag is put down */
test("the switch drops every piece of state that belongs to one realm", () => {
  const dropped = [
    ["S.modsScanError=null", "the previous realm's scan failure sentence"],
    ["S.modsScanning=false", "a scan still in flight on the previous realm"],
    ["S.modsUpdating=false", "an Update all still running on the previous realm"],
    ["S.modRowStatus={}", "the previous realm's per-row update phases"],
    ["MOD_UPD=null", "the previous realm's update progress bar"],
    ["S.cleansing=false", "a cleanse running on the previous realm"],
    ["S.worldMods=null", "the previous realm's difficulty dials"],
    ["MOD_UPDATES_HIDDEN=null", "a mod-updates bar the host dismissed on the previous realm"],
    ["PLUGIN_FAIL_HIDDEN=null", "a plugin-failure bar the host dismissed on the previous realm"],
    ["cfgForget()", "the scroll the host had open on the previous realm"],
  ];

  for (const [line, what] of dropped) {
    assert.ok(SWITCH.indexOf(line) > 0,
      "a realm switch still carries " + what + " (" + line + " is gone)");
  }
});

/* ------------------------------------------- rule 2: the unsaved prompt, before anything */
test("a switch away from unsaved work asks before it throws the work away", () => {
  assert.ok(SWITCH.indexOf("unsavedRealmWork()") > 0,
    "a realm switch no longer asks what is unsaved");
  assert.ok(SWITCH.indexOf("confirmModal(") > 0,
    "a realm switch no longer raises the question it knows how to ask");

  const asked = SWITCH.indexOf("unsavedRealmWork()");
  const fetched = SWITCH.indexOf("rpc(\"profiles.get\"");
  assert.ok(asked < fetched,
    "the question is asked after the new realm has already been fetched");

  // And the answer really can stop the switch: the guarded road returns.
  const guard = SWITCH.slice(asked, fetched);
  assert.ok(/\breturn;/.test(guard), "the prompt does not hold the switch back");

  // Saying yes goes through again with the work discarded rather than by a second road.
  assert.ok(SWITCH.indexOf("discardUnsaved:true") > 0,
    "discarding takes a different road from the one that asked");
});

test("both halls that can hold unsaved work are named in the question", () => {
  const body = fn("function unsavedRealmWork(");
  assert.ok(body.indexOf("worldFormDirtyKeys()") > 0,
    "the World hall's own dirty tracker is not consulted");
  assert.ok(body.indexOf("CFG.dirty") > 0,
    "the Runes editor's own dirty flag is not consulted");

  for (const id of [
    "realm.switch.unsaved.title",
    "realm.switch.unsaved.body",
    "realm.switch.unsaved.discard",
    "realm.switch.unsaved.what.world",
    "realm.switch.unsaved.what.runes",
  ]) {
    assert.ok(KEYS[id], "the catalog has no sentence for " + id);
  }
});

/* ------------------------------------------- rule 3: the editor really is emptied */
test("cfgForget empties the file, the flag, the realm and the box", () => {
  const body = fn("function cfgForget(");
  for (const line of ["CFG.file=null", "CFG.dirty=false", "CFG.profile=null", "cfgFindReset()"]) {
    assert.ok(body.indexOf(line) > 0, "cfgForget no longer does " + line);
  }
  assert.ok(/\$\("#cfgEditor"\)/.test(body), "cfgForget no longer clears the editor box");
});

/* ------------------------------------------- rule 4: the write names the realm it is for */
test("a save carries the realm the scroll was read from, and the host refuses a mismatch", () => {
  const write = fn("async function cfgWrite(");
  assert.ok(/profile\s*:\s*profile/.test(write),
    "config.write no longer carries the realm the scroll belongs to");

  const load = fn("async function loadCfg(");
  assert.ok(load.indexOf("CFG.profile=S.profileName") > 0,
    "opening a scroll no longer records which realm it came from");

  assert.ok(SOURCE.indexOf("cfgWrite(CFG.file,$(\"#cfgEditor\").value,CFG.profile)") > 0,
    "the Save button no longer hands the realm to the write");

  // The other side of the seam.
  assert.ok(HOST.indexOf("config.wrongRealm") > 0,
    "the host side no longer refuses a write that names another realm");
  assert.ok(KEYS["runes.write.reason.wrong_realm"],
    "the catalog has no sentence for the refusal the host side throws");
  assert.ok(SOURCE.indexOf("named:\"config.wrongRealm\"") > 0,
    "the page pairs no sentence with that refusal, so it would read as \"the call failed\"");
});

/* ------------- rule 4b: driven, because this one wrote to the wrong realm's disk ------ */
test("driven: an emptied editor has nothing to write, and a write names its own realm", () => {
  const context = {
    console,
    Native: { available: true },
    FAIL: Symbol("FAIL"),
    S: { profileName: "Final Sunset" },
    boxes: {},
    wrote: [],
  };
  /* The two elements the editor road touches, and nothing else. */
  context.$ = id => (context.boxes[id] = context.boxes[id] || { id, value: "", disabled: false });
  context.cfgFindReset = () => { context.findReset = (context.findReset || 0) + 1; };
  context.resetCfgSaveBtn = () => {};
  context.rpc = (name, args) => { context.wrote.push({ name, args }); return Promise.resolve(true); };

  vm.createContext(context);
  const declared = SOURCE.indexOf("const CFG={files:");
  vm.runInContext(
    [
      SOURCE.slice(declared, SOURCE.indexOf(";", declared) + 1),
      fn("function cfgForget(") + "\n}",
      fn("async function cfgWrite(") + "\n}",
      "this.CFG=CFG;this.cfgForget=cfgForget;this.cfgWrite=cfgWrite;",
    ].join("\n"),
    context, { filename: "app.js#cfg" });

  const CFG = context.CFG;

  // The host is on Final Sunset with an unsaved scroll open.
  CFG.file = "shudnal.ExtraSlots.cfg";
  CFG.dirty = true;
  CFG.profile = "Final Sunset";
  context.$("#cfgEditor").value =
    ["[General]", "Slots = 4", "; EDIT MADE ON FINAL SUNSET", ""].join("\n");

  // The switch to Midgard Test, and what it does to the editor.
  context.S.profileName = "Midgard Test";
  context.cfgForget();

  assert.strictEqual(CFG.file, null, "the switch left the previous realm's file name open");
  assert.strictEqual(CFG.dirty, false, "the switch left the unsaved mark standing");
  assert.strictEqual(CFG.profile, null, "the switch left the previous realm named on the buffer");
  assert.strictEqual(context.$("#cfgEditor").value, "",
    "the switch left the previous realm's TEXT in the box, which is what one press of Save "
    + "then wrote into the new realm's file");
  assert.strictEqual(context.$("#cfgSaveBtn").disabled, true);

  // And a write that does happen names the realm it was read from, so the host side can
  // refuse one that names another. The call is made and the payload read straight after:
  // the stub records it as the call goes out, so nothing here waits on a promise this
  // harness would not be watching.
  context.cfgWrite("some.cfg", "text", "Final Sunset");

  assert.strictEqual(context.wrote.length, 1);
  assert.strictEqual(context.wrote[0].name, "config.write");
  assert.strictEqual(context.wrote[0].args.profile, "Final Sunset",
    "the write does not say which realm the scroll belongs to");
  assert.strictEqual(context.wrote[0].args.file, "some.cfg");
  assert.strictEqual(context.wrote[0].args.text, "text");
});

/* ------------------------------------------- rule 5: a late scan reply is dropped */
test("a scan reply for a realm that is no longer open is thrown away", () => {
  const body = fn("function scanReplyIsOurs(");
  const context = { console };
  vm.createContext(context);
  vm.runInContext(
    "var S={profileName:'Beta'};" + body + "\n}\nthis.scanReplyIsOurs=scanReplyIsOurs;this.S=S;",
    context, { filename: "app.js#scanReplyIsOurs" });

  // Asked for Alpha, landed while Beta is on screen.
  assert.strictEqual(context.scanReplyIsOurs("Alpha", { mods: [], profile: "Alpha" }), false,
    "a reply asked for another realm is accepted");

  // Asked for Beta and the host side agrees it read Beta.
  assert.strictEqual(context.scanReplyIsOurs("Beta", { mods: [], profile: "Beta" }), true,
    "a reply for the realm on screen is dropped");

  // The host side read a different realm than the page thinks it is on.
  assert.strictEqual(context.scanReplyIsOurs("Beta", { mods: [], profile: "Alpha" }), false,
    "a reply the host side says is another realm's is accepted");

  // An older bridge sends no profile at all, and the page's own question still answers.
  assert.strictEqual(context.scanReplyIsOurs("Beta", { mods: [] }), true);
  assert.strictEqual(context.scanReplyIsOurs("Alpha", { mods: [] }), false);

  // A bare array is the oldest shape of all.
  assert.strictEqual(context.scanReplyIsOurs("Beta", []), true);
});

test("the scan asks the question before it writes anything down", () => {
  const body = fn("async function scanMods(");
  const asked = body.indexOf("const forRealm=S.profileName");
  const guarded = body.indexOf("scanReplyIsOurs(forRealm,r)");
  const cleared = body.indexOf("S.modsScanning=false;");
  const rows = body.indexOf("S.mods=rows;");

  assert.ok(asked > 0, "the scan no longer records which realm it is for");
  assert.ok(guarded > asked, "the reply is not checked against the realm that asked");
  assert.ok(guarded < cleared,
    "the busy flag is put down before the reply is checked, so a late reply clears the "
    + "flag the NEW realm's own scan is holding");
  assert.ok(guarded < rows, "the rows are written before the reply is checked");
});

test("the reply really carries the realm the host side read", () => {
  const from = HOST.indexOf("RegisterRpc(\"mods.scan\"");
  assert.ok(from > 0, "the bridge no longer registers mods.scan");
  const chunk = HOST.slice(from, from + 9000);
  assert.ok(chunk.indexOf("profile = CurrentProfile") > 0,
    "the scan reply no longer says which realm it is about");
});

/* ------------------------------------------- rule 6: the dials are keyed on the realm too */
test("the held difficulty set is about this realm's world rather than any world of that name", () => {
  const body = fn("function worldModsHeldFor(");
  const context = { console };
  vm.createContext(context);
  vm.runInContext(
    "var S={profileName:'Beta',worldMods:null};" + body
    + "\n}\nthis.worldModsHeldFor=worldModsHeldFor;this.S=S;",
    context, { filename: "app.js#worldModsHeldFor" });

  context.S.worldMods = { world: "Eikthyrnir", profile: "Alpha", pulled: true };
  assert.strictEqual(context.worldModsHeldFor("Eikthyrnir"), false,
    "a set abandoned on another realm is still read as this realm's");

  context.S.worldMods = { world: "Eikthyrnir", profile: "Beta", pulled: true };
  assert.strictEqual(context.worldModsHeldFor("Eikthyrnir"), true);
  assert.strictEqual(context.worldModsHeldFor("Somewhere Else"), false);

  // A set written before the realm was known is still this realm's: the name is the only
  // thing it ever claimed, and the switch drops the set anyway.
  context.S.worldMods = { world: "Eikthyrnir", pulled: true };
  assert.strictEqual(context.worldModsHeldFor("Eikthyrnir"), true);
});

test("both decisions about the dials go through that one question", () => {
  const render = fn("async function renderWorldMods(");
  assert.ok(render.indexOf("worldModsHeldFor(world)") > 0,
    "the card still re-paints a held set keyed on the world name alone");

  // The save guard reads it too, which is the one that writes to disk.
  assert.ok(SOURCE.indexOf("worldModsHeldFor(prefs.WorldName)") > 0,
    "Save Config still writes a held set keyed on the world name alone");
});

/* ------------------------------------------- rule 7: the possibly-outdated column */
test("the possibly-outdated column is decided by the whole list, not by the search", () => {
  const body = fn("function renderMods(");
  const at = body.indexOf("po-empty");
  assert.ok(at > 0, "the possibly-outdated column no longer hides itself");

  const line = body.slice(at, at + 200);
  assert.ok(/!mods\.some\(/.test(line),
    "the column appears and disappears as the host types, because it is counted over the "
    + "rows a search has narrowed it to");
  assert.ok(!/!shown\.some\(/.test(line));
});

/* ------------------------------------------- rule 8: the Saga line tells the two roads apart

   What this rule is really about is the LINE BEING TRUE: a scan the page starts for itself
   reuses the listing it already holds, and the log used to claim the network read either
   way, so a host reading the Saga after a realm switch saw "reading the community listing
   index" against an application log with no request in it for a quarter of an hour.

   It was first fixed by giving the two roads a catalog id each. Batch E took the ids back
   out: the Saga log is English and verbatim on every install, because a log line is a record
   the host pastes to somebody else, and WebUiDialogCopyTests holds that decision for the
   whole file. The two sentences are English literals here now and the two roads are still
   told apart, which is the half that matters. */
test("the scan's two Saga lines are English, and still say which road was taken", () => {
  const body = fn("async function scanMods(");
  const call = body.slice(body.indexOf('logLine("info","[Thunderstore] "+(force'));
  assert.ok(call.indexOf('?"reading the community listing index"') > 0,
    "a host press no longer says it went to the site");
  assert.ok(call.indexOf('?"reading the mod folder, and the community listing already held"') > 0
         || call.indexOf(':"reading the mod folder, and the community listing already held"') > 0,
    "a scan the page starts for itself no longer says it reused what it holds");

  // and neither of them is a lookup any more, on either side
  assert.ok(body.indexOf("mods.scan.saga.asking_site") < 0,
    "the Saga line is asked for by id again");
  assert.ok(body.indexOf("mods.scan.saga.reusing_listing") < 0,
    "the Saga line is asked for by id again");
  assert.ok(!KEYS["mods.scan.saga.asking_site"], "the id is still in the catalog");
  assert.ok(!KEYS["mods.scan.saga.reusing_listing"], "the id is still in the catalog");
});

/* ------------------------------------------- rule 9: the last save time is formatted once */
test("the last save time is written by the lookup's own formatter", () => {
  const body = fn("function renderLastSave(");
  assert.ok(body.indexOf("fmtT(S.lastSaveAt)") > 0,
    "the one clock on this card is built by hand, so it reads in a notation no other time "
    + "in the window uses");
  assert.ok(body.indexOf("pad(S.lastSaveAt.getHours())") < 0);
});

console.log("");
if (failures.length) {
  console.log("realm switch state selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("realm switch state selftest: " + passed + " passed");
