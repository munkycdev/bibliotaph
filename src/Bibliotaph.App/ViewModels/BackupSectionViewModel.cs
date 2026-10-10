using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Bibliotaph.App.Services;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A library folder of the backup being restored: found where it was, pointed to its new place, or left offline to be
/// fixed later (slice 4j plan, choice 4).
/// </summary>
public sealed partial class RestoreFolderItem(RestoreFolder folder) : ObservableObject
{
    public long Id => folder.Id;

    /// <summary>Where the folder was when the backup was made.</summary>
    public string Path => folder.Path;

    public bool Found => folder.Found;

    /// <summary>"120 files".</summary>
    public string Files { get; } = $"{folder.Files.ToString("N0", CultureInfo.CurrentCulture)} {LibraryActivity.Plural(folder.Files, "file", "files")}";

    /// <summary>Where the user pointed a folder that wasn't found; null leaves it offline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(IsPointed))]
    public partial string? NewPlace { get; set; }

    public bool IsPointed => NewPlace is not null;

    public string Status => Found ? "Found"
        : NewPlace is { } place ? $"Will be read from {place}"
        : "Not found: point to its new place, or leave it offline and point it there later in Settings > Library.";

    /// <summary>Why the folder picked can't be its new place; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Problem { get; set; } = "";

    public bool HasProblem => Problem.Length > 0;
}

/// <summary>
/// Settings > Backup (slice 4j): Back up now, the weekly backups and their folder, restoring a backup with its library
/// folders found or pointed to their new places, and exporting the library as CSV or JSON. The work is
/// <see cref="BackupService"/>'s and <see cref="ExportService"/>'s; this asks where, and says what happened.
/// </summary>
public sealed partial class BackupSectionViewModel(BackupService backups, ExportService export, AppRestart restart, TimeProvider clock,
    ILogger<BackupSectionViewModel> log) : SettingsSectionViewModel
{
    bool _loading;

    public override SettingsSection Section => SettingsSection.Backup;
    public override string Label => "Backup";

    /// <summary>"Last automatic backup: Monday 5 October 2026 at 09:12", or that there is none yet.</summary>
    [ObservableProperty]
    public partial string LastBackup { get; private set; } = "";

    /// <summary>Backups take index.db too, for Back up now and the weekly backup alike.</summary>
    [ObservableProperty]
    public partial bool IncludeIndex { get; set; }

    /// <summary>What Back up now did, or is doing.</summary>
    [ObservableProperty]
    public partial string? BackupNote { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand), nameof(ChooseBackupCommand), nameof(RestoreCommand), nameof(ExportCsvCommand), nameof(ExportJsonCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The backup chosen for restoring, which can be restored; null when none is, or it was refused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial RestorePreview? Preview { get; private set; }

    public bool HasPreview => Preview is not null;

    public ObservableCollection<RestoreFolderItem> RestoreFolders { get; } = [];

    /// <summary>When the chosen backup was made, by which Bibliotaph, and what it holds.</summary>
    [ObservableProperty]
    public partial string? RestoreSummary { get; private set; }

    /// <summary>Why the chosen backup can't be restored, or why restoring it didn't work.</summary>
    [ObservableProperty]
    public partial string? RestoreProblem { get; private set; }

    /// <summary>What the last export wrote.</summary>
    [ObservableProperty]
    public partial string? ExportNote { get; private set; }

    public override async Task LoadAsync()
    {
        _loading = true;
        try
        {
            IncludeIndex = await backups.GetIncludeIndexAsync();
        }
        finally
        {
            _loading = false;
        }
        LastBackup = backups.ListAutomatic() is not [var last, ..]
            ? "No automatic backup yet. Bibliotaph makes one each week, the first time it starts that week, once your library has books in it."
            : $"Last automatic backup: {last.CreatedUtc.ToLocalTime().ToString("dddd d MMMM yyyy 'at' HH:mm", CultureInfo.CurrentCulture)}. The last {BackupService.KeptAutomatic} are kept.";
    }

    async partial void OnIncludeIndexChanged(bool value)
    {
        if (_loading) return;
        try
        {
            await backups.SetIncludeIndexAsync(value);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Saving whether backups include the search index failed");
        }
    }

    bool IsIdle() => !IsBusy;

    /// <summary>Back up now: a ZIP saved where the user picks.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    async Task BackUpNow()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back up your library",
            FileName = $"Bibliotaph backup {clock.GetLocalNow():yyyy-MM-dd}.zip",
            DefaultExt = ".zip",
            Filter = "Bibliotaph backups (*.zip)|*.zip",
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) await BackUpToAsync(dialog.FileName);
    }

    /// <summary>Backs up to <paramref name="path"/>: what Back up now does once a place is picked.</summary>
    public async Task BackUpToAsync(string path)
    {
        IsBusy = true;
        BackupNote = "Backing up…";
        try
        {
            var file = await backups.BackUpAsync(path);
            BackupNote = $"Saved {System.IO.Path.GetFileName(file.Path)} ({LibraryActivity.Size(file.SizeBytes)}).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            log.LogError(ex, "Backing up to {Path} failed", path);
            BackupNote = $"The backup couldn't be saved: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    void OpenBackupsFolder()
    {
        Directory.CreateDirectory(backups.BackupsFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{backups.BackupsFolder}\"") { UseShellExecute = false })?.Dispose();
    }

    /// <summary>Restore: the backup to restore from, then its library folders.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    async Task ChooseBackup()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Restore a backup",
            Filter = "Bibliotaph backups (*.zip)|*.zip",
            InitialDirectory = backups.BackupsFolder,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true) await PreviewRestoreAsync(dialog.FileName);
    }

    /// <summary>Reads <paramref name="path"/> for restoring and shows what it holds: what choosing a backup does.</summary>
    public async Task PreviewRestoreAsync(string path)
    {
        IsBusy = true;
        ClearRestore();
        RestoreSummary = "Reading the backup…";
        try
        {
            var preview = await backups.PreviewRestoreAsync(path);
            if (!preview.CanRestore)
            {
                RestoreSummary = null;
                RestoreProblem = preview.Problem;
                return;
            }
            foreach (var folder in preview.Folders) RestoreFolders.Add(new RestoreFolderItem(folder));
            RestoreSummary = Describe(preview);
            Preview = preview;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            log.LogError(ex, "Reading backup {Path} failed", path);
            RestoreSummary = null;
            RestoreProblem = $"The backup can't be read: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    static string Describe(RestorePreview preview)
    {
        var manifest = preview.Manifest!;
        var counts = preview.Counts!;
        var made = manifest.CreatedUtc.ToLocalTime().ToString("d MMMM yyyy 'at' HH:mm", CultureInfo.CurrentCulture);
        var version = AboutInfo.ParseVersion(manifest.App).Version;
        static string Count(int count, string one, string many) => $"{count.ToString("N0", CultureInfo.CurrentCulture)} {LibraryActivity.Plural(count, one, many)}";
        var holds = string.Join(", ", Count(counts.Books, "book", "books"), Count(counts.Collections, "collection", "collections"),
            Count(counts.SessionPacks, "session", "sessions"), Count(counts.Notes, "note", "notes"));
        var index = preview.IncludesIndex ? "It includes the search index."
            : "The search index isn't in it, so your books are read again for search after restoring, in the background.";
        return $"Made {made}{(manifest.Automatic ? " (an automatic backup)" : "")} by Bibliotaph {version}: {holds}. {index}";
    }

    Dictionary<long, string?> Places() => RestoreFolders.ToDictionary(f => f.Id, f => f.NewPlace);

    /// <summary>Point to its new place, for a folder of the backup that isn't where it was.</summary>
    [RelayCommand]
    void PointRestoredFolder(RestoreFolderItem folder)
    {
        if (Preview is not { } preview) return;
        if (LibraryFolders.PickOne($"Where is {System.IO.Path.GetFileName(folder.Path)} now?") is not { } path) return;
        folder.Problem = BackupService.CheckPlace(preview, Places(), folder.Id, path) switch
        {
            RootRelocation.NotFound => "That folder can't be opened.",
            RootRelocation.Overlaps => "That folder is another library folder of this backup, is inside one, or holds one.",
            _ => "",
        };
        if (!folder.HasProblem) folder.NewPlace = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    }

    /// <summary>Leaves a folder that wasn't found offline, to be pointed to its new place later.</summary>
    [RelayCommand]
    static void LeaveOffline(RestoreFolderItem folder)
    {
        folder.NewPlace = null;
        folder.Problem = "";
    }

    bool CanRestore() => Preview is { CanRestore: true } && !IsBusy;

    /// <summary>Restore and restart: the current catalog is backed up, the restored one swapped in, and Bibliotaph restarts.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    async Task Restore()
    {
        if (Preview is not { } preview) return;
        var offline = RestoreFolders.Count(f => !f.Found && !f.IsPointed);
        var message = "Bibliotaph will keep a copy of your current catalog in the backups folder, then restart with this backup's. "
            + "Anything you did since the backup was made is replaced." + (offline == 0 ? ""
                : $"\n\n{(offline == 1 ? "One library folder stays" : $"{offline} library folders stay")} offline until you point {(offline == 1 ? "it" : "them")} to {(offline == 1 ? "its" : "their")} new place.");
        if (MessageBox.Show(Application.Current.MainWindow, message, "Restore this backup?", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            if (await backups.StageRestoreAsync(preview, Places()) is { } problem)
            {
                RestoreProblem = problem;
                return;
            }
            restart.Run();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            log.LogError(ex, "Restoring {Path} failed", preview.BackupPath);
            RestoreProblem = $"The backup couldn't be restored: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    void CancelRestore()
    {
        ClearRestore();
        try
        {
            backups.DiscardStaging();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tidied at the next start instead.
            log.LogWarning(ex, "The backup read for restoring couldn't be deleted yet");
        }
    }

    void ClearRestore()
    {
        Preview = null;
        RestoreFolders.Clear();
        RestoreSummary = null;
        RestoreProblem = null;
    }

    /// <summary>Export: a CSV with one row per book, for a spreadsheet.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    Task ExportCsv() => ExportAsync("Export your library for a spreadsheet", $"Bibliotaph library {clock.GetLocalNow():yyyy-MM-dd}.csv", "CSV (*.csv)|*.csv",
        async path => $"Exported {BulkEditViewModel.Books(await export.WriteCsvAsync(path))} to {System.IO.Path.GetFileName(path)}.");

    /// <summary>Export: everything in the catalog as JSON.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    Task ExportJson() => ExportAsync("Export everything as JSON", $"Bibliotaph library {clock.GetLocalNow():yyyy-MM-dd}.json", "JSON (*.json)|*.json",
        async path =>
        {
            await export.WriteJsonAsync(path);
            return $"Exported everything to {System.IO.Path.GetFileName(path)}.";
        });

    async Task ExportAsync(string title, string name, string filter, Func<string, Task<string>> write)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = name, Filter = filter, DefaultExt = System.IO.Path.GetExtension(name) };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        IsBusy = true;
        ExportNote = "Exporting…";
        try
        {
            ExportNote = await write(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            log.LogError(ex, "Exporting to {Path} failed", dialog.FileName);
            ExportNote = $"The export couldn't be saved: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
