using System.Reflection;

namespace Bibliotaph.Web.Services;

/// <summary>
/// What the footer under the app and the site's pages says: a tagline, the version running, and the site's pages.
/// The server's pages use it too, so the app and the site can't disagree.
/// </summary>
public static class Footer
{
    /// <summary>One is picked each time a page loads, for a little fun at the bottom of the page.</summary>
    public static IReadOnlyList<string> Taglines { get; } =
    [
        "Every book you own, and the page you were looking for.",
        "A bibliotaph hides books away. This one finds them again.",
        "Rolled a natural 20 on finding that one table.",
        "Your hoard, catalogued. The dragon would approve.",
        "No more “I know it's in one of these PDFs.”",
        "The rule you need is on page 147. We checked.",
    ];

    /// <summary>The site's own pages, in the order the site's footer lists them.</summary>
    public static IReadOnlyList<(string Href, string Label)> Links { get; } =
    [
        ("/about", "About"),
        ("/features", "Features"),
        ("/guide", "Guide"),
        ("/changelog", "Change log"),
        ("/contact", "Contact us"),
        ("/terms", "Terms"),
        ("/privacy", "Privacy"),
        ("/licences", "Licences"),
        ("https://github.com/munkycdev/bibliotaph", "Source code"),
    ];

    public static string Tagline() => Taglines[Random.Shared.Next(Taglines.Count)];

    /// <summary>"1.3.0 (4578ab9)": the build's version, and its commit when it was built from git.</summary>
    public static string Version { get; } =
        VersionOf(typeof(Footer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    static string VersionOf(string informational)
    {
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0) return informational;
        var commit = informational[(plus + 1)..];
        return commit.Length == 0 ? informational[..plus] : $"{informational[..plus]} ({commit[..Math.Min(7, commit.Length)]})";
    }
}
