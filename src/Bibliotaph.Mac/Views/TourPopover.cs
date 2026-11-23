using AppKit;
using Bibliotaph.Core;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// A page whose title and subtitle in the toolbar say more than its name: the Library's heading and how many, Home's
/// totals. The page shows the rest at its top.
/// </summary>
interface IWindowTitle
{
    void FollowTitle(Bindings bindings, Action<string, string> show);
}

/// <summary>A page or pane that can point the tour at one of its controls.</summary>
interface ITourAnchors
{
    NSView? Anchor(TourAnchor anchor);
}

/// <summary>
/// The first-run tour, as a popover beside the control each step is about, as the Mac's own tips are: what it is,
/// Back and Next (or the step's own action), and Skip Tour. A step whose control isn't showing is passed over, as on
/// Windows.
/// </summary>
sealed class TourPopover : IDisposable
{
    readonly TourViewModel _tour;
    readonly Func<TourAnchor, NSView?> _find;
    readonly Bindings _bindings = new();
    NSPopover? _popover;

    /// <summary>The window the popover showing is drawn in, for a test copy's screenshot.</summary>
    public static NSWindow? Showing { get; private set; }

    public TourPopover(TourViewModel tour, Func<TourAnchor, NSView?> find)
    {
        _tour = tour;
        _find = find;
        EventHandler changed = (_, _) => Bindings.OnMain(ShowSoon);
        tour.StepChanged += changed;
        _bindings.Add(() => tour.StepChanged -= changed);
        _bindings.Property(tour, nameof(TourViewModel.IsOpen), () =>
        {
            if (!tour.IsOpen) Close();
            else ShowSoon();
        });
    }

    // After the page has laid out what the step points at.
    void ShowSoon() => NSApplication.SharedApplication.BeginInvokeOnMainThread(Show);

    void Show()
    {
        Close();
        if (_tour.Current is not { } step) return;
        var anchor = step.Anchors.Select(_find).FirstOrDefault(v => v is { Window: not null, IsHiddenOrHasHiddenAncestor: false });
        if (anchor is null)
        {
            _tour.PassOverAsync().FireAndForget();
            return;
        }

        var title = NSTextField.CreateLabel(step.Title);
        title.Font = NSFont.SystemFontOfSize(14, NSFontWeight.Semibold)!;
        var text = NSTextField.CreateWrappingLabel(step.Text);
        text.Font = NSFont.SystemFontOfSize(12)!;
        text.WidthAnchor.ConstraintEqualTo(300).Active = true;
        var count = NSTextField.CreateLabel(_tour.StepText);
        count.Font = NSFont.SystemFontOfSize(11)!;
        count.TextColor = NSColor.SecondaryLabel;
        var skip = NSButton.CreateButton("Skip Tour", () => _tour.SkipCommand.Execute(null));
        skip.Bordered = false;
        skip.ContentTintColor = NSColor.SecondaryLabel;
        skip.Font = NSFont.SystemFontOfSize(11)!;
        var back = NSButton.CreateButton("Back", () => _tour.BackCommand.Execute(null));
        back.Hidden = !_tour.CanGoBack;
        var next = NSButton.CreateButton(_tour.NextText, () => _tour.NextCommand.Execute(null));
        next.TintProminence = NSTintProminence.Primary;
        next.KeyEquivalent = "\r";
        var spacer = new NSView();
        spacer.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var buttons = NSStackView.FromViews([count, skip, spacer, back, next]);
        buttons.Spacing = 8;
        var content = NSStackView.FromViews([title, text, buttons]);
        content.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        content.Alignment = NSLayoutAttribute.Leading;
        content.Spacing = 8;
        content.EdgeInsets = new NSEdgeInsets(14, 16, 12, 16);
        buttons.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;
        content.SetFrameSize(content.FittingSize);

        var controller = new NSViewController { View = content };
        // It stays while the window is used, as the tour points at the window's own controls; Skip Tour or Esc ends it.
        _popover = new NSPopover { ContentViewController = controller, Behavior = NSPopoverBehavior.ApplicationDefined, Animates = true };
        _popover.Delegate = new Escape(_tour);
        var edge = anchor.Window!.ContentView is { } root && root.ConvertRectFromView(anchor.Bounds, anchor).GetMidX() < root.Bounds.Width / 3
            ? NSRectEdge.MaxXEdge
            : NSRectEdge.MinYEdge;
        _popover.Show(anchor.Bounds, anchor, edge);
        Showing = content.Window;
    }

    void Close()
    {
        if (_popover is not { } popover) return;
        _popover = null;
        Showing = null;
        popover.Delegate = null!;
        // Closed at once, and left to AppKit to let go of: freeing it while its closing animation runs crashes.
        popover.Animates = false;
        popover.Close();
    }

    public void Dispose()
    {
        _bindings.Dispose();
        // The window closes from the app's shutdown, off the main thread; the popover is AppKit's.
        Bindings.OnMain(Close);
    }

    /// <summary>Esc on the popover skips the tour, as it does on Windows.</summary>
    sealed class Escape(TourViewModel tour) : NSPopoverDelegate
    {
        public override bool ShouldClose(NSPopover popover)
        {
            if (tour.IsOpen) tour.SkipCommand.Execute(null);
            return true;
        }
    }
}
