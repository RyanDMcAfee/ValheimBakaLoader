/* The sidebar's sub-menus (1.3.0, the owner's feature), proved against app.js, app.css,
 * index.html and the catalog as they are.
 *
 * WHY THIS EXISTS. Under the rail item of the hall on screen a column of forged bronze ring
 * buttons rolls out, one per section of that hall; over any other item a flyout holds the same
 * buttons in a row. A press opens that section alone, folds the others and lands it; the
 * chevrons and the hall's own scrolling are left as they were, so several sections may still
 * stand open. The quiet ways that goes wrong: a second list of entries that drifts from
 * SECTIONS, a press that folds nothing or folds with every road in (the palette, the toasts),
 * a landing measured while the sections above are still rolling shut (aimed at where the
 * section used to be), a landing lost when the window is hidden in the middle of the roll, a
 * ring that stays lit after a chevron folds its section, a flyout that opens on a pointer
 * only passing over or never closes, a ring look that drifts from the owner's pick, a
 * flyout that slides under reduced motion, and embers that keep burning behind a game.
 * scripts/ui/sidebar_submenu_probe.js asks the same of the real page in a browser.
 *
 * The rules:
 *   1. The entries come off SECTIONS: every row names its button's words (subId, and norseId
 *      except Mods, which has no Norse name), the catalog holds them in plain sentence case,
 *      each hall lists its sections in the table's order, Players lists none, and a button's
 *      glyph is read off its section's head.
 *   2. A press is goSection with {solo}: the hall's other sections fold, the chosen one opens,
 *      and the landing runs once, after the last of those rolls; a road without {solo} folds
 *      nothing; a press on the only open section rolls nothing and still lands.
 *   3. A window hidden in the middle of the roll: the landing waits in the rolls' {done} and
 *      runs when they finish (when the page is shown again), never on a timer that can be lost.
 *   4. The lit ring follows sectSet, so a chevron's fold rests it and an open lights it.
 *   5. The flyout: 120 ms before it opens, nothing for a pointer that leaves sooner, 150 ms
 *      after the pointer is out of both the item and the flyout, at once on Escape or a press
 *      elsewhere, one at a time, never over the hall on screen or Players, and placed on its
 *      item's rect every frame it is open.
 *   6. The keyboard: the items are buttons a keyboard reaches with aria-expanded and
 *      aria-controls, arrows walk a menu, the flyout is a role="menu" of menuitems, Tab goes
 *      into it and out of it, and Escape hands focus back to the item.
 *   7. The ring is the owner's pick, lifted whole: four background layers, the registered
 *      --sheen turning 26 degrees in 0.2 s, the .heat and the ember face, five shadows in every
 *      state, the designer's halo at .5, the idle rune in the rail's colour, no second ember
 *      bar, no fonts fetched, tokens on :root, 30px rings at the shortest rail.
 *   8. Motion: the dropdown rolls through reveal and is drawn hidden; the flyout slides 8px out
 *      from behind the rail and leaves through fadeGhost; under reduced motion both are fades,
 *      the sheen holds still and the ember stops; under body.idle the lit ring's ember pauses.
 *   9. The record: docs/sidebar.md carries the section, and the copy gate runs this selftest
 *      and the probe.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/sidebar_submenu_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const vm = require("vm");
const assert = require("assert");

const ROOT = path.resolve(__dirname, "..", "..");
const WEB = path.join(ROOT, "ValheimBakaLoader", "WebUI");
const read = p => fs.readFileSync(p, "utf8").replace(/\r\n/g, "\n");
const APP = read(path.join(WEB, "app.js"));
const CSS = read(path.join(WEB, "app.css"));
const HTML = read(path.join(WEB, "index.html"));
const EN = JSON.parse(read(path.join(WEB, "i18n", "en.json"))).keys;
const MAP = read(path.join(ROOT, "docs", "sidebar.md"));
const GATE = read(path.join(ROOT, "scripts", "copy-gate", "copy_gate.sh"));

let passed = 0;
const failures = [];
function test(name, fn) {
  try { fn(); passed++; console.log("  ok   " + name); }
  catch (problem) {
    failures.push(name);
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}
const same = (a, b, why) => assert.deepStrictEqual(JSON.parse(JSON.stringify(a)), b, why);
function body(head) {
  const at = APP.indexOf(head);
  assert.ok(at >= 0, "app.js no longer has " + head);
  return APP.slice(at, APP.indexOf("\n}", at) + 2);
}
/* A rule written at the start of a line, so ".ring" is not found inside ".subitem .ring". */
function rule(sel) {
  let at = -1;
  for (let i = CSS.indexOf(sel + "{"); i >= 0; i = CSS.indexOf(sel + "{", i + 1))
    if (i === 0 || CSS[i - 1] === "\n" || CSS[i - 1] === "}") { at = i; break; }
  assert.ok(at >= 0, "app.css has no rule " + sel);
  return CSS.slice(at, CSS.indexOf("}", CSS.indexOf("{", at)) + 1);
}
/* The SECTIONS block, the sub-menus inside it, evaluated with the page stood in. */
function block() {
  const from = APP.indexOf("/* ---------- SECTIONS (1.2.18");
  const to = APP.indexOf("/* ---------- NAV ---------- */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the SECTIONS block before NAV");
  const b = APP.slice(from, to);
  assert.ok(b.includes("/* SUBMENU-BEGIN") && b.includes("/* SUBMENU-END */"), "the sub-menus are not marked inside the SECTIONS block");
  return b;
}
const GLYPHS = { "#page-hearth>.pagehead": "ᚠ", "#sect-saga>.pagehead": "ᛋ", "#page-atlas>.pagehead": "ᛞ", "#sect-skald>.pagehead": "ᛏ",
                 "#page-mods>.pagehead": "ᚱ", "#sect-runes>.pagehead": "ᚲ", "#page-world>.pagehead": "ᛃ", "#sect-herald>.pagehead": "ᚺ" };
function page(opts) {
  opts = opts || {};
  const timers = new Map(); let tid = 0;
  const frames = [];
  const reveals = [];
  const landed = [];
  const buttons = opts.buttons || [];
  const els = {};
  const el = sel => els[sel] || (els[sel] = {
    sel, classList: { toggle() {}, add() {}, remove() {}, contains: () => false },
    parentElement: { classList: { toggle() {} } }, querySelector: () => null, offsetWidth: 0, style: {} });
  const c = {
    Native: { available: false }, T: id => "<" + id + ">", rpc: () => {},
    esc: s => String(s), revealReduced: () => false, rescanScrollCues: () => {}, sectStackRead: () => {},
    currentPage: opts.page || "hearth", document: { activeElement: null }, window: { innerHeight: 800 },
    $: sel => {
      const m = /^(.*) h1 \.r$/.exec(sel);
      if (m) return GLYPHS[m[1]] ? { textContent: GLYPHS[m[1]] } : null;
      if (opts.dollar && opts.dollar(sel)) return opts.dollar(sel);
      return el(sel);
    },
    $$: sel => buttons.filter(b => sel === '.subitem[data-sect="' + b.dataset.sect + '"]' || sel === ".subitem"),
    reveal: (e, show, o) => { reveals.push({ sel: e && e.sel, show, o }); },
    requestAnimationFrame: fn => { frames.push(fn); return frames.length; }, cancelAnimationFrame: () => {},
    setTimeout: (fn, ms) => { timers.set(++tid, { fn, ms }); return tid; }, clearTimeout: id => { timers.delete(id); },
    goPage: name => { c.currentPage = name; },
    skaldRefresh: () => {}, sagaToTop: () => {}, refreshCfgList: () => {}, refreshHerald: () => {}, fadeGhost: () => {},
  };
  vm.createContext(c);
  vm.runInContext(block() + "\nthis.S=SECTIONS;this.UI=UI_SECTIONS;this.sectSet=sectSet;this.sectIsOpen=sectIsOpen;this.goSection=goSection;"
    + "this.subEntries=subEntries;this.subButtonHtml=subButtonHtml;this.F=SUB_FLY;this.subHoverIn=subHoverIn;this.subHoverOut=subHoverOut;"
    + "this.subFlyOpen=subFlyOpen;this.subFlyClose=subFlyClose;this.subFlyTick=subFlyTick;this.subSync=subSync;this.SUB_OPEN_MS=SUB_OPEN_MS;this.SUB_CLOSE_MS=SUB_CLOSE_MS;",
    c, { filename: "app.js#sections" });
  /* sectLand is in the block; the landing is asked of it, so it is stood in after the block runs. */
  c.sectLand = key => landed.push(key);
  Object.assign(c, { timers, frames, reveals, landed });
  return c;
}

test("1. the entries come off SECTIONS, worded from the catalog, glyphs from the heads", () => {
  const p = page();
  const halls = {};
  for (const hall of ["hearth", "vikings", "atlas", "mods", "world"]) halls[hall] = p.subEntries(hall).map(s => s.key);
  same(halls, { hearth: ["hearth", "saga"], vikings: [], atlas: ["atlas", "skald"], mods: ["mods", "runes"], world: ["world", "herald"] },
    "the sub-menus do not list the owner's entries under the halls he named");
  const words = { hearth: "Overview", saga: "Log", atlas: "Map", skald: "Statistics", mods: "Mods", runes: "Configs", world: "Settings", herald: "Discord" };
  for (const s of p.S) {
    assert.strictEqual(s.subId, "side.sub." + s.key + ".label", s.key + " does not name its button's words");
    assert.ok(EN[s.subId] && EN[s.subId].lore === words[s.key], s.subId + " is not \"" + words[s.key] + "\" in the catalog");
    if (s.key === "mods") assert.ok(!s.norseId, "Mods carries a Norse caption, and its rail item has none");
    else {
      assert.strictEqual(s.norseId, "common.norse." + s.key, s.key + "'s caption is not its Norse name");
      assert.ok(EN[s.norseId], s.norseId + " is not in the catalog");
    }
  }
  /* One table: nothing else in app.js lists the sub-menu entries. */
  assert.ok(!/side\.sub\.[a-z]+\.label"\s*[,\]]/.test(APP.replace(/subId:"side\.sub\.[a-z]+\.label"/g, "")), "a second list of sub-menu words exists outside SECTIONS");
  const html = p.subButtonHtml(p.S.find(s => s.key === "runes"));
  assert.ok(/^<button type="button" class="subitem" data-sect="runes"><span class="ring" aria-hidden="true"><span class="heat"><\/span><span class="rune">ᚲ<\/span><\/span><span class="lbl"><\/span><span class="norse"><\/span><\/button>$/.test(html),
    "a button is not a ring with its section head's rune over a label and a caption: " + html);
  /* The static rail is untouched: the gates that read it by its markup still read five items. */
  assert.strictEqual([...HTML.matchAll(/<div class="navitem[^"]*" data-page="[a-z]+">/g)].length, 5, "the rail's markup changed");
});

test("2. a press opens the one section alone and lands it after every roll; other roads fold nothing", () => {
  const p = page({ page: "hearth" });
  p.sectSet("mods", true, { now: true, silent: true }); p.sectSet("runes", false, { now: true, silent: true });
  p.reveals.length = 0;
  p.goSection("runes", { solo: true });
  assert.strictEqual(p.currentPage, "mods", "the press did not walk to the hall");
  same(p.reveals.map(r => [r.sel, r.show]), [["#sectBody-mods", false], ["#sectBody-runes", true]], "the hall's other section did not fold, or the chosen one did not open");
  assert.strictEqual(p.sectIsOpen("mods"), false); assert.strictEqual(p.sectIsOpen("runes"), true);
  assert.deepStrictEqual(p.landed, [], "the landing ran before the rolls were over");
  p.reveals[1].o.done();
  assert.deepStrictEqual(p.landed, [], "the landing ran while the section above was still rolling shut");
  p.reveals[0].o.done();
  assert.deepStrictEqual(p.landed, ["runes"], "the landing did not run once, after the last roll");
  assert.ok(p.reveals.every(r => !r.o.land), "a roll in the press asked for its own landing as well");
  /* A road without {solo}: the palette, a toast, a link. */
  const q = page({ page: "hearth" });
  q.sectSet("mods", true, { now: true, silent: true });
  q.reveals.length = 0;
  q.goSection("runes");
  same(q.reveals.map(r => [r.sel, r.show]), [["#sectBody-runes", true]], "a plain road folded a section");
  q.reveals[0].o.done();
  assert.deepStrictEqual(q.landed, ["runes"], "a plain road no longer lands its section after the roll");
  assert.strictEqual(q.sectIsOpen("mods"), true);
  /* Scenario g: the only open section pressed again. reveal answers at once for a box already
     shown (no roll); the landing still runs. */
  const g = page({ page: "mods" });
  g.sectSet("mods", false, { now: true, silent: true }); g.sectSet("runes", true, { now: true, silent: true });
  g.reveals.length = 0;
  g.reveal = (e, show, o) => { g.reveals.push({ sel: e && e.sel, show, o }); if (o && o.done) o.done(); };
  g.goSection("runes", { solo: true });
  same(g.reveals.map(r => [r.sel, r.show]), [["#sectBody-runes", true]], "a press on the only open section rolled another section");
  assert.deepStrictEqual(g.landed, ["runes"], "a press on the only open section did not land it");
  const go = body("function goSection(key,opts){");
  assert.ok(go.includes("others.forEach(o=>sectSet(o.key,false,{silent:true,done:settled}));") && go.includes("sectSet(key,true,{done:settled});"),
    "the press does not wait on every roll before it lands");
});

test("3. a window hidden in the middle of the roll still lands once it is shown", () => {
  /* While the document is hidden the browser runs no frames, so a roll's finish (and its
     {done}) waits until the window is shown. The landing lives in those {done}s, not on a timer
     or a frame of its own, so it is late rather than lost. */
  const p = page({ page: "hearth" });
  p.sectSet("hearth", true, { now: true, silent: true }); p.sectSet("saga", false, { now: true, silent: true });
  p.reveals.length = 0;
  p.goSection("saga", { solo: true });
  assert.strictEqual(p.timers.size, 0, "the press set a timer that a hidden window could outlive");
  /* hidden: nothing finishes; the glow's frame may run or not */
  p.frames.forEach(f => f());
  assert.deepStrictEqual(p.landed, [], "the landing ran with the rolls still out");
  /* shown again: the rolls finish, in either order */
  p.reveals.slice().reverse().forEach(r => r.o.done());
  assert.deepStrictEqual(p.landed, ["saga"], "the landing was lost while the window was hidden");
  const set = body("function sectSet(key,open,opts){");
  assert.ok(set.includes("if(then){try{then();}catch(_){}}") && set.includes("reveal(body,open,opts&&opts.now?{now:true,done}:{done});"),
    "sectSet does not hand its roll's end on to a caller that waits for it");
});

test("4. the lit ring follows sectSet, from a chevron as much as from a press", () => {
  const mk = key => ({ dataset: { sect: key }, cls: new Set(), classList: null });
  const buttons = ["mods", "runes", "runes"].map(mk);
  buttons.forEach(b => { b.classList = { toggle: (c, on) => { if (on) b.cls.add(c); else b.cls.delete(c); } }; });
  const p = page({ page: "mods", buttons });
  p.sectSet("runes", true, { silent: true });
  assert.ok(buttons[1].cls.has("sel") && buttons[2].cls.has("sel"), "opening Configs did not light its rings (dropdown and flyout)");
  assert.ok(!buttons[0].cls.has("sel"), "opening Configs lit the Mods ring");
  p.sectSet("runes", false, { silent: true });
  assert.ok(!buttons[1].cls.has("sel"), "folding Configs left its ring lit");
  assert.ok(body("function sectSet(key,open,opts){").includes("try{subLight(key);}catch(_){}"), "sectSet no longer lights the ring");
  assert.ok(body("function subButtonHtml(s){").includes('(sectIsOpen(s.key)?" sel":"")'), "a button is drawn unlit for a section that stands open");
  /* The chevron is unchanged: it toggles its own section and lands it, folding nothing (j). */
  assert.ok(body("function sectWire(){").includes('b.addEventListener("click",()=>sectSet(s.key,!sectIsOpen(s.key),{land:true}));'), "the chevron changed");
});

test("5. the flyout: 120 ms in, 150 ms out, at once on Escape or a press elsewhere, one at a time", () => {
  assert.strictEqual(page().SUB_OPEN_MS, 120); assert.strictEqual(page().SUB_CLOSE_MS, 150);
  const rect = (top, h, left, w) => { const l = left === undefined ? 8 : left, ww = w || 76; return { top, bottom: top + h, left: l, right: l + ww, height: h, width: ww }; };
  const item = (pg, top) => ({ dataset: { page: pg }, isConnected: true, contains: x => x === this, getBoundingClientRect: () => rect(top, 45),
                              querySelector: () => ({ textContent: pg }) });
  let ghosts = 0;
  const fly = { style: {}, offsetHeight: 70, set innerHTML(v) { this.html = v; }, set textContent(v) { this.html = v; },
                classList: { set: new Set(), add(c) { this.set.add(c); }, remove(c) { this.set.delete(c); } },
                setAttribute() {}, querySelectorAll: () => [], querySelector: () => null, contains: () => false,
                get firstElementChild() { return { getBoundingClientRect: () => rect(380, 70, 92, 160) }; } };
  const p = page({ page: "hearth", dollar: s => (s === ".side" ? { getBoundingClientRect: () => rect(44, 756, 0, 92) } : null) });
  p.fadeGhost = () => { ghosts++; };
  p.F.el = fly;
  const world = item("world", 400), mods = item("mods", 340), vik = item("vikings", 250), hearth = item("hearth", 200);
  const pending = () => [...p.timers.values()];
  /* a pointer that passes over: nothing */
  p.subHoverIn(world);
  same(pending().map(t => t.ms), [120], "the flyout does not wait 120 ms");
  p.subHoverOut(world);
  assert.strictEqual(p.timers.size, 0, "a pointer that left before 120 ms still opens the flyout");
  assert.ok(!fly.classList.set.has("open"));
  /* never over the hall on screen, never over Players */
  p.subHoverIn(hearth); p.subHoverIn(vik);
  assert.strictEqual(p.timers.size, 0, "the hall on screen or Players offers a flyout");
  /* stays 120 ms: open, placed level with its item against the rail's edge */
  p.subHoverIn(world);
  pending()[0].fn(); p.timers.clear();
  assert.ok(fly.classList.set.has("open") && p.F.item === world, "the flyout did not open after 120 ms");
  assert.ok(fly.html.includes('data-sect="world"') && fly.html.includes('data-sect="herald"'), "the flyout does not hold the hall's buttons");
  assert.strictEqual(fly.style.left, "92px", "the flyout is not flush against the rail's edge");
  assert.strictEqual(fly.style.top, Math.round(400 + 22.5 - 35) + "px", "the flyout is not level with its item");
  /* pointer over the item, then over the flyout: stays; out of both: 150 ms */
  p.F.px = 40; p.F.py = 420; p.subFlyTick();
  assert.strictEqual(p.timers.size, 0, "a pointer over the item started the close");
  p.F.px = 150; p.F.py = 400; p.subFlyTick();
  assert.strictEqual(p.timers.size, 0, "a pointer over the flyout started the close");
  p.F.px = 600; p.F.py = 300; p.subFlyTick();
  same(pending().map(t => t.ms), [150], "leaving both does not close after 150 ms");
  p.F.px = 150; p.F.py = 400; p.subFlyTick();
  assert.strictEqual(p.timers.size, 0, "coming back inside 150 ms did not keep it open");
  /* every frame it is open, placed again: an item that moves takes its flyout with it */
  world.getBoundingClientRect = () => rect(300, 45);
  p.F.px = 40; p.F.py = 320; p.subFlyTick();
  assert.strictEqual(fly.style.top, Math.round(300 + 22.5 - 35) + "px", "the flyout stayed where its item was");
  assert.ok(p.frames.length >= 2, "the flyout is not placed again on the next frame");
  /* one at a time: another item's 120 ms closes the first, through fadeGhost */
  p.subHoverIn(mods); pending()[0].fn(); p.timers.clear();
  assert.ok(p.F.item === mods && ghosts === 1, "a second flyout opened beside the first, or the first vanished without fading");
  /* its item becoming the hall on screen closes it at once */
  p.currentPage = "mods"; p.subSync("mods");
  assert.ok(p.F.item === null && !fly.classList.set.has("open") && fly.html === "", "the flyout outlived its item becoming the hall on screen, or kept its buttons");
  /* Escape and a press elsewhere close at once, with no 150 ms wait */
  const wire = body("function subWire(){");
  assert.ok(/if\(e\.key!=="Escape"\|\|!f\.item\) return;[\s\S]*subFlyClose\(\);/.test(wire), "Escape does not close the flyout at once");
  assert.ok(/document\.addEventListener\("pointerdown",e=>\{[\s\S]*if\(f\.item&&!f\.el\.contains\(t\)&&!f\.item\.contains\(t\)\) subFlyClose\(\);[\s\S]*\},true\);/.test(wire), "a press elsewhere does not close the flyout at once");
  assert.ok(wire.includes("if(f.want&&!f.want.contains(t)){clearTimeout(f.openT);"), "a press elsewhere during the 120 ms still opens the flyout");
  assert.ok(wire.includes("host.insertAdjacentHTML(\"beforeend\",'<div class=\"subfly\" id=\"subFly\" role=\"menu\"></div>');") && wire.includes('const host=$(".body"); if(!host||$("#subFly")) return;'),
    "the flyout is not one element in .body, made once");
});

test("6. the keyboard reaches every button, and Escape hands focus back", () => {
  const wire = body("function subWire(){");
  assert.ok(wire.includes('item.tabIndex=0; item.setAttribute("role","button");'), "a rail item is not a button a keyboard reaches");
  assert.ok(wire.includes('item.setAttribute("aria-controls",id);') && wire.includes('item.setAttribute("aria-expanded","false");'), "a rail item does not say which menu it opens");
  assert.ok(body("function subSync(page,opts){").includes('item.setAttribute("aria-expanded",on?"true":"false");'), "aria-expanded does not track the dropdown");
  assert.ok(wire.includes('if(item.matches(":focus-visible")) subFlyOpen(item);'), "the flyout does not open on keyboard focus (or opens on a mouse press)");
  assert.ok(body("function subFlyOpen(item){").includes("subWireButtons(f.el,true);") && body("function subWireButtons(box,menu){").includes('if(menu) b.setAttribute("role","menuitem");'),
    "the flyout's buttons are not menuitems");
  const arrows = body("function subArrows(e,box){");
  assert.ok(arrows.includes('e.key==="ArrowDown"||e.key==="ArrowRight"') && arrows.includes('e.key==="ArrowUp"||e.key==="ArrowLeft"'), "arrows do not walk a menu");
  const key = body("function subItemKey(e,item){");
  assert.ok(key.includes('if(e.key==="Enter"||e.key===" "){e.preventDefault();item.click();return;}'), "Enter or Space does not press a rail item");
  assert.ok(key.includes('if(e.key==="Tab"&&!e.shiftKey&&f.item===item){'), "Tab from an item does not go into its flyout");
  const fk = body("function subFlyKey(e){");
  assert.ok(fk.includes("if(e.shiftKey&&i===0){e.preventDefault();f.hush=f.item;f.item.focus();}") && fk.includes("const next=subNextFocusable(f.item);"),
    "Tab does not come back out of the flyout, either way");
  assert.ok(wire.includes("if(inside){f.hush=item;try{item.focus({preventScroll:true});}catch(_){}}"), "Escape does not hand focus back to the item, or the item opens its flyout again");
  assert.ok(body("function subGo(key){").includes("const b=$('.subnav .subitem[data-sect=\"'+key+'\"]');"), "a press from the flyout drops the keyboard's focus");
});

test("7. the ring is the owner's pick, forged bronze, lifted whole with the designer's notes", () => {
  const ring = rule(".ring");
  assert.strictEqual((ring.match(/gradient\(/g) || []).length, 4, "the ring does not paint four layers");
  assert.ok(/radial-gradient\(circle at 30% 16%/.test(ring) && /repeating-conic-gradient\(from calc\(var\(--sheen\) \+ 7deg\)[\s\S]*16deg 24deg\)/.test(ring)
    && /repeating-conic-gradient\(from calc\(3deg - var\(--sheen\)\)[\s\S]*22deg 37deg\)/.test(ring) && /conic-gradient\(from calc\(var\(--sheen\) \+ 200deg\)/.test(ring),
    "the four layers are not the broad light, the 24 and 37 degree facets and the specular spot");
  assert.ok(ring.includes("transition:box-shadow .2s var(--ease),filter .2s var(--ease),--sheen .2s var(--ease);"), "the sheen does not ease over 0.2 s");
  assert.ok(CSS.includes("@property --sheen{syntax:'<angle>';inherits:false;initial-value:0deg}"), "--sheen is not a registered angle");
  const hover = rule(".subitem:hover .ring,.subitem:focus-visible .ring");
  assert.ok(hover.includes("--sheen:26deg;"), "hover does not turn the light 26 degrees");
  assert.ok(hover.includes("rgba(255,179,92,.5)") && !hover.includes(".42)"), "the hover halo is not the designer's .5");
  const shadows = r => /box-shadow:([^;}]*)/.exec(r)[1].split(/,(?![^(]*\))/).length;
  for (const sel of [".ring", ".subitem:hover .ring,.subitem:focus-visible .ring", ".subitem.sel .ring", ".subitem.sel:hover .ring,.subitem.sel:focus-visible .ring"])
    assert.strictEqual(shadows(rule(sel)), 5, sel + " does not carry the five shadows every state carries");
  assert.ok(rule(".ring .heat").includes("mix-blend-mode:overlay;opacity:0;") && rule(".subitem.sel .ring .heat").includes("opacity:.95"), "the forge heat is not laid over the open ring");
  assert.ok(rule(".ring::after").includes("opacity:0;") && rule(".subitem.sel .ring::after").includes("opacity:1;"), "the open ring's face holds no ember");
  assert.ok(rule(".ring .rune").includes("color:var(--bone-dim);"), "the resting rune is not the rail's own colour");
  assert.ok(!/\.subitem(\.sel)?::before\{/.test(CSS), "the sub-item carries an ember bar of its own beside the rail item's");
  assert.ok(!/fonts\.googleapis|@import|url\(\s*["']?https?:/.test(CSS) && !/fonts\.googleapis/.test(HTML), "a font is fetched from the network");
  const root = CSS.slice(CSS.indexOf(":root{"), CSS.indexOf("}", CSS.indexOf(":root{")));
  for (const t of ["--ring:34px", "--band:3.5px", "--bz-hi:#F0CB8A", "--bz-lit:#D9AE6A", "--bz-mid:#A87638", "--bz-warm:#7E5526", "--bz-low:#6E4A1E", "--bz-deep:#3E2810", "--bz-edge:#1A1006"])
    assert.ok(root.includes(t), "the ring's token " + t + " is not on :root");
  const short = CSS.slice(CSS.indexOf("@media (max-height:780px){\n  .side{padding:8px 0 6px;gap:0}"));
  assert.ok(short.slice(0, short.indexOf("\n}")).includes(":root{--ring:30px;--band:3px}"), "the rings do not come down to 30px at the shortest rail");
  assert.ok(rule(".subitem .norse:empty::before").includes('content:"\\00a0"'), "Mods' empty caption does not keep its line");
  assert.ok(/\.subitem \.lbl\{[^}]*overflow-wrap:anywhere/.test(CSS), "a long label is clipped rather than taking a second line");
});

test("8. motion: the dropdown rolls, the flyout slides out from behind the rail, fades under reduced motion, and rests behind a game", () => {
  assert.ok(body("function subSync(page,opts){").includes("reveal(nav,on,opts&&opts.now?{now:true}:undefined);"), "the dropdown does not roll through reveal");
  assert.ok(body("function subWire(){").includes('<div class="subnav" id="\'+id+\'" data-page="\'+esc(page)+\'" style="display:none">'), "the dropdown is not drawn hidden for its roll");
  assert.ok(body("function goPage(name){").includes("try{subSync(name);}catch(_){}"), "a walk between halls does not roll the dropdowns");
  assert.ok(body("function subFlyClose(){").includes("fadeGhost(f.el);"), "the flyout vanishes rather than fading");
  assert.ok(CSS.includes(".subfly.open .subfly-panel{animation:subFlyIn .2s ease-out}")
    && CSS.includes("@keyframes subFlyIn{from{opacity:0;transform:translateX(-8px)}to{opacity:1;transform:none}}"), "the flyout does not slide 8px in over 0.2 s");
  assert.ok(rule(".subfly").includes("clip-path:inset(-40px -80px -60px 0)") && rule(".subfly").includes("position:fixed"), "the flyout is not clipped at the rail's edge alone");
  assert.ok(/z-index:30;/.test(rule(".subfly")), "the flyout is not above the halls and under the dialogs and the strip's menu");
  const rm = CSS.slice(CSS.indexOf("@media (prefers-reduced-motion:reduce){\n  .subfly.open .subfly-panel"));
  const rmb = rm.slice(0, rm.indexOf("\n}"));
  assert.ok(rmb.includes(".subfly.open .subfly-panel{animation:fadeIn .2s ease-out}"), "the flyout slides under reduced motion");
  assert.ok(rmb.includes(".ring{transition:box-shadow .2s var(--ease),filter .2s var(--ease)}") && rmb.includes("--sheen:0deg"), "the sheen turns under reduced motion");
  assert.ok(rmb.includes(".subitem.sel .ring::after{animation:none}"), "the ember breathes under reduced motion");
  assert.ok(rule(".subitem.sel .ring::after").includes("animation:ringEmber 4.8s ease-in-out infinite"), "the open ring's ember does not breathe like the rail's embers");
  const idle = CSS.slice(CSS.indexOf("body.idle .lm-bar.indeterminate .lm-fill,"));
  assert.ok(idle.slice(0, idle.indexOf("{")).includes("body.idle .subitem.sel .ring::after"), "the lit ring's ember keeps burning behind a game");
  assert.ok(!/\.style\.display\s*=/.test(APP.slice(APP.indexOf("/* SUBMENU-BEGIN"), APP.indexOf("/* SUBMENU-END */"))), "the sub-menus write style.display by hand");
});

test("9. the record: docs/sidebar.md carries the sub-menus and the gate runs both checks", () => {
  assert.ok(/^## Sub-menus \(1\.3\.0\)$/m.test(MAP), "docs/sidebar.md has no Sub-menus (1.3.0) section");
  for (const w of ["goSection(key,{solo:true})", "SUB_OPEN_MS", "fadeGhost", "sidebar_submenu_probe.js"]) assert.ok(MAP.includes(w), "docs/sidebar.md does not say " + w);
  assert.ok(GATE.includes('SUBMENU_SELFTEST="$UI_DIR/sidebar_submenu_selftest.js"') && GATE.includes('SUBMENU_PROBE="$UI_DIR/sidebar_submenu_probe.js"'), "the copy gate does not run the sub-menu checks");
});

console.log(failures.length === 0
  ? "sidebar submenu selftest: all " + passed + " rules pass"
  : "sidebar submenu selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAIL");
process.exit(failures.length === 0 ? 0 : 1);
