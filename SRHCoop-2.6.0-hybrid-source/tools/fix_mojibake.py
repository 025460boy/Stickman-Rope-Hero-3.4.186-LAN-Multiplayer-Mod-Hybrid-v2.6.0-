"""Repair a UTF-8 file whose Chinese text was decoded as GBK and re-encoded.

Original bytes  ->  decoded as cp936  ->  written as UTF-8  (mojibake)
Reverse: mojibake.encode('gbk') -> original UTF-8 bytes -> decode utf-8.
"""
import sys

path = sys.argv[1]
raw = open(path, "r", encoding="utf-8-sig", newline="").read()

out = []
fixed = 0
broken = 0
for i, line in enumerate(raw.split("\n"), 1):
    if all(ord(c) < 128 for c in line):
        out.append(line)
        continue
    try:
        out.append(line.encode("gbk").decode("utf-8"))
        fixed += 1
    except Exception:
        out.append(line)
        broken += 1
        print("UNRECOVERABLE line %d: %s" % (i, line.strip()[:100].encode("ascii", "replace").decode()))

open(path, "w", encoding="utf-8", newline="").write("\n".join(out))
print("fixed=%d broken=%d" % (fixed, broken))
