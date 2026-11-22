using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Foundation;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// A clickable card for a collection or a binder: the covers of up to two of its books, overlapped like a stack, its
/// name and what it holds. Collections and Binders lay these out in a wrapping grid.
/// </summary>
sealed class ShelfCard : NSView
{
    readonly Action _open;

    public ShelfCard(string title, string detail, IReadOnlyList<LibraryItemViewModel> covers, MacCovers images, Action open, string? badge = null)
    {
        _open = open;
        WantsLayer = true;
        var back = Cover(covers.Count > 1 ? covers[1] : null, images);
        var front = Cover(covers.Count > 0 ? covers[0] : null, images);
        back.AlphaValue = covers.Count > 1 ? 0.75f : 0;
        var name = NSTextField.CreateLabel(title);
        name.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var meta = NSTextField.CreateWrappingLabel(badge is null ? detail : $"{detail} · {badge}");
        meta.Font = NSFont.SystemFontOfSize(11)!;
        meta.TextColor = NSColor.SecondaryLabel;
        meta.MaximumNumberOfLines = 2;
        meta.PreferredMaxLayoutWidth = 190;
        foreach (var view in new NSView[] { back, front, name, meta })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            WidthAnchor.ConstraintEqualTo(190),
            back.TopAnchor.ConstraintEqualTo(TopAnchor),
            back.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 40),
            back.WidthAnchor.ConstraintEqualTo(130),
            back.HeightAnchor.ConstraintEqualTo(174),
            front.TopAnchor.ConstraintEqualTo(TopAnchor, 14),
            front.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 10),
            front.WidthAnchor.ConstraintEqualTo(130),
            front.HeightAnchor.ConstraintEqualTo(174),
            name.TopAnchor.ConstraintEqualTo(front.BottomAnchor, 10),
            name.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            name.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            meta.TopAnchor.ConstraintEqualTo(name.BottomAnchor, 2),
            meta.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            meta.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            meta.BottomAnchor.ConstraintEqualTo(BottomAnchor),
        ]);
        ToolTip = title;
    }

    static NSImageView Cover(LibraryItemViewModel? book, MacCovers images)
    {
        var view = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, WantsLayer = true };
        view.Layer!.CornerRadius = 5;
        view.Layer.MasksToBounds = true;
        view.Layer.BackgroundColor = NSColor.QuaternarySystemFill.CGColor;
        if (book?.CoverName is { } name)
        {
            view.Image = images.TryGet(name)!;
            if (view.Image is null) _ = LoadAsync(view, name, images);
        }
        return view;
    }

    static async Task LoadAsync(NSImageView view, string name, MacCovers images)
    {
        var image = await images.LoadAsync(name);
        Bindings.OnMain(() => view.Image = image!);
    }

    public override void MouseUp(NSEvent theEvent)
    {
        if (Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) _open();
    }

    public override void ResetCursorRects() => AddCursorRect(Bounds, NSCursor.PointingHandCursor);
}

/// <summary>Views every shelf page builds the same way.</summary>
static class PageParts
{
    public static NSTextField Heading(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(22, NSFontWeight.Bold)!;
        return label;
    }

    public static NSTextField Section(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        label.TextColor = NSColor.SecondaryLabel;
        return label;
    }

    public static NSTextField Secondary(string text)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.TextColor = NSColor.SecondaryLabel;
        return label;
    }

    /// <summary>Cards side by side, as many to a row as fit; the rows are rebuilt when the page's width changes.</summary>
    public static WrapView Wrap() => new();

    /// <summary>A page that scrolls: its parts one under another from the top.</summary>
    public static (NSView Root, NSStackView Content) ScrollingPage()
    {
        var content = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 14,
            EdgeInsets = new NSEdgeInsets(16, 24, 24, 24),
        };
        var document = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        document.AddSubview(content);
        var scroll = new NSScrollView { HasVerticalScroller = true, DrawsBackground = false, DocumentView = document };
        var root = new NSView();
        scroll.TranslatesAutoresizingMaskIntoConstraints = false;
        root.AddSubview(scroll);
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            content.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            content.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            content.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
            document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
            scroll.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
        ]);
        return (root, content);
    }

    sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}

/// <summary>Lays its views out left to right, wrapping to a new row when the next wouldn't fit.</summary>
sealed class WrapView : NSView
{
    const double Gap = 22;
    readonly List<NSView> _items = [];
    NSLayoutConstraint? _height;

    public WrapView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
    }

    public override bool IsFlipped => true;

    public void SetItems(IEnumerable<NSView> items)
    {
        foreach (var item in _items) item.RemoveFromSuperview();
        _items.Clear();
        foreach (var item in items)
        {
            item.TranslatesAutoresizingMaskIntoConstraints = true;
            _items.Add(item);
            AddSubview(item);
        }
        NeedsLayout = true;
    }

    public override void Layout()
    {
        base.Layout();
        double x = 0, y = 0, row = 0;
        foreach (var item in _items)
        {
            var size = item.FittingSize;
            if (x > 0 && x + size.Width > Bounds.Width)
            {
                x = 0;
                y += row + Gap;
                row = 0;
            }
            item.Frame = new CGRect(x, y, size.Width, size.Height);
            x += size.Width + Gap;
            row = Math.Max(row, size.Height);
        }
        var height = _items.Count == 0 ? 0 : y + row;
        if (_height is null)
        {
            _height = HeightAnchor.ConstraintEqualTo((nfloat)height);
            _height.Active = true;
        }
        else if (Math.Abs(_height.Constant - height) > 0.5) _height.Constant = (nfloat)height;
    }
}
