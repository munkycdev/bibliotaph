using AppKit;
using CoreGraphics;

namespace Bibliotaph.MacSpike;

/// <summary>The selected book: its cover, title and where it is. The real details panel is the Windows inspector's.</summary>
sealed class InspectorController(LibraryViewModel library, Covers covers) : NSViewController
{
    public override void LoadView()
    {
        var cover = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown };
        var title = NSTextField.CreateWrappingLabel("");
        title.Font = NSFont.SystemFontOfSize(20, NSFontWeight.Bold);
        var kind = NSTextField.CreateLabel("");
        kind.TextColor = NSColor.SecondaryLabel;
        // Built once and only retexted: NSGridView.RemoveRow leaves the row's views on screen.
        var (folder, where, path, modified, content) = (Value(), Value(), Value(), Value(), Value());
        var grid = NSGridView.Create(
        [
            [Name("Folder"), folder],
            [Name("Where"), where],
            [Name("Path"), path],
            [Name("Modified"), modified],
            [Name("Content"), content],
        ]);
        grid.RowSpacing = 8;
        grid.ColumnSpacing = 10;
        var empty = NSTextField.CreateLabel("Select a book");
        empty.TextColor = NSColor.TertiaryLabel;
        empty.Font = NSFont.SystemFontOfSize(15);

        var stack = NSStackView.FromViews([cover, title, kind, grid]);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.Spacing = 10;
        stack.EdgeInsets = new NSEdgeInsets(56, 18, 18, 18);

        var root = new NSView();
        foreach (var view in new NSView[] { stack, empty })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            stack.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            stack.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            cover.HeightAnchor.ConstraintEqualTo(300),
            cover.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -36),
            title.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -36),
            empty.CenterXAnchor.ConstraintEqualTo(root.CenterXAnchor),
            empty.CenterYAnchor.ConstraintEqualTo(root.CenterYAnchor),
        ]);
        View = root;

        Ui.Watch(library, nameof(LibraryViewModel.SelectedBook), async () =>
        {
            var book = library.SelectedBook;
            stack.Hidden = book is null;
            empty.Hidden = book is not null;
            if (book is null) return;
            title.StringValue = book.Title;
            kind.StringValue = $"{book.Kind} · {book.SizeBytes / 1024.0 / 1024.0:N1} MB";
            folder.StringValue = book.FolderName;
            where.StringValue = book.Location;
            path.StringValue = book.Path;
            modified.StringValue = book.ModifiedUtc.ToLocalTime().ToString("d MMM yyyy");
            content.StringValue = book.ContentHash[..12] + "…";
            cover.Image = covers.Cached(book.ContentHash)!;
            if (cover.Image is not null) return;
            var image = await covers.GetAsync(book.ContentHash);
            Ui.OnMain(() =>
            {
                if (library.SelectedBook == book) cover.Image = image!;
            });
        });
    }

    static NSTextField Name(string name)
    {
        var label = NSTextField.CreateLabel(name);
        label.TextColor = NSColor.SecondaryLabel;
        return label;
    }

    static NSTextField Value()
    {
        var text = NSTextField.CreateWrappingLabel("");
        text.PreferredMaxLayoutWidth = 200;
        text.Selectable = true;
        return text;
    }
}
