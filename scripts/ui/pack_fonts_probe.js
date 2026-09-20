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
  sentences out of the pack's own catalog, and then asks the browser two questions.

    1. Did every face the pack publishes actually load? Asked of document.fonts, matching
       a FontFace by family AND by unicode range, because app.css declares 'Inter' too and
       a pack that registers nothing would otherwise look fine. NOT asked with
       document.fonts.check, which answers true for a family nobody ever declared.

    2. When the language's own sentences are drawn through the product's own stacks, does
       the text come out in a face the PACK carries? Asked twice over: the pack face has
       to be loaded by the stack samples alone, before anything is forced, and Chromium
       has to report a downloaded font as the one it actually rendered the node with.

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

/* The sentences a host actually reads, out of the pack's own catalog. */
function sentences(catalog, test, want) {
  const all = [];
  const walk = value => {
    if (typeof value === "string") { if (value.trim()) all.push(value.trim()); return; }
    if (value && typeof value === "object") Object.keys(value).forEach(k => walk(value[k]));
  };
  walk(catalog.keys || {});

  const script = all.filter(s => test.test(s));
  script.sort((a, b) => b.length - a.length);

  const picked = [];
  for (const line of script) {
    if (picked.length >= want) break;
    if (line.length > 90) continue;
    if (line.length < 6) continue;
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

async function drive(page, code, fonts, stackSamples, faceSamples) {
  await page.evaluate(([code, fonts, stackSamples]) => {
    /* The page's own writer, with the shape the bridge hands over. */
    langInjectFonts(fonts);

    /* And the language the same way the switch sets it: the attribute the stacks hang
       off and the one Chromium picks its own Han face from. */
    if (typeof setLanguageAttributes === "function") setLanguageAttributes(code);
    document.documentElement.lang = code;
    document.documentElement.setAttribute("data-lang", code);

    const old = document.getElementById("packProbe");
    if (old) old.remove();

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
  }, [code, fonts, stackSamples]);

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
    problems.push(code + ": the catalog holds no " + script.label + " sentence to draw");
    return served;
  }

  const stackSamples = STACK_VARS.map((variable, index) => {
    const text = lines[index % lines.length];
    return { variable, text, script: onlyScript(text, script.test) };
  });
  const faceSamples = fonts.map(face => ({ family: face.family, text: textFor(face, lines) }));

  const { afterStacks, afterForcing } = await drive(page, code, fonts, stackSamples, faceSamples);

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

  /* 2. and the language's own sentences have to come out in one of them */
  const pulled = fonts.filter(face => found(afterStacks, face.family, face.unicodeRange).some(f => f.status === "loaded"));
  if (!pulled.length) {
    problems.push(
      code + ": the stacks drew " + script.label + " and pulled in no face the pack carries, " +
      "so every one of them fell through to a system face");
    say("FAIL " + code.padEnd(8) + " stacks pulled in none of the pack's faces");
  } else {
    say("ok   " + code.padEnd(8) + " stacks pulled in " + pulled.map(f => f.family).join(", "));
  }

  /* 3. and Chromium has to say so too, about a node holding nothing but the script */
  const drawn = await platformFonts(page, stackSamples.map((_, index) => "packScript" + index));
  const clean = Object.keys(drawn).filter(id => {
    const used = drawn[id] || [];
    return used.some(f => f.isCustomFont && f.glyphCount > 0) &&
      !used.some(f => !f.isCustomFont && f.glyphCount > 0);
  });

  if (!clean.length) {
    const named = Object.keys(drawn)
      .map(id => STACK_VARS[Number(id.replace("packScript", ""))] + " drew in " +
        (drawn[id] || []).map(f => f.familyName + (f.isCustomFont ? "" : " (the system's)")).join(" and "))
      .join("; ");
    problems.push(code + ": Chromium drew the " + script.label + " with a font it did not download (" + named + ")");
    say("FAIL " + code.padEnd(8) + " " + script.label + " rendered in a system face: " + named);
  } else {
    const names = (drawn[clean[0]] || []).map(f => f.familyName + " x" + f.glyphCount);
    const rest = Object.keys(drawn)
      .filter(id => !clean.includes(id))
      .map(id => STACK_VARS[Number(id.replace("packScript", ""))] + " in " +
        (drawn[id] || []).map(f => f.familyName + (f.isCustomFont ? "" : " (the system's)")).join(" and "));

    say("ok   " + code.padEnd(8) + " " + script.label + " rendered in " + names.join(", ") +
      " on " + clean.length + " of " + Object.keys(drawn).length + " stacks" +
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
    console.log("TOTAL " + problems.length + "  every face the pack publishes is loaded, and the stacks draw in one");
    for (const problem of problems) console.log("  " + problem);
    process.exitCode = problems.length ? 1 : 0;
  } finally {
    await browser.close();
    server.close();
  }
})().catch(err => { console.error(err); process.exit(2); });
