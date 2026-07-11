"use strict";

/* ============ NATIVE BRIDGE (WebView2 host seam) ============ */
const Native = (() => {
  const wv = window.chrome?.webview;
  let seq = 0; const pending = new Map(); const listeners = new Map();
  if (wv) wv.addEventListener('message', e => {
    const m = e.data;
    if (m && m.id != null && pending.has(m.id)) { const {res, rej} = pending.get(m.id); pending.delete(m.id); m.ok ? res(m.result) : rej(new Error(m.error)); }
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
const pad=n=>String(n).padStart(2,"0");
function fmtT(d){const t=new Date(d);return isNaN(t)?"-":pad(t.getHours())+":"+pad(t.getMinutes());}
/* "3d ago at 1945" - delta plus wall-clock, the way you'd tell a friend. */
function agoAt(d){
  const t=new Date(d); if(isNaN(t)) return "-";
  const s=Math.max(0,(Date.now()-t.getTime())/1000);
  const ago=s<60?"just now":s<3600?Math.floor(s/60)+"m ago":s<86400?Math.floor(s/3600)+"h ago":Math.floor(s/86400)+"d ago";
  return ago+" at "+pad(t.getHours())+pad(t.getMinutes());
}

/* RPC wrapper: every call catches + toasts; returns FAIL sentinel instead of rejecting
   so no rejected promise is ever unhandled. */
const FAIL=Symbol("rpc-failed");
function rpc(method,params){
  return Native.call(method,params).catch(err=>{
    toast("ᚦ "+method+" failed · "+(err?.message||"unknown error"));
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
  modSort:{col:null,dir:0},   // mods table sort: col name|installed|latest|status, dir 0=default 1=asc 2=desc
  extIp:null, intIp:null,
  domain:null,            // custom join domain (the Waystone) - shown instead of the raw public IP when set
  invite:null,            // crossplay invite code (shown while known; cleared on stop)
  saveDur:[],             // last 10 world-save durations (ms) for the rolling average
  lastSaveAt:null,        // Date of the last observed world save
  net:{conns:null,zdos:null,sent:null,recv:null,at:null,hist:[]}, // parsed "Connections N ZDOS:… sent:… recv:…" server stats
  servers:[],             // multi-server chip strip: [{name,status,running,playersOnline,active}]
};

/* True when an event's profile tag belongs to the profile shown in the UI.
   Untagged events (null/undefined) always pass - single-server compatibility. */
const isActiveProfile=p=>p==null||!S.profileName||String(p).toLowerCase()===String(S.profileName).toLowerCase();

const fmtBytes=n=>{
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

/* draggable region: titlebar, minus interactive elements */
$("#titlebar").addEventListener("mousedown",e=>{
  if(e.button!==0||!Native.available) return;
  if(e.target.closest(".cmdchip,.winbtns,.winbtn")) return;
  Native.post("win.dragStart");
});

/* resize grips */
$$(".grip").forEach(g=>g.addEventListener("mousedown",e=>{
  if(e.button!==0||!Native.available) return;
  e.preventDefault();
  Native.post("win.resizeStart",{edge:g.dataset.edge});
}));

/* ---------- TERMINOLOGY (plain-English toggle, Upkeep card) ----------
   PLAIN swaps the Norse-lore wording for plain English. Ordered pairs:
   longest / most specific phrases first so generic word swaps never
   pre-empt them. Server log lines are NEVER plainified (raw data). */
let PLAIN=false;
const TERM_PAIRS=[
  ["No vikings have set sail for this realm yet.","No players have connected yet."],
  ["Some deeds are sleeping","Some features are unavailable"],
  ["right-click a viking for deeds","right-click a player for actions"],
  ["Hearth stoked · vikings warned","Restart scheduled · players warned"],
  ["Hearth stoked · no vikings online","Restarting · no players online"],
  ["Hearth stoked · server restarting","Server restarting"],
  ["Hearth stoked","Restart scheduled"],
  ["Hearth doused · server stopping","Server stopping"],
  ["Hearth doused · server stopped","Server stopped"],
  ["Hearth kindled · server starting","Server starting"],
  ["STOPPED · embers doused","STOPPED"],
  ["dousing the embers","shutting down"],
  ["embers doused","server stopped"],
  ["consult the saga","check the console log"],
  ["Saga log","console log"],
  ["Join prompt copied · sailing forth","Join prompt copied"],
  ["sailing forth","connecting"],
  ["Sail Forth","Copy Join Info"],
  ["sail forth","connect"],
  ["The Herald speaks","Status post published"],
  ["The Herald falls silent","Status post removed"],
  ["Summon the Herald","Discord sharing setup"],
  ["Let the Herald speak","Publish the post"],
  ["The Herald","Discord sharing"],
  ["the Herald","the Discord post"],
  ["HERALD","DISCORD"],
  ["Herald","Discord"],
  ["Forge the webhook","Create the webhook"],
  ["Choose the tidings","Choose what to share"],
  ["Raise the Waystone","Save the domain"],
  ["Raise a Waystone","Set up a custom domain"],
  ["The Waystone stands","Domain saved"],
  ["The Waystone falls","Domain removed"],
  ["Name the Waystone","Choose the name"],
  ["Prove the Waystone","Verify the domain"],
  ["current Waystone","current domain"],
  ["WAYSTONE","DOMAIN"],
  ["Waystone","Domain"],
  ["waystone","domain"],
  ["Unearth this layer","Restore this backup"],
  ["Layer unearthed","Backup restored"],
  ["Layer deleted","Backup deleted"],
  ["The Barrow","Backups"],
  ["the Barrow","the backup manager"],
  ["Unearthing","Restoring"],
  ["UNEARTH","RESTORE"],
  ["Unearth","Restore"],
  ["unearth","restore"],
  ["unearthing","restore"],
  ["BARROW","BACKUPS"],
  ["Barrow","Backups"],
  ["Layers","Backups"],
  ["Layer","Backup"],
  ["layers","backups"],
  ["layer","backup"],
  ["nothing chronicled yet - happenings appear as the realm lives","nothing recorded yet - events appear as the server runs"],
  ["counted on this machine only, nothing leaves it","stored on this machine only, never uploaded"],
  ["no mod updates chronicled yet","no mod updates recorded yet"],
  ["the realm's story in numbers","the server's history in numbers"],
  ["the hearth collapsed","server crashed"],
  ["valkyries dispatched","player deaths"],
  ["chronicled since","recorded since"],
  ["hearth kindled","server started"],
  ["hearth doused","server stopped"],
  ["Hearth burned","Server uptime"],
  ["fell in battle","died"],
  ["Mod chronicle","Mod history"],
  ["unique souls","unique players"],
  ["came ashore","joined"],
  ["raiding now","online now"],
  ["sailed off","left"],
  ["Happenings","Recent events"],
  ["Kindlings","Starts"],
  ["visits","sessions"],
  ["alight","running"],
  ["SKALD","ANALYTICS"],
  ["Skald","Analytics"],
  ["skald","analytics"],
  ["BepInEx config scrolls","BepInEx config files"],
  ["no .cfg scrolls found","no .cfg files found"],
  ["Scrolls reloaded","Configs reloaded"],
  ["in the config vault","in the config folder"],
  ["rune-scrolls","config files"],
  ["rune-scroll","config file"],
  ["last rune-check","last save"],
  ["unsaved rune-work","unsaved changes"],
  ["live chronicle of the realm","live server log"],
  ["ADVANCED RITES","ADVANCED SETTINGS"],
  ["Hearth Status","Server Status"],
  ["Forge Load","System Load"],
  ["Summon a command…","Type a command…"],
  ["KINDLING…","STARTING…"],
  ["FADING…","STOPPING…"],
  ["BURNING","RUNNING"],
  ["COLD","STOPPED"],
  ["Kindle","Start"],
  ["Douse","Stop"],
  ["Stoke","Restart"],
  ["HEARTH","DASHBOARD"],
  ["Hearth","Dashboard"],
  ["hearth","server"],
  ["VIKINGS","PLAYERS"],
  ["Vikings","Players"],
  ["vikings","players"],
  ["Viking","Player"],
  ["viking","player"],
  ["raiding","online"],
  ["deeds","actions"],
  ["RUNES","CONFIGS"],
  ["Runes","Configs"],
  ["SAGA","CONSOLE"],
  ["Saga","Console"],
  ["saga","log"],
  ["rites","settings"],
  ["rite","action"],
  ["forge","mods"],
  ["realm","world"],
];
const TERM_RES=TERM_PAIRS.map(([f,t])=>[
  new RegExp((/^\w/.test(f)?"\\b":"")+f.replace(/[.*+?^${}()|[\]\\]/g,"\\$&")+(/\w$/.test(f)?"\\b":""),"g"),
  t
]);
function plainify(s){
  if(!PLAIN||typeof s!=="string") return s;
  for(const [re,t] of TERM_RES) s=s.replace(re,t);
  return s;
}
const TT=plainify;
/* Static lore-bearing elements: text nodes only (rune glyphs + pills untouched).
   Dynamic surfaces (#vikSub, #runesSub, hState/hPid, toasts…) route through TT()
   at render time instead - caching their originals would restore stale values. */
const TERM_STATIC_SEL="h1,.navitem .lbl,#sailBtn,.microlabel,#lastSaveLbl,.capshead,#page-vikings th,#page-skald th,#skDeathsSub,#secRites,#page-saga .sub,#page-world .sub,.pitem .k,#waystoneBtn";
const TERM_ORIG=new Map();
function applyTerms(){
  $$(TERM_STATIC_SEL).forEach(el=>[...el.childNodes].forEach(n=>{
    if(n.nodeType!==3||!n.nodeValue.trim()) return;
    if(!TERM_ORIG.has(n)) TERM_ORIG.set(n,n.nodeValue);
    const o=TERM_ORIG.get(n);
    n.nodeValue=PLAIN?plainify(o):o;
  }));
  [$("#palInput"),$("#cfgEditor")].forEach(el=>{
    if(!el) return;
    if(!TERM_ORIG.has(el)) TERM_ORIG.set(el,el.placeholder);
    const o=TERM_ORIG.get(el);
    el.placeholder=PLAIN?plainify(o):o;
  });
  /* refresh the lore-bearing dynamic surfaces so the swap is immediate */
  try{renderHearth();}catch{}
  try{renderCaps();}catch{}
  try{renderPlayers();}catch{}
  try{if(CFG.files&&CFG.files.length)renderCfgList();}catch{}
  try{if(SKALD)renderSkald();}catch{}
}

/* ---------- NAV ---------- */
let currentPage="hearth";
function goPage(name){
  $$(".navitem").forEach(n=>n.classList.toggle("active",n.dataset.page===name));
  $$(".page").forEach(p=>{
    const on=p.id==="page-"+name;
    p.classList.toggle("active",on);
    p.style.display=on?(p.id==="page-atlas"?"flex":"block"):"none"; // atlas is a flex column
  });
  currentPage=name;
  try{renderEditBar();}catch(_){}
  if(name==="atlas"){try{atlasEnter();}catch(_){}} // also drives the mock preview
  if(name==="skald"){try{skaldRefresh();}catch(_){}} // mock in preview, journal in-app
  if(Native.available){
    if(name==="mods"&&!S.modsScanned&&!S.modsScanning) scanMods();
    if(name==="vikings") refreshPlayers();
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
    return `<div class="${cls}" data-srv="${esc(s.name)}" title="${esc(s.name)}${s.running?" · "+esc(s.status):""} · right-click for options"><span class="srvdot${s.running?" on":""}"></span><span class="srvinit">${esc(initial)}</span>${badge}</div>`;
  }).join("");
  html+=`<div class="srvchip add" id="srvAdd" title="Found a new realm"><span class="srvinit">+</span></div>`;
  const archBadge=archived.length?`<span class="srvbadge">${archived.length}</span>`:"";
  const restoreTip=archived.length
    ?`Restore a realm · ${archived.length} archived, or bring back a past world`
    :`Restore a past realm from its world files`;
  html+=`<div class="srvchip arch" id="srvRestore" title="${restoreTip}"><span class="srvinit">↺</span>${archBadge}</div>`;
  el.innerHTML=html;
  const sep=$("#srvSep"); if(sep) sep.style.display=list.length?"":"none";
}
/* Right-click a chip → per-realm actions. */
function serverChipMenu(name,x,y){
  const s=(S.servers||[]).find(v=>String(v.name)===String(name)); if(!s) return;
  const items=[
    {r:"ᛒ",label:"Turn helm here",fn:()=>switchServer(name),disabled:s.active,tip:"Already the active realm"},
    {r:"ᚱ",label:"Rename…",fn:()=>renameServer(name),disabled:s.running,tip:"Stop the server first"},
    {r:"ᛞ",label:"Duplicate…",fn:()=>duplicateServer(name)},
    "hr",
    {r:"⌂",label:"Archive",fn:()=>archiveServer(name),disabled:s.running,tip:"Stop the server first"},
    {r:"ᛟ",label:"Delete…",fn:()=>deleteServerFlow(name),danger:true,disabled:s.running,tip:"Stop the server first"},
  ];
  ctxOpen(x,y,name,items);
}
async function renameServer(name){
  promptModal(TT("Rename realm"),name,async v=>{
    v=(v||"").trim(); if(!v||v===name) return;
    const r=await rpc("profiles.rename",{name,newName:v});
    if(r===FAIL) return;
    if(S.profileName===name){S.profileName=v;if(S.prefs)S.prefs.ProfileName=v;}
    await refreshServers(); toast(TT("ᚱ Realm renamed · ")+v);
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
  await refreshServers(); toast(TT("⌂ Realm archived · kept for restore"));
}
/* Delete flow: default removes settings only; a danger toggle also reclaims the
   isolated install + this realm's worlds/backups (with an exact file count/size). */
async function deleteServerFlow(name){
  const info=await rpc("profiles.deleteInfo",{name});
  if(info===FAIL||!info){toast(TT("Could not read that realm."));return;}
  if(info.running){toast(TT("Stop the server before deleting it."));return;}
  const mb=(info.sizeBytes||0)/1048576;
  const sizeStr=mb>=1024?(mb/1024).toFixed(1)+" GB":Math.max(0,mb).toFixed(mb<10?1:0)+" MB";
  const hasFiles=(info.hasIsolatedInstall||info.saveFolder)&&info.fileCount>0;
  const dangerLine=hasFiles
    ?`<label class="togglerow" style="cursor:pointer"><span class="tl" style="color:var(--danger,#E8560F)">${esc(TT("Also delete this realm's isolated worlds, backups & install"))}<br><span style="font-size:9.5px;opacity:.7">${info.fileCount} ${esc(TT("files · "))}${esc(sizeStr)}${esc(TT(" · cannot be undone"))}</span></span><div class="toggle" id="delFiles"></div></label>`
    :`<div class="fieldnote">${esc(TT("This realm shares its install/worlds — no isolated files to remove."))}</div>`;
  const m=modalOpen(
    `<div class="mtitle">${esc(TT("Delete realm"))} · ${esc(name)}</div>`+
    `<div class="mbody"><p>${esc(TT("By default this removes only the saved settings — the realm can be re-adopted later from its world files."))}</p>`+
      dangerLine+
      `<div class="mbody-note" id="delStatus"></div></div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="delCancel">Cancel</button>`+
      `<button class="btn btn-ember btn-sm" id="delOk">${esc(TT("Delete"))}</button></div>`);
  const delFiles=m.querySelector("#delFiles");
  if(delFiles) delFiles.addEventListener("click",()=>delFiles.classList.toggle("on"));
  m.querySelector("#delCancel").addEventListener("click",modalClose);
  m.querySelector("#delOk").addEventListener("click",async()=>{
    const df=delFiles?delFiles.classList.contains("on"):false;
    const ok=m.querySelector("#delOk"); ok.disabled=true; ok.textContent=TT("Deleting…");
    const r=await rpc("profiles.delete",{name,deleteFiles:df});
    if(r===FAIL){ok.disabled=false;ok.textContent=TT("Delete");const st=m.querySelector("#delStatus");if(st)st.textContent=TT("Delete failed — see the saga log.");return;}
    modalClose();
    if(S.profileName===name) S.profileName=null;
    await refreshServers();
    // If we deleted the active realm, switch the UI to whatever remains.
    const next=(S.servers||[]).find(s=>!s.archived);
    if(next) await switchServer(next.name);
    toast(TT("ᛟ Realm deleted"));
  });
}
/* Restore surface: (1) archived realms — unarchive or delete for good; (2) past worlds
   still on disk that no realm owns — adopt each back as its own isolated server. Pull-based:
   orphan worlds are only queried here, never on startup, so many old worlds never nag. */
async function restoreModal(){
  const archived=(S.servers||[]).filter(s=>s.archived);
  const orphans=await rpc("worlds.listOrphans",{});
  const orphanList=Array.isArray(orphans)?orphans:[];
  if(!archived.length&&!orphanList.length){toast(TT("Nothing to restore — no archived realms or past worlds found."));return;}

  const archRows=archived.map(s=>
    `<div class="arow" style="display:flex;align-items:center;justify-content:space-between;gap:10px;padding:7px 0;border-bottom:1px solid var(--line,#2A2E34)">`+
    `<span class="cilbl">${esc(s.name)}</span>`+
    `<span style="display:flex;gap:6px"><button class="btn btn-ghost btn-sm arst" data-name="${esc(s.name)}">${esc(TT("Restore"))}</button>`+
    `<button class="btn btn-ghost btn-sm ardel" data-name="${esc(s.name)}" style="color:var(--danger,#E8560F)">${esc(TT("Delete"))}</button></span></div>`).join("");

  const orphanRows=orphanList.map((o,i)=>{
    const mb=(o.sizeBytes||0)/1048576;
    const sizeStr=mb>=1024?(mb/1024).toFixed(1)+" GB":Math.max(0,mb).toFixed(mb<10?1:0)+" MB";
    let when="";
    try{const d=new Date(o.modifiedUtc);if(!isNaN(d))when=d.toLocaleDateString();}catch{}
    const older=o.olderCount>0?` · +${o.olderCount} older`:"";
    return `<div class="arow" style="display:flex;align-items:center;justify-content:space-between;gap:10px;padding:7px 0;border-bottom:1px solid var(--line,#2A2E34)">`+
      `<span class="cilbl">${esc(o.world)}<br><span style="font-size:9.5px;opacity:.6">${esc(sizeStr)}${when?" · "+esc(when):""}${esc(older)}</span></span>`+
      `<button class="btn btn-ghost btn-sm oadopt" data-i="${i}">${esc(TT("Bring back"))}</button></div>`;
  }).join("");

  const archSection=archived.length
    ?`<div class="mtitle" style="font-size:12px;opacity:.85">${esc(TT("Archived realms"))}</div><div class="mbody">${archRows}</div>`:"";
  const orphanSection=orphanList.length
    ?`<div class="mtitle" style="font-size:12px;opacity:.85${archived.length?";margin-top:10px":""}">${esc(TT("Past worlds on disk"))}</div>`+
     `<div class="fieldnote" style="margin:2px 0 4px">${esc(TT("Worlds no realm currently owns. Bringing one back makes a fresh isolated server — the original files are left untouched."))}</div>`+
     `<div class="mbody">${orphanRows}</div>`:"";

  const m=modalOpen(
    `<div class="mtitle">${esc(TT("Restore a realm"))}</div>`+
    archSection+orphanSection+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="arClose">Close</button></div>`);
  m.querySelector("#arClose").addEventListener("click",modalClose);
  m.querySelectorAll(".arst").forEach(b=>b.addEventListener("click",async()=>{
    const r=await rpc("profiles.unarchive",{name:b.dataset.name});
    if(r===FAIL) return;
    modalClose(); await refreshServers(); toast(TT("⌂ Realm restored · ")+b.dataset.name);
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
   seeded from the active realm, else vanilla — kept distinct to avoid cross-contamination). */
async function adoptWorldFlow(o){
  const taken=new Set((S.servers||[]).map(s=>String(s.name).toLowerCase()));
  const m=modalOpen(
    `<div class="mtitle">${esc(TT("Bring back world"))} · ${esc(o.world)}</div>`+
    `<div class="mbody">`+
      `<div class="field"><label>${esc(TT("New realm name"))}</label>`+
        `<input type="text" id="awName" value="${esc(o.world)}" spellcheck="false" autocomplete="off"></div>`+
      `<label class="togglerow" style="cursor:pointer"><span class="tl">${esc(TT("Copy the active realm's mods into it"))}`+
        `<br><span style="font-size:9.5px;opacity:.7">${esc(TT("Off = start this realm vanilla"))}</span></span>`+
        `<div class="toggle on" id="awSeed"></div></label>`+
      `<div class="fieldnote" id="awStatus">${esc(TT("The world files are copied — the original is left untouched."))}</div>`+
    `</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="awCancel">Cancel</button>`+
      `<button class="btn btn-ember btn-sm" id="awOk">${esc(TT("Bring back"))}</button></div>`);
  const seed=m.querySelector("#awSeed");
  seed.addEventListener("click",()=>seed.classList.toggle("on"));
  m.querySelector("#awCancel").addEventListener("click",modalClose);
  m.querySelector("#awOk").addEventListener("click",async()=>{
    const name=(m.querySelector("#awName").value||"").trim();
    const st=m.querySelector("#awStatus");
    if(!name){st.textContent=TT("A realm name is required.");return;}
    if(taken.has(name.toLowerCase())){st.textContent=TT("A realm by that name already exists.");return;}
    const ok=m.querySelector("#awOk"); ok.disabled=true; ok.textContent=TT("Bringing back…");
    const r=await rpc("servers.adoptWorld",{world:o.world,folder:o.folder,sub:o.sub,name,seedMods:seed.classList.contains("on")});
    if(r===FAIL){ok.disabled=false;ok.textContent=TT("Bring back");st.textContent=TT("Could not bring that world back — see the saga log.");return;}
    modalClose();
    await refreshServers();
    if(r&&r.ProfileName){await switchServer(r.ProfileName);goPage("world");}
    toast(TT("↺ Realm restored · ")+name);
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
  try{atlasReset();}catch{}
  const st=await rpc("server.state");
  if(st!==FAIL){S.state=null;applyState(st);}
  renderAllFromPrefs();
  await refreshPlayers();
  renderMods();
  const caps=await rpc("caps.get");
  if(caps!==FAIL&&caps){S.caps=caps;renderCaps();}
  try{renderWorldMods();}catch{}
  try{renderMaxPlayers();}catch{}
  refreshServers();
  logLine("info","[BakaLoader] switched to profile '"+name+"'");
  toast(TT("ᛒ Helm turned · ")+prefs.ProfileName);
  if(currentPage==="mods") scanMods();
  if(currentPage==="runes") refreshCfgList(true);
}
/* New-Server wizard: a realm gets its OWN identity (name, world, free ports) and,
   by default, its OWN isolated install (independent BepInEx/plugins) + save folder,
   so a second server is genuinely separate rather than a shadow of the first. The
   heavy lifting (provisioning the isolated install) happens in servers.create. */
async function addServerProfile(){
  if(!Native.available){
    // Mock/preview: keep the old lightweight add so the UI is explorable offline.
    promptModal(TT("Name the new realm"),"e.g. Midgard Two",name=>{
      name=(name||"").trim(); if(!name) return;
      S.servers=[...(S.servers||[]),{name,status:"Stopped",running:false,playersOnline:0,active:false}];
      renderServerChips();
    });
    return;
  }
  const m=modalOpen(
    `<div class="mtitle">${esc(TT("Found a new realm"))}</div>`+
    `<div class="mbody" style="display:flex;flex-direction:column;gap:2px">`+
      `<div class="field"><label>${esc(TT("Realm name"))}</label>`+
        `<input type="text" id="wsName" placeholder="e.g. Midgard Two" spellcheck="false" autocomplete="off"></div>`+
      `<div class="field"><label>${esc(TT("World"))}</label>`+
        `<input type="text" id="wsWorld" placeholder="${esc(TT("its own new world"))}" spellcheck="false" autocomplete="off">`+
        `<div class="fieldnote" id="wsPorts">${esc(TT("finding a free port…"))}</div></div>`+
      `<div class="field"><label>${esc(TT("World seed"))}</label>`+
        `<input type="text" id="wsWorldSeed" placeholder="${esc(TT("random (leave blank)"))}" spellcheck="false" autocomplete="off">`+
        `<div class="fieldnote">${esc(TT("only for a brand-new world · fixed forever once created"))}</div></div>`+
      `<div class="togglerow" title="${esc(TT(
        "ON — this realm gets its own BepInEx: its own mods, mod configs, and cache, fully independent of your other servers. "+
        "Example: run Epic Loot here while your main server stays vanilla, or trial a mod update without risking the live world. "+
        "The bulky game files are shared behind the scenes, so this does NOT duplicate the multi-gigabyte install.\n\n"+
        "OFF — the realm runs from the same install and mod folder as the base server: installing, updating, or removing a mod on either one changes both."
      ))}"><span class="tl">${esc(TT("Separate install (own mods)"))}</span>`+
        `<div class="toggle on" id="wsIso"></div></div>`+
      `<div class="togglerow" title="${esc(TT(
        "ON — the new realm starts with a copy of the current server's mods and their configs, then goes its own way: updating or removing a mod here never touches the original. "+
        "Example: clone your main server's whole mod set to build a matching test server.\n\n"+
        "OFF — the realm starts clean with no mods; add them later in the MODS hall.\n\n"+
        "Only applies with a separate install — on a shared install the mods are shared by definition."
      ))}"><span class="tl">${esc(TT("Copy this server's mods to start"))}</span>`+
        `<div class="toggle on" id="wsSeed"></div></div>`+
      `<div class="togglerow" title="${esc(TT(
        "ON — this realm keeps its worlds and backups in its own save folder, so two servers can never write to the same world file and backups never mix. "+
        "Example: Midgard Two saves under its own folder instead of the shared IronGate\\Valheim one.\n\n"+
        "OFF — the realm uses the shared Valheim save folder. That works, as long as two realms never host the same world at the same time (BakaLoader warns if they would)."
      ))}"><span class="tl">${esc(TT("Separate save folder (own worlds/backups)"))}</span>`+
        `<div class="toggle on" id="wsSaveIso"></div></div>`+
      `<div class="mbody-note" id="wsStatus"></div>`+
    `</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="wsCancel">Cancel</button>`+
      `<button class="btn btn-ember btn-sm" id="wsOk">${esc(TT("Forge realm"))}</button></div>`);

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

  let ports=null;
  rpc("servers.suggestPort",{}).then(r=>{
    if(r&&r!==FAIL){ports=r; portsN.textContent=TT("will claim game port ")+r.gamePort+" (+"+(r.gamePort+1)+") · RCON "+r.rconPort;}
    else portsN.textContent=TT("ports will be auto-assigned");
  });

  m.querySelector("#wsCancel").addEventListener("click",modalClose);
  okB.addEventListener("click",async()=>{
    const name=nameI.value.trim();
    if(!name){nameI.focus();return;}
    if((S.servers||[]).some(s=>String(s.name).toLowerCase()===name.toLowerCase())){
      statusN.textContent=TT("A realm by that name already burns."); return;
    }
    okB.disabled=true; okB.textContent=TT("Forging…");
    statusN.textContent=on(iso)?TT("provisioning a separate install (this can take a moment)…"):TT("saving…");
    const created=await rpc("servers.create",{
      name, world:worldI.value.trim(),
      worldSeed:m.querySelector("#wsWorldSeed").value.trim(),
      isolateInstall:on(iso), seedMods:on(iso)&&on(seed), isolateSaveFolder:on(saveIso),
      port:ports?ports.gamePort:null, rconPort:ports?ports.rconPort:null,
    });
    if(created===FAIL||!created){
      okB.disabled=false; okB.textContent=TT("Forge realm");
      statusN.textContent=TT("Could not forge that realm — see the saga log.");
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

/* ---------- HEARTH: uptime + douse ---------- */
let running=true, upMin=272; // 4h 32m (mock only)
const hCard=$("#hearthCard"), hState=$("#hState"), hPid=$("#hPid"),
      douseBtn=$("#douseBtn"), stokeBtn=$("#stokeBtn"), sailBtn=$("#sailBtn");
function renderHearth(){
  if(Native.available){renderHearthNative();return;}
  if(running){
    hCard.classList.remove("cold");
    hState.innerHTML=`BURNING · ${Math.floor(upMin/60)}h ${String(upMin%60).padStart(2,"0")}m`;
    hPid.textContent="RUNNING · PID 150428 · valheim_server.x86_64";
    douseBtn.textContent="Douse";
  }else{
    hCard.classList.add("cold");
    hState.textContent="COLD";
    hPid.textContent="STOPPED · embers doused · world saved";
    douseBtn.textContent="Kindle";
  }
  stokeBtn.disabled=!running;
}
function renderHearthNative(){
  const st=S.state||{status:"Stopped",canStart:false,canStop:false};
  hCard.classList.toggle("cold",st.status!=="Running");
  if(st.status==="Running"){
    const up=S.upSince?Date.now()-S.upSince:0;
    hState.textContent=TT(`BURNING · ${Math.floor(up/3600000)}h ${pad(Math.floor(up/60000)%60)}m`);
    hPid.textContent="RUNNING · "+(S.prefs?.WorldName||"world")+" · valheim_server"+(st.adopted?" · adopted":"");
  }else if(st.status==="Starting"){
    hState.textContent=TT("KINDLING…");
    hPid.textContent=TT("STARTING · raising the server");
  }else if(st.status==="Stopping"){
    hState.textContent=TT("FADING…");
    hPid.textContent=TT("STOPPING · dousing the embers");
  }else{
    hState.textContent=TT("COLD");
    hPid.textContent=TT("STOPPED · embers doused");
  }
  douseBtn.textContent=TT(st.canStop?"Douse":"Kindle");
  douseBtn.disabled=!(st.canStart||st.canStop);
  stokeBtn.textContent=st.countdownActive?"Restart NOW":TT("Stoke");
  stokeBtn.title=TT(st.countdownActive?"Skip the countdown and restart immediately":"Restart the server · warns players in-game first if any are online");
  stokeBtn.disabled=!(st.canStop||st.countdownActive);
  stokeBtn.classList.toggle("btn-ember",!!st.countdownActive);
  stokeBtn.classList.toggle("btn-ghost",!st.countdownActive);
}
function applyState(st){
  if(!st) return;
  const prev=S.state?.status;
  S.state=st;
  if(st.status==="Running"&&prev!=="Running"){
    S.upSince=Date.now();
    if(S.saveSec==null) S.saveSec=S.saveInterval;
  }
  if(st.status==="Stopped"){
    S.upSince=null; S.saveSec=null;
    $("#saveCountdown").textContent="-:-";
    if(S.invite){S.invite=null;renderNet();} // invite dies with the server
  }
  renderHearthNative();
}
douseBtn.addEventListener("click",async e=>{
  e.stopPropagation();
  if(Native.available){
    const st=S.state||{};
    if(st.canStop){
      const r=await rpc("server.stop");
      if(r!==FAIL){applyState(r);toast("ᛪ Hearth doused · server stopping");logLine("warn","[BakaLoader] stop requested - dousing the embers");}
    }else if(st.canStart){
      if(!S.prefs){toast("ᚦ no profile loaded · cannot start");return;}
      const r=await rpc("server.start",{prefs:S.prefs});
      if(r!==FAIL){applyState(r);toast("ᚠ Hearth kindled · server starting");logLine("ok","[BakaLoader] start requested · profile "+(S.profileName||"?"));}
    }
    return;
  }
  running=!running; if(running) upMin=0;
  renderHearth();
  toast(running?"ᚠ Hearth kindled · server starting":"ᛪ Hearth doused · server stopped");
  logLine(running?"ok":"warn",running?"[BakaLoader] server process launched (PID 150428)":"[BakaLoader] graceful shutdown - world saved, embers doused");
});
function smartRestart(){
  return rpc("server.restart").then(r=>{
    if(r===FAIL) return;
    applyState(r.state);
    if(r.restart==="countdown"){
      // countdown task spins up async - flip the button to "Restart NOW" right away
      if(S.state) S.state.countdownActive=true;
      renderHearthNative();
      toast("ᚱ Hearth stoked · vikings warned · restart in 1 min");
      logLine("warn","[BakaLoader] restart requested - players online, 1-minute warning broadcast");
    }else if(r.restart==="bypassed"){
      toast("ᚱ Restarting NOW · countdown skipped");
      logLine("warn","[BakaLoader] restart countdown bypassed - restarting now");
    }else if(r.restart==="now"){
      toast("ᚱ Hearth stoked · no vikings online · restarting now");
      logLine("warn","[BakaLoader] restart requested - server empty, restarting immediately");
    }else{
      toast("ᚦ Restart unavailable · server not running");
    }
  });
}
stokeBtn.addEventListener("click",e=>{
  e.stopPropagation();
  if(Native.available){smartRestart();return;}
  toast("ᚱ Hearth stoked · server restarting");
  logLine("warn","[BakaLoader] restart requested - stoking the hearth");
});
/* Sail Forth copies a full join prompt - everything a friend needs to connect:
   server name / world / public ip:port / password (+ crossplay code when known).
   When a Waystone (custom domain) is raised, the domain rides in place of the raw IP -
   Valheim resolves A/AAAA records on join, but the port must still travel with it (no SRV). */
function joinHost(){return S.domain||S.extIp||"…";}
function buildJoinPrompt(){
  const p=S.prefs||{};
  const addr=joinHost()+":"+(p.Port??2456);
  const lines=[p.Name||"Valheim server", p.WorldName||"-", addr];
  if(p.Password) lines.push(p.Password);
  if(p.Crossplay&&S.invite) lines.push("crossplay code "+S.invite);
  return {text:lines.join("\n"), addr};
}
sailBtn.addEventListener("click",()=>{
  if(Native.available){
    const jp=buildJoinPrompt();
    navigator.clipboard?.writeText(jp.text).catch(()=>{});
    toast("ᛟ Join prompt copied · "+jp.addr);
    return;
  }
  if(!running){running=true;upMin=0;renderHearth();}
  const jp=buildJoinPrompt();
  navigator.clipboard?.writeText(jp.text).catch(()=>{});
  toast("ᛟ Join prompt copied · sailing forth");
});
renderHearth();

/* ---------- SPARKLINES (shared drawing; mock driver below) ---------- */
const N=40, cpu=[], ram=[];
function drawLine(el,data,min,max){
  const pts=data.map((v,i)=>{
    const x=(i/(N-1))*200, y=28-((v-min)/(max-min))*26;
    return x.toFixed(1)+","+Math.max(2,Math.min(28,y)).toFixed(1);
  }).join(" ");
  el.setAttribute("points",pts);
}

/* ---------- STATUS CLOCK ---------- */
function clock(){const d=new Date();return String(d.getHours()).padStart(2,"0")+":"+String(d.getMinutes()).padStart(2,"0");}
setInterval(()=>{
  const d=new Date();
  $("#clockSeg").textContent=[d.getHours(),d.getMinutes(),d.getSeconds()].map(x=>String(x).padStart(2,"0")).join(":")+" JST";
  if(!Native.available) $("#tickVal").textContent=running?(58+Math.floor(Math.random()*3))+"/60":"-";
},1000);

/* ---------- COPY CHIP ---------- */
$("#copyIp").addEventListener("click",e=>{
  e.stopPropagation();
  if(Native.available){
    const addr=(S.extIp||"…")+":"+(S.prefs?.Port??2456);
    navigator.clipboard?.writeText(addr).catch(()=>{});
    e.target.textContent="COPIED";
    toast("ᚾ Address copied · "+addr);
    setTimeout(()=>e.target.textContent="COPY",1400);
    return;
  }
  e.target.textContent="COPIED";
  toast("ᚾ Address copied · 203.0.113.42:2456");
  setTimeout(()=>e.target.textContent="COPY",1400);
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
  $("#netLan").textContent="LAN "+(S.intIp||"…")+":"+port;
  $("#netLoc").textContent="127.0.0.1:"+port;
  const il=$("#netInviteLine");
  il.style.display=S.invite?"":"none";
  if(S.invite) $("#netInvite").textContent="invite "+S.invite;
  const rl=$("#netRconLine"), on=!!S.prefs?.RconEnabled;
  rl.style.display=on?"":"none";
  if(on) $("#netRcon").textContent="RCON 127.0.0.1:"+(S.prefs.RconPort??25575);
  $("#netPill").textContent=on?"rcon bound":"rcon off";
  $("#netPill").className="pill "+(on?"green":"blue");
}
/* LAN / local / invite copy chips - copy exactly what's displayed (works in mock + native) */
[["#copyLan","#netLan"],["#copyLoc","#netLoc"],["#copyInv","#netInvite"],["#copyDomain","#netDomain"]].forEach(([chip,src])=>{
  $(chip).addEventListener("click",e=>{
    e.stopPropagation();
    const txt=$(src).textContent.replace(/^(LAN|invite)\s+/,"");
    navigator.clipboard?.writeText(txt).catch(()=>{});
    e.target.textContent="COPIED";
    toast("ᚾ Copied · "+txt);
    setTimeout(()=>e.target.textContent="COPY",1400);
  });
});
/* open-folder affordances (shell.open - native only) */
$("#openWorlds").addEventListener("click",e=>{
  e.stopPropagation();
  if(Native.available) rpc("shell.open",{target:"saveData"});
  else toast("ᛃ Worlds folder · preview only");
});
$("#openLogs").addEventListener("click",()=>{
  if(Native.available) rpc("shell.open",{target:"logs"});
  else toast("ᛃ Logs folder · preview only");
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
emberize($("#meadLink"));
emberize($("#lchipFog")); // fog now defaults on (may veil the whole realm) - keep its switch eye-catching

/* horn-of-mead - opens the donate page in the default browser (native only) */
$("#meadLink").addEventListener("click",e=>{
  e.preventDefault();
  if(Native.available){ rpc("shell.openUrl",{target:"donate"}); toast("ᛥ Skål! Opening the mead hall…"); }
  else toast("ᛥ A horn of mead · preview only");
});

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
  if(col==="name") s.sort((a,b)=>m*String(a.ModName||"").localeCompare(String(b.ModName||""),undefined,{sensitivity:"base"}));
  else if(col==="installed") s.sort((a,b)=>m*verCmp(a.InstalledVersion,b.InstalledVersion));
  else if(col==="latest") s.sort((a,b)=>m*verCmp(a.LatestVersion,b.LatestVersion));
  else if(col==="status") s.sort((a,b)=>m*((b.UpdateAvailable?1:0)-(a.UpdateAvailable?1:0)));
  return s;
}
function renderModSortMarks(){
  document.querySelectorAll("#page-mods th.sortable").forEach(th=>{
    const on=S.modSort.col===th.dataset.sort&&S.modSort.dir;
    th.classList.toggle("sorted",!!on);
    const ver=th.dataset.sort==="installed"||th.dataset.sort==="latest";
    const up=(S.modSort.dir===1)!==ver;   // versions show hi→lo (▼) on first click
    th.querySelector(".sortmark").textContent=on?(up?"▲":"▼"):"";
  });
}
document.querySelectorAll("#page-mods th.sortable").forEach(th=>th.addEventListener("click",()=>{
  const c=th.dataset.sort;
  if(S.modSort.col===c) S.modSort.dir=(S.modSort.dir+1)%3;
  else S.modSort={col:c,dir:1};
  if(!S.modSort.dir) S.modSort.col=null;
  renderMods();
}));
function renderMods(){
  const busy=S.modsScanning||S.modsUpdating;
  $("#scanBtn").disabled=busy;
  $("#addModBtn").disabled=busy;
  const scanned=S.mods!==null;
  const mods=sortedMods(S.mods||[]);
  const upd=mods.filter(m=>m.UpdateAvailable);
  $("#updAllBtn").innerHTML="ᚱ&nbsp; Update all ("+(scanned?upd.length:"-")+")";
  $("#updAllBtn").disabled=busy||!upd.length;
  $("#modCount").textContent=scanned?mods.length:"-";
  $("#sbMods").textContent=(scanned?mods.length:"-")+" mods";
  renderModSortMarks();
  if(!scanned){
    $("#modTable").innerHTML=`<tr><td colspan="4" class="mono" style="color:var(--bone-dim)">${S.modsScanning?"Scanning Thunderstore…":"Not yet scanned - press Scan Thunderstore."}</td></tr>`;
    $("#modTable")._list=[];
    $("#modUpdWrap").innerHTML="";
    $("#modsSub").textContent="Thunderstore index · "+(S.modsScanning?"scanning…":"not yet scanned");
    return;
  }
  $("#modTable").innerHTML=mods.map((m,i)=>{
    const has=!!m.UpdateAvailable;
    const pill=m.Bundled?`<span class="pill ember">Bundled</span>`
      :`<span class="pill ${has?"amber":"green"}">${has?"Update":"Current"}</span>`;
    return `<tr data-i="${i}"><td><strong>${esc(m.ModName)}</strong> <span class="mono" style="font-size:10px;color:var(--bone-faint)">${esc(m.Author)}</span></td>`+
      `<td class="mono">${esc(m.InstalledVersion||"-")}</td>`+
      `<td class="mono"${has?' style="color:var(--amber)"':""}>${esc(m.LatestVersion||"-")}</td>`+
      `<td>${pill}</td></tr>`;
  }).join("")||`<tr><td colspan="4" class="mono" style="color:var(--bone-dim)">No mods found - check the server exe path.</td></tr>`;
  $("#modTable")._list=mods;
  $("#modUpdWrap").innerHTML=upd.length
    ?`<span class="pill amber">${upd.length} update${upd.length===1?"":"s"}</span>`
    :`<span class="pill green">up to date</span>`;
  $("#modsSub").textContent=mods.length+" loaded · Thunderstore index"+(S.lastScan?" · last scan "+S.lastScan:"");
}
async function scanMods(){
  if(!Native.available||S.modsScanning||S.modsUpdating) return;
  S.modsScanning=true; renderMods();
  toast("ᛋ Thunderstore scan begun · v1 community index");
  logLine("info","[Thunderstore] fetching valheim community index (cached 15m)…");
  const r=await rpc("mods.scan");
  S.modsScanning=false;
  if(r===FAIL||!Array.isArray(r)){renderMods();return;}
  S.mods=r; S.modsScanned=true; S.lastScan=clock();
  renderMods();
  const u=r.filter(m=>m.UpdateAvailable).length;
  toast("ᛋ Scan complete · "+r.length+" mods · "+u+" update"+(u===1?"":"s"));
}
async function doUpdateAll(){
  if(S.modsUpdating||S.modsScanning) return;
  S.modsUpdating=true; renderMods();
  toast("ᚱ Updating mods · fetching from Thunderstore…");
  const r=await rpc("mods.updateAll");
  S.modsUpdating=false;
  if(r===FAIL||!Array.isArray(r)){renderMods();return;}
  const okN=r.filter(x=>x.Updated).length, errN=r.filter(x=>x.Error).length;
  r.forEach(x=>logLine(x.Updated?"ok":"warn","[Thunderstore] "+x.mod+" "+(x.Updated?(x.FromVersion+" → "+x.ToVersion):("failed: "+(x.Error||"unknown")))));
  toast(errN?("ᚦ Updated "+okN+" mods · "+errN+" failed"):("ᚱ Updated "+okN+" mod"+(okN===1?"":"s")));
  await scanMods(); // re-render from a fresh scan
}
$("#updAllBtn").addEventListener("click",()=>{
  if(Native.available){
    const upd=(S.mods||[]).filter(m=>m.UpdateAvailable);
    if(!upd.length) return;
    /* NOTE: mods.updateAll in the bridge does NOT stop/restart the server - reflect that. */
    const runWarn=(S.state?.status==="Running")
      ?`<div class="mwarn">⚠ The server is RUNNING. Updating does NOT stop or restart it - new versions only load after a restart, and locked files may fail to update. Stopping the server first is recommended.</div>`:"";
    confirmModal("Update "+upd.length+" mod"+(upd.length===1?"":"s"),
      `<div class="mono-list">${upd.map(m=>esc(m.FullName)+"  "+esc(m.InstalledVersion)+" → "+esc(m.LatestVersion)).join("<br>")}</div>`+runWarn,
      "Update all",()=>doUpdateAll());
    return;
  }
  toast("ᚱ Updating 2 mods · WorldEditCommands, ExtraSlots");
  logLine("info","[Thunderstore] downloading WorldEditCommands 1.66.0 …");
});
$("#scanBtn").addEventListener("click",()=>{
  if(Native.available){scanMods();return;}
  toast("ᛋ Thunderstore scan begun · v1 community index");
  logLine("info","[Thunderstore] fetching valheim community index (cached 15m)…");
});
/* right-click a mod row → remove */
$("#modTable").addEventListener("contextmenu",e=>{
  if(!Native.available) return;
  const tr=e.target.closest("tr[data-i]"); if(!tr) return;
  e.preventDefault();
  const mod=($("#modTable")._list||[])[+tr.dataset.i]; if(!mod) return;
  ctxOpen(e.clientX,e.clientY,mod.FullName,[
    {r:"ᛪ",label:"Remove mod…",danger:true,fn:()=>removeModFlow(mod)},
  ]);
});
async function removeModFlow(mod){
  const files=await rpc("mods.findConfigs",{fullName:mod.FullName});
  if(files===FAIL) return;
  const cfgs=Array.isArray(files)?files:[];
  const body=
    `<div class="mbody-note">The plugin folder is backed up to BepInEx\\.bakaloader-removed, then removed.</div>`+
    (cfgs.length
      ?`<label class="mchk"><input type="checkbox" id="mIncCfg" checked> Also delete ${cfgs.length} config file${cfgs.length===1?"":"s"}:</label><div class="mono-list">${cfgs.map(f=>esc(String(f).split(/[\\/]/).pop())).join("<br>")}</div>`
      :`<div class="mbody-note">No config files found for this mod.</div>`)+
    ((S.state?.status==="Running")
      ?`<div class="mwarn">⚠ The server is RUNNING - removal does not stop it; locked files may fail. Stopping first is recommended.</div>`:"");
  confirmModal("Remove "+mod.FullName+"?",body,"Remove",m=>{
    const includeConfig=!!m.querySelector("#mIncCfg")?.checked;
    doRemoveMod(mod,includeConfig);
  });
}
async function doRemoveMod(mod,includeConfig){
  toast("ᛪ Removing "+mod.FullName+"…");
  const r=await rpc("mods.remove",{fullName:mod.FullName,includeConfig});
  if(r===FAIL) return;
  if(r.Removed){
    toast("ᛪ Removed "+r.mod+" · backup kept");
    logLine("warn","[BakaLoader] removed mod "+r.mod+(r.BackupDirectory?" (backup: "+r.BackupDirectory+")":""));
  }else{
    toast("ᚦ Remove failed · "+(r.Error||"unknown"));
  }
  scanMods();
}
/* ---- Add a mod from any pasted Thunderstore link ---- */
function addModFlow(){
  if(S.modsUpdating||S.modsScanning) return;
  const runWarn=(S.state?.status==="Running")
    ?`<div class="mwarn">⚠ The server is RUNNING - new mods only load after a restart. Replacing an existing mod may fail on locked files.</div>`:"";
  confirmModal("Add mod from Thunderstore",
    `<div class="mbody-note">Paste any Thunderstore link - the mod's page, its versions page, a direct download link, or a ror2mm:// mod-manager link. If the link has no version, the latest release is installed.</div>`+
    `<input type="text" id="mModUrl" placeholder="https://thunderstore.io/c/valheim/p/Author/ModName/" spellcheck="false" autocomplete="off" style="margin-top:10px">`+
    runWarn,
    "Install",m=>{
      const url=(m.querySelector("#mModUrl")?.value||"").trim();
      if(!url){toast("ᚦ No link pasted");return;}
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
  toast("ᚨ Fetching from Thunderstore…");
  logLine("info","[Thunderstore] resolving pasted link: "+url);
  const r=await rpc("mods.addFromUrl",{url});
  S.modsUpdating=false;
  if(r===FAIL){renderMods();return;}
  if(r.Installed){
    const full=r.Owner+"-"+r.Name;
    toast("ᚨ Installed "+full+" "+(r.Version||"")+(r.Replaced?" · replaced previous install":""));
    logLine("ok","[Thunderstore] installed "+full+" v"+(r.Version||"?")+(r.Replaced?" (previous copy backed up)":""));
    if(S.state?.status==="Running") logLine("warn","[Thunderstore] server is running - "+full+" loads on the next restart");
    await scanMods();
  }else{
    toast("ᚦ Install failed · "+(r.Error||"unknown"));
    logLine("warn","[Thunderstore] install failed: "+(r.Error||"unknown"));
    renderMods();
  }
}
$("#addModBtn").addEventListener("click",()=>{
  if(Native.available){addModFlow();return;}
  toast("ᚨ Add from Thunderstore · native app only");
});

/* ---------- COMMAND PALETTE ---------- */
const palBg=$("#paletteBg"), palIn=$("#palInput");
let palOpen=false;
function openPal(){palOpen=true;palBg.classList.add("open");palIn.value="";updatePalGating();filterPal("");setTimeout(()=>palIn.focus(),30);}
function closePal(){palOpen=false;palBg.classList.remove("open");}
/* Grey out rites that have nothing to act on: RCON commands need a RUNNING server,
   start/stop follow canStart/canStop. Browser preview keeps everything clickable. */
const PAL_NEEDS_RUNNING={"Save world now":1,"Kill all monsters":1,"Broadcast message…":1,"Send console command…":1};
function updatePalGating(){
  if(!Native.available) return;
  const st=S.state||{}, running=st.status==="Running";
  $$(".pitem").forEach(it=>{
    const cmd=it.dataset.cmd; let dis=false, why="";
    if(it.id==="palConsole"||PAL_NEEDS_RUNNING[cmd]){dis=!running;why="Server not running - nothing to send the command to";}
    else if(cmd==="Restart server"){dis=!(st.canStop||st.countdownActive);why="Server not running";}
    else if(cmd==="Stop server"){dis=!st.canStop;why="Server not running";}
    else if(cmd==="Start server"){dis=!st.canStart;why="Server already running";}
    it.classList.toggle("disabled",dis);
    it.title=dis?why:"";
  });
}
/* Free-form console command over RCON. Echoes "> cmd" to the Saga log, then the
   reply (many Valheim commands answer to stdout, which the log already tails). */
async function sendConsole(cmd,okToast){
  cmd=(cmd||"").trim(); if(!cmd) return;
  logLine("cmd","> "+cmd);
  const r=await rpc("server.command",{command:cmd});
  if(r===FAIL) return;
  if(!r.ok){
    toast("ᚦ Command not delivered · server running & RCON bound?");
    logLine("warn","[RCON] command failed - is the server running with RCON enabled?");
    return;
  }
  if(okToast) toast(okToast);
  const resp=(r.response||"").trim();
  if(resp) resp.split(/\r?\n/).forEach(l=>logLine("ok",l));
  else logLine("ok","[RCON] "+cmd+" · dispatched - replies land here in the Saga log");
}
function filterPal(q){
  const raw=q.trim(); q=raw.toLowerCase(); let first=true;
  $$(".pitem:not(.pconsole)").forEach(it=>{
    const hit=it.dataset.cmd.toLowerCase().includes(q);
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
      $("#palConsoleLbl").textContent="Send \""+raw+"\" to server console";
    }
  }
}
function invokePal(){
  const sel=$(".pitem.sel:not(.hidden)")||$(".pitem:not(.hidden)");
  if(!sel) return;
  if(sel.classList.contains("disabled")){toast("ᚦ "+(sel.title||"Unavailable right now"));return;}
  closePal();
  if(sel.dataset.cmd==="Discord sharing"){goPage("herald");return;} // works in preview too
  if(sel.dataset.cmd==="Custom domain"){waystoneWizard();return;}   // works in preview too
  if(sel.dataset.cmd==="World backups"){barrowModal();return;}      // works in preview too
  if(sel.dataset.cmd==="Server analytics"){goPage("skald");return;} // works in preview too
  if(Native.available){
    const cmd=sel.dataset.cmd;
    if(sel.id==="palConsole"){
      sendConsole(sel.dataset.raw||"");
    }else if(cmd==="Restart server"){
      smartRestart();
    }else if(cmd==="Start server"){
      const st=S.state||{};
      if(!st.canStart){toast("ᚦ Start unavailable · already running?");}
      else if(!S.prefs){toast("ᚦ no profile loaded · cannot start");}
      else rpc("server.start",{prefs:S.prefs}).then(r=>{
        if(r!==FAIL){applyState(r);toast("ᚠ Hearth kindled · server starting");logLine("ok","[BakaLoader] start requested · profile "+(S.profileName||"?"));}
      });
    }else if(cmd==="Stop server"){
      if(!(S.state||{}).canStop){toast("ᚦ Stop unavailable · server not running");}
      else rpc("server.stop").then(r=>{
        if(r!==FAIL){applyState(r);toast("ᛪ Hearth doused · server stopping");logLine("warn","[BakaLoader] stop requested - dousing the embers");}
      });
    }else if(cmd==="Save world now"){
      sendConsole("save","ᛉ World save requested");
    }else if(cmd==="Kill all monsters"){
      sendConsole("baka_killall","ᚦ KillAll unleashed · players, pets & allies spared");
    }else if(cmd==="Broadcast message…"){
      promptModal(TT("Broadcast to all vikings"),"message shown in-game to everyone online",m=>{
        rpc("server.broadcast",{message:m}).then(r=>{
          if(r===FAIL) return;
          toast(r?"ᛒ Broadcast delivered":"ᚦ Broadcast failed · RCON bound?");
          logLine(r?"ok":"warn",r?"[RCON] broadcast delivered":"[RCON] broadcast failed - is RCON enabled and bound?");
        });
      });
    }else if(cmd==="Send console command…"){
      consoleModal();
    }else if(cmd==="Update all mods"){
      goPage("mods");
      $("#updAllBtn").click();
    }else if(cmd==="Kick player…"){
      goPage("vikings");
      toast("ᚲ Right-click a viking to kick");
    }else if(cmd==="Copy join address"){
      const addr=joinHost()+":"+(S.prefs?.Port??2456);
      navigator.clipboard?.writeText(addr).catch(()=>{});
      toast("ᛟ Join address copied · "+addr);
    }else if(cmd==="Open world folder"){
      rpc("shell.open",{target:"saveData"});
    }else if(cmd==="Open config folder"){
      rpc("shell.open",{target:"config"});
    }else if(cmd==="Open plugins folder"){
      rpc("shell.open",{target:"plugins"});
    }else if(cmd==="Open server logs"){
      rpc("shell.open",{target:"logs"});
    }
    return;
  }
  toast("ᛒ "+sel.dataset.cmd+" · invoked");
  logLine("cmd","> "+sel.dataset.cmd.toLowerCase().replace(/…/,""));
}
$("#cmdchip").addEventListener("click",openPal);
palIn.addEventListener("input",()=>filterPal(palIn.value));
palBg.addEventListener("click",e=>{if(e.target===palBg)closePal();});
$$(".pitem").forEach(it=>it.addEventListener("click",()=>{
  if(it.classList.contains("disabled")){toast("ᚦ "+(it.title||"Unavailable right now"));return;}
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

/* ---------- TOASTS ---------- */
function toast(msg){
  msg=TT(msg); // plain-terminology swap (rune glyph token is never matched)
  const t=document.createElement("div");
  t.className="toast";
  const parts=msg.split(" ");
  t.innerHTML=`<span class="r">${parts[0]}</span><span>${esc(parts.slice(1).join(" "))}</span><span class="tt">${clock()}</span>`;
  $("#toasts").appendChild(t);
  setTimeout(()=>{t.classList.add("out");setTimeout(()=>t.remove(),320);},4200);
}

/* ---------- SAGA TERMINAL ---------- */
const term=$("#term"); let filter="all";
function logLine(kind,text){
  const d=document.createElement("div");
  d.className="ln "+kind; d.dataset.k=kind;
  d.innerHTML=`<span class="t">${clock()}:${String(new Date().getSeconds()).padStart(2,"0")}</span>  <span class="${kind}">${esc(text)}</span>`;
  d.style.display=(filter==="all"||filter===kind||(filter==="info"&&kind==="ok"))?"":"none";
  term.appendChild(d);
  while(term.children.length>160) term.firstChild.remove();
  term.scrollTop=term.scrollHeight;
}
function classifyLog(line){
  const l=(line||"").toLowerCase();
  if(l.includes("error")||l.includes("exception")) return "err";
  if(l.includes("warn")) return "warn";
  return "info";
}

/* filter pills */
$$(".fpill").forEach(p=>p.addEventListener("click",()=>{
  $$(".fpill").forEach(x=>x.classList.remove("active")); p.classList.add("active");
  filter=p.dataset.f;
  $$("#term .ln").forEach(l=>{
    const k=l.dataset.k;
    l.style.display=(filter==="all"||filter===k||(filter==="info"&&k==="ok"))?"":"none";
  });
  term.scrollTop=term.scrollHeight;
}));

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
        `<div>ᛜ <b>${esc((m.Author?m.Author+"/":"")+(m.ModName||""))}</b> - ${esc(m.RequiredFor||m.Description||"")}</div>`
      ).join("");
      banner.style.display="";
    }else banner.style.display="none";
  }
  const gated=Native.available&&!S.caps?.devcommands;
  tIn.disabled=gated;
  tIn.placeholder=gated
    ?TT("broadcast sleeps - install the RCON + devcommands mods (see VIKINGS page)")
    :"devcommand… (Enter to send)";
}
let capsInstallBusy=false;
$("#capsInstallBtn")?.addEventListener("click",async()=>{
  if(capsInstallBusy||!Native.available) return;
  capsInstallBusy=true;
  const btn=$("#capsInstallBtn");
  btn.disabled=true; btn.textContent="ᚠ  Fetching from Thunderstore…";
  const r=await rpc("caps.install");
  if(r!==FAIL){
    const ok=(r.results||[]).filter(x=>x.installed).length;
    const still=(r.stillMissing||[]).length;
    toast(still
      ?"ᚦ installed "+ok+" · "+still+" still missing - check the Saga log"
      :"ᚠ "+ok+" mod"+(ok===1?"":"s")+" installed · loads on the next server start");
    const caps=await rpc("caps.get");
    if(caps!==FAIL&&caps) S.caps=caps;
  }
  btn.disabled=false; btn.textContent="ᚠ\u00a0 Install missing mods";
  capsInstallBusy=false;
  renderCaps();
});

/* ---------- TOGGLES ---------- */
$$("[data-t]").forEach(t=>t.addEventListener("click",()=>{t.classList.toggle("on");syncAdvGates();}));

/* ---------- UPKEEP (app self-update + start with Windows) ---------- */
$("#upkeepHead").addEventListener("click",()=>$("#upkeepCard").classList.toggle("open"));

/* WORLD hall: World Modifiers + Advanced Rites + Directories fold like the Upkeep card */
["secWorldMods","secRites","secDirs"].forEach(id=>{
  const el=document.getElementById(id);
  el.addEventListener("click",()=>el.classList.toggle("open"));
});
async function initUpkeep(){
  const up=await rpc("userprefs.get");
  if(up!==FAIL&&up){
    setT("tAutoUpdApp",up.AutoUpdateBakaLoader);
    setT("tStartWin",up.StartWithWindows);
    setT("tShareStats",up.ShareAnonymousStats);
    setT("tPlainTerms",up.PlainTerminology);
    PLAIN=!!up.PlainTerminology;
    if(PLAIN) applyTerms();
    if(up.AppVersion) $("#blVersion").textContent="v"+up.AppVersion;
    heraldApply(up); /* Herald hall shares the same DTO */
    S.domain=(up.CustomJoinDomain||"").trim()||null;
    renderWaystone();
  }
  /* the generic [data-t] handler already flipped .on before these fire, so just persist */
  const save=()=>rpc("userprefs.save",{prefs:{AutoUpdateBakaLoader:T("tAutoUpdApp"),StartWithWindows:T("tStartWin"),ShareAnonymousStats:T("tShareStats"),PlainTerminology:T("tPlainTerms")}});
  $("#tAutoUpdApp").addEventListener("click",save);
  $("#tStartWin").addEventListener("click",save);
  $("#tShareStats").addEventListener("click",save);
  $("#tPlainTerms").addEventListener("click",()=>{PLAIN=T("tPlainTerms");save();applyTerms();});
}

/* ---------- HERALD (Discord sharing · one self-editing status post) ----------
   The C# DiscordStatusService owns the post: it debounces edits and PATCHes the
   same message forever. This hall only reads/writes prefs + the 3 discord.* RPCs. */
let HERALD_HAS_POST=false;
const HERALD_URL_RE=/^https:\/\/((ptb|canary)\.)?discord(app)?\.com\/api\/(v\d+\/)?webhooks\/\d+\/[\w-]+/;
function heraldRenderPost(){
  const el=$("#heraldPostStat"); if(!el) return;
  el.textContent=TT(HERALD_HAS_POST
    ?"post is placed · it edits itself whenever the realm changes"
    :"no post yet · publish to place the Herald in your channel");
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
    DiscordSharingEnabled:T("tHerald"),
    DiscordShareAddress:T("tHeraldAddr"),
    DiscordSharePassword:T("tHeraldPass"),
    DiscordEventPosts:T("tHeraldEvents"),
    DiscordWebhookUrl:$("#heraldUrl").value.trim(),
    DiscordWebhookThreadId:$("#heraldThread").value.trim()
  },extra||{})});
}
["tHerald","tHeraldAddr","tHeraldPass","tHeraldEvents"].forEach(id=>
  $("#"+id).addEventListener("click",()=>heraldSave()));
{
  const inp=$("#heraldUrl"),stat=$("#heraldUrlStat");
  let t=null;
  inp.addEventListener("input",()=>{
    clearTimeout(t);
    const v=inp.value.trim();
    if(!v){
      stat.className="wiz-stat dim";stat.textContent="paste a webhook URL, or run the setup wizard";
      t=setTimeout(()=>heraldSave(),400);return;
    }
    stat.className="wiz-stat dim";stat.textContent="checking…";
    t=setTimeout(async()=>{
      const r=Native.available?await rpc("discord.validate",{url:v})
        :{ok:HERALD_URL_RE.test(v),name:"preview-hook",error:"that doesn't look like a Discord webhook URL"};
      if(r===FAIL) return;
      stat.className="wiz-stat "+(r.ok?"ok":"bad");
      stat.textContent=r.ok?("ᛉ webhook answers · \""+(r.name||"webhook")+"\"")
                           :("ᚦ "+(r.error||"that webhook does not answer"));
      if(r.ok) heraldSave();
    },500);
  });
  $("#heraldThread").addEventListener("change",()=>heraldSave());
}
$("#heraldPublish").addEventListener("click",async()=>{
  const url=$("#heraldUrl").value.trim();
  if(!url){toast("ᚦ No webhook URL · paste one or run the setup wizard");return;}
  setT("tHerald",true); /* publishing implies sharing is on */
  await heraldSave();
  if(!Native.available){HERALD_HAS_POST=true;heraldRenderPost();toast("ᚺ preview · post published");return;}
  const r=await rpc("discord.publish");
  if(r===FAIL) return;
  if(r.ok){
    HERALD_HAS_POST=true;heraldRenderPost();
    toast("ᚺ The Herald speaks · status post placed in Discord");
    logLine("ok","[Herald] Discord status post published");
  }else toast("ᚦ "+(r.error||"publish failed"));
});
$("#heraldRemove").addEventListener("click",async()=>{
  if(!Native.available){HERALD_HAS_POST=false;heraldRenderPost();toast("ᚺ preview · post removed");return;}
  const r=await rpc("discord.remove");
  if(r===FAIL) return;
  if(r.ok){
    HERALD_HAS_POST=false;heraldRenderPost();
    toast("ᚺ The Herald falls silent · post removed from Discord");
    logLine("warn","[Herald] Discord status post removed");
  }else toast("ᚦ "+(r.error||"remove failed"));
});
$("#heraldWizBtn").addEventListener("click",()=>heraldWizard());

/* ---------- CONTEXT MENU (live, JS-positioned) ---------- */
const ctxEl=$("#ctxMenu");
function ctxClose(){ctxEl.classList.remove("open");ctxEl.style.display="none";ctxEl._items=null;}
function ctxOpen(x,y,head,items){
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
    ci.querySelector(".cilbl").textContent="Confirm "+it.label.replace(/…$/,"")+"?";
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
function modalClose(){modalBg.classList.remove("open");modalBg.innerHTML="";}
modalBg.addEventListener("mousedown",e=>{if(e.target===modalBg)modalClose();});
function modalOpen(html){
  modalBg.innerHTML=`<div class="modal">${html}</div>`;
  modalBg.classList.add("open");
  return modalBg.firstElementChild;
}
function promptModal(title,placeholder,onOk){
  const m=modalOpen(
    `<div class="mtitle">${esc(title)}</div>`+
    `<input type="text" id="mIn" placeholder="${esc(placeholder)}" spellcheck="false" autocomplete="off">`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mCancel">Cancel</button><button class="btn btn-ember btn-sm" id="mOk">Confirm</button></div>`);
  const inp=m.querySelector("#mIn");
  const ok=()=>{const v=inp.value.trim(); if(!v)return; modalClose(); onOk(v);};
  m.querySelector("#mOk").addEventListener("click",ok);
  m.querySelector("#mCancel").addEventListener("click",modalClose);
  inp.addEventListener("keydown",e=>{if(e.key==="Enter")ok();});
  setTimeout(()=>inp.focus(),30);
}
function confirmModal(title,bodyHtml,okLabel,onOk){
  const m=modalOpen(
    `<div class="mtitle">${esc(title)}</div>`+
    `<div class="mbody">${bodyHtml}</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mCancel">Cancel</button><button class="btn btn-ember btn-sm" id="mOk">${esc(okLabel)}</button></div>`);
  m.querySelector("#mOk").addEventListener("click",()=>{try{onOk(m);}finally{modalClose();}});
  m.querySelector("#mCancel").addEventListener("click",modalClose);
}
/* Server-console picker: curated commands the modded server understands
   (BakaLoaderCommander natives + devcommands staples). Clicking a complete
   command sends it; commands that take arguments prefill the input instead. */
const CONSOLE_CMDS=[
  ["save","","write the world to disk"],
  ["playerlist","","online vikings + their positions"],
  ["baka_killall","","slay hostile mobs only · players, pets & allies spared"],
  ["broadcast center ","<message>","banner message shown to everyone online"],
  ["dmg ","<player> <amount>","damage a player · negative amount heals"],
  ["tp ","<player> <x,z,y | player>","teleport a player to coords or another player"],
  ["kick ","<player | hostId>","remove a viking from the server"],
  ["baka_spawn ","<prefab> <x,z,y> [amount] [level]","spawn an object into the world"],
  ["skiptime ","<seconds>","advance world time (devcommands)"],
  ["sleep","","skip to morning (devcommands)"],
];
function consoleModal(){
  const rows=CONSOLE_CMDS.map(([c,a,d])=>
    `<div class="crow" data-cmd="${esc(c)}" data-complete="${a?"":"1"}">`+
    `<span class="cc">${esc(c.trim())}${a?` <span class="ca">${esc(a)}</span>`:""}</span>`+
    `<span class="cd">${esc(TT(d))}</span></div>`).join("");
  const m=modalOpen(
    `<div class="mtitle">Server console</div>`+
    `<input type="text" id="mIn" placeholder="type a command, or pick one below…" spellcheck="false" autocomplete="off">`+
    `<div class="clist">${rows}</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mCancel">Cancel</button><button class="btn btn-ember btn-sm" id="mOk">Send</button></div>`);
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
    (copyable&&val&&!String(val).startsWith("-")?`<span class="copychip" data-copy="${esc(val)}">COPY</span>`:"")+`</div>`;
  const n=S.net;
  const asOf=n.at?new Date(n.at):null;
  const stat=(k,v)=>`<div class="dstat"><div class="bigval" style="font-size:20px">${v}</div><div class="subval">${k}</div></div>`;
  const online=S.players.filter(p=>p.status==="Online"||p.status==="Joining");
  const sess=online.length
    ?online.map(p=>`<div class="drow"><span class="dk">${esc(p.displayName)}</span><span class="dv mono">${esc(p.Platform||"?")} · ${esc(p.PlayerId||"")}</span><span class="dv" style="flex:0 0 auto">joined ${fmtT(p.lastStatusChange)}</span></div>`).join("")
    :`<div class="subval" style="padding:4px 2px">${TT("no vikings connected")}</div>`;
  const m=modalOpen(
    `<div class="mtitle"><span class="r" style="margin-right:8px">ᚾ</span>Network</div>`+
    `<div class="mbody">`+
    `<div class="dsec">Addresses</div>`+
    (S.domain?addrRow(TT("Waystone"),S.domain+":"+port,true):"")+
    addrRow("Public",(S.extIp||"-")+":"+port,true)+
    addrRow("LAN",(S.intIp||"-")+":"+port,true)+
    addrRow("Local","127.0.0.1:"+port,true)+
    (S.invite?addrRow("Crossplay invite",S.invite,true):"")+
    addrRow("RCON",S.prefs?.RconEnabled?"127.0.0.1:"+(S.prefs.RconPort??25575):"off",false)+
    addrRow("Steam query port",String(port+1),false)+
    `<div class="dsec">Traffic <span class="subval" style="text-transform:none;letter-spacing:0">· reported by the server every ~10 min${asOf?" · as of "+pad(asOf.getHours())+":"+pad(asOf.getMinutes()):""}</span></div>`+
    `<div class="dstats">`+
      stat("connections",n.conns??"-")+
      stat("sent /s",n.sent!=null?fmtBytes(n.sent):"-")+
      stat("recv /s",n.recv!=null?fmtBytes(n.recv):"-")+
      stat("world objects (ZDOs)",n.zdos!=null?n.zdos.toLocaleString():"-")+
    `</div>`+
    (n.at?"":`<div class="subval" style="margin:2px 0 6px">no report yet - appears in the first ~10 min after the server starts</div>`)+
    `<div class="dsec">Sessions</div>`+sess+
    `<div class="subval" style="margin-top:8px">per-player ping isn't reported by the dedicated server - players can check theirs in-game with F2</div>`+
    `</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mCancel">Close</button></div>`);
  m.querySelector("#mCancel").addEventListener("click",modalClose);
  m.querySelectorAll("[data-copy]").forEach(c=>c.addEventListener("click",()=>{
    navigator.clipboard?.writeText(c.dataset.copy);
    toast("ᛜ Copied · "+c.dataset.copy);
  }));
}

/* ---------- SAVES DRILL-DOWN (World Saves card click) ---------- */
async function savesModal(){
  const world=S.prefs?.WorldName||"";
  let info=null;
  if(Native.available&&world){
    const r=await rpc("world.info",{world});
    if(r!==FAIL) info=r;
  }
  const frow=f=>`<div class="drow"><span class="dk mono">${esc(f.name)}</span><span class="dv mono">${fmtBytes(f.sizeBytes)}</span><span class="dv" style="flex:0 0 auto">${fmtT(f.modifiedUtc)}</span></div>`;
  const files=info?.files?.length?info.files.map(frow).join(""):`<div class="subval" style="padding:4px 2px">world files not found yet - created on first save</div>`;
  const bk=info?.backups||[];
  const bkRows=bk.length
    ?bk.slice(-5).reverse().map(frow).join("")+(bk.length>5?`<div class="subval" style="padding:2px 2px">…and ${bk.length-5} older</div>`:"")
    :`<div class="subval" style="padding:4px 2px">no backups found</div>`;
  const avg=S.saveDur.length?Math.round(S.saveDur.reduce((a,b)=>a+b,0)/S.saveDur.length):null;
  const m=modalOpen(
    `<div class="mtitle"><span class="r" style="margin-right:8px">ᛉ</span>World Saves · ${esc(world||"-")}</div>`+
    `<div class="mbody">`+
    `<div class="dsec">Rhythm</div>`+
    `<div class="dstats">`+
      `<div class="dstat"><div class="bigval" style="font-size:20px">${$("#saveCountdown").textContent}</div><div class="subval">until next save</div></div>`+
      `<div class="dstat"><div class="bigval" style="font-size:20px">${Math.round((S.saveInterval??600)/60)} min</div><div class="subval">save interval</div></div>`+
      `<div class="dstat"><div class="bigval" style="font-size:20px">${S.lastSaveAt?pad(S.lastSaveAt.getHours())+":"+pad(S.lastSaveAt.getMinutes()):"-"}</div><div class="subval">last save</div></div>`+
      `<div class="dstat"><div class="bigval" style="font-size:20px">${avg!=null?avg+" ms":"-"}</div><div class="subval">avg write (last ${S.saveDur.length||"-"})</div></div>`+
    `</div>`+
    `<div class="dsec">World files</div>`+files+
    `<div class="dsec">Backups <span class="subval" style="text-transform:none;letter-spacing:0">· ${bk.length} on disk</span></div>`+bkRows+
    (info?.folder?`<div class="subval mono" style="margin-top:8px;word-break:break-all">${esc(info.folder)}</div>`:"")+
    `</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mBarrow">${esc(TT("The Barrow"))}</button><button class="btn btn-ghost btn-sm" id="mOpenWorlds">Open folder</button><button class="btn btn-ghost btn-sm" id="mCancel">Close</button></div>`);
  m.querySelector("#mCancel").addEventListener("click",modalClose);
  m.querySelector("#mBarrow").addEventListener("click",()=>barrowModal());
  m.querySelector("#mOpenWorlds").addEventListener("click",()=>{
    if(!Native.available){toast("ᛃ Preview · folder opens in the app");return;}
    rpc("shell.open",{target:"saveData"});
  });
}

/* ---------- THE BARROW (layered per-world backup manager) ---------- */
/* Preview-mode stand-in data so the whole flow can be walked without the app. */
const BARROW_MOCK=[
  {world:"Midgard",folder:"C:/Users/you/AppData/LocalLow/IronGate/Valheim",sub:"worlds_local",
   owner:"Default",running:true,sizeBytes:48234567,day:412,
   modifiedUtc:new Date(Date.now()-7*60000).toISOString(),backupBytes:139460000,
   backups:[
     {file:"Midgard_backup_auto-20260711-060000.fwl",kind:"auto",sizeBytes:47102003,day:411,hasDb:true,modifiedUtc:new Date(Date.now()-5*3600000).toISOString()},
     {file:"Midgard.fwl.old",kind:"old",sizeBytes:46990111,day:410,hasDb:true,modifiedUtc:new Date(Date.now()-26*3600000).toISOString()},
     {file:"Midgard_backup_restore-20260708-193045.fwl",kind:"restore",sizeBytes:45367886,day:398,hasDb:true,modifiedUtc:new Date(Date.now()-3*86400000-4.25*3600000).toISOString()},
   ]},
  {world:"Trialgrounds",folder:"C:/Users/you/AppData/LocalLow/BakaLoader/servers/proving",sub:"worlds_local",
   owner:null,running:false,sizeBytes:9034120,day:23,
   modifiedUtc:new Date(Date.now()-12*86400000).toISOString(),backupBytes:8877001,
   backups:[
     {file:"Trialgrounds_backup_auto-20260629-120000.fwl",kind:"auto",sizeBytes:8877001,day:22,hasDb:true,modifiedUtc:new Date(Date.now()-12*86400000-3600000).toISOString()},
   ]},
];
const BARROW_KIND={auto:"AUTO SNAPSHOT",old:"LAST GOOD",restore:"PRE-RESTORE",other:"BACKUP"};
function barrowFolderLabel(g){
  const parts=String(g.folder||"").split(/[\\/]/).filter(Boolean);
  const i=parts.findIndex(x=>x.toLowerCase()==="servers");
  const tail=i>=0&&parts[i+1]?"servers/"+parts[i+1]:parts[parts.length-1]||"";
  return tail+(g.sub==="worlds"?" · worlds":"");
}
async function barrowFetch(){
  if(!Native.available) return BARROW_MOCK;
  const r=await rpc("backups.overview",{});
  return r===FAIL?null:(r||[]);
}
async function barrowModal(){
  const groups=await barrowFetch();
  if(!groups){toast("ᚦ Could not read the backups");return;}
  const rows=groups.length?groups.map((g,i)=>
    `<div class="drow browRow" data-i="${i}" style="cursor:pointer">`+
    `<span class="dk mono">${esc(g.world)}</span>`+
    `<span class="dv">${g.owner?esc(g.owner):"<span style='opacity:.55'>unclaimed</span>"}${g.running?` <span style="color:var(--ok,#7dc98f)">● ${esc(TT("raiding"))}</span>`:""}</span>`+
    `<span class="dv mono">${(g.backups||[]).length} ${TT((g.backups||[]).length===1?"layer":"layers")} · ${fmtBytes((g.sizeBytes||0)+(g.backupBytes||0))}</span>`+
    `<span class="dv" style="flex:0 0 auto">${agoAt(g.modifiedUtc)}</span>`+
    `</div><div class="subval mono" style="padding:0 2px 6px;opacity:.6">${esc(barrowFolderLabel(g))}${g.day!=null?" · day "+g.day:""}</div>`
  ).join(""):`<div class="subval" style="padding:6px 2px">no worlds found on disk yet</div>`;
  const m=modalOpen(
    `<div class="mtitle"><span class="r" style="margin-right:8px">ᛝ</span>${esc(TT("The Barrow"))}</div>`+
    `<div class="mbody">`+
    `<div class="subval" style="margin-bottom:8px">${esc(TT("every realm's layers - automatic snapshots, the game's last-good pair, and the safety copies laid down before each restore"))}</div>`+
    rows+`</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mCancel">Close</button></div>`);
  m.querySelector("#mCancel").addEventListener("click",modalClose);
  m.querySelectorAll(".browRow").forEach(r=>r.addEventListener("click",()=>barrowWorldModal(groups[+r.dataset.i])));
}
function barrowWorldModal(g){
  const layerRow=(b,i)=>
    `<div class="drow"><span class="dk mono" style="min-width:0;overflow:hidden;text-overflow:ellipsis">${esc(b.file)}</span>`+
    `<span class="dv" style="flex:0 0 auto">${BARROW_KIND[b.kind]||"BACKUP"}</span>`+
    `<span class="dv mono" style="flex:0 0 auto">${fmtBytes(b.sizeBytes)}${b.day!=null?" · day "+b.day:""}</span>`+
    `<span class="dv" style="flex:0 0 auto">${agoAt(b.modifiedUtc)}</span>`+
    `<span class="copychip bUnearth" data-i="${i}"${g.running?` style="opacity:.4;cursor:not-allowed" title="stop the server first"`:""}>${esc(TT("UNEARTH"))}</span>`+
    `<span class="copychip bDrop" data-i="${i}" style="color:var(--warn,#e0a35c)">✕</span>`+
    `</div>`;
  const bks=g.backups||[];
  const m=modalOpen(
    `<div class="mtitle"><span class="r" style="margin-right:8px">ᛝ</span>${esc(TT("The Barrow"))} · ${esc(g.world)}</div>`+
    `<div class="mbody">`+
    `<div class="dsec">Live</div>`+
    `<div class="drow"><span class="dk mono">${esc(g.world)}.fwl + .db</span>`+
    `<span class="dv mono">${fmtBytes(g.sizeBytes)}${g.day!=null?" · day "+g.day:""}</span>`+
    `<span class="dv" style="flex:0 0 auto">${agoAt(g.modifiedUtc)}</span></div>`+
    (g.running?`<div class="subval" style="padding:2px 2px 6px;color:var(--warn,#e0a35c)">${esc(TT("this realm is raiding right now - stop the server to unearth a layer"))}</div>`:"")+
    `<div class="dsec">${esc(TT("Layers"))} <span class="subval" style="text-transform:none;letter-spacing:0">· ${bks.length} on disk · ${fmtBytes(g.backupBytes||0)}</span></div>`+
    (bks.length?bks.map(layerRow).join(""):`<div class="subval" style="padding:4px 2px">no backup layers yet - the game lays down automatic snapshots as it saves</div>`)+
    `<div class="subval mono" style="margin-top:8px;word-break:break-all">${esc(g.folder)}/${esc(g.sub)}</div>`+
    `</div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mBack">Back</button><button class="btn btn-ghost btn-sm" id="mCancel">Close</button></div>`);
  m.querySelector("#mCancel").addEventListener("click",modalClose);
  m.querySelector("#mBack").addEventListener("click",()=>barrowModal());
  const reopen=async()=>{
    const groups=await barrowFetch();
    const g2=groups&&groups.find(x=>x.world===g.world&&x.folder===g.folder&&x.sub===g.sub);
    if(g2) barrowWorldModal(g2); else barrowModal();
  };
  m.querySelectorAll(".bUnearth").forEach(c=>c.addEventListener("click",()=>{
    if(g.running){toast("ᚦ "+TT("Stop the server first - the live realm would clobber the restored files"));return;}
    const b=bks[+c.dataset.i];
    confirmModal(TT("Unearth this layer?"),
      `<div class="subval">${esc(TT("The live realm is copied to a fresh safety layer first, then"))} <span class="mono">${esc(b.file)}</span> ${esc(TT("replaces the live files. Every unearthing is reversible from the Barrow."))}${b.hasDb?"":" <span style='color:var(--warn,#e0a35c)'>"+esc(TT("This layer has no .db - only the world seed/meta is restored."))+"</span>"}</div>`,
      TT("Unearth"),async()=>{
        if(!Native.available){
          const ts=new Date();
          g.backups.unshift({file:g.world+"_backup_restore-"+ts.getFullYear()+pad(ts.getMonth()+1)+pad(ts.getDate())+"-"+pad(ts.getHours())+pad(ts.getMinutes())+"00.fwl",kind:"restore",sizeBytes:g.sizeBytes,day:g.day,hasDb:true,modifiedUtc:ts.toISOString()});
          toast("ᛝ "+TT("Layer unearthed")+" · preview only");
          setTimeout(()=>barrowWorldModal(g),0); // confirmModal closes itself right after onOk
          return;
        }
        const r=await rpc("backups.restore",{world:g.world,folder:g.folder,sub:g.sub,file:b.file});
        if(r===FAIL) return;
        toast("ᛝ "+TT("Layer unearthed")+" · "+TT("safety copy laid down"));
        logLine("ok","[BakaLoader] restored '"+g.world+"' from "+b.file+(r.snapshot?" · safety copy "+r.snapshot:""));
        reopen();
      });
  }));
  m.querySelectorAll(".bDrop").forEach(c=>c.addEventListener("click",()=>{
    const b=bks[+c.dataset.i];
    confirmModal(TT("Delete this layer?"),
      `<div class="subval"><span class="mono">${esc(b.file)}</span>${b.hasDb?" "+esc(TT("and its paired .db")):""} ${esc(TT("will be deleted from disk. This cannot be undone."))}</div>`,
      "Delete",async()=>{
        if(!Native.available){
          g.backups=g.backups.filter(x=>x!==b);
          toast("ᛪ "+TT("Layer deleted")+" · preview only");
          setTimeout(()=>barrowWorldModal(g),0);
          return;
        }
        const r=await rpc("backups.delete",{world:g.world,folder:g.folder,sub:g.sub,file:b.file});
        if(r===FAIL) return;
        toast("ᛪ "+TT("Layer deleted"));
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
/* "13d 0h" / "4h 32m" / "7m" / "40s" - big spans coarse, small spans exact */
function skDur(sec){
  sec=Math.max(0,Math.round(Number(sec)||0));
  const d=Math.floor(sec/86400),h=Math.floor(sec%86400/3600),m=Math.floor(sec%3600/60);
  if(d>0) return d+"d "+h+"h";
  if(h>0) return h+"h "+m+"m";
  if(m>0) return m+"m";
  return sec+"s";
}
const SKALD_ICON={join:"→",leave:"←",death:"†",start:"ᚠ",stop:"ᛪ",crash:"ᚦ"};
const SKALD_VERB={join:"came ashore",leave:"sailed off",death:"fell in battle",
  start:"hearth kindled",stop:"hearth doused",crash:"the hearth collapsed"};
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
  if(d.running&&(u.currentSec||0)>0){now.style.display="";now.textContent=TT("alight")+" "+skDur(u.currentSec);}
  else now.style.display="none";
  $("#skStarts").textContent=u.starts??0;
  const cp=$("#skCrashPill");
  if((u.crashes||0)>0){cp.style.display="";cp.textContent=u.crashes+(u.crashes===1?" crash":" crashes");}
  else cp.style.display="none";
  $("#skVikings").textContent=(d.players||[]).length;
  $("#skVikingsSub").textContent=TT("unique souls")+" · "+(t.sessions??0)+" "+TT("visits");
  $("#skDeaths").textContent=t.deaths??0;
  $("#skModUps").textContent=t.modUpdates??0;
  $("#skModUpsSub").textContent="updates · "+(t.modInstalls??0)+" installs";
  $("#skaldSub").textContent=TT((d.since?"chronicled since "+new Date(d.since).toLocaleDateString()+" · ":"")
    +"counted on this machine only, nothing leaves it");
  /* playtime per viking */
  const rows=(d.players||[]).map(p=>
    `<tr><td><span class="vdot ${p.online?"on":"off"}" style="display:inline-block;margin-right:8px"></span>`+
    `<span class="vname">${esc(p.name||p.character||p.key)}</span>`+
    (p.character&&p.character!==p.name?` <span class="subval">(${esc(p.character)})</span>`:"")+`</td>`+
    `<td class="mono">${skDur(p.playSec)}</td><td class="mono">${p.sessions??0}</td><td class="mono">${p.deaths??0}</td>`+
    `<td class="mono">${p.online?`<span style="color:var(--moss)">${esc(TT("raiding now"))}</span>`:agoAt(p.lastSeen)}</td></tr>`
  ).join("");
  $("#skPlayerTable").innerHTML=rows
    ||`<tr><td colspan="5" style="padding:10px 12px" class="skempty">${esc(TT("No vikings have set sail for this realm yet."))}</td></tr>`;
  /* happenings feed */
  const feed=(d.feed||[]).map(e=>{
    const who=e.name||e.character;
    const what=(e.kind==="join"||e.kind==="leave"||e.kind==="death")
      ?`<span class="skw">${esc(who||"?")}</span> ${esc(TT(SKALD_VERB[e.kind]))}`
      :esc(TT(SKALD_VERB[e.kind]||e.kind));
    return `<div class="skrow"><span class="skk ${e.kind}">${SKALD_ICON[e.kind]||"·"}</span><span>${what}</span><span class="skt">${agoAt(e.t)}</span></div>`;
  }).join("");
  $("#skFeed").innerHTML=feed
    ||`<div class="skempty">${esc(TT("nothing chronicled yet - happenings appear as the realm lives"))}</div>`;
  /* mod chronicle */
  const mods=(d.mods||[]).map(e=>{
    const ver=e.kind==="modin"?(e.to?"v"+e.to:""):((e.from?e.from+" → ":"")+(e.to||""));
    return `<div class="skrow"><span class="skk" style="color:var(--amber)">${e.kind==="modin"?"ᚨ":"ᚱ"}</span>`+
      `<span><span class="skw">${esc(e.mod||"?")}</span> ${e.kind==="modin"?"installed":"updated"}${ver?" "+esc(ver):""}</span>`+
      `<span class="skt">${agoAt(e.t)}</span></div>`;
  }).join("");
  $("#skModFeed").innerHTML=mods
    ||`<div class="skempty">${esc(TT("no mod updates chronicled yet"))}</div>`;
}

/* ---------- VIKINGS (players) ---------- */
/* RCON target mirrors MainWindow.GetRconTargetName: LastStatusCharacter || PlayerName.
   The DTO embeds the character as "Name (Char)" in displayName. */
function playerTarget(p){
  const m=/\(([^)]+)\)\s*$/.exec(p.displayName||"");
  return (m&&m[1])||p.PlayerName||p.PlayerId;
}
function renderPlayers(){
  if(!Native.available) return;
  const rank=s=>({Online:0,Joining:1,Leaving:1}[s]??2);
  const list=[...S.players].sort((a,b)=>rank(a.status)-rank(b.status)||(a.displayName||"").localeCompare(b.displayName||""));
  const online=list.filter(p=>p.status==="Online").length;
  $("#vikSub").textContent=TT(online+" of "+list.length+" raiding · right-click a viking for deeds");
  $("#homeVikPill").textContent=online+" online";
  $("#homeVik").innerHTML=list.slice(0,3).map(p=>{
    const on=p.status==="Online";
    return `<div class="vrow"${on?"":' style="opacity:.4"'}><span class="vdot ${on?"on":"off"}"></span><span class="vname">${esc(p.displayName)}</span><span class="vsub">${on?"joined":"seen"} ${fmtT(p.lastStatusChange)}</span></div>`;
  }).join("")||`<div class="vrow" style="opacity:.5"><span class="vdot off"></span><span class="vname">${TT("No vikings yet")}</span><span class="vsub">awaiting arrivals</span></div>`;
  const tb=$("#vikTable");
  tb.innerHTML=list.map((p,i)=>{
    const pill=p.status==="Online"?"green":(p.status==="Offline"?"blue":"amber");
    return `<tr data-i="${i}"${p.status==="Offline"?' class="dim"':""}>`+
      `<td><span class="vdot ${p.status==="Online"?"on":"off"}" style="display:inline-block;margin-right:9px"></span><strong>${esc(p.displayName)}</strong></td>`+
      `<td><span class="pill ${pill}">${esc(p.status)}</span></td>`+
      `<td class="mono">${fmtT(p.lastStatusChange)}</td>`+
      `<td class="mono">-</td>`+
      `<td class="mono" style="color:var(--ember)">⋯</td></tr>`;
  }).join("")||`<tr><td colspan="5" class="mono" style="color:var(--bone-dim)">${TT("No vikings have set sail for this realm yet.")}</td></tr>`;
  tb._list=list;
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
  const noR="requires the RCON mod", noD="requires the devcommands mods";
  const items=[
    {r:"ᛏ",label:"Heal",disabled:!rcon,tip:noR,fn:()=>doPlayerAct("players.heal",{target:tgt},"Healed "+tgt)},
    {r:"ᚦ",label:"Smite",danger:true,confirm:true,disabled:!rcon,tip:noR,fn:()=>doPlayerAct("players.smite",{target:tgt},"Smote "+tgt)},
    {r:"ᛒ",label:"Teleport…",disabled:!rcon,tip:noR,
      fn:()=>promptModal("Teleport "+tgt,"destination - player name, or x,z,y coords (spaces fine)",
        v=>doPlayerAct("players.teleport",{target:tgt,destination:v},"Teleported "+tgt+" → "+v))},
    {r:"ᛟ",label:"Spawn item at…",disabled:!dev,tip:noD,fn:()=>openSpawnModal(p)},
    "hr",
    {r:"ᚨ",label:isAdmin===true?"Demote from admin":"Promote to admin",fn:()=>setPlayerList(p,"Admin",isAdmin!==true)},
    {r:"ᚹ",label:isPerm===true?"Remove from whitelist":"Permit (whitelist)",fn:()=>setPlayerList(p,"Permitted",isPerm!==true)},
    "hr",
    {r:"ᚲ",label:"Kick",danger:true,confirm:true,disabled:!rcon,tip:noR,fn:()=>doPlayerAct("players.kick",{target:tgt},"Kicked "+tgt)},
    {r:"ᛉ",label:isBan===true?"Unban":"Ban",danger:isBan!==true,confirm:isBan!==true,fn:()=>doBan(p,isBan===true,tgt)},
    "hr",
    {r:"ᛁ",label:"Copy ID",fn:()=>{navigator.clipboard?.writeText(id||"").catch(()=>{});toast("ᛁ ID copied · "+id);}},
  ];
  if(p.status==="Offline"){
    items.push({r:"ᛪ",label:"Remove offline player",danger:true,confirm:true,
      fn:async()=>{const r=await rpc("players.remove",{key:p.key}); if(r===FAIL)return; toast("ᛪ Removed "+(p.displayName||id)); refreshPlayers();}});
  }
  ctxOpen(x,y,p.displayName||id||"Viking",items);
}
async function doPlayerAct(method,params,okMsg){
  const r=await rpc(method,params);
  if(r===FAIL) return;
  if(r===false){toast("ᚦ command not delivered · server running & RCON bound?");return;}
  toast("ᛒ "+okMsg);
  logLine("cmd","> "+method.replace("players.","")+" "+(params.target||params.playerName||""));
}
async function setPlayerList(p,list,on){
  const r=await rpc("players.setList",{list,id:p.PlayerId,on});
  if(r===FAIL) return;
  toast("ᚨ "+(on?"Added to":"Removed from")+" "+list.toLowerCase()+" list · "+(p.displayName||p.PlayerId));
  logLine("info","[BakaLoader] "+list.toLowerCase()+" list "+(on?"+ ":"- ")+(p.displayName||p.PlayerId));
}
async function doBan(p,unban,tgt){
  const r=await rpc("players.setList",{list:"Banned",id:p.PlayerId,on:!unban});
  if(r===FAIL) return;
  toast(unban?("ᛉ Unbanned "+tgt):("ᛉ Banned "+tgt));
  logLine(unban?"info":"warn","[BakaLoader] "+(unban?"unbanned ":"banned ")+(p.displayName||p.PlayerId));
  if(!unban&&S.caps.rcon&&p.status==="Online"){
    const k=await rpc("players.kick",{target:tgt});
    if(k!==FAIL&&k) logLine("warn","[BakaLoader] kicked "+tgt+" to enforce ban");
  }
}
/* spawn-item picker (items.search) */
function openSpawnModal(p){
  const tgt=playerTarget(p);
  const m=modalOpen(
    `<div class="mtitle">Spawn item at ${esc(tgt)}</div>`+
    `<input type="text" id="spQ" placeholder="search items &amp; creatures…" spellcheck="false" autocomplete="off">`+
    `<div class="pick-list" id="spList"></div>`+
    `<div class="mrow"><label>Amount</label><input type="number" id="spAmt" value="1" min="1" max="9999">`+
    `<label id="spLqLbl">Level</label><input type="number" id="spLq" value="0" min="0" max="5" disabled></div>`+
    `<div class="mbtns"><button class="btn btn-ghost btn-sm" id="mCancel">Cancel</button><button class="btn btn-ember btn-sm" id="mOk" disabled>Spawn</button></div>`);
  const list=m.querySelector("#spList"), q=m.querySelector("#spQ"),
        amt=m.querySelector("#spAmt"), lq=m.querySelector("#spLq"),
        lqLbl=m.querySelector("#spLqLbl"), okB=m.querySelector("#mOk");
  let sel=null, searchSeq=0;
  async function search(){
    const my=++searchSeq;
    const r=await rpc("items.search",{query:q.value.trim(),limit:100});
    if(r===FAIL||my!==searchSeq) return;
    const res=r.results||[];
    list.innerHTML=res.map((it,i)=>
      `<div class="pick-item" data-i="${i}"><span>${esc(it.Label)}</span><span class="pk">${esc(it.category||"")}</span><span class="pm">${esc(it.PrefabName)}</span></div>`
    ).join("")||`<div class="pick-item" style="opacity:.5;cursor:default">no matches</div>`;
    list._res=res;
    sel=null; okB.disabled=true;
  }
  q.addEventListener("input",()=>{clearTimeout(q._t);q._t=setTimeout(search,150);});
  list.addEventListener("click",e=>{
    const el=e.target.closest(".pick-item[data-i]"); if(!el) return;
    list.querySelectorAll(".pick-item").forEach(x=>x.classList.remove("sel"));
    el.classList.add("sel");
    sel=list._res[+el.dataset.i];
    okB.disabled=false;
    const hasLq=!!(sel.HasLevel||sel.HasQuality);
    lq.disabled=!hasLq;
    lqLbl.textContent=sel.HasLevel?"Level":"Quality";
    lq.max=sel.HasLevel?"2":"5";
    if(!hasLq) lq.value=0;
    else if(+lq.value>+lq.max) lq.value=lq.max;
  });
  okB.addEventListener("click",async()=>{
    if(!sel) return;
    const amount=Math.min(9999,Math.max(1,parseInt(amt.value,10)||1));
    const levelOrQuality=lq.disabled?0:Math.max(0,parseInt(lq.value,10)||0);
    const item=sel;
    modalClose();
    const r=await rpc("players.spawn",{playerName:tgt,prefab:item.PrefabName,amount,levelOrQuality});
    if(r===FAIL) return;
    if(r===false){toast("ᚦ spawn failed · server running & player online?");return;}
    toast("ᛟ Spawned "+amount+"× "+item.Label+" at "+tgt);
    logLine("cmd","> spawn "+item.PrefabName+" ×"+amount+" @ "+tgt);
  });
  m.querySelector("#mCancel").addEventListener("click",modalClose);
  setTimeout(()=>q.focus(),30);
  search();
}

/* ---------- WORLD (profile config) ---------- */
const T=id=>$("#"+id).classList.contains("on");
const setT=(id,on)=>$("#"+id).classList.toggle("on",!!on);
/* toggle → parameter-field gating (mirrors the WinForms enable/disable behavior) */
function syncAdvGates(){
  const gate=(inputId,on)=>{
    const el=$("#"+inputId); if(!el) return;
    el.disabled=!on;
    el.closest(".field")?.classList.toggle("gated",!on);
  };
  gate("fEmptyDelay",T("tEmpty"));
  gate("fSchedHours",T("tSched"));
  gate("fCrashDelay",T("tCrash"));
  gate("fRconPort",T("tRcon"));
  gate("fRconPw",T("tRcon"));
  updAdvLabels();
}
function updAdvLabels(){
  $("#tEmptyLbl").textContent="Restart when empty for "+($("#fEmptyDelay").value.trim()||"5")+" min";
  $("#tSchedLbl").textContent="Every "+($("#fSchedHours").value.trim()||"6")+" h with in-game countdown";
  $("#tRconLbl").textContent=T("tRcon")?("Bound on port "+($("#fRconPort").value.trim()||"25575")):"Not bound";
}
["fEmptyDelay","fSchedHours","fRconPort"].forEach(id=>$("#"+id).addEventListener("input",updAdvLabels));
/* show/hide password chips */
function wireEye(chipId,inputId){
  $("#"+chipId).addEventListener("click",()=>{
    const i=$("#"+inputId), show=i.type==="password";
    i.type=show?"text":"password";
    $("#"+chipId).textContent=show?"HIDE":"SHOW";
  });
}
wireEye("eyePw","fPassword"); wireEye("eyeRcon","fRconPw");
/* directory Open buttons (shell.open - native only) */
$("#btnOpenSrv").addEventListener("click",()=>{
  if(Native.available) rpc("shell.open",{target:"serverDir"});
  else toast("ᛃ Server folder · preview only");
});
$("#btnOpenSave").addEventListener("click",()=>{
  if(Native.available) rpc("shell.open",{target:"saveData"});
  else toast("ᛃ Save data folder · preview only");
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
  renderWorldSelect();
}
async function renderWorldSelect(){
  const cur=S.prefs?.WorldName??"";
  const r=await rpc("worlds.list");
  const names=[...new Set([...(Array.isArray(r)&&r!==FAIL?r:[]),...(cur?[cur]:[])])];
  $("#fWorld").innerHTML=names.map(n=>`<option${n===cur?" selected":""}>${esc(n)}</option>`).join("");
  renderWorldMods();
  renderWorldSeed();
}
/* World-generation dials. value "" = Normal = game default (no -modifier arg emitted).
   Every non-empty value must match Game/WorldGen.cs exactly - the C# save validates. */
const WORLDGEN={
  combat:{sel:"fModCombat",label:"Combat",opts:[
    ["veryeasy","Very easy"],["easy","Easy"],["","Normal"],["hard","Hard"],["veryhard","Very hard"]]},
  deathpenalty:{sel:"fModDeath",label:"Death penalty",opts:[
    ["casual","Casual - no skill or item loss"],["veryeasy","Very easy - keep equipped items"],
    ["easy","Easy"],["","Normal"],["hard","Hard"],["hardcore","Hardcore - permadeath"]]},
  resources:{sel:"fModResources",label:"Resources",opts:[
    ["muchless","Much less"],["less","Less"],["","Normal"],
    ["more","More - double resources"],["muchmore","Much more"],["most","Most"]]},
  raids:{sel:"fModRaids",label:"Raids",opts:[
    ["none","None"],["muchless","Much less"],["less","Less"],["","Normal"],
    ["more","More"],["muchmore","Much more"]]},
  portals:{sel:"fModPortals",label:"Portals",opts:[
    ["casual","Casual - anything through portals"],["","Normal - ore restricted"],
    ["hard","Hard - nothing teleports"],["veryhard","Very hard - portals unusable"]]},
};
const wgOptions=(key,val)=>WORLDGEN[key].opts.map(([v,l])=>
  `<option value="${v}"${v===val?" selected":""}>${esc(l)}</option>`).join("");
async function renderWorldMods(){
  const world=$("#fWorld").value||S.prefs?.WorldName||"";
  let cur={};
  if(Native.available&&world){
    const r=await rpc("worldgen.get",{world});
    if(r!==FAIL&&r?.modifiers) cur=r.modifiers;
  }
  for(const [key,def] of Object.entries(WORLDGEN))
    $("#"+def.sel).innerHTML=wgOptions(key,cur[key]||"");
}
/* World seed: read-only identity from the world's .fwl. A seed is fixed at world
   creation and can NEVER change (the field is locked); a world with no .fwl yet
   gets its seed on first launch - random, or the one chosen in the realm wizard. */
let _seedSeq=0;
async function renderWorldSeed(){
  const el=$("#fSeed"); if(!el) return;
  const world=$("#fWorld").value||S.prefs?.WorldName||"";
  const seq=++_seedSeq;
  if(!Native.available){el.value="yBvEFPKD9S · 649688311";el.dataset.copy="yBvEFPKD9S";return;}
  if(!world){el.value="";el.dataset.copy="";return;}
  const r=await rpc("world.seed",{world});
  if(seq!==_seedSeq) return; // a newer lookup superseded this one
  if(r===FAIL||!r){el.value="";el.dataset.copy="";return;}
  if(r.exists){el.value=r.seedName+" · "+r.seed;el.dataset.copy=r.seedName;}
  else{el.value=TT("not created yet · seed set on first launch");el.dataset.copy="";}
}
$("#copySeed").addEventListener("click",()=>{
  const v=$("#fSeed")?.dataset.copy||"";
  if(!v){toast(TT("no seed yet · world not created"));return;}
  navigator.clipboard?.writeText(v).catch(()=>{});
  toast(TT("ᛟ seed copied · ")+v);
});
$("#fWorld").addEventListener("change",()=>{renderWorldMods();renderWorldSeed();});
renderWorldMods(); // seed the dials with Normal defaults (both modes)
renderWorldSeed();
/* Max players: server-wide, not per-world. 10 = vanilla cap (no plugin); above 10 the
   bundled BakaLoader max-players plugin's cfg is the source of truth. */
async function renderMaxPlayers(){
  if(!Native.available) return;
  const r=await rpc("maxplayers.get",{});
  if(r!==FAIL&&r?.count!=null) $("#fMaxPlayers").value=r.count;
}
renderMaxPlayers();
$("#saveCfgBtn").addEventListener("click",async()=>{
  if(!Native.available){toast("ᛉ Config saved · runes etched");return;}
  const name=S.profileName||S.prefs?.ProfileName||"Default";
  // fetch fresh, mutate only form-controlled fields, send the WHOLE object back
  const cur=await rpc("profiles.get",{name});
  if(cur===FAIL) return;
  // int fields: parse, fall back to the freshly-fetched previous value on NaN
  const num=(id,prev)=>{const v=parseInt($("#"+id).value,10);return Number.isNaN(v)?prev:v;};
  const prefs={...cur,
    Name:$("#fName").value.trim(),
    WorldName:$("#fWorld").value,
    Password:$("#fPassword").value,
    Port:num("fPort",cur.Port??2456),
    Public:T("tPublic"), Crossplay:T("tCrossplay"),
    SaveInterval:num("fSaveInterval",cur.SaveInterval??600),
    BackupCount:num("fBackups",cur.BackupCount??4),
    BackupIntervalShort:num("fBackShort",cur.BackupIntervalShort??600),
    BackupIntervalLong:num("fBackLong",cur.BackupIntervalLong??43200),
    WriteServerLogsToFile:T("tLogs"), AutoStart:T("tAutoStart"),
    AutoRestart:T("tCrash"), AutoRestartDelay:num("fCrashDelay",cur.AutoRestartDelay??10),
    EmptyServerRestart:T("tEmpty"), EmptyServerRestartDelayMinutes:num("fEmptyDelay",cur.EmptyServerRestartDelayMinutes??5),
    ScheduledRestart:T("tSched"), ScheduledRestartHours:num("fSchedHours",cur.ScheduledRestartHours??6),
    RconEnabled:T("tRcon"), RconPort:num("fRconPort",cur.RconPort??25575),
    RconPassword:$("#fRconPw").value,
    ServerExePath:$("#fServerExe").value.trim(),
    SaveDataFolderPath:$("#fSaveDir").value.trim(),
    AdditionalArgs:$("#fArgs").value,
  };
  const r=await rpc("profiles.save",{name,prefs});
  if(r===FAIL) return;
  // world dials ride along with Save Config, keyed to the selected world
  if(prefs.WorldName){
    const modifiers={};
    for(const [key,def] of Object.entries(WORLDGEN)){const v=$("#"+def.sel).value;if(v)modifiers[key]=v;}
    await rpc("worldgen.save",{world:prefs.WorldName,modifiers});
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
  toast("ᛉ Config saved · profile "+r.ProfileName);
  logLine("ok","[BakaLoader] profile '"+r.ProfileName+"' saved");
});
function renderAllFromPrefs(){
  if(!S.prefs) return;
  $("#hearthSub").textContent=(S.prefs.WorldName||"-")+" · dedicated server ops";
  if(S.prefs.Name) $("#tbSrv").textContent=S.prefs.Name.toUpperCase();
  $("#sbWorld").textContent=S.prefs.WorldName||"-";
  $("#sbRcon").textContent=S.prefs.RconEnabled?String(S.prefs.RconPort??25575):"off";
  $("#sbRconSeg").classList.toggle("dim",!S.prefs.RconEnabled);
  renderWorldForm();
  renderNet();
  renderHearthNative();
  try{renderEditBar();}catch(_){}
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
    world:(worldSel&&worldSel.value)||p.WorldName||"",
    port:parseInt(gv("fPort",p.Port??2456),10)||0,
    rconEnabled:$("#tRcon")?T("tRcon"):!!p.RconEnabled,
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
  const rconTxt=v.rconEnabled?("RCON "+(v.rconPort||"?")):"RCON off";
  bar.innerHTML=
    `<span class="ebedit">${esc(TT("Editing"))}</span>`+
    `<span class="ebname">${esc(v.name||v.profile||TT("this realm"))}</span>`+
    `<span class="ebmeta">${esc(TT("world"))} <b>${esc(v.world||"-")}</b> · ${esc(TT("port"))} <b>${v.port||"-"}</b> · <b>${esc(rconTxt)}</b></span>`+
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
    w.textContent="⚠ "+warnings.length+" "+TT(warnings.length===1?"conflict":"conflicts");
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
  if(!Native.available){toast(skip?"ᛉ preview · setup skipped":"ᛉ preview · setup complete");return;}
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
  toast(skip?"ᛉ Setup skipped · set paths any time in the WORLD hall"
            :"ᛉ Setup complete · may the voyage be bold");
  logLine("ok","[BakaLoader] first-time setup "+(skip?"skipped":"completed"));
}

function wizardRender(){
  const names=["WELCOME","SERVER","SAVES","WORLD","DONE"];
  const steps=`<div class="wiz-steps">`+names.map((n,i)=>
    `<span class="ws${i===WIZ.step?" on":i<WIZ.step?" done":""}"><b>${i+1}</b>${n}</span>`).join("")+`</div>`;
  const dflt=WIZ.status.defaultSavePath||"%USERPROFILE%\\AppData\\LocalLow\\IronGate\\Valheim";
  let body="",nav="";

  if(WIZ.step===0){
    body=
      `<div class="mtitle">Welcome to BakaLoader</div>`+
      `<div class="wiz-help">Before the first voyage, BakaLoader needs to know where two things live on this machine. This takes under a minute.</div>`+
      `<div class="wiz-paths">`+
      `<div><span class="r">ᛞ</span><strong>Server executable</strong> · <code>valheim_server.exe</code>, installed by the free <em>Valheim Dedicated Server</em> tool in your Steam library.</div>`+
      `<div><span class="r">ᛃ</span><strong>Save data folder</strong> · where worlds are kept. Most people keep Valheim's default.</div>`+
      `</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wSkip">Skip for now</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wNext">Begin</button>`;
  }else if(WIZ.step===1){
    body=
      `<div class="mtitle">Find the server executable</div>`+
      `<div class="wiz-help">Usually inside a Steam library at<br><code>...\\steamapps\\common\\Valheim dedicated server\\valheim_server.exe</code><br>Paste the full path below, or let BakaLoader search your drives.</div>`+
      `<div class="field"><label>Path to valheim_server.exe</label><input type="text" id="wizExe" spellcheck="false" autocomplete="off" placeholder="D:\\SteamLibrary\\steamapps\\common\\Valheim dedicated server\\valheim_server.exe"></div>`+
      `<div class="wiz-stat dim" id="wizExeStat">paste a path, or press Detect</div>`+
      `<div id="wizFound"></div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wBack">Back</button>`+
        `<button class="btn btn-ghost btn-sm" id="wDetect">ᚱ Detect</button><span class="grow"></span>`+
        `<button class="btn btn-ghost btn-sm" id="wSkip">Skip for now</button>`+
        `<button class="btn btn-ember btn-sm" id="wNext" disabled>Next</button>`;
  }else if(WIZ.step===2){
    body=
      `<div class="mtitle">Save data folder</div>`+
      `<div class="wiz-help">Where Valheim keeps worlds and player data. <strong>Leave blank to use Valheim's default</strong> (recommended):<br><code>${esc(dflt)}</code></div>`+
      `<div class="field"><label>Save data folder</label><input type="text" id="wizSave" spellcheck="false" autocomplete="off" placeholder="blank = Valheim default"></div>`+
      `<div class="wiz-stat ok" id="wizSaveStat">using the Valheim default</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wBack">Back</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wNext">Next</button>`;
  }else if(WIZ.step===3){
    body=
      `<div class="mtitle">World rules</div>`+
      `<div class="wiz-help">Valheim's world modifiers - all <strong>Normal</strong> is the classic experience. Change any of them later in the <strong>WORLD</strong> hall under <strong>WORLD MODIFIERS</strong>.</div>`+
      `<div class="formgrid">`+
      Object.entries(WORLDGEN).map(([key,def])=>
        `<div class="field"><label>${esc(def.label)}</label><select id="wizMod_${key}" data-wgkey="${key}">${wgOptions(key,WIZ.mods[key]||"")}</select></div>`).join("")+
      `</div>`+
      (WIZ.seedEligible?
        `<div class="field" style="margin-top:8px"><label>World seed</label>`+
        `<input type="text" id="wizSeed" placeholder="random (leave blank)" spellcheck="false" autocomplete="off">`+
        `<div class="wiz-help">Seed for the new world <strong>${esc(WIZ.seedWorld)}</strong> - it hasn't been created yet. Blank = random. A world's seed is <strong>fixed forever</strong> once created.</div></div>`:"")+
      `<div class="wiz-help">Max players defaults to <strong>10</strong> - Valheim's built-in cap. Raise it later in the <strong>WORLD</strong> hall under <strong>WORLD MODIFIERS</strong> (installs a small bundled server plugin).</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wBack">Back</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wNext">Next</button>`;
  }else{
    const exe=WIZ.exe.trim(),save=WIZ.save.trim();
    const modsPicked=Object.entries(WIZ.mods).filter(([,v])=>v)
      .map(([k,v])=>`${WORLDGEN[k].label.toLowerCase()} ${v}`).join(" · ");
    body=
      `<div class="mtitle">All set</div>`+
      `<div class="wiz-sum">`+
      `<div class="row"><span class="k">Server executable</span><span class="v">${esc(exe||"(not set · configure later in the WORLD hall)")}</span></div>`+
      `<div class="row"><span class="k">Save data folder</span><span class="v">${esc(save||"Valheim default")}</span></div>`+
      `<div class="row"><span class="k">World rules</span><span class="v">${esc(modsPicked||"all Normal · the classic experience")}</span></div>`+
      `</div>`+
      `<div class="wiz-help">`+TT("Everything can be changed any time in the <strong>WORLD</strong> hall. Name your server, pick a world, and sail forth.")+`</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wBack">Back</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wNext">Finish</button>`;
  }

  const m=modalOpen(steps+body+`<div class="wiz-nav">${nav}</div>`);
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
        s.className="wiz-stat bad";s.textContent="ᚦ folder not found · clear the field to use the default";
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
        stat.className="wiz-stat dim";stat.textContent="paste a path, or press Detect";
        return;
      }
      stat.className="wiz-stat dim";stat.textContent="checking…";
      t=setTimeout(async()=>{
        const ok=await wizValidate("exe",inp.value);
        WIZ.exeValid=ok;next.disabled=!ok;
        stat.className="wiz-stat "+(ok?"ok":"bad");
        stat.textContent=ok?"ᛉ found · that's the one"
                           :"ᚦ not found · the path must point at an existing valheim_server.exe";
      },250);
    };
    inp.addEventListener("input",check);
    if(WIZ.exe) check();
    setTimeout(()=>inp.focus(),30);
    on("#wDetect",async()=>{
      stat.className="wiz-stat dim";stat.textContent="searching your drives…";
      const r=Native.available?await rpc("setup.detect")
        :["C:\\Program Files (x86)\\Steam\\steamapps\\common\\Valheim dedicated server\\valheim_server.exe",
          "D:\\SteamLibrary\\steamapps\\common\\Valheim dedicated server\\valheim_server.exe"];
      const list=r!==FAIL&&Array.isArray(r)?r:[];
      const box=m.querySelector("#wizFound");
      if(!list.length){
        stat.className="wiz-stat bad";
        stat.textContent="ᚦ no install found · install the Valheim Dedicated Server tool on Steam, or paste the path by hand";
        box.innerHTML="";return;
      }
      stat.className="wiz-stat ok";
      stat.textContent="ᛉ found "+list.length+(list.length===1?" install · click to use it":" installs · click one to use it");
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
      if(!inp.value.trim()){stat.className="wiz-stat ok";stat.textContent="using the Valheim default";return;}
      stat.className="wiz-stat dim";stat.textContent="checking…";
      t=setTimeout(async()=>{
        const ok=await wizValidate("dir",inp.value);
        stat.className="wiz-stat "+(ok?"ok":"bad");
        stat.textContent=ok?"ᛉ folder found":"ᚦ folder not found · clear the field to use the default";
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
  confirmModal("Run first-time setup again?",
    `<p>This clears the saved setup answers (server executable and save folder) and reopens the guided setup.</p>`+
    `<p><strong>No server files or worlds on disk are touched.</strong></p>`,
    "Continue",()=>{
    /* confirmModal closes itself after onOk - defer the second confirm past that close */
    setTimeout(()=>{
      confirmModal("Are you certain?",
        `<p>Setup answers reset to their defaults, including any per-profile directory overrides. Your server install and worlds stay exactly where they are.</p>`,
        "Reset setup",async()=>{
        if(!Native.available){toast("ᛉ preview · setup reset");setTimeout(()=>wizardOpen({}),60);return;}
        const st=await rpc("setup.reset");
        if(st===FAIL) return;
        toast("ᛉ Setup reset · paths restored to defaults");
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
    addr:T("tHeraldAddr"),pass:T("tHeraldPass"),events:T("tHeraldEvents")
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
    toast("ᚺ preview · the Herald is set up");return;
  }
  const r=await rpc("discord.publish");
  if(r===FAIL) return;
  if(r.ok){
    HERALD_HAS_POST=true;heraldRenderPost();
    $("#heraldUrlStat").className="wiz-stat ok";
    $("#heraldUrlStat").textContent="ᛉ webhook answers"+(HWIZ.name?" · \""+HWIZ.name+"\"":"");
    toast("ᚺ The Herald speaks · status post placed in Discord");
    logLine("ok","[Herald] Discord sharing configured · status post published");
  }else{
    toast("ᚦ "+(r.error||"publish failed")+" · check the webhook in the HERALD hall");
  }
}
function heraldWizRender(){
  const names=["WELCOME","WEBHOOK","TIDINGS","PUBLISH"];
  const steps=`<div class="wiz-steps">`+names.map((n,i)=>
    `<span class="ws${i===HWIZ.step?" on":i<HWIZ.step?" done":""}"><b>${i+1}</b>${n}</span>`).join("")+`</div>`;
  let body="",nav="";

  if(HWIZ.step===0){
    body=
      `<div class="mtitle">${TT("Summon the Herald")}</div>`+
      `<div class="wiz-help">${TT("The Herald keeps <strong>one</strong> status post in a Discord channel of your choosing and quietly <strong>edits that same post</strong> whenever the realm changes. It never sends a stream of messages - your channel stays clean.")}</div>`+
      `<div class="wiz-paths">`+
      `<div><span class="r">ᛒ</span><strong>${TT("The post always carries")}</strong> · server name, status, players online, world, mod count, last mod update, next restart.</div>`+
      `<div><span class="r">ᛜ</span><strong>${TT("You choose")}</strong> · ${TT("whether the join address, the password, and one-off event posts are included.")}</div>`+
      `<div><span class="r">ᛟ</span><strong>${TT("What you need")}</strong> · ${TT("a Discord server where you can manage webhooks (or a friendly admin who can). Takes about a minute.")}</div>`+
      `</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="hwCancel">Cancel</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="hwNext">Begin</button>`;
  }else if(HWIZ.step===1){
    body=
      `<div class="mtitle">${TT("Forge the webhook")}</div>`+
      `<div class="wiz-help">${TT("A webhook is a private posting address for one channel. In <strong>Discord</strong>:")}<br>`+
      `1 · ${TT("open your Discord server and click its name → <strong>Server Settings</strong>")}<br>`+
      `2 · ${TT("go to <strong>Integrations → Webhooks → New Webhook</strong>")}<br>`+
      `3 · ${TT("name it (say, <em>BakaLoader Herald</em>) and pick the channel the post should live in")}<br>`+
      `4 · ${TT("press <strong>Copy Webhook URL</strong> and paste it below")}</div>`+
      `<div class="field"><label>Webhook URL</label><input type="text" id="hwUrl" spellcheck="false" autocomplete="off" placeholder="https://discord.com/api/webhooks/…"></div>`+
      `<div class="wiz-stat dim" id="hwUrlStat">${TT("paste the URL · the Herald will knock on it to make sure it answers")}</div>`+
      `<div class="field" style="margin-top:8px"><label>${TT("Thread ID · optional")}</label><input type="text" id="hwThread" spellcheck="false" autocomplete="off" placeholder="${TT("blank = post in the channel itself")}"></div>`+
      `<div class="wiz-help" style="margin-top:4px">${TT("Thread ID is only for posting inside a thread or forum post: right-click the thread → <strong>Copy Thread ID</strong> (needs Developer Mode, under App Settings → Advanced). Most people leave this blank.")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="hwBack">Back</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="hwNext" disabled>Next</button>`;
  }else if(HWIZ.step===2){
    body=
      `<div class="mtitle">${TT("Choose the tidings")}</div>`+
      `<div class="wiz-help">${TT("The post always shows name, status, players, world, mods and the next restart. Decide what else rides along - everything can be changed later in the <strong>HERALD</strong> hall.")}</div>`+
      `<div class="togglerow"><span class="tl">${TT("Join address (IP:port)")}</span><span class="toggle${HWIZ.addr?" on":""}" id="hwAddr" title="${esc(TT("Include the server's public IP and port so friends copy it straight from Discord."))}"></span></div>`+
      `<div class="togglerow"><span class="tl">${TT("Server password")}</span><span class="toggle${HWIZ.pass?" on":""}" id="hwPass" title="${esc(TT("Include the password in the post. Anyone who can read the channel sees it."))}"></span></div>`+
      `<div class="wiz-stat dim" id="hwPassWarn" style="color:var(--amber)">${HWIZ.pass?TT("ᚦ everyone in that channel will see the password - private channels only"):""}</div>`+
      `<div class="togglerow"><span class="tl">${TT("Event posts")}</span><span class="toggle${HWIZ.events?" on":""}" id="hwEvents" title="${esc(TT("Also send one-off messages for server start/stop/crash and player join/leave. Separate messages - not edits of the status post."))}"></span></div>`+
      `<div class="wiz-help" style="margin-top:2px">${TT("Event posts are extra one-off messages (start · stop · crash · join · leave). Leave off for the single silent status post only.")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="hwBack">Back</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="hwNext">Next</button>`;
  }else{
    body=
      `<div class="mtitle">${TT("Let the Herald speak")}</div>`+
      `<div class="wiz-sum">`+
      `<div class="row"><span class="k">Webhook</span><span class="v">${esc(HWIZ.name?("\""+HWIZ.name+"\" · answers"):"validated")}</span></div>`+
      `<div class="row"><span class="k">${TT("Thread")}</span><span class="v">${esc(HWIZ.thread.trim()||TT("none · posts in the channel itself"))}</span></div>`+
      `<div class="row"><span class="k">${TT("Join address")}</span><span class="v">${HWIZ.addr?"shared":"hidden"}</span></div>`+
      `<div class="row"><span class="k">${TT("Password")}</span><span class="v">${HWIZ.pass?TT("shared · trusted channel"):"hidden"}</span></div>`+
      `<div class="row"><span class="k">${TT("Event posts")}</span><span class="v">${HWIZ.events?"on":"off"}</span></div>`+
      `</div>`+
      `<div class="wiz-help">${TT("Finish places the first post. From then on the Herald edits that same post whenever the realm changes - you never need to touch it again.")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="hwBack">Back</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="hwNext">${TT("Publish the post")}</button>`;
  }

  const m=modalOpen(steps+body+`<div class="wiz-nav">${nav}</div>`);
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
        stat.className="wiz-stat dim";stat.textContent=TT("paste the URL · the Herald will knock on it to make sure it answers");
        return;
      }
      stat.className="wiz-stat dim";stat.textContent="checking…";
      t=setTimeout(async()=>{
        const r=Native.available?await rpc("discord.validate",{url:v})
          :{ok:HERALD_URL_RE.test(v),name:"preview-hook",error:"that doesn't look like a Discord webhook URL"};
        const ok=r!==FAIL&&!!r.ok;
        HWIZ.valid=ok;HWIZ.name=ok?(r.name||""):"";next.disabled=!ok;
        stat.className="wiz-stat "+(ok?"ok":"bad");
        stat.textContent=ok?("ᛉ "+TT("the webhook answers")+(HWIZ.name?" · \""+HWIZ.name+"\"":""))
                           :("ᚦ "+((r&&r.error)||TT("that webhook does not answer · re-copy the URL from Discord")));
      },350);
    };
    inp.addEventListener("input",check);
    if(HWIZ.url) check();
    setTimeout(()=>inp.focus(),30);
  }
  if(HWIZ.step===2){
    const flip=(id,key)=>{const el=m.querySelector(id);el.addEventListener("click",()=>{
      HWIZ[key]=!HWIZ[key];el.classList.toggle("on",HWIZ[key]);
      if(key==="pass") m.querySelector("#hwPassWarn").textContent=HWIZ.pass?TT("ᚦ everyone in that channel will see the password - private channels only"):"";
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
    toast("ᛦ "+TT("The Waystone stands")+" · "+WWIZ.domain);
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
      `<div class="mtitle">${TT("Raise a Waystone")}</div>`+
      `<div class="wiz-help">${TT("A Waystone gives your server a <strong>name</strong> - friends type <span class=\"mono\">valheim.your-domain.com</span> instead of a raw IP. The name survives IP changes: update one DNS record and every join prompt, copy chip and Discord post follows.")}</div>`+
      `<div class="wiz-paths">`+
      `<div><span class="r">ᛟ</span><strong>${TT("What you need")}</strong> · ${TT("a domain you own (any registrar), or a free dynamic-DNS name (DuckDNS, No-IP). Nothing is bought or installed here - this wizard only teaches your DNS to point at this server.")}</div>`+
      `<div><span class="r">ᚦ</span><strong>${TT("One honest limit")}</strong> · ${TT("Valheim resolves the name but has <strong>no SRV support</strong>, so the port always travels with it: friends join with")} <span class="mono">${esc("name:"+port)}</span>.</div>`+
      `<div><span class="r">ᛜ</span><strong>${TT("Where it shows")}</strong> · ${TT("Sail Forth join prompts, the Network card, Copy join address, and the Herald's Discord post all prefer the name once it's raised.")}</div>`+
      `</div>`+
      (S.domain?`<div class="wiz-stat dim" style="margin-top:8px">${TT("current Waystone")} · <span class="mono">${esc(S.domain)}</span></div>`:"");
    nav=`<button class="btn btn-ghost btn-sm" id="wwCancel">Cancel</button>`+
        (S.domain?`<button class="btn btn-ghost btn-sm" id="wwRemove" title="${esc(TT("Lower the Waystone - join surfaces go back to showing the raw public IP. Your DNS record is untouched."))}">${TT("Remove")}</button>`:"")+
        `<span class="grow"></span><button class="btn btn-ember btn-sm" id="wwNext">Begin</button>`;
  }else if(WWIZ.step===1){
    body=
      `<div class="mtitle">${TT("Name the Waystone")}</div>`+
      `<div class="wiz-help">${TT("Pick the full name friends will type. A <strong>subdomain</strong> keeps your main site untouched - <span class=\"mono\">valheim.example.com</span> rather than <span class=\"mono\">example.com</span>. Dynamic-DNS users: paste the name your provider gave you, like <span class=\"mono\">mysaga.duckdns.org</span>.")}</div>`+
      `<div class="field"><label>${TT("Domain name")}</label><input type="text" id="wwDomain" spellcheck="false" autocomplete="off" placeholder="valheim.example.com" title="${esc(TT("Just the name - no https://, no port. Pasting a full URL is fine; it will be trimmed."))}"></div>`+
      `<div class="wiz-stat dim" id="wwDomStat">${TT("type the name · letters, digits and hyphens, with at least one dot")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wwBack">Back</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wwNext" disabled>Next</button>`;
  }else if(WWIZ.step===2){
    body=
      `<div class="mtitle">${TT("Point the name at this server")}</div>`+
      `<div class="wiz-help">${TT("Teach DNS that")} <span class="mono">${esc(WWIZ.domain)}</span> ${TT("lives at this server's public IP:")} <span class="mono">${esc(myIp)}</span></div>`+
      `<div class="wiz-paths">`+
      `<div><span class="r">ᚨ</span><strong>${TT("Own domain · add an A record")}</strong><br>`+
      `1 · ${TT("open your DNS provider's dashboard (Cloudflare, Namecheap, Porkbun, wherever the domain lives)")}<br>`+
      `2 · ${TT("add a record: <strong>Type</strong> A · <strong>Name/Host</strong> the subdomain part (for <span class=\"mono\">valheim.example.com</span> enter <span class=\"mono\">valheim</span>) · <strong>Value</strong>")} <span class="mono">${esc(myIp)}</span><br>`+
      `3 · ${TT("<strong>TTL</strong> 300-3600 s is fine · on Cloudflare set the cloud to <strong>DNS only</strong> (grey) - the orange proxy does not carry game traffic")}</div>`+
      `<div><span class="r">ᛉ</span><strong>${TT("Home connection · IP changes? use dynamic DNS")}</strong><br>`+
      `· ${TT("<strong>DuckDNS</strong> (free): claim <span class=\"mono\">yourname.duckdns.org</span>, run their tiny updater so the record follows your IP")}<br>`+
      `· ${TT("<strong>No-IP</strong> or your <strong>router's built-in DDNS</strong> page work the same way")}<br>`+
      `· ${TT("own a domain AND have a changing IP? point a CNAME at your dynamic-DNS name")}</div>`+
      `<div><span class="r">ᚦ</span><strong>${TT("Remember")}</strong> · ${TT("the name only replaces the IP - friends still need the port")} (<span class="mono">${esc(String(port))}</span>)${TT(", and port-forwarding stays exactly as it is today.")}</div>`+
      `</div>`+
      `<div class="wiz-help" style="margin-top:6px">${TT("New records usually answer in a minute or two; some resolvers take up to an hour. The next step checks it live - you can finish either way and re-check later.")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wwBack">Back</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wwNext">${TT("Prove it")}</button>`;
  }else{
    body=
      `<div class="mtitle">${TT("Prove the Waystone")}</div>`+
      `<div class="wiz-help">${TT("BakaLoader asks DNS for")} <span class="mono">${esc(WWIZ.domain)}</span> ${TT("and compares the answer with this server's public IP.")}</div>`+
      `<div class="wiz-stat dim" id="wwCheckStat">${TT("asking the name-servers…")}</div>`+
      `<div class="wiz-sum" id="wwCheckSum" style="display:none"></div>`+
      `<div class="wiz-help" style="margin-top:6px">${TT("A fresh record can lag behind - if it doesn't resolve yet you can still raise the Waystone now and it starts working the moment DNS catches up.")}</div>`;
    nav=`<button class="btn btn-ghost btn-sm" id="wwBack">Back</button>`+
        `<button class="btn btn-ghost btn-sm" id="wwAgain">${TT("Check again")}</button><span class="grow"></span>`+
        `<button class="btn btn-ember btn-sm" id="wwNext">${TT("Raise the Waystone")}</button>`;
  }

  const m=modalOpen(steps+body+`<div class="wiz-nav">${nav}</div>`);
  m.classList.add("wiz");
  const on=(sel,fn)=>{const el=m.querySelector(sel);if(el)el.addEventListener("click",fn);};
  on("#wwCancel",modalClose);
  on("#wwBack",()=>{WWIZ.step--;waystoneWizRender();});
  on("#wwRemove",async()=>{
    modalClose();
    if(await waystoneSave("")){
      toast("ᛦ "+TT("The Waystone falls")+" · "+TT("back to the raw IP"));
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
          stat.className="wiz-stat dim";stat.textContent=TT("type the name · letters, digits and hyphens, with at least one dot");
          return;
        }
        const ok=WAYSTONE_HOST_RE.test(v);
        WWIZ.syntaxOk=ok;next.disabled=!ok;
        stat.className="wiz-stat "+(ok?"ok":"bad");
        stat.textContent=ok?("ᛉ "+TT("a well-formed name")+" · "+v)
                           :("ᚦ "+TT("not a valid hostname - needs a dot, no spaces, no ports, like valheim.example.com"));
      },350);
    };
    inp.addEventListener("input",check);
    if(WWIZ.domain) check();
    setTimeout(()=>inp.focus(),30);
  }
  if(WWIZ.step===3){
    const stat=m.querySelector("#wwCheckStat"),sum=m.querySelector("#wwCheckSum");
    const runCheck=async()=>{
      stat.className="wiz-stat dim";stat.style.color="";stat.textContent=TT("asking the name-servers…");
      sum.style.display="none";
      const r=Native.available?await rpc("domain.check",{domain:WWIZ.domain})
        :await new Promise(res=>setTimeout(()=>res({ok:true,ips:[myIp],publicIp:myIp,match:true}),600));
      if(r===FAIL){stat.className="wiz-stat bad";stat.textContent="ᚦ "+TT("the check itself failed - you can still raise the Waystone");return;}
      WWIZ.res=r;
      if(r.ok&&r.match){
        stat.className="wiz-stat ok";
        stat.textContent="ᛉ "+TT("the name answers with this server's IP - perfect");
      }else if(r.ok){
        stat.className="wiz-stat";stat.style.color="var(--amber)";
        stat.textContent="ᚦ "+TT("the name resolves, but not to this server's public IP - a stale record, a proxy (orange cloud?), or propagation still in flight");
      }else{
        stat.className="wiz-stat bad";
        stat.textContent="ᚦ "+((r.error&&TT(r.error))||TT("the name does not resolve yet"))+" · "+TT("give DNS a few minutes, then Check again");
      }
      sum.style.display="";
      sum.innerHTML=
        `<div class="row"><span class="k">${TT("Name")}</span><span class="v mono">${esc(WWIZ.domain)}</span></div>`+
        `<div class="row"><span class="k">${TT("Resolves to")}</span><span class="v mono">${esc((r.ips&&r.ips.length?r.ips.join(" · "):"-"))}</span></div>`+
        `<div class="row"><span class="k">${TT("This server")}</span><span class="v mono">${esc(r.publicIp||myIp)}</span></div>`+
        `<div class="row"><span class="k">${TT("Friends join with")}</span><span class="v mono">${esc(WWIZ.domain+":"+port)}</span></div>`;
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
function renderCfgList(){
  $("#runesSub").textContent=TT(CFG.files.length+" rune-scroll"+(CFG.files.length===1?"":"s")+" in the config vault");
  $("#cfgList").innerHTML=CFG.files.map(f=>
    `<div class="cfg-item${f===CFG.file?" sel":""}" data-f="${esc(f)}"><span class="cfgname">${esc(f)}</span>${(f===CFG.file&&CFG.dirty)?'<span class="dot"></span>':""}</div>`
  ).join("")||`<div class="cfg-item" style="opacity:.5;cursor:default"><span class="cfgname">${TT("no .cfg scrolls found")}</span></div>`;
}
function setCfgDirty(d){
  if(CFG.dirty===d) return;
  CFG.dirty=d;
  $("#cfgSaveBtn").disabled=!d||!CFG.file;
  renderCfgList();
}
async function loadCfg(f,force){
  if(!f||(f===CFG.file&&!force)) return;
  if(CFG.dirty&&!force){
    confirmModal("Discard changes?",
      `<b>${esc(CFG.file)}</b> ${TT("has unsaved rune-work.")} Abandon it and open <b>${esc(f)}</b>?`,
      "Discard",()=>loadCfg(f,true));
    return;
  }
  const text=await cfgRead(f);
  if(text===null) return;
  CFG.file=f; CFG.dirty=false;
  $("#cfgEditor").value=text;
  $("#cfgSaveBtn").disabled=true;
  resetCfgSaveBtn();
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
  }else if(reRead&&CFG.file&&!CFG.dirty){
    const text=await cfgRead(CFG.file);
    if(text!==null) $("#cfgEditor").value=text;
  }
  renderCfgList();
}
let cfgConfirmT=null;
function resetCfgSaveBtn(){
  clearTimeout(cfgConfirmT); cfgConfirmT=null;
  const b=$("#cfgSaveBtn");
  b.classList.remove("confirming");
  b.innerHTML="ᛉ&nbsp; Save";
}
$("#cfgList").addEventListener("click",e=>{
  const it=e.target.closest(".cfg-item[data-f]");
  if(it) loadCfg(it.dataset.f,false);
});
$("#cfgEditor").addEventListener("input",()=>{if(CFG.file)setCfgDirty(true);});
$("#cfgReloadBtn").addEventListener("click",()=>{
  const go=async()=>{
    CFG.dirty=false;
    await refreshCfgList(true);
    if(CFG.file){const t=await cfgRead(CFG.file);if(t!==null)$("#cfgEditor").value=t;}
    $("#cfgSaveBtn").disabled=true; resetCfgSaveBtn(); renderCfgList();
    toast("ᛋ Scrolls reloaded");
  };
  if(CFG.dirty){
    confirmModal("Discard changes?",
      `<b>${esc(CFG.file)}</b> ${TT("has unsaved rune-work.")} Reload from disk anyway?`,
      "Discard & reload",go);
  }else go();
});
$("#cfgSaveBtn").addEventListener("click",async()=>{
  if(!CFG.file||!CFG.dirty) return;
  const b=$("#cfgSaveBtn");
  if(!b.classList.contains("confirming")){
    b.classList.add("confirming");
    b.innerHTML="ᛉ&nbsp; Confirm save?";
    cfgConfirmT=setTimeout(resetCfgSaveBtn,3000);
    return;
  }
  resetCfgSaveBtn();
  const ok=await cfgWrite(CFG.file,$("#cfgEditor").value);
  if(!ok) return;
  setCfgDirty(false);
  $("#cfgSaveBtn").disabled=true;
  toast("ᛉ Rune etched · "+CFG.file+" saved");
  logLine("ok","[BakaLoader] config '"+CFG.file+"' written");
});
$("#cfgOpenBtn").addEventListener("click",()=>{
  if(!Native.available){toast("ᛃ Preview · config folder opens in the app");return;}
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
  layers:{portals:true,pois:true,builds:true,pins:false,fog:true},
  fogWipe:null,                                   // {to,r} while the fog toggle wipes concentrically from world center
  cam:{cx:0,cz:0,ppm:0},                          // ppm = screen px per world meter
  drag:null,
};
function atlasWorldName(){return (S.prefs&&S.prefs.WorldName)||null;}
function atlasMsg(text){
  const el=$("#atlasMsg"); if(!el) return;
  if(text==null){el.classList.add("hidden");return;}
  el.textContent=text; el.classList.remove("hidden");
}
function atlasReset(){
  ATLAS.world=null; ATLAS.mapImg=null; ATLAS.mapReady=false;
  ATLAS.fogImg=null; ATLAS.fogReady=false; ATLAS.fogTex=null; ATLAS.fogMask=null; ATLAS.info=null;
  ATLAS.cam.ppm=0; ATLAS.seq++;
  atlasMsg("Loading the known world…");
  if(currentPage==="atlas") atlasEnter();
}
async function atlasEnter(){
  atlasResize();
  if(!Native.available){atlasMock();return;}
  const world=atlasWorldName();
  $("#atlasWorldName").textContent=world||"—";
  if(!world){atlasMsg("No world chosen yet — pick one in the World hall.");return;}
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
  atlasMsg("Charting the realm from its seed…");
  const r=await rpc("atlas.render",{world,size:2048,force:!!force});
  ATLAS.rendering=false;
  if(seq!==ATLAS.seq) return;
  if(r===FAIL||!r||!r.url){
    atlasMsg("The map could not be drawn — set a seed or start the server once first.");
    return;
  }
  ATLAS.mapExtent=Number(r.edge)||10500;
  const warn=$("#atlasModWarn");
  if(warn){
    if(r.warnings&&r.warnings.length){
      warn.style.display=""; warn.textContent="⚠ worldgen mods"; warn.title=r.warnings.join("\n");
    }else warn.style.display="none";
  }
  const img=new Image();
  img.onload=()=>{
    if(seq!==ATLAS.seq) return;
    ATLAS.mapImg=img; ATLAS.mapReady=true; atlasMsg(null);
    if(!ATLAS.cam.ppm) atlasFit();
    atlasDraw();
  };
  img.onerror=()=>{if(seq===ATLAS.seq)atlasMsg("Map image failed to load.");};
  img.src=r.url+"?t="+Date.now(); // bust the WebView2 cache after re-renders
}
async function atlasRefreshInfo(){
  const world=ATLAS.world; if(!world) return;
  const seq=ATLAS.seq;
  const r=await Native.call("atlas.worldInfo",{world}).catch(()=>null);
  if(seq!==ATLAS.seq||!r) return;
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
function renderAtlasSide(){
  const info=ATLAS.info;
  $("#atlasWorldName").textContent=ATLAS.world||"—";
  if(!info||!info.hasDb){
    $("#atlasDay").textContent="—";
    $("#atlasClock").textContent="no save file yet — the clock starts with the first launch";
    $("#atlasSaved").textContent="—"; $("#atlasExplored").textContent="—"; $("#atlasEvent").textContent="—";
  }else{
    $("#atlasDay").textContent="Day "+info.day;
    const frac=((Number(info.netTime)%1800)+1800)%1800/1800;
    const mins=Math.floor(frac*24*60);
    $("#atlasClock").textContent="in-game "+String(Math.floor(mins/60)).padStart(2,"0")+":"+String(mins%60).padStart(2,"0")+" at last save";
    $("#atlasSaved").textContent=atlasAge(info.savedAgeSeconds);
    $("#atlasExplored").textContent=info.hasSharedMap
      ? info.exploredPercent.toFixed(1)+"% ("+info.mapTables+(info.mapTables===1?" table)":" tables)")
      : "no shared map yet";
    $("#atlasEvent").textContent=info.eventName?info.eventName:"quiet skies";
  }
  /* roster: who's online (mod-free maps can't place them) */
  const roster=$("#atlasRoster");
  if(roster){
    const online=(S.players||[]).filter(p=>p.status==="Online");
    roster.innerHTML=online.map(p=>
      `<div class="vrow"><span class="vdot on"></span><span class="vname">${esc(p.displayName)}</span></div>`
    ).join("")||`<div class="empty">${TT("No vikings ashore right now.")}</div>`;
  }
  /* waypoints: altars & traders, then portals, then table pins */
  const wp=$("#atlasWaypoints");
  if(wp){
    const rows=[];
    if(info&&info.hasDb){
      (info.pois||[]).forEach(l=>rows.push({r:"ᛒ",n:l.label,x:l.x,z:l.z}));
      (info.portals||[]).forEach(p=>rows.push({r:"ᛈ",n:p.tag||"(untagged)",x:p.x,z:p.z}));
      (info.pins||[]).forEach(p=>rows.push({r:"ᛘ",n:p.name||"(pin)",x:p.x,z:p.z}));
    }
    wp.innerHTML=rows.map((w,i)=>
      `<div class="wp" data-i="${i}"><span class="wr">${w.r}</span><span class="wn">${esc(w.n)}</span><span class="wc">${Math.round(w.x)}, ${Math.round(w.z)}</span></div>`
    ).join("")||`<div class="empty">${TT("Waypoints appear once the world has been saved.")}</div>`;
    wp._rows=rows;
  }
  renderWeather();
}
function atlasAge(sec){
  sec=Number(sec)||0;
  if(sec<90) return Math.round(sec)+"s ago";
  if(sec<5400) return Math.round(sec/60)+" min ago";
  if(sec<172800) return (sec/3600).toFixed(1)+" h ago";
  return Math.round(sec/86400)+" days ago";
}
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
function wxCompass(deg){
  const n=["N","NNE","NE","ENE","E","ESE","SE","SSE","S","SSW","SW","WSW","W","WNW","NW","NNW"];
  return n[Math.round(deg/22.5)%16];
}
/* WX-ENGINE-END */
const WX_LABEL={Clear:"Clear skies",Rain:"Rain",Misty:"Mist",ThunderStorm:"Thunderstorm",
  LightRain:"Light rain",DeepForest_Mist:"Forest mist",SwampRain:"Swamp drizzle",Snow:"Snowfall",
  SnowStorm:"Blizzard",Heath_clear:"Clear heath winds",Twilight_Clear:"Cold and clear",
  Twilight_Snow:"Driving snow",Twilight_SnowStorm:"Polar storm",Mistlands_clear:"Still mists",
  Mistlands_rain:"Misty rain",Mistlands_thunder:"Mistland thunder",Ashlands_ashrain:"Ash rain",
  Ashlands_misty:"Ash haze",Ashlands_CinderRain:"Cinder rain",Ashlands_storm:"Firestorm"};
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
  $("#wxNow").innerHTML=`<span class="wxfr">${WX_RUNE[env]||"ᛋ"}</span>${esc(TT(WX_LABEL[env]||env))}`;
  $("#wxArrow").style.transform="rotate("+Math.round(wind.deg)+"deg)";
  $("#wxWind").textContent=wxCompass(wind.deg)+" · "+Math.round(wind.intensity*100)+"%";
  const period=Math.floor(nt.t/666),today=Math.floor(nt.t/1800),rows=[];
  for(let i=1;i<=5;i++){
    const pt=(period+i)*666;
    const e=wxEnvAt(pt,biome);
    const w=wxWindAt(pt,e);
    rows.push(`<div class="wxf"><span class="wxft">${wxClockLabel(pt,today)}</span>`+
      `<span class="wxfr">${WX_RUNE[e]||""}</span><span class="wxfe">${esc(TT(WX_LABEL[e]||e))}</span>`+
      `<span class="wxfw">${Math.round(w.intensity*100)}%</span></div>`);
  }
  $("#wxForecast").innerHTML=rows.join("");
  $("#wxAnchor").textContent=nt.paused
    ?TT("Time stands still — no vikings ashore.")
    :TT("Anchored to the last world save.");
}
/* biome picker pills */
(function(){
  const bs=$("#wxBiomes"); if(!bs) return;
  const BIOMES=[["Meadows","Meadows"],["BlackForest","Black Forest"],["Swamp","Swamp"],
    ["Mountain","Mountain"],["Plains","Plains"],["Mistlands","Mistlands"],
    ["Ashlands","Ashlands"],["DeepNorth","Deep North"],["Ocean","Ocean"]];
  bs.innerHTML=BIOMES.map(b=>`<span class="wxb${b[0]==="Meadows"?" on":""}" data-b="${b[0]}">${b[1]}</span>`).join("");
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
/* The realm is a 10.5km disc - the cloud veil should sit over the world only,
   thinning to nothing past the rim. destination-in with a radial gradient
   feathers the veil's edge into a soft circular cutout. extentM = world meters
   from the canvas centre to its edge. */
function atlasVeilFeather(g,w,h,extentM){
  const cx=w/2,cy=h/2;
  const r1=10500/extentM*(w/2);                 // world rim in canvas px
  const r0=Math.max(0,9300/extentM*(w/2));      // fade starts inside the rim
  const grad=g.createRadialGradient(cx,cy,r0,cx,cy,r1);
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
        g.fillText("ᚾ unexplored — no cartography table has shared the realm yet",s.w/2,s.h/2);
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
  if(z) z.textContent=Math.round(1/c.ppm)+" m/px";
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
    toast(TT("ᚾ No shared map yet — the realm stays veiled until a viking presses 'Record discoveries' on a cartography table."));
  }
  if(layer==="pins"&&turningOn&&Native.available&&ATLAS.info&&ATLAS.info.hasDb&&!(ATLAS.info.pins||[]).length){
    toast(TT("ᛘ No table pins yet — pins ride the cartography table's shared map."));
    return;
  }
  ATLAS.layers[layer]=turningOn;
  ch.classList.toggle("on",turningOn);
  if(layer==="fog"&&ATLAS.mapReady){atlasFogWipe(turningOn);return;}
  atlasDraw();
}));
$("#atlasRecenter").addEventListener("click",()=>{atlasFit();atlasDraw();});
$("#atlasRedraw").addEventListener("click",()=>{
  if(!Native.available){toast("ᛞ Preview · the real map renders in the app");return;}
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
    toast("ᛉ World saved · "+ms+"ms");
    $("#lastSave").textContent=clock()+" · "+ms+"ms";
    S.lastSaveAt=new Date();
    S.saveDur.push(ms); if(S.saveDur.length>10)S.saveDur.shift();
    const avg=S.saveDur.reduce((a,b)=>a+b,0)/S.saveDur.length;
    $("#saveAvg").textContent="avg "+Math.round(avg)+"ms";
    S.saveSec=S.saveInterval;
    if(currentPage==="atlas") atlasRefreshInfo(); // fresh .db on disk = fresh atlas facts/fog
  });
  Native.on("atlas.renderProgress",d=>{
    if(currentPage==="atlas"&&d&&d.world===ATLAS.world&&ATLAS.rendering)
      atlasMsg("Charting the realm from its seed… "+d.pct+"%");
  });
  Native.on("server.inviteCode",d=>{
    if(!d?.code||!isActiveProfile(d?.profile)) return;
    S.invite=d.code; renderNet();
    toast("ᛟ Crossplay invite · "+d.code);
    logLine("info","[BakaLoader] crossplay invite code: "+d.code);
  });
  Native.on("server.crashed",d=>{
    // Crashes always toast, even for background servers - name the culprit.
    const who=d?.profile&&!isActiveProfile(d.profile)?" · "+d.profile:"";
    toast(TT("ᚦ Server crashed · consult the saga")+who);
    if(isActiveProfile(d?.profile)) logLine("err","[BakaLoader] server process crashed");
  });
  Native.on("server.countdown",d=>{if(d?.message&&isActiveProfile(d?.profile))toast("ᚨ "+d.message);});
  Native.on("player.updated",p=>{
    if(!p) return;
    if(!isActiveProfile(p.serverKey)) return; // another server's viking
    const i=S.players.findIndex(x=>x.key===p.key);
    if(i>=0) S.players[i]=p; else S.players.push(p);
    renderPlayers();
  });
  Native.on("ip.external",d=>{S.extIp=d?.ip??null;renderNet();});
  Native.on("ip.internal",d=>{S.intIp=d?.ip??null;renderNet();});
  Native.on("profiles.changed",()=>{refreshServers();});
  Native.on("servers.changed",d=>{if(Array.isArray(d)){S.servers=d;renderServerChips();}});
  /* Skald hall keeps itself fresh while it is the page on screen */
  Native.on("server.status",()=>{if(currentPage==="skald")skaldRefresh();});
  Native.on("server.playerDied",d=>{
    if(currentPage==="skald"&&isActiveProfile(d?.profile))skaldRefresh();
  });
  Native.on("player.updated",p=>{
    if(currentPage==="skald"&&isActiveProfile(p?.serverKey))skaldRefresh();
  });
  Native.on("log.app",d=>{if(d?.line!=null)logLine(classifyLog(d.line),d.line);});
  Native.on("log.server",d=>{
    if(d?.line==null||!isActiveProfile(d?.profile)) return;
    logLine(classifyLog(d.line),d.line);
    parseNetLine(d.line); // net telemetry rides the server log
  });

  /* --- boot sequence --- */
  (async function bootNative(){
    /* placeholders until real data lands */
    $("#lastSave").textContent="-";
    $("#saveCountdown").textContent="-:-";
    $("#tickVal").textContent="-";
    $("#sbMods").textContent="- mods";
    tIn.placeholder="console command… (Enter to send)";
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
    prio.title="BakaLoader always boosts the server process to AboveNormal";
    prio.insertAdjacentHTML("afterend",`<div class="fieldnote">always AboveNormal - boosted by BakaLoader</div>`);

    const info=await rpc("app.info");
    if(info!==FAIL&&info){
      S.version=info.version||"";
      $("#sbVersion").textContent="v"+S.version;
      $("#sideVer").textContent="v"+S.version;
    }
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
    if(buf!==FAIL&&Array.isArray(buf)) buf.forEach(l=>logLine(classifyLog(l),l));
    await refreshPlayers();
    renderAllFromPrefs();
    renderMods();
    renderHearthNative();
    await initUpkeep();

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
  setT("tHeraldAddr",true); // herald preview mirrors the C# default (address shared, rest off)

  /* multi-server chip strip preview */
  S.servers=[
    {name:"Final Sunset",status:"Running",running:true,playersOnline:2,active:true},
    {name:"Midgard Test",status:"Stopped",running:false,playersOnline:0,active:false},
  ];
  renderServerChips();

  /* first-launch wizard preview: open index.html#wizard to walk the panes */
  if(location.hash==="#wizard"){
    setTimeout(()=>wizardOpen({
      setupCompleted:false,exeValid:false,saveValid:true,
      defaultExePath:"%ProgramFiles(x86)%\\Steam\\steamapps\\common\\Valheim dedicated server\\valheim_server.exe",
      defaultSavePath:"%USERPROFILE%\\AppData\\LocalLow\\IronGate\\Valheim",
    }),150);
  }

  /* mock DOM seeding (tables ship empty in index.html; native fills them from RPCs) */
  $("#homeVik").innerHTML=
    `<div class="vrow"><span class="vdot on"></span><span class="vname">Smithix</span><span class="vsub">joined 22:12</span></div>`+
    `<div class="vrow"><span class="vdot on"></span><span class="vname">Van Hoenhiem</span><span class="vsub">joined 22:14</span></div>`+
    `<div class="vrow" style="opacity:.4"><span class="vdot off"></span><span class="vname">Ragnhild</span><span class="vsub">last seen 19:40</span></div>`;
  $("#vikTable").innerHTML=
    `<tr>
      <td><span class="vdot on" style="display:inline-block;margin-right:9px"></span><strong>Smithix</strong></td>
      <td><span class="pill green">Online</span></td>
      <td class="mono">22:12</td>
      <td class="mono">3959, −1361, 35</td>
      <td class="mono" style="color:var(--ember)">⋯</td>
    </tr>
    <tr style="background:var(--ember-dim)">
      <td><span class="vdot on" style="display:inline-block;margin-right:9px"></span><strong>Van Hoenhiem</strong></td>
      <td><span class="pill green">Online</span></td>
      <td class="mono">22:14</td>
      <td class="mono">−212, 887, 41</td>
      <td class="mono" style="color:var(--ember)">⋯</td>
    </tr>
    <tr class="dim">
      <td><span class="vdot off" style="display:inline-block;margin-right:9px"></span>Ragnhild</td>
      <td><span class="pill blue">Offline</span></td>
      <td class="mono">-</td>
      <td class="mono">last seen 19:40</td><td></td>
    </tr>
    <tr class="dim">
      <td><span class="vdot off" style="display:inline-block;margin-right:9px"></span>Bjornulf</td>
      <td><span class="pill blue">Offline</span></td>
      <td class="mono">-</td>
      <td class="mono">last seen Tue</td><td></td>
    </tr>`;
  S.mods=[
    {ModName:"WorldEditCommands",Author:"JereKuusela",FullName:"JereKuusela-WorldEditCommands",InstalledVersion:"1.65.0",LatestVersion:"1.66.0",UpdateAvailable:true},
    {ModName:"ExtraSlots",Author:"shudnal",FullName:"shudnal-ExtraSlots",InstalledVersion:"1.0.20",LatestVersion:"1.0.22",UpdateAvailable:true},
    {ModName:"EpicLoot",Author:"RandyKnapp",FullName:"RandyKnapp-EpicLoot",InstalledVersion:"0.11.3",LatestVersion:"0.11.3"},
    {ModName:"ComfyMods-Gizmo",Author:"ComfyMods",FullName:"ComfyMods-Gizmo",InstalledVersion:"1.15.0",LatestVersion:"1.15.0"},
    {ModName:"PlantEverything",Author:"Advize",FullName:"Advize-PlantEverything",InstalledVersion:"1.18.2",LatestVersion:"1.18.2"},
    {ModName:"SearsCatalog",Author:"ComfyMods",FullName:"ComfyMods-SearsCatalog",InstalledVersion:"1.4.0",LatestVersion:"1.4.0"},
    {ModName:"PlanBuild",Author:"MathiasDecrock",FullName:"MathiasDecrock-PlanBuild",InstalledVersion:"0.16.4",LatestVersion:"0.16.4"},
    {ModName:"BakaLoaderSpawnHelper",Author:"BakaLoader",FullName:"BakaLoader-BakaLoaderSpawnHelper",InstalledVersion:"1.1.0",LatestVersion:"-",Bundled:true},
    {ModName:"Rcon_Commands",Author:"JereKuusela",FullName:"JereKuusela-Rcon_Commands",InstalledVersion:"1.12.0",LatestVersion:"1.12.0"},
    {ModName:"ValheimOptimizer",Author:"Dreous",FullName:"Dreous-ValheimOptimizer",InstalledVersion:"1.2.1",LatestVersion:"1.2.1"},
  ];
  S.modsScanned=true; S.lastScan="21:38";
  renderMods();
  $("#fWorld").innerHTML=`<option>Final Sunset</option>`;

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

  /* uptime tick */
  setInterval(()=>{ if(running){upMin++;renderHearth();} },60000);

  /* save countdown */
  let saveSec=252; // 04:12
  setInterval(()=>{
    if(!running) return;
    saveSec--;
    if(saveSec<0){
      saveSec=600;
      toast("ᛉ World saved · "+clock());
      logLine("ok","World saved ( Final_Sunset.db )  8.42 MB  in 214 ms");
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
  setTimeout(()=>toast("ᚱ 2 mod updates available · WorldEditCommands, ExtraSlots"),2500);

  /* saga seed + chatter */
  const seed=[
   ["info","[BepInEx] 63 plugins loaded in 4.21 s"],
   ["ok","World loaded ( Final_Sunset )  ZDOs: 412,882"],
   ["net","[Steam] game server connected, public IP 203.0.113.42"],
   ["ok","RCON bound on 127.0.0.1:25575"],
   ["info","Smithix has arrived - spawned at 3959, -1361, 35"],
   ["info","Van Hoenhiem has arrived - spawned at -212, 887, 41"],
   ["warn","[ValheimOptimizer] frame budget exceeded 18.4 ms (single spike)"],
   ["ok","World saved ( Final_Sunset.db )  8.39 MB  in 198 ms"],
  ];
  seed.forEach(([k,t])=>logLine(k,t));
  const chatter=[
   ["info",()=>"ZDO sync: "+ (412000+Math.floor(Math.random()*2000)).toLocaleString() +" active, "+Math.floor(Math.random()*40+8)+" dirty"],
   ["net",()=>"[Steam] socket recv "+(Math.random()*42+6).toFixed(1)+" KB/s · send "+(Math.random()*18+3).toFixed(1)+" KB/s"],
   ["ok",()=>"World saved ( Final_Sunset.db )  8.4"+Math.floor(Math.random()*9)+" MB  in "+(180+Math.floor(Math.random()*90))+" ms"],
   ["info",()=>["Smithix hauled 30 iron across the swamp","Van Hoenhiem tamed a lox","Wind shifted - sailing weather fair","Smithix has arrived","Raven event rolled: none"][Math.floor(Math.random()*5)]],
   ["warn",()=>"[ExtraSlots] config reloaded from memory snapshot"],
   ["net",()=>"[RCON] keepalive ok · 127.0.0.1:25575"],
  ];
  setInterval(()=>{ if(!running) return;
    const [k,f]=chatter[Math.floor(Math.random()*chatter.length)]; logLine(k,f());
  },2000);
}
