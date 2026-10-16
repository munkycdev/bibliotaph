using System.IO;
using Bibliotaph.Core;
using Microsoft.Web.WebView2.Core;

namespace Bibliotaph.App.Services;

/// <summary>
/// The e-book reader's browser: WebView2 with a profile of its own under <see cref="AppPaths.EbookReader"/>, which
/// shows a book's page at <see cref="Host"/>. That address is never looked up: every request the page makes is
/// answered from the book (<see cref="EbookContent"/>) or refused, so a book never reaches the network.
/// </summary>
public sealed class EbookBrowser(AppPaths paths)
{
    /// <summary>The made-up address the reader's page is at; <c>.invalid</c> names no site anywhere.</summary>
    public const string Host = "book.bibliotaph.invalid";

    /// <summary>The page itself, at the root of <see cref="Host"/>.</summary>
    public const string PagePath = "/book.html";

    static readonly Lazy<string> s_stylesheet = new(() => Read("reader.css"));
    static readonly Lazy<string> s_script = new(() => Read("reader.js"));
    Task<CoreWebView2Environment>? _environment;

    /// <summary>The reader page's stylesheet, from the app.</summary>
    public static string Stylesheet => s_stylesheet.Value;

    /// <summary>The reader page's script, from the app: the only script on the page.</summary>
    public static string Script => s_script.Value;

    /// <summary>One browser for every reader, in the main window and the pop-outs.</summary>
    public async Task<CoreWebView2Environment> EnvironmentAsync()
    {
        _environment ??= CoreWebView2Environment.CreateAsync(null, paths.EbookReader);
        try
        {
            return await _environment;
        }
        catch
        {
            _environment = null;
            throw;
        }
    }

    static string Read(string name)
    {
        using var stream = typeof(EbookBrowser).Assembly.GetManifestResourceStream("Bibliotaph.App.Ebook." + name)
            ?? throw new InvalidOperationException($"The e-book reader's {name} isn't built into the app.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
