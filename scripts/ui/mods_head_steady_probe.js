#!/usr/bin/env node
/*
  Issue 34, measured in a real browser: nothing in the Mods head moves when a header is clicked.

  WHY THIS EXISTS. Up to 1.3.2 the head carried a sentence ("sorted by Author, ascending") that
  was hidden while the table rested on Name and appeared the moment a header moved it. It came
  in between the Needs line and the search box, so the search box and the buttons slid along,
  and at narrower widths the head wrapped onto another line and the table's top dropped. The
  sentence is a screen reader's line now (vhide): it is still written, it takes no room, and the
  header's mark, colour and aria-sort are what the eye reads. Whether a box moved is a question
  for the browser, which the node selftest cannot answer.

  Asked, at 1280x800 and 980x700, in English and in the pseudo catalog:
    * at rest, the boxes of the Mods head (.pagehead), the search box and Add from Thunderstore;
    * then each sortable header clicked twice, with a real mouse: after every click all three
      boxes equal the resting ones within 1px, the page did not scroll, the clicked header
      says how it is sorted (aria-sort), and the sort line holds the sentence modSortStatement
      gives while it measures no bigger than 1x1.
  One screenshot pair at 980x700 in the pseudo catalog, at rest and sorted by Author, to the
  folder named by BAKA_SHOTS (or the notes folder's shots/B1).

  Prints one line per check, then TOTAL n, and exits non zero when n is not 0.

  node scripts/ui/mods_head_steady_probe.js
  BAKA_WEBUI=<another WebUI folder> runs it against another copy of the page.
*/
"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

const ROOT = process.env.BAKA_WEBUI ? path.resolve(process.env.BAKA_WEBUI) : path.resolve(__dirname, "..", "..", "ValheimBakaLoader", "WebUI");
const SHOTS = process.env.BAKA_SHOTS || path.resolve(__dirname, "..", "..", "..", "ValheimBakaLoader-133-notes", "shots", "B1");
const SIZES = [{ width: 1280, height: 800 }, { width: 980, height: 700 }];
const TOLERANCE = 1;

const TYPES = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8", ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml", ".png": "image/png", ".woff2": "font/woff2",
};

function playwright() {
  const tries = ["playwright", path.join(process.env.APPDATA || "", "npm", "node_modules", "playwright")];
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

/* A host that answers nothing in particular. */
const STUB = `(() => {
  const listeners = [];
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    addEventListener: (k, fn) => { if (k === "message") listeners.push(fn); },
    postMessage: m => { if (m.id == null) return;
      setTimeout(() => listeners.forEach(fn => fn({ data: { id: m.id, ok: true, result: {} } })), 0); },
  };
})()`;

/* A scanned list with every kind of Status and some blank dates, resting on Name A to Z. */
function plant() {
  try { modalClose(); } catch (_) { /* no dialog */ }
  document.querySelector('[data-page="mods"]').click();
  const mod = (author, name, version, extra) => Object.assign({
    FullName: author + "-" + name, ModName: name, Author: author, InstalledVersion: version, LatestVersion: version,
    UpdateAvailable: false, installedUtc: "2026-10-20T10:00:00Z", modUpdatedUtc: "2026-10-01T10:00:00Z",
    installedSource: "thunderstore", switchable: true, disabled: false,
  }, extra || {});
  S.mods = [
    mod("Acme", "Aurora", "1.2.3"), mod("Acme", "Borealis", "2.0.0", { disabled: true }),
    mod("Acme", "CirrusWithAMuchLongerNameThanMost", "10.4.12", { notListed: true, LatestVersion: "" }),
    mod("Acme", "Drift", "0.9.1", { installedSource: "local", modUpdatedUtc: null }),
    mod("Acme", "Ember", "3.1.0", { UpdateAvailable: true, LatestVersion: "3.2.0" }),
    mod("Zephyr", "Frost", "1.0.0", { installedSource: "hexium", thunderstoreNewer: true, LatestVersion: "1.1.0", modUpdatedUtc: null }),
    mod("Zephyr", "Gale", "4.4.4", { installedUtc: null }), mod("Acme", "Patcher", "1.0.0", { switchable: false, Bundled: true }),
  ];
  S.modsScanned = true; S.modsScanning = false; S.modsUpdating = false; S.modRowStatus = {}; S.modsIndexBlind = false;
  if (S.modTicks && S.modTicks.clear) S.modTicks.clear();
  S.modFilter = "";
  const box = document.getElementById("modSearch"); if (box) box.value = "";
  sortReset(S.modSort);
  renderMods();
}

function boxes() {
  const r = el => {
    if (!el) return null;
    const b = el.getBoundingClientRect();
    return { x: b.left, y: b.top, w: b.width, h: b.height };
  };
  const line = document.getElementById("modsSortLine");
  return {
    head: r(document.querySelector("#page-mods > .pagehead")),
    search: r(document.getElementById("modSearch")),
    add: r(document.getElementById("addModBtn")),
    line: r(line), lineText: line ? line.textContent : null,
    said: typeof modSortStatement === "function" ? modSortStatement() : null,
    scroll: document.getElementById("page-mods").scrollTop,
    cols: Array.from(document.querySelectorAll("#page-mods th.sortable")).map(th => th.dataset.sort),
  };
}

function differs(a, b) {
  if (!a || !b) return "missing";
  const out = [];
  for (const k of ["x", "y", "w", "h"]) if (Math.abs(a[k] - b[k]) > TOLERANCE) out.push(k + " " + a[k].toFixed(1) + " to " + b[k].toFixed(1));
  return out.join(", ");
}

(async () => {
  const { server, port } = await serve();
  const { chromium } = playwright();
  const browser = await chromium.launch({ headless: true });
  let failures = 0;
  const shots = [];
  const say = (bad, line) => { console.log((bad ? "FAIL " : "ok   ") + line); if (bad) failures++; };
  const pseudo = JSON.parse(fs.readFileSync(path.join(ROOT, "i18n", "xx.json"), "utf8"));

  try {
    for (const size of SIZES) {
      for (const language of ["en", "xx"]) {
        const page = await browser.newPage({ viewport: size });
        await page.addInitScript(STUB);
        await page.goto("http://127.0.0.1:" + port + "/index.html", { waitUntil: "load" });
        await page.waitForTimeout(700);
        if (language === "xx") await page.evaluate(cat => window.BakaPreview.setLanguage("xx", cat), pseudo);
        await page.evaluate(plant);
        await page.waitForTimeout(450);
        const where = size.width + "x" + size.height + " " + language.padEnd(3);
        const rest = await page.evaluate(boxes);
        say(!rest.head || !rest.search || !rest.add || !rest.cols.length,
          where + "at rest".padEnd(22) + "head " + (rest.head ? rest.head.h.toFixed(1) + "px tall" : "missing") +
          ", " + rest.cols.length + " sortable headers");

        const shoot = async name => {
          if (size.width !== 980 || language !== "xx") return;
          fs.mkdirSync(SHOTS, { recursive: true });
          const file = path.join(SHOTS, name);
          const clip = await page.evaluate(() => {
            const p = document.getElementById("page-mods").getBoundingClientRect();
            const t = document.querySelector("#modsTable thead").getBoundingClientRect();
            const y = Math.max(0, p.top);
            return { x: Math.max(0, p.left), y, width: Math.min(window.innerWidth - p.left, p.width),
              height: Math.min(window.innerHeight - y, Math.max(120, t.bottom - y + 70)) };
          });
          await page.screenshot({ path: file, clip });
          shots.push(file);
        };
        await shoot("b1_mods_head_rest_980_xx.png");

        for (const col of rest.cols) {
          for (const n of [1, 2]) {
            await page.click('#page-mods th.sortable[data-sort="' + col + '"]');
            await page.waitForTimeout(260);
            const now = await page.evaluate(boxes);
            const moved = ["head", "search", "add"].map(k => {
              const d = differs(rest[k], now[k]);
              return d ? k + " " + d : "";
            }).filter(Boolean);
            const aria = await page.evaluate(c => {
              const th = document.querySelector('#page-mods th.sortable[data-sort="' + c + '"]');
              return th ? th.getAttribute("aria-sort") : null;
            }, col);
            const st = await page.evaluate(() => ({ col: S.modSort.col, dir: S.modSort.dir }));
            const atHome = st.col === "name" && st.dir === 1;
            const ariaOk = atHome ? aria === "ascending" || col !== "name" && aria === "none" : st.col === col && aria !== "none";
            const lineOk = now.lineText === now.said && (atHome ? now.said === "" : !!now.said) &&
              !!now.line && now.line.w <= 1 && now.line.h <= 1;
            const scrolled = Math.abs(now.scroll - rest.scroll) > 0;
            const bad = moved.length > 0 || !ariaOk || !lineOk || scrolled;
            say(bad, where + (col + " click " + n).padEnd(22) +
              (moved.length ? "moved: " + moved.join("; ") : "head, search and Add still") +
              (ariaOk ? "" : ", aria-sort " + aria) +
              (lineOk ? "" : ", sort line " + JSON.stringify(now.lineText) + " " + (now.line ? now.line.w.toFixed(0) + "x" + now.line.h.toFixed(0) : "missing")) +
              (scrolled ? ", the hall scrolled" : "") + (bad ? "  <<" : ""));
            if (col === "author" && n === 1) await shoot("b1_mods_head_author_980_xx.png");
          }
        }
        await page.close();
      }
    }
    console.log("");
    for (const s of shots) console.log("shot " + s);
    console.log("TOTAL " + failures + "  the Mods head holds still while the headers sort, 1280x800 and 980x700, English and pseudo");
    process.exitCode = failures ? 1 : 0;
  } finally {
    await browser.close();
    server.close();
  }
})().catch(err => { console.error(err); process.exit(2); });
