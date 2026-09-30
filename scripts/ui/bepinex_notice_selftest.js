/* The BepInEx notice bar: what it raises, how many buttons it offers, and what happens the
 * second time the same fact arrives.
 *
 * WHY THIS EXISTS. The owner's install runs a scheduled restart window. Every window asked
 * BepInEx whether there was anything to do, every one of them declined for the same reason
 * (the loader on that install was not put there by BakaLoader), and the page stood the same
 * bar up again each time: eight identical lines in his log over two days and a sentence to
 * close after every single launch, about a decision he had already taken. Beside it sat two
 * buttons that both only closed the bar. In his words: "The 'Bepinex was left as it was'
 * disclaimer every launch is really not necessary. It comes up every time and needs to be
 * dismissed/close, which also dont have any different function between the two."
 *
 * So there are three rules here and each one of them is a thing no unit test could see:
 *
 *   1. A fact that has been READ is not raised again. The host side names the fact and says
 *      whether the host has closed a notice about it; the page has to honour that, in the
 *      condition bar and in the note under the loader row.
 *   2. A bar offers ONE action, and one close. The four reasons a press cannot help with
 *      carry their wiki link inside the sentence instead of on a button, and the bar's own
 *      dismiss IS the close: there is no second button saying the same word.
 *   3. A reason where a press DOES work keeps its button, so nothing was taken away.
 *
 * Every one of them is driven through the real painters out of app.js, against the real
 * English catalog. A copy of those functions in this file would pass for ever while the
 * page drew something else.
 *
 * Prints one line per rule and exits non zero on the first failure.
 *
 *     node scripts/ui/bepinex_notice_selftest.js [app.js] [en.json]
 *
 * The paths are there so a mutation can be driven without touching the tree: point it at a
 * copy with the seen check taken out and rule 1 has to go red, or it is decoration.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const CATALOG = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");

const SOURCE = fs.readFileSync(APP, "utf8").replace(/\r\n/g, "\n");
const KEYS = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys;

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

/** One whole top level function out of app.js, named by its opening line. */
function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to) + "\n}";
}

/** A function written on ONE line, which the "closes at the left margin" reader would run
    straight past and pick up the next function's brace instead. */
function fn1(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n", from);
  assert.ok(to > from, JSON.stringify(opening) + " is no longer one line");
  return SOURCE.slice(from, to);
}

/** A top level const whose value is a literal, read to the line that closes it. */
function table(name, closer) {
  const from = SOURCE.indexOf("const " + name + "=");
  assert.ok(from >= 0, "app.js no longer holds " + name);
  // The first line that ENDS with the closer, not the first one that begins with it: a
  // reader looking for it at the left margin ran past every table whose last entry and
  // closing bracket share a line and swallowed the declarations after it whole.
  const lines = SOURCE.slice(from).split("\n");
  for (let i = 0; i < lines.length; i++)
    if (lines[i].endsWith(closer)) return lines.slice(0, i + 1).join("\n");
  throw new Error(name + " never closes with " + closer);
}

/* ------------------------------------------------------------------ the catalog, for real

   The sentences matter here, not just the ids: the inline link is put INSIDE one of them by
   splitting the rendered English on a placeholder, and an id stub would hide the day that
   placeholder went missing. So this reads en.json and fills the named slots the way the
   lookup does. */
function words(id, params) {
  const entry = KEYS[id];
  if (!entry) return String(id);
  let text = entry.lore != null ? entry.lore : (entry.plain != null ? entry.plain : String(id));
  if (params) {
    for (const slot of Object.keys(params))
      text = text.split("{" + slot + "}").join(String(params[slot] == null ? "" : params[slot]));
  }
  return text;
}

/* ------------------------------------------------------------------------- the fake window

   Just enough of one element to let renderConditionBar draw into it and let each raiser's
   wire() find the controls it wants. querySelector answers from what was actually written
   into innerHTML, so a button the painter did not draw really does come back null. */
function element() {
  const wired = [];
  const el = {
    innerHTML: "",
    style: { display: "none" },
    dataset: {},
    wired,
    removeAttribute(name) { delete el.dataset[name.replace(/^data-/, "")]; },
    querySelector(selector) {
      const id = selector.startsWith("#") ? selector.slice(1) : null;
      const present = id
        ? el.innerHTML.indexOf('id="' + id + '"') >= 0
        : el.innerHTML.indexOf(selector.replace(/^\[|\]$/g, "")) >= 0;
      if (!present) return null;
      return {
        addEventListener(kind, handler) { wired.push({ selector, kind, handler }); },
      };
    },
  };
  return el;
}

function press(el, selector) {
  const row = el.wired.filter(w => w.selector === selector).pop();
  assert.ok(row, "nothing is wired to " + selector);
  row.handler({ preventDefault() {} });
}

function buttons(el) {
  return (el.innerHTML.match(/<button/g) || []).length;
}

function body(el) {
  const at = el.innerHTML.indexOf('<span class="hbmsg">');
  assert.ok(at >= 0, "the bar drew no message");
  const from = at + '<span class="hbmsg">'.length;
  return el.innerHTML.slice(from, el.innerHTML.indexOf("</span>", from));
}

/* ----------------------------------------------------------------------------- the harness */
function harness() {
  const bar = element();
  const called = [];
  const context = {
    Object, Array, JSON, Map, Set, Date, String, Number, console, Symbol,
    T: words,
    LOC: () => "en",
    logLine: () => {},
    goPage: () => {},
    bepInExUpdateFlow: () => { called.push("update"); },
    bepInExRepairPackGone: () => false,
    S: { bepinex: {} },
    Native: { available: true, call: (m, p) => { called.push(m); return Promise.resolve(null); } },
    $: selector => (selector === "#launchHold" ? bar : null),
    bar,
    called,
  };
  vm.createContext(context);
  vm.runInContext(fn1("const esc=s=>"), context, { filename: "app.js#esc" });
  vm.runInContext(table("CONDITION_ORDER", "];"), context, { filename: "app.js#order" });
  vm.runInContext(fn1("const CONDITIONS=new Map();"), context, { filename: "app.js#conditions" });
  vm.runInContext(fn1("let BEP_WRITE_CHRONICLED=null;"), context, { filename: "app.js#chronicled" });
  vm.runInContext(table("HOST_SENTENCES", "];"), context, { filename: "app.js#host" });
  vm.runInContext(table("LANG_REASONS", "];"), context, { filename: "app.js#lang" });
  vm.runInContext(table("BEPINEX_REASONS", "];"), context, { filename: "app.js#reasons" });
  vm.runInContext(table("BEPINEX_LEFT_ALONE_ACTS", "};"), context, { filename: "app.js#acts" });
  for (const opening of [
    "function setCondition(kind,cond){",
    "function renderConditionBar(){",
    "function langReasonText(id,params){",
    "function hostSentence(id,params){",
    "function bepInExReasonText(id,params){",
    "function bepInExLeftAloneText(last){",
    "function bepInExLeftAloneAct(last){",
    "function bepInExLeftAloneSeen(last){",
    "function bepInExLeftAloneWrite(last){",
    "function bepInExLeftAloneWiki(){",
    "function bepInExNoticeDrawn(last){",
    "function bepInExLeftAloneKeptHtml(){",
    "function conditionBepInExLeftAlone(last){",
    "function conditionBepInExWritten(last){",
    "function conditionBepInExHealed(last){",
  ]) vm.runInContext(fn(opening), context, { filename: "app.js#" + opening });
  for (const opening of [
    "function clearCondition(kind){",
    "function bepInExNoticeSeen(last){",
  ]) vm.runInContext(fn1(opening), context, { filename: "app.js#" + opening });
  return context;
}

/** The outcome shape BepInExUnattendedDto sends, with only what a row cares about. */
function refused(changed) {
  return Object.assign({
    outcome: "refused",
    profile: "Second Sunset",
    reason: "foreign",
    installedVersion: "5.4.19.0",
    version: "5.4.2351",
    leftAsItWas: true,
    whenUtc: "2026-09-29T02:11:00Z",
    key: "refused|foreign|5.4.2351|5.4.19.0|",
    seen: false,
  }, changed || {});
}

/* ---------------------------------- rule 1: a fact that has been read is not raised again */
test("a refusal the host has already closed raises nothing at all", () => {
  const h = harness();
  vm.runInContext("conditionBepInExLeftAlone(LAST)", Object.assign(h, { LAST: refused() }));
  assert.strictEqual(h.bar.style.display, "flex", "the first time the fact arrives it must be shown");
  assert.ok(body(h.bar).indexOf("not the framework") > 0, "the bar says nothing about the loader");

  h.LAST = refused({ seen: true });
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  assert.strictEqual(h.bar.style.display, "none",
    "the same fact stood the bar up again, which is the whole complaint");
  assert.strictEqual(vm.runInContext("CONDITIONS.size", h), 0,
    "the condition is still held after a seen fact");
});

test("a NEW fact is still said once, even after an older one was closed", () => {
  const h = harness();
  h.LAST = refused({ seen: true });
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  assert.strictEqual(h.bar.style.display, "none");

  // A different reason is a different fact, and the host side says so with seen:false.
  h.LAST = refused({
    reason: "soak", version: "5.4.2352", seen: false,
    key: "refused|soak|5.4.2352|5.4.19.0|", eligibleUtc: "2026-10-02T00:00:00Z",
  });
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  assert.strictEqual(h.bar.style.display, "flex", "a reason the host has never read was silenced");
});

/* --------------------------------- rule 2: one action, one dismiss, and the link inline */
test("foreign draws exactly one button and puts the wiki link in the sentence", () => {
  const h = harness();
  h.LAST = refused();
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);

  assert.strictEqual(buttons(h.bar), 1,
    "the bar has " + buttons(h.bar) + " buttons; the reasons a press cannot help with get one");
  const said = body(h.bar);
  assert.ok(/<a href="#" class="inlink" id="cbBepWikiLink">/.test(said),
    "the wiki link is not inside the sentence: " + said);
  assert.strictEqual(h.bar.innerHTML.indexOf("cbBepLeftAct"), -1,
    "the ember button is still drawn for a reason whose only offer is a page to read");
});

test("each of the four reasons a press cannot help with draws one button", () => {
  for (const reason of ["drivenElsewhere", "foreign", "newer", "repairMismatch"]) {
    const h = harness();
    h.LAST = refused({ reason, key: "refused|" + reason + "|5.4.2351|5.4.19.0|" });
    vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
    assert.strictEqual(h.bar.style.display, "flex", reason + " raised no bar");
    assert.strictEqual(buttons(h.bar), 1,
      reason + " draws " + buttons(h.bar) + " buttons rather than one");
    assert.ok(h.bar.innerHTML.indexOf("cbBepWikiLink") > 0, reason + " lost its wiki link");
  }
});

test("the one button is the bar's own, and pressing it tells the host side", () => {
  const h = harness();
  h.LAST = refused();
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  assert.ok(h.bar.innerHTML.indexOf(words("common.button.close")) > 0,
    "the bar's own button no longer reads Close");
  press(h.bar, "[data-cond-dismiss]");
  assert.ok(h.called.indexOf("bepinex.noticeSeen") >= 0,
    "closing the bar never told the host side, so the fact is not written down");
});

/* ---------------------------------- rule 1 again: the gap between the Close and the RPC */
test("closing marks what the page is holding BEFORE the call goes out", () => {
  const h = harness();
  h.LAST = refused();
  h.S.bepinex.lastUnattended = h.LAST;
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);

  // What the page was holding at the moment the RPC was made, rather than afterwards.
  let heldAtCall = null;
  h.Native.call = () => {
    heldAtCall = {
      raised: h.LAST.seen,
      held: h.S.bepinex.lastUnattended.seen,
    };
    return Promise.resolve(null);
  };

  press(h.bar, "[data-cond-dismiss]");
  assert.ok(heldAtCall, "closing the bar never reached the host side at all");
  assert.strictEqual(heldAtCall.raised, true,
    "the answer the bar was raised with still said seen:false when the call went out");
  assert.strictEqual(heldAtCall.held, true,
    "the outcome the page holds still said seen:false when the call went out");
});

test("a status event in that gap does not stand the bar back up", () => {
  const h = harness();
  h.LAST = refused();
  h.S.bepinex.lastUnattended = h.LAST;
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  assert.strictEqual(h.bar.style.display, "flex");

  press(h.bar, "[data-cond-dismiss]");
  assert.strictEqual(h.bar.style.display, "none", "the close did not take the bar down");

  // A server.status or a bepinex.changed lands before the host side has answered, and it
  // raises the condition again off the SAME held outcome. The host side has not written
  // anything down yet, so the only thing that can stop the flicker is the page's own mark.
  vm.runInContext("conditionBepInExLeftAlone(S.bepinex.lastUnattended)", h);
  assert.strictEqual(h.bar.style.display, "none",
    "the bar came back in the gap between the close and the host side's answer");

  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  assert.strictEqual(h.bar.style.display, "none",
    "a replay off the answer the bar was raised with brought it back");
});

test("pressing the offer marks it too, not only the close", () => {
  const h = harness();
  h.LAST = refused({
    reason: "soak", version: "5.4.2352", eligibleUtc: "2026-10-02T00:00:00Z",
    key: "refused|soak|5.4.2352|5.4.19.0|",
  });
  h.S.bepinex.lastUnattended = h.LAST;
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);

  press(h.bar, "#cbBepLeftAct");
  assert.strictEqual(h.LAST.seen, true, "the offer is a read notice and did not mark it");
  assert.strictEqual(h.S.bepinex.lastUnattended.seen, true,
    "the offer left the held outcome saying seen:false, so the next push raises it again");
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  assert.strictEqual(h.bar.style.display, "none", "the bar came back after the offer");
});

test("the inline link opens the wiki, and its words come out of the catalog", () => {
  const h = harness();
  h.LAST = refused();
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  press(h.bar, "#cbBepWikiLink");
  assert.ok(h.called.indexOf("shell.openUrl") >= 0, "the inline link opens nothing");

  for (const id of ["bepinex.condition.left_alone.kept", "bepinex.condition.left_alone.wiki_link"]) {
    assert.ok(KEYS[id], id + " is not in the English catalog, so the bar would print an id");
    assert.ok(/[a-z]/.test(words(id)), id + " has no words");
  }
  assert.ok(body(h.bar).indexOf(words("bepinex.condition.left_alone.wiki_link")) > 0,
    "the link text is not the catalog's");
});

test("the link lands INSIDE the sentence rather than after it", () => {
  const h = harness();
  h.LAST = refused();
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  const said = body(h.bar);
  const at = said.indexOf("<a href=");
  const closes = said.indexOf("</a>");
  assert.ok(at > 0 && closes > at, "no anchor in the sentence");
  assert.ok(said.slice(closes).replace(/<[^>]*>/g, "").trim().length > 0,
    "the sentence ends at the link, so the placeholder in the catalog entry was lost");
});

/* -------------------------------- rule 3: a press that does work still has its button */
test("soak keeps its Install it now beside the close", () => {
  const h = harness();
  h.LAST = refused({
    reason: "soak", version: "5.4.2352", eligibleUtc: "2026-10-02T00:00:00Z",
    key: "refused|soak|5.4.2352|5.4.19.0|",
  });
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  assert.strictEqual(buttons(h.bar), 2,
    "soak draws " + buttons(h.bar) + " buttons; it needs the offer and the close");
  assert.ok(h.bar.innerHTML.indexOf("cbBepLeftAct") > 0, "soak lost its Install it now");
  assert.strictEqual(h.bar.innerHTML.indexOf("cbBepWikiLink"), -1,
    "soak grew an inline wiki link, which is not what it needs");
  press(h.bar, "#cbBepLeftAct");
  assert.ok(h.called.indexOf("update") >= 0, "the offer no longer writes");
});

test("every reason whose offer is a write keeps a second button", () => {
  const h0 = harness();
  const acts = vm.runInContext("BEPINEX_LEFT_ALONE_ACTS", h0);
  const writes = Object.keys(acts).filter(k => acts[k].act === "write");
  assert.ok(writes.length >= 4, "the offer table has lost its write rows");
  for (const reason of writes) {
    const h = harness();
    h.LAST = refused({ reason, key: "refused|" + reason + "|5.4.2351|5.4.19.0|" });
    vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
    assert.strictEqual(buttons(h.bar), 2, reason + " draws " + buttons(h.bar) + " buttons");
  }
});

/* ---------------------------- rule 1 again: the write and heal notices read seen as well */
test("a write the host has closed does not come back, and a new one does", () => {
  const h = harness();
  h.LAST = {
    outcome: "written", toVersion: "5.4.2352", fromVersion: "5.4.2351",
    backupStamp: "20260929-021100", backupFolder: ".bakaloader-bepinex-backups",
    whenUtc: "2026-09-29T02:11:00Z", leftAsItWas: false,
    key: "written||||2026-09-29T02:11:00.0000000Z", seen: false,
  };
  vm.runInContext("conditionBepInExWritten(LAST)", h);
  assert.strictEqual(h.bar.style.display, "flex", "a write nobody watched was never reported");
  assert.strictEqual(buttons(h.bar), 1,
    "the written notice draws " + buttons(h.bar) + " buttons; one close is all it needs");

  h.LAST = Object.assign({}, h.LAST, { seen: true });
  vm.runInContext("conditionBepInExWritten(LAST)", h);
  assert.strictEqual(h.bar.style.display, "none", "a write the host has read came back");

  h.LAST = Object.assign({}, h.LAST, {
    seen: false, toVersion: "5.4.2353", whenUtc: "2026-09-30T02:11:00Z",
    key: "written||||2026-09-30T02:11:00.0000000Z",
  });
  vm.runInContext("conditionBepInExWritten(LAST)", h);
  assert.strictEqual(h.bar.style.display, "flex", "the next restart's write was silenced");
});

test("a heal the host has closed does not come back either", () => {
  const h = harness();
  h.LAST = {
    outcome: "healed", toVersion: "5.4.23.5", whenUtc: "2026-09-29T02:11:00Z",
    leftAsItWas: false, key: "healed||||2026-09-29T02:11:00.0000000Z", seen: true,
  };
  vm.runInContext("conditionBepInExWritten(LAST)", h);
  assert.strictEqual(h.bar.style.display, "none", "a heal the host has read came back");

  h.LAST = Object.assign({}, h.LAST, { seen: false });
  vm.runInContext("conditionBepInExWritten(LAST)", h);
  assert.strictEqual(h.bar.style.display, "flex", "a heal nobody has read was never reported");
  assert.strictEqual(buttons(h.bar), 1,
    "the heal notice draws " + buttons(h.bar) + " buttons; one close is all it needs");
});

/* ------------------------- rule 1 again: the note under the loader row honours it too */
test("the note under the loader row is not repeated once the fact has been read", () => {
  const at = SOURCE.indexOf("const leftAlone=bepInExNoticeSeen(b.lastUnattended)");
  assert.ok(at > 0,
    "the loader row reads the standing outcome without asking whether it has been seen, so "
    + "the note repeats under the row even after the bar was closed");
});

console.log("");
console.log("bepinex notice selftest: " + passed + " passed"
  + (failures.length ? ", " + failures.length + " FAILED" : ""));
if (failures.length) process.exit(1);
