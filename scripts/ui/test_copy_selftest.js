/* Make a test copy (issue 20), driven for real.
 *
 * WHY THIS EXISTS. A test copy is a realm a host makes to try a game or mod update on, and
 * the whole point of it is that it stays apart from the realm it came from: its own name, its
 * own world, never listed in the server browser, and, if the host wants one, a copy of a
 * character rather than the character itself. Each of those is a line in the dialog that a
 * later edit could quietly drop, and the dialog would still open and still forge something.
 *
 * THE CODE UNDER TEST is the block between the TEST-COPY markers in app.js, evaluated with
 * the page's own esc, markSwitch and gateDialogSwitch cut out of app.js beside it, and the
 * chip's badge out of the CHIPLOOK block. The sentences come out of the real en.json, and a
 * T() call for an id the catalog does not have fails the rule it is in.
 *
 *   1. The names a test copy is offered under: source_bakaT for the realm, world_bakaT for the
 *      world, and source_bakaT2 and on when a realm already has the name.
 *   2. A name another realm has, whatever its case, is refused with the catalog's sentence; a
 *      free one and an empty one are not (the empty one is the dialog's own rule).
 *   3. The listing switch is held off: not on, out of the pointer's and the keyboard's reach,
 *      with the reason under it, and the payload goes with the source named and public false
 *      whatever the switch said.
 *   4. The character drop down: no character first, a readable one offered under its copy's
 *      name, one whose copy is already there offered and refused with a sentence naming the
 *      file, one that could not be read shown and not pickable.
 *   5. The chip of a test copy carries the T and says whose copy it is, a copy whose source is
 *      gone says so, and a realm that is not a test copy draws exactly as before; the title bar
 *      line says the same.
 *   6. The wiring: the chip menu offers it, it opens the Duplicate dialog with test set, the
 *      dialog uses this block's names, lock, refusals and payload, and copies the character
 *      only after the realm stands.
 *   7. The host side: servers.create takes testCopyOf, refuses a source that is gone, holds
 *      the listing off, seeds the install from the source's BepInEx by name; servers.list, a
 *      settings save and a rename each carry the mark; and every refusal the character copy
 *      can throw is worded in HOST_SENTENCES.
 *   8. The settings page's listing switch shows off for a test copy and is stopped before the
 *      shared switch handler can flip it.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/test_copy_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const WEB = path.join(ROOT, "ValheimBakaLoader", "WebUI");
const APP = fs.readFileSync(path.join(WEB, "app.js"), "utf8").replace(/\r\n/g, "\n");
const HTML = fs.readFileSync(path.join(WEB, "index.html"), "utf8").replace(/\r\n/g, "\n");
const CSS = fs.readFileSync(path.join(WEB, "app.css"), "utf8").replace(/\r\n/g, "\n");
const KEYS = JSON.parse(fs.readFileSync(path.join(WEB, "i18n", "en.json"), "utf8")).keys || {};
const read = rel => fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", rel), "utf8").replace(/\r\n/g, "\n");
const BRIDGE = read("Forms/BlendWindow.Bridge.cs");
const TESTCOPY_CS = read("Forms/BlendWindow.TestCopy.cs");
const COPIER = read("Tools/CharacterCopy.cs");
const PREFS = read("Game/ServerPreferences.cs");
const OPTIONS = read("Game/ValheimServerOptions.cs");

let passed = 0;
const failures = [];
function test(name, body) {
  try { body(); passed++; console.log("  ok   " + name); }
  catch (problem) {
    failures.push(name);
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}
function between(from, to) {
  const a = APP.indexOf(from);
  assert.ok(a >= 0, "app.js no longer carries " + from);
  const b = APP.indexOf(to, a + from.length);
  assert.ok(b > a, "app.js no longer carries " + to + " after " + from);
  return APP.slice(a, b);
}
/* One top level function out of app.js, from its opening line to the brace at the margin. */
function fn(opening) {
  const a = APP.indexOf(opening);
  assert.ok(a >= 0, "app.js no longer holds " + opening);
  const b = APP.indexOf("\n}", a);
  assert.ok(b > a, opening + " no longer closes at the left margin");
  return APP.slice(a, b + 2);
}
function line(prefix) {
  const a = APP.indexOf(prefix);
  assert.ok(a >= 0, "app.js no longer carries " + prefix);
  return APP.slice(a, APP.indexOf("\n", a));
}
function T(id, params) {
  const entry = KEYS[id];
  if (!entry || typeof entry.lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return entry.lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

// ------------------------------------------------------------------ a small stand-in switch

class ClassList {
  constructor(list) { this.set = new Set(list || []); }
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
  constructor(classes) { this.classList = new ClassList(classes); this.style = {}; this.attrs = {}; this.textContent = ""; this.title = ""; }
  setAttribute(k, v) { this.attrs[k] = String(v); }
  getAttribute(k) { return k in this.attrs ? this.attrs[k] : null; }
}

function page() {
  const context = { T };
  vm.createContext(context);
  const code = [
    line("const esc="),
    line("const WORLD_NAME_MAX="),
    fn("function markSwitch(t){"),
    fn("function gateDialogSwitch(t){"),
    between("/* CHIPLOOK-BEGIN", "/* CHIPLOOK-END */"),
    between("/* TEST-COPY-BEGIN", "/* TEST-COPY-END */"),
  ].join("\n").replace(/^const /gm, "var ");
  vm.runInContext(code, context, { filename: "app.js#testcopy" });
  for (const name of ["testCopyName", "testCopyWorld", "testCopyNameProblem", "testCopyLockPublic",
    "testCopyCharacterOptions", "testCopyCharacterProblem", "testCopyCharacterNote", "testCopyPayload",
    "testCopyHead", "chipTestBadge", "chipHtml"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the blocks");
  return context;
}

const SERVERS = [{ name: "Second Sunset" }, { name: "Home" }];

test("1. the names: source_bakaT, world_bakaT, and a number when the name is taken", () => {
  const p = page();
  assert.strictEqual(vm.runInContext("TEST_COPY_SUFFIX", p), "_bakaT");
  assert.strictEqual(p.testCopyName("Second Sunset", SERVERS), "Second Sunset_bakaT");
  assert.strictEqual(p.testCopyName("  Second Sunset ", SERVERS), "Second Sunset_bakaT");
  assert.strictEqual(p.testCopyWorld("Sunset"), "Sunset_bakaT");
  assert.strictEqual(p.testCopyWorld(""), "");
  // A source world near the limit is cut so the suffix fits: the dialog must not open on a
  // world name its own check turns down as too long.
  const max = vm.runInContext("WORLD_NAME_MAX", p);
  assert.strictEqual(typeof max, "number");
  const long = "W".repeat(max - 2);
  const offered = p.testCopyWorld(long);
  assert.ok(offered.length <= max, "the world prefill is " + offered.length + " long, over " + max);
  assert.ok(offered.endsWith("_bakaT"), offered);
  assert.strictEqual(offered, "W".repeat(max - 6) + "_bakaT");
  assert.strictEqual(p.testCopyWorld("W".repeat(max - 6)), "W".repeat(max - 6) + "_bakaT");
  // A cut that would leave a trailing space or dot before the suffix drops it.
  assert.strictEqual(p.testCopyWorld("W".repeat(max - 7) + " XYZ"), "W".repeat(max - 7) + "_bakaT");
  const taken = SERVERS.concat([{ name: "second sunset_BAKAT" }, { name: "Second Sunset_bakaT2" }]);
  assert.strictEqual(p.testCopyName("Second Sunset", taken), "Second Sunset_bakaT3",
    "the dialog opens on a name another realm already has");
});

test("2. a name another realm has is refused, whatever its case, and a free one is not", () => {
  const p = page();
  const servers = SERVERS.concat([{ name: "Second Sunset_bakaT" }]);
  assert.strictEqual(p.testCopyNameProblem("second sunset_BAKAT", servers),
    T("realm.test.name.taken", { name: "second sunset_BAKAT" }));
  assert.strictEqual(p.testCopyNameProblem("Home", servers), T("realm.test.name.taken", { name: "Home" }));
  assert.strictEqual(p.testCopyNameProblem("Second Sunset_bakaT2", servers), null);
  assert.strictEqual(p.testCopyNameProblem("   ", servers), null, "an empty name is the dialog's own rule");
  assert.ok(/name/.test(KEYS["realm.test.name.taken"].lore));
});

test("3. the listing switch is held off with its reason, and the payload is never public", () => {
  const p = page();
  const sw = new El(["toggle", "on"]);
  const note = new El();
  note.style.display = "none";
  assert.strictEqual(p.testCopyLockPublic(sw, note), true);
  assert.ok(!sw.classList.contains("on"), "the switch is still on");
  assert.strictEqual(sw.style.pointerEvents, "none", "the pointer can still reach the switch");
  assert.strictEqual(sw.getAttribute("tabindex"), "-1", "the keyboard can still reach the switch");
  assert.strictEqual(sw.getAttribute("aria-disabled"), "true");
  assert.strictEqual(sw.getAttribute("aria-checked"), "false");
  assert.strictEqual(note.textContent, T("realm.test.public.locked"));
  assert.strictEqual(note.style.display, "");
  assert.strictEqual(sw.title, T("realm.test.public.locked"));
  assert.ok(/never shows in the server list/.test(T("realm.test.public.locked")));

  const payload = p.testCopyPayload({ name: "x", public: true }, { profile: "Second Sunset", test: true });
  assert.strictEqual(payload.public, false);
  assert.strictEqual(payload.testCopyOf, "Second Sunset");
});

const CHARS = [
  { folder: "characters_local", file: "ragnar.fch", name: "Ragnar", copyName: "Ragnar_bakaT", copyFile: "ragnar_bakat.fch", copyExists: false, problem: null },
  { folder: "characters_local", file: "ylva.fch", name: "Ylva", copyName: "Ylva_bakaT", copyFile: "ylva_bakat.fch", copyExists: true, problem: null },
  { folder: "characters", file: "broken.fch", name: null, copyName: null, copyFile: null, copyExists: false, problem: "notACharacter" },
];

test("4. the character drop down offers, refuses and explains from the list", () => {
  const p = page();
  const options = p.testCopyCharacterOptions(CHARS);
  assert.deepStrictEqual(Array.from(options, o => o.value),
    ["", "characters_local/ragnar.fch", "characters_local/ylva.fch", "characters/broken.fch"]);
  assert.strictEqual(options[0].label, T("realm.test.character.none"));
  assert.strictEqual(options[1].label, T("realm.test.character.option", { name: "Ragnar", copy: "Ragnar_bakaT" }));
  assert.strictEqual(options[1].disabled, false);
  assert.strictEqual(options[2].label, T("realm.test.character.option.exists", { name: "Ylva", copy: "Ylva_bakaT" }));
  assert.strictEqual(options[2].disabled, false, "a character whose copy is there cannot even be picked to be told why");
  assert.strictEqual(options[3].disabled, true, "a file that could not be read can be picked");

  assert.strictEqual(p.testCopyCharacterProblem(CHARS, ""), null);
  assert.strictEqual(p.testCopyCharacterProblem(CHARS, "characters_local/ragnar.fch"), null);
  assert.strictEqual(p.testCopyCharacterProblem(CHARS, "characters_local/ylva.fch"),
    T("realm.test.character.exists", { file: "ylva_bakat.fch", copy: "Ylva_bakaT" }));
  assert.ok(/never writes over a character/.test(T("realm.test.character.exists", { file: "f", copy: "c" })));
  assert.strictEqual(p.testCopyCharacterProblem(CHARS, "characters/broken.fch"),
    T("realm.test.character.unreadable", { file: "broken.fch" }));
  assert.strictEqual(p.testCopyCharacterProblem(CHARS, "characters_local/gone.fch"), T("realm.test.character.gone"));

  assert.strictEqual(p.testCopyCharacterNote(CHARS, ""), T("realm.test.character.none.note"));
  assert.strictEqual(p.testCopyCharacterNote(CHARS, "characters_local/ragnar.fch"),
    T("realm.test.character.note", { name: "Ragnar", copy: "Ragnar_bakaT" }));
  assert.deepStrictEqual(Array.from(p.testCopyCharacterOptions(null), o => o.value), [""]);
});

const initOf = html => (/<span class="srvinit">([^<]*)<\/span>/.exec(html) || [])[1];
const classOf = html => (/^<div class="([^"]*)"/.exec(html) || [])[1];

test("5. the chip carries the T, a gone source says so, and a plain realm draws as before", () => {
  const p = page();
  const plain = { name: "Second Sunset", status: "Stopped", running: false, playersOnline: 0, active: true };
  const before = p.chipHtml(plain);
  assert.strictEqual(classOf(before), "srvchip active");
  assert.ok(!before.includes("srvtest"), "a realm that is not a test copy carries the T");

  const copy = Object.assign({}, plain, { name: "Second Sunset_bakaT", testCopyOf: "Second Sunset", testSourceGone: false, playersOnline: 2 });
  const html = p.chipHtml(copy);
  assert.strictEqual(classOf(html), "srvchip active test");
  assert.strictEqual(initOf(html), "S");
  const badge = /<span class="srvtest" title="([^"]*)">([^<]*)<\/span>/.exec(html);
  assert.ok(badge, "the test copy's chip has no T");
  assert.strictEqual(badge[2], T("realm.test.badge"));
  assert.strictEqual(badge[1], T("realm.test.badge.title", { source: "Second Sunset" }));
  assert.ok(html.includes('<span class="srvbadge">2</span>'), "the T took the player count's place");

  const gone = p.chipHtml(Object.assign({}, copy, { testSourceGone: true }));
  const goneBadge = /<span class="srvtest gone" title="([^"]*)">/.exec(gone);
  assert.ok(goneBadge, "a copy whose source is gone does not say so on its chip");
  assert.strictEqual(goneBadge[1], T("realm.test.badge.title.gone", { source: "Second Sunset" }));

  const evil = p.chipHtml(Object.assign({}, copy, { testCopyOf: "\"><img src=x>" }));
  assert.ok(!evil.includes("<img"), "the source's name reached the markup unescaped");

  assert.strictEqual(p.testCopyHead(copy), T("realm.test.head", { source: "Second Sunset" }));
  assert.strictEqual(p.testCopyHead(Object.assign({}, copy, { testSourceGone: true })), T("realm.test.head.gone"));
  assert.strictEqual(p.testCopyHead(plain), "");
  assert.strictEqual(p.testCopyHead(undefined), "");
  assert.strictEqual(T("realm.test.head", { source: "Second Sunset" }), "test copy of Second Sunset");

  assert.ok(/<span class="srvtesthead" id="tbTest" style="display:none"><\/span>/.test(HTML), "the title bar has no place for the line");
  assert.ok(/\.srvchip \.srvtest\{/.test(CSS) && /\.srvchip \.srvtest\.gone\{/.test(CSS), "the T has no rule in app.css");
  const head = fn("function renderTestCopyHead(){");
  assert.ok(head.includes('$("#tbTest")') && head.includes("testCopyHead((S.servers||[]).find(v=>v&&v.active))"));
  assert.ok(fn("function renderServerChips(){").includes("renderTestCopyHead();"), "the strip does not repaint the title bar line");
});

test("6. the wiring: the menu, the dialog, and the character after the realm", () => {
  const menu = fn("function serverChipMenu(");
  assert.ok(menu.includes('label:T("realm.menu.test_copy"),fn:()=>testCopyServer(name),tip:T("realm.menu.test_copy.tip")'),
    "the chip menu does not offer Make a test copy");
  assert.strictEqual(KEYS["realm.menu.test_copy"].plain, "Make a test copy…");
  assert.ok(!/environment/i.test(KEYS["realm.menu.test_copy"].lore + KEYS["realm.menu.test_copy"].plain));

  const open = fn("async function testCopyServer(name){");
  assert.ok(open.indexOf("switchServer(name)") >= 0 && open.indexOf("switchServer(name)") < open.indexOf("S.prefs.WorldName"),
    "the source is read before the switch");
  assert.ok(/addServerProfile\(\{profile:name,world,[\s\S]*test:true\}\)/.test(open), "the dialog is not told it is a test copy");

  const dialog = fn("async function addServerProfile(source)");
  for (const needle of [
    "const testing=duplicating&&!!source.test;",
    'T("realm.test.title",{name:source.profile})',
    'T("realm.test.ok")',
    "nameI.value=testCopyName(source.profile,S.servers);",
    "worldI.value=testCopyWorld(source.world);",
    'testCopyLockPublic(pubT,m.querySelector("#wsPublicLock"));',
    "if(duplicating&&!testing&&pubT&&source.listed)",
    "const takenTest=testing?testCopyNameProblem(name,S.servers):null;",
    "const charProblem=testing?testCopyCharacterProblem(chars,charPick):null;",
    "if(testing) testCopyPayload(payload,source);",
    'rpc("characters.list",{})',
    'rpc("characters.copy",{folder:pickedChar.folder,file:pickedChar.file})',
    'toast("ᛇ "+T("realm.test.character.done.toast"',
  ]) assert.ok(dialog.includes(needle), "the dialog no longer carries: " + needle);

  /* The refusals come before the forge, and the character copy after the realm stands. */
  const create = dialog.indexOf('rpc("servers.create",payload)');
  assert.ok(dialog.indexOf("takenTest") < create && dialog.indexOf("charProblem") < create, "a refusal comes after the forge");
  assert.ok(dialog.indexOf('rpc("characters.copy"') > dialog.indexOf("await switchServer(created.ProfileName)"),
    "the character is copied before the realm stands");
  /* And the payload's public is the switch's, then held off: the line Duplicate sends is unchanged. */
  assert.ok(dialog.indexOf("password, public:!!(pubT&&on(pubT))") < dialog.indexOf("testCopyPayload(payload,source)"));
});

test("7. the host side: create, list, save, rename, and every refusal worded", () => {
  const create = BRIDGE.slice(BRIDGE.indexOf('RegisterRpc("servers.create"'), BRIDGE.indexOf('RegisterRpc("worlds.listOrphans"'));
  for (const needle of [
    'var testCopyOf = (p.Value<string>("testCopyOf") ?? "").Trim();',
    'HostFacingException("servers.create.testCopySourceMissing"',
    "if (testSource != null) listPublicly = false;",
    "created.TestCopyOf = testSource?.ProfileName;",
    "? GetBepInExDirFor(testSource.ProfileName)",
    "created.Public = listPublicly;",
  ]) assert.ok(create.includes(needle), "servers.create no longer carries: " + needle);
  assert.ok(create.indexOf("testCopySourceMissing") < create.indexOf("SeedNewRealm(basePrefs)"),
    "a missing source is noticed after the realm is seeded");

  const list = BRIDGE.slice(BRIDGE.indexOf("private object BuildServersList()"));
  assert.ok(list.includes("testCopyOf = look?.TestCopyOf,") && list.includes("testSourceGone = "), "servers.list does not hand the mark back");
  assert.ok(BRIDGE.includes("prefs = KeepTestCopyOf(prefs);"), "a settings save can drop the mark");
  assert.ok(BRIDGE.includes("MoveTestCopiesOf(name, newName);"), "a rename of the source leaves its copies naming nothing");
  assert.ok(/created\.TestCopyOf = null;/.test(BRIDGE.slice(BRIDGE.indexOf("public static ServerPreferences SeedNewRealm("))),
    "a plain duplicate of a test copy is a test copy");
  assert.ok(TESTCOPY_CS.includes('RegisterRpc("characters.list"') && TESTCOPY_CS.includes('RegisterRpc("characters.copy"'));
  assert.ok(PREFS.includes("if (TestCopyOf != null) Public = false;"), "a save can list a test copy");
  assert.ok(OPTIONS.includes("Public = serverPrefs.Public && string.IsNullOrWhiteSpace(serverPrefs.TestCopyOf),"),
    "a hand edited test copy starts listed");
  assert.ok(COPIER.includes("FileMode.CreateNew"), "the character copy can write over a file");

  const table = APP.slice(APP.indexOf("const HOST_SENTENCES=["), APP.indexOf("];", APP.indexOf("const HOST_SENTENCES=[")));
  const thrown = [...(COPIER + BRIDGE).matchAll(/HostFacingException\("((?:characters\.copy|servers\.create\.testCopy)[^"]*)"/g)].map(m => m[1]);
  assert.ok(thrown.length >= 7, "only " + thrown.length + " refusals found");
  for (const id of new Set(thrown)) {
    const row = new RegExp('named:"' + id.replace(/\./g, "\\.") + '",\\s*textId:"([^"]+)"').exec(table);
    assert.ok(row, id + " has no row in HOST_SENTENCES, so it is said in English for ever");
    assert.ok(KEYS[row[1]], "the catalog has no " + row[1]);
  }
});

test("8. the settings switch shows off for a test copy and refuses to move", () => {
  const form = fn("function renderWorldForm(){");
  assert.ok(form.includes('setT("tPublic",p.Public&&!p.TestCopyOf);'), "the settings switch shows a test copy as listed");
  const guard = between('$("#tPublic")?.parentElement?.addEventListener("click"', "},true);");
  assert.ok(guard.includes("S.prefs.TestCopyOf") && guard.includes("e.stopPropagation(); e.preventDefault();"),
    "the settings switch can be flipped on a test copy");
  assert.ok(guard.includes('toast("ᚦ "+T("realm.test.public.locked.toast"))'));
  assert.strictEqual(KEYS["realm.test.public.locked.toast"].mark, "ᚦ");
});

console.log("");
if (failures.length) {
  console.log("test copy selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("test copy selftest: all " + passed + " rules pass");
