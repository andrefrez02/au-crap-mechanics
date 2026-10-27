#!/usr/bin/env python3
"""bp_convert.py - converts Approximately Up blueprints saved with the Crap Mechanics prefab names from before 0.27.0
(SC_Pistao, SC_MotorP, SC_RodaSportsP, ...) to the English names (SC_Piston, SC_MotorSmall, SC_WheelSportsSmall, ...).

You normally don't need this: Crap Mechanics 0.27.0+ renames the parts of an old blueprint by itself when the game loads it
(look for "old prefab names" in the BepInEx log), and the next save writes the new names. Use this tool when that in-game
conversion didn't run (the log says the blueprint patch wasn't applied) or to convert a whole folder at once.

A blueprint stores each part by the 64-bit hash of its prefab name. The game computes those hashes, so the mod writes the
old and new hash of every renamed prefab to BepInEx/config/CrapMechanics/prefab-renames.txt the first time 0.27.0+ runs:
start the game once with the new version before using this tool.

Usage:
  python bp_convert.py <prefab-renames.txt> [<blueprint .bp or folder> ...] [--dry-run]

  With no blueprint path, the game's blueprint folder is used
  (%USERPROFILE%/AppData/LocalLow/ApproximatelyGames/ApproximatelyUp/Blueprints).
  Folders are searched recursively for .bp files. Each changed file is first copied to <name>.bp.bak-0.27 (an existing
  backup is never overwritten). --dry-run only reports what would change.

File format (reverse engineered, every byte accounted for):
  [u32 nStructs]
  nStructs x struct definition: [u64 typeHash][u32 structSize][u32 fieldCount] + fieldCount x [u64 fieldHash][u32 offset][u32 typeCode]
  records until the end of the file: [u64 componentTypeHash][u32 schemaIndex][u32 reserved] + structSize bytes of payload
  componentTypeHash is the hash of the part's prefab name - the only thing this tool changes. A file that doesn't parse
  to the last byte is left alone.
Uses only Python's standard library.
"""
import os, shutil, struct, sys

REC = struct.Struct("<QII")
BACKUP_SUFFIX = ".bak-0.27"


def read_renames(path):
    """old hash -> (new hash, old name, new name), from the mod's prefab-renames.txt (old name, new name, old hash, new hash)."""
    m = {}
    for raw in open(path, encoding="utf-8"):
        line = raw.rstrip("\r\n")
        if not line or line.startswith("#"): continue
        parts = line.split("\t")
        if len(parts) != 4: raise SystemExit("unexpected line in %s: %r" % (path, line))
        old, new, a, b = parts
        a, b = int(a), int(b)
        if a == 0 or b == 0 or a == b: continue
        m[a] = (b, old, new)
    if not m: raise SystemExit(path + ": no renames found")
    return m


def record_offsets(data):
    """Offsets of the records' componentTypeHash, or None if the file doesn't follow the format exactly."""
    if len(data) == 0: return []
    if len(data) < 4: return None
    n = struct.unpack_from("<I", data, 0)[0]; off = 4; sizes = []
    if n > 4096: return None
    for _ in range(n):
        if off + 16 > len(data): return None
        _, size, nf = REC.unpack_from(data, off); off += 16
        if nf > 4096 or off + 16 * nf > len(data): return None
        off += 16 * nf; sizes.append(size)
    offs = []
    while off < len(data):
        if off + 16 > len(data): return None
        _, idx, _ = REC.unpack_from(data, off)
        if idx >= len(sizes) or off + 16 + sizes[idx] > len(data): return None
        offs.append(off); off += 16 + sizes[idx]
    return offs


def convert(path, renames, dry):
    data = bytearray(open(path, "rb").read())
    offs = record_offsets(data)
    if offs is None: print("SKIPPED (not a blueprint this tool understands): " + path); return 0
    counts = {}
    for o in offs:
        h = struct.unpack_from("<Q", data, o)[0]
        if h in renames:
            nh, old, new = renames[h]; struct.pack_into("<Q", data, o, nh)
            counts[(old, new)] = counts.get((old, new), 0) + 1
    if not counts: return 0
    total = sum(counts.values())
    print(("would convert " if dry else "converted ") + path + ": " + str(total) + " part(s) — " +
          ", ".join("%s -> %s x%d" % (o, n, c) for (o, n), c in sorted(counts.items())))
    if not dry:
        bak = path + BACKUP_SUFFIX
        if not os.path.exists(bak): shutil.copy2(path, bak)
        tmp = path + ".tmp"
        with open(tmp, "wb") as f: f.write(data)
        os.replace(tmp, path)
    return total


def blueprints(paths):
    for p in paths:
        if os.path.isdir(p):
            for d, _, fs in os.walk(p):
                for f in sorted(fs):
                    if f.lower().endswith(".bp"): yield os.path.join(d, f)
        elif os.path.isfile(p): yield p
        else: print("not found: " + p)


def main(argv):
    dry = "--dry-run" in argv; args = [a for a in argv if a != "--dry-run"]
    if not args or args[0] in ("-h", "--help"): print(__doc__); return 1
    renames = read_renames(args[0])
    paths = args[1:] or [os.path.join(os.path.expanduser("~"), "AppData", "LocalLow", "ApproximatelyGames", "ApproximatelyUp", "Blueprints")]
    files = changed = parts = 0
    for bp in blueprints(paths):
        files += 1; n = convert(bp, renames, dry)
        if n: changed += 1; parts += n
    print("%d blueprint(s) checked, %d %s, %d part(s) renamed%s" % (files, changed, "would change" if dry else "changed", parts,
          "" if dry or not changed else " (backups: *" + BACKUP_SUFFIX + ")"))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
