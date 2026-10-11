/* The Mod folder button in the Mods hall head, driven for real.
 *
 * WHY THIS EXISTS. The button opens this realm's BepInEx plugins folder in Explorer, and the
 * only thing it can get wrong is WHEN it is offered. Offered on a realm with no plugins folder
 * it is a press that answers "Folder not found", which is what a broken button looks like.
 * Offered before the host side has said where the folder is, it opens whatever the last realm
 * left behind. So the gate is the feature, and the gate is what this drives.
 *
 * THE CODE UNDER TEST is the block between the MODFOLDER markers in app.js, evaluated here
 * with the page's own globals stood in for. A copy of those functions in this file would pass
 * forever while the page did something else. The sentences come out of the real en.json.
 *
 * The rules:
 *   1. A mods.count reply naming a folder for the realm on screen enables the button, puts the
 *      path in its tooltip, and a press sends shell.open with the keyword "plugins" and
 *      nothing else (never the path: the host side only takes closed keywords).
 *   2. A reply with no folder (BepInEx not installed) leaves it disabled with the sentence
 *      that says so, and a press sends nothing.
 *   3. Before any reply has landed it is disabled with the "looking" sentence and sends nothing.
 *   4. A reply for a realm the host has left is dropped and changes nothing.
 *   5. In the browser preview the press says "preview only" and sends nothing.
 *   6. A refused press asks the disk again, so the button catches up with a folder that went.
 *   7. The wiring around the block: the button is in the Mods head, the click is bound to the
 *      block's handler, renderMods paints it, refreshModCount feeds it, a realm switch forgets
 *      the old folder, and walking into the hall asks again.
 *   8. The host side: shell.open knows "plugins" and mods.count answers with folder and profile.
 *   9. A lookup that failed says so and the press asks again (never opens), a failure for a
 *      realm the host has left is dropped, and a folder already known is kept.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/modfolder_selftest.js
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

function test(name, body) {
  try {
    body();
    passed++;
    console.log("  ok   " + name);
  } catch (problem) {
    failures.push(name);
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}

async function testAsync(name, body) {
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
  const from = APP.indexOf("/* MODFOLDER-BEGIN");
  const to = APP.indexOf("/* MODFOLDER-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the MODFOLDER markers");
  return APP.slice(from, to);
}

/* The catalog's own sentence with its slots filled, so a missing id fails loudly here. */
function T(id, params) {
  const entry = KEYS[id];
  if (!entry || typeof entry.lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return entry.lore.replace(/\\u([0-9a-fA-F]{4})/g, (_, h) => String.fromCharCode(parseInt(h, 16)))
    .replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

const FAIL = { failed: true };

/* A fresh page: the real block, a button, and a record of everything it sends. */
function page(opts) {
  const sent = [];
  const toasts = [];
  let recounts = 0;
  const button = { textContent: "", disabled: true, title: "" };
  const context = {
    S: { profileName: "Final Sunset", modsFolder: undefined },
    Native: { available: opts.native !== false },
    FAIL,
    T,
    $: sel => (sel === "#modFolderBtn" ? button : null),
    rpc: (method, params) => { sent.push({ method, params }); return Promise.resolve(opts.reply === undefined ? true : opts.reply); },
    toast: line => toasts.push(line),
    refreshModCount: () => { recounts++; },
  };
  context.isActiveProfile = p => p == null || !context.S.profileName
    || String(p).toLowerCase() === String(context.S.profileName).toLowerCase();
  vm.createContext(context);
  vm.runInContext(block(), context, { filename: "app.js#modfolder" });
  for (const name of ["modFolderFromCount", "modFolderLookupFailed", "modFolderGate", "renderModFolderBtn", "modFolderClick"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  return { context, button, sent, toasts, recounts: () => recounts };
}

const PLUGINS = "D:\\SteamLibrary\\steamapps\\common\\Valheim dedicated server\\BepInEx\\plugins";

(async function main() {
  await testAsync("1. a folder on disk enables the button and a press sends shell.open plugins", async () => {
    const p = page({});
    assert.strictEqual(p.context.modFolderFromCount({ count: 12, folder: PLUGINS, profile: "Final Sunset" }), true);
    p.context.renderModFolderBtn();
    assert.strictEqual(p.button.disabled, false, "the button stayed disabled with a folder on disk");
    assert.ok(p.button.title.includes(PLUGINS), "the tooltip does not name the folder: " + p.button.title);
    await p.context.modFolderClick();
    assert.strictEqual(p.sent.length, 1, "expected one call, got " + JSON.stringify(p.sent));
    assert.strictEqual(p.sent[0].method, "shell.open");
    assert.deepStrictEqual(JSON.parse(JSON.stringify(p.sent[0].params)), { target: "plugins" },
      "the press must send the closed keyword and never a path");
    assert.strictEqual(p.recounts(), 0, "a press that worked asked the disk again for nothing");
  });

  await testAsync("2. no plugins folder: disabled, says why, and a press sends nothing", async () => {
    const p = page({});
    p.context.modFolderFromCount({ count: null, folder: null, profile: "Final Sunset" });
    p.context.renderModFolderBtn();
    assert.strictEqual(p.button.disabled, true);
    assert.strictEqual(p.button.title, T("mods.folder.missing.title"));
    assert.ok(/BepInEx/.test(p.button.title), "the sentence does not say what is missing");
    await p.context.modFolderClick();
    assert.strictEqual(p.sent.length, 0, "a disabled button sent " + JSON.stringify(p.sent));
    /* An empty string is not a folder either. */
    p.context.modFolderFromCount({ count: null, folder: "   ", profile: "Final Sunset" });
    assert.strictEqual(p.context.modFolderGate().enabled, false, "a blank folder enabled the button");
  });

  await testAsync("3. before the host side has answered: disabled, looking, sends nothing", async () => {
    const p = page({});
    p.context.renderModFolderBtn();
    assert.strictEqual(p.button.disabled, true);
    assert.strictEqual(p.button.title, T("mods.folder.checking.title"));
    await p.context.modFolderClick();
    assert.strictEqual(p.sent.length, 0);
  });

  await testAsync("4. a reply for a realm the host has left changes nothing", async () => {
    const p = page({});
    assert.strictEqual(p.context.modFolderFromCount({ count: 3, folder: PLUGINS, profile: "Old Realm" }), false);
    assert.strictEqual(p.context.S.modsFolder, undefined, "the old realm's folder was taken");
    p.context.renderModFolderBtn();
    assert.strictEqual(p.button.disabled, true);
  });

  await testAsync("5. the browser preview says preview only and sends nothing", async () => {
    const p = page({ native: false });
    p.context.renderModFolderBtn();
    assert.strictEqual(p.button.disabled, false, "the preview should show the button as it stands in the app");
    await p.context.modFolderClick();
    assert.strictEqual(p.sent.length, 0);
    assert.deepStrictEqual(p.toasts, ["\u16c3 " + T("mods.folder.preview.toast")]);
    assert.strictEqual(KEYS["mods.folder.preview.toast"].mark, "\u16c3", "the toast's rune is not on its catalog entry");
  });

  await testAsync("6. a refused press asks the disk again", async () => {
    const p = page({ reply: FAIL });
    p.context.modFolderFromCount({ count: 1, folder: PLUGINS, profile: "Final Sunset" });
    await p.context.modFolderClick();
    assert.strictEqual(p.sent.length, 1);
    assert.strictEqual(p.recounts(), 1, "the button was left offering a folder the host side just refused");
  });

  test("7. the wiring around the block", () => {
    const head = HTML.slice(HTML.indexOf('id="page-mods"'), HTML.indexOf('id="modUpdProg"'));
    assert.ok(/<button[^>]*id="modFolderBtn"[^>]*data-i18n="mods\.folder\.label"/.test(head),
      "the Mods head has no #modFolderBtn carrying its catalog label");
    assert.ok(/\$\("#modFolderBtn"\)\??\.addEventListener\("click",modFolderClick\)/.test(APP),
      "the button's click is not bound to modFolderClick");
    const renderMods = APP.slice(APP.indexOf("function renderMods("), APP.indexOf("\n}", APP.indexOf("function renderMods(")));
    assert.ok(renderMods.includes("renderModFolderBtn()"), "renderMods never paints the button");
    const recount = APP.slice(APP.indexOf("async function refreshModCount("), APP.indexOf("\n}", APP.indexOf("async function refreshModCount(")));
    assert.ok(recount.includes("modFolderFromCount(r)"), "refreshModCount does not feed the button");
    assert.ok(/S\.modsFolder=undefined;[^\n]*\n\s*refreshModCount\(\);/.test(APP),
      "a realm switch does not forget the previous realm's folder before asking again");
    assert.ok(/S\.modsFolder=undefined;try\{renderModFolderBtn\(\);\}catch\(_\)\{\}[^\n]*\n\s*refreshModCount\(\);/.test(APP),
      "a realm switch leaves the old realm's folder in the button's tooltip until the next paint");
    assert.ok(/if\(r===FAIL\|\|!r\)\{try\{modFolderLookupFailed\(asked\);\}catch\(_\)\{\}return;\}/.test(recount),
      "a failed mods.count leaves the button looking forever");
    assert.ok(/if\(name==="mods"\) refreshModCount\(\);/.test(APP), "walking into the Mods hall does not ask again");
  });

  test("8. the host side knows the keyword and answers with the folder", () => {
    assert.ok(/"plugins"\s*=>\s*GetPluginsDirectory\(\)/.test(BRIDGE), "shell.open has no plugins target");
    const count = BRIDGE.slice(BRIDGE.indexOf('RegisterRpc("mods.count"'), BRIDGE.indexOf('RegisterRpc("mods.scan"'));
    assert.ok(/folder\s*=\s*pluginsDir/.test(count), "mods.count does not name the folder it found");
    assert.ok(/folder\s*=\s*\(string\)null/.test(count), "mods.count does not answer null for a missing folder");
    assert.ok(/profile\s*\}/.test(count), "mods.count does not say which realm it read");
  });

  await testAsync("9. a failed lookup says so and the press looks again", async () => {
    const p = page({});
    assert.strictEqual(p.context.modFolderLookupFailed("Final Sunset"), true);
    assert.strictEqual(p.button.disabled, false, "a failed lookup left no way to ask again");
    assert.strictEqual(p.button.title, T("mods.folder.failed.title"));
    await p.context.modFolderClick();
    assert.strictEqual(p.sent.length, 0, "a press after a failed lookup opened something: " + JSON.stringify(p.sent));
    assert.strictEqual(p.recounts(), 1, "the press did not ask the disk again");
    assert.strictEqual(p.context.S.modsFolder, undefined, "the press did not go back to looking");
    assert.strictEqual(p.button.disabled, true);
    assert.strictEqual(p.button.title, T("mods.folder.checking.title"));
    /* A failure for a realm the host has left changes nothing. */
    const q = page({});
    assert.strictEqual(q.context.modFolderLookupFailed("Old Realm"), false);
    assert.strictEqual(q.context.S.modsFolder, undefined);
    /* A folder already known survives one missed answer. */
    const k = page({});
    k.context.modFolderFromCount({ count: 2, folder: PLUGINS, profile: "Final Sunset" });
    k.context.modFolderLookupFailed("Final Sunset");
    assert.strictEqual(k.context.S.modsFolder, PLUGINS, "one failed answer wiped a folder that was known");
    assert.strictEqual(k.button.disabled, false);
  });

  for (const id of ["mods.folder.label", "mods.folder.title", "mods.folder.checking.title", "mods.folder.failed.title", "mods.folder.missing.title", "mods.folder.preview.toast"]) {
    const lore = KEYS[id] && KEYS[id].lore;
    if (typeof lore !== "string" || /[\u2014\u2013]/.test(lore)) { failures.push("catalog " + id); console.log("  FAIL catalog " + id); }
  }

  console.log(failures.length ? "modfolder selftest: " + failures.length + " FAILED, " + passed + " passed"
    : "modfolder selftest: all " + passed + " rules pass");
  process.exit(failures.length ? 1 : 0);
})();
