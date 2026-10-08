"""Builds the app icon from the mockup's monogram (.bt-monogram): "b." in Libre Caslon Display,
light text on a dark tile with a larger top-right corner.

Writes src/Bibliotaph.App/Assets/Bibliotaph.ico (one PNG per size, each drawn at that size so the
tile edges land on whole pixels) and Bibliotaph.svg (the same geometry, glyphs as outlines).

    python tools/AppIcon/make_icon.py

Needs Pillow and fontTools. The font is read from src/Bibliotaph.App/Fonts.
"""
import io
import struct
from pathlib import Path

from fontTools.pens.basePen import BasePen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
FONT = ROOT / "src/Bibliotaph.App/Fonts/LibreCaslonDisplay-Regular.ttf"
OUT = ROOT / "src/Bibliotaph.App/Assets"

# The monogram in CSS px, from the mockup.
TILE_W, TILE_H = 30.0, 34.0
RADII = (3.0, 9.0, 3.0, 3.0)  # top-left, top-right, bottom-right, bottom-left
FONT_SIZE = 31.0
PADDING_BOTTOM = 3.0
TEXT = "b."
TILE_COLOR = (0x23, 0x2B, 0x26, 255)  # --bt-text (light)
GLYPH_COLOR = (0xF9, 0xF8, 0xF5, 255)  # --bt-bg (light)
# A slightly lighter rim keeps the dark tile visible on a dark taskbar (Fluent's "contrast stroke").
RIM_COLOR = (0x40, 0x4A, 0x43, 255)

CANVAS = 36.0  # one unit of margin above and below the tile
SIZES = (16, 20, 24, 32, 40, 48, 64, 256)
SUPERSAMPLE = 16
# Libre Caslon's hairlines vanish at small sizes, so the glyphs are thickened there (output px per side).
EMBOLDEN = {16: 0.3, 20: 0.25, 24: 0.2, 32: 0.12}


class FlattenPen(BasePen):
    """Collects each contour as a polygon, with curves split into short lines."""

    def __init__(self, glyph_set, steps=24):
        super().__init__(glyph_set)
        self.steps = steps
        self.contours = []
        self._current = []

    def _moveTo(self, pt):
        self._current = [pt]

    def _lineTo(self, pt):
        self._current.append(pt)

    def _qCurveToOne(self, pt1, pt2):
        x0, y0 = self._getCurrentPoint()
        for i in range(1, self.steps + 1):
            t = i / self.steps
            u = 1 - t
            self._current.append((u * u * x0 + 2 * u * t * pt1[0] + t * t * pt2[0],
                                  u * u * y0 + 2 * u * t * pt1[1] + t * t * pt2[1]))

    def _curveToOne(self, pt1, pt2, pt3):
        x0, y0 = self._getCurrentPoint()
        for i in range(1, self.steps + 1):
            t = i / self.steps
            u = 1 - t
            self._current.append((u ** 3 * x0 + 3 * u * u * t * pt1[0] + 3 * u * t * t * pt2[0] + t ** 3 * pt3[0],
                                  u ** 3 * y0 + 3 * u * u * t * pt1[1] + 3 * u * t * t * pt2[1] + t ** 3 * pt3[1]))

    def _closePath(self):
        if self._current:
            self.contours.append(self._current)
        self._current = []

    _endPath = _closePath


def glyph_layout(font):
    """Returns (glyph name, x, baseline) for each character, in tile units, laid out as CSS does
    with line-height 1 and the text centred in the tile above the bottom padding."""
    upm = font["head"].unitsPerEm
    hhea = font["hhea"]
    scale = FONT_SIZE / upm
    cmap = font.getBestCmap()
    names = [cmap[ord(c)] for c in TEXT]
    advance = sum(font["hmtx"][n][0] for n in names) * scale
    half_leading = (FONT_SIZE - (hhea.ascent - hhea.descent) * scale) / 2
    content_h = TILE_H - PADDING_BOTTOM
    baseline = (content_h - FONT_SIZE) / 2 + half_leading + hhea.ascent * scale
    x = (TILE_W - advance) / 2
    placed = []
    for n in names:
        placed.append((n, x, baseline))
        x += font["hmtx"][n][0] * scale
    return placed, scale


def tile_points(x, y, w, h, radii, k, steps=24):
    """Polygon for a rectangle with a different radius on each corner (radii scaled by k)."""
    import math
    tl, tr, br, bl = (r * k for r in radii)
    corners = [  # centre x, centre y, radius, start angle
        (x + w - tr, y + tr, tr, -90),
        (x + w - br, y + h - br, br, 0),
        (x + bl, y + h - bl, bl, 90),
        (x + tl, y + tl, tl, 180),
    ]
    pts = []
    for cx, cy, r, a0 in corners:
        for i in range(steps + 1):
            a = math.radians(a0 + 90 * i / steps)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts


def render(font, size):
    s = size * SUPERSAMPLE
    # Snap the tile to whole pixels at this size, keeping the mockup's proportions.
    tile_h = round(TILE_H / CANVAS * size)
    tile_w = round(TILE_W / TILE_H * tile_h)
    left = (size - tile_w) // 2
    top = (size - tile_h) // 2
    k = tile_h / TILE_H * SUPERSAMPLE  # tile units to supersampled pixels
    ox, oy = left * SUPERSAMPLE, top * SUPERSAMPLE

    tile = Image.new("L", (s, s), 0)
    ImageDraw.Draw(tile).polygon(tile_points(ox, oy, tile_w * SUPERSAMPLE, tile_h * SUPERSAMPLE, RADII, k), fill=255)
    rim = 1 if size <= 64 else 2
    w = rim * SUPERSAMPLE
    inner = Image.new("L", (s, s), 0)
    inner_radii = tuple(max(r * k - w, 0) / k for r in RADII)
    ImageDraw.Draw(inner).polygon(
        tile_points(ox + w, oy + w, (tile_w - 2 * rim) * SUPERSAMPLE, (tile_h - 2 * rim) * SUPERSAMPLE, inner_radii, k), fill=255)

    glyphs = Image.new("L", (s, s), 0)
    placed, scale = glyph_layout(font)
    glyph_set = font.getGlyphSet()
    for name, gx, baseline in placed:
        pen = FlattenPen(glyph_set)
        glyph_set[name].draw(pen)
        for contour in pen.contours:
            pts = [(ox + (gx + px * scale) * k, oy + (baseline - py * scale) * k) for px, py in contour]
            layer = Image.new("L", (s, s), 0)
            ImageDraw.Draw(layer).polygon(pts, fill=255)
            glyphs = ImageChops.logical_xor(glyphs.convert("1"), layer.convert("1")).convert("L")
    grow = round(EMBOLDEN.get(size, 0) * SUPERSAMPLE)
    if grow:
        glyphs = glyphs.filter(ImageFilter.MaxFilter(2 * grow + 1))
    glyphs = ImageChops.multiply(glyphs, inner)

    image = Image.new("RGBA", (s, s), TILE_COLOR[:3] + (0,))
    image.paste(Image.new("RGBA", (s, s), RIM_COLOR), mask=tile)
    image.paste(Image.new("RGBA", (s, s), TILE_COLOR), mask=inner)
    image.paste(Image.new("RGBA", (s, s), GLYPH_COLOR), mask=glyphs)
    return image.resize((size, size), Image.Resampling.LANCZOS)


def write_ico(path, images):
    """ICO with PNG-compressed entries, which Windows has read since Vista."""
    blobs = []
    for im in images:
        buf = io.BytesIO()
        im.save(buf, format="PNG", optimize=True)
        blobs.append(buf.getvalue())
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for im, blob in zip(images, blobs):
        w, h = im.size
        entries += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    path.write_bytes(header + entries + b"".join(blobs))


def write_svg(path, font):
    placed, scale = glyph_layout(font)
    glyph_set = font.getGlyphSet()
    x0, y0 = (CANVAS - TILE_W) / 2, (CANVAS - TILE_H) / 2
    pts = tile_points(x0, y0, TILE_W, TILE_H, RADII, 1, steps=8)
    tile_d = "M" + " L".join(f"{x:.3f},{y:.3f}" for x, y in pts) + " Z"
    glyph_d = []
    for name, gx, baseline in placed:
        pen = SVGPathPen(glyph_set, ntos=lambda v: f"{v:.3f}".rstrip("0").rstrip("."))
        glyph_set[name].draw(TransformPen(pen, (scale, 0, 0, -scale, x0 + gx, y0 + baseline)))
        glyph_d.append(pen.getCommands())
    hexcolor = lambda c: "#%02x%02x%02x" % c[:3]
    path.write_text(
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {CANVAS:g} {CANVAS:g}">\n'
        f'  <path fill="{hexcolor(TILE_COLOR)}" d="{tile_d}"/>\n'
        f'  <path fill="{hexcolor(GLYPH_COLOR)}" d="{" ".join(glyph_d)}"/>\n'
        "</svg>\n",
        encoding="utf-8")


def main():
    font = TTFont(FONT)
    OUT.mkdir(parents=True, exist_ok=True)
    images = [render(font, size) for size in SIZES]
    write_ico(OUT / "Bibliotaph.ico", images)
    write_svg(OUT / "Bibliotaph.svg", font)
    print(f"Wrote {OUT / 'Bibliotaph.ico'} ({', '.join(map(str, SIZES))} px) and Bibliotaph.svg")


if __name__ == "__main__":
    main()
