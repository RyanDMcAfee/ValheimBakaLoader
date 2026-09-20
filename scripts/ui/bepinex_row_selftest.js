/* What the loader row says about an install, as a table, plus the rules that made the
 * table necessary.
 *
 * WHY THIS EXISTS. BepInEx used to be three answers: missing, looked after, outside. A real
 * install can be in nine other shapes, and the page drew every one of them as "outside" with
 * an Update button that wrote. A core another mod manager drives, a core that is not a 5.x
 * BepInEx at all, a folder holding files nothing recognises, a core whose BepInEx.dll is
 * gone, a winhttp.dll an antivirus took, an install somebody else has been writing to since
 * BakaLoader put it there: each of those needs its own sentence, its own buttons, and for
 * three of them its own answer sent with the write, because the host side refuses that write
 * without one. A row that reads the same in all of them tells a host nothing and a button
 * that writes without asking is the defect the whole takeover exists to stop.
 *
 *   THE TABLE. Every shape the host's answer can arrive in, driven through the REAL
 *   choosers: the block between the BEPINEX-STATE markers in app.js, evaluated here. A copy
 *   of those functions in this file would pass for ever while the page chose something else.
 *   Each row asks the same six questions: which state, which pill, which sentence, which
 *   buttons, whether the press has to be asked about first, and which of the four shapes the
 *   question takes.
 *
 *   THE RULES. Every id a chooser can name is a sentence the English catalog really has;
 *   every state the table of words knows is one the state chooser can answer and one the
 *   button chooser has an answer for; a press that needs asking about never goes out with an
 *   empty set of answers; the confirm sends what the chooser worked out rather than a hand
 *   typed literal; and every refusal the host side can throw is paired with a sentence in the
 *   page, so no raw .NET message reaches a toast.
 *
 * Prints one line per case and exits non zero on the first failure.
 *
 * node scripts/ui/bepinex_row_selftest.js [app.js]
 *
 * The path is there so a mutation can be driven without touching the tree: point it at a
 * copy with one rule taken out and the rows that rule holds up have to go red, or the table
 * is decoration.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const CATALOG = path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");
const HOST = [
  path.join(ROOT, "ValheimBakaLoader", "Tools", "BepInExService.cs"),
  path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"),
];

let passed = 0;
const failures = [];

function test(name, body) {
  try {
    body();
    passed++;
    console.log("  ok   " + name);
  } catch (problem) {
    failures.push(name + ": " + (problem && problem.message ? problem.message : problem));
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}

const SOURCE = fs.readFileSync(APP, "utf8");
const KEYS = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys;

function block(beginMarker, endMarker) {
  const from = SOURCE.indexOf(beginMarker);
  const to = SOURCE.indexOf(endMarker);
  assert.ok(from >= 0 && to > from, "app.js no longer carries the " + beginMarker + " markers");
  return SOURCE.slice(from, to);
}

/** One whole top level function out of app.js, named by its opening line. */
function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from > 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to);
}

/** The real choosers, out of app.js. The tables they read are lifted out with them: they
    are `const`, which a VM script keeps to itself, so the block is asked for them by name. */
function choosers() {
  const context = {};
  vm.createContext(context);
  vm.runInContext(
    block("/* BEPINEX-STATE-BEGIN", "/* BEPINEX-STATE-END */") +
      "\n;this.TABLES={BEPINEX_ROW_WORDS,BEPINEX_ACTION_WORDS," +
      "BEPINEX_CONFIRM_EXTRA,BEPINEX_OUTSIDE_BODY};",
    context, { filename: "app.js#bepinex-state" });
  for (const name of ["bepInExState", "bepInExRowWords", "bepInExRowActions",
                      "bepInExNeedsConfirm", "bepInExWriteFlags", "bepInExQuestion",
                      "bepInExConsent", "bepInExUnanswered", "bepInExRowVersion",
                      "bepInExRowParams", "bepInExConfirmExtraId", "bepInExBroughtInAtRestart",
                      "bepInExMissingFileList", "bepInExCoreFilePresent",
                      "bepInExLoaderFileGone"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  return context;
}

const B = choosers();

/** An answer out of the block, as plain data. Anything the VM builds carries the VM's own
    Object and Array, so a strict comparison against a literal written here fails on the
    prototype alone and says the values have the same structure. */
function plain(value) {
  return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

/* ------------------------------------------------------------------ the shapes

   The fields are the ones BuildBepInExDto sends. The defaults are a whole install nobody
   has touched; each row below says only what is different about it. */

function dto(changed) {
  return Object.assign({
    installed: true,
    maintainedByBakaLoader: false,
    packVersion: null,
    coreFileVersion: null,
    coreVersion: "5.4.23.5",
    loaderFilePresent: true,
    loaderFileName: "winhttp.dll",
    coreFilePresent: true,
    drivenElsewhere: false,
    foreignCore: false,
    damaged: false,
    unrecognised: false,
    missingFiles: [],
    drifted: false,
    newestBackup: null,
    maintained: true,
    maintenanceAsked: true,
    consent: true,
    consentUnanswered: false,
  }, changed || {});
}

const MINE = { maintainedByBakaLoader: true, packVersion: "5.4.2350" };

const CASES = [
  // --- nothing has answered yet. Unknown is not "no loader" and never draws as one ---
  ["nothing read yet", null,
    { state: null, actions: [], confirm: false, flags: {}, variant: "fresh" }],

  // --- the three answers the row always had ---
  ["nothing installed", dto({ installed: false, coreVersion: null, coreFilePresent: false }),
    { state: "missing", pill: "bepinex.row.pill.missing", msg: "bepinex.row.state.missing",
      actions: ["install"], confirm: false, flags: {}, variant: "fresh" }],
  // no core AND no loose file, which is a folder with nothing of BepInEx in it at all
  ["nothing installed, nothing beside the server either",
    dto({ installed: false, coreVersion: null, coreFilePresent: false, loaderFilePresent: false }),
    { state: "missing", actions: ["install"], confirm: false, flags: {}, variant: "fresh" }],
  ["BakaLoader's own, looked after", dto(Object.assign({}, MINE)),
    { state: "maintained", pill: "bepinex.row.pill.maintained",
      msg: "bepinex.row.state.maintained", actions: [], confirm: false, flags: {},
      variant: "fresh" }],
  ["BakaLoader's own, the host said no", dto(Object.assign({}, MINE,
    { maintained: false, consent: false })),
    { state: "manual", pill: "bepinex.row.pill.manual", msg: "bepinex.row.state.manual",
      actions: ["update"], confirm: false, flags: {}, variant: "fresh" }],

  // --- the switch says yes and nobody has been asked: not an answer, so not looked after ---
  ["BakaLoader's own, never asked", dto(Object.assign({}, MINE,
    { consent: false, consentUnanswered: true, maintenanceAsked: false })),
    { state: "manual", actions: ["update"], confirm: false, flags: {}, variant: "fresh" }],

  // --- somebody else's install ---
  ["an install BakaLoader did not make", dto({}),
    { state: "outside", pill: "bepinex.row.pill.outside", msg: "bepinex.row.state.outside",
      actions: ["update"], confirm: true, flags: { overOutside: true },
      variant: "outside", body: "bepinex.dialog.first.body.outside" }],
  ["the same, with no version to read off the core", dto({ coreVersion: null }),
    { state: "outside", actions: ["update"], confirm: true, flags: { overOutside: true },
      variant: "outside", body: "bepinex.dialog.first.body.outside.noversion" }],

  // --- somebody else has been writing to BakaLoader's own install ---
  ["drifted", dto({ drifted: true }),
    { state: "drifted", pill: "bepinex.row.pill.outside", msg: "bepinex.row.state.drifted",
      actions: ["update"], confirm: true, flags: { overOutside: true },
      extra: "bepinex.dialog.takeover.drifted", variant: "outside" }],

  // --- another mod manager's ---
  ["another mod manager drives it", dto({ drivenElsewhere: true }),
    { state: "elsewhere", pill: "bepinex.row.pill.elsewhere",
      msg: "bepinex.row.state.elsewhere", actions: ["update"], confirm: true,
      flags: { overOutside: true, overDrivenElsewhere: true },
      extra: "bepinex.dialog.takeover.elsewhere", variant: "elsewhere",
      body: "bepinex.dialog.first.body.elsewhere" }],
  ["a pack BakaLoader wrote that another tool now drives", dto(Object.assign({}, MINE,
    { drivenElsewhere: true })),
    { state: "elsewhere", actions: ["update"], confirm: true,
      flags: { overDrivenElsewhere: true }, variant: "elsewhere" }],

  // --- not the framework this pack carries ---
  ["a core that is not a 5.x BepInEx", dto({ foreignCore: true, coreVersion: "6.0.0.0" }),
    { state: "foreign", pill: "bepinex.row.pill.foreign", msg: "bepinex.row.state.foreign",
      actions: ["update"], confirm: true,
      flags: { overOutside: true, overForeign: true },
      extra: "bepinex.dialog.takeover.foreign", variant: "foreign",
      body: "bepinex.dialog.first.body.foreign" }],

  // --- a core nothing recognises, with and without a saved copy to put back ---
  ["an unrecognised core with a backup", dto({ unrecognised: true, damaged: true,
    installed: false, coreVersion: null, coreFilePresent: false,
    newestBackup: "20260920-011500" }),
    { state: "unrecognised", pill: "bepinex.row.pill.unrecognised",
      msg: "bepinex.row.state.unrecognised", actions: ["restore", "install"], confirm: true,
      flags: { overOutside: true, overUnrecognised: true },
      extra: "bepinex.dialog.takeover.unrecognised", variant: "outside",
      body: "bepinex.dialog.first.body.outside.noversion" }],
  ["an unrecognised core with nothing saved", dto({ unrecognised: true, damaged: true,
    installed: false, coreVersion: null, coreFilePresent: false }),
    { state: "unrecognised", actions: ["install"], confirm: true,
      flags: { overOutside: true, overUnrecognised: true } }],

  // --- BakaLoader's own core, broken: its files, so nothing is asked about ---
  ["BakaLoader's own core with BepInEx.dll gone", dto(Object.assign({}, MINE,
    { damaged: true, installed: false, coreVersion: null, coreFilePresent: false,
      newestBackup: "20260920-011500" })),
    { state: "damaged", pill: "bepinex.row.pill.damaged", msg: "bepinex.row.state.damaged",
      actions: ["restore", "install"], confirm: false, flags: {} }],
  ["a broken core nobody owns", dto({ damaged: true, installed: false, coreVersion: null,
    coreFilePresent: false }),
    { state: "damaged", actions: ["install"], confirm: true, flags: { overOutside: true } }],

  // --- the antivirus case. winhttp.dll is half of what "installed" means, so this arrives
  //     looking exactly like "nothing installed" and must not be drawn as one ---
  ["winhttp.dll taken by an antivirus", dto(Object.assign({}, MINE,
    { installed: false, loaderFilePresent: false, missingFiles: ["winhttp.dll"] })),
    { state: "incomplete", pill: "bepinex.row.pill.incomplete",
      msg: "bepinex.row.state.incomplete", actions: ["repair"], confirm: false, flags: {},
      file: "winhttp.dll", variant: "fresh" }],
  ["a core file gone from an install that still reads as whole", dto(Object.assign({}, MINE,
    { missingFiles: ["BepInEx\\core\\0Harmony.dll"] })),
    { state: "incomplete", actions: ["repair"], confirm: false, flags: {},
      file: "BepInEx\\core\\0Harmony.dll" }],

  // --- the SAME antivirus case on an install BakaLoader did not write, which is the one
  //     that had no way of being drawn. There is no note for the file to be missing FROM,
  //     so missingFiles is empty whatever has gone, and the row said "Missing / Not
  //     installed" over a real core on disk: it offered an Install the host side refuses
  //     with bepinex.outsideUnconfirmed, and it put the NOTHING-INSTALLED question to a host
  //     who plainly has BepInEx ---
  ["winhttp.dll taken from an install BakaLoader did not write",
    dto({ installed: false, loaderFilePresent: false }),
    { state: "incomplete", pill: "bepinex.row.pill.incomplete",
      msg: "bepinex.row.state.incomplete", actions: ["repair"], confirm: true,
      flags: { overOutside: true }, file: "winhttp.dll",
      variant: "outside", body: "bepinex.dialog.first.body.outside" }],
  ["the same with no version to read off the core",
    dto({ installed: false, loaderFilePresent: false, coreVersion: null }),
    { state: "incomplete", actions: ["repair"], confirm: true, flags: { overOutside: true },
      file: "winhttp.dll", variant: "outside",
      body: "bepinex.dialog.first.body.outside.noversion" }],
  // the loose file gone from a DRIFTED install, where MissingFiles is blanked host side
  ["winhttp.dll gone from an install ownership was given up on",
    dto({ installed: false, loaderFilePresent: false, drifted: true }),
    { state: "incomplete", actions: ["repair"], confirm: true,
      flags: { overOutside: true }, file: "winhttp.dll", variant: "outside" }],
  // and the file is never named twice when the note already missed it
  ["the note already missed the loose file", dto(Object.assign({}, MINE,
    { installed: false, loaderFilePresent: false, missingFiles: ["winhttp.dll"] })),
    { state: "incomplete", actions: ["repair"], confirm: false, flags: {},
      file: "winhttp.dll", variant: "fresh" }],

  // --- two things true of one install at once. The row can draw one of them; the confirm
  //     has to say what the write would COST, and that is the other one ---
  ["another mod manager's install with its loose file gone",
    dto({ installed: false, loaderFilePresent: false, drivenElsewhere: true }),
    { state: "incomplete", actions: ["repair"], confirm: true,
      flags: { overOutside: true, overDrivenElsewhere: true },
      extra: "bepinex.dialog.takeover.elsewhere", file: "winhttp.dll",
      variant: "elsewhere" }],
  ["a core that is not a 5.x with its loose file gone",
    dto({ installed: false, loaderFilePresent: false, foreignCore: true,
          coreVersion: "6.0.0.0" }),
    { state: "incomplete", actions: ["repair"], confirm: true,
      flags: { overOutside: true, overForeign: true },
      extra: "bepinex.dialog.takeover.foreign", variant: "foreign" }],
];

CASES.forEach(([name, answer, want]) => {
  test(name, () => {
    assert.strictEqual(B.bepInExState(answer), want.state, "state");
    const words = B.bepInExRowWords(answer);
    if (want.pill) assert.strictEqual(words.pillId, want.pill, "pill");
    if (want.msg) assert.strictEqual(words.msgId, want.msg, "sentence");
    assert.deepStrictEqual(plain(B.bepInExRowActions(answer)), want.actions, "buttons");
    if ("confirm" in want)
      assert.strictEqual(B.bepInExNeedsConfirm(answer, "update"), want.confirm, "asked first");
    if (want.flags) assert.deepStrictEqual(plain(B.bepInExWriteFlags(answer)), want.flags, "answers sent");
    if (want.extra) assert.strictEqual(B.bepInExConfirmExtraId(answer), want.extra, "extra sentence");
    if (!want.extra && want.state && want.confirm === false)
      assert.strictEqual(B.bepInExConfirmExtraId(answer), null, "extra sentence");
    if (want.variant) assert.strictEqual(B.bepInExQuestion(answer).variant, want.variant, "question");
    if (want.body) assert.strictEqual(B.bepInExQuestion(answer).bodyId, want.body, "question body");
    if (want.file) assert.strictEqual(B.bepInExRowParams(answer).file, want.file, "the file named");
    // The loose loader file is named once or not at all. It arrives from two places (a note
    // that misses it, and the flat "it is not there" fact), and an install BakaLoader wrote
    // has both.
    const gone = plain(B.bepInExMissingFileList(answer)) || [];
    const loose = gone.filter(f => String(f).toLowerCase() === "winhttp.dll").length;
    assert.ok(loose <= 1, "winhttp.dll is named " + loose + " times");
  });
});

/* ------------------------------------------- the two flat facts the row reads about the files

   An answer from before these fields existed must draw the row it always did rather than
   claiming a file has gone on the strength of a field nobody sent. */

[["a whole install", { }, { core: true, looseGone: false }],
 ["the loose file gone", { loaderFilePresent: false }, { core: true, looseGone: true }],
 ["no core at all", { coreFilePresent: false, loaderFilePresent: false },
   { core: false, looseGone: false }],
 ["nothing read yet", null, { core: false, looseGone: false }],
].forEach(([name, changed, want]) => {
  test("the files: " + name, () => {
    const answer = changed === null ? null : dto(changed);
    assert.strictEqual(B.bepInExCoreFilePresent(answer), want.core, "the core file");
    assert.strictEqual(B.bepInExLoaderFileGone(answer), want.looseGone, "the loose file");
  });
});

test("an answer from before these fields existed claims nothing", () => {
  const old = { installed: true, maintainedByBakaLoader: true, packVersion: "5.4.2350",
                maintained: true, maintenanceAsked: true };
  assert.strictEqual(B.bepInExLoaderFileGone(old), false, "no loose file is claimed gone");
  assert.strictEqual(B.bepInExCoreFilePresent(old), true, "installed means the core is there");
  assert.deepStrictEqual(plain(B.bepInExMissingFileList(old)), [], "nothing is named");
  assert.strictEqual(B.bepInExState(old), "maintained", "the row it always drew");
});

/* ------------------------------- what the row promises a restart will do, against what it does

   The row says out loud that a scheduled restart will bring an install into BakaLoader's
   care. BepInExUnattended.Decide on the host side skips four kinds of install outright, so
   the note has to skip exactly the same four or it is a promise the window breaks. */

const BROUGHT_IN = [
  ["an install BakaLoader did not make, with the yes given", dto({}), true],
  ["the same, with no yes given", dto({ consent: false }), false],
  ["the same, never asked", dto({ consent: false, consentUnanswered: true, maintenanceAsked: false }), false],
  ["an install another mod manager drives", dto({ drivenElsewhere: true }), false],
  ["a core that is not a 5.x BepInEx", dto({ foreignCore: true }), false],
  ["a core nothing recognises", dto({ unrecognised: true, damaged: true, installed: false }), false],
  ["one somebody else has written to", dto({ drifted: true }), false],
  ["BakaLoader's own, already looked after", dto(Object.assign({}, MINE)), false],
  ["nothing installed", dto({ installed: false, coreVersion: null }), false],
  ["nothing read yet", null, false],
];

BROUGHT_IN.forEach(([name, answer, want]) => {
  test("a restart " + (want ? "does" : "does not") + " adopt " + name, () => {
    assert.strictEqual(B.bepInExBroughtInAtRestart(answer), want);
  });
});

test("the row only promises the restart where the window really writes", () => {
  const row = fn("function renderBepInExRow(");
  assert.ok(row.indexOf("const broughtIn=bepInExBroughtInAtRestart(b);") > 0,
    "the row works the promise out itself instead of asking the rule the window uses");
  assert.ok(row.indexOf("T(\"bepinex.row.note.brought_in\")") > 0, "the note is gone");
});

/* ---------------------------------------------------- the switch and the answer, read together */

const CONSENT = [
  ["the host said yes", { consent: true }, true, false],
  ["the host said no", { consent: false }, false, false],
  ["nobody has been asked", { consent: false, consentUnanswered: true }, false, true],
  ["an older answer, yes", { maintained: true, maintenanceAsked: true }, true, false],
  ["an older answer, no", { maintained: false, maintenanceAsked: true }, false, false],
  ["an older answer, never asked", { maintained: true, maintenanceAsked: false }, false, true],
  ["an answer from before the question existed", { installed: true }, false, false],
];

CONSENT.forEach(([name, shape, consent, unanswered]) => {
  test("consent: " + name, () => {
    assert.strictEqual(B.bepInExConsent(shape), consent, "effective");
    assert.strictEqual(B.bepInExUnanswered(shape), unanswered, "unanswered");
  });
});

test("nothing answered at all is neither a yes nor a question", () => {
  assert.strictEqual(B.bepInExConsent(null), false);
  assert.strictEqual(B.bepInExUnanswered(null), false);
});

/* -------------------------------------------------------- which of the two numbers is shown */

const VERSIONS = [
  ["a note recording the pack", Object.assign({}, MINE),
    { version: "5.4.2350", titleId: "bepinex.row.version.pack.title" }],
  ["a note with no pack version in it", { maintainedByBakaLoader: true, coreVersion: "5.4.23.5" },
    { version: "5.4.23.5", titleId: "bepinex.row.version.file.title" }],
  ["no note at all", { coreVersion: "5.4.23.5" },
    { version: "5.4.23.5", titleId: "bepinex.row.version.file.title" }],
  ["an older answer that only carries the old field", { coreFileVersion: "5.4.23.5" },
    { version: "5.4.23.5", titleId: "bepinex.row.version.file.title" }],
  ["no number anywhere", { installed: true }, null],
];

VERSIONS.forEach(([name, shape, want]) => {
  test("version: " + name, () => {
    assert.deepStrictEqual(plain(B.bepInExRowVersion(shape)), want);
  });
});

/* ---------------------------------------------------- the rules, over the real file and catalog */

test("every id the choosers can name is a sentence the catalog has", () => {
  const named = new Set();
  const collect = table => Object.values(table).forEach(row =>
    Object.keys(row).forEach(key => { if (/Id$/.test(key) && row[key]) named.add(row[key]); }));
  collect(B.TABLES.BEPINEX_ROW_WORDS);
  collect(B.TABLES.BEPINEX_ACTION_WORDS);
  collect(B.TABLES.BEPINEX_CONFIRM_EXTRA);
  collect(B.TABLES.BEPINEX_OUTSIDE_BODY);
  // and the four shapes of the question, driven rather than read
  CASES.forEach(([, answer]) => {
    const q = B.bepInExQuestion(answer);
    [q.titleId, q.bodyId, q.keptId, q.noId].forEach(id => { if (id) named.add(id); });
    const shown = B.bepInExRowVersion(answer);
    if (shown) named.add(shown.titleId);
  });

  assert.ok(named.size >= 20, "the choosers name almost nothing: " + named.size);
  const gone = Array.from(named).filter(id => !(id in KEYS));
  assert.deepStrictEqual(gone, [], "the catalog has no sentence for these");
});

test("every state the words know is one the choosers can answer for", () => {
  const states = Object.keys(B.TABLES.BEPINEX_ROW_WORDS);
  const reached = new Set(CASES.map(([, answer]) => B.bepInExState(answer)).filter(Boolean));
  const never = states.filter(state => !reached.has(state));
  assert.deepStrictEqual(never, [], "the table above never reaches these states");
  const unknown = Array.from(reached).filter(state => states.indexOf(state) < 0);
  assert.deepStrictEqual(unknown, [], "the state chooser answers states the words do not know");
});

test("a press that has to be asked about never goes out with nothing to say for itself", () => {
  CASES.forEach(([name, answer]) => {
    if (!B.bepInExNeedsConfirm(answer, "update")) return;
    const flags = plain(B.bepInExWriteFlags(answer));
    assert.ok(Object.keys(flags).length > 0,
      name + " is asked about and then sends no answer, so the host side refuses it");
  });
});

test("a press that needs no question sends no answers either", () => {
  CASES.forEach(([name, answer]) => {
    if (B.bepInExNeedsConfirm(answer, "update")) return;
    assert.deepStrictEqual(plain(B.bepInExWriteFlags(answer)), {},
      name + " sends an answer to a question nobody was asked");
  });
});

test("restore is only ever offered where there is a saved copy to put back", () => {
  CASES.forEach(([name, answer]) => {
    if (plain(B.bepInExRowActions(answer)).indexOf("restore") < 0) return;
    assert.ok(answer && answer.newestBackup, name + " offers a restore with nothing saved");
  });
});

test("the confirm sends what the chooser worked out, not a literal typed at the call site", () => {
  const body = fn("function bepInExTakeoverModal(");
  assert.ok(body.indexOf("bepInExWriteFlags(b)") > 0,
    "the takeover confirm no longer sends the answers the state calls for");
  assert.ok(body.indexOf("bepInExConfirmExtraId(b)") > 0,
    "the takeover confirm no longer carries the consequence of the state it is asked in");
  // and both buttons that can land on somebody else's install go through it
  ["function bepInExUpdateFlow(", "function bepInExInstallFlow("].forEach(opening => {
    const flow = fn(opening);
    assert.ok(/bepInExNeedsConfirm\(S\.bepinex,"(update|install)"\)/.test(flow),
      opening + " writes without asking whether this install is somebody else's");
    assert.ok(flow.indexOf("bepInExTakeoverModal(") > 0,
      opening + " no longer opens the question");
  });
});

/* --------------------------------------------- one row per answer the host side clamps on

   Each of these is a refusal the service makes on its own unless the call carries the
   answer, and the answer only means anything if the host was shown what it costs. So each
   one has to be written in exactly one place, that place has to be the question's own yes,
   and the press the host makes BEFORE the question has to carry none of them. */

const CLAMPS = [
  ["overOutside", "function bepInExWriteFlags("],
  ["overUnrecognised", "function bepInExWriteFlags("],
  ["overDrivenElsewhere", "function bepInExWriteFlags("],
  ["overForeign", "function bepInExWriteFlags("],
  ["allowDowngrade", "function bepInExDowngradeModal("],
];

CLAMPS.forEach(([flag, owner]) => {
  test(flag + " is written in one place, and that place is a question's yes", () => {
    const where = SOURCE.split(flag).length - 1;
    assert.strictEqual(where, 1,
      flag + " is written in " + where + " places, so one of them is not behind a question");
    assert.ok(fn(owner).indexOf(flag) > 0, flag + " is no longer written by " + owner);
  });
});

test("the press the host makes before the question carries no answers at all", () => {
  // both flows send a bare {} on the road that does NOT go through the confirm, and the
  // confirm is the only caller of the chooser that fills one in
  assert.ok(fn("function bepInExUpdateFlow(").indexOf("bepInExWrite(\"bepinex.update\",{},null)") > 0,
    "the Update press sends something other than an empty set of answers");
  assert.ok(fn("function bepInExInstallFlow(").indexOf("bepInExWrite(\"bepinex.install\",{},after)") > 0,
    "the Install press sends something other than an empty set of answers");
  // and the only place those answers are handed to a WRITE is the confirm's own yes. The
  // page names the chooser once more, in the preview seam, where a walk asks what a given
  // answer would come to and nothing is written.
  const handed = SOURCE.match(/bepInExWrite\([^;]*bepInExWriteFlags\(/g) || [];
  assert.strictEqual(handed.length, 1,
    "the answers are handed to " + handed.length + " writes, so one of them skips the question");
  assert.ok(fn("function bepInExTakeoverModal(").indexOf("bepInExWrite(method,bepInExWriteFlags(b)") > 0,
    "the one write that carries the answers is not the confirm's yes");
});

/* ------------------------------------- what the standing "nothing was written" row offers

   The row stands because nobody was watching the window that raised it. A row with nothing
   to press is a dead end, and a row offering a press that cannot help is worse. */

const LEFT_ALONE = [
  ["a pack still soaking", "soak", "write", "bepinex.condition.left_alone.install"],
  ["a pre-release", "prerelease", "write", "bepinex.condition.left_alone.install"],
  ["a locked file", "bepinex.locked", "write", "bepinex.condition.left_alone.retry"],
  ["another mod manager", "drivenElsewhere", "wiki", "bepinex.condition.not_loaded.action"],
  ["a foreign core", "foreign", "wiki", "bepinex.condition.not_loaded.action"],
  ["an install that is newer", "newer", "wiki", "bepinex.condition.not_loaded.action"],
  // the archive that came down is not the one the note recorded, and pressing again fetches
  // the same archive: the only thing a host can usefully do is read what the row is about
  ["a pack that is not what the note recorded", "repairMismatch", "wiki",
    "bepinex.condition.not_loaded.action"],
  // nothing a press can do about these, so nothing is offered
  ["a deprecated package", "deprecated", null, null],
  ["a version the site took down", "pulled", null, null],
  ["a download with no size", "unverified", null, null],
  ["a core nothing recognises", "unrecognised", null, null],
  ["somebody else writing here", "drift", null, null],
  ["a write that fell over", "bepinex.writeFailed", null, null],
];

const acts = (() => {
  const table = SOURCE.slice(SOURCE.indexOf("const BEPINEX_LEFT_ALONE_ACTS={"),
                             SOURCE.indexOf("function bepInExLeftAloneAct("));
  const context = {};
  vm.createContext(context);
  vm.runInContext(table + "\n;this.T=BEPINEX_LEFT_ALONE_ACTS;", context, { filename: "app.js#left-alone" });
  return context.T;
})();

LEFT_ALONE.forEach(([name, reason, act, labelId]) => {
  test("left alone by " + name + " offers " + (act || "nothing"), () => {
    const row = acts[reason] || null;
    if (!act) {
      assert.strictEqual(row, null, "it offers a press that cannot help");
      return;
    }
    assert.ok(row, "it offers nothing to press");
    assert.strictEqual(row.act, act);
    assert.strictEqual(row.labelId, labelId);
    assert.ok(labelId in KEYS, "the catalog has no " + labelId);
  });
});

test("every reason the row can be raised with is one the page has a sentence for", () => {
  LEFT_ALONE.forEach(([name, reason]) => {
    const worded = SOURCE.indexOf("named:\"" + reason + "\"") > 0;
    assert.ok(worded, name + " (" + reason + ") would raise a row with no sentence on it");
  });
});

/* The same question asked of the SERVICE rather than of the table above, which is what
   stops a reason added host side from arriving on a row with no words for it. A window
   that writes nothing and says nothing is the silence section H is about, and the page
   deliberately renders nothing for a reason it does not know rather than printing an id:
   so an unpaired reason is not a visible bug, it is a blank. This is the gate that catches
   it. `pulled` is here because it arrived exactly that way. */
test("every skip reason the service can name is paired in the page", () => {
  const service = fs.readFileSync(HOST[0], "utf8");
  const table = service.slice(service.indexOf("class BepInExSkipReason"));
  const names = new Set();
  const constant = /public const string [A-Za-z]+ = "([A-Za-z]+)";/g;
  let match;
  while ((match = constant.exec(table.slice(0, table.indexOf("\n    }")))) !== null)
    names.add(match[1]);

  assert.ok(names.size >= 9, "the skip reasons did not read: " + names.size);
  const unworded = Array.from(names).filter(r => SOURCE.indexOf("named:\"" + r + "\"") < 0);
  assert.deepStrictEqual(unworded, [], "the page has no sentence for these skip reasons");

  // and every sentence those rows name is really in the catalog
  const reasons = SOURCE.slice(SOURCE.indexOf("const BEPINEX_REASONS=["));
  const rows = reasons.slice(0, reasons.indexOf("];"));
  const paired = /textId:"([^"]+)"/g;
  const missing = [];
  while ((match = paired.exec(rows)) !== null)
    if (!(match[1] in KEYS)) missing.push(match[1]);
  assert.deepStrictEqual(missing, [], "the catalog has no sentence for these");
});

test("the downgrade question only ever follows the refusal that names both numbers", () => {
  const write = fn("async function bepInExWrite(");
  assert.ok(write.indexOf("window.BAKA_ERR_ID===\"bepinex.newer\"") > 0,
    "the page no longer turns the newer-install refusal into a question");
  assert.ok(write.indexOf("bepInExDowngradeModal(method,params,after,window.BAKA_ERR_PARAMS)") > 0,
    "the downgrade question is not handed the two versions the refusal named");
  const ask = fn("function bepInExDowngradeModal(");
  assert.ok(ask.indexOf("allowDowngrade:true") > 0,
    "the downgrade question no longer sends the one answer that makes the write happen");
  // nothing else in the page may send it: an install is never moved backwards unasked
  const sends = SOURCE.split("allowDowngrade:true").length - 1;
  assert.strictEqual(sends, 1, "allowDowngrade is sent from " + sends + " places");
});

test("every refusal the host side can throw is paired with a sentence in the page", () => {
  const source = HOST.map(file => fs.readFileSync(file, "utf8")).join("\n");
  const thrown = new Set();
  const call = /HostFacingException\("(bepinex\.[A-Za-z]+)"/g;
  let match;
  while ((match = call.exec(source)) !== null) thrown.add(match[1]);
  assert.ok(thrown.size > 10, "nothing was read out of the host source: " + thrown.size);

  // the one that is not a refusal the page reads out: it is a notice, answered with a
  // toast and a standing row that carries the offer to open the setting
  thrown.delete("bepinex.alreadyMaintained");
  assert.ok(SOURCE.indexOf("errorId===\"bepinex.alreadyMaintained\"") > 0,
    "the already-looked-after notice is no longer answered on its own road");

  const bare = Array.from(thrown).filter(id => SOURCE.indexOf("named:\"" + id + "\"") < 0).sort();
  assert.deepStrictEqual(bare, [],
    "these refusals would reach a toast as the raw sentence the machine gave");
});

test("every sentence the page pairs a refusal with is one the catalog has", () => {
  const table = SOURCE.slice(SOURCE.indexOf("const HOST_SENTENCES=["),
                             SOURCE.indexOf("const LANG_REASONS=["));
  const rows = table.match(/textId:\s*"([^"]+)"/g) || [];
  assert.ok(rows.length > 10, "the refusal table is empty");
  rows.map(row => row.replace(/.*"([^"]+)"$/, "$1"))
      .forEach(id => assert.ok(id in KEYS, "the catalog has no " + id));
});

console.log("");
if (failures.length) {
  console.log("bepinex row selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("bepinex row selftest: " + passed + " passed");
