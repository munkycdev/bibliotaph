using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>The AI pass of Find overlaps: what it asks, what it keeps, and when it leaves the model alone.</summary>
    public sealed class OverlapTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        IReadOnlyList<ModelRequest> OverlapRequests() => [.. _model.Requests.Where(r => r.SchemaName == "term_overlaps")];

        VocabularyService Vocabulary(TermOverlapFinder finder) => new(_vocabulary, _projector, _service, _hints, _settings, finder: finder);

        [Fact]
        public async Task The_AI_pass_asks_once_per_list_of_names_keeps_what_it_can_match_and_shows_it_in_Needs_review()
        {
            await _vocabulary.AddTermAsync("setting", "Toril", Ct);
            await _vocabulary.AddTermAsync("setting", "Athas", Ct);
            await _vocabulary.AddTermAsync("setting", "Glorantha", Ct);
            _model.Answer = request => request.SchemaName != "term_overlaps" ? "{}" : request.User.Contains("Athas", StringComparison.Ordinal) ? """
                {"groups": [
                  {"names": ["Forgotten Realms", "toril"], "why": "Toril is the Forgotten Realms' world."},
                  {"names": ["Dark Sun", "Athas"], "why": "Athas is Dark Sun's world."},
                  {"names": ["Greyhawk", "Ravenloft"], "why": "Both came with Bibliotaph, so never doubles."},
                  {"names": ["Glorantha", "Nowhere"], "why": "An unknown name matches nothing."}
                ]}
                """ : """{"groups": []}""";
            await SwitchAiOnAsync();
            using var finder = new TermOverlapFinder(_vocabulary, _settings, _ai, settle: TimeSpan.FromMilliseconds(50));
            var vocabulary = Vocabulary(finder);

            var found = await finder.RunAsync(Ct);
            var again = await finder.RunAsync(Ct);
            var proposals = await vocabulary.FindOverlapsAsync("setting", Ct);
            var all = await vocabulary.FindAllOverlapsAsync(Ct);
            var found2 = await Review().CountAsync(Ct);

            Assert.True(found);
            Assert.False(again);
            var request = Assert.Single(OverlapRequests(), r => r.User.Contains("Athas", StringComparison.Ordinal));
            Assert.Contains("list of settings", request.System);
            Assert.Contains("Forgotten Realms (also: Faerun; FR; Sword Coast)", request.User);
            Assert.Equal(TermOverlapPrompt.SchemaJson, request.SchemaJson);
            Assert.Equal(["Forgotten Realms · Toril", "Dark Sun · Athas"], proposals.Select(ReviewText.OverlapTitle));
            Assert.All(proposals, p => Assert.True(p.FromAi));
            Assert.Equal("The AI says they are the same thing: Athas is Dark Sun's world.", ReviewText.OverlapDetail(proposals[1]));
            Assert.Equal(proposals.Select(p => p.Id), all.Where(p => p.Vocabulary == "setting").Select(p => p.Id));
            Assert.Equal(all.Count, found2);
            // The Forgotten Realms came with Bibliotaph, so it is the one to keep.
            Assert.Equal(proposals[0].Terms.Single(t => t.Key == "forgotten-realms").Id, proposals[0].KeepId);
        }

        [Fact]
        public async Task A_group_answered_leaves_Needs_review_and_comes_back_on_undo_or_forget()
        {
            await _vocabulary.AddTermAsync("setting", "FR", Ct);
            await _vocabulary.AddAiOverlapsAsync("setting", [(["forgotten-realms", "fr"], "FR stands for the Forgotten Realms.")], Ct);
            var vocabulary = Vocabulary(null!);
            var proposal = Assert.Single(await vocabulary.FindOverlapsAsync("setting", Ct));

            var apart = await vocabulary.KeepApartAsync("setting", [.. proposal.Terms.Select(t => t.Key)], Ct);
            var whileApart = await vocabulary.FindOverlapsAsync("setting", Ct);
            foreach (var id in apart) await vocabulary.ForgetKeptApartAsync(id, Ct);
            var forgotten = await vocabulary.FindOverlapAsync(proposal.Id, Ct);
            var combined = await vocabulary.CombineAsync(proposal.KeepId, [.. proposal.Terms.Where(t => t.Id != proposal.KeepId).Select(t => t.Id)], Ct);
            var whileCombined = await vocabulary.FindOverlapsAsync("setting", Ct);
            await vocabulary.UndoCombineAsync(combined, Ct);
            var undone = await vocabulary.FindOverlapAsync(proposal.Id, Ct);

            Assert.Empty(whileApart);
            Assert.Equal((proposal.Id, proposal.Stamp), (forgotten?.Id, forgotten?.Stamp));
            Assert.Empty(whileCombined);
            Assert.Equal((proposal.Id, proposal.Stamp), (undone?.Id, undone?.Stamp));
        }

        [Fact]
        public async Task The_names_alone_and_the_AI_agreeing_make_one_card_and_a_run_waits_while_the_AI_is_busy()
        {
            await _vocabulary.AddTermAsync("setting", "The Forgotten Realms", Ct);
            await _vocabulary.AddAiOverlapsAsync("setting", [(["forgotten-realms", "the-forgotten-realms"], "The same world.")], Ct);
            _model.Answer = _ => """{"groups": []}""";
            await SwitchAiOnAsync();
            using var finder = new TermOverlapFinder(_vocabulary, _settings, _ai, settle: TimeSpan.FromMilliseconds(50));
            var vocabulary = Vocabulary(finder);

            var proposals = await vocabulary.FindOverlapsAsync("setting", Ct);
            using (_activity.Begin())
            {
                using var soon = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                soon.CancelAfter(TimeSpan.FromMilliseconds(400));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finder.RunAsync(soon.Token));
            }
            var asked = OverlapRequests().Count;
            await Task.Delay(_activity.QuietPeriod + TimeSpan.FromMilliseconds(100), Ct);
            await finder.RunAsync(Ct);

            var card = Assert.Single(proposals);
            Assert.False(card.FromAi);
            Assert.Equal("The same name but for small words, brackets or a plural", card.Reason);
            Assert.Equal(0, asked);
            Assert.NotEmpty(OverlapRequests());
        }
    }
}
