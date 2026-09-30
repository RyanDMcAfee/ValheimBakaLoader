/* Every small word in the window, held to WCAG AA against every ground it can sit on.
 *
 * WHY THIS EXISTS. The interface is a dark one and most of its words are 9 to 11 pixels of
 * mono, which is exactly the shape a colour token gets away with being too dim in: it reads
 * fine in a screenshot at 200% on the machine it was designed on, and it is a grey smudge on
 * a laptop in a lit room. Five tokens were measured in 1.2.6 and four of them failed:
 * bone-dim reached 5.5 on the darkest card and only 4.2 on the lightest one, bone-faint was
 * 2.4 everywhere, cold 3.7, blood 3.8, and the bronze-hi a microlabel's rune was drawn in
 * came out at 2.5. Nothing in the suite had an opinion about any of it.
 *
 * AND WHY IT GOT STRICTER. The first cut only read a rule that carried BOTH a font-size in
 * pixels AND a colour token, in that same rule, in app.css. Four whole classes of small word
 * walked past it, and three of them were live in the tree it passed:
 *
 *   A SIZE ON THE PARENT. `.term .dbg` is `color:var(--bone-faint-text);opacity:.7` and its
 *   11px comes from `.term`. 146 rules set a colour with no size beside them. So a size is
 *   now inherited down a descendant selector, and a rule whose size cannot be found anywhere
 *   is LISTED rather than passed.
 *
 *   A SIZE WRITTEN AS A TOKEN. `font-size:var(--t11)` is 11px and the old regex only read
 *   digits, so every rule using the scale tokens was invisible.
 *
 *   OPACITY. The old reader composited an rgba alpha and ignored `opacity` entirely, which is
 *   the whole difference between `.term .dbg` measuring 4.55 and measuring 3.84.
 *
 *   A TOKEN THAT DOES NOT EXIST. `.wtab.on` was shipped with `color:var(--iron-deepest)`, a
 *   name declared nowhere, so the declaration is invalid at computed-value time and the label
 *   fell back to inherited bone: pale bone on an ember pill, 2.0. An unresolvable token is
 *   now a FAILURE and not a skip, whatever size the rule is, because it is a bug either way.
 *
 *   AND COLOURS SET FROM CODE. app.js sets `color:var(--blood)` in inline styles and through
 *   `element.style.color`, on `.subval` and `.fieldnote` words. A gate that reads only the
 *   stylesheet says nothing about them, so they are read out of app.js too and held to the
 *   same floor.
 *
 * WHAT IT CHECKS NOW. Every rule in app.css that ends up with a colour and a font-size under
 * 14px, its own or inherited; plus every colour token app.js sets at runtime. The token is
 * resolved through the :root blocks (chains and all) and composited with its own alpha and
 * its rule's opacity. The ground is the rule's OWN background when it paints an opaque one
 * (including each stop of a linear-gradient, so a pill is measured against the pill), and
 * otherwise every ground in the stylesheet's tonal ladder: pass on all of them and the word
 * is legible wherever it lands.
 *
 * AND WHY IT GOT STRICTER AGAIN, in 1.2.6's batch D. Five blind spots, four of them hiding a
 * live word under 4.5 in the tree this gate passed:
 *
 *   OPACITY ON AN ANCESTOR was never composed. `tbody tr.dim` dims a whole roster row to .42
 *   and the cells inside it carry the colour; `.togglerow.gated` dims to .45 and `.tl` carries
 *   it; `.field.gated label` dims the label `.field label` coloured. Each measured as though
 *   it had never been dimmed. On screen they are 3.40, 3.70 and 2.69.
 *
 *   `opacity:70%` parsed as 70 and clamped to 1, so a percentage read as no dimming at all.
 *
 *   A COMPOUND ON ONE ELEMENT (`.wtab.on`) inherited no size, because the size walk split on
 *   spaces and a selector with no space had nothing to walk.
 *
 *   A COLOUR THE PARSER CANNOT READ (`rgb(a b c / 70%)`, `hsl(...)`) was listed rather than
 *   failed, which is the same as not having the rule. Both forms are read now, and anything
 *   still unreadable in a word-bearing rule FAILS.
 *
 *   THE @keyframes STRIP stopped at the first inner brace, so every percentage stop after the
 *   first leaked into the rule list looking like a selector with a body.
 *
 * WHAT IT DOES NOT CHECK, out loud. A rule with no font-size anywhere this gate can follow is
 * PRINTED as a list rather than counted as a pass, because a gate that quietly skips what it
 * cannot read is a gate that goes green on the day the tokens stop being used.
 *
 *     node scripts/ui/contrast_gate.js [app.css] [app.js]
 *
 * Exits non-zero when any resolvable pair is under 4.5, or when a colour names a token that
 * is not declared.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const ROOT = path.join(__dirname, "..", "..");
const CSS = process.argv[2] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.css");
const JS = process.argv[3] || path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const SOURCE = fs.readFileSync(CSS, "utf8");
const SCRIPT = fs.existsSync(JS) ? fs.readFileSync(JS, "utf8") : "";

/** AA for body text. The same floor for every size, because none of these are large text. */
const FLOOR = 4.5;

/** Anything at or above this is not small text and is out of scope. */
const SMALL_UNDER = 14;

/**
 * Rules that are pure ornament: a glyph or a bar carrying no word, where the colour is
 * decoration rather than information. Each entry names the selector and says why, because an
 * allowlist with no reason on it is a list of things nobody checked.
 */
const ORNAMENT = [
  // The middle dot between BAKALOADER and the server's name in the title bar. A separator
  // glyph and not a word: the two names either side of it are the words, and each is held on
  // its own row here. WCAG 1.4.3 is about text and images of text; a punctuation mark whose
  // removal costs nothing but a space is neither.
  { selector: ".wordmark .dot", why: "a separator dot, not a word" },
  // The rune on a palette row that cannot be pressed. Here the DIMMING is the message, which
  // is 1.4.3's own exception for an inactive control, and the row's words are dimmed with it.
  { selector: ".pitem.disabled:hover .r", why: "a rune on a row that cannot be pressed" },
  // Its sibling in the right-click menu, same rule and same reason: the row is disabled, its
  // words are dimmed to say so, and the rune in front of them carries nothing they do not.
  { selector: ".ctxmenu .ci.disabled:hover .r", why: "a rune on a menu row that cannot be pressed" },
  // NOT here any more: .formsec .r, which was forgiven as ornament while drawing a rune at
  // 2.1 in front of a heading. It is --bronze-text now, like every other rune beside a label,
  // and it is measured with the rest. And .lm-bar, which never needed forgiving: it sets a
  // background and no colour, so it was never a word-bearing rule in the first place, and an
  // allowlist entry that forgives nothing is an entry nobody can check.
];

/* ------------------------------------------------------------------ the colour maths */

function channel(value) {
  const c = value / 255;
  return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
}

function luminance([r, g, b]) {
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

function ratio(fg, bg) {
  const a = luminance(fg);
  const b = luminance(bg);
  const hi = Math.max(a, b);
  const lo = Math.min(a, b);
  return (hi + 0.05) / (lo + 0.05);
}

/** One colour as [r, g, b, a], or null when it is not one this gate can read. */
function parseColour(text) {
  const value = String(text || "").trim().toLowerCase();

  let m = /^#([0-9a-f]{6})$/.exec(value);
  if (m) return [parseInt(m[1].slice(0, 2), 16), parseInt(m[1].slice(2, 4), 16),
    parseInt(m[1].slice(4, 6), 16), 1];

  m = /^#([0-9a-f]{3})$/.exec(value);
  if (m) return [parseInt(m[1][0] + m[1][0], 16), parseInt(m[1][1] + m[1][1], 16),
    parseInt(m[1][2] + m[1][2], 16), 1];

  m = /^rgba?\(\s*([0-9.]+)\s*,\s*([0-9.]+)\s*,\s*([0-9.]+)\s*(?:,\s*([0-9.]+%?)\s*)?\)$/.exec(value);
  if (m) return [Math.round(+m[1]), Math.round(+m[2]), Math.round(+m[3]), alpha(m[4])];

  /* The space-separated form, which is the one a modern stylesheet is written in and the one
     this parser used to hand back null for: rgb(158 153 142 / 70%). A null there was a word
     the gate listed instead of measuring, which is the same as not having the rule. */
  m = /^rgba?\(\s*([0-9.]+)\s+([0-9.]+)\s+([0-9.]+)\s*(?:\/\s*([0-9.]+%?)\s*)?\)$/.exec(value);
  if (m) return [Math.round(+m[1]), Math.round(+m[2]), Math.round(+m[3]), alpha(m[4])];

  /* hsl, both the comma form and the space form. Same reason. */
  m = /^hsla?\(\s*([0-9.]+)(?:deg)?\s*[, ]\s*([0-9.]+)%\s*[, ]\s*([0-9.]+)%\s*(?:[,\/]\s*([0-9.]+%?)\s*)?\)$/
    .exec(value);
  if (m) {
    const [r, g, b] = fromHsl(+m[1], +m[2] / 100, +m[3] / 100);
    return [r, g, b, alpha(m[4])];
  }

  if (value === "#fff" || value === "white") return [255, 255, 255, 1];
  if (value === "#000" || value === "black") return [0, 0, 0, 1];
  // A real value, and one this stylesheet uses on purpose: the config editor's find layer
  // draws the text transparent and paints only the mark behind it. Read rather than failed,
  // and then skipped below with everything else that is not on screen.
  if (value === "transparent") return [0, 0, 0, 0];

  return null;
}

/** An alpha written as a number or as a percentage, or 1 when it was not written at all. */
function alpha(text) {
  if (text === undefined || text === null || text === "") return 1;
  const value = String(text).trim();
  const n = value.endsWith("%") ? parseFloat(value) / 100 : parseFloat(value);
  return Number.isFinite(n) ? Math.max(0, Math.min(1, n)) : 1;
}

/** hsl to rgb, so an hsl() colour is measured rather than listed. */
function fromHsl(h, s, l) {
  const hue = ((h % 360) + 360) % 360 / 360;
  if (s === 0) { const v = Math.round(l * 255); return [v, v, v]; }
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
  const p = 2 * l - q;
  const one = (t) => {
    if (t < 0) t += 1;
    if (t > 1) t -= 1;
    if (t < 1 / 6) return p + (q - p) * 6 * t;
    if (t < 1 / 2) return q;
    if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
    return p;
  };
  return [Math.round(one(hue + 1 / 3) * 255), Math.round(one(hue) * 255),
    Math.round(one(hue - 1 / 3) * 255)];
}

/** A translucent colour laid over an opaque one, which is what the eye actually sees. */
function over(fg, bg) {
  const a = fg.length > 3 ? fg[3] : 1;
  if (a >= 1) return [fg[0], fg[1], fg[2]];
  return [0, 1, 2].map(i => Math.round(fg[i] * a + bg[i] * (1 - a)));
}

/* ------------------------------------------------------------------ reading the stylesheet */

/**
 * Every @keyframes and @font-face block removed WHOLE, braces counted.
 * <p>
 * The one-liner this replaces was `@(?:font-face|keyframes)[^{]*{[^}]*}`, which stops at the
 * first inner brace: a keyframes block is a set of nested blocks, so the strip ended inside
 * the first stop and every stop after it (`0%`, `to`, `100%`) was left behind looking exactly
 * like a selector with a body. Those leaked into the rule list and any colour inside one was
 * measured against a selector that does not exist.
 */
function stripAtBlocks(text, names) {
  const pattern = new RegExp("@(?:" + names.join("|") + ")\\b[^{]*\\{", "g");
  let out = text;
  for (;;) {
    pattern.lastIndex = 0;
    const m = pattern.exec(out);
    if (!m) return out;
    let depth = 0;
    let end = -1;
    for (let i = m.index + m[0].length - 1; i < out.length; i++) {
      if (out[i] === "{") depth++;
      else if (out[i] === "}") {
        depth--;
        if (depth === 0) { end = i + 1; break; }
      }
    }
    if (end < 0) return out.slice(0, m.index);
    out = out.slice(0, m.index) + out.slice(end);
  }
}

/** Comments out, so a colour inside a note is never read as a rule. */
const CLEAN = SOURCE.replace(/\/\*[\s\S]*?\*\//g, "");

/** Every token the :root blocks declare, by name, before any var() is resolved. */
function rawTokens() {
  const tokens = new Map();
  const roots = CLEAN.match(/:root[^{]*\{[^}]*\}/g) || [];
  for (const block of roots) {
    const body = block.slice(block.indexOf("{") + 1, block.lastIndexOf("}"));
    for (const decl of body.split(";")) {
      const at = decl.indexOf(":");
      if (at < 0) continue;
      const name = decl.slice(0, at).trim();
      if (!name.startsWith("--")) continue;
      tokens.set(name, decl.slice(at + 1).trim());
    }
  }
  return tokens;
}

const TOKENS = rawTokens();

/** Whether a name is declared at all. An undeclared one is a bug, not a thing to skip. */
function declared(name) {
  return TOKENS.has(name);
}

/** One token, followed through however many var() hops it takes, or null. */
function resolve(name, seen) {
  const chain = seen || new Set();
  if (chain.has(name)) return null;
  chain.add(name);

  const value = TOKENS.get(name);
  if (value === undefined) return null;

  const hop = /^var\(\s*(--[A-Za-z0-9-]+)\s*\)$/.exec(value.trim());
  if (hop) return resolve(hop[1], chain);

  return parseColour(value);
}

/** One token as a number of pixels, for the scale tokens a font-size can be written with. */
function resolvePx(name, seen) {
  const chain = seen || new Set();
  if (chain.has(name)) return null;
  chain.add(name);

  const value = TOKENS.get(name);
  if (value === undefined) return null;

  const hop = /^var\(\s*(--[A-Za-z0-9-]+)\s*\)$/.exec(value.trim());
  if (hop) return resolvePx(hop[1], chain);

  const px = /^([0-9.]+)px$/.exec(value.trim());
  return px ? +px[1] : null;
}

/**
 * The GROUNDS, which is not the same thing as every token a background is painted with.
 * <p>
 * The stylesheet's own tonal ladder (surface-0 to surface-3) plus the three iron tones are
 * what ordinary words sit on: the app chrome, the cards, the raised rows, the menus and the
 * modals. An accent token is also painted as a background here and there, on a pill or a
 * chip or a badge, and those carry their own explicit text colour chosen against that one
 * accent: measuring bone against amber because a pill exists says nothing about anything.
 * Such a rule is measured against ITS OWN background instead, further down.
 * <p>
 * The list is still DERIVED rather than written down: a ladder token that stopped being
 * painted anywhere would drop out of it, and a new one would join.
 */
const GROUND = /^--(?:surface-[0-9]+|iron|iron-deep|iron-deeper)$/;

function surfaces() {
  const found = new Map();
  const paints = CLEAN.match(/background(?:-color)?\s*:[^;}]+/g) || [];
  for (const paint of paints) {
    for (const hit of paint.match(/var\(\s*--[A-Za-z0-9-]+\s*\)/g) || []) {
      const name = /--[A-Za-z0-9-]+/.exec(hit)[0];
      if (!GROUND.test(name)) continue;
      const colour = resolve(name);
      // An opaque ground only. A translucent one sits over something else and is not a
      // surface in its own right.
      if (colour && colour[3] >= 1) found.set(name, colour);
    }
  }
  return [...found.entries()].sort((a, b) => a[0].localeCompare(b[0]));
}

const SURFACES = surfaces();

/**
 * Every rule in the stylesheet, one entry per comma-separated selector, flattened out of any
 * @media block so a rule inside one is read like any other.
 */
function allRules() {
  const rules = [];
  const flat = stripAtBlocks(CLEAN, ["font-face", "keyframes", "-webkit-keyframes", "supports"])
    .replace(/@media[^{]*\{/g, "");
  const pattern = /([^{}]+)\{([^{}]*)\}/g;
  let m;
  while ((m = pattern.exec(flat)) !== null) {
    const head = m[1].trim().replace(/\s+/g, " ");
    const body = m[2];
    if (!head || head.startsWith("@") || head.includes(":root")) continue;
    for (const one of head.split(",")) {
      const selector = one.trim().replace(/\s+/g, " ");
      if (selector) rules.push({ selector, body });
    }
  }
  return rules;
}

const RULES = allRules();

/** The font-size one rule declares, in pixels, or null. Tokens resolved. */
function ownSize(body) {
  const px = /font-size\s*:\s*([0-9.]+)px/.exec(body);
  if (px) return +px[1];
  const token = /font-size\s*:\s*var\(\s*(--[A-Za-z0-9-]+)\s*\)/.exec(body);
  if (token) return resolvePx(token[1]);
  return null;
}

/**
 * Every selector that declares a size, so a rule with none of its own can inherit one. The
 * SMALLEST wins where a selector is written more than once: a word is only as legible as its
 * smallest rendering.
 */
const SIZES = new Map();
for (const rule of RULES) {
  const size = ownSize(rule.body);
  if (size === null) continue;
  const had = SIZES.get(rule.selector);
  if (had === undefined || size < had) SIZES.set(rule.selector, size);
}

/**
 * The size a rule's words are really drawn at: its own, or the nearest ancestor's down its
 * own descendant selector. `.term .dbg` has no size and `.term` has 11px, so the words in
 * the first are 11px. Walked from the closest ancestor outwards, because the closest one is
 * the one that would win.
 */
function effectiveSize(selector) {
  for (const candidate of sizeCandidates(selector)) {
    const said = SIZES.get(candidate);
    if (said !== undefined) return { size: said, from: candidate };
  }
  return null;
}

/**
 * One compound part written every simpler way that still matches the same element, most
 * specific first: `td.mono:hover` gives td.mono:hover, td.mono, td, .mono.
 * <p>
 * This is the blind spot that hid `.wtab.on`. The old reader split the selector on SPACES and
 * walked the ancestors, so a selector with no space in it had nothing to walk: a compound on
 * one element inherited no size at all, was listed as unresolvable and went past the gate. The
 * pill it drew was 11px of pale bone on ember at 2.0.
 */
function sameElementForms(part) {
  const bases = [part];
  let p = part;
  for (;;) {
    const at = p.lastIndexOf(":");
    if (at <= 0) break;
    p = p.slice(0, at);
    bases.push(p);
  }

  const forms = [];
  for (const base of bases) {
    const at = base.indexOf(".");
    const tag = at < 0 ? base : base.slice(0, at);
    const classes = at < 0 ? [] : base.slice(at).split(".").filter(Boolean).map(c => "." + c);
    forms.push(base);
    for (let cut = classes.length - 1; cut >= 1; cut--) forms.push(tag + classes.slice(0, cut).join(""));
    if (tag) forms.push(tag);
    for (const one of classes) forms.push(one);
  }
  return [...new Set(forms)].filter(Boolean);
}

/**
 * Every selector a size could be coming from, most specific first: this rule, the same element
 * written more simply, then each ancestor the same way. A word is only as legible as its
 * smallest rendering, and SIZES already holds the smallest per selector.
 */
/**
 * Elements whose size comes from an ancestor no selector in this stylesheet names. `table` is
 * sized once and every cell in it inherits that, and nothing here is ever written as
 * `table tbody td`. Written down rather than guessed, because the list is short and each entry
 * is a real HTML containment rule.
 */
/**
 * The containers HTML puts an element inside, whatever the stylesheet says. Used the other way
 * round from IMPLIED_ANCESTORS: to find a rule that DIMS a container this element must be in.
 */
const IMPLIED_CONTAINERS = {
  td: ["tr", "tbody", "thead", "table"],
  th: ["tr", "thead", "tbody", "table"],
  tr: ["tbody", "thead", "tfoot", "table"],
  option: ["select", "optgroup"],
  optgroup: ["select"],
  li: ["ul", "ol"],
};

const IMPLIED_ANCESTORS = {
  table: [], thead: ["table"], tbody: ["table"], tfoot: ["table"],
  tr: ["table"], td: ["table"], th: ["table"],
  li: ["ul", "ol"], option: ["select"], optgroup: ["select"],
};

function sizeCandidates(selector) {
  const parts = selector.split(" ");
  const out = [selector];
  const last = parts[parts.length - 1];
  for (const form of sameElementForms(last)) {
    out.push(parts.slice(0, -1).concat(form).join(" "));
    out.push(form);
  }
  /* The same rule written with a simpler ANCESTOR and the same element: `.field.gated label`
     takes its 9.5px from `.field label`, and a walk that only simplified the ancestor on its
     own (to `.field`) never asked that question. Crossed rather than nested, because either
     half can be the one that is written more simply. */
  if (parts.length > 1) {
    const lastForms = sameElementForms(last);
    for (const form of sameElementForms(parts[parts.length - 2])) {
      const head = parts.slice(0, -2).concat(form);
      for (const tail of lastForms) out.push(head.concat(tail).join(" "));
    }
  }
  for (let cut = parts.length - 1; cut >= 1; cut--) {
    const prefix = parts.slice(0, cut);
    out.push(prefix.join(" "));
    for (const form of sameElementForms(prefix[prefix.length - 1]))
      out.push(prefix.slice(0, -1).concat(form).join(" "));
  }
  for (const part of parts) {
    const tag = /^[A-Za-z][A-Za-z0-9-]*/.exec(part);
    if (!tag) continue;
    for (const implied of IMPLIED_ANCESTORS[tag[0]] || []) out.push(implied);
  }
  return [...new Set(out)];
}

/**
 * The opacity a rule puts on its own words, or 1.
 * <p>
 * Percentages read as percentages. `opacity:70%` is legal CSS and the old reader parsed the 70
 * and clamped it to 1, which is the difference between reading a word at 0.7 and pretending it
 * was never dimmed at all.
 */
function ownOpacity(body) {
  const m = /(?:^|[;{\s])opacity\s*:\s*([0-9.]+%?)/.exec(body);
  if (!m) return 1;
  const raw = m[1].endsWith("%") ? parseFloat(m[1]) / 100 : parseFloat(m[1]);
  return Number.isFinite(raw) ? Math.max(0, Math.min(1, raw)) : 1;
}

/**
 * Selectors whose opacity is NOT composed onto the words underneath, and why. WCAG 1.4.3
 * exempts text that is part of an inactive user interface component, and that is the whole of
 * what is in here: a control a host cannot press right now, dimmed to say so. Everything else
 * that dims a word is composed and measured.
 */
const DIMMED_OK = [
  { selector: ".btn:disabled", why: "an inactive control: WCAG 1.4.3 exempts one, and the dimming IS the message" },
  { selector: ".btn:disabled:hover", why: "the same button under a pointer that cannot press it" },
  { selector: ".field input:disabled", why: "an inactive input, same exemption" },
  { selector: ".field select:disabled", why: "an inactive select, same exemption" },
  { selector: ".togglerow select:disabled", why: "an inactive select, same exemption" },
  { selector: ".togglerow.gated .toggle", why: "a switch that cannot be moved yet, same exemption" },
  // `.togglerow.gated select` was here and is not any more. .gated is put on exactly three
  // rows, the Detailed log row, the auto-update row and the kill-scope radios, and not one
  // of them holds a select; nothing anywhere sets disabled on one either. An exemption for
  // a control that does not exist cannot be proved and cannot be read, so it is gone. The
  // day a gated select really is drawn, this gate flags it and the entry can come back with
  // a real thing behind it.
  { selector: ".modal input:disabled", why: "an inactive input in a dialog, same exemption" },
  { selector: ".lm-act:disabled", why: "a language row's button while a download runs, same exemption" },
  { selector: ".pitem.disabled", why: "a palette row that cannot be pressed, same exemption" },
  { selector: ".ctxmenu .ci.disabled", why: "a menu row that cannot be pressed, same exemption" },
];

/**
 * True when a selector's last part draws a PSEUDO-ELEMENT rather than the element itself.
 * `.modal .dsec::after` is the hairline beside a dialog heading: its opacity has nothing to do
 * with the heading's words, and composing it onto them said the heading was drawn at 0.6.
 */
function isPseudoElement(selector) {
  const last = selector.split(" ").pop();
  return /::|:(?:before|after|placeholder|selection|marker|backdrop|first-line|first-letter)\b/
    .test(last);
}

/** Every selector that dims, and by how much. The SMALLEST wins: worst case is the reading. */
const OPACITIES = new Map();
for (const rule of RULES) {
  if (DIMMED_OK.some(d => d.selector === rule.selector)) continue;
  if (isPseudoElement(rule.selector)) continue;
  const value = ownOpacity(rule.body);
  if (value >= 1) continue;
  // An opacity of 0 is a state that is NOT ON SCREEN (.atlas-msg.hidden, a panel before it
  // fades in), not a dimmed word. Composing it made every word in the panel measure 1.00
  // against everything, which is a finding about nothing.
  if (value === 0) continue;
  const had = OPACITIES.get(rule.selector);
  if (had === undefined || value < had) OPACITIES.set(rule.selector, value);
}

/** The simple selectors one compound part is made of, as a set: ".seg.dim" -> {.seg, .dim}. */
function simpleParts(part) {
  const out = new Set();
  for (const hit of part.match(/^[A-Za-z][A-Za-z0-9-]*|[.#:][A-Za-z0-9_-]+(?:\([^)]*\))?/g) || [])
    out.add(hit);
  return out;
}

/** True when `special` matches every element `general` does and possibly fewer. */
function specialises(general, special) {
  const g = general.split(" ");
  const p = special.split(" ");
  if (g.length !== p.length) return false;
  for (let i = 0; i < g.length; i++) {
    const want = simpleParts(g[i]);
    const have = simpleParts(p[i]);
    for (const one of want) if (!have.has(one)) return false;
  }
  return true;
}

/**
 * Everything that dims this rule's words, multiplied: its own opacity, any ANCESTOR's, and any
 * more specific rule on the SAME element.
 * <p>
 * Neither of the last two was composed before, and that is where the four remaining words in
 * the window under 4.5 were hiding. `tbody tr.dim` sets opacity:.42 and the cells inside it
 * carry the colour; `.togglerow.gated` sets .45 and `.tl` carries the colour; `.field.gated
 * label` sets .5 over the colour `.field label` carries. Each one measured as if it were never
 * dimmed, and each one was a readable small word at 2.3 to 3.7 on screen.
 */
function effectiveOpacity(selector, body) {
  /* Its own body, and any OTHER rule written against the same selector: one block carries the
     colour and a later one carries the dimming often enough that reading only this rule's body
     means reading half of it. Smallest wins, the way SIZES does, because worst case is the
     reading a host gets. */
  const elsewhere = OPACITIES.has(selector) ? OPACITIES.get(selector) : 1;
  let value = Math.min(ownOpacity(body), elsewhere);
  const seen = new Set([selector]);

  const parts = selector.split(" ");
  for (let cut = parts.length - 1; cut >= 1; cut--) {
    const prefix = parts.slice(0, cut).join(" ");
    for (const [dimmed, amount] of OPACITIES) {
      if (seen.has(dimmed)) continue;
      if (dimmed === prefix || specialises(prefix, dimmed)) {
        // A dimmed ancestor only counts when it really is an ancestor of THIS rule, which for
        // a specialising selector means the rest of the chain still matches.
        if (dimmed !== prefix && !selector.startsWith(prefix + " ")) continue;
        seen.add(dimmed);
        value *= amount;
      }
    }
  }

  for (const [dimmed, amount] of OPACITIES) {
    if (seen.has(dimmed)) continue;
    if (specialises(selector, dimmed)) { seen.add(dimmed); value *= amount; }
  }

  /* And the containers HTML gives this element whether a selector names them or not. A cell is
     inside a row and a row is inside a table, and nothing in this stylesheet is ever written
     `table tbody tr td`: `tbody tr.dim{opacity:.42}` dims every word in an offline viking's row
     and `td.mono` is where the colour is. That word measures 3.40 on screen and measured 5.2
     here until this ran. Sound because it is HTML containment and not a guess: the list is in
     IMPLIED_CONTAINERS and every entry is an element that can only appear inside those. */
  const tagsOf = (sel) => sel.split(" ")
    .map(part => (/^[A-Za-z][A-Za-z0-9-]*/.exec(part) || [""])[0])
    .filter(Boolean);

  const mine = tagsOf(selector);
  const tag = mine.length ? mine[mine.length - 1] : "";
  const containers = IMPLIED_CONTAINERS[tag] || [];
  if (containers.length) {
    for (const [dimmed, amount] of OPACITIES) {
      if (seen.has(dimmed)) continue;
      const theirs = tagsOf(dimmed);
      if (!theirs.length) continue;
      // Every element the dimming rule names has to be one this element can sit inside.
      if (!theirs.every(t => t === tag || containers.includes(t))) continue;
      // And it must not name a DIFFERENT section of the table than this rule does: a thead th
      // is never inside a tbody, and composing `tbody tr.dim` onto it said the header row was
      // drawn at 0.42.
      const SECTIONS = ["thead", "tbody", "tfoot"];
      const mySection = mine.find(t => SECTIONS.includes(t));
      const theirSection = theirs.find(t => SECTIONS.includes(t));
      if (mySection && theirSection && mySection !== theirSection) continue;
      seen.add(dimmed);
      value *= amount;
    }
  }

  return Math.max(0, Math.min(1, value));
}

/**
 * The grounds a rule's own background paints, when it paints an opaque one. Every stop of a
 * gradient counts: a pill whose ink passes against its top stop and fails against its bottom
 * one is a pill with an unreadable bottom half.
 */
function ownGrounds(body) {
  const paint = /(?:^|[;{\s])background(?:-color|-image)?\s*:\s*([^;}]+)/.exec(body);
  if (!paint) return null;
  const value = paint[1];
  const found = [];

  for (const hit of value.match(/var\(\s*--[A-Za-z0-9-]+\s*\)/g) || []) {
    const name = /--[A-Za-z0-9-]+/.exec(hit)[0];
    const colour = resolve(name);
    if (colour && colour[3] >= 1) found.push([name, [colour[0], colour[1], colour[2]]]);
  }
  for (const hit of value.match(/#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b|rgba?\([^)]*\)/g) || []) {
    const colour = parseColour(hit);
    if (colour && colour[3] >= 1) found.push([hit, [colour[0], colour[1], colour[2]]]);
  }
  return found.length ? found : null;
}

/* ------------------------------------------------------------------ the verdict */

const failures = [];
const unresolved = [];
const invisible = [];
let measured = 0;
let held = 0;

for (const rule of RULES) {
  if (ORNAMENT.some(o => o.selector === rule.selector)) continue;

  const colour = /(?:^|[;{\s])color\s*:\s*([^;}]+)/.exec(rule.body);
  if (!colour) continue;
  const text = colour[1].trim();

  // An undeclared token first, and whatever the size is. The declaration is invalid at
  // computed-value time, so the words fall back to whatever they inherit, which is never the
  // colour anybody chose.
  const token = /^var\(\s*(--[A-Za-z0-9-]+)\s*\)$/.exec(text);
  if (token && !declared(token[1])) {
    failures.push(rule.selector + ": color names " + token[1] + ", which is declared nowhere,"
      + " so the declaration is dropped and the words inherit some other colour");
    continue;
  }

  const size = effectiveSize(rule.selector);
  if (!size) {
    unresolved.push(rule.selector + " { color:" + text + " } (no font-size here or on any"
      + " ancestor this gate can follow)");
    continue;
  }
  if (size.size >= SMALL_UNDER) continue;

  let fg = token ? resolve(token[1]) : parseColour(text);
  if (!fg) {
    /* A word-bearing rule whose colour this gate cannot read is a FAILURE and not a note.
       Listing it was the same as having no rule: the value goes past, the word ships, and the
       list at the bottom is read by nobody. Either the parser learns the form or the
       stylesheet writes it a form the parser knows. Only a rule with nothing to measure at
       all (no size anywhere) is listed. */
    failures.push(rule.selector + " at " + size.size + "px: color:" + text
      + " is not a colour this gate can read, so nothing about it has been measured"
      + (token ? " (the token resolves to something unreadable)" : ""));
    continue;
  }

  const opacity = effectiveOpacity(rule.selector, rule.body);
  const seenAlpha = (fg.length > 3 ? fg[3] : 1) * opacity;
  // Nothing on screen: a hidden state's own rule, or a layer that draws its text transparent
  // and paints only the mark behind it. Counted out loud rather than skipped in silence.
  if (seenAlpha <= 0) { invisible.push(rule.selector + " { color:" + text + " }"); continue; }
  if (opacity < 1) fg = [fg[0], fg[1], fg[2], seenAlpha];

  const grounds = ownGrounds(rule.body) || SURFACES;
  held++;
  for (const [name, bg] of grounds) {
    measured++;
    const seen = ratio(over(fg, bg), bg);
    if (seen < FLOOR) {
      failures.push(rule.selector + " at " + size.size + "px"
        + (size.from === rule.selector ? "" : " (inherited from " + size.from + ")")
        + (opacity < 1 ? " at opacity " + opacity : "")
        + ": " + text + " on " + name + " is " + seen.toFixed(2) + ", under " + FLOOR);
    }
  }
}

/* ---------------------------------------------------------- and the ones set from the page
   app.css is not the only place a word's colour is chosen. app.js sets one in an inline
   style attribute and through element.style.color, and the two it sets are the tokens the
   stylesheet's own note marks as not for words. Every one of them lands on a .subval, a
   .fieldnote, a .pill or a td.mono, all under 14px, so they are held to the same floor
   against every ground in the ladder rather than guessed at from the element. */
const RUNTIME = new Map();

/** One site, filed under the token it names. */
function runtimeHit(name, index) {
  const line = SCRIPT.slice(0, index).split("\n").length;
  if (!RUNTIME.has(name)) RUNTIME.set(name, []);
  if (!RUNTIME.get(name).includes(line)) RUNTIME.get(name).push(line);
}

// `color:` and never `border-color:`, `caret-color:` or any other of them: a border is a line
// and a line is not a word. The lookbehind is what keeps the two apart.
for (const m of SCRIPT.matchAll(/(?<![-\w])color\s*:\s*var\(\s*(--[A-Za-z0-9-]+)\s*\)/g))
  runtimeHit(m[1], m.index);

// And the ones written as an assignment, where every branch of the expression counts: one
// line can hand three different tokens to three different states.
for (const m of SCRIPT.matchAll(/\.style\.color\s*=([^;\n]*)/g))
  for (const hit of m[1].matchAll(/var\(\s*(--[A-Za-z0-9-]+)\s*\)/g))
    runtimeHit(hit[1], m.index);

// A colour app.js writes as a LITERAL rather than as a token cannot be held to a token's
// promise, the same way one in the stylesheet cannot. There are none today, and the day one
// appears it is listed here rather than quietly skipped.
for (const m of SCRIPT.matchAll(/(?<![-\w])color\s*:\s*(#[0-9A-Fa-f]{3,8}|rgba?\([^)]*\))/g))
  unresolved.push("app.js:" + SCRIPT.slice(0, m.index).split("\n").length
    + " sets color:" + m[1] + " as a literal, which no token promise covers");
for (const m of SCRIPT.matchAll(/\.style\.color\s*=\s*["'](#[0-9A-Fa-f]{3,8}|rgba?\([^)]*\))["']/g))
  unresolved.push("app.js:" + SCRIPT.slice(0, m.index).split("\n").length
    + " sets color:" + m[1] + " as a literal, which no token promise covers");

for (const [name, lines] of [...RUNTIME.entries()].sort()) {
  const where = "app.js:" + lines.join(",");
  if (!declared(name)) {
    failures.push(where + " sets color:" + name + ", which is declared nowhere");
    continue;
  }
  const fg = resolve(name);
  if (!fg) {
    unresolved.push(where + " sets color:" + name + " (not a colour this gate can read)");
    continue;
  }
  held++;
  for (const [surface, bg] of SURFACES) {
    measured++;
    const seen = ratio(over(fg, bg), bg);
    if (seen < FLOOR) {
      failures.push(where + " sets color:" + name + " on small text: " + seen.toFixed(2)
        + " on " + surface + ", under " + FLOOR);
    }
  }
}

console.log("  tokens: " + TOKENS.size + ", surfaces painted: "
  + SURFACES.map(s => s[0]).join(" "));
console.log("  rules read: " + RULES.length + ", small-text colours held: " + held
  + ", pairs measured: " + measured
  + ", runtime colours from app.js: " + RUNTIME.size);

if (invisible.length) {
  console.log("  " + invisible.length + " rule(s) whose words are not drawn at all "
    + "(a hidden state, or text behind a highlight), so nothing to measure:");
  for (const line of invisible) console.log("    - " + line);
}

if (unresolved.length) {
  console.log("  " + unresolved.length + " pair(s) this gate cannot resolve, listed rather than passed:");
  for (const line of unresolved) console.log("    - " + line);
}

if (failures.length) {
  console.log("  " + failures.length + " finding(s):");
  for (const line of failures) console.log("    FAIL " + line);
  console.log("");
  console.log("contrast gate: " + failures.length + " FAILED");
  process.exit(1);
}

console.log("");
console.log("contrast gate: " + measured + " pairs at or above " + FLOOR);
