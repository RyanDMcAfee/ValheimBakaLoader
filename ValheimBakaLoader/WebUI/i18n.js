/* ============================================================================
   i18n.js - the lookup the halls read their words out of.

   Loaded by index.html before app.js, through the same cache stamp both other
   includes use, so a new release is a new address and the browser's disk cache
   cannot hand back last version's catalog.

   It is a classic script on purpose: app.js is one too, and a module would put
   the lookup behind an await that every render function would then have to
   learn about. The guard at the bottom also hands the same object back to
   require(), which is how scripts/i18n/i18n_selftest.js drives it under node
   with no browser and no jsdom in the room.

   THE CONTRACT, in one paragraph. An id is a stable dotted name and never the
   English text: keying on English orphans every translation the first time
   somebody edits a sentence, and this codebase edits sentences often. One entry
   holds BOTH registers of the same sentence, `lore` and `plain`, because two
   sibling keys is how one half of a pair goes missing. Slots are named, {like}
   this, never positional, because Russian and Japanese reorder the clauses
   around them. A plural is an object of CLDR categories, chosen by
   Intl.PluralRules from the parameter the entry names. The order at every call
   site is T(id, params) then esc() then the DOM, and esc() stays last.
   There is no path from English text back to an id any more. There was one while the
   halls were being migrated, so a call site still spelling a sentence out and one
   already asking by id could not word the same thing two ways; every call site asks by
   id now, and a reverse map kept past that point is a second way to name a sentence
   and therefore a second thing to keep in step.
   ============================================================================ */

(function (root, factory) {
  "use strict";
  var api = factory();
  if (typeof module === "object" && module && module.exports) module.exports = api;
  if (root) root.I18N = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  "use strict";

  /* ---------------------------------------------------------------- state */

  /* The English catalog is the source of record and the fallback both. It is
     kept separately from the active one so a pack that is older than the app
     shows the English sentence for a key it has never heard of, rather than a
     dotted id. */
  var english = null;
  var active = null;
  var activeTag = "en";

  /* Ids the active catalog did not have and English answered for. Read by the
     pack tests and worth reading in the console while a translation is in
     review: a long list means the pack is behind the app. */
  var missing = [];
  var missingSeen = Object.create(null);

  /* Ids already complained about. Cleared by load(), so a catalog that arrives without
     an id still says so even if something asked for it before the file landed. */
  var warned = Object.create(null);

  /* True once a catalog is in. app.js paints as it is evaluated and the English catalog
     is FETCHED, so every painter on that road asks for words nothing can answer yet:
     warning about those 57 ids says only that a fetch had not resolved, which is not a
     defect and buried the one line that would be. Silence until there is a catalog to
     be missing from; the boot path says its own piece when the fetch fails. */
  var loaded = false;

  /* What the app says about the register right now. The app owns the answer
     (it is a preference, and the switch is in Upkeep), so it hands a getter in
     rather than this file reaching for a global it does not own. */
  var registerGetter = function () { return false; };

  var has = Object.prototype.hasOwnProperty;

  /* ------------------------------------------------------------ the lookup */

  function keysOf(catalog) {
    return (catalog && typeof catalog === "object" && catalog.keys) || null;
  }

  /**
   * Installs a catalog as the active one, and as the English fallback when it
   * is the English one. Sets the locale the formatters read.
   * @param {object} catalog parsed en.json or a pack's strings.json
   * @param {string} [langCode] overrides _meta.language
   * @returns {string} the language tag now active
   */
  function load(catalog, langCode) {
    var meta = (catalog && catalog._meta) || {};
    var tag = String(langCode || meta.language || "en").trim() || "en";
    var keys = keysOf(catalog) || Object.create(null);

    if (tag === "en" || meta.language === "en") english = keys;
    active = keys;
    loaded = true;
    /* A new catalog answers for its own ids, so what the one before it could not
       answer for says nothing about this one. Both lists start again. */
    missing.length = 0;
    missingSeen = Object.create(null);
    warned = Object.create(null);
    setLocale(tag);
    return activeTag;
  }

  function note(id) {
    if (missingSeen[id]) return;
    missingSeen[id] = true;
    missing.push(id);
  }

  /* The marker a pack-merge tool writes for an id no translation batch answered. A pack
     built that way carries it where the sentence should be, which is worse than having
     no key at all: the id is present, so the fallback below never ran, and the sentence
     that reached the screen was the dotted id itself. Two whole halls of
     HEARTH.HEAD.TITLE were photographed that way before anyone noticed.
     It is a pack-builder's marker and never a sentence, so the lookup reads it as a
     hole in the pack, which is what it is. */
  var MISSING_MARK = "__missing";

  /* WORDS, and there is exactly one shape of them: a string that is neither the marker
     above nor blank. This is the test everything below finishes on, because it is the
     only thing a host can actually read. */
  function words(value) {
    return typeof value === "string" && value !== MISSING_MARK && value.trim() !== "";
  }

  /* What counts as an ANSWER, and it is deliberately narrow. Two shapes can hold words:
     an entry object, and a bare string for a pack that stores one value per id.
     Everything else that can land in a JSON file - the marker above, a number, a
     boolean, null, an array, an empty string - is a hole, and a hole in the ACTIVE
     catalog has to fall through to English rather than be painted.
     A pack is a file built by somebody else, sometimes by a tool nobody here wrote, so
     "the catalog will be well formed" is not a thing this file gets to assume. */
  function usable(value) {
    if (typeof value === "string") return words(value);
    return !!value && typeof value === "object" && !Array.isArray(value);
  }

  function plainWanted() {
    try { return !!registerGetter(); } catch (_) { return false; }
  }

  /* One entry, two registers, and a translated pack's single value, in the order they
     are tried. Plain wins when the host asked for plain and the entry has one; a pack's
     own translation wins next; the lore wording is the floor.
     A LIST rather than one pick, because a register is its own hole. The value of the
     key can be a whole entry object and still carry nothing to read: a builder marking
     an id it could not answer writes the marker, or a null, or an empty string, INTO
     the register, and the entry around it still looks like an entry. Each candidate is
     judged on the words it produces, further down, and a register that produces none is
     stepped over rather than painted. */
  function registersOf(entry) {
    /* A pack that stores one value per id rather than an entry object. */
    if (typeof entry === "string") return words(entry) ? [entry] : [];
    if (!usable(entry)) return [];
    var out = [];
    if (plainWanted() && usable(entry.plain)) out.push(entry.plain);
    if (usable(entry.translation)) out.push(entry.translation);
    if (usable(entry.lore)) out.push(entry.lore);
    return out;
  }

  function pluralCategory(n) {
    var value = Number(n);
    if (!isFinite(value)) return "other";
    try { return pluralRules().select(value); } catch (_) { return "other"; }
  }

  /* A plural value is an object of CLDR categories. The category the language
     does not have, or a parameter that is not a number, both land on "other",
     which every language has. A pack that filled in neither the category this
     count needs nor "other" has left a hole the size of the whole sentence, and it
     falls through with the rest of them.

     A caller that formats its own number hands the raw one in pluralValue and the
     category is picked from THAT. A formatted number cannot be read back: ar-EG
     writes ١ for 1 and Number("١") is NaN, so a slot carrying the words for a
     number would have put every Arabic sentence on "other" and written the plural
     of a sentence about one thing. */
  function pluralOf(entry, value, params) {
    var name = entry && entry.plural;
    var counted = NaN;
    if (params) counted = has.call(params, "pluralValue") ? params.pluralValue : params[name];
    var picked = pluralCategory(counted);
    return words(value[picked]) ? value[picked] : value.other;
  }

  /** The words one entry gives for these params, or null when it gives none. */
  function resolveValue(entry, params) {
    var candidates = registersOf(entry);
    for (var i = 0; i < candidates.length; i++) {
      var value = candidates[i];
      if (typeof value === "object") value = pluralOf(entry, value, params);
      if (words(value)) return value;
    }
    return null;
  }

  /* The entry that can ANSWER for an id, the active catalog first and English behind it.
     "The catalog holds this id" is a different question and the wrong one: a pack can
     hold the id and hold a hole behind it, and English has to run then too. So the walk
     asks each catalog for the WORDS and keeps the entry that produced them, which is
     what makes has() and T() one reading rather than two that can disagree. */
  function answerFor(id, params) {
    var text;
    if (active && has.call(active, id)) {
      text = resolveValue(active[id], params);
      if (text != null) return { entry: active[id], text: text };
    }
    if (english && has.call(english, id)) {
      text = resolveValue(english[id], params);
      if (text != null) { note(id); return { entry: english[id], text: text }; }
    }
    return null;
  }

  function entryFor(id, params) {
    var answer = answerFor(id, params);
    return answer ? answer.entry : null;
  }

  var SLOT = /\{([A-Za-z_][A-Za-z0-9_]*)\}/g;

  /* A slot with no parameter behind it is left standing rather than blanked.
     An empty gap in a sentence reads as a wording mistake and gets lived with;
     a literal {count} on screen gets reported the same day. */
  function fill(text, params) {
    if (!params || text.indexOf("{") < 0) return text;
    return text.replace(SLOT, function (whole, name) {
      return has.call(params, name) ? String(params[name]) : whole;
    });
  }

  function warnOnce(id) {
    if (!loaded || warned[id]) return;
    warned[id] = true;
    if (typeof console !== "undefined" && console && console.warn) {
      console.warn("[i18n] no wording for " + id);
    }
  }

  /**
   * The words for an id. Falls back to English, then to the id itself.
   * @param {string} id dotted catalog id
   * @param {object} [params] named slot values, plus the optional pluralValue: the
   *   raw number to choose the plural category with, for a caller whose slot already
   *   carries that number written out in the host's own digits
   * @returns {string} plain text, never markup: esc() still comes after this
   */
  function T(id, params) {
    if (id == null) return "";
    var key = String(id);
    var answer = answerFor(key, params);
    if (!answer) { warnOnce(key); return key; }
    return fill(answer.text, params);
  }

  /** True when some catalog can answer for this id WITH WORDS. Literally the reading T()
      does, over the same entries, so a caller that asks before it draws can never be told
      yes and then handed an id.
      @param {string} id dotted catalog id
      @param {object} [params] the params T() will be given, when there are any: a plural
        entry is read for the category those params choose, and for "other" without them */
  function hasKey(id, params) {
    if (id == null) return false;
    return answerFor(String(id), params) != null;
  }

  /** The rune that leads this message, or "" when it leads with none. Taken off the entry
      that supplied the WORDS, so a pack entry that kept the rune and lost the sentence
      cannot stand its rune in front of an English fallback it had no part in. */
  function mark(id) {
    var entry = id == null ? null : entryFor(String(id));
    return entry && entry.mark != null ? String(entry.mark) : "";
  }

  /** The app hands in the getter that answers "plain wording, or lore?". */
  function setRegister(getter) {
    registerGetter = typeof getter === "function" ? getter : function () { return false; };
    return registerGetter;
  }

  /** True when the host is reading the plain register right now. */
  function plain() { return plainWanted(); }

  /* --------------------------------------------------------- the formatters

     Every one of these is built on Intl with the ACTIVE locale, and every one
     is pinned to the options that make English come out exactly as the hand
     rolled formatter it replaced did. That pinning is deliberate: this phase
     changes which code writes the number, never what an English host reads.
     Where a language wants its own shape (4 ч 32 мин rather than 4h 32m) it is
     Intl that knows, and that is the entire point of the move.                */

  var cache = Object.create(null);

  function cached(kind, opts, build) {
    var key = kind + "|" + activeTag + "|" + JSON.stringify(opts || {});
    if (has.call(cache, key)) return cache[key];
    var made = null;
    try { made = build(); } catch (_) { made = null; }
    cache[key] = made;
    return made;
  }

  /** Cached Intl.NumberFormat for the active locale. */
  function numberFormat(opts) {
    return cached("n", opts, function () { return new Intl.NumberFormat(activeTag, opts); });
  }

  /** Cached Intl.DateTimeFormat for the active locale. */
  function dateTimeFormat(opts) {
    return cached("d", opts, function () { return new Intl.DateTimeFormat(activeTag, opts); });
  }

  /** Cached Intl.RelativeTimeFormat for the active locale. */
  function relativeFormat(opts) {
    return cached("r", opts, function () { return new Intl.RelativeTimeFormat(activeTag, opts); });
  }

  /** Cached Intl.Collator for the active locale. */
  function collator(opts) {
    return cached("c", opts, function () { return new Intl.Collator(activeTag, opts); });
  }

  /** Cached Intl.PluralRules for the active locale. */
  function pluralRules() {
    return cached("p", null, function () { return new Intl.PluralRules(activeTag); });
  }

  /** The CLDR categories this locale actually uses. The catalog gate asks the
      same question of the same machinery, so a plural entry can never be
      complete here and short there. */
  function pluralCategories() {
    var rules = pluralRules();
    try { return rules.resolvedOptions().pluralCategories.slice(); }
    catch (_) { return ["other"]; }
  }

  /** A number in the host's own notation. */
  function fmtNumber(n, opts) {
    var value = Number(n);
    if (!isFinite(value)) return "-";
    var formatter = numberFormat(opts);
    if (!formatter) return String(value);
    try { return formatter.format(value); } catch (_) { return String(value); }
  }

  function fixed(value, digits) {
    return fmtNumber(value, {
      minimumFractionDigits: digits, maximumFractionDigits: digits, useGrouping: false
    });
  }

  /**
   * "512 B" / "1.5 KB" / "2.0 MB" / "1.25 GB".
   * The tiers and the digit counts are the ones the halls have always shown;
   * what Intl brings is the decimal mark, which is a comma in half of Europe.
   * Grouping is off because it was off before, and a separator appearing in
   * "1023.9 KB" would be a change no release note claimed.
   */
  function fmtBytes(n) {
    var value = Number(n);
    if (!isFinite(value)) return "-";
    if (value < 1024) return fmtNumber(value, { maximumFractionDigits: 0, useGrouping: false }) + " B";
    if (value < 1048576) return fixed(value / 1024, 1) + " KB";
    if (value < 1073741824) return fixed(value / 1048576, 1) + " MB";
    return fixed(value / 1073741824, 2) + " GB";
  }

  var RELATIVE_OPTS = { numeric: "always", style: "narrow" };

  /**
   * "40s ago" / "5m ago" / "3h ago" / "2d ago", in the host's language.
   * @param {number} amount how many units ago. Positive is the past.
   * @param {string} [unit] "second" | "minute" | "hour" | "day". Left out, the
   *   amount is read as seconds and the unit is chosen by size, rounding DOWN
   *   the way the hand rolled version did.
   */
  function fmtRelative(amount, unit) {
    var value = Number(amount);
    if (!isFinite(value)) return "-";
    var named = unit;
    if (!named) {
      var s = Math.max(0, value);
      if (s < 60) { named = "second"; value = Math.floor(s); }
      else if (s < 3600) { named = "minute"; value = Math.floor(s / 60); }
      else if (s < 86400) { named = "hour"; value = Math.floor(s / 3600); }
      else { named = "day"; value = Math.floor(s / 86400); }
    }
    var formatter = relativeFormat(RELATIVE_OPTS);
    if (!formatter) return fmtNumber(value) + " " + named;
    try { return formatter.format(-value, named); }
    catch (_) { return fmtNumber(value) + " " + named; }
  }

  var TIME_OPTS = { hour: "2-digit", minute: "2-digit", hourCycle: "h23" };

  /**
   * "19:45". The 24 hour cycle is pinned rather than left to the locale,
   * because the halls have always shown a 24 hour clock and a server log
   * beside a 12 hour status bar reads as two different machines.
   */
  function fmtTime(when) {
    var d = when instanceof Date ? when : new Date(when);
    if (isNaN(d.getTime())) return "-";
    var formatter = dateTimeFormat(TIME_OPTS);
    if (!formatter) return two(d.getHours()) + ":" + two(d.getMinutes());
    try { return formatter.format(d); }
    catch (_) { return two(d.getHours()) + ":" + two(d.getMinutes()); }
  }

  function two(n) { return String(n).padStart(2, "0"); }

  /* Four option sets rather than one, because a span of exactly thirteen days
     has to read "13d 0h" and a span of seven minutes has to read "7m": the
     zero component is wanted in one and not the other, and that is a display
     choice per tier rather than per formatter. */
  var DURATION_TIERS = {
    day: { style: "narrow", daysDisplay: "always", hoursDisplay: "always" },
    hour: { style: "narrow", hoursDisplay: "always", minutesDisplay: "always" },
    minute: { style: "narrow", minutesDisplay: "always" },
    second: { style: "narrow", secondsDisplay: "always" }
  };

  function durationFormat(opts) {
    if (typeof Intl === "undefined" || typeof Intl.DurationFormat !== "function") return null;
    return cached("dur", opts, function () { return new Intl.DurationFormat(activeTag, opts); });
  }

  /**
   * "13d 0h" / "4h 32m" / "7m" / "40s", big spans coarse and small spans exact.
   * Intl.DurationFormat where the runtime has it (it is young: Chromium 129),
   * and the digits with their letters where it does not. Both arms produce the
   * same English, which is how the fallback stays honest.
   */
  function fmtDuration(seconds) {
    var total = Math.max(0, Math.round(Number(seconds) || 0));
    var d = Math.floor(total / 86400);
    var h = Math.floor((total % 86400) / 3600);
    var m = Math.floor((total % 3600) / 60);
    var s = total % 60;

    var tier, parts;
    if (d > 0) { tier = "day"; parts = { days: d, hours: h }; }
    else if (h > 0) { tier = "hour"; parts = { hours: h, minutes: m }; }
    else if (m > 0) { tier = "minute"; parts = { minutes: m }; }
    else { tier = "second"; parts = { seconds: s }; }

    var formatter = durationFormat(DURATION_TIERS[tier]);
    if (formatter) {
      try { return formatter.format(parts); } catch (_) { /* fall through */ }
    }
    if (tier === "day") return fmtNumber(d) + "d " + fmtNumber(h) + "h";
    if (tier === "hour") return fmtNumber(h) + "h " + fmtNumber(m) + "m";
    if (tier === "minute") return fmtNumber(m) + "m";
    return fmtNumber(s) + "s";
  }

  var COMPARE_OPTS = { sensitivity: "base" };

  /**
   * Sorts the way the host's language sorts. Chinese wants pinyin order and
   * Swedish wants a after z, and neither is something a byte comparison knows.
   * @param {object} [opts] Intl.Collator options; the default ignores case.
   */
  function compare(a, b, opts) {
    var left = String(a == null ? "" : a);
    var right = String(b == null ? "" : b);
    var c = collator(opts || COMPARE_OPTS);
    if (!c) return left < right ? -1 : (left > right ? 1 : 0);
    try { return c.compare(left, right); }
    catch (_) { return left < right ? -1 : (left > right ? 1 : 0); }
  }

  /* ------------------------------------------------------------ the locale */

  /** The BCP 47 tag every formatter above is reading. */
  function locale() { return activeTag; }

  /** Points the formatters at another language. Clears the formatter cache. */
  function setLocale(code) {
    var tag = String(code || "en").trim() || "en";
    if (tag !== activeTag) cache = Object.create(null);
    activeTag = tag;
    return activeTag;
  }

  /* ------------------------------------------------------------ the walker

     565 static text nodes come up on first paint and the old terminology
     selector reached 87 of them, so a static node is translated by carrying its
     own id rather than by being matched from a list. One attribute per place
     the words can sit: the text, the tooltip, the placeholder, the label a
     screen reader reads.                                                      */

  /* An element that has element children keeps them: only its own words are
     replaced. That is what lets a palette row carry its id on the row itself
     and keep the rune and the badge that sit either side of the label. */
  function setText(el, value) {
    var nodes = el.childNodes;
    var hasElementChild = false;
    var i;
    if (nodes && nodes.length) {
      for (i = 0; i < nodes.length; i++) {
        if (nodes[i].nodeType === 1) { hasElementChild = true; break; }
      }
      for (i = 0; i < nodes.length; i++) {
        if (nodes[i].nodeType === 3 && String(nodes[i].nodeValue).trim()) {
          nodes[i].nodeValue = value;
          return;
        }
      }
    }
    if (!hasElementChild) { el.textContent = value; return; }
    /* Markup and no words of its own: append rather than wipe the children. */
    if (el.ownerDocument && el.ownerDocument.createTextNode) {
      el.appendChild(el.ownerDocument.createTextNode(value));
    }
  }

  function attribute(name) {
    return function (el, value) { el.setAttribute(name, value); };
  }

  var WALK = [
    ["data-i18n", setText],
    ["data-i18n-title", attribute("title")],
    ["data-i18n-placeholder", attribute("placeholder")],
    ["data-i18n-aria", attribute("aria-label")]
  ];

  /**
   * Fills every element under root that names an id.
   * @param {object} [root] defaults to the document
   * @returns {number} how many places were written
   */
  function applyStatic(root) {
    var where = root || (typeof document !== "undefined" ? document : null);
    if (!where || typeof where.querySelectorAll !== "function") return 0;

    var written = 0;
    for (var w = 0; w < WALK.length; w++) {
      var attr = WALK[w][0], apply = WALK[w][1];
      var found = where.querySelectorAll("[" + attr + "]") || [];
      for (var i = 0; i < found.length; i++) {
        var el = found[i];
        var id = el.getAttribute(attr);
        if (!id) continue;
        apply(el, T(id));
        written++;
      }
    }
    return written;
  }

  /* ---------------------------------------------------------------- the API */

  var api = {
    load: load,
    T: T,
    has: hasKey,
    mark: mark,
    setRegister: setRegister,
    plain: plain,
    locale: locale,
    setLocale: setLocale,
    fmtNumber: fmtNumber,
    fmtBytes: fmtBytes,
    fmtRelative: fmtRelative,
    fmtTime: fmtTime,
    fmtDuration: fmtDuration,
    compare: compare,
    applyStatic: applyStatic,
    numberFormat: numberFormat,
    dateTimeFormat: dateTimeFormat,
    relativeFormat: relativeFormat,
    collator: collator,
    pluralCategories: pluralCategories,
    missing: missing
  };

  return api;
});
