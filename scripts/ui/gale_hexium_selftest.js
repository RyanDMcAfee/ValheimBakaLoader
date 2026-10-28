/* A Gale profile mod that is only on Hexium, and the Failed pill a Gale walk used to leave
 * behind (1.3.1, issue 30), driven for real.
 *
 * WHY THIS EXISTS. A profile can pin a mod Thunderstore never carried. The review called it
 * "removed", offered nothing, and the walk left a red "Failed: ..." pill on the Mods row that
 * ran out of its column and outlived every later install. The fixes are all quiet ways to go
 * wrong: a row that installs from a second site without asking the host about that one mod, a
 * consent token kept from earlier and used after it went stale, two mods put to the host in one
 * question, a review that asks Hexium again every time it opens, a walk that keeps going for a
 * realm the host switched away from, and a pill that still overflows or still outlives the walk.
 *
 * THE CODE UNDER TEST is the GALE block of app.js, the MODSTATUS block, and the page's own
 * Hexium flow (hexiumConsentModal, doInstallFromHexium, hexiumFailToast, hexiumDepMark) and the
 * row's renderRowStatus, all evaluated with the page's globals stood in for. The sentences come
 * out of the real en.json. Mod names are made up.
 *
 * The rules (the letters are the spec's adversarial scenarios):
 *   1. The five Hexium states and a build the site took away each draw their own tag and
 *      tooltip; exact and other have no tick and their one button, the rest keep a tick (a, e).
 *   2. Tick all and the review's Install never touch a Hexium row; the walk never asks for one,
 *      counts a build Hexium has as waiting rather than missing, goes on past an unreachable
 *      index, and clears every row pill it wrote when it ends while the review keeps the row (b, j).
 *   3. A walk that stops on a realm switch sends nothing more: no add, no Hexium (h).
 *   4. Opening the review twice asks Hexium nothing (g).
 *   5. Install from Hexium asks mods.hexiumPrepare on every press and installs with the token
 *      of the latest answer, never an older one (c).
 *   6. With the server up the consent dialog carries the running warning; the install goes in,
 *      its row's walk pill goes, gale.rescan drops the row and the review comes back (d).
 *   7. Two rows on Hexium are two separate consent dialogs, one mod each, in row order (f).
 *   8. A row Hexium has in other versions only opens the page, and nothing installs (e).
 *   9. A Failed pill longer than 60 characters says the short words with the whole sentence in
 *      its tooltip, and the stylesheet keeps it inside its cell; the add's sentence for a package
 *      the site never had is its own (i).
 *  10. A status pill whose words change in a redraw fades them in through revealText, and one
 *      whose words did not change is left alone (the owner's no-jump rule).
 *  11. Every id the new code asks for is in the catalog, with no long dash and none of the banned words.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/gale_hexium_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const WEB = path.join(ROOT, "ValheimBakaLoader", "WebUI");
const APP = fs.readFileSync(path.join(WEB, "app.js"), "utf8").replace(/\r\n/g, "\n");
const CSS = fs.readFileSync(path.join(WEB, "app.css"), "utf8").replace(/\r\n/g, "\n");
const KEYS = JSON.parse(fs.readFileSync(path.join(WEB, "i18n", "en.json"), "utf8")).keys || {};

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

function between(from, to) {
  const a = APP.indexOf(from), b = APP.indexOf(to);
  assert.ok(a >= 0 && b > a, "app.js no longer carries " + from);
  return APP.slice(a, b);
}

/* One top-level function of app.js, by its head, to its closing brace at the start of a line. */
function fn(head) {
  const at = APP.indexOf("\n" + head);
  assert.ok(at >= 0, "app.js no longer has " + head);
  return APP.slice(at + 1, APP.indexOf("\n}\n", at) + 3);
}

function T(id, params) {
  const entry = KEYS[id];
  if (!entry) throw new Error("the catalog has no sentence for " + id);
  let lore = entry.lore;
  if (lore && typeof lore === "object") lore = params && Number(params.count) === 1 ? lore.one : lore.other;
  if (typeof lore !== "string") throw new Error("the catalog entry " + id + " has no usable lore");
  return lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

const esc = s => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
const FAIL = { failed: true };

async function settle() {
  for (let i = 0; i < 30; i++) await new Promise(r => setImmediate(r));
}

/* The dialog as the GALE block draws it: the html it was handed, its ticks read back out, its
   two footer buttons and the few parts the block writes into after it is drawn. */
function fakeModal(html) {
  const boxes = [];
  const re = /<input type="checkbox" data-gale-row="([^"]+)"( checked)?/g;
  let m;
  while ((m = re.exec(html)) !== null) {
    const row = m[1];
    boxes.push({ checked: !!m[2], getAttribute: () => row });
  }
  const handlers = {};
  const button = id => ({ disabled: new RegExp('id="' + id + '"[^>]*\\sdisabled').test(html), addEventListener: (_, f) => { handlers[id] = f; } });
  const buttons = { "#galeNotNow": button("galeNotNow"), "#galeInstall": button("galeInstall") };
  const text = { textContent: "", style: {}, innerHTML: "", querySelector: () => null, getClientRects: () => [] };
  return {
    html, boxes, handlers,
    classList: { add: () => {}, toggle: () => {} },
    addEventListener: () => {},
    querySelector: sel => buttons[sel] || Object.assign({}, text),
    querySelectorAll: sel => (sel === "[data-gale-row]" ? boxes : []),
  };
}

/* A click inside the dialog as its delegated listener sees it. */
function clickOn(attr, value) {
  const node = { disabled: false, getAttribute: a => (a === attr ? value : null) };
  return { target: { closest: sel => (sel === "[" + attr + "]" ? node : null) } };
}

function page(opts) {
  opts = opts || {};
  const sent = [], toasts = [], modals = [], consents = [], logs = [];
  const replies = opts.replies || {};
  const answer = (method, params) => { const r = replies[method]; return typeof r === "function" ? r(params) : r; };
  const context = {
    S: { profileName: "Final Sunset", state: { status: opts.status || "Stopped" }, modRowStatus: opts.rowStatus || {} },
    Native: {
      available: true,
      call: (method, params) => { sent.push({ method, params }); const r = answer(method, params); return r instanceof Error ? Promise.reject(r) : Promise.resolve(r); },
    },
    FAIL, T, esc, MOD_UPD: null, LOC: () => "en",
    $: () => null,
    agoAt: iso => "at " + iso,
    rpc: (method, params) => { sent.push({ method, params }); const r = answer(method, params); return Promise.resolve(r === undefined ? FAIL : r); },
    toast: line => toasts.push(line),
    logLine: (kind, line) => logs.push(kind + " " + line),
    setCondition: () => {}, clearCondition: () => {},
    modalOpen: html => { const m = fakeModal(html); modals.push(m); return m; },
    modalClose: () => {},
    modalIsOpen: () => false,
    confirmModal: (title, body, ok, onOk) => { consents.push({ title: title(), body: body(), ok: ok(), accept: () => onOk() }); },
    fmtBytes: n => n + " B",
    renderModUpdateProgress: () => {}, renderMods: () => {},
    scanMods: () => Promise.resolve(),
    hostSentence: () => null,
    BEP_ADD_IN_FLIGHT: false, bepInExProgressDone: () => {},
    setModEnabled: () => Promise.resolve(),
  };
  context.isActiveProfile = p => p == null || String(p).toLowerCase() === String(context.S.profileName).toLowerCase();
  vm.createContext(context);
  vm.runInContext(between("/* REVEAL-BEGIN", "/* REVEAL-END */"), context, { filename: "app.js#reveal" });
  vm.runInContext(between("/* GALE-BEGIN", "/* GALE-END */"), context, { filename: "app.js#gale" });
  vm.runInContext(between("/* MODSTATUS-BEGIN", "/* MODSTATUS-END */"), context, { filename: "app.js#modstatus" });
  for (const head of ["function hexiumFailToast(", "function hexiumDepMark(", "function hexiumConsentModal(",
    "async function doInstallFromHexium(", "function renderRowStatus("])
    vm.runInContext(fn(head), context, { filename: "app.js#" + head });
  const methods = () => sent.map(s => s.method).filter(m => m !== "gale.configs" && m !== "gale.get");
  /* The block's own lets (GALE, GALE_DLG_ST, ...) live in the script scope, not on the context. */
  const get = name => vm.runInContext(name, context);
  return { context, sent, toasts, modals, consents, logs, methods, get };
}

/* A followed realm whose last walk found these rows. Owner-Plain is an ordinary add; the rest
   were marked gone, each with what the walk learned. */
const ROWS = [
  { name: "Owner-Plain", kind: "add", profileVersion: "1.0.0", serverVersion: null, ticked: true },
  { name: "Owner-Exact", kind: "add", profileVersion: "1.2.3", serverVersion: null, ticked: true, notAvailable: true, hexium: "exact", goneKind: "noPackage" },
  { name: "Owner-Other", kind: "add", profileVersion: "2.0.0", serverVersion: null, ticked: true, notAvailable: true, hexium: "other", goneKind: "noPackage" },
  { name: "Owner-None", kind: "add", profileVersion: "3.0.0", serverVersion: null, ticked: false, notAvailable: true, hexium: "none", goneKind: "noPackage" },
  { name: "Owner-Unreach", kind: "add", profileVersion: "4.0.0", serverVersion: null, ticked: false, notAvailable: true, hexium: "unreachable", goneKind: "noPackage" },
  { name: "Owner-Off", kind: "add", profileVersion: "5.0.0", serverVersion: null, ticked: false, notAvailable: true, hexium: "off", goneKind: "noPackage" },
  { name: "Owner-Removed", kind: "update", profileVersion: "6.1.0", serverVersion: "6.0.0", ticked: false, notAvailable: true, hexium: "none", goneKind: "noVersion" },
];

function state(rows, over) {
  return Object.assign({
    profile: "Final Sunset", following: true, id: "ab12CD", feed: "gale", lastApproved: null, unticked: [], heldBy: null,
    check: { seq: 4, at: "2026-10-27T10:00:00Z", ok: true, statusId: null, updatedAt: "2026-10-26T09:00:00Z", due: true,
      addCount: 1, updateCount: 0, skipped: 0, onlyHereCount: 0, configChanges: null, rows: rows || ROWS },
  }, over);
}

/* The <tr> of one mod in the dialog's markup. */
function rowHtml(html, name) {
  const parts = html.split("<tr>");
  const row = parts.find(p => p.includes('<td class="mono">' + name + "<") || p.includes('<td class="mono">' + name + '<div'));
  assert.ok(row, "the dialog has no row for " + name);
  return row.slice(0, row.indexOf("</tr>"));
}

function openReview(p, st) {
  p.context.galeApply(st);
  p.context.galeDialog();
  return p.modals[p.modals.length - 1];
}

const prepared = (owner, name, version, token) => ({
  NeedsConsent: true, Owner: owner, Name: name, Version: version, Token: token, FileSize: 2048,
  Replacing: false, Dependencies: [], DependencyMarks: [], PageUrl: "https://hexium.gg/x",
});

(async function main() {
  await test("1. each Hexium state and a removed build draw their own tag, tooltip, tick and button (a, e)", () => {
    const p = page();
    const html = openReview(p, state()).html;

    const exact = rowHtml(html, "Owner-Exact");
    assert.ok(!exact.includes('type="checkbox"'), "a build Hexium has can be ticked into the review's Install");
    assert.ok(exact.includes('data-gale-hexium="Owner-Exact"') && exact.includes(">" + T("mods.gale.dialog.on_hexium.install") + "<"), "no Install from Hexium on the row");
    assert.ok(exact.includes('title="' + esc(T("mods.gale.dialog.on_hexium.title")) + '">' + T("mods.gale.dialog.on_hexium") + "<"), "the exact row is not tagged on Hexium with its tooltip");

    const other = rowHtml(html, "Owner-Other");
    assert.ok(!other.includes('type="checkbox"'), "a row Hexium has in other versions can be ticked");
    assert.ok(!other.includes("data-gale-hexium=") && other.includes('data-gale-hexium-page="Owner-Other"'), "the other row offers an install instead of the page");
    assert.ok(other.includes(">" + T("mods.gale.dialog.on_hexium.open") + "<"), "no Open on Hexium");
    assert.ok(other.includes(esc(T("mods.gale.dialog.on_hexium_other.title", { version: "2.0.0" }))) && other.includes(">" + T("mods.gale.dialog.on_hexium_other") + "<"),
      "the other row does not say the profile's version and that Hexium has others");

    const tips = { "Owner-None": "mods.gale.dialog.not_on_thunderstore.none.title", "Owner-Unreach": "mods.gale.dialog.not_on_thunderstore.unreachable.title",
      "Owner-Off": "mods.gale.dialog.not_on_thunderstore.off.title" };
    for (const [name, tip] of Object.entries(tips)) {
      const row = rowHtml(html, name);
      assert.ok(row.includes('title="' + esc(T(tip)) + '">' + T("mods.gale.dialog.not_on_thunderstore") + "<"), name + " is not tagged not on Thunderstore with " + tip);
      assert.ok(/<input type="checkbox" data-gale-row="[^"]+" aria-label/.test(row), name + " lost its tick, so ticking it to try again is gone");
      assert.ok(!row.includes("data-gale-hexium"), name + " has a Hexium button");
      assert.ok(!row.includes(">" + T("mods.gale.dialog.not_available") + "<"), name + " still says removed for a package the site never had");
    }
    assert.ok(T("mods.gale.dialog.not_on_thunderstore.off.title").includes("Also check Hexium in Upkeep"), "the switched-off row does not say where the switch is");

    const removed = rowHtml(html, "Owner-Removed");
    assert.ok(removed.includes('title="' + esc(T("mods.gale.dialog.not_available.title")) + '">' + T("mods.gale.dialog.not_available") + "<"), "a build the site took away lost its own words");

    /* A mark made before 1.3.1 carries neither field and reads as it did. */
    const old = page();
    const oldHtml = openReview(old, state([{ name: "Owner-Old", kind: "add", profileVersion: "1.0.0", serverVersion: null, ticked: false, notAvailable: true }])).html;
    assert.ok(rowHtml(oldHtml, "Owner-Old").includes(">" + T("mods.gale.dialog.not_available") + "<"), "a 1.3.0 mark changed its words");
    const rows = old.context.galeDialogRows(state([{ name: "Owner-Old", kind: "add", profileVersion: "1.0.0", notAvailable: true }])).rows;
    assert.ok(!rows[0].held && rows[0].hexium === null, "a 1.3.0 mark is held back from the review's tick");
  });

  await test("2. the review's Install never touches a Hexium row; the walk counts it apart, goes on past an unreachable index and clears its row pills (b, j)", async () => {
    const after = state(ROWS.filter(r => r.name !== "Owner-Plain"));
    const p = page({ replies: {
      "gale.untick": { ok: true, state: state() },
      "mods.addFromUrl": params => {
        const name = /\/p\/([^/]+)\/([^/]+)\/v\//.exec(params.url).slice(1).join("-");
        if (name === "Owner-Plain") return { Installed: true, Version: "1.0.0" };
        if (name === "Owner-Off") return { Installed: false, Error: "the zip was short" };
        const hexium = name === "Owner-Removed" ? "exact" : name === "Owner-Unreach" ? "unreachable" : name === "Owner-None" ? "none" : null;
        return { Installed: false, Reason: "notAvailable", Error: "raw", ErrorParams: { mod: name, version: "x", kind: "noPackage" }, hexium };
      },
      "gale.rescan": { ok: true, state: after },
    } });
    const m = openReview(p, state());
    p.context.galeTickAll("mods", true);
    const plan = p.context.galePlan(p.get("GALE_DLG_ST"));
    const ticked = plan.ticked.map(r => r.name);
    assert.ok(!ticked.includes("Owner-Exact") && !ticked.includes("Owner-Other"), "Tick all ticked a Hexium row: " + ticked);
    assert.ok(ticked.includes("Owner-None") && ticked.includes("Owner-Off") && ticked.length === 5, "Tick all missed a tickable row: " + ticked);
    assert.ok(!plan.unticked.some(r => r.notAvailable), "a gone row was written to the unticks");
    m.handlers.galeInstall();
    await settle();

    const adds = p.sent.filter(s => s.method === "mods.addFromUrl").map(s => s.params.url);
    assert.strictEqual(adds.length, 5, "the walk did not try the five tickable rows: " + adds.join(" "));
    assert.ok(!adds.some(u => u.includes("/Exact/") || u.includes("/Other/")), "the walk installed a Hexium row through Thunderstore");
    assert.ok(!p.sent.some(s => /hexium/i.test(s.method)), "the walk asked Hexium for something: " + p.methods());
    assert.ok(p.toasts.includes("ᚦ " + T("mods.gale.install.partial.missing.toast", { done: 1, missing: 2, left: 1 })),
      "the summary counts a build Hexium has as missing, or stopped at the unreachable one: " + p.toasts.join(" | "));
    assert.ok(p.toasts.includes("ᚺ " + T("mods.gale.install.hexium.toast", { count: 1 })), "nothing says one build waits on Hexium: " + p.toasts.join(" | "));
    const left = Object.entries(p.context.S.modRowStatus).filter(([, s]) => s && s.phase === "failed");
    assert.deepStrictEqual(left, [], "the walk left Failed pills behind: " + JSON.stringify(left));
    assert.strictEqual(Object.keys(p.context.S.modRowStatus).length, 0, "the walk left row pills behind");
    assert.ok(p.context.galeDialogRows(p.get("GALE")).rows.some(r => r.name === "Owner-Off"), "the failed row is gone from the review");

    /* A walk whose only ticked row turned out to be on Hexium is answered, and says only that. */
    const q = page({ replies: {
      "gale.untick": { ok: true, state: state() },
      "mods.addFromUrl": { Installed: false, Reason: "notAvailable", ErrorParams: { mod: "Owner-None", version: "3.0.0", kind: "noPackage" }, hexium: "exact" },
      "gale.dismiss": { ok: true, state: state() },
    } });
    const qm = openReview(q, state());
    q.context.galeDialogChange({ target: { getAttribute: a => (a === "data-gale-row" ? "Owner-None" : null), checked: true } });
    q.context.galeDialogChange({ target: { getAttribute: a => (a === "data-gale-row" ? "Owner-Plain" : null), checked: false } });
    qm.handlers.galeInstall();
    await settle();
    assert.ok(q.methods().includes("gale.dismiss"), "a walk with nothing failed was not answered: " + q.methods());
    assert.ok(!q.toasts.some(t => t.includes(T("mods.gale.install.done.toast", { count: 0 }))), "it says nothing was installed as if that were the news");
    assert.deepStrictEqual(q.toasts.filter(t => t.startsWith("ᚺ")), ["ᚺ " + T("mods.gale.install.hexium.toast", { count: 1 })]);
  });

  await test("3. a walk that stops on a realm switch sends nothing more for the old realm (h)", async () => {
    const p = page({ replies: {
      "gale.untick": { ok: true, state: state() },
      "mods.addFromUrl": { Installed: false, Reason: "realmChanged", Error: "changed" },
      "gale.rescan": { ok: true, state: state() },
    } });
    const m = openReview(p, state());
    p.context.galeTickAll("mods", true);
    m.handlers.galeInstall();
    await settle();
    assert.strictEqual(p.sent.filter(s => s.method === "mods.addFromUrl").length, 1, "the walk went on after the realm changed: " + p.methods());
    assert.ok(!p.sent.some(s => /hexium/i.test(s.method)), "Hexium was asked after the realm changed");
    assert.strictEqual(Object.keys(p.context.S.modRowStatus).length, 0, "the stopped walk left its row pill");
  });

  await test("4. opening the review twice asks Hexium nothing (g)", () => {
    const p = page();
    openReview(p, state());
    openReview(p, state());
    assert.deepStrictEqual(p.methods(), [], "opening the review sent " + p.methods());
    assert.ok(p.modals.length === 2 && p.modals[1].html.includes('data-gale-hexium="Owner-Exact"'), "the second review did not draw the Hexium row from what it read");
  });

  await test("5. Install from Hexium asks again on every press and installs with the latest token (c)", async () => {
    let n = 0;
    const p = page({ replies: {
      "mods.hexiumPrepare": params => prepared(params.owner, params.name, params.version, "token-" + (++n)),
      "mods.installFromHexium": params => ({ Installed: true, Owner: params.owner, Name: params.name, Version: params.version, Dependencies: [] }),
      "gale.rescan": { ok: true, state: state() },
    } });
    openReview(p, state());
    p.context.galeDialogClick(clickOn("data-gale-hexium", "Owner-Exact"));
    await settle();
    /* The host walked away; the question went stale. The review is opened and pressed again. */
    openReview(p, state());
    p.context.galeDialogClick(clickOn("data-gale-hexium", "Owner-Exact"));
    await settle();
    const asks = p.sent.filter(s => s.method === "mods.hexiumPrepare");
    assert.strictEqual(asks.length, 2, "a press used a kept answer instead of asking again");
    assert.deepStrictEqual(JSON.parse(JSON.stringify(asks[1].params)), { owner: "Owner", name: "Exact", version: "1.2.3" });
    assert.strictEqual(p.consents.length, 2, "the second press did not open its own dialog");
    p.consents[1].accept();
    await settle();
    const install = p.sent.filter(s => s.method === "mods.installFromHexium");
    assert.strictEqual(install.length, 1);
    assert.strictEqual(install[0].params.token, "token-2", "the install used a token from an earlier question");
    /* A row that is not exact never reaches prepare, however the click arrives. */
    p.context.galeDialogClick(clickOn("data-gale-hexium", "Owner-Other"));
    p.context.galeDialogClick(clickOn("data-gale-hexium", "Owner-None"));
    await settle();
    assert.strictEqual(p.sent.filter(s => s.method === "mods.hexiumPrepare").length, 2, "a row Hexium does not have the build for was prepared");
  });

  await test("6. with the server up the consent dialog warns; the install goes in, its pill goes, the rescan drops the row and the review comes back (d)", async () => {
    const same = state(ROWS.map(r => (r.name === "Owner-Exact" ? { name: "Owner-Exact", kind: "same", profileVersion: "1.2.3", serverVersion: "1.2.3" } : r)));
    const p = page({ status: "Running", rowStatus: { "Owner-Exact": { phase: "failed", error: "no package" }, "Owner-Plain": { phase: "done" } },
      replies: {
        "mods.hexiumPrepare": params => prepared(params.owner, params.name, params.version, "t"),
        "mods.installFromHexium": params => ({ Installed: true, Owner: "owner", Name: "exact", Version: params.version, Dependencies: [] }),
        "gale.rescan": { ok: true, state: same },
      } });
    p.context.S.state.status = "Running";
    openReview(p, state());
    p.context.galeDialogClick(clickOn("data-gale-hexium", "Owner-Exact"));
    await settle();
    assert.strictEqual(p.consents.length, 1);
    assert.ok(p.consents[0].body.includes(esc(T("mods.hexium.consent.running"))), "the consent dialog does not say the server is running");
    p.consents[0].accept();
    await settle();
    assert.ok(p.methods().join(",").startsWith("mods.hexiumPrepare,mods.installFromHexium,gale.rescan"), "the order is " + p.methods());
    assert.ok(!("Owner-Exact" in p.context.S.modRowStatus), "the walk's Failed pill outlived the install from Hexium");
    assert.ok("Owner-Plain" in p.context.S.modRowStatus, "another row's pill was taken");
    const rowsNow = p.context.galeDialogRows(p.get("GALE")).rows;
    assert.ok(rowsNow.length > 0 && !rowsNow.some(r => r.name === "Owner-Exact"), "the installed row is still offered");
    const back = p.modals[p.modals.length - 1].html;
    assert.strictEqual(p.modals.length, 2, "the review did not come back");
    assert.ok(!back.includes('data-gale-hexium="Owner-Exact"'), "the review came back with the installed row");
  });

  await test("7. two rows on Hexium are two consent dialogs, one mod each, in row order (f)", async () => {
    const two = ROWS.concat([{ name: "Owner-Exact2", kind: "add", profileVersion: "7.0.0", serverVersion: null, ticked: false, notAvailable: true, hexium: "exact", goneKind: "noPackage" }]);
    const afterFirst = state(two.filter(r => r.name !== "Owner-Exact"));
    const p = page({ replies: {
      "mods.hexiumPrepare": params => prepared(params.owner, params.name, params.version, "t-" + params.name),
      "mods.installFromHexium": params => ({ Installed: true, Owner: params.owner, Name: params.name, Version: params.version, Dependencies: [] }),
      "gale.rescan": { ok: true, state: afterFirst },
    } });
    const html = openReview(p, state(two)).html;
    assert.ok(html.indexOf('data-gale-hexium="Owner-Exact"') < html.indexOf('data-gale-hexium="Owner-Exact2"'), "the buttons are not in row order");
    p.context.galeDialogClick(clickOn("data-gale-hexium", "Owner-Exact"));
    await settle();
    assert.strictEqual(p.consents.length, 1, "one press opened more than one question");
    p.consents[0].accept();
    await settle();
    p.context.galeDialogClick(clickOn("data-gale-hexium", "Owner-Exact2"));
    await settle();
    assert.strictEqual(p.consents.length, 2);
    assert.ok(p.consents[0].body.includes("Owner / Exact<br>") && !p.consents[0].body.includes("Exact2"), "the first question is not about the first row alone");
    assert.ok(p.consents[1].body.includes("Owner / Exact2<br>"), "the second question is not about the second row");
    assert.deepStrictEqual(p.sent.filter(s => s.method === "mods.installFromHexium").map(s => s.params.name), ["Exact"], "the second mod went in without its own yes");
  });

  await test("8. a row Hexium has in other versions only opens the page (e)", async () => {
    const p = page({ replies: { "shell.openHexium": {} } });
    openReview(p, state());
    p.context.galeDialogClick(clickOn("data-gale-hexium-page", "Owner-Other"));
    await settle();
    assert.deepStrictEqual(p.sent.filter(s => s.method !== "gale.configs" && s.method !== "gale.get").map(s => [s.method, JSON.parse(JSON.stringify(s.params))]),
      [["shell.openHexium", { owner: "Owner", name: "Other" }]], "the page link did something else: " + p.methods());
    assert.strictEqual(p.consents.length, 0, "the page link asked to install");
    p.context.galeTickAll("mods", true);
    const plan = p.context.galePlan(p.get("GALE_DLG_ST"));
    assert.ok(plan.ticked.length === 5 && !plan.ticked.some(r => r.name === "Owner-Other"), "the other row is in the install plan");
  });

  await test("9. a long Failed pill says the short words with the sentence in its tooltip, and keeps to its cell (i)", () => {
    const p = page();
    const long = "Owner-Package 1.2.3: Thunderstore has no package by this name; Gale on the players' machines may still carry it, and then some more words to reach two hundred.";
    const sentence = (long + " " + long).slice(0, 200);
    const html = p.context.renderRowStatus({ phase: "failed", error: sentence });
    assert.ok(html.includes(">" + esc(T("mods.row.failed.short")) + "</span>"), "a 200 character failure is printed whole in the pill: " + html.slice(0, 120));
    assert.ok(html.includes('title="' + esc(sentence) + '"'), "the whole sentence is not in the tooltip");
    assert.ok(/class="pill rowfail"/.test(html), "the pill lost the class the stylesheet keeps it in its cell by");
    const short = "zip short";
    assert.ok(p.context.renderRowStatus({ phase: "failed", error: short }).includes(">" + esc(T("mods.row.failed", { detail: short })) + "<"), "a short failure lost its words");
    const sixty = "x".repeat(60 - T("mods.row.failed", { detail: "" }).length);
    assert.ok(p.context.renderRowStatus({ phase: "failed", error: sixty }).includes(">" + T("mods.row.failed", { detail: sixty }) + "<"), "60 characters is shortened");
    assert.ok(p.context.renderRowStatus({ phase: "failed", error: sixty + "y" }).includes(">" + esc(T("mods.row.failed.short")) + "<"), "61 characters is not shortened");
    assert.ok(p.context.renderRowStatus({ phase: "failed" }).includes(">" + T("mods.row.failed.nodetail") + "<"), "a failure with nothing to say changed");
    assert.ok(/#modTable td\.modstat \.pill\.rowfail\{[^}]*max-width:\d+px;overflow:hidden;text-overflow:ellipsis/.test(CSS), "the stylesheet's net under the pill is gone");
    assert.ok(fn("function modRowHtml(").includes('<td class="modstat">${statusCell}</td>'), "the status cell lost the class the net finds it by");
    /* The add's sentence for a package the site never had is its own. */
    const gone = { Reason: "notAvailable", ErrorParams: { mod: "Owner-Package", version: "1.2.3", kind: "noPackage" } };
    assert.strictEqual(p.context.modNotAvailableText(gone), T("mods.add.not_on_thunderstore.toast", { mod: "Owner-Package", version: "1.2.3" }));
    gone.ErrorParams.kind = "noVersion";
    assert.strictEqual(p.context.modNotAvailableText(gone), T("mods.add.not_available.toast", { mod: "Owner-Package", version: "1.2.3" }));
  });

  await test("10. a status pill whose words change fades them in through revealText; one that did not is left alone", () => {
    const p = page();
    const animated = [];
    const pill = words => ({ textContent: words, getClientRects: () => [1], animate(frames) { animated.push(this.textContent); return {}; } });
    const row = (key, words) => {
      const pills = [pill(words)];
      const cell = { get textContent() { return pills.map(x => x.textContent).join(""); }, querySelectorAll: () => pills };
      return { getAttribute: () => key, querySelector: sel => (sel === "td.modstat" ? cell : null) };
    };
    const table = rows => ({ querySelectorAll: sel => (sel === "tr[data-key]" ? rows : []) });
    const was = p.context.modStatusWords(table([row("Owner-A", "Failed, hover for why"), row("Owner-B", "current"), row("Owner-C", "Updating")]));
    assert.strictEqual(was.get("Owner-A"), "Failed, hover for why");
    p.context.modStatusFade(table([row("Owner-A", "current"), row("Owner-B", "current"), row("Owner-D", "current"), row("Owner-C", "✓ 1 → 2")]), was);
    assert.deepStrictEqual(animated, ["current", "✓ 1 → 2"], "the wrong pills faded: " + animated);
    const render = fn("function renderMods(");
    assert.ok(render.indexOf("const statusWas=modStatusWords($(\"#modTable\"));") < render.indexOf("$(\"#modTable\").innerHTML=shown.map")
      && render.indexOf("$(\"#modTable\").innerHTML=shown.map") < render.indexOf("modStatusFade($(\"#modTable\"),statusWas);"),
      "renderMods no longer reads the words before the redraw and fades them after");
  });

  await test("11. every id the new code asks for is in the catalog, with no long dash and none of the banned words", () => {
    const code = [fn("function galeGoneTagHtml("), fn("function galeHexiumBtnHtml("), fn("async function galeHexiumInstall("),
      fn("function galeHexiumPage("), fn("function renderRowStatus("), fn("function modNotAvailableText("), fn("async function galeInstall(")].join("\n");
    const ids = [...code.matchAll(/T\("([^"]+)"/g)].map(m => m[1]);
    assert.ok(ids.length >= 15, "the new code asks for fewer sentences than it should: " + ids.length);
    for (const id of ids) {
      const entry = KEYS[id];
      assert.ok(entry, "the catalog has no " + id);
      const text = JSON.stringify(entry.lore);
      assert.ok(!/[\u2013\u2014]/.test(text), id + " carries a long dash");
      assert.ok(!/\b\u0041I\b/.test(text), id + " says a banned word");
    }
  });

  console.log(failures.length
    ? "gale hexium selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAILED"
    : "gale hexium selftest: all " + passed + " rules pass");
  process.exit(failures.length ? 1 : 0);
})();
