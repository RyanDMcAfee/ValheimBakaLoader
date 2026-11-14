/* The Mods table's sort, from issue 20, driven for real.
 *
 * WHY THIS EXISTS. The table used to rest in the scan's own order, which is folder order,
 * which is author order, while the row showed the author smaller and fainter than anything
 * else on it. Issue 20 asked for four things: rest on Name A to Z, give Author a column of its
 * own, sort two new date columns, and say in the head how the table is sorted whenever it is
 * not resting. This holds all four.
 *
 * ISSUE 34 (1.3.3) changed three things this holds as well. The Status column used to sort on
 * UpdateAvailable alone, so a table of Current, Not listed and Local rows tied on every row and
 * the header cycled while nothing moved: it now ranks the pill's own kind (modStatusKind, the
 * one chain the pill, the search and the sort all read) by a fixed order, the same in every
 * language. A blank date used to sit last both ways round: it now counts as the smallest value,
 * the way a missing Latest version always has. And the sentence in the head used to appear and
 * push the search box and the buttons along: it is a screen reader's line now (vhide), and the
 * header's mark, colour and aria-sort are what the eye reads. scripts/ui/mods_head_steady_probe.js
 * measures the head in a real browser; this holds the rest.
 *
 * THE CODE UNDER TEST is the block between the MODSORT markers in app.js plus the shared
 * table helpers (sortCycle, sortHome, sortReset) it is wired through, and the row's
 * modStatusPill, evaluated here with the page's globals stood in for. The sentences come out
 * of the real en.json, and the pseudo catalog for the language rule.
 *
 *   1. The table rests on Name, A to Z, and a realm switch (sortReset) puts it back there.
 *      The roster still rests in its own order.
 *   2. The screen reader's sentence is empty while the table rests, and "sorted by Author,
 *      descending" after two clicks on Author; a version column's first click reads descending.
 *   3. Author sorts as a column of its own, ties broken by name, both ways.
 *   4. Both dates sort newest first on the first click, and a blank date is the smallest value:
 *      last on the first click, first on the second, and blanks among themselves by name.
 *   5. The third click on any column comes back to Name A to Z, never to the scan's order.
 *   6. The search still reaches the author, now that the name cell no longer shows it.
 *   7. The wiring: the header row carries Author between Name and Installed and both dates,
 *      every sortable header has a sentence for the statement, and renderMods paints it.
 *   8 to 16. Issue 34: the reporter's list, all eight kinds in rank order, the pill and the
 *      rank agreeing, the index blind, Latest and Updated agreeing on blanks, a language
 *      switch, a search over a sorted table, the sort line taking no room, and one status
 *      chain in the whole page. Rule 4 is the issue's blank dates.
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
  /* A plural entry (1.2.16's "Needs {count} mods") is read the way English reads it. */
  let lore = entry && entry.lore;
  if (lore && typeof lore === "object") lore = params && Number(params.count) === 1 ? lore.one : lore.other;
  if (typeof lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

/* A fresh page: the real block and the real shared helpers, over a mod list of its own. */
function page() {
  const context = {
    S: { modSort: { col: "name", dir: 1 }, vikSort: { col: null, dir: 0 }, mods: [] },
    T,
    cmpText: (a, b) => String(a ?? "").localeCompare(String(b ?? ""), undefined, { sensitivity: "base" }),
    document: { querySelectorAll: () => [] },
    modIsHeld: () => false,
    /* 1.2.16: the search carries the row's "Needs" pill. The real modNeeds lives in the
       MODROW block, which this harness does not lift; this is its rule, word for word. */
    modNeeds: m => (m && Array.isArray(m.missingDependencies)) ? m.missingDependencies.filter(n => n && n.fullName) : [],
    renderMods: () => {},
  };
  /* The S literal in app.js is where the default lives, so it is read from there rather than
     written into the stand-in above: the stand-in is overwritten with what app.js says. */
  const literal = /\n  modSort:(\{[^}]*\}),/.exec(APP);
  assert.ok(literal, "the S literal no longer gives modSort a starting value");
  context.S.modSort = vm.runInNewContext("(" + literal[1] + ")");
  vm.createContext(context);
  const code = [
    APP.slice(APP.indexOf("const esc="), APP.indexOf("\n", APP.indexOf("const esc="))),
    between("/* MODSORT-BEGIN", "/* MODSORT-END */"),
    /* The row's pill lives with the row render, outside the block; it is lifted on its own so
       rule 10 can hold the word it draws to the kind the sort ranks. */
    between("function modStatusPill(", "\n/* The Installed cell."),
  ].join("\n");
  vm.runInContext(code.replace(/^const /gm, "var "), context, { filename: "app.js#modsort" });
  for (const name of ["sortedMods", "sortCycle", "sortReset", "sortHome", "modSortStatement", "modSearchText",
                      "modStatusKind", "modStatusPill", "modsForBody"])
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

test("2. the screen reader's sentence is empty at rest and names the column and the direction otherwise", () => {
  const p = page();
  assert.strictEqual(p.modSortStatement(), "", "the sort line speaks while the table is resting");
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

/* Issue 34 replaced this rule's old truth (a blank date sat LAST both ways round, so a Hexium
   or Local row with no Updated date stayed at the foot whichever way the column ran). A blank
   is the smallest value now, as a missing Latest version always was: last on the first click,
   which reads high to low, and first on the second. Blanks among themselves fall to name order,
   and a date that is not a date is a blank, not the start of time. */
const DATED = [
  { ModName: "Moss", Author: "Acme", FullName: "Acme-Moss", installedUtc: "2026-07-02T18:14:00Z", modUpdatedUtc: "2026-06-14T00:00:00Z" },
  { ModName: "Fern", Author: "Acme", FullName: "Acme-Fern", installedUtc: "2026-09-20T09:41:00Z", modUpdatedUtc: null },
  { ModName: "Reed", Author: "Acme", FullName: "Acme-Reed", installedUtc: null, modUpdatedUtc: "2026-09-09T00:00:00Z" },
  { ModName: "Clover", Author: "Acme", FullName: "Acme-Clover", installedUtc: "2026-05-11T21:03:00Z", modUpdatedUtc: "2026-09-10T00:00:00Z" },
  { ModName: "Sedge", Author: "Acme", FullName: "Acme-Sedge", installedUtc: "", modUpdatedUtc: "" },
  { ModName: "Bracken", Author: "Acme", FullName: "Acme-Bracken", installedUtc: "not a date" },
];

test("4. both dates sort newest first, and a blank date is the smallest value both columns", () => {
  const p = page();
  click(p, "installedOn");
  assert.deepStrictEqual(names(p.sortedMods(DATED)),
    ["Fern", "Moss", "Clover", "Bracken", "Reed", "Sedge"], "Installed on, first click: newest first, blanks last by name");
  click(p, "installedOn");
  assert.deepStrictEqual(names(p.sortedMods(DATED)),
    ["Bracken", "Reed", "Sedge", "Clover", "Moss", "Fern"], "Installed on, second click: the blanks did not come to the top");
  click(p, "updated");
  assert.deepStrictEqual(names(p.sortedMods(DATED)),
    ["Clover", "Reed", "Moss", "Bracken", "Fern", "Sedge"], "Updated, first click: newest first, blanks last by name");
  click(p, "updated");
  assert.deepStrictEqual(names(p.sortedMods(DATED)),
    ["Bracken", "Fern", "Sedge", "Moss", "Reed", "Clover"], "Updated, second click: the blanks did not come to the top");
  /* Every date blank: name order both ways, both columns. */
  const blank = [{ ModName: "Reed" }, { ModName: "Fern" }, { ModName: "Moss", installedUtc: "", modUpdatedUtc: "soon" }];
  for (const col of ["installedOn", "updated"]) {
    p.sortReset(p.S.modSort);
    click(p, col);
    assert.deepStrictEqual(names(p.sortedMods(blank)), ["Fern", "Moss", "Reed"], col + " with no dates, first click");
    click(p, col);
    assert.deepStrictEqual(names(p.sortedMods(blank)), ["Fern", "Moss", "Reed"], col + " with no dates, second click");
  }
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
  /* 1.2.16: a row that needs a mod is found by the pill's word and by the missing mod's name. */
  const needs = p.modSearchText(Object.assign({}, MODS[0], { missingDependencies: [{ fullName: "Someone-Someone_CoreLib", name: "Someone_CoreLib", state: "missing" }] }));
  assert.ok(needs.includes("Needs Someone_CoreLib"), "the Needs pill is not searchable: " + needs);
  const two = p.modSearchText(Object.assign({}, MODS[0], { missingDependencies: [{ fullName: "A-One", name: "One" }, { fullName: "B-Two", name: "Two" }] }));
  assert.ok(two.includes("Needs 2 mods") && two.includes("One") && two.includes("Two"), "two needs are not searchable: " + two);
  assert.ok(!p.modSearchText(MODS[0]).includes("Needs"), "a row that needs nothing reads as needing something");
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

/* ---------- issue 34: the Status column ---------- */

/* One row of each kind, by the fields the scan really hands over. */
function kindRow(kind, name, extra) {
  const base = { ModName: name, Author: "Acme", FullName: "Acme-" + name, InstalledVersion: "1.0.0", LatestVersion: "1.0.0",
    installedSource: "thunderstore", UpdateAvailable: false };
  const fields = {
    update: { UpdateAvailable: true, LatestVersion: "1.1.0" },
    held: { installedSource: "hexium", thunderstoreNewer: true, LatestVersion: "1.1.0" },
    not_listed: { notListed: true, LatestVersion: "" },
    local: { installedSource: "local" },
    off: { disabled: true },
    current: {},
    bundled: { Bundled: true, installedSource: "" },
  }[kind];
  assert.ok(fields, "no row recipe for " + kind);
  return Object.assign(base, fields, extra || {});
}
function clickTo(p, col, times) { p.sortReset(p.S.modSort); for (let i = 0; i < times; i++) click(p, col); }
const D3 = ["update", "held", "not_listed", "unchecked", "local", "off", "current", "bundled"];

test("8. the reporter's list: Current, Not listed and Local rows move when Status is clicked", () => {
  const p = page();
  p.S.modsIndexBlind = false;
  const rows = [kindRow("current", "Birch"), kindRow("local", "Cedar"), kindRow("not_listed", "Alder"), kindRow("current", "Aspen")];
  clickTo(p, "status", 1);
  assert.deepStrictEqual(names(p.sortedMods(rows)), ["Alder", "Cedar", "Aspen", "Birch"],
    "first click is not Not listed, Local, Current (the old sort read UpdateAvailable only and every row tied)");
  clickTo(p, "status", 2);
  assert.deepStrictEqual(names(p.sortedMods(rows)), ["Aspen", "Birch", "Cedar", "Alder"],
    "second click is not Current, Local, Not listed, ties by name");
  assert.strictEqual(p.modSortStatement(), "sorted by Status, descending");
});

test("9. all eight kinds: the first click is the fixed rank exactly, the second its reverse", () => {
  const p = page();
  assert.deepStrictEqual(Array.from(vm.runInContext("MOD_STATUS_RANK", p)), D3, "the rank is not the planner's order");
  assert.ok(!vm.runInContext("MOD_DESC_FIRST", p).has("status"), "Status reads high to low on its first click");
  /* The index blind is one answer for the whole scan, so Not checked never shares a table with
     Held, Not listed, Update or Current. The eight together are proved with the row's kind
     stood in; the two real tables after it are proved by the fields alone. */
  const by = { update: "Pine", held: "Ash", not_listed: "Oak", unchecked: "Elm", local: "Yew", off: "Birch", current: "Fir", bundled: "Hazel" };
  const eight = D3.map(k => ({ ModName: by[k], kind: k })).sort((a, b) => a.ModName.localeCompare(b.ModName));
  const real = p.modStatusKind;
  p.modStatusKind = m => m.kind;
  try {
    clickTo(p, "status", 1);
    assert.deepStrictEqual(Array.from(p.sortedMods(eight), m => m.kind), D3);
    clickTo(p, "status", 2);
    assert.deepStrictEqual(Array.from(p.sortedMods(eight), m => m.kind), D3.slice().reverse());
  } finally { p.modStatusKind = real; }
  const seen = D3.filter(k => k !== "unchecked").map(k => kindRow(k, by[k]));
  p.S.modsIndexBlind = false;
  clickTo(p, "status", 1);
  assert.deepStrictEqual(Array.from(p.sortedMods(seen), m => p.modStatusKind(m, false)), D3.filter(k => k !== "unchecked"));
  assert.deepStrictEqual(names(p.sortedMods(seen)), ["Pine", "Ash", "Oak", "Yew", "Birch", "Fir", "Hazel"]);
  clickTo(p, "status", 2);
  assert.deepStrictEqual(names(p.sortedMods(seen)), ["Hazel", "Fir", "Birch", "Yew", "Oak", "Ash", "Pine"]);
  p.S.modsIndexBlind = true;
  clickTo(p, "status", 1);
  assert.deepStrictEqual(Array.from(p.sortedMods(seen), m => p.modStatusKind(m, true)),
    ["unchecked", "unchecked", "unchecked", "unchecked", "local", "off", "bundled"]);
  assert.deepStrictEqual(names(p.sortedMods(seen)), ["Ash", "Fir", "Oak", "Pine", "Yew", "Birch", "Hazel"]);
});

test("10. a row that is two things at once ranks as the word its pill says", () => {
  const p = page();
  const cases = [
    ["held", kindRow("held", "Held", { UpdateAvailable: true }), false],
    ["off", kindRow("off", "Off", { UpdateAvailable: true, LatestVersion: "1.1.0" }), false],
    ["bundled", kindRow("bundled", "Bundled", { UpdateAvailable: true, LatestVersion: "1.1.0" }), false],
    ["local", kindRow("local", "Local", { UpdateAvailable: true }), true],
    ["off", kindRow("off", "OffLocal", { installedSource: "local" }), true],
    ["not_listed", kindRow("not_listed", "Gone", { UpdateAvailable: true }), false],
  ];
  const look = vm.runInContext("MOD_STATUS_LOOK", p);
  for (const [want, m, blind] of cases) {
    p.S.modsIndexBlind = blind;
    const kind = p.modStatusKind(m, blind);
    assert.strictEqual(kind, want, m.ModName + " ranks as " + kind + ", not " + want);
    const word = T(look[kind].wordId);
    assert.strictEqual(word, T("mods.status." + want), m.ModName + "'s look table names another word");
    const pill = p.modStatusPill(m, blind);
    assert.ok(pill.includes(">" + p.esc(word) + "</span>"), m.ModName + "'s pill does not say " + word + ": " + pill);
    assert.ok(p.modSearchText(m).includes(word), m.ModName + "'s search text does not carry " + word);
    /* And the place in the sort is that kind's place, beside a plain Current row (Not checked
       while blind), which ranks after some of these and before others. */
    clickTo(p, "status", 1);
    const order = names(p.sortedMods([m, kindRow("current", "Zz")]));
    assert.deepStrictEqual(order, D3.indexOf(want) < D3.indexOf(blind ? "unchecked" : "current") ? [m.ModName, "Zz"] : ["Zz", m.ModName],
      m.ModName + " sorts as something its pill does not say");
  }
  /* The pill's look did not change in the move to one chain: colour and hover per kind. */
  p.S.modsIndexBlind = false;
  assert.strictEqual(p.modStatusPill(kindRow("bundled", "B"), false), '<span class="pill ember">' + p.esc(T("mods.status.bundled")) + "</span>");
  assert.strictEqual(p.modStatusPill(kindRow("current", "C"), false), '<span class="pill green">' + p.esc(T("mods.status.current")) + "</span>");
  assert.strictEqual(p.modStatusPill(kindRow("update", "U"), false), '<span class="pill amber">' + p.esc(T("mods.status.update")) + "</span>");
  assert.strictEqual(p.modStatusPill(kindRow("held", "H"), false),
    '<span class="pill amber" title="' + p.esc(T("mods.status.held.tip")) + '">' + p.esc(T("mods.status.held")) + "</span>");
  assert.strictEqual(p.modStatusPill(kindRow("current", "N"), true),
    '<span class="pill grey" title="' + p.esc(T("mods.status.unchecked.tip")) + '">' + p.esc(T("mods.status.unchecked")) + "</span>");
});

test("11. the index blind: every row that is not bundled, off or local ranks as Not checked", () => {
  const p = page();
  p.S.modsIndexBlind = true;
  const rows = ["update", "held", "not_listed", "current"].map((k, i) => kindRow(k, ["Dock", "Aloe", "Cress", "Basil"][i]));
  for (const m of rows) assert.strictEqual(p.modStatusKind(m, true), "unchecked", m.ModName + " is not Not checked while blind");
  rows.push(kindRow("local", "Anise"), kindRow("off", "Abaca"), kindRow("bundled", "Agave"));
  clickTo(p, "status", 1);
  assert.deepStrictEqual(names(p.sortedMods(rows)), ["Aloe", "Basil", "Cress", "Dock", "Anise", "Abaca", "Agave"],
    "while blind the update row still jumped ahead of the other unchecked rows");
  assert.strictEqual(p.modSearchText(rows[0]).includes(T("mods.status.unchecked")), true);
});

test("12. Latest and Updated put rows with neither at the same end for the same click", () => {
  const p = page();
  const rows = [
    { ModName: "Tansy", LatestVersion: "2.0.0", modUpdatedUtc: "2026-09-01T00:00:00Z" },
    { ModName: "Sorrel", LatestVersion: "", modUpdatedUtc: null },
    { ModName: "Rue", LatestVersion: "0.1.0", modUpdatedUtc: "2026-01-01T00:00:00Z" },
    { ModName: "Mint", LatestVersion: "-", modUpdatedUtc: "" },
    { ModName: "Lovage", LatestVersion: "1.0.0", modUpdatedUtc: "2026-05-01T00:00:00Z" },
  ];
  const blankNames = ["Mint", "Sorrel"];
  for (const times of [1, 2]) {
    clickTo(p, "latest", times);
    const byLatest = names(p.sortedMods(rows));
    clickTo(p, "updated", times);
    const byUpdated = names(p.sortedMods(rows));
    const ends = list => times === 1 ? list.slice(-2) : list.slice(0, 2);
    assert.deepStrictEqual(ends(byLatest), blankNames, "Latest, click " + times + ": " + byLatest.join(","));
    assert.deepStrictEqual(ends(byUpdated), blankNames, "Updated, click " + times + ": " + byUpdated.join(","));
    assert.deepStrictEqual(byLatest, byUpdated, "the two columns order these rows differently on click " + times);
  }
});

test("13. a language switch while sorted by Status leaves every row where it was", () => {
  const p = page();
  p.S.modsIndexBlind = false;
  const rows = D3.filter(k => k !== "unchecked").map((k, i) => kindRow(k, ["Pine", "Ash", "Oak", "Yew", "Birch", "Fir", "Hazel"][i]));
  rows.push(kindRow("current", "Elm"), kindRow("update", "Cane"));
  for (const times of [1, 2]) {
    clickTo(p, "status", times);
    const english = names(p.sortedMods(rows));
    const XX = JSON.parse(fs.readFileSync(path.join(WEB, "i18n", "xx.json"), "utf8")).keys || {};
    p.T = (id, params) => {
      const t = XX[id] && XX[id].translation;
      return typeof t === "string" ? t.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m)) : T(id, params);
    };
    assert.deepStrictEqual(names(p.sortedMods(rows)), english, "the pseudo catalog moved a row on click " + times);
    /* A catalog whose words run the other way round alphabetically: a sort by the word would
       turn the table over here, a sort by the kind does not. */
    p.T = id => /^mods\.status\./.test(id) ? String.fromCharCode(90 - D3.indexOf(id.slice(12))) + " " + id : T(id);
    assert.deepStrictEqual(names(p.sortedMods(rows)), english, "a catalog with other words moved a row on click " + times);
    p.T = T;
  }
});

test("14. a search over a Status-sorted table keeps the sorted order among the rows it shows", () => {
  const p = page();
  p.S.modsIndexBlind = false;
  const rows = [
    kindRow("current", "Alder", { Author: "Zephyr", FullName: "Zephyr-Alder" }), kindRow("update", "Beech"),
    kindRow("local", "Cedar"), kindRow("not_listed", "Dogwood", { Author: "Zephyr", FullName: "Zephyr-Dogwood" }),
    kindRow("current", "Elder"), kindRow("held", "Fig", { Author: "Zephyr", FullName: "Zephyr-Fig" }), kindRow("off", "Gum"),
  ];
  for (const times of [1, 2]) {
    clickTo(p, "status", times);
    const sorted = p.sortedMods(rows);
    p.S.modFilter = "acme";
    assert.deepStrictEqual(names(p.modsForBody(sorted)), names(sorted).filter(n => !["Alder", "Dogwood", "Fig"].includes(n)),
      "the search reordered what it kept on click " + times);
    p.S.modFilter = T("mods.status.current");
    assert.deepStrictEqual(names(p.modsForBody(sorted)), ["Alder", "Elder"], "the pill's word no longer finds its rows");
    p.S.modFilter = "";
  }
  /* And the table really sorts first and narrows second. */
  const render = between("function renderMods(){", "\n/* \"showing N of M\"");
  const a = render.indexOf("sortedMods(S.mods"), b = render.indexOf("modsForBody(mods)");
  assert.ok(a >= 0 && b > a, "renderMods no longer sorts the whole list and then narrows it");
});

test("15. the sort line takes no room: a screen reader's line, still written, the header carries the look", () => {
  const head = HTML.slice(HTML.indexOf('id="page-mods"'), HTML.indexOf('id="modsTable"'));
  const tag = /<span[^>]*id="modsSortLine"[^>]*>/.exec(head);
  assert.ok(tag, "the Mods head has no sort line");
  assert.ok(/class="vhide"/.test(tag[0]), "the sort line is not vhide, so it takes space in the head: " + tag[0]);
  assert.ok(/role="status"/.test(tag[0]) && /aria-live="polite"/.test(tag[0]), "the sort line is no longer a polite live region");
  const CSS = fs.readFileSync(path.join(WEB, "app.css"), "utf8").replace(/\r\n/g, "\n");
  assert.ok(/\n\.vhide\{position:absolute;width:1px;height:1px;[^}]*overflow:hidden;clip:rect\(0,0,0,0\)/.test(CSS), "app.css lost the vhide rule");
  /* A display:none on it would silence the live region; any rule that names it could put it
     back into the head. Nothing in app.css names it now. */
  assert.ok(!/modsortline|#modsSortLine/.test(CSS), "app.css still styles the sort line");
  const render = between("function renderMods(){", "\n/* \"showing N of M\"");
  assert.ok(render.includes('$("#modsSortLine")') && render.includes("modSortStatement()"), "renderMods never writes the sentence");
  assert.ok(render.indexOf("renderModSortMarks()") >= 0, "renderMods never paints the header's mark");
  /* The header's mark and colour are the visible signal. */
  assert.ok(/thead th\.sortable\.sorted\{color:/.test(CSS), "a sorted header no longer changes colour");
  const p = page();
  click(p, "author");
  assert.strictEqual(p.modSortStatement(), "sorted by Author, ascending", "the sentence the reader hears went quiet");
});

test("16. one status chain: the pill, the search and the sort all read modStatusKind", () => {
  const pill = between("function modStatusPill(", "\n/* The Installed cell.");
  const search = between("function modSearchText(", "\n/* A copy the host took from Hexium");
  const sort = between("function sortedMods(", "\n/* The header word for each sortable column");
  for (const [name, body] of [["modStatusPill", pill], ["modSearchText", search], ["sortedMods", sort]])
    assert.ok(/modStatusKind\(/.test(body), name + " does not read modStatusKind");
  assert.ok(!/UpdateAvailable/.test(sort), "sortedMods reads UpdateAvailable on its own again");
  /* No second copy of the chain anywhere: the eight words are asked for only through the look table. */
  for (const k of ["bundled", "local", "held", "not_listed", "update", "current"])
    assert.ok(!APP.includes('T("mods.status.' + k + '")'), "a second chain asks for mods.status." + k + " by hand");
  const kinds = between("/* MODKIND-BEGIN", "/* MODKIND-END */");
  assert.ok(between("/* MODSORT-BEGIN", "/* MODSORT-END */").includes(kinds), "modStatusKind left the MODSORT block");
  const p = page();
  assert.deepStrictEqual(Object.keys(vm.runInContext("MOD_STATUS_LOOK", p)).sort(), D3.slice().sort(), "a kind has no look, or a look has no kind");
});

console.log("");
if (failures.length) {
  console.log("mod sort selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("mod sort selftest: all " + passed + " rules pass");
