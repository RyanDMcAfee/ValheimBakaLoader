/* Mods and Configs read the new folder after the Server Executable moves, driven for real.
 *
 * WHY THIS EXISTS. Both halls are read off the BepInEx install beside the server executable.
 * A host who pointed a realm at another executable with Save Config went on seeing the old
 * install's mods and config files until BakaLoader was restarted, and the rows and the open
 * file still acted by name on the install the realm pointed at now. Issue 17's thread asked
 * for both halls to follow the move and for a way to ask the disk again by hand.
 *
 * THE CODE UNDER TEST is the block between the DISKREAD markers in app.js, plus the page's own
 * scanMods, scanReplyIsOurs, refreshModCount, cfgForget, cfgListFiles and refreshCfgList cut
 * out of app.js and run here with the page's globals stood in for. A copy of those functions
 * in this file would pass forever while the page did something else. The sentences come out
 * of the real en.json.
 *
 * The rules:
 *   1. The path comparison: case, slash direction and a trailing slash are not a move; another
 *      folder is; the effective path in a reply wins over the stored field; a blank box that
 *      falls back to the same default is not a move.
 *   2. After a move both lists are read again: a config list and a mod count from the old
 *      folder are replaced by the new folder's, the open file is let go, the old scan's rows
 *      and the waiting-updates bar are put down, and a scan starts when the Mods hall is open.
 *   3. A scan asked before the move and answering after it is dropped: its rows are not
 *      drawn, the busy flag goes down, and the hall asks the new folder.
 *   4. A mod count asked before the move and answering after it is dropped.
 *   5. The Refresh press: reads the count and starts the quiet scan (force false, never the
 *      site-asking one), says so while a scan is already out, does nothing during Update all,
 *      and in the browser preview says preview only and sends nothing.
 *   6. The wiring: Refresh sits in the Mods head next to Scan with its catalog label and
 *      title, renderMods greys it with Scan, the click is bound, Reload sits in the Configs
 *      head with a title, Save Config re-reads after a move, and asks before letting an
 *      unsaved config file go.
 *   7. The host side reads the folder fresh on every call (config.list and mods.count go
 *      through the stored path each time, nothing cached), which is what makes a re-read
 *      from the page enough.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/disk_reread_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const WEB = path.join(ROOT, "ValheimBakaLoader", "WebUI");
const APP = fs.readFileSync(path.join(WEB, "app.js"), "utf8");
const HTML = fs.readFileSync(path.join(WEB, "index.html"), "utf8");
const KEYS = JSON.parse(fs.readFileSync(path.join(WEB, "i18n", "en.json"), "utf8")).keys || {};
const BRIDGE = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"), "utf8");

let passed = 0;
const failures = [];

async function rule(name, body) {
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

function between(from, to) {
  const a = APP.indexOf(from);
  assert.ok(a >= 0, "app.js no longer carries " + from);
  const b = APP.indexOf(to, a + from.length);
  assert.ok(b > a, "app.js no longer carries " + to + " after " + from);
  return APP.slice(a, b);
}

/* The catalog's own sentence with its slots filled, so a missing id fails loudly here. */
function T(id, params) {
  const entry = KEYS[id];
  let lore = entry && entry.lore;
  /* A plural entry holds one sentence per category; English has two. */
  if (lore && typeof lore === "object" && entry.plural)
    lore = Number(params && params[entry.plural]) === 1 ? lore.one : lore.other;
  if (typeof lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return lore.replace(/\\u([0-9a-fA-F]{4})/g, (_, h) => String.fromCharCode(parseInt(h, 16)))
    .replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

const FAIL = { failed: true };
const OLD = "D:\\Old\\Valheim dedicated server\\valheim_server.exe";
const NEW = "E:\\New\\Valheim dedicated server\\valheim_server.exe";

/* A fresh page: the real code, a disk the stubs answer from, and a record of what was sent. */
function page(opts) {
  opts = opts || {};
  const sent = [];
  const toasts = [];
  const logs = [];
  const cleared = [];
  const disk = { files: ["old.alpha.cfg", "old.beta.cfg"], count: 7, folder: "D:\\Old\\BepInEx\\plugins", rows: [{ FullName: "Old-Mod", ModName: "Mod", UpdateAvailable: true }] };
  const held = {};   // replies the test lets go of by hand
  const els = {};
  const el = id => (els[id] = els[id] || { value: "", disabled: false, textContent: "", title: "", innerHTML: "", classList: { add() {}, remove() {}, contains() { return false; }, toggle() {} } });
  const context = {
    S: { profileName: "Final Sunset", prefs: { ServerExePath: OLD }, mods: null, modsFolder: undefined, modsOnDisk: null, modsScanning: false, modsUpdating: false },
    Native: { available: opts.native !== false },
    FAIL, T, console,
    currentPage: opts.page || "mods",
    SCAN_CEILING_MS: 60000,
    $: sel => el(sel.replace(/^#/, "")),
    toast: line => toasts.push(line),
    logLine: (kind, line) => logs.push(line),
    clearCondition: name => cleared.push(name),
    renderMods: () => {}, renderModFolderBtn: () => {}, renderCfgList: () => {},
    renderModIndexLine: () => {}, cfgFindReset: () => {}, cfgFindStale: () => {}, resetCfgSaveBtn: () => {},
    modFolderLookupFailed: () => {}, modsScanFailReason: () => "reason",
    modFolderFromCount: r => { context.S.modsFolder = r.folder; return true; },
    refreshBepInEx: () => { sent.push({ method: "bepinex.status" }); return null; },
    clock: () => "12:00", hhmm: () => "12:00",
    setTimeout: () => 0,
    rpc: (method, params) => {
      sent.push({ method, params });
      const answer = () => {
        if (method === "config.list") return disk.files.slice();
        if (method === "config.read") return "text of " + params.file;
        if (method === "mods.count") return { count: disk.count, folder: disk.folder, profile: "Final Sunset" };
        if (method === "mods.scan") return { mods: disk.rows.map(r => Object.assign({}, r)), ok: true, profile: "Final Sunset" };
        return true;
      };
      if (opts.hold && opts.hold.includes(method)) {
        return new Promise(done => { held[method] = () => done(answer()); });
      }
      return Promise.resolve(answer());
    },
  };
  vm.createContext(context);
  const code = [
    between("const CFG={", "\n").replace(/^const /, "var "),
    "var MOD_UPDATES_HIDDEN=null;",
    between("function cfgForget(){", "\nasync function switchServer("),
    between("async function refreshModCount(){", "\n/* DISKREAD-BEGIN"),
    between("/* DISKREAD-BEGIN", "/* DISKREAD-END */"),
    between("function scanReplyIsOurs(", "\nasync function scanMods("),
    between("async function scanMods(", "\nasync function doUpdateAll("),
    between("async function cfgListFiles(){", "\nasync function cfgRead("),
    between("async function refreshCfgList(", "\nlet cfgConfirmT"),
  ].join("\n");
  vm.runInContext(code, context, { filename: "app.js#diskread" });
  for (const name of ["exePathKey", "exePathOf", "exePathMoved", "exePathBoxMoved", "forgetRealmDisk", "rereadRealmDisk", "modsRefreshPress", "scanMods", "refreshCfgList", "refreshModCount"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of app.js");
  context.isActiveProfile = p => p == null || String(p) === String(context.S.profileName);
  const CFG = vm.runInContext("CFG", context);
  return { context, CFG, disk, sent, toasts, logs, cleared, held, el };
}

const tick = () => new Promise(done => setImmediate(done));

(async function main() {
  await rule("1. a move is another folder, never a spelling of the same one", () => {
    const { context: c } = page();
    assert.strictEqual(c.exePathMoved({ ServerExePath: OLD }, { ServerExePath: OLD.toUpperCase() }), false, "case was taken for a move");
    assert.strictEqual(c.exePathMoved({ ServerExePath: OLD }, { ServerExePath: OLD.replace(/\\/g, "/") }), false, "slash direction was taken for a move");
    assert.strictEqual(c.exePathMoved({ ServerExePath: "D:\\Old\\" }, { ServerExePath: " D:\\Old" }), false, "a trailing slash or a space was taken for a move");
    assert.strictEqual(c.exePathMoved({ ServerExePath: OLD }, { ServerExePath: NEW }), true, "another folder was not taken for a move");
    /* The effective path is what is in force, so a blank box over the same default is not a
       move, and a blank box over a different default is. */
    assert.strictEqual(c.exePathMoved({ ServerExePath: OLD, EffectiveServerExePath: OLD }, { ServerExePath: "", EffectiveServerExePath: OLD }), false);
    assert.strictEqual(c.exePathMoved({ ServerExePath: OLD, EffectiveServerExePath: OLD }, { ServerExePath: "", EffectiveServerExePath: NEW }), true);
    /* The box against what is saved, for the question asked before the write. */
    c.$("#fServerExe").value = OLD.toLowerCase();
    assert.strictEqual(c.exePathBoxMoved(), false);
    c.$("#fServerExe").value = NEW;
    assert.strictEqual(c.exePathBoxMoved(), true);
  });

  await rule("2. after a move both lists are the new folder's, without a restart", async () => {
    const p = page({ page: "mods" });
    const c = p.context;
    await c.refreshCfgList(false);
    await c.refreshModCount();
    await c.scanMods({ force: false });
    assert.deepStrictEqual(Array.from(p.CFG.files), ["old.alpha.cfg", "old.beta.cfg"]);
    assert.strictEqual(c.S.modsOnDisk, 7);
    assert.strictEqual(c.S.mods.length, 1);
    p.CFG.file = "old.alpha.cfg"; p.CFG.profile = "Final Sunset";
    c.$("#cfgEditor").value = "text of the old install's old.alpha.cfg";
    /* The disk is now the new install's. It carries a file with the same name as the one open,
       the way real installs share BepInEx.cfg and every mod's .cfg, so the open file is let go
       by the move itself and not by the list noticing the name vanished. */
    p.disk.files = ["old.alpha.cfg", "new.gamma.cfg"]; p.disk.count = 2; p.disk.folder = "E:\\New\\BepInEx\\plugins";
    p.disk.rows = [{ FullName: "New-One", ModName: "One" }, { FullName: "New-Two", ModName: "Two" }];
    p.sent.length = 0;
    await c.rereadRealmDisk();
    await tick(); await tick();
    assert.deepStrictEqual(Array.from(p.CFG.files), ["old.alpha.cfg", "new.gamma.cfg"], "the config list is still the old folder's");
    assert.strictEqual(p.CFG.file, null, "the old install's open file was kept under a name the new folder also has");
    assert.strictEqual(c.$("#cfgEditor").value, "", "the old install's text stayed in the editor, where Save would write it into the new install");
    assert.strictEqual(c.S.modsOnDisk, 2, "the mod count is still the old folder's");
    assert.strictEqual(c.S.modsFolder, "E:\\New\\BepInEx\\plugins");
    assert.deepStrictEqual(c.S.mods.map(m => m.FullName), ["New-One", "New-Two"], "the old scan's rows are still drawn");
    const scan = p.sent.find(s => s.method === "mods.scan");
    assert.ok(scan, "the Mods hall was open and no scan was started");
    assert.strictEqual(scan.params.force, false, "the re-read asked the sites again");
    assert.ok(p.sent.some(s => s.method === "bepinex.status"), "the loader row was not asked again");
    assert.ok(p.cleared.includes("modUpdates"), "the old install's waiting-updates bar was left up");
    assert.ok(p.logs.some(l => /read again/.test(l)), "the Saga does not say the folder was read again");
    /* On another hall there is no scan; the count and the list are still read. */
    const q = page({ page: "saga" });
    await q.context.rereadRealmDisk();
    assert.ok(!q.sent.some(s => s.method === "mods.scan"), "a scan was started with the Mods hall closed");
    assert.ok(q.sent.some(s => s.method === "mods.count") && q.sent.some(s => s.method === "config.list"));
  });

  await rule("3. a scan from before the move is dropped when it lands", async () => {
    const p = page({ page: "mods", hold: ["mods.scan"] });
    const c = p.context;
    const out = c.scanMods({ force: false });
    assert.strictEqual(c.S.modsScanning, true);
    c.forgetRealmDisk();
    assert.strictEqual(c.S.modsScanning, true, "the busy flag went down while the host side was still scanning");
    p.disk.rows = [{ FullName: "New-One", ModName: "One" }];
    p.held["mods.scan"]();
    /* Let the dropped reply go and the scan it starts in its place land too. */
    await tick();
    assert.ok(p.logs.some(l => /moved away from/.test(l)), "the drop was not said in the Saga");
    const scans = p.sent.filter(s => s.method === "mods.scan");
    assert.strictEqual(scans.length, 2, "the hall did not ask the new folder after dropping the old reply");
    p.held["mods.scan"]();
    await out; await tick(); await tick();
    assert.deepStrictEqual((c.S.mods || []).map(m => m.FullName), ["New-One"], "the old install's rows were drawn");
    assert.strictEqual(c.S.modsScanning, false);
  });

  await rule("4. a mod count from before the move is dropped when it lands", async () => {
    const p = page({ hold: ["mods.count"] });
    const c = p.context;
    const out = c.refreshModCount();
    c.forgetRealmDisk();
    p.held["mods.count"]();
    await out;
    assert.strictEqual(c.S.modsOnDisk, null, "the old folder's count was taken");
    assert.strictEqual(c.S.modsFolder, undefined, "the old folder was put back in the Mod folder button");
  });

  await rule("5. the Refresh press reads the disk and never asks the sites", async () => {
    const p = page();
    await p.context.modsRefreshPress();
    assert.ok(p.sent.some(s => s.method === "mods.count"), "the press did not read the count");
    const scan = p.sent.find(s => s.method === "mods.scan");
    assert.ok(scan && scan.params.force === false, "the press did not start the quiet scan: " + JSON.stringify(p.sent));
    const busy = page();
    busy.context.S.modsScanning = true;
    busy.context.modsRefreshPress();
    assert.strictEqual(busy.sent.length, 0);
    assert.deepStrictEqual(busy.toasts, ["\u16a6 " + T("mods.scan.reason.already_running")]);
    const upd = page();
    upd.context.S.modsUpdating = true;
    upd.context.modsRefreshPress();
    assert.strictEqual(upd.sent.length, 0, "a press during Update all sent " + JSON.stringify(upd.sent));
    const pv = page({ native: false });
    pv.context.modsRefreshPress();
    assert.strictEqual(pv.sent.length, 0);
    assert.deepStrictEqual(pv.toasts, ["\u16c7 " + T("mods.refresh.preview.toast")]);
    assert.strictEqual(KEYS["mods.refresh.preview.toast"].mark, "\u16c7", "the toast's rune is not on its catalog entry");
  });

  await rule("6. the wiring around the block", () => {
    const head = HTML.slice(HTML.indexOf('id="page-mods"'), HTML.indexOf('id="modUpdProg"'));
    const refresh = head.indexOf('id="modsRefreshBtn"'), scan = head.indexOf('id="scanBtn"');
    assert.ok(refresh > 0 && scan > refresh, "Refresh is not in the Mods head next to Scan");
    assert.ok(/<button[^>]*id="modsRefreshBtn"[^>]*data-i18n="mods\.refresh\.label"[^>]*data-i18n-title="mods\.refresh\.title"/.test(head),
      "Refresh does not carry its catalog label and title");
    assert.ok(/\$\("#modsRefreshBtn"\)\?\.addEventListener\("click",\(\)=>\{modsRefreshPress\(\);\}\)/.test(APP), "the Refresh click is not bound");
    const renderMods = between("function renderMods(){", "\n}");
    assert.ok(/\$\("#modsRefreshBtn"\);\s*if\(rb\) rb\.disabled=busy;/.test(renderMods), "renderMods does not grey Refresh with Scan");
    const runes = HTML.slice(HTML.indexOf('id="page-runes"'), HTML.indexOf('class="runes-grid"'));
    assert.ok(/<button[^>]*id="cfgReloadBtn"[^>]*data-i18n="runes\.reload\.label"[^>]*data-i18n-title="runes\.reload\.title"/.test(runes),
      "the Configs head has no Reload carrying its label and title");
    assert.ok(/\$\("#cfgReloadBtn"\)\.addEventListener\("click",[\s\S]{0,120}refreshCfgList\(true\)/.test(APP), "Reload does not re-read the list from disk");
    const save = between("async function saveConfigPress(opts){", "\nfunction renderAllFromPrefs(){");
    assert.ok(/\$\("#saveCfgBtn"\)\.addEventListener\("click",\(\)=>\{saveConfigPress\(\);\}\);/.test(APP), "Save Config is not bound to saveConfigPress");
    assert.ok(/renderAllFromPrefs\(\);[\s\S]{0,200}if\(exePathMoved\(cur,r\)\) rereadRealmDisk\(\);/.test(save),
      "Save Config does not re-read Mods and Configs after the executable moved");
    assert.ok(/if\(!\(opts&&opts\.discardRunes\)&&CFG\.dirty&&CFG\.file&&exePathBoxMoved\(\)\)\{\s*confirmModal\(/.test(save),
      "Save Config lets an unsaved config file go without asking");
    assert.ok(save.indexOf("exePathBoxMoved()") < save.indexOf('rpc("profiles.save"'), "the question is asked after the write");
    for (const id of ["world.saved.exe_moved.runes.title", "world.saved.exe_moved.runes.body", "world.saved.exe_moved.runes.discard"])
      assert.ok(save.includes('"' + id + '"'), "the question does not use " + id);
  });

  await rule("7. the host side reads the folder fresh on every call", () => {
    assert.ok(/private string GetServerExePathFor\(string profileName\)\s*=>\s*PathCheck\.Resolve\(StoredServerExePathFor\(profileName\)\)/.test(BRIDGE),
      "the exe path is no longer resolved from the stored profile on each call");
    assert.ok(/ServerPrefsProvider\.LoadPreferences\(profileName\)/.test(BRIDGE.slice(BRIDGE.indexOf("private string StoredServerExePathFor"))),
      "the stored exe path is no longer read off the profile each time");
    const list = BRIDGE.slice(BRIDGE.indexOf('RegisterRpc("config.list"'), BRIDGE.indexOf('RegisterRpc("config.read"'));
    assert.ok(/GetConfigDirectory\(\)/.test(list), "config.list does not ask for the folder each time");
    const count = BRIDGE.slice(BRIDGE.indexOf('RegisterRpc("mods.count"'), BRIDGE.indexOf('RegisterRpc("mods.scan"'));
    assert.ok(/GetPluginsDirectory\(\)/.test(count), "mods.count does not ask for the folder each time");
  });

  for (const id of ["mods.refresh.label", "mods.refresh.title", "mods.refresh.preview.toast", "runes.reload.title",
    "world.saved.exe_moved.runes.title", "world.saved.exe_moved.runes.body", "world.saved.exe_moved.runes.discard"]) {
    const lore = KEYS[id] && KEYS[id].lore;
    if (typeof lore !== "string" || /[\u2014\u2013]/.test(lore)) { failures.push("catalog " + id); console.log("  FAIL catalog " + id); }
  }

  console.log(failures.length ? "disk reread selftest: " + failures.length + " FAILED, " + passed + " passed"
    : "disk reread selftest: all " + passed + " rules pass");
  process.exit(failures.length ? 1 : 0);
})();
