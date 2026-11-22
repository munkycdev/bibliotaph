
namespace Bibliotaph.Presentation.Services;

/// <summary>The e-book reader page's own stylesheet and script, built into the app and put in each book's page.</summary>
public static class EbookReaderPage
{
    static readonly Lazy<string> s_stylesheet = new(() => Read("reader.css"));
    static readonly Lazy<string> s_script = new(() => Read("reader.js"));

    public static string Stylesheet => s_stylesheet.Value;

    /// <summary>The only script on the page.</summary>
    public static string Script => s_script.Value;

    static string Read(string name)
    {
        using var stream = typeof(EbookReaderPage).Assembly.GetManifestResourceStream("Bibliotaph.Presentation.Ebook." + name)
            ?? throw new InvalidOperationException($"The e-book reader's {name} isn't built into the app.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
