using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;

namespace Bibliotaph.App.Tests;

/// <summary>Needs review's cards for a new term, the adventures proposed in a file, and the places in a new version of a book.</summary>
public class OtherReviewCardTests
{
    readonly FakeReviewActions _actions = new();

    TermCardViewModel TermCard() =>
        new(new PendingTerm(5, new Term("type", "heist", "Heist"), MetadataFields.Types, 3, "a daring heist"), Values.Terms, _actions);

    [Fact]
    public void A_new_term_can_be_filed_only_under_a_term_of_its_own_field_once_one_is_picked()
    {
        var card = TermCard();

        Assert.Equal(["Adventure", "Map"], card.Targets.Select(t => t.Label));
        Assert.False(card.MapCommand.CanExecute(null));
        card.Target = card.Targets[0];
        Assert.True(card.MapCommand.CanExecute(null));
    }

    [Fact]
    public async Task Filing_a_new_term_under_another_says_where_it_went()
    {
        var card = TermCard();
        card.Target = card.Targets[0];

        await card.MapCommand.ExecuteAsync(null);

        Assert.Equal(["map Heist to adventure"], _actions.Calls);
        Assert.Equal("Filed Heist under Adventure.", card.DoneText);
    }

    [Fact]
    public async Task Adding_a_new_term_names_the_field_it_joins()
    {
        var card = TermCard();

        await card.AddCommand.ExecuteAsync(null);

        Assert.Equal("Added Heist as a new type.", card.DoneText);
    }

    // A 40-page file numbered 1 to 40 as printed.
    static readonly IReadOnlyList<string?> Labels = [.. Enumerable.Repeat<string?>(null, 40)];

    PartProposalCardViewModel Proposal() => new(new PartProposalItem(new EntryId(2001), "Mill Road Anthology", 21, PartProposalSource.Rules,
        [new ProposedPart("The Sunken Bell", new PartPages(2, 9)), new ProposedPart("Ashes of Varn", new PartPages(10, 19), Levels: "3-5")]), Labels, _actions);

    [Fact]
    public void Each_proposed_adventure_shows_its_pages_as_the_file_numbers_them()
    {
        var card = Proposal();

        Assert.Equal("pp. 3–10", card.Rows[0].Summary);
        Assert.Equal("pp. 11–20 · Levels 3–5", card.Rows[1].Summary);
    }

    [Fact]
    public void Unticking_an_adventure_counts_one_card_fewer()
    {
        var card = Proposal();
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        card.Rows[1].IsChecked = false;

        Assert.Equal("Make 1 card", card.AcceptLabel);
        Assert.Contains(nameof(card.AcceptLabel), changed);
    }

    [Fact]
    public async Task Accept_makes_cards_for_the_ticked_adventures_as_edited()
    {
        var card = Proposal();
        card.Rows[0].Title = "  The Sunken Bell, revised ";
        card.Rows[0].To = "";
        card.Rows[1].IsChecked = false;

        await card.AcceptCommand.ExecuteAsync(null);

        var part = Assert.Single(_actions.AcceptedParts!);
        Assert.Equal(new ProposedPart("The Sunken Bell, revised", new PartPages(2, 2)), part);
        Assert.Equal("Mill Road Anthology: 1 adventure with cards of their own.", card.DoneText);
    }

    [Fact]
    public async Task Pages_the_file_does_not_have_stop_Accept_and_open_the_editor()
    {
        var card = Proposal();
        card.Rows[1].To = "41";

        await card.AcceptCommand.ExecuteAsync(null);

        Assert.Empty(_actions.Calls);
        Assert.True(card.IsEditing);
        Assert.Equal("The pages of Ashes of Varn don't read as pages of this file.", card.Problem);
        Assert.Equal("pp. 11–41", card.Rows[1].PagesText);
    }

    [Fact]
    public async Task A_problem_with_the_adventures_leaves_the_card_open()
    {
        var card = Proposal();
        _actions.Problem = "The Sunken Bell and Ashes of Varn share a page.";

        await card.AcceptCommand.ExecuteAsync(null);

        Assert.False(card.IsDone);
        Assert.Equal("The Sunken Bell and Ashes of Varn share a page.", card.Problem);
    }

    static RevisionPlace Place(PageCheck outcome, int page, string text) =>
        new(new PlacedPages(PlaceKind.SessionItem, page, new EntryId(3001), new PageRange(31, page, page), text, "Session 4"),
            new PageRefCheck(outcome, 32, page, page), null);

    RevisionCardViewModel Revision() => new(new RevisionItem(new EntryId(3001), 32, "The Sunken Bell", null,
        [Place(PageCheck.NeedsLook, 4, "Bell tower map"), Place(PageCheck.Found, 7, "Crypt"), Place(PageCheck.NeedsLook, 12, "The drowned choir")]), _actions);

    [Fact]
    public void A_new_version_lists_the_places_that_need_a_look_and_names_those_found()
    {
        var card = Revision();

        Assert.Equal("New version of The Sunken Bell: 1 place found its page, 2 need a look", card.Heading);
        Assert.Equal(["Bell tower map", "The drowned choir"], card.Rows.Select(r => r.Place.Place.Text));
        Assert.Equal(["Crypt in Session 4"], card.Found);
    }

    [Fact]
    public async Task Using_the_suggested_page_for_a_place_counts_it_as_checked()
    {
        var card = Revision();

        await card.Rows[0].UseCommand.ExecuteAsync(null);

        Assert.True(card.Rows[0].IsChecked);
        Assert.True(card.Rows[0].CanUndoUse);
        Assert.Equal([card.Rows[0].Place], _actions.UsedPlaces);
        Assert.EndsWith("1 needs a look", card.Heading, StringComparison.Ordinal);
        Assert.False(card.IsDone);
    }

    [Fact]
    public async Task The_card_is_done_once_every_place_is_checked_and_its_Undo_puts_every_page_back()
    {
        var card = Revision();
        await card.Rows[0].UseCommand.ExecuteAsync(null);
        await card.Rows[1].UseCommand.ExecuteAsync(null);

        Assert.True(card.IsDone);
        Assert.Equal("Every place in the new version of The Sunken Bell is checked.", card.DoneText);

        await card.UndoCommand.ExecuteAsync(null);

        Assert.False(card.IsDone);
        Assert.Equal(2, _actions.Undone);
        Assert.All(card.Rows, r => Assert.False(r.IsChecked));
        Assert.Equal([-1, 1], _actions.Decisions);
    }

    [Fact]
    public async Task A_page_taken_for_one_place_can_be_put_back_before_the_card_is_done()
    {
        var card = Revision();
        await card.Rows[0].UseCommand.ExecuteAsync(null);

        await card.Rows[0].UndoUseCommand.ExecuteAsync(null);

        Assert.False(card.Rows[0].IsChecked);
        Assert.Equal(1, _actions.Undone);
        Assert.EndsWith("2 need a look", card.Heading, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_page_note_is_named_by_its_first_words()
    {
        var note = new RevisionPlace(new PlacedPages(PlaceKind.PageNote, 1, new EntryId(3001), new PageRange(31, 4, 4),
            "The lever behind the altar opens the vault, but only while the bell is ringing above"), new PageRefCheck(PageCheck.NeedsLook, 32, 4, 4), null);

        Assert.Equal("Note: The lever behind the altar opens the vault, but only while…", RevisionRowViewModel.Describe(note));
    }
}
