using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Settings (⌘,) with the sections the Mac has so far: Library, its folders with what each holds, and Preferences,
/// light or dark and what waits in Needs review. Each section is the Windows one's view model, loaded while it shows.
/// </summary>
public sealed class SettingsWindow : NSWindowController
{
    readonly IServiceProvider _services;
    readonly FolderAdding _adding;
    readonly List<(SettingsSectionViewModel Section, Bindings Bindings)> _open = [];

    public SettingsWindow(IServiceProvider services, FolderAdding adding)
        : base(new NSWindow(new CGRect(0, 0, 620, 460), NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable, NSBackingStore.Buffered, false))
    {
        _services = services;
        _adding = adding;
        var window = Window!;
        window.Title = "Settings";
        window.ReleaseWhenClosed(false);
        window.WillClose += (_, _) => Closed();
    }

    public void Open()
    {
        if (_open.Count == 0) Build();
        Window!.Center();
        Window.MakeKeyAndOrderFront(null);
    }

    void Build()
    {
        var tabs = new NSTabViewController { TabStyle = NSTabViewControllerTabStyle.Toolbar };
        tabs.AddTabViewItem(Tab("Library", "books.vertical", LibraryTab()));
        tabs.AddTabViewItem(Tab("Preferences", "gearshape", PreferencesTab()));
        Window!.ContentViewController = tabs;
        Window.SetContentSize(new CGSize(620, 460));
    }

    static NSTabViewItem Tab(string label, string symbol, NSView view) => new()
    {
        Label = label,
        Image = NSImage.GetSystemSymbol(symbol, label)!,
        ViewController = new NSViewController { View = view },
    };

    NSView LibraryTab()
    {
        var library = _services.GetRequiredService<LibrarySectionViewModel>();
        var bindings = Load(library);

        var intro = NSTextField.CreateWrappingLabel("The folders Bibliotaph reads your books from. Files stay where they are and are only ever read; removing a folder keeps your work on its books.");
        intro.TextColor = NSColor.SecondaryLabel;
        var list = Stack(vertical: true, spacing: 10);
        var add = NSButton.CreateButton("Add folder…", () => _adding.PickAsync().FireAndForget());
        var rescan = NSButton.CreateButton("Look for changes", () => { });
        bindings.Command(rescan, library.RescanCommand);
        var buttons = Stack(vertical: false, spacing: 8, add, rescan);

        void Refresh()
        {
            foreach (var view in list.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var pending in library.Pending) list.AddArrangedSubview(Pending(library, pending, bindings));
            if (library.Folders.Count == 0 && library.Pending.Count == 0) list.AddArrangedSubview(NSTextField.CreateLabel("No folders yet."));
            foreach (var folder in library.Folders) list.AddArrangedSubview(Folder(library, folder, bindings));
        }
        bindings.Collection(library.Folders, _ => Refresh());
        bindings.Collection(library.Pending, _ => Refresh());
        Refresh();
        EventHandler added = (_, _) => Bindings.OnMain(() => library.LoadAsync().FireAndForget());
        _adding.Added += added;
        bindings.Add(() => _adding.Added -= added);

        var scroll = new NSScrollView { HasVerticalScroller = true, DrawsBackground = false, DocumentView = Flipped(list) };
        return Page(intro, scroll, buttons);
    }

    static NSStackView Folder(LibrarySectionViewModel library, LibraryFolderItem folder, Bindings bindings)
    {
        var path = NSTextField.CreateLabel(folder.Path);
        path.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Medium)!;
        path.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        var detail = NSTextField.CreateWrappingLabel("");
        detail.TextColor = NSColor.SecondaryLabel;
        detail.Font = NSFont.SystemFontOfSize(11)!;
        bindings.Properties(folder, [nameof(LibraryFolderItem.Detail), nameof(LibraryFolderItem.Problem)],
            () => detail.StringValue = folder.HasProblem ? folder.Problem : folder.Detail);
        var remove = NSButton.CreateButton("Remove", () =>
        {
            using var alert = new NSAlert
            {
                MessageText = $"Remove “{Path.GetFileName(folder.Path.TrimEnd('/'))}” from your library?",
                InformativeText = "Its books leave the library. The folder and its files aren't touched, and your work on its books is kept if you add it again.",
            };
            alert.AddButton("Remove");
            alert.AddButton("Cancel");
            if (alert.RunModal() == (nint)(long)NSAlertButtonReturn.First) library.RemoveCommand.Execute(folder);
        });
        remove.ControlSize = NSControlSize.Small;
        var text = Stack(vertical: true, spacing: 2, path, detail);
        var row = Stack(vertical: false, spacing: 12, text, remove);
        row.Alignment = NSLayoutAttribute.CenterY;
        text.SetContentHuggingPriorityForOrientation(200, NSLayoutConstraintOrientation.Horizontal);
        path.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        return row;
    }

    /// <summary>A folder picked from a link elsewhere (the Library's Add folder), with what it holds, to add or not.</summary>
    static NSStackView Pending(LibrarySectionViewModel library, PendingFolder pending, Bindings bindings)
    {
        var path = NSTextField.CreateLabel(pending.Shown);
        path.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Medium)!;
        path.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        var detail = NSTextField.CreateWrappingLabel("");
        detail.TextColor = NSColor.SecondaryLabel;
        detail.Font = NSFont.SystemFontOfSize(11)!;
        var add = NSButton.CreateButton("Add", () => library.ConfirmAddCommand.Execute(pending));
        var cancel = NSButton.CreateButton("Cancel", () => library.CancelAddCommand.Execute(pending));
        bindings.Properties(pending, [nameof(PendingFolder.Detail), nameof(PendingFolder.CanAdd)], () =>
        {
            detail.StringValue = pending.Detail;
            add.Enabled = pending.CanAdd;
        });
        var text = Stack(vertical: true, spacing: 2, path, detail);
        var row = Stack(vertical: false, spacing: 8, text, add, cancel);
        text.SetContentHuggingPriorityForOrientation(200, NSLayoutConstraintOrientation.Horizontal);
        path.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        return row;
    }

    NSView PreferencesTab()
    {
        var preferences = _services.GetRequiredService<PreferencesSectionViewModel>();
        var bindings = Load(preferences);

        var appearance = NSTextField.CreateLabel("Appearance");
        appearance.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        var system = Radio("Same as macOS", () => preferences.UseSystem = true);
        var light = Radio("Light", () => preferences.UseLight = true);
        var dark = Radio("Dark", () => preferences.UseDark = true);
        bindings.Properties(preferences, [nameof(PreferencesSectionViewModel.UseSystem), nameof(PreferencesSectionViewModel.UseLight),
            nameof(PreferencesSectionViewModel.UseDark)], () =>
        {
            system.State = preferences.UseSystem ? NSCellStateValue.On : NSCellStateValue.Off;
            light.State = preferences.UseLight ? NSCellStateValue.On : NSCellStateValue.Off;
            dark.State = preferences.UseDark ? NSCellStateValue.On : NSCellStateValue.Off;
        });

        var review = NSTextField.CreateLabel("Needs review");
        review.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        var unsure = Radio("Only what Bibliotaph is unsure of", () => preferences.ReviewWhenUnsure = true);
        var everything = Radio("Everything it works out", () => preferences.ReviewEverything = true);
        var note = NSTextField.CreateWrappingLabel("");
        note.TextColor = NSColor.SecondaryLabel;
        bindings.Properties(preferences, [nameof(PreferencesSectionViewModel.ReviewWhenUnsure), nameof(PreferencesSectionViewModel.ReviewEverything),
            nameof(PreferencesSectionViewModel.ReviewNote)], () =>
        {
            unsure.State = preferences.ReviewWhenUnsure ? NSCellStateValue.On : NSCellStateValue.Off;
            everything.State = preferences.ReviewEverything ? NSCellStateValue.On : NSCellStateValue.Off;
            note.StringValue = preferences.ReviewNote;
        });

        return Page(Stack(vertical: true, spacing: 6, appearance, system, light, dark), Stack(vertical: true, spacing: 6, review, unsure, everything, note));
    }

    /// <summary>Loads a section while the window shows it, and unloads it with the window.</summary>
    Bindings Load(SettingsSectionViewModel section)
    {
        var bindings = new Bindings();
        _open.Add((section, bindings));
        section.LoadAsync().FireAndForget();
        return bindings;
    }

    /// <summary>The sections stop following the library; the next Open builds them afresh in place of these.</summary>
    void Closed()
    {
        foreach (var (section, bindings) in _open)
        {
            section.Unload();
            bindings.Dispose();
        }
        _open.Clear();
    }

    static NSButton Radio(string title, Action chosen)
    {
        var button = NSButton.CreateRadioButton(title, chosen);
        return button;
    }

    static NSStackView Stack(bool vertical, double spacing, params NSView[] views)
    {
        var stack = NSStackView.FromViews(views);
        stack.Orientation = vertical ? NSUserInterfaceLayoutOrientation.Vertical : NSUserInterfaceLayoutOrientation.Horizontal;
        stack.Alignment = vertical ? NSLayoutAttribute.Leading : NSLayoutAttribute.CenterY;
        stack.Spacing = (nfloat)spacing;
        return stack;
    }

    /// <summary>A scroll view's document that starts at the top, with its content as wide as the view.</summary>
    static FlippedView Flipped(NSView content)
    {
        var document = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        document.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            content.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            content.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            content.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
        ]);
        return document;
    }

    static NSView Page(params NSView[] parts)
    {
        var root = new NSView();
        NSView? above = null;
        var constraints = new List<NSLayoutConstraint>();
        foreach (var part in parts)
        {
            part.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(part);
            constraints.Add(part.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 24));
            constraints.Add(part.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -24));
            constraints.Add(part.TopAnchor.ConstraintEqualTo(above?.BottomAnchor ?? root.TopAnchor, above is null ? 20 : 16));
            if (part is NSScrollView scroll)
            {
                constraints.Add(scroll.HeightAnchor.ConstraintGreaterThanOrEqualTo(160));
                constraints.Add(scroll.DocumentView!.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor));
            }
            above = part;
        }
        if (above is not null) constraints.Add(above.BottomAnchor.ConstraintLessThanOrEqualTo(root.BottomAnchor, -20));
        // The folder list takes the room the rest leaves.
        if (parts.OfType<NSScrollView>().FirstOrDefault() is { } list && parts[^1] != list)
            constraints.Add(parts[^1].BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -20));
        NSLayoutConstraint.ActivateConstraints([.. constraints]);
        return root;
    }

    sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}
