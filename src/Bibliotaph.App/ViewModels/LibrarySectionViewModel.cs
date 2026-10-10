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

    /// <summary>Where the folder is; it changes when the folder is pointed to its new place.</summary>
    [ObservableProperty]
    public partial string Path { get; set; } = path;

    /// <summary>The folder can't be read right now, so it offers Point to its new place (slice 4g plan, choice 6).</summary>
    [ObservableProperty]
    public partial bool IsOffline { get; set; }

    /// <summary>Why the folder picked as its new place wasn't taken; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Problem { get; set; } = "";

    public bool HasProblem => Problem.Length > 0;

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
    LibraryFolders folders, MetadataHints hints, SettingsStore settings, ILogger<LibrarySectionViewModel> log) : SettingsSectionViewModel
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
        StartOnLibrary = await settings.GetAsync(SettingKeys.StartPage) == nameof(Route.Library);
        if (folders.TakePickRequest()) await AddFolder();
    }

    /// <summary>Start on: the Library rather than Home when Bibliotaph opens (slice 3 plan, choice 7).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartOnHome))]
    public partial bool StartOnLibrary { get; set; }

    public bool StartOnHome
    {
        get => !StartOnLibrary;
        set { if (value) StartOnLibrary = false; }
    }

    async partial void OnStartOnLibraryChanged(bool value)
    {
        try
        {
            await settings.SetAsync(SettingKeys.StartPage, value ? nameof(Route.Library) : nameof(Route.Home));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Saving the start page failed");
        }
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
                item.Path = root.Path;
                // Availability is kept in the catalog, so a folder offline before a restart says so before it is scanned again.
                item.IsOffline = root.Availability == SourceRootAvailability.Offline;
                item.Detail = Describe(root.AddedUtc, item.IsOffline, scans.GetValueOrDefault(root.Id), searchable, ids.Count);
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

    static string Describe(DateTime addedUtc, bool offline, RootScan? scan, long searchable, int documents)
    {
        var added = addedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        if (offline || scan is { Reachable: false })
        {
            var why = scan?.Offline switch
            {
                OfflineReason.DifferentDisk => "Its drive holds a different disk right now.",
                OfflineReason.LooksEmpty => "It looks empty right now, so nothing in it is marked missing.",
                _ => "Can't be reached right now.",
            };
            return $"Added {added} · {why} Its books stay in your library.";
        }
        if (scan is null) return $"Added {added} · Waiting to be looked at";
        var state = documents == 0 ? "" : searchable >= documents ? "Up to date · " : $"{searchable:N0} of {documents:N0} searchable · ";
        return $"Added {added} · {state}{DescribeContents(scan.Summary!)}";
    }

    /// <summary>"488 PDFs, 120 images, 3 ZIPs · 30 online-only (12.4 GB to download) · 15 other files ignored".</summary>
    static string DescribeContents(ScanSummary summary)
    {
        var pdfs = summary.ByFormat.GetValueOrDefault(SourceFormats.Pdf);
        var images = summary.Indexable - pdfs;
        var parts = new List<string>
        {
            summary.Indexable + summary.Archives == 0 ? "No PDFs or images"
                : string.Join(", ", new[] { Count(pdfs, "PDF", "PDFs"), Count(images, "image", "images"), Count(summary.Archives, "ZIP", "ZIPs") }.Where(p => p.Length > 0)),
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

    /// <summary>
    /// Point to its new place (slice 4g plan, choice 6), for a folder that moved for good: picked with the folder picker,
    /// then every book in it, and everything done with them, follows without its files being read again.
    /// </summary>
    [RelayCommand]
    async Task PointToNewPlace(LibraryFolderItem folder)
    {
        if (LibraryFolders.PickOne($"Where is {System.IO.Path.GetFileName(folder.Path)} now?") is not { } path) return;
        try
        {
            folder.Problem = await indexing.RelocateRootAsync(folder.Id, path) switch
            {
                RootRelocation.NotFound => "That folder can't be opened.",
                RootRelocation.Overlaps => "That folder is another library folder, is inside one, or holds one.",
                RootRelocation.WasRemoved => "That folder was a library folder of its own before. Add a folder brings it back.",
                _ => "",
            };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Pointing library folder {RootId} to its new place failed", folder.Id);
            folder.Problem = "That didn't work. Try again.";
        }
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
