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
# Usage (from the repo root): python3 tools/make_icons.py RealisticRoads/Icons <preview sheet.png>
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

# logo: a road sweeping round a bend, white dashes down the middle, on a
# transparent background (drawn on the button backgrounds below)
def logo(size, name):
    big = size * S
    im = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    k = size / 40.0 * S
    # an S-curve from the bottom-left to the top-right (cubic Bezier)
    P = [(4, 35), (24, 35), (16, 5), (36, 5)]
    pts = []
    for i in range(41):
        t = i / 40.0
        x = sum(c * P[j][0] for j, c in enumerate(((1 - t) ** 3, 3 * t * (1 - t) ** 2, 3 * t * t * (1 - t), t ** 3)))
        y = sum(c * P[j][1] for j, c in enumerate(((1 - t) ** 3, 3 * t * (1 - t) ** 2, 3 * t * t * (1 - t), t ** 3)))
        pts.append((x * k, y * k))
    d.line(pts, fill=OURS, width=int(12 * k), joint="curve")             # yellow edges
    d.line(pts, fill=(80, 85, 95, 255), width=int(9 * k), joint="curve")   # asphalt
    for i in range(0, 40, 4):                                             # dashed centre line
        d.line(pts[i:i + 3], fill=DASH, width=int(1.5 * k))
    im.resize((size, size), Image.LANCZOS).save(f"{out}/{name}.png")

logo(40, "logo")
logo(28, "logoSmall")

# Build: a check mark; Undo: an arrow curling back. Each with a dimmed copy
# for when it can't be used (nothing to build / nothing to undo).
def action_icons():
    for name, draw in (("build", lambda d: d.line(p(9, 21, 17, 29, 31, 11), fill=DASH, width=int(5 * S), joint="curve")),
                       ("undo", None)):
        im, d = canvas()
        if name == "undo":
            d.arc(p(9, 11, 31, 33), 200, 90, fill=DASH, width=int(4 * S))
            d.polygon(p(4, 13, 15, 9, 13, 21), fill=DASH)
        else:
            draw(d)
        save(im, name)
        dim = Image.open(f"{out}/{name}.png").copy()
        alpha = dim.getchannel("A").point(lambda a: a * 35 // 100)
        dim.putalpha(alpha)
        dim.save(f"{out}/{name}Off.png")

action_icons()

# the game's own road tool: a straight road with dashes, plain
im, d = canvas()
d.line(p(20, 4, 20, 36), fill=EXIST, width=int(12 * S))
for y0 in (7, 17, 27):
    d.line(p(20, y0, 20, y0 + 5), fill=DASH, width=int(1.6 * S))
save(im, "gameTool")

# page arrows for the list along the road
for name, pts in (("pageBack", (25, 10, 13, 20, 25, 30)), ("pageNext", (15, 10, 27, 20, 15, 30))):
    im, d = canvas()
    d.polygon(p(*pts), fill=DASH)
    save(im, name)

# a background for buttons that can't be used right now
im, d = canvas()
d.rounded_rectangle(p(1, 1, 39, 39), radius=8 * S, fill=(30, 34, 40, 150), outline=(70, 75, 85, 200), width=2 * S)
save(im, "bgOff")

# the RR button's backgrounds (46 px, like the game's menu buttons)
for name, fill, edge in (("button", (35, 45, 60, 235), (100, 120, 145, 255)),
                         ("buttonHover", (55, 75, 100, 240), (160, 190, 225, 255)),
                         ("buttonPressed", (40, 95, 150, 245), (150, 200, 255, 255))):
    im = Image.new("RGBA", (46 * S, 46 * S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([S, S, 45 * S, 45 * S], radius=9 * S, fill=fill, outline=edge, width=2 * S)
    im.resize((46, 46), Image.LANCZOS).save(f"{out}/{name}.png")

# drawing modes: a curved piece, straight pieces with a rounded corner, and
# snapping (a fixed angle between two rays, with its arc)
im, d = canvas()
P = [(5, 34), (22, 34), (18, 6), (35, 6)]
pts = []
for i in range(33):
    t = i / 32.0
    w = ((1 - t) ** 3, 3 * t * (1 - t) ** 2, 3 * t * t * (1 - t), t ** 3)
    pts.append((sum(c * P[j][0] for j, c in enumerate(w)) * S, sum(c * P[j][1] for j, c in enumerate(w)) * S))
d.line(pts, fill=OURS, width=int(6 * S), joint="curve")
save(im, "curve")

im, d = canvas()
d.line(p(6, 34, 6, 18), fill=OURS, width=int(6 * S))
d.arc(p(6, 6, 30, 30), 180, 270, fill=OURS, width=int(6 * S))
d.line(p(18, 6, 34, 6), fill=OURS, width=int(6 * S))
save(im, "straight")

im, d = canvas()
d.line(p(7, 33, 35, 33), fill=DASH, width=int(3 * S))
d.line(p(7, 33, 30, 10), fill=DASH, width=int(3 * S))
d.arc(p(-7, 19, 21, 47), 315, 360, fill=OURS, width=int(3 * S))
d.ellipse(p(4, 30, 10, 36), fill=OURS)
save(im, "snap")

# preview sheet for checking by eye
names = ["junction", "bridge", "overpass", "underpass", "tunnel", "plus", "minus", "logo", "curve", "straight", "snap"]
sheet = Image.new("RGBA", (len(names) * 48, 48), (40, 50, 60, 255))
for i, nm in enumerate(names):
    bg = Image.open(f"{out}/bg.png"); ic = Image.open(f"{out}/{nm}.png")
    sheet.alpha_composite(bg, (i * 48 + 4, 4)); sheet.alpha_composite(ic, (i * 48 + 4, 4))
sheet = sheet.resize((sheet.width * 3, sheet.height * 3), Image.NEAREST)
sheet.save(sys.argv[2])
