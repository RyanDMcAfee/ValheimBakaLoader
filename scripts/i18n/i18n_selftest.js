/* Self test for WebUI/i18n.js, run from the copy gate.
 *
 * No jsdom and no browser. The walker is driven over a fake element tree that
 * implements the four things it actually touches (an attribute reader, child
 * nodes, an attribute writer and a text content setter), which is enough to
 * prove the one thing that matters about it: a row that carries markup either
 * side of its label keeps that markup.
 *
 * Node is asked for the plural categories the same way the catalog gate asks,
 * so a Russian plural that is complete here is complete there.
 *
 * Prints one line per case and exits non zero on the first failure.
 */

"use strict";

const path = require("path");
const assert = require("assert");

const I18N_PATH = path.join(__dirname, "..", "..", "ValheimBakaLoader", "WebUI", "i18n.js");

let passed = 0;
const failures = [];

function test(name, body) {
  try {
    body();
    passed++;
    console.log("  ok   " + name);
  } catch (problem) {
    failures.push(name + ": " + (problem && problem.message ? problem.message : problem));
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}

/** A module with no state left over from the case before it. */
function fresh() {
  delete require.cache[require.resolve(I18N_PATH)];
  return require(I18N_PATH);
}

const ENGLISH = {
  _meta: { language: "en", appVersion: "1.2.0", catalog: 1 },
  keys: {
    "hall.title": { lore: "Hearth Status", plain: "Server Status" },
    "hall.greet": { lore: "Welcome back, {name}", params: { name: "text" } },
    "hall.mark": { mark: "ᛉ", lore: "World saved" },
    "hall.twin.a": { lore: "Same words" },
    "hall.twin.b": { lore: "Same words" },
    "mods.scanned": {
      lore: { one: "{count} mod scanned", other: "{count} mods scanned" },
      params: { count: "number" },
      plural: "count"
    },
    "tip.saga": { lore: "The server log" },
    "field.world": { lore: "World name" },
    "aria.close": { lore: "Close this panel" },
    "only.in.english": { lore: "Nothing translated this one yet" }
  }
};

const RUSSIAN = {
  _meta: { language: "ru", appVersion: "1.2.0", catalog: 1 },
  keys: {
    "hall.title": { translation: "Состояние очага" },
    "mods.scanned": {
      translation: {
        one: "{count} мод проверен",
        few: "{count} мода проверено",
        many: "{count} модов проверено",
        other: "{count} мода проверено"
      },
      params: { count: "number" },
      plural: "count"
    }
  }
};

/* ------------------------------------------------------------- the fake DOM */

function textNode(value) {
  return { nodeType: 3, nodeValue: value };
}

function element(attrs, children) {
  const el = {
    nodeType: 1,
    attrs: Object.assign({}, attrs),
    childNodes: (children || []).slice(),
    getAttribute(name) { return Object.prototype.hasOwnProperty.call(this.attrs, name) ? this.attrs[name] : null; },
    setAttribute(name, value) { this.attrs[name] = value; },
    appendChild(node) { this.childNodes.push(node); return node; },
    ownerDocument: { createTextNode: textNode },
    /** The words this element shows, the way a reader would see them. */
    words() {
      return this.childNodes.filter(n => n.nodeType === 3).map(n => n.nodeValue).join("");
    }
  };
  Object.defineProperty(el, "textContent", {
    get() { return this.childNodes.map(n => (n.nodeType === 3 ? n.nodeValue : n.textContent)).join(""); },
    set(value) { this.childNodes = [textNode(value)]; }
  });
  return el;
}

/** Just enough of a document: one selector shape, "[attribute]". */
function documentOf(tree) {
  return {
    querySelectorAll(selector) {
      const attr = selector.replace(/^\[|\]$/g, "");
      const out = [];
      (function walk(node) {
        if (!node || node.nodeType !== 1) return;
        if (node.getAttribute(attr) != null) out.push(node);
        node.childNodes.forEach(walk);
      })(tree);
      return out;
    }
  };
}

/* ------------------------------------------------------------------- cases */

console.log("i18n.js self test");

test("a named slot is filled from the parameter", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  assert.strictEqual(I.T("hall.greet", { name: "Odin" }), "Welcome back, Odin");
});

test("a slot with no parameter is left standing rather than blanked", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  assert.strictEqual(I.T("hall.greet"), "Welcome back, {name}");
  assert.strictEqual(I.T("hall.greet", { other: "x" }), "Welcome back, {name}");
});

test("the register getter chooses lore or plain", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  assert.strictEqual(I.T("hall.title"), "Hearth Status");
  I.setRegister(() => true);
  assert.strictEqual(I.T("hall.title"), "Server Status");
  assert.strictEqual(I.plain(), true);
  I.setRegister(() => false);
  assert.strictEqual(I.T("hall.title"), "Hearth Status");
});

test("a register getter that throws does not take the sentence with it", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  I.setRegister(() => { throw new Error("no prefs yet"); });
  assert.strictEqual(I.T("hall.title"), "Hearth Status");
});

test("English plurals pick one and other", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  assert.deepStrictEqual(I.pluralCategories(), ["one", "other"]);
  assert.strictEqual(I.T("mods.scanned", { count: 1 }), "1 mod scanned");
  assert.strictEqual(I.T("mods.scanned", { count: 7 }), "7 mods scanned");
  assert.strictEqual(I.T("mods.scanned", { count: 0 }), "0 mods scanned");
});

test("Russian plurals pick one, few and many", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  I.load(RUSSIAN, "ru");
  assert.deepStrictEqual(I.pluralCategories().sort(), ["few", "many", "one", "other"]);
  assert.strictEqual(I.T("mods.scanned", { count: 1 }), "1 мод проверен");
  assert.strictEqual(I.T("mods.scanned", { count: 2 }), "2 мода проверено");
  assert.strictEqual(I.T("mods.scanned", { count: 5 }), "5 модов проверено");
  assert.strictEqual(I.T("mods.scanned", { count: 21 }), "21 мод проверен");
});

test("a parameter that is not a number lands on other", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  assert.strictEqual(I.T("mods.scanned", { count: "lots" }), "lots mods scanned");
  assert.strictEqual(I.T("mods.scanned"), "{count} mods scanned");
});

test("a key the pack does not have falls back to English and is recorded", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  I.load(RUSSIAN, "ru");
  assert.strictEqual(I.T("hall.title"), "Состояние очага");
  assert.strictEqual(I.T("only.in.english"), "Nothing translated this one yet");
  assert.deepStrictEqual(I.missing, ["only.in.english"]);
  // Recorded once, however many times it is asked for.
  I.T("only.in.english");
  assert.deepStrictEqual(I.missing, ["only.in.english"]);
});

test("a key no catalog has comes back as the id, and warns once", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  const said = [];
  const warn = console.warn;
  console.warn = (...args) => said.push(args.join(" "));
  try {
    assert.strictEqual(I.T("no.such.key"), "no.such.key");
    assert.strictEqual(I.T("no.such.key"), "no.such.key");
  } finally {
    console.warn = warn;
  }
  assert.strictEqual(said.length, 1, "warned " + said.length + " times");
  assert.ok(said[0].indexOf("no.such.key") >= 0, said[0]);
});

test("has and mark answer off the entry", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  assert.strictEqual(I.has("hall.mark"), true);
  assert.strictEqual(I.has("no.such.key"), false);
  assert.strictEqual(I.mark("hall.mark"), "ᛉ");
  assert.strictEqual(I.mark("hall.title"), "");
});

test("the bridge finds the id for an English sentence, and refuses an ambiguous one", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  assert.strictEqual(I.idFor("Hearth Status"), "hall.title");
  assert.strictEqual(I.idFor("Same words"), null, "two entries share it, so it names neither");
  assert.strictEqual(I.idFor("never written anywhere"), null);
});

test("the walker fills text, title, placeholder and the screen reader label", () => {
  const I = fresh();
  I.load(ENGLISH, "en");

  const row = element(
    { "data-i18n": "hall.title" },
    [element({ class: "r" }, [textNode("ᚱ")]), textNode("OLD LABEL"), element({ class: "k" }, [textNode("rite")])]
  );
  const leaf = element({ "data-i18n": "hall.mark" }, []);
  const chip = element({ "data-i18n-title": "tip.saga" }, [textNode("Saga")]);
  const input = element({ "data-i18n-placeholder": "field.world" }, []);
  const button = element({ "data-i18n-aria": "aria.close" }, [textNode("x")]);
  const tree = element({}, [row, leaf, chip, input, button]);

  const written = I.applyStatic(documentOf(tree));
  assert.strictEqual(written, 5, "wrote " + written + " places");

  assert.strictEqual(row.words(), "Hearth Status");
  assert.strictEqual(row.childNodes.length, 3, "the rune and the badge are still there");
  assert.strictEqual(row.childNodes[0].childNodes[0].nodeValue, "ᚱ");
  assert.strictEqual(row.childNodes[2].childNodes[0].nodeValue, "rite");

  assert.strictEqual(leaf.textContent, "World saved");
  assert.strictEqual(chip.getAttribute("title"), "The server log");
  assert.strictEqual(chip.words(), "Saga", "a title does not touch the words");
  assert.strictEqual(input.getAttribute("placeholder"), "World name");
  assert.strictEqual(button.getAttribute("aria-label"), "Close this panel");
});

test("the walker leaves markup alone when there is nothing to replace", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  const wrapper = element({ "data-i18n": "hall.title" }, [element({ class: "r" }, [textNode("ᚱ")])]);
  I.applyStatic(documentOf(element({}, [wrapper])));
  assert.strictEqual(wrapper.childNodes.length, 2, "the child element survived");
  assert.strictEqual(wrapper.childNodes[0].nodeType, 1);
  assert.strictEqual(wrapper.words(), "Hearth Status");
});

test("the walker is a no op with nothing to walk", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  assert.strictEqual(I.applyStatic(documentOf(element({}, []))), 0);
  assert.strictEqual(I.applyStatic({}), 0);
});

test("the English shapes the halls already showed are unchanged", () => {
  const I = fresh();
  I.load(ENGLISH, "en");

  assert.strictEqual(I.fmtBytes(512), "512 B");
  assert.strictEqual(I.fmtBytes(2048), "2.0 KB");
  assert.strictEqual(I.fmtBytes(1572864), "1.5 MB");
  assert.strictEqual(I.fmtBytes(1073741824), "1.00 GB");
  assert.strictEqual(I.fmtBytes("not a number"), "-");

  assert.strictEqual(I.fmtRelative(45), "45s ago");
  assert.strictEqual(I.fmtRelative(300), "5m ago");
  assert.strictEqual(I.fmtRelative(7200), "2h ago");
  assert.strictEqual(I.fmtRelative(259200), "3d ago");

  assert.strictEqual(I.fmtDuration(13 * 86400), "13d 0h");
  assert.strictEqual(I.fmtDuration(4 * 3600 + 32 * 60), "4h 32m");
  assert.strictEqual(I.fmtDuration(7 * 60), "7m");
  assert.strictEqual(I.fmtDuration(40), "40s");

  assert.strictEqual(I.fmtTime(new Date(2026, 0, 2, 9, 5)), "09:05");
  assert.strictEqual(I.fmtTime(new Date(2026, 0, 2, 19, 45)), "19:45");
  assert.strictEqual(I.fmtTime("not a date"), "-");

  assert.strictEqual(I.fmtNumber(1234567), "1,234,567");
});

test("the edges of each tier read the way they always did", () => {
  const I = fresh();
  I.load(ENGLISH, "en");

  // One byte under each boundary, where a rounding change would show first.
  assert.strictEqual(I.fmtBytes(0), "0 B");
  assert.strictEqual(I.fmtBytes(1023), "1023 B", "no grouping separator appears here");
  assert.strictEqual(I.fmtBytes(1048575), "1024.0 KB");
  assert.strictEqual(I.fmtBytes(-5), "-5 B");

  assert.strictEqual(I.fmtDuration(0), "0s");
  assert.strictEqual(I.fmtDuration(-5), "0s", "a negative span is no span");
  assert.strictEqual(I.fmtRelative(0, "second"), "0s ago");
});

test("the formatters follow the language", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  I.setLocale("ru");
  assert.strictEqual(I.locale(), "ru");
  assert.strictEqual(I.fmtBytes(2048), "2,0 KB", "Russian writes a decimal comma");
  I.setLocale("en");
  assert.strictEqual(I.fmtBytes(2048), "2.0 KB");
});

test("sorting ignores case by default and does not when asked", () => {
  const I = fresh();
  I.load(ENGLISH, "en");
  assert.strictEqual(I.compare("anna", "Anna"), 0);
  assert.notStrictEqual(I.compare("anna", "Anna", {}), 0);
  assert.ok(I.compare("apple", "banana") < 0);
  assert.strictEqual(I.compare(null, ""), 0);
});

test("with no catalog at all the lookup still answers", () => {
  const I = fresh();
  const warn = console.warn;
  console.warn = () => {};
  try {
    assert.strictEqual(I.T("some.key"), "some.key");
    assert.strictEqual(I.T(null), "");
    assert.strictEqual(I.has("some.key"), false);
    assert.strictEqual(I.idFor("anything"), null);
  } finally {
    console.warn = warn;
  }
  assert.strictEqual(I.fmtBytes(2048), "2.0 KB");
});

test("loading a catalog reports the language it installed", () => {
  const I = fresh();
  assert.strictEqual(I.load(ENGLISH), "en");
  assert.strictEqual(I.load(RUSSIAN), "ru");
  assert.strictEqual(I.load({ keys: {} }, "ja"), "ja");
  assert.strictEqual(I.load(null), "en");
});

console.log("");
if (failures.length) {
  console.log("SELFTEST FAIL " + failures.length + " of " + (passed + failures.length));
  failures.forEach(line => console.log("  " + line));
  process.exit(1);
}
console.log("SELFTEST PASS " + passed + " cases");
