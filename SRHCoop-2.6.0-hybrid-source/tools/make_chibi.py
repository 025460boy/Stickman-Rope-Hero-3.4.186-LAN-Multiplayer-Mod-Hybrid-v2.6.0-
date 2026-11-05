"""Turn the white-background chibi into a transparent PNG and export sizes."""
import sys
from collections import deque

import numpy as np
from PIL import Image

SRC = r"D:\Users\Administrator\.dsh\attachments\v1\objects\d2\d23f3155c2a7fd504ebc6a1eeada77a6d8efc26f0ea04de3aef63d31985f4c63"
OUT = r"E:\deepseek\work\art"


def near_white(px, tol=30):
    return px[0] >= 255 - tol and px[1] >= 255 - tol and px[2] >= 255 - tol


def main():
    im = Image.open(SRC).convert("RGBA")
    a = np.array(im)
    h, w = a.shape[:2]

    visited = np.zeros((h, w), dtype=bool)
    dq = deque()
    for x in range(w):
        for y in (0, h - 1):
            if near_white(a[y, x]):
                visited[y, x] = True
                dq.append((y, x))
    for y in range(h):
        for x in (0, w - 1):
            if near_white(a[y, x]) and not visited[y, x]:
                visited[y, x] = True
                dq.append((y, x))

    while dq:
        y, x = dq.popleft()
        a[y, x, 3] = 0
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ny, nx = y + dy, x + dx
            if 0 <= ny < h and 0 <= nx < w and not visited[ny, nx] and near_white(a[ny, nx]):
                visited[ny, nx] = True
                dq.append((ny, nx))

    out = Image.fromarray(a)
    box = out.getbbox()
    out = out.crop(box)
    print("cropped:", out.size)
    out.save(OUT + r"\chibi.png")
    for s in (512, 256, 128, 96):
        out.resize((s, s), Image.LANCZOS).save(OUT + rf"\chibi_{s}.png")
    print("ok")


if __name__ == "__main__":
    main()
