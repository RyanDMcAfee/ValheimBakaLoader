# The motion sweep (1.2.18)

The owner's standing rule, 2026-10-21: nothing on the page may appear, vanish or jump when a
toggle, checkbox, button or menu is clicked. A part that shows eases in from the direction the
layout shifts (rolls down) or fades in; a part that hides rolls up or fades out; everything it
pushes moves with it. 0.2 s, ease-out. Under prefers-reduced-motion a plain 0.2 s fade, no
movement.

1.2.17 added the one helper for this, `reveal(el, show, opts)` (the REVEAL block at the top of
`ValheimBakaLoader/WebUI/app.js`), and used it on the Discord screen. This is the record of the
sweep that put it under the rest of the app. Line numbers are those of `app.js` at the commit
that carries this file; the selftest `scripts/ui/motion_sweep_selftest.js` (copy gate 64) holds
the list of places left alone against this record by name rather than by line.

## What the sweep added to the helper

- `{display:"flex"}`: a box whose shown layout is not the stylesheet's own (none are left after
  the sweep, since every bar that was set to flex by hand is flex in the stylesheet now, but
  the option stays for the next one). Remembered on the box so it rolls away laid out the same
  way.
- `{now:true}`: the box is set with no roll. Used for the half of a tab swap that has to leave
  before the other half can take its place.
- `{done}`: runs once the box is where it was asked to be, after the roll or at once when there
  is none, and never when a later call turns the roll around. Used wherever a hide used to
  empty the box at the same time: the words stay while it rolls away and go once it has gone.
- `fadeGhost(el)`: for a thing that has to go at once because whatever runs on its dismissal
  must see it gone (a dialog, a menu, the palette, the hover panel, the unsaved band). A copy
  with no ids, names or label targets stands in its place for 0.2 s and fades
  (`.reveal-ghost` in app.css). `modalFadeOut` is now this, for every dialog; the
  `data-fade-out` opt-in from 1.2.17 is gone.

## Converted

Each line: where, what it is, how it moves now.

| app.js | what | now |
| --- | --- | --- |
| 1240 `langMenuClose` | the globe (language) menu | fades in by the stylesheet (`.langmenu.open`, 0.2 s), leaves through fadeGhost |
| 1998 `wireCollapsible` | the Upkeep and Connection cards and the three folding Settings sections | the body rolls through reveal; the `open` class only turns the chevron. The bodies start hidden in index.html (`style="display:none"`), and the class rules that hid them by display are gone from app.css. The bento row keeps its content height until a card has rolled shut (`syncBentoRow`), so it never snaps |
| 2032 `syncDirsSection` | the Directories section unfolding by itself for a path problem | through the same `rec.set(true)` the click uses |
| 2345 `renderTestCopyHead` | the title bar's test-copy line | fade (a span in a row) |
| 2715 `dupMark` | the source value under a dial in the Duplicate realm dialog | rolls; the words go after the roll |
| 2833, 3069, 3102 | the locked-listing note and the copy notes in the realm dialogs | roll |
| 3649, 3652 `renderHearthVersion` | the Server card's version line | fade |
| 3998, 4003 `renderSaveBars` | the saves chart's caption | rolls |
| 4131, 4150, 4153 `renderWaystone`/`renderNet` | the Network card's domain, invite and RCON lines | roll |
| 4296, 4297 `renderAppUpdatePill` | the BakaLoader update pill | fade |
| 4349 `appUpdateModal` | the "why the button cannot work" line | rolls |
| 4670, 4672 `renderModUpdateProgress` | the bulk update bar on the Mods page | rolls (flex in the stylesheet now, hidden in the markup); emptied after the roll |
| 5458, 5520, 5535 `renderBepInExRow` | the BepInEx row above the mods table | rolls in; a row already on screen slides to its new height (revealSwap); emptied after the roll away |
| 7052 `renderGaleBox` | the Gale profile box under its button | rolls |
| 7069 | the Review button in that box | fade (a button in a row) |
| 7344, 7350 `galeCfgToggle` | a config preview in the Gale review dialog | drawn hidden and rolled in; rolls shut before the part is drawn again without it |
| 7621 `closePal` | the command palette | fades in by the stylesheet, leaves through fadeGhost |
| 8432, 8539, 10563 | the refusal lines in the Kill, Clear drops and prompt dialogs | roll away |
| 9087, 9088 `renderCaps` | the missing-mods banner | rolls |
| 9317 `syncDetailedLogGate` | the "forced on" note under Detailed log | rolls |
| 9405 `drawStartWinNote` | the notes under Start with Windows | roll; the words go after the roll |
| 10389 `ctxClose` | every row menu | fades in by the stylesheet (`.ctxmenu.ctxfixed.open`, 0.2 s), leaves through fadeGhost |
| 10484 `modalFadeOut` | every dialog | fades in (`.modal-bg`, 0.2 s ease-out), leaves through fadeGhost; no longer opt-in |
| 10748, 10750, 10757 `renderWorldIntegrity` | the damaged-world note on the Worlds card | rolls; slides when its words change; emptied after the roll away |
| 11313, 11319 `renderUpdatePill` | the server update pill | fade |
| 11407, 11425 `renderConditionBar` | the condition bar under the title bar | rolls; emptied after the roll away |
| 11694, 11698 `renderModsPluginNote` | the missing-plugin line on the Mods page | rolls |
| 12935 to 12940 `renderSkald` | the Statistics pills (running now, crashes) | fade |
| 13562, 13572 `worldTab` | the Settings hall's Server and App tabs, and Save Config | the tab that leaves goes at once, the one that comes fades in (both on screen for a roll would have doubled the hall's height); Save Config fades |
| 13865 | the note under the Directories section | rolls |
| 13880 to 13893 `renderPathNote` | the line under each path box | rolls; `syncDirsSection` reads it through revealShown |
| 14076 | the unsaved-changes band | slides in by its own stylesheet rule as before; leaves through fadeGhost |
| 14266 to 14281 `syncWorldNew` | the New world box, its note and the Copy as chip | box and note roll; the chip fades |
| 14528 `wgTipClose` | the (?) hover panel | fades in by the stylesheet (`.wgtip.open`, 0.2 s), leaves through fadeGhost |
| 14724 `renderWorldCarried` | the carried world-modifier line | rolls |
| 14746 to 14778 `renderWorldImported` | the "what a first meeting brought in" notice | rolls; emptied after the roll away |
| 15063 `renderCfgRunningNote` | the note above Save Config | rolls |
| 15304, 15312 `renderEditBar` | the realm edit bar | rolls |
| 15323, 15325 | the conflict warning inside it | fade |
| 15723, 15727 `heraldWizRender` | the thread box and the own-webhook box in the Discord wizard | roll |
| 15907, 15931 `waystoneWizRender` | the DNS check summary in the Waystone wizard | rolls |
| 16320, 16321 `atlasRenderMap` | the worldgen-mods warning pill on the map | fade |
| 16488, 16494 | the chunk note and the shape note slot under the map | roll; the shape note's words go after the roll |
| 16704, 16705 `renderWeather` | the Skies box | rolls |

Stylesheet entrances brought to the rule (app.css): `.modal-bg` and `.palette-bg` fade in over
0.2 s ease-out (were 0.16 s); `.langmenu.open`, `.ctxmenu.ctxfixed.open` and `.wgtip.open` fade
rather than slide. Under prefers-reduced-motion `.page`, `.palette`, `.ctxmenu`, those three and
every dialog are a plain fade.

## Left alone, and why

| app.js | what | why |
| --- | --- | --- |
| REVEAL block | the helper itself | it is the one place that may write display |
| 2027 `goPage` | goPage: walking between halls | a page walk, not a show or hide inside a layout; the hall arriving fades in by `.page`'s own rule, which reduced motion keeps as a fade |
| 2157, 2171, 2205 `renderServerChips`, `fitServerStrip` | the rail's realm strip, fitted by measurement | the strip's separator and "+N" cue are set while the rail is measured with every child frozen; a roll inside that measurement would misreport the rail |
| 8772, 8892, 8934 `applyTermVis`, `logLine`, `sagaReplayUnseen` | the Saga's lines, filtered and streamed | a filter pill touches every line of a log that can hold thousands; one roll per line would cost more than it shows, and a streaming line is data arriving, not a click |
| 9039, 9042 | the console caret under typing | typing, not a click, and a 1-character caret |
| 10389, 10395 `ctxClose`, `ctxOpen` | the row menu's own open and close, which fade through the stylesheet and fadeGhost | the menu has to measure itself at display:block before it is placed; its fade is the stylesheet's and fadeGhost's |
| 12251 `consoleModal` | the console dialog's rows, narrowed by typing | a search narrowing a list on every keystroke |
| 7350 `galeCfgToggle` | a Gale preview drawn hidden for its roll in | the start state of a roll, written just before reveal |
| 1541 `renderLangDot` | the 6 px partial-translation dot on the rail | raised by a catalog read, not by a click, through a class the stylesheet owns |
| 8573, 8581 `filterPal` | the palette's rows under typing | a search narrowing a list on every keystroke |
| 8797, 8801 `renderTermStream` | the "N new" chip over the log | raised by scrolling the log, not by a click |
| 16276, 16277 `atlasMsg` | the map's message line | already fades: `.atlas-msg.hidden` is an opacity transition |
| index.html | the 45 parts written with `style="display:none"`, and `#heraldPanels` | initial render and hidden templates: nothing is on screen to move |
| toasts | | come and go by `toastIn`/`toastOut` as before |
| `.page.active` | | the hall transition, as above |

## Behaviour kept

Same ids, same words, same saved values. A box emptied on hide is emptied after the roll
rather than before, which no reader of the box depends on (each is rewritten on the next
show). Fast clicking never leaves a box half open: a second call starts from where the box is
and a late finish of the first changes nothing (reveal_selftest rule 3). No fixed height is
left on any box after a roll (rule 1). The selftests that stood in for these parts were taught
the new truth where they had asserted the pop: a shown box reads `display:""` (the
stylesheet's own value) rather than `"block"` or `"flex"`, a stand-in starts hidden as the
markup does, and the browser probe for the realm band waits for the roll to end.
