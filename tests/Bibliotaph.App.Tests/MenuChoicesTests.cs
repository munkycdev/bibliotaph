using Bibliotaph.App.Controls;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;

namespace Bibliotaph.App.Tests;

public class MenuChoicesTests
{
    /// <summary>A page that shows cards: what its menus list, and which cards are pinned.</summary>
    sealed class Page(params object[] pinned) : ICollectionChoices, ISessionChoices, IPinState
    {
        public IReadOnlyList<CollectionMenuChoice> MenuChoices => [];

        IReadOnlyList<SessionMenuChoice> ISessionChoices.MenuChoices => [];

        public bool IsPinned(object? context) => context is not null && pinned.Contains(context);
    }

    static readonly DateTime Day = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    static CollectionInfo Collection(long id, string name, int usedDaysAgo, long? parentId = null) =>
        new(id, parentId, name, null, null, Day.AddDays(-usedDaysAgo), 0);

    static SessionPackInfo Pack(long id, string title) => new(id, title, null, null, Day, 0, []);

    [Fact]
    public void A_cards_menu_finds_its_page_beyond_the_card()
    {
        var card = new object();
        var page = new Page();

        Assert.Same(page, MenuContext.Nearest<ICollectionChoices>([card, null, page]));
        Assert.Same(page, MenuContext.Nearest<ISessionChoices>([card, page]));
        Assert.Same(page, MenuContext.Nearest<IPinState>([card, page]));
    }

    [Fact]
    public void A_menu_whose_own_context_knows_the_choices_uses_it_over_the_pages_above()
    {
        var own = new Page();

        Assert.Same(own, MenuContext.Nearest<ICollectionChoices>([own, new Page()]));
    }

    [Fact]
    public void A_menu_with_nothing_above_that_knows_the_choices_finds_none()
    {
        Assert.Null(MenuContext.Nearest<ICollectionChoices>([new object(), null]));
        Assert.Null(MenuContext.Nearest<ISessionChoices>([]));
    }

    [Fact]
    public void The_pin_item_says_Unpin_for_a_pinned_card_and_Pin_otherwise()
    {
        var pinned = new object();
        var page = new Page(pinned);

        Assert.Equal("Unpin", PinMenu.Label(page, pinned));
        Assert.Equal("Pin", PinMenu.Label(page, new object()));
        Assert.Equal("Pin", PinMenu.Label(page, null));
        Assert.Equal("Pin", PinMenu.Label(null, pinned));
    }

    [Fact]
    public void Add_to_collection_lists_the_four_used_last_by_path_then_New_then_Choose()
    {
        IReadOnlyList<CollectionInfo> all =
        [
            Collection(1, "Winter campaign", usedDaysAgo: 9),
            Collection(2, "maps", usedDaysAgo: 1, parentId: 1),
            Collection(3, "Handouts", usedDaysAgo: 2),
            Collection(4, "Bestiary", usedDaysAgo: 3),
            Collection(5, "Tavern", usedDaysAgo: 4),
        ];
        var paths = new Dictionary<long, string>
        {
            [1] = "Winter campaign", [2] = "Winter campaign › maps", [3] = "Handouts", [4] = "Bestiary", [5] = "Tavern",
        };

        var choices = CollectionDirectory.MenuChoicesFor(all, id => paths[id]);

        Assert.Equal(
        [
            new CollectionMenuChoice(CollectionMenuKind.Collection, 4, "Add to Bestiary", "Bestiary"),
            new CollectionMenuChoice(CollectionMenuKind.Collection, 3, "Add to Handouts", "Handouts"),
            new CollectionMenuChoice(CollectionMenuKind.Collection, 5, "Add to Tavern", "Tavern"),
            new CollectionMenuChoice(CollectionMenuKind.Collection, 2, "Add to maps", "Winter campaign › maps"),
            new CollectionMenuChoice(CollectionMenuKind.New, null, "Add to a new collection…", null),
            new CollectionMenuChoice(CollectionMenuKind.Choose, null, "Add to another collection…", null),
        ], choices);
    }

    [Fact]
    public void Add_to_collection_leaves_out_Choose_while_every_collection_is_listed()
    {
        IReadOnlyList<CollectionInfo> all = [Collection(1, "Maps", usedDaysAgo: 1)];

        Assert.Equal(
        [
            new CollectionMenuChoice(CollectionMenuKind.Collection, 1, "Add to Maps", "Maps"),
            new CollectionMenuChoice(CollectionMenuKind.New, null, "Add to a new collection…", null),
        ], CollectionDirectory.MenuChoicesFor(all, _ => "Maps"));
        Assert.Equal([new CollectionMenuChoice(CollectionMenuKind.New, null, "Add to a new collection…", null)],
            CollectionDirectory.MenuChoicesFor([], _ => ""));
    }

    [Fact]
    public void Add_to_session_lists_the_current_binder_then_Choose_when_there_are_others_then_New()
    {
        Assert.Equal(
        [
            new SessionMenuChoice(SessionMenuKind.Session, 7, "Add to The midnight bell"),
            new SessionMenuChoice(SessionMenuKind.Choose, null, "Add to another binder…"),
            new SessionMenuChoice(SessionMenuKind.New, null, "Add to a new binder…"),
        ], SessionDirectory.MenuChoicesFor([Pack(7, "The midnight bell"), Pack(3, "House rules")]));
        Assert.Equal(
        [
            new SessionMenuChoice(SessionMenuKind.Session, 7, "Add to The midnight bell"),
            new SessionMenuChoice(SessionMenuKind.New, null, "Add to a new binder…"),
        ], SessionDirectory.MenuChoicesFor([Pack(7, "The midnight bell")]));
        Assert.Equal([new SessionMenuChoice(SessionMenuKind.New, null, "Add to a new binder…")], SessionDirectory.MenuChoicesFor([]));
    }
}
