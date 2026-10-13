using System.Collections.ObjectModel;
using System.Windows;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>A library folder in Settings > AI, with whether its books are sent to the model.</summary>
public sealed partial class AiFolderItem(long id, string path, bool send, Func<AiFolderItem, Task> changed) : ObservableObject
{
    public long Id { get; } = id;

    public string Path { get; } = path;

    [ObservableProperty]
    public partial bool Send { get; set; } = send;

    partial void OnSendChanged(bool value) => _ = changed(this);
}

/// <summary>
/// Settings > AI: Built in or a model server; for a server, its address and model, a key if it needs one, and Test;
/// which library folders are sent, and how far classification has got, with Reclassify; with an embedding model, how far
/// the library has been read for meaning, which can be switched off. AI is off until a model is
/// set up and switched on; an endpoint off this computer and network needs the user's say-so as well.
/// </summary>
public sealed partial class AiSettingsViewModel(
    AiService ai, AiTestBox tester, SourceRootStore roots, SettingsStore settings, LibraryActivity activity, PilotMode pilotMode,
    PilotPanelViewModel pilot, BuiltInAiViewModel builtIn, SettingsLinks links, LibraryVectors libraryVectors, ILogger<AiSettingsViewModel> log)
    : SettingsSectionViewModel
{
    public override SettingsSection Section => SettingsSection.Ai;
    public override string Label => "AI";

    public LibraryActivity Activity { get; } = activity;

    /// <summary>The model pilot's section, shown only when the app started with --pilot.</summary>
    public PilotPanelViewModel Pilot { get; } = pilot;

    public bool ShowPilot => pilotMode.IsOn;

    /// <summary>The Built in panel: its models, download and Test.</summary>
    public BuiltInAiViewModel BuiltIn { get; } = builtIn;

    /// <summary>
    /// The page shows Built in rather than the model server: when the built-in AI is in use, and for anyone who hasn't
    /// set AI up at all. Showing it changes nothing until a model is chosen or downloaded.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowBuiltIn { get; private set; }

    partial void OnShowBuiltInChanged(bool value)
    {
        BuiltIn.IsShown = value;
        OnPropertyChanged(nameof(UseBuiltIn));
        OnPropertyChanged(nameof(UseServer));
    }

    /// <summary>The Built in side of the choice. Switching keeps the model server's settings for a switch back.</summary>
    public bool UseBuiltIn
    {
        get => ShowBuiltIn;
        set { if (value && !ShowBuiltIn) _ = SwitchToBuiltInAsync(); }
    }

    /// <summary>The Use a model server side. Switching keeps the built-in model, downloaded, for a switch back.</summary>
    public bool UseServer
    {
        get => !ShowBuiltIn;
        set { if (value && ShowBuiltIn) _ = SwitchToServerAsync(); }
    }

    async Task SwitchToBuiltInAsync()
    {
        ShowBuiltIn = true;
        SwitchProblem = null;
        SaveNote = null;
        await BuiltIn.ShowAsync();
    }

    async Task SwitchToServerAsync()
    {
        ShowBuiltIn = false;
        SwitchProblem = null;
        if (ai.Setup.BuiltIn) await ai.SaveAsync(ai.Setup with { BuiltIn = false });
        IsOn = ai.Setup.Enabled;
        await RefreshProgressAsync();
        if (ai.Setup.Endpoint is not null && !HasModels) _ = ConnectAsync();
    }

    void OnBuiltInSaved(object? sender, EventArgs e)
    {
        IsOn = ai.Setup.Enabled;
        SwitchProblem = null;
        _ = RefreshProgressAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemote))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(SaveCommand), nameof(TestCommand))]
    public partial string Endpoint { get; set; } = "";

    partial void OnEndpointChanged(string value)
    {
        Models.Clear();
        HasModels = false;
        ConnectionText = "";
        ConnectionProblem = null;
        _provider = null;
        Dirty = true;
    }

    string? _provider;

    public ObservableCollection<string> Models { get; } = [];

    /// <summary>The server listed its models, so the model is picked from them; otherwise it is typed.</summary>
    [ObservableProperty]
    public partial bool HasModels { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    public partial string Model { get; set; } = "";

    partial void OnModelChanged(string value) => Dirty = true;

    /// <summary>
    /// The server's embedding model, which Ask this book finds passages by meaning with, or empty for none, and Ask
    /// finds them by their words.
    /// </summary>
    [ObservableProperty]
    public partial string EmbeddingModel { get; set; } = "";

    partial void OnEmbeddingModelChanged(string value) => Dirty = true;

    /// <summary>None, then the server's models: an embedding model is listed with the rest.</summary>
    public ObservableCollection<Choice<string>> EmbeddingChoices { get; } = [new("", NoEmbedding)];

    const string NoEmbedding = "None: find passages by their words";

    /// <summary>Lists the server's models after None, keeping the saved one even when the server no longer lists it.</summary>
    void ListEmbeddingChoices(IEnumerable<string> models)
    {
        var keep = EmbeddingModel;
        EmbeddingChoices.Clear();
        EmbeddingChoices.Add(new("", NoEmbedding));
        foreach (var model in models) EmbeddingChoices.Add(new(model, model));
        if (keep.Length > 0 && EmbeddingChoices.All(c => c.Value != keep)) EmbeddingChoices.Insert(1, new(keep, keep));
        var dirty = Dirty;
        EmbeddingModel = keep;
        Dirty = dirty;
    }

    /// <summary>A key typed but not saved yet; the saved one is never read back into the page.</summary>
    public string NewKey { get; set; } = "";

    [ObservableProperty]
    public partial bool HasSavedKey { get; private set; }

    [ObservableProperty]
    public partial string ConnectionText { get; private set; } = "";

    [ObservableProperty]
    public partial string? ConnectionProblem { get; private set; }

    [ObservableProperty]
    public partial bool IsConnecting { get; private set; }

    /// <summary>The endpoint isn't on this computer or its network, so its books' excerpts would leave them.</summary>
    public bool IsRemote => Endpoint.Trim().Length > 0 && !LocalModelClient.IsLocal(Endpoint);

    [ObservableProperty]
    public partial bool RemoteAllowed { get; set; }

    partial void OnRemoteAllowedChanged(bool value) => Dirty = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool Dirty { get; private set; }

    [ObservableProperty]
    public partial string? SaveNote { get; private set; }

    /// <summary>AI is switched on: books are sent as the Classify lane gets to them.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; private set; }

    /// <summary>What the switch just did, under it.</summary>
    [ObservableProperty]
    public partial string? SwitchNote { get; private set; }

    /// <summary>Why the switch can't turn on yet, under it.</summary>
    [ObservableProperty]
    public partial string? SwitchProblem { get; private set; }

    /// <summary>Asks the page to put the cursor in the address box: where setting up starts.</summary>
    public event EventHandler? FocusEndpointRequested;

    public ObservableCollection<AiFolderItem> Folders { get; } = [];

    [ObservableProperty]
    public partial bool HasFolders { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = "";

    /// <summary>Books an earlier model or prompt classified, which Reclassify would read again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReclassify))]
    public partial int Earlier { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReclassify), nameof(HasFailed))]
    public partial long Failed { get; private set; }

    public bool HasFailed => Failed > 0;

    public bool CanReclassify => Earlier > 0 || Failed > 0;

    public string ReclassifyText => Earlier > 0 && Failed > 0
        ? $"{Earlier:N0} {Books(Earlier)} were classified by another model or an earlier version of Bibliotaph, and {Failed:N0} couldn't be."
        : Earlier > 0 ? $"{Earlier:N0} {Books(Earlier)} were classified by another model or an earlier version of Bibliotaph."
        : $"{Failed:N0} {Books(Failed)} couldn't be classified.";

    public string ReclassifyLabel => Earlier > 0 ? $"Reclassify {Earlier + Failed:N0}" : "Try again";

    public override async Task LoadAsync()
    {
        var setup = ai.Setup;
        Endpoint = setup.Endpoint ?? "";
        Model = setup.Model ?? "";
        EmbeddingModel = setup.EmbeddingModel ?? "";
        ListEmbeddingChoices([]);
        _provider = setup.Provider.Length > 0 ? setup.Provider : null;
        RemoteAllowed = setup.RemoteAllowed;
        IsOn = setup.Enabled;
        HasSavedKey = setup.Endpoint is { } saved && ai.Keys.Find(saved) is { Length: > 0 };
        ShowBuiltIn = setup.BuiltIn || (setup.Endpoint is null && setup.Model is null);
        BuiltIn.Saved -= OnBuiltInSaved;
        BuiltIn.Saved += OnBuiltInSaved;
        BuiltIn.Load();
        Dirty = false;
        SaveNote = null;
        // Saying it is set up but the user hasn't been asked would show the first-run step again.
        await settings.SetAsync(SettingKeys.AiAsked, bool.TrueString);

        Folders.Clear();
        foreach (var root in await roots.ListAsync())
            Folders.Add(new AiFolderItem(root.Id, root.Path, !setup.SkippedRoots.Contains(root.Id), OnFolderChangedAsync));
        HasFolders = Folders.Count > 0;

        // Choosing the section that is showing loads it again, without an Unload in between.
        Activity.Refreshed -= OnActivityRefreshed;
        Activity.Refreshed += OnActivityRefreshed;
        libraryVectors.Changed -= OnLibraryVectorsChanged;
        libraryVectors.Changed += OnLibraryVectorsChanged;
        ShowLibraryVectors();
        await RefreshProgressAsync();
        if (ShowPilot) await Pilot.LoadAsync();
        if (setup.Endpoint is not null && !ShowBuiltIn) _ = ConnectAsync();
    }

    public override void Unload()
    {
        Activity.Refreshed -= OnActivityRefreshed;
        libraryVectors.Changed -= OnLibraryVectorsChanged;
        BuiltIn.Saved -= OnBuiltInSaved;
        BuiltIn.Unload();
        Pilot.Unload();
    }

    void OnActivityRefreshed(object? sender, EventArgs e) => _ = RefreshProgressAsync();

    void OnLibraryVectorsChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.InvokeAsync(ShowLibraryVectors);

    /// <summary>With an embedding model set, the library is read for meaning in the background, and this section says how far it has got.</summary>
    [ObservableProperty]
    public partial bool HasLibraryVectors { get; private set; }

    /// <summary>"Read the whole library for meaning": on unless switched off. Switching it saves at once.</summary>
    public bool ReadLibrary
    {
        get => ai.Setup.LibraryVectors;
        set
        {
            if (value == ai.Setup.LibraryVectors) return;
            _ = SaveReadLibraryAsync(value);
        }
    }

    async Task SaveReadLibraryAsync(bool on)
    {
        try
        {
            await ai.SaveAsync(ai.Setup with { LibraryVectors = on });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogError(ex, "Saving whether to read the library for meaning failed");
        }
        OnPropertyChanged(nameof(ReadLibrary));
        ShowLibraryVectors();
    }

    /// <summary>"12,345 of 98,765 pages read for meaning (13%). Waiting while the AI is in use."</summary>
    [ObservableProperty]
    public partial string LibraryVectorsText { get; private set; } = "";

    void ShowLibraryVectors()
    {
        var status = libraryVectors.Status;
        HasLibraryVectors = status.State != LibraryVectorsState.Unavailable;
        OnPropertyChanged(nameof(ReadLibrary));
        if (!HasLibraryVectors) return;
        var share = status.Pages == 0 ? "" : $" ({(double)status.WithVectors / status.Pages:P0})";
        var counted = $"{status.WithVectors:N0} of {status.Pages:N0} pages read for meaning{share}.";
        LibraryVectorsText = status.State switch
        {
            LibraryVectorsState.Off => counted + " Switched off: questions to the library find the other pages by their words.",
            LibraryVectorsState.Reading => counted + " Reading the rest in the background.",
            LibraryVectorsState.Waiting => counted + " Waiting while the AI is in use.",
            LibraryVectorsState.Done => counted + " Every page is read.",
            LibraryVectorsState.Stopped => counted + $" Stopped for now: {status.Problem ?? "the model server didn't answer"}. It tries again in a few minutes.",
            _ => counted,
        };
    }

    async Task RefreshProgressAsync()
    {
        try
        {
            var progress = Activity.Progress;
            var summary = await ai.SummarizeAsync();
            Earlier = summary.Earlier;
            Failed = progress.ClassifyFailed;
            OnPropertyChanged(nameof(ReclassifyText));
            OnPropertyChanged(nameof(ReclassifyLabel));
            var model = ai.Setup.ModelName;
            ProgressText = model is null || !ai.Setup.Enabled
                ? $"{summary.Current + summary.Earlier:N0} {Books(summary.Current + summary.Earlier)} classified so far."
                : progress.ToClassify > 0
                    ? $"{summary.Current:N0} {Books(summary.Current)} classified with {model}; {progress.ToClassify:N0} waiting."
                    : $"{summary.Current:N0} {Books(summary.Current)} classified with {model}. Nothing is waiting.";
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not count classified books");
        }
    }

    bool HasEndpoint() => Endpoint.Trim().Length > 0;

    /// <summary>Finds out what is at the endpoint and lists its models.</summary>
    [RelayCommand(CanExecute = nameof(HasEndpoint))]
    async Task ConnectAsync()
    {
        if (LocalModelClient.Tidy(Endpoint) is not { } endpoint)
        {
            ConnectionProblem = "An endpoint is an address like http://localhost:11434.";
            return;
        }
        IsConnecting = true;
        ConnectionProblem = null;
        ConnectionText = "Connecting…";
        try
        {
            var keep = Model;
            var info = await ai.ConnectAsync(endpoint);
            _provider = info.Provider;
            Models.Clear();
            foreach (var model in info.Models) Models.Add(model);
            // A model the server no longer lists stays as typed, so the user sees what is saved.
            if (keep.Length > 0 && !Models.Contains(keep)) Models.Insert(0, keep);
            HasModels = Models.Count > 0;
            ListEmbeddingChoices(info.Models);
            var dirty = Dirty;
            Model = keep.Length > 0 ? keep : (info.Models.Count > 0 ? info.Models[0] : "");
            Dirty = dirty || keep.Length == 0;
            var server = info.Kind == EndpointKind.Ollama ? $"Ollama{(info.Version is { } v ? " " + v : "")}" : "an OpenAI-compatible server";
            ConnectionText = info.Models.Count == 0
                ? $"Found {server}, but it has no models. Download one there first."
                : $"Found {server} with {info.Models.Count:N0} {(info.Models.Count == 1 ? "model" : "models")}.";
        }
        catch (ModelEndpointException ex)
        {
            ConnectionText = "";
            ConnectionProblem = ex.Message;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    bool CanSave() => Dirty && HasEndpoint() && Model.Trim().Length > 0;

    /// <summary>Saves the endpoint, model and key. A new model doesn't reclassify anything by itself.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    async Task SaveAsync()
    {
        if (LocalModelClient.Tidy(Endpoint) is not { } endpoint)
        {
            ConnectionProblem = "An endpoint is an address like http://localhost:11434.";
            return;
        }
        if (IsRemote && !RemoteAllowed)
        {
            SaveNote = "Tick “Send excerpts to this server” first, or use a server on this computer.";
            return;
        }
        if (_provider is null) await ConnectAsync();
        if (NewKey.Length > 0)
        {
            ai.Keys.Remember(endpoint, NewKey);
            NewKey = "";
            OnPropertyChanged(nameof(NewKey));
            HasSavedKey = true;
        }
        var was = ai.Setup;
        // A first save switches AI on: that's what saving a model is for. Later saves keep it as it was.
        var enabled = was.Endpoint is null || was.Model is null || was.Enabled;
        // Saving a model server means using it, instead of the built-in AI.
        await ai.SaveAsync(was with
        {
            Endpoint = endpoint,
            Model = Model.Trim(),
            EmbeddingModel = EmbeddingModel.Trim(),
            Provider = _provider ?? "",
            Enabled = enabled,
            RemoteAllowed = RemoteAllowed,
            BuiltIn = false,
        });
        Endpoint = endpoint;
        IsOn = enabled;
        SwitchProblem = null;
        Dirty = false;
        SaveNote = was.Model is { } old && old != Model.Trim()
            ? "Saved. Books already classified keep their values; Reclassify reads them again with this model."
            : enabled ? "Saved. Books are classified in the background, one at a time." : "Saved.";
        await RefreshProgressAsync();
    }

    [RelayCommand]
    void ForgetKey()
    {
        if (LocalModelClient.Tidy(Endpoint) is { } endpoint) ai.Keys.Forget(endpoint);
        HasSavedKey = false;
    }

    /// <summary>The On side of the switch: sends books again once there is a saved endpoint and model.</summary>
    public bool UseAi
    {
        get => IsOn;
        set { if (value && !IsOn) _ = SwitchAsync(true); }
    }

    /// <summary>The Off side: keeps the setup, sends nothing.</summary>
    public bool NoAi
    {
        get => !IsOn;
        set { if (value && IsOn) _ = SwitchAsync(false); }
    }

    partial void OnIsOnChanged(bool value)
    {
        OnPropertyChanged(nameof(UseAi));
        OnPropertyChanged(nameof(NoAi));
    }

    async Task SwitchAsync(bool on)
    {
        if (on && ShowBuiltIn && (!ai.Setup.BuiltIn || ai.Setup.BuiltInModel is null))
        {
            await Task.Yield();
            OnIsOnChanged(IsOn);
            SwitchNote = null;
            SwitchProblem = "Download a model below first. Downloading turns AI on.";
            return;
        }
        if (on && !ShowBuiltIn && (ai.Setup.Endpoint is null || ai.Setup.Model is null))
        {
            // The radio button has already checked itself; putting it back has to wait until its click is over.
            await Task.Yield();
            OnIsOnChanged(IsOn);
            SwitchNote = null;
            SwitchProblem = "Connect to a model server and pick a model first. Saving turns AI on.";
            FocusEndpointRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        IsOn = on;
        SwitchProblem = null;
        await ai.SaveAsync(ai.Setup with { Enabled = on });
        SwitchNote = on ? "On. Books are classified in the background, one at a time." : "Off. Nothing is sent; your setup is kept.";
    }

    async Task OnFolderChangedAsync(AiFolderItem folder)
    {
        var skipped = ai.Setup.SkippedRoots.ToHashSet();
        if (folder.Send) skipped.Remove(folder.Id);
        else skipped.Add(folder.Id);
        await ai.SaveAsync(ai.Setup with { SkippedRoots = skipped });
    }

    bool CanTest() => HasEndpoint() && Model.Trim().Length > 0;

    /// <summary>Opens Test with a book for what is on screen, saved or not. The test stores nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    void Test()
    {
        if (LocalModelClient.Tidy(Endpoint) is not { } endpoint)
        {
            ConnectionProblem = "An endpoint is an address like http://localhost:11434.";
            return;
        }
        if (IsRemote && !RemoteAllowed)
        {
            SaveNote = "Tick “Send excerpts to this server” first: the test sends a book's excerpt too.";
            return;
        }
        tester.Show(endpoint, Model.Trim());
    }

    /// <summary>Opens AI reading: the book being read now and the last ones read.</summary>
    [RelayCommand]
    void OpenAiReading() => links.OpenAiReading();

    /// <summary>The books the AI couldn't read, and why, in Needs review's Files needing attention.</summary>
    [RelayCommand]
    void OpenFailed() => links.OpenFilesNeedingAttention();

    [RelayCommand]
    async Task ReclassifyAsync()
    {
        var queued = await ai.ReclassifyAsync();
        SaveNote = queued == 0 ? "Nothing to reclassify." : "The books go back to the AI one at a time, in the background.";
        Activity.Invalidate();
    }

    static string Books(long count) => count == 1 ? "book" : "books";
}
