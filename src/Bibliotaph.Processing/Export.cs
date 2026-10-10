using System.Text;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// RFC 4180 CSV for spreadsheets: UTF-8 with a byte order mark, so Excel reads accents and dashes right; CRLF between
/// rows; a field holding a comma, quote or line break in double quotes, with its quotes doubled. A field starting with
/// = + - @ or a tab gets a leading apostrophe, so a spreadsheet shows it as text instead of running it as a formula:
/// titles and tags can come from a downloaded PDF's own metadata.
/// </summary>
public static class Csv
{
    /// <summary>One row, without its line break.</summary>
    public static string Row(IEnumerable<string?> fields) => string.Join(',', fields.Select(Field));

    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Writes a header and rows to a new file, replacing one there.</summary>
    public static async Task WriteAsync(string path, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows, CancellationToken ct = default)
    {
        var partial = path + ".partial";
        try
        {
            await using (var writer = new StreamWriter(partial, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { NewLine = "\r\n" })
            {
                await writer.WriteLineAsync(Row(header).AsMemory(), ct);
                foreach (var row in rows) await writer.WriteLineAsync(Row(row).AsMemory(), ct);
            }
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }
}

/// <summary>
/// Export (slice 4j plan, choice 7): a CSV with one row per book for spreadsheets, from Settings > Backup or for the
/// books ticked in the Library; or JSON with everything in catalog.db, for moving the library somewhere else.
/// Passwords and AI keys are never in either.
/// </summary>
public sealed class ExportService(LibraryStore library, EntryStore entries, MetadataStore metadata, VocabularyStore vocabularies, PackStore packs,
    CatalogExport catalog, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static IReadOnlyList<string> CsvHeader { get; } = ["Title", "System", "Type", "Levels", "Publisher", "Tags", "Collections", "Path"];

    /// <summary>
    /// The CSV: a row per book the Library shows, or per one of <paramref name="entryIds"/>, by title, with its values as
    /// its card shows them (a book's file name until it has a title), its collections, and where its current copy is.
    /// A book owned elsewhere has no path. Returns how many rows it wrote.
    /// </summary>
    public async Task<int> WriteCsvAsync(string path, IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        var rows = await GetCsvRowsAsync(entryIds, ct);
        await Csv.WriteAsync(path, CsvHeader, rows, ct);
        return rows.Count;
    }

    public async Task<IReadOnlyList<IReadOnlyList<string?>>> GetCsvRowsAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        var books = await entries.GetCurrentAsync(entryIds ?? await library.GetVisibleEntryIdsAsync(ct: ct), ct);
        var ids = books.Select(b => b.EntryId).ToList();
        var vocabulary = await vocabularies.GetAsync(ct);
        var values = await metadata.GetManyAsync(ids, ct);
        var collections = await catalog.GetCollectionNamesAsync(ids, ct);
        var files = await catalog.GetFilePathsAsync([.. books.Where(b => b.Kind != EntryKind.Pack && b.DocumentId is not null).Select(b => b.DocumentId!.Value)], ct);
        var places = await packs.GetPlacesAsync([.. books.Where(b => b.Kind == EntryKind.Pack).Select(b => b.EntryId)], ct);

        var rows = new List<(string Title, IReadOnlyList<string?> Row)>();
        foreach (var book in books)
        {
            var meta = values.TryGetValue(book.EntryId, out var found) ? MetadataProjector.Build(found, vocabulary) : null;
            var path = book.Kind == EntryKind.Pack ? places.GetValueOrDefault(book.EntryId)?.FullPath
                : book.DocumentId is { } document ? files.GetValueOrDefault(document) : null;
            var title = meta?.Title ?? book.Name ?? (path is null ? "" : DisplayTitle.FromFileName(path));
            var levels = meta?.Levels switch
            {
                LevelState.NotApplicable => LevelRange.None.Describe(),
                LevelState.Known when meta.LevelMin is { } min && meta.LevelMax is { } max => new LevelRange(min, max).Describe(),
                _ => null,
            };
            rows.Add((title, [title, meta?.SystemLabel, meta?.KindLabel, levels, meta?.Publisher, meta?.Tags,
                collections.TryGetValue(book.EntryId, out var names) ? string.Join("; ", names) : null, path]));
        }
        return [.. rows.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).Select(r => r.Row)];
    }

    /// <summary>The JSON: every table of catalog.db, with a header that says how to read it (<see cref="CatalogExport"/>).</summary>
    public async Task WriteJsonAsync(string path, CancellationToken ct = default)
    {
        var partial = path + ".partial";
        try
        {
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                await catalog.WriteJsonAsync(stream, BackupService.AppVersion, _clock.GetUtcNow().UtcDateTime, ct);
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }
}
