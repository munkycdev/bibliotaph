using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using Bibliotaph.App.Services;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A model in Settings > AI's built-in list: one of <see cref="BuiltInModels"/>, with its size, whether it suits this
/// computer and whether it's here yet; or a model file of the user's own, kept where it is.
/// </summary>
public sealed partial class BuiltInModelItem : ObservableObject
{
    public const string FitsGroup = "Fits your graphics card";
    public const string BiggerGroup = "Needs more graphics memory";
    public const string FileGroup = "Your own model file";

    BuiltInModelItem(BuiltInModel? model, string? file, bool suggested, bool fits)
    {
        (Model, File, IsSuggested, Fits) = (model, file, suggested, fits);
    }

    public static BuiltInModelItem For(BuiltInModel model, bool suggested, bool fits) => new(model, null, suggested, fits);

    public static BuiltInModelItem ForFile(string path) => new(null, path, false, true);

    /// <summary>The listed model, or null for a file of the user's own.</summary>
    public BuiltInModel? Model { get; }

    /// <summary>The path of a model file of the user's own, or null for a listed model.</summary>
    public string? File { get; }

    /// <summary>What settings store: the listed model's id, or the file's path.</summary>
    public string Id => Model?.Id ?? File!;

    public string Name => Model?.Name ?? System.IO.Path.GetFileNameWithoutExtension(File!);

    public bool IsSuggested { get; }

    public bool Fits { get; }

    /// <summary>The heading it is listed under.</summary>
    public string Group => File is not null ? FileGroup : Fits ? FitsGroup : BiggerGroup;

    /// <summary>"4.7 GB download, for graphics cards with 7 GB or more. Apache-2.0."</summary>
    public string Detail => Model is { } model
        ? $"{BuiltInModelDownloads.Gb(model.Bytes)} download, for graphics cards with {model.GraphicsBytes >> 30} GB or more. {model.Licence}.{(model.Note is { } note ? $" {note}" : "")}"
        : File!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial BuiltInModelState State { get; set; }

    [ObservableProperty]
    public partial bool IsChosen { get; set; }

    public string StateText => (File, State) switch
    {
        (not null, BuiltInModelState.Downloaded) => "On this computer",
        (not null, _) => "File not found",
        (_, BuiltInModelState.Downloaded) => "Downloaded",
        (_, BuiltInModelState.Partial) => "Part downloaded",
        _ => IsSuggested ? "Suggested" : "",
    };

    public override string ToString() => Name;
}

/// <summary>
/// Settings > AI's "Built in" panel: the models offered, the one that suits this computer's graphics card picked
/// first, Download with progress, Pause and Cancel, Delete model, and Test. Pressing Download sets the built-in AI to
/// that model, so books are read as soon as it arrives, even if Settings has been closed by then; a model already
/// downloaded is used as soon as it is chosen.
/// </summary>
public sealed partial class BuiltInAiViewModel(
    AiService ai, BuiltInEngine engine, BuiltInEngineDownload engineDownload, BuiltInModelDownloads downloads, GraphicsMemory graphics, AiTestBox tester,
    [FromKeyedServices(BuiltInEngine.EmbeddingKey)] BuiltInEngine embeddingEngine)
    : ObservableObject
{
    // Ask this book's embedding model, downloaded like a chat model and run by a second engine.

    readonly BuiltInModel _embedding = BuiltInModels.Embedding;

    /// <summary>The embedding model can be downloaded: its file is pinned. Until then Ask finds passages by their words.</summary>
    public bool CanOfferEmbedding => _embedding.IsPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmbeddingText), nameof(EmbeddingDownloadLabel))]
    [NotifyCanExecuteChangedFor(nameof(DownloadEmbeddingCommand), nameof(DeleteEmbeddingCommand))]
    public partial BuiltInModelState EmbeddingState { get; private set; }

    public string EmbeddingText
    {
        get
        {
            var model = BuiltInModels.Embedding;
            if (!model.IsPinned)
                return "Ask this book finds passages by their words. Finding them by what they mean as well needs an embedding model, which comes from a model server rather than the built-in AI.";
            return EmbeddingState == BuiltInModelState.Downloaded
                ? $"{model.Name} is downloaded, so Ask this book finds passages by what they mean as well as by their words."
                : $"Download {model.Name} ({BuiltInModelDownloads.Gb(model.Bytes)}) so Ask this book finds passages by what they mean as well as by their words.";
        }
    }

    public string EmbeddingLicenceText => $"{_embedding.Name} is shared by its makers under the {_embedding.Licence} licence.";

    public string EmbeddingDownloadLabel => EmbeddingState == BuiltInModelState.Partial ? "Carry on downloading the embedding model" : "Download the embedding model";

    bool CanDownloadEmbedding() => CanOfferEmbedding && !IsDownloading && EmbeddingState != BuiltInModelState.Downloaded;

    /// <summary>Sets Ask to use the embedding model once it is here, and downloads it in the background.</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadEmbedding))]
    async Task DownloadEmbeddingAsync()
    {
        var model = BuiltInModels.Embedding;
        Problem = null;
        await ai.SaveAsync(ai.Setup with { BuiltInEmbeddingModel = model.Id });
        Note = "Ask this book finds passages by meaning as soon as the download finishes.";
        _ = downloads.DownloadAsync(model);
    }

    bool CanDeleteEmbedding() => EmbeddingState == BuiltInModelState.Downloaded && downloads.Current?.Model != BuiltInModels.Embedding;

    [RelayCommand(CanExecute = nameof(CanDeleteEmbedding))]
    async Task DeleteEmbeddingAsync()
    {
        var model = BuiltInModels.Embedding;
        // The embedding engine holds the file open while it runs.
        await embeddingEngine.StopAsync();
        try
        {
            downloads.Delete(model);
        }
        catch (System.IO.IOException ex)
        {
            Problem = $"{model.Name} couldn't be deleted: {ex.Message}";
            return;
        }
        await ai.SaveAsync(ai.Setup with { BuiltInEmbeddingModel = null });
        Note = $"Deleted {model.Name}. Ask this book finds passages by their words.";
        Refresh();
    }

    [RelayCommand]
    void OpenEmbeddingLicence() =>
        Process.Start(new ProcessStartInfo(_embedding.LicenceUrl.ToString()) { UseShellExecute = true })?.Dispose();

    public ObservableCollection<BuiltInModelItem> Models { get; } = [];

    /// <summary>The models as the list shows them, under their headings: those that fit this computer's graphics card first.</summary>
    public ICollectionView ModelList => field ??= Grouped(Models);

    static ListCollectionView Grouped(ObservableCollection<BuiltInModelItem> models)
    {
        var view = new ListCollectionView(models);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BuiltInModelItem.Group)));
        return view;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LicenceText), nameof(DownloadLabel), nameof(IsListedChosen), nameof(FileText))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(CancelCommand), nameof(DeleteCommand), nameof(TestCommand))]
    public partial BuiltInModelItem? Chosen { get; set; }

    /// <summary>"llama.cpp b11552", or why there is no engine yet.</summary>
    public string EngineText =>
        engine.IsInstalled ? $"Runs on {engine.Version ?? "llama.cpp"}, on this computer only."
        : engineDownload.IsDownloading ? "Downloading the built-in AI engine…"
        : engineDownload.Problem ?? BuiltInEngine.NotInstalled;

    public bool IsEngineInstalled => engine.IsInstalled;

    /// <summary>What Bibliotaph found out about the graphics card, which the suggestion follows.</summary>
    public string GraphicsText => graphics.Bytes is { } bytes
        ? $"This computer's graphics card has {Math.Round(bytes / (double)(1L << 30))} GB."
        : "Bibliotaph couldn't tell how much memory this computer's graphics card has, so the smaller model is suggested.";

    public string LicenceText => Chosen is { Model: { } model } item ? $"{item.Name} is shared by its makers under the {model.Licence} licence." : "";

    /// <summary>A listed model is chosen, so it has a licence to read and a Download button.</summary>
    public bool IsListedChosen => Chosen is { Model: not null };

    /// <summary>A file of the user's own is chosen: what Bibliotaph does and doesn't know about it.</summary>
    public string FileText => Chosen is { File: not null } item
        ? item.State == BuiltInModelState.Downloaded
            ? "Bibliotaph uses this file where it is and never changes or deletes it. Its licence is whatever its makers set."
            : "This file isn't there any more. Choose it again, or choose another model."
        : "";

    public string DownloadLabel => Chosen is { Model: { } model, State: BuiltInModelState.Partial }
        ? $"Carry on downloading ({BuiltInModelDownloads.Gb(downloads.PartialBytes(model))} of {BuiltInModelDownloads.Gb(model.Bytes)})"
        : $"Download{(Chosen is { Model: { } chosen } ? $" ({BuiltInModelDownloads.Gb(chosen.Bytes)})" : "")}";

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
        foreach (var model in BuiltInModels.Offered)
            Models.Add(BuiltInModelItem.For(model, model == suggested, BuiltInModels.Fits(model, graphics.Bytes)));
        if (ai.Setup.BuiltInModel is { } file && BuiltInModels.IsFile(file)) Models.Add(BuiltInModelItem.ForFile(file));
        Chosen = Models.FirstOrDefault(m => m.Id == ai.Setup.BuiltInModel)
            ?? Models.FirstOrDefault(m => m.Model is not null && m.Model == downloads.Current?.Model)
            ?? Models.First(m => m.IsSuggested);
        downloads.Changed -= OnDownloadsChanged;
        downloads.Changed += OnDownloadsChanged;
        engineDownload.Changed -= OnDownloadsChanged;
        engineDownload.Changed += OnDownloadsChanged;
        Refresh();
    }

    public void Unload()
    {
        downloads.Changed -= OnDownloadsChanged;
        engineDownload.Changed -= OnDownloadsChanged;
    }

    void OnDownloadsChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.InvokeAsync(Refresh);

    void Refresh()
    {
        foreach (var item in Models)
            item.State = item.Model is { } model ? downloads.StateOf(model)
                : System.IO.File.Exists(item.File) ? BuiltInModelState.Downloaded : BuiltInModelState.None;
        OnPropertyChanged(nameof(FileText));
        EmbeddingState = downloads.StateOf(BuiltInModels.Embedding);
        var current = downloads.Current;
        IsDownloading = current is not null;
        DownloadPercent = (current?.Fraction ?? 0) * 100;
        DownloadText = current is null ? ""
            : current.Checking ? $"Checking {current.Model.Name}…"
            : $"Downloading {current.Model.Name}: {BuiltInModelDownloads.Gb(current.Bytes)} of {BuiltInModelDownloads.Gb(current.Model.Bytes)}";
        if (downloads.Problem is { } problem) Problem = problem;
        OnPropertyChanged(nameof(DownloadLabel));
        OnPropertyChanged(nameof(EngineText));
        OnPropertyChanged(nameof(IsEngineInstalled));
        ShowCancel = CanCancel();
        DownloadCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
        DownloadEmbeddingCommand.NotifyCanExecuteChanged();
        DeleteEmbeddingCommand.NotifyCanExecuteChanged();
    }

    partial void OnChosenChanged(BuiltInModelItem? value)
    {
        foreach (var item in Models) item.IsChosen = item == value;
        ShowCancel = CanCancel();
        Problem = null;
        Note = null;
        if (value is not null && IsShown && value.State == BuiltInModelState.Downloaded && ai.Setup.BuiltInModel != value.Id)
            _ = UseAsync(value);
    }

    /// <summary>Uses a .gguf model file already on this computer, such as one LM Studio downloaded, where it is.</summary>
    [RelayCommand]
    void ChooseFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Use a model file on this computer", Filter = "Model files (*.gguf)|*.gguf" };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        ChooseFile(dialog.FileName);
    }

    /// <summary>Lists <paramref name="path"/> as the user's own model, in place of any chosen before, and chooses it.</summary>
    public void ChooseFile(string path)
    {
        if (Models.FirstOrDefault(m => m.File is not null) is { } before) Models.Remove(before);
        var item = BuiltInModelItem.ForFile(path);
        Models.Add(item);
        Refresh();
        Chosen = item;
    }

    /// <summary>The page switched to Built in: uses the chosen model if it's here, or the one set before if that still is.</summary>
    public Task ShowAsync()
    {
        IsShown = true;
        var model = Chosen is { State: BuiltInModelState.Downloaded } chosen ? chosen.Id
            : ai.Setup.BuiltInModel is { } before && BuiltInModels.Find(before) is { } known && downloads.StateOf(known) == BuiltInModelState.Downloaded ? before
            : ai.Setup.BuiltInModel is { } file && BuiltInModels.IsFile(file) && System.IO.File.Exists(file) ? file
            : null;
        return SaveAsync(model);
    }

    async Task UseAsync(BuiltInModelItem item)
    {
        var before = ai.Setup.BuiltInModel;
        await SaveAsync(item.Id);
        Note = before is not null && before != item.Id
            ? $"Now using {item.Name}. Books already classified keep their values; Reclassify reads them again with it."
            : item.State == BuiltInModelState.Downloaded ? $"Using {item.Name}." : null;
    }

    async Task SaveAsync(string? model)
    {
        var was = ai.Setup;
        // Setting a model up for the first time switches AI on, as saving a model server does. Later it stays as it was.
        var first = model is not null && was.BuiltInModel is null && was.Model is null;
        await ai.SaveAsync(was with { BuiltIn = true, BuiltInModel = model, Enabled = first || was.Enabled });
        Saved?.Invoke(this, EventArgs.Empty);
    }

    // The model can download while the engine still is: the lane waits for both.
    bool CanDownload() => !IsDownloading && Chosen is { Model: not null, State: not BuiltInModelState.Downloaded };

    /// <summary>Sets the built-in AI to the chosen model and downloads it in the background.</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    async Task DownloadAsync()
    {
        if (Chosen is not { Model: { } model } item) return;
        Problem = null;
        Note = "Books are read as soon as the download finishes, even if Settings is closed by then.";
        await UseAsync(item);
        _ = downloads.DownloadAsync(model);
    }

    bool CanPause() => IsDownloading;

    [RelayCommand(CanExecute = nameof(CanPause))]
    void Pause()
    {
        downloads.Pause();
        Note = "Paused. Download carries on from where it got to, after a restart too.";
    }

    bool CanCancel() => IsDownloading || Chosen is { Model: not null, State: BuiltInModelState.Partial };

    [RelayCommand(CanExecute = nameof(CanCancel))]
    void Cancel()
    {
        if ((downloads.Current?.Model ?? Chosen?.Model) is not { } model) return;
        downloads.Cancel(model);
        Note = "Cancelled. What had arrived was deleted.";
    }

    // A file of the user's own is never deleted: Bibliotaph didn't put it there.
    bool CanDelete() => Chosen is { Model: { } model, State: BuiltInModelState.Downloaded } && downloads.Current?.Model != model;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    async Task DeleteAsync()
    {
        if (Chosen is not { Model: { } model } item) return;
        if (MessageBox.Show(Application.Current.MainWindow,
                $"{item.Name} ({BuiltInModelDownloads.Gb(model.Bytes)}) will be deleted from this computer. The built-in AI can't read books until a model is downloaded again.",
                "Delete this model?", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        var inUse = ai.Setup.BuiltInModel == model.Id;
        // The engine holds the file open while it runs.
        await engine.StopAsync();
        try
        {
            downloads.Delete(model);
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
        if (Chosen is { } item) tester.ShowBuiltIn(item.Id);
    }

    [RelayCommand]
    void OpenLicence()
    {
        if (Chosen is { Model: { } model }) Process.Start(new ProcessStartInfo(model.LicenceUrl.ToString()) { UseShellExecute = true })?.Dispose();
    }
}
