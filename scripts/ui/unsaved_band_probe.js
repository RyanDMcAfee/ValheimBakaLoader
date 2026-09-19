#!/usr/bin/env node
/*
  The band under the Settings hall, measured on the real page.

  WHY THIS EXISTS
  The unsaved notice stands at the bottom left of the Settings hall, outside the hall
  rather than inside it, and the hall gives up a band at the foot of its own scrolling
  area for as long as the notice is up. That much is structural and a source gate can
  hold it. How DEEP the band has to be cannot be held that way: it is the notice's own
  height, the notice is two sentences, sentences wrap, and the same two sentences take a
  line more in a language whose words run longer or in a window one step narrower.

  The first fix wrote the depth down as a number, --unsaved-band:70px, and the number was
  short. At 1408x880 in the pseudo catalog the notice stands about 75px tall, the hall
  gave up 70, and the last few px of the form went back behind an opaque notice. Same
  defect, same section, one catalog over, and every test in the suite stayed green
  because every one of them read the source rather than the page.

  So this drives the page. It opens the interface in a headless browser, puts something
  unsaved in the form, and asserts the one thing that matters at every width and in both
  catalogs: the hall's bottom edge never falls below the notice's top edge. Then it walks
  the hall's whole scroll range and asserts that no control inside it is standing under
  the notice, which is the same invariant said in the form the reader would notice.

  It fails on the pre-fix stylesheet. That is the point of it.

  HOW TO RUN
      node scripts/ui/unsaved_band_probe.js

  It serves ValheimBakaLoader/WebUI itself, on a port it picks, and closes both the
  server and the browser on the way out. Playwright is found through PLAYWRIGHT_PATH,
  through NODE_PATH, or at the usual global npm location. Exit code 0 is a clean sweep;
  1 is at least one width or catalog where something was standing behind the notice.
*/

"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

/* The interface folder this reads. BAKA_WEBUI points it somewhere else, which is how the
   gate was proved to go red: a copy of the folder with the measurement taken back out
   again fails every width, and the folder in the repository passes every width. A gate
   that has never been seen to fail is not known to be a gate. */
const ROOT = path.resolve(process.env.BAKA_WEBUI
  || path.join(__dirname, "..", "..", "ValheimBakaLoader", "WebUI"));

/* Both catalogs, and every width from the widest a window is likely to be down past the
   narrowest one it can be dragged to. The fixed band passed at some of these and failed
   at others, which is exactly why a single width proves nothing. */
const WIDTHS = [1600, 1408, 1280, 1100, 1024, 980, 900, 820, 760];
const CATALOGS = ["en", "xx"];
const SCROLL_STOPS = 9;

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
};

/* A static server over the interface folder and nothing else. Every path is resolved and
   then checked to be inside ROOT, so a request cannot walk out of it. */
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

/* Put something unsaved in the form, on the Settings hall. The box is emptied and the
   form declared saved in that state FIRST, so the typing that follows is a real change
   every time round rather than only the first: this runs once per width, in one page,
   and a box that already held the value would have been typed into and stayed clean.
   Nothing here reaches a path on disk: the preview seam takes the answers the app would
   have given and the browser never asks for one. */
async function makeDirty(page) {
  return page.evaluate(() => {
    goPage("world");
    window.BakaPreview.worldDirs({
      prefs: {
        EffectiveServerExePath: "D:\\Steam\\Valheim dedicated server\\valheim_server.exe",
        ServerExePathSource: "default",
        EffectiveSaveDataFolderPath: "D:\\Saves\\Valheim",
        SaveDataFolderPathSource: "default",
      },
      typed: { exe: "", dir: "" },
      snapshot: true,
    });
    const box = document.getElementById("fServerExe");
    box.value = "E:\\elsewhere\\valheim_server.exe";
    box.dispatchEvent(new Event("input", { bubbles: true }));
    return document.getElementById("worldUnsaved").classList.contains("on");
  });
}

/* The measurement, in two forms of the same question.
   The invariant is the hall's bottom edge against the notice's top: the hall is the
   thing that scrolls, so nothing it holds can be painted below its own bottom edge.
   The second form is what a reader would actually see. A grid of points is dropped over
   the notice and each one is asked what is UNDER it. The notice takes no pointer events,
   so the answer is whatever it is standing on, and if that is the hall or anything
   inside the hall then a piece of the form is behind an opaque panel. This is asked at
   every scroll position the hall can be read at, because the first shape of this defect
   only showed at some of them.
   Points rather than rectangles on purpose: a control scrolled past the fold still
   reports a rectangle down where it would have been, so comparing rectangles counts
   things that are clipped and invisible. What is painted is the question. */
async function measure(page) {
  return page.evaluate(stops => {
    const hallEl = document.getElementById("page-world");
    const noticeEl = document.getElementById("worldUnsaved");
    const hall = hallEl.getBoundingClientRect();
    const notice = noticeEl.getBoundingClientRect();

    const covered = [];
    const max = Math.max(0, hallEl.scrollHeight - hallEl.clientHeight);
    const held = hallEl.scrollTop;
    const seen = new Set();
    for (let i = 0; i < stops; i++) {
      hallEl.scrollTop = stops === 1 ? 0 : Math.round((max * i) / (stops - 1));
      for (let x = notice.left + 1; x < notice.right - 1; x += 12) {
        for (let y = notice.top + 1; y < notice.bottom - 1; y += 6) {
          const under = document.elementFromPoint(x, y);
          if (!under || !(under === hallEl || hallEl.contains(under))) continue;
          const who = under.id || under.className || under.tagName;
          const key = hallEl.scrollTop + "|" + who;
          if (seen.has(key)) continue;
          seen.add(key);
          covered.push({ at: hallEl.scrollTop, id: who });
        }
      }
    }
    hallEl.scrollTop = held;

    return {
      on: noticeEl.classList.contains("on"),
      room: hallEl.classList.contains("unsaved-room"),
      band: getComputedStyle(document.documentElement).getPropertyValue("--unsaved-band").trim(),
      noticeHeight: Math.round(notice.height),
      hallBottom: Math.round(hall.bottom),
      noticeTop: Math.round(notice.top),
      stops,
      covered,
    };
  }, SCROLL_STOPS);
}

(async () => {
  if (!fs.existsSync(path.join(ROOT, "index.html")))
    throw new Error("no index.html under " + ROOT + " (set BAKA_WEBUI to the interface folder)");

  const { server, port } = await serve();
  const { chromium } = playwright();
  const browser = await chromium.launch();
  let bad = 0;

  try {
    const page = await browser.newPage();
    page.on("pageerror", e => { console.log("  page error: " + e.message); bad++; });
    await page.goto("http://127.0.0.1:" + port + "/index.html", { waitUntil: "load" });
    /* Wait for the seam rather than for the load event alone, and say what is wrong in
       words if it never arrives. Without this, an interface folder that is not where
       this thought it was comes out as a TypeError on an undefined, three frames deep. */
    await page.waitForFunction(() => !!window.BakaPreview, null, { timeout: 15000 })
      .catch(() => { throw new Error("the interface never finished loading from " + ROOT); });

    for (const code of CATALOGS) {
      const loaded = await page.evaluate(c => window.BakaPreview.loadLanguage(c), code);
      if (!loaded) { console.log("FAIL  no catalog for " + code); bad++; continue; }

      for (const width of WIDTHS) {
        await page.setViewportSize({ width, height: 880 });
        await makeDirty(page);
        // One frame for the measurement to land and the band to be re-read.
        await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
        const m = await measure(page);

        const clear = m.hallBottom <= m.noticeTop;
        const ok = m.on && m.room && clear && m.covered.length === 0;
        if (!ok) bad++;
        console.log(
          (ok ? "ok   " : "FAIL ") + code + " " + String(width).padStart(5) + "x880" +
          "  band=" + (m.band || "(unset)").padStart(6) +
          "  notice=" + String(m.noticeHeight).padStart(3) + "px" +
          "  hallBottom=" + String(m.hallBottom).padStart(4) +
          "  noticeTop=" + String(m.noticeTop).padStart(4) +
          "  slack=" + String(m.noticeTop - m.hallBottom).padStart(3) +
          "  covered=" + m.covered.length);
        for (const c of m.covered.slice(0, 6)) console.log("        behind the notice at scrollTop " + c.at + ": " + c.id);
      }
    }
  } finally {
    await browser.close();
    server.close();
  }

  console.log(bad === 0
    ? "\nTOTAL 0  the hall never reaches under the notice"
    : "\nTOTAL " + bad + "  the hall reaches under the notice");
  process.exit(bad === 0 ? 0 : 1);
})().catch(e => { console.error(e); process.exit(2); });
