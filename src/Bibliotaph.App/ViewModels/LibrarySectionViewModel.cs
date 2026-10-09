using System.Collections.ObjectModel;
using System.Globalization;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>A library folder, with what it holds and how much of it is searchable. Updated in place as indexing runs.</summary>
public sealed partial class LibraryFolderItem(long id, string path) : ObservableObject
{
    public long Id { get; } = id;

    public string Path { get; } = path;

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    /// <summary>Percent of the folder's documents that are searchable.</summary>
    [ObservableProperty]
    public partial double Percent { get; set; }

    /// <summary>The bar shows while some of the folder isn't searchable yet.</summary>
    [ObservableProperty]
    public partial bool ShowProgress { get; set; }
}

/// <summary>A folder the user picked, shown with what it holds before it joins the library.</summary>
public sealed partial class PendingFolder(string path) : ObservableObject
{
    public string Path { get; } = path;

    [ObservableProperty]
    public partial string Detail { get; set; } = "Looking at what's inside…";

    [ObservableProperty]
    public partial bool CanAdd { get; set; }
}

/// <summary>A folder name read as a label, such as “Adventures” for the type Adventure, which the user can switch off.</summary>
public sealed partial class FolderLabelItem(FolderLabelUse use, Func<FolderLabelItem, bool, Task> setEnabled) : ObservableObject
{
    public FolderLabelUse Use { get; } = use;

    public string Folder => Use.Folder;

    /// <summary>"Type: Adventure".</summary>
    public string Meaning { get; } = $"{MetadataFields.All.FirstOrDefault(f => f.Vocabulary == use.Term.Vocabulary)?.Label ?? use.Term.Vocabulary}: {use.Term.Label}";

    public string Detail { get; } = $"{use.Documents.ToString("N0", CultureInfo.CurrentCulture)} {LibraryActivity.Plural(use.Documents, "document", "documents")} under a folder with this name";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = use.Enabled;

    partial void OnIsEnabledChanged(bool value) => _ = setEnabled(this, value);
}

/// <summary>
/// Settings > Library: the folders Bibliotaph reads from and what each holds, adding them after a preview, looking for
/// changes, and the folder names read as labels. Removing a folder keeps its catalog rows and the user's work.
/// </summary>
public sealed partial class LibrarySectionViewModel(
    SourceRootStore roots, LibraryStore library, IndexQueries queries, IndexingService indexing, LibraryActivity activity,
    LibraryFolders folders, MetadataHints hints, ILogger<LibrarySectionViewModel> log) : SettingsSectionViewModel
{
    public override SettingsSection Section => SettingsSection.Library;
    public override string Label => "Library";

    public ObservableCollection<LibraryFolderItem> Folders { get; } = [];

    public ObservableCollection<PendingFolder> Pending { get; } = [];

    [ObservableProperty]
    public partial bool HasFolders { get; set; }

    /// <summary>Folder names in the library that Bibliotaph reads as a game system, type or other label.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolderLabels))]
    public partial IReadOnlyList<FolderLabelItem> FolderLabels { get; set; } = [];

    public bool HasFolderLabels => FolderLabels.Count > 0;

    static readonly TimeSpan FolderRefresh = TimeSpan.FromSeconds(3);
    DateTime _foldersRefreshed;
    bool _refreshingFolders;

    public override async Task LoadAsync()
    {
        activity.Refreshed -= OnActivityRefreshed;
        activity.Refreshed += OnActivityRefreshed;
        await RefreshFoldersAsync();
        await LoadFolderLabelsAsync();
        if (folders.TakePickRequest()) await AddFolder();
    }

    async Task LoadFolderLabelsAsync()
    {
        try
        {
            var uses = await Task.Run(() => hints.GetFolderLabelsAsync());
            FolderLabels = [.. uses.Select(u => new FolderLabelItem(u, SetFolderLabelEnabledAsync))];
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Listing folder labels failed");
        }
    }

    /// <summary>Switching a label re-reads the hints of every book under a folder of that name.</summary>
    async Task SetFolderLabelEnabledAsync(FolderLabelItem label, bool enabled)
    {
        try
        {
            await Task.Run(() => hints.SetFolderLabelEnabledAsync(label.Use.Folder, label.Use.Term, enabled));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Switching a folder label failed");
        }
    }

    public override void Unload() => activity.Refreshed -= OnActivityRefreshed;

    async void OnActivityRefreshed(object? sender, EventArgs e)
    {
        // Activity refreshes often while indexing runs; per-folder counts needn't keep up with it.
        if (DateTime.UtcNow - _foldersRefreshed >= FolderRefresh) await RefreshFoldersAsync();
    }

    /// <summary>Brings the folder cards up to date in place, so a refresh doesn't take focus or scroll away.</summary>
    async Task RefreshFoldersAsync()
    {
        if (_refreshingFolders) return;
        _refreshingFolders = true;
        try
        {
            _foldersRefreshed = DateTime.UtcNow;
            var scans = indexing.Scans.ToDictionary(s => s.RootId);
            var current = await roots.ListAsync();
            foreach (var gone in Folders.Where(f => current.All(r => r.Id != f.Id)).ToList()) Folders.Remove(gone);
            for (var i = 0; i < current.Count; i++)
            {
                var root = current[i];
                var item = Folders.FirstOrDefault(f => f.Id == root.Id);
                if (item is null) Folders.Insert(Math.Min(i, Folders.Count), item = new LibraryFolderItem(root.Id, root.Path));
                var ids = await library.GetVisibleEntryIdsAsync(root.Id);
                var searchable = await queries.CountSearchableAsync(ids);
                item.Detail = Describe(root.AddedUtc, scans.GetValueOrDefault(root.Id), searchable, ids.Count);
                item.Percent = ids.Count == 0 ? 0 : 100.0 * searchable / ids.Count;
                item.ShowProgress = searchable < ids.Count;
            }
            HasFolders = Folders.Count > 0;
        }
        finally
        {
            _refreshingFolders = false;
        }
    }

    static string Describe(DateTime addedUtc, RootScan? scan, long searchable, int documents)
    {
        var added = addedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        if (scan is null) return $"Added {added} · Waiting to be looked at";
        if (!scan.Reachable) return $"Added {added} · Can't be reached right now. Its books stay in your library.";
        var state = documents == 0 ? "" : searchable >= documents ? "Up to date · " : $"{searchable:N0} of {documents:N0} searchable · ";
        return $"Added {added} · {state}{DescribeContents(scan.Summary!)}";
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
        if (summary.OnlineOnly > 0) parts.Add($"{summary.OnlineOnly:N0} online-only ({LibraryActivity.Size(summary.OnlineOnlyBytes)} to download)");
        if (summary.Unsupported > 0) parts.Add($"{Count(summary.Unsupported, "other file", "other files")} ignored");
        if (summary.Inaccessible.Count > 0) parts.Add($"{Count(summary.Inaccessible.Count, "folder", "folders")} couldn't be opened");
        return string.Join(" · ", parts);
    }

    static string Count(long count, string one, string many) =>
        count == 0 ? "" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} {LibraryActivity.Plural(count, one, many)}";

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
    Task RefreshFolderLabels() => LoadFolderLabelsAsync();

    [RelayCommand]
    void CancelAdd(PendingFolder pending) => Pending.Remove(pending);

    [RelayCommand]
    async Task Remove(LibraryFolderItem folder)
    {
        await roots.RemoveAsync(folder.Id);
        await RefreshFoldersAsync();
        await LoadFolderLabelsAsync();
    }

    [RelayCommand]
    void Rescan() => indexing.RequestScan();
}
