/* The Duplicate dialog's difficulty dials, driven for real.
 *
 * WHY THIS EXISTS. With Copy the world on, the dialog opened its five dials on Normal and sent
 * only the dials that were moved. The copy's own header was imported first on the host side,
 * so a dial left on Normal said nothing, a Casual source stayed Casual, and Normal could not be
 * chosen at all. The host also had no way to see which fields of the duplicate differ from the
 * realm it came from.
 *
 * THE CODE UNDER TEST is the block between the DUPLICATE-DIALS markers in app.js, evaluated
 * here beside the page's own WORLDGEN_HELP table, WORLDGEN, wgOpt and worldGenAnswered, all cut
 * out of app.js as they stand. The sentences come out of the real en.json, and a T() call for
 * an id the catalog does not have fails the rule it is in.
 *
 * The rules:
 *   1. A worldgen.get answer becomes the five source dials, "" for Normal; an answer that is
 *      not the whole stored set, a preset world, or a value the table does not know gives null.
 *   2. The source is read with worldgen.get only from the realm the dialog was opened from, and
 *      never in the browser preview.
 *   3. With the source read, every dial is sent and Normal goes as "normal", a word the host
 *      side's own parser takes as a choice; without it only the moved dials are sent.
 *   4. Seeding moves the dials the host has not touched, fires change so the sentence under
 *      each one repaints, and a forced reset puts every dial back on Normal.
 *   5. A dial changed away from the source is marked and names the source's value; one that
 *      matches is not; with the copy off no dial is marked.
 *   6. Name, password, world and listing are marked only when they differ from the source.
 *   7. The wiring in addServerProfile: the notes in the markup, the payload, the switch, the
 *      refusal and the read on open.
 *   8. The host side: servers.create parses the explicit Normals and hands them to the merge
 *      after the first meeting, and the merge takes them off the world.
 *   9. Turning Copy the world off, run through the real dupSyncCopyDials on the stand-in
 *      dialog: every dial back on Normal, no dial marked and no source note showing; turned
 *      back on, the source's values return. The dialog's paintMarks and syncCopyDials pass the
 *      live state of the switch rather than a fixed answer.
 *  10. A copy kept under the source's own world name, typed or taken from the realm name with
 *      the World box blank, is refused before anything is sent when a dial differs from the
 *      source, and servers.create refuses the same case by name instead of dropping the Normals.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/duplicate_dials_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const WEB = path.join(ROOT, "ValheimBakaLoader", "WebUI");
// Read as LF whatever the checkout wrote: a Windows checkout with core.autocrlf hands app.js over
// with CRLF, and every slice below ends on a bare line feed.
const APP = fs.readFileSync(path.join(WEB, "app.js"), "utf8").replace(/\r\n/g, "\n");
const KEYS = JSON.parse(fs.readFileSync(path.join(WEB, "i18n", "en.json"), "utf8")).keys || {};
const BRIDGE = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"), "utf8");
const WORLDGEN_CS = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Game", "WorldGen.cs"), "utf8");

let passed = 0;
const failures = [];

async function test(name, body) {
  try {
    await body();
    passed++;
    console.log("  ok   " + name);
  } catch (problem) {
    failures.push(name);
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}

function slice(fromText, toText) {
  const from = APP.indexOf(fromText);
  assert.ok(from >= 0, "app.js no longer carries " + fromText);
  const to = APP.indexOf(toText, from);
  assert.ok(to > from, "app.js no longer ends " + fromText + " with " + toText);
  return APP.slice(from, to + toText.length);
}

/* A statement that starts with fromText and ends at the first line that is exactly "};". */
function statement(fromText) {
  const from = APP.indexOf(fromText);
  assert.ok(from >= 0, "app.js no longer carries " + fromText);
  const end = APP.indexOf("\n};", from);
  assert.ok(end > from, "could not find the end of " + fromText);
  return APP.slice(from, end + 3);
}

function T(id, params) {
  const entry = KEYS[id];
  if (!entry || typeof entry.lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return entry.lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

// ------------------------------------------------------------------ a small stand-in DOM

class ClassList {
  constructor() { this.set = new Set(); }
  add(c) { this.set.add(c); }
  remove(c) { this.set.delete(c); }
  contains(c) { return this.set.has(c); }
  toggle(c, force) {
    const want = force === undefined ? !this.set.has(c) : !!force;
    if (want) this.set.add(c); else this.set.delete(c);
    return want;
  }
}

class El {
  constructor(id, kind, parent) {
    this.id = id; this.kind = kind; this.parent = parent || null;
    this.value = ""; this.textContent = ""; this.style = {};
    this.classList = new ClassList();
    this.listeners = {};
    this.fired = [];
  }
  closest(sel) {
    for (let at = this; at; at = at.parent)
      if (sel === "." + at.kind) return at;
    return null;
  }
  addEventListener(type, fn) { (this.listeners[type] = this.listeners[type] || []).push(fn); }
  dispatchEvent(ev) {
    this.fired.push(ev.type);
    (this.listeners[ev.type] || []).forEach(fn => fn(ev));
    return true;
  }
}

/* The Duplicate dialog's controls, laid out the way addServerProfile builds them. */
function dialog(sandbox) {
  const byId = {};
  const add = (id, kind, parent) => (byId[id] = new El(id, kind, parent));
  const field = id => new El(id + "_field", "field", null);
  const nameF = field("name"); add("wsName", "input", nameF); add("wsNameSrc", "note", nameF);
  const passF = field("pass"); add("wsPass", "input", passF); add("wsPassSrc", "note", passF);
  const worldF = field("world"); add("wsWorld", "input", worldF); add("wsWorldSrc", "note", worldF);
  const pubRow = new El("pub_row", "togglerow", null);
  add("wsPublic", "toggle", pubRow); add("wsPublicSrc", "note", null);
  for (const key of Object.keys(sandbox.WORLDGEN)) {
    const f = field(key);
    add("wsMod_" + key, "select", f);
    add("wsSrc_" + key, "note", f);
  }
  return {
    byId,
    querySelector(sel) { return sel.startsWith("#") ? byId[sel.slice(1)] || null : null; },
  };
}

function sandboxFor() {
  const code = [
    statement("const WORLDGEN_HELP={"),
    slice("const WORLDGEN=Object.fromEntries(", "]));"),
    slice("const wgOpt=", ";\n"),
    slice("function worldGenAnswered(r){", "\n}"),
    slice("/* DUPLICATE-DIALS-BEGIN", "/* DUPLICATE-DIALS-END */"),
    "this.WORLDGEN=WORLDGEN;",
  ].join("\n");
  const calls = [];
  const sandbox = {
    T, FAIL: { fail: true }, calls,
    Native: { available: true },
    S: { profileName: "Source" },
    rpc: async (method, params) => { calls.push({ method, params }); return sandbox.reply; },
    reply: null,
    Event: class { constructor(type) { this.type = type; } },
    console,
  };
  vm.createContext(sandbox);
  /* The page's one show/hide helper, loaded the way the Discord selftests load it: the code under
     test reveals and hides through it since the sweep. */
  vm.runInContext(APP.slice(APP.indexOf("/* REVEAL-BEGIN"), APP.indexOf("/* REVEAL-END */")), sandbox, { filename: "app.js#reveal" });
  vm.runInContext(code + "\nthis.api={dupSourceDials,dupReadSourceDials,dupDialsPayload,dupSetDials,dupPaintMarks,dupSyncCopyDials,dupEffectiveWorld,dupSameWorldProblem,DUP_NORMAL};", sandbox);
  return sandbox;
}

const answer = mods => ({ world: "Midgard", preset: "", modifiers: mods, keys: [], switches: [], passThrough: [], imported: null });
const SOURCE = { profile: "Source", world: "Midgard", password: "hunter22", listed: true };

(async () => {
  const sb = sandboxFor();
  const api = sb.api;
  const DIALS = Object.keys(sb.WORLDGEN);

  await test("1. a worldgen.get answer becomes the five source dials, and a doubtful one becomes none", () => {
    assert.deepStrictEqual(DIALS.slice().sort(), ["combat", "deathpenalty", "portals", "raids", "resources"]);
    const read = api.dupSourceDials(answer({ deathpenalty: "casual", combat: "hard" }));
    assert.strictEqual(read.deathpenalty, "casual");
    assert.strictEqual(read.combat, "hard");
    assert.strictEqual(read.resources, "");
    assert.strictEqual(Object.keys(read).length, 5);
    assert.strictEqual(api.dupSourceDials(sb.FAIL), null, "a failed call");
    assert.strictEqual(api.dupSourceDials({}), null, "a bare object is not the stored set");
    assert.strictEqual(api.dupSourceDials({ ...answer({}), preset: "hard" }), null, "a preset world");
    assert.strictEqual(api.dupSourceDials(answer({ combat: "brutal" })), null, "a value the table does not know");
    const normal = api.dupSourceDials({ ...answer({ raids: "normal" }), preset: "normal" });
    assert.strictEqual(normal.raids, "");
  });

  await test("2. the source is read only from the realm it came from, and never in the preview", async () => {
    sb.calls.length = 0;
    sb.reply = answer({ deathpenalty: "casual" });
    const read = await api.dupReadSourceDials(SOURCE);
    assert.strictEqual(read.deathpenalty, "casual");
    assert.deepStrictEqual(JSON.parse(JSON.stringify(sb.calls)), [{ method: "worldgen.get", params: { world: "Midgard" } }]);
    sb.calls.length = 0;
    sb.S.profileName = "Elsewhere";
    assert.strictEqual(await api.dupReadSourceDials(SOURCE), null);
    sb.S.profileName = "Source";
    sb.Native.available = false;
    assert.strictEqual(await api.dupReadSourceDials(SOURCE), null);
    sb.Native.available = true;
    assert.strictEqual(await api.dupReadSourceDials({ profile: "Source", world: "" }), null);
    assert.strictEqual(sb.calls.length, 0, "nothing should have been asked: " + JSON.stringify(sb.calls));
  });

  await test("3. with the source read every dial is sent and Normal is the word the host side takes", () => {
    const src = api.dupSourceDials(answer({ deathpenalty: "casual" }));
    const values = { combat: "hard", deathpenalty: "", resources: "", raids: "", portals: "" };
    const sent = api.dupDialsPayload(values, src);
    assert.deepStrictEqual(Object.keys(sent).sort(), DIALS.slice().sort());
    assert.strictEqual(sent.deathpenalty, "normal");
    assert.strictEqual(sent.combat, "hard");
    assert.deepStrictEqual(JSON.parse(JSON.stringify(api.dupDialsPayload(values, null))), { combat: "hard" });
    // The word is the one the host side's own parser reads as a choice, and no dial treats it
    // as an ordinary value.
    const parser = BRIDGE.slice(BRIDGE.indexOf("public static HashSet<string> ParseExplicitNormalDials"));
    assert.ok(parser.length > 0, "the bridge no longer has ParseExplicitNormalDials");
    assert.ok(parser.slice(0, 800).includes('value != "' + api.DUP_NORMAL + '"'),
      "ParseExplicitNormalDials does not read the word the page sends");
    const table = WORLDGEN_CS.slice(WORLDGEN_CS.indexOf("Modifiers = new Dictionary"), WORLDGEN_CS.indexOf("Switches = new"));
    assert.ok(!table.includes('"normal"'), "a dial lists normal as an ordinary value");
  });

  await test("4. seeding moves the untouched dials, repaints them, and a reset puts all on Normal", () => {
    const m = dialog(sb);
    const src = api.dupSourceDials(answer({ deathpenalty: "casual", portals: "hard" }));
    m.byId.wsMod_portals.value = "veryhard"; // the host got there first
    api.dupSetDials(m, src, new Set(["portals"]), false);
    assert.strictEqual(m.byId.wsMod_deathpenalty.value, "casual");
    assert.deepStrictEqual(m.byId.wsMod_deathpenalty.fired, ["change"], "the sentence under the dial was not repainted");
    assert.strictEqual(m.byId.wsMod_portals.value, "veryhard", "a dial the host moved was moved again");
    assert.deepStrictEqual(m.byId.wsMod_combat.fired, [], "a dial that did not move fired change");
    api.dupSetDials(m, null, null, true);
    for (const key of DIALS) assert.strictEqual(m.byId["wsMod_" + key].value, "", key + " is not back on Normal");
  });

  await test("5. a dial changed away from the source is marked with the source's value, and only then", () => {
    const m = dialog(sb);
    const src = api.dupSourceDials(answer({ deathpenalty: "casual" }));
    api.dupSetDials(m, src, null, true);
    api.dupPaintMarks(m, SOURCE, src, true);
    for (const key of DIALS)
      assert.ok(!m.byId["wsMod_" + key].parent.classList.contains("field-dirty"), key + " is marked on the source's own value");
    m.byId.wsMod_deathpenalty.value = "";
    api.dupPaintMarks(m, SOURCE, src, true);
    assert.ok(m.byId.wsMod_deathpenalty.parent.classList.contains("field-dirty"));
    const casual = sb.WORLDGEN.deathpenalty.opts.find(([v]) => v === "casual")[1];
    assert.strictEqual(m.byId.wsSrc_deathpenalty.textContent, T("realm.duplicate.source", { value: T(casual) }));
    assert.ok(/^source: /.test(m.byId.wsSrc_deathpenalty.textContent));
    assert.strictEqual(m.byId.wsSrc_deathpenalty.style.display, "");
    assert.ok(!m.byId.wsMod_combat.parent.classList.contains("field-dirty"));
    assert.strictEqual(m.byId.wsSrc_combat.style.display, "none");
    // Copy switched off: nothing about the dials is marked.
    api.dupPaintMarks(m, SOURCE, src, false);
    for (const key of DIALS) {
      assert.ok(!m.byId["wsMod_" + key].parent.classList.contains("field-dirty"), key + " still marked with the copy off");
      assert.strictEqual(m.byId["wsSrc_" + key].textContent, "");
    }
    // And with the source unread nothing is marked either: there is nothing to compare to.
    m.byId.wsMod_combat.value = "hard";
    api.dupPaintMarks(m, SOURCE, null, true);
    assert.ok(!m.byId.wsMod_combat.parent.classList.contains("field-dirty"));
  });

  await test("6. name, password, world and listing are marked only when they differ from the source", () => {
    const m = dialog(sb);
    const b = m.byId;
    b.wsPass.value = SOURCE.password;
    b.wsPublic.classList.add("on");
    api.dupPaintMarks(m, SOURCE, null, true);
    for (const id of ["wsName", "wsPass", "wsWorld"])
      assert.ok(!b[id].parent.classList.contains("field-dirty"), id + " marked while it matches or is empty");
    assert.ok(!b.wsPublic.parent.classList.contains("field-dirty"));
    b.wsName.value = "Copy of Source";
    b.wsPass.value = "another1";
    b.wsWorld.value = "Midgard2";
    b.wsPublic.classList.remove("on");
    api.dupPaintMarks(m, SOURCE, null, true);
    assert.strictEqual(b.wsNameSrc.textContent, T("realm.duplicate.source", { value: "Source" }));
    assert.strictEqual(b.wsPassSrc.textContent, T("realm.duplicate.source", { value: "hunter22" }));
    assert.strictEqual(b.wsWorldSrc.textContent, T("realm.duplicate.source", { value: "Midgard" }));
    assert.strictEqual(b.wsPublicSrc.textContent,
      T("realm.duplicate.source", { value: T("realm.duplicate.source.listed") }));
    assert.ok(b.wsPublic.parent.classList.contains("field-dirty"), "the listing row is not marked");
    for (const id of ["wsName", "wsPass", "wsWorld"]) assert.ok(b[id].parent.classList.contains("field-dirty"), id);
    // A source with no password names that rather than an empty slot.
    b.wsPass.value = "x1234";
    api.dupPaintMarks(m, { ...SOURCE, password: "" }, null, true);
    assert.strictEqual(b.wsPassSrc.textContent, T("realm.duplicate.source", { value: T("realm.duplicate.source.none") }));
    // A brand new realm is not a duplicate and marks nothing.
    const fresh = dialog(sb);
    fresh.byId.wsName.value = "Anything";
    api.dupPaintMarks(fresh, undefined, null, false);
    assert.ok(!fresh.byId.wsName.parent.classList.contains("field-dirty"));
    T("realm.duplicate.source.unlisted"); T("realm.duplicate.dials.unread", { world: "Midgard" });
  });

  await test("7. the dialog carries the notes, sends through the payload and follows the switch", () => {
    const fn = slice("async function addServerProfile(source){", "\n}\n");
    for (const id of ["wsNameSrc", "wsPassSrc", "wsWorldSrc", "wsPublicSrc"])
      assert.ok(fn.includes('srcNote("' + id + '")'), "no note for " + id);
    assert.ok(fn.includes('srcNote("wsSrc_"+key)'), "no note under the dials");
    assert.ok(/return dupDialsPayload\(values,copyingNow\(\)\?srcDials:null\)/.test(fn), "the payload does not go through dupDialsPayload");
    assert.ok(/copyT\.addEventListener\("click",\(\)=>\{copyT\.classList\.toggle\("on"\);paintWorldSection\(\);syncCopyDials\(\);\}\)/.test(fn),
      "the copy switch does not move the dials");
    const refusal = fn.slice(fn.indexOf('rpc("servers.copyCheck"'), fn.indexOf("[iso,seed,saveIso]"));
    assert.ok(refusal.includes("syncCopyDials();"), "a refused copy leaves the source's dials up");
    assert.ok(fn.includes("dupReadSourceDials(source).then("), "the source is not read when the dialog opens");
    assert.ok(/if\(read&&copyingNow\(\)\) dupSetDials\(m,read,touched,false\)/.test(fn), "a late read can move a dial the host moved");
    assert.ok(fn.includes('sel.addEventListener("input",()=>{touched.add(key);paintMarks();});'), "the host's own pick is not remembered");
    assert.ok(!/if\(v\)mods\[key\]=v/.test(fn), "the old Normal-dropping read is still in the dialog");
  });

  await test("8. servers.create hands the explicit Normals to the merge after the first meeting", () => {
    const from = BRIDGE.indexOf('RegisterRpc("servers.create"');
    const to = BRIDGE.indexOf("RegisterRpc(", from + 10);
    const handler = BRIDGE.slice(from, to);
    const parse = handler.indexOf('ParseExplicitNormalDials(p["modifiers"] as JObject)');
    const meet = handler.indexOf("ImportWorldKeysOnFirstMeeting(world, created.SaveDataFolderPath)");
    const merge = handler.indexOf("ApplyForgeWorldChoices(WorldPrefsProvider, world, worldModifiers, worldNormals, worldSwitches)");
    assert.ok(!handler.includes("worldNormals.Clear()"), "servers.create drops the explicit Normals without a word");
    assert.ok(parse > 0, "the explicit Normals are not parsed");
    assert.ok(meet > 0 && merge > meet, "the merge does not follow the first meeting");
    const body = BRIDGE.slice(BRIDGE.indexOf("public static WorldPreferences ApplyForgeWorldChoices("));
    assert.ok(/foreach \(var key in normals\) worldPrefs\.Modifiers\.Remove\(key\)/.test(body.slice(0, 2000)),
      "the merge does not take an explicit Normal off the world");
  });

  await test("9. turning the copy off puts every dial back on Normal and clears the marks, and on brings the source back", () => {
    const m = dialog(sb);
    const b = m.byId;
    const src = api.dupSourceDials(answer({ deathpenalty: "casual", combat: "hard" }));
    const touched = new Set();
    api.dupSyncCopyDials(m, SOURCE, src, true, touched);
    assert.strictEqual(b.wsMod_deathpenalty.value, "casual");
    assert.strictEqual(b.wsMod_combat.value, "hard");
    // The host moves one dial away from the source, so it is marked.
    b.wsMod_deathpenalty.value = "";
    touched.add("deathpenalty");
    api.dupPaintMarks(m, SOURCE, src, true);
    assert.ok(b.wsMod_deathpenalty.parent.classList.contains("field-dirty"), "the moved dial is not marked");
    // Copy the world off.
    api.dupSyncCopyDials(m, SOURCE, src, false, touched);
    for (const key of DIALS) {
      assert.strictEqual(b["wsMod_" + key].value, "", key + " kept a value with the copy off");
      assert.ok(!b["wsMod_" + key].parent.classList.contains("field-dirty"), key + " is still marked with the copy off");
      assert.notStrictEqual(b["wsSrc_" + key].style.display, "", key + " still shows its source note with the copy off");
    }
    assert.strictEqual(touched.size, 0, "the host's earlier picks were not forgotten");
    // And back on.
    api.dupSyncCopyDials(m, SOURCE, src, true, touched);
    assert.strictEqual(b.wsMod_deathpenalty.value, "casual");
    assert.strictEqual(b.wsMod_combat.value, "hard");
    for (const key of DIALS)
      assert.ok(!b["wsMod_" + key].parent.classList.contains("field-dirty"), key + " marked on the source's own value");
    // The dialog runs exactly this, with the live state of the switch.
    const fn = slice("async function addServerProfile(source){", "\n}\n");
    assert.ok(fn.includes("const paintMarks=()=>dupPaintMarks(m,source,srcDials,copyingNow());"),
      "paintMarks does not pass the live state of the copy switch");
    assert.ok(fn.includes("const syncCopyDials=()=>dupSyncCopyDials(m,source,srcDials,copyingNow(),touched);"),
      "syncCopyDials does not run dupSyncCopyDials with the live state of the copy switch");
  });

  await test("10. a copy under the source's own world name with a changed dial is refused before it is sent", () => {
    // The world name servers.create will use, the way MakeSafeName makes it.
    assert.strictEqual(api.dupEffectiveWorld("", "Midgard"), "Midgard");
    assert.strictEqual(api.dupEffectiveWorld("  Other ", "Midgard"), "Other");
    assert.strictEqual(api.dupEffectiveWorld("", "a:b/c?"), "a_b_c_");
    assert.strictEqual(api.dupEffectiveWorld("", " .. "), "server");
    const make = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Tools", "InstallIsolationService.cs"), "utf8");
    const body = make.slice(make.indexOf("public static string MakeSafeName("), make.indexOf("private static bool SameVolume"));
    assert.ok(body.includes("Path.GetInvalidFileNameChars()") && body.includes("Trim('.', ' ')") && body.includes('"server"'),
      "MakeSafeName changed, so dupEffectiveWorld has to be checked against it again");

    const m = dialog(sb);
    const b = m.byId;
    const src = api.dupSourceDials(answer({ deathpenalty: "casual" }));
    api.dupSyncCopyDials(m, SOURCE, src, true, new Set());
    // Every dial on the source's value: nothing to refuse, even under the source's world name.
    assert.strictEqual(api.dupSameWorldProblem(m, SOURCE, src, true, "", "Midgard"), null);
    // Normal chosen on purpose, World box blank, realm named after the source world.
    b.wsMod_deathpenalty.value = "";
    assert.strictEqual(api.dupSameWorldProblem(m, SOURCE, src, true, "", "Midgard"),
      T("realm.duplicate.same_world_dials", { world: "Midgard" }));
    // Typed, in another case.
    assert.strictEqual(api.dupSameWorldProblem(m, SOURCE, src, true, "midgard", "Anything"),
      T("realm.duplicate.same_world_dials", { world: "midgard" }));
    // Another world name, or the copy off: fine.
    assert.strictEqual(api.dupSameWorldProblem(m, SOURCE, src, true, "Midgard2", "Midgard"), null);
    assert.strictEqual(api.dupSameWorldProblem(m, SOURCE, src, false, "", "Midgard"), null);
    // Source unread: a moved dial is a change, none moved is not.
    const fresh = dialog(sb);
    assert.strictEqual(api.dupSameWorldProblem(fresh, SOURCE, null, true, "", "Midgard"), null);
    fresh.byId.wsMod_combat.value = "hard";
    assert.ok(api.dupSameWorldProblem(fresh, SOURCE, null, true, "", "Midgard"));

    // The dialog asks before it sends, and the host side refuses by the same name.
    const fn = slice("async function addServerProfile(source){", "\n}\n");
    const ask = fn.indexOf("dupSameWorldProblem(m,source,srcDials,copyingNow(),world,name)");
    assert.ok(ask > 0 && ask < fn.indexOf('rpc("servers.create",payload)'), "the dialog does not ask before it sends");
    assert.ok(fn.includes("if(sameWorld){statusN.textContent=sameWorld;worldI.focus();return;}"),
      "the dialog asks and then sends anyway");
    assert.ok(APP.includes('{named:"servers.create.copySameWorldDials", textId:"realm.duplicate.same_world_dials"}'),
      "the host side's refusal has no sentence on the page");
    const from = BRIDGE.indexOf('RegisterRpc("servers.create"');
    const handler = BRIDGE.slice(from, BRIDGE.indexOf("RegisterRpc(", from + 10));
    const refuse = handler.indexOf('"servers.create.copySameWorldDials"');
    assert.ok(refuse > 0 && refuse < handler.indexOf("WorldStore.CopyWorldAs("), "servers.create does not refuse before it copies");
  });

  console.log(failures.length === 0
    ? `duplicate dials selftest: ${passed} passed`
    : `duplicate dials selftest: ${failures.length} failed, ${passed} passed`);
  process.exit(failures.length === 0 ? 0 : 1);
})();
