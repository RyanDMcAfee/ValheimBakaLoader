#!/usr/bin/env node
/*
  Every state the globe's menu can draw a row in, measured in a real browser.

  WHY THIS EXISTS. The menu was widened to 380px so the longest status line in it would
  stay on ONE line, and the probe that proved it drove five rows. The sixth and seventh,
  the two states with no pack behind them, were never among them, and one of those carries
  the longest sentence of the lot: "No pack published for this version yet". It came out
  over two lines on a row that also carried the machine tag, and nothing said so, because
  the invariant the width was chosen for was only ever asserted for the states somebody
  remembered to drive. This drives ALL of them, and it asks the same question of each.

  Three questions per row:
    * the status line fits on ONE line, measured against its OWN line-height rather than
      against a number typed here, so a type-scale change moves both sides together
    * the name fits on one line, same measurement
    * a row with nothing to press is not pressable: no menuitem role, no tab stop

  Prints one line per row, then TOTAL n, and exits non zero when n is not 0.

  node scripts/ui/lang_row_probe.js
*/
"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..", "..", "ValheimBakaLoader", "WebUI");

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

/* A host that answers nothing in particular. The menu is driven by writing LANG and
   calling the page's own renderer, which is what the host's answer does anyway. */
const STUB = `(() => {
  const listeners = [];
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    addEventListener: (k, fn) => { if (k === "message") listeners.push(fn); },
    postMessage: m => { if (m.id == null) return;
      setTimeout(() => listeners.forEach(fn => fn({ data: { id: m.id, ok: true, result: {} } })), 0); },
  };
  window.__push = (event, data) => listeners.forEach(fn => fn({ data: { event, data } }));
})()`;

/* One language per state the row line has a sentence for, all seven in one menu, so the
   measurement is made with the menu at the width it really opens at. The machine tag is
   on every row that can carry one: it is the 90 pixels that made the difference. */
const ROWS = [
  { state: "current", code: "en", nativeName: "English", builtIn: true, installed: true, matchesApp: true, available: false, status: "reviewed" },
  { state: "built_in", code: "xx", nativeName: "Pseudo", builtIn: true, installed: true, matchesApp: true, available: false, status: "reviewed" },
  { state: "installed", code: "zh-Hans", nativeName: "简体中文", installed: true, installedVersion: "1.2.0", matchesApp: true, available: true, bytes: 4100000, status: "machine" },
  { state: "older_pack", code: "ja", nativeName: "日本語", installed: true, installedVersion: "1.1.9", matchesApp: false, available: true, bytes: 4400000, status: "machine" },
  { state: "not_installed", code: "ru", nativeName: "Русский", installed: false, matchesApp: false, available: true, bytes: 2306867, status: "machine" },
  { state: "not_published", code: "zh-Hant", nativeName: "繁體中文", installed: false, matchesApp: false, available: false, bytes: 0, status: "machine" },
];

/* The offline state is the same row shape with the manifest unread, so it needs its own
   menu rather than its own row. */
const OFFLINE = { state: "offline", code: "ko", nativeName: "한국어", installed: false, matchesApp: false, available: false, bytes: 0, status: "machine" };

async function measure(page, rows, manifestOk) {
  return page.evaluate(([rows, manifestOk]) => {
    LANG.list = {
      current: "en", appVersion: "1.2.0", checkEnabled: true, busy: false,
      languages: rows,
      manifest: { ok: manifestOk, fromCache: false, errorId: null },
    };
    LANG.busyCode = null;
    LANG.failed = null;
    renderLangMenu();
    const menu = document.getElementById("langMenu");
    menu.classList.add("open");

    const lines = el => {
      if (!el) return { lines: 0, height: 0, lineHeight: 0, text: "" };
      const cs = getComputedStyle(el);
      const lh = parseFloat(cs.lineHeight) || parseFloat(cs.fontSize) * 1.2;
      const h = el.getBoundingClientRect().height;
      return { lines: Math.round(h / lh), height: Math.round(h * 10) / 10, lineHeight: Math.round(lh * 10) / 10, text: (el.textContent || "").trim() };
    };

    return rows.map(r => {
      const el = menu.querySelector('[data-lang-row="' + r.code + '"], [data-lang-inert="' + r.code + '"]');
      return {
        state: r.state,
        code: r.code,
        found: !!el,
        role: el ? el.getAttribute("role") : null,
        tabindex: el ? el.getAttribute("tabindex") : null,
        inert: el ? el.hasAttribute("data-lang-inert") : false,
        sub: lines(el && el.querySelector(".lm-sub")),
        name: lines(el && el.querySelector(".lm-name")),
        menuWidth: Math.round(menu.getBoundingClientRect().width),
      };
    });
  }, [rows, manifestOk]);
}

(async () => {
  const { server, port } = await serve();
  const { chromium } = playwright();
  const browser = await chromium.launch({ headless: true });
  let failures = 0;

  try {
    const page = await browser.newPage({ viewport: { width: 1408, height: 800 } });
    await page.addInitScript(STUB);
    await page.goto("http://127.0.0.1:" + port + "/index.html", { waitUntil: "load" });
    await page.waitForTimeout(700);

    const seen = (await measure(page, ROWS, true))
      .concat(await measure(page, [OFFLINE], false));

    for (const row of seen) {
      const problems = [];
      if (!row.found) problems.push("no row was drawn");
      if (row.sub.lines > 1) problems.push("the status line wrapped onto " + row.sub.lines + " lines");
      if (row.name.lines > 1) problems.push("the name wrapped onto " + row.name.lines + " lines");
      /* The two states with no pack behind them are reasons, not controls. */
      const shouldPress = row.state !== "not_published" && row.state !== "offline";
      if (shouldPress && row.role !== "menuitem") problems.push("a pressable row is not a menuitem");
      if (shouldPress && row.tabindex !== "0") problems.push("a pressable row is not a tab stop");
      if (!shouldPress && row.role) problems.push("a row with nothing to press claims role=" + row.role);
      if (!shouldPress && row.tabindex) problems.push("a row with nothing to press is a tab stop");

      const head = (problems.length ? "FAIL " : "ok   ") +
        row.state.padEnd(14) + row.code.padEnd(9) +
        "sub " + String(row.sub.height).padStart(5) + "px /" + String(row.sub.lineHeight).padStart(6) + "px" +
        "  menu " + row.menuWidth + "px";
      console.log(problems.length ? head + "  << " + problems.join("; ") + "  << " + row.sub.text : head);
      if (problems.length) failures++;
    }

    console.log("");
    console.log("TOTAL " + failures + "  every row state on one line, and only a row with a pack is pressable");
    process.exitCode = failures ? 1 : 0;
  } finally {
    await browser.close();
    server.close();
  }
})().catch(err => { console.error(err); process.exit(2); });
