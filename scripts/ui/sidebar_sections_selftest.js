/* The simpler sidebar (1.2.18): five halls on the rail, the other four as folding sections,
 * remembered, and every old road still landing where it did.
 *
 * WHY THIS EXISTS. The owner's spec: the rail shows Dashboard, Players, Map, Mods, Settings
 * and nothing else; the Log and Statistics are sections of the Dashboard, Configs a section
 * under the Mods list, Discord a section at the foot of Settings; every section folds behind a
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
 *   2. The table: SECTIONS in app.js names the seven sections with the spec's defaults, and the
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
 *   7. The glow: 0.5 s in, three pulses over 3 s, 0.5 s out, steady under reduced motion; the
 *      landing scrolls the heading into view.
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
const PREFS = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Game", "UserPreferences.cs"), "utf8");
const PREFS_FILE = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Game", "UserPreferencesFile.cs"), "utf8");
const BRIDGE = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"), "utf8");
const MAP = fs.readFileSync(path.join(ROOT, "docs", "sidebar.md"), "utf8").replace(/\r\n/g, "\n");

let passed = 0;
const failures = [];
function test(name, fn) {
  try {
    fn();
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
const SECTS = { skald: "hearth", saga: "hearth", runes: "mods", herald: "world" };

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
  /* The Dashboard: bento, then Statistics, then the Log. Mods: list, then Configs. Settings: tabs, then Discord. */
  const order = (a, b) => assert.ok(HTML.indexOf(a) < HTML.indexOf(b), a + " does not come before " + b);
  order('<div class="bento">', 'id="sect-skald"'); order('id="sect-skald"', 'id="sect-saga"');
  order('id="sectBody-mods"', 'id="sect-runes"'); order('id="sectBody-world"', 'id="sect-herald"');
  /* The old halls' ids are all still there, so every link and every test that keys on them lands. */
  for (const id of ["term", "termIn", "termSearch", "vellumBtn", "skResetBtn", "skModFeed", "cfgList", "cfgEditor", "cfgSaveBtn",
                    "heraldPublish", "heraldLookBtn", "heraldPanels", "heraldLookPanel", "tHeraldRealm"])
    assert.ok(HTML.includes('id="' + id + '"'), "#" + id + " left the page with its hall");
  for (const id of ["page-saga", "page-skald", "page-runes", "page-herald"])
    assert.ok(!HTML.includes('id="' + id + '"') && !APP.includes("#" + id), id + " is still a hall somewhere");
  for (const key of Object.keys(SECTS)) assert.ok(MAP.includes("`#sect-" + key + "`"), "docs/sidebar.md does not place " + key);
});

test("2. the table: seven sections with the spec's defaults, and the markup starts each body as its default says", () => {
  const t = table();
  const rows = t.SECTIONS.map(s => [s.key, s.page, !!s.open]);
  same(rows, [["hearth", "hearth", true], ["skald", "hearth", false], ["saga", "hearth", false],
                                ["mods", "mods", true], ["runes", "mods", false], ["world", "world", true], ["herald", "world", false]]);
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
  same(writes, [["userprefs.save", { prefs: { UiSections: { hearth: false, skald: false, saga: true, mods: true, runes: true, world: true, herald: false } } }]],
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
  const go = body("function goSection(key){");
  assert.ok(go.includes("goPage(s.page);") && go.includes("sectSet(key,true);"), "goSection does not open the hall and unfold the section");
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
  assert.ok(go.includes('if(name==="hearth"&&sectIsOpen("skald"))') && go.includes('if(name==="mods"&&sectIsOpen("runes")) refreshCfgList(false);')
    && go.includes('if(name==="world"&&sectIsOpen("herald")) refreshHerald();'), "the walk into a hall does not refresh the open sections");
  const block = sectionsBlock();
  assert.ok(block.includes('enter:()=>{try{skaldRefresh();}catch(_){}}') && block.includes("enter:()=>{if(Native.available) refreshCfgList(false);}")
    && block.includes("enter:()=>{if(Native.available) refreshHerald();}"), "a section does not do on the way open what its hall did on the walk in");
  const set = body("function sectSet(key,open,opts){");
  assert.ok(set.includes('if(open&&!was&&typeof s.enter==="function") s.enter();'), "the way-open work runs on a fold, or on a repeat");
});

test("7. the glow: 0.5 s in, three pulses over 3 s, 0.5 s out, steady under reduced motion, and the landing scrolls the heading up", () => {
  assert.ok(/\.sect-glow\{animation:sectGlow 4s ease-out\}/.test(CSS), "the landing light is not 4 s");
  const frames = /@keyframes sectGlow\{([^}]*\}[^}]*\}[^}]*\}[^}]*\})/.exec(CSS);
  assert.ok(frames, "no sectGlow keyframes");
  assert.ok(/12\.5%,37\.5%,62\.5%,87\.5%\{/.test(frames[1]) && /25%,50%,75%\{/.test(frames[1]), "the glow does not pulse three times between its fade in and its fade out");
  assert.ok(/0%\{box-shadow:0 0 0 0 rgba\(255,122,26,0\)/.test(frames[1]) && /100%\{box-shadow:0 0 0 0 rgba\(255,122,26,0\)/.test(frames[1]), "the glow does not fade in from, or out to, nothing");
  const reduced = CSS.slice(CSS.indexOf("@media (prefers-reduced-motion:reduce){\n  .sectchev{animation:none}"));
  assert.ok(reduced.includes(".sect-glow{animation:sectGlowSteady 4s ease-out}"), "the glow still pulses under reduced motion");
  assert.ok(/@keyframes sectGlowSteady\{[^}]*\}[^}]*12\.5%,87\.5%\{/.test(CSS), "the steady glow is not steady");
  const go = body("function goSection(key){");
  assert.ok(go.includes('scrollIntoView({block:"start",behavior:revealReduced()?"auto":"smooth"})'), "the landing does not scroll the heading into view");
  assert.ok(go.includes('body.classList.add("sect-glow")') && go.includes("setTimeout(()=>body.classList.remove(\"sect-glow\"),4200)"), "the landing does not light the section, or leaves it lit");
  assert.ok(go.includes("void body.offsetWidth;"), "a second landing on a lit section does not light it again");
});

console.log(failures.length === 0
  ? "sidebar sections selftest: all " + passed + " rules pass"
  : "sidebar sections selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAIL");
process.exit(failures.length === 0 ? 0 : 1);
