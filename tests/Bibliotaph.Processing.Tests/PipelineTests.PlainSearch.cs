using Bibliotaph.Classification;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    const string PlainSearchAnswer = """
        {"filters": [{"field": "system", "value": "Mothership", "exclude": false}, {"field": "environment", "value": "rainforest", "exclude": false},
                     {"field": "players", "value": "4", "exclude": false}],
         "words": [{"text": "terraform", "exclude": false}]}
        """;

    PlainSearchService PlainSearch() => new(_ai, _vocabulary, _queries, new AiUse());

    /// <summary>Ask through the app's AI setup, with the fake model in place of the server.</summary>
    public sealed class PlainSearchTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        [Fact]
        public async Task With_AI_off_plain_search_reads_a_request_by_rules_and_sends_nothing()
        {
            var plainSearch = PlainSearch();

            var result = await plainSearch.InterpretAsync("Mothership for 4 players, a rainforest world with terraforming", Ct);

            Assert.False(plainSearch.IsReady);
            Assert.Null(result.Problem);
            Assert.Equal("system:Mothership players:4 environment:Jungle about:\"world with terraforming\"", result.Interpretation!.QueryText);
            Assert.Equal([PlainSearchText.ReadByRules], result.Interpretation.Notes);
            Assert.Empty(_model.Requests);
        }

        [Fact]
        public async Task With_AI_on_plain_search_reads_a_request_through_the_same_model_as_the_classifier()
        {
            await SwitchAiOnAsync();
            _model.Answer = _ => PlainSearchAnswer;
            var plainSearch = PlainSearch();

            var result = await plainSearch.InterpretAsync("Mothership for 4 players, a rainforest world with terraforming", Ct);

            Assert.True(plainSearch.IsReady);
            Assert.Null(result.Problem);
            // The starter vocabulary knows Mothership, and the rainforest as the Jungle environment.
            Assert.Equal("system:Mothership environment:Jungle players:4 terraform", result.Interpretation!.QueryText);
            var request = Assert.Single(_model.Requests);
            Assert.Equal(("fake-8b", PlainSearchPrompt.SchemaJson), (request.Model, request.SchemaJson));
            Assert.Contains("rainforest world with terraforming", request.User, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_model_server_that_is_down_reads_the_request_by_rules_and_says_why()
        {
            await SwitchAiOnAsync();
            _model.Down = true;

            var result = await PlainSearch().InterpretAsync("Mothership one-shots for 3 level 5 players", Ct);

            Assert.Null(result.Problem);
            Assert.Equal("system:Mothership duration:One-shot players:3 level:5", result.Interpretation!.QueryText);
            Assert.Contains(result.Interpretation.Notes, n => n.StartsWith("The AI couldn't read this (Nothing is answering", StringComparison.Ordinal)
                && n.EndsWith("so it was read without AI, by the names in your vocabulary.", StringComparison.Ordinal));
        }

        [Fact]
        public async Task An_answer_cut_off_by_its_token_limit_reads_the_request_by_rules_instead()
        {
            await SwitchAiOnAsync();
            _model.Answer = _ => throw new ModelAnswerCutOffException("{\"filters\": [", PlainSearchPrompt.AnswerTokens);

            var result = await PlainSearch().InterpretAsync("one-shots for 3 level 5 players", Ct);

            Assert.Null(result.Problem);
            Assert.Equal("duration:One-shot players:3 level:5", result.Interpretation!.QueryText);
            Assert.Contains(result.Interpretation.Notes, n => n.Contains("cut off", StringComparison.Ordinal));
        }
    }
}
