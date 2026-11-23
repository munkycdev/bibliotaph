using AppKit;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// The window's content: the view for the page the shell shows. A page the Mac has no view for yet says so and
/// offers the way back to the Library, so a link from a shared view model never leaves the window empty.
/// </summary>
public sealed class PageHost(MacCovers covers, INavigationService navigation, IServiceProvider services) : NSViewController
{
    NSViewController? _current;

    public override void LoadView() => View = new NSView();

    /// <summary>The page showing, for its title in the window.</summary>
    public NSViewController? Current => _current;

    /// <summary>What the tour points at on the page showing.</summary>
    public NSView? Anchor(Core.TourAnchor anchor) => (_current as ITourAnchors)?.Anchor(anchor);

    public void Show(PageViewModel? page)
    {
        var next = page switch
        {
            HomeViewModel home => new HomeController(home, covers),
            LibraryViewModel library => new LibraryController(library, covers),
            ViewerViewModel reader => new ReaderController(reader, navigation),
            NeedsReviewViewModel review => new ReviewController(review, covers),
            CollectionsViewModel collections => new CollectionsController(collections, covers),
            SessionsViewModel binders => new BindersController(binders, covers),
            SessionReaderViewModel binder => new BinderController(binder),
            SessionPackViewModel pack => new BinderEditController(pack),
            SummaryViewModel numbers => new SummaryController(numbers),
            AiReadingViewModel reading => new AiReadingController(reading),
            DownloadCheckViewModel download => new DownloadCheckController(download),
            OnlineReaderViewModel online => OnlineReaderController.For(online, services),
            null => null,
            _ => (NSViewController)new NotYetController(page, navigation),
        };
        if (_current is not null)
        {
            _current.View.RemoveFromSuperview();
            _current.RemoveFromParentViewController();
            _current.Dispose();
        }
        _current = next;
        if (next is null) return;
        AddChildViewController(next);
        var view = next.View;
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        View.AddSubview(view);
        NSLayoutConstraint.ActivateConstraints(
        [
            view.TopAnchor.ConstraintEqualTo(View.TopAnchor),
            view.BottomAnchor.ConstraintEqualTo(View.BottomAnchor),
            view.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
            view.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
        ]);
    }
}

/// <summary>A page that comes to the Mac later.</summary>
sealed class NotYetController(PageViewModel page, INavigationService navigation) : NSViewController
{
    public override void LoadView()
    {
        var title = NSTextField.CreateLabel(page.Title.Length > 0 ? page.Title : page.Route.ToString());
        title.Font = NSFont.SystemFontOfSize(20, NSFontWeight.Semibold)!;
        var body = NSTextField.CreateWrappingLabel("This page isn't on the Mac yet. Your library is.");
        body.TextColor = NSColor.SecondaryLabel;
        var back = NSButton.CreateButton("Show the Library", () => navigation.NavigateTo(Route.Library));
        var stack = NSStackView.FromViews([title, body, back]);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Spacing = 12;
        var root = new NSView();
        stack.TranslatesAutoresizingMaskIntoConstraints = false;
        root.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.CenterXAnchor.ConstraintEqualTo(root.CenterXAnchor),
            stack.CenterYAnchor.ConstraintEqualTo(root.CenterYAnchor),
        ]);
        View = root;
    }
}
