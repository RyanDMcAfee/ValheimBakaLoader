/* A save folder an older build left in a folder named for a variable, and a save folder
 * that moves while the server is up. Both of them are things the page has to SAY.
 *
 * WHY THIS EXISTS. Issue 17. The shipped default app wide save folder is the literal string
 * "%USERPROFILE%\AppData\LocalLow\IronGate\Valheim", and a raw %USERPROFILE% is a FOLDER
 * NAME until something fills it in. Through 1.2.8 the duplicate path handed it raw to
 * Directory.CreateDirectory, which anchored a relative path at the working directory: a real
 * folder called %USERPROFILE% appeared inside the tester's BakaLoader install with his copied
 * world in it. 1.2.9 fills the variable in on every read, which fixes every future realm and
 * leaves the worlds already on disk out of the app's sight. So the fix owes the host two
 * sentences it cannot get from a unit test:
 *
 *   the condition bar row, which names BOTH folders and offers ONE button and never moves
 *   anything on its own, and
 *   the save toast, which says that BakaLoader follows the new folder from now on AND that
 *   the server that is up keeps writing its world to the old one until it restarts.
 *
 * Thirteen rules. They are held over the source the way the worldgen selftest beside this one
 * is, and the ordering ones are the point: a row that is not in CONDITION_ORDER is never drawn,
 * and a row that is never asked for is never raised, and both of those look right in a diff.
 *
 *   1. The row is a kind the bar knows: it is in CONDITION_ORDER.
 *   2. Every sentence it draws comes from the catalog, and every body names both paths.
 *   3. The button is offered for the stray shape and NOT when anything is at the destination.
 *   4. The press goes through paths.moveStray and reads the reply, so the row answers itself.
 *   5. Every refusal behind that button is in HOST_SENTENCES, so none reads as English.
 *   6. The row is asked for on the first frame and again after a realm switch.
 *   7. It is cleared on a realm switch, because the stored folder is a per-realm field.
 *   8. The save toast reads the reply's own field and says BOTH sentences, including when the
 *      difficulty write was refused in the same save.
 *   9. A reply for a realm the host has LEFT paints nothing.
 *  10. Every id any of the above asks for is in the English catalog.
 *  11. The row ranks BELOW every live failure and live state, and above the two lists. This
 *      one earned its place: the row began the release at the top of the bar, and two of its
 *      shapes stand until somebody moves folders in Explorer, so a world save that had just
 *      FAILED on a running server was replaced on the bar by a sentence the host had already
 *      read and could not act on from there.
 *  12. A dismissal is remembered per FACT: the raiser reads `seen` and the close writes the
 *      key, so the same fact is not raised again on the next boot, switch or save, and a
 *      changed fact is news.
 *  13. `Check again` is on every shape, asks the host side now, and says what it found.
 *
 * Prints one line per rule and exits non zero on the first failure.
 *
 *     node scripts/ui/save_folder_row_selftest.js [app.js] [en.json]
 *
 * The paths are there so a mutation can be driven without touching the tree: point it at a
 * copy with the raiser deleted and rules 1 to 7 go red, and at one with the toast branch
 * deleted and rule 8 does.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");

const ROOT = path.join(__dirname, "..", "..");
const APP = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const CATALOG = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "i18n", "en.json");
const SOURCE = fs.readFileSync(APP, "utf8");
const KEYS = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys || {};

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

/* One function of app.js, from its signature to the line that closes it at column 0. */
function fn(name) {
  const start = SOURCE.indexOf("function " + name + "(");
  assert.notStrictEqual(start, -1, name + " is not defined in " + APP);
  const end = SOURCE.indexOf("\n}", start);
  assert.notStrictEqual(end, -1, name + " never closes");
  return SOURCE.slice(start, end);
}

/* The Save Config click handler, read as one block. */
function saveHandler() {
  const start = SOURCE.indexOf('$("#saveCfgBtn").addEventListener("click"');
  assert.notStrictEqual(start, -1, "the Save Config click handler was not found in " + APP);
  const end = SOURCE.indexOf("\n});", start);
  assert.notStrictEqual(end, -1, "the Save Config click handler never closes");
  return SOURCE.slice(start, end);
}

/* Every id a stretch of source asks the catalog for. */
function askedIds(text) {
  const out = new Set();
  const call = /T\(\s*"([^"]+)"/g;
  let hit;
  while ((hit = call.exec(text)) !== null) out.add(hit[1]);
  return out;
}

const KIND = "saveFolderStray";

/* ------------------------------------------------- rule 1: the bar knows the row */
test("the row is a kind the condition bar draws", () => {
  const order = /const\s+CONDITION_ORDER\s*=\s*\[([\s\S]*?)\]/.exec(SOURCE);
  assert.ok(order, "CONDITION_ORDER is gone, so nothing says which row wins");
  assert.ok(order[1].includes('"' + KIND + '"'),
    "CONDITION_ORDER does not name " + KIND + ", so renderConditionBar walks straight past"
    + " the row: it is raised, it is held in the map, and it is never drawn");

  /* And it outranks the two rows that are only lists. A server whose worlds are out of the
     app's sight comes up on an empty world, which is worse than a count of mods with updates
     waiting or a restart that is pending. */
  const names = order[1].split(",").map(s => s.trim().replace(/"/g, ""));
  const mine = names.indexOf(KIND);
  for (const lower of ["modUpdates", "restartPending"]) {
    const at = names.indexOf(lower);
    assert.ok(at === -1 || mine < at,
      KIND + " sits below " + lower + ", so a realm with waiting mod updates hides the row"
      + " that says its worlds are somewhere else");
  }
});

/* ------------------- rule 11: and it ranks below everything happening now */
test("a live failure or a live state outranks the row", () => {
  const order = /const\s+CONDITION_ORDER\s*=\s*\[([\s\S]*?)\]/.exec(SOURCE);
  assert.ok(order, "CONDITION_ORDER is gone, so nothing says which row wins");
  const names = order[1].split(",").map(s => s.trim().replace(/"/g, ""));
  const mine = names.indexOf(KIND);
  assert.ok(mine >= 0, "CONDITION_ORDER does not name " + KIND);

  /* THE BUG THIS HOLDS. The row shipped at the top of the bar, above every one of these.
     Two of its shapes (worlds in both folders, a destination that is there holding nothing)
     are raised from facts BakaLoader cannot change, so they STAND until the host moves
     folders about in Explorer, and both of them coexist with a RUNNING server. A world save
     that failed, or a backup that failed, is minutes of play sitting in memory and it is
     about this second; a sentence about what a build before 1.2.9 did is not. The bar draws
     ONE row, so ranking the standing fact first meant the failure was never drawn. */
  const live = {
    saveFailed: "a world save that just failed, with everything since the last one in memory",
    backupFailed: "a world copy that just failed",
    crashRelaunch: "a server that just crashed",
    serverUpdate: "a server update running or waiting",
    launchHold: "a start held, waiting for an answer",
    appUpdate: "a BakaLoader update waiting on a press",
    bepinexWritten: "what a restart window wrote to the loader",
    bepinexLeftAlone: "a loader a restart window left as it was",
  };
  for (const kind of Object.keys(live)) {
    const at = names.indexOf(kind);
    assert.ok(at >= 0, "CONDITION_ORDER no longer names " + kind);
    assert.ok(at < mine,
      KIND + " outranks " + kind + " (" + live[kind] + "). The bar draws one row, and this"
      + " row stands until somebody moves folders in Explorer, so it would replace a thing"
      + " that is happening right now with a thing the host has already read.");
  }
});

/* ------------------------------------- rule 2: every sentence is a catalog lookup */
test("every sentence it draws comes from the catalog and names both folders", () => {
  const raiser = fn("conditionStraySaveFolder");

  assert.ok(/title\s*:\s*T\("cond\.stray_save\.title"\)/.test(raiser),
    "the row's heading is not a catalog lookup, so it reads English in every language");
  assert.ok(raiser.includes('T("cond.stray_save.body"'),
    "the stray-shape sentence is not asked for");
  assert.ok(raiser.includes('T("cond.stray_save.both"'),
    "the worlds-in-both-folders sentence is not asked for");
  assert.ok(raiser.includes('T("cond.stray_save.destination_empty"'),
    "the sentence for a destination folder that is THERE and holds no worlds is not asked"
    + " for, so that shape would read as one of the other two and name the wrong folder as"
    + " the one holding the worlds");
  assert.ok(raiser.includes('T("cond.stray_save.many"'),
    "the sentence for worlds in MORE THAN ONE old folder is not asked for, so an install"
    + " that was launched from two different working directories would be told about one of"
    + " its two stray trees and nothing at all about the other");

  /* And that one names every folder rather than the first of them, which is the whole
     reason it exists. */
  const many = KEYS["cond.stray_save.many"];
  assert.ok(many && typeof many.lore === "string", "the catalog has no cond.stray_save.many");
  for (const slot of ["{strays}", "{resolved}"]) {
    assert.ok(many.lore.includes(slot),
      "cond.stray_save.many does not name " + slot + ", so the shape that exists to name"
      + " every folder names something else");
  }
  assert.ok(/strays\s*:\s*list/.test(raiser),
    "the list of folders is not handed to the sentence, so {strays} renders as its own braces");
  assert.ok(/s\.strays/.test(raiser),
    "the raiser never reads the reply's own list of folders, so it could only ever name one");

  /* The whole point of the row is that the host can read the two paths and act on them in
     Explorer, so every sentence has to carry both. */
  for (const id of ["cond.stray_save.body", "cond.stray_save.both",
                    "cond.stray_save.destination_empty"]) {
    const entry = KEYS[id];
    assert.ok(entry && typeof entry.lore === "string", "the catalog has no " + id);
    for (const slot of ["{stray}", "{resolved}"]) {
      assert.ok(entry.lore.includes(slot),
        id + " does not name " + slot + ", so the host is told something is wrong and not where");
    }
  }

  /* And the raiser really hands both in, under those names. */
  assert.ok(/stray\s*:\s*String\(s\.stray/.test(raiser) && /resolved\s*:\s*String\(s\.resolved/.test(raiser),
    "the two paths are not handed to the sentence, so the slots render as their own braces");
});

/* -------------------------- rule 3: the button is offered for one shape only */
test("the move is offered for the stray shape and not when the destination is there", () => {
  const raiser = fn("conditionStraySaveFolder");

  assert.ok(/const\s+both\s*=\s*s\.shape\s*===\s*"both"/.test(raiser),
    "the raiser does not tell the worlds-in-both shape apart");
  assert.ok(/const\s+destinationEmpty\s*=\s*s\.shape\s*===\s*"destination_empty"/.test(raiser),
    "the raiser does not tell the destination-is-there-and-empty shape apart");
  assert.ok(/const\s+many\s*=\s*s\.shape\s*===\s*"many"/.test(raiser),
    "the raiser does not tell the more-than-one-old-folder shape apart");
  assert.ok(/actionsHtml\s*:\s*\(\s*\(\s*both\s*\|\|\s*destinationEmpty\s*\|\|\s*many\s*\)\s*\?\s*""/.test(raiser),
    "the move button is offered whatever is on disk. With a folder already at the destination"
    + " a move would be a merge, and with TWO old folders it would move one and leave the"
    + " other, which is a half repair reported as a whole one. Neither is this row's decision");
  assert.ok(raiser.includes('T("cond.stray_save.move")'),
    "the button has no label from the catalog");

  /* Nothing in the raiser moves anything. The move is a press, and only a press. */
  assert.ok(!raiser.includes('rpc("paths.moveStray"'),
    "the raiser moves the folder while it is drawing the row, so a host who did nothing"
    + " has had their worlds moved for them");
});

/* ------------------- rule 4: the press moves it and the row answers itself */
test("the press goes through paths.moveStray and redraws from the reply", () => {
  const raiser = fn("conditionStraySaveFolder");
  assert.ok(/#cbStrayMove[\s\S]*?moveStraySaveFolder\(/.test(raiser),
    "the button is not wired to the press, so it does nothing at all");

  const press = fn("moveStraySaveFolder");
  assert.ok(/rpc\("paths\.moveStray"/.test(press),
    "the press does not call paths.moveStray, so nothing is moved");
  assert.ok(/if\s*\(\s*after\s*===\s*FAIL\s*\)\s*return/.test(press),
    "a refused move is not read as a refusal, so the press would toast success over it");
  assert.ok(press.includes('T("cond.stray_save.moved.toast"'),
    "a move that worked says nothing, so the host cannot tell it from one that did not");

  const movedAt = press.indexOf('rpc("paths.moveStray"');
  const redrawAt = press.lastIndexOf("conditionStraySaveFolder(after)");
  assert.ok(redrawAt > movedAt,
    "the row is not redrawn from the reply after the move, so it stands over a folder that"
    + " is no longer there and offers to move it again");
});

/* ----------------------- rule 5: every refusal is said in the host's words */
test("every refusal behind the button has a sentence of its own", () => {
  const table = /const\s+HOST_SENTENCES\s*=\s*\[([\s\S]*?)\n\];/.exec(SOURCE);
  assert.ok(table, "HOST_SENTENCES is gone, so every host refusal falls back to English");

  for (const pair of [
    ["paths.stray.nothingToMove", "paths.stray.reason.nothing_to_move"],
    ["paths.stray.moveFailed", "paths.stray.reason.move_failed"],
    ["paths.stray.serverRunning", "paths.stray.reason.server_running"],
  ]) {
    assert.ok(table[1].includes('"' + pair[0] + '"'),
      "HOST_SENTENCES does not name " + pair[0] + ", so that refusal arrives as the method"
      + " name and a raw message");
    assert.ok(table[1].includes('"' + pair[1] + '"'),
      "HOST_SENTENCES names no catalog id for " + pair[0]);
  }

  /* The one that carries a reason has to carry it as a slot, or the reason is dropped. */
  const failed = KEYS["paths.stray.reason.move_failed"];
  assert.ok(failed && failed.lore.includes("{reason}"),
    "paths.stray.reason.move_failed names no {reason}, so a host whose folder would not"
    + " move is told that it would not move and nothing about why");
});

/* ------------------------ rule 6: the row is asked for, on both roads in */
test("the row is asked for on the first frame and again after a realm switch", () => {
  assert.ok(/async\s+function\s+refreshStraySaveFolder\s*\(/.test(SOURCE),
    "nothing reads paths.strayCheck, so the row can never be raised");

  const reader = fn("refreshStraySaveFolder") || "";
  assert.ok(reader.includes("paths.strayCheck"), "the reader does not call paths.strayCheck");
  assert.ok(/Native\.call\(/.test(reader),
    "the check goes through rpc, so a host whose disk did not answer gets a toast about a"
    + " question they never asked");

  const boot = SOURCE.indexOf("(async function bootNative()");
  assert.notStrictEqual(boot, -1, "bootNative is gone");
  const bootBody = SOURCE.slice(boot, SOURCE.indexOf("\n  })();", boot));
  assert.ok(bootBody.includes("refreshStraySaveFolder()"),
    "the first frame never asks, so a host whose worlds are out of the app's sight is not"
    + " told before they press Start on what looks like an empty world");

  const switcher = SOURCE.indexOf("async function switchServer(");
  assert.notStrictEqual(switcher, -1, "switchServer is gone");
  const switchBody = SOURCE.slice(switcher, SOURCE.indexOf("\n}", switcher));
  assert.ok(switchBody.includes("refreshStraySaveFolder()"),
    "a realm switch never asks, so the row only ever describes the realm the app opened on");
});

/* ------------------- rule 7: and cleared, because the folder is per realm */
test("the row is cleared on a realm switch", () => {
  const switcher = SOURCE.indexOf("async function switchServer(");
  const switchBody = SOURCE.slice(switcher, SOURCE.indexOf("\n}", switcher));

  const cleared = /\[([^\]]*?)\]\s*\.forEach\(clearCondition\)/.exec(switchBody);
  assert.ok(cleared, "the realm switch no longer clears the previous realm's conditions");
  assert.ok(cleared[1].includes('"' + KIND + '"'),
    "the realm switch does not clear " + KIND + ", so one realm's folders are named on"
    + " another realm's bar, with that realm's move button under them");

  const clearAt = switchBody.search(/\.forEach\(clearCondition\)/);
  const askAt = switchBody.indexOf("refreshStraySaveFolder()");
  assert.ok(clearAt >= 0 && askAt > clearAt,
    "the row is asked for before the clear wipes it, so the switch raises the new realm's"
    + " row and then takes it straight back down");
});

/* ------------------ rule 8: the save toast says both halves of the disagreement */
test("a save that moves a running server's folder says both sentences", () => {
  const handler = saveHandler();

  assert.ok(/r\.SaveFolderMovedWhileRunning/.test(handler),
    "the save reply's own answer is not read, so the one case the plain running sentence"
    + " does not cover passes under it");

  const branch = /else\s+if\(savedirMoved\)\s*\n?\s*toast\(([\s\S]*?)\);/.exec(handler);
  assert.ok(branch, "there is no toast branch for a save folder that moved while the server is up");
  assert.ok(branch[1].includes('T("world.saved.savedir.toast")'),
    "the toast does not say that BakaLoader follows the new folder from now on");
  assert.ok(branch[1].includes('T("world.saved.savedir.running")'),
    "the toast does not say that the running server keeps writing to the old folder, which"
    + " is the half a host would otherwise go looking for their world over");

  /* And the combination: a save that moved a running server's folder AND had its difficulty
     write refused. The difficulty branch catches that save, so unless it carries the
     running-folder sentence the whole point of the pair is lost in exactly the case where
     the host has two things to act on. */
  const refused = /if\(worldGenFailed\)\s*\n?\s*toast\(([\s\S]*?)\);/.exec(handler);
  assert.ok(refused, "there is no toast branch for a refused difficulty write");
  assert.ok(refused[1].includes("savedirMoved")
    && refused[1].includes('T("world.saved.savedir.running")'),
    "a save that BOTH failed the difficulty write and moved a running server's save folder"
    + " says nothing about the folder, so the host is told to press Save again and never"
    + " told their world is still being written somewhere else");

  /* Ahead of the plain running sentence, or it never fires: that branch catches every save
     made while the world is up. */
  const mine = handler.indexOf("savedirMoved");
  const plain = handler.indexOf("cfgServerIsUp()) toast");
  assert.ok(mine >= 0 && plain >= 0 && mine < plain,
    "the branch sits below the plain running toast, which catches every save made while the"
    + " world is up, so this one is dead code");

  /* And a save can take a realm into or out of the stray shape, so the row is asked again. */
  assert.ok(handler.includes("refreshStraySaveFolder()"),
    "a save never re-asks about the stray folder, so a host who typed the folder out by hand"
    + " is left looking at a row about a state they have just left");
});

/* ------------- rule 9: a reply for a realm the host has left paints nothing */
test("an answer about a realm the host has left is not painted", () => {
  const raiser = fn("conditionStraySaveFolder");

  /* refreshStraySaveFolder is fire and forget from the realm switch, so on a quick A to B
     to C the answer for A can come back with C on screen. This row is ONE row about ONE
     realm's folders, with that realm's move button wired under it. */
  assert.ok(/isActiveProfile\(\s*found\.profile\s*\)/.test(raiser),
    "the raiser does not check which realm the answer is about, so a late reply for a realm"
    + " the host has left paints over the realm now showing, move button and all");

  const guard = raiser.indexOf("isActiveProfile(");
  const draws = raiser.indexOf("setCondition(");
  const clears = raiser.indexOf('clearCondition("saveFolderStray")');
  assert.ok(guard >= 0 && draws > guard && clears > guard,
    "the realm check sits below the drawing, so it cannot stop anything");

  /* Returning rather than clearing: a stale "nothing to say" about the realm the host has
     left must not take down the row the realm on screen has. */
  assert.ok(/isActiveProfile\(found\.profile\)\)\s*return;/.test(raiser),
    "a reply for another realm clears the bar instead of being ignored, so a quiet answer"
    + " about realm A takes realm B's row down with it");

  /* And the reply really carries the name, or the guard is reading undefined. */
  assert.ok(/found\s*&&\s*found\.profile/.test(raiser),
    "the guard does not allow for an answer with no realm named");
});

/* ---------------- rule 12: a dismissal is remembered, per fact, for good */
test("a fact the host has waved away is not raised again, and a changed one is", () => {
  const raiser = fn("conditionStraySaveFolder");

  /* THE BUG THIS HOLDS. The row is asked for on the first frame, on every realm switch and
     after every Save Config, and two of its shapes are states only folder surgery ends. A
     dismiss that only cleared the bar meant the same sentence came back on the next boot,
     the next switch and the next save, for ever, about a decision the host had already made.
     It is the same bug the unattended BepInEx notice had, and the same answer. */
  assert.ok(/if\s*\(\s*s\.seen/.test(raiser),
    "the raiser does not read the reply's `seen`, so a row the host has waved away is stood"
    + " straight back up on the next boot, realm switch and Save Config, for ever");
  const seenAt = raiser.indexOf("s.seen");
  const drawsAt = raiser.indexOf("setCondition(");
  assert.ok(seenAt >= 0 && drawsAt > seenAt,
    "the seen check sits below the drawing, so it cannot stop anything");

  assert.ok(/onDismiss\s*:\s*\(\s*\)\s*=>\s*straySaveFolderNoticeDrawn\(/.test(raiser),
    "closing the row writes nothing down, so the host side cannot know the fact was read");

  const closed = fn("straySaveFolderNoticeDrawn");
  assert.ok(/Native\.call\("paths\.strayNoticeSeen"/.test(closed),
    "the close does not tell the host side, so nothing is remembered past this window");
  assert.ok(/key\s*:\s*found\.key/.test(closed),
    "the close does not send the FACT's key, so either nothing is remembered or the wrong"
    + " thing is: a key is what makes a CHANGED fact news again");
  assert.ok(/found\.seen\s*=\s*true/.test(closed),
    "the answer in hand is not marked, so a repaint or a language switch can put the row"
    + " back up before the write has landed");
});

/* ------------------------- rule 13: and Check again is the way back */
test("Check again is on every shape, asks now, and says what it found", () => {
  const raiser = fn("conditionStraySaveFolder");

  /* The row never promises to go away on its own, so it owes the host the one press that
     asks the disk now. On EVERY shape: three of the four end in Explorer, and those are the
     three where the host has just done something and wants to know. */
  assert.ok(raiser.includes('T("cond.stray_save.recheck")'),
    "there is no Check again button, so the only way to re-ask is a realm switch or a relaunch");
  assert.ok(/id="cbStrayRecheck"/.test(raiser),
    "the Check again button has no id, so nothing can be wired to it");
  const acts = /actionsHtml\s*:\s*([\s\S]*?)\n\s{4}again\s*:/.exec(raiser);
  assert.ok(acts, "the row's actions are no longer one expression this rule can read");
  assert.ok(/\)\s*\+\s*`<button[^`]*cbStrayRecheck/.test(acts[1]),
    "Check again is inside the branch that drops the move button, so the three shapes with"
    + " no move have no way to re-ask either");
  assert.ok(/#cbStrayRecheck[\s\S]*?recheckStraySaveFolder\(/.test(raiser),
    "the Check again button is not wired, so it does nothing at all");

  const press = fn("recheckStraySaveFolder");
  assert.ok(/rpc\("paths\.strayCheck"/.test(press),
    "Check again does not ask the host side, so it cannot have found anything");
  assert.ok(/if\s*\(\s*found\s*===\s*FAIL\s*\)\s*return/.test(press),
    "a refused check is not read as a refusal, so the press would toast an answer over it");
  assert.ok(press.includes('T("cond.stray_save.rechecked.clear"'),
    "a check that found nothing left says nothing, so the press the host most wants to"
    + " succeed looks exactly like one that did nothing");
  assert.ok(press.includes('T("cond.stray_save.rechecked.same"'),
    "an unchanged answer redraws the identical row and says nothing, so Check again reads"
    + " as a button that does not work");
  assert.ok(/conditionStraySaveFolder\(found\s*,\s*\{\s*asked\s*:\s*true\s*\}\)/.test(press),
    "the press redraws without saying it was asked for, so an answer the host waved away"
    + " once is silently dropped and the press shows them nothing");
});

/* ------------------------------ rule 10: nothing can render as its own id */
test("every id any of this asks for is in the English catalog", () => {
  const asked = new Set();
  for (const where of [fn("conditionStraySaveFolder"), fn("moveStraySaveFolder"),
                       fn("refreshStraySaveFolder"), fn("recheckStraySaveFolder"),
                       saveHandler()])
    for (const id of askedIds(where)) asked.add(id);

  /* The four refusal sentences are reached by id through HOST_SENTENCES rather than by a
     T() call, so they are named here. */
  asked.add("paths.stray.reason.nothing_to_move");
  asked.add("paths.stray.reason.move_failed");
  asked.add("paths.stray.reason.server_running");
  asked.add("paths.isolated.reason.no_free_name");

  const missing = [...asked].filter(id => !KEYS[id]);
  assert.deepStrictEqual(missing, [],
    "the catalog is missing " + missing.join(", ") + ", so each one renders as its own id");

  /* And the sixteen this release added are all there, named out so a rename says so here. */
  const added = ["cond.stray_save.title", "cond.stray_save.body", "cond.stray_save.both",
    "cond.stray_save.destination_empty", "cond.stray_save.many",
    "cond.stray_save.move", "cond.stray_save.moved.toast",
    "cond.stray_save.recheck", "cond.stray_save.rechecked.clear",
    "cond.stray_save.rechecked.same",
    "paths.stray.reason.nothing_to_move", "paths.stray.reason.move_failed",
    "paths.stray.reason.server_running", "paths.isolated.reason.no_free_name",
    "world.saved.savedir.toast", "world.saved.savedir.running"];
  for (const id of added) assert.ok(KEYS[id], "the catalog has no " + id);
});

console.log("");
if (failures.length) {
  console.log("save folder row selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("save folder row selftest: " + passed + " passed");
