using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Bibliotaph.App.Services;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Check a download: a folder or ZIP checked against the library, file by file, with the results grouped by verdict,
/// New first. One page for the whole app, so a check keeps running while you go back to the library, and its results
/// are still here when you come back.
/// </summary>
public sealed partial class DownloadCheckViewModel(DownloadCheck check, ReaderWindows readers, IFilePicker files, IClipboard clipboard, IDesktopShell shell,
    ILogger<DownloadCheckViewModel> log) : PageViewModel
{
    /// <summary>Stops the running check.</summary>
    Action? _cancel;
    int _checked;
    int _total;
    bool _stopped;

    public override Route Route => Route.DownloadCheck;

    public override string Title => "Check a download";

    public override string Section => "Library";

    public override Route? SectionRoute => Route.Library;

    /// <summary>The verdicts so far, New first; a verdict with no files has no group.</summary>
    public ObservableCollection<CheckGroupViewModel> Groups { get; } = [];

    /// <summary>The folder or ZIP being checked, or last checked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    public partial string? Target { get; private set; }

    public bool HasTarget => Target is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(CheckFolderCommand), nameof(CheckZipCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>"Checked 12 of 300 files", while it runs and after.</summary>
    [ObservableProperty]
    public partial string Progress { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    [ObservableProperty]
    public partial string? Copied { get; private set; }

    public bool HasResults => Groups.Count > 0;

    [RelayCommand(CanExecute = nameof(CanStart))]
    Task CheckFolderAsync() =>
        files.PickFolder("Check a folder against your library") is { } folder ? StartAsync(folder) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStart))]
    Task CheckZipAsync() =>
        files.OpenFile("Check a ZIP against your library", "ZIP files (*.zip)|*.zip") is { } zip ? StartAsync(zip) : Task.CompletedTask;

    bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    void Stop()
    {
        _stopped = true;
        _cancel?.Invoke();
    }

    /// <summary>Checks a folder or ZIP, replacing the last results. Finishes when the check does, or is stopped.</summary>
    public async Task StartAsync(string path)
    {
        if (IsRunning) return;
        Target = path;
        Groups.Clear();
        OnPropertyChanged(nameof(HasResults));
        CopyListCommand.NotifyCanExecuteChanged();
        Problem = null;
        Copied = null;
        _checked = _total = 0;
        _stopped = false;
        IsRunning = true;
        Progress = "Looking for PDFs and images…";
        using var stop = new CancellationTokenSource();
        _cancel = stop.Cancel;
        try
        {
            var items = await Task.Run(() => check.List(path, stop.Token), stop.Token);
            _total = items.Count;
            UpdateProgress();
            await using var run = await check.StartAsync(path, stop.Token);
            foreach (var item in items)
            {
                stop.Token.ThrowIfCancellationRequested();
                Add(await Task.Run(() => run.CheckAsync(item, stop.Token), stop.Token));
                _checked++;
                UpdateProgress();
            }
        }
        catch (OperationCanceledException)
        {
            UpdateProgress();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Check a download couldn't read {Path}", path);
            Problem = $"It couldn't be read: {ex.Message}";
            Progress = "";
        }
        finally
        {
            _cancel = null;
            IsRunning = false;
        }
    }

    void UpdateProgress()
    {
        static string Files(int n) => $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? "file" : "files")}";
        Progress = _total == 0 ? "There are no PDFs or images here."
            : _stopped && _checked < _total ? $"Stopped after {_checked.ToString("N0", CultureInfo.CurrentCulture)} of {Files(_total)}."
            : _checked < _total ? $"Checked {_checked.ToString("N0", CultureInfo.CurrentCulture)} of {Files(_total)}…"
            : $"Checked {Files(_total)}. Nothing was copied, moved or added.";
    }

    void Add(CheckedFile result)
    {
        var group = Groups.FirstOrDefault(g => g.Verdict == result.Verdict);
        if (group is null)
        {
            group = new CheckGroupViewModel(result.Verdict);
            var at = Groups.TakeWhile(g => g.Verdict < result.Verdict).Count();
            Groups.Insert(at, group);
            OnPropertyChanged(nameof(HasResults));
            CopyListCommand.NotifyCanExecuteChanged();
        }
        group.Add(new CheckResultViewModel(result, this));
    }

    /// <summary>The results as text, one file a line, tab-separated so they paste into a spreadsheet.</summary>
    [RelayCommand(CanExecute = nameof(HasResults))]
    void CopyList()
    {
        var text = new StringBuilder("Verdict\tFile\tBook in your library").AppendLine();
        foreach (var group in Groups)
            foreach (var item in group.Items)
                text.Append(group.Label).Append('\t').Append(item.Location).Append('\t').AppendLine(item.Result.MatchTitle ?? "");
        try
        {
            clipboard.SetText(text.ToString());
            Copied = "The list is on the clipboard.";
        }
        catch (COMException ex)
        {
            log.LogWarning(ex, "The clipboard was busy");
            Copied = "The clipboard is busy. Try again.";
        }
    }

    internal void ShowInFolder(string path) => shell.ShowInFolder(path);

    internal void Open(long documentId, string title) => readers.OpenInMainWindow(new ViewerRequest(documentId, title));
}

/// <summary>The files with one verdict.</summary>
public sealed partial class CheckGroupViewModel(CheckVerdict verdict) : ObservableObject
{
    public CheckVerdict Verdict { get; } = verdict;

    public string Label { get; } = DownloadCheckText.Label(verdict);

    public string Explanation { get; } = DownloadCheckText.Explanation(verdict);

    public ObservableCollection<CheckResultViewModel> Items { get; } = [];

    public string Heading => $"{Label} · {Items.Count.ToString("N0", CultureInfo.CurrentCulture)}";

    public void Add(CheckResultViewModel item)
    {
        Items.Add(item);
        OnPropertyChanged(nameof(Heading));
    }
}

/// <summary>A checked file: its name, where it is, and the book it matched.</summary>
public sealed partial class CheckResultViewModel(CheckedFile result, DownloadCheckViewModel page)
{
    public CheckedFile Result { get; } = result;

    public string Name => Result.Item.Name;

    public string Location => Result.Item.Location;

    /// <summary>"Another copy of Drowned Abbey", "In your library at …", and anything to add, such as why its pages weren't read.</summary>
    public string Detail => DownloadCheckText.Detail(Result);

    public bool HasDetail => Detail.Length > 0;

    /// <summary>A match with a file here opens it; a book owned elsewhere has none.</summary>
    public bool CanOpen => Result.MatchDocumentId is not null;

    [RelayCommand]
    void ShowInFolder() => page.ShowInFolder(Result.Item.FullPath); // a file inside a ZIP: the ZIP

    [RelayCommand]
    void OpenBook()
    {
        if (Result.MatchDocumentId is { } document) page.Open(document, Result.MatchTitle ?? Name);
    }
}
