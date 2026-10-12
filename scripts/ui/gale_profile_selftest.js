/* Following a Gale profile: the Mods hall box, the condition bar row and the approval dialog,
 * driven for real.
 *
 * WHY THIS EXISTS. The feature installs mods on a server because a profile somebody else
 * edits has moved. Every way it can go wrong is quiet and looks right in a diff: a row that
 * outranks a failure happening this second, a row raised by a check that never reached Gale,
 * a tick that comes back on after the host took it off, an Install offered while the server
 * has the plugins loaded, a refused code sent to the service anyway, a walk that stamps the
 * profile as approved after half its mods failed. So the block is evaluated here and pushed
 * through each of those.
 *
 * THE CODE UNDER TEST is the block between the GALE markers in app.js, evaluated with the
 * page's own globals stood in for. The sentences come out of the real en.json.
 *
 * The rules:
 *   1. The row is a kind the bar draws, and it ranks below every live failure, the unsaved
 *      stop and the stray folder, and above the two plain lists.
 *   2. The row is raised only by a check that came back whole with work to do and a moved
 *      profile; a failed check, a check with nothing to do, a dismissed profile and no follow
 *      all leave it down, and a failed check takes an earlier one down.
 *   3. Dismiss tells the host side with the check's seq, Review opens the dialog.
 *   4. The status line names the last check and the last approval, and a failure is one
 *      sentence on it, with no toast.
 *   5. Check now: a refused code says its sentence and sends nothing to the service; a new
 *      code is stored first and then checked; the same code is only checked; the preview
 *      says so and sends nothing.
 *   6. The dialog: ticks on by default, off for names the host unticked before, one note line
 *      for mods the profile switched off, "not installed" for an add, and with the server up
 *      the stop sentence and no Install.
 *   7. Install: unticks written first, then one mod at a time through the Mods hall's add with
 *      the version pinned, the realm named and a stopped server required; a clean walk stores
 *      the approval, a failed one does not and has the counts made again against the disk
 *      (so the mods that did go in are not offered twice), and a server that came up stops
 *      the walk.
 *   8. The wiring: the box is in the Mods hall, the hall's walk-in, the first frame and Check now
 *      are the only callers of the check, a realm switch takes the row down, and the host side
 *      has the seven RPCs and holds the add to the realm and a stopped server.
 *   9. Every id the block asks for is in the catalog, with no long dash and no "AI".
 *  10. No BepInEx on the server: with the pack in the profile the dialog says it goes in first
 *      and Install puts it in through the host side before the first mod; without the pack
 *      Install is off with the sentence to put BepInEx in first.
 *  11. A pinned build Thunderstore no longer has: the add's sentence names the mod and the
 *      version, the walk goes on, the summary counts the ones that landed and the ones that
 *      were not available, and the next review marks the row instead of offering a plain add.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/gale_profile_selftest.js
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
const FORMS = path.join(ROOT, "ValheimBakaLoader", "Forms");
const BRIDGE = fs.readFileSync(path.join(FORMS, "BlendWindow.Bridge.cs"), "utf8");
const GALE_CS = fs.readFileSync(path.join(FORMS, "BlendWindow.Gale.cs"), "utf8");

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

function block() {
  const from = APP.indexOf("/* GALE-BEGIN");
  const to = APP.indexOf("/* GALE-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the GALE markers");
  return APP.slice(from, to);
}

/* The catalog's own sentence with its slots filled, so a missing id fails loudly here. */
function T(id, params) {
  const entry = KEYS[id];
  if (!entry) throw new Error("the catalog has no sentence for " + id);
  let lore = entry.lore;
  if (lore && typeof lore === "object") lore = params && Number(params.count) === 1 ? lore.one : lore.other;
  if (typeof lore !== "string") throw new Error("the catalog entry " + id + " has no usable lore");
  return lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

const FAIL = { failed: true };

function el() {
  return { value: "", textContent: "", disabled: false, title: "", style: {} };
}

/* A dialog stand in: it keeps the html it was handed, reads the ticks back out of it, and
   lets a test press its two buttons. */
function fakeModal(html) {
  const boxes = [];
  const re = /<input type="checkbox" data-gale-row="(\d+)"( checked)?/g;
  let m;
  while ((m = re.exec(html)) !== null) {
    const row = m[1];
    boxes.push({ checked: !!m[2], getAttribute: () => row });
  }
  const handlers = {};
  const button = id => ({
    disabled: new RegExp('id="' + id + '"[^>]*\\sdisabled').test(html),
    addEventListener: (_, fn) => { handlers[id] = fn; },
  });
  const buttons = { "#galeNotNow": button("galeNotNow"), "#galeInstall": button("galeInstall") };
  return {
    html, boxes, handlers, buttons,
    querySelector: sel => buttons[sel] || null,
    querySelectorAll: sel => (sel === "[data-gale-row]" ? boxes : []),
  };
}

/* A fresh page: the real block and a record of everything it sends. */
function page(opts) {
  opts = opts || {};
  const sent = [];
  const toasts = [];
  const conditions = new Map();
  const modals = [];
  const els = { "#galeId": el(), "#galeStatus": el(), "#galeCheckBtn": el(), "#galeForgetBtn": el(), "#galeReviewBtn": el() };
  const replies = opts.replies || {};
  const answer = (method, params) => {
    const r = replies[method];
    return typeof r === "function" ? r(params) : r;
  };
  const context = {
    S: { profileName: "Final Sunset", state: { status: opts.status || "Stopped" }, modRowStatus: {} },
    Native: {
      available: opts.native !== false,
      call: (method, params) => { sent.push({ method, params }); const r = answer(method, params); return r instanceof Error ? Promise.reject(r) : Promise.resolve(r); },
    },
    FAIL,
    T,
    MOD_UPD: null,
    LOC: () => "en",
    $: sel => els[sel] || null,
    esc: s => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;"),
    agoAt: iso => "at " + iso,
    rpc: (method, params) => { sent.push({ method, params }); const r = answer(method, params); return Promise.resolve(r === undefined ? FAIL : r); },
    toast: line => toasts.push(line),
    setCondition: (kind, cond) => { if (cond) conditions.set(kind, cond); else conditions.delete(kind); },
    clearCondition: kind => conditions.delete(kind),
    modalOpen: html => { const m = fakeModal(html); modals.push(m); return m; },
    modalClose: () => {},
    renderModUpdateProgress: () => {},
    renderMods: () => {},
    scanMods: () => Promise.resolve(),
    hostSentence: () => null,
    BEP_ADD_IN_FLIGHT: false,
    bepInExProgressDone: () => {},
  };
  context.isActiveProfile = p => p == null || !context.S.profileName
    || String(p).toLowerCase() === String(context.S.profileName).toLowerCase();
  vm.createContext(context);
  vm.runInContext(block(), context, { filename: "app.js#gale" });
  for (const name of ["galeApply", "renderGaleBox", "conditionGale", "galeCheck", "galeCheckNow", "galeForget",
    "galeDialog", "galeDialogRows", "galeInstall", "galeStatusLine"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  return { context, sent, toasts, conditions, modals, els, methods: () => sent.map(s => s.method) };
}

/* A followed realm with a check that found one add, one update and one switched off mod. */
function state(over) {
  const check = Object.assign({
    seq: 7, at: "2026-10-12T10:00:00Z", ok: true, statusId: null, updatedAt: "2026-10-11T09:00:00.123456+00:00",
    due: true, addCount: 1, updateCount: 1, skipped: 0,
    rows: [
      { name: "Azumatt-AzuCraftyBoxes", kind: "add", untickedFrom: null, profileVersion: "1.8.4", serverVersion: null, downgrade: false },
      { name: "ValheimModding-Jotunn", kind: "update", untickedFrom: null, profileVersion: "2.21.0", serverVersion: "2.20.1", downgrade: false },
      { name: "RandyKnapp-EpicLoot", kind: "disabled", untickedFrom: null, profileVersion: "0.11.0", serverVersion: null, downgrade: false },
      { name: "shudnal-ExtraSlots", kind: "unticked", untickedFrom: "add", profileVersion: "1.0.0", serverVersion: null, downgrade: false },
      { name: "Someone-OnlyHere", kind: "notInProfile", untickedFrom: null, profileVersion: null, serverVersion: "3.0.0", downgrade: false },
    ],
  }, over && over.check);
  return Object.assign({ profile: "Final Sunset", following: true, id: "ab12CD", lastApproved: null, unticked: ["shudnal-ExtraSlots"], heldBy: null, check }, over, { check: over && over.check === null ? null : check });
}

function order() {
  const m = /const\s+CONDITION_ORDER\s*=\s*\[([\s\S]*?)\]/.exec(APP);
  assert.ok(m, "CONDITION_ORDER is gone, so nothing says which row wins");
  return m[1].split(",").map(s => s.trim().replace(/"/g, "")).filter(Boolean);
}

function fnBody(source, head) {
  const start = source.indexOf(head);
  assert.notStrictEqual(start, -1, head + " is gone");
  return source.slice(start, source.indexOf("\n}", start));
}

(async function main() {
  await test("1. the row is drawn, under every live failure, the unsaved stop and the stray folder, over the lists", () => {
    const names = order();
    const at = names.indexOf("galeChanged");
    assert.ok(at >= 0, "CONDITION_ORDER does not name galeChanged, so the row is raised and never drawn");
    for (const k of ["launchHold", "saveFailed", "backupFailed", "crashRelaunch", "bepinexNoMods", "bepinexNotLoaded",
      "bepinexMissingFiles", "serverUpdate", "pluginFailure", "bepinexNotice", "appUpdate", "unsavedStop", "saveFolderStray"]) {
      assert.ok(names.indexOf(k) >= 0, k + " left CONDITION_ORDER; this list needs looking at");
      assert.ok(names.indexOf(k) < at, "galeChanged outranks " + k);
    }
    for (const k of ["modUpdates", "restartPending"]) assert.ok(names.indexOf(k) > at, k + " outranks galeChanged");
  });

  await test("2. raised only by a whole check with work and a moved profile", () => {
    const p = page();
    p.context.galeApply(state());
    const row = p.conditions.get("galeChanged");
    assert.ok(row, "a due check raised no row");
    assert.strictEqual(row.title, T("cond.gale.title"));
    assert.strictEqual(row.msg, T("cond.gale.msg", { add: 1, update: 1 }));
    assert.ok(row.msg.startsWith("1 to add, 1 to update"), "the row does not lead with the two counts: " + row.msg);

    for (const [why, st] of [
      ["a dismissed profile", state({ check: { due: false } })],
      ["a failed check", state({ check: { ok: false, due: false, statusId: "mods.gale.check.timeout", addCount: 0, updateCount: 0, rows: [] } })],
      ["no follow", state({ following: false, id: null, check: null })],
    ]) {
      const q = page();
      q.context.galeApply(st);
      assert.ok(!q.conditions.has("galeChanged"), why + " raised the row");
      /* And takes down one an earlier check raised. */
      const r = page();
      r.context.galeApply(state());
      r.context.galeApply(st);
      assert.ok(!r.conditions.has("galeChanged"), why + " left an earlier row standing");
    }

    /* An answer about a realm the host has left changes nothing. */
    const s = page();
    assert.strictEqual(s.context.galeApply(state({ profile: "Old Realm" })), false);
    assert.ok(!s.conditions.has("galeChanged"), "another realm's check raised this realm's row");
  });

  await test("3. Dismiss sends the seq, Review opens the dialog", async () => {
    const p = page({ replies: { "gale.dismiss": { ok: true, state: state({ check: { due: false } }) } } });
    p.context.galeApply(state());
    const row = p.conditions.get("galeChanged");
    row.onDismiss();
    await new Promise(r => setImmediate(r));
    const d = p.sent.find(s => s.method === "gale.dismiss");
    assert.ok(d, "Dismiss never told the host side");
    assert.deepStrictEqual(JSON.parse(JSON.stringify(d.params)), { profile: "Final Sunset", seq: 7 });
    assert.ok(!p.conditions.has("galeChanged"), "the answer to a dismissal left the row up");

    const q = page();
    q.context.galeApply(state());
    const bar = { querySelector: () => ({ addEventListener: (_, fn) => fn() }) };
    q.conditions.get("galeChanged").wire(bar);
    assert.strictEqual(q.modals.length, 1, "Review did not open the dialog");
    assert.ok(q.modals[0].html.includes(T("mods.gale.dialog.title")));
  });

  await test("4. the status line names the check and the approval, and a failure is one quiet line", () => {
    const p = page();
    const line = st => p.context.galeStatusLine(st, false, null);
    assert.strictEqual(line(null), T("mods.gale.status.off"));
    assert.strictEqual(p.context.galeStatusLine(state(), true, null), T("mods.gale.status.checking"));
    assert.strictEqual(line(state({ check: null })), T("mods.gale.status.never") + " " + T("mods.gale.status.approved.never"));
    const due = line(state());
    assert.ok(due.startsWith(T("mods.gale.status.due", { at: "at 2026-10-12T10:00:00Z", add: 1, update: 1 })), due);
    const failed = line(state({ check: { ok: false, statusId: "mods.gale.check.network", due: false } }));
    assert.ok(failed.startsWith(T("mods.gale.check.network")), "the failure is not said: " + failed);
    const approved = line(state({ lastApproved: "2026-10-01T00:00:00Z", check: { due: false } }));
    assert.ok(approved.includes(T("mods.gale.status.waved", { at: "at 2026-10-12T10:00:00Z", add: 1, update: 1 })), approved);
    assert.ok(/Last approved/.test(approved), "the last approval is not named: " + approved);
    const same = line(state({ check: { addCount: 0, updateCount: 0, due: false } }));
    assert.ok(same.startsWith(T("mods.gale.status.current", { at: "at 2026-10-12T10:00:00Z" })), same);
    /* An id the table does not know is never printed as an id. */
    const odd = line(state({ check: { ok: false, statusId: "mods.gale.check.who_knows" } }));
    assert.ok(!odd.includes("who_knows"), "an unknown id was printed: " + odd);
    assert.ok(odd.startsWith(T("mods.gale.check.network")), odd);
    assert.strictEqual(p.toasts.length, 0, "a status line toasted");
  });

  await test("4b. a failed check from the host side toasts nothing and raises nothing", async () => {
    const failed = state({ check: { ok: false, statusId: "mods.gale.check.timeout", due: false, addCount: 0, updateCount: 0, rows: [] } });
    const p = page({ replies: { "gale.check": failed } });
    await p.context.galeCheck();
    assert.strictEqual(p.toasts.length, 0, "a failed check toasted: " + p.toasts.join(" | "));
    assert.ok(!p.conditions.has("galeChanged"));
    assert.ok(p.els["#galeStatus"].textContent.startsWith(T("mods.gale.check.timeout")), p.els["#galeStatus"].textContent);
    /* A bridge that threw is the same quiet nothing. */
    const q = page({ replies: { "gale.check": new Error("boom") } });
    await q.context.galeCheck();
    assert.strictEqual(q.toasts.length, 0);
  });

  await test("5. Check now: refused code, new code, same code, preview", async () => {
    const refused = page({ replies: { "gale.set": { ok: false, sentenceId: "mods.gale.id.bad", state: state({ following: false, id: null, check: null }) } } });
    refused.els["#galeId"].value = "not a code!";
    await refused.context.galeCheckNow();
    assert.deepStrictEqual(refused.methods(), ["gale.set"], "a refused code went further: " + refused.methods());
    assert.strictEqual(refused.els["#galeStatus"].textContent, T("mods.gale.id.bad"));

    const fresh = page({ replies: { "gale.set": { ok: true, state: state({ check: null }) }, "gale.check": state() } });
    fresh.els["#galeId"].value = "  ab12CD ";
    await fresh.context.galeCheckNow();
    assert.deepStrictEqual(fresh.methods(), ["gale.set", "gale.check"]);
    assert.strictEqual(fresh.sent[0].params.id, "ab12CD", "the code was not trimmed");

    const same = page({ replies: { "gale.check": state() } });
    same.context.galeApply(state({ check: null }));
    same.els["#galeId"].value = "ab12CD";
    await same.context.galeCheckNow();
    assert.deepStrictEqual(same.methods(), ["gale.check"], "an unchanged code was stored again");

    const cleared = page({ replies: { "gale.set": { ok: true, state: state({ following: false, id: null, check: null }) } } });
    cleared.context.galeApply(state());
    cleared.els["#galeId"].value = "";
    await cleared.context.galeCheckNow();
    assert.deepStrictEqual(cleared.methods(), ["gale.set"], "a cleared box asked Gale: " + cleared.methods());

    const preview = page({ native: false });
    await preview.context.galeCheckNow();
    assert.strictEqual(preview.sent.length, 0);
    assert.deepStrictEqual(preview.toasts, ["ᛋ " + T("mods.gale.preview.toast")]);
    assert.strictEqual(KEYS["mods.gale.preview.toast"].mark, "ᛋ", "the toast's rune is not on its catalog entry");
  });

  await test("6. the dialog: ticks, remembered unticks, the disabled note, not installed, server up", () => {
    const p = page();
    p.context.galeApply(state());
    p.context.galeDialog();
    const m = p.modals[0];
    assert.strictEqual(m.boxes.length, 3, "the dialog lists " + m.boxes.length + " rows, not the add, the update and the unticked add");
    const rows = p.context.galeDialogRows(state()).rows;
    const ticked = Object.fromEntries(m.boxes.map(b => [rows[Number(b.getAttribute())].name, b.checked]));
    assert.deepStrictEqual(ticked, { "Azumatt-AzuCraftyBoxes": true, "ValheimModding-Jotunn": true, "shudnal-ExtraSlots": false },
      "ticks are not on by default and off for a remembered untick");
    assert.ok(m.html.includes(p.context.esc(T("mods.gale.dialog.disabled", { names: "RandyKnapp-EpicLoot" }))), "no note for the switched off mod");
    const lead = T("mods.gale.dialog.disabled", { names: "" }).split(":")[0];
    assert.strictEqual(m.html.split(lead).length - 1, 1, "not exactly one note line for switched off mods");
    assert.ok(m.html.includes(T("mods.gale.dialog.not_installed")), "an add does not say it is not installed");
    assert.ok(m.html.includes("2.20.1") && m.html.includes("2.21.0"), "an update does not show both versions");
    assert.ok(!m.html.includes("Someone-OnlyHere"), "a mod only the server has is offered");
    assert.strictEqual(m.buttons["#galeInstall"].disabled, false, "Install is off on a stopped server");
    assert.ok(!m.html.includes("mwarn"), "a stopped server got the stop sentence");

    for (const [why, opts, st] of [
      ["the host side says the folder is held", {}, state({ heldBy: "Final Sunset" })],
      ["the state on screen says it is running", { status: "Running" }, state()],
    ]) {
      const q = page(opts);
      q.context.galeApply(st);
      q.context.galeDialog();
      const d = q.modals[0];
      assert.ok(d.html.includes(q.context.esc(T("mods.gale.dialog.running", { name: "Final Sunset" }))), why + ": no stop sentence");
      assert.strictEqual(d.buttons["#galeInstall"].disabled, true, why + ": Install is offered");
    }
  });

  await test("7. install: unticks first, one at a time, pinned, stamped only when whole", async () => {
    const added = [];
    const ok = page({ replies: {
      "gale.untick": { ok: true, state: state() },
      "mods.addFromUrl": params => { added.push(params); return { Installed: true, Version: params.url.split("/v/")[1].replace("/", "") }; },
      "gale.dismiss": { ok: true, state: state({ lastApproved: "2026-10-11T09:00:00.123456+00:00", check: { due: false } }) },
    } });
    ok.context.galeApply(state());
    ok.context.galeDialog();
    const m = ok.modals[0];
    /* The host takes the tick off the update and leaves the rest. */
    const rows = ok.context.galeDialogRows(state()).rows;
    for (const b of m.boxes) if (rows[Number(b.getAttribute())].name === "ValheimModding-Jotunn") b.checked = false;
    m.handlers.galeInstall();
    for (let i = 0; i < 20; i++) await new Promise(r => setImmediate(r));

    assert.deepStrictEqual(ok.methods(), ["gale.untick", "mods.addFromUrl", "gale.dismiss"], ok.methods().join(","));
    const untick = ok.sent[0].params;
    assert.deepStrictEqual(Array.from(untick.unticked), ["ValheimModding-Jotunn", "shudnal-ExtraSlots"],
      "the untick the host made, and the one remembered from before, were not both written");
    assert.deepStrictEqual(Array.from(untick.ticked), ["Azumatt-AzuCraftyBoxes"]);
    assert.strictEqual(added.length, 1);
    assert.strictEqual(added[0].url, "https://thunderstore.io/c/valheim/p/Azumatt/AzuCraftyBoxes/v/1.8.4/", "the version is not pinned");
    assert.strictEqual(added[0].profile, "Final Sunset");
    assert.strictEqual(added[0].requireStopped, true, "the add does not ask for a stopped server");
    assert.strictEqual(ok.sent[2].params.seq, 7, "the approval was not stamped with the check that was shown");
    assert.ok(ok.toasts.some(t => t.includes(T("mods.gale.install.done.toast", { count: 1 }))), ok.toasts.join(" | "));

    /* A walk with a failure stamps nothing, so the row stays for the rest. The mod that did
       go in is on the disk now: the host side is asked to read the folder again (and never
       Gale), and its answer is what the row, the line and the next Review draw from, so the
       add that landed is not offered a second time. */
    const left = state({ check: { addCount: 0, updateCount: 1,
      rows: state().check.rows.map(r => (r.name === "Azumatt-AzuCraftyBoxes" ? Object.assign({}, r, { kind: "same", serverVersion: "1.8.4" }) : r)) } });
    const bad = page({ replies: {
      "gale.untick": { ok: true, state: state() },
      "mods.addFromUrl": params => (params.url.includes("Jotunn") ? { Installed: false, Error: "zip was short", Reason: null } : { Installed: true, Version: "1.8.4" }),
      "gale.rescan": { ok: true, state: left },
    } });
    bad.context.galeApply(state());
    bad.context.galeDialog();
    bad.modals[0].handlers.galeInstall();
    for (let i = 0; i < 20; i++) await new Promise(r => setImmediate(r));
    assert.deepStrictEqual(bad.methods(), ["gale.untick", "mods.addFromUrl", "mods.addFromUrl", "gale.rescan"], bad.methods().join(","));
    assert.strictEqual(bad.sent[3].params.profile, "Final Sunset", "the rescan was not for the realm the walk ran on");
    assert.strictEqual(bad.conditions.get("galeChanged").msg, T("cond.gale.msg", { add: 0, update: 1 }),
      "the row still counts the mod that went in: " + bad.conditions.get("galeChanged").msg);
    bad.context.galeDialog();
    assert.ok(bad.modals[1], "Review did not open after the walk");
    assert.ok(!bad.modals[1].html.includes("AzuCraftyBoxes"), "Review offers the mod that went in again");
    assert.ok(bad.modals[1].html.includes("ValheimModding-Jotunn"), "Review lost the mod that failed");
    assert.ok(bad.toasts.some(t => t.includes(T("mods.add.failed.toast", { detail: "zip was short" }))), "the Mods hall's own failure sentence was not used");
    assert.ok(bad.toasts.some(t => t.includes(T("mods.gale.install.partial.toast", { done: 1, left: 1 }))), bad.toasts.join(" | "));

    /* A server that came up between two mods stops the walk where it is. */
    const up = page({ replies: {
      "gale.untick": { ok: true, state: state() },
      "mods.addFromUrl": () => ({ Installed: false, Reason: "serverRunning", ErrorParams: { name: "Final Sunset" }, Error: "Stop the server" }),
    } });
    up.context.galeApply(state());
    up.context.galeDialog();
    up.modals[0].handlers.galeInstall();
    for (let i = 0; i < 20; i++) await new Promise(r => setImmediate(r));
    assert.deepStrictEqual(up.methods(), ["gale.untick", "mods.addFromUrl", "gale.rescan"], "the walk went on past a running server: " + up.methods());
    assert.ok(up.toasts.some(t => t.includes(T("mods.gale.install.server_running.toast", { name: "Final Sunset" }))), up.toasts.join(" | "));

    /* Nothing ticked: the choices are saved and the row goes, and nothing is installed. */
    const none = page({ replies: { "gale.untick": { ok: true, state: state() }, "gale.dismiss": { ok: true, state: state({ check: { due: false } }) } } });
    none.context.galeApply(state());
    none.context.galeDialog();
    for (const b of none.modals[0].boxes) b.checked = false;
    none.modals[0].handlers.galeInstall();
    for (let i = 0; i < 20; i++) await new Promise(r => setImmediate(r));
    assert.deepStrictEqual(none.methods(), ["gale.untick", "gale.dismiss"], none.methods().join(","));
  });

  await test("8. the wiring: the box, the three moments, the realm switch, the host side", () => {
    const hall = HTML.slice(HTML.indexOf('id="page-mods"'), HTML.indexOf('id="modsTable"'));
    for (const id of ["galeBox", "galeId", "galeCheckBtn", "galeForgetBtn", "galeReviewBtn", "galeStatus"])
      assert.ok(hall.includes('id="' + id + '"'), "the Mods hall has no #" + id);
    assert.ok(/data-i18n="mods\.gale\.box\.label"/.test(hall), "the box's label is not from the catalog");
    assert.ok(/\$\("#galeCheckBtn"\)\?\.addEventListener\("click",\(\)=>galeCheckNow\(\)\)/.test(APP), "Check now is not wired");
    assert.ok(/\$\("#galeForgetBtn"\)\?\.addEventListener\("click",\(\)=>galeForget\(\)\)/.test(APP), "Forget is not wired");

    /* The check is called from exactly three places outside the block: the hall's walk in,
       the first frame and Check now (which is inside the block). */
    const outside = APP.slice(0, APP.indexOf("/* GALE-BEGIN")) + APP.slice(APP.indexOf("/* GALE-END */"));
    const calls = outside.match(/galeCheck\(\)/g) || [];
    assert.strictEqual(calls.length, 2, "galeCheck() is called from " + calls.length + " places outside the block, not the hall and the first frame");
    assert.ok(/if\(name==="mods"\) galeCheck\(\);/.test(fnBody(APP, "function goPage(")), "walking into the Mods hall does not check");
    assert.ok(/requestAnimationFrame\(\(\)=>setTimeout\(\(\)=>galeCheck\(\),0\)\)/.test(APP), "the first frame does not check after it is up");
    const sw = fnBody(APP, "async function switchServer(");
    assert.ok(/"galeChanged"\]\.forEach\(clearCondition\)/.test(sw), "a realm switch leaves the Gale row up");
    assert.ok(/GALE=null;/.test(sw) && /galeRefresh\(\);/.test(sw), "a realm switch keeps the old realm's follow in the box");

    for (const rpc of ["gale.get", "gale.set", "gale.forget", "gale.check", "gale.dismiss", "gale.untick", "gale.rescan", "gale.bepinex"])
      assert.ok(GALE_CS.includes('RegisterRpc("' + rpc + '"'), "the host side has no " + rpc);
    assert.ok(/RegisterGaleRpcs\(\);/.test(BRIDGE), "the Gale RPCs are never registered");
    assert.ok(/Task\.Run\(\(\) => RunGaleCheckAsync/.test(GALE_CS), "the check does not run on a worker");
    const add = BRIDGE.slice(BRIDGE.indexOf('RegisterRpc("mods.addFromUrl"'), BRIDGE.indexOf('RegisterRpc("mods.hexiumPrepare"'));
    assert.ok(/Value<bool\?>\("requireStopped"\)/.test(add) && /RunningSessionName\(new\[\] \{ pluginsDir \}\)/.test(add),
      "the add does not refuse a plugins folder a running server holds");
    assert.ok(/Value<string>\("profile"\)/.test(add) && /"realmChanged"/.test(add), "the add does not hold the walk to its realm");
    assert.ok(!/Remove|Delete/.test(GALE_CS.replace(/TryRemove/g, "").replace(/\/\/\/.*|\/\/.*/g, "")),
      "the Gale bridge calls something that removes");
  });

  await test("9. every id the block asks for is in the catalog, with no long dash and no AI", () => {
    const src = block();
    const ids = new Set();
    let hit;
    const call = /T\(\s*"([^"]+)"/g;
    while ((hit = call.exec(src)) !== null) ids.add(hit[1]);
    const table = /textId:"([^"]+)"/g;
    while ((hit = table.exec(src)) !== null) ids.add(hit[1]);
    const label = /labelId:"([^"]+)"/g;
    while ((hit = label.exec(src)) !== null) ids.add(hit[1]);
    assert.ok(ids.size >= 30, "only " + ids.size + " ids were found in the block");
    for (const id of ids) {
      assert.ok(KEYS[id], id + " is not in en.json");
      const words = JSON.stringify(KEYS[id].lore) + JSON.stringify(KEYS[id].plain || "");
      assert.ok(!/[\u2013\u2014]/.test(words), id + " carries a long dash");
      assert.ok(!/\bAI\b/.test(words), id + " says AI");
    }
    /* Every sentence the host side can name is in the table the status line reads. */
    for (const id of (GALE_CS + fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Tools", "GaleSyncClient.cs"), "utf8")
      + fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Tools", "GaleProfileId.cs"), "utf8")).match(/"mods\.gale\.[a-z_.]+"/g) || [])
      assert.ok(src.includes('{textId:' + id + '}'), "the status line cannot say " + id);
  });

  await test("10. no BepInEx: the note and BepInEx first, or Install off with the sentence", async () => {
    const carried = state({ check: { bepinex: { missing: true, inProfile: true } } });
    const p = page();
    p.context.galeApply(carried);
    p.context.galeDialog();
    const m = p.modals[0];
    const first = p.context.esc(T("mods.gale.dialog.bepinex_first"));
    assert.strictEqual(m.html.split(first).length - 1, 1, "not exactly one BepInEx-first note");
    assert.ok(!m.html.includes(p.context.esc(T("mods.gale.dialog.bepinex_missing"))), "the pack is in the profile and Install is refused");
    assert.strictEqual(m.buttons["#galeInstall"].disabled, false, "Install is off although the profile carries BepInEx");

    /* BepInEx is present: no note at all. */
    const here = page();
    here.context.galeApply(state({ check: { bepinex: { missing: false, inProfile: true } } }));
    here.context.galeDialog();
    assert.ok(!here.modals[0].html.includes(first), "a server with BepInEx got the BepInEx note");

    /* No pack in the profile: Install off, and the sentence says to put BepInEx in first. */
    const bare = page();
    bare.context.galeApply(state({ check: { bepinex: { missing: true, inProfile: false } } }));
    bare.context.galeDialog();
    assert.ok(bare.modals[0].html.includes(bare.context.esc(T("mods.gale.dialog.bepinex_missing"))), "no sentence to put BepInEx in first");
    assert.strictEqual(bare.modals[0].buttons["#galeInstall"].disabled, true, "Install is offered with no BepInEx and no pack in the profile");

    /* Install: the host side's BepInEx write goes before the first mod, and its answer is
       taken as the loader's new state. */
    const order = page({ replies: {
      "gale.untick": { ok: true, state: carried },
      "gale.bepinex": { ok: true, wrote: true, bepinex: { installed: true, consent: true } },
      "mods.addFromUrl": params => ({ Installed: true, Version: params.url.split("/v/")[1].replace("/", "") }),
      "gale.dismiss": { ok: true, state: state({ check: { due: false } }) },
    } });
    order.context.galeApply(carried);
    order.context.galeDialog();
    order.modals[0].handlers.galeInstall();
    for (let i = 0; i < 20; i++) await new Promise(r => setImmediate(r));
    assert.deepStrictEqual(order.methods(), ["gale.untick", "gale.bepinex", "mods.addFromUrl", "mods.addFromUrl", "gale.dismiss"], order.methods().join(","));
    assert.strictEqual(order.sent[1].params.profile, "Final Sunset", "BepInEx was not asked for the realm the dialog was opened on");
    assert.deepStrictEqual(JSON.parse(JSON.stringify(order.context.S.bepinex)), { installed: true, consent: true }, "the loader's new state was not taken");

    /* A BepInEx write that did not land stops the walk before any mod. */
    const stop = page({ replies: {
      "gale.untick": { ok: true, state: carried },
      "gale.bepinex": { ok: false, Reason: "serverRunning", ErrorParams: { name: "Final Sunset" } },
      "gale.rescan": { ok: true, state: carried },
    } });
    stop.context.galeApply(carried);
    stop.context.galeDialog();
    stop.modals[0].handlers.galeInstall();
    for (let i = 0; i < 20; i++) await new Promise(r => setImmediate(r));
    assert.deepStrictEqual(stop.methods(), ["gale.untick", "gale.bepinex", "gale.rescan"], "a mod went in without BepInEx: " + stop.methods());
    assert.ok(stop.toasts.some(t => t.includes(T("mods.gale.install.server_running.toast", { name: "Final Sunset" }))), stop.toasts.join(" | "));

    /* A server with BepInEx never asks for it. */
    const plain = page({ replies: {
      "gale.untick": { ok: true, state: state() },
      "mods.addFromUrl": () => ({ Installed: true, Version: "1" }),
      "gale.dismiss": { ok: true, state: state({ check: { due: false } }) },
    } });
    plain.context.galeApply(state());
    plain.context.galeDialog();
    plain.modals[0].handlers.galeInstall();
    for (let i = 0; i < 20; i++) await new Promise(r => setImmediate(r));
    assert.ok(!plain.methods().includes("gale.bepinex"), "BepInEx was asked for on a server that has it");
  });

  await test("11. a build no longer on Thunderstore: its sentence, the summary, the mark on the next review", async () => {
    const gone = { Installed: false, Reason: "notAvailable", Error: "raw", ErrorParams: { mod: "Azumatt-AzuCraftyBoxes", version: "1.8.4" } };
    const marked = state({ check: { due: false, addCount: 0, updateCount: 0,
      rows: state().check.rows.map(r => (r.name === "Azumatt-AzuCraftyBoxes" ? Object.assign({}, r, { notAvailable: true }) : r))
        .map(r => (r.name === "ValheimModding-Jotunn" ? Object.assign({}, r, { kind: "same", serverVersion: "2.21.0" }) : r)) } });
    const p = page({ replies: {
      "gale.untick": { ok: true, state: state() },
      "mods.addFromUrl": params => (params.url.includes("AzuCraftyBoxes") ? gone : { Installed: true, Version: "2.21.0" }),
      "gale.dismiss": { ok: true, state: marked },
    } });
    p.context.galeApply(state());
    p.context.galeDialog();
    p.modals[0].handlers.galeInstall();
    for (let i = 0; i < 20; i++) await new Promise(r => setImmediate(r));
    const sentence = T("mods.add.not_available.toast", { mod: "Azumatt-AzuCraftyBoxes", version: "1.8.4" });
    assert.ok(sentence.startsWith("Azumatt-AzuCraftyBoxes 1.8.4 is no longer on Thunderstore"), sentence);
    assert.ok(p.toasts.some(t => t === "ᚦ " + sentence), "the not available sentence was not said: " + p.toasts.join(" | "));
    assert.ok(!p.toasts.some(t => t.includes("raw")), "the raw sentence was shown");
    assert.deepStrictEqual(p.methods(), ["gale.untick", "mods.addFromUrl", "mods.addFromUrl", "gale.dismiss"], "the walk did not go on past a gone build: " + p.methods());
    assert.ok(p.toasts.some(t => t === "ᚨ " + T("mods.gale.install.summary.toast", { done: 1, missing: 1 })), "the summary does not count both: " + p.toasts.join(" | "));

    /* The next review: the row carries its mark, starts unticked, and an untouched gone row
       is not written to the unticks. */
    const rows = p.context.galeDialogRows(marked).rows;
    const azu = rows.find(r => r.name === "Azumatt-AzuCraftyBoxes");
    assert.ok(azu && azu.notAvailable && !azu.ticked, "the gone row is offered as a plain add: " + JSON.stringify(azu));
    const q = page({ replies: { "gale.untick": { ok: true, state: marked }, "gale.dismiss": { ok: true, state: marked } } });
    q.context.galeApply(marked);
    q.context.galeDialog();
    const m = q.modals[0];
    assert.ok(m.html.includes(">" + T("mods.gale.dialog.not_available") + "<"), "the row has no not available mark");
    assert.ok(m.html.includes(q.context.esc(T("mods.gale.dialog.not_available.title"))), "the mark does not say why");
    m.handlers.galeInstall();
    for (let i = 0; i < 20; i++) await new Promise(r => setImmediate(r));
    const untick = q.sent.find(x => x.method === "gale.untick");
    assert.ok(!Array.from(untick.params.unticked).includes("Azumatt-AzuCraftyBoxes"), "leaving a gone row was written as a no");

    /* A gone build among real failures: the partial toast counts all three. */
    const mix = page({ replies: {
      "gale.untick": { ok: true, state: state() },
      "mods.addFromUrl": params => (params.url.includes("AzuCraftyBoxes") ? gone : { Installed: false, Error: "zip was short" }),
      "gale.rescan": { ok: true, state: state() },
    } });
    mix.context.galeApply(state());
    mix.context.galeDialog();
    mix.modals[0].handlers.galeInstall();
    for (let i = 0; i < 20; i++) await new Promise(r => setImmediate(r));
    assert.ok(mix.toasts.some(t => t === "ᚦ " + T("mods.gale.install.partial.missing.toast", { done: 0, missing: 1, left: 1 })), mix.toasts.join(" | "));

    /* The plain add says the same sentence. */
    assert.ok(/const own=modNotAvailableText\(r\)\|\|hostSentence\(r\.Reason,r\.ErrorParams\);/.test(fnBody(APP, "async function doAddMod(")),
      "a pasted link does not get the not available sentence");
  });

  console.log("");
  console.log(failures.length ? "gale profile selftest: " + failures.length + " FAILED, " + passed + " passed"
    : "gale profile selftest: all " + passed + " rules pass");
  process.exit(failures.length ? 1 : 0);
})();
