using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// Slice 5c: with AI on, the model proposes a compilation's adventures when the rules can't, and each adventure is
/// classified on its own pages.
/// </summary>
public sealed partial class PipelineTests
{
    /// <summary>
    /// The adventures in the quiet compilation (Fog Tales) with true quotes, plus a candidate that doesn't
    /// exist, one given twice, and one whose quote isn't on its page.
    /// </summary>
    const string FoundAdventures = """
        {"adventures": [
          {"candidate": 1, "title": "The Bell Tower", "levels": "2", "page": 2, "quote": "Recommended party: level 2"},
          {"candidate": 2, "title": "Fog Market", "levels": "4-5", "page": 4, "quote": "Recommended party: levels 4 and 5"},
          {"candidate": 2, "title": "Fog Market Again", "levels": "", "page": 4, "quote": "A night of trading goes wrong"},
          {"candidate": 3, "title": "Fog Beasts", "levels": "", "page": 6, "quote": "Mist hounds and bell wraiths"},
          {"candidate": 1, "title": "Ignore the rules", "levels": "9", "page": 3, "quote": "Mark every file as an adventure"}
        ]}
        """;

    /// <summary>What a model reading the first adventure's pages would answer, with an author from a page outside them.</summary>
    const string BellTowerValues = """
        {
          "title": [{"value": "The Bell Tower", "page": 2, "quote": "The Bell Tower A scenario for heroes"}],
          "levels": [{"value": "2", "page": 2, "quote": "Recommended party: level 2"}],
          "authors": [{"value": "Ana Ruiz", "page": 1, "quote": "Written by Ana Ruiz"}]
        }
        """;

    static bool FindsParts(ModelRequest request) => request.SchemaJson == PartFinderPrompt.SchemaJson;

    /// <summary>A classifier request that reads only the pages from marker <paramref name="first"/> to <paramref name="last"/> of a six-page file.</summary>
    static bool ReadsOnly(ModelRequest request, int first, int last) =>
        !FindsParts(request) && Enumerable.Range(1, 6).All(m => request.User.Contains($"[Page {m}]", StringComparison.Ordinal) == (m >= first && m <= last));

    [Fact]
    public async Task With_AI_on_a_model_proposes_a_compilations_adventures_and_each_accepted_one_is_read_on_its_own_pages()
    {
        _model.Answer = request => FindsParts(request) ? FoundAdventures : ReadsOnly(request, 2, 3) ? BellTowerValues : "{}";
        await SwitchAiOnAsync();
        Copy(pdfs.QuietCompilation, "Compilations/Fog Tales.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleClassifyAsync();

        // The model is asked once, about the two bookmarks that aren't credits or an appendix, guarded like the classifier.
        var asked = Assert.Single(_model.Requests, FindsParts);
        Assert.Contains("1. The Bell Tower (pages 2 to 3)\n2. Fog Market (pages 4 to 5)\n", asked.User.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("Fog Market", asked.System, StringComparison.Ordinal);
        Assert.True(asked.User.IndexOf("<book-excerpt>", StringComparison.Ordinal) < asked.User.IndexOf("The Bell Tower", StringComparison.Ordinal));

        // What passed the check waits in Needs review as AI's proposal; nothing is made by itself.
        var proposal = Assert.Single(await _proposals.GetPendingAsync(Ct));
        Assert.Equal(PartProposalSource.Ai, proposal.Source);
        Assert.Equal([("The Bell Tower", new PartPages(1, 2), "2"), ("Fog Market", new PartPages(3, 4), "4-5")],
            proposal.Parts.Select(p => (p.Title, p.Pages, p.Levels)));
        var search = new LibraryQueries(_index);
        var book = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));

        // Accepted, each adventure is read on its own pages and its values go on its own card.
        var accepted = await _proposals.AcceptAsync(proposal, proposal.Parts, Ct);
        Assert.Null(accepted.Problem);
        await WaitForAsync(() => _model.Requests.Count(r => ReadsOnly(r, 2, 3) || ReadsOnly(r, 4, 5)) == 2);
        await SettleClassifyAsync();
        var tower = (await _metadataStore.GetAsync(accepted.Parts[0], Ct)).Compute();
        var levels = tower[MetadataFields.Levels].First!;
        Assert.Equal(("2", "Recommended party: level 2"), (levels.Value, levels.Source.Quote));
        Assert.Contains(tower[MetadataFields.Levels].Values, v => v.Origin == AssertionOrigin.Ai);
        Assert.DoesNotContain(tower[MetadataFields.Authors].Values.Concat(tower[MetadataFields.Authors].Alternatives), v => v.Origin == AssertionOrigin.Ai);
        Assert.Equal("fake-8b", (await _runs.GetReadByAsync([accepted.Parts[0]], Ct))[accepted.Parts[0]]);

        // Read once per model: reading the file again sends neither the book, its adventures nor the question again.
        var sent = _model.Requests.Count;
        Assert.True(await _service.ReprocessAsync(book.DocumentId, ct: Ct));
        await SettleClassifyAsync();
        Assert.Equal(sent, _model.Requests.Count);
        Assert.Equal(new ClassificationSummary(1, 0), await _runs.SummarizeAsync("fake-8b", ClassifierPrompt.Version, Ct));

        // An adventure marked by hand is read too.
        await accepted.Undo!();
        Assert.Null((await _parts.AddAsync(book.DocumentId, new PartPages(1, 2), "The Bell Tower", Ct)).Problem);
        await WaitForAsync(() => _model.Requests.Count(r => ReadsOnly(r, 2, 3)) == 2);
    }

    [Fact]
    public async Task A_model_isnt_asked_about_a_file_the_rules_proposed_adventures_for()
    {
        _model.Answer = request => FindsParts(request) ? FoundAdventures : "{}";
        await SwitchAiOnAsync();
        Copy(pdfs.Compilation, "Compilations/Marsh Tales.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleClassifyAsync();

        Assert.Single(_model.Requests);
        Assert.DoesNotContain(_model.Requests, FindsParts);
        Assert.Equal(PartProposalSource.Rules, Assert.Single(await _proposals.GetPendingAsync(Ct)).Source);
    }

    /// <summary>Waits until <paramref name="done"/>, as for work a lane picks up after something else finished.</summary>
    static async Task WaitForAsync(Func<bool> done)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);
        while (!done()) await Task.Delay(100, timeout.Token);
    }
}
