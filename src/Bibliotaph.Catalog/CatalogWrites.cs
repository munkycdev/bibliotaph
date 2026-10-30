using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bibliotaph.Catalog;

/// <summary>
/// Notices writes to the tables that hold the user's work, whichever store or page made them, so library sync can write
/// this PC's sync file a few seconds later. It looks at the SQL EF Core runs against catalog.db: an INSERT, UPDATE or
/// DELETE on one of <see cref="Tables"/> raises <see cref="Written"/>. Writes made inside <see cref="Quiet"/> don't:
/// library sync's own merging doesn't need another look.
/// </summary>
public sealed partial class CatalogWrites : DbCommandInterceptor
{
    /// <summary>The tables library sync reads: metadata decisions, marks, notes and everything the user makes, and which files make up each card.</summary>
    public static IReadOnlySet<string> Tables { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "assertion", "rejection", "favorite", "note", "page_ref", "collection", "collection_item", "smart_view", "pin", "session_pack",
        "session_section", "session_item", "entry", "entry_source", "copy_decision", "vocabulary_term", "vocabulary_alias",
        "lookup_record",
    };

    static readonly AsyncLocal<bool> Quieted = new();

    /// <summary>Raised after a write to the user's work, on the thread that wrote it.</summary>
    public event EventHandler? Written;

    /// <summary>Until disposed, writes on this async flow raise nothing.</summary>
    public static IDisposable Quiet()
    {
        var before = Quieted.Value;
        Quieted.Value = true;
        return new Restore(before);
    }

    sealed class Restore(bool before) : IDisposable
    {
        public void Dispose() => Quieted.Value = before;
    }

    /// <summary>Whether <paramref name="sql"/> writes one of <see cref="Tables"/>.</summary>
    public static bool WritesUserWork(string sql)
    {
        var start = sql.AsSpan().TrimStart();
        if (start.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || start.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (Match match in WriteStatement().Matches(sql))
            if (Tables.Contains(match.Groups["table"].Value)) return true;
        return false;
    }

    [GeneratedRegex(@"(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+""(?<table>\w+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteStatement();

    void Check(DbCommand command)
    {
        if (!Quieted.Value && Written is { } written && WritesUserWork(command.CommandText)) written(this, EventArgs.Empty);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }
}
