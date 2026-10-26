using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Bibliotaph.Server.Site;

/// <summary>
/// The repository's own Markdown (the change log, the third-party notices) as HTML for the site's pages. It knows only
/// what those files use: headings, paragraphs, lists, tables, code, bold, italic and links. Everything is escaped
/// first, so nothing in a file reaches the page as markup.
/// </summary>
public static partial class SiteMarkdown
{
    /// <summary>Where a link to another file in the repository goes, since the site doesn't serve the repository.</summary>
    public const string RepositoryFiles = "https://github.com/munkycdev/bibliotaph/blob/main/";

    /// <summary>The file as HTML, without its title (the page has its own); "## x" becomes an h2 with an id.</summary>
    public static string ToHtml(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var html = new StringBuilder();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                i++;
            }
            else if (Heading().Match(line) is { Success: true } heading)
            {
                var level = heading.Groups[1].Length;
                if (level > 1)
                {
                    var text = heading.Groups[2].Value.Trim();
                    html.Append($"<h{level} id=\"{Slug(text)}\">{Inline(text)}</h{level}>\n");
                }
                i++;
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                html.Append("<ul>\n");
                while (i < lines.Length && lines[i].StartsWith("- ", StringComparison.Ordinal))
                {
                    var item = new StringBuilder(lines[i][2..].Trim());
                    // An item goes on over the indented lines below it.
                    for (i++; i < lines.Length && lines[i].StartsWith(' ') && lines[i].Trim().Length > 0; i++)
                        item.Append(' ').Append(lines[i].Trim());
                    html.Append("<li>").Append(Inline(item.ToString())).Append("</li>\n");
                }
                html.Append("</ul>\n");
            }
            else if (line.StartsWith('|'))
            {
                var rows = new List<string[]>();
                for (; i < lines.Length && lines[i].StartsWith('|'); i++)
                    if (!Separator().IsMatch(lines[i])) rows.Add(Cells(lines[i]));
                html.Append("<div class=\"bt-tablewrap\"><table class=\"bt-table\">\n<thead><tr>");
                foreach (var cell in rows[0]) html.Append("<th>").Append(Inline(cell)).Append("</th>");
                html.Append("</tr></thead>\n<tbody>\n");
                foreach (var row in rows.Skip(1))
                {
                    html.Append("<tr>");
                    foreach (var cell in row) html.Append("<td>").Append(Inline(cell)).Append("</td>");
                    html.Append("</tr>\n");
                }
                html.Append("</tbody></table></div>\n");
            }
            else
            {
                var paragraph = new StringBuilder(line.Trim());
                for (i++; i < lines.Length && lines[i].Trim().Length > 0 && !StartsBlock(lines[i]); i++)
                    paragraph.Append(' ').Append(lines[i].Trim());
                html.Append("<p>").Append(Inline(paragraph.ToString())).Append("</p>\n");
            }
        }
        return html.ToString();
    }

    static bool StartsBlock(string line) =>
        line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith('|') || Heading().IsMatch(line);

    static string[] Cells(string row) =>
        [.. row.Trim().Trim('|').Split('|').Select(cell => cell.Trim())];

    /// <summary>One line's code, links, bold and italic. Code spans are kept apart so nothing inside them is read as Markdown.</summary>
    static string Inline(string text)
    {
        var html = new StringBuilder();
        var parts = text.Split('`');
        for (var p = 0; p < parts.Length; p++)
        {
            // An odd part is inside backticks, unless the last backtick has no partner.
            if (p % 2 == 1 && p < parts.Length - 1)
                html.Append("<code>").Append(Escape(parts[p])).Append("</code>");
            else
                html.Append(Prose(p % 2 == 1 ? "`" + parts[p] : parts[p]));
        }
        return html.ToString();
    }

    static string Prose(string text)
    {
        var html = Escape(text);
        html = Link().Replace(html, m => $"<a href=\"{Href(WebUtility.HtmlDecode(m.Groups[2].Value))}\">{m.Groups[1].Value}</a>");
        html = BareUrl().Replace(html, m => $"<a href=\"{m.Value}\">{m.Value}</a>");
        html = Bold().Replace(html, "<strong>$1</strong>");
        return Italic().Replace(html, "<em>$1</em>");
    }

    /// <summary>A link's address for the site: the web and mail as they are, a file in the repository on GitHub.</summary>
    static string Href(string target)
    {
        if (target.StartsWith("https://", StringComparison.Ordinal) || target.StartsWith("http://", StringComparison.Ordinal)
            || target.StartsWith("mailto:", StringComparison.Ordinal) || target.StartsWith('#'))
            return Escape(target);
        return Escape(RepositoryFiles + target.TrimStart('.', '/'));
    }

    /// <summary>Only what HTML gives a meaning to, so the files' own characters (·, ›, é) stay as they are.</summary>
    static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>"1.3 · 19 October 2026" becomes "1-3-19-october-2026", for links to one release.</summary>
    public static string Slug(string heading)
    {
        var slug = NotWord().Replace(heading.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "section" : slug;
    }

    [GeneratedRegex(@"^(#{1,4})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\|(\s*:?-{3,}:?\s*\|)+\s*$")]
    private static partial Regex Separator();

    // Applied to encoded text, so a link's text and address are already safe; the address is decoded and re-encoded.
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex Link();

    // A bare address not already inside a link that Link() made.
    [GeneratedRegex(@"(?<![""=>])https://[^\s<)|]+[^\s<)|.,;:]")]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])")]
    private static partial Regex Italic();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotWord();
}
