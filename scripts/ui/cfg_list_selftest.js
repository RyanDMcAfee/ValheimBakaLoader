/* The Configs hall's list once it walks folders, from issue 20, driven for real.
 *
 * WHY THIS EXISTS. config.list hands back paths relative to BepInEx/config now
 * ("ModNameFolder/com.someModAuthor.ModName.ExtraSettings.cfg") and .yaml, .yml, .json and .md
 * files beside the .cfg ones. The list has to draw a path so the name still reads first, keep
 * the order the host side sorts by, and let the search find a file by its folder.
 *
 * THE CODE UNDER TEST is the block between the CFGITEM markers in app.js and the search text
 * function above it, evaluated with the page's globals stood in for.
 *
 *   1. An entry draws the folders muted in front of the name, every part escaped, the whole
 *      path in its tooltip and in data-f (which is what a click opens).
 *   2. A top-level file draws no prefix at all.
 *   3. Path order, case ignored, a folder's own files together: the host side's order.
 *   4. The search finds a file by its folder, by its extension, and by the words of its name.
 *   5. The wiring: renderCfgList draws through cfgItemHtml, the empty list no longer says
 *      ".cfg", and the preview mock carries a file one and two folders down.
 *
 *     node scripts/ui/cfg_list_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const WEB = path.join(ROOT, "ValheimBakaLoader", "WebUI");
const APP = fs.readFileSync(path.join(WEB, "app.js"), "utf8").replace(/\r\n/g, "\n");
const KEYS = JSON.parse(fs.readFileSync(path.join(WEB, "i18n", "en.json"), "utf8")).keys || {};

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
function line(prefix) {
  const a = APP.indexOf(prefix);
  assert.ok(a >= 0, "app.js no longer carries " + prefix);
  return APP.slice(a, APP.indexOf("\n", a));
}

function page() {
  const context = { S: { mods: [] } };
  vm.createContext(context);
  const code = [
    line("const esc=").replace(/^const /, "var "),
    between("function searchTokens(", "\n/* The last piece of a path"),
    between("/* CFGITEM-BEGIN", "/* CFGITEM-END */"),
    between("function cfgSearchText(", "\n/* THE ONE PLACE the Configs search"),
  ].join("\n");
  vm.runInContext(code, context, { filename: "app.js#cfglist" });
  return context;
}

const ISSUE = [
  "com.someModAuthor.ModName.DefaultSettings.cfg",
  "ModNameFolder/com.someModAuthor.ModName.ExtraSettings.cfg",
  "ModNameData/com.someModAuthor.ModName.CustomList.yaml",
];

test("1. the folders come first and quiet, the name after, all of it escaped", () => {
  const p = page();
  const html = p.cfgItemHtml(ISSUE[1], true, true);
  assert.strictEqual(html,
    '<div class="cfg-item sel" data-f="ModNameFolder/com.someModAuthor.ModName.ExtraSettings.cfg" title="ModNameFolder/com.someModAuthor.ModName.ExtraSettings.cfg">' +
    '<span class="cfgname"><span class="cfgpre">ModNameFolder/</span>com.someModAuthor.ModName.ExtraSettings.cfg</span><span class="dot"></span></div>');
  const deep = p.cfgItemHtml("A&B/<x>/odd\"name.json", false, false);
  assert.ok(deep.includes('<span class="cfgpre">A&amp;B/&lt;x&gt;/</span>odd&quot;name.json'), deep);
  assert.ok(deep.includes('data-f="A&amp;B/&lt;x&gt;/odd&quot;name.json"'), deep);
});

test("2. a file at the top level draws no prefix", () => {
  const p = page();
  assert.strictEqual(p.cfgItemHtml("BepInEx.cfg", false, false),
    '<div class="cfg-item" data-f="BepInEx.cfg" title="BepInEx.cfg"><span class="cfgname">BepInEx.cfg</span></div>');
});

test("3. path order, case ignored, a folder's files together", () => {
  const p = page();
  const list = ["Other/two.json", "BepInEx.cfg", "modnamefolder/z.cfg", "ModNameData/notes.md", "ModNameFolder/a.cfg", "Mod.cfg", "ModNameData.cfg"];
  assert.deepStrictEqual(Array.from(list.slice().sort(p.cfgPathCmp)),
    ["BepInEx.cfg", "Mod.cfg", "ModNameData/notes.md", "ModNameData.cfg", "ModNameFolder/a.cfg", "modnamefolder/z.cfg", "Other/two.json"]);
  /* The same order the host side's OrdinalIgnoreCase gives, "_" and "-" included (the C#
     test The_matcher_ignores_case_extension_and_the_three_separators holds the other end). */
  assert.deepStrictEqual(Array.from(["shudnal.ExtraSlots.cfg", "extra_slots.yaml", "Extra-Slots Custom.json", "extras.cfg"].sort(p.cfgPathCmp)),
    ["Extra-Slots Custom.json", "extras.cfg", "extra_slots.yaml", "shudnal.ExtraSlots.cfg"]);
});

test("4. the search finds a file by its folder, its extension and the words of its name", () => {
  const p = page();
  const hit = (q, f) => p.searchHit(p.cfgSearchText(f), p.searchTokens(q));
  assert.ok(hit("ModNameData", ISSUE[2]), "the folder name does not find the file");
  assert.ok(hit("yaml", ISSUE[2]), "the extension does not find the file");
  assert.ok(hit("customlist", ISSUE[2]), "a word of the name does not find the file");
  assert.ok(hit("extrasettings modnamefolder", ISSUE[1]));
  assert.ok(!hit("ModNameData", ISSUE[1]), "a file in another folder matched the folder");
});

test("5. the wiring", () => {
  const render = between("function renderCfgList(){", "\n/* \"showing N of M\" beside the box");
  assert.ok(render.includes("shown.map(f=>cfgItemHtml(f,f===CFG.file,f===CFG.file&&CFG.dirty))"), "renderCfgList does not draw through cfgItemHtml");
  assert.ok(render.includes('T("runes.list.empty")'), "the empty list lost its sentence");
  assert.ok(!/\.cfg/.test(KEYS["runes.list.empty"].lore + KEYS["runes.list.empty"].plain), "the empty list still names only .cfg files");
  const mock = between("  CFG.mock={", "\n  refreshCfgList(false);");
  assert.ok(/"[^"/]+\/[^"/]+\.(yaml|json)":/.test(mock), "the preview mock carries no file one folder down");
  assert.ok(/"[^"/]+\/[^"/]+\/[^"/]+\.md":/.test(mock), "the preview mock carries no .md two folders down");
  const list = between("async function cfgListFiles(){", "\nasync function cfgRead(");
  assert.ok(list.includes("Object.keys(CFG.mock||{}).sort(cfgPathCmp)"), "the preview's list is not in the host side's order");
});

console.log("");
if (failures.length) {
  console.log("cfg list selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("cfg list selftest: all " + passed + " rules pass");
