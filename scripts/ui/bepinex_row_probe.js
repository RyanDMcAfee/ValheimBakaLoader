#!/usr/bin/env node
/*
  Every state the loader row can draw, every shape of the question, and every confirm the
  takeover put in front of a write, measured in a real browser at the smallest window the
  app opens at.

  WHY THIS EXISTS. The table in bepinex_row_selftest.js proves the page CHOOSES the right
  sentence. It cannot see whether that sentence fits. The row is one flex line with a pill,
  a version, a message and the buttons on it, and the new sentences are two to three times
  longer than "Kept up to date by BakaLoader."; the confirms carry four paragraphs where the
  old ones carried one. At 1024 by 680, which is the app's minimum window, either can run
  off the bottom or push the buttons off the end with nothing to say it did.

  Four questions per surface:
    * nothing is clipped: no element's scroll extent is wider or taller than its own box
    * the page itself never scrolls sideways
    * a dialog fits the viewport, so its buttons can be reached without a scroll
    * the row keeps its buttons on the same line as its name

  Run twice: in English, and in the generated pseudo locale, which pads every sentence by a
  third and is the closest thing here to a translation that runs long.

  Prints one line per surface, then TOTAL n, and exits non zero when n is not 0.

  node scripts/ui/bepinex_row_probe.js
*/
"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..", "..", "ValheimBakaLoader", "WebUI");
const SHOTS = process.env.BAKA_SHOTS || "";

const TYPES = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8", ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml", ".png": "image/png", ".woff2": "font/woff2",
};

function playwright() {
  const tries = [
    "playwright",
    path.join(process.env.APPDATA || "", "npm", "node_modules", "playwright"),
  ];
  for (const where of tries) {
    try { return require(where); } catch (_) { /* next */ }
  }
  throw new Error("playwright was not found");
}

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

/* A host that answers nothing in particular. Every surface below is driven through
   BakaPreview, which is the seam the app's own events use. */
const STUB = `(() => {
  const listeners = [];
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    addEventListener: (k, fn) => { if (k === "message") listeners.push(fn); },
    postMessage: m => { if (m.id == null) return;
      setTimeout(() => listeners.forEach(fn => fn({ data: { id: m.id, ok: true, result: {} } })), 0); },
  };
})()`;

/* The shapes the host's answer arrives in, the same ones the selftest tables. */
const WHOLE = {
  installed: true, baseFolder: "D:\\\\steamlibrary\\\\Valheim dedicated server",
  maintainedByBakaLoader: false, packVersion: null, coreFileVersion: null,
  coreVersion: "5.4.23.5", loaderFilePresent: true, package: "denikson-BepInExPack_Valheim",
  source: "thunderstore", installedUtc: null, wrongLocationFolder: null,
  doorstopTarget: null, drivenElsewhere: false, foreignCore: false, damaged: false,
  unrecognised: false, missingFiles: [], drifted: false, newestBackup: null,
  interruptedWrite: false, coreIsJunction: false,
  sharingProfiles: ["Final Sunset", "Midgard Test"], runningProfiles: [],
  maintained: true, maintenanceAsked: true, consent: true, consentUnanswered: false,
  result: null, lastUnattended: null, updateWaiting: null, busy: false,
};
const MINE = { maintainedByBakaLoader: true, packVersion: "5.4.2350" };
const dto = changed => Object.assign({}, WHOLE, changed || {});

const ROWS = [
  ["missing", dto({ installed: false, coreVersion: null })],
  ["maintained", dto(MINE)],
  ["manual", dto(Object.assign({}, MINE, { maintained: false, consent: false }))],
  ["outside", dto({})],
  ["drifted", dto({ drifted: true })],
  ["elsewhere", dto({ drivenElsewhere: true, doorstopTarget: "..\\\\profiles\\\\Default\\\\BepInEx\\\\core\\\\BepInEx.Preloader.dll" })],
  ["foreign", dto({ foreignCore: true, coreVersion: "6.0.0.0" })],
  ["unrecognised", dto({ unrecognised: true, damaged: true, installed: false, coreVersion: null, newestBackup: "20260920-011500" })],
  ["damaged", dto(Object.assign({}, MINE, { damaged: true, installed: false, coreVersion: null, newestBackup: "20260920-011500" }))],
  ["incomplete", dto(Object.assign({}, MINE, { installed: false, loaderFilePresent: false, missingFiles: ["winhttp.dll"] }))],
  // the outside row with the host's yes already given: the note about the next restart and
  // the press that reaches the switch
  ["outside-consented", dto({ consent: true, maintenanceAsked: true })],
  // a pack the window is holding back, which is the sentence section M asks the row for
  ["soaking", dto(Object.assign({}, MINE, {
    lastUnattended: {
      outcome: "refused", reason: "soak", version: "5.4.2400",
      eligibleUtc: "2026-09-23T04:00:00Z", leftAsItWas: true,
      whenUtc: "2026-09-20T04:00:00Z",
    },
  }))],
];

/* The standing rows: the answer that raises each one, the server state it is raised
   under, and the fewest buttons it may carry (the dismissal always counts as one). */
const CONDITIONS = [
  ["ask", dto({ consent: false, consentUnanswered: true, maintenanceAsked: false }), "Stopped", 3],
  ["missing-files", dto(Object.assign({}, MINE, {
    installed: false, loaderFilePresent: false, missingFiles: ["winhttp.dll"],
  })), "Stopped", 2],
  ["left-alone-soak", dto(Object.assign({}, MINE, {
    lastUnattended: {
      outcome: "refused", reason: "soak", version: "5.4.2400",
      eligibleUtc: "2026-09-23T04:00:00Z", leftAsItWas: true, whenUtc: "2026-09-20T04:00:00Z",
    },
  })), "Stopped", 2],
  ["left-alone-elsewhere", dto({
    drivenElsewhere: true,
    lastUnattended: {
      outcome: "refused", reason: "drivenElsewhere", leftAsItWas: true,
      whenUtc: "2026-09-20T04:00:00Z",
    },
  }), "Stopped", 2],
  ["written", dto(Object.assign({}, MINE, {
    lastUnattended: {
      outcome: "written", fromVersion: "5.4.2350", toVersion: "5.4.2400",
      backupStamp: "20260920-011500", backupFolder: ".bakaloader-bepinex-backups",
      leftAsItWas: false, whenUtc: "2026-09-20T04:00:00Z",
    },
  })), "Stopped", 2],
];

/* The question, in each of the four shapes it takes, and the three confirms. */
const DIALOGS = [
  ["ask-fresh", dto({ installed: false, coreVersion: null, consent: false, consentUnanswered: true, maintenanceAsked: false }), "bepinexFirstDialog"],
  ["ask-outside", dto({ consent: false, consentUnanswered: true, maintenanceAsked: false }), "bepinexFirstDialog"],
  ["ask-elsewhere", dto({ drivenElsewhere: true, consent: false, consentUnanswered: true, maintenanceAsked: false }), "bepinexFirstDialog"],
  ["ask-foreign", dto({ foreignCore: true, coreVersion: "6.0.0.0", consent: false, consentUnanswered: true, maintenanceAsked: false }), "bepinexFirstDialog"],
  ["takeover", dto({ updateWaiting: "5.4.2400" }), "bepinexTakeover"],
  ["takeover-elsewhere", dto({ drivenElsewhere: true, consent: true }), "bepinexTakeover"],
  ["takeover-unrecognised", dto({ unrecognised: true, damaged: true, installed: false, coreVersion: null, newestBackup: "20260920-011500" }), "bepinexTakeover"],
  ["downgrade", dto({}), "bepinexDowngrade"],
  ["restore", dto({ damaged: true, installed: false, coreVersion: null, newestBackup: "20260920-011500" }), "bepinexRestore"],
];

/* What a measurement has to answer. Written once, asked of a row and of a dialog alike. */
const MEASURE = `(where) => {
  const root = document.querySelector(where.selector);
  if (!root) return { found: false };
  const clipped = [];
  const walk = el => {
    const box = el.getBoundingClientRect();
    if (box.width < 1 || box.height < 1) return;
    const over = getComputedStyle(el).overflow;
    if (over === "visible" || over === "") {
      if (el.scrollWidth - el.clientWidth > 1 && el.clientWidth > 0)
        clipped.push((el.id || el.className || el.tagName) + " is " + el.scrollWidth + "px wide in " + el.clientWidth + "px");
      if (el.scrollHeight - el.clientHeight > 1 && el.clientHeight > 0)
        clipped.push((el.id || el.className || el.tagName) + " is " + el.scrollHeight + "px tall in " + el.clientHeight + "px");
    }
    Array.prototype.forEach.call(el.children, walk);
  };
  walk(root);
  const box = root.getBoundingClientRect();
  const buttons = Array.from(root.querySelectorAll("button"));
  return {
    found: true,
    text: (root.textContent || "").replace(/\\s+/g, " ").trim().slice(0, 96),
    width: Math.round(box.width), height: Math.round(box.height),
    top: Math.round(box.top), bottom: Math.round(box.bottom),
    buttons: buttons.length,
    buttonsOut: buttons.filter(b => {
      const r = b.getBoundingClientRect();
      return r.right > innerWidth + 1 || r.bottom > innerHeight + 1 || r.width < 1;
    }).length,
    pageScrollsSideways: document.documentElement.scrollWidth > innerWidth + 1,
    clipped: clipped.slice(0, 3),
  };
}`;

async function shot(page, name) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name + ".png") });
}

(async () => {
  const { server, port } = await serve();
  const { chromium } = playwright();
  const browser = await chromium.launch({ headless: true });
  let failures = 0;

  const say = (ok, language, name, said, problems) => {
    const head = (ok ? "ok   " : "FAIL ") + language.padEnd(3) + name.padEnd(24) + said;
    console.log(problems.length ? head + "  << " + problems.join("; ") : head);
    if (!ok) failures++;
  };

  try {
    const page = await browser.newPage({ viewport: { width: 1024, height: 680 } });
    await page.addInitScript(STUB);
    await page.goto("http://127.0.0.1:" + port + "/index.html", { waitUntil: "load" });
    await page.waitForTimeout(700);

    const pseudo = JSON.parse(fs.readFileSync(path.join(ROOT, "i18n", "xx.json"), "utf8"));

    for (const language of ["en", "xx"]) {
      if (language === "xx")
        await page.evaluate(cat => window.BakaPreview.setLanguage("xx", cat), pseudo);
      await page.evaluate(() => { document.querySelector('[data-page="mods"]').click(); });
      await page.waitForTimeout(150);

      for (const [name, answer] of ROWS) {
        // The preview opens its first-run wizard on a timer, which would sit over every
        // screenshot from here on. The row is measured either way, but a picture of a
        // dialog is not a picture of the row.
        await page.evaluate(a => { modalClose(); window.BakaPreview.bepinexStatus(a); }, answer);
        await page.waitForTimeout(60);
        const seen = await page.evaluate(new Function("return " + MEASURE)(), { selector: "#bepinexRow" });
        const problems = [];
        if (!seen.found) problems.push("nothing was drawn");
        else {
          if (seen.clipped.length) problems.push(...seen.clipped);
          if (seen.pageScrollsSideways) problems.push("the page scrolls sideways");
          if (seen.buttonsOut) problems.push(seen.buttonsOut + " button is off the window");
        }
        await shot(page, language + "-row-" + name);
        say(!problems.length, language, name,
          seen.found ? seen.width + "x" + seen.height + "  " + seen.buttons + " buttons" : "",
          problems);
      }

      // The standing rows, which are condition bar entries rather than dialogs. The bar
      // draws one at a time and worst first, so each is raised on its own with the rest of
      // the answer clean, or a higher row would hide the one being looked at.
      for (const [name, answer, status, least] of CONDITIONS) {
        await page.evaluate(([a, s]) => {
          modalClose();
          window.BakaPreview.bepinexStatus(a);
          window.BakaPreview.bepinex(a, s);
        }, [answer, status]);
        await page.waitForTimeout(80);
        const bar = await page.evaluate(new Function("return " + MEASURE)(), { selector: "#launchHold" });
        const problems = [];
        if (!bar.found || bar.height < 10) problems.push("the row was never drawn");
        else {
          if (bar.clipped.length) problems.push(...bar.clipped);
          if (bar.buttons < least) problems.push("it carries " + bar.buttons + " buttons, not " + least);
          if (bar.buttonsOut) problems.push(bar.buttonsOut + " button is off the window");
          if (bar.pageScrollsSideways) problems.push("the page scrolls sideways");
        }
        await shot(page, language + "-condition-" + name);
        say(!problems.length, language, "condition-" + name,
          bar.found ? bar.width + "x" + bar.height + "  " + bar.buttons + " buttons" : "", problems);
      }

      for (const [name, answer, seam] of DIALOGS) {
        await page.evaluate(([a, s]) => {
          window.BakaPreview.bepinexStatus(a);
          window.BakaPreview[s]();
        }, [answer, seam]);
        await page.waitForTimeout(80);
        const seen = await page.evaluate(new Function("return " + MEASURE)(), { selector: "#modalBg .modal" });
        const problems = [];
        if (!seen.found) problems.push("no dialog opened");
        else {
          if (seen.clipped.length) problems.push(...seen.clipped);
          if (seen.bottom > 680) problems.push("it runs " + (seen.bottom - 680) + "px past the bottom of the window");
          if (seen.top < 0) problems.push("it starts " + -seen.top + "px above the window");
          if (seen.buttonsOut) problems.push(seen.buttonsOut + " button is off the window");
          if (seen.buttons < 2) problems.push("it carries " + seen.buttons + " buttons");
        }
        await shot(page, language + "-dialog-" + name);
        say(!problems.length, language, name,
          seen.found ? seen.width + "x" + seen.height + " at " + seen.top + "  " + seen.buttons + " buttons" : "",
          problems);
        await page.evaluate(() => modalClose());
      }
    }

    console.log("");
    console.log("TOTAL " + failures + "  every row state and every dialog at 1024x680, English and pseudo");
    process.exitCode = failures ? 1 : 0;
  } finally {
    await browser.close();
    server.close();
  }
})().catch(err => { console.error(err); process.exit(2); });
