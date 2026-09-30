/* The contrast gate's own blind spots, each one proved with a planted defect.
 *
 * WHY THIS EXISTS. scripts/ui/contrast_gate.js went green over a tree holding four readable
 * words under 4.5, because of five things it could not read: an opacity written as a
 * percentage, an opacity set by an ancestor rule, a compound class on one element, a colour
 * form the parser did not know, and a @keyframes strip that stopped at the first inner brace.
 * A gate is only worth what it FAILS on, so each of those five gets a defect planted into a
 * scratchpad copy of app.css here. The copy must exit 1 and the tree must exit 0.
 *
 * Nothing in the repository is written to: every copy lives under the system temp folder and
 * is deleted at the end.
 *
 *     node scripts/ui/contrast_gate_proofs.js
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const { execFileSync } = require("child_process");

const ROOT = path.join(__dirname, "..", "..");
const GATE = path.join(__dirname, "contrast_gate.js");
const CSS = path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.css");
const JS = path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js");
const SOURCE = fs.readFileSync(CSS, "utf8");

const HOME = fs.mkdtempSync(path.join(os.tmpdir(), "vbl-contrast-proof-"));

let passed = 0;
const failures = [];

/** The gate over one stylesheet. Answers its exit code and everything it printed. */
function run(css) {
  try {
    const out = execFileSync(process.execPath, [GATE, css, JS], { encoding: "utf8" });
    return { code: 0, out };
  } catch (problem) {
    return { code: problem.status === undefined ? 1 : problem.status,
      out: (problem.stdout || "") + (problem.stderr || "") };
  }
}

function withCss(name, text) {
  const file = path.join(HOME, name);
  fs.writeFileSync(file, text, "utf8");
  return file;
}

function test(name, body) {
  try {
    body();
    passed++;
    console.log("  ok   " + name);
  } catch (problem) {
    failures.push(name);
    console.log("  FAIL " + name);
    console.log("       " + (problem && problem.message ? problem.message : problem));
  }
}

function mustFail(name, css, expect) {
  const r = run(withCss(name + ".css", css));
  if (r.code === 0) throw new Error("the gate passed a stylesheet holding the defect");
  if (expect && r.out.indexOf(expect) < 0)
    throw new Error("the gate failed, but not about " + JSON.stringify(expect) + ":\n" + r.out);
}

/* ------------------------------------------------------------------ the tree itself */

test("the tree passes", () => {
  const r = run(CSS);
  if (r.code !== 0) throw new Error("the gate fails on the real stylesheet:\n" + r.out);
});

/* ------------------------------------------------- (a) an opacity written as a percentage */

test("an opacity written as a percentage is read as a percentage", () => {
  // 45% over bone on the lightest card is about 2.5. Written as .45 the old reader caught it;
  // written as 45% it parsed 45, clamped to 1, and said the word was never dimmed.
  mustFail("pct", SOURCE + "\n.subval{opacity:45%}\n", "at opacity 0.45");
});

/* ------------------------------------------------------ (b) an opacity on an ancestor rule */

test("an opacity on an ancestor rule dims the words inside it", () => {
  // Exactly the rule the tree carried until batch D: the row dims and the label inside it
  // keeps its colour, so the label was measured as though it were never dimmed.
  mustFail("ancestor", SOURCE + "\n.togglerow.gated{opacity:.45}\n", ".togglerow .tl");
});

test("an opacity on a table row dims the cells in it", () => {
  mustFail("rowdim", SOURCE + "\ntbody tr.dim{opacity:.42}\n", "td.mono");
});

/* -------------------------------------------------- (c) a compound class on one element */

test("a compound class on one element still finds its size", () => {
  // .wtab is 11px and .wtab.on has no size of its own. The old size walk split on spaces, so a
  // selector with no space in it had nothing to walk: the rule was listed, not measured.
  const css = SOURCE + "\n.wtab.quiet{color:var(--bronze-hi)}\n";
  mustFail("compound", css, ".wtab.quiet");
  const r = run(withCss("compound2.css", css));
  if (r.out.indexOf(".wtab.quiet { color:var(--bronze-hi) } (no font-size") >= 0)
    throw new Error("the compound rule was listed as unresolvable rather than measured");
});

/* ------------------------------------- (d) a colour the parser cannot read fails out loud */

test("a colour the parser cannot read is a failure and not a note", () => {
  mustFail("unreadable", SOURCE + "\n.subval{color:lab(52% 40 59)}\n",
    "is not a colour this gate can read");
});

test("and the two forms it used to choke on are read rather than failed", () => {
  // rgb() with slashes and hsl() are ordinary CSS. Both used to come back null, which meant
  // listed and unmeasured; now they are measured, so a BAD one fails on its ratio.
  mustFail("slash", SOURCE + "\n.subval{color:rgb(110 110 110 / 90%)}\n", "under 4.5");
  mustFail("hsl", SOURCE + "\n.subval{color:hsl(210 8% 42%)}\n", "under 4.5");

  // and a good one in the same forms passes, so the parser is not just failing everything
  const ok = run(withCss("slashok.css", SOURCE + "\n.subval{color:rgb(232 226 213 / 100%)}\n"));
  if (ok.code !== 0) throw new Error("a legible rgb() with a slash was failed:\n" + ok.out);
});

/* ------------------------------------------------- (e) the @keyframes strip, braces counted */

test("a @keyframes block is stripped whole, stops and all", () => {
  const css = SOURCE + [
    "",
    "@keyframes proofSweep{",
    "  0%{color:var(--bronze-hi);font-size:9px;opacity:.2}",
    "  50%{color:var(--bone-faint);font-size:9px}",
    "  100%{color:var(--bronze-hi);font-size:9px}",
    "}",
    ".proof-after{font-size:10px;color:var(--bone-dim)}",
    "",
  ].join("\n");

  const r = run(withCss("keyframes.css", css));
  if (r.code !== 0)
    throw new Error("a keyframes block's own stops leaked into the rule list:\n" + r.out);
  if (/\b(?:0%|50%|100%)\b/.test(r.out))
    throw new Error("a percentage stop was read as a selector:\n" + r.out);

  // And the rule AFTER the block is still read, so the strip did not eat the rest of the file.
  const bad = run(withCss("keyframes2.css",
    css.replace(".proof-after{font-size:10px;color:var(--bone-dim)}",
      ".proof-after{font-size:10px;color:var(--bronze-hi)}")));
  if (bad.code === 0)
    throw new Error("the rule after a keyframes block was never read:\n" + bad.out);
});

/* ------------------------------------------------------------------ and the four live words */

test("the four words batch D lifted are at or above the floor now", () => {
  const r = run(CSS);
  for (const selector of ["tbody tr.dim", ".statusbar .seg.dim", ".togglerow .tl",
    ".field.gated label", "td.mono"])
    if (r.out.indexOf("FAIL " + selector) >= 0)
      throw new Error(selector + " is still under the floor:\n" + r.out);
});

try { fs.rmSync(HOME, { recursive: true, force: true }); } catch (_) { /* a temp folder */ }

console.log("");
console.log("contrast gate proofs: " + passed + " passed"
  + (failures.length ? ", " + failures.length + " FAILED" : ""));
if (failures.length) process.exit(1);
