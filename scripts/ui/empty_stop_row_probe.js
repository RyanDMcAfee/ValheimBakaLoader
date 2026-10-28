#!/usr/bin/env node
/*
  The empty-server rows in Advanced, measured in a real browser at the 1024x680 minimum.

  WHY THIS EXISTS. 1.3.1 adds Stop when empty and its Windows half beside Restart when empty.
  Each sits in a togglerow 34 pixels tall with the switch on its right, and the pseudo catalog
  makes every sentence longer: a label that runs under its switch, or out of its row, reads as
  broken and hides the very switch it names. The selftest cannot see that; this measures it.

  Per row, in English and in the pseudo catalog, with the Advanced section open:
    * the label's text fits inside the label (nothing cut off sideways)
    * the label ends before the switch starts
    * the label stays inside its row, top and bottom
    * the field's own label (the line over the row) fits its field
  and the Windows row is drawn asleep while Stop is off and awake while it is on.

  Prints one line per row and language, then TOTAL n, and exits non zero when n is not 0.

  node scripts/ui/empty_stop_row_probe.js
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

const ROWS = ["tEmpty", "tEmptyStop", "tEmptyStopWin"];

function measure(ids) {
  const box = el => el.getBoundingClientRect();
  return ids.map(id => {
    const sw = document.getElementById(id);
    if (!sw) return { id, found: false };
    const row = sw.closest(".togglerow"), field = sw.closest(".field");
    const tl = row && row.querySelector(".tl");
    const lab = field && field.querySelector("label");
    const r = box(row), t = box(tl), s = box(sw), f = box(field), l = box(lab);
    return {
      id, found: true,
      text: (tl.textContent || "").trim(),
      fieldText: (lab.textContent || "").trim(),
      clipped: tl.scrollWidth > tl.clientWidth + 1,
      intoSwitch: t.right > s.left + 0.5,
      outOfRow: t.top < r.top - 0.5 || t.bottom > r.bottom + 0.5 || t.left < r.left - 0.5,
      fieldClipped: lab.scrollWidth > lab.clientWidth + 1 || l.right > f.right + 0.5,
      gated: row.classList.contains("gated"),
      rowWidth: Math.round(r.width),
    };
  });
}

(async () => {
  const { server, port } = await serve();
  const { chromium } = playwright();
  const browser = await chromium.launch({ headless: true });
  let failures = 0;

  try {
    const page = await browser.newPage({ viewport: { width: 1024, height: 680 } });
    await page.addInitScript(STUB);
    await page.goto("http://127.0.0.1:" + port + "/index.html", { waitUntil: "load" });
    await page.waitForTimeout(700);

    const pseudo = JSON.parse(fs.readFileSync(path.join(ROOT, "i18n", "xx.json"), "utf8"));

    for (const language of ["en", "xx"]) {
      if (language === "xx")
        await page.evaluate(cat => window.BakaPreview.setLanguage("xx", cat), pseudo);
      await page.evaluate(() => {
        try { modalClose(); } catch (_) { /* no dialog */ }
        document.querySelector('[data-page="world"]').click();
        const sec = document.getElementById("secRites");
        if (sec && sec.getAttribute("aria-expanded") !== "true") sec.click();
      });
      await page.waitForTimeout(450);

      for (const stopOn of [false, true]) {
        await page.evaluate(on => {
          renderEmptyTimers({ EmptyServerRestart: !on, EmptyServerStop: on, EmptyServerStopShutsDownWindows: true });
          syncAdvGates();
        }, stopOn);
        await page.waitForTimeout(260);

        const seen = await page.evaluate(measure, ROWS);
        for (const row of seen) {
          const problems = [];
          if (!row.found) problems.push("no row was drawn");
          else {
            if (row.clipped) problems.push("the label is cut off");
            if (row.intoSwitch) problems.push("the label runs under the switch");
            if (row.outOfRow) problems.push("the label leaves its row");
            if (row.fieldClipped) problems.push("the line over the row is cut off");
            if (row.id === "tEmptyStopWin" && row.gated === stopOn)
              problems.push(stopOn ? "the Windows row sleeps with Stop on" : "the Windows row is awake with Stop off");
          }
          const head = (problems.length ? "FAIL " : "ok   ") + language.padEnd(3) +
            ("stop " + (stopOn ? "on " : "off")).padEnd(9) + row.id.padEnd(15) +
            "row " + row.rowWidth + "px  " + JSON.stringify(row.text);
          console.log(problems.length ? head + "  << " + problems.join("; ") : head);
          if (problems.length) failures++;
        }
      }
    }

    console.log("");
    console.log("TOTAL " + failures + "  every empty-server row fits its switch and its row at 1024x680, English and pseudo");
    process.exitCode = failures ? 1 : 0;
  } finally {
    await browser.close();
    server.close();
  }
})().catch(err => { console.error(err); process.exit(2); });
