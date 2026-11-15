using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core;

/// <summary>
/// The sections of a book's details, top to bottom: what a game master asks first (system, levels, what's in it and what
/// goes with it), then where it came from, then the file and how it was read. Each folds away under its heading; both
/// clients store the folded ones as <see cref="Write"/> gives them, and group a book's fields as <see cref="FieldSection"/> says.
/// </summary>
public static class DetailSections
{
    public const string Glance = "glance";
    public const string Adventures = "adventures";
    public const string InFile = "in-file";
    public const string GoesWith = "goes-with";
    public const string Pack = "pack";
    public const string Inside = "inside";
    public const string Collections = "collections";
    public const string Publication = "publication";
    public const string File = "file";
    public const string Copies = "copies";
    public const string Read = "read";

    // The headings of the sections whose heading never changes with the book, the same in both clients.
    public const string GlanceHeading = "AT A GLANCE";
    public const string InsideHeading = "WHAT'S INSIDE";
    public const string PublicationHeading = "PUBLICATION";
    public const string ReadHeading = "HOW IT WAS READ";

    /// <summary>Over the chapters under What's inside, which describe parts of the book while the fields above describe it whole.</summary>
    public const string ChaptersNote = "By chapter, found by the AI. These describe parts of the book and don't change its details.";

    /// <summary>Every section, top to bottom.</summary>
    public static IReadOnlyList<string> All { get; } = [Glance, Adventures, InFile, GoesWith, Pack, Inside, Collections, Publication, File, Copies, Read];

    /// <summary>Folded until opened: where the book came from, its file and how it was read, which most visits don't need.</summary>
    public static IReadOnlyCollection<string> FoldedAtFirst { get; } = new HashSet<string>(StringComparer.Ordinal) { Publication, File, Copies, Read };

    /// <summary>
    /// Marks a stored value written since the sections were regrouped, so one written before is read through
    /// <see cref="Earlier"/> rather than taken to mean the new sections are open.
    /// </summary>
    const string Version = "v2";

    /// <summary>The folded sections a stored value names; nothing stored yet means <see cref="FoldedAtFirst"/>.</summary>
    public static IReadOnlySet<string> ReadFolded(string? stored)
    {
        if (stored is null) return FoldedAtFirst.ToHashSet(StringComparer.Ordinal);
        var names = stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var folded = names.Contains(Version, StringComparer.Ordinal) ? names : Earlier(names);
        return folded.Where(All.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// What was folded before the sections were regrouped, in today's sections. Found inside became What's inside,
    /// Details At a glance, Indexing How it was read; About the file and Where it is became one, open if either was;
    /// Publication is new, so folded as it is at first.
    /// </summary>
    static IEnumerable<string> Earlier(IReadOnlyCollection<string> names)
    {
        foreach (var name in names)
        {
            switch (name)
            {
                case "found": yield return Inside; break;
                case "details": yield return Glance; break;
                case "indexing": yield return Read; break;
                case "file" or "where": break;
                default: yield return name; break;
            }
        }
        if (names.Contains("file") && names.Contains("where")) yield return File;
        yield return Publication;
    }

    /// <summary>The folded sections as stored: their names, comma-separated, in <see cref="All"/>'s order.</summary>
    public static string Write(IEnumerable<string> folded)
    {
        var set = folded.ToHashSet(StringComparer.Ordinal);
        return string.Join(',', All.Where(set.Contains).Prepend(Version));
    }

    static readonly MetadataField[] GlanceFields =
    [
        MetadataFields.System, MetadataFields.Edition, MetadataFields.Types, MetadataFields.Levels, MetadataFields.Players, MetadataFields.Settings,
        MetadataFields.Includes, MetadataFields.Duration, MetadataFields.Tags,
    ];

    static readonly MetadataField[] InsideFields =
        [MetadataFields.Themes, MetadataFields.Styles, MetadataFields.Environments, MetadataFields.Monsters, MetadataFields.Items];

    static readonly MetadataField[] PublicationFields =
    [
        MetadataFields.Title, MetadataFields.Publisher, MetadataFields.Authors, MetadataFields.Artists, MetadataFields.Series, MetadataFields.Year,
        MetadataFields.Mechanics, MetadataFields.AlsoOwn, MetadataFields.GetItFrom,
    ];

    /// <summary>
    /// The section a field shows in: <see cref="Glance"/>, <see cref="Inside"/> or <see cref="Publication"/>. A book
    /// owned elsewhere says first where it is owned, so its Also own is at a glance.
    /// </summary>
    public static string FieldSection(MetadataField field, bool elsewhere) =>
        elsewhere && field == MetadataFields.AlsoOwn || GlanceFields.Contains(field) ? Glance
        : InsideFields.Contains(field) ? Inside
        : Publication;

    /// <summary>Every field, in the order the details show them.</summary>
    public static IReadOnlyList<MetadataField> FieldOrder(bool elsewhere) => elsewhere
        ? [MetadataFields.AlsoOwn, .. GlanceFields, .. InsideFields, .. PublicationFields.Where(f => f != MetadataFields.AlsoOwn)]
        : [.. GlanceFields, .. InsideFields, .. PublicationFields];

    /// <summary>A book's fields, put in the order the details show them.</summary>
    public static IEnumerable<T> InFieldOrder<T>(IEnumerable<T> fields, Func<T, MetadataField> field, bool elsewhere)
    {
        var order = FieldOrder(elsewhere).Select((f, at) => (f, at)).ToDictionary(o => o.f, o => o.at);
        return fields.OrderBy(f => order.GetValueOrDefault(field(f), int.MaxValue));
    }

    /// <summary>How many chapters What's inside shows before Show all.</summary>
    public const int ChaptersAtFirst = 3;

    /// <summary>
    /// The chapters What's inside shows before Show all: the first that the AI found something in, or the first of
    /// them all while none has anything yet, so a book being read still says so.
    /// </summary>
    public static IReadOnlyList<T> FirstChapters<T>(IReadOnlyList<T> chapters, Func<T, bool> found)
    {
        var some = chapters.Where(found).Take(ChaptersAtFirst).ToList();
        return some.Count > 0 ? some : [.. chapters.Take(ChaptersAtFirst)];
    }

    /// <summary>"Show all 12 chapters".</summary>
    public static string ShowAllChapters(int count) => $"Show all {count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} chapters";
}
