using System.Collections.Frozen;
using System.Net;
using System.Text.RegularExpressions;
using Bibliotaph.Web.Icons;
using Bibliotaph.Web.Services;

namespace Bibliotaph.Server.Site;

/// <summary>
/// The site's own pages, around the app: the landing page someone signed out sees at /, Features, About, the change
/// log, Terms, Privacy, Licences and Contact us. They are plain HTML in one layout, read from the server's resources
/// once, so they load without the WebAssembly client and work for anyone. The change log and licences come from the
/// repository's own CHANGELOG.md, THIRD-PARTY-NOTICES.md and LICENSE-EXCEPTION.md, so they can't drift from them.
/// </summary>
public static partial class SitePages
{
    /// <summary>A page: its address, resource, title for the tab, and description for search engines and link previews.</summary>
    sealed record Page(string Path, string Resource, string Title, string Description);

    static readonly Page[] Pages =
    [
        new("/", "home", "Bibliotaph: your RPG library, searchable",
            "Bibliotaph catalogs the RPG books, maps and handouts in your own folders and cloud storage, and finds the right book and the right page."),
        new("/features", "features", "Features · Bibliotaph", "Everything Bibliotaph does: search inside every book, a catalog that fills itself in, binders for the table, and more."),
        new("/about", "about", "About · Bibliotaph", "Why Bibliotaph exists, who makes it, and what it's built on."),
        new("/changelog", "changelog", "Change log · Bibliotaph", "What changed in each version of Bibliotaph."),
        new("/terms", "terms", "Terms of service · Bibliotaph", "The terms for using the Bibliotaph web service."),
        new("/privacy", "privacy", "Privacy · Bibliotaph", "What the Bibliotaph web service keeps about you and your library, why, and how to have it deleted."),
        new("/licences", "licences", "Licences · Bibliotaph", "Bibliotaph's licence, the GNU GPL version 3 or later, and the licences of the components it is built with."),
        new("/contact", "contact", "Contact us · Bibliotaph", "How to report a problem, suggest something or reach the people who run Bibliotaph."),
    ];

    /// <summary>The screenshots on the landing page, from the repository's docs.</summary>
    static readonly FrozenDictionary<string, string> Pictures = new Dictionary<string, string>
    {
        ["/site/library.png"] = "library.png",
        ["/site/reader.png"] = "reader.png",
    }.ToFrozenDictionary();

    const string SignIn = """<a class="bt-button primary" href="/signin">Sign in</a>""";
    const string OpenLibrary = """<a class="bt-button primary" href="/">Open your library</a>""";

    static readonly Lazy<FrozenDictionary<string, (string SignedOut, string SignedIn)>> Rendered = new(Render);

    public static WebApplication MapSite(this WebApplication app)
    {
        // Someone signed in gets the app at /, from the fallback below; anyone else gets the landing page.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/" && HttpMethods.IsGet(context.Request.Method) && context.User.Identity?.IsAuthenticated != true)
            {
                await WriteAsync(context, "/");
                return;
            }
            await next(context);
        });

        foreach (var page in Pages.Where(p => p.Path != "/"))
            app.MapGet(page.Path, (HttpContext context) => WriteAsync(context, page.Path));

        // Addresses given out before the pages had their own, and the other spelling.
        app.MapGet("/privacy.html", () => Results.Redirect("/privacy", permanent: true));
        app.MapGet("/licenses", () => Results.Redirect("/licences", permanent: true));

        foreach (var (path, name) in Pictures)
            app.MapGet(path, (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "public, max-age=86400";
                return Results.Stream(Resource($"Site.{name}"), "image/png");
            });
        return app;
    }

    static Task WriteAsync(HttpContext context, string path)
    {
        var (signedOut, signedIn) = Rendered.Value[path];
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        var html = context.User.Identity?.IsAuthenticated == true ? signedIn : signedOut;
        return context.Response.WriteAsync(html.Replace("{{tagline}}", WebUtility.HtmlEncode(Footer.Tagline()), StringComparison.Ordinal), context.RequestAborted);
    }

    static FrozenDictionary<string, (string, string)> Render()
    {
        var links = string.Join('\n', Footer.Links.Select(l => $"      <a href=\"{l.Href}\">{WebUtility.HtmlEncode(l.Label)}</a>"));
        var layout = Text("Site.layout.html")
            .Replace("{{links}}", links, StringComparison.Ordinal)
            .Replace("{{version}}", WebUtility.HtmlEncode(Footer.Version), StringComparison.Ordinal);
        var pages = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var page in Pages)
        {
            var content = Text($"Site.{page.Resource}.html")
                .Replace("{{changelog}}", SiteMarkdown.ToHtml(Text("CHANGELOG.md")), StringComparison.Ordinal)
                .Replace("{{notices}}", SiteMarkdown.ToHtml(Text("THIRD-PARTY-NOTICES.md")), StringComparison.Ordinal)
                .Replace("{{exception}}", SiteMarkdown.ToHtml(Text("LICENSE-EXCEPTION.md")), StringComparison.Ordinal);
            content = IconPlaceholder().Replace(content, m => Icon(m.Groups["name"].Value));
            // The page's own link in the header and footer says so, for screen readers and the underline.
            var html = layout
                .Replace("{{title}}", WebUtility.HtmlEncode(page.Title), StringComparison.Ordinal)
                .Replace("{{description}}", WebUtility.HtmlEncode(page.Description), StringComparison.Ordinal)
                .Replace("{{content}}", content, StringComparison.Ordinal)
                .Replace($"<a href=\"{page.Path}\">", $"<a href=\"{page.Path}\" aria-current=\"page\">", StringComparison.Ordinal);
            pages[page.Path] = (html.Replace("{{account}}", SignIn, StringComparison.Ordinal),
                html.Replace("{{account}}", OpenLibrary, StringComparison.Ordinal));
        }
        return pages.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// {{icon:Search}} in a page: the app's Lucide icon drawn inline, since the pages load without the app's script and
    /// the site's policy allows no inline styles. A name the app doesn't have stops the server at its first page.
    /// </summary>
    static string Icon(string name) => LucideIcons.Paths.TryGetValue(name, out var path)
        ? $"""<svg class="bt-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="{path}"/></svg>"""
        : throw new InvalidOperationException($"The site names an icon the app doesn't have: {name}.");

    [GeneratedRegex(@"\{\{icon:(?<name>[A-Za-z0-9]+)\}\}")]
    private static partial Regex IconPlaceholder();

    static string Text(string name)
    {
        using var reader = new StreamReader(Resource(name));
        return reader.ReadToEnd();
    }

    static Stream Resource(string name) =>
        typeof(SitePages).Assembly.GetManifestResourceStream($"Bibliotaph.Server.{name}")
        ?? throw new InvalidOperationException($"The server was built without its {name}.");
}
