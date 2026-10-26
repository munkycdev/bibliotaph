# Usage: python make-logo.py <LibreCaslonDisplay-Regular.ttf> <out folder>   (pip install fonttools uharfbuzz)
# Builds the large-format logo from the sidebar's CSS: .bt-monogram (30x34 px, radii 3/9/3/3, "b." at 31 px, 3 px
# bottom padding) beside "Bibliotaph" at 23 px, 10 px apart, letter-spacing -0.7 px; drawn here at 10x. The glyphs
# are outlines rather than text, so the files look the same where the font isn't installed, such as Auth0's sign-in page.
import sys, uharfbuzz as hb
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen

FONT = sys.argv[1]; OUT = sys.argv[2]
tt = TTFont(FONT); gs = tt.getGlyphSet(); upem = tt['head'].unitsPerEm
ASC, DESC = 970 / upem, 266 / upem
blob = hb.Blob.from_file_path(FONT); face = hb.Face(blob); font = hb.Font(face)

def shaped(text, size, spacing, x, baseline):
    buf = hb.Buffer(); buf.add_str(text); buf.guess_segment_properties(); hb.shape(font, buf, {"kern": True, "liga": True})
    s = size / upem; parts = []
    for info, pos in zip(buf.glyph_infos, buf.glyph_positions):
        name = tt.getGlyphName(info.codepoint)
        pen = SVGPathPen(gs, ntos=lambda v: f"{v:.2f}".rstrip('0').rstrip('.'))
        gs[name].draw(TransformPen(pen, (s, 0, 0, -s, x + pos.x_offset * s, baseline - pos.y_offset * s)))
        parts.append(pen.getCommands())
        x += pos.x_advance * s + spacing
    return " ".join(p for p in parts if p), x

def width(text, size, spacing):
    return shaped(text, size, spacing, 0, 0)[1]

TILE_W, TILE_H, PAD_B = 300, 340, 30
SPACING = -7
tile = f"M30 0H{TILE_W-90}A90 90 0 0 1 {TILE_W} 90V{TILE_H-30}A30 30 0 0 1 {TILE_W-30} {TILE_H}H30A30 30 0 0 1 0 {TILE_H-30}V30A30 30 0 0 1 30 0Z"

# line-height 1: the line box is the font size, with the content area's overflow split above and below.
mono_size = 310
mono_base = (mono_size - (ASC + DESC) * mono_size) / 2 + ASC * mono_size
mono_x = (TILE_W - width("b.", mono_size, SPACING)) / 2
mono, _ = shaped("b.", mono_size, SPACING, mono_x, mono_base)

word_size = 230
word_base = (TILE_H - (ASC + DESC) * word_size) / 2 + ASC * word_size
word_x = TILE_W + 100
word, end = shaped("Bibliotaph", word_size, SPACING, word_x, word_base)
full_w = round(end - SPACING + 4)

def svg(w, h, body, title):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" role="img" aria-label="{title}">\n'
            f'  <title>{title}</title>\n{body}</svg>\n')

themes = {"": ("#232b26", "#f9f8f5"), "-dark": ("#e8ece6", "#1d211f")}
for suffix, (ink, paper) in themes.items():
    body = (f'  <path fill="{ink}" d="{tile}"/>\n  <path fill="{paper}" d="{mono}"/>\n  <path fill="{ink}" d="{word}"/>\n')
    open(f"{OUT}/bibliotaph-logo{suffix}.svg", "w").write(svg(full_w, TILE_H, body, "Bibliotaph"))
    body = f'  <path fill="{ink}" d="{tile}"/>\n  <path fill="{paper}" d="{mono}"/>\n'
    open(f"{OUT}/bibliotaph-monogram{suffix}.svg", "w").write(svg(TILE_W, TILE_H, body, "Bibliotaph"))
print(full_w, TILE_H)
