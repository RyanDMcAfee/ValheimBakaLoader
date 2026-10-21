/* The Herald per realm on the Discord screen, driven for real (1.2.16, issue 24; one card
 * since 1.2.17).
 *
 * WHY THIS EXISTS. Until 1.2.16 the screen edited one post for the whole app, and that post
 * described whichever realm happened to be running. Now each realm keeps a post of its own, so
 * the screen edits the realm on screen, and since 1.2.17 nothing else: its switch, the webhook
 * it posts through (by the name Discord gave it), what its post carries, its name, picture,
 * title and status line, and Publish and Remove act on its post alone. The ways this goes
 * wrong quietly are: a realm switch leaving the last realm's settings on screen (and the next
 * save writing them onto this realm), a save or a press landing on whichever realm is on screen
 * by the time the reply comes back, Publish acting on another realm's post, the post line
 * saying one realm's post is placed while showing another, and an app-wide switch or webhook
 * creeping back onto a screen that is about one realm.
 *
 * THE CODE UNDER TEST is the block between the HERALD-REALM markers in app.js, evaluated here
 * with the page's own globals stood in for (the tick helpers and the look rules are taken out
 * of app.js itself), and the wizard's placing rule taken out of app.js. The sentences come out
 * of the real en.json, and a T() call for an id the catalog does not have fails the rule it is in.
 *
 * The rules:
 *   1. The screen paints the realm on screen from that realm's own block, and a save reads the
 *      same controls back and names the realm.
 *   2. A realm switch repaints from the other realm's block, asks the host side again once, and
 *      a realm with nothing read yet shows what a new realm starts as.
 *   3. A save names the realm the screen was drawn for, even when the realm on screen moved first.
 *   4. Publish acts on this realm: it names it, switches this realm on, and the post line
 *      follows this realm only. With no webhook the setup opens and nothing is sent.
 *   5. Remove acts on this realm alone, and a failed press changes nothing.
 *   6. The webhook line: the webhook's own name, none yet, or a name asked of Discord once.
 *   7. No app-wide switch and no webhook box on the screen; the host side reads the retired
 *      master as on once the realms stand alone.
 *   8. The wizard sets up the realm on screen: its own link, or the same channel as another.
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
function tickEl(on) { return { checked: !!on }; }

/* What userprefs.get hands back for two realms: Final Sunset shares through its own webhook
   with a post placed, Ashlands Test is off with no webhook yet. */
function prefs() {
  return {
    HeraldRealms: [
      { Realm: "Final Sunset", On: true, WebhookUrl: HOOK_B, WebhookName: "Longhouse", ThreadId: "999", ShareAddress: false,
        SharePassword: true, Title: "Ravenhold", StatusLine: "Ashlands Friday", BotName: "Raven",
        AvatarUrl: "https://i.imgur.com/raven.png", HasPost: true },
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
    "#tHeraldRealm": switchEl(false),
    "#tHeraldAddr": tickEl(true),
    "#tHeraldPass": tickEl(false),
    "#heraldTitle": { value: "" },
    "#heraldStatusLine": { value: "" },
    "#heraldName": { value: "" },
    "#heraldAvatar": { value: "" },
    "#heraldRealmName": { textContent: "" },
    "#heraldOnLabel": { textContent: "" },
    "#heraldHookLine": { textContent: "" },
    "#heraldHookChange": { textContent: "" },
    "#heraldRealmPill": { textContent: "", style: { display: "none" } },
  };
  const context = {
    S: { profileName: "Final Sunset", prefs: { ProfileName: "Final Sunset" } },
    Native: { available: opts.native !== false },
    FAIL,
    T,
    URL,
    HERALD_HAS_POST: false,
    HERALD_LOOK_REFUSED: "stale",
    $: sel => el[sel] || null,
    setT: (id, on) => el["#" + id].classList.toggle("on", !!on),
    toast: m => toasts.push(m),
    logLine: () => {},
    heraldRenderPost: () => { context.postShown = context.HERALD_HAS_POST; },
    heraldRenderLookLine: () => { context.lookLines = (context.lookLines || 0) + 1; },
    heraldRenderModCodeLine: () => { context.codeLines = (context.codeLines || 0) + 1; },
    heraldRenderEvents: () => {},
    heraldRenderWarns: () => {},
    heraldPreviewRefresh: () => {},
    heraldWizard: () => { context.wizards = (context.wizards || 0) + 1; },
    heraldSave: (extra, realmExtra) => {
      saves.push(JSON.parse(JSON.stringify(Object.assign(context.heraldRealmValues(), realmExtra || {}))));
      return Promise.resolve(opts.saveFails ? FAIL : {});
    },
    rpc: (method, args) => {
      calls.push({ method, args: args ? JSON.parse(JSON.stringify(args)) : null });
      if (opts.rpc) return Promise.resolve(opts.rpc(method, args));
      if (method === "userprefs.get") return Promise.resolve(opts.read ? opts.read() : prefs());
      if (method === "discord.validate") return Promise.resolve({ ok: true, name: "Mead Hall" });
      return Promise.resolve({ ok: true });
    },
  };
  vm.createContext(context);
  /* The page's one show/hide helper (REVEAL), as the page has it: a stand-in element has no layout, so it is set at once. */
  vm.runInContext(APP.slice(APP.indexOf("/* REVEAL-BEGIN"), APP.indexOf("/* REVEAL-END */")), context, { filename: "app.js#reveal" });
  /* The page's own helpers the block leans on, out of app.js rather than stood in for. */
  for (const head of ["function heraldTick(id,on){", "function heraldTicked(id){", "function heraldDefined(o){",
                      "function heraldLookAvatarProblem(v){", "function heraldLookNameProblem(v){"])
    vm.runInContext(fn(head), context);
  vm.runInContext('const HERALD_LOOK_IMAGE_HOSTS=["raw.githubusercontent.com","cdn.discordapp.com","i.imgur.com"];' +
    "const HERALD_LOOK_IMAGE_EXT=/\\.(png|jpe?g|gif|webp)$/i;const HERALD_LOOK_NAME_MAX=80;", context);
  vm.runInContext(block(), context, { filename: "app.js#herald-realm" });
  for (const name of ["heraldRealmsFrom", "heraldRealmEntry", "heraldRealmValues", "heraldRenderRealm",
                      "heraldRenderRealmLines", "heraldRealmPost", "heraldPublish", "heraldRemove", "heraldAskHookName"])
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
    assert.strictEqual(h.el["#tHeraldAddr"].checked, false);
    assert.strictEqual(h.el["#tHeraldPass"].checked, true);
    assert.strictEqual(h.el["#heraldTitle"].value, "Ravenhold");
    assert.strictEqual(h.el["#heraldStatusLine"].value, "Ashlands Friday");
    assert.strictEqual(h.el["#heraldName"].value, "Raven", "the realm's own name is not painted");
    assert.strictEqual(h.el["#heraldAvatar"].value, "https://i.imgur.com/raven.png");
    assert.strictEqual(h.context.HERALD_LOOK_REFUSED, null, "a refusal about another realm's text was left standing");
    assert.strictEqual(h.context.postShown, true, "the post line does not say this realm's post is placed");
    assert.strictEqual(h.el["#heraldRealmName"].textContent, T("herald.screen.head", { name: "Final Sunset" }));
    assert.strictEqual(h.el["#heraldOnLabel"].textContent, T("herald.screen.on", { name: "Final Sunset" }));
    assert.strictEqual(h.el["#heraldRealmPill"].textContent, T("herald.realm.editing", { name: "Final Sunset" }));
    assert.strictEqual(h.el["#heraldRealmPill"].style.display, "", "the head does not name the realm being edited");
    assert.ok(h.context.lookLines > 0 && h.context.codeLines > 0, "the look and code lines were not redrawn");

    /* The webhook is not read off the screen: only the wizard sets it. */
    assert.deepStrictEqual(JSON.parse(JSON.stringify(h.context.heraldRealmValues())), {
      Realm: "Final Sunset", On: true, ShareAddress: false, SharePassword: true, Title: "Ravenhold",
      StatusLine: "Ashlands Friday", BotName: "Raven", AvatarUrl: "https://i.imgur.com/raven.png",
    });
    /* A name Discord would refuse is left out of the save, and the rest still goes. */
    h.el["#heraldName"].value = "Discord Bot";
    assert.strictEqual(h.context.heraldRealmValues().BotName, undefined);
    /* A box that is not in the document is left out, never sent as blank. */
    delete h.el["#heraldTitle"];
    assert.strictEqual(h.context.heraldRealmValues().Title, undefined);
    assert.ok(!("Title" in JSON.parse(JSON.stringify(h.context.heraldDefined(h.context.heraldRealmValues())))));
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
    assert.strictEqual(h.el["#heraldHookLine"].textContent, T("herald.hook.line.none"), "the last realm's webhook was left on screen");
    assert.strictEqual(h.el["#heraldTitle"].value, "", "the last realm's title was left on screen");
    assert.strictEqual(h.el["#heraldStatusLine"].value, "");
    assert.strictEqual(h.el["#heraldName"].value, "", "the last realm's name was left on screen");
    assert.strictEqual(h.el["#tHeraldAddr"].checked, true);
    assert.strictEqual(h.el["#tHeraldPass"].checked, false, "the last realm's password tick was left on");
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
    assert.strictEqual(fresh.el["#tHeraldAddr"].checked, true);
    assert.strictEqual(fresh.el["#heraldHookLine"].textContent, T("herald.hook.line.none"));
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
    /* A key the save left out keeps what was read, rather than becoming undefined. */
    h.context.heraldRealmRemember({ Realm: "Final Sunset", BotName: undefined });
    assert.strictEqual(h.context.heraldRealmEntry("Final Sunset").BotName, "Raven");
  });

  await test("4. Publish acts on this realm, switches it on, and its post line follows it", async () => {
    /* Ashlands Test set up with a webhook of its own, and still off. */
    const hooked = () => { const p = prefs(); Object.assign(p.HeraldRealms[1], { WebhookUrl: HOOK_A, WebhookName: "Mead Hall" }); return p; };
    const h = hall({ read: hooked });
    h.context.heraldRealmsFrom(hooked());
    h.context.S.profileName = "Ashlands Test";
    h.context.heraldRenderRealm();
    await settle();
    h.calls.length = 0;

    assert.strictEqual(await h.context.heraldPublish(), true);
    const pub = h.calls.filter(c => c.method === "discord.publish");
    assert.strictEqual(pub.length, 1);
    assert.deepStrictEqual(pub[0].args, { profile: "Ashlands Test" }, "Publish did not name this realm");
    assert.strictEqual(h.el["#tHeraldRealm"].classList.contains("on"), true, "Publish left this realm off");
    assert.strictEqual(h.saves.length, 1, "Publish did not save first");
    assert.strictEqual(h.saves[0].On, true);
    assert.ok(!("DiscordSharingEnabled" in h.saves[0]), "Publish still switches an app-wide master");
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

    /* No webhook for this realm: the setup opens, nothing is sent or saved. Another realm's
       webhook is not borrowed. */
    const none = hall({});
    none.context.heraldRealmsFrom(prefs());
    none.context.S.profileName = "Ashlands Test";
    none.context.heraldRenderRealm();
    await settle();
    none.calls.length = 0;
    assert.strictEqual(await none.context.heraldPublish(), false);
    assert.strictEqual(none.calls.length, 0, "Publish with no webhook asked the host side");
    assert.strictEqual(none.saves.length, 0);
    assert.strictEqual(none.context.wizards, 1, "Publish with no webhook did not open the setup");
    assert.strictEqual(none.toasts[0], "ᚦ " + T("herald.publish.no_url.toast"), "Publish with no webhook did not say why");

    /* A refusal from Discord says so and leaves the line as it was. */
    /* Final Sunset with no post placed yet, as the host side reads it. */
    const unplaced = () => { const p = prefs(); p.HeraldRealms[0].HasPost = false; return p; };
    const refused = hall({ rpc: m => (m === "discord.publish" ? { ok: false, error: "Discord rejected the post (403)." } : unplaced()) });
    refused.context.heraldRealmsFrom(unplaced());
    refused.context.heraldRenderRealm();
    await settle();
    assert.strictEqual(await refused.context.heraldPublish(), false);
    assert.strictEqual(refused.context.postShown, false);
    assert.strictEqual(refused.toasts[0], "ᚦ Discord rejected the post (403).");

    /* The save that switches the realm on failed: nothing is published after it. */
    const unsaved = hall({ saveFails: true, read: unplaced });
    unsaved.context.heraldRealmsFrom(unplaced());
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

  await test("6. the webhook line: its own name, none yet, or a name asked of Discord once", async () => {
    const h = hall({ native: false });
    h.context.heraldRealmsFrom(prefs());
    h.context.heraldRenderRealm();
    assert.strictEqual(h.el["#heraldHookLine"].textContent, T("herald.hook.line.named", { name: "Longhouse" }));
    assert.ok(h.el["#heraldHookLine"].textContent.includes('"Longhouse"'), "the line does not quote the webhook's name");
    assert.ok(!h.el["#heraldHookLine"].textContent.includes("https://"), "the line shows the link itself");
    assert.strictEqual(h.el["#heraldHookChange"].textContent, T("herald.hook.change"));

    h.context.S.profileName = "Ashlands Test";
    h.context.heraldRenderRealm();
    assert.strictEqual(h.el["#heraldHookLine"].textContent, T("herald.hook.line.none"));
    assert.strictEqual(h.el["#heraldHookChange"].textContent, T("herald.hook.setup"));

    /* A webhook kept from before 1.2.17 has no name on file: Discord is asked once, the name is
       saved on the realm, and the line says it. */
    const ask = hall({});
    ask.context.heraldRealmsFrom({ HeraldRealms: [{ Realm: "Final Sunset", On: true, WebhookUrl: HOOK_A }] });
    ask.context.heraldRenderRealm();
    assert.strictEqual(ask.el["#heraldHookLine"].textContent, T("herald.hook.line.unnamed"));
    await settle(); await settle();
    const asked = ask.calls.filter(c => c.method === "discord.validate");
    assert.deepStrictEqual(asked.map(c => c.args), [{ url: HOOK_A }]);
    assert.strictEqual(ask.saves.length, 1, "the name Discord gave was not saved");
    assert.strictEqual(ask.saves[0].WebhookName, "Mead Hall");
    assert.strictEqual(ask.saves[0].Realm, "Final Sunset");
    ask.context.heraldRenderRealm();
    await settle();
    assert.strictEqual(ask.calls.filter(c => c.method === "discord.validate").length, 1, "the same link was asked twice");
  });

  await test("7. no app-wide switch or webhook box on the screen, and the master is retired", async () => {
    const page = HTML.slice(HTML.indexOf('<section class="page" id="page-herald"'), HTML.indexOf("<!-- ============ PAGE: SKALD"));
    for (const id of ["tHerald", "heraldUrl", "heraldThread", "heraldRealmUrl", "heraldRealmThread", "heraldIntro", "heraldWizBtn"])
      assert.ok(!page.includes('id="' + id + '"'), id + " is still on the Discord screen");
    assert.ok(!/herald\.sharing\./.test(page), "the Discord sharing card is still on the screen");
    assert.ok(page.includes('id="tHeraldRealm"'), "the realm's own switch left the screen");
    assert.ok(page.includes('id="heraldHookLine"') && page.includes('id="heraldHookChange"'), "the webhook line is not on the screen");

    /* Every press saves, and the save carries the realm's block and no master. */
    assert.ok(/\["tHeraldRealm","tHeraldLink"\]\.forEach\(id=>\s*\$\("#"\+id\)\.addEventListener\("click",\(\)=>heraldSave\(\)\)\);/.test(APP),
      "a switch on the screen does not save");
    assert.ok(/\["tHeraldAddr","tHeraldPass"\]\.forEach\(id=>\s*\$\("#"\+id\)\.addEventListener\("change",\(\)=>heraldSave\(\)\)\);/.test(APP),
      "a tick on the screen does not save");
    const save = fn("function heraldSave(extra,realmExtra){");
    assert.ok(!save.includes("DiscordSharingEnabled"), "the save still carries the master switch");
    assert.ok(!save.includes("DiscordWebhookUrl"), "the save still carries the app-wide webhook");
    assert.ok(save.includes("Herald:realm"), "the save does not carry the realm's block");

    /* The host side: the master read as on once the realms stand alone, and every rule reads it. */
    const rules = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Tools", "HeraldRealms.cs"), "utf8");
    assert.ok(/MasterOn\(UserPreferences app\) => app != null && \(app\.HeraldSimplifiedMigrated \|\| app\.DiscordSharingEnabled\)/.test(rules),
      "the retired master is not read as on after the move");
    assert.ok(/public static bool Shares\(UserPreferences app, ServerPreferences realm\)\s*=> MasterOn\(app\)/.test(rules),
      "HeraldRealms.Shares does not read the master through MasterOn");
    assert.ok(/StandsAlone\(app\) \? null :/.test(rules), "a realm's blank webhook still borrows the app-wide one after the move");
  });

  await test("8. the wizard sets up the realm on screen: its own link, or the same channel as another", async () => {
    const ctx = vm.createContext({});
    vm.runInContext(fn("function heraldWizPlace(w){"), ctx);
    const place = w => JSON.parse(JSON.stringify(ctx.heraldWizPlace(w)));
    const other = { realm: "Final Sunset", url: HOOK_B, thread: "999", name: "Longhouse" };
    /* The same channel as another realm: that realm's link, thread and name are copied. */
    assert.deepStrictEqual(place({ same: true, other, url: HOOK_A, thread: "5", threadOn: true, name: "x" }),
      { url: HOOK_B, thread: "999", name: "Longhouse" });
    /* Its own link, and a thread only behind "Post in a thread instead". */
    assert.deepStrictEqual(place({ same: false, other, url: " " + HOOK_A + " ", thread: "5", threadOn: false, name: "Mead Hall" }),
      { url: HOOK_A, thread: "", name: "Mead Hall" });
    assert.deepStrictEqual(place({ same: false, other: null, url: HOOK_A, thread: " 5 ", threadOn: true, name: "" }),
      { url: HOOK_A, thread: "5", name: "" });

    /* The first choice is another realm's channel, when another realm has a webhook. */
    const pick = vm.createContext({ HERALD_REALMS: {
      "final sunset": { Realm: "Final Sunset", WebhookUrl: HOOK_B, ThreadId: "999", WebhookName: "Longhouse" },
      "ashlands test": { Realm: "Ashlands Test", WebhookUrl: "" },
    } });
    vm.runInContext(fn("function heraldRealmKey(name){"), pick);
    vm.runInContext(fn("function heraldWizOther(realm){"), pick);
    assert.deepStrictEqual(JSON.parse(JSON.stringify(pick.heraldWizOther("Ashlands Test"))),
      { realm: "Final Sunset", url: HOOK_B, thread: "999", name: "Longhouse" });
    assert.strictEqual(pick.heraldWizOther("Final Sunset"), null, "a realm was offered its own channel");
    const open = fn("function heraldWizard(){");
    assert.ok(open.includes("same:!own&&!!other"), "the other realm's channel is not the first choice for a realm with no webhook");
    const render = fn("function heraldWizRender(){");
    assert.ok(render.indexOf('id="hwSame"') > 0 && render.indexOf('id="hwSame"') < render.indexOf('id="hwOwn"'),
      "Use the same channel is not the first choice");
    assert.ok(render.includes('T("herald.wiz.same",{name:HWIZ.other.realm})'));
    assert.ok(render.includes('id="hwThreadOn"') && render.includes('T("herald.wiz.thread.toggle")'), "the thread is not behind its own tick");

    const wiz = fn("async function heraldWizFinish(){");
    assert.ok(wiz.includes('setT("tHeraldRealm",true);'), "the wizard does not switch the realm on");
    assert.ok(wiz.includes('rpc("discord.publish",{profile:realm})'), "the wizard's publish does not name the realm");
    assert.ok(wiz.includes("heraldSave(null,{WebhookUrl:place.url,ThreadId:place.thread,WebhookName:place.name})"),
      "the wizard does not save the webhook on the realm");
    assert.ok(!/DiscordWebhookUrl|heraldUrl/.test(wiz), "the wizard still writes an app-wide webhook");
    assert.ok(wiz.indexOf("heraldRealmKey(HWIZ.realm)!==heraldRealmKey(HERALD_REALM)") > 0
      && wiz.indexOf("heraldRealmKey(HWIZ.realm)!==heraldRealmKey(HERALD_REALM)") < wiz.indexOf("await heraldSave("),
      "the wizard can set up a realm that is no longer on screen");
    assert.ok(open.includes("const realm=HERALD_REALM||heraldRealmOnScreen();"), "the wizard does not remember its realm");
    assert.ok(APP.includes('T("herald.wiz.sum.realm")'), "the wizard's summary does not name the realm");
    assert.ok(/\$\("#heraldHookChange"\)\.addEventListener\("click",\(\)=>heraldWizard\(\)\);/.test(APP), "Change does not run the setup");
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

    /* 1.2.17: the card's own words replaced the second card's caption, switch and boxes. */
    for (const id of ["herald.realm.editing", "herald.screen.head", "herald.screen.on", "herald.screen.on.help",
                      "herald.hook.line.named", "herald.hook.line.unnamed", "herald.hook.line.none",
                      "herald.hook.change", "herald.hook.setup", "herald.hook.change.title", "herald.look.preview.realm_off",
                      "herald.modcode.preview.realm_off", "herald.wiz.sum.realm", "herald.wiz.realm_moved.toast",
                      "herald.wiz.same", "herald.wiz.own", "herald.wiz.thread.toggle", "herald.wiz.sum.webhook.same"])
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
                       "ShareAddress = r.HeraldShareAddress", "SharePassword = r.HeraldSharePassword", "HasPost =",
                       "WebhookName = r.HeraldWebhookName", "BotName = r.HeraldBotName", "AvatarUrl = r.HeraldAvatarUrl"])
      assert.ok(BRIDGE.includes(key), "the realm's block does not carry " + key);
  });

  if (failures.length) {
    console.log("herald realm selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAIL");
    process.exit(1);
  }
  console.log("herald realm selftest: all " + passed + " rules pass");
})();
