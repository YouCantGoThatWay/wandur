#!/usr/bin/env python3
"""Six vector logo marks for Wandur, written as SVG and rendered to a contact sheet.

Each mark lives on a 64 by 64 grid, drawn in amber on near-black, then in black on white, then
rendered at 512, 64, 32 and 16 pixels so the sheet shows what survives at favicon size.
"""
import os, subprocess
from PIL import Image, ImageDraw

AMBER = "#E0A64A"
INK = "#161412"
here = os.path.dirname(os.path.abspath(__file__))

def svg(body, fg, bg, size=64):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="{size}" height="{size}">'
            f'<rect width="64" height="64" rx="12" fill="{bg}"/>{body.replace("FG", fg)}</svg>')

marks = {
    # A: a threshold. Two uprights and a lintel with an open bottom, a path narrowing into the gap.
    "threshold": '''
        <path d="M16 46 V18 H48 V46" fill="none" stroke="FG" stroke-width="6" stroke-linejoin="round" stroke-linecap="round"/>
        <path d="M22 52 L32 30 L42 52 Z" fill="FG"/>
    ''',
    # B: a route. One continuous line of three turns ending in a point of light.
    "route": '''
        <path d="M14 50 H26 V36 H38 V22 H46" fill="none" stroke="FG" stroke-width="6" stroke-linejoin="round" stroke-linecap="round"/>
        <circle cx="48" cy="22" r="5" fill="FG"/>
    ''',
    # C: brackets as a doorway with the path between them.
    "brackets": '''
        <path d="M24 14 H14 V50 H24" fill="none" stroke="FG" stroke-width="6" stroke-linejoin="round" stroke-linecap="round"/>
        <path d="M40 14 H50 V50 H40" fill="none" stroke="FG" stroke-width="6" stroke-linejoin="round" stroke-linecap="round"/>
        <path d="M28 50 L32 30 L36 50 Z" fill="FG"/>
    ''',
    # D: a lantern in three shapes: cap, body, light.
    "lantern": '''
        <path d="M20 22 L32 12 L44 22 Z" fill="FG"/>
        <rect x="22" y="26" width="20" height="24" rx="3" fill="none" stroke="FG" stroke-width="5"/>
        <circle cx="32" cy="38" r="4" fill="FG"/>
    ''',
    # E: a compass needle that reads as a cursor, pointing where you are going.
    "needle": '''
        <path d="M32 10 L44 40 L32 34 L20 40 Z" fill="FG"/>
        <path d="M22 50 H42" stroke="FG" stroke-width="5" stroke-linecap="round"/>
    ''',
    # F: a W drawn as one path, the middle peak a point of light.
    "w-path": '''
        <path d="M12 20 L20 46 L32 28 L44 46 L52 20" fill="none" stroke="FG" stroke-width="6" stroke-linejoin="round" stroke-linecap="round"/>
        <circle cx="32" cy="16" r="4" fill="FG"/>
    ''',
}

def render(name, body, fg, bg, size):
    path = os.path.join(here, f"{name}-{'dark' if bg == INK else 'light'}-{size}.png")
    svg_path = os.path.join(here, f"{name}-{'dark' if bg == INK else 'light'}.svg")
    with open(svg_path, "w") as f: f.write(svg(body, fg, bg))
    subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size), "-o", path, svg_path], check=True)
    return Image.open(path).convert("RGBA")

cols = len(marks); cell = 300; pad = 24; label = 26
sheet = Image.new("RGB", (pad + cols * (cell + pad), pad + 2 * (cell + label + pad) + 90), (10, 9, 9))
draw = ImageDraw.Draw(sheet)
for i, (name, body) in enumerate(marks.items()):
    x = pad + i * (cell + pad)
    big = render(name, body, AMBER, INK, 256).resize((cell, cell), Image.LANCZOS)
    sheet.paste(big, (x, pad))
    draw.text((x + 4, pad + cell + 6), name, fill=(200, 196, 190))
    light = render(name, body, INK, "#F6F1E8", 256).resize((cell, cell), Image.LANCZOS)
    y2 = pad + cell + label + pad
    sheet.paste(light, (x, y2))
    # The small sizes in a strip under the light version: 64, 32, 16, each on dark.
    sx = x
    for s in (64, 32, 16):
        small = render(name, body, AMBER, INK, s)
        sheet.paste(small, (sx, y2 + cell + 12))
        sx += s + 10
out = os.path.join(here, "marks-sheet.png")
sheet.save(out, optimize=True)
print(out)
