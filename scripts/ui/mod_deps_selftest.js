/* What a mod needs, from 1.2.16, driven for real.
 *
 * WHY THIS EXISTS. A mod was installed by link without the core library it is built on.
 * BepInEx logged "Could not load ... missing dependencies" and the mod never loaded, and
 * nothing in BakaLoader said so. 1.2.16 hands the missing ones back from the add and from
 * every scan; this proves the page says it and offers it, and that the two unattended roads
 * (the Gale walk, a row's Reinstall) still say and offer nothing.
 *
 * THE CODE UNDER TEST is the MODROW block (the pill), the MODMENU block (the row menu entry),
 * the MODDEPS block (the dialog and the walk), doAddMod, hexiumDepMark and renderModNeedsLine,
 * all lifted out of app.js and evaluated with the page's globals stood in for. Sentences from
 * the real en.json.
 *
 *   1. The pill: nothing for a row with no needs or no field, "Needs <name>" for one, "Needs
 *      2 mods" for two, every line on hover, escaped, after the Status pill, no extra cell.
 *   2. The row menu: "Install what it needs" only on a row that needs something, and it opens
 *      the dialog with the row's own list.
 *   3. The dialog: its title, missing and older rows ticked, an off row with Switch on and no
 *      tick, Not now, nothing ticked, and Install held while the server runs.
 *   4. The walk: one add per ticked row, newest build, with the realm and requireStopped, in
 *      order, then one scan; a plain failure goes on, a running server stops it.
 *   5. doAddMod: a pasted link opens the dialog after its scan when the add says something is
 *      missing; a Reinstall (no opts) and an add that needs nothing never do.
 *   6. Hexium's consent list marks each line missing, older or off, and leaves a good one alone.
 *   7. The head sentence counts the rows that need something, and is empty when none do.
 *   8. The host side: the add's answer and the scan's row carry missingDependencies, and every
 *      new catalog id is there with no long dash.
 *
 *     node scripts/ui/mod_deps_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const WEB = path.join(ROOT, "ValheimBakaLoader", "WebUI");
const APP = fs.readFileSync(path.join(WEB, "app.js"), "utf8").replace(/\r\n/g, "\n");
const BRIDGE = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"), "utf8").replace(/\r\n/g, "\n");
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
function fnBody(head) {
  const a = APP.indexOf(head);
  assert.ok(a >= 0, "app.js no longer carries " + head);
  return APP.slice(a, APP.indexOf("\n}", a) + 2);
}

function T(id, params) {
  const entry = KEYS[id];
  if (!entry) throw new Error("the catalog has no sentence for " + id);
  let lore = entry.lore;
  if (lore && typeof lore === "object") lore = (params && Number(params.count) === 1) ? lore.one : lore.other;
  if (typeof lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

/* A dialog as the page's modalOpen would hold it: the markup, and the controls in it that the
   dialog reads or wires, found in the markup the way a browser would find them. */
function fakeModal(html) {
  const control = extra => Object.assign({ disabled: false, checked: false, dataset: {}, handlers: {},
    addEventListener(type, fn) { this.handlers[type] = fn; },
    click() { if (!this.disabled && this.handlers.click) return this.handlers.click(); } }, extra);
  const boxes = [...html.matchAll(/<input type="checkbox" data-dep="(\d+)"( checked)?>/g)]
    .map(m => control({ checked: !!m[2], dataset: { dep: m[1] } }));
  const ons = [...html.matchAll(/<button [^>]*data-dep-on="(\d+)"/g)].map(m => control({ dataset: { depOn: m[1] } }));
  const byId = { "#mOk": control(), "#mCancel": control() };
  return {
    html, boxes, ons, byId,
    querySelector: sel => byId[sel] || null,
    querySelectorAll: sel => sel === "input[data-dep]" ? boxes : sel === "button[data-dep-on]" ? ons : [],
  };
}

const FAIL = { failed: true };

function page(opts) {
  opts = opts || {};
  const sent = [], toasts = [], scans = [], modals = [], logs = [], dialogs = [], switched = [];
  let closed = 0;
  const els = { "#modsNeeds": { textContent: "" } };
  const context = {
    S: { mods: opts.mods || [], profileName: "Final Sunset", state: { status: opts.status || "Stopped" }, modRowStatus: {} },
    Native: { available: opts.native !== false },
    FAIL, T,
    $: sel => els[sel] || null,
    LOC: () => "en-US",
    CFG: { mock: {} },
    modIsHeld: () => false,
    modLatestMark: () => "",
    renderRowStatus: () => "<span>status</span>",
    lastPathPart: p => String(p || "").split(/[\\/]/).filter(Boolean).pop() || "",
    rpc: (method, params) => {
      sent.push({ method, params: JSON.parse(JSON.stringify(params || {})) });
      const r = opts.reply ? opts.reply(method, params, sent.length) : { ok: true };
      return Promise.resolve(r);
    },
    toast: t => toasts.push(t),
    logLine: (k, t) => logs.push(t),
    scanMods: o => { scans.push(o); return Promise.resolve(); },
    renderMods: () => {},
    renderModUpdateProgress: () => {},
    modalOpen: html => { const m = fakeModal(html); modals.push(m); return m; },
    modalClose: () => { closed++; },
    goPage: () => {},
    refreshCfgList: () => Promise.resolve(),
    loadCfg: () => Promise.resolve(),
    document: { querySelectorAll: () => [] },
    isActiveProfile: p => p == null || String(p).toLowerCase() === String(context.S.profileName).toLowerCase(),
    hostSentence: () => null,
    modNotAvailableText: r => (r && r.Reason === "notAvailable" ? "GONE" : null),
    bepInExProgressDone: () => {},
    modResultSource: r => (r && (r.Source || r.source)) || null,
    hexiumFailToast: () => {},
    noticeBepInExMaintained: () => {},
    refreshBepInEx: () => Promise.resolve(),
    bepInExOfferModal: () => {},
    hexiumConsentModal: () => {},
    doInstallFromHexium: () => {},
    BEP_ADD_IN_FLIGHT: false,
    MOD_UPD: null,
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
    between("/* MODDEPS-BEGIN", "/* MODDEPS-END */"),
    fnBody("async function doAddMod(url,extra,opts){"),
    fnBody("function hexiumDepMark("),
    fnBody("function renderModNeedsLine("),
  ].join("\n").replace(/^const /gm, "var ").replace(/^let /gm, "var ");
  vm.runInContext(code, context, { filename: "app.js#moddeps" });
  /* The dialog is spied on where a test asks, and is otherwise the real one. */
  if (opts.spyDialog) context.modDepsDialog = (who, needs) => { dialogs.push({ who, needs: JSON.parse(JSON.stringify(needs)) }); };
  /* Switch on is the Mods hall's own setModEnabled, which mod_row_selftest drives; here it is
     only seen to be reached with the right row. */
  context.setModEnabled = (mod, on) => { switched.push({ mod: JSON.parse(JSON.stringify(mod)), on }); return Promise.resolve(); };
  return { context, sent, toasts, scans, modals, logs, dialogs, switched, els, closed: () => closed };
}

const settle = async () => { for (let i = 0; i < 30; i++) await new Promise(r => setImmediate(r)); };

const CORE = { fullName: "MySix-MySix_Core", name: "MySix_Core", minVersion: "1.0.2", state: "missing", requiredBy: "MySix-MySix_ForgeOfPotential" };
const JOTUNN = { fullName: "ValheimModding-Jotunn", name: "Jotunn", minVersion: "2.20.0", state: "older", installedVersion: "2.19.1", requiredBy: "MySix-MySix_Core" };
const OFF = { fullName: "Azumatt-AzuCraftyBoxes", name: "AzuCraftyBoxes", minVersion: "1.8.0", state: "off", installedVersion: "1.8.4", requiredBy: "MySix-MySix_ForgeOfPotential" };
const ROW = {
  ModName: "MySix_ForgeOfPotential", Author: "MySix", FullName: "MySix-MySix_ForgeOfPotential",
  InstalledVersion: "1.0.0", LatestVersion: "1.0.0", thunderstoreNamespace: "MySix", thunderstoreName: "MySix_ForgeOfPotential",
  installedSource: "thunderstore", installedUtc: "2026-10-20T10:10:00Z", modUpdatedUtc: "2026-10-01T00:00:00Z",
  disabled: false, layout: "managed", missingDependencies: [],
};
const mod = extra => Object.assign({}, ROW, extra || {});
const labels = items => Array.from(items, it => it === "hr" ? "hr" : (it.section ? "[" + it.section + "]" : it.label));

(async function main() {
  await test("1. the pill: none, one named, two counted, every line on hover, after the Status pill", () => {
    const p = page();
    assert.strictEqual(p.context.modNeedsPill(mod()), "", "a row with nothing missing has a pill");
    const hand = mod(); delete hand.missingDependencies;
    assert.strictEqual(p.context.modNeedsPill(hand), "", "a row from a scan with no field has a pill");
    const one = p.context.modNeedsPill(mod({ missingDependencies: [CORE] }));
    assert.ok(one.includes('class="pill amber modneeds"'), one);
    assert.ok(one.includes(">" + T("mods.deps.pill.one", { name: "MySix_Core" }) + "</span>"), one);
    assert.strictEqual(T("mods.deps.pill.one", { name: "MySix_Core" }), "Needs MySix_Core");
    const two = p.context.modNeedsPill(mod({ missingDependencies: [CORE, JOTUNN] }));
    assert.ok(two.includes(">Needs 2 mods</span>"), two);
    const tip = (two.match(/title="([^"]*)"/) || [])[1];
    assert.strictEqual(tip, p.context.esc([T("mods.deps.pill.tip"),
      T("mods.deps.line.missing.version", { name: "MySix_Core", version: "1.0.2" }),
      T("mods.deps.line.older", { name: "Jotunn", installed: "2.19.1", version: "2.20.0" })].join("\n")), "the tooltip does not list them");
    const bad = p.context.modNeedsPill(mod({ missingDependencies: [{ fullName: "a-<b>", name: "<b>", state: "missing" }] }));
    assert.ok(!bad.includes("<b>") && bad.includes("&lt;b&gt;"), "the name is not escaped: " + bad);
    const row = p.context.modRowHtml(mod({ missingDependencies: [CORE] }), false);
    const status = row.indexOf(">" + T("mods.status.current") + "</span>");
    assert.ok(status > 0 && row.indexOf("modneeds") > status, "the pill is not after the Status pill");
    assert.strictEqual((row.match(/<td[\s>]/g) || []).length, p.context.modColCount(), "the pill took a cell of its own");
    assert.strictEqual(p.context.modDepLine(OFF), "AzuCraftyBoxes is installed but switched off");
    assert.strictEqual(p.context.modDepLine({ fullName: "A-B", name: "B", state: "missing" }), "B is not installed");
  });

  await test("2. the row menu: Install what it needs only when something is needed, with the row's list", () => {
    const p = page({ spyDialog: true });
    assert.ok(!labels(p.context.modRowItems(mod())).includes(T("mods.menu.install_needs")), "a row that needs nothing is offered the entry");
    const items = p.context.modRowItems(mod({ missingDependencies: [CORE, OFF] }));
    const it = items.find(x => x.label === T("mods.menu.install_needs"));
    assert.ok(it, "a row that needs something is not offered the entry");
    const l = labels(items);
    assert.ok(l.indexOf(T("mods.menu.install_needs")) < l.indexOf(T("mods.menu.disable")), "the entry is not with the install entries");
    it.fn();
    assert.strictEqual(p.dialogs.length, 1);
    assert.strictEqual(p.dialogs[0].who, "MySix_ForgeOfPotential");
    assert.deepStrictEqual(p.dialogs[0].needs.map(n => n.fullName), ["MySix-MySix_Core", "Azumatt-AzuCraftyBoxes"]);
  });

  await test("3. the dialog: title, ticks, Switch on for an off row, Not now, nothing ticked, held while running", async () => {
    const p = page({ native: false });
    let walked = null;
    const m = p.context.modDepsDialog("MySix_ForgeOfPotential", [CORE, JOTUNN, OFF]);
    p.context.modDepsInstall = list => { walked = list; };
    assert.ok(m.html.includes(p.context.esc(T("mods.deps.dialog.title", { mod: "MySix_ForgeOfPotential" }))), "no title");
    assert.strictEqual(T("mods.deps.dialog.title", { mod: "X" }), "X needs these to load");
    assert.deepStrictEqual(m.boxes.map(b => [b.dataset.dep, b.checked]), [["0", true], ["1", true]], "missing and older are not both ticked");
    assert.strictEqual(m.ons.length, 1, "the off row has no Switch on");
    assert.ok(!/data-dep="2"/.test(m.html), "the off row has a tick");
    assert.ok(m.html.includes(">" + T("mods.deps.dialog.later") + "</button>") && T("mods.deps.dialog.later") === "Not now");
    assert.ok(m.html.includes(">" + T("common.button.install") + "</button>"));
    assert.strictEqual(m.byId["#mOk"].disabled, false);
    await m.ons[0].click();
    assert.deepStrictEqual(p.switched, [{ mod: { FullName: "Azumatt-AzuCraftyBoxes", ModName: "AzuCraftyBoxes" }, on: true }]);
    assert.strictEqual(m.ons[0].disabled, true, "Switch on can be pressed twice");
    m.boxes[1].checked = false;
    m.byId["#mOk"].click();
    assert.deepStrictEqual(Array.from(walked, n => n.fullName), ["MySix-MySix_Core"], "Install did not take the ticked rows only");
    /* Not now closes and fetches nothing. */
    const q = page();
    let walkedQ = 0;
    const n = q.context.modDepsDialog("X", [CORE]);
    q.context.modDepsInstall = () => { walkedQ++; };
    n.byId["#mCancel"].click();
    assert.strictEqual(q.closed(), 1);
    assert.strictEqual(walkedQ, 0);
    /* Nothing ticked is said, and nothing is fetched. */
    const r = page();
    const o = r.context.modDepsDialog("X", [CORE]);
    o.boxes[0].checked = false;
    o.byId["#mOk"].click();
    assert.deepStrictEqual(r.toasts, ["ᚨ " + T("mods.deps.install.none.toast")]);
    assert.strictEqual(r.sent.length, 0);
    /* A running server: the warning, and Install held. */
    const up = page({ status: "Running" });
    const u = up.context.modDepsDialog("X", [CORE]);
    assert.ok(u.html.includes(up.context.esc(T("mods.deps.dialog.running"))), "no running warning");
    assert.strictEqual(u.byId["#mOk"].disabled, true, "Install is offered while the server runs");
    assert.strictEqual(page().context.modDepsDialog("X", []), null, "an empty list opened a dialog");
  });

  await test("4. the walk: one add per row, newest build, realm and requireStopped, then one scan", async () => {
    const p = page({ reply: (method, params) => (method === "mods.addFromUrl" ? { Installed: true, Version: "9.9.9" } : { ok: true }) });
    await p.context.modDepsInstall([CORE, JOTUNN]);
    assert.deepStrictEqual(p.sent.map(s => s.method), ["mods.addFromUrl", "mods.addFromUrl"]);
    assert.deepStrictEqual(p.sent[0].params, { url: "https://thunderstore.io/c/valheim/p/MySix/MySix_Core/", profile: "Final Sunset", requireStopped: true });
    assert.strictEqual(p.sent[1].params.url, "https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/");
    assert.strictEqual(p.scans.length, 1, "the table was not scanned once after the walk");
    assert.strictEqual(p.context.S.modsUpdating, false, "the walk left the hall busy");
    assert.strictEqual(p.toasts[p.toasts.length - 1], "ᚨ " + T("mods.deps.install.done.toast", { count: 2 }));
    /* A plain failure is said and the walk goes on. */
    const f = page({ reply: (m, params, n) => (n === 1 ? { Installed: false, Error: "zip was short" } : { Installed: true }) });
    await f.context.modDepsInstall([CORE, JOTUNN]);
    assert.strictEqual(f.sent.length, 2, "one failure stopped the walk");
    assert.ok(f.toasts.includes("ᚦ " + T("mods.add.failed.toast", { detail: "zip was short" })), f.toasts.join(" | "));
    assert.ok(f.toasts.includes("ᚦ " + T("mods.deps.install.partial.toast", { done: 1, total: 2 })), f.toasts.join(" | "));
    /* A server that is up stops it: the next row would be refused the same way. */
    const s = page({ reply: () => ({ Installed: false, Reason: "serverRunning", ErrorParams: { name: "Final Sunset" } }) });
    await s.context.modDepsInstall([CORE, JOTUNN]);
    assert.strictEqual(s.sent.length, 1, "the walk went on past a running server");
    assert.ok(s.toasts.includes("ᚦ " + T("mods.deps.install.server_running.toast", { name: "Final Sunset" })), s.toasts.join(" | "));
    /* The preview sends nothing. */
    const pv = page({ native: false });
    await pv.context.modDepsInstall([CORE]);
    assert.strictEqual(pv.sent.length, 0);
  });

  await test("5. doAddMod: a pasted link offers after its scan; a Reinstall and a satisfied add never do", async () => {
    const answer = { Installed: true, Owner: "MySix", Name: "MySix_ForgeOfPotential", Version: "1.0.0", missingDependencies: [CORE] };
    const p = page({ spyDialog: true, reply: () => answer });
    await p.context.doAddMod("https://thunderstore.io/c/valheim/p/MySix/MySix_ForgeOfPotential/", undefined, { offerDeps: true });
    assert.strictEqual(p.scans.length, 1);
    assert.strictEqual(p.dialogs.length, 1, "the pasted link did not offer what it needs");
    assert.strictEqual(p.dialogs[0].who, "MySix_ForgeOfPotential");
    assert.deepStrictEqual(p.dialogs[0].needs, [CORE]);
    assert.ok(p.logs.some(l => /needs: MySix-MySix_Core \(missing\)/.test(l)), "the Saga does not say what it needs");
    const re = page({ spyDialog: true, reply: () => answer });
    await re.context.doAddMod("https://thunderstore.io/c/valheim/p/MySix/MySix_ForgeOfPotential/v/1.0.0/", { profile: "Final Sunset", requireStopped: true });
    assert.strictEqual(re.dialogs.length, 0, "a Reinstall offered the dependencies");
    assert.deepStrictEqual(re.sent.map(s => s.method), ["mods.addFromUrl"], "a Reinstall made another call");
    const ok = page({ spyDialog: true, reply: () => Object.assign({}, answer, { missingDependencies: [] }) });
    await ok.context.doAddMod("https://thunderstore.io/c/valheim/p/MySix/MySix_ForgeOfPotential/", undefined, { offerDeps: true });
    assert.strictEqual(ok.dialogs.length, 0, "an add that needs nothing opened the dialog");
    const old = page({ spyDialog: true, reply: () => ({ Installed: true, Owner: "A", Name: "B" }) });
    await old.context.doAddMod("https://thunderstore.io/c/valheim/p/A/B/", undefined, { offerDeps: true });
    assert.strictEqual(old.dialogs.length, 0, "an answer with no field opened the dialog");
    /* Only the paste passes opts, and the BepInEx offer finishes the same add with them. */
    const flow = fnBody("function addModFlow(");
    assert.ok(flow.includes("doAddMod(url,undefined,{offerDeps:true})"), "the paste does not ask for the offer");
    assert.ok(APP.includes("bepInExOfferModal(()=>doAddMod(url,extra,opts))"), "the BepInEx offer drops the offer");
    const swap = fnBody("function thunderstoreSwapFlow(");
    assert.ok(!/offerDeps/.test(swap) && !/offerDeps/.test(fnBody("function reinstallModFlow(")), "a swap or a Reinstall asks for the offer");
  });

  await test("6. Hexium's consent list: missing, older and off marked, a good line left alone", () => {
    const p = page();
    const marks = [{ fullName: "MySix-MySix_Core", state: "missing" }, { fullName: "valheimmodding-jotunn", state: "older" }, OFF];
    const mark = d => p.context.hexiumDepMark(d, marks);
    assert.strictEqual(mark("MySix-MySix_Core-1.0.2"), ' <span class="moddep-mark">' + T("mods.deps.mark.missing") + "</span>");
    assert.ok(mark("ValheimModding-Jotunn-2.20.0").includes(T("mods.deps.mark.older")), "the name is matched with its case");
    assert.ok(mark("Azumatt-AzuCraftyBoxes-1.8.0").includes(T("mods.deps.mark.off")));
    assert.strictEqual(mark("denikson-BepInExPack_Valheim-5.4.2350"), "", "a line that is there is marked");
    assert.strictEqual(p.context.hexiumDepMark("A-B-1.0.0", null), "");
    const modal = fnBody("function hexiumConsentModal(");
    assert.ok(modal.includes("esc(d)+hexiumDepMark(d,marks)") && modal.includes("pay.DependencyMarks"), "the consent list does not carry the marks");
  });

  await test("7. the head sentence counts the rows that need something", () => {
    const p = page({ mods: [mod({ missingDependencies: [CORE] }), mod({ FullName: "a-b" }), mod({ FullName: "c-d", missingDependencies: [CORE, OFF] })] });
    p.context.renderModNeedsLine();
    assert.strictEqual(p.els["#modsNeeds"].textContent, T("mods.index.needs", { count: 2 }));
    assert.strictEqual(T("mods.index.needs", { count: 2 }), "2 mods need something they do not have");
    p.context.S.mods = [mod()];
    p.context.renderModNeedsLine();
    assert.strictEqual(p.els["#modsNeeds"].textContent, "");
    p.context.S.mods = null;
    p.context.renderModNeedsLine();
    assert.strictEqual(p.els["#modsNeeds"].textContent, "", "an unscanned hall counts something");
    const html = fs.readFileSync(path.join(WEB, "index.html"), "utf8");
    assert.ok(/<span class="sub modsneeds" id="modsNeeds"/.test(html), "index.html has no place for the sentence");
    assert.ok(fnBody("function renderModIndexLine(").includes("renderModNeedsLine();"), "the index line does not draw it");
  });

  await test("8. the host side carries the field, and every new id is in the catalog", () => {
    const add = BRIDGE.slice(BRIDGE.indexOf('RegisterRpc("mods.addFromUrl"'), BRIDGE.indexOf('RegisterRpc("mods.hexiumPrepare"'));
    assert.ok(/missingDependencies = missing\.Select\(DependencyNeedDto\)\.ToArray\(\)/.test(add), "the add's answer has no missingDependencies");
    assert.ok(/missingDependencies = Array\.Empty<object>\(\)/.test(add), "a refused add has a different shape");
    const dto = BRIDGE.slice(BRIDGE.indexOf("public static object BuildModDto(Tools.Models.InstalledMod mod, DateTime? gameUpdatedUtc, DateTime nowUtc)"));
    assert.ok(/missingDependencies = \(mod\.MissingDependencies/.test(dto), "the scan's row has no missingDependencies");
    assert.ok(/DependencyMarks = hexiumNeeds/.test(BRIDGE), "the Hexium plan has no marks");
    const ids = new Set();
    for (const block of [between("/* MODDEPS-BEGIN", "/* MODDEPS-END */"), fnBody("function modNeedsPill("), fnBody("function modDepLine("),
                         fnBody("function hexiumDepMark("), fnBody("function renderModNeedsLine(")])
      for (const m of block.matchAll(/T\("([a-z0-9_.]+)"/g)) ids.add(m[1]);
    ids.add("mods.menu.install_needs");
    for (const id of ids) {
      assert.ok(KEYS[id], "the catalog has no " + id);
      const text = JSON.stringify(KEYS[id].lore);
      assert.ok(![0x2013, 0x2014].some(c => text.includes(String.fromCharCode(c))), id + " carries a long dash");
      assert.ok(!/\bAI\b/.test(text), id + " uses a word the copy rules ban");
    }
  });

  console.log("");
  console.log(failures.length ? "mod deps selftest: " + failures.length + " FAILED, " + passed + " passed"
    : "mod deps selftest: all " + passed + " rules pass");
  process.exit(failures.length ? 1 : 0);
})();
