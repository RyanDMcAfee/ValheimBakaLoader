/* The Discord hall's preview card, driven for real (1.2.16, issue 24).
 *
 * WHY THIS EXISTS. The hall shows the realm's post as Discord would draw it, under "The post",
 * from discord.preview, which hands back the JSON a publish would send now. The ways this goes
 * wrong quietly are: a name, title or field the host typed reaching the page as markup, a reply
 * for the realm the host just switched away from painting over the realm now on screen, an edit
 * in the hall that never asks again so the card shows a post that is no longer the one sent, and
 * a key the page reads that the host side never sends.
 *
 * THE CODE UNDER TEST is the block between the HERALD-PREVIEW markers in app.js, evaluated here
 * with the page's own globals stood in for. The sentences come out of the real en.json.
 *
 * The rules:
 *   1. A status post draws its name, avatar letter, colour bar, title, every field and footer,
 *      and everything the host or Discord wrote is escaped.
 *   2. The bits of Discord's markdown the posts use are drawn, and nothing else becomes markup.
 *   3. A sample of each kind of event post, with its catalog label and where it goes.
 *   4. Only the newest ask paints, and only while its realm is on screen; every save in the hall,
 *      a realm switch and a publish ask again.
 *   5. The seam: the page reads only what discord.preview sends, its words are in the catalog,
 *      and the remove toast tells a forum post from a channel one.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/herald_preview_selftest.js
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
  const from = APP.indexOf("/* HERALD-PREVIEW-BEGIN");
  const to = APP.indexOf("/* HERALD-PREVIEW-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the HERALD-PREVIEW markers");
  return APP.slice(from, to);
}

/* One top-level function of app.js, by its opening line, up to the brace that closes it. */
function fn(head) {
  const at = APP.indexOf(head);
  assert.ok(at >= 0, "app.js has no " + head);
  return APP.slice(at, APP.indexOf("\n}", at) + 2);
}

/* The page's own esc, taken from app.js rather than written again here. */
const ESC_LINE = APP.split("\n").find(l => l.startsWith("const esc=s=>"));

const USED = new Set();
function T(id, params) {
  USED.add(id);
  const k = KEYS[id];
  assert.ok(k && typeof k.lore === "string", "the catalog has no " + id);
  return k.lore.replace(/\{(\w+)\}/g, (m, p) => (params && p in params ? String(params[p]) : m));
}

function hall(opts) {
  opts = opts || {};
  const el = { "#heraldPreviewPost": { innerHTML: "" }, "#heraldPreviewEvents": { innerHTML: "" }, "#heraldPreviewNote": { textContent: "" } };
  const asks = [];
  const context = {
    $: sel => el[sel] || null,
    T,
    FAIL: { fail: true },
    Native: { available: opts.native !== false },
    HERALD_REALM: "Final Sunset",
    heraldRealmOnScreen: () => context.HERALD_REALM,
    heraldRealmKey: n => String(n || "").toLowerCase(),
    rpc: (method, params) => new Promise(resolve => asks.push({ method, params, resolve })),
    Promise,
    Date,
    LOC: () => "en",
  };
  vm.createContext(context);
  vm.runInContext(ESC_LINE + "\nthis.esc=esc;\n" + block() + "\nthis.HERALD_PREVIEW_KINDS=HERALD_PREVIEW_KINDS;", context, { filename: "app.js#herald-preview" });
  for (const name of ["heraldPreviewText", "heraldPreviewCard", "heraldPreviewPaint", "heraldPreviewRefresh"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  return { context, el, asks };
}

const STATUS = {
  username: "Raven <b>",
  avatar_url: "https://example.org/r.png",
  allowed_mentions: { parse: [] },
  embeds: [{
    title: "Alpha's <hall>",
    description: "Ashlands opens **Friday**",
    color: 0x57F287,
    fields: [
      { name: "Status", value: "Online", inline: true },
      { name: "Join address", value: "`1.2.3.4:2456`", inline: true },
      { name: "Next restart", value: "<t:1800000000:R>", inline: true },
      { name: "Server news", value: "<#123456789012345678>", inline: false },
    ],
    footer: { text: "Valheim BakaLoader v1.2.16 · this post updates itself" },
  }],
};

const REPLY = {
  ok: true,
  payload: STATUS,
  events: [
    { kind: "Routine", on: true, thread: "123456789012345678", payload: { embeds: [{ title: "Server Started", description: "**Alpha** is now online.", color: 0x57F287 }] } },
    { kind: "Crash", on: true, thread: null, payload: { content: "<@&99999999>", allowed_mentions: { parse: [], roles: ["99999999"] }, embeds: [{ title: "Server Crashed", color: 0xED4245 }] } },
    { kind: "Players", on: false, thread: null, payload: { embeds: [{ title: "Player Joined", color: 0x3498DB }] } },
  ],
};

(async () => {
  await test("1. a status post draws name, avatar, colour bar, title, every field and footer, all escaped", () => {
    const h = hall();
    const html = h.context.heraldPreviewCard(STATUS);
    assert.ok(html.includes('class="dc-name">Raven &lt;b&gt;</span>'), "the name is not drawn escaped");
    assert.ok(html.includes('class="dc-ava">R</div>'), "the avatar is not the name's first letter");
    assert.ok(html.includes("border-left-color:#57f287"), "the colour bar is not the embed's colour");
    assert.ok(html.includes("Alpha&#39;s &lt;hall&gt;"), "the title is not drawn escaped");
    STATUS.embeds[0].fields.forEach(f => assert.ok(html.includes(">" + f.name + "<"), f.name + " is not drawn"));
    assert.ok(html.includes('class="dc-field full"'), "a field that is not inline does not take the whole row");
    assert.ok(html.includes("this post updates itself"), "the footer is not drawn");
    assert.ok(!html.includes("<b>") && !html.includes("<hall>"), "something the host typed became markup");
    assert.ok(!/<img|url\(/.test(html), "the card fetches a picture");
    /* No name: the webhook's own, worded by the catalog. */
    assert.ok(h.context.heraldPreviewCard({ embeds: [{}] }).includes(h.context.esc(T("herald.preview.name.default"))));
  });

  await test("2. Discord's markdown the posts use is drawn, and nothing else becomes markup", () => {
    const h = hall();
    const t = h.context.heraldPreviewText;
    assert.strictEqual(t("**Alpha** joined"), "<strong>Alpha</strong> joined");
    assert.strictEqual(t("`1.2.3.4:2456`"), "<code>1.2.3.4:2456</code>");
    assert.strictEqual(t("<#123456789012345678>"), '<span class="dc-mention">#123456789012345678</span>');
    assert.strictEqual(t("<@&99999999>"), '<span class="dc-mention">@99999999</span>');
    assert.ok(!t("<t:1800000000:R>").includes("&lt;t:"), "a Discord timestamp is left raw");
    assert.strictEqual(t("<img src=x onerror=alert(1)>"), "&lt;img src=x onerror=alert(1)&gt;");
    assert.strictEqual(t("**<script>**"), "<strong>&lt;script&gt;</strong>");
    assert.strictEqual(t(null), "");
  });

  await test("3. a sample of each kind of event post, with its label and where it goes", () => {
    const h = hall();
    h.context.heraldPreviewPaint(REPLY);
    const ev = h.el["#heraldPreviewEvents"].innerHTML;
    assert.ok(ev.includes(T("herald.events.routine")) && ev.includes(T("herald.events.crash")) && ev.includes(T("herald.events.players")));
    assert.ok(ev.includes(T("herald.preview.into_thread", { thread: "123456789012345678" })), "routine does not say its thread");
    assert.ok(ev.includes(T("herald.preview.into_channel")), "crash does not say it goes to the channel");
    assert.ok(ev.includes(T("herald.preview.off")), "players does not say it is off");
    assert.ok(ev.includes('class="dc-content"><span class="dc-mention">@99999999</span>'), "the crash ping is not drawn");
    assert.strictEqual((ev.match(/class="dc-msg"/g) || []).length, 3);
    assert.ok(h.el["#heraldPreviewPost"].innerHTML.includes("dc-embed"));
    /* A failed ask clears the card rather than leaving another realm's post up. */
    h.context.heraldPreviewPaint(null);
    assert.strictEqual(h.el["#heraldPreviewPost"].innerHTML, "");
    assert.strictEqual(h.el["#heraldPreviewEvents"].innerHTML, "");
  });

  await test("4. only the newest ask paints, only for the realm on screen; every edit asks again", async () => {
    const h = hall();
    const first = h.context.heraldPreviewRefresh();
    assert.strictEqual(h.asks.length, 1);
    assert.deepStrictEqual(JSON.parse(JSON.stringify(h.asks[0])).params, { profile: "Final Sunset" });
    assert.strictEqual(h.asks[0].method, "discord.preview");
    /* The host switches realm before the reply lands: it is dropped. */
    h.context.HERALD_REALM = "Other";
    h.asks[0].resolve(REPLY);
    assert.strictEqual(await first, false);
    assert.strictEqual(h.el["#heraldPreviewPost"].innerHTML, "");
    /* Two asks for the same realm: the older reply is dropped, the newer paints. */
    const a = h.context.heraldPreviewRefresh(), b = h.context.heraldPreviewRefresh();
    h.asks[2].resolve(REPLY);
    h.asks[1].resolve({ ok: true, payload: { embeds: [{ title: "stale" }] }, events: [] });
    assert.strictEqual(await b, true);
    assert.strictEqual(await a, false);
    assert.ok(!h.el["#heraldPreviewPost"].innerHTML.includes("stale"));
    /* Outside the app: nothing asked, and the card stays empty (hidden by its CSS). */
    const off = hall({ native: false });
    assert.strictEqual(await off.context.heraldPreviewRefresh(), false);
    assert.strictEqual(off.asks.length, 0);

    assert.ok(fn("function heraldSave(extra,realmExtra){").includes("heraldPreviewRefresh()"), "a save on the screen does not ask again");
    assert.ok(fn("function heraldRenderRealm(){").includes("heraldPreviewRefresh()"), "a realm switch does not ask again");
    assert.ok(fn("async function heraldPublish(){").includes("heraldPreviewRefresh()"), "a publish does not ask again");
  });

  await test("5. the seam: what the page reads is what discord.preview sends; words in the catalog", () => {
    const at = BRIDGE.indexOf('RegisterRpc("discord.preview"');
    assert.ok(at >= 0, "the host side has no discord.preview");
    const rpc = BRIDGE.slice(at, BRIDGE.indexOf("// --- Custom join domain", at));
    assert.ok(rpc.includes("DiscordStatus.Preview(realm)"), "discord.preview does not ask the status service");
    ["ok =", "payload =", "events =", "kind =", "on =", "thread ="].forEach(k => assert.ok(rpc.includes(k), "discord.preview does not send " + k));
    const b = block();
    ["r.ok", "r.payload", "r.events", "ev.kind", "ev.on", "ev.thread", "ev.payload"].forEach(k => assert.ok(b.includes(k), "the page does not read " + k));
    /* Every word the block asks for is in the catalog, the kinds' labels included. */
    [...b.matchAll(/T\("([a-z0-9_.]+)"/g)].forEach(m => assert.ok(KEYS[m[1]], "the block asks for " + m[1] + ", which the catalog lacks"));
    Object.values(hall().context.HERALD_PREVIEW_KINDS).forEach(id => assert.ok(KEYS[id], id));
    /* The preview sits on the one card (1.2.17), above Publish and Remove, with its words keyed.
       Its note became the (?) beside the Preview heading, so it is said once, on hover or focus. */
    const pv = HTML.indexOf('<div class="dc-preview" id="heraldPreview">');
    assert.ok(pv > HTML.indexOf('<div class="card b-herald">'), "heraldPreview is not on the card");
    const card = HTML.slice(pv, HTML.indexOf('id="heraldPublish"', pv));
    ["heraldPreview", "heraldPreviewPost", "heraldPreviewEvents", "heraldHelpPreview"].forEach(id => assert.ok(card.includes('id="' + id + '"'), id + " is not with the preview"));
    assert.ok(!HTML.includes('id="heraldPreviewNote"'), "the note is said twice: under the heading and in its (?)");
    ["herald.preview.label", "herald.preview.events"].forEach(id => {
      assert.ok(card.includes('data-i18n="' + id + '"'), id + " is not on the card");
      assert.ok(KEYS[id], "the catalog has no " + id);
    });
    assert.ok(APP.includes('{marker:"heraldHelpPreview",textId:"herald.preview.note"}'), "the preview's (?) does not carry its note");
    assert.ok(KEYS["herald.preview.note"], "the catalog has no herald.preview.note");
    /* Remove tells a forum post, which Discord keeps, from a channel post. */
    assert.ok(BRIDGE.includes("forum = result.InForum"), "discord.remove does not say whether the post was a forum post");
    assert.ok(fn("async function heraldRemove(){").includes('(r.forum?T("herald.remove.done.forum.toast"):T("herald.remove.done.toast"))'), "the remove toast does not tell a forum post apart");
    assert.ok(KEYS["herald.remove.done.forum.toast"], "the catalog has no forum remove sentence");
  });

  console.log(failures.length ? "herald preview selftest: " + failures.length + " FAILED, " + passed + " passed"
                              : "herald preview selftest: all " + passed + " rules hold");
  process.exit(failures.length ? 1 : 0);
})();
