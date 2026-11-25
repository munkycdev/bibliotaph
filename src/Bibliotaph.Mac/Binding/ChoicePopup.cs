using System.Collections.Specialized;
using System.ComponentModel;
using AppKit;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Binding;

/// <summary>
/// A pop-up button over a view model's choices, as WPF's ComboBox over a list and its selected item: it lists the
/// choices by label, shows the chosen one, and sets it when another is picked. The list can change under it.
/// </summary>
static class ChoicePopup
{
    public static NSPopUpButton Create<T>(Bindings bindings, INotifyPropertyChanged source, string property,
        Func<IReadOnlyList<Choice<T>>> choices, Func<Choice<T>?> chosen, Action<Choice<T>> choose)
    {
        var popup = new NSPopUpButton { ControlSize = NSControlSize.Small };
        popup.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        // A pop-up is as wide as its longest choice, which for a folder or a game system can be very long; past this
        // the title shortens instead.
        popup.WidthAnchor.ConstraintLessThanOrEqualTo(240).Active = true;
        popup.Cell.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        var showing = false;
        void Show()
        {
            showing = true;
            popup.RemoveAllItems();
            // Through the menu, as AddItem drops a title the list already has.
            foreach (var choice in choices()) popup.Menu!.AddItem(new NSMenuItem(choice.Label));
            var list = choices();
            var at = chosen() is { } current ? IndexOf(list, current) : -1;
            if (at >= 0) popup.SelectItem(at);
            showing = false;
        }
        popup.Activated += (_, _) =>
        {
            var list = choices();
            var at = (int)popup.IndexOfSelectedItem;
            if (!showing && at >= 0 && at < list.Count) choose(list[at]);
        };
        bindings.Property(source, property, Show);
        if (choices() is INotifyCollectionChanged collection) bindings.Collection(collection, _ => Show());
        return popup;
    }

    static int IndexOf<T>(IReadOnlyList<Choice<T>> list, Choice<T> choice)
    {
        for (var i = 0; i < list.Count; i++)
            if (Equals(list[i], choice)) return i;
        return -1;
    }
}
