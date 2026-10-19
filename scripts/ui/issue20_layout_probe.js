#!/usr/bin/env node
/*
  Three layout answers from issue 20, asked of the real page in headless chromium.

  WHY THIS EXISTS. All three are things a code read cannot settle, because each one is about
  where the browser actually put a box:

    1. ROW PADDING. A settings row holding a button or a select used to carry an inline
       style="height:auto;padding:6px 0" that took the row's 12px side padding away, so its
       label sat against the border while every switch row's did not. Asked: every visible
       .togglerow in the Settings hall (both tabs) and on the Dashboard's Connection and Upkeep
       cards has the same left padding, and no .togglerow in index.html carries an inline
       padding or height.

    2. THE GRIP OVER THE SCROLLBAR. The right-hand resize grip is 6px wide at z-index 90 on
       the window's edge, and every hall's 9px scrollbar sat in the same strip, so most of the
       thumb belonged to the grip. Asked, for every hall that scrolls: the element at the
       thumb's left edge, middle and right edge is the hall and never a .grip; and the grip
       still owns the window's last pixels, so the window can still be resized there.

    3. THE STICKY HEAD. On the Dashboard, Mods and Settings the hall's head stays on screen
       while the hall scrolls. Asked: scrolled 400px down, the head's top is still at the top
       of the hall, and it casts the shadow while the pane's top fade is off.

  The page is the browser preview (no host), served from WebUI over a local port. Scrollbars
  are left ON (Playwright hides them by default), because the second question is about them.
  It FAILS 2 and 3 on the 1.2.14 WebUI and 1 on its connection test row.

  Prints one line per question, then TOTAL n. Exits non zero when n is not 0.

  node scripts/ui/issue20_layout_probe.js
  BAKA_WEBUI=<another WebUI folder> node scripts/ui/issue20_layout_probe.js
*/
"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

const ROOT = process.env.BAKA_WEBUI || path.resolve(__dirname, "..", "..", "ValheimBakaLoader", "WebUI");
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

let failures = 0;
const say = (ok, name, said) => {
  console.log((ok ? "ok   " : "FAIL ") + name.padEnd(58) + said);
  if (!ok) failures++;
};

(async () => {
  const { server, port } = await serve();
  const { chromium } = playwright();
  const browser = await chromium.launch({ headless: true, ignoreDefaultArgs: ["--hide-scrollbars"] });
  const open = async (w, h) => {
    const page = await browser.newPage({ viewport: { width: w, height: h } });
    await page.goto("http://127.0.0.1:" + port + "/index.html", { waitUntil: "load" });
    await page.waitForTimeout(900);
    await page.evaluate(() => { try { modalClose(); } catch (_) {} });
    return page;
  };
  try {
    // ---------------------------------------------------------------- 1. row padding
    {
      const html = fs.readFileSync(path.join(ROOT, "index.html"), "utf8");
      const inline = [...html.matchAll(/<(?:div|label)[^>]*class="togglerow[^"]*"[^>]*style="([^"]*)"/g)]
        .map(m => m[1]).filter(s => /padding|height/.test(s));
      say(!inline.length, "no settings row sets its own padding or height inline",
        inline.length ? inline.length + " do: " + inline.join(" | ") : "none do");

      const page = await open(1408, 900);
      const rows = [];
      for (const [hall, prep] of [["hearth", "open"], ["world:server", "server"], ["world:app", "app"]]) {
        const got = await page.evaluate(([h, how]) => {
          if (how === "open") {
            goPage("hearth");
            for (const [card, head] of [["#connCard", "#connHead"], ["#upkeepCard", "#upkeepHead"]]) {
              const c = document.querySelector(card);
              if (c && !c.classList.contains("open")) document.querySelector(head).click();
            }
          } else { goPage("world"); worldTab(how); }
          const scope = how === "open" ? "#page-hearth" : "#page-world";
          return [...document.querySelectorAll(scope + " .togglerow")].filter(r => r.offsetParent && !r.closest(".pagehead")).map(r => {
            const cs = getComputedStyle(r), label = r.querySelector(".tl");
            return {
              hall: h, pad: cs.paddingLeft, padR: cs.paddingRight,
              inset: label ? Math.round(label.getBoundingClientRect().left - r.getBoundingClientRect().left) : null,
              what: (label ? label.textContent : r.textContent).trim().slice(0, 34),
              control: r.querySelector("button,select") ? "button/select" : "switch",
            };
          });
        }, [hall, prep]);
        rows.push(...got);
      }
      const pads = new Set(rows.map(r => r.pad + "/" + r.padR));
      const insets = new Set(rows.filter(r => r.inset !== null).map(r => r.inset));
      say(rows.length > 10 && pads.size === 1, "every settings row has the same side padding",
        rows.length + " rows: " + [...pads].join(", "));
      say(insets.size === 1, "every row's label starts the same distance from its border",
        [...insets].join(", ") + " px  (" + rows.filter(r => r.control !== "switch").length + " with a button or a select)");
      const test = rows.find(r => /Test the connection/.test(r.what));
      say(!!test && test.pad === "12px", "the connection test row is padded like its neighbours",
        test ? test.pad + ", label at " + test.inset + "px" : "the row was not found");
      await page.close();
    }

    // ---------------------------------------------------------------- 2. grip over the scrollbar
    {
      const page = await open(1408, 640);
      const halls = await page.evaluate(() => [...document.querySelectorAll(".page")].map(p => p.id.replace("page-", "")));
      let asked = 0;
      for (const h of halls) {
        await page.evaluate(n => {
          goPage(n);
          const c = document.querySelector("#connCard");
          if (n === "hearth" && c && !c.classList.contains("open")) document.querySelector("#connHead").click();
        }, h);
        await page.waitForTimeout(250);
        const r = await page.evaluate(n => {
          const el = document.querySelector("#page-" + n);
          const max = el.scrollHeight - el.clientHeight;
          if (max <= 4) return null;
          el.scrollTop = Math.round(max / 2);
          const box = el.getBoundingClientRect();
          const bar = el.offsetWidth - el.clientWidth;
          const track = el.clientHeight, thumb = Math.max(18, track * track / el.scrollHeight);
          const y = box.top + thumb / 2 + (track - thumb) * (el.scrollTop / max);
          const xs = [box.right - bar + 1, box.right - bar / 2, box.right - 1];
          return {
            bar, y: Math.round(y),
            hits: xs.map(x => { const t = document.elementFromPoint(x, y); return !t ? "nothing" : t.classList.contains("grip") ? "grip" : t === el ? "hall" : t.tagName.toLowerCase(); }),
          };
        }, h);
        if (!r) continue;
        asked++;
        say(r.bar > 0 && r.hits.every(t => t === "hall"), "the " + h + " scrollbar thumb is all the hall's",
          "bar " + r.bar + "px, at y " + r.y + ": " + r.hits.join(", "));
      }
      say(asked >= 3, "enough halls scroll at 1408x640 to ask", asked + " asked");
      const edge = await page.evaluate(() => {
        const out = [];
        for (const x of [innerWidth - 1, innerWidth - 3, innerWidth - 6]) {
          const t = document.elementFromPoint(x, innerHeight / 2);
          out.push(t && t.classList.contains("grip") && t.dataset.edge === "right" ? "grip" : (t ? t.tagName.toLowerCase() : "nothing"));
        }
        return out;
      });
      say(edge.every(t => t === "grip"), "the right grip still owns the window's last 6px", edge.join(", "));
      await page.close();
    }

    // ---------------------------------------------------------------- 3. sticky head
    {
      const page = await open(1408, 640);
      for (const h of ["hearth", "mods", "world"]) {
        const r = await page.evaluate(async n => {
          goPage(n);
          if (n === "hearth") {
            for (const [card, head] of [["#connCard", "#connHead"], ["#upkeepCard", "#upkeepHead"]]) {
              const c = document.querySelector(card);
              if (c && !c.classList.contains("open")) document.querySelector(head).click();
            }
          }
          await new Promise(r => setTimeout(r, 250));
          const el = document.querySelector("#page-" + n);
          const head = el.querySelector(":scope > .pagehead");
          el.scrollTop = 400; el.dispatchEvent(new Event("scroll"));
          await new Promise(r => setTimeout(r, 200));
          const pane = el.getBoundingClientRect(), hb = head.getBoundingClientRect();
          const before = getComputedStyle(document.querySelector(".pages"), "::before");
          return {
            scrolled: el.scrollTop, floating: head.classList.contains("floating"),
            offset: Math.round(hb.top - pane.top), visible: hb.bottom > pane.top + 20,
            shadow: getComputedStyle(head).boxShadow, fade: before.display,
          };
        }, h);
        say(r.scrolled >= 100 && r.floating && r.visible && r.offset >= -20 && r.offset <= 0,
          "the " + h + " head stays on screen while the hall scrolls",
          "scrolled " + r.scrolled + "px, head top " + r.offset + "px from the pane's");
        say(r.shadow !== "none" && r.fade === "none", "the " + h + " head casts the shadow, the pane's fade is off",
          "shadow " + (r.shadow === "none" ? "none" : "on") + ", fade " + r.fade);
      }
      await page.close();
    }
  } finally {
    await browser.close();
    server.close();
  }
  console.log("");
  console.log("TOTAL " + failures);
  process.exitCode = failures ? 1 : 0;
})().catch(err => { console.error(err); process.exit(2); });
