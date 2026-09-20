/* The five world switches, held between the game's vocabulary, the page and the catalog.
 *
 * WHY THIS EXISTS (issue 15). The game's five boolean starting keys are stored with the
 * world exactly as the difficulty dials are, and they reach the command line the same way,
 * but until 1.2.1 the card drew five dials and no switch: there was no WRITER. That is
 * worse than a missing control, because BakaLoader emits -resetmodifiers at every start
 * and that clears a world's WHOLE starting key list. A host who set "No build cost" in the
 * game client lost it at the first BakaLoader start and had no way in the window to put it
 * back.
 *
 * So a switch that exists in Game/WorldGen.cs and has no control on the page is the defect
 * itself, and a control with no label, no help, no place in the unsaved reader or nothing
 * sending it on save is the same defect a step further along. Each of those is a rule here.
 *
 *   THE NAMES. WorldGen.Switches (C#) and WORLDGEN_SWITCHES (app.js) hold the same five
 *   names in the same order, and neither may grow one the other does not have.
 *   THE CONTROLS. Every row names a toggle and a ? marker that really are in index.html.
 *   THE WORDS. Every id a row names, and the aria id on its marker, is a sentence the
 *   English catalog really has.
 *   THE UNSAVED BAND. worldFormRead reads every one of the five, so an unsaved switch
 *   wears its marker, raises the notice and makes Save Config breathe, exactly as an
 *   unsaved dial does; and renderWorldMods adopts all five after the pull, so a switch the
 *   host has never touched does not raise it.
 *   THE SAVE. Save Config sends the switches, always as a list and never conditionally: an
 *   absent list means "leave the stored switches alone" on the host side, so a page that
 *   sometimes omits it is a page that sometimes cannot turn one off.
 *   THE ROUND TRIP. The real paint and scrape functions out of app.js, driven over a stub
 *   of the five toggles: what is painted comes back, case is normalised, and a key no
 *   toggle stands for never turns one on.
 *   THE PULL THAT NEVER ANSWERED. The card holds ONE object for the selected world and
 *   everything downstream believes it, so a lookup that failed must not be able to pass
 *   itself off as a world that is Normal throughout. Driven, at the bottom of this file.
 *   THE SHAPE OF AN ANSWER. Asking whether something came back is not asking whether a
 *   stored set came back: a bare {} passes the first question and carries no dials, no
 *   keys and no world. Driven as a table over the real test out of app.js.
 *   THE WORLD THAT WAS LEFT. One lookup can be in flight for a world the host has already
 *   moved off. Every draw takes the next number so the late answer is dropped; a draw that
 *   takes none is a draw that cannot supersede anything. Driven.
 *   THE PREVIEW'S OWN DIAL. With no host behind the page there is nothing to be unread
 *   about, so a dial turned by hand there has to survive every later draw. Driven, and the
 *   half that cannot be driven - the preview drawing its card once for the world its own
 *   option list chose - is read out of app.js beside it.
 *
 * Prints one line per rule and exits non zero on the first failure.
 *
 * node scripts/ui/world_switches_selftest.js [app.js]
 *
 * The path is there so a mutation can be driven without touching the tree: point it at a
 * copy with one switch taken out of the table and the rules that name it have to go red,
 * or this file is decoration. Run against 709f241's app.js every rule fails, because
 * 1.2.0 has no WORLDGEN_SWITCHES at all.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const PAGE = path.join(ROOT, "ValheimBakaLoader", "WebUI", "index.html");
const CATALOG = path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");
const WORLDGEN = path.join(ROOT, "ValheimBakaLoader", "Game", "WorldGen.cs");

const SOURCE = fs.readFileSync(APP, "utf8");
const MARKUP = fs.readFileSync(PAGE, "utf8");
const KEYS = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys;
const CSHARP = fs.readFileSync(WORLDGEN, "utf8");

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
  assert.ok(from > 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to);
}

/* ------------------------------------------------------------------ the two tables */

/** WorldGen.Switches, out of the C# the command line is built from. */
function switchesFromGame() {
  const at = CSHARP.indexOf("Switches = new[]");
  assert.ok(at > 0, "Game/WorldGen.cs no longer declares Switches");
  const body = CSHARP.slice(at, CSHARP.indexOf("};", at));
  return (body.match(/"([a-z]+)"/g) || []).map(q => q.slice(1, -1));
}

/** WORLDGEN_SWITCHES, out of the page that draws them. */
function switchesFromPage() {
  const from = SOURCE.indexOf("const WORLDGEN_SWITCHES={");
  assert.ok(from > 0, "app.js no longer holds WORLDGEN_SWITCHES");
  const to = SOURCE.indexOf("const WORLDGEN_SWITCH_KEYS=", from);
  assert.ok(to > from, "WORLDGEN_SWITCH_KEYS no longer follows the table");
  const context = {};
  vm.createContext(context);
  vm.runInContext(SOURCE.slice(from, to) + "\n;this.TABLE=WORLDGEN_SWITCHES;",
    context, { filename: "app.js#world-switches" });
  return context.TABLE;
}

/* A page with no switch table at all is the 1.2.0 defect itself, so it is reported as one
   line rather than as a stack trace from the first rule that tried to read it. */
let GAME, TABLE;
try {
  GAME = switchesFromGame();
  TABLE = switchesFromPage();
} catch (problem) {
  console.log("  FAIL the five world switches are on the page at all");
  console.log("       " + (problem && problem.message ? problem.message : problem));
  console.log("");
  console.log("world switches selftest: 1 FAILED, 0 passed");
  process.exit(1);
}
const NAMES = Object.keys(TABLE);

test("the page draws every switch the game has, and no other", () => {
  assert.ok(GAME.length >= 5, "the game's list did not read: " + GAME.join(", "));
  assert.deepStrictEqual(NAMES, GAME,
    "the page and Game/WorldGen.cs do not hold the same five names in the same order");
});

/* ------------------------------------------------------------------ the controls */

NAMES.forEach(name => {
  const row = TABLE[name];
  test(name + " has a toggle and a help marker on the page", () => {
    assert.ok(row.sw, name + " names no toggle");
    assert.ok(row.marker, name + " names no help marker");
    assert.ok(MARKUP.indexOf('id="' + row.sw + '" data-t') > 0,
      "index.html has no [data-t] toggle with id " + row.sw + ", so the switch cannot be set");
    assert.ok(MARKUP.indexOf('id="' + row.marker + '"') > 0,
      "index.html has no help marker with id " + row.marker);
  });

  test(name + " has a label, a line and a help sentence the catalog really holds", () => {
    ["labelId", "lineId", "helpId"].forEach(field => {
      assert.ok(row[field], name + " names no " + field);
      assert.ok(row[field] in KEYS, "the catalog has no " + row[field]);
    });
    /* The ? marker's spoken label is static markup rather than a field on the row, so it
       is read off the markup: a marker with no data-i18n-aria speaks English for ever. */
    const marker = MARKUP.slice(MARKUP.indexOf('id="' + row.marker + '"'));
    const aria = /data-i18n-aria="([^"]+)"/.exec(marker.slice(0, marker.indexOf(">")));
    assert.ok(aria, row.marker + " carries no data-i18n-aria");
    assert.ok(aria[1] in KEYS, "the catalog has no " + aria[1]);
  });
});

/* -------------------------------------------------------------- the unsaved band */

test("every switch is read by the unsaved-band reader", () => {
  const read = fn("function worldFormRead(");
  assert.ok(/WORLDGEN_SWITCH_KEYS/.test(read),
    "worldFormRead no longer walks the switches, so an unsaved one shows no sign at all");
  assert.ok(/classList\.contains\("on"\)/.test(read),
    "worldFormRead no longer reads a switch's state as a string it can be compared in");
});

test("a switch the host never touched does not raise the unsaved notice", () => {
  const render = fn("async function renderWorldMods(");
  assert.ok(render.indexOf("worldFormAdopt(") > 0, "renderWorldMods adopts nothing");
  assert.ok(/WORLDGEN_SWITCH_KEYS\.map\(key=>WORLDGEN_SWITCHES\[key\]\.sw\)/.test(render),
    "renderWorldMods adopts the dials and not the switches, so every pull marks five " +
    "fields the host has never seen as unsaved");
});

/* --------------------------------------------------------------------- the save */

test("Save Config sends the switches, always as a list", () => {
  const save = SOURCE.slice(SOURCE.indexOf('$("#saveCfgBtn").addEventListener'));
  const body = save.slice(0, save.indexOf("\n});"));
  assert.ok(body.indexOf("const keys=scrapeWorldSwitches();") > 0,
    "Save Config no longer reads the switches off the screen");
  assert.ok(/rpc\("worldgen\.save",\{world:[^}]*modifiers,keys\}\)/.test(body),
    "Save Config no longer sends keys with the dials, so a switch turned off stays on");
  /* Conditionally is not good enough: absent means "leave the stored switches alone" on
     the host side, which is what keeps an older page from wiping them. A page that knows
     about switches has to say what it means every time. */
  assert.ok(!/keys\s*:\s*[a-zA-Z_$]+\s*\?/.test(body),
    "the switch list is sent conditionally, and an absent list is not an empty one");
});

/* ---------------------------------------------------- the paint and scrape, driven

   The REAL functions out of app.js over a stub of the five toggles. A copy of them written
   here would pass for ever while the page did something else. */

function driver() {
  const dom = {};
  NAMES.forEach(name => {
    const on = new Set();
    dom[TABLE[name].sw] = {
      classList: {
        toggle: (cls, want) => { if (want) on.add(cls); else on.delete(cls); },
        contains: cls => on.has(cls),
      },
    };
  });
  const context = {
    WORLDGEN_SWITCHES: TABLE,
    WORLDGEN_SWITCH_KEYS: NAMES,
    $: id => dom[String(id).replace(/^#/, "")] || null,
    swOn: id => !!(dom[id] && dom[id].classList.contains("on")),
  };
  vm.createContext(context);
  vm.runInContext(fn("function applyWorldSwitches(") + "\n}\n" +
    fn("function scrapeWorldSwitches(") + "\n}\n", context,
    { filename: "app.js#world-switch-paint" });
  assert.strictEqual(typeof context.applyWorldSwitches, "function",
    "applyWorldSwitches did not come out of app.js");
  assert.strictEqual(typeof context.scrapeWorldSwitches, "function",
    "scrapeWorldSwitches did not come out of app.js");
  return context;
}

const ROUND_TRIP = [
  ["nothing set", [], []],
  ["one switch", ["nomap"], ["nomap"]],
  ["two switches", ["nomap", "fire"], ["nomap", "fire"]],
  ["every switch", NAMES.slice(), NAMES.slice()],
  ["upper case, as a header could spell it", ["NoMap", " FIRE "], ["nomap", "fire"]],
  // the pass-through: a key no toggle stands for never turns one on, and never comes back
  ["a key no toggle stands for", ["carryweightrate 150"], []],
  ["a value key wearing a switch's name", ["nomap 5"], []],
  ["nothing at all sent", null, []],
];

ROUND_TRIP.forEach(([name, painted, want]) => {
  test("paint and scrape: " + name, () => {
    const page = driver();
    page.applyWorldSwitches(painted);
    assert.deepStrictEqual(JSON.parse(JSON.stringify(page.scrapeWorldSwitches())), want);
  });
});

test("the scrape answers in the table's own order, whatever order it was painted in", () => {
  const page = driver();
  page.applyWorldSwitches(NAMES.slice().reverse());
  assert.deepStrictEqual(JSON.parse(JSON.stringify(page.scrapeWorldSwitches())), NAMES);
});

/* ------------------------------------------- the keys no toggle stands for, and the notice */

test("the carried keys are named on the card rather than dropped in silence", () => {
  const carried = fn("function renderWorldCarried(");
  assert.ok(carried.indexOf("S.worldMods.passThrough") > 0,
    "the carried line no longer reads the keys the host side kept back for it");
  assert.ok(carried.indexOf('T("world.wgs.carried"') > 0, "the carried line has no sentence");
  assert.ok("world.wgs.carried" in KEYS, "the catalog has no world.wgs.carried");
  const screen = fn("function worldModsFromScreen(");
  assert.ok(screen.indexOf("passThrough") > 0,
    "reading the card back drops the keys no toggle stands for, so the next save loses them");
});

/* The other half of the preview defect, and the half no driver can reach: the page draws
   the card once while app.js is being evaluated, and the preview lays its world options down
   hundreds of lines later without asking for another draw. Nothing on that road fires an
   event, so the held set goes on being about the empty field the first draw found. */
test("the preview draws the card for the world its own option list chose", () => {
  const at = SOURCE.indexOf('if(!WORLD_LIST_MOCK.includes("Final Sunset"))');
  assert.ok(at > 0, "app.js no longer holds the preview's own world list");
  const to = SOURCE.indexOf("/* mock config vault", at);
  assert.ok(to > at, "the preview's world list no longer runs into the mock config vault");
  assert.ok(/\n\s*renderWorldMods\(\);/.test(SOURCE.slice(at, to)),
    "the preview lays its world options down and never draws the card for them, so the " +
    "card goes on holding the empty field app.js found while it was being evaluated");
});

test("the one note line about switches is drawn and is in the catalog", () => {
  assert.ok("world.wgs.note" in KEYS, "the catalog has no world.wgs.note");
  assert.ok(MARKUP.indexOf('data-i18n="world.wgs.note"') > 0,
    "index.html no longer carries the note about when a switch goes in");
});

test("a first meeting is said once, and saying it is what takes it away", () => {
  const notice = fn("function renderWorldImported(");
  ["world.wgs.imported.title", "world.wgs.imported.body", "world.wgs.imported.action"]
    .forEach(id => {
      assert.ok(notice.indexOf('T("' + id + '"') > 0, "the notice no longer says " + id);
      assert.ok(id in KEYS, "the catalog has no " + id);
    });
  assert.ok(notice.indexOf('rpc("worldgen.noticeSeen"') > 0,
    "nothing tells the host side the notice was read, so it comes back for ever");
  assert.ok(/Native\.on\("worldgen\.imported"/.test(SOURCE),
    "an import that happens while the hall is open is never drawn");
});

/* The notice says the dials and switches BESIDE it were filled in off this world's own
   header. The card under it redraws for whatever world the field holds, so a notice left
   standing after the host picks another world is a sentence that is false about the very
   controls it is pointing at, and pressing Got it there acknowledges a world that is not on
   screen. Two halves: the pull clears a held one, and the draw refuses to show one that is
   about another world. */
test("the notice never stands over a card drawing another world", () => {
  const pull = fn("async function renderWorldMods(");
  assert.ok(pull.indexOf("if(brought) showWorldImported(brought);") < 0,
    "a pull that brings back no notice still leaves the world before it standing");
  assert.ok(/\n\s*showWorldImported\(brought\);/.test(pull),
    "the pull no longer hands its answer to the notice at all");

  const notice = fn("function renderWorldImported(");
  assert.ok(notice.indexOf("n.world!==worldFieldValue()") > 0,
    "the notice is drawn without asking whether it is about the world on screen");
  assert.ok(pull.indexOf("renderWorldImported();") > 0,
    "a change of world does not ask the notice again until its pull comes back");
});

/* --------------------------------------------- the pull that never answered, driven

   The card holds ONE object for the selected world, S.worldMods, and everything downstream
   believes it: the draw that keeps a held set rather than looking it up again, the dial turn
   that rebuilds it, and Save Config, which writes it to the world.

   A worldgen.get that FAILED used to leave that object standing for this world with an empty
   dial map and an empty switch list, which on screen and in the payload is indistinguishable
   from a world the host has set to Normal throughout with every switch off. Save Config's
   guard asked only whether the object was about the world being saved, so it passed, and the
   host side replaced this world's whole stored map with the empty one and took every switch
   off it. On a world BakaLoader had never met, that same save imported the world's own
   starting keys a line before it and then wrote over them. Either way, the -resetmodifiers
   every start emits took what was left off the world, because what is stored afterwards is
   all the world has. One lookup that did not answer, and a world lost its difficulty.

   So the object says whether it was ever read, and every reader that would ACT on it asks.
   These are DRIVEN rather than read: the real renderWorldMods, the real worldModsFromScreen
   and the real world half of the Save Config handler, lifted out of app.js and run over a
   host and a screen that are stubs. The paint and the scrape are stubs here because the
   round trip above already drives the real ones; what is under test is which of them are
   called and what reaches the host side. */

/** The world half of the Save Config handler, between the two comments that bracket it. */
function saveWorldHalf() {
  const from = SOURCE.indexOf("  // World dials ride along with Save Config");
  const to = SOURCE.indexOf("  // max players rides along too", from);
  assert.ok(from > 0 && to > from,
    "app.js no longer carries the world half of Save Config between its two comments");
  return SOURCE.slice(from, to);
}

/**
 * One card with a host behind it. `answers` maps an rpc name to what it comes back with,
 * as a value or as a function of the arguments; `FAIL` is handed in so the page's own
 * comparison is against the very symbol it was given.
 */
function card(answers, opts) {
  const options = opts || {};
  const calls = [];
  const warnings = [];
  const toasts = [];
  const screen = { mods: {}, keys: [] };
  /* The world the FIELD is holding, and it moves: picking another one is what the host
     does, and half of what is under test down here only happens across a change of world.
     An empty string is a world in its own right here - it is what the field holds while
     app.js is still being evaluated - so it is taken as given rather than defaulted away. */
  let world = options.world === undefined ? "Midgard" : options.world;

  const context = {
    /* Only the two the lifted code reaches for. Everything else it names is a stub below. */
    WORLDGEN: { combat: { sel: "fModCombat" } },
    WORLDGEN_SWITCHES: TABLE,
    WORLDGEN_SWITCH_KEYS: NAMES,
    S: { worldMods: null },
    Native: { available: options.native !== false },
    T: id => id,
    toast: line => toasts.push(String(line)),
    console: { warn: line => warnings.push(String(line)) },
    /* The one element repaintWorldDialCopy looks up, and it is allowed to not be there:
       the note under the dials is static markup and the [data-i18n] walk has already
       redrawn it, so what is under test here is the dials and the switches beside it. */
    $: () => null,
    worldFieldValue: () => world,
    renderWorldImported: () => {},
    showWorldImported: () => {},
    renderWorldCarried: () => {},
    worldFormAdopt: () => {},
    applyWorldModDials: mods => { screen.mods = Object.assign({}, mods || {}); },
    applyWorldSwitches: keys => { screen.keys = Array.isArray(keys) ? keys.slice() : []; },
    scrapeWorldModDials: () => Object.assign({}, screen.mods),
    scrapeWorldSwitches: () => screen.keys.slice(),
  };
  context.FAIL = Symbol("rpc-failed");
  context.rpc = (name, args) => {
    calls.push({ name, args });
    const answer = Object.prototype.hasOwnProperty.call(answers, name) ? answers[name] : null;
    return Promise.resolve(typeof answer === "function" ? answer(args, context.FAIL) : answer);
  };
  vm.createContext(context);

  /* _worldModsSeq is a module level `let` in app.js, so it comes across by hand: the lifted
     functions share this script's own top level scope and see it there. */
  vm.runInContext(
    "let _worldModsSeq=0;\n" +
    fn("function worldGenAnswered(") + "\n}\n" +
    fn("async function renderWorldMods(") + "\n}\n" +
    fn("function worldModsFromScreen(") + "\n}\n" +
    fn("function repaintWorldDialCopy(") + "\n}\n" +
    "this.saveConfigWorldHalf=async function(prefs){" + saveWorldHalf() + "};\n",
    context, { filename: "app.js#world-pull" });

  ["worldGenAnswered", "renderWorldMods", "worldModsFromScreen", "repaintWorldDialCopy",
   "saveConfigWorldHalf"].forEach(name =>
    assert.strictEqual(typeof context[name], "function", name + " did not come out of app.js"));

  return {
    calls, warnings, toasts, screen, page: context,
    get world() { return world; },
    /* The host picking another world in the field. Nothing else changes: the card is only
       redrawn when something asks it to be, exactly as on the page. */
    pick: name => { world = name; },
    draw: () => context.renderWorldMods(),
    /* The words arriving, or the host switching language: the dials are painted again from
       the intended state rather than looked up a second time. */
    repaint: () => context.repaintWorldDialCopy(),
    save: () => context.saveConfigWorldHalf({ WorldName: world }),
    /* A dial turned by hand, which is the page's own change handler in one line. */
    turn: mods => {
      screen.mods = Object.assign({}, mods);
      context.S.worldMods = context.worldModsFromScreen();
    },
    sent: name => calls.filter(c => c.name === name),
  };
}

/** Anything the VM built, as plain data: a cross realm object fails a strict compare. */
function plain(value) {
  return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

/* A stored set as the handler really builds it. RegisterRpc("worldgen.get") answers with
   world, preset, modifiers, the stored keys WHOLE, and the same keys split into the five
   switches and everything else, so the fixture carries all of them rather than the two the
   page happens to read: a fixture shaped like the page's appetite would go on passing the
   day the page starts reading a third field. */
const STORED = args => ({
  world: args.world,
  preset: "",
  modifiers: { combat: "hard" },
  keys: ["nomap", "carryweightrate 150"],
  switches: ["nomap"],
  passThrough: ["carryweightrate 150"],
});
const SAVED = args => ({ world: args.world, switches: args.keys || [], passThrough: ["carryweightrate 150"] });

/* ------------------------------------------------- the shape of an answer, driven

   worldGenAnswered is the whole of the question "did the host side really hand back this
   world's stored set". It used to be `r!==FAIL&&!!r`, which is the question "did anything
   come back at all", and a bare {} answers that one yes while carrying no dials, no keys
   and no world. The card then believed it, and the next Save Config wrote an empty dial map
   and an empty key list to the world; the -resetmodifiers the next start emits did the rest.
   The real function out of app.js, over the answers a bridge can hand back. */
function shapeJudge() {
  const context = {};
  context.FAIL = Symbol("rpc-failed");
  vm.createContext(context);
  vm.runInContext(fn("function worldGenAnswered(") + "\n}\n;this.judge=worldGenAnswered;",
    context, { filename: "app.js#worldgen-shape" });
  assert.strictEqual(typeof context.judge, "function",
    "worldGenAnswered did not come out of app.js");
  return context;
}

const SHAPE = [
  ["the answer the handler really builds",
    { world: "Midgard", preset: "", modifiers: { combat: "hard" }, keys: ["nomap"],
      switches: ["nomap"], passThrough: [] }, true],
  ["a world with nothing stored, which is still a read",
    { world: "Midgard", preset: "", modifiers: {}, keys: [], switches: [], passThrough: [] }, true],
  ["a bare empty object", {}, false],
  ["a modifiers map and no key list", { modifiers: { combat: "hard" } }, false],
  ["a key list and no modifiers map", { keys: ["nomap"] }, false],
  ["modifiers sent as a list", { modifiers: [], keys: [] }, false],
  ["keys sent as a map", { modifiers: {}, keys: {} }, false],
  ["modifiers sent as null", { modifiers: null, keys: [] }, false],
  ["a string where an answer should be", "ok", false],
  ["nothing at all", null, false],
  ["the symbol rpc answers a failed call with", "FAIL", false],
];

/* And the seam under it. The shape test names two fields of an answer that is built in C#,
   so the two have to be held together in both directions: a handler that stopped building
   either field would turn every read into a refusal, and then the card would stand at
   Normal for every world on the machine while Save Config quietly refused to write any of
   them. Read off the handler itself rather than off a copy of it written here. */
test("the shape the read check asks for is the shape the handler really builds", () => {
  const bridge = fs.readFileSync(
    path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"), "utf8");
  const from = bridge.indexOf('RegisterRpc("worldgen.get"');
  assert.ok(from > 0, "the bridge no longer registers worldgen.get");
  const to = bridge.indexOf('RegisterRpc("worldgen.save"', from);
  assert.ok(to > from, "the worldgen.get handler no longer runs into worldgen.save");
  const handler = bridge.slice(from, to);

  assert.ok(/modifiers\s*=/.test(handler),
    "worldgen.get no longer builds a modifiers map, so the page would read every answer " +
    "as a lookup that never came back");
  assert.ok(/\n\s*keys,/.test(handler),
    "worldgen.get no longer builds the stored key list, so the page would read every " +
    "answer as a lookup that never came back");

  /* Neither of them may be conditional either: the page's test is that both are THERE,
     and a handler that leaves one out for a world it has never saved would make exactly
     the worlds that most need saving the ones it refuses to save. */
  assert.ok(/modifiers\s*=\s*prefs\?\.Modifiers\s*\?\?/.test(handler),
    "the modifiers map is no longer built for a world with no stored prefs");
  assert.ok(/var keys = WorldKeyList\(prefs\);/.test(handler),
    "the key list is no longer built from the stored prefs through WorldKeyList, which is " +
    "what answers with an empty list rather than nothing for a world never saved");

  const judge = fn("function worldGenAnswered(");
  assert.ok(judge.indexOf("r.modifiers") > 0 && judge.indexOf("r.keys") > 0,
    "the read check no longer asks for the two fields the handler builds");
});

SHAPE.forEach(([name, answer, want]) => {
  test("the read check: " + name + (want ? " IS a read" : " is not a read"), () => {
    const page = shapeJudge();
    const said = page.judge(answer === "FAIL" ? page.FAIL : answer);
    assert.strictEqual(said, want,
      want
        ? "a real stored set was refused, so the card would stand at Normal for a world " +
          "that has dials on it and Save Config would never write to it again"
        : "this was taken for a world's stored set, and the next Save Config writes it to " +
          "the world, which is what takes the world's own difficulty off it");
  });
});

async function main() {
  /* A lookup that failed, one that came back with nothing at all, and two that came back
     with something that is not a stored set. All four leave the card standing at Normal
     with no switch on, and none of them is the host saying so. */
  for (const [name, answer] of [
    ["failed", (a, fail) => fail],
    ["answered nothing", null],
    ["answered with a bare {}", () => ({})],
    ["answered with a dial map and no key list", () => ({ modifiers: { combat: "hard" } })],
  ]) {
    const run = card({ "worldgen.get": answer, "worldgen.save": SAVED });
    await run.draw();
    await run.save();
    test("a lookup that " + name + " is never written back over the world", () => {
      assert.deepStrictEqual(run.sent("worldgen.save"), [],
        "Save Config wrote the empty dial map to the world, which is what takes a world's " +
        "own difficulty off it at the next start");
      assert.strictEqual(run.toasts.length, 1, "the skip passed in silence under a success toast");
      assert.ok(/world\.difficulty\.not_saved\.toast/.test(run.toasts[0]),
        "the toast said something other than the sentence for a difficulty that was not saved");
      assert.ok(run.warnings.length === 1 && run.warnings[0].indexOf(run.world) > 0,
        "the saga line does not name the world whose difficulty went unsaved: " +
        JSON.stringify(run.warnings));
    });
  }

  /* And the whole point of the guard: a lookup that ANSWERED still writes, including the
     host's deliberate all Normal, which is how a difficulty is cleared on purpose. */
  const read = card({ "worldgen.get": STORED, "worldgen.save": SAVED });
  await read.draw();
  await read.save();
  test("a lookup that answered is written back as it stands", () => {
    const sent = read.sent("worldgen.save");
    assert.strictEqual(sent.length, 1, "Save Config sent " + sent.length + " writes, not one");
    assert.deepStrictEqual(plain(sent[0].args),
      { world: read.world, modifiers: { combat: "hard" }, keys: ["nomap"] });
    assert.deepStrictEqual(read.toasts, [], "a save that went through still complained");
  });

  const cleared = card({ "worldgen.get": STORED, "worldgen.save": SAVED });
  await cleared.draw();
  cleared.turn({});
  await cleared.save();
  test("every dial turned back to Normal still clears the world", () => {
    const sent = cleared.sent("worldgen.save");
    assert.strictEqual(sent.length, 1,
      "the guard swallowed the host's deliberate all Normal, which is the one way a stored " +
      "difficulty is cleared from the window");
    assert.deepStrictEqual(plain(sent[0].args.modifiers), {});
  });

  /* A dial turned on a card that never answered must not turn it into one the save believes:
     the turn rebuilds the held object out of the screen, and the screen is standing at
     Normal for every dial the host did not touch. */
  const turned = card({ "worldgen.get": (a, fail) => fail, "worldgen.save": SAVED });
  await turned.draw();
  turned.turn({ combat: "hard" });
  await turned.save();
  test("a dial turned on a card that never answered is still not written back", () => {
    assert.deepStrictEqual(turned.sent("worldgen.save"), [],
      "turning one dial promoted an unread card into one Save Config wrote, and the four " +
      "dials the host never touched went to the world as Normal");
  });

  /* An unread card has to be asked again, because nothing else asks until the world changes:
     kept, it would stand for the rest of the session and the save would refuse for ever. */
  const again = card({ "worldgen.get": (a, fail) => fail, "worldgen.save": SAVED });
  await again.draw();
  await again.draw();
  test("a card that never answered is asked again the next time it is drawn", () => {
    assert.strictEqual(again.sent("worldgen.get").length, 2,
      "the unread set was held as though it were this world's, so the failure stands for " +
      "the rest of the session");
  });

  const held = card({ "worldgen.get": STORED, "worldgen.save": SAVED });
  await held.draw();
  await held.draw();
  test("a card that answered is not asked again", () => {
    assert.strictEqual(held.sent("worldgen.get").length, 1,
      "a second draw looked the same world up again, which discards an unsaved dial");
  });

  /* The browser preview has no host to ask, so there is nothing to be unread about: a dial
     set there has to survive every later draw exactly as it did before. */
  const preview = card({}, { native: false });
  await preview.draw();
  preview.turn({ combat: "hard" });
  await preview.draw();
  test("with no host behind the page a dial set by hand survives the next draw", () => {
    assert.deepStrictEqual(preview.calls, [], "the preview asked a host that is not there");
    assert.deepStrictEqual(plain(preview.page.S.worldMods.mods), { combat: "hard" },
      "the preview threw the host's dial away on the next draw");
  });

  /* And the same page one frame earlier, which is where it really went wrong. app.js draws
     the card while it is still being evaluated, and at that moment the world field is empty
     because the preview's own option list is laid down hundreds of lines further down. So
     the held set is about "" and the field says Final Sunset, and every dial turned by hand
     was read against a set about another world: the card recorded it as UNREAD, and the
     next draw - picking a world, typing in the New world box, copying a world - pulled it
     back to Normal. There is no host here to have been unread, which is the fix, and the
     preview drawing its card once for the world it chose is the other half of it.
     The repaint is driven on the end of it because it is the same set the language switch
     paints the dials from: once a draw has rebuilt that set at Normal, every later switch
     of language goes on painting Normal, and the dial is not coming back. */
  const early = card({}, { native: false, world: "" });
  await early.draw();
  early.pick("Final Sunset");
  early.turn({ combat: "hard" });
  await early.draw();
  early.repaint();
  test("a dial set in the preview survives a draw under a world the first draw never saw", () => {
    assert.deepStrictEqual(early.calls, [], "the preview asked a host that is not there");
    assert.deepStrictEqual(plain(early.page.S.worldMods.mods), { combat: "hard" },
      "the dial was recorded as an unread card, so the redraw pulled it back to Normal");
    assert.deepStrictEqual(plain(early.screen.mods), { combat: "hard" },
      "the dial is gone off the screen, whatever the card is holding");
  });

  /* THE WORLD THAT WAS LEFT. One lookup can still be out for a world the host has already
     moved off, and it comes back holding that world's dials and switches. Read A, pick B and
     leave B's lookup in flight, pick A again - a draw the card answers out of what it
     already holds, without asking anybody - and then let B's answer land. It has to be
     dropped: painting it puts B's difficulty on a card whose field says A, and the next Save
     Config writes exactly what is on that card to A. The guard is the sequence number, and
     it only works if EVERY draw takes one: a draw that takes none cannot supersede a lookup,
     so the early return above used to hand the card back and leave the late answer matching. */
  let landB = null;
  const race = card({
    "worldgen.get": args => {
      if (args.world === "Asgard") {
        return { world: "Asgard", preset: "", modifiers: { combat: "hard" },
                 keys: ["nomap"], switches: ["nomap"], passThrough: [] };
      }
      return new Promise(resolve => {
        landB = () => resolve({ world: "Vanaheim", preset: "", modifiers: { combat: "veryeasy" },
                                keys: ["fire"], switches: ["fire"], passThrough: [] });
      });
    },
    "worldgen.save": SAVED,
  }, { world: "Asgard" });

  await race.draw();            // A, read and painted
  race.pick("Vanaheim");
  const inFlight = race.draw(); // B's lookup goes out, and stays out
  race.pick("Asgard");
  await race.draw();            // back to A, answered from the set the card is holding
  assert.strictEqual(typeof landB, "function", "B's lookup never went out at all");
  landB();
  await inFlight;

  test("a lookup for the world that was left never paints over the one on screen", () => {
    assert.strictEqual(race.page.S.worldMods.world, "Asgard",
      "the card is now holding the world the host moved OFF, so Save Config would write " +
      "that world's difficulty to the world on screen");
    assert.deepStrictEqual(plain(race.screen.mods), { combat: "hard" },
      "the late answer painted the world that was left over the dials on screen");
    assert.deepStrictEqual(plain(race.screen.keys), ["nomap"],
      "the late answer painted the world that was left over the switches on screen");
  });

  console.log("");
  if (failures.length) {
    console.log("world switches selftest: " + failures.length + " FAILED, " + passed + " passed");
    process.exit(1);
  }
  console.log("world switches selftest: " + passed + " passed");
}

main().catch(problem => {
  console.log("  FAIL the pull and save could not be driven out of app.js at all");
  console.log("       " + (problem && problem.stack ? problem.stack : problem));
  console.log("");
  console.log("world switches selftest: could not run, " + passed + " passed before it");
  process.exit(1);
});
