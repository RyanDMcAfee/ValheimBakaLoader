#!/usr/bin/env node
/*
  The 1.2.7 page findings, measured on the real page in a real browser.

  WHY THIS EXISTS. Four of the walk's findings are invisible to any source gate, because what
  they are about is what a BROWSER makes of the page:

    * NO SWITCH HAD A NAME. Thirty-one of the window's thirty-five carried role=switch, tabindex
      and a correct aria-checked, and the accessibility tree reported the name "" for every one of
      them: the words beside a switch are a sibling <span class="tl">, which is a label to a reader
      with eyes and nothing at all to the tree. A screen reader said "switch, on" and never what
      had moved. (The other four, in the new-realm dialog, were not switches to the tree at all:
      that is the next finding.) Only the tree itself can say whether this is fixed.
    * THE FORGE WIZARD'S SWITCHES WERE MOUSE-ONLY. A dialog is written with innerHTML after the
      one pass that wires the window's own switches has run.
    * THE ATLAS LOST EVERY PLACE NAME AT A BIGGER TEXT SIZE. Text size is a page zoom, so the
      only way to read this is at a real devicePixelRatio, which a browser sets and a model
      cannot.
    * THE EMPTY SAVE CHART OVERFLOWED ITS OWN ROW onto the line above it, which is a measurement
      of two boxes on screen.

  HOW TO RUN
      node scripts/ui/switch_names_probe.js [--shots DIR]
      node scripts/ui/switch_names_probe.js --check

  It serves ValheimBakaLoader/WebUI itself on a port it picks, drives it headless, and closes
  both the server and the browser on the way out. Playwright is found through PLAYWRIGHT_PATH,
  through NODE_PATH, or at the usual global npm location. Exit 0 is a clean sweep.

  --check answers whether this box can run it at all and names what is missing when it cannot,
  which is what scripts/copy-gate/copy_gate.sh reads: the gate says NOT RUN on a machine with no
  browser rather than failing it. The question is answered HERE, beside the code that needs the
  answer, because a gate that worked it out for itself would eventually say yes to a box the probe
  then failed on.
*/

"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(process.env.BAKA_WEBUI
  || path.join(__dirname, "..", "..", "ValheimBakaLoader", "WebUI"));

const shotsAt = process.argv.indexOf("--shots");
const SHOTS = shotsAt > 0 ? process.argv[shotsAt + 1] : null;

function playwright() {
  const tries = [
    process.env.PLAYWRIGHT_PATH,
    "playwright",
    path.join(process.env.APPDATA || "", "npm", "node_modules", "playwright"),
  ].filter(Boolean);
  for (const where of tries) {
    try { return require(where); } catch (_) { /* try the next one */ }
  }
  throw new Error("playwright was not found. Set PLAYWRIGHT_PATH to its folder.");
}

const TYPES = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".woff2": "font/woff2",
  ".woff": "font/woff",
  ".ttf": "font/ttf",
};

function serve() {
  return new Promise(resolve => {
    const server = http.createServer((req, res) => {
      const asked = decodeURIComponent((req.url || "/").split("?")[0]);
      const file = path.resolve(ROOT, "." + (asked === "/" ? "/index.html" : asked));
      if (!file.startsWith(ROOT)) { res.writeHead(403).end(); return; }
      fs.readFile(file, (err, body) => {
        if (err) { res.writeHead(404).end(); return; }
        res.writeHead(200, { "content-type": TYPES[path.extname(file).toLowerCase()] || "application/octet-stream" });
        res.end(body);
      });
    });
    server.listen(0, "127.0.0.1", () => resolve({ server, port: server.address().port }));
  });
}

/**
 * What this box is missing before the probe could run, in the words somebody would need to fix it.
 * Empty means it can run. Three things have to hold: the playwright package has to resolve, the
 * chromium build it drives has to be on disk (the package alone is a library with nothing to
 * drive), and a socket on localhost has to be listenable, because the page is served from one.
 */
async function missingParts() {
  const missing = [];

  let pw = null;
  try { pw = playwright(); }
  catch (_) { missing.push("the playwright package (npm install -g playwright)"); }

  if (pw) {
    let exe = null;
    try { exe = pw.chromium.executablePath(); } catch (_) { exe = null; }
    if (!exe || !fs.existsSync(exe)) {
      missing.push("the chromium build playwright drives (npx playwright install chromium)");
    }
  }

  const port = await new Promise(resolve => {
    const probe = http.createServer();
    probe.on("error", () => resolve(false));
    probe.listen(0, "127.0.0.1", () => probe.close(() => resolve(true)));
  });
  if (!port) missing.push("a free port on 127.0.0.1");

  return missing;
}

let failures = 0;
let passed = 0;
function ok(name, condition, detail) {
  if (condition) { passed++; console.log("  ok   " + name); return true; }
  failures++;
  console.log("  FAIL " + name);
  if (detail !== undefined) console.log("       " + (typeof detail === "string" ? detail : JSON.stringify(detail)));
  return false;
}

/** Every switch the accessibility tree can see, with the name it reports for it. */
async function switchesInTree(page) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("Accessibility.enable");
  const tree = await cdp.send("Accessibility.getFullAXTree");
  const found = [];
  for (const node of tree.nodes || []) {
    const role = node.role && node.role.value;
    if (role !== "switch") continue;
    found.push({
      name: (node.name && node.name.value) || "",
      checked: (node.properties || []).filter(p => p.name === "checked")
        .map(p => String(p.value && p.value.value))[0] || "",
      backendId: node.backendDOMNodeId,
    });
  }
  await cdp.detach();
  return found;
}

async function probe() {
  const pw = playwright();
  const { server, port } = await serve();
  const browser = await pw.chromium.launch();
  const url = "http://127.0.0.1:" + port + "/index.html";

  try {
    /* ---------------------------------------------------- the window's own switches */
    const page = await (await browser.newContext({ viewport: { width: 1426, height: 1000 } })).newPage();
    /* Anything the page says went wrong, gathered for the last rule. A sentence declared below
       where it is read throws on evaluation and takes the whole file with it, and every rule here
       would then fail for a reason none of them names. */
    const shouted = [];
    page.on("pageerror", e => shouted.push("pageerror: " + (e && e.message)));
    page.on("console", m => { if (m.type() === "error") shouted.push("console: " + m.text()); });
    await page.goto(url, { waitUntil: "load" });
    await page.waitForTimeout(600);

    /* The tree only carries what is RENDERED, so a switch inside a folded card or on a hall that
       is not on screen is not in it. The halls are walked and the two folds opened, and the
       switches are gathered across the whole walk by their own node. */
    const held = new Map();
    const shown = new Set();
    const stops = [];
    const gather = async where => {
      /* What is on screen right now, so the tree can be held to covering all of it: a hall that
         is not showing and a card that is folded are not in the tree, and a count alone could not
         tell that apart from a switch the tree has no name for. */
      const visible = await page.evaluate(() => [...document.querySelectorAll(".toggle")]
        .filter(el => el.offsetParent !== null)
        .map(el => el.id || "(no id)"));
      const tree = await switchesInTree(page);
      for (const one of tree) held.set(one.backendId, Object.assign({ where }, one));
      for (const id of visible) shown.add(id);
      stops.push({ where, visible: visible.length, inTree: tree.length,
        nameless: tree.filter(s => !String(s.name).trim()).length, ids: visible });
    };
    await page.evaluate(async () => {
      goPage("hearth");
      if (!document.getElementById("upkeepCard").classList.contains("open")) document.getElementById("upkeepHead").click();
      if (!document.getElementById("connCard").classList.contains("open")) document.getElementById("connHead").click();
      await new Promise(r => setTimeout(r, 250));
    });
    await gather("hearth");
    for (const [name, prepare] of [
      /* The Settings hall's three folds, opened: World modifiers, Advanced rites and the
         directories. Eleven of the window's switches are behind them, and a switch inside a fold
         that is shut is not in the tree at all. */
      ["settings server", 'goPage("world");worldTab("server");'
        + '["secWorldMods","secRites","secDirs"].forEach(id=>{'
        + 'const el=document.getElementById(id);'
        + 'if(el&&!el.classList.contains("open"))el.click();});'],
      ["settings app", 'goPage("world");worldTab("app");'],
      ["players", 'goPage("vikings");'],
      ["discord", 'goPage("herald");'],
    ]) {
      await page.evaluate(code => eval(code), prepare);
      await page.waitForTimeout(250);
      await gather(name);
    }
    const inPage = [...held.values()];
    const nameless = inPage.filter(s => !String(s.name).trim());
    const short = stops.filter(s => s.inTree < s.visible);
    ok("every switch on screen is a named switch in the accessibility tree",
      shown.size >= 25 && nameless.length === 0 && short.length === 0,
      shown.size + " switches walked over " + stops.length + " stops, " + inPage.length
      + " seen in the tree, " + nameless.length + " with an empty name"
      + (nameless.length ? ": " + JSON.stringify(nameless) : "")
      + (short.length ? "; halls where the tree saw fewer than were on screen: "
        + JSON.stringify(short) : "")
      + "; three of the names: " + JSON.stringify(inPage.slice(0, 3).map(s => s.name)));
    console.log("       walked: " + stops.map(s => s.where + " " + s.visible).join(", ")
      + "; " + shown.size + " switches, every one named");
    ok("and each one still says which way it is set",
      inPage.every(s => s.checked === "true" || s.checked === "false"),
      JSON.stringify(inPage.filter(s => s.checked !== "true" && s.checked !== "false")));
    await page.evaluate(() => goPage("hearth"));

    /* ---------------------------------------------------- the Forge wizard's switches */
    await page.evaluate(() => { addServerProfile(); });
    await page.waitForTimeout(300);

    const wizard = await page.evaluate(() => ["wsPublic", "wsIso", "wsSeed", "wsSaveIso"].map(id => {
      const el = document.getElementById(id);
      return el ? {
        id, role: el.getAttribute("role"), tabindex: el.getAttribute("tabindex"),
        checked: el.getAttribute("aria-checked"), labelledby: el.getAttribute("aria-labelledby"),
        labelText: (document.getElementById(el.getAttribute("aria-labelledby") || "") || {}).textContent || "",
      } : { id, missing: true };
    }));
    ok("the wizard's four switches are switches, named, and in the tab order",
      wizard.every(w => !w.missing && w.role === "switch" && w.tabindex === "0"
        && (w.checked === "true" || w.checked === "false") && String(w.labelText).trim()),
      JSON.stringify(wizard));

    const inWizard = (await switchesInTree(page)).filter(s => s.name);
    ok("and the tree sees them with their own words",
      ["List in the community browser", "Separate install (own mods)",
        "Separate save folder (own worlds/backups)"].every(said =>
        inWizard.some(s => s.name.indexOf(said) >= 0)),
      JSON.stringify(inWizard.map(s => s.name)));

    /* Tab to one of them and move it with Space, which is what a keyboard-only host does. */
    const keyboard = await page.evaluate(async () => {
      const el = document.getElementById("wsIso");
      el.focus();
      const was = el.getAttribute("aria-checked");
      const focused = document.activeElement === el;
      el.dispatchEvent(new KeyboardEvent("keydown", { key: " ", bubbles: true, cancelable: true }));
      await new Promise(r => setTimeout(r, 30));
      return { focused, was, now: el.getAttribute("aria-checked"), on: el.classList.contains("on") };
    });
    ok("Space moves a wizard switch and the aria goes with it",
      keyboard.focused && keyboard.was === "true" && keyboard.now === "false" && !keyboard.on,
      JSON.stringify(keyboard));

    await page.evaluate(() => modalClose());

    /* ---------------------------------------------------- the realm band and the palette */
    const band = await page.evaluate(async () => {
      /* A realm to be editing. renderEditBar takes the band down when there is no profile at all,
         and a browser has no host to hand it one, so without this the band is down on both tabs
         and the rule would pass over a page that cannot tell them apart. */
      S.prefs = Object.assign({}, S.prefs, {
        ProfileName: "Second Sunset", ServerName: "Second Sunset",
        WorldName: "There and Back again", Port: 2460, RconEnabled: true, RconPort: 25577,
      });
      goPage("world");
      worldTab("server");
      await new Promise(r => setTimeout(r, 200));
      const onServer = getComputedStyle(document.getElementById("editBar")).display;
      const saidServer = document.getElementById("editBar").textContent.trim().slice(0, 40);
      worldTab("app");
      await new Promise(r => setTimeout(r, 200));
      return { onServer, saidServer, onApp: getComputedStyle(document.getElementById("editBar")).display };
    });
    ok("the realm band stands on the Server tab and not on the App tab",
      band.onServer !== "none" && band.saidServer.length > 0 && band.onApp === "none",
      JSON.stringify(band));

    const landing = await page.evaluate(async () => {
      goPage("hearth");
      await new Promise(r => setTimeout(r, 80));
      openAppTab("selTextSize");
      await new Promise(r => setTimeout(r, 120));
      const want = document.getElementById("selTextSize");
      const row = want.closest(".togglerow");
      return {
        focused: document.activeElement === want,
        lit: !!(row && row.classList.contains("rowflash")),
        appTab: getComputedStyle(document.getElementById("worldTabApp")).display !== "none",
      };
    });
    ok("the palette's landing points at the row it promised",
      landing.appTab && landing.focused && landing.lit, JSON.stringify(landing));

    /* ---------------------------------------------------- the empty save chart */
    /* The two the walk photographed, plus a tall one: a rule that only ever sees the ornament
       stand down is half a rule. */
    for (const [w, h] of [[1408, 800], [1024, 680], [1600, 1200]]) {
      const sized = await (await browser.newContext({ viewport: { width: w, height: h } })).newPage();
      await sized.goto(url, { waitUntil: "load" });
      await sized.waitForTimeout(500);
      const read = await sized.evaluate(() => {
        goPage("hearth");
        S.saveDur = [];
        S.lastSaveAt = null;
        S.lastSaveMs = null;
        renderSaveBars();
        renderLastSave();
        renderSaveAvg();
        saveBarsEmptyFit();
        const box = document.getElementById("saveBars");
        const empty = box.querySelector(".empty-state");
        const metrics = document.getElementById("saveCountdown").closest("div").parentElement;
        const mark = box.querySelector(".es-mark");
        const r = empty.getBoundingClientRect();
        const m = metrics.getBoundingClientRect();
        const b = box.getBoundingClientRect();
        return {
          rowHeight: Math.round(b.height),
          fullPx: SAVE_EMPTY_FULL_PX,
          emptyTop: Math.round(r.top), emptyBottom: Math.round(r.bottom),
          metricsBottom: Math.round(m.bottom),
          markShown: !!mark && getComputedStyle(mark).display !== "none",
          norune: box.classList.contains("es-norune"),
          title: (box.querySelector(".es-title") || {}).textContent || "",
        };
      });
      console.log("       saves card at " + w + "x" + h + ": " + JSON.stringify(read));
      ok("the empty chart stays below the metric line at " + w + "x" + h,
        read.emptyTop >= read.metricsBottom - 1 && read.emptyBottom <= read.emptyTop + read.rowHeight + 1,
        JSON.stringify(read));
      ok("and it still says what it is at " + w + "x" + h,
        String(read.title).trim().length > 0, JSON.stringify(read));
      /* The ornament, which is the half a stylesheet cannot decide: it stands when the row has
         room for the whole stack and stands down when it has not. */
      ok("the ornament matches the room the row has at " + w + "x" + h,
        read.markShown === (read.rowHeight >= read.fullPx) && read.norune === !read.markShown,
        JSON.stringify(read));
      if (SHOTS) {
        const card = await sized.$("#savesCard");
        await card.screenshot({ path: path.join(SHOTS, "saves_empty_" + w + "x" + h + ".png") });
        await sized.screenshot({ path: path.join(SHOTS, "hearth_" + w + "x" + h + ".png") });
      }
      await sized.context().close();
    }

    /* ---------------------------------------------------- the Atlas, at real zooms

       Text size is the page's zoom: the CSS viewport shrinks and devicePixelRatio rises by the
       factor, which is what a context with a smaller viewport and a bigger deviceScaleFactor is.
       So these are read at real devicePixelRatios rather than at a faked one.

       What the fix guarantees is about the CHART rather than about the window: a chart at the same
       number of metres to a DEVICE pixel draws the same names at every text size. (The pane itself
       is physically smaller at a bigger text size, because the nav and the side panel keep their
       CSS widths and so grow on screen; fewer names on a smaller pane is the layout rather than
       the cull, and the names that are drawn are the same physical size.) */
    const chart = [];
    for (const dsf of [1, 1.2, 1.45]) {
      const atlas = await (await browser.newContext({
        viewport: { width: Math.round(1426 / dsf), height: Math.round(1000 / dsf) },
        deviceScaleFactor: dsf,
      })).newPage();
      await atlas.goto(url, { waitUntil: "load" });
      await atlas.waitForTimeout(500);
      const read = await atlas.evaluate(async () => {
        goPage("atlas");
        await new Promise(r => setTimeout(r, 900));
        ATLAS.layers.fog = false;
        ATLAS.layers.pins = true;
        ATLAS.info = Object.assign({}, ATLAS.info || {}, {
          hasDb: true,
          pins: [{ name: "$enemy_eikthyr", x: 0, z: 0 }, { name: "$hud_pin_hildir2", x: 900, z: -900 }],
        });
        atlasFit();
        atlasDraw();
        renderAtlasSide();
        const names = [...document.querySelectorAll("#atlasWaypoints .wn")].map(n => n.textContent);
        /* WHETHER A LABEL WAS REALLY DRAWN, read off the canvas rather than asked of the helper.
           The chart is drawn twice over the same camera, once with the pin named and once with its
           name taken away, and the strip to the RIGHT of the marker is compared pixel for pixel: a
           label is the only thing that differs between the two draws there. Counting bright pixels
           instead would not work, because the biome map underneath is already bright; and reading
           the threshold expression would pass over a cull put back to counting CSS pixels, because
           the expression is not where the decision is made. */
        const strip = named => {
          ATLAS.info.pins = [{ name: named ? "$enemy_eikthyr" : "", x: 0, z: 0 }];
          atlasDraw();
          const cv = document.getElementById("atlasCanvas");
          const dpr = window.devicePixelRatio || 1;
          /* The marker sits at the world's centre and the label starts six CSS pixels to its
             right. The strip starts past the marker so the marker's own ink is never in it. */
          return cv.getContext("2d").getImageData(
            Math.round((w2sX(0) + 6) * dpr), Math.round((w2sY(0) - 7) * dpr),
            Math.round(70 * dpr), Math.round(14 * dpr)).data;
        };
        const labelInk = () => {
          const named = strip(true);
          const bare = strip(false);
          let different = 0;
          for (let i = 0; i < named.length; i += 4) {
            if (named[i] !== bare[i] || named[i + 1] !== bare[i + 1] || named[i + 2] !== bare[i + 2]) different++;
          }
          return different;
        };
        const atDensity = metresPerDevicePixel => {
          ATLAS.cam.ppm = 1 / (metresPerDevicePixel * window.devicePixelRatio);
          const ink = labelInk();
          return { labels: ink > 0, ink, oldRule: ATLAS.cam.ppm > 0.045 };
        };
        /* A chart a host can read a name on, and one far enough out that a name is ink rather
           than information. Both given in metres to a DEVICE pixel, so they are the same chart
           on screen at every text size. */
        const close = atDensity(16);
        const far = atDensity(60);
        atlasFit();
        atlasDraw();
        return {
          dpr: window.devicePixelRatio,
          metresPerCssPixel: Math.round(1 / ATLAS.cam.ppm),
          metresPerDevicePixel: Math.round(1 / (ATLAS.cam.ppm * window.devicePixelRatio)),
          close, far, names,
        };
      });
      chart.push(read);
      if (SHOTS) await atlas.screenshot({ path: path.join(SHOTS, "atlas_dsf" + dsf + ".png") });
      await atlas.context().close();
    }
    ok("a chart at one density draws the same names at every text size",
      chart.every(c => c.close.labels === true) && chart.every(c => c.far.labels === false),
      JSON.stringify(chart.map(c => ({ dpr: c.dpr, close: c.close, far: c.far }))));
    ok("and the CSS density really moved, so the two rules can be told apart",
      new Set(chart.map(c => c.metresPerCssPixel)).size > 1
      && chart.some(c => c.close.oldRule !== chart[0].close.oldRule),
      JSON.stringify(chart.map(c => ({ dpr: c.dpr, mpx: c.metresPerCssPixel,
        oldRuleAtTheSameChart: c.close.oldRule }))));
    /* The owner's own two readings, at the two devicePixelRatios they were taken at: 22 metres to a
       CSS pixel at Normal on a 144 dpi display (1.5) and 29 at Large (1.5 x 1.2 = 1.8). Under the
       rule this replaced the first draws names and the second draws none, which IS the finding. */
    const walk = [];
    for (const [dsf, metres] of [[1.5, 22], [1.8, 29]]) {
      const atlas = await (await browser.newContext({
        viewport: { width: Math.round(2138 / dsf), height: Math.round(1500 / dsf) },
        deviceScaleFactor: dsf,
      })).newPage();
      await atlas.goto(url, { waitUntil: "load" });
      await atlas.waitForTimeout(400);
      walk.push(await atlas.evaluate(async m => {
        goPage("atlas");
        await new Promise(r => setTimeout(r, 700));
        ATLAS.layers.fog = false;
        ATLAS.layers.pins = true;
        ATLAS.info = Object.assign({}, ATLAS.info || {}, { hasDb: true, pins: [] });
        ATLAS.cam.ppm = 1 / m;
        const strip = named => {
          ATLAS.info.pins = [{ name: named ? "$enemy_eikthyr" : "", x: 0, z: 0 }];
          atlasDraw();
          const cv = document.getElementById("atlasCanvas");
          const dpr = window.devicePixelRatio || 1;
          return cv.getContext("2d").getImageData(
            Math.round((w2sX(0) + 6) * dpr), Math.round((w2sY(0) - 7) * dpr),
            Math.round(70 * dpr), Math.round(14 * dpr)).data;
        };
        const named = strip(true);
        const bare = strip(false);
        let ink = 0;
        for (let i = 0; i < named.length; i += 4) {
          if (named[i] !== bare[i] || named[i + 1] !== bare[i + 1] || named[i + 2] !== bare[i + 2]) ink++;
        }
        return {
          dpr: window.devicePixelRatio, metresPerCssPixel: m,
          labels: ink > 0, ink, oldRule: ATLAS.cam.ppm > 0.045,
        };
      }, metres));
      await atlas.context().close();
    }
    ok("the owner's own world keeps its names at Normal and at Large",
      walk.every(w => w.labels === true), JSON.stringify(walk));
    ok("and the rule this replaced really did lose them at Large",
      walk[0].oldRule === true && walk[1].oldRule === false, JSON.stringify(walk));

    ok("the Waypoints list shows names rather than Valheim's own keys",
      chart.every(c => c.names.length >= 2 && !c.names.some(n => String(n).startsWith("$"))
        && c.names.indexOf("Eikthyr") >= 0 && c.names.indexOf("Howling Cavern") >= 0),
      JSON.stringify(chart.map(c => c.names)));

    /* Every hall was walked, both tabs pressed, the wizard opened and closed, and a language row
       drawn, on one page. Nothing in any of that is allowed to have thrown. */
    ok("nothing in the window threw while all of that was driven", shouted.length === 0,
      JSON.stringify(shouted.slice(0, 6)));

    await page.context().close();
  } finally {
    await browser.close();
    server.close();
  }

  console.log("");
  console.log("switch names probe: " + passed + " passed" + (failures ? ", " + failures + " FAILED" : ""));
  process.exit(failures ? 1 : 0);
}

if (process.argv.includes("--check")) {
  missingParts().then(missing => {
    if (!missing.length) { console.log("switch names probe: ready"); process.exit(0); }
    console.log("switch names probe: cannot run here, it needs " + missing.join(", and ") + ".");
    process.exit(1);
  }).catch(e => { console.log("switch names probe: " + (e && e.message ? e.message : e)); process.exit(1); });
} else {
  probe().catch(e => { console.error(e); process.exit(2); });
}
