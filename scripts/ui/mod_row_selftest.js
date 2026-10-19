/* A Mods row and its menu, from issue 20, driven for real.
 *
 * WHY THIS EXISTS. 1.2.15 gave the row four new things to say and the menu four new things to
 * do, and every one of them hangs on a field the scan hands over (contract_BC: installedUtc,
 * installedSource, disabled, layout). A row that drew the field wrong, or a menu that offered
 * the wrong entry, would read perfectly well in a screenshot of the happy case. So the cases
 * are each drawn here, through the page's own functions.
 *
 * THE CODE UNDER TEST is the block between the MODROW markers and the block between the
 * MODMENU markers in app.js, with the functions after MODMENU-END that its entries call
 * (openModConfig, setModEnabled, reinstallModFlow) and the Configs hall's path helpers
 * (CFGITEM), evaluated with the page's globals stood in for. Sentences from the real en.json.
 *
 *   1. Dates: the day in YYYY-MM-DD, local time, with the whole timestamp on hover; a blank
 *      cell for a date nobody knows.
 *   2. Installed: the version in the Hexium chip's style for hexium and for thunderstore, each
 *      with its own tooltip, and no pill for a hand-copied folder.
 *   3. Off: a switched-off row is dimmed (tr.off) and its Status pill says Off with the
 *      .dll.old tooltip; the cell count matches the header.
 *   4. The menu: Switch off or Switch on by state, greyed while the realm runs, under the
 *      update entries; Reinstall this version only with a package and a version and never for
 *      a Hexium copy or a patcher, with the mod manager tooltip on a nested row; Configs above
 *      Remove.
 *   5. The Configs section: looking, none, and one entry per file with the folders muted and
 *      the name bold, every part escaped; a click opens the Configs hall on that file.
 *   6. The configs are asked once per mod per scan, and forgotten when a scan replaces the
 *      list or the realm changes.
 *   7. Switch off and on send mods.disable / mods.enable with the realm and the mod, scan
 *      again, and put the host's refusal in a toast.
 *   8. Reinstall sends the Mods hall's own add with the pinned Thunderstore address.
 *
 *     node scripts/ui/mod_row_selftest.js
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
async function test(name, body) {
  try { await body(); passed++; console.log("  ok   " + name); }
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

function T(id, params) {
  const entry = KEYS[id];
  if (!entry || typeof entry.lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return entry.lore.replace(/\\u([0-9a-fA-F]{4})/g, (_, h) => String.fromCharCode(parseInt(h, 16)))
    .replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

const FAIL = { failed: true };

function page(opts) {
  opts = opts || {};
  const sent = [], toasts = [], scans = [], adds = [], pages = [], loads = [];
  const context = {
    S: { mods: [], profileName: "Final Sunset", state: { status: opts.status || "Stopped" }, modRowStatus: {} },
    Native: { available: opts.native !== false },
    FAIL, T,
    esc: null, pad: null,
    LOC: () => "en-US",
    CFG: { mock: opts.mock || {} },
    modIsHeld: () => false,
    modLatestMark: () => "",
    renderRowStatus: () => "<span>status</span>",
    modDateValue: null,
    lastPathPart: p => String(p || "").split(/[\\/]/).filter(Boolean).pop() || "",
    rpc: (method, params) => {
      sent.push({ method, params: JSON.parse(JSON.stringify(params || {})) });
      const r = opts.reply ? opts.reply(method, params) : { ok: true };
      return Promise.resolve(r);
    },
    toast: t => toasts.push(t),
    logLine: () => {},
    scanMods: o => { scans.push(o); return Promise.resolve(); },
    doAddMod: (url, extra) => { adds.push({ url, extra }); },
    goPage: n => pages.push(n),
    refreshCfgList: () => Promise.resolve(),
    loadCfg: (f, force) => { loads.push(f); return Promise.resolve(); },
    document: { querySelectorAll: () => [] },
  };
  vm.createContext(context);
  const code = [
    line("const esc=").replace(/^const /, "var "),
    line("const pad=").replace(/^const /, "var "),
    between("function modDateValue(", "\n/* Two dates, with a blank"),
    between("function galeModUrl(", "\n/* The Mods hall's own sentence"),
    between("/* CFGITEM-BEGIN", "/* CFGITEM-END */"),
    between("/* MODROW-BEGIN", "/* MODROW-END */"),
    between("/* MODMENU-BEGIN", "\n/* What one check's answer does to the row"),
  ].join("\n").replace(/^const /gm, "var ").replace(/^let /gm, "var ");
  vm.runInContext(code, context, { filename: "app.js#modrow" });
  for (const name of ["modRowHtml", "modDateText", "modRowItems", "modConfigItems", "modConfigsFor", "setModEnabled", "reinstallModFlow", "openModConfig"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of app.js");
  return { context, sent, toasts, scans, adds, pages, loads };
}

const BASE = {
  ModName: "PlanBuild", Author: "MathiasDecrock", FullName: "MathiasDecrock-PlanBuild",
  InstalledVersion: "0.16.4", LatestVersion: "0.16.4", thunderstoreNamespace: "MathiasDecrock", thunderstoreName: "PlanBuild",
  installedSource: "thunderstore", installedUtc: "2026-04-05T10:10:00Z", modUpdatedUtc: "2026-09-09T00:00:00Z",
  disabled: false, layout: "managed",
};
const mod = extra => Object.assign({}, BASE, extra || {});
const labels = items => Array.from(items, it => it === "hr" ? "hr" : (it.section ? "[" + it.section + "]" : it.label));

(async function main() {
  await test("1. dates: the local day, the whole stamp on hover, a blank for none", () => {
    const p = page();
    const iso = "2026-10-14T19:49:41Z";
    const d = new Date(iso);
    const want = d.getFullYear() + "-" + String(d.getMonth() + 1).padStart(2, "0") + "-" + String(d.getDate()).padStart(2, "0");
    assert.strictEqual(p.context.modDateText(iso), want);
    const cell = p.context.modDateCell(iso);
    assert.ok(cell.includes(">" + want + "</td>"), cell);
    assert.ok(cell.includes('title="' + d.toLocaleString("en-US") + '"'), "the whole timestamp is not on hover: " + cell);
    assert.strictEqual(p.context.modDateCell(null), '<td class="mono mod-date"></td>');
    assert.strictEqual(p.context.modDateCell("never"), '<td class="mono mod-date"></td>');
  });

  await test("2. Installed: a pill for each known source, none for a hand copy", () => {
    const p = page();
    const ts = p.context.modInstalledCell(mod());
    assert.ok(ts.includes('class="modchip modver"') && ts.includes(T("mods.tag.installed.thunderstore.tip")), ts);
    const hx = p.context.modInstalledCell(mod({ installedSource: "hexium" }));
    assert.ok(hx.includes('class="modchip modver"') && hx.includes(T("mods.tag.installed.hexium.tip")), hx);
    assert.ok(/may show a different version/.test(T("mods.tag.installed.hexium.tip")), "the Hexium tooltip does not say the two columns can differ");
    const hand = p.context.modInstalledCell(mod({ installedSource: null }));
    assert.strictEqual(hand, '<td class="mono">0.16.4</td>');
    assert.ok(p.context.modInstalledCell(mod({ InstalledVersion: '<b>"x"</b>' })).includes("&lt;b&gt;&quot;x&quot;&lt;/b&gt;"), "the version is not escaped");
  });

  await test("3. Off: a dimmed row, an Off pill that says why, and a cell for every header", () => {
    const p = page();
    const row = p.context.modRowHtml(mod({ disabled: true }), false);
    assert.ok(/^<tr data-key="MathiasDecrock-PlanBuild" class="off">/.test(row), row.slice(0, 80));
    assert.ok(row.includes(">" + T("mods.status.off") + "</span>") && row.includes(T("mods.status.off.tip")), "no Off pill");
    assert.ok(/\.dll\.old/.test(T("mods.status.off.tip")) && /BepInEx skips/.test(T("mods.status.off.tip")));
    const on = p.context.modRowHtml(mod(), false);
    assert.ok(!/class="off"/.test(on), "a row that is on is dimmed");
    const cells = (on.match(/<td[\s>]/g) || []).length;
    const head = HTML.slice(HTML.indexOf('<table id="modsTable">'), HTML.indexOf('<tbody id="modTable">'));
    assert.strictEqual(cells, (head.match(/<th[\s>]/g) || []).length, "a row's cells and the header's columns disagree");
    assert.strictEqual(p.context.modColCount(), cells, "modColCount is not the row's cell count");
  });

  await test("4. the menu: switch, reinstall and configs, each where and when it belongs", () => {
    const p = page();
    const items = p.context.modRowItems(mod({ layout: "nested" }));
    const l = labels(items);
    const at = x => l.indexOf(x);
    assert.ok(at(T("mods.menu.update")) >= 0 && at(T("mods.menu.update")) < at(T("mods.menu.disable")), "Switch off is not under the update entries: " + l.join(" | "));
    assert.ok(at(T("mods.menu.disable")) < at("[" + T("mods.menu.configs") + "]"), "Configs is not under the switch");
    assert.ok(at("[" + T("mods.menu.configs") + "]") < at(T("mods.menu.remove")), "Configs is not above Remove");
    const re = items.find(it => it.label === T("mods.menu.reinstall"));
    assert.ok(re, "a nested Thunderstore row is not offered Reinstall");
    assert.strictEqual(re.title, "Lays the files out the way a mod manager would");
    const managed = p.context.modRowItems(mod()).find(it => it.label === T("mods.menu.reinstall"));
    assert.ok(managed && !managed.title, "a managed row's Reinstall carries the nested tooltip");
    for (const [why, extra] of [["a Hexium copy", { installedSource: "hexium" }], ["a patcher-only row", { IsPatcher: true }],
                                ["no package", { thunderstoreNamespace: null }], ["no version", { InstalledVersion: "" }]])
      assert.ok(!labels(p.context.modRowItems(mod(extra))).includes(T("mods.menu.reinstall")), why + " is offered Reinstall");
    const off = labels(p.context.modRowItems(mod({ disabled: true })));
    assert.ok(off.includes(T("mods.menu.enable")) && !off.includes(T("mods.menu.disable")), "a switched-off row is not offered Switch on");
    const sw = p.context.modRowItems(mod()).find(it => it.label === T("mods.menu.disable"));
    assert.strictEqual(sw.disabled, false, "Switch off is greyed on a stopped realm");
    const up = page({ status: "Running" });
    const swUp = up.context.modRowItems(mod()).find(it => it.label === T("mods.menu.disable"));
    assert.strictEqual(swUp.disabled, true, "Switch off is offered while the server runs");
    assert.strictEqual(swUp.tip, T("mods.menu.toggle.tip.running"));
    /* Update entries keep working on an off row. */
    const offUpd = page().context.modRowItems(mod({ disabled: true, UpdateAvailable: true })).find(it => it.label === T("mods.menu.update"));
    assert.strictEqual(offUpd.disabled, false, "a switched-off row cannot be updated");
  });

  await test("5. the Configs section: looking, none, and one escaped entry per file", async () => {
    const p = page();
    const looking = labels(p.context.modConfigItems(mod(), undefined));
    assert.deepStrictEqual(looking, ["[" + T("mods.menu.configs") + "]", T("mods.menu.configs.loading")]);
    const none = p.context.modConfigItems(mod(), []);
    assert.strictEqual(none[1].label, "No config files carry this mod's name");
    assert.strictEqual(none[1].disabled, true);
    const files = p.context.modConfigItems(mod(), ["ModNameFolder/com.someModAuthor.ModName.ExtraSettings.cfg", "a<b>.cfg"]);
    assert.strictEqual(files[1].html, '<span class="cfgpre">ModNameFolder/</span><b>com.someModAuthor.ModName.ExtraSettings.cfg</b>');
    assert.strictEqual(files[1].title, "ModNameFolder/com.someModAuthor.ModName.ExtraSettings.cfg");
    assert.strictEqual(files[2].html, '<span class="cfgpre"></span><b>a&lt;b&gt;.cfg</b>');
    assert.ok(files[1].wide, "a path entry does not ask for the wider menu");
    await files[1].fn();
    assert.deepStrictEqual(p.pages, ["runes"], "the entry did not open the Configs hall");
    assert.deepStrictEqual(p.loads, ["ModNameFolder/com.someModAuthor.ModName.ExtraSettings.cfg"], "the entry did not open its file");
    /* And ctxOpen draws a section and a markup label the way the items ask. */
    const open = between("function ctxOpen(", "\nctxEl.addEventListener(");
    assert.ok(open.includes('it.section?`<div class="ctxsec">${esc(it.section)}</div>`'), "ctxOpen does not draw a section label");
    assert.ok(open.includes("${it.html||esc(it.label)}"), "ctxOpen does not draw a markup label");
    assert.ok(open.includes('ctxEl.classList.toggle("wide",items.some(it=>it&&it.wide));'));
  });

  await test("6. configs are asked once per mod per scan, and forgotten by a scan or a switch", async () => {
    const p = page({ reply: () => ({ files: ["PlanBuild/MathiasDecrock.PlanBuild.Blueprints.json"] }) });
    p.context.S.mods = [mod()];
    assert.strictEqual(p.context.modConfigsCached(mod()), undefined);
    const first = await p.context.modConfigsFor(mod());
    assert.deepStrictEqual(Array.from(first), ["PlanBuild/MathiasDecrock.PlanBuild.Blueprints.json"]);
    await p.context.modConfigsFor(mod());
    assert.strictEqual(p.sent.filter(s => s.method === "mods.configs").length, 1, "the menu asked twice in one scan");
    assert.deepStrictEqual(p.sent[0].params, { profile: "Final Sunset", fullName: "MathiasDecrock-PlanBuild" });
    const items = labels(p.context.modRowItems(mod()));
    assert.ok(items.includes("PlanBuild/MathiasDecrock.PlanBuild.Blueprints.json"), "the cached files are not in the menu");
    p.context.S.mods = [mod()];          // a scan hands over a new list
    assert.strictEqual(p.context.modConfigsCached(mod()), undefined, "a scan did not forget the files");
    await p.context.modConfigsFor(mod());
    p.context.S.profileName = "Midgard Test";
    assert.strictEqual(p.context.modConfigsCached(mod()), undefined, "a realm switch did not forget the files");
    /* A reply that lands after the list changed is not kept for the new list. */
    let release;
    const slow = page({ reply: () => new Promise(r => { release = () => r({ files: ["old.cfg"] }); }) });
    slow.context.S.mods = [mod()];
    const pending = slow.context.modConfigsFor(mod());
    slow.context.S.mods = [mod()];
    release();
    await pending;
    assert.strictEqual(slow.context.modConfigsCached(mod()), undefined, "a late reply was kept for a list it was not about");
    /* The preview works the files out of its own mock with the host side's rule. */
    const pv = page({ native: false, mock: { "ExtraSlots/shudnal.ExtraSlots.CustomSlots.yaml": "", "shudnal.ExtraSlots.cfg": "", "BepInEx.cfg": "" } });
    pv.context.S.mods = [];
    const got = await pv.context.modConfigsFor({ ModName: "ExtraSlots", Author: "shudnal", FullName: "shudnal-ExtraSlots", thunderstoreName: "ExtraSlots" });
    assert.deepStrictEqual(Array.from(got), ["ExtraSlots/shudnal.ExtraSlots.CustomSlots.yaml", "shudnal.ExtraSlots.cfg"]);
  });

  await test("7. switch off and on: the right call, a fresh scan, the host's refusal in a toast", async () => {
    const p = page();
    await p.context.setModEnabled(mod(), false);
    assert.deepStrictEqual(p.sent.map(s => s.method), ["mods.disable"]);
    assert.deepStrictEqual(p.sent[0].params, { profile: "Final Sunset", fullName: "MathiasDecrock-PlanBuild" });
    assert.strictEqual(p.scans.length, 1, "the table was not scanned again");
    assert.deepStrictEqual(p.toasts, ["ᚱ " + T("mods.disable.done.toast", { mod: "PlanBuild" })]);
    await p.context.setModEnabled(mod({ disabled: true }), true);
    assert.strictEqual(p.sent[1].method, "mods.enable");
    const refused = page({ reply: () => ({ ok: false, error: "Stop the server 'Final Sunset' first." }) });
    await refused.context.setModEnabled(mod(), false);
    assert.strictEqual(refused.scans.length, 0, "a refusal scanned anyway");
    assert.deepStrictEqual(refused.toasts, ["ᚦ " + T("mods.toggle.failed.toast", { mod: "PlanBuild", detail: "Stop the server 'Final Sunset' first." })]);
    const pv = page({ native: false });
    await pv.context.setModEnabled(mod(), false);
    assert.strictEqual(pv.sent.length, 0, "the preview sent a call");
    assert.deepStrictEqual(pv.toasts, ["ᚱ " + T("mods.toggle.preview.toast")]);
  });

  await test("8. reinstall: the Mods hall's own add, pinned to the installed version", () => {
    const p = page();
    p.context.reinstallModFlow(mod({ layout: "nested" }));
    assert.strictEqual(p.adds.length, 1);
    assert.strictEqual(p.adds[0].url, "https://thunderstore.io/c/valheim/p/MathiasDecrock/PlanBuild/v/0.16.4/");
    assert.deepStrictEqual(JSON.parse(JSON.stringify(p.adds[0].extra)), { profile: "Final Sunset", requireStopped: true });
    p.context.reinstallModFlow(mod({ thunderstoreName: null }));
    assert.strictEqual(p.adds.length, 1, "a row with no package was reinstalled");
    /* doAddMod hands the two keys on to the host side. */
    const add = between("async function doAddMod(url,extra){", "\n$(\"#addModBtn\")");
    assert.ok(add.includes('rpc("mods.addFromUrl",Object.assign({url},extra||{}))'), "doAddMod drops the realm and requireStopped");
    /* and the two refusals only a reinstall can meet are worded as a reinstall, not as a Gale walk */
    assert.ok(add.includes('r.Reason==="serverRunning"') && add.includes('T("mods.reinstall.server_running.toast",'), "a running server's refusal is not worded");
    assert.ok(add.includes('T("mods.reinstall.realm_changed.toast")'), "a changed realm's refusal is not worded");
    assert.ok(/nothing was reinstalled/.test(T("mods.reinstall.server_running.toast", { name: "x" })) && !/Review/.test(T("mods.reinstall.server_running.toast", { name: "x" })));
  });

  console.log("");
  if (failures.length) {
    console.log("mod row selftest: " + failures.length + " FAILED, " + passed + " passed");
    process.exit(1);
  }
  console.log("mod row selftest: all " + passed + " rules pass");
})();
