"""Final UI polish: fix remaining mojibake literal, enlarge peer card, nudge title."""
import io

PATH = r"E:\deepseek\work\mod\SRHCoop\CoopBehaviour.cs"

with io.open(PATH, "r", encoding="utf-8", newline="") as f:
    lines = f.read().split("\n")

for i, line in enumerate(lines):
    if "UiKit.Button(pill" in line:
        lines[i] = '                if (UiKit.Button(pill, L("联机", "LAN"), _center, true)) _showPanel = true;'
    elif "float cardH = fs * 2.0f;" in line:
        lines[i] = "            float cardH = fs * 2.45f;"
    elif "y = header.y + headerH * 0.5f - fs * 0.62f;" in line:
        lines[i] = "            y = header.y + headerH * 0.5f - fs * 0.50f;"
    elif "float barY = r.yMax - pad - fs * 0.55f;" in line:
        lines[i] = "            float barY = r.yMax - pad * 0.7f - fs * 0.6f;"
    elif "Gui.Label" in line and "barY - fs * 0.14f" in line:
        lines[i] = ('            GUI.Label(new Rect(r.xMax - pad - fs * 3.1f, barY - fs * 0.16f, fs * 3.1f, fs * 0.95f),')

with io.open(PATH, "w", encoding="utf-8", newline="") as f:
    f.write("\n".join(lines))
print("ok")
