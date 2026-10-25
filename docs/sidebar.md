# The simpler sidebar (1.2.18)

The rail lists five halls, in this order: Dashboard, Players, Map, Mods, Settings. Nothing
that was reachable before is gone: the four halls that left the rail are sections of the
halls that stayed, with the ids, words and saved values they had.

## Where every old entry lives now

| old rail entry | now | opens by default | how to reach it besides the chevron |
| --- | --- | --- | --- |
| Dashboard (Hearth) | the Dashboard hall; its bento is the hall's first section | open | the rail, the palette |
| Players (Vikings) | the Players hall, unchanged | | the rail, the palette, the Players card on the Dashboard |
| Mods | the Mods hall; the list is the hall's first section | open | the rail, the palette, the Mods card, the condition bar's "mods with updates" row, the BepInEx landings |
| Configs (Runes) | a section under the Mods list (`#sect-runes`) | folded | a mod row's "configs" entry (`openModConfig`), which unfolds it and lights it |
| Settings (World) | the Settings hall; the tabs and both panels are the hall's first section | open | the rail, the palette, a new or duplicated realm landing on its ports and paths, `openAppTab` for the three App tab controls |
| Map (Atlas) | the Map hall; the map and its side column are the hall's first section, with Statistics under it since 1.3.0 | open | the rail, the palette |
| Log (Saga) | a section at the foot of the Dashboard (`#sect-saga`), under the bento (under Statistics until 1.3.0) | folded | the Recent log card's chip, the "open the log" toast action, the condition bar's unsaved, crash, failed-save and plugin rows |
| Discord (Herald) | a section at the foot of Settings (`#sect-herald`) | folded | the palette's Discord sharing entry; the setup wizard opens from inside it |
| Statistics (Skald) | a section on the Map hall (`#sect-skald`), under the map, since 1.3.0 (issue 27); it stood between the bento and the Log on the Dashboard before | folded | the palette's Server analytics entry, which walks to the Map hall and so starts the map's drawing behind it |

The halls are written in index.html in the rail's order (hearth, vikings, atlas, mods, world),
which the test `The_halls_are_written_in_the_order_the_rail_lists_them` holds.

## What a section is

Each section is a `.sect` with the old hall's `.pagehead` first (now `.secthead`, under a
hairline) and the rest of the old hall in a `.sectbody`, which app.js rolls open and shut
through `reveal()` (docs/motion-sweep.md). The halls' own first sections keep their sticky
heads; their bodies are the bento, the map (`.atlas-grid`, since 1.3.0), `#sectBody-mods` and
`#sectBody-world`. Since 1.3.0
the Settings tab strip stands just above `#sectBody-world`, outside it, so folding the panels
leaves the tabs under the head; a press on a tab while the panels are folded unfolds them.

## Every head on screen (1.3.0)

Every section head stays on screen (issue 27). The hall's own head sticks at the top as it
always did. Each later head sticks at the top under the heads above it once its section has
scrolled up there, and at the foot of the pane, above the heads below it, while its section
is still under the fold, so with the hall at the top every folded section's head is in view
at the foot. It is plain `position:sticky` with both `top` and `bottom`. A sticky box only
travels inside its parent, so the `.sect` wrapper draws no box of its own
(`display:contents`); it keeps its id, `data-sect` and `open` class, and `reveal()` asks about
a body inside it through the hall.

The offsets are the measured heights of the heads above and below each head, written on it as
`--stick-top` and `--stick-bottom` by `sectStackSize` from one ResizeObserver over every hall,
head and body (heads are 62 to 129 px depending on hall, window, text size and language, so
nothing is written down). `sectStackRead` reads which heads stand at the top and at the foot
on every scroll and after every roll, and writes the hall's scroll padding from those two
stacks (`--stack-top`, `--stack-bottom`), which replaced the fixed 120px; the lowest head stuck
at the top and the highest at the foot cast the hall head's shadow, and the fade over the
pane's foot stands down while heads stand there.

`sectWire()` in app.js writes the chevron to the left of every section heading: a button
that glows like the app's other embers (`.sectchev`, `chevEmber`), points right while
folded and turns to a V when open, carries `aria-expanded` and `aria-controls`, and rests
with the other embers while the window is behind another (`body.idle`).

The table `SECTIONS` in app.js is the one list: key (the hall's own name), hall, head, body,
default, and what the hall used to do on the walk in, which the section does on the way
open (Statistics refreshes its journal, Configs re-lists the files, Discord re-reads its
preferences).

## The Map hall (1.3.0)

Statistics moved from the Dashboard to the Map hall (issue 27), and the Map became a two-section
hall like the others: the map with its side column is the hall's own section (`atlas` in
`SECTIONS`, head `#page-atlas>.pagehead`, which floats now, body `.atlas-grid`, open by
default), and Statistics stands under it, folded by default. Its key stays `skald`, so a saved
open or folded answer survives the move.

With Statistics folded the hall fits the pane and does not scroll: the map's row is
`max(180px, calc(100cqh - var(--stack-all)))`, where `100cqh` is the hall's own content height
(the hall is a size container) and `--stack-all` is every head of the hall with the margins
around it, measured by `sectStackSize`. The height is on the grid's ROW, not on the grid, so a
fold of the map's own section rolls the grid away while the map keeps its size and is clipped
by the roll; the hall keeps its scrollbar's gutter (`scrollbar-gutter:stable`) so Statistics
opening does not narrow it either. That matters because `atlasResize` takes a change of the
map's size for a zoom: with the map's size untouched by every fold, a fold and an unfold come
back to the same zoom and centre (the layout probe folds each five times and compares). A fit
asked while the map is folded (the image landing, Recenter) is owed and made when the map is
back. The canvas keeps zooming on the wheel; the hall scrolls by its scrollbar, the chevron or a
wheel anywhere outside the canvas. A realm switch reads Statistics again after the map is reset
for the new realm, and a read asked while one is out runs again after it rather than being
dropped, so the numbers are always the realm on screen's.

## Remembered

Open or folded is remembered per section, by the hall's own name: across hall walks in
`UI_SECTIONS`, across restarts in userprefs.json as `uiSections` (`UserPreferences.UiSections`,
sent as `UiSections` through `userprefs.get` and `userprefs.save`, the same document and the
same calls the Upkeep card's switches use). The saved answer is read with the Upkeep card's
first read and set with no roll; every move writes the whole answer. The defaults in the
table apply only to a section the document does not name, which is the first run and
nothing after it (and, for the Map's own section, the first run of 1.3.0).

## Every road in

`goSection(key)` is the one way a button, a palette row, a toast action or a link lands on
a section: it opens the hall, unfolds the section, brings it into view once it has rolled
open and lights its body with the ember glow (`.sect-glow`: 0.5 s in, three soft pulses over
3 s, 0.5 s out; a steady glow under reduced motion).

Bringing it into view is `sectLand`, run from the roll's `{done}` (`sectSet(key,true,{land:true})`),
when the section stands at its full height; until 1.3.0 the scroll ran in the next frame while
the body was still 0 high, so a section opened from the top landed with its body under the
fold. The hall scrolls so the section's foot sits at the foot of the pane, just above the heads
stuck there (the end of the hall for the last section); a section taller than the room between
the heads above and below it lands with its head right under the heads above instead.
`sectLandTop` holds the rule as plain numbers; the scroll is smooth, or at once under reduced
motion. A chevron click that opens a section lands it the same way; a fold never scrolls. A
mod row's configs entry brings its file's row up inside the Configs list only, so the hall's
one scroll is the landing. The landings that light a row of their own (`openAppTab`, `openUpkeepCard`,
`openConnectionCard`, the two BepInEx landings on the Mods list) unfold their section with
`sectSet` and keep their own row flash. The rail's own clicks walk to the hall and leave the
sections as they stand.

## Checks that read the old shape

The checks that keyed on `currentPage==="saga"`, `"runes"` and `"skald"` read `sectOn(key)`
now (the hall is showing and the section stands open), and the three re-reads the walk into
a hall used to make happen on the walk in only when the section is open, and on the way
open otherwise.
