using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Views;

/// <summary>Collections: a card for each, which opens the Library on it, and New collection.</summary>
public sealed class CollectionsController(CollectionsViewModel collections, MacCovers covers) : NSViewController
{
    readonly Bindings _bindings = new();

    public override void LoadView()
    {
        var (root, content) = PageParts.ScrollingPage();
        var add = NSButton.CreateButton("New collection…", () => { });
        _bindings.Command(add, collections.NewCollectionCommand);
        var top = NSStackView.FromViews([PageParts.Heading("Collections"), new NSView(), add]);
        var empty = PageParts.Secondary("No collections yet. A collection gathers books from anywhere in your library, such as everything for one campaign.");
        var grid = PageParts.Wrap();
        foreach (var view in new NSView[] { top, empty, grid }) content.AddArrangedSubview(view);
        top.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;
        grid.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;
        View = root;

        void Show()
        {
            empty.Hidden = !collections.IsLoaded || collections.HasCollections;
            grid.SetItems(collections.Cards.Select(card => new ShelfCard(card.Name, card.CountLabel, card.Covers, covers,
                () => collections.OpenCollectionCommand.Execute(card), card.Badge)));
        }
        _bindings.Collection(collections.Cards, _ => Show());
        _bindings.Properties(collections, [nameof(CollectionsViewModel.IsLoaded), nameof(CollectionsViewModel.HasCollections)], Show);
        DialogSheet.Follow(_bindings, collections.Actions, () => View.Window);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Binders, newest first: those coming up, those kept at hand, those with no date, and earlier ones folded away.</summary>
public sealed class BindersController(SessionsViewModel binders, MacCovers covers) : NSViewController
{
    readonly Bindings _bindings = new();

    public override void LoadView()
    {
        var (root, content) = PageParts.ScrollingPage();
        var add = NSButton.CreateButton("New binder…", () => { });
        _bindings.Command(add, binders.NewSessionCommand);
        var top = NSStackView.FromViews([PageParts.Heading("Binders"), new NSView(), add]);
        var empty = PageParts.Secondary("No binders yet. A binder holds the pages you'll want at the table for a session: rules, maps, handouts.");
        var upcoming = Group("Coming up", binders.Upcoming);
        var atHand = Group("At hand", binders.AtHand);
        var undated = Group("No date", binders.Undated);
        var earlierToggle = NSButton.CreateButton("", () => { });
        earlierToggle.BezelStyle = NSBezelStyle.Inline;
        _bindings.Command(earlierToggle, binders.ToggleEarlierCommand);
        var earlier = Group(null, binders.Earlier);
        foreach (var view in new NSView[] { top, empty, upcoming, atHand, undated, earlierToggle, earlier }) content.AddArrangedSubview(view);
        foreach (var view in new NSView[] { top, upcoming, atHand, undated, earlier }) view.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;
        View = root;

        _bindings.Properties(binders, [nameof(SessionsViewModel.IsLoaded), nameof(SessionsViewModel.Version), nameof(SessionsViewModel.ShowEarlier)], () =>
        {
            empty.Hidden = !binders.ShowEmpty;
            upcoming.Hidden = !binders.HasUpcoming;
            atHand.Hidden = !binders.HasAtHand;
            undated.Hidden = !binders.HasUndated;
            earlierToggle.Hidden = !binders.HasEarlier;
            earlierToggle.Title = binders.EarlierLabel;
            earlier.Hidden = !binders.HasEarlier || !binders.ShowEarlier;
        });
        DialogSheet.Follow(_bindings, binders.Actions, () => View.Window);
    }

    NSStackView Group(string? title, System.Collections.ObjectModel.ObservableCollection<SessionCardViewModel> cards)
    {
        var grid = PageParts.Wrap();
        var group = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 8 };
        if (title is not null) group.AddArrangedSubview(PageParts.Section(title));
        group.AddArrangedSubview(grid);
        grid.WidthAnchor.ConstraintEqualTo(group.WidthAnchor).Active = true;
        void Show() => grid.SetItems(cards.Select(card => new ShelfCard(card.Title, $"{card.DateLabel} · {card.CountLabel}", card.Covers, covers,
            () => binders.OpenSessionCommand.Execute(card))));
        _bindings.Collection(cards, _ => Show());
        Show();
        return group;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }
}
