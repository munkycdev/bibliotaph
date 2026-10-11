using Bibliotaph.Classification;

namespace Bibliotaph.Processing.Tests;

/// <summary>Ask (scenario review, row 5) through the app's AI setup, with the fake model in place of the server.</summary>
public sealed partial class PipelineTests
{
    const string PlainSearchAnswer = """
        {"filters": [{"field": "system", "value": "Mothership", "exclude": false}, {"field": "environment", "value": "rainforest", "exclude": false},
                     {"field": "players", "value": "4", "exclude": false}],
         "words": [{"text": "terraform", "exclude": false}]}
        """;

    PlainSearchService PlainSearch() => new(_ai, _vocabulary, _queries);

    [Fact]
    public async Task With_AI_off_plain_search_says_it_needs_AI_and_sends_nothing()
    {
        var plainSearch = PlainSearch();

        var result = await plainSearch.InterpretAsync("Mothership jungle world with terraforming", Ct);

        Assert.False(plainSearch.IsReady);
        Assert.Equal((null, PlainSearchText.NeedsAi), (result.Interpretation, result.Problem));
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
    public async Task A_model_server_that_is_down_is_a_problem_to_show_not_an_error()
    {
        await SwitchAiOnAsync();
        _model.Down = true;

        var result = await PlainSearch().InterpretAsync("heists in a city", Ct);

        Assert.Null(result.Interpretation);
        Assert.StartsWith("The AI couldn't read your question. Nothing is answering", result.Problem, StringComparison.Ordinal);
    }
}
