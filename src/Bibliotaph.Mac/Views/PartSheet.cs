using System.ComponentModel;
using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// An adventure's pages, typed as the file numbers them (and its title when it's new), as a sheet over the window while
/// the details or the reader have one open: Add Adventure, Edit Pages, and the reader's Mark as Adventure.
/// </summary>
static class PartSheet
{
    public static void Follow(Bindings owner, INotifyPropertyChanged source, string property, Func<PartEditorViewModel?> current, Func<NSWindow?> window)
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
        owner.Property(source, property, () =>
        {
            Close();
            if (current() is not { } editor || window() is not { } host) return;
            showing = Show(editor, host);
        });
        owner.Add(Close);
    }

    static (Sheet, Bindings) Show(PartEditorViewModel editor, NSWindow host)
    {
        var sheet = new Sheet(420);
        var bindings = new Bindings();
        sheet.Text(editor.Heading, size: 15).Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold)!;
        if (editor.ShowsTitle)
        {
            var title = new NSTextField { PlaceholderString = "The adventure's title", StringValue = editor.Title };
            title.Changed += (_, _) => editor.Title = title.StringValue;
            sheet.AddWide(title);
        }
        var from = new NSTextField { PlaceholderString = "From page", StringValue = editor.From };
        from.Changed += (_, _) => editor.From = from.StringValue;
        var to = new NSTextField { PlaceholderString = "To page", StringValue = editor.To };
        to.Changed += (_, _) => editor.To = to.StringValue;
        var pages = Row(Label("Pages", 13), from, Label("to", 13), to);
        from.WidthAnchor.ConstraintEqualTo(90).Active = true;
        to.WidthAnchor.ConstraintEqualTo(90).Active = true;
        sheet.Content.AddArrangedSubview(pages);
        var hint = sheet.Text(Bibliotaph.Processing.DetailsText.PagesHint, secondary: true, size: 11);
        var problem = sheet.Text("");
        problem.TextColor = NSColor.SystemOrange;
        bindings.Properties(editor, [nameof(PartEditorViewModel.Problem), nameof(PartEditorViewModel.From), nameof(PartEditorViewModel.To)], () =>
        {
            problem.StringValue = editor.Problem ?? "";
            problem.Hidden = editor.Problem is null;
            hint.TextColor = editor.PagesProblem is null ? NSColor.SecondaryLabel : NSColor.SystemOrange;
            sheet.Fit();
        });
        var cancel = NSButton.CreateButton("Cancel", () => { });
        cancel.KeyEquivalent = "\u001b";
        bindings.Command(cancel, editor.CancelCommand);
        var save = NSButton.CreateButton(editor.ActionText, () => { });
        bindings.Command(save, editor.SaveCommand);
        sheet.Buttons(cancel, save);
        sheet.Show(host);
        return (sheet, bindings);
    }

    static NSTextField Label(string text, nfloat size)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.Font = NSFont.SystemFontOfSize(size)!;
        return label;
    }

    static NSStackView Row(params NSView[] views)
    {
        var row = NSStackView.FromViews(views);
        row.Spacing = 8;
        return row;
    }
}
