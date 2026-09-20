# Design working files

- `marks/`: six candidate logo marks drawn as SVG on 2026-09-20 (threshold, route, brackets, lantern,
  needle, w-path) with `marks.py`, which renders `marks-sheet.png` at 512, 64, 32 and 16 pixels in amber
  on dark and ink on light. The owner has not chosen; the reviewer's pick was the brackets mark with a
  path, to refine with heavier strokes and a tapering path. Requires `rsvg-convert` and Pillow.
- `logo-proposals.sh`: the image-model prompt for logo directions; it produced poor results (chunky
  clip art), kept for the prompt text only. Vector marks drawn as code are the better route.
