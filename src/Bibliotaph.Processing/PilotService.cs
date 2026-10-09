using System.Globalization;
using System.Net;
using System.Text;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>How far a pilot run has got: which model, how many of the books it has read, and how fast.</summary>
public sealed record PilotProgress(string Model, int ModelIndex, int Models, int Done, int Total, double? AverageSeconds);

/// <summary>How a pilot run ended: every model read every book, or the endpoint stopped answering.</summary>
public sealed record PilotRunEnd(bool Finished, string? Problem);

/// <summary>The pilot's state for Settings: books on the list, books answered, and books each model has read.</summary>
public sealed record PilotStatus(int Books, int Answered, IReadOnlyDictionary<string, int> ReadByModel);

/// <summary>A value offered on the review page, from a model or the catalog, without saying which.</summary>
public sealed record PilotOption(string Text, int? PdfPage, string? Quote);

/// <summary>One field on the review page: the values offered, and the answer so far.</summary>
public sealed record PilotFieldReview(MetadataField Field, IReadOnlyList<PilotOption> Options, string Answer, bool NotInBook);

/// <summary>A book on the review page.</summary>
public sealed record PilotBookReview(PilotBook Book, string Title, bool Answered, IReadOnlyList<PilotFieldReview> Fields);

/// <summary>What the user said about a field on the review page: values as typed, or that the book doesn't state it.</summary>
public sealed record PilotAnswer(string Typed, bool NotInBook);

/// <summary>
/// The model pilot (slice 2d): picks the books, runs each model over them into pilot.db with the Classify lane paused,
/// takes the user's answers on the review page (and, by choice, into the catalog), and scores the models into a report.
/// None of it runs unless the app starts with --pilot.
/// </summary>
public sealed class PilotService(
    PilotStore store, AiSettings ai, IndexingService indexing, IndexQueries queries, LibraryStore library, ClassifierInputs inputs,
    MetadataService metadata, VocabularyStore vocabularies, TimeProvider? clock = null, ILogger<PilotService>? log = null)
{
    /// <summary>The models the plan names (choice P3); Settings lets the user change the list.</summary>
    public const string DefaultModels = "qwen3:8b, llama3.1:8b, gemma3:12b";

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<PilotService>.Instance;

    public PilotStore Store => store;

    /// <summary>
    /// Picks <paramref name="count"/> books with text, spread across folders and systems, and writes the list to
    /// books.txt, where the user can swap books before a run. Books in folders kept from AI are never picked.
    /// </summary>
    public async Task<IReadOnlyList<PilotBook>> PickBooksAsync(int count = PilotSample.DefaultCount, int seed = PilotSample.DefaultSeed, CancellationToken ct = default)
    {
        var skipped = (await library.GetDocumentIdsInRootsAsync([.. ai.Setup.SkippedRoots], ct)).ToHashSet();
        var candidates = (await queries.GetPilotCandidatesAsync(ct: ct)).Where(c => !skipped.Contains(c.DocumentId)).ToList();
        var books = new List<PilotBook>();
        foreach (var id in PilotSample.Pick(candidates, candidates.Count, seed))
        {
            if (books.Count == count) break;
            if (await library.GetSourceAsync(id, ct) is { } source) books.Add(new PilotBook(id, source.FullPath, books.Count));
        }
        await SaveBookListAsync(books, ct);
        return books;
    }

    /// <summary>Makes these books the list, in this order, leaving out any that have no file left.</summary>
    public async Task<IReadOnlyList<PilotBook>> UseBooksAsync(IReadOnlyList<long> documentIds, CancellationToken ct = default)
    {
        var books = new List<PilotBook>();
        foreach (var id in documentIds.Distinct())
            if (await library.GetSourceAsync(id, ct) is { } source) books.Add(new PilotBook(id, source.FullPath, books.Count));
        await SaveBookListAsync(books, ct);
        return books;
    }

    async Task SaveBookListAsync(IReadOnlyList<PilotBook> books, CancellationToken ct)
    {
        await store.SetBooksAsync(books, ct);
        await WriteBookListAsync(books, ct);
    }

    /// <summary>The book list, read again from books.txt when it is there, so the user's edits to it count.</summary>
    public async Task<IReadOnlyList<PilotBook>> LoadBooksAsync(CancellationToken ct = default)
    {
        var known = await store.GetBooksAsync(ct);
        if (!File.Exists(store.BookListPath)) return known;
        var paths = known.ToDictionary(b => b.DocumentId, b => b.Path);
        var books = new List<PilotBook>();
        foreach (var id in ParseBookList(await File.ReadAllLinesAsync(store.BookListPath, ct)).Distinct())
        {
            var path = paths.GetValueOrDefault(id) ?? (await library.GetSourceAsync(id, ct))?.FullPath;
            if (path is not null) books.Add(new PilotBook(id, path, books.Count));
        }
        if (!books.Select(b => (b.DocumentId, b.Path)).SequenceEqual(known.Select(b => (b.DocumentId, b.Path)))) await store.SetBooksAsync(books, ct);
        return books;
    }

    /// <summary>The document ids on a books.txt list: each line starts with one; blank lines and # lines are skipped.</summary>
    public static IEnumerable<long> ParseBookList(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var end = text.IndexOfAny(['\t', ' ']);
            if (long.TryParse(end < 0 ? text : text[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) yield return id;
        }
    }

    async Task WriteBookListAsync(IReadOnlyList<PilotBook> books, CancellationToken ct)
    {
        Directory.CreateDirectory(store.Folder);
        var text = new StringBuilder()
            .AppendLine("# The books the model pilot reads, one per line: the book's number in Bibliotaph, a tab, then where it is.")
            .AppendLine("# Delete a line to leave a book out. To swap one in, add its number (the path is only a reminder).")
            .AppendLine("# Start reads this list again, and models only read the books they haven't.");
        foreach (var book in books) text.Append(book.DocumentId.ToString(CultureInfo.InvariantCulture)).Append('\t').AppendLine(book.Path);
        await File.WriteAllTextAsync(store.BookListPath, text.ToString(), ct);
    }

    public async Task<PilotStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var books = await LoadBooksAsync(ct);
        if (books.Count == 0) return new PilotStatus(0, 0, new Dictionary<string, int>());
        var onList = books.Select(b => b.DocumentId).ToHashSet();
        var runs = await store.GetRunsAsync(ct);
        var answers = await store.GetAnswersAsync(ct);
        return new PilotStatus(books.Count, answers.Keys.Count(onList.Contains),
            runs.Where(r => onList.Contains(r.DocumentId)).GroupBy(r => r.Model).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>"qwen3:8b, llama3.1:8b" as a list, in order, without repeats.</summary>
    public static IReadOnlyList<string> ParseModels(string text) =>
        [.. text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Has each model read each book on the list it hasn't read yet (choices P2 and P3), storing what it proposed in
    /// pilot.db and nothing in the catalog. The Classify lane is paused meanwhile, so the GPU serves one thing at a time.
    /// Cancelling pauses: the next run carries on where this one stopped. A book whose answer can't be used counts as
    /// that model's failure; an endpoint that stops answering ends the run with its message.
    /// </summary>
    public async Task<PilotRunEnd> RunAsync(IReadOnlyList<string> models, IProgress<PilotProgress>? progress = null, CancellationToken ct = default)
    {
        if (ai.Setup.Endpoint is not { } endpoint) return new PilotRunEnd(false, "Set up a model server in Settings > AI first.");
        if (models.Count == 0) return new PilotRunEnd(false, "Name at least one model.");
        var books = await LoadBooksAsync(ct);
        if (books.Count == 0) return new PilotRunEnd(false, "Pick the books first.");

        var pausedHere = !indexing.IsPaused(Lane.Classify);
        if (pausedHere) indexing.Pause(Lane.Classify);
        try
        {
            var client = ai.ClientFor(endpoint);
            EndpointInfo info;
            try
            {
                info = await client.ConnectAsync(ct);
            }
            catch (ModelEndpointException ex)
            {
                return new PilotRunEnd(false, ex.Message);
            }
            var done = (await store.GetRunsAsync(ct)).Select(r => (r.Model, r.DocumentId)).ToHashSet();
            for (var m = 0; m < models.Count; m++)
            {
                var model = models[m];
                var classifier = new ModelClassifier(client, info.Provider, model);
                var seconds = new List<double>();
                var count = books.Count(b => done.Contains((model, b.DocumentId)));
                progress?.Report(new PilotProgress(model, m, models.Count, count, books.Count, null));
                foreach (var book in books.Where(b => !done.Contains((model, b.DocumentId))))
                {
                    var (run, proposals, stop) = await ReadAsync(classifier, client, book.DocumentId, ct);
                    if (stop is not null) return new PilotRunEnd(false, stop);
                    await store.RecordAsync(run!, proposals, CancellationToken.None);
                    if (run!.Problem is null) seconds.Add(run.Seconds);
                    progress?.Report(new PilotProgress(model, m, models.Count, ++count, books.Count, seconds.Count == 0 ? null : seconds.Average()));
                }
                _log.LogInformation("Pilot: {Model} has read {Count} books", model, count);
            }
            return new PilotRunEnd(true, null);
        }
        finally
        {
            if (pausedHere) indexing.Resume(Lane.Classify);
        }
    }

    async Task<(PilotRun? Run, IReadOnlyList<PilotProposal> Proposals, string? Stop)> ReadAsync(
        ModelClassifier classifier, IModelClient client, long documentId, CancellationToken ct)
    {
        var model = classifier.Model;
        var (input, why) = await inputs.BuildAsync(documentId, ct);
        if (input is null) return (new PilotRun(model, documentId, 0, why), [], null);
        var started = _clock.GetTimestamp();
        try
        {
            var result = await classifier.ClassifyAsync(input, ct);
            var seconds = _clock.GetElapsedTime(started).TotalSeconds;
            List<PilotProposal> proposals =
            [
                .. result.Accepted.Select(c => new PilotProposal(model, documentId, c.Field.Key, c.Value, c.PdfPage, c.Quote, true)),
                .. result.Dropped.Select(d => new PilotProposal(model, documentId, d.Claim.Field, d.Claim.Value ?? "", d.Claim.Page, d.Claim.Quote, false, d.Reason)),
            ];
            return (new PilotRun(model, documentId, seconds, null), proposals, null);
        }
        catch (ClassifierAnswerException ex)
        {
            return (new PilotRun(model, documentId, _clock.GetElapsedTime(started).TotalSeconds, ex.Message), [], null);
        }
        catch (ModelEndpointException ex)
        {
            // A book that took too long, or that the server refused, is the model's failure; a server that's gone stops the run.
            try
            {
                await client.ConnectAsync(ct);
            }
            catch (ModelEndpointException)
            {
                return (null, [], ex.Message);
            }
            return (new PilotRun(model, documentId, _clock.GetElapsedTime(started).TotalSeconds, ex.Message), [], null);
        }
    }

    /// <summary>
    /// A book for the review page (choice P5): for each core field, the values the models kept and the catalog's own,
    /// merged and in alphabetical order so nothing says which model gave which, and the answer so far. Before the book is
    /// answered, a value the user confirmed in the catalog is the starting answer.
    /// </summary>
    public async Task<PilotBookReview> GetReviewAsync(PilotBook book, CancellationToken ct = default)
    {
        var (effective, vocabulary) = await metadata.GetAsync(book.DocumentId, ct);
        var matcher = new PilotMatcher(vocabulary);
        var proposals = (await store.GetProposalsAsync(book.DocumentId, ct)).Where(p => p.Kept).ToList();
        var answers = (await store.GetAnswersAsync(ct)).GetValueOrDefault(book.DocumentId);
        var fields = new List<PilotFieldReview>();
        foreach (var field in PilotFields.Core)
        {
            var current = effective[field];
            var offered = proposals.Where(p => p.Field == field.Key).Select(p => (p.Value, p.PdfPage, p.Quote))
                .Concat(current.Values.Concat(current.Alternatives).Select(v => (v.Value, PdfPage: (int?)null, Quote: (string?)null)));
            var options = new List<(string Value, PilotOption Option)>();
            foreach (var (value, page, quote) in offered)
                if (!options.Any(o => matcher.Same(field, o.Value, value))) options.Add((value, new PilotOption(matcher.Label(field, value), page, quote)));

            string answer;
            var notInBook = false;
            if (answers is not null)
            {
                var values = answers.GetValueOrDefault(field.Key) ?? [];
                notInBook = values.Contains(PilotStore.NotInBook);
                answer = string.Join(", ", values.Where(v => v != PilotStore.NotInBook).Select(v => matcher.Label(field, v)));
            }
            else answer = string.Join(", ", current.Values.Where(v => v.Confirmed).Select(v => matcher.Label(field, v.Value)));
            fields.Add(new PilotFieldReview(field, [.. options.Select(o => o.Option).OrderBy(o => o.Text, StringComparer.CurrentCultureIgnoreCase)], answer, notInBook));
        }
        var title = await queries.GetTitleAsync(book.DocumentId, ct) ?? Path.GetFileNameWithoutExtension(book.Path);
        return new PilotBookReview(book, title, answers is not null, fields);
    }

    /// <summary>
    /// Saves the user's answers for a book. A field left blank stays unanswered and isn't scored. With
    /// <paramref name="alsoCatalog"/>, the values also become the user's own in the catalog, as if typed in the
    /// inspector. Returns the problem instead when a value can't be stored.
    /// </summary>
    public async Task<MetadataProblem?> SaveAnswersAsync(long documentId, IReadOnlyDictionary<MetadataField, PilotAnswer> answers, bool alsoCatalog, CancellationToken ct = default)
    {
        var vocabulary = await vocabularies.GetAsync(ct);
        var stored = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (field, answer) in answers)
        {
            if (answer.NotInBook)
            {
                stored[field.Key] = [PilotStore.NotInBook];
                continue;
            }
            var values = new List<string>();
            foreach (var part in MetadataValues.Split(field, answer.Typed))
            {
                if (field.Kind == FieldKind.Term)
                {
                    if (MetadataText.Normalize(part).Length == 0) return new MetadataProblem(field, $"{field.Label} needs some letters or numbers.");
                    values.Add(vocabulary.Resolve(field.Vocabulary!, part)?.Key ?? MetadataText.Tidy(part));
                    continue;
                }
                var (value, problem) = MetadataValues.Parse(field, part);
                if (problem is not null) return new MetadataProblem(field, problem);
                values.Add(value!);
            }
            if (values.Count > 0) stored[field.Key] = values;
        }
        await store.SaveAnswersAsync(documentId, stored, ct);
        if (!alsoCatalog) return null;
        foreach (var (field, answer) in answers.Where(a => !a.Value.NotInBook && stored.ContainsKey(a.Key.Key)))
            if (await metadata.SetAsync(documentId, field, answer.Typed, ct) is { } problem) return problem;
        return null;
    }

    /// <summary>Scores every model that has read any book on the list against the answers so far.</summary>
    public async Task<IReadOnlyList<PilotModelScore>> ScoreAsync(CancellationToken ct = default)
    {
        var onList = (await LoadBooksAsync(ct)).Select(b => b.DocumentId).ToHashSet();
        var runs = (await store.GetRunsAsync(ct)).Where(r => onList.Contains(r.DocumentId)).ToList();
        var proposals = await store.GetProposalsAsync(ct: ct);
        var models = runs.Select(r => r.Model).Distinct(StringComparer.Ordinal).ToList();
        return PilotScoring.Score(models, runs, proposals, await store.GetAnswersAsync(ct), new PilotMatcher(await vocabularies.GetAsync(ct)));
    }

    /// <summary>Writes report.html in the pilot folder (choice P7) and returns where it is. It stays on this computer.</summary>
    public async Task<string> WriteReportAsync(CancellationToken ct = default)
    {
        var books = await LoadBooksAsync(ct);
        var scores = await ScoreAsync(ct);
        Directory.CreateDirectory(store.Folder);
        await File.WriteAllTextAsync(store.ReportPath, PilotReport.Html(scores, books, _clock.GetLocalNow()), ct);
        return store.ReportPath;
    }
}

/// <summary>The pilot report: a page of numbers per model, the gates, and the values each model got wrong.</summary>
public static class PilotReport
{
    public static string Html(IReadOnlyList<PilotModelScore> scores, IReadOnlyList<PilotBook> books, DateTimeOffset when)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        static string P(double? value) => value is { } v ? v.ToString("P0", CultureInfo.CurrentCulture) : "–";
        static string S(double? value) => value is { } v ? $"{v.ToString("0.0", CultureInfo.CurrentCulture)} s" : "–";
        var best = PilotScoring.Best(scores);
        var names = books.ToDictionary(b => b.DocumentId, b => Path.GetFileName(b.Path));
        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Bibliotaph model pilot</title>
            <style>
            body{font:15px/1.5 "Segoe UI",system-ui,sans-serif;margin:2rem auto;max-width:72rem;padding:0 1rem;color:#1d1b18;background:#faf8f4}
            table{border-collapse:collapse;margin:.5rem 0 1.5rem}th,td{border-bottom:1px solid #ddd6cb;padding:.3rem .8rem;text-align:left;vertical-align:top}
            th{font-weight:600}.pass{color:#256b2f;font-weight:600}.fail{color:#a3321e;font-weight:600}.note{color:#6b655c}
            </style></head><body>
            <h1>Model pilot</h1>
            """);
        html.Append(CultureInfo.CurrentCulture, $"<p class=\"note\">{E(when.ToString("f", CultureInfo.CurrentCulture))}. {books.Count} books on the list.</p>");
        html.Append(best is null
            ? "<p><b>No model passes every gate yet,</b> so AI stays something you switch on yourself.</p>"
            : $"<p><b>{E(best.Model)}</b> passes every gate and is the one to offer by default.</p>");
        html.Append(CultureInfo.InvariantCulture, $"""
            <h2>Gates</h2>
            <p class="note">Precision: of the values a model stored for title, system, type and publisher, the share that were right (at least {P(PilotModelScore.PrecisionGate)}).
            Coverage: of those fields your answers fill, the share the model filled completely and correctly (at least {P(PilotModelScore.CoverageGate)}).
            Corrections: answered books where you'd have to fix any core value, levels included (under {P(PilotModelScore.CorrectionGate)}).</p>
            <table><tr><th>Model</th><th>Books read</th><th>Failures</th><th>Median time</th><th>Answered</th><th>Precision</th><th>Coverage</th><th>Corrections</th><th>Result</th></tr>
            """);
        foreach (var s in scores)
            html.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{E(s.Model)}</td><td>{s.Books}</td><td>{s.Failures}</td><td>{S(s.MedianSeconds)}</td><td>{s.Answered}</td><td>{P(s.Precision)}</td><td>{P(s.Coverage)}</td><td>{P(s.CorrectionRate)}</td><td class=\"{(s.Passes ? "pass" : "fail")}\">{(s.Passes ? "Passes" : "Doesn't pass")}</td></tr>");
        html.Append("</table><h2>By field</h2><table><tr><th>Model</th>");
        foreach (var field in PilotFields.Core) html.Append(CultureInfo.InvariantCulture, $"<th>{E(field.Label)}</th>");
        html.Append("</tr>");
        foreach (var s in scores)
        {
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(s.Model)}</td>");
            foreach (var f in s.Fields)
                html.Append(CultureInfo.InvariantCulture, $"<td>{P(f.Precision)} right<br><span class=\"note\">{P(f.Coverage)} filled ({f.Correct}/{f.Proposed}, {f.Filled}/{f.Answerable})</span></td>");
            html.Append("</tr>");
        }
        html.Append("</table>");
        foreach (var s in scores.Where(s => s.Mistakes.Count > 0))
        {
            html.Append(CultureInfo.InvariantCulture, $"<h2>What {E(s.Model)} got wrong</h2><table><tr><th>Book</th><th>Field</th><th>It said</th><th>Your answer</th></tr>");
            foreach (var m in s.Mistakes)
                html.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{E(names.GetValueOrDefault(m.DocumentId) ?? m.DocumentId.ToString(CultureInfo.InvariantCulture))}</td><td>{E(m.Field)}</td><td>{E(m.Proposed)}</td><td>{E(m.Answer)}</td></tr>");
            html.Append("</table>");
        }
        html.Append("<p class=\"note\">This report stays on your computer. Only its numbers go into the architecture document.</p></body></html>");
        return html.ToString();
    }
}
