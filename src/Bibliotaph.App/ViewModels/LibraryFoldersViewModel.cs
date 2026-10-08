using System.Collections.ObjectModel;
using System.Globalization;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

public sealed record LibraryFolderItem(long Id, string Path, string Detail);

/// <summary>A folder the user picked, shown with what it holds before it joins the library.</summary>
public sealed partial class PendingFolder(string path) : ObservableObject
{
    public string Path { get; } = path;

    [ObservableProperty]
    public partial string Detail { get; set; } = "Looking at what's inside…";

    [ObservableProperty]
    public partial bool CanAdd { get; set; }
}

/// <summary>
/// The folders Bibliotaph reads from, what each holds, and indexing progress with pause and resume.
/// Removing a folder keeps its catalog rows and the user's work.
/// </summary>
public sealed partial class LibraryFoldersViewModel(
    SourceRootStore roots, IndexingService indexing, LibraryActivity activity, LibraryFolders folders) : PageViewModel
{
    public override Route Route => Route.LibraryFolders;
    public override string Section => "Settings";
    public override string Title => "Library folders";

    public LibraryActivity Activity { get; } = activity;

    public ObservableCollection<LibraryFolderItem> Folders { get; } = [];

    public ObservableCollection<PendingFolder> Pending { get; } = [];

    [ObservableProperty]
    public partial bool HasFolders { get; set; }

    [ObservableProperty]
    public partial string ProgressDetail { get; set; } = "";

    public override async Task LoadAsync()
    {
        Activity.Refreshed -= OnActivityRefreshed;
        Activity.Refreshed += OnActivityRefreshed;
        await RefreshFoldersAsync();
        UpdateProgress();
        if (folders.TakePickRequest()) await AddFolder();
    }

    public override void Unload() => Activity.Refreshed -= OnActivityRefreshed;

    async void OnActivityRefreshed(object? sender, EventArgs e)
    {
        UpdateProgress();
        await RefreshFoldersAsync();
    }

    async Task RefreshFoldersAsync()
    {
        var scans = indexing.Scans.ToDictionary(s => s.RootId);
        var current = await roots.ListAsync();
        Folders.Clear();
        foreach (var root in current)
            Folders.Add(new LibraryFolderItem(root.Id, root.Path, Describe(root.AddedUtc, scans.GetValueOrDefault(root.Id))));
        HasFolders = Folders.Count > 0;
    }

    static string Describe(DateTime addedUtc, RootScan? scan)
    {
        var added = addedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        if (scan is null) return $"Added {added} · Waiting to be looked at";
        if (!scan.Reachable) return $"Added {added} · Can't be reached right now. Its books stay in your library.";
        return $"Added {added} · {DescribeContents(scan.Summary!)}";
    }

    /// <summary>"488 PDFs, 120 images · 30 online-only (12.4 GB to download) · 15 other files ignored".</summary>
    static string DescribeContents(ScanSummary summary)
    {
        var pdfs = summary.ByFormat.GetValueOrDefault(SourceFormats.Pdf);
        var images = summary.Indexable - pdfs;
        var parts = new List<string>
        {
            summary.Indexable == 0 ? "No PDFs or images"
                : string.Join(", ", new[] { Count(pdfs, "PDF", "PDFs"), Count(images, "image", "images") }.Where(p => p.Length > 0)),
        };
        if (summary.OnlineOnly > 0) parts.Add($"{summary.OnlineOnly:N0} online-only ({Size(summary.OnlineOnlyBytes)} to download)");
        if (summary.Unsupported > 0) parts.Add($"{Count(summary.Unsupported, "other file", "other files")} ignored");
        if (summary.Inaccessible.Count > 0) parts.Add($"{Count(summary.Inaccessible.Count, "folder", "folders")} couldn't be opened");
        return string.Join(" · ", parts);
    }

    static string Count(long count, string one, string many) =>
        count == 0 ? "" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} {LibraryActivity.Plural(count, one, many)}";

    static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{Math.Max(1, bytes / 1024):N0} KB",
    };

    void UpdateProgress()
    {
        var p = Activity.Progress;
        var parts = new List<string>();
        if (p.Documents > 0) parts.Add($"{p.Searchable:N0} of {p.Documents:N0} searchable");
        if (p.PagesAwaitingOcr > 0) parts.Add($"{Count(p.PagesAwaitingOcr, "scanned page", "scanned pages")} waiting to be read");
        if (p.NeedAttention > 0) parts.Add($"{Count(p.NeedAttention, "file needs", "files need")} attention");
        if (Activity.Counts.OnlineOnly > 0) parts.Add($"{Activity.Counts.OnlineOnly:N0} online-only");
        ProgressDetail = string.Join(" · ", parts);
    }

    [RelayCommand]
    async Task AddFolder()
    {
        foreach (var path in LibraryFolders.Pick())
        {
            if (Pending.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var pending = new PendingFolder(path);
            Pending.Add(pending);
            var preview = await indexing.PreviewAsync(path);
            if (preview is null)
            {
                pending.Detail = "This folder can't be opened.";
                continue;
            }
            pending.Detail = DescribeContents(preview.Summary)
                + (preview.Summary.OnlineOnly > 0 ? ". Online-only files download as they are indexed; local files go first." : "");
            pending.CanAdd = true;
        }
    }

    [RelayCommand]
    async Task ConfirmAdd(PendingFolder pending)
    {
        Pending.Remove(pending);
        var root = await roots.AddAsync(pending.Path);
        indexing.RequestScan(root.Id);
        await RefreshFoldersAsync();
    }

    [RelayCommand]
    void CancelAdd(PendingFolder pending) => Pending.Remove(pending);

    [RelayCommand]
    async Task Remove(LibraryFolderItem folder)
    {
        await roots.RemoveAsync(folder.Id);
        await RefreshFoldersAsync();
    }

    [RelayCommand]
    void ToggleIndexing() => Activity.SetPaused(Lane.Index, !Activity.IndexPaused);

    [RelayCommand]
    void ToggleOcr() => Activity.SetPaused(Lane.Ocr, !Activity.OcrPaused);

    [RelayCommand]
    void Rescan() => indexing.RequestScan();
}
