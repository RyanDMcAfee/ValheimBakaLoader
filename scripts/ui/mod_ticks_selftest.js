/* Ticking rows of the Mods table and switching the ticked ones in one request (1.3.2, issue 33),
 * driven for real.
 *
 * WHY THIS EXISTS. A tester halving a list of sixty mods to find the one that broke a start had
 * to right-click every row twice. 1.3.2 gives every row a box, a bar of five buttons and one
 * host call, mods.switchMany, for the whole set. Every way that goes wrong is quiet: a double
 * click that sends two batches, a search that hides a tick and then switches it anyway without
 * a word, a scan that forgets the ticks, a reply from the realm the host has just left landing
 * on this one, a partial result that reads as a success. So each is driven here.
 *
 * THE CODE UNDER TEST is the block between the MODTICKS markers in app.js, the MODROW block (the
 * row and its box), the switch reason table and its sentence, modByKey, modRealmUp and
 * renderRowStatus, evaluated with the page's globals stood in for and the real en.json. A tiny
 * stand-in DOM holds the bar and the table's boxes; renderMods is stood in by the same four
 * steps the real one takes (read the focused box, write the rows, give the focus back, draw the
 * bar), and the real renderMods is held to that order by rule 8.
 *
 *   1. A server up: both switches are grey with the running tip, a press sends nothing, and the
 *      host's serverRunning refusal is said in the page's words with no scan and no row marked.
 *   2. One mod left as it was among ten: the partial toast, one scan, the row's Failed pill with
 *      the reason on hover, one Saga line naming it and why.
 *   3. A library switched off with what needs it left on is sent like any other row, and the
 *      request is the same whichever order the boxes were ticked in.
 *   4. The protected row has no box; Tick all shown passes it by; a tick on it put there by hand
 *      is never sent.
 *   5. Ticks survive a search, its clearing and a sort; ticks the search hides are counted and
 *      are named in a question before anything is sent, and the switch covers every ticked row;
 *      Escape in the search box clears the search and not the ticks; a Shift range follows the
 *      order on screen.
 *   6. While the batch is out the hall's other jobs are held off (S.modsUpdating), a second
 *      press sends nothing, and the host's busy refusal is worded with nothing scanned.
 *   7. Two presses in one tick, and Enter held down, send exactly one mods.switchMany.
 *   8. Keyboard and screen reader: a real checkbox with its own label in every row, a header
 *      label, a polite live count, Shift with Space ranges, the box keeps the keyboard after a
 *      tick and gets it back after a redraw, and a press from the bar keeps it on the bar.
 *   9. The bar: hidden before a scan, rolled in through reveal, five buttons with a title each.
 *  10. A scan keeps only the ticks of rows still on disk, a realm switch empties them, and an
 *      answer for a realm no longer on screen is dropped without a toast or a scan.
 *  11. A set mixing on and off rows: Switch off ticked sends only the ones that are on, and the
 *      skipped and the not found are counted in the one toast.
 *  12. The cost: one mods.switchMany and one rescan for the batch, and no rescan at all when
 *      nothing moved.
 *
 *     node scripts/ui/mod_ticks_selftest.js
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
/* A rule that waits on an answer the stand-in host never gives empties the event loop, and node
   would end quietly with 0. So the run has to reach its summary to pass. */
let finished = false;
process.on("exit", code => {
  if (!finished && code === 0) {
    console.log("mod ticks selftest: FAIL, the run stopped part way (a rule waited on an answer that never came)");
    process.exitCode = 1;
  }
});
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
  let depth = 0;
  for (let i = APP.indexOf("{", a); i < APP.length; i++) {
    if (APP[i] === "{") depth++;
    else if (APP[i] === "}" && --depth === 0) return APP.slice(a, i + 1);
  }
  throw new Error("no end to " + head);
}

/* The catalog lookup the page makes, plurals included (English: one and other). */
function T(id, params) {
  const entry = KEYS[id];
  if (!entry) throw new Error("the catalog has no sentence for " + id);
  let lore = entry.lore;
  if (lore && typeof lore === "object") {
    const n = Number(params && params[entry.plural]);
    lore = n === 1 ? lore.one : lore.other;
  }
  if (typeof lore !== "string") throw new Error("no usable sentence for " + id);
  return lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

const FAIL = { failed: true };

/* ------------------------------------------------------------------ the stand-in page */

function page(opts) {
  opts = opts || {};
  const sent = [], toasts = [], scans = [], logs = [], confirms = [];
  const doc = { activeElement: null };
  let boxes = [];
  let pending = null;

  function boxEl(key, checked) {
    const box = {
      key, checked, focused: 0,
      getAttribute: n => (n === "data-mod-tick" ? key : null),
      closest: sel => (sel === "#modTable" ? {} : null),
      focus() { box.focused++; doc.activeElement = box; },
    };
    return box;
  }
  function barEl() {
    const kids = {};
    const bar = {
      shown: false, built: 0,
      set innerHTML(html) {
        bar.built++;
        bar.html = html;
        for (const k of Object.keys(kids)) delete kids[k];
        kids["#modTickCount"] = { textContent: "" };
        kids["#modTickHidden"] = { textContent: "" };
        for (const m of html.matchAll(/data-mod-bulk="(\w+)"/g)) {
          const id = m[1];
          const b = {
            id, textContent: "", disabled: false, title: "", focused: 0,
            getAttribute: n => (n === "data-mod-bulk" ? id : null),
            closest: sel => (sel === "#modTickBar" ? bar : null),
            focus() { b.focused++; doc.activeElement = b; },
          };
          kids['[data-mod-bulk="' + id + '"]'] = b;
        }
      },
      get innerHTML() { return bar.html || ""; },
      querySelector: sel => (sel === "[data-mod-bulk]" ? bar.buttons()[0] || null : kids[sel] || null),
      querySelectorAll: sel => (sel === "[data-mod-bulk]" ? bar.buttons() : []),
      buttons: () => Object.keys(kids).filter(k => k.startsWith("[data-mod-bulk")).map(k => kids[k]),
      button: id => kids['[data-mod-bulk="' + id + '"]'],
      text: () => (kids["#modTickCount"] || {}).textContent,
      hiddenText: () => (kids["#modTickHidden"] || {}).textContent,
    };
    return bar;
  }
  const bar = barEl();

  const context = {
    S: {
      mods: (opts.mods || []).map(m => Object.assign({}, m)), profileName: "Final Sunset",
      state: { status: opts.status || "Stopped" }, modRowStatus: {}, modTicks: new Set(opts.ticks || []),
      modTickAnchor: null, modShown: null, modsScanning: false, modsUpdating: false,
    },
    Native: { available: opts.native !== false },
    FAIL, T, document: {
      get activeElement() { return doc.activeElement; },
      querySelectorAll: sel => (sel === "#modTable [data-mod-tick]" ? boxes.slice() : []),
    },
    $: sel => (sel === "#modTickBar" ? bar : null),
    reveal: (el, show) => { el.shown = !!show; return !!show; },
    revealText: (el, text) => { if (el) el.textContent = String(text == null ? "" : text); },
    pad: n => String(n).padStart(2, "0"),
    LOC: () => "en-US",
    modIsHeld: () => false,
    modLatestMark: () => "",
    modDateValue: () => NaN,
    lastPathPart: p => String(p || "").split(/[\\/]/).filter(Boolean).pop() || "",
    rpc: (method, params) => {
      sent.push({ method, params: JSON.parse(JSON.stringify(params || {})) });
      if (opts.hold) return new Promise(done => { pending = done; });
      return Promise.resolve(opts.reply ? opts.reply(method, params) : { ok: true, changed: 0, results: [] });
    },
    toast: t => toasts.push(t),
    logLine: (kind, text) => logs.push({ kind, text }),
    scanMods: o => {
      scans.push(o);
      if (opts.rescan) { context.S.mods = opts.rescan(context.S.mods); context.modTicksPrune(); }
      return Promise.resolve();
    },
    confirmModal: (title, body, ok, onOk) => { confirms.push({ title: title(), body: body(), ok: ok(), onOk }); },
    /* What the real renderMods does about the table and the bar, in its order. */
    renderMods: () => {
      const keep = context.modTickFocusKey();
      const shown = (opts.filter ? context.S.mods.filter(opts.filter) : context.S.mods);
      const order = opts.order ? shown.slice().sort(opts.order) : shown;
      boxes = order.map(m => {
        const html = context.modRowHtml(m, false);
        const hit = /data-mod-tick="([^"]+)"( checked)?/.exec(html);
        return hit ? boxEl(hit[1].replace(/&amp;/g, "&"), !!hit[2]) : null;
      }).filter(Boolean);
      if (doc.activeElement && doc.activeElement.key) doc.activeElement = null;
      context.modTickFocusRestore(keep);
      context.renderModTickBar(order);
    },
  };
  vm.createContext(context);
  const code = [
    line("const esc=").replace(/^const /, "var "),
    between("function modByKey(", "\n/* Which site an install answer"),
    between("/* The transient status a row shows", "\n/* The bulk-update bar's own record"),
    between("/* MODROW-BEGIN", "/* MODROW-END */"),
    between("function modRealmUp(){", "\n/* right-click a mod row"),
    between("const MOD_SWITCH_REASONS=[", "\n/* Switch a mod off (every .dll"),
    between("/* MODTICKS-BEGIN", "/* MODTICKS-END */"),
    "var __flags = () => ({ busy: MOD_BULK_BUSY, shift: MOD_TICK_SHIFT });",
  ].join("\n").replace(/^const /gm, "var ").replace(/^let /gm, "var ");
  vm.runInContext(code, context, { filename: "app.js#modticks" });
  for (const name of ["modTickCell", "modRowHtml", "renderModTickBar", "switchTicked", "switchTickedNow", "modTickClick",
    "modTicksPrune", "modBulkPress", "modTickFocusKey", "modTickFocusRestore", "modSwitchReasonText"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of app.js");
  return {
    context, sent, toasts, scans, logs, confirms, bar, doc,
    boxes: () => boxes,
    box: key => boxes.find(b => b.key === key),
    answer: r => { const done = pending; pending = null; done(r); },
    flags: () => context.__flags(),
  };
}

const row = (name, extra) => Object.assign({
  FullName: "Acme-" + name, ModName: name, Author: "Acme", InstalledVersion: "1.0.0", LatestVersion: "1.0.0",
  installedSource: "thunderstore", disabled: false, switchable: true,
}, extra || {});
const ten = () => "Aurora Borealis Cirrus Drift Ember Frost Gale Hearth Iron Juniper".split(" ").map(n => row(n));
const tick = (p, ...names) => names.forEach(n => p.context.modTickClick("Acme-" + n, true, false));
const settle = () => new Promise(done => setImmediate(done));
const ok = name => ({ fullName: "Acme-" + name, ok: true, skipped: false, count: 1, reason: null, error: null, errorParams: null });
const no = (name, reason, extra) => Object.assign({ fullName: "Acme-" + name, ok: false, skipped: reason === "alreadyOff" || reason === "alreadyOn",
  count: 0, reason, error: "HOST ENGLISH " + reason, errorParams: null }, extra || {});

(async function main() {
  await test("1. a server up: the switches are grey with the running tip, a press sends nothing, the host's refusal is worded", async () => {
    const p = page({ mods: ten(), status: "Running" });
    p.context.renderMods();
    tick(p, "Aurora", "Borealis");
    for (const id of ["off", "on"]) {
      assert.ok(p.bar.button(id).disabled, id + " is pressable while the server is up");
      assert.strictEqual(p.bar.button(id).title, T("mods.menu.toggle.tip.running"));
    }
    assert.ok(!p.bar.button("all").disabled, "ticking is not a write and stays open while the server is up");
    await p.context.switchTicked(true);
    assert.deepStrictEqual(p.sent, [], "a press sent a switch while the server is up");

    /* The page thought it was stopped and the host knew better: the whole batch is refused. */
    const q = page({ mods: ten(), reply: () => ({ ok: false, off: true, changed: 0, results: [], reason: "serverRunning",
      error: "HOST ENGLISH", errorParams: { name: "Final Sunset" } }) });
    q.context.renderMods();
    tick(q, "Aurora", "Borealis");
    await q.context.switchTicked(true);
    assert.strictEqual(q.sent.length, 1);
    assert.deepStrictEqual(q.toasts, ["ᚦ " + T("mods.switch.reason.server_running", { name: "Final Sunset" })]);
    assert.deepStrictEqual(q.scans, [], "a refused batch scanned");
    assert.deepStrictEqual(q.context.S.modRowStatus, {}, "a refused batch marked rows");
    assert.ok(!q.flags().busy && !q.context.S.modsUpdating, "a refused batch left the hall busy");
  });

  await test("2. one mod left as it was among ten: the partial toast, one scan, its Failed pill and one Saga line", async () => {
    const names = ten().map(m => m.ModName);
    const p = page({ mods: ten(), reply: () => ({ ok: true, off: true, changed: 9, skipped: 0, failed: 1,
      results: names.map(n => n === "Ember" ? no(n, "failed", { errorParams: { detail: "the file is in use" } }) : ok(n)) }) });
    p.context.renderMods();
    p.context.modTickAllShown();
    await p.context.switchTicked(true);
    assert.strictEqual(p.sent.length, 1);
    assert.deepStrictEqual(p.sent[0].params.fullNames, names.map(n => "Acme-" + n));
    assert.strictEqual(p.sent[0].params.off, true);
    assert.strictEqual(p.sent[0].params.profile, "Final Sunset");
    assert.strictEqual(p.scans.length, 1, "not one rescan");
    assert.deepStrictEqual(p.toasts, ["ᚦ " + T("mods.bulk.partial.toast", { ok: 9, failed: 1 })]);
    assert.ok(/9 switched, 1 left as it was/.test(p.toasts[0]), p.toasts[0]);
    const st = p.context.S.modRowStatus["Acme-Ember"];
    const why = T("mods.switch.reason.failed.off", { mod: "Ember", detail: "the file is in use" });
    assert.strictEqual(JSON.stringify(st), JSON.stringify({ phase: "failed", bulk: true, error: why }));
    const pill = p.context.modRowHtml(p.context.S.mods.find(m => m.ModName === "Ember"), false);
    assert.ok(pill.includes('class="pill rowfail"') && pill.includes('title="' + why + '"'), "the row does not say Failed with the reason: " + pill);
    const saga = p.logs.filter(l => l.text.startsWith("[Mods] switched"));
    assert.strictEqual(saga.length, 1, "not one Saga line for the batch");
    assert.strictEqual(saga[0].kind, "warn");
    assert.ok(saga[0].text.includes("Acme-Ember (HOST ENGLISH failed)") && saga[0].text.includes("Acme-Aurora"), saga[0].text);
    /* The ticks stand, so the other button reverses the batch in one press. */
    assert.strictEqual(p.context.S.modTicks.size, 10);
  });

  await test("3. a library off under what needs it is sent like any row, the same whichever order it was ticked in", async () => {
    const mods = [row("App", { missingDependencies: [] }), row("Lib"), row("Other")];
    const asked = [];
    for (const order of [["Lib", "Other"], ["Other", "Lib"]]) {
      const p = page({ mods, reply: () => ({ ok: true, off: true, changed: 2, results: order.map(ok) }) });
      p.context.renderMods();
      tick(p, ...order);
      await p.context.switchTicked(true);
      asked.push(p.sent[0].params.fullNames);
      assert.ok(!p.toasts[0].startsWith("ᚦ"), "a library under a dependent was refused: " + p.toasts[0]);
    }
    assert.deepStrictEqual(asked[0], ["Acme-Lib", "Acme-Other"]);
    assert.deepStrictEqual(asked[1], asked[0], "the request depends on the order of the ticks");
  });

  await test("4. the protected row has no box, Tick all passes it by, and a tick put on it by hand is never sent", async () => {
    const mods = [row("Aurora"), row("Patcher", { switchable: false }), row("Borealis", { disabled: true })];
    const p = page({ mods });
    const html = p.context.modRowHtml(mods[1], false);
    assert.ok(html.includes('<td class="modtick"></td>') && !html.includes("data-mod-tick"), "the protected row has a box: " + html);
    assert.ok(/^<tr data-key="Acme-Patcher"><td class="modtick"><\/td><td><strong>Patcher/.test(html), html.slice(0, 90));
    /* An older host with no flag at all still gets a box: the host refuses protected anyway. */
    assert.ok(p.context.modRowHtml(row("Old", { switchable: undefined }), false).includes('data-mod-tick="Acme-Old"'));
    p.context.renderMods();
    p.context.modTickAllShown();
    assert.deepStrictEqual([...p.context.S.modTicks].sort(), ["Acme-Aurora", "Acme-Borealis"]);
    p.context.S.modTicks.add("Acme-Patcher");
    await p.context.switchTicked(true);
    assert.deepStrictEqual(p.sent[0].params.fullNames, ["Acme-Aurora"], "the protected row or an off row was sent to Switch off");
  });

  await test("5. ticks survive a search, its clearing and a sort; hidden ticks are counted and named first; a range follows the screen", async () => {
    let words = "";
    const p = page({ mods: ten(), filter: m => !words || m.ModName.toLowerCase().includes(words) });
    p.context.renderMods();
    tick(p, "Aurora", "Cirrus", "Gale");
    words = "r";   // every row but Gale has an r in its name, so Gale is the one hidden
    p.context.renderMods();
    assert.strictEqual(p.context.S.modTicks.size, 3, "typing a search dropped ticks");
    assert.ok(p.box("Acme-Aurora").checked && p.box("Acme-Cirrus").checked && !p.box("Acme-Drift").checked);
    assert.strictEqual(p.bar.text(), T("mods.tick.count", { ticked: 3, shown: 9 }));
    assert.strictEqual(p.bar.hiddenText(), T("mods.tick.hidden", { hidden: 1 }));

    /* The switch covers every ticked row, and says which before it sends a thing. */
    await p.context.switchTicked(true);
    assert.deepStrictEqual(p.sent, [], "a hidden tick was switched without a question");
    assert.strictEqual(p.confirms.length, 1);
    const c = p.confirms[0];
    assert.strictEqual(c.title, T("mods.bulk.confirm.off.title", { count: 3 }));
    assert.strictEqual(c.ok, T("mods.bulk.off"));
    assert.ok(c.body.includes(T("mods.bulk.confirm.hidden", { hidden: 1 })) && c.body.includes("Acme-Gale")
      && c.body.includes("Acme-Aurora") && c.body.includes("Acme-Cirrus"), c.body);
    c.onOk();
    await settle();
    assert.deepStrictEqual(p.sent[0].params.fullNames, ["Acme-Aurora", "Acme-Cirrus", "Acme-Gale"]);

    /* Clearing the search shows the hidden tick again, still ticked. */
    words = "";
    p.context.renderMods();
    assert.ok(p.box("Acme-Gale").checked, "clearing the search lost the hidden tick");
    assert.strictEqual(p.bar.hiddenText(), "");

    /* Escape in the search box is setModFilter(""), and neither touches a tick. */
    const esc = between('$("#modSearch")?.addEventListener("keydown",e=>{', "\n});");
    assert.ok(esc.includes('setModFilter("")') && !/modTicks/.test(esc) && !/modTicks/.test(fnBody("function setModFilter(")),
      "Escape in the search box reaches the ticks");

    /* A range follows the order on screen: sorted Z to A, Shift from Juniper to Gale ticks the three between. */
    const q = page({ mods: ten(), order: (a, b) => b.ModName.localeCompare(a.ModName) });
    q.context.renderMods();
    q.context.modTickClick("Acme-Juniper", true, false);
    q.context.modTickClick("Acme-Gale", true, true);
    assert.deepStrictEqual([...q.context.S.modTicks].sort(), ["Acme-Gale", "Acme-Hearth", "Acme-Iron", "Acme-Juniper"]);
    /* and Shift on a box being unticked unticks the range */
    q.context.modTickClick("Acme-Iron", false, true);
    assert.deepStrictEqual([...q.context.S.modTicks].sort(), ["Acme-Juniper"]);
  });

  await test("6. while the batch is out the hall's other jobs are held off, and the host's busy is worded", async () => {
    const p = page({ mods: ten(), hold: true });
    p.context.renderMods();
    tick(p, "Aurora");
    const run = p.context.switchTicked(true);
    assert.ok(p.flags().busy && p.context.S.modsUpdating, "the hall is not busy while the batch is out");
    assert.ok(p.bar.button("off").disabled && p.bar.button("on").disabled, "a switch can be pressed while the batch is out");
    /* Not awaited: a second request would wait on an answer that never comes. */
    p.context.switchTicked(true);
    p.context.switchTicked(false);
    p.context.modBulkPress("on");
    assert.strictEqual(p.sent.length, 1, "a second batch went out while the first was out");
    p.answer({ ok: false, off: true, changed: 0, results: [], reason: "busy", error: "HOST ENGLISH" });
    await run;
    assert.deepStrictEqual(p.toasts, ["ᚦ " + T("mods.switch.reason.busy")]);
    assert.deepStrictEqual(p.scans, []);
    assert.ok(!p.flags().busy && !p.context.S.modsUpdating);
    /* The other jobs on the hall read S.modsUpdating: Scan, Add, Update all, the row switch. */
    for (const head of ["async function scanMods(opts){", "async function doUpdateAll(){", "async function setModEnabled(mod,on){", "function addModFlow(){"])
      assert.ok(/if\(S\.modsUpdating\|\|S\.modsScanning\)|if\(S\.modsScanning\|\|S\.modsUpdating\)/.test(fnBody(head)), head + " does not wait for a batch");
  });

  await test("7. two presses in one tick, and Enter held down, send exactly one request", async () => {
    const p = page({ mods: ten(), hold: true });
    p.context.renderMods();
    tick(p, "Aurora", "Borealis");
    p.context.switchTicked(true);
    p.context.switchTicked(true);
    for (let i = 0; i < 6; i++) p.context.modBulkPress("off");
    assert.strictEqual(p.sent.filter(s => s.method === "mods.switchMany").length, 1);
    p.answer({ ok: true, off: true, changed: 2, results: [ok("Aurora"), ok("Borealis")] });
    await settle();
    assert.strictEqual(p.scans.length, 1);
    /* the flag is a plain variable set before the first await */
    const now = fnBody("async function switchTickedNow(");
    assert.ok(now.indexOf("MOD_BULK_BUSY=true") < now.indexOf("await "), "the busy flag is set after an await");
  });

  await test("8. keyboard and screen reader: a labelled box per row, a live count, Shift with Space, and the focus kept", async () => {
    const p = page({ mods: ten() });
    const html = p.context.modRowHtml(row("Aurora", { ModName: 'A "quoted" <name>' }), false);
    assert.ok(/<input type="checkbox" data-mod-tick="Acme-Aurora" aria-label="Tick A &quot;quoted&quot; &lt;name&gt;">/.test(html), html);
    assert.ok(!/tabindex="-1"/.test(html), "a box is taken out of the Tab order");
    const head = HTML.slice(HTML.indexOf('<table id="modsTable">'), HTML.indexOf('<tbody id="modTable">'));
    assert.ok(/<th class="modtick" scope="col"><span class="vhide" data-i18n="mods.tick.col">Tick<\/span><\/th>/.test(head), "the tick column has no header label");
    assert.strictEqual(head.indexOf('<th class="modtick"'), head.indexOf("<th "), "the tick column is not the first");
    p.context.renderMods();
    assert.ok(/<span class="modtickmsg" role="status" aria-live="polite">/.test(p.bar.innerHTML), "the count is not a polite live region");

    /* A tick never redraws: the box keeps the keyboard. */
    p.box("Acme-Cirrus").focus();
    const before = p.boxes();
    p.box("Acme-Cirrus").checked = true;
    p.context.modTickClick("Acme-Cirrus", true, false);
    assert.strictEqual(p.boxes(), before, "a tick redrew the table");
    assert.strictEqual(p.doc.activeElement.key, "Acme-Cirrus");
    /* A redraw gives the keyboard back to the same row's new box. */
    p.context.renderMods();
    assert.notStrictEqual(p.box("Acme-Cirrus"), before[2]);
    assert.strictEqual(p.doc.activeElement, p.box("Acme-Cirrus"), "the redraw lost the focused box");

    /* Shift with Space: the keydown remembers Shift for the click the key makes. */
    const wiring = between("/* MODTICKS-END */", "/* ---- Add a mod from any pasted Thunderstore link ---- */");
    assert.ok(/addEventListener\("keydown",[\s\S]*?MOD_TICK_SHIFT=!!e\.shiftKey/.test(wiring), "Shift with Space is not read");
    assert.ok(/const shift=!!e\.shiftKey\|\|MOD_TICK_SHIFT;\s*MOD_TICK_SHIFT=false;/.test(wiring), "the click does not take the Shift the key left");
    assert.ok(/modTickClick\(box\.getAttribute\("data-mod-tick"\),box\.checked,shift\)/.test(wiring));
    assert.ok(/addEventListener\("mousedown",[\s\S]*?if\(box&&e\.shiftKey\) e\.preventDefault\(\)/.test(wiring), "a Shift click selects the text between the rows");

    /* The real renderMods reads the focused box before it writes the rows and gives it back after. */
    const render = fnBody("function renderMods(){");
    const a = render.indexOf("const tickFocus=modTickFocusKey();");
    const w = render.indexOf('$("#modTable").innerHTML=shown.map(m=>modRowHtml(m,blind))');
    const b = render.indexOf("modTickFocusRestore(tickFocus);");
    assert.ok(a >= 0 && w > a && b > w, "renderMods does not keep the focused box across the redraw");
    assert.ok(render.trim().endsWith("renderModTickBar(shown);\n}"), "renderMods does not draw the bar last");

    /* A press from the bar keeps the keyboard on the bar: the other switch once this one has nothing left. */
    const q = page({ mods: ten(), reply: () => ({ ok: true, off: true, changed: 1, results: [ok("Aurora")] }),
      rescan: mods => mods.map(m => m.ModName === "Aurora" ? Object.assign({}, m, { disabled: true }) : m) });
    q.context.renderMods();
    tick(q, "Aurora");
    q.bar.button("off").focus();
    await q.context.switchTicked(true);
    assert.ok(q.bar.button("off").disabled, "Switch off ticked is still pressable with nothing on");
    assert.strictEqual(q.doc.activeElement, q.bar.button("on"), "the keyboard did not land on Switch on ticked");
  });

  await test("9. the bar: hidden until a scan, rolled in, five buttons with a title each, words from the catalog", async () => {
    const p = page({ mods: ten() });
    p.context.renderModTickBar(null);
    assert.strictEqual(p.bar.shown, false);
    assert.strictEqual(p.bar.built, 0, "an unscanned hall built the bar");
    const empty = page({ mods: [] });
    empty.context.renderModTickBar([]);
    assert.strictEqual(empty.bar.shown, false, "a hall with no mods shows the bar");
    p.context.renderMods();
    assert.strictEqual(p.bar.shown, true);
    assert.deepStrictEqual(p.bar.buttons().map(b => b.id), ["all", "none", "offones", "off", "on"]);
    const want = { all: "mods.tick.all", none: "mods.tick.none", offones: "mods.tick.off_ones", off: "mods.bulk.off", on: "mods.bulk.on" };
    for (const b of p.bar.buttons()) {
      assert.strictEqual(b.textContent, T(want[b.id]));
      assert.strictEqual(b.title, T(want[b.id] + ".title"));
    }
    /* Nothing ticked: only Tick all shown can do anything. */
    assert.deepStrictEqual(p.bar.buttons().filter(b => !b.disabled).map(b => b.id), ["all"]);
    /* Built once: a tick changes the words in place and keeps the same buttons. */
    const built = p.bar.built, off = p.bar.button("off");
    tick(p, "Aurora");
    assert.strictEqual(p.bar.built, built);
    assert.strictEqual(p.bar.button("off"), off);
    assert.ok(!off.disabled);
    assert.strictEqual(p.bar.text(), T("mods.tick.count", { ticked: 1, shown: 10 }));
    /* The real one rolls through reveal and fades its words through revealText. */
    const body = fnBody("function renderModTickBar(shown){");
    assert.ok(body.includes("reveal(bar,true)") && body.includes("reveal(bar,false)") && body.includes('revealText(bar.querySelector("#modTickCount")'),
      "the bar shows or its count changes without reveal");
    assert.ok(HTML.includes('<div class="modtickbar" id="modTickBar" style="display:none"></div>'), "the bar is not hidden in the markup");
  });

  await test("10. a scan keeps the ticks of rows on disk, a realm switch empties them, and a late answer from another realm is dropped", async () => {
    const p = page({ mods: ten(), rescan: mods => mods.filter(m => m.ModName !== "Drift") });
    p.context.renderMods();
    tick(p, "Aurora", "Drift");
    p.context.S.modRowStatus["Acme-Iron"] = { phase: "failed", bulk: true, error: "x" };
    p.context.S.modRowStatus["Acme-Gale"] = { phase: "updating" };
    await p.context.scanMods({ force: false });
    assert.deepStrictEqual([...p.context.S.modTicks], ["Acme-Aurora"], "a scan kept a tick on a row no longer on disk");
    assert.ok(!p.context.S.modRowStatus["Acme-Iron"], "a scan kept a batch's Failed pill");
    assert.ok(p.context.S.modRowStatus["Acme-Gale"], "a scan took a pill that was not the batch's");
    const scan = fnBody("async function scanMods(opts){");
    assert.ok(/S\.mods=rows; S\.modsScanned=true; S\.lastScan=clock\(\);\n\s*modTicksPrune\(\);/.test(scan), "the real scan does not prune the ticks after it takes the rows");

    const sw = APP.slice(APP.indexOf('  S.modFilter=""; if($("#modSearch")) $("#modSearch").value="";'));
    assert.ok(/^  S\.modFilter="";[^\n]*\n[^\n]*\n  S\.modTicks=new Set\(\); S\.modTickAnchor=null; S\.modShown=null;/.test(sw), "a realm switch keeps the ticks");

    const q = page({ mods: ten(), hold: true });
    q.context.renderMods();
    tick(q, "Aurora");
    const run = q.context.switchTicked(true);
    q.context.S.profileName = "Other Realm"; q.context.S.modsUpdating = false;   // what the realm switch does
    q.answer({ ok: true, off: true, changed: 1, results: [ok("Aurora")] });
    await run;
    assert.deepStrictEqual(q.toasts, [], "an answer for another realm was toasted");
    assert.deepStrictEqual(q.scans, [], "an answer for another realm scanned this one");
    assert.ok(!q.flags().busy);
    assert.ok(q.logs.some(l => /no longer open, so it was dropped/.test(l.text)));
  });

  await test("11. a mixed set: Switch off ticked sends only the rows that are on, and the rest are counted in one toast", async () => {
    const mods = [row("Aurora"), row("Borealis", { disabled: true }), row("Cirrus"), row("Drift"), row("Ember", { disabled: true })];
    const p = page({ mods, reply: () => ({ ok: true, off: true, changed: 1, skipped: 1, failed: 1,
      results: [ok("Aurora"), no("Cirrus", "alreadyOff"), no("Drift", "notFound")] }) });
    p.context.renderMods();
    p.context.modTickAllShown();
    await p.context.switchTicked(true);
    assert.deepStrictEqual(p.sent[0].params.fullNames, ["Acme-Aurora", "Acme-Cirrus", "Acme-Drift"], "rows already off were sent");
    assert.deepStrictEqual(p.toasts, ["ᚦ " + T("mods.bulk.partial.toast", { ok: 1, failed: 1 }) + " " + T("mods.bulk.skipped.note", { count: 1 })]);
    assert.strictEqual(p.context.S.modRowStatus["Acme-Drift"].error, T("mods.switch.reason.not_found", { mod: "Drift" }));
    assert.ok(!p.context.S.modRowStatus["Acme-Cirrus"], "a row already off is marked failed");
    /* All done and some already that way: the done toast with the note. All already that way: its own sentence. */
    const q = page({ mods, reply: () => ({ ok: true, on: true, changed: 2, results: [ok("Borealis"), ok("Ember")] }) });
    q.context.renderMods();
    q.context.modTickOffOnes();
    assert.deepStrictEqual([...q.context.S.modTicks].sort(), ["Acme-Borealis", "Acme-Ember"]);
    await q.context.switchTicked(false);
    assert.strictEqual(q.sent[0].params.off, false);
    assert.deepStrictEqual(q.toasts, ["ᚱ " + T("mods.bulk.on.done.toast", { count: 2 })]);
    const r = page({ mods, reply: () => ({ ok: true, changed: 0, results: [no("Aurora", "alreadyOff")] }) });
    r.context.renderMods();
    tick(r, "Aurora");
    await r.context.switchTicked(true);
    assert.deepStrictEqual(r.toasts, ["ᚱ " + T("mods.bulk.skipped.toast", { count: 1 })]);
    /* Every toast is one line whatever the size of the batch, so the three toast cap never hides it. */
    for (const t of [p, q, r]) assert.strictEqual(t.toasts.length, 1);
  });

  await test("12. the cost: one request and one rescan for fifty, and no rescan when nothing moved", async () => {
    const fifty = Array.from({ length: 60 }, (_, i) => row("Aurora" + String(i).padStart(2, "0")));
    const p = page({ mods: fifty, reply: (m, params) => ({ ok: true, off: true, changed: params.fullNames.length,
      results: params.fullNames.map(n => ok(n.slice(5))) }) });
    p.context.renderMods();
    for (const m of fifty.slice(0, 50)) p.context.modTickClick(m.FullName, true, false);
    await p.context.switchTicked(true);
    assert.strictEqual(p.sent.length, 1);
    assert.strictEqual(p.sent[0].params.fullNames.length, 50);
    assert.strictEqual(p.scans.length, 1);
    assert.strictEqual(JSON.stringify(p.scans[0]), JSON.stringify({ force: false }), "the rescan went back out to the site");
    assert.deepStrictEqual(p.toasts, ["ᚱ " + T("mods.bulk.off.done.toast", { count: 50 })]);

    const q = page({ mods: ten(), reply: () => ({ ok: true, changed: 0, results: [no("Aurora", "nameTaken", { errorParams: { taken: "A.dll.old", beside: "A.dll" } })] }) });
    q.context.renderMods();
    tick(q, "Aurora");
    await q.context.switchTicked(true);
    assert.deepStrictEqual(q.scans, [], "a batch that moved nothing scanned");
    assert.strictEqual(q.context.S.modRowStatus["Acme-Aurora"].error, T("mods.switch.reason.name_taken", { taken: "A.dll.old", beside: "A.dll" }));

    /* And the preview, with no host at all, says where switching happens and sends nothing. */
    const v = page({ mods: ten(), native: false });
    v.context.renderMods();
    tick(v, "Aurora");
    await v.context.switchTicked(true);
    assert.deepStrictEqual(v.sent, []);
    assert.deepStrictEqual(v.toasts, ["ᚱ " + T("mods.toggle.preview.toast")]);
  });

  finished = true;
  console.log(failures.length === 0
    ? "mod ticks selftest: all " + passed + " rules pass"
    : "mod ticks selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAIL");
  process.exit(failures.length === 0 ? 0 : 1);
})();
