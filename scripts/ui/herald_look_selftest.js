/* The Herald's look on the Discord hall, driven for real.
 *
 * WHY THIS EXISTS. Under the webhook fields the hall has four boxes (Display name, Avatar image
 * URL, Title, Status line) and one switch (Link to the BakaLoader page). They decide how every
 * post is signed and headed. The ways this goes wrong quietly are: a picture link that is not a
 * picture sent anyway (or refused in silence), a refused value riding along on the next switch
 * press, the line under the boxes promising something the post will not carry, the switch drawn
 * on while the saved preference is off, and the page and the host side disagreeing about which
 * links are pictures.
 *
 * THE CODE UNDER TEST is the block between the HERALD-LOOK markers in app.js, evaluated here
 * with the page's own globals stood in for. The sentences come out of the real en.json, and a
 * T() call for an id the catalog does not have fails the rule it is in.
 *
 * The rules:
 *   1. All blank: the line names the webhook's own name, the logo, the server name as the title
 *      and the link, and a save carries blank boxes and the switch on.
 *   2. Every box set: the line names each of them and a save carries each of them.
 *   3. The switch off takes the link out of the line and out of the save as false.
 *   4. A picture link that is not a picture shows the one refusal sentence and sends nothing; it
 *      is left out of every other save too; fixing it takes the sentence down and sends it.
 *   5. A name Discord refuses does the same.
 *   6. The host side's own refusal keeps the text and shows the sentence.
 *   7. With Discord sharing off the line says the look is saved and nothing is posted.
 *   8. A repaint from the saved preferences fills the boxes and the switch and drops a refusal.
 *   9. In the browser preview nothing is sent and the hall still answers.
 *  10. The page and the host side hold the same picture rules and name rules.
 *  11. The wiring: where the controls sit, what is bound to them, the save, the redraws, the
 *      refusal rows, the catalog and the host side's save and read.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/herald_look_selftest.js
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
const LOOK = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Tools", "DiscordHeraldLook.cs"), "utf8");
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
  const from = APP.indexOf("/* HERALD-LOOK-BEGIN");
  const to = APP.indexOf("/* HERALD-LOOK-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the HERALD-LOOK markers");
  return APP.slice(from, to);
}

/* The catalog's own sentence with its slots filled, so a missing id fails loudly here. */
function T(id, params) {
  const entry = KEYS[id];
  if (!entry || typeof entry.lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return entry.lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

const FAIL = { failed: true };
const NAME = "Ravenhold Herald";
const AVATAR = "https://example.org/pics/raven.png";
const TITLE = "Ravenhold, the long winter";
const STATUS = "Ashlands opens Friday at eight.";

/* A switch as the page has it: on is a class. */
function switchEl(on) {
  const classes = new Set(on ? ["toggle", "on"] : ["toggle"]);
  return { classList: { contains: c => classes.has(c), toggle: (c, v) => (v ? classes.add(c) : classes.delete(c)) } };
}

/* A fresh hall: the real block, the controls it reads, and a record of every save. heraldSave
   is the page's own shape: the base keys, then the look, then the extras. */
function hall(opts) {
  opts = opts || {};
  const sent = [];
  const el = {
    "#heraldName": { value: "" },
    "#heraldAvatar": { value: "" },
    "#heraldTitle": { value: "" },
    "#heraldStatusLine": { value: "" },
    "#heraldLookStat": { className: "", textContent: "" },
    "#tHeraldLink": switchEl(opts.link !== false),
    "#tHerald": switchEl(opts.sharing !== false),
  };
  const context = {
    Native: { available: opts.native !== false },
    FAIL,
    T,
    URL,
    window: { BAKA_ERR_ID: null },
    $: sel => el[sel] || null,
  };
  context.heraldSave = extra => {
    sent.push(JSON.parse(JSON.stringify(Object.assign({ DiscordSharingEnabled: true }, context.heraldLookValues(), extra || {}))));
    if (opts.onSave) return opts.onSave(context);
    return Promise.resolve(opts.reply === undefined ? {} : opts.reply);
  };
  vm.createContext(context);
  vm.runInContext(block(), context, { filename: "app.js#herald-look" });
  for (const name of ["heraldLookAvatarProblem", "heraldLookNameProblem", "heraldLookValues",
                      "heraldRenderLookLine", "heraldApplyLook", "heraldLookCommit"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  return { context, el, sent, line: () => el["#heraldLookStat"] };
}

function fill(h, v) {
  if ("name" in v) h.el["#heraldName"].value = v.name;
  if ("avatar" in v) h.el["#heraldAvatar"].value = v.avatar;
  if ("title" in v) h.el["#heraldTitle"].value = v.title;
  if ("status" in v) h.el["#heraldStatusLine"].value = v.status;
}

function preview(parts) {
  return T("herald.look.preview", { parts: parts.join(T("herald.look.part.join")) });
}

(async function main() {
  await test("1. all blank: the defaults, named, and a save that carries blank", async () => {
    const h = hall({});
    assert.strictEqual(await h.context.heraldLookCommit(), true);
    assert.strictEqual(h.line().className, "wiz-stat ok");
    assert.strictEqual(h.line().textContent, preview([
      T("herald.look.part.name.default"), T("herald.look.part.avatar.logo"),
      T("herald.look.part.title.server"), T("herald.look.part.link")]));
    assert.strictEqual(h.sent.length, 1);
    const s = h.sent[0];
    assert.strictEqual(s.DiscordBotName, "");
    assert.strictEqual(s.DiscordAvatarUrl, "");
    assert.strictEqual(s.DiscordTitle, "");
    assert.strictEqual(s.DiscordStatusLine, "");
    assert.strictEqual(s.DiscordLinkToBakaLoader, true);
  });

  await test("2. every box set: each named in the line and carried by the save", async () => {
    const h = hall({});
    fill(h, { name: "  " + NAME + " ", avatar: AVATAR, title: TITLE, status: STATUS });
    assert.strictEqual(await h.context.heraldLookCommit(), true);
    assert.strictEqual(h.el["#heraldName"].value, NAME, "the box was not trimmed");
    assert.strictEqual(h.line().textContent, preview([
      T("herald.look.part.name", { name: NAME }), T("herald.look.part.avatar.custom"),
      T("herald.look.part.title", { title: TITLE }), T("herald.look.part.status"), T("herald.look.part.link")]));
    const s = h.sent[0];
    assert.strictEqual(s.DiscordBotName, NAME);
    assert.strictEqual(s.DiscordAvatarUrl, AVATAR);
    assert.strictEqual(s.DiscordTitle, TITLE);
    assert.strictEqual(s.DiscordStatusLine, STATUS);
  });

  await test("3. the switch off: no link in the line, false in the save", async () => {
    const h = hall({ link: false });
    fill(h, { status: STATUS });
    h.context.heraldRenderLookLine();
    assert.ok(!h.line().textContent.includes(T("herald.look.part.link")), "the line still promises the link");
    assert.ok(h.line().textContent.includes(T("herald.look.part.status")));
    await h.context.heraldSave();
    assert.strictEqual(h.sent[0].DiscordLinkToBakaLoader, false);
  });

  await test("4. a picture link that is not a picture is refused in one sentence and never sent", async () => {
    const h = hall({});
    fill(h, { avatar: "http://example.org/a.png", title: TITLE });
    assert.strictEqual(await h.context.heraldLookCommit(), false);
    assert.strictEqual(h.sent.length, 0, "a refused picture link was sent");
    assert.strictEqual(h.line().className, "wiz-stat bad");
    assert.strictEqual(h.line().textContent, "ᚦ " + T("herald.look.avatar.refused"));
    assert.strictEqual(h.el["#heraldAvatar"].value, "http://example.org/a.png", "the text was not kept to fix");

    /* A switch pressed beside it still saves, and the refused link is not in that save. */
    await h.context.heraldSave();
    assert.strictEqual(h.sent.length, 1);
    assert.ok(!("DiscordAvatarUrl" in h.sent[0]), "a switch press carried the refused link");
    assert.strictEqual(h.sent[0].DiscordTitle, TITLE);
    assert.strictEqual(h.line().textContent, "ᚦ " + T("herald.look.avatar.refused"), "the refusal fell off on a switch press");

    fill(h, { avatar: "https://i.imgur.com/AbCdEf" });
    assert.strictEqual(await h.context.heraldLookCommit(), true);
    assert.strictEqual(h.sent[1].DiscordAvatarUrl, "https://i.imgur.com/AbCdEf");
    assert.strictEqual(h.line().className, "wiz-stat ok");
  });

  await test("5. a name Discord refuses is refused here and never sent", async () => {
    for (const bad of ["The Discord Herald", "clyde", "x".repeat(81)]) {
      const h = hall({});
      fill(h, { name: bad });
      assert.strictEqual(await h.context.heraldLookCommit(), false, bad);
      assert.strictEqual(h.sent.length, 0, "a refused name was sent: " + bad);
      assert.strictEqual(h.line().textContent, "ᚦ " + T("herald.look.name.refused"));
      await h.context.heraldSave();
      assert.ok(!("DiscordBotName" in h.sent[0]), "a switch press carried the refused name");
    }
  });

  await test("6. the host side's own refusal keeps the text and shows the sentence", async () => {
    const h = hall({ onSave: ctx => { ctx.window.BAKA_ERR_ID = "userprefs.save.badAvatarUrl"; return Promise.resolve(FAIL); } });
    fill(h, { avatar: "https://example.org/a.png" });
    assert.strictEqual(await h.context.heraldLookCommit(), false);
    assert.strictEqual(h.el["#heraldAvatar"].value, "https://example.org/a.png");
    assert.strictEqual(h.line().textContent, "ᚦ " + T("herald.look.avatar.refused"));

    const other = hall({ onSave: ctx => { ctx.window.BAKA_ERR_ID = "something.else"; return Promise.resolve(FAIL); } });
    fill(other, { title: TITLE });
    assert.strictEqual(await other.context.heraldLookCommit(), false);
    assert.strictEqual(other.line().className, "wiz-stat ok", "an unrelated failure was read as a refused look");
  });

  await test("7. sharing off: saved, and the line says nothing is posted", async () => {
    const h = hall({ sharing: false });
    fill(h, { name: NAME });
    assert.strictEqual(await h.context.heraldLookCommit(), true);
    assert.strictEqual(h.sent.length, 1, "the look was not saved while sharing is off");
    assert.strictEqual(h.line().className, "wiz-stat dim");
    assert.strictEqual(h.line().textContent, T("herald.look.preview.off"));
  });

  await test("8. a repaint from the saved preferences fills the hall and drops a refusal", async () => {
    const h = hall({});
    fill(h, { avatar: "nope" });
    await h.context.heraldLookCommit();
    assert.strictEqual(h.line().className, "wiz-stat bad");
    h.context.heraldApplyLook({ DiscordBotName: NAME, DiscordAvatarUrl: AVATAR, DiscordTitle: null,
                                DiscordStatusLine: STATUS, DiscordLinkToBakaLoader: false });
    assert.strictEqual(h.el["#heraldName"].value, NAME);
    assert.strictEqual(h.el["#heraldAvatar"].value, AVATAR);
    assert.strictEqual(h.el["#heraldTitle"].value, "");
    assert.strictEqual(h.el["#heraldStatusLine"].value, STATUS);
    assert.strictEqual(h.el["#tHeraldLink"].classList.contains("on"), false, "the switch is drawn on while the preference is off");
    assert.strictEqual(h.line().className, "wiz-stat ok");
    /* A file from before the look has no key at all, and that is the default: on. */
    h.context.heraldApplyLook({});
    assert.strictEqual(h.el["#tHeraldLink"].classList.contains("on"), true);
    assert.strictEqual(h.el["#heraldName"].value, "");
  });

  await test("9. the browser preview: nothing sent, the hall still answers", async () => {
    const h = hall({ native: false });
    fill(h, { title: TITLE });
    assert.strictEqual(await h.context.heraldLookCommit(), true);
    assert.strictEqual(h.sent.length, 0);
    assert.ok(h.line().textContent.includes(TITLE));
  });

  await test("10. the page and the host side hold the same picture and name rules", async () => {
    const h = hall({});
    const hosts = /ImageHosts = \{([^}]*)\}/.exec(LOOK);
    const exts = /ImageExtensions = \{([^}]*)\}/.exec(LOOK);
    assert.ok(hosts && exts, "DiscordHeraldLook.cs no longer lists its hosts and endings");
    const list = m => m[1].split(",").map(x => x.trim().replace(/^"|"$/g, "")).filter(Boolean).sort();
    assert.strictEqual(JSON.stringify(list(hosts)),
      vm.runInContext("JSON.stringify(HERALD_LOOK_IMAGE_HOSTS.slice().sort())", h.context));
    const pageExt = vm.runInContext("HERALD_LOOK_IMAGE_EXT.source", h.context);
    for (const e of list(exts)) assert.ok(new RegExp(pageExt, "i").test("a" + e), "the page refuses the ending " + e);
    for (const e of [".svg", ".bmp", ".html"]) assert.ok(!new RegExp(pageExt, "i").test("a" + e), "the page takes the ending " + e);
    assert.ok(/public const int NameMax = 80;/.test(LOOK));
    assert.strictEqual(vm.runInContext("HERALD_LOOK_NAME_MAX", h.context), 80);

    /* The same table the host side's tests run. */
    const table = [
      ["https://example.org/a.png", true], ["https://example.org/a.JPG", true], ["https://example.org/a.jpeg", true],
      ["https://example.org/a.gif", true], ["https://example.org/a.webp", true], ["https://example.org/a.png?size=128", true],
      ["https://raw.githubusercontent.com/someone/repo/main/logo", true], ["https://cdn.discordapp.com/avatars/1/abc", true],
      ["https://i.imgur.com/AbCdEf", true], ["  https://i.imgur.com/AbCdEf.png  ", true],
      ["http://example.org/a.png", false], ["ftp://example.org/a.png", false], ["https://example.org/a.png.html", false],
      ["https://example.org/page", false], ["https://imgur.com/AbCdEf", false], ["https://i.imgur.com.evil.example/a", false],
      ["https://user:pass@example.org/a.png", false], ["javascript:alert(1)//.png", false], ["example.org/a.png", false],
      ["not a link", false],
    ];
    for (const [url, ok] of table)
      assert.strictEqual(h.context.heraldLookAvatarProblem(url) === null, ok, url);
    assert.strictEqual(h.context.heraldLookAvatarProblem(""), null, "blank is the logo, not a refusal");
    assert.strictEqual(h.context.heraldLookAvatarProblem(
      "https://raw.githubusercontent.com/RyanDMcAfee/ValheimBakaLoader/main/img/herald-avatar.png"), null);
  });

  await test("11. the wiring around the block", async () => {
    /* Under the webhook fields, in the sharing card, before the card about what the post carries. */
    const card = HTML.slice(HTML.indexOf('<div class="card b-herald">'), HTML.indexOf('<div class="card b-heraldshare">'));
    const order = ['id="heraldUrl"', 'id="heraldThread"', 'id="heraldName"', 'id="heraldAvatar"',
                   'id="heraldTitle"', 'id="heraldStatusLine"', 'id="tHeraldLink"', 'id="heraldLookStat"'];
    let at = -1;
    for (const id of order) {
      const i = card.indexOf(id);
      assert.ok(i > at, id + " is not where the hall puts it");
      at = i;
    }
    assert.ok(/<span class="toggle on" id="tHeraldLink" data-t/.test(card), "the link switch is not on by default");
    for (const [id, key] of [["heraldName", "herald.look.name.label"], ["heraldAvatar", "herald.look.avatar.label"],
                             ["heraldTitle", "herald.look.title.label"], ["heraldStatusLine", "herald.look.status.label"]])
      assert.ok(card.includes('<label for="' + id + '" data-i18n="' + key + '">'), id + " has no label");
    assert.ok(card.includes('data-i18n="herald.look.avatar.note"'), "the plain note about public image links is gone");

    const save = APP.slice(APP.indexOf("function heraldSave(extra){"), APP.indexOf("\n}", APP.indexOf("function heraldSave(extra){")));
    assert.ok(save.includes("},heraldLookValues(),extra||{})});"), "heraldSave does not carry the look");
    assert.ok(save.includes("heraldRenderLookLine()"), "a save does not repaint the line");
    assert.ok(/\["tHerald","tHeraldAddr","tHeraldPass","tHeraldEvents","tHeraldLink"\]\.forEach\(id=>\s*\$\("#"\+id\)\.addEventListener\("click",\(\)=>heraldSave\(\)\)\);/.test(APP),
      "the link switch does not save");
    assert.ok(APP.includes('["heraldName","heraldAvatar","heraldTitle","heraldStatusLine"].forEach(id=>{\n  $("#"+id).addEventListener("change",()=>{heraldLookCommit();});'),
      "leaving a box does not commit the look");
    const apply = APP.slice(APP.indexOf("function heraldApply(up){"), APP.indexOf("\n}", APP.indexOf("function heraldApply(up){")));
    assert.ok(apply.includes("heraldApplyLook(up)"), "reading the preferences does not paint the look");
    const refresh = APP.slice(APP.indexOf("async function refreshHerald(){"), APP.indexOf("\n}", APP.indexOf("async function refreshHerald(){")));
    assert.ok(refresh.indexOf("heraldRenderLookLine()") >= 0 && refresh.indexOf("heraldRenderLookLine()") < refresh.indexOf("if(!Native.available) return;"),
      "opening the hall in the browser preview leaves the line under the look empty");
    assert.ok((APP.match(/try\{heraldRenderLookLine\(\);\}catch\(_\)\{\}/g) || []).length >= 3,
      "a language switch does not redraw the line under the look");

    const table = APP.slice(APP.indexOf("const HOST_SENTENCES=["), APP.indexOf("];", APP.indexOf("const HOST_SENTENCES=[")));
    assert.ok(table.includes('{named:"userprefs.save.badAvatarUrl",   textId:"herald.look.avatar.refused"}'));
    assert.ok(table.includes('{named:"userprefs.save.badBotName",     textId:"herald.look.name.refused"}'));

    for (const id of ["herald.look.label", "herald.look.name.label", "herald.look.name.placeholder",
                      "herald.look.avatar.label", "herald.look.avatar.placeholder", "herald.look.avatar.note",
                      "herald.look.title.label", "herald.look.title.placeholder", "herald.look.status.label",
                      "herald.look.status.placeholder", "herald.look.link", "herald.look.link.title",
                      "herald.look.avatar.refused", "herald.look.name.refused", "herald.look.preview",
                      "herald.look.preview.off", "host.discord.author"])
      assert.ok(KEYS[id] && typeof KEYS[id].lore === "string", "the catalog has no " + id);

    /* The host side reads and writes every one, and refuses before it writes. */
    for (const key of ["DiscordBotName", "DiscordAvatarUrl", "DiscordStatusLine", "DiscordTitle", "DiscordLinkToBakaLoader"]) {
      assert.ok(BRIDGE.includes('Apply("' + key + '"'), "userprefs.save does not write " + key);
      assert.ok(BRIDGE.includes("prefs." + key + ",\n"), "userprefs.get does not hand back " + key);
    }
    const check = BRIDGE.indexOf("CheckHeraldLook(dto);");
    const mutate = BRIDGE.indexOf("UserPrefsProvider.Mutate(current =>", check);
    assert.ok(check > 0 && mutate > check, "the look is not checked before the save writes");
    assert.ok(BRIDGE.includes('HostFacingException("userprefs.save.badAvatarUrl"'));
    assert.ok(BRIDGE.includes('HostFacingException("userprefs.save.badBotName"'));
  });

  if (failures.length) {
    console.log("herald look selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAIL");
    process.exit(1);
  }
  console.log("herald look selftest: all " + passed + " rules pass");
})();
