/* The page's transport for a headless host (docs/linux-port.md, 4.3), driven for real.
 *
 * WHY THIS EXISTS. The Linux port runs the page against a service with no window, over a
 * WebSocket instead of WebView2. The whole point of the shim is that nothing above the Native
 * block can tell: the same frames, the same Error shape, the same on() listeners, and the page
 * treating the service as a real host rather than as the mock preview. The ways that goes
 * wrong quietly are a socket opened at the wrong address, a frame that drifts from the WebView2
 * shape, a call that never settles after the socket drops, a reconnect that hammers the service
 * or never comes, the page asking again for nothing on the way back, a win.* message sent to a
 * host that has no window, and the lost-link sentence missing from the catalog.
 *
 * THE CODE UNDER TEST is the block between the NATIVE markers in app.js, evaluated here with a
 * stand-in window, document, location, WebSocket and setTimeout, plus the markup in index.html,
 * the stylesheet, the catalog, and the page's own source checks.
 *
 * The rules:
 *   1. WebView2 wins when it is there: no socket is opened even with the service flag set,
 *      available and windowed are both true, and a call goes through postMessage as before.
 *   2. The service branch: the socket opens at ws(s)://host/ws, available is true and windowed
 *      is false, call() sends {id, method, params} and resolves on {id, ok, result}, an error
 *      reply becomes an Error carrying errorId and errorParams, post() sends no id, and a
 *      pushed {event, data} reaches on() listeners.
 *   3. A close refuses every pending call with host.disconnected, a call made while the socket
 *      is down is refused at once the same way, host.lost is raised, and the reconnects wait
 *      1s, 2s, 4s ... capped at 30s, with the wait back at 1s after a successful open.
 *   4. host.reconnected fires after the second connection and never after the first, and the
 *      page answers it by asking again for the realm strip, BakaLoader's own update and the
 *      active realm's state, and by taking the lost-link row down.
 *   5. No win.* frame is ever sent without a window: the transport drops one, every window
 *      site in app.js asks Native.windowed, and index.html hangs html.headless before the body
 *      so app.css never draws the buttons or the grips (a first-frame state, no jump).
 *   6. visibilitychange raises window.active through the same listener the WinForms host feeds,
 *      and the page's listener is the one that sets body.idle.
 *   7. The catalog carries the lost-link sentences, the bar ranks hostLost first, the row is
 *      raised with an again closure, and the page's own checks (check_first_frame.py and
 *      check_catalog.py) still pass with all of this in.
 *
 * Prints one line per rule and exits non zero on any failure.
 *
 *     node scripts/ui/host_transport_selftest.js
 */

"use strict";

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");
const { spawnSync } = require("child_process");

const ROOT = path.join(__dirname, "..", "..");
const WEB = path.join(ROOT, "ValheimBakaLoader", "WebUI");
const APP = fs.readFileSync(path.join(WEB, "app.js"), "utf8").replace(/\r\n/g, "\n");
const HTML = fs.readFileSync(path.join(WEB, "index.html"), "utf8").replace(/\r\n/g, "\n");
const CSS = fs.readFileSync(path.join(WEB, "app.css"), "utf8").replace(/\r\n/g, "\n");
const KEYS = JSON.parse(fs.readFileSync(path.join(WEB, "i18n", "en.json"), "utf8")).keys || {};

let passed = 0;
const failures = [];

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

function block() {
  const from = APP.indexOf("/* NATIVE-BEGIN");
  const to = APP.indexOf("/* NATIVE-END */");
  assert.ok(from >= 0 && to > from, "app.js no longer carries the NATIVE markers");
  return APP.slice(from, to);
}

function body(head) {
  const at = APP.indexOf(head);
  assert.ok(at >= 0, "app.js no longer has " + head);
  return APP.slice(at, APP.indexOf("\n}", at) + 2);
}

/* A stand-in socket the test opens, closes and feeds by hand. */
function fakeSocketClass(log) {
  return class FakeSocket {
    constructor(url) {
      this.url = url;
      this.sent = [];
      this.onopen = this.onmessage = this.onclose = this.onerror = null;
      log.sockets.push(this);
    }
    send(text) { this.sent.push(JSON.parse(text)); }
    open() { this.onopen && this.onopen({}); }
    feed(frame) { this.onmessage && this.onmessage({ data: JSON.stringify(frame) }); }
    close() { this.onclose && this.onclose({}); }
  };
}

/* The block evaluated against a page: a WebView2 host, the service, or nothing at all. */
function page(opts) {
  opts = opts || {};
  const log = { sockets: [], timers: [], posted: [] };
  const docListeners = {};
  const wv = opts.webview ? {
    addEventListener: (ev, fn) => { log.wvListener = fn; },
    postMessage: m => log.posted.push(m),
  } : undefined;
  const context = {
    window: { chrome: wv ? { webview: wv } : undefined, BAKA_HOST: opts.host },
    document: { visibilityState: "visible", addEventListener: (ev, fn) => { docListeners[ev] = fn; } },
    location: { protocol: opts.protocol || "http:", host: opts.hostname || "127.0.0.1:8080" },
    WebSocket: fakeSocketClass(log),
    setTimeout: (fn, ms) => { log.timers.push({ fn, ms }); return log.timers.length; },
    JSON, Promise, Map, Array, String, Error, Math,
  };
  vm.createContext(context);
  /* A const is the script's own, not the context's, so the block's last value is read instead. */
  const Native = vm.runInContext(block() + "\nNative;", context, { filename: "app.js#native" });
  assert.strictEqual(typeof Native, "object", "Native did not come out of the block");
  const fire = () => { const t = log.timers.shift(); assert.ok(t, "no reconnect was scheduled"); t.fn(); return t.ms; };
  return { Native, log, docListeners, fire, doc: context.document, socket: () => log.sockets[log.sockets.length - 1] };
}

const tick = () => new Promise(r => setImmediate(r));

/* A promise's state once the microtasks have run, without waiting on one that never settles. */
async function settled(p) {
  let out = { pending: true };
  p.then(v => { out = { ok: true, v }; }, e => { out = { ok: false, e }; });
  await tick();
  return out;
}

/* Objects made inside the vm have their own prototypes, so they are compared by their shape. */
function same(actual, expected, why) {
  assert.strictEqual(JSON.stringify(actual), JSON.stringify(expected), why);
}

async function main() {
  test("1. WebView2 wins when it is there, even with the service flag set", () => {
    const p = page({ webview: true, host: "service" });
    assert.strictEqual(p.log.sockets.length, 0, "a socket was opened under WebView2");
    assert.strictEqual(p.Native.available, true);
    assert.strictEqual(p.Native.windowed, true, "WebView2 is not windowed");
    p.Native.call("app.info", {});
    same(p.log.posted, [{ id: 1, method: "app.info", params: {} }], "a call no longer goes through postMessage");
    p.Native.post("win.minimize");
    same(p.log.posted[1], { method: "win.minimize", params: {} }, "a window message no longer goes through postMessage");
    /* No host at all is still the preview. */
    const none = page({});
    assert.strictEqual(none.Native.available, false, "a page with no host thinks it has one");
    assert.strictEqual(none.log.sockets.length, 0, "a page with no host opened a socket");
  });

  await (async () => {
    const name = "2. the service branch: the right address, the same frames, the same Error, post() with no id, events to on()";
    try {
      const p = page({ host: "service" });
      assert.strictEqual(p.Native.available, true, "the service is not a real host to the page");
      assert.strictEqual(p.Native.windowed, false, "the service is windowed");
      assert.strictEqual(p.log.sockets.length, 1, "the service branch did not open a socket");
      assert.strictEqual(p.socket().url, "ws://127.0.0.1:8080/ws", "the socket opened at " + p.socket().url);
      const tls = page({ host: "service", protocol: "https:", hostname: "baka.example:8443" });
      assert.strictEqual(tls.socket().url, "wss://baka.example:8443/ws", "an https page did not open wss");
      p.socket().open();
      /* The watchers go on before the replies land, as a page's own catch would be. */
      const a = settled(p.Native.call("servers.list", {}));
      const b = settled(p.Native.call("profiles.get", { name: "Default" }));
      same(p.socket().sent, [{ id: 1, method: "servers.list", params: {} }, { id: 2, method: "profiles.get", params: { name: "Default" } }],
        "a call's frame is not {id, method, params}");
      p.socket().feed({ id: 1, ok: true, result: [{ name: "Final Sunset" }] });
      p.socket().feed({ id: 2, ok: false, error: "No such profile", errorId: "profiles.noSuchProfile", errorParams: { name: "Default" } });
      const ra = await a, rb = await b;
      same(ra, { ok: true, v: [{ name: "Final Sunset" }] }, "an ok reply did not resolve its call");
      assert.strictEqual(rb.ok, false, "an error reply did not reject its call");
      assert.ok(rb.e instanceof Error && rb.e.message === "No such profile", "the error's sentence is not the Error's message");
      assert.strictEqual(rb.e.errorId, "profiles.noSuchProfile", "errorId does not ride on the Error");
      same(rb.e.errorParams, { name: "Default" }, "errorParams do not ride on the Error");
      /* An error reply that named nothing: null, as under WebView2. */
      const c = settled(p.Native.call("x.y"));
      p.socket().feed({ id: 3, ok: false, error: "boom" });
      const rc = await c;
      assert.strictEqual(rc.e.errorId, null); assert.strictEqual(rc.e.errorParams, null);
      same(p.socket().sent[2], { id: 3, method: "x.y", params: {} }, "missing params are not sent as {}");
      /* post(): no id. */
      p.Native.post("page.seen", { hall: "hearth" });
      const last = p.socket().sent[p.socket().sent.length - 1];
      same(last, { method: "page.seen", params: { hall: "hearth" } }, "post() sends an id or the wrong shape");
      assert.ok(!("id" in last), "post() sends an id");
      /* Events. */
      const heard = [];
      p.Native.on("server.status", d => heard.push(d));
      p.Native.on("server.status", d => heard.push("again"));
      p.socket().feed({ event: "server.status", data: { profile: "Final Sunset", status: "Running" } });
      same(heard, [{ profile: "Final Sunset", status: "Running" }, "again"], "a pushed event did not reach every on() listener");
      /* A frame that is not JSON is dropped, never thrown. */
      p.socket().onmessage({ data: "not json" });
      passed++; console.log("  ok   " + name);
    } catch (problem) { failures.push(name); console.log("  FAIL " + name); console.log("       " + (problem && problem.message ? problem.message : problem)); }
  })();

  await (async () => {
    const name = "3. a close refuses pending calls with host.disconnected and reconnects with a doubling wait, 1s to 30s";
    try {
      const p = page({ host: "service" });
      const lost = [];
      p.Native.on("host.lost", d => lost.push(d));
      p.socket().open();
      const a = settled(p.Native.call("server.state", {}));
      p.socket().close();
      const ra = await a;
      assert.strictEqual(ra.ok, false, "a pending call survived the close unsettled");
      assert.ok(ra.e instanceof Error && ra.e.errorId === "host.disconnected", "the refusal is not named host.disconnected");
      assert.strictEqual(ra.e.errorParams, null);
      assert.strictEqual(lost.length, 1, "host.lost was not raised on the close");
      /* Down: a new call is refused at once, nothing is queued on a socket that is not there. */
      const rb = await settled(p.Native.call("server.state", {}));
      assert.ok(rb.ok === false && rb.e.errorId === "host.disconnected", "a call while the socket is down is not refused at once");
      /* The waits: 1s, 2s, 4s, 8s, 16s, 30s, 30s. Each fire opens a new socket that closes again. */
      const waits = [];
      for (let i = 0; i < 7; i++) {
        const sockets = p.log.sockets.length;
        waits.push(p.fire());
        assert.strictEqual(p.log.sockets.length, sockets + 1, "a reconnect fired without opening a socket");
        p.socket().close();
      }
      same(waits, [1000, 2000, 4000, 8000, 16000, 30000, 30000], "the reconnect waits are " + waits.join(","));
      assert.strictEqual(p.log.timers.length, 1, "more than one reconnect is scheduled at a time");
      assert.strictEqual(lost.length, 8, "host.lost is not raised on every close");
      /* A socket that opens puts the wait back at 1s. */
      p.fire(); p.socket().open(); p.socket().close();
      assert.strictEqual(p.fire(), 1000, "a successful open did not put the wait back at 1s");
      /* A late close from a socket already replaced does nothing to the live one. */
      const stale = p.socket();
      p.socket().open();
      assert.strictEqual(p.log.timers.length, 0, "a reconnect is scheduled while the socket is open");
      const c = settled(p.Native.call("app.info", {}));
      const dead = p.log.sockets[p.log.sockets.length - 2];
      dead.close();
      assert.strictEqual((await c).pending, true, "a stale socket's close dropped the live socket's call");
      assert.strictEqual(p.log.timers.length, 0, "a stale socket's close scheduled a reconnect");
      assert.ok(stale === p.socket(), "the live socket was replaced by a stale close");
      passed++; console.log("  ok   " + name);
    } catch (problem) { failures.push(name); console.log("  FAIL " + name); console.log("       " + (problem && problem.message ? problem.message : problem)); }
  })();

  test("4. host.reconnected fires after the second connection, not the first, and the page asks again for what it was showing", () => {
    const p = page({ host: "service" });
    const again = [];
    p.Native.on("host.reconnected", d => again.push(d));
    p.socket().open();
    assert.strictEqual(again.length, 0, "host.reconnected fired on the first connection");
    p.socket().close(); p.fire(); p.socket().close(); p.fire();
    assert.strictEqual(again.length, 0, "host.reconnected fired before the socket was open again");
    p.socket().open();
    assert.strictEqual(again.length, 1, "host.reconnected did not fire on the second connection");
    p.socket().close(); p.fire(); p.socket().open();
    assert.strictEqual(again.length, 2, "host.reconnected does not fire on every later connection");
    /* A first connection that fails before it ever opens is not a reconnection either. */
    const q = page({ host: "service" });
    const early = [];
    q.Native.on("host.reconnected", () => early.push(1));
    q.socket().close(); q.fire(); q.socket().open();
    assert.strictEqual(early.length, 0, "a first open after a failed attempt counted as a reconnection");
    /* The page's answer: the realm strip, BakaLoader's own update, the active realm's state. */
    const at = APP.indexOf('Native.on("host.reconnected",');
    assert.ok(at >= 0, "the page does not listen for host.reconnected");
    const handler = APP.slice(at, APP.indexOf("\n});", at));
    assert.ok(handler.includes("refreshServers();"), "the realm strip is not asked again (servers.list)");
    assert.ok(handler.includes("refreshAppUpdateInfo();"), "BakaLoader's own update is not asked again (app.updateStatus)");
    assert.ok(handler.includes('rpc("server.state").then(st=>{if(st!==FAIL) applyState(st);});'), "the active realm's state is not asked again");
    assert.ok(handler.includes("HOST_LOST=false;") && handler.includes("renderHostLink();"), "the lost-link row is not taken down on the way back");
    assert.ok(handler.includes('toast("ᚱ "+T("service.conn.back"));'), "the page does not say it is connected again");
    assert.ok(body("async function refreshServers(){").includes('Native.call("servers.list",{})'), "refreshServers no longer asks servers.list");
    assert.ok(body("async function refreshAppUpdateInfo(){").includes('Native.call("app.updateStatus",{})'), "refreshAppUpdateInfo no longer asks app.updateStatus");
  });

  test("5. no win.* frame is ever sent without a window: the transport, the four sites, and the first-frame class", () => {
    const p = page({ host: "service" });
    p.socket().open();
    p.Native.post("win.minimize"); p.Native.post("win.dragStart"); p.Native.post("win.chrome", { x: 1 });
    same(p.socket().sent, [], "a win.* message was sent to the service");
    p.Native.post("page.seen", {});
    assert.strictEqual(p.socket().sent.length, 1, "post() stopped sending anything at all");
    /* Every site that posts win.* asks Native.windowed first, and nothing asks available for it. */
    const sites = [
      ['$$(".winbtn[data-win]").forEach(b=>b.addEventListener("click",()=>{\n  if(!Native.windowed) return;\n  Native.post("win."+b.dataset.win);', "the window buttons"],
      ["function postWinChrome(){\n  if(!Native.windowed||!TB_MAXBTN) return;", "the maximize button's square"],
      ['$("#titlebar").addEventListener("mousedown",e=>{\n  if(e.button!==0||!Native.windowed) return;', "the title bar drag"],
      ['$$(".grip").forEach(g=>g.addEventListener("mousedown",e=>{\n  if(e.button!==0||!Native.windowed) return;', "the resize grips"],
    ];
    for (const [text, what] of sites) assert.ok(APP.includes(text), what + " no longer asks Native.windowed");
    const posts = [...APP.matchAll(/Native\.post\("win\.[^"]*"|Native\.post\("win\."\+/g)].length;
    assert.strictEqual(posts, 5, "a win.* post appeared somewhere new: " + posts);
    const drag = APP.slice(APP.indexOf('window.addEventListener("mousemove",e=>{\n  if(!TB_DRAG) return;'), APP.indexOf('window.addEventListener("mouseup"'));
    assert.ok(drag.includes('Native.post("win.dragRestore"'), "the drag restore moved");
    /* The class is hung in the head, before the body, and only for the service without WebView2. */
    const head = HTML.slice(0, HTML.indexOf("<body>"));
    assert.ok(head.includes('if (window.chrome && window.chrome.webview) return;\n  if (window.BAKA_HOST === "service") document.documentElement.classList.add("headless");'),
      "index.html does not hang html.headless before the body for the service");
    assert.ok(!/classList\.(add|toggle)\("headless"/.test(APP), "app.js hangs the class after the frame is up");
    assert.ok(CSS.includes("html.headless .winbtns,html.headless .grip{display:none}"), "app.css does not hide the buttons and the grips under html.headless");
    assert.ok(/<div class="winbtns">/.test(HTML) && (HTML.match(/<div class="grip" data-edge=/g) || []).length === 8, "the markup for the buttons or the grips changed");
  });

  test("6. visibilitychange raises window.active through the same listener the WinForms host feeds", () => {
    const p = page({ host: "service" });
    const heard = [];
    p.Native.on("window.active", d => heard.push(d));
    assert.strictEqual(typeof p.docListeners.visibilitychange, "function", "the page does not listen to visibilitychange");
    p.docListeners.visibilitychange();
    same(heard, [{ active: true }], "a visible tab is not an active window");
    /* The listener reads the tab's state at the moment it fires, not when it was wired. */
    p.doc.visibilityState = "hidden";
    p.docListeners.visibilitychange();
    same(heard[1], { active: false }, "a hidden tab is not an idle window");
    p.doc.visibilityState = "visible";
    p.docListeners.visibilitychange();
    same(heard[2], { active: true }, "a tab shown again is not an active window");
    /* The socket had nothing to do with it: the tab is the only input here. */
    assert.strictEqual(p.socket().sent.length, 0, "reading the tab sent something to the host");
    /* The same listener list the host's own push lands on. */
    const q = page({ host: "service" });
    const seen = [];
    q.Native.on("window.active", d => seen.push(d));
    q.Native.on("window.active", () => seen.push("page"));
    q.docListeners.visibilitychange();
    assert.ok(seen.length === 2 && seen[1] === "page", "window.active did not reach every listener");
    q.socket().open();
    q.socket().feed({ event: "window.active", data: { active: false } });
    same(seen[2], { active: false }, "a host's own window.active does not land on the same listeners");
    /* Under WebView2 the host says it, so the tab is not read. */
    const w = page({ webview: true });
    assert.strictEqual(w.docListeners.visibilitychange, undefined, "WebView2 also reads the tab, so the host's word would be overridden");
    /* The page's one listener sets body.idle. */
    const at = APP.indexOf('Native.on("window.active",d=>{');
    assert.ok(at >= 0, "the page no longer listens for window.active");
    const listener = APP.slice(at, APP.indexOf("\n});", at));
    assert.ok(listener.includes('document.body.classList.toggle("idle",!S.windowActive);'), "window.active no longer sets body.idle");
    assert.strictEqual((APP.match(/Native\.on\("window\.active"/g) || []).length, 1, "window.active has more than one listener");
  });

  test("7. the catalog carries the sentences, the bar ranks the row first, and the page's own checks still pass", () => {
    for (const [id, lore] of [["service.conn.title", "Connection lost"],
                              ["service.conn.lost", "The connection to BakaLoader's service was lost. Trying again."],
                              ["service.conn.back", "Connected again."]]) {
      assert.ok(KEYS[id] && typeof KEYS[id].lore === "string", "the catalog has no " + id);
      assert.strictEqual(KEYS[id].lore, lore, id + " reads " + KEYS[id].lore);
      assert.ok(!/\u2014|\u2013/.test(KEYS[id].lore), id + " carries a long dash");
    }
    const xx = JSON.parse(fs.readFileSync(path.join(WEB, "i18n", "xx.json"), "utf8")).keys || {};
    for (const id of ["service.conn.title", "service.conn.lost", "service.conn.back"]) assert.ok(xx[id], "the pseudo locale was not regenerated: no " + id);
    assert.ok(/const CONDITION_ORDER=\["hostLost","launchHold",/.test(APP), "hostLost is not the first row of the bar");
    const render = body("function renderHostLink(){");
    assert.ok(render.includes('setCondition("hostLost",{') && render.includes('title:T("service.conn.title"),') && render.includes('msg:T("service.conn.lost"),')
      && render.includes("again:renderHostLink,"), "the lost-link row is not raised through setCondition with its again closure");
    assert.ok(render.includes('if(!HOST_LOST){clearCondition("hostLost");return;}'), "the row does not come down through clearCondition");
    const lost = APP.slice(APP.indexOf('Native.on("host.lost",'), APP.indexOf("\n});", APP.indexOf('Native.on("host.lost",')));
    assert.ok(lost.includes("if(HOST_LOST) return;") && lost.includes("HOST_LOST=true;") && lost.includes("renderHostLink();"), "host.lost does not raise the row once per outage");
    assert.ok(APP.includes('{named:"host.disconnected",      textId:"service.conn.lost"}'), "rpc() has no sentence for host.disconnected");
    /* The page's own checks, with all of this in. */
    const py = ["python3", "python"].find(c => spawnSync(c, ["-c", "print(1)"]).status === 0);
    assert.ok(py, "no python to run the page's checks with");
    for (const script of ["check_first_frame.py", "check_catalog.py"]) {
      const r = spawnSync(py, ["-I", path.join(ROOT, "scripts", "i18n", script)], { encoding: "utf8" });
      assert.strictEqual(r.status, 0, script + " fails:\n" + (r.stdout || "") + (r.stderr || ""));
    }
  });

  console.log(failures.length === 0
    ? "host transport selftest: all " + passed + " rules pass"
    : "host transport selftest: " + failures.length + " of " + (passed + failures.length) + " rules FAIL");
  process.exit(failures.length === 0 ? 0 : 1);
}

main().catch(problem => { console.log("host transport selftest: " + (problem && problem.stack || problem)); process.exit(1); });
