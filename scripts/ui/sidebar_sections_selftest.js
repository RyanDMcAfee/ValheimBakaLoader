/* The simpler sidebar (1.2.18): five halls on the rail, the other four as folding sections,
 * remembered, and every old road still landing where it did.
 *
 * WHY THIS EXISTS. The owner's spec: the rail shows Dashboard, Players, Map, Mods, Settings
 * and nothing else; the Log is a section of the Dashboard, Statistics a section under the map
 * on the Map hall (it stood on the Dashboard between the bento and the Log until 1.3.0, issue
 * 27), Configs a section under the Mods list, Discord a section at the foot of Settings; the
 * Map's own map is a section too since 1.3.0, like the other halls' first ones; every section
 * folds behind a
 * glowing chevron to the left of its heading and rolls through reveal; open or folded is
 * remembered per section across hall walks and restarts; and every way of jumping somewhere
 * opens the hall, unfolds the section, scrolls to it and lights it. The quiet ways that goes
 * wrong are a hall quietly dropped (its ids gone with it), a section whose default or whose
 * saved answer is not honoured, a road that still walks to a hall the rail no longer lists,
 * a check that still reads currentPage for a hall that is a section now, and the preference
 * missing on one side of the bridge. docs/sidebar.md is the map this holds the page to.
 *
 * The rules:
 *   1. The rail: five halls in the owner's order; the four that left are sections with the ids
 *      they had, each under the hall the spec names, in the spec's order.
 *   2. The table: SECTIONS in app.js names the eight sections with the spec's defaults, and the
 *      markup starts each body the way its default says.
 *   3. The chevron: written to the left of every section heading, glows, rests when idle, says
 *      its state, and is worded from the catalog on every language repaint.
 *   4. Remembered: every move writes UiSections whole through userprefs.save, the saved answer
 *      is read with the Upkeep card's read and set with no roll, and the preference exists on
 *      the host side (the document key, the DTO and the save).
 *   5. Every road in goes through goSection or unfolds with sectSet: the palette, the condition
 *      bar, a mod row's configs entry, the log chip and toast, the realm landings, and no road
 *      walks to a hall that is a section now.
 *   6. The checks that read the old halls read sectOn, and the walk-in work of the old halls
 *      runs on the way open.
 *   7. The glow: 0.5 s in, three pulses over 3 s, 0.5 s out, steady under reduced motion; and
 *      the landing (1.3.0, issue 27): a chevron click and every road in bring the opened section
 *      into view once it has rolled open, never before, never on a fold, by the rule in
 *      sectLandTop (its foot at the pane's foot above the heads stuck there, or its head under
 *      the heads above when it is taller than the room between them).
 *   8. The head stack (1.3.0, issue 27): every section head sticks at the top under the heads
 *      above it and at the foot above the heads below it, from heights measured by an observer
 *      rather than written down; the wrapper draws no box so the head can travel the hall; the
 *      scroll padding is the measured stack, not 120px; and the Settings tab strip stands
 *      outside the folding body, where a press on it unfolds the panels.
 *   9. The Map hall (1.3.0, issue 27): its map is the hall's own section and Statistics stands
 *      under it; the map's height is the pane less the measured head stack and lives on the
 *      row, so no fold changes the map's size and so its zoom; a fit asked with the map folded
 *      is owed rather than lost; a realm switch reads Statistics again; and a refresh asked
 *      while one is out is not dropped.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/sidebar_sections_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const vm = require("vm");
const assert = require("assert");

const ROOT = path.resolve(__dirname, "..", "..");
const WEB = path.join(ROOT, "ValheimBakaLoader", "WebUI");
const APP = fs.readFileSync(path.join(WEB, "app.js"), "utf8").replace(/\r\n/g, "\n");
const CSS = fs.readFileSync(path.join(WEB, "app.css"), "utf8").replace(/\r\n/g, "\n");
const HTML = fs.readFileSync(path.join(WEB, "index.html"), "utf8").replace(/\r\n/g, "\n");
const EN = JSON.parse(fs.readFileSync(path.join(WEB, "i18n", "en.json"), "utf8")).keys;
const PREFS = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Game", "UserPreferences.cs"), "utf8").replace(/\r\n/g, "\n");
const PREFS_FILE = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Game", "UserPreferencesFile.cs"), "utf8").replace(/\r\n/g, "\n");
const BRIDGE = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"), "utf8").replace(/\r\n/g, "\n");
const MAP = fs.readFileSync(path.join(ROOT, "docs", "sidebar.md"), "utf8").replace(/\r\n/g, "\n");

let passed = 0;
const failures = [];
/* A rule may hand back a promise (rule 9 waits on a read it holds open); its line is printed
   when it settles, and the summary waits for it. */
const pending = [];
function test(name, fn) {
  const fail = problem => {
    failures.push(name);
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  };
  try {
    const later = fn();
    if (later && typeof later.then === "function") {
      pending.push(later.then(() => { passed++; console.log("  ok   " + name); }, fail));
      return;
    }
    passed++;
    console.log("  ok   " + name);
  } catch (problem) {
    failures.push(name);
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}
/* Objects made inside the page's context have its prototypes, so they are compared as data. */
function same(actual, expected, why) {
  assert.deepStrictEqual(JSON.parse(JSON.stringify(actual)), expected, why);
}
function body(head) {
  const at = APP.indexOf(head);
  assert.ok(at >= 0, "app.js no longer has " + head);
  return APP.slice(at, APP.indexOf("\n}", at) + 2);
}
function sectionsBlock() {
  const from = APP.indexOf("/* ---------- SECTIONS (1.2.18");
  const to = APP.indexOf("/* ---------- NAV ---------- */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the SECTIONS block before NAV");
  return APP.slice(from, to);
}

/* The table, read out of the block with reveal and the rest stood in. */
function table() {
  const context = { Native: { available: false }, $: () => null, $$: () => [], T: id => id, rpc: () => {},
                    reveal: () => {}, revealReduced: () => false, rescanScrollCues: () => {}, requestAnimationFrame: () => {},
                    skaldRefresh: () => {}, updateToastLift: () => {}, refreshCfgList: () => {}, refreshHerald: () => {},
                    goPage: () => {}, currentPage: "hearth", document: {}, setTimeout: () => 0, clearTimeout: () => {} };
  vm.createContext(context);
  vm.runInContext(sectionsBlock() + "\nthis.SECTIONS=SECTIONS;this.sectIsOpen=sectIsOpen;this.sectSet=sectSet;this.sectOn=sectOn;this.sectApplyPrefs=sectApplyPrefs;this.UI=UI_SECTIONS;", context, { filename: "app.js#sections" });
  return context;
}

const RAIL = ["hearth", "vikings", "atlas", "mods", "world"];
const SECTS = { skald: "atlas", saga: "hearth", runes: "mods", herald: "world" };

test("1. the rail: five halls in the owner's order, the other four as sections under the halls the spec names", () => {
  const rail = [...HTML.matchAll(/<div class="navitem[^"]*" data-page="([a-z]+)">/g)].map(m => m[1]);
  assert.deepStrictEqual(rail, RAIL, "the rail reads " + rail.join(", "));
  const labels = [...HTML.matchAll(/data-i18n="side\.nav\.([a-z]+)\.label"/g)].map(m => m[1]);
  assert.deepStrictEqual(labels, RAIL, "a rail label is out of step with the rail");
  const halls = [...HTML.matchAll(/<section class="page[^"]*" id="page-([a-z]+)"/g)].map(m => m[1]);
  assert.deepStrictEqual(halls, RAIL, "the halls are not written in the rail's order: " + halls.join(", "));
  for (const [key, hall] of Object.entries(SECTS)) {
    const at = HTML.indexOf('<div class="sect" id="sect-' + key + '" data-sect="' + key + '">');
    assert.ok(at > 0, "the " + key + " section is gone");
    const page = HTML.lastIndexOf("<section class=", at);
    assert.ok(/id="page-([a-z]+)"/.exec(HTML.slice(page, page + 80))[1] === hall, key + " is not inside the " + hall + " hall");
    assert.ok(HTML.indexOf('id="sectBody-' + key + '"', at) > at, key + " has no body to roll");
    assert.ok(HTML.indexOf('<section class="page', at) === -1 || HTML.indexOf('</section>', at) < HTML.indexOf('<section class="page', at), key + " runs past its hall");
  }
  /* The Dashboard: bento, then the Log. The Map: the map, then Statistics (1.3.0; until then
     Statistics stood between the bento and the Log). Mods: list, then Configs. Settings: tabs,
     then Discord. */
  const order = (a, b) => assert.ok(HTML.indexOf(a) < HTML.indexOf(b), a + " does not come before " + b);
  order('<div class="bento">', 'id="sect-saga"'); order('<div class="atlas-grid">', 'id="sect-skald"');
  order('id="sectBody-mods"', 'id="sect-runes"'); order('id="sectBody-world"', 'id="sect-herald"');
  /* The old halls' ids are all still there, so every link and every test that keys on them lands. */
  for (const id of ["term", "termIn", "termSearch", "vellumBtn", "skResetBtn", "skModFeed", "cfgList", "cfgEditor", "cfgSaveBtn",
                    "heraldPublish", "heraldLookBtn", "heraldPanels", "heraldLookPanel", "tHeraldRealm"])
    assert.ok(HTML.includes('id="' + id + '"'), "#" + id + " left the page with its hall");
  for (const id of ["page-saga", "page-skald", "page-runes", "page-herald"])
    assert.ok(!HTML.includes('id="' + id + '"') && !APP.includes("#" + id), id + " is still a hall somewhere");
  for (const key of Object.keys(SECTS)) assert.ok(MAP.includes("`#sect-" + key + "`"), "docs/sidebar.md does not place " + key);
});

test("2. the table: eight sections with the spec's defaults, and the markup starts each body as its default says", () => {
  const t = table();
  const rows = t.SECTIONS.map(s => [s.key, s.page, !!s.open]);
  /* In the rail's order, each hall's own section first: the head stack reads the first row of a
     hall as its top. Statistics keeps its key, skald, on the Map, so a saved answer still holds. */
  same(rows, [["hearth", "hearth", true], ["saga", "hearth", false], ["atlas", "atlas", true], ["skald", "atlas", false],
                                ["mods", "mods", true], ["runes", "mods", false], ["world", "world", true], ["herald", "world", false]]);
  const atlas = t.SECTIONS.find(s => s.key === "atlas");
  assert.strictEqual(atlas.head, "#page-atlas>.pagehead", "the Map's own section is not headed by the hall's head");
  assert.strictEqual(atlas.body, "#page-atlas>.atlas-grid", "the Map's own section does not fold the map");
  assert.ok(HTML.includes('          <div class="atlas-grid">\n'), "the map does not start shown, as its default says");
  for (const s of t.SECTIONS) {
    assert.ok(HTML.includes(s.head.replace(/>/g, "").replace(/^#/, 'id="').split(".")[0] + '"'), s.key + "'s head names nothing in the markup");
    const bodyId = s.body.replace(/^.*#/, "").split(">")[0].replace(/\..*$/, "");
    if (s.body.startsWith("#sectBody-")) {
      const tag = new RegExp('<div class="sectbody" id="' + bodyId + '"( style="display:none")?>').exec(HTML);
      assert.ok(tag, s.key + "'s body is not a sectbody");
      assert.strictEqual(!!tag[1], !s.open, s.key + "'s body does not start the way its default says");
    }
    assert.strictEqual(t.sectIsOpen(s.key), !!s.open, s.key + " does not answer its default before anything is saved");
  }
});

test("3. the chevron: left of every heading, glowing, resting when idle, saying its state, worded on every repaint", () => {
  const wire = body("function sectWire(){");
  assert.ok(wire.includes('b.className="sectchev"') && wire.includes("head.insertBefore(b,head.firstChild)"), "the chevron is not the first thing in the heading");
  assert.ok(wire.includes('<svg viewBox="0 0 12 12"') && /M2\.5 4\.2 6 7\.7l3\.5-3\.5/.test(wire), "the chevron is not a V");
  assert.ok(/\.sectchev\{[^}]*animation:chevEmber 4\.8s ease-in-out infinite\}/.test(CSS), "the chevron does not glow like the other embers");
  assert.ok(/@keyframes chevEmber\{/.test(CSS) && /\.sectchev:hover,\.sectchev:focus-visible\{animation:none;color:var\(--ember-hi\)/.test(CSS), "the chevron does not answer a hover");
  assert.ok(/\.sectchev svg\{[^}]*transform:rotate\(-90deg\);transition:transform \.2s ease-out\}/.test(CSS), "a folded chevron does not point right");
  assert.ok(/\.sect\.open>\.pagehead \.sectchev svg,\.page\.open>\.pagehead \.sectchev svg\{transform:none\}/.test(CSS), "an open chevron is not a V");
  assert.ok(/body\.idle \.sectchev,/.test(CSS), "the chevron goes on burning behind another window");
  const set = body("function sectSet(key,open,opts){");
  assert.ok(set.includes('chev.setAttribute("aria-expanded",open?"true":"false")'), "the chevron does not say its state");
  assert.ok(set.includes('box.classList.toggle("open",open)'), "the section does not wear its state for the stylesheet");
  assert.ok(set.includes("reveal(body,open,"), "the section does not roll through reveal");
  assert.ok(EN["common.section.toggle.title"] && EN["common.section.toggle.title"].lore, "the chevron's sentence is not in the catalog");
  assert.ok(body("function sectWords(){").includes('T("common.section.toggle.title")'), "the chevron's words do not come from the catalog");
  assert.ok(/try\{sectWords\(\);\}catch\(_\)\{\}/.test(APP), "a language switch leaves the chevron's words behind");
});

test("4. remembered: every move writes UiSections whole, the saved answer is read with the Upkeep card's read, and the host side carries it", () => {
  const t = table();
  const writes = [];
  t.Native.available = true; t.rpc = (m, p) => writes.push([m, p]);
  t.sectApplyPrefs({ UiSections: { saga: true, hearth: false } });
  assert.strictEqual(writes.length, 0, "applying the saved answer wrote it back");
  assert.strictEqual(t.sectIsOpen("saga"), true, "a saved open was not honoured");
  assert.strictEqual(t.sectIsOpen("hearth"), false, "a saved fold was not honoured");
  assert.strictEqual(t.sectIsOpen("skald"), false, "a section the document does not name lost its default");
  t.sectSet("runes", true);
  same(writes, [["userprefs.save", { prefs: { UiSections: { hearth: false, saga: true, atlas: true, skald: false, mods: true, runes: true, world: true, herald: false } } }]],
    "a move does not write the whole answer");
  /* Before the saved answer has been read, a move is not written: the same rule as the Upkeep switches. */
  const fresh = table(); const early = [];
  fresh.Native.available = true; fresh.rpc = (m, p) => early.push(m);
  fresh.sectSet("saga", true);
  assert.deepStrictEqual(early, [], "a move was written over a document that has not been read");
  assert.ok(/try\{sectApplyPrefs\(up\);\}catch\(_\)\{\}/.test(body("async function initUpkeep(){")), "the saved answer is not read with the Upkeep card's read");
  const apply = body("function sectApplyPrefs(up){");
  assert.ok(apply.includes("{now:true,silent:true}"), "the saved answer rolls or writes itself back");
  /* The host side: the document key, the model, the DTO and the save. */
  assert.ok(/public Dictionary<string, bool> UiSections \{ get; set; \} = new\(\);/.test(PREFS), "UserPreferences has no UiSections");
  assert.ok(/UiSections = \(file\.UiSections \?\? new\(\)\)/.test(PREFS) && /UiSections = \(UiSections \?\? new\(\)\)/.test(PREFS), "UiSections does not round trip through the file");
  assert.ok(/\[JsonProperty\("uiSections"\)\] public Dictionary<string, bool> UiSections/.test(PREFS_FILE), "userprefs.json has no uiSections key");
  assert.ok(/\n\s*prefs\.UiSections,\n/.test(BRIDGE), "userprefs.get does not carry UiSections");
  assert.ok(/Apply\("UiSections", v =>/.test(BRIDGE) && /p\.Value\.Type == JTokenType\.Boolean/.test(BRIDGE), "userprefs.save does not take UiSections, or takes more than true and false");
});

test("5. every road in goes through goSection or unfolds with sectSet, and none walks to a hall that is a section now", () => {
  for (const key of ["saga", "skald", "runes", "herald"])
    assert.ok(!APP.includes('goPage("' + key + '")'), 'something still walks to the ' + key + ' hall with goPage');
  const roads = [
    ['openLog:()=>goSection("saga")', "the open-the-log toast action"],
    ['$("#openSagaChip")?.addEventListener("click",e=>{e.stopPropagation();goSection("saga");});', "the Recent log card's chip"],
    ['async function openModConfig(f){\n  goSection("runes");', "a mod row's configs entry"],
    ['if(sel.dataset.cmd==="discord_sharing"){goSection("herald");return;}', "the palette's Discord row"],
    ['if(sel.dataset.cmd==="server_analytics"){goSection("skald");return;}', "the palette's Statistics row"],
    ['bar.querySelector("#cbSaga").addEventListener("click",()=>goSection("saga"))', "the condition bar's log row"],
    ['bar.querySelector("#cbCrashLog").addEventListener("click",()=>goSection("saga"))', "the condition bar's crash row"],
    ['bar.querySelector("#cbPluginLog").addEventListener("click",()=>goSection("saga"))', "the condition bar's plugin row"],
    ['bar.querySelector("#cbUnsavedSaga").addEventListener("click",()=>goSection("saga"))', "the condition bar's unsaved row"],
    ['bar.querySelector("#cbMods").addEventListener("click",()=>goSection("mods"))', "the condition bar's mods row"],
    ['goSection("world"); // land on Settings', "a new realm landing on its ports and paths"],
    ['await switchServer(r.ProfileName);goSection("world");', "a duplicated realm landing on Settings"],
    ['function openAppTab(focusId){\n  goPage("world"); sectSet("world",true);', "openAppTab"],
    ['function openUpkeepCard(){\n  goPage("hearth"); sectSet("hearth",true);', "openUpkeepCard"],
    ['function openConnectionCard(){\n  goPage("hearth"); sectSet("hearth",true);', "openConnectionCard"],
  ];
  for (const [road, name] of roads) assert.ok(APP.includes(road), name + " no longer lands on its section");
  assert.strictEqual((APP.match(/goPage\("mods"\); sectSet\("mods",true\);/g) || []).length, 2, "the two BepInEx landings do not unfold the Mods list");
  const go = body("function goSection(key,opts){");
  /* 1.3.0: the unfold asks for the landing, which sectSet runs after the roll (rule 7). */
  assert.ok(go.includes("goPage(s.page);") && go.includes("sectSet(key,true,{land:true});"), "goSection does not open the hall and unfold the section");
  assert.ok(go.includes('if(!s){goPage(key);return;}'), "a hall with no section of that name is not a plain walk");
});

test("6. the checks that read the old halls read sectOn, and the walk-in work runs on the way open", () => {
  /* The one mention left is the comment on sectOn that says what it replaced. */
  const code = APP.split("\n").filter(l => !/^\s*(\/\*|\*|\/\/)/.test(l) && !/to read currentPage==="saga"/.test(l)).join("\n");
  for (const key of ["saga", "skald", "runes", "herald"])
    assert.ok(!code.includes('currentPage==="' + key + '"') && !code.includes('name==="' + key + '"'), "something still reads the " + key + " hall by name");
  const t = table();
  t.currentPage = "hearth";
  assert.strictEqual(t.sectOn("saga"), false); t.sectSet("saga", true, { silent: true }); assert.strictEqual(t.sectOn("saga"), true);
  t.currentPage = "mods"; assert.strictEqual(t.sectOn("saga"), false, "a section counts as on screen with its hall away");
  const go = body("function goPage(name){");
  /* Statistics is on the Map since 1.3.0, so the walk that reads it again is the walk into the Map. */
  assert.ok(go.includes('if(name==="atlas"&&sectIsOpen("skald"))') && !go.includes('if(name==="hearth"&&sectIsOpen("skald"))')
    && go.includes('if(name==="mods"&&sectIsOpen("runes")) refreshCfgList(false);')
    && go.includes('if(name==="world"&&sectIsOpen("herald")) refreshHerald();'), "the walk into a hall does not refresh the open sections");
  const block = sectionsBlock();
  assert.ok(block.includes('enter:()=>{try{skaldRefresh();}catch(_){}}') && block.includes("enter:()=>{if(Native.available) refreshCfgList(false);}")
    && block.includes("enter:()=>{if(Native.available) refreshHerald();}"), "a section does not do on the way open what its hall did on the walk in");
  const set = body("function sectSet(key,open,opts){");
  assert.ok(set.includes('if(open&&!was&&typeof s.enter==="function") s.enter();'), "the way-open work runs on a fold, or on a repeat");
});

test("7. the glow: 0.5 s in, three pulses over 3 s, 0.5 s out, steady under reduced motion, and the landing brings the section into view after its roll", () => {
  assert.ok(/\.sect-glow\{animation:sectGlow 4s ease-out\}/.test(CSS), "the landing light is not 4 s");
  const frames = /@keyframes sectGlow\{([^}]*\}[^}]*\}[^}]*\}[^}]*\})/.exec(CSS);
  assert.ok(frames, "no sectGlow keyframes");
  assert.ok(/12\.5%,37\.5%,62\.5%,87\.5%\{/.test(frames[1]) && /25%,50%,75%\{/.test(frames[1]), "the glow does not pulse three times between its fade in and its fade out");
  assert.ok(/0%\{box-shadow:0 0 0 0 rgba\(255,122,26,0\)/.test(frames[1]) && /100%\{box-shadow:0 0 0 0 rgba\(255,122,26,0\)/.test(frames[1]), "the glow does not fade in from, or out to, nothing");
  const reduced = CSS.slice(CSS.indexOf("@media (prefers-reduced-motion:reduce){\n  .sectchev{animation:none}"));
  assert.ok(reduced.includes(".sect-glow{animation:sectGlowSteady 4s ease-out}"), "the glow still pulses under reduced motion");
  assert.ok(/@keyframes sectGlowSteady\{[^}]*\}[^}]*12\.5%,87\.5%\{/.test(CSS), "the steady glow is not steady");
  const go = body("function goSection(key,opts){");
  /* Until 1.3.0 goSection scrolled the heading into view with scrollIntoView in the next frame,
     while the body was still 0 high: the target was clamped to the folded hall's end and a
     section opened from the top landed with its body under the fold (map_sections.md 6). The
     new truth: goSection and the chevron ask sectSet for the landing, sectSet runs it from the
     roll's {done}, and the rule is sectLandTop's. */
  assert.ok(go.includes("sectSet(key,true,{land:true});") && !go.includes("scrollIntoView"), "goSection does not ask for the landing, or scrolls on its own before the roll is over");
  assert.ok(body("function sectWire(){").includes("b.addEventListener(\"click\",()=>sectSet(s.key,!sectIsOpen(s.key),{land:true}));"), "a chevron click does not bring the section it opens into view");
  const land = body("function sectLand(key){");
  assert.ok(land.includes("pageEl.scrollTo({top,behavior:revealReduced()?\"auto\":\"smooth\"})") && !land.includes("scrollIntoView"), "the landing is not one smooth scroll of the hall to the computed place");
  {
    const t = table(), rolls = [], landed = [];
    t.reveal = (el, open, opts) => { rolls.push(opts); };
    t.sectLand = key => landed.push(key);
    t.sectStackRead = () => {};
    t.sectSet("saga", true, { land: true, silent: true });
    assert.deepStrictEqual(landed, [], "the section was brought into view before it had rolled open");
    rolls[0].done();
    assert.deepStrictEqual(landed, ["saga"], "the section was not brought into view once it had rolled open");
    t.sectSet("saga", false, { land: true, silent: true }); rolls[1].done();
    assert.deepStrictEqual(landed, ["saga"], "a fold scrolled the hall");
    t.sectSet("runes", true, { silent: true }); rolls[2].done();
    assert.deepStrictEqual(landed, ["saga"], "an unfold that did not ask (openAppTab and the like keep their own scroll) scrolled the hall");
    /* The rule, in numbers: tops are the heads' places with nothing stuck, hs their heights. */
    const top = g => t.sectLandTop(g);
    assert.strictEqual(top({ tops: [0, 600, 900], hs: [97, 53, 53], i: 1, view: 631, full: 1400 }), 322,
      "a section that fits does not end where the heads stuck at the foot begin");
    assert.strictEqual(top({ tops: [0, 600, 1300], hs: [97, 53, 53], i: 1, view: 631, full: 1400 }), 503,
      "a section taller than the room does not land with its head right under the heads above");
    assert.strictEqual(top({ tops: [0, 700], hs: [62, 52], i: 1, view: 631, full: 1100 }), 469,
      "the last section, fitting, does not land at the end of the hall");
    assert.strictEqual(top({ tops: [0, 700], hs: [62, 52], i: 1, view: 631, full: 1500 }), 638,
      "the last section, taller than the room, does not land under the hall's own head");
    assert.strictEqual(top({ tops: [0, 900], hs: [62, 52], i: 0, view: 631, full: 1300 }), 0,
      "the hall's own first section, taller than the room, does not land at the top of the hall");
    assert.strictEqual(top({ tops: [0, 300], hs: [62, 52], i: 0, view: 631, full: 700 }), 0,
      "a landing goes above the top of the hall");
    assert.strictEqual(top({ tops: [0, 1000, 1100], hs: [97, 53, 53], i: 2, view: 631, full: 1200 }), 569,
      "the last of three sections, fitting, does not land at the end of the hall");
  }
  assert.ok(go.includes('body.classList.add("sect-glow")') && go.includes("setTimeout(()=>body.classList.remove(\"sect-glow\"),4200)"), "the landing does not light the section, or leaves it lit");
  assert.ok(go.includes("void body.offsetWidth;"), "a second landing on a lit section does not light it again");
});

test("8. the head stack: every section head sticks at both ends from measured heights, the scroll padding is the stack, and the Settings tabs stay out of the fold", () => {
  assert.ok(/\.sect\{display:contents\}/.test(CSS), "the section wrapper still draws a box, so its head cannot stick past its own section");
  assert.ok(/\.sect>\.pagehead\.secthead\{position:sticky;z-index:4;\s*top:calc\(var\(--stick-top,0px\) - 20px\);bottom:calc\(var\(--stick-bottom,0px\) - 20px\);/.test(CSS),
    "a section head does not stick at the top under the heads above it and at the foot above the heads below it");
  assert.ok(/\.sect>\.sectbody\{flex:none\}/.test(CSS), "a rolling section body can be squeezed by the Dashboard's column");
  assert.ok(!/scroll-padding-top:\d+px/.test(CSS), "the scroll padding is a number written down again");
  assert.ok(/\.page:has\(>\.pagehead\.floating\)\{scroll-padding-top:var\(--stack-top,0px\);scroll-padding-bottom:var\(--stack-bottom,0px\)\}/.test(CSS),
    "the scroll padding is not the measured stack at the top and the foot");
  assert.ok(/\.pages\.heads-below::after\{opacity:0\}/.test(CSS) && CSS.indexOf(".pages.heads-below::after") > CSS.indexOf(".pages.cue-down::after{opacity:1}"),
    "the foot fade darkens the heads stuck at the foot, or pops rather than easing");
  /* The heights: measured, and written as the heads above and below each one; and, since 1.3.0,
     the whole stack with every head's margins on the hall as --stack-all (the Map sizes its map
     with it), rounded up. The margins are the stylesheet's: the hall's own head reaches 20px up
     over the hall's padding and keeps 4px under it, a section head keeps 14px over and 4px under. */
  const t = table(), props = {};
  const fake = (name, h, mt, mb) => ({ getBoundingClientRect: () => ({ height: h }), margins: { marginTop: mt + "px", marginBottom: mb + "px" },
                                       style: { setProperty: (k, v) => { (props[name] = props[name] || {})[k] = v; } } });
  const els = { "#page-hearth>.pagehead": fake("hearth", 97, -20, 4), "#sect-saga>.pagehead": fake("saga", 60, 14, 4), "#page-hearth": fake("page-hearth", 0, 0, 0),
                "#page-atlas>.pagehead": fake("atlas", 61.5, -20, 4), "#sect-skald>.pagehead": fake("skald", 52.5, 14, 4), "#page-atlas": fake("page-atlas", 0, 0, 0) };
  t.$ = sel => els[sel] || null; t.sectStackRead = () => {}; t.getComputedStyle = el => el.margins;
  t.sectStackSize("hearth"); t.sectStackSize("atlas");
  same(props, { saga: { "--stick-top": "97px", "--stick-bottom": "0px" }, "page-hearth": { "--stack-all": "159px" },
                skald: { "--stick-top": "61.5px", "--stick-bottom": "0px" }, "page-atlas": { "--stack-all": "116px" } },
    "the heads are not told the heights above and below them, or the hall the whole stack with its margins");
  const wire = body("function sectStackWire(){");
  assert.ok(wire.includes("new ResizeObserver(") && wire.includes('addEventListener("scroll"'), "the stack is not measured again when a head, a body or the pane changes size, or read again on a scroll");
  assert.ok(APP.includes("sectWire();\nsectStackWire();"), "the stack is never wired");
  assert.ok(body("function goPage(name){").includes("try{sectStackRead(name);}catch(_){}"), "the walk into a hall leaves the pane with the last hall's stack");
  const read = body("function sectStackRead(page){");
  assert.ok(read.includes('pageEl.style.setProperty("--stack-top"') && read.includes('pageEl.style.setProperty("--stack-bottom"'), "the scroll padding is not written from the stack as it stands");
  /* The Settings tab strip: under the head, outside the body that folds, and it unfolds it. */
  const hall = HTML.indexOf('id="page-world"'), strip = HTML.indexOf('<div class="worldtabs" role="tablist">'), fold = HTML.indexOf('<div class="sectbody" id="sectBody-world">');
  assert.ok(hall > 0 && hall < strip && strip < fold, "the Settings tab strip is inside the body that folds, so folding the panels takes the tabs away");
  assert.ok(APP.includes('$$(".wtab").forEach(b=>b.addEventListener("click",()=>{if(!sectIsOpen("world")) sectSet("world",true,{land:true});}));'),
    "a tab pressed with the panels folded leaves them folded");
  assert.ok(APP.indexOf('$$(".wtab").forEach(b=>b.addEventListener("click",()=>worldTab(') < APP.indexOf('if(!sectIsOpen("world")) sectSet("world",true,{land:true});'),
    "the unfold is wired above the tab's own handler");
});

test("9. the Map hall: the map is a section with Statistics under it, keeps its size and zoom through every fold, and Statistics follows the realm", () => {
  /* The markup: the hall's head floats like the other halls' first heads, and Statistics is the
     last thing in the hall, after the map. */
  const hall = HTML.slice(HTML.indexOf('<section class="page" id="page-atlas">'), HTML.indexOf("<!-- ============ PAGE: MODS"));
  assert.ok(hall.includes('<div class="pagehead floating">'), "the Map's head does not stay on screen while Statistics is read");
  assert.ok(hall.indexOf('<div class="atlas-grid">') < hall.indexOf('<div class="sect" id="sect-skald" data-sect="skald">')
    && /<\/div>\s*<\/section>\s*$/.test(hall), "Statistics is not under the map, at the end of the Map hall");
  /* The size: the pane less the measured stack, on the ROW, and the gutter kept. A height on the
     grid itself would roll the map smaller under a fold, and atlasResize takes that for a zoom. */
  const page = /#page-atlas\{([^}]*)\}/.exec(CSS), grid = /\.atlas-grid\{([^}]*)\}/.exec(CSS);
  assert.ok(page && /container-type:size/.test(page[1]) && /scrollbar-gutter:stable/.test(page[1]), "the Map hall is not the size the map is measured from, or its scrollbar narrows it when Statistics opens");
  assert.ok(grid && /flex:none/.test(grid[1]) && /grid-template-rows:max\(180px,calc\(100cqh - var\(--stack-all,0px\)\)\)/.test(grid[1]),
    "the map's row is not the pane less the measured head stack");
  assert.ok(!/(^|;)\s*(height|min-height|flex):/.test(grid[1].replace(/flex:none/, "")), "the grid has a height of its own, which a fold would roll and the map would follow");
  /* The chart: atlasResize and atlasFit over a stand-in map. */
  const wrap = { clientWidth: 0, clientHeight: 0 }, canvas = { width: 0, height: 0 };
  const c = { Math, Number, window: { devicePixelRatio: 1 }, wrap, canvas, drawn: 0,
              $: sel => (sel === "#atlasWrap" ? wrap : sel === "#atlasCanvas" ? canvas : null),
              ATLAS: { mapExtent: 10500, viewBase: 0, fitOwed: false, cam: { cx: 0, cz: 0, ppm: 0 } } };
  c.atlasDraw = () => { c.drawn++; };
  vm.createContext(c);
  for (const head of ["function atlasWrapSize(){", "function atlasFitPpm(){", "function atlasFit(){", "function atlasResize(){"])
    vm.runInContext(body(head), c, { filename: "app.js#" + head });
  const resize = (w, h) => { wrap.clientWidth = w; wrap.clientHeight = h; vm.runInContext("atlasResize()", c); };
  resize(600, 216);
  vm.runInContext("atlasFit()", c);
  const fit = 216 / 21000;
  assert.strictEqual(c.ATLAS.cam.ppm, fit, "the fit is not the shorter side across the world");
  c.ATLAS.cam.ppm = fit * 1.5;
  resize(0, 0); resize(600, 216);
  assert.strictEqual(c.ATLAS.cam.ppm, fit * 1.5, "a fold and an unfold of the map changed its zoom");
  resize(600, 300);
  assert.ok(Math.abs(c.ATLAS.cam.ppm - fit * 1.5 * 300 / 216) < 1e-15, "the window growing no longer grows the chart with it");
  resize(0, 0);
  vm.runInContext("atlasFit()", c);
  assert.strictEqual(c.ATLAS.cam.ppm, 0.03, "a fit with the map folded did not keep a sane stand-in");
  resize(600, 216);
  assert.strictEqual(c.ATLAS.cam.ppm, fit, "a fit asked with the map folded was lost: the map came back at the stand-in");
  resize(600, 216);
  assert.strictEqual(c.ATLAS.cam.ppm, fit, "the owed fit was paid twice");
  /* A realm switch reads Statistics again, after the map is reset for the new realm. */
  const sw = body("async function switchServer(name,opts){");
  const reset = sw.indexOf("try{atlasReset();}catch{}"), again = sw.indexOf("try{skaldRefresh();}catch(_){}");
  assert.ok(reset > 0 && again > reset, "a realm switch leaves Statistics on the last realm's numbers");
  assert.ok(sw.indexOf('rpc("profiles.get",{name})') < again, "Statistics is read before the host has made the new realm the active one");
  /* A refresh asked while one is out is not dropped, and the answer it replaces is not drawn. */
  const at = APP.indexOf("let skaldBusy=false"), end = APP.indexOf("\nfunction renderSkald(){", at);
  assert.ok(at > 0 && end > at, "skaldRefresh is no longer where this rule reads it");
  const k = { answers: [], drawn: [], fetches: 0, SKALD: null };
  k.skaldFetch = () => new Promise(r => { k.fetches++; k.answers.push(r); });
  k.renderSkald = () => k.drawn.push(k.SKALD);
  vm.createContext(k);
  vm.runInContext(APP.slice(at, end).replace("let skaldBusy=false, skaldAgain=false;", "var skaldBusy=false, skaldAgain=false;")
    + "\nthis.refresh=skaldRefresh;this.getSkald=()=>SKALD;", k, { filename: "app.js#skaldRefresh" });
  return (async () => {
    const first = k.refresh();
    k.refresh();                         /* the realm switch, while the event's read is out */
    k.answers[0]("left realm");
    await new Promise(r => setImmediate(r));
    assert.strictEqual(k.fetches, 2, "a refresh asked while one was out was dropped");
    k.answers[1]("this realm");
    await first;
    same(k.drawn, ["this realm"], "the read that answered for the realm just left was drawn");
  })();
});

Promise.all(pending).then(() => {
  console.log(failures.length === 0
    ? "sidebar sections selftest: all " + passed + " rules pass"
    : "sidebar sections selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAIL");
  process.exit(failures.length === 0 ? 0 : 1);
});
