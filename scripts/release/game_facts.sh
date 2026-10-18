#!/usr/bin/env bash
# Reads the world version a Valheim server install writes (Version.c_WorldVersion out of its assembly_valheim.dll)
# and fails when that, scripts/release/game_facts.json and WorldDbReader.MaxWorldVersion do not all agree.
# Usage from the repo root: bash scripts/release/game_facts.sh [server install dir]. Exit 0 agree, 1 disagree, 2 could not read.
set -uo pipefail

SERVER="${1:-C:/Users/Mithi/dev/BakaTest/server}"
DLL="$SERVER/valheim_server_Data/Managed/assembly_valheim.dll"
FACTS=scripts/release/game_facts.json
READER=ValheimBakaLoader/Tools/Atlas/WorldDbReader.cs

py=""
for cand in python C:/Python314/python python3; do
  if "$cand" -c "print(1)" >/dev/null 2>&1; then py="$cand"; break; fi
done
[ -n "$py" ] || { echo "FAIL  no working python found"; exit 2; }

[ -f "$FACTS" ]  || { echo "FAIL  $FACTS not found (run this from the repo root)"; exit 2; }
[ -f "$READER" ] || { echo "FAIL  $READER not found (run this from the repo root)"; exit 2; }
[ -f "$DLL" ]    || { echo "FAIL  no assembly_valheim.dll under $SERVER, so the game was not read"; exit 2; }

# The json is one line the release engineer writes; read the two fields this needs.
facts_game=$(sed -n 's/.*"gameVersion"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$FACTS")
facts_world=$(sed -n 's/.*"worldVersion"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$FACTS")
facts_checked=$(sed -n 's/.*"checked"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$FACTS")
source_world=$(sed -n 's/^[[:space:]]*internal const int MaxWorldVersion = \([0-9][0-9]*\);.*/\1/p' "$READER")
[ -n "$facts_world" ]  || { echo "FAIL  $FACTS has no worldVersion"; exit 2; }
[ -n "$source_world" ] || { echo "FAIL  no MaxWorldVersion constant in $READER"; exit 2; }

# The game does not carry its version as text (it is new GameVersion(1, 0, 20) in code), so the
# number read is the constant the save code writes. A const lives in the metadata Constant table,
# which a few dozen lines can reach without decompiling anything.
game=$("$py" -I - "$DLL" <<'PY'
import struct, sys
b = open(sys.argv[1], 'rb').read()
pe = struct.unpack_from('<I', b, 0x3C)[0]
nsec = struct.unpack_from('<H', b, pe + 6)[0]
opt = pe + 24
magic = struct.unpack_from('<H', b, opt)[0]
dirs = opt + (96 if magic == 0x10B else 112)
secs = opt + struct.unpack_from('<H', b, pe + 20)[0]
def off(rva):
    for i in range(nsec):
        s = secs + 40 * i
        vsize, va, rawsize, raw = struct.unpack_from('<IIII', b, s + 8)
        if va <= rva < va + max(vsize, rawsize):
            return rva - va + raw
    raise SystemExit('rva outside every section')
cli = off(struct.unpack_from('<I', b, dirs + 14 * 8)[0])
md = off(struct.unpack_from('<I', b, cli + 8)[0])
vlen = struct.unpack_from('<I', b, md + 12)[0]
count = struct.unpack_from('<H', b, md + 16 + vlen + 2)[0]
p, streams = md + 16 + vlen + 4, {}
for _ in range(count):
    o, size = struct.unpack_from('<II', b, p)
    name = b[p + 8:b.index(b'\0', p + 8)].decode()
    streams[name] = md + o
    p += 8 + ((len(name) + 4) & ~3)
t = streams['#~']
heaps = b[t + 6]
valid = struct.unpack_from('<Q', b, t + 8)[0]
rows, q = {}, t + 24
for i in range(64):
    if valid >> i & 1:
        rows[i] = struct.unpack_from('<I', b, q)[0]; q += 4
S = 4 if heaps & 1 else 2; G = 4 if heaps & 2 else 2; B = 4 if heaps & 4 else 2
def idx(table): return 4 if rows.get(table, 0) >= 0x10000 else 2
def coded(tables, bits): return 4 if max(rows.get(x, 0) for x in tables) >= (1 << (16 - bits)) else 2
TDOR = coded([2, 1, 0x1B], 2)
layout = {
    0x00: [2, S, G, G, G], 0x01: [coded([0, 0x1A, 0x23, 1], 2), S, S],
    0x02: [4, S, S, TDOR, idx(4), idx(6)], 0x03: [idx(4)], 0x04: [2, S, B], 0x05: [idx(6)],
    0x06: [4, 2, 2, S, B, idx(8)], 0x07: [idx(8)], 0x08: [2, 2, S], 0x09: [idx(2), TDOR],
    0x0A: [coded([2, 1, 0x1A, 6, 0x1B], 3), S, B], 0x0B: [1, 1, coded([4, 8, 0x17], 2), B],
}
start = {}
for table in range(0x0C):
    if table in rows:
        start[table] = q; q += rows[table] * sum(layout[table])
def row(table, n):
    out, at = [], start[table] + (n - 1) * sum(layout[table])
    for w in layout[table]:
        out.append(struct.unpack_from({1: '<B', 2: '<H', 4: '<I'}[w], b, at)[0]); at += w
    return out
def text(o):
    s = streams['#Strings'] + o
    return b[s:b.index(b'\0', s)].decode()
def blob(o):
    s = streams['#Blob'] + o; n = b[s]
    if n & 0x80 == 0: return b[s + 1:s + 1 + n]
    if n & 0xC0 == 0x80: n = (n & 0x3F) << 8 | b[s + 1]; return b[s + 2:s + 2 + n]
    n = (n & 0x1F) << 24 | b[s + 1] << 16 | b[s + 2] << 8 | b[s + 3]; return b[s + 4:s + 4 + n]
consts = {}
for n in range(1, rows.get(0x0B, 0) + 1):
    kind, _, parent, value = row(0x0B, n)
    if parent & 3 == 0 and kind == 0x08:
        consts[parent >> 2] = struct.unpack('<i', blob(value))[0]
types = [row(2, n) for n in range(1, rows[2] + 1)]
def fields(i):
    end = types[i + 1][4] if i + 1 < len(types) else rows[4] + 1
    return {text(row(4, f)[1]): f for f in range(types[i][4], end)}
written, top = None, {}
for i, ty in enumerate(types):
    if text(ty[2]) != '': continue
    f = fields(i)
    if text(ty[1]) == 'Version' and 'c_WorldVersion' in f:
        written = consts.get(f['c_WorldVersion'])
    if text(ty[1]) == 'World' and 'ChunkedSave' in f and 'value__' in f:
        top = {name: consts[r] for name, r in f.items() if r in consts}
if written is None or not top:
    raise SystemExit('Version.c_WorldVersion or the Version.World enum was not found')
name = next((k for k, v in top.items() if v == written), '?')
print(written, name, max(top.values()))
PY
) || { echo "FAIL  could not read the world version out of $DLL"; exit 2; }

read -r dll_world dll_name dll_enum_top <<<"$game"
build=$(sed -n 's/.*"buildid"[[:space:]]*"\([0-9]*\)".*/\1/p' "$SERVER/steamapps/appmanifest_896660.acf" 2>/dev/null | head -1)

echo "game        $SERVER (Steam build ${build:-unknown})"
echo "            writes world version $dll_world (Version.World.$dll_name; the enum tops out at $dll_enum_top)"
echo "facts       $FACTS says game $facts_game, world version $facts_world, checked $facts_checked"
echo "reader      WorldDbReader.MaxWorldVersion is $source_world"

fail=0
if [ "$dll_world" != "$facts_world" ]; then
  echo "FAIL  the game writes $dll_world but $FACTS says $facts_world: run the compat check and update both"
  fail=1
fi
if [ "$source_world" != "$facts_world" ]; then
  echo "FAIL  MaxWorldVersion is $source_world but $FACTS says $facts_world"
  fail=1
fi
[ "$fail" -eq 0 ] && echo "GAME FACTS: PASS" || echo "GAME FACTS: FAIL"
exit "$fail"
