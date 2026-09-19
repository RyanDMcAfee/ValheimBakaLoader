#!/usr/bin/env node
/*
  The unsaved marker on a field, measured on the real page.

  WHY THIS EXISTS
  A field with something unsaved in it wears a dot beside its label and a rule down its
  left edge. The first shape of that rule was stood in room the field made for it:
  .field.field-dirty{position:relative;padding-left:9px}. Padding is layout, so the
  label and the box both slid 9px right and the box lost 9px of its width the instant
  the first character was typed. Measured on the page: #fName sat at left 131 clean and
  at left 140 dirty, in the middle of typing into it. A marker that moves the thing it
  is marking is worse than no marker.

  A source gate cannot hold this. The rule is a handful of declarations and every one of
  them is spelled correctly whichever way it is written; what is wrong is what the
  browser then does with them, and the only way to know that is to ask the browser. So
  this drives the page: it lays the form out, measures every field on the Settings hall,
  puts something unsaved in one control, and measures again. Every field's left and
  width must be the same number twice, and so must its own box's.

  It fails on the pre-fix stylesheet. That is the point of it.

  HOW TO RUN
      node scripts/ui/dirty_marker_probe.js

  It serves ValheimBakaLoader/WebUI itself, on a port it picks, and closes both the
  server and the browser on the way out. Playwright is found through PLAYWRIGHT_PATH,
  through NODE_PATH, or at the usual global npm location. Exit code 0 is a clean sweep;
  1 is at least one field that moved or changed width when it was marked.
*/

"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

/* The interface folder this reads. BAKA_WEBUI points it somewhere else, which is how
   the gate was proved to go red: a copy of the folder with padding-left:9px put back on
   the marker fails every field at every width, and the folder in the repository passes.
   A gate that has never been seen to fail is not known to be a gate. */
const ROOT = path.resolve(process.env.BAKA_WEBUI
  || path.join(__dirname, "..", "..", "ValheimBakaLoader", "WebUI"));

/* The two window sizes that matter: a roomy one, and the smallest the window can be
   dragged to, which is Forms/BlendWindow.cs DesignMinWidth x DesignMinHeight. The
   narrow one is where a 9px shove costs the most, because the box it comes out of is
   the narrowest it ever gets. */
const SIZES = [{ width: 1440, height: 900 }, { width: 1024, height: 680 }];

/* Both catalogs. The pseudo one is where the words run longest, so it is where a marker
   that takes inline room in a label would be the one to push it onto a second line. */
const CATALOGS = ["en", "xx"];

/* One text box out of each shape the form is laid out in: the two column grid, the
   three column row the backup dials share, the modifiers section, and the two
   full-width path fields inside Directories. */
const PROBES = ["fName", "fPort", "fBackups", "fBackLong", "fMaxPlayers", "fServerExe", "fSaveDir"];

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

/* Lay the whole form out and declare it saved, so every field below has a rectangle to
   measure and nothing is marked before the probe marks it. The three folding sections
   are opened by hand: a field inside a shut one has no layout at all, and the two path
   fields are the widest shape on the hall. Nothing here reaches a path on disk: the
   preview seam takes the answers the app would have given and the browser never asks
   for one. */
async function layOut(page) {
  return page.evaluate(() => {
    goPage("world");
    for (const id of ["secWorldMods", "secRites", "secDirs"]) {
      const sec = document.getElementById(id);
      if (sec) sec.classList.add("open");
    }
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
    return !document.getElementById("worldUnsaved").classList.contains("on");
  });
}

/* Every field on the hall, by the id of the control inside it, with the numbers that
   are not allowed to move. The field's own box is measured AND the control's, because
   the defect showed in both: padding on the field shoved the label right and took the
   same 9px off the box's width. Rounded to a tenth of a px rather than compared raw, so
   a fractional layout is not read as a shove. */
async function measure(page) {
  return page.evaluate(() => {
    const round = n => Math.round(n * 10) / 10;
    const out = {};
    for (const field of document.querySelectorAll("#page-world .field")) {
      const control = field.querySelector("input,select");
      if (!control || !control.id) continue;
      const f = field.getBoundingClientRect();
      const c = control.getBoundingClientRect();
      const label = field.querySelector("label");
      out[control.id] = {
        left: round(f.left), width: round(f.width), height: round(f.height),
        cleft: round(c.left), cwidth: round(c.width),
        /* Whole px on purpose, unlike the rest. A label's own height wanders by a tenth
           of a px between two layouts of the same text, on fields nothing has been typed
           into, so a tenth here would report noise rather than the one thing this line is
           watching for: a label taking a whole second line. */
        lheight: label ? Math.round(label.getBoundingClientRect().height) : 0,
        dirty: field.classList.contains("field-dirty"),
      };
    }
    return out;
  });
}

/* Put something unsaved in one box, or take it back out again. The value is changed and
   an input event fired the way a keystroke would, so the page's own comparison against
   its snapshot is what decides the field is dirty rather than a class set by hand. */
async function type(page, id, suffix) {
  return page.evaluate(([which, add]) => {
    const box = document.getElementById(which);
    if (!box) return null;
    if (add) { box.dataset.probeHeld = box.value; box.value = box.value + "9"; }
    else { box.value = box.dataset.probeHeld || ""; delete box.dataset.probeHeld; }
    box.dispatchEvent(new Event("input", { bubbles: true }));
    return box.closest(".field").classList.contains("field-dirty");
  }, [id, suffix]);
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

      for (const size of SIZES) {
        await page.setViewportSize(size);
        const clean = await layOut(page);
        await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
        const before = await measure(page);

        for (const id of PROBES) {
          if (!before[id]) { console.log("FAIL  no field for #" + id); bad++; continue; }

          const marked = await type(page, id, true);
          await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
          const after = await measure(page);

          /* Every field on the hall, not only the one that was typed into: a shove that
             moved its neighbours would be the same defect wearing a different face. */
          const moved = [];
          let tallest = 0;
          for (const key in before) {
            const a = before[key], b = after[key];
            if (!b) { moved.push(key + " vanished"); continue; }
            if (a.left !== b.left) moved.push(key + " left " + a.left + " to " + b.left);
            if (a.width !== b.width) moved.push(key + " width " + a.width + " to " + b.width);
            if (a.cleft !== b.cleft) moved.push(key + " box left " + a.cleft + " to " + b.cleft);
            if (a.cwidth !== b.cwidth) moved.push(key + " box width " + a.cwidth + " to " + b.cwidth);
            /* The label's height is reported rather than asserted. The dot is drawn
               after the label's own text, so a label already one character short of
               wrapping would take a second line for it, which is a real shove even
               though it is not a horizontal one. Nothing has ever been seen to do it,
               and this line is how it would be seen. */
            if (a.lheight !== b.lheight) tallest++;
          }

          const ok = marked === true && moved.length === 0;
          if (!ok) bad++;
          console.log(
            (ok ? "ok   " : "FAIL ") + code + " " + String(size.width).padStart(5) + "x" + size.height +
            "  #" + id.padEnd(12) +
            "  marked=" + (marked === true ? "yes" : "NO ") +
            "  moved=" + String(moved.length).padStart(2) +
            "  labels re-wrapped=" + tallest);
          for (const what of moved.slice(0, 6)) console.log("        " + what);

          await type(page, id, false);
          await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
        }

        if (clean !== true) { console.log("  note: the hall did not start clean at " + size.width); }
      }
    }
  } finally {
    await browser.close();
    server.close();
  }

  console.log(bad === 0
    ? "\nTOTAL 0  marking a field costs it no layout"
    : "\nTOTAL " + bad + "  marking a field moves it");
  process.exit(bad === 0 ? 0 : 1);
})().catch(e => { console.error(e); process.exit(2); });
