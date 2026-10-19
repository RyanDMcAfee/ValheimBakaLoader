/* The Mods table's sort, from issue 20, driven for real.
 *
 * WHY THIS EXISTS. The table used to rest in the scan's own order, which is folder order,
 * which is author order, while the row showed the author smaller and fainter than anything
 * else on it. Issue 20 asked for four things: rest on Name A to Z, give Author a column of its
 * own, sort two new date columns, and say in the head how the table is sorted whenever it is
 * not resting. This holds all four.
 *
 * THE CODE UNDER TEST is the block between the MODSORT markers in app.js plus the shared
 * table helpers (sortCycle, sortHome, sortReset) it is wired through, evaluated here with the
 * page's globals stood in for. The sentences come out of the real en.json.
 *
 *   1. The table rests on Name, A to Z, and a realm switch (sortReset) puts it back there.
 *      The roster still rests in its own order.
 *   2. The head says nothing while the table rests, and "sorted by Author, descending" after
 *      two clicks on Author; a version column's first click reads descending, as its arrow.
 *   3. Author sorts as a column of its own, ties broken by name, both ways.
 *   4. Both dates sort newest first on the first click, and a blank date sorts LAST both ways.
 *   5. The third click on any column comes back to Name A to Z, never to the scan's order.
 *   6. The search still reaches the author, now that the name cell no longer shows it.
 *   7. The wiring: the header row carries Author between Name and Installed and both dates,
 *      every sortable header has a sentence for the statement, and renderMods paints it.
 *
 *     node scripts/ui/mod_sort_selftest.js
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

function T(id, params) {
  const entry = KEYS[id];
  if (!entry || typeof entry.lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return entry.lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

/* A fresh page: the real block and the real shared helpers, over a mod list of its own. */
function page() {
  const context = {
    S: { modSort: { col: "name", dir: 1 }, vikSort: { col: null, dir: 0 }, mods: [] },
    T,
    cmpText: (a, b) => String(a ?? "").localeCompare(String(b ?? ""), undefined, { sensitivity: "base" }),
    document: { querySelectorAll: () => [] },
    modIsHeld: () => false,
    renderMods: () => {},
  };
  /* The S literal in app.js is where the default lives, so it is read from there rather than
     written into the stand-in above: the stand-in is overwritten with what app.js says. */
  const literal = /\n  modSort:(\{[^}]*\}),/.exec(APP);
  assert.ok(literal, "the S literal no longer gives modSort a starting value");
  context.S.modSort = vm.runInNewContext("(" + literal[1] + ")");
  vm.createContext(context);
  const code = [
    between("/* MODSORT-BEGIN", "/* MODSORT-END */"),
  ].join("\n");
  vm.runInContext(code.replace(/^const /gm, "var "), context, { filename: "app.js#modsort" });
  for (const name of ["sortedMods", "sortCycle", "sortReset", "sortHome", "modSortStatement", "modSearchText"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  return context;
}

const MODS = [
  { ModName: "WorldEditCommands", Author: "JereKuusela", FullName: "JereKuusela-WorldEditCommands", InstalledVersion: "1.65.0", LatestVersion: "1.66.0",
    installedUtc: "2026-07-02T18:14:00Z", modUpdatedUtc: "2026-06-14T00:00:00Z" },
  { ModName: "ExtraSlots", Author: "shudnal", FullName: "shudnal-ExtraSlots", InstalledVersion: "1.0.20", LatestVersion: "1.0.22",
    installedUtc: "2026-09-20T09:41:00Z", modUpdatedUtc: null },
  { ModName: "Rcon_Commands", Author: "JereKuusela", FullName: "JereKuusela-Rcon_Commands", InstalledVersion: "1.12.0", LatestVersion: "1.12.0",
    installedUtc: null, modUpdatedUtc: "2026-09-09T00:00:00Z" },
  { ModName: "EpicLoot", Author: "RandyKnapp", FullName: "RandyKnapp-EpicLoot", InstalledVersion: "0.11.3", LatestVersion: "0.11.3",
    installedUtc: "2026-05-11T21:03:00Z", modUpdatedUtc: "2026-09-10T00:00:00Z" },
  { ModName: "ComfyMods-Gizmo", Author: "ComfyMods", FullName: "ComfyMods-Gizmo", InstalledVersion: "1.15.0", LatestVersion: "1.15.0",
    installedUtc: "2026-08-18T16:22:00Z", modUpdatedUtc: "2026-09-09T00:00:00Z" },
];
const names = list => Array.from(list, m => m.ModName);
function click(p, col) { p.sortCycle(p.S.modSort, col); }

test("1. the table rests on Name, A to Z, and a realm switch puts it back there", () => {
  const p = page();
  assert.deepStrictEqual(JSON.parse(JSON.stringify(p.S.modSort)), { col: "name", dir: 1 }, "the table does not start on Name A to Z");
  assert.deepStrictEqual(names(p.sortedMods(MODS)),
    ["ComfyMods-Gizmo", "EpicLoot", "ExtraSlots", "Rcon_Commands", "WorldEditCommands"]);
  click(p, "author"); click(p, "author");
  const held = p.S.modSort;
  p.sortReset(p.S.modSort);
  assert.strictEqual(p.S.modSort, held, "sortReset replaced the object instead of putting it back");
  assert.deepStrictEqual(JSON.parse(JSON.stringify(p.S.modSort)), { col: "name", dir: 1 });
  p.S.vikSort.col = "playtime"; p.S.vikSort.dir = 2;
  p.sortReset(p.S.vikSort);
  assert.deepStrictEqual(JSON.parse(JSON.stringify(p.S.vikSort)), { col: null, dir: 0 }, "the roster no longer rests in its own order");
});

test("2. the head is quiet at rest and names the column and the direction otherwise", () => {
  const p = page();
  assert.strictEqual(p.modSortStatement(), "", "the head speaks while the table is resting");
  click(p, "author");
  assert.strictEqual(p.modSortStatement(), "sorted by Author, ascending");
  click(p, "author");
  assert.strictEqual(p.modSortStatement(), "sorted by Author, descending");
  click(p, "name");   // a different column: its first click
  assert.strictEqual(p.modSortStatement(), "", "Name A to Z is the resting order whichever road led there");
  click(p, "name");
  assert.strictEqual(p.modSortStatement(), "sorted by Name, descending");
  click(p, "installed");
  assert.strictEqual(p.modSortStatement(), "sorted by Installed, descending", "a version's first click is high to low, and says so");
  click(p, "updated");
  assert.strictEqual(p.modSortStatement(), "sorted by Updated, descending");
  assert.strictEqual(KEYS["mods.sort.statement"].lore, "sorted by {col}, {dir}");
});

test("3. Author is a column of its own, ties broken by name, both ways", () => {
  const p = page();
  click(p, "author");
  assert.deepStrictEqual(names(p.sortedMods(MODS)),
    ["ComfyMods-Gizmo", "Rcon_Commands", "WorldEditCommands", "EpicLoot", "ExtraSlots"]);
  click(p, "author");
  assert.deepStrictEqual(names(p.sortedMods(MODS)),
    ["ExtraSlots", "EpicLoot", "Rcon_Commands", "WorldEditCommands", "ComfyMods-Gizmo"],
    "reversed, with JereKuusela's two still in name order");
});

test("4. both dates sort newest first, and a blank date is last both ways", () => {
  const p = page();
  click(p, "installedOn");
  assert.deepStrictEqual(names(p.sortedMods(MODS)),
    ["ExtraSlots", "ComfyMods-Gizmo", "WorldEditCommands", "EpicLoot", "Rcon_Commands"]);
  click(p, "installedOn");
  assert.deepStrictEqual(names(p.sortedMods(MODS)),
    ["EpicLoot", "WorldEditCommands", "ComfyMods-Gizmo", "ExtraSlots", "Rcon_Commands"],
    "the blank date jumped to the top when the column was reversed");
  click(p, "updated");
  assert.deepStrictEqual(names(p.sortedMods(MODS)),
    ["EpicLoot", "ComfyMods-Gizmo", "Rcon_Commands", "WorldEditCommands", "ExtraSlots"]);
  click(p, "updated");
  assert.deepStrictEqual(names(p.sortedMods(MODS)),
    ["WorldEditCommands", "ComfyMods-Gizmo", "Rcon_Commands", "EpicLoot", "ExtraSlots"]);
  /* A date that is not a date is a blank, not the start of time. */
  const odd = MODS.concat([{ ModName: "Zed", Author: "Z", FullName: "Z-Zed", installedUtc: "not a date" }]);
  p.sortReset(p.S.modSort); click(p, "installedOn");
  assert.deepStrictEqual(names(p.sortedMods(odd)).slice(-2), ["Rcon_Commands", "Zed"]);
});

test("5. the third click on any column comes back to Name A to Z", () => {
  const p = page();
  for (const col of ["author", "installed", "latest", "installedOn", "updated", "status"]) {
    click(p, col); click(p, col); click(p, col);
    assert.deepStrictEqual(JSON.parse(JSON.stringify(p.S.modSort)), { col: "name", dir: 1 }, col + "'s third click did not come home");
    assert.deepStrictEqual(names(p.sortedMods(MODS)),
      ["ComfyMods-Gizmo", "EpicLoot", "ExtraSlots", "Rcon_Commands", "WorldEditCommands"]);
  }
  /* And Name itself turns between its two directions. */
  click(p, "name");
  assert.deepStrictEqual(JSON.parse(JSON.stringify(p.S.modSort)), { col: "name", dir: 2 });
  click(p, "name");
  assert.deepStrictEqual(JSON.parse(JSON.stringify(p.S.modSort)), { col: "name", dir: 1 });
});

test("6. the search still reaches the author", () => {
  const p = page();
  p.S.modsIndexBlind = false;
  const text = p.modSearchText(Object.assign({}, MODS[0], { installedSource: "thunderstore" }));
  assert.ok(text.includes("JereKuusela"), "the author is no longer searchable: " + text);
  const off = p.modSearchText(Object.assign({}, MODS[1], { disabled: true }));
  assert.ok(off.includes(T("mods.status.off")), "a switched-off mod's Off pill is not searchable");
});

test("7. the wiring: headers, sentences, and the paint", () => {
  const head = HTML.slice(HTML.indexOf('<table id="modsTable">'), HTML.indexOf('<tbody id="modTable">'));
  const order = [...head.matchAll(/data-sort="(\w+)"/g)].map(m => m[1]);
  assert.deepStrictEqual(order, ["name", "author", "installed", "latest", "installedOn", "updated", "status"]);
  assert.ok(!/data-sort="author"[^>]*>[^<]*<span class="mono"/.test(head));
  const p = page();
  for (const col of order) {
    const id = vm.runInContext("MOD_SORT_COLS", p)[col];
    assert.ok(id && KEYS[id], col + " has no header sentence the statement can use");
    assert.ok(new RegExp('data-sort="' + col + '"[^>]*data-i18n="' + id.replace(/\./g, "\\.") + '"').test(head),
      col + "'s header is not worded by " + id + ", so the statement and the header could disagree");
  }
  assert.strictEqual(KEYS["mods.col.latest"].lore, "Thunderstore latest");
  const render = between("function renderMods(){", "\n/* \"showing N of M\"");
  assert.ok(render.includes('$("#modsSortLine")') && render.includes("modSortStatement()"), "renderMods never paints the sort line");
  assert.ok(/id="modsSortLine"/.test(HTML.slice(HTML.indexOf('id="page-mods"'), HTML.indexOf('id="modsTable"'))),
    "the Mods head has no sort line");
  /* The row no longer carries the author in the name cell. */
  const row = between("function modRowHtml(", "\n/* MODROW-END */");
  assert.ok(!/<strong>\$\{esc\(m\.ModName\)\}<\/strong> <span class="mono"[^`]*m\.Author/.test(row), "the author is still tucked into the name cell");
  assert.ok(row.includes('<td class="mono mod-author">${esc(m.Author||"")}</td>'), "the author has no cell of its own");
});

console.log("");
if (failures.length) {
  console.log("mod sort selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("mod sort selftest: all " + passed + " rules pass");
