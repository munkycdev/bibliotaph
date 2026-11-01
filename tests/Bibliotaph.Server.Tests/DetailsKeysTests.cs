using System.Text.RegularExpressions;

namespace Bibliotaph.Server.Tests;

public sealed partial class DetailsKeysTests
{
    // Blazor refuses to render siblings that share a key, even siblings of different components, and the refusal
    // takes the whole details pane down for every book.
    [Fact]
    public void Each_section_of_a_books_details_has_a_key_of_its_own()
    {
        var drawer = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Bibliotaph.Web", "Components", "BookDetailsDrawer.razor"));

        var keys = SectionKey().Matches(drawer).Select(m => (Section: m.Groups[1].Value, Key: m.Groups[2].Value)).ToList();

        Assert.True(keys.Count >= 6, $"Found only {keys.Count} keyed sections; has the drawer's markup moved?");
        var shared = keys.GroupBy(k => k.Key).Where(g => g.Count() > 1).Select(g => $"{g.Key}: {string.Join(", ", g.Select(k => k.Section))}");
        Assert.Empty(shared);
    }

    [GeneratedRegex("""<(\w+Section) @key="([^"]*)"\s""")]
    private static partial Regex SectionKey();

    static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Bibliotaph.slnx"))) return folder.FullName;
        throw new InvalidOperationException($"No Bibliotaph.slnx above {AppContext.BaseDirectory}.");
    }
}
