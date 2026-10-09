using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Dapper;

namespace Bibliotaph.Processing.Tests;

/// <summary>The model pilot (slice 2d) over a real library, with fake models in place of the server.</summary>
public sealed partial class PipelineTests
{
    async Task<PilotService> PilotOverLanternAsync()
    {
        Copy(pdfs.Injected, "Adventures/Lantern.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        // The pilot picks books with some length to them: three pages of more than 200 characters.
        await _writer.WriteAsync((c, t) => c.Execute("UPDATE page SET text = text || ' ' || @filler", new { filler = string.Join(' ', Enumerable.Repeat("More of the adventure.", 12)) }, t), Ct);
        // Set up but switched off, so only the pilot sends anything.
        await _ai.SaveAsync(new AiSetup(Endpoint, "fake-8b", "ollama", Enabled: false, RemoteAllowed: false, new HashSet<long>()), Ct);
        return new PilotService(new PilotStore(_paths.Pilot), _ai, _service, _queries, _libraryStore, new ClassifierInputs(_libraryStore, _queries, _vocabulary),
            _metadata, _vocabulary);
    }

    [Fact]
    public async Task The_pilot_runs_each_model_once_per_book_into_its_own_database_and_scores_the_answers()
    {
        var pilot = await PilotOverLanternAsync();
        var lanePaused = new List<bool>();
        _model.Answer = request =>
        {
            lanePaused.Add(_service.IsPaused(Lane.Classify));
            return ObedientAnswer;
        };

        var book = Assert.Single(await pilot.PickBooksAsync(ct: Ct));
        Assert.Contains($"{book.DocumentId}\t{book.Path}", await File.ReadAllTextAsync(pilot.Store.BookListPath, Ct));
        var progress = new List<PilotProgress>();
        var end = await pilot.RunAsync(["fake-8b", "fake-14b"], new Collect<PilotProgress>(progress.Add), Ct);

        Assert.Equal(new PilotRunEnd(true, null), end);
        Assert.Equal([true, true], lanePaused);
        Assert.False(_service.IsPaused(Lane.Classify));
        Assert.Equal([("fake-8b", 0), ("fake-8b", 1), ("fake-14b", 0), ("fake-14b", 1)], progress.Select(p => (p.Model, p.Done)));
        // Nothing reached the catalog: the proposals are in pilot.db only.
        Assert.DoesNotContain((await _metadataStore.GetAsync(book.DocumentId, Ct)).Claims, c => c.Origin == AssertionOrigin.Ai);
        Assert.Empty(await _runs.GetReadByAsync(ct: Ct));
        var proposals = await pilot.Store.GetProposalsAsync(book.DocumentId, Ct);
        Assert.Contains(proposals, p => p is { Model: "fake-14b", Field: "title", Value: "The Sunken Lantern", Kept: true, PdfPage: 0 });
        Assert.Contains(proposals, p => p is { Field: "publisher", Value: "Evil Corp", Kept: false, Reason: not null });

        // A second run carries on where the first stopped, so a model that has read a book doesn't read it again.
        Assert.Equal(new PilotRunEnd(true, null), await pilot.RunAsync(["fake-8b", "fake-14b"], ct: Ct));
        Assert.Equal(2, _model.Requests.Count);

        // The review page offers the models' title once, with its page, and the catalog's title from the file name.
        var review = await pilot.GetReviewAsync(book, Ct);
        var title = review.Fields.Single(f => f.Field == MetadataFields.Title);
        Assert.Contains(new PilotOption("The Sunken Lantern", 0, "THE SUNKEN LANTERN"), title.Options);
        Assert.Contains(title.Options, o => o is { Text: "Lantern", PdfPage: null });
        Assert.False(review.Answered);

        var problem = await pilot.SaveAnswersAsync(book.DocumentId, new Dictionary<MetadataField, PilotAnswer>
        {
            [MetadataFields.Title] = new("The Sunken Lantern", false),
            [MetadataFields.Levels] = new("3", false),
            [MetadataFields.Publisher] = new("", true),
        }, alsoCatalog: true, Ct);

        Assert.Null(problem);
        var (effective, _) = await _metadata.GetAsync(book.DocumentId, Ct);
        Assert.Equal(("The Sunken Lantern", true), (effective[MetadataFields.Title].First!.Value, effective[MetadataFields.Title].First!.Confirmed));
        Assert.Equal("3", effective[MetadataFields.Levels].First!.Value);
        Assert.True((await pilot.GetReviewAsync(book, Ct)).Fields.Single(f => f.Field == MetadataFields.Publisher).NotInBook);
        var scores = await pilot.ScoreAsync(Ct);
        Assert.Equal(["fake-8b", "fake-14b"], scores.Select(s => s.Model));
        Assert.All(scores, s => Assert.Equal(new PilotFieldScore("title", 1, 1, 1, 1), s.Fields.Single(f => f.Field == "title")));
        Assert.All(scores, s => Assert.Equal(new PilotFieldScore("levels", 1, 1, 1, 1), s.Fields.Single(f => f.Field == "levels")));
        var status = await pilot.GetStatusAsync(Ct);
        Assert.Equal((1, 1), (status.Books, status.Answered));
        Assert.Equal(new Dictionary<string, int> { ["fake-8b"] = 1, ["fake-14b"] = 1 }, status.ReadByModel);
        var report = await File.ReadAllTextAsync(await pilot.WriteReportAsync(Ct), Ct);
        Assert.Contains("fake-14b", report);
        Assert.DoesNotContain("THE SUNKEN LANTERN", report);
    }

    [Fact]
    public async Task A_bad_answer_is_the_models_failure_and_a_server_that_goes_away_stops_the_run()
    {
        var pilot = await PilotOverLanternAsync();
        await pilot.PickBooksAsync(ct: Ct);
        _model.Answer = _ => "I think this book is about lanterns.";

        Assert.Equal(new PilotRunEnd(true, null), await pilot.RunAsync(["fake-8b"], ct: Ct));
        Assert.NotNull(Assert.Single(await pilot.Store.GetRunsAsync(Ct)).Problem);

        _model.Down = true;
        var end = await pilot.RunAsync(["fake-14b"], ct: Ct);

        Assert.False(end.Finished);
        Assert.Contains("Nothing is answering", end.Problem);
        Assert.False(_service.IsPaused(Lane.Classify));
        Assert.Single(await pilot.Store.GetRunsAsync(Ct));
    }
}
