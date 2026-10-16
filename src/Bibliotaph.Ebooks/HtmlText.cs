using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Bibliotaph.Ebooks;

/// <summary>A paragraph, heading, list item or table row of a chapter, as plain text.</summary>
public sealed record HtmlBlock(string Text, bool Heading);

/// <summary>A printed page's start marked in a chapter: before block <see cref="Block"/>, with its printed label.</summary>
public sealed record HtmlPageBreak(int Block, string? Label);

/// <summary>
/// A chapter file as text: its blocks in reading order, the printed page starts it marks, and the block each element
/// id is in, so a link or a contents entry to <c>chapter.xhtml#id</c> can be found.
/// </summary>
public sealed record HtmlChapter(IReadOnlyList<HtmlBlock> Blocks, IReadOnlyList<HtmlPageBreak> Breaks, IReadOnlyDictionary<string, int> Ids)
{
    public static HtmlChapter Empty { get; } = new([], [], new Dictionary<string, int>());
}

/// <summary>
/// Reads a chapter's HTML as text with an HTML5 parser, which takes real-world markup that isn't well-formed XML (and
/// EPUB files often aren't). Scripts and styles are never run or read.
/// </summary>
public static partial class HtmlText
{
    /// <summary>Deeper than this, an element's text is taken whole: a crafted file could nest elements without end.</summary>
    const int MaxDepth = 256;

    static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "body", "caption", "center", "dd", "details", "dialog", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "li",
        "main", "nav", "ol", "p", "pre", "section", "summary", "table", "tbody", "tfoot", "thead", "tr", "ul",
    };

    static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { "head", "script", "style", "template", "noscript", "svg", "title" };

    static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>A chapter's text in blocks, with its page breaks and ids.</summary>
    public static HtmlChapter Read(string html)
    {
        var document = Parse(html);
        var walker = new Walker();
        walker.Walk(document.Body ?? (INode?)document.DocumentElement, 0);
        walker.Flush(heading: false);
        return new HtmlChapter(walker.Blocks, walker.Breaks, walker.Ids);
    }

    /// <summary>A fragment's text with its markup removed, as a description can carry.</summary>
    public static string Plain(string html)
    {
        if (!html.Contains('<', StringComparison.Ordinal) && !html.Contains('&', StringComparison.Ordinal)) return html.Trim();
        var chapter = Read(html);
        return string.Join(" ", chapter.Blocks.Select(b => b.Text)).Trim();
    }

    /// <summary>The first image a page shows (an <c>img</c>, or an SVG <c>image</c> as cover pages use), as written.</summary>
    public static string? FirstImage(string html)
    {
        var document = Parse(html);
        foreach (var element in document.QuerySelectorAll("img, image"))
        {
            var src = element.LocalName == "img" ? element.GetAttribute("src")
                : element.GetAttribute("xlink:href") ?? element.GetAttribute("http://www.w3.org/1999/xlink", "href") ?? element.GetAttribute("href");
            if (!string.IsNullOrWhiteSpace(src) && !src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return src.Trim();
        }
        return null;
    }

    /// <summary>
    /// Parses XHTML or HTML. An HTML5 parser reads <c>&lt;a id="x"/&gt;</c> as an element left open, which would swallow
    /// the text after it, so XHTML's self-closed elements are closed first.
    /// </summary>
    internal static IHtmlDocument Parse(string html)
    {
        var document = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument(CloseSelfClosed(html));
        Flatten(document);
        return document;
    }

    /// <summary>
    /// Replaces each element nested deeper than <see cref="MaxDepth"/> with its text, without recursion: the parser
    /// keeps any depth, and walking, reading text from or writing out a tree thousands deep overflows the stack.
    /// </summary>
    static void Flatten(IHtmlDocument document)
    {
        if (document.DocumentElement is not { } root) return;
        var open = new Stack<(INode Node, int Depth)>();
        open.Push((root, 0));
        while (open.TryPop(out var at))
        {
            foreach (var child in at.Node.ChildNodes.OfType<IElement>().ToList())
            {
                if (at.Depth + 1 < MaxDepth) open.Push((child, at.Depth + 1));
                else at.Node.ReplaceChild(document.CreateTextNode(TextOf(child)), child);
            }
        }
    }

    /// <summary>The text in a node, in reading order, gathered without recursion.</summary>
    static string TextOf(INode node)
    {
        var text = new StringBuilder();
        var next = new Stack<INode>();
        next.Push(node);
        while (next.TryPop(out var at))
        {
            if (at is IText t) text.Append(t.Data);
            else if (at is IElement { LocalName: not ("script" or "style") })
                for (var i = at.ChildNodes.Length - 1; i >= 0; i--) next.Push(at.ChildNodes[i]);
        }
        return text.ToString();
    }

    internal static string CloseSelfClosed(string html) => SelfClosed().Replace(html, m =>
        Void.Contains(m.Groups[1].Value.ToLowerInvariant()) ? m.Value : $"<{m.Groups[1].Value}{m.Groups[2].Value}></{m.Groups[1].Value}>");

    // Non-backtracking: matching time stays linear however a file is crafted.
    [GeneratedRegex("""<([A-Za-z][\w:.\-]*)((?:\s+[^\s/>="']+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'>]+))?)*)\s*/>""", RegexOptions.NonBacktracking)]
    private static partial Regex SelfClosed();

    /// <summary>Whether an element marks where a printed page began (EPUB 3 and DPUB-ARIA both have a way).</summary>
    internal static bool IsPageBreak(IElement element) =>
        element.GetAttribute("epub:type") is { } type && type.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("pagebreak")
        || element.GetAttribute("role") == "doc-pagebreak";

    internal static string? PageLabel(IElement element)
    {
        var label = element.GetAttribute("title") ?? element.GetAttribute("aria-label") ?? element.TextContent;
        return Collapse(label) is { Length: > 0 and <= 32 } text ? text : null;
    }

    internal static string Collapse(string text) => Spaces().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[ \t\f\v ]+")]
    private static partial Regex InlineSpaces();

    sealed class Walker
    {
        readonly StringBuilder _text = new();
        int _pre;

        public List<HtmlBlock> Blocks { get; } = [];

        public List<HtmlPageBreak> Breaks { get; } = [];

        public Dictionary<string, int> Ids { get; } = new(StringComparer.Ordinal);

        public void Walk(INode? node, int depth)
        {
            if (node is null) return;
            foreach (var child in node.ChildNodes)
            {
                switch (child)
                {
                    case IText text:
                        Append(text.Data);
                        break;
                    case IElement element:
                        Visit(element, depth + 1);
                        break;
                }
            }
        }

        void Visit(IElement element, int depth)
        {
            var name = element.LocalName;
            if (Skipped.Contains(name)) return;
            var pageBreak = IsPageBreak(element);
            var block = BlockElements.Contains(name);
            if (block || pageBreak) Flush(heading: false);
            // The block the element's text goes into: the one being read now, which is the next to be added.
            if (element.Id is { Length: > 0 } id) Ids.TryAdd(id, Blocks.Count);
            if (pageBreak)
            {
                Breaks.Add(new HtmlPageBreak(Blocks.Count, PageLabel(element)));
                return;
            }
            switch (name)
            {
                case "br":
                    _text.Append('\n');
                    return;
                case "td" or "th":
                    if (HasText) _text.Append(' ');
                    break;
            }

            if (name == "pre") _pre++;
            // Parse leaves no element deeper than this; the check keeps a walk of any other tree bounded too.
            if (depth >= MaxDepth) Append(TextOf(element));
            else Walk(element, depth);
            if (name == "pre") _pre--;
            if (block) Flush(heading: name is "h1" or "h2" or "h3");
        }

        bool HasText => _text.Length > 0;

        void Append(string text)
        {
            if (_pre > 0)
            {
                _text.Append(text);
                return;
            }
            // Outside <pre>, a run of white space is one space, as a browser shows it.
            var collapsed = Spaces().Replace(text, " ");
            if (collapsed.Length == 0) return;
            if (collapsed[0] == ' ' && (_text.Length == 0 || _text[^1] is ' ' or '\n')) collapsed = collapsed[1..];
            _text.Append(collapsed);
        }

        public void Flush(bool heading)
        {
            if (_text.Length == 0) return;
            var lines = _text.ToString().Split('\n').Select(l => InlineSpaces().Replace(l, " ").Trim()).Where(l => l.Length > 0);
            var text = string.Join('\n', lines);
            _text.Clear();
            if (text.Length > 0) Blocks.Add(new HtmlBlock(text, heading));
        }
    }
}
