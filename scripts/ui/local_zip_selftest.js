/* Add from link with a package zip on this PC (1.3.2, issue 32), driven for real.
 *
 * WHY THIS EXISTS. The host decides what a paste is and what goes in; the page decides which
 * words the host reads and which road the paste takes. A page that sent a path down the link
 * road would answer "not a Thunderstore link" to a host holding the right file, and a page that
 * installed on the first call would skip the question the whole feature hangs on. So the page's
 * half is driven here, through its own functions, against the one table the host's tests read.
 *
 * THE CODE UNDER TEST is the block between the LOCALZIP markers in app.js, the MODROW and
 * MODMENU blocks it shares a row with, modSearchText, and unquotePath, evaluated with the page's globals stood in for. Sentences from the
 * real en.json. No mod or author is named: the stand-in is Acme-Aurora.
 *
 *   1. Path or link: looksLikeLocalPath gives the host's answer on every row of
 *      scripts/ui/local_path_cases.json.
 *   2. The row: the Local chip by the name, the Installed version in a dashed outline with its
 *      own tooltip, the Local status pill after Off and before Unchecked, Held and Not listed,
 *      and the search box finds both words.
 *   3. The menu: Update greyed with the zip's own reason, no Reinstall, no Thunderstore swap,
 *      Check this mod there and live.
 *   4. Two calls: the first sends the path and the realm and opens the question; the yes sends
 *      the same with accept, clears a Failed pill, scans, rescans a Gale follow and offers the
 *      missing dependencies. A cancel sends nothing more.
 *   5. The question: what the zip is, where its owner came from, what it replaces (a newer copy,
 *      the same number, a step back, a folder of no known version, a switched-off copy), the
 *      hold-off line, the dependencies, every part escaped.
 *   6. Refusals: each local reason in the reader's own language with the host's values in it,
 *      the realm switch, no BepInEx opening the loader's offer, and an unknown reason falling
 *      back to the host's own English.
 *   7. Browse asks Windows' picker for a zip and puts the answer in the box; a cancel leaves
 *      the box alone, and outside the app it only says why.
 *   8. Add from link: the box has Browse and the note beside it, and a path takes the zip road
 *      before any link handling.
 *
 *     node scripts/ui/local_zip_selftest.js
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
const CASES = JSON.parse(fs.readFileSync(path.join(__dirname, "local_path_cases.json"), "utf8")).cases;

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
  return entry.lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

const FAIL = { failed: true };

function page(opts) {
  opts = opts || {};
  const sent = [], toasts = [], scans = [], modals = [], offers = [], deps = [], logs = [], gale = [];
  const context = {
    S: { mods: [], profileName: "Zip Test", state: { status: "Stopped" }, modRowStatus: opts.rowStatus || {} },
    Native: {
      available: opts.native !== false,
      call: (method, params) => { gale.push({ method, params }); return Promise.resolve({ state: null }); },
    },
    FAIL, T,
    esc: null, pad: null,
    LOC: () => "en-US",
    CFG: { mock: {} },
    GALE: opts.gale || null,
    modIsHeld: m => m.installedSource === "hexium",
    modLatestMark: () => "",
    renderRowStatus: () => "<span>status</span>",
    modDateValue: null,
    lastPathPart: p => String(p || "").split(/[\\/]/).filter(Boolean).pop() || "",
    fmtBytes: n => n + " B",
    rpc: (method, params) => {
      sent.push({ method, params: JSON.parse(JSON.stringify(params || {})) });
      const r = opts.reply ? opts.reply(method, params, sent.length) : { ok: true };
      return Promise.resolve(r);
    },
    toast: t => toasts.push(t),
    logLine: (kind, text) => logs.push(kind + " " + text),
    renderMods: () => {},
    scanMods: o => { scans.push(o); return Promise.resolve(); },
    confirmModal: (title, body, ok, onOk) => { modals.push({ title, body, ok, onOk }); },
    bepInExOfferModal: after => offers.push(after),
    modDepsDialog: (who, needs) => deps.push({ who, needs }),
    galeApply: () => true,
    renderGaleBox: () => {},
    goPage: () => {}, goSection: () => {},
    refreshCfgList: () => Promise.resolve(), loadCfg: () => Promise.resolve(),
    document: { querySelectorAll: () => [] },
  };
  vm.createContext(context);
  const code = [
    line("const esc=").replace(/^const /, "var "),
    line("const pad=").replace(/^const /, "var "),
    line("const PATH_QUOTE="),
    between("function unquotePath(", "\n/* The two boxes"),
    between("function modSearchText(", "\nfunction ") ,
    between("function modDateValue(", "\n/* Two dates, with a blank"),
    between("function galeModUrl(", "\n/* The Mods hall's own sentence"),
    between("/* CFGITEM-BEGIN", "/* CFGITEM-END */"),
    between("/* MODROW-BEGIN", "/* MODROW-END */"),
    between("/* MODMENU-BEGIN", "\n/* What one check's answer does to the row"),
    between("/* LOCALZIP-BEGIN", "/* LOCALZIP-END */"),
  ].join("\n").replace(/^const /gm, "var ").replace(/^let /gm, "var ");
  vm.runInContext(code, context, { filename: "app.js#localzip" });
  for (const name of ["looksLikeLocalPath", "localZipBrowse", "doAddLocalZip", "localZipRefused", "localZipConfirm",
                      "doInstallLocalZip", "modRowHtml", "modRowItems", "modInstalledCell", "modStatusPill", "modSearchText"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of app.js");
  return { context, sent, toasts, scans, modals, offers, deps, logs, gale };
}

const ZIP = "C:\\Users\\Zoë\\Downloads\\Acme-Aurora-1.4.0-beta.1.zip";
const ASKED = {
  NeedsLocalConsent: true, Installed: false, Replaced: false, Owner: "Acme", Name: "Aurora", Version: "1.4.0-beta.1",
  FileName: "Acme-Aurora-1.4.0-beta.1.zip", Bytes: 2048, OwnerFrom: "fileName", Dependencies: [], wouldReplace: null, source: "local",
};
const DONE = { Installed: true, Replaced: false, Owner: "Acme", Name: "Aurora", Version: "1.4.0-beta.1", source: "local", missingDependencies: [] };
const ROW = {
  ModName: "Aurora", Author: "Acme", FullName: "Acme-Aurora", InstalledVersion: "1.4.0-beta.1", LatestVersion: "1.4.0",
  thunderstoreNamespace: "Acme", thunderstoreName: "Aurora", installedSource: "local", installedUtc: "2026-10-30T10:00:00Z",
  modUpdatedUtc: null, disabled: false, layout: "managed", UpdateAvailable: false, thunderstoreNewer: false,
};
const row = extra => Object.assign({}, ROW, extra || {});
const labels = items => Array.from(items, it => it === "hr" ? "hr" : (it.section ? "[" + it.section + "]" : it.label));
const flush = () => new Promise(r => setImmediate(r));

(async function main() {
  await test("1. path or link: the page agrees with the host on every row of the shared table", () => {
    const p = page();
    assert.ok(CASES.length >= 30, "the shared table has only " + CASES.length + " rows");
    for (const c of CASES)
      assert.strictEqual(p.context.looksLikeLocalPath(c.in), c.local, JSON.stringify(c.in) + " is " + (c.local ? "" : "not ") + "a path on the host");
  });

  await test("2. the row: Local chip, dashed Installed, Local pill in its place, found by search", () => {
    const p = page();
    const html = p.context.modRowHtml(row(), false);
    assert.ok(html.includes(">" + T("mods.tag.local") + "</span>") && html.includes(p.context.esc(T("mods.tag.local.tip"))), "no Local chip by the name");
    const cell = p.context.modInstalledCell(row());
    assert.ok(cell.includes('class="modchip modver from-local"') && cell.includes(p.context.esc(T("mods.tag.installed.local.tip"))), cell);
    const rule = (CSS.match(/\.modchip\.modver\.from-local\{([^}]*)\}/) || [])[1] || "";
    assert.ok(/border-style:dashed/.test(rule), "the zip's Installed pill is not an outline of its own: " + (rule || "no rule"));
    const pill = (m, blind) => p.context.modStatusPill(m, blind);
    const local = T("mods.status.local");
    assert.ok(pill(row(), false).includes(">" + local + "<") && pill(row(), false).includes(p.context.esc(T("mods.status.local.tip"))), "no Local pill");
    assert.ok(pill(row(), true).includes(">" + local + "<"), "an unchecked index hides the Local pill");
    assert.ok(pill(row({ notListed: true }), false).includes(">" + local + "<"), "a test build reads as not listed");
    assert.ok(pill(row({ disabled: true }), false).includes(">" + T("mods.status.off") + "<"), "Off no longer comes first");
    assert.ok(!pill(row({ installedSource: "thunderstore" }), false).includes(">" + local + "<"), "a Thunderstore copy reads as Local");
    const words = p.context.modSearchText(row());
    assert.ok(words.includes(T("mods.tag.local")) && words.includes(local), "the search box cannot find a zip copy");
  });

  await test("3. the menu: Update greyed for the zip's reason, no Reinstall, no swap, Check this mod live", () => {
    const p = page();
    const items = p.context.modRowItems(row());
    const update = items.find(it => it.label === T("mods.menu.update"));
    assert.ok(update && update.disabled && update.tip === T("mods.menu.update.tip.local"), "Update is not greyed with the zip's reason");
    const l = labels(items);
    assert.ok(!l.includes(T("mods.menu.reinstall")), "a zip copy is offered Reinstall, which would put the published build over it");
    assert.ok(!l.includes(T("mods.menu.install_thunderstore")), "a zip copy is offered the Thunderstore swap");
    const check = items.find(it => it.label === T("mods.menu.check_one"));
    assert.ok(check && !check.disabled, "Check this mod is missing or greyed on a zip copy");
    assert.ok(labels(p.context.modRowItems(row({ installedSource: "thunderstore" }))).includes(T("mods.menu.reinstall")),
      "the control case lost Reinstall, so the rule above proves nothing");
  });

  await test("4. two calls: the path and the realm, the question, then the same with accept", async () => {
    const p = page({
      gale: { id: "ab12CD" },
      rowStatus: { "acme-aurora": { phase: "failed" }, "Other-Mod": { phase: "failed" } },
      reply: (m, params, n) => n === 1 ? ASKED : Object.assign({}, DONE, { missingDependencies: [{ fullName: "Acme-Needed", state: "missing" }] }),
    });
    await p.context.doAddLocalZip(ZIP);
    assert.deepStrictEqual(p.sent, [{ method: "mods.addFromUrl", params: { url: ZIP, profile: "Zip Test" } }]);
    assert.strictEqual(p.modals.length, 1, "no question was asked");
    assert.strictEqual(p.scans.length, 0, "the first call scanned as if something went in");
    p.modals[0].onOk();
    await flush(); await flush();
    assert.deepStrictEqual(p.sent[1], { method: "mods.addFromUrl", params: { url: ZIP, profile: "Zip Test", accept: true } });
    assert.strictEqual(p.sent.length, 2);
    assert.ok(p.toasts.includes("ᚨ " + T("mods.add.installed.toast", { name: "Acme-Aurora", version: "1.4.0-beta.1" })), p.toasts.join(" | "));
    assert.deepStrictEqual(Object.keys(p.context.S.modRowStatus), ["Other-Mod"], "the Failed pill a Gale walk left is still on the row");
    assert.strictEqual(p.scans.length, 1);
    assert.ok(p.gale.some(g => g.method === "gale.rescan"), "a Gale follow did not read the disk again");
    assert.strictEqual(p.deps.length, 1, "the missing dependency was not offered");
    assert.ok(p.logs.some(l => /^ok \[Local\] installed Acme-Aurora v1\.4\.0-beta\.1 from Acme-Aurora-1\.4\.0-beta\.1\.zip$/.test(l)), p.logs.join(" | "));

    const c = page({ reply: () => ASKED });
    await c.context.doAddLocalZip(ZIP);
    assert.strictEqual(c.modals.length, 1);
    await flush();
    assert.strictEqual(c.sent.length, 1, "a cancelled question sent a second call");
    assert.strictEqual(c.scans.length, 0);
  });

  await test("5. the question says what the zip is and what it replaces, every part escaped", () => {
    const p = page();
    const ask = rep => {
      p.modals.length = 0;
      p.context.localZipConfirm(Object.assign({}, ASKED, rep), () => {});
      const m = p.modals[0];
      const v = x => typeof x === "function" ? x() : x;
      return { title: v(m.title), body: v(m.body), ok: v(m.ok) };
    };
    const fresh = ask({});
    assert.strictEqual(fresh.title, T("mods.local.confirm.title"));
    assert.strictEqual(fresh.ok, T("common.button.install"));
    for (const s of [T("mods.local.confirm.note"), T("mods.local.confirm.fresh"), T("mods.local.confirm.held"),
                     T("mods.local.confirm.owner.file_name", { owner: "Acme" }), "Acme-Aurora-1.4.0-beta.1.zip"])
      assert.ok(fresh.body.includes(p.context.esc(s)), "the question does not say: " + s);
    const mod = "Acme-Aurora";
    const cases = [
      [{ installedVersion: "1.3.0", same: false, downgrade: false }, T("mods.local.replace.body", { mod, installed: "1.3.0", version: "1.4.0-beta.1" })],
      [{ installedVersion: "1.4.0-beta.1", same: true, downgrade: false }, T("mods.local.replace.same", { mod, version: "1.4.0-beta.1" })],
      [{ installedVersion: "1.5.0", same: false, downgrade: true }, T("mods.local.replace.downgrade", { mod, installed: "1.5.0", version: "1.4.0-beta.1" })],
      [{ installedVersion: null, same: false, downgrade: false }, T("mods.local.replace.unknown", { mod })],
    ];
    for (const [rep, words] of cases) {
      const q = ask({ wouldReplace: Object.assign({ source: null, disabled: false }, rep), OwnerFrom: "installed" });
      assert.strictEqual(q.title, T("mods.local.replace.title"));
      assert.strictEqual(q.ok, T("mods.local.replace.accept"));
      assert.ok(q.body.includes(p.context.esc(words)), "the replace line is not: " + words);
      assert.ok(q.body.includes(p.context.esc(T("mods.local.confirm.owner.installed", { owner: "Acme" }))), "the owner's source is not said");
      assert.ok(!q.body.includes(p.context.esc(T("mods.local.replace.off"))), "a copy that is on reads as off");
    }
    const off = ask({ wouldReplace: { installedVersion: "1.3.0", source: "hexium", disabled: true, same: false, downgrade: false } });
    assert.ok(off.body.includes(p.context.esc(T("mods.local.replace.off"))), "a switched-off copy is not said to stay off");
    const needs = ask({ Dependencies: ["Acme-Needed-1.0.0"] });
    assert.ok(needs.body.includes(p.context.esc(T("mods.local.confirm.deps"))) && needs.body.includes("Acme-Needed-1.0.0"), "the dependencies are not listed");
    const evil = ask({ FileName: "<img src=x onerror=alert(1)>.zip", Owner: "<b>", Name: "\"x\"" });
    assert.ok(!/<img|<b>/.test(evil.body), "the question is not escaped: " + evil.body);
  });

  await test("6. refusals: the reader's own language, the realm switch, the loader's offer, the English fallback", async () => {
    const said = async reply => {
      const p = page({ reply: () => reply });
      await p.context.doAddLocalZip(ZIP);
      return p;
    };
    const run = await said({ Installed: false, Reason: "local.serverRunning", ErrorParams: { name: "Zip Test" }, Error: "x" });
    assert.deepStrictEqual(run.toasts.slice(1), ["ᚦ " + T("mods.local.reason.server_running", { name: "Zip Test" })]);
    assert.strictEqual(run.modals.length, 0, "a refusal opened the question");
    const lie = await said({ Installed: false, Reason: "local.unsafeEntry", ErrorParams: { file: "a.zip", entry: "../x.dll" }, Error: "x" });
    assert.deepStrictEqual(lie.toasts.slice(1), ["ᚦ " + T("mods.local.reason.unsafe_entry", { file: "a.zip", entry: "../x.dll" })]);
    const realm = await said({ Installed: false, Reason: "realmChanged", Error: "x" });
    assert.deepStrictEqual(realm.toasts.slice(1), ["ᚦ " + T("mods.local.reason.realm_changed")]);
    const bep = await said({ Installed: false, Reason: "noBepInEx", Error: "x" });
    assert.strictEqual(bep.offers.length, 1, "no BepInEx did not offer the loader");
    const odd = await said({ Installed: false, Reason: "local.somethingNew", Error: "The host's own words." });
    assert.deepStrictEqual(odd.toasts.slice(1), ["ᚦ " + T("mods.add.failed.toast", { detail: "The host's own words." })]);
    // Every row of the zip's own table has its sentence, and none is left in the table of throws.
    const table = between("const LOCAL_ZIP_REASONS=[", "\n];");
    assert.ok(!/named:"local\./.test(between("const HOST_SENTENCES=[", "\n];")), "a zip reason is in HOST_SENTENCES, the table of throws");
    const rows = [...table.matchAll(/named:"(local\.\w+)",\s*textId:"([\w.]+)"/g)];
    assert.ok(rows.length >= 23, "only " + rows.length + " local rows");
    for (const [, named, id] of rows) assert.ok(KEYS[id], named + " points at " + id + ", which the catalog does not have");
  });

  await test("7. Browse: the picker for a zip, the answer in the box, a cancel leaves it, outside the app it says why", async () => {
    const box = { value: "old", focus() {} };
    const p = page({ reply: () => ({ path: "\"C:\\Users\\Zoë\\Downloads\\Acme-Aurora-1.4.0.zip\"" }) });
    await p.context.localZipBrowse(box);
    assert.deepStrictEqual(p.sent, [{ method: "shell.pickFile", params: { kind: "zip" } }]);
    assert.strictEqual(box.value, "C:\\Users\\Zoë\\Downloads\\Acme-Aurora-1.4.0.zip");
    const c = page({ reply: () => ({ cancelled: true }) });
    const kept = { value: "kept", focus() {} };
    await c.context.localZipBrowse(kept);
    assert.strictEqual(kept.value, "kept");
    const out = page({ native: false });
    await out.context.localZipBrowse(kept);
    assert.strictEqual(out.sent.length, 0);
    assert.deepStrictEqual(out.toasts, ["ᚨ " + T("mods.add.native_only.toast")]);
  });

  await test("8. Add from link: Browse and the note by the box, a path on the zip road before any link handling", () => {
    const flow = between("function addModFlow(", "/* LOCALZIP-BEGIN");
    assert.ok(flow.includes('id="mModBrowse"') && flow.includes('T("mods.add.browse")'), "the box has no Browse");
    assert.ok(flow.includes('T("mods.add.file_note")'), "the box has no note saying a zip works");
    const road = flow.indexOf("if(looksLikeLocalPath(url)){doAddLocalZip(url);return;}");
    assert.ok(road >= 0, "a path does not take the zip road");
    const link = flow.indexOf("doAddMod(", road);
    assert.ok(link < 0 || link > road, "the link road runs before the path check");
    assert.ok(/#mModBrowse"\)\?\.addEventListener\("click",\(\)=>localZipBrowse\(/.test(flow), "Browse is not wired to the picker");
  });

  console.log("");
  if (failures.length) {
    console.log("local zip selftest: " + failures.length + " FAILED, " + passed + " passed");
    process.exit(1);
  }
  console.log("local zip selftest: all " + passed + " rules pass");
})();
