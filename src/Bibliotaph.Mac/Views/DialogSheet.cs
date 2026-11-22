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

    static void Show(CollectionDialogViewModel dialog, NSWindow host)
    {
        var alert = new NSAlert { MessageText = dialog.Heading };
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
            alert.AccessoryView = Fields(choices);
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
                else dialog.CancelCommand.Execute(null);
            }
            else if (choices!.IndexOfSelectedItem is var at and >= 0 && at < dialog.Shown.Count) dialog.PickCommand.Execute(dialog.Shown[(int)at]);
            else dialog.CancelCommand.Execute(null);
            alert.Dispose();
        });
    }

    static void Show(SessionDialogViewModel dialog, NSWindow host)
    {
        var alert = new NSAlert { MessageText = dialog.Heading };
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
            alert.AccessoryView = Fields(packs);
        }
        else
        {
            // Adding a page range is done from the reader, which offers it on Windows; the Mac reader doesn't yet.
            dialog.CancelCommand.Execute(null);
            alert.Dispose();
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
                else dialog.CancelCommand.Execute(null);
            }
            else if (packs!.IndexOfSelectedItem is var at and >= 0 && at < dialog.Shown.Count) dialog.PickCommand.Execute(dialog.Shown[(int)at]);
            else dialog.CancelCommand.Execute(null);
            alert.Dispose();
        });
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
