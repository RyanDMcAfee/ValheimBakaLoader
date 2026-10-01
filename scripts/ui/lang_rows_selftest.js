/* What a language row says, and what it offers, when the pack on disk is older than the one that
 * has been published.
 *
 * WHY THIS EXISTS. On the owner's own machine the Russian and Japanese rows read "Pack from 1.2.0;
 * newer sentences in English until the 1.2.6 pack is out", with no size and nothing to press, while
 * manifest-cache.json in the same install named lang-ja-1.2.6.zip with 1898 of 1898 keys in it. The
 * row could only ask whether a pack was installed AT ALL: langRowLine reached the branch that
 * offers a download only when nothing was on disk, so installed-but-older never offered one, and
 * the sentence it did show claimed a pack was not out when the app was holding the manifest that
 * named it. The one road to those words was relaunching the app with that language already saved.
 *
 * Every chooser and every painter here is the real one, lifted out of app.js and run against the
 * real English catalog. Prints one line per rule and exits non zero on the first failure.
 *
 *     node scripts/ui/lang_rows_selftest.js [app.js] [en.json]
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

function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to) + "\n}";
}

/** One statement, from its opening to the line that ends it. */
function block(opening, closing) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf(closing, from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer ends with " + JSON.stringify(closing));
  return SOURCE.slice(from, to + closing.length);
}

function words(id, params) {
  const entry = KEYS[id];
  if (!entry || typeof entry.lore !== "string") return id;
  return entry.lore.replace(/\{([A-Za-z_][A-Za-z0-9_]*)\}/g,
    (whole, name) => (params && Object.prototype.hasOwnProperty.call(params, name)
      ? String(params[name]) : whole));
}

/** The menu's own choosers and painter, over a fake LANG. */
function menu(current) {
  const context = {
    Object, Array, String, Number, Math, JSON, console,
    T: words,
    esc: t => String(t),
    fmtBytes: n => Math.round(Number(n) / 1024) + " KB",
    langProgressHtml: code => "<progress " + code + ">",
    langReasonText: () => null,
    document: { documentElement: { dataset: { lang: "en" } } },
    LANG: { list: null, status: null, failed: null, busyCode: null, cancelling: null, prog: null },
    S: { version: "1.2.7" },
  };
  vm.createContext(context);
  for (const opening of ["function langRows(){", "function langCurrent(){", "function langNameOf(code){",
    "function langAppVersion(){", "function langRowUpdatable(l){", "function langRowPackVersion(l){",
    "function langRowLine(l){", "function langRowInert(l){", "function langRowHtml(l){"]) {
    vm.runInContext(fn(opening), context, { filename: "app.js#" + opening });
  }
  vm.runInContext('function langRowFor(code){return langRows().find(l=>l.code===code)||null;}', context);
  context.LANG.list = { current, appVersion: "1.2.7", languages: [] };
  context.line = l => vm.runInContext("langRowLine(this.row)", Object.assign(context, { row: l }));
  context.html = l => vm.runInContext("langRowHtml(this.row)", Object.assign(context, { row: l }));
  return context;
}

/** The owner's Japanese row on the day of the walk, with the 1.2.7 manifest behind it. */
const OLDER_WITH_NEWER = {
  code: "ja", nativeName: "日本語", englishName: "Japanese", builtIn: false,
  installed: true, installedVersion: "1.2.0", matchesApp: false, available: true,
  packVersion: "1.2.7", updateAvailable: true, bytes: 521216, status: "machine",
};

/** The same row on the day the 1.2.7 packs genuinely were not cut yet. */
const OLDER_WITH_NOTHING = Object.assign({}, OLDER_WITH_NEWER,
  { available: false, packVersion: null, updateAvailable: false, bytes: 0 });

/** And a pack that is the one this app was built with. */
const CURRENT_PACK = Object.assign({}, OLDER_WITH_NEWER,
  { installedVersion: "1.2.7", matchesApp: true, updateAvailable: false, packVersion: "1.2.7" });

/* ------------------------------------------------------- the three states of an installed pack */

test("an older pack with a newer one published says so, with its size", () => {
  const h = menu("en");
  const said = h.line(OLDER_WITH_NEWER);

  assert.strictEqual(said, words("lang.row.update_ready",
    { version: "1.2.0", pack: "1.2.7", size: "509 KB" }), said);
  assert.ok(said.indexOf("1.2.0") >= 0 && said.indexOf("1.2.7") >= 0,
    "the row names neither the pack on disk nor the one that is ready: " + said);
  assert.ok(said.indexOf("509 KB") >= 0, "the row does not say what the download would cost: " + said);
  assert.ok(said.indexOf("until") < 0,
    "the row still tells the host to wait for a pack the manifest already names: " + said);
});

test("an older pack with nothing published keeps the waiting sentence", () => {
  const h = menu("en");
  const said = h.line(OLDER_WITH_NOTHING);

  assert.strictEqual(said, words("lang.row.older_pack", { version: "1.2.0", app: "1.2.7" }), said);
  assert.ok(said.indexOf("until") >= 0,
    "the one case the waiting sentence is true of lost it: " + said);
});

test("the pack this app was built with says only that it is here", () => {
  const h = menu("en");
  assert.strictEqual(h.line(CURRENT_PACK), words("lang.row.installed"));
});

test("and the language on screen gets the same three answers, with Current in front", () => {
  const h = menu("ja");

  assert.strictEqual(h.line(OLDER_WITH_NEWER), words("lang.row.current_update_ready",
    { version: "1.2.0", pack: "1.2.7", size: "509 KB" }));
  assert.strictEqual(h.line(OLDER_WITH_NOTHING),
    words("lang.row.current_older_pack", { version: "1.2.0", app: "1.2.7" }));
  assert.strictEqual(h.line(CURRENT_PACK), words("lang.row.current"));
});

test("a row names the version the manifest would install, not this app's", () => {
  const h = menu("en");
  /* The manifest comes off the newest release at or below this app's version, so a 1.2.7 app with
     only 1.2.6 packs published must offer 1.2.6 rather than promise a 1.2.7 pack nobody cut. */
  const behind = Object.assign({}, OLDER_WITH_NEWER, { packVersion: "1.2.6" });
  const said = h.line(behind);
  assert.ok(said.indexOf("1.2.6") >= 0, "the row promises a pack the manifest does not name: " + said);
  assert.ok(said.indexOf("1.2.7") < 0, said);

  /* And a host side too old to send the field falls back to this app's version rather than to
     an empty slot in the middle of a sentence. */
  const quiet = Object.assign({}, OLDER_WITH_NEWER, { packVersion: null });
  assert.ok(h.line(quiet).indexOf("1.2.7") >= 0, h.line(quiet));
});

/* ------------------------------------------------------------------ the button beside it */

test("the row carries an Update button, and only when there is something to update", () => {
  const h = menu("en");

  const offered = h.html(OLDER_WITH_NEWER);
  assert.ok(offered.indexOf('data-lang-update="ja"') > 0,
    "a row with a newer pack behind it offers nothing to press: " + offered);
  assert.ok(offered.indexOf(words("lang.update")) > 0, "the button has no word on it: " + offered);

  for (const row of [OLDER_WITH_NOTHING, CURRENT_PACK]) {
    assert.ok(h.html(row).indexOf("data-lang-update") < 0,
      "a row with nothing to fetch offers an Update: " + h.html(row));
  }

  // English ships inside the app and is never downloaded, whatever a manifest says about it.
  const english = { code: "en", nativeName: "English", builtIn: true, installed: true,
    matchesApp: true, available: true, updateAvailable: true, packVersion: "1.2.7" };
  assert.ok(h.html(english).indexOf("data-lang-update") < 0,
    "English was offered a download: " + h.html(english));
});

test("the language on screen can update its own pack too", () => {
  const h = menu("ja");
  const offered = h.html(OLDER_WITH_NEWER);
  assert.ok(offered.indexOf('data-lang-update="ja"') > 0,
    "the host reading an older pack is the one who most needs the offer, and the row has none");
  assert.ok(offered.indexOf("lm-row on") > 0, "the current row lost its own mark");
});

test("a failing row keeps its reason and its Try again instead", () => {
  const h = menu("en");
  h.LANG.failed = { code: "ja", reasonId: "lang.integrity", reasonParams: null };
  const said = h.html(OLDER_WITH_NEWER);
  assert.ok(said.indexOf("data-lang-retry") > 0, "a failed row lost Try again: " + said);
  assert.ok(said.indexOf("data-lang-update") < 0,
    "a row that is already carrying a failure has two buttons on it: " + said);
});

test("the machine-translation tag stands down beside an Update, the way it does beside Try again", () => {
  const h = menu("en");
  const offered = h.html(OLDER_WITH_NEWER);
  assert.ok(offered.indexOf("lm-tag") < 0,
    "the tag and the button are both in the ninety pixels the longest sentence needs: " + offered);
  assert.ok(h.html(CURRENT_PACK).indexOf("lm-tag") > 0,
    "a row with no button lost the tag as well");
});

/* --------------------------------------------- what the press does, and what it must not do */

test("an Update press fetches the pack and leaves the window's language alone", () => {
  const handler = SOURCE.slice(SOURCE.indexOf('$("#langMenu")?.addEventListener("click"'),
    SOURCE.indexOf("document.addEventListener(\"click\",()=>{if(langMenuIsOpen())langMenuClose();})"));

  assert.ok(handler.indexOf('t.closest("[data-lang-update]")') > 0,
    "nothing in the menu answers an Update press");
  assert.ok(handler.indexOf("langDownload(code,code===langCurrent())") > 0,
    "an Update press switches the window's language, which is not what the host asked for");

  // And the keyboard road does not run both commands off one key.
  assert.ok(handler.indexOf('t.closest("[data-lang-update]")||t.closest("[data-lang-retry]")') > 0,
    "Enter on a button inside a row also picks the language under it");
});

test("the download knows how to land without switching, and says so", () => {
  const body = fn("async function langDownload(code,andSwitch){");
  assert.ok(body.indexOf("if(andSwitch===false){") > 0,
    "langDownload cannot land without switching the window");
  assert.ok(body.indexOf('T("lang.update.done.toast"') > 0,
    "a pack that landed without a switch says nothing at all about it");
  assert.ok(body.indexOf("await langRefresh();") > 0,
    "the list is not asked for again, so the row goes on offering a download that has happened");
  assert.ok(KEYS["lang.update.done.toast"], "lang.update.done.toast is not in the English catalog");
  assert.ok(KEYS["lang.update"], "lang.update is not in the English catalog");
  assert.ok(KEYS["lang.row.update_ready"], "lang.row.update_ready is not in the English catalog");
  assert.ok(KEYS["lang.row.current_update_ready"],
    "lang.row.current_update_ready is not in the English catalog");
});

/* ------------------------------- the refresh the host never asked for, and what the menu says */

/** The page's own latch, the report handler that drives it, and the real row painter. */
function writes(current) {
  const h = menu(current);
  h.painted = [];
  h.rendered = 0;
  h.refreshed = 0;
  h.langPaintProgress = () => { h.painted.push(h.LANG.prog && h.LANG.prog.phase); return true; };
  h.renderLangMenu = () => { h.rendered++; return 0; };
  h.langRefresh = () => { h.refreshed++; return Promise.resolve(true); };
  /* The globe, open or shut. lang.list is the one call allowed to reach the release page and the
     host opening the globe is what allows it, so a pack landing under a shut menu redraws what the
     page has rather than asking again. */
  h.menuOpen = true;
  h.langMenuIsOpen = () => h.menuOpen;
  const heard = {};
  h.Native = { on: (name, handler) => { heard[name] = handler; } };
  vm.runInContext(block("const LANG_PHASE_ENDED=", ";"), h, { filename: "app.js#endings" });
  vm.runInContext(block('Native.on("lang.downloadProgress",d=>{', "});"), h,
    { filename: "app.js#progress" });
  vm.runInContext(fn("function langAdoptBusy(code){"), h, { filename: "app.js#adopt" });
  h.report = d => heard["lang.downloadProgress"](d);
  h.adopt = code => vm.runInContext("langAdoptBusy(" + JSON.stringify(code) + ")", h);
  return h;
}

test("a refresh nobody pressed puts the row up and takes it down again", () => {
  const h = writes("ja");
  h.LANG.list.languages = [OLDER_WITH_NEWER];

  /* The app fetches the newer pack for the language on screen on its own account after it updates
     itself. The page was never told, so the row went on offering an Update for the pack that was
     being replaced as it drew. The host posts the same reports it posts for a press. */
  h.report({ code: "ja", phase: "resolving", percent: -1, bytesDone: 0, bytesTotal: 0 });
  assert.strictEqual(h.LANG.busyCode, "ja", "the row knows nothing about a write that is running");
  assert.strictEqual(h.LANG.busyMine, false, "the page claimed a download it did not start");
  assert.ok(h.html(OLDER_WITH_NEWER).indexOf('data-lang-busy="ja"') > 0,
    "the row is not drawn as the one coming down: " + h.html(OLDER_WITH_NEWER));

  h.report({ code: "ja", phase: "downloading", percent: 40, bytesDone: 4, bytesTotal: 10 });
  assert.deepStrictEqual(h.painted, ["resolving", "downloading"],
    "the bar is not being painted in place");

  /* And the ending. A row the host did not put up has no reply to wait for, so the last report is
     the only thing that can take it down: without this it stood until something else redrew it. */
  h.report({ code: "ja", phase: "done", percent: 100, bytesDone: 10, bytesTotal: 10 });
  assert.strictEqual(h.LANG.busyCode, null, "the row stands after the write has finished");
  assert.strictEqual(h.LANG.prog, null);
  assert.strictEqual(h.refreshed, 1, "the rows are not asked for again, so they name the old pack");

  /* And with the globe SHUT the page redraws what it has rather than asking the release page on
     the app's own account: a window that merely started must make no request of its own. */
  const quiet = writes("ja");
  quiet.menuOpen = false;
  quiet.report({ code: "ja", phase: "resolving", percent: -1 });
  quiet.report({ code: "ja", phase: "done", percent: 100 });
  assert.strictEqual(quiet.refreshed, 0, "a pack landing under a shut menu asked the release page");
  assert.ok(quiet.rendered > 0, "the rows were never put back");
  assert.strictEqual(quiet.LANG.busyCode, null);
});

test("a refresh that ends badly takes the row down too, and says nothing", () => {
  for (const ending of ["failed", "cancelled"]) {
    const h = writes("ja");
    h.report({ code: "ja", phase: "resolving", percent: -1 });
    h.report({ code: "ja", phase: ending, percent: -1 });
    assert.strictEqual(h.LANG.busyCode, null, "a " + ending + " write left its row standing");
    assert.strictEqual(h.refreshed, 0, "nothing landed, so there is nothing to re-read");
    assert.ok(h.rendered > 0, "the rows were never put back");
  }
});

test("a download this window started still ends on its own answer, not on a report", () => {
  const h = writes("ja");
  h.LANG.busyCode = "ja";
  h.LANG.busyMine = true;

  h.report({ code: "ja", phase: "done", percent: 100 });
  assert.strictEqual(h.LANG.busyCode, "ja",
    "the report took down a row whose RPC has not answered yet, which races the answer");
  assert.strictEqual(h.refreshed, 0);
  assert.deepStrictEqual(h.painted, ["done"]);
});

test("the menu's own answer is the authority when no report has reached this window", () => {
  const h = writes("en");

  /* The globe can be opened in the middle of a refresh, in which case this window has had no
     report at all, and a refresh can end while the menu is shut. Both are read off lang.list. */
  assert.strictEqual(h.adopt("ru"), true);
  assert.strictEqual(h.LANG.busyCode, "ru");
  assert.strictEqual(h.LANG.prog.phase, "resolving");
  assert.strictEqual(h.adopt("ru"), false, "the same answer twice rebuilt the row");

  assert.strictEqual(h.adopt(null), true);
  assert.strictEqual(h.LANG.busyCode, null);
  assert.strictEqual(h.LANG.prog, null);

  // And it never touches a download this window is waiting on.
  h.LANG.busyCode = "ja";
  h.LANG.busyMine = true;
  assert.strictEqual(h.adopt(null), false);
  assert.strictEqual(h.LANG.busyCode, "ja");

  // The menu asks for it on every refresh rather than only when a report arrives.
  const refresh = fn("async function langRefresh(){");
  assert.ok(refresh.indexOf("langAdoptBusy(r.busyCode);") > 0,
    "the list's answer is read without its busyCode, so a window that opened the globe mid write"
    + " draws an Update over a pack that is being replaced");
});

test("no row offers an Update while a pack is being written", () => {
  const h = writes("en");
  h.LANG.busyCode = "ru";
  h.LANG.busyMine = false;
  h.LANG.list.languages = [OLDER_WITH_NEWER,
    { code: "ru", nativeName: "Russian", builtIn: false, installed: true, installedVersion: "1.2.0",
      matchesApp: false, available: true, packVersion: "1.2.7", updateAvailable: true, bytes: 1 }];

  const said = h.html(OLDER_WITH_NEWER);
  assert.ok(said.indexOf("data-lang-update=\"ja\"") > 0, "the row lost its button altogether: " + said);
  assert.ok(said.indexOf(" disabled ") > 0 || said.indexOf(" disabled") > 0,
    "the Update is live while another pack is being written, so the press is refused: " + said);
  /* And it says why, naming the pack that is running rather than "a language pack". */
  assert.ok(said.indexOf(words("lang.reason.busy_language", { language: "Russian" })) > 0,
    "the held button says nothing about what is running: " + said);

  // Nothing held once the write is over.
  h.LANG.busyCode = null;
  assert.ok(h.html(OLDER_WITH_NEWER).indexOf("disabled") < 0, h.html(OLDER_WITH_NEWER));
});

test("a press refused because something else is running says what is running", () => {
  const body = fn("async function langDownload(code,andSwitch){");
  assert.ok(body.indexOf('T("lang.reason.busy_language",{language:langNameOf(LANG.busyCode)})') > 0,
    "the refusal is still the nameless sentence, which sends a host looking for a download they"
    + " never started: " + body.slice(0, 600));
  assert.ok(KEYS["lang.reason.busy_language"],
    "lang.reason.busy_language is not in the English catalog");
  assert.ok(words("lang.reason.busy_language", { language: "X" }).indexOf("X") > 0,
    "the sentence has no slot for the language that is running");

  // And the delegated handler leaves a held button alone.
  const handler = SOURCE.slice(SOURCE.indexOf('$("#langMenu")?.addEventListener("click"'),
    SOURCE.indexOf("document.addEventListener(\"click\",()=>{if(langMenuIsOpen())langMenuClose();})"));
  assert.ok(/if\(update\)\{\s*\/\*[\s\S]*?\*\/\s*if\(update\.disabled\) return;/.test(handler),
    "a press on a held Update still starts a second write: " + handler.slice(-700));
});

test("the page reads the host's answer rather than working it out itself", () => {
  const updatable = fn("function langRowUpdatable(l){");
  assert.ok(updatable.indexOf("l.updateAvailable") > 0,
    "the page decides for itself whether a newer pack exists, which it cannot know: only the host"
    + " side knows which release the manifest came off and what catalogue the installed pack carries");
  assert.ok(updatable.indexOf("!l.builtIn") > 0 && updatable.indexOf("l.installed") > 0,
    "the rule no longer holds that this is about a pack that is on disk and is not English");
});

console.log("");
if (failures.length) {
  console.log("language rows selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("language rows selftest: " + passed + " passed");
