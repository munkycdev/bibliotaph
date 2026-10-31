namespace Bibliotaph.Core;

/// <summary>
/// The sections of a book's details that fold away under their headings, and which are folded until the user opens
/// them. Both clients store the folded ones as <see cref="Write"/> gives them.
/// </summary>
public static class DetailSections
{
    public const string Adventures = "adventures";
    public const string InFile = "in-file";
    public const string Pack = "pack";
    public const string Found = "found";
    public const string Collections = "collections";
    public const string Details = "details";
    public const string File = "file";
    public const string Where = "where";
    public const string Copies = "copies";
    public const string Indexing = "indexing";

    /// <summary>Every section, top to bottom.</summary>
    public static IReadOnlyList<string> All { get; } = [Adventures, InFile, Pack, Found, Collections, Details, File, Where, Copies, Indexing];

    /// <summary>Folded until opened: what the file is and how it was read, which most visits don't need.</summary>
    public static IReadOnlySet<string> FoldedAtFirst { get; } = new HashSet<string>(StringComparer.Ordinal) { File, Where, Copies, Indexing };

    /// <summary>The folded sections a stored value names; nothing stored yet means <see cref="FoldedAtFirst"/>.</summary>
    public static IReadOnlySet<string> Read(string? stored) => stored is null
        ? FoldedAtFirst
        : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(All.Contains).ToHashSet(StringComparer.Ordinal);

    /// <summary>The folded sections as stored: their names, comma-separated, in <see cref="All"/>'s order.</summary>
    public static string Write(IEnumerable<string> folded)
    {
        var set = folded.ToHashSet(StringComparer.Ordinal);
        return string.Join(',', All.Where(set.Contains));
    }
}
