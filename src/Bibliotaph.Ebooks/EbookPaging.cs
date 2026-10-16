namespace Bibliotaph.Ebooks;

/// <summary>Where a page begins: a chapter (its place in the reading order), a block in it, and a character in that block.</summary>
public readonly record struct EbookLocator(int Chapter, int Block, int Offset = 0) : IComparable<EbookLocator>
{
    public int CompareTo(EbookLocator other) =>
        Chapter != other.Chapter ? Chapter.CompareTo(other.Chapter) : Block != other.Block ? Block.CompareTo(other.Block) : Offset.CompareTo(other.Offset);

    public static bool operator <(EbookLocator left, EbookLocator right) => left.CompareTo(right) < 0;

    public static bool operator <=(EbookLocator left, EbookLocator right) => left.CompareTo(right) <= 0;

    public static bool operator >(EbookLocator left, EbookLocator right) => left.CompareTo(right) > 0;

    public static bool operator >=(EbookLocator left, EbookLocator right) => left.CompareTo(right) >= 0;
}

/// <summary>One page of an e-book: its text, the printed page number when it is a printed page, and where it begins.</summary>
public sealed record EbookPage(int Index, string? Label, string Text, EbookLocator Start);

/// <summary>A contents entry and the page it opens at.</summary>
public sealed record EbookOutlineItem(string Title, int PageIndex, int Depth);

/// <summary>An e-book cut into pages, and its contents mapped to them.</summary>
public sealed record EbookLayout(IReadOnlyList<EbookPage> Pages, IReadOnlyList<EbookOutlineItem> Outline, bool PrintPages);

/// <summary>
/// Cuts an e-book into pages, which search, evidence, notes and binders all point at. The rule depends only on the
/// file's content, never on how a window draws it, so page 37 is the same page on every PC and in every version of the
/// app:
/// <list type="number">
/// <item>A fixed-layout book has a page per chapter file, as it was drawn.</item>
/// <item>A book that marks where its printed pages began (a page list, or page-break markers) has those pages, with their
/// printed numbers. What comes before the first printed page (a cover, a title page) is cut as in 3.</item>
/// <item>Otherwise each chapter file starts a page, and a page ends before the block that would take it past
/// <see cref="Target"/> characters, or before a heading once it has <see cref="HeadingBreak"/>. A block longer than
/// <see cref="Target"/> is split after a sentence.</item>
/// </list>
/// Changing the rule moves every e-book's pages, so it is versioned (<see cref="Version"/>) and never changed lightly.
/// </summary>
public static class EbookPaging
{
    public const int Version = 1;

    /// <summary>About a printed page of a novel, half a dense two-column rules page: small enough to point at precisely.</summary>
    public const int Target = 2500;

    public const int HeadingBreak = 1250;

    /// <summary>Fewer marked printed pages than this, and the markers are taken as stray.</summary>
    const int MinPrintPages = 3;

    /// <summary>Reads every chapter of <paramref name="book"/> and cuts it into pages.</summary>
    public static EbookLayout Cut(IEbook book, CancellationToken ct = default)
    {
        var chapters = new List<HtmlChapter>(book.Spine.Count);
        foreach (var item in book.Spine)
        {
            ct.ThrowIfCancellationRequested();
            chapters.Add(book.IsEncrypted(item.Path) ? HtmlChapter.Empty : HtmlText.Read(book.ReadText(item.Path)));
        }
        return Cut(book.Spine, chapters, book.PageList, book.Contents);
    }

    internal static EbookLayout Cut(IReadOnlyList<EpubSpineItem> spine, IReadOnlyList<HtmlChapter> chapters,
        IReadOnlyList<EpubNavPoint> pageList, IReadOnlyList<EpubNavPoint> contents)
    {
        var chapterOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < spine.Count; i++) chapterOf.TryAdd(spine[i].Path, i);

        EbookLocator? Locate(EpubNavPoint point)
        {
            if (!chapterOf.TryGetValue(point.Path, out var chapter)) return null;
            if (point.Fragment is null) return new EbookLocator(chapter, 0);
            return chapters[chapter].Ids.TryGetValue(point.Fragment, out var block) ? new EbookLocator(chapter, block) : null;
        }

        var breaks = PrintBreaks(chapters, pageList, Locate);
        var builder = new Builder(chapters);
        if (spine.All(s => s.FixedLayout)) builder.OnePerChapter(breaks);
        else if (breaks.Count >= MinPrintPages) builder.Printed(breaks);
        else builder.Flowing(0, chapters.Count, default);

        var pages = builder.Pages;
        var outline = new List<EbookOutlineItem>();
        foreach (var point in contents)
            if (Locate(point) is { } at && point.Title.Length > 0)
                outline.Add(new EbookOutlineItem(point.Title, PageAt(pages, at), point.Depth));
        return new EbookLayout(pages, outline, breaks.Count >= MinPrintPages && !spine.All(s => s.FixedLayout));
    }

    /// <summary>The page holding <paramref name="at"/>: the last one that starts at or before it.</summary>
    public static int PageAt(IReadOnlyList<EbookPage> pages, EbookLocator at)
    {
        int low = 0, high = pages.Count - 1, found = 0;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (pages[mid].Start <= at)
            {
                found = mid;
                low = mid + 1;
            }
            else high = mid - 1;
        }
        return found;
    }

    /// <summary>Printed page starts: the page list's where it names places in the book, else the markers in the chapters.</summary>
    static List<(EbookLocator At, string? Label)> PrintBreaks(IReadOnlyList<HtmlChapter> chapters, IReadOnlyList<EpubNavPoint> pageList,
        Func<EpubNavPoint, EbookLocator?> locate)
    {
        var fromList = pageList.Select(p => (At: locate(p), Label: p.Title.Length > 0 ? p.Title : null))
            .Where(p => p.At is not null).Select(p => (At: p.At!.Value, p.Label)).ToList();
        var breaks = fromList.Count >= MinPrintPages ? fromList
            : [.. chapters.SelectMany((c, i) => c.Breaks.Select(b => (At: new EbookLocator(i, b.Block), b.Label)))];
        // In reading order; a page list that lists a place twice keeps the first.
        return [.. breaks.Select((b, i) => (b, i)).OrderBy(x => x.b.At).ThenBy(x => x.i).Select(x => x.b).DistinctBy(b => (b.At, b.Label))];
    }

    sealed class Builder(IReadOnlyList<HtmlChapter> chapters)
    {
        readonly List<string> _parts = [];
        int _length;
        EbookLocator _start;
        string? _label;
        bool _open;

        public List<EbookPage> Pages { get; } = [];

        void Begin(EbookLocator at, string? label)
        {
            End();
            _start = at;
            _label = label;
            _open = true;
        }

        void End()
        {
            if (!_open) return;
            Pages.Add(new EbookPage(Pages.Count, _label, string.Join('\n', _parts), _start));
            _parts.Clear();
            _length = 0;
            _open = false;
        }

        void Add(string text)
        {
            _parts.Add(text);
            _length += text.Length + 1;
        }

        public void OnePerChapter(List<(EbookLocator At, string? Label)> breaks)
        {
            for (var c = 0; c < chapters.Count; c++)
            {
                Begin(new EbookLocator(c, 0), breaks.FirstOrDefault(b => b.At.Chapter == c).Label);
                foreach (var block in chapters[c].Blocks) Add(block.Text);
            }
            End();
        }

        public void Printed(List<(EbookLocator At, string? Label)> breaks)
        {
            var first = breaks[0].At;
            // Before the first printed page: a cover, a title page, cut as a flowing book is.
            if (first > new EbookLocator(0, 0)) Flowing(0, first.Chapter + 1, first);

            var next = 0;
            for (var c = first.Chapter; c < chapters.Count; c++)
            {
                var blocks = chapters[c].Blocks;
                for (var b = c == first.Chapter ? first.Block : 0; b <= blocks.Count; b++)
                {
                    // Every break here starts a page, so blank printed pages keep their numbers.
                    while (next < breaks.Count && breaks[next].At <= new EbookLocator(c, b))
                    {
                        Begin(new EbookLocator(c, b), breaks[next].Label);
                        next++;
                    }
                    if (b < blocks.Count) Add(blocks[b].Text);
                }
            }
            End();
        }

        /// <summary>Chapters <paramref name="from"/> up to <paramref name="to"/>, stopping at <paramref name="stop"/> when it is set.</summary>
        public void Flowing(int from, int to, EbookLocator stop)
        {
            var bounded = stop != default;
            for (var c = from; c < to; c++)
            {
                var blocks = chapters[c].Blocks;
                var end = bounded && c == stop.Chapter ? stop.Block : blocks.Count;
                if (bounded && c == stop.Chapter && end == 0) break;
                Begin(new EbookLocator(c, 0), null);
                for (var b = 0; b < end; b++)
                {
                    var block = blocks[b];
                    var offset = 0;
                    foreach (var piece in Pieces(block.Text))
                    {
                        var startsHeading = offset == 0 && block.Heading && _length >= HeadingBreak;
                        if (_length > 0 && (_length + piece.Length > Target || startsHeading)) Begin(new EbookLocator(c, b, offset), null);
                        Add(piece.Trim());
                        offset += piece.Length;
                    }
                }
                // A chapter of pictures only still has its page, so a cover is page 1.
            }
            End();
        }

        /// <summary>A block in pieces of at most <see cref="Target"/> characters, cut after a sentence where there is one.</summary>
        static IEnumerable<string> Pieces(string text)
        {
            var start = 0;
            while (text.Length - start > Target)
            {
                var cut = Cut(text, start);
                yield return text[start..cut];
                start = cut;
            }
            yield return text[start..];
        }

        static int Cut(string text, int start)
        {
            var limit = start + Target;
            for (var i = limit - 1; i > start + Target / 2; i--)
                if (text[i] == ' ' && text[i - 1] is '.' or '!' or '?' or ':' or ';') return i + 1;
            for (var i = limit - 1; i > start + Target / 2; i--)
                if (text[i] == ' ') return i + 1;
            return limit;
        }
    }
}
