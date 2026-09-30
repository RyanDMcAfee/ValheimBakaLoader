/* The notes under Start with Windows, driven rather than read.
 *
 * WHY THIS EXISTS. Issue 18's reporter sent a log full of "Could not read the startup entry
 * under HKEY_LOCAL_MACHINE: Requested registry access is not allowed." That line was the
 * harmless half. The other half is that an entry written while BakaLoader was running as
 * administrator lives in the machine wide key, an ordinary run cannot remove it, and so
 * turning the switch OFF left the switch reading off while Windows went on starting the app,
 * with nothing in the window to say so.
 *
 *   1. The host side answers an ID, never a sentence: a phrase written in C# arrives on a
 *      Japanese, Russian or Chinese host's screen in English.
 *   2. Every id the host side can answer with is a sentence this catalog owns, and the page
 *      draws nothing at all for an id it has no words for.
 *   3. The elements the notes are drawn into exist, and start out of the layout.
 *   4. The page really asks for them: on the read that paints the card, and again after a save.
 *   5. The save WRITES to the registry only when the switch MOVED, because the Upkeep card
 *      posts every switch on it together. Reads still happen, and they are read only.
 *   6. The notes are repainted with the rest of the card, so a language switch takes them with
 *      everything around them.
 *   7. The two notes that name a path get it from the answer and put it in the sentence, so
 *      nothing reaches the screen with a literal {path} in it and no sentence is assembled
 *      out of pieces here, and each one carries the words that say how to get out of it.
 *   8. A save that FAILED leaves the standing note exactly where it was. A failed save changed
 *      nothing on the machine, so the sentence that was true before it is still the true one.
 *   9. Two notes can stand at once, one about the machine wide key and one about this
 *      account's own, and the card draws both rather than letting the first hide the second.
 *  10. The table, the catalog and every sentence in the source that counts these notes out
 *      loud agree on how many there are, with each sentence's number counted against the
 *      thing that sentence describes rather than against one number spent on all of them.
 *  11. The card WORKS THE NOTES OUT AGAIN rather than showing them once: every one of them
 *      sends the host out of this window to fix it, and the card they come back to has to be
 *      able to tell them whether it worked.
 *
 * Prints one line per rule and exits non zero on the first failure.
 *
 *     node scripts/ui/start_with_windows_selftest.js [app.js] [en.json] [index.html] [Bridge.cs]
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const CATALOG = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");
const PAGE = process.argv[4] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "index.html");
const BRIDGE = process.argv[5]
  || path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs");
const HELPER = process.argv[6]
  || path.join(ROOT, "ValheimBakaLoader", "Tools", "StartupHelper.cs");

const SOURCE = fs.readFileSync(APP, "utf8");
const KEYS = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys;
const HTML = fs.readFileSync(PAGE, "utf8");
const HOST = fs.readFileSync(BRIDGE, "utf8");
const HELP = fs.readFileSync(HELPER, "utf8");

/** The one prefix every sentence under this switch is named with. */
const NOTE_PREFIX = "hearth.upkeep.start_windows.note";

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

/** The table and the two painters that go with it, lifted out of app.js and run for real. */
function reader() {
  const from = SOURCE.indexOf("const START_WIN_NOTES={");
  assert.ok(from > 0, "app.js no longer holds START_WIN_NOTES");
  const opening = SOURCE.indexOf("function renderStartWinNote(given){", from);
  assert.ok(opening > from, "app.js no longer holds renderStartWinNote");
  const to = SOURCE.indexOf("\n}", opening);
  assert.ok(to > opening, "renderStartWinNote no longer closes at the left margin");

  const box = () => ({ textContent: "untouched", style: { display: "" } });
  // Two boxes, because the machine wide key and this account's own key are separate facts and
  // both of them can be wrong at once.
  const els = { "#startWinNote": box(), "#startWinNoteAlso": box() };
  // S is the page's state object. The notes have to be drawable from it alone, because a
  // language switch redraws this card long after the answer that named them is gone.
  const context = {
    $: sel => els[sel] || null,
    FAIL: Symbol("FAIL"),
    S: {},
    /* The real lookup's two steps: the words for the id, then the named slots filled in.
       A stub that skipped the filling would pass a sentence with a literal {path} in it. */
    T: (id, params) => {
      const words = KEYS[id] ? KEYS[id].lore : "MISSING:" + id;
      if (!params) return words;
      return words.replace(/\{(\w+)\}/g, (whole, name) =>
        Object.prototype.hasOwnProperty.call(params, name) ? String(params[name]) : whole);
    },
  };
  vm.createContext(context);
  vm.runInContext(SOURCE.slice(from, to + 2) + "\nthis.TABLE=START_WIN_NOTES;"
    + "\nthis.draw=renderStartWinNote;", context, { filename: "app.js#startWinNote" });
  return {
    el: els["#startWinNote"],
    also: els["#startWinNoteAlso"],
    table: context.TABLE,
    draw: context.draw,
    FAIL: context.FAIL,
    S: context.S,
  };
}

/* ------------------------------------------- rule 1: the wire carries an id, not English */
test("the host side answers an id and the page owns the words", () => {
  const { table } = reader();
  const ids = Object.keys(table).map(k => table[k].noticeId);
  assert.ok(ids.length >= 1, "the page has no list of notes the host can name");

  ids.forEach(id => {
    assert.ok(KEYS[id], "the catalog has no " + id + ", so the card would show the id");
    assert.ok(HOST.indexOf(id) > 0 || HELP.indexOf('"' + id + '"') > 0,
      "nothing on the host side can ever answer " + id);
  });

  // The id itself is declared once, beside the code that decides it.
  assert.ok(/public const string MachineHiveNoticeId = "/.test(HELP),
    "the notice id is no longer a named constant on the host side");

  // And the reply carries them, beside every preference it always carried: the ids, the paths
  // that go in the sentences, and the read that answers when no save named one.
  assert.ok(/var startup = startupNotes \?\? StartupHelper\.NotesFor\(/.test(HOST),
    "the standing notes are no longer read, so only the host who was watching a save go"
    + " through would ever see one");
  assert.ok(/StartupNoticeId = startup\?\.Notice\?\.Id,/.test(HOST),
    "the preferences answer no longer carries the note, so the card cannot draw it");
  assert.ok(/StartupNoticePath = startup\?\.Notice\?\.Path,/.test(HOST),
    "the preferences answer no longer carries the path, so the two notes that name one"
    + " would reach the screen with an empty slot");
  assert.ok(/StartupAlsoNoticeId = startup\?\.Also\?\.Id,/.test(HOST),
    "the preferences answer no longer carries the second note, so a refusal under a machine"
    + " wide entry would be hidden by it");
  assert.ok(/StartupAlsoNoticePath = startup\?\.Also\?\.Path,/.test(HOST),
    "the second note carries no path, so it would reach the screen with an empty slot");

  // And the save works its notes out with the SAME method the read uses, which is what makes
  // the sentence a save showed and the sentence the next open shows one sentence.
  assert.ok(/Notes = NotesFor\(userPreference, entryName, exePath\)/.test(HELP),
    "the save no longer answers from NotesFor, so it can name a note the next open of the"
    + " card contradicts");
});

/* --------------------------- rule 2: the note is drawn, and only for an id we have words for */
test("the note draws the catalog sentence, and nothing at all for an unknown id", () => {
  const { el, table, draw } = reader();
  const known = table[Object.keys(table)[0]];

  draw({ StartWithWindows: true, StartupNoticeId: null });
  assert.strictEqual(el.textContent, "", "a card with nothing to say still shows something");
  assert.strictEqual(el.style.display, "none", "an empty note is still taking up the layout");

  draw({ StartupNoticeId: known.noticeId });
  assert.strictEqual(el.style.display, "", "the note was answered and not shown");
  assert.strictEqual(el.textContent, KEYS[known.textId].lore,
    "the note is not the catalog's sentence");
  assert.ok(/Task Manager/.test(el.textContent),
    "the note does not name the Startup tab, which is the way out that needs no restart");
  assert.ok(/administrator/.test(el.textContent),
    "the note does not say why an ordinary run cannot remove the entry");

  draw({ StartupNoticeId: "something.nobody.wrote" });
  assert.strictEqual(el.textContent, "",
    "an id the page has no words for is rendered, so a host would read an id");
  assert.strictEqual(el.style.display, "none");
});

/* ------------------------------------------------ rule 3: there is somewhere to draw them */
test("the card has the elements, under the switch, and out of the layout until they are needed", () => {
  const at = HTML.indexOf('id="startWinNote"');
  assert.ok(at > 0, "index.html has no startWinNote, so the note is drawn nowhere");

  const row = HTML.lastIndexOf('id="tStartWin"', at);
  assert.ok(row > 0 && row < at,
    "the note no longer sits under the switch it is about");

  const second = HTML.indexOf('id="startWinNoteAlso"');
  assert.ok(second > at,
    "index.html has no startWinNoteAlso under the first box, so a refusal standing under a"
    + " machine wide entry is drawn nowhere");

  [at, second].forEach(where => {
    const line = HTML.slice(HTML.lastIndexOf("<", where), HTML.indexOf(">", where) + 1);
    assert.ok(/display:none/.test(line),
      "a note starts in the layout, so the card opens with an empty band under the switch");
    assert.ok(/class="subval"/.test(line),
      "a note is not styled like the other notes on this card");
  });
});

/* ------------------------------------------------- rule 4: the page really asks for them */
test("the card asks for the notes on load and again after a save", () => {
  assert.ok(/renderStartWinNote\(up\);/.test(SOURCE),
    "the note is never drawn from the read that paints the card, so only the host who was"
    + " watching a save go through would ever see it");
  assert.ok(/rpc\("userprefs\.save",[\s\S]{0,700}?\}\)\.then\(renderStartWinNote\);/.test(SOURCE),
    "a save does not redraw the note, so turning the switch off says nothing");
});

/* ------------------- rule 5: an ordinary save of that card writes nothing to the registry */
test("the registry is reached only by a save that MOVED the switch", () => {
  assert.ok(/if \(StartupHelper\.ApplySavedValue\(prefs, v\)\) startWithWindowsMoved = true;/
    .test(HOST),
    "the bridge no longer decides the move by comparing the carried value with the preference");
  assert.ok(/if \(startWithWindowsMoved\)/.test(HOST),
    "the Run key is not behind the moved flag");
  assert.ok(HOST.indexOf(
    'if (dto.TryGetValue("StartWithWindows", StringComparison.OrdinalIgnoreCase, out _))') < 0,
    "the shape that shipped before is back: the key arriving counts as the switch moving,"
    + " and the card posts all of its switches together");

  // And a write only ever names the current user's hive.
  assert.ok(/Registry\.CurrentUser\.OpenSubKey\(RunKeyPath, writable: true\)/
    .test(fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Tools", "RunKeyRegistry.cs"),
      "utf8")),
    "the Run key write no longer names the current user's hive");
});

/* ------------------------ rule 6: the notes follow a language switch like every other line */
test("the notes are repainted with the rest of the card when the language changes", () => {
  // The painter has to be ON the first-frame repaint list. Every other surface on the Upkeep
  // card is, and the elements carry no data-i18n, so the static walk cannot reach them either:
  // without this line the paragraphs under the switch stay in the language they were painted
  // in while the whole card around them is redrawn in the new one.
  const at = SOURCE.indexOf("function repaintBootCopy(){");
  assert.ok(at > 0, "app.js no longer holds repaintBootCopy");
  const body = SOURCE.slice(at, SOURCE.indexOf("\n}", at));
  assert.ok(/try\{renderStartWinNote\(\);\}catch/.test(body),
    "repaintBootCopy does not redraw the note, so a language switch leaves it in the old"
    + " language and a catalog that lands after the card is painted never reaches it");

  // And the painter really can draw with nothing handed to it, which is all a repaint has.
  const { el, table, draw, S } = reader();
  const known = table[Object.keys(table)[0]];

  draw({ StartupNoticeId: known.noticeId });
  assert.strictEqual(S.startWinNotice, known.noticeId,
    "the note the host side named is not kept, so a repaint has nothing to draw from");

  el.textContent = "the sentence from the language before this one";
  draw();
  assert.strictEqual(el.textContent, KEYS[known.textId].lore,
    "a repaint with no answer to hand left the old sentence standing");
  assert.strictEqual(el.style.display, "", "a repaint took the standing note out of the card");

  // A save that cleared it clears the state too, so the repaint after it draws nothing.
  draw({ StartupNoticeId: null });
  draw();
  assert.strictEqual(el.textContent, "", "a note that was cleared came back on the next repaint");
  assert.strictEqual(el.style.display, "none");
});

/* ----------------------------- rule 7: the path is the answer's, and the sentence is the
   catalog's. Two of the five notes name a folder, and the folder is a fact about THIS
   machine while the sentence around it is a fact about the language: joining them here
   would put an English sentence on a Japanese host's screen, and leaving the slot unfilled
   would put a literal {path} on everybody's. */
test("the notes that name a path get it from the answer and fill the slot", () => {
  const { el, table, draw } = reader();
  const WITH_PATH = ["machineOther", "otherCopy"];

  WITH_PATH.forEach(name => {
    const note = table[name];
    assert.ok(note, "the page no longer has a note called " + name);
    assert.ok(/\{path\}/.test(KEYS[note.textId].lore),
      KEYS[note.textId] ? note.textId + " no longer has a {path} slot" : "no words for " + note.textId);

    draw({ StartupNoticeId: note.noticeId, StartupNoticePath: "D:\\Old\\ValheimBakaLoader.exe" });
    assert.strictEqual(el.style.display, "", name + " was answered and not shown");
    assert.ok(el.textContent.indexOf("D:\\Old\\ValheimBakaLoader.exe") > 0,
      name + " does not name the folder Windows will actually start");
    assert.ok(el.textContent.indexOf("{path}") < 0,
      name + " reached the screen with the slot still in it");
  });

  // The way out is in the words, because a note a host cannot act on is a note that only
  // worries them. The words are asserted whole: a two letter regex like /on/ passes on the
  // word "account" and proves nothing about the sentence at all.
  draw({ StartupNoticeId: table.machineOther.noticeId, StartupNoticePath: "D:\\Old\\x.exe" });
  assert.ok(el.textContent.indexOf("Startup tab in Task Manager") > 0,
    "the machine note no longer names the Startup tab, which is the way out that needs no"
    + " run as administrator");
  assert.ok(el.textContent.indexOf("Startup apps page in Windows Settings") > 0,
    "the machine note names only Task Manager, and Windows 11 puts the same list on the"
    + " Startup apps page in Settings");

  draw({ StartupNoticeId: table.otherCopy.noticeId, StartupNoticePath: "D:\\Old\\x.exe" });
  assert.ok(el.textContent.indexOf("off and then on again") > 0,
    "the other copy note no longer says how to point Windows at this copy: it has to name"
    + " the whole move, off and then on again, in so many words");

  // And the refused one says what the host is looking at: a switch that reads on while
  // nothing will start.
  draw({ StartupNoticeId: table.refused.noticeId, StartupNoticePath: null });
  assert.ok(/refused/.test(el.textContent),
    "the refused note no longer says that Windows refused the write");
  assert.ok(el.textContent.indexOf("{path}") < 0);

  // The same refusal on the way OUT, which went unsaid until 1.2.5: the switch reads off,
  // Windows goes on starting BakaLoader, and the note has to name both halves and the way
  // out, because an ordinary run cannot make Windows let go of that entry.
  assert.ok(table.refusedOff, "the page has no note for a delete Windows refused");
  draw({ StartupNoticeId: table.refusedOff.noticeId, StartupNoticePath: null });
  assert.strictEqual(el.style.display, "", "the refused delete was answered and not shown");
  assert.ok(el.textContent.indexOf("The switch is off in BakaLoader") === 0,
    "the refused delete note no longer opens by saying the switch reads off");
  assert.ok(el.textContent.indexOf("Windows is still set to start it for this account") > 0,
    "the refused delete note no longer says Windows will go on starting BakaLoader");
  assert.ok(el.textContent.indexOf("Startup tab in Task Manager") > 0,
    "the refused delete note names no way out, and this one is the only way out there is");
  assert.ok(el.textContent.indexOf("{path}") < 0);

  // A note that names no path leaves the state clean, so the next one that does cannot
  // inherit a folder from the note before it.
  draw({ StartupNoticeId: table.machineOther.noticeId });
  assert.ok(el.textContent.indexOf("{path}") < 0,
    "an answer with no path left the slot in the sentence");
});

/* ------------------------------- rule 8: a save that failed may not take a warning off the card */
test("a failed save leaves the standing note exactly where it was", () => {
  const { el, also, table, draw, FAIL } = reader();

  draw({
    StartupNoticeId: table.machineOther.noticeId,
    StartupNoticePath: "D:\\Old\\x.exe",
    StartupAlsoNoticeId: table.refusedOff.noticeId,
  });
  const first = el.textContent;
  const second = also.textContent;
  assert.ok(first.length > 0 && second.length > 0, "the two notes were not drawn to begin with");

  // The sentinel a failed rpc hands back. Nothing was written, so nothing about the machine
  // changed, so the sentences that were true a moment ago are still the true ones.
  draw(FAIL);
  assert.strictEqual(el.textContent, first, "a failed save cleared the standing note");
  assert.strictEqual(el.style.display, "", "a failed save took the standing note out of the card");
  assert.strictEqual(also.textContent, second, "a failed save cleared the second note");

  draw(null);
  assert.strictEqual(el.textContent, first, "an empty reply cleared the standing note");
  assert.strictEqual(also.textContent, second, "an empty reply cleared the second note");

  // A repaint has no answer at all and draws what the last real one said.
  draw();
  assert.strictEqual(el.textContent, first, "a repaint after a failed save lost the note");

  // And a real reply that says there is nothing to say still clears them, because that one
  // is an answer about the machine rather than a save that never happened.
  draw({ StartupNoticeId: null });
  assert.strictEqual(el.textContent, "", "a real reply could not clear the note");
  assert.strictEqual(also.textContent, "", "a real reply could not clear the second note");
});

/* ----------------- rule 9: two notes at once, because two keys can be wrong at the same time */
test("the machine note and this account's note stand together rather than hiding each other", () => {
  const { el, also, table, draw } = reader();

  draw({
    StartupNoticeId: table.machineOther.noticeId,
    StartupNoticePath: "D:\\Old\\x.exe",
    StartupAlsoNoticeId: table.refused.noticeId,
  });

  assert.strictEqual(el.style.display, "", "the machine note was answered and not shown");
  assert.strictEqual(also.style.display, "", "the second note was answered and not shown");
  assert.ok(el.textContent.indexOf("every account on this PC") > 0,
    "the first box is not drawing the machine wide sentence");
  assert.strictEqual(also.textContent, KEYS[table.refused.textId].lore,
    "the second box is not drawing this account's own sentence, so a refusal the save met is"
    + " hidden by the machine note on the next open of the card");
  assert.ok(also.textContent.indexOf("{path}") < 0,
    "the second note reached the screen with the slot still in it");

  // The second box takes itself out of the layout again the moment there is one note only,
  // which is nearly always.
  draw({ StartupNoticeId: table.machine.noticeId });
  assert.strictEqual(also.textContent, "", "the second note outlived the answer that named it");
  assert.strictEqual(also.style.display, "none", "the empty second note is still in the layout");
});

/* ---------------------- rule 10: nobody has to keep a count in their head, or in a comment */
test("the table, the catalog and every count written out in the source agree", () => {
  const { table } = reader();
  const inCatalog = Object.keys(KEYS).filter(id => id.indexOf(NOTE_PREFIX) === 0).sort();
  const onPage = Object.keys(table).map(k => table[k].noticeId).sort();

  assert.deepStrictEqual(onPage, inCatalog,
    "the catalog and the page no longer hold the same notes under " + NOTE_PREFIX
    + ": a sentence with nobody to answer it, or an id with no words, has crept in");

  // The prose that counts them out loud, in all four places it is written. THREE DIFFERENT
  // COUNTS live in that prose and they are worked out separately here, each one against the
  // thing its own sentence describes. Deriving one number and spending it on all of them is
  // how a wrong sentence passes: it happened to come out right while there were five notes
  // and exactly one of them was remembered rather than read.
  const WORDS = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

  // Every note the helper can answer with, under the C# name it answers by, read out of the
  // helper rather than listed here a second time.
  const nameFor = {};
  const declared = /const\s+string\s+(\w+)\s*=\s*(?:\r?\n\s*)?"([^"]+)"/g;
  for (let found = declared.exec(HELP); found; found = declared.exec(HELP)) {
    if (found[2].indexOf(NOTE_PREFIX) === 0) nameFor[found[2]] = found[1];
  }
  onPage.forEach(id => assert.ok(nameFor[id],
    "the helper no longer declares a constant for " + id + ", so this rule cannot tell whether"
    + " that note is read back from the registry or remembered for the run"));

  // A note the helper answers out of the field that REMEMBERS a refusal has nothing left on
  // disk behind it, so a later read cannot find it again. Every other one is a standing fact
  // the registry still holds. Counted, so a sixth note of either kind moves the right number
  // and only the right number.
  const remembered = onPage.filter(id =>
    new RegExp("WriteWasRefused\\s*\\?\\s*new StartupNotice\\s*\\{\\s*Id\\s*=\\s*"
      + nameFor[id] + "\\b").test(HELP));
  assert.ok(remembered.length > 0 && remembered.length < onPage.length,
    "every note is remembered for the run, or none of them is, which neither the helper nor"
    + " the sentences in the source describe");

  const all = WORDS[onPage.length];                              // how many notes there are
  const readable = WORDS[onPage.length - remembered.length];     // how many a read finds again
  const beside = WORDS[onPage.length - 1];                       // how many stand beside the first
  assert.ok(all && readable && beside, "there are more notes than this rule has words for");

  assert.ok(SOURCE.indexOf("any of the " + all + " things that leave the switch") > 0,
    "app.js counts the notes under the switch as something other than " + all);
  assert.ok(SOURCE.indexOf(readable + " of the " + all + " are standing facts") > 0,
    "app.js no longer says that " + readable + " of the " + all + " are read back from the"
    + " registry every time the card opens");
  assert.ok(HTML.indexOf(all.charAt(0).toUpperCase() + all.slice(1) + " of them have words") > 0,
    "index.html counts the notes under the switch as something other than " + all);
  assert.ok(HOST.indexOf("The same road carries the other " + beside) > 0,
    "the bridge counts the notes beside the first one as something other than " + beside);
});

/* ------------- rule 11: the card works the notes out again rather than showing them once */
test("the notes are asked for again, from the one place that knows the tab changed", () => {
  // WHY. Every one of these sentences names a way out that leads the host OUT of this window:
  // the Startup tab in Task Manager, the Startup apps page in Settings, another copy of
  // BakaLoader in another folder. They go and do it, they come back, and a card that had only
  // ever asked once would still be showing them the warning they just cleared, with no way to
  // tell whether it worked. The registry moves while BakaLoader is running, so the answer has
  // to be asked for again, not repainted from the last one.
  const refresh = SOURCE.indexOf("async function refreshStartWinNote()");
  assert.ok(refresh > 0, "app.js no longer has a re-read of the notes at all");

  const body = SOURCE.slice(refresh, SOURCE.indexOf("\n}", refresh));
  assert.ok(/rpc\("userprefs\.get"\)/.test(body),
    "the re-read does not ask the host side for anything, so it is a repaint wearing the name"
    + " of a read and the sentence under the switch can only ever be the one the last answer"
    + " carried");
  assert.ok(/Native\.available/.test(body),
    "the re-read is not held to the app: the mock preview has no registry to read and would"
    + " lose whatever it was last drawn from");
  assert.ok(/renderStartWinNote\(up\)/.test(body) && /FAIL/.test(body),
    "the re-read does not hand a real reply to the painter, or does not hold the FAIL sentinel"
    + " back from it");

  // And it is really reached, from the ONE place that knows the tab changed. This is the
  // rule that shipped broken: the re-read was hung on a click listener on #wtabApp, and the
  // listener that actually switches the tab is added three thousand lines further down, in
  // the tab strip's own wiring. Listeners fire in the order they were ADDED, so the first
  // press of App read WORLD_TAB as "server", returned, and only then did worldTab set it to
  // "app". Nothing refreshed, ever, and rule 11 passed because goPage carries the same
  // sentence for the walk into the hall.
  //
  // So the ask has to sit INSIDE worldTab, after the assignment that moves the tab, and no
  // listener on the tab button may read WORLD_TAB at all.
  const tab = SOURCE.indexOf("function worldTab(name){");
  assert.ok(tab > 0, "app.js no longer holds the tab strip's own switch");
  const tabBody = SOURCE.slice(tab, SOURCE.indexOf("\n}", tab));

  const moved = tabBody.indexOf("WORLD_TAB=want;");
  const asked = tabBody.indexOf("refreshStartWinNote()");
  assert.ok(moved > 0, "worldTab no longer moves WORLD_TAB where this rule expects");
  assert.ok(asked > 0,
    "worldTab does not ask for the notes again, so the only road left is a listener on the"
    + " tab button, and that listener reads the tab the hall is LEAVING: a host who cleared"
    + " an entry in Task Manager comes back to the same warning standing");
  assert.ok(asked > moved,
    "the ask sits above the line that moves WORLD_TAB, so it reads the previous tab");
  assert.ok(/if\(want==="app"\)\{/.test(tabBody),
    "worldTab asks on every tab press rather than on the App tab, so pressing Server counts"
    + " as pressing App");

  assert.ok(SOURCE.indexOf('$("#wtabApp").addEventListener') < 0
    && SOURCE.indexOf('$("#wtabApp")?.addEventListener') < 0,
    "a listener is back on the tab button. It is added before the tab strip's own wiring, so"
    + " it reads WORLD_TAB one press behind; worldTab is the only place that knows the tab"
    + " changed");

  // The walk into the hall goes through the same road rather than carrying a second ask, so
  // one walk reads the preferences once.
  assert.ok(/if\(name==="world"\)\{try\{worldTab\(WORLD_TAB\);\}catch\(_\)\{\}\}/.test(SOURCE),
    "the walk into the Settings hall no longer goes through worldTab, so nothing asks for the"
    + " notes again when a host walks in with the App tab already showing");
  assert.strictEqual((SOURCE.match(/refreshStartWinNote\(\)/g) || []).length, 2,
    "the number of places that ask for the notes has changed: the declaration and the one ask"
    + " inside worldTab are the two this rule expects, so a second road has appeared and one"
    + " walk into the hall now reads the preferences twice");
});

test("the flag the boot walk reads is hoisted, so goPage(\"hearth\") at script evaluation cannot hit a dead zone", () => {
  // WORLD_TAB is read by the same walk and is declared with let, which is safe for a different
  // reason: the boot walk goes to the Hearth, so the branch that reads it never runs during the
  // script's own evaluation. Held here so the pair is read together.
  assert.ok(/let WORLD_TAB="server";/.test(SOURCE),
    "WORLD_TAB is not where this rule expects it; re-read the tab strip's own state");
  assert.ok(/^var UPKEEP_PAINTED=false;/m.test(SOURCE),
    "UPKEEP_PAINTED must be declared with var: the first goPage(\"hearth\") runs while the script is still"
    + " being evaluated, thousands of lines above the declaration, and a let there throws ReferenceError");
  const boot=SOURCE.indexOf('goPage("hearth");'); const decl=SOURCE.indexOf("var UPKEEP_PAINTED=false;");
  assert.ok(boot>0 && decl>boot, "the boot walk and the declaration are not where this rule expects; re-read both");
});

console.log("");
if (failures.length) {
  console.log("start with windows selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("start with windows selftest: " + passed + " passed");
