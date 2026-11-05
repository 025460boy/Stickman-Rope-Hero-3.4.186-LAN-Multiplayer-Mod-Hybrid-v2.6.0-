"""Repair the block that holds the panel close button (line numbers shifted during repair)."""
import io

PATH = r"E:\deepseek\work\mod\SRHCoop\CoopBehaviour.cs"

with io.open(PATH, "r", encoding="utf-8", newline="") as f:
    lines = f.read().split("\n")

# expected shape around the header label / close button
start = None
for i in range(770, 800):
    if "SRH Co-op" in lines[i]:
        start = i
        break
if start is None:
    raise SystemExit("anchor not found")

print("anchor line:", start + 1)
print("before:", [l.encode("ascii", "replace").decode()[:60] for l in lines[start:start + 5]])

block = [
    '                L("鐏煷浜虹怀绱㈣嫳闆?路 鑱旀満", "SRH Co-op"), _title);',
    '',
    '            Rect close = new Rect(header.xMax - fs * 1.55f, header.y + (headerH - fs * 1.55f) * 0.5f,',
    '                fs * 1.55f, fs * 1.55f);',
    '            if (UiKit.Button(close, "脳", _center, false, 0.85f))',
]
lines[start:start + 5] = block

with io.open(PATH, "w", encoding="utf-8", newline="") as f:
    f.write("\n".join(lines))
print("patched")

