using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Bibliotaph.App.Services;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>A model in Settings > AI's built-in list: its size, whether it suits this computer, and whether it's here yet.</summary>
public sealed partial class BuiltInModelItem(BuiltInModel model, bool suggested) : ObservableObject
{
    public BuiltInModel Model { get; } = model;

    public string Name => Model.Name;

    public bool IsSuggested { get; } = suggested;

    /// <summary>"4.7 GB download, for graphics cards with 7 GB or more."</summary>
    public string Detail => $"{BuiltInModelDownloads.Gb(Model.Bytes)} download, for graphics cards with {Model.GraphicsBytes >> 30} GB or more.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial BuiltInModelState State { get; set; }

    [ObservableProperty]
    public partial bool IsChosen { get; set; }

    public string StateText => State switch
    {
        BuiltInModelState.Downloaded => "Downloaded",
        BuiltInModelState.Partial => "Part downloaded",
        _ => IsSuggested ? "Suggested" : "",
    };
}

/// <summary>
/// Settings > AI's "Built in" panel (Built-in AI plan, choices 4 to 7 and 14): the models offered, the one that suits
/// this computer's graphics card picked first, Download with progress, Pause and Cancel, Delete model, and Test.
/// Pressing Download sets the built-in AI to that model, so books are read as soon as it arrives, even if Settings has
/// been closed by then; a model already downloaded is used as soon as it is chosen.
/// </summary>
public sealed partial class BuiltInAiViewModel(AiService ai, BuiltInEngine engine, BuiltInModelDownloads downloads, GraphicsMemory graphics, AiTestBox tester)
    : ObservableObject
{
    public ObservableCollection<BuiltInModelItem> Models { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LicenceText), nameof(DownloadLabel))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(CancelCommand), nameof(DeleteCommand), nameof(TestCommand))]
    public partial BuiltInModelItem? Chosen { get; set; }

    /// <summary>"llama.cpp b11552", or why there is no engine.</summary>
    public string EngineText => engine.IsInstalled ? $"Runs on {engine.Version ?? "llama.cpp"}, on this computer only." : BuiltInEngine.NotInstalled;

    public bool IsEngineInstalled => engine.IsInstalled;

    /// <summary>What Bibliotaph found out about the graphics card, which the suggestion follows.</summary>
    public string GraphicsText => graphics.Bytes is { } bytes
        ? $"This computer's graphics card has {Math.Round(bytes / (double)(1L << 30))} GB."
        : "Bibliotaph couldn't tell how much memory this computer's graphics card has, so the smaller model is suggested.";

    public string LicenceText => Chosen is { } item ? $"{item.Name} is shared by its makers under the {item.Model.Licence} licence." : "";

    public string DownloadLabel => Chosen is { } item && item.State == BuiltInModelState.Partial
        ? $"Carry on downloading ({BuiltInModelDownloads.Gb(downloads.PartialBytes(item.Model))} of {BuiltInModelDownloads.Gb(item.Model.Bytes)})"
        : $"Download{(Chosen is { } chosen ? $" ({BuiltInModelDownloads.Gb(chosen.Model.Bytes)})" : "")}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(PauseCommand), nameof(CancelCommand), nameof(DeleteCommand))]
    public partial bool IsDownloading { get; private set; }

    /// <summary>0 to 100.</summary>
    [ObservableProperty]
    public partial double DownloadPercent { get; private set; }

    [ObservableProperty]
    public partial string DownloadText { get; private set; } = "";

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>A download is running, or a part of one is kept: Cancel download shows.</summary>
    [ObservableProperty]
    public partial bool ShowCancel { get; private set; }

    [ObservableProperty]
    public partial string? Note { get; private set; }

    /// <summary>Raised after this panel saved the setup, so the page can show AI's switch as it now is.</summary>
    public event EventHandler? Saved;

    /// <summary>Whether the page is showing Built in, so a chosen model that is here is used at once.</summary>
    public bool IsShown { get; set; }

    public void Load()
    {
        var suggested = BuiltInModels.Suggest(graphics.Bytes);
        Models.Clear();
        foreach (var model in BuiltInModels.All) Models.Add(new BuiltInModelItem(model, model == suggested));
        Chosen = Models.FirstOrDefault(m => m.Model.Id == ai.Setup.BuiltInModel)
            ?? Models.FirstOrDefault(m => m.Model == downloads.Current?.Model)
            ?? Models.First(m => m.IsSuggested);
        downloads.Changed -= OnDownloadsChanged;
        downloads.Changed += OnDownloadsChanged;
        Refresh();
    }

    public void Unload() => downloads.Changed -= OnDownloadsChanged;

    void OnDownloadsChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.InvokeAsync(Refresh);

    void Refresh()
    {
        foreach (var item in Models) item.State = downloads.StateOf(item.Model);
        var current = downloads.Current;
        IsDownloading = current is not null;
        DownloadPercent = (current?.Fraction ?? 0) * 100;
        DownloadText = current is null ? ""
            : current.Checking ? $"Checking {current.Model.Name}…"
            : $"Downloading {current.Model.Name}: {BuiltInModelDownloads.Gb(current.Bytes)} of {BuiltInModelDownloads.Gb(current.Model.Bytes)}";
        if (downloads.Problem is { } problem) Problem = problem;
        OnPropertyChanged(nameof(DownloadLabel));
        ShowCancel = CanCancel();
        DownloadCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
    }

    partial void OnChosenChanged(BuiltInModelItem? value)
    {
        foreach (var item in Models) item.IsChosen = item == value;
        ShowCancel = CanCancel();
        Problem = null;
        Note = null;
        if (value is not null && IsShown && value.State == BuiltInModelState.Downloaded && ai.Setup.BuiltInModel != value.Model.Id)
            _ = UseAsync(value.Model);
    }

    [RelayCommand]
    void Choose(BuiltInModelItem item) => Chosen = item;

    /// <summary>The page switched to Built in: uses the chosen model if it's here, or the one set before if that still is.</summary>
    public Task ShowAsync()
    {
        IsShown = true;
        var model = Chosen is { State: BuiltInModelState.Downloaded } chosen ? chosen.Model.Id
            : ai.Setup.BuiltInModel is { } before && BuiltInModels.Find(before) is { } known && downloads.StateOf(known) == BuiltInModelState.Downloaded ? before
            : null;
        return SaveAsync(model);
    }

    async Task UseAsync(BuiltInModel model)
    {
        var before = ai.Setup.BuiltInModel;
        await SaveAsync(model.Id);
        Note = before is not null && before != model.Id
            ? $"Now using {model.Name}. Books already classified keep their values; Reclassify reads them again with it."
            : downloads.StateOf(model) == BuiltInModelState.Downloaded ? $"Using {model.Name}." : null;
    }

    async Task SaveAsync(string? model)
    {
        var was = ai.Setup;
        // Setting a model up for the first time switches AI on, as saving a model server does. Later it stays as it was.
        var first = model is not null && was.BuiltInModel is null && was.Model is null;
        await ai.SaveAsync(was with { BuiltIn = true, BuiltInModel = model, Enabled = first || was.Enabled });
        Saved?.Invoke(this, EventArgs.Empty);
    }

    bool CanDownload() => !IsDownloading && engine.IsInstalled && Chosen is { State: not BuiltInModelState.Downloaded };

    /// <summary>Sets the built-in AI to the chosen model and downloads it in the background.</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    async Task DownloadAsync()
    {
        if (Chosen is not { } item) return;
        Problem = null;
        Note = "Books are read as soon as the download finishes, even if Settings is closed by then.";
        await UseAsync(item.Model);
        _ = downloads.DownloadAsync(item.Model);
    }

    bool CanPause() => IsDownloading;

    [RelayCommand(CanExecute = nameof(CanPause))]
    void Pause()
    {
        downloads.Pause();
        Note = "Paused. Download carries on from where it got to, after a restart too.";
    }

    bool CanCancel() => IsDownloading || Chosen is { State: BuiltInModelState.Partial };

    [RelayCommand(CanExecute = nameof(CanCancel))]
    void Cancel()
    {
        if ((downloads.Current?.Model ?? Chosen?.Model) is not { } model) return;
        downloads.Cancel(model);
        Note = "Cancelled. What had arrived was deleted.";
    }

    bool CanDelete() => Chosen is { State: BuiltInModelState.Downloaded } item && downloads.Current?.Model != item.Model;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    async Task DeleteAsync()
    {
        if (Chosen is not { } item) return;
        if (MessageBox.Show(Application.Current.MainWindow,
                $"{item.Name} ({BuiltInModelDownloads.Gb(item.Model.Bytes)}) will be deleted from this computer. The built-in AI can't read books until a model is downloaded again.",
                "Delete this model?", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        var inUse = ai.Setup.BuiltInModel == item.Model.Id;
        // The engine holds the file open while it runs.
        await engine.StopAsync();
        try
        {
            downloads.Delete(item.Model);
        }
        catch (System.IO.IOException ex)
        {
            Problem = $"{item.Name} couldn't be deleted: {ex.Message}";
            return;
        }
        if (inUse) await SaveAsync(null);
        Note = $"Deleted {item.Name}.";
        Refresh();
    }

    bool CanTest() => engine.IsInstalled && Chosen is { State: BuiltInModelState.Downloaded };

    /// <summary>Opens Test with a book for the chosen model. The engine starts for it, which can take a little while.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    void Test()
    {
        if (Chosen is { } item) tester.ShowBuiltIn(item.Model.Id);
    }

    [RelayCommand]
    void OpenLicence()
    {
        if (Chosen is { } item) Process.Start(new ProcessStartInfo(item.Model.LicenceUrl.ToString()) { UseShellExecute = true })?.Dispose();
    }
}
