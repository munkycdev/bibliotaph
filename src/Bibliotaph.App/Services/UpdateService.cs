using System.ComponentModel;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Bibliotaph.App.Services;

/// <summary>
/// Updates from GitHub Releases (slice 4l plan, choice 4): checked at start and once a day, downloaded in the
/// background, then offered in the sidebar as "Update ready: restart to update". Bibliotaph never restarts by itself,
/// since the user may be mid-session at the table. An interface so the smoke test can show an update as ready without
/// one, and never restart.
/// </summary>
public interface IUpdateService : INotifyPropertyChanged
{
    /// <summary>True once an update has downloaded and waits for a restart.</summary>
    bool IsReady { get; }

    /// <summary>"Version 0.5.1", for the sidebar; empty until an update is ready.</summary>
    string ReadyText { get; }

    /// <summary>Starts checking, once the window shows. Does nothing in a build that wasn't installed.</summary>
    void Start();

    /// <summary>Closes Bibliotaph, applies the update and opens it again. Only when the user asks.</summary>
    void RestartToUpdate();
}

/// <summary>
/// <see cref="IUpdateService"/> through Velopack. Silent when it can't help: in a build that wasn't installed (a
/// development build, the smoke test), offline, and on any error, which is logged and tried again the next day.
/// </summary>
public sealed partial class UpdateService(TimeProvider time, ILogger<UpdateService> log) : ObservableObject, IUpdateService, IDisposable
{
    /// <summary>
    /// The GitHub repository whose published releases are offered, never its drafts or pre-releases: this one
    /// (<see cref="AboutInfo.SourceUrl"/>), or the fork whose release workflow built this copy (Bibliotaph.App.csproj).
    /// </summary>
    public static string ReleasesUrl { get; } = typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "ReleasesUrl")?.Value is { Length: > 0 } url ? url : AboutInfo.SourceUrl;

    static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

    readonly CancellationTokenSource _stop = new();
    UpdateManager? _manager;
    VelopackAsset? _ready;
    SynchronizationContext? _ui;

    [ObservableProperty]
    public partial bool IsReady { get; private set; }

    [ObservableProperty]
    public partial string ReadyText { get; private set; } = "";

    /// <summary>False in a build Velopack didn't install, which never checks.</summary>
    public bool IsInstalled { get; private set; }

    public void Start()
    {
        if (_manager is not null) return;
        _ui = SynchronizationContext.Current;
        try
        {
            var manager = new UpdateManager(new GithubSource(ReleasesUrl, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
            {
                log.LogInformation("Updates: this build wasn't installed, so it doesn't check for them");
                return;
            }
            _manager = manager;
            IsInstalled = true;
            log.LogInformation("Updates: version {Version} installed; checking {Source} daily", manager.CurrentVersion, ReleasesUrl);
            // Downloaded in an earlier sitting and not applied yet.
            if (manager.UpdatePendingRestart is { } pending) ShowReady(pending);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Updates: Velopack couldn't start, so updates aren't checked");
            return;
        }
        _ = Task.Run(() => CheckDailyAsync(_stop.Token));
    }

    public void RestartToUpdate()
    {
        if (_manager is not { } manager || _ready is not { } ready) return;
        try
        {
            // ApplyUpdatesAndRestart, but closing the app the usual way rather than ending the process at once, so the
            // host stops and the databases close before the updater replaces the files.
            log.LogInformation("Updates: restarting to update to {Version}", ready.Version);
            manager.WaitExitThenApplyUpdates(ready, silent: false, restart: true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Updates: the updater didn't start");
            return;
        }
        Application.Current.Shutdown();
    }

    async Task CheckDailyAsync(CancellationToken ct)
    {
        try
        {
            // _ready, not IsReady, which the UI thread sets a moment later.
            while (_ready is null)
            {
                await CheckOnceAsync(_manager!, ct);
                if (_ready is not null) return;
                await Task.Delay(CheckEvery, time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Closing.
        }
    }

    async Task CheckOnceAsync(UpdateManager manager, CancellationToken ct)
    {
        try
        {
            if (await manager.CheckForUpdatesAsync() is not { } update)
            {
                log.LogInformation("Updates: up to date");
                return;
            }
            log.LogInformation("Updates: downloading {Version}", update.TargetFullRelease.Version);
            await manager.DownloadUpdatesAsync(update, null, ct);
            ShowReady(update.TargetFullRelease);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Offline, GitHub's rate limit, a half-finished release: nothing the user needs to hear about.
            log.LogWarning("Updates: checking failed, trying again tomorrow: {Message}", ex.Message);
        }
    }

    void ShowReady(VelopackAsset ready)
    {
        _ready = ready;
        void Show()
        {
            ReadyText = $"Version {ready.Version}";
            IsReady = true;
        }
        if (_ui is null) Show();
        else _ui.Post(_ => Show(), null);
        log.LogInformation("Updates: {Version} is ready for a restart", ready.Version);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}

/// <summary>The smoke test's updates: none are checked, one is made ready on demand, and a restart is only counted.</summary>
public sealed partial class SmokeUpdateService : ObservableObject, IUpdateService
{
    [ObservableProperty]
    public partial bool IsReady { get; private set; }

    [ObservableProperty]
    public partial string ReadyText { get; private set; } = "";

    /// <summary>How many times Restart to update was asked for. The smoke test never clicks it.</summary>
    public int RestartRequests { get; private set; }

    public void Start()
    {
    }

    public void ShowReady(string version)
    {
        ReadyText = $"Version {version}";
        IsReady = true;
    }

    public void Clear()
    {
        IsReady = false;
        ReadyText = "";
    }

    public void RestartToUpdate() => RestartRequests++;
}
