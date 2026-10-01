/* The Atlas's two findings from the real-app walk of 1.2.6: the place names that disappeared at
 * a bigger text size, and the ones that were never names at all.
 *
 * WHY THIS EXISTS.
 *
 *   1. CHOOSING A BIGGER TEXT SIZE REMOVED EVERY PLACE NAME FROM THE CHART. Text size is the
 *      page's zoom: it shrinks the CSS viewport and raises devicePixelRatio by the same factor,
 *      so the atlas wrap shrinks with it and the auto-fit lands at a higher number of metres per
 *      CSS pixel. The label cull was written against that number, so the owner's world drew every
 *      portal and altar name at Normal (22 m/px, ppm 0.04545, one percent above the threshold) and
 *      not one of them at Large (29 m/px, ppm 0.0345), on the same window, while the release note
 *      said text size zooms the map's labels with everything else. The decision belongs in DEVICE
 *      pixels, which is the one unit that does not move when the zoom does.
 *   2. THE WAYPOINTS LIST PRINTED VALHEIM'S OWN LOCALIZATION KEYS. A shared map table carries
 *      "$enemy_eikthyr" for the stone at Eikthyr's altar and "$hud_pin_hildir1" for the first of
 *      Hildir's errands. Nothing resolved them, so eight rows read as keys while the altars group
 *      two rows above already said "Eikthyr" and "Moder".
 *
 * Every painter and every resolver here is the real one, lifted out of app.js and run. Prints one
 * line per rule and exits non zero on the first failure.
 *
 *     node scripts/ui/atlas_names_selftest.js [app.js] [en.json] [BlendWindow.Bridge.cs]
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

const SOURCE = fs.readFileSync(APP, "utf8").replace(/\r\n/g, "\n");
const KEYS = JSON.parse(fs.readFileSync(CATALOG, "utf8")).keys;
const HOST = fs.readFileSync(BRIDGE, "utf8").replace(/\r\n/g, "\n");

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

/** One whole function, from its opening line to the brace that closes it at the left margin. */
function fn(opening) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf("\n}", from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer closes at the left margin");
  return SOURCE.slice(from, to) + "\n}";
}

/** One statement, from its opening to the line that ends it at the left margin. */
function block(opening, closing) {
  const from = SOURCE.indexOf(opening);
  assert.ok(from >= 0, "app.js no longer holds " + JSON.stringify(opening));
  const to = SOURCE.indexOf(closing, from);
  assert.ok(to > from, JSON.stringify(opening) + " no longer ends with " + JSON.stringify(closing));
  return SOURCE.slice(from, to + closing.length);
}

/** The catalog, read the one way these entries need: a lore string with named slots. */
function words(id, params) {
  const entry = KEYS[id];
  if (!entry || typeof entry.lore !== "string") return id;
  return entry.lore.replace(/\{([A-Za-z_][A-Za-z0-9_]*)\}/g,
    (whole, name) => (params && Object.prototype.hasOwnProperty.call(params, name)
      ? String(params[name]) : whole));
}

/* ------------------------------------------------------------------ rule 1: the label cull */

/** The real fit arithmetic and the real density gate, over a fake wrap and a fake zoom. */
function chart() {
  const wrap = { clientWidth: 0, clientHeight: 0 };
  const context = {
    Number, Math, String, console,
    window: { devicePixelRatio: 1 },
    $: selector => (selector === "#atlasWrap" ? wrap : null),
    ATLAS: { mapExtent: 10500, cam: { cx: 0, cz: 0, ppm: 0 } },
    wrap,
  };
  vm.createContext(context);
  vm.runInContext(fn("function atlasWrapSize(){"), context, { filename: "app.js#wrapSize" });
  vm.runInContext(fn("function atlasFitPpm(){"), context, { filename: "app.js#fitPpm" });
  vm.runInContext(fn("function atlasPpmDev(ppm){"), context, { filename: "app.js#ppmDev" });
  vm.runInContext(block("const ATLAS_LABEL_PPM_DEV=", ";"), context, { filename: "app.js#threshold" });

  /* The one line atlasDraw makes the decision on, lifted rather than described. */
  const decide = block("const showLbl=", ";").replace(/^const /, "");
  context.decide = (ppm) => {
    context.c = { ppm };
    vm.runInContext("var " + decide + "\nthis.showLbl=showLbl;", context, { filename: "app.js#cull" });
    return context.showLbl;
  };

  /** One window, given in DEVICE pixels, read at one zoom. */
  context.atSize = (deviceW, deviceH, dpr, extent) => {
    context.window.devicePixelRatio = dpr;
    wrap.clientWidth = deviceW / dpr;
    wrap.clientHeight = deviceH / dpr;
    context.ATLAS.mapExtent = extent || 10500;
    const ppm = vm.runInContext("atlasFitPpm()", context);
    return { ppm, show: context.decide(ppm) };
  };
  return context;
}

test("the same world in the same window shows the same labels at every text size", () => {
  const h = chart();
  /* The three zooms the host side really sets: Normal, Large (x1.2) and Extra large (x1.45) on a
     144 dpi display, which is 1.5, 1.8 and 2.175, and the same three on a 96 dpi one. */
  for (const base of [1, 1.5]) {
    for (const [w, hgt] of [[1024, 680], [1426, 1215], [2138, 1822], [3840, 2160]]) {
      const read = [1, 1.2, 1.45].map(zoom => h.atSize(w, hgt, base * zoom, 10500));
      const first = read[0].show;
      for (let i = 1; i < read.length; i++) {
        assert.strictEqual(read[i].show, first,
          "a " + w + "x" + hgt + " device window at dpr " + (base * [1, 1.2, 1.45][i])
          + " draws labels " + read[i].show + " where the same window at dpr " + base
          + " draws them " + first + ": choosing a bigger text size changed which names are on"
          + " the chart");
      }
      /* And the metres-per-CSS-pixel really did move, or the rule above proves nothing. */
      assert.notStrictEqual(read[0].ppm, read[2].ppm,
        "the fit did not change with the zoom at all, so this window cannot tell the two rules apart");
    }
  }
});

test("the owner's own world, at Normal and at Large, draws its names at both", () => {
  const h = chart();
  /* The walk's measurements, not a model of them: 22 m/px at Normal with devicePixelRatio 1.5,
     29 m/px at Large with 1.8. Under the rule this replaced the first shows names (0.04545 is one
     percent over 0.045) and the second shows none (0.0345 is not), which is the finding. */
  assert.strictEqual(h.decide(1 / 22), true, "Normal lost the names it had");
  assert.strictEqual(h.window.devicePixelRatio, 1, "the fixture moved the zoom under this row");

  h.window.devicePixelRatio = 1.5;
  assert.strictEqual(h.decide(1 / 22), true, "Normal at 22 m/px draws no names");
  h.window.devicePixelRatio = 1.8;
  assert.strictEqual(h.decide(1 / 29), true,
    "Large at 29 m/px still draws no names, which is the whole finding");
  h.window.devicePixelRatio = 2.175;
  assert.strictEqual(h.decide(1 / 36), true, "Extra large draws no names");

  // The old rule, written out here so the row above cannot pass for the wrong reason: it is the
  // comparison this release replaced, and it disagrees.
  assert.strictEqual(1 / 29 > 0.045, false,
    "the CSS-pixel rule this replaced would have shown the Large names too, so this fixture is"
    + " not reproducing the defect");
});

test("a chart zoomed far enough out still culls, so the gate is a gate", () => {
  const h = chart();
  h.window.devicePixelRatio = 1.5;
  assert.strictEqual(h.decide(1 / 200), false,
    "200 metres to a CSS pixel draws place names, which is ink rather than information");
  assert.strictEqual(h.decide(1 / 10), true, "ten metres to a CSS pixel draws no place names");
});

test("the threshold is named once and the cull reads it through the device helper", () => {
  const cull = block("const showLbl=", ";");
  assert.ok(cull.indexOf("atlasPpmDev(") > 0,
    "the cull is back to comparing metres per CSS pixel: " + cull);
  assert.ok(cull.indexOf("ATLAS_LABEL_PPM_DEV") > 0, "the threshold is a literal again: " + cull);
  assert.ok(/const ATLAS_LABEL_PPM_DEV=0\.045;/.test(SOURCE), "the threshold is no longer 0.045");
  // One gate, not two copies of it.
  assert.strictEqual((SOURCE.match(/c\.ppm>0\.045/g) || []).length, 0,
    "a copy of the old CSS-pixel cull is still in the file");
});

/* ------------------------------------------------- rule 2: the names a map table writes */

/** The real resolver and the real table. */
function pins() {
  const context = { Object, String, console, T: words };
  vm.createContext(context);
  vm.runInContext(block("const ATLAS_PIN_TOKENS=[", "];"), context, { filename: "app.js#pinTable" });
  vm.runInContext(block("const ATLAS_PIN_BY_TOKEN=(()=>{", "})();"), context,
    { filename: "app.js#pinIndex" });
  vm.runInContext(fn("function atlasPinName(raw){"), context, { filename: "app.js#pinName" });
  context.name = raw => vm.runInContext("atlasPinName(" + JSON.stringify(raw) + ")", context);
  return context;
}

test("every token the game writes into a map-table pin resolves to its own name", () => {
  const h = pins();
  const expected = {
    "$enemy_eikthyr": "Eikthyr",
    "$enemy_gdking": "The Elder",
    "$enemy_bonemass": "Bonemass",
    "$enemy_dragon": "Moder",
    "$enemy_goblinking": "Yagluth",
    "$enemy_seekerqueen": "The Queen",
    "$enemy_fader": "Fader",
    "$hud_pin_hildir1": "Smouldering Tomb",
    "$hud_pin_hildir2": "Howling Cavern",
    "$hud_pin_hildir3": "Sealed Tower",
  };
  for (const [token, said] of Object.entries(expected)) {
    assert.strictEqual(h.name(token), said, token + " renders as " + JSON.stringify(h.name(token)));
  }
  // Every row of the table is in the catalog, so a translator can reach all ten.
  for (const row of vm.runInContext("ATLAS_PIN_TOKENS", h)) {
    assert.ok(KEYS[row.nameId], row.nameId + " is not in the English catalog");
  }
});

/** The altars-and-traders resolver and its table, with the catalog behind it. */
function altars() {
  const context = { Object, String, console, T: words };
  vm.createContext(context);
  vm.runInContext(block("const ATLAS_POI_NAMES=[", "];"), context, { filename: "app.js#poiTable" });
  vm.runInContext(block("const ATLAS_POI_BY_PREFAB=(()=>{", "})();"), context,
    { filename: "app.js#poiIndex" });
  vm.runInContext(fn("function atlasPoiName(poi){"), context, { filename: "app.js#poiName" });
  context.name = poi => vm.runInContext("atlasPoiName(" + JSON.stringify(poi) + ")", context);
  return context;
}

/** Every prefab the host side labels, with the English it sends for it. */
function hostLabels() {
  const table = HOST.slice(HOST.indexOf("AtlasPoiLabels = new(StringComparer.OrdinalIgnoreCase)"));
  const rows = table.slice(0, table.indexOf("};"));
  const found = [];
  const re = /\["([^"]+)"\]\s*=\s*"([^"]+)"/g;
  let hit;
  while ((hit = re.exec(rows))) found.push({ prefab: hit[1], english: hit[2] });
  return found;
}

test("the boss names are the SAME catalog entry the pins group reads", () => {
  const h = pins();
  const a = altars();
  /* THE INVERSION THIS FIXED. The pins went through the catalog and the altars two rows above
     them were the host side's English, drawn verbatim: in a translated window the pins read in
     the host's language and the altars did not, which is the same list disagreeing with itself
     about the same place. Both groups resolve through one id now, and these are the seven places
     the two groups can name the same boss. */
  const pairs = [["Eikthyrnir", "$enemy_eikthyr"], ["GDKing", "$enemy_gdking"],
    ["Bonemass", "$enemy_bonemass"], ["Dragonqueen", "$enemy_dragon"],
    ["GoblinKing", "$enemy_goblinking"], ["Mistlands_DvergrBossEntrance1", "$enemy_seekerqueen"],
    ["FaderLocation", "$enemy_fader"]];
  const byPrefab = {};
  for (const row of vm.runInContext("ATLAS_POI_NAMES", a)) byPrefab[row.prefab] = row.nameId;
  const byToken = vm.runInContext("ATLAS_PIN_BY_TOKEN", h);

  for (const [prefab, token] of pairs) {
    assert.strictEqual(byPrefab[prefab], byToken[token.slice(1)],
      "the altar at " + prefab + " reads " + JSON.stringify(byPrefab[prefab]) + " where its pin"
      + " reads " + JSON.stringify(byToken[token.slice(1)]) + ": two entries for one place is two"
      + " translations of one place");
    assert.strictEqual(a.name({ prefab, label: "WRONG" }), h.name(token),
      "the altar at " + prefab + " says " + JSON.stringify(a.name({ prefab, label: "WRONG" }))
      + " where its pin says " + JSON.stringify(h.name(token)));
  }
});

test("every prefab the host side labels resolves through the catalog, and nothing is orphaned", () => {
  const a = altars();
  const labels = hostLabels();
  assert.ok(labels.length >= 11, "the host side labels only " + labels.length + " locations");

  const byPrefab = {};
  for (const row of vm.runInContext("ATLAS_POI_NAMES", a)) byPrefab[row.prefab] = row.nameId;

  for (const { prefab, english } of labels) {
    const id = byPrefab[prefab];
    assert.ok(id, prefab + " is labelled by the host side and has no catalog id, so that row stays"
      + " in English in a translated window");
    assert.ok(KEYS[id], id + " is not in the English catalog");
    /* And the catalog's English is the host side's English, so the fallback and the entry cannot
       say two different things about one place. */
    assert.strictEqual(words(id), english,
      prefab + " reads " + JSON.stringify(words(id)) + " in the catalog and "
      + JSON.stringify(english) + " on the wire");
    assert.strictEqual(a.name({ prefab, label: english }), english);
  }

  // Nothing in the table names a place the host side no longer sends.
  for (const row of vm.runInContext("ATLAS_POI_NAMES", a)) {
    assert.ok(labels.some(l => l.prefab === row.prefab),
      row.prefab + " is in the page's table and nothing sends it any more");
  }
});

test("a place this page has never heard of keeps the English the host sent", () => {
  const a = altars();
  /* A location Valheim adds in a later release: the host side finds it and sends its English, and
     the page has no entry for it. The English is the floor, never a key and never an empty row. */
  assert.strictEqual(a.name({ prefab: "NewBossLocation2027", label: "Somebody New" }), "Somebody New");
  assert.strictEqual(a.name({ prefab: "", label: "Haldor" }), "Haldor");
  assert.strictEqual(a.name({ prefab: "StartTemple" }), words("atlas.poi.start_temple"));
  assert.strictEqual(a.name({ prefab: "starttemple", label: "x" }), words("atlas.poi.start_temple"),
    "the prefab lookup is case sensitive, and the save file's spelling is not guaranteed");
  assert.strictEqual(a.name(null), "");
});

test("a token nobody has heard of is still readable, and a host's own pin is untouched", () => {
  const h = pins();
  assert.strictEqual(h.name("$enemy_newboss"), "Newboss",
    "a location the game adds in a later release still prints as a key");
  assert.strictEqual(h.name("$hud_pin_somewhere_else"), "Somewhere else");
  assert.strictEqual(h.name("$ENEMY_EIKTHYR"), "Eikthyr", "the lookup is case sensitive");
  assert.strictEqual(h.name("Base camp"), "Base camp", "a name the host typed was rewritten");
  assert.strictEqual(h.name("  Base camp  "), "Base camp");
  assert.strictEqual(h.name(""), "");
  assert.strictEqual(h.name(null), "");
  assert.strictEqual(h.name(undefined), "");
  assert.strictEqual(h.name("$"), "$", "a bare dollar sign has nothing to make readable");
});

/* ------------------------------------------- the Waypoints list, drawn rather than described */

/** The real side panel, over a fake hall. */
function side() {
  const made = {};
  const element = id => ({
    id, textContent: "", innerHTML: "", style: {}, title: "",
    classList: { add() {}, remove() {}, toggle() {}, contains: () => false },
  });
  const context = {
    Object, Array, String, Number, Math, JSON, console,
    T: words,
    esc: t => String(t),
    $: selector => (made[selector] = made[selector] || element(selector)),
    S: { players: [] },
    ATLAS: { world: "WalkWorld126", info: null },
    atlasAge: () => "40s ago",
    atlasNoDbText: () => "no world data",
    emptyState: o => "<empty>" + o.title + "</empty>",
    esWire() {},
    renderWeather() {},
    refreshScrollCues() {},
    made,
  };
  vm.createContext(context);
  vm.runInContext(block("const ATLAS_PIN_TOKENS=[", "];"), context, { filename: "app.js#pinTable" });
  vm.runInContext(block("const ATLAS_PIN_BY_TOKEN=(()=>{", "})();"), context,
    { filename: "app.js#pinIndex" });
  vm.runInContext(fn("function atlasPinName(raw){"), context, { filename: "app.js#pinName" });
  vm.runInContext(block("const ATLAS_POI_NAMES=[", "];"), context, { filename: "app.js#poiTable" });
  vm.runInContext(block("const ATLAS_POI_BY_PREFAB=(()=>{", "})();"), context,
    { filename: "app.js#poiIndex" });
  vm.runInContext(fn("function atlasPoiName(poi){"), context, { filename: "app.js#poiName" });
  vm.runInContext(fn("function renderAtlasSide(){"), context, { filename: "app.js#side" });
  return context;
}

test("the Waypoints list draws names rather than keys", () => {
  const h = side();
  h.ATLAS.info = {
    hasDb: true, hasSharedMap: false, day: 333, exploredPercent: 0, mapTables: 0,
    savedAgeSeconds: 40, chunksSkipped: 0, chunksTotal: 0, eventName: null,
    /* The host side's own shape: the prefab it found in the world's location list and the English
       it holds for it. The row reads the catalog through the prefab, so a deliberately wrong
       English here proves the list is not drawing what came off the wire. */
    pois: [{ prefab: "Eikthyrnir", label: "NOT THE CATALOG", x: 100, z: 200 },
      { prefab: "Vendor_BlackForest", label: "Haldor", x: -500, z: 600 }],
    portals: [{ tag: "home", x: 10, z: 20 }],
    pins: [
      { name: "$enemy_eikthyr", x: 110, z: 210 },
      { name: "$hud_pin_hildir2", x: -300, z: 400 },
      { name: "Silver up here", x: 1, z: 2 },
      { name: "", x: 3, z: 4 },
    ],
  };
  vm.runInContext("renderAtlasSide()", h);

  const drawn = h.made["#atlasWaypoints"].innerHTML;
  assert.ok(drawn.indexOf("$enemy_") < 0 && drawn.indexOf("$hud_pin") < 0,
    "the list still prints Valheim's own keys: " + drawn);
  assert.ok(drawn.indexOf("Eikthyr") > 0, "the boss pin lost its name");
  assert.ok(drawn.indexOf("Howling Cavern") > 0, "Hildir's cavern lost its name");
  assert.ok(drawn.indexOf("Silver up here") > 0, "a name the host typed was dropped");
  assert.ok(drawn.indexOf(words("atlas.waypoint.pin.unnamed")) > 0,
    "a pin with no name at all lost its (pin) placeholder");
  assert.ok(drawn.indexOf("NOT THE CATALOG") < 0,
    "the altars group is drawing the English off the wire rather than the catalog entry: " + drawn);

  // The rows the panel hands its click handler carry the same resolved names.
  const rows = h.made["#atlasWaypoints"]._rows;
  assert.strictEqual(rows.length, 7, "the list is " + rows.length + " rows rather than seven");
  assert.strictEqual(rows[0].n, words("atlas.pin.token.enemy_eikthyr"),
    "the altar row reads " + JSON.stringify(rows[0].n));
  assert.strictEqual(rows[1].n, words("atlas.poi.trader"));
  assert.strictEqual(rows[3].n, "Eikthyr");
  assert.strictEqual(rows[4].n, "Howling Cavern");
  /* The altar and the pin for one boss are the same words, which is the finding read off the
     drawn list rather than off the tables. */
  assert.strictEqual(rows[0].n, rows[3].n,
    "the altar and the pin for Eikthyr read as two different places in one list");
});

test("and the chart's own labels go through the same resolvers", () => {
  const draw = SOURCE.slice(SOURCE.indexOf("function atlasDraw(){"),
    SOURCE.indexOf("const z=$(\"#atlasZoomLbl\");"));
  assert.ok(draw.indexOf("atlasPinName(p.name)") > 0,
    "the canvas still draws p.name straight, so the chart and the list disagree about a pin");
  assert.strictEqual((draw.match(/ctx\.fillText\(p\.name/g) || []).length, 0,
    "a raw p.name is still painted onto the canvas");
  assert.ok(draw.indexOf("atlasPoiName(l)") > 0,
    "the canvas still draws the altar label off the wire, so the chart stays English in a"
    + " translated window while the list beside it is translated");
  assert.strictEqual((draw.match(/ctx\.fillText\(l\.label/g) || []).length, 0,
    "a raw l.label is still painted onto the canvas");
});

console.log("");
if (failures.length) {
  console.log("atlas names selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("atlas names selftest: " + passed + " passed");
