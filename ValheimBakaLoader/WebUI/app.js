"use strict";

/* ============ NATIVE BRIDGE (WebView2 host seam) ============ */
const Native = (() => {
  const wv = window.chrome?.webview;
  let seq = 0; const pending = new Map(); const listeners = new Map();
  if (wv) wv.addEventListener('message', e => {
    const m = e.data;
    if (m && m.id != null && pending.has(m.id)) {
      const {res, rej} = pending.get(m.id); pending.delete(m.id);
      if (m.ok) { res(m.result); return; }
      /* The English sentence is the Error's message, as it has always been. The host
         also names the sentence now, and that id rides on the Error so the catch can
         reach it without changing what anything already reads. Null when the throw
         did not name itself (a framework failure, say). */
      const err = new Error(m.error);
      err.errorId = m.errorId ?? null;
      err.errorParams = m.errorParams ?? null;
      rej(err);
    }
    else if (m && m.event) (listeners.get(m.event) || []).forEach(fn => fn(m.data));
  });
  return {
    available: !!wv,
    post(method, params) { if (wv) wv.postMessage({ method, params: params ?? {} }); },
    call(method, params) {
      if (!wv) return Promise.reject(new Error('no native host'));
      const id = ++seq;
      return new Promise((res, rej) => { pending.set(id, {res, rej}); wv.postMessage({ id, method, params: params ?? {} }); });
    },
    on(event, fn) { const a = listeners.get(event) || []; a.push(fn); listeners.set(event, a); }
  };
})();

const $=s=>document.querySelector(s), $$=s=>[...document.querySelectorAll(s)];
const esc=s=>String(s??"").replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
/* A name to stand out inside a sentence the catalog owns. The markup goes into the SLOT
   rather than into the entry, so the entry stays a plain sentence a translator can move
   the name around inside, and the catalog's no-markup rule still holds over it. esc()
   runs on the name first, which is the whole reason this is a helper and not a template
   spelled out at each call site. */
const bold=s=>"<b>"+esc(s)+"</b>";
/* The same trick for a value that keeps the monospaced face: an address, a port, a file
   name. The markup rides the slot so the catalog entry stays a plain sentence. */
const mono=s=>"<span class=\"mono\">"+esc(s)+"</span>";
const pad=n=>String(n).padStart(2,"0");

/* ---------- THE FORMATTERS ----------
   Numbers, sizes, clocks, spans and sort order all belong to the host's own
   language, and none of them are something a hand rolled function can know: a
   decimal comma, a Russian duration, pinyin order for Chinese. i18n.js holds
   them, built on Intl for whatever language is active, with the English options
   pinned so an English host reads exactly what they read before.

   Every one is reached through intl() rather than through a captured reference,
   because index.html retries a failed include at the plain address and that can
   land after this file has already run. The else arm of each is the shape the
   halls showed before the lookup existed, so a window with no lookup at all is
   a window in English rather than a window full of dashes. */
const intl=()=>window.I18N;
/* The tag every remaining toLocale* call in this file reads. */
const LOC=()=>{const L=intl();return L?L.locale():undefined;};
/* Sorting with case and accents ignored, which is what every sort in the halls
   asked localeCompare for, and cmpExact where the old call asked for the
   default instead. */
const cmpText=(a,b)=>{const L=intl();return L?L.compare(a,b)
  :String(a??"").localeCompare(String(b??""),undefined,{sensitivity:"base"});};
const cmpExact=(a,b)=>{const L=intl();return L?L.compare(a,b,{})
  :String(a??"").localeCompare(String(b??""));};

function fmtT(d){
  const t=new Date(d); if(isNaN(t)) return "-";
  const L=intl();
  return L?L.fmtTime(t):pad(t.getHours())+":"+pad(t.getMinutes());
}
/* "3d ago at 1945" - delta plus wall-clock, the way you'd tell a friend.
   The span is the lookup's, and the two words around it are the catalog's: the
   joiner is one key with both halves in it, because a language that puts the
   clock first cannot reorder two string concatenations. */
function agoAt(d){
  const t=new Date(d); if(isNaN(t)) return "-";
  const s=Math.max(0,(Date.now()-t.getTime())/1000);
  const L=intl();
  const ago=s<60?T("common.ago.just_now")
    :L?L.fmtRelative(s)
    :s<3600?Math.floor(s/60)+"m ago":s<86400?Math.floor(s/3600)+"h ago":Math.floor(s/86400)+"d ago";
  return T("common.ago.at",{ago,clock:pad(t.getHours())+pad(t.getMinutes())});
}

/* RPC wrapper: every call catches + toasts; returns FAIL sentinel instead of rejecting
   so no rejected promise is ever unhandled. */
const FAIL=Symbol("rpc-failed");
/* The last refusal the host named, kept for whoever asks. Two values rather than a
   history: a toast is about the thing that just happened, and anything older is in
   the log. Read by the debug console today.
   THE RECIPE, when the native side starts wording its refusals by id: ask
   I18N.has(errorId) FIRST and only then call T(errorId, errorParams). T() never
   answers null - an id it cannot look up comes back as the id itself - so
   `T(errorId) ?? err.message` would put a dotted name in a toast rather than fall
   back to the English sentence the bridge is still sending beside it. */
window.BAKA_ERR_ID=null;
window.BAKA_ERR_PARAMS=null;
/* The refusals the native side names with an id this catalog also holds a sentence for.
   The two spellings are not the same and cannot be: a throw's id is dotted lower camel
   (bepinex.serversRunning) and a catalog id is dotted lower case (bepinex.reason.servers_running),
   so the pairing is written down here rather than derived. A refusal on this list is read
   out of the catalog in the host's own language; anything not on it keeps the sentence the
   bridge sent, exactly as before. */
const HOST_SENTENCES=[
  {named:"bepinex.serversRunning", textId:"bepinex.reason.servers_running"},
  {named:"bepinex.notALoader",     textId:"bepinex.reason.not_a_loader"},
  {named:"bepinex.integrity",      textId:"bepinex.reason.integrity"},
  {named:"bepinex.tooLarge",       textId:"bepinex.reason.too_large"},
  {named:"bepinex.offline",        textId:"bepinex.reason.offline"},
  {named:"bepinex.busy",           textId:"bepinex.reason.busy"},
  {named:"bepinex.noServerPath",   textId:"bepinex.reason.no_server_path"},
  {named:"bepinex.noWrongFolder",  textId:"bepinex.reason.no_wrong_folder"},
  {named:"worlds.copySourceRequired",    textId:"world.copy.reason.source_required"},
  {named:"worlds.copyBadSourceRef",      textId:"world.copy.reason.bad_source"},
  {named:"worlds.copyBadSubfolder",      textId:"world.copy.reason.bad_subfolder"},
  {named:"worlds.copyUnknownSaveFolder", textId:"world.copy.reason.unknown_save_folder"},
  {named:"worlds.copyTargetRequired",    textId:"world.copy.reason.target_required"},
  {named:"worlds.copyTargetTooLong",     textId:"world.copy.reason.target_too_long"},
  {named:"worlds.copyBadTargetRef",      textId:"world.copy.reason.bad_target"},
  {named:"worlds.copyTargetExists",      textId:"world.copy.reason.target_exists"},
  {named:"worlds.copyServerRunning",     textId:"world.copy.reason.server_running"},
  {named:"worlds.copyNoSuchWorld",       textId:"world.copy.reason.no_such_world"},
  {named:"worlds.copyUnreadable",        textId:"world.copy.reason.unreadable"},
  {named:"worlds.copyFailed",            textId:"world.copy.reason.failed"},
  /* The three the language bridge throws. The endings a pack DOWNLOAD can have are not
     throws at all, so they are a table of their own below. */
  {named:"lang.busy",         textId:"lang.reason.busy"},
  {named:"lang.unknownCode",  textId:"lang.reason.unknown_code"},
  {named:"lang.notInstalled", textId:"lang.reason.not_installed"},
];
/* Every way a language pack download can end, as the pack service names it in the result,
   beside the catalog entry that words it. This is the same question HOST_SENTENCES answers
   asked at the END of a download rather than at the start, which is why it is a second
   table: nothing throws these, so the gate that holds every HOST_SENTENCES row to a real
   throw stays exactly as strict as it was.
   The two halves are spelled differently on purpose and are paired rather than derived. An
   id the host names is lower camel within a segment and a catalog id is lower case, so
   lang.reason.tooLarge is the ending and lang.reason.too_large is the sentence. An ending
   this table does not know is not rendered: an id is not copy, and a page that prints one
   has told the host nothing. */
const LANG_REASONS=[
  {named:"lang.reason.busy",       textId:"lang.reason.busy"},
  {named:"lang.reason.stalled",    textId:"lang.reason.stalled"},
  {named:"lang.reason.cancelled",  textId:"lang.reason.cancelled"},
  {named:"lang.reason.integrity",  textId:"lang.reason.integrity"},
  {named:"lang.reason.tooLarge",   textId:"lang.reason.too_large"},
  {named:"lang.reason.tooOld",     textId:"lang.reason.too_old"},
  {named:"lang.reason.offline",    textId:"lang.reason.offline"},
  {named:"lang.reason.noPack",     textId:"lang.reason.no_pack"},
  {named:"lang.reason.unknownCode",textId:"lang.reason.unknown_code"},
  {named:"lang.reason.builtIn",    textId:"lang.reason.built_in"},
  {named:"lang.reason.checksOff",  textId:"lang.reason.checks_off"},
  {named:"lang.reason.contents",   textId:"lang.reason.contents"},
  {named:"lang.reason.install",    textId:"lang.reason.install"},
];
/* The sentence for an ending a language pack download named, or null when the id is not one
   of them. */
function langReasonText(id,params){
  if(!id) return null;
  const row=LANG_REASONS.find(r=>r.named===id);
  if(!row) return null;
  try{return T(row.textId,params||{});}catch(_){return null;}
}
/* The catalog sentence for a refusal the host named, or null when it named none this
   page owns the words for. */
function hostSentence(id,params){
  if(!id) return null;
  const row=HOST_SENTENCES.find(r=>r.named===id);
  if(!row) return langReasonText(id,params);
  try{return T(row.textId,params||{});}catch(_){return null;}
}
function rpc(method,params){
  return Native.call(method,params).catch(err=>{
    window.BAKA_ERR_ID=err?.errorId||null;
    window.BAKA_ERR_PARAMS=err?.errorParams||null;
    /* A refusal that named itself and that this page has a sentence for is said in the
       reader's own language. Everything else falls back to the wording that always was:
       the method name and the reason travel as named slots so a language that puts the
       reason first can. */
    if(err?.errorId==="bepinex.alreadyMaintained"){ noticeBepInExMaintained(); return FAIL; }
    const own=hostSentence(err?.errorId,err?.errorParams);
    toast("ᚦ "+(own||T("common.rpc.failed.toast",
      {method,detail:err?.message||T("common.error.unknown")})));
    return FAIL;
  });
}

/* ---------- LIVE STATE (native) ---------- */
const S={
  version:"", profileName:null, prefs:null,          // current profile (full ServerPreferences, PascalCase)
  state:null, upSince:null,                          // server state DTO + uptime anchor
  saveSec:null, saveInterval:600,
  players:[], caps:{rcon:false,devcommands:false},
  mods:null, modsScanned:false, modsScanning:false, modsUpdating:false, lastScan:null,
  /* BepInEx, as bepinex.status answers it and bepinex.changed pushes it. One fact for
     the whole install rather than for this profile: every server provisioned from one
     install loads the same loader through junctions and hard links. null means nothing
     has answered yet, which is NOT the same as "no loader" and is never drawn as one. */
  bepinex:null,
  /* When the package list the rows were answered from was read, and which of the two
     addresses answered. Not the same thing as when Scan was pressed: a press that finds
     a list read moments ago keeps the earlier time, because that is the truthful one. */
  modIndexAt:null, modIndexSource:null,
  hexium:false,           // the host's "Also check Hexium" switch, mirrored from userprefs

  modSort:{col:null,dir:0},   // mods table sort: col name|installed|latest|status, dir 0=default 1=asc 2=desc
  /* What is typed in the Mods search box. It lives here rather than in the DOM so a
     re-render keeps it, and it narrows the rows on screen ONLY: every count, the Update
     all button and the unattended paths read the whole list. Cleared on a profile switch,
     because the previous realm's search is not this realm's. */
  modFilter:"",
  /* The same for the Configs hall: it narrows the list of scrolls on screen, and reading,
     saving and reverting all act on the whole file whatever the list is showing. */
  runeFilter:"",
  extIp:null, intIp:null,
  gameVersion:null, networkVersion:null,   // read off the server's own startup banner
  domain:null,            // custom join domain (the Waystone) - shown instead of the raw public IP when set
  invite:null,            // crossplay invite code (shown while known; cleared on stop)
  saveDur:[],             // last 10 world-save durations (ms) for the rolling average
  lastSaveAt:null,        // Date of the last observed world save
  net:{conns:null,zdos:null,sent:null,recv:null,at:null,hist:[]}, // parsed "Connections N ZDOS:… sent:… recv:…" server stats
  servers:[],             // multi-server chip strip: [{name,status,running,playersOnline,active}]
  /* The world-generation dials the host is editing, kept out of the DOM so a re-render never
     drops an unsaved pick. {world, mods:{combat,deathpenalty,...}} for the loaded world, or
     null before any world is loaded. mods:{} means "the host chose Normal for everything",
     which is different from "the dials were never populated for this world" (world mismatch
     or null) - only the former is allowed to clear a stored difficulty on save. */
  worldMods:null,
  crashed:false,          // the last stop was a crash, and nothing has started since
  journal:{},             // player key -> {playSec,deaths,sessions} from the Skald journal
  vikSort:{col:null,dir:0}, // roster sort: name|status|platform|session|playtime|seen|deaths|pos
  /* Last answer from server.updateCheck: how this server was installed, whether Steam has
     an update waiting for it, and whether BakaLoader can apply that update itself. */
  update:{installKind:null,updatePending:false,pendingBytes:0,buildId:null,targetBuildId:null,
          canUpdate:false,running:false,reason:""},
  /* The two app wide paths a server with an empty Directories box falls back to, with
     their variables already filled in, as userprefs.get answers them. Null until that
     read lands, and the two per kind placeholders are what the boxes read until it does.
     Keyed the way paths.check is keyed: "exe" for the executable, "dir" for the folder. */
  userPaths:{exe:null,dir:null},
  /* The last answer paths.check gave for each box, with the path it was asked about kept
     beside it so a note is never shown against a path that has since been retyped. */
  pathChecks:{exe:null,dir:null},
};

/* An update rewrites valheim_server.exe and the managed assemblies under the install
   folder. A start begun while that is happening launches off half written files, so every
   start and restart surface asks this first and says the same sentence when it refuses.
   The native side holds the same gate on IServerUpdateService.IsRunning, so a stale page
   cannot get past it either. */
function updBlockMsg(){return T("srvupd.block.msg");}
function updateBlocksStart(){return !!(S.update&&S.update.running);}
/* A refusal the native side worded itself is shown exactly as it came. Returns true when
   the answer was a refusal, so the caller stops there. */
function rpcRefused(r){
  if(!r||typeof r!=="object"||r.ok!==false) return false;
  const why=String(r.error||r.reason||"").trim();
  toast("ᚦ "+(why||T("common.rpc.refused.toast")));
  if(why) logLine("warn","[BakaLoader] refused: "+why);
  return true;
}

/* True when an event's profile tag belongs to the profile shown in the UI.
   Untagged events (null/undefined) always pass - single-server compatibility. */
const isActiveProfile=p=>p==null||!S.profileName||String(p).toLowerCase()===String(S.profileName).toLowerCase();

const fmtBytes=n=>{
  const L=intl(); if(L) return L.fmtBytes(n);
  n=Number(n); if(!isFinite(n)) return "-";
  if(n<1024) return n+" B";
  if(n<1048576) return (n/1024).toFixed(1)+" KB";
  if(n<1073741824) return (n/1048576).toFixed(1)+" MB";
  return (n/1073741824).toFixed(2)+" GB";
};

/* The dedicated server prints "Connections 1 ZDOS:1428161  sent:3970 recv:1004"
   every ~10 minutes - the only net telemetry it emits. Parsed off the Saga tail. */
const NET_RE=/Connections\s+(\d+)\s+ZDOS:(\d+)\s+sent:(\d+)\s+recv:(\d+)/;
function parseNetLine(line){
  const m=NET_RE.exec(line); if(!m) return;
  S.net.conns=+m[1]; S.net.zdos=+m[2]; S.net.sent=+m[3]; S.net.recv=+m[4]; S.net.at=Date.now();
  S.net.hist.push({t:Date.now(),conns:+m[1],zdos:+m[2],sent:+m[3],recv:+m[4]});
  if(S.net.hist.length>72) S.net.hist.shift(); // ~12h at the 10-min cadence
}

/* ---------- WINDOW CONTROLS ---------- */
$$(".winbtn[data-win]").forEach(b=>b.addEventListener("click",()=>{
  if(!Native.available) return;
  Native.post("win."+b.dataset.win);
}));

/* ---------- THE WINDOW'S OWN STATE ----------
   The page cannot ask Windows how the window is sitting, so the host says so: once when
   the page has loaded, and again every time it changes. Two things read it. The maximize
   button, which drew one fixed square whatever the window was doing and now swaps to the
   restore pair when there is something to restore. And the drag below, which only has
   work to do while the window is maximized. */
const TB_MAXBTN=$('.winbtn[data-win="maximize"]');
/* The maximize glyph is index.html's own, read once rather than written out a second time
   here, so editing the markup edits both states. The restore glyph is the pair of
   overlapping squares Windows draws, in the stroke the other two winbtn glyphs use. */
const TB_GLYPH_MAXIMIZE=TB_MAXBTN?TB_MAXBTN.innerHTML:"";
const TB_GLYPH_RESTORE='<svg viewBox="0 0 11 11">'+
  '<rect x="1.2" y="3.2" width="6.6" height="6.6" fill="none" stroke="currentColor" stroke-width="1.3"/>'+
  '<rect x="3.4" y="1.2" width="6.6" height="6.6" fill="none" stroke="currentColor" stroke-width="1.3"/>'+
  '</svg>';
function renderWinState(){
  if(!TB_MAXBTN) return;
  const max=document.body.dataset.win==="max";
  /* The id goes onto the element as well as the words, so the catalog walk and the
     language switch reach this tooltip the way they reach every static one, and a
     state that arrived before the catalog did is corrected by the same walk. Both ids
     are spelled out at the call rather than held in a variable, because that is what
     the catalog gate reads to know a sentence is still asked for. */
  TB_MAXBTN.setAttribute("data-i18n-title",max?"titlebar.win.restore":"titlebar.win.maximize");
  TB_MAXBTN.title=max?T("titlebar.win.restore"):T("titlebar.win.maximize");
  TB_MAXBTN.innerHTML=max?TB_GLYPH_RESTORE:TB_GLYPH_MAXIMIZE;
}
Native.on("win.state",d=>{
  document.body.dataset.win=d&&d.maximized?"max":"normal";
  renderWinState();
});

/* How far the pointer has to travel before a press on a maximized window's title bar is
   a drag rather than a click. Small on purpose: Windows' own threshold is this order, and
   anything larger feels like the window is stuck for the first part of the movement. */
const TB_DRAG_SLOP=4;
let TB_DRAG=null;

/* draggable region: titlebar, minus interactive elements.
   .tb-interactive is the convention for anything added to the titlebar later: put the
   class on it and it stops being a drag handle, without this selector having to learn
   its name. The three original classes stay listed so nothing existing changes. */
const TB_NO_DRAG=".cmdchip,.winbtns,.winbtn,.tb-interactive";
$("#titlebar").addEventListener("mousedown",e=>{
  if(e.button!==0||!Native.available) return;
  if(e.target.closest(TB_NO_DRAG)) return;
  Native.post("win.dragStart");
  /* Only a maximized window has anything left to watch. A normal one is already inside
     Windows' own move loop by the time this line runs, and the page stops seeing the
     mouse at all, so there is nothing here to track and nothing to restore. */
  if(document.body.dataset.win!=="max") return;
  const bar=$("#titlebar").getBoundingClientRect();
  TB_DRAG={x:e.clientX,y:e.clientY,
    xRatio:bar.width>0?Math.min(1,Math.max(0,(e.clientX-bar.left)/bar.width)):0.5};
});

/* ---------- DRAG A MAXIMIZED WINDOW BACK DOWN ----------
   Windows brings a maximized window back down to size the moment its caption is dragged,
   and carries the move on without the button ever being let go. There is no caption here
   and win.dragStart does nothing while maximized, so the page watches the pointer instead:
   past the slop the press was a drag, the host is told once and takes the move from there.
   A plain click on a maximized title bar still does nothing at all. */
window.addEventListener("mousemove",e=>{
  if(!TB_DRAG) return;
  if(Math.abs(e.clientX-TB_DRAG.x)<TB_DRAG_SLOP&&Math.abs(e.clientY-TB_DRAG.y)<TB_DRAG_SLOP) return;
  const held=TB_DRAG; TB_DRAG=null;   /* once per press, never twice */
  Native.post("win.dragRestore",{xRatio:held.xRatio});
});
window.addEventListener("mouseup",()=>{TB_DRAG=null;});

/* ---------- LANGUAGE ATTRIBUTES ----------
   Two attributes, set together, always. `lang` is what Chromium reads to pick a Han
   face (without it Japanese renders with Simplified glyph shapes) and what a screen
   reader announces; `data-lang` is what the per-language CSS block hangs off. They are
   set here rather than written into index.html so there is one place that knows.
   Called with "en" at boot; the switch will call it again with the chosen tag. */
function setLanguageAttributes(code){
  const tag=String(code||"en").trim()||"en";
  const root=document.documentElement;
  root.lang=tag;
  root.dataset.lang=tag;
  return tag;
}
setLanguageAttributes("en");

/* resize grips */
$$(".grip").forEach(g=>g.addEventListener("mousedown",e=>{
  if(e.button!==0||!Native.available) return;
  e.preventDefault();
  Native.post("win.resizeStart",{edge:g.dataset.edge});
}));

/* ---------- TERMINOLOGY (the "Show Norse names" switch, Upkeep card) ----------
   PLAIN is the one bit the switch answers with: true when the host asked for plain
   English rather than the Norse wording. It used to drive 129 ordered regular
   expressions over text that had already been rendered, which worked for exactly as
   long as the product had one language in it: a word boundary in JavaScript is
   defined against ASCII, so the table was a bare substring replace in Cyrillic and
   in Han, and running it over a translated sentence is silently destructive rather
   than loudly broken.
   So the register is picked inside the lookup instead. I18N.setRegister is handed the
   getter below and T(id) answers with entry.plain when this is true and the entry has
   one, and with entry.lore when it does not. One entry, two registers, no ordering
   hazard, no inflection bleed, and the Norse register becomes something a translator
   can write rather than something a regex produces.
   The switch itself does two things and nothing else: it flips this bit, and it calls
   applyLanguage(), which draws every surface again. */
let PLAIN=false;

/* ---------- THE CATALOG ----------
   T(id, params) is the lookup. i18n.js holds it; this is the short name the
   halls call it by, and the guard is there so a window whose i18n.js did not
   arrive still comes up: every static label carries its English in index.html,
   so the page reads correctly with no catalog at all. Order at every call site
   stays T() then esc() then the DOM. */
const T=(id,params)=>window.I18N?window.I18N.T(id,params):String(id==null?"":id);

/* One sentence, one entry, and a value inside it that keeps its own typeface. A
   catalog value carries no markup (the gate allows that only on the wizard steps, which
   are written into the page unescaped), so the slot takes a marker instead: the whole
   sentence is escaped, and only then does each marker become a <span class="mono">
   around a value escaped on its own. esc() is still the last thing that touched both
   halves, and the translator still gets one whole sentence with named slots.
   The marker is U+0000, which no sentence and no file name on Windows can hold. */
const monoSlot=name=>"\u0000"+name+"\u0000";
function monoFill(text,values){
  let out=esc(text);
  for(const name in values)
    out=out.split(monoSlot(name)).join(`<span class="mono">${esc(values[name])}</span>`);
  return out;
}


/* The English catalog is a file beside the page, fetched once at boot through
   the same cache stamp the other two includes carry. A fetch rather than
   something written into the page, because en.json is the file translators and
   reviewers read and a second copy inline would have drifted from it inside a
   week. Everything the walker fills already holds its English in index.html, so
   the window is right before this resolves and identical after it. */
const I18N_READY=(function(){
  /* Walk ONLY when a catalog actually landed. With none loaded T() answers with the
     id itself, so walking a failed fetch would paint "hearth.saves.label" over the
     English every static node already carries in the page - which is the one thing
     the fallback English is there to prevent. */
  const walk=ok=>{
    try{if(ok&&window.I18N)window.I18N.applyStatic(document);}catch(_){}
    if(ok){try{repaintBootCopy();}catch(_){}}
    /* The boot cloak comes off HERE, and with no language named on purpose. This is the
       end of the boot chain whichever way it went: the pack landed and the document has
       just been walked in it, or the pack did not and the window is in English and will
       stay there. Holding the cloak past this point could only mean a blank window over
       a pack that already failed. The 1500 ms timeout in index.html covers the one case
       this line cannot reach, which is a chain that never ends at all. */
    try{if(window.BAKA_LANG_REVEAL)window.BAKA_LANG_REVEAL();}catch(_){}
    return ok;
  };
  try{
    if(!window.I18N) return Promise.resolve(false);
    window.I18N.setRegister(()=>PLAIN);
    const url=window.BAKA_ASSET?window.BAKA_ASSET("i18n/en.json"):"i18n/en.json";
    return fetch(url,{cache:"no-cache"})
      .then(r=>r.ok?r.json():Promise.reject(new Error("HTTP "+r.status)))
      /* English first, always, and held: it is the fallback every other catalog is read
         over, so a pack missing a line still answers in English rather than in an id.
         Then, and only then, the saved language. The two are in one chain on purpose:
         the walk below is what paints words over the English index.html carries, and
         waiting for the pack is what stops this chain painting English over English
         and then painting a second time.
         Be exact about what that buys, because the wiki gets written from comments like
         this one and this one has already been read for more than it says. What this
         chain buys on its own is that no pass over the document is ever made in a catalog
         the host did not ask for, so nothing is written in English and corrected
         afterwards. Measured the same way: two passes at boot, this one and the
         terminology pass initUpkeep makes, and both are the host's.
         What it did NOT buy, and what the boot cloak now does: the first PAINTED frame.
         The page frame goes up while these two local files are still being read, and it
         used to show the English in the markup for about 130ms with the language landing
         four painted frames later. The host says the language before the document exists
         (window.BAKA_LANG, injected on the WebView2 core before Navigate), the bootstrap
         in index.html hangs html.lang-pending on that, and walk() below takes it off. The
         probe records one sample per painted frame and there is no longer a frame in
         which a non-English window shows English. An English window has no cloak, no
         delay and the same 74ms first frame it always had. */
      .then(cat=>{EN_CATALOG=cat;window.I18N.load(cat,"en");return langBootCatalog();})
      .then(()=>walk(true))
      .catch(e=>{console.warn("[i18n] the English catalog did not load",e);return walk(false);});
  }catch(e){
    console.warn("[i18n] the English catalog did not load",e);
    return Promise.resolve(false);
  }
})();
/* Held where a test or a layout probe can wait on it. */
window.BAKA_I18N_READY=I18N_READY;

/* The dynamic copy that is already on screen before the catalog arrives.
   app.js runs to its last line long before that fetch resolves, and the Hearth paints
   itself on the way down (renderHearth at the bottom of the Hearth block, syncUpkeepGates
   at the bottom of the Upkeep one). Those painters ask T() for words nothing can answer
   yet, and T() answers an unanswerable id with the id, so the first frame would show
   "hearth.appbar.lifecycle.start" on the button until something happened to paint it
   again. Static markup does not have this problem: every data-i18n element carries its
   English in index.html and the walker only ever replaces it.
   So the moment the words land, the same painters run again off the same state. Each one
   re-renders from S rather than from what is on screen, so this is a no-op for everything
   that does not come out of the catalog.
   The condition bar is the awkward one and is worth reading rerenderConditions() for: a
   condition stores the sentence it was raised with, not the reason, so drawing the bar
   again would only re-draw words already chosen. The preview raises one while app.js is
   still being evaluated, so this is not a theoretical case.
   NOT the whole of the re-render path. A modal already open, a wizard pane, the Atlas side
   panel - anything built once and never drawn again - keeps the words it was built with.
   That belongs to rerenderAllCopy(), which the language switch needs anyway; nothing here
   can be open on the first frame, so it is not this function's problem. */
function repaintBootCopy(){
  try{renderHearth();}catch(_){}          /* Hearth card, app bar, waiting-update pill */
  try{syncUpkeepGates();}catch(_){}       /* the gated auto-update row's tooltip */
  try{renderSideVer();}catch(_){}         /* the sidebar version line */
  try{renderWinState();}catch(_){}        /* the maximize button, in whichever state */
  try{renderAppUpdatePill();}catch(_){}   /* the BakaLoader-update pill */
  try{renderMods();}catch(_){}            /* the mod table's pills, tags and marks */
  try{renderPlayers();}catch(_){}         /* the roster's tooltips and its online cell */
  try{renderCaps();}catch(_){}            /* the missing-mods banner's button */
  try{renderCfgList();}catch(_){}         /* the config list's two empty lines */
  try{resetCfgSaveBtn();}catch(_){}       /* the Configs hall's Save button */
  try{renderHearthLog();}catch(_){}       /* the Hearth log card's empty state */
  try{renderSaveBars();}catch(_){}        /* the save-timing card's empty state */
  try{renderSaveAvg();}catch(_){}         /* the rolling write-time average */
  try{renderServerChips();}catch(_){}     /* the sidebar strip's chip tooltips */
  try{renderHexiumCopy();}catch(_){}      /* the Upkeep card's second-mod-site switch */
  try{heraldRenderIntro();}catch(_){}     /* the Discord hall's opening sentence */
  try{heraldRenderPost();}catch(_){}      /* whether a status post is placed */
  try{heraldRenderUrlStat();}catch(_){}   /* and the line under the webhook box */
  try{renderTermStream();}catch(_){}      /* the Saga console's live or paused chip */
  try{renderMead();}catch(_){}            /* the horn of mead, embers and all */
  try{renderTermEmpty();}catch(_){}       /* the Saga console's empty state */
  try{renderAtlasMsg();}catch(_){}        /* the line the Map hall is showing right now */
  try{renderWxBiomes();}catch(_){}        /* the weather box's nine biome pills */
  try{syncCollapsibleTerms();}catch(_){}  /* every folded header's "n settings" hint */
  try{renderCfgRunningNote();}catch(_){}  /* the Settings hall's running-server note */
  try{renderStatusRconPreview();}catch(_){}  /* the status bar's RCON word in the preview */
  try{updAdvLabels();}catch(_){}          /* the three Advanced labels that carry a number */
  try{renderEyeChips();}catch(_){}        /* the two password chips' SHOW / HIDE word */
  try{repaintWorldDialCopy();}catch(_){}  /* the five world dials: options, notes, footnote */
  try{repaintWorldSelectCopy();}catch(_){}  /* the World field: its New world entry and line */
  try{renderWorldDirty();}catch(_){}      /* the unsaved signs, and with them both Directories lines */
  try{renderPlayerMsgLang();}catch(_){}   /* the Upkeep card's player-message select */
  try{renderLangDot();}catch(_){}         /* the sidebar's partial-translation mark */
  /* These two are async, so a throw inside them lands on the promise rather than in the
     try around the call, and an unhandled rejection is a console error on every boot. */
  try{renderWorldSeed()?.catch(()=>{});}catch(_){}   /* the seed field's not-created-yet line */
  try{renderEditBar()?.catch(()=>{});}catch(_){}     /* the Settings hall's editing bar */
  try{rerenderConditions();}catch(_){}    /* every row already standing on the bar */
}

/* ---------- THE LANGUAGE SWITCH ----------
   One path, and every surface on it is drawn AGAIN rather than patched. There is no
   reload: a reload would take the Saga scrollback, both search boxes and, worst of
   the three, whatever is unsaved in the config editor (CFG.dirty). None of those is
   worth a language switch.
   The order matters. The attributes go first, because Chromium picks its Han face off
   `lang` and every measurement after this reads the face that choice lands on. Then
   the static half, which is one walk over every element that carries an id. Then the
   dynamic half: repaintBootCopy() is the painters that draw from S, and after it come
   the surfaces that cannot be open on the first frame and can be open now - an open
   dialog, a wizard pane, the Atlas side panel and its weather box, a row menu, the
   palette's gating tooltips.
   What is deliberately NOT redrawn: the lines already in the Saga console. The
   server's own output is English whatever the interface is reading, and rewriting
   scrollback that arrived in one language into another would be a lie about what the
   server said. The chrome around it is drawn again with everything else. */
function applyLanguage(code){
  const tag=setLanguageAttributes(code||(intl()?intl().locale():"en"));
  /* The Norse CAPTIONS are CSS, not copy: .plain-terms hides every one of them. */
  document.documentElement.classList.toggle("plain-terms",PLAIN);
  try{if(window.I18N)window.I18N.applyStatic(document);}catch(_){}
  /* Every painter that draws from S, including the whole of the first-frame set. */
  try{repaintBootCopy();}catch(_){}
  /* And the surfaces repaintBootCopy leaves alone because nothing can be open on the
     first frame. Each is wrapped on its own so one throw cannot stop the rest. */
  try{renderAppBar();}catch(_){}            /* the Hearth's lifecycle buttons */
  try{renderConditionBar();}catch(_){}      /* the standing condition, words and all */
  try{renderNet();}catch(_){}               /* the network card's labels */
  try{renderWaystone();}catch(_){}          /* the custom-domain chip */
  try{renderVikCols();}catch(_){}           /* the roster's column chooser */
  try{if(SKALD)renderSkald();}catch(_){}    /* the Statistics hall, once it has numbers */
  try{renderModSourceLabels();}catch(_){}   /* the Mods header's site names */
  try{renderModUpdateProgress();}catch(_){} /* a bulk update's progress line */
  try{renderServerUpdate();}catch(_){}      /* the server-update card */
  try{renderUpdatePill();}catch(_){}        /* the waiting-update pill */
  try{renderLaunchHold();}catch(_){}        /* the held start, if one is standing */
  try{heraldRenderIntro();}catch(_){}       /* the Discord hall's opening sentence */
  try{heraldRenderPost();}catch(_){}        /* and its post status */
  try{heraldRenderUrlStat();}catch(_){}     /* and the line under its webhook box */
  try{renderTermStream();}catch(_){}        /* the Saga console's live/paused chip */
  try{renderAtlasSide();}catch(_){}         /* the Map hall's side panel */
  try{renderWeather();}catch(_){}           /* and the weather box inside it */
  try{updatePalGating();}catch(_){}         /* the palette's greyed-out reasons */
  try{redrawOpenModal();}catch(_){}         /* a dialog or a wizard pane, from state */
  try{redrawContextMenu();}catch(_){}       /* an open row menu, at the same corner */
  /* The globe's own menu last, because it is the one surface that can be open WHILE the
     switch runs: a host picks a row and the menu is still under their pointer when the
     window comes back in the new language. It draws from LANG, so this is the same
     redraw every other surface gets. */
  try{renderLangMenu();}catch(_){}          /* the globe menu, rows, notes and all */
  /* The boot cloak, if one is still hanging. Named, so only a redraw in the language
     the cloak was hung for takes it off: a host who opens the globe and picks a third
     language while the boot pack is still coming down has not yet seen the frame this
     was waiting for. In every window that is already up this answers false and costs a
     class lookup. */
  try{if(window.BAKA_LANG_REVEAL)window.BAKA_LANG_REVEAL(tag);}catch(_){}
  return tag;
}
/* The terminology switch is a language switch with the language left alone: the words
   come out of the same entries, the other register of them. One path, so a surface
   that follows one follows both and neither can quietly grow a hole the other has. */
function applyTerms(){return applyLanguage();}

/* ---------- THE GLOBE ----------
   One button in the title bar, one menu under it, and one road from a row to the window
   being drawn again in another language. Nothing here reloads the page: applyLanguage()
   above is the whole of the switch, and it keeps the Saga scrollback, both search boxes
   and whatever is unsaved in the config editor.

   Four things this block is careful about, because each of them is a way to lie to a host:

   1. IT ASKS FOR NOTHING UNTIL THE HOST ASKS. lang.list is allowed to reach the release
      page, so it is called when the globe is opened and when the Upkeep card that holds
      the player-message select is expanded, and never on boot. Opening either one is the
      host asking out loud; a window that simply started has asked nothing.
   2. THE CANCEL BUTTON NEVER CLAIMS MORE THAN THE HOST DID. lang.cancel answers with the
      service's own bool. A press that lands once the pack has begun moving into place
      stops nothing, and the page says so in those words rather than showing a cancelled
      row over a pack that is still being installed.
   3. NOTHING IS WRITTEN IN ENGLISH AND THEN REWRITTEN. langBootCatalog() runs inside the
      same promise that loads the English catalog, between loading it and walking the
      document, so every pass over the document is made in the catalog the host asked for
      and none is made in English for a later pass to correct.
      That is a statement about the PASSES, and for a while this comment also claimed the
      first frame, which the passes alone cannot buy: the page frame is up while those two
      local files are still being read, and a headless harness driving the real boot shape
      put the English in index.html on screen for about 130ms with the language landing
      four painted frames later.
      THERE IS A BOOT CLOAK NOW, and it is the other half. The host injects
      window.BAKA_LANG on the core before Navigate when the saved language is not English
      and has a pack on disk; the bootstrap in index.html reads it before the document
      exists and hangs html.lang-pending, which app.css draws as visibility:hidden on the
      body's children; the walk at the end of the catalog chain takes it off, and a 1500 ms
      timeout takes it off whatever happens, so a pack that never arrives costs a second
      and a half rather than a blank window. An English window is untouched: no class, no
      delay. The English in the markup is still deliberate and still what a failed catalog
      fetch falls back to, and scripts/i18n/check_first_frame.py exists to keep it correct.
   4. THE FACES ARE WRITTEN AT RUNTIME. The font store is content addressed, so the address
      of a face is a hash the stylesheet cannot know. switchLanguage() owns a
      <style id="langFonts"> element and replaces it whole on every switch. */
const LANG={
  /* The last lang.status answer: which language is saved, where its words are, what its
     pack is missing, and what the quiet post-update fetch decided. */
  status:null,
  /* The last lang.list answer, which is everything the menu draws. Null until the host
     opens the globe or expands the Upkeep card. */
  list:null,
  /* The code being downloaded right now, or null. One at a time, app wide: the service
     owns that latch and refuses a second with lang.busy. */
  busyCode:null,
  /* The last lang.downloadProgress for busyCode. */
  prog:null,
  /* The row that keeps a Try again, and the ending that put it there. */
  failed:null,
  /* The code a cancel has been asked for and not yet answered. A report landing in that
     gap redraws the row, and without this the button it redraws would come back live and
     invite a second press at something already stopping. */
  cancelling:null,
  /* True once a lang.list is in flight, so two presses do not make two requests. */
  asking:false,
  /* The code THIS window asked the host to switch to, held until the lang.changed that
     the ask produces has been seen and dropped. One switch has to be one re-render, and
     without this it was two.
     Why it needs holding at all: the host raises LanguageChanged while it is still
     inside the lang.set call, so the event reaches this window BEFORE the reply it is
     waiting for does. Every guard downstream reads what the window is showing now,
     which at that instant is still the old language, so the event read as somebody
     else's switch and the window drew itself twice - once off the event and once off
     the reply, with a second pack fetch between them.
     It is the origin token by another name, kept on this side rather than round tripped
     through the host, because the page is the only place that knows which press started
     this. Consumed once: the next lang.changed naming this code is genuinely another
     window's and is followed like any other. */
  mine:null,
  /* The saved PlayerMessageLanguage: "same" as the interface, or a code of its own. Read
     off the same userprefs document the Upkeep card is painted from. */
  playerMessages:"same",
};
/* The English catalog, held from boot so switching back to it costs no fetch. The lookup
   keeps its own English fallback, but there is no API for making the fallback active
   again, and a second fetch of a file already read is a round trip for nothing. */
let EN_CATALOG=null;

/* ---- what the menu knows ---- */
function langRows(){
  const rows=(LANG.list&&LANG.list.languages)||[];
  return rows.filter(l=>l&&l.code);
}
/* The language the window is reading right now. The list's own answer first, then the
   status, then the attribute the switch set, so this is right before either RPC lands. */
function langCurrent(){
  return (LANG.list&&LANG.list.current)||(LANG.status&&LANG.status.current)||
    document.documentElement.dataset.lang||"en";
}
function langRowFor(code){return langRows().find(l=>l.code===code)||null;}
/* What a language calls itself, which is what the menu lists and what a toast names. The
   code itself is the floor: a name nothing has answered for yet is still recognisable. */
function langNameOf(code){
  const row=langRowFor(code);
  return row?(row.nativeName||row.englishName||row.code):String(code==null?"":code);
}
/* The one line under a language's own name. Six states, and the order is the order a host
   reads them in: what they are using, what they have, what it would cost, and why they
   cannot have it. */
function langRowLine(l){
  if(l.code===langCurrent()){
    /* Current AND behind. The two facts are not alternatives and the row used to show
       only the first, so the one host who most needs to know the words on screen come
       from an older pack - the host reading them right now - was the only one the menu
       did not tell. The line below it is where every other language says the same
       thing, and this is the same sentence with what it is joined to the front. */
    return (l.installed&&!l.matchesApp&&!l.builtIn)
      ?T("lang.row.current_older_pack",{version:l.installedVersion||""})
      :T("lang.row.current");
  }
  if(l.builtIn) return T("lang.row.built_in");
  if(l.installed&&l.matchesApp) return T("lang.row.installed");
  if(l.installed) return T("lang.row.older_pack",{version:l.installedVersion||""});
  if(l.available) return T("lang.row.not_installed",{size:fmtBytes(l.bytes||0)});
  if(!(LANG.list&&LANG.list.manifest&&LANG.list.manifest.ok)) return T("lang.row.offline");
  return T("lang.row.not_published");
}
/* A row there is nothing to press. Two of the six states have no pack on disk and none to
   fetch either: the release page has not published one for this version, or the page could
   not be reached at all. Both used to be pressable, and both answered a press with a
   failure toast that said what the row already said. The row keeps its reason and stops
   claiming to be a control: no menuitem role, no tab stop, no hover. */
function langRowInert(l){
  if(!l) return false;
  return !l.builtIn&&!l.installed&&!l.available&&l.code!==langCurrent();
}
/* The word for the phase a download is in. The service names the phase; the page owns the
   words, because a phase name is not copy. */
function langPhaseWord(phase){
  if(phase==="downloading") return T("lang.phase.downloading");
  if(phase==="verifying") return T("lang.phase.verifying");
  if(phase==="installing") return T("lang.phase.installing");
  return T("lang.phase.resolving");
}

/* ---- the menu ---- */
/* The progress row, which replaces the status line while a pack is coming down. The bar is
   determinate the moment the host has reported a byte total and indeterminate before that:
   a bar that sits at zero while the release page is being resolved reads as stuck, and a
   sweeping band reads as working. Cancel is live until the pack begins moving into place,
   and from then it is disabled and says why on its tooltip rather than disappearing. */
function langProgressHtml(code){
  const p=LANG.prog||{};
  const total=Number(p.bytesTotal)||0;
  const done=Number(p.bytesDone)||0;
  const pct=total>0?Math.max(0,Math.min(100,Math.round(done/total*100))):0;
  /* Two reasons the button is not pressable, and only one of them is worth a tooltip: a
     cancel already asked for is simply on its way, and a pack already being installed is
     a thing the host needs told. */
  const tooLate=p.phase==="installing";
  const asked=LANG.cancelling===code;
  const bar=total>0
    ?`<div class="lm-bar" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${pct}" `+
       `aria-label="${esc(T("lang.progress.aria",{language:langNameOf(code)}))}">`+
       `<div class="lm-fill" style="width:${pct}%"></div></div>`
    :`<div class="lm-bar indeterminate" role="progressbar" `+
       `aria-label="${esc(T("lang.progress.aria",{language:langNameOf(code)}))}">`+
       `<div class="lm-fill"></div></div>`;
  const line=total>0
    ?langPhaseWord(p.phase)+" · "+T("lang.progress.sized",{done:fmtBytes(done),total:fmtBytes(total)})
    :langPhaseWord(p.phase);
  /* The empty mark column keeps the name on the same left edge as every other row, so a
     row turning into this one does not appear to jump sideways. */
  return `<span class="lm-mark"></span>`+
    `<div class="lm-text"><span class="lm-name">${esc(langNameOf(code))}</span>`+
    `<span class="lm-sub" data-lang-phase>${esc(line)}</span>${bar}</div>`+
    `<button class="lm-act" data-lang-cancel="${esc(code)}"${tooLate||asked?" disabled":""}`+
    `${tooLate?` title="${esc(T("lang.cancel.too_late"))}"`:""}>${esc(T("lang.cancel"))}</button>`;
}
/* One row, in whichever of its three shapes applies: coming down, failed and offering
   another go, or standing there waiting to be picked. */
function langRowHtml(l){
  const here=l.code===langCurrent();
  if(l.code===LANG.busyCode)
    return `<div class="lm-row busy" data-lang-busy="${esc(l.code)}">${langProgressHtml(l.code)}</div>`;

  const failed=LANG.failed&&LANG.failed.code===l.code?LANG.failed:null;
  const line=failed
    ?(langReasonText(failed.reasonId,failed.reasonParams)||T("common.error.unknown"))
    :langRowLine(l);
  /* The machine tag stands down while the row is carrying a failure, and only then. A
     failing row already has a button beside it, and with the tag there too the reason was
     left about ninety pixels to be read in: the checksum sentence came out six lines deep
     at three words a line, which is a sentence a host has to work at rather than read.
     The tag is a standing property of the pack and will be back the moment the row is
     itself again; the reason is the only thing on that row that is about right now. The
     note at the foot of the menu still carries the machine sentence for the language
     actually being read, so nothing is lost by holding it back here. */
  const machine=(l.status==="machine"&&!l.builtIn&&!failed)
    ?`<span class="lm-tag">${esc(T("lang.row.machine"))}</span>`:"";
  const retry=failed
    ?`<button class="lm-act" data-lang-retry="${esc(l.code)}">${esc(T("lang.retry"))}</button>`:"";
  /* A row with nothing behind it is a standing reason, not a control. It still carries its
     code so the menu can be read back, and it carries no role, no tab stop and no press.
     The tag stands down here for the failing row's reason and one of its own: a language
     the release page has published no pack for cannot have a machine translated pack, so
     the tag was saying something about a file that does not exist, and it was saying it in
     the ninety pixels the longest sentence in this menu needs to stay on one line. */
  if(!failed&&langRowInert(l))
    return `<div class="lm-row off" data-lang-inert="${esc(l.code)}">`+
      `<span class="lm-mark"></span>`+
      `<span class="lm-text"><span class="lm-name">${esc(l.nativeName||l.code)}</span>`+
      `<span class="lm-sub">${esc(line)}</span></span></div>`;
  return `<div class="lm-row${here?" on":""}" role="menuitem" tabindex="0" data-lang-row="${esc(l.code)}">`+
    `<span class="lm-mark">${here?"ᚠ":""}</span>`+
    `<span class="lm-text"><span class="lm-name">${esc(l.nativeName||l.code)}</span>`+
    `<span class="lm-sub">${esc(line)}</span></span>${machine}${retry}</div>`;
}
/* The whole menu, drawn from LANG and nothing else, so the language switch redraws it by
   calling this again. */
function renderLangMenu(){
  const menu=$("#langMenu"); if(!menu) return 0;
  const rows=langRows();
  let html=`<div class="lm-head">${esc(T("lang.menu.title"))}</div>`;
  if(!rows.length){
    html+=`<div class="lm-note">${esc(T("lang.menu.loading"))}</div>`;
  }else{
    html+=rows.map(langRowHtml).join("");
    /* The two standing notes. Neither is ever a toast: a toast is about something that
       just happened, and both of these are facts about the words on screen right now. */
    const missing=Number(LANG.status&&LANG.status.missingKeys)||0;
    if(missing>0) html+=`<div class="lm-note">${esc(T("lang.note.partial",{count:missing}))}</div>`;
    const cur=langRowFor(langCurrent());
    if(cur&&cur.status==="machine"&&!cur.builtIn)
      html+=`<div class="lm-note">${esc(T("lang.note.machine"))}</div>`;
    if(LANG.list&&LANG.list.checkEnabled===false)
      html+=`<div class="lm-note warn">${esc(T("lang.note.checks_off"))}</div>`;
  }
  menu.innerHTML=html;
  return rows.length;
}
/* Paints the bar in place rather than rebuilding the menu on every report. Rebuilding
   would restart the sweep animation sixty times over a large pack and throw away the row
   the pointer is on; this writes the four things that actually changed. */
function langPaintProgress(){
  const menu=$("#langMenu");
  const row=menu?menu.querySelector("[data-lang-busy]"):null;
  if(!row||row.getAttribute("data-lang-busy")!==LANG.busyCode){renderLangMenu();return false;}
  row.innerHTML=langProgressHtml(LANG.busyCode);
  return true;
}
function langMenuIsOpen(){return !!$("#langMenu")?.classList.contains("open");}
function langMenuClose(){
  const menu=$("#langMenu"), btn=$("#langBtn");
  if(menu) menu.classList.remove("open");
  if(btn) btn.setAttribute("aria-expanded","false");
  return false;
}
function langMenuOpen(){
  const menu=$("#langMenu"), btn=$("#langBtn");
  if(!menu) return false;
  renderLangMenu();
  menu.classList.add("open");
  if(btn) btn.setAttribute("aria-expanded","true");
  /* Opening the globe IS the host asking out loud, so this is the one place the page is
     allowed to make the app reach the release page on its own. */
  langRefresh();
  return true;
}
function langMenuToggle(){return langMenuIsOpen()?langMenuClose():langMenuOpen();}

/* ---- the host's answers ---- */
/* Asks for the whole menu once. Quiet: a window with no host behind it (the browser
   preview) has BakaPreview.langList instead, and a refusal here is a menu that keeps the
   rows it had rather than a toast about a press the host did not make. */
async function langRefresh(){
  if(!Native.available||LANG.asking) return false;
  LANG.asking=true;
  const r=await Native.call("lang.list",{}).catch(e=>{
    console.warn("[lang] the language list could not be read",e);return null;});
  LANG.asking=false;
  if(!r) return false;
  LANG.list=r;
  renderLangMenu();
  renderPlayerMsgLang();
  return true;
}
/* The first frame's language. Runs inside the catalog boot, between the English catalog
   landing and the document being walked, so the walk paints the host's own language once
   rather than painting English and then painting over it.
   Everything in here is quiet and nothing in here throws: a window whose pack cannot be
   read is a window in English, which is exactly what it was before this existed. */
async function langBootCatalog(){
  /* The walk seam. A browser has no host to ask, and the one rule worth proving here is
     an ORDER rather than a value, so a harness seeds the answer lang.status would have
     given before app.js runs and then counts how many times the document was walked.
     Nothing in the product ever writes this, and it is read once, here. */
  const st=Native.available
    ?await Native.call("lang.status",{}).catch(e=>{
      console.warn("[lang] the saved language could not be read",e);return null;})
    :(window.BAKA_LANG_BOOT||null);
  if(!st) return false;
  LANG.status=st;
  if(!st.stringsUrl||st.current==="en") return false;
  try{
    const r=await fetch(st.stringsUrl,{cache:"no-cache"});
    if(!r.ok) throw new Error("HTTP "+r.status);
    const cat=await r.json();
    langInjectFonts(st.fonts);
    setLanguageAttributes(st.current);
    if(window.I18N) window.I18N.load(cat,st.current);
    return true;
  }catch(e){
    console.warn("[lang] the installed pack did not load, so the window stays in English",e);
    return false;
  }
}

/* ---- the faces ---- */
/* One @font-face per face the pack carries, written into an element this owns and
   replaced whole on every switch. The families and the addresses come from pack.json,
   which the service only writes after the archive matched its published checksum, and
   the three characters that could end a declaration early are dropped anyway: a
   stylesheet built from a file is a stylesheet somebody else wrote.
   Handed nothing, the element goes: an English window declares no pack faces at all. */

/* The format word for a face, read off the address rather than assumed. The store admits
   five extensions (woff2, woff, ttf, otf, ttc) and names every stored file after one of
   them, so a pack publishing a .ttf face has to be declared truetype: a src whose declared
   format does not match the bytes is a src the browser skips, and the face installs, hashes,
   sniffs clean and then never draws. Anything unrecognised, which the store should not be
   able to produce, is declared as the one the store assumes when a face carries no
   extension at all. */
function langFontFormat(url){
  const match=/\.([A-Za-z0-9]{1,8})(?:[?#]|$)/.exec(String(url==null?"":url));
  switch((match&&match[1]||"").toLowerCase()){
    case "woff": return "woff";
    case "ttf": return "truetype";
    case "otf": return "opentype";
    case "ttc": return "collection";
    default: return "woff2";
  }
}

function langInjectFonts(fonts){
  const clean=v=>String(v==null?"":v).replace(/[;{}"'<>\\]/g,"").trim();
  const list=(Array.isArray(fonts)?fonts:[]).filter(f=>f&&f.family&&f.url);
  let style=document.getElementById("langFonts");
  if(!list.length){if(style)style.remove();return 0;}
  if(!style){
    style=document.createElement("style");
    style.id="langFonts";
    document.head.appendChild(style);
  }
  style.textContent=list.map(f=>
    "@font-face{font-family:'"+clean(f.family)+"';"+
    "font-style:"+(clean(f.style)||"normal")+";"+
    "font-weight:"+(clean(f.weight)||"400")+";"+
    "font-display:swap;"+
    "src:url('"+clean(f.url)+"') format('"+langFontFormat(clean(f.url))+"');"+
    (f.unicodeRange?"unicode-range:"+clean(f.unicodeRange)+";":"")+
    "}").join("\n");
  return list.length;
}

/* ---- the switch ---- */
/**
 * Puts the window into the language lang.set just answered for: the pack's words, its
 * faces, and one redraw. English is the one case with no fetch at all, because its
 * catalog was read at boot and is still held.
 * @param {object} payload what lang.set answered: code, stringsUrl, fonts
 * @returns {Promise<boolean>} false when the words could not be read, in which case
 *   nothing on screen changed
 */
async function switchLanguage(payload){
  const p=payload||{};
  const code=p.code||"en";
  if(p.stringsUrl){
    let cat=null;
    try{
      const r=await fetch(p.stringsUrl,{cache:"no-cache"});
      if(r.ok) cat=await r.json();
    }catch(e){console.warn("[lang] the pack catalog did not load",e);}
    if(!cat) return false;
    langInjectFonts(p.fonts);
    if(window.I18N) window.I18N.load(cat,code);
  }else{
    if(!EN_CATALOG) return false;
    langInjectFonts(null);
    if(window.I18N) window.I18N.load(EN_CATALOG,"en");
  }
  if(LANG.status) LANG.status=Object.assign({},LANG.status,
    {current:code,missingKeys:Number(p.missingKeys)||0,stringsUrl:p.stringsUrl||null,fonts:p.fonts||[]});
  if(LANG.list) LANG.list=Object.assign({},LANG.list,{current:code});
  applyLanguage(code);
  return true;
}
/* A row was picked. English and an installed language are one call; anything else is a
   download first, and the row turns into the progress row for as long as that takes. */
async function langPick(code){
  if(!code) return false;
  if(code===langCurrent()){langMenuClose();return false;}
  const row=langRowFor(code);
  /* Belt and braces for the two rows the menu draws inert: a press that arrives anyway
     (a keyboard walker, a stale DOM under a redraw) must not start a download that has no
     pack to fetch and would only come back as a failure. */
  if(langRowInert(row)) return false;
  if(row&&!row.installed&&!row.builtIn) return langDownload(code);
  return langSet(code);
}
/* The switch itself, once the words are known to be on disk. */
async function langSet(code){
  /* Claimed BEFORE the call, because the event this call raises can arrive before the
     call answers. See LANG.mine. */
  LANG.mine=code;
  const r=await rpc("lang.set",{code});
  /* The call itself did not land, so the host changed nothing and raised nothing. The
     claim goes with it: left standing it would swallow the next real switch from
     another window. */
  if(r===FAIL){if(LANG.mine===code)LANG.mine=null;return false;}
  /* Past this line the claim is cleared by ONE thing: the lang.changed this call raised,
     arriving at the handler below. That is safe exactly as long as a successful lang.set
     always raises it, which is not a hope: the rpc writes the preference and calls
     LanguagePacks.NotifyLanguageChanged in the same block (BlendWindow.Bridge.cs), and
     BlendWindowLanguageBridgeTests pins both lines to that block. A future lang.set that
     succeeds quietly would leave the claim standing and swallow the next genuine switch
     to this same language from another window, so it is that test that has to go red
     first rather than this page that has to guess. */
  const ok=await switchLanguage(r);
  if(!ok){toast("ᚦ "+T("lang.toast.failed",
    {language:langNameOf(code),reason:T("lang.reason.contents")}));return false;}
  langMenuClose();
  toast("ᚠ "+T("lang.toast.switched",{language:langNameOf(code)}));
  return true;
}
/* The download, start to finish. The RPC is awaited for the whole of it and the bar is
   driven by the events that arrive meanwhile, which is why the row can show a phase the
   answer has not reported yet. */
async function langDownload(code){
  /* A second pack while one is coming down is refused by the service, and the host has to
     be told why rather than pressing a row that does nothing. The service's own words,
     since it is the service's own rule. */
  if(LANG.busyCode){
    if(LANG.busyCode!==code) toast("ᚦ "+T("lang.reason.busy"));
    return false;
  }
  LANG.busyCode=code;
  LANG.cancelling=null;
  LANG.failed=null;
  LANG.prog={code,phase:"resolving",percent:-1,bytesDone:0,bytesTotal:0};
  renderLangMenu();
  const r=await rpc("lang.download",{code});
  LANG.busyCode=null;
  LANG.cancelling=null;
  LANG.prog=null;
  if(r===FAIL){renderLangMenu();return false;}
  if(r.cancelled){
    renderLangMenu();
    toast("ᛊ "+T("lang.toast.cancelled"));
    return false;
  }
  if(!r.ok){
    LANG.failed={code,reasonId:r.reasonId,reasonParams:r.reasonParams};
    renderLangMenu();
    toast("ᚦ "+T("lang.toast.failed",{language:langNameOf(code),
      reason:langReasonText(r.reasonId,r.reasonParams)||T("common.error.unknown")}));
    return false;
  }
  /* The list is stale the moment a pack lands, so it is asked for again before the row is
     drawn as Downloaded. */
  await langRefresh();
  return langSet(code);
}
/* The cancel, and the one thing it must never do. The service answers whether the press
   actually stopped anything; a press that lost the race to the install step is told so in
   those words, and the row carries on to the end it was already heading for. */
async function langCancel(code){
  LANG.cancelling=code;
  const r=await rpc("lang.cancel",{code});
  if(r===FAIL){LANG.cancelling=null;return false;}
  if(!r.cancelled){
    /* It lost the race, so the row goes back to being a live download heading for the end
       it was already heading for. Saying anything else would be the one lie a cancel
       button must never tell. */
    LANG.cancelling=null;
    langPaintProgress();
    toast("ᚦ "+T("lang.toast.too_late",{language:langNameOf(code)}));
    return false;
  }
  return true;
}

/* ---- the sidebar mark and the player-message select ---- */
/* The partial-translation mark. A dot rather than a toast, for the same reason the menu
   note is a note: it is a standing fact about the words on screen. */
function renderLangDot(){
  const dot=$("#langDot"); if(!dot) return 0;
  const missing=Number(LANG.status&&LANG.status.missingKeys)||0;
  const said=missing>0?T("lang.note.partial",{count:missing}):"";
  dot.classList.toggle("on",missing>0);
  dot.title=said;
  dot.setAttribute("aria-label",said);
  return missing;
}
/* The English for the one option every install has, read off the markup ONCE, before
   the first rebuild replaces it. Read on every call instead, it would answer with the
   markup the first time and with nothing every time after, which is the shape a
   fallback has when it has quietly stopped being one. */
const PLAYER_MSG_SAME_EN=(()=>{
  const o=$('#selPlayerMsgLang option[value="same"]');
  return o?String(o.textContent||"").trim():"";
})();
/* The Upkeep card's "Messages to players" select. Its options are the languages a pack is
   actually installed for, because a language nobody has downloaded has no words to write
   a countdown in. Before lang.list has been asked for, the select still carries what is
   saved, so it never shows a choice the host did not make. */
function renderPlayerMsgLang(){
  const sel=$("#selPlayerMsgLang"); if(!sel) return 0;
  /* This runs as a statement while app.js is still being evaluated, which is long
     before the catalog fetch resolves, and the first option it writes is a T() call.
     With no catalog T() answers an id with the id, so that option read
     "settings.player_messages.same" for two painted frames. An <option> is not
     somewhere the static walker can put the English back either: the walker replaces
     what markup carries, and this had already wiped it.
     So the one option every install has is worded from the catalog when there is one
     and from the English index.html carries when there is not. Asking has() for this one
     id rather than whether a catalog landed at all is deliberate: a window whose en.json
     never arrived keeps a working select with the languages it has, rather than losing the
     setting altogether. */
  const sameWord=(window.I18N&&window.I18N.has("settings.player_messages.same"))
    ?T("settings.player_messages.same"):PLAYER_MSG_SAME_EN;
  const saved=String(LANG.playerMessages||"same");
  const rows=langRows().filter(l=>l.installed||l.builtIn);
  const opts=[{code:"same",name:sameWord}];
  rows.forEach(l=>opts.push({code:l.code,name:l.nativeName||l.englishName||l.code}));
  if(!opts.some(o=>o.code===saved)) opts.push({code:saved,name:langNameOf(saved)});
  sel.innerHTML=opts.map(o=>
    `<option value="${esc(o.code)}"${o.code===saved?" selected":""}>${esc(o.name)}</option>`).join("");
  sel.value=saved;
  return opts.length;
}

/* Both are painted now and again the moment the catalog lands, the same as every other
   painter repaintBootCopy runs: the select and the dot are on screen from the first frame
   and would otherwise carry ids until something happened to redraw them. */
renderPlayerMsgLang();
renderLangDot();

/* ---- wiring ---- */
$("#langBtn")?.addEventListener("click",e=>{e.stopPropagation();langMenuToggle();});
$("#langBtn")?.addEventListener("keydown",e=>{
  if(e.key!=="Enter"&&e.key!==" ") return;
  e.preventDefault(); langMenuToggle();
});
/* One delegated handler for the whole menu, so a redraw never leaves a dead button
   behind. Cancel and Try again are inside a row, so both stop the press from reaching the
   row underneath them: a click on Cancel must not also pick the language. */
$("#langMenu")?.addEventListener("click",e=>{
  e.stopPropagation();
  const t=e.target;
  if(!t||typeof t.closest!=="function") return;
  const cancel=t.closest("[data-lang-cancel]");
  if(cancel){
    if(cancel.disabled) return;
    cancel.disabled=true;
    langCancel(cancel.getAttribute("data-lang-cancel"));
    return;
  }
  const retry=t.closest("[data-lang-retry]");
  if(retry){langDownload(retry.getAttribute("data-lang-retry"));return;}
  const row=t.closest("[data-lang-row]");
  if(row) langPick(row.getAttribute("data-lang-row"));
});
$("#langMenu")?.addEventListener("keydown",e=>{
  if(e.key!=="Enter"&&e.key!==" ") return;
  const row=e.target&&typeof e.target.closest==="function"?e.target.closest("[data-lang-row]"):null;
  if(!row) return;
  e.preventDefault();
  langPick(row.getAttribute("data-lang-row"));
});
document.addEventListener("click",()=>{if(langMenuIsOpen())langMenuClose();});
document.addEventListener("keydown",e=>{if(e.key==="Escape"&&langMenuIsOpen())langMenuClose();});
/* The card that holds the player-message select. Expanding it is the second place the
   host asks for the list out loud, and it is asked once: langRefresh answers false while
   one is already in flight and the list is kept afterwards.

   ASKED AFTER THE PRESS HAS BEEN HANDLED, never during it. Two listeners sit on this
   header and the one that actually opens the card is added LATER than this one, because
   wireCollapsible("upkeepHead", ...) runs much further down this file. Listeners fire in
   the order they were added, so reading the class here inside the dispatch reads the
   state the card is LEAVING rather than the one it is arriving at, and that had the whole
   thing backwards: expanding the card asked for nothing, and collapsing it reached the
   release page for a menu the host had just put away. A task queued here runs once the
   dispatch is finished, whatever order the two listeners were added in.

   Both roads, because the header is operable from the keyboard and that road never
   produces a click: wireCollapsible's own keydown calls the toggle directly. */
const langUpkeepAsk=()=>setTimeout(()=>{
  if($("#upkeepCard")?.classList.contains("open")) langRefresh();
},0);
$("#upkeepHead")?.addEventListener("click",langUpkeepAsk);
$("#upkeepHead")?.addEventListener("keydown",e=>{
  if(e.key==="Enter"||e.key===" "||e.key==="Spacebar") langUpkeepAsk();
});
$("#selPlayerMsgLang")?.addEventListener("change",()=>{
  const sel=$("#selPlayerMsgLang");
  const asked=String(sel.value||"same");
  LANG.playerMessages=asked;
  if(Native.available) rpc("userprefs.save",{prefs:{PlayerMessageLanguage:asked}});
});

/* Every report the host pushes while a pack comes down. The bar is painted in place; the
   ending is the RPC's answer, not an event, so nothing here toasts. */
Native.on("lang.downloadProgress",d=>{
  if(!d||!d.code) return;
  if(LANG.busyCode&&d.code!==LANG.busyCode) return;
  LANG.prog=d;
  langPaintProgress();
});
/* Another window switched language. Every window on this install follows, because the
   preference is one document and a second window left in the old language would be
   showing something that is no longer true. */
Native.on("lang.changed",d=>{
  if(!d||!d.code) return;
  /* This window's own ask, coming back as an event. Dropped, and the claim with it, so
     the NEXT event naming the same language really is another window's and is followed.
     The reply to lang.set is what switches this window, and it is on its way. */
  if(LANG.mine&&d.code===LANG.mine){LANG.mine=null;return;}
  if(d.code===langCurrent()) return;
  if(!Native.available) return;
  Native.call("lang.status",{}).then(st=>{
    if(!st) return;
    LANG.status=st;
    return switchLanguage({code:st.current,stringsUrl:st.stringsUrl,
      fonts:st.fonts,missingKeys:st.missingKeys});
  }).catch(e=>console.warn("[lang] a switch in another window could not be followed",e));
});

/* ---------- EMPTY STATES ----------
   One shape for every "there is nothing here yet": a mark, a sentence-case title of
   at most six words, one sentence saying why it is empty, and at most one button
   that does the thing which would fill it. Nothing here ever says just "-".
   The mark is the one piece of ornament an empty panel is allowed (see the card
   header note in app.css).

   title, reason and label arrive already worded: every caller asks the catalog for
   them by id before calling in. This used to word them itself, through the terminology
   swap, which meant the table of empty states was a table of English the completeness
   gate could not see and a translator could not reach. Passing the wording in keeps
   the one composed reason honest too: the mod search counts the mods it is hiding, and
   that count is a named slot in one catalog entry rather than three pieces glued
   together here. */
function emptyState(o){
  o=o||{};
  const a=o.action;
  return `<div class="empty-state${o.compact?" es-compact":""}">`+
    `<div class="es-mark" aria-hidden="true">${o.mark||"ᛜ"}</div>`+
    `<div class="es-title">${esc(o.title||T("common.empty.title"))}</div>`+
    (o.reason?`<div class="es-reason">${esc(o.reason)}</div>`:"")+
    (a?`<button class="btn btn-ghost btn-sm" data-es-action="${esc(a.name)}">${esc(a.label)}</button>`:"")+
    `</div>`;
}
/* Named actions, so an empty state can offer the same command the toolbar offers
   without every caller re-wiring a click handler. */
const ES_ACTIONS={};
/* Attaches the buttons inside a container that was just filled with emptyState()
   HTML. stopPropagation matters: several of these sit inside cards that are
   themselves clickable (the saves card opens its drill-down). */
function esWire(root){
  if(!root) return;
  root.querySelectorAll("[data-es-action]").forEach(b=>{
    if(b._esWired) return;
    b._esWired=true;
    b.addEventListener("click",e=>{
      e.stopPropagation();
      const fn=ES_ACTIONS[b.dataset.esAction];
      if(fn) fn();
    });
  });
}

/* ---------- SCROLL CUES ----------
   Every scrollable pane says so out loud. .can-scroll-down / .can-scroll-up ride
   the pane itself; the halls also mirror theirs onto .pages, which paints the
   fade, so a hall's own animation is never re-rasterised behind a mask. Cheap:
   one passive scroll listener plus a shared ResizeObserver per pane, no polling. */
const SCROLLCUE_SEL=".page,.atlas-side,.atlas-list,.srvstrip,.b-net,.modal .mbody,.modal .mono-list,.modal .clist,.modal .pick-list,.modal .wiz-found";
const _cueSeen=new WeakSet();
function updateScrollCue(el){
  if(!el||!el.isConnected||!el.clientHeight) return;   // a hidden pane never speaks for its host
  const max=el.scrollHeight-el.clientHeight;
  const on=max>4;
  const down=on&&el.scrollTop<max-2, up=on&&el.scrollTop>2;
  el.classList.toggle("can-scroll-down",down);
  el.classList.toggle("can-scroll-up",up);
  if(el.dataset.cueHost==="parent"&&el.parentElement){
    el.parentElement.classList.toggle("cue-down",down);
    el.parentElement.classList.toggle("cue-up",up);
  }
}
function _cueObserver(){
  if(typeof ResizeObserver==="undefined") return null;
  return new ResizeObserver(es=>{
    const seen=new Set();
    es.forEach(e=>{
      const pane=e.target.classList&&e.target.classList.contains("scrollcue")
        ?e.target:(e.target.closest?e.target.closest(".scrollcue"):null);
      if(pane&&!seen.has(pane)){seen.add(pane);updateScrollCue(pane);}
    });
  });
}
/* Two observers, not one. The persistent panes (.page, the atlas, the chip strip) are
   reused for the life of the window, so their observer is never torn down. Everything
   inside a modal is thrown away and rebuilt on the next open, and a ResizeObserver holds
   its targets alive, so modal panes get their own observer that modalClose disconnects.
   One shared observer leaked every discarded modal subtree for the whole session. */
const _cueRO=_cueObserver();
const _cueModalRO=_cueObserver();
function cueObserverFor(el){
  return (el.closest&&el.closest(".modal-bg"))?_cueModalRO:_cueRO;
}
/* Drop every modal-scoped target the observer is still holding. Called before the modal
   markup is replaced or wiped, while those nodes are still reachable. */
function releaseModalScrollCues(){
  if(_cueModalRO) _cueModalRO.disconnect();
}
function watchScrollCue(el){
  if(!el) return;
  if(_cueSeen.has(el)){updateScrollCue(el);return;}
  _cueSeen.add(el);
  el.classList.add("scrollcue");
  if(el.classList.contains("page")) el.dataset.cueHost="parent";
  else el.classList.add("scrollfade");
  el.addEventListener("scroll",()=>updateScrollCue(el),{passive:true});
  const ro=cueObserverFor(el);
  if(ro){ro.observe(el);[...el.children].forEach(c=>ro.observe(c));}
  updateScrollCue(el);
}
function rescanScrollCues(){$$(".scrollcue").forEach(updateScrollCue);}
function refreshScrollCues(){
  $$(SCROLLCUE_SEL).forEach(watchScrollCue);
  /* A pane measured the instant it is shown is still mid-layout: fonts have not
     swapped and the mock/native drivers have not filled it. Re-read once the
     frame has settled so the cue is right at load, not only after a scroll. */
  requestAnimationFrame(rescanScrollCues);
}
window.addEventListener("load",()=>{refreshScrollCues();setTimeout(rescanScrollCues,400);});
if(document.fonts&&document.fonts.ready) document.fonts.ready.then(rescanScrollCues).catch(()=>{});

/* ---------- WINDOW RESIZE ----------
   One listener for the whole app, coalesced onto a single animation frame. A live
   window drag fires resize dozens of times a second, and each of the five things
   below reads layout, so five raw listeners meant five forced reflow passes per
   event. Now every measurement happens once per painted frame, in a fixed order:
   the rail first (it decides how tall the chip strip may be), then the panes that
   measure inside it. The flame is left to its own ResizeObserver on the canvas,
   which already fires for a window resize, and the roster's column note is only
   worth computing while the roster is the hall on screen. */
let _resizeRaf=0;
function onWindowResize(){
  if(_resizeRaf) return;
  _resizeRaf=requestAnimationFrame(()=>{
    _resizeRaf=0;
    try{fitServerStrip();}catch(_){}
    try{hlogFit();}catch(_){}
    try{rescanScrollCues();}catch(_){}
    try{updateToastLift();}catch(_){}
    /* The canvas observer watches the CSS box, and moving the window to a display with a
       different scale does not change that box, only devicePixelRatio. Without this the
       flame keeps the old backing store and draws blurred. It is idempotent: it returns
       without touching anything unless the backing size really differs. */
    try{flameResize();}catch(_){}
    if(currentPage==="vikings"){try{renderVikCols();}catch(_){}}
    /* A narrower pane wraps the config text differently, so a painted find mark would be
       left over words it no longer belongs to. It is painted again where they went, and
       the pane is not scrolled: the host is resizing a window, not asking to jump. */
    if(currentPage==="runes"){try{cfgFindPaint(false);}catch(_){}}
  });
}
window.addEventListener("resize",onWindowResize);

/* ---------- TOAST LIFT ----------
   Toasts live at the bottom right of the content pane, clear of the status bar. Two
   things are allowed to push them further up: the Saga hall's console input row, so
   they never cover what you are typing, and an open modal's button row, so a toast
   fired mid-dialog never lands on the buttons that dialog is waiting on. */
const TOAST_BASE=42;    // matches the base offset in the .toasts rule in app.css
function updateToastLift(){
  let lift=0;
  if(currentPage==="saga"){
    const row=$("#page-saga .termin");
    lift=(row&&row.offsetHeight?row.offsetHeight:36)+18;
  }
  const btns=document.querySelector(".modal-bg.open .modal .mbtns");
  const app=$("#app");
  if(btns&&app){
    const r=btns.getBoundingClientRect(), a=app.getBoundingClientRect();
    if(r.height>0) lift=Math.max(lift,Math.round(a.bottom-r.top)+12-TOAST_BASE);
    /* A dialog tall enough to reach the top of the window would otherwise lift the
       stack clean off the screen. Keep it inside the page area and let it overlap the
       dialog's upper half instead of vanishing. */
    lift=Math.min(lift,Math.max(0,Math.round(a.height)-TOAST_BASE-160));
  }
  document.documentElement.style.setProperty("--toast-lift",Math.max(0,lift)+"px");
}
/* goPage still calls this by name; currentPage is already the new hall by then. */
function setToastLift(){updateToastLift();}

/* ---------- HEARTH: RECENT LOG CARD ----------
   Mirrors the tail of the Saga chronicle (logLine feeds it). It keeps 30 lines
   and shows as many as the card is tall enough for (8 on a short window, up to
   all 30 on a maximised one), so the card fills its space instead of sitting
   half empty. One rAF-coalesced paint per burst, so a flood of server lines
   costs the terminal nothing extra. Log text is raw data, never plainified. */
/* HLOG_ROW mirrors the fixed line-height on .b-log .hlog, so the row count is
   exact arithmetic rather than an estimate - the card renders only lines that
   fit and never hides any behind its own edge. */
const HLOG_BUF=30, HLOG_MIN=4, HLOG_ROW=16;
let HLOG_MAX=HLOG_MIN;
const hlogBuf=[];
let hlogDirty=false, hlogRaf=0;
function hlogPush(kind,text,time){
  hlogBuf.push({kind,text,time});
  if(hlogBuf.length>HLOG_BUF) hlogBuf.shift();
  if(currentPage!=="hearth"){hlogDirty=true;return;}
  if(hlogRaf) return;
  hlogRaf=requestAnimationFrame(()=>{hlogRaf=0;renderHearthLog();});
}
function renderHearthLog(){
  const el=$("#hearthLog"); if(!el) return;
  hlogDirty=false;
  const rows=hlogBuf.slice(-HLOG_MAX);
  el.innerHTML=rows.length
    ?rows.map(l=>`<div class="ln ${l.kind}" title="${esc(l.time+"  "+l.text)}"><span class="t">${esc(l.time)}</span>  <span class="${l.kind}">${esc(l.text)}</span></div>`).join("")
    :emptyState({compact:true,mark:"ᛋ",title:T("hearth.saga.empty.title"),
        reason:T("hearth.saga.empty.reason")});
  esWire(el);
}
/* how many whole lines the card can actually show right now */
function hlogFit(){
  const el=$("#hearthLog"); if(!el||!el.clientHeight) return;
  const cs=getComputedStyle(el);
  const inner=el.clientHeight-(parseFloat(cs.paddingTop)||0)-(parseFloat(cs.paddingBottom)||0);
  if(inner<=0) return;
  const n=Math.max(HLOG_MIN,Math.min(HLOG_BUF,Math.floor(inner/HLOG_ROW)));
  if(n===HLOG_MAX) return;
  HLOG_MAX=n;
  renderHearthLog();
}
renderHearthLog();
if(typeof ResizeObserver!=="undefined"){
  const hlogRO=new ResizeObserver(()=>hlogFit());
  if($("#hearthLog")) hlogRO.observe($("#hearthLog"));
}
/* window resize is handled by the shared dispatcher above */

/* ---------- COLLAPSIBLE HEADERS ----------
   One wiring for every foldable header: click or Enter/Space toggles it, the
   chevron rotates open, and while it is closed the header says how many
   settings are folded away. The toggle itself is unchanged. */
const COLLAPSIBLES=[];
function syncCollapsible(rec){
  const open=rec.target.classList.contains("open");
  rec.head.setAttribute("aria-expanded",open?"true":"false");
  /* One folded setting used to read "1 settings": the number was glued to the word
     before the lookup ever saw it, so no catalog could tell the two forms apart. */
  rec.hint.textContent=open?"":T("common.collapsible.folded",{count:rec.count});
  /* An open Upkeep card is taller than a fixed bento row, so the last row is
     told to take its content height while the card is unfolded. */
  if(rec.target.id==="upkeepCard"){
    const bento=document.querySelector(".bento");
    if(bento) bento.classList.toggle("upkeep-open",open);
  }
}
function syncCollapsibleTerms(){
  /* Spelled as a loop with a call of its own rather than forEach(syncCollapsible), because
     the first-frame gate reads statement calls: a header wired before the catalog landed has
     to be visibly one of the things the boot repaint runs again. */
  for(const rec of COLLAPSIBLES){
    syncCollapsible(rec);
  }
}
function wireCollapsible(headId,body,targetEl){
  const head=document.getElementById(headId); if(!head) return;
  const target=targetEl||head;
  const count=body?(body.querySelectorAll(".field").length||body.querySelectorAll(".togglerow").length):0;
  const hint=document.createElement("span"); hint.className="fs-hint";
  const chev=document.createElement("span"); chev.className="fs-chev"; chev.textContent="▸";
  head.insertBefore(hint,head.querySelector("[data-fs-anchor]"));
  head.appendChild(chev);
  const rec={head,target,hint,count};
  COLLAPSIBLES.push(rec);
  const toggle=()=>{
    target.classList.toggle("open");
    syncCollapsible(rec);
    rescanScrollCues();
    /* rows revealed below the fold are no use to anyone: bring them up, but
       only as far as it takes (block:"nearest" leaves a fitting card alone) */
    if(target.classList.contains("open")) requestAnimationFrame(()=>{
      try{target.scrollIntoView({block:"nearest"});}catch(_){}
      rescanScrollCues();
    });
  };
  head.addEventListener("click",toggle);
  head.addEventListener("keydown",e=>{
    if(e.key==="Enter"||e.key===" "||e.key==="Spacebar"){e.preventDefault();toggle();}
  });
  syncCollapsible(rec);
}

/* ---------- NAV ---------- */
let currentPage="hearth";
/* Halls laid out as a flex column so a child can be handed the pane's real
   leftover height: the Atlas map, and the Hearth bento. */
const FLEX_PAGES={"page-atlas":1,"page-hearth":1};
function goPage(name){
  $$(".navitem").forEach(n=>n.classList.toggle("active",n.dataset.page===name));
  $$(".page").forEach(p=>{
    const on=p.id==="page-"+name;
    p.classList.toggle("active",on);
    p.style.display=on?(FLEX_PAGES[p.id]?"flex":"block"):"none";
  });
  currentPage=name;
  setToastLift();
  if(name==="vikings"){try{renderVikCols();}catch(_){}}
  if(name==="hearth"){if(hlogDirty)renderHearthLog();hlogFit();flameResize();}
  flameTick();   // the fire only burns while the Dashboard is on screen
  refreshScrollCues();
  try{renderEditBar();}catch(_){}
  if(name==="atlas"){try{atlasEnter();}catch(_){}} // also drives the mock preview
  if(name==="skald"){try{skaldRefresh();}catch(_){}} // mock in preview, journal in-app
  if(Native.available){
    if(name==="mods"&&!S.modsScanned&&!S.modsScanning) scanMods();
    if(name==="vikings"){refreshPlayers();refreshJournal(true);}
    if(name==="runes") refreshCfgList(false); // re-list scrolls on every visit (keeps a dirty editor untouched)
    if(name==="herald") refreshHerald();      // re-read prefs so the hall always shows the saved truth
  }
}
$$(".navitem").forEach(n=>n.addEventListener("click",()=>goPage(n.dataset.page)));
$$("[data-goto]").forEach(c=>c.addEventListener("click",()=>goPage(c.dataset.goto)));
goPage("hearth");

/* ---------- MULTI-SERVER CHIP STRIP ----------
   One chip per server profile (saved or live). The active chip is the profile
   every hall renders; other servers keep burning in the background. */
async function refreshServers(){
  if(!Native.available) return;
  const r=await Native.call("servers.list",{}).catch(()=>null);
  if(Array.isArray(r)){S.servers=r;renderServerChips();}
}
function renderServerChips(){
  const el=$("#srvStrip"); if(!el) return;
  const all=S.servers||[];
  const list=all.filter(s=>!s.archived);       // strip shows only live realms
  const archived=all.filter(s=>s.archived);
  let html=list.map(s=>{
    const cls="srvchip"+(s.active?" active":"")+(s.running?" running":"");
    const initial=(s.name||"?").trim().charAt(0).toUpperCase();
    const badge=s.playersOnline>0?`<span class="srvbadge">${s.playersOnline}</span>`:"";
    /* The running chip says what it is doing as well as who it is, so the two
       shapes are two entries rather than one with a gap in the middle. */
    const tip=s.running
      ?T("side.strip.chip.title.running",{name:s.name,status:s.status})
      :T("side.strip.chip.title",{name:s.name});
    return `<div class="${cls}" data-srv="${esc(s.name)}" title="${esc(tip)}"><span class="srvdot${s.running?" on":""}"></span><span class="srvinit">${esc(initial)}</span>${badge}</div>`;
  }).join("");
  html+=`<div class="srvchip add" id="srvAdd" title="${esc(T("side.strip.add.title"))}"><span class="srvinit">+</span></div>`;
  const archBadge=archived.length?`<span class="srvbadge">${archived.length}</span>`:"";
  const restoreTip=archived.length
    ?T("side.strip.restore.title.archived",{count:archived.length})
    :T("side.strip.restore.title");
  html+=`<div class="srvchip arch" id="srvRestore" title="${restoreTip}"><span class="srvinit">↺</span>${archBadge}</div>`;
  el.innerHTML=html;
  const sep=$("#srvSep"); if(sep) sep.style.display=list.length?"":"none";
  fitServerStrip();
  requestAnimationFrame(fitServerStrip);   // re-measure once the new chips have laid out
}
/* The strip is never allowed to cut a chip in half: its height is always a whole
   number of chip pitches (40px chip + 7px gap), so any cut lands on a gap. It
   takes whatever the rail can spare once the nav items have had their say, and
   the "+N" cue below it names the chips that are still out of view. */
const CHIP_H=40, CHIP_GAP=7, CHIP_PITCH=CHIP_H+CHIP_GAP, CHIP_MAX=8, CHIP_CUE_H=12;
function fitServerStrip(){
  const strip=$("#srvStrip"); if(!strip) return;
  const side=strip.closest(".side"); if(!side) return;
  const more=$("#srvMore");
  const chips=strip.querySelectorAll(".srvchip").length;
  if(!chips){strip.style.maxHeight="0px";if(more)more.style.display="none";return;}
  /* Measure the rail with every child frozen at its own size and the strip
     deliberately OVERFLOWING it. Collapsing the strip instead hands its height
     to whatever can absorb free space: the footer's margin-top:auto resolves to
     exactly that leftover, and getComputedStyle hands back the used value, so
     the rail measured as full and the strip was left a single chip. With no
     free space there is nothing to absorb and every margin reads as declared. */
  const kids=[...side.children];
  const wasFlex=kids.map(k=>k.style.flex);
  const wasMax=strip.style.maxHeight, wasMin=strip.style.minHeight;
  kids.forEach(k=>{k.style.flex="none";});
  strip.style.maxHeight="none"; strip.style.minHeight="4000px";
  const cs=getComputedStyle(side);
  const gap=parseFloat(cs.rowGap)||0;
  let used=(parseFloat(cs.paddingTop)||0)+(parseFloat(cs.paddingBottom)||0);
  let shown=1;                                     // the cue always keeps its seat
  kids.forEach(k=>{
    if(k===more) return;                           // budgeted below, visible or not
    const ks=getComputedStyle(k);
    if(ks.display==="none") return;
    shown++;
    if(k===strip) return;
    used+=k.getBoundingClientRect().height+(parseFloat(ks.marginTop)||0)+(parseFloat(ks.marginBottom)||0);
  });
  used+=CHIP_CUE_H+gap*Math.max(0,shown-1);
  const avail=side.clientHeight;
  strip.style.maxHeight=wasMax;                    // put the rail back before anything paints
  strip.style.minHeight=wasMin;
  kids.forEach((k,i)=>{k.style.flex=wasFlex[i];});
  let k=Math.floor((avail-used+CHIP_GAP)/CHIP_PITCH);
  k=Math.max(1,Math.min(chips,CHIP_MAX,k));
  strip.style.maxHeight=(k*CHIP_PITCH-CHIP_GAP)+"px";
  const hidden=chips-k;
  if(more){
    more.style.display=hidden>0?"":"none";
    more.textContent=hidden>0?"▾ +"+hidden:"▾";
    more.title=hidden>0
      ?T("side.strip.more.title.hidden",{count:hidden})
      :T("side.strip.more.title");
  }
  updateScrollCue(strip);
}
/* window resize is handled by the shared dispatcher, so the rail is measured once a frame */
/* web fonts change the footer's height, which changes what the strip can have */
if(document.fonts&&document.fonts.ready) document.fonts.ready.then(fitServerStrip).catch(()=>{});
/* Right-click a chip → per-realm actions. */
function serverChipMenu(name,x,y){
  const s=(S.servers||[]).find(v=>String(v.name)===String(name)); if(!s) return;
  const stopFirst=T("realm.menu.stop_first.tip");
  const items=[
    {r:"ᛒ",label:T("realm.menu.switch"),fn:()=>switchServer(name),disabled:s.active,tip:T("realm.menu.switch.tip")},
    {r:"ᚱ",label:T("realm.menu.rename"),fn:()=>renameServer(name),disabled:s.running,tip:stopFirst},
    {r:"ᛞ",label:T("realm.menu.duplicate"),fn:()=>duplicateServer(name)},
    "hr",
    {r:"⌂",label:T("realm.menu.archive"),fn:()=>archiveServer(name),disabled:s.running,tip:stopFirst},
    {r:"ᛟ",label:T("realm.menu.delete"),fn:()=>deleteServerFlow(name),danger:true,disabled:s.running,tip:stopFirst},
  ];
  ctxOpen(x,y,name,items,()=>serverChipMenu(name,x,y));
}
async function renameServer(name){
  promptModal(()=>T("realm.rename.title"),name,async v=>{
    v=(v||"").trim(); if(!v||v===name) return;
    const r=await rpc("profiles.rename",{name,newName:v});
    if(r===FAIL) return;
    if(S.profileName===name){S.profileName=v;if(S.prefs)S.prefs.ProfileName=v;}
    await refreshServers(); toast("ᚱ "+T("realm.renamed.toast",{name:v}));
  });
}
async function duplicateServer(name){
  // Seed a new realm from this one: switch to it so the wizard clones its mod set.
  if(!(S.servers||[]).find(v=>String(v.name)===String(name)&&v.active)) await switchServer(name);
  addServerProfile();
}
async function archiveServer(name){
  const r=await rpc("profiles.archive",{name});
  if(r===FAIL) return;
  if(S.profileName===name) S.profileName=null;
  await refreshServers(); toast("⌂ "+T("realm.archive.done.toast"));
}
/* Delete flow: default removes settings only; a danger toggle also reclaims the
   isolated install + this realm's worlds/backups (with an exact file count/size). */
async function deleteServerFlow(name){
  const info=await rpc("profiles.deleteInfo",{name});
  if(info===FAIL||!info){toast(T("realm.delete.unreadable.toast"));return;}
  if(info.running){toast(T("realm.delete.running.toast"));return;}
  const mb=(info.sizeBytes||0)/1048576;
  const sizeStr=mb>=1024?(mb/1024).toFixed(1)+" GB":Math.max(0,mb).toFixed(mb<10?1:0)+" MB";
  const hasFiles=(info.hasIsolatedInstall||info.saveFolder)&&info.fileCount>0;
  const dangerLine=hasFiles
    ?`<label class="togglerow" style="cursor:pointer"><span class="tl" style="color:var(--danger,#E8560F)">${esc(T("realm.delete.files.label"))}<br><span style="font-size:9.5px;opacity:.7">${esc(T("realm.delete.files.note",{count:info.fileCount,size:sizeStr}))}</span></span><div class="toggle" id="delFiles"></div></label>`
    :`<div class="fieldnote">${esc(T("realm.delete.shared.note"))}</div>`;
  const m=modalOpen(
    `<div class="mtitle">${esc(T("realm.delete.title"))} · ${esc(name)}</div>`+
    `<div class="mbody"><p>${esc(T("realm.delete.body"))}</p>`+
      dangerLine+
      `<div class="mbody-note" id="delStatus"></div></div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="delCancel">${esc(T("common.button.cancel"))}</button>`+
      `<button class="btn btn-ember btn-sm" id="delOk">${esc(T("common.button.delete"))}</button></div>`,()=>deleteServerFlow(name));
  const delFiles=m.querySelector("#delFiles");
  if(delFiles) delFiles.addEventListener("click",()=>delFiles.classList.toggle("on"));
  m.querySelector("#delCancel").addEventListener("click",modalClose);
  m.querySelector("#delOk").addEventListener("click",async()=>{
    const df=delFiles?delFiles.classList.contains("on"):false;
    const ok=m.querySelector("#delOk"); ok.disabled=true; ok.textContent=T("realm.delete.working");
    const r=await rpc("profiles.delete",{name,deleteFiles:df});
    if(r===FAIL){ok.disabled=false;ok.textContent=T("common.button.delete");const st=m.querySelector("#delStatus");if(st)st.textContent=T("realm.delete.failed");return;}
    modalClose();
    if(S.profileName===name) S.profileName=null;
    await refreshServers();
    // If we deleted the active realm, switch the UI to whatever remains.
    const next=(S.servers||[]).find(s=>!s.archived);
    if(next) await switchServer(next.name);
    toast("ᛟ "+T("realm.delete.done.toast"));
  });
}
/* Restore surface: (1) archived realms, unarchive or delete for good; (2) past worlds
   still on disk that no realm owns, adopt each back as its own isolated server. Pull-based:
   orphan worlds are only queried here, never on startup, so many old worlds never nag. */
async function restoreModal(){
  const archived=(S.servers||[]).filter(s=>s.archived);
  const orphans=await rpc("worlds.listOrphans",{});
  const orphanList=Array.isArray(orphans)?orphans:[];
  if(!archived.length&&!orphanList.length){toast(T("realm.restore.none.toast"));return;}

  const archRows=archived.map(s=>
    `<div class="arow" style="display:flex;align-items:center;justify-content:space-between;gap:10px;padding:7px 0;border-bottom:1px solid var(--line,#2A2E34)">`+
    `<span class="cilbl">${esc(s.name)}</span>`+
    `<span style="display:flex;gap:6px"><button class="btn btn-ghost btn-sm arst" data-name="${esc(s.name)}">${esc(T("common.button.restore"))}</button>`+
    `<button class="btn btn-ghost btn-sm ardel" data-name="${esc(s.name)}" style="color:var(--danger,#E8560F)">${esc(T("common.button.delete"))}</button></span></div>`).join("");

  const orphanRows=orphanList.map((o,i)=>{
    const mb=(o.sizeBytes||0)/1048576;
    const sizeStr=mb>=1024?(mb/1024).toFixed(1)+" GB":Math.max(0,mb).toFixed(mb<10?1:0)+" MB";
    let when="";
    try{const d=new Date(o.modifiedUtc);if(!isNaN(d))when=d.toLocaleDateString(LOC());}catch{}
    const older=o.olderCount>0?" · "+T("realm.restore.orphans.older",{count:o.olderCount}):"";
    return `<div class="arow" style="display:flex;align-items:center;justify-content:space-between;gap:10px;padding:7px 0;border-bottom:1px solid var(--line,#2A2E34)">`+
      `<span class="cilbl">${esc(o.world)}<br><span style="font-size:9.5px;opacity:.6">${esc(sizeStr)}${when?" · "+esc(when):""}${esc(older)}</span></span>`+
      `<button class="btn btn-ghost btn-sm oadopt" data-i="${i}">${esc(T("common.button.bring_back"))}</button></div>`;
  }).join("");

  const archSection=archived.length
    ?`<div class="mtitle" style="font-size:12px;opacity:.85">${esc(T("realm.restore.archived.head"))}</div><div class="mbody">${archRows}</div>`:"";
  const orphanSection=orphanList.length
    ?`<div class="mtitle" style="font-size:12px;opacity:.85${archived.length?";margin-top:10px":""}">${esc(T("realm.restore.orphans.head"))}</div>`+
     `<div class="fieldnote" style="margin:2px 0 4px">${esc(T("realm.restore.orphans.note"))}</div>`+
     `<div class="mbody">${orphanRows}</div>`:"";

  const m=modalOpen(
    `<div class="mtitle">${esc(T("realm.restore.title"))}</div>`+
    archSection+orphanSection+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="arClose">${esc(T("common.button.close"))}</button></div>`,restoreModal);
  m.querySelector("#arClose").addEventListener("click",modalClose);
  m.querySelectorAll(".arst").forEach(b=>b.addEventListener("click",async()=>{
    const r=await rpc("profiles.unarchive",{name:b.dataset.name});
    if(r===FAIL) return;
    modalClose(); await refreshServers(); toast("⌂ "+T("realm.unarchived.toast",{name:b.dataset.name}));
  }));
  m.querySelectorAll(".ardel").forEach(b=>b.addEventListener("click",async()=>{
    modalClose(); deleteServerFlow(b.dataset.name);
  }));
  m.querySelectorAll(".oadopt").forEach(b=>b.addEventListener("click",()=>{
    const o=orphanList[+b.dataset.i]; if(!o) return;
    modalClose(); adoptWorldFlow(o);
  }));
}
/* Adopt an orphan world as its own isolated server (own save + install; mods optionally
   seeded from the active realm, else vanilla, kept distinct to avoid cross-contamination). */
async function adoptWorldFlow(o){
  const taken=new Set((S.servers||[]).map(s=>String(s.name).toLowerCase()));
  const m=modalOpen(
    `<div class="mtitle">${esc(T("realm.adopt.title"))} · ${esc(o.world)}</div>`+
    `<div class="mbody">`+
      `<div class="field"><label>${esc(T("realm.adopt.name.label"))}</label>`+
        `<input type="text" id="awName" value="${esc(o.world)}" spellcheck="false" autocomplete="off"></div>`+
      `<label class="togglerow" style="cursor:pointer"><span class="tl">${esc(T("realm.adopt.mods.label"))}`+
        `<br><span style="font-size:9.5px;opacity:.7">${esc(T("realm.adopt.mods.note"))}</span></span>`+
        `<div class="toggle on" id="awSeed"></div></label>`+
      `<div class="fieldnote" id="awStatus">${esc(T("realm.adopt.status"))}</div>`+
    `</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="awCancel">${esc(T("common.button.cancel"))}</button>`+
      `<button class="btn btn-ember btn-sm" id="awOk">${esc(T("common.button.bring_back"))}</button></div>`,()=>adoptWorldFlow(o));
  const seed=m.querySelector("#awSeed");
  seed.addEventListener("click",()=>seed.classList.toggle("on"));
  m.querySelector("#awCancel").addEventListener("click",modalClose);
  m.querySelector("#awOk").addEventListener("click",async()=>{
    const name=(m.querySelector("#awName").value||"").trim();
    const st=m.querySelector("#awStatus");
    if(!name){st.textContent=T("realm.adopt.name.required");return;}
    if(taken.has(name.toLowerCase())){st.textContent=T("realm.adopt.name.taken");return;}
    const ok=m.querySelector("#awOk"); ok.disabled=true; ok.textContent=T("realm.adopt.working");
    const r=await rpc("servers.adoptWorld",{world:o.world,folder:o.folder,sub:o.sub,name,seedMods:seed.classList.contains("on")});
    if(r===FAIL){ok.disabled=false;ok.textContent=T("common.button.bring_back");st.textContent=T("realm.adopt.failed");return;}
    modalClose();
    await refreshServers();
    if(r&&r.ProfileName){await switchServer(r.ProfileName);goPage("world");}
    toast("↺ "+T("realm.adopted.toast",{name}));
  });
}
async function switchServer(name){
  if(!name||name===S.profileName) return;
  const prefs=await rpc("profiles.get",{name});
  if(prefs===FAIL||!prefs) return;
  S.prefs=prefs; S.profileName=prefs.ProfileName; S.saveInterval=prefs.SaveInterval??600;
  S.players=[]; S.invite=null; S.net={conns:null,zdos:null,sent:null,recv:null,at:null,hist:[]};
  S.saveDur=[]; S.lastSaveAt=null; S.saveSec=null; S.upSince=null;
  S.mods=null; S.modsScanned=false; S.lastScan=null; S.modSort={col:null,dir:0};
  S.modIndexAt=null; S.modIndexSource=null;
  /* a search was typed about the previous realm's mods and scrolls, so it goes with them */
  S.modFilter=""; if($("#modSearch")) $("#modSearch").value="";
  S.runeFilter=""; if($("#runeSearch")) $("#runeSearch").value="";
  cfgFindReset();
  /* conditions belong to the realm that raised them */
  ["saveFailed","backupFailed","crashRelaunch","modUpdates","serverUpdate","restartPending"]
    .forEach(clearCondition);
  /* and so does a dismissal: the settings the host waved away were that realm's */
  RESTART_PENDING_HIDDEN=null;
  /* so does the update answer: install kind, waiting bytes and the reason a realm cannot
     be updated are all about the install the previous realm pointed at. */
  SRV_UPDATE=null; _updHidden=false;
  S.update={installKind:null,updatePending:false,pendingBytes:0,buildId:null,targetBuildId:null,
            canUpdate:false,running:false,reason:""};
  try{renderUpdatePill();}catch(_){}
  S.journal={}; S.vikSort={col:null,dir:0}; S.crashed=false;
  /* the previous realm's write times belong to the previous realm */
  renderSaveAvg();
  try{atlasReset();}catch{}
  const st=await rpc("server.state");
  if(st!==FAIL){S.state=null;applyState(st);}
  refreshUpdateInfo();   // this realm's own install, asked fresh
  renderAllFromPrefs();
  await refreshPlayers();
  renderMods();
  const caps=await rpc("caps.get");
  if(caps!==FAIL&&caps){S.caps=caps;renderCaps();}
  try{renderWorldMods();}catch{}
  try{renderMaxPlayers();}catch{}
  refreshServers();
  logDivider(T("realm.switched.divider",{name:prefs.ProfileName}));
  // replay this server's in-memory session tail so the saga isn't blank after a switch
  const sbuf=await rpc("logs.serverBuffer");
  if(sbuf!==FAIL&&Array.isArray(sbuf)&&sbuf.length) sbuf.slice(-200).forEach(logRaw);
  logLine("info","[BakaLoader] switched to profile '"+name+"'");
  toast("ᛒ "+T("realm.switched.toast",{name:prefs.ProfileName}));
  if(currentPage==="mods") scanMods();
  if(currentPage==="runes") refreshCfgList(true);
  renderSaveBars();
}
/* New-Server wizard: a realm gets its OWN identity (name, world, free ports) and,
   by default, its OWN isolated install (independent BepInEx/plugins) + save folder,
   so a second server is genuinely separate rather than a shadow of the first. The
   heavy lifting (provisioning the isolated install) happens in servers.create. */
async function addServerProfile(){
  // The realm-forge dials for a brand-new world: their own ids so they never collide with the
  // settings-page fMod* dials. Every dial defaults to Normal ("").
  const wsModsHtml=Object.entries(WORLDGEN).map(([key,def])=>
    `<div class="field"><label>${esc(T(def.labelId))}`+
      `<button type="button" class="wginfo" id="wsInfo_${key}" aria-controls="wgTip" `+
        `aria-label="${esc(T(def.ariaId))}">?</button></label>`+
      `<select id="wsMod_${key}">${wgOptions(key,"")}</select>`+
      `<div class="fieldnote wgnote" id="wsNote_${key}"></div></div>`).join("");
  const m=modalOpen(
    `<div class="mtitle">${esc(T("realm.new.title"))}</div>`+
    `<div class="mbody" style="display:flex;flex-direction:column;gap:2px">`+
      `<div class="field"><label>${esc(T("realm.new.name.label"))}</label>`+
        `<input type="text" id="wsName" placeholder="${esc(T("realm.new.name.placeholder"))}" spellcheck="false" autocomplete="off"></div>`+
      `<div class="field"><label>${esc(T("realm.new.world.label"))}</label>`+
        `<input type="text" id="wsWorld" placeholder="${esc(T("realm.new.world.placeholder"))}" spellcheck="false" autocomplete="off">`+
        `<div class="fieldnote" id="wsPorts">${esc(T("realm.new.ports.finding"))}</div></div>`+
      `<div class="field"><label>${esc(T("realm.new.seed.label"))}</label>`+
        `<input type="text" id="wsWorldSeed" placeholder="${esc(T("realm.new.seed.placeholder"))}" spellcheck="false" autocomplete="off">`+
        `<div class="fieldnote">${esc(T("realm.new.seed.note"))}</div></div>`+
      `<div class="fieldnote" style="margin:2px 0 0">${esc(T("realm.new.difficulty.note"))}</div>`+
      wsModsHtml+
      `<div class="fieldnote" style="margin:6px 0 2px">${esc(T("world.wg.own_note"))}</div>`+
      `<div class="togglerow" title="${esc(T("realm.new.iso.title"))}"><span class="tl">${esc(T("realm.new.iso.label"))}</span>`+
        `<div class="toggle on" id="wsIso"></div></div>`+
      `<div class="togglerow" title="${esc(T("realm.new.seedmods.title"))}"><span class="tl">${esc(T("realm.new.seedmods.label"))}</span>`+
        `<div class="toggle on" id="wsSeed"></div></div>`+
      `<div class="togglerow" title="${esc(T("realm.new.savedir.title"))}"><span class="tl">${esc(T("realm.new.savedir.label"))}</span>`+
        `<div class="toggle on" id="wsSaveIso"></div></div>`+
      `<div class="mbody-note" id="wsStatus"></div>`+
    `</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="wsCancel">${esc(T("common.button.cancel"))}</button>`+
      `<button class="btn btn-ember btn-sm" id="wsOk">${esc(T("realm.new.ok"))}</button></div>`,addServerProfile);

  const nameI=m.querySelector("#wsName"), worldI=m.querySelector("#wsWorld");
  const iso=m.querySelector("#wsIso"), seed=m.querySelector("#wsSeed"), saveIso=m.querySelector("#wsSaveIso");
  const portsN=m.querySelector("#wsPorts"), statusN=m.querySelector("#wsStatus");
  const okB=m.querySelector("#wsOk");
  const on=el=>el.classList.contains("on");
  [iso,seed,saveIso].forEach(t=>t.addEventListener("click",()=>{
    t.classList.toggle("on");
    // Seeding mods only makes sense with a separate install: dim it when install is shared.
    if(t===iso){ if(!on(iso)){seed.classList.remove("on");seed.style.opacity=".4";} else {seed.style.opacity="";} }
  }));

  // Every forge dial gets the same live sentence and hover panel as the settings page,
  // painted from the same table, so a realm is founded on the wording it will keep.
  for(const key of Object.keys(WORLDGEN))
    wireWorldDialHelp(key,m.querySelector("#wsMod_"+key),
      m.querySelector("#wsNote_"+key),m.querySelector("#wsInfo_"+key));

  // Read the difficulty dials back into a modifiers map (Normal dropped) at forge time.
  const collectWsMods=()=>{
    const mods={};
    for(const key of Object.keys(WORLDGEN)){const v=m.querySelector("#wsMod_"+key).value;if(v)mods[key]=v;}
    return mods;
  };

  let ports=null;
  if(Native.available){
    rpc("servers.suggestPort",{}).then(r=>{
      if(r&&r!==FAIL){ports=r; portsN.textContent=T("realm.new.ports.claim",{game:r.gamePort,next:r.gamePort+1,rcon:r.rconPort});}
      else portsN.textContent=T("realm.new.ports.auto");
    });
  }else{
    portsN.textContent=T("realm.new.ports.auto");
  }

  m.querySelector("#wsCancel").addEventListener("click",modalClose);
  okB.addEventListener("click",async()=>{
    const name=nameI.value.trim();
    if(!name){nameI.focus();return;}
    if((S.servers||[]).some(s=>String(s.name).toLowerCase()===name.toLowerCase())){
      statusN.textContent=T("realm.new.name.taken"); return;
    }
    /* The world name is held to the same rule the World field and Copy world as
       hold one to. The forge used to send whatever was typed: an unusable name got
       as far as the first start, which then could not build a command line out of
       it. A blank box is still fine, and still means "name it after the realm". */
    const world=worldI.value.trim();
    if(world){
      const worldProblem=worldNameProblem(world,null);
      if(worldProblem){statusN.textContent=worldProblem;worldI.focus();return;}
    }
    const modifiers=collectWsMods();
    if(!Native.available){
      // Mock/preview: no native host to forge against, so add a local chip so the strip
      // stays explorable offline. The chosen difficulty is cosmetic here (nothing persists).
      S.servers=[...(S.servers||[]),{name,status:"Stopped",running:false,playersOnline:0,active:false}];
      renderServerChips();
      modalClose();
      return;
    }
    okB.disabled=true; okB.textContent=T("realm.new.working");
    statusN.textContent=on(iso)?T("realm.new.status.provisioning"):T("realm.new.status.saving");
    const created=await rpc("servers.create",{
      name, world,
      worldSeed:m.querySelector("#wsWorldSeed").value.trim(),
      isolateInstall:on(iso), seedMods:on(iso)&&on(seed), isolateSaveFolder:on(saveIso),
      port:ports?ports.gamePort:null, rconPort:ports?ports.rconPort:null,
      modifiers,
    });
    if(created===FAIL||!created){
      okB.disabled=false; okB.textContent=T("realm.new.ok");
      statusN.textContent=T("realm.new.failed");
      return;
    }
    modalClose();
    await switchServer(created.ProfileName);
    goPage("world"); // land on World so ports/paths are visible for the new realm
  });
  setTimeout(()=>nameI.focus(),30);
}
$("#srvStrip").addEventListener("click",e=>{
  if(e.target.closest("#srvAdd")){addServerProfile();return;}
  if(e.target.closest("#srvRestore")){restoreModal();return;}
  const chip=e.target.closest(".srvchip[data-srv]");
  if(chip)switchServer(chip.dataset.srv);
});
$("#srvStrip").addEventListener("contextmenu",e=>{
  const chip=e.target.closest(".srvchip[data-srv]");
  if(!chip) return;
  e.preventDefault();
  serverChipMenu(chip.dataset.srv,e.clientX,e.clientY);
});

/* ---------- HEARTH FLAME (canvas particle fire) ----------
   Five states, one canvas, one requestAnimationFrame loop.

     cold        the server is stopped: slow blue-grey glints, no embers
     kindling    starting: spawn rate breathes up and down, sparks in bursts
     burning     running: full spawn on the warm palette
     smoldering  stopping: low spawn, red coals, grey smoke
     crashed     stopped after a crash: ash drifting, one coal pulsing red

   The particle geometry is written in a fixed 260x320 space, and the context is
   transformed to whatever the card gives it multiplied by devicePixelRatio, so the
   fire is the same shape at 100% and at 150% Windows scaling and never renders
   soft. The loop runs only while the Dashboard is the visible hall and the window
   is not hidden; prefers-reduced-motion gets one still frame and no loop. Every
   entry point is a no-op if the canvas is not in the document. */
/* var, not const: goPage() runs once while this file is still being evaluated, and a
   const would still be in its temporal dead zone when it calls flameTick(). */
var FLAME_READY=false;
const FLAME_VW=260, FLAME_VH=320;
const FLAME_PAL={
  burning:{rate:34,lift:[1.1,2.2],life:[38,70],size:[3,7],wobble:.9,
    colors:["#FFF0C8","#FFB35C","#FF7A1A","#C7420C"],glow:"rgba(255,122,26,.35)",smoke:0,ember:1},
  kindling:{rate:14,lift:[.7,1.8],life:[26,52],size:[2,5],wobble:.7,
    colors:["#FFD08A","#FF9A3C","#E8560F"],glow:"rgba(255,122,26,.22)",smoke:.08,ember:1,pulse:true},
  smoldering:{rate:9,lift:[.3,.9],life:[40,90],size:[2,5],wobble:.4,
    colors:["#FF8B2A","#B0380C","#7A2409"],glow:"rgba(217,67,47,.3)",smoke:.5,ember:1},
  cold:{rate:3,lift:[.25,.6],life:[80,140],size:[1.5,3],wobble:.3,
    colors:["#CFE3EE","#8FB4C9","#5B7A8C"],glow:"rgba(91,122,140,.16)",smoke:.15,ember:0},
  crashed:{rate:2,lift:[.2,.5],life:[90,160],size:[2,4],wobble:.5,
    colors:["#5A5A60","#3A3A3E"],glow:"rgba(217,67,47,.18)",smoke:1,ember:0,coal:true},
};
const FLAME={cv:null,ctx:null,state:"cold",ps:[],t:0,raf:0,reduced:false,ro:null};
function flamePal(){return FLAME_PAL[FLAME.state]||FLAME_PAL.burning;}
function flameSpawn(){
  const s=flamePal();
  const x=FLAME_VW/2+(Math.random()-.5)*(s.rate>20?70:44);
  FLAME.ps.push({
    x,y:FLAME_VH-46-Math.random()*8,
    vx:(Math.random()-.5)*s.wobble,
    vy:-(s.lift[0]+Math.random()*(s.lift[1]-s.lift[0]))*2,
    life:0,max:s.life[0]+Math.random()*(s.life[1]-s.life[0]),
    r:s.size[0]+Math.random()*(s.size[1]-s.size[0]),
    c:s.colors[Math.floor(Math.random()*s.colors.length)],
    smoke:Math.random()<s.smoke,
  });
}
/* Match the backing store to the drawn size at this display's pixel ratio, then
   scale the context so every number below is in the fixed 260x320 space. */
function flameResize(){
  if(!FLAME_READY) return;
  const cv=FLAME.cv, ctx=FLAME.ctx;
  if(!cv||!ctx||!cv.isConnected) return;
  const r=cv.getBoundingClientRect();
  if(r.width<1||r.height<1) return;
  const dpr=Math.min(3,Math.max(1,window.devicePixelRatio||1));
  const w=Math.max(1,Math.round(r.width*dpr)), h=Math.max(1,Math.round(r.height*dpr));
  if(cv.width!==w||cv.height!==h){cv.width=w;cv.height=h;}
  ctx.setTransform(cv.width/FLAME_VW,0,0,cv.height/FLAME_VH,0,0);
}
function flameFrame(){
  FLAME.raf=0;
  const ctx=FLAME.ctx, cv=FLAME.cv;
  if(!ctx||!cv||!cv.isConnected) return;
  const s=flamePal();
  FLAME.t++;
  ctx.clearRect(0,0,FLAME_VW,FLAME_VH);
  /* ground glow under the logs */
  const g=ctx.createRadialGradient(FLAME_VW/2,FLAME_VH-40,4,FLAME_VW/2,FLAME_VH-40,110);
  g.addColorStop(0,s.glow); g.addColorStop(1,"rgba(0,0,0,0)");
  ctx.fillStyle=g; ctx.fillRect(0,0,FLAME_VW,FLAME_VH);
  let rate=s.rate;
  if(s.pulse) rate=s.rate*(.4+.6*(.5+.5*Math.sin(FLAME.t/38)));
  for(let i=0;i<rate*.12;i++){ if(Math.random()<rate*.12-i) flameSpawn(); }
  ctx.globalCompositeOperation="lighter";
  for(let i=FLAME.ps.length-1;i>=0;i--){
    const p=FLAME.ps[i];
    p.life++;
    p.x+=p.vx+Math.sin((FLAME.t+p.y)/9)*s.wobble*.35;
    p.y+=p.vy; p.vy*=.995;
    const k=p.life/p.max;
    if(k>=1){FLAME.ps.splice(i,1);continue;}
    const a=(1-k)*(p.smoke?.35:.95);
    const r=p.r*(p.smoke?1+k*2.2:1-k*.55);
    ctx.beginPath(); ctx.arc(p.x,p.y,Math.max(.5,r),0,Math.PI*2);
    ctx.fillStyle=p.smoke?`rgba(120,124,132,${a})`:p.c;
    ctx.globalAlpha=a; ctx.fill();
  }
  ctx.globalAlpha=1; ctx.globalCompositeOperation="source-over";
  if(s.coal){
    const b=.35+.65*(.5+.5*Math.sin(FLAME.t/22));
    ctx.beginPath(); ctx.arc(FLAME_VW/2+18,FLAME_VH-44,5,0,Math.PI*2);
    ctx.fillStyle=`rgba(255,123,107,${b})`;
    ctx.shadowColor="#D9432F"; ctx.shadowBlur=14*b; ctx.fill(); ctx.shadowBlur=0;
  }
  if(s.ember){
    for(let i=0;i<3;i++){
      const b=.4+.6*(.5+.5*Math.sin(FLAME.t/17+i*2));
      ctx.beginPath(); ctx.arc(FLAME_VW/2-24+i*24,FLAME_VH-42+((i%2)*3),3,0,Math.PI*2);
      ctx.fillStyle=`rgba(255,179,92,${b})`; ctx.fill();
    }
  }
  if(flameShouldRun()) FLAME.raf=requestAnimationFrame(flameFrame);
}
/* The fire costs nothing while nobody is looking at it. */
function flameShouldRun(){
  return !!FLAME.ctx && !FLAME.reduced && !document.hidden
    && currentPage==="hearth" && FLAME.cv && FLAME.cv.isConnected;
}
function flameTick(){
  if(!FLAME_READY||!FLAME.ctx) return;
  if(flameShouldRun()){
    if(!FLAME.raf) FLAME.raf=requestAnimationFrame(flameFrame);
  }else if(FLAME.raf){
    cancelAnimationFrame(FLAME.raf); FLAME.raf=0;
  }
}
function flameSetState(state){
  if(!FLAME_READY) return;
  if(!FLAME_PAL[state]) state="cold";
  if(FLAME.state===state) return;
  FLAME.state=state;
  FLAME.ps.length=0;
  for(let i=0;i<40;i++) flameSpawn();
  if(FLAME.reduced) flameStill(); else flameTick();
}
/* prefers-reduced-motion: one settled frame, no loop. */
function flameStill(){
  if(!FLAME.ctx) return;
  flameResize();
  /* run the simulation forward without painting, so the one frame shows a settled
     fire rather than a puff of fresh particles sitting on the logs */
  for(let i=0;i<70;i++){
    FLAME.t++;
    const s=flamePal();
    for(let j=0;j<s.rate*.12;j++) flameSpawn();
    FLAME.ps.forEach(p=>{p.life++;p.y+=p.vy;p.vy*=.995;p.x+=p.vx;});
    FLAME.ps=FLAME.ps.filter(p=>p.life<p.max);
  }
  flameFrame();   // paints once: flameShouldRun() is false under reduced motion
}
function flameInit(){
  const cv=document.getElementById("hearthFlame");
  if(!cv||!cv.getContext) return;             // no canvas, no fire, no error
  let ctx=null;
  try{ ctx=cv.getContext("2d"); }catch(_){ return; }
  if(!ctx) return;
  FLAME.cv=cv; FLAME.ctx=ctx; FLAME_READY=true;
  try{ FLAME.reduced=!!(window.matchMedia&&matchMedia("(prefers-reduced-motion: reduce)").matches); }catch(_){}
  flameResize();
  for(let i=0;i<40;i++) flameSpawn();
  if(typeof ResizeObserver!=="undefined"){
    FLAME.ro=new ResizeObserver(()=>flameResize());
    FLAME.ro.observe(cv);
  }else{
    /* Only where ResizeObserver is missing. With it, the canvas observer above already
       fires for a window resize, and a second path re-measured the same canvas twice. */
    window.addEventListener("resize",flameResize);
  }
  document.addEventListener("visibilitychange",flameTick);
  if(FLAME.reduced) flameStill(); else flameTick();
}
flameInit();

/* ---------- HEARTH: uptime + douse ---------- */
let running=true, upMin=272; // 4h 32m (mock only)
const hCard=$("#hearthCard"), hState=$("#hState"), hPid=$("#hPid"),
      douseBtn=$("#douseBtn"), stokeBtn=$("#stokeBtn"), sailBtn=$("#sailBtn");

/* ---------- PERSISTENT HEADER ----------
   Every hall shows the same four facts and the same single lifecycle button, so the
   answer to "is it up, and can I stop it" is never a page away. Restart is not here
   on purpose: it belongs with the dashboard card and the command palette, where the
   in-game countdown it triggers is explained. */
const AB_PILL={Running:"green",Starting:"amber",Stopping:"amber",Stopped:"blue"};
/* The status the header pill says out loud. The four the app can be in each carry
   their own entry; a status the host invents is shown exactly as it came, which is
   what the pill did before there was a catalog to ask. */
function abStateLabel(status){
  if(status==="Running") return T("hearth.appbar.state.running");
  if(status==="Starting") return T("hearth.appbar.state.starting");
  if(status==="Stopping") return T("hearth.appbar.state.stopping");
  if(status==="Stopped") return T("hearth.appbar.state.stopped");
  return String(status==null?"":status);
}
function abStatus(){
  if(Native.available) return (S.state&&S.state.status)||"Stopped";
  return running?"Running":"Stopped";
}
/* UPTIME-SPAN-BEGIN
   How long the server has been up, as one sentence fragment with the unit letters in it.
   Four call sites built this by hand, each spelling "h " and "m" into the middle of a
   string, which is two English words a translator could not reach: the width pilot
   measured a Russian Hearth state and found it FITTED only because the span inside it
   was still "4h 32m" in Latin letters, and wrote down that the real break returns the
   day somebody translates them. This is that day.
   One formatter, named slots, and the letters live in the catalog. The English is
   byte-identical to what the four sites produced: hours plain, minutes padded to two.
   The padding is a display choice this side owns rather than something a translator has
   to know, so it is done here and the slot arrives already two characters wide.
   @param {number} minutes whole minutes of uptime
   @returns {string} "4h 32m" in English, and whatever the pack says in another language */
function uptimeSpan(minutes){
  const total=Math.max(0,Math.floor(Number(minutes)||0));
  return T("common.uptime.hm",
    {hours:String(Math.floor(total/60)),minutes:String(total%60).padStart(2,"0")});
}
/* UPTIME-SPAN-END */
function abUptime(){
  if(!Native.available) return running?uptimeSpan(upMin):"";
  if(!S.upSince||abStatus()!=="Running") return "";
  return uptimeSpan((Date.now()-S.upSince)/60000);
}
function abOnline(){
  const list=S.players||[];
  if(Native.available) return list.filter(p=>p.status==="Online").length;
  if(!running) return 0;
  return list.length?list.filter(p=>p.status==="Online").length:$$("#homeVik .vdot.on").length;
}
function renderAppBar(){
  const pill=$("#abState"), dot=$("#abDot"); if(!pill||!dot) return;
  const st=abStatus();
  const name=(Native.available?(S.prefs&&S.prefs.Name)||S.profileName:null)
    ||($("#tbSrv")&&$("#tbSrv").textContent)||T("hearth.appbar.name.fallback");
  const nameEl=$("#abName");
  nameEl.textContent=name; nameEl.title=name;

  pill.className="pill "+(AB_PILL[st]||"blue")+" ab-pill";
  /* keep the dot element (and its id) and swap only the label text node beside it */
  let lbl=pill.lastChild;
  if(!lbl||lbl.nodeType!==3){lbl=document.createTextNode("");pill.appendChild(lbl);}
  lbl.nodeValue=abStateLabel(st);

  const up=abUptime();
  const upEl=$("#abUptime");
  upEl.textContent=up?T("hearth.appbar.uptime.value",{span:up}):"";
  upEl.title=up?T("hearth.appbar.uptime.title"):"";

  const online=abOnline();
  const max=Native.available?(S.prefs&&S.prefs.MaxPlayers):10;
  const pEl=$("#abPlayers");
  pEl.textContent=max
    ?T("hearth.appbar.players.value",{online,max})
    :T("hearth.appbar.players.value.nomax",{online});
  pEl.title=T("hearth.appbar.players.title");

  const b=$("#abLifecycle"); if(!b) return;
  const canStop=Native.available?!!(S.state&&S.state.canStop):running;
  const canStart=Native.available?!!(S.state&&S.state.canStart):!running;
  /* Stop stays available while an install is being updated; a start does not. */
  const updBusy=!canStop&&updateBlocksStart();
  b.textContent=canStop?T("hearth.appbar.lifecycle.stop"):T("hearth.appbar.lifecycle.start");
  b.title=updBusy?updBlockMsg()
    :(canStop?T("hearth.appbar.lifecycle.stop.title"):T("hearth.appbar.lifecycle.start.title"));
  b.disabled=updBusy?true:(Native.available?!(canStart||canStop):false);
  b.classList.toggle("btn-cold",canStop);
  b.classList.toggle("btn-ember",!canStop);
}

/* The one guarded start/stop path. The dashboard button and the header button are the
   same action; neither may grow its own copy of the launch guard. */
async function lifecycleToggle(){
  if(Native.available){
    const st=S.state||{};
    if(st.canStop){
      const r=await rpc("server.stop");
      if(r!==FAIL){applyState(r);toast("ᛪ "+T("hearth.stopping.toast"));logLine("warn","[BakaLoader] stop requested, dousing the embers");}
    }else if(st.canStart){
      if(!S.prefs){toast("ᚦ "+T("hearth.no_profile.toast"));return;}
      if(updateBlocksStart()){toast("ᚦ "+updBlockMsg());return;}
      // Nothing changed -> straight to the same start call as before. A changed build or a
      // waiting Steam update puts the choice to the host first, and only then starts.
      withLaunchGuard(async answer=>{
        if(answer){startWithAnswer(answer);return;}
        const r=await rpc("server.start",{prefs:S.prefs});
        if(r===FAIL||rpcRefused(r)) return;
        applyState(r);toast("ᚠ "+T("hearth.starting.toast"));logLine("ok","[BakaLoader] start requested · profile "+(S.profileName||"?"));
      });
    }
    return;
  }
  running=!running; if(running) upMin=0;
  renderHearth();
  toast(running?"ᚠ "+T("hearth.starting.toast"):"ᛪ "+T("hearth.stopped.toast"));
  logLine(running?"ok":"warn",running?"[BakaLoader] server process launched (PID 150428)":"[BakaLoader] graceful shutdown: world saved, embers doused");
}
function renderHearth(){
  if(Native.available){renderHearthNative();return;}
  hCard.classList.toggle("crashed",false);
  flameSetState(running?"burning":"cold");
  if(running){
    hCard.classList.remove("cold");
    hState.textContent=T("hearth.card.hstate.running",{span:uptimeSpan(upMin)});
    hPid.textContent="RUNNING · PID 150428 · valheim_server.x86_64";   // preview fixture
    douseBtn.textContent=T("hearth.appbar.lifecycle.stop");
    douseBtn.title=T("hearth.appbar.lifecycle.stop.title");
  }else{
    hCard.classList.add("cold");
    hState.textContent=T("hearth.card.hstate.stopped");
    hPid.textContent=T("hearth.card.state.stopped_saved");
    douseBtn.textContent=T("hearth.appbar.lifecycle.start");
    douseBtn.title=T("hearth.appbar.lifecycle.start.title");
  }
  hState.title=hState.textContent;
  hPid.title=hPid.textContent;
  stokeBtn.disabled=!running;
  try{renderUpdatePill();}catch(_){}
  renderAppBar();
}
function renderHearthNative(){
  /* The browser preview has no native host to send a state, so this stands in for one.
     restartPending is false in it for the same reason every other key is quiet: nothing is
     running, so nothing can be behind what is saved. */
  const st=S.state||{status:"Stopped",canStart:false,canStop:false,restartPending:false};
  hCard.classList.toggle("cold",st.status!=="Running");
  /* the fire says what the server is doing: crashed outranks stopped, because the
     difference between "you stopped it" and "it fell over" is the whole point */
  const crashed=st.status==="Stopped"&&S.crashed;
  hCard.classList.toggle("crashed",!!crashed);
  flameSetState(crashed?"crashed"
    :st.status==="Running"?"burning"
    :st.status==="Starting"?"kindling"
    :st.status==="Stopping"?"smoldering"
    :"cold");
  if(st.status==="Running"){
    const up=S.upSince?Date.now()-S.upSince:0;
    hState.textContent=T("hearth.card.hstate.running",{span:uptimeSpan(up/60000)});
    /* An adopted install is a whole sentence of its own rather than a tail the
       running one grows, because a trailing clause is not where every language
       puts it. */
    const world=S.prefs?.WorldName||T("hearth.card.pid.world.unnamed");
    hPid.textContent=st.adopted
      ?T("hearth.card.pid.running.adopted",{world})
      :T("hearth.card.pid.running",{world});
  }else if(st.status==="Starting"){
    hState.textContent=T("hearth.card.hstate.starting");
    hPid.textContent=T("hearth.card.state.starting");
  }else if(st.status==="Stopping"){
    hState.textContent=T("hearth.card.hstate.stopping");
    hPid.textContent=T("hearth.card.state.stopping");
  }else{
    hState.textContent=T("hearth.card.hstate.stopped");
    hPid.textContent=T("hearth.card.state.stopped");
  }
  /* the state line is one nowrap line: say the whole of it on hover */
  hState.title=hState.textContent;
  hPid.title=hPid.textContent;
  renderHearthVersion();
  douseBtn.textContent=st.canStop?T("hearth.appbar.lifecycle.stop"):T("hearth.appbar.lifecycle.start");
  douseBtn.title=st.canStop?T("hearth.appbar.lifecycle.stop.title"):T("hearth.appbar.lifecycle.start.title");
  douseBtn.disabled=!(st.canStart||st.canStop);
  stokeBtn.textContent=st.countdownActive?T("hearth.card.restart.now"):T("hearth.card.restart");
  stokeBtn.title=st.countdownActive?T("hearth.card.restart.now.title"):T("hearth.card.restart.title");
  stokeBtn.disabled=!(st.canStop||st.countdownActive);
  stokeBtn.classList.toggle("btn-ember",!!st.countdownActive);
  stokeBtn.classList.toggle("btn-ghost",!st.countdownActive);
  /* Neither a start nor a restart may begin while this install is being rewritten. */
  if(updateBlocksStart()){
    if(!st.canStop){douseBtn.disabled=true;douseBtn.title=updBlockMsg();}
    stokeBtn.disabled=true;stokeBtn.title=updBlockMsg();
  }
  try{renderUpdatePill();}catch(_){}
  renderAppBar();
}
/* "Valheim 1.0.7 (net 39)" on the status card - the numbers the running server printed
   itself, never a guess. Falls back to what this profile last ran while it is down, so a
   host can see which build their worlds are on without starting anything. */
function renderHearthVersion(){
  const el=$("#hVer"); if(!el) return;
  const live=S.gameVersion, net=S.networkVersion;
  const last=S.prefs?.LastLaunchedGameVersion;
  if(live){
    el.textContent=net?T("hearth.card.version.live.net",{version:live,net})
                      :T("hearth.card.version.live",{version:live});
    el.title=T("hearth.card.version.live.title");
  }else if(last){
    el.textContent=T("hearth.card.version.last",{version:last});
    el.title=T("hearth.card.version.last.title");
  }else{
    el.style.display="none";
    return;
  }
  el.style.display="";
}
function applyState(st){
  if(!st) return;
  const prev=S.state?.status;
  S.state=st;
  /* a crash is true until the server is running again: it drives both the condition
     bar and the hearth fire */
  if(st.status==="Starting"||st.status==="Running"){S.crashed=false;clearCondition("crashRelaunch");}
  if(st.status==="Starting"&&prev!=="Starting"){
    // each server session opens under its own rule in the chronicle
    logDivider(T("saga.divider.session",{name:S.profileName||T("saga.divider.session.unnamed"),when:new Date().toLocaleString(LOC())}));
  }
  if(st.status==="Running"&&prev!=="Running"){
    S.upSince=Date.now();
    if(S.saveSec==null) S.saveSec=S.saveInterval;
  }
  if(st.status==="Stopped"){
    S.upSince=null; S.saveSec=null;
    $("#saveCountdown").textContent="-:-";
    if(S.invite){S.invite=null;renderNet();} // invite dies with the server
  }
  // Both versions come from the server's own banner - null until it has printed one.
  if("gameVersion" in st) S.gameVersion=st.gameVersion||null;
  if("networkVersion" in st) S.networkVersion=st.networkVersion||null;
  // A held start rides along with the state, so the banner survives a reload.
  if("launchHold" in st) setLaunchHold(st.launchHold);
  // Companion plugins that would not install. Absent on a 1.0.0 host, which simply
  // means the condition is never raised.
  if("pluginFailures" in st) conditionPluginFailures(st.pluginFailures,st.status);
  /* Settings saved while this world is up: true only while the live server is running a
     different command line from the one saved on disk. Absent on an older host, which simply
     means the row is never raised. */
  if("restartPending" in st) conditionRestartPending(st.restartPending,st.restartPendingSig);
  /* BepInEx: a pack update waiting for every server on this install to stop, and a start
     where the loader never wrote its log. Absent on an older host. */
  if("bepinex" in st) conditionBepInEx(st.bepinex,st.status);
  /* The same fact, said again where the host is doing the saving. */
  try{renderCfgRunningNote();}catch(_){}
  /* Whether an update can run at all depends on the run state, so the cached answer is
     stale the moment the server starts or stops. Re-ask on the transition rather than
     leaving the pill greyed with "Stop the server to update it." after it was stopped. */
  /* BakaLoader's own update reads the same transition: what the dialog may offer turns on
     whether a server is up, so a stop is the moment "update now" becomes possible. */
  if(prev!==st.status){try{refreshUpdateInfo();}catch(_){} try{refreshAppUpdateInfo();}catch(_){}}
  renderHearthNative();
}
douseBtn.addEventListener("click",e=>{e.stopPropagation();lifecycleToggle();});
/* The Server card's update pill: the same action the condition bar and the palette run. */
$("#hUpdPill")?.addEventListener("click",e=>{
  e.stopPropagation();
  if(e.currentTarget.disabled) return;
  updateBackupPrompt(false);
});
$("#abLifecycle").addEventListener("click",()=>lifecycleToggle());
/* A restart stops the server and starts it again, so it meets the same guard as a start.
   Ask first, then send the answer along with the restart. */
function smartRestart(){
  if(updateBlocksStart()){toast("ᚦ "+updBlockMsg());return;}
  withLaunchGuard(answer=>{smartRestartNow(answer);});
}
function smartRestartNow(answer){
  return rpc("server.restart",answer?{guard:answer}:{}).then(r=>{
    if(r===FAIL||rpcRefused(r)) return;
    applyState(r.state);
    if(r.restart==="countdown"){
      // countdown task spins up async - flip the button to "Restart NOW" right away
      if(S.state) S.state.countdownActive=true;
      renderHearthNative();
      toast("ᚱ "+T("hearth.restart.countdown.toast"));
      logLine("warn","[BakaLoader] restart requested: players online, 1-minute warning broadcast");
    }else if(r.restart==="bypassed"){
      toast("ᚱ "+T("hearth.restart.now.toast"));
      logLine("warn","[BakaLoader] restart countdown bypassed. Restarting now");
    }else if(r.restart==="now"){
      toast("ᚱ "+T("hearth.restart.empty.toast"));
      logLine("warn","[BakaLoader] restart requested: server empty, restarting immediately");
    }else{
      toast("ᚦ "+T("hearth.restart.unavailable.toast"));
    }
  });
}
stokeBtn.addEventListener("click",e=>{
  e.stopPropagation();
  if(Native.available){smartRestart();return;}
  toast("ᚱ "+T("hearth.restart.preview.toast"));
  logLine("warn","[BakaLoader] restart requested. Stoking the hearth");
});
/* Sail Forth copies a full join prompt - everything a friend needs to connect:
   server name / world / public ip:port / password (+ crossplay code when known).
   When a Waystone (custom domain) is raised, the domain rides in place of the raw IP -
   Valheim resolves A/AAAA records on join, but the port must still travel with it (no SRV). */
function joinHost(){return S.domain||S.extIp||"…";}
function buildJoinPrompt(){
  const p=S.prefs||{};
  const addr=joinHost()+":"+(p.Port??2456);
  const lines=[p.Name||T("hearth.sail.name.fallback"), p.WorldName||"-", addr];
  if(p.Password) lines.push(p.Password);
  if(p.Crossplay&&S.invite) lines.push(T("hearth.sail.crossplay",{code:S.invite}));
  return {text:lines.join("\n"), addr};
}
sailBtn.addEventListener("click",()=>{
  if(Native.available){
    const jp=buildJoinPrompt();
    navigator.clipboard?.writeText(jp.text).catch(()=>{});
    toast("ᛟ "+T("hearth.sail.copied.toast",{addr:jp.addr}));
    return;
  }
  if(!running){running=true;upMin=0;renderHearth();}
  const jp=buildJoinPrompt();
  navigator.clipboard?.writeText(jp.text).catch(()=>{});
  toast("ᛟ "+T("hearth.sail.copied.preview.toast"));
});
renderHearth();

/* The rolling average of the last dozen world writes, or a dash while nothing has been
   timed yet. A painter rather than five assignments, because the browser preview seeds
   the numbers while app.js is still being evaluated and the catalog is fetched: without
   somewhere for the second paint to run again, that one line read its own id. */
function renderSaveAvg(){
  const el=$("#saveAvg"); if(!el) return;
  const d=S.saveDur||[];
  el.textContent=d.length
    ?T("hearth.saves.avg.value",{ms:Math.round(d.reduce((a,b)=>a+b,0)/d.length)})
    :T("hearth.saves.avg.none");
}

/* Save-time bars: the last dozen world writes, tallest bar = slowest write.
   Each bar carries its own value so the chart is readable, not decorative. */
function renderSaveBars(){
  const box=$("#saveBars"); if(!box) return;
  const cap=$("#saveCap");
  const d=(S.saveDur||[]).slice(-12);
  if(!d.length){
    /* An empty chart, never the mock bars: a captioned chart of numbers nothing measured
       is a reading of save performance the host never had. */
    box.innerHTML=emptyState({compact:true,mark:"ᛉ",title:T("hearth.saves.empty.title"),
      reason:T("hearth.saves.empty.reason")});
    esWire(box);
    if(cap) cap.style.display="none";
    return;
  }
  if(cap) cap.style.display="";
  const max=Math.max.apply(null,d.concat([1]));
  box.innerHTML=d.map((ms,i)=>
    `<i class="${i===d.length-1?"hot":""}" style="height:${Math.max(10,Math.round(ms/max*100))}%" title="${esc(T("hearth.saves.ms",{ms}))}"></i>`
  ).join("");
}

/* Named empty-state actions. Each one is a command the interface already offers,
   so an empty panel can hand the host the same button the toolbar would. */
Object.assign(ES_ACTIONS,{
  scanMods:()=>{ if(Native.available) scanMods(); else toast("ᛋ "+T("mods.scan.preview.toast")); },
  addMod:()=>addModFlow(),
  installBepInEx:()=>{ if(Native.available) bepInExInstallFlow(null);
    else toast("ᛋ "+T("mods.add.preview.toast")); },
  openBackups:()=>barrowModal(),
  redrawMap:()=>{ const b=$("#atlasRedraw"); if(b) b.click(); },
  openLog:()=>goPage("saga"),
  copyJoin:()=>{ if(sailBtn) sailBtn.click(); },
  startServer:()=>lifecycleToggle(),
  clearModSearch:()=>setModFilter(""),
  clearRuneSearch:()=>setRuneFilter(""),
});

/* ---------- SPARKLINES (shared drawing; mock driver below) ---------- */
const N=40, cpu=[], ram=[];
function drawLine(el,data,min,max){
  const pts=data.map((v,i)=>{
    const x=(i/(N-1))*200, y=28-((v-min)/(max-min))*26;
    return x.toFixed(1)+","+Math.max(2,Math.min(28,y)).toFixed(1);
  }).join(" ");
  el.setAttribute("points",pts);
}

/* ---------- STATUS CLOCK ----------
   The status bar used to append one hardcoded time zone abbreviation, which was right
   on exactly one machine and a lie on every other. The time is the host's own wall
   clock, so it is formatted for the host's own locale and labelled with the host's
   own zone. */
function clock(){const d=new Date();const L=intl();return L?L.fmtTime(d):pad(d.getHours())+":"+pad(d.getMinutes());}
const CLOCK_OPTS={
  hour:"2-digit",minute:"2-digit",second:"2-digit",
  hourCycle:"h23",timeZoneName:"short",
};
/* Asked for per paint rather than built once, because the formatter has to
   follow the language: a clock built at boot would keep writing in whatever was
   active then. The lookup caches them, so this costs one map lookup a second. */
function clockFmt(){
  const L=intl(); if(L) return L.dateTimeFormat(CLOCK_OPTS);
  try{ return new Intl.DateTimeFormat(undefined,CLOCK_OPTS); }catch(_){ return null; }
}
/* The fallback is the old digits with the zone spelled out from the offset, so even a
   runtime with no Intl at all still says which clock it is showing. */
function clockOffsetName(d){
  const mins=-d.getTimezoneOffset(), sign=mins<0?"-":"+", a=Math.abs(mins);
  const hh=Math.floor(a/60), mm=a%60;
  return "UTC"+sign+hh+(mm?":"+String(mm).padStart(2,"0"):"");
}
function clockBarText(d){
  const f=clockFmt();
  if(f){ try{ return f.format(d); }catch(_){} }
  return [d.getHours(),d.getMinutes(),d.getSeconds()].map(x=>String(x).padStart(2,"0")).join(":")+
    " "+clockOffsetName(d);
}
function paintClockSeg(){
  const el=$("#clockSeg"); if(el) el.textContent=clockBarText(new Date());
}
/* Painted once at boot as well as every second, so the first second of the window is
   the host's own clock rather than the placeholder written into the page. */
paintClockSeg();
setInterval(()=>{
  paintClockSeg();
  if(!Native.available) $("#tickVal").textContent=running?(58+Math.floor(Math.random()*3))+"/60":"-";
},1000);

/* ---------- COPY CHIP ---------- */
$("#copyIp").addEventListener("click",e=>{
  e.stopPropagation();
  if(Native.available){
    const addr=(S.extIp||"…")+":"+(S.prefs?.Port??2456);
    navigator.clipboard?.writeText(addr).catch(()=>{});
    e.target.textContent=T("hearth.net.chip.copied");
    toast("ᚾ "+T("hearth.net.copied.toast",{addr}));
    setTimeout(()=>e.target.textContent=T("common.chip.copy"),1400);
    return;
  }
  e.target.textContent=T("hearth.net.chip.copied");
  toast("ᚾ "+T("hearth.net.copied.toast",{addr:"203.0.113.42:2456"}));   // preview fixture
  setTimeout(()=>e.target.textContent=T("common.chip.copy"),1400);
});
function renderWaystone(){
  const line=$("#netDomainLine"); if(!line) return;
  const port=S.prefs?.Port??2456;
  line.style.display=S.domain?"":"none";
  if(S.domain) $("#netDomain").textContent=S.domain+":"+port;
}
function renderNet(){
  if(!Native.available) return;
  const port=S.prefs?.Port??2456;
  renderWaystone();
  $("#netAddr").textContent=(S.extIp||"…")+":"+port;
  /* The chips copy what the line is ABOUT, not the words around it: a label that
     reads "LAN" in English is a different word in every other language, and the old
     chip found the address by stripping the English prefix off the text. */
  const lan=(S.intIp||"…")+":"+port;
  $("#netLan").textContent=T("hearth.net.addr.lan.value",{addr:lan});
  $("#netLan").dataset.value=lan;
  $("#netLoc").textContent="127.0.0.1:"+port;
  const il=$("#netInviteLine");
  il.style.display=S.invite?"":"none";
  if(S.invite){
    $("#netInvite").textContent=T("hearth.net.addr.invite.value",{code:S.invite});
    $("#netInvite").dataset.value=S.invite;
  }
  const rl=$("#netRconLine"), on=!!S.prefs?.RconEnabled;
  rl.style.display=on?"":"none";
  if(on) $("#netRcon").textContent=T("hearth.net.addr.rcon.value",{addr:"127.0.0.1:"+(S.prefs.RconPort??25575)});
  $("#netPill").textContent=on?T("hearth.net.pill.on"):T("hearth.net.pill.off");
  $("#netPill").className="pill "+(on?"green":"blue");
}
/* LAN / local / invite copy chips - copy exactly what's displayed (works in mock + native) */
[["#copyLan","#netLan"],["#copyLoc","#netLoc"],["#copyInv","#netInvite"],["#copyDomain","#netDomain"]].forEach(([chip,src])=>{
  $(chip).addEventListener("click",e=>{
    e.stopPropagation();
    const el=$(src);
    /* The value the line was built from where the app wrote one, and the English
       shape for the browser preview, which has no app behind it. */
    const txt=el.dataset.value||el.textContent.replace(/^(LAN|invite)\s+/,"");
    navigator.clipboard?.writeText(txt).catch(()=>{});
    e.target.textContent=T("hearth.net.chip.copied");
    toast("ᚾ "+T("hearth.net.copied.value.toast",{value:txt}));
    setTimeout(()=>e.target.textContent=T("common.chip.copy"),1400);
  });
});
/* Recent-log card header: jump straight to the full chronicle */
$("#openSagaChip")?.addEventListener("click",e=>{e.stopPropagation();goPage("saga");});

/* open-folder affordances (shell.open - native only) */
$("#openWorlds").addEventListener("click",e=>{
  e.stopPropagation();
  if(Native.available) rpc("shell.open",{target:"saveData"});
  else toast("ᛃ "+T("hearth.saves.open.preview.toast"));
});
$("#openLogs").addEventListener("click",()=>{
  if(Native.available) rpc("shell.open",{target:"logs"});
  else toast("ᛃ "+T("saga.logs.open.preview.toast"));
});

/* ember smoulder - wrap every letter in its own span so each ember glows on its
   own clock (randomized duration + phase; ~1 in 3 letters runs the redder
   keyframe set). Used by the horn-of-mead link and the Atlas fog-of-war chip. */
function emberize(el){
  if(!el) return;
  const rnd=(lo,hi)=>lo+Math.random()*(hi-lo);
  [...el.childNodes].forEach(node=>{
    if(node.nodeType!==Node.TEXT_NODE) return;   // keep the <br> intact
    const frag=document.createDocumentFragment();
    for(const ch of node.textContent){
      if(ch===" "||ch==="\u00A0"){ frag.append(ch); continue; }
      const s=document.createElement("span");
      s.className="mc"+(Math.random()<.34?" mc-r":"");
      s.textContent=ch;
      s.style.setProperty("--dur",rnd(3.4,6.6).toFixed(2)+"s");
      s.style.setProperty("--del",(-rnd(0,6.6)).toFixed(2)+"s"); // negative = start mid-cycle
      frag.append(s);
    }
    node.replaceWith(frag);
  });
}
/* The link's words come out of the catalog and the embers are lit over them,
   in that order: emberize wraps every letter in its own span, and a textContent
   write afterwards would throw those spans away and leave the link dead. The rune
   and the pennant either side are ornament, so they stay here. */
function renderMead(){
  const el=$("#meadLink"); if(!el) return;
  el.textContent="ᛥ\u00a0"+T("side.mead.label")+"\u00a0⚑";
  emberize(el);
}
renderMead();
emberize($("#lchipFog")); // fog now defaults on (may veil the whole realm) - keep its switch eye-catching

/* horn-of-mead - opens the donate page in the default browser (native only) */
$("#meadLink").addEventListener("click",e=>{
  e.preventDefault();
  if(Native.available){ rpc("shell.openUrl",{target:"donate"}); toast("ᛥ "+T("side.mead.opening.toast")); }
  else toast("ᛥ "+T("side.mead.preview.toast"));
});

/* ---------- BAKALOADER'S OWN UPDATE ----------
   One answer, read by every surface that mentions it: the sidebar version, the Hearth
   pill, the standing row and the dialog. They all come off app.updateStatus, which the
   app builds from the same record it pushes on app.updateAvailable, so no two of them
   can say different things about whether there is an update or which version it is.
   When there is nothing newer, none of them show anything at all. */
let APP_UPD=null;
/* The shipped version as index.html carries it. Kept so the sidebar has something
   honest to fall back to before app.info has answered, and so clearing the update
   state puts the line back exactly the way it was. */
const SIDE_VER_PLAIN=($("#sideVer")?.textContent||"").trim();
function sideVerBase(){
  const v=S.version||(APP_UPD&&APP_UPD.installedVersion)||"";
  return v?"v"+v:SIDE_VER_PLAIN;
}
/* The version the app reported, written everywhere it is shown. Named so the boot
   handler is not the only thing that can run it: the sidebar has two states now, and a
   plain textContent write would drop the ember spans and leave the glow dead. */
function applyAppVersion(v){
  if(v) S.version=v;
  const sb=$("#sbVersion"); if(sb) sb.textContent="v"+(S.version||"");
  renderSideVer();
}
async function refreshAppUpdateInfo(){
  if(Native.available){
    const r=await Native.call("app.updateStatus",{}).catch(()=>null);
    if(r&&typeof r==="object"){
      APP_UPD=r;
      if(r.installedVersion) S.version=r.installedVersion;
    }
  }
  renderAppUpdatePill();
  renderSideVer();
  /* Nothing newer any more (the update went in, or the release was pulled): the standing
     row is about something that is no longer true, so it goes with the rest of it. */
  if(APP_UPD&&!APP_UPD.updateAvailable){
    try{if(CONDITIONS.has("appUpdate")){APP_UPDATE_V=null;clearCondition("appUpdate");}}catch(_){}
  }else{
    /* Still waiting, and what the row may promise turns on whether a world is up. This runs
       on every server start and stop, so the sentence follows the hearth rather than keeping
       whatever was true when the release was first found. */
    try{if(CONDITIONS.has("appUpdate")) conditionAppUpdate(APP_UPDATE_V);}catch(_){}
  }
  return APP_UPD;
}
/* The sidebar's version line. Two states and nothing in between: the shipped version on
   its own, or the shipped version, the waiting one, and a glow that says so. */
function renderSideVer(){
  const el=$("#sideVer"); if(!el) return;
  const foot=el.closest(".foot-ver"); if(!foot) return;
  const u=APP_UPD||{};
  if(u.updateAvailable&&u.latestVersion){
    foot.classList.add("upd");
    foot.setAttribute("role","button");
    foot.setAttribute("tabindex","0");
    foot.title=T("appupd.side.title");
    el.textContent=T("appupd.side.line",{installed:sideVerBase(),latest:u.latestVersion});
    emberize(el);   /* AFTER the write, always: textContent replaces the ember spans */
  }else{
    foot.classList.remove("upd");
    foot.removeAttribute("role");
    foot.removeAttribute("tabindex");
    foot.removeAttribute("title");
    el.textContent=sideVerBase();
  }
}
/* The Hearth pill, beside the Valheim server's own. Ember and named so the two are never
   mistaken for each other: this one says which BakaLoader is waiting. */
function renderAppUpdatePill(){
  const el=$("#hAppUpdPill"); if(!el) return;
  const u=APP_UPD||{};
  if(!u.updateAvailable||!u.latestVersion){el.style.display="none";return;}
  el.style.display="";
  el.textContent=T("appupd.pill.label",{version:u.latestVersion});
  emberize(el);   /* AFTER the write, every time, or the pill sits there unlit */
  el.title=u.anyServerRunning
    ?T("appupd.pill.title.running")
    :T("appupd.pill.title.idle");
}
/* A plain sentence for every way an asked-for update can be turned down. Each reason the
   app can send gets its own, because they call for different things: one waits, one turns
   a switch back on, one is a file the app could not read. Built on demand rather than once,
   so the terminology switch reaches them too. */
function appUpdRefusal(reason){
  if(reason==="serverBusy")
    return T("appupd.refusal.server_busy");
  if(reason==="checkingOff")
    return T("appupd.refusal.checking_off");
  if(reason==="cooldown")
    return T("appupd.refusal.cooldown");
  if(reason==="notAvailable")
    return T("appupd.refusal.not_available");
  if(reason==="offline")
    return T("appupd.refusal.offline");
  return T("appupd.refusal.other");
}
/* The one place that offers to do something about a waiting release. What it offers turns
   on whether a server is up: installing means closing BakaLoader, and the server is this
   app's own child process, so a live world would go down with it. That is the host's call
   to make from the server controls. This dialog never offers to stop anything. */
async function appUpdateModal(){
  const u=(await refreshAppUpdateInfo())||APP_UPD||{};
  const v=u.latestVersion||"";
  const running=!!u.anyServerRunning;
  const body=running
    ?`<div style="margin-bottom:8px">${esc(T("appupd.modal.running.body"))}</div>`+
     `<div class="subval">${esc(T("appupd.modal.running.note"))}</div>`
    :`<div style="margin-bottom:8px">${esc(T("appupd.modal.idle.body"))}</div>`+
     `<div class="subval">${esc(T("appupd.modal.idle.note"))}</div>`;
  const primary=running?T("appupd.modal.go.running"):T("appupd.modal.go.idle");
  const m=modalOpen(
    `<div class="mtitle"><span class="r" style="margin-right:8px">ᛟ</span>`+
      `${esc(v?T("appupd.modal.title",{version:v}):T("appupd.modal.title.noversion"))}</div>`+
    `<div class="mbody">`+body+
      `<div class="subval" id="auWhy" style="margin-top:8px;display:none"></div>`+
    `</div>`+
    `<div class="mbtns">`+
      `<button class="btn btn-ember btn-sm" id="auGo">${esc(primary)}</button>`+
      `<button class="btn btn-ghost btn-sm" id="auNotes">${esc(T("appupd.modal.notes"))}</button>`+
      `<button class="btn btn-ghost btn-sm" id="auLater">${esc(T("appupd.modal.later"))}</button>`+
    `</div>`,appUpdateModal);
  const why=m.querySelector("#auWhy");
  const say=text=>{why.textContent=text;why.style.display="";};
  /* The one way the button cannot work whatever the host clicks, said here rather than
     after a click that fails. */
  if(u.checkEnabled===false){
    const go=m.querySelector("#auGo");
    go.disabled=true;
    go.title=T("appupd.modal.go.off.title");
    say(appUpdRefusal("checkingOff"));
  }
  m.querySelector("#auLater").addEventListener("click",modalClose);
  /* The notes for the release the app actually found, not whatever is newest on the
     repository today: the app holds that address and opens it, so what the host reads is
     the version the dialog just offered them. When the check handed over no address of its
     own, the button still has somewhere honest to go: the releases page. */
  m.querySelector("#auNotes").addEventListener("click",()=>{
    if(!Native.available){toast("ᛟ "+T("appupd.notes.preview.toast"));return;}
    const open=u.releaseUrl
      ?Native.call("shell.openAppRelease",{})
      :Native.call("shell.openUrl",{target:"releases"});
    open.catch(()=>{
      say(T("appupd.notes.failed"));
    });
  });
  m.querySelector("#auGo").addEventListener("click",async e=>{
    const btn=e.currentTarget;
    btn.disabled=true;
    if(running){
      /* The switch in Upkeep is the mechanism, so this flips that switch rather than
         inventing a second way to say the same thing. */
      if(Native.available){
        const r=await rpc("userprefs.save",{prefs:{AutoUpdateBakaLoader:true}});
        if(r===FAIL){btn.disabled=false;say(T("appupd.modal.save_failed"));return;}
      }
      $("#tAutoUpdApp")?.classList.add("on");
      try{syncUpkeepGates();}catch(_){}
      modalClose();
      toast("ᛟ "+T("appupd.modal.armed.toast"));
      await refreshAppUpdateInfo();
      if(CONDITIONS.has("appUpdate")) conditionAppUpdate(APP_UPDATE_V);
      return;
    }
    if(!Native.available){btn.disabled=false;say(T("appupd.modal.preview_only"));return;}
    const r=await Native.call("app.selfUpdateNow",{}).catch(()=>null);
    if(r&&r.ok){
      toast("ᛟ "+T("appupd.modal.fetched.toast"));
      return;   /* the app closes itself from here; leave the dialog as it is */
    }
    btn.disabled=false;
    say(appUpdRefusal(r&&r.reason));
    await refreshAppUpdateInfo();
  });
}
$("#hAppUpdPill")?.addEventListener("click",e=>{e.stopPropagation();appUpdateModal();});
/* The sidebar version line opens the same dialog while an update is waiting, by click or
   by keyboard. It is only a button in that state, so a plain version string stays inert. */
(()=>{
  const foot=$("#sideVer")?.closest(".foot-ver"); if(!foot) return;
  foot.addEventListener("click",()=>{if(foot.classList.contains("upd"))appUpdateModal();});
  foot.addEventListener("keydown",e=>{
    if(!foot.classList.contains("upd")) return;
    if(e.key!=="Enter"&&e.key!==" ") return;
    e.preventDefault();
    appUpdateModal();
  });
})();

/* ---------- MODS: scan / update-all / render / sort / remove ---------- */
/* numeric-aware version compare (1.2.10 > 1.2.9); missing/dash versions sort lowest */
function verCmp(a,b){
  const pa=String(a||"").split("."), pb=String(b||"").split(".");
  for(let i=0;i<Math.max(pa.length,pb.length);i++){
    const x=parseInt(pa[i],10), y=parseInt(pb[i],10);
    const nx=isNaN(x)?-1:x, ny=isNaN(y)?-1:y;
    if(nx!==ny) return nx-ny;
  }
  return 0;
}
/* returns a sorted copy per S.modSort; dir 0 = the default scan order untouched.
   First click: name A→Z, versions hi→lo, status updates-first. Second click reverses. */
function sortedMods(mods){
  const {col,dir}=S.modSort;
  if(!col||!dir) return mods;
  const ver=col==="installed"||col==="latest";
  const m=(dir===1)!==ver?1:-1, s=[...mods];
  if(col==="name") s.sort((a,b)=>m*cmpText(a.ModName||"",b.ModName||""));
  else if(col==="installed") s.sort((a,b)=>m*verCmp(a.InstalledVersion,b.InstalledVersion));
  else if(col==="latest") s.sort((a,b)=>m*verCmp(a.LatestVersion,b.LatestVersion));
  else if(col==="status") s.sort((a,b)=>m*((b.UpdateAvailable?1:0)-(a.UpdateAvailable?1:0)));
  return s;
}
/* ---------- SEARCH BOXES ----------
   One rule for every search box in the app: what is typed is cut at whitespace, and a
   row is kept only when EVERY piece is found somewhere in that row's own text, case
   ignored. So "jere world" finds JereKuusela's WorldEditCommands whichever order it is
   typed in, and a single word still behaves the way anybody expects.
   Nothing here sorts, and nothing here removes anything from the list the rest of the
   app reads: a search box narrows what is drawn and that is the whole of its job. */
function searchTokens(q){
  return String(q||"").trim().toLowerCase().split(/\s+/).filter(Boolean);
}
function searchHit(haystack,tokens){
  const hay=String(haystack||"").toLowerCase();
  return tokens.every(t=>hay.includes(t));
}
/* The last piece of a path, whichever slash the host's machine writes. */
function lastPathPart(p){
  const parts=String(p||"").split(/[\\/]/).filter(Boolean);
  return parts.length?parts[parts.length-1]:"";
}
/* Everything a mod row shows, gathered into one string to search: its name and author,
   the folder it sits in, both version numbers, and every tag and chip the row can carry
   (patcher, Hexium, bundled, and the word in the Status cell). */
function modSearchText(m){
  if(!m) return "";
  const bits=[
    m.ModName, m.Author, m.FullName,
    lastPathPart(m.PluginDirectory), lastPathPart(m.PatcherDirectory),
    m.InstalledVersion, m.LatestVersion, m.hexiumLatest,
    m.IsPatcher?T("mods.tag.patcher"):"",
    (m.installedSource==="hexium")?T("mods.tag.hexium"):"",
    /* The pill the row draws, word for word. A search over the table has to carry the
       quiet pill too, or a host looking for the mods that were pulled is the one host
       the box cannot answer. */
    m.Bundled?T("mods.status.bundled"):modIsHeld(m)?T("mods.status.held")
      :m.notListed?T("mods.status.not_listed")
      :(m.UpdateAvailable?T("mods.status.update"):T("mods.status.current")),
    m.possiblyOutdated&&!m.IsPatcher?T("mods.possibly_outdated.yes"):"",
  ];
  return bits.filter(Boolean).join(" ");
}
/* A copy the host took from Hexium that either site has since moved past. It is not
   CURRENT: something newer stands out there and BakaLoader is deliberately not taking it,
   whichever site the newer build is on. A Hexium copy nothing has moved past is genuinely
   current, and reads that way. */
function modIsHeld(m){
  if(!m||m.installedSource!=="hexium") return false;
  return !!(m.thunderstoreNewer&&m.LatestVersion)||!!(m.hexiumNewer&&m.hexiumLatest);
}
/* THE ONE PLACE the Mods search is allowed to be applied: the table body render calls
   this and nothing else does. Every counter in the header, the Update all button, the
   waiting-updates pill, the condition bar and the unattended paths all read the whole
   list, so a search narrows what is on screen and never what the app acts on. */
function modsForBody(mods){
  const tokens=searchTokens(S.modFilter);
  if(!tokens.length) return mods;
  return mods.filter(m=>searchHit(modSearchText(m),tokens));
}
/* A row is found again by its own name, never by where it happens to sit in the table,
   so a row menu or a mark opened while a search is narrowing the table still acts on
   the mod that was clicked. */
function modByKey(key){
  if(!key) return null;
  return (S.mods||[]).find(m=>m&&m.FullName===key)||null;
}
/* Which site an install answer came from. The two Hexium calls have always spelled the
   key Source and the Thunderstore add now carries source, so this reads whichever is
   there and every caller stops caring which. */
function modResultSource(r){
  if(!r) return "";
  return String(r.Source??r.source??"");
}
/* ---------- SHARED TABLE SORT ----------
   Click cycles a column: first click, reverse, then back to the table's own order.
   descFirst names the columns whose first click reads high to low (versions, counts,
   durations), which is what someone clicking "Playtime" or "Latest" actually wants.
   The Mods table and the player roster both run through this. */
function sortCycle(state,col){
  if(state.col===col) state.dir=(state.dir+1)%3;
  else{state.col=col;state.dir=1;}
  if(!state.dir) state.col=null;
}
function renderSortMarks(sel,state,descFirst){
  document.querySelectorAll(sel).forEach(th=>{
    const key=th.dataset.sort;
    const on=state.col===key&&state.dir;
    th.classList.toggle("sorted",!!on);
    const up=(state.dir===1)!==descFirst.has(key);
    th.setAttribute("aria-sort",on?(up?"ascending":"descending"):"none");
    th.querySelector(".sortmark").textContent=on?(up?"▲":"▼"):"";
  });
}
function wireSort(sel,state,onChange){
  document.querySelectorAll(sel).forEach(th=>{
    th.setAttribute("role","button");
    th.setAttribute("tabindex","0");
    const go=()=>{sortCycle(state,th.dataset.sort);onChange();};
    th.addEventListener("click",go);
    th.addEventListener("keydown",e=>{
      if(e.key==="Enter"||e.key===" "||e.key==="Spacebar"){e.preventDefault();go();}
    });
  });
}
const MOD_DESC_FIRST=new Set(["installed","latest"]);
function renderModSortMarks(){renderSortMarks("#page-mods th.sortable",S.modSort,MOD_DESC_FIRST);}
wireSort("#page-mods th.sortable",S.modSort,()=>renderMods());
/* The six sentences a Mods row explains itself with used to stand as consts here, and
   every one of them is asked for by id now. The Latest cell on a delisted mod was already
   written that way (mods.status.not_listed.tip) and the reason it gave holds for all six:
   a constant handed to the lookup is a sentence no gate can see, no translator can reach
   and no completeness check can count.
     mods.col.possibly_outdated.tip          the hint under the column
     mods.col.possibly_outdated.tip.unknown  what it says instead when the game's own
                                             update date could not be read for this server
     mods.tag.hexium.tip                     the chip a copy from the second site carries
     mods.status.held.tip                    the HELD pill standing beside that chip
     mods.mark.hexium_newer.tip              and the two offers that hang off Latest
     mods.mark.thunderstore_newer.tip                                                    */
/* The mark that hangs off the Latest cell, or nothing. At most one: a row is either a
   Thunderstore install the other site is ahead of, or a Hexium install Thunderstore is
   ahead of, and never both. */
/* The mark names its own row rather than a position in the table, so it still opens the
   right offer while a search is narrowing what is drawn.
   A mark that would repeat the number already printed in the cell it hangs off says the
   words alone: the Latest cell reads the Thunderstore version, so "newer on Thunderstore
   1.66.0" beside a cell reading 1.66.0 said the same thing twice. */
function modLatestMark(m){
  const key=esc(m.FullName);
  /* The cell this mark hangs off prints the Thunderstore version, so a mark carrying
     that same number says it twice. Said once. */
  const cell=String(m.LatestVersion||"");
  const say=(words,version)=>esc(words)+(String(version||"")===cell?"":" "+esc(version));
  if(m.hexiumNewer&&m.hexiumLatest)
    return ` <span class="modmark" data-hex="${key}" role="button" tabindex="0" title="${esc(T("mods.mark.hexium_newer.tip"))}">${say(T("mods.mark.hexium_newer"),m.hexiumLatest)}</span>`;
  if(m.thunderstoreNewer&&m.LatestVersion)
    return ` <span class="modmark ts" data-ts="${key}" role="button" tabindex="0" title="${esc(T("mods.mark.thunderstore_newer.tip"))}">${say(T("mods.mark.thunderstore_newer"),m.LatestVersion)}</span>`;
  return "";
}
/* The transient status a row shows while a bulk update runs. Cleared by the fresh scan
   that follows the run, so it never lingers into the resting table. */
function renderRowStatus(st){
  if(!st) return "";
  if(st.phase==="queued") return `<span class="pill" style="opacity:.5">${esc(T("mods.row.queued"))}</span>`;
  if(st.phase==="updating") return `<span class="pill ember">${esc(T("mods.row.updating"))}</span>`;
  if(st.phase==="done"){
    const move=(st.from||"?")+" → "+(st.to||"?");
    return `<span class="pill green" title="${esc(move)}">✓ ${esc(move)}</span>`;
  }
  /* Two entries rather than one with a ternary inside it: a run that failed with nothing
     to say would otherwise read "Failed:" with a colon hanging off the end of it. */
  if(st.phase==="failed")
    return `<span class="pill" style="color:var(--blood);border-color:var(--blood)" title="${esc(st.error||"")}">${esc(st.error?T("mods.row.failed",{detail:st.error}):T("mods.row.failed.nodetail"))}</span>`;
  return "";
}
/* The bulk-update bar's own record: how many mods, how many finished, which one is running.
   Kept apart from the row map so the bar and the rows can update from one event. */
let MOD_UPD=null;
function renderModUpdateProgress(){
  const wrap=$("#modUpdProg"); if(!wrap) return;
  if(!MOD_UPD){wrap.style.display="none";wrap.innerHTML="";return;}
  const u=MOD_UPD;
  wrap.style.display="flex";
  if(u.phase==="checking"){
    wrap.innerHTML=
      `<span class="hbmsg">${esc(T("mods.progress.checking"))}</span>`+
      `<span class="hbprog indet" role="progressbar" aria-label="${esc(T("mods.progress.aria"))}" title="${esc(T("mods.progress.working"))}"><i></i></span>`;
    return;
  }
  const total=u.total||0;
  const pct=total>0?Math.max(0,Math.min(100,Math.round((u.done||0)/total*100))):0;
  /* One whole sentence per shape, with the mod and the two numbers in named slots: the
     bar used to be two words glued to three values, which no translator can reorder. */
  const label=(u.phase==="updating"&&u.current)
    ? T("mods.progress.updating.one",{mod:u.current,index:u.index,total:total})
    : T("mods.progress.updating.all",{done:u.done||0,total:total});
  wrap.innerHTML=
    `<span class="hbmsg">${esc(label)}</span>`+
    `<span class="hbprog" role="progressbar" aria-label="${esc(T("mods.progress.aria"))}" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${pct}" title="${pct}%"><i style="width:${pct}%"></i></span>`+
    `<span class="hbpct">${pct}%</span>`;
}
/* One inbound mods.updateProgress event: advance the bar and flip the named row. A light
   re-render only; the fresh scan at the end of the run is what re-reads the folder. */
function onModUpdateProgress(d){
  if(!d) return;
  S.modsUpdating=true;
  S.modRowStatus=S.modRowStatus||{};
  const phase=d.phase;
  if(phase==="checking"){
    MOD_UPD={phase:"checking",total:0,done:0,current:null,index:0};
  }else if(phase==="updating"){
    MOD_UPD={phase:"updating",total:d.total||0,done:(MOD_UPD&&MOD_UPD.done)||0,current:d.mod,index:d.index||0};
    if(d.mod) S.modRowStatus[d.mod]={phase:"updating"};
  }else if(phase==="done"||phase==="failed"){
    const done=((MOD_UPD&&MOD_UPD.done)||0)+1;
    MOD_UPD={phase:phase,total:d.total||0,done:done,current:null,index:d.index||done};
    if(d.mod) S.modRowStatus[d.mod]=(phase==="done")
      ?{phase:"done",from:d.fromVersion,to:d.toVersion}
      :{phase:"failed",error:d.error};
  }
  renderModUpdateProgress();
  renderMods();
}
/* The line under the Mods heading. It reads when the PACKAGE LIST was read, not when the
   button was pressed: those were the same thing until a press started reusing a list read
   moments ago, and showing the press time would have been the same lie that started all
   this. On hover it names which of the two addresses answered, because the fast one can
   fall back to the slow one without anything else on screen changing. */
function renderModIndexLine(count){
  const el=$("#modsSub"); if(!el) return;
  /* With no time to show, the line says the list has not been read rather than saying
     "checked" and then trailing off: a press that came back with nothing read is the one
     moment the word "checked" would be claiming something that did not happen. */
  const at=S.modIndexAt;
  /* With the second site switched on a scan reads both of them, so the line names both.
     Off, BakaLoader opens no connection to it at all, and the line names the one site
     that was actually read. */
  const both=!!S.hexium;
  /* Four whole sentences rather than a count, a joiner and two fragments. The clock face
     and the number sit in named slots, because a language that puts the time first has
     nowhere to put it when the sentence is glued together here. */
  const p={count:count,at:at};
  el.textContent=at
    ?(both?T("mods.index.line.checked.both",p):T("mods.index.line.checked",p))
    :(both?T("mods.index.line.unread.both",p):T("mods.index.line.unread",p));
  el.title=S.modIndexSource==="listing-index"?T("mods.index.via.listing_index")
    :(S.modIndexSource==="v1"?T("mods.index.via.full_listing"):"");
}
/* The two buttons in the Mods header say which sites they reach, so they follow the
   Upkeep switch rather than naming Thunderstore whatever the setting is. A host who never
   turned the second site on reads its name on the switch and nowhere else, which is the
   rule the row menu and the Latest marks already live under. Only the words move: both
   buttons keep the id they have always had, so everything wired to them still finds
   them.
   The words are this render's now, which is why neither button carries a data-i18n any
   more: an element the walker fills and a render overwrites is a sentence with two
   owners, and the loser is whichever ran second. The rune and the no break space that
   follows it are part of the entry, the way every other runed button already carries
   them. */
function renderModSourceLabels(){
  const both=!!S.hexium;
  const add=$("#addModBtn");
  if(add) add.textContent=both?T("mods.add.label.link"):T("mods.add.label");
  const scan=$("#scanBtn");
  if(scan) scan.textContent=both?T("mods.scan.label.sites"):T("mods.scan.label");
}
/* Both halves of the switch in one place: the page's own mirror of the setting, and the
   Mods hall drawn again in the same breath so its labels never sit a render behind it.
   The host and the browser preview both come through here, so the two cannot drift. */
function setHexiumSource(on){
  S.hexium=!!on;
  try{renderMods();}catch{}
}
/* The clock face of an ISO instant the bridge sent, in the host's own time. Written by
   the lookup where there is one, exactly as clock() is, so the two times under the Mods
   heading are never in two different notations. */
function hhmm(iso){
  if(!iso) return null;
  const d=new Date(iso);
  if(isNaN(d.getTime())) return null;
  const L=intl();
  return L?L.fmtTime(d):pad(d.getHours())+":"+pad(d.getMinutes());
}
function renderMods(){
  const busy=S.modsScanning||S.modsUpdating;
  $("#scanBtn").disabled=busy;
  $("#addModBtn").disabled=busy;
  renderModSourceLabels();
  /* Above the table, and outside it: the loader is not a mod and never joins the list
     the counts, Update all and the waiting pill are taken from. */
  renderBepInExRow();
  const scanned=S.mods!==null;
  const mods=sortedMods(S.mods||[]);
  const upd=mods.filter(m=>m.UpdateAvailable);
  /* The rune and the no break space after it belong to the entry, the way they do on
     every other runed button, and the count sits in a slot. esc() still stands between
     the words and innerHTML: the button is markup, and a translated sentence is text. */
  $("#updAllBtn").innerHTML=esc(T("mods.update_all.label",{count:scanned?upd.length:"-"}));
  $("#updAllBtn").disabled=busy||!upd.length;
  $("#modCount").textContent=scanned?mods.length:"-";
  /* The rail's own count. A hall that has not been scanned has no number to show, and
     "-" is not a number, so the plural falls to the category every language has. */
  $("#sbMods").textContent=T("side.mods.count",{count:scanned?mods.length:"-"});
  renderModSortMarks();
  if(!scanned){
    $("#modTable").innerHTML=`<tr><td colspan="5">${S.modsScanning
      ?emptyState({mark:"ᛋ",title:T("mods.empty.scanning.title"),reason:T("mods.empty.scanning.reason")})
      :emptyState({mark:"ᚱ",title:T("mods.empty.unscanned.title"),
          reason:T("mods.empty.unscanned.reason"),
          action:{name:"scanMods",label:T("mods.empty.unscanned.action")}})}</td></tr>`;
    esWire($("#modTable"));
    $("#modTable")._list=[];
    $("#modUpdWrap").innerHTML="";
    /* Two whole sentences rather than a stem and two endings: a language that says the
       state before the thing it is a state of has nowhere to put "scanning". */
    $("#modsSub").textContent=S.modsScanning
      ?T("mods.index.line.scanning"):T("mods.index.line.not_scanned");
    /* Nothing has been read, so the note saying which address answered goes with it. */
    $("#modsSub").title="";
    renderModShowing(0,0);
    return;
  }
  const anyGameDate=mods.some(m=>m.gameUpdatedUtc);
  const th=$("#thPossiblyOutdated");
  if(th) th.title=(scanned&&mods.length&&!anyGameDate)
    ?T("mods.col.possibly_outdated.tip.unknown"):T("mods.col.possibly_outdated.tip");
  /* The one use of the search: the rows that get drawn. Everything above and below this
     line counts the whole list. */
  const shown=modsForBody(mods);
  $("#modTable").innerHTML=shown.map(m=>{
    const has=!!m.UpdateAvailable;
    /* A copy the host took from Hexium is HELD, not CURRENT: BakaLoader leaves it where
       it is even when a site has moved past it, and the pill has to say so rather than
       reading like the row is level with the world. */
    const held=modIsHeld(m);
    /* A row whose package the list came back without has no Latest to be level with, so
       it cannot read CURRENT either. It says the one thing that is known: Thunderstore is
       not listing it at the moment, and nothing has been done about that. */
    const pill=m.Bundled?`<span class="pill ember">${esc(T("mods.status.bundled"))}</span>`
      :held?`<span class="pill amber" title="${esc(T("mods.status.held.tip"))}">${esc(T("mods.status.held"))}</span>`
      :m.notListed?`<span class="pill grey" title="${esc(T("mods.status.not_listed.tip"))}">${esc(T("mods.status.not_listed"))}</span>`
      :`<span class="pill ${has?"amber":"green"}">${esc(has?T("mods.status.update"):T("mods.status.current"))}</span>`;
    const st=S.modRowStatus&&S.modRowStatus[m.FullName];
    const statusCell=st?renderRowStatus(st):pill;
    /* Patcher-only mods are not update-tracked (the install path is plugins-oriented), so the
       possibly-outdated hint stays blank for them. */
    const po=(m.possiblyOutdated&&!m.IsPatcher)
      ?`<td class="mod-po" style="color:var(--amber)" title="${esc(T("mods.col.possibly_outdated.tip"))}">${esc(T("mods.possibly_outdated.yes"))}</td>`
      :`<td class="mod-po"></td>`;
    const patcherTag=m.IsPatcher
      ?` <span class="mono" style="font-size:10px;color:var(--bone-faint)">${esc(T("mods.tag.patcher"))}</span>`:"";
    /* A copy the host took from the second site says so, and keeps saying so whatever
       happens to the switch afterwards: it is why this row sits out Update all. */
    const srcChip=(m.installedSource==="hexium")
      ?` <span class="modchip" title="${esc(T("mods.tag.hexium.tip"))}">${esc(T("mods.tag.hexium"))}</span>`:"";
    /* One mark at most on the Latest cell. Either the other site is ahead of both what
       is installed and what Thunderstore has, or this is a Hexium copy Thunderstore has
       moved past. Both are offers, and both ask before they do anything. */
    const mark=modLatestMark(m);
    return `<tr data-key="${esc(m.FullName)}"><td><strong>${esc(m.ModName)}</strong> <span class="mono" style="font-size:10px;color:var(--bone-faint)">${esc(m.Author)}</span>${patcherTag}${srcChip}</td>`+
      `<td class="mono">${esc(m.InstalledVersion||"-")}</td>`+
      `<td class="mono"${has?' style="color:var(--amber)"':""}${m.notListed?` title="${esc(T("mods.status.not_listed.tip"))}"`:""}>${esc(m.LatestVersion||"-")}${mark}</td>`+
      `<td>${statusCell}</td>`+
      po+`</tr>`;
  }).join("")||`<tr><td colspan="5">${mods.length
    ?emptyState({mark:"ᚱ",title:T("mods.empty.no_match.title"),
        /* The one reason that counts what it is hiding. The count is a slot and the
           sentence is a plural entry, so a language that inflects around the number
           has both halves to work with. */
        reason:T("mods.empty.no_match.reason",{count:mods.length}),
        action:{name:"clearModSearch",label:T("mods.empty.no_match.action")}})
    /* Two different nothings. With no loader installed the hall is not empty because
       nobody added a mod, it is empty because nothing could load one, and the button
       that fills it is a different button. Unknown stays on the old wording: the row
       above says what is known, and a hall must not claim a loader is missing on an
       install nothing has looked at. */
    :(bepInExMissing()
      ?emptyState({mark:"ᛒ",title:T("mods.empty.no_bepinex.title"),
          reason:T("mods.empty.no_bepinex.reason"),
          action:{name:"installBepInEx",label:T("mods.empty.no_bepinex.action")}})
      :emptyState({mark:"ᚱ",title:T("mods.empty.none.title"),
          reason:T("mods.empty.none.reason"),
          action:{name:"addMod",label:T("mods.empty.none.action")}}))}</td></tr>`;
  esWire($("#modTable"));
  $("#modTable")._list=mods;
  /* A real plural rather than an English "s", and both pills escaped on the way in. */
  $("#modUpdWrap").innerHTML=upd.length
    ?`<span class="pill amber">${esc(T("mods.updates_waiting",{count:upd.length}))}</span>`
    :`<span class="pill green">${esc(T("mods.up_to_date"))}</span>`;
  /* waiting mod updates are a standing condition, not a toast that repeats forever */
  conditionModUpdates(upd.length);
  renderModIndexLine(mods.length);
  renderModShowing(shown.length,mods.length);
}
/* "showing N of M", and only while something is typed. It reads the two lengths the
   render already has: the whole list, and the rows that were drawn. */
function renderModShowing(shown,total){
  const el=$("#modShowing"); if(!el) return;
  const on=searchTokens(S.modFilter).length>0;
  el.textContent=on?T("common.showing",{shown:shown,total:total}):"";
  /* With the table narrowed, Update all is the one button whose reach is wider than
     what is on screen, so it says so on hover. */
  const btn=$("#updAllBtn");
  if(btn) btn.title=on?T("mods.showing.update_all.title"):"";
}
/* ---------- BEPINEX, THE LOADER EVERY MOD RUNS UNDER ----------
   One row above the mods table, and it is not a mod row. BepInEx has no folder under
   plugins, no package on the list a scan reads and no PluginDirectory to replace, so it
   is keyed "bepinex", built from its own DTO and drawn OUTSIDE #modTable. Nothing here
   ever reaches sortedMods(), Update all or the waiting-updates pill: all three count
   S.mods and only S.mods, and the loader is not in it.
   It is not per profile either. Every server provisioned from one install loads the same
   BepInEx through directory junctions and hard links, so this row says one thing for the
   whole install and every profile's Mods hall shows the same answer. That is also why a
   write is refused while any server on the install is up, and why the row names them. */
const BEPINEX_DEFAULT_URL="https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/";
let BEP_WRITING=false;         // a write this window started
let BEP_PROGRESS=null;         // the last bepinex.progress of that write
let BEP_PROG_OPEN=false;       // and whether the dialog showing it is ours to close
let BEP_ADD_IN_FLIGHT=false;   // an add the host may install the loader inside of
let BEP_ASKED_THIS_RUN=false;  // the first-start question, put once and not again
/* What is typed into the loader dialog's address box, kept out of the element on purpose:
   a language switch rebuilds the dialog from its thunk and a value left in the input
   would be rebuilt away. Delegated on the background, which is the one node a rebuild
   does not replace. */
let BEP_URL_TYPED=null;
$("#modalBg").addEventListener("input",e=>{
  if(e.target&&e.target.id==="mBepUrl") BEP_URL_TYPED=e.target.value;
});
/* Which of the three states the row is in. "outside" is an install BakaLoader did not
   write: there is no note beside it, so what is on show is the loader's own file version
   and a pack bump cannot be seen from here at all. */
function bepInExState(b){
  if(!b) return null;
  if(!b.installed) return "missing";
  return b.maintainedByBakaLoader?"maintained":"outside";
}
/* True only once the host has actually answered and said no loader is there. Unknown is
   not missing: the empty state and the add flow both read this and neither may claim a
   thing about an install nothing has looked at yet. */
function bepInExMissing(){return !!S.bepinex&&!S.bepinex.installed;}
/* The servers that share this install and are up right now. A write would be refused. */
function bepInExRunning(){
  const b=S.bepinex;
  return (b&&Array.isArray(b.runningProfiles))?b.runningProfiles.filter(Boolean):[];
}
function renderBepInExRow(){
  const el=$("#bepinexRow"); if(!el) return;
  const b=S.bepinex;
  const state=bepInExState(b);
  /* Nothing has been read yet, or this host predates the answer. A strip that says
     nothing is worse than no strip at all. */
  if(!state){el.style.display="none";el.innerHTML="";return;}
  const busy=!!b.busy||BEP_WRITING;
  const up=bepInExRunning();
  /* Every button here writes the loader, and the host refuses every one of them while a
     server on this install is up. Refusing it here as well, out loud, is the difference
     between a button that explains itself and a button that fails. */
  const stop=busy||up.length>0;
  const why=busy?T("bepinex.row.note.busy"):T("bepinex.row.blocked.title");
  const gate=stop?` disabled title="${esc(why)}"`:"";
  const version=state==="maintained"?b.packVersion:b.coreFileVersion;
  const verHtml=state==="missing"?""
    :`<span class="bepver mono" title="${esc(state==="maintained"
        ?T("bepinex.row.version.pack.title"):T("bepinex.row.version.file.title"))}">`+
      `${esc(version||T("bepinex.row.version.unknown"))}</span>`;
  let cls,pill,msg,acts="";
  if(state==="missing"){
    cls="grey"; pill=T("bepinex.row.pill.missing"); msg=T("bepinex.row.state.missing");
    acts=`<button class="btn btn-ember btn-sm" id="bepInstall"${gate}>${esc(T("bepinex.row.action.install"))}</button>`;
  }else if(state==="maintained"){
    /* "maintained" is only the marker on disk: it says BakaLoader wrote this pack, not that
       it is still watching it. Whether it watches is the Upkeep switch, and the pill has to
       answer the same question as the sentence beside it. A green "Looked after" over
       "BakaLoader put this pack in and is not looking after it" is the row arguing with
       itself, so the switch-off case gets a wording of its own.
       Looked after also means the next restart window moves it, so there is nothing to
       press; with the switch off the same install keeps the button, and that is the whole
       of what the switch changes about this hall. */
    if(b.maintained){
      cls="green"; pill=T("bepinex.row.pill.maintained"); msg=T("bepinex.row.state.maintained");
    }else{
      cls="blue"; pill=T("bepinex.row.pill.manual"); msg=T("bepinex.row.state.manual");
      acts=`<button class="btn btn-ghost btn-sm" id="bepUpdate"${gate}>${esc(T("bepinex.row.action.update"))}</button>`;
    }
  }else{
    cls="amber"; pill=T("bepinex.row.pill.outside"); msg=T("bepinex.row.state.outside");
    acts=`<button class="btn btn-ghost btn-sm" id="bepUpdate"${gate}>${esc(T("bepinex.row.action.update"))}</button>`;
  }
  /* The notes under the row, each a whole sentence of its own. A deferred update is one
     of them rather than a state: it is true of an install that is otherwise perfectly
     current, and the condition bar says the same thing where a host is not on this hall. */
  const notes=[];
  if(state==="maintained"&&b.maintained) notes.push(T("bepinex.row.note.next_check"));
  if(b.updateWaiting) notes.push(T("bepinex.row.note.waiting",{version:b.updateWaiting}));
  const shared=(Array.isArray(b.sharingProfiles)?b.sharingProfiles.filter(Boolean):[]).length;
  if(shared>1) notes.push(T("bepinex.row.note.shared",{count:shared-1}));
  if(busy) notes.push(T("bepinex.row.note.busy"));
  /* A pack unpacked under plugins is a wrong install rather than a state of the right
     one, so it gets its own line and its own offer. */
  const wrong=b.wrongLocationFolder
    ?`<div class="bepwrong"><span class="bepmsg">${esc(T("bepinex.row.note.wrong_folder"))}</span>`+
     `<span class="mono bepver" title="${esc(b.wrongLocationFolder)}">${esc(b.wrongLocationFolder)}</span>`+
     `<button class="btn btn-ghost btn-sm" id="bepRemove"${gate}>${esc(T("bepinex.row.action.remove"))}</button></div>`
    :"";
  el.style.display="block";
  el.innerHTML=
    `<div class="beprow" data-key="bepinex" data-state="${esc(state)}">`+
    `<span class="r bepmark" aria-hidden="true">ᛒ</span>`+
    `<span class="bepname">${esc(T("bepinex.row.title"))}</span>`+
    verHtml+
    `<span class="pill ${cls}">${esc(pill)}</span>`+
    `<span class="bepmsg">${esc(msg)}</span>`+
    `<span class="bepacts">${acts}</span>`+
    `</div>`+
    (notes.length?`<div class="bepnotes">${notes.map(n=>`<div>${esc(n)}</div>`).join("")}</div>`:"")+
    wrong;
  el.querySelector("#bepInstall")?.addEventListener("click",()=>bepInExInstallFlow(null));
  el.querySelector("#bepUpdate")?.addEventListener("click",()=>bepInExUpdateFlow());
  el.querySelector("#bepRemove")?.addEventListener("click",()=>bepInExRemoveFlow());
}
/* The row's own Install. With BakaLoader looking after the loader there is nothing to
   choose and the pinned pack goes straight in; with the switch off the host is asked
   which address to fetch from, because that is exactly what the switch hands back. */
function bepInExInstallFlow(after){
  if(S.bepinex&&S.bepinex.maintained){bepInExWrite("bepinex.install",{},after);return;}
  bepInExOfferModal(after);
}
function bepInExUpdateFlow(){bepInExWrite("bepinex.update",{},null);}
function bepInExRemoveFlow(){
  confirmModal(()=>T("bepinex.dialog.remove.title"),
    ()=>`<div class="mbody-note">${esc(T("bepinex.dialog.remove.body"))}</div>`+
      `<div class="subval mono" style="margin-top:8px;word-break:break-all">`+
      `${esc((S.bepinex&&S.bepinex.wrongLocationFolder)||"")}</div>`,
    ()=>T("bepinex.dialog.remove.ok"),
    ()=>bepInExWrite("bepinex.remove",{},null));
}
/* The question the add flow asks with the switch off, and the one the row's own Install
   asks for the same reason. The address is shown and editable: the pinned pack is the
   default because it is the one shape BakaLoader knows, and anything else is checked for
   that shape on the host side before a byte is written. An untouched box sends no address
   at all, so the pinned pack is resolved fresh rather than pinned to a version here. */
function bepInExOfferModal(after){
  BEP_URL_TYPED=null;
  const up=()=>bepInExRunning();
  confirmModal(()=>T("bepinex.dialog.install.title"),
    ()=>`<div class="mbody-note">${esc(T("bepinex.dialog.install.body"))}</div>`+
      `<div class="mbody-note" style="margin-top:10px">${esc(T("bepinex.dialog.install.source"))}</div>`+
      `<input type="text" id="mBepUrl" value="${esc(BEP_URL_TYPED==null?BEPINEX_DEFAULT_URL:BEP_URL_TYPED)}" spellcheck="false" autocomplete="off" style="margin-top:6px">`+
      `<div class="mbody-note" style="margin-top:8px">${esc(T("bepinex.dialog.install.note"))}</div>`+
      (up().length?`<div class="mwarn">⚠ ${esc(T("bepinex.dialog.install.running.warn",{names:up().join(", ")}))}</div>`:""),
    ()=>T("bepinex.dialog.install.ok"),m=>{
      const link=(m.querySelector("#mBepUrl")?.value||"").trim();
      if(!link){toast("ᚦ "+T("bepinex.dialog.install.no_link.toast"));return;}
      bepInExWrite("bepinex.install",link===BEPINEX_DEFAULT_URL?{}:{url:link},after);
    });
  const inp=document.querySelector("#mBepUrl");
  if(inp) setTimeout(()=>{inp.focus();inp.select();},40);
}
/* The one path every host-driven write goes through, so the row's buttons, the add
   flow's question and the empty state cannot drift apart. The answer IS the new status,
   so nothing asks again afterwards, and `after` is the add that was waiting on it. */
async function bepInExWrite(method,params,after){
  if(BEP_WRITING) return;
  BEP_WRITING=true;
  BEP_PROGRESS={phase:"resolving",percent:0,version:null};
  renderBepInExProgress();
  renderBepInExRow();
  const r=await rpc(method,params||{});
  BEP_WRITING=false;
  bepInExProgressDone();
  if(r===FAIL){renderBepInExRow();return;}
  S.bepinex=r;
  renderMods();
  if(method==="bepinex.remove"){
    toast("ᛒ "+T("bepinex.row.removed.toast"));
    logLine("ok","[BepInEx] the misplaced pack folder under plugins was removed");
  }else{
    const v=r.packVersion||r.coreFileVersion;
    toast("ᛒ "+(v?T("bepinex.row.installed.toast",{version:v})
                      :T("bepinex.row.installed.noversion.toast")));
    logLine("ok","[BepInEx] "+(method==="bepinex.update"?"updated":"installed")+
      (r.packVersion?" · pack "+r.packVersion:""));
  }
  if(typeof after==="function") after();
}
/* The write's own progress, in the dialog layer where the question was asked. The bar is
   the one the server update and the bulk mod update already draw, so a host who has seen
   one has seen all three. It only ever opens for a write this window is part of: the
   unattended path posts the same event, and a dialog opening by itself while nobody is
   at the keyboard would be a fright rather than a report. */
function renderBepInExProgress(){
  if(!BEP_PROGRESS||!(BEP_WRITING||BEP_ADD_IN_FLIGHT)) return;
  const p=BEP_PROGRESS;
  const n=Number(p.percent);
  const det=isFinite(n)&&n>=0;
  const pct=det?Math.max(0,Math.min(100,Math.round(n))):0;
  BEP_PROG_OPEN=true;
  modalOpen(
    `<div class="mtitle">${esc(T("bepinex.progress.title"))}</div>`+
    `<div class="mbody"><div class="modprog" style="display:flex;margin:0">`+
    `<span class="hbmsg">${esc(bepInExPhaseText(p))}</span>`+
    `<span class="hbprog${det?"":" indet"}" role="progressbar" aria-label="${esc(T("bepinex.progress.aria"))}" aria-valuemin="0" aria-valuemax="100"`+
    (det?` aria-valuenow="${pct}" title="${pct}%"`:` title="${esc(T("bepinex.progress.phase.working"))}"`)+
    `><i${det?` style="width:${pct}%"`:""}></i></span>`+
    (det?`<span class="hbpct">${pct}%</span>`:"")+
    `</div></div>`,
    renderBepInExProgress);
}
/* One whole sentence per phase rather than a stem and an ending: a language that puts the
   thing before what is being done to it has nowhere to put "downloading". */
function bepInExPhaseText(p){
  const k=String((p&&p.phase)||"");
  if(k==="resolving") return T("bepinex.progress.phase.resolving");
  if(k==="downloading") return p.version
    ?T("bepinex.progress.phase.downloading.version",{version:p.version})
    :T("bepinex.progress.phase.downloading");
  if(k==="checking") return T("bepinex.progress.phase.checking");
  if(k==="extracting") return T("bepinex.progress.phase.extracting");
  if(k==="installing") return T("bepinex.progress.phase.installing");
  if(k==="linking") return T("bepinex.progress.phase.linking");
  if(k==="done") return T("bepinex.progress.phase.done");
  return T("bepinex.progress.phase.working");
}
/* One inbound bepinex.progress event, as a named handler so a walk drives exactly what
   the native host drives. */
function onBepInExProgress(d){
  if(!d) return;
  BEP_PROGRESS=d;
  renderBepInExProgress();
}
/* The write is over. The dialog goes only if it was this one that put it up. */
function bepInExProgressDone(){
  BEP_PROGRESS=null;
  if(!BEP_PROG_OPEN) return;
  BEP_PROG_OPEN=false;
  modalClose();
}
/* The row's fact, asked once at boot and pushed by the host after that. */
async function refreshBepInEx(){
  if(!Native.available) return null;
  const r=await rpc("bepinex.status");
  if(r===FAIL||!r) return null;
  S.bepinex=r;
  renderMods();
  return r;
}
/* ---- the question asked once, on the first Start ----
   D11.6: a start is what loads BepInEx, so it is the first moment the answer changes
   anything. Both buttons write both preferences in one save, so a window closed on the
   way past cannot leave the question answered with nothing chosen. Dismissed rather than
   answered, nothing is written and nothing starts, exactly as the launch guard behaves;
   it is not put again in this run, so the next press of Start simply starts. */
function bepInExFirstStartModal(onDone){
  const answer=yes=>{
    modalClose();
    rpc("userprefs.save",{prefs:{BepInExMaintained:yes,BepInExMaintenanceAsked:true}}).then(r=>{
      if(r===FAIL) return;
      try{setT("tBepMaint",yes);}catch(_){}
      if(S.bepinex){S.bepinex.maintained=yes;S.bepinex.maintenanceAsked=true;renderMods();}
      toast("ᛒ "+(yes?T("bepinex.dialog.first.yes.toast"):T("bepinex.dialog.first.no.toast")));
      logLine("info","[BepInEx] maintenance "+(yes?"on":"off")+", asked at the first start");
    });
    if(typeof onDone==="function") onDone();
  };
  const m=modalOpen(
    `<div class="mtitle">${esc(T("bepinex.dialog.first.title"))}</div>`+
    `<div class="mbody"><div style="margin-bottom:8px">${esc(T("bepinex.dialog.first.body"))}</div>`+
    `<div class="subval">${esc(T("bepinex.dialog.first.note"))}</div></div>`+
    `<div class="mbtns">`+
    `<button class="btn btn-ember btn-sm" id="mBepYes">${esc(T("bepinex.dialog.first.yes"))}</button>`+
    `<button class="btn btn-ghost btn-sm" id="mBepNo">${esc(T("bepinex.dialog.first.no"))}</button>`+
    `</div>`,()=>bepInExFirstStartModal(onDone));
  m.querySelector("#mBepYes").addEventListener("click",()=>answer(true));
  m.querySelector("#mBepNo").addEventListener("click",()=>answer(false));
  /* "Yes, look after it" is the default, so it is the one the keyboard lands on. */
  setTimeout(()=>{try{m.querySelector("#mBepYes").focus();}catch(_){}},40);
  return m;
}
/* The gate in front of a start. A host that predates the setting, or a status that has
   not come back yet, is never put a question there is no truthful wording for. */
function bepInExAskOnce(next){
  const b=S.bepinex;
  if(!b||b.maintenanceAsked||BEP_ASKED_THIS_RUN){next();return;}
  BEP_ASKED_THIS_RUN=true;
  /* The host pressed Start. This is a question in front of that press, not a gate on it,
     so waving it away with Escape or a click on the backdrop leaves the setting exactly
     as it was and the start carries on. Dropping the start there reads as "Start did
     nothing", which is the one answer a button must never give. Answered or dismissed,
     the start happens once: the modal's own answer runs this first, and the dismissal
     watcher finds it already spent. */
  let went=false;
  const go=()=>{if(went)return;went=true;next();};
  bepInExFirstStartModal(go);
  onModalDismissed(go);
}
/* A scan always asks the sites again rather than reading whatever BakaLoader was holding.
   It used to hold a package list for fifteen minutes, so a scan a minute after a release
   found nothing and a host watching the mod's own page saw a version BakaLoader would not
   admit to. Each client keeps its own short cooldown, so two presses in a row are still
   one trip out. */
async function scanMods(){
  if(!Native.available||S.modsScanning||S.modsUpdating) return;
  S.modsScanning=true; renderMods();
  toast("ᛋ "+T("mods.scan.begun.toast"));
  logLine("info","[Thunderstore] reading the community listing index…");
  const r=await rpc("mods.scan",{force:true});
  S.modsScanning=false;
  /* The bridge answers with the rows and what it read them from. A plain array is still
     accepted so nothing here depends on the two shipping in lockstep. */
  const rows=Array.isArray(r)?r:(r&&r!==FAIL&&Array.isArray(r.mods)?r.mods:null);
  if(r===FAIL||!rows){renderMods();return;}
  const idx=(r&&!Array.isArray(r))?r.index:null;
  S.mods=rows; S.modsScanned=true; S.lastScan=clock();
  /* The time the LIST was read, which a press that reused a fresh one leaves where it
     was. An older bridge answering with a bare array has no such time, and the press
     time is the best that is known there. */
  S.modIndexAt=idx?hhmm(idx.fetchedUtc):S.lastScan;
  S.modIndexSource=(idx&&idx.source)||null;
  renderMods();
  const u=rows.filter(m=>m.UpdateAvailable).length;
  toast("ᛋ "+T("mods.scan.done.toast",{count:rows.length,updates:u}));
}
async function doUpdateAll(){
  if(S.modsUpdating||S.modsScanning) return;
  S.modsUpdating=true;
  /* Seed every mod that has an update as queued, so a row can flip queued -> updating ->
     done as the progress events arrive. The bar opens on the indeterminate check. */
  S.modRowStatus={};
  (S.mods||[]).filter(m=>m.UpdateAvailable).forEach(m=>{S.modRowStatus[m.FullName]={phase:"queued"};});
  MOD_UPD={phase:"checking",total:0,done:0,current:null,index:0};
  renderModUpdateProgress(); renderMods();
  toast("ᚱ "+T("mods.update_all.begun.toast"));
  const r=await rpc("mods.updateAll");
  S.modsUpdating=false;
  /* The run is over: drop the transient bar and row states so the fresh scan renders the
     resting table, not leftover queued rows. */
  MOD_UPD=null; S.modRowStatus={}; renderModUpdateProgress();
  if(r===FAIL||!Array.isArray(r)){renderMods();return;}
  const okN=r.filter(x=>x.Updated).length, errN=r.filter(x=>x.Error).length;
  r.forEach(x=>logLine(x.Updated?"ok":"warn","[Thunderstore] "+x.mod+" "+(x.Updated?(x.FromVersion+" → "+x.ToVersion):("failed: "+(x.Error||"unknown")))));
  /* Two whole sentences, each a plural on the count that leads it. The failing one used
     to say "1 mods" the moment a single update went wrong. */
  toast(errN
    ?"ᚦ "+T("mods.update_all.failed.toast",{ok:okN,failed:errN})
    :"ᚱ "+T("mods.update_all.done.toast",{count:okN}));
  await scanMods(); // re-render from a fresh scan
}
$("#updAllBtn").addEventListener("click",()=>{
  if(Native.available){
    const upd=(S.mods||[]).filter(m=>m.UpdateAvailable);
    if(!upd.length) return;
    /* NOTE: mods.updateAll in the bridge does NOT stop/restart the server - reflect that. */
    const runWarn=()=>(S.state?.status==="Running")
      ?`<div class="mwarn">⚠ ${esc(T("mods.update_all.running.warn"))}</div>`:"";
    confirmModal(()=>T("mods.update_all.confirm.title",{count:upd.length}),
      ()=>`<div class="mono-list">${upd.map(m=>esc(m.FullName)+"  "+esc(m.InstalledVersion)+" → "+esc(m.LatestVersion)).join("<br>")}</div>`+runWarn(),
      ()=>T("mods.update_all.confirm.ok"),()=>doUpdateAll());
    return;
  }
  toast("ᚱ "+T("mods.update_all.preview.toast",
    {count:2,names:"WorldEditCommands, ExtraSlots"}));   // preview fixture
  logLine("info","[Thunderstore] downloading WorldEditCommands 1.66.0 …");
});
$("#scanBtn").addEventListener("click",()=>{
  if(Native.available){scanMods();return;}
  toast("ᛋ "+T("mods.scan.begun.toast"));
  logLine("info","[Thunderstore] reading the community listing index…");
});
/* ---- The Mods search box ----
   The typed value is held in S, so a scan, an update or any other re-render leaves it
   where it was. Escape empties it, and the "/" key anywhere on the Mods hall puts the
   cursor in it, the way search works nearly everywhere else. */
function setModFilter(value){
  S.modFilter=String(value||"");
  const box=$("#modSearch");
  if(box&&box.value!==S.modFilter) box.value=S.modFilter;
  renderMods();
}
$("#modSearch")?.addEventListener("input",e=>setModFilter(e.target.value));
$("#modSearch")?.addEventListener("keydown",e=>{
  if(e.key!=="Escape") return;
  e.preventDefault(); e.stopPropagation();
  setModFilter("");
});
/* Opens the mod's own page on Thunderstore in the host's browser. The scan is what
   pairs a plugin with a page, so a hand-dropped or bundled plugin has none and the
   menu says that rather than opening nothing. */
function openThunderstorePage(mod){
  const namespace=mod&&mod.thunderstoreNamespace, name=mod&&mod.thunderstoreName;
  if(!namespace||!name) return;
  Native.call("shell.openThunderstore",{namespace,name})
    .catch(err=>toast("ᚦ "+T("mods.page.thunderstore.failed.toast",
      {detail:(err&&err.message)||T("common.error.unknown")})));
}
/* Opens the mod's page on Hexium in the host's browser. The app builds the address
   from the two halves of the identity the scan matched, so this only ever lands on
   Hexium; a mod the site does not carry has no page and the menu says so. */
function openHexiumPage(mod){
  if(!mod||!mod.hexiumUrl) return;
  Native.call("shell.openHexium",{owner:readHexiumOwner(mod),name:readHexiumName(mod)})
    .catch(err=>toast("ᚦ "+T("mods.page.hexium.failed.toast",
      {detail:(err&&err.message)||T("common.error.unknown")})));
}
/* The scan hands back the built page address rather than the two halves, so the halves
   are read back off it when the menu needs them. The app built that address from
   segments it had already checked, and it checks them again before it opens anything. */
function readHexiumOwner(mod){const p=String(mod.hexiumUrl||"").split("/");return p.length>=2?p[p.length-2]:"";}
function readHexiumName(mod){const p=String(mod.hexiumUrl||"").split("/");return p.length>=1?p[p.length-1]:"";}
/* right-click a mod row → its page on either site, the swap offers, or remove */
function modRowItems(mod){
  const onStore=!!(mod.thunderstoreNamespace&&mod.thunderstoreName);
  const onHexium=!!mod.hexiumUrl;
  const isPatcher=!!mod.IsPatcher;
  const canUpd=!!mod.UpdateAvailable&&!isPatcher;
  const canCheck=!mod.Bundled&&!!(mod.Author&&mod.ModName);
  const items=[
    /* The greyed tip says which of the reasons it is. "Already up to date" is only one of
       them, and it is the wrong one for a row whose Latest nobody knows: a mod the list
       came back without has nothing to be up to date with. */
    {r:"ᚱ",label:T("mods.menu.update"),disabled:!canUpd,
      tip:isPatcher?T("mods.menu.update.tip.patcher")
        :(mod.installedSource==="hexium"?T("mods.menu.update.tip.hexium")
        :(mod.notListed?T("mods.menu.update.tip.not_listed")
        :T("mods.menu.update.tip.current"))),
      fn:()=>doUpdateOne(mod)},
    /* One question to Thunderstore about this one mod, for a host looking at the mod's own
       page and knowing there is something newer than the row says. It asks the address the
       page itself reads, so it is never behind; it changes what the row shows and nothing
       else. A bundled plugin has no package to ask about. */
    {r:"ᛋ",label:T("mods.menu.check_one"),disabled:!canCheck,
      tip:mod.Bundled?T("mods.menu.check_one.tip.bundled")
        :T("mods.menu.check_one.tip.no_package"),
      fn:()=>checkOneNow(mod)},
    {r:"ᛋ",label:T("mods.menu.thunderstore"),disabled:!onStore,tip:T("mods.menu.thunderstore.tip.missing"),
      fn:()=>openThunderstorePage(mod)},
  ];
  /* Left out entirely rather than greyed: a host who never turned the second site on
     should not read its name anywhere but on the switch that turns it on. */
  if(onHexium)
    items.push({r:"ᚺ",label:T("mods.menu.hexium"),fn:()=>openHexiumPage(mod)});
  /* Each swap is its own deliberate act, and each one asks before it fetches anything. */
  if(mod.hexiumNewer&&mod.hexiumLatest)
    items.push({r:"ᚺ",label:T("mods.menu.install_hexium"),fn:()=>hexiumInstallFlow(mod)});
  if(mod.thunderstoreNewer&&mod.LatestVersion)
    items.push({r:"ᛋ",label:T("mods.menu.install_thunderstore"),fn:()=>thunderstoreSwapFlow(mod)});
  items.push("hr");
  items.push({r:"ᛪ",label:T("mods.menu.remove"),danger:true,fn:()=>removeModFlow(mod)});
  return items;
}
/* What one check's answer does to the row it was asked about, kept apart from the toast
   so the row's state is one readable rule.
   A reply that found the package writes the numbers it came back with. A reply that says
   the list no longer holds the package leaves those numbers exactly where they are and
   takes the UPDATE away, which is the shape a scan leaves a row in when a fresh list came
   back without it. Without that second half a row could carry a "not listed" pill and an
   Update offer at the same moment, which is two answers to one question, and Update all
   would go on counting it.
   A reply that found nothing and is not saying the package was pulled is the third case:
   nobody could be asked, so the row is left alone rather than told something wrong. */
function applyCheckOneToRow(row,r){
  if(!row||!r) return row;
  row.notListed=!!r.notListed;
  if(r.found){
    row.LatestVersion=r.latestVersion;
    row.modUpdatedUtc=r.latestReleasedUtc||row.modUpdatedUtc;
    row.UpdateAvailable=!!r.updateAvailable;
    /* A row the list came back without could not open its page, because the scan had
       no identity to build one from. This answer has one, so the page opens again
       without waiting for the next scan. */
    if(r.thunderstoreNamespace) row.thunderstoreNamespace=r.thunderstoreNamespace;
    if(r.thunderstoreName) row.thunderstoreName=r.thunderstoreName;
    /* A Hexium copy Thunderstore has moved past: the row keeps saying held, and the
       swap back stays on offer. */
    row.thunderstoreNewer=!!r.thunderstoreNewer;
  }else if(row.notListed){
    /* Nothing is removed and nothing is rolled back to say this: the version last seen
       stays on the row, and only the offer to update to it goes. */
    row.UpdateAvailable=false;
  }
  return row;
}
/* Asks Thunderstore about one mod, right now. This is the answer the mod's own page shows,
   so it settles the "the site says 2.0.5 and BakaLoader says 2.0.3" argument on the spot.
   It updates that one row and says what it found in plain words. Nothing is downloaded. */
async function checkOneNow(mod){
  const r=await rpc("mods.checkOne",{author:mod.Author,name:mod.ModName});
  if(r===FAIL||!r) return;
  if(r.reason==="cooldown"){toast("ᛋ "+T("mods.check_one.cooldown.toast"));return;}
  if(r.reason==="badName"){toast("ᚦ "+T("mods.check_one.bad_name.toast"));return;}
  /* The site not answering says nothing about the mod, so the row is left where it was
     rather than being told its package has been pulled. */
  if(r.reason==="unreachable"){toast("ᚦ "+T("mods.check_one.unreachable.toast"));return;}
  const row=modByKey(mod.FullName);
  if(row){
    applyCheckOneToRow(row,r);
    renderMods();
  }
  if(!r.found){toast("ᛋ "+T("mods.check_one.not_listed.toast",{name:mod.ModName}));return;}
  /* Three answers, not two. A copy that came from Hexium is left alone whatever
     Thunderstore has, so telling its host they have the newest would be untrue. */
  if(r.held&&r.thunderstoreNewer){
    toast("ᛋ "+T("mods.check_one.held.toast",{version:r.latestVersion}));
    return;
  }
  toast("ᛋ "+(r.updateAvailable
    ?T("mods.check_one.newer.toast",{version:r.latestVersion})
    :T("mods.check_one.current.toast")));
}
/* Update a single mod from its row menu. The bridge streams the same mods.updateProgress
   events the bulk path uses, so the row flips updating -> done in place; no server is
   stopped or restarted, so a new version only loads on the next restart. */
async function doUpdateOne(mod){
  if(S.modsUpdating||S.modsScanning) return;
  S.modsUpdating=true;
  S.modRowStatus={}; S.modRowStatus[mod.FullName]={phase:"queued"};
  renderMods();
  toast("ᚱ "+T("mods.update_one.begun.toast",{mod:mod.FullName}));
  const r=await rpc("mods.update",{fullName:mod.FullName});
  S.modsUpdating=false;
  MOD_UPD=null; S.modRowStatus={}; renderModUpdateProgress();
  if(r===FAIL||!r){renderMods();return;}
  logLine(r.Updated?"ok":"warn","[Thunderstore] "+r.mod+" "+(r.Updated?(r.FromVersion+" → "+r.ToVersion):("failed: "+(r.Error||"unknown"))));
  /* Three answers, three whole sentences, each naming the mod in a slot. */
  toast(r.Updated?("ᚱ "+T("mods.update_one.done.toast",{mod:r.mod}))
    :(r.Error?("ᚦ "+T("mods.update_one.failed.toast",{mod:r.mod}))
             :("ᚱ "+T("mods.update_one.current.toast",{mod:r.mod}))));
  await scanMods();
}
$("#modTable").addEventListener("contextmenu",e=>{
  if(!Native.available) return;
  const tr=e.target.closest("tr[data-key]"); if(!tr) return;
  e.preventDefault();
  const mod=modByKey(tr.dataset.key); if(!mod) return;
  const x=e.clientX,y=e.clientY;
  const again=()=>ctxOpen(x,y,mod.FullName,modRowItems(mod),again);
  again();
});
async function removeModFlow(mod){
  /* Config files and the mods that depend on this one, both read locally. Dependents come
     back ordered so removing them ahead of the mod never orphans one midway. */
  const [files,deps]=await Promise.all([
    rpc("mods.findConfigs",{fullName:mod.FullName}),
    rpc("mods.dependents",{fullName:mod.FullName}),
  ]);
  if(files===FAIL) return;
  const cfgs=Array.isArray(files)?files:[];
  const dependents=(deps===FAIL||!Array.isArray(deps))?[]:deps;
  const depBlock=()=>dependents.length
    ?`<div class="mbody-note" style="margin-top:10px">${esc(T("mods.remove.dependents.note",{name:mod.ModName}))}</div>`+
     `<label class="mchk"><input type="checkbox" id="mDepAll"> ${esc(T("mods.remove.dependents.also",{count:dependents.length}))}</label>`+
     `<div class="mono-list">${dependents.map(d=>esc(d.displayName||d.fullName)).join("<br>")}</div>`
    :"";
  const body=()=>
    `<div class="mbody-note">${esc(T("mods.remove.backup.note"))}</div>`+
    (cfgs.length
      ?`<label class="mchk"><input type="checkbox" id="mIncCfg" checked> ${esc(T("mods.remove.configs.also",{count:cfgs.length}))}</label><div class="mono-list">${cfgs.map(f=>esc(String(f).split(/[\\/]/).pop())).join("<br>")}</div>`
      :`<div class="mbody-note">${esc(T("mods.remove.configs.none"))}</div>`)+
    depBlock()+
    ((S.state?.status==="Running")
      ?`<div class="mwarn">⚠ ${esc(T("mods.remove.running.warn"))}</div>`:"");
  confirmModal(()=>T("mods.remove.confirm.title",{name:mod.FullName}),body,()=>T("common.button.remove"),m=>{
    const includeConfig=!!m.querySelector("#mIncCfg")?.checked;
    const alsoRemove=!!m.querySelector("#mDepAll")?.checked;
    doRemoveMod(mod,includeConfig,alsoRemove?dependents:[]);
  });
}
async function doRemoveMod(mod,includeConfig,dependents){
  dependents=dependents||[];
  /* Dependents first (they were ordered for it), then the mod itself. Each reuses the
     one-mod remove RPC and carries the same delete-config choice. */
  const targets=dependents.map(d=>d.fullName).concat([mod.FullName]);
  /* One mod is named; several are counted. Two entries rather than one sentence with a
     name or a number dropped into the same hole, which no language can inflect around. */
  toast("ᛪ "+(targets.length>1
    ?T("mods.remove.begun.count.toast",{count:targets.length})
    :T("mods.remove.begun.named.toast",{name:mod.FullName})));
  let okN=0,failN=0,lastErr="";
  for(const fullName of targets){
    const r=await rpc("mods.remove",{fullName,includeConfig});
    if(r===FAIL){failN++;continue;}
    if(r.Removed){
      okN++;
      logLine("warn","[BakaLoader] removed mod "+r.mod+(r.BackupDirectory?" (backup: "+r.BackupDirectory+")":""));
    }else{
      failN++; lastErr=r.Error||"unknown";
      logLine("warn","[BakaLoader] remove failed "+fullName+": "+lastErr);
    }
  }
  /* The detail is its own entry rather than a slot that may be empty: a trailing
     separator with nothing after it reads as a typo, not as a message with no detail. */
  if(failN) toast("ᚦ "+(lastErr
    ?T("mods.remove.failed.detail.toast",{ok:okN,failed:failN,detail:lastErr})
    :T("mods.remove.failed.toast",{ok:okN,failed:failN})));
  else toast("ᛪ "+T("mods.remove.done.toast",{count:okN}));
  scanMods();
}
/* ---- The second mod site: ask first, then fetch ----
   Nothing on this path ever installs on its own. The host is shown exactly what the
   download is, who Hexium says published it, and what BakaLoader cannot tell them, and
   only a deliberate answer moves a byte. The host side refuses the install without the
   one-shot token this dialog hands back. */
function hexiumFailToast(r){
  if(!r) return;
  if(r.Reason==="sourceOff"){
    toast("ᚦ "+T("mods.hexium.source_off.toast"));
    return;
  }
  const why=r.Error||T("common.error.unknown");
  toast("ᚦ "+T("mods.hexium.failed.toast",{detail:why}));
  logLine("warn","[Hexium] "+why);
}
/* The stand-in answer the browser preview feeds the real dialog, so the wording and the
   layout can be walked with no host behind them. The app never reads this. */
const HEXIUM_PREVIEW={Owner:"JereKuusela",Name:"WorldEditCommands",Version:"1.67.0",
  FileSize:1830412,Replacing:true,ReplacingVersion:"1.65.0",
  Dependencies:["denikson-BepInExPack_Valheim-5.4.2350"],Token:"preview"};
function hexiumConsentModal(pay,onAccept){
  const size=(pay.FileSize!=null&&Number(pay.FileSize)>0)?fmtBytes(pay.FileSize):null;
  const deps=Array.isArray(pay.Dependencies)?pay.Dependencies:[];
  const body=()=>
    `<div class="mbody-note">${esc(T("mods.hexium.consent.note"))}</div>`+
    `<div class="mono-list">`+
      `${esc(pay.Owner)} / ${esc(pay.Name)}<br>`+
      `${esc(T("common.label.version"))} ${esc(pay.Version)}`+
      (size?`<br>${esc(T("mods.hexium.consent.download"))} ${esc(size)}`:"")+
    `</div>`+
    (pay.Replacing
      ?`<div class="mwarn">⚠ ${esc(T("mods.hexium.consent.replacing"))}</div>`
      :`<div class="mbody-note">${esc(T("mods.hexium.consent.fresh"))}</div>`)+
    (deps.length
      ?`<div class="mbody-note" style="margin-top:10px">${esc(T("mods.hexium.consent.deps"))}</div>`+
       `<div class="mono-list">${deps.map(d=>esc(d)).join("<br>")}</div>`
      :"")+
    ((S.state?.status==="Running")
      ?`<div class="mwarn">⚠ ${esc(T("mods.hexium.consent.running"))}</div>`:"");
  confirmModal(()=>T("mods.hexium.consent.title"),body,()=>T("mods.hexium.consent.accept"),()=>onAccept());
}
/* Resolves what installing would do, then puts it to the host. A row hands its own
   identity in; a pasted link has already been resolved by the host side. */
async function hexiumInstallFlow(mod){
  if(S.modsUpdating||S.modsScanning) return;
  if(!Native.available){
    /* Browser preview: the real dialog with a stand-in answer, so it can be walked. */
    hexiumConsentModal(HEXIUM_PREVIEW,()=>toast("ᚺ "+T("mods.hexium.preview.toast")));
    return;
  }
  const r=await rpc("mods.hexiumPrepare",{
    owner:readHexiumOwner(mod),name:readHexiumName(mod),version:mod.hexiumLatest||null});
  if(r===FAIL) return;
  if(!r.NeedsConsent){hexiumFailToast(r);return;}
  hexiumConsentModal(r,()=>doInstallFromHexium(r));
}
async function doInstallFromHexium(pay){
  if(S.modsUpdating||S.modsScanning) return;
  S.modsUpdating=true; renderMods();
  toast("ᚺ "+T("mods.hexium.fetching.toast"));
  logLine("info","[Hexium] downloading "+pay.Owner+"-"+pay.Name+" v"+pay.Version);
  const r=await rpc("mods.installFromHexium",
    {owner:pay.Owner,name:pay.Name,version:pay.Version,token:pay.Token});
  S.modsUpdating=false;
  if(r===FAIL){renderMods();return;}
  if(r.Installed){
    const full=r.Owner+"-"+r.Name;
    toast("ᚺ "+(r.Version
      ?T("mods.hexium.installed.toast",{name:full,version:r.Version})
      :T("mods.hexium.installed.noversion.toast",{name:full})));
    logLine("ok","[Hexium] installed "+full+" v"+(r.Version||"?")+(r.Replaced?" (previous copy backed up)":""));
    if(Array.isArray(r.Dependencies)&&r.Dependencies.length)
      logLine("info","[Hexium] "+full+" says it needs: "+r.Dependencies.join(", "));
    if(S.state?.status==="Running") logLine("warn","[Hexium] server is running, so "+full+" loads on the next restart");
    await scanMods();
  }else{
    hexiumFailToast(r);
    renderMods();
  }
}
/* The way back. A Hexium copy is never replaced on its own, so the swap to the
   Thunderstore build is its own action and it asks the same way the other one does. */
function thunderstoreSwapFlow(mod){
  const owner=mod.thunderstoreNamespace, name=mod.thunderstoreName, version=mod.LatestVersion;
  if(!owner||!name||!version) return;
  confirmModal(()=>T("mods.swap.title"),
    ()=>`<div class="mbody-note">${esc(T("mods.swap.body"))}</div>`+
    `<div class="mono-list">${esc(owner)} / ${esc(name)}<br>${esc(T("common.label.version"))} ${esc(version)}</div>`+
    `<div class="mwarn">⚠ ${esc(T("mods.swap.warn"))}</div>`,
    ()=>T("mods.menu.install_thunderstore"),
    ()=>{
      if(!Native.available){toast("ᛋ "+T("mods.add.preview.toast"));return;}
      doAddMod("https://thunderstore.io/c/valheim/p/"+owner+"/"+name+"/v/"+version+"/");
    });
}
/* Clicking a mark on the Latest cell is the same offer the row menu carries. */
$("#modTable").addEventListener("click",e=>{
  const mark=e.target.closest(".modmark"); if(!mark) return;
  e.preventDefault(); e.stopPropagation();
  if(mark.dataset.hex!=null){const m=modByKey(mark.dataset.hex); if(m) hexiumInstallFlow(m); return;}
  if(mark.dataset.ts!=null){const m=modByKey(mark.dataset.ts); if(m) thunderstoreSwapFlow(m);}
});

/* ---- Add a mod from any pasted Thunderstore link ---- */
function addModFlow(){
  if(S.modsUpdating||S.modsScanning) return;
  const runWarn=()=>(S.state?.status==="Running")
    ?`<div class="mwarn">⚠ ${esc(T("mods.add.running.warn"))}</div>`:"";
  /* With the second site switched on, a hexium.gg address is accepted here too. It
     never installs from the paste: it comes back with what the download would be, and
     the host reads that and answers. */
  const hexNote=()=>S.hexium
    ?`<div class="mbody-note" style="margin-top:8px">${esc(T("mods.add.hexium_note"))}</div>`
    :"";
  /* The title and the box follow the same switch the two header buttons do: with the
     second site on, this dialog takes a link from either of them, so it says that rather
     than naming one of the two. */
  const both=!!S.hexium;
  confirmModal(()=>both?T("mods.add.title.link"):T("mods.add.title.store"),
    ()=>`<div class="mbody-note">${esc(T("mods.add.paste.note"))}</div>`+
    `<input type="text" id="mModUrl" placeholder="${esc(both?T("mods.add.placeholder.link"):"https://thunderstore.io/c/valheim/p/Author/ModName/")}" spellcheck="false" autocomplete="off" style="margin-top:10px">`+
    hexNote()+
    runWarn(),
    ()=>T("common.button.install"),m=>{
      const url=(m.querySelector("#mModUrl")?.value||"").trim();
      if(!url){toast("ᚦ "+T("mods.add.no_link.toast"));return;}
      doAddMod(url);
    });
  const inp=document.querySelector("#mModUrl");
  if(inp){
    setTimeout(()=>inp.focus(),40);
    inp.addEventListener("keydown",e=>{if(e.key==="Enter")document.querySelector("#mOk")?.click();});
  }
}
async function doAddMod(url){
  if(S.modsUpdating||S.modsScanning) return;
  S.modsUpdating=true; renderMods();
  toast("ᚨ "+T("mods.add.fetching.toast"));
  logLine("info","[Thunderstore] resolving pasted link: "+url);
  /* Anything the host installs on the way through this one call reports progress on
     its own event, and the bar that shows it is opened by the first event that
     arrives rather than guessed at from here. */
  BEP_ADD_IN_FLIGHT=true;
  const r=await rpc("mods.addFromUrl",{url});
  BEP_ADD_IN_FLIGHT=false;
  bepInExProgressDone();
  S.modsUpdating=false;
  if(r===FAIL){renderMods();return;}
  /* A hexium.gg address stops here on purpose: the host side resolved it and handed
     back what it is, and nothing is fetched until the host reads that and says yes. */
  if(r.NeedsConsent){
    renderMods();
    hexiumConsentModal(r,()=>doInstallFromHexium(r));
    return;
  }
  /* The loader is not a mod, and the three answers that say so are read before
     anything about the plugins folder, because two of them never touched it.
     alreadyMaintained is a notice rather than a failure: the pasted link was the
     pack's own while BakaLoader is looking after it, so the row says where the switch
     is. "bepinex" means that link WAS the pack and it went in, which only happens
     with the switch off. noBepInEx only ever comes back with the switch off as well:
     with it on the host side installs first and finishes this same add itself, which
     is why neither road has a try-that-again step. */
  if(r.Reason==="alreadyMaintained"){renderMods();noticeBepInExMaintained();return;}
  if(r.Reason==="bepinex"){
    toast("ᛒ "+T("bepinex.add.pack_installed.toast"));
    logLine("ok","[BepInEx] the pasted link was the loader pack, and it was installed");
    await refreshBepInEx();
    await scanMods();
    return;
  }
  if(r.Reason==="noBepInEx"){
    renderMods();
    /* The pasted address is held right here and the same add is finished with it the
       moment the loader is in, so the host never types it twice. */
    bepInExOfferModal(()=>doAddMod(url));
    return;
  }
  /* Which site an answer came from. The two Hexium calls spell it Source and the
     Thunderstore add spells it source, so the page reads either one rather than caring. */
  if(r.Reason==="sourceOff"||(modResultSource(r)==="hexium"&&!r.Installed)){
    hexiumFailToast(r);
    renderMods();
    return;
  }
  if(r.Installed){
    const full=r.Owner+"-"+r.Name;
    /* Four whole sentences rather than a name, a version that may be missing and a
       tail that may not be there: a language that inflects around either of them has
       nothing to work with when the words are glued together here. */
    if(r.Replaced) toast("ᚨ "+(r.Version
      ?T("mods.add.installed.replaced.toast",{name:full,version:r.Version})
      :T("mods.add.installed.replaced.noversion.toast",{name:full})));
    else toast("ᚨ "+(r.Version
      ?T("mods.add.installed.toast",{name:full,version:r.Version})
      :T("mods.add.installed.noversion.toast",{name:full})));
    logLine("ok","[Thunderstore] installed "+full+" v"+(r.Version||"?")+(r.Replaced?" (previous copy backed up)":""));
    if(S.state?.status==="Running") logLine("warn","[Thunderstore] server is running, so "+full+" loads on the next restart");
    await scanMods();
  }else{
    /* A refusal that named itself is said in the reader's own language here too. The add
       carries the id in Reason and the sentence's named values in ErrorParams, so a
       BepInEx refusal that came back through the add reads exactly as the same refusal
       does on the direct call rather than as the raw English the host side wrote. */
    const own=hostSentence(r.Reason,r.ErrorParams);
    toast("ᚦ "+(own||T("mods.add.failed.toast",{detail:r.Error||T("common.error.unknown")})));
    logLine("warn","[Thunderstore] install failed: "+(r.Error||"unknown"));
    renderMods();
  }
}
$("#addModBtn").addEventListener("click",()=>{
  if(Native.available){addModFlow();return;}
  toast("ᚨ "+T("mods.add.native_only.toast"));
});

/* ---------- COMMAND PALETTE ---------- */
const palBg=$("#paletteBg"), palIn=$("#palInput");
let palOpen=false;
function openPal(){palOpen=true;palBg.classList.add("open");palIn.value="";updatePalGating();filterPal("");setTimeout(()=>palIn.focus(),30);}
function closePal(){palOpen=false;palBg.classList.remove("open");}
/* The words a row is showing right now, which is what palette search matches and
   what the preview echoes back. Its own text nodes only, so the rune in front and
   the hall badge behind it stay out of the haystack: typing "rite" should not
   light up every row tagged rite. */
const palLabel=it=>[...it.childNodes].filter(n=>n.nodeType===3).map(n=>n.nodeValue).join("").trim();
/* Grey out rites that have nothing to act on: RCON commands need a RUNNING server,
   start/stop follow canStart/canStop. Browser preview keeps everything clickable.
   Keyed on the command's NAME, never on its label: a translated label would have
   walked straight past a gate keyed on English. */
const PAL_NEEDS_RUNNING={save_world:1,kill_monsters:1,broadcast:1,console_command:1};
function updatePalGating(){
  if(!Native.available) return;
  const st=S.state||{}, running=st.status==="Running";
  $$(".pitem").forEach(it=>{
    const cmd=it.dataset.cmd; let dis=false, why="";
    if(it.id==="palConsole"||PAL_NEEDS_RUNNING[cmd]){dis=!running;why=T("pal.gate.needs_running");}
    else if(cmd==="update_server"){
      const u=S.update||{};
      if(u.running){dis=true;why=T("pal.gate.update_running");}
      else if(!u.updatePending){dis=true;why=T("pal.gate.no_update");}
      else if(!updCanUpdate(null)){dis=true;why=u.reason||T("pal.gate.update_by_hand");}
      else if(st.status!=="Stopped"){dis=true;why=T("pal.gate.stop_first");}
    }
    else if(cmd==="restart_server"){
      if(updateBlocksStart()){dis=true;why=updBlockMsg();}
      else{dis=!(st.canStop||st.countdownActive);why=T("pal.gate.not_running");}
    }
    else if(cmd==="stop_server"){dis=!st.canStop;why=T("pal.gate.not_running");}
    else if(cmd==="start_server"){
      if(updateBlocksStart()){dis=true;why=updBlockMsg();}
      else{dis=!st.canStart;why=T("pal.gate.already_running");}
    }
    it.classList.toggle("disabled",dis);
    it.title=dis?why:"";
  });
}
/* Free-form console command over RCON. Echoes "> cmd" to the Saga log, then the
   reply (many Valheim commands answer to stdout, which the log already tails).
   @param {string} cmd the line to send
   @param {function} [read] what to say about the REPLY, once the command has been
     delivered. It is handed the reply text, trimmed, and answers with a whole toast or
     with nothing. r.ok says only that RCON carried the line; the server answers a
     command it refused on the same channel it answers one it ran, so a caller that
     toasts success off delivery alone is claiming something nobody checked. Every
     caller whose command can come back refused hands one of these in; the ones that
     say nothing on success (the free-typed console, the command picker) hand in
     nothing and the reply stands on its own in the log. */
async function sendConsole(cmd,read){
  cmd=(cmd||"").trim(); if(!cmd) return;
  logLine("cmd","> "+cmd);
  const r=await rpc("server.command",{command:cmd});
  if(r===FAIL) return;
  if(!r.ok){
    toast("ᚦ "+T("pal.console.undelivered.toast"));
    logLine("warn","[RCON] command failed. Is the server running with RCON enabled?");
    return;
  }
  const resp=(r.response||"").trim();
  if(typeof read==="function"){
    const said=read(resp);
    if(said) toast(said);
  }
  if(resp) resp.split(/\r?\n/).forEach(l=>logLine("ok",l));
  else logLine("ok","[RCON] "+cmd+" · dispatched. Replies land here in the Saga log");
}

/* KILLALL-BEGIN
   ------------------------------------------------------------------------------------
   The kill sweep: what the host is about to do, and what the server said they did.

   Both halves of this block exist because of one class of defect. sendConsole used to
   fire an "unleashed" toast the moment RCON carried the line, and baka_killall answers
   on that same line with every one of: the three counts, a "started" line for a sweep
   too big to finish inside the answer, a refusal because one is already running, a
   stopped-early line, and five different sentences that open with "Error:". Every one
   of them used to read as success.

   Everything here is a pure function of its arguments so it can be driven as a table
   with no browser in the room (scripts/ui/killall_reply_selftest.js runs the real code
   out of this file, between these markers). The wording is not in here: these answer
   with what happened, and the caller asks the catalog for the words.                  */

/* True when a server's answer to any console command is a refusal rather than a result.
   The three openings every command in this product can come back with. */
function consoleRefused(reply){
  const said=String(reply==null?"":reply).trim();
  return /^(Error:|Unknown command)/i.test(said)||/^KillAll failed:/i.test(said);
}

/** The whole numbers out of a counts clause, or null when it is not one. */
function killAllCounts(said){
  const m=/(\d+)\s+hostiles?\s+slain,\s*(\d+)\s+out of reach,\s*(\d+)\s+spared/i.exec(said);
  return m?{slain:+m[1],unreachable:+m[2],spared:+m[3]}:null;
}

/**
 * What baka_killall just said, read rather than assumed.
 * @param {string} reply the server's answer, exactly as it arrived
 * @returns {object} kind, plus whatever that kind carries:
 *   complete  {slain,unreachable,spared} the sweep finished inside the answer
 *   started   {candidates}               too big for the answer, the result is in the log
 *   busy      {}                         a sweep was already walking; nothing was struck
 *   stopped   {slain,unreachable,spared} it threw part way; the counts are real work
 *   refused   {}                         Error:, Unknown command, KillAll failed, Usage:
 *   silent    {}                         RCON carried it and the server said nothing
 *   unknown   {}                         a shape this version has not met
 */
function killAllReply(reply){
  const said=String(reply==null?"":reply).trim();
  if(!said) return {kind:"silent"};
  if(consoleRefused(said)) return {kind:"refused"};
  /* The plugin answers a line it could not parse with its usage sentence and strikes
     nothing. Read as a shape this version had not met, it came back as "the server
     answered something this version cannot read", which is true of the READER and not
     of what happened: the command was refused, and that is what the host is told. */
  if(/^Usage:\s*baka_killall\b/i.test(said)) return {kind:"refused"};
  if(/^KillAll is already running:/i.test(said)) return {kind:"busy"};
  if(/^KillAll stopped early:/i.test(said)){
    const c=killAllCounts(said);
    /* No counts behind it means nobody counted: say it stopped and say nothing else. */
    return c?Object.assign({kind:"stopped"},c):{kind:"stopped",counted:false};
  }
  if(/^KillAll complete:/i.test(said)){
    const c=killAllCounts(said);
    /* "complete" with no counts behind it is a shape this version has not met, and
       calling it a finished sweep would put three zeroes on screen that nobody
       counted. */
    return c?Object.assign({kind:"complete"},c):{kind:"unknown"};
  }
  const started=/^KillAll started:\s*(\d+)\s+candidates?/i.exec(said);
  if(started) return {kind:"started",candidates:+started[1]};
  return {kind:"unknown"};
}

/** The scope a host has chosen, before it is a command. Free of the DOM on purpose. */
function killAllScopeBlank(){return {scope:"everywhere",who:"",radius:"100",name:""};}

/**
 * The command line for a scope, or the reason it is not one yet.
 * @param {object} s {scope,who,radius,name}
 * @returns {object} {cmd} when it is runnable, {problemId} when it is not. The id is a
 *   catalog id rather than a sentence: this function does not know what language the
 *   window is reading.
 */
function killAllCommand(s){
  const pick=(s&&s.scope)||"everywhere";
  if(pick==="near"){
    const who=String((s&&s.who)||"").trim();
    if(!who) return {problemId:"pal.kill.problem.no_player"};
    /* The same number the plugin will refuse if it is not one, refused here first so
       the host reads it beside the box rather than as a server error afterwards. A
       player name may hold spaces and the plugin takes the radius off the END of the
       line, so a name with a space in it is fine here and only the radius is checked. */
    const metres=Number(String((s&&s.radius)||"").trim().replace(",","."));
    if(!isFinite(metres)||metres<=0) return {problemId:"pal.kill.problem.radius"};
    return {cmd:"baka_killall near "+who+" "+metres};
  }
  if(pick==="creature"){
    const name=String((s&&s.name)||"").trim();
    if(!name) return {problemId:"pal.kill.problem.no_creature"};
    /* A prefab name never holds a space, and the plugin answers a longer line with a
       usage error rather than guessing which word was meant. */
    if(/\s/.test(name)) return {problemId:"pal.kill.problem.creature_spaces"};
    return {cmd:"baka_killall "+name};
  }
  return {cmd:"baka_killall"};
}
/* KILLALL-END */

/** The toast for a reply, worded. Split from the reading above so the table test can
    drive the reading with no catalog, and so one sentence has one owner. */
function killAllToast(read){
  switch(read.kind){
    case "complete":
      return read.slain>0
        ?"ᚦ "+T("pal.kill.toast.complete",
          {slain:read.slain,unreachable:read.unreachable,spared:read.spared,pluralValue:read.slain})
        :"ᛉ "+T("pal.kill.toast.none",{unreachable:read.unreachable,spared:read.spared});
    case "started":  return "ᚦ "+T("pal.kill.toast.started",{count:read.candidates});
    case "busy":     return "ᛊ "+T("pal.kill.toast.busy");
    /* The counts on a stopped sweep are work that really happened, so they are said
       rather than dropped: a host who reads "it stopped part way" and nothing else has
       no idea whether one creature fell or four hundred did. */
    case "stopped":  if(read.counted===false) return "ᚦ "+T("pal.kill.toast.stopped.nocounts");
                     return "ᚦ "+T("pal.kill.toast.stopped",
      {slain:read.slain,unreachable:read.unreachable,spared:read.spared,pluralValue:read.slain});
    case "refused":  return "ᚦ "+T("pal.console.refused.toast");
    case "silent":   return "ᚦ "+T("pal.kill.toast.silent");
    default:         return "ᚦ "+T("pal.kill.toast.unreadable");
  }
}

/* The question before the sweep. One tap used to send the bare command, and the bare
   command now really does strike every hostile in every zone the server has loaded, for
   every player who is online: a host clearing a raid off one base emptied four other
   bases with them. So the blast radius is said out loud and the host picks how much of
   it they meant.
   The three scopes are the three the plugin serves and nothing else, and the command
   they build is the plugin's own spelling. What is typed survives a language switch the
   way promptModal's box does, because the state lives beside the rebuilder. */
let KILL_SCOPE=killAllScopeBlank();
function killAllModal(){
  const online=(S.players||[]).filter(p=>p.status==="Online").map(p=>p.displayName||p.PlayerId||"");
  /* A player who has gone offline while the dialog stood open is not a radius origin any
     more, so the choice is dropped rather than sent at a name nobody answers to. */
  if(KILL_SCOPE.who&&!online.includes(KILL_SCOPE.who)) KILL_SCOPE.who="";
  /* And with nobody online at all the radius scope is not a choice this dialog can serve,
     so a remembered "near" goes back to the default rather than standing checked on a row
     that is greyed out. Left as it was, the row said "Nobody is online, so there is
     nothing to measure a radius from" and pressing Kill them answered "Choose the player
     to measure the radius from": two sentences for one condition, and the second of them
     asking for something the dialog was not offering. */
  if(KILL_SCOPE.scope==="near"&&!online.length) KILL_SCOPE.scope="everywhere";
  if(!KILL_SCOPE.who&&online.length===1) KILL_SCOPE.who=online[0];
  /* A refusal is a reply to one press of one scope, so it is remembered WITH that scope
     and drawn only while that scope is the one being asked. Held as a bare id it
     outlived the thing it was about: pick a different scope and the line about creature
     names was still sitting under a radius box. Matching on the scope covers the radio,
     the preview seam and anything else that changes the question, rather than relying on
     every one of them to remember to clear it. */
  let problem={id:"",scope:""};

  const again=()=>{
    const pick=KILL_SCOPE.scope;
    const problemId=problem.scope===pick?problem.id:"";
    const row=(value,label,note,inert)=>
      `<label class="togglerow${inert?" gated":""}" style="height:auto;padding:7px 0;align-items:flex-start;cursor:${inert?"default":"pointer"}">`+
        `<input type="radio" name="kaScope" value="${esc(value)}"${pick===value?" checked":""}${inert?" disabled":""} style="margin:3px 9px 0 0">`+
        `<span class="tl" style="display:block"><span style="display:block">${esc(label)}</span>`+
        `<span class="subval" style="display:block;margin-top:2px">${esc(note)}</span></span></label>`;

    const m=modalOpen(
      `<div class="mtitle">${esc(T("pal.kill.confirm.title"))}</div>`+
      `<div class="mbody">`+
        `<div class="subval">${esc(T("pal.kill.confirm.body"))}</div>`+
        `<div class="dsec" style="margin-top:10px">${esc(T("pal.kill.scope.label"))}</div>`+
        row("everywhere",T("pal.kill.scope.everywhere"),T("pal.kill.scope.everywhere.note"),false)+
        row("near",T("pal.kill.scope.near"),
          online.length?T("pal.kill.scope.near.note"):T("pal.kill.nobody_online"),!online.length)+
        (pick==="near"&&online.length
          ?`<div style="display:flex;gap:10px;margin:2px 0 4px 25px">`+
             `<div class="field" style="flex:1 1 auto"><label>${esc(T("pal.kill.field.player"))}</label>`+
               `<select id="kaWho">${online.map(n=>
                 `<option value="${esc(n)}"${n===KILL_SCOPE.who?" selected":""}>${esc(n)}</option>`).join("")}</select></div>`+
             `<div class="field" style="flex:0 0 130px"><label>${esc(T("pal.kill.field.radius"))}</label>`+
               `<input type="text" id="kaRadius" inputmode="numeric" spellcheck="false" autocomplete="off" value="${esc(KILL_SCOPE.radius)}"></div>`+
           `</div>`
          :"")+
        row("creature",T("pal.kill.scope.creature"),T("pal.kill.scope.creature.note"),false)+
        (pick==="creature"
          ?`<div class="field" style="margin:2px 0 4px 25px"><label>${esc(T("pal.kill.field.creature"))}</label>`+
             `<input type="text" id="kaName" placeholder="${esc(T("pal.kill.field.creature.placeholder"))}" spellcheck="false" autocomplete="off" value="${esc(KILL_SCOPE.name)}"></div>`
          :"")+
        (problemId?`<div class="subval" id="kaNote" style="color:var(--blood);margin-top:6px">${esc(T(problemId))}</div>`:"")+
      `</div>`+
      `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="kaCancel">${esc(T("common.button.cancel"))}</button>`+
        `<button class="btn btn-blood btn-sm" id="kaOk">${esc(T("pal.kill.confirm.button"))}</button></div>`,
      again);

    m.querySelectorAll('input[name="kaScope"]').forEach(r=>r.addEventListener("change",()=>{
      KILL_SCOPE.scope=r.value; again();
    }));
    /* The line goes as soon as the host starts fixing what it complained about, the same
       way promptModal's does. Hidden rather than redrawn, because redrawing the dialog on
       a keystroke would take the caret with it. */
    const hideNote=()=>{
      if(!problem.id) return;
      problem={id:"",scope:""};
      const note=m.querySelector("#kaNote");
      if(note) note.style.display="none";
    };
    const who=m.querySelector("#kaWho");
    if(who) who.addEventListener("change",()=>{KILL_SCOPE.who=who.value;hideNote();});
    const rad=m.querySelector("#kaRadius");
    /* Kept on every keystroke rather than read on the press: a language switch redraws
       this dialog, and a half-typed radius is the same loss a reload was rejected for. */
    if(rad) rad.addEventListener("input",()=>{KILL_SCOPE.radius=rad.value;hideNote();});
    const name=m.querySelector("#kaName");
    if(name) name.addEventListener("input",()=>{KILL_SCOPE.name=name.value;hideNote();});

    m.querySelector("#kaCancel").addEventListener("click",modalClose);
    m.querySelector("#kaOk").addEventListener("click",()=>{
      if(who) KILL_SCOPE.who=who.value;
      if(rad) KILL_SCOPE.radius=rad.value;
      if(name) KILL_SCOPE.name=name.value;
      const built=killAllCommand(KILL_SCOPE);
      /* A refusal keeps the dialog open with everything still in it, so one wrong
         character is one character to fix rather than the whole thing to type again. */
      if(built.problemId){problem={id:built.problemId,scope:KILL_SCOPE.scope};again();return;}
      modalClose();
      sendConsole(built.cmd,reply=>killAllToast(killAllReply(reply)));
    });
    setTimeout(()=>{(m.querySelector("#kaName")||m.querySelector("#kaRadius"))?.focus();},30);
    return m;
  };
  return again();
}
function filterPal(q){
  const raw=q.trim(); q=raw.toLowerCase(); let first=true;
  $$(".pitem:not(.pconsole)").forEach(it=>{
    const hit=palLabel(it).toLowerCase().includes(q);
    const pick=hit&&!it.classList.contains("disabled");
    it.classList.toggle("hidden",!hit);
    it.classList.toggle("sel",pick&&first); if(pick) first=false;
  });
  // Free-typed helper: anything typed can be sent straight to the server console.
  // Shown at the bottom while typing; becomes the selection when nothing matches.
  const con=$("#palConsole");
  if(con){
    const show=Native.available&&raw.length>0;
    con.classList.toggle("hidden",!show);
    con.classList.toggle("sel",show&&first&&!con.classList.contains("disabled"));
    if(show){
      con.dataset.raw=raw;
      $("#palConsoleLbl").textContent=T("pal.console.prompt",{command:raw});
    }
  }
}
function invokePal(){
  const sel=$(".pitem.sel:not(.hidden)")||$(".pitem:not(.hidden)");
  if(!sel) return;
  if(sel.classList.contains("disabled")){toast("ᚦ "+(sel.title||T("pal.gate.unavailable")));return;}
  closePal();
  if(sel.dataset.cmd==="discord_sharing"){goPage("herald");return;} // works in preview too
  if(sel.dataset.cmd==="custom_domain"){waystoneWizard();return;}   // works in preview too, the Waystone
  if(sel.dataset.cmd==="world_backups"){barrowModal();return;}      // works in preview too
  if(sel.dataset.cmd==="server_analytics"){goPage("skald");return;} // works in preview too
  if(sel.dataset.cmd==="log_settings"){vellumModal();return;}      // works in preview too
  if(Native.available){
    const cmd=sel.dataset.cmd;
    if(sel.id==="palConsole"){
      sendConsole(sel.dataset.raw||"");
    }else if(cmd==="restart_server"){
      smartRestart();
    }else if(cmd==="start_server"){
      const st=S.state||{};
      if(updateBlocksStart()){toast("ᚦ "+updBlockMsg());}
      else if(!st.canStart){toast("ᚦ "+T("pal.start.unavailable.toast"));}
      else if(!S.prefs){toast("ᚦ "+T("hearth.no_profile.toast"));}
      /* the same gate the Kindle button goes through: a changed build or a waiting
         Steam update is put to the host here too, not started past silently */
      else withLaunchGuard(async answer=>{
        if(answer){startWithAnswer(answer);return;}
        const r=await rpc("server.start",{prefs:S.prefs});
        if(r===FAIL||rpcRefused(r)) return;
        applyState(r);toast("ᚠ "+T("hearth.starting.toast"));logLine("ok","[BakaLoader] start requested · profile "+(S.profileName||"?"));
      });
    }else if(cmd==="stop_server"){
      if(!(S.state||{}).canStop){toast("ᚦ "+T("pal.stop.unavailable.toast"));}
      else rpc("server.stop").then(r=>{
        if(r!==FAIL){applyState(r);toast("ᛪ "+T("hearth.stopping.toast"));logLine("warn","[BakaLoader] stop requested, dousing the embers");}
      });
    }else if(cmd==="save_world"){
      /* The other command in this palette whose reply can be a refusal. A modded server
         answers an unknown verb with "Unknown command: 'save' ..." on the same channel
         it answers a real one, so the saved toast waits to see which arrived. */
      sendConsole("save",reply=>consoleRefused(reply)
        ?"ᚦ "+T("pal.console.refused.toast")
        :"ᛉ "+T("pal.save_world.done.toast"));
    }else if(cmd==="kill_monsters"){
      killAllModal();
    }else if(cmd==="broadcast"){
      promptModal(()=>T("vikings.broadcast.title"),()=>T("vikings.broadcast.placeholder"),m=>{
        rpc("server.broadcast",{message:m}).then(r=>{
          if(r===FAIL) return;
          toast(r?"ᛒ "+T("pal.broadcast.done.toast"):"ᚦ "+T("pal.broadcast.failed.toast"));
          logLine(r?"ok":"warn",r?"[RCON] broadcast delivered":"[RCON] broadcast failed. Is RCON enabled and bound?");
        });
      });
    }else if(cmd==="console_command"){
      consoleModal();
    }else if(cmd==="update_server"){
      /* Same question the condition bar asks, and the same one-at-a-time guard. */
      updateBackupPrompt(false);
    }else if(cmd==="update_mods"){
      goPage("mods");
      $("#updAllBtn").click();
    }else if(cmd==="kick_player"){
      goPage("vikings");
      toast("ᚲ "+T("pal.kick.hint.toast"));
    }else if(cmd==="copy_join_address"){
      const addr=joinHost()+":"+(S.prefs?.Port??2456);
      navigator.clipboard?.writeText(addr).catch(()=>{});
      toast("ᛟ "+T("pal.join_address.copied.toast",{addr}));
    }else if(cmd==="open_world_folder"){
      rpc("shell.open",{target:"saveData"});
    }else if(cmd==="open_config_folder"){
      rpc("shell.open",{target:"config"});
    }else if(cmd==="open_plugins_folder"){
      rpc("shell.open",{target:"plugins"});
    }else if(cmd==="open_server_logs"){
      rpc("shell.open",{target:"logs"});
    }
    return;
  }
  /* Preview only. It echoes the LABEL, because that is the thing the host just
     clicked; the name behind it is not something they have ever seen. */
  toast("ᛒ "+T("pal.invoked.preview.toast",{label:palLabel(sel)}));
  logLine("cmd","> "+palLabel(sel).toLowerCase().replace(/…/,""));
}
$("#cmdchip").addEventListener("click",openPal);
palIn.addEventListener("input",()=>filterPal(palIn.value));
palBg.addEventListener("click",e=>{if(e.target===palBg)closePal();});
$$(".pitem").forEach(it=>it.addEventListener("click",()=>{
  if(it.classList.contains("disabled")){toast("ᚦ "+(it.title||T("pal.gate.unavailable")));return;}
  $$(".pitem").forEach(x=>x.classList.remove("sel")); it.classList.add("sel"); invokePal();
}));
document.addEventListener("keydown",e=>{
  if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==="k"){e.preventDefault();palOpen?closePal():openPal();return;}
  if(!palOpen) return;
  const vis=$$(".pitem:not(.hidden):not(.disabled)");
  const idx=vis.findIndex(v=>v.classList.contains("sel"));
  if(e.key==="Escape"){closePal();}
  else if(e.key==="ArrowDown"){e.preventDefault();vis.forEach(v=>v.classList.remove("sel"));(vis[Math.min(idx+1,vis.length-1)]||vis[0])?.classList.add("sel");}
  else if(e.key==="ArrowUp"){e.preventDefault();vis.forEach(v=>v.classList.remove("sel"));(vis[Math.max(idx-1,0)]||vis[0])?.classList.add("sel");}
  else if(e.key==="Enter"){e.preventDefault();invokePal();}
});

/* ---------- "/" REACHES THE SEARCH BOX ----------
   On a hall that carries one, "/" puts the cursor in its search box and selects what is
   already there, the way search works nearly everywhere else. It stands down while a
   dialog or the command palette is open, and while anything is already being typed into,
   so a slash typed into a field stays a slash. */
const PAGE_SEARCH_BOX={mods:"#modSearch",runes:"#runeSearch"};
function typingIntoSomething(){
  const a=document.activeElement;
  if(!a) return false;
  const tag=(a.tagName||"").toLowerCase();
  return tag==="input"||tag==="textarea"||tag==="select"||a.isContentEditable===true;
}
document.addEventListener("keydown",e=>{
  if(e.key!=="/"||e.ctrlKey||e.metaKey||e.altKey) return;
  if(palOpen||$("#modalBg")?.classList.contains("open")) return;
  if(typingIntoSomething()) return;
  const sel=PAGE_SEARCH_BOX[currentPage]; if(!sel) return;
  const box=$(sel); if(!box) return;
  e.preventDefault();
  box.focus(); box.select();
});

/* ---------- TOASTS ---------- */
const TOAST_MAX=3, TOAST_LIFE=4800;
/* The glyph in the ember slot. It used to be found by splitting on the first space and
   putting whatever came back as innerHTML, which is two bugs standing together: a
   language that does not put spaces between words (Chinese) hands the WHOLE sentence to
   the slot, unescaped, leaving an empty body; and any sentence whose first word is a
   word puts that word in ember. So the mark is now recognised for what it is, a single
   glyph, and the body is escaped whatever happens.
   The Runic block is the set the halls draw from. The two symbol marks already in use
   (the house and the return arrow) are named beside it rather than left to regress. */
const TOAST_MARK_RE=/^([\u16A0-\u16FF\u2302\u21BA])\s/;
function toast(msg,opts){
  /* The sentence arrives worded. Every caller asks T() for it and the register is
     picked inside the lookup, so there is nothing left here to swap: a toast used to
     be rewritten one more time on its way to the screen, and now it is not. */
  let mark=(opts&&opts.mark)?String(opts.mark):"";
  let body=String(msg==null?"":msg);
  if(!mark){
    const m=TOAST_MARK_RE.exec(body);
    if(m){mark=m[1];body=body.slice(m[0].length);}
  }
  const t=document.createElement("div");
  t.className="toast";
  t.innerHTML=(mark?`<span class="r">${esc(mark)}</span>`:"")+
    `<span>${esc(body)}</span><span class="tt">${clock()}</span>`;
  const box=$("#toasts");
  box.appendChild(t);
  /* three at a time, oldest first out - a taller stack just buries the controls */
  while(box.children.length>TOAST_MAX) box.firstElementChild.remove();
  setTimeout(()=>{t.classList.add("out");setTimeout(()=>t.remove(),320);},TOAST_LIFE);
}

/* ---------- SAGA TERMINAL ---------- */
const term=$("#term"); let filter="all", termQ="";
const TERM_CAP=1200;                      // scrollback (lines kept in the DOM)
let termPin=true, termUnseen=0;           // pinned-to-bottom = live tail; scrolled up = paused

/* one visibility rule for pills + search (dividers ride every pill, hide under search) */
function lnVisible(el){
  const k=el.dataset.k;
  if(k==="div") return !termQ;
  const passK=(filter==="all"||filter===k||(filter==="info"&&k==="ok"));
  return passK&&(!termQ||(el.dataset.s||"").includes(termQ));
}
function applyTermVis(){
  $$("#term .ln").forEach(l=>{l.style.display=lnVisible(l)?"":"none";});
  renderTermEmpty();
  if(termPin) term.scrollTop=term.scrollHeight;
}
function renderTermStream(){
  const chip=$("#termNew"), pill=$("#termStream");
  if(termPin){
    chip.classList.add("hidden");
    pill.textContent=T("saga.stream.live"); pill.className="pill green";
  }else{
    chip.textContent="↓ "+T("saga.stream.unseen",{count:termUnseen});
    chip.classList.toggle("hidden",termUnseen===0);
    pill.textContent=T("saga.stream.paused"); pill.className="pill amber";
  }
}
/* The console starts empty on a cold launch, so say so rather than showing a blank
   black panel that looks broken. */
function renderTermEmpty(){
  if(!term) return;
  const has=term.querySelector(".ln");
  const es=term.querySelector(".empty-state");
  if(has&&es){es.remove();return;}
  if(has||es) return;
  term.insertAdjacentHTML("beforeend",emptyState({mark:"ᛋ",title:T("saga.empty.title"),
    reason:T("saga.empty.reason"),
    action:{name:"startServer",label:T("saga.empty.action")}}));
  esWire(term);
}
function termAppend(d){
  const es=term.querySelector(".empty-state"); if(es) es.remove();
  term.appendChild(d);
  while(term.children.length>TERM_CAP) term.firstChild.remove();
  if(termPin) term.scrollTop=term.scrollHeight;
  else if(d.style.display!=="none"&&d.dataset.k!=="div"){termUnseen++;renderTermStream();}
}
function logLine(kind,text,time){
  const d=document.createElement("div");
  d.className="ln "+kind; d.dataset.k=kind;
  d.dataset.s=(text||"").toLowerCase();
  const t=time||clock()+":"+String(new Date().getSeconds()).padStart(2,"0");
  d.innerHTML=`<span class="t">${esc(t)}</span>  <span class="${kind}">${esc(text)}</span>`;
  d.style.display=lnVisible(d)?"":"none";
  termAppend(d);
  hlogPush(kind,text,t);   // Hearth hall keeps a mirror of the last few lines
}
/* horizontal rule with a label - marks session starts and helm turns */
function logDivider(text){
  const d=document.createElement("div");
  d.className="ln div"; d.dataset.k="div"; d.dataset.s="";
  d.innerHTML=`<span class="divline"></span><span class="divtxt">${esc(text)}</span><span class="divline"></span>`;
  d.style.display=lnVisible(d)?"":"none";
  termAppend(d);
}
/* App-log lines arrive as "[HH:mm:ss.fff] [WRN] msg" - lift the timestamp into the
   time cell (no double clocks) and read the severity tag instead of guessing.
   Server lines carry no tag, so they fall back to content heuristics. */
const LOG_TS_RE=/^\[(\d\d:\d\d:\d\d)(?:\.\d+)?\]\s*/;
const LOG_TAG_RE=/^\[(VER|DBG|WRN|ERR|FAT)\]\s*/;
const LOG_TAG_KIND={VER:"dbg",DBG:"dbg",WRN:"warn",ERR:"err",FAT:"err"};
function classifyLog(line){
  let text=line??"", time=null, kind=null;
  const ts=text.match(LOG_TS_RE);
  if(ts){time=ts[1];text=text.slice(ts[0].length);}
  const tag=text.match(LOG_TAG_RE);
  if(tag){kind=LOG_TAG_KIND[tag[1]];text=text.slice(tag[0].length);}
  if(!kind){
    if(NET_RE.test(text)) kind="net";
    else{
      const l=text.toLowerCase();
      if(/\bexception\b|\berror\b|\bfailed\b|\bfatal\b/.test(l)||/^\s+at\s+\S/.test(text)) kind="err";
      else if(/\bwarn/.test(l)) kind="warn";
      else kind="info";
    }
  }
  return {kind,time,text};
}
function logRaw(line){const c=classifyLog(line);logLine(c.kind,c.text,c.time);}

renderTermEmpty();

/* filter pills */
$$(".fpill[data-f]").forEach(p=>p.addEventListener("click",()=>{
  $$(".fpill[data-f]").forEach(x=>x.classList.remove("active")); p.classList.add("active");
  filter=p.dataset.f;
  applyTermVis();
}));

/* search - narrows whatever the active pill shows */
$("#termSearch").addEventListener("input",e=>{
  termQ=e.target.value.trim().toLowerCase();
  applyTermVis();
});

/* pause-on-scroll-up: leave the bottom and the tail holds still; a chip counts
   what arrived while reading. Click the chip (or scroll back down) to resume. */
term.addEventListener("scroll",()=>{
  const atBottom=term.scrollTop+term.clientHeight>=term.scrollHeight-8;
  if(atBottom&&!termPin){termPin=true;termUnseen=0;renderTermStream();}
  else if(!atBottom&&termPin){termPin=false;renderTermStream();}
});
$("#termNew").addEventListener("click",()=>{
  termPin=true;termUnseen=0;term.scrollTop=term.scrollHeight;renderTermStream();
});

/* copy exactly what's on screen (current pill + search) */
$("#termCopy").addEventListener("click",()=>{
  const lines=$$("#term .ln").filter(l=>l.style.display!=="none"&&l.dataset.k!=="div")
    .map(l=>l.textContent);
  navigator.clipboard?.writeText(lines.join("\n")).catch(()=>{});
  toast("ᛝ "+T("saga.copied.toast",{count:lines.length}));
});

/* ---------- THE VELLUM (log settings) ---------- */
async function vellumModal(){
  let up=null;
  if(Native.available){const r=await rpc("userprefs.get");if(r!==FAIL)up=r;}
  const m=modalOpen(
    `<div class="mtitle">${esc(T("saga.vellum.modal.title"))}<span class="cnorse">${esc(T("common.norse.vellum"))}</span></div>`+
    `<div class="mbody">`+
      `<div class="field"><label>${esc(T("saga.vellum.folder.label"))}</label>`+
        `<input type="text" id="vlPath" value="${esc(up?.LogsFolderPath||"")}" placeholder="${esc(up?.DefaultLogsFolderPath||T("saga.vellum.folder.placeholder"))}" spellcheck="false" autocomplete="off">`+
        `<div class="fieldnote">${esc(T("saga.vellum.folder.note"))}</div></div>`+
      `<label class="togglerow" style="cursor:pointer"><span class="tl">${esc(T("saga.vellum.applog.label"))}`+
        `<br><span style="font-size:9.5px;opacity:.7">ApplicationLogs_&lt;${esc(T("saga.vellum.applog.day"))}&gt;.txt · ${esc(T("saga.vellum.applog.keep"))}</span></span>`+
        `<div class="toggle${(up?up.WriteApplicationLogsToFile:true)?" on":""}" id="vlApp"></div></label>`+
      /* The file-name pattern is two angle-bracketed words, and an angle bracket inside a
         catalog value reads as a tag to the gate that forbids markup there. So the sentence
         holds two named slots and the brackets are put round the words here, where they are
         punctuation rather than wording: the entry stays plain text a translator can move
         the pattern around inside, and the host reads exactly what they read before. */
      `<div class="fieldnote">${esc(T("saga.vellum.serverlog.note",
        {server:"<"+T("saga.vellum.serverlog.realm")+">",start:"<"+T("saga.vellum.serverlog.start")+">"}))}</div>`+
      `<div class="fieldnote" id="vlStatus"></div>`+
    `</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="vlCancel">${esc(T("common.button.cancel"))}</button>`+
      `<button class="btn btn-ember btn-sm" id="vlOk" title="${esc(T("saga.vellum.save.title"))}">${esc(T("common.button.save"))}</button></div>`,vellumModal);
  const tApp=m.querySelector("#vlApp");
  tApp.addEventListener("click",()=>tApp.classList.toggle("on"));
  m.querySelector("#vlCancel").addEventListener("click",modalClose);
  m.querySelector("#vlOk").addEventListener("click",async()=>{
    if(!Native.available){modalClose();toast("ᚹ "+T("saga.vellum.preview.toast"));return;}
    const st=m.querySelector("#vlStatus");
    const r=await rpc("userprefs.save",{prefs:{
      LogsFolderPath:m.querySelector("#vlPath").value.trim(),
      WriteApplicationLogsToFile:tApp.classList.contains("on"),
    }});
    if(r===FAIL){st.textContent=T("saga.vellum.save.failed");return;}
    modalClose();
    toast("ᚹ "+T("saga.vellum.saved.toast"));
  });
}
$("#vellumBtn").addEventListener("click",vellumModal);

/* command input */
const tIn=$("#termIn"), tCaret=$("#termCaret");
tIn.addEventListener("input",()=>tCaret.style.display=tIn.value?"none":"");
tIn.addEventListener("keydown",e=>{
  if(e.key!=="Enter"||!tIn.value.trim())return;
  const c=tIn.value.trim(); tIn.value=""; tCaret.style.display="";
  if(Native.available){
    // Real console command over RCON (sendConsole echoes "> cmd" + the reply itself)
    sendConsole(c);
    return;
  }
  logLine("cmd","> "+c);
  setTimeout(()=>{
    const r={save:"World saved ( Final_Sunset.db )  8.44 MB  in 205 ms",
             players:"2 vikings online: Smithix, Van Hoenhiem",
             help:"available rites: save · players · restart · spawn <item> · kick <viking>"}[c.split(" ")[0]]
           ||"[RCON] command dispatched · ok";
    logLine("ok",r);
  },420);
});

/* ---------- CAPABILITY GATING (missing required mods) ----------
   Native only: advertise which Thunderstore mods unlock the sleeping deeds
   (banner on the VIKINGS page) and grey out the saga broadcast input. */
function renderCaps(){
  const missing=(Native.available&&Array.isArray(S.caps?.missing))?S.caps.missing:[];
  const banner=$("#capsBanner");
  if(banner){
    if(missing.length){
      $("#capsList").innerHTML=missing.map(m=>
        `<div>ᛜ <b>${esc((m.Author?m.Author+"/":"")+(m.ModName||""))}</b>: ${esc(m.RequiredFor||m.Description||"")}</div>`
      ).join("");
      banner.style.display="";
    }else banner.style.display="none";
  }
  /* The banner's own button. It is written here rather than carried in the page so its
     words come out of the catalog, and it is left alone mid-install: a terminology
     toggle while Thunderstore is answering must not wipe "Fetching from Thunderstore…"
     off a button that is still busy. */
  const install=$("#capsInstallBtn");
  if(install&&!capsInstallBusy) install.textContent=T("vikings.caps.install.label");
  const gated=Native.available&&!S.caps?.devcommands;
  tIn.disabled=gated;
  tIn.placeholder=gated
    ?T("saga.term.gated.placeholder")
    :T("saga.term.placeholder");
}
let capsInstallBusy=false;
$("#capsInstallBtn")?.addEventListener("click",async()=>{
  if(capsInstallBusy||!Native.available) return;
  capsInstallBusy=true;
  const btn=$("#capsInstallBtn");
  btn.disabled=true; btn.textContent=T("vikings.caps.install.busy");
  const r=await rpc("caps.install");
  if(r!==FAIL){
    const ok=(r.results||[]).filter(x=>x.installed).length;
    const still=(r.stillMissing||[]).length;
    toast(still
      ?"ᚦ "+T("vikings.caps.install.partial.toast",{installed:ok,missing:still})
      :"ᚠ "+T("vikings.caps.install.done.toast",{count:ok}));
    const caps=await rpc("caps.get");
    if(caps!==FAIL&&caps) S.caps=caps;
  }
  btn.disabled=false; btn.textContent=T("vikings.caps.install.label");
  capsInstallBusy=false;
  renderCaps();
});

/* ---------- TOGGLES ---------- */
$$("[data-t]").forEach(t=>t.addEventListener("click",()=>{t.classList.toggle("on");syncAdvGates();}));

/* ---------- UPKEEP (app self-update + start with Windows) ---------- */
wireCollapsible("upkeepHead",$("#upkeepBody"),$("#upkeepCard"));

/* Auto-update has nothing to work from while update checking is off: C# reads the pair
   the same way (AppUpdateService.MaySelfUpdate), so the switch is dimmed and the click is
   turned away rather than storing a choice that would not be honoured. The guard sits on
   the card in the CAPTURE phase on purpose - the generic [data-t] handler above is
   registered first, so a listener on the switch itself would see the click too late. */
/* Reads the switch off the element rather than through swOn(): this runs once while the file
   is still being evaluated, and T is declared further down, which put it in the temporal
   dead zone and threw on every load. */
function syncUpkeepGates(){
  const row=$("#rowAutoUpdApp"), sw=$("#tCheckUpd");
  if(!row||!sw) return;
  const off=!sw.classList.contains("on");
  row.classList.toggle("gated",off);
  row.title=off?T("hearth.upkeep.auto_update.gated.title"):"";
}
$("#upkeepBody")?.addEventListener("click",e=>{
  const t=e.target;
  if(!t||typeof t.closest!=="function"||!t.closest("#tAutoUpdApp")) return;
  if($("#tCheckUpd")?.classList.contains("on")) return;
  e.stopPropagation(); e.preventDefault();
  toast("ᚦ "+T("hearth.upkeep.auto_update.gated.toast"));
},true);
$("#tCheckUpd")?.addEventListener("click",syncUpkeepGates);
syncUpkeepGates();

/* ---------- THE SECOND MOD SITE (Hexium) ----------
   Turning the switch on IS the consent: there is no separate notice anywhere, so the
   switch has to carry the whole of what it means. Both halves are catalog entries,
   and the page carries the English of both so the card reads correctly on the frame
   before the catalog lands. */
function renderHexiumCopy(){
  const label=$("#hexiumSwitchLabel"); if(label) label.textContent=T("hearth.upkeep.hexium.label");
  const help=$("#hexiumHelp"); if(help) help.textContent=T("hearth.upkeep.hexium.note");
}
renderHexiumCopy();

/* WORLD hall: World Modifiers + Advanced Rites + Directories fold like the Upkeep card */
["secWorldMods","secRites","secDirs"].forEach(id=>{
  const el=document.getElementById(id);
  wireCollapsible(id,el?el.nextElementSibling:null,el);
});
async function initUpkeep(){
  const up=await rpc("userprefs.get");
  /* Every switch on this card rides in on one save, so a click on any one of them writes
     the state of all of them. That is only honest once the card has been painted from an
     answer that arrived: with the read failed the switches are whatever the document
     shipped with, and persisting that would turn settings the host never touched, the one
     that writes files unattended among them. So the save waits for a read that worked. */
  let loaded=false;
  if(up!==FAIL&&up){
    setT("tCheckUpd",up.CheckForUpdates);
    setT("tAutoUpdApp",up.AutoUpdateBakaLoader);
    setT("tAutoUpdMods",up.AutoUpdateMods);
    setT("tBepMaint",up.BepInExMaintained);
    setT("tUseHexium",up.UseHexiumSource);
    /* The Mods header names the sites a scan reads, so it has to be told on load and not
       only when the switch moves under a host's finger. */
    setHexiumSource(up.UseHexiumSource);
    setT("tStartWin",up.StartWithWindows);
    setT("tStartMin",up.StartMinimized);
    setT("tShareStats",up.ShareAnonymousStats);
    /* Lives on the World hall beside the password box, but it is a user setting and it
       rides in on the same document, so it is read here with the rest of them. */
    setT("tPwCheck",up.EnablePasswordValidation);
    /* The two app wide paths, variables filled in, which is what an empty Directories box
       falls back to. Read here because they arrive on this same document, and set BEFORE
       the redraw below so both placeholders are right on the first painting of the hall. */
    S.userPaths={exe:up.DefaultServerExePath||null,dir:up.DefaultSaveDataFolderPath||null};
    /* The switch reads "Show Norse names", so it sits at the INVERSE of the stored
       PlainTerminology flag. The pref keeps its original meaning and its original key,
       so nothing on disk has to be migrated. */
    setT("tPlainTerms",!up.PlainTerminology);
    PLAIN=!!up.PlainTerminology;
    /* The player-message choice rides in on this same document. It is read here rather
       than asked for on its own, and the select is drawn from it before applyTerms draws
       everything else, so the card never shows a choice the host did not make. */
    LANG.playerMessages=String(up.PlayerMessageLanguage||"same");
    renderPlayerMsgLang();
    applyTerms();
    syncUpkeepGates();
    if(up.AppVersion) $("#blVersion").textContent="v"+up.AppVersion;
    heraldApply(up); /* Herald hall shares the same DTO */
    S.domain=(up.CustomJoinDomain||"").trim()||null;
    renderWaystone();
    loaded=true;
  }
  /* the generic [data-t] handler already flipped .on before these fire, so just persist */
  const save=()=>loaded&&rpc("userprefs.save",{prefs:{CheckForUpdates:swOn("tCheckUpd"),AutoUpdateBakaLoader:swOn("tAutoUpdApp"),AutoUpdateMods:swOn("tAutoUpdMods"),BepInExMaintained:swOn("tBepMaint"),UseHexiumSource:swOn("tUseHexium"),StartWithWindows:swOn("tStartWin"),StartMinimized:swOn("tStartMin"),ShareAnonymousStats:swOn("tShareStats"),PlainTerminology:!swOn("tPlainTerms")}});
  $("#tCheckUpd").addEventListener("click",()=>{
    save();
    /* the standing update row says whether it installs itself, and with checking off it
       installs nothing at all, so the row has to follow this switch too */
    if(CONDITIONS.has("appUpdate")) conditionAppUpdate(APP_UPDATE_V);
    /* the dialog reads both switches off the app, not off these elements */
    refreshAppUpdateInfo();
  });
  $("#tAutoUpdApp").addEventListener("click",()=>{
    save();
    /* the standing update row says whether it installs itself, so it has to follow
       the switch rather than keep whatever was true when it was raised */
    if(CONDITIONS.has("appUpdate")) conditionAppUpdate(APP_UPDATE_V);
    refreshAppUpdateInfo();
  });
  $("#tAutoUpdMods").addEventListener("click",save);
  /* The loader's own switch. The row above the mods table reads it, so it is redrawn
     here rather than left to say the opposite of the switch until the next status. */
  $("#tBepMaint")?.addEventListener("click",()=>{
    save();
    const on=swOn("tBepMaint");
    if(S.bepinex){S.bepinex.maintained=on;S.bepinex.maintenanceAsked=true;renderMods();}
    toast("ᛒ "+(on?T("hearth.upkeep.bepinex.on.toast"):T("hearth.upkeep.bepinex.off.toast")));
  });
  /* The second site goes on or off here and nowhere else. Off, BakaLoader opens no
     connection to hexium.gg at all, so the rows lose their Hexium marks on the next
     scan rather than the moment the switch moves. */
  $("#tUseHexium")?.addEventListener("click",()=>{
    save();
    setHexiumSource(swOn("tUseHexium"));
    toast("ᚺ "+(S.hexium
      ?T("hearth.upkeep.hexium.on.toast")
      :T("hearth.upkeep.hexium.off.toast")));
  });
  $("#tStartWin").addEventListener("click",save);
  $("#tStartMin").addEventListener("click",save);
  $("#tShareStats").addEventListener("click",save);
  $("#tPlainTerms").addEventListener("click",()=>{PLAIN=!swOn("tPlainTerms");save();applyTerms();});
}

/* ---------- HERALD (Discord sharing · one self-editing status post) ----------
   The C# DiscordStatusService owns the post: it debounces edits and PATCHes the
   same message forever. This hall only reads/writes prefs + the 3 discord.* RPCs. */
let HERALD_HAS_POST=false;
const HERALD_URL_RE=/^https:\/\/((ptb|canary)\.)?discord(app)?\.com\/api\/(v\d+\/)?webhooks\/\d+\/[\w-]+/;
/* The Discord hall's opening sentence. One entry with one named slot, and the bold
   goes round the slot VALUE at this call site rather than into the entry, so the
   catalog keeps its no-markup rule and a translator gets a whole sentence with the
   emphasised word somewhere they can move it. */
function heraldRenderIntro(){
  const el=$("#heraldIntro"); if(!el) return;
  el.innerHTML=esc(T("herald.intro.body",{one:"\u0000one\u0000"}))
    .split("\u0000one\u0000").join("<strong>"+esc(T("herald.intro.one"))+"</strong>");
}
function heraldRenderPost(){
  const el=$("#heraldPostStat"); if(!el) return;
  el.textContent=HERALD_HAS_POST?T("herald.post.placed"):T("herald.post.none");
}
function heraldApply(up){
  setT("tHerald",up.DiscordSharingEnabled);
  setT("tHeraldAddr",up.DiscordShareAddress);
  setT("tHeraldPass",up.DiscordSharePassword);
  setT("tHeraldEvents",up.DiscordEventPosts);
  $("#heraldUrl").value=up.DiscordWebhookUrl||"";
  $("#heraldThread").value=up.DiscordWebhookThreadId||"";
  HERALD_HAS_POST=!!up.HasDiscordStatusMessage;
  heraldRenderPost();
}
async function refreshHerald(){
  if(!Native.available) return; /* preview keeps whatever the user clicked */
  const up=await rpc("userprefs.get");
  if(up!==FAIL&&up) heraldApply(up);
}
function heraldSave(extra){
  return rpc("userprefs.save",{prefs:Object.assign({
    DiscordSharingEnabled:swOn("tHerald"),
    DiscordShareAddress:swOn("tHeraldAddr"),
    DiscordSharePassword:swOn("tHeraldPass"),
    DiscordEventPosts:swOn("tHeraldEvents"),
    DiscordWebhookUrl:$("#heraldUrl").value.trim(),
    DiscordWebhookThreadId:$("#heraldThread").value.trim()
  },extra||{})});
}
["tHerald","tHeraldAddr","tHeraldPass","tHeraldEvents"].forEach(id=>
  $("#"+id).addEventListener("click",()=>heraldSave()));
/* What the webhook line is saying, as a fact rather than as text already on screen.
   It used to be four textContent writes and no state at all, which meant a language
   switch left whatever the last keystroke had put there: the line is the one place on
   this hall that says whether the host's webhook answered, and reading it in the
   language before the switch is worse than reading nothing.
   An id where the sentence is ours, and `text` where it is the host side's own words,
   which a catalog cannot reword and which are shown exactly as they arrived. */
let HERALD_URL_STAT={cls:"wiz-stat dim",wordId:"herald.hook.stat.idle"};
function heraldRenderUrlStat(){
  const el=$("#heraldUrlStat"); if(!el) return;
  const s=HERALD_URL_STAT||{};
  el.className=s.cls||"wiz-stat dim";
  el.textContent=(s.mark?s.mark+" ":"")+(s.text!=null?s.text:T(s.wordId||"herald.hook.stat.idle",s.params));
}
{
  const inp=$("#heraldUrl");
  let t=null;
  inp.addEventListener("input",()=>{
    clearTimeout(t);
    const v=inp.value.trim();
    if(!v){
      HERALD_URL_STAT={cls:"wiz-stat dim",wordId:"herald.hook.stat.idle"};heraldRenderUrlStat();
      t=setTimeout(()=>heraldSave(),400);return;
    }
    HERALD_URL_STAT={cls:"wiz-stat dim",wordId:"common.stat.checking"};heraldRenderUrlStat();
    t=setTimeout(async()=>{
      const r=Native.available?await rpc("discord.validate",{url:v})
        :{ok:HERALD_URL_RE.test(v),name:"preview-hook",error:T("herald.hook.stat.malformed")};
      if(r===FAIL) return;
      /* Named and unnamed are two sentences, not one with a ternary in the middle: a
         webhook that answered without giving a name must not leave a dangling separator.
         A refusal the host side worded itself is kept as text, because no id came with it. */
      HERALD_URL_STAT=r.ok
        ?{cls:"wiz-stat ok",mark:"ᛉ",
          wordId:r.name?"herald.hook.stat.answers":"herald.hook.stat.answers.unnamed",
          params:r.name?{name:r.name}:null}
        :(r.error?{cls:"wiz-stat bad",mark:"ᚦ",text:r.error}
                 :{cls:"wiz-stat bad",mark:"ᚦ",wordId:"herald.hook.stat.no_answer"});
      heraldRenderUrlStat();
      if(r.ok) heraldSave();
    },500);
  });
  $("#heraldThread").addEventListener("change",()=>heraldSave());
}
$("#heraldPublish").addEventListener("click",async()=>{
  const url=$("#heraldUrl").value.trim();
  if(!url){toast("ᚦ "+T("herald.publish.no_url.toast"));return;}
  setT("tHerald",true); /* publishing implies sharing is on */
  await heraldSave();
  if(!Native.available){HERALD_HAS_POST=true;heraldRenderPost();toast("ᚺ "+T("herald.publish.preview.toast"));return;}
  const r=await rpc("discord.publish");
  if(r===FAIL) return;
  if(r.ok){
    HERALD_HAS_POST=true;heraldRenderPost();
    toast("ᚺ "+T("herald.publish.done.toast"));
    logLine("ok","[Herald] Discord status post published");
  }else toast("ᚦ "+(r.error||T("herald.publish.failed.toast")));
});
$("#heraldRemove").addEventListener("click",async()=>{
  if(!Native.available){HERALD_HAS_POST=false;heraldRenderPost();toast("ᚺ "+T("herald.remove.preview.toast"));return;}
  const r=await rpc("discord.remove");
  if(r===FAIL) return;
  if(r.ok){
    HERALD_HAS_POST=false;heraldRenderPost();
    toast("ᚺ "+T("herald.remove.done.toast"));
    logLine("warn","[Herald] Discord status post removed");
  }else toast("ᚦ "+(r.error||T("herald.remove.failed.toast")));
});
$("#heraldWizBtn").addEventListener("click",()=>heraldWizard());

/* ---------- CONTEXT MENU (live, JS-positioned) ---------- */
const ctxEl=$("#ctxMenu");
/* Opens the row menu again where it stands, in the words that are current now. A menu
   that named no rebuilder is closed instead: it is a transient the host opened a moment
   ago, and half of it in the language before would read as a bug rather than a menu. */
function redrawContextMenu(){
  if(!ctxEl.classList.contains("open")) return false;
  const again=CTX_REDRAW;
  if(typeof again!=="function"){ctxClose();return false;}
  again();
  return true;
}
function ctxClose(){ctxEl.classList.remove("open");ctxEl.style.display="none";ctxEl._items=null;CTX_REDRAW=null;}
/* What would open this menu again, at the same corner, in the words that are current
   now. Same reason as the dialog above: a menu is innerHTML written once. */
let CTX_REDRAW=null;
/**
 * Opens the row menu.
 * @param {number} x window coordinates of the corner it hangs from
 * @param {number} y
 * @param {string} head the heading, already worded
 * @param {Array} items the rows, each already worded
 * @param {function} [again] opens this same menu from state, for the language switch
 */
function ctxOpen(x,y,head,items,again){
  CTX_REDRAW=typeof again==="function"?again:null;
  ctxEl.innerHTML=`<div class="ctxhead">${esc(head)}</div>`+items.map((it,i)=>it==="hr"?"<hr>":
    `<div class="ci${it.danger?" danger":""}${it.disabled?" disabled":""}" data-i="${i}"${it.disabled&&it.tip?` title="${esc(it.tip)}"`:""}><span class="r">${it.r}</span><span class="cilbl">${esc(it.label)}</span></div>`
  ).join("");
  ctxEl._items=items;
  ctxEl.style.left="0px"; ctxEl.style.top="0px";
  ctxEl.style.display="block"; ctxEl.classList.add("open");
  const r=ctxEl.getBoundingClientRect();
  ctxEl.style.left=Math.max(4,Math.min(x,innerWidth-r.width-8))+"px";
  ctxEl.style.top=Math.max(4,Math.min(y,innerHeight-r.height-8))+"px";
}
ctxEl.addEventListener("click",e=>{
  const ci=e.target.closest(".ci"); if(!ci) return;
  e.stopPropagation();
  const it=(ctxEl._items||[])[+ci.dataset.i];
  if(!it||it.disabled) return;
  if(it.confirm&&!ci.classList.contains("confirm")){
    ci.classList.add("confirm");
    ci.querySelector(".cilbl").textContent=T("common.ctx.confirm",{label:it.label.replace(/…$/,"")});
    return;
  }
  ctxClose(); it.fn();
});
document.addEventListener("click",e=>{if(!e.target.closest("#ctxMenu"))ctxClose();});
document.addEventListener("keydown",e=>{
  if(e.key!=="Escape") return;
  ctxClose();
  if($("#modalBg").classList.contains("open")) modalClose();
});
window.addEventListener("blur",ctxClose);

/* ---------- MODALS ---------- */
const modalBg=$("#modalBg");
/* What would build the open dialog AGAIN, from state. A dialog is written into the
   page once with innerHTML and never drawn again, so it is the one surface a walk
   cannot reach: its words were chosen the moment it opened. An opener that can rebuild
   itself hands that rebuilder to modalOpen, and the language switch replays it.
   A flow that reads before it draws reads AGAIN when this fires. Every one of them is
   a read, so that costs a round trip and nothing else; a read that fails closes the
   dialog with the same toast it would have shown the first time.
   The spawn picker used to be the one that lost something real: it reopened with the
   player it was aimed at and nothing else, because the search text and the item it had
   picked lived in the closure that is being replaced. They live beside the rebuilder
   now, the way promptModal keeps what was typed, so the switch costs it the same round
   trip it costs everything else and nothing more. */
let MODAL_REDRAW=null;
/* True while a dialog is on screen. */
function modalIsOpen(){return modalBg.classList.contains("open");}
/* Draws the open dialog again in the words that are current now. Answers false when
   there is nothing open, and when what is open was handed its sentences already
   worded and named no rebuilder: that dialog keeps the wording it opened with, and
   says which one it was rather than leaving a reader to wonder. */
function redrawOpenModal(){
  if(!modalIsOpen()) return false;
  if(typeof MODAL_REDRAW!=="function"){
    console.warn("[i18n] the open dialog named no rebuilder and keeps its wording");
    return false;
  }
  MODAL_REDRAW();
  return true;
}
function modalClose(){
  wgTipClose();               // a dial panel must not outlive the dialog that opened it
  releaseModalScrollCues();   // let go of this modal's panes before the nodes are dropped
  modalBg.classList.remove("open");
  modalBg.innerHTML="";
  MODAL_REDRAW=null;
  updateToastLift();
}
modalBg.addEventListener("mousedown",e=>{if(e.target===modalBg)modalClose();});
/**
 * Puts one dialog on screen.
 * @param {string} html the dialog, worded and escaped by the caller
 * @param {function} [again] rebuilds this same dialog from state, for the language
 *   switch. Left out, the dialog keeps the words it opened with.
 */
function modalOpen(html,again){
  /* Several flows swap one modal for the next without closing first (the Barrow's
     world drill-down, a wizard step). The outgoing panes are discarded right here,
     so they are released here too. */
  releaseModalScrollCues();
  MODAL_REDRAW=typeof again==="function"?again:null;
  modalBg.innerHTML=`<div class="modal">${html}</div>`;
  modalBg.classList.add("open");
  esWire(modalBg);
  refreshScrollCues();
  updateToastLift();
  return modalBg.firstElementChild;
}
/**
 * Runs `fn` once the dialog that is open right now has gone, however it goes: a button,
 * Escape, or a click on the backdrop. Nothing else can see a dismissal, because a
 * dismissal is the absence of a click on anything.
 * Used where a QUESTION sits in front of something the host actually asked for: waving
 * the question away is an answer to the question, never a cancellation of the press.
 * @param {function} fn what to run when the dialog closes
 */
function onModalDismissed(fn){
  if(!modalIsOpen()){fn();return;}
  const ob=new MutationObserver(()=>{
    if(modalIsOpen()) return;
    ob.disconnect();
    fn();
  });
  ob.observe(modalBg,{attributes:true,attributeFilter:["class"]});
}
/* A sentence, or a function that words one. Every call site that wants its dialog to
   follow a language switch hands in the function, an arrow that asks the catalog for the
   asks it again when the words change. A plain string still works and reads the same;
   it simply keeps the wording it was given. */
const worded=v=>typeof v==="function"?String(v()):String(v==null?"":v);
function promptModal(title,placeholder,onOk,check){
  /* What is typed survives the redraw. Losing a half-typed name to a language switch
     would be the same class of loss a reload was rejected for. */
  let typed="";
  /* check, when a caller hands one in, says what is wrong with what was typed, or ""
     when nothing is. It is asked on the press, and a refusal keeps the dialog open with
     the name still in the box rather than shutting it and leaving the host to open it
     again and type the whole name again to fix one character of it.
     refused only says that a press was turned down; the WORDS are asked for again on
     every redraw, so a language switch re-words the line the way it re-words the rest. */
  let refused=false;
  const again=()=>{
    const problem=refused&&check?String(check(typed)||""):"";
    const m=modalOpen(
      `<div class="mtitle">${esc(worded(title))}</div>`+
      `<input type="text" id="mIn" placeholder="${esc(worded(placeholder))}" spellcheck="false" autocomplete="off">`+
      (problem?`<div class="subval" id="mInNote" style="color:var(--blood);padding:4px 2px 0">${esc(problem)}</div>`:"")+
      `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mCancel">${esc(T("common.button.cancel"))}</button><button class="btn btn-ember btn-sm" id="mOk">${esc(T("common.button.confirm"))}</button></div>`,
      again);
    const inp=m.querySelector("#mIn");
    inp.value=typed;
    /* The line goes as soon as the host starts fixing the name: it was a reply to one
       press, not a running commentary. Hidden rather than redrawn, because redrawing
       the dialog on a keystroke would take the caret with it. */
    inp.addEventListener("input",()=>{typed=inp.value;
      if(refused){refused=false;const note=m.querySelector("#mInNote");if(note)note.style.display="none";}});
    const ok=()=>{
      const v=inp.value.trim(); if(!v)return;
      if(check&&String(check(v)||"")){typed=inp.value;refused=true;again();return;}
      modalClose(); onOk(v);
    };
    m.querySelector("#mOk").addEventListener("click",ok);
    m.querySelector("#mCancel").addEventListener("click",modalClose);
    inp.addEventListener("keydown",e=>{if(e.key==="Enter")ok();});
    setTimeout(()=>inp.focus(),30);
    return m;
  };
  return again();
}
function confirmModal(title,bodyHtml,okLabel,onOk){
  const again=()=>{
    const m=modalOpen(
      `<div class="mtitle">${esc(worded(title))}</div>`+
      `<div class="mbody">${worded(bodyHtml)}</div>`+
      `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mCancel">${esc(T("common.button.cancel"))}</button><button class="btn btn-ember btn-sm" id="mOk">${esc(worded(okLabel))}</button></div>`,
      again);
    m.querySelector("#mOk").addEventListener("click",()=>{try{onOk(m);}finally{modalClose();}});
    m.querySelector("#mCancel").addEventListener("click",modalClose);
    return m;
  };
  return again();
}
/* ---------- LAUNCH GUARD (build changed / Steam update waiting) ----------
   Valheim 1.0 upgrades a world the first time it saves it, and there is no way back:
   an older server cannot read the world afterwards. So a start on a build this profile
   has not run before is put to the host first, and an unattended start (auto-start,
   scheduled restart, empty-server restart, crash recovery) is HELD until they answer.
   The banner below is that question when nobody was at the keyboard to see the prompt. */
/* Size of a waiting Steam download, or "" when nothing is actually queued.
   Steam can report a build mismatch with no bytes outstanding (the manifest buildid
   differs from TargetBuildID but the download has not been staged), and rounding that
   up to "1 MB" states a number nobody measured. */
function guardMb(bytes){const n=Number(bytes)||0;return n>0?Math.max(1,Math.round(n/1048576)):0;}
function guardBuildLabel(g){
  const to=g.currentBuildShort||T("guard.build.to.unknown");
  const from=g.lastBuildShort
    ?(g.lastVersion?T("guard.build.from.recorded.version",{build:g.lastBuildShort,version:g.lastVersion})
                   :T("guard.build.from.recorded",{build:g.lastBuildShort}))
    :T("guard.build.from.unknown");
  return {from,to};
}
/* The prompt body, shared by the modal and the banner tooltip.
   A size Steam has not staged is a whole second sentence rather than a gap in the
   middle of the first one: a bracket with nothing in it reads as a typo, and the
   two shapes are not the same sentence in every language. */
function guardBody(g){
  if(g.outcome==="updatePending"){
    const risk=T("guard.body.update_pending.risk");
    const mb=guardMb(g.pendingBytes);
    return mb>0?T("guard.body.update_pending.sized",{size:mb,risk})
               :T("guard.body.update_pending",{risk});
  }
  const b=guardBuildLabel(g);
  return T("guard.body.build_changed",
    {from_build:b.from,to_build:b.to,risk:T("guard.body.build_changed.risk")});
}
/* Puts the question to the host. onPick gets "proceed", "backup" or null (not now).
   For a waiting Steam update the button set follows how this server was installed, so
   the modal never offers an update it has no way to apply. */
async function launchGuardModal(g,onPick){
  const pending=g.outcome==="updatePending";
  /* Ask what kind of install this is before the buttons are drawn, so the host sees
     the right set on the first paint rather than a set that changes under them. */
  if(pending) await refreshUpdateInfo();
  const kind=pending?updKind(g):"";
  const canUpd=pending&&updCanUpdate(g);
  const title=pending?T("guard.title.update_pending"):T("guard.title.build_changed");
  const pick=v=>{modalClose();onPick(v);};
  /* An update already in flight takes every start off the table, this modal's
     "Start anyway" included: the files it would launch are being rewritten right now. */
  const busy=updateBlocksStart();
  const off=busy?` disabled title="${esc(updBlockMsg())}"`:"";
  const buttons=pending
    ?(canUpd&&!busy?`<button class="btn btn-ember btn-sm" id="mUpdate">${esc(T("guard.btn.update_and_start"))}</button>`:"")+
     `<button class="btn btn-ghost btn-sm" id="mAnyway"${off}>${esc(T("guard.btn.start_anyway"))}</button>`+
     `<button class="btn btn-ghost btn-sm" id="mLater">${esc(T("guard.btn.not_now"))}</button>`
    :`<button class="btn btn-ember btn-sm" id="mBackup"${off}>${esc(T("guard.btn.backup_and_start"))}</button>`+
     `<button class="btn btn-ghost btn-sm" id="mAnyway"${off}>${esc(T("guard.btn.start_no_backup"))}</button>`+
     `<button class="btn btn-ghost btn-sm" id="mLater">${esc(T("guard.btn.not_now"))}</button>`;
  /* Steam owns a library install, so opening Steam stays on offer there. It is the
     only thing on offer when BakaLoader cannot tell how the server was installed. */
  const steamLink=pending&&(kind==="steamLibrary"||kind==="unknown")
    ?`<div style="margin-top:8px"><span class="mlink" id="mSteam" role="button" tabindex="0">ᛊ&nbsp; ${esc(T("guard.btn.open_steam"))}</span></div>`
    :"";
  const m=modalOpen(
    `<div class="mtitle"><span class="r" style="margin-right:8px">ᛊ</span>${esc(title)}</div>`+
    `<div class="mbody">`+
    `<div style="margin-bottom:8px">${esc(guardBody(g))}</div>`+
    (busy?`<div class="subval" style="margin-bottom:6px">${esc(updBlockMsg())}</div>`:"")+
    (pending&&kind==="unknown"
      ?`<div class="subval" style="margin-bottom:6px">${esc(T("guard.note.unknown_install"))}</div>`
      :"")+
    (pending&&canUpd
      ?`<div class="subval" style="margin-bottom:6px">${esc(kind==="standalone"
          ?T("guard.note.standalone")
          :T("guard.note.steam_library"))}</div>`
      :"")+
    (g.hasWorlds&&!pending
      ?`<div class="subval" style="margin-bottom:6px">${esc(T("guard.note.backup"))}</div>`
      :"")+
    (g.manifest?`<div class="subval mono" style="word-break:break-all">${esc(g.manifest)}</div>`:"")+
    steamLink+
    `</div>`+
    `<div class="mbtns">${buttons}</div>`,()=>launchGuardModal(g,onPick));
  const openSteam=()=>{
    rpc("shell.openUrl",{target:"steam-downloads"});
    toast("ᛊ "+T("guard.steam.opened.toast"));
  };
  const steam=m.querySelector("#mSteam");
  steam?.addEventListener("click",openSteam);
  steam?.addEventListener("keydown",e=>{if(e.key==="Enter"||e.key===" "){e.preventDefault();openSteam();}});
  /* The primary answers the backup question by doing it: copy aside, update, start. */
  m.querySelector("#mUpdate")?.addEventListener("click",()=>{
    modalClose();
    runServerUpdate({backup:true,startAfter:true});
  });
  m.querySelector("#mBackup")?.addEventListener("click",()=>{if(updateBlocksStart()){toast("ᚦ "+updBlockMsg());return;}pick("backup");});
  m.querySelector("#mAnyway")?.addEventListener("click",()=>{if(updateBlocksStart()){toast("ᚦ "+updBlockMsg());return;}pick("proceed");});
  m.querySelector("#mLater")?.addEventListener("click",()=>pick(null));
}
/* Runs the guard for a start-shaped action. Calls go(answer) once the host has decided;
   answer is null for a normal start, or the token the native side stages for this launch. */
function withLaunchGuard(go){
  if(!Native.available){go(null);return;}
  /* One question in front of the guard, asked once ever. A start is what loads
     BepInEx, so a start is the first moment the answer matters, and both starts in
     the app come through here rather than each growing a copy of the question. */
  bepInExAskOnce(()=>launchCheckThenGo(go));
}
/* The guard itself, unchanged: ask the host side what a start would mean on this
   build, and either start or put the question up. */
function launchCheckThenGo(go){
  rpc("server.launchCheck",S.prefs?{prefs:S.prefs}:{}).then(g=>{
    if(g===FAIL||!g||g.outcome==="proceed"){go(null);return;}
    /* The modal is async now (it asks what kind of install this is first), and nothing
       awaits it, so catch here rather than leave a rejection nobody handles. */
    Promise.resolve(launchGuardModal(g,answer=>{ if(answer) go(answer); }))
      .catch(e=>{
        const detail=String(e&&e.message||"").trim();
        toast("ᚦ "+(detail?T("guard.ask.failed.toast",{detail}):T("guard.ask.failed.toast.nodetail")));
      });
  });
}

/* ---------- UPDATE THE SERVER FROM HERE ----------
   Steam knows an update is waiting long before anyone opens Steam. BakaLoader can
   apply it itself, so the host never leaves the app: a Steam-library install is
   handed to the Steam client to verify and download, and a standalone install is
   updated with steamcmd. Which of those it is decides what the buttons may offer,
   so every surface asks server.updateCheck first and offers only what is really possible.
   Progress is a condition-bar row with a real percentage, never a run of toasts. */
const UPD_PHASES=["idle","backingUp","askingSteam","downloading","verifying",
                  "runningSteamCmd","finished","failed","cancelled"];
/* The host may send the phase as its name or as the enum's ordinal, so accept both
   and compare on letters only (askingSteam, asking_steam and AskingSteam all match). */
function updPhaseKey(p){
  if(typeof p==="number") return UPD_PHASES[p]||"";
  const s=String(p==null?"":p).toLowerCase().replace(/[^a-z]/g,"");
  return UPD_PHASES.find(k=>k.toLowerCase()===s)||"";
}
/* Steam is asked to download and then simply watched, so waiting can be given up on.
   Once bytes are being written into the install folder, stopping part way would leave
   a half-written install, so the button says what is happening and does nothing. */
const UPD_CANCELLABLE={askingSteam:1,downloading:1};
/* A reason that already ends in a full stop, with another sentence about to be appended,
   reads as two stops. */
function updTrimStop(t){return String(t==null?"":t).trim().replace(/\.\s*$/,"");}
/* The bridge says outright whether a completion was the host stopping it. The older
   wording is still recognised, so a host that predates the flag is quiet about it too. */
function updWasCancelled(d,why){
  if(d&&d.cancelled===true) return true;
  if(d&&d.cancelled===false) return false;
  return /cancel/i.test(String(why||""));
}
function updMbPair(done,total){
  const mb=n=>Math.round((Number(n)||0)/1048576);
  return T("srvupd.phase.downloading.progress",{done:mb(done),total:mb(total)});
}
/* The page's own sentence for a phase it knows, or "" for one it does not.
   The host sets a message on every phase it reports, so this table used to be dead
   code: the old order took u.message first and never reached here. The page owns the
   wording for every phase it recognises, and the host's sentence is what covers the
   phases it does not (a failure, whose reason only the host knows). */
function updPhaseSentence(k,u){
  if(k==="backingUp") return T("srvupd.phase.backing_up");
  if(k==="askingSteam") return T("srvupd.phase.asking_steam");
  if(k==="downloading"){
    const total=Number(u&&u.bytesTotal)||0;
    return total>0
      ?updMbPair(u.bytesDone,total)
      :T("srvupd.phase.downloading");
  }
  if(k==="verifying") return T("srvupd.phase.verifying");
  if(k==="runningSteamCmd") return T("srvupd.phase.running_steamcmd");
  if(k==="finished") return T("srvupd.phase.finished");
  if(k==="cancelled") return T("srvupd.phase.cancelled");
  return "";
}
/* What the bar says right now. A phase the page knows wins; the host's own sentence is
   the fallback for everything else. */
function updPhaseText(u){
  const own=updPhaseSentence(updPhaseKey(u&&u.phase),u);
  if(own) return own;
  const said=String((u&&u.message)||"").trim();
  return said||T("srvupd.phase.unknown");
}
/* Normalised install kind. An answer we do not recognise counts as unknown, which is
   the shape that offers Steam and nothing else. */
function updKind(g){
  const k=String((g&&g.installKind)||S.update.installKind||"").toLowerCase();
  if(k==="steamlibrary") return "steamLibrary";
  if(k==="standalone") return "standalone";
  return "unknown";
}
/* True only when the native side said so. A host that does not carry the update RPCs
   yet answers nothing, so every surface falls back to what 1.0.0 offered. */
function updCanUpdate(g){
  if(updKind(g)==="unknown") return false;
  const c=g&&g.canUpdate;
  return c==null?!!S.update.canUpdate:!!c;
}
/* Quiet: a host without the RPC must not toast on every dashboard render. */
async function refreshUpdateInfo(){
  if(!Native.available) return S.update;
  const r=await Native.call("server.updateCheck",{}).catch(()=>null);
  if(r&&typeof r==="object"){
    S.update=Object.assign({},S.update,r);
    renderUpdatePill();
    /* The hold's primary ("Update server" or "Open Steam") is decided from this answer,
       and the bar is painted long before it lands, so repaint it here or the first paint
       is the only one the host ever sees. The same answer gates the start controls. */
    try{renderLaunchHold();}catch(_){}
    try{updatePalGating();}catch(_){}
    try{renderAppBar();}catch(_){}
  }
  return S.update;
}
/* The one place that asks the native side to run an update. */
let _updSending=false;
/* Whether the run in flight promised to start the server afterwards. It decides whether a
   failed run may put a held start back on screen: an update run from the pill or the
   palette was never a start, so there is no start to hold. */
let _updStartAfter=false;
async function runServerUpdate(opts){
  opts=opts||{};
  if(!Native.available){toast("ᛊ "+T("srvupd.preview_only.toast"));return;}
  /* running is only true once the native side has answered, so a second click during
     that round trip needs its own guard or two updates go out for one intent. */
  if(S.update.running||_updSending){toast("ᛊ "+T("srvupd.already_running.toast"));return;}
  _updSending=true;
  let r;
  try{ r=await rpc("server.update",{backup:opts.backup!==false,startAfter:!!opts.startAfter}); }
  finally{ _updSending=false; }
  if(r===FAIL) return;
  if(r&&r.started===false){
    const why=r.reason||T("srvupd.not_started.reason.fallback");
    toast("ᚦ "+T("srvupd.not_started.toast",{reason:why}));
    logLine("warn","[BakaLoader] the server update was not started: "+why);
    return;
  }
  _updHidden=false;
  _updStartAfter=!!opts.startAfter;
  S.update.running=true;
  SRV_UPDATE={profile:S.profileName,phase:"backingUp",percent:-1};
  renderUpdatePill(); renderServerUpdate(); renderLaunchHold(); renderAppBar();
  logLine("ok","[BakaLoader] server update requested"+
    (opts.startAfter?" · the server starts when it finishes":""));
}
/* The bar, the dashboard pill and the palette all ask the backup question first: the
   modal's "Update and start" is the only path that has already answered it. */
function updateBackupPrompt(startAfter){
  const m=modalOpen(
    `<div class="mtitle"><span class="r" style="margin-right:8px">ᛊ</span>${esc(T("srvupd.prompt.title"))}</div>`+
    `<div class="mbody">`+
    `<div style="margin-bottom:8px">${esc(T("srvupd.prompt.question"))}</div>`+
    `<div class="subval">${esc(T("guard.note.backup"))}</div>`+
    `</div>`+
    `<div class="mbtns">`+
      `<button class="btn btn-ember btn-sm" id="muYes">${esc(T("srvupd.prompt.backup"))}</button>`+
      `<button class="btn btn-ghost btn-sm" id="muNo">${esc(T("srvupd.prompt.no_backup"))}</button>`+
      `<button class="btn btn-ghost btn-sm" id="muCancel">${esc(T("common.button.cancel"))}</button>`+
    `</div>`,()=>updateBackupPrompt(startAfter));
  m.querySelector("#muCancel").addEventListener("click",modalClose);
  m.querySelector("#muNo").addEventListener("click",()=>{modalClose();runServerUpdate({backup:false,startAfter});});
  m.querySelector("#muYes").addEventListener("click",()=>{modalClose();runServerUpdate({backup:true,startAfter});});
}
/* ---- the update's own condition-bar row ---- */
let SRV_UPDATE=null;    // the last progress record for the active profile, or null
let _updHidden=false;   // the host dismissed the row; the update itself carries on
function renderServerUpdate(){
  const u=SRV_UPDATE;
  if(!u||_updHidden||!isActiveProfile(u.profile)){clearCondition("serverUpdate");return;}
  const k=updPhaseKey(u.phase);
  const pct=Number(u.percent);
  const det=isFinite(pct)&&pct>=0;
  const shown=det?Math.max(0,Math.min(100,Math.round(pct))):0;
  const stoppable=!!UPD_CANCELLABLE[k];
  setCondition("serverUpdate",{
    sev:"info",
    title:T("srvupd.row.title"),
    msg:updPhaseText(u),
    dismissLabel:T("srvupd.row.hide"),
    progressHtml:
      `<span class="hbprog${det?"":" indet"}" role="progressbar" aria-label="${esc(T("srvupd.row.progress.aria"))}" aria-valuemin="0" aria-valuemax="100"`+
      (det?` aria-valuenow="${shown}" title="${shown}%"`:` title="${esc(T("srvupd.row.progress.busy.title"))}"`)+
      `><i${det?` style="width:${shown}%"`:""}></i></span>`+
      (det?`<span class="hbpct">${shown}%</span>`:""),
    actionsHtml:stoppable
      ?`<button class="btn btn-ghost btn-sm" id="suCancel" title="${esc(T("srvupd.row.stop.title"))}">${esc(T("srvupd.row.stop"))}</button>`
      :`<button class="btn btn-ghost btn-sm" disabled title="${esc(T("srvupd.row.locked.title"))}">${esc(T("srvupd.row.title"))}</button>`,
    again:renderServerUpdate,
    wire:bar=>{
      bar.querySelector("#suCancel")?.addEventListener("click",()=>{
        /* The bridge answers whether it actually stopped anything. Once files are being
           written it cannot, and the one line the host gets comes from the completion
           event, so nothing is claimed here that the run has not done yet. */
        rpc("server.updateCancel",{}).then(r=>{
          if(r===FAIL) return;
          if(r&&typeof r==="object"&&r.cancelled===false)
            toast("ᚦ "+T("srvupd.cancel_refused.toast"));
        });
      });
    },
    onDismiss:()=>{_updHidden=true;},   // hides the row only; nothing is cancelled
  });
}
/* ---- the three inbound events, as named handlers so the preview harness can drive
       exactly what the native host drives ---- */
/* ---- the window's own countdown chip ----
   The host side sends an id and a number, never a sentence. The chip is read by whoever
   is at this window, in this window's language, while the announcement the same countdown
   puts in the game chat is written in the players' one: two audiences, two catalogs, and
   the only side that knows which language this window is in is this one.
   The tier (hours, minutes, seconds) is decided once on the host side, where the players'
   announcement decides it, and only the words for it are written here. The raw number
   rides in pluralValue for the reason atlasAgeSay puts it there: a slot already carrying
   the number written out in the host's own notation cannot be read back, and ar-EG writes
   1 as a character Number() answers NaN for. */
const COUNTDOWN_TIERS={hoursId:"hearth.countdown.hours",minutesId:"hearth.countdown.minutes",
                       secondsId:"hearth.countdown.seconds"};
/* COUNTDOWN-CHIP-BEGIN (pure - no DOM; driven by scripts/ui/countdown_chip_probe.js) */
function countdownChipWords(d){
  if(!d||!d.id) return "";
  if(d.id==="restart_now") return T("hearth.countdown.restart_now");
  if(d.id!=="restart_in") return "";
  const id=COUNTDOWN_TIERS[String(d.unit||"minutes")+"Id"]||COUNTDOWN_TIERS.minutesId;
  const n=Number(d.count)||0;
  const L=intl();
  const written=L?L.fmtNumber(n,{maximumFractionDigits:0,useGrouping:false}):String(n);
  return T("hearth.countdown.restart_in",{time:T(id,{count:written,pluralValue:n})});
}
/* COUNTDOWN-CHIP-END */

function onUpdateProgress(d){
  if(!d||!isActiveProfile(d.profile)) return;
  SRV_UPDATE=d;
  S.update.running=true;
  if(d.line) logLine("info","[update] "+d.line);
  renderUpdatePill(); renderServerUpdate(); renderLaunchHold(); renderAppBar();
}
function onUpdateDone(d){
  if(d&&!isActiveProfile(d.profile)) return;
  SRV_UPDATE=null; _updHidden=false; _updStartAfter=false;
  S.update.running=false; S.update.updatePending=false;
  if(d&&d.buildId) S.update.buildId=d.buildId;
  clearCondition("serverUpdate");
  clearLaunchHold();       // the question that raised the hold has been answered
  renderUpdatePill();
  toast("ᛊ "+(d&&d.startAfter?T("srvupd.done.starting.toast"):T("srvupd.phase.finished")));
  logLine("ok","[BakaLoader] the server update finished"+(d&&d.buildId?" · build "+d.buildId:""));
  renderAppBar();
  refreshUpdateInfo();
}
function onUpdateFailed(d){
  if(d&&!isActiveProfile(d.profile)) return;
  const startAfter=_updStartAfter;
  SRV_UPDATE=null; _updHidden=false; _updStartAfter=false;
  S.update.running=false;
  clearCondition("serverUpdate");
  renderUpdatePill();
  const why=updTrimStop(d&&d.reason);
  /* Stopping the wait is the host's own decision, so it is said once, quietly, and
     nothing turns red. The row that stood before this update is put back untouched. */
  if(updWasCancelled(d,why)){
    logLine("info","[BakaLoader] the server update was stopped. Steam keeps downloading on its own.");
    toast("ᛊ "+T("srvupd.stopped.toast"));
    renderLaunchHold(); renderAppBar();
    refreshUpdateInfo();
    return;
  }
  const said=why||T("srvupd.failed.reason.fallback");
  toast("ᚦ "+T("srvupd.failed.toast",{reason:said}));
  /* A held start is put back with the failure written across it. An update run from the
     pill or the palette held no start, so there is none to put back and none to mention. */
  if(!LAUNCH_HOLD&&startAfter&&S.update.updatePending)
    LAUNCH_HOLD={outcome:"updatePending",pendingBytes:S.update.pendingBytes,profile:S.profileName};
  logLine("err","[BakaLoader] the update did not finish: "+said+"."+
    (LAUNCH_HOLD?" The server was not started.":""));
  if(LAUNCH_HOLD){LAUNCH_HOLD.updateError=said;renderLaunchHold();}
  renderAppBar();
  refreshUpdateInfo();
}
/* Preview and walk-harness seam: the same three handlers the native events call, plus
   a way to seed what a check would have answered. Nothing here reaches a real server. */
window.BakaPreview={
  updateInfo:o=>{S.update=Object.assign({},S.update,o||{});renderUpdatePill();renderLaunchHold();},
  updateProgress:onUpdateProgress,
  updateDone:onUpdateDone,
  updateFailed:onUpdateFailed,
  launchGuard:(g,cb)=>Promise.resolve(launchGuardModal(g||{outcome:"updatePending"},cb||(()=>{}))),
  launchHold:g=>setLaunchHold(g),
  /* The bulk mod update seam: seed the queued rows, feed the same mods.updateProgress
     events the native host feeds, then end the run (what the RPC returning does live). */
  modUpdateStart:fullNames=>{
    S.modsUpdating=true; S.modRowStatus={};
    (fullNames||[]).forEach(fn=>{S.modRowStatus[fn]={phase:"queued"};});
    MOD_UPD={phase:"checking",total:0,done:0,current:null,index:0};
    renderModUpdateProgress(); renderMods();
  },
  modUpdateProgress:onModUpdateProgress,
  modUpdateEnd:()=>{
    S.modsUpdating=false; MOD_UPD=null; S.modRowStatus={};
    renderModUpdateProgress(); renderMods();
  },
  /* The language seam. Hands a catalog straight to the lookup and redraws the window
     in place, which is the whole of what the titlebar's globe will do once there is
     one: there is no other path and no reload anywhere in it.
     It is here rather than behind a native check because this is exactly what a walk
     needs - WebUI/i18n/xx.json is a generated pseudo-locale that wraps every English
     sentence and pads it by a third, so a screenshot of the page under it shows every
     surface that did NOT follow the switch as plain English, and every one that did as
     a bracketed one. Left without a catalog it just redraws in the language already
     loaded, which is how the terminology switch is walked.
     @param {string} code a BCP 47 tag
     @param {object} [catalog] a parsed strings.json; left out, only the redraw runs */
  setLanguage:(code,catalog)=>{
    if(catalog&&window.I18N) window.I18N.load(catalog,code);
    else if(window.I18N&&code) window.I18N.setLocale(code);
    return applyLanguage(code);
  },
  /* The kill sweep's two seams. The dialog is behind a native check in the palette,
     because the command it builds goes over RCON and a browser has no server to send it
     to; the DIALOG itself is all page, so a walk opens it by name and photographs every
     scope. The second hands a server's answer straight to the reader and gives back the
     toast it would have raised, so all seven endings can be seen without a sweep.
       killAll        opens the dialog, as the palette does
       killAllScope   seeds what is chosen and typed, then redraws
       killAllSaid    one reply, read and worded, exactly as the real path words it */
  killAll:()=>{killAllModal();return modalIsOpen();},
  killAllScope:o=>{
    KILL_SCOPE=Object.assign(killAllScopeBlank(),KILL_SCOPE,o||{});
    if(modalIsOpen()) redrawOpenModal();
    return Object.assign({},KILL_SCOPE);
  },
  killAllSaid:reply=>({read:killAllReply(reply),toast:killAllToast(killAllReply(reply))}),
  /* The roster the radius scope is measured from. A walk wants the EMPTY case as much as
     the full one: with nobody online the dialog cannot serve a radius at all, and the
     preview's own four players can never show that state. */
  players:list=>{S.players=Array.isArray(list)?list:[];renderPlayers();return S.players.length;},
  /* The BepInEx seam. Both rows ride on server.status, which nothing in a browser can
     push, so a walk hands the same block straight to the raiser the status handler calls.
     bepinexNotice is the refusal's row, raised live from the rpc catch. */
  bepinex:(bep,status)=>{conditionBepInEx(bep||null,status||"Running");return CONDITIONS.size;},
  bepinexNotice:()=>{noticeBepInExMaintained();return CONDITIONS.size;},
  /* The row and its two dialogs. The row rides on bepinex.status, which nothing in a
     browser can answer, so a walk hands the DTO straight in; the bar rides on the
     event the host posts while it writes, and the dialogs are opened by name. */
  bepinexStatus:dto=>{S.bepinex=dto||null;renderMods();return S.bepinex;},
  bepinexProgress:d=>{BEP_WRITING=true;onBepInExProgress(d);return BEP_PROGRESS;},
  bepinexProgressEnd:()=>{BEP_WRITING=false;bepInExProgressDone();return BEP_PROG_OPEN;},
  bepinexFirstDialog:()=>{BEP_ASKED_THIS_RUN=false;bepInExAskOnce(()=>{});return modalIsOpen();},
  bepinexOffer:()=>{bepInExOfferModal(null);return modalIsOpen();},
  /* The window-state seam. The host pushes win.state; nothing in a browser can, so a
     walk drives this instead and the button swaps its glyph exactly as it does live. */
  winState:maximized=>{
    document.body.dataset.win=maximized?"max":"normal";
    renderWinState();
    return document.body.dataset.win;
  },
  /* The Directories seam. Every state of that section depends on an answer only the app
     can give (the path in force for this server, the app wide default behind it, what
     paths.check found at a typed path), so a browser can reach none of them and a walk
     could screenshot exactly one state: empty. This hands all three straight in.
       prefs    the four keys profiles.get adds (EffectiveServerExePath and its source word)
       defaults {exe,dir} the app wide paths the placeholders read
       typed    {exe,dir} what to put in each box, as if the host had typed it
       checks   {exe,dir} a paths.check answer each, or null for no note
       snapshot true to call what is on screen the saved state, so nothing reads as unsaved
     Answers with what is on screen afterwards, so a walk can assert rather than only
     photograph. Nothing here reaches a real path or writes anything anywhere. */
  worldDirs:o=>{
    const d=o||{};
    if(d.prefs) S.prefs=Object.assign({},S.prefs||{},d.prefs);
    if(d.defaults) S.userPaths=Object.assign({},S.userPaths,d.defaults);
    if(d.typed) for(const kind in d.typed){
      const row=WORLD_DIRS.find(x=>x.kind===kind), box=row?$("#"+row.input):null;
      if(box) box.value=d.typed[kind];
    }
    if(d.snapshot) worldFormSnapshot();
    if(d.checks) for(const kind in d.checks){
      const row=WORLD_DIRS.find(x=>x.kind===kind);
      const typed=row?String($("#"+row.input)?.value||"").trim():"";
      S.pathChecks[kind]=d.checks[kind]?Object.assign({forPath:typed},d.checks[kind]):null;
    }
    const dirty=renderWorldDirty();
    return {
      dirty,
      exe:$("#curServerExe")?.textContent||"",
      exeSource:$("#curServerExeSrc")?.textContent||"",
      save:$("#curSaveDir")?.textContent||"",
      saveSource:$("#curSaveDirSrc")?.textContent||"",
      openTitle:$("#btnOpenSrv")?.title||"",
      notice:!!$("#worldUnsaved")?.classList.contains("on"),
      band:!!$("#page-world")?.classList.contains("unsaved-room"),
      dirsOpen:!!$("#secDirs")?.classList.contains("open"),
    };
  },
  /* The globe's seam. Every row of that menu is an answer from the host about packs on
     disk, a manifest and a download in flight, and a browser can produce none of those,
     so a walk hands each one straight in. Nothing here reaches a real pack: langDone is
     handed the same payload lang.set answers with, and its stringsUrl is whatever the
     walk wants fetched (i18n/xx.json beside the page, in the harness).
       langList     the lang.list answer, drawn as-is
       langProgress one lang.downloadProgress report, exactly as the host posts it
       langDone     the lang.set answer, which is the switch itself
       langFailed   an ending, so the row keeps its reason and its Try again
       langScript   a whole download acted out, one report at a time, so the menu can be
                    photographed at every phase without a host behind it */
  langList:o=>{
    LANG.list=o||null;
    if(o&&o.current) LANG.status=Object.assign({},LANG.status||{},{current:o.current});
    renderLangMenu(); renderPlayerMsgLang();
    return langRows().length;
  },
  langStatus:o=>{LANG.status=o||null;renderLangDot();renderLangMenu();return LANG.status;},
  langOpen:()=>{langMenuOpen();return langMenuIsOpen();},
  langClose:()=>langMenuClose(),
  langProgress:d=>{
    if(!d||!d.code) return false;
    LANG.busyCode=d.code; LANG.failed=null; LANG.prog=d;
    langPaintProgress();
    return true;
  },
  langDone:payload=>{
    LANG.busyCode=null; LANG.prog=null; LANG.failed=null;
    return switchLanguage(payload);
  },
  langFailed:(code,reasonId,reasonParams)=>{
    LANG.busyCode=null; LANG.prog=null;
    LANG.failed={code,reasonId,reasonParams:reasonParams||{}};
    renderLangMenu();
    return LANG.failed;
  },
  /* One scripted download. Each step is a real report through the same painter the host
     drives, and the pause between them is the walk's to choose. */
  langScript:async (code,steps,pause)=>{
    for(const s of (steps||[])){
      window.BakaPreview.langProgress(Object.assign({code},s));
      await new Promise(r=>setTimeout(r,Math.max(0,Number(pause)||0)));
    }
    return LANG.prog;
  },
  /* Fetches that catalog from beside the page first. Answers false when there is none,
     because a walk that silently proved nothing is worse than one that says so. */
  loadLanguage:async code=>{
    const url=window.BAKA_ASSET?window.BAKA_ASSET("i18n/"+code+".json"):"i18n/"+code+".json";
    try{
      const r=await fetch(url,{cache:"no-cache"});
      if(!r.ok) return false;
      window.BakaPreview.setLanguage(code,await r.json());
      return true;
    }catch(e){console.warn("[i18n] no catalog for "+code,e);return false;}
  },
};
/* Dashboard Server card: the waiting update, said once, where the state is said. */
function renderUpdatePill(){
  const el=$("#hUpdPill"); if(!el) return;
  const u=S.update||{};
  if(!u.updatePending||u.running){el.style.display="none";return;}
  const st=S.state||{};
  /* Read the state, and fall back to the card's own cold class so the preview shows
     the same disabled pill the app does without a state DTO. */
  const card=document.getElementById("hearthCard");
  const live=st.status?st.status!=="Stopped":!!(card&&!card.classList.contains("cold"));
  el.style.display="";
  el.textContent=T("srvupd.pill.label");
  el.disabled=!!live||!updCanUpdate(null);
  el.title=live
    ?T("srvupd.pill.live.title")
    :(updCanUpdate(null)
      ?T("srvupd.pill.ready.title")
      :(u.reason||T("srvupd.pill.blocked.title")));
}

/* ---------- CONDITION BAR ----------
   One standing condition at a time, worst first, on a severity-coloured left edge.
   A condition is something that is STILL TRUE and still wants an answer, so it stays
   on screen until it is answered or dismissed. Toasts are left to confirm what the
   host just did. The element keeps the id launchHold: the held start is one of the
   conditions it carries, and the native side still addresses it by that name. */
/* Worst first, and the order says so: the held start, then the three failures, then the
   two warnings, then the notices. A save that failed outranks a plugin that would not
   install, and both outrank a download running to plan.
   bepinexNotice sits ABOVE appUpdate for one reason: it is the only row here raised by a
   press the host just made, and every other notice is standing. A standing
   BakaLoader-update row is the ordinary case, and with the notice below it a host who
   pasted the pack link got no answer at all. The row is only half of that fix; the press
   also toasts, because one row is all this bar ever draws.
   bepinexWaiting stays BELOW appUpdate on purpose, and it is worth saying why, because a
   standing BakaLoader update does hide it most of the time. A deferred loader update is
   the one condition here that answers itself: it goes in at the next restart window with
   nobody pressing anything, and until then the BepInEx row above the mods table carries
   the same sentence in the hall the host would be looking at anyway. An app update needs
   a press. Putting the self-resolving row over the one that wants an answer would be
   swapping which of the two is hidden, for the worse. */
const CONDITION_ORDER=["launchHold","saveFailed","backupFailed","crashRelaunch",
                       "bepinexNotLoaded","serverUpdate","pluginFailure","bepinexNotice",
                       "appUpdate","bepinexWaiting","modUpdates","restartPending"];
const CONDITIONS=new Map();
function setCondition(kind,cond){
  if(!cond) CONDITIONS.delete(kind); else CONDITIONS.set(kind,cond);
  renderConditionBar();
}
function clearCondition(kind){CONDITIONS.delete(kind);renderConditionBar();}
/* Every standing condition, built again from the facts that raised it.
   setCondition stores SENTENCES, not the reasons behind them, so drawing the bar again
   re-renders words that were already chosen. A condition raised before the English
   catalog arrived - the preview raises one while app.js is still being evaluated, and a
   status event can beat the fetch in the app - would keep the ids it was built with
   forever. So each raiser hands in `again`, a closure over its own arguments, and this
   replays them. The language switch needs exactly this seam, which is why it is a field
   on the condition rather than a list of names kept somewhere else. */
function rerenderConditions(){
  for(const cond of Array.from(CONDITIONS.values()))
    if(cond&&typeof cond.again==="function"){try{cond.again();}catch(_){}}
}
function renderConditionBar(){
  const bar=$("#launchHold"); if(!bar) return;
  let kind=null,c=null;
  for(const k of CONDITION_ORDER){const v=CONDITIONS.get(k); if(v){kind=k;c=v;break;}}
  if(!c){
    bar.style.display="none"; bar.innerHTML="";
    bar.removeAttribute("data-kind"); bar.removeAttribute("data-sev");
    return;
  }
  bar.dataset.kind=kind;
  bar.dataset.sev=c.sev||"info";
  bar.innerHTML=
    `<span class="hbtitle">${esc(c.title)}</span>`+
    `<span class="hbmsg">${esc(c.msg)}</span>`+
    (c.progressHtml||"")+
    `<span class="hbacts">${c.actionsHtml||""}`+
    `<button class="btn btn-ghost btn-sm" data-cond-dismiss>${esc(c.dismissLabel||T("cond.btn.dismiss"))}</button>`+
    `</span>`;
  bar.style.display="flex";
  if(c.wire) c.wire(bar);
  bar.querySelector("[data-cond-dismiss]").addEventListener("click",()=>{
    if(c.onDismiss) c.onDismiss();
    clearCondition(kind);
  });
}

/* The held-start condition: the same question the start prompt asks, kept on screen
   because the start that raised it happened with nobody watching. */
let LAUNCH_HOLD=null;
function renderLaunchHold(){
  const g=LAUNCH_HOLD;
  if(!g||!isActiveProfile(g.profile)){clearCondition("launchHold");return;}
  /* An update in flight is answering this very question, and every action here is refused
     while it runs, so the progress row has the bar to itself. The hold object is kept, so
     a cancelled or failed update puts this row back exactly as it was. */
  if(updateBlocksStart()){clearCondition("launchHold");return;}
  const pending=g.outcome==="updatePending";
  const canUpd=pending&&updCanUpdate(g);
  /* A failed update leaves this hold standing, so it carries the reason it failed and
     every action it had before. */
  const why=g.updateError
    ?T("cond.launch.update_failed",{reason:updTrimStop(g.updateError)})+" "
    :"";
  setCondition("launchHold",{
    sev:g.updateError?"err":"warn",
    title:pending?T("cond.launch.title.update_pending"):T("cond.launch.title.build_changed"),
    msg:why+guardBody(g),
    dismissLabel:T("guard.btn.not_now"),
    actionsHtml:
      /* "Update server" takes the primary seat from "Open Steam" wherever BakaLoader
         can do the update itself; Steam stays the only offer where it cannot. */
      (canUpd?`<button class="btn btn-ember btn-sm" id="lhUpdate">${esc(T("cond.launch.update"))}</button>`:"")+
      (pending&&!canUpd?`<button class="btn btn-ghost btn-sm" id="lhSteam">${esc(T("guard.btn.open_steam"))}</button>`:"")+
      (pending?"":`<button class="btn btn-ember btn-sm" id="lhBackup">${esc(T("guard.btn.backup_and_start"))}</button>`)+
      `<button class="btn btn-ghost btn-sm" id="lhAnyway">${esc(pending?T("guard.btn.start_anyway"):T("guard.btn.start_no_backup"))}</button>`,
    again:renderLaunchHold,
    wire:bar=>{
      bar.querySelector("#lhSteam")?.addEventListener("click",()=>{
        rpc("shell.openUrl",{target:"steam-downloads"});
        toast("ᛊ "+T("guard.steam.opened.toast"));
      });
      bar.querySelector("#lhUpdate")?.addEventListener("click",()=>updateBackupPrompt(true));
      bar.querySelector("#lhBackup")?.addEventListener("click",()=>startWithAnswer("backup"));
      bar.querySelector("#lhAnyway")?.addEventListener("click",()=>startWithAnswer("proceed"));
    },
    onDismiss:()=>{
      const profile=LAUNCH_HOLD?.profile;
      LAUNCH_HOLD=null;
      rpc("server.dismissLaunchHold",{profile});
      toast("ᛊ "+T("cond.launch.dismissed.toast"));
    },
  });
}
function setLaunchHold(g){LAUNCH_HOLD=g||null;renderLaunchHold();}
function clearLaunchHold(){LAUNCH_HOLD=null;renderLaunchHold();}

/* ---- the other conditions, each raised by a real signal ---- */
function conditionSaveFailed(ms){
  setCondition("saveFailed",{sev:"err",title:T("cond.save.title"),
    /* The write time is a whole second shape rather than a bracket that is
       sometimes empty, which is the rule every composed sentence here follows. */
    msg:ms?T("cond.save.msg.timed",{ms,body:T("cond.save.body")})
          :T("cond.save.msg",{body:T("cond.save.body")}),
    actionsHtml:`<button class="btn btn-ghost btn-sm" id="cbSaga">${esc(T("cond.btn.open_log"))}</button>`,
    again:()=>conditionSaveFailed(ms),
    wire:bar=>bar.querySelector("#cbSaga").addEventListener("click",()=>goPage("saga")),
  });
}
function conditionBackupFailed(err){
  setCondition("backupFailed",{sev:"err",title:T("cond.backup.title"),
    msg:T("cond.backup.body")+(err?" "+err:""),
    actionsHtml:`<button class="btn btn-ghost btn-sm" id="cbBarrow">${esc(T("cond.backup.open"))}</button>`,
    again:()=>conditionBackupFailed(err),
    wire:bar=>bar.querySelector("#cbBarrow").addEventListener("click",()=>barrowModal()),
  });
}
function conditionCrashed(){
  const relaunch=!!(S.prefs&&S.prefs.AutoRestart);   // "Relaunch after a crash" in the World hall
  setCondition("crashRelaunch",{sev:"err",title:T("cond.crash.title"),
    msg:relaunch?T("cond.crash.body.relaunch")
                :T("cond.crash.body.down"),
    actionsHtml:`<button class="btn btn-ghost btn-sm" id="cbCrashLog">${esc(T("cond.btn.open_log"))}</button>`,
    again:()=>conditionCrashed(),
    wire:bar=>bar.querySelector("#cbCrashLog").addEventListener("click",()=>goPage("saga")),
  });
}
/* A companion plugin that would not install. The server starts anyway, on purpose, so
   without this the feature it powers simply goes missing and only the log says why.
   server.status carries the list, so this needs no RPC and no event of its own. */
let PLUGIN_FAIL_HIDDEN=null;    // the failure set the host has already waved away
function pluginFailureSig(list){
  return JSON.stringify(list.map(f=>[String(f.plugin||""),String(f.text||f.message||"")]));
}
/* The Mods hall already lists BakaLoader's own bundled plugins beside the Thunderstore
   ones, so a plugin that never landed belongs there too. Unlike the condition bar this
   line cannot be dismissed: the gap is still there after the bar has been waved away,
   and this is the hall a host opens to ask whether a plugin is present. */
function renderModsPluginNote(fails){
  const el=$("#modsPluginNote"); if(!el) return;
  const names=(fails||[]).map(f=>String(f.plugin||"").trim()).filter(Boolean);
  if(!names.length){el.style.display="none";el.textContent="";return;}
  /* "A and B", "A, B and C": a bare comma list reads as a fragment, not a sentence. */
  const who=names.length===1?names[0]
    :T("common.list.and",{names:names.slice(0,-1).join(", "),last:names[names.length-1]});
  el.style.display="";
  el.textContent=names.length===1
    ?T("mods.plugin_note.one",{names:who})
    :T("mods.plugin_note.many",{names:who});
}
function conditionPluginFailures(list,status){
  const fails=Array.isArray(list)?list.filter(Boolean):[];
  /* An empty list means the plugins are in place now, so a later failure is news again
     rather than something the host already dismissed. */
  if(!fails.length) PLUGIN_FAIL_HIDDEN=null;
  /* Only worth saying while the server this affects is up: a stopped server has no
     missing feature to explain, and the installers run again at the next start. */
  const live=status==="Running"||status==="Starting";
  renderModsPluginNote(live?fails:[]);
  if(!fails.length||!live){clearCondition("pluginFailure");return;}
  const sig=pluginFailureSig(fails);
  if(PLUGIN_FAIL_HIDDEN===sig){clearCondition("pluginFailure");return;}
  const first=fails[0];
  const more=fails.length-1;
  const text=String(first.text||first.message||"").trim()
    ||T("cond.plugin.body");
  setCondition("pluginFailure",{sev:"warn",title:T("cond.plugin.title"),
    /* The first failure said in full, then a count for the rest: four plugin sentences
       in one bar reads as noise, and the log carries every one of them. */
    msg:text+(more>0?" "+T("cond.plugin.more",{count:more}):""),
    actionsHtml:`<button class="btn btn-ghost btn-sm" id="cbPluginLog">${esc(T("cond.btn.open_log"))}</button>`,
    again:()=>conditionPluginFailures(fails,status),
    wire:bar=>bar.querySelector("#cbPluginLog").addEventListener("click",()=>goPage("saga")),
    onDismiss:()=>{PLUGIN_FAIL_HIDDEN=sig;},
  });
}
let MOD_UPDATES_HIDDEN=null;   // the waiting-update count the host has waved away
function conditionModUpdates(n){
  /* Nothing waiting any more: the updates were taken, so a later one is news again rather
     than something already dismissed. */
  if(!n){MOD_UPDATES_HIDDEN=null;clearCondition("modUpdates");return;}
  /* The Mods hall draws itself again for a great many small reasons: a sort click, a
     letter typed in the search box, a row status changing. Without this the row the host
     waved away came straight back on the next one of them. It is keyed to the count, so a
     mod that picks up an update afterwards raises the row again. */
  const key=String(n);
  if(MOD_UPDATES_HIDDEN===key){clearCondition("modUpdates");return;}
  setCondition("modUpdates",{sev:"info",title:T("cond.mods.title"),
    msg:T("cond.mods.msg",{count:n}),
    actionsHtml:`<button class="btn btn-ember btn-sm" id="cbMods">${esc(T("cond.mods.review"))}</button>`,
    again:()=>conditionModUpdates(n),
    wire:bar=>bar.querySelector("#cbMods").addEventListener("click",()=>goPage("mods")),
    onDismiss:()=>{MOD_UPDATES_HIDDEN=key;},
  });
}
/* Settings saved while the world was up. Valheim reads its whole configuration off the command
   line at launch, so a running server keeps what it started with no matter what is on disk: the
   change is real, it is saved, and it is simply not in force yet. The native side compares the
   saved settings with the live ones and sends the answer with every status refresh; this row is
   where the host finds out, and the restart it offers is the same warned one the Hearth button
   runs, never a bare stop. */
let RESTART_PENDING_HIDDEN=null;   // the saved-settings fingerprint the host has waved away
function conditionRestartPending(on,sig){
  const key=String(sig||"");
  /* Not pending any more: the server stopped, the settings were put back, or a restart took
     them in. Either way a later change is news again rather than something already dismissed. */
  if(!on){RESTART_PENDING_HIDDEN=null;clearCondition("restartPending");return;}
  if(RESTART_PENDING_HIDDEN===key){clearCondition("restartPending");return;}
  setCondition("restartPending",{sev:"info",title:T("cond.restart.title"),
    msg:T("cond.restart.body"),
    actionsHtml:`<button class="btn btn-ember btn-sm" id="cbRestartPending">${esc(T("cond.restart.act"))}</button>`,
    again:()=>conditionRestartPending(on,sig),
    wire:bar=>bar.querySelector("#cbRestartPending").addEventListener("click",()=>smartRestart()),
    /* Keyed to this exact set of saved settings: waving it away hides THESE, and the next
       change the host saves raises the row again with a fingerprint of its own. */
    onDismiss:()=>{RESTART_PENDING_HIDDEN=key;},
  });
}
/* ---- BepInEx, the framework every mod loads under ----
   Two standing conditions, both of them things that are STILL TRUE and still want an
   answer. They ride on server.status beside the plugin failures, because BepInEx is a
   fact about the install a server runs from and the status event is where this page
   already learns facts about that install. Both are absent on an older host, which
   simply means neither row is ever raised. */
function conditionBepInEx(bep,status){
  if(!bep){clearCondition("bepinexWaiting");clearCondition("bepinexNotLoaded");return;}
  conditionBepInExWaiting(bep.updateWaiting,bep.waitingProfiles);
  conditionBepInExNotLoaded(!!bep.notLoaded&&(status==="Running"));
}
/* A newer pack that cannot go in yet. Every server on one install loads one BepInEx
   through the same junctions and hard links, so the write waits for all of them rather
   than replacing a loader out from under a world that is up. */
function conditionBepInExWaiting(version,names){
  if(!version){clearCondition("bepinexWaiting");return;}
  const up=Array.isArray(names)?names.filter(Boolean):[];
  // The host keeps the row up after a defer but recounts who is blocking on every push, so
  // the list can be empty before the next restart window applies it: say so, never "Still up: .".
  setCondition("bepinexWaiting",{sev:"info",title:T("bepinex.condition.waiting.title"),
    msg:up.length?T("bepinex.condition.waiting.body",{version,names:up.join(", ")})
                 :T("bepinex.condition.waiting.body.none",{version}),
    again:()=>conditionBepInExWaiting(version,names),
  });
}
/* The server came up, the grace passed, and BepInEx never wrote its log. Nothing else in
   the app can see this: on Windows the whole loader mechanism is a DLL beside the
   executable, so there is no flag to get wrong and no answer coming back either. */
function conditionBepInExNotLoaded(on){
  if(!on){clearCondition("bepinexNotLoaded");return;}
  setCondition("bepinexNotLoaded",{sev:"warn",title:T("bepinex.condition.not_loaded.title"),
    msg:T("bepinex.condition.not_loaded.body"),
    actionsHtml:`<button class="btn btn-ghost btn-sm" id="cbBepWiki">${esc(T("bepinex.condition.not_loaded.action"))}</button>`,
    again:()=>conditionBepInExNotLoaded(on),
    wire:bar=>bar.querySelector("#cbBepWiki").addEventListener("click",()=>{
      if(Native.available) Native.call("shell.openUrl",{target:"bepinex-wiki"});
    }),
  });
}
/* The whole answer to that press: the toast AND the row.
   The bar draws exactly ONE condition, so a row alone can be stored and never drawn, and
   the host who pressed Add reads that as nothing having happened. The toast is what
   guarantees the press is answered; the row is what carries the offer to open the setting
   and stays until it is taken. Deliberately NOT part of the row's own `again` replay: a
   language switch redraws the row and must not toast again. */
function noticeBepInExMaintained(){
  toast("ᛒ "+T("bepinex.notice.already_maintained"));
  conditionBepInExMaintained();
}
/* The host tried to put BepInEx in by hand while BakaLoader is looking after it. Not a
   failure, so it is not a red toast: it is a row that says where the switch is. */
function conditionBepInExMaintained(){
  setCondition("bepinexNotice",{sev:"info",title:T("bepinex.notice.already_maintained.title"),
    msg:T("bepinex.notice.already_maintained"),
    actionsHtml:`<button class="btn btn-ember btn-sm" id="cbBepSetting">${esc(T("bepinex.notice.already_maintained.action"))}</button>`,
    again:()=>conditionBepInExMaintained(),
    wire:bar=>bar.querySelector("#cbBepSetting").addEventListener("click",()=>{
      clearCondition("bepinexNotice");
      openUpkeepBepInEx();
    }),
  });
}
/* Opens the Upkeep card, where the auto-update switch lives. */
function openUpkeepCard(){
  goPage("hearth");
  const card=$("#upkeepCard");
  if(card&&!card.classList.contains("open")) $("#upkeepHead").click();
  requestAnimationFrame(()=>{try{card.scrollIntoView({block:"nearest"});}catch(_){}});
}
/* The same card, and then the one switch the notice was about. A card with eight
   switches in it is not an answer to "where is the setting", so the row is moved to
   and lit for a moment rather than left to be found. */
function openUpkeepBepInEx(){
  openUpkeepCard();
  const row=$("#rowBepMaint"); if(!row) return;
  requestAnimationFrame(()=>{
    try{row.scrollIntoView({block:"nearest"});}catch(_){}
    flashRow(row);
  });
}
/* A short ember pulse on one row. The class is removed and put back with a reflow
   between, so a second press lights it again rather than finding the animation over. */
function flashRow(el){
  if(!el) return;
  el.classList.remove("rowflash");
  void el.offsetWidth;
  el.classList.add("rowflash");
  setTimeout(()=>el.classList.remove("rowflash"),1800);
}
let APP_UPDATE_V=null;   // the version the standing BakaLoader-update row is about
function conditionAppUpdate(v){
  APP_UPDATE_V=v==null?APP_UPDATE_V:v;
  /* Only promise the install when BOTH switches that perform it are on. This row can be
     raised by a check made before either was turned off - the host reads it afterwards -
     and either one being off means nothing installs itself, so saying otherwise is a lie
     the host acts on. C# reads the same pair (AppUpdateService.MaySelfUpdate). */
  const read=id=>{try{return swOn(id);}catch(_){return false;}};
  const checking=read("tCheckUpd"), installs=read("tAutoUpdApp");
  const auto=checking&&installs;
  /* Installing means closing BakaLoader, which takes the server with it, so what this row
     can promise turns on whether a world is up as much as on the two switches. The run state
     comes off the same answer every other surface reads, and the row is drawn again whenever
     that answer changes. */
  const running=!!(APP_UPD&&APP_UPD.anyServerRunning);
  const notAuto=running
    ?T("cond.appupd.manual.running")
    :T("cond.appupd.manual.idle");
  setCondition("appUpdate",{sev:"info",title:T("cond.appupd.title"),
    msg:(v?T("cond.appupd.body.version",{version:v}):T("cond.appupd.body.generic"))+" "+
        (auto?T("cond.appupd.auto")
             :checking?notAuto
                      :T("cond.appupd.checking_off")),
    actionsHtml:
      `<button class="btn btn-ghost btn-sm" id="cbChanges">${esc(T("cond.appupd.open"))}</button>`+
      `<button class="btn btn-ghost btn-sm" id="cbUpkeep">${esc(T("cond.appupd.upkeep"))}</button>`,
    again:()=>conditionAppUpdate(APP_UPDATE_V),
    wire:bar=>{
      /* This opens the dialog, which is where the release notes are offered along with the
         one thing the host can safely do about the release from here. Sending them straight
         to the browser skipped the part where they find out what updating would cost them,
         so the button says what it actually does. */
      bar.querySelector("#cbChanges").addEventListener("click",()=>{appUpdateModal();});
      bar.querySelector("#cbUpkeep").addEventListener("click",()=>{openUpkeepCard();});
    },
  });
}
/* Start the server carrying the host's answer to the guard. */
function startWithAnswer(answer){
  if(!S.prefs){toast("ᚦ "+T("hearth.no_profile.toast"));return;}
  /* Asked before the hold is cleared: a refused start must leave the question standing. */
  if(updateBlocksStart()){
    toast("ᚦ "+updBlockMsg());
    logLine("warn","[BakaLoader] the start was refused: "+updBlockMsg());
    return;
  }
  clearLaunchHold();
  rpc("server.start",{prefs:S.prefs,guard:answer}).then(r=>{
    if(r===FAIL||rpcRefused(r)) return;
    applyState(r);
    /* The copy has not happened yet: server.worldsBackedUp is what says how many
       worlds actually landed, and it is the only thing allowed to claim it is done. */
    toast("ᚠ "+(answer==="backup"
      ?T("hearth.start.backup.toast")
      :T("hearth.starting.toast")));
    logLine("ok","[BakaLoader] start requested · profile "+(S.profileName||"?")+" · "+answer);
  });
}

/* Server-console picker: curated commands the modded server understands
   (BakaLoaderCommander natives + devcommands staples). Clicking a complete
   command sends it; commands that take arguments prefill the input instead. */
/* The command and its argument shape are what a host types at a server console, so both
   stay verbatim; only the line that says what it does is wording. descId rather than a
   sentence, because that is how the catalog gate sees a table that holds ids.
   asks:"killall" is the one exception to "a complete command is sent on the click". The
   bare sweep strikes every hostile in every loaded zone for every player online, and a
   row in a list is one tap: the same question the palette asks is asked here, from the
   same dialog, so there is no route to the sweep that skips it. */
const CONSOLE_CMDS=[
  {cmd:"save",args:"",descId:"pal.console.cmd.save"},
  {cmd:"playerlist",args:"",descId:"pal.console.cmd.playerlist"},
  {cmd:"baka_killall",args:"",descId:"pal.console.cmd.killall",asks:"killall"},
  {cmd:"broadcast center ",args:"<message>",descId:"pal.console.cmd.broadcast"},
  {cmd:"dmg ",args:"<player> <amount>",descId:"pal.console.cmd.dmg"},
  {cmd:"tp ",args:"<player> <x,z,y | player>",descId:"pal.console.cmd.tp"},
  {cmd:"kick ",args:"<player | hostId>",descId:"pal.console.cmd.kick"},
  {cmd:"baka_spawn ",args:"<prefab> <x,z,y> [amount] [level or quality]",descId:"pal.console.cmd.spawn"},
  {cmd:"skiptime ",args:"<seconds>",descId:"pal.console.cmd.skiptime"},
  {cmd:"sleep",args:"",descId:"pal.console.cmd.sleep"},
];
function consoleModal(){
  const rows=CONSOLE_CMDS.map(c=>
    `<div class="crow" data-cmd="${esc(c.cmd)}" data-complete="${c.args?"":"1"}"${c.asks?` data-asks="${esc(c.asks)}"`:""}>`+
    `<span class="cc">${esc(c.cmd.trim())}${c.args?` <span class="ca">${esc(c.args)}</span>`:""}</span>`+
    `<span class="cd">${esc(T(c.descId))}</span></div>`).join("");
  const m=modalOpen(
    `<div class="mtitle">${esc(T("pal.console.modal.title"))}</div>`+
    `<input type="text" id="mIn" placeholder="${esc(T("pal.console.modal.placeholder"))}" spellcheck="false" autocomplete="off">`+
    `<div class="clist">${rows}</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mCancel">${esc(T("common.button.cancel"))}</button><button class="btn btn-ember btn-sm" id="mOk">${esc(T("pal.console.modal.send"))}</button></div>`,consoleModal);
  const inp=m.querySelector("#mIn");
  const ok=()=>{const v=inp.value.trim(); if(!v)return; modalClose(); sendConsole(v);};
  m.querySelector("#mOk").addEventListener("click",ok);
  m.querySelector("#mCancel").addEventListener("click",modalClose);
  inp.addEventListener("keydown",e=>{if(e.key==="Enter")ok();});
  inp.addEventListener("input",()=>{
    const q=inp.value.trim().toLowerCase();
    m.querySelectorAll(".crow").forEach(r=>{
      r.style.display=(!q||r.dataset.cmd.trim().toLowerCase().startsWith(q.split(" ")[0])||r.textContent.toLowerCase().includes(q))?"":"none";
    });
  });
  m.querySelectorAll(".crow").forEach(r=>r.addEventListener("click",()=>{
    /* The sweep asks first, wherever it is started from, and the dialog it opens is the
       one that reads the server's answer afterwards. */
    if(r.dataset.asks==="killall"){modalClose();killAllModal();return;}
    if(r.dataset.complete){modalClose();sendConsole(r.dataset.cmd);return;}
    inp.value=r.dataset.cmd;
    inp.dispatchEvent(new Event("input"));
    inp.focus();
  }));
  setTimeout(()=>inp.focus(),30);
}

/* ---------- NETWORK DRILL-DOWN (Network card click) ---------- */
function netModal(){
  const port=S.prefs?.Port??2456;
  const addrRow=(label,val,copyable)=>
    `<div class="drow"><span class="dk">${esc(label)}</span><span class="dv mono">${esc(val)}</span>`+
    (copyable&&val&&!String(val).startsWith("-")?`<span class="copychip" data-copy="${esc(val)}">${esc(T("common.chip.copy"))}</span>`:"")+`</div>`;
  const n=S.net;
  const asOf=n.at?new Date(n.at):null;
  const stat=(k,v)=>`<div class="dstat"><div class="bigval" style="font-size:20px">${v}</div><div class="subval">${k}</div></div>`;
  const online=S.players.filter(p=>p.status==="Online"||p.status==="Joining");
  const sess=online.length
    /* Same platformName() the roster uses, so one player never has two names. */
    ?online.map(p=>`<div class="drow"><span class="dk">${esc(p.displayName)}</span><span class="dv mono">${esc(playerPlatform(p)||"?")} · ${esc(p.PlayerId||"")}</span><span class="dv" style="flex:0 0 auto">${esc(T("hearth.vikings.row.joined",{when:fmtT(p.lastStatusChange)}))}</span></div>`).join("")
    :`<div class="subval" style="padding:4px 2px">${T("hearth.net.players.empty")}</div>`;
  const m=modalOpen(
    `<div class="mtitle"><span class="r" style="margin-right:8px">ᚾ</span>${esc(T("hearth.net.modal.title"))}</div>`+
    `<div class="mbody">`+
    `<div class="dsec">${esc(T("hearth.net.modal.sec.addresses"))}</div>`+
    (S.domain?addrRow(T("hearth.net.addr.waystone"),S.domain+":"+port,true):"")+
    addrRow(T("hearth.net.addr.public"),(S.extIp||"-")+":"+port,true)+
    addrRow(T("hearth.net.addr.lan"),(S.intIp||"-")+":"+port,true)+
    addrRow(T("hearth.net.addr.local"),"127.0.0.1:"+port,true)+
    (S.invite?addrRow(T("hearth.net.addr.crossplay"),S.invite,true):"")+
    addrRow(T("hearth.net.addr.rcon"),S.prefs?.RconEnabled?"127.0.0.1:"+(S.prefs.RconPort??25575):T("hearth.net.addr.rcon.off"),false)+
    addrRow(T("hearth.net.addr.query"),String(port+1),false)+
    // Clients must match the network version exactly, so it belongs next to the addresses.
    addrRow(T("hearth.net.game_version"),
      S.gameVersion||S.prefs?.LastLaunchedGameVersion||"-",false)+
    addrRow(T("hearth.net.network_version"),
      S.networkVersion?T("hearth.net.network_version.value",{version:S.networkVersion}):"-",false)+
    `<div class="dsec">${esc(T("hearth.net.modal.sec.traffic"))} <span class="subval" style="text-transform:none;letter-spacing:0">${esc(asOf
        ?T("hearth.net.modal.traffic.note.asof",{clock:pad(asOf.getHours())+":"+pad(asOf.getMinutes())})
        :T("hearth.net.modal.traffic.note"))}</span></div>`+
    `<div class="dstats">`+
      stat(esc(T("hearth.net.stat.connections")),n.conns??"-")+
      stat(esc(T("hearth.net.stat.sent")),n.sent!=null?fmtBytes(n.sent):"-")+
      stat(esc(T("hearth.net.stat.recv")),n.recv!=null?fmtBytes(n.recv):"-")+
      stat(esc(T("hearth.net.stat.zdos")),n.zdos!=null?n.zdos.toLocaleString(LOC()):"-")+
    `</div>`+
    (n.at?"":`<div class="subval" style="margin:2px 0 6px">${esc(T("hearth.net.modal.no_report"))}</div>`)+
    `<div class="dsec">${esc(T("hearth.net.modal.sec.sessions"))}</div>`+sess+
    `<div class="subval" style="margin-top:8px">${esc(T("hearth.net.modal.ping.note"))}</div>`+
    `</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mCancel">${esc(T("common.button.close"))}</button></div>`,netModal);
  m.querySelector("#mCancel").addEventListener("click",modalClose);
  m.querySelectorAll("[data-copy]").forEach(c=>c.addEventListener("click",()=>{
    navigator.clipboard?.writeText(c.dataset.copy);
    toast("ᚾ "+T("hearth.net.copied.value.toast",{value:c.dataset.copy}));
  }));
}

/* ---------- DELETE A WORLD ----------
   The one place a world goes for good, shared by the World Saves card and the Barrow's
   world view. worlds.delete makes the same three checks again on the way in, so this is
   not the guard - it is the guard said out loud, so a control that could only come back
   refused is greyed with its reason instead of offered. */
function worldDeleteBlock(ctx){
  if(!ctx||!ctx.world) return T("world.delete.block.no_world");
  if(!ctx.folder||!ctx.sub) return T("world.delete.block.no_folder");
  if(ctx.running) return T("world.delete.block.running",{owner:ctx.owner||""});
  if(ctx.owner) return T("world.delete.block.owned",{owner:ctx.owner});
  return "";
}
function worldDeleteBackupNote(alsoBackups){
  return alsoBackups
    ? T("world.delete.backups.gone")
    : T("world.delete.backups.kept");
}
function worldDeleteModal(ctx,after){
  const block=worldDeleteBlock(ctx);
  if(block){toast("ᚦ "+block);return;}
  const m=modalOpen(
    `<div class="mtitle">${esc(T("world.delete.title"))}<span class="cnorse">${esc(ctx.world)}</span></div>`+
    `<div class="mbody">`+
      /* Two whole sentences rather than one with a noun phrase swapped into the middle:
         which files go is the only difference, and a translator handed the tail alone
         cannot agree it with the rest of the clause. */
      `<div class="subval">${monoFill(
        ctx.format==="legacy"
          ?T("world.delete.body.legacy",{world:monoSlot("world")})
          :T("world.delete.body.folder",{world:monoSlot("world")}),
        {world:ctx.world})}</div>`+
      `<div class="subval mono" style="margin-top:6px;word-break:break-all">${esc(String(ctx.folder||""))}/${esc(String(ctx.sub||""))}</div>`+
      `<label class="togglerow" style="cursor:pointer;margin-top:10px"><span class="tl">${esc(T("world.delete.backups.label"))}</span><div class="toggle" id="dwBk"></div></label>`+
      `<div class="subval" id="dwBkNote" style="margin-top:6px">${esc(worldDeleteBackupNote(false))}</div>`+
      `<div class="field" style="margin-top:12px"><label>${esc(T("world.delete.confirm.label"))}</label>`+
        `<input type="text" id="dwName" placeholder="${esc(ctx.world)}" spellcheck="false" autocomplete="off"></div>`+
      `<div class="subval" id="dwStat" style="margin-top:6px;color:var(--blood)">${esc(T("world.delete.warning"))}</div>`+
    `</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="dwCancel">${esc(T("common.button.cancel"))}</button>`+
      `<button class="btn btn-blood btn-sm" id="dwOk" disabled>${esc(T("world.delete.button"))}</button></div>`,()=>worldDeleteModal(ctx,after));
  const bk=m.querySelector("#dwBk"), name=m.querySelector("#dwName"), ok=m.querySelector("#dwOk");
  bk.addEventListener("click",()=>{
    bk.classList.toggle("on");
    m.querySelector("#dwBkNote").textContent=worldDeleteBackupNote(bk.classList.contains("on"));
  });
  /* Checked here the same way C# checks it - trimmed, and the capitals have to match - so
     the button never promises a delete the host would then be turned away for. */
  const typedOk=()=>name.value.trim()===String(ctx.world).trim();
  name.addEventListener("input",()=>{ok.disabled=!typedOk();});
  m.querySelector("#dwCancel").addEventListener("click",modalClose);
  let busy=false;
  const run=async()=>{
    if(busy||!typedOk()) return;
    busy=true; ok.disabled=true;
    const also=bk.classList.contains("on");
    if(!Native.available){
      modalClose();
      toast("ᛪ "+T("world.delete.preview.toast"));
      if(after) after();
      return;
    }
    const r=await rpc("worlds.delete",
      {world:ctx.world,folder:ctx.folder,sub:ctx.sub,confirmName:name.value,includeBackups:also});
    busy=false;
    if(r===FAIL){ok.disabled=false;return;}
    modalClose();
    toast("ᛪ "+T("world.delete.done.toast",{world:ctx.world}));
    logLine("warn","[BakaLoader] deleted world '"+ctx.world+"' ("+((r.deleted||[]).length)
      +" item(s) removed · backups "+(also?"deleted too":"kept")+")");
    if(after) after();
  };
  ok.addEventListener("click",run);
  name.addEventListener("keydown",e=>{if(e.key==="Enter")run();});
  setTimeout(()=>name.focus(),30);
}
/* ---------- COPY A WORLD UNDER A NEW NAME ----------
   The one place a world is duplicated, shared by the Settings hall's World field and the
   Barrow's world list, the way the delete is. worlds.copyAs makes every one of these
   checks again on the way in, so this is not the guard: it is the guard said out loud, so
   a control that could only come back refused is greyed with its reason instead of
   offered. Whoever has the world merely CHOSEN is not asked: a copy leaves the source and
   its backup layers exactly where they are, so there is nothing for them to lose. */
function worldCopyBlock(ctx){
  if(!ctx||!ctx.world) return T("world.copy.block.no_world");
  if(ctx.running) return T("world.copy.block.running",{owner:ctx.owner||""});
  return "";
}
function worldCopyModal(ctx,after){
  const block=worldCopyBlock(ctx);
  if(block){toast("ᚦ "+block);return;}
  /* The rule goes INTO the dialog rather than being read after it has shut. A name the
     rule turns down is said under the box with the name still in it, which is the only
     place a host can act on it: a toast over a closed dialog meant opening the dialog
     again and typing the whole name again to change one character of it. It is handed in
     as the dialog's check, below, which the press runs before it hands the name on, so
     this arm is only ever reached by a name the rule has already passed and says the
     rule once rather than twice. worlds.copyAs asks it again on the way in regardless,
     which is the check that actually holds. */
  promptModal(
    ()=>T("world.copy.title",{world:ctx.world}),
    ()=>T("world.copy.placeholder"),
    async typed=>{
      const target=String(typed).trim();
      if(!Native.available){
        toast("ᛝ "+T("world.copy.preview.toast",{world:target}));
        if(after) after(target);
        return;
      }
      const r=await rpc("worlds.copyAs",
        {source:ctx.world,target,folder:ctx.folder||"",sub:ctx.sub||""});
      if(r===FAIL) return;
      toast("ᛝ "+T("world.copy.done.toast",{source:ctx.world,target}));
      logLine("ok","[BakaLoader] copied world '"+ctx.world+"' as '"+target+"'");
      if(after) after(target);
    },
    typed=>worldNameProblem(typed,ctx.taken||[]));
}
/* Preview stand-in for world.info, so the card and its delete control can be walked
   without the app behind them. */
const WORLD_INFO_MOCK={world:"Midgard",folder:"C:/Users/you/AppData/LocalLow/IronGate/Valheim",
  sub:"worlds_local",owner:null,running:false,format:"chunked",files:[],backups:[]};

/* ---------- SAVES DRILL-DOWN (World Saves card click) ---------- */
async function savesModal(){
  let world=S.prefs?.WorldName||"";
  let info=null;
  if(Native.available){
    if(world){
      const r=await rpc("world.info",{world});
      if(r!==FAIL) info=r;
    }
  }else{
    info=WORLD_INFO_MOCK; world=world||info.world;
  }
  const frow=f=>`<div class="drow"><span class="dk mono">${esc(f.name)}</span><span class="dv mono">${fmtBytes(f.sizeBytes)}</span><span class="dv" style="flex:0 0 auto">${fmtT(f.modifiedUtc)}</span></div>`;
  const files=info?.files?.length?info.files.map(frow).join(""):`<div class="subval" style="padding:4px 2px">${esc(T("hearth.saves.modal.files.none"))}</div>`;
  const bk=info?.backups||[];
  const bkRows=bk.length
    ?bk.slice(-5).reverse().map(frow).join("")+(bk.length>5?`<div class="subval" style="padding:2px 2px">${esc(T("hearth.saves.modal.older",{count:bk.length-5}))}</div>`:"")
    :emptyState({compact:true,mark:"\u16DD",title:T("barrow.saves.empty.title"),
        reason:T("barrow.saves.empty.reason"),
        action:{name:"openBackups",label:T("barrow.saves.empty.action")}});
  const avg=S.saveDur.length?Math.round(S.saveDur.reduce((a,b)=>a+b,0)/S.saveDur.length):null;
  const delCtx={world,folder:info?.folder,sub:info?.sub,owner:info?.owner,running:info?.running,format:info?.format};
  const delBlock=worldDeleteBlock(delCtx);
  const m=modalOpen(
    `<div class="mtitle"><span class="r" style="margin-right:8px">ᛉ</span>${esc(T("hearth.saves.modal.title",{world:world||"-"}))}</div>`+
    `<div class="mbody">`+
    `<div class="dsec">${esc(T("hearth.saves.modal.sec.rhythm"))}</div>`+
    `<div class="dstats">`+
      `<div class="dstat"><div class="bigval" style="font-size:20px">${$("#saveCountdown").textContent}</div><div class="subval">${esc(T("hearth.saves.next.caption"))}</div></div>`+
      `<div class="dstat"><div class="bigval" style="font-size:20px">${esc(T("hearth.saves.modal.interval",{minutes:Math.round((S.saveInterval??600)/60)}))}</div><div class="subval">${esc(T("hearth.saves.modal.interval.caption"))}</div></div>`+
      `<div class="dstat"><div class="bigval" style="font-size:20px">${S.lastSaveAt?pad(S.lastSaveAt.getHours())+":"+pad(S.lastSaveAt.getMinutes()):"-"}</div><div class="subval">${esc(T("hearth.saves.modal.last.caption"))}</div></div>`+
      `<div class="dstat"><div class="bigval" style="font-size:20px">${avg!=null?esc(T("hearth.saves.ms",{ms:avg})):"-"}</div><div class="subval">${esc(T("hearth.saves.modal.avg.caption",{count:S.saveDur.length||"-"}))}</div></div>`+
    `</div>`+
    `<div class="dsec">${esc(T("hearth.saves.modal.sec.files"))}</div>`+files+
    `<div class="dsec">${esc(T("hearth.saves.modal.sec.backups"))} <span class="subval" style="text-transform:none;letter-spacing:0">· ${esc(T("hearth.saves.modal.backups.count",{count:bk.length}))}</span></div>`+bkRows+
    (info?.folder?`<div class="subval mono" style="margin-top:8px;word-break:break-all">${esc(info.folder)}</div>`:"")+
    `</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mBarrow" title="${esc(T("common.norse.barrow"))}">${esc(T("barrow.title"))}</button><button class="btn btn-ghost btn-sm" id="mOpenWorlds">${esc(T("hearth.saves.modal.open_folder"))}</button>`+
    `<button class="btn btn-blood btn-sm" id="mDelWorld"${delBlock?` disabled title="${esc(delBlock)}"`:` title="${esc(T("world.delete.button.title"))}"`}>${esc(T("world.delete.button"))}</button>`+
    `<button class="btn btn-ghost btn-sm" id="mCancel">${esc(T("common.button.close"))}</button></div>`,savesModal);
  m.querySelector("#mCancel").addEventListener("click",modalClose);
  m.querySelector("#mBarrow").addEventListener("click",()=>barrowModal());
  m.querySelector("#mDelWorld").addEventListener("click",()=>{
    if(delBlock){toast("ᚦ "+delBlock);return;}
    /* the world list on the World hall is the one place that would still offer a world
       that is no longer there, so it is read again once the delete lands */
    worldDeleteModal(delCtx,()=>{try{renderWorldSelect();}catch(_){}});
  });
  m.querySelector("#mOpenWorlds").addEventListener("click",()=>{
    if(!Native.available){toast("ᛃ "+T("hearth.saves.modal.open.preview.toast"));return;}
    rpc("shell.open",{target:"saveData"});
  });
}

/* ---------- THE BARROW (layered per-world backup manager) ---------- */
/* Preview-mode stand-in data so the whole flow can be walked without the app. */
const BARROW_MOCK=[
  {world:"Midgard",folder:"C:/Users/you/AppData/LocalLow/IronGate/Valheim",sub:"worlds_local",
   owner:"Default",running:true,sizeBytes:48234567,day:412,format:"chunked",formatLabel:"1.0",
   modifiedUtc:new Date(Date.now()-7*60000).toISOString(),backupBytes:139460000,
   backups:[
     {file:"Midgard_backup_auto-20260711-060000",kind:"auto",isDirectory:true,committed:true,sizeBytes:47102003,day:411,hasDb:true,modifiedUtc:new Date(Date.now()-5*3600000).toISOString()},
     {file:"Midgard_backup_20260710-101500.fwl",kind:"legacy",isDirectory:false,committed:true,sizeBytes:46990111,day:410,hasDb:true,modifiedUtc:new Date(Date.now()-26*3600000).toISOString()},
     {file:"Midgard_backup_restore-20260708-193045",kind:"restore",isDirectory:true,committed:true,sizeBytes:45367886,day:398,hasDb:true,modifiedUtc:new Date(Date.now()-3*86400000-4.25*3600000).toISOString()},
   ]},
  {world:"Trialgrounds",folder:"C:/Users/you/AppData/LocalLow/BakaLoader/servers/proving",sub:"worlds_local",
   owner:null,running:false,sizeBytes:9034120,day:23,format:"legacy",formatLabel:"Legacy",
   modifiedUtc:new Date(Date.now()-12*86400000).toISOString(),backupBytes:8877001,
   backups:[
     {file:"Trialgrounds_backup_auto-20260629-120000.fwl",kind:"auto",isDirectory:false,committed:true,sizeBytes:8877001,day:22,hasDb:true,modifiedUtc:new Date(Date.now()-12*86400000-3600000).toISOString()},
     {file:"Trialgrounds.fwl.old",kind:"old",isDirectory:false,committed:true,sizeBytes:8790224,day:21,hasDb:true,modifiedUtc:new Date(Date.now()-13*86400000).toISOString()},
   ]},
];
/* Backup layers whose world is no longer on disk. Nothing else on the machine lists
   these, and a stack of them can be as large as the world it came from. */
const BARROW_ORPHAN_MOCK=[
  {world:"Eikthyr",folder:"C:/Users/you/AppData/LocalLow/IronGate/Valheim",sub:"worlds_local",
   layers:[
     {file:"Eikthyr_backup_20260114-133010.fwl",kind:"other",isDirectory:false,committed:true,
      damaged:false,sizeBytes:74210000,day:118,hasDb:true,
      modifiedUtc:new Date(Date.now()-240*86400000).toISOString()},
   ]},
  {world:"Stonefall",folder:"C:/Users/you/AppData/LocalLow/IronGate/Valheim",sub:"worlds",
   layers:[
     {file:"Stonefall_backup_20251214-171043.fwl",kind:"other",isDirectory:false,committed:true,
      damaged:false,sizeBytes:69120000,day:66,hasDb:true,
      modifiedUtc:new Date(Date.now()-300*86400000).toISOString()},
   ]},
];
/* What kind of layer a row is, by the kind the host sends. The property names end in Id
   because the words are in the catalog and the value here is the id that fetches them:
   spelled that way, the completeness gate can see all seven without one lookup call site
   per kind, and the lookup still happens when the row is drawn rather than when this
   file is evaluated, which is before a catalog has been loaded.
   "legacy" is the untouched pre-conversion pair Valheim renames aside the first time it
   saves an older world into the 1.0 directory format. */
const BARROW_KIND={autoId:"barrow.kind.auto",oldId:"barrow.kind.old",restoreId:"barrow.kind.restore",
  legacyId:"barrow.kind.legacy",cloudId:"barrow.kind.cloud",preupdateId:"barrow.kind.preupdate",
  otherId:"barrow.kind.other"};
const barrowKind=kind=>T(BARROW_KIND[kind+"Id"]||BARROW_KIND.otherId);
/* Extra warning under the unearth prompt, when this layer needs one. */
function barrowUnearthNote(g,b){
  const warn=t=>" <span style='color:var(--warn,#e0a35c)'>"+esc(t)+"</span>";
  if(b.isDirectory&&b.committed===false)
    return warn(T("barrow.layer.block.uncommitted"));
  if(b.kind==="legacy"&&g.format==="chunked")
    return warn(T("barrow.unearth.note.legacy"));
  if(b.kind==="preupdate")
    return warn(T("barrow.unearth.note.preupdate"));
  if(!b.isDirectory&&!b.hasDb)
    return warn(T("barrow.unearth.note.nodb"));
  return "";
}
/* The sentence under a layer-delete confirm. What goes with the layer is the whole
   difference between the three shapes, so each is a sentence of its own rather than one
   sentence with a clause swapped into the middle: a translator handed " and its paired
   .db" alone cannot agree it with the rest of the clause. The orphan pane says one more
   thing after it, joined with a space, which is the documented separator for the pair. */
function barrowLayerDeleteBody(b,orphan){
  const sentence=b.isDirectory
    ?T("barrow.layer.delete.body.folder",{file:monoSlot("file")})
    :(b.hasDb&&b.damaged!==true
        ?T("barrow.layer.delete.body.pair",{file:monoSlot("file")})
        :T("barrow.layer.delete.body.plain",{file:monoSlot("file")}));
  const note=orphan?" "+T("barrow.layer.delete.orphan.note"):"";
  return `<div class="subval">${monoFill(sentence+note,{file:b.file})}</div>`;
}
/* "1.0" = the world is a directory of chunks; "Legacy" = the older .fwl and .db pair. */
function worldFormatLabel(g){
  if(!g) return "";
  if(g.formatLabel) return g.formatLabel;
  return g.format==="chunked"?"1.0":(g.format==="legacy"?T("barrow.format.legacy"):"");
}
function barrowFolderLabel(g){
  const parts=String(g.folder||"").split(/[\\/]/).filter(Boolean);
  const i=parts.findIndex(x=>x.toLowerCase()==="servers");
  const tail=i>=0&&parts[i+1]?"servers/"+parts[i+1]:parts[parts.length-1]||"";
  return tail+(g.sub==="worlds"?" · worlds":"");
}
/* backups.overview answers either the plain list of worlds it always did, or an object
   carrying that list plus the backup sets whose world is gone. Both are read here so the
   Barrow works against a host of either vintage. */
function barrowNormalize(r){
  if(Array.isArray(r)) return {groups:r,orphans:[]};
  if(r&&typeof r==="object"){
    const g=r.worlds||r.groups||r.items||[];
    return {groups:Array.isArray(g)?g:[],orphans:Array.isArray(r.orphans)?r.orphans:[]};
  }
  return {groups:[],orphans:[]};
}
/* An owner-less set laid out like a world group, so one row component draws both. */
function barrowOrphanGroup(o){
  const layers=(o&&(o.layers||o.backups))||[];
  return {world:String((o&&o.world)||""),folder:(o&&o.folder)||"",sub:(o&&o.sub)||"",
          running:false,orphan:true,backups:Array.isArray(layers)?layers:[]};
}
async function barrowFetch(){
  if(!Native.available) return {groups:BARROW_MOCK,orphans:BARROW_ORPHAN_MOCK.map(barrowOrphanGroup)};
  const r=await rpc("backups.overview",{});
  if(r===FAIL) return null;
  const n=barrowNormalize(r);
  return {groups:n.groups,orphans:n.orphans.map(barrowOrphanGroup)};
}
async function barrowModal(){
  const all=await barrowFetch();
  if(!all){toast("ᚦ "+T("barrow.read.failed.toast"));return;}
  const groups=all.groups||[], orphans=all.orphans||[];
  const rows=groups.length?groups.map((g,i)=>
    `<div class="drow browRow" data-i="${i}" style="cursor:pointer">`+
    `<span class="dk mono">${esc(g.world)}</span>`+
    `<span class="dv">${g.owner?esc(g.owner):`<span style='opacity:.55'>${esc(T("barrow.world.unclaimed"))}</span>`}${g.running?` <span style="color:var(--ok,#7dc98f)">● ${esc(T("barrow.world.raiding"))}</span>`:""}</span>`+
    `<span class="dv mono">${esc(T("barrow.layers.count",{count:(g.backups||[]).length}))} · ${fmtBytes((g.sizeBytes||0)+(g.backupBytes||0))}</span>`+
    `<span class="dv" style="flex:0 0 auto">${agoAt(g.modifiedUtc)}</span>`+
    `<span class="copychip bCopyAs" data-i="${i}" title="${esc(T("world.copy.chip.title"))}">${esc(T("world.copy.chip"))}</span>`+
    `</div><div class="subval mono" style="padding:0 2px 6px;opacity:.6">${esc(barrowFolderLabel(g))}${worldFormatLabel(g)?" · "+esc(worldFormatLabel(g)):""}${g.day!=null?" · "+esc(T("barrow.day",{day:g.day})):""}</div>`
  ).join(""):emptyState({mark:"\u16DD",title:T("barrow.worlds.empty.title"),
    reason:T("barrow.worlds.empty.reason")});
  /* Layers whose world is gone. Nothing else on the machine shows them, and until now
     nothing here did either, so the disk they hold could not be seen or reclaimed. */
  const orphanRows=orphans.length
    ?`<div class="dsec">${esc(T("barrow.orphans.head"))}</div>`+
     `<div class="subval" style="margin-bottom:6px">${esc(T("barrow.orphans.note"))}</div>`+
     orphans.map((o,oi)=>
       `<div class="drow"><span class="dk mono">${esc(o.world)}</span>`+
       `<span class="dv">${esc(T("barrow.orphans.no_world"))}</span>`+
       `<span class="dv mono">${esc(T("barrow.layers.count",{count:o.backups.length}))} · `+
       `${fmtBytes(o.backups.reduce((n,b)=>n+(Number(b.sizeBytes)||0),0))}</span></div>`+
       `<div class="subval mono" style="padding:0 2px 6px;opacity:.6">${esc(barrowFolderLabel(o))}</div>`+
       o.backups.map((b,i)=>barrowLayerRowHtml(o,b,i,
         {restore:"oUnearth",drop:"oDrop"},` data-o="${oi}"`)).join("")
     ).join("")
    :"";
  /* Wider than a normal modal on purpose: a layer row is a file name plus four facts
     plus two controls, and at 460px the tail was drawn outside the modal and could not
     be clicked. Both Barrow panes share the width so stepping in and out does not
     resize the window under the pointer. */
  const m=modalOpen(
    `<div class="mtitle">${esc(T("barrow.title"))}<span class="cnorse">${esc(T("common.norse.barrow"))}</span></div>`+
    `<div class="mbody">`+
    `<div class="subval" style="margin-bottom:8px">${esc(T("barrow.note"))}</div>`+
    rows+orphanRows+`</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mCancel">${esc(T("common.button.close"))}</button></div>`,barrowModal);
  m.classList.add("mwide");
  m.querySelector("#mCancel").addEventListener("click",modalClose);
  m.querySelectorAll(".browRow").forEach(r=>r.addEventListener("click",()=>barrowWorldModal(groups[+r.dataset.i])));
  /* The chip sits inside a row that opens the world when it is pressed, so the press
     has to stop there: without this, one press would open the drill-down behind the
     name box and the copy would land on whatever the drill-down then showed. */
  m.querySelectorAll(".bCopyAs").forEach(c=>c.addEventListener("click",e=>{
    e.stopPropagation();
    const g=groups[+c.dataset.i]; if(!g) return;
    barrowCopyWorld(g,groups);
  }));
  m.querySelectorAll(".oUnearth").forEach(c=>c.addEventListener("click",()=>{
    const o=orphans[+c.dataset.o]; if(!o) return;
    const b=o.backups[+c.dataset.i];
    const block=barrowLayerBlock(o,b);
    if(block){toast("ᚦ "+block);return;}
    confirmModal(()=>T("barrow.orphan.restore.title"),
      ()=>`<div class="subval">${monoFill(
        T("barrow.orphan.restore.body",{file:monoSlot("file"),world:monoSlot("world")}),
        {file:b.file,world:o.world})}</div>`,
      ()=>T("common.button.restore"),async()=>{
        if(!Native.available){
          toast("ᛝ "+T("barrow.world.brought_back.preview.toast"));
          return;
        }
        const r=await rpc("backups.restore",{world:o.world,folder:o.folder,sub:o.sub,file:b.file});
        if(r===FAIL) return;
        toast("ᛝ "+T("barrow.world.brought_back.toast",{world:o.world}));
        logLine("ok","[BakaLoader] brought '"+o.world+"' back from "+b.file+" · it had no world on disk");
        barrowModal();
      });
  }));
  m.querySelectorAll(".oDrop").forEach(c=>c.addEventListener("click",()=>{
    const o=orphans[+c.dataset.o]; if(!o) return;
    const b=o.backups[+c.dataset.i];
    confirmModal(()=>T("barrow.layer.delete.title"),()=>barrowLayerDeleteBody(b,true),
      ()=>T("common.button.delete"),async()=>{
        if(!Native.available){
          o.backups=o.backups.filter(x=>x!==b);
          toast("ᛪ "+T("barrow.layer.deleted.preview.toast"));
          setTimeout(()=>barrowModal(),0);
          return;
        }
        const r=await rpc("backups.delete",{world:o.world,folder:o.folder,sub:o.sub,file:b.file});
        if(r===FAIL) return;
        toast("ᛪ "+T("barrow.layer.deleted.toast"));
        logLine("warn","[BakaLoader] deleted the ownerless backup layer "+b.file+" of '"+o.world+"'");
        barrowModal();
      });
  }));
}
/* A snapshot folder Valheim has not finished writing carries no world to put back. The
   warning under the confirm already said so; the button itself now says it too, instead
   of running a restore that can only fail. Same for a damaged layer: the host names it
   in the overview, and the app refuses that restore anyway. */
function barrowLayerBlock(g,b){
  /* Damaged goes first on purpose. Stopping the server does not bring a missing world
     file back, so "Stop the server first" would send the host off to do something that
     cannot help. This layer can only ever be cleared. */
  if(b.damaged===true)
    return T("barrow.layer.block.damaged");
  if(g.running) return T("barrow.layer.block.running");
  if(b.isDirectory&&b.committed===false)
    return T("barrow.layer.block.uncommitted");
  return "";
}
/* One layer row, drawn the same whether the world it belongs to is still on disk or
   gone: sel names the two classes the calling modal wires, extra carries any attribute
   that row needs to say which set it came from. */
function barrowLayerRowHtml(g,b,i,sel,extra){
  const block=barrowLayerBlock(g,b);
  const cls=sel||{restore:"bUnearth",drop:"bDrop"};
  const at=extra||"";
  return `<div class="drow blayer"><span class="dk mono" style="min-width:0;overflow:hidden;text-overflow:ellipsis">${esc(b.file)}</span>`+
  `<span class="dv" style="flex:0 0 auto">${esc(barrowKind(b.kind))}</span>`+
  `<span class="dv mono" style="flex:0 0 auto">${fmtBytes(b.sizeBytes)}${b.isDirectory?" · "+esc(T("barrow.layer.folder")):""}${b.day!=null?" · "+esc(T("barrow.day",{day:b.day})):""}`+
  /* A leftover reads as a leftover in the row, not only in the greyed control's title. */
  `${b.damaged===true?" · <span style=\"color:var(--warn,#e0a35c)\">"+esc(T("barrow.layer.damaged"))+"</span>":""}</span>`+
  `<span class="dv" style="flex:0 0 auto">${agoAt(b.modifiedUtc)}</span>`+
  `<span class="copychip ${cls.restore}${block?" disabled":""}" data-i="${i}"${at} title="${esc(block||T("barrow.layer.restore.title"))}"${block?` style="opacity:.4;cursor:not-allowed"`:""}>${esc(T("barrow.layer.restore.chip"))}</span>`+
  `<span class="copychip ${cls.drop}" data-i="${i}"${at} style="color:var(--warn,#e0a35c)">✕</span>`+
  `</div>`;
}
/* Copy one of the Barrow's worlds under a new name and come back to the list with the
   copy in it. In the browser preview there is no host to copy anything, so the mock list
   grows a world the same way it loses one on a delete, and the Barrow stays walkable. */
function barrowCopyWorld(g,groups){
  /* The names already spoken for, when the caller knows them. The Barrow lists every
     save folder BakaLoader knows in one go, and a name is only taken inside ONE of
     them, so the listing is cut down to the folder this world sits in before it is
     asked: a realm kept on its own save folder was being turned away from a name that
     was free there, because some other folder had a world by that name. The world view
     was handed ONE world and holds no listing at all, so in the app it says nothing
     rather than guessing, and in the browser preview the mock listing stands in for the
     native side, cut to this world's own save folder the same way. A preview that turned
     down a name the app would take is a walker writing the feature up wrong, and this
     view and the row it was opened from have to answer the same. Either way the native
     side answers for certain on the way in. */
  const here=groups?groups.filter(x=>x.folder===g.folder):null;
  const taken=here?here.map(x=>x.world)
    :(Native.available?[]:BARROW_MOCK.filter(x=>x.folder===g.folder).map(x=>x.world));
  worldCopyModal({world:g.world,folder:g.folder,sub:g.sub,owner:g.owner,running:g.running,taken},
    target=>{
      if(!Native.available){
        BARROW_MOCK.push({...g,world:target,owner:null,running:false,
          backups:[],backupBytes:0,modifiedUtc:new Date().toISOString()});
      }
      barrowModal();
    });
}
function barrowWorldModal(g){
  const layerRow=(b,i)=>barrowLayerRowHtml(g,b,i);
  const bks=g.backups||[];
  const delCtx={world:g.world,folder:g.folder,sub:g.sub,owner:g.owner,running:g.running,format:g.format};
  const delBlock=worldDeleteBlock(delCtx);
  const m=modalOpen(
    `<div class="mtitle">${esc(T("barrow.title"))} · ${esc(g.world)}<span class="cnorse">${esc(T("common.norse.barrow"))}</span></div>`+
    `<div class="mbody">`+
    `<div class="dsec">${esc(T("barrow.sec.live"))}</div>`+
    `<div class="drow"><span class="dk mono">${esc(g.world)}${g.format==="chunked"?"/":".fwl + .db"}</span>`+
    `<span class="dv mono">${worldFormatLabel(g)?esc(worldFormatLabel(g))+" · ":""}${fmtBytes(g.sizeBytes)}${g.day!=null?" · "+esc(T("barrow.day",{day:g.day})):""}</span>`+
    `<span class="dv" style="flex:0 0 auto">${agoAt(g.modifiedUtc)}</span></div>`+
    (g.running?`<div class="subval" style="padding:2px 2px 6px;color:var(--warn,#e0a35c)">${esc(T("barrow.world.running.note"))}</div>`:"")+
    `<div class="dsec">${esc(T("barrow.sec.layers"))} <span class="subval" style="text-transform:none;letter-spacing:0">· ${esc(T("hearth.saves.modal.backups.count",{count:bks.length}))} · ${fmtBytes(g.backupBytes||0)}</span></div>`+
    (bks.length?bks.map(layerRow).join(""):emptyState({compact:true,mark:"\u16DD",title:T("barrow.layers.empty.title"),
      reason:T("barrow.layers.empty.reason")}))+
    `<div class="subval mono" style="margin-top:8px;word-break:break-all">${esc(g.folder)}/${esc(g.sub)}</div>`+
    `</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mBack">${esc(T("common.button.back"))}</button>`+
    `<button class="btn btn-ghost btn-sm" id="mCopyAs" title="${esc(T("world.copy.button.title"))}">${esc(T("world.copy.button"))}</button>`+
    `<button class="btn btn-blood btn-sm" id="mDelWorld"${delBlock?` disabled title="${esc(delBlock)}"`:` title="${esc(T("world.delete.button.title"))}"`}>${esc(T("world.delete.button"))}</button>`+
    `<button class="btn btn-ghost btn-sm" id="mCancel">${esc(T("common.button.close"))}</button></div>`,()=>barrowWorldModal(g));
  m.classList.add("mwide");
  m.querySelector("#mCancel").addEventListener("click",modalClose);
  m.querySelector("#mBack").addEventListener("click",()=>barrowModal());
  m.querySelector("#mCopyAs").addEventListener("click",()=>barrowCopyWorld(g,null));
  m.querySelector("#mDelWorld").addEventListener("click",()=>{
    if(delBlock){toast("ᚦ "+delBlock);return;}
    /* back to the world list: this one is not in it any more */
    worldDeleteModal(delCtx,()=>{
      if(!Native.available){
        const i=BARROW_MOCK.findIndex(x=>x.world===g.world&&x.folder===g.folder&&x.sub===g.sub);
        if(i>=0) BARROW_MOCK.splice(i,1);
      }
      barrowModal();
    });
  });
  const reopen=async()=>{
    const all=await barrowFetch();
    const g2=all&&(all.groups||[]).find(x=>x.world===g.world&&x.folder===g.folder&&x.sub===g.sub);
    if(g2) barrowWorldModal(g2); else barrowModal();
  };
  m.querySelectorAll(".bUnearth").forEach(c=>c.addEventListener("click",()=>{
    if(g.running){toast("ᚦ "+T("barrow.layer.restore.running.toast"));return;}
    const b=bks[+c.dataset.i];
    const block=barrowLayerBlock(g,b);
    if(block){toast("ᚦ "+block);return;}
    confirmModal(()=>T("barrow.layer.restore.confirm.title"),
      ()=>`<div class="subval">${monoFill(T("barrow.layer.restore.body",{file:monoSlot("file")}),{file:b.file})}${barrowUnearthNote(g,b)}</div>`,
      ()=>T("common.button.restore"),async()=>{
        if(!Native.available){
          const ts=new Date();
          const stamp=g.world+"_backup_restore-"+ts.getFullYear()+pad(ts.getMonth()+1)+pad(ts.getDate())+"-"+pad(ts.getHours())+pad(ts.getMinutes())+"00";
          g.backups.unshift({file:g.format==="chunked"?stamp:stamp+".fwl",kind:"restore",isDirectory:g.format==="chunked",committed:true,sizeBytes:g.sizeBytes,day:g.day,hasDb:true,modifiedUtc:ts.toISOString()});
          toast("ᛝ "+T("barrow.layer.unearthed.preview.toast"));
          setTimeout(()=>barrowWorldModal(g),0); // confirmModal closes itself right after onOk
          return;
        }
        const r=await rpc("backups.restore",{world:g.world,folder:g.folder,sub:g.sub,file:b.file});
        if(r===FAIL) return;
        toast("ᛝ "+T("barrow.layer.unearthed.toast"));
        logLine("ok","[BakaLoader] restored '"+g.world+"' from "+b.file+(r.snapshot?" · safety copy "+r.snapshot:""));
        reopen();
      });
  }));
  m.querySelectorAll(".bDrop").forEach(c=>c.addEventListener("click",()=>{
    const b=bks[+c.dataset.i];
    confirmModal(()=>T("barrow.layer.delete.title"),()=>barrowLayerDeleteBody(b,false),
      ()=>T("common.button.delete"),async()=>{
        if(!Native.available){
          g.backups=g.backups.filter(x=>x!==b);
          toast("ᛪ "+T("barrow.layer.deleted.preview.toast"));
          setTimeout(()=>barrowWorldModal(g),0);
          return;
        }
        const r=await rpc("backups.delete",{world:g.world,folder:g.folder,sub:g.sub,file:b.file});
        if(r===FAIL) return;
        toast("ᛪ "+T("barrow.layer.deleted.toast"));
        logLine("warn","[BakaLoader] deleted backup layer "+b.file+" of '"+g.world+"'");
        reopen();
      });
  }));
}

/* card click wiring (chips inside the cards must not trigger the drill-down) */
$("#netCard")?.addEventListener("click",e=>{if(!e.target.closest(".copychip"))netModal();});
$("#savesCard")?.addEventListener("click",e=>{if(!e.target.closest(".copychip"))savesModal();});

/* ---------- SKALD (local analytics · the realm's story in numbers) ----------
   Everything the Skald knows lives in analytics.json on this machine - the
   journal is never uploaded anywhere. Preview mode renders a mock hall. */
const SKALD_MOCK={
  profile:"Final Sunset",running:true,
  since:new Date(Date.now()-42*86400000).toISOString(),eventCount:1874,
  uptime:{totalSec:1123260,currentSec:16320,starts:57,crashes:2},
  players:[
    {key:"Steam:76561198000000001",name:"Bjorn",character:"Bjorn Ironside",playSec:432600,sessions:64,deaths:23,lastSeen:new Date(Date.now()-4*60000).toISOString(),online:true},
    {key:"Steam:76561198000000002",name:"Astrid",character:"Astrid",playSec:301200,sessions:48,deaths:11,lastSeen:new Date(Date.now()-11*60000).toISOString(),online:true},
    {key:"Steam:76561198000000003",name:"Leif",character:"Leif the Lost",playSec:122400,sessions:31,deaths:19,lastSeen:new Date(Date.now()-2*86400000).toISOString(),online:false},
    {key:"Steam:76561198000000004",name:"Freya",character:"Freya",playSec:56200,sessions:28,deaths:8,lastSeen:new Date(Date.now()-5*86400000-3*3600000).toISOString(),online:false},
  ],
  totals:{playSec:912400,deaths:61,sessions:171,modUpdates:38,modInstalls:9},
  feed:[
    {t:new Date(Date.now()-4*60000).toISOString(),kind:"join",name:"Bjorn",character:"Bjorn Ironside"},
    {t:new Date(Date.now()-26*60000).toISOString(),kind:"death",name:"Astrid",character:"Astrid"},
    {t:new Date(Date.now()-64*60000).toISOString(),kind:"join",name:"Astrid",character:"Astrid"},
    {t:new Date(Date.now()-4.5*3600000).toISOString(),kind:"start"},
    {t:new Date(Date.now()-4.6*3600000).toISOString(),kind:"stop"},
    {t:new Date(Date.now()-9*3600000).toISOString(),kind:"leave",name:"Leif",character:"Leif the Lost"},
    {t:new Date(Date.now()-2*86400000).toISOString(),kind:"crash"},
  ],
  mods:[
    {t:new Date(Date.now()-4.5*3600000).toISOString(),kind:"modup",mod:"Therzie-Warfare",from:"1.9.5",to:"1.9.7"},
    {t:new Date(Date.now()-4.5*3600000).toISOString(),kind:"modup",mod:"ValheimModding-Jotunn",from:"2.26.0",to:"2.26.2"},
    {t:new Date(Date.now()-6*86400000).toISOString(),kind:"modin",mod:"Azumatt-AzuExtendedPlayerInventory",to:"1.4.6"},
  ],
};
/* "13d 0h" / "4h 32m" / "7m" / "40s" - big spans coarse, small spans exact.
   Intl.DurationFormat where the runtime has it, which is what turns that into
   "4 ч 32 мин" in Russian without a second table here. */
function skDur(sec){
  const L=intl(); if(L) return L.fmtDuration(sec);
  sec=Math.max(0,Math.round(Number(sec)||0));
  const d=Math.floor(sec/86400),h=Math.floor(sec%86400/3600),m=Math.floor(sec%3600/60);
  if(d>0) return d+"d "+h+"h";
  if(h>0) return h+"h "+m+"m";
  if(m>0) return m+"m";
  return sec+"s";
}
const SKALD_ICON={join:"→",leave:"←",death:"†",start:"ᚠ",stop:"ᛪ",crash:"ᚦ"};
/* One whole sentence per happening, not a verb glued behind a name: the three that
   name a viking carry a {name} slot, so a language that puts the name last can. The
   property names end in Id because that is how the catalog gate sees a table that
   holds ids rather than English. */
const SKALD_VERB={joinId:"skald.feed.join",leaveId:"skald.feed.leave",deathId:"skald.feed.death",
  startId:"skald.feed.start",stopId:"skald.feed.stop",crashId:"skald.feed.crash"};
/* The name inside one of those sentences keeps its own weight. Same trick as bold():
   the markup rides the slot, so the entry stays a plain sentence. */
const skaldName=s=>`<span class="skw">${esc(s)}</span>`;
let SKALD=null;
async function skaldFetch(){
  if(!Native.available) return SKALD_MOCK;
  const r=await rpc("analytics.overview",{});
  return r===FAIL?null:r;
}
let skaldBusy=false;
async function skaldRefresh(){
  if(skaldBusy) return; skaldBusy=true;
  try{
    const d=await skaldFetch();
    if(d){SKALD=d;renderSkald();}
  }finally{skaldBusy=false;}
}
function renderSkald(){
  const d=SKALD; if(!d) return;
  const u=d.uptime||{},t=d.totals||{};
  $("#skUptime").textContent=skDur(u.totalSec);
  const now=$("#skNowPill");
  if(d.running&&(u.currentSec||0)>0){now.style.display="";now.textContent=T("skald.uptime.now",{span:skDur(u.currentSec)});}
  else now.style.display="none";
  $("#skStarts").textContent=u.starts??0;
  const cp=$("#skCrashPill");
  if((u.crashes||0)>0){cp.style.display="";cp.textContent=T("skald.starts.crashes.pill",{count:u.crashes});}
  else cp.style.display="none";
  $("#skVikings").textContent=(d.players||[]).length;
  const vsub=T("skald.vikings.sub",{count:t.sessions??0});
  $("#skVikingsSub").textContent=vsub; $("#skVikingsSub").title=vsub;
  $("#skDeaths").textContent=t.deaths??0;
  $("#skModUps").textContent=t.modUpdates??0;
  const msub=T("skald.mods.sub",{count:t.modInstalls??0});
  $("#skModUpsSub").textContent=msub; $("#skModUpsSub").title=msub;
  /* A journal with no first date is a different sentence, not this one with a gap in
     the middle of it: a lone separator before "counted on this machine" reads as a typo. */
  $("#skaldSub").textContent=d.since
    ?T("skald.sub.since",{date:new Date(d.since).toLocaleDateString(LOC())})
    :T("skald.sub.local");
  /* playtime per viking */
  const rows=(d.players||[]).map(p=>{
    const nm=p.name||p.character||p.key||"";
    const ch=(p.character&&p.character!==p.name)?p.character:"";
    const seen=agoAt(p.lastSeen);
    return `<tr><td title="${esc(nm+(ch?" ("+ch+")":""))}"><span class="vdot ${p.online?"on":"off"}" style="display:inline-block;margin-right:8px"></span>`+
    `<span class="vname">${esc(nm)}</span>`+
    (ch?` <span class="subval">(${esc(ch)})</span>`:"")+`</td>`+
    `<td class="mono" title="${esc(skDur(p.playSec))}">${skDur(p.playSec)}</td><td class="mono">${p.sessions??0}</td><td class="mono">${p.deaths??0}</td>`+
    `<td class="mono" title="${p.online?"":esc(seen)}">${p.online?`<span style="color:var(--moss)">${esc(T("skald.players.online"))}</span>`:seen}</td></tr>`;
  }).join("");
  $("#skPlayerTable").innerHTML=rows
    ||`<tr><td colspan="5">${emptyState({mark:"ᛗ",title:T("skald.players.empty.title"),
        reason:T("skald.players.empty.reason")})}</td></tr>`;
  esWire($("#skPlayerTable"));
  /* happenings feed */
  const feed=(d.feed||[]).map(e=>{
    const who=e.name||e.character;
    const id=SKALD_VERB[e.kind+"Id"];
    const what=!id?esc(e.kind)
      :(e.kind==="join"||e.kind==="leave"||e.kind==="death")
        ?T(id,{name:skaldName(who||"?")})
        :esc(T(id));
    return `<div class="skrow"><span class="skk ${e.kind}">${SKALD_ICON[e.kind]||"·"}</span><span>${what}</span><span class="skt">${agoAt(e.t)}</span></div>`;
  }).join("");
  $("#skFeed").innerHTML=feed
    ||emptyState({compact:true,mark:"ᛋ",title:T("skald.feed.empty.title"),
        reason:T("skald.feed.empty.reason")});
  esWire($("#skFeed"));
  /* mod chronicle */
  const mods=(d.mods||[]).map(e=>{
    const ver=e.kind==="modin"?(e.to?"v"+e.to:""):((e.from?e.from+" → ":"")+(e.to||""));
    return `<div class="skrow"><span class="skk" style="color:var(--amber)">${e.kind==="modin"?"ᚨ":"ᚱ"}</span>`+
      `<span>${ver
        ?(e.kind==="modin"
           ?T("skald.mods.installed.version",{mod:skaldName(e.mod||"?"),version:esc(ver)})
           :T("skald.mods.updated.version",{mod:skaldName(e.mod||"?"),version:esc(ver)}))
        :(e.kind==="modin"
           ?T("skald.mods.installed",{mod:skaldName(e.mod||"?")})
           :T("skald.mods.updated",{mod:skaldName(e.mod||"?")}))}</span>`+
      `<span class="skt">${agoAt(e.t)}</span></div>`;
  }).join("");
  $("#skModFeed").innerHTML=mods
    ||emptyState({compact:true,mark:"ᚱ",title:T("skald.mods.empty.title"),
        reason:T("skald.mods.empty.reason")});
  esWire($("#skModFeed"));
}
/* Clearing the journal. Every realm's numbers live in the one file, so this clears all of
   them at once, which the prompt has to say out loud. The old journal is set aside as a
   single backup copy rather than thrown away outright. */
$("#skResetBtn")?.addEventListener("click",()=>{
  confirmModal(()=>T("skald.reset.confirm.title"),
    ()=>`<div class="subval">${esc(T("skald.reset.confirm.body"))}</div>`+
    `<div class="subval" style="margin-top:6px">${esc(T("skald.reset.confirm.note"))}</div>`,
    ()=>T("common.button.reset"),async()=>{
      if(!Native.available){toast("ᛪ "+T("skald.reset.preview.toast"));return;}
      const r=await rpc("analytics.reset",{});
      if(r===FAIL) return;
      /* Kept and not kept are two sentences: a reset with nothing set aside must not
         trail a separator with nothing behind it. */
      toast("ᛪ "+(r.kept?T("skald.reset.kept.toast",{file:r.kept}):T("skald.reset.done.toast")));
      logLine("warn","[BakaLoader] statistics journal reset"+(r.kept?" (kept as "+r.kept+")":""));
      SKALD=null;
      skaldRefresh();
    });
});

/* ---------- VIKINGS (players) ---------- */
/* Column count of the roster table, so the empty state always spans the whole row. */
const VIK_COLS=9;
/* Short platform tag off the id the roster already carries. Steam accounts are a
   bare 17-digit id; crossplay accounts arrive as "<Platform>_<id>". Anything that
   matches neither shape gets no guess, just a dash. */
/* One table, and it says exactly what PlayerPlatforms.DisplayName says in C#: the same
   player must not read as "Switch" in the roster and "Nintendo" in the Network drawer.
   The aliases are the ones TryGetValidPlatform accepts, plus a few that only ever turn
   up as an id prefix (xboxlive, microsoft, epic), which the C# side passes through. */
const PLATFORM_NAMES={steam:"Steam",v:"Steam",xbox:"Xbox",xboxlive:"Xbox",microsoft:"Xbox",x:"Xbox",
  playstation:"PlayStation",psn:"PlayStation",s:"PlayStation",
  nintendo:"Nintendo Switch",switch:"Nintendo Switch",n:"Nintendo Switch",
  gamecenter:"Apple Game Center",a:"Apple Game Center",playfab:"Crossplay",epic:"Epic"};
/* Own keys only. A player id such as "constructor_123" or "toString_9" would otherwise
   find a function on Object.prototype and print it as the player's platform. */
function platformName(key){
  const k=String(key==null?"":key).toLowerCase();
  return Object.prototype.hasOwnProperty.call(PLATFORM_NAMES,k)?PLATFORM_NAMES[k]:"";
}
function platformTag(id){
  const v=String(id==null?"":id).trim(); if(!v) return "";
  const u=v.indexOf("_");
  if(u>0){const pre=v.slice(0,u);return platformName(pre)||pre;}
  return /^\d{17}$/.test(v)?"Steam":"";
}
/* The roster now carries the platform the peer actually joined on, so use it and keep the
   id guess only as a fallback. Ids stopped having a fixed shape, so guessing can be wrong. */
function playerPlatform(p){
  const named=String((p&&p.platform)||(p&&p.Platform)||"").trim();
  if(named) return platformName(named)||named;
  return platformTag(p&&p.PlayerId);
}

/* RCON target mirrors MainWindow.GetRconTargetName: LastStatusCharacter || PlayerName.
   The DTO embeds the character as "Name (Char)" in displayName. */
function playerTarget(p){
  const m=/\(([^)]+)\)\s*$/.exec(p.displayName||"");
  return (m&&m[1])||p.PlayerName||p.PlayerId;
}
/* Seconds this player has been online in the CURRENT session. Their status last
   flipped when they joined, so that timestamp is the session start. */
function playerSessionSec(p){
  if(!p||p.status!=="Online") return 0;
  const t=new Date(p.lastStatusChange).getTime();
  if(isNaN(t)) return 0;
  return Math.max(0,(Date.now()-t)/1000);
}
/* Journal facts for a player: total playtime, deaths and visit count, read from the
   local analytics journal (the same numbers the Statistics hall shows). */
function playerJournal(p){return (p&&S.journal&&S.journal[p.key])||null;}

/* The word in the Status pill. The roster carries the status as the token the C# side
   writes (Game/Statuses.cs), and that token is what the sort order and the pill's colour
   read; only the word on screen comes out of the catalog. A token this table has never
   heard of is shown exactly as it arrived rather than as a dotted id, so a status added
   on the C# side reads as itself until somebody keys it. */
const VIK_STATUS_WORDS={
  Online:{wordId:"vikings.status.online"},
  Offline:{wordId:"vikings.status.offline"},
  Joining:{wordId:"vikings.status.joining"},
  Leaving:{wordId:"vikings.status.leaving"},
};
function playerStatusWord(status){
  const key=String(status==null?"":status);
  return Object.prototype.hasOwnProperty.call(VIK_STATUS_WORDS,key)
    ?T(VIK_STATUS_WORDS[key].wordId):key;
}

const VIK_DESC_FIRST=new Set(["session","playtime","deaths"]);
const VIK_RANK={Online:0,Joining:1,Leaving:1};
function sortedPlayers(list){
  const {col,dir}=S.vikSort;
  if(!col||!dir) return list;
  const m=(dir===1)!==VIK_DESC_FIRST.has(col)?1:-1;
  const txt=(a,b,f)=>cmpText(f(a)||"",f(b)||"");
  const num=(a,b,f)=>(f(a)||0)-(f(b)||0);
  const out=[...list];
  if(col==="name") out.sort((a,b)=>m*txt(a,b,p=>p.displayName));
  else if(col==="status") out.sort((a,b)=>m*((VIK_RANK[a.status]??2)-(VIK_RANK[b.status]??2)||txt(a,b,p=>p.displayName)));
  else if(col==="platform") out.sort((a,b)=>m*txt(a,b,playerPlatform));
  else if(col==="session") out.sort((a,b)=>m*num(a,b,playerSessionSec));
  else if(col==="playtime") out.sort((a,b)=>m*num(a,b,p=>(playerJournal(p)||{}).playSec));
  else if(col==="deaths") out.sort((a,b)=>m*num(a,b,p=>(playerJournal(p)||{}).deaths));
  else if(col==="seen") out.sort((a,b)=>m*num(a,b,p=>new Date(p.lastStatusChange).getTime()||0));
  else if(col==="pos") out.sort((a,b)=>m*txt(a,b,p=>p.position));
  return out;
}
/* Which columns the window is currently too narrow to carry. Read from the live
   layout rather than re-deriving the breakpoints, so the note can never drift
   from the CSS. */
/* Two whole sentences rather than one glued around a joined list. The list used to be
   built with gone.join(" and ") and handed to a sentence that always said "need", so one
   hidden column read "position need a wider window"; and a language that does not join a
   pair of nouns with a word in the middle had nowhere to put its own. One key per count
   keeps the verb right and lets the pair be worded however the language pairs things. */
function renderVikCols(){
  const note=$("#vikColsNote"); if(!note) return;
  const gone=[];
  const pos=document.querySelector("#page-vikings th.vik-pos");
  const dea=document.querySelector("#page-vikings th.vik-deaths");
  if(pos&&getComputedStyle(pos).display==="none") gone.push(T("vikings.cols.name.position"));
  if(dea&&getComputedStyle(dea).display==="none") gone.push(T("vikings.cols.name.deaths"));
  note.textContent=gone.length>1
    ?T("vikings.cols.hidden.two",{first:gone[0],second:gone[1]})
    :(gone.length?T("vikings.cols.hidden.one",{column:gone[0]}):"");
}
/* Recomputed by the shared resize dispatcher, but only while the roster is on screen:
   reading the header cells' computed display on every tick of a drag paid for a note
   nobody could see. goPage brings it up to date when the hall is opened. */

function renderPlayers(){
  const rank=s=>VIK_RANK[s]??2;
  const base=[...S.players].sort((a,b)=>rank(a.status)-rank(b.status)||cmpExact(a.displayName||"",b.displayName||""));
  const list=sortedPlayers(base);
  const online=base.filter(p=>p.status==="Online").length;
  $("#vikSub").textContent=T("vikings.sub.online",{online:online,total:base.length});
  $("#homeVikPill").textContent=T("hearth.vikings.pill",{count:online});
  renderSortMarks("#page-vikings th.sortable",S.vikSort,VIK_DESC_FIRST);
  renderVikCols();
  $("#homeVik").innerHTML=base.slice(0,4).map(p=>{
    const on=p.status==="Online";
    const when=fmtT(p.lastStatusChange);
    return `<div class="vrow"${on?"":' style="opacity:.4"'}><span class="vdot ${on?"on":"off"}"></span><span class="vname">${esc(p.displayName)}</span><span class="vsub">${esc(on?T("hearth.vikings.row.joined",{when}):T("hearth.vikings.row.seen",{when}))}</span></div>`;
  }).join("")||emptyState({compact:true,mark:"ᛗ",title:T("hearth.vikings.empty.title"),
      reason:T("hearth.vikings.empty.reason")});
  esWire($("#homeVik"));
  const tb=$("#vikTable");
  /* Positions come off the running server's own player list, so an offline player and a
     stopped or RCON-less server all show the dash rather than a stale coordinate. */
  const noPos=T("vikings.row.no_position.title");
  tb.innerHTML=list.map((p,i)=>{
    const pill=p.status==="Online"?"green":(p.status==="Offline"?"blue":"amber");
    const j=playerJournal(p);
    const sess=playerSessionSec(p);
    const who=p.displayName||p.PlayerId||"player";
    return `<tr data-i="${i}"${p.status==="Offline"?' class="dim"':""}>`+
      `<td title="${esc(who)}"><span class="vdot ${p.status==="Online"?"on":"off"}" style="display:inline-block;margin-right:9px"></span><strong>${esc(who)}</strong></td>`+
      `<td><span class="pill ${pill}">${esc(playerStatusWord(p.status))}</span></td>`+
      `<td class="mono">${esc(playerPlatform(p))||"-"}</td>`+
      `<td class="mono num">${sess?esc(skDur(sess)):`<span class="vdash" title="${esc(T("vikings.row.not_online.title"))}">-</span>`}</td>`+
      `<td class="mono num">${j&&j.playSec?esc(skDur(j.playSec)):`<span class="vdash" title="${esc(T("vikings.row.nothing_recorded.title"))}">-</span>`}</td>`+
      `<td class="mono" title="${esc(agoAt(p.lastStatusChange))}">${p.status==="Online"?esc(T("vikings.row.online_now")):esc(agoAt(p.lastStatusChange))}</td>`+
      `<td class="mono num vik-deaths">${j&&j.deaths!=null?j.deaths:`<span class="vdash" title="${esc(T("vikings.row.nothing_recorded.title"))}">-</span>`}</td>`+
      `<td class="mono num vik-pos">${p.position?esc(p.position):`<span class="vdash" title="${esc(noPos)}">-</span>`}</td>`+
      `<td class="rowmenu-cell"><button class="rowmenu" data-i="${i}" title="${esc(T("vikings.row.actions.title"))}" aria-label="${esc(T("vikings.row.actions.aria",{name:who}))}">⋯</button></td></tr>`;
  }).join("")||`<tr><td colspan="${VIK_COLS}">${emptyState({mark:"ᛗ",title:T("vikings.empty.title"),
      reason:T("vikings.empty.reason"),
      action:{name:"copyJoin",label:T("vikings.empty.action")}})}</td></tr>`;
  esWire(tb);
  tb._list=list;
  renderAppBar();   // after #homeVik is filled: the preview counts its dots
}
wireSort("#page-vikings th.sortable",S.vikSort,()=>renderPlayers());
/* The "..." button opens exactly the menu a right-click opens, so the roster's
   actions are reachable from the keyboard and from a trackpad without a second
   button. It is positioned off the button, not off the pointer. */
$("#vikTable").addEventListener("click",e=>{
  const b=e.target.closest(".rowmenu"); if(!b) return;
  e.stopPropagation();
  if(!Native.available){toast("ᛗ "+T("vikings.actions.preview.toast"));return;}
  const p=($("#vikTable")._list||[])[+b.dataset.i]; if(!p) return;
  const r=b.getBoundingClientRect();
  openPlayerMenu(r.left,r.bottom+4,p);
});

/* ---------- STATISTICS JOURNAL (roster playtime + deaths) ----------
   The roster's Playtime and Deaths come from the same local journal the Statistics
   hall reads. Pulled on a leash: once when the hall opens and at most every 20s
   while it is on screen, so the 500ms roster poll never turns into a journal poll. */
let _journalAt=0,_journalBusy=false;
async function refreshJournal(force){
  if(!Native.available||_journalBusy) return;
  if(!force&&Date.now()-_journalAt<20000) return;
  _journalBusy=true;
  try{
    /* quiet call: a journal hiccup must not toast over the hall */
    const r=await Native.call("analytics.overview",{}).catch(()=>null);
    if(r&&Array.isArray(r.players)){
      const m={};
      r.players.forEach(a=>{if(a&&a.key)m[a.key]={playSec:a.playSec,deaths:a.deaths,sessions:a.sessions};});
      S.journal=m;
      _journalAt=Date.now();
      renderPlayers();
    }
  }finally{_journalBusy=false;}
}
async function refreshPlayers(){
  const r=await rpc("players.list");
  if(r===FAIL||!Array.isArray(r)) return;
  S.players=r;
  renderPlayers();
}
$("#vikTable").addEventListener("contextmenu",e=>{
  if(!Native.available) return;
  const tr=e.target.closest("tr[data-i]"); if(!tr) return;
  e.preventDefault();
  const p=($("#vikTable")._list||[])[+tr.dataset.i]; if(!p) return;
  openPlayerMenu(e.clientX,e.clientY,p);
});
async function openPlayerMenu(x,y,p){
  const id=p.PlayerId, tgt=playerTarget(p);
  const [isAdmin,isPerm,isBan]=await Promise.all([
    rpc("players.isListed",{list:"Admin",id}),
    rpc("players.isListed",{list:"Permitted",id}),
    rpc("players.isListed",{list:"Banned",id}),
  ]);
  const rcon=!!S.caps.rcon, dev=!!S.caps.devcommands;
  const noR=T("vikings.menu.tip.no_rcon"), noD=T("vikings.menu.tip.no_devcommands");
  /* Half of this menu reaches for the character standing in the world, so it has
     nothing to act on while that player is away: heal, smite, teleport, a spawn at
     their feet and a kick all need them connected. Online is exactly what the
     roster pill calls online, so a player still joining or on their way out reads
     as away here too. The list actions below write files on disk and work either
     way, which is why they stay live for an offline player. */
  const live=p.status==="Online";
  /* A player still joining is connected, so a kick can reach them; the other live
     actions want a character standing in the world, which a joining player is not yet. */
  const connected=live||p.status==="Joining";
  const noLive=T("vikings.menu.tip.online_only");
  const noConn=T("vikings.menu.tip.connected_only");
  const items=[
    {r:"ᛏ",label:T("vikings.menu.heal"),disabled:!rcon||!live,tip:rcon?noLive:noR,
      fn:()=>doPlayerAct("players.heal",{target:tgt},T("vikings.heal.done.toast",{name:tgt}))},
    {r:"ᚦ",label:T("vikings.menu.smite"),danger:true,confirm:true,disabled:!rcon||!live,tip:rcon?noLive:noR,
      fn:()=>doPlayerAct("players.smite",{target:tgt},T("vikings.smite.done.toast",{name:tgt}))},
    {r:"ᛒ",label:T("vikings.menu.teleport"),disabled:!rcon||!live,tip:rcon?noLive:noR,
      fn:()=>promptModal(()=>T("vikings.teleport.prompt.title",{name:tgt}),()=>T("vikings.teleport.prompt.placeholder"),
        v=>doPlayerAct("players.teleport",{target:tgt,destination:v},
          T("vikings.teleport.done.toast",{name:tgt,destination:v})))},
    {r:"ᛟ",label:T("vikings.menu.spawn"),disabled:!dev||!live,tip:dev?noLive:noD,fn:()=>openSpawnModal(p)},
    "hr",
    {r:"ᚨ",label:isAdmin===true?T("vikings.menu.demote_admin"):T("vikings.menu.promote_admin"),
      fn:()=>setPlayerList(p,"Admin",isAdmin!==true)},
    {r:"ᚹ",label:isPerm===true?T("vikings.menu.unpermit"):T("vikings.menu.permit"),
      fn:()=>setPlayerList(p,"Permitted",isPerm!==true)},
    "hr",
    {r:"ᚲ",label:T("vikings.menu.kick"),danger:true,confirm:true,disabled:!rcon||!connected,tip:rcon?noConn:noR,
      fn:()=>doKick(p,tgt)},
    {r:"ᛉ",label:isBan===true?T("vikings.menu.unban"):T("vikings.menu.ban"),
      danger:isBan!==true,confirm:isBan!==true,fn:()=>doBan(p,isBan===true,tgt)},
    "hr",
    {r:"ᛁ",label:T("vikings.menu.copy_id"),
      fn:()=>{navigator.clipboard?.writeText(id||"").catch(()=>{});toast("ᛁ "+T("vikings.copy_id.done.toast",{id:id}));}},
  ];
  if(p.status==="Offline"){
    items.push({r:"ᛪ",label:T("vikings.menu.remove_offline"),danger:true,confirm:true,
      fn:async()=>{const r=await rpc("players.remove",{key:p.key}); if(r===FAIL)return;
        toast("ᛪ "+T("vikings.remove.done.toast",{name:p.displayName||id})); refreshPlayers();}});
  }
  const again=()=>openPlayerMenu(x,y,p);
  ctxOpen(x,y,p.displayName||id||T("vikings.menu.head.fallback"),items,again);
}
/* okMsg arrives already worded: every caller above asks the catalog for the whole
   sentence, name and all, rather than handing a verb here to be glued to a player. */
async function doPlayerAct(method,params,okMsg){
  const r=await rpc(method,params);
  if(r===FAIL) return;
  if(r===false){toast("ᚦ "+T("vikings.act.undelivered.toast"));return;}
  toast("ᛒ "+okMsg);
  logLine("cmd","> "+method.replace("players.","")+" "+(params.target||params.playerName||""));
}

/* KICK-BEGIN
   ------------------------------------------------------------------------------------
   What a kick actually did, read rather than assumed.

   A kick used to go through doPlayerAct, which raises "Kicked X" for any answer at all
   that is not literally false. The server has four things to say back: it kicked
   somebody and names them, nobody of that name or id is on the server, it refused the
   line, or it says nothing at all (the vanilla console answers a kick with silence).
   Three of those four used to read as a kick that had happened.

   That mattered most for the case this was written for. A name that reached the app in
   the wrong encoding matches nobody, so the kick landed on no one while the player
   stood there and the host was told it was done. The app sends the platform id now and
   only falls back to the name, so this reader is the second half of the same fix: it
   says which of the four happened. Pure, like the kill-all reader above, so it can be
   driven as a table with no browser in the room
   (scripts/ui/kick_reply_selftest.js runs the real code out of this file). */

/**
 * What the server said about a kick.
 * @param {string} reply the answer, exactly as it arrived
 * @returns {object} kind, plus what that kind carries:
 *   kicked  {name}  it disconnected somebody, and names who
 *   missing {name}  nobody of that name or id is on the server
 *   refused {}      Error:, Unknown command, the usage line
 *   silent  {}      it was carried and the server said nothing
 *   unknown {}      a shape this version has not met
 */
function kickReply(reply){
  const said=String(reply==null?"":reply).trim();
  if(!said) return {kind:"silent"};
  /* Before the refusal test, because this one opens with "Error:" too and it is the one
     answer that says something the host can act on: they have the wrong name. */
  const missing=/^Error:\s*no player named\s*'([\s\S]*)'\s*is online/i.exec(said);
  if(missing) return {kind:"missing",name:missing[1]};
  if(consoleRefused(said)) return {kind:"refused"};
  if(/^Usage:\s*kick\b/i.test(said)) return {kind:"refused"};
  const kicked=/^Kicked:\s*(.+)$/i.exec(said);
  if(kicked) return {kind:"kicked",name:kicked[1].trim()};
  return {kind:"unknown"};
}

/**
 * Which name to say a kick landed on. The SERVER's spelling wins where it gave one,
 * because that is the spelling it acted on, with one answer taken back off it: the
 * 1.6.0 plugin answers "Kicked: " with the exact text it was handed, and the app hands
 * it the host id first, so that older server names an ID where the host is reading for
 * a person. An answer that is only the id we sent is no name at all, and the row's own
 * is better. A leading "Steam_" is not part of an id, on either side of the comparison,
 * because the plugin can hand the id back with the prefix or without it.
 * @param {string} said the name the server gave back
 * @param {string} who the row's own name, which is what the host was looking at
 * @param {string} hostId the id this kick was sent with, or nothing
 */
function kickedName(said,who,hostId){
  const bare=v=>{const s=String(v==null?"":v).trim();
    return s.indexOf("Steam_")===0?s.slice(6):s;};
  const name=bare(said), sent=bare(hostId);
  if(!name) return who;
  if(sent&&name===sent) return who||said;
  return said;
}
/* KICK-END */

/** The toast for a kick, worded. The name comes from the SERVER where it named one,
    because that is the spelling it acted on; the row's own name is the fallback, and
    it is also what an answer that only echoes the id back falls to. */
function kickToast(read,who,hostId){
  switch(read.kind){
    case "kicked":  return "ᛒ "+T("vikings.kick.done.toast",{name:kickedName(read.name,who,hostId)});
    case "missing": return "ᚦ "+T("vikings.kick.nobody.toast",{name:read.name||who});
    case "refused": return "ᚦ "+T("pal.console.refused.toast");
    /* The vanilla console says nothing back at all, so silence is not a failure and must
       not be worded as one. It is also not a kick anybody watched happen. */
    case "silent":  return "ᚦ "+T("pal.kill.toast.silent");
    default:        return "ᚦ "+T("pal.kill.toast.unreadable");
  }
}

/** The kick itself. hostId is the id the server knows this player by; the C# side sends
    it first and falls back to the name, so a name it never read correctly is no longer
    the only way to reach somebody. */
async function doKick(p,tgt){
  /* Held, rather than written into the call, because the toast has to know what was
     sent to tell an answer that names a person from one that echoes the id back. */
  const hostId=(p&&p.hostId)||null;
  const r=await rpc("players.kick",{target:tgt,hostId});
  if(r===FAIL) return;
  /* null is the C# side saying it never went out: no server, or no RCON. */
  if(r===false||r==null){toast("ᚦ "+T("vikings.act.undelivered.toast"));return;}
  toast(kickToast(kickReply(r),tgt,hostId));
  logLine("cmd","> kick "+tgt);
}
/* The two lists this menu writes, and the three whole sentences each of them says. The
   toast used to be built as "Added to "+list.toLowerCase()+" list · "+who, which hands a
   translator a list name to bend into the middle of a sentence it never sees, and puts
   the English word "admin" inside every language. One key per list per outcome instead. */
const VIK_LISTS={
  Admin:{addedId:"vikings.list.admin.added.toast",removedId:"vikings.list.admin.removed.toast",
         failedId:"vikings.list.admin.failed.toast"},
  Permitted:{addedId:"vikings.list.permitted.added.toast",removedId:"vikings.list.permitted.removed.toast",
             failedId:"vikings.list.permitted.failed.toast"},
};
async function setPlayerList(p,list,on){
  const who=p.displayName||p.PlayerId;
  const words=VIK_LISTS[list];
  const r=await rpc("players.setList",{list,id:p.PlayerId,on});
  /* a write that failed must never read as done - the list on disk is unchanged */
  if(r===FAIL){toast("ᚦ "+T(words.failedId,{name:who}));return;}
  toast("ᚨ "+T(on?words.addedId:words.removedId,{name:who}));
  logLine("info","[BakaLoader] "+list.toLowerCase()+" list "+(on?"+ ":"- ")+who);
}
async function doBan(p,unban,tgt){
  const r=await rpc("players.setList",{list:"Banned",id:p.PlayerId,on:!unban});
  if(r===FAIL){toast("ᚦ "+T("vikings.list.banned.failed.toast",{name:tgt}));return;}
  toast(unban?("ᛉ "+T("vikings.unban.done.toast",{name:tgt})):("ᛉ "+T("vikings.ban.done.toast",{name:tgt})));
  logLine(unban?"info":"warn","[BakaLoader] "+(unban?"unbanned ":"banned ")+(p.displayName||p.PlayerId));
  if(!unban&&S.caps.rcon&&p.status==="Online"){
    const k=await rpc("players.kick",{target:tgt,hostId:p.hostId||null});
    /* The ban is written either way: it is a file on disk and it holds at their next
       login. What the log must not say is that they were thrown off when nobody
       watched it happen. ONE of the five answers names a kicked player, and that is
       the only one this line may claim a kick for: a refusal, a silence and a shape
       this version has not met are no more a kick than the miss is, and the toast a
       few lines above already words all of them that way. */
    if(k!==FAIL){
      const read=kickReply(k);
      logLine("warn",read.kind==="kicked"
        ?"[BakaLoader] kicked "+tgt+" to enforce ban"
        :"[BakaLoader] banned "+tgt+", but the kick reached nobody");
    }
  }
}
/* spawn-item picker (items.search) */
function openSpawnModal(p){
  const tgt=playerTarget(p);
  /* Everything the picker is holding lives OUT HERE, where the rebuilder can read it,
     the way promptModal keeps the name that was typed. It used to live in the closure
     the rebuilder replaces, so a language switch reopened the dialog aimed at the right
     viking and holding nothing else: the search text gone, the item unpicked, the
     amount back at one. Finding an item again is the expensive half of this dialog, and
     a switch is a deliberate act nobody performs to clear a form.
     The picked item is held as the row itself and matched back by PrefabName after each
     search, because the list is fetched again on every redraw and the objects that come
     back are new ones. */
  let query="", picked=null, amountText="1", gradeText="0", searchSeq=0;
  const again=()=>{
    const m=modalOpen(
      `<div class="mtitle">${esc(T("vikings.spawn.title",{name:tgt}))}</div>`+
      `<input type="text" id="spQ" placeholder="${esc(T("vikings.spawn.search.placeholder"))}" spellcheck="false" autocomplete="off">`+
      `<div class="pick-list" id="spList"></div>`+
      `<div class="mrow"><label>${esc(T("vikings.spawn.amount.label"))}</label><input type="number" id="spAmt" value="1" min="1" max="9999">`+
      `<label id="spLqLbl">${esc(T("vikings.spawn.level.label"))}</label><input type="number" id="spLq" value="0" min="0" max="5" disabled></div>`+
      `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mCancel">${esc(T("common.button.cancel"))}</button><button class="btn btn-ember btn-sm" id="mOk" disabled>${esc(T("vikings.spawn.ok"))}</button></div>`,
      again);
    const list=m.querySelector("#spList"), q=m.querySelector("#spQ"),
          amt=m.querySelector("#spAmt"), lq=m.querySelector("#spLq"),
          lqLbl=m.querySelector("#spLqLbl"), okB=m.querySelector("#mOk");
    /* The three boxes, put back before anything is wired: the markup carries the empty
       dialog's values and this is the state the host left it in. */
    q.value=query; amt.value=amountText; lq.value=gradeText;
    /* The grade box and the button, gated on whatever is picked. One piece of code for
       both roads into this state - a click, and a redraw - so the two cannot drift. */
    const gate=()=>{
      okB.disabled=!picked;
      const hasLq=!!(picked&&(picked.HasLevel||picked.HasQuality));
      lq.disabled=!hasLq;
      if(picked) lqLbl.textContent=picked.HasLevel?T("vikings.spawn.level.label"):T("vikings.spawn.quality.label");
      lq.max=picked&&picked.HasLevel?"2":"5";
      if(!hasLq) lq.value=0;
      else if(+lq.value>+lq.max) lq.value=lq.max;
      gradeText=lq.value;
    };
    /* Once now, so a redraw that still holds a picked item shows a live button and an
       open grade box for the whole of the round trip the list below costs, rather than
       a dialog that looks like it has forgotten and then remembers. */
    gate();
    /* Marks the row the picked item sits on in the list that is on screen now. An item
       that is not in this list any more (the host narrowed the search past it) is no
       longer picked, which is what the list used to do to EVERY search. */
    const markPicked=()=>{
      const res=list._res||[];
      const at=picked?res.findIndex(it=>it&&it.PrefabName===picked.PrefabName):-1;
      picked=at<0?null:res[at];
      list.querySelectorAll(".pick-item").forEach(x=>x.classList.remove("sel"));
      if(at>=0){
        const row=list.querySelector(`.pick-item[data-i="${at}"]`);
        if(row) row.classList.add("sel");
      }
      gate();
    };
    async function search(){
      const my=++searchSeq;
      const r=await rpc("items.search",{query:query.trim(),limit:100});
      if(r===FAIL||my!==searchSeq) return;
      const res=r.results||[];
      list.innerHTML=res.map((it,i)=>
        `<div class="pick-item" data-i="${i}"><span>${esc(it.Label)}</span><span class="pk">${esc(it.category||"")}</span><span class="pm">${esc(it.PrefabName)}</span></div>`
      ).join("")||`<div class="pick-item" style="opacity:.5;cursor:default">${esc(T("vikings.spawn.no_match"))}</div>`;
      list._res=res;
      markPicked();
    }
    q.addEventListener("input",()=>{query=q.value;clearTimeout(q._t);q._t=setTimeout(search,150);});
    amt.addEventListener("input",()=>{amountText=amt.value;});
    lq.addEventListener("input",()=>{gradeText=lq.value;});
    list.addEventListener("click",e=>{
      const el=e.target.closest(".pick-item[data-i]"); if(!el) return;
      picked=(list._res||[])[+el.dataset.i]||null;
      markPicked();
    });
    okB.addEventListener("click",async()=>{
      if(!picked) return;
      const amount=Math.min(9999,Math.max(1,parseInt(amt.value,10)||1));
      const levelOrQuality=lq.disabled?0:Math.max(0,parseInt(lq.value,10)||0);
      const item=picked;
      modalClose();
      const r=await rpc("players.spawn",{playerName:tgt,prefab:item.PrefabName,amount,levelOrQuality});
      if(r===FAIL) return;
      /* The server answers every spawn with a line, a refusal as readily as a success, so the
         line is what decides and what gets shown. A bare false is the older shape of this
         answer and still means the same thing. */
      const said=(r&&typeof r==="object"&&r.message)?String(r.message):"";
      if(r===false||(r&&typeof r==="object"&&r.ok===false)){
        toast("ᚦ "+(said?T("vikings.spawn.failed.toast",{detail:said}):T("vikings.spawn.failed.nodetail.toast")));
        logLine("err","[BakaLoader] spawn failed · "+(said||"no reply from the server"));
        return;
      }
      /* The same box means star level on a creature and quality on an item, so the sentence
         says which one rather than gluing a bare word to a number. A spawn with no grade at
         all is its own key, so nothing has to word an empty tail. */
      const done=levelOrQuality>0
        ?(item.HasLevel
          ?T("vikings.spawn.done.level.toast",{count:amount,item:item.Label,name:tgt,grade:levelOrQuality})
          :T("vikings.spawn.done.quality.toast",{count:amount,item:item.Label,name:tgt,grade:levelOrQuality}))
        :T("vikings.spawn.done.toast",{count:amount,item:item.Label,name:tgt});
      /* The server's own line already names the stack count and the quality it settled on,
         which is more than the picker knows, so it wins when there is one. */
      toast("ᛟ "+(said||done));
      /* The saga stays English, so it keeps its own plain tail rather than the worded one. */
      const gradeLog=levelOrQuality>0?" · "+(item.HasLevel?"level":"quality")+" "+levelOrQuality:"";
      logLine("cmd","> spawn "+item.PrefabName+" ×"+amount+gradeLog+" @ "+tgt+(said?" · "+said:""));
    });
    m.querySelector("#mCancel").addEventListener("click",modalClose);
    setTimeout(()=>q.focus(),30);
    search();
    return m;
  };
  return again();
}

/* ---------- WORLD (profile config) ----------
   swOn reads a switch, setT writes one. The reader used to be called T and gave
   that name up to the catalog lookup, which is the one thing in the file that
   earns a one letter name: it wraps nearly every sentence a host reads. The
   writer kept its name because nothing else wants it. */
const swOn=id=>$("#"+id).classList.contains("on");
const setT=(id,on)=>$("#"+id).classList.toggle("on",!!on);
/* toggle → parameter-field gating (mirrors the WinForms enable/disable behavior) */
function syncAdvGates(){
  const gate=(inputId,on)=>{
    const el=$("#"+inputId); if(!el) return;
    el.disabled=!on;
    el.closest(".field")?.classList.toggle("gated",!on);
  };
  gate("fEmptyDelay",swOn("tEmpty"));
  gate("fSchedHours",swOn("tSched"));
  gate("fCrashDelay",swOn("tCrash"));
  gate("fRconPort",swOn("tRcon"));
  gate("fRconPw",swOn("tRcon"));
  updAdvLabels();
}
/* The three labels in Advanced that carry a number. Each is rebuilt from the field
   beside it on every keystroke, so it is a run-time sentence with a named slot rather
   than a static node the walker could fill: index.html leaves them id-less on purpose.
   Two of them count something, so their entries carry the plural categories and the
   category is chosen from the value rather than spelled out here. */
function updAdvLabels(){
  const minutes=$("#fEmptyDelay").value.trim()||"5";
  const hours=$("#fSchedHours").value.trim()||"6";
  const port=$("#fRconPort").value.trim()||"25575";
  $("#tEmptyLbl").textContent=T("world.empty.label",{minutes});
  $("#tSchedLbl").textContent=T("world.sched.label",{hours});
  $("#tRconLbl").textContent=swOn("tRcon")?T("world.rcon.label",{port}):T("world.rcon.off.label");
}
["fEmptyDelay","fSchedHours","fRconPort"].forEach(id=>$("#"+id).addEventListener("input",updAdvLabels));
/* show/hide password chips. The word on the chip says which way the next click goes, so
   it changes under the host's finger and cannot be a static node the walker fills once:
   the id would be written back over HIDE the next time anything walked the page. The
   chip carries its English in index.html for the frame before the catalog lands, and
   this painter owns it from then on - repaintBootCopy runs it when the words arrive. */
const EYE_CHIPS=[["eyePw","fPassword"],["eyeRcon","fRconPw"]];
function renderEyeChips(){
  for(const [chipId,inputId] of EYE_CHIPS){
    const chip=$("#"+chipId), box=$("#"+inputId);
    if(!chip||!box) continue;
    chip.textContent=box.type==="password"?T("common.chip.show"):T("common.chip.hide");
  }
}
function wireEye(chipId,inputId){
  $("#"+chipId).addEventListener("click",()=>{
    const i=$("#"+inputId);
    i.type=i.type==="password"?"text":"password";
    renderEyeChips();
  });
}
wireEye("eyePw","fPassword"); wireEye("eyeRcon","fRconPw");
/* The password rule is a user setting, not a realm one, so it saves on the spot rather
   than waiting for Save Config - which writes the realm's own settings and would carry
   this one nowhere. initUpkeep reads it back out of the same document. */
$("#tPwCheck")?.addEventListener("click",async()=>{
  if(!Native.available){toast("ᛃ "+T("world.pwcheck.preview.toast"));return;}
  const r=await rpc("userprefs.save",{prefs:{EnablePasswordValidation:swOn("tPwCheck")}});
  if(r===FAIL) return;
  toast(swOn("tPwCheck")
    ?"ᛃ "+T("world.pwcheck.on.toast")
    :"ᛃ "+T("world.pwcheck.off.toast"));
});
syncAdvGates(); // initial gate state from the static markup (both modes)
function renderWorldForm(){
  const p=S.prefs; if(!p) return;
  $("#fName").value=p.Name??"";
  $("#fPassword").value=p.Password??"";
  $("#fPort").value=p.Port??2456;
  $("#fSaveInterval").value=p.SaveInterval??600;
  $("#fBackups").value=p.BackupCount??4;
  $("#fBackShort").value=p.BackupIntervalShort??600;
  $("#fBackLong").value=p.BackupIntervalLong??43200;
  $("#fEmptyDelay").value=p.EmptyServerRestartDelayMinutes??5;
  $("#fSchedHours").value=p.ScheduledRestartHours??6;
  $("#fCrashDelay").value=p.AutoRestartDelay??10;
  $("#fRconPort").value=p.RconPort??25575;
  $("#fRconPw").value=p.RconPassword??"";
  $("#fServerExe").value=p.ServerExePath??"";
  $("#fSaveDir").value=p.SaveDataFolderPath??"";
  $("#fArgs").value=p.AdditionalArgs??"";
  setT("tPublic",p.Public); setT("tCrossplay",p.Crossplay);
  setT("tEmpty",p.EmptyServerRestart); setT("tSched",p.ScheduledRestart); setT("tRcon",p.RconEnabled);
  setT("tLogs",p.WriteServerLogsToFile); setT("tAutoStart",p.AutoStart); setT("tCrash",p.AutoRestart);
  syncAdvGates();
  /* This form has just been filled from what is saved, so nothing in it is unsaved.
     The world is the one control that is not filled by the lines above: its list is
     fetched, and the chosen entry only becomes the saved world when it lands. So it is
     ADOPTED when it does, on its own, rather than a second snapshot being taken of the
     whole form. A second snapshot would also swallow anything typed into any other box
     in the meantime, which is a small window but a real one: the fetch can take as long
     as the disk does, and a reader who starts typing a server name into a hall that is
     already on screen would have had it declared saved without ever being saved. */
  worldFormSnapshot();
  renderWorldSelect().then(()=>worldFormAdopt("world")).catch(()=>{});
}

/* ---------- DIRECTORIES ----------
   Two boxes that were reported as blank on a fresh install, and they were: the first
   time setup writes its two answers to the APP WIDE settings, and a profile only holds
   a path when somebody set one for that ONE server. The boxes show the profile's own
   value, so on a normal install they show nothing at all, while Open was resolving the
   same fallback the launcher resolves and opening the right folder. Both halves true,
   and together they read as a setting that had been lost.

   Nothing about where the values live has changed. What is new is the line ABOVE each
   box: the path BakaLoader will really use, variables filled in, with one word saying
   whether it was set for this server or is the default every server falls back to. Open
   moved onto that line, because that line is what Open acts on. The box underneath is
   still the override and is still allowed to be empty.

   Ownership, since two things write to this section: the walker owns the label, the
   Browse button and the note under the section, all of which are static; renderWorldDirs
   owns the Currently value, the source word, Open's words and its tooltip, and both
   placeholders, all of which need a path to say anything at all. No element has both. */
const WORLD_DIRS=[
  {kind:"exe", input:"fServerExe", cur:"curServerExe", src:"curServerExeSrc", note:"noteServerExe",
   open:"btnOpenSrv", browse:"btnBrowseSrv", target:"serverDir", pick:"shell.pickFile",
   effKey:"EffectiveServerExePath", srcKey:"ServerExePathSource",
   openAriaId:"world.server_exe.open.title",
   placeholderId:"world.server_exe.placeholder",
   okId:"paths.ok.exe"},
  {kind:"dir", input:"fSaveDir", cur:"curSaveDir", src:"curSaveDirSrc", note:"noteSaveDir",
   open:"btnOpenSave", browse:"btnBrowseSave", target:"saveData", pick:"shell.pickFolder",
   effKey:"EffectiveSaveDataFolderPath", srcKey:"SaveDataFolderPathSource",
   openAriaId:"world.save_dir.open.title",
   placeholderId:"world.save_dir.placeholder",
   okId:"paths.ok.dir"},
];
/* Every sentence paths.check is allowed to put under a box, and nothing else. The native
   side sends an id (Tools/PathCheck.cs holds the same list) and this is the closed set
   the page will read out of the catalog: an id this does not know shows no note at all
   rather than printing a dotted name on screen. Written out rather than derived, because
   a derived id is one the completeness gate cannot see and a translator cannot find.
   `bad` decides the colour only. None of these is a refusal: Save Config behaves exactly
   as it did before whatever is said here. */
const PATH_PROBLEMS=[
  {problem:"exe_wrong_name",     textId:"paths.problem.exe_wrong_name",     bad:true},
  {problem:"exe_missing",        textId:"paths.problem.exe_missing",        bad:true},
  {problem:"exe_no_data_folder", textId:"paths.problem.exe_no_data_folder", bad:true},
  {problem:"exe_is_folder",      textId:"paths.problem.exe_is_folder",      bad:true},
  {problem:"dir_read_only",      textId:"paths.problem.dir_read_only",      bad:true},
  {problem:"dir_will_be_created",textId:"paths.problem.dir_will_be_created", bad:false},
  {problem:"dir_missing_parent", textId:"paths.problem.dir_missing_parent", bad:true},
  {problem:"dir_is_file",        textId:"paths.problem.dir_is_file",        bad:true},
  {problem:"unreadable",         textId:"paths.problem.unreadable",         bad:true},
  /* Not the path's fault and not a verdict on it, so it is the amber one: the disk did
     not answer inside the budget the native side gives it, and the next keystroke asks
     again. */
  {problem:"timed_out",          textId:"paths.problem.timed_out",          bad:false},
];
function pathProblemRow(id){return id?PATH_PROBLEMS.find(r=>r.textId===id)||null:null;}
/* The two Currently lines, both placeholders, Open's words and tooltip, and the note
   under each box. One pass, from S.prefs and the last check, so a language switch and a
   profile load both arrive here by the same road. */
function renderWorldDirs(){
  const p=S.prefs||{};
  const dirty=worldFormDirtyKeys();
  for(const d of WORLD_DIRS){
    const path=String(p[d.effKey]||"").trim();
    const own=p[d.srcKey]==="profile";
    const line=$("#"+d.cur);
    if(line) line.textContent=path||T("world.dir.unresolved");
    const word=$("#"+d.src);
    if(word){
      word.textContent=own?T("world.dir.source.profile"):T("world.dir.source.default");
      word.classList.toggle("own",own);
    }
    const open=$("#"+d.open);
    if(open){
      open.textContent=T("common.button.open");
      /* Open acts on what is SAVED, so while the box below holds something else the
         tooltip says which of the two folders is about to be opened. That was the whole
         of one report: a path typed, Open pressed, the old folder opened, no word said. */
      open.title=!path?T("world.dir.open.none.title")
        :dirty.indexOf(d.input)>=0?T("world.dir.open.dirty.title",{path})
        :T("world.dir.open.title",{path});
      /* Two buttons both read Open, so each one carries the folder it belongs to as its
         accessible name. Set here rather than in the markup: one owner per element. */
      open.setAttribute("aria-label",T(d.openAriaId));
    }
    const box=$("#"+d.input);
    const fallback=S.userPaths?S.userPaths[d.kind]:null;
    if(box) box.placeholder=fallback?T("world.dir.placeholder",{path:fallback}):T(d.placeholderId);
    renderPathNote(d);
  }
  syncDirsSection();
}
/* The one line under a box. Nothing typed means nothing to say: the note under the whole
   section already explains what an empty box does, and repeating it twice more would
   make the common case the noisy one. */
function renderPathNote(d){
  const el=$("#"+d.note); if(!el) return;
  const typed=String($("#"+d.input)?.value||"").trim();
  const answer=S.pathChecks?S.pathChecks[d.kind]:null;
  if(!typed||!answer||answer.forPath!==typed){
    el.style.display="none"; el.textContent=""; el.style.color="";
    return;
  }
  const row=pathProblemRow(answer.problemId);
  if(answer.problemId&&!row){
    /* A refusal id this page has no sentence for. Saying nothing is right: the id is
       not copy, and a dotted name under a text box is worse than an empty line. */
    el.style.display="none"; el.textContent=""; el.style.color="";
    delete el.dataset.problem;
    return;
  }
  el.textContent=row?T(row.textId):T(d.okId);
  el.style.color=row?(row.bad?"var(--blood)":"var(--amber)"):"var(--moss)";
  el.style.display="";
  /* Which of the two kinds of line this is, so the section below knows whether it is
     worth unfolding for. A confirmation is not: a host who folded the section away is
     not owed a fold back to be told that the path they saved is still fine. */
  if(row) el.dataset.problem="1"; else delete el.dataset.problem;
}
/* A warning folded inside a shut section is a warning nobody reads, so the section opens
   itself the moment a box inside it holds something unsaved or has something to say.
   What this function does and does not do, exactly, because the sentence that used to
   stand here said the second half and the code never did it. It never SHUTS the section:
   nothing in here removes the class, and a section that is already open is left alone on
   the first line. It does open the section again. This runs from renderWorldDirs, which
   runs from renderWorldDirty, which runs on every keystroke on the hall, so a host who
   folds the section away while a box inside it is still unsaved or still has a problem
   to say gets it back the next time anything on the hall is touched. Folding it away for
   good means saving or clearing what is in it first.
   That is the behaviour this is meant to have: the section is folded shut the moment a
   host lands on the hall, and a warning underneath it about a path that will not work is
   worth more than the fold. What it is NOT is a fold that stays folded, and a comment
   promising one was how a reader would come to expect it. */
function syncDirsSection(){
  const sec=$("#secDirs"); if(!sec||sec.classList.contains("open")) return;
  const dirty=worldFormDirtyKeys();
  const wanted=WORLD_DIRS.some(d=>{
    if(dirty.indexOf(d.input)>=0) return true;
    const note=$("#"+d.note);
    return !!(note&&note.style.display!=="none"&&note.dataset.problem==="1");
  });
  if(!wanted) return;
  sec.classList.add("open");
  try{syncCollapsibleTerms();}catch(_){}
  try{rescanScrollCues();}catch(_){}
}
/* What is at the path in the box, asked while it is still being typed and never oftener
   than the host can read an answer. The sequence guard is the usual one: a slow answer
   for an older keystroke must never land on a newer one. */
const PATH_CHECK_MS=320;
const _pathCheckT={}, _pathCheckSeq={};
function schedulePathCheck(kind){
  clearTimeout(_pathCheckT[kind]);
  _pathCheckT[kind]=setTimeout(()=>{runPathCheck(kind).catch(()=>{});},PATH_CHECK_MS);
}
async function runPathCheck(kind){
  const d=WORLD_DIRS.find(x=>x.kind===kind); if(!d) return;
  const typed=String($("#"+d.input)?.value||"").trim();
  if(!typed){S.pathChecks[kind]=null;renderWorldDirs();return;}
  /* No host behind the preview, so whatever BakaPreview.worldDirs seeded stands. */
  if(!Native.available){renderWorldDirs();return;}
  const seq=(_pathCheckSeq[kind]=(_pathCheckSeq[kind]||0)+1);
  const r=await rpc("paths.check",{kind,path:typed});
  if(seq!==_pathCheckSeq[kind]) return;
  if(r===FAIL||!r) return;
  S.pathChecks[kind]=Object.assign({},r,{forPath:typed});
  renderWorldDirs();
}

/* ---------- UNSAVED CHANGES (Settings hall) ----------
   Saving stays manual, which is what the hall has always done and what the report asked
   to keep. What was missing was any sign that there was something to save: the focus
   glow on a box reads as "stored", and a host walked away from a hall full of edits
   believing the opposite.
   So a snapshot is taken whenever the form is filled from what is saved, and every
   keystroke is held against it. Four signs, none of them a dialog: a marker on each
   changed field that survives blur, a notice in the far corner from Save Config, the
   button itself breathing, and a dot on the rail so the state is readable from another
   hall. Nothing blocks navigation: the values stay put and the dot says so. */
/* The controls the form is made of, by the id of the control itself. The world is
   tracked as one key rather than two, because the name can be picked from the list or
   typed into the box that opens under it and those are one setting. The five difficulty
   dials are here too: they ride along with Save Config, so an unsaved one is unsaved.
   Not here: the seed, which is read only, and the process priority, which Save Config
   has never sent anywhere. A marker on a control nothing saves would be a lie. */
const WORLD_FORM_FIELDS=["fName","fPassword","fPort","fSaveInterval","fBackups","fBackShort",
  "fBackLong","fEmptyDelay","fSchedHours","fCrashDelay","fRconPort","fRconPw",
  "fServerExe","fSaveDir","fArgs","fMaxPlayers"];
const WORLD_FORM_TOGGLES=["tPublic","tCrossplay","tEmpty","tSched","tRcon","tLogs",
  "tAutoStart","tCrash"];
/* Which control's field wears the marker for a key that is not a control of its own. */
const WORLD_FORM_MARKER={world:"fWorld"};
function worldFormMarkerFor(key){return WORLD_FORM_MARKER[key]||key;}
/* The whole form as a flat map of strings, which is the only shape two moments of it can
   be compared in. A toggle answers "1" or "0" rather than a class list. */
function worldFormRead(){
  const out={};
  for(const id of WORLD_FORM_FIELDS){const el=$("#"+id);if(el)out[id]=String(el.value??"");}
  for(const id of WORLD_FORM_TOGGLES){const el=$("#"+id);if(el)out[id]=el.classList.contains("on")?"1":"0";}
  for(const key in WORLDGEN){const el=$("#"+WORLDGEN[key].sel);if(el)out[WORLDGEN[key].sel]=String(el.value??"");}
  out.world=worldFieldValue();
  return out;
}
/* The clean state, and null before the form has ever been filled, which is read as
   "nothing is unsaved" rather than as "everything is". */
let WORLD_SNAP=null;
function worldFormSnapshot(){WORLD_SNAP=worldFormRead();renderWorldDirty();}
/* One or more controls are declared clean without touching the rest. This is for a
   control the app itself fills LATER than the form was snapshotted: the max players
   count and the five dials are both fetched, and without this each of them raised the
   notice about a value the host had never seen, let alone changed. */
function worldFormAdopt(...ids){
  if(!WORLD_SNAP){worldFormSnapshot();return;}
  const now=worldFormRead();
  for(const id of ids) if(Object.prototype.hasOwnProperty.call(now,id)) WORLD_SNAP[id]=now[id];
  renderWorldDirty();
}
function worldFormDirtyKeys(){
  if(!WORLD_SNAP) return [];
  const now=worldFormRead(), out=[];
  for(const key in now) if(now[key]!==WORLD_SNAP[key]) out.push(key);
  return out;
}
/* ---------- THE BAND THE NOTICE STANDS IN ----------
   The hall gives up the foot of its own scrolling area while the notice is up, and how
   deep that has to be is the notice's height. That is not a number anyone can write
   down. The notice is two sentences, sentences wrap, and the same two sentences take a
   line more in a language whose words run longer or in a window one step narrower. The
   first shape of this was a fixed 70px in the stylesheet, and it was short: at 1408x880
   in the pseudo catalog the notice stood 75px tall against a 70px band, and the last
   5px of the form sat behind an opaque notice. Which is the very defect the band was
   added to close, reappearing at a width nobody had re-measured at.
   So it is measured. The gap under the notice is read back out of the stylesheet rather
   than repeated here, so the two cannot drift apart, and a few px of slack sit on top so
   a fractional layout never lands the two edges on the same line. */
const UNSAVED_BAND_SLACK=6;
function sizeUnsavedBand(){
  const notice=$("#worldUnsaved"); if(!notice) return;
  const box=notice.getBoundingClientRect();
  /* Nothing on screen to measure: another hall is showing, or there is nothing unsaved
     and the notice is display:none. The last good depth stands, and the observer below
     brings the real one back the instant the notice is up again. */
  if(!box.height) return;
  const foot=parseFloat(getComputedStyle(notice).bottom)||0;
  document.documentElement.style.setProperty(
    "--unsaved-band",Math.ceil(box.height+foot+UNSAVED_BAND_SLACK)+"px");
}
/* Every other way that height can change reaches the same measurement: the window
   getting narrower, a webfont swapping in after first paint, a language switch, the
   notice coming up at all when the rail walks back to this hall. One observer rather
   than a call at each of those sites, because the site that gets forgotten is the one
   that brings the defect back. The notice's width does not depend on the band, so
   writing the token cannot feed back in here. */
if(typeof ResizeObserver!=="undefined"){
  try{
    const watched=$("#worldUnsaved");
    if(watched) new ResizeObserver(()=>sizeUnsavedBand()).observe(watched);
  }catch(_){}
}
/* Every sign the form carries, painted from one comparison. Also the one entry point:
   the Directories lines are drawn from here too, because Open's tooltip depends on
   whether the box under it has been touched. */
function renderWorldDirty(){
  const now=worldFormRead(), dirty=[];
  for(const key in now){
    const changed=!!WORLD_SNAP&&now[key]!==WORLD_SNAP[key];
    if(changed) dirty.push(key);
    const el=$("#"+worldFormMarkerFor(key));
    const field=el&&el.closest?el.closest(".field"):null;
    if(field) field.classList.toggle("field-dirty",changed);
  }
  const any=dirty.length>0;
  /* A class rather than a display, because this is only half of the answer: the notice
     lives outside the halls, so whether it is on screen is this AND which hall is
     showing, and that second half is the stylesheet's to know. */
  const notice=$("#worldUnsaved"); if(notice) notice.classList.toggle("on",any);
  /* The band the notice stands in. The hall gives up the foot of its scrolling area for
     as long as the notice is up, so the form ends above it instead of sliding behind it
     at every scroll position but the last one. Measured right here rather than left to
     the observer below, so the very first frame the notice appears in already has a band
     deep enough to hold it. */
  const hall=$("#page-world"); if(hall) hall.classList.toggle("unsaved-room",any);
  sizeUnsavedBand();
  const save=$("#saveCfgBtn"); if(save) save.classList.toggle("pulse",any);
  const rail=document.querySelector('.navitem[data-page="world"]');
  if(rail) rail.classList.toggle("has-unsaved",any);
  renderWorldDirs();
  return dirty;
}
/* Every way a value in this hall can change reaches the same recount. The toggles need
   their own line: the shared [data-t] handler flips a class and fires no input event,
   and it is registered on the switch itself, so by the time the click reaches the hall
   the class is already the new one. */
$("#page-world")?.addEventListener("input",()=>{renderWorldDirty();});
$("#page-world")?.addEventListener("change",()=>{renderWorldDirty();});
$("#page-world")?.addEventListener("click",e=>{
  const t=e.target;
  if(t&&typeof t.closest==="function"&&t.closest("[data-t]")) renderWorldDirty();
});
/* A path with one matched pair of double quotes taken off the outside of it.
   Explorer's own Copy as path wraps what it puts on the clipboard in quotes, which makes
   a quoted path the commonest way one arrives in either of these boxes, and it arrived
   unusable: the closing quote was part of the value, so the name no longer ended in
   valheim_server.exe and the line under the box said the file was the wrong one. The
   file name was right all along.
   The same rule as Tools/PathCheck.cs Expand, said again here because the two boxes are
   the other half of it: the native side can only fix what it is asked about, and what is
   IN the box is what Save Config writes to the profile. Whitespace either side comes off
   with the quotes, the way Expand takes it off, so a path copied with a trailing newline
   behind it lands as the path. A quote anywhere else is left where it is, and is one of
   the marks Windows will not take.
   The quote is named rather than written twice as a literal: two of those on one line
   puts a double quote on either side of a minus, and the copy gate reads the stretch
   between them as a sentence with a dash standing in it. */
const PATH_QUOTE='"';
function unquotePath(v){
  let s=String(v??"").trim();
  const last=s.length-1;
  if(last>=1&&s[0]===PATH_QUOTE&&s[last]===PATH_QUOTE) s=s.slice(1,last).trim();
  return s;
}
/* The two boxes, their pickers and their Open buttons. */
for(const d of WORLD_DIRS){
  $("#"+d.input)?.addEventListener("input",()=>{schedulePathCheck(d.kind);});
  /* A paste is the one way a quoted path gets in, so it is the one place the quotes come
     off. Not on every input event: a host typing a quote deliberately would have it
     vanish under the caret the moment they typed the second one, and a box that eats
     what is typed into it is worse than a box that takes a path with quotes on it.
     Only a paste that is actually wrapped is intercepted. Anything else falls through to
     the browser's own handling, which is what puts it at the caret, keeps the undo stack
     and fires the input event that the rest of this hall listens for. */
  $("#"+d.input)?.addEventListener("paste",e=>{
    const box=e.currentTarget;
    const pasted=(e.clipboardData||window.clipboardData)?.getData("text")||"";
    const clean=unquotePath(pasted);
    if(!clean||clean===pasted) return;
    e.preventDefault();
    const from=box.selectionStart??box.value.length, to=box.selectionEnd??from;
    box.value=box.value.slice(0,from)+clean+box.value.slice(to);
    const caret=from+clean.length;
    try{box.setSelectionRange(caret,caret);}catch(_){}
    /* Said out loud rather than left to the browser, because preventDefault took the
       browser's own event with it, and the marker, the notice and the check under the
       box all hang off it. */
    box.dispatchEvent(new Event("input",{bubbles:true}));
  });
  /* The preview's two sentences are spelled out here rather than kept in the table
     above, so each one still sits a line below the rune that leads it: the gate that
     holds a mark against its call site reads the glyph printed nearest the lookup, and
     an id reached through a table has no call site for it to read. */
  $("#"+d.open)?.addEventListener("click",()=>{
    if(Native.available){rpc("shell.open",{target:d.target});return;}
    if(d.kind==="exe") toast("ᛃ "+T("world.server_exe.open.preview.toast"));
    else toast("ᛃ "+T("world.save_dir.open.preview.toast"));
  });
  /* Browse fills the box and marks it changed. It never saves: what it produces is a
     path the host still has to keep, like anything else typed on this hall. */
  $("#"+d.browse)?.addEventListener("click",async()=>{
    if(!Native.available){toast("ᛃ "+T("world.dir.browse.preview.toast"));return;}
    const r=await rpc(d.pick,{kind:d.kind});
    if(r===FAIL||!r||r.cancelled||!r.path) return;
    const box=$("#"+d.input); if(!box) return;
    /* Through the same rule a paste goes through. A Windows picker hands back a bare
       path, so this takes nothing off today; it is here so there is ONE way a path gets
       into this box from outside, rather than one that is cleaned and one that is not. */
    box.value=unquotePath(r.path);
    renderWorldDirty();
    schedulePathCheck(d.kind);
    try{scheduleEditBar();}catch(_){}
  });
}
/* ---------- THE WORLD FIELD ----------
   The list of worlds this realm can be pointed at, and the one entry at the end of it
   that is not a world: New world, which opens a box to type a name in.

   The value of that entry is a string no world can ever be called, because it holds two
   characters Windows will not put in a file name. An empty value would not do: "" is
   what the field carries before any world exists, so a sentinel that meant "type one"
   and "there are none" at the same time could not tell the two apart. */
const WORLD_NEW_VALUE="*new*";
/* The names the field is showing right now, so a typed name can be held against them
   before anything is sent. The native side answers the same question again on the way
   in; this is the answer said out loud, early, while the host is still typing. */
let WORLD_NAMES=[];
/* Two worlds, so the World field and the Barrow can be walked in a browser with no app
   behind them. Nothing here is ever written anywhere. */
const WORLD_LIST_MOCK=["Midgard","Trialgrounds"];
/* Everything Windows refuses in a file name, which is everything the game refuses in a
   world name: the two separators, the drive colon, both wildcards, the redirections, the
   pipe and the quote. A string and a loop rather than a character class, because a
   backslash beside a slash inside one reads as the end of the pattern to more than one
   tool that walks this file, and what follows it then reads as code. */
const WORLD_NAME_BAD="\\/:*?\"<>|";
/* True when a name carries one of those, or a character below space. */
function worldNameHasBadCharacter(v){
  for(let i=0;i<v.length;i++){
    if(WORLD_NAME_BAD.indexOf(v[i])>=0||v.charCodeAt(i)<32) return true;
  }
  return false;
}
/* The names that reach a device rather than a file, whatever folder they are written in. */
const WORLD_NAME_DEVICES=new Set(["CON","PRN","AUX","NUL",
  "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
  "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"]);
/* The same length the native side holds a new name to (WorldStore.WorldNameMaxLength).
   Written down twice on purpose, because the two are read in two different languages and
   a shared constant would have to travel over the wire before the first keystroke. A
   test holds the two numbers together. */
const WORLD_NAME_MAX=64;
/* What is wrong with a world name, worded, or "" when nothing is. One reader for every
   place a host types one: the New world box, the realm forge, and Copy world as. The
   rules are the native side's own (WorldStore.IsSafeReferenceToken plus the length),
   said here so a host is turned away while typing rather than after pressing. */
function worldNameProblem(name,taken){
  const v=String(name==null?"":name).trim();
  if(!v) return T("world.name.problem.required");
  if(v.length>WORLD_NAME_MAX) return T("world.name.problem.too_long",{limit:WORLD_NAME_MAX});
  if(worldNameHasBadCharacter(v)||v.includes("..")||v===".") return T("world.name.problem.bad_characters");
  const last=v[v.length-1];
  if(last==="."||last===" ") return T("world.name.problem.bad_characters");
  const dot=v.indexOf(".");
  if(WORLD_NAME_DEVICES.has((dot>=0?v.slice(0,dot):v).toUpperCase()))
    return T("world.name.problem.bad_characters");
  if((taken||[]).some(n=>String(n).toLowerCase()===v.toLowerCase()))
    return T("world.name.problem.taken",{world:v});
  return "";
}
/* The world this hall is pointed at right now: the entry chosen in the list, or the name
   typed in the New world box when New world is what is chosen. A half typed new name
   answers as itself and never as the world the realm is still saved on, or the seed line
   and the difficulty dials would be describing a world nobody is looking at. */
function worldFieldValue(){
  const sel=$("#fWorld");
  if(!sel) return S.prefs?.WorldName||"";
  if(sel.value===WORLD_NEW_VALUE){const box=$("#fWorldNew");return box?box.value.trim():"";}
  return sel.value||S.prefs?.WorldName||"";
}
/* What is wrong with the name in the New world box, or "" when the box is shut, empty on
   a hall that has no world of its own yet, or holding a name that is fine. Save Config
   asks this before it writes anything.

   The empty box is only a problem when answering it would CHANGE what the hall is pointed
   at. A realm forged with the world box left blank has "" saved and nothing in its world
   list, so New world is what the field picks by itself and the box opens empty: refusing
   there would hold the port, the password, the RCON fields and the backup dials hostage to
   a name nothing has asked for yet, on the one screen that can set them. A hall that HAS a
   world saved is the other question, because an empty box there would quietly wipe it, so
   that one is still asked. */
function worldNewProblem(){
  const sel=$("#fWorld");
  if(!sel||sel.value!==WORLD_NEW_VALUE) return "";
  const typed=String($("#fWorldNew")?.value??"").trim();
  if(!typed&&!(S.prefs?.WorldName||"")) return "";
  return worldNameProblem(typed,WORLD_NAMES);
}
/* Opens or shuts the New world box to match what is chosen, says what is wrong with what
   is in it, and takes Copy world as away while there is no world chosen to copy.
   Called from the list, from every keystroke in the box, and from the language switch. */
function syncWorldNew(){
  const sel=$("#fWorld"), box=$("#fWorldNew"), note=$("#fWorldNote");
  const typing=!!sel&&sel.value===WORLD_NEW_VALUE;
  if(box) box.style.display=typing?"":"none";
  if(note){
    if(!typing){note.style.display="none";note.textContent="";note.style.color="";}
    else{
      const typed=!!(box&&box.value.trim());
      const problem=typed?worldNewProblem():"";
      note.textContent=problem||T("world.new.note");
      note.style.color=problem?"var(--blood)":"";
      note.style.display="";
    }
  }
  /* Copy as has nothing to act on while a new name is being typed, and the
     chip goes rather than greys: its tooltip is the walker's, filled from the
     markup, so a greyed one would be offering the wrong sentence on hover. */
  const chip=$("#copyWorldAs");
  if(chip) chip.style.display=(!typing&&!!(sel&&sel.value))?"":"none";
}
async function renderWorldSelect(){
  const cur=S.prefs?.WorldName??"";
  const r=Native.available?await rpc("worlds.list"):WORLD_LIST_MOCK;
  const names=[...new Set([...(Array.isArray(r)&&r!==FAIL?r:[]),...(cur?[cur]:[])])];
  WORLD_NAMES=names;
  /* The union is what puts a typed name back on screen as the chosen one after a reload:
     it is saved in the profile but not on disk until the server first starts, so the
     world list alone would answer without it and the field would jump to another world. */
  $("#fWorld").innerHTML=names.map(n=>`<option${n===cur?" selected":""}>${esc(n)}</option>`).join("")
    +`<option value="${WORLD_NEW_VALUE}">${esc(T("world.new.option"))}</option>`;
  syncWorldNew();
  renderWorldMods();
  renderWorldSeed();
}
/* The two words this field paints itself rather than reading out of the markup: the New
   world entry at the end of the list and the line under the box. Repainted rather than
   rendered again, so a half typed name and the host's chosen entry both survive a
   language switch, and so a switch costs no trip to the world list. */
function repaintWorldSelectCopy(){
  const entry=$("#fWorld")?.querySelector('option[value="'+WORLD_NEW_VALUE+'"]');
  if(entry) entry.textContent=T("world.new.option");
  syncWorldNew();
}
/* World-generation dials. value "" = Normal = game default (no -modifier arg emitted).
   Every non-empty value must match Game/WorldGen.cs exactly - the C# save validates.

   ONE table drives every world-difficulty surface: the order and wording of the five
   dropdowns, the live sentence under each dial, and the hover panel that lays all of
   the options out at once. The settings page and the realm-forge dialog both paint
   from here, so the two can never drift apart. "effects" is the key list the game
   itself sets for that option, kept as fine print so a host can match a BakaLoader
   world against the game's own world-modifier menu. */
const WORLDGEN_HELP={
  combat:{sel:"fModCombat",label:"Combat",labelId:"world.wg.combat.label",ariaId:"world.mod.combat.help.aria",
    introId:"world.wg.combat.intro",
    opts:[
      {v:"veryeasy",labelId:"world.wg.combat.veryeasy.label",
        explainId:"world.wg.combat.veryeasy.explain",
        effects:"playerdamage 125, enemydamage 50, enemyspeedsize 90"},
      {v:"easy",labelId:"world.wg.combat.easy.label",
        explainId:"world.wg.combat.easy.explain",
        effects:"playerdamage 110, enemydamage 75, enemyspeedsize 95"},
      {v:"",labelId:"world.wg.combat.normal.label",
        explainId:"world.wg.combat.normal.explain",
        effects:"no keys set"},
      {v:"hard",labelId:"world.wg.combat.hard.label",
        explainId:"world.wg.combat.hard.explain",
        effects:"playerdamage 85, enemydamage 150, enemyspeedsize 110, enemyleveluprate 120"},
      {v:"veryhard",labelId:"world.wg.combat.veryhard.label",
        explainId:"world.wg.combat.veryhard.explain",
        effects:"playerdamage 70, enemydamage 200, enemyspeedsize 120, enemyleveluprate 140"},
    ]},
  deathpenalty:{sel:"fModDeath",label:"Death penalty",labelId:"world.wg.deathpenalty.label",ariaId:"world.mod.death.help.aria",
    introId:"world.wg.deathpenalty.intro",
    opts:[
      {v:"casual",labelId:"world.wg.deathpenalty.casual.label",
        explainId:"world.wg.deathpenalty.casual.explain",
        effects:"deathkeepequip, skillreductionrate 15"},
      {v:"veryeasy",labelId:"world.wg.deathpenalty.veryeasy.label",
        explainId:"world.wg.deathpenalty.veryeasy.explain",
        effects:"skillreductionrate 15"},
      {v:"easy",labelId:"world.wg.deathpenalty.easy.label",
        explainId:"world.wg.deathpenalty.easy.explain",
        effects:"skillreductionrate 50"},
      {v:"",labelId:"world.wg.deathpenalty.normal.label",
        explainId:"world.wg.deathpenalty.normal.explain",
        effects:"no keys set"},
      {v:"hard",labelId:"world.wg.deathpenalty.hard.label",
        explainId:"world.wg.deathpenalty.hard.explain",
        effects:"deathdeleteunequipped, skillreductionrate 150"},
      {v:"hardcore",labelId:"world.wg.deathpenalty.hardcore.label",
        explainId:"world.wg.deathpenalty.hardcore.explain",
        effects:"deathdeleteitems, deathskillsreset"},
    ]},
  resources:{sel:"fModResources",label:"Resources",labelId:"world.wg.resources.label",ariaId:"world.mod.resources.help.aria",
    introId:"world.wg.resources.intro",
    opts:[
      {v:"muchless",labelId:"world.wg.resources.muchless.label",
        explainId:"world.wg.resources.muchless.explain",
        effects:"resourcerate 50"},
      {v:"less",labelId:"world.wg.resources.less.label",
        explainId:"world.wg.resources.less.explain",
        effects:"resourcerate 75"},
      {v:"",labelId:"world.wg.resources.normal.label",
        explainId:"world.wg.resources.normal.explain",
        effects:"no keys set"},
      {v:"more",labelId:"world.wg.resources.more.label",
        explainId:"world.wg.resources.more.explain",
        effects:"resourcerate 150"},
      {v:"muchmore",labelId:"world.wg.resources.muchmore.label",
        explainId:"world.wg.resources.muchmore.explain",
        effects:"resourcerate 200"},
      {v:"most",labelId:"world.wg.resources.most.label",
        explainId:"world.wg.resources.most.explain",
        effects:"resourcerate 300"},
    ]},
  raids:{sel:"fModRaids",label:"Raids",labelId:"world.wg.raids.label",ariaId:"world.mod.raids.help.aria",
    introId:"world.wg.raids.intro",
    opts:[
      {v:"none",labelId:"world.wg.raids.none.label",
        explainId:"world.wg.raids.none.explain",
        effects:"eventrate 0"},
      {v:"muchless",labelId:"world.wg.raids.muchless.label",
        explainId:"world.wg.raids.muchless.explain",
        effects:"eventrate 200"},
      {v:"less",labelId:"world.wg.raids.less.label",
        explainId:"world.wg.raids.less.explain",
        effects:"eventrate 150"},
      {v:"",labelId:"world.wg.raids.normal.label",
        explainId:"world.wg.raids.normal.explain",
        effects:"no keys set"},
      {v:"more",labelId:"world.wg.raids.more.label",
        explainId:"world.wg.raids.more.explain",
        effects:"eventrate 60"},
      {v:"muchmore",labelId:"world.wg.raids.muchmore.label",
        explainId:"world.wg.raids.muchmore.explain",
        effects:"eventrate 30"},
    ]},
  portals:{sel:"fModPortals",label:"Portals",labelId:"world.wg.portals.label",ariaId:"world.mod.portals.help.aria",
    introId:"world.wg.portals.intro",
    opts:[
      {v:"casual",labelId:"world.wg.portals.casual.label",
        explainId:"world.wg.portals.casual.explain",
        effects:"teleportall"},
      {v:"",labelId:"world.wg.portals.normal.label",
        explainId:"world.wg.portals.normal.explain",
        effects:"no keys set"},
      {v:"hard",labelId:"world.wg.portals.hard.label",
        explainId:"world.wg.portals.hard.explain",
        effects:"nobossportals"},
      {v:"veryhard",labelId:"world.wg.portals.veryhard.label",
        explainId:"world.wg.portals.veryhard.explain",
        effects:"noportals"},
    ]},
};
/* The shape the dial code already speaks (id, label, [value, label id] tuples), derived
   from the one table above so there is no second list to keep in step by hand. The
   option half carries the catalog id rather than the English now: the only reader is
   wgOptions, which paints it. ariaId is the sentence the dial's ? button speaks, and it
   is the SAME entry the settings page names in index.html, so the forge dialog and the
   hall cannot word that one two ways. The dial half still keeps its English because one
   composed sentence reads it: the first-run wizard's summary line. */
const WORLDGEN=Object.fromEntries(Object.entries(WORLDGEN_HELP).map(([key,h])=>
  [key,{sel:h.sel,label:h.label,labelId:h.labelId,ariaId:h.ariaId,opts:h.opts.map(o=>[o.v,o.labelId])}]));
/* What BakaLoader promises about these dials is world.wg.own_note in the
   catalog, written once and shown on both surfaces. */
const wgOptions=(key,val)=>WORLDGEN[key].opts.map(([v,id])=>
  `<option value="${v}"${v===val?" selected":""}>${esc(T(id))}</option>`).join("");
/* The table row for one dial's selected option ("" == Normal). */
const wgOpt=(key,val)=>(WORLDGEN_HELP[key]?.opts||[]).find(o=>o.v===(val||""));
/* The single line under a dial: what it is set to right now, in plain words. */
const worldModExplain=(key,val)=>{const o=wgOpt(key,val);return o?T(o.explainId):"";};
/* The hover panel for one dial: what the category does, then every option it offers
   with its sentence and the keys the game sets for it. */
function worldModPanelHtml(key,val){
  const h=WORLDGEN_HELP[key]; if(!h) return "";
  const cur=val||"";
  return `<div class="wgt-head">${esc(T(h.labelId))}</div>`+
    `<div class="wgt-intro">${esc(T(h.introId))}</div>`+
    h.opts.map(o=>`<div class="wgt-row${o.v===cur?" sel":""}">`+
      `<div class="wgt-lbl">${esc(T(o.labelId))}</div>`+
      `<div class="wgt-ex">${esc(T(o.explainId))}</div>`+
      `<div class="wgt-eff">${esc(o.effects)}</div></div>`).join("")+
    `<div class="wgt-foot">${esc(T("world.wg.own_note"))}</div>`;
}
/* One shared hover panel serves every dial on both surfaces. It is fixed to the window
   and sits outside the form, so neither a scrolling pane nor the realm-forge dialog's
   own clipping can cut it off, and there is no per-dial copy to keep in step. */
const wgTip=$("#wgTip");
/* Which dial each marker speaks for, so the dial itself can point a screen reader at the
   panel for as long as it is showing. */
const wgTipSel=new WeakMap();
let wgTipOwner=null, wgTipHold=0;
/* The marker whose panel went up last, when it went up, and whether the host closed it
   themselves. Together they tell a real Escape from one aimed at a panel that had already
   gone away, which must not fall through and take the dialog behind it. */
let wgTipLastOwner=null, wgTipOpenedAt=0, wgTipDismissed=true;
/* Giving a marker below the fold focus makes the browser scroll it into view, and that
   scroll lands a frame or two AFTER the panel opened. Scrolls this soon after an open are
   the panel arriving, never the host scrolling away from it. */
const WGTIP_SETTLE_MS=250;
/* Point the marker's dial at the panel while it is showing, and let go of it after. Only
   ever takes back its own name, so a description set for another reason is left alone. */
function wgTipDescribe(marker,on){
  const sel=marker&&wgTipSel.get(marker);
  if(!sel) return;
  if(on) sel.setAttribute("aria-describedby","wgTip");
  else if(sel.getAttribute("aria-describedby")==="wgTip") sel.removeAttribute("aria-describedby");
}
function wgTipClose(){
  clearTimeout(wgTipHold);
  if(!wgTip||!wgTipOwner) return;
  wgTipDescribe(wgTipOwner,false);
  wgTipOwner=null;
  wgTip.classList.remove("open");
  wgTip.setAttribute("aria-hidden","true");
}
/* Leaving the marker gives the pointer a moment to reach the panel, so a host can
   move onto it and read (or scroll) a long list instead of losing it on the way. */
function wgTipCloseSoon(){clearTimeout(wgTipHold);wgTipHold=setTimeout(wgTipClose,140);}
function wgTipKeep(){clearTimeout(wgTipHold);}
/* Is the marker that owns the panel still somewhere the host can see it? The window is not
   the whole test: the dials sit in panes that clip their own overflow (the settings page's
   scroller, the realm dialog's body), so a row scrolled past the top of its pane is gone
   from view while its coordinates are still inside the window. */
function wgTipOwnerOnScreen(){
  const el=wgTipOwner;
  if(!el||!el.isConnected) return false;
  const r=el.getBoundingClientRect();
  if(!r.width&&!r.height) return false;
  let top=0, left=0, right=innerWidth, bottom=innerHeight;
  for(let p=el.parentElement;p;p=p.parentElement){
    const st=getComputedStyle(p);
    if(st.overflowX!=="visible"||st.overflowY!=="visible"){
      const b=p.getBoundingClientRect();
      top=Math.max(top,b.top); left=Math.max(left,b.left);
      right=Math.min(right,b.right); bottom=Math.min(bottom,b.bottom);
    }
  }
  return r.bottom>top+2&&r.top<bottom-2&&r.right>left+2&&r.left<right-2;
}
function wgTipOpen(marker,key,val){
  if(!wgTip||!marker) return;
  wgTipKeep();
  wgTip.innerHTML=worldModPanelHtml(key,val);
  // moving straight from one marker to the next never closes the panel, so the dial it
  // was describing has to be let go of here or it keeps a description of the wrong thing
  if(wgTipOwner&&wgTipOwner!==marker) wgTipDescribe(wgTipOwner,false);
  wgTipOwner=marker;
  wgTipLastOwner=marker;
  wgTipOpenedAt=performance.now();
  wgTipDismissed=false;
  wgTipDescribe(marker,true);
  wgTip.setAttribute("aria-hidden","false");
  wgTip.classList.add("open");
  wgTipPlace(marker);
}
/* Where the panel stands for a given marker. Split out from opening it so a scroll that
   moves the marker can put the panel back beside it instead of throwing it away. */
function wgTipPlace(marker){
  if(!wgTip||!marker) return;
  // measure at the origin, then place
  wgTip.style.left="0px"; wgTip.style.top="0px";
  const m=marker.getBoundingClientRect(), p=wgTip.getBoundingClientRect();
  const gap=8, edge=8, fitsX=x=>x>=edge&&x+p.width<=innerWidth-edge;
  let left=null, top=null;
  /* Inside a dialog the panel stands beside the WHOLE dialog rather than beside the
     marker, so the dials it is describing stay on screen while it is open. */
  const dlg=marker.closest(".modal");
  if(dlg){
    const d=dlg.getBoundingClientRect();
    const beside=fitsX(d.right+gap)?d.right+gap:(fitsX(d.left-gap-p.width)?d.left-gap-p.width:null);
    if(beside!==null){left=beside;top=Math.max(edge,Math.min(d.top,innerHeight-p.height-edge));}
  }
  /* Otherwise under the marker, over it, or beside it, in that order. The last case is
     the one that matters: a panel with nowhere to go would otherwise be clamped on top
     of the marker that opened it, which takes the pointer off the marker and closes the
     panel again the instant it appears. */
  if(left===null){
    left=Math.max(edge,Math.min(m.left-6,innerWidth-p.width-edge));
    if(m.bottom+gap+p.height<=innerHeight-edge) top=m.bottom+gap;
    else if(m.top-gap-p.height>=edge) top=m.top-gap-p.height;
    else{
      top=Math.max(edge,Math.min(m.top-24,innerHeight-p.height-edge));
      left=fitsX(m.right+gap)?m.right+gap:Math.max(edge,m.left-gap-p.width);
    }
  }
  wgTip.style.left=left+"px";
  wgTip.style.top=top+"px";
}
if(wgTip){
  wgTip.addEventListener("mouseenter",wgTipKeep);
  wgTip.addEventListener("mouseleave",wgTipClose);
}
/* Give one dial its live sentence and its hover panel. Both surfaces call this with
   their own elements, so the settings page and the realm forge behave identically. */
function wireWorldDialHelp(key,selEl,noteEl,markerEl){
  if(!selEl) return;
  const paint=()=>{if(noteEl) noteEl.textContent=worldModExplain(key,selEl.value);};
  paint();
  selEl.addEventListener("change",()=>{
    paint();
    if(wgTipOwner===markerEl) wgTipOpen(markerEl,key,selEl.value); // keep an open panel honest
  });
  if(!markerEl) return;
  wgTipSel.set(markerEl,selEl);
  const open=()=>wgTipOpen(markerEl,key,selEl.value);
  markerEl.addEventListener("mouseenter",open);
  markerEl.addEventListener("focus",open);      // same panel for a host on the keyboard
  markerEl.addEventListener("click",e=>{e.preventDefault();open();});
  markerEl.addEventListener("mouseleave",wgTipCloseSoon);
  markerEl.addEventListener("blur",()=>{
    wgTipCloseSoon();
    // the swallow below only guards the marker the host is actually standing on
    if(wgTipLastOwner===markerEl&&wgTipOwner!==markerEl) wgTipLastOwner=null;
  });
  /* While a marker holds focus, Escape belongs to its panel. With the panel up the
     document handler below has already taken the key and this never runs; this is for the
     case where the panel went away on its own (the pointer left it, a pane scrolled) and
     the host presses Escape at a panel that is no longer there. Letting that reach the
     realm dialog would shut the dialog and lose the name and seed already typed into it.
     Closing it with Escape is a different thing: that key was spent on purpose, so the
     next one is the dialog's. */
  markerEl.addEventListener("keydown",e=>{
    if(e.key!=="Escape") return;
    if(wgTipOwner===markerEl){e.stopPropagation();wgTipClose();wgTipDismissed=true;return;}
    if(wgTipLastOwner===markerEl&&!wgTipDismissed){e.stopPropagation();wgTipDismissed=true;}
  });
}
/* Escape closes the panel and goes no further, so reading the options inside the
   realm-forge dialog cannot cost the host the dialog as well. */
document.addEventListener("keydown",e=>{
  if(e.key!=="Escape"||!wgTipOwner) return;
  e.stopPropagation();
  wgTipClose();
  wgTipDismissed=true;
},true);
/* A panel fixed to the window would otherwise hang beside a marker that has scrolled
   away. Four cases, in this order. The panel's own scrolling is not a page scroll, so
   reading a long list is left alone. A scroll arriving right after an open is the browser
   bringing a freshly focused marker into view, which is how a host on the keyboard reaches
   a dial below the fold: the panel is put back beside it, never dropped. After that the
   panel follows its marker for as long as the marker is on screen. Only a scroll that has
   carried the marker out of view takes the panel with it. */
document.addEventListener("scroll",e=>{
  if(!wgTipOwner) return;
  const t=e.target;
  if(wgTip&&t&&t.nodeType===1&&(t===wgTip||wgTip.contains(t))) return;
  if(performance.now()-wgTipOpenedAt<WGTIP_SETTLE_MS||wgTipOwnerOnScreen()){wgTipPlace(wgTipOwner);return;}
  wgTipClose();
},true);
window.addEventListener("blur",wgTipClose);
/* Paint the five dials from a modifiers map (missing key == Normal == ""), and with
   them the live sentence under each one. */
function applyWorldModDials(mods){
  mods=mods||{};
  for(const [key,def] of Object.entries(WORLDGEN)){
    $("#"+def.sel).innerHTML=wgOptions(key,mods[key]||"");
    const note=$("#"+def.sel+"Note");
    if(note) note.textContent=worldModExplain(key,mods[key]||"");
  }
}
/* Read the five dials back into a modifiers map, dropping Normal (""). */
function scrapeWorldModDials(){
  const mods={};
  for(const [key,def] of Object.entries(WORLDGEN)){const v=$("#"+def.sel).value;if(v)mods[key]=v;}
  return mods;
}
let _worldModsSeq=0;
/* Render the difficulty dials for the selected world. The host's pick lives in S.worldMods,
   NOT in the DOM, so a re-render (an event, a Save, a helm turn) never discards an unsaved
   edit: while S.worldMods already holds THIS world, its values are re-painted as-is. Only a
   genuine change of world reloads the stored values from disk. */
async function renderWorldMods(){
  const world=worldFieldValue();
  // Same world we already hold: keep the host's (possibly unsaved) dials, no reload, no clobber.
  if(S.worldMods&&S.worldMods.world===world){ applyWorldModDials(S.worldMods.mods); return; }
  // A different world (or first load): pull its stored values fresh, guarded so a slow
  // lookup for an older world can never overwrite a newer one.
  const seq=++_worldModsSeq;
  let cur={};
  if(Native.available&&world){
    const r=await rpc("worldgen.get",{world});
    if(seq!==_worldModsSeq) return; // a newer world selection superseded this lookup
    if(r!==FAIL&&r?.modifiers) cur=r.modifiers;
  }
  S.worldMods={world,mods:{...cur}};
  applyWorldModDials(cur);
  /* The dials now show what is stored for THIS world, so that is their clean state. Only
     these five are adopted: a host who typed a server name and then changed world still
     has the name marked, which is the whole point of adopting rather than re-snapshotting. */
  try{worldFormAdopt(...Object.values(WORLDGEN).map(def=>def.sel));}catch(_){}
}
/* World seed: read-only identity from the world's .fwl. A seed is fixed at world
   creation and can NEVER change (the field is locked); a world with no .fwl yet
   gets its seed on first launch - random, or the one chosen in the realm wizard. */
let _seedSeq=0;
async function renderWorldSeed(){
  const el=$("#fSeed"); if(!el) return;
  const world=worldFieldValue();
  const seq=++_seedSeq;
  if(!Native.available){el.value="yBvEFPKD9S · 649688311";el.dataset.copy="yBvEFPKD9S";return;}
  if(!world){el.value="";el.dataset.copy="";return;}
  const r=await rpc("world.seed",{world});
  if(seq!==_seedSeq) return; // a newer lookup superseded this one
  if(r===FAIL||!r){el.value="";el.dataset.copy="";return;}
  if(r.exists){el.value=r.seedName+" · "+r.seed;el.dataset.copy=r.seedName;}
  else{el.value=T("world.seed.not_created");el.dataset.copy="";}
}
$("#copySeed").addEventListener("click",()=>{
  const v=$("#fSeed")?.dataset.copy||"";
  if(!v){toast(T("world.seed.none.toast"));return;}
  navigator.clipboard?.writeText(v).catch(()=>{});
  toast("ᛟ "+T("world.seed.copied.toast",{seed:v}));
});
$("#fWorld").addEventListener("change",()=>{
  syncWorldNew();
  if($("#fWorld").value===WORLD_NEW_VALUE) setTimeout(()=>$("#fWorldNew")?.focus(),0);
  renderWorldMods();renderWorldSeed();
});
/* A keystroke in the New world box is a change of world, so everything that follows the
   field follows it too. The seed line and the dials are held back a moment: both ask the
   native side a question, and a question per keystroke is a question per keystroke. */
let _worldNewT=null;
function scheduleWorldNew(){
  syncWorldNew();
  clearTimeout(_worldNewT);
  _worldNewT=setTimeout(()=>{
    try{renderWorldMods();}catch(_){}
    try{renderWorldSeed()?.catch(()=>{});}catch(_){}
  },260);
  try{scheduleEditBar();}catch(_){}
}
$("#fWorldNew")?.addEventListener("input",scheduleWorldNew);
/* Copy world as, from the hall the world is chosen on. No folder and no subfolder travel
   with it: the native side answers for the save folder this realm is reading, which is
   the one the list on screen came from. */
$("#copyWorldAs")?.addEventListener("click",()=>{
  const sel=$("#fWorld");
  const world=sel&&sel.value!==WORLD_NEW_VALUE?sel.value:"";
  if(!world){toast("ᚦ "+T("world.copy.block.no_world"));return;}
  const running=cfgServerIsUp()
    &&String(S.prefs?.WorldName||"").toLowerCase()===String(world).toLowerCase();
  /* The list is read again so the copy is in it, and the entry the host had chosen
     is put back on top: a field that jumped to the saved world after a copy would
     quietly undo a pick they had not saved yet. */
  const refresh=async()=>{
    const keep=$("#fWorld")?.value;
    await renderWorldSelect();
    const back=$("#fWorld");
    if(!back||!keep) return;
    if(![...back.options].some(o=>o.value===keep)) return;
    back.value=keep;
    syncWorldNew();renderWorldMods();renderWorldSeed();
  };
  worldCopyModal(
    {world,folder:"",sub:"",owner:S.profileName||S.prefs?.ProfileName||"",running,taken:WORLD_NAMES},
    target=>{
      /* No host behind the preview, so the mock list grows the copy the way the
         Barrow's mock list does. Without it the toast said a world had landed and
         the field it landed in could not show it. */
      if(!Native.available&&target&&!WORLD_LIST_MOCK.includes(target)) WORLD_LIST_MOCK.push(target);
      refresh().catch(()=>{});
    });
});
/* A dial the host turns updates the intended state for the selected world at once, so the
   value survives any later re-render and Save Config sends exactly what is on screen. */
for(const [key,def] of Object.entries(WORLDGEN)){
  $("#"+def.sel).addEventListener("change",()=>{
    const world=worldFieldValue();
    S.worldMods={world,mods:scrapeWorldModDials()};
  });
  /* The live sentence and the hover panel for this dial. The ids follow the select's
     own: fModCombat -> fModCombatNote for the line, iModCombat for the marker. */
  wireWorldDialHelp(key,$("#"+def.sel),$("#"+def.sel+"Note"),$("#i"+def.sel.slice(1)));
}
/* The dials' words, painted again once the catalog lands. Every one of them comes out of
   the catalog - the five dropdowns' option labels, the live sentence under each dial, and
   the note that sits under all five - and all of them are painted while app.js is still
   being evaluated, so without this the Settings hall stood there reading
   "world.wg.combat.normal.explain" until the host happened to turn a dial. Repainting
   from the intended state rather than calling renderWorldMods() again keeps it a wording
   pass: no second lookup for a world the page already holds, and an unsaved dial the host
   set before the words arrived survives it. */
function repaintWorldDialCopy(){
  applyWorldModDials(S.worldMods?S.worldMods.mods:{});
  const own=$("#wgOwnNote"); if(own) own.textContent=T("world.wg.own_note");
}
repaintWorldDialCopy();
renderWorldMods(); // seed the dials with Normal defaults (both modes)
renderWorldSeed();
/* Max players: server-wide, not per-world. 10 = vanilla cap (no plugin); above 10 the
   bundled BakaLoader max-players plugin's cfg is the source of truth. */
async function renderMaxPlayers(){
  if(!Native.available) return;
  const r=await rpc("maxplayers.get",{});
  /* Fetched, and therefore filled after the form was snapshotted. Adopted rather than
     re-snapshotted so nothing else in the form is quietly declared saved. Inside the arm
     that FILLED the box, never beside it: a call that failed leaves whatever was in the
     box, and adopting then would declare a number the reader may well have typed
     themselves to be the saved one. */
  if(r!==FAIL&&r?.count!=null){
    $("#fMaxPlayers").value=r.count;
    try{worldFormAdopt("fMaxPlayers");}catch(_){}
  }
}
renderMaxPlayers();
/* The clean state the hall starts in. Taken here, at the bottom of the World block, so
   every control it reads is already in the page. renderWorldForm takes it again the
   moment a profile is loaded, and the save takes it again once the write has landed. */
worldFormSnapshot();
/* True while a server is up (or on its way up) for the profile the halls are showing. The
   Settings note and the Save Config sentence both turn on it, and it is the same pair of
   states the native side calls "restart pending". */
function cfgServerIsUp(){
  const status=S.state&&S.state.status;
  return status==="Running"||status==="Starting";
}
/* The line above Save Config. A save goes to disk straight away, but the running server was
   handed its whole configuration on the command line when it launched and keeps it until it
   launches again, so the button says so before it is pressed rather than after. */
function renderCfgRunningNote(){
  const el=$("#cfgRunningNote"); if(!el) return;
  const up=cfgServerIsUp();
  el.style.display=up?"":"none";
  el.textContent=up?T("world.running.note"):"";
}
renderCfgRunningNote();
$("#saveCfgBtn").addEventListener("click",async()=>{
  /* A typed world name is held to the same rules the native side holds it to, before
     anything is written: a name that could not be a folder is a realm that cannot be
     launched, and the place to say so is here rather than at the next start. */
  const typedProblem=worldNewProblem();
  if(typedProblem){toast("ᚦ "+typedProblem);$("#fWorldNew")?.focus();return;}
  if(!Native.available){toast("ᛉ "+T("world.saved.preview.toast"));return;}
  const name=S.profileName||S.prefs?.ProfileName||"Default";
  // fetch fresh, mutate only form-controlled fields, send the WHOLE object back
  const cur=await rpc("profiles.get",{name});
  if(cur===FAIL) return;
  // int fields: parse, fall back to the freshly-fetched previous value on NaN
  const num=(id,prev)=>{const v=parseInt($("#"+id).value,10);return Number.isNaN(v)?prev:v;};
  const prefs={...cur,
    Name:$("#fName").value.trim(),
    WorldName:worldFieldValue(),
    Password:$("#fPassword").value,
    Port:num("fPort",cur.Port??2456),
    Public:swOn("tPublic"), Crossplay:swOn("tCrossplay"),
    SaveInterval:num("fSaveInterval",cur.SaveInterval??600),
    BackupCount:num("fBackups",cur.BackupCount??4),
    BackupIntervalShort:num("fBackShort",cur.BackupIntervalShort??600),
    BackupIntervalLong:num("fBackLong",cur.BackupIntervalLong??43200),
    WriteServerLogsToFile:swOn("tLogs"), AutoStart:swOn("tAutoStart"),
    AutoRestart:swOn("tCrash"), AutoRestartDelay:num("fCrashDelay",cur.AutoRestartDelay??10),
    EmptyServerRestart:swOn("tEmpty"), EmptyServerRestartDelayMinutes:num("fEmptyDelay",cur.EmptyServerRestartDelayMinutes??5),
    ScheduledRestart:swOn("tSched"), ScheduledRestartHours:num("fSchedHours",cur.ScheduledRestartHours??6),
    RconEnabled:swOn("tRcon"), RconPort:num("fRconPort",cur.RconPort??25575),
    RconPassword:$("#fRconPw").value,
    ServerExePath:$("#fServerExe").value.trim(),
    SaveDataFolderPath:$("#fSaveDir").value.trim(),
    AdditionalArgs:$("#fArgs").value,
  };
  /* The reply this was built from carries four keys that are answers rather than
     settings: the path in force for each of the two boxes and where it came from. They
     are dropped on the way back so what is sent is the profile and nothing else. The
     native side ignores a key it has no field for, so this is belt and braces; it is
     here because a payload that says things it does not mean is how one of them ends up
     being read one day. */
  delete prefs.EffectiveServerExePath; delete prefs.ServerExePathSource;
  delete prefs.EffectiveSaveDataFolderPath; delete prefs.SaveDataFolderPathSource;
  const r=await rpc("profiles.save",{name,prefs});
  if(r===FAIL) return;
  // World dials ride along with Save Config, keyed to the selected world. Only write them
  // when S.worldMods actually holds THIS world's dials: that way an unpopulated or
  // stale-world dial set can never wipe a stored difficulty, while an intentional all-Normal
  // choice (mods:{}) still clears it. The dials are scraped once more here so the on-screen
  // value is authoritative even if a change event was missed.
  if(prefs.WorldName&&S.worldMods&&S.worldMods.world===prefs.WorldName){
    const modifiers=scrapeWorldModDials();
    S.worldMods.mods=modifiers;
    await rpc("worldgen.save",{world:prefs.WorldName,modifiers});
  }else if(prefs.WorldName){
    /* The dials on screen belong to another world, or to no world yet. Writing them here
       would stamp one world's difficulty onto another, so the save is skipped, but it is
       skipped OUT LOUD: this used to pass in silence under a success toast, and the host
       walked away believing a difficulty they had just set was saved. */
    console.warn("[BakaLoader] world difficulty not saved: the dials on screen belong to "+
      (S.worldMods&&S.worldMods.world?"'"+S.worldMods.world+"'":"no world yet")+
      ", not '"+prefs.WorldName+"'");
    toast("ᚷ "+T("world.difficulty.not_saved.toast"));
  }
  // max players rides along too - >10 auto-installs the bundled max-players plugin
  const mp=parseInt($("#fMaxPlayers").value,10);
  if(!Number.isNaN(mp)){
    const mr=await rpc("maxplayers.save",{count:mp});
    if(mr!==FAIL&&mr?.count!=null){
      $("#fMaxPlayers").value=mr.count;
      if(mr.modInstalled&&mp>10)
        logLine("ok","[BakaLoader] max players set to "+mr.count+" (bundled plugin · next start)");
    }
  }
  S.prefs=r; S.profileName=r.ProfileName; S.saveInterval=r.SaveInterval??600;
  renderAllFromPrefs();
  /* The write landed, so the form on screen IS what is saved. renderWorldForm above has
     already said so; this is said again here because "after a successful save" is the
     rule, and a later change to how the form is redrawn must not be able to lose it. */
  worldFormSnapshot();
  /* Whether the running server is now behind what is saved is the native side's answer, and
     nothing about a save moves the server's status, so ask for the state again rather than
     leaving the row to wait for the next start or stop. */
  const after=await rpc("server.state");
  if(after!==FAIL) applyState(after);
  /* A save while the world is up is real and on disk, and it is still not what the players are
     playing. Say that instead of a plain confirmation the host would read as "in force now". */
  if(cfgServerIsUp()) toast("ᛉ "+T("world.saved.running.toast"));
  else toast("ᛉ "+T("world.saved.toast",{profile:r.ProfileName}));
  logLine("ok","[BakaLoader] profile '"+r.ProfileName+"' saved");
});
function renderAllFromPrefs(){
  if(!S.prefs) return;
  $("#hearthSub").textContent=T("hearth.head.sub",{world:S.prefs.WorldName||"-"});
  if(S.prefs.Name) $("#tbSrv").textContent=S.prefs.Name.toUpperCase();
  $("#sbWorld").textContent=S.prefs.WorldName||"-";
  $("#sbRcon").textContent=S.prefs.RconEnabled?String(S.prefs.RconPort??25575):T("status.rcon.off");
  $("#sbRconSeg").classList.toggle("dim",!S.prefs.RconEnabled);
  renderWorldForm();
  renderNet();
  renderHearthNative();
  try{renderCfgRunningNote();}catch(_){}
  try{renderEditBar();}catch(_){}
}
/* The status bar's RCON word in the browser preview, which has no prefs behind it.
   renderAllFromPrefs owns that element in the app and never runs here, so the word
   would otherwise be the one left in the page: English whatever the language is, and
   the one segment of the bar a switch never reaches. The label beside it is markup
   and the walker fills that, so the two halves have one owner each.
   Called from repaintBootCopy and from nowhere else, so the English in the page is
   what the first frame reads and the catalog takes over the moment it lands. */
function renderStatusRconPreview(){
  if(Native.available) return;
  const seg=$("#sbRcon"); if(seg) seg.textContent=T("status.rcon.bound");
}

/* ---------- EDIT BAR (World hall) ----------
   A persistent banner that makes it plain WHICH realm you're editing and shows
   live collision warnings (port/RCON/install/world) against every other realm -
   long before the throwing launch-time check would fire. Pull-based: only the
   World hall renders it, and each check is guarded by a sequence counter so a
   slow result from an older keystroke never overwrites a newer one. */
let _editBarSeq=0, _ebT=null;
function editBarValues(){
  const p=S.prefs||{};
  const gv=(id,fb)=>{const el=$("#"+id);return el&&el.value!=null&&el.value!==""?el.value:fb;};
  const worldSel=$("#fWorld");
  return {
    profile:S.profileName||p.ProfileName||"",
    name:String(gv("fName",p.Name)||"").trim(),
    world:worldSel?worldFieldValue():(p.WorldName||""),
    port:parseInt(gv("fPort",p.Port??2456),10)||0,
    rconEnabled:$("#tRcon")?swOn("tRcon"):!!p.RconEnabled,
    rconPort:parseInt(gv("fRconPort",p.RconPort??25575),10)||0,
    exePath:gv("fServerExe",p.ServerExePath??""),
    saveFolder:gv("fSaveDir",p.SaveDataFolderPath??"")
  };
}
async function renderEditBar(){
  const bar=$("#editBar"); if(!bar) return;
  // The bar belongs to the World hall - that's where the identity fields live.
  if(currentPage!=="world"||!S.prefs){bar.style.display="none";return;}
  const v=editBarValues();
  const rconTxt=v.rconEnabled?T("world.editbar.rcon",{port:v.rconPort||"?"}):T("world.editbar.rcon.off");
  bar.innerHTML=
    `<span class="ebedit">${esc(T("world.editbar.editing"))}</span>`+
    `<span class="ebname">${esc(v.name||v.profile||T("world.editbar.unnamed"))}</span>`+
    `<span class="ebmeta">${esc(T("world.editbar.world"))} <b>${esc(v.world||"-")}</b> · ${esc(T("world.editbar.port"))} <b>${v.port||"-"}</b> · <b>${esc(rconTxt)}</b></span>`+
    `<span class="ebwarn" id="ebWarn" style="display:none"></span>`;
  bar.style.display="flex";
  if(!Native.available) return;
  const seq=++_editBarSeq;
  const warnings=await rpc("servers.checkCollision",{
    profile:v.profile,port:v.port,rconEnabled:v.rconEnabled,rconPort:v.rconPort,
    world:v.world,exePath:v.exePath,saveFolder:v.saveFolder});
  if(seq!==_editBarSeq) return; // a newer keystroke already superseded this check
  const w=$("#ebWarn"); if(!w) return;
  if(Array.isArray(warnings)&&warnings.length){
    w.textContent="⚠ "+T("world.editbar.conflicts",{count:warnings.length});
    w.title=warnings.map(x=>x.message).join("  •  ");
    w.style.display="";
  }else{
    w.style.display="none";
  }
}
function scheduleEditBar(){clearTimeout(_ebT);_ebT=setTimeout(()=>{renderEditBar().catch(()=>{});},260);}
["fName","fPort","fRconPort","fServerExe","fSaveDir"].forEach(id=>{
  const el=$("#"+id); if(el) el.addEventListener("input",scheduleEditBar);
});
$("#fWorld")?.addEventListener("change",scheduleEditBar);
$("#fWorldNew")?.addEventListener("input",scheduleEditBar);
$("#tRcon")?.addEventListener("click",scheduleEditBar);

/* ---------- FIRST-LAUNCH SETUP WIZARD ---------- */
const WIZ={step:0,exe:"",save:"",exeValid:false,status:{},mods:{},worldSeed:"",seedWorld:"",seedEligible:false};

function wizardOpen(status){
  Object.assign(WIZ,{step:0,exe:"",save:"",exeValid:false,status:status||{},mods:{},worldSeed:"",seedWorld:"",seedEligible:false});
  // Seed is choosable ONLY for a world that doesn't exist yet (an existing
  // world's seed is immutable). Probe now; the answer lands well before step 3.
  const world=S.prefs?.WorldName||"";
  if(world){
    WIZ.seedWorld=world;
    if(!Native.available){WIZ.seedEligible=true;}
    else rpc("world.seed",{world}).then(r=>{
      WIZ.seedEligible=r!==FAIL&&r&&!r.exists;
    });
  }
  wizardRender();
}

async function wizValidate(kind,path){
  const v=(path||"").trim();
  if(!v) return kind==="dir"; /* blank save dir = keep the default; blank exe = invalid */
  if(!Native.available) return kind==="exe"?/valheim_server\.exe$/i.test(v):true;
  const r=await rpc("setup.validate",{kind,path:v});
  return r!==FAIL&&!!r?.valid;
}

async function wizFinish(skip){
  modalClose();
  if(!Native.available){toast("ᛉ "+(skip?T("setup.wiz.skipped.preview.toast"):T("setup.wiz.done.preview.toast")));return;}
  const r=await rpc("setup.complete",skip?{}:{serverExePath:WIZ.exe.trim(),saveDataFolderPath:WIZ.save.trim()});
  if(r===FAIL) return;
  // world rules picked in the wizard apply to the active profile's world
  const world=S.prefs?.WorldName;
  if(!skip&&world&&Object.values(WIZ.mods).some(v=>v))
    await rpc("worldgen.save",{world,modifiers:WIZ.mods});
  // chosen seed applies ONLY to a not-yet-created world (C# refuses otherwise)
  if(!skip&&WIZ.seedEligible&&WIZ.worldSeed.trim()&&WIZ.seedWorld){
    const sr=await rpc("world.setSeed",{world:WIZ.seedWorld,seedName:WIZ.worldSeed.trim()});
    if(sr!==FAIL&&sr?.seedName){
      logLine("ok","[BakaLoader] world '"+WIZ.seedWorld+"' will be born from seed '"+sr.seedName+"' ("+sr.seed+")");
      renderWorldSeed();
    }
  }
  toast("ᛉ "+(skip?T("setup.wiz.skipped.toast"):T("setup.wiz.done.toast")));
  logLine("ok","[BakaLoader] first-time setup "+(skip?"skipped":"completed"));
}

function wizardRender(){
  const names=[T("setup.wiz.step.welcome"),T("setup.wiz.step.server"),T("setup.wiz.step.saves"),
               T("setup.wiz.step.world"),T("setup.wiz.step.done")];
  const steps=`<div class="wiz-steps">`+names.map((n,i)=>
    `<span class="ws${i===WIZ.step?" on":i<WIZ.step?" done":""}"><b>${i+1}</b>${esc(n)}</span>`).join("")+`</div>`;
  const dflt=WIZ.status.defaultSavePath||"%USERPROFILE%\\AppData\\LocalLow\\IronGate\\Valheim";
  let body="",nav="";

  if(WIZ.step===0){
    body=
      `<div class="mtitle">${esc(T("setup.wiz.intro.title"))}</div>`+
      `<div class="wiz-help">${esc(T("setup.wiz.intro.body"))}</div>`+
      `<div class="wiz-paths">`+
      `<div><span class="r">ᛞ</span><strong>${esc(T("setup.wiz.intro.exe"))}</strong> · ${T("setup.wiz.intro.exe.body")}</div>`+
      `<div><span class="r">ᛃ</span><strong>${esc(T("setup.wiz.intro.save"))}</strong> · ${esc(T("setup.wiz.intro.save.body"))}</div>`+
      `</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wSkip">${esc(T("common.button.skip"))}</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wNext">${esc(T("common.button.begin"))}</button>`;
  }else if(WIZ.step===1){
    body=
      `<div class="mtitle">${esc(T("setup.wiz.exe.title"))}</div>`+
      `<div class="wiz-help">${T("setup.wiz.exe.body")}</div>`+
      `<div class="field"><label>${esc(T("setup.wiz.exe.label"))}</label><input type="text" id="wizExe" spellcheck="false" autocomplete="off" placeholder="D:\\SteamLibrary\\steamapps\\common\\Valheim dedicated server\\valheim_server.exe"></div>`+
      `<div class="wiz-stat dim" id="wizExeStat">${esc(T("setup.wiz.exe.stat.idle"))}</div>`+
      `<div id="wizFound"></div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wBack">${esc(T("common.button.back"))}</button>`+
        `<button class="btn btn-ghost btn-sm" id="wDetect">ᚱ ${esc(T("setup.wiz.detect.label"))}</button><span class="grow"></span>`+
        `<button class="btn btn-ghost btn-sm" id="wSkip">${esc(T("common.button.skip"))}</button>`+
        `<button class="btn btn-ember btn-sm" id="wNext" disabled>${esc(T("common.button.next"))}</button>`;
  }else if(WIZ.step===2){
    body=
      `<div class="mtitle">${esc(T("setup.wiz.save.title"))}</div>`+
      `<div class="wiz-help">${T("setup.wiz.save.body")}<br><code>${esc(dflt)}</code></div>`+
      `<div class="field"><label>${esc(T("setup.wiz.save.label"))}</label><input type="text" id="wizSave" spellcheck="false" autocomplete="off" placeholder="${esc(T("setup.wiz.save.placeholder"))}"></div>`+
      `<div class="wiz-stat ok" id="wizSaveStat">${esc(T("setup.wiz.save.stat.default"))}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wBack">${esc(T("common.button.back"))}</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wNext">${esc(T("common.button.next"))}</button>`;
  }else if(WIZ.step===3){
    body=
      `<div class="mtitle">${esc(T("setup.wiz.rules.title"))}</div>`+
      `<div class="wiz-help">${T("setup.wiz.rules.body")}</div>`+
      `<div class="formgrid">`+
      Object.entries(WORLDGEN).map(([key,def])=>
        `<div class="field"><label>${esc(T(def.labelId))}</label><select id="wizMod_${key}" data-wgkey="${key}">${wgOptions(key,WIZ.mods[key]||"")}</select></div>`).join("")+
      `</div>`+
      (WIZ.seedEligible?
        `<div class="field" style="margin-top:8px"><label>${esc(T("setup.wiz.seed.label"))}</label>`+
        `<input type="text" id="wizSeed" placeholder="${esc(T("setup.wiz.seed.placeholder"))}" spellcheck="false" autocomplete="off">`+
        `<div class="wiz-help">${T("setup.wiz.seed.note",{world:bold(WIZ.seedWorld)})}</div></div>`:"")+
      `<div class="wiz-help">${T("setup.wiz.maxplayers.note")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wBack">${esc(T("common.button.back"))}</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wNext">${esc(T("common.button.next"))}</button>`;
  }else{
    const exe=WIZ.exe.trim(),save=WIZ.save.trim();
    /* Each picked dial reads as the dial's own name and the choice's own name, both out
       of the catalog: the old line lower-cased the English label and printed the raw value
       behind it, which was neither translatable nor the wording the dial itself shows. */
    const modsPicked=Object.entries(WIZ.mods).filter(([,v])=>v)
      .map(([k,v])=>{const o=wgOpt(k,v);return o?T("setup.wiz.done.rule",{dial:T(WORLDGEN[k].labelId),choice:T(o.labelId)}):"";})
      .filter(Boolean).join(" · ");
    body=
      `<div class="mtitle">${esc(T("setup.wiz.done.title"))}</div>`+
      `<div class="wiz-sum">`+
      `<div class="row"><span class="k">${esc(T("setup.wiz.done.exe"))}</span><span class="v">${esc(exe||T("setup.wiz.done.exe.none"))}</span></div>`+
      `<div class="row"><span class="k">${esc(T("setup.wiz.done.save"))}</span><span class="v">${esc(save||T("setup.wiz.done.save.default"))}</span></div>`+
      `<div class="row"><span class="k">${esc(T("setup.wiz.done.rules"))}</span><span class="v">${esc(modsPicked||T("setup.wiz.done.rules.normal"))}</span></div>`+
      `</div>`+
      `<div class="wiz-help">`+T("setup.wiz.done.note")+`</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wBack">${esc(T("common.button.back"))}</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wNext">${esc(T("common.button.finish"))}</button>`;
  }

  const m=modalOpen(steps+body+`<div class="wiz-nav">${nav}</div>`,wizardRender);
  m.classList.add("wiz");

  const on=(sel,fn)=>{const el=m.querySelector(sel);if(el)el.addEventListener("click",fn);};
  on("#wBack",()=>{WIZ.step--;wizardRender();});
  on("#wSkip",()=>wizFinish(true));
  on("#wNext",async()=>{
    if(WIZ.step===4){wizFinish(false);return;}
    if(WIZ.step===2){
      const ok=await wizValidate("dir",WIZ.save);
      if(!ok){
        const s=m.querySelector("#wizSaveStat");
        s.className="wiz-stat bad";s.textContent="ᚦ "+T("setup.wiz.save.stat.missing");
        return;
      }
    }
    WIZ.step++;wizardRender();
  });

  if(WIZ.step===1){
    const inp=m.querySelector("#wizExe"),stat=m.querySelector("#wizExeStat"),next=m.querySelector("#wNext");
    inp.value=WIZ.exe;
    let t=null;
    const check=()=>{
      WIZ.exe=inp.value;
      clearTimeout(t);
      if(!inp.value.trim()){
        WIZ.exeValid=false;next.disabled=true;
        stat.className="wiz-stat dim";stat.textContent=T("setup.wiz.exe.stat.idle");
        return;
      }
      stat.className="wiz-stat dim";stat.textContent=T("common.stat.checking");
      t=setTimeout(async()=>{
        const ok=await wizValidate("exe",inp.value);
        WIZ.exeValid=ok;next.disabled=!ok;
        stat.className="wiz-stat "+(ok?"ok":"bad");
        stat.textContent=ok?"ᛉ "+T("setup.wiz.exe.stat.found")
                           :"ᚦ "+T("setup.wiz.exe.stat.missing");
      },250);
    };
    inp.addEventListener("input",check);
    if(WIZ.exe) check();
    setTimeout(()=>inp.focus(),30);
    on("#wDetect",async()=>{
      stat.className="wiz-stat dim";stat.textContent=T("setup.wiz.detect.searching");
      const r=Native.available?await rpc("setup.detect")
        :["C:\\Program Files (x86)\\Steam\\steamapps\\common\\Valheim dedicated server\\valheim_server.exe",
          "D:\\SteamLibrary\\steamapps\\common\\Valheim dedicated server\\valheim_server.exe"];
      const list=r!==FAIL&&Array.isArray(r)?r:[];
      const box=m.querySelector("#wizFound");
      if(!list.length){
        stat.className="wiz-stat bad";
        stat.textContent="ᚦ "+T("setup.wiz.detect.none");
        box.innerHTML="";return;
      }
      stat.className="wiz-stat ok";
      stat.textContent="ᛉ "+T("setup.wiz.detect.found",{count:list.length});
      box.innerHTML=`<div class="wiz-found">`+list.map(pth=>`<button class="wf" data-p="${esc(pth)}">${esc(pth)}</button>`).join("")+`</div>`;
      box.querySelectorAll(".wf").forEach(b=>b.addEventListener("click",()=>{inp.value=b.dataset.p;check();}));
    });
  }
  if(WIZ.step===2){
    const inp=m.querySelector("#wizSave"),stat=m.querySelector("#wizSaveStat");
    inp.value=WIZ.save;
    let t=null;
    inp.addEventListener("input",()=>{
      WIZ.save=inp.value;
      clearTimeout(t);
      if(!inp.value.trim()){stat.className="wiz-stat ok";stat.textContent=T("setup.wiz.save.stat.default");return;}
      stat.className="wiz-stat dim";stat.textContent=T("common.stat.checking");
      t=setTimeout(async()=>{
        const ok=await wizValidate("dir",inp.value);
        stat.className="wiz-stat "+(ok?"ok":"bad");
        stat.textContent=ok?"ᛉ "+T("setup.wiz.save.stat.found"):"ᚦ "+T("setup.wiz.save.stat.missing");
      },250);
    });
    setTimeout(()=>inp.focus(),30);
  }
  if(WIZ.step===3){
    m.querySelectorAll("[data-wgkey]").forEach(sel=>
      sel.addEventListener("change",()=>{WIZ.mods[sel.dataset.wgkey]=sel.value;}));
    const seedI=m.querySelector("#wizSeed");
    if(seedI){seedI.value=WIZ.worldSeed;seedI.addEventListener("input",()=>{WIZ.worldSeed=seedI.value;});}
  }
}

/* World hall: red "run first-time setup again" button (double confirm) */
$("#btnSetupReset").addEventListener("click",()=>{
  confirmModal(()=>T("setup.reset.confirm.title"),
    ()=>`<p>${esc(T("setup.reset.confirm.body"))}</p>`+
    `<p><strong>${esc(T("setup.reset.confirm.safe"))}</strong></p>`,
    ()=>T("common.button.continue"),()=>{
    /* confirmModal closes itself after onOk - defer the second confirm past that close */
    setTimeout(()=>{
      confirmModal(()=>T("setup.reset.again.title"),
        ()=>`<p>${esc(T("setup.reset.again.body"))}</p>`,
        ()=>T("setup.reset.ok"),async()=>{
        if(!Native.available){toast("ᛉ "+T("setup.reset.preview.toast"));setTimeout(()=>wizardOpen({}),60);return;}
        const st=await rpc("setup.reset");
        if(st===FAIL) return;
        toast("ᛉ "+T("setup.reset.done.toast"));
        logLine("warn","[BakaLoader] first-time setup was reset");
        const cur=await rpc("profiles.get",{name:S.profileName||S.prefs?.ProfileName||"Default"});
        if(cur!==FAIL&&cur){S.prefs=cur;renderAllFromPrefs();}
        setTimeout(()=>wizardOpen(st),60);
      });
    },40);
  });
});

/* ---------- HERALD SETUP WIZARD ----------
   Same shell as the first-launch wizard: numbered steps, per-step help,
   live webhook validation, and a first publish at the end. */
const HWIZ={step:0,url:"",thread:"",valid:false,name:"",addr:true,pass:false,events:false};
function heraldWizard(){
  Object.assign(HWIZ,{
    step:0,
    url:$("#heraldUrl").value.trim(),
    thread:$("#heraldThread").value.trim(),
    valid:false,name:"",
    addr:swOn("tHeraldAddr"),pass:swOn("tHeraldPass"),events:swOn("tHeraldEvents")
  });
  heraldWizRender();
}
async function heraldWizFinish(){
  /* persist choices + flip sharing on, then place (or refresh) the post */
  setT("tHerald",true);
  setT("tHeraldAddr",HWIZ.addr);setT("tHeraldPass",HWIZ.pass);setT("tHeraldEvents",HWIZ.events);
  $("#heraldUrl").value=HWIZ.url.trim();
  $("#heraldThread").value=HWIZ.thread.trim();
  modalClose();
  await heraldSave();
  if(!Native.available){
    HERALD_HAS_POST=true;heraldRenderPost();
    toast("ᚺ "+T("herald.wiz.preview.toast"));return;
  }
  const r=await rpc("discord.publish");
  if(r===FAIL) return;
  if(r.ok){
    HERALD_HAS_POST=true;heraldRenderPost();
    $("#heraldUrlStat").className="wiz-stat ok";
    /* Two sentences rather than one with a gap in it: a webhook that answered without
       naming itself must not leave a trailing separator on screen. */
    $("#heraldUrlStat").textContent="ᛉ "+(HWIZ.name
      ?T("herald.hook.stat.answers",{name:HWIZ.name})
      :T("herald.hook.stat.answers.unnamed"));
    toast("ᚺ "+T("herald.publish.done.toast"));
    logLine("ok","[Herald] Discord sharing configured · status post published");
  }else{
    toast("ᚦ "+T("herald.wiz.publish.failed.toast",{reason:r.error||T("herald.publish.failed.toast")}));
  }
}
function heraldWizRender(){
  const names=[T("herald.wiz.step.welcome"),T("herald.wiz.step.webhook"),
               T("herald.wiz.step.tidings"),T("herald.wiz.step.publish")];
  const steps=`<div class="wiz-steps">`+names.map((n,i)=>
    `<span class="ws${i===HWIZ.step?" on":i<HWIZ.step?" done":""}"><b>${i+1}</b>${esc(n)}</span>`).join("")+`</div>`;
  let body="",nav="";

  if(HWIZ.step===0){
    body=
      `<div class="mtitle">${T("herald.wiz.intro.title")}</div>`+
      `<div class="wiz-help">${T("herald.wiz.intro.body")}</div>`+
      `<div class="wiz-paths">`+
      `<div><span class="r">ᛒ</span><strong>${T("herald.wiz.intro.carries")}</strong> · ${T("herald.wiz.intro.carries.body")}</div>`+
      `<div><span class="r">ᛜ</span><strong>${T("herald.wiz.intro.choose")}</strong> · ${T("herald.wiz.intro.choose.body")}</div>`+
      `<div><span class="r">ᛟ</span><strong>${T("common.wiz.need")}</strong> · ${T("herald.wiz.intro.need.body")}</div>`+
      `</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="hwCancel">${esc(T("common.button.cancel"))}</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="hwNext">${esc(T("common.button.begin"))}</button>`;
  }else if(HWIZ.step===1){
    body=
      `<div class="mtitle">${T("herald.wiz.hook.title")}</div>`+
      `<div class="wiz-help">${T("herald.wiz.hook.body")}<br>`+
      `1 · ${T("herald.wiz.hook.step1")}<br>`+
      `2 · ${T("herald.wiz.hook.step2")}<br>`+
      `3 · ${T("herald.wiz.hook.step3")}<br>`+
      `4 · ${T("herald.wiz.hook.step4")}</div>`+
      `<div class="field"><label>${esc(T("herald.wiz.hook.url.label"))}</label><input type="text" id="hwUrl" spellcheck="false" autocomplete="off" placeholder="https://discord.com/api/webhooks/…"></div>`+
      `<div class="wiz-stat dim" id="hwUrlStat">${T("herald.wiz.hook.stat.idle")}</div>`+
      `<div class="field" style="margin-top:8px"><label>${T("herald.wiz.hook.thread.label")}</label><input type="text" id="hwThread" spellcheck="false" autocomplete="off" placeholder="${T("herald.wiz.hook.thread.placeholder")}"></div>`+
      `<div class="wiz-help" style="margin-top:4px">${T("herald.wiz.hook.thread.note")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="hwBack">${esc(T("common.button.back"))}</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="hwNext" disabled>${esc(T("common.button.next"))}</button>`;
  }else if(HWIZ.step===2){
    body=
      `<div class="mtitle">${T("herald.wiz.share.title")}</div>`+
      `<div class="wiz-help">${T("herald.wiz.share.body")}</div>`+
      `<div class="togglerow"><span class="tl">${T("herald.share.address")}</span><span class="toggle${HWIZ.addr?" on":""}" id="hwAddr" title="${esc(T("herald.share.address.title"))}"></span></div>`+
      `<div class="togglerow"><span class="tl">${T("herald.share.password")}</span><span class="toggle${HWIZ.pass?" on":""}" id="hwPass" title="${esc(T("herald.wiz.share.password.title"))}"></span></div>`+
      `<div class="wiz-stat dim" id="hwPassWarn" style="color:var(--amber)">${HWIZ.pass?"ᚦ "+T("herald.wiz.share.password.warn"):""}</div>`+
      `<div class="togglerow"><span class="tl">${T("herald.share.events")}</span><span class="toggle${HWIZ.events?" on":""}" id="hwEvents" title="${esc(T("herald.wiz.share.events.title"))}"></span></div>`+
      `<div class="wiz-help" style="margin-top:2px">${T("herald.wiz.share.events.note")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="hwBack">${esc(T("common.button.back"))}</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="hwNext">${esc(T("common.button.next"))}</button>`;
  }else{
    body=
      `<div class="mtitle">${T("herald.wiz.sum.title")}</div>`+
      `<div class="wiz-sum">`+
      `<div class="row"><span class="k">${esc(T("herald.wiz.sum.webhook"))}</span><span class="v">${esc(HWIZ.name?T("herald.wiz.sum.webhook.named",{name:HWIZ.name}):T("herald.wiz.sum.webhook.validated"))}</span></div>`+
      `<div class="row"><span class="k">${T("herald.wiz.sum.thread")}</span><span class="v">${esc(HWIZ.thread.trim()||T("herald.wiz.sum.thread.none"))}</span></div>`+
      `<div class="row"><span class="k">${T("herald.wiz.sum.address")}</span><span class="v">${esc(HWIZ.addr?T("common.state.shared"):T("common.state.hidden"))}</span></div>`+
      `<div class="row"><span class="k">${T("herald.wiz.sum.password")}</span><span class="v">${esc(HWIZ.pass?T("herald.wiz.sum.password.shared"):T("common.state.hidden"))}</span></div>`+
      `<div class="row"><span class="k">${T("herald.share.events")}</span><span class="v">${esc(HWIZ.events?T("common.state.on"):T("common.state.off"))}</span></div>`+
      `</div>`+
      `<div class="wiz-help">${T("herald.wiz.sum.note")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="hwBack">${esc(T("common.button.back"))}</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="hwNext">${T("herald.wiz.publish")}</button>`;
  }

  const m=modalOpen(steps+body+`<div class="wiz-nav">${nav}</div>`,heraldWizRender);
  m.classList.add("wiz");
  const on=(sel,fn)=>{const el=m.querySelector(sel);if(el)el.addEventListener("click",fn);};
  on("#hwCancel",modalClose);
  on("#hwBack",()=>{HWIZ.step--;heraldWizRender();});
  on("#hwNext",()=>{
    if(HWIZ.step===3){heraldWizFinish();return;}
    HWIZ.step++;heraldWizRender();
  });

  if(HWIZ.step===1){
    const inp=m.querySelector("#hwUrl"),thr=m.querySelector("#hwThread"),
          stat=m.querySelector("#hwUrlStat"),next=m.querySelector("#hwNext");
    inp.value=HWIZ.url; thr.value=HWIZ.thread;
    thr.addEventListener("input",()=>{HWIZ.thread=thr.value;});
    let t=null;
    const check=()=>{
      HWIZ.url=inp.value;
      clearTimeout(t);
      const v=inp.value.trim();
      if(!v){
        HWIZ.valid=false;next.disabled=true;
        stat.className="wiz-stat dim";stat.textContent=T("herald.wiz.hook.stat.idle");
        return;
      }
      stat.className="wiz-stat dim";stat.textContent=T("common.stat.checking");
      t=setTimeout(async()=>{
        const r=Native.available?await rpc("discord.validate",{url:v})
          :{ok:HERALD_URL_RE.test(v),name:"preview-hook",error:T("herald.hook.stat.malformed")};
        const ok=r!==FAIL&&!!r.ok;
        HWIZ.valid=ok;HWIZ.name=ok?(r.name||""):"";next.disabled=!ok;
        stat.className="wiz-stat "+(ok?"ok":"bad");
        stat.textContent=ok
          ?("ᛉ "+(HWIZ.name?T("herald.wiz.hook.stat.answers",{name:HWIZ.name})
                           :T("herald.wiz.hook.stat.answers.unnamed")))
          :("ᚦ "+((r&&r.error)||T("herald.wiz.hook.stat.bad")));
      },350);
    };
    inp.addEventListener("input",check);
    if(HWIZ.url) check();
    setTimeout(()=>inp.focus(),30);
  }
  if(HWIZ.step===2){
    const flip=(id,key)=>{const el=m.querySelector(id);el.addEventListener("click",()=>{
      HWIZ[key]=!HWIZ[key];el.classList.toggle("on",HWIZ[key]);
      if(key==="pass") m.querySelector("#hwPassWarn").textContent=HWIZ.pass?"ᚦ "+T("herald.wiz.share.password.warn"):"";
    });};
    flip("#hwAddr","addr");flip("#hwPass","pass");flip("#hwEvents","events");
  }
}

/* ---------- WAYSTONE (custom join domain wizard) ----------
   Lets friends join by name (valheim.example.com) instead of a raw IP. Valheim
   clients resolve A/AAAA records when joining by name, but there is NO SRV
   support - the port always travels with the name. The domain is a user-level
   pref (CustomJoinDomain); every join surface prefers it once raised. */
const WWIZ={step:0,domain:"",syntaxOk:false,res:null};
const WAYSTONE_HOST_RE=/^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{1,62}$/i;
function waystoneNormalize(v){
  return (v||"").trim().toLowerCase()
    .replace(/^[a-z][a-z0-9+.-]*:\/\//,"") /* pasted with a scheme */
    .replace(/[/#?].*$/,"")                /* pasted with a path */
    .replace(/:\d+$/,"")                   /* pasted with a port */
    .replace(/\.$/,"");                    /* trailing dot */
}
async function waystoneSave(domain){
  if(Native.available){
    const r=await rpc("userprefs.save",{prefs:{CustomJoinDomain:domain}});
    if(r===FAIL) return false;
  }
  S.domain=(domain||"").trim()||null;
  renderWaystone();
  return true;
}
function waystoneWizard(){
  Object.assign(WWIZ,{step:0,domain:S.domain||"",syntaxOk:!!S.domain,res:null});
  waystoneWizRender();
}
async function waystoneWizFinish(){
  modalClose();
  if(await waystoneSave(WWIZ.domain)){
    toast("ᛦ "+T("waystone.raised.toast",{domain:WWIZ.domain}));
    logLine("ok","[Waystone] custom join domain set · "+WWIZ.domain);
  }
}
function waystoneWizRender(){
  const names=["WELCOME","NAME","POINT","PROVE"];
  const steps=`<div class="wiz-steps">`+names.map((n,i)=>
    `<span class="ws${i===WWIZ.step?" on":i<WWIZ.step?" done":""}"><b>${i+1}</b>${n}</span>`).join("")+`</div>`;
  const myIp=S.extIp||(Native.available?"…":"203.0.113.42");
  const port=S.prefs?.Port??2456;
  let body="",nav="";

  if(WWIZ.step===0){
    body=
      `<div class="mtitle">${T("waystone.wiz.intro.title")}</div>`+
      `<div class="wiz-help">${T("waystone.wiz.intro.body")}</div>`+
      `<div class="wiz-paths">`+
      `<div><span class="r">ᛟ</span><strong>${T("common.wiz.need")}</strong> · ${T("waystone.wiz.intro.need.body")}</div>`+
      `<div><span class="r">ᚦ</span><strong>${T("waystone.wiz.intro.limit")}</strong> · ${T("waystone.wiz.intro.limit.body",{join:mono(T("waystone.wiz.intro.limit.name")+":"+port)})}</div>`+
      `<div><span class="r">ᛜ</span><strong>${T("waystone.wiz.intro.shows")}</strong> · ${T("waystone.wiz.intro.shows.body")}</div>`+
      `</div>`+
      (S.domain?`<div class="wiz-stat dim" style="margin-top:8px">${T("waystone.wiz.intro.current")} · <span class="mono">${esc(S.domain)}</span></div>`:"");
    nav=`<button class="btn btn-ghost btn-sm" id="wwCancel">${esc(T("common.button.cancel"))}</button>`+
        (S.domain?`<button class="btn btn-ghost btn-sm" id="wwRemove" title="${esc(T("waystone.wiz.remove.title"))}">${T("common.button.remove")}</button>`:"")+
        `<span class="grow"></span><button class="btn btn-ember btn-sm" id="wwNext">${esc(T("common.button.begin"))}</button>`;
  }else if(WWIZ.step===1){
    body=
      `<div class="mtitle">${T("waystone.wiz.name.title")}</div>`+
      `<div class="wiz-help">${T("waystone.wiz.name.body")}</div>`+
      `<div class="field"><label>${T("waystone.wiz.name.label")}</label><input type="text" id="wwDomain" spellcheck="false" autocomplete="off" placeholder="valheim.example.com" title="${esc(T("waystone.wiz.name.input.title"))}"></div>`+
      `<div class="wiz-stat dim" id="wwDomStat">${T("waystone.wiz.name.stat.idle")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wwBack">${esc(T("common.button.back"))}</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wwNext" disabled>${esc(T("common.button.next"))}</button>`;
  }else if(WWIZ.step===2){
    body=
      `<div class="mtitle">${T("waystone.wiz.dns.title")}</div>`+
      `<div class="wiz-help">${T("waystone.wiz.dns.body",{domain:mono(WWIZ.domain),ip:mono(myIp)})}</div>`+
      `<div class="wiz-paths">`+
      `<div><span class="r">ᚨ</span><strong>${T("waystone.wiz.dns.own")}</strong><br>`+
      `1 · ${T("waystone.wiz.dns.own.step1")}<br>`+
      `2 · ${T("waystone.wiz.dns.own.step2",{ip:mono(myIp)})}<br>`+
      `3 · ${T("waystone.wiz.dns.own.step3")}</div>`+
      `<div><span class="r">ᛉ</span><strong>${T("waystone.wiz.dns.ddns")}</strong><br>`+
      `· ${T("waystone.wiz.dns.ddns.duckdns")}<br>`+
      `· ${T("waystone.wiz.dns.ddns.noip")}<br>`+
      `· ${T("waystone.wiz.dns.ddns.cname")}</div>`+
      `<div><span class="r">ᚦ</span><strong>${T("waystone.wiz.dns.remember")}</strong> · ${T("waystone.wiz.dns.remember.body",{port:mono(String(port))})}</div>`+
      `</div>`+
      `<div class="wiz-help" style="margin-top:6px">${T("waystone.wiz.dns.note")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wwBack">${esc(T("common.button.back"))}</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wwNext">${T("waystone.wiz.dns.next")}</button>`;
  }else{
    body=
      `<div class="mtitle">${T("waystone.wiz.check.title")}</div>`+
      `<div class="wiz-help">${T("waystone.wiz.check.body",{domain:mono(WWIZ.domain)})}</div>`+
      `<div class="wiz-stat dim" id="wwCheckStat">${T("waystone.wiz.check.asking")}</div>`+
      `<div class="wiz-sum" id="wwCheckSum" style="display:none"></div>`+
      `<div class="wiz-help" style="margin-top:6px">${T("waystone.wiz.check.note")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wwBack">${esc(T("common.button.back"))}</button>`+
        `<button class="btn btn-ghost btn-sm" id="wwAgain">${T("waystone.wiz.check.again")}</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wwNext">${T("waystone.wiz.check.ok")}</button>`;
  }

  const m=modalOpen(steps+body+`<div class="wiz-nav">${nav}</div>`,waystoneWizRender);
  m.classList.add("wiz");
  const on=(sel,fn)=>{const el=m.querySelector(sel);if(el)el.addEventListener("click",fn);};
  on("#wwCancel",modalClose);
  on("#wwBack",()=>{WWIZ.step--;waystoneWizRender();});
  on("#wwRemove",async()=>{
    modalClose();
    if(await waystoneSave("")){
      toast("ᛦ "+T("waystone.removed.toast"));
      logLine("warn","[Waystone] custom join domain removed");
    }
  });
  on("#wwNext",()=>{
    if(WWIZ.step===3){waystoneWizFinish();return;}
    WWIZ.step++;waystoneWizRender();
  });

  if(WWIZ.step===1){
    const inp=m.querySelector("#wwDomain"),stat=m.querySelector("#wwDomStat"),next=m.querySelector("#wwNext");
    inp.value=WWIZ.domain;
    let t=null;
    const check=()=>{
      clearTimeout(t);
      t=setTimeout(()=>{
        const v=waystoneNormalize(inp.value);
        WWIZ.domain=v;
        if(!v){
          WWIZ.syntaxOk=false;next.disabled=true;
          stat.className="wiz-stat dim";stat.textContent=T("waystone.wiz.name.stat.idle");
          return;
        }
        const ok=WAYSTONE_HOST_RE.test(v);
        WWIZ.syntaxOk=ok;next.disabled=!ok;
        stat.className="wiz-stat "+(ok?"ok":"bad");
        stat.textContent=ok?("ᛉ "+T("waystone.wiz.name.stat.ok",{name:v}))
                           :("ᚦ "+T("waystone.wiz.name.stat.bad"));
      },350);
    };
    inp.addEventListener("input",check);
    if(WWIZ.domain) check();
    setTimeout(()=>inp.focus(),30);
  }
  if(WWIZ.step===3){
    const stat=m.querySelector("#wwCheckStat"),sum=m.querySelector("#wwCheckSum");
    const runCheck=async()=>{
      stat.className="wiz-stat dim";stat.style.color="";stat.textContent=T("waystone.wiz.check.asking");
      sum.style.display="none";
      const r=Native.available?await rpc("domain.check",{domain:WWIZ.domain})
        :await new Promise(res=>setTimeout(()=>res({ok:true,ips:[myIp],publicIp:myIp,match:true}),600));
      if(r===FAIL){stat.className="wiz-stat bad";stat.textContent="ᚦ "+T("waystone.wiz.check.stat.failed");return;}
      WWIZ.res=r;
      if(r.ok&&r.match){
        stat.className="wiz-stat ok";
        stat.textContent="ᛉ "+T("waystone.wiz.check.stat.match");
      }else if(r.ok){
        stat.className="wiz-stat";stat.style.color="var(--amber)";
        stat.textContent="ᚦ "+T("waystone.wiz.check.stat.mismatch");
      }else{
        stat.className="wiz-stat bad";
        /* What DNS said and the fact that it said nothing are two sentences, each ending
           with the same advice: a reason-shaped gap in the middle of one reads as a typo. */
        stat.textContent="ᚦ "+(r.error
          ?T("waystone.wiz.check.stat.error",{detail:r.error})
          :T("waystone.wiz.check.stat.unresolved"));
      }
      sum.style.display="";
      sum.innerHTML=
        `<div class="row"><span class="k">${T("waystone.wiz.sum.name")}</span><span class="v mono">${esc(WWIZ.domain)}</span></div>`+
        `<div class="row"><span class="k">${T("waystone.wiz.sum.resolves")}</span><span class="v mono">${esc((r.ips&&r.ips.length?r.ips.join(" · "):"-"))}</span></div>`+
        `<div class="row"><span class="k">${T("waystone.wiz.sum.server")}</span><span class="v mono">${esc(r.publicIp||myIp)}</span></div>`+
        `<div class="row"><span class="k">${T("waystone.wiz.sum.join")}</span><span class="v mono">${esc(WWIZ.domain+":"+port)}</span></div>`;
    };
    on("#wwAgain",runCheck);
    runCheck();
  }
}
$("#waystoneBtn")?.addEventListener("click",e=>{e.stopPropagation();waystoneWizard();});

/* ---------- RUNES (BepInEx .cfg editor) ---------- */
const CFG={files:[],file:null,dirty:false,mock:null};
async function cfgListFiles(){
  if(!Native.available) return Object.keys(CFG.mock||{});
  const r=await rpc("config.list");
  return (r===FAIL||!Array.isArray(r))?null:r;
}
async function cfgRead(file){
  if(!Native.available) return CFG.mock?.[file]??"";
  const r=await rpc("config.read",{file});
  return r===FAIL?null:String(r??"");
}
async function cfgWrite(file,text){
  // NOTE: Bridge.cs config.write reads p.Value<string>("text") - param is `text`, not `content`
  if(!Native.available){if(CFG.mock)CFG.mock[file]=text;return true;}
  const r=await rpc("config.write",{file,text});
  return r!==FAIL;
}
/* Everything one scroll can be found by: its own file name, and the mod that wrote it.
   BepInEx names a config after the plugin that owns it, so the pieces of the name are
   worth searching on their own ("extraslots" finds shudnal.ExtraSlots.cfg), and where a
   scan has matched a plugin to that name its author and title are searchable too. */
function cfgSearchText(f){
  const name=String(f||"");
  const stem=name.replace(/\.cfg$/i,"");
  const bits=[name,stem.split(/[.\-_]+/).filter(Boolean).join(" ")];
  const low=name.toLowerCase();
  (S.mods||[]).forEach(m=>{
    if(!m) return;
    const mod=String(m.ModName||"").toLowerCase(), full=String(m.FullName||"").toLowerCase();
    if((mod&&low.includes(mod))||(full&&low.includes(full)))
      bits.push(m.ModName,m.Author,m.FullName);
  });
  return bits.filter(Boolean).join(" ");
}
/* THE ONE PLACE the Configs search is allowed to be applied: the list render. Opening,
   reading, saving and reverting all work from CFG.files and CFG.file, which the search
   never touches, so a narrowed list is only ever a narrowed list. */
function cfgFilesForList(){
  const tokens=searchTokens(S.runeFilter);
  if(!tokens.length) return CFG.files;
  return CFG.files.filter(f=>searchHit(cfgSearchText(f),tokens));
}
function renderCfgList(){
  $("#runesSub").textContent=T("runes.sub.count",{count:CFG.files.length});
  const shown=cfgFilesForList();
  $("#cfgList").innerHTML=shown.map(f=>
    `<div class="cfg-item${f===CFG.file?" sel":""}" data-f="${esc(f)}"><span class="cfgname">${esc(f)}</span>${(f===CFG.file&&CFG.dirty)?'<span class="dot"></span>':""}</div>`
  ).join("")||(CFG.files.length
    ?`<div class="cfg-item" style="opacity:.5;cursor:default"><span class="cfgname">${esc(T("runes.list.no_match"))}</span></div>`
    :`<div class="cfg-item" style="opacity:.5;cursor:default"><span class="cfgname">${esc(T("runes.list.empty"))}</span></div>`);
  renderRuneShowing(shown.length,CFG.files.length);
}
/* "showing N of M" beside the box, and only while something is typed. */
function renderRuneShowing(shown,total){
  const el=$("#runeShowing"); if(!el) return;
  el.textContent=searchTokens(S.runeFilter).length?T("common.showing",{shown:shown,total:total}):"";
}
function setRuneFilter(value){
  S.runeFilter=String(value||"");
  const box=$("#runeSearch");
  if(box&&box.value!==S.runeFilter) box.value=S.runeFilter;
  renderCfgList();
}
$("#runeSearch")?.addEventListener("input",e=>setRuneFilter(e.target.value));
$("#runeSearch")?.addEventListener("keydown",e=>{
  if(e.key!=="Escape") return;
  e.preventDefault(); e.stopPropagation();
  setRuneFilter("");
});

/* ---- Find inside the open scroll ----
   The editor is one plain text box, so the match is PAINTED rather than selected: a layer
   behind the text holds the same words in the same font at the same width, the match is
   marked on it, and the pane scrolls to it. Nothing in here focuses the editor, moves its
   cursor, selects any of its text or writes to it, so a keystroke meant for the find box
   can never land in the config file and the file is never left with a selection waiting to
   be typed over. Save still writes the whole document exactly as it stands. */
let CFG_FIND_AT=0;
/* The match on show, kept so a window resize can paint it again where the words have
   moved to. Null means nothing is marked. */
let CFG_FIND_MARK=null;
/* Past this many letters the layer is not painted: holding a second copy of a very large
   file in the page for every keystroke is not worth it, so such a file gets the scroll and
   the count and no paint. Config files are a few thousand letters, so this never fires in
   practice; it is here so a huge one cannot make the box crawl. */
const CFG_FIND_PAINT_MAX=200000;
function cfgFindClearMark(){
  CFG_FIND_MARK=null;
  const layer=$("#cfgEdMark"); if(layer&&layer.innerHTML) layer.innerHTML="";
}
function cfgFindReset(){
  CFG_FIND_AT=0;
  cfgFindClearMark();
  const box=$("#cfgFind"); if(box) box.value="";
  const count=$("#cfgFindCount"); if(count) count.textContent="";
}
/* The host has typed in the file, so the painted match no longer stands where it was and
   the count no longer holds. The words stay in the find box, and the next Enter looks
   again at the text as it is now. */
function cfgFindStale(){
  if(!CFG_FIND_MARK) return;
  cfgFindClearMark();
  const count=$("#cfgFindCount"); if(count) count.textContent="";
}
function cfgFindMatches(needle){
  const ed=$("#cfgEditor");
  if(!ed||!needle) return [];
  const hay=ed.value.toLowerCase(), q=needle.toLowerCase();
  const out=[];
  for(let i=hay.indexOf(q);i>=0;i=hay.indexOf(q,i+q.length)) out.push(i);
  return out;
}
/* Paints the marked match and, when asked, brings it into view. The layer wraps its lines
   the way the editor wraps its own, so the mark sits over the letters it belongs to
   wherever they have ended up, and the scroll is worked out from where the mark landed
   rather than guessed from a line count. */
function cfgFindPaint(bringIntoView){
  const ed=$("#cfgEditor"), layer=$("#cfgEdMark");
  if(!ed||!layer||!CFG_FIND_MARK) return;
  if(!ed.clientWidth) return;   // the hall is not on screen; the next paint will place it
  const at=CFG_FIND_MARK.at, len=CFG_FIND_MARK.len;
  const text=ed.value;
  if(text.length>CFG_FIND_PAINT_MAX){
    layer.innerHTML="";
    if(bringIntoView){
      const before=text.slice(0,at).split("\n").length-1;
      const lineHeight=Math.max(12,Math.round(parseFloat(getComputedStyle(ed).lineHeight)||19));
      ed.scrollTop=Math.max(0,before*lineHeight-Math.round(ed.clientHeight/2));
    }
    return;
  }
  layer.style.width=ed.clientWidth+"px";
  layer.innerHTML=esc(text.slice(0,at))+"<mark>"+esc(text.slice(at,at+len))+"</mark>"+esc(text.slice(at+len))+"\n";
  if(bringIntoView){
    const hit=layer.querySelector("mark");
    const top=hit?hit.offsetTop:0;
    ed.scrollTop=Math.max(0,Math.round(top-ed.clientHeight/2));
  }
  layer.scrollTop=ed.scrollTop; layer.scrollLeft=ed.scrollLeft;
}
function cfgFindGo(step){
  const ed=$("#cfgEditor"); if(!ed) return;
  const box=$("#cfgFind");
  const needle=(box?.value||"");
  const count=$("#cfgFindCount");
  if(!needle){cfgFindClearMark(); if(count) count.textContent=""; return;}
  const hits=cfgFindMatches(needle);
  if(!hits.length){
    cfgFindClearMark();
    if(count) count.textContent=T("runes.find.no_match");
    return;
  }
  CFG_FIND_AT=((CFG_FIND_AT+step)%hits.length+hits.length)%hits.length;
  CFG_FIND_MARK={at:hits[CFG_FIND_AT],len:needle.length};
  cfgFindPaint(true);
  if(count) count.textContent=T("runes.find.count",{index:CFG_FIND_AT+1,total:hits.length});
}
$("#cfgFind")?.addEventListener("input",()=>{CFG_FIND_AT=-1;cfgFindGo(1);});
$("#cfgFind")?.addEventListener("keydown",e=>{
  if(e.key==="Enter"){e.preventDefault();cfgFindGo(e.shiftKey?-1:1);return;}
  if(e.key!=="Escape") return;
  e.preventDefault(); e.stopPropagation();
  cfgFindReset();
});
/* The chip is pressable, and a press on it must not pull the keyboard out of the box the
   host is typing in: the mouse press is stopped from moving the keyboard at all, so Enter
   still walks the matches after a click. */
$("#cfgFindNext")?.addEventListener("mousedown",e=>e.preventDefault());
$("#cfgFindNext")?.addEventListener("click",()=>cfgFindGo(1));
$("#cfgFindNext")?.addEventListener("keydown",e=>{
  if(e.key==="Enter"||e.key===" "||e.key==="Spacebar"){e.preventDefault();cfgFindGo(1);}
});
/* The layer sits still behind a pane that scrolls, so it is moved with it. */
$("#cfgEditor")?.addEventListener("scroll",()=>{
  const ed=$("#cfgEditor"), layer=$("#cfgEdMark");
  if(!ed||!layer) return;
  layer.scrollTop=ed.scrollTop; layer.scrollLeft=ed.scrollLeft;
});
function setCfgDirty(d){
  if(CFG.dirty===d) return;
  CFG.dirty=d;
  $("#cfgSaveBtn").disabled=!d||!CFG.file;
  renderCfgList();
}
async function loadCfg(f,force){
  if(!f||(f===CFG.file&&!force)) return;
  if(CFG.dirty&&!force){
    /* One whole sentence with both file names in named slots. The two names arrive
       already escaped and already wrapped, so the entry itself carries no markup and
       a language that puts the new scroll first can put it first. */
    confirmModal(()=>T("runes.dirty.title"),
      ()=>T("runes.dirty.open.body",{file:bold(CFG.file),next:bold(f)}),
      ()=>T("runes.dirty.discard"),()=>loadCfg(f,true));
    return;
  }
  const text=await cfgRead(f);
  if(text===null) return;
  CFG.file=f; CFG.dirty=false;
  $("#cfgEditor").value=text;
  $("#cfgSaveBtn").disabled=true;
  resetCfgSaveBtn();
  cfgFindReset();   // a find was about the scroll that was open, not this one
  renderCfgList();
}
async function refreshCfgList(reRead){
  const files=await cfgListFiles();
  if(files===null) return;
  CFG.files=files;
  if(CFG.file&&!files.includes(CFG.file)){
    // current file vanished from disk
    CFG.file=null; CFG.dirty=false;
    $("#cfgEditor").value=""; $("#cfgSaveBtn").disabled=true;
    cfgFindStale();   // the text it was painted over is not there any more
  }else if(reRead&&CFG.file&&!CFG.dirty){
    const text=await cfgRead(CFG.file);
    if(text!==null){$("#cfgEditor").value=text;cfgFindStale();}
  }
  renderCfgList();
}
let cfgConfirmT=null;
function resetCfgSaveBtn(){
  clearTimeout(cfgConfirmT); cfgConfirmT=null;
  const b=$("#cfgSaveBtn");
  b.classList.remove("confirming");
  b.textContent=T("runes.save.label");
}
$("#cfgList").addEventListener("click",e=>{
  const it=e.target.closest(".cfg-item[data-f]");
  if(it) loadCfg(it.dataset.f,false);
});
$("#cfgEditor").addEventListener("input",()=>{if(CFG.file)setCfgDirty(true);cfgFindStale();});
$("#cfgReloadBtn").addEventListener("click",()=>{
  const go=async()=>{
    CFG.dirty=false;
    await refreshCfgList(true);
    if(CFG.file){const t=await cfgRead(CFG.file);if(t!==null){$("#cfgEditor").value=t;cfgFindStale();}}
    $("#cfgSaveBtn").disabled=true; resetCfgSaveBtn(); renderCfgList();
    toast("ᛋ "+T("runes.reloaded.toast"));
  };
  if(CFG.dirty){
    confirmModal(()=>T("runes.dirty.title"),
      ()=>T("runes.dirty.reload.body",{file:bold(CFG.file)}),
      ()=>T("runes.dirty.discard_reload"),go);
  }else go();
});
$("#cfgSaveBtn").addEventListener("click",async()=>{
  if(!CFG.file||!CFG.dirty) return;
  const b=$("#cfgSaveBtn");
  if(!b.classList.contains("confirming")){
    b.classList.add("confirming");
    b.textContent=T("runes.save.confirm.label");
    cfgConfirmT=setTimeout(resetCfgSaveBtn,3000);
    return;
  }
  resetCfgSaveBtn();
  const ok=await cfgWrite(CFG.file,$("#cfgEditor").value);
  if(!ok) return;
  setCfgDirty(false);
  $("#cfgSaveBtn").disabled=true;
  toast("ᛉ "+T("runes.saved.toast",{file:CFG.file}));
  logLine("ok","[BakaLoader] config '"+CFG.file+"' written");
});
$("#cfgOpenBtn").addEventListener("click",()=>{
  if(!Native.available){toast("ᛃ "+T("runes.open.preview.toast"));return;}
  rpc("shell.open",{target:"config"});
});

/* ============ ATLAS (webmap + skies) ============
   Mod-free webmap: the biome map is rendered server-side from the world seed
   (atlas.render), live world facts come from a read-only .db parse
   (atlas.worldInfo), and fog of war is the cartography tables' combined
   explored mask. No plugin, no game hook - so no live player dots; the
   roster shows who's online instead. */
const ATLAS={
  world:null,
  mapImg:null, mapReady:false, mapExtent:10500,   // world meters, center -> png edge
  fogImg:null, fogReady:false, fogTex:null, fogMask:null, fogExtent:12288, // fog png spans ±2048*12/2 m
  viewBase:0,                                     // min(wrap w,h) at last resize - keeps the chart scaling with the window
  info:null, rendering:false, seq:0,
  infoError:null,                                 // why the last world-info read did not answer
  layers:{portals:true,pois:true,builds:true,pins:false,fog:true},
  fogWipe:null,                                   // {to,r} while the fog toggle wipes concentrically from world center
  cam:{cx:0,cz:0,ppm:0},                          // ppm = screen px per world meter
  drag:null,
};
function atlasWorldName(){return (S.prefs&&S.prefs.WorldName)||null;}
/* The sentences the map's own line can carry. Ids rather than English, and in a table
   rather than at each call site, so atlasMsg can be handed an id: the hall is entered
   while app.js is still being evaluated, and a T() answered before the catalog lands
   answers with the id itself. The property names end in Id because that is how the
   catalog gate sees a table that holds ids. */
const ATLAS_MSG={loadingId:"atlas.msg.loading",noWorldId:"atlas.msg.no_world",
  chartingId:"atlas.msg.charting",chartingPctId:"atlas.msg.charting.progress",
  undrawnId:"atlas.msg.undrawn",imageFailedId:"atlas.msg.image_failed"};
let ATLAS_MSG_NOW=null;
/**
 * The line across the map, by catalog id. Kept as the id it was given, so the boot
 * repaint can word it again the moment the catalog arrives.
 * @param {string|null} id a catalog id, or null to take the line down
 * @param {object} [params] named slot values
 */
function atlasMsg(id,params){
  const el=$("#atlasMsg"); if(!el) return;
  ATLAS_MSG_NOW=id==null?null:{id,params:params||null};
  if(id==null){el.classList.add("hidden");return;}
  el.textContent=T(id,params); el.classList.remove("hidden");
}
/* Whatever the map is saying right now, said again in the words that just landed. */
function renderAtlasMsg(){
  if(ATLAS_MSG_NOW) atlasMsg(ATLAS_MSG_NOW.id,ATLAS_MSG_NOW.params);
}
function atlasReset(){
  ATLAS.world=null; ATLAS.mapImg=null; ATLAS.mapReady=false;
  ATLAS.fogImg=null; ATLAS.fogReady=false; ATLAS.fogTex=null; ATLAS.fogMask=null; ATLAS.info=null;
  ATLAS.infoError=null;
  ATLAS.cam.ppm=0; ATLAS.seq++;
  atlasMsg(ATLAS_MSG.loadingId);
  if(currentPage==="atlas") atlasEnter();
}
async function atlasEnter(){
  atlasResize();
  if(!Native.available){atlasMock();return;}
  const world=atlasWorldName();
  $("#atlasWorldName").textContent=world||"-";
  if(!world){atlasMsg(ATLAS_MSG.noWorldId);return;}
  if(ATLAS.world!==world){
    ATLAS.world=world; ATLAS.mapImg=null; ATLAS.mapReady=false;
    ATLAS.fogImg=null; ATLAS.fogReady=false; ATLAS.fogTex=null; ATLAS.fogMask=null; ATLAS.info=null; ATLAS.cam.ppm=0;
  }
  if(!ATLAS.mapReady&&!ATLAS.rendering) atlasRenderMap(false);
  atlasRefreshInfo();
}
async function atlasRenderMap(force){
  const world=ATLAS.world; if(!world||ATLAS.rendering) return;
  ATLAS.rendering=true; const seq=++ATLAS.seq;
  atlasMsg(ATLAS_MSG.chartingId);
  const r=await rpc("atlas.render",{world,size:2048,force:!!force});
  ATLAS.rendering=false;
  if(seq!==ATLAS.seq) return;
  if(r===FAIL||!r||!r.url){
    atlasMsg(ATLAS_MSG.undrawnId);
    return;
  }
  ATLAS.mapExtent=Number(r.edge)||10500;
  const warn=$("#atlasModWarn");
  if(warn){
    if(r.warnings&&r.warnings.length){
      warn.style.display=""; warn.textContent="⚠ "+T("atlas.warn.worldgen_mods"); warn.title=r.warnings.join("\n");
    }else warn.style.display="none";
  }
  const img=new Image();
  img.onload=()=>{
    if(seq!==ATLAS.seq) return;
    ATLAS.mapImg=img; ATLAS.mapReady=true; atlasMsg(null);
    if(!ATLAS.cam.ppm) atlasFit();
    atlasDraw();
  };
  img.onerror=()=>{if(seq===ATLAS.seq)atlasMsg(ATLAS_MSG.imageFailedId);};
  img.src=r.url+"?t="+Date.now(); // bust the WebView2 cache after re-renders
}
async function atlasRefreshInfo(){
  const world=ATLAS.world; if(!world) return;
  const seq=ATLAS.seq;
  /* Keep the rejection instead of swallowing it. A scan already in flight, or a reader
     that threw, used to leave the last world's facts on screen with nothing said. */
  let err=null;
  const r=await Native.call("atlas.worldInfo",{world}).catch(e=>{err=e;return null;});
  if(seq!==ATLAS.seq) return;
  if(!r){
    ATLAS.infoError=(err&&err.message)?String(err.message):"";
    renderAtlasSide();
    return;
  }
  ATLAS.infoError=null;
  r._rxAt=Date.now(); // weather clock anchors netTime+savedAge to this receipt
  ATLAS.info=r;
  if(r.fogUrl){
    ATLAS.fogExtent=Number(r.fogExtent)||12288;
    const fi=new Image();
    fi.onload=()=>{if(seq===ATLAS.seq){ATLAS.fogImg=fi;ATLAS.fogTex=null;ATLAS.fogMask=null;ATLAS.fogReady=true;atlasDraw();}};
    fi.src=r.fogUrl;
  }else{ATLAS.fogImg=null;ATLAS.fogReady=false;}
  renderAtlasSide(); atlasDraw();
}
/* Three different things used to read as one: a world that has genuinely never been
   saved, a save on disk this reader could not make sense of, and a read that never
   answered at all. The C# reader already works out which and can say so, so pass its
   own sentence through when it sends one, and never tell a host to wait for a save
   that already exists. */
function atlasNoDbText(info){
  const said=String((info&&(info.reason||info.diagnostic))||ATLAS.infoError||"").trim();
  if(said) return T("atlas.save.failed.detail",{detail:said});
  /* infoError is null until a read actually fails, and "" for a failure that carried
     no message, so an empty string still means failed. */
  if(ATLAS.infoError!=null)
    return T("atlas.save.unreadable");
  if(!info) return T("atlas.save.reading");
  if(info.saveExists===true||info.hasSave===true)
    return T("atlas.save.unparsed");
  return T("atlas.save.none");
}
function renderAtlasSide(){
  const info=ATLAS.info;
  $("#atlasWorldName").textContent=ATLAS.world||"-";
  if(!info||!info.hasDb){
    const why=atlasNoDbText(info);
    $("#atlasDay").textContent="-";
    $("#atlasClock").textContent=why;
    $("#atlasClock").title=why;
    $("#atlasSaved").textContent="-"; $("#atlasExplored").textContent="-"; $("#atlasEvent").textContent="-";
  }else{
    $("#atlasClock").title="";
    $("#atlasDay").textContent=T("atlas.fact.day",{day:info.day});
    const frac=((Number(info.netTime)%1800)+1800)%1800/1800;
    const mins=Math.floor(frac*24*60);
    $("#atlasClock").textContent=T("atlas.fact.clock",{clock:String(Math.floor(mins/60)).padStart(2,"0")+":"+String(mins%60).padStart(2,"0")});
    $("#atlasSaved").textContent=atlasAge(info.savedAgeSeconds);
    $("#atlasExplored").textContent=info.hasSharedMap
      ? T("atlas.fact.explored.value",{percent:info.exploredPercent.toFixed(1),count:info.mapTables})
      : T("atlas.fact.explored.none");
    $("#atlasEvent").textContent=info.eventName?info.eventName:T("atlas.fact.event.none");
  }
  /* A world read with chunks missing has fewer portals, builds and pins than the world
     really has, so say so rather than let an empty list read as "there are none". */
  const cn=$("#atlasChunkNote");
  if(cn){
    const skipped=info&&info.hasDb?(info.chunksSkipped||0):0;
    cn.style.display=skipped>0?"":"none";
    if(skipped>0) cn.textContent=T("atlas.chunks.skipped",{count:skipped,total:info.chunksTotal||skipped});
  }
  /* The Ashlands caveat that used to sit here is gone: the atlas now draws that coast
     from the game's own height function, so the shape on screen is the shape in game.
     #atlasShapeNote stays as an empty, hidden slot for the next note that needs it. */
  const sn=$("#atlasShapeNote");
  if(sn&&sn.textContent){sn.textContent="";sn.style.display="none";}
  /* roster: who's online (mod-free maps can't place them) */
  const roster=$("#atlasRoster");
  if(roster){
    const online=(S.players||[]).filter(p=>p.status==="Online");
    roster.innerHTML=online.map(p=>
      `<div class="vrow"><span class="vdot on"></span><span class="vname">${esc(p.displayName)}</span></div>`
    ).join("")||emptyState({compact:true,mark:"\u16D7",title:T("atlas.roster.empty.title"),
        reason:T("atlas.roster.empty.reason")});
    esWire(roster);
  }
  /* waypoints: altars & traders, then portals, then table pins */
  const wp=$("#atlasWaypoints");
  if(wp){
    const rows=[];
    if(info&&info.hasDb){
      (info.pois||[]).forEach(l=>rows.push({r:"ᛒ",n:l.label,x:l.x,z:l.z}));
      (info.portals||[]).forEach(p=>rows.push({r:"ᛈ",n:p.tag||T("atlas.waypoint.portal.untagged"),x:p.x,z:p.z}));
      (info.pins||[]).forEach(p=>rows.push({r:"ᛘ",n:p.name||T("atlas.waypoint.pin.unnamed"),x:p.x,z:p.z}));
    }
    wp.innerHTML=rows.map((w,i)=>
      `<div class="wp" data-i="${i}"><span class="wr">${w.r}</span><span class="wn">${esc(w.n)}</span><span class="wc">${Math.round(w.x)}, ${Math.round(w.z)}</span></div>`
    ).join("")||emptyState({compact:true,mark:"\u16DE",title:T("atlas.waypoints.empty.title"),
        reason:T("atlas.waypoints.empty.reason"),
        action:{name:"redrawMap",label:T("atlas.waypoints.empty.action")}});
    esWire(wp);
    wp._rows=rows;
  }
  renderWeather();
  refreshScrollCues();
}
/* How old the rendered map is, in the four tiers this panel has always used: seconds
   up to a minute and a half, whole minutes up to an hour and a half, hours to one
   decimal up to two days, then whole days.
   The English is pinned to the shapes 1.1.x showed - "40s ago", "4 min ago",
   "1.5 h ago", "3 days ago" - because Intl.RelativeTimeFormat has no style that
   writes all four of them: narrow says "4m ago" and "1.5h ago", short says "4 min.
   ago" with a full stop, and long says "4 minutes ago". Rather than let the panel's
   wording drift to whichever style is closest, the four sentences are catalog entries
   with the number in a named slot. The NUMBER still goes through the lookup, so a
   host who writes 1,5 reads 1,5; the words around it are a translator's.
   The hand-rolled arm below is the floor for a window whose i18n.js never arrived or
   whose catalog fetch failed, and it writes the same English the entries do. */
/* One entry per tier. The property names end in Id because that is how the catalog gate
   recognises a table that holds ids rather than English: the world dials do the same,
   and without it these four would read as orphan keys nothing asks for. */
/* ATLAS-AGE-BEGIN (pure - no DOM; run as a table by scripts/i18n/atlas_age_selftest.js,
   which is what keeps the English here pinned to the four shapes 1.1.x showed) */
const ATLAS_AGE={secondId:"atlas.age.seconds",minuteId:"atlas.age.minutes",
                 hourId:"atlas.age.hours",dayId:"atlas.age.days"};
/* One tier's sentence. The slot carries the number written out for the host, and the
   raw number rides alongside it in pluralValue so the day tier's category is chosen
   from the NUMBER rather than from the words for it. Reading it back off the slot is
   what this used to do, and it only ever worked in ASCII: ar-EG writes ١ for 1, and
   Number("١") is NaN, so every Arabic day would have read "1 days ago". */
function atlasAgeSay(unit,count,fallback){
  const L=intl();
  const id=ATLAS_AGE[unit+"Id"];
  if(!L||!L.has(id)) return fallback;
  return T(id,{count:L.fmtNumber(count,unit==="hour"
    ?{minimumFractionDigits:1,maximumFractionDigits:1,useGrouping:false}
    :{maximumFractionDigits:0,useGrouping:false}),pluralValue:count});
}
function atlasAge(sec){
  sec=Number(sec)||0;
  if(sec<90) return atlasAgeSay("second",Math.round(sec),Math.round(sec)+"s ago");
  if(sec<5400) return atlasAgeSay("minute",Math.round(sec/60),Math.round(sec/60)+" min ago");
  if(sec<172800){
    const h=Number((sec/3600).toFixed(1));
    return atlasAgeSay("hour",h,(sec/3600).toFixed(1)+" h ago");
  }
  return atlasAgeSay("day",Math.round(sec/86400),Math.round(sec/86400)+" days ago");
}
/* ATLAS-AGE-END */
/* --- deterministic skies: Valheim's weather is a pure function of net time ---
   Weather rerolls every 666 s from Unity's xorshift128 PRNG seeded with the
   period index; wind is 4 re-seeded octaves. No mod, no game hook - the same
   math the server runs, so we can forecast. Env tables drift with game patches;
   they are data, not code. */
/* WX-ENGINE-BEGIN (pure - no DOM; extracted by the validation harness) */
const WX_ENVS={
  Meadows:[["Clear",25],["Rain",1],["Misty",1],["ThunderStorm",1],["LightRain",1]],
  BlackForest:[["DeepForest_Mist",20],["Rain",1],["Misty",1],["ThunderStorm",1]],
  Swamp:[["SwampRain",1]],
  Mountain:[["SnowStorm",1],["Snow",5]],
  Plains:[["Heath_clear",5],["Misty",1],["LightRain",1]],
  Mistlands:[["Mistlands_clear",15],["Mistlands_rain",1],["Mistlands_thunder",1]],
  Ashlands:[["Ashlands_ashrain",30],["Ashlands_misty",2],["Ashlands_CinderRain",4],["Ashlands_storm",1]],
  DeepNorth:[["Twilight_SnowStorm",1],["Twilight_Snow",2],["Twilight_Clear",1]],
  Ocean:[["Rain",1],["LightRain",1],["Misty",1],["Clear",10],["ThunderStorm",1]],
};
const WX_WIND={Clear:[.1,.6],Misty:[.1,.3],Rain:[.5,1],LightRain:[.1,.6],ThunderStorm:[.8,1],
  DeepForest_Mist:[.1,.6],SwampRain:[.1,.3],Snow:[.1,.6],SnowStorm:[.8,1],Heath_clear:[.4,.8],
  Twilight_Clear:[.2,.6],Twilight_Snow:[.3,.6],Twilight_SnowStorm:[.7,1],
  Mistlands_clear:[.05,.2],Mistlands_rain:[.05,.2],Mistlands_thunder:[.5,1],
  Ashlands_ashrain:[.1,.5],Ashlands_misty:[.1,.5],Ashlands_CinderRain:[.7,.75],Ashlands_storm:[1,1]};
/* Unity Random (xorshift128) - float-rounded to match the game's 32-bit math */
function wxRng(seed){
  let x=seed>>>0,
      y=(Math.imul(x,1812433253)+1)>>>0,
      z=(Math.imul(y,1812433253)+1)>>>0,
      w=(Math.imul(z,1812433253)+1)>>>0;
  const next=()=>{let t=(x^(x<<11))>>>0;t=(t^(t>>>8))>>>0;x=y;y=z;z=w;w=(w^(w>>>19)^t)>>>0;return w;};
  const value=()=>Math.fround((next()&0x7FFFFF)/8388607);
  const range=(a,b)=>Math.fround(Math.fround(Math.fround(a-b)*value())+b); // Unity Range = REVERSED lerp
  return {next,value,range};
}
function wxEnvAt(netTime,biome){
  const envs=WX_ENVS[biome]||WX_ENVS.Meadows;
  const r=wxRng(Math.floor(netTime/666)|0);
  let total=0; for(const e of envs) total=Math.fround(total+e[1]);
  const roll=r.range(0,total);
  let cum=0;
  for(const e of envs){cum=Math.fround(cum+e[1]); if(cum>=roll) return e[0];}
  return envs[envs.length-1][0];
}
function wxWindAt(netTime,env){
  let angle=0,intensity=0.5;
  for(const o of [1,2,4,8]){
    const r=wxRng(Math.floor(netTime/(1000/o))|0);
    angle=Math.fround(angle+Math.fround(r.value()*Math.fround(2*Math.PI/o)));
    intensity=Math.fround(intensity+Math.fround(Math.fround(-0.5/o)+Math.fround(r.value()/o)));
  }
  const wr=WX_WIND[env]||[0.05,1];
  const t=Math.min(1,Math.max(0,intensity));
  let inten=wr[0]+(wr[1]-wr[0])*t;          // Mathf.Lerp clamps t
  inten=Math.min(1,Math.max(0.05,inten));
  const deg=((angle*180/Math.PI)%360+360)%360; // dir=(sin a,0,cos a): 0 = north(+Z), clockwise
  return {deg,intensity:inten};
}
/* WX-ENGINE-END */
/* The sixteen points of the compass, from north and going clockwise, which is the order
   a bearing walks them in. English abbreviates a direction to letters and most languages
   do not, so each point is a catalog entry rather than one of sixteen literals a
   translator could never reach. The English abbreviation stays written beside its id:
   it is the floor a box reads before any catalog has answered, and the catalog gate holds
   the pair together so neither half can be edited on its own. The property name ends in
   Id because that is how the gate recognises a table that holds ids rather than English.
   This used to live inside the weather engine above, which is pure and asks nothing of
   the page; asking the catalog for words is not pure, so the table sits out here with
   WX_LABEL, the other table of the same shape. */
const WX_COMPASS=[
  {point:"N",   pointId:"atlas.compass.n"},
  {point:"NNE", pointId:"atlas.compass.nne"},
  {point:"NE",  pointId:"atlas.compass.ne"},
  {point:"ENE", pointId:"atlas.compass.ene"},
  {point:"E",   pointId:"atlas.compass.e"},
  {point:"ESE", pointId:"atlas.compass.ese"},
  {point:"SE",  pointId:"atlas.compass.se"},
  {point:"SSE", pointId:"atlas.compass.sse"},
  {point:"S",   pointId:"atlas.compass.s"},
  {point:"SSW", pointId:"atlas.compass.ssw"},
  {point:"SW",  pointId:"atlas.compass.sw"},
  {point:"WSW", pointId:"atlas.compass.wsw"},
  {point:"W",   pointId:"atlas.compass.w"},
  {point:"WNW", pointId:"atlas.compass.wnw"},
  {point:"NW",  pointId:"atlas.compass.nw"},
  {point:"NNW", pointId:"atlas.compass.nnw"},
];
/* The point a bearing falls on, worded. T() answers an id it cannot look up with the id
   itself, and a dotted name in the wind box would read worse than the abbreviation it
   replaced, so the English beside the id is what a window with no catalog shows. */
function wxCompass(deg){
  const p=WX_COMPASS[Math.round(deg/22.5)%16];
  if(!p) return "";
  const said=T(p.pointId);
  return said===p.pointId?p.point:said;
}
/* The env keys are Valheim's own and never move; what a host reads is the catalog's.
   The property names end in Id because that is how the catalog gate recognises a table
   that holds ids rather than English. */
const WX_LABEL={ClearId:"atlas.wx.env.clear",RainId:"atlas.wx.env.rain",MistyId:"atlas.wx.env.mist",
  ThunderStormId:"atlas.wx.env.thunderstorm",LightRainId:"atlas.wx.env.light_rain",
  DeepForest_MistId:"atlas.wx.env.forest_mist",SwampRainId:"atlas.wx.env.swamp_drizzle",
  SnowId:"atlas.wx.env.snowfall",SnowStormId:"atlas.wx.env.blizzard",
  Heath_clearId:"atlas.wx.env.heath_clear",Twilight_ClearId:"atlas.wx.env.cold_clear",
  Twilight_SnowId:"atlas.wx.env.driving_snow",Twilight_SnowStormId:"atlas.wx.env.polar_storm",
  Mistlands_clearId:"atlas.wx.env.still_mists",Mistlands_rainId:"atlas.wx.env.misty_rain",
  Mistlands_thunderId:"atlas.wx.env.mistland_thunder",Ashlands_ashrainId:"atlas.wx.env.ash_rain",
  Ashlands_mistyId:"atlas.wx.env.ash_haze",Ashlands_CinderRainId:"atlas.wx.env.cinder_rain",
  Ashlands_stormId:"atlas.wx.env.firestorm"};
/* The words for an env, or the env's own key when the tables have drifted apart. */
const wxWords=env=>{const id=WX_LABEL[env+"Id"];return id?T(id):String(env);};
const WX_RUNE={Clear:"ᛋ",Heath_clear:"ᛋ",Twilight_Clear:"ᛋ",Mistlands_clear:"ᛋ",
  Rain:"ᛚ",LightRain:"ᛚ",SwampRain:"ᛚ",Mistlands_rain:"ᛚ",Ashlands_ashrain:"ᛚ",Ashlands_CinderRain:"ᛚ",
  Misty:"ᚾ",DeepForest_Mist:"ᚾ",Ashlands_misty:"ᚾ",
  ThunderStorm:"ᚦ",Mistlands_thunder:"ᚦ",Ashlands_storm:"ᚦ",
  Snow:"ᛁ",Twilight_Snow:"ᛁ",SnowStorm:"ᚺ",Twilight_SnowStorm:"ᚺ"};
/* Net time right now: the .db value plus wall-clock drift while vikings are
   ashore. Empty modern dedicated servers PAUSE world time, and logout forces a
   save - so with nobody online the saved value IS the current value. */
function wxNetTimeNow(){
  const info=ATLAS.info;
  if(!info||!info.hasDb) return null;
  const base=Number(info.netTime)||0;
  const online=(S.players||[]).filter(p=>p.status==="Online").length;
  if(!online&&Native.available) return {t:base,paused:true};
  const rx=Number(info._rxAt)||Date.now();
  return {t:base+(Number(info.savedAgeSeconds)||0)+(Date.now()-rx)/1000,paused:false};
}
function wxClockLabel(t,relDay){
  const day=Math.floor(t/1800);
  const mins=Math.floor(((t%1800)+1800)%1800/1800*24*60);
  const hm=String(Math.floor(mins/60)).padStart(2,"0")+":"+String(mins%60).padStart(2,"0");
  return day!==relDay?"d"+day+" "+hm:hm;
}
function renderWeather(){
  const box=$("#wxBox"); if(!box) return;
  const nt=wxNetTimeNow();
  if(!nt){box.style.display="none";return;}
  box.style.display="";
  const biome=ATLAS.wxBiome||"Meadows";
  const env=wxEnvAt(nt.t,biome);
  const wind=wxWindAt(nt.t,env);
  $("#wxNow").innerHTML=`<span class="wxfr">${WX_RUNE[env]||"ᛋ"}</span>${esc(wxWords(env))}`;
  $("#wxArrow").style.transform="rotate("+Math.round(wind.deg)+"deg)";
  $("#wxWind").textContent=wxCompass(wind.deg)+" · "+Math.round(wind.intensity*100)+"%";
  const period=Math.floor(nt.t/666),today=Math.floor(nt.t/1800),rows=[];
  for(let i=1;i<=5;i++){
    const pt=(period+i)*666;
    const e=wxEnvAt(pt,biome);
    const w=wxWindAt(pt,e);
    rows.push(`<div class="wxf"><span class="wxft">${wxClockLabel(pt,today)}</span>`+
      `<span class="wxfr">${WX_RUNE[e]||""}</span><span class="wxfe">${esc(wxWords(e))}</span>`+
      `<span class="wxfw">${Math.round(w.intensity*100)}%</span></div>`);
  }
  $("#wxForecast").innerHTML=rows.join("");
  $("#wxAnchor").textContent=nt.paused
    ?T("atlas.wx.anchor.paused")
    :T("atlas.wx.anchor.live");
}
/* biome picker pills. The game's own biome keys never move; the words on the pills are
   the catalog's. */
const WX_BIOMES=[{key:"Meadows",labelId:"atlas.wx.biome.meadows"},{key:"BlackForest",labelId:"atlas.wx.biome.black_forest"},
  {key:"Swamp",labelId:"atlas.wx.biome.swamp"},{key:"Mountain",labelId:"atlas.wx.biome.mountain"},
  {key:"Plains",labelId:"atlas.wx.biome.plains"},{key:"Mistlands",labelId:"atlas.wx.biome.mistlands"},
  {key:"Ashlands",labelId:"atlas.wx.biome.ashlands"},{key:"DeepNorth",labelId:"atlas.wx.biome.deep_north"},
  {key:"Ocean",labelId:"atlas.wx.biome.ocean"}];
/* A painter rather than a line inside the wiring below: the bar is built while app.js is
   still being evaluated, which is long before the catalog lands, so the boot repaint has
   to be able to draw it again. Written once it showed nine dotted ids on the Map hall.
   The click handler sits on the strip itself, so rebuilding the pills keeps it. */
function renderWxBiomes(){
  const bs=$("#wxBiomes"); if(!bs) return;
  const now=ATLAS.wxBiome||"Meadows";
  bs.innerHTML=WX_BIOMES.map(b=>`<span class="wxb${b.key===now?" on":""}" data-b="${b.key}">${esc(T(b.labelId))}</span>`).join("");
}
(function(){
  const bs=$("#wxBiomes"); if(!bs) return;
  renderWxBiomes();
  bs.addEventListener("click",e=>{
    const el=e.target.closest(".wxb"); if(!el) return;
    ATLAS.wxBiome=el.dataset.b;
    bs.querySelectorAll(".wxb").forEach(x=>x.classList.toggle("on",x===el));
    renderWeather();
  });
})();
setInterval(()=>{if(currentPage==="atlas")renderWeather();},5000);
/* --- canvas: transforms, draw, pan/zoom --- */
function atlasWrapSize(){
  const w=$("#atlasWrap");
  return w?{w:w.clientWidth,h:w.clientHeight}:{w:0,h:0};
}
function atlasFitPpm(){
  const s=atlasWrapSize();
  return s.w&&s.h?Math.min(s.w,s.h)/(2*ATLAS.mapExtent):0;
}
function atlasFit(){
  ATLAS.cam.cx=0; ATLAS.cam.cz=0; ATLAS.cam.ppm=atlasFitPpm()||0.03;
}
function w2sX(wx){const s=atlasWrapSize();return (wx-ATLAS.cam.cx)*ATLAS.cam.ppm+s.w/2;}
function w2sY(wz){const s=atlasWrapSize();return s.h/2-(wz-ATLAS.cam.cz)*ATLAS.cam.ppm;}
function atlasResize(){
  const cv=$("#atlasCanvas"); if(!cv) return;
  const s=atlasWrapSize(); if(!s.w||!s.h) return;
  /* the chart scales WITH the window: growing the pane grows the map by the
     same ratio (relative zoom is preserved) instead of just adding empty sea */
  const m=Math.min(s.w,s.h);
  if(ATLAS.viewBase&&ATLAS.cam.ppm&&m!==ATLAS.viewBase) ATLAS.cam.ppm*=m/ATLAS.viewBase;
  ATLAS.viewBase=m;
  const dpr=window.devicePixelRatio||1;
  const W=Math.round(s.w*dpr),H=Math.round(s.h*dpr);
  if(cv.width!==W||cv.height!==H){cv.width=W;cv.height=H;}
  atlasDraw();
}
/* Tolkien-style parchment chart used as the fog-of-war veil: unexplored land
   is "still on the old maps". Shipped at WebUI/assets/fog-parchment.png. */
const FOG_PARCHMENT=new Image();
FOG_PARCHMENT.onload=()=>{ATLAS.fogTex=null;ATLAS.fogPlainTex=null;if(currentPage==="atlas")atlasDraw();};
FOG_PARCHMENT.src="assets/fog-parchment.png";
/* The realm is a 10.5km disc - unexplored land must stay fully obfuscated all
   the way out to the rim, so the veil DARKENS toward the edge (fading into the
   #080C12 atlas backdrop) instead of thinning to transparency (which used to
   let the map's outer ring show through the fog). source-atop only paints
   where veil pixels exist, so cartography-explored cutouts stay clear - the
   crew's charted paths remain visible right up to the rim. A destination-in
   trim just PAST the rim (over backdrop only) keeps the circular silhouette.
   extentM = world meters from the canvas centre to its edge. */
function atlasVeilFeather(g,w,h,extentM){
  const cx=w/2,cy=h/2,px=m=>m/extentM*(w/2);
  let grad=g.createRadialGradient(cx,cy,px(9300),cx,cy,px(10500));
  grad.addColorStop(0,"rgba(8,12,18,0)");
  grad.addColorStop(1,"rgba(8,12,18,1)");
  g.globalCompositeOperation="source-atop";
  g.fillStyle=grad; g.fillRect(0,0,w,h);
  grad=g.createRadialGradient(cx,cy,px(10500),cx,cy,px(10900));
  grad.addColorStop(0,"rgba(0,0,0,1)");
  grad.addColorStop(1,"rgba(0,0,0,0)");
  g.globalCompositeOperation="destination-in";
  g.fillStyle=grad; g.fillRect(0,0,w,h);
  g.globalCompositeOperation="source-over";
}
/* No-cartography veil: the parchment itself, feathered to the world circle.
   Cached per extent (re-baked if the world's fog extent changes). */
function atlasFogPlainTex(extentM){
  if(ATLAS.fogPlainTex&&ATLAS.fogPlainTexExt===extentM) return ATLAS.fogPlainTex;
  if(!(FOG_PARCHMENT.complete&&FOG_PARCHMENT.naturalWidth)) return null;
  const cnv=document.createElement("canvas");
  cnv.width=cnv.height=1024;
  const g=cnv.getContext("2d");
  g.globalAlpha=.97;
  g.drawImage(FOG_PARCHMENT,0,0,1024,1024);
  g.globalAlpha=1;
  atlasVeilFeather(g,1024,1024,extentM);
  ATLAS.fogPlainTex=cnv; ATLAS.fogPlainTexExt=extentM;
  return cnv;
}
/* Bake the parchment through the fog png's alpha once per save (the png's own
   colour is ignored - it is only the explored/unexplored cutout). Drawing the
   result onto itself compounds the mask's .8 alpha to ~.96 so the veil reads
   as solid vellum with only a faint ghost of the terrain beneath. */
function atlasFogTex(){
  if(!ATLAS.fogImg) return null;
  if(ATLAS.fogTex) return ATLAS.fogTex;
  if(!(FOG_PARCHMENT.complete&&FOG_PARCHMENT.naturalWidth)) return null; // plain veil until it loads
  const cnv=document.createElement("canvas");
  cnv.width=ATLAS.fogImg.width; cnv.height=ATLAS.fogImg.height;
  const g=cnv.getContext("2d");
  g.drawImage(FOG_PARCHMENT,0,0,cnv.width,cnv.height);
  g.globalCompositeOperation="destination-in";
  g.drawImage(ATLAS.fogImg,0,0);
  g.globalCompositeOperation="source-over";
  g.drawImage(cnv,0,0);
  atlasVeilFeather(g,cnv.width,cnv.height,ATLAS.fogExtent);
  ATLAS.fogTex=cnv;
  return cnv;
}
/* Markers are swallowed by the veil: a waypoint the crew hasn't charted stays
   secret. Samples the fog png's alpha at a world position (unexplored = ~.8
   alpha, explored = fully clear). No fog data at all = everything is veiled. */
function atlasFogMaskData(){
  if(!ATLAS.fogImg) return null;
  if(ATLAS.fogMask) return ATLAS.fogMask;
  const cnv=document.createElement("canvas");
  cnv.width=ATLAS.fogImg.width; cnv.height=ATLAS.fogImg.height;
  const g=cnv.getContext("2d",{willReadFrequently:true});
  g.drawImage(ATLAS.fogImg,0,0);
  try{ATLAS.fogMask=g.getImageData(0,0,cnv.width,cnv.height);}catch{return null;}
  return ATLAS.fogMask;
}
/* Toggling the fog wipes it on/off in a concentric ring from the world's
   center (0,0) outward: inside the growing circle shows the NEW state, outside
   keeps the OLD state until the ring passes. Markers ride the same ring. */
function atlasFogWipe(to){
  const dur=3200, maxR=Math.max(ATLAS.mapExtent||10500,10500)*1.02;
  const start=performance.now();
  const w={to,r:0};
  ATLAS.fogWipe=w;
  const step=now=>{
    if(ATLAS.fogWipe!==w) return;                // superseded by a newer toggle
    const p=Math.min(1,(now-start)/dur);
    w.r=(0.5-0.5*Math.cos(Math.PI*p))*maxR;      // ease-in-out - mist drifts, no snap
    if(p<1&&currentPage==="atlas"){atlasDraw();requestAnimationFrame(step);}
    else{ATLAS.fogWipe=null;atlasDraw();}
  };
  requestAnimationFrame(step);
}
function atlasFogEffectiveOn(wx,wz){
  const a=ATLAS.fogWipe;
  if(a) return ((wx*wx+wz*wz)<=a.r*a.r)?a.to:!a.to;
  return ATLAS.layers.fog;
}
function atlasFogHides(wx,wz){
  if(!atlasFogEffectiveOn(wx,wz)) return false;
  if(!ATLAS.fogReady) return true;               // nothing shared - whole realm veiled
  const m=atlasFogMaskData(); if(!m) return true;
  const e=ATLAS.fogExtent;
  const px=Math.round((wx+e)/(2*e)*(m.width-1));
  const py=Math.round((e-wz)/(2*e)*(m.height-1));
  if(px<0||py<0||px>=m.width||py>=m.height) return true;
  return m.data[(py*m.width+px)*4+3]>96;         // veil alpha is ~204 where unexplored
}
function atlasDraw(){
  const cv=$("#atlasCanvas"); if(!cv||!cv.width) return;
  const ctx=cv.getContext("2d");
  const dpr=window.devicePixelRatio||1;
  const s=atlasWrapSize();
  ctx.setTransform(dpr,0,0,dpr,0,0);
  ctx.fillStyle="#080C12"; ctx.fillRect(0,0,s.w,s.h);
  if(!ATLAS.mapReady||!ATLAS.cam.ppm) return;
  const c=ATLAS.cam;
  ctx.imageSmoothingEnabled=true;
  const sz=2*ATLAS.mapExtent*c.ppm;
  ctx.drawImage(ATLAS.mapImg,w2sX(-ATLAS.mapExtent),w2sY(ATLAS.mapExtent),sz,sz);
  /* The veil is a Tolkien-style parchment sea chart, not a black shroud. No
     shared cartography data = nothing explored, so the whole realm stays
     parchment - and every marker underneath it stays secret (atlasFogHides). */
  const wipe=ATLAS.fogWipe;
  if(ATLAS.layers.fog||wipe){
    /* During a wipe the veil is drawn to a scratch canvas, then a feathered
       radial gradient at the ring (destination-in when appearing, -out when
       vanishing) melts its edge so the wave rolls in like mist, not a knife. */
    let g=ctx;
    if(wipe){
      const sc=ATLAS.fogWipeCnv||(ATLAS.fogWipeCnv=document.createElement("canvas"));
      if(sc.width!==cv.width||sc.height!==cv.height){sc.width=cv.width;sc.height=cv.height;}
      g=sc.getContext("2d");
      g.setTransform(dpr,0,0,dpr,0,0);
      g.clearRect(0,0,s.w,s.h);
    }
    const pReady=FOG_PARCHMENT.complete&&FOG_PARCHMENT.naturalWidth;
    if(ATLAS.fogReady){
      const fsz=2*ATLAS.fogExtent*c.ppm;
      g.drawImage(atlasFogTex()||ATLAS.fogImg,w2sX(-ATLAS.fogExtent),w2sY(ATLAS.fogExtent),fsz,fsz);
    }else{
      const ext=Math.max(ATLAS.mapExtent,ATLAS.fogExtent),fsz=2*ext*c.ppm;
      const plain=pReady?atlasFogPlainTex(ext):null;
      if(plain){
        g.drawImage(plain,w2sX(-ext),w2sY(ext),fsz,fsz);
        g.fillStyle="rgba(58,44,24,.85)";              // ink on vellum
      }else{
        g.fillStyle="rgba(6,7,10,.8)"; g.fillRect(w2sX(-ext),w2sY(ext),fsz,fsz);
        g.fillStyle="rgba(226,217,196,.45)";
      }
      if(!wipe){
        g.font="11px 'IBM Plex Mono',monospace"; g.textAlign="center"; g.textBaseline="middle";
        g.fillText("ᚾ "+T("atlas.fog.unexplored"),s.w/2,s.h/2);
        g.textAlign="start";
      }
    }
    if(wipe){
      const wcx=w2sX(0),wcy=w2sY(0);
      const rPx=Math.max(wipe.r*c.ppm,0.01);
      const fPx=Math.max(28,900*c.ppm);              // ~900 m soft edge, never razor-thin on screen
      const grad=g.createRadialGradient(wcx,wcy,Math.max(0,rPx-fPx),wcx,wcy,rPx);
      grad.addColorStop(0,"rgba(0,0,0,1)");
      grad.addColorStop(1,"rgba(0,0,0,0)");
      g.globalCompositeOperation=wipe.to?"destination-in":"destination-out";
      g.fillStyle=grad; g.fillRect(0,0,s.w,s.h);
      g.globalCompositeOperation="source-over";
      ctx.drawImage(ATLAS.fogWipeCnv,0,0,s.w,s.h);
    }
  }
  const info=ATLAS.info;
  const showLbl=c.ppm>0.045;
  ctx.font="10px 'IBM Plex Mono',monospace"; ctx.textBaseline="middle";
  if(info&&info.hasDb){
    if(ATLAS.layers.builds)(info.builds||[]).forEach(b=>{
      if(atlasFogHides(b.x,b.z)) return;
      const x=w2sX(b.x),y=w2sY(b.z),r=Math.max(4,b.radius*c.ppm);
      ctx.strokeStyle="rgba(198,164,110,.75)"; ctx.fillStyle="rgba(198,164,110,.12)";
      ctx.lineWidth=1; ctx.beginPath(); ctx.arc(x,y,r,0,Math.PI*2); ctx.fill(); ctx.stroke();
    });
    if(ATLAS.layers.pins)(info.pins||[]).forEach(p=>{
      if(atlasFogHides(p.x,p.z)) return;
      const x=w2sX(p.x),y=w2sY(p.z);
      ctx.fillStyle="rgba(226,217,196,.85)";
      ctx.beginPath(); ctx.arc(x,y,2.5,0,Math.PI*2); ctx.fill();
      if(showLbl&&p.name){ctx.fillStyle="rgba(226,217,196,.6)";ctx.fillText(p.name,x+6,y);}
    });
    if(ATLAS.layers.portals)(info.portals||[]).forEach(p=>{
      if(atlasFogHides(p.x,p.z)) return;
      const x=w2sX(p.x),y=w2sY(p.z);
      ctx.fillStyle="#63B3C4"; ctx.strokeStyle="rgba(8,12,18,.9)"; ctx.lineWidth=1;
      ctx.beginPath(); ctx.moveTo(x,y-5); ctx.lineTo(x+4,y); ctx.lineTo(x,y+5); ctx.lineTo(x-4,y); ctx.closePath();
      ctx.fill(); ctx.stroke();
      if(showLbl&&p.tag){ctx.fillStyle="rgba(99,179,196,.9)";ctx.fillText(p.tag,x+7,y);}
    });
    if(ATLAS.layers.pois)(info.pois||[]).forEach(l=>{
      if(atlasFogHides(l.x,l.z)) return;
      const x=w2sX(l.x),y=w2sY(l.z);
      ctx.fillStyle="#FF7A1A"; ctx.strokeStyle="rgba(8,12,18,.9)"; ctx.lineWidth=1;
      ctx.beginPath(); ctx.arc(x,y,4,0,Math.PI*2); ctx.fill(); ctx.stroke();
      if(showLbl){ctx.fillStyle="rgba(255,164,92,.95)";ctx.fillText(l.label,x+8,y);}
    });
    if(info.eventName&&!atlasFogHides(info.eventX,info.eventZ)){
      const x=w2sX(info.eventX),y=w2sY(info.eventZ);
      ctx.strokeStyle="rgba(196,74,58,.9)"; ctx.lineWidth=2;
      ctx.beginPath(); ctx.arc(x,y,9,0,Math.PI*2); ctx.stroke();
    }
  }
  const z=$("#atlasZoomLbl");
  if(z) z.textContent=T("atlas.zoom.scale",{metres:Math.round(1/c.ppm)});
}
/* pan / zoom / coord readout */
(function(){
  const cv=$("#atlasCanvas"); if(!cv) return;
  cv.addEventListener("pointerdown",e=>{
    try{cv.setPointerCapture(e.pointerId);}catch(_){}
    ATLAS.drag={x:e.clientX,y:e.clientY,cx:ATLAS.cam.cx,cz:ATLAS.cam.cz};
    cv.classList.add("dragging");
  });
  cv.addEventListener("pointermove",e=>{
    const rect=cv.getBoundingClientRect();
    const mx=e.clientX-rect.left,my=e.clientY-rect.top;
    if(ATLAS.mapReady&&ATLAS.cam.ppm){
      const wx=ATLAS.cam.cx+(mx-rect.width/2)/ATLAS.cam.ppm;
      const wz=ATLAS.cam.cz-(my-rect.height/2)/ATLAS.cam.ppm;
      const co=$("#atlasCoord"); if(co)co.textContent=Math.round(wx)+", "+Math.round(wz);
    }
    if(!ATLAS.drag) return;
    ATLAS.cam.cx=ATLAS.drag.cx-(e.clientX-ATLAS.drag.x)/ATLAS.cam.ppm;
    ATLAS.cam.cz=ATLAS.drag.cz+(e.clientY-ATLAS.drag.y)/ATLAS.cam.ppm;
    atlasDraw();
  });
  ["pointerup","pointercancel"].forEach(ev=>cv.addEventListener(ev,()=>{
    ATLAS.drag=null; cv.classList.remove("dragging");
  }));
  cv.addEventListener("wheel",e=>{
    e.preventDefault();
    if(!ATLAS.cam.ppm) return;
    const rect=cv.getBoundingClientRect();
    const mx=e.clientX-rect.left,my=e.clientY-rect.top;
    const wx=ATLAS.cam.cx+(mx-rect.width/2)/ATLAS.cam.ppm;
    const wz=ATLAS.cam.cz-(my-rect.height/2)/ATLAS.cam.ppm;
    const f=e.deltaY<0?1.18:1/1.18;
    ATLAS.cam.ppm=Math.min(Math.max(ATLAS.cam.ppm*f,(atlasFitPpm()||0.01)*0.5),8);
    ATLAS.cam.cx=wx-(mx-rect.width/2)/ATLAS.cam.ppm;   // keep the cursor's world point fixed
    ATLAS.cam.cz=wz+(my-rect.height/2)/ATLAS.cam.ppm;
    atlasDraw();
  },{passive:false});
  cv.addEventListener("dblclick",()=>{atlasFit();atlasDraw();});
  try{new ResizeObserver(()=>{if(currentPage==="atlas")atlasResize();}).observe($("#atlasWrap"));}catch{}
})();
/* layer chips - fog with no shared data still veils the whole realm (honest
   "nothing explored" state), so the toggle always works; just explain the dark */
$$(".lchip").forEach(ch=>ch.addEventListener("click",()=>{
  const layer=ch.dataset.layer;
  const turningOn=!ATLAS.layers[layer];
  if(layer==="fog"&&turningOn&&Native.available&&(!ATLAS.info||!ATLAS.info.hasSharedMap)){
    toast("ᚾ "+T("atlas.layer.shared_map.none.toast"));
  }
  if(layer==="pins"&&turningOn&&Native.available&&ATLAS.info&&ATLAS.info.hasDb&&!(ATLAS.info.pins||[]).length){
    toast("ᛘ "+T("atlas.layer.pins.none.toast"));
    return;
  }
  ATLAS.layers[layer]=turningOn;
  ch.classList.toggle("on",turningOn);
  if(layer==="fog"&&ATLAS.mapReady){atlasFogWipe(turningOn);return;}
  atlasDraw();
}));
$("#atlasRecenter").addEventListener("click",()=>{atlasFit();atlasDraw();});
$("#atlasRedraw").addEventListener("click",()=>{
  if(!Native.available){toast("ᛞ "+T("atlas.redraw.preview.toast"));return;}
  atlasRenderMap(true);
});
$("#atlasWaypoints").addEventListener("click",e=>{
  const row=e.target.closest(".wp"); if(!row) return;
  const w=($("#atlasWaypoints")._rows||[])[Number(row.dataset.i)];
  if(!w) return;
  ATLAS.cam.cx=w.x; ATLAS.cam.cz=w.z;
  ATLAS.cam.ppm=Math.max(ATLAS.cam.ppm,(atlasFitPpm()||0.03)*6);
  atlasDraw();
});
/* mock fixture: a synthetic island so the preview exercises pan/zoom/layers */
function atlasMock(){
  if(ATLAS.mapReady){renderAtlasSide();atlasDraw();return;}
  const size=1024,off=document.createElement("canvas");
  off.width=size; off.height=size;
  const g=off.getContext("2d");
  g.fillStyle="#0E2A4A"; g.fillRect(0,0,size,size);
  const blob=(x,y,r,col)=>{const gr=g.createRadialGradient(x,y,0,x,y,r);gr.addColorStop(0,col);gr.addColorStop(1,"rgba(0,0,0,0)");g.fillStyle=gr;g.beginPath();g.arc(x,y,r,0,Math.PI*2);g.fill();};
  blob(512,512,300,"#5E8A3D"); blob(400,430,170,"#34502F"); blob(640,420,130,"#5E5440");
  blob(520,330,110,"#DADEE2"); blob(620,600,150,"#BDA95F"); blob(390,610,120,"#69607A");
  blob(512,830,140,"#80352A"); blob(512,190,140,"#C6D2DA");
  g.globalCompositeOperation="destination-in";
  g.fillStyle="#000"; // opaque mask - a leftover gradient here erased the island
  g.beginPath(); g.arc(512,512,470,0,Math.PI*2); g.fill();
  g.globalCompositeOperation="destination-over";
  g.fillStyle="#080C12"; g.fillRect(0,0,size,size);
  const img=new Image();
  img.onload=()=>{
    ATLAS.world="Final Sunset"; ATLAS.mapImg=img; ATLAS.mapReady=true; ATLAS.mapExtent=10500;
    ATLAS.info={hasDb:true,day:333,netTime:333*1800+912,savedAgeSeconds:245,_rxAt:Date.now(),
      hasSharedMap:false,mapTables:2,exploredPercent:0,eventName:"",
      portals:[{tag:"Farm",x:88,z:-353},{tag:"Bonemass",x:-2450,z:1180},{tag:"Bjorngard",x:1420,z:2210}],
      pois:[{label:"Sacrificial Stones",x:20,z:-18},{label:"Eikthyr",x:610,z:840},{label:"The Elder",x:-1830,z:-960}],
      builds:[{x:88,z:-353,pieces:2854,radius:289},{x:-2410,z:1130,pieces:412,radius:96}],
      pins:[]};
    $("#atlasWorldName").textContent=ATLAS.world;
    atlasMsg(null); atlasFit(); atlasDraw(); renderAtlasSide();
  };
  img.src=off.toDataURL("image/png");
}

/* ============ NATIVE WIRING (WebView2 host drives real data) ============ */
if(Native.available){

  /* --- event subscriptions (registered before boot so nothing is missed) --- */
  Native.on("server.status",d=>{if(isActiveProfile(d?.profile))applyState(d);});
  Native.on("server.worldSaved",d=>{
    if(!isActiveProfile(d?.profile)) return;
    const ms=Math.round(Number(d?.seconds)||0); // bridge param is named 'seconds' but carries milliseconds
    toast("ᛉ "+T("hearth.saves.saved.toast",{ms}));
    clearCondition("saveFailed");   // a good write answers the failed one
    $("#lastSave").textContent=T("hearth.saves.last.value",{clock:clock(),ms});
    S.lastSaveAt=new Date();
    S.saveDur.push(ms); if(S.saveDur.length>12)S.saveDur.shift();
    renderSaveAvg();
    renderSaveBars();
    S.saveSec=S.saveInterval;
    if(currentPage==="atlas") atlasRefreshInfo(); // fresh .db on disk = fresh atlas facts/fog
  });
  /* A save that FAILED. Everything played since the last good one is only in memory,
     so this is loud and it stays in the log. */
  Native.on("server.worldSaveFailed",d=>{
    if(!isActiveProfile(d?.profile)) return;
    const ms=Math.round(Number(d?.durationMs)||0);
    conditionSaveFailed(ms);
    toast("ᚦ "+T("hearth.saves.failed.toast",{ms}));
    logLine("err","[BakaLoader] the server reported a FAILED world save after "+ms+
      "ms. Everything since the last good save is only in memory. Check free disk space and "+
      "whether anything else has the world files open.");
  });
  /* The world on disk is still pre-1.0. The next save converts it and there is no way back. */
  Native.on("server.legacyWorld",d=>{
    if(!isActiveProfile(d?.profile)) return;
    toast("ᛉ "+T("world.format.legacy.toast"));
    logLine("warn","[BakaLoader] "+(d?.world||"this world")+" is in the old format. The next save "+
      "converts it to the 1.0 format and keeps the old files as a backup. Older servers will not "+
      "be able to load it afterwards.");
  });
  /* The version banner: the only place the real game and network versions come from. */
  Native.on("server.version",d=>{
    if(!isActiveProfile(d?.profile)) return;
    S.gameVersion=d?.version||null;
    S.networkVersion=d?.network||null;
    renderHearthVersion();
    logLine("ok","[BakaLoader] server is running Valheim "+(d?.version||"?")+
      (d?.network?" (network version "+d.network+")":""));
  });
  /* A start BakaLoader held back because nobody was there to answer for it. */
  Native.on("server.launchHold",d=>{
    if(!isActiveProfile(d?.profile)) return;
    setLaunchHold(d);
    /* The same sentence the condition bar and the modal put, built from the facts in
       the event rather than from the host's prose. One builder, one wording. */
    toast("ᛊ "+T("guard.held.toast",{reason:guardBody(d||{})}));
    logLine("warn","[BakaLoader] start held: "+(d?.message||"the launch guard did not clear it"));
  });
  Native.on("server.launchHoldCleared",d=>{
    if(isActiveProfile(d?.profile)) clearLaunchHold();
  });
  /* A start that ended before the server came up - a bad exe path, an unreachable save
     folder, a check that could not run. The state event that follows re-enables Kindle. */
  Native.on("server.launchFailed",d=>{
    if(!isActiveProfile(d?.profile)) return;
    toast("ᚦ "+T("hearth.launch.failed.toast",{reason:d?.message||T("hearth.launch.failed.nodetail")}));
    logLine("err","[BakaLoader] the server was not started: "+(d?.message||"unknown error"));
  });
  /* The pre-update world snapshot finished (or could not be made, which stops the start). */
  Native.on("server.worldsBackedUp",d=>{
    if(!isActiveProfile(d?.profile)) return;
    if(d?.ok){
      clearCondition("backupFailed");
      toast("ᛝ "+T("srvupd.worlds_copy.done.toast",{count:d.count,size:fmtBytes(d.bytes)}));
      logLine("ok","[BakaLoader] copied "+d.count+" world(s) aside before starting ("+fmtBytes(d.bytes)+")");
      (d.skipped||[]).forEach(s=>logLine("warn","[BakaLoader] not copied aside: "+s));
    }else{
      conditionBackupFailed(d&&d.error?String(d.error):"");
      toast("ᚦ "+T("srvupd.worlds_copy.failed.toast"));
      logLine("err","[BakaLoader] the pre-update world copy failed, so the server was not started: "+(d?.error||"unknown error"));
    }
  });
  Native.on("atlas.renderProgress",d=>{
    if(currentPage==="atlas"&&d&&d.world===ATLAS.world&&ATLAS.rendering)
      atlasMsg(ATLAS_MSG.chartingPctId,{percent:d.pct});
  });
  Native.on("server.inviteCode",d=>{
    if(!d?.code||!isActiveProfile(d?.profile)) return;
    S.invite=d.code; renderNet();
    toast("ᛟ "+T("hearth.net.invite.toast",{code:d.code}));
    logLine("info","[BakaLoader] crossplay invite code: "+d.code);
  });
  Native.on("server.crashed",d=>{
    // Crashes always toast, even for background servers - name the culprit.
    /* Whose server crashed is a whole second sentence rather than a tail on the first:
       a background realm names itself, the one on screen does not. */
    toast("ᚦ "+(d?.profile&&!isActiveProfile(d.profile)
      ?T("hearth.crashed.named.toast",{profile:d.profile})
      :T("hearth.crashed.toast")));
    if(isActiveProfile(d?.profile)){
      S.crashed=true;
      conditionCrashed();
      renderHearthNative();
      logLine("err","[BakaLoader] server process crashed");
    }
  });
  /* The host posts this when a check has found a newer BakaLoader release. The check runs
     at launch and again every few hours for as long as the app is open, so this can arrive
     at any point in a session. The standing row and every other surface follow it. */
  Native.on("app.updateAvailable",d=>{
    conditionAppUpdate(d&&d.version);
    refreshAppUpdateInfo();
  });
  /* The server update, from the service that runs it. Progress is a bar, not toasts. */
  Native.on("server.updateProgress",onUpdateProgress);
  Native.on("server.updateDone",onUpdateDone);
  Native.on("server.updateFailed",onUpdateFailed);
  /* The bulk mod update streams per-mod progress the same way. The bar and the row states
     follow it; the run's own completion (the RPC returning) clears them. */
  Native.on("mods.updateProgress",onModUpdateProgress);
  /* BepInEx: the row's fact, pushed after every write the host makes, and the progress
     of that write. Neither is filtered by profile, because the loader belongs to the
     install rather than to any one server on it. */
  Native.on("bepinex.changed",d=>{S.bepinex=d||null;renderMods();});
  Native.on("bepinex.progress",onBepInExProgress);
  /* The chip. A tick with no id is the countdown ending and says nothing. */
  Native.on("server.countdown",d=>{
    if(!isActiveProfile(d?.profile)) return;
    const said=countdownChipWords(d);
    if(said) toast("ᚨ "+said);
  });
  Native.on("player.updated",p=>{
    if(!p) return;
    if(!isActiveProfile(p.serverKey)) return; // another server's viking
    const i=S.players.findIndex(x=>x.key===p.key);
    if(i>=0) S.players[i]=p; else S.players.push(p);
    renderPlayers();
    if(currentPage==="vikings") refreshJournal();
  });
  Native.on("ip.external",d=>{S.extIp=d?.ip??null;renderNet();});
  Native.on("ip.internal",d=>{S.intIp=d?.ip??null;renderNet();});
  Native.on("profiles.changed",()=>{refreshServers();});
  Native.on("servers.changed",d=>{if(Array.isArray(d)){S.servers=d;renderServerChips();}});
  /* Skald hall keeps itself fresh while it is the page on screen */
  Native.on("server.status",()=>{if(currentPage==="skald")skaldRefresh();});
  Native.on("server.playerDied",d=>{
    if(currentPage==="skald"&&isActiveProfile(d?.profile))skaldRefresh();
    if(currentPage==="vikings"&&isActiveProfile(d?.profile))refreshJournal(true);
  });
  Native.on("player.updated",p=>{
    if(currentPage==="skald"&&isActiveProfile(p?.serverKey))skaldRefresh();
  });
  Native.on("log.app",d=>{if(d?.line!=null)logRaw(d.line);});
  Native.on("log.server",d=>{
    if(d?.line==null||!isActiveProfile(d?.profile)) return;
    logRaw(d.line);
    parseNetLine(d.line); // net telemetry rides the server log
  });

  /* --- boot sequence --- */
  (async function bootNative(){
    /* placeholders until real data lands */
    $("#lastSave").textContent="-";
    $("#saveCountdown").textContent="-:-";
    $("#tickVal").textContent="-";
    $("#sbMods").textContent=T("side.mods.count",{count:"-"});
    /* the mock save chart from index.html is design filler, not a measurement */
    $("#saveBars").innerHTML="";
    renderSaveAvg();
    renderSaveBars();
    tIn.placeholder=T("saga.term.native.placeholder");
    /* Forge Load: real CPU/RAM sampled from the tracked server process every 3s.
       Quiet Native.call (no rpc() wrapper) so a hiccup never toast-spams the poll. */
    for(let i=0;i<N;i++){cpu.push(0);ram.push(0);}
    drawLine($("#cpuLine"),cpu,0,60); drawLine($("#ramLine"),ram,0,100);
    $("#cpuVal").textContent="-"; $("#ramVal").textContent="-";
    async function pollMetrics(){
      const m=await Native.call("metrics.get",{}).catch(()=>null);
      cpu.shift(); ram.shift();
      if(m&&m.running){
        cpu.push(Math.min(100,Math.max(0,m.cpu||0)));
        ram.push((m.ramBytes||0)/1024/1024/1024*10); /* GB x10 - same scale as the value line */
        $("#cpuVal").textContent=Math.round(cpu[N-1])+"%";
        $("#ramVal").textContent=(ram[N-1]/10).toFixed(1)+" GB";
      }else{
        cpu.push(0); ram.push(0);
        $("#cpuVal").textContent="-"; $("#ramVal").textContent="-";
      }
      /* headroom scales up when a heavy modpack pushes past the base range */
      drawLine($("#cpuLine"),cpu,0,Math.max(60,...cpu));
      drawLine($("#ramLine"),ram,0,Math.max(100,...ram));
    }
    pollMetrics(); setInterval(pollMetrics,3000);
    /* process priority is not pref-backed: always AboveNormal in C# */
    const prio=$("#prioSel");
    prio.value="AboveNormal"; prio.disabled=true;
    prio.title=T("world.priority.fixed.title");
    prio.insertAdjacentHTML("afterend",`<div class="fieldnote">${esc(T("world.priority.fixed.note"))}</div>`);

    const info=await rpc("app.info");
    if(info!==FAIL&&info){
      /* Written through applyAppVersion so the sidebar keeps whichever of its two states
         it is in: a bare textContent write here would drop the ember spans. */
      applyAppVersion(info.version||"");
    }
    await refreshAppUpdateInfo();   // quiet: lights the sidebar and the Hearth pill, or neither
    const profs=await rpc("profiles.list");
    let profName="Default";
    if(profs!==FAIL&&Array.isArray(profs)&&profs.length){
      /* prefer the splash-assigned startup profile (app.info), else Default, else first */
      const startProf=info!==FAIL&&info&&info.profile
        ?profs.find(p=>p.ProfileName===info.profile):null;
      profName=(startProf||profs.find(p=>p.ProfileName==="Default")||profs[0]).ProfileName;
    }
    const prefs=await rpc("profiles.get",{name:profName});
    if(prefs!==FAIL&&prefs){
      S.prefs=prefs; S.profileName=prefs.ProfileName;
      S.saveInterval=prefs.SaveInterval??600;
    }
    const st=await rpc("server.state");
    if(st!==FAIL) applyState(st);
    refreshUpdateInfo();   // quiet: fills the dashboard pill and gates the palette entry
    refreshServers(); // populate the multi-server chip strip
    const caps=await rpc("caps.get");
    if(caps!==FAIL&&caps) S.caps=caps;
    renderCaps();
    const ips=await rpc("ip.get");
    if(ips!==FAIL&&ips){S.extIp=ips.external;S.intIp=ips.internal;}
    rpc("ip.refresh").then(r=>{
      if(r!==FAIL&&r){S.extIp=r.external;S.intIp=r.internal;renderNet();}
    });
    const buf=await rpc("logs.appBuffer");
    if(buf!==FAIL&&Array.isArray(buf)) buf.forEach(logRaw);
    // a server may already be mid-session (auto-start / adopted) - replay its tail too
    const sbuf=await rpc("logs.serverBuffer");
    if(sbuf!==FAIL&&Array.isArray(sbuf)&&sbuf.length){
      logDivider(T("saga.divider.earlier"));
      sbuf.slice(-200).forEach(logRaw);
    }
    await refreshPlayers();
    renderAllFromPrefs();
    renderMods();
    renderHearthNative();
    await initUpkeep();
    /* Asked once here; every change after this arrives on bepinex.changed. */
    await refreshBepInEx();

    /* first-launch guided setup: only when never completed AND the exe isn't already valid */
    const setup=await rpc("setup.status");
    if(setup!==FAIL&&setup&&!setup.setupCompleted&&!setup.exeValid) wizardOpen(setup);

    /* 1s heartbeat: uptime + save countdown (ticks only while Running) */
    setInterval(()=>{
      if(S.state?.status!=="Running") return;
      renderHearthNative();
      if(S.saveSec!=null){
        S.saveSec=Math.max(0,S.saveSec-1);
        $("#saveCountdown").textContent=pad(Math.floor(S.saveSec/60))+":"+pad(S.saveSec%60);
      }
    },1000);
    /* 500ms player poll - only while the vikings page is active */
    setInterval(()=>{if(currentPage==="vikings")refreshPlayers();},500);
  })();
}

/* ============ MOCK DRIVERS (browser preview only - native host drives real data via events) ============ */
if(!Native.available){

  /* upkeep card preview values (native fills these from userprefs.get) */
  $("#blVersion").textContent=$("#sideVer").textContent; // mirror the shipped version string
  /* the terminology switch is wired in initUpkeep, which is native-only; wire it here
     too so the browser preview can be walked in both states */
  $("#tPlainTerms").addEventListener("click",()=>{PLAIN=!swOn("tPlainTerms");applyTerms();});
  /* the second-site switch is wired in initUpkeep too, which is native-only; it goes
     through the same one function, so the preview shows the labels the app shows */
  $("#tUseHexium")?.addEventListener("click",()=>setHexiumSource(swOn("tUseHexium")));
  setT("tHeraldAddr",true); // herald preview mirrors the C# default (address shared, rest off)

  /* The loader row, preview side. An install BakaLoader looks after is the state a
     host sees most, so it is the one the browser preview opens on; the walk drives
     the other three through BakaPreview.bepinexStatus. */
  S.bepinex={installed:true,baseFolder:"D:\\steamlibrary\\Valheim dedicated server",
    maintainedByBakaLoader:true,packVersion:"5.4.2350",coreFileVersion:"5.4.23.5",
    package:"denikson-BepInExPack_Valheim",source:"thunderstore",
    installedUtc:null,wrongLocationFolder:null,
    sharingProfiles:["Final Sunset","Midgard Test"],runningProfiles:[],
    maintained:true,maintenanceAsked:true,updateWaiting:null,busy:false};
  renderBepInExRow();

  /* multi-server chip strip preview */
  S.servers=[
    {name:"Final Sunset",status:"Running",running:true,playersOnline:2,active:true},
    {name:"Midgard Test",status:"Stopped",running:false,playersOnline:0,active:false},
  ];
  renderServerChips();

  /* BakaLoader's own update, preview side. The waiting-release state is the one worth
     looking at, so it is the default; index.html#uptodate walks the quiet one, where
     none of these surfaces show anything at all. */
  APP_UPD=location.hash==="#uptodate"
    ?{installedVersion:"1.2.0",latestVersion:null,updateAvailable:false,releaseUrl:null,
      autoUpdateOnRestart:false,checkEnabled:true,anyServerRunning:true}
    :{installedVersion:"1.2.0",latestVersion:"1.2.1",updateAvailable:true,
      releaseUrl:"https://github.com/RyanDMcAfee/ValheimBakaLoader/releases/latest",
      autoUpdateOnRestart:false,checkEnabled:true,anyServerRunning:true};
  renderAppUpdatePill();
  renderSideVer();
  if(APP_UPD.updateAvailable) conditionAppUpdate(APP_UPD.latestVersion);

  /* first-launch wizard preview: open index.html#wizard to walk the panes */
  if(location.hash==="#wizard"){
    setTimeout(()=>wizardOpen({
      setupCompleted:false,exeValid:false,saveValid:true,
      defaultExePath:"%ProgramFiles(x86)%\\Steam\\steamapps\\common\\Valheim dedicated server\\valheim_server.exe",
      defaultSavePath:"%USERPROFILE%\\AppData\\LocalLow\\IronGate\\Valheim",
    }),150);
  }

  /* Waiting-update preview: open index.html#update, #update=steamLibrary or
     #update=unknown to walk the three install kinds without a native host. */
  if(location.hash.indexOf("#update")===0){
    const kind=location.hash.indexOf("=")>0?location.hash.split("=")[1]:"standalone";
    S.update=Object.assign({},S.update,{
      installKind:kind,updatePending:true,pendingBytes:848*1048576,
      buildId:"19503481",targetBuildId:"19640213",
      canUpdate:kind!=="unknown",running:false,
      reason:kind==="unknown"?"No Steam manifest was found above the server executable.":"",
    });
    setLaunchHold({outcome:"updatePending",pendingBytes:S.update.pendingBytes,profile:null});
    renderUpdatePill();
  }

  /* Preview roster: real player DTOs through the real renderer, so sorting, the
     folding columns and the row menu are all exercised offline. One Steam id, one
     crossplay Xbox id and one PlayStation id keep the Platform column honest. */
  const ago=min=>new Date(Date.now()-min*60000).toISOString();
  S.players=[
    {key:"Steam:76561198012345678",platform:"Steam",PlayerId:"76561198012345678",PlayerName:"Smithix",
     displayName:"Smithix",status:"Online",lastStatusChange:ago(272),position:"3959, -1361, 35"},
    {key:"Xbox:Xbox_2814639011776000",platform:"Xbox",PlayerId:"Xbox_2814639011776000",PlayerName:"Van Hoenhiem",
     displayName:"Van Hoenhiem",status:"Online",lastStatusChange:ago(126),position:"-212, 887, 41"},
    {key:"PlayStation:PlayStation_5001234567",platform:"PlayStation",PlayerId:"PlayStation_5001234567",PlayerName:"Ragnhild",
     displayName:"Ragnhild",status:"Offline",lastStatusChange:ago(185)},
    {key:"Steam:76561198087654321",platform:"Steam",PlayerId:"76561198087654321",PlayerName:"Bjornulf",
     displayName:"Bjornulf",status:"Offline",lastStatusChange:ago(2954)},
  ];
  S.journal={
    "Steam:76561198012345678":{playSec:432600,deaths:23,sessions:64},
    "Xbox:Xbox_2814639011776000":{playSec:301200,deaths:11,sessions:48},
    "PlayStation:PlayStation_5001234567":{playSec:122400,deaths:19,sessions:31},
    "Steam:76561198087654321":{playSec:56200,deaths:8,sessions:28},
  };
  renderPlayers();
  S.mods=[
    /* The second site is ahead of both this install and Thunderstore, so the row carries
       the "newer on Hexium" mark and the menu offers that build. */
    {ModName:"WorldEditCommands",Author:"JereKuusela",FullName:"JereKuusela-WorldEditCommands",InstalledVersion:"1.65.0",LatestVersion:"1.66.0",UpdateAvailable:true,
     hexiumLatest:"1.67.0",hexiumNewer:true,hexiumUrl:"https://valheim.hexium.gg/mods/JereKuusela/WorldEditCommands"},
    /* A copy the host took from Hexium: chipped, out of Update all whatever Thunderstore
       holds, and offered the way back as its own deliberate action. */
    {ModName:"ExtraSlots",Author:"shudnal",FullName:"shudnal-ExtraSlots",InstalledVersion:"1.0.20",LatestVersion:"1.0.22",UpdateAvailable:false,
     installedSource:"hexium",thunderstoreNewer:true,
     hexiumLatest:"1.0.20",hexiumNewer:false,hexiumUrl:"https://valheim.hexium.gg/mods/shudnal/ExtraSlots"},
    /* Carried by both sites at the same version: a page to open, and no mark at all. */
    {ModName:"EpicLoot",Author:"RandyKnapp",FullName:"RandyKnapp-EpicLoot",InstalledVersion:"0.11.3",LatestVersion:"0.11.3",
     hexiumLatest:"0.11.3",hexiumNewer:false,hexiumUrl:"https://valheim.hexium.gg/mods/RandyKnapp/EpicLoot"},
    /* A mod the package list came back without: pulled by its author, or taken down. The
       row says so on hover of the Latest cell, offers no update, and nothing is removed
       or rolled back. The next scan that finds it again clears the note. */
    {ModName:"QuietTorches",Author:"Skogsvandrare",FullName:"Skogsvandrare-QuietTorches",
     InstalledVersion:"1.4.0",LatestVersion:null,UpdateAvailable:false,notListed:true},
    {ModName:"ComfyMods-Gizmo",Author:"ComfyMods",FullName:"ComfyMods-Gizmo",InstalledVersion:"1.15.0",LatestVersion:"1.15.0"},
    {ModName:"PlantEverything",Author:"Advize",FullName:"Advize-PlantEverything",InstalledVersion:"1.18.2",LatestVersion:"1.18.2"},
    {ModName:"SearsCatalog",Author:"ComfyMods",FullName:"ComfyMods-SearsCatalog",InstalledVersion:"1.4.0",LatestVersion:"1.4.0"},
    {ModName:"PlanBuild",Author:"MathiasDecrock",FullName:"MathiasDecrock-PlanBuild",InstalledVersion:"0.16.4",LatestVersion:"0.16.4"},
    {ModName:"BakaLoaderSpawnHelper",Author:"BakaLoader",FullName:"BakaLoader-BakaLoaderSpawnHelper",InstalledVersion:"1.1.0",LatestVersion:"-",Bundled:true},
    {ModName:"Rcon_Commands",Author:"JereKuusela",FullName:"JereKuusela-Rcon_Commands",InstalledVersion:"1.12.0",LatestVersion:"1.12.0"},
    {ModName:"ValheimOptimizer",Author:"Dreous",FullName:"Dreous-ValheimOptimizer",InstalledVersion:"1.2.1",LatestVersion:"1.2.1"},
  ];
  /* A scan is what pairs a plugin with its Thunderstore page, so the preview carries
     the same three fields the real scan adds. The bundled helper ships inside
     BakaLoader and has no page, which is the row that shows the menu item greyed. */
  /* The scan also carries the "possibly outdated" hint: the game's last update date (one
     value for every row) against each mod's newest release date. One row reads Yes (its
     release predates the game update), one reads blank (released after), and one is blank
     because its release date is unknown. */
  const demoGameUpdated="2026-09-01T00:00:00Z";
  const demoModDates={
    "JereKuusela-WorldEditCommands":"2026-06-15T00:00:00Z", // before the game update -> Yes
    "shudnal-ExtraSlots":"2026-09-08T00:00:00Z",            // after the game update -> blank
    "RandyKnapp-EpicLoot":null,                             // release date unknown -> blank
  };
  S.mods.forEach(m=>{
    const on=!m.Bundled;
    m.thunderstoreNamespace=on?m.Author:null;
    m.thunderstoreName=on?m.ModName:null;
    m.thunderstoreUrl=on?("https://thunderstore.io/c/valheim/p/"+m.Author+"/"+m.ModName+"/"):null;
    const md=(m.FullName in demoModDates)?demoModDates[m.FullName]:"2026-09-10T00:00:00Z";
    m.gameUpdatedUtc=on?demoGameUpdated:null;
    m.modUpdatedUtc=on?md:null;
    m.possiblyOutdated=!!(m.modUpdatedUtc&&m.gameUpdatedUtc&&new Date(m.modUpdatedUtc)<new Date(m.gameUpdatedUtc));
  });
  S.modsScanned=true; S.lastScan="21:38";
  /* The header line reads when the package list was read and which address answered, so
     the preview carries both the way a real scan hands them back. */
  S.modIndexAt="21:38"; S.modIndexSource="listing-index";
  renderMods();
  /* The row menu is host-only in the app, so the preview gets its own opener; it calls
     the same builder, so what a screenshot shows is what the app shows. */
  $("#modTable").addEventListener("contextmenu",e=>{
    const tr=e.target.closest("tr[data-key]"); if(!tr) return;
    e.preventDefault();
    const mod=modByKey(tr.dataset.key); if(!mod) return;
    const x=e.clientX,y=e.clientY;
    const again=()=>ctxOpen(x,y,mod.FullName,modRowItems(mod),again);
    again();
  });
  /* The preview's own world, put INTO the mock list rather than written over the top
     of the field: a hand written option list dropped the New world entry off the end
     and left the names the field is holding saying something other than the names on
     screen, on the very first frame, before a walk had driven anything.
     The options are laid down here rather than by renderWorldSelect because app.js is
     still being evaluated and the catalog has not landed yet, so the New world entry
     is left wordless and repaintBootCopy fills it the moment the words arrive, which
     is the road every other dynamic sentence on this page travels. renderAllFromPrefs
     paints this field in the app and never runs here, which is why the preview has to
     lay it down itself at all. */
  if(!WORLD_LIST_MOCK.includes("Final Sunset")) WORLD_LIST_MOCK.unshift("Final Sunset");
  WORLD_NAMES=WORLD_LIST_MOCK.slice();
  $("#fWorld").innerHTML=WORLD_NAMES.map(n=>`<option>${esc(n)}</option>`).join("")
    +`<option value="${WORLD_NEW_VALUE}"></option>`;
  syncWorldNew();

  /* mock config vault (RUNES page preview) */
  CFG.mock={
    "BepInEx.cfg":
`[Logging.Console]
## Enables showing a console for log output.
Enabled = true

[Logging.Disk]
WriteUnityLog = false
AppendLog = false
LogLevels = Fatal, Error, Warning, Message, Info

[Preloader.Entrypoint]
Assembly = UnityEngine.CoreModule.dll
Type = MonoBehaviour
Method = .cctor`,
    "shudnal.ExtraSlots.cfg":
`[General]
## Extra utility slots for equipped items
Extra utility slots = 2
Quick slots = 3

[Slots]
Equipment slots enabled = true
Food slots amount = 3
Misc slots amount = 2`,
    "nl.avii.plugins.rcon.cfg":
`[RCON]
## Enable the RCON listener
enabled = true
port = 25575
password = ******

[Advanced]
maxPacketSize = 4096`,
  };
  refreshCfgList(false);

  /* world-save write times: a dozen plausible runs so the bars carry real tooltips */
  S.saveDur=[198,214,205,232,209,241,218,236,252,224,268,214];
  renderSaveAvg();
  renderSaveBars();

  /* uptime tick */
  setInterval(()=>{ if(running){upMin++;renderHearth();} },60000);

  /* save countdown */
  let saveSec=252; // 04:12
  setInterval(()=>{
    if(!running) return;
    saveSec--;
    if(saveSec<0){
      saveSec=600;
      const ms=190+Math.floor(Math.random()*90);
      S.saveDur.push(ms); if(S.saveDur.length>12)S.saveDur.shift();
      renderSaveAvg();
      renderSaveBars();
      toast("ᛉ "+T("hearth.saves.saved.preview.toast",{clock:clock()}));
      logLine("ok","World saved ( Final_Sunset.db )  8.42 MB  in "+ms+" ms");
    }
    $("#saveCountdown").textContent=
      String(Math.floor(saveSec/60)).padStart(2,"0")+":"+String(saveSec%60).padStart(2,"0");
  },1000);

  /* sparklines */
  for(let i=0;i<N;i++){cpu.push(20+Math.random()*10);ram.push(60+Math.random()*4);}
  function tickSpark(){
    cpu.shift(); ram.shift();
    const base=running?23:2, rbase=running?62:8;
    cpu.push(Math.max(1,base+(Math.random()*14-6)+(Math.random()<.06?22:0)));
    ram.push(Math.max(2,rbase+(Math.random()*3-1.5)));
    drawLine($("#cpuLine"),cpu,0,60); drawLine($("#ramLine"),ram,0,100);
    $("#cpuVal").textContent=Math.round(cpu[N-1])+"%";
    $("#ramVal").textContent=(ram[N-1]/10).toFixed(1)+" GB";
  }
  setInterval(tickSpark,900); tickSpark();

  /* periodic mock toasts */
  setInterval(()=>{ if(running) toast("ᛉ World saved · "+clock()); },12000);
  /* mod updates are a standing condition now (renderMods raises it), not a toast */
  setTimeout(()=>renderAppBar(),300);

  /* saga seed + chatter */
  const seed=[
   ["info","[BepInEx] 63 plugins loaded in 4.21 s"],
   ["ok","World loaded ( Final_Sunset )  ZDOs: 412,882"],
   ["net","[Steam] game server connected, public IP 203.0.113.42"],
   ["ok","RCON bound on 127.0.0.1:25575"],
   ["info","Smithix has arrived, spawned at 3959, -1361, 35"],
   ["info","Van Hoenhiem has arrived, spawned at -212, 887, 41"],
   ["warn","[ValheimOptimizer] frame budget exceeded 18.4 ms (single spike)"],
   ["ok","World saved ( Final_Sunset.db )  8.39 MB  in 198 ms"],
  ];
  seed.forEach(([k,t])=>logLine(k,t));
  const chatter=[
   ["info",()=>"ZDO sync: "+ (412000+Math.floor(Math.random()*2000)).toLocaleString(LOC()) +" active, "+Math.floor(Math.random()*40+8)+" dirty"],
   ["net",()=>"[Steam] socket recv "+(Math.random()*42+6).toFixed(1)+" KB/s · send "+(Math.random()*18+3).toFixed(1)+" KB/s"],
   ["ok",()=>"World saved ( Final_Sunset.db )  8.4"+Math.floor(Math.random()*9)+" MB  in "+(180+Math.floor(Math.random()*90))+" ms"],
   ["info",()=>["Smithix hauled 30 iron across the swamp","Van Hoenhiem tamed a lox","Wind shifted, sailing weather fair","Smithix has arrived","Raven event rolled: none"][Math.floor(Math.random()*5)]],
   ["warn",()=>"[ExtraSlots] config reloaded from memory snapshot"],
   ["net",()=>"[RCON] keepalive ok · 127.0.0.1:25575"],
  ];
  setInterval(()=>{ if(!running) return;
    const [k,f]=chatter[Math.floor(Math.random()*chatter.length)]; logLine(k,f());
  },2000);
}
