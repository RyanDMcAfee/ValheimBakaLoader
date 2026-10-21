/* The Discord screen of 1.2.17, driven for real: one card about the realm on screen.
 *
 * WHY THIS EXISTS. The owner called the screen before it "confusing": an app-wide switch and
 * webhook beside a realm's own, two cards that both talked about webhooks, and every kind of
 * message carrying its own webhook and thread boxes on the main screen. 1.2.17 makes it one card
 * in the order a host reads it, moves the rarely touched parts into two dialogs, and puts a (?)
 * on every heading. The ways this goes wrong quietly are: a control drifting back onto the card
 * (or out of the order), a heading losing its (?) or a (?) that only a mouse can open, a dialog
 * whose boxes are not the ones the saves read, and a dialog closed by Escape or a click outside
 * it losing what was typed, or keeping its panel so the next save cannot see the boxes.
 *
 * THE CODE UNDER TEST is the block between the HERALD-DIALOGS markers in app.js, evaluated here
 * with the page's own globals stood in for, and the markup in index.html. The sentences come out
 * of the real en.json.
 *
 * The rules:
 *   1. The card, top to bottom: the head, the realm's switch, the webhook line, Show in the post
 *      (the mod code's kind and box in a block under its tick), Also send messages when (each
 *      kind's webhook and thread, and the crash role, in a block under its own tick; the Send
 *      these somewhere else dialog left in the 1.2.17 follow-up), the preview, Publish, Remove
 *      and Appearance; and nothing app wide on it.
 *   2. Every heading and the role box has a (?): the shared hover panel, opened by the mouse and
 *      by keyboard focus, with one or two sentences out of the catalog.
 *   3. Appearance opens the app's dialog with its panel in it and asks to fade out, and however
 *      the dialog goes, the panel goes home and what was typed is saved.
 *   4. The Appearance panel holds the boxes the saves read and waits hidden at the end of the
 *      screen; the retired dialog's sentences stay in the catalog, retired, asked for by nothing.
 *   5. Nothing on the screen appears, vanishes or jumps: every show and hide goes through
 *      reveal (rolled), revealText (faded) or revealSwap (the preview), and the hidden boxes
 *      are still saved.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/herald_screen_selftest.js
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

function T(id, params) {
  const entry = KEYS[id];
  if (!entry || typeof entry.lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return entry.lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

function block() {
  const from = APP.indexOf("/* HERALD-DIALOGS-BEGIN");
  const to = APP.indexOf("/* HERALD-DIALOGS-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the HERALD-DIALOGS markers");
  return APP.slice(from, to);
}

const PAGE = HTML.slice(HTML.indexOf('<section class="page" id="page-herald"'), HTML.indexOf("<!-- ============ PAGE: SKALD"));
const CARD = PAGE.slice(PAGE.indexOf('<div class="card b-herald">'), PAGE.indexOf('<div id="heraldPanels"'));

/* A node that knows its parent, enough for the panels to be lent and put back. */
function node(name) {
  const n = { name, parentNode: null, children: [], listeners: {} };
  n.appendChild = child => {
    if (child.parentNode) child.parentNode.children = child.parentNode.children.filter(c => c !== child);
    child.parentNode = n;
    n.children.push(child);
    return child;
  };
  n.addEventListener = (ev, f) => { n.listeners[ev] = f; };
  return n;
}

function screen() {
  const home = node("home");
  const look = node("look");
  home.appendChild(look);
  const markers = {};
  ["heraldHelpOn", "heraldHelpShow", "heraldHelpEvents", "heraldHelpRole", "heraldHelpPreview"].forEach(id => { markers["#" + id] = node(id); });
  const el = Object.assign({ "#heraldPanels": home, "#heraldLookPanel": look }, markers);
  const log = { modals: [], dismissed: [], commits: [], wired: [], tips: [] };
  let modal = null;
  const context = {
    $: sel => el[sel] || null,
    T,
    esc: s => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;"),
    HERALD_REALM: "Final Sunset",
    heraldRealmOnScreen: () => "Final Sunset",
    modalOpen: (html, again) => {
      const slot = node("slot"), done = node("done");
      modal = { html, again, slot, done, closed: false };
      log.modals.push(modal);
      modal.box = { dataset: {}, querySelector: q => (q === "[data-herald-slot]" ? slot : q === "[data-herald-done]" ? done : null) };
      return modal.box;
    },
    modalClose: () => { if (modal) modal.closed = true; },
    onModalDismissed: fn => log.dismissed.push(fn),
    heraldLookCommit: () => { log.commits.push("look"); return Promise.resolve(true); },
    heraldSave: () => { log.commits.push("save"); return Promise.resolve({}); },
    wireWgTipMarker: (marker, control, open) => log.wired.push({ marker, control, open }),
    wgTipOpenHtml: (marker, html) => log.tips.push({ marker, html }),
  };
  vm.createContext(context);
  vm.runInContext(block(), context, { filename: "app.js#herald-dialogs" });
  for (const name of ["heraldPanelsHome", "heraldDialog", "heraldWireHelp", "heraldHelpHtml"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  return { context, el, log, home, look, markers, current: () => modal };
}

test("1. the card, top to bottom, and nothing app wide on it", () => {
  assert.ok(CARD.length > 0, "the Discord card is gone");
  assert.strictEqual((PAGE.match(/<div class="card /g) || []).length, 1, "the Discord screen has more than one card");
  const order = ['id="heraldRealmName"', 'id="heraldOnLabel"', 'id="tHeraldRealm"', 'id="heraldHookLine"', 'id="heraldHookChange"',
                 'data-i18n="herald.show.label"', 'id="tHeraldAddr"', 'id="tHeraldPass"', 'id="tHeraldModCode"',
                 'id="heraldModCodeWrap"', 'id="heraldModKindTs"', 'id="heraldModCode"', 'id="heraldModCodeStat"',
                 'data-i18n="herald.events.label"',
                 'id="tHeraldRoutine"', 'id="heraldRoutineMore"', 'id="heraldRoutineUrl"', 'id="heraldRoutineThread"', 'id="heraldRoutineWarn"',
                 'id="tHeraldCrash"', 'id="heraldCrashMore"', 'id="heraldCrashUrl"', 'id="heraldCrashThread"', 'id="heraldCrashRole"', 'id="heraldCrashWarn"',
                 'id="tHeraldPlayers"', 'id="heraldPlayersMore"', 'id="heraldPlayersUrl"', 'id="heraldPlayersThread"', 'id="heraldPlayersWarn"',
                 'id="heraldPreview"', 'id="heraldPublish"', 'id="heraldRemove"', 'id="heraldLookBtn"'];
  let at = -1;
  for (const mark of order) {
    const i = CARD.indexOf(mark);
    assert.ok(i > at, mark + " is not where the card puts it");
    at = i;
  }
  /* The ticks are ticks; the realm's own switch is the one switch. */
  for (const id of ["tHeraldAddr", "tHeraldPass", "tHeraldModCode", "tHeraldRoutine", "tHeraldCrash", "tHeraldPlayers"])
    assert.ok(CARD.includes('<input type="checkbox" id="' + id + '"'), id + " is not a tick");
  assert.strictEqual((CARD.match(/class="toggle[ "]/g) || []).length, 1, "the card has a switch besides the realm's own");
  /* Nothing app wide: no master, no webhook or thread box, no link anywhere on the card. */
  for (const id of ["tHerald", "heraldUrl", "heraldThread", "heraldRealmUrl", "heraldRealmThread", "heraldWizBtn"])
    assert.ok(!PAGE.includes('id="' + id + '"'), id + " is still on the Discord screen");
  /* 1.2.17 follow-up: no Send these somewhere else dialog. Each kind's boxes (and the mod code's)
     sit in their own block right under the tick, hidden until it is ticked. */
  assert.ok(!/heraldElsewhere/.test(PAGE + APP), "the Send these somewhere else dialog is still on the screen");
  for (const [more, ids] of [["heraldModCodeWrap", ["heraldModKindTs", "heraldModKindGale", "heraldModCode", "heraldModCodeStat"]],
                             ["heraldRoutineMore", ["heraldRoutineUrl", "heraldRoutineThread"]],
                             ["heraldCrashMore", ["heraldCrashUrl", "heraldCrashThread", "heraldCrashRole", "heraldHelpRole"]],
                             ["heraldPlayersMore", ["heraldPlayersUrl", "heraldPlayersThread"]]]) {
    const m = CARD.match(new RegExp('<div id="' + more + '" class="herald-sub" style="display:none">'));
    assert.ok(m, more + " is not a block that starts hidden");
    const from = CARD.indexOf(m[0]);
    let depth = 0, at = from, end = -1;
    const tag = /<(\/?)div\b[^>]*>/g;
    tag.lastIndex = from;
    for (let t; (t = tag.exec(CARD)); ) { depth += t[1] ? -1 : 1; if (depth === 0) { end = t.index; break; } }
    const inner = CARD.slice(from, end);
    ids.forEach(id => assert.ok(inner.includes('id="' + id + '"'), id + " is not under its own tick in " + more));
  }
  assert.ok(!/discord\.com\/api\/webhooks/.test(CARD), "the card shows a webhook link");
  /* The words: the head names the realm, the webhook line names the webhook, never the link. */
  assert.strictEqual(T("herald.screen.head", { name: "Final Sunset" }), "Discord · Final Sunset");
  assert.strictEqual(T("herald.screen.on", { name: "Final Sunset" }), "Post Final Sunset to Discord");
  assert.strictEqual(T("herald.hook.line.named", { name: "Longhouse" }), 'Posting through the "Longhouse" webhook');
  assert.strictEqual(T("herald.hook.line.none"), "Not set up yet");
  assert.strictEqual(T("herald.events.routine.tick"), "the server starts, stops or updates");
  assert.strictEqual(T("herald.show.password"), "Password (trusted channels only)");
});

test("2. every heading and the role box has a (?), by mouse and by keyboard focus", () => {
  const heads = [...CARD.matchAll(/<div class="herald-head"><span class="microlabel" data-i18n="([^"]+)">[^<]*<\/span><button type="button" class="wginfo" id="([^"]+)" aria-controls="wgTip"[^>]*data-i18n-aria="herald\.help\.aria">\?<\/button><\/div>/g)];
  assert.deepStrictEqual(heads.map(m => m[1]), ["herald.show.label", "herald.events.label", "herald.preview.label"],
    "a heading on the card has no (?) beside it");
  assert.ok(/<span class="tl" id="heraldOnLabel"><\/span><button type="button" class="wginfo" id="heraldHelpOn"/.test(CARD), "the realm's switch has no (?)");
  assert.ok(/id="heraldCrashRoleWrap"[\s\S]*?id="heraldCrashRole"[\s\S]*?class="wginfo" id="heraldHelpRole"/.test(CARD), "the role box has no (?)");
  assert.ok(!/<div class="microlabel[^"]*"(?![^>]*heraldRealmName)[^>]*data-i18n="herald\.(show|events)\.label"/.test(CARD), "a heading stands outside its (?) row");
  assert.ok(KEYS["herald.help.aria"], "the (?) has no spoken name in the catalog");

  const s = screen();
  s.context.heraldWireHelp();
  assert.strictEqual(s.log.wired.length, 5, "not every (?) is wired");
  for (const w of s.log.wired) {
    assert.ok(w.marker, "a (?) was wired to nothing");
    w.open();
  }
  assert.strictEqual(s.log.tips.length, 5);
  const said = s.log.tips.map(t => t.html);
  for (const id of ["herald.screen.on.help", "herald.show.help", "herald.events.help", "herald.events.role.help", "herald.preview.note"]) {
    const words = s.context.esc(T(id, { name: "Final Sunset" }));
    assert.ok(said.some(h => h.includes(words)), id + " is not what its (?) says");
    const sentences = T(id, { name: "x" }).split(/(?<=[.!?])\s+/).filter(Boolean);
    assert.ok(sentences.length >= 1 && sentences.length <= 2, id + " is not one or two plain sentences");
  }
  assert.ok(T("herald.events.role.help").includes("Developer Mode") && T("herald.events.role.help").includes("Copy Role ID"),
    "the role box's (?) does not say how to copy a role ID");
  /* The shared panel itself answers focus as well as the mouse. */
  const wire = APP.slice(APP.indexOf("function wireWgTipMarker("), APP.indexOf("\n}", APP.indexOf("function wireWgTipMarker(")));
  assert.ok(wire.includes('addEventListener("focus",open)') && wire.includes('addEventListener("mouseenter",open)'),
    "the shared hover panel no longer opens on keyboard focus");
  assert.ok(/\nheraldWireHelp\(\);/.test(APP), "the (?) markers are never wired");
});

test("3. Appearance lends its panel to the app's dialog, fades out, and however it goes the panel goes home and saves", () => {
  for (const [which, titleId, commit] of [["look", "herald.look.label", "look"]]) {
    const s = screen();
    const panel = s[which];
    s.context.heraldDialog(which);
    const m = s.current();
    assert.ok(m.html.includes(s.context.esc(T(titleId))), which + " has the wrong title");
    assert.strictEqual(panel.parentNode, m.slot, which + "'s panel is not in the dialog");
    assert.strictEqual(m.box.dataset.fadeOut, "1", which + " does not ask to fade out");
    /* Close: the button closes the app's dialog; then whatever closed it, the dismissal puts the
       panel back home and saves what was typed. */
    m.done.listeners.click();
    assert.strictEqual(m.closed, true, "the dialog's button does not close it");
    assert.strictEqual(s.log.dismissed.length, 1, which + " does not watch its own dismissal");
    s.log.dismissed[0]();
    assert.strictEqual(panel.parentNode, s.home, which + "'s panel was not put back");
    assert.deepStrictEqual(s.log.commits, [commit], which + " does not save on the way out");

    /* Escape or a click outside: no button pressed, the same dismissal, the same save. */
    const t = screen();
    t.context.heraldDialog(which);
    t.log.dismissed[0]();
    assert.strictEqual(t[which].parentNode, t.home);
    assert.deepStrictEqual(t.log.commits, [commit], which + " lost what was typed when closed without its button");

    /* A language switch draws the dialog again, with the same panel in it. */
    const u = screen();
    u.context.heraldDialog(which);
    u.current().again();
    assert.strictEqual(u[which].parentNode, u.current().slot, "a redraw left " + which + "'s panel behind");
    assert.strictEqual(u.log.modals.length, 2);
  }
  assert.ok(/\$\("#heraldLookBtn"\)\.addEventListener\("click",\(\)=>heraldDialog\("look"\)\);/.test(APP), "Appearance does not open its dialog");
  assert.ok(!/heraldDialog\("elsewhere"\)/.test(APP), "Send these somewhere else still opens a dialog");
});

test("4. the panels hold the boxes the saves read, hidden at the end of the screen", () => {
  assert.ok(/<div id="heraldPanels" style="display:none">/.test(PAGE), "the panels' home shows on the screen");
  const look = PAGE.slice(PAGE.indexOf('<div id="heraldLookPanel">'));
  for (const id of ["heraldName", "heraldAvatar", "heraldTitle", "heraldStatusLine", "tHeraldLink", "heraldLookStat"])
    assert.ok(look.includes('id="' + id + '"'), id + " is not in Appearance");
  assert.strictEqual((PAGE.slice(PAGE.indexOf('<div id="heraldPanels"')).match(/<div id="herald\w+Panel">/g) || []).length, 1,
    "the panels' home holds a dialog besides Appearance");
  /* The saves read those boxes by id, and leave out a box that is not in the document. */
  const values = APP.slice(APP.indexOf("function heraldRealmValues(){"), APP.indexOf("\n}", APP.indexOf("function heraldRealmValues(){")));
  assert.ok(values.includes('return el?String(el.value||"").trim():undefined;'), "a missing box reads as blank");
  for (const id of ["heraldName", "heraldAvatar", "heraldTitle", "heraldStatusLine"]) assert.ok(values.includes('box("' + id + '")'), id + " is not read by the save");
  const save = APP.slice(APP.indexOf("function heraldSave(extra,realmExtra){"), APP.indexOf("\n}", APP.indexOf("function heraldSave(extra,realmExtra){")));
  assert.ok(save.includes("heraldDefined("), "the save sends undefined boxes as keys");
  for (const id of ["herald.look.button", "herald.look.label"])
    assert.ok(KEYS[id] && typeof KEYS[id].lore === "string", "the catalog has no " + id);
  /* The dialog's words are released ids: kept in the catalog, retired, asked for by nothing. */
  const RETIRED = fs.readFileSync(path.join(path.dirname(WEB), "..", "scripts", "i18n", "check_catalog.py"), "utf8");
  for (const id of ["herald.elsewhere.button", "herald.elsewhere.title", "herald.elsewhere.note"]) {
    assert.ok(KEYS[id], id + " was deleted from the catalog");
    assert.ok(RETIRED.includes('"' + id + '": "1.2.17"'), id + " is not retired");
    assert.ok(!(PAGE + APP).includes(id), id + " is still asked for");
  }
  assert.ok(!/\u2014|\u2013/.test(Object.entries(KEYS).filter(([k]) => /^herald\.(screen|hook\.line|show|elsewhere|help|wiz\.(same|own|thread))/.test(k)).map(([, v]) => v.lore).join(" ")),
    "a new sentence carries a long dash");
});

test("5. nothing on the screen appears, vanishes or jumps: every show and hide goes through reveal", () => {
  const from = APP.indexOf("let HERALD_HAS_POST"), to = APP.indexOf("/* HERALD-DIALOGS-END */");
  assert.ok(from > 0 && to > from, "the Discord screen's code moved");
  const herald = APP.slice(from, to);
  assert.ok(!/\.style\.display\s*=/.test(herald), "a part of the Discord screen still pops in or out with style.display");
  const body = head => { const at = APP.indexOf(head); assert.ok(at >= 0, "app.js has no " + head); return APP.slice(at, APP.indexOf("\n}", at)); };
  assert.ok(body("function heraldRenderModTick(on){").includes('reveal($("#heraldModCodeWrap"),!!on)'), "the mod code's block does not roll");
  assert.ok(body("function heraldRenderKindBoxes(){").includes("reveal($(\"#\"+k.more),heraldTicked(k.sw))"), "a kind's boxes do not roll");
  assert.ok(body("function heraldRenderWarns(b){").includes("reveal(w,show)"), "a forum warning line pops in");
  assert.ok(body("function heraldRenderRealmLines(){").includes("reveal(pill,!!name,{fade:true})"), "the realm pill pops in");
  assert.ok(/revealText\(line,/.test(body("function heraldRenderRealmLines(){")), "the webhook line (Not set up yet) does not fade");
  assert.ok(body("function heraldRenderPost(){").includes("revealText(el,"), "the post line does not fade");
  assert.ok(body("function heraldRenderModCodeLine(){").includes("revealText(el,") && body("function heraldRenderLookLine(){").includes("revealText(el,"),
    "a paused or refused line does not fade");
  assert.ok(body("function heraldPreviewPaint(r){").includes('revealSwap($("#heraldPreview")'), "the preview jumps on a refresh");
  /* Every tick wires to the rolling paint, and the Appearance dialog fades out through modalClose. */
  assert.ok(APP.includes('sw.addEventListener("change",()=>{heraldRenderKindBoxes();heraldSave();});'), "a kind's tick does not roll its boxes");
  assert.ok(body("function modalClose(){").includes("modalFadeOut();"), "a dialog asking to fade out vanishes");
  /* Saved values are kept while hidden: the boxes stay in the document and the save reads them. */
  const values = body("function heraldEventValues(){");
  assert.ok(values.includes("out[k.kind+\"WebhookUrl\"]=box(k.url);") && !values.includes("heraldTicked"), "a hidden kind's boxes are not saved");
});

console.log(failures.length === 0
  ? "herald screen selftest: all " + passed + " rules pass"
  : "herald screen selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAIL");
process.exit(failures.length === 0 ? 0 : 1);
