using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// The collection and binder dialogs the shared actions open (New collection, Rename, Add to a collection, New binder,
/// Add to a binder), as sheets on the window. Each answers its view model's Save, Pick or Cancel, which close it.
/// </summary>
static class DialogSheet
{
    public static void Follow(Bindings bindings, CollectionActions actions, Func<NSWindow?> window) =>
        bindings.Property(actions, nameof(CollectionActions.Dialog), () =>
        {
            if (actions.Dialog is { } dialog && window() is { } host) Show(dialog, host);
        });

    public static void Follow(Bindings bindings, SessionActions actions, Func<NSWindow?> window) =>
        bindings.Property(actions, nameof(SessionActions.Dialog), () =>
        {
            if (actions.Dialog is { } dialog && window() is { } host) Show(dialog, host);
        });

    static void Show(CollectionDialogViewModel dialog, NSWindow host, string? problem = null)
    {
        var alert = new NSAlert { MessageText = dialog.Heading, InformativeText = problem ?? "" };
        NSTextField? name = null;
        NSTextField? description = null;
        NSPopUpButton? choices = null;
        if (dialog.IsNaming)
        {
            name = Field("Name", dialog.Name);
            description = Field("Description (optional)", dialog.Description);
            alert.AccessoryView = Fields(name, description);
            alert.Window.InitialFirstResponder = name;
        }
        else
        {
            choices = Choices(dialog.Shown.Select(c => new string(' ', c.Depth * 3) + c.Name));
            // Words narrow the list to the collections named with them, as Windows' filter box does.
            var filter = Filter(text => dialog.Filter = text, () => Refill(choices, dialog.Shown.Select(c => new string(' ', c.Depth * 3) + c.Name), "No collection has that in its name."),
                "Collections named with…");
            dialog.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CollectionDialogViewModel.Shown)) Refill(choices, dialog.Shown.Select(c => new string(' ', c.Depth * 3) + c.Name), "No collection has that in its name.");
            };
            alert.AccessoryView = Fields(filter, choices);
            alert.Window.InitialFirstResponder = filter;
        }
        alert.AddButton(dialog.IsNaming ? dialog.ActionText : "Add").KeyEquivalent = "\r";
        alert.AddButton("Cancel").KeyEquivalent = "\u001b";
        // A question that can wait doesn't stop the app quitting.
        alert.Window.PreventsApplicationTerminationWhenModal = false;
        alert.BeginSheet(host, response =>
        {
            if ((long)response != (long)NSAlertButtonReturn.First) dialog.CancelCommand.Execute(null);
            else if (name is not null)
            {
                dialog.Name = name.StringValue;
                dialog.Description = description!.StringValue;
                if (dialog.SaveCommand.CanExecute(null)) dialog.SaveCommand.Execute(null);
                else if (dialog.Name.Trim().Length == 0)
                {
                    alert.Dispose();
                    NSApplication.SharedApplication.BeginInvokeOnMainThread(() => Show(dialog, host, "Type a name."));
                    return;
                }
                else dialog.CancelCommand.Execute(null);
            }
            else if (choices!.IndexOfSelectedItem is var at and >= 0 && at < dialog.Shown.Count) dialog.PickCommand.Execute(dialog.Shown[(int)at]);
            else dialog.CancelCommand.Execute(null);
            alert.Dispose();
        });
    }

    static void Show(SessionDialogViewModel dialog, NSWindow host, string? problem = null)
    {
        // What was wrong with the last try shows under the heading, as Windows shows it under the box, and the dialog stays.
        var alert = new NSAlert { MessageText = dialog.Heading, InformativeText = problem ?? "" };
        NSTextField? title = null;
        NSTextField? date = null;
        NSButton? atHand = null;
        NSPopUpButton? system = null;
        NSPopUpButton? packs = null;
        if (dialog.IsNaming)
        {
            title = Field(dialog.TitleLabel, dialog.Title);
            var views = new List<NSView> { title };
            if (dialog.ShowsAtHand)
            {
                date = Field("Date, such as 14 Dec (optional)", dialog.DateText);
                atHand = NSButton.CreateCheckbox("Keep at hand, with no date", () => date.Enabled = atHand!.State != NSCellStateValue.On);
                atHand.State = dialog.KeepAtHand ? NSCellStateValue.On : NSCellStateValue.Off;
                system = Choices(dialog.SystemChoices.Select(c => c.Label));
                views.AddRange([date, atHand, system]);
            }
            alert.AccessoryView = Fields([.. views]);
            alert.Window.InitialFirstResponder = title;
        }
        else if (dialog.IsPicking)
        {
            packs = Choices(dialog.Shown.Select(p => p.Title));
            var filter = Filter(text => dialog.Filter = text, () => Refill(packs, dialog.Shown.Select(p => p.Title), "No binder has that in its title."), "Binders titled with…");
            dialog.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SessionDialogViewModel.Shown)) Refill(packs, dialog.Shown.Select(p => p.Title), "No binder has that in its title.");
            };
            alert.AccessoryView = Fields(filter, packs);
            alert.Window.InitialFirstResponder = filter;
        }
        else
        {
            alert.Dispose();
            ShowPages(dialog, host);
            return;
        }
        alert.AddButton(dialog.IsNaming ? dialog.ActionText : "Add").KeyEquivalent = "\r";
        alert.AddButton("Cancel").KeyEquivalent = "\u001b";
        // A question that can wait doesn't stop the app quitting.
        alert.Window.PreventsApplicationTerminationWhenModal = false;
        alert.BeginSheet(host, response =>
        {
            if ((long)response != (long)NSAlertButtonReturn.First) dialog.CancelCommand.Execute(null);
            else if (title is not null)
            {
                dialog.Title = title.StringValue;
                if (atHand is not null) dialog.KeepAtHand = atHand.State == NSCellStateValue.On;
                if (date is not null) dialog.DateText = date.StringValue;
                if (system is not null && system.IndexOfSelectedItem is var at and >= 0 && at < dialog.SystemChoices.Count)
                    dialog.GameSystem = dialog.SystemChoices[(int)at];
                if (dialog.SaveCommand.CanExecute(null)) dialog.SaveCommand.Execute(null);
                else if (dialog.DateProblem is { } date)
                {
                    alert.Dispose();
                    NSApplication.SharedApplication.BeginInvokeOnMainThread(() => Show(dialog, host, date));
                    return;
                }
                else if (dialog.Title.Trim().Length == 0)
                {
                    alert.Dispose();
                    NSApplication.SharedApplication.BeginInvokeOnMainThread(() => Show(dialog, host, $"Type a {dialog.TitleLabel.ToLowerInvariant()}."));
                    return;
                }
                else dialog.CancelCommand.Execute(null);
            }
            else if (packs!.IndexOfSelectedItem is var at and >= 0 && at < dialog.Shown.Count) dialog.PickCommand.Execute(dialog.Shown[(int)at]);
            else dialog.CancelCommand.Execute(null);
            alert.Dispose();
        });
    }

    /// <summary>
    /// Add pages: from and to as the book numbers them, a label, the binder and where in it. A sheet of its own, so
    /// pages that don't read say so while it stays open.
    /// </summary>
    static void ShowPages(SessionDialogViewModel dialog, NSWindow host)
    {
        var sheet = new Sheet(420);
        var bindings = new Bindings();
        sheet.Text("Add pages to a binder", size: 15).Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold)!;
        var from = new NSTextField { PlaceholderString = "From page", StringValue = dialog.From };
        from.Changed += (_, _) => dialog.From = from.StringValue;
        var to = new NSTextField { PlaceholderString = "To page", StringValue = dialog.To };
        to.Changed += (_, _) => dialog.To = to.StringValue;
        from.WidthAnchor.ConstraintEqualTo(90).Active = true;
        to.WidthAnchor.ConstraintEqualTo(90).Active = true;
        sheet.Content.AddArrangedSubview(Row(Label("Pages"), from, Label("to"), to));
        var problem = sheet.Text("", secondary: true, size: 11);
        problem.TextColor = NSColor.SystemOrange;
        var label = new NSTextField { PlaceholderString = "Label (optional), such as “The gate”", StringValue = dialog.Label };
        label.Changed += (_, _) => dialog.Label = label.StringValue;
        sheet.AddWide(label);

        var pack = new NSPopUpButton(CGRect.Empty, false);
        foreach (var choice in dialog.PackChoices) pack.Menu!.AddItem(new NSMenuItem(choice.Label));
        pack.Activated += (_, _) =>
        {
            if (pack.IndexOfSelectedItem is var at and >= 0 && at < dialog.PackChoices.Count) dialog.Pack = dialog.PackChoices[(int)at];
        };
        var where = new NSPopUpButton(CGRect.Empty, false);
        where.Activated += (_, _) =>
        {
            if (where.IndexOfSelectedItem is var at and >= 0 && at < dialog.SectionChoices.Count) dialog.Section = dialog.SectionChoices[(int)at];
        };
        sheet.Content.AddArrangedSubview(Row(Caption("Binder"), pack));
        sheet.Content.AddArrangedSubview(Row(Caption("Where"), where));
        pack.WidthAnchor.ConstraintEqualTo(300).Active = true;
        where.WidthAnchor.ConstraintEqualTo(300).Active = true;
        var none = sheet.Text(dialog.PackChoices.Count == 0 ? "There are no binders yet. Make one under Binders first." : "", secondary: true);

        bindings.Properties(dialog, [nameof(SessionDialogViewModel.Pack), nameof(SessionDialogViewModel.SectionChoices), nameof(SessionDialogViewModel.Section)], () =>
        {
            if (dialog.Pack is { } chosen) pack.SelectItem(dialog.PackChoices.ToList().IndexOf(chosen));
            where.RemoveAllItems();
            foreach (var choice in dialog.SectionChoices) where.Menu!.AddItem(new NSMenuItem(choice.Label));
            if (dialog.Section is { } section) where.SelectItem(dialog.SectionChoices.ToList().IndexOf(section));
        });
        bindings.Property(dialog, nameof(SessionDialogViewModel.PagesProblem), () =>
        {
            problem.StringValue = dialog.PagesProblem ?? "";
            problem.Hidden = dialog.PagesProblem is null;
            sheet.Fit();
        });

        var cancel = NSButton.CreateButton("Cancel", () => dialog.CancelCommand.Execute(null));
        cancel.KeyEquivalent = "\u001b";
        var add = NSButton.CreateButton(dialog.ActionText, () => { });
        bindings.Command(add, dialog.SaveCommand);
        sheet.Buttons(cancel, add);
        void Closed(object? sender, SessionDialogResult? result)
        {
            dialog.Closed -= Closed;
            bindings.Dispose();
            sheet.Close();
            sheet.Dispose();
        }
        dialog.Closed += Closed;
        sheet.Window.InitialFirstResponder = from;
        sheet.Show(host);
    }

    /// <summary>A box whose words narrow the list under it; Return in it adds to the first left.</summary>
    static NSSearchField Filter(Action<string> changed, Action refilled, string placeholder)
    {
        var field = new NSSearchField(new CGRect(0, 0, 300, 24)) { PlaceholderString = placeholder, SendsSearchStringImmediately = true };
        field.WidthAnchor.ConstraintEqualTo(300).Active = true;
        field.Changed += (_, _) =>
        {
            changed(field.StringValue);
            refilled();
        };
        return field;
    }

    static void Refill(NSPopUpButton popup, IEnumerable<string> labels, string none)
    {
        popup.RemoveAllItems();
        foreach (var label in labels) popup.Menu!.AddItem(new NSMenuItem(label));
        if (popup.ItemCount == 0) popup.Menu!.AddItem(new NSMenuItem(none) { Enabled = false });
    }

    static NSTextField Label(string text) => NSTextField.CreateLabel(text);

    /// <summary>A field's name, right-aligned in a column of its own, as a form's are.</summary>
    static NSTextField Caption(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Alignment = NSTextAlignment.Right;
        label.WidthAnchor.ConstraintEqualTo(50).Active = true;
        return label;
    }

    static NSStackView Row(params NSView[] views)
    {
        var row = NSStackView.FromViews(views);
        row.Spacing = 8;
        return row;
    }

    static NSTextField Field(string placeholder, string value)
    {
        var field = new NSTextField(new CGRect(0, 0, 300, 24)) { PlaceholderString = placeholder, StringValue = value };
        field.WidthAnchor.ConstraintEqualTo(300).Active = true;
        return field;
    }

    static NSPopUpButton Choices(IEnumerable<string> labels)
    {
        var popup = new NSPopUpButton(new CGRect(0, 0, 300, 26), false);
        // Through the menu, as AddItem drops a title the list already has, and two collections may share a name.
        foreach (var label in labels) popup.Menu!.AddItem(new NSMenuItem(label));
        popup.WidthAnchor.ConstraintEqualTo(300).Active = true;
        return popup;
    }

    static NSStackView Fields(params NSView[] views)
    {
        var stack = NSStackView.FromViews(views);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.Spacing = 8;
        stack.SetFrameSize(stack.FittingSize);
        return stack;
    }
}
