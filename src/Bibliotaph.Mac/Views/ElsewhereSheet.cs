using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// "Add a Book I Own Elsewhere", as a sheet on the Library's window while its view model is open: in print or on a
/// virtual tabletop, a card with no file that search finds with the rest. Its details open once it's added.
/// </summary>
static class ElsewhereSheet
{
    public static void Follow(Bindings bindings, LibraryViewModel library, Func<NSWindow?> window)
    {
        (Sheet Sheet, Bindings Bindings)? showing = null;
        void Close()
        {
            if (showing is not { } old) return;
            old.Bindings.Dispose();
            old.Sheet.Close();
            old.Sheet.Dispose();
            showing = null;
        }
        bindings.Property(library, nameof(LibraryViewModel.AddElsewhereDialog), () =>
        {
            Close();
            if (library.AddElsewhereDialog is { } dialog && window() is { } host) showing = Show(dialog, host);
        });
        bindings.Add(Close);
    }

    static (Sheet, Bindings) Show(AddElsewhereViewModel dialog, NSWindow host)
    {
        var sheet = new Sheet(480);
        var bindings = new Bindings();
        sheet.Text("Add a Book I Own Elsewhere", size: 17).Font = NSFont.SystemFontOfSize(17, NSFontWeight.Semibold)!;
        sheet.Text("In print or on a virtual tabletop. It gets a card with no file, so search finds it with the rest.", secondary: true);

        var title = new NSTextField { PlaceholderString = "Required", StringValue = dialog.Title };
        title.Changed += (_, _) => dialog.Title = title.StringValue;
        Field(sheet, "Title", title);
        var system = FormText.Choices(dialog.Systems.Select(c => c.Label), at =>
        {
            if (at >= 0 && at < dialog.Systems.Count) dialog.System = dialog.Systems[at];
        });
        system.SelectItem(IndexOf(dialog.Systems, dialog.System));
        var type = FormText.Choices(dialog.Types.Select(c => c.Label), at =>
        {
            if (at >= 0 && at < dialog.Types.Count) dialog.Type = dialog.Types[at];
        });
        type.SelectItem(IndexOf(dialog.Types, dialog.Type));
        var choices = NSStackView.FromViews([Labelled("Game system", system), Labelled("Type", type)]);
        choices.Distribution = NSStackViewDistribution.FillEqually;
        choices.Spacing = 12;
        sheet.AddWide(choices);
        var publisher = new NSTextField { StringValue = dialog.Publisher };
        publisher.Changed += (_, _) => dialog.Publisher = publisher.StringValue;
        Field(sheet, "Publisher", publisher);

        var owns = new WrapView { Gap = 10 };
        owns.SetItems(dialog.Owns.Select(option =>
        {
            var box = NSButton.CreateCheckbox(option.Label, () => { });
            box.State = option.IsChecked ? NSCellStateValue.On : NSCellStateValue.Off;
            box.Activated += (_, _) => option.IsChecked = box.State == NSCellStateValue.On;
            box.SetFrameSize(box.FittingSize);
            return (NSView)box;
        }));
        var other = new NSTextField { PlaceholderString = "Somewhere else, or the book's link online", StringValue = dialog.OtherOwn };
        other.Changed += (_, _) => dialog.OtherOwn = other.StringValue;
        sheet.Content.AddArrangedSubview(Label("Also own"));
        sheet.AddWide(owns);
        sheet.AddWide(other);
        sheet.Text("For example: D&D Beyond https://…", secondary: true, size: 11);

        if (dialog.CanChooseCover)
        {
            var choose = NSButton.CreateButton("Choose a Cover Image…", () => { });
            bindings.Command(choose, dialog.ChooseCoverCommand);
            var chosen = NSTextField.CreateLabel("");
            chosen.TextColor = NSColor.SecondaryLabel;
            chosen.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
            var remove = NSButton.CreateButton("Remove", () => { });
            remove.Bordered = false;
            remove.ContentTintColor = NSColor.ControlAccent;
            bindings.Command(remove, dialog.ClearCoverCommand);
            var row = NSStackView.FromViews([choose, chosen, remove]);
            row.Spacing = 8;
            sheet.Content.AddArrangedSubview(Label("Cover"));
            sheet.AddWide(row);
            bindings.Property(dialog, nameof(AddElsewhereViewModel.CoverName), () =>
            {
                chosen.StringValue = dialog.CoverName ?? "";
                chosen.Hidden = remove.Hidden = !dialog.HasCover;
                choose.Hidden = dialog.HasCover;
            });
        }

        var problem = sheet.Text("");
        problem.TextColor = NSColor.SystemOrange;
        bindings.Property(dialog, nameof(AddElsewhereViewModel.Problem), () =>
        {
            problem.StringValue = dialog.Problem ?? "";
            problem.Hidden = dialog.Problem is null;
            sheet.Fit();
        });
        var cancel = NSButton.CreateButton("Cancel", () => { });
        cancel.KeyEquivalent = "\u001b";
        bindings.Command(cancel, dialog.CancelCommand);
        var save = NSButton.CreateButton("Add to Library", () => { });
        bindings.Command(save, dialog.SaveCommand);
        sheet.Buttons(cancel, save);
        sheet.Show(host);
        Bindings.OnMain(() => sheet.Window.MakeFirstResponder(title));
        return (sheet, bindings);
    }

    static void Field(Sheet sheet, string label, NSView field) => sheet.AddWide(Labelled(label, field));

    static NSStackView Labelled(string label, NSView field)
    {
        var column = NSStackView.FromViews([Label(label), field]);
        column.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        column.Alignment = NSLayoutAttribute.Leading;
        column.Spacing = 4;
        field.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        return column;
    }

    static NSTextField Label(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium)!;
        return label;
    }

    static nint IndexOf<T>(IReadOnlyList<Choice<T>> list, Choice<T> choice)
    {
        for (var i = 0; i < list.Count; i++)
            if (Equals(list[i], choice)) return i;
        return 0;
    }
}
