/* The Herald per realm on the Discord hall, driven for real (1.2.16, issue 24).
 *
 * WHY THIS EXISTS. Until 1.2.16 the hall edited one post for the whole app, and that post
 * described whichever realm happened to be running. Now each realm keeps a post of its own, so
 * the hall edits the realm on screen: its switch, its webhook and thread, what its post carries,
 * its title and status line, and Publish and Remove act on its post alone. The ways this goes
 * wrong quietly are: a realm switch leaving the last realm's settings on screen (and the next
 * save writing them onto this realm), a save or a press landing on whichever realm is on screen
 * by the time the reply comes back, Publish acting on another realm's post, the post line
 * saying one realm's post is placed while showing another, and the master switch losing its
 * place over every realm.
 *
 * THE CODE UNDER TEST is the block between the HERALD-REALM markers in app.js, evaluated here
 * with the page's own globals stood in for, and the wizard's placing rule taken out of app.js.
 * The sentences come out of the real en.json, and a T() call for an id the catalog does not
 * have fails the rule it is in.
 *
 * The rules:
 *   1. The hall paints the realm on screen from that realm's own block, and a save reads the
 *      same controls back and names the realm.
 *   2. A realm switch repaints from the other realm's block, asks the host side again once, and
 *      a realm with nothing read yet shows what a new realm starts as.
 *   3. A save names the realm the hall was drawn for, even when the realm on screen moved first.
 *   4. Publish acts on this realm: it names it, switches the master and this realm on, and
 *      the post line follows this realm only. With no webhook anywhere nothing is sent.
 *   5. Remove acts on this realm alone, and a failed press changes nothing.
 *   6. The line under the realm's webhook box: the app-wide webhook, none at all, or its own.
 *   7. The master switch keeps its place: app wide, saved on every press, and over every realm.
 *   8. The wizard sets up the realm on screen and places its webhook where it belongs.
 *   9. The wiring: the repaint on a realm switch, the language redraw, the refusal rows, the
 *      catalog, and the host side's RPCs taking the realm.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/herald_realm_selftest.js
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
const BRIDGE = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"), "utf8").replace(/\r\n/g, "\n");

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
  const from = APP.indexOf("/* HERALD-REALM-BEGIN");
  const to = APP.indexOf("/* HERALD-REALM-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the HERALD-REALM markers");
  return APP.slice(from, to);
}

/* One top-level function of app.js, by its opening line, up to the brace that closes it. */
function fn(head) {
  const at = APP.indexOf(head);
  assert.ok(at >= 0, "app.js has no " + head);
  const end = APP.indexOf("\n}", at);
  return APP.slice(at, end + 2);
}

function T(id, params) {
  const entry = KEYS[id];
  if (!entry || typeof entry.lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return entry.lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

const FAIL = { failed: true };
const HOOK_A = "https://discord.com/api/webhooks/111/aaa";
const HOOK_B = "https://discord.com/api/webhooks/222/bbb";

function switchEl(on) {
  const classes = new Set(on ? ["toggle", "on"] : ["toggle"]);
  return { classList: { contains: c => classes.has(c), toggle: (c, v) => (v ? classes.add(c) : classes.delete(c)) } };
}

/* What userprefs.get hands back for two realms: Final Sunset shares through its own webhook
   with a post placed, Ashlands Test is off on the app-wide webhook. */
function prefs() {
  return {
    DiscordSharingEnabled: true,
    DiscordWebhookUrl: HOOK_A,
    HeraldRealms: [
      { Realm: "Final Sunset", On: true, WebhookUrl: HOOK_B, ThreadId: "999", ShareAddress: false,
        SharePassword: true, Title: "Ravenhold", StatusLine: "Ashlands Friday", HasPost: true },
      { Realm: "Ashlands Test", On: false, WebhookUrl: null, ThreadId: null, ShareAddress: true,
        SharePassword: false, Title: null, StatusLine: null, HasPost: false },
    ],
  };
}

function hall(opts) {
  opts = opts || {};
  const calls = [];
  const toasts = [];
  const saves = [];
  const el = {
    "#tHerald": switchEl(opts.master !== false),
    "#tHeraldRealm": switchEl(false),
    "#tHeraldAddr": switchEl(true),
    "#tHeraldPass": switchEl(false),
    "#heraldUrl": { value: opts.appUrl === undefined ? HOOK_A : opts.appUrl },
    "#heraldRealmUrl": { value: "" },
    "#heraldRealmThread": { value: "" },
    "#heraldTitle": { value: "" },
    "#heraldStatusLine": { value: "" },
    "#heraldRealmName": { textContent: "" },
    "#heraldRealmPill": { textContent: "", style: { display: "none" } },
    "#heraldRealmUrlStat": { className: "", textContent: "" },
  };
  const context = {
    S: { profileName: "Final Sunset", prefs: { ProfileName: "Final Sunset" } },
    Native: { available: opts.native !== false },
    FAIL,
    T,
    HERALD_HAS_POST: false,
    $: sel => el[sel] || null,
    setT: (id, on) => el["#" + id].classList.toggle("on", !!on),
    toast: m => toasts.push(m),
    logLine: () => {},
    heraldRenderPost: () => { context.postShown = context.HERALD_HAS_POST; },
    heraldRenderLookLine: () => { context.lookLines = (context.lookLines || 0) + 1; },
    heraldRenderModCodeLine: () => { context.codeLines = (context.codeLines || 0) + 1; },
    heraldSave: () => {
      saves.push(JSON.parse(JSON.stringify(context.heraldRealmValues())));
      return Promise.resolve(opts.saveFails ? FAIL : {});
    },
    rpc: (method, args) => {
      calls.push({ method, args: args ? JSON.parse(JSON.stringify(args)) : null });
      if (opts.rpc) return Promise.resolve(opts.rpc(method, args));
      if (method === "userprefs.get") return Promise.resolve(opts.read ? opts.read() : prefs());
      return Promise.resolve({ ok: true });
    },
  };
  vm.createContext(context);
  vm.runInContext(block(), context, { filename: "app.js#herald-realm" });
  for (const name of ["heraldRealmsFrom", "heraldRealmEntry", "heraldRealmValues", "heraldRenderRealm",
                      "heraldRenderRealmLines", "heraldRealmPost", "heraldPublish", "heraldRemove"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  const realm = () => vm.runInContext("HERALD_REALM", context);
  return { context, el, calls, toasts, saves, realm };
}

const settle = () => new Promise(res => setImmediate(res));

(async function main() {
  await test("1. the hall paints the realm on screen from its own block, and a save reads it back", async () => {
    const h = hall({});
    h.context.heraldRealmsFrom(prefs());
    h.context.heraldRenderRealm();
    assert.strictEqual(h.realm(), "Final Sunset");
    assert.strictEqual(h.el["#tHeraldRealm"].classList.contains("on"), true);
    assert.strictEqual(h.el["#heraldRealmUrl"].value, HOOK_B);
    assert.strictEqual(h.el["#heraldRealmThread"].value, "999");
    assert.strictEqual(h.el["#tHeraldAddr"].classList.contains("on"), false);
    assert.strictEqual(h.el["#tHeraldPass"].classList.contains("on"), true);
    assert.strictEqual(h.el["#heraldTitle"].value, "Ravenhold");
    assert.strictEqual(h.el["#heraldStatusLine"].value, "Ashlands Friday");
    assert.strictEqual(h.context.postShown, true, "the post line does not say this realm's post is placed");
    assert.strictEqual(h.el["#heraldRealmName"].textContent, T("herald.realm.editing", { name: "Final Sunset" }));
    assert.strictEqual(h.el["#heraldRealmPill"].textContent, T("herald.realm.editing", { name: "Final Sunset" }));
    assert.strictEqual(h.el["#heraldRealmPill"].style.display, "", "the head does not name the realm being edited");
    assert.ok(h.context.lookLines > 0 && h.context.codeLines > 0, "the look and code lines were not redrawn");

    assert.deepStrictEqual(JSON.parse(JSON.stringify(h.context.heraldRealmValues())), {
      Realm: "Final Sunset", On: true, WebhookUrl: HOOK_B, ThreadId: "999", ShareAddress: false,
      SharePassword: true, Title: "Ravenhold", StatusLine: "Ashlands Friday",
    });
  });

  await test("2. a realm switch repaints from that realm, reads again once, and a new realm starts off", async () => {
    const h = hall({});
    h.context.heraldRealmsFrom(prefs());
    h.context.heraldRenderRealm();
    await settle();
    const readsBefore = h.calls.filter(c => c.method === "userprefs.get").length;

    h.context.S.profileName = "Ashlands Test";
    h.context.heraldRenderRealm();
    assert.strictEqual(h.realm(), "Ashlands Test");
    assert.strictEqual(h.el["#tHeraldRealm"].classList.contains("on"), false);
    assert.strictEqual(h.el["#heraldRealmUrl"].value, "", "the last realm's webhook was left on screen");
    assert.strictEqual(h.el["#heraldRealmThread"].value, "");
    assert.strictEqual(h.el["#heraldTitle"].value, "", "the last realm's title was left on screen");
    assert.strictEqual(h.el["#heraldStatusLine"].value, "");
    assert.strictEqual(h.el["#tHeraldAddr"].classList.contains("on"), true);
    assert.strictEqual(h.el["#tHeraldPass"].classList.contains("on"), false, "the last realm's password switch was left on");
    assert.strictEqual(h.context.postShown, false, "the last realm's post was shown as this realm's");
    await settle();
    assert.strictEqual(h.calls.filter(c => c.method === "userprefs.get").length, readsBefore + 1,
      "a realm switch does not read the host side again, exactly once");

    /* Repainting the same realm again asks nothing. */
    h.context.heraldRenderRealm();
    await settle();
    assert.strictEqual(h.calls.filter(c => c.method === "userprefs.get").length, readsBefore + 1);

    /* A realm made since the last read: what a new realm starts as. */
    const fresh = hall({ native: false });
    fresh.context.S.profileName = "Brand New";
    fresh.context.heraldRenderRealm();
    assert.strictEqual(fresh.el["#tHeraldRealm"].classList.contains("on"), false);
    assert.strictEqual(fresh.el["#tHeraldAddr"].classList.contains("on"), true);
    assert.strictEqual(fresh.el["#heraldRealmUrl"].value, "");
    assert.strictEqual(fresh.calls.length, 0, "the browser preview asked the host side");
  });

  await test("3. a save names the realm the hall was drawn for, not whichever is on screen now", async () => {
    const h = hall({ native: false });
    h.context.heraldRealmsFrom(prefs());
    h.context.heraldRenderRealm();
    h.context.S.profileName = "Ashlands Test"; /* the switch lands before the repaint */
    assert.strictEqual(h.context.heraldRealmValues().Realm, "Final Sunset");

    /* A saved block is kept by name, so switching away and back shows what was saved. */
    h.el["#heraldTitle"].value = "  A new title ";
    h.context.heraldRealmRemember(h.context.heraldRealmValues());
    h.context.heraldRenderRealm(); /* now Ashlands Test */
    h.context.S.profileName = "Final Sunset";
    h.context.heraldRenderRealm();
    assert.strictEqual(h.el["#heraldTitle"].value, "A new title");
  });

  await test("4. Publish acts on this realm, switches it and the master on, and its post line follows it", async () => {
    const h = hall({ master: false });
    h.context.heraldRealmsFrom(prefs());
    h.context.S.profileName = "Ashlands Test";
    h.context.heraldRenderRealm();
    await settle();
    h.calls.length = 0;

    assert.strictEqual(await h.context.heraldPublish(), true);
    const pub = h.calls.filter(c => c.method === "discord.publish");
    assert.strictEqual(pub.length, 1);
    assert.deepStrictEqual(pub[0].args, { profile: "Ashlands Test" }, "Publish did not name this realm");
    assert.strictEqual(h.el["#tHerald"].classList.contains("on"), true, "Publish left the master off");
    assert.strictEqual(h.el["#tHeraldRealm"].classList.contains("on"), true, "Publish left this realm off");
    assert.strictEqual(h.saves.length, 1, "Publish did not save first");
    assert.strictEqual(h.saves[0].On, true);
    assert.strictEqual(h.context.postShown, true);
    assert.strictEqual(h.toasts[0], "ᚺ " + T("herald.publish.done.toast"));

    /* The other realm's post line is its own. */
    h.context.S.profileName = "Final Sunset";
    h.context.heraldRenderRealm();
    assert.strictEqual(h.context.postShown, true);
    h.context.heraldRealmPost("Final Sunset", false);
    h.context.S.profileName = "Ashlands Test";
    h.context.heraldRenderRealm();
    assert.strictEqual(h.context.postShown, true, "removing one realm's post took the other's line down");

    /* No webhook anywhere: nothing is sent and the host is told. */
    const none = hall({ appUrl: "" });
    none.context.S.profileName = "Nowhere";
    none.context.heraldRenderRealm();
    await settle();
    none.calls.length = 0;
    assert.strictEqual(await none.context.heraldPublish(), false);
    assert.strictEqual(none.calls.length, 0, "Publish with no webhook asked the host side");
    assert.strictEqual(none.saves.length, 0);
    assert.strictEqual(none.toasts[0], "ᚦ " + T("herald.publish.no_url.toast"));

    /* A realm's own webhook is enough, with no app-wide one. */
    const own = hall({ appUrl: "" });
    own.context.heraldRealmsFrom(prefs());
    own.context.heraldRenderRealm();
    await settle();
    assert.strictEqual(await own.context.heraldPublish(), true);

    /* A refusal from Discord says so and leaves the line as it was. */
    const refused = hall({ rpc: m => (m === "discord.publish" ? { ok: false, error: "Discord rejected the post (403)." } : prefs()) });
    refused.context.S.profileName = "Ashlands Test";
    refused.context.heraldRealmsFrom(prefs());
    refused.context.heraldRenderRealm();
    await settle();
    assert.strictEqual(await refused.context.heraldPublish(), false);
    assert.strictEqual(refused.context.postShown, false);
    assert.strictEqual(refused.toasts[0], "ᚦ Discord rejected the post (403).");

    /* The save that switches the realm on failed: nothing is published after it. */
    const unsaved = hall({ saveFails: true });
    unsaved.context.S.profileName = "Ashlands Test";
    unsaved.context.heraldRealmsFrom(prefs());
    unsaved.context.heraldRenderRealm();
    await settle();
    unsaved.calls.length = 0;
    assert.strictEqual(await unsaved.context.heraldPublish(), false);
    assert.strictEqual(unsaved.saves.length, 1, "Publish did not try to save first");
    assert.strictEqual(unsaved.calls.filter(c => c.method === "discord.publish").length, 0,
      "Publish went on to the host side after the save that switches the realm on failed");
    assert.strictEqual(unsaved.context.postShown, false);
  });

  await test("5. Remove acts on this realm alone", async () => {
    const h = hall({});
    h.context.heraldRealmsFrom(prefs());
    h.context.heraldRenderRealm();
    await settle();
    h.calls.length = 0;
    assert.strictEqual(await h.context.heraldRemove(), true);
    assert.deepStrictEqual(h.calls, [{ method: "discord.remove", args: { profile: "Final Sunset" } }]);
    assert.strictEqual(h.context.postShown, false);
    assert.strictEqual(h.context.heraldRealmEntry("Final Sunset").HasPost, false);
    assert.strictEqual(h.context.heraldRealmEntry("Ashlands Test").On, false, "Remove touched the other realm");

    const failed = hall({ rpc: m => (m === "discord.remove" ? FAIL : prefs()) });
    failed.context.heraldRealmsFrom(prefs());
    failed.context.heraldRenderRealm();
    await settle();
    assert.strictEqual(await failed.context.heraldRemove(), false);
    assert.strictEqual(failed.context.postShown, true, "a failed Remove took the post line down");
  });

  await test("6. the line under the realm's webhook box", async () => {
    const h = hall({ native: false });
    h.context.S.profileName = "Ashlands Test";
    h.context.heraldRealmsFrom(prefs());
    h.context.heraldRenderRealm();
    assert.strictEqual(h.el["#heraldRealmUrlStat"].textContent, T("herald.realm.hook.stat.app"));
    h.el["#heraldUrl"].value = "";
    h.context.heraldRenderRealmLines();
    assert.strictEqual(h.el["#heraldRealmUrlStat"].textContent, T("herald.realm.hook.stat.none"));
    h.el["#heraldRealmUrl"].value = HOOK_B;
    h.context.heraldRenderRealmLines();
    assert.strictEqual(h.el["#heraldRealmUrlStat"].textContent, "", "a realm with its own webhook is told about the app-wide one");
  });

  await test("7. the master switch keeps its place over every realm", async () => {
    const appCard = HTML.slice(HTML.indexOf('<div class="card b-herald">'), HTML.indexOf('<div class="card b-heraldshare">'));
    const realmCard = HTML.slice(HTML.indexOf('<div class="card b-heraldshare">'), HTML.indexOf("<!-- THE POST -->"));
    assert.ok(appCard.includes('id="tHerald"') && !realmCard.includes('id="tHerald"'), "the master switch left the app-wide card");
    assert.ok(realmCard.includes('id="tHeraldRealm"') && !appCard.includes('id="tHeraldRealm"'), "the realm's switch is not in the realm's card");
    assert.ok(/data-i18n="herald\.sharing\.toggle"/.test(appCard));
    assert.ok(/data-i18n="herald\.realm\.toggle"/.test(realmCard));
    for (const id of ["heraldUrl", "heraldThread"]) assert.ok(appCard.includes('id="' + id + '"'), id + " left the app-wide card");
    for (const id of ["heraldRealmUrl", "heraldRealmThread", "tHeraldAddr", "tHeraldPass", "heraldTitle", "heraldStatusLine", "heraldModCode"])
      assert.ok(realmCard.includes('id="' + id + '"'), id + " is not in the realm's card");
    assert.ok(/id="heraldRealmUrl"[^>]*data-i18n-placeholder="herald\.realm\.webhook\.placeholder"/.test(realmCard));
    assert.strictEqual(KEYS["herald.realm.webhook.placeholder"].lore, "blank = the app-wide webhook");

    /* Every press of either switch saves, and the save carries the master and the realm's block. */
    assert.ok(/\["tHerald","tHeraldRealm","tHeraldAddr","tHeraldPass","tHeraldLink"\]\.forEach\(id=>\s*\$\("#"\+id\)\.addEventListener\("click",\(\)=>heraldSave\(\)\)\);/.test(APP),
      "a switch on the hall does not save");
    const save = fn("function heraldSave(extra){");
    assert.ok(save.includes('DiscordSharingEnabled:swOn("tHerald")'), "the save does not carry the master switch");
    assert.ok(save.includes("Herald:realm"), "the save does not carry the realm's block");
    assert.ok(!/DiscordShareAddress|DiscordSharePassword/.test(save), "the share switches still go out as app-wide keys");

    /* And the host side holds it over every realm: a realm shares only with the master on. */
    const rules = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Tools", "HeraldRealms.cs"), "utf8");
    assert.ok(/app != null && app\.DiscordSharingEnabled\s*&& realm != null && realm\.HeraldOn/.test(rules),
      "HeraldRealms.Shares no longer asks the master first");
  });

  await test("8. the wizard sets up the realm on screen and places its webhook", async () => {
    const ctx = vm.createContext({});
    vm.runInContext(fn("function heraldWizPlace(url,thread,appUrl,appThread){"), ctx);
    const place = (...a) => JSON.parse(JSON.stringify(ctx.heraldWizPlace(...a)));
    /* The first webhook set up becomes the app-wide one. */
    assert.deepStrictEqual(place(HOOK_A, "5", "", ""), { appUrl: HOOK_A, appThread: "5", url: "", thread: "" });
    /* The app-wide one again: this realm stays on it, with a thread of its own only when it differs. */
    assert.deepStrictEqual(place(HOOK_A, "5", HOOK_A, "5"), { appUrl: HOOK_A, appThread: "5", url: "", thread: "" });
    assert.deepStrictEqual(place(HOOK_A, "6", HOOK_A, "5"), { appUrl: HOOK_A, appThread: "5", url: "", thread: "6" });
    /* Any other webhook is this realm's own, and the app-wide pair is left alone. */
    assert.deepStrictEqual(place(" " + HOOK_B + " ", "7", HOOK_A, "5"), { appUrl: HOOK_A, appThread: "5", url: HOOK_B, thread: "7" });

    const wiz = fn("async function heraldWizFinish(){");
    assert.ok(wiz.includes('setT("tHeraldRealm",true);'), "the wizard does not switch the realm on");
    assert.ok(wiz.includes('rpc("discord.publish",{profile:realm})'), "the wizard's publish does not name the realm");
    assert.ok(wiz.includes("heraldWizPlace("), "the wizard does not place its webhook");
    assert.ok(wiz.indexOf("heraldRealmKey(HWIZ.realm)!==heraldRealmKey(HERALD_REALM)") > 0
      && wiz.indexOf("heraldRealmKey(HWIZ.realm)!==heraldRealmKey(HERALD_REALM)") < wiz.indexOf("await heraldSave();"),
      "the wizard can set up a realm that is no longer on screen");
    assert.ok(fn("function heraldWizard(){").includes("realm:HERALD_REALM||heraldRealmOnScreen()"), "the wizard does not remember its realm");
    assert.ok(APP.includes('T("herald.wiz.sum.realm")'), "the wizard's summary does not name the realm");
  });

  await test("9. the wiring: realm switch, language redraw, refusals, catalog and the host side", async () => {
    const all = fn("function renderAllFromPrefs(){");
    assert.ok(all.includes("heraldRenderRealm()"), "a realm switch does not repaint the realm's post settings");
    const apply = fn("function heraldApply(up){");
    assert.ok(apply.includes("heraldRealmsFrom(up)") && apply.includes("heraldRenderRealm()"), "reading the preferences does not paint the realm");
    assert.ok(!/DiscordShareAddress|DiscordSharePassword/.test(apply), "the share switches are still painted from app-wide keys");
    assert.ok((APP.match(/try\{heraldRenderRealmLines\(\);\}catch\(_\)\{\}/g) || []).length >= 3,
      "a language switch does not redraw the realm's lines");
    assert.ok(/\$\("#heraldPublish"\)\.addEventListener\("click",\(\)=>\{heraldPublish\(\);\}\);/.test(APP));
    assert.ok(/\$\("#heraldRemove"\)\.addEventListener\("click",\(\)=>\{heraldRemove\(\);\}\);/.test(APP));

    const table = APP.slice(APP.indexOf("const HOST_SENTENCES=["), APP.indexOf("];", APP.indexOf("const HOST_SENTENCES=[")));
    for (const [named, textId] of [["userprefs.save.heraldNoRealm", "herald.realm.gone.save"],
                                   ["discord.publish.noSuchRealm", "herald.realm.gone.send"],
                                   ["discord.remove.noSuchRealm", "herald.realm.gone.send"],
                                   ["discord.publish.off", "herald.realm.off.send"]]) {
      assert.ok(new RegExp('\\{named:"' + named.replace(/\./g, "\\.") + '",\\s*textId:"' + textId.replace(/\./g, "\\.") + '"\\}').test(table),
        named + " has no sentence");
      assert.ok(BRIDGE.includes('"' + named + '"'), "the host side never throws " + named);
    }
    assert.ok(BRIDGE.includes('"' + KEYS["herald.realm.gone.save"].lore + '"'), "the host side's English drifted from herald.realm.gone.save");
    assert.ok(BRIDGE.includes('"' + KEYS["herald.realm.gone.send"].lore + '"'), "the host side's English drifted from herald.realm.gone.send");
    assert.ok(BRIDGE.includes('"' + KEYS["herald.realm.off.send"].lore + '"'), "the host side's English drifted from herald.realm.off.send");

    for (const id of ["herald.realm.head", "herald.realm.editing", "herald.realm.toggle", "herald.realm.toggle.title",
                      "herald.realm.webhook", "herald.realm.webhook.title", "herald.realm.webhook.placeholder",
                      "herald.realm.thread", "herald.realm.thread.placeholder", "herald.realm.hook.stat.app",
                      "herald.realm.hook.stat.none", "herald.sharing.app.note", "herald.look.preview.realm_off",
                      "herald.modcode.preview.realm_off", "herald.wiz.sum.realm", "herald.wiz.realm_moved.toast"])
      assert.ok(KEYS[id] && typeof KEYS[id].lore === "string", "the catalog has no " + id);
    /* A sentence that says realm says server in the plain register. */
    for (const [id, entry] of Object.entries(KEYS))
      if (id.startsWith("herald.realm.") && /\brealm\b/i.test(entry.lore))
        assert.ok(typeof entry.plain === "string" && !/\brealm\b/i.test(entry.plain), id + " has no plain wording");

    /* The host side: the two presses take the realm, the read hands each realm's block back. */
    const handler = name => {
      const at = BRIDGE.indexOf('RegisterRpc("' + name + '"');
      assert.ok(at > 0, "the bridge no longer registers " + name);
      return BRIDGE.slice(at, BRIDGE.indexOf("RegisterRpc(", at + 12));
    };
    assert.ok(handler("discord.publish").includes("DiscordStatus.PublishNowAsync(realm)"));
    assert.ok(handler("discord.publish").includes("HeraldRealmOf(p, publishing: true)"));
    assert.ok(handler("discord.remove").includes("DiscordStatus.RemoveStatusMessageAsync(realm)"));
    assert.ok(handler("discord.remove").includes("HeraldRealmOf(p, publishing: false)"));
    assert.ok(/var realm = p\?\.Value<string>\("profile"\)/.test(BRIDGE), "the presses do not read the realm the page names");
    assert.ok(BRIDGE.includes("HeraldRealms = BuildHeraldRealmsDto(prefs),"), "userprefs.get does not hand back each realm's block");
    for (const key of ["Realm = r.ProfileName", "On = r.HeraldOn", "WebhookUrl = r.HeraldWebhookUrl", "ThreadId = r.HeraldThreadId",
                       "ShareAddress = r.HeraldShareAddress", "SharePassword = r.HeraldSharePassword", "HasPost ="])
      assert.ok(BRIDGE.includes(key), "the realm's block does not carry " + key);
  });

  if (failures.length) {
    console.log("herald realm selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAIL");
    process.exit(1);
  }
  console.log("herald realm selftest: all " + passed + " rules pass");
})();
