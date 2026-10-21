/* A realm chip's look, from issue 20, driven for real.
 *
 * WHY THIS EXISTS. A host can now put a rune in place of the first letter on a realm's chip,
 * and a colour on it, saved on the profile as two names (LookRune, LookColor). The chip has to
 * draw exactly as it did for a realm with no look, draw the rune and the colour for one with
 * a look, and fall back to the letter for anything it does not know, because the names come
 * off a file a hand can edit.
 *
 * THE CODE UNDER TEST is the block between the CHIPLOOK markers in app.js, evaluated with the
 * page's globals stood in for. Sentences come out of the real en.json and the colours out of
 * the real app.css.
 *
 *   1. No look: the letter, and no class beyond the ones the chip always had.
 *   2. A look: the rune's glyph, look-<colour> and rune on the chip.
 *   3. A name the lists do not hold (a hand edit, a removed entry) draws the letter and no
 *      colour, and nothing from the file reaches the markup.
 *   4. The lists: Elder Futhark only, none of the runes the sidebar's halls stand for, every
 *      rune named and explained in the catalog, eight colours each with a chip rule and a
 *      swatch rule in app.css drawn from a token the window already has.
 *   5. The wiring: the strip draws through chipHtml, the chip menu offers Customize look, the
 *      save goes through profiles.save with the two fields, and the host side's servers.list
 *      hands both back.
 *
 *     node scripts/ui/chip_look_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

const ROOT = path.join(__dirname, "..", "..");
const WEB = path.join(ROOT, "ValheimBakaLoader", "WebUI");
const APP = fs.readFileSync(path.join(WEB, "app.js"), "utf8").replace(/\r\n/g, "\n");
const CSS = fs.readFileSync(path.join(WEB, "app.css"), "utf8").replace(/\r\n/g, "\n");
const HTML = fs.readFileSync(path.join(WEB, "index.html"), "utf8").replace(/\r\n/g, "\n");
const KEYS = JSON.parse(fs.readFileSync(path.join(WEB, "i18n", "en.json"), "utf8")).keys || {};
const BRIDGE = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "Forms", "BlendWindow.Bridge.cs"), "utf8").replace(/\r\n/g, "\n");

let passed = 0;
const failures = [];
function test(name, body) {
  try { body(); passed++; console.log("  ok   " + name); }
  catch (problem) {
    failures.push(name);
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}
function between(from, to) {
  const a = APP.indexOf(from);
  assert.ok(a >= 0, "app.js no longer carries " + from);
  const b = APP.indexOf(to, a + from.length);
  assert.ok(b > a, "app.js no longer carries " + to + " after " + from);
  return APP.slice(a, b);
}
function line(prefix) {
  const a = APP.indexOf(prefix);
  assert.ok(a >= 0, "app.js no longer carries " + prefix);
  return APP.slice(a, APP.indexOf("\n", a));
}
function T(id, params) {
  const entry = KEYS[id];
  if (!entry || typeof entry.lore !== "string") throw new Error("the catalog has no sentence for " + id);
  return entry.lore.replace(/\{(\w+)\}/g, (m, k) => (params && k in params ? String(params[k]) : m));
}

function page() {
  const context = { T };
  vm.createContext(context);
  const code = [line("const esc=").replace(/^const /, "var "), between("/* CHIPLOOK-BEGIN", "/* CHIPLOOK-END */")]
    .join("\n").replace(/^const /gm, "var ");
  vm.runInContext(code, context, { filename: "app.js#chiplook" });
  for (const name of ["chipHtml", "chipInitial", "chipLookClass", "chipRune", "chipColor"])
    assert.strictEqual(typeof context[name], "function", name + " did not come out of the block");
  return context;
}
const REALM = { name: "Final Sunset", status: "Running", running: true, playersOnline: 2, active: true };
const initOf = html => (/<span class="srvinit">([^<]*)<\/span>/.exec(html) || [])[1];
const classOf = html => (/^<div class="([^"]*)"/.exec(html) || [])[1];

test("1. no look: the letter, and only the classes the chip always had", () => {
  const p = page();
  const html = p.chipHtml(REALM);
  assert.strictEqual(initOf(html), "F");
  assert.strictEqual(classOf(html), "srvchip active running");
  assert.strictEqual(classOf(p.chipHtml({ name: "midgard", status: "Stopped" })), "srvchip");
  assert.strictEqual(initOf(p.chipHtml({ name: "midgard" })), "M", "the letter is not upper case");
});

test("2. a look: the rune, its colour, and the rune class", () => {
  const p = page();
  const html = p.chipHtml(Object.assign({}, REALM, { lookRune: "algiz", lookColor: "frost" }));
  assert.strictEqual(initOf(html), "ᛉ");
  assert.strictEqual(classOf(html), "srvchip active running look-frost rune");
  const colourOnly = p.chipHtml(Object.assign({}, REALM, { lookColor: "moss" }));
  assert.strictEqual(initOf(colourOnly), "F");
  assert.strictEqual(classOf(colourOnly), "srvchip active running look-moss");
  const runeOnly = p.chipHtml(Object.assign({}, REALM, { lookRune: "othala" }));
  assert.strictEqual(initOf(runeOnly), "ᛟ");
  assert.strictEqual(classOf(runeOnly), "srvchip active running rune");
});

test("3. a name the lists do not hold draws the letter, and nothing of it reaches the markup", () => {
  const p = page();
  for (const look of [{ lookRune: "fehu" }, { lookRune: "<img src=x>" }, { lookColor: "\" onmouseover=\"x" }, { lookColor: "hotpink" }, { lookRune: "Algiz" }]) {
    const html = p.chipHtml(Object.assign({}, REALM, look));
    assert.strictEqual(initOf(html), "F", JSON.stringify(look) + " did not fall back to the letter");
    assert.strictEqual(classOf(html), "srvchip active running", JSON.stringify(look) + " reached the class list");
    assert.ok(!html.includes("onmouseover") && !html.includes("<img"), JSON.stringify(look) + " reached the markup");
  }
});

test("4. the lists: Elder Futhark, none of the halls' own runes, every one worded, eight colours drawn", () => {
  const p = page();
  const runes = vm.runInContext("CHIP_RUNES", p);
  const glyphs = Array.from(runes, r => r.g);
  for (const g of glyphs) {
    const c = g.codePointAt(0);
    assert.ok(c >= 0x16A0 && c <= 0x16EA, g + " is not a runic letter");
  }
  /* The rail lists five halls since 1.2.18; the four that became sections keep their runes on
     their section headings, and those are still the halls' own. */
  const nav = [...HTML.matchAll(/<div class="navitem[^"]*" data-page="[a-z]+"><span class="rune">(.)<\/span>/gu)].map(m => m[1]);
  const sectRunes = [...HTML.matchAll(/<div class="sect" id="sect-[a-z]+" data-sect="[a-z]+">\s*<div class="pagehead secthead">\s*<h1[^>]*><span class="r">(.)<\/span>/gu)].map(m => m[1]);
  nav.push(...sectRunes);
  assert.strictEqual(nav.length, 9, "the sidebar's halls and the sections that were halls could not be read: " + nav.join(""));
  /* sowilo is drawn as the long branch form (U+16CB) in the sidebar, and U+16CA is the same rune. */
  const halls = new Set(nav.concat(nav.includes("ᛋ") ? ["ᛊ"] : []));
  for (const g of glyphs) assert.ok(!halls.has(g), g + " already stands for one of the halls");
  assert.ok(!glyphs.includes("ᛈ"), "perthro is offered, and nobody agrees what it means");
  assert.strictEqual(new Set(glyphs).size, glyphs.length, "a rune is offered twice");
  for (const r of runes) {
    assert.strictEqual(r.nameId, "realm.look.rune." + r.key);
    assert.ok(KEYS[r.nameId], r.key + " has no name in the catalog");
    assert.ok(KEYS[r.tipId], r.key + " has no meaning in the catalog");
  }
  const colours = Array.from(vm.runInContext("CHIP_COLORS", p), c => {
    assert.strictEqual(c.nameId, "realm.look.color." + c.key);
    return c.key;
  });
  assert.strictEqual(colours.length, 8);
  const tokens = new Set([...CSS.matchAll(/--([a-z-]+):/g)].map(m => m[1]));
  for (const c of colours) {
    assert.ok(KEYS["realm.look.color." + c], c + " has no name in the catalog");
    const chip = new RegExp("\\.srvchip\\.look-" + c + " \\.srvinit\\{color:var\\(--([a-z-]+)\\)").exec(CSS);
    assert.ok(chip, c + " has no chip rule in app.css");
    assert.ok(tokens.has(chip[1]), c + " draws with --" + chip[1] + ", which the window does not define");
    assert.ok(new RegExp("\\.lookcolor\\.look-" + c + " \\.sw\\{background:var\\(--" + chip[1] + "\\)\\}").test(CSS), c + " has no swatch rule, or its swatch is not its chip's colour");
  }
});

test("5. the wiring: the strip, the menu, the save and the host side", () => {
  const render = between("function renderServerChips(){", "\n/* The strip is never allowed");
  assert.ok(render.includes("let html=list.map(chipHtml).join(\"\");"), "the strip does not draw through chipHtml");
  const menu = between("function serverChipMenu(", "\nasync function renameServer(");
  assert.ok(menu.includes('{r:"ᚹ",label:T("realm.menu.look"),fn:()=>chipLookFlow(name)}'), "the chip menu does not offer Customize look");
  const save = between("async function saveChipLook(", "\nasync function renameServer(");
  assert.ok(save.includes('rpc("profiles.get",{name})') && save.includes('rpc("profiles.save",{name,prefs})'), "the look is not saved through the profile");
  assert.ok(save.includes("LookRune:rune||null,LookColor:color||null"), "the two look fields are not what is written");
  const list = BRIDGE.slice(BRIDGE.indexOf("private object BuildServersList()"));
  assert.ok(/lookRune = storedByName\.TryGetValue\(name, out var look\) \? look\.LookRune : null,/.test(list), "servers.list does not hand back the rune");
  assert.ok(/lookColor = look\?\.LookColor,/.test(list), "servers.list does not hand back the colour");
  assert.ok(T("realm.menu.look").startsWith("Customize look"));
});

console.log("");
if (failures.length) {
  console.log("chip look selftest: " + failures.length + " FAILED, " + passed + " passed");
  process.exit(1);
}
console.log("chip look selftest: all " + passed + " rules pass");
