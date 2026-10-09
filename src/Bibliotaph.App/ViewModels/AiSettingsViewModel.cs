using System.Collections.ObjectModel;
using System.Globalization;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core.Metadata;
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

/// <summary>One line of a Test result: a value the model gave and the page that backs it, or one the check threw out.</summary>
public sealed record AiTestLine(string Text, string Detail);

/// <summary>
/// Settings > AI (choice 10): the model server and model, a key if the server needs one, Test, which library folders
/// are sent, and how far classification has got, with Reclassify. AI is off until an endpoint and model are saved and
/// switched on; an endpoint off this computer and network needs the user's say-so as well.
/// </summary>
public sealed partial class AiSettingsViewModel(
    AiService ai, SourceRootStore roots, SettingsStore settings, LibraryActivity activity, ILogger<AiSettingsViewModel> log) : PageViewModel
{
    /// <summary>Stops a Test still waiting on the model, when the page closes.</summary>
    Action? _stopTest;

    public override Route Route => Route.Ai;
    public override string Title => "AI";
    public override string Section => "Settings";
    public override Route? SectionRoute => Route.Settings;

    public LibraryActivity Activity { get; } = activity;

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
    [NotifyPropertyChangedFor(nameof(CanReclassify))]
    public partial long Failed { get; private set; }

    public bool CanReclassify => Earlier > 0 || Failed > 0;

    public string ReclassifyText => Earlier > 0 && Failed > 0
        ? $"{Earlier:N0} {Books(Earlier)} were classified by another model or an earlier version of Bibliotaph, and {Failed:N0} couldn't be."
        : Earlier > 0 ? $"{Earlier:N0} {Books(Earlier)} were classified by another model or an earlier version of Bibliotaph."
        : $"{Failed:N0} {Books(Failed)} couldn't be classified.";

    public string ReclassifyLabel => Earlier > 0 ? $"Reclassify {Earlier + Failed:N0}" : "Try again";

    [ObservableProperty]
    public partial bool IsTesting { get; private set; }

    [ObservableProperty]
    public partial string? TestSummary { get; private set; }

    [ObservableProperty]
    public partial string? TestProblem { get; private set; }

    public ObservableCollection<AiTestLine> TestKept { get; } = [];

    public ObservableCollection<AiTestLine> TestDropped { get; } = [];

    [ObservableProperty]
    public partial bool HasTestDropped { get; private set; }

    public override async Task LoadAsync()
    {
        var setup = ai.Setup;
        Endpoint = setup.Endpoint ?? "";
        Model = setup.Model ?? "";
        _provider = setup.Provider.Length > 0 ? setup.Provider : null;
        RemoteAllowed = setup.RemoteAllowed;
        IsOn = setup.Enabled;
        HasSavedKey = setup.Endpoint is { } saved && ai.Keys.Find(saved) is { Length: > 0 };
        Dirty = false;
        SaveNote = null;
        // Saying it is set up but the user hasn't been asked would show the first-run step again.
        await settings.SetAsync(SettingKeys.AiAsked, bool.TrueString);

        Folders.Clear();
        foreach (var root in await roots.ListAsync())
            Folders.Add(new AiFolderItem(root.Id, root.Path, !setup.SkippedRoots.Contains(root.Id), OnFolderChangedAsync));
        HasFolders = Folders.Count > 0;

        Activity.Refreshed += OnActivityRefreshed;
        await RefreshProgressAsync();
        if (setup.Endpoint is not null) _ = ConnectAsync();
    }

    public override void Unload()
    {
        Activity.Refreshed -= OnActivityRefreshed;
        _stopTest?.Invoke();
    }

    void OnActivityRefreshed(object? sender, EventArgs e) => _ = RefreshProgressAsync();

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
            var model = ai.Setup.Model;
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

    /// <summary>Saves the endpoint, model and key. A new model doesn't reclassify anything by itself (choice 10).</summary>
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
        await ai.SaveAsync(was with { Endpoint = endpoint, Model = Model.Trim(), Provider = _provider ?? "", Enabled = enabled, RemoteAllowed = RemoteAllowed });
        Endpoint = endpoint;
        IsOn = enabled;
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
        if (on && (ai.Setup.Endpoint is null || ai.Setup.Model is null))
        {
            SaveNote = "Connect to a model server and save a model first.";
            OnIsOnChanged(IsOn); // puts the switch back
            return;
        }
        IsOn = on;
        await ai.SaveAsync(ai.Setup with { Enabled = on });
        SaveNote = on ? "On. Books are classified in the background, one at a time." : "Off. Nothing is sent; your setup is kept.";
    }

    async Task OnFolderChangedAsync(AiFolderItem folder)
    {
        var skipped = ai.Setup.SkippedRoots.ToHashSet();
        if (folder.Send) skipped.Remove(folder.Id);
        else skipped.Add(folder.Id);
        await ai.SaveAsync(ai.Setup with { SkippedRoots = skipped });
    }

    bool CanTest() => HasEndpoint() && Model.Trim().Length > 0 && !IsTesting;

    /// <summary>Classifies one book with what is on screen, saved or not, and shows what it kept and threw out. Stores nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    async Task TestAsync()
    {
        if (LocalModelClient.Tidy(Endpoint) is not { } endpoint) return;
        if (IsRemote && !RemoteAllowed)
        {
            TestProblem = "Tick “Send excerpts to this server” first: the test sends a book's excerpt too.";
            return;
        }
        using var work = new CancellationTokenSource();
        _stopTest = work.Cancel;
        IsTesting = true;
        TestCommand.NotifyCanExecuteChanged();
        TestSummary = "Reading a book from your library. A model that isn't loaded yet can take a minute…";
        TestProblem = null;
        TestKept.Clear();
        TestDropped.Clear();
        HasTestDropped = false;
        try
        {
            var test = await ai.TestAsync(endpoint, Model.Trim(), work.Token);
            var seconds = test.Took.TotalSeconds.ToString("0", CultureInfo.CurrentCulture);
            if (test.Result is not { } result)
            {
                TestSummary = test.Title.Length > 0 ? $"Tried {test.Title}." : null;
                TestProblem = test.Problem;
                return;
            }
            TestSummary = result.Accepted.Count == 0
                ? $"Read {test.Title} in {seconds} s, and found nothing it could back up with a quote."
                : $"Read {test.Title} in {seconds} s. Every value below quotes the page it came from.";
            var vocabulary = await ai.VocabularyAsync();
            foreach (var claim in result.Accepted)
            {
                var value = claim.Field.Kind == FieldKind.Term && !claim.IsNewTerm
                    ? vocabulary.Label(claim.Field.Vocabulary!, claim.Value)
                    : claim.IsNewTerm ? $"{claim.Value} (new)" : claim.Value;
                TestKept.Add(new AiTestLine($"{claim.Field.Label}: {value}",
                    $"Page {ClassifierPrompt.Marker(claim.PdfPage).ToString(CultureInfo.CurrentCulture)}{(claim.FromSampling ? ", from sampled pages" : "")}: “{claim.Quote}”"));
            }
            foreach (var dropped in result.Dropped)
                TestDropped.Add(new AiTestLine($"{MetadataFields.Find(dropped.Claim.Field)?.Label ?? dropped.Claim.Field}: {dropped.Claim.Value}", dropped.Reason));
            HasTestDropped = TestDropped.Count > 0;
        }
        catch (OperationCanceledException)
        {
            TestSummary = null;
        }
        finally
        {
            _stopTest = null;
            IsTesting = false;
            TestCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    async Task ReclassifyAsync()
    {
        var queued = await ai.ReclassifyAsync();
        SaveNote = queued == 0 ? "Nothing to reclassify." : "The books go back to the AI one at a time, in the background.";
        Activity.Invalidate();
    }

    static string Books(long count) => count == 1 ? "book" : "books";
}
