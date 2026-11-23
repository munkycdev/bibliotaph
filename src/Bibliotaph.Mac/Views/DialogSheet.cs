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

    /// <summary>A collection to add to, from a list its words narrow; naming one is a sheet of its own.</summary>
    static void Show(CollectionDialogViewModel dialog, NSWindow host)
    {
        if (dialog.IsNaming)
        {
            ShowNaming(dialog, host);
            return;
        }
        var alert = new NSAlert { MessageText = dialog.Heading };
        var choices = Choices(dialog.Shown.Select(c => new string(' ', c.Depth * 3) + c.Name));
        // Words narrow the list to the collections named with them, as Windows' filter box does.
        var filter = Filter(text => dialog.Filter = text, () => Refill(choices, dialog.Shown.Select(c => new string(' ', c.Depth * 3) + c.Name), "No collection has that in its name."),
            "Collections named with…");
        alert.AccessoryView = Fields(filter, choices);
        alert.Window.InitialFirstResponder = filter;
        alert.AddButton("Add").KeyEquivalent = "\r";
        alert.AddButton("Cancel").KeyEquivalent = "\u001b";
        // A question that can wait doesn't stop the app quitting.
        alert.Window.PreventsApplicationTerminationWhenModal = false;
        alert.BeginSheet(host, response =>
        {
            if ((long)response == (long)NSAlertButtonReturn.First && choices.IndexOfSelectedItem is var at and >= 0 && at < dialog.Shown.Count)
                dialog.PickCommand.Execute(dialog.Shown[(int)at]);
            else dialog.CancelCommand.Execute(null);
            alert.Dispose();
        });
    }

    /// <summary>A binder to add to, from a list its words narrow; naming one and adding pages are sheets of their own.</summary>
    static void Show(SessionDialogViewModel dialog, NSWindow host)
    {
        if (dialog.IsNaming)
        {
            ShowNaming(dialog, host);
            return;
        }
        if (!dialog.IsPicking)
        {
            ShowPages(dialog, host);
            return;
        }
        var alert = new NSAlert { MessageText = dialog.Heading };
        var packs = Choices(dialog.Shown.Select(p => p.Title));
        var filter = Filter(text => dialog.Filter = text, () => Refill(packs, dialog.Shown.Select(p => p.Title), "No binder has that in its title."), "Binders titled with…");
        alert.AccessoryView = Fields(filter, packs);
        alert.Window.InitialFirstResponder = filter;
        alert.AddButton("Add").KeyEquivalent = "\r";
        alert.AddButton("Cancel").KeyEquivalent = "\u001b";
        alert.Window.PreventsApplicationTerminationWhenModal = false;
        alert.BeginSheet(host, response =>
        {
            if ((long)response == (long)NSAlertButtonReturn.First && packs.IndexOfSelectedItem is var at and >= 0 && at < dialog.Shown.Count)
                dialog.PickCommand.Execute(dialog.Shown[(int)at]);
            else dialog.CancelCommand.Execute(null);
            alert.Dispose();
        });
    }

    /// <summary>
    /// New collection or Rename, as a sheet of its own: the name and an optional description, with Save only once a name
    /// is typed, as Windows' dialog has it.
    /// </summary>
    static void ShowNaming(CollectionDialogViewModel dialog, NSWindow host)
    {
        var sheet = new Sheet(420);
        var bindings = new Bindings();
        sheet.Text(dialog.Heading, size: 15).Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold)!;
        var name = new NSTextField { StringValue = dialog.Name, PlaceholderString = "Required" };
        name.Changed += (_, _) => dialog.Name = name.StringValue;
        var description = new NSTextField { StringValue = dialog.Description, PlaceholderString = "Optional" };
        description.Changed += (_, _) => dialog.Description = description.StringValue;
        sheet.AddWide(Labelled("Name:", name));
        sheet.AddWide(Labelled("Description:", description));
        var cancel = NSButton.CreateButton("Cancel", () => dialog.CancelCommand.Execute(null));
        cancel.KeyEquivalent = "\u001b";
        var save = NSButton.CreateButton(dialog.ActionText, () => { });
        bindings.Command(save, dialog.SaveCommand);
        sheet.Buttons(cancel, save);
        void Closed(object? sender, CollectionDialogResult? result)
        {
            dialog.Closed -= Closed;
            bindings.Dispose();
            sheet.Close();
            sheet.Dispose();
        }
        dialog.Closed += Closed;
        sheet.Window.InitialFirstResponder = name;
        sheet.Show(host);
    }

    /// <summary>
    /// New binder, Rename or date, or a section's name, as a sheet of its own: the title, and for a binder its date or
    /// Keep at hand with the game system it's for. A date that doesn't read says so under it while the sheet stays.
    /// </summary>
    static void ShowNaming(SessionDialogViewModel dialog, NSWindow host)
    {
        var sheet = new Sheet(440);
        var bindings = new Bindings();
        sheet.Text(dialog.Heading, size: 15).Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold)!;
        var title = new NSTextField { StringValue = dialog.Title, PlaceholderString = "Required" };
        title.Changed += (_, _) => dialog.Title = title.StringValue;
        sheet.AddWide(Labelled($"{dialog.TitleLabel}:", title));
        NSTextField? problem = null;
        if (dialog.ShowsAtHand)
        {
            var date = new NSTextField { StringValue = dialog.DateText, PlaceholderString = "Optional, such as 14 Dec" };
            date.Changed += (_, _) => dialog.DateText = date.StringValue;
            var dateRow = Labelled("Date:", date);
            sheet.AddWide(dateRow);
            problem = sheet.Text("", size: 11);
            problem.TextColor = NSColor.SystemOrange;
            var atHand = NSButton.CreateCheckbox("Keep at hand, with no date", () => { });
            atHand.State = dialog.KeepAtHand ? NSCellStateValue.On : NSCellStateValue.Off;
            atHand.ToolTip = "For rules and references you look up often. At hand in any binder opens its pages in a window of their own.";
            atHand.Activated += (_, _) => dialog.KeepAtHand = atHand.State == NSCellStateValue.On;
            // In the fields' column, under them, as a Mac form lines its checkboxes up.
            sheet.AddWide(Labelled("", atHand));
            var system = FormText.Choices(dialog.SystemChoices.Select(c => c.Label), at =>
            {
                if (at >= 0 && at < dialog.SystemChoices.Count) dialog.GameSystem = dialog.SystemChoices[at];
            });
            if (dialog.GameSystem is { } chosen) system.SelectItem(dialog.SystemChoices.ToList().IndexOf(chosen));
            var systemRow = Labelled("For:", system);
            sheet.AddWide(systemRow);
            bindings.Properties(dialog, [nameof(SessionDialogViewModel.ShowsDate), nameof(SessionDialogViewModel.DateProblem), nameof(SessionDialogViewModel.KeepAtHand)], () =>
            {
                dateRow.Hidden = !dialog.ShowsDate;
                systemRow.Hidden = !dialog.KeepAtHand;
                problem.StringValue = dialog.DateProblem ?? "";
                problem.Hidden = dialog.DateProblem is null;
                sheet.Fit();
            });
        }
        var cancel = NSButton.CreateButton("Cancel", () => dialog.CancelCommand.Execute(null));
        cancel.KeyEquivalent = "\u001b";
        var save = NSButton.CreateButton(dialog.ActionText, () => { });
        bindings.Command(save, dialog.SaveCommand);
        sheet.Buttons(cancel, save);
        void Closed(object? sender, SessionDialogResult? result)
        {
            dialog.Closed -= Closed;
            bindings.Dispose();
            sheet.Close();
            sheet.Dispose();
        }
        dialog.Closed += Closed;
        sheet.Window.InitialFirstResponder = title;
        sheet.Show(host);
    }

    /// <summary>A field with its name beside it, right-aligned, as a Mac form has them.</summary>
    static NSStackView Labelled(string name, NSView field)
    {
        var label = NSTextField.CreateLabel(name);
        label.Alignment = NSTextAlignment.Right;
        label.WidthAnchor.ConstraintEqualTo(90).Active = true;
        field.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var row = NSStackView.FromViews([label, field]);
        row.Spacing = 8;
        return row;
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
