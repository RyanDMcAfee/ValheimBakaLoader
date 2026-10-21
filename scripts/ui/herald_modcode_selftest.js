/* The mod profile code on the Discord hall, driven for real.
 *
 * WHY THIS EXISTS. The hall's "What the post carries" group has a radio pair (Thunderstore code,
 * Gale code) and one box for the code a mod manager hands out. Players paste it into their own
 * manager to get the same mods. The code belongs to ONE realm, because realms carry different
 * mod sets, while everything else on the hall belongs to the app. So the ways this goes wrong
 * quietly are: the line under the box naming the wrong manager, a blank box taking something
 * else on the hall down with it, a refused code being sent anyway or refused in silence, and a
 * realm switch leaving one realm's code on another.
 *
 * THE CODE UNDER TEST is the block between the HERALD-MODCODE markers in app.js, evaluated here
 * with the page's own globals stood in for. The sentences come out of the real en.json, and a
 * T() call for an id the catalog does not have fails the rule it is in.
 *
 * The rules:
 *   1. The radio drives the field name the preview line gives, for both managers.
 *   2. A blank code says the post carries no code, saves as none, and disables nothing else:
 *      both radios and the box stay live and the save carries the code keys and nothing more.
 *   3. A code that could not be one shows the one refusal sentence and sends nothing; fixing it
 *      takes the sentence down and sends it.
 *   4. A save names the realm the code was typed on, and a realm switch before the reply lands
 *      does not write the old realm's code into the new realm's profile.
 *   5. A realm switch repaints the radios, the box and the line from that realm's own profile,
 *      and drops a refusal that was about the previous realm's box.
 *   6. The host side's own refusal keeps the text and shows the sentence; any other failure puts
 *      the box back to what is saved.
 *   7. In the browser preview nothing is sent and the hall still answers.
 *   8. The wiring: where the controls sit, what is bound to them, the realm repaint, the refusal
 *      rows, the catalog and the host side's patterns.
 *   9. Each realm has a post of its own since 1.2.16, so another realm's server being up changes
 *      nothing here: the line promises this realm's post. With this realm's own switch off there
 *      is no post for it, so the line says the code is saved and why it is not showing. (Until
 *      1.2.16 the one post followed whichever realm was up, and this rule held the line to that.)
 *  10. The tick in front of the code (1.2.17) shows the box only with a code, unticking takes the
 *      code out of the post, and there is no app-wide sharing switch for the line to answer to.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/herald_modcode_selftest.js
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
const CODES = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Game", "ModProfileCodes.cs"), "utf8");
const GALE_ID = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Tools", "GaleProfileId.cs"), "utf8");
const SERVICE = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Tools", "DiscordStatusService.cs"), "utf8");

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
  const from = APP.indexOf("/* HERALD-MODCODE-BEGIN");
  const to = APP.indexOf("/* HERALD-MODCODE-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the HERALD-MODCODE markers");
  return APP.slice(from, to);
}

/* The catalog's own sentence with its slots filled, so a missing id fails loudly here. */
function T(id, params) {
  const entry = KEYS[id];
  if (!entry || typeof entry.lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return entry.lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

const FAIL = { failed: true };
const TS_CODE = "018f5c2a-7d1e-4b3a-9f00-1c2d3e4f5a6b";

/* A fresh hall: the real block, the controls it reads, and a record of every save. */
function hall(opts) {
  opts = opts || {};
  const sent = [];
  const el = {
    "#heraldModKindTs": { checked: true, disabled: false },
    "#heraldModKindGale": { checked: false, disabled: false },
    "#heraldModCode": { value: "", disabled: false },
    "#heraldModCodeStat": { className: "", textContent: "", style: {} },
    "#tHeraldModCode": { checked: false },
    "#heraldModCodeWrap": { style: { display: "none" } }, /* hidden in the markup until ticked */
    "#tHerald": switchEl(opts.sharing !== false),
    "#tHeraldRealm": switchEl(opts.realm !== false),
  };
  const context = {
    S: { profileName: "Final Sunset", prefs: Object.assign({ ProfileName: "Final Sunset" }, opts.prefs || {}),
         servers: opts.servers || [] },
    Native: { available: opts.native !== false },
    FAIL,
    T,
    window: { BAKA_ERR_ID: null },
    $: sel => el[sel] || null,
    heraldSave: extra => {
      sent.push(JSON.parse(JSON.stringify(extra || {})));
      if (opts.onSave) return opts.onSave(context);
      return Promise.resolve(opts.reply === undefined ? {} : opts.reply);
    },
  };
  vm.createContext(context);
  /* The page's one show/hide helper (REVEAL), as the page has it: a stand-in element has no layout, so it is set at once. */
  vm.runInContext(APP.slice(APP.indexOf("/* REVEAL-BEGIN"), APP.indexOf("/* REVEAL-END */")), context, { filename: "app.js#reveal" });
  vm.runInContext(block(), context, { filename: "app.js#herald-modcode" });
  for (const name of ["heraldModCodeKind", "heraldModCodeProblem", "heraldRenderModCodeLine",
                      "heraldRenderModCode", "heraldModCodeCommit"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  return { context, el, sent, line: () => el["#heraldModCodeStat"] };
}

/* A switch as the page has it: on is a class. */
function switchEl(on) {
  const classes = new Set(on ? ["sw", "on"] : ["sw"]);
  return { classList: { contains: c => classes.has(c), toggle: (c, v) => (v ? classes.add(c) : classes.delete(c)) } };
}

function choose(h, kind) {
  h.el["#heraldModKindTs"].checked = kind === "thunderstore";
  h.el["#heraldModKindGale"].checked = kind === "gale";
}

(async function main() {
  await test("1. the radio drives the field name the preview line gives", async () => {
    const h = hall({});
    h.el["#heraldModCode"].value = TS_CODE;
    choose(h, "thunderstore");
    assert.strictEqual(await h.context.heraldModCodeCommit(), true);
    assert.strictEqual(h.line().className, "wiz-stat ok");
    assert.strictEqual(h.line().textContent, T("herald.modcode.preview", { name: T("herald.modcode.kind.thunderstore") }));

    h.el["#heraldModCode"].value = "AB12CD";
    choose(h, "gale");
    assert.strictEqual(await h.context.heraldModCodeCommit(), true);
    assert.strictEqual(h.line().textContent, T("herald.modcode.preview", { name: T("herald.modcode.kind.gale") }));
    assert.ok(!h.line().textContent.includes(T("herald.modcode.kind.thunderstore")),
      "the line still names Thunderstore after Gale was chosen");
    assert.strictEqual(h.sent[1].ModProfileCodeKind, "gale");
    assert.strictEqual(h.sent[0].ModProfileCodeKind, "thunderstore");
  });

  await test("2. a blank code: no code in the post, saved as none, nothing else disabled", async () => {
    const h = hall({ prefs: { ModProfileCodeKind: "gale", ModProfileCode: "AB12CD" } });
    h.context.heraldRenderModCode();
    h.el["#heraldModCode"].value = "   ";
    assert.strictEqual(await h.context.heraldModCodeCommit(), true);
    assert.strictEqual(h.line().className, "wiz-stat dim");
    assert.strictEqual(h.line().textContent, T("herald.modcode.preview.none"));
    assert.deepStrictEqual(h.sent, [{ ModProfileCodeKind: "none", ModProfileCode: "", ModProfileCodeRealm: "Final Sunset" }],
      "a blank save must carry exactly the three code keys");
    for (const id of ["#heraldModKindTs", "#heraldModKindGale", "#heraldModCode"])
      assert.strictEqual(h.el[id].disabled, false, id + " was disabled by a blank code");
    assert.strictEqual(h.el["#heraldModCode"].value, "", "the box kept the spaces");
    assert.strictEqual(h.context.S.prefs.ModProfileCodeKind, "none");
    assert.strictEqual(h.context.S.prefs.ModProfileCode, null);
    /* And the save went through the road every other field on the screen takes, which builds
       the realm's block alongside whatever extra it is handed. */
    const save = APP.slice(APP.indexOf("function heraldSave(extra,realmExtra){"), APP.indexOf("\n}", APP.indexOf("function heraldSave(extra,realmExtra){")));
    assert.ok(/Herald:realm/.test(save) && /extra\|\|\{\}/.test(save),
      "heraldSave no longer carries the realm's block beside the extra keys");
  });

  await test("3. a refused code shows its one sentence and sends nothing", async () => {
    const h = hall({});
    for (const bad of ["`@everyone`", "https://thunderstore.io/x", "abc", "-abcd", "has space"]) {
      h.el["#heraldModCode"].value = bad;
      choose(h, "thunderstore");
      assert.strictEqual(await h.context.heraldModCodeCommit(), false, bad + " was accepted");
      assert.strictEqual(h.line().className, "wiz-stat bad");
      assert.strictEqual(h.line().textContent, "ᚦ " + T("herald.modcode.refused"));
    }
    h.el["#heraldModCode"].value = "AB12";
    choose(h, "gale");
    assert.strictEqual(await h.context.heraldModCodeCommit(), false, "a four character Gale code was accepted");
    assert.strictEqual(h.sent.length, 0, "a refused code was sent: " + JSON.stringify(h.sent));
    assert.strictEqual(h.context.S.prefs.ModProfileCode, undefined, "a refused code reached the profile");
    /* One sentence, as the catalog holds it. */
    assert.strictEqual((T("herald.modcode.refused").match(/[.!?](\s|$)/g) || []).length, 1,
      "the refusal is more than one sentence");

    h.el["#heraldModCode"].value = "AB12CD";
    assert.strictEqual(await h.context.heraldModCodeCommit(), true);
    assert.strictEqual(h.line().className, "wiz-stat ok", "the refusal stayed up after the code was fixed");
    assert.strictEqual(h.sent.length, 1);
  });

  await test("4. a save names its realm, and a switch mid flight does not cross realms", async () => {
    let release;
    const h = hall({
      onSave: () => new Promise(res => { release = () => res({}); }),
    });
    h.el["#heraldModCode"].value = TS_CODE;
    const pending = h.context.heraldModCodeCommit();
    assert.strictEqual(h.sent[0].ModProfileCodeRealm, "Final Sunset");
    /* The host switches realms before the reply lands. */
    h.context.S.profileName = "Ashlands Test";
    h.context.S.prefs = { ProfileName: "Ashlands Test", ModProfileCodeKind: "none", ModProfileCode: null };
    release();
    assert.strictEqual(await pending, true);
    assert.strictEqual(h.context.S.prefs.ModProfileCode, null, "the old realm's code landed on the new realm's profile");
    assert.strictEqual(h.context.S.prefs.ModProfileCodeKind, "none");
  });

  await test("5. a realm switch repaints from that realm's profile and drops a stale refusal", async () => {
    const h = hall({});
    h.el["#heraldModCode"].value = "nope nope";
    await h.context.heraldModCodeCommit();
    assert.strictEqual(h.line().className, "wiz-stat bad");

    h.context.S.prefs = { ProfileName: "Gale Realm", ModProfileCodeKind: "gale", ModProfileCode: "ZX90QW" };
    h.context.heraldRenderModCode();
    assert.strictEqual(h.el["#heraldModKindGale"].checked, true);
    assert.strictEqual(h.el["#heraldModKindTs"].checked, false);
    assert.strictEqual(h.el["#heraldModCode"].value, "ZX90QW");
    assert.strictEqual(h.line().textContent, T("herald.modcode.preview", { name: T("herald.modcode.kind.gale") }));

    h.context.S.prefs = { ProfileName: "Plain Realm" };
    h.context.heraldRenderModCode();
    assert.strictEqual(h.el["#heraldModKindTs"].checked, true, "a realm with no kind should rest on Thunderstore");
    assert.strictEqual(h.el["#heraldModKindGale"].checked, false);
    assert.strictEqual(h.el["#heraldModCode"].value, "");
    assert.strictEqual(h.line().textContent, T("herald.modcode.preview.none"));
  });

  await test("6. the host side's refusal keeps the text; any other failure puts back what is saved", async () => {
    const refused = hall({
      prefs: { ModProfileCodeKind: "gale", ModProfileCode: "AB12CD" },
      onSave: c => { c.window.BAKA_ERR_ID = "userprefs.save.badModCode"; return Promise.resolve(FAIL); },
    });
    refused.el["#heraldModCode"].value = "QQ11WW";
    choose(refused, "gale");
    assert.strictEqual(await refused.context.heraldModCodeCommit(), false);
    assert.strictEqual(refused.el["#heraldModCode"].value, "QQ11WW", "the host's refusal threw away what was typed");
    assert.strictEqual(refused.line().textContent, "ᚦ " + T("herald.modcode.refused"));
    assert.strictEqual(refused.context.S.prefs.ModProfileCode, "AB12CD");

    const failed = hall({
      prefs: { ModProfileCodeKind: "gale", ModProfileCode: "AB12CD" },
      onSave: c => { c.window.BAKA_ERR_ID = null; return Promise.resolve(FAIL); },
    });
    failed.el["#heraldModCode"].value = TS_CODE;
    choose(failed, "thunderstore");
    assert.strictEqual(await failed.context.heraldModCodeCommit(), false);
    assert.strictEqual(failed.el["#heraldModCode"].value, "AB12CD", "a failed save left an unsaved code in the box");
    assert.strictEqual(failed.el["#heraldModKindGale"].checked, true);
  });

  await test("7. the browser preview sends nothing and still answers", async () => {
    const h = hall({ native: false });
    h.el["#heraldModCode"].value = "AB12CD";
    choose(h, "gale");
    assert.strictEqual(await h.context.heraldModCodeCommit(), true);
    assert.strictEqual(h.sent.length, 0);
    assert.strictEqual(h.context.S.prefs.ModProfileCodeKind, "gale");
    assert.strictEqual(h.line().className, "wiz-stat ok");
  });

  await test("8. the wiring around the block", async () => {
    /* 1.2.17: one card, and the code under Show in the post. */
    const share = HTML.slice(HTML.indexOf('<div class="card b-herald">'), HTML.indexOf('id="heraldPreview"'));
    assert.ok(share.length > 0, "the Discord card is gone");
    const at = id => share.indexOf('id="' + id + '"');
    for (const id of ["heraldModCode", "heraldModKindTs", "heraldModKindGale", "heraldModCodeStat"])
      assert.ok(at(id) > 0, id + " is not in the What the post carries group");
    assert.ok(at("tHeraldPass") < at("heraldModCode") && at("heraldModCode") < at("tHeraldRoutine"),
      "the code is not below the address and password switches");
    assert.ok(/<input type="radio" name="heraldModKind" id="heraldModKindTs" value="thunderstore"/.test(share));
    assert.ok(/<input type="radio" name="heraldModKind" id="heraldModKindGale" value="gale"/.test(share));
    assert.ok(/role="radiogroup"[^>]*data-i18n-aria="herald\.modcode\.kinds\.aria"/.test(share));
    /* The note under the box gave way to a tick in front of it, worded as the post words it. */
    assert.ok(/<input type="checkbox" id="tHeraldModCode"> <span data-i18n="herald\.show\.modcode">/.test(share), "the code has no tick in front of it");
    assert.ok(at("tHeraldModCode") < at("heraldModKindTs"), "the tick is not in front of the code");
    assert.ok(/data-i18n="herald\.modcode\.kind\.ts\.short"/.test(share) && /data-i18n="herald\.modcode\.kind\.gale\.short"/.test(share),
      "the two kinds are not worded on the card");
    const stat = share.slice(share.indexOf('id="heraldModCodeStat"') - 40, share.indexOf('id="heraldModCodeStat"') + 60);
    assert.ok(!/data-i18n/.test(stat), "the line under the box is app.js's and must carry no id");

    assert.ok(/\$\("#heraldModCode"\)\.addEventListener\("change",\(\)=>\{heraldModCodeCommit\(\);\}\);/.test(APP),
      "leaving the box does not commit the code");
    assert.ok(/\["heraldModKindTs","heraldModKindGale"\]\.forEach\(id=>\s*\$\("#"\+id\)\.addEventListener\("change",\(\)=>\{heraldModCodeCommit\(\);\}\)\);/.test(APP),
      "moving a radio does not commit the code");
    const all = APP.slice(APP.indexOf("function renderAllFromPrefs(){"), APP.indexOf("\n}", APP.indexOf("function renderAllFromPrefs(){")));
    assert.ok(all.includes("heraldRenderModCode()"), "a realm switch does not repaint the code");
    const refresh = APP.slice(APP.indexOf("async function refreshHerald(){"), APP.indexOf("\n}", APP.indexOf("async function refreshHerald(){")));
    assert.ok(refresh.includes("heraldRenderModCode()"), "opening the hall does not repaint the code");

    const table = APP.slice(APP.indexOf("const HOST_SENTENCES=["), APP.indexOf("];", APP.indexOf("const HOST_SENTENCES=[")));
    assert.ok(table.includes('{named:"userprefs.save.badModCode",     textId:"herald.modcode.refused"}'));
    assert.ok(table.includes('{named:"userprefs.save.modCodeNoRealm", textId:"herald.modcode.no_realm"}'));

    for (const id of ["herald.modcode.label", "herald.modcode.kinds.aria", "herald.modcode.kind.thunderstore",
                      "herald.modcode.kind.gale", "herald.modcode.placeholder", "herald.modcode.note",
                      "herald.modcode.preview", "herald.modcode.preview.none", "herald.modcode.refused",
                      "herald.modcode.preview.realm_off", "herald.modcode.preview.off",
                      "herald.modcode.no_realm", "host.status.field.mod_code.thunderstore",
                      "host.status.field.mod_code.gale"])
      assert.ok(KEYS[id] && typeof KEYS[id].lore === "string", "the catalog has no " + id);
    assert.strictEqual(KEYS["herald.modcode.note"].lore, "Players paste this into their mod manager to get the same mods.");

    /* The page and the host side hold the same two patterns. */
    const cs = name => {
      const m = new RegExp("public const string " + name + ' = "([^"]+)";').exec(CODES);
      assert.ok(m, "ModProfileCodes.cs has no " + name);
      return m[1];
    };
    const ctx = vm.createContext({ $: () => null, S: {}, T, Native: {}, FAIL, window: {}, heraldSave: () => null });
    vm.runInContext(block() + "\nthis.RE=HERALD_MODCODE_RE;", ctx);
    assert.strictEqual(ctx.RE.thunderstore.source, cs("ThunderstorePattern"));
    /* A Gale code is the id the Mods hall follows a Gale profile by, so the host side checks it
       with GaleProfileId and the page's copy is that file's PagePattern. */
    assert.ok(/public const string GalePattern = Tools\.GaleProfileId\.PagePattern;/.test(CODES),
      "ModProfileCodes.cs keeps a Gale pattern of its own again");
    const page = /public const string PagePattern = "([^"]+)";/.exec(GALE_ID);
    assert.ok(page, "GaleProfileId.cs has no PagePattern");
    assert.strictEqual(ctx.RE.gale.source, page[1]);
    for (const id of ["Ab_9-xY_z0Qr7_Lm-NoPqA", "_AAAAAAAAAAAAAAAAAAAAA", "-b12cd34_ef56gh78ij90g", "AB12CD"])
      assert.ok(ctx.RE.gale.test(id), "the page refuses the Gale id " + id);
    for (const id of ["AAAAAAAAAAAAAAAAAAAAAB", "ab_12C", "a1b2c3d4-e5f6", "AAAAAAAAAAAAAAAAAAAAA"])
      assert.ok(!ctx.RE.gale.test(id), "the page takes the Gale id " + id);

    /* And the post names the field from the host catalog, after Mods. */
    const mods = SERVICE.indexOf('HostCatalog.T("host.status.field.mods")');
    const code = SERVICE.indexOf('HostCatalog.T("host.status.field.mod_code.gale")');
    const next = SERVICE.indexOf('HostCatalog.T("host.status.field.last_mod_update")');
    assert.ok(mods > 0 && code > mods && next > code, "the code field is not straight after Mods in the post");
  });

  await test("9. each realm its own post: another realm up changes nothing, this realm off says so", async () => {
    const servers = [
      { name: "Final Sunset", running: false },
      { name: "Ashlands Test", running: true },
    ];
    const h = hall({ servers });
    h.el["#heraldModCode"].value = TS_CODE;
    assert.strictEqual(await h.context.heraldModCodeCommit(), true);
    assert.strictEqual(h.sent.length, 1, "the code was not saved for this realm");
    assert.strictEqual(h.sent[0].ModProfileCodeRealm, "Final Sunset");
    /* Another realm's server is up, and this realm's post still carries this realm's code. */
    assert.strictEqual(h.line().className, "wiz-stat ok");
    assert.strictEqual(h.line().textContent, T("herald.modcode.preview", { name: T("herald.modcode.kind.thunderstore") }));
    assert.ok(!/heraldPostRealm/.test(block()), "the block still picks one realm for the post");

    /* This realm's own switch off: saved, and there is no post for it. */
    h.el["#tHeraldRealm"].classList.toggle("on", false);
    h.context.heraldRenderModCodeLine();
    assert.strictEqual(h.line().className, "wiz-stat dim");
    assert.strictEqual(h.line().textContent, T("herald.modcode.preview.realm_off"));
    const off = hall({ realm: false });
    off.el["#heraldModCode"].value = "AB12CD";
    choose(off, "gale");
    assert.strictEqual(await off.context.heraldModCodeCommit(), true);
    assert.strictEqual(off.sent.length, 1, "the code was not saved while this realm is off");
    assert.strictEqual(off.line().textContent, T("herald.modcode.preview.realm_off"));

    /* The retired master changes nothing: this realm's own switch is the only one there is. */
    const master = hall({ realm: false, sharing: false });
    master.el["#heraldModCode"].value = "AB12CD";
    choose(master, "gale");
    master.context.heraldRenderModCodeLine();
    assert.strictEqual(master.line().textContent, T("herald.modcode.preview.realm_off"));

    /* And the line follows the realm's switch: its repaint redraws the line. */
    const realm = APP.slice(APP.indexOf("function heraldRenderRealm(){"), APP.indexOf("\n}", APP.indexOf("function heraldRenderRealm(){")));
    assert.ok(realm.includes("heraldRenderModCodeLine()"), "a realm repaint does not redraw the line under the code");
  });

  await test("10. the tick in front of the code, and no app-wide switch to answer to", async () => {
    /* A stray app-wide switch drawn off changes nothing: it left the screen in 1.2.17. */
    const h = hall({ sharing: false });
    h.el["#heraldModCode"].value = "AB12CD";
    choose(h, "gale");
    assert.strictEqual(await h.context.heraldModCodeCommit(), true);
    assert.strictEqual(h.sent.length, 1, "the code was not saved");
    assert.strictEqual(h.line().className, "wiz-stat ok", "the line still answers to the retired master switch");
    assert.strictEqual(h.line().textContent, T("herald.modcode.preview", { name: T("herald.modcode.kind.gale") }));
    assert.ok(!block().includes('$("#tHerald")'), "the block still reads the retired master switch");
    const save = APP.slice(APP.indexOf("function heraldSave(extra,realmExtra){"), APP.indexOf("\n}", APP.indexOf("function heraldSave(extra,realmExtra){")));
    assert.ok(save.includes("heraldRenderModCodeLine()"), "moving the realm's switch does not repaint the line");

    /* A realm with a code shows the tick on and the box; one without shows neither. */
    const withCode = hall({ prefs: { ModProfileCodeKind: "gale", ModProfileCode: "AB12CD" } });
    withCode.context.heraldRenderModCode();
    assert.strictEqual(withCode.el["#tHeraldModCode"].checked, true);
    assert.strictEqual(withCode.el["#heraldModCodeWrap"].style.display, "");
    const none = hall({});
    none.context.heraldRenderModCode();
    assert.strictEqual(none.el["#tHeraldModCode"].checked, false);
    assert.strictEqual(none.el["#heraldModCodeWrap"].style.display, "none", "the box shows with no code and no tick");
    /* 1.2.17 follow-up: the kind, the box and the line under them are one block below the tick,
       rolled in and out together, so the line has no show/hide of its own. */
    const page = fs.readFileSync(path.join(WEB, "index.html"), "utf8");
    const wrapAt = page.indexOf('<div id="heraldModCodeWrap" class="herald-sub" style="display:none">');
    assert.ok(wrapAt > page.indexOf('id="tHeraldModCode"'), "the mod code's block is not below its tick");
    assert.ok(page.indexOf('id="heraldModCodeStat"') > wrapAt && page.indexOf('id="heraldModCodeStat"') < page.indexOf('data-i18n="herald.events.label"'),
      "the line under the box is not inside its block");
    /* Unticking takes the code out of the post, the way a blank box always has. */
    const at = APP.indexOf('$("#tHeraldModCode").addEventListener("change",()=>{');
    assert.ok(at > 0, "the tick is not wired");
    const wire = APP.slice(at, APP.indexOf("\n});", at));
    assert.ok(wire.includes('box.value="";heraldModCodeCommit();'), "unticking does not take the code out of the post");
    assert.ok(wire.includes("heraldRenderModTick(on)"), "the tick does not show or hide the box");

    /* A refusal still comes first: what is wrong with the box comes first. */
    h.el["#heraldModCode"].value = "no good";
    assert.strictEqual(await h.context.heraldModCodeCommit(), false);
    assert.strictEqual(h.line().textContent, "ᚦ " + T("herald.modcode.refused"));
  });

  console.log(failures.length
    ? "herald mod code selftest: " + failures.length + " FAILED, " + passed + " passed"
    : "herald mod code selftest: " + passed + "/" + passed + " rules hold");
  process.exit(failures.length ? 1 : 0);
})();
