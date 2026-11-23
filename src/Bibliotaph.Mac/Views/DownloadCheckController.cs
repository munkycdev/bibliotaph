using System.Collections.Specialized;
using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Check a download, from the File menu: which files in a folder or ZIP the library already has, grouped by what the
/// check found, each with the book it matched to open and the file to show in Finder. Nothing is copied or added.
/// </summary>
public sealed class DownloadCheckController(DownloadCheckViewModel check) : NSViewController
{
    readonly Bindings _bindings = new();
    readonly List<CheckGroupViewModel> _watched = [];
    NSStackView _groups = null!;
    bool _pending;

    public override void LoadView()
    {
        var (root, content) = PageParts.ScrollingPage();
        View = root;
        var subtitle = PageParts.Secondary(
            "See which files in a folder or ZIP you already have before you copy them anywhere. Nothing is copied, moved or added to your library.");
        var folder = Button("Check a Folder…", check.CheckFolderCommand);
        folder.TintProminence = NSTintProminence.Primary;
        var zip = Button("Check a ZIP…", check.CheckZipCommand);
        var stop = Button("Stop", check.StopCommand);
        var copy = Button("Copy List", check.CopyListCommand);
        copy.ToolTip = "The results as text, one file a line: verdict, file and the book it matched";
        var buttons = NSStackView.FromViews([folder, zip, stop, copy]);
        buttons.Spacing = 8;

        var target = NSTextField.CreateLabel("");
        target.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium)!;
        target.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        var progress = PageParts.Secondary("");
        var problem = NSTextField.CreateWrappingLabel("");
        problem.TextColor = NSColor.SystemOrange;
        var copied = PageParts.Secondary("");
        var status = NSStackView.FromViews([target, progress, problem, copied]);
        status.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        status.Alignment = NSLayoutAttribute.Leading;
        status.Spacing = 4;

        _groups = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 24 };
        foreach (var view in new NSView[] { PageParts.Heading("Check a Download"), subtitle, buttons, status, _groups }) content.AddArrangedSubview(view);
        content.SetCustomSpacing(18, subtitle);
        foreach (var view in new NSView[] { subtitle, status, _groups }) view.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;

        _bindings.Properties(check, [nameof(DownloadCheckViewModel.Target), nameof(DownloadCheckViewModel.IsRunning), nameof(DownloadCheckViewModel.Progress),
            nameof(DownloadCheckViewModel.Problem), nameof(DownloadCheckViewModel.Copied)], () =>
        {
            status.Hidden = !check.HasTarget;
            target.StringValue = check.Target ?? "";
            target.ToolTip = check.Target ?? "";
            progress.StringValue = check.Progress;
            problem.StringValue = check.Problem ?? "";
            problem.Hidden = !check.HasProblem;
            copied.StringValue = check.Copied ?? "";
            copied.Hidden = check.Copied is null;
            stop.Hidden = !check.IsRunning;
        });
        _bindings.Collection(check.Groups, _ => Changed());
        _bindings.Add(() =>
        {
            foreach (var group in _watched) group.Items.CollectionChanged -= OnItems;
        });
        Show();
    }

    void OnItems(object? sender, NotifyCollectionChangedEventArgs e) => Bindings.OnMain(Changed);

    // Files arrive one by one as the check goes; they're shown together a moment later, not one rebuild each.
    void Changed()
    {
        if (_pending) return;
        _pending = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _pending = false;
            Show();
        });
    }

    void Show()
    {
        foreach (var group in _watched) group.Items.CollectionChanged -= OnItems;
        _watched.Clear();
        foreach (var view in _groups.ArrangedSubviews) view.RemoveFromSuperview();
        foreach (var group in check.Groups)
        {
            group.Items.CollectionChanged += OnItems;
            _watched.Add(group);
            var heading = NSTextField.CreateLabel(group.Heading);
            heading.Font = NSFont.SystemFontOfSize(17, NSFontWeight.Semibold)!;
            var rows = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 0 };
            var section = NSStackView.FromViews([heading, PageParts.Secondary(group.Explanation), rows]);
            section.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
            section.Alignment = NSLayoutAttribute.Leading;
            section.Spacing = 6;
            _groups.AddArrangedSubview(section);
            section.WidthAnchor.ConstraintEqualTo(_groups.WidthAnchor).Active = true;
            rows.WidthAnchor.ConstraintEqualTo(section.WidthAnchor).Active = true;
            foreach (var item in group.Items)
            {
                var row = Row(item);
                rows.AddArrangedSubview(row);
                row.WidthAnchor.ConstraintEqualTo(rows.WidthAnchor).Active = true;
            }
        }
    }

    /// <summary>A file: its name, where it is, what the check found, and Open the Book and Show in Finder beside it.</summary>
    static NSStackView Row(CheckResultViewModel item)
    {
        var name = NSTextField.CreateLabel(item.Name);
        name.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var location = NSTextField.CreateLabel(item.Location);
        location.Font = NSFont.SystemFontOfSize(11)!;
        location.TextColor = NSColor.SecondaryLabel;
        location.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        location.ToolTip = item.Location;
        var text = NSStackView.FromViews([name, location]);
        text.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        text.Alignment = NSLayoutAttribute.Leading;
        text.Spacing = 2;
        if (item.HasDetail) text.AddArrangedSubview(PageParts.Secondary(item.Detail));
        foreach (var view in text.ArrangedSubviews) view.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        text.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);

        var links = new NSStackView { Spacing = 14, Alignment = NSLayoutAttribute.Top };
        if (item.CanOpen) links.AddArrangedSubview(Link("Open the Book", () => item.OpenBookCommand.Execute(null)));
        links.AddArrangedSubview(Link("Show in Finder", () => item.ShowInFolderCommand.Execute(null)));
        var row = NSStackView.FromViews([text, links]);
        row.Alignment = NSLayoutAttribute.Top;
        row.Distribution = NSStackViewDistribution.Fill;
        row.EdgeInsets = new NSEdgeInsets(9, 0, 9, 0);
        var line = new NSBox { BoxType = NSBoxType.NSBoxSeparator };
        var block = NSStackView.FromViews([line, row]);
        block.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        block.Spacing = 0;
        block.Alignment = NSLayoutAttribute.Width;
        return block;
    }

    static NSButton Link(string title, Action open)
    {
        var link = NSButton.CreateButton(title, open);
        link.Bordered = false;
        link.ContentTintColor = NSColor.ControlAccent;
        link.Font = NSFont.SystemFontOfSize(12)!;
        return link;
    }

    NSButton Button(string title, System.Windows.Input.ICommand command)
    {
        var button = NSButton.CreateButton(title, () => { });
        _bindings.Command(button, command);
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }
}
