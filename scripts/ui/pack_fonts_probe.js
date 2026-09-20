#!/usr/bin/env node
/*
  The faces a language pack ships, drawn in a real browser.

  WHY THIS EXISTS. Four packs were cut, verified, hashed, published and installed, and
  not one byte of any of their faces was ever drawn. The packager read the font builder's
  face LIST as an object keyed by file name, every lookup missed, and each face came out
  named after its own file: "NotoSansJP-jp" where app.css asks for "Noto Sans JP",
  "Inter-cyrillic" where the Russian stack asks for "Inter". Everything downstream was
  happy. The zip verified, the digests matched, the installer stored the bytes, the page
  wrote its @font-face rules and the browser fetched the fonts, and then every stack fell
  straight through to a Windows face because nothing ever asked for the families the pack
  published. No test could see it: the names it had to be held against are in a stylesheet,
  and the proof is a rendered character.

  So this registers the pack's faces through the page's OWN langInjectFonts, with the
  shape the bridge hands over, sets the language the same way the switch does, draws real
  sentences out of the pack's own catalog, and then asks the browser three questions.

    1. Did every face the pack publishes actually load? Asked of document.fonts, matching
       a FontFace by family AND by unicode range, because app.css declares 'Inter' too and
       a pack that registers nothing would otherwise look fine. NOT asked with
       document.fonts.check, which answers true for a family nobody ever declared.

    2. Is EVERY one of them reached by a stack, before anything is forced? One sample per
       face: the four stacks are read back off the page as the browser resolved them, each
       face is drawn through a stack that names its family, and the text is built out of
       characters inside that face's range which nothing ahead of it in the stack covers.
       "At least one of the pack's faces was pulled in" is not the question, and it used
       to be: a pack carrying a fourth face under a family no stack ever names passed that
       happily, and those bytes were downloaded, stored, served and never asked for.

    3. When the language's own sentences are drawn through the product's own stacks, does
       Chromium report a downloaded font as the one it actually rendered the node with,
       and for enough of the script to mean it? A node carrying two characters is no
       evidence about a page of Japanese, so the sentences are picked with at least a
       dozen characters of the language's own script in them, and the node that proves the
       point has to come out with that many glyphs drawn in faces the browser downloaded.
       The count is printed, so a run that only just clears the floor says so.

  Takes an unpacked pack folder (one holding pack.json) or a folder of release zips.

  node scripts/ui/pack_fonts_probe.js <folder>

  Prints one line per check, then TOTAL n, and exits non zero when n is not 0.
*/
"use strict";

const crypto = require("crypto");
const fs = require("fs");
const http = require("http");
const path = require("path");
const zlib = require("zlib");

const WEBUI = path.resolve(__dirname, "..", "..", "ValheimBakaLoader", "WebUI");

const TYPES = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8", ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml", ".png": "image/png", ".woff2": "font/woff2", ".woff": "font/woff",
};

/* What each language is written in, as the characters a sentence is picked for. */
const SCRIPTS = {
  "ru": { label: "Cyrillic", test: /[Ѐ-ӿ]/ },
  "ja": { label: "Japanese", test: /[぀-ヿ一-鿿]/ },
  "zh-Hans": { label: "Han", test: /[一-鿿]/ },
  "zh-Hant": { label: "Han", test: /[一-鿿]/ },
};

/* The four variables every font-family rule in the product goes through. */
const STACK_VARS = ["--sans", "--serif", "--serif-small", "--mono"];

/* How much of the language's own script a node has to carry before what Chromium says it
   was drawn with counts as an answer. Two glyphs out of a mostly Latin line is how a
   whole screen of Han can be reported as proved; a dozen is a phrase, and every one of
   the four catalogs holds hundreds of lines with that many, so the floor costs nothing. */
const SCRIPT_FLOOR = 12;

/* Below this a character is in a block app.css declares its own Latin faces over, so a
   pack face asked for one of them may lose it to Inter or Cinzel rather than to a fault
   of its own. Sampling stays above the line wherever the face's range allows. */
const LATIN_BLOCKS = 0x0250;

function playwright() {
  const tries = [
    "playwright",
    path.join(process.env.APPDATA || "", "npm", "node_modules", "playwright"),
  ];
  for (const where of tries) {
    try { return require(where); } catch (_) { /* next */ }
  }
  throw new Error("playwright was not found");
}

/* ---- reading a pack ---- */

/* A zip, read out of its central directory. Small enough to keep here: a probe that
   needs an install step is a probe nobody runs. */
function unzip(buffer) {
  let eocd = -1;
  for (let i = buffer.length - 22; i >= 0 && i >= buffer.length - 70000; i--) {
    if (buffer.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  }
  if (eocd < 0) throw new Error("no end of central directory, so this is not a zip");

  const count = buffer.readUInt16LE(eocd + 10);
  let at = buffer.readUInt32LE(eocd + 16);
  const out = new Map();

  for (let n = 0; n < count; n++) {
    if (buffer.readUInt32LE(at) !== 0x02014b50) throw new Error("the central directory does not read");
    const method = buffer.readUInt16LE(at + 10);
    const compressed = buffer.readUInt32LE(at + 20);
    const nameLength = buffer.readUInt16LE(at + 28);
    const extraLength = buffer.readUInt16LE(at + 30);
    const commentLength = buffer.readUInt16LE(at + 32);
    const localAt = buffer.readUInt32LE(at + 42);
    const name = buffer.toString("utf8", at + 46, at + 46 + nameLength);

    const localName = buffer.readUInt16LE(localAt + 26);
    const localExtra = buffer.readUInt16LE(localAt + 28);
    const start = localAt + 30 + localName + localExtra;
    const raw = buffer.subarray(start, start + compressed);

    out.set(name, method === 8 ? zlib.inflateRawSync(raw) : Buffer.from(raw));
    at += 46 + nameLength + extraLength + commentLength;
  }

  return out;
}

function fromFolder(folder) {
  const files = new Map();
  const walk = (dir, prefix) => {
    for (const name of fs.readdirSync(dir)) {
      const full = path.join(dir, name);
      if (fs.statSync(full).isDirectory()) walk(full, prefix + name + "/");
      else files.set(prefix + name, fs.readFileSync(full));
    }
  };
  walk(folder, "");
  return files;
}

/* Every pack the argument points at: one unpacked folder, or a folder of release zips. */
function packsIn(where) {
  if (!fs.existsSync(where)) throw new Error(where + " is not there");

  if (fs.existsSync(path.join(where, "pack.json"))) {
    return [{ name: path.basename(where), files: fromFolder(where) }];
  }

  const zips = fs.readdirSync(where).filter(n => n.toLowerCase().endsWith(".zip")).sort();
  if (!zips.length) throw new Error(where + " holds neither a pack.json nor any pack zip");

  return zips.map(name => ({ name, files: unzip(fs.readFileSync(path.join(where, name))) }));
}

/* ---- what the bridge hands the page ---- */

/*
  The installed shape, worked out the way the service works it out: every face is
  addressed in the shared store under its own digest, so two entries over one file give
  two @font-face rules and one address between them.
*/
function payload(pack, files, prefix) {
  const served = new Map();
  const fonts = [];

  for (const face of pack.fonts || []) {
    const inside = String(face.file || "");
    const bytes = files.get(inside);
    if (!bytes) throw new Error(pack.code + ": the pack names " + inside + " and does not carry it");

    const digest = crypto.createHash("sha256").update(bytes).digest("hex");
    const extension = path.extname(inside).toLowerCase() || ".woff2";
    const stored = "_fonts/" + digest + extension;

    served.set(prefix + stored, bytes);
    fonts.push({
      family: face.family,
      url: prefix + stored,
      weight: face.weight,
      style: face.style,
      unicodeRange: face.unicodeRange || null,
    });
  }

  return { fonts, served };
}

/* How much of one line is the language's own script. */
function scriptCount(text, test) {
  let held = 0;
  for (const character of Array.from(text)) if (test.test(character)) held++;
  return held;
}

/* The sentences a host actually reads, out of the pack's own catalog.
   A line only counts when it carries SCRIPT_FLOOR characters of the language's own
   script. The node that has to prove what Chromium drew is built out of one of these,
   and a line with two Han characters in it makes a node that proves nothing. */
function sentences(catalog, test, want) {
  const all = [];
  const walk = value => {
    if (typeof value === "string") { if (value.trim()) all.push(value.trim()); return; }
    if (value && typeof value === "object") Object.keys(value).forEach(k => walk(value[k]));
  };
  walk(catalog.keys || {});

  const script = all.filter(s => scriptCount(s, test) >= SCRIPT_FLOOR);
  script.sort((a, b) => b.length - a.length);

  const picked = [];
  for (const line of script) {
    if (picked.length >= want) break;
    if (line.length > 90) continue;
    picked.push(line);
  }
  while (picked.length < want && script.length) picked.push(script[picked.length % script.length]);
  return picked;
}

/* A face declared over a range is only asked for the characters in it, so the text that
   proves it loads has to hold one of them. The catalog is asked first. */
function textFor(face, catalog) {
  const spans = ranges(face.unicodeRange);
  if (!spans) return catalog.slice(0, 40);

  const found = [];
  for (const line of catalog) {
    for (const character of Array.from(line)) {
      const point = character.codePointAt(0);
      if (spans.some(([low, high]) => point >= low && point <= high)) {
        found.push(character);
        if (found.length >= 12) return found.join("");
      }
    }
  }
  if (found.length) return found.join("");

  return spans.slice(0, 4).map(([low]) => String.fromCodePoint(low)).join("");
}

/* A line with nothing in it but the language's own script: no Latin word, no digit, no
   space, nothing a Latin face could be asked to draw. */
function onlyScript(text, test) {
  return Array.from(text).filter(character => test.test(character)).slice(0, 28).join("");
}

function ranges(text) {
  if (!text) return null;
  const spans = [];
  for (let piece of String(text).split(",")) {
    piece = piece.trim().toUpperCase().replace(/^U\+/, "");
    if (!piece) continue;
    const [low, high] = piece.split("-");
    const first = parseInt(low.replace(/\?/g, "0"), 16);
    const last = parseInt((high || low).replace(/\?/g, "F"), 16);
    if (Number.isNaN(first) || Number.isNaN(last)) continue;
    spans.push([first, last]);
  }
  return spans.length ? spans : null;
}

/* Chromium restates a range its own way ("U+301, U+400-45F"), so both sides are put in
   one shape before they are compared. A face with no range covers everything, which is
   what the browser calls U+0-10FFFF. */
function canonical(text) {
  const spans = ranges(text) || [[0, 0x10FFFF]];
  return spans.map(([low, high]) => low.toString(16) + "-" + high.toString(16)).sort().join(",");
}

/* The family names in a stack, in order, with the quotes off. */
function stackFamilies(stack) {
  return String(stack == null ? "" : stack)
    .split(",")
    .map(piece => piece.trim().replace(/^['"]|['"]$/g, "").trim())
    .filter(piece => piece.length > 0);
}

function sameFamily(one, other) {
  return String(one).toLowerCase() === String(other).toLowerCase();
}

/* A face with no range is asked for every character, which is what null means here. */
function inSpans(point, spans) {
  return spans === null ? true : spans.some(([low, high]) => point >= low && point <= high);
}

/*
  The text that can only be answered by this face, drawn through this stack. A browser
  asks the stack left to right and stops at the first family that covers the character,
  so a face is only ever reached by a character inside its own range that nothing named
  ahead of it covers. Characters out of the pack's own catalog first, and out of the
  blocks app.css keeps its Latin faces over only when the range leaves nothing else.
*/
function reachingText(face, ahead, lines, want) {
  const mine = ranges(face.unicodeRange);
  const blocked = ahead.map(other => ranges(other.unicodeRange));
  const reaches = point => inSpans(point, mine) && !blocked.some(spans => inSpans(point, spans));

  const above = [];
  const below = [];
  for (const line of lines) {
    for (const character of Array.from(line)) {
      const point = character.codePointAt(0);
      if (!reaches(point)) continue;
      (point >= LATIN_BLOCKS ? above : below).push(character);
      if (above.length >= want) return above.join("");
    }
  }

  const picked = above.concat(below).slice(0, want);
  if (picked.length) return picked.join("");

  /* Nothing in the catalog reaches it, so the range itself is asked. A face published
     over characters this language never writes is still a face, and check 1 still has to
     be able to load it. */
  const made = [];
  for (const [low, high] of mine || [[0x20, 0x7E]]) {
    for (let point = low; point <= high && made.length < want; point++) {
      if (reaches(point)) made.push(String.fromCodePoint(point));
    }
    if (made.length >= want) break;
  }
  return made.join("");
}

/*
  One sample per face: the stack it lives in, and the text that will make that stack ask
  for it. A face whose family no stack names has nowhere to be drawn at all, which is the
  whole finding, so it comes back said rather than guessed at.
*/
function faceSample(face, fonts, stacks, lines) {
  const homes = [];

  for (const variable of STACK_VARS) {
    const names = stackFamilies(stacks[variable]);
    const at = names.findIndex(name => sameFamily(name, face.family));
    if (at < 0) continue;

    const ahead = names
      .slice(0, at)
      .map(name => fonts.find(other => sameFamily(other.family, name)))
      .filter(other => other && other !== face);

    homes.push({ variable, ahead });
    const text = reachingText(face, ahead, lines, 8);
    if (text) return { variable, ahead, text };
  }

  if (!homes.length) return { variable: null, ahead: [], text: "" };
  return { variable: homes[0].variable, ahead: homes[0].ahead, text: "" };
}

/* ---- the page ---- */

const STUB = `(() => {
  const listeners = [];
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    addEventListener: (k, fn) => { if (k === "message") listeners.push(fn); },
    postMessage: m => { if (m.id == null) return;
      setTimeout(() => listeners.forEach(fn => fn({ data: { id: m.id, ok: true, result: {} } })), 0); },
  };
  window.__push = (event, data) => listeners.forEach(fn => fn({ data: { event, data } }));
})()`;

function serve(extra) {
  return new Promise(resolve => {
    const server = http.createServer((req, res) => {
      const asked = decodeURIComponent((req.url || "/").split("?")[0]);

      if (extra.has(asked)) {
        const body = extra.get(asked);
        res.writeHead(200, { "content-type": "font/woff2", "content-length": body.length });
        res.end(body);
        return;
      }

      const file = path.resolve(WEBUI, "." + (asked === "/" ? "/index.html" : asked));
      if (!file.startsWith(WEBUI)) { res.writeHead(403).end(); return; }
      fs.readFile(file, (err, body) => {
        if (err) { res.writeHead(404).end(); return; }
        res.writeHead(200, { "content-type": TYPES[path.extname(file).toLowerCase()] || "application/octet-stream" });
        res.end(body);
      });
    });
    server.listen(0, "127.0.0.1", () => resolve({ server, port: server.address().port }));
  });
}

/* Every face the browser holds, as it holds it. */
const READ_FACES = () => Array.from(document.fonts).map(face => ({
  family: face.family.replace(/^['"]|['"]$/g, ""),
  status: face.status,
  unicodeRange: face.unicodeRange,
}));

/*
  The page put in this language with this pack's faces declared, and the four stacks read
  back off it as the browser resolved them. A computed custom property has its var()
  already followed, so --serif-small reads as the stack it points at, which is --serif in
  English and --sans under CJK. Reading them off the page rather than parsing the
  stylesheet a second time means the samples below are built for the stack the text will
  really be drawn through.
*/
async function prepare(page, code, fonts) {
  return page.evaluate(([code, fonts, variables]) => {
    /* The page's own writer, with the shape the bridge hands over. */
    langInjectFonts(fonts);

    /* And the language the same way the switch sets it: the attribute the stacks hang
       off and the one Chromium picks its own Han face from. */
    if (typeof setLanguageAttributes === "function") setLanguageAttributes(code);
    document.documentElement.lang = code;
    document.documentElement.setAttribute("data-lang", code);

    for (const id of ["packProbe", "packFaces", "packForced"]) {
      const old = document.getElementById(id);
      if (old) old.remove();
    }

    const style = getComputedStyle(document.documentElement);
    const stacks = {};
    variables.forEach(name => { stacks[name] = style.getPropertyValue(name); });
    return stacks;
  }, [code, fonts, STACK_VARS]);
}

async function drive(page, stackSamples, faceStackSamples, faceSamples) {
  await page.evaluate(([stackSamples, faceStackSamples]) => {
    const box = document.createElement("div");
    box.id = "packProbe";
    box.style.cssText = "position:fixed;left:0;top:0;width:920px;z-index:99999;background:#111;color:#E8E2D5";
    stackSamples.forEach((sample, index) => {
      /* The sentence as a host reads it, Latin words, version numbers and all. */
      const line = document.createElement("div");
      line.id = "packStack" + index;
      line.style.fontFamily = "var(" + sample.variable + ")";
      line.style.fontSize = "18px";
      line.textContent = sample.text;
      box.appendChild(line);

      /* And the same line with nothing in it but the script itself. Chromium reports the
         fonts a node was drawn with and not which characters went to which, so a node
         holding one Latin word would answer "a downloaded font" for Inter drawing that
         word while every character of the language fell to Windows. This node has no
         character in it that anything but the language's own face can draw. */
      const pure = document.createElement("div");
      pure.id = "packScript" + index;
      pure.style.fontFamily = "var(" + sample.variable + ")";
      pure.style.fontSize = "18px";
      pure.textContent = sample.script;
      box.appendChild(pure);
    });
    document.body.appendChild(box);

    /* And one node per face the pack publishes, each through a stack that names it, with
       text only that face can be asked for. Same road as the sentences above and the same
       moment: nothing here is forced by family, so a face that turns up loaded turned up
       because a stack in this product asked a real character of it. */
    const faces = document.createElement("div");
    faces.id = "packFaces";
    faces.style.cssText = "position:fixed;left:0;top:300px;width:920px;z-index:99999;background:#111;color:#E8E2D5";
    faceStackSamples.forEach((sample, index) => {
      if (!sample.variable || !sample.text) return;
      const line = document.createElement("div");
      line.id = "packFaceStack" + index;
      line.style.fontFamily = "var(" + sample.variable + ")";
      line.style.fontSize = "18px";
      line.textContent = sample.text;
      faces.appendChild(line);
    });
    document.body.appendChild(faces);
  }, [stackSamples, faceStackSamples]);

  await page.evaluate(() => document.fonts.ready.then(() => null));
  await page.waitForTimeout(250);
  const afterStacks = await page.evaluate(READ_FACES);

  /* Only now is anything forced, so what loaded above loaded because a stack asked for it. */
  await page.evaluate(faceSamples => {
    const box = document.createElement("div");
    box.id = "packForced";
    box.style.cssText = "position:fixed;left:0;top:520px;width:920px;z-index:99999;background:#111;color:#E8E2D5";
    faceSamples.forEach((sample, index) => {
      const line = document.createElement("div");
      line.id = "packFace" + index;
      line.style.fontFamily = "'" + sample.family.replace(/'/g, "") + "'";
      line.style.fontSize = "18px";
      line.textContent = sample.text;
      box.appendChild(line);
    });
    document.body.appendChild(box);
  }, faceSamples);

  await page.evaluate(() => document.fonts.ready.then(() => null));
  await page.waitForTimeout(250);
  const afterForcing = await page.evaluate(READ_FACES);

  return { afterStacks, afterForcing };
}

/* What Chromium actually drew a node with. document.fonts.check cannot answer this: it
   says true for a family nobody declared, because it answers about the fallback too. */
async function platformFonts(page, ids) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("DOM.enable");
  await cdp.send("CSS.enable");
  const doc = await cdp.send("DOM.getDocument");

  const out = {};
  for (const id of ids) {
    const node = await cdp.send("DOM.querySelector", { nodeId: doc.root.nodeId, selector: "#" + id });
    if (!node.nodeId) { out[id] = []; continue; }
    const used = await cdp.send("CSS.getPlatformFontsForNode", { nodeId: node.nodeId });
    out[id] = used.fonts || [];
  }

  await cdp.detach();
  return out;
}

/* ---- the run ---- */

function found(faces, family, range) {
  const wanted = canonical(range);
  return faces.filter(face => face.family === family && canonical(face.unicodeRange) === wanted);
}

async function check(page, pack, files, problems, say) {
  const code = pack.code;
  const script = SCRIPTS[code] || { label: "its own script", test: /[^\u0000-ɏ]/ };
  const prefix = "/lang/";
  const { fonts, served } = payload(pack, files, prefix);

  const catalog = JSON.parse(files.get("strings.json").toString("utf8"));
  const lines = sentences(catalog, script.test, STACK_VARS.length);
  if (!lines.length) {
    problems.push(
      code + ": the catalog holds no line with " + SCRIPT_FLOOR + " characters of " +
      script.label + " in it, so nothing here can say what a page of it is drawn with");
    say("FAIL " + code.padEnd(8) + " no sentence with " + SCRIPT_FLOOR + " characters of " + script.label);
    return served;
  }

  const stacks = await prepare(page, code, fonts);

  const stackSamples = STACK_VARS.map((variable, index) => {
    const text = lines[index % lines.length];
    return { variable, text, script: onlyScript(text, script.test) };
  });
  const faceStackSamples = fonts.map(face => faceSample(face, fonts, stacks, lines));
  const faceSamples = fonts.map(face => ({ family: face.family, text: textFor(face, lines) }));

  const { afterStacks, afterForcing } = await drive(page, stackSamples, faceStackSamples, faceSamples);

  /* 1. every face the pack publishes has to be a face the browser really loaded */
  for (const face of fonts) {
    const held = found(afterForcing, face.family, face.unicodeRange);
    const loaded = held.filter(f => f.status === "loaded");
    if (!held.length) {
      problems.push(code + ": nothing in document.fonts is '" + face.family + "' over " + (face.unicodeRange || "everything"));
      say("FAIL " + code.padEnd(8) + " face " + face.family + ": no FontFace with that family and range");
    } else if (!loaded.length) {
      problems.push(code + ": '" + face.family + "' is declared and its status is " + held[0].status);
      say("FAIL " + code.padEnd(8) + " face " + face.family + ": status " + held[0].status);
    } else {
      say("ok   " + code.padEnd(8) + " face " + face.family + " loaded over " + (face.unicodeRange || "everything"));
    }
  }

  /* 2. and EVERY one of them has to be pulled in by a stack that names it, before
        anything is forced. One face reached is not the question: a face no stack can
        reach is weight a host downloads and never sees a glyph of. */
  const pulled = [];
  fonts.forEach((face, index) => {
    const sample = faceStackSamples[index];

    if (!sample.variable) {
      problems.push(
        code + ": the pack publishes '" + face.family + "' and no stack that applies to " + code +
        " names it, so nothing in the product will ever ask for a character of it");
      say("FAIL " + code.padEnd(8) + " face " + face.family + ": no stack for " + code + " names it");
      return;
    }

    if (!sample.text) {
      const ahead = sample.ahead.map(other => other.family).join(", ") || "the faces ahead of it";
      problems.push(
        code + ": '" + face.family + "' sits behind " + ahead + " in the " + sample.variable +
        " stack over every character it covers, so it can never be the face that draws one");
      say("FAIL " + code.padEnd(8) + " face " + face.family + ": nothing in the " + sample.variable + " stack reaches it");
      return;
    }

    const loaded = found(afterStacks, face.family, face.unicodeRange).some(f => f.status === "loaded");
    if (!loaded) {
      problems.push(
        code + ": the " + sample.variable + " stack was asked for " + JSON.stringify(sample.text) +
        " and did not pull in '" + face.family + "', so that text was drawn by something else");
      say("FAIL " + code.padEnd(8) + " face " + face.family + ": the " + sample.variable + " stack did not pull it in");
      return;
    }

    pulled.push(face.family + " (" + sample.variable + ")");
  });

  if (pulled.length === fonts.length) {
    say("ok   " + code.padEnd(8) + " stacks pulled in all " + fonts.length + ": " + pulled.join(", "));
  }

  /* 3. and Chromium has to say so too, about a node holding nothing but the script, and
        about enough of it to be an answer. A node whose downloaded face drew two glyphs
        is a node that says nothing about the page a host reads, so the count is held
        against SCRIPT_FLOOR and printed either way. */
  const drawn = await platformFonts(page, stackSamples.map((_, index) => "packScript" + index));
  const nodes = Object.keys(drawn).map(id => {
    const used = drawn[id] || [];
    const own = used.filter(f => f.isCustomFont && f.glyphCount > 0);
    const system = used.filter(f => !f.isCustomFont && f.glyphCount > 0);
    const glyphs = own.reduce((total, f) => total + f.glyphCount, 0);
    return {
      variable: STACK_VARS[Number(id.replace("packScript", ""))],
      own, glyphs,
      clean: own.length > 0 && system.length === 0 && glyphs >= SCRIPT_FLOOR,
      said: used.length
        ? used.map(f => f.familyName + (f.isCustomFont ? "" : " (the system's)") + " x" + f.glyphCount).join(" and ")
        : "nothing at all",
    };
  });

  const clean = nodes.filter(node => node.clean);
  const named = nodes.map(node => node.variable + " drew " + node.said).join("; ");

  if (!clean.length) {
    problems.push(
      code + ": no stack drew " + SCRIPT_FLOOR + " or more glyphs of " + script.label +
      " in fonts the browser downloaded and nothing else (" + named + ")");
    say("FAIL " + code.padEnd(8) + " " + script.label + " was not drawn in " + SCRIPT_FLOOR +
      " downloaded glyphs by any stack: " + named);
  } else {
    const best = clean[0];
    const rest = nodes.filter(node => !node.clean).map(node => node.variable + " in " + node.said);

    say("ok   " + code.padEnd(8) + " " + script.label + " rendered in " +
      best.own.map(f => f.familyName + " x" + f.glyphCount).join(", ") +
      " on " + best.variable + ", " + best.glyphs + " glyphs against a floor of " + SCRIPT_FLOOR +
      ", on " + clean.length + " of " + nodes.length + " stacks" +
      (rest.length ? "; the rest: " + rest.join("; ") : ""));
  }

  return served;
}

(async () => {
  const where = process.argv[2];
  if (!where) {
    console.error("usage: node scripts/ui/pack_fonts_probe.js <unpacked pack folder or folder of pack zips>");
    process.exit(2);
  }

  const packs = packsIn(path.resolve(where));
  const read = packs.map(entry => {
    const packJson = entry.files.get("pack.json");
    if (!packJson) throw new Error(entry.name + " holds no pack.json");
    return { name: entry.name, pack: JSON.parse(packJson.toString("utf8")), files: entry.files };
  });

  /* Every pack's faces are served up front, so one server answers the whole run. */
  const served = new Map();
  for (const entry of read) {
    const { served: theirs } = payload(entry.pack, entry.files, "/lang/");
    theirs.forEach((bytes, url) => served.set(url, bytes));
  }

  const { server, port } = await serve(served);
  const { chromium } = playwright();
  const browser = await chromium.launch({ headless: true });
  const problems = [];
  const said = [];
  const say = line => { said.push(line); console.log(line); };

  try {
    const page = await browser.newPage({ viewport: { width: 1408, height: 900 } });
    await page.addInitScript(STUB);
    await page.goto("http://127.0.0.1:" + port + "/index.html", { waitUntil: "load" });
    await page.waitForTimeout(700);

    for (const entry of read) {
      say("");
      say("== " + entry.name + "  (" + entry.pack.code + ", " + (entry.pack.fonts || []).length + " faces) ==");
      await check(page, entry.pack, entry.files, problems, say);
    }

    console.log("");
    console.log(
      "TOTAL " + problems.length + "  every face the pack publishes loads, every one of them is " +
      "reached by a stack, and the script comes out in them");
    for (const problem of problems) console.log("  " + problem);
    process.exitCode = problems.length ? 1 : 0;
  } finally {
    await browser.close();
    server.close();
  }
})().catch(err => { console.error(err); process.exit(2); });
