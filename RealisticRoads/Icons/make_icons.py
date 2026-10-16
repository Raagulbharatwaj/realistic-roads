# Draws the crossing-tool icons. Our road = yellow, existing road = grey.
import sys
from PIL import Image, ImageDraw
S = 4            # supersampling
N = 40           # final size
OURS = (255, 200, 40, 255)
EXIST = (175, 180, 190, 255)
DASH = (255, 255, 255, 255)
WATER = (70, 150, 230, 255)
GROUND = (110, 170, 80, 255)
# Usage: python3 make_icons.py <output folder> <preview sheet.png>
out = sys.argv[1]

def canvas():
    im = Image.new("RGBA", (N * S, N * S), (0, 0, 0, 0))
    return im, ImageDraw.Draw(im)

def save(im, name):
    im.resize((N, N), Image.LANCZOS).save(f"{out}/{name}.png")

def p(*xy):  # scale coordinates given in 40-px space
    return [v * S for v in xy]

def line(d, xy, color, w):
    d.line(p(*xy), fill=color, width=int(w * S), joint="curve")

# junction: two roads crossing at the same level
im, d = canvas()
line(d, (4, 20, 36, 20), EXIST, 9)
line(d, (20, 4, 20, 36), OURS, 9)
line(d, (20, 6, 20, 12), DASH, 1.2); line(d, (20, 28, 20, 34), DASH, 1.2)
save(im, "junction")

# bridge: ours on a deck with pillars above the existing road
im, d = canvas()
line(d, (3, 33, 37, 33), EXIST, 6)
line(d, (3, 12, 37, 12), OURS, 6)
for x in (10, 30):
    line(d, (x, 15, x, 29), OURS, 3)
save(im, "bridge")

# overpass: existing road lowered into a dip, ours straight across at ground
im, d = canvas()
d.polygon(p(2, 22, 12, 22, 16, 34, 24, 34, 28, 22, 38, 22, 38, 38, 2, 38), fill=GROUND)
line(d, (2, 22, 12, 22, 16, 33, 24, 33, 28, 22, 38, 22), EXIST, 5)
line(d, (2, 14, 38, 14), OURS, 6)
save(im, "overpass")

# underpass: existing road on a short bridge, ours dips under it
im, d = canvas()
line(d, (2, 12, 38, 12), EXIST, 6)
line(d, (11, 14, 11, 21), EXIST, 3); line(d, (29, 14, 29, 21), EXIST, 3)
line(d, (2, 24, 11, 24, 16, 34, 24, 34, 29, 24, 38, 24), OURS, 5)
save(im, "underpass")

# tunnel: portal in a hill, ours going inside
im, d = canvas()
d.pieslice(p(2, 6, 38, 46), 180, 360, fill=GROUND)
d.pieslice(p(12, 18, 28, 38), 180, 360, fill=(40, 40, 45, 255))
line(d, (2, 30, 38, 30), OURS, 5)
line(d, (14, 30, 26, 30), (60, 60, 60, 255), 5)
save(im, "tunnel")

# plus / minus
for name, vertical in (("plus", True), ("minus", False)):
    im, d = canvas()
    line(d, (10, 20, 30, 20), DASH, 5)
    if vertical: line(d, (20, 10, 20, 30), DASH, 5)
    save(im, name)

# backgrounds: rounded squares
for name, fill, edge in (("bg", (30, 40, 50, 210), (90, 110, 130, 255)),
                         ("bgHover", (50, 70, 90, 230), (150, 180, 210, 255)),
                         ("bgSelected", (30, 110, 60, 235), (130, 230, 150, 255))):
    im, d = canvas()
    d.rounded_rectangle(p(1, 1, 39, 39), radius=8 * S, fill=fill, outline=edge, width=2 * S)
    save(im, name)

# preview sheet for checking by eye
names = ["junction", "bridge", "overpass", "underpass", "tunnel", "plus", "minus"]
sheet = Image.new("RGBA", (len(names) * 48, 48), (40, 50, 60, 255))
for i, nm in enumerate(names):
    bg = Image.open(f"{out}/bg.png"); ic = Image.open(f"{out}/{nm}.png")
    sheet.alpha_composite(bg, (i * 48 + 4, 4)); sheet.alpha_composite(ic, (i * 48 + 4, 4))
sheet = sheet.resize((sheet.width * 3, sheet.height * 3), Image.NEAREST)
sheet.save(sys.argv[2])
