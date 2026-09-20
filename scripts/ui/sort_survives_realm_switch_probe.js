#!/usr/bin/env node
/*
  A sortable header has to keep working for the whole life of the window.

  WHY THIS EXISTS (issue 14). Both sortable tables - the Mods hall (index.html:366-369) and
  the player roster (index.html:331-338) - are wired ONCE while app.js is being evaluated, by
  wireSort. Until 1.2.1 wireSort was handed the state OBJECT rather than a way to find it,
  and switchServer replaced both objects on every realm switch. From the first switch on,
  every header click mutated an orphan while sortedMods and sortedPlayers kept reading the
  live one, so all TWELVE headers stopped sorting and nothing but a reload cured it. The
  header still glowed, because that glow is CSS with nothing behind it (app.css:757), which
  is why nobody caught it by looking.

  WHAT IS ASKED. Three questions per column, on a freshly loaded window and again after
  every event that has ever redrawn one of these tables:
    * clicking the header moves the rows
    * the header that was clicked is marked (.sorted, a sortmark, aria-sort)
    * the state the renderer reads is the state the click wrote

  The answer after an event has to be the SAME as the answer on a fresh window. A column
  whose first click happens to leave this particular list where it already was (Status over
  a list that is already in status order) then proves nothing either way instead of reading
  as a failure it is not.

  THE EVENTS, in order, all twelve columns re-asked after each one:
    a realm switch · there and back again · a scan · a mod added · a mod removed ·
    a language switch

  All twelve columns, not the eight that fit a narrow window: the roster's Deaths and
  Position headers are hidden by the stylesheet below 1293px and 1161px, so the window is
  opened wide enough to carry them.

  It FAILS on 1.2.0 (709f241) and passes on 1.2.1. To run it against 1.2.0, put that
  revision's WebUI somewhere and point BAKA_WEBUI at it:
    git show 709f241:ValheimBakaLoader/WebUI/app.js > <copy>/app.js
    BAKA_WEBUI=<copy> node scripts/ui/sort_survives_realm_switch_probe.js

  Prints one line per question, then TOTAL n. Exits non zero when n is not 0.

  node scripts/ui/sort_survives_realm_switch_probe.js
*/
"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

const ROOT = process.env.BAKA_WEBUI ||
  path.resolve(__dirname, "..", "..", "ValheimBakaLoader", "WebUI");
const SHOTS = process.env.BAKA_SHOTS || "";

const TYPES = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8", ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml", ".png": "image/png", ".woff2": "font/woff2",
};

function playwright() {
  const tries = ["playwright", path.join(process.env.APPDATA || "", "npm", "node_modules", "playwright")];
  for (const where of tries) { try { return require(where); } catch (_) { /* next */ } }
  throw new Error("playwright was not found");
}

function serve() {
  return new Promise(resolve => {
    const base = path.resolve(ROOT);
    const server = http.createServer((req, res) => {
      const asked = decodeURIComponent((req.url || "/").split("?")[0]);
      const file = path.resolve(base, "." + (asked === "/" ? "/index.html" : asked));
      if (!file.startsWith(base)) { res.writeHead(403).end(); return; }
      fs.readFile(file, (err, body) => {
        if (err) { res.writeHead(404).end(); return; }
        res.writeHead(200, { "content-type": TYPES[path.extname(file).toLowerCase()] || "application/octet-stream" });
        res.end(body);
      });
    });
    server.listen(0, "127.0.0.1", () => resolve({ server, port: server.address().port }));
  });
}

/* The preview has no host, so switchServer would give up at app.js:1848 before it reached
   the lines this is about. The stub answers profiles.get and refuses the rest with the
   page's own FAIL symbol, which is the answer every other caller in switchServer already
   copes with. Nothing else about the switch is changed. */
const STUB_RPC = `async () => {
  const FAILV = await rpc("__probe.capture_fail__");
  window.rpc = (method, params) => method === "profiles.get"
    ? Promise.resolve({ ProfileName: (params && params.name) || "?", Name: (params && params.name) || "?",
                        WorldName: "Testbed", SaveInterval: 600, RconEnabled: true, RconPort: 25575, Port: 2456 })
    : Promise.resolve(FAILV);
  return typeof FAILV;
}`;

const TABLES = {
  mods: {
    page: "mods", head: "#page-mods th.sortable", th: "#page-mods th", body: "#modTable",
    cols: ["name", "installed", "latest", "status"], state: "modSort",
  },
  vikings: {
    page: "vikings", head: "#page-vikings th.sortable", th: "#page-vikings th", body: "#vikTable",
    cols: ["name", "status", "platform", "session", "playtime", "seen", "deaths", "pos"],
    state: "vikSort",
  },
};
const COLUMNS = Object.values(TABLES).reduce((n, t) => n + t.cols.length, 0);

const READ = `(t) => ({
  rows: Array.from(document.querySelectorAll(t.body + " tr")).map(tr =>
    tr.dataset.key || (tr.querySelector("td") || {}).textContent || "").map(s => String(s).replace(/\\s+/g, " ").trim()),
  marked: Array.from(document.querySelectorAll(t.head)).filter(th =>
    th.classList.contains("sorted") || (th.querySelector(".sortmark") || {}).textContent).map(th => th.dataset.sort),
  state: JSON.stringify(S[t.state]),
})`;

let failures = 0;
const say = (ok, name, said) => {
  console.log((ok ? "ok   " : "FAIL ") + name.padEnd(56) + said);
  if (!ok) failures++;
};

/* What one click on one header comes to, column by column, with the table put back into its
   own order after each (the third click is dir 0, app.js:3025). */
async function ask(page, t, when) {
  const read = () => page.evaluate(new Function("return " + READ)(), t);
  await page.evaluate(p => { modalClose(); document.querySelector('[data-page="' + p + '"]').click(); }, t.page);
  await page.waitForTimeout(250);
  await page.evaluate(() => modalClose());

  const out = {};
  for (const col of t.cols) {
    const before = await read();
    await page.evaluate(([h, c]) => document.querySelector(h + '[data-sort="' + c + '"]').click(), [t.th, col]);
    await page.waitForTimeout(120);
    const after = await read();
    out[col] = {
      moved: before.rows.join("|") !== after.rows.join("|"),
      marked: after.marked.includes(col),
      written: after.state.indexOf('"' + col + '"') >= 0,
      order: after.rows.join("|"),
    };
    await page.evaluate(([h, c]) => {
      const th = document.querySelector(h + '[data-sort="' + c + '"]');
      th.click(); th.click();
    }, [t.th, col]);
    await page.waitForTimeout(100);
  }
  if (SHOTS) {
    fs.mkdirSync(SHOTS, { recursive: true });
    await page.screenshot({ path: path.join(SHOTS, when.replace(/[^a-z0-9]+/gi, "-") + "-" + t.page + ".png") });
  }
  return out;
}

/* An event that added or removed a row changed what the table HOLDS, so the row order it
   comes out in is allowed to differ from the fresh window's: what may not differ is whether
   the click sorted at all. Everywhere else the two answers have to match outright. */
function judge(t, fresh, later, when, listChanged) {
  for (const col of t.cols) {
    const a = fresh[col], b = later[col];
    const sameOrder = listChanged || a.order === b.order;
    const same = sameOrder && a.moved === b.moved && a.marked === b.marked && a.written === b.written;
    say(same && b.marked && b.written, t.page + " / " + col + " after " + when,
      same ? (listChanged ? "sorts as a fresh window does" : "same as a fresh window")
           : "marked " + a.marked + "->" + b.marked + "  state written " + a.written + "->" + b.written +
             "  sorted " + a.moved + "->" + b.moved +
             "  order " + (a.order === b.order ? "same" : "DIFFERENT"));
  }
}

/* switchServer empties both lists (app.js:1850, 1853) and the app refills them from the
   host (refreshPlayers, scanMods). There is no host here, so the seed taken at the start is
   handed back, which is all those two calls do that sorting depends on. */
const RESEED = `() => {
  S.mods = window.__seed.mods.slice(); S.modsScanned = true; renderMods();
  S.players = window.__seed.players.slice(); S.journal = window.__seed.journal; renderPlayers();
}`;

async function reseed(page) { await page.evaluate(new Function("return " + RESEED)()); }

async function switchTo(page, realm) {
  await page.evaluate(r => document.querySelector('.srvchip[data-srv="' + r + '"]').click(), realm);
  await page.waitForTimeout(450);
  await reseed(page);
  await page.waitForTimeout(150);
  const where = await page.evaluate(() => S.profileName);
  if (where !== realm) throw new Error("the switch did not land: " + where);
}

(async () => {
  const { server, port } = await serve();
  const { chromium } = playwright();
  const browser = await chromium.launch({ headless: true });
  try {
    /* Wide enough that the stylesheet keeps Deaths (>1160px) and Position (>1292px) on
       screen, so all twelve headers are really asked rather than eight of them. */
    const page = await browser.newPage({ viewport: { width: 1420, height: 900 } });
    await page.goto("http://127.0.0.1:" + port + "/index.html", { waitUntil: "load" });
    await page.waitForTimeout(900);
    await page.evaluate(() => {
      modalClose();
      window.__seed = { mods: S.mods, players: S.players, journal: S.journal };
    });

    const fresh = {};
    for (const key of ["mods", "vikings"]) fresh[key] = await ask(page, TABLES[key], "fresh");

    const kind = await page.evaluate(new Function("return " + STUB_RPC)());
    if (kind !== "symbol") throw new Error("the page's FAIL symbol was not captured");

    /* Every event that redraws one of these tables, each one asked about in turn. */
    const EVENTS = [
      ["a realm switch", async () => { await switchTo(page, "Midgard Test"); }],
      ["there and back", async () => {
        await switchTo(page, "Final Sunset");
        await switchTo(page, "Midgard Test");
      }],
      ["a scan", async () => {
        await page.evaluate(() => { modalClose(); document.querySelector('[data-page="mods"]').click(); });
        await page.waitForTimeout(200);
        await page.evaluate(() => document.querySelector("#scanBtn").click());
        await page.waitForTimeout(250);
        await reseed(page);
      }],
      ["a mod added", async () => {
        /* A copy of the LAST row, appended. Every column then ranks it exactly where the
           row it was copied from already is, so whether a click sorts at all is the same
           question it was on a fresh window: the list is longer, not differently shaped. */
        await page.evaluate(() => {
          const last = S.mods[S.mods.length - 1];
          S.mods = S.mods.concat([Object.assign({}, last, {
            ModName: "ZzzProbeMod", FullName: "probe-ZzzProbeMod",
          })]);
          renderMods();
        });
        await page.waitForTimeout(150);
      }, true],
      ["a mod removed", async () => {
        await page.evaluate(() => {
          S.mods = S.mods.filter(m => m.FullName !== "probe-ZzzProbeMod");
          renderMods();
        });
        await page.waitForTimeout(150);
      }],
      ["a language switch", async () => {
        const ok = await page.evaluate(() =>
          switchLanguage({ code: "xx", stringsUrl: "/i18n/xx.json" }));
        if (!ok) throw new Error("the pseudo catalog did not load");
        await page.waitForTimeout(300);
        /* and back to English, so the next read is against the same words as the first */
        await page.evaluate(() => switchLanguage({ code: "en" }));
        await page.waitForTimeout(300);
        await reseed(page);
      }],
    ];

    for (const [when, run, listChanged] of EVENTS) {
      await run();
      for (const key of ["mods", "vikings"])
        judge(TABLES[key], fresh[key], await ask(page, TABLES[key], when), when,
          !!listChanged && key === "mods");
    }

    console.log("");
    console.log("TOTAL " + failures + "   " + COLUMNS + " sortable columns x " +
      EVENTS.length + " events, each held against a fresh window");
    process.exitCode = failures ? 1 : 0;
  } finally {
    await browser.close();
    server.close();
  }
})().catch(err => { console.error(err); process.exit(2); });
