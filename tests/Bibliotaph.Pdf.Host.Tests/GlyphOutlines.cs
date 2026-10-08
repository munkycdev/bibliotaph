using System.Buffers.Binary;
using PdfSharp.Drawing;

namespace Bibliotaph.Pdf.Host.Tests;

/// <summary>
/// Turns a string into filled glyph outlines read straight from a TrueType font's glyf table. PDFsharp's
/// cross-platform build leaves <see cref="XGraphicsPath.AddString(string, XFontFamily, XFontStyleEx, double, XPoint, XStringFormat)"/>
/// empty, so the "scanned" fixture draws its outlines this way: text you can see, with no text layer to extract.
/// Handles what DM Sans needs for basic Latin: a format 4 cmap and simple (not composite) glyphs.
/// </summary>
sealed class GlyphOutlines
{
    readonly byte[] _font;
    readonly Dictionary<string, (int Offset, int Length)> _tables = [];
    readonly int _unitsPerEm;
    readonly bool _longLoca;
    readonly int _metricCount;

    public GlyphOutlines(byte[] font)
    {
        _font = font;
        int tableCount = U16(4);
        for (var i = 0; i < tableCount; i++)
        {
            var record = 12 + 16 * i;
            var tag = System.Text.Encoding.ASCII.GetString(font, record, 4);
            _tables[tag] = ((int)U32(record + 8), (int)U32(record + 12));
        }
        _unitsPerEm = U16(Table("head") + 18);
        _longLoca = I16(Table("head") + 50) == 1;
        _metricCount = U16(Table("hhea") + 34);
    }

    /// <summary>Adds <paramref name="text"/> to <paramref name="path"/>, left end of the baseline at <paramref name="origin"/>.</summary>
    public void AddString(XGraphicsPath path, string text, double size, XPoint origin)
    {
        var scale = size / _unitsPerEm;
        var x = origin.X;
        foreach (var ch in text)
        {
            var glyph = GlyphIndex(ch);
            AddGlyph(path, glyph, p => new XPoint(x + p.X * scale, origin.Y - p.Y * scale));
            x += Advance(glyph) * scale;
        }
    }

    void AddGlyph(XGraphicsPath path, int glyph, Func<XPoint, XPoint> place)
    {
        var (start, end) = GlyphRange(glyph);
        if (end <= start) return; // a space: no outline
        int contours = I16(start);
        if (contours < 0) throw new NotSupportedException($"Glyph {glyph} is composite, which this fixture reader does not handle.");

        var endPoints = new int[contours];
        for (var i = 0; i < contours; i++) endPoints[i] = U16(start + 10 + 2 * i);
        var pointCount = contours == 0 ? 0 : endPoints[^1] + 1;
        var at = start + 10 + 2 * contours;
        at += 2 + U16(at); // skip the instructions

        var flags = new byte[pointCount];
        for (var i = 0; i < pointCount;)
        {
            var flag = _font[at++];
            flags[i++] = flag;
            if ((flag & 8) == 0) continue;
            for (int repeat = _font[at++]; repeat > 0; repeat--) flags[i++] = flag;
        }
        var xs = ReadCoordinates(flags, ref at, shortBit: 2, sameBit: 16);
        var ys = ReadCoordinates(flags, ref at, shortBit: 4, sameBit: 32);

        var first = 0;
        foreach (var last in endPoints)
        {
            var points = new List<(XPoint Point, bool OnCurve)>();
            for (var i = first; i <= last; i++) points.Add((new XPoint(xs[i], ys[i]), (flags[i] & 1) != 0));
            AddContour(path, points, place);
            first = last + 1;
        }
    }

    /// <summary>A closed quadratic contour, with its implied on-curve midpoints, as lines and cubic Béziers.</summary>
    static void AddContour(XGraphicsPath path, List<(XPoint Point, bool OnCurve)> points, Func<XPoint, XPoint> place)
    {
        var n = points.Count;
        if (n == 0) return;
        // Start on an on-curve point: the first, else the last, else the midpoint between them.
        var (start, first, count) = points[0].OnCurve ? (points[0].Point, 1, n - 1)
            : points[n - 1].OnCurve ? (points[n - 1].Point, 0, n - 1)
            : (Mid(points[n - 1].Point, points[0].Point), 0, n);

        path.StartFigure();
        var current = start;
        XPoint? control = null;
        for (var k = 0; k <= count; k++)
        {
            var (point, on) = k < count ? points[first + k] : (start, true);
            if (on)
            {
                if (control is { } q) Quad(current, q, point);
                else if (point != current) path.AddLine(place(current), place(point));
                current = point;
                control = null;
            }
            else
            {
                if (control is { } q)
                {
                    var mid = Mid(q, point);
                    Quad(current, q, mid);
                    current = mid;
                }
                control = point;
            }
        }
        path.CloseFigure();

        void Quad(XPoint from, XPoint q, XPoint to) =>
            path.AddBezier(place(from), place(from + (q - from) * (2.0 / 3)), place(to + (q - to) * (2.0 / 3)), place(to));
    }

    static XPoint Mid(XPoint a, XPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    int[] ReadCoordinates(byte[] flags, ref int at, int shortBit, int sameBit)
    {
        var values = new int[flags.Length];
        var value = 0;
        for (var i = 0; i < flags.Length; i++)
        {
            var flag = flags[i];
            if ((flag & shortBit) != 0)
                value += (flag & sameBit) != 0 ? _font[at++] : -_font[at++];
            else if ((flag & sameBit) == 0)
            {
                value += I16(at);
                at += 2;
            }
            values[i] = value;
        }
        return values;
    }

    int GlyphIndex(char ch)
    {
        var cmap = Table("cmap");
        int subtables = U16(cmap + 2);
        for (var i = 0; i < subtables; i++)
        {
            var record = cmap + 4 + 8 * i;
            if (U16(record) != 3 || U16(record + 2) != 1) continue;
            var table = cmap + (int)U32(record + 4);
            if (U16(table) != 4) break;
            var segments = U16(table + 6) / 2;
            var ends = table + 14;
            var starts = ends + 2 * segments + 2;
            var deltas = starts + 2 * segments;
            var rangeOffsets = deltas + 2 * segments;
            for (var s = 0; s < segments; s++)
            {
                if (ch > U16(ends + 2 * s)) continue;
                int first = U16(starts + 2 * s);
                if (ch < first) return 0;
                int rangeOffset = U16(rangeOffsets + 2 * s);
                if (rangeOffset == 0) return (ch + I16(deltas + 2 * s)) & 0xFFFF;
                var glyph = U16(rangeOffsets + 2 * s + rangeOffset + 2 * (ch - first));
                return glyph == 0 ? 0 : (glyph + I16(deltas + 2 * s)) & 0xFFFF;
            }
            return 0;
        }
        throw new NotSupportedException("The font has no Windows Unicode (3, 1) format 4 cmap.");
    }

    (int Start, int End) GlyphRange(int glyph)
    {
        var loca = Table("loca");
        var glyf = Table("glyf");
        return _longLoca
            ? (glyf + (int)U32(loca + 4 * glyph), glyf + (int)U32(loca + 4 * glyph + 4))
            : (glyf + 2 * U16(loca + 2 * glyph), glyf + 2 * U16(loca + 2 * glyph + 2));
    }

    int Advance(int glyph) => U16(Table("hmtx") + 4 * Math.Min(glyph, _metricCount - 1));

    int Table(string tag) => _tables.TryGetValue(tag, out var t) ? t.Offset : throw new NotSupportedException($"The font has no {tag} table.");

    int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(_font.AsSpan(at));

    short I16(int at) => BinaryPrimitives.ReadInt16BigEndian(_font.AsSpan(at));

    uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(_font.AsSpan(at));
}
