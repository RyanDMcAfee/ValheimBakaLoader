#!/usr/bin/env node
/*
  The sidebar's sub-menus (1.3.0, the owner's feature), asked of the real page in headless
  chromium.

  WHY THIS EXISTS. sidebar_submenu_selftest.js proves the rules over a page stood in, with the
  clock and the frames in its own hands. What only a browser can answer is whether the real
  pointer, the real timers and the real layout do it: whether a pointer that only passes over a
  rail item opens nothing and one that stays opens the flyout, whether the flyout holds while the
  pointer crosses into it and goes 150 ms after it leaves, whether two fast rail clicks leave one
  dropdown and no stray node, whether the flyout stays on its item while a roll moves the rail
  and the window is resized, whether reduced motion really is a fade, whether every label fits
  the 76 px rail in the pseudo catalog and the rail still fits the 680 px window with the three
  lines of the horn of mead under it, whether a keyboard alone gets everywhere, whether a press
  on the only open section rolls nothing and still lands, whether the lit ring rests behind a
  game, and whether a chevron still keeps several sections open with a dropdown out.

  Asked in the browser preview (no host) served from WebUI over a local port, at 1024x680 and
  1408x800. With --shots <folder> it also writes the screenshots the batch reports.

  Prints one line per question, then TOTAL n. Exits non zero when n is not 0.

  node scripts/ui/sidebar_submenu_probe.js [--shots <folder>]
  BAKA_WEBUI=<another WebUI folder> node scripts/ui/sidebar_submenu_probe.js
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
  console.log((ok ? "ok   " : "FAIL ") + (name.length < 70 ? name.padEnd(70) : name + "  ") + said);
  if (!ok) failures++;
};
const wait = ms => new Promise(r => setTimeout(r, ms));

/* In the page: what the sub-menus look like right now. */
const STATE = `() => {
  const open = [...document.querySelectorAll(".subnav")].filter(n => n.style.display !== "none").map(n => n.dataset.page);
  const fly = document.querySelectorAll(".subfly:not(.reveal-ghost)");
  const f = document.querySelector("#subFly");
  return {
    open, subnavs: document.querySelectorAll(".subnav").length,
    dropButtons: document.querySelectorAll(".subnav .subitem").length,
    flies: fly.length, flyOpen: !!f && f.classList.contains("open"), flyFor: SUB_FLY.item ? SUB_FLY.item.dataset.page : null,
    flyButtons: f ? f.querySelectorAll(".subitem").length : -1,
    ghosts: document.querySelectorAll(".reveal-ghost").length,
    expanded: [...document.querySelectorAll(".navitem[aria-expanded]")].filter(n => n.getAttribute("aria-expanded") === "true").map(n => n.dataset.page),
    sections: Object.fromEntries(SECTIONS.map(s => [s.key, sectIsOpen(s.key)])),
    lit: [...document.querySelectorAll(".subnav .subitem.sel")].filter(b => b.closest(".subnav").style.display !== "none").map(b => b.dataset.sect),
    page: currentPage,
  };
}`;
const CENTER = `sel => { const r = document.querySelector(sel).getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; }`;

(async () => {
  const { server, port } = await serve();
  const { chromium } = playwright();
  const browser = await chromium.launch({ headless: true });
  const open = async (w, h, reduced) => {
    const page = await browser.newPage({ viewport: { width: w, height: h }, reducedMotion: reduced ? "reduce" : "no-preference" });
    page.on("pageerror", e => say(false, "the page ran without an error", e.message));
    await page.goto("http://127.0.0.1:" + port + "/index.html", { waitUntil: "load" });
    await page.waitForFunction(() => !!window.BakaPreview, null, { timeout: 15000 });
    await page.waitForTimeout(700);
    await page.evaluate(() => { try { modalClose(); } catch (_) {} });
    await page.evaluate(`window.__state = ${STATE}; window.__center = ${CENTER};`);
    return page;
  };
  const state = page => page.evaluate(() => window.__state());
  const center = (page, sel) => page.evaluate(s => window.__center(s), sel);
  const item = p => '.navitem[data-page="' + p + '"]';
  const away = async page => { const c = await center(page, ".pages"); await page.mouse.move(c.x, c.y); };
  const shot = async (page, name) => {
    if (!SHOTS) return;
    fs.mkdirSync(SHOTS, { recursive: true });
    const file = path.join(SHOTS, name);
    await page.screenshot({ path: file });
    console.log("     shot " + file);
  };

  try {
    /* ---------- a. hover timing ---------- */
    {
      const page = await open(1408, 800);
      await away(page);
      const s0 = await center(page, item("world"));
      let t0 = Date.now();
      await page.mouse.move(s0.x, s0.y);
      await wait(40);
      await away(page);
      const left = Date.now() - t0;
      await wait(250);
      let st = await state(page);
      say(left < 115 ? !st.flyOpen : true, "a. a pointer that only passes over Settings opens nothing", "left after " + left + " ms, open " + st.flyOpen);
      await page.mouse.move(s0.x, s0.y);
      t0 = Date.now();
      await page.waitForFunction(() => SUB_FLY.item !== null, null, { timeout: 1000 }).catch(() => {});
      const took = Date.now() - t0;
      st = await state(page);
      say(st.flyOpen && st.flyFor === "world" && took >= 100 && st.flyButtons === 2, "a. one that stays opens the Settings flyout after the delay", "after " + took + " ms, for " + st.flyFor + ", " + st.flyButtons + " buttons");
      for (const p of ["hearth", "vikings"]) {
        const c = await center(page, item(p));
        await page.mouse.move(c.x, c.y);
        await wait(300);
        st = await state(page);
        say(st.flyFor !== p, "a. the hall on screen and Players offer no flyout (" + p + ")", "flyout for " + st.flyFor);
      }
      /* into the flyout across the rail's margin */
      await page.mouse.move(s0.x, s0.y); await wait(250);
      const panel = await center(page, "#subFly .subfly-panel");
      await page.mouse.move(panel.x, panel.y, { steps: 6 });
      await wait(400);
      st = await state(page);
      say(st.flyOpen && st.flyFor === "world", "a. crossing from the item into the flyout keeps it open", "open " + st.flyOpen);
      await away(page);
      await wait(70);
      const mid = await state(page);
      await wait(250);
      st = await state(page);
      say(mid.flyOpen && !st.flyOpen && st.flyButtons === 0, "a. leaving both closes it after 150 ms, not before", "70 ms " + mid.flyOpen + ", 320 ms " + st.flyOpen);
      /* Escape and a click elsewhere inside the 150 ms */
      await page.mouse.move(s0.x, s0.y); await wait(250);
      await away(page); await wait(30);
      await page.keyboard.press("Escape");
      st = await state(page);
      say(!st.flyOpen, "a. Escape during the 150 ms closes it at once", "open " + st.flyOpen);
      await page.mouse.move(s0.x, s0.y); await wait(250);
      await away(page); await wait(30);
      await page.mouse.down(); await page.mouse.up();
      st = await state(page);
      say(!st.flyOpen, "a. a click elsewhere during the 150 ms closes it at once", "open " + st.flyOpen);
      /* never two: straight from Settings to Mods */
      await page.mouse.move(s0.x, s0.y); await wait(250);
      const m = await center(page, item("mods"));
      await page.mouse.move(m.x, m.y); await wait(300);
      st = await state(page);
      await wait(250);
      const settled = await state(page);
      say(st.flies === 1 && st.flyFor === "mods" && settled.ghosts === 0, "a. from Settings straight to Mods: one flyout, no copy left behind", "for " + st.flyFor + ", " + st.flies + " flyout, " + settled.ghosts + " ghosts");
      await page.close();
    }

    /* ---------- b. two rail clicks inside one roll ---------- */
    {
      const page = await open(1408, 800);
      await page.evaluate(() => goPage("atlas")); await wait(400);
      await page.click(item("hearth"));
      await wait(60);
      await page.click(item("world"));
      await wait(500);
      const st = await state(page);
      say(st.page === "world" && JSON.stringify(st.open) === '["world"]' && st.subnavs === 4 && st.dropButtons === 8
        && st.flyButtons === 0 && st.ghosts === 0 && JSON.stringify(st.expanded) === '["world"]',
        "b. Dashboard then Settings within 60 ms: one dropdown, under Settings", JSON.stringify({ open: st.open, subnavs: st.subnavs, buttons: st.dropButtons, fly: st.flyButtons, ghosts: st.ghosts, expanded: st.expanded }));
      await page.close();
    }

    /* ---------- c. a hall roll and a resize with a flyout out ---------- */
    {
      const page = await open(1408, 800);
      const m = await center(page, item("mods"));
      await page.mouse.move(m.x, m.y); await wait(300);
      /* the Dashboard's dropdown rolls shut above Mods: every frame the flyout keeps level */
      const track = await page.evaluate(() => new Promise(resolve => {
        const item = document.querySelector('.navitem[data-page="mods"]'), fly = document.querySelector("#subFly");
        const out = []; const t0 = performance.now();
        goPage("vikings");
        const step = () => {
          if (fly.classList.contains("open") && SUB_FLY.item === item) {
            const a = item.getBoundingClientRect(), b = fly.getBoundingClientRect();
            out.push({ off: Math.abs((a.top + a.height / 2) - (b.top + b.height / 2)), itemTop: a.top });
          }
          if (performance.now() - t0 < 260) requestAnimationFrame(step); else resolve(out);
        };
        requestAnimationFrame(step);
      }));
      const worst = Math.max(0, ...track.map(t => t.off));
      const moved = track.length ? Math.round(track[0].itemTop - track[track.length - 1].itemTop) : 0;
      say(track.length >= 5 && worst <= 1.5 && moved > 20, "c. the flyout stays level with Mods every frame while the rail rolls", track.length + " frames, item moved " + moved + " px, worst " + worst.toFixed(1) + " px");
      await wait(400);
      let st = await state(page);
      say(st.flyFor !== "mods" && st.flies === 1 && st.ghosts === 0, "c. Mods moved out from under the pointer: its flyout leaves cleanly", "flyout for " + st.flyFor + ", ghosts " + st.ghosts);
      /* a resize across the rail's height tier, the flyout out on Settings */
      await page.evaluate(() => goPage("hearth")); await wait(400);
      const s = await center(page, item("world"));
      await page.mouse.move(s.x, s.y); await wait(300);
      await page.setViewportSize({ width: 1300, height: 760 });
      const off = await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => {
        const a = document.querySelector('.navitem[data-page="world"]').getBoundingClientRect(), b = document.querySelector("#subFly").getBoundingClientRect();
        resolve({ open: SUB_FLY.item !== null, off: Math.abs((a.top + a.height / 2) - (b.top + b.height / 2)), left: Math.abs(b.left - document.querySelector(".side").getBoundingClientRect().right) });
      }))));
      say(off.open && off.off <= 1.5 && off.left <= 1, "c. after a resize the flyout is placed on its item again", JSON.stringify(off));
      await wait(500);
      st = await state(page);
      say(st.flies === 1 && st.ghosts === 0 && (st.flyOpen ? st.flyFor === "world" : st.flyButtons === 0), "c. and nothing is left over once it settles", JSON.stringify({ flyFor: st.flyFor, ghosts: st.ghosts }));
      await page.close();
    }

    /* ---------- d. reduced motion ---------- */
    for (const reduced of [false, true]) {
      const page = await open(1408, 800, reduced);
      const tag = reduced ? "reduced motion" : "full motion";
      const roll = await page.evaluate(() => {
        goPage("world");
        const a = document.querySelector("#subnav-world").getAnimations()[0];
        const keys = a ? Object.keys(a.effect.getKeyframes()[0]).filter(k => !["offset", "easing", "composite", "computedOffset"].includes(k)) : [];
        return keys.sort().join(",");
      });
      say(reduced ? roll === "opacity" : /height/.test(roll), "d. " + tag + ": the dropdown " + (reduced ? "fades, no roll" : "rolls"), "animates " + (roll || "nothing"));
      await wait(400);
      const m = await center(page, item("mods"));
      await page.mouse.move(m.x, m.y); await wait(160);
      const fly = await page.evaluate(() => {
        const p = document.querySelector("#subFly .subfly-panel"), ring = document.querySelector(".subnav .ring");
        return { anim: p ? getComputedStyle(p).animationName : "", ring: getComputedStyle(ring).transitionProperty };
      });
      say(reduced ? fly.anim === "fadeIn" : fly.anim === "subFlyIn", "d. " + tag + ": the flyout " + (reduced ? "fades, no slide" : "slides out from the rail"), fly.anim);
      say(reduced ? !/--sheen/.test(fly.ring) : /--sheen/.test(fly.ring), "d. " + tag + ": the sheen " + (reduced ? "holds still" : "turns"), fly.ring);
      if (reduced) {
        const b = await center(page, "#subnav-world .subitem");
        await page.mouse.move(b.x, b.y); await wait(300);
        const sheen = await page.evaluate(() => getComputedStyle(document.querySelector("#subnav-world .ring")).getPropertyValue("--sheen").trim());
        say(sheen === "0deg", "d. reduced motion: a hovered ring keeps its light where it is", sheen);
      }
      await page.close();
    }

    /* ---------- e. every label fits, and the rail fits the window ---------- */
    for (const code of ["en", "xx"]) for (const [w, h] of [[1024, 680], [1408, 800]]) {
      const page = await open(w, h);
      if (code !== "en") {
        const loaded = await page.evaluate(c => window.BakaPreview.loadLanguage(c), code);
        if (!loaded) { say(false, "e. " + code + " catalog loads", "it did not"); await page.close(); continue; }
        await wait(300);
      }
      const tag = "e. " + code + " " + w + "x" + h;
      const bad = [], fit = [];
      for (const hall of ["hearth", "atlas", "mods", "world"]) {
        await page.evaluate(n => goPage(n), hall); await wait(350);
        const r = await page.evaluate(() => {
          const side = document.querySelector(".side").getBoundingClientRect();
          const clipped = [...document.querySelectorAll(".subnav .lbl,.subnav .norse")].filter(k => k.getClientRects().length).filter(k => {
            const r = k.getBoundingClientRect(), b = k.closest(".subitem").getBoundingClientRect();
            return k.scrollWidth > k.clientWidth + 0.5 || r.left < b.left - 0.5 || r.right > b.right + 0.5 || r.right > side.right;
          }).map(k => k.textContent);
          const nav = [...document.querySelectorAll(".navgroup")].pop().getBoundingClientRect();
          const foot = document.querySelector(".side-foot").getBoundingClientRect();
          const mead = document.querySelector("#meadLink");
          const ring = document.querySelector(".subnav:not([style*='none']) .ring");
          return { clipped, gap: Math.round(foot.top - nav.bottom), footIn: foot.bottom <= side.bottom + 0.5,
                   lines: mead.querySelectorAll("br").length + 1, ring: ring ? ring.offsetWidth : 0 };
        });
        bad.push(...r.clipped);
        fit.push(hall + " gap " + r.gap + " ring " + r.ring);
        if (r.gap < 0 || !r.footIn || r.lines !== 3 || r.ring !== (h <= 780 ? 30 : 34)) bad.push(hall + ": gap " + r.gap + ", foot inside " + r.footIn + ", mead lines " + r.lines + ", ring " + r.ring);
      }
      /* the flyout's labels too */
      const c = await center(page, item("vikings"));
      await page.mouse.move(c.x, c.y);
      const m = await center(page, item("mods"));
      await page.mouse.move(m.x, m.y); await wait(300);
      const flyClip = await page.evaluate(() => [...document.querySelectorAll("#subFly .lbl,#subFly .norse")].filter(k => k.getClientRects().length && k.scrollWidth > k.clientWidth + 0.5).map(k => k.textContent));
      bad.push(...flyClip);
      say(bad.length === 0, tag + ": no label clips, the rail and the three-line foot fit", bad.length ? bad.join(" | ") : fit.join(", "));
      if (code === "en" && w === 1024) {
        await away(page); await wait(300);
        await page.evaluate(() => goPage("mods")); await wait(400);
        await page.click('#subnav-mods .subitem[data-sect="runes"]');
        await wait(900);
        await shot(page, "p2_configs_landed_1024x680.png");
      }
      await page.close();
    }

    /* ---------- f. the keyboard alone ---------- */
    {
      const page = await open(1408, 800);
      await page.keyboard.press("Shift");
      await page.evaluate(() => document.querySelector('.navitem[data-page="hearth"]').focus());
      const act = () => page.evaluate(() => { const a = document.activeElement; return a.dataset.sect || a.dataset.page || a.id || a.className; });
      await page.keyboard.press("Tab");
      const first = await act();
      await page.keyboard.press("ArrowDown");
      const second = await act();
      await page.keyboard.press("Enter");
      await wait(500);
      let st = await state(page);
      say(first === "hearth" && second === "saga" && st.sections.saga && !st.sections.hearth,
        "f. Tab reaches Overview, an arrow reaches Log, Enter opens the Log alone", first + " > " + second + ", hearth " + st.sections.hearth + ", saga " + st.sections.saga);
      await page.keyboard.press("Tab");          /* Players */
      await page.keyboard.press("Tab");          /* Map: opens its flyout on focus */
      st = await state(page);
      const onMap = await act();
      const aria = await page.evaluate(() => [document.querySelector('.navitem[data-page="hearth"]').getAttribute("aria-expanded"),
        document.querySelector('.navitem[data-page="atlas"]').getAttribute("aria-expanded"), document.querySelector('.navitem[data-page="hearth"]').getAttribute("aria-controls"),
        document.querySelector("#subFly").getAttribute("role"), (document.querySelector("#subFly .subitem") || {}).getAttribute ? document.querySelector("#subFly .subitem").getAttribute("role") : null]);
      say(onMap === "atlas" && st.flyFor === "atlas" && aria.join() === "true,false,subnav-hearth,menu,menuitem",
        "f. focus on Map opens its flyout; aria-expanded, aria-controls and the menu roles hold", onMap + ", flyout " + st.flyFor + ", " + aria.join());
      await page.keyboard.press("Tab");
      const inFly = await page.evaluate(() => document.querySelector("#subFly").contains(document.activeElement) && document.activeElement.dataset.sect);
      await page.keyboard.press("ArrowRight");
      const inFly2 = await page.evaluate(() => document.activeElement.dataset.sect);
      await page.keyboard.press("Escape");
      await wait(100);
      st = await state(page);
      const back = await act();
      say(inFly === "atlas" && inFly2 === "skald" && !st.flyOpen && back === "atlas",
        "f. Tab goes into the flyout, arrows walk it, Escape closes it and hands focus back", inFly + " > " + inFly2 + ", open " + st.flyOpen + ", focus " + back);
      await page.keyboard.press("Tab");          /* Mods: its flyout */
      await page.keyboard.press("Tab");          /* into it: Mods */
      await page.keyboard.press("ArrowRight");   /* Configs */
      await page.keyboard.press("Enter");
      await wait(600);
      st = await state(page);
      const after = await page.evaluate(() => { const a = document.activeElement; return (a.closest(".subnav") ? "dropdown:" : "") + (a.dataset.sect || a.tagName); });
      say(st.page === "mods" && st.sections.runes && !st.sections.mods && after === "dropdown:runes" && JSON.stringify(st.expanded) === '["mods"]',
        "f. Enter in the flyout opens Configs alone and focus lands on its dropdown button", JSON.stringify({ page: st.page, runes: st.sections.runes, mods: st.sections.mods, focus: after, expanded: st.expanded }));
      await page.close();
    }

    /* ---------- g. the only open section pressed again ---------- */
    {
      const page = await open(1408, 800);
      await page.evaluate(() => goPage("mods")); await wait(400);
      await page.click('#subnav-mods .subitem[data-sect="runes"]');
      await wait(900);
      await shot(page, "p2_configs_landed_1408x800.png");
      const landed = await page.evaluate(() => {
        const pg = document.querySelector("#page-mods"), heads = sectHeads("mods"), g = sectGeometry(pg, heads); g.i = 1;
        return { at: Math.round(pg.scrollTop), want: sectLandTop(g) };
      });
      let st = await state(page);
      say(!st.sections.mods && st.sections.runes && Math.abs(landed.at - landed.want) <= 1 && JSON.stringify(st.lit) === '["runes"]',
        "g. a Configs press: Mods folded, Configs open and landed, its ring the lit one", JSON.stringify({ landed, lit: st.lit }));
      await page.evaluate(() => { document.querySelector("#page-mods").scrollTop = 0; });
      await wait(100);
      const again = await page.evaluate(() => new Promise(resolve => {
        const body = document.querySelector("#sectBody-runes"), pg = document.querySelector("#page-mods");
        document.querySelector('#subnav-mods .subitem[data-sect="runes"]').click();
        const rolled = body.getAnimations().filter(a => a.effect && a.effect.getKeyframes().some(k => "height" in k)).length;
        const others = document.querySelector("#sectBody-mods").getAnimations().length;
        setTimeout(() => resolve({ rolled, others, top: Math.round(pg.scrollTop) }), 800);
      }));
      const want2 = await page.evaluate(() => { const pg = document.querySelector("#page-mods"), g = sectGeometry(pg, sectHeads("mods")); g.i = 1; return sectLandTop(g); });
      st = await state(page);
      say(again.rolled === 0 && again.others === 0 && st.sections.runes && !st.sections.mods && Math.abs(again.top - want2) <= 1,
        "g. pressed again as the only open section: no roll, and it still lands", JSON.stringify({ ...again, want: want2 }));
      await page.close();
    }

    /* ---------- i. behind a game the lit ring rests ---------- */
    {
      const page = await open(1408, 800);
      const play = () => page.evaluate(() => {
        const a = document.getAnimations().find(x => x.animationName === "ringEmber");
        return a ? a.playState : "none";
      });
      const before = await play();
      await page.evaluate(() => document.body.classList.add("idle"));
      await wait(100);
      const idle = await play();
      await page.evaluate(() => document.body.classList.remove("idle"));
      await wait(100);
      const back = await play();
      say(before === "running" && idle === "paused" && back === "running", "i. body.idle pauses the lit ring's ember, and it resumes", before + " > " + idle + " > " + back);
      await page.close();
    }

    /* ---------- j. the chevrons still keep several open, with a dropdown out ---------- */
    {
      const page = await open(1408, 800);
      await page.evaluate(() => goPage("mods")); await wait(400);
      await page.click("#sect-runes .sectchev");
      await wait(500);
      let st = await state(page);
      say(st.sections.mods && st.sections.runes && JSON.stringify(st.lit) === '["mods","runes"]' && JSON.stringify(st.open) === '["mods"]',
        "j. a Configs chevron with the Mods dropdown out: both open, both rings lit", JSON.stringify({ mods: st.sections.mods, runes: st.sections.runes, lit: st.lit }));
      await page.evaluate(() => { document.querySelector("#page-mods").scrollTop = 0; });
      await page.click("#page-mods>.pagehead .sectchev");
      await wait(500);
      st = await state(page);
      say(!st.sections.mods && st.sections.runes && JSON.stringify(st.lit) === '["runes"]', "j. the Mods chevron folds Mods alone and rests its ring", JSON.stringify({ mods: st.sections.mods, runes: st.sections.runes, lit: st.lit }));
      await page.close();
    }

    /* ---------- the screenshots: the dropdown under Dashboard, the flyout over Settings ---------- */
    if (SHOTS) {
      const page = await open(1408, 800);
      await away(page); await wait(400);
      await shot(page, "p2_dropdown_dashboard_1408x800.png");
      const s = await center(page, item("world"));
      await page.mouse.move(s.x, s.y); await wait(500);
      await shot(page, "p2_flyout_settings_1408x800.png");
      await page.close();
    }
  } finally {
    await browser.close();
    server.close();
  }
  console.log("TOTAL " + failures);
  process.exit(failures === 0 ? 0 : 1);
})().catch(e => { console.log("FAIL the probe ran: " + (e && e.stack || e)); process.exit(1); });
