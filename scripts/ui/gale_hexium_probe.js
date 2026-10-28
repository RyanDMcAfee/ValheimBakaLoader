#!/usr/bin/env node
/*
  Issue 30, measured in a real browser at the 1024x680 minimum, in English and in the pseudo
  catalog.

  WHY THIS EXISTS. The screenshot on issue 30 is a red "Failed: ..." pill running out of the
  Mods table's status column, and 1.3.1 puts new tags and a button into the Gale review's rows.
  Both are about where the browser actually puts a box, which the node selftest cannot see.

  Asked:
    * Mods hall, a row whose Gale walk failed with a 200 character sentence: the pill says the
      short words, carries the whole sentence in its tooltip, and its right edge stays inside
      its cell; in English the table does not run past the hall (in the pseudo catalog the
      table's own head already does at this width, pill or no pill).
    * Gale review, one row per Hexium state (on Hexium, another version, not on Thunderstore
      three ways, and a removed build): each tag and each row button stays inside its cell,
      the button's words are not cut, and the dialog does not scroll sideways.
  One screenshot, the Mods table in English (the pseudo table's status column is past the
  hall's edge at this width), goes to the folder named by BAKA_SHOTS (or the notes folder's
  shots/Q2).

  Prints one line per check and language, then TOTAL n, and exits non zero when n is not 0.

  node scripts/ui/gale_hexium_probe.js
*/
"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..", "..", "ValheimBakaLoader", "WebUI");
const SHOTS = process.env.BAKA_SHOTS || path.resolve(__dirname, "..", "..", "..", "ValheimBakaLoader-131-notes", "shots", "Q2");

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

const LONG = ("Owner-Package 1.2.3: Thunderstore has no package by this name; Gale on the players' machines may still carry it. "
  + "The rest of this sentence is here only to make it two hundred characters long.").slice(0, 200);

function plantMods(long) {
  try { modalClose(); } catch (_) { /* no dialog */ }
  document.querySelector('[data-page="mods"]').click();
  const mod = (author, name, version) => ({
    FullName: author + "-" + name, ModName: name, Author: author, InstalledVersion: version, LatestVersion: version,
    UpdateAvailable: false, installedUtc: "2026-10-20T10:00:00Z", modUpdatedUtc: "2026-10-01T10:00:00Z",
  });
  S.mods = [mod("Owner", "Package", "1.2.3"), mod("Owner", "Second", "2.0.0"), mod("Another", "LongerModNameHere", "10.4.12")];
  S.modsScanning = false; S.modsUpdating = false;
  S.modRowStatus = { "Owner-Package": { phase: "failed", error: long } };
  renderMods();
}

function measurePill() {
  const tr = document.querySelector('#modTable tr[data-key="Owner-Package"]');
  if (!tr) return { found: false };
  const td = tr.querySelector("td.modstat"), pill = td && td.querySelector(".pill.rowfail");
  if (!pill) return { found: false };
  const p = pill.getBoundingClientRect(), c = td.getBoundingClientRect();
  const table = document.getElementById("modsTable"), wrap = table.parentElement;
  return {
    found: true,
    text: pill.textContent, title: pill.getAttribute("title"),
    outOfCell: p.right > c.right + 0.5 || p.left < c.left - 0.5,
    tableWider: table.getBoundingClientRect().right > wrap.getBoundingClientRect().right + 0.5,
    pillWidth: Math.round(p.width), cellWidth: Math.round(c.width),
  };
}

function plantGale() {
  try { modalClose(); } catch (_) { /* no dialog */ }
  const row = (name, version, hexium, goneKind) => ({ name, kind: "add", untickedFrom: null, profileVersion: version, serverVersion: null,
    downgrade: false, ticked: false, notAvailable: true, hexium, goneKind });
  GALE = { profile: S.profileName, following: true, id: "ab12CD", feed: "gale", lastApproved: null, unticked: [], heldBy: null,
    check: { seq: 1, at: "2026-10-27T10:00:00Z", ok: true, statusId: null, updatedAt: "2026-10-26T09:00:00Z", due: true,
      addCount: 1, updateCount: 0, skipped: 0, onlyHereCount: 0, configChanges: 0,
      rows: [
        { name: "Owner-Plain", kind: "add", untickedFrom: null, profileVersion: "1.0.0", serverVersion: null, downgrade: false, ticked: true },
        row("Owner-OnlyOnHexiumPackage", "1.2.3", "exact", "noPackage"),
        row("Owner-OtherVersionsPackage", "12.20.300", "other", "noPackage"),
        row("Owner-NowhereAtAll", "3.0.0", "none", "noPackage"),
        row("Owner-IndexWasDown", "4.0.0", "unreachable", "noPackage"),
        row("Owner-SwitchIsOff", "5.0.0", "off", "noPackage"),
        row("Owner-TakenAway", "6.1.0", "none", "noVersion"),
      ] } };
  galeDialog();
}

function measureGale() {
  const m = document.querySelector("#modalBg .modal");
  if (!m) return [{ what: "dialog", found: false }];
  const out = [];
  const inside = (el, td) => {
    const e = el.getBoundingClientRect(), c = td.getBoundingClientRect();
    return e.right <= c.right + 0.5 && e.left >= c.left - 0.5;
  };
  for (const tag of m.querySelectorAll(".galetbl .galegone")) {
    const td = tag.closest("td");
    const name = tag.closest("tr").querySelector("td.mono").firstChild.textContent;
    out.push({ what: "tag " + name, found: true, text: tag.textContent, ok: inside(tag, td), cut: false });
  }
  for (const btn of m.querySelectorAll(".galetbl .galehexbtn .btn")) {
    const td = btn.closest("td");
    const name = btn.closest("tr").querySelector("td.mono").firstChild.textContent;
    out.push({ what: "button " + name, found: true, text: btn.textContent, ok: inside(btn, td), cut: btn.scrollWidth > btn.clientWidth + 1 });
  }
  out.push({ what: "dialog width", found: true, text: Math.round(m.getBoundingClientRect().width) + "px",
    ok: m.scrollWidth <= m.clientWidth + 1 && m.getBoundingClientRect().right <= window.innerWidth, cut: false });
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
      const short = await page.evaluate(() => T("mods.row.failed.short"));

      await page.evaluate(plantMods, LONG);
      await page.waitForTimeout(450);
      const pill = await page.evaluate(measurePill);
      const problems = [];
      if (!pill.found) problems.push("no failed pill was drawn");
      else {
        if (pill.text !== short) problems.push("the pill says " + JSON.stringify(pill.text) + " rather than the short words");
        if (pill.title !== LONG) problems.push("the tooltip is not the whole sentence");
        if (pill.outOfCell) problems.push("the pill runs out of its cell");
        /* English only: in the pseudo catalog the table's own head runs past the hall at this
           width with no pill at all (1.3.0 and before), so the pill cannot be asked to fix it. */
        if (pill.tableWider && language === "en") problems.push("the table runs past the hall");
      }
      say(problems.length, language.padEnd(3) + "mods row pill  " + (pill.found ? "pill " + pill.pillWidth + "px in a " + pill.cellWidth + "px cell  " + JSON.stringify(pill.text) : "")
        + (problems.length ? "  << " + problems.join("; ") : ""));
      if (language === "en") {
        fs.mkdirSync(SHOTS, { recursive: true });
        const file = path.join(SHOTS, "q2_failed_pill_1024.png");
        const box = await page.evaluate(() => {
          const r = document.getElementById("modsTable").getBoundingClientRect();
          return { x: Math.max(0, r.left - 8), y: Math.max(0, r.top - 8), width: Math.min(window.innerWidth, r.width + 16), height: Math.min(260, r.height + 16) };
        });
        await page.screenshot({ path: file, clip: box });
        shots.push(file);
      }

      await page.evaluate(plantGale);
      await page.waitForTimeout(450);
      for (const r of await page.evaluate(measureGale)) {
        const bad = !r.found || !r.ok || r.cut;
        say(bad, language.padEnd(3) + "gale review    " + r.what.padEnd(36) + JSON.stringify(r.text || "")
          + (bad ? "  << " + (!r.found ? "not drawn" : r.cut ? "its words are cut" : "it leaves its cell") : ""));
      }
      await page.evaluate(() => { try { modalClose(); } catch (_) { /* none */ } });
      await page.waitForTimeout(300);
    }

    console.log("");
    for (const s of shots) console.log("shot " + s);
    console.log("TOTAL " + failures + "  the Failed pill and the Gale review's Hexium tags and buttons fit at 1024x680, English and pseudo");
    process.exitCode = failures ? 1 : 0;
  } finally {
    await browser.close();
    server.close();
  }
})().catch(err => { console.error(err); process.exit(2); });
