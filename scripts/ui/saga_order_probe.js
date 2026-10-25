#!/usr/bin/env node
/*
  The Log turned over (1.3.0, issue 27), asked of the real page in headless chromium.

  WHY THIS EXISTS. saga_order_selftest.js proves the rules over a stand-in console that does the
  layout arithmetic itself. What only a browser can answer is whether the page really does it:
  whether a paused reader's line stays put to the pixel while lines go in above it (the browser's
  own scroll anchoring would move it a second time, which is why .term turns it off), whether the
  chip's slide back to the top arrives and leaves the console live, whether a console that was
  folded while lines arrived opens on its newest line (before 1.3.0 it opened on its oldest and
  said it was streaming), and whether the head row, which gained a Pause button, still fits (one
  line in English; in the pseudo catalog the search, Copy and Pause may take a second line as one
  group at the right), spills no control's words over its border, and does not shift when Pause
  flips.

  Asked at 1024x680 and 1408x800, in the browser preview (no host) served from WebUI over a local
  port. The preview's chatter (a line every two seconds) is held off so every line is one this
  probe wrote. With --shots <folder> it also writes the screenshots the batch reports.

  Prints one line per question, then TOTAL n. Exits non zero when n is not 0.

  node scripts/ui/saga_order_probe.js [--shots <folder>]
  BAKA_WEBUI=<another WebUI folder> node scripts/ui/saga_order_probe.js
*/
"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(process.env.BAKA_WEBUI || path.join(__dirname, "..", "..", "ValheimBakaLoader", "WebUI"));
const SHOTS = (() => { const i = process.argv.indexOf("--shots"); return i > 0 ? process.argv[i + 1] : null; })();
const TYPES = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8", ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml", ".png": "image/png", ".woff2": "font/woff2",
};

function playwright() {
  const tries = [process.env.PLAYWRIGHT_PATH, "playwright",
    path.join(process.env.APPDATA || "", "npm", "node_modules", "playwright")].filter(Boolean);
  for (const where of tries) { try { return require(where); } catch (_) { /* next */ } }
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

let failures = 0;
const say = (ok, name, said) => {
  console.log((ok ? "ok   " : "FAIL ") + (name.length < 64 ? name.padEnd(64) : name + "  ") + said);
  if (!ok) failures++;
};

/* In the page: host rows the way the bridge hands them over, with the host's day. */
const ADD = `(n, from, day) => {
  for (let i = 0; i < n; i++) {
    const k = from + i, ss = String(k % 60).padStart(2, "0"), mm = String(Math.floor(k / 60) % 60).padStart(2, "0");
    logRaw("[12:" + mm + ":" + ss + ".000] probe line " + k, undefined, day === undefined ? "2026-10-24" : day);
  }
}`;
const FRAMES = "() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))";

(async () => {
  const { server, port } = await serve();
  const { chromium } = playwright();
  const browser = await chromium.launch({ headless: true });
  const open = async (w, h, reduced) => {
    const page = await browser.newPage({ viewport: { width: w, height: h }, reducedMotion: reduced ? "reduce" : "no-preference" });
    page.on("pageerror", e => say(false, "the page ran without an error", e.message));
    /* the preview's chatter is the only two-second interval; every line here is the probe's own */
    await page.addInitScript(() => {
      const si = window.setInterval;
      window.setInterval = (f, ms, ...a) => (ms === 2000 ? 0 : si(f, ms, ...a));
    });
    await page.goto("http://127.0.0.1:" + port + "/index.html", { waitUntil: "load" });
    await page.waitForFunction(() => !!window.BakaPreview, null, { timeout: 15000 });
    await page.waitForTimeout(700);
    await page.evaluate(() => { try { modalClose(); } catch (_) {} });
    await page.evaluate(`window.__add = ${ADD}; window.__frames = ${FRAMES};`);
    return page;
  };
  const shot = async (page, name, clipSel) => {
    if (!SHOTS) return;
    fs.mkdirSync(SHOTS, { recursive: true });
    const file = path.join(SHOTS, name);
    if (clipSel) {
      /* A hall section is display:contents (its head sticks to the hall), so it has no box of
         its own: the shot takes in its children, the head and the body, as one. */
      const box = await page.evaluate(sel => {
        const parts = [...document.querySelector(sel).children].map(k => k.getBoundingClientRect()).filter(r => r.width && r.height);
        const x = Math.min(...parts.map(r => r.left)), y = Math.min(...parts.map(r => r.top));
        return { x, y, width: Math.max(...parts.map(r => r.right)) - x, height: Math.max(...parts.map(r => r.bottom)) - y };
      }, clipSel);
      await page.screenshot({ path: file, clip: box });
    } else await page.screenshot({ path: file });
    console.log("     shot " + file);
  };

  try {
    for (const [w, h] of [[1024, 680], [1408, 800]]) {
      const size = w + "x" + h;
      const page = await open(w, h);

      /* ---------- 1. folded while lines arrive, then opened: the newest line shows ---------- */
      const opened = await page.evaluate(async () => {
        __add(40, 0);
        goSection("saga");
        await new Promise(r => setTimeout(r, 700));
        const term = document.getElementById("term");
        const first = [...term.querySelectorAll(".ln")].find(l => l.style.display !== "none" && l.dataset.k !== "div");
        const hold = document.getElementById("termHold");
        return { top: term.scrollTop, first: first ? first.textContent : null, live: termPin,
                 button: hold.classList.contains("paused") ? "Live (paused)" : "Pause (live)" };
      });
      say(opened.top === 0 && opened.live && opened.button === "Pause (live)" && /^2026-10-24 12:00:39\s+probe line 39$/.test(opened.first || ""),
        size + " opened after lines arrived folded: the newest line on top",
        "scrollTop " + opened.top + ", first " + JSON.stringify(opened.first) + ", button " + opened.button);

      /* ---------- 2. the command line stands over the lines ---------- */
      const rows = await page.evaluate(() => {
        const r = s => document.querySelector(s).getBoundingClientRect();
        return { head: r("#sect-saga .termhead"), cmd: r("#sect-saga .termin"), term: r("#term") };
      });
      say(rows.head.bottom <= rows.cmd.top + 0.5 && rows.cmd.bottom <= rows.term.top + 0.5,
        size + " the command line sits between the head row and the lines",
        "head " + Math.round(rows.head.bottom) + ", command " + Math.round(rows.cmd.top) + "-" + Math.round(rows.cmd.bottom) + ", lines from " + Math.round(rows.term.top));

      /* ---------- 3. screenshot: ten lines newest first ---------- */
      await page.evaluate(async () => {
        const term = document.getElementById("term");
        term.querySelectorAll(".ln").forEach(l => l.remove());
        termRealmCount = null;
        const day = "2026-10-24";
        logRaw("[14:02:11.000] Game server connected", undefined, day);
        logRaw("[14:02:12.000] [Info   :   BepInEx] 63 plugins loaded", undefined, day);
        logRaw("[14:02:15.000] Got connection SteamID 76561198000000001", undefined, day);
        logRaw("[14:03:02.000] [Warning:   BepInEx] a plugin is slow to answer", undefined, day);
        logRaw("[14:03:40.000] NullReferenceException: Object reference not set to an instance of an object", undefined, day);
        logRaw("[14:03:40.001]   at Example.Patch.Postfix () [0x00012] in <00000000>:0", undefined, day);
        logRaw("[14:04:00.000] World save (5/5) done. Total time [612ms]", undefined, day);
        logRaw("[14:04:31.000] Connections 2 ZDOS:412882  sent:41 recv:38", undefined, day);
        logLine("cmd", "> save");
        logLine("ok", "World saved ( Final_Sunset.db )  8.44 MB  in 205 ms");
        sagaToTop();
        await __frames();
      });
      await page.waitForTimeout(450);
      await shot(page, "log_ten_newest_first_" + size + ".png", "#sect-saga");
      if (w === 1024) await shot(page, "log_ten_newest_first_window_" + size + ".png");

      /* ---------- 4. paused by scrolling: the line being read holds still ---------- */
      const held = await page.evaluate(async () => {
        __add(160, 100);
        await __frames();
        const term = document.getElementById("term");
        term.scrollTop = 320;
        await __frames();
        const tr = term.getBoundingClientRect();
        const pick = () => {
          const el = document.elementFromPoint(tr.left + 40, tr.top + 70);
          return el && el.closest ? el.closest(".ln") : null;
        };
        const ref = pick();
        const before = ref.getBoundingClientRect().top;
        const live = termPin;
        __add(5, 400);                     /* one burst, one task */
        await __frames();
        const afterBurst = ref.getBoundingClientRect().top;
        for (let i = 0; i < 8; i++) {      /* and a trickle, one line a frame */
          __add(1, 500 + i);
          await new Promise(r => requestAnimationFrame(r));
        }
        await __frames();
        await new Promise(r => setTimeout(r, 300));
        const chip = document.getElementById("termNew"), cs = getComputedStyle(chip);
        return { live, before, afterBurst, afterTrickle: ref.getBoundingClientRect().top, sameLine: pick() === ref,
                 unseen: termUnseen, chip: chip.textContent, chipShown: cs.visibility === "visible" && Number(cs.opacity) > 0.95,
                 top: term.scrollTop };
      });
      say(!held.live && Math.abs(held.afterBurst - held.before) < 1 && Math.abs(held.afterTrickle - held.before) < 1 && held.sameLine,
        size + " paused, 13 lines in above: the line being read does not move",
        "moved " + (held.afterBurst - held.before).toFixed(2) + " px after a burst of 5, "
        + (held.afterTrickle - held.before).toFixed(2) + " px after 8 more one a frame (scrollTop now " + Math.round(held.top) + ")");
      say(held.chipShown && held.unseen === 13 && /^↑ 13 new lines, back to the top$/.test(held.chip),
        size + " the chip counts them and points up", JSON.stringify(held.chip) + ", shown " + held.chipShown);
      await shot(page, "log_paused_chip_" + size + ".png", "#sect-saga");
      if (w === 1024) await shot(page, "log_paused_chip_window_" + size + ".png");

      /* ---------- 5. the chip slides back to the top and the console is live again ---------- */
      await page.click("#termNew");
      const mid = await page.evaluate(async () => { await new Promise(r => setTimeout(r, 60)); return document.getElementById("term").scrollTop; });
      await page.waitForTimeout(900);
      const back = await page.evaluate(() => {
        const chip = getComputedStyle(document.getElementById("termNew"));
        return { top: document.getElementById("term").scrollTop, live: termPin, going: termGoingTop, chip: chip.visibility };
      });
      say(back.top === 0 && back.live && !back.going && back.chip === "hidden" && mid > 0,
        size + " the chip slides the console to its top and it follows again",
        "60 ms in at " + Math.round(mid) + ", then " + back.top + ", live " + back.live + ", chip " + back.chip);

      /* ---------- 6. the head row: fits, spills nothing, and nothing shifts when Pause flips ---------- */
      /* English keeps the row on one line at both sizes. A long language may put the search, Copy
         and Pause on a second line under the filters, but only as one group at the right end;
         either way no control is cut off and no control's words run over its own border. */
      for (const code of ["en", "xx"]) {
        if (code !== "en") {
          const loaded = await page.evaluate(c => window.BakaPreview.loadLanguage(c), code);
          if (!loaded) { say(false, size + " " + code + " catalog loads", "it did not"); continue; }
          await page.waitForTimeout(300);
        }
        const row = await page.evaluate(async () => {
          const head = document.querySelector("#sect-saga .termhead");
          const tools = head.querySelector(".termtools");
          const controls = [...head.querySelectorAll(".fpill,#termSearch")].filter(k => k.getClientRects().length);
          const hr = head.getBoundingClientRect();
          const name = k => k.id || k.dataset.f;
          const clipped = controls.filter(k => { const r = k.getBoundingClientRect(); return r.right > hr.right + 0.5 || r.left < hr.left - 0.5; }).map(name);
          /* the words against the box they sit in (scrollWidth will not do: the pills' wider tap
             area, .fpill::before, reaches past them on purpose) */
          const spills = k => {
            const words = document.createRange();
            words.selectNodeContents([...k.children].find(c => getComputedStyle(c).visibility === "visible") || k);
            const w = words.getBoundingClientRect(), r = k.getBoundingClientRect(), cs = getComputedStyle(k);
            const left = r.left + parseFloat(cs.borderLeftWidth) + parseFloat(cs.paddingLeft);
            const right = r.right - parseFloat(cs.borderRightWidth) - parseFloat(cs.paddingRight);
            return w.left < left - 0.5 || w.right > right + 0.5;
          };
          const spilled = controls.filter(k => k.id !== "termSearch" && spills(k)).map(name);
          const mid = k => Math.round(k.getBoundingClientRect().top + k.getBoundingClientRect().height / 2);
          const lines = new Set(controls.map(mid)).size;
          const toolMids = new Set([...tools.children].map(mid)).size;
          const toolsRight = Math.abs(tools.getBoundingClientRect().right - (hr.right - parseFloat(getComputedStyle(head).paddingRight)));
          const at = () => controls.map(k => { const r = k.getBoundingClientRect(); return k.id === "termHold" ? r.width : r.left + r.width; });
          const before = at();
          document.getElementById("termHold").click();
          await new Promise(r => setTimeout(r, 300));
          const after = at();
          const pressed = document.getElementById("termHold").getAttribute("aria-pressed");
          const showing = [...document.querySelectorAll("#termHold>span")]
            .filter(s => getComputedStyle(s).visibility === "visible").map(s => s.className);
          document.getElementById("termHold").click();
          await new Promise(r => setTimeout(r, 300));
          return { overflow: head.scrollWidth - head.clientWidth, clipped, spilled, lines, toolMids, toolsRight,
                   shift: after.reduce((s, x, i) => s + Math.abs(x - before[i]), 0), pressed, showing, live: termPin };
        });
        if (code === "xx" && w === 1024) {
          /* the longer words make the hall scroll, and the section's head sticks over the top of
             it: bring the head row out from under it for the picture */
          await page.evaluate(() => document.querySelector("#sect-saga .termhead").scrollIntoView({ block: "center" }));
          await page.waitForTimeout(250);
          await shot(page, "log_head_row_xx_window_" + size + ".png");
        }
        const shape = code === "en" ? row.lines === 1 : row.lines <= 2 && row.toolMids === 1 && row.toolsRight < 1;
        say(row.overflow <= 0 && !row.clipped.length && !row.spilled.length && shape,
          size + " " + code + " the head row fits and every control keeps its words inside it",
          "overflow " + row.overflow + " px, clipped " + JSON.stringify(row.clipped) + ", spilled " + JSON.stringify(row.spilled) +
          ", " + row.lines + " line(s), search/Copy/Pause on " + row.toolMids + " line, " + row.toolsRight.toFixed(1) + " px from the right edge");
        say(row.shift < 0.5 && row.pressed === "true" && row.showing.join(" ") === "th-live" && row.live,
          size + " " + code + " Pause flips without moving anything beside it",
          "moved " + row.shift.toFixed(2) + " px, pressed " + row.pressed + ", showing " + row.showing.join(" ") + ", back to live " + row.live);
      }
      await page.evaluate(() => window.BakaPreview.loadLanguage("en"));
      await page.waitForTimeout(300);

      /* ---------- 7. Pause holds even at the top ---------- */
      const pause = await page.evaluate(async () => {
        const term = document.getElementById("term");
        sagaToTop();
        await __frames();
        document.getElementById("termHold").click();
        const tr = term.getBoundingClientRect();
        const ref = document.elementFromPoint(tr.left + 40, tr.top + 40).closest(".ln");
        const before = ref.getBoundingClientRect().top;
        __add(4, 700);
        await __frames();
        const after = ref.getBoundingClientRect().top;
        term.scrollTop = 0;
        await __frames();
        const stillPaused = !termPin;
        document.getElementById("termHold").click();
        await new Promise(r => setTimeout(r, 900));
        return { moved: after - before, stillPaused, live: termPin, top: term.scrollTop };
      });
      say(Math.abs(pause.moved) < 1 && pause.stillPaused && pause.live && pause.top === 0,
        size + " Pause holds the console at its top; Live follows again",
        "moved " + pause.moved.toFixed(2) + " px, scrolled to the top it stays paused " + pause.stillPaused + ", Live gives top " + pause.top);

      /* ---------- 8. folded and opened again while paused: back on the newest line ---------- */
      const again = await page.evaluate(async () => {
        const term = document.getElementById("term");
        term.scrollTop = 500;
        await __frames();
        sectSet("saga", false);
        await new Promise(r => setTimeout(r, 400));
        __add(6, 800);
        sectSet("saga", true, { land: true });
        await new Promise(r => setTimeout(r, 700));
        const first = [...term.querySelectorAll(".ln")].find(l => l.style.display !== "none" && l.dataset.k !== "div");
        return { top: term.scrollTop, live: termPin, first: first.textContent };
      });
      say(again.top === 0 && again.live && /probe line 805$/.test(again.first),
        size + " folded while paused, lines in, opened: the newest line on top",
        "scrollTop " + again.top + ", live " + again.live + ", first " + JSON.stringify(again.first));
      await page.close();
    }

    /* ---------- 9. reduced motion: the chip jumps rather than slides ---------- */
    {
      const page = await open(1024, 680, true);
      const r = await page.evaluate(async () => {
        goSection("saga");
        await new Promise(res => setTimeout(res, 500));
        __add(160, 0);
        const term = document.getElementById("term");
        term.scrollTop = 400;
        await __frames();
        __add(3, 200);
        await __frames();
        document.getElementById("termNew").click();
        const at = term.scrollTop;
        return { at, live: termPin };
      });
      say(r.at === 0 && r.live, "1024x680 reduced motion: the chip goes back to the top at once", "scrollTop right after the press " + r.at);
      await shot(page, "log_reduced_motion_1024x680.png", "#sect-saga");
      await page.close();
    }
  } finally {
    await browser.close();
    server.close();
  }
  console.log("TOTAL " + failures);
  process.exit(failures ? 1 : 0);
})().catch(e => { console.error(e); process.exit(2); });
