using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Views;

/// <summary>Send to Nornis, for the reader's pages and a binder's items alike.</summary>
static class NornisSheet
{
    /// <summary>Send to Nornis's pages, label and world, as a sheet over the reader or the binder while it's open.</summary>
    public static void Follow(Bindings owner, System.ComponentModel.INotifyPropertyChanged source, string property, Func<SendToNornisDialogViewModel?> current,
        Func<NSWindow?> window)
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
            if (current() is not { } dialog || window() is not { } host) return;
            var sheet = new Sheet(440);
            var bindings = new Bindings();
            sheet.Text("Send to Nornis", size: 17).Font = NSFont.SystemFontOfSize(17, NSFontWeight.Semibold)!;
            sheet.Text("Nornis files these pages as an excerpt in the world, once you confirm there.", secondary: true);
            NSTextField Box(string placeholder, string value, Action<string> changed)
            {
                var box = new NSTextField { PlaceholderString = placeholder, StringValue = value };
                box.Changed += (_, _) => changed(box.StringValue);
                return box;
            }
            var from = Box("From page", dialog.From, text => dialog.From = text);
            var to = Box("To page", dialog.To, text => dialog.To = text);
            var pages = NSStackView.FromViews([Labelled("From page", from), Labelled("To page", to)]);
            pages.Distribution = NSStackViewDistribution.FillEqually;
            pages.Spacing = 16;
            sheet.AddWide(pages);
            var problem = sheet.Text("", secondary: true, size: 11);
            var label = Box("A name for the excerpt, such as Warehouse ambush", dialog.Label, text => dialog.Label = text);
            sheet.AddWide(Labelled("Label", label));
            if (dialog.ChoosesWorld)
            {
                var world = FormText.Choices(dialog.Worlds.Select(w => w.Name), at =>
                {
                    if (at >= 0 && at < dialog.Worlds.Count) dialog.World = dialog.Worlds[at];
                });
                sheet.AddWide(Labelled("World", world));
            }
            bindings.Properties(dialog, [nameof(SendToNornisDialogViewModel.From), nameof(SendToNornisDialogViewModel.To)], () =>
            {
                problem.StringValue = dialog.PagesProblem ?? "";
                problem.Hidden = dialog.PagesProblem is null;
                sheet.Fit();
            });
            var cancel = NSButton.CreateButton("Cancel", () => { });
            cancel.KeyEquivalent = "\u001b";
            bindings.Command(cancel, dialog.CancelCommand);
            var open = NSButton.CreateButton("Open in Nornis", () => { });
            bindings.Command(open, dialog.OpenCommand);
            sheet.Buttons(cancel, open);
            sheet.Show(host);
            showing = (sheet, bindings);
        });
        owner.Add(Close);
    }

    static NSStackView Labelled(string label, NSView field)
    {
        var name = NSTextField.CreateLabel(label);
        name.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium)!;
        var column = NSStackView.FromViews([name, field]);
        column.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        column.Alignment = NSLayoutAttribute.Leading;
        column.Spacing = 4;
        field.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        return column;
    }
}
