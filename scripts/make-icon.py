"""Draws src/FolderLink/assets/FolderLink.ico: a yellow folder with a blue link
badge. Re-run after changing it: python3 scripts/make-icon.py"""
from PIL import Image, ImageDraw

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
# Folder: tab, then body.
d.rounded_rectangle((90, 190, 470, 340), 50, fill=(232, 168, 36))
d.rounded_rectangle((90, 260, 934, 840), 70, fill=(247, 190, 56))
d.rounded_rectangle((90, 330, 934, 840), 70, fill=(252, 207, 84))
# Link badge: blue circle, two white chain links.
cx, cy, r = 700, 690, 250
d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(37, 99, 235), outline=(255, 255, 255), width=36)
w = 46
for dx, dy in ((-70, 70), (70, -70)):
    x, y = cx + dx, cy + dy
    box = (x - 105, y - 62, x + 105, y + 62)
    link = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(link).rounded_rectangle(box, 62, outline=(255, 255, 255), width=w)
    img.alpha_composite(link.rotate(45, center=(x, y)))
sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
img.save("src/FolderLink/assets/FolderLink.ico", sizes=sizes)
img.resize((256, 256), Image.LANCZOS).save("src/FolderLink/assets/FolderLink.png")
