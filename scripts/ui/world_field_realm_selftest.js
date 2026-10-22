#!/usr/bin/env node
/* Issue 25: after a realm switch the Settings Editing bar showed the new realm's name with the
   OLD realm's world, because the world box is a list fetched after the switch and everything
   that read it in that window read the last realm's entry. Save reads the same box. This holds
   the two halves of the fix against the real app.js: worldFieldValue trusts the box only once it
   says it was filled for the realm on screen, and the refill draws the bar again. */
"use strict";
const fs = require("fs"), path = require("path"), assert = require("assert"), vm = require("vm");
const ROOT = path.join(__dirname, "..", "..");
const src = fs.readFileSync(path.join(ROOT, "ValheimBakaLoader", "WebUI", "app.js"), "utf8").replace(/\r\n/g, "\n");
function fn(name) {
  const start = src.indexOf("function " + name + "(");
  assert.ok(start >= 0, name + " is in app.js");
  const end = src.indexOf("\n}", start);
  return src.slice(start, end + 2);
}
let pass = 0;
function rule(text, body) { body(); pass++; }

const box = { value: "Sulneil", dataset: {} };
const ctx = { WORLD_NEW_VALUE: "__new__", S: { profileName: "Sulniel_Dev", prefs: { WorldName: "Sulniel_Dev" } },
  $: id => (id === "#fWorld" ? box : null) };
vm.createContext(ctx);
vm.runInContext("var WORLD_NEW_VALUE=this.WORLD_NEW_VALUE;" + fn("worldFieldValue"), ctx);

rule("a box filled for another realm is not this realm's world", () => {
  box.dataset.realm = "Sulniel";
  assert.strictEqual(vm.runInContext("worldFieldValue()", ctx), "Sulniel_Dev");
});
rule("a box never filled yet is not trusted either", () => {
  delete box.dataset.realm;
  assert.strictEqual(vm.runInContext("worldFieldValue()", ctx), "Sulniel_Dev");
});
rule("once filled for this realm, the box is the answer", () => {
  box.dataset.realm = "Sulniel_Dev"; box.value = "Another";
  assert.strictEqual(vm.runInContext("worldFieldValue()", ctx), "Another");
});
rule("the refill stamps the realm taken before the fetch and draws the bar again", () => {
  const body = fn("renderWorldSelect");
  const taken = body.indexOf("const forRealm=S.profileName"), fetched = body.indexOf("await rpc(\"worlds.list\")");
  assert.ok(taken >= 0 && fetched > taken, "the realm is taken before the fetch");
  assert.ok(/dataset\.realm=forRealm/.test(body), "the box is stamped with that realm");
  assert.ok(/renderEditBar\(\)/.test(body), "the bar is drawn again after the refill");
});
console.log("world field realm selftest: all " + pass + " rules hold");
