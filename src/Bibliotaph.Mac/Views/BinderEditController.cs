using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Edit binder: its title and date, its notes, and its items in order by section, each moved up or down, opened or
/// removed, with sections added, renamed and deleted. Done goes back to the binder at the table.
/// </summary>
public sealed class BinderEditController(SessionPackViewModel pack) : NSViewController, IWindowTitle
{
    readonly Bindings _bindings = new();
    Bindings _rows = new();
    NSStackView _list = null!;

    /// <summary>The binder's name, with when it's for and how many items under it.</summary>
    public void FollowTitle(Bindings bindings, Action<string, string> show) =>
        bindings.Property(pack, nameof(SessionPackViewModel.Pack), () => show(pack.Title, string.Join(" · ", new[] { pack.Eyebrow, pack.CountLabel }.Where(t => t.Length > 0))));

    public override void LoadView()
    {
        var (root, content) = PageParts.ScrollingPage();
        var actions = NSStackView.FromViews([
            Command("Done", pack.DoneCommand),
            Command("Rename or date…", pack.EditCommand),
            Command("Add section…", pack.AddSectionCommand),
            Suggested(),
            Command("Find resources…", pack.FindResourcesCommand),
            Command("Show its books", pack.ShowBooksCommand),
            Command("Duplicate", pack.DuplicateCommand),
            Command("Delete binder…", pack.DeleteCommand),
        ]);
        actions.Spacing = 8;
        var notesLabel = PageParts.Section("Notes for the session");
        var notes = new NSTextField { PlaceholderString = "What to remember at the table" };
        notes.Changed += (_, _) => pack.Notes = notes.StringValue;
        notes.HeightAnchor.ConstraintEqualTo(60).Active = true;
        _list = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 6 };
        foreach (var view in new NSView[] { actions, notesLabel, notes, _list }) content.AddArrangedSubview(view);
        notes.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;
        _list.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;
        View = root;

        _bindings.Property(pack, nameof(SessionPackViewModel.Notes), () =>
        {
            if (notes.CurrentEditor is null) notes.StringValue = pack.Notes;
        });
        _bindings.Collection(pack.Rows, _ => Rebuild());
        Rebuild();
        DialogSheet.Follow(_bindings, pack.Actions, () => View.Window);
    }

    void Rebuild()
    {
        _rows.Dispose();
        _rows = new Bindings();
        foreach (var view in _list.ArrangedSubviews) view.RemoveFromSuperview();
        if (pack.Rows.Count == 0)
            _list.AddArrangedSubview(PageParts.Secondary("Nothing in this binder yet. Add pages from the reader, or books from the Library's menus."));
        foreach (var row in pack.Rows)
        {
            if (row is SessionSectionRow section)
            {
                var name = PageParts.Section(section.Name.ToUpperInvariant());
                var line = NSStackView.FromViews([name, new NSView(), Command("Rename…", pack.RenameSectionCommand, section), Command("Delete", pack.DeleteSectionCommand, section)]);
                _list.AddArrangedSubview(line);
                line.WidthAnchor.ConstraintEqualTo(_list.WidthAnchor).Active = true;
            }
            else if (row is SessionItemRow item)
            {
                var heading = NSTextField.CreateWrappingLabel($"{item.Number}  {item.Heading}");
                heading.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Medium)!;
                var where = NSTextField.CreateWrappingLabel(item.Reason ?? item.Where);
                where.TextColor = item.HasReason ? NSColor.SystemOrange : NSColor.SecondaryLabel;
                where.Font = NSFont.SystemFontOfSize(11)!;
                var text = NSStackView.FromViews([heading, where]);
                text.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
                text.Alignment = NSLayoutAttribute.Leading;
                text.Spacing = 1;
                text.SetContentHuggingPriorityForOrientation(200, NSLayoutConstraintOrientation.Horizontal);
                var line = NSStackView.FromViews([text, Command("↑", pack.MoveUpCommand, item), Command("↓", pack.MoveDownCommand, item),
                    Command("Open", pack.OpenItemCommand, item), Command("In a window", pack.OpenItemInNewWindowCommand, item),
                    Command("Remove", pack.RemoveItemCommand, item)]);
                line.Alignment = NSLayoutAttribute.CenterY;
                _list.AddArrangedSubview(line);
                line.WidthAnchor.ConstraintEqualTo(_list.WidthAnchor).Active = true;
            }
        }
    }

    /// <summary>The sections binders usually have, as Windows offers them: a menu of those, each added with a click.</summary>
    NSButton Suggested()
    {
        var button = NSButton.CreateButton("Suggested sections", () => { });
        button.ControlSize = NSControlSize.Small;
        button.Image = NSImage.GetSystemSymbol("chevron.down", null)!;
        button.ImagePosition = NSCellImagePosition.ImageTrailing;
        button.Activated += (_, _) =>
        {
            var menu = new NSMenu();
            foreach (var name in SessionPackViewModel.SuggestedSections)
                menu.AddItem(new NSMenuItem(name, (_, _) => pack.AddSuggestedSectionCommand.Execute(name)));
            menu.PopUpMenu(null, new CoreGraphics.CGPoint(0, button.Bounds.Height + 4), button);
        };
        return button;
    }

    NSButton Command(string title, System.Windows.Input.ICommand command, object? parameter = null)
    {
        var button = NSButton.CreateButton(title, () => { });
        button.ControlSize = NSControlSize.Small;
        (parameter is null ? _bindings : _rows).Command(button, command, parameter is null ? null : () => parameter);
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rows.Dispose();
            _bindings.Dispose();
        }
        base.Dispose(disposing);
    }
}
