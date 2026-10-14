using Bibliotaph.App.ViewModels;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using static Bibliotaph.App.Tests.Values;

namespace Bibliotaph.App.Tests;

/// <summary>Needs review's card for one field of one book: Accept, Reject, Edit, and Undo once decided.</summary>
public class ReviewCardTests
{
    readonly FakeReviewActions _actions = new();

    ReviewCardViewModel Card(ReviewKind kind, MetadataField field, IReadOnlyList<EffectiveValue> current, IReadOnlyList<EffectiveValue> suggested, int groupSize = 1) =>
        new(new ReviewItem(new EntryId(1001), 1, "The Sunken Bell", new ReviewIssue(field, kind, current, suggested), groupSize), Terms, _actions);

    ReviewCardViewModel Conflict(int groupSize = 1) => Card(ReviewKind.Conflict, MetadataFields.System,
        [Value(MetadataFields.System, "dnd5e")], [Value(MetadataFields.System, "pf2e", origin: AssertionOrigin.Ai)], groupSize);

    [Fact]
    public async Task Accepting_folds_the_card_to_a_line_saying_what_the_field_is_now()
    {
        var card = Conflict();

        await card.AcceptCommand.ExecuteAsync(null);

        Assert.True(card.IsDone);
        Assert.Equal("Game system is now Pathfinder 2e.", card.DoneText);
        Assert.Equal(["accept"], _actions.Calls);
        Assert.Equal([-1], _actions.Decisions);
    }

    [Fact]
    public async Task Undo_takes_the_answer_back_and_opens_the_card_again()
    {
        var card = Conflict();
        await card.AcceptCommand.ExecuteAsync(null);

        await card.UndoCommand.ExecuteAsync(null);

        Assert.False(card.IsDone);
        Assert.Equal(1, _actions.Undone);
        Assert.Equal([-1, 1], _actions.Decisions);
    }

    [Fact]
    public async Task Rejecting_where_sources_disagree_keeps_what_the_field_shows()
    {
        var card = Conflict();

        await card.RejectCommand.ExecuteAsync(null);

        Assert.Equal("Kept D&D 5e.", card.DoneText);
        Assert.Equal("Keep D&D 5e; Pathfinder 2e won't be suggested again", card.RejectHint);
    }

    [Fact]
    public async Task Rejecting_a_suggestion_turns_down_what_it_proposed()
    {
        var card = Card(ReviewKind.Suggestion, MetadataFields.Types,
            [Value(MetadataFields.Types, "adventure", confirmed: true)],
            [Value(MetadataFields.Types, "adventure", confirmed: true), Value(MetadataFields.Types, "map")]);

        await card.RejectCommand.ExecuteAsync(null);

        Assert.Equal("Rejected Map.", card.DoneText);
    }

    [Fact]
    public async Task While_one_answer_is_being_saved_the_card_ignores_another()
    {
        var card = Conflict();
        _actions.HoldAccept = new TaskCompletionSource();

        var accepting = card.AcceptCommand.ExecuteAsync(null);
        await card.RejectCommand.ExecuteAsync(null);
        var busy = card.IsBusy;
        _actions.HoldAccept.SetResult();
        await accepting;

        Assert.True(busy);
        Assert.Equal(["accept"], _actions.Calls);
        Assert.False(card.IsBusy);
    }

    [Fact]
    public void A_missing_title_with_nothing_to_keep_can_only_be_typed()
    {
        var card = Card(ReviewKind.MissingTitle, MetadataFields.Title, [], []);

        Assert.False(card.CanAccept);
        Assert.False(card.CanReject);
        Assert.Equal("Type a title", card.EditLabel);
        Assert.Equal("", card.EditText);
    }

    [Fact]
    public async Task A_title_typed_on_the_card_decides_it()
    {
        var card = Card(ReviewKind.MissingTitle, MetadataFields.Title, [], []);
        card.EditCommand.Execute(null);
        card.EditText = "The  Sunken Bell ";

        await card.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["edit The  Sunken Bell "], _actions.Calls);
        Assert.False(card.IsEditing);
        Assert.Equal("Title is now The Sunken Bell.", card.DoneText);
    }

    [Fact]
    public async Task A_value_that_cannot_be_saved_keeps_the_editor_open_and_says_why()
    {
        var card = Conflict();
        _actions.Problem = "Game system needs some letters or numbers.";
        card.EditCommand.Execute(null);
        card.EditText = "!!";

        await card.SaveCommand.ExecuteAsync(null);

        Assert.True(card.IsEditing);
        Assert.False(card.IsDone);
        Assert.Equal("Game system needs some letters or numbers.", card.Problem);
        Assert.Empty(_actions.Decisions);
    }

    [Fact]
    public async Task Emptying_a_field_with_several_values_says_it_was_cleared()
    {
        var card = Card(ReviewKind.Suggestion, MetadataFields.Types, [], [Value(MetadataFields.Types, "map")]);
        card.EditText = " , ";

        await card.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Type cleared.", card.DoneText);
    }

    [Fact]
    public void A_value_picked_from_the_list_is_added_to_a_field_with_several_values_once()
    {
        var card = Card(ReviewKind.Suggestion, MetadataFields.Types, [], [Value(MetadataFields.Types, "adventure")]);

        card.PickedChoice = "Map";
        card.PickedChoice = "adventure";

        Assert.Equal("Adventure, Map", card.EditText);
        Assert.Null(card.PickedChoice);
    }

    [Fact]
    public void Typing_another_name_for_a_term_says_which_term_it_files_as()
    {
        var card = Conflict();

        card.EditText = "pf2e";

        Assert.Equal("“pf2e” is another name for Pathfinder 2e.", card.Resolution);
    }
}
