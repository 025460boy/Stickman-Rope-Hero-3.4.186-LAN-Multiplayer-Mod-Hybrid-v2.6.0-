"""Targeted fixes: replace whole lines by ASCII anchor instead of matching mojibake."""
import io

PATH = r"E:\deepseek\work\mod\SRHCoop\CoopBehaviour.cs"

with io.open(PATH, "r", encoding="utf-8", newline="") as f:
    lines = f.read().split("\n")

changed = 0
for i, line in enumerate(lines):
    if '"SRH Co-op"' in line:
        lines[i] = '                L("火柴人绳索英雄 · 联机", "SRH Co-op"), _title);'
        changed += 1
    elif "UiKit.Button(close," in line:
        lines[i] = '            if (UiKit.Button(close, "×", _center, false, 0.85f))'
        changed += 1

with io.open(PATH, "w", encoding="utf-8", newline="") as f:
    f.write("\n".join(lines))
print("changed", changed)
