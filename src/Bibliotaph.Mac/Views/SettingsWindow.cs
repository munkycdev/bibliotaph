using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Settings (⌘,): Library, its folders with what each holds; Online libraries; Web Library, connecting this Mac; Processing; AI; Vocabulary; Preferences, light or dark and what
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
            Presentation.Services.SettingsSection.Devices => 2,
            Presentation.Services.SettingsSection.Processing => 3,
            Presentation.Services.SettingsSection.Ai => 4,
            Presentation.Services.SettingsSection.Vocabulary => 5,
            Presentation.Services.SettingsSection.Preferences => 6,
            Presentation.Services.SettingsSection.Passwords => 7,
            Presentation.Services.SettingsSection.Backup => 8,
            _ => -1,
        };
        // Else Library, the first pane, each time Settings opens.
        if (at < 0) at = 0;
        if (_tabs is { } tabs && at >= 0 && at < tabs.TabViewItems.Length) tabs.SelectedTabViewItemIndex = at;
        Window!.Center();
        Window.MakeKeyAndOrderFront(null);
    }

    void Build()
    {
        var tabs = new NSTabViewController { TabStyle = NSTabViewControllerTabStyle.Toolbar };
        tabs.AddTabViewItem(Tab("Library", "books.vertical", LibraryTab()));
        tabs.AddTabViewItem(Tab("Online", "globe", OnlineTab()));
        tabs.AddTabViewItem(Tab("Web Library", "icloud", WebLibraryTab()));
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

    static NSTabViewItem Tab(string label, string symbol, NSView view) => new()
    {
        Label = label,
        Image = NSImage.GetSystemSymbol(symbol, label)!,
        // A pane of its own layout, rather than one that scrolls, keeps a size that fits it from the start.
        ViewController = new PaneController { View = view, Title = label, PreferredContentSize = view is NSScrollView ? CGSize.Empty : new CGSize(720, 640) },
    };

    /// <summary>A pane, which titles the window and sizes it as it shows.</summary>
    sealed class PaneController : NSViewController
    {
        public override void ViewDidAppear()
        {
            base.ViewDidAppear();
            if (View.Window is not { } window) return;
            // The window's title names the pane showing.
            window.Title = Title ?? "Settings";
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
        Storage(form, library, bindings);
        FollowPicker(library, bindings, () => Window);
        var folders = form.Section("Library folders",
            "Bibliotaph reads your books from these folders. Files stay where they are and are only ever read; removing a folder keeps your work on its books.");
        var rescan = NSButton.CreateButton("Look for Changes", () => { });
        bindings.Command(rescan, library.RescanCommand);
        var fromStorage = NSButton.CreateButton("Add from Storage…", () => { });
        fromStorage.ToolTip = "A folder in the storage you connected above";
        bindings.Command(fromStorage, library.AddFromStorageCommand);
        bindings.Property(library.Storage, nameof(StorageAccountsViewModel.HasAccounts), () => fromStorage.Hidden = !library.Storage.HasAccounts);
        form.Buttons(rescan, fromStorage, NSButton.CreateButton("Add Folder…", () => _adding.PickAsync().FireAndForget()));
        // The tour's Add a folder opens this pane for the picker.
        if (_services.GetRequiredService<LibraryFolders>().TakePickRequest())
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() => _adding.PickAsync().FireAndForget());

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
    /// <summary>
    /// Settings › AI, as Windows has it: AI on or off, scanning whole books, and where the model runs, built in or on a
    /// model server; then reading the books again, reading the whole library for meaning, and the folders it may read.
    /// </summary>
    NSScrollView AiTab()
    {
        var ai = _services.GetRequiredService<AiSettingsViewModel>();
        var bindings = Load(ai);
        var form = new Form();

        var use = form.Section("AI",
            "A model on this Mac reads a few pages of each book (its opening pages, contents, introduction and a sample of the rest) and suggests its title, game system, type, levels and more. Each suggestion has to quote the page it came from, or it's thrown away. Your own values always win.");
        var on = FormText.Switch(value =>
        {
            if (value) ai.UseAi = true;
            else ai.NoAi = true;
        });
        var useRow = use.Row("Use AI", on);
        var waiting = useRow.AddDetail();
        waiting.TextColor = NSColor.SystemOrange;
        var switchNote = useRow.AddDetail();
        var reading = NSButton.CreateButton("See What the AI Is Reading", () => ai.OpenAiReadingCommand.Execute(null));
        reading.Bordered = false;
        reading.ContentTintColor = NSColor.ControlAccent;
        reading.Font = NSFont.SystemFontOfSize(11)!;
        use.Row("AI reading", "The book the AI is reading now, and what it kept from the last ones", reading);
        var scan = FormText.Switch(value => ai.ScanWholeBooks = value);
        var scanRow = use.Row("Scan whole books",
            "Once a book is catalogued, the AI reads all of it, chapter by chapter, and notes each chapter's setting, environments, themes and creatures, with the page that shows them. It goes slowly, after everything else, and stops whenever you ask the AI something. The book's own details are never changed.",
            scan);
        var scanProgress = scanRow.AddDetail();
        var runs = NSSegmentedControl.FromLabels(["Built In", "Model Server"], NSSegmentSwitchTracking.SelectOne, () => { });
        runs.Activated += (_, _) =>
        {
            if (runs.SelectedSegment == 0) ai.UseBuiltIn = true;
            else ai.UseServer = true;
        };
        use.Row("Where the model runs", "Built in: Bibliotaph downloads a model once and runs it itself. A model server: Ollama, LM Studio or similar, which you install and run.", runs);
        bindings.Properties(ai, [nameof(AiSettingsViewModel.IsOn), nameof(AiSettingsViewModel.ProgressText), nameof(AiSettingsViewModel.SwitchNote),
            nameof(AiSettingsViewModel.SwitchProblem), nameof(AiSettingsViewModel.ScanWholeBooks), nameof(AiSettingsViewModel.ScanProgressText),
            nameof(AiSettingsViewModel.ShowBuiltIn)], () =>
        {
            FormText.Set(on, ai.IsOn);
            useRow.ShowDetail(ai.SwitchProblem ?? ai.ProgressText);
            useRow.Detail.TextColor = ai.SwitchProblem is null ? NSColor.SecondaryLabel : NSColor.SystemRed;
            switchNote.StringValue = ai.SwitchNote ?? "";
            switchNote.Hidden = ai.SwitchNote is null;
            FormText.Set(scan, ai.ScanWholeBooks);
            scanProgress.StringValue = ai.ScanProgressText;
            scanProgress.Hidden = ai.ScanProgressText.Length == 0;
            runs.SelectedSegment = ai.ShowBuiltIn ? 0 : 1;
        });
        bindings.Property(ai.Activity, nameof(LibraryActivity.AiWaiting), () =>
        {
            waiting.StringValue = ai.Activity.AiWaiting is { Length: > 0 } why ? $"Waiting: {why}" : "";
            waiting.Hidden = waiting.StringValue.Length == 0;
        });

        var builtIn = BuiltInSections(form, ai.BuiltIn, bindings);
        var server = ServerSections(form, ai, bindings);
        bindings.Property(ai, nameof(AiSettingsViewModel.ShowBuiltIn), () =>
        {
            foreach (var section in builtIn) section.View.Hidden = !ai.ShowBuiltIn;
            foreach (var section in server) section.View.Hidden = ai.ShowBuiltIn;
        });

        var again = form.Section("Reading the books again");
        var reclassify = NSButton.CreateButton("", () => { });
        bindings.Command(reclassify, ai.ReclassifyCommand);
        var reclassifyRow = again.Row("Reclassify", reclassify);
        var failed = NSButton.CreateButton("See Which Books, and Why", () => ai.OpenFailedCommand.Execute(null));
        failed.Bordered = false;
        failed.ContentTintColor = NSColor.ControlAccent;
        failed.Font = NSFont.SystemFontOfSize(11)!;
        var failedRow = again.Row("Books the AI couldn't read", failed);
        var processing = NSButton.CreateButton("Processing", () => ai.OpenProcessingCommand.Execute(null));
        processing.Bordered = false;
        processing.ContentTintColor = NSColor.ControlAccent;
        again.Row("Read every book again", "With your lists as they stand now: the button at the end of Cataloguing with AI, in Processing", processing);
        var library = FormText.Switch(value => ai.ReadLibrary = value);
        var libraryRow = again.Row("Read the whole library for meaning",
            "So a question to your whole library finds pages by what they mean, not only by their words. It runs slowly in the background, can take hours for a large library, pauses while you're using the AI, and carries on where it was after a restart.",
            library);
        var libraryProgress = libraryRow.AddDetail();
        bindings.Properties(ai, [nameof(AiSettingsViewModel.Earlier), nameof(AiSettingsViewModel.Failed), nameof(AiSettingsViewModel.SaveNote),
            nameof(AiSettingsViewModel.HasLibraryVectors), nameof(AiSettingsViewModel.LibraryVectorsText), nameof(AiSettingsViewModel.ReadLibrary)], () =>
        {
            reclassifyRow.Hidden = !ai.CanReclassify;
            reclassify.Title = ai.ReclassifyLabel;
            reclassifyRow.ShowDetail($"{ai.ReclassifyText} Values you've accepted, typed or rejected stay as they are.");
            failedRow.Hidden = !ai.HasFailed;
            libraryRow.Hidden = !ai.HasLibraryVectors;
            FormText.Set(library, ai.ReadLibrary);
            libraryProgress.StringValue = ai.LibraryVectorsText;
            libraryProgress.Hidden = ai.LibraryVectorsText.Length == 0;
            again.Note(ai.SaveNote);
        });

        var foldersSection = form.Section("Folders it may read",
            "Untick a folder to keep its books away from the model. They keep their hints from names, and you can still fill them in yourself.");
        void Folders()
        {
            foldersSection.Clear();
            foreach (var folder in ai.Folders)
            {
                var send = FormText.Switch(value => folder.Send = value);
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

    /// <summary>
    /// The built-in AI: llama.cpp's Apple silicon build, downloaded at startup, and a model downloaded once, or a .gguf
    /// file already here, such as one LM Studio keeps; then Ask this book's embedding model.
    /// </summary>
    static FormSection[] BuiltInSections(Form form, BuiltInAiViewModel builtIn, Bindings bindings)
    {
        var model = form.Section("Built-in model");
        var engineRow = model.Row("Engine", (NSView[])[]);
        var choose = new NSPopUpButton();
        // Group headings stay greyed out as headings, which the menu would otherwise turn on.
        choose.Menu!.AutoEnablesItems = false;
        choose.WidthAnchor.ConstraintEqualTo(280).Active = true;
        var items = new List<BuiltInModelItem>();
        choose.Activated += (_, _) =>
        {
            var at = (int)choose.SelectedItem.Tag;
            if (at >= 0 && at < items.Count) builtIn.Chosen = items[at];
        };
        var file = NSButton.CreateButton("Use a Model File…", () => builtIn.ChooseFileCommand.Execute(null));
        file.ToolTip = "A .gguf model file already on this Mac, used where it is";
        var modelRow = model.Row("Model", choose, file);
        var licence = NSButton.CreateButton("Read the Licence", () => builtIn.OpenLicenceCommand.Execute(null));
        licence.Bordered = false;
        licence.ContentTintColor = NSColor.ControlAccent;
        licence.Font = NSFont.SystemFontOfSize(11)!;
        var licenceRow = model.Row("Licence", licence);
        var progress = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Bar, Indeterminate = false, MinValue = 0, MaxValue = 100 };
        progress.WidthAnchor.ConstraintEqualTo(200).Active = true;
        var progressRow = model.Row("Downloading", progress);
        var download = NSButton.CreateButton("", () => { });
        download.TintProminence = NSTintProminence.Primary;
        bindings.Command(download, builtIn.DownloadCommand);
        var pause = NSButton.CreateButton("Pause", () => { });
        bindings.Command(pause, builtIn.PauseCommand);
        var cancel = NSButton.CreateButton("Cancel Download", () => { });
        bindings.Command(cancel, builtIn.CancelCommand);
        var test = NSButton.CreateButton("Test with a Book…", () => { });
        bindings.Command(test, builtIn.TestCommand);
        var delete = NSButton.CreateButton("Delete Model", () => { });
        bindings.Command(delete, builtIn.DeleteCommand);
        var actions = model.Row("", download, pause, cancel, test, delete);

        void Models()
        {
            items.Clear();
            choose.RemoveAllItems();
            string? group = null;
            foreach (var item in builtIn.Models)
            {
                if (item.Group != group)
                {
                    group = item.Group;
                    var heading = new NSMenuItem(Mac(group)) { Enabled = false };
                    choose.Menu!.AddItem(heading);
                }
                var entry = new NSMenuItem(item.StateText.Length > 0 ? $"{item.Name} — {item.StateText}" : item.Name) { Tag = items.Count, IndentationLevel = 1 };
                items.Add(item);
                choose.Menu!.AddItem(entry);
                if (item == builtIn.Chosen) choose.SelectItem(entry);
            }
        }
        var stateBindings = new Bindings();
        bindings.Add(() => stateBindings.Dispose());
        void Watch()
        {
            stateBindings.Dispose();
            stateBindings = new Bindings();
            foreach (var item in builtIn.Models) stateBindings.Property(item, nameof(BuiltInModelItem.State), Models);
        }
        bindings.Collection(builtIn.Models, _ =>
        {
            Watch();
            Models();
        });
        Watch();

        bindings.Properties(builtIn, [nameof(BuiltInAiViewModel.Chosen), nameof(BuiltInAiViewModel.IsDownloading), nameof(BuiltInAiViewModel.DownloadPercent),
            nameof(BuiltInAiViewModel.DownloadText), nameof(BuiltInAiViewModel.Problem), nameof(BuiltInAiViewModel.ShowCancel), nameof(BuiltInAiViewModel.Note),
            nameof(BuiltInAiViewModel.EngineText), nameof(BuiltInAiViewModel.FileText), nameof(BuiltInAiViewModel.DownloadLabel)], () =>
        {
            Models();
            engineRow.ShowDetail(builtIn.EngineText);
            modelRow.ShowDetail(Mac(builtIn.Chosen?.Detail ?? builtIn.FileText));
            licenceRow.Hidden = !builtIn.IsListedChosen;
            licenceRow.ShowDetail(builtIn.LicenceText);
            progressRow.Hidden = !builtIn.IsDownloading;
            progress.DoubleValue = builtIn.DownloadPercent;
            progressRow.ShowDetail(builtIn.DownloadText);
            download.Title = builtIn.DownloadLabel;
            pause.Hidden = !builtIn.IsDownloading;
            cancel.Hidden = !builtIn.ShowCancel;
            actions.ShowDetail(builtIn.Problem ?? builtIn.Note);
            actions.Detail.TextColor = builtIn.Problem is null ? NSColor.SecondaryLabel : NSColor.SystemRed;
            model.Note(Mac(builtIn.GraphicsText));
        });

        var ask = form.Section("Ask this book");
        var embeddingLicence = NSButton.CreateButton("Read the Licence", () => builtIn.OpenEmbeddingLicenceCommand.Execute(null));
        embeddingLicence.Bordered = false;
        embeddingLicence.ContentTintColor = NSColor.ControlAccent;
        embeddingLicence.Font = NSFont.SystemFontOfSize(11)!;
        var downloadEmbedding = NSButton.CreateButton("", () => { });
        bindings.Command(downloadEmbedding, builtIn.DownloadEmbeddingCommand);
        var deleteEmbedding = NSButton.CreateButton("Delete", () => { });
        bindings.Command(deleteEmbedding, builtIn.DeleteEmbeddingCommand);
        var embeddingRow = ask.Row("Passages by meaning", downloadEmbedding, deleteEmbedding);
        var embeddingLicenceRow = ask.Row("Licence", embeddingLicence);
        bindings.Properties(builtIn, [nameof(BuiltInAiViewModel.EmbeddingState)], () =>
        {
            embeddingRow.ShowDetail(builtIn.EmbeddingText);
            downloadEmbedding.Title = builtIn.EmbeddingDownloadLabel;
            downloadEmbedding.Hidden = deleteEmbedding.Hidden = !builtIn.CanOfferEmbedding;
            embeddingLicenceRow.Hidden = !builtIn.CanOfferEmbedding;
            embeddingLicenceRow.ShowDetail(builtIn.EmbeddingLicenceText);
        });
        return [model, ask];
    }

    // The built-in AI's words are Windows', where a model has to fit a graphics card's memory; a Mac's GPU shares its
    // memory with everything else, and Metal says how much of it the GPU may use.
    static string Mac(string text) => text
        .Replace("graphics cards", "GPUs", StringComparison.Ordinal)
        .Replace("graphics card", "GPU", StringComparison.Ordinal)
        .Replace("graphics memory", "GPU memory", StringComparison.Ordinal);

    /// <summary>A model server: its address, model, embedding model and key, and whether books may go to one elsewhere.</summary>
    static FormSection[] ServerSections(Form form, AiSettingsViewModel ai, Bindings bindings)
    {
        var server = form.Section("Model server",
            "Ollama is usually at http://localhost:11434 and LM Studio at http://localhost:1234. Any server that speaks the OpenAI chat API works too.");
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
        server.Row("API key", "Kept in your keychain, never in Bibliotaph's files", key, forgetKey);
        var remote = FormText.Switch(on => ai.RemoteAllowed = on);
        var remoteRow = server.Row("Send excerpts to this server", "It isn't on this Mac or your network: each book's excerpt, a few thousand words of its text, would be sent there", remote);

        var state = form.Section();
        var save = NSButton.CreateButton("Save", () => { });
        save.KeyEquivalent = "\r";
        bindings.Command(save, ai.SaveCommand);
        // Test with a Book reads one of your books with what's on screen, saved or not, and stores nothing.
        var test = NSButton.CreateButton("Test with a Book…", () => { });
        bindings.Command(test, ai.TestCommand);
        var status = state.Row("", test, save);

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
        bindings.Properties(ai, [nameof(AiSettingsViewModel.SaveNote), nameof(AiSettingsViewModel.IsOn), nameof(AiSettingsViewModel.Dirty)], () =>
        {
            status.Label.StringValue = ai.Dirty ? "Not saved yet" : ai.IsOn ? "On: books are read with this model" : "Saved";
            status.ShowDetail(ai.SaveNote);
        });
        return [server, state];
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
        var tour = form.Section("Tour");
        tour.Row("Show the tour again", "On Home, the part your library is ready for", NSButton.CreateButton("Show Tour", () => preferences.ShowTourCommand.Execute(null)));
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
