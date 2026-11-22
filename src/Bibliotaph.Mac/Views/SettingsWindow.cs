using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Settings (⌘,): Library, its folders with what each holds; Online libraries; Processing; AI; Vocabulary; Preferences, light or dark and what
/// waits in Needs review; Passwords; and Backup. Each section is the Windows one's view model, loaded while it shows.
/// </summary>
public sealed partial class SettingsWindow : NSWindowController
{
    readonly IServiceProvider _services;
    readonly FolderAdding _adding;
    readonly List<(SettingsSectionViewModel Section, Bindings Bindings)> _open = [];

    public SettingsWindow(IServiceProvider services, FolderAdding adding)
        // No minimize or zoom, as the Mac's own settings windows: ⌘, brings it back, and each pane scrolls in its size.
        : base(new NSWindow(new CGRect(0, 0, 720, 640), NSWindowStyle.Titled | NSWindowStyle.Closable, NSBackingStore.Buffered, false))
    {
        _services = services;
        _adding = adding;
        var window = Window!;
        window.Title = "Settings";
        window.ReleaseWhenClosed(false);
        window.AutorecalculatesKeyViewLoop = true;
        window.BackgroundColor = Palette.Background;
        window.WillClose += (_, _) => Closed();
    }

    NSTabViewController? _tabs;

    /// <summary>Opens Settings at a section, as a link into Settings does on Windows, or where it was.</summary>
    public void Open(Presentation.Services.SettingsSection? section = null)
    {
        if (_open.Count == 0) Build();
        var at = section switch
        {
            Presentation.Services.SettingsSection.Library => 0,
            Presentation.Services.SettingsSection.OnlineLibraries => 1,
            Presentation.Services.SettingsSection.Processing => 2,
            Presentation.Services.SettingsSection.Ai => 3,
            Presentation.Services.SettingsSection.Vocabulary => 4,
            Presentation.Services.SettingsSection.Preferences => 5,
            Presentation.Services.SettingsSection.Passwords => 6,
            Presentation.Services.SettingsSection.Backup => 7,
            _ => -1,
        };
        // Else the pane last looked at, as the Mac's settings windows reopen where they were.
        if (at < 0) at = (int)NSUserDefaults.StandardUserDefaults.IntForKey(LastPane);
        if (_tabs is { } tabs && at >= 0 && at < tabs.TabViewItems.Length) tabs.SelectedTabViewItemIndex = at;
        Window!.Center();
        Window.MakeKeyAndOrderFront(null);
    }

    void Build()
    {
        var tabs = new NSTabViewController { TabStyle = NSTabViewControllerTabStyle.Toolbar };
        tabs.AddTabViewItem(Tab("Library", "books.vertical", LibraryTab()));
        tabs.AddTabViewItem(Tab("Online", "globe", OnlineTab()));
        tabs.AddTabViewItem(Tab("Processing", "gauge.with.dots.needle.33percent", ProcessingTab()));
        tabs.AddTabViewItem(Tab("AI", "sparkles", AiTab()));
        tabs.AddTabViewItem(Tab("Vocabulary", "character.book.closed", VocabularyTab()));
        tabs.AddTabViewItem(Tab("Preferences", "gearshape", PreferencesTab()));
        tabs.AddTabViewItem(Tab("Passwords", "key", PasswordsTab()));
        tabs.AddTabViewItem(Tab("Backup", "externaldrive", BackupTab()));
        _tabs = tabs;
        Window!.ContentViewController = tabs;
        Window.SetContentSize(new CGSize(720, 640));
    }

    const string LastPane = "SettingsPane";

    static NSTabViewItem Tab(string label, string symbol, NSView view) => new()
    {
        Label = label,
        Image = NSImage.GetSystemSymbol(symbol, label)!,
        // A pane of its own layout, rather than one that scrolls, keeps a size that fits it from the start.
        ViewController = new PaneController { View = view, Title = label, PreferredContentSize = view is NSScrollView ? CGSize.Empty : new CGSize(720, 640) },
    };

    /// <summary>A pane, which remembers it was the one looked at last.</summary>
    sealed class PaneController : NSViewController
    {
        public override void ViewDidAppear()
        {
            base.ViewDidAppear();
            if (View.Window is not { } window) return;
            // The window's title names the pane showing.
            window.Title = Title ?? "Settings";
            if (window.ContentViewController is NSTabViewController tabs)
                NSUserDefaults.StandardUserDefaults.SetInt(tabs.SelectedTabViewItemIndex, LastPane);
            Fit(window);
            // A pane whose content arrives after it shows, or grows and shrinks, takes the window with it.
            if (View is NSScrollView { DocumentView: { } document } && _watch is null)
            {
                document.PostsFrameChangedNotifications = true;
                _watch = NSView.Notifications.ObserveFrameChanged(document, (_, _) =>
                {
                    if (View.Window is { } shown && shown.ContentViewController is NSTabViewController { SelectedTabViewItemIndex: var at } tabs
                        && tabs.TabViewItems[at].ViewController == this) Fit(shown);
                });
            }
        }

        NSObject? _watch;

        /// <summary>
        /// The window takes the pane's height, as the Mac's settings windows do, keeping its top where it was; a pane
        /// taller than that scrolls.
        /// </summary>
        void Fit(NSWindow window)
        {
            // A pane of its own layout, the Vocabulary's list beside what it shows, keeps a size that fits both.
            if (View is not NSScrollView { DocumentView: { } document })
            {
                PreferredContentSize = new CGSize(720, 640);
                return;
            }
            View.LayoutSubtreeIfNeeded();
            var height = Math.Clamp(document.FittingSize.Height, 240, 700);
            // The tab view controller sizes the window to the pane's preferred size, keeping its top where it was.
            if (Math.Abs(PreferredContentSize.Height - height) < 2) return;
            PreferredContentSize = new CGSize(720, height);
        }
    }

    /// <summary>A pane of grouped sections, scrolling when it is taller than the window lets it be.</summary>
    static NSScrollView Pane(Form form)
    {
        var scroll = new NSScrollView { HasVerticalScroller = true, DrawsBackground = false, DocumentView = Flipped(form.Root) };
        scroll.DocumentView!.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor).Active = true;
        return scroll;
    }

    /// <summary>The library's folders, each with what it holds, and adding or looking again; as a list with its buttons under it.</summary>
    NSScrollView LibraryTab()
    {
        var library = _services.GetRequiredService<LibrarySectionViewModel>();
        var bindings = Load(library);

        var form = new Form();
        var folders = form.Section("Library folders",
            "Bibliotaph reads your books from these folders. Files stay where they are and are only ever read; removing a folder keeps your work on its books.");
        var rescan = NSButton.CreateButton("Look for Changes", () => { });
        bindings.Command(rescan, library.RescanCommand);
        form.Buttons(rescan, NSButton.CreateButton("Add Folder…", () => _adding.PickAsync().FireAndForget()));

        var rows = new Bindings();
        bindings.Add(() => rows.Dispose());
        void Refresh()
        {
            rows.Dispose();
            rows = new Bindings();
            folders.Clear();
            foreach (var pending in library.Pending) Pending(folders, library, pending, rows);
            foreach (var folder in library.Folders) Folder(folders, library, folder, rows);
            if (library.Folders.Count == 0 && library.Pending.Count == 0) folders.Row("No folders yet. Add one to start your library.");
        }
        bindings.Collection(library.Folders, _ => Refresh());
        bindings.Collection(library.Pending, _ => Refresh());
        Refresh();
        EventHandler added = (_, _) => Bindings.OnMain(() => library.LoadAsync().FireAndForget());
        _adding.Added += added;
        bindings.Add(() => _adding.Added -= added);
        return Pane(form);
    }

    /// <summary>A folder: its name, with its path and how it stands under it; Remove, and Point to its new place while it is offline.</summary>
    static void Folder(FormSection section, LibrarySectionViewModel library, LibraryFolderItem folder, Bindings bindings)
    {
        var point = NSButton.CreateButton("Locate…", () => library.PointToNewPlaceCommand.Execute(folder));
        point.ToolTip = "Point to where the folder is now";
        var remove = NSButton.CreateButton("Remove…", () =>
        {
            using var alert = new NSAlert
            {
                MessageText = $"Remove “{Name(folder.Path)}” from your library?",
                InformativeText = "Its books leave the library. The folder and its files aren't touched, and your work on its books is kept if you add it again.",
            };
            alert.AddButton("Remove");
            alert.AddButton("Cancel");
            if (alert.RunModal() == (nint)(long)NSAlertButtonReturn.First) library.RemoveCommand.Execute(folder);
        });
        var row = section.Row(Name(folder.Path), point, remove);
        row.Label.LineBreakMode = NSLineBreakMode.TruncatingTail;
        row.OneLine();
        var state = row.AddDetail();
        bindings.Properties(folder, [nameof(LibraryFolderItem.Path), nameof(LibraryFolderItem.Detail), nameof(LibraryFolderItem.Problem),
            nameof(LibraryFolderItem.CanPointToNewPlace)], () =>
        {
            row.Label.StringValue = Name(folder.Path);
            row.ShowDetail(folder.Path);
            row.Detail.ToolTip = folder.Path;
            state.StringValue = folder.HasProblem ? folder.Problem : folder.Detail;
            state.TextColor = folder.HasProblem ? NSColor.SystemOrange : NSColor.SecondaryLabel;
            point.Hidden = !folder.CanPointToNewPlace;
        });
    }

    /// <summary>A folder picked from a link elsewhere (the Library's Add folder), with what it holds, to add or not.</summary>
    static void Pending(FormSection section, LibrarySectionViewModel library, PendingFolder pending, Bindings bindings)
    {
        var add = NSButton.CreateButton("Add", () => library.ConfirmAddCommand.Execute(pending));
        add.KeyEquivalent = "\r";
        var cancel = NSButton.CreateButton("Cancel", () => library.CancelAddCommand.Execute(pending));
        var row = section.Row(pending.Shown, cancel, add);
        bindings.Properties(pending, [nameof(PendingFolder.Detail), nameof(PendingFolder.CanAdd)], () =>
        {
            row.ShowDetail(pending.Detail);
            add.Enabled = pending.CanAdd;
        });
    }

    /// <summary>A folder's own name, as Finder shows it.</summary>
    static string Name(string path) => Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : path;

    /// <summary>
    /// A model server for Bibliotaph to read books and answer with: its address, which model, a key if it needs one, and
    /// which library folders may be sent. The built-in model is Windows' only, so the Mac always uses a server.
    /// </summary>
    NSScrollView AiTab()
    {
        var ai = _services.GetRequiredService<AiSettingsViewModel>();
        var bindings = Load(ai);
        // The built-in engine is llama.cpp's Windows build; a Mac switched to it would read nothing.
        bindings.Property(ai, nameof(AiSettingsViewModel.ShowBuiltIn), () =>
        {
            if (ai.ShowBuiltIn) ai.UseServer = true;
        });

        var form = new Form();
        var server = form.Section("Model server",
            "Bibliotaph can ask a model server you run or use (Ollama, LM Studio, or one online) to read your books: what system and kind each is, and answers in Ask. Nothing is sent until you save a model here.");
        var endpoint = new NSTextField { PlaceholderString = "http://localhost:11434" };
        endpoint.WidthAnchor.ConstraintEqualTo(240).Active = true;
        endpoint.Changed += (_, _) => ai.Endpoint = endpoint.StringValue;
        var connect = NSButton.CreateButton("Connect", () => { });
        bindings.Command(connect, ai.ConnectCommand);
        endpoint.Activated += (_, _) =>
        {
            if (ai.ConnectCommand.CanExecute(null)) ai.ConnectCommand.Execute(null);
        };
        var address = server.Row("Address", endpoint, connect);
        var model = new NSPopUpButton();
        model.Activated += (_, _) => ai.Model = model.TitleOfSelectedItem ?? "";
        model.WidthAnchor.ConstraintEqualTo(240).Active = true;
        server.Row("Model", "Reads your books and answers in Ask", model);
        var embedding = new NSPopUpButton();
        embedding.Activated += (_, _) =>
        {
            var at = (int)embedding.IndexOfSelectedItem;
            if (at >= 0 && at < ai.EmbeddingChoices.Count) ai.EmbeddingModel = ai.EmbeddingChoices[at].Value;
        };
        embedding.WidthAnchor.ConstraintEqualTo(240).Active = true;
        server.Row("Passages by meaning", "Finds passages that say the same thing in other words", embedding);
        var key = new NSSecureTextField { PlaceholderString = "If the server needs one" };
        key.WidthAnchor.ConstraintEqualTo(240).Active = true;
        key.Changed += (_, _) => ai.NewKey = key.StringValue;
        var forgetKey = NSButton.CreateButton("Forget", () => { });
        bindings.Command(forgetKey, ai.ForgetKeyCommand);
        server.Row("API key", key, forgetKey);
        var remote = FormText.Switch(on => ai.RemoteAllowed = on);
        var remoteRow = server.Row("Send books to this server", "It isn't on this Mac", remote);

        var state = form.Section();
        var save = NSButton.CreateButton("Save", () => { });
        save.KeyEquivalent = "\r";
        bindings.Command(save, ai.SaveCommand);
        var status = state.Row("", save);

        var foldersSection = form.Section("Folders it may read", "Only books in the folders ticked here are sent to the model.");

        bindings.Property(ai, nameof(AiSettingsViewModel.Endpoint), () =>
        {
            if (endpoint.CurrentEditor is null) endpoint.StringValue = ai.Endpoint;
        });
        bindings.Properties(ai, [nameof(AiSettingsViewModel.ConnectionText), nameof(AiSettingsViewModel.ConnectionProblem), nameof(AiSettingsViewModel.IsConnecting)], () =>
        {
            address.ShowDetail(ai.IsConnecting ? "Connecting…" : ai.ConnectionProblem ?? ai.ConnectionText);
            address.Detail.TextColor = ai.ConnectionProblem is null ? NSColor.SecondaryLabel : NSColor.SystemRed;
        });
        void Models()
        {
            model.RemoveAllItems();
            foreach (var name in ai.Models) model.Menu!.AddItem(new NSMenuItem(name));
            if (ai.Models.Count == 0) model.Menu!.AddItem(new NSMenuItem("Connect to list the server's models"));
            if (ai.Model.Length > 0 && ai.Models.IndexOf(ai.Model) is var at and >= 0) model.SelectItem(at);
            model.Enabled = ai.HasModels;
            embedding.RemoveAllItems();
            foreach (var choice in ai.EmbeddingChoices) embedding.Menu!.AddItem(new NSMenuItem(choice.Label));
            var chosen = ai.EmbeddingChoices.ToList().FindIndex(c => c.Value == ai.EmbeddingModel);
            if (chosen >= 0) embedding.SelectItem(chosen);
        }
        bindings.Collection(ai.Models, _ => Models());
        bindings.Collection(ai.EmbeddingChoices, _ => Models());
        bindings.Properties(ai, [nameof(AiSettingsViewModel.Model), nameof(AiSettingsViewModel.EmbeddingModel), nameof(AiSettingsViewModel.HasModels)], Models);
        bindings.Properties(ai, [nameof(AiSettingsViewModel.HasSavedKey), nameof(AiSettingsViewModel.RemoteAllowed), nameof(AiSettingsViewModel.Endpoint)], () =>
        {
            forgetKey.Hidden = !ai.HasSavedKey;
            remoteRow.Hidden = !ai.IsRemote;
            FormText.Set(remote, ai.RemoteAllowed);
        });
        bindings.Properties(ai, [nameof(AiSettingsViewModel.SaveNote), nameof(AiSettingsViewModel.SwitchProblem), nameof(AiSettingsViewModel.IsOn),
            nameof(AiSettingsViewModel.ProgressText)], () =>
        {
            status.Label.StringValue = ai.IsOn ? "On: books are read with this model" : "Off";
            status.ShowDetail(ai.SwitchProblem ?? ai.SaveNote);
            state.Note(ai.ProgressText);
        });
        void Folders()
        {
            foldersSection.Clear();
            foreach (var folder in ai.Folders)
            {
                var send = FormText.Switch(on => folder.Send = on);
                FormText.Set(send, folder.Send);
                var row = foldersSection.Row(Name(folder.Path), folder.Path, send);
                row.OneLine();
            }
            foldersSection.View.Hidden = ai.Folders.Count == 0;
        }
        bindings.Collection(ai.Folders, _ => Folders());
        Folders();
        return Pane(form);
    }

    /// <summary>Light or dark, where Bibliotaph opens, and what waits in Needs review: one choice a row.</summary>
    NSScrollView PreferencesTab()
    {
        var preferences = _services.GetRequiredService<PreferencesSectionViewModel>();
        var bindings = Load(preferences);

        var form = new Form();
        var general = form.Section("General");
        var appearance = FormText.Choices(["Same as macOS", "Light", "Dark"], at =>
        {
            if (at == 0) preferences.UseSystem = true;
            else if (at == 1) preferences.UseLight = true;
            else preferences.UseDark = true;
        });
        general.Row("Appearance", appearance);
        bindings.Properties(preferences, [nameof(PreferencesSectionViewModel.UseSystem), nameof(PreferencesSectionViewModel.UseLight),
            nameof(PreferencesSectionViewModel.UseDark)], () => appearance.SelectItem(preferences.UseLight ? 1 : preferences.UseDark ? 2 : 0));
        var start = FormText.Choices(["Home", "Library"], at => preferences.StartOnLibrary = at == 1);
        general.Row("Open on", "The page Bibliotaph shows when it starts", start);
        bindings.Property(preferences, nameof(PreferencesSectionViewModel.StartOnLibrary), () => start.SelectItem(preferences.StartOnLibrary ? 1 : 0));

        var review = form.Section("Needs review");
        var ask = FormText.Choices(["Only what Bibliotaph is unsure of", "Everything it works out"], at =>
        {
            if (at == 0) preferences.ReviewWhenUnsure = true;
            else preferences.ReviewEverything = true;
        });
        review.Row("Show", ask);
        bindings.Properties(preferences, [nameof(PreferencesSectionViewModel.ReviewWhenUnsure), nameof(PreferencesSectionViewModel.ReviewEverything),
            nameof(PreferencesSectionViewModel.ReviewNote)], () =>
        {
            ask.SelectItem(preferences.ReviewEverything ? 1 : 0);
            review.Note(preferences.ReviewNote);
        });
        return Pane(form);
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

    sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}
