using System.Globalization;
using System.Text.Json;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Bibliotaph.Catalog;

/// <summary>
/// What export reads from catalog.db (slice 4j plan, choice 7): every table of the user's work for the JSON export,
/// and the collections and file paths the CSV's rows show. It writes to a stream it is given; the file is the caller's.
/// </summary>
public sealed class CatalogExport(IDbContextFactory<CatalogDbContext> contexts)
{
    /// <summary>The JSON export's own version, which goes up when its shape changes, not when a table is added.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// Columns left out of the JSON export, as "table.column": they describe one computer's disks, not the library.
    /// Every other column of every table is exported.
    /// </summary>
    public static IReadOnlySet<string> ExcludedColumns { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "source_root.volume_serial",
        "file_location.ntfs_file_id",
    };

    /// <summary>
    /// How to read the export, written into it so the file explains itself (spec §Portability: documented JSON with
    /// stable IDs, paths, hashes, provenance, collection memberships and page-reference conventions).
    /// </summary>
    static readonly (string Name, string Text)[] Conventions =
    [
        ("tables", "Each key under \"tables\" is a table of catalog.db with its rows; each row is its columns by name. Every table of the catalog is here."),
        ("ids", "Ids are stable: other rows refer to a row by its id (entry_id, document_id, page_ref_id, ...), and a backup restored keeps them."),
        ("books", "An entry is a library card. Its documents are in entry_source, one of them current; a pack's images are entries whose parent_entry_id is the pack; a book owned elsewhere has no document."),
        ("files", "A document is one file's content, identified by content_hash: the SHA-256 of the file's bytes as 64 lowercase hex characters. file_location.relative_path is under its source_root.path; a file inside a ZIP has the ZIP's path and its own name there, entry_path, and container_id points at the ZIP's own row."),
        ("metadata", "assertion holds every value claimed for a field of an entry, with its origin (User, Ai, Folder, ...), state and evidence; the value a card shows is worked out from them, the user's own and confirmed values first. rejection holds values the user said are wrong, which never come back."),
        ("pages", "PDF pages are zero-based page indexes into the file: page_ref.first_pdf_page 0 is the file's first page. first_label and last_label are the pages' printed numbers when the book has its own."),
        ("collections", "collection_item puts an entry in a collection; a collection inside another has its parent_id. Session packs are session_pack, with session_section headings and session_item rows in position order."),
        ("values", "Times are UTC in ISO 8601. Choices such as kind, state and origin are stored by name."),
        ("left out", "Passwords and AI keys are never in Bibliotaph's database, so never here. Left out as particular to one computer: " + string.Join(", ", ExcludedColumns.Order(StringComparer.Ordinal)) + "."),
    ];

    /// <summary>Every table of the catalog's model, by table name: what the JSON export holds.</summary>
    public static IReadOnlyList<IEntityType> Tables(IModel model) =>
        [.. model.GetEntityTypes().Where(t => t.GetTableName() is not null).OrderBy(t => t.GetTableName(), StringComparer.Ordinal)];

    /// <summary>
    /// Writes the whole catalog as JSON: a header saying what it is and how to read it, then every table's rows in id
    /// order. Read in one transaction, so the tables agree with each other even while the app goes on working.
    /// </summary>
    public async Task WriteJsonAsync(Stream stream, string appVersion, DateTime exportedUtc, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var schema = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = db.Database.GetDbConnection();

        await using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("format", "bibliotaph-catalog");
        json.WriteNumber("formatVersion", FormatVersion);
        json.WriteString("app", appVersion);
        json.WriteString("schema", schema);
        json.WriteString("exportedUtc", Utc(exportedUtc));
        json.WriteStartObject("conventions");
        foreach (var (name, text) in Conventions) json.WriteString(name, text);
        json.WriteEndObject();

        json.WriteStartObject("tables");
        foreach (var table in Tables(db.Model))
        {
            var name = table.GetTableName()!;
            var columns = table.GetProperties()
                .Select(p => (Property: p, Column: p.GetColumnName()))
                .Where(c => !ExcludedColumns.Contains($"{name}.{c.Column}"))
                .ToList();
            var key = table.FindPrimaryKey()!.Properties.Select(p => Quote(p.GetColumnName()));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = $"SELECT {string.Join(", ", columns.Select(c => Quote(c.Column)))} FROM {Quote(name)} ORDER BY {string.Join(", ", key)}";

            json.WriteStartArray(name);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                json.WriteStartObject();
                for (var i = 0; i < columns.Count; i++)
                {
                    json.WritePropertyName(columns[i].Column);
                    WriteValue(json, reader.GetValue(i), columns[i].Property.ClrType);
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            // A big library's page refs and assertions run to many megabytes; don't hold them all in the writer.
            await json.FlushAsync(ct);
        }
        json.WriteEndObject();
        json.WriteEndObject();
        await json.FlushAsync(ct);
    }

    static void WriteValue(Utf8JsonWriter json, object value, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        switch (value)
        {
            case DBNull:
                json.WriteNullValue();
                break;
            case long number when type == typeof(bool):
                json.WriteBooleanValue(number != 0);
                break;
            case long number:
                json.WriteNumberValue(number);
                break;
            case double number:
                json.WriteNumberValue(number);
                break;
            case string text when type == typeof(DateTime):
                json.WriteStringValue(Utc(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)));
                break;
            case string text:
                json.WriteStringValue(text);
                break;
            case byte[] bytes:
                json.WriteBase64StringValue(bytes);
                break;
            default:
                json.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    static string Utc(DateTime time) =>
        DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Where each document's file is, for the CSV: a file that is there first, then one online-only, then one inside a
    /// ZIP (as File Explorer shows it), then the last place a missing one was. Folders the user removed don't count.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, string>> GetFilePathsAsync(IReadOnlyCollection<long> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return new Dictionary<long, string>();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = documentIds.Distinct().ToList();
        var rows = await db.FileLocations.AsNoTracking()
            .Where(f => f.DocumentId != null && ids.Contains(f.DocumentId.Value) && f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser)
            .Select(f => new { DocumentId = f.DocumentId!.Value, Root = f.SourceRoot.Path, f.RelativePath, f.State, InArchive = f.ContainerId != null, f.Id })
            .ToListAsync(ct);
        return rows
            .OrderBy(r => r.State == FileLocationState.Missing).ThenBy(r => r.InArchive).ThenBy(r => r.State == FileLocationState.OnlineOnly).ThenBy(r => r.Id)
            .GroupBy(r => r.DocumentId)
            .ToDictionary(g => g.Key, g => Path.Combine(g.First().Root, g.First().RelativePath));
    }

    /// <summary>The names of the collections each entry was added to, in name order.</summary>
    public async Task<IReadOnlyDictionary<EntryId, IReadOnlyList<string>>> GetCollectionNamesAsync(IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        if (entryIds.Count == 0) return new Dictionary<EntryId, IReadOnlyList<string>>();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = entryIds.Select(e => e.Value).Distinct().ToList();
        var rows = await (from item in db.CollectionItems
                          join collection in db.Collections on item.CollectionId equals collection.Id
                          where ids.Contains(item.EntryId)
                          select new { item.EntryId, collection.Name }).ToListAsync(ct);
        return rows.GroupBy(r => new EntryId(r.EntryId))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => r.Name).Order(StringComparer.CurrentCultureIgnoreCase)]);
    }
}
