using System.Net;
using System.Text;

namespace Bibliotaph.Server.Site;

/// <summary>
/// The site's Guide: the repository's user guide, docs/guide, as pages at /guide/getting-started and so on, with a
/// contents page at /guide. The pages are the guide's own Markdown, so the site and GitHub never tell two stories; links
/// between chapters stay on the site, and the breadcrumb each file starts with, written for GitHub, gives way to the
/// site's list of chapters.
/// </summary>
public static class SiteGuide
{
    /// <summary>A chapter: its file in docs/guide, without .md, and a line for the contents page and search engines.</summary>
    public sealed record Chapter(string Name, string Summary);

    /// <summary>In the order the README's guide lists them, which is the order to read them in.</summary>
    public static IReadOnlyList<Chapter> Chapters { get; } =
    [
        new("getting-started", "Install the Windows app, take the tour, add your folders and find your way around."),
        new("searching", "Search words and fields, ask in plain English, ask your library a question, filters and saved searches."),
        new("reading", "The reader, notes on pages, Ask this book, windows of their own, e-books and protected books."),
        new("cataloging", "Where suggestions come from, fixing details, Needs review, copies, packs, adventures, vocabulary and your tags."),
        new("organizing", "Favorites, collections, pins, saved searches and exporting a list."),
        new("binders", "Gather books and pages in a binder, in your order, and step through them."),
        new("summary", "The Summary page: how much you have, where it came from, what it is and what you've done with it."),
        new("online-libraries", "The books you own on D&D Beyond, Demiplane, Humble Bundle and DriveThruRPG."),
        new("ai", "What the AI does and sends, setting it up on your PC or with a model server, and scanning whole books."),
        new("onedrive-and-other-pcs", "OneDrive, USB and network drives, backups, a new PC and more than one PC."),
        new("faq", "Questions and answers about safety, privacy, getting going and day-to-day use."),
        new("web", "bibliotaph.online: invitations, storage, your library in the browser, AI, connecting a Windows PC and more."),
    ];

    /// <summary>The chapter's title, from its file's own heading.</summary>
    public static string Title(string markdown)
    {
        var heading = markdown.Split('\n').FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal));
        return heading is null ? throw new InvalidOperationException("A guide chapter has no title.") : heading[2..].Trim();
    }

    /// <summary>The contents: every chapter with its line, in reading order.</summary>
    public static string Contents(IReadOnlyDictionary<string, string> titles)
    {
        var html = new StringBuilder("<ol class=\"bt-manuallist\">\n");
        foreach (var chapter in Chapters)
            html.Append($"        <li><a href=\"/guide/{chapter.Name}\"><b>{WebUtility.HtmlEncode(titles[chapter.Name])}</b>")
                .Append($"<span>{WebUtility.HtmlEncode(chapter.Summary)}</span></a></li>\n");
        return html.Append("      </ol>").ToString();
    }

    /// <summary>One chapter's page: the chapter, with the list of chapters beside it.</summary>
    public static string Page(string name, string markdown, IReadOnlyDictionary<string, string> titles)
    {
        var body = string.Join('\n', markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => !l.StartsWith("[Bibliotaph](../../README.md)", StringComparison.Ordinal)));
        var nav = new StringBuilder();
        foreach (var chapter in Chapters)
            nav.Append($"          <li><a href=\"/guide/{chapter.Name}\">{WebUtility.HtmlEncode(titles[chapter.Name])}</a></li>\n");
        // The chapter comes first, so on a narrow screen the list of chapters follows it rather than pushing it down.
        return $"""
                <div class="bt-manual">
                  <article class="bt-policy bt-manualpage">
                    <a class="bt-eyebrow" href="/guide">GUIDE</a>
                    <h1>{WebUtility.HtmlEncode(titles[name])}</h1>
            {SiteMarkdown.ToHtml(body, Link)}    </article>
                  <nav class="bt-manualnav" aria-label="Guide chapters">
                    <span class="bt-eyebrow">CHAPTERS</span>
                    <ol>
            {nav}        </ol>
                  </nav>
                </div>
            """;
    }

    /// <summary>
    /// A link in the guide, on the site: another chapter (and a heading in it) is its page here, and the README's list of
    /// chapters is the contents page. Anything else is left to the repository on GitHub.
    /// </summary>
    public static string? Link(string target)
    {
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        var (path, anchor) = hash < 0 ? (target, "") : (target[..hash], target[hash..]);
        if (path == "../../README.md" && anchor == "#the-guide")
            return "/guide";
        if (path.EndsWith(".md", StringComparison.Ordinal) && Chapters.Any(c => c.Name == path[..^3]))
            return $"/guide/{path[..^3]}{anchor}";
        if (path.Length > 0 && !path.Contains(':', StringComparison.Ordinal) && !path.StartsWith('/'))
            return new Uri(new Uri(SiteMarkdown.RepositoryFiles + "docs/guide/"), target).AbsoluteUri;
        return null;
    }
}
