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
       Since 1.3.0 (issue 27) every SECTION head stays on screen too, and an opened section
       lands in view. Asked at 1024x680 and 1408x800: with the hall at the top every later
       head stands at the foot of the pane, and at every scroll position every head of the
       hall is inside the pane; a section opened from the top, by its chevron and by
       goSection, does not move the hall until it has rolled open, then ends with its head
       inside the pane and either its head right under the heads above or its foot at the
       foot of the pane (just above the heads stuck there, less the gap the hall keeps under
       a section: the margin over the next head, or the hall's own bottom padding after the
       last); a fold never scrolls; and with the Settings panels folded the tab strip is still
       on screen and a press on it unfolds them. Before 1.3.0 the later heads never stuck and
       a section opened from the top landed with its body under the fold.

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
  console.log((ok ? "ok   " : "FAIL ") + (name.length < 58 ? name.padEnd(58) : name + "  ") + said);
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

      /* 1.3.0, issue 27: every section head on screen, and the landing, at the minimum window
         and at the design size. */
      for (const [w, hgt] of [[1024, 680], [1408, 800]]) {
        const p = await open(w, hgt);
        const at = w + "x" + hgt;
        /* With the hall at the top, every later head at the foot; then with every section open,
           every head inside the pane at five scroll positions. */
        for (const hall of ["hearth", "mods", "world"]) {
          const r = await p.evaluate(async n => {
            const wait = ms => new Promise(r => setTimeout(r, ms));
            /* The hall's heads straight from the SECTIONS table, so this asks the page and not app.js's own helper. */
            const hallHeads = n => SECTIONS.filter(x => x.page === n).map(x => ({ key: x.key, head: document.querySelector(x.head), body: document.querySelector(x.body) })).filter(x => x.head);
            SECTIONS.forEach(x => sectSet(x.key, !!x.open, { now: true, silent: true }));
            goPage(n); await wait(250);
            const el = document.querySelector("#page-" + n);
            const read = () => {
              const pane = el.getBoundingClientRect(), view = el.clientHeight, top0 = pane.top + el.clientTop;
              return hallHeads(n).map(x => { const b = x.head.getBoundingClientRect(); return { top: b.top - top0, bottom: b.bottom - top0, h: b.height, view }; });
            };
            el.scrollTop = 0; el.dispatchEvent(new Event("scroll")); await wait(150);
            const top = read();
            /* The pane's foot fade stands down while heads stand there; it eases (0.18 s). */
            await wait(250);
            const fade = getComputedStyle(document.querySelector(".pages"), "::after").opacity;
            const foot = top.slice(1).map((b, i, a) => Math.abs(b.bottom - (b.view - a.slice(i + 1).reduce((t, x) => t + x.h, 0))));
            SECTIONS.forEach(x => sectSet(x.key, true, { now: true, silent: true }));
            await wait(150);
            const max = el.scrollHeight - el.clientHeight, off = [];
            for (const f of [0, 0.25, 0.5, 0.75, 1]) {
              el.scrollTop = Math.round(max * f); el.dispatchEvent(new Event("scroll")); await wait(120);
              read().forEach((b, i) => { if (b.top < -0.5 || b.bottom > b.view + 0.5) off.push(f + ":" + i + "@" + Math.round(b.top)); });
            }
            SECTIONS.forEach(x => sectSet(x.key, !!x.open, { now: true, silent: true }));
            return { later: top.length - 1, foot: foot.map(d => +d.toFixed(1)), off, max, fade };
          }, hall);
          say(r.later > 0 && r.foot.every(d => d <= 1) && r.fade === "0", "at " + at + " with the " + hall + " hall at the top, every later head stands at the foot",
            r.later + " later head(s), off the foot by " + r.foot.join(", ") + " px, foot fade opacity " + r.fade);
          say(r.off.length === 0, "at " + at + " every " + hall + " head is on screen at every scroll position",
            r.off.length ? "off screen: " + r.off.join(" ") : "5 positions over " + r.max + "px, every head inside the pane");
        }
        /* Each later section opened from the top, by its chevron and by goSection. */
        for (const how of ["chevron", "goSection"]) for (const key of ["skald", "saga", "runes", "herald"]) {
          const r = await p.evaluate(async ([key, how]) => {
            const wait = ms => new Promise(r => setTimeout(r, ms));
            /* The hall's heads straight from the SECTIONS table, so this asks the page and not app.js's own helper. */
            const hallHeads = n => SECTIONS.filter(x => x.page === n).map(x => ({ key: x.key, head: document.querySelector(x.head), body: document.querySelector(x.body) })).filter(x => x.head);
            const s = sectRec(key);
            SECTIONS.forEach(x => sectSet(x.key, !!x.open, { now: true, silent: true }));
            goPage(s.page); await wait(250);
            const el = document.querySelector("#page-" + s.page);
            el.scrollTop = 0; el.dispatchEvent(new Event("scroll")); await wait(150);
            if (how === "chevron") document.querySelector(s.head + " .sectchev").click(); else goSection(key);
            /* The roll is 0.2 s; the hall must not move inside it. */
            const t0 = performance.now(); let early = 0;
            while (performance.now() - t0 < 180) { early = Math.max(early, el.scrollTop); await wait(20); }
            await wait(1300);
            const heads = hallHeads(s.page), i = heads.findIndex(x => x.key === key);
            const pane = el.getBoundingClientRect(), view = el.clientHeight, top0 = pane.top + el.clientTop;
            const hs = heads.map(x => x.head.getBoundingClientRect().height);
            const above = hs.slice(0, i).reduce((t, x) => t + x, 0), below = hs.slice(i + 1).reduce((t, x) => t + x, 0);
            const hb = heads[i].head.getBoundingClientRect(), bb = heads[i].body.getBoundingClientRect();
            /* The gap the hall keeps under the section, read with every head set static, since a
               stuck head reports where it is stuck rather than where it stands in the hall. */
            heads.forEach(x => { x.head.style.position = "static"; });
            const bodyEnd = heads[i].body.getBoundingClientRect().bottom - top0 + el.scrollTop;
            const next = i + 1 < heads.length ? heads[i + 1].head.getBoundingClientRect().top - top0 + el.scrollTop : el.scrollHeight;
            heads.forEach(x => { x.head.style.position = ""; });
            return { early, headTop: hb.top - top0, headBottom: hb.bottom - top0, above, below, view,
                     bodyBottom: bb.bottom - top0, gap: next - bodyEnd };
          }, [key, how]);
          const under = Math.abs(r.headTop - r.above) <= 1;
          const footed = Math.abs(r.bodyBottom + r.gap - (r.view - r.below)) <= 1;
          const inside = r.headTop >= -0.5 && r.headBottom <= r.view + 0.5;
          say(r.early === 0 && inside && (under || footed),
            "at " + at + " " + key + " opened from the top by " + how + " lands in view",
            "no move in the roll (" + r.early + "), head " + Math.round(r.headTop) + " to " + Math.round(r.headBottom) + " of " + r.view + ", "
            + (under ? "right under the heads above (" + Math.round(r.above) + ")" : "")
            + (footed ? (under ? " and " : "") + "foot " + Math.round(r.bodyBottom) + " + gap " + Math.round(r.gap) + " = pane foot " + Math.round(r.view - r.below) : "")
            + (!under && !footed ? "neither: foot " + Math.round(r.bodyBottom) + " + gap " + Math.round(r.gap) + " against " + Math.round(r.view - r.below) : ""));
        }
        /* A fold never scrolls the hall. */
        const fold = await p.evaluate(async () => {
          const wait = ms => new Promise(r => setTimeout(r, ms));
          SECTIONS.forEach(x => sectSet(x.key, x.page === "hearth", { now: true, silent: true }));
          goPage("hearth"); await wait(250);
          const el = document.querySelector("#page-hearth");
          el.scrollTop = 300; el.dispatchEvent(new Event("scroll")); await wait(150);
          const before = el.scrollTop;
          document.querySelector("#sect-skald>.pagehead .sectchev").click();
          await wait(900);
          const after = el.scrollTop;
          SECTIONS.forEach(x => sectSet(x.key, !!x.open, { now: true, silent: true }));
          return { before, after };
        });
        say(fold.before === fold.after, "at " + at + " folding a section does not scroll the hall",
          "scrollTop " + fold.before + " before the fold, " + fold.after + " after");
        /* The Settings tabs stand outside the folding body, and a press on them unfolds it. */
        const tabs = await p.evaluate(async () => {
          const wait = ms => new Promise(r => setTimeout(r, ms));
          SECTIONS.forEach(x => sectSet(x.key, !!x.open, { now: true, silent: true }));
          goPage("world"); worldTab("server"); await wait(250);
          const el = document.querySelector("#page-world");
          el.scrollTop = 0; el.dispatchEvent(new Event("scroll")); await wait(100);
          document.querySelector("#page-world>.pagehead .sectchev").click();
          await wait(700);
          const pane = el.getBoundingClientRect();
          const shown = ["#wtabServer", "#wtabApp"].map(id => {
            const b = document.querySelector(id).getBoundingClientRect();
            return b.height > 0 && b.top >= pane.top && b.bottom <= pane.top + el.clientHeight;
          });
          const folded = !sectIsOpen("world");
          document.querySelector("#wtabApp").click();
          await wait(700);
          const result = { folded, shown, open: sectIsOpen("world"), app: document.querySelector("#worldTabApp").getBoundingClientRect().height > 0 };
          worldTab("server");
          SECTIONS.forEach(x => sectSet(x.key, !!x.open, { now: true, silent: true }));
          return result;
        });
        say(tabs.folded && tabs.shown.every(Boolean), "at " + at + " the Settings tabs stay on screen with the panels folded",
          "folded " + tabs.folded + ", Server and App on screen: " + tabs.shown.join(", "));
        say(tabs.open && tabs.app, "at " + at + " a tab pressed with the panels folded unfolds them",
          "open " + tabs.open + ", App panel on screen " + tabs.app);
        await p.close();
      }
    }
  } finally {
    await browser.close();
    server.close();
  }
  console.log("");
  console.log("TOTAL " + failures);
  process.exitCode = failures ? 1 : 0;
})().catch(err => { console.error(err); process.exit(2); });
