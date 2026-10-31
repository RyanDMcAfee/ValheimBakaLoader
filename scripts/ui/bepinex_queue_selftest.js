/* The BepInEx queue on the page (issue 31): Install it now while a server is up, a refusal
 * that outlasts its toast, the queued bar, and Stop, install and start again.
 *
 * WHY THIS EXISTS. A restart window turned a new pack down as too young for a write nobody
 * watched, and the bar it raised offered Install it now. The bar only ever arrives on the
 * relaunched server's Starting status, so the press always met a server that counted as up.
 * The page marked the bar read BEFORE the write, jumped to the Mods hall, and sent a write
 * the host was bound to refuse; the refusal was one toast for five seconds, and after it
 * nothing on screen said anything. These rules drive the real painters out of app.js against
 * the real English catalog, with the host side stood in by a recording rpc().
 *
 *     node scripts/ui/bepinex_queue_selftest.js [app.js] [en.json]
 *
 * Pointed at the 1.3.1 app.js it fails: the press there sends bepinex.update.
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
const pending = [];

function test(name, body) {
  pending.push(async () => {
    try {
      await body();
      passed++;
      console.log("  ok   " + name);
    } catch (problem) {
      failures.push(name);
      console.log("  FAIL " + name);
      console.log("       " + (problem && problem.message ? problem.message : problem));
    }
  });
}

function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to) + "\n}";
}

function fn1(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  return SOURCE.slice(from, SOURCE.indexOf("\n", from));
}

function table(name, closer) {
  const from = SOURCE.indexOf("const " + name + "=");
  assert.ok(from >= 0, "app.js no longer holds " + name);
  const lines = SOURCE.slice(from).split("\n");
  for (let i = 0; i < lines.length; i++)
    if (lines[i].endsWith(closer)) return lines.slice(0, i + 1).join("\n");
  throw new Error(name + " never closes with " + closer);
}

function english(id, params) {
  const entry = KEYS[id];
  if (!entry) return String(id);
  let text = entry.lore != null ? entry.lore : String(id);
  if (params)
    for (const slot of Object.keys(params))
      text = text.split("{" + slot + "}").join(String(params[slot] == null ? "" : params[slot]));
  return text;
}

/* An element that answers querySelector from what was written into it, so a control the
   painter did not draw comes back null, and remembers every handler wired to it. */
function element() {
  const wired = [];
  const el = {
    innerHTML: "", style: { display: "none" }, dataset: {}, wired,
    removeAttribute(name) { delete el.dataset[name.replace(/^data-/, "")]; },
    querySelector(selector) {
      const id = selector.startsWith("#") ? selector.slice(1) : null;
      const present = id ? el.innerHTML.indexOf('id="' + id + '"') >= 0
        : el.innerHTML.indexOf(selector.replace(/^\[|\]$/g, "")) >= 0;
      if (!present) return null;
      const disabled = id ? new RegExp('id="' + id + '"[^>]*disabled').test(el.innerHTML) : false;
      return { disabled, addEventListener(kind, handler) { wired.push({ selector, kind, handler }); } };
    },
  };
  return el;
}

function press(el, selector) {
  const row = el.wired.filter(w => w.selector === selector).pop();
  assert.ok(row, "nothing is wired to " + selector);
  return row.handler({ preventDefault() {} });
}

function body(el) {
  const at = el.innerHTML.indexOf('<span class="hbmsg">');
  assert.ok(at >= 0, "the bar drew no message");
  const from = at + '<span class="hbmsg">'.length;
  return el.innerHTML.slice(from, el.innerHTML.indexOf("</span>", from));
}

const flush = () => new Promise(r => setImmediate(r));

function harness() {
  const bar = element();
  const calls = [];
  const toasts = [];
  const modal = { open: false, html: "", el: null, again: null };
  const answers = {};
  const context = {
    Object, Array, JSON, Map, Set, Date, String, Number, RegExp, Promise, console, Symbol, setTimeout,
    T: english, LOC: () => "en", logLine: () => {}, goPage: () => { calls.push({ method: "goPage" }); },
    sectSet: () => {}, bepInExRepairPackGone: () => false, bepInExNeedsConfirm: () => false,
    bepInExWriteFlags: () => ({}), bepInExPackOffered: () => null, renderBepInExProgress: () => {},
    bepInExProgressDone: () => {}, renderMods: () => context.renderBepInExRow(), bepInExDowngradeModal: () => {},
    bepInExReportWrite: (m, r) => { toasts.push("report:" + ((r && r.result && r.result.version) || "")); },
    toast: text => { toasts.push(text); },
    FAIL: { fail: true },
    S: { bepinex: { runningProfiles: [] } },
    Native: { available: true, call: (m, p) => { calls.push({ method: m, params: p }); return Promise.resolve(null); } },
    $: selector => (selector === "#launchHold" ? bar : null),
    modalOpen: (html, again) => {
      const el = element();
      el.innerHTML = html;
      Object.assign(modal, { open: true, html, el, again });
      return el;
    },
    modalClose: () => { modal.open = false; },
    rpc: (method, params) => {
      calls.push({ method, params });
      const a = answers[method];
      const settle = v => {
        if (v && v.fail) {
          context.window.BAKA_ERR_ID = v.fail;
          context.window.BAKA_ERR_PARAMS = v.params || null;
          toasts.push("refusal:" + v.fail);
          return context.FAIL;
        }
        context.window.BAKA_ERR_ID = null; context.window.BAKA_ERR_PARAMS = null;
        return v == null ? {} : v;
      };
      return a && typeof a.then === "function" ? a.then(settle) : Promise.resolve(settle(a));
    },
    bar, calls, toasts, modal, answers,
  };
  context.window = context;
  context.renderBepInExRow = () => {
    const b = context.S.bepinex;
    if (b && "queued" in b) vm.runInContext("conditionBepInExQueued(S.bepinex.queued)", context);
  };
  vm.createContext(context);
  vm.runInContext(SOURCE.slice(SOURCE.indexOf("/* REVEAL-BEGIN"), SOURCE.indexOf("/* REVEAL-END */")), context);
  vm.runInContext(fn1("const esc=s=>"), context);
  vm.runInContext(table("CONDITION_ORDER", "];"), context);
  vm.runInContext(fn1("const CONDITIONS=new Map();"), context);
  vm.runInContext(table("HOST_SENTENCES", "];"), context);
  vm.runInContext(table("LANG_REASONS", "];"), context);
  vm.runInContext(table("BEPINEX_REASONS", "];"), context);
  vm.runInContext(table("BEPINEX_LEFT_ALONE_ACTS", "};"), context);
  vm.runInContext("let BEP_WRITING=false; let BEP_PROGRESS=null; let BEP_CHECKING=false;"
    + "let BEP_REFUSAL=null; let BEP_BAR_FAIL=null;", context);
  for (const opening of [
    "function setCondition(kind,cond){", "function renderConditionBar(){", "function rerenderConditions(){",
    "function langReasonText(id,params){", "function hostSentence(id,params){",
    "function bepInExReasonText(id,params){", "function bepInExLeftAloneText(last){",
    "function bepInExLeftAloneAct(last){", "function bepInExLeftAloneSeen(last){",
    "function bepInExLeftAloneWrite(last){", "function bepInExNoticeDrawn(last){",
    "function bepInExLeftAloneKeptHtml(){", "function conditionBepInExLeftAlone(last){",
    "function bepInExRunning(){", "function conditionBepInExQueued(q){", "function bepInExQueuedRedraw(){",
    "function bepInExUpdateFlow(extra,bar){", "function bepInExWriteOrQueue(method,params,after,bar){",
    "async function bepInExWrite(method,params,after,bar){", "function bepInExNamesOf(names){",
    "function bepInExQueueVersion(bar){", "function bepInExQueueModal(params,names,bar){",
    "function bepInExRefusalText(f){", "async function bepInExQueue(params,bar){",
    "async function bepInExUnqueue(){", "async function bepInExStopAndInstall(params,bar){",
    "async function bepInExCheckNow(){",
  ]) vm.runInContext(fn(opening), context, { filename: "app.js#" + opening });
  for (const opening of ["function clearCondition(kind){", "function bepInExNoticeSeen(last){"])
    vm.runInContext(fn1(opening), context);
  return context;
}

/* The soak outcome the tester's restart window recorded, as BepInExUnattendedDto sends it. */
function soak(changed) {
  return Object.assign({
    outcome: "refused", profile: "Default", reason: "soak", version: "5.4.2360",
    installedVersion: "5.4.23.5", eligibleUtc: "2026-10-31T16:00:00Z", leftAsItWas: true,
    whenUtc: "2026-10-28T16:44:34Z", key: "refused|soak|5.4.2360|5.4.23.5|", seen: false,
  }, changed || {});
}

function sent(h, method) { return h.calls.filter(c => c.method === method); }

/* ---------------------------------------------------------- scenario 1 */
test("Install it now with Default Starting sends no write and asks the queue question", async () => {
  const h = harness();
  h.S.bepinex.runningProfiles = ["Default"];
  h.LAST = soak();
  h.S.bepinex.lastUnattended = h.LAST;
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  press(h.bar, "#cbBepLeftAct");
  await flush();
  assert.strictEqual(sent(h, "bepinex.update").length, 0, "a write the host is bound to refuse went out");
  assert.ok(h.modal.open, "the queue question did not open");
  assert.ok(h.modal.html.indexOf(english("bepinex.dialog.queue.title")) >= 0, "the dialog is not the queue question");
  assert.ok(h.modal.html.indexOf("Still up: Default") >= 0, "the dialog does not name the server still up");
  assert.strictEqual(h.LAST.seen, false, "the bar was marked read before anything was answered");
  assert.strictEqual(sent(h, "bepinex.noticeSeen").length, 0, "the notice was closed on the host before an answer");
  assert.strictEqual(h.bar.dataset.kind, "bepinexLeftAlone", "the bar went down while the question is open");
  assert.strictEqual(sent(h, "goPage").length, 0, "the press jumped halls away from the bar");
});

test("Queue it is the answer that closes the notice and stands the queued bar up", async () => {
  const h = harness();
  h.S.bepinex.runningProfiles = ["Default"];
  h.LAST = soak();
  h.S.bepinex.lastUnattended = h.LAST;
  h.answers["bepinex.queue"] = { runningProfiles: ["Default"],
    queued: { version: "5.4.2360", names: ["Default"], reason: null } };
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  press(h.bar, "#cbBepLeftAct");
  press(h.modal.el, "#mOk");
  await flush();
  const q = sent(h, "bepinex.queue");
  assert.strictEqual(q.length, 1, "Queue it sent " + q.length + " bepinex.queue calls");
  assert.strictEqual(q[0].params.version, "5.4.2360", "the queue does not carry the pack the bar named");
  assert.strictEqual(h.LAST.seen, true, "an accepted queue left the notice unread");
  assert.strictEqual(h.bar.dataset.kind, "bepinexQueued", "the queued bar is not what stands now");
  assert.ok(body(h.bar).indexOf("Still up: Default") > 0, "the queued bar does not name who is still up");
  assert.ok(h.toasts.some(t => t.indexOf(english("bepinex.queued.toast", { version: "5.4.2360" })) >= 0),
    "the press was not answered");
});

/* ---------------------------------------------------------- scenario 2 */
test("a serversRunning refusal still stands six seconds later, not only in a toast", async () => {
  const h = harness();
  // A server BakaLoader did not start: the page sees nobody up, the host refuses.
  h.answers["bepinex.update"] = { fail: "bepinex.serversRunning", params: { names: "Valheim dedicated server" } };
  h.LAST = soak();
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  press(h.bar, "#cbBepLeftAct");
  await flush(); await flush();
  assert.strictEqual(sent(h, "bepinex.update").length, 1);
  const sentence = english("bepinex.reason.servers_running", { names: "Valheim dedicated server" });
  // The toast is gone after TOAST_LIFE; nothing below runs on a timer, so what stands now stands then.
  await new Promise(r => setTimeout(r, 30));
  assert.strictEqual(vm.runInContext("bepInExRefusalText(BEP_REFUSAL)", h), sentence,
    "the row has no standing copy of the refusal");
  assert.ok(body(h.bar).indexOf(sentence) >= 0, "the bar the press came from does not carry the refusal");
  assert.ok(h.modal.open && h.modal.html.indexOf(english("bepinex.dialog.queue.title")) >= 0,
    "a refusal for a server up did not turn into the queue question");
  assert.strictEqual(h.LAST.seen, false, "a refused press marked the notice read");

  // A fresh status push redraws the bar from the host's answer and the refusal is still in it.
  vm.runInContext("conditionBepInExLeftAlone(Object.assign({},LAST))", h);
  assert.ok(body(h.bar).indexOf(sentence) >= 0, "the next status push wiped the refusal off the bar");
});

test("any other refusal from the bar puts the bar back with the host sentence", async () => {
  const h = harness();
  h.answers["bepinex.update"] = { fail: "bepinex.integrity" };
  h.LAST = soak();
  vm.runInContext("conditionBepInExLeftAlone(LAST)", h);
  press(h.bar, "#cbBepLeftAct");
  await flush(); await flush();
  assert.strictEqual(h.bar.dataset.kind, "bepinexLeftAlone");
  assert.strictEqual(h.bar.dataset.sev, "warn", "a refused press is not drawn as a warning");
  assert.ok(body(h.bar).indexOf(english("bepinex.reason.integrity")) >= 0, "the host sentence is not in the bar");
  assert.ok(!h.modal.open, "a hard refusal opened the queue question");
});

/* ---------------------------------------------------------- scenario 11, page side; scenario 8 */
test("Stop, install and start again greys while out, a second press sends nothing, the toast names the pack written", async () => {
  const h = harness();
  let release;
  h.answers["bepinex.stopAndInstall"] = new Promise(r => { release = r; });
  h.S.bepinex.queued = { version: "5.4.2360", names: ["Default"] };
  vm.runInContext("conditionBepInExQueued(S.bepinex.queued)", h);
  press(h.bar, "#cbBepStopNow");
  await flush();
  assert.ok(h.bar.querySelector("#cbBepStopNow").disabled, "the button is live while the stop is out");
  press(h.bar, "#cbBepStopNow");
  await flush();
  assert.strictEqual(sent(h, "bepinex.stopAndInstall").length, 1, "a second press sent a second stop");
  release({ result: { version: "5.4.2361", skipped: false }, started: ["Default"], queued: null });
  await flush(); await flush();
  assert.ok(h.toasts.some(t => t.indexOf(english("bepinex.stop_install.done.toast",
    { version: "5.4.2361", names: "Default" })) >= 0), "the done toast does not name the pack actually written");
  assert.ok(!h.bar.dataset.kind, "the queued bar stayed up after the write ran");
});

test("a stop that timed out stands on the row in its own words", async () => {
  const h = harness();
  h.answers["bepinex.stopAndInstall"] = { fail: "bepinex.stopTimedOut", params: { names: "Default" } };
  vm.runInContext("bepInExStopAndInstall(null,null)", h);
  await flush(); await flush();
  assert.strictEqual(vm.runInContext("bepInExRefusalText(BEP_REFUSAL)", h),
    english("bepinex.reason.stop_timed_out", { names: "Default" }), "the timeout has no standing sentence");
});

/* ---------------------------------------------------------- scenario 19 */
test("a language switch redraws the queued bar in the new words, with no toast and no call", async () => {
  const h = harness();
  vm.runInContext("conditionBepInExQueued({version:'5.4.2360',names:['Default']})", h);
  const before = body(h.bar);
  const calls = h.calls.length, toasts = h.toasts.length;
  h.T = (id, params) => "[xx] " + english(id, params);
  vm.runInContext("rerenderConditions()", h);
  assert.notStrictEqual(body(h.bar), before, "the bar kept its English");
  assert.ok(body(h.bar).indexOf("[xx] ") === 0, "the bar is not in the new language");
  assert.ok(h.bar.innerHTML.indexOf("[xx] " + english("bepinex.condition.queued.stop_now")) > 0,
    "the button kept its English");
  assert.strictEqual(h.calls.length, calls, "the redraw made a call");
  assert.strictEqual(h.toasts.length, toasts, "the redraw toasted");
});

test("Cancel on the queued bar is the unqueue, and nothing else", async () => {
  const h = harness();
  h.answers["bepinex.unqueue"] = { queued: null };
  vm.runInContext("conditionBepInExQueued({version:'5.4.2360',names:[]})", h);
  assert.ok(h.bar.innerHTML.indexOf(english("bepinex.condition.queued.cancel")) > 0, "the bar has no Cancel");
  press(h.bar, "[data-cond-dismiss]");
  await flush();
  assert.strictEqual(sent(h, "bepinex.unqueue").length, 1, "Cancel did not take the queue off the host");
  assert.ok(!h.bar.dataset.kind, "the bar stayed up after Cancel");
});

/* ---------------------------------------------------------- scenario 16 */
test("Check BepInEx now offline says the offline sentence, records nothing and raises no bar", async () => {
  const h = harness();
  const before = { runningProfiles: [], lastUnattended: null };
  h.S.bepinex = before;
  h.answers["bepinex.check"] = { fail: "bepinex.offline" };
  await vm.runInContext("bepInExCheckNow()", h);
  assert.strictEqual(sent(h, "bepinex.check").length, 1, "the check was not asked exactly once");
  assert.deepStrictEqual(h.toasts, ["refusal:bepinex.offline"], "the offline answer said more than its sentence");
  assert.strictEqual(h.S.bepinex, before, "an offline check replaced what the page knows");
  assert.strictEqual(vm.runInContext("CONDITIONS.size", h), 0, "an offline check raised a bar");
  assert.strictEqual(vm.runInContext("BEP_CHECKING", h), false, "the check stayed busy after it answered");
  assert.ok(vm.runInContext("hostSentence('bepinex.offline',null)", h), "offline has no host sentence");
});

test("Check BepInEx now on the current pack is one toast naming it", async () => {
  const h = harness();
  h.answers["bepinex.check"] = { runningProfiles: [], check: { kind: "current", version: "5.4.2361", installed: "5.4.2361" } };
  await vm.runInContext("bepInExCheckNow()", h);
  assert.deepStrictEqual(h.toasts.map(t => t.replace(/^\S+ /, "")),
    [english("bepinex.check.current.toast", { version: "5.4.2361" })], "the current pack was not one plain toast");
  await vm.runInContext("bepInExCheckNow()", h);
  assert.strictEqual(sent(h, "bepinex.check").length, 2, "a second check after the first answered was refused");
});

/* ---------------------------------------------------------- the wiring the painters need */
test("the queued kind sits just above the waiting one, and the timeout has its sentence", () => {
  const h = harness();
  const order = vm.runInContext("CONDITION_ORDER", h);
  assert.strictEqual(order.indexOf("bepinexQueued") + 1, order.indexOf("bepinexWaiting"),
    "bepinexQueued is not just before bepinexWaiting");
  assert.ok(vm.runInContext("hostSentence('bepinex.stopTimedOut',{names:'A'})", h).indexOf("A") > 0);
  assert.ok(fn("function renderBepInExRow(){").indexOf("bepInExRefusalText(BEP_REFUSAL)") > 0,
    "the row does not draw the standing refusal");
  assert.ok(fn("function renderBepInExRow(){").indexOf("bepInExRowOffersCheck(b)") > 0,
    "the row does not offer Check BepInEx now");
});

(async () => {
  for (const run of pending) await run();
  console.log(failures.length ? "bepinex queue selftest: " + failures.length + " FAILED" :
    "bepinex queue selftest: " + passed + " rules passed");
  process.exit(failures.length ? 1 : 0);
})();
