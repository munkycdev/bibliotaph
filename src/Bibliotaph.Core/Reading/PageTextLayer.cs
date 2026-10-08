using System.Text;

namespace Bibliotaph.Core.Reading;

/// <summary>A rectangle on a page in PDF points, origin bottom-left, so <see cref="Top"/> &gt; <see cref="Bottom"/>.</summary>
public readonly record struct PageRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Bottom && y <= Top;

    public PageRect Union(PageRect other) =>
        new(Math.Min(Left, other.Left), Math.Max(Top, other.Top), Math.Max(Right, other.Right), Math.Min(Bottom, other.Bottom));

    /// <summary>How far a point is from the rectangle; 0 inside it.</summary>
    public double DistanceTo(double x, double y)
    {
        var dx = Math.Max(0, Math.Max(Left - x, x - Right));
        var dy = Math.Max(0, Math.Max(Bottom - y, y - Top));
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}

/// <summary>A run of characters on a page by index.</summary>
public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;
    public bool IsEmpty => Length <= 0;
}

/// <summary>
/// A page's selectable text with one box per character, in reading order: PDFium's characters, or words read by OCR
/// spread across their letters. Selection works on character indexes, as in other PDF readers, so a selection always
/// copies the text in the order the page is read.
/// </summary>
public sealed class PageTextLayer
{
    public PageTextLayer(string text, IReadOnlyList<PageRect> boxes)
    {
        if (text.Length != boxes.Count) throw new ArgumentException($"{text.Length} characters but {boxes.Count} boxes.", nameof(boxes));
        Text = text;
        Boxes = boxes;
    }

    public static PageTextLayer Empty { get; } = new("", []);

    public string Text { get; }

    /// <summary>One per character of <see cref="Text"/>; empty for spaces and line breaks the reader generated.</summary>
    public IReadOnlyList<PageRect> Boxes { get; }

    public bool IsEmpty => Boxes.All(b => b.IsEmpty);

    public TextRange All => new(0, Text.Length);

    /// <summary>
    /// Builds a layer from OCR words in reading order. Each word's box is shared out across its letters; a word that
    /// starts below the last one starts a new line.
    /// </summary>
    public static PageTextLayer FromWords(IEnumerable<(string Text, PageRect Box)> words)
    {
        var text = new StringBuilder();
        var boxes = new List<PageRect>();
        PageRect? previous = null;
        foreach (var (word, box) in words)
        {
            if (word.Length == 0) continue;
            if (previous is { } last)
            {
                text.Append(box.Top < last.Bottom + (last.Height / 2) ? '\n' : ' ');
                boxes.Add(default);
            }
            var step = box.Width / word.Length;
            for (var i = 0; i < word.Length; i++)
            {
                text.Append(word[i]);
                boxes.Add(new PageRect(box.Left + (step * i), box.Top, box.Left + (step * (i + 1)), box.Bottom));
            }
            previous = box;
        }
        return new PageTextLayer(text.ToString(), boxes);
    }

    /// <summary>The character at a point, or the nearest one: first on the same line, then anywhere. Null with no text.</summary>
    public int? HitTest(double x, double y)
    {
        int? sameLine = null, nearest = null;
        double sameLineDistance = double.MaxValue, nearestDistance = double.MaxValue;
        for (var i = 0; i < Boxes.Count; i++)
        {
            var box = Boxes[i];
            if (box.IsEmpty) continue;
            if (box.Contains(x, y)) return i;
            var distance = box.DistanceTo(x, y);
            if (y >= box.Bottom && y <= box.Top && distance < sameLineDistance) (sameLine, sameLineDistance) = (i, distance);
            if (distance < nearestDistance) (nearest, nearestDistance) = (i, distance);
        }
        return sameLine ?? nearest;
    }

    /// <summary>The characters from one index to another, whichever way the drag went, both ends included.</summary>
    public static TextRange Between(int anchor, int focus) => new(Math.Min(anchor, focus), Math.Abs(focus - anchor) + 1);

    /// <summary>The word around a character (double-click), or just that character when it is not part of a word.</summary>
    public TextRange WordAt(int index)
    {
        if (index < 0 || index >= Text.Length) return default;
        if (!IsWordChar(Text[index])) return new TextRange(index, 1);
        var start = index;
        var end = index;
        while (start > 0 && IsWordChar(Text[start - 1])) start--;
        while (end < Text.Length - 1 && IsWordChar(Text[end + 1])) end++;
        return new TextRange(start, end - start + 1);
    }

    /// <summary>The line around a character (triple-click), without its line break.</summary>
    public TextRange LineAt(int index)
    {
        if (index < 0 || index >= Text.Length) return default;
        var start = index;
        var end = index;
        while (start > 0 && !IsLineBreak(Text[start - 1])) start--;
        while (end < Text.Length - 1 && !IsLineBreak(Text[end + 1])) end++;
        return new TextRange(start, end - start + 1);
    }

    /// <summary>The text of a range for the clipboard: Windows line breaks, no control characters.</summary>
    public string TextOf(TextRange range)
    {
        if (range.IsEmpty) return "";
        var start = Math.Clamp(range.Start, 0, Text.Length);
        var end = Math.Clamp(range.End, start, Text.Length);
        var builder = new StringBuilder(end - start);
        for (var i = start; i < end; i++)
        {
            var c = Text[i];
            if (c == '\r') continue;
            if (c == '\n') builder.Append("\r\n");
            else if (c == '\t' || !char.IsControl(c)) builder.Append(c);
        }
        return builder.ToString().Trim();
    }

    /// <summary>Rectangles covering a range, one per run of characters on the same line, for drawing a selection.</summary>
    public IReadOnlyList<PageRect> RectsOf(TextRange range)
    {
        var rects = new List<PageRect>();
        PageRect? current = null;
        var end = Math.Min(range.End, Boxes.Count);
        for (var i = Math.Max(0, range.Start); i < end; i++)
        {
            var box = Boxes[i];
            if (box.IsEmpty) continue;
            if (current is { } run && SameLine(run, box) && box.Left >= run.Left - 1) current = run.Union(box);
            else
            {
                if (current is { } done) rects.Add(done);
                current = box;
            }
        }
        if (current is { } last) rects.Add(last);
        return rects;
    }

    static bool SameLine(PageRect a, PageRect b)
    {
        var overlap = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        return overlap > Math.Min(a.Height, b.Height) / 2;
    }

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’' or '-' or '_';

    static bool IsLineBreak(char c) => c is '\r' or '\n';
}
