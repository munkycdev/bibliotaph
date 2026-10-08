using System.Collections.ObjectModel;
using System.IO;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

// Each screen shows its empty state from the mockup. Folders are scanned and indexed (slice 1a); the library grid
// and search arrive in 1b, so until then a library with folders shows indexing progress.

/// <summary>Shared by pages whose empty state depends on whether any folders have been added.</summary>
public abstract partial class LibraryAwarePageViewModel(SourceRootStore roots, LibraryActivity activity) : PageViewModel
{
    public LibraryActivity Activity { get; } = activity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolders))]
    public partial int FolderCount { get; set; }

    public bool HasFolders => FolderCount > 0;

    public override async Task LoadAsync() => FolderCount = (await roots.ListAsync()).Count;
}

public sealed partial class HomeViewModel(SourceRootStore roots, LibraryActivity activity, LibraryFolders folders, INavigationService navigation)
    : LibraryAwarePageViewModel(roots, activity)
{
    public override Route Route => Route.Home;
    public override string Title => "Home";

    [RelayCommand]
    void AddFolder() => folders.RequestPick();

    [RelayCommand]
    void ManageFolders() => navigation.NavigateTo(Route.LibraryFolders);
}

public sealed partial class LibraryViewModel(SourceRootStore roots, LibraryActivity activity, LibraryFolders folders, INavigationService navigation)
    : LibraryAwarePageViewModel(roots, activity)
{
    public override Route Route => Route.Library;
    public override string Title => "Library";

    [RelayCommand]
    void AddFolder() => folders.RequestPick();

    [RelayCommand]
    void ManageFolders() => navigation.NavigateTo(Route.LibraryFolders);
}

public sealed partial class CollectionsViewModel(INavigationService navigation) : PageViewModel
{
    public override Route Route => Route.Collections;
    public override string Title => "Collections";

    [RelayCommand]
    void BrowseLibrary() => navigation.NavigateTo(Route.Library);
}

public sealed partial class SessionsViewModel(INavigationService navigation) : PageViewModel
{
    public override Route Route => Route.Sessions;
    public override string Title => "Sessions";

    [RelayCommand]
    void BrowseLibrary() => navigation.NavigateTo(Route.Library);
}

/// <summary>A file Bibliotaph couldn't fully process, and why, in words the user can act on.</summary>
public sealed record AttentionRow(long? DocumentId, string Title, string Detail)
{
    public bool CanRetry => DocumentId is not null;
}

public sealed partial class NeedsReviewViewModel(IndexQueries queries, IndexingService indexing, LibraryActivity activity, INavigationService navigation)
    : PageViewModel
{
    public override Route Route => Route.NeedsReview;
    public override string Title => "Needs review";

    /// <summary>Files needing attention: a password to enter, a damaged download, a folder that can't be read.</summary>
    public ObservableCollection<AttentionRow> Files { get; } = [];

    [ObservableProperty]
    public partial bool HasFiles { get; set; }

    public override async Task LoadAsync()
    {
        activity.Refreshed -= OnRefreshed;
        activity.Refreshed += OnRefreshed;
        await RefreshAsync();
    }

    public override void Unload() => activity.Refreshed -= OnRefreshed;

    async void OnRefreshed(object? sender, EventArgs e) => await RefreshAsync();

    async Task RefreshAsync()
    {
        var rows = (await queries.GetAttentionAsync())
            .Select(a => new AttentionRow(a.DocumentId, a.Title, $"{StageName(a.Stage)}: {a.Reason ?? "something went wrong"}"))
            .Concat(indexing.Unreadable.Select(u => new AttentionRow(null, Path.GetFileName(u.Path), $"Couldn't be read: {u.Reason}")))
            .ToList();
        if (rows.SequenceEqual(Files)) return;
        Files.Clear();
        foreach (var row in rows) Files.Add(row);
        HasFiles = Files.Count > 0;
    }

    static string StageName(Stage stage) => stage switch
    {
        Stage.Probe => "Opening",
        Stage.Text => "Reading text",
        Stage.Covers => "Making a cover",
        Stage.Ocr => "Reading scanned pages",
        _ => stage.ToString(),
    };

    [RelayCommand]
    async Task Retry(AttentionRow row)
    {
        if (row.DocumentId is not { } id) return;
        await indexing.RetryAsync(id);
        activity.Invalidate();
        await RefreshAsync();
    }

    [RelayCommand]
    void ReturnToLibrary() => navigation.NavigateTo(Route.Library);
}
