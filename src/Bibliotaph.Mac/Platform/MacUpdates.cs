using System.ComponentModel;
using System.Reflection;
using AppKit;
using Bibliotaph.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// <see cref="IUpdateService"/> through Velopack, as Windows has it: the repository's published releases, checked
/// daily, downloaded quietly, and applied when the user chooses Restart to Update. Silent when it can't help: in a
/// build Velopack didn't install (a development build), offline, and on any error, which is logged.
/// </summary>
public sealed partial class MacUpdates(TimeProvider time, ILogger<MacUpdates> log) : ObservableObject, IUpdateService, IDisposable
{
    /// <summary>This repository's releases, or the fork's whose release workflow built this copy.</summary>
    static string ReleasesUrl { get; } = typeof(MacUpdates).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "ReleasesUrl")?.Value is { Length: > 0 } url ? url : AboutInfo.SourceUrl;

    static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

    readonly CancellationTokenSource _stop = new();
    UpdateManager? _manager;
    VelopackAsset? _ready;

    [ObservableProperty]
    public partial bool IsReady { get; private set; }

    [ObservableProperty]
    public partial string ReadyText { get; private set; } = "";

    public void Start()
    {
        if (_manager is not null) return;
        try
        {
            var manager = new UpdateManager(new GithubSource(ReleasesUrl, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
            {
                log.LogInformation("Updates: this build wasn't installed, so it doesn't check for them");
                return;
            }
            _manager = manager;
            log.LogInformation("Updates: version {Version} installed; checking {Source} daily", manager.CurrentVersion, ReleasesUrl);
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
            // The app quits the usual way, so the databases close before the updater replaces the app.
            log.LogInformation("Updates: restarting to update to {Version}", ready.Version);
            manager.WaitExitThenApplyUpdates(ready, silent: false, restart: true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Updates: the updater didn't start");
            return;
        }
        NSApplication.SharedApplication.Terminate(null);
    }

    async Task CheckDailyAsync(CancellationToken ct)
    {
        try
        {
            while (_ready is null)
            {
                await CheckOnceAsync(_manager!, ct);
                if (_ready is not null) return;
                await Task.Delay(CheckEvery, time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Quitting.
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
            log.LogWarning("Updates: checking failed, trying again tomorrow: {Message}", ex.Message);
        }
    }

    void ShowReady(VelopackAsset ready)
    {
        _ready = ready;
        Binding.Bindings.OnMain(() =>
        {
            ReadyText = $"Version {ready.Version}";
            IsReady = true;
        });
        log.LogInformation("Updates: {Version} is ready for a restart", ready.Version);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
