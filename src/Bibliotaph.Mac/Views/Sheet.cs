using AppKit;
using CoreGraphics;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// A sheet of its own on a window, for the dialogs an alert can't be: ones that stay open to show a problem, or that
/// change as they're used. Its content is a column of views; the caller ends it when its view model closes.
/// </summary>
sealed class Sheet : IDisposable
{
    readonly NSWindow _window;

    public Sheet(nfloat width)
    {
        Content = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 10,
            EdgeInsets = new NSEdgeInsets(20, 22, 20, 22),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        Content.WidthAnchor.ConstraintEqualTo(width).Active = true;
        _window = new NSWindow(new CGRect(0, 0, width, 200), NSWindowStyle.Titled, NSBackingStore.Buffered, false);
        _window.ReleaseWhenClosed(false);
        var root = new NSView();
        root.AddSubview(Content);
        NSLayoutConstraint.ActivateConstraints(
        [
            Content.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            Content.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            Content.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            Content.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
        ]);
        _window.ContentView = root;
    }

    public NSStackView Content { get; }

    public NSWindow Window => _window;

    /// <summary>A line wide as the sheet, for text that wraps and rows that fill it.</summary>
    public void AddWide(NSView view)
    {
        Content.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(Content.WidthAnchor, 1, -44).Active = true;
    }

    public NSTextField Text(string? text, bool secondary = false, nfloat? size = null)
    {
        var label = NSTextField.CreateWrappingLabel(text ?? "");
        label.Font = NSFont.SystemFontOfSize(size ?? 13)!;
        if (secondary) label.TextColor = NSColor.SecondaryLabel;
        label.Hidden = string.IsNullOrEmpty(text);
        AddWide(label);
        return label;
    }

    /// <summary>The buttons along the bottom, the last the default.</summary>
    public NSStackView Buttons(params NSButton[] buttons)
    {
        var row = NSStackView.FromViews([new NSView(), .. buttons]);
        row.Spacing = 8;
        if (buttons is [.., var last]) last.KeyEquivalent = "\r";
        AddWide(row);
        return row;
    }

    public void Show(NSWindow host)
    {
        _window.SetContentSize(Content.FittingSize);
        host.BeginSheet(_window, _ => { });
    }

    /// <summary>After the content changed size.</summary>
    public void Fit() => _window.SetContentSize(Content.FittingSize);

    public void Close()
    {
        if (_window.SheetParent is { } host) host.EndSheet(_window);
        _window.OrderOut(null);
    }

    public void Dispose() => _window.Dispose();
}
