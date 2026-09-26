/* Why a scan came back with nothing, read off the server's own answer rather than guessed.
 *
 * WHY THIS EXISTS. Since 1.2.3 a failed scan has had a state of its own with a sentence, a
 * Try again and a way to the connection test, and that was the right shape. What it did not
 * have was the REASON. The page knows two things about a failure: the call came back with
 * nothing, and whether its own one-minute ceiling was what ended it. The host side knows
 * which read failed, what kind of failure it was, and how long every caller is refused the
 * trip for, and until 1.2.4 it threw all of that away. A host whose machine cannot reach
 * thunderstore.io at all read the same general sentence as a host whose scan was refused for
 * a missing server path.
 *
 * Worse than the wording: a scan that read the BepInEx folder and then could not reach the
 * site answered like a SUCCESS. Every row came back with a dash in the Latest column, which
 * is the same thing the page draws when the site answered and had nothing, so the hall said
 * the mods had been checked when nothing had been asked.
 *
 *   1. The reply carries ok, reasonId and reasonParams, and the ids on both sides match.
 *   2. A reply that says it checked nothing lands on the failed state rather than on a table
 *      full of dashes.
 *   3. The page renders the host's sentence when it has words for it, and its own kind table
 *      otherwise. An id is never rendered.
 *   4. A refusal the bridge THREW is named too, out of the error the RPC wrapper kept.
 *   5. Every id either side can name is in the catalog, with the slots the sentence declares.
 *
 * Prints one line per rule and exits non zero on the first failure.
 *
 *     node scripts/ui/mods_scan_reason_selftest.js [app.js] [en.json] [BlendWindow.Bridge.cs]
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const CATALOG = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");
const BRIDGE = process.argv[4]
  || path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
const CLIENT = process.argv[5]
  || path.join(ROOT, "ValheimBakaLoader", "Tools", "ThunderstoreClient.cs");

const SOURCE = fs.readFileSync(APP, "utf8");
const KEYS = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys;
const HOST = fs.readFileSync(BRIDGE, "utf8");
const SITE = fs.readFileSync(CLIENT, "utf8");

let passed = 0;
const failures = [];
const pending = [];

function note(name, problem) {
  failures.push(name);
  console.log("  FAIL " + name);
  console.log("       " + (problem && problem.message ? problem.message : problem));
}

/* A rule may DRIVE the real function rather than read it, and scanMods is async, so a
   body that hands back a promise is waited for before the count is printed. */
function test(name, body) {
  try {
    const answer = body();
    if (answer && typeof answer.then === "function") {
      pending.push(answer.then(
        () => { passed++; console.log("  ok   " + name); },
        problem => note(name, problem)));
      return;
    }
    passed++;
    console.log("  ok   " + name);
  } catch (problem) {
    note(name, problem);
  }
}

/** The body of the method that words the failed reply, read on its own. */
function scanFailureBody() {
  const from = HOST.indexOf("private static object ScanFailureParams(");
  if (from < 0) return "";
  const to = HOST.indexOf("\n        }", from);
  return HOST.slice(from, to > from ? to : from);
}

/** One whole top level function out of app.js, named by its opening line. */
function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from > 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to);
}

/** A const table out of app.js, evaluated rather than parsed by eye. */
function table(name) {
  const from = SOURCE.indexOf("const " + name + "={");
  assert.ok(from > 0, "app.js no longer holds " + name);
  const to = SOURCE.indexOf("\n};", from);
  assert.ok(to > from, name + " no longer closes at the left margin");
  const context = {};
  vm.createContext(context);
  vm.runInContext(SOURCE.slice(from, to + 3) + "\nthis.TABLE=" + name + ";",
    context, { filename: "app.js#" + name });
  return context.TABLE;
}

/* ------------------------------------------- rule 1: both sides of the seam name one id */
test("the reply carries a reason and both sides spell the id the same way", () => {
  const named = table("MODS_SCAN_REASONS");
  const ids = Object.keys(named).map(k => named[k].reasonId);
  assert.ok(ids.length >= 1, "the page has no list of reasons the host can name");

  ids.forEach(id => {
    assert.ok(KEYS[id], "the catalog has no " + id + ", so the panel would show the id");
    assert.ok(HOST.indexOf('"' + id + '"') > 0,
      "the bridge never sends " + id + ", so the page holds words nothing asks for");
  });

  // And the reply really carries the three fields, beside the ones it always carried.
  ["ok = !blind", "reasonId = blind ?", "reasonParams = blind ?"].forEach(needle => {
    assert.ok(HOST.indexOf(needle) > 0, "mods.scan no longer answers with " + needle);
  });
  assert.ok(/mods = mods\.Select/.test(HOST) && /index = BuildIndexStateDto/.test(HOST),
    "the reply lost a field it used to carry: the two new ones are additive or they are a"
    + " page from an older build that stops working");
});

/* ------------------------------ rule 2: a scan that checked nothing is not a scan that did */
test("a scan that could not reach the site says so and keeps the host's own rows", () => {
  const body = fn("async function scanMods(");

  assert.ok(/r\.ok===false/.test(body),
    "scanMods never reads whether the scan could check anything, so a scan that reached"
    + " nothing draws a table as though the site had answered");
  assert.ok(/S\.modsScanReason=\(blind&&r\.reasonId\)/.test(body),
    "the reason the host named is thrown away");
  assert.ok(/S\.modsScanReason=null/.test(body),
    "the reason is never cleared, so a scan that works still shows the last failure's words");

  // The host side decides "checked nothing" on the two facts that mean it: a remembered
  // failure, and no index held from an earlier read.
  assert.ok(/var blind = failure != null && ThunderstoreClient\.IndexFetchedUtc == null;/.test(HOST),
    "the bridge no longer decides blindness on a remembered failure with nothing held");

  /* Driven rather than read. The rows are the host's OWN install, read off their own
     disk, and they are not the site's to take away: a reply that dropped them took the
     whole table for the session, every row action with it, and a Hexium mark, which is
     answered by a site Thunderstore has nothing to do with, could never be drawn. */
  const S = { mods: null, modsScanned: false, modsScanning: false, modsUpdating: false,
    modsScanError: null, modsScanReason: null, modsIndexBlind: false,
    lastScan: null, modIndexAt: null, modIndexSource: null };
  const context = {
    S, Native: { available: true }, FAIL: Symbol("FAIL"), SCAN_CEILING_MS: 60000,
    rpc: async () => context.REPLY,
    renderMods() {}, toast() {}, logLine() {},
    modsScanFailReason: () => "the site was not reached",
    T: id => id, clock: () => "12:00", hhmm: () => "12:00",
    setTimeout, console, window: {}, REPLY: null,
  };
  vm.createContext(context);
  // fn() stops at the closing brace rather than taking it, so it is put back here.
  vm.runInContext(body + "\n}\nthis.scanMods=scanMods;", context,
    { filename: "app.js#scanMods" });

  context.REPLY = {
    mods: [
      { FullName: "A-B", InstalledVersion: "1.0.0", LatestVersion: null,
        hexiumLatest: "1.67.0", hexiumNewer: true },
      { FullName: "C-D", InstalledVersion: "2.0.0" },
    ],
    index: { fetchedUtc: null, source: null, refreshed: false },
    ok: false,
    reasonId: "mods.empty.failed.reason.unreachable",
    // The WIRE's own shape. The bridge sends detailName, the page reads detailName, and a
    // fixture that made one up drove a path the product never takes: the rule would have
    // gone on passing with the two sides spelling the field differently.
    reasonParams: {
      detailName: "connect",
      retrySeconds: 900,
      retryAtUtc: new Date(Date.now() + 900000).toISOString().replace(/\.\d+Z$/, "Z"),
    },
  };

  return context.scanMods().then(() => {
    assert.ok(Array.isArray(S.mods) && S.mods.length === 2,
      "a reply that could not reach the site threw the host's own mod table away");
    assert.strictEqual(S.modsScanned, true, "the hall went back to the unscanned panel");
    assert.strictEqual(S.modsScanError, "failed", "the failure was not recorded");
    assert.strictEqual(S.modsIndexBlind, true,
      "nothing marks the Latest column as unchecked, so a row would read CURRENT against"
      + " a site that was never asked");
    assert.strictEqual(S.modIndexAt, null,
      "the line under the heading would say the list was read at the press time");
  });
});

/* --------------------------- rule 2b: the reason belongs to the realm the rows came from */
test("the failure state is declared with the rows and goes out with them", () => {
  // Declared in the state literal beside the fields it belongs with. A field that only
  // ever comes into being on the path that sets it is a field nothing else can be read
  // for: every reader of it is guessing, and the clear that ought to exist has nothing
  // to clear.
  const declared = SOURCE.slice(SOURCE.indexOf("const S={"), SOURCE.indexOf("\n};",
    SOURCE.indexOf("const S={")));
  ["modsIndexBlind", "modsScanReason"].forEach(field => {
    assert.ok(new RegExp(field + "\\s*:").test(declared),
      "S does not declare " + field + ", so it exists only once the failure path has run");
  });

  // And cleared on a profile switch, with the rows, the index time and the search box.
  // The previous realm's failure is not this realm's, and a panel wording one would be
  // saying something nobody has asked about this server.
  const swap = fn("async function switchServer(name){");
  ["S.mods=null", "S.modsScanned=false", "S.modIndexAt=null",
   "S.modsIndexBlind=false", "S.modsScanReason=null"].forEach(clear => {
    assert.ok(swap.indexOf(clear) > 0,
      "a profile switch does not clear " + clear + ", so the previous realm's answer is"
      + " drawn against this realm's rows");
  });
});

/* ------------------------------- rule 3: the host's sentence first, the kind table under it */
test("the panel says what the host named, and falls back rather than showing an id", () => {
  const chooser = fn("function modsScanFailReason(){");
  assert.ok(/modsScanReasonText\(\)/.test(chooser), "the panel never asks what the host named");
  assert.ok(/MODS_SCAN_FAIL\[S\.modsScanError\]/.test(chooser),
    "the panel has no floor under it when the host named nothing");

  const reader = fn("function modsScanReasonText(){");
  assert.ok(/MODS_SCAN_REASONS\[k\]\.reasonId===named\.reasonId/.test(reader),
    "the reader renders any id the host sends rather than the ones this page has words for");
  assert.ok(/return hostSentence\(named\.reasonId,params\);/.test(reader),
    "a refusal the bridge threw is not looked up at all");
  assert.ok(/return null;/.test(reader),
    "the reader has no way to say it has no words, so an id could reach the panel");
});

/* ------------------------------------------ rule 4: a refusal that was thrown is named too */
test("a refusal the bridge threw carries its own name into the panel", () => {
  const body = fn("async function scanMods(");
  assert.ok(/window\.BAKA_ERR_ID/.test(body),
    "a throw's id is dropped, so a refusal with a name reads as the general failure");

  // The RPC wrapper is what keeps it, and it keeps the slots with it.
  const wrapper = fn("function rpc(method,params){");
  assert.ok(/window\.BAKA_ERR_ID=err\?\.errorId\|\|null/.test(wrapper),
    "the wrapper no longer keeps the id a refusal named");
  assert.ok(/window\.BAKA_ERR_PARAMS=err\?\.errorParams\|\|null/.test(wrapper),
    "the wrapper no longer keeps the slots a refusal named");

  // And it CLEARS them on a call that worked. These are read by a caller after its own
  // call comes back, so a name left standing from an earlier refusal would be picked up
  // and shown as the reason for a failure that had nothing to do with it.
  assert.ok(/\.then\(answer=>\{[\s\S]{0,600}window\.BAKA_ERR_ID=null;[\s\S]{0,120}window\.BAKA_ERR_PARAMS=null;/
    .test(wrapper),
    "the wrapper never clears the last refusal's name, so it outlives the call that"
    + " named it");

  // Driven, because a rule that reads the source cannot tell a clear that happens from a
  // clear that is written down.
  const context = { window: { BAKA_ERR_ID: "bepinex.newer", BAKA_ERR_PARAMS: { a: 1 } },
    Native: { call: () => Promise.resolve({ ok: true }) },
    FAIL: Symbol("FAIL"), toast() {}, T: id => id,
    hostSentence: () => null, noticeBepInExMaintained() {} };
  vm.createContext(context);
  vm.runInContext(wrapper + "\n}\nthis.rpc=rpc;", context, { filename: "app.js#rpc" });

  return context.rpc("mods.scan", {}).then(() => {
    assert.strictEqual(context.window.BAKA_ERR_ID, null,
      "a call that worked left the last refusal's id standing");
    assert.strictEqual(context.window.BAKA_ERR_PARAMS, null,
      "a call that worked left the last refusal's slots standing");
  });
});

/* ---------------------------------------------- rule 5: every slot the sentence declares */
test("every reason sentence declares the slots it uses and no others", () => {
  const named = table("MODS_SCAN_REASONS");
  const id = named.unreachable && named.unreachable.reasonId;
  assert.ok(id, "the page no longer knows the unreachable reason");

  const slots = entry => (KEYS[entry].lore.match(/\{([a-z]+)\}/g) || [])
    .map(s => s.slice(1, -1)).sort();
  const declared = entry => Object.keys(KEYS[entry].params || {}).sort();

  // The frame names the KIND of failure and nothing else. The wait is a sentence of its
  // own beside it, because the wait runs down while the panel is on screen and the frame
  // does not: a slot inside the frame could only ever hold the number that was true when
  // the reply was written.
  assert.deepStrictEqual(slots(id), ["detail"], "the frame's slots moved");
  assert.deepStrictEqual(declared(id), ["detail"],
    "the entry declares different slots than it uses");

  Object.keys(named).forEach(kind => {
    const frame = named[kind].reasonId;
    assert.ok(KEYS[frame], "the catalog has no " + frame);
    assert.deepStrictEqual(slots(frame), declared(frame),
      frame + " declares different slots than it uses");
  });

  // And the wait's own two sentences: one while it is running, one when it is over.
  assert.deepStrictEqual(slots("mods.scan.retry.waiting"), ["retry"]);
  assert.deepStrictEqual(declared("mods.scan.retry.waiting"), ["retry"]);
  assert.deepStrictEqual(slots("mods.scan.retry.ready"), []);
});

/* ------------------------------------- rule 5b: a site that ANSWERED was not unreachable */
test("a site that answered has a frame of its own rather than one that contradicts it", () => {
  const named = table("MODS_SCAN_REASONS");

  assert.ok(named.answered && named.answered.reasonId,
    "the page has no frame for a site that answered with something that was not a"
    + " package list");
  assert.ok(KEYS[named.answered.reasonId], "the catalog has no " + named.answered.reasonId);

  // The frame it used to be shown inside asserts the site could not be reached, and the
  // detail it was handed says the site answered. One sentence, both halves, contradicting
  // each other inside one bracket.
  assert.ok(/could not reach/i.test(KEYS[named.unreachable.reasonId].lore),
    "the unreachable frame no longer says what it says, so this rule is stale");
  assert.ok(!/could not reach/i.test(KEYS[named.answered.reasonId].lore),
    "the answered frame still says the site could not be reached");
  assert.ok(/answered/i.test(KEYS[named.answered.reasonId].lore),
    "the answered frame does not say the site answered");

  // And the bridge really picks it, on the name the client writes for that case.
  assert.ok(/"answered", StringComparison\.Ordinal/.test(HOST),
    "the bridge never tells the answered case apart, so it falls to the unreachable frame");
  assert.ok(HOST.indexOf('"' + named.answered.reasonId + '"') > 0,
    "the bridge never sends " + named.answered.reasonId);
});

/* ------------------------------- rule 5c: the wait is worded when the panel DRAWS, not at reply */
test("the wait is worded from an absolute time, so a panel left open stops lying", () => {
  // The failed panel is persistent. A number of seconds worked out when the reply was
  // written is still saying "15 minutes" a quarter of an hour later, which is the whole
  // of the wait it was describing.
  assert.ok(/retryAtUtc = ScanRetryAtUtc\(failure\)/.test(HOST),
    "the bridge does not send the moment the wait ends");
  assert.ok(/retrySeconds = ScanRetrySeconds\(failure, DateTime\.UtcNow\)/.test(HOST),
    "the bridge no longer sends the seconds a page from an older build reads");

  const left = fn("function modsRetryLeft(given){");
  assert.ok(/given\.retryAtUtc/.test(left) && /Date\.now\(\)/.test(left),
    "the page does not work the remaining wait out for itself");
  assert.ok(/retrySeconds/.test(left),
    "there is no fallback for a reply that carried only the seconds");

  const sentence = fn("function modsRetrySentence(given){");
  assert.ok(/mods\.scan\.retry\.ready/.test(sentence),
    "a wait that has run out is still worded as a wait");
  assert.ok(/mods\.scan\.retry\.waiting/.test(sentence),
    "a wait that is still running is not worded at all");

  // Driven, at both ends of it.
  const context = {
    T: (id, params) => id + (params && params.retry ? "(" + params.retry + ")" : ""),
    Date, Number, Math, isNaN,
  };
  vm.createContext(context);
  vm.runInContext(
    fn("function modsRetryWait(seconds){") + "\n}\n"
    + left + "\n}\n" + sentence + "\n}\n"
    + "this.wait=modsRetryWait; this.left=modsRetryLeft; this.say=modsRetrySentence;",
    context, { filename: "app.js#retry" });

  // The moment wins over the number, and it is counted from NOW.
  const inFifteen = new Date(Date.now() + 900000).toISOString();
  assert.ok(Math.abs(context.left({ retryAtUtc: inFifteen, retrySeconds: 3 }) - 900) <= 2,
    "the page read the stale seconds instead of the moment");
  // A moment that has passed is nought, never a negative number.
  assert.strictEqual(context.left({ retryAtUtc: new Date(Date.now() - 60000).toISOString() }), 0);
  // And a reply with only the seconds on it still works.
  assert.strictEqual(context.left({ retrySeconds: 42 }), 42);
  assert.strictEqual(context.left({}), 0);

  // The plural boundaries, which is where a wait is worded wrongly if it is worded at all.
  assert.strictEqual(context.wait(0), "mods.scan.retry.moment");
  assert.strictEqual(context.wait(1), "mods.scan.retry.moment");
  assert.strictEqual(context.wait(2), "mods.scan.retry.seconds");
  assert.strictEqual(context.wait(59), "mods.scan.retry.seconds");
  assert.strictEqual(context.wait(60), "mods.scan.retry.minutes");
  assert.strictEqual(context.wait(900), "mods.scan.retry.minutes");

  // And the two sentences either side of the boundary.
  assert.ok(context.say({ retryAtUtc: inFifteen }).startsWith("mods.scan.retry.waiting"));
  assert.strictEqual(
    context.say({ retryAtUtc: new Date(Date.now() - 1000).toISOString() }),
    "mods.scan.retry.ready");
});

/* ---------------------------------------- rule 5d: the toast is a line, not the paragraph */
test("the failed scan's toast has a sentence of its own", () => {
  const body = fn("async function scanMods(");

  assert.ok(/toast\("ᚦ "\+T\("mods\.scan\.failed\.toast"\)\)/.test(body),
    "the toast is raised from something other than its own id: a panel's paragraph and a"
    + " panel's heading are both the wrong length for a toast");
  assert.ok(KEYS["mods.scan.failed.toast"], "the catalog has no mods.scan.failed.toast");
  assert.strictEqual(KEYS["mods.scan.failed.toast"].mark, "ᚦ",
    "the toast carries no rune, which every other toast does");
});

/* ------------------------------- rule 6: a reason is data, and the words are the page's */
test("neither slot is filled with an English phrase written on the host side", () => {
  // The sentence AROUND these two slots is translated, so a phrase written in C# arrives
  // on a Japanese, Russian or Chinese host's screen in English. The host hands an id and
  // a number; the page turns both into words out of the one catalog.
  assert.ok(/detailName = Tools\.ThunderstoreClient\.ReasonName\(failure\.Reason\)/.test(HOST),
    "the bridge does not hand the kind of failure over as one of the closed names");
  assert.ok(/retrySeconds = ScanRetrySeconds\(failure, DateTime\.UtcNow\)/.test(HOST),
    "the bridge does not hand the wait over as a number of seconds");
  assert.ok(/left <= TimeSpan\.Zero \? 0 :/.test(HOST),
    "a wait that has run out can come over as a negative number of seconds");

  const wording = scanFailureBody();
  assert.ok(wording, "the reply's slots are no longer built in one place");
  ["no reason given", "a moment", " second", " minute"].forEach(phrase => {
    assert.ok(wording.indexOf(phrase) < 0,
      "the bridge still writes " + JSON.stringify(phrase) + " into a translated sentence");
  });
  assert.ok(HOST.indexOf("DescribeWait") < 0,
    "the host still words a wait of its own, which no pack can translate");

  const reader = fn("function modsScanReasonText(){");
  assert.ok(/params\.detail=T\(\(MODS_SCAN_DETAIL\[given\.detailName\]\|\|MODS_SCAN_DETAIL\.unknown\)\.textId\)/
    .test(reader),
    "the page never words the kind of failure, so the slot would show the bare name");
  assert.ok(/params\.retry=modsRetryWait\(modsRetryLeft\(given\)\)/.test(reader),
    "the page never words the wait, so the slot would show a number of seconds");
  assert.ok(/modsRetrySentence\(given\)/.test(reader),
    "the wait is not a sentence of its own, so it cannot say the wait is over");

  const wait = fn("function modsRetryWait(seconds){");
  ["mods.scan.retry.moment", "mods.scan.retry.seconds", "mods.scan.retry.minutes"]
    .forEach(word => {
      assert.ok(wait.indexOf(word) > 0, "the wait is not worded from " + word);
      assert.ok(KEYS[word], "the catalog has no " + word);
    });
  assert.strictEqual(KEYS["mods.scan.retry.seconds"].plural, "count",
    "the seconds entry does not inflect on its number");
  assert.strictEqual(KEYS["mods.scan.retry.minutes"].plural, "count",
    "the minutes entry does not inflect on its number");
});

/* --------------------------- rule 7: every name the host can write has a sentence here */
test("every reason name the host can write is a sentence the catalog owns", () => {
  const list = /public static readonly string\[\] ReasonNames =\s*\{([^}]*)\}/.exec(SITE);
  assert.ok(list, "the client no longer keeps a closed list of reason names");

  const names = list[1].split(",").map(w => w.trim().replace(/"/g, "")).filter(Boolean);
  assert.ok(names.length >= 5, "the closed list emptied out: " + names.join(", "));

  const words = table("MODS_SCAN_DETAIL");
  names.concat(["unknown"]).forEach(name => {
    assert.ok(words[name], "the page has no sentence for " + name + ", so that slot would"
      + " fall back to the general one and say less than the host knew");
    assert.ok(KEYS[words[name].textId], "the catalog has no " + words[name].textId);
  });

  // A STAGE name can no longer become a reason. An exception nothing matched is a read,
  // and a failure with nothing thrown says which of the names its caller meant: the site
  // answering with something that is not a package list is "answered", not "index read".
  assert.ok(/return problem == null \? fallback : "read";/.test(SITE),
    "an unmatched exception's type name can still land inside the sentence");
  assert.ok(/RememberFailure\("answered", ListingIndexUrl\)/.test(SITE),
    "a site that answered with something that was not a package list is still reported"
    + " under the name of the stage that read it");

  // And a press that never left the machine is not worded as the site failing to answer.
  const named = table("MODS_SCAN_REASONS");
  assert.ok(named.busy && named.busy.reasonId, "the page has no words for a busy press");
  assert.ok(KEYS[named.busy.reasonId], "the catalog has no " + named.busy.reasonId);
  assert.ok(HOST.indexOf('"mods.empty.failed.reason.busy"') > 0,
    "the bridge never sends the busy sentence, so a queued press reads as the site"
    + " refusing to answer");
});

Promise.all(pending).then(() => {
  console.log("");
  if (failures.length) {
    console.log("mods scan reason selftest: " + failures.length + " FAILED, " + passed + " passed");
    process.exit(1);
  }
  console.log("mods scan reason selftest: " + passed + " passed");
});
