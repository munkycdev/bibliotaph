using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Processing;

/// <summary>A book in the pilot, in its place on the list.</summary>
public sealed record PilotBook(long DocumentId, string Path, int Position);

/// <summary>One model's read of one book: how long it took, and why it failed if it did.</summary>
public sealed record PilotRun(string Model, long DocumentId, double Seconds, string? Problem);

/// <summary>
/// A value a model proposed. <see cref="Kept"/> values passed the evidence check, so the Classify stage would have
/// stored them as suggestions; the rest carry the check's <see cref="Reason"/>.
/// </summary>
public sealed record PilotProposal(string Model, long DocumentId, string Field, string Value, int? PdfPage, string? Quote, bool Kept, string? Reason = null);

/// <summary>
/// The pilot's own database, pilot.db in the pilot folder (slice 2d, choice P4): the book list, every model's
/// proposals and the user's answers. Nothing here reaches catalog.db unless the user's answers are saved there too.
/// </summary>
public sealed class PilotStore
{
    /// <summary>The value an answer stores for a field the user said the book doesn't state.</summary>
    public const string NotInBook = "";

    const string Schema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS book (
            document_id INTEGER PRIMARY KEY,
            path        TEXT    NOT NULL,
            position    INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS run (
            model        TEXT    NOT NULL,
            document_id  INTEGER NOT NULL,
            seconds      REAL    NOT NULL,
            problem      TEXT,
            finished_utc TEXT    NOT NULL,
            PRIMARY KEY (model, document_id)
        );
        CREATE TABLE IF NOT EXISTS proposal (
            model        TEXT    NOT NULL,
            document_id  INTEGER NOT NULL,
            field        TEXT    NOT NULL,
            value        TEXT    NOT NULL,
            pdf_page     INTEGER,
            quote        TEXT,
            kept         INTEGER NOT NULL,
            reason       TEXT
        );
        CREATE INDEX IF NOT EXISTS proposal_book ON proposal (document_id, model);
        CREATE TABLE IF NOT EXISTS answer (
            document_id  INTEGER NOT NULL,
            field        TEXT    NOT NULL,
            value        TEXT    NOT NULL,
            PRIMARY KEY (document_id, field, value)
        );
        CREATE TABLE IF NOT EXISTS answered (
            document_id  INTEGER PRIMARY KEY,
            answered_utc TEXT    NOT NULL
        );
        """;

    readonly string _connectionString;
    readonly TimeProvider _clock;
    bool _created;

    public PilotStore(string folder, TimeProvider? clock = null)
    {
        Folder = folder;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "pilot.db"), Pooling = false }.ToString();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Where pilot.db, books.txt and the report live.</summary>
    public string Folder { get; }

    public string BookListPath => Path.Combine(Folder, "books.txt");

    public string ReportPath => Path.Combine(Folder, "report.html");

    async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        if (!_created)
        {
            await connection.ExecuteAsync(new CommandDefinition(Schema, cancellationToken: ct));
            _created = true;
        }
        return connection;
    }

    /// <summary>Replaces the book list, keeping what models and the user already said about books still on it.</summary>
    public async Task SetBooksAsync(IReadOnlyList<PilotBook> books, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM book", transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO book (document_id, path, position) VALUES (@DocumentId, @Path, @Position)",
            books, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<PilotBook>> GetBooksAsync(CancellationToken ct = default)
    {
        if (!File.Exists(Path.Combine(Folder, "pilot.db"))) return [];
        await using var connection = await OpenAsync(ct);
        return [.. (await connection.QueryAsync<BookRow>(new CommandDefinition(
            "SELECT document_id AS DocumentId, path AS Path, position AS Position FROM book ORDER BY position", cancellationToken: ct)))
            .Select(r => new PilotBook(r.DocumentId, r.Path, r.Position))];
    }

    /// <summary>Records a model's read of a book with what it proposed, replacing an earlier read by the same model.</summary>
    public async Task RecordAsync(PilotRun run, IReadOnlyList<PilotProposal> proposals, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var key = new { run.Model, run.DocumentId };
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM proposal WHERE model = @Model AND document_id = @DocumentId", key, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT OR REPLACE INTO run (model, document_id, seconds, problem, finished_utc)
            VALUES (@Model, @DocumentId, @Seconds, @Problem, @finished)
            """,
            new { run.Model, run.DocumentId, run.Seconds, run.Problem, finished = _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture) },
            transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO proposal (model, document_id, field, value, pdf_page, quote, kept, reason)
            VALUES (@Model, @DocumentId, @Field, @Value, @PdfPage, @Quote, @Kept, @Reason)
            """,
            proposals, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<PilotRun>> GetRunsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return [.. (await connection.QueryAsync<RunRow>(new CommandDefinition(
            "SELECT model AS Model, document_id AS DocumentId, seconds AS Seconds, problem AS Problem FROM run", cancellationToken: ct)))
            .Select(r => new PilotRun(r.Model, r.DocumentId, r.Seconds, r.Problem))];
    }

    /// <summary>Every proposal, or those for one book.</summary>
    public async Task<IReadOnlyList<PilotProposal>> GetProposalsAsync(long? documentId = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return [.. (await connection.QueryAsync<ProposalRow>(new CommandDefinition(
            $"""
            SELECT model AS Model, document_id AS DocumentId, field AS Field, value AS Value, pdf_page AS PdfPage, quote AS Quote,
                   kept AS Kept, reason AS Reason
            FROM proposal {(documentId is null ? "" : "WHERE document_id = @documentId")}
            ORDER BY rowid
            """,
            new { documentId }, cancellationToken: ct)))
            .Select(r => new PilotProposal(r.Model, r.DocumentId, r.Field, r.Value, r.PdfPage, r.Quote, r.Kept, r.Reason))];
    }

    /// <summary>
    /// Saves the user's answers for a book: for each field, its values, or <see cref="NotInBook"/> alone when the book
    /// doesn't state it. Fields left out stay unanswered.
    /// </summary>
    public async Task SaveAnswersAsync(long documentId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM answer WHERE document_id = @documentId", new { documentId }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("INSERT OR IGNORE INTO answer (document_id, field, value) VALUES (@documentId, @Field, @Value)",
            answers.SelectMany(a => a.Value.Select(v => new { documentId, Field = a.Key, Value = v })), transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("INSERT OR REPLACE INTO answered (document_id, answered_utc) VALUES (@documentId, @now)",
            new { documentId, now = _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture) }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>The answers for the books the user has answered: document, then field, then values.</summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyList<string>>>> GetAnswersAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var answered = await connection.QueryAsync<long>(new CommandDefinition("SELECT document_id FROM answered", cancellationToken: ct));
        var rows = (await connection.QueryAsync<(long DocumentId, string Field, string Value)>(new CommandDefinition(
            "SELECT document_id, field, value FROM answer ORDER BY rowid", cancellationToken: ct))).ToList();
        return answered.ToDictionary(id => id, id => (IReadOnlyDictionary<string, IReadOnlyList<string>>)rows
            .Where(r => r.DocumentId == id)
            .GroupBy(r => r.Field)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => r.Value)]));
    }

    // Dapper maps columns onto settable properties with conversions (INTEGER to int and bool); records need exact types.
    sealed class BookRow
    {
        public long DocumentId { get; init; }
        public string Path { get; init; } = "";
        public int Position { get; init; }
    }

    sealed class RunRow
    {
        public string Model { get; init; } = "";
        public long DocumentId { get; init; }
        public double Seconds { get; init; }
        public string? Problem { get; init; }
    }

    sealed class ProposalRow
    {
        public string Model { get; init; } = "";
        public long DocumentId { get; init; }
        public string Field { get; init; } = "";
        public string Value { get; init; } = "";
        public int? PdfPage { get; init; }
        public string? Quote { get; init; }
        public bool Kept { get; init; }
        public string? Reason { get; init; }
    }
}
