using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Organize mode's dialogs, as sheets on the Library's window: Same book, Pack together and the bulk editor, each
/// shown while its view model is open and gone when it closes.
/// </summary>
static class OrganizeSheets
{
    public static void Follow(Bindings bindings, LibrarySelectionViewModel selection, Func<NSWindow?> window)
    {
        (Sheet Sheet, Bindings Bindings)? sameBook = null, pack = null, bulk = null;
        bindings.Property(selection, nameof(LibrarySelectionViewModel.SameBookDialog), () =>
            Swap(ref sameBook, selection.SameBookDialog is { } dialog && window() is { } host ? SameBook(dialog, host) : null));
        bindings.Property(selection, nameof(LibrarySelectionViewModel.PackDialog), () =>
            Swap(ref pack, selection.PackDialog is { } dialog && window() is { } host ? Pack(dialog, host) : null));
        bindings.Property(selection, nameof(LibrarySelectionViewModel.BulkEdit), () =>
            Swap(ref bulk, selection.BulkEdit is { } editor && window() is { } host ? BulkEditSheet.Show(editor, host) : null));
        bindings.Add(() =>
        {
            Swap(ref sameBook, null);
            Swap(ref pack, null);
            Swap(ref bulk, null);
        });
    }

    static void Swap(ref (Sheet Sheet, Bindings Bindings)? showing, (Sheet Sheet, Bindings Bindings)? next)
    {
        if (showing is { } old)
        {
            old.Bindings.Dispose();
            old.Sheet.Close();
            old.Sheet.Dispose();
        }
        showing = next;
    }

    static (Sheet, Bindings) SameBook(SameBookDialogViewModel dialog, NSWindow host)
    {
        var sheet = new Sheet(500);
        var bindings = new Bindings();
        sheet.Text(dialog.Heading, size: 17).Font = NSFont.SystemFontOfSize(17, NSFontWeight.Semibold)!;
        sheet.Text("Copies and versions of one book share a card. Nothing on disk changes.", secondary: true);
        sheet.Text(dialog.Elsewhere, secondary: true);
        sheet.Content.AddArrangedSubview(PageParts.Section("Which File Opens"));
        foreach (var file in dialog.Files)
        {
            var radio = NSButton.CreateRadioButton(file.Name, () => file.Opens = true);
            bindings.Property(file, nameof(SameBookFileViewModel.Opens), () => radio.State = file.Opens ? NSCellStateValue.On : NSCellStateValue.Off);
            var detail = NSTextField.CreateLabel($"{file.Card} · {file.Detail}");
            detail.Font = NSFont.SystemFontOfSize(11)!;
            detail.TextColor = NSColor.SecondaryLabel;
            detail.LineBreakMode = NSLineBreakMode.TruncatingTail;
            var line = NSStackView.FromViews([radio, detail]);
            line.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
            line.Alignment = NSLayoutAttribute.Leading;
            line.Spacing = 1;
            sheet.AddWide(line);
        }
        sheet.Text("Where the cards set different values, such as two titles, the card keeps one and Needs review asks about the other.", secondary: true, size: 11);
        var problem = sheet.Text("");
        problem.TextColor = NSColor.SystemOrange;
        bindings.Property(dialog, nameof(SameBookDialogViewModel.Problem), () =>
        {
            problem.StringValue = dialog.Problem ?? "";
            problem.Hidden = dialog.Problem is null;
            sheet.Fit();
        });
        var cancel = NSButton.CreateButton("Cancel", () => { });
        cancel.KeyEquivalent = "\u001b";
        bindings.Command(cancel, dialog.CancelCommand);
        var join = NSButton.CreateButton("Join as one book", () => { });
        bindings.Command(join, dialog.JoinCommand);
        sheet.Buttons(cancel, join);
        sheet.Show(host);
        return (sheet, bindings);
    }

    static (Sheet, Bindings) Pack(PackTogetherDialogViewModel dialog, NSWindow host)
    {
        var sheet = new Sheet(440);
        var bindings = new Bindings();
        sheet.Content.AddArrangedSubview(PageParts.Section(dialog.Eyebrow));
        sheet.Text(dialog.Heading).Font = NSFont.SystemFontOfSize(17, NSFontWeight.Semibold)!;
        sheet.Text(dialog.Explanation, secondary: true);
        sheet.Content.AddArrangedSubview(PageParts.Section("Name"));
        var name = new NSTextField { StringValue = dialog.Name };
        name.Changed += (_, _) => dialog.Name = name.StringValue;
        sheet.AddWide(name);
        var problem = sheet.Text("");
        problem.TextColor = NSColor.SystemOrange;
        bindings.Property(dialog, nameof(PackTogetherDialogViewModel.Problem), () =>
        {
            problem.StringValue = dialog.Problem ?? "";
            problem.Hidden = dialog.Problem is null;
            sheet.Fit();
        });
        var cancel = NSButton.CreateButton("Cancel", () => { });
        cancel.KeyEquivalent = "\u001b";
        bindings.Command(cancel, dialog.CancelCommand);
        var packButton = NSButton.CreateButton("Pack together", () => { });
        bindings.Command(packButton, dialog.PackCommand);
        sheet.Buttons(cancel, packButton);
        sheet.Window.InitialFirstResponder = name;
        sheet.Show(host);
        return (sheet, bindings);
    }
}
