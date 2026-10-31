using System.Security.Claims;
using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/ask: Ask, as the Windows app asks. /api/ask/{documentId} is Ask this book, the reader's Ask panel;
/// /api/ask/search and /api/ask/library are Ask in the top bar, which reads a request as a search or answers a question
/// from the books the Library shows. A question is answered in one request whose body is a stream of
/// <see cref="AskUpdate"/> lines, one per change, so the answer shows as the model writes it; closing the request stops
/// the model.
/// </summary>
public static class AskApi
{
    /// <summary>How often the answer being written is sent on: often enough to read as it comes, without a line per word.</summary>
    static readonly TimeSpan StreamEvery = TimeSpan.FromMilliseconds(150);

    public static void MapAskApi(this RouteGroupBuilder api)
    {
        api.MapGet("/ask", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return new AskStatus(lease.Library.Get<PlainSearchService>().IsReady);
        });

        // Holds the request while the model reads, as the Windows panel waits; closing the panel cancels it.
        api.MapPost("/ask/search", async (AskQuestion asked, ClaimsPrincipal principal, UserLibraries libraries, ILogger<PlainSearchService> log,
            CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var search = lease.Library.Get<PlainSearchService>();
            try
            {
                var result = await search.InterpretAsync(asked.Question, ct);
                return result.Interpretation is { } reading
                    ? new AskReading([.. reading.Parts.Select(p => new AskChip(p.Field?.Name, p.Value, p.Exclude))], reading.Notes, null)
                    : new AskReading([], [], result.Problem);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                log.LogError(ex, "Ask failed");
                return new AskReading([], [], AskPanelText.Failed);
            }
        });

        api.MapPost("/ask/library", async (AskLibraryQuestion asked, HttpContext http, ClaimsPrincipal principal, UserLibraries libraries,
            ILogger<LibraryAskService> log) =>
        {
            var ct = http.RequestAborted;
            var question = asked.Question.Trim();
            if (question.Length == 0) return Results.BadRequest();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var scope = await library.Get<LibraryStore>().GetVisibleEntryIdsAsync(asked.Scope.Folder, ct);
            var plan = SearchPlan.From(SearchQuery.Parse(asked.Scope.Q?.Trim() ?? ""));
            AskFilter filter = new BookFilter(LibraryApi.Filter(asked.Scope, scope), plan);
            http.Response.ContentType = "application/x-ndjson";
            http.Response.Headers.CacheControl = "no-store";
            await new AnsweringLibrary(library, question, [filter], http.Response.Body, log, ct).RunAsync();
            return Results.Empty;
        });

        var ask = api.MapGroup("/ask/{documentId:long}");

        // Opening a book in the reader starts its pages being read for meaning, as the Windows reader does.
        ask.MapGet("", async (long documentId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await library.Get<LibraryStore>().GetSourceAsync(documentId, ct) is null) return Results.NotFound();
            var service = library.Get<AskService>();
            var why = await service.WhyNotAsync(documentId, ct);
            if (why is null) await service.PrepareAsync(documentId, ct);
            var history = await service.HistoryAsync(documentId, ct);
            return Results.Ok(new AskBook(service.IsOn, why, [.. history.Select(t =>
                new AskTurn(t.Info.Question, t.Info.Rewritten is { } rewritten ? AskText.LookedFor(rewritten) : null, Parts(t.Parts)))]));
        });

        ask.MapDelete("", async (long documentId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<AskService>().ClearHistoryAsync(documentId, ct);
            return Results.NoContent();
        });

        ask.MapPost("", async (long documentId, AskQuestion asked, HttpContext http, ClaimsPrincipal principal, UserLibraries libraries,
            ILogger<AskService> log) =>
        {
            var ct = http.RequestAborted;
            var question = asked.Question.Trim();
            if (question.Length == 0) return Results.BadRequest();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await library.Get<LibraryStore>().GetSourceAsync(documentId, ct) is null) return Results.NotFound();
            http.Response.ContentType = "application/x-ndjson";
            http.Response.Headers.CacheControl = "no-store";
            await new Answering(library.Get<AskService>(), documentId, question, http.Response.Body, log, ct).RunAsync();
            return Results.Empty;
        });
    }

    /// <summary>An answer's parts as the panel shows them; a citation to another book opens it under the card in <paramref name="cards"/>.</summary>
    internal static IReadOnlyList<AskPart> Parts(IEnumerable<AnswerPart> parts, IReadOnlyDictionary<long, long>? cards = null) =>
        [.. parts.Select(p => new AskPart(p.Kind switch
        {
            AnswerPartKind.Quote => AskPartKind.Quote,
            AnswerPartKind.Paraphrase => AskPartKind.Paraphrase,
            AnswerPartKind.Citation => AskPartKind.Citation,
            AnswerPartKind.Unsupported => AskPartKind.Unsupported,
            _ => AskPartKind.Text,
        }, p.Text, p.PdfPage, p.Phrase, p.DocumentId, p.DocumentId is { } document ? cards?.GetValueOrDefault(document) : null))];

    static async Task SendAsync(Stream body, AskUpdate update, CancellationToken ct)
    {
        await JsonSerializer.SerializeAsync(body, update, JsonSerializerOptions.Web, ct);
        await body.WriteAsync("\n"u8.ToArray(), ct);
        await body.FlushAsync(ct);
    }

    /// <summary>One question, from rewriting a follow-up through finding its pages to the answer, each step a line of the stream.</summary>
    sealed class Answering(AskService ask, long documentId, string question, Stream body, ILogger log, CancellationToken ct)
    {
        ModelProgress? _latest;

        public async Task RunAsync()
        {
            try
            {
                if (await ask.WhyNotAsync(documentId, ct) is { } why)
                {
                    await SendAsync(new AskUpdate { Message = why, Done = true });
                    return;
                }
                var earlier = (await ask.HistoryAsync(documentId, ct)).Select(t => t.Earlier).ToList();
                var looked = question;
                var rewritten = false;
                if (earlier.Count > 0 && ask.CanAnswer)
                {
                    await SendAsync(new AskUpdate { Step = AskText.Rewriting });
                    (looked, rewritten) = await ask.RewriteAsync(documentId, question, earlier, ct);
                }
                await SendAsync(new AskUpdate { Step = AskText.Looking, LookedFor = rewritten ? AskText.LookedFor(looked) : null });
                var findings = await ask.FindAsync(documentId, looked, ct);
                var found = new AskUpdate
                {
                    Passages = [.. findings.Passages.Select(p => new AskPassageLine(p.PdfPage, AskText.Link(p), p.Text, AskText.Found(p), p.Phrase))],
                    PassagesHeading = AskText.PagesHeading,
                    Message = AskText.Weak(findings),
                    Coverage = AskText.Coverage(findings.Coverage),
                    Meaning = AskText.Meaning(findings, ask.FillOf(documentId)),
                };
                if (findings.Weak || findings.Evidence.Count == 0 || !ask.CanAnswer)
                {
                    await SendAsync(found with { Done = true });
                    return;
                }
                await SendAsync(found with { Step = AskText.Waiting, Answering = true });
                await SendAsync(await AnswerAsync(findings, earlier, rewritten ? looked : null) with
                {
                    Meaning = AskText.Meaning(findings, ask.FillOf(documentId)),
                    Done = true,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Stopped, or the reader closed: nobody is reading the stream any more.
            }
            catch (AskUnavailableException ex)
            {
                await SendAsync(new AskUpdate { Message = ex.Message, Done = true });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogError(ex, "Asking document {DocumentId} failed", documentId);
                await SendAsync(new AskUpdate { Message = AskText.Failed, Done = true });
            }
        }

        /// <summary>Has the model answer from the pages found, sending the answer on as it is written, and keeps the turn.</summary>
        async Task<AskUpdate> AnswerAsync(AskFindings findings, IReadOnlyList<AskEarlierTurn> earlier, string? rewritten)
        {
            var pages = AskService.PromptPages(findings);
            using var writing = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sending = SendProgressAsync(pages, writing.Token);
            try
            {
                var answer = await ask.AnswerAsync(documentId, findings, new Progress(this), earlier, ct);
                await writing.CancelAsync();
                await sending;
                var saved = await ask.SaveTurnAsync(documentId, question, rewritten, answer, CancellationToken.None);
                return new AskUpdate
                {
                    Answer = answer.Checked.NotAnswered ? [] : Parts(answer.Checked.Parts),
                    Message = answer.Checked.NotAnswered ? AskText.NotAnswered : null,
                    PassagesHeading = AskText.ReadFrom(pages),
                    Turn = saved is null ? null : new AskTurn(question, rewritten is null ? null : AskText.LookedFor(rewritten), Parts(saved.Parts)),
                };
            }
            catch (ModelEmptyAnswerException)
            {
                return new AskUpdate { Message = AskText.Empty };
            }
            catch (ModelEndpointException ex)
            {
                return new AskUpdate { Message = AskText.CouldNotAnswer(ex.Message) };
            }
            finally
            {
                await writing.CancelAsync();
                await sending;
            }
        }

        /// <summary>Sends the latest the model has written, checked against the pages, every <see cref="StreamEvery"/> while it writes.</summary>
        async Task SendProgressAsync(IReadOnlyList<AskPromptPage> pages, CancellationToken stop)
        {
            ModelProgress? sent = null;
            try
            {
                while (true)
                {
                    await Task.Delay(StreamEvery, stop);
                    if (Volatile.Read(ref _latest) is not { } latest || ReferenceEquals(latest, sent)) continue;
                    sent = latest;
                    // Not cut short by the answer finishing, so a line is never left half written.
                    await SendAsync(new AskUpdate
                    {
                        Step = AskText.Step(latest),
                        Answering = true,
                        Answer = latest.Step == ModelStep.Writing ? Parts(AskAnswerCheck.Check(latest.Answer, pages).Parts) : null,
                    });
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }

        Task SendAsync(AskUpdate update) => AskApi.SendAsync(body, update, ct);

        /// <summary>Keeps only the latest progress; the sender picks it up on its next beat.</summary>
        sealed class Progress(Answering answering) : IProgress<ModelProgress>
        {
            public void Report(ModelProgress value) => Volatile.Write(ref answering._latest, value);
        }
    }

    /// <summary>A question to the library, from finding its pages across the books in scope to the answer, each step a line of the stream.</summary>
    sealed class AnsweringLibrary(UserLibrary library, string question, IReadOnlyList<AskFilter> filters, Stream body, ILogger log, CancellationToken ct)
    {
        readonly LibraryAskService _ask = library.Get<LibraryAskService>();
        ModelProgress? _latest;

        public async Task RunAsync()
        {
            try
            {
                await SendAsync(new AskUpdate { Step = LibraryAskText.Looking });
                var findings = await _ask.FindAsync(question, filters, ct);
                var cards = await CardsAsync(findings);
                var found = new AskUpdate
                {
                    Passages = [.. findings.Passages.Select(p => new AskPassageLine(p.PdfPage, LibraryAskText.Link(p), p.Text, LibraryAskText.Found(p), p.Phrase,
                        p.DocumentId, cards.GetValueOrDefault(p.DocumentId), LibraryAskText.Open(p)))],
                    PassagesHeading = AskText.PagesHeading,
                    Message = LibraryAskText.Weak(findings),
                    Coverage = LibraryAskText.Coverage(findings),
                };
                if (findings.Weak || findings.Evidence.Count == 0 || !_ask.CanAnswer)
                {
                    await SendAsync(found with { Done = true });
                    return;
                }
                await SendAsync(found with { Step = AskText.Waiting, Answering = true });
                await SendAsync(await AnswerAsync(findings, cards) with { Done = true });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Stopped, or Ask closed: nobody is reading the stream any more.
            }
            catch (AskUnavailableException ex)
            {
                await SendAsync(new AskUpdate { Message = ex.Message, Done = true });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogError(ex, "Asking the library failed");
                await SendAsync(new AskUpdate { Message = AskText.Failed, Done = true });
            }
        }

        /// <summary>The card each book found is opened under: its own, or for a file in a pack, the pack's.</summary>
        async Task<IReadOnlyDictionary<long, long>> CardsAsync(LibraryAskFindings findings)
        {
            var entries = library.Get<EntryStore>();
            var cards = new Dictionary<long, long>();
            foreach (var documentId in findings.Passages.Select(p => p.DocumentId).Concat(findings.Evidence.Select(e => e.DocumentId)).Distinct())
                if (await entries.GetCardAsync(documentId, ct) is { } card) cards[documentId] = card.Value;
            return cards;
        }

        /// <summary>Has the model answer from the pages found, sending the answer on as it is written.</summary>
        async Task<AskUpdate> AnswerAsync(LibraryAskFindings findings, IReadOnlyDictionary<long, long> cards)
        {
            var pages = LibraryAskService.PromptPages(findings);
            using var writing = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sending = SendProgressAsync(pages, findings.Evidence, cards, writing.Token);
            try
            {
                var answer = await _ask.AnswerAsync(findings, new Progress(this), ct);
                await writing.CancelAsync();
                await sending;
                return new AskUpdate
                {
                    Answer = answer.Checked.NotAnswered ? [] : Parts(answer.Checked.Parts, cards),
                    Message = answer.Checked.NotAnswered ? AskText.NotAnswered : null,
                    PassagesHeading = LibraryAskText.ReadFrom(findings),
                };
            }
            catch (ModelEmptyAnswerException)
            {
                return new AskUpdate { Message = AskText.Empty };
            }
            catch (ModelEndpointException ex)
            {
                return new AskUpdate { Message = AskText.CouldNotAnswer(ex.Message) };
            }
            finally
            {
                await writing.CancelAsync();
                await sending;
            }
        }

        /// <summary>Sends the latest the model has written, checked against the pages, every <see cref="StreamEvery"/> while it writes.</summary>
        async Task SendProgressAsync(IReadOnlyList<AskPromptPage> pages, IReadOnlyList<LibraryAskEvidence> evidence, IReadOnlyDictionary<long, long> cards,
            CancellationToken stop)
        {
            ModelProgress? sent = null;
            try
            {
                while (true)
                {
                    await Task.Delay(StreamEvery, stop);
                    if (Volatile.Read(ref _latest) is not { } latest || ReferenceEquals(latest, sent)) continue;
                    sent = latest;
                    // Not cut short by the answer finishing, so a line is never left half written.
                    await SendAsync(new AskUpdate
                    {
                        Step = AskText.Step(latest),
                        Answering = true,
                        Answer = latest.Step == ModelStep.Writing ? Parts(LibraryAskService.Check(latest.Answer, pages, evidence).Parts, cards) : null,
                    });
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }

        Task SendAsync(AskUpdate update) => AskApi.SendAsync(body, update, ct);

        /// <summary>Keeps only the latest progress; the sender picks it up on its next beat.</summary>
        sealed class Progress(AnsweringLibrary answering) : IProgress<ModelProgress>
        {
            public void Report(ModelProgress value) => Volatile.Write(ref answering._latest, value);
        }
    }
}
