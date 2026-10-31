#!/usr/bin/env node
/*
  Issue 33, measured in a real browser at the 1024x680 minimum, in English and in the pseudo
  catalog: the Mods table with its tick column and the bar of five buttons above it.

  WHY THIS EXISTS. 1.3.2 puts a ninth cell at the front of every Mods row and a bar of five
  buttons between the BepInEx row and the table. Whether the hall then scrolls sideways, whether
  the bar wraps or runs out, and whether a button's words are cut are all about where the browser
  puts a box, which the node selftest cannot see.

  Asked, in each language:
    * the head and every row have nine cells, and the tick column is no wider than 40px;
    * the hall (the scrolling .page) does not scroll sideways;
    * the bar is drawn, sits inside the hall, and each of its five buttons is inside the bar,
      overlaps no other button, and has its words whole;
    * the count line and the hidden-rows line are inside the bar.
  One screenshot per language of the bar and the top of the table, to the folder named by
  BAKA_SHOTS (or the notes folder's shots/B3).

  Prints one line per check and language, then TOTAL n, and exits non zero when n is not 0.

  node scripts/ui/mod_ticks_probe.js
*/
"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

const ROOT = process.env.BAKA_WEBUI ? path.resolve(process.env.BAKA_WEBUI) : path.resolve(__dirname, "..", "..", "ValheimBakaLoader", "WebUI");
const SHOTS = process.env.BAKA_SHOTS || path.resolve(__dirname, "..", "..", "..", "ValheimBakaLoader-132-notes", "shots", "B3");

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

/* Twelve rows: long and short names, one switched off, one the host cannot switch, and a search
   ("acme") that hides the two ticked rows by another author, so the bar carries its longest words. */
function plant() {
  try { modalClose(); } catch (_) { /* no dialog */ }
  document.querySelector('[data-page="mods"]').click();
  const mod = (author, name, version, extra) => Object.assign({
    FullName: author + "-" + name, ModName: name, Author: author, InstalledVersion: version, LatestVersion: version,
    UpdateAvailable: false, installedUtc: "2026-10-20T10:00:00Z", modUpdatedUtc: "2026-10-01T10:00:00Z",
    installedSource: "thunderstore", switchable: true, disabled: false,
  }, extra || {});
  const rows = [
    mod("Acme", "Aurora", "1.2.3"), mod("Acme", "Borealis", "2.0.0", { disabled: true }),
    mod("Acme", "CirrusWithAMuchLongerNameThanMost", "10.4.12"), mod("Acme", "Drift", "0.9.1"),
    mod("Acme", "Ember", "3.1.0", { UpdateAvailable: true, LatestVersion: "3.2.0" }), mod("Acme", "Frost", "1.0.0"),
    mod("Acme", "Gale", "4.4.4"), mod("Acme", "Hearthstone", "5.0.0"), mod("Acme", "Ironwood", "6.1.2"),
    mod("Zephyr", "Juniper", "7.0.0"), mod("Zephyr", "Kestrel", "8.8.8"), mod("Acme", "Patcher", "1.0.0", { switchable: false }),
  ];
  S.mods = rows; S.modsScanned = true; S.modsScanning = false; S.modsUpdating = false; S.modRowStatus = {};
  if (S.modTicks && S.modTicks.clear) {
    S.modTicks.clear();
    ["Acme-Aurora", "Acme-Borealis", "Zephyr-Juniper", "Zephyr-Kestrel"].forEach(k => S.modTicks.add(k));
  }
  S.modFilter = "acme";
  const box = document.getElementById("modSearch"); if (box) box.value = "acme";
  renderMods();
}

function measure(language) {
  const out = [];
  const table = document.getElementById("modsTable");
  const head = table.querySelectorAll("thead tr > th").length;
  const rows = Array.from(document.querySelectorAll("#modTable tr[data-key]"));
  const bad = rows.filter(r => r.querySelectorAll(":scope > td").length !== 9).map(r => r.getAttribute("data-key"));
  out.push({ what: "nine cells", ok: head === 9 && rows.length > 0 && !bad.length,
    text: "head " + head + ", " + rows.length + " rows" + (bad.length ? ", wrong: " + bad.join(",") : "") });
  const tick = table.querySelector("thead th.modtick");
  const tw = tick ? Math.round(tick.getBoundingClientRect().width) : 0;
  out.push({ what: "tick column", ok: !!tick && tw > 0 && tw <= 40, text: tw + "px" });
  const boxes = rows.filter(r => r.querySelector("input[data-mod-tick]")).length;
  out.push({ what: "boxes", ok: boxes === rows.length - (rows.some(r => r.getAttribute("data-key") === "Acme-Patcher") ? 1 : 0),
    text: boxes + " of " + rows.length + " rows" });

  const page = document.getElementById("page-mods");
  out.push({ what: "hall sideways", ok: page.scrollWidth <= page.clientWidth + 1, text: page.scrollWidth + " in " + page.clientWidth });
  /* The card clips what runs past it, so the hall never scrolls: the table itself has to fit,
     or the Status column is cut at the card's edge. */
  const t = table.getBoundingClientRect(), c = table.parentElement.getBoundingClientRect();
  const widths = Array.from(table.querySelectorAll("thead th")).filter(th => th.getClientRects().length)
    .map(th => Math.round(th.getBoundingClientRect().width)).join("+");
  out.push({ what: "table in its card", ok: t.right <= c.right + 0.5,
    text: Math.round(t.width) + " in " + Math.round(c.width) + " (" + widths + ")" });

  const bar = document.getElementById("modTickBar");
  const shown = !!bar && bar.getClientRects().length > 0 && getComputedStyle(bar).display !== "none";
  out.push({ what: "bar drawn", ok: shown, text: shown ? Math.round(bar.getBoundingClientRect().height) + "px tall" : "not on screen" });
  if (shown) {
    const b = bar.getBoundingClientRect(), p = page.getBoundingClientRect();
    out.push({ what: "bar in the hall", ok: b.left >= p.left - 0.5 && b.right <= p.right + 0.5, text: Math.round(b.width) + "px" });
    const buttons = Array.from(bar.querySelectorAll("button"));
    out.push({ what: "five buttons", ok: buttons.length === 5, text: String(buttons.length) });
    const rects = buttons.map(x => x.getBoundingClientRect());
    buttons.forEach((x, i) => {
      const r = rects[i];
      const inside = r.left >= b.left - 0.5 && r.right <= b.right + 0.5 && r.top >= b.top - 0.5 && r.bottom <= b.bottom + 0.5;
      const cut = x.scrollWidth > x.clientWidth + 1;
      const overlap = rects.some((o, j) => j !== i && r.left < o.right - 0.5 && o.left < r.right - 0.5 && r.top < o.bottom - 0.5 && o.top < r.bottom - 0.5);
      out.push({ what: "button " + (x.getAttribute("data-mod-bulk") || i), ok: inside && !cut && !overlap,
        text: JSON.stringify(x.textContent) + (inside ? "" : " outside the bar") + (cut ? " cut" : "") + (overlap ? " overlaps" : "") });
    });
    for (const sel of ["#modTickCount", "#modTickHidden"]) {
      const el = bar.querySelector(sel);
      const r = el && el.getBoundingClientRect();
      const ok = !!el && !!el.textContent && r.left >= b.left - 0.5 && r.right <= b.right + 0.5;
      out.push({ what: sel, ok, text: el ? JSON.stringify(el.textContent) : "missing" });
    }
  }
  return out;
}

(async () => {
  const { server, port } = await serve();
  const { chromium } = playwright();
  const browser = await chromium.launch({ headless: true });
  let failures = 0;
  const shots = [];

  try {
    const page = await browser.newPage({ viewport: { width: 1024, height: 680 } });
    await page.addInitScript(STUB);
    await page.goto("http://127.0.0.1:" + port + "/index.html", { waitUntil: "load" });
    await page.waitForTimeout(700);
    const pseudo = JSON.parse(fs.readFileSync(path.join(ROOT, "i18n", "xx.json"), "utf8"));
    const say = (bad, line) => { console.log((bad ? "FAIL " : "ok   ") + line); if (bad) failures++; };

    for (const language of ["en", "xx"]) {
      if (language === "xx") await page.evaluate(cat => window.BakaPreview.setLanguage("xx", cat), pseudo);
      await page.evaluate(plant);
      await page.waitForTimeout(450);
      for (const r of await page.evaluate(measure, language))
        say(!r.ok, language.padEnd(3) + r.what.padEnd(18) + r.text + (r.ok ? "" : "  <<"));

      fs.mkdirSync(SHOTS, { recursive: true });
      const file = path.join(SHOTS, "b3_tick_bar_1024_" + language + ".png");
      const box = await page.evaluate(() => {
        const top = (document.getElementById("modTickBar") || document.getElementById("modsTable")).getBoundingClientRect();
        const t = document.getElementById("modsTable").getBoundingClientRect();
        const p = document.getElementById("page-mods").getBoundingClientRect();
        const y = Math.max(0, Math.min(top.top, t.top) - 8);
        return { x: Math.max(0, p.left), y, width: Math.min(window.innerWidth - p.left, p.width), height: Math.min(window.innerHeight - y, 330) };
      });
      await page.screenshot({ path: file, clip: box });
      shots.push(file);
    }

    console.log("");
    for (const s of shots) console.log("shot " + s);
    console.log("TOTAL " + failures + "  the Mods tick column and its bar fit at 1024x680, English and pseudo");
    process.exitCode = failures ? 1 : 0;
  } finally {
    await browser.close();
    server.close();
  }
})().catch(err => { console.error(err); process.exit(2); });
