"""Prepare the mascot button image (already a cutout with alpha)."""
import sys

import numpy as np
from PIL import Image

SRC = r"D:\Users\Administrator\.dsh\attachments\v1\objects\05\05ca4c354531352b07b9676cbd19ca81cbf10552d74a47e938dd2709e49ba7d0"
OUT = r"E:\deepseek\work\art\mascot.png"

im = Image.open(SRC).convert("RGBA")
a = np.array(im)
alpha = a[:, :, 3]
ys, xs = np.where(alpha > 8)
box = (xs.min(), ys.min(), xs.max() + 1, ys.max() + 1)
im = im.crop(box)
print("cropped", im.size)

# square canvas so the button keeps its aspect
side = max(im.size)
canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
canvas.paste(im, ((side - im.size[0]) // 2, (side - im.size[1]) // 2))
canvas.resize((256, 256), Image.LANCZOS).save(OUT)
print("wrote", OUT)
