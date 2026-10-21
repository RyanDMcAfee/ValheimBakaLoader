/* The realm's event posts on the Discord hall, driven for real (1.2.16, issue 24).
 *
 * WHY THIS EXISTS. Until 1.2.16 one "Event posts" switch sent every one-off message from every
 * realm to the app-wide webhook. Now each realm sends three kinds (Server news, Crashes, players
 * coming and going), each with its own switch, webhook and thread, and Crashes can ping one role.
 * The ways this goes wrong quietly are: a realm switch leaving the last realm's routing on screen
 * for the next save to write onto this one, a box the page sends that the host side never reads
 * (or reads under another name), the wizard's single switch flattening a host's careful choice,
 * and the old switch lingering somewhere so a save writes a value nothing honours.
 *
 * Since 1.2.17 each kind is a tick on the one card and a refusal Discord gave for a forum with no
 * thread is a line under its kind. Since the 1.2.17 follow-up the webhook and thread of each kind
 * (and the crash role) roll in right under its tick while it is ticked, and keep what was typed
 * while hidden; the Send these somewhere else dialog that held them is gone.
 *
 * THE CODE UNDER TEST is the block between the HERALD-EVENTS markers in app.js, evaluated here
 * with the page's own globals stood in for (the tick helpers come out of app.js itself). The
 * sentences come out of the real en.json.
 *
 * The rules:
 *   1. The three kinds paint from the realm's block, and a save reads the same controls back.
 *   2. A realm with nothing read yet has every kind off and every box blank.
 *   3. The wizard's one switch moves all three kinds only when the host moved it.
 *   4. The screen: every tick on the card and every box in its dialog with their words in the
 *      catalog, the role box only with crashes, the forum warning per kind, every tick and box
 *      saves, and nothing of the old single switch is left.
 *   5. The seam: every key the page sends is one the host side applies and hands back, and the
 *      refusals are worded in the page's table with the catalog's own English.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/herald_events_selftest.js
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

function block() {
  const from = APP.indexOf("/* HERALD-EVENTS-BEGIN");
  const to = APP.indexOf("/* HERALD-EVENTS-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the HERALD-EVENTS markers");
  return APP.slice(from, to);
}

/* One top-level function of app.js, by its opening line, up to the brace that closes it. */
function fn(head) {
  const at = APP.indexOf(head);
  assert.ok(at >= 0, "app.js has no " + head);
  return APP.slice(at, APP.indexOf("\n}", at) + 2);
}

const HOOK_B = "https://discord.com/api/webhooks/222/bbb";
const HOOK_C = "https://discord.com/api/webhooks/333/ccc";
const SWITCHES = ["tHeraldRoutine", "tHeraldCrash", "tHeraldPlayers"];
const BOXES = ["heraldRoutineUrl", "heraldRoutineThread", "heraldCrashUrl", "heraldCrashThread", "heraldCrashRole",
               "heraldPlayersUrl", "heraldPlayersThread"];

function switchEl() {
  const listeners = {};
  return { checked: false, addEventListener: (ev, f) => { listeners[ev] = f; }, listeners };
}
function boxEl() {
  const listeners = {};
  return { value: "", addEventListener: (ev, f) => { listeners[ev] = f; }, listeners };
}

function hall() {
  const el = {};
  SWITCHES.forEach(id => { el["#" + id] = switchEl(); });
  BOXES.forEach(id => { el["#" + id] = boxEl(); });
  /* Each kind's block of boxes, hidden in the markup until its tick is ticked. */
  ["heraldRoutineMore", "heraldCrashMore", "heraldPlayersMore"].forEach(id => { el["#" + id] = { style: { display: "none" } }; });
  ["heraldRoutineWarn", "heraldCrashWarn", "heraldPlayersWarn"].forEach(id => { el["#" + id] = { style: { display: "none" }, textContent: "" }; });
  const saves = [];
  const context = {
    $: sel => el[sel] || null,
    T: id => { if (!KEYS[id]) throw new Error("the catalog has no sentence for " + id); return KEYS[id].lore; },
    heraldSave: () => { saves.push(JSON.parse(JSON.stringify(context.heraldEventValues()))); return Promise.resolve({}); },
  };
  vm.createContext(context);
  /* The page's one show/hide helper (REVEAL), as the page has it: a stand-in element has no layout, so it is set at once. */
  vm.runInContext(APP.slice(APP.indexOf("/* REVEAL-BEGIN"), APP.indexOf("/* REVEAL-END */")), context, { filename: "app.js#reveal" });
  for (const head of ["function heraldTick(id,on){", "function heraldTicked(id){"]) vm.runInContext(fn(head), context);
  vm.runInContext(block(), context, { filename: "app.js#herald-events" });
  for (const name of ["heraldRenderEvents", "heraldEventValues", "heraldEventsAllOn", "heraldEventsFromWizard", "heraldWireEvents"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  const on = id => el["#" + id].checked;
  return { context, el, saves, on };
}

const BLOCK = {
  Realm: "Final Sunset",
  RoutineOn: true, RoutineWebhookUrl: HOOK_B, RoutineThreadId: "700",
  CrashOn: true, CrashWebhookUrl: HOOK_C, CrashThreadId: null, CrashRoleId: "123456789012345678",
  PlayersOn: false, PlayersWebhookUrl: null, PlayersThreadId: "900",
};

test("1. the three kinds paint from the realm's block, and a save reads them back", () => {
  const h = hall();
  h.context.heraldRenderEvents(BLOCK);
  assert.ok(h.on("tHeraldRoutine") && h.on("tHeraldCrash") && !h.on("tHeraldPlayers"));
  assert.strictEqual(h.el["#heraldRoutineUrl"].value, HOOK_B);
  assert.strictEqual(h.el["#heraldCrashRole"].value, "123456789012345678");
  assert.strictEqual(h.el["#heraldCrashThread"].value, "", "a null box does not paint blank");
  assert.deepStrictEqual(JSON.parse(JSON.stringify(h.context.heraldEventValues())), {
    RoutineOn: true, RoutineWebhookUrl: HOOK_B, RoutineThreadId: "700",
    CrashOn: true, CrashWebhookUrl: HOOK_C, CrashThreadId: "", CrashRoleId: "123456789012345678",
    PlayersOn: false, PlayersWebhookUrl: "", PlayersThreadId: "900",
  });
  /* The boxes are trimmed on the way out, as the realm's own are. */
  h.el["#heraldPlayersUrl"].value = "  " + HOOK_C + " ";
  assert.strictEqual(h.context.heraldEventValues().PlayersWebhookUrl, HOOK_C);
});

test("2. a realm with nothing read yet has every kind off and every box blank", () => {
  const h = hall();
  h.context.heraldRenderEvents(BLOCK);
  h.context.heraldRenderEvents({ Realm: "New Realm" });
  SWITCHES.forEach(id => assert.ok(!h.on(id), id + " kept the last realm's switch"));
  BOXES.forEach(id => assert.strictEqual(h.el["#" + id].value, "", id + " kept the last realm's box"));
  h.context.heraldRenderEvents(null);
  assert.ok(!h.context.heraldEventsAllOn());
});

test("3. the wizard's one switch moves all three only when the host moved it", () => {
  const h = hall();
  h.context.heraldRenderEvents({ CrashOn: true });
  const was = h.context.heraldEventsAllOn();
  assert.strictEqual(was, false);
  /* Left as it opened: Crashes alone stays on. */
  assert.strictEqual(h.context.heraldEventsFromWizard(was, was), false);
  assert.ok(h.on("tHeraldCrash") && !h.on("tHeraldRoutine") && !h.on("tHeraldPlayers"));
  /* Switched on: all three. */
  assert.strictEqual(h.context.heraldEventsFromWizard(false, true), true);
  assert.ok(h.context.heraldEventsAllOn());
  /* Switched off: all three. */
  h.context.heraldEventsFromWizard(true, false);
  SWITCHES.forEach(id => assert.ok(!h.on(id)));
  /* And the wizard opens on that answer and finishes through it. */
  const wiz = fn("function heraldWizard(){");
  assert.ok(/events:heraldEventsAllOn\(\),eventsWas:heraldEventsAllOn\(\)/.test(wiz), "the wizard does not open on the realm's kinds");
  const finish = fn("async function heraldWizFinish(){");
  assert.ok(finish.includes("heraldEventsFromWizard(HWIZ.eventsWas,HWIZ.events)"), "the wizard's switch does not reach the kinds");
});

test("4. the screen: every tick on the card with its own boxes right under it, its words in the catalog, every one saves", () => {
  const card = HTML.slice(HTML.indexOf('<div class="card b-herald">'), HTML.indexOf('id="heraldPreview"'));
  assert.ok(card.length > 0, "the realm's card is gone");
  /* 1.2.17 follow-up: no dialog. Each tick is followed by its own block (hidden until ticked)
     holding its webhook and thread, and for crashes the role, before the next tick. */
  SWITCHES.forEach(id => assert.ok(card.includes('<input type="checkbox" id="' + id + '"'), id + " is not a tick"));
  assert.ok(!/heraldElsewhere/.test(HTML), "the Send these somewhere else dialog is still on the screen");
  const kinds = [["tHeraldRoutine", "heraldRoutineMore", ["heraldRoutineUrl", "heraldRoutineThread"]],
                 ["tHeraldCrash", "heraldCrashMore", ["heraldCrashUrl", "heraldCrashThread", "heraldCrashRole"]],
                 ["tHeraldPlayers", "heraldPlayersMore", ["heraldPlayersUrl", "heraldPlayersThread"]]];
  kinds.forEach(([sw, more, boxes], i) => {
    const tick = card.indexOf('id="' + sw + '"'), block = card.indexOf('<div id="' + more + '" class="herald-sub" style="display:none">');
    const next = i + 1 < kinds.length ? card.indexOf('id="' + kinds[i + 1][0] + '"') : card.length;
    assert.ok(tick > 0 && block > tick && block < next, more + " is not right under " + sw + " and hidden until ticked");
    boxes.forEach(id => { const at = card.indexOf('id="' + id + '"'); assert.ok(at > block && at < next, id + " is not under " + sw); });
  });
  const ids = [...card.matchAll(/data-i18n(?:-title|-placeholder|-aria)?="([^"]+)"/g)].map(m => m[1]);
  ["herald.events.label", "herald.events.routine.tick", "herald.events.crash.tick", "herald.events.players.tick",
   "herald.events.routine", "herald.events.routine.title",
   "herald.events.crash", "herald.events.crash.title", "herald.events.players", "herald.events.players.title",
   "herald.events.webhook.placeholder", "herald.events.thread.placeholder", "herald.events.role.placeholder",
   "herald.events.role.title", "herald.events.role.label"].forEach(id => {
    assert.ok(ids.includes(id), id + " is not on the card");
    assert.ok(KEYS[id] && typeof KEYS[id].lore === "string", "the catalog has no " + id);
  });
  ids.forEach(id => assert.ok(KEYS[id], "the card asks for " + id + ", which the catalog lacks"));
  assert.ok(!/herald\.share\.events\.note/.test(HTML + APP), "the retired from-every-realm note is still shown");

  /* Each kind's boxes follow its tick, and a forum refusal is one line under its kind. */
  const r = hall();
  r.context.heraldRenderEvents({ CrashOn: false });
  ["heraldRoutineMore", "heraldCrashMore", "heraldPlayersMore"].forEach(id =>
    assert.strictEqual(r.el["#" + id].style.display, "none", id + " shows with its kind off"));
  r.context.heraldRenderEvents({ CrashOn: true, PlayersNoThread: true, CrashWebhookUrl: HOOK_C });
  assert.strictEqual(r.el["#heraldCrashMore"].style.display, "", "the crash boxes hide with crashes on");
  assert.strictEqual(r.el["#heraldRoutineMore"].style.display, "none");
  assert.strictEqual(r.el["#heraldPlayersWarn"].style.display, "");
  assert.strictEqual(r.el["#heraldPlayersWarn"].textContent, "ᚦ " + KEYS["herald.events.nothread"].lore);
  assert.strictEqual(r.el["#heraldCrashWarn"].style.display, "none", "a kind Discord did not refuse shows a warning");
  r.context.heraldWireEvents();
  r.el["#tHeraldCrash"].checked = false;
  r.el["#tHeraldCrash"].listeners.change();
  assert.strictEqual(r.el["#heraldCrashMore"].style.display, "none", "unticking crashes left its boxes up");
  /* Hidden is not forgotten: the boxes keep what was typed and the save still carries it. */
  assert.strictEqual(r.el["#heraldCrashUrl"].value, HOOK_C, "unticking cleared the crash webhook");
  assert.strictEqual(r.context.heraldEventValues().CrashWebhookUrl, HOOK_C, "a hidden kind's webhook is not saved");

  /* Every tick saves when it changes and every box when it is left. */
  const h = hall();
  h.context.heraldWireEvents();
  SWITCHES.forEach(id => { assert.ok(h.el["#" + id].listeners.change, id + " does not save"); h.el["#" + id].listeners.change(); });
  BOXES.forEach(id => { assert.ok(h.el["#" + id].listeners.change, id + " does not save"); h.el["#" + id].listeners.change(); });
  assert.strictEqual(h.saves.length, SWITCHES.length + BOXES.length);
  assert.ok(/\n\/\* HERALD-EVENTS-END \*\/\nheraldWireEvents\(\);/.test(APP), "the block's controls are never wired");

  /* The save carries them inside the realm's block, and a realm switch repaints them. */
  const save = fn("function heraldSave(extra,realmExtra){");
  assert.ok(save.includes("const realm=heraldDefined(Object.assign(heraldRealmValues(),heraldEventValues(),realmExtra||{}));") && save.includes("Herald:realm"),
    "the save does not carry the kinds in the realm's block");
  assert.ok(fn("function heraldRenderRealm(){").includes("heraldRenderEvents(b)"), "a realm switch does not repaint the kinds");

  /* Nothing of the old single switch is left to save a value nothing honours. */
  assert.ok(!/tHeraldEvents|DiscordEventPosts/.test(APP), "app.js still carries the old Event posts switch");
  assert.ok(!/tHeraldEvents/.test(HTML), "the page still carries the old Event posts switch");
});

test("5. the seam: every key the page sends is applied and handed back, refusals worded", () => {
  const h = hall();
  const sent = Object.keys(h.context.heraldEventValues());
  assert.strictEqual(sent.length, 10);
  const apply = BRIDGE.slice(BRIDGE.indexOf("public static bool ApplyHeraldBlock("), BRIDGE.indexOf("public static void CheckHeraldEvents("));
  const dto = BRIDGE.slice(BRIDGE.indexOf("public static List<object> BuildHeraldRealmsDto("), BRIDGE.indexOf("private string HeraldRealmOf("));
  sent.forEach(key => {
    assert.ok(new RegExp('(Switch|Text)\\("' + key + '"').test(apply), "the host side never applies " + key);
    assert.ok(new RegExp("\\b" + key + " = r\\.Herald" + key + "\\b").test(dto), "the host side never hands back " + key);
  });
  assert.ok(!/Apply\("DiscordEventPosts"/.test(BRIDGE), "a save still writes the old switch");

  /* The refusals: the page's table names each, and the host side throws the catalog's English. */
  for (const [named, textId] of [["userprefs.save.badEventWebhook", "herald.events.hook.refused"],
                                 ["userprefs.save.badCrashRole", "herald.events.role.refused"]]) {
    const row = APP.indexOf('{named:"' + named + '",');
    assert.ok(row > 0 && /^\{named:"[^"]+",\s*textId:"([^"]+)"\}/.exec(APP.slice(row))[1] === textId,
      named + " has no row in HOST_SENTENCES");
    const at = BRIDGE.indexOf('new HostFacingException("' + named + '",');
    assert.ok(at > 0, "the host side never refuses with " + named);
    const english = BRIDGE.slice(at).match(/,\s*"([^"]+)"\)/)[1];
    assert.strictEqual(english, KEYS[textId].lore, named + " is not thrown with the catalog's English");
  }
  assert.ok(/CheckHeraldEvents\(dto\.TryGetValue\("Herald"/.test(BRIDGE), "the save does not check the kinds before writing");
});

console.log(failures.length === 0
  ? "herald events selftest: all " + passed + " rules pass"
  : "herald events selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAIL");
process.exit(failures.length === 0 ? 0 : 1);
