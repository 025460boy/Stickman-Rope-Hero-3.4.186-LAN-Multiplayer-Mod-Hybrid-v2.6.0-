"""Restore the Chinese literals that could not be reversed automatically."""
import io

PATH = r"E:\deepseek\work\mod\SRHCoop\CoopBehaviour.cs"

FIXES = {
    553: '                    f.RequestCharactersInTexture("联机玩家", size, FontStyle.Normal);',
    555: "                    if (f.GetCharacterInfo('联', out ci, size, FontStyle.Normal) && ci.glyphWidth > 0)",
    683: '                string cap = L("局域网联机已加载", "LAN co-op loaded");',
    783: '                L("火柴人绳索英雄 · 联机", "SRH Co-op"), _title);',
    785: '            if (UiKit.Button(close, "×", _center, false, 0.85f))',
    796: '            string role = !_lanOn ? L("未联机", "offline") : (IsHost ? L("主机", "HOST") : L("客户端", "CLIENT"));',
    810: '                    _lanOn ? L("关闭联机", "Stop LAN") : L("开始联机", "Start LAN"), _center, true))',
    815: '                    _cfg.ShowHealthBars ? L("血条：显示", "Bars: ON") : L("血条：隐藏", "Bars: OFF"), _center, false))',
    821: '                  + (_rttMs >= 0f ? L("　延迟 ", "  rtt ") + Mathf.RoundToInt(_rttMs) + "ms" : "")',
    822: '                  + (_net != null ? L("　收 ", "  rx ") + _net.PacketsReceived : "")',
    823: '                : L("与好友连同一个 Wi-Fi 即可自动发现", "Same Wi-Fi required");',
    843: '            if (UiKit.Button(new Rect(innerX, y, halfW, bh), L("测试机器人", "Test bot"), _center, false))',
    845: '            if (UiKit.Button(new Rect(innerX + halfW + gap, y, halfW, bh), L("测试受击", "Test hit"), _center, false))',
    849: '            if (UiKit.Button(new Rect(innerX, y, innerW, bh), L("回环联机测试", "Loopback net test"), _center, false, 0.82f))',
    902: '            t.SendTo(Wire.BuildState(fakeId, L("回环玩家", "LoopPeer"), 1, pos,',
    941: '            string name = L("测试机器人", "TestBot");',
}

with io.open(PATH, "r", encoding="utf-8", newline="") as f:
    lines = f.read().split("\n")

for no, text in sorted(FIXES.items()):
    idx = no - 1
    print("line %d: %r -> %r" % (no, lines[idx][:40].encode("ascii", "replace").decode(), text[:40]))
    lines[idx] = text

with io.open(PATH, "w", encoding="utf-8", newline="") as f:
    f.write("\n".join(lines))
print("done")
