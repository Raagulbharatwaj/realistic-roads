# Draws the Steam Workshop preview image (PreviewImage.png, 644 x 644): the
# RR road curve over a bridge and a tunnel, with the mod's name and BETA.
# Usage (from the repo root): python3 tools/make_preview.py RealisticRoads/PreviewImage.png
import sys
from PIL import Image, ImageDraw, ImageFont
N, S = 644, 2
OURS = (255, 200, 40, 255)
ASPHALT = (80, 85, 95, 255)
DASH = (255, 255, 255, 255)
EXIST = (175, 180, 190, 255)
GROUND = (110, 170, 80, 255)
SKY = (28, 38, 52, 255)
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"

big = N * S
im = Image.new("RGBA", (big, big), SKY)
d = ImageDraw.Draw(im)
k = big / 644.0

def P(*xy):
    return [v * k for v in xy]

# hills and an existing road on the ground
d.polygon(P(0, 470, 120, 430, 260, 455, 400, 420, 520, 440, 644, 425, 644, 644, 0, 644), fill=GROUND)
d.line(P(0, 560, 644, 545), fill=EXIST, width=int(26 * k))

# our road: an S-curve on pillars, sweeping over the existing road
pts = []
B = [(40, 520), (260, 520), (300, 300), (610, 290)]
for i in range(81):
    t = i / 80.0
    w = ((1 - t) ** 3, 3 * t * (1 - t) ** 2, 3 * t * t * (1 - t), t ** 3)
    pts.append((sum(c * B[j][0] for j, c in enumerate(w)) * k, sum(c * B[j][1] for j, c in enumerate(w)) * k))
for i in range(20, 81, 15):
    x, y = pts[i]
    d.line([x, y, x, 560 * k], fill=(200, 200, 205, 255), width=int(10 * k))
d.line(pts, fill=OURS, width=int(44 * k), joint="curve")
d.line(pts, fill=ASPHALT, width=int(34 * k), joint="curve")
for i in range(0, 80, 6):
    d.line(pts[i:i + 3], fill=DASH, width=int(4 * k))

# title
title = ImageFont.truetype(FONT, int(70 * k))
small = ImageFont.truetype(FONT, int(34 * k))
d.text((322 * k, 92 * k), "REALISTIC", font=title, fill=DASH, anchor="mm")
d.text((322 * k, 168 * k), "ROADS", font=title, fill=OURS, anchor="mm")
d.rounded_rectangle(P(462, 206, 600, 252), radius=10 * k, fill=(200, 60, 50, 255))
d.text((531 * k, 229 * k), "BETA", font=small, fill=DASH, anchor="mm")

im.resize((N, N), Image.LANCZOS).convert("RGB").save(sys.argv[1], optimize=True)
